using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace InfernoXC
{
    public class CleanItem
    {
        public string Id, Title, Detail, Group;
        public bool Default, Risky;
        public Func<long> Scan;          // bytes that would be removed (-1 = unknown until cleaned)
        public Func<long> Run;           // bytes freed
        public long Size = -1;
        public bool Selected;
        public TextBlock SizeText;       // ui
        public ToggleButton Switch;      // ui
        public string Note = "";
    }

    public static class CleanKit
    {
        static readonly string Local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        static readonly string ProgData = Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData";
        static readonly string Win = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

        public static long DirSize(string dir)
        {
            long total = 0;
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
                Stack<string> st = new Stack<string>();
                st.Push(dir);
                while (st.Count > 0)
                {
                    string d = st.Pop();
                    try
                    {
                        foreach (string f in Directory.GetFiles(d)) { try { total += new FileInfo(f).Length; } catch { } }
                        foreach (string s in Directory.GetDirectories(d))
                        {
                            try { if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) == 0) st.Push(s); } catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return total;
        }

        static long DelRec(string dir, DateTime cut, bool removeSelf)
        {
            long freed = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    try
                    {
                        FileInfo fi = new FileInfo(f);
                        if (fi.LastWriteTime > cut) continue;
                        long l = fi.Length;
                        fi.Attributes = FileAttributes.Normal;
                        fi.Delete();
                        freed += l;
                    }
                    catch { }
                }
            }
            catch { }
            try
            {
                foreach (string s in Directory.GetDirectories(dir))
                {
                    try
                    {
                        if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue;
                        freed += DelRec(s, cut, true);
                    }
                    catch { }
                }
            }
            catch { }
            if (removeSelf) { try { Directory.Delete(dir, false); } catch { } }
            return freed;
        }

        /// <summary>Deletes everything inside dir that has not been modified for minAgeMinutes.</summary>
        public static long DeleteIn(string dir, int minAgeMinutes)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
            return DelRec(dir, DateTime.Now.AddMinutes(-minAgeMinutes), false);
        }

        static long FilesSize(string dir, string pattern)
        {
            long t = 0;
            try { if (Directory.Exists(dir)) foreach (string f in Directory.GetFiles(dir, pattern)) { try { t += new FileInfo(f).Length; } catch { } } } catch { }
            return t;
        }
        static long DeleteFiles(string dir, string pattern)
        {
            long t = 0;
            try
            {
                if (Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir, pattern))
                    {
                        try { FileInfo fi = new FileInfo(f); long l = fi.Length; fi.Attributes = FileAttributes.Normal; fi.Delete(); t += l; } catch { }
                    }
            }
            catch { }
            return t;
        }

        static long Sum(string[] dirs, bool scan, int age)
        {
            long t = 0;
            foreach (string d in dirs) t += scan ? DirSize(d) : DeleteIn(d, age);
            return t;
        }

        static string[] Existing(params string[] dirs) { return dirs.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).ToArray(); }

        static string[] BrowserCaches()
        {
            List<string> l = new List<string>();
            string[] roots = new string[] { Path.Combine(Local, @"Google\Chrome\User Data"), Path.Combine(Local, @"Microsoft\Edge\User Data"), Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data") };
            foreach (string r in roots)
            {
                try
                {
                    if (!Directory.Exists(r)) continue;
                    foreach (string prof in Directory.GetDirectories(r))
                    {
                        string n = Path.GetFileName(prof);
                        if (n != "Default" && !n.StartsWith("Profile ")) continue;
                        foreach (string c in new string[] { "Cache", "Code Cache", "GPUCache" })
                        {
                            string p = Path.Combine(prof, c);
                            if (Directory.Exists(p)) l.Add(p);
                        }
                    }
                }
                catch { }
            }
            return l.ToArray();
        }

        static long FreeOnSystemDrive()
        {
            try { return new DriveInfo(Path.GetPathRoot(Win)).AvailableFreeSpace; } catch { return 0; }
        }

        static long EventLogSize()
        {
            return FilesSize(Path.Combine(Win, @"System32\winevt\Logs"), "*.evtx");
        }

        static long ClearEventLogs()
        {
            long before = EventLogSize();
            Util.RunResult r = Util.Run("wevtutil.exe", "el", 20000);
            foreach (string line in r.Out.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                Util.Run("wevtutil.exe", "cl \"" + line.Trim() + "\"", 20000);
            return Math.Max(0, before - EventLogSize());
        }

        static long CleanWindowsUpdate()
        {
            string dir = Path.Combine(Win, @"SoftwareDistribution\Download");
            bool wu = GameMode.SvcRunning("wuauserv"), bits = GameMode.SvcRunning("BITS");
            if (wu) Util.Run("net.exe", "stop wuauserv /y", 30000);
            if (bits) Util.Run("net.exe", "stop bits /y", 30000);
            long f = DeleteIn(dir, 0);
            if (bits) Util.Run("net.exe", "start bits", 30000);
            if (wu) Util.Run("net.exe", "start wuauserv", 30000);
            return f;
        }

        static long ComponentCleanup()
        {
            long before = FreeOnSystemDrive();
            Util.Run("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", 40 * 60 * 1000);
            return Math.Max(0, FreeOnSystemDrive() - before);
        }

        static long RamPurge()
        {
            double before = GameMode.AvailMB();
            Native.MemoryCommand(2);
            Native.MemoryCommand(3);
            Native.MemoryCommand(4);
            return (long)(Math.Max(0, GameMode.AvailMB() - before) * 1048576);
        }

        static string TrimOutput = "";
        static long Trim()
        {
            string ps = "$o=@(); foreach($v in Get-Volume | Where-Object { $_.DriveType -eq 'Fixed' -and $_.DriveLetter }){ try { $pd = Get-Partition -DriveLetter $v.DriveLetter | Get-Disk | Get-PhysicalDisk; if($pd.MediaType -eq 'SSD' -or $pd.BusType -eq 'NVMe'){ Optimize-Volume -DriveLetter $v.DriveLetter -ReTrim -ErrorAction Stop | Out-Null; $o += ($v.DriveLetter + ': trimmed') } else { $o += ($v.DriveLetter + ': skipped (not an SSD)') } } catch { $o += ($v.DriveLetter + ': ' + $_.Exception.Message) } }; $o -join '; '";
            Util.RunResult r = Util.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + ps.Replace("\"", "\\\"") + "\"", 10 * 60 * 1000);
            TrimOutput = (r.Out ?? "").Trim();
            Log.Info("TRIM: " + TrimOutput);
            return 0;
        }
        public static string LastTrim { get { return TrimOutput; } }

        public static List<CleanItem> Build()
        {
            List<CleanItem> l = new List<CleanItem>();
            string temp = Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath();

            // ---- temporary files
            Add(l, "Temporary files", "temp-user", "Your temp folder", "Leftovers from installers and apps. Files used in the last 30 minutes are kept.", true, false,
                () => DirSize(temp), () => DeleteIn(temp, 30));
            Add(l, "Temporary files", "temp-win", "Windows temp folder", "System-wide temporary files.", true, false,
                () => DirSize(Path.Combine(Win, "Temp")), () => DeleteIn(Path.Combine(Win, "Temp"), 30));
            Add(l, "Temporary files", "prefetch", "Prefetch", "Launch hints Windows rebuilds on its own. Safe to clear.", true, false,
                () => DirSize(Path.Combine(Win, "Prefetch")), () => DeleteIn(Path.Combine(Win, "Prefetch"), 0));
            Add(l, "Temporary files", "thumbs", "Thumbnail and icon cache", "Explorer rebuilds it when you open folders.", true, false,
                () => FilesSize(Path.Combine(Local, @"Microsoft\Windows\Explorer"), "*cache_*.db"), () => DeleteFiles(Path.Combine(Local, @"Microsoft\Windows\Explorer"), "*cache_*.db"));
            string[] dumps = new string[] { Path.Combine(Win, "Minidump"), Path.Combine(Local, "CrashDumps"), Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportQueue") };
            Add(l, "Temporary files", "dumps", "Crash dumps and error reports", "Old dump files. Keep them if you are still investigating a crash.", false, false,
                () => Sum(dumps, true, 0) + FileLen(Path.Combine(Win, "MEMORY.DMP")), () => Sum(dumps, false, 0) + DelFile(Path.Combine(Win, "MEMORY.DMP")));

            // ---- graphics caches (only vendors that exist)
            string[] amd = Existing(Path.Combine(Local, @"AMD\DxCache"), Path.Combine(Local, @"AMD\DxcCache"), Path.Combine(Local, @"AMD\GLCache"), Path.Combine(Local, @"AMD\VkCache"));
            if (amd.Length > 0) Add(l, "Graphics and game caches", "sh-amd", "AMD shader cache", "Games rebuild it on the next launch, so the first run may stutter briefly.", true, false, () => Sum(amd, true, 0), () => Sum(amd, false, 0));
            string[] nv = Existing(Path.Combine(Local, @"NVIDIA\DXCache"), Path.Combine(Local, @"NVIDIA\GLCache"), Path.Combine(ProgData, @"NVIDIA Corporation\NV_Cache"));
            if (nv.Length > 0) Add(l, "Graphics and game caches", "sh-nv", "NVIDIA shader cache", "Rebuilt automatically; the first game launch may take a little longer.", true, false, () => Sum(nv, true, 0), () => Sum(nv, false, 0));
            string[] d3d = Existing(Path.Combine(Local, "D3DSCache"), Path.Combine(Local, @"Intel\ShaderCache"));
            if (d3d.Length > 0) Add(l, "Graphics and game caches", "sh-d3d", "DirectX shader cache", "Windows' own shader cache. Rebuilt on demand.", true, false, () => Sum(d3d, true, 0), () => Sum(d3d, false, 0));
            string[] glogs = Existing(Path.Combine(Local, @"TslGame\Saved\Logs"), Path.Combine(Local, @"TslGame\Saved\Crashes"), Path.Combine(Local, @"VALORANT\Saved\Logs"), Path.Combine(Local, @"VALORANT\Saved\Crashes"));
            if (glogs.Length > 0) Add(l, "Graphics and game caches", "glogs", "Game logs and crash folders", "PUBG and VALORANT log files. Not your settings or saves.", true, false, () => Sum(glogs, true, 0), () => Sum(glogs, false, 0));
            string[] br = BrowserCaches();
            if (br.Length > 0) Add(l, "Graphics and game caches", "browser", "Browser caches", "Chrome, Edge and Brave cache only. You stay signed in. Close the browser first.", false, false, () => Sum(br, true, 0), () => Sum(br, false, 0));

            // ---- windows
            Add(l, "Windows", "wu", "Windows Update download cache", "Already-installed update files. Update service is paused for a moment, then restarted.", true, false,
                () => DirSize(Path.Combine(Win, @"SoftwareDistribution\Download")), CleanWindowsUpdate);
            Add(l, "Windows", "do", "Delivery Optimization cache", "Update pieces Windows keeps to share with other PCs.", true, false,
                () => DirSize(Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")),
                () => DeleteIn(Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"), 0));
            Add(l, "Windows", "dism", "Windows component store cleanup", "Removes superseded system files (DISM). Size is measured when it finishes; takes a few minutes.", false, false,
                () => -1, ComponentCleanup);
            Add(l, "Windows", "events", "Event logs", "Clears the Windows event viewer logs. This also erases the history the Crash Report page reads.", false, true,
                EventLogSize, ClearEventLogs);

            // ---- memory & drive
            Add(l, "Memory and drive", "ram", "Free memory now", "Empties standby and idle memory so apps start with clean RAM. Nothing is closed.", true, false,
                () => -1, RamPurge);
            Add(l, "Memory and drive", "trim", "SSD TRIM", "Tells your SSDs which blocks are free so they keep their speed. Hard disks are skipped.", true, false,
                () => -1, Trim);

            foreach (CleanItem c in l) c.Selected = Settings.GetBool("clean_" + c.Id, c.Default);
            return l;
        }

        static long FileLen(string f) { try { return File.Exists(f) ? new FileInfo(f).Length : 0; } catch { return 0; } }
        static long DelFile(string f) { try { if (!File.Exists(f)) return 0; long n = new FileInfo(f).Length; File.Delete(f); return n; } catch { return 0; } }

        static void Add(List<CleanItem> l, string group, string id, string title, string detail, bool def, bool risky, Func<long> scan, Func<long> run)
        {
            CleanItem c = new CleanItem();
            c.Group = group; c.Id = id; c.Title = title; c.Detail = detail; c.Default = def; c.Risky = risky; c.Scan = scan; c.Run = run;
            l.Add(c);
        }
    }

    public class CleanPage : PageBase
    {
        List<CleanItem> _items;
        TextBlock _total, _status;
        Button _scanBtn, _cleanBtn;
        bool _busy;
        bool _scannedOnce;

        public CleanPage() : base("Clean and Optimize", "Pick what to clean. Nothing is removed until you press Clean selected.")
        {
            _items = CleanKit.Build();

            Grid top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel left = new StackPanel();
            _total = UK.T("Scanning...", "Big");
            _status = UK.T("Sizes are measured in the background.", "Muted");
            _status.Margin = new Thickness(0, 4, 0, 0);
            left.Children.Add(_total); left.Children.Add(_status);
            top.Children.Add(left);
            StackPanel btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _scanBtn = UK.Btn("Scan again", false, delegate { Scan(); });
            _cleanBtn = UK.Btn("Clean selected", true, delegate { Clean(); });
            _cleanBtn.Margin = new Thickness(0);
            btns.Children.Add(_scanBtn); btns.Children.Add(_cleanBtn);
            Grid.SetColumn(btns, 1);
            top.Children.Add(btns);
            Body.Children.Add(UK.Card(top));

            foreach (string group in _items.Select(i => i.Group).Distinct())
            {
                List<CleanItem> g = _items.Where(i => i.Group == group).ToList();
                StackPanel sp = new StackPanel();
                List<ToggleButton> switches = new List<ToggleButton>();
                for (int i = 0; i < g.Count; i++)
                {
                    CleanItem c = g[i];
                    ToggleButton sw = UK.Sw(c.Selected);
                    c.Switch = sw;
                    sw.Click += delegate
                    {
                        c.Selected = sw.IsChecked == true;
                        Settings.SetBool("clean_" + c.Id, c.Selected);
                        UpdateTotal();
                    };
                    c.SizeText = UK.Right(c.Size < 0 ? "" : Util.Bytes(c.Size));
                    string detail = c.Detail + (c.Risky ? "  (off by default)" : "");
                    sp.Children.Add(UK.Row(c.Title, detail, c.SizeText, sw, i == g.Count - 1));
                    switches.Add(sw);
                }
                ToggleButton master = UK.Sw(g.All(x => x.Selected));
                List<CleanItem> gg = g;
                master.Click += delegate
                {
                    bool on = master.IsChecked == true;
                    foreach (CleanItem c in gg)
                    {
                        if (c.Risky && on) continue;   // risky items are never switched on in bulk
                        c.Selected = on;
                        c.Switch.IsChecked = on;
                        Settings.SetBool("clean_" + c.Id, on);
                    }
                    UpdateTotal();
                };
                Body.Children.Add(UK.Section(group, g.Count + " item" + (g.Count == 1 ? "" : "s"), sp, true, master));
            }
            UpdateTotal();
        }

        void UpdateTotal()
        {
            long sum = _items.Where(i => i.Selected && i.Size > 0).Sum(i => i.Size);
            int n = _items.Count(i => i.Selected);
            if (!_scannedOnce) { _total.Text = n + " selected"; return; }
            _total.Text = Util.Bytes(sum) + " ready to clean";
            _status.Text = n + " of " + _items.Count + " items selected.";
        }

        public override void Shown()
        {
            if (!_scannedOnce && !_busy) Scan();
        }

        void Scan()
        {
            if (_busy) return;
            _busy = true;
            _scanBtn.IsEnabled = false; _cleanBtn.IsEnabled = false;
            _status.Text = "Measuring...";
            UK.Bg(delegate
            {
                foreach (CleanItem c in _items)
                {
                    long s = -1;
                    try { s = c.Scan(); } catch (Exception ex) { Log.Err(ex); }
                    c.Size = s;
                    CleanItem cc = c;
                    UK.UI(delegate { cc.SizeText.Text = cc.Size < 0 ? "" : Util.Bytes(cc.Size); });
                }
                return null;
            }, delegate
            {
                _busy = false; _scannedOnce = true;
                _scanBtn.IsEnabled = true; _cleanBtn.IsEnabled = true;
                UpdateTotal();
            });
        }

        void Clean()
        {
            if (_busy) return;
            List<CleanItem> todo = _items.Where(i => i.Selected).ToList();
            if (todo.Count == 0) { _status.Text = "Nothing selected."; return; }
            string list = string.Join("\n  ", todo.Select(i => i.Title).ToArray());
            MessageBoxResult r = MessageBox.Show("Clean these now?\n\n  " + list + (todo.Any(i => i.Id == "events") ? "\n\nThis includes the event logs, which erases your crash history." : ""), Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            _busy = true;
            _scanBtn.IsEnabled = false; _cleanBtn.IsEnabled = false;
            UK.Bg(delegate
            {
                long total = 0;
                StringBuilder notes = new StringBuilder();
                foreach (CleanItem c in todo)
                {
                    CleanItem cc = c;
                    UK.UI(delegate { _status.Text = "Cleaning: " + cc.Title + "..."; });
                    long f = 0;
                    try { f = c.Run(); } catch (Exception ex) { Log.Err(ex); }
                    if (f > 0) total += f;
                    Log.Info("Clean " + c.Id + " freed " + f);
                    if (c.Id == "trim" && CleanKit.LastTrim.Length > 0) notes.Append("  TRIM: " + CleanKit.LastTrim);
                }
                return total.ToString() + "|" + notes.ToString();
            }, delegate(object res)
            {
                string[] parts = ((string)res ?? "0|").Split(new char[] { '|' }, 2);
                long freed = 0; long.TryParse(parts[0], out freed);
                _busy = false;
                _scanBtn.IsEnabled = true; _cleanBtn.IsEnabled = true;
                _total.Text = "Freed " + Util.Bytes(freed);
                _status.Text = "Done at " + DateTime.Now.ToString("HH:mm") + "." + (parts.Length > 1 ? parts[1] : "");
                // refresh sizes without clobbering the message
                UK.Bg(delegate
                {
                    foreach (CleanItem c in _items)
                    {
                        long s = -1;
                        try { s = c.Scan(); } catch { }
                        c.Size = s;
                        CleanItem cc = c;
                        UK.UI(delegate { cc.SizeText.Text = cc.Size < 0 ? "" : Util.Bytes(cc.Size); });
                    }
                    return null;
                }, null);
            });
        }
    }
}
