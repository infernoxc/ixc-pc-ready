using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace InfernoXC
{
    public class GpuInfo
    {
        public string Name;
        public string Driver;
        public DateTime? DriverDate;
        public long VramMB;
        public string Vendor;
        public int Hz;
        public string Res;
    }

    public class RamModule
    {
        public string Slot;
        public double CapacityGB;
        public int Speed;
        public int Configured;
        public string Maker;
        public string Part;
    }

    public class DiskInfo
    {
        public string Model;
        public string Kind;
        public double SizeGB;
    }

    public class Finding
    {
        public int Level; // 0 ok, 1 info, 2 warn, 3 problem
        public string Title;
        public string Detail;
        public Finding(int l, string t, string d) { Level = l; Title = t; Detail = d; }
    }

    public class HardwareInfo
    {
        public string CpuName, CpuVendor, Board, Bios, Os, OsBuild;
        public int Cores, Threads, CpuMaxMHz;
        public List<GpuInfo> Gpus = new List<GpuInfo>();
        public List<RamModule> Ram = new List<RamModule>();
        public int RamSlots;
        public double RamTotalGB;
        public List<DiskInfo> Disks = new List<DiskInfo>();
        public string SysDrive;
        public double SysFreeGB, SysTotalGB;
        public double UptimeDays;
        public bool Hags;
        public List<string> Antivirus = new List<string>();
        public bool IsLaptop, HasBattery;
        public string PowerPlan = "", PowerPlanGuid = "";
        public string GpuSourceNote = "";
        public List<Finding> Findings = new List<Finding>();

        public GpuInfo MainGpu
        {
            get
            {
                if (Gpus.Count == 0) return null;
                GpuInfo best = Gpus[0];
                foreach (GpuInfo g in Gpus) if (g.VramMB > best.VramMB) best = g;
                return best;
            }
        }

        static IEnumerable<ManagementBaseObject> Q(string scope, string query)
        {
            List<ManagementBaseObject> list = new List<ManagementBaseObject>();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope, query))
                    foreach (ManagementBaseObject o in s.Get()) list.Add(o);
            }
            catch (Exception ex) { Log.Err("WMI " + query, ex); }
            return list;
        }
        static string S(ManagementBaseObject o, string p)
        {
            try { object v = o[p]; return v == null ? "" : v.ToString().Trim(); } catch { return ""; }
        }
        static long L(ManagementBaseObject o, string p)
        {
            try { object v = o[p]; return v == null ? 0 : Convert.ToInt64(v); } catch { return 0; }
        }

        static long VramFromRegistry(string name)
        {
            try
            {
                using (RegistryKey cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls == null) return 0;
                    foreach (string sub in cls.GetSubKeyNames())
                    {
                        if (sub.Length != 4) continue;
                        using (RegistryKey k = cls.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            object d = k.GetValue("DriverDesc");
                            if (d == null || !string.Equals(d.ToString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                            object q = k.GetValue("HardwareInformation.qwMemorySize");
                            if (q is long) return (long)q;
                            if (q is byte[] && ((byte[])q).Length >= 8) return BitConverter.ToInt64((byte[])q, 0);
                            object m = k.GetValue("HardwareInformation.MemorySize");
                            if (m is byte[] && ((byte[])m).Length >= 4) return BitConverter.ToUInt32((byte[])m, 0);
                            if (m is int) return (uint)(int)m;
                        }
                    }
                }
            }
            catch { }
            return 0;
        }

        public static HardwareInfo Read()
        {
            HardwareInfo h = new HardwareInfo();

            foreach (ManagementBaseObject o in Q("root\\cimv2", "SELECT Name,Manufacturer,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed FROM Win32_Processor"))
            {
                h.CpuName = Regex1(S(o, "Name"));
                h.CpuVendor = S(o, "Manufacturer");
                h.Cores += (int)L(o, "NumberOfCores");
                h.Threads += (int)L(o, "NumberOfLogicalProcessors");
                h.CpuMaxMHz = (int)L(o, "MaxClockSpeed");
            }
            if (h.Threads == 0) h.Threads = Environment.ProcessorCount;

            foreach (ManagementBaseObject o in Q("root\\cimv2", "SELECT Name,DriverVersion,DriverDate,AdapterRAM,CurrentRefreshRate,CurrentHorizontalResolution,CurrentVerticalResolution,AdapterCompatibility FROM Win32_VideoController"))
            {
                string n = S(o, "Name");
                if (n.Length == 0 || n.IndexOf("Basic", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Remote", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                GpuInfo g = new GpuInfo();
                g.Name = n;
                g.Driver = S(o, "DriverVersion");
                g.Vendor = S(o, "AdapterCompatibility");
                try { string dd = S(o, "DriverDate"); if (dd.Length > 0) g.DriverDate = ManagementDateTimeConverter.ToDateTime(dd); } catch { }
                long vram = VramFromRegistry(n);
                if (vram <= 0) vram = L(o, "AdapterRAM");
                g.VramMB = vram / (1024 * 1024);
                // AdapterRAM is a 32-bit value and caps at 4 GB; NVIDIA's own library knows the real size
                if (g.VramMB <= 4096 && n.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try
                    {
                        foreach (NvGpu ng in Nvml.Poll())
                            if (ng.Name != null && (n.IndexOf(ng.Name.Replace("NVIDIA ", ""), StringComparison.OrdinalIgnoreCase) >= 0 || ng.Name.IndexOf(n.Replace("NVIDIA ", ""), StringComparison.OrdinalIgnoreCase) >= 0) && ng.VramTotalMB > g.VramMB)
                                g.VramMB = (long)ng.VramTotalMB;
                    }
                    catch { }
                }
                g.Hz = (int)L(o, "CurrentRefreshRate");
                long w = L(o, "CurrentHorizontalResolution"), hh = L(o, "CurrentVerticalResolution");
                if (w > 0 && hh > 0) g.Res = w + "x" + hh;
                h.Gpus.Add(g);
            }

            foreach (ManagementBaseObject o in Q("root\\cimv2", "SELECT BankLabel,DeviceLocator,Capacity,Speed,ConfiguredClockSpeed,Manufacturer,PartNumber FROM Win32_PhysicalMemory"))
            {
                RamModule m = new RamModule();
                m.Slot = S(o, "DeviceLocator");
                m.CapacityGB = L(o, "Capacity") / 1073741824.0;
                m.Speed = (int)L(o, "Speed");
                m.Configured = (int)L(o, "ConfiguredClockSpeed");
                m.Maker = S(o, "Manufacturer");
                m.Part = S(o, "PartNumber");
                h.Ram.Add(m);
                h.RamTotalGB += m.CapacityGB;
            }
            if (h.Gpus.Count == 0) { h.GpuFallback(); }

            foreach (ManagementBaseObject o in Q("root\\cimv2", "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
                h.RamSlots += (int)L(o, "MemoryDevices");

            foreach (ManagementBaseObject o in Q("root\\cimv2", "SELECT Manufacturer,Product FROM Win32_BaseBoard"))
                h.Board = (S(o, "Manufacturer") + " " + S(o, "Product")).Trim();
            foreach (ManagementBaseObject o in Q("root\\cimv2", "SELECT SMBIOSBIOSVersion FROM Win32_BIOS"))
                h.Bios = S(o, "SMBIOSBIOSVersion");
            foreach (ManagementBaseObject o in Q("root\\cimv2", "SELECT Caption,BuildNumber,LastBootUpTime FROM Win32_OperatingSystem"))
            {
                h.Os = S(o, "Caption").Replace("Microsoft ", "");
                h.OsBuild = S(o, "BuildNumber");
                try { h.UptimeDays = (DateTime.Now - ManagementDateTimeConverter.ToDateTime(S(o, "LastBootUpTime"))).TotalDays; } catch { }
            }

            // physical disks (SSD / HDD / NVMe)
            bool gotStorage = false;
            foreach (ManagementBaseObject o in Q("root\\Microsoft\\Windows\\Storage", "SELECT FriendlyName,MediaType,BusType,Size FROM MSFT_PhysicalDisk"))
            {
                DiskInfo d = new DiskInfo();
                d.Model = S(o, "FriendlyName");
                d.SizeGB = L(o, "Size") / 1073741824.0;
                long mt = L(o, "MediaType"), bt = L(o, "BusType");
                d.Kind = bt == 17 ? "NVMe SSD" : mt == 4 ? "SSD" : mt == 3 ? "HDD" : "Disk";
                h.Disks.Add(d);
                gotStorage = true;
            }
            if (!gotStorage)
                foreach (ManagementBaseObject o in Q("root\\cimv2", "SELECT Model,Size FROM Win32_DiskDrive"))
                {
                    DiskInfo d = new DiskInfo();
                    d.Model = S(o, "Model"); d.SizeGB = L(o, "Size") / 1073741824.0; d.Kind = "Disk";
                    h.Disks.Add(d);
                }

            try
            {
                string sd = Path.GetPathRoot(Environment.SystemDirectory);
                DriveInfo di = new DriveInfo(sd);
                h.SysDrive = sd;
                h.SysFreeGB = di.AvailableFreeSpace / 1073741824.0;
                h.SysTotalGB = di.TotalSize / 1073741824.0;
            }
            catch { }

            int? hw = Util.RegInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
            h.Hags = hw.HasValue && hw.Value == 2;

            foreach (ManagementBaseObject o in Q("root\\SecurityCenter2", "SELECT displayName FROM AntiVirusProduct"))
                h.Antivirus.Add(S(o, "displayName"));

            // laptop or desktop
            foreach (ManagementBaseObject o in Q("root\\cimv2", "SELECT ChassisTypes FROM Win32_SystemEnclosure"))
            {
                try
                {
                    ushort[] ct = o["ChassisTypes"] as ushort[];
                    if (ct != null) foreach (ushort c in ct) if (c == 8 || c == 9 || c == 10 || c == 11 || c == 12 || c == 14 || c == 18 || c == 21 || c == 30 || c == 31 || c == 32) h.IsLaptop = true;
                }
                catch { }
            }
            foreach (ManagementBaseObject o in Q("root\\cimv2", "SELECT Name FROM Win32_Battery")) { h.HasBattery = true; }
            if (h.HasBattery && h.Disks.Count > 0 && !h.IsLaptop)
            {
                // a battery on a desktop chassis is usually a UPS; treat it as a laptop only if the chassis says so
            }
            try
            {
                Util.RunResult pr = Util.Run("powercfg.exe", "/getactivescheme", 8000);
                Match pm = Regex.Match(pr.Out ?? "", @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\(([^)]*)\)");
                if (pm.Success) { h.PowerPlanGuid = pm.Groups[1].Value.ToLowerInvariant(); h.PowerPlan = pm.Groups[2].Value.Trim(); }
            }
            catch (Exception ex) { Log.Err("power plan", ex); }

            h.BuildFindings();
            return h;
        }

        /// <summary>If WMI lists no graphics adapter, read them straight from the registry class key.</summary>
        void GpuFallback()
        {
            try
            {
                using (RegistryKey cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls == null) return;
                    foreach (string sub in cls.GetSubKeyNames())
                    {
                        if (sub.Length != 4) continue;
                        using (RegistryKey k = cls.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            object d = k.GetValue("DriverDesc");
                            if (d == null) continue;
                            string n = d.ToString();
                            if (n.IndexOf("Basic", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Remote", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            GpuInfo g = new GpuInfo();
                            g.Name = n;
                            g.Driver = k.GetValue("DriverVersion") == null ? "" : k.GetValue("DriverVersion").ToString();
                            g.Vendor = k.GetValue("ProviderName") == null ? "" : k.GetValue("ProviderName").ToString();
                            g.VramMB = VramFromRegistry(n) / (1024 * 1024);
                            Gpus.Add(g);
                        }
                    }
                }
                if (Gpus.Count > 0) GpuSourceNote = "Graphics adapters were read from the registry because WMI returned none.";
            }
            catch (Exception ex) { Log.Err("GPU registry fallback", ex); }
        }

        static string Regex1(string s)
        {
            return System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();
        }

        void BuildFindings()
        {
            // RAM kit
            if (Ram.Count == 0)
            {
                Findings.Add(new Finding(1, "RAM not readable", "Windows did not report memory module details."));
            }
            else
            {
                if (Ram.Count == 1 && RamSlots >= 2)
                    Findings.Add(new Finding(2, "Single memory stick", "Only one RAM module is installed, so memory runs single-channel. Two matching sticks give noticeably more bandwidth, which helps CPU-bound games."));
                var parts = Ram.Select(m => (m.Part ?? "").Trim().ToUpperInvariant()).Distinct().ToList();
                var caps = Ram.Select(m => Math.Round(m.CapacityGB)).Distinct().ToList();
                var spd = Ram.Select(m => m.Speed).Distinct().ToList();
                if (Ram.Count > 1 && (parts.Count > 1 || caps.Count > 1 || spd.Count > 1))
                {
                    string why = parts.Count > 1 ? "different part numbers (" + string.Join(" / ", parts.ToArray()) + ")" : caps.Count > 1 ? "different capacities" : "different rated speeds";
                    Findings.Add(new Finding(2, "RAM sticks are not a matched kit", "The installed modules have " + why + ". Mixed kits can be unstable at XMP/DOCP speeds and are a classic cause of random game crashes and heap-corruption errors. Run a memory test and, if errors appear, lower the RAM speed or use a matched kit."));
                }
                int slow = Ram.Count(m => m.Configured > 0 && m.Speed > 0 && m.Configured + 100 < m.Speed);
                if (slow > 0)
                    Findings.Add(new Finding(2, "RAM running below its rated speed", "Memory is set to " + Ram[0].Configured + " MT/s but rated for " + Ram[0].Speed + " MT/s. Enable DOCP / XMP in the BIOS for free performance."));
                else if (Ram.Any(m => m.Configured >= 2933))
                    Findings.Add(new Finding(0, "RAM speed", Ram[0].Configured + " MT/s, " + Ram.Count + " module(s), " + RamTotalGB.ToString("0") + " GB total."));
                if (RamTotalGB > 0 && RamTotalGB < 15.5)
                    Findings.Add(new Finding(2, "Low memory", "Only " + RamTotalGB.ToString("0") + " GB of RAM. Modern games and streaming are happier with 16 GB or more."));
            }

            // antivirus
            if (Antivirus.Count == 0)
                Findings.Add(new Finding(2, "No antivirus registered", "Windows Security Center reports no active antivirus. Install one you trust, or re-enable Windows Defender, to stay protected."));
            else
                Findings.Add(new Finding(0, "Antivirus", string.Join(", ", Antivirus.ToArray())));

            // disk space
            if (SysTotalGB > 0)
            {
                double pct = SysFreeGB / SysTotalGB * 100;
                if (pct < 10) Findings.Add(new Finding(3, "System drive almost full", SysDrive + " has only " + SysFreeGB.ToString("0.0") + " GB free (" + pct.ToString("0") + "%). SSDs slow down when nearly full."));
                else if (pct < 20) Findings.Add(new Finding(2, "System drive getting full", SysDrive + " has " + SysFreeGB.ToString("0.0") + " GB free (" + pct.ToString("0") + "%). Keep at least 20% free for best SSD speed."));
                else Findings.Add(new Finding(0, "System drive space", SysFreeGB.ToString("0") + " GB free on " + SysDrive));
            }

            // storage type
            if (Disks.Count > 0 && !Disks.Any(d => d.Kind.IndexOf("SSD") >= 0))
                Findings.Add(new Finding(2, "No SSD detected", "Games and Windows on a hard disk load slowly and stutter. An SSD is the biggest upgrade for that."));

            // driver age
            GpuInfo g = MainGpu;
            if (g != null && g.DriverDate.HasValue)
            {
                double months = (DateTime.Now - g.DriverDate.Value).TotalDays / 30.4;
                if (months > 12) Findings.Add(new Finding(2, "Old graphics driver", g.Name + " driver is from " + g.DriverDate.Value.ToString("MMM yyyy") + ". Update from the " + (g.Vendor ?? "GPU") + " website."));
                else Findings.Add(new Finding(0, "Graphics driver", g.Driver + " (" + g.DriverDate.Value.ToString("MMM yyyy") + ")"));
            }

            // uptime
            if (UptimeDays > 7)
                Findings.Add(new Finding(1, "Long uptime", "PC has been on for " + UptimeDays.ToString("0") + " days. A restart clears leaked memory and pending updates."));

            // multiple gpus
            if (Gpus.Count > 1)
                Findings.Add(new Finding(1, "More than one GPU", "Make sure your game is set to use the " + (MainGpu != null ? MainGpu.Name : "dedicated GPU") + " (see Processes page > GPU preference)."));
        }

        public string Summary()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("CPU: " + CpuName + " (" + Cores + "C/" + Threads + "T)");
            foreach (GpuInfo g in Gpus) sb.AppendLine("GPU: " + g.Name + " " + g.VramMB + " MB driver " + g.Driver);
            sb.AppendLine("RAM: " + RamTotalGB.ToString("0") + " GB in " + Ram.Count + "/" + RamSlots + " slots");
            foreach (RamModule m in Ram) sb.AppendLine("  " + m.Slot + " " + m.CapacityGB.ToString("0") + "GB " + m.Speed + "/" + m.Configured + " " + m.Maker + " " + m.Part);
            sb.AppendLine("Board: " + Board + " BIOS " + Bios);
            sb.AppendLine("OS: " + Os + " build " + OsBuild);
            foreach (DiskInfo d in Disks) sb.AppendLine("Disk: " + d.Model + " " + d.Kind + " " + d.SizeGB.ToString("0") + " GB");
            sb.AppendLine("HAGS: " + Hags + "  AV: " + string.Join(",", Antivirus.ToArray()));
            foreach (Finding f in Findings) sb.AppendLine("[" + f.Level + "] " + f.Title + " - " + f.Detail);
            return sb.ToString();
        }
    }
}
