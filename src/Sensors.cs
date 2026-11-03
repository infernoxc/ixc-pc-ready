using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace InfernoXC
{
    /// <summary>One measurement, or the honest reason there is none.</summary>
    public class Reading
    {
        public double Value = -1;
        public string Source = "";
        public string Reason = "";
        public bool Ok { get { return Value >= 0; } }
        public static Reading Of(double v, string src) { Reading r = new Reading(); r.Value = v; r.Source = src; return r; }
        public static Reading None(string why) { Reading r = new Reading(); r.Reason = why; return r; }
    }

    /// <summary>
    /// Windows PDH with English counter names. Works on every Windows language,
    /// unlike PerformanceCounter which can fail on localised systems.
    /// </summary>
    public static class Pdh
    {
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string src, IntPtr user, out IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr q, string path, IntPtr user, out IntPtr c);
        [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr q);
        [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr c, uint fmt, IntPtr type, out PDH_VALUE v);
        [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterArrayW(IntPtr c, uint fmt, ref uint bufSize, ref uint count, IntPtr buf);
        [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr q);

        [StructLayout(LayoutKind.Sequential)]
        struct PDH_VALUE { public uint Status; public uint Pad; public double Value; }

        const uint PDH_FMT_DOUBLE = 0x200;
        const uint PDH_MORE_DATA = 0x800007D2;

        public class Query : IDisposable
        {
            IntPtr _q;
            List<IntPtr> _c = new List<IntPtr>();
            public bool Ok;
            public string Error = "";

            public Query() { uint r = PdhOpenQuery(null, IntPtr.Zero, out _q); Ok = r == 0; if (!Ok) Error = "PdhOpenQuery 0x" + r.ToString("X"); }
            public int Add(string path)
            {
                if (!Ok) return -1;
                IntPtr c;
                uint r = PdhAddEnglishCounterW(_q, path, IntPtr.Zero, out c);
                if (r != 0) { Error = path + " -> 0x" + r.ToString("X"); return -1; }
                _c.Add(c);
                return _c.Count - 1;
            }
            public bool Collect() { return Ok && PdhCollectQueryData(_q) == 0; }
            public double Value(int idx)
            {
                if (idx < 0 || idx >= _c.Count) return -1;
                PDH_VALUE v;
                if (PdhGetFormattedCounterValue(_c[idx], PDH_FMT_DOUBLE, IntPtr.Zero, out v) != 0 || v.Status != 0) return -1;
                return v.Value;
            }
            /// <summary>All instances of a wildcard counter: instance name -> value.</summary>
            public Dictionary<string, double> Array(int idx)
            {
                Dictionary<string, double> d = new Dictionary<string, double>();
                if (idx < 0 || idx >= _c.Count) return d;
                uint size = 0, count = 0;
                uint r = PdhGetFormattedCounterArrayW(_c[idx], PDH_FMT_DOUBLE, ref size, ref count, IntPtr.Zero);
                if (r != PDH_MORE_DATA || size == 0) return d;
                IntPtr buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    r = PdhGetFormattedCounterArrayW(_c[idx], PDH_FMT_DOUBLE, ref size, ref count, buf);
                    if (r != 0) return d;
                    int item = IntPtr.Size == 8 ? 24 : 20;   // szName + CStatus + (pad) + double
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr p = new IntPtr(buf.ToInt64() + (long)i * item);
                        IntPtr namePtr = Marshal.ReadIntPtr(p);
                        string name = Marshal.PtrToStringUni(namePtr);
                        int off = IntPtr.Size;
                        uint status = (uint)Marshal.ReadInt32(p, off);
                        if (status != 0 || name == null) continue;
                        double val = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, off + 8));
                        d[name] = val;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
                return d;
            }
            public void Dispose() { if (_q != IntPtr.Zero) { PdhCloseQuery(_q); _q = IntPtr.Zero; } }
        }
    }

    public class NvGpu
    {
        public string Name;
        public double Temp = -1, Util = -1;
        public double VramUsedMB = -1, VramTotalMB = -1;
    }

    /// <summary>NVIDIA Management Library, loaded only if the driver provides it.</summary>
    public static class Nvml
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string f);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr m, string n);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FInit();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FCount(out uint n);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FHandle(uint i, out IntPtr h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FName(IntPtr h, StringBuilder sb, uint len);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FTemp(IntPtr h, int sensor, out uint t);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FUtil(IntPtr h, out NvUtil u);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FMem(IntPtr h, out NvMem m);

        [StructLayout(LayoutKind.Sequential)] struct NvUtil { public uint Gpu, Memory; }
        [StructLayout(LayoutKind.Sequential)] struct NvMem { public ulong Total, Free, Used; }

        static bool _tried, _ready;
        static DateTime _next = DateTime.MinValue;
        static FCount _count; static FHandle _handle; static FName _name; static FTemp _temp; static FUtil _util; static FMem _mem;
        public static string Status = "not tried";

        static T Fn<T>(IntPtr lib, string n) where T : class
        {
            IntPtr p = GetProcAddress(lib, n);
            if (p == IntPtr.Zero) return null;
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        static bool Init()
        {
            if (_ready) return true;
            if (DateTime.UtcNow < _next) return false;
            _next = DateTime.UtcNow.AddSeconds(30);
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string pf = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
            string[] paths = new string[] { Path.Combine(sys, "nvml.dll"), Path.Combine(pf, @"NVIDIA Corporation\NVSMI\nvml.dll") };
            IntPtr lib = IntPtr.Zero;
            foreach (string p in paths) { if (File.Exists(p)) { lib = LoadLibraryW(p); if (lib != IntPtr.Zero) break; } }
            if (lib == IntPtr.Zero) { Status = "nvml.dll not found (no NVIDIA driver installed)"; return false; }
            FInit init = Fn<FInit>(lib, "nvmlInit_v2");
            _count = Fn<FCount>(lib, "nvmlDeviceGetCount_v2");
            _handle = Fn<FHandle>(lib, "nvmlDeviceGetHandleByIndex_v2");
            _name = Fn<FName>(lib, "nvmlDeviceGetName");
            _temp = Fn<FTemp>(lib, "nvmlDeviceGetTemperature");
            _util = Fn<FUtil>(lib, "nvmlDeviceGetUtilizationRates");
            _mem = Fn<FMem>(lib, "nvmlDeviceGetMemoryInfo");
            if (init == null || _count == null || _handle == null) { Status = "nvml.dll is missing required functions"; return false; }
            int r = init();
            if (r != 0) { Status = "nvmlInit failed (code " + r + ")"; return false; }
            _ready = true; Status = "ok";
            return true;
        }

        public static List<NvGpu> Poll()
        {
            List<NvGpu> l = new List<NvGpu>();
            try
            {
                if (!Init()) return l;
                uint n;
                if (_count(out n) != 0) { Status = "nvmlDeviceGetCount failed"; _ready = false; return l; }
                for (uint i = 0; i < n; i++)
                {
                    IntPtr h;
                    if (_handle(i, out h) != 0) continue;
                    NvGpu g = new NvGpu();
                    StringBuilder sb = new StringBuilder(96);
                    if (_name != null && _name(h, sb, 96) == 0) g.Name = sb.ToString();
                    uint t;
                    if (_temp != null && _temp(h, 0, out t) == 0) g.Temp = t;
                    NvUtil u;
                    if (_util != null && _util(h, out u) == 0) g.Util = u.Gpu;
                    NvMem m;
                    if (_mem != null && _mem(h, out m) == 0) { g.VramTotalMB = m.Total / 1048576.0; g.VramUsedMB = m.Used / 1048576.0; }
                    l.Add(g);
                }
                _tried = true;
            }
            catch (Exception ex) { Status = "NVML error: " + ex.Message; _ready = false; Log.Err("NVML", ex); }
            return l;
        }
    }

    /// <summary>
    /// Vendor-neutral GPU temperature through the Windows graphics kernel
    /// (the same source Task Manager uses). Works for AMD, NVIDIA and Intel
    /// drivers that report it.
    /// </summary>
    public static class GpuKernel
    {
        [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct ADAPTERINFO { public uint hAdapter; public LUID Luid; public uint NumSources; public int Precise; }
        [StructLayout(LayoutKind.Sequential)] struct ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }
        [StructLayout(LayoutKind.Sequential)] struct QUERYINFO { public uint hAdapter; public int Type; public IntPtr Data; public uint Size; }
        [StructLayout(LayoutKind.Sequential)] struct CLOSE { public uint hAdapter; }

        [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref ENUMADAPTERS2 e);
        [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QUERYINFO q);
        [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CLOSE c);

        public class Adapter { public long Luid; public double TempC = -1; public int FanRpm = -1; public string Note = ""; }

        public static int PerfDataType = 62;      // KMTQAITYPE_ADAPTERPERFDATA (62 in current SDKs; 56 is tried as fallback)
        public static string Status = "not tried";

        /// <summary>Diagnostic for the self-test: raw result of each query type on each adapter.</summary>
        public static string Dump()
        {
            StringBuilder sb = new StringBuilder();
            IntPtr arr = IntPtr.Zero;
            try
            {
                ENUMADAPTERS2 e = new ENUMADAPTERS2();
                int r = D3DKMTEnumAdapters2(ref e);
                if (r != 0 || e.NumAdapters == 0) return "enum 0x" + r.ToString("X");
                int sz = Marshal.SizeOf(typeof(ADAPTERINFO));
                arr = Marshal.AllocHGlobal(sz * (int)e.NumAdapters);
                e.pAdapters = arr;
                r = D3DKMTEnumAdapters2(ref e);
                for (int i = 0; i < e.NumAdapters; i++)
                {
                    ADAPTERINFO ai = (ADAPTERINFO)Marshal.PtrToStructure(new IntPtr(arr.ToInt64() + (long)i * sz), typeof(ADAPTERINFO));
                    sb.AppendLine("adapter " + i + " luid " + ai.Luid.High + ":" + ai.Luid.Low);
                    IntPtr data = Marshal.AllocHGlobal(256);
                    try
                    {
                        foreach (int type in new int[] { 56, 57, 58, 59, 60, 61, 62, 63, 64 })
                        {
                            for (int b = 0; b < 256; b++) Marshal.WriteByte(data, b, 0);
                            QUERYINFO q = new QUERYINFO(); q.hAdapter = ai.hAdapter; q.Type = type; q.Data = data; q.Size = 64;
                            int qr = D3DKMTQueryAdapterInfo(ref q);
                            StringBuilder h = new StringBuilder();
                            if (qr == 0) for (int b = 0; b < 64; b += 4) h.Append(((uint)Marshal.ReadInt32(data, b)).ToString("X") + " ");
                            sb.AppendLine("  type " + type + " -> 0x" + qr.ToString("X") + "  " + h);
                        }
                    }
                    finally { Marshal.FreeHGlobal(data); CLOSE c = new CLOSE(); c.hAdapter = ai.hAdapter; D3DKMTCloseAdapter(ref c); }
                }
            }
            catch (Exception ex) { sb.AppendLine("dump error " + ex.Message); }
            finally { if (arr != IntPtr.Zero) Marshal.FreeHGlobal(arr); }
            return sb.ToString();
        }

        public static List<Adapter> Read()
        {
            List<Adapter> res = new List<Adapter>();
            IntPtr arr = IntPtr.Zero;
            try
            {
                ENUMADAPTERS2 e = new ENUMADAPTERS2();
                int r = D3DKMTEnumAdapters2(ref e);
                if (r != 0 || e.NumAdapters == 0) { Status = "D3DKMTEnumAdapters2 returned 0x" + r.ToString("X"); return res; }
                int sz = Marshal.SizeOf(typeof(ADAPTERINFO));
                arr = Marshal.AllocHGlobal(sz * (int)e.NumAdapters);
                e.pAdapters = arr;
                r = D3DKMTEnumAdapters2(ref e);
                if (r != 0) { Status = "D3DKMTEnumAdapters2 (2nd) 0x" + r.ToString("X"); return res; }
                for (int i = 0; i < e.NumAdapters; i++)
                {
                    ADAPTERINFO ai = (ADAPTERINFO)Marshal.PtrToStructure(new IntPtr(arr.ToInt64() + (long)i * sz), typeof(ADAPTERINFO));
                    Adapter a = new Adapter();
                    a.Luid = ((long)ai.Luid.High << 32) | ai.Luid.Low;
                    IntPtr data = Marshal.AllocHGlobal(128);
                    try
                    {
                        string notes = "";
                        foreach (int type in new int[] { PerfDataType, 62, 56 })
                        {
                            for (int b = 0; b < 128; b++) Marshal.WriteByte(data, b, 0);
                            QUERYINFO q = new QUERYINFO();
                            q.hAdapter = ai.hAdapter; q.Type = type; q.Data = data; q.Size = 64;
                            int qr = D3DKMTQueryAdapterInfo(ref q);
                            if (qr != 0) { notes += "type " + type + " query 0x" + qr.ToString("X") + "; "; continue; }
                            uint raw = (uint)Marshal.ReadInt32(data, 56);
                            uint fan = (uint)Marshal.ReadInt32(data, 48);
                            if (fan > 0 && fan < 20000) a.FanRpm = (int)fan;
                            if (raw >= 130 && raw <= 1500) { a.TempC = raw / 10.0; PerfDataType = type; a.Note = "type " + type; break; }   // deci-Celsius
                            if (raw >= 15 && raw < 130) { a.TempC = raw; PerfDataType = type; a.Note = "type " + type; break; }               // whole degrees
                            notes += "type " + type + " raw " + raw + "; ";
                        }
                        if (a.TempC < 0) a.Note = "driver reports no temperature (" + notes + ")";
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(data);
                        CLOSE c = new CLOSE(); c.hAdapter = ai.hAdapter; D3DKMTCloseAdapter(ref c);
                    }
                    res.Add(a);
                }
                Status = "ok";
            }
            catch (Exception ex) { Status = "error: " + ex.Message; Log.Err("GpuKernel", ex); }
            finally { if (arr != IntPtr.Zero) Marshal.FreeHGlobal(arr); }
            return res;
        }
    }


    /// <summary>
    /// Reads CPU temperature from a hardware monitor the user already runs (MSI Afterburner or HWiNFO), through the
    /// shared memory those tools publish. This app loads no driver of its own, so it cannot upset anti-cheat.
    /// </summary>
    public static class ExtSensors
    {
        public static string Status = "not tried";
        static DateTime _next = DateTime.MinValue;
        static Reading _last = Reading.None("not tried");

        static string Str(System.IO.UnmanagedMemoryAccessor a, long pos, int max)
        {
            byte[] b = new byte[max];
            a.ReadArray(pos, b, 0, max);
            int n = Array.IndexOf(b, (byte)0); if (n < 0) n = max;
            return Encoding.Default.GetString(b, 0, n);
        }

        public static Reading CpuTemp()
        {
            if (DateTime.UtcNow < _next && !_last.Ok) return _last;
            Reading r = ReadAfterburner();
            if (!r.Ok) { Reading h = ReadHwinfo(); if (h.Ok) r = h; else r = Reading.None(r.Reason + " " + h.Reason); }
            _last = r;
            if (!r.Ok) _next = DateTime.UtcNow.AddSeconds(5);
            Status = r.Ok ? "ok: " + r.Source : r.Reason;
            return r;
        }

        static Reading ReadAfterburner()
        {
            System.IO.MemoryMappedFiles.MemoryMappedFile mm = null;
            try
            {
                try { mm = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting("MAHMSharedMemory", System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read); }
                catch { return Reading.None("MSI Afterburner is not running."); }
                using (System.IO.UnmanagedMemoryAccessor a = mm.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read))
                {
                    uint sig = a.ReadUInt32(0);
                    if (sig == 0xDEAD) return Reading.None("MSI Afterburner is closing.");
                    uint hdr = a.ReadUInt32(8), num = a.ReadUInt32(12), esz = a.ReadUInt32(16);
                    if (num == 0 || num > 600 || esz < 1312 || esz > 4096 || hdr > 4096) return Reading.None("MSI Afterburner shared memory has an unknown layout.");
                    double best = -1; string bestName = ""; bool exact = false;
                    for (uint i = 0; i < num; i++)
                    {
                        long o = hdr + (long)i * esz;
                        if (o + esz > a.Capacity) break;
                        string name = Str(a, o, 260);
                        string units = Str(a, o + 260, 260);
                        string n = name.ToLowerInvariant();
                        if (n.IndexOf("cpu") < 0 || n.IndexOf("temp") < 0 || n.IndexOf("gpu") >= 0) continue;
                        float v = a.ReadSingle(o + 1300);
                        if (float.IsNaN(v) || v < 5 || v > 125) continue;
                        bool isMain = n == "cpu temperature" || n.IndexOf("package") >= 0 || n.IndexOf("tctl") >= 0 || n.IndexOf("tdie") >= 0;
                        if (isMain && !exact) { best = v; bestName = name; exact = true; }
                        else if (isMain == exact && v > best) { best = v; bestName = name; }
                    }
                    if (best < 0) return Reading.None("MSI Afterburner is running but its monitor has no CPU temperature graph enabled (Settings > Monitoring > tick CPU temperature).");
                    return Reading.Of(best, "MSI Afterburner (" + bestName + ")");
                }
            }
            catch (Exception ex) { return Reading.None("MSI Afterburner read failed: " + ex.Message + "."); }
            finally { if (mm != null) mm.Dispose(); }
        }

        static Reading ReadHwinfo()
        {
            System.IO.MemoryMappedFiles.MemoryMappedFile mm = null;
            try
            {
                try { mm = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting("Global\\HWiNFO_SENS_SM2", System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read); }
                catch { return Reading.None("HWiNFO is not running with shared memory support."); }
                using (System.IO.UnmanagedMemoryAccessor a = mm.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read))
                {
                    uint sig = a.ReadUInt32(0);
                    if (sig == 0xDEAD) return Reading.None("HWiNFO shared memory is closed.");
                    uint rOff = a.ReadUInt32(32), rSize = a.ReadUInt32(36), rNum = a.ReadUInt32(40);
                    if (rNum == 0 || rNum > 5000 || rSize < 300 || rSize > 1024) return Reading.None("HWiNFO shared memory has an unknown layout.");
                    double best = -1; string bestName = ""; int bestRank = 99;
                    for (uint i = 0; i < rNum; i++)
                    {
                        long o = rOff + (long)i * rSize;
                        if (o + rSize > a.Capacity) break;
                        if (a.ReadInt32(o) != 1) continue;          // temperature readings only
                        string label = Str(a, o + 140, 128); if (label.Length == 0) label = Str(a, o + 12, 128);
                        string l = label.ToLowerInvariant();
                        if (l.IndexOf("cpu") < 0 || l.IndexOf("gpu") >= 0) continue;
                        double v = a.ReadDouble(o + 284);
                        if (double.IsNaN(v) || v < 5 || v > 125) continue;
                        int rank = l.IndexOf("tctl") >= 0 || l.IndexOf("tdie") >= 0 ? 0 : l.IndexOf("package") >= 0 ? 1 : l.IndexOf("die") >= 0 ? 2 : 3;
                        if (rank < bestRank || (rank == bestRank && v > best)) { bestRank = rank; best = v; bestName = label; }
                    }
                    if (best < 0) return Reading.None("HWiNFO is running but publishes no CPU temperature.");
                    return Reading.Of(best, "HWiNFO (" + bestName + ")");
                }
            }
            catch (Exception ex) { return Reading.None("HWiNFO read failed: " + ex.Message + "."); }
            finally { if (mm != null) mm.Dispose(); }
        }

        public static string FindAfterburner()
        {
            string[] c = new string[] {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + "\\MSI Afterburner\\MSIAfterburner.exe",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\MSI Afterburner\\MSIAfterburner.exe" };
            foreach (string p in c) if (System.IO.File.Exists(p)) return p;
            return null;
        }
        public static string FindHwinfo()
        {
            string[] c = new string[] {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\HWiNFO64\\HWiNFO64.exe",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + "\\HWiNFO32\\HWiNFO32.exe" };
            foreach (string p in c) if (System.IO.File.Exists(p)) return p;
            return null;
        }
    }

    public class SensorSnapshot
    {
        public Reading GpuTemp = Reading.None("not read yet");
        public Reading CpuTemp = Reading.None("not read yet");
        public Reading GpuUtil = Reading.None("not read yet");
        public Reading Disk = Reading.None("not read yet");
        public Reading GpuFan = Reading.None("not read yet");
        public List<NvGpu> Nvidia = new List<NvGpu>();
        public string GpuTempAll = "";
    }

    /// <summary>
    /// Reads temperatures and the fallback sensors. Every source is tried independently;
    /// a failure in one never affects the others, and failed sources retry on their own.
    /// </summary>
    public class SensorHub
    {
        List<PerformanceCounter> _thermal;
        bool _thermalTried;
        DateTime _thermalNext = DateTime.MinValue;
        string _thermalWhy = "";
        Pdh.Query _pdh;
        int _pdhDisk = -1;
        DateTime _pdhNext = DateTime.MinValue;
        int _tick;
        SensorSnapshot _snap = new SensorSnapshot();

        public SensorSnapshot Poll(long primaryLuid)
        {
            _tick++;
            SensorSnapshot s = new SensorSnapshot();
            // --- NVIDIA through NVML
            List<NvGpu> nv = Nvml.Poll();
            s.Nvidia = nv;

            // --- vendor-neutral kernel read (AMD, Intel, NVIDIA)
            List<GpuKernel.Adapter> ka = GpuKernel.Read();
            StringBuilder all = new StringBuilder();
            foreach (GpuKernel.Adapter a in ka) if (a.TempC >= 0) all.Append((all.Length > 0 ? ", " : "") + a.TempC.ToString("0") + "C");
            s.GpuTempAll = all.ToString();

            NvGpu best = null;
            foreach (NvGpu g in nv) if (g.Temp >= 0 && (best == null || g.Temp > best.Temp)) best = g;
            if (best != null)
            {
                s.GpuTemp = Reading.Of(best.Temp, "NVIDIA driver (NVML)");
            }
            else
            {
                GpuKernel.Adapter pick = null;
                foreach (GpuKernel.Adapter a in ka) if (a.TempC >= 0 && a.Luid == primaryLuid) pick = a;
                if (pick == null) foreach (GpuKernel.Adapter a in ka) if (a.TempC >= 0 && (pick == null || a.TempC > pick.TempC)) pick = a;
                if (pick != null) { s.GpuTemp = Reading.Of(pick.TempC, "Windows graphics driver"); if (pick.FanRpm > 0) s.GpuFan = Reading.Of(pick.FanRpm, "Windows graphics driver"); }
                else
                {
                    string why = ka.Count == 0 ? "Windows did not list a graphics adapter for sensors (" + GpuKernel.Status + ")." : "The graphics driver does not report a temperature to Windows.";
                    if (Nvml.Status != "ok" && Nvml.Status != "not tried" && Nvml.Status.IndexOf("not found", StringComparison.OrdinalIgnoreCase) < 0) why += " NVIDIA sensor library: " + Nvml.Status + ".";
                    s.GpuTemp = Reading.None(why);
                }
            }

            // --- NVIDIA utilisation fallback
            foreach (NvGpu g in nv) if (g.Util >= 0 && (s.GpuUtil.Value < g.Util)) s.GpuUtil = Reading.Of(g.Util, "NVIDIA driver (NVML)");
            if (!s.GpuUtil.Ok) s.GpuUtil = Reading.None("no NVIDIA sensor library");

            // --- CPU temperature (ACPI thermal zones only; core sensors need a vendor driver)
            s.CpuTemp = ExtSensors.CpuTemp();
            if (!s.CpuTemp.Ok) { string extWhy = s.CpuTemp.Reason; Reading z = ReadCpuTemp(); s.CpuTemp = z.Ok ? z : Reading.None(extWhy); }

            // --- disk activity, language independent
            s.Disk = ReadDisk();

            _snap = s;
            return s;
        }

        Reading ReadCpuTemp()
        {
            try
            {
                if (_thermal == null && DateTime.UtcNow >= _thermalNext)
                {
                    _thermalNext = DateTime.UtcNow.AddSeconds(60);
                    try
                    {
                        if (PerformanceCounterCategory.Exists("Thermal Zone Information"))
                        {
                            PerformanceCounterCategory c = new PerformanceCounterCategory("Thermal Zone Information");
                            string[] inst = c.GetInstanceNames();
                            if (inst.Length > 0) { _thermal = new List<PerformanceCounter>(); foreach (string zi in inst) _thermal.Add(new PerformanceCounter("Thermal Zone Information", "Temperature", zi)); _thermalWhy = ""; }
                            else _thermalWhy = "Windows lists no thermal zones on this PC.";
                        }
                        else _thermalWhy = "This PC has no ACPI thermal zone counters.";
                    }
                    catch (Exception ex) { _thermalWhy = "Thermal counters unavailable (" + ex.Message + ")."; }
                }
                if (_thermal != null)
                {
                    double best = -1;
                    foreach (PerformanceCounter pc in _thermal) { double c = pc.NextValue() - 273.15; if (c > best) best = c; }
                    // a motherboard zone below 25 C is not a CPU reading (it is usually a fixed or ambient sensor)
                    if (best >= 25 && best <= 110) return Reading.Of(best, "Motherboard thermal zone (approximate, not the CPU core sensor)");
                    _thermalWhy = "Windows only exposes a motherboard thermal zone here (" + best.ToString("0") + "\u00B0C), which is not the CPU core temperature.";
                }
            }
            catch (Exception ex) { _thermal = null; _thermalWhy = "Thermal read failed: " + ex.Message; }
            if (string.IsNullOrEmpty(_thermalWhy)) _thermalWhy = "not available";
            return Reading.None(_thermalWhy + " Reading core temperature needs a hardware sensor driver, which this app does not install because such drivers can conflict with anti-cheat.");
        }

        Reading ReadDisk()
        {
            try
            {
                if (_pdh == null && DateTime.UtcNow >= _pdhNext)
                {
                    _pdhNext = DateTime.UtcNow.AddSeconds(30);
                    Pdh.Query q = new Pdh.Query();
                    int i = q.Add("\\PhysicalDisk(_Total)\\% Idle Time");
                    if (i >= 0) { _pdh = q; _pdhDisk = i; q.Collect(); }
                    else q.Dispose();
                }
                if (_pdh != null)
                {
                    if (!_pdh.Collect()) { _pdh.Dispose(); _pdh = null; return Reading.None("Disk counters stopped answering; retrying."); }
                    double idle = _pdh.Value(_pdhDisk);
                    if (idle >= 0) return Reading.Of(Math.Max(0, Math.Min(100, 100 - idle)), "Windows performance counters");
                }
            }
            catch (Exception ex) { if (_pdh != null) { try { _pdh.Dispose(); } catch { } _pdh = null; } return Reading.None("Disk counters failed: " + ex.Message); }
            return Reading.None("Windows has no disk performance counters on this PC.");
        }
    }
}
