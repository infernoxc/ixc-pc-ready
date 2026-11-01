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
    public static class GameMode
    {
        public static bool Active;
        public static DateTime Since;
        public static string Summary = "";
        public static event Action Changed;

        static readonly HashSet<string> Guard = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer","dwm","winlogon","csrss","wininit","services","lsass","svchost","system","smss","fontdrvhost","audiodg","sihost","taskhostw",
            "obs64","obs32","obs","streamlabs obs","streamlabs desktop","tslgame","valorant-win64-shipping","vgc","vgtray","beservice","easyanticheat",
            "processlasso","processgovernor","insightsservice","ixc-pc-ready","powershell","powershell_ise","pwsh","cmd","windowsterminal","wt","openconsole","taskmgr","mmc","regedit","discord","discordptb","discordcanary","wavelink","elgato","streamdeck","voicemeeter","voicemeeterpro","rtss","msiafterburner","radeonsoftware","amdrsserv","cncmd","dllhost","conhost","ctfmon","searchhost","startmenuexperiencehost","shellexperiencehost","textinputhost","runtimebroker","applicationframehost","securityhealthsystray","claude"
        };

        public static bool IsGuarded(string name) { return Guard.Contains(name); }

        public static readonly string[] DefaultApps = new string[] { "chrome", "msedge", "brave", "firefox", "opera", "OneDrive", "Teams", "ms-teams", "Spotify", "Slack", "Skype", "Zoom", "Notion", "Dropbox" };

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

        public static void RestoreIfLeftOver()
        {
            // if the app was closed while Game Mode was active, give services back
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
            }
            catch (Exception ex) { Log.Err(ex); }
        }

        /// <summary>Starts Game Mode using the saved selection. Runs on the calling thread (call from a worker).</summary>
        public static string Start()
        {
            if (Active) return "Game Mode is already on.";
            int closed = 0, paused = 0;
            long freedMb = 0;
            StringBuilder note = new StringBuilder();

            double before = AvailMB();

            // 1. close chosen apps
            foreach (string name in SavedApps())
            {
                if (IsGuarded(name)) continue;
                try
                {
                    Process[] ps = Process.GetProcessesByName(name);
                    if (ps.Length == 0) continue;
                    foreach (Process p in ps) { try { p.CloseMainWindow(); } catch { } }
                    System.Threading.Thread.Sleep(1500);
                    foreach (Process p in ps)
                    {
                        try { if (!p.HasExited) p.Kill(); } catch { }
                        finally { p.Dispose(); }
                    }
                    closed++;
                    Log.Info("GameMode closed " + name);
                }
                catch (Exception ex) { Log.Err("close " + name, ex); }
            }

            // 2. pause chosen services
            List<string> stopped = new List<string>();
            foreach (string svc in SavedSvcs())
            {
                if (!SvcExists(svc) || !SvcRunning(svc)) continue;
                Util.RunResult r = Util.Run("net.exe", "stop \"" + svc + "\" /y", 30000);
                if (!SvcRunning(svc)) { stopped.Add(svc); paused++; Log.Info("GameMode paused service " + svc); }
            }
            Settings.Set("gm_paused", string.Join(",", stopped.ToArray()));

            // 3. clear memory
            if (Settings.GetBool("gm_purge", true))
            {
                Native.MemoryCommand(2);
                Native.MemoryCommand(4);
            }
            freedMb = (long)Math.Max(0, AvailMB() - before);

            Active = true;
            Since = DateTime.Now;
            Summary = "Closed " + closed + " app" + (closed == 1 ? "" : "s") + ", paused " + paused + " service" + (paused == 1 ? "" : "s") + (freedMb > 0 ? ", freed " + Util.MB(freedMb) + " of RAM" : "") + ".";
            Settings.SetBool("gm_active", true);
            if (Changed != null) Changed();
            return Summary;
        }

        public static string End()
        {
            if (!Active) return "Game Mode is off.";
            string left = Settings.Get("gm_paused", "");
            int n = 0;
            foreach (string s in left.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Util.Run("net.exe", "start \"" + s + "\"", 30000);
                n++;
            }
            Settings.Set("gm_paused", "");
            Active = false;
            Summary = "Restored " + n + " service" + (n == 1 ? "" : "s") + ".";
            Settings.SetBool("gm_active", false);
            Log.Info("GameMode ended. " + Summary);
            if (Changed != null) Changed();
            return Summary;
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

        // boost any running game while the mode is on
        static Dictionary<int, bool> _boosted = new Dictionary<int, bool>();
        static int _miss;
        static bool _seenGame;

        public static bool Tick(SystemSample s)
        {
            if (!Active) { _boosted.Clear(); _miss = 0; _seenGame = false; return false; }
            List<ProcRow> games = s.Rows.Where(r => r.IsGame).ToList();
            if (games.Count > 0) { _seenGame = true; _miss = 0; } else _miss++;
            if (Settings.GetBool("gm_prio", true))
            {
                foreach (ProcRow g in games)
                {
                    if (_boosted.ContainsKey(g.Pid)) continue;
                    _boosted[g.Pid] = true;
                    foreach (Process p in Process.GetProcessesByName(g.Name))
                    {
                        try { p.PriorityClass = ProcessPriorityClass.High; } catch { }
                        finally { p.Dispose(); }
                    }
                    Log.Info("GameMode priority High -> " + g.Name);
                }
            }
            if (Settings.GetBool("gm_gpu", true))
            {
                foreach (ProcRow g in games)
                {
                    if (string.IsNullOrEmpty(g.Path)) continue;
                    try
                    {
                        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences"))
                        {
                            object cur = k.GetValue(g.Path);
                            if (cur == null) k.SetValue(g.Path, "GpuPreference=2;", RegistryValueKind.String);
                        }
                    }
                    catch { }
                }
            }
            // auto end once the game has been closed for ~6 s
            return Settings.GetBool("gm_auto", true) && _seenGame && _miss >= 4;
        }
    }

    // ------------------------------------------------------------------ page
    public class GamePage : PageBase
    {
        TextBlock _state, _detail, _live;
        Button _toggle;
        StackPanel _appsPanel, _gamesPanel;
        TextBox _addBox;
        bool _busy;
        HashSet<string> _selApps;
        HardwareInfo _hw;

        public GamePage(HardwareInfo hw) : base("Game Mode", "One switch before you play: close background apps, pause services you do not need, clear memory and boost your game.")
        {
            _hw = hw;
            _selApps = new HashSet<string>(GameMode.SavedApps(), StringComparer.OrdinalIgnoreCase);

            // hero card
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

            // apps to close
            _appsPanel = new StackPanel();
            Body.Children.Add(UK.Section("Apps to close", "Tick the programs Game Mode may shut down. Unsaved work in them is lost, so save first.", AppsContent(), true, null));

            // services
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
                };
                sv.Children.Add(UK.Row(si.Title, si.Why, UK.Right(GameMode.SvcRunning(si.Name) ? "running" : "stopped"), sw, i == present.Count - 1));
            }
            if (present.Count == 0) sv.Children.Add(UK.T("None of the optional services exist on this PC. Nothing to pause.", "Muted"));
            Body.Children.Add(UK.Section("Services to pause", "Stopped while Game Mode is on and started again when it ends.", sv, false, null));

            // options
            StackPanel op = new StackPanel();
            op.Children.Add(OptionRow("gm_purge", true, "Free memory when starting", "Empties standby and idle memory so the game gets clean RAM."));
            op.Children.Add(OptionRow("gm_prio", true, "Raise the game's CPU priority", "Sets High priority on any detected game while Game Mode is on."));
            op.Children.Add(OptionRow("gm_gpu", true, "Prefer the high-performance GPU", "Sets the Windows graphics preference of a running game to High performance."));
            Border last = OptionRow("gm_auto", true, "End automatically when the game closes", "Restores services a few seconds after your game exits.");
            op.Children.Add(last);
            Body.Children.Add(UK.Section("While you play", "These apply to any game Game Mode detects.", op, false, null));

            // games
            _gamesPanel = new StackPanel();
            Body.Children.Add(UK.Section("Games on this PC", "Detected from Steam, Epic and Riot. Add any other game by its program name.", GamesContent(), false, null));

            RefreshState();
            GameMode.Changed += delegate { UK.UI(delegate { RefreshState(); }); };
        }

        Border OptionRow(string key, bool def, string title, string why)
        {
            ToggleButton sw = UK.Sw(Settings.GetBool(key, def));
            string k = key;
            sw.Click += delegate { Settings.SetBool(k, sw.IsChecked == true); };
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
            // also show saved choices that are running without a window (e.g. OneDrive)
            foreach (string n in _selApps)
            {
                if (mem.ContainsKey(n) || GameMode.IsGuarded(n)) continue;
                Process[] ps = Process.GetProcessesByName(n);
                if (ps.Length == 0) continue;
                double m = 0;
                foreach (Process p in ps) { try { m += p.WorkingSet64 / 1048576.0; } catch { } p.Dispose(); }
                mem[n] = m;
            }
            List<KeyValuePair<string, double>> list = mem.OrderByDescending(x => x.Value).ToList();
            if (list.Count == 0) _appsPanel.Children.Add(UK.T("No other apps with windows are open right now.", "Muted"));
            for (int i = 0; i < list.Count; i++)
            {
                string name = list[i].Key;
                ToggleButton sw = UK.Sw(_selApps.Contains(name));
                sw.Click += delegate
                {
                    if (sw.IsChecked == true) _selApps.Add(name); else _selApps.Remove(name);
                    GameMode.SaveApps(_selApps);
                };
                _appsPanel.Children.Add(UK.Row(name, null, UK.Right(Util.MB(list[i].Value)), sw, i == list.Count - 1));
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
                List<string> running = new List<string>();
                foreach (string n in GameMode.SavedApps()) if (!GameMode.IsGuarded(n) && Process.GetProcessesByName(n).Length > 0) running.Add(n);
                if (running.Count > 0)
                {
                    MessageBoxResult r = MessageBox.Show("Game Mode will close these apps:\n\n  " + string.Join("\n  ", running.ToArray()) + "\n\nSave your work first. Continue?", Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question);
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
            if (GameMode.Active)
            {
                _state.Text = "Game Mode is ON";
                _state.Foreground = UK.Br("GoodBrush");
                _detail.Text = GameMode.Summary + "  Since " + GameMode.Since.ToString("HH:mm") + ".";
                _toggle.Content = "End Game Mode";
                _toggle.Style = UK.S("GhostBtn");
            }
            else
            {
                _state.Text = "Game Mode is off";
                _state.Foreground = UK.Br("TextBrush");
                _detail.Text = string.IsNullOrEmpty(GameMode.Summary) ? "Start it right before you launch a game. It will not touch your game, OBS or anti-cheat." : GameMode.Summary;
                _toggle.Content = "Start Game Mode";
                _toggle.Style = UK.S("PrimaryBtn");
            }
        }

        public override void Update(SystemSample s)
        {
            List<ProcRow> g = s.Rows.Where(r => r.IsGame).OrderByDescending(r => r.Gpu + r.Cpu).ToList();
            if (g.Count == 0) _live.Text = "No game running right now.";
            else
            {
                ProcRow r = g[0];
                _live.Text = r.Name + "   CPU " + r.Cpu.ToString("0.0") + "%   GPU " + r.Gpu.ToString("0.0") + "%   RAM " + Util.MB(r.RamMB) + (r.VramMB > 1 ? "   VRAM " + Util.MB(r.VramMB) : "");
            }
        }
    }
}
