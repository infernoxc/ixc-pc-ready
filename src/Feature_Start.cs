using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace InfernoXC
{
    public class StartItem
    {
        public string Name, Command, Source, Kind;   // Kind: Run, Folder, Task, Service
        public bool Enabled, Protected;
        public string Reason = "";
        public string Hive, Sub, ValueName, Approved; // registry / folder data
        public string TaskPath, SvcName;
    }

    public static class StartMgr
    {
        static readonly Regex ProtectedSvc = new Regex(@"vgc|vgk|beservice|battleye|easyanticheat|eaanticheat|faceit|vanguard|defender|msmpeng|nissrv|windefend|security|firewall|nvidia|nvdisplay|radeon|amd |amdfend|amdrs|realtek|intel\(r\)|wlan|bluetooth|audio|asus|gigabyte|msi |armoury", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ProtectedRun = new Regex(@"securityhealth|windowsdefender|vgtray|riot vanguard|realtek|nvidia|radeon|amd|rtkauduservice|igfx|processlasso|process lasso", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ---------------- registry Run keys and startup folders
        public static List<StartItem> RunItems()
        {
            List<StartItem> l = new List<StartItem>();
            AddRun(l, Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Current user");
            AddRun(l, Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "All users");
            AddRun(l, Registry.LocalMachine, "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", "All users (32-bit)");
            AddFolder(l, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, "HKCU", "Startup folder (you)");
            AddFolder(l, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, "HKLM", "Startup folder (all users)");
            return l;
        }

        static bool ApprovedEnabled(RegistryKey root, string approvedPath, string name)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(approvedPath))
                {
                    if (k == null) return true;
                    byte[] b = k.GetValue(name) as byte[];
                    if (b == null || b.Length == 0) return true;
                    return (b[0] & 1) == 0;
                }
            }
            catch { return true; }
        }

        static void AddRun(List<StartItem> l, RegistryKey root, string hive, string runPath, string approvedPath, string source)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(runPath))
                {
                    if (k == null) return;
                    foreach (string n in k.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(n)) continue;
                        StartItem s = new StartItem();
                        s.Kind = "Run"; s.Name = n; s.Command = Convert.ToString(k.GetValue(n));
                        s.Source = source; s.Hive = hive; s.ValueName = n; s.Approved = approvedPath;
                        s.Enabled = ApprovedEnabled(root, approvedPath, n);
                        if (ProtectedRun.IsMatch(n + " " + s.Command)) { s.Protected = true; s.Reason = "Hardware or security helper - left alone"; }
                        l.Add(s);
                    }
                }
            }
            catch (Exception ex) { Log.Err("run " + runPath, ex); }
        }

        static void AddFolder(List<StartItem> l, string dir, RegistryKey root, string hive, string source)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                string ap = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
                if (hive == "HKLM") ap = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
                foreach (string f in Directory.GetFiles(dir))
                {
                    string n = Path.GetFileName(f);
                    if (n.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    StartItem s = new StartItem();
                    s.Kind = "Folder"; s.Name = Path.GetFileNameWithoutExtension(n); s.Command = f; s.Source = source;
                    s.Hive = hive; s.ValueName = n; s.Approved = ap;
                    s.Enabled = ApprovedEnabled(root, ap, n);
                    l.Add(s);
                }
            }
            catch (Exception ex) { Log.Err("startup folder", ex); }
        }

        static bool SetApproved(StartItem s, bool enable)
        {
            RegistryKey root = s.Hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            using (RegistryKey k = root.CreateSubKey(s.Approved))
            {
                byte[] b = new byte[12];
                b[0] = (byte)(enable ? 2 : 3);
                if (!enable)
                {
                    long ft = DateTime.UtcNow.ToFileTimeUtc();
                    byte[] t = BitConverter.GetBytes(ft);
                    Array.Copy(t, 0, b, 4, 8);
                }
                k.SetValue(s.ValueName, b, RegistryValueKind.Binary);
            }
            return true;
        }

        // ---------------- scheduled tasks (third-party only, run at sign-in / boot)
        public static List<StartItem> TaskItems()
        {
            List<StartItem> l = new List<StartItem>();
            try
            {
                Type t = Type.GetTypeFromProgID("Schedule.Service");
                if (t == null) return l;
                dynamic svc = Activator.CreateInstance(t);
                svc.Connect();
                WalkTasks(svc.GetFolder("\\"), l);
            }
            catch (Exception ex) { Log.Err("tasks", ex); }
            return l;
        }

        static void WalkTasks(dynamic folder, List<StartItem> l)
        {
            try
            {
                foreach (dynamic task in folder.GetTasks(1))
                {
                    try
                    {
                        string path = task.Path;
                        if (path.StartsWith("\\Microsoft\\", StringComparison.OrdinalIgnoreCase)) continue;
                        bool logon = false;
                        foreach (dynamic tr in task.Definition.Triggers)
                        {
                            int ty = (int)tr.Type;
                            if (ty == 8 || ty == 9) logon = true;
                        }
                        if (!logon) continue;
                        string cmd = "";
                        foreach (dynamic a in task.Definition.Actions)
                        {
                            try { cmd = Convert.ToString(a.Path) + " " + Convert.ToString(a.Arguments); } catch { }
                            break;
                        }
                        StartItem s = new StartItem();
                        s.Kind = "Task"; s.Name = Convert.ToString(task.Name); s.Command = cmd.Trim(); s.Source = "Scheduled task";
                        s.TaskPath = path; s.Enabled = (bool)task.Enabled;
                        if (ProtectedRun.IsMatch(s.Name + " " + s.Command)) { s.Protected = true; s.Reason = "Hardware or security helper - left alone"; }
                        l.Add(s);
                    }
                    catch { }
                }
            }
            catch { }
            try
            {
                foreach (dynamic sub in folder.GetFolders(0))
                {
                    string p = sub.Path;
                    if (p.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                    WalkTasks(sub, l);
                }
            }
            catch { }
        }

        static void SetTask(StartItem s, bool enable)
        {
            Type t = Type.GetTypeFromProgID("Schedule.Service");
            dynamic svc = Activator.CreateInstance(t);
            svc.Connect();
            dynamic task = svc.GetFolder("\\").GetTask(s.TaskPath);
            task.Enabled = enable;
        }

        // ---------------- services
        public static List<StartItem> ServiceItems(bool thirdParty)
        {
            List<StartItem> l = new List<StartItem>();
            try
            {
                using (ManagementObjectSearcher q = new ManagementObjectSearcher("root\\cimv2", "SELECT Name,DisplayName,StartMode,State,PathName,Description FROM Win32_Service"))
                {
                    foreach (ManagementBaseObject o in q.Get())
                    {
                        string name = Convert.ToString(o["Name"]);
                        string path = Convert.ToString(o["PathName"]) ?? "";
                        string disp = Convert.ToString(o["DisplayName"]);
                        string mode = Convert.ToString(o["StartMode"]);
                        bool isWindows = path.ToLowerInvariant().Contains("\\windows\\") || path.ToLowerInvariant().Contains("svchost");
                        if (thirdParty == isWindows) continue;
                        if (mode == "Disabled" && thirdParty == false) { }
                        StartItem s = new StartItem();
                        s.Kind = "Service"; s.Name = disp; s.SvcName = name; s.Command = path.Trim('"');
                        s.Source = Convert.ToString(o["State"]);
                        s.Enabled = mode == "Auto";
                        string desc = Convert.ToString(o["Description"]);
                        if (!string.IsNullOrEmpty(desc)) s.Reason = desc;
                        if (ProtectedSvc.IsMatch(name + " " + disp + " " + path)) { s.Protected = true; s.Reason = "Graphics, audio, network or security service - left alone"; }
                        l.Add(s);
                    }
                }
            }
            catch (Exception ex) { Log.Err("services", ex); }
            return l;
        }

        // curated Windows services that are safe to set to Manual
        public class WinSvc { public string Name, Title, Why; }
        public static readonly WinSvc[] Optional = new WinSvc[]
        {
            new WinSvc { Name = "DiagTrack", Title = "Connected User Experiences and Telemetry", Why = "Sends usage data to Microsoft. Safe to set to Manual." },
            new WinSvc { Name = "MapsBroker", Title = "Downloaded Maps Manager", Why = "Only needed by the Maps app." },
            new WinSvc { Name = "WerSvc", Title = "Windows Error Reporting", Why = "Uploads crash reports to Microsoft." },
            new WinSvc { Name = "XblAuthManager", Title = "Xbox Live Auth Manager", Why = "Only needed for Xbox / Game Pass sign-in. Leave on if you use those." },
            new WinSvc { Name = "XblGameSave", Title = "Xbox Live Game Save", Why = "Cloud saves for Xbox titles." },
            new WinSvc { Name = "XboxNetApiSvc", Title = "Xbox Live Networking", Why = "Xbox networking features." },
            new WinSvc { Name = "XboxGipSvc", Title = "Xbox Accessory Management", Why = "Only for Xbox controllers and accessories." },
            new WinSvc { Name = "lfsvc", Title = "Geolocation Service", Why = "Location for apps. Not needed on a desktop." },
            new WinSvc { Name = "RetailDemo", Title = "Retail Demo Service", Why = "Store demo mode. Never needed." },
            new WinSvc { Name = "Fax", Title = "Fax", Why = "Never needed." },
            new WinSvc { Name = "PhoneSvc", Title = "Phone Service", Why = "Phone Link calling." },
            new WinSvc { Name = "RemoteRegistry", Title = "Remote Registry", Why = "Lets other PCs edit your registry. Better off." },
            new WinSvc { Name = "SysMain", Title = "SysMain (Superfetch)", Why = "Preloads apps into RAM. Not useful on an SSD." },
            new WinSvc { Name = "WSearch", Title = "Windows Search indexer", Why = "Indexes files in the background. Leave on if you search often." },
            new WinSvc { Name = "TabletInputService", Title = "Touch Keyboard and Handwriting", Why = "Only for touch screens and pens." }
        };

        public static List<StartItem> OptionalWindowsItems()
        {
            Dictionary<string, StartItem> all = new Dictionary<string, StartItem>(StringComparer.OrdinalIgnoreCase);
            foreach (StartItem s in ServiceItems(false)) if (s.SvcName != null) all[s.SvcName] = s;
            List<StartItem> l = new List<StartItem>();
            foreach (WinSvc w in Optional)
            {
                StartItem s;
                if (!all.TryGetValue(w.Name, out s)) continue;
                s.Name = w.Title; s.Reason = w.Why; s.Protected = false;
                l.Add(s);
            }
            return l;
        }

        static void SetService(StartItem s, bool enable)
        {
            Util.RunResult r = Util.Run("sc.exe", "config \"" + s.SvcName + "\" start= " + (enable ? "auto" : "demand"), 20000);
            if (r.Code != 0) throw new InvalidOperationException(((r.Out ?? "") + (r.Err ?? "")).Trim());
        }

        public static void SetEnabled(StartItem s, bool enable)
        {
            if (s.Kind == "Run" || s.Kind == "Folder") SetApproved(s, enable);
            else if (s.Kind == "Task") SetTask(s, enable);
            else if (s.Kind == "Service") SetService(s, enable);
            s.Enabled = enable;
            Log.Info("Startup " + s.Kind + " '" + s.Name + "' -> " + (enable ? "enabled" : "disabled"));
        }
    }

    public class StartPage : PageBase
    {
        TextBlock _summary, _msg;
        StackPanel _host;
        bool _loaded, _loading;

        public StartPage() : base("Startup and Services", "Decide what starts with Windows. Every switch is reversible: items are disabled, never deleted.")
        {
            Grid top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel left = new StackPanel();
            _summary = UK.T("Reading startup items...", "Big");
            _msg = UK.T("Changes to startup apps and tasks apply at the next sign-in. Services change at the next restart.", "Muted");
            _msg.Margin = new Thickness(0, 4, 0, 0);
            left.Children.Add(_summary); left.Children.Add(_msg);
            top.Children.Add(left);
            Button rf = UK.Btn("Refresh", false, delegate { Load(true); });
            rf.Margin = new Thickness(0);
            rf.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(rf, 1);
            top.Children.Add(rf);
            Body.Children.Add(UK.Card(top));

            _host = new StackPanel();
            Body.Children.Add(_host);
        }

        public override void Shown() { if (!_loaded) Load(false); }

        class Bundle { public List<StartItem> Run, Tasks, Third, Win; }

        void Load(bool force)
        {
            if (_loading) return;
            _loading = true;
            _summary.Text = "Reading startup items...";
            UK.Bg(delegate
            {
                Bundle b = new Bundle();
                b.Run = StartMgr.RunItems();
                b.Tasks = StartMgr.TaskItems();
                b.Third = StartMgr.ServiceItems(true).OrderByDescending(x => x.Enabled).ThenBy(x => x.Name).ToList();
                b.Win = StartMgr.OptionalWindowsItems();
                return b;
            }, delegate(object o)
            {
                _loading = false; _loaded = true;
                Bundle b = o as Bundle;
                _host.Children.Clear();
                if (b == null) { _summary.Text = "Could not read startup items."; return; }
                int on = b.Run.Count(x => x.Enabled) + b.Tasks.Count(x => x.Enabled) + b.Third.Count(x => x.Enabled);
                _summary.Text = on + " items start with Windows";
                _host.Children.Add(MakeSection("Startup apps", "Programs that open when you sign in.", b.Run, true));
                _host.Children.Add(MakeSection("Scheduled tasks at sign-in", "Background tasks installed by apps (Windows' own tasks are hidden).", b.Tasks, false));
                _host.Children.Add(MakeSection("Background services from installed apps", "Helpers that run all the time. Hardware, audio and security services are locked.", b.Third, false));
                _host.Children.Add(MakeSection("Optional Windows services", "Built-in services you can safely set to Manual so they only start when needed.", b.Win, false));
            });
        }

        UIElement MakeSection(string title, string sub, List<StartItem> items, bool open)
        {
            StackPanel sp = new StackPanel();
            if (items.Count == 0) sp.Children.Add(UK.T("Nothing here.", "Muted"));
            for (int i = 0; i < items.Count; i++)
            {
                StartItem it = items[i];
                ToggleButton sw = UK.Sw(it.Enabled);
                if (it.Protected) { sw.IsEnabled = false; }
                sw.Click += delegate
                {
                    bool want = sw.IsChecked == true;
                    try
                    {
                        StartMgr.SetEnabled(it, want);
                        _msg.Text = (want ? "Enabled: " : "Disabled: ") + it.Name + ". Takes effect at the next " + (it.Kind == "Service" ? "restart." : "sign-in.");
                    }
                    catch (Exception ex)
                    {
                        sw.IsChecked = !want;
                        _msg.Text = "Could not change " + it.Name + ": " + ex.Message;
                        Log.Err(ex);
                    }
                };
                string detail = string.IsNullOrEmpty(it.Reason) ? (it.Command ?? "") : it.Reason;
                if (detail.Length > 110) detail = detail.Substring(0, 107) + "...";
                string right = it.Protected ? "locked" : it.Source;
                sp.Children.Add(UK.Row(it.Name, detail, UK.Right(right ?? ""), sw, i == items.Count - 1));
            }
            return UK.Section(title, items.Count + " item" + (items.Count == 1 ? "" : "s") + "  -  " + sub, sp, open, null);
        }
    }
}
