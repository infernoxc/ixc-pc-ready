using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace InfernoXC
{
    // ------------------------------------------------------------------ installed game detection
    public class GameEntry
    {
        public string Name, Launcher, Dir;
        public long SizeBytes;
    }

    public static class GameLib
    {
        static List<GameEntry> _games = new List<GameEntry>();
        static List<string> _dirs = new List<string>();
        static readonly object _lk = new object();
        public static bool Scanned;

        public static List<GameEntry> All
        {
            get { lock (_lk) { return new List<GameEntry>(_games); } }
        }

        public static bool IsGamePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            List<string> d;
            lock (_lk) { d = _dirs; }
            string p = path.ToLowerInvariant();
            for (int i = 0; i < d.Count; i++) if (p.StartsWith(d[i])) return true;
            return false;
        }

        public static void Scan()
        {
            List<GameEntry> list = new List<GameEntry>();
            try { ScanSteam(list); } catch (Exception ex) { Log.Err("steam scan", ex); }
            try { ScanEpic(list); } catch (Exception ex) { Log.Err("epic scan", ex); }
            try { ScanRiot(list); } catch (Exception ex) { Log.Err("riot scan", ex); }
            list = list.OrderBy(g => g.Name).ToList();
            List<string> dirs = new List<string>();
            foreach (GameEntry g in list)
            {
                if (string.IsNullOrEmpty(g.Dir)) continue;
                string d = g.Dir.ToLowerInvariant().TrimEnd('\\') + "\\";
                if (!dirs.Contains(d)) dirs.Add(d);
            }
            lock (_lk) { _games = list; _dirs = dirs; Scanned = true; }
        }

        static void ScanSteam(List<GameEntry> list)
        {
            string steam = Util.RegString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            if (string.IsNullOrEmpty(steam)) steam = Util.RegString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            if (string.IsNullOrEmpty(steam)) return;
            steam = steam.Replace('/', '\\');
            List<string> libs = new List<string>();
            libs.Add(steam);
            string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                {
                    string p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (!libs.Contains(p, StringComparer.OrdinalIgnoreCase)) libs.Add(p);
                }
            }
            foreach (string lib in libs)
            {
                string apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (string f in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    try
                    {
                        string t = File.ReadAllText(f);
                        Match n = Regex.Match(t, "\"name\"\\s+\"([^\"]+)\"");
                        Match d = Regex.Match(t, "\"installdir\"\\s+\"([^\"]+)\"");
                        Match s = Regex.Match(t, "\"SizeOnDisk\"\\s+\"(\\d+)\"");
                        if (!n.Success || !d.Success) continue;
                        string name = n.Groups[1].Value;
                        if (name.StartsWith("Steamworks Common", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)) continue;
                        GameEntry g = new GameEntry();
                        g.Name = name; g.Launcher = "Steam";
                        g.Dir = Path.Combine(apps, "common", d.Groups[1].Value);
                        long sz; if (s.Success && long.TryParse(s.Groups[1].Value, out sz)) g.SizeBytes = sz;
                        list.Add(g);
                    }
                    catch { }
                }
            }
        }

        static void ScanEpic(List<GameEntry> list)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.GetFiles(dir, "*.item"))
            {
                try
                {
                    string t = File.ReadAllText(f);
                    Match n = Regex.Match(t, "\"DisplayName\"\\s*:\\s*\"([^\"]+)\"");
                    Match d = Regex.Match(t, "\"InstallLocation\"\\s*:\\s*\"([^\"]+)\"");
                    Match s = Regex.Match(t, "\"InstallSize\"\\s*:\\s*(\\d+)");
                    if (!n.Success || !d.Success) continue;
                    GameEntry g = new GameEntry();
                    g.Name = n.Groups[1].Value; g.Launcher = "Epic Games";
                    g.Dir = d.Groups[1].Value.Replace("\\\\", "\\").Replace("/", "\\");
                    long sz; if (s.Success && long.TryParse(s.Groups[1].Value, out sz)) g.SizeBytes = sz;
                    list.Add(g);
                }
                catch { }
            }
        }

        static void ScanRiot(List<GameEntry> list)
        {
            foreach (DriveInfo di in DriveInfo.GetDrives())
            {
                try
                {
                    if (di.DriveType != DriveType.Fixed || !di.IsReady) continue;
                    string root = Path.Combine(di.Name, "Riot Games");
                    if (!Directory.Exists(root)) continue;
                    foreach (string sub in Directory.GetDirectories(root))
                    {
                        string n = Path.GetFileName(sub);
                        if (n.Equals("Riot Client", StringComparison.OrdinalIgnoreCase) || n.Equals("Riot Vanguard", StringComparison.OrdinalIgnoreCase) || n.Equals("Metadata", StringComparison.OrdinalIgnoreCase)) continue;
                        GameEntry g = new GameEntry();
                        g.Name = n; g.Launcher = "Riot Games"; g.Dir = sub;
                        g.SizeBytes = CleanKit.DirSize(sub);
                        list.Add(g);
                    }
                }
                catch { }
            }
        }
    }

    // ------------------------------------------------------------------ game mode engine
    public class GmMode
    {
        public string Id, Title, Detail;
        public bool CloseApps, ExtraApps, PauseSvcs, AllSvcs, LightSvcs, Purge, Gpu, LowerBg, Quiet, ObsUp;
        public string Boost = "";     // "" = leave alone, "High"
    }

    public static class GameMode
    {
        public static bool Active;
        public static DateTime Since;
        public static string Summary = "";
        public static string LastReport = Settings.Get("gm_last", "");
        public static event Action Changed;
        public static event Action FeedChanged;

        static readonly HashSet<string> Guard = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer","dwm","winlogon","csrss","wininit","services","lsass","svchost","system","smss","fontdrvhost","audiodg","sihost","taskhostw",
            "obs64","obs32","obs","streamlabs obs","streamlabs desktop","tslgame","valorant-win64-shipping","vgc","vgtray","beservice","easyanticheat",
            "processlasso","processgovernor","insightsservice","ixc-pc-ready","powershell","powershell_ise","pwsh","cmd","windowsterminal","wt","openconsole","taskmgr","mmc","regedit","discord","discordptb","discordcanary","wavelink","elgato","streamdeck","voicemeeter","voicemeeterpro","rtss","msiafterburner","radeonsoftware","amdrsserv","cncmd","dllhost","conhost","ctfmon","searchhost","startmenuexperiencehost","shellexperiencehost","textinputhost","runtimebroker","applicationframehost","securityhealthsystray","claude"
        };

        public static bool IsGuarded(string name) { return Guard.Contains(name); }

        public static readonly string[] DefaultApps = new string[] { "chrome", "msedge", "brave", "firefox", "opera", "OneDrive", "Teams", "ms-teams", "Spotify", "Slack", "Skype", "Zoom", "Notion", "Dropbox" };
        static readonly string[] HeavyExtra = new string[] { "Widgets", "PhoneExperienceHost", "Telegram", "WhatsApp" };
        static readonly string[] LowerNames = new string[] { "steamwebhelper", "RiotClientServices", "RiotClientUx", "RiotClientUxRender", "EpicWebHelper", "EpicGamesLauncher", "chrome", "msedge", "brave", "firefox", "opera" };
        static readonly string[] LightSvcNames = new string[] { "DiagTrack", "WerSvc", "MapsBroker", "DoSvc" };

        public class SvcInfo { public string Name, Title, Why; public bool DefaultOn; }
        public static readonly SvcInfo[] Services = new SvcInfo[]
        {
            new SvcInfo { Name = "WSearch", Title = "Windows Search indexer", Why = "Stops file indexing (disk and CPU use) while you play.", DefaultOn = true },
            new SvcInfo { Name = "SysMain", Title = "SysMain (Superfetch)", Why = "Stops background memory prefetching.", DefaultOn = true },
            new SvcInfo { Name = "DiagTrack", Title = "Connected User Experiences and Telemetry", Why = "Stops telemetry uploads.", DefaultOn = true },
            new SvcInfo { Name = "DoSvc", Title = "Delivery Optimization", Why = "Stops Windows sharing update files over your network.", DefaultOn = true },
            new SvcInfo { Name = "BITS", Title = "Background Intelligent Transfer", Why = "Stops background downloads that compete with your game's network traffic.", DefaultOn = true },
            new SvcInfo { Name = "wuauserv", Title = "Windows Update", Why = "Pauses update downloads until the session ends (it is started again afterwards).", DefaultOn = false },
            new SvcInfo { Name = "WerSvc", Title = "Windows Error Reporting", Why = "Stops crash-report uploads during play.", DefaultOn = true },
            new SvcInfo { Name = "MapsBroker", Title = "Downloaded Maps Manager", Why = "Rarely needed; stops map updates.", DefaultOn = true },
            new SvcInfo { Name = "Spooler", Title = "Print Spooler", Why = "Only pause this if you are not printing.", DefaultOn = false }
        };

        public static readonly GmMode[] Modes = new GmMode[]
        {
            new GmMode { Id = "competitive", Title = "Competitive", Detail = "Everything that helps frame time and input: closes your chosen apps, pauses background services, frees memory, game to High priority and the fast GPU, launchers and browsers to Below Normal, notifications silenced.",
                CloseApps = true, PauseSvcs = true, Purge = true, Gpu = true, LowerBg = true, Quiet = true, Boost = "High" },
            new GmMode { Id = "streaming", Title = "Streaming", Detail = "Keeps OBS and Discord safe: the game gets High priority, OBS gets Above Normal so the encoder is not starved, only light services are paused, nothing is purged mid-stream.",
                CloseApps = true, LightSvcs = true, Gpu = true, LowerBg = true, Quiet = true, ObsUp = true, Boost = "High" },
            new GmMode { Id = "heavy", Title = "Heavy games", Detail = "Competitive plus more: closes extra background apps (Widgets, Phone Link, chat apps), pauses every optional service including Windows Update, and clears memory twice. For big open-world games on 16 GB.",
                CloseApps = true, ExtraApps = true, PauseSvcs = true, AllSvcs = true, Purge = true, Gpu = true, LowerBg = true, Quiet = true, Boost = "High" },
            new GmMode { Id = "silent", Title = "Silent / battery", Detail = "Gentle: nothing is closed or boosted. Light services paused, launchers and browsers lowered, notifications silenced. Good on a laptop or when you want less heat and fan noise.",
                LightSvcs = true, LowerBg = true, Quiet = true },
            new GmMode { Id = "custom", Title = "Custom", Detail = "Exactly the apps, services and options you choose below.", CloseApps = true, PauseSvcs = true }
        };

        public static GmMode Selected
        {
            get
            {
                string id = Settings.Get("gm_mode", "competitive");
                foreach (GmMode m in Modes) if (m.Id == id) return m;
                return Modes[0];
            }
        }

        /// <summary>The effective settings of a mode. Custom reads the switches on the page.</summary>
        public static GmMode Effective(GmMode m)
        {
            if (m.Id != "custom") return m;
            GmMode c = new GmMode { Id = m.Id, Title = m.Title, Detail = m.Detail, CloseApps = true, PauseSvcs = true };
            c.Purge = Settings.GetBool("gm_purge", true);
            c.Gpu = Settings.GetBool("gm_gpu", true);
            c.Boost = Settings.GetBool("gm_prio", true) ? "High" : "";
            return c;
        }

        public static List<string> AppsFor(GmMode m)
        {
            List<string> l = new List<string>();
            if (m.CloseApps) l.AddRange(SavedApps());
            if (m.ExtraApps) foreach (string s in HeavyExtra) if (!l.Contains(s, StringComparer.OrdinalIgnoreCase)) l.Add(s);
            return l.Where(x => !IsGuarded(x)).ToList();
        }

        public static List<string> SvcsFor(GmMode m)
        {
            List<string> l = new List<string>();
            if (m.AllSvcs) l.AddRange(Services.Where(s => s.Name != "Spooler").Select(s => s.Name));
            else if (m.PauseSvcs) l.AddRange(SavedSvcs());
            else if (m.LightSvcs) l.AddRange(LightSvcNames);
            return l.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Plain-language list of what a mode will do, shown before it is started.</summary>
        public static List<string> Describe(GmMode raw)
        {
            GmMode m = Effective(raw);
            List<string> l = new List<string>();
            List<string> apps = AppsFor(m);
            if (apps.Count > 0) l.Add("Close: " + string.Join(", ", apps.ToArray()) + "  (only the ones that are running)");
            List<string> sv = SvcsFor(m).Where(SvcExists).ToList();
            if (sv.Count > 0) l.Add("Pause services: " + string.Join(", ", sv.ToArray()) + "  (started again when it ends)");
            if (m.Purge) l.Add("Empty standby and idle memory so the game starts with free RAM");
            if (m.Boost == "High") l.Add("Set the game's CPU priority to High (the game only, never anti-cheat)");
            if (m.ObsUp) l.Add("Set OBS to Above Normal priority");
            if (m.Gpu) l.Add("Set the game's Windows graphics preference to High performance");
            if (m.LowerBg) l.Add("Lower Steam/Riot/Epic helpers and browsers to Below Normal priority");
            if (m.Quiet) l.Add("Turn app notification pop-ups off, back on afterwards");
            if (l.Count == 0) l.Add("Nothing will be changed.");
            return l;
        }

        public static List<string> SavedApps()
        {
            string v = Settings.Get("gm_apps", null);
            if (v == null) return new List<string>(DefaultApps);
            return v.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        }
        public static void SaveApps(IEnumerable<string> l) { Settings.Set("gm_apps", string.Join(",", l.ToArray())); }

        public static List<string> SavedSvcs()
        {
            string v = Settings.Get("gm_svcs", null);
            if (v == null) return Services.Where(s => s.DefaultOn).Select(s => s.Name).ToList();
            return v.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        }
        public static void SaveSvcs(IEnumerable<string> l) { Settings.Set("gm_svcs", string.Join(",", l.ToArray())); }

        public static bool SvcRunning(string name)
        {
            try
            {
                using (System.ServiceProcess.ServiceController sc = new System.ServiceProcess.ServiceController(name))
                    return sc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
            }
            catch { return false; }
        }
        public static bool SvcExists(string name)
        {
            try
            {
                using (System.ServiceProcess.ServiceController sc = new System.ServiceProcess.ServiceController(name))
                { System.ServiceProcess.ServiceControllerStatus st = sc.Status; return true; }
            }
            catch { return false; }
        }

        // ---- live feed: every action is written here so the page can show it
        static readonly List<string> _feed = new List<string>();
        static readonly object _fl = new object();
        public static void Say(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "   " + msg;
            lock (_fl) { _feed.Add(line); if (_feed.Count > 300) _feed.RemoveRange(0, 100); }
            Log.Info("GameMode: " + msg);
            Action a = FeedChanged; if (a != null) a();
        }
        public static string FeedText(int max)
        {
            lock (_fl)
            {
                IEnumerable<string> l = _feed.Count > max ? _feed.Skip(_feed.Count - max) : _feed;
                return string.Join("\r\n", l.ToArray());
            }
        }
        public static int FeedCount { get { lock (_fl) return _feed.Count; } }

        // ---- game detection
        public class Detected { public string Name, Path; public int Pid; public DateTime At; }
        static readonly Dictionary<int, Detected> _live = new Dictionary<int, Detected>();
        static readonly Dictionary<int, bool> _seenPid = new Dictionary<int, bool>();
        static System.Threading.Thread _poll;
        static System.Management.ManagementEventWatcher _watch;
        public static string DetectorNote = "starting";
        public static Detected CurrentGame
        {
            get { lock (_live) { return _live.Values.OrderByDescending(d => d.At).FirstOrDefault(); } }
        }
        public static int LiveCount { get { lock (_live) return _live.Count; } }

        public static void StartWatcher()
        {
            if (_poll != null) return;
            // instant path: Windows tells us the moment any process starts
            try
            {
                _watch = new System.Management.ManagementEventWatcher(new System.Management.WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
                _watch.EventArrived += delegate(object o, System.Management.EventArrivedEventArgs e)
                {
                    try
                    {
                        int pid = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);
                        string name = Convert.ToString(e.NewEvent.Properties["ProcessName"].Value);
                        if (name != null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                        Inspect(pid, name);
                    }
                    catch { }
                };
                _watch.Start();
                DetectorNote = "instant (process start events) + 1 s scan";
            }
            catch (Exception ex) { DetectorNote = "1 second scan"; Log.Err("process watcher", ex); }
            // safety net: scan every second (also notices games that exit)
            _poll = new System.Threading.Thread(delegate()
            {
                int goneTicks = 0;
                while (true)
                {
                    try
                    {
                        HashSet<int> alive = new HashSet<int>();
                        Process[] all = Process.GetProcesses();
                        foreach (Process p in all)
                        {
                            try { alive.Add(p.Id); bool seen; lock (_seenPid) seen = _seenPid.ContainsKey(p.Id); if (!seen) Inspect(p.Id, p.ProcessName); }
                            catch { }
                            finally { p.Dispose(); }
                        }
                        List<Detected> gone = new List<Detected>();
                        lock (_live) { foreach (Detected d in _live.Values.ToList()) if (!alive.Contains(d.Pid)) { gone.Add(d); _live.Remove(d.Pid); } }
                        lock (_seenPid) { foreach (int k in _seenPid.Keys.ToList()) if (!alive.Contains(k)) _seenPid.Remove(k); }
                        foreach (Detected d in gone) Say("Game closed: " + d.Name + " (ran " + (DateTime.Now - d.At).ToString(@"hh\:mm\:ss") + ")");
                        if (gone.Count > 0) { Action c = Changed; if (c != null) c(); }
                        if (Active && LiveCount == 0 && _sawGame && Settings.GetBool("gm_auto", true))
                        {
                            goneTicks++;
                            if (goneTicks == 1) Say("No game is running. Ending Game Mode in 6 seconds unless a game starts again.");
                            if (goneTicks >= 6) { goneTicks = 0; End(); }
                        }
                        else goneTicks = 0;
                    }
                    catch (Exception ex) { Log.Err("game poll", ex); }
                    System.Threading.Thread.Sleep(LiveCount > 0 ? 2000 : 1000);
                }
            });
            _poll.IsBackground = true;
            _poll.Name = "ixc-game-detect";
            _poll.Start();
        }

        static void Inspect(int pid, string name)
        {
            lock (_seenPid) { if (_seenPid.ContainsKey(pid)) return; _seenPid[pid] = true; }
            if (string.IsNullOrEmpty(name)) return;
            string n = name;
            bool game = SysMonitor.IsGameName(n);
            string path = null;
            if (!game)
            {
                // path lookups are cheap but skip obvious Windows processes
                if (n.Equals("svchost", StringComparison.OrdinalIgnoreCase) || n.Equals("conhost", StringComparison.OrdinalIgnoreCase) || n.Equals("System", StringComparison.OrdinalIgnoreCase)) return;
                path = Native.ProcPath(pid);
                game = path != null && GameLib.IsGamePath(path);
                // helper tools living in a game's folder (crash handlers, launchers, anti-cheat) are not the game
                if (game && (n.IndexOf("crash", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("launcher", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("redist", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("unins", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("easyanticheat", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("beservice", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("helper", StringComparison.OrdinalIgnoreCase) >= 0)) game = false;
            }
            else path = Native.ProcPath(pid);
            if (!game) return;
            Detected d = new Detected { Name = n, Pid = pid, Path = path, At = DateTime.Now };
            lock (_live) { _live[pid] = d; }
            Say("Detected game: " + n + " (process " + pid + (path != null ? ", " + path : "") + ")");
            _sawGame = true;
            Action c = Changed; if (c != null) c();
            if (Active) { ApplyToGame(d); }
            else if (Settings.GetBool("gm_autostart", false))
            {
                Say("Auto-start is on: starting " + Selected.Title + " mode");
                System.Threading.ThreadPool.QueueUserWorkItem(delegate { Start(); });
            }
        }

        // ---- the mode itself
        static readonly object _modeLock = new object();
        static bool _sawGame;
        static GmMode _run;
        static readonly HashSet<int> _boosted = new HashSet<int>();
        static List<string> _closedNames = new List<string>();
        static List<string> _stoppedNames = new List<string>();
        static double _freedMb;
        static string _peakName = ""; static double _peakCpu, _peakGpu, _peakRam, _peakVram;

        public static void RestoreIfLeftOver()
        {
            // if the app was closed while Game Mode was active, give services and notifications back
            try
            {
                string left = Settings.Get("gm_paused", "");
                if (left.Length > 0 && !Active)
                {
                    foreach (string s in left.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        Util.Run("net.exe", "start \"" + s + "\"", 30000);
                    Settings.Set("gm_paused", "");
                    Log.Info("Restored leftover paused services: " + left);
                }
                RestoreToast();
            }
            catch (Exception ex) { Log.Err(ex); }
        }

        const string ToastKey = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
        static void QuietOn()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(ToastKey))
                {
                    object cur = k.GetValue("ToastEnabled");
                    if (Settings.Get("gm_toast_bk", "") == "") Settings.Set("gm_toast_bk", cur == null ? "ABSENT" : "V:" + cur);
                    k.SetValue("ToastEnabled", 0, RegistryValueKind.DWord);
                    Say("Notifications: pop-ups turned off (was " + (cur == null ? "default on" : cur.ToString() == "0" ? "off" : "on") + ")");
                }
            }
            catch (Exception ex) { Say("Notifications: could not change (" + ex.Message + ")"); }
        }
        static void RestoreToast()
        {
            string bk = Settings.Get("gm_toast_bk", "");
            if (bk == "") return;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(ToastKey))
                {
                    if (bk == "ABSENT") k.DeleteValue("ToastEnabled", false);
                    else k.SetValue("ToastEnabled", int.Parse(bk.Substring(2)), RegistryValueKind.DWord);
                }
                Settings.Set("gm_toast_bk", "");
                Say("Notifications: put back to how they were");
            }
            catch (Exception ex) { Say("Notifications: could not restore (" + ex.Message + ")"); }
        }

        /// <summary>Starts the selected mode. Safe to call from any thread.</summary>
        public static string Start()
        {
            lock (_modeLock)
            {
                if (Active) return "Game Mode is already on.";
                GmMode m = Effective(Selected);
                _run = m;
                _closedNames.Clear(); _stoppedNames.Clear(); _boosted.Clear(); _freedMb = 0;
                _peakName = ""; _peakCpu = _peakGpu = _peakRam = _peakVram = 0;
                Say("Starting " + m.Title + " mode");
                double before = AvailMB();

                // 1. close chosen apps
                foreach (string name in AppsFor(m))
                {
                    try
                    {
                        Process[] ps = Process.GetProcessesByName(name);
                        if (ps.Length == 0) continue;
                        double mb = 0;
                        foreach (Process p in ps) { try { mb += p.WorkingSet64 / 1048576.0; } catch { } }
                        Say("Closing " + name + " (" + ps.Length + " process" + (ps.Length == 1 ? "" : "es") + ", " + Util.MB(mb) + ")");
                        foreach (Process p in ps) { try { p.CloseMainWindow(); } catch { } }
                        for (int i = 0; i < 8; i++) { System.Threading.Thread.Sleep(250); if (ps.All(p => { try { return p.HasExited; } catch { return true; } })) break; }
                        bool forced = false;
                        foreach (Process p in ps) { try { if (!p.HasExited) { p.Kill(); forced = true; } } catch { } }
                        bool still = Process.GetProcessesByName(name).Length > 0;
                        foreach (Process p in ps) p.Dispose();
                        if (still) Say("   " + name + " is still running (it may restart itself)");
                        else { _closedNames.Add(name); Say("   closed" + (forced ? " (had to force it)" : "")); }
                    }
                    catch (Exception ex) { Say("   could not close " + name + ": " + ex.Message); }
                }

                // 2. pause chosen services
                List<string> stopped = new List<string>();
                foreach (string svc in SvcsFor(m))
                {
                    if (!SvcExists(svc) || !SvcRunning(svc)) continue;
                    Say("Pausing service " + svc);
                    Util.Run("net.exe", "stop \"" + svc + "\" /y", 30000);
                    if (!SvcRunning(svc)) { stopped.Add(svc); Say("   stopped"); }
                    else Say("   could not stop it (Windows keeps it running)");
                }
                _stoppedNames = stopped;
                Settings.Set("gm_paused", string.Join(",", stopped.ToArray()));

                // 3. memory
                if (m.Purge)
                {
                    Native.MemoryCommand(2);
                    Native.MemoryCommand(4);
                    if (m.AllSvcs) { System.Threading.Thread.Sleep(1500); Native.MemoryCommand(4); }
                    _freedMb = Math.Max(0, AvailMB() - before);
                    Say("Memory: emptied standby and idle RAM. Free RAM went up by " + Util.MB(_freedMb));
                }

                // 4. lower background launchers/browsers
                if (m.LowerBg)
                {
                    int n = 0;
                    foreach (string name in LowerNames)
                    {
                        foreach (Process p in Process.GetProcessesByName(name))
                        {
                            try { if (p.PriorityClass != ProcessPriorityClass.BelowNormal) { p.PriorityClass = ProcessPriorityClass.BelowNormal; n++; } } catch { }
                            finally { p.Dispose(); }
                        }
                    }
                    Say(n > 0 ? "Priority: lowered " + n + " launcher/browser processes to Below Normal" : "Priority: no launcher or browser processes needed lowering");
                }

                // 5. OBS
                if (m.ObsUp)
                {
                    int n = 0;
                    foreach (string name in new string[] { "obs64", "obs32", "streamlabs desktop" })
                        foreach (Process p in Process.GetProcessesByName(name))
                        {
                            try { p.PriorityClass = ProcessPriorityClass.AboveNormal; n++; } catch { }
                            finally { p.Dispose(); }
                        }
                    Say(n > 0 ? "Priority: OBS set to Above Normal" : "OBS is not running (it will not be touched later either)");
                }

                // 6. notifications
                if (m.Quiet) QuietOn();

                Active = true;
                Since = DateTime.Now;
                Summary = "Closed " + _closedNames.Count + " app" + (_closedNames.Count == 1 ? "" : "s") + ", paused " + stopped.Count + " service" + (stopped.Count == 1 ? "" : "s") + (_freedMb > 0 ? ", freed " + Util.MB(_freedMb) + " of RAM" : "") + ".";
                Settings.SetBool("gm_active", true);
                Say("Game Mode is on. " + Summary);
                Detected cur = CurrentGame;
                if (cur != null) ApplyToGame(cur); else Say("Waiting for a game to start...");
                Action c = Changed; if (c != null) c();
                return Summary;
            }
        }

        static void ApplyToGame(Detected d)
        {
            GmMode m = _run ?? Effective(Selected);
            lock (_boosted) { if (_boosted.Contains(d.Pid)) return; _boosted.Add(d.Pid); }
            if (m.Boost == "High")
            {
                int ok = 0; string back = "";
                foreach (Process p in Process.GetProcessesByName(d.Name))
                {
                    try { p.PriorityClass = ProcessPriorityClass.High; p.Refresh(); back = p.PriorityClass.ToString(); ok++; } catch { }
                    finally { p.Dispose(); }
                }
                Say(ok > 0 ? "Priority: " + d.Name + " set to High (read back: " + back + ")" : "Priority: could not change " + d.Name + " (Windows refused)");
            }
            if (m.Gpu && !string.IsNullOrEmpty(d.Path))
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences"))
                    {
                        object cur = k.GetValue(d.Path);
                        if (cur == null) { k.SetValue(d.Path, "GpuPreference=2;", RegistryValueKind.String); Say("Graphics: " + d.Name + " set to High performance GPU (this Windows setting stays after the game closes)"); }
                        else Say("Graphics: " + d.Name + " already has a GPU preference (" + cur + "), left as it is");
                    }
                }
                catch (Exception ex) { Say("Graphics: could not set GPU preference (" + ex.Message + ")"); }
            }
        }

        public static string End()
        {
            lock (_modeLock)
            {
                if (!Active) return "Game Mode is off.";
                Say("Ending Game Mode");
                int n = 0;
                string left = Settings.Get("gm_paused", "");
                foreach (string s in left.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    Util.Run("net.exe", "start \"" + s + "\"", 30000);
                    Say("Service " + s + (SvcRunning(s) ? " started again" : " could not be started (it will start on demand)"));
                    n++;
                }
                Settings.Set("gm_paused", "");
                RestoreToast();
                TimeSpan dur = DateTime.Now - Since;
                StringBuilder r = new StringBuilder();
                r.AppendLine("Last session: " + Since.ToString("dd MMM HH:mm") + ", " + _run.Title + " mode, lasted " + dur.ToString(@"hh\:mm\:ss"));
                r.AppendLine(_peakName.Length > 0 ? "Game: " + _peakName + "  -  peak CPU " + _peakCpu.ToString("0") + "%, peak GPU " + _peakGpu.ToString("0") + "%, peak RAM " + Util.MB(_peakRam) + (_peakVram > 1 ? ", peak VRAM " + Util.MB(_peakVram) : "") + " (measured while it ran)" : "No game was detected during this session.");
                r.AppendLine("Closed: " + (_closedNames.Count == 0 ? "nothing" : string.Join(", ", _closedNames.ToArray())));
                r.AppendLine("Paused services: " + (_stoppedNames.Count == 0 ? "none" : string.Join(", ", _stoppedNames.ToArray())) + "  (all started again)");
                r.Append("RAM freed at start: " + (_freedMb > 0 ? Util.MB(_freedMb) : "n/a"));
                LastReport = r.ToString();
                Settings.Set("gm_last", LastReport);
                Active = false;
                _sawGame = false;
                Summary = "Restored " + n + " service" + (n == 1 ? "" : "s") + ".";
                Settings.SetBool("gm_active", false);
                Say("Game Mode is off. " + Summary);
                Action c = Changed; if (c != null) c();
                return Summary;
            }
        }

        public static double AvailMB()
        {
            try
            {
                using (PerformanceCounter pc = new PerformanceCounter("Memory", "Available MBytes"))
                    return pc.NextValue();
            }
            catch { return 0; }
        }

        /// <summary>Called from the main sample loop: records real peaks of the running game for the session report.</summary>
        public static bool Tick(SystemSample s)
        {
            if (!Active) return false;
            ProcRow g = s.Rows.Where(r => r.IsGame).OrderByDescending(r => r.Gpu + r.Cpu).FirstOrDefault();
            if (g == null) return false;
            if (_peakName != g.Name) { _peakName = g.Name; _peakCpu = _peakGpu = _peakRam = _peakVram = 0; }
            _peakCpu = Math.Max(_peakCpu, g.Cpu); _peakGpu = Math.Max(_peakGpu, g.Gpu); _peakRam = Math.Max(_peakRam, g.RamMB); _peakVram = Math.Max(_peakVram, g.VramMB);
            return false;
        }
    }

    // ------------------------------------------------------------------ page
    public class GamePage : PageBase
    {
        TextBlock _state, _detail, _live, _plan, _report;
        ScrollViewer _feedScroll;
        TextBlock _feed;
        Button _toggle;
        StackPanel _appsPanel, _gamesPanel;
        TextBox _addBox;
        bool _busy;
        HashSet<string> _selApps;
        HardwareInfo _hw;
        List<RadioButton> _modeBtns = new List<RadioButton>();

        public GamePage(HardwareInfo hw) : base("Game Mode", "One switch before you play. It finds your game the moment it starts and tells you every single thing it does.")
        {
            _hw = hw;
            _selApps = new HashSet<string>(GameMode.SavedApps(), StringComparer.OrdinalIgnoreCase);

            // hero
            Grid hero = new Grid();
            hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel hs = new StackPanel();
            _state = UK.T("", "H1");
            _detail = UK.T("", "Muted");
            _detail.Margin = new Thickness(0, 4, 0, 0);
            _live = UK.T("", "Body");
            _live.Margin = new Thickness(0, 10, 0, 0);
            hs.Children.Add(_state); hs.Children.Add(_detail); hs.Children.Add(_live);
            hero.Children.Add(hs);
            _toggle = UK.Btn("Start Game Mode", true, delegate { Toggle(); });
            _toggle.Margin = new Thickness(20, 0, 0, 0);
            _toggle.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_toggle, 1);
            hero.Children.Add(_toggle);
            Body.Children.Add(UK.Card(hero));

            // modes
            StackPanel mp = new StackPanel();
            WrapPanel mb = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            foreach (GmMode M in GameMode.Modes)
            {
                GmMode mm = M;
                RadioButton rb = new RadioButton { Style = UK.S("Seg"), GroupName = "gmmode", Content = M.Title, Tag = M, Margin = new Thickness(0, 0, 0, 6), IsChecked = GameMode.Selected.Id == M.Id };
                rb.Click += delegate
                {
                    Settings.Set("gm_mode", mm.Id);
                    ShowPlan();
                    if (GameMode.Active) GameMode.Say("Mode " + mm.Title + " chosen. It takes effect the next time Game Mode starts.");
                };
                _modeBtns.Add(rb); mb.Children.Add(rb);
            }
            mp.Children.Add(mb);
            _plan = UK.T("", "Body");
            _plan.TextWrapping = TextWrapping.Wrap;
            mp.Children.Add(_plan);
            Body.Children.Add(UK.Section("Mode", "Pick what Game Mode does. The list below the buttons is exactly what it will do.", mp, true, null));

            // live feed
            StackPanel fp = new StackPanel();
            _feed = new TextBlock { FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12, Foreground = UK.Br("TextBrush"), TextWrapping = TextWrapping.Wrap };
            _feedScroll = new ScrollViewer { MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _feed };
            Border fb = new Border { Background = UK.Br("Card2Brush"), BorderBrush = UK.Br("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10), Child = _feedScroll };
            fp.Children.Add(fb);
            _report = UK.T("", "Muted");
            _report.Margin = new Thickness(0, 10, 0, 0);
            _report.TextWrapping = TextWrapping.Wrap;
            fp.Children.Add(_report);
            Body.Children.Add(UK.Section("What Game Mode is doing", "Live. Every game detected and every change appears here with the time.", fp, true, null));

            // automatic behaviour
            StackPanel au = new StackPanel();
            au.Children.Add(OptionRow("gm_autostart", false, "Start automatically when a game is detected", "Starts the chosen mode the moment a known game opens. Apps in your close list are shut down without asking, so leave this off if you keep unsaved work open."));
            au.Children.Add(OptionRow("gm_auto", true, "End automatically when the game closes", "Restores services and notifications about six seconds after the last game process exits."));
            Body.Children.Add(UK.Section("Automatic", "Detection is always on while this app runs: " + GameMode.DetectorNote + ".", au, false, null));

            // custom choices
            _appsPanel = new StackPanel();
            StackPanel cu = new StackPanel();
            cu.Children.Add(UK.T("Used by the Custom mode, and as the list of apps that Competitive, Streaming and Heavy close.", "Muted"));
            ((TextBlock)cu.Children[0]).Margin = new Thickness(0, 0, 0, 10);
            cu.Children.Add(UK.T("Apps to close", "H2"));
            cu.Children.Add(AppsContent());
            cu.Children.Add(UK.T("Services to pause", "H2"));
            ((TextBlock)cu.Children[cu.Children.Count - 1]).Margin = new Thickness(0, 14, 0, 4);
            StackPanel sv = new StackPanel();
            HashSet<string> selS = new HashSet<string>(GameMode.SavedSvcs(), StringComparer.OrdinalIgnoreCase);
            List<GameMode.SvcInfo> present = GameMode.Services.Where(x => GameMode.SvcExists(x.Name)).ToList();
            for (int i = 0; i < present.Count; i++)
            {
                GameMode.SvcInfo si = present[i];
                ToggleButton sw = UK.Sw(selS.Contains(si.Name));
                string nm = si.Name;
                sw.Click += delegate
                {
                    List<string> cur = GameMode.SavedSvcs();
                    cur.RemoveAll(x => x.Equals(nm, StringComparison.OrdinalIgnoreCase));
                    if (sw.IsChecked == true) cur.Add(nm);
                    GameMode.SaveSvcs(cur);
                    ShowPlan();
                };
                sv.Children.Add(UK.Row(si.Title, si.Why, UK.Right(GameMode.SvcRunning(si.Name) ? "running" : "stopped"), sw, i == present.Count - 1));
            }
            if (present.Count == 0) sv.Children.Add(UK.T("None of the optional services exist on this PC.", "Muted"));
            cu.Children.Add(sv);
            cu.Children.Add(UK.T("Options (Custom mode only)", "H2"));
            ((TextBlock)cu.Children[cu.Children.Count - 1]).Margin = new Thickness(0, 14, 0, 4);
            cu.Children.Add(OptionRow("gm_purge", true, "Free memory when starting", "Empties standby and idle memory so the game gets clean RAM."));
            cu.Children.Add(OptionRow("gm_prio", true, "Raise the game's CPU priority", "Sets High priority on the detected game."));
            cu.Children.Add(OptionRow("gm_gpu", true, "Prefer the high-performance GPU", "Sets the Windows graphics preference of the game to High performance."));
            Body.Children.Add(UK.Section("Custom choices", "Apps, services and options", cu, false, null));

            // games
            _gamesPanel = new StackPanel();
            Body.Children.Add(UK.Section("Games on this PC", "Detected from Steam, Epic and Riot. Add any other game by its program name.", GamesContent(), false, null));

            ShowPlan();
            RefreshState();
            RefreshFeed();
            GameMode.Changed += delegate { UK.UI(delegate { RefreshState(); }); };
            GameMode.FeedChanged += delegate { UK.UI(delegate { RefreshFeed(); }); };
        }

        void ShowPlan()
        {
            GmMode m = GameMode.Selected;
            foreach (RadioButton rb in _modeBtns) rb.IsChecked = ((GmMode)rb.Tag).Id == m.Id;
            _plan.Text = m.Detail + "\r\n\r\nWhat it will do:\r\n  - " + string.Join("\r\n  - ", GameMode.Describe(m).ToArray());
        }

        void RefreshFeed()
        {
            string t = GameMode.FeedText(120);
            _feed.Text = t.Length == 0 ? "Nothing yet. Start Game Mode, or start a game, and the steps appear here." : t;
            _feedScroll.ScrollToEnd();
        }

        Border OptionRow(string key, bool def, string title, string why)
        {
            ToggleButton sw = UK.Sw(Settings.GetBool(key, def));
            string k = key;
            sw.Click += delegate { Settings.SetBool(k, sw.IsChecked == true); if (k.StartsWith("gm_p") || k.StartsWith("gm_g")) ShowPlan(); };
            return UK.Row(title, why, null, sw, false);
        }

        UIElement AppsContent()
        {
            StackPanel root = new StackPanel();
            StackPanel btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            btns.Children.Add(UK.Btn("Refresh list", false, delegate { FillApps(); }));
            root.Children.Add(btns);
            root.Children.Add(_appsPanel);
            FillApps();
            return root;
        }

        void FillApps()
        {
            _appsPanel.Children.Clear();
            Dictionary<string, double> mem = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            string self = Process.GetCurrentProcess().ProcessName;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    string n = p.ProcessName;
                    if (GameMode.IsGuarded(n) || n.Equals(self, StringComparison.OrdinalIgnoreCase)) continue;
                    if (SysMonitor.KnownGames.Contains(n)) continue;
                    double m = p.WorkingSet64 / 1048576.0;
                    double cur; mem.TryGetValue(n, out cur); mem[n] = cur + m;
                }
                catch { }
                finally { p.Dispose(); }
            }
            // saved choices are always listed, running or not, so they can be unticked
            foreach (string n in _selApps)
            {
                if (mem.ContainsKey(n) || GameMode.IsGuarded(n)) continue;
                double m = 0;
                foreach (Process p in Process.GetProcessesByName(n)) { try { m += p.WorkingSet64 / 1048576.0; } catch { } p.Dispose(); }
                mem[n] = m;
            }
            List<KeyValuePair<string, double>> list = mem.OrderByDescending(x => _selApps.Contains(x.Key) ? 1 : 0).ThenByDescending(x => x.Value).ToList();
            if (list.Count == 0) _appsPanel.Children.Add(UK.T("No other apps with windows are open right now.", "Muted"));
            for (int i = 0; i < list.Count; i++)
            {
                string name = list[i].Key;
                ToggleButton sw = UK.Sw(_selApps.Contains(name));
                sw.Click += delegate
                {
                    if (sw.IsChecked == true) _selApps.Add(name); else _selApps.Remove(name);
                    GameMode.SaveApps(_selApps);
                    ShowPlan();
                };
                _appsPanel.Children.Add(UK.Row(name, null, UK.Right(list[i].Value > 0 ? Util.MB(list[i].Value) : "not running"), sw, i == list.Count - 1));
            }
        }

        UIElement GamesContent()
        {
            StackPanel root = new StackPanel();
            StackPanel add = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            _addBox = new TextBox { Style = UK.S("Box"), Width = 260, Height = 34 };
            add.Children.Add(_addBox);
            Button b = UK.Btn("Add game", false, delegate { AddCustom(); });
            b.Margin = new Thickness(8, 0, 0, 0);
            add.Children.Add(b);
            add.Children.Add(UK.T("  program name without .exe, e.g. TslGame", "Muted"));
            ((TextBlock)add.Children[2]).VerticalAlignment = VerticalAlignment.Center;
            root.Children.Add(add);
            root.Children.Add(_gamesPanel);
            FillGames();
            return root;
        }

        void AddCustom()
        {
            string n = (_addBox.Text ?? "").Trim();
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            if (n.Length == 0) return;
            string cur = Settings.Get("custom_games", "");
            List<string> l = cur.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!l.Contains(n, StringComparer.OrdinalIgnoreCase)) l.Add(n);
            Settings.Set("custom_games", string.Join(",", l.ToArray()));
            SysMonitor.ReloadCustom();
            _addBox.Text = "";
            FillGames();
        }

        void FillGames()
        {
            _gamesPanel.Children.Clear();
            List<GameEntry> g = GameLib.All;
            for (int i = 0; i < g.Count; i++)
                _gamesPanel.Children.Add(UK.Row(g[i].Name, g[i].Launcher + (string.IsNullOrEmpty(g[i].Dir) ? "" : "  -  " + g[i].Dir), UK.Right(g[i].SizeBytes > 0 ? Util.Bytes(g[i].SizeBytes) : ""), null, false));
            string cur = Settings.Get("custom_games", "");
            foreach (string n in cur.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = n.Trim();
                Button rm = UK.Btn("Remove", false, null);
                rm.Margin = new Thickness(0);
                rm.Click += delegate
                {
                    List<string> l = Settings.Get("custom_games", "").Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Where(x => !x.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                    Settings.Set("custom_games", string.Join(",", l.ToArray()));
                    SysMonitor.ReloadCustom();
                    FillGames();
                };
                _gamesPanel.Children.Add(UK.Row(name, "Added by you", null, rm, false));
            }
            if (_gamesPanel.Children.Count == 0) _gamesPanel.Children.Add(UK.T("No games found yet. Add one above, or start a game and it is picked up automatically.", "Muted"));
        }

        void Toggle()
        {
            if (_busy) return;
            if (!GameMode.Active)
            {
                GmMode m = GameMode.Effective(GameMode.Selected);
                List<string> running = new List<string>();
                foreach (string n in GameMode.AppsFor(m)) if (Process.GetProcessesByName(n).Length > 0) running.Add(n);
                if (running.Count > 0)
                {
                    MessageBoxResult r = Dlg.Show(m.Title + " mode will close these apps:\n\n  " + string.Join("\n  ", running.ToArray()) + "\n\nSave your work first. Continue?", Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes) return;
                }
            }
            _busy = true;
            _toggle.IsEnabled = false;
            _state.Text = GameMode.Active ? "Ending..." : "Starting...";
            bool starting = !GameMode.Active;
            UK.Bg(delegate { return starting ? GameMode.Start() : GameMode.End(); }, delegate(object res)
            {
                _busy = false;
                _toggle.IsEnabled = true;
                RefreshState();
            });
        }

        public void RefreshState()
        {
            GmMode m = GameMode.Selected;
            if (GameMode.Active)
            {
                _state.Text = "Game Mode is ON  -  " + m.Title;
                _state.Foreground = UK.Br("GoodBrush");
                _detail.Text = GameMode.Summary + "  Since " + GameMode.Since.ToString("HH:mm") + ".";
                _toggle.Content = "End Game Mode";
                _toggle.Style = UK.S("GhostBtn");
            }
            else
            {
                _state.Text = "Game Mode is off";
                _state.Foreground = UK.Br("TextBrush");
                _detail.Text = string.IsNullOrEmpty(GameMode.Summary) ? "Chosen mode: " + m.Title + ". Start it right before you launch a game. It never touches your game's anti-cheat, OBS or Discord." : GameMode.Summary;
                _toggle.Content = "Start " + m.Title;
                _toggle.Style = UK.S("PrimaryBtn");
            }
            _report.Text = GameMode.LastReport;
            _report.Visibility = string.IsNullOrEmpty(GameMode.LastReport) ? Visibility.Collapsed : Visibility.Visible;
        }

        public override void Update(SystemSample s)
        {
            GameMode.Detected d = GameMode.CurrentGame;
            List<ProcRow> g = s.Rows.Where(r => r.IsGame).OrderByDescending(r => r.Gpu + r.Cpu).ToList();
            if (g.Count == 0) _live.Text = d == null ? "No game detected. Watching: " + GameMode.DetectorNote + "." : "Detected: " + d.Name;
            else
            {
                ProcRow r = g[0];
                _live.Text = "Game: " + r.Name + "   CPU " + r.Cpu.ToString("0.0") + "%   GPU " + r.Gpu.ToString("0.0") + "%   RAM " + Util.MB(r.RamMB) + (r.VramMB > 1 ? "   VRAM " + Util.MB(r.VramMB) : "");
            }
        }
    }
}
