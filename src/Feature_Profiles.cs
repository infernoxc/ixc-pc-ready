using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;

namespace InfernoXC
{
    /// <summary>The settings Windows itself holds for one program, read straight from the registry.</summary>
    public class ProgState
    {
        public int Cpu = -1;            // IFEO CpuPriorityClass: 1 low, 2 normal, 3 high, 5 below normal, 6 above normal
        public int Io = -1;             // IFEO IoPriority: 0 very low, 1 low, 2 normal, 3 high
        public int Gpu = -1;            // UserGpuPreferences: 1 power saving, 2 high performance
        public string GpuRaw = "";
        public bool Fso;                // fullscreen optimizations switched off
        public bool FsoMachine;         // the same flag set for all users
        public bool Any { get { return Cpu >= 0 || Io >= 0 || Gpu >= 0 || Fso || FsoMachine; } }
    }

    /// <summary>One program found on the PC: installed app, game, running process or something already configured in the registry.</summary>
    public class ProgEntry
    {
        public string Exe = "";
        public List<string> Paths = new List<string>();
        public List<string> Owners = new List<string>();
        public string Kind = "App";         // Game | App | Running | Set
        public bool Running;
        public ProgState St = new ProgState();
        public string OwnerText { get { return Owners.Count == 0 ? "" : string.Join(", ", Owners.Take(2).ToArray()); } }
    }

    public static class ProfileEngine
    {
        public const string IfeoRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        const string GpuKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
        const string LayerKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        const string FsoFlag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

        public static readonly string[] CpuText = new string[] { "Not set", "Low", "Below normal", "Normal", "Above normal", "High" };
        public static readonly int[] CpuVals = new int[] { -1, 1, 5, 2, 6, 3 };
        public static readonly string[] IoText = new string[] { "Not set", "Low", "Normal", "High" };
        public static readonly int[] IoVals = new int[] { -1, 1, 2, 3 };
        public static readonly string[] GpuText = new string[] { "Not set", "Power saving", "High performance" };
        public static readonly int[] GpuVals = new int[] { -1, 1, 2 };
        public static readonly string[] FsoText = new string[] { "Not set", "Turned off" };

