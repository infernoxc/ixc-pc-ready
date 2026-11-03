using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace InfernoXC
{
    public class CrashGroup
    {
        public string App, Code, Module;
        public int Count;
        public DateTime Last;
        public string Meaning, ModuleHint;
    }

    public class CrashEvent
    {
        public string Key, Category, Source, Title, Detail;
        public DateTime Time;
        public int EventId;
    }

    public class SourceStatus
    {
        public string Name; public int Count; public string Error;
    }

    public class CrashReport
    {
        public List<CrashEvent> Events = new List<CrashEvent>();
        public List<SourceStatus> Sources = new List<SourceStatus>();
        public int Duplicates;
        public int Days = 30;
        public List<CrashGroup> Apps = new List<CrashGroup>();
        public int Hangs, Kernel41, Unexpected6008, BugChecks, GpuResets, Whea, DiskErrors;
        public List<string> BugCheckCodes = new List<string>();
        public List<string> Overlays = new List<string>();
        public List<KeyValuePair<string, string>> Causes = new List<KeyValuePair<string, string>>(); // title, detail
        public int Minidumps;
        public bool Failed;
    }

    public static class CrashKit
    {
        const string Window = "TimeCreated[timediff(@SystemTime) <= 2592000000]";

        static readonly Dictionary<string, string> Codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "c0000374", "Heap corruption: something wrote into memory it did not own. Usual causes: unstable or mismatched RAM, an overlay or injected DLL, or damaged game files." },
            { "c0000005", "Access violation: the program touched memory it should not. Often a bad overlay or driver, a damaged install, or unstable RAM / overclock." },
            { "c0000409", "Fast-fail / stack buffer overrun: the program detected corruption in itself and shut down. Same family of causes as heap corruption." },
            { "c0000142", "A DLL failed to start. Usually a broken runtime, security software or an overlay getting in the way." },
            { "c0000135", "A required DLL is missing. Reinstall the app or its Visual C++ / .NET runtime." },
            { "c000001d", "Illegal CPU instruction. Can mean an unstable overclock / undervolt or a corrupted file." },
            { "c0000006", "In-page error: Windows could not read data from storage. Check the SSD health and cables / drivers." },
            { "c00000fd", "Stack overflow inside the program. That is the app's own bug." },
            { "c0000417", "Invalid parameter handler. That is usually an app bug." },
            { "e06d7363", "Unhandled C++ exception. The app's own bug or missing files." },
            { "80000003", "Breakpoint hit. Common with anti-cheat or debugging tools." },
            { "c0000094", "Integer divide by zero in the program." }
        };

        static readonly string[,] ModHints = new string[,]
        {
            { "atidxx64", "AMD graphics driver" }, { "amdxx64", "AMD graphics driver" }, { "aticfx", "AMD graphics driver" }, { "amdvlk", "AMD Vulkan driver" }, { "atiumd", "AMD graphics driver" },
            { "nvwgf2um", "NVIDIA graphics driver" }, { "nvlddmkm", "NVIDIA graphics driver" }, { "nvd3dum", "NVIDIA graphics driver" },
            { "igd10um", "Intel graphics driver" }, { "ig9icd", "Intel graphics driver" },
            { "d3d11", "DirectX runtime (usually the graphics driver)" }, { "d3d12", "DirectX runtime (usually the graphics driver)" }, { "dxgi", "DirectX runtime (usually the graphics driver)" },
            { "ntdll", "Windows core library - points to memory corruption" }, { "kernelbase", "Windows core library" }, { "ucrtbase", "C runtime" }, { "vcruntime", "Visual C++ runtime" }, { "msvcp", "Visual C++ runtime" },
            { "beclient", "BattlEye anti-cheat" }, { "battleye", "BattlEye anti-cheat" }, { "easyanticheat", "Easy Anti-Cheat" }, { "vgc", "Riot Vanguard" },
            { "gameoverlayrenderer", "Steam overlay" }, { "discordhook", "Discord overlay" }, { "rtss", "RivaTuner / MSI Afterburner overlay" }, { "nahimic", "Nahimic audio software" },
            { "unityplayer", "Unity engine" }, { "mono", "Unity / Mono runtime" }
        };

        static readonly string[] OverlayProcs = new string[] { "RTSS", "RTSSHooksLoader64", "MSIAfterburner", "Overwolf", "OverwolfBrowser", "Discord", "GameBar", "GameBarFTServer", "Medal", "NVIDIA Share", "nvsphelper64", "Nahimic3", "NahimicService", "SonicStudio3", "aida64" };

        static List<EventData> Query(string log, string xpath, int max) { return Query(null, log, xpath, max, log); }

        static List<EventData> Query(CrashReport rep, string log, string xpath, int max, string label)
        {
            List<EventData> l = new List<EventData>();
            SourceStatus st = new SourceStatus(); st.Name = label;
            if (rep != null) rep.Sources.Add(st);
            try
            {
                EventLogQuery q = new EventLogQuery(log, PathType.LogName, xpath);
                q.ReverseDirection = true;
                using (EventLogReader r = new EventLogReader(q))
                {
                    EventRecord e;
                    while ((e = r.ReadEvent()) != null && l.Count < max)
                    {
                        try
                        {
                            EventData d = new EventData();
                            d.Log = log; d.Record = e.RecordId ?? 0;
                            d.Id = e.Id; d.Time = e.TimeCreated ?? DateTime.MinValue; d.Provider = e.ProviderName;
                            foreach (EventProperty p in e.Properties) d.Props.Add(p.Value == null ? "" : p.Value.ToString());
                            try { d.Text = e.FormatDescription(); } catch { }
                            l.Add(d);
                        }
                        catch { }
                        finally { e.Dispose(); }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Err("event query " + label, ex);
                st.Error = ex is UnauthorizedAccessException ? "No permission. Run the app as administrator." : ex.Message;
            }
            st.Count = l.Count;
            return l;
        }

        class EventData
        {
            public int Id; public long Record; public string Log; public DateTime Time; public string Provider, Text;
            public List<string> Props = new List<string>();
        }

        public static CrashReport Build(HardwareInfo hw)
        {
            CrashReport rep = new CrashReport();
            try
            {
                // application crashes
                List<EventData> crashes = Query(rep, "Application", "*[System[Provider[@Name='Application Error'] and (EventID=1000) and " + Window + "]]", 800, "Application crashes (event 1000)");
                Dictionary<string, CrashGroup> g = new Dictionary<string, CrashGroup>(StringComparer.OrdinalIgnoreCase);
                foreach (EventData e in crashes)
                {
                    if (e.Props.Count < 8) continue;
                    AddEvent(rep, e, "App crash", e.Props[0], "Crashed in " + e.Props[3] + ", exception " + e.Props[6]);
                    string app = e.Props[0], mod = e.Props[3], code = (e.Props[6] ?? "").Replace("0x", "").ToLowerInvariant();
                    string key = app + "|" + code + "|" + mod;
                    CrashGroup cg;
                    if (!g.TryGetValue(key, out cg))
                    {
                        cg = new CrashGroup(); cg.App = app; cg.Code = code; cg.Module = mod; cg.Last = e.Time;
                        string m; if (Codes.TryGetValue(code, out m)) cg.Meaning = m; else cg.Meaning = "Exception code " + code + ".";
                        cg.ModuleHint = HintFor(mod);
                        g[key] = cg;
                    }
                    cg.Count++;
                    if (e.Time > cg.Last) cg.Last = e.Time;
                }
                rep.Apps = g.Values.OrderByDescending(x => x.Count).ThenByDescending(x => x.Last).ToList();

                List<EventData> hangs = Query(rep, "Application", "*[System[Provider[@Name='Application Hang'] and (EventID=1002) and " + Window + "]]", 500, "Application hangs (event 1002)");
                foreach (EventData e in hangs) AddEvent(rep, e, "Hang", e.Props.Count > 0 ? e.Props[0] : "Application", "The program stopped responding and was closed or waited on.");
                rep.Hangs = hangs.Count;

                // GPU hardware resets reported through Windows Error Reporting
                foreach (EventData e in Query(rep, "Application", "*[System[Provider[@Name='Windows Error Reporting'] and (EventID=1001) and " + Window + "]]", 400, "Windows Error Reporting (event 1001)"))
                {
                    string t = (e.Text ?? "") + " " + string.Join(" ", e.Props.ToArray());
                    if (t.IndexOf("LiveKernelEvent", StringComparison.OrdinalIgnoreCase) >= 0 && Regex.IsMatch(t, @"\b(141|117|1a8|193)\b", RegexOptions.IgnoreCase)) { rep.GpuResets++; AddEvent(rep, e, "GPU reset", "Graphics driver reset", "Windows recovered the graphics driver after it stopped responding (LiveKernelEvent)."); }
                }

                // system log
                foreach (EventData e in Query(rep, "System", "*[System[(EventID=41 or EventID=1001 or EventID=6008 or EventID=4101) and " + Window + "]]", 600, "System: shutdowns, blue screens, display resets"))
                {
                    if (e.Id == 41 && (e.Provider ?? "").IndexOf("Kernel-Power", StringComparison.OrdinalIgnoreCase) >= 0) { rep.Kernel41++; AddEvent(rep, e, "Shutdown", "Unexpected power loss or restart", "Kernel-Power 41: the PC restarted without shutting down cleanly."); }
                    else if (e.Id == 6008) { rep.Unexpected6008++; AddEvent(rep, e, "Shutdown", "Previous shutdown was unexpected", e.Text); }
                    else if (e.Id == 1001 && (e.Provider ?? "").IndexOf("WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        rep.BugChecks++;
                        AddEvent(rep, e, "Blue screen", "Windows restarted after a blue screen", e.Text);
                        Match m = Regex.Match((e.Text ?? "") + " " + (e.Props.Count > 0 ? e.Props[0] : ""), @"0x[0-9a-fA-F]{8}");
                        if (m.Success && !rep.BugCheckCodes.Contains(m.Value.ToLowerInvariant())) rep.BugCheckCodes.Add(m.Value.ToLowerInvariant());
                    }
                    else if (e.Id == 4101) { rep.GpuResets++; AddEvent(rep, e, "GPU reset", "Display driver stopped responding and recovered", e.Text); }
                }
                List<EventData> wh = Query(rep, "System", "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and " + Window + "]]", 200, "Hardware errors (WHEA)");
                foreach (EventData e in wh) AddEvent(rep, e, "Hardware", "Hardware error reported by the CPU or bus", e.Text);
                rep.Whea = wh.Count;
                List<EventData> dk = Query(rep, "System", "*[System[Provider[@Name='disk'] and (EventID=7 or EventID=11 or EventID=51 or EventID=153) and " + Window + "]]", 200, "Disk errors");
                foreach (EventData e in dk) AddEvent(rep, e, "Disk", "Disk error (event " + e.Id + ")", e.Text);
                rep.DiskErrors = dk.Count;
                rep.Events = rep.Events.OrderByDescending(x => x.Time).ToList();

                try
                {
                    string md = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "Minidump");
                    if (Directory.Exists(md)) rep.Minidumps = Directory.GetFiles(md, "*.dmp").Length;
                }
                catch { }

                foreach (string o in OverlayProcs)
                    if (Process.GetProcessesByName(o).Length > 0) rep.Overlays.Add(o);

                BuildCauses(rep, hw);
            }
            catch (Exception ex) { Log.Err(ex); rep.Failed = true; }
            return rep;
        }

        /// <summary>One entry per Windows event record. Records seen twice are counted as duplicates, not listed again.</summary>
        static void AddEvent(CrashReport rep, EventData e, string cat, string title, string detail)
        {
            string key = e.Log + "|" + e.Record + "|" + e.Id + "|" + e.Time.Ticks;
            foreach (CrashEvent x in rep.Events) { if (x.Key == key) { rep.Duplicates++; return; } }
            CrashEvent c = new CrashEvent();
            c.Key = key; c.Category = cat; c.Source = e.Provider ?? e.Log; c.Title = title; c.EventId = e.Id; c.Time = e.Time;
            string t = (detail ?? "").Trim();
            if (t.Length > 1500) t = t.Substring(0, 1500) + "...";
            c.Detail = t;
            rep.Events.Add(c);
        }

        public static string EventsCsv(IEnumerable<CrashEvent> list)
        {
            StringBuilder sb = new StringBuilder("Time,Category,Source,EventId,Title,Detail\r\n");
            foreach (CrashEvent c in list)
                sb.AppendLine(string.Join(",", new string[] { c.Time.ToString("yyyy-MM-dd HH:mm:ss"), Csv(c.Category), Csv(c.Source), c.EventId.ToString(), Csv(c.Title), Csv(c.Detail) }));
            return sb.ToString();
        }
        static string Csv(string s) { s = (s ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " "); return "\"" + s + "\""; }

        static string HintFor(string module)
        {
            string m = (module ?? "").ToLowerInvariant();
            for (int i = 0; i < ModHints.GetLength(0); i++) if (m.Contains(ModHints[i, 0])) return ModHints[i, 1];
            return "";
        }

        static void BuildCauses(CrashReport r, HardwareInfo hw)
        {
            int memoryish = r.Apps.Where(a => a.Code == "c0000374" || a.Code == "c0000409" || (a.Code == "c0000005" && (a.Module ?? "").ToLowerInvariant().Contains("ntdll"))).Sum(a => a.Count);
            bool ramFinding = hw != null && hw.Findings.Any(f => f.Level >= 2 && (f.Title.IndexOf("RAM", StringComparison.OrdinalIgnoreCase) >= 0 || f.Title.IndexOf("memory", StringComparison.OrdinalIgnoreCase) >= 0));
            int gpuish = r.Apps.Where(a => (a.ModuleHint ?? "").IndexOf("graphics", StringComparison.OrdinalIgnoreCase) >= 0 || (a.ModuleHint ?? "").IndexOf("DirectX", StringComparison.OrdinalIgnoreCase) >= 0 || (a.ModuleHint ?? "").IndexOf("Vulkan", StringComparison.OrdinalIgnoreCase) >= 0).Sum(a => a.Count) + r.GpuResets;
            int overlayish = r.Apps.Where(a => (a.ModuleHint ?? "").IndexOf("overlay", StringComparison.OrdinalIgnoreCase) >= 0 || (a.ModuleHint ?? "").IndexOf("RivaTuner", StringComparison.OrdinalIgnoreCase) >= 0 || (a.ModuleHint ?? "").IndexOf("Nahimic", StringComparison.OrdinalIgnoreCase) >= 0).Sum(a => a.Count);
            int antiCheat = r.Apps.Where(a => (a.ModuleHint ?? "").IndexOf("anti-cheat", StringComparison.OrdinalIgnoreCase) >= 0 || (a.ModuleHint ?? "").IndexOf("Vanguard", StringComparison.OrdinalIgnoreCase) >= 0).Sum(a => a.Count);

            if (memoryish >= 2 || (memoryish >= 1 && ramFinding))
            {
                string d = memoryish + " crash" + (memoryish == 1 ? "" : "es") + " point to corrupted memory (error c0000374 / c0000409 / ntdll). ";
                if (ramFinding) d += "Your RAM also has a flagged problem (see the list below), which makes this the most likely cause. ";
                d += "Try: 1) run MemTest86 or Windows Memory Diagnostic for a few passes, 2) in the BIOS lower the RAM speed one step or test one stick at a time, 3) remove overlays (next item), 4) verify the game's files.";
                r.Causes.Add(new KeyValuePair<string, string>("Memory instability" + (ramFinding ? " (RAM mismatch found)" : ""), d));
            }
            if (r.Overlays.Count > 0 && (memoryish > 0 || overlayish > 0 || r.Apps.Count > 0))
                r.Causes.Add(new KeyValuePair<string, string>("Overlay or hook software is running", string.Join(", ", r.Overlays.ToArray()) + " inject code into games. They are a known trigger for heap-corruption crashes. Turn off in-game overlays (RTSS, Discord, GeForce / Radeon overlay) and test again."));
            if (gpuish > 0)
                r.Causes.Add(new KeyValuePair<string, string>("Graphics driver or GPU resets", gpuish + " event" + (gpuish == 1 ? "" : "s") + " involve the graphics driver (driver timeouts or crashes inside driver files). Do a clean driver install, remove any GPU undervolt / overclock, and check GPU temperature and power cables."));
            if (r.Kernel41 > 0 || r.Unexpected6008 > 0 || r.BugChecks > 0)
            {
                string d = (r.Kernel41 + r.Unexpected6008) + " unexpected shutdown" + (r.Kernel41 + r.Unexpected6008 == 1 ? "" : "s") + (r.BugChecks > 0 ? " and " + r.BugChecks + " blue screen" + (r.BugChecks == 1 ? "" : "s") + (r.BugCheckCodes.Count > 0 ? " (" + string.Join(", ", r.BugCheckCodes.ToArray()) + ")" : "") : "") + ". ";
                d += "Usual causes: power supply or cable problems, overheating, an unstable overclock / RAM profile, or a driver fault. Check CPU and GPU temperatures under load and update the BIOS and chipset drivers.";
                r.Causes.Add(new KeyValuePair<string, string>("PC lost power or blue-screened", d));
            }
            if (r.Whea > 0)
                r.Causes.Add(new KeyValuePair<string, string>("Hardware errors reported by the CPU", r.Whea + " WHEA event" + (r.Whea == 1 ? "" : "s") + ". Windows records these when the CPU, memory controller or PCIe bus corrects or reports a hardware error. An unstable overclock, RAM profile or undervolt is the common reason."));
            if (r.DiskErrors > 0)
                r.Causes.Add(new KeyValuePair<string, string>("Storage errors", r.DiskErrors + " disk error event" + (r.DiskErrors == 1 ? "" : "s") + ". Check the SSD health (CrystalDiskInfo), update the NVMe driver / firmware and reseat the drive."));
            if (antiCheat > 0)
                r.Causes.Add(new KeyValuePair<string, string>("Anti-cheat involved", antiCheat + " crash" + (antiCheat == 1 ? "" : "es") + " mention anti-cheat modules. Repair or reinstall the anti-cheat from the game launcher and exclude the game folder from other security software."));
            if (r.Causes.Count == 0)
                r.Causes.Add(new KeyValuePair<string, string>(r.Apps.Count == 0 ? "No crashes in the last " + r.Days + " days" : "No clear pattern", r.Apps.Count == 0 ? "Nothing to fix here. Note: if you cleared the Windows event logs recently, older crashes are gone." : "The crashes below do not share a common cause. Look at the most frequent app first."));
        }

        public static string AsText(CrashReport r, HardwareInfo hw)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Brand.Name + " - crash report " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("Last " + r.Days + " days");
            sb.AppendLine();
            if (hw != null) { sb.AppendLine("PC"); sb.AppendLine(hw.Summary()); }
            sb.AppendLine("Totals: app crashes " + r.Apps.Sum(a => a.Count) + ", hangs " + r.Hangs + ", unexpected shutdowns " + (r.Kernel41 + r.Unexpected6008) + ", blue screens " + r.BugChecks + ", GPU resets " + r.GpuResets + ", hardware errors " + r.Whea + ", disk errors " + r.DiskErrors);
            sb.AppendLine();
            sb.AppendLine("Likely causes");
            foreach (KeyValuePair<string, string> c in r.Causes) sb.AppendLine("- " + c.Key + ": " + c.Value);
            sb.AppendLine();
            sb.AppendLine("Crashes by app");
            foreach (CrashGroup g in r.Apps.Take(40)) sb.AppendLine("- " + g.App + "  x" + g.Count + "  code " + g.Code + "  module " + g.Module + "  last " + g.Last.ToString("yyyy-MM-dd HH:mm") + "  " + g.Meaning + (string.IsNullOrEmpty(g.ModuleHint) ? "" : "  [" + g.ModuleHint + "]"));
            return sb.ToString();
        }
    }

    public class CrashPage : PageBase
    {
        HardwareInfo _hw;
        StackPanel _host;
        TextBlock _head, _sub;
        CrashReport _rep;
        bool _loading, _loaded;
        Button _save;
        StackPanel _evHost;
        TextBox _search, _detail;
        int _catIdx, _rangeIdx = 2;
        static readonly string[] Cats = new string[] { "All types", "App crash", "Hang", "Shutdown", "Blue screen", "GPU reset", "Hardware", "Disk" };
        static readonly string[] Ranges = new string[] { "Last 24 hours", "Last 7 days", "Last 30 days" };
        TextBlock _evCount;
        List<CrashEvent> _shown = new List<CrashEvent>();

        public CrashPage(HardwareInfo hw) : base("Crash Center", "Why your games and apps crash, and what is mismatched in this PC. Read from the last 30 days of Windows event logs.")
        {
            _hw = hw;
            Grid top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel left = new StackPanel();
            _head = UK.T("Reading event logs...", "Big");
            _sub = UK.T("This takes a few seconds.", "Muted");
            _sub.Margin = new Thickness(0, 4, 0, 0);
            left.Children.Add(_head); left.Children.Add(_sub);
            top.Children.Add(left);
            StackPanel btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            btns.Children.Add(UK.Btn("Scan again", false, delegate { Load(); }));
            _save = UK.Btn("Save report", true, delegate { Save(); });
            _save.Margin = new Thickness(0);
            _save.IsEnabled = false;
            btns.Children.Add(_save);
            Grid.SetColumn(btns, 1);
            top.Children.Add(btns);
            Body.Children.Add(UK.Card(top));

            _host = new StackPanel();
            Body.Children.Add(_host);
        }

        public override void Shown() { if (!_loaded) Load(); }

        void Load()
        {
            if (_loading) return;
            _loading = true;
            _head.Text = "Reading event logs...";
            UK.Bg(delegate { return CrashKit.Build(_hw); }, delegate(object o)
            {
                _loading = false; _loaded = true;
                _rep = o as CrashReport;
                Render();
            });
        }

        void Render()
        {
            _host.Children.Clear();
            if (_rep == null || _rep.Failed) { _head.Text = "Could not read the event logs."; _sub.Text = "Make sure the app is running as administrator."; return; }
            CrashReport r = _rep;
            int appCrashes = r.Apps.Sum(a => a.Count);
            _head.Text = appCrashes + " app crash" + (appCrashes == 1 ? "" : "es") + " in 30 days";
            _sub.Text = r.Hangs + " hangs  -  " + (r.Kernel41 + r.Unexpected6008) + " unexpected shutdowns  -  " + r.BugChecks + " blue screens  -  " + r.GpuResets + " GPU resets  -  " + r.Whea + " hardware errors";
            _save.IsEnabled = true;

            // likely causes
            StackPanel cs = new StackPanel();
            for (int i = 0; i < r.Causes.Count; i++)
            {
                cs.Children.Add(UK.Row(r.Causes[i].Key, r.Causes[i].Value, null, null, i == r.Causes.Count - 1));
            }
            _host.Children.Add(UK.Section("Likely causes", "Ranked from the evidence in your logs and your hardware.", cs, true, null));

            // mismatches
            StackPanel ms = new StackPanel();
            List<Finding> fl = _hw == null ? new List<Finding>() : _hw.Findings.Where(f => f.Level >= 1).OrderByDescending(f => f.Level).ToList();
            if (fl.Count == 0) ms.Children.Add(UK.T("Nothing looks mismatched. Your hardware reads as consistent.", "Muted"));
            foreach (Finding f in fl) ms.Children.Add(Ui.FindingRow(f));
            if (_hw != null && _hw.Ram.Count > 1)
            {
                StackPanel kit = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
                foreach (RamModule m in _hw.Ram)
                    kit.Children.Add(Ui.KeyValue(m.Slot, m.CapacityGB.ToString("0") + " GB  " + m.Part + "  " + m.Speed + " MT/s (running " + m.Configured + ")  " + m.Maker));
                ms.Children.Add(kit);
            }
            _host.Children.Add(UK.Section("What looks mismatched on this PC", fl.Count + " thing" + (fl.Count == 1 ? "" : "s") + " worth knowing", ms, true, null));

            // crashes by app
            StackPanel ca = new StackPanel();
            List<CrashGroup> top = r.Apps.Take(15).ToList();
            if (top.Count == 0) ca.Children.Add(UK.T("No application crashes were recorded in the last 30 days.", "Muted"));
            for (int i = 0; i < top.Count; i++)
            {
                CrashGroup g = top[i];
                string detail = g.Meaning + (string.IsNullOrEmpty(g.ModuleHint) ? "" : "  Module: " + g.Module + " (" + g.ModuleHint + ").") + "  Last: " + g.Last.ToString("MMM d, HH:mm");
                ca.Children.Add(UK.Row(g.App, detail, UK.Right(g.Count + "x   " + g.Code), null, i == top.Count - 1));
            }
            _host.Children.Add(UK.Section("Crashes by app", r.Apps.Count + " distinct problem" + (r.Apps.Count == 1 ? "" : "s"), ca, r.Apps.Count > 0, null));

            // overlays
            // event list
            StackPanel ev = new StackPanel();
            WrapPanel fb = new WrapPanel(); fb.Margin = new Thickness(0, 0, 0, 8);
            _search = new TextBox(); _search.Style = UK.S("Box"); _search.Width = 220; _search.Margin = new Thickness(0, 0, 8, 0);
            _search.ToolTip = "Search app, source or text";
            _search.TextChanged += delegate { FillEvents(); };
            Button catBtn = UK.Cycle("Type: ", Cats, _catIdx, delegate(int i) { _catIdx = i; FillEvents(); });
            Button rangeBtn = UK.Cycle("Range: ", Ranges, _rangeIdx, delegate(int i) { _rangeIdx = i; FillEvents(); });
            fb.Children.Add(_search); fb.Children.Add(catBtn); fb.Children.Add(rangeBtn);
            fb.Children.Add(UK.Btn("Copy list", false, delegate { CopyList(); }));
            fb.Children.Add(UK.Btn("Export CSV", false, delegate { ExportCsv(); }));
            ev.Children.Add(fb);
            _evCount = UK.T("", "Muted"); _evCount.Margin = new Thickness(0, 0, 0, 6);
            ev.Children.Add(_evCount);
            _evHost = new StackPanel();
            ev.Children.Add(_evHost);
            _detail = new TextBox(); _detail.Style = UK.S("Box"); _detail.IsReadOnly = true; _detail.TextWrapping = TextWrapping.Wrap; _detail.MinHeight = 60; _detail.MaxHeight = 200;
            _detail.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _detail.Margin = new Thickness(0, 8, 0, 0);
            _detail.Text = "Click an event to see its full details here.";
            ev.Children.Add(_detail);
            _host.Children.Add(UK.Section("Events", r.Events.Count + " recorded" + (r.Duplicates > 0 ? ", " + r.Duplicates + " duplicate record(s) merged" : ""), ev, false, null));
            FillEvents();

            // sources
            StackPanel src = new StackPanel();
            for (int i = 0; i < r.Sources.Count; i++)
            {
                SourceStatus ss = r.Sources[i];
                src.Children.Add(UK.Row(ss.Name, ss.Error == null ? "Read successfully." : "Could not read: " + ss.Error, UK.Right(ss.Error == null ? ss.Count + " found" : "Error"), null, i == r.Sources.Count - 1));
            }
            int bad = r.Sources.Count(x => x.Error != null);
            _host.Children.Add(UK.Section("Log sources", bad == 0 ? "All " + r.Sources.Count + " sources were read" : bad + " source(s) could not be read", src, bad > 0, null));

            if (r.Overlays.Count > 0)
            {
                StackPanel ov = new StackPanel();
                ov.Children.Add(UK.T("These programs hook into games and are running now: " + string.Join(", ", r.Overlays.ToArray()) + ". Close them and see if the crashes stop.", "Body"));
                _host.Children.Add(UK.Section("Overlay software running now", r.Overlays.Count + " found", ov, false, null));
            }
        }

        void FillEvents()
        {
            if (_rep == null || _evHost == null) return;
            string q = (_search.Text ?? "").Trim();
            string cat = _catIdx <= 0 ? null : Cats[_catIdx];
            DateTime from = DateTime.Now.AddDays(_rangeIdx == 0 ? -1 : _rangeIdx == 1 ? -7 : -30);
            _shown = _rep.Events.Where(e => e.Time >= from && (cat == null || e.Category == cat) &&
                (q.Length == 0 || (e.Title ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (e.Source ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (e.Detail ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            _evHost.Children.Clear();
            int max = 150;
            for (int i = 0; i < _shown.Count && i < max; i++)
            {
                CrashEvent c = _shown[i];
                string d = (c.Detail ?? "").Replace("\r", " ").Replace("\n", " ");
                if (d.Length > 160) d = d.Substring(0, 160) + "...";
                Border row = UK.Row(c.Title, c.Category + "  -  " + c.Source + (d.Length > 0 ? "\n" + d : ""), UK.Right(c.Time.ToString("MMM d HH:mm")), null, i == _shown.Count - 1 || i == max - 1);
                row.Cursor = System.Windows.Input.Cursors.Hand;
                CrashEvent cc = c;
                row.MouseLeftButtonUp += delegate { _detail.Text = cc.Time.ToString("yyyy-MM-dd HH:mm:ss") + "   " + cc.Category + "   event " + cc.EventId + "   " + cc.Source + "\r\n" + cc.Title + "\r\n\r\n" + cc.Detail; };
                _evHost.Children.Add(row);
            }
            _evCount.Text = _shown.Count == 0 ? "No events match." : _shown.Count > max ? "Showing the newest " + max + " of " + _shown.Count + ". Export to see all." : _shown.Count + " event(s).";
        }

        void CopyList()
        {
            try { Clipboard.SetText(CrashKit.EventsCsv(_shown)); _evCount.Text = _shown.Count + " event(s) copied to the clipboard."; }
            catch (Exception ex) { _evCount.Text = "Could not copy: " + ex.Message; }
        }

        void ExportCsv()
        {
            try
            {
                string f = Path.Combine(Paths.Sub("reports"), "crash-events-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv");
                File.WriteAllText(f, CrashKit.EventsCsv(_shown), Encoding.UTF8);
                _evCount.Text = "Exported " + _shown.Count + " event(s) to " + f;
                Process.Start("explorer.exe", "/select,\"" + f + "\"");
            }
            catch (Exception ex) { _evCount.Text = "Could not export: " + ex.Message; }
        }

        void Save()
        {
            if (_rep == null) return;
            try
            {
                string dir = Paths.Sub("reports");
                string f = Path.Combine(dir, "crash-report-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt");
                File.WriteAllText(f, CrashKit.AsText(_rep, _hw), Encoding.UTF8);
                try { string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); File.Copy(f, Path.Combine(desk, Path.GetFileName(f)), true); } catch { }
                _sub.Text = "Saved to your Desktop: " + Path.GetFileName(f);
                Process.Start("explorer.exe", "/select,\"" + f + "\"");
            }
            catch (Exception ex) { _sub.Text = "Could not save: " + ex.Message; }
        }
    }
}
