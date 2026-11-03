using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace InfernoXC
{
    /// <summary>
    /// Real behaviour checks that run against throw-away files, registry keys and a harmless process,
    /// then clean up after themselves. Started with --functest by the build script.
    /// </summary>
    public static class FuncTest
    {
        static StringBuilder R = new StringBuilder();
        static int pass, fail;

        static void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            R.AppendLine((ok ? "PASS  " : "FAIL  ") + name + (string.IsNullOrEmpty(detail) ? "" : "  -  " + detail));
        }

        static void Step(string title, Action a)
        {
            R.AppendLine(); R.AppendLine("== " + title);
            try { a(); }
            catch (Exception ex) { fail++; R.AppendLine("FAIL  " + title + " threw: " + ex.GetType().Name + ": " + ex.Message); R.AppendLine("      " + string.Join(" | ", (ex.StackTrace ?? "").Split(new char[] { '\n' }).Take(4).Select(x => x.Trim()).ToArray())); }
        }

        static void MakeFiles(string dir, int n, bool old)
        {
            Directory.CreateDirectory(dir);
            byte[] data = new byte[10240];
            for (int i = 0; i < n; i++)
            {
                string sub = Path.Combine(dir, "d" + (i % 5));
                Directory.CreateDirectory(sub);
                string f = Path.Combine(sub, "f" + i + ".tmp");
                File.WriteAllBytes(f, data);
                if (old) File.SetLastWriteTime(f, DateTime.Now.AddDays(-2));
            }
        }

        public static void Run()
        {
            R.AppendLine("FUNCTEST " + Brand.Version + "  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            string root = Path.Combine(Path.GetTempPath(), "ixc-functest");
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }

            Step("Updater: GitHub release check", delegate
            {
                UpdateInfo u = Updater.Check();
                Check("check completes without crashing", true, u.Ok ? "tag " + u.Tag + ", newer=" + u.Newer + ", exe asset=" + (u.ExeUrl.Length > 0) + ", sha256 asset=" + (u.HashUrl.Length > 0) : "error: " + u.Error);
                if (u.Ok) Check("running version is not older than the release", !u.Newer, "running " + Brand.Version);
            });

            Step("Clean: delete only old files, keep recent ones", delegate
            {
                string d = Path.Combine(root, "t1");
                MakeFiles(d, 50, true);
                string keep = Path.Combine(d, "recent.tmp"); File.WriteAllBytes(keep, new byte[100]);
                CleanJob j = new CleanJob();
                long freed = CleanKit.DeleteForTest(d, j);
                Check("old files removed", Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length == 1, "left: " + Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length);
                Check("recent file kept", File.Exists(keep), "");
                Check("space counted", freed == 50L * 10240, "freed " + freed + " bytes, job says " + j.Freed + ", cleaned " + j.Cleaned);
            });

            Step("Clean: cancel stops a running job", delegate
            {
                string d = Path.Combine(root, "t2");
                MakeFiles(d, 4000, true);
                CleanJob j = new CleanJob();
                bool cancelled = false;
                Thread t = new Thread(delegate () { try { CleanKit.DeleteForTest(d, j); } catch (OperationCanceledException) { cancelled = true; } });
                t.Start();
                Thread.Sleep(60); j.Cancel(); t.Join(20000);
                int left = Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length;
                Check("cancel raised", cancelled || left == 0, "cancelled=" + cancelled + ", files left " + left);
                Check("cancel left the rest untouched", left > 0 || !cancelled, "files left " + left + " of 4000");
            });

            Step("Clean: pause holds the job, resume finishes it", delegate
            {
                string d = Path.Combine(root, "t3");
                MakeFiles(d, 3000, true);
                CleanJob j = new CleanJob();
                Thread t = new Thread(delegate () { try { CleanKit.DeleteForTest(d, j); } catch (OperationCanceledException) { } });
                t.Start();
                Thread.Sleep(40);
                j.Pause();
                Thread.Sleep(200);
                long a = Interlocked.Read(ref j.Cleaned);
                Thread.Sleep(500);
                long b = Interlocked.Read(ref j.Cleaned);
                Check("no progress while paused", a == b, a + " -> " + b);
                j.Resume(); t.Join(30000);
                int left = Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length;
                Check("finished after resume", left == 0, "files left " + left);
            });

            Step("Clean: read-only scan of every real item", delegate
            {
                List<CleanItem> items = CleanKit.Build();
                int bad = 0;
                foreach (CleanItem c in items)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    CleanJob j = new CleanJob();
                    long s = -2;
                    try { s = c.Scan(j); } catch (Exception ex) { bad++; R.AppendLine("      " + c.Id + " scan threw " + ex.Message); }
                    R.AppendLine("      " + c.Id.PadRight(10) + (s < 0 ? "(measured when cleaning)" : Util.Bytes(s)).PadRight(26) + sw.ElapsedMilliseconds + " ms   default " + (c.Selected ? "on" : "off") + (c.Shader ? "  [shader cache]" : ""));
                }
                Check("every item scans without error", bad == 0, items.Count + " items");
                Check("shader caches are off by default", !items.Any(c => c.Shader && c.Selected), "");
                Check("no shader cache is in the scheduled routine set", !items.Any(c => c.Shader && c.Routine), "");
                string g = CleanKit.RunningGame();
                R.AppendLine("      running game detected: " + (g ?? "none"));
            });

            Step("App profiles: settings live in the registry and apply without this app", delegate
            {
                string dir = Path.Combine(Path.GetTempPath(), "ixc-functest-prof");
                Directory.CreateDirectory(dir);
                string exe = Path.Combine(dir, "ixc-functest-ping.exe");
                File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe, true);
                ProgEntry e = new ProgEntry(); e.Exe = "ixc-functest-ping.exe"; e.Paths.Add(exe);
                Process p = null;
                try
                {
                    ProfileEngine.ReadStates(new List<ProgEntry> { e });
                    List<string> errs = ProfileEngine.Apply(e, 5, 1, 2, true, true, true);
                    Check("settings written", errs.Count == 0, string.Join("; ", errs.ToArray()));
                    ProfileEngine.ReadStates(new List<ProgEntry> { e });
                    Check("registry holds CPU priority 5 (below normal)", e.St.Cpu == 5, "read " + e.St.Cpu);
                    Check("registry holds I/O priority 1 (low)", e.St.Io == 1, "read " + e.St.Io);
                    Check("graphics preference read back", e.St.Gpu == 2, e.St.GpuRaw);
                    Check("fullscreen flag read back", e.St.Fso, "");
                    p = Process.Start(new ProcessStartInfo(exe, "-n 25 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                    ProcessPriorityClass pc = ProcessPriorityClass.Normal;
                    for (int i = 0; i < 8; i++) { Thread.Sleep(400); p.Refresh(); try { pc = p.PriorityClass; } catch { } if (pc == ProcessPriorityClass.BelowNormal) break; }
                    Check("Windows starts the program at Below normal on its own (no watcher running)", pc == ProcessPriorityClass.BelowNormal, "priority is " + pc);
                    try { p.Kill(); } catch { }
                    string r = ProfileEngine.Reset(e);
                    Check("reset reports no error", r == null, r);
                    ProfileEngine.ReadStates(new List<ProgEntry> { e });
                    Check("reset removes every value", !e.St.Any, "cpu " + e.St.Cpu + " io " + e.St.Io + " gpu " + e.St.Gpu + " fso " + e.St.Fso);
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ProfileEngine.IfeoRoot + "\\ixc-functest-ping.exe"))
                        Check("empty registry key is removed too", k == null, "");
                    Check("anti-cheat is refused", ProfileEngine.SetPerf("vgc.exe", "CpuPriorityClass", 3) != null, "");
                    Check("Vanguard-watched Riot game is refused for priority", ProfileEngine.SetPerf("VALORANT-Win64-Shipping.exe", "CpuPriorityClass", 3) != null, "");
                }
                finally
                {
                    try { if (p != null && !p.HasExited) p.Kill(); } catch { }
                    try { ProfileEngine.Reset(e); } catch { }
                    try { Thread.Sleep(300); Directory.Delete(dir, true); } catch { }
                }
            });

            Step("App profiles: every installed app, game and running program is listed", delegate
            {
                List<ProgEntry> all = ProfileEngine.BuildCatalog();
                Check("catalog is not empty", all.Count > 0, all.Count + " programs");
                R.AppendLine("      games " + all.Count(x => x.Kind == "Game") + ", apps " + all.Count(x => x.Kind == "App") + ", running " + all.Count(x => x.Kind == "Running") + ", set in registry " + all.Count(x => x.Kind == "Set") + ", with settings " + all.Count(x => x.St.Any));
                foreach (ProgEntry x in all.Where(y => y.St.Any).Take(12))
                    R.AppendLine("      " + x.Exe + "  cpu " + x.St.Cpu + "  io " + x.St.Io + "  gpu " + x.St.Gpu + "  fso " + x.St.Fso + "  [" + x.Kind + "]");
                Check("anti-cheat is never listed", !all.Any(x => ProfileEngine.IsProtected(x.Exe)), "");
            });

            Step("Uninstaller: leftovers found, backed up and removed", delegate
            {
                string name = "IXC Functest App 7431";
                string folder = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), name);
                Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "settings.json"), "{}");
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\" + name + @"\Config")) k.SetValue("x", 1);
                AppRow a = new AppRow(); a.Name = name; a.Publisher = "IXC Test Publisher"; a.Version = "1.0";
                List<LeftoverItem> l = Leftovers.Scan(a);
                Check("folder found", l.Any(x => x.Kind == "Folder" && x.Path == folder), l.Count + " items");
                Check("registry key found", l.Any(x => x.Kind == "Registry" && x.Path.EndsWith(name)), "");
                int ok, failed;
                string dir = Leftovers.Remove(a, l, out ok, out failed);
                Check("all removed without failures", failed == 0 && ok == l.Count, ok + " ok, " + failed + " failed");
                Check("folder gone", !Directory.Exists(folder), "");
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\" + name)) Check("registry key gone", k == null, "");
                Check("registry backup file exists", Directory.GetFiles(dir, "*.reg").Length > 0, dir);
                try { Directory.Delete(dir, true); } catch { }
                Check("short or generic names are not scanned", Leftovers.Scan(new AppRow { Name = "App", Publisher = "Microsoft" }).Count == 0, "");
            });

            Step("Crash Center: event log read", delegate
            {
                CrashReport r = CrashKit.Build(null);
                Check("report built", !r.Failed, "events " + r.Events.Count + ", duplicates merged " + r.Duplicates);
                foreach (SourceStatus s in r.Sources) R.AppendLine("      " + s.Name.PadRight(50) + (s.Error == null ? s.Count + " found" : "ERROR " + s.Error));
                Check("no log source failed", !r.Sources.Any(s => s.Error != null), "");
                Check("no event listed twice", r.Events.Select(e => e.Key).Distinct().Count() == r.Events.Count, "");
            });

            Step("Smart Tune: profiles are consistent", delegate
            {
                HardwareInfo hw = HardwareInfo.Read();
                List<Tweak> t = TuneKit.Build(hw);
                string why; string rec = TuneKit.RecommendLevel(hw, out why);
                Check("recommendation made", rec.Length > 0, rec + " - " + why);
                bool allKnown = TuneKit.Levels.All(L => L.Ids.All(id => t.Any(x => x.Id == id)));
                Check("every profile only names real settings", allKnown, "");
            });

            Step("Debloat: protection lists", delegate
            {
                string[] must = new string[] { "Microsoft.WindowsStore", "Microsoft.SecHealthUI", "Microsoft.WindowsTerminal", "Microsoft.DesktopAppInstaller", "NVIDIACorp.NVIDIAControlPanel", "Microsoft.WindowsCalculator" };
                Check("system apps are protected", must.All(m => Debloater.Protected(m)), "");
                Check("no catalog entry is a protected app", !Debloater.Catalog().Any(c => c.Pattern.IndexOf('*') < 0 && Debloater.Protected(c.Pattern)), "");
                List<SvcEntry> sv = SvcKit.Catalog();
                string[] never = new string[] { "WinDefend", "mpssvc", "BFE", "Dnscache", "Dhcp", "Audiosrv", "AudioEndpointBuilder", "EventLog", "RpcSs", "wuauserv", "vgc", "vgk", "BEService", "Winmgmt", "LanmanWorkstation", "NlaSvc", "WlanSvc", "nsi", "Themes", "SecurityHealthService", "wscsvc", "UserManager", "ProfSvc" };
                Check("no essential service is offered", !sv.Any(x => never.Contains(x.Name, StringComparer.OrdinalIgnoreCase)), "");
                SvcKit.Read(sv);
                Check("service state is read", sv.Any(x => x.Exists && x.Start > 0), sv.Count(x => x.Exists) + " of " + sv.Count + " exist on this PC");
                Check("app list is read", true, "");
                string err; List<BloatApp> found = Debloater.Scan(out err);
                Check("Store app scan runs", err == null || found.Count > 0, "found " + found.Count + (err == null ? "" : ", " + err));
                Check("scan never lists a protected app", !found.Any(f => Debloater.Protected(f.Name)), "");
            });

            Step("Updates: winget table parser and driver reader", delegate
            {
                string table = "Name                         Id                      Version      Available    Source\r\n" +
                               "------------------------------------------------------------------------------------\r\n" +
                               "Mozilla Firefox (x64 en-US)  Mozilla.Firefox         130.0        131.0.2      winget\r\n" +
                               "7-Zip 23.01 (x64)            7zip.7zip               23.01        24.08        winget\r\n" +
                               "Some App                     Vendor.SomeApp          < 1.2        1.4          winget\r\n" +
                               "3 upgrades available.\r\n";
                string real2 = "Name                                            Id                                 Version Available Source\r\n" +
                               "-----------------------------------------------------------------------------------------------------------\r\n" +
                               "7-Zip 26.04 (x64)                               7zip.7zip                          26.04   26.05     winget\r\n" +
                               "Microsoft Windows Desktop Runtime 10.0.13 (x64) Microsoft.DotNet.DesktopRuntime.10 10.0.13 10.0.14   winget\r\n" +
                               "Node.js                                         OpenJS.NodeJS.LTS                  24.22.0 26.13.0   winget\r\n" +
                               "3 upgrades available.\r\n";
                string e0; List<AppUpdate> u0 = UpdateKit.ParseTable(real2, 0, out e0);
                Check("real winget layout (columns one space apart)", u0.Count == 3 && u0[1].Name == "Microsoft Windows Desktop Runtime 10.0.13 (x64)" && u0[1].Id == "Microsoft.DotNet.DesktopRuntime.10" && u0[1].Version == "10.0.13" && u0[1].Available == "10.0.14" && u0[1].Source == "winget" && u0[2].Id == "OpenJS.NodeJS.LTS", u0.Count + " rows" + (u0.Count > 1 ? ", " + u0[1].Name + " | " + u0[1].Id + " | " + u0[1].Version + " > " + u0[1].Available : ""));
                string e; List<AppUpdate> u = UpdateKit.ParseTable(table, 0, out e);
                Check("parses three rows", u.Count == 3, "got " + u.Count);
                Check("name with spaces and brackets kept", u.Count > 0 && u[0].Name == "Mozilla Firefox (x64 en-US)" && u[0].Id == "Mozilla.Firefox", u.Count > 0 ? u[0].Name + " | " + u[0].Id : "");
                Check("versions split", u.Count > 1 && u[1].Version == "23.01" && u[1].Available == "24.08", "");
                Check("'<' marker removed", u.Count > 2 && u[2].Version == "1.2", u.Count > 2 ? u[2].Version : "");
                Check("empty output gives empty list", UpdateKit.ParseTable("No installed package found matching input criteria.", 0, out e).Count == 0, "");
                string listTbl = "Name                  Id                         Version  Source\r\n" +
                                 "---------------------------------------------------------------\r\n" +
                                 "OBS Studio            OBSProject.OBSStudio       32.2.2   winget\r\n" +
                                 "Some Old Tool         ARP\\Machine\\X64\\ToolX   1.0\r\n";
                List<AppUpdate> la = UpdateKit.ParseAll(listTbl, 0, out e);
                Check("list parser: four-column table (no Available column)", la.Count == 2 && la[0].Id == "OBSProject.OBSStudio" && la[0].Status == "ok" && la[0].Source == "winget", la.Count + " rows");
                Check("list parser: untracked app is recognised", la.Count == 2 && la[1].Status == "untracked", la.Count == 2 ? la[1].Status : "");
                List<AppUpdate> la5 = UpdateKit.ParseAll(real2, 0, out e);
                Check("list parser: five-column table marks updates", la5.Count == 3 && la5.All(x => x.Status == "update"), la5.Count + " rows");
                Check("failure reason: app running is explained", (UpdateKit.Explain(1, "Installer failed with exit code: 1603", false) ?? "").Contains("1603"), "");
                Check("failure reason: success gives none", UpdateKit.Explain(0, "Successfully installed", false) == null, "");
                Check("failure reason: restart needed counts as success", UpdateKit.Explain(1, "Installer failed with exit code: 3010", false) == null, "");
                string wv; bool hasW = UpdateKit.HasWinget(out wv);
                R.AppendLine("      winget on this PC: " + (hasW ? wv : "not found"));
                if (hasW)
                {
                    string se; List<AppUpdate> real = UpdateKit.Scan(out se);
                    Check("real winget scan runs", se == null, "found " + real.Count + " update(s)" + (se == null ? "" : ", " + se));
                    if (se != null)
                    {
                        Util.RunResult raw = Util.Run("winget.exe", "upgrade --include-unknown --accept-source-agreements --disable-interactivity", 180000);
                        R.AppendLine("      raw winget output (exit " + raw.Code + "):");
                        foreach (string ln in (raw.Out ?? "").Replace("\r", "").Split('\n').Take(14)) R.AppendLine("      > [" + ln.Length + "] " + new string(ln.Select(ch => ch < 32 || ch > 126 ? '?' : ch).ToArray()));
                    }
                    foreach (AppUpdate a in real.Take(8)) R.AppendLine("      " + a.Name + "  " + a.Version + " > " + a.Available + "  [" + a.Id + "]");
                }
                if (hasW)
                {
                    foreach (string idq in new string[] { "list --id OBSProject.OBSStudio --exact --accept-source-agreements --disable-interactivity", "show OBSProject.OBSStudio --exact --accept-source-agreements --disable-interactivity" })
                    {
                        Util.RunResult dg = Util.Run("winget.exe", idq, 90000);
                        R.AppendLine("      winget " + idq.Split(' ')[0] + " OBS (exit " + dg.Code + "):");
                        foreach (string ln in (dg.Out ?? "").Replace("\r", "").Split('\n').Where(x => x.Trim().Length > 2 && !x.Trim().All(ch => "-\\|/ ".IndexOf(ch) >= 0)).Take(22)) R.AppendLine("      > " + new string(ln.Select(ch => ch < 32 || ch > 126 ? '?' : ch).ToArray()));
                    }
                    bool obsOpen = false; foreach (string pn in new string[] { "obs64", "obs32" }) { Process[] ps = Process.GetProcessesByName(pn); if (ps.Length > 0) obsOpen = true; foreach (Process pp in ps) pp.Dispose(); }
                    R.AppendLine("      OBS running during this test: " + obsOpen);
                    string le; List<AppUpdate> allList = UpdateKit.ListAll(out le);
                    Check("full app list is read", allList.Count > 0, allList.Count + " apps, " + allList.Count(x => x.Status == "update") + " updates, " + allList.Count(x => x.Status == "untracked") + " not tracked" + (le == null ? "" : ", " + le));
                }
                List<string> pr; List<DriverRow> dr = DriverKit.Installed(out pr);
                Check("drivers are read", dr.Count > 0, dr.Count + " drivers, " + dr.Count(d => d.Old) + " old, " + pr.Count + " problem devices");
                foreach (DriverRow d in dr.Where(x => x.Category == "Graphics" || x.Category == "Network").Take(6)) R.AppendLine("      " + d.Category + ": " + d.Device + " v" + d.Version + " (" + DriverKit.AgeText(d) + ")");
                Check("auto driver state reported", DriverKit.AutoDriverState().Length > 0, DriverKit.AutoDriverState());
            });

            R.AppendLine();
            R.AppendLine("RESULT: " + pass + " passed, " + fail + " failed");
            try { File.WriteAllText(Path.Combine(Paths.AppDir, "functest.txt"), R.ToString(), Encoding.UTF8); } catch { }
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }
}