        // Names that are never touched: anti-cheat, security and core Windows.
        static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "vgc.exe", "vgtray.exe", "beservice.exe", "bedaisy.exe", "easyanticheat.exe", "easyanticheat_eos.exe", "easyanticheat_launcher.exe", "faceit.exe", "esea.exe",
            "system", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "svchost.exe", "dwm.exe", "msmpeng.exe", "nissrv.exe",
            "securityhealthservice.exe", "explorer.exe", "taskmgr.exe", "audiodg.exe", "vanguard.exe"
        };
        // Vanguard watches the Image File Execution Options key, so priority is never written there for Riot's games.
        static readonly HashSet<string> NoIfeo = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "VALORANT-Win64-Shipping.exe", "VALORANT.exe", "RiotClientServices.exe", "RiotClientUx.exe", "RiotClientUxRender.exe", "LeagueClient.exe", "League of Legends.exe"
        };

        public static bool IsProtected(string exe) { return Protected.Contains(exe ?? ""); }
        public static bool IfeoBlocked(string exe) { return NoIfeo.Contains(exe ?? ""); }

        static string NormExe(string exe)
        {
            exe = (exe ?? "").Trim();
            if (exe.Length == 0) return "";
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
            return System.IO.Path.GetFileName(exe);
        }

        // ---------------------------------------------------------------- reading what is really in the registry

        static Dictionary<string, string> ReadMap(RegistryKey root, string sub)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (RegistryKey k = root.OpenSubKey(sub))
                {
                    if (k == null) return d;
                    foreach (string n in k.GetValueNames()) d[n] = Convert.ToString(k.GetValue(n) ?? "");
                }
            }
            catch { }
            return d;
        }

        public static int ReadIfeo(string exe, string name)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(IfeoRoot + "\\" + exe + "\\PerfOptions"))
                {
                    if (k == null) return -1;
                    object v = k.GetValue(name);
                    return v is int ? (int)v : -1;
                }
            }
            catch { return -1; }
        }

        static int ParseGpu(string raw)
        {
            Match m = Regex.Match(raw ?? "", @"GpuPreference=(\d)");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        /// <summary>Fills in the live state of every entry from three registry reads.</summary>
        public static void ReadStates(List<ProgEntry> list)
        {
            Dictionary<string, string> gpu = ReadMap(Registry.CurrentUser, GpuKey);
            Dictionary<string, string> lay = ReadMap(Registry.CurrentUser, LayerKey);
            Dictionary<string, string> layLm = ReadMap(Registry.LocalMachine, LayerKey);
            foreach (ProgEntry e in list)
            {
                ProgState s = new ProgState();
                s.Cpu = ReadIfeo(e.Exe, "CpuPriorityClass");
                s.Io = ReadIfeo(e.Exe, "IoPriority");
                foreach (string p in e.Paths)
                {
                    string raw; if (gpu.TryGetValue(p, out raw)) { int g = ParseGpu(raw); if (g >= 0 && g > s.Gpu) { s.Gpu = g; s.GpuRaw = raw; } }
                    string l; if (lay.TryGetValue(p, out l) && l.IndexOf(FsoFlag, StringComparison.OrdinalIgnoreCase) >= 0) s.Fso = true;
                    if (layLm.TryGetValue(p, out l) && l.IndexOf(FsoFlag, StringComparison.OrdinalIgnoreCase) >= 0) s.FsoMachine = true;
                }
                e.St = s;
            }
        }

        // ---------------------------------------------------------------- writing

        /// <summary>Sets (value &gt;= 0) or removes (value &lt; 0) a PerfOptions value. Windows applies it every time the program starts, with or without this app.</summary>
        public static string SetPerf(string exe, string valueName, int val)
        {
            exe = NormExe(exe);
            if (exe.Length == 0) return "No program name.";
            if (IsProtected(exe)) return exe + " is an anti-cheat, security or core Windows program. It is never changed.";
            if (NoIfeo.Contains(exe)) return exe + " belongs to a Riot game protected by Vanguard, which watches this registry area. Priority for it is not written here.";
            try
            {
                if (val >= 0)
                {
                    using (RegistryKey k = Registry.LocalMachine.CreateSubKey(IfeoRoot + "\\" + exe + "\\PerfOptions"))
                        k.SetValue(valueName, val, RegistryValueKind.DWord);
                }
                else
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(IfeoRoot + "\\" + exe + "\\PerfOptions", true))
                        if (k != null) k.DeleteValue(valueName, false);
                    CleanupIfeo(exe);
                }
                int back = ReadIfeo(exe, valueName);
                if (back != val) return "Windows did not keep the value (read back " + back + ").";
                return null;
            }
            catch (Exception ex) { Log.Err("ifeo " + exe, ex); return ex.Message; }
        }

        static void CleanupIfeo(string exe)
        {
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(IfeoRoot, true))
                {
                    if (root == null) return;
                    bool poEmpty;
                    using (RegistryKey po = root.OpenSubKey(exe + "\\PerfOptions")) poEmpty = po != null && po.ValueCount == 0 && po.SubKeyCount == 0;
                    if (poEmpty) root.DeleteSubKey(exe + "\\PerfOptions", false);
                    bool exeEmpty;
                    using (RegistryKey ek = root.OpenSubKey(exe)) exeEmpty = ek != null && ek.ValueCount == 0 && ek.SubKeyCount == 0;
                    if (exeEmpty) root.DeleteSubKey(exe, false);
                }
            }
            catch { }
        }

        public static string SetGpu(string path, int pref)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(GpuKey))
                {
                    string cur = Convert.ToString(k.GetValue(path) ?? "");
                    List<string> tokens = cur.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Where(t => !t.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (pref > 0) tokens.Insert(0, "GpuPreference=" + pref);
                    if (tokens.Count == 0) k.DeleteValue(path, false);
                    else k.SetValue(path, string.Join(";", tokens.ToArray()) + ";", RegistryValueKind.String);
                    string back = Convert.ToString(k.GetValue(path) ?? "");
                    if (ParseGpu(back) != (pref > 0 ? pref : -1)) return "Windows did not keep the graphics preference.";
                }
                return null;
            }
            catch (Exception ex) { Log.Err("gpu pref", ex); return ex.Message; }
        }

        static void SetFsoHive(RegistryKey root, string path, bool off)
        {
            using (RegistryKey k = root.CreateSubKey(LayerKey))
            {
                string cur = Convert.ToString(k.GetValue(path) ?? "");
                List<string> tokens = cur.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(t => t != "~").ToList();
                bool has = tokens.Contains(FsoFlag);
                if (off && !has) tokens.Add(FsoFlag);
                else if (!off && has) tokens.Remove(FsoFlag);
                else return;
                if (tokens.Count == 0) k.DeleteValue(path, false);
                else k.SetValue(path, "~ " + string.Join(" ", tokens.ToArray()), RegistryValueKind.String);
            }
        }

        public static string SetFso(string path, bool off)
        {
            try
            {
                SetFsoHive(Registry.CurrentUser, path, off);
                if (!off) { try { SetFsoHive(Registry.LocalMachine, path, false); } catch { } }
                return null;
            }
            catch (Exception ex) { Log.Err("fso", ex); return ex.Message; }
        }

        public static string ReadGpu(string path)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(GpuKey))
                    return k == null ? "" : Convert.ToString(k.GetValue(path) ?? "");
            }
            catch { return ""; }
        }

        // ---------------------------------------------------------------- original values, so a reset really puts things back

        static string BackupFile { get { return System.IO.Path.Combine(Paths.DataDir, "profile-originals.tsv"); } }
        static readonly object Lk = new object();

        /// <summary>Remembers what the registry held before this app first changed a program.</summary>
        public static void BackupOnce(ProgEntry e)
        {
            lock (Lk)
            {
                try
                {
                    List<string> lines = File.Exists(BackupFile) ? File.ReadAllLines(BackupFile, Encoding.UTF8).ToList() : new List<string>();
                    if (lines.Any(l => l.StartsWith(e.Exe + "\t", StringComparison.OrdinalIgnoreCase))) return;
                    ProgState s = e.St;
                    List<string> paths = e.Paths.Count == 0 ? new List<string> { "" } : e.Paths;
                    foreach (string p in paths)
                    {
                        int gp = -1; bool fs = false;
                        if (p.Length > 0) { gp = ParseGpu(ReadGpu(p)); fs = s.Fso; }
                        lines.Add(e.Exe + "\t" + s.Cpu + "\t" + s.Io + "\t" + p + "\t" + gp + "\t" + (fs ? "1" : "0"));
                    }
                    File.WriteAllLines(BackupFile, lines.ToArray(), Encoding.UTF8);
                }
                catch (Exception ex) { Log.Err("profile backup", ex); }
            }
        }

        public static bool HasBackup(string exe)
        {
            try { return File.Exists(BackupFile) && File.ReadAllLines(BackupFile, Encoding.UTF8).Any(l => l.StartsWith(exe + "\t", StringComparison.OrdinalIgnoreCase)); }
            catch { return false; }
        }

        /// <summary>Puts a program back to what it was before this app touched it (or to "not set" when nothing was recorded).</summary>
        public static string Reset(ProgEntry e)
        {
            string err = null;
            lock (Lk)
            {
                List<string> rest = new List<string>();
                List<string[]> mine = new List<string[]>();
                try
                {
                    if (File.Exists(BackupFile))
                        foreach (string l in File.ReadAllLines(BackupFile, Encoding.UTF8))
                        {
                            string[] f = l.Split('\t');
                            if (f.Length >= 6 && string.Equals(f[0], e.Exe, StringComparison.OrdinalIgnoreCase)) mine.Add(f); else rest.Add(l);
                        }
                }
                catch { }
                int cpu = mine.Count > 0 ? int.Parse(mine[0][1]) : -1, io = mine.Count > 0 ? int.Parse(mine[0][2]) : -1;
                if (!IsProtected(e.Exe) && !IfeoBlocked(e.Exe))
                {
                    string a = SetPerf(e.Exe, "CpuPriorityClass", cpu); if (a != null) err = a;
                    a = SetPerf(e.Exe, "IoPriority", io); if (a != null) err = a;
                }
                foreach (string p in e.Paths)
                {
                    string[] m = mine.FirstOrDefault(f => string.Equals(f[3], p, StringComparison.OrdinalIgnoreCase));
                    int gp = m != null ? int.Parse(m[4]) : -1; bool fs = m != null && m[5] == "1";
                    string a = SetGpu(p, gp); if (a != null) err = a;
                    a = SetFso(p, fs); if (a != null) err = a;
                }
                try { if (rest.Count == 0) File.Delete(BackupFile); else File.WriteAllLines(BackupFile, rest.ToArray(), Encoding.UTF8); } catch { }
            }
            return err;
        }

        // ---------------------------------------------------------------- finding every program

        static readonly Regex Junk = new Regex(@"(unins|uninst|vc_?redist|vcredist|dxsetup|dotnet|crash|setup|install|^update|updater|notification_helper|elevate|webview|cefsharp|werfault|helper|bootstrap|^launcher_|reporter|diagnostic)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_commonredist", "redist", "redistributables", "__installer", "support", "node_modules", "locales", "resources", "swiftshader", "crashreport", "crashpad",
            "directx", "vcredist", "dotnet", "engine", "thirdparty", "easyanticheat", "battleye", "cache", "logs", "$recycle.bin", "windows", "plugins", "mono", "il2cpp"
        };

        static void Walk(string dir, int depth, int maxDepth, long minSize, List<KeyValuePair<string, long>> found, int[] budget)
        {
            if (budget[0]-- <= 0) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*.exe"))
                {
                    try
                    {
                        FileInfo fi = new FileInfo(f);
                        if (fi.Length < minSize || Junk.IsMatch(fi.Name)) continue;
                        found.Add(new KeyValuePair<string, long>(f, fi.Length));
                    }
                    catch { }
                }
                if (depth >= maxDepth) return;
                foreach (string s in Directory.GetDirectories(dir))
                {
                    if (SkipDirs.Contains(System.IO.Path.GetFileName(s))) continue;
                    Walk(s, depth + 1, maxDepth, minSize, found, budget);
                }
            }
            catch { }
        }

        public static List<string> FindExes(string dir, int maxDepth, int take, long minSize)
        {
            List<KeyValuePair<string, long>> found = new List<KeyValuePair<string, long>>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return new List<string>();
            Walk(dir, 0, maxDepth, minSize, found, new int[] { 500 });
            return found.OrderByDescending(x => x.Value).Take(take).Select(x => x.Key).ToList();
        }

        static ProgEntry Add(Dictionary<string, ProgEntry> map, string exe, string path, string owner, string kind)
        {
            exe = NormExe(exe);
            if (exe.Length == 0) return null;
            ProgEntry e;
            if (!map.TryGetValue(exe, out e)) { e = new ProgEntry(); e.Exe = exe; e.Kind = kind; map[exe] = e; }
            if (!string.IsNullOrEmpty(path) && !e.Paths.Contains(path, StringComparer.OrdinalIgnoreCase)) e.Paths.Add(path);
            if (!string.IsNullOrEmpty(owner) && !e.Owners.Contains(owner, StringComparer.OrdinalIgnoreCase)) e.Owners.Add(owner);
            // a Game label wins over App, which wins over Running / Set
            int rank = kind == "Game" ? 3 : kind == "App" ? 2 : kind == "Running" ? 1 : 0;
            int cur = e.Kind == "Game" ? 3 : e.Kind == "App" ? 2 : e.Kind == "Running" ? 1 : 0;
            if (rank > cur) e.Kind = kind;
            return e;
        }

        /// <summary>Scans installed apps, games, running programs and everything already configured. Takes a few seconds; call from a worker.</summary>
        public static List<ProgEntry> BuildCatalog()
        {
            Dictionary<string, ProgEntry> map = new Dictionary<string, ProgEntry>(StringComparer.OrdinalIgnoreCase);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();

            // games
            try
            {
                foreach (GameEntry g in GameLib.All)
                    foreach (string p in FindExes(g.Dir, 5, 5, 3L * 1024 * 1024))
                        Add(map, System.IO.Path.GetFileName(p), p, g.Name, "Game");
            }
            catch (Exception ex) { Log.Err("catalog games", ex); }

            // installed applications
            try
            {
                HashSet<string> gameDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (AppRow a in AppReader.ReadInstalled())
                {
                    if (string.IsNullOrEmpty(a.Location) || !Directory.Exists(a.Location)) continue;
                    string ll = a.Location.ToLowerInvariant();
                    if (ll.StartsWith(win) || a.Name.IndexOf("Redistributable", StringComparison.OrdinalIgnoreCase) >= 0 || a.Name.IndexOf("Runtime", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    bool isGame = GameLib.IsGamePath(a.Location + "\\");
                    foreach (string p in FindExes(a.Location, 2, 4, 512L * 1024))
                        Add(map, System.IO.Path.GetFileName(p), p, a.Name, isGame ? "Game" : "App");
                }
            }
            catch (Exception ex) { Log.Err("catalog apps", ex); }

            // running programs
            try
            {
                foreach (Process pr in Process.GetProcesses())
                {
                    try
                    {
                        string p = Native.ProcPath(pr.Id);
                        if (p == null || p.ToLowerInvariant().StartsWith(win)) continue;
                        ProgEntry e = Add(map, pr.ProcessName + ".exe", p, null, "Running");
                        if (e != null) e.Running = true;
                    }
                    catch { }
                    finally { pr.Dispose(); }
                }
            }
            catch (Exception ex) { Log.Err("catalog running", ex); }

            // everything already set in the registry, even for programs that are not installed any more
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(IfeoRoot))
                    if (k != null)
                        foreach (string n in k.GetSubKeyNames())
                        {
                            if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                            using (RegistryKey po = k.OpenSubKey(n + "\\PerfOptions"))
                                if (po != null && po.ValueCount > 0) Add(map, n, null, null, "Set");
                        }
                foreach (KeyValuePair<string, string> kv in ReadMap(Registry.CurrentUser, GpuKey))
                    if (ParseGpu(kv.Value) >= 0) Add(map, System.IO.Path.GetFileName(kv.Key), kv.Key, null, "Set");
                foreach (KeyValuePair<string, string> kv in ReadMap(Registry.CurrentUser, LayerKey))
                    if (kv.Value.IndexOf(FsoFlag, StringComparison.OrdinalIgnoreCase) >= 0) Add(map, System.IO.Path.GetFileName(kv.Key), kv.Key, null, "Set");
            }
            catch (Exception ex) { Log.Err("catalog registry", ex); }

            HashSet<string> running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Process pr in Process.GetProcesses()) { try { running.Add(pr.ProcessName + ".exe"); } catch { } finally { pr.Dispose(); } }
            foreach (ProgEntry e in map.Values) e.Running = running.Contains(e.Exe);

            List<ProgEntry> list = map.Values.Where(e => !IsProtected(e.Exe)).OrderBy(e => e.Exe, StringComparer.OrdinalIgnoreCase).ToList();
            ReadStates(list);
            return list;
        }

        // ---------------------------------------------------------------- recipes

        public class Preset { public string Exe, Note; public int Cpu = -1, Io = -1, Gpu = -1; public bool Fso; }

        public static List<Preset> Presets()
        {
            return new List<Preset>
            {
                new Preset { Exe = "TslGame.exe", Note = "PUBG", Cpu = 3, Io = 3, Gpu = 2, Fso = true },
                new Preset { Exe = "VALORANT-Win64-Shipping.exe", Note = "VALORANT (graphics and fullscreen only, Vanguard)", Gpu = 2, Fso = true },
                new Preset { Exe = "obs64.exe", Note = "OBS Studio", Cpu = 6 },
                new Preset { Exe = "steamwebhelper.exe", Note = "Steam browser helper", Cpu = 5, Io = 1 },
                new Preset { Exe = "brave.exe", Note = "Brave", Cpu = 5, Io = 1 },
                new Preset { Exe = "Discord.exe", Note = "Discord", Cpu = 5 }
            };
        }

        /// <summary>Applies CPU / I/O priority, graphics preference and fullscreen setting to a program, recording originals first. Returns a list of problems (empty when all good).</summary>
        public static List<string> Apply(ProgEntry e, int cpu, int io, int gpu, bool fso, bool touchCpu, bool touchGpu)
        {
            List<string> errs = new List<string>();
            if (IsProtected(e.Exe)) { errs.Add(e.Exe + " is protected and is never changed."); return errs; }
            BackupOnce(e);
            if (touchCpu && !IfeoBlocked(e.Exe))
            {
                string a = SetPerf(e.Exe, "CpuPriorityClass", cpu); if (a != null) errs.Add("CPU priority: " + a);
                a = SetPerf(e.Exe, "IoPriority", io); if (a != null) errs.Add("I/O priority: " + a);
            }
            if (touchGpu)
                foreach (string p in e.Paths)
                {
                    string a = SetGpu(p, gpu); if (a != null) errs.Add("Graphics: " + a);
                    a = SetFso(p, fso); if (a != null) errs.Add("Fullscreen: " + a);
                }
            return errs;
        }

        /// <summary>Old versions kept a list of profiles in a text file and applied them while the app ran. They are moved into the registry once.</summary>
        public static void Start()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string old = System.IO.Path.Combine(Paths.DataDir, "app-profiles.tsv");
                    if (!File.Exists(old)) return;
                    int n = 0;
                    foreach (string line in File.ReadAllLines(old, Encoding.UTF8))
                    {
                        string[] f = line.Split('\t');
                        if (f.Length < 7 || f[0].Length == 0 || f[4] != "1") continue;
                        ProgEntry e = new ProgEntry(); e.Exe = NormExe(f[0]);
                        if (f[5].Length > 0) e.Paths.Add(f[5]);
                        ReadStates(new List<ProgEntry> { e });
                        int cpu = f[1] == "Idle" ? 1 : f[1] == "BelowNormal" ? 5 : f[1] == "Normal" ? 2 : f[1] == "AboveNormal" ? 6 : f[1] == "High" ? 3 : -1;
                        int gpu; if (!int.TryParse(f[2], out gpu)) gpu = -1;
                        BackupOnce(e);
                        if (cpu >= 0) SetPerf(e.Exe, "CpuPriorityClass", cpu);
                        foreach (string p in e.Paths) { if (gpu > 0) SetGpu(p, gpu); if (f[3] == "1") SetFso(p, true); }
                        n++;
                    }
                    File.Move(old, old + ".migrated");
                    Log.Info("Moved " + n + " old app profiles into the registry");
                }
                catch (Exception ex) { Log.Err("profile migration", ex); }
            });
        }
    }

    // ------------------------------------------------------------------ page
    public class ProfilesPage : PageBase
    {
        List<ProgEntry> _all = new List<ProgEntry>();
        ProgEntry _sel;
        string _filter = "All";
        TextBox _search;
        StackPanel _rows, _detail;
        TextBlock _head, _msg, _count;
        Dictionary<ProgEntry, Border> _rowMap = new Dictionary<ProgEntry, Border>();
        bool _scanning;
        DateTime _scanned = DateTime.MinValue;
        List<RadioButton> _filters = new List<RadioButton>();

        public ProfilesPage() : base("App Profiles", "Every program on this PC. What you pick is written into Windows itself, so it stays after a restart, with or without this app running.")
        {
            // top card
            StackPanel top = new StackPanel();
            _head = UK.T("Scanning installed apps and games...", "Big");
            _msg = UK.T("", "Muted"); _msg.Margin = new Thickness(0, 4, 0, 10); _msg.TextWrapping = TextWrapping.Wrap;
            top.Children.Add(_head); top.Children.Add(_msg);
            WrapPanel bt = new WrapPanel();
            bt.Children.Add(UK.Btn("Set up all my games", true, delegate { SetupAllGames(); }));
            bt.Children.Add(UK.Btn("Apply my usual set", false, delegate { ApplyUsual(); }));
            bt.Children.Add(UK.Btn("Rescan", false, delegate { Scan(); }));
            top.Children.Add(bt);
            Body.Children.Add(UK.Card(top));

            // list + detail side by side on wide windows, stacked on narrow ones
            FlowGrid fg = new FlowGrid { MinItemWidth = 470, Gap = 14 };
            Body.Children.Add(fg);

            StackPanel left = new StackPanel();
            _search = new TextBox { Style = UK.S("Box"), Height = 34, Margin = new Thickness(0, 0, 0, 8), Tag = "Search programs...", ToolTip = "Search by program or app name" };
            _search.TextChanged += delegate { Refilter(); };
            left.Children.Add(_search);
            WrapPanel fl = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            foreach (string f in new string[] { "All", "Games", "Apps", "Running", "Configured" })
            {
                string name = f;
                RadioButton rb = new RadioButton { Style = UK.S("Seg"), GroupName = "proffilter", Content = f, IsChecked = f == "All", Margin = new Thickness(0, 0, 0, 4) };
                rb.Click += delegate { _filter = name; Refilter(); };
                _filters.Add(rb); fl.Children.Add(rb);
            }
            left.Children.Add(fl);
            _count = UK.T("", "Muted"); _count.Margin = new Thickness(0, 0, 0, 6);
            left.Children.Add(_count);
            _rows = new StackPanel();
            ScrollViewer sv = new ScrollViewer { MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _rows };
            left.Children.Add(sv);
            Border lc = new Border { Style = UK.S("Card"), Child = left };
            fg.Children.Add(lc);

            _detail = new StackPanel();
            Border dc = new Border { Style = UK.S("Card"), Child = _detail };
            fg.Children.Add(dc);
            ShowDetail(null);

            Scan();
        }

        public override void Shown()
        {
            // cheap: re-read the registry for every row, so what you see is what Windows holds right now
            if (_all.Count > 0 && (DateTime.Now - _scanned).TotalSeconds > 3)
            {
                ProfileEngine.ReadStates(_all);
                RefreshRows();
                if (_sel != null) ShowDetail(_sel);
            }
        }

        void Scan()
        {
            if (_scanning) return;
            _scanning = true;
            _head.Text = "Scanning installed apps and games...";
            UK.Bg(delegate { return ProfileEngine.BuildCatalog(); }, delegate(object o)
            {
                _scanning = false; _scanned = DateTime.Now;
                List<ProgEntry> l = o as List<ProgEntry>;
                if (l != null)
                {
                    string keep = _sel == null ? null : _sel.Exe;
                    _all = l;
                    _sel = keep == null ? null : l.FirstOrDefault(x => string.Equals(x.Exe, keep, StringComparison.OrdinalIgnoreCase));
                }
                Refilter();
                if (_sel != null) ShowDetail(_sel);
            });
        }

        IEnumerable<ProgEntry> Filtered()
        {
            string q = (_search.Text ?? "").Trim().ToLowerInvariant();
            IEnumerable<ProgEntry> e = _all;
            if (_filter == "Games") e = e.Where(x => x.Kind == "Game");
            else if (_filter == "Apps") e = e.Where(x => x.Kind == "App");
            else if (_filter == "Running") e = e.Where(x => x.Running);
            else if (_filter == "Configured") e = e.Where(x => x.St.Any);
            if (q.Length > 0) e = e.Where(x => x.Exe.ToLowerInvariant().Contains(q) || x.Owners.Any(o => o.ToLowerInvariant().Contains(q)));
            return e;
        }

        void Refilter()
        {
            int configured = _all.Count(x => x.St.Any);
            _head.Text = _all.Count + " programs found, " + configured + " with settings in Windows";
            if (_msg.Text.Length == 0) _msg.Text = "Click a program to see exactly what the registry holds for it, and change it. Anti-cheat and core Windows programs are hidden and never touched.";
            RefreshRows();
        }

        void RefreshRows()
        {
            _rows.Children.Clear(); _rowMap.Clear();
            List<ProgEntry> l = Filtered().ToList();
            int shown = 0;
            foreach (ProgEntry e in l)
            {
                if (shown++ >= 250) break;
                Border b = BuildRow(e);
                _rowMap[e] = b;
                _rows.Children.Add(b);
            }
            _count.Text = l.Count == 0 ? (_scanning ? "Scanning..." : "Nothing matches.") : (l.Count > 250 ? "Showing the first 250 of " + l.Count + ". Type in the search box to narrow it down." : l.Count + " shown");
            Highlight();
        }

        Border BuildRow(ProgEntry e)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel l = new StackPanel();
            TextBlock n = UK.T(e.Exe, "Body"); n.FontWeight = FontWeights.SemiBold; n.TextTrimming = TextTrimming.CharacterEllipsis;
            l.Children.Add(n);
            string sub = (e.OwnerText.Length > 0 ? e.OwnerText + "  -  " : "") + (e.Kind == "Set" ? "set in registry" : e.Kind.ToLowerInvariant()) + (e.Running && !string.Equals(e.Kind, "Running", StringComparison.OrdinalIgnoreCase) ? "  -  running" : "");
            TextBlock s = UK.T(sub, "Muted"); s.TextTrimming = TextTrimming.CharacterEllipsis; s.Margin = new Thickness(0, 1, 0, 0);
            l.Children.Add(s);
            g.Children.Add(l);
            WrapPanel chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 210 };
            ProgState st = e.St;
            if (st.Cpu >= 0) chips.Children.Add(UK.Chip("CPU " + Name(ProfileEngine.CpuVals, ProfileEngine.CpuText, st.Cpu), st.Cpu >= 6 ? "Good" : st.Cpu == 2 ? "" : "Medium"));
            if (st.Io >= 0) chips.Children.Add(UK.Chip("I/O " + Name(ProfileEngine.IoVals, ProfileEngine.IoText, st.Io), st.Io >= 3 ? "Good" : st.Io == 2 ? "" : "Medium"));
            if (st.Gpu >= 0) chips.Children.Add(UK.Chip(st.Gpu == 2 ? "GPU high" : "GPU saver", st.Gpu == 2 ? "Good" : "Medium"));
            if (st.Fso || st.FsoMachine) chips.Children.Add(UK.Chip("FSO off", "Good"));
            if (!st.Any) { TextBlock none = UK.T("not set", "Muted"); chips.Children.Add(none); }
            Grid.SetColumn(chips, 1);
            g.Children.Add(chips);
            Border b = new Border { Child = g, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 3), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = System.Windows.Media.Brushes.Transparent, Background = System.Windows.Media.Brushes.Transparent, Cursor = Cursors.Hand };
            ProgEntry en = e;
            b.MouseEnter += delegate { if (_sel != en) b.Background = UK.Br("HoverBrush"); };
            b.MouseLeave += delegate { if (_sel != en) b.Background = System.Windows.Media.Brushes.Transparent; };
            b.MouseLeftButtonUp += delegate { _sel = en; Highlight(); ShowDetail(en); };
            return b;
        }

        void Highlight()
        {
            foreach (KeyValuePair<ProgEntry, Border> kv in _rowMap)
            {
                bool sel = kv.Key == _sel;
                kv.Value.Background = sel ? UK.Br("SelBrush") : System.Windows.Media.Brushes.Transparent;
                kv.Value.BorderBrush = sel ? UK.Br("SelEdgeBrush") : System.Windows.Media.Brushes.Transparent;
            }
        }

        static string Name(int[] vals, string[] text, int v)
        {
            int i = Array.IndexOf(vals, v);
            return i >= 0 ? text[i] : v.ToString();
        }

        // ---------------------------------------------------------------- detail

        void ShowDetail(ProgEntry e)
        {
            _detail.Children.Clear();
            if (e == null)
            {
                _detail.Children.Add(UK.T("Pick a program", "H2"));
                TextBlock t = UK.T("Click any program in the list. This panel then shows what Windows currently holds for it in the registry, and lets you change it. The highlighted button in each row is the value that is really stored right now.", "Muted");
                t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 6, 0, 0);
                _detail.Children.Add(t);
                return;
            }
            _detail.Children.Add(UK.T(e.Exe, "H2"));
            TextBlock sub = UK.T((e.OwnerText.Length > 0 ? e.OwnerText + "  -  " : "") + e.Kind.ToLowerInvariant() + (e.Running && !string.Equals(e.Kind, "Running", StringComparison.OrdinalIgnoreCase) ? "  -  running now" : ""), "Muted");
            sub.Margin = new Thickness(0, 2, 0, 0);
            _detail.Children.Add(sub);
            if (e.Paths.Count > 0)
            {
                TextBlock p = UK.T(string.Join("\n", e.Paths.Take(3).ToArray()), "Muted");
                p.TextWrapping = TextWrapping.Wrap; p.Margin = new Thickness(0, 4, 0, 0);
                _detail.Children.Add(p);
            }
            WrapPanel tags = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
            tags.Children.Add(UK.Chip("PERMANENT: stored in Windows", "Good"));
            _detail.Children.Add(tags);

            _dmsg = UK.T("", "Muted"); _dmsg.TextWrapping = TextWrapping.Wrap; _dmsg.Margin = new Thickness(0, 4, 0, 8);
            _detail.Children.Add(_dmsg);

            ProgState s = e.St;
            bool blocked = ProfileEngine.IfeoBlocked(e.Exe);
            bool noPath = e.Paths.Count == 0;

            AddSetting(e, "CPU priority", "How much processor time this program gets against others. Windows applies it every time the program starts.",
                ProfileEngine.CpuText, Array.IndexOf(ProfileEngine.CpuVals, s.Cpu), !blocked,
                delegate(int i) { return ProfileEngine.SetPerf(e.Exe, "CpuPriorityClass", ProfileEngine.CpuVals[i]); },
                "HKLM\\...\\Image File Execution Options\\" + e.Exe + "\\PerfOptions", "CpuPriorityClass", s.Cpu >= 0 ? s.Cpu.ToString() + " (" + Name(ProfileEngine.CpuVals, ProfileEngine.CpuText, s.Cpu) + ")" : null,
                blocked ? "Not available here: Vanguard watches this area, so priority for this Riot program is not written to the registry." : null);

            AddSetting(e, "Disk priority (I/O)", "Which program the drive serves first when it is busy.",
                ProfileEngine.IoText, Array.IndexOf(ProfileEngine.IoVals, s.Io), !blocked,
                delegate(int i) { return ProfileEngine.SetPerf(e.Exe, "IoPriority", ProfileEngine.IoVals[i]); },
                "HKLM\\...\\Image File Execution Options\\" + e.Exe + "\\PerfOptions", "IoPriority", s.Io >= 0 ? s.Io.ToString() + " (" + Name(ProfileEngine.IoVals, ProfileEngine.IoText, s.Io) + ")" : null, null);

            AddSetting(e, "Graphics card", "Which GPU Windows uses for this program. High performance picks the fast card on laptops and mixed setups.",
                ProfileEngine.GpuText, Array.IndexOf(ProfileEngine.GpuVals, s.Gpu), !noPath,
                delegate(int i)
                {
                    string err = null;
                    foreach (string p in e.Paths) { string a = ProfileEngine.SetGpu(p, ProfileEngine.GpuVals[i]); if (a != null) err = a; }
                    return err;
                },
                "HKCU\\Software\\Microsoft\\DirectX\\UserGpuPreferences", "(program path)", s.Gpu >= 0 ? s.GpuRaw : null,
                noPath ? "The location of this program is not known yet. Use Browse below, or start the program once." : null);

            AddSetting(e, "Fullscreen optimizations", "Turned off gives the game exclusive control of the screen path, which many players prefer for steadier frame times.",
                ProfileEngine.FsoText, (s.Fso || s.FsoMachine) ? 1 : 0, !noPath,
                delegate(int i)
                {
                    string err = null;
                    foreach (string p in e.Paths) { string a = ProfileEngine.SetFso(p, i == 1); if (a != null) err = a; }
                    return err;
                },
                "HKCU\\...\\AppCompatFlags\\Layers", "(program path)", (s.Fso || s.FsoMachine) ? "~ DISABLEDXMAXIMIZEDWINDOWEDMODE" + (s.FsoMachine ? " (also set for all users)" : "") : null,
                noPath ? "The location of this program is not known yet. Use Browse below, or start the program once." : null);

            WrapPanel act = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            act.Children.Add(UK.Btn("My game setup", true, delegate
            {
                List<string> errs = ProfileEngine.Apply(e, 3, 3, 2, true, !blocked, !noPath);
                Reload(e, errs.Count == 0 ? "Game setup saved: CPU High, disk High, graphics High performance, fullscreen optimizations off. Read back from the registry below." : string.Join(" ", errs.ToArray()), errs.Count == 0);
            }));
            act.Children.Add(UK.Btn("Reset to original", false, delegate
            {
                string err = ProfileEngine.Reset(e);
                Reload(e, err ?? (ProfileEngine.HasBackup(e.Exe) ? "Put back." : "Everything this app set for this program was removed."), err == null);
            }));
            if (e.Running) act.Children.Add(UK.Btn("Apply priority to running copy now", false, delegate { ApplyLive(e); }));
            act.Children.Add(UK.Btn("Browse for program...", false, delegate { Browse(e); }));
            _detail.Children.Add(act);

            TextBlock note = UK.T("Priority and disk priority use the same Windows feature as Task Manager's Set priority, stored per program name so they are applied at every launch. Real-time is never offered. Nothing is injected into any program. Anti-cheat and core Windows programs are never touched.", "Muted");
            note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 12, 0, 0);
            _detail.Children.Add(note);
        }

        TextBlock _dmsg;

        void AddSetting(ProgEntry e, string title, string why, string[] opts, int selected, bool enabled, Func<int, string> pick, string regPath, string regName, string regValue, string disabledNote)
        {
            StackPanel blk = new StackPanel { Margin = new Thickness(0, 8, 0, 6) };
            blk.Children.Add(UK.T(title, "Body"));
            ((TextBlock)blk.Children[0]).FontWeight = FontWeights.SemiBold;
            TextBlock w = UK.T(why, "Muted"); w.TextWrapping = TextWrapping.Wrap; w.Margin = new Thickness(0, 1, 0, 6);
            blk.Children.Add(w);
            WrapPanel seg = new WrapPanel();
            string group = "set" + Guid.NewGuid().ToString("N");
            for (int i = 0; i < opts.Length; i++)
            {
                int idx = i;
                RadioButton rb = new RadioButton { Style = UK.S("Seg"), GroupName = group, Content = opts[i], IsChecked = i == Math.Max(0, selected), IsEnabled = enabled, Margin = new Thickness(0, 0, 0, 4) };
                rb.Click += delegate
                {
                    string err = pick(idx);
                    Reload(e, err ?? e.Exe + ": " + title + " set to " + opts[idx].ToLowerInvariant() + ". Saved to the registry.", err == null);
                };
                seg.Children.Add(rb);
            }
            blk.Children.Add(seg);
            TextBlock reg = UK.T(disabledNote ?? ("Registry now: " + (regValue == null ? "nothing stored (Windows default)" : regName + " = " + regValue) + "   [" + regPath + "]"), "Muted");
            reg.FontSize = 11; reg.TextWrapping = TextWrapping.Wrap; reg.Margin = new Thickness(0, 2, 0, 0);
            if (disabledNote != null) reg.Foreground = UK.Br("WarnBrush");
            blk.Children.Add(reg);
            Border line = new Border { Height = 1, Background = UK.Br("LineBrush"), Margin = new Thickness(0, 10, 0, 0) };
            blk.Children.Add(line);
            _detail.Children.Add(blk);
        }

        /// <summary>Re-reads this program from the registry, redraws the detail and the row, and shows the result.</summary>
        void Reload(ProgEntry e, string message, bool good)
        {
            ProfileEngine.ReadStates(new List<ProgEntry> { e });
            Border old;
            if (_rowMap.TryGetValue(e, out old))
            {
                int idx = _rows.Children.IndexOf(old);
                Border nb = BuildRow(e);
                if (idx >= 0) { _rows.Children.RemoveAt(idx); _rows.Children.Insert(idx, nb); _rowMap[e] = nb; }
            }
            Highlight();
            ShowDetail(e);
            _dmsg.Text = message;
            _dmsg.Foreground = UK.Br(good ? "GoodBrush" : "BadBrush");
            _head.Text = _all.Count + " programs found, " + _all.Count(x => x.St.Any) + " with settings in Windows";
        }

        void ApplyLive(ProgEntry e)
        {
            int n = 0; string back = "";
            ProcessPriorityClass cls;
            switch (e.St.Cpu) { case 1: cls = ProcessPriorityClass.Idle; break; case 5: cls = ProcessPriorityClass.BelowNormal; break; case 6: cls = ProcessPriorityClass.AboveNormal; break; case 3: cls = ProcessPriorityClass.High; break; case 2: cls = ProcessPriorityClass.Normal; break; default: Reload(e, "Pick a CPU priority first; the running copy then gets the same one.", false); return; }
            foreach (Process p in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(e.Exe)))
            {
                try { p.PriorityClass = cls; p.Refresh(); back = p.PriorityClass.ToString(); n++; } catch { }
                finally { p.Dispose(); }
            }
            Reload(e, n > 0 ? "Changed " + n + " running process" + (n == 1 ? "" : "es") + " now (read back: " + back + ")." : "Windows would not let this app change the running copy.", n > 0);
        }

        void Browse(ProgEntry e)
        {
            OpenFileDialog d = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Locate " + e.Exe };
            if (d.ShowDialog() != true) return;
            if (!string.Equals(System.IO.Path.GetFileName(d.FileName), e.Exe, StringComparison.OrdinalIgnoreCase)) { Reload(e, "That file is " + System.IO.Path.GetFileName(d.FileName) + ", not " + e.Exe + ".", false); return; }
            if (!e.Paths.Contains(d.FileName, StringComparer.OrdinalIgnoreCase)) e.Paths.Add(d.FileName);
            Reload(e, "Location saved for this session: " + d.FileName, true);
        }

        void SetupAllGames()
        {
            List<ProgEntry> games = _all.Where(x => x.Kind == "Game" && !ProfileEngine.IsProtected(x.Exe)).ToList();
            if (games.Count == 0) { _msg.Text = "No installed games were found. Use the Rescan button, or pick a program and press My game setup."; return; }
            if (Dlg.Show("Apply the game setup to " + games.Count + " game program" + (games.Count == 1 ? "" : "s") + "?\n\nCPU priority High, disk priority High, graphics High performance and fullscreen optimizations off. Riot games get graphics and fullscreen only. Originals are saved and Reset to original puts them back.", Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            int ok = 0, bad = 0;
            foreach (ProgEntry g in games)
            {
                List<string> errs = ProfileEngine.Apply(g, 3, 3, 2, true, !ProfileEngine.IfeoBlocked(g.Exe), g.Paths.Count > 0);
                if (errs.Count == 0) ok++; else bad++;
            }
            ProfileEngine.ReadStates(_all);
            _msg.Text = "Game setup written for " + ok + " program" + (ok == 1 ? "" : "s") + (bad > 0 ? ", " + bad + " could not be changed (see the log)" : "") + ". It is stored in Windows and applies at every launch.";
            RefreshRows(); if (_sel != null) ShowDetail(_sel);
        }

        void ApplyUsual()
        {
            int n = 0; List<string> bad = new List<string>();
            foreach (ProfileEngine.Preset p in ProfileEngine.Presets())
            {
                ProgEntry e = _all.FirstOrDefault(x => string.Equals(x.Exe, p.Exe, StringComparison.OrdinalIgnoreCase));
                if (e == null) { e = new ProgEntry(); e.Exe = p.Exe; e.Kind = "Set"; ProfileEngine.ReadStates(new List<ProgEntry> { e }); _all.Add(e); }
                List<string> errs = ProfileEngine.Apply(e, p.Cpu, p.Io, p.Gpu, p.Fso, p.Cpu >= 0 || p.Io >= 0, p.Gpu >= 0 || p.Fso);
                if (errs.Count == 0) n++; else bad.Add(p.Exe);
            }
            _all = _all.OrderBy(x => x.Exe, StringComparer.OrdinalIgnoreCase).ToList();
            ProfileEngine.ReadStates(_all);
            _msg.Text = "Usual set written for " + n + " program" + (n == 1 ? "" : "s") + " (PUBG, VALORANT, OBS, Steam helper, Brave, Discord)." + (bad.Count > 0 ? " Problems: " + string.Join(", ", bad.ToArray()) + "." : "") + " Graphics and fullscreen settings need the program's location, so they apply to the ones found on this PC.";
            Refilter(); if (_sel != null) ShowDetail(_sel);
        }
    }
}
