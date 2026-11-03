using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace InfernoXC
{
    public class ProcRow
    {
        public string Name { get; set; }
        public int Count { get; set; }
        public int Pid { get; set; }
        public string Path { get; set; }
        public double Cpu { get; set; }
        public double Gpu { get; set; }
        public double RamMB { get; set; }
        public double VramMB { get; set; }
        public bool IsGame { get; set; }

        public string Display { get { return Count > 1 ? Name + "  (" + Count + ")" : Name; } }
        public string CpuText { get { return Cpu < 0.05 ? "-" : Cpu.ToString("0.0") + " %"; } }
        public string GpuText { get { return Gpu < 0.05 ? "-" : Gpu.ToString("0.0") + " %"; } }
        public string RamText { get { return Util.MB(RamMB); } }
        public string VramText { get { return VramMB < 1 ? "-" : Util.MB(VramMB); } }
        public string Kind { get { return IsGame ? "Game" : ""; } }
    }

    public class SystemSample
    {
        public double Cpu, Gpu, RamUsedMB, RamTotalMB, VramUsedMB;
        public bool GpuOk;
        public string GpuSource = "", GpuReason = "";
        public long GpuLuid;
        public SensorSnapshot Sensors = new SensorSnapshot();
        public List<ProcRow> Rows = new List<ProcRow>();
    }

    public class SysMonitor
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        public static readonly HashSet<string> KnownGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TslGame","VALORANT-Win64-Shipping","VALORANT","cs2","csgo","FortniteClient-Win64-Shipping","r5apex","r5apex_dx12","ApexLegends",
            "Overwatch","GTA5","GTA5_Enhanced","RDR2","eldenring","nightreign","cyberpunk2077","witcher3","Destiny2","RainbowSix","RainbowSix_Vulkan",
            "dota2","League of Legends","RobloxPlayerBeta","FallGuys_client_game","VRChat","Minecraft","javaw","BF2042","bf1","bfv","Warzone","cod","ModernWarfare",
            "MarvelRivals-Win64-Shipping","Deadlock","hl2","Palworld-Win64-Shipping","Lethal Company","Rust","RustClient","EscapeFromTarkov","TheFinals",
            "Wuthering Waves","GenshinImpact","StarRail","ZenlessZoneZero","Hades2","HorizonZeroDawn","Cyberpunk2077","hogwartslegacy","Wow","WoW-64","Diablo IV","PathOfExile_x64","ffxiv_dx11"
        };

        static HashSet<string> _custom;
        public static bool IsGameName(string n)
        {
            if (KnownGames.Contains(n)) return true;
            if (_custom == null)
            {
                _custom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string s in Settings.Get("custom_games", "").Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    _custom.Add(s.Trim());
            }
            return _custom.Contains(n);
        }
        public static void ReloadCustom() { _custom = null; }

        readonly int _logical = Environment.ProcessorCount;
        Dictionary<int, TimeSpan> _prevCpu = new Dictionary<int, TimeSpan>();
        DateTime _prevT = DateTime.MinValue;
        PerformanceCounter _cpuTotal;
        Dictionary<string, CounterSample> _prevGpu = new Dictionary<string, CounterSample>();
        DateTime _gpuRetryAt = DateTime.MinValue;
        int _gpuFails;
        string _gpuReason = "";
        long _primaryLuid;
        SensorHub _hub = new SensorHub();
        Pdh.Query _pdhGpu;
        int _pdhGpuIdx = -1;
        public string GpuSourceNow = "";
        static readonly Regex LuidRx = new Regex(@"luid_0x([0-9a-fA-F]+)_0x([0-9a-fA-F]+)", RegexOptions.Compiled);
        static readonly Regex GpuInst = new Regex(@"pid_(\d+)_.*?engtype_(.+)$", RegexOptions.Compiled);
        static readonly Regex PidInst = new Regex(@"pid_(\d+)_", RegexOptions.Compiled);

        public double TotalRamMB;
        public SysMonitor()
        {
            try { _cpuTotal = new PerformanceCounter("Processor", "% Processor Time", "_Total"); _cpuTotal.NextValue(); } catch { }
            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(m)) TotalRamMB = m.ullTotalPhys / 1048576.0;
        }

        public SystemSample Sample()
        {
            SystemSample s = new SystemSample();
            DateTime now = DateTime.UtcNow;
            double dt = _prevT == DateTime.MinValue ? 1.0 : (now - _prevT).TotalSeconds;
            if (dt < 0.2) dt = 0.2;
            _prevT = now;

            try { if (_cpuTotal != null) s.Cpu = Math.Min(100, Math.Max(0, _cpuTotal.NextValue())); } catch { }

            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(m))
            {
                s.RamTotalMB = m.ullTotalPhys / 1048576.0;
                s.RamUsedMB = (m.ullTotalPhys - m.ullAvailPhys) / 1048576.0;
            }

            // GPU counters: .NET counters first, then language-independent PDH, then the vendor library.
            // A failure is never permanent: it is retried every 15 seconds and the reason is kept.
            Dictionary<int, double> gpuPid = new Dictionary<int, double>();
            Dictionary<int, double> vramPid = new Dictionary<int, double>();
            if (DateTime.UtcNow >= _gpuRetryAt)
            {
                bool ok = false;
                try { ReadGpu(s, gpuPid, vramPid); ok = true; s.GpuSource = "Windows GPU counters"; }
                catch (Exception ex)
                {
                    _gpuReason = ".NET counters: " + ex.Message;
                    Log.Err("GPU counters", ex);
                    try { ok = ReadGpuPdh(s, gpuPid, vramPid); if (ok) s.GpuSource = "Windows GPU counters (PDH)"; else _gpuReason += " | PDH: no GPU Engine counters"; }
                    catch (Exception ex2) { _gpuReason += " | PDH: " + ex2.Message; }
                }
                if (ok) { s.GpuOk = true; _gpuFails = 0; _gpuReason = ""; GpuSourceNow = s.GpuSource; }
                else { _gpuFails++; _gpuRetryAt = DateTime.UtcNow.AddSeconds(15); GpuSourceNow = ""; }
            }
            else
            {
                // between retries keep using the working source if there is one
                if (GpuSourceNow.Length > 0)
                {
                    try { ReadGpu(s, gpuPid, vramPid); s.GpuOk = true; s.GpuSource = GpuSourceNow; }
                    catch { _gpuRetryAt = DateTime.MinValue; }
                }
            }
            s.GpuReason = _gpuReason;

            // sensors (temperatures, NVIDIA fallback, disk activity)
            try
            {
                s.Sensors = _hub.Poll(_primaryLuid);
                s.GpuLuid = _primaryLuid;
                if (!s.GpuOk && s.Sensors.GpuUtil.Ok)
                {
                    s.Gpu = s.Sensors.GpuUtil.Value; s.GpuOk = true; s.GpuSource = s.Sensors.GpuUtil.Source;
                    foreach (NvGpu g in s.Sensors.Nvidia) if (g.VramUsedMB > s.VramUsedMB) s.VramUsedMB = g.VramUsedMB;
                }
                if (s.GpuOk && s.VramUsedMB <= 0)
                    foreach (NvGpu g in s.Sensors.Nvidia) if (g.VramUsedMB > s.VramUsedMB) s.VramUsedMB = g.VramUsedMB;
                if (!s.GpuOk) s.GpuReason = (string.IsNullOrEmpty(_gpuReason) ? "No GPU counters" : _gpuReason) + ". Retrying automatically.";
            }
            catch (Exception ex) { Log.Err("sensors", ex); }

            Dictionary<string, ProcRow> byName = new Dictionary<string, ProcRow>(StringComparer.OrdinalIgnoreCase);
            Dictionary<int, TimeSpan> curCpu = new Dictionary<int, TimeSpan>();
            Process[] procs = Process.GetProcesses();
            foreach (Process p in procs)
            {
                try
                {
                    int pid = p.Id;
                    if (pid == 0) continue;
                    string name = p.ProcessName;
                    double cpu = 0;
                    try
                    {
                        TimeSpan t = p.TotalProcessorTime;
                        curCpu[pid] = t;
                        TimeSpan prev;
                        if (_prevCpu.TryGetValue(pid, out prev))
                            cpu = (t - prev).TotalSeconds / (dt * _logical) * 100.0;
                    }
                    catch { }
                    if (cpu < 0) cpu = 0;
                    double ram = 0;
                    try { ram = p.WorkingSet64 / 1048576.0; } catch { }
                    double g; gpuPid.TryGetValue(pid, out g);
                    double v; vramPid.TryGetValue(pid, out v);

                    ProcRow r;
                    if (!byName.TryGetValue(name, out r))
                    {
                        r = new ProcRow();
                        r.Name = name;
                        r.Pid = pid;
                        r.IsGame = IsGameName(name);
                        byName[name] = r;
                    }
                    r.Count++;
                    r.Cpu += cpu;
                    r.Gpu += g;
                    r.RamMB += ram;
                    r.VramMB += v;
                    if (r.Path == null)
                    {
                        try { r.Path = p.MainModule.FileName; r.Pid = pid; } catch { }
                        if (!r.IsGame && r.Path != null && GameLib.IsGamePath(r.Path)) r.IsGame = true;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            _prevCpu = curCpu;
            s.Rows = byName.Values.ToList();
            return s;
        }

        void ReadGpu(SystemSample s, Dictionary<int, double> gpuPid, Dictionary<int, double> vramPid)
        {
            PerformanceCounterCategory eng = new PerformanceCounterCategory("GPU Engine");
            InstanceDataCollectionCollection all = eng.ReadCategory();
            if (!all.Contains("Utilization Percentage")) throw new InvalidOperationException("the GPU Engine category has no Utilization Percentage counter (needs Windows 10 1709 or newer with a WDDM 2.x driver)");
            InstanceDataCollection util = all["Utilization Percentage"];
            Dictionary<string, CounterSample> cur = new Dictionary<string, CounterSample>();
            Dictionary<int, Dictionary<string, double>> perPidType = new Dictionary<int, Dictionary<string, double>>();
            Dictionary<string, double> typeTotal = new Dictionary<string, double>();
            foreach (InstanceData d in util.Values)
            {
                cur[d.InstanceName] = d.Sample;
                CounterSample prev;
                if (!_prevGpu.TryGetValue(d.InstanceName, out prev)) continue;
                float val;
                try { val = CounterSampleCalculator.ComputeCounterValue(prev, d.Sample); } catch { continue; }
                if (val <= 0) continue;
                Match mt = GpuInst.Match(d.InstanceName);
                if (!mt.Success) continue;
                int pid = int.Parse(mt.Groups[1].Value);
                string type = mt.Groups[2].Value;
                Dictionary<string, double> pt;
                if (!perPidType.TryGetValue(pid, out pt)) { pt = new Dictionary<string, double>(); perPidType[pid] = pt; }
                double cv; pt.TryGetValue(type, out cv); pt[type] = cv + val;
                double tv; typeTotal.TryGetValue(type, out tv); typeTotal[type] = tv + val;
            }
            _prevGpu = cur;
            foreach (KeyValuePair<int, Dictionary<string, double>> kv in perPidType)
                gpuPid[kv.Key] = Math.Min(100, kv.Value.Values.Max());
            s.Gpu = typeTotal.Count == 0 ? 0 : Math.Min(100, typeTotal.Values.Max());

            try
            {
                PerformanceCounterCategory pm = new PerformanceCounterCategory("GPU Process Memory");
                InstanceDataCollection ded = pm.ReadCategory()["Dedicated Usage"];
                foreach (InstanceData d in ded.Values)
                {
                    Match mt = PidInst.Match(d.InstanceName);
                    if (!mt.Success) continue;
                    int pid = int.Parse(mt.Groups[1].Value);
                    double cv; vramPid.TryGetValue(pid, out cv);
                    vramPid[pid] = cv + d.Sample.RawValue / 1048576.0;
                }
            }
            catch { }
            try
            {
                PerformanceCounterCategory am = new PerformanceCounterCategory("GPU Adapter Memory");
                InstanceDataCollection ded = am.ReadCategory()["Dedicated Usage"];
                double best = 0;
                foreach (InstanceData d in ded.Values)
                {
                    double mb = d.Sample.RawValue / 1048576.0;
                    if (mb >= best)
                    {
                        best = mb;
                        Match lm = LuidRx.Match(d.InstanceName);
                        if (lm.Success) { try { _primaryLuid = (Convert.ToInt64(lm.Groups[1].Value, 16) << 32) | Convert.ToInt64(lm.Groups[2].Value, 16); } catch { } }
                    }
                }
                s.VramUsedMB = best;
            }
            catch { }
        }

        /// <summary>Language-independent fallback for the GPU Engine counters.</summary>
        bool ReadGpuPdh(SystemSample s, Dictionary<int, double> gpuPid, Dictionary<int, double> vramPid)
        {
            if (_pdhGpu == null)
            {
                Pdh.Query q = new Pdh.Query();
                int i = q.Add("\\GPU Engine(*)\\Utilization Percentage");
                if (i < 0) { q.Dispose(); return false; }
                _pdhGpu = q; _pdhGpuIdx = i;
                q.Collect();
                System.Threading.Thread.Sleep(250);
            }
            if (!_pdhGpu.Collect()) { _pdhGpu.Dispose(); _pdhGpu = null; return false; }
            Dictionary<string, double> vals = _pdhGpu.Array(_pdhGpuIdx);
            Dictionary<int, Dictionary<string, double>> perPidType = new Dictionary<int, Dictionary<string, double>>();
            Dictionary<string, double> typeTotal = new Dictionary<string, double>();
            foreach (KeyValuePair<string, double> kv in vals)
            {
                if (kv.Value <= 0) continue;
                Match mt = GpuInst.Match(kv.Key);
                if (!mt.Success) continue;
                int pid = int.Parse(mt.Groups[1].Value);
                string type = mt.Groups[2].Value;
                Dictionary<string, double> pt;
                if (!perPidType.TryGetValue(pid, out pt)) { pt = new Dictionary<string, double>(); perPidType[pid] = pt; }
                double cv; pt.TryGetValue(type, out cv); pt[type] = cv + kv.Value;
                double tv; typeTotal.TryGetValue(type, out tv); typeTotal[type] = tv + kv.Value;
            }
            foreach (KeyValuePair<int, Dictionary<string, double>> kv in perPidType)
                gpuPid[kv.Key] = Math.Min(100, kv.Value.Values.Max());
            s.Gpu = typeTotal.Count == 0 ? 0 : Math.Min(100, typeTotal.Values.Max());
            return true;
        }
    }
}
