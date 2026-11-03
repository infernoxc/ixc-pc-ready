using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace InfernoXC
{
    public class AppRow : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string Publisher { get; set; }
        public string Version { get; set; }
        public long SizeBytes { get; set; }
        public string Installed { get; set; }
        public string Location { get; set; }
        public string Uninstall { get; set; }
        public string Launcher { get; set; }
        public string LocLower;

        public string SizeText { get { return SizeBytes <= 0 ? "-" : Util.Bytes(SizeBytes); } }

        string _run = "";
        public string RunText
        {
            get { return _run; }
            set { if (_run != value) { _run = value; if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("RunText")); } }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public static class AppReader
    {
        public static List<AppRow> ReadInstalled()
        {
            List<AppRow> l = new List<AppRow>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ReadKey(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", l, seen);
            ReadKey(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", l, seen);
            ReadKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall", l, seen);
            return l;
        }

        static void ReadKey(RegistryKey root, string path, List<AppRow> l, HashSet<string> seen)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(path))
                {
                    if (k == null) return;
                    foreach (string sub in k.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey sk = k.OpenSubKey(sub))
                            {
                                if (sk == null) continue;
                                string name = sk.GetValue("DisplayName") as string;
                                if (string.IsNullOrWhiteSpace(name)) continue;
                                object sc = sk.GetValue("SystemComponent");
                                if (sc is int && (int)sc == 1) continue;
                                if (sk.GetValue("ParentKeyName") != null) continue;
                                if (name.StartsWith("Update for", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Security Update", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Hotfix", StringComparison.OrdinalIgnoreCase)) continue;
                                string ver = Convert.ToString(sk.GetValue("DisplayVersion"));
                                string key = name + "|" + ver;
                                if (!seen.Add(key)) continue;
                                AppRow a = new AppRow();
                                a.Name = name.Trim();
                                a.Publisher = Convert.ToString(sk.GetValue("Publisher"));
                                a.Version = ver;
                                object es = sk.GetValue("EstimatedSize");
                                if (es is int) a.SizeBytes = (long)(int)es * 1024L;
                                string idate = Convert.ToString(sk.GetValue("InstallDate"));
                                if (idate != null && idate.Length == 8) a.Installed = idate.Substring(0, 4) + "-" + idate.Substring(4, 2) + "-" + idate.Substring(6, 2);
                                a.Location = (Convert.ToString(sk.GetValue("InstallLocation")) ?? "").Trim().Trim('"');
                                if (a.Location.Length == 0)
                                {
                                    string icon = Convert.ToString(sk.GetValue("DisplayIcon")) ?? "";
                                    int c = icon.IndexOf(',');
                                    if (c > 0) icon = icon.Substring(0, c);
                                    icon = icon.Trim().Trim('"');
                                    try { if (icon.Length > 3 && icon.IndexOf(':') == 1) a.Location = Path.GetDirectoryName(icon); } catch { }
                                }
                                if (a.Location != null && a.Location.Length > 3) a.LocLower = a.Location.ToLowerInvariant().TrimEnd('\\') + "\\";
                                a.Uninstall = Convert.ToString(sk.GetValue("QuietUninstallString"));
                                if (string.IsNullOrEmpty(a.Uninstall)) a.Uninstall = Convert.ToString(sk.GetValue("UninstallString"));
                                l.Add(a);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Log.Err("apps " + path, ex); }
        }

        public static List<AppRow> GamesAsRows()
        {
            List<AppRow> l = new List<AppRow>();
            foreach (GameEntry g in GameLib.All)
            {
                AppRow a = new AppRow();
                a.Name = g.Name; a.Publisher = g.Launcher; a.Launcher = g.Launcher;
                a.SizeBytes = g.SizeBytes; a.Location = g.Dir;
                if (!string.IsNullOrEmpty(g.Dir)) a.LocLower = g.Dir.ToLowerInvariant().TrimEnd('\\') + "\\";
                l.Add(a);
            }
            return l;
        }
    }

    public class AppsPage : PageBase
    {
        DataGrid _grid;
        TextBox _search;
        RadioButton _rApps, _rGames;
        TextBlock _count, _msg;
        List<AppRow> _apps = new List<AppRow>(), _games = new List<AppRow>();
        bool _loaded, _loading;
        SystemSample _last;
        ProcRow _dummy;
        StackPanel _leftHost;
        List<LeftoverItem> _left;
        AppRow _leftApp;

        public AppsPage() : base("Installed Apps", "Everything installed on this PC, what it takes on disk, and what it is using right now.")
        {
            Grid top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            StackPanel seg = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _rApps = new RadioButton { Content = "Apps", Style = UK.S("Seg"), IsChecked = true, GroupName = "appsmode" };
            _rGames = new RadioButton { Content = "Games", Style = UK.S("Seg"), GroupName = "appsmode" };
            seg.Children.Add(_rApps); seg.Children.Add(_rGames);
            top.Children.Add(seg);
            _search = new TextBox { Style = UK.S("Box"), Height = 34, Width = 260, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(_search, 1);
            top.Children.Add(_search);
            top.Margin = new Thickness(0, 0, 0, 12);
            Body.Children.Add(top);

            _count = UK.T("Reading installed apps...", "Muted");
            _count.Margin = new Thickness(0, 0, 0, 8);
            Body.Children.Add(_count);

            _grid = new DataGrid();
            _grid.Height = 430;
            _grid.Columns.Add(Col("Name", "Name", new DataGridLength(2.2, DataGridLengthUnitType.Star), false));
            _grid.Columns.Add(Col("Publisher", "Publisher", new DataGridLength(1.4, DataGridLengthUnitType.Star), false));
            _grid.Columns.Add(Col("Version", "Version", new DataGridLength(0.9, DataGridLengthUnitType.Star), false));
            _grid.Columns.Add(Col("Size", "SizeText", new DataGridLength(0.8, DataGridLengthUnitType.Star), false));
            _grid.Columns.Add(Col("Installed", "Installed", new DataGridLength(0.9, DataGridLengthUnitType.Star), false));
            _grid.Columns.Add(Col("Using now", "RunText", new DataGridLength(1.1, DataGridLengthUnitType.Star), true));
            Border gb = UK.Card(_grid);
            gb.Padding = new Thickness(4);
            Body.Children.Add(gb);

            StackPanel btns = new StackPanel { Orientation = Orientation.Horizontal };
            btns.Children.Add(UK.Btn("Open folder", false, delegate { OpenFolder(); }));
            btns.Children.Add(UK.Btn("Uninstall...", false, delegate { DoUninstall(); }));
            btns.Children.Add(UK.Btn("Scan leftovers", false, delegate { ScanLeftovers(); }));
            Body.Children.Add(btns);
            _leftHost = new StackPanel();
            Body.Children.Add(_leftHost);
            _msg = UK.T("", "Muted");
            _msg.Margin = new Thickness(0, 10, 0, 0);
            Body.Children.Add(_msg);

            RoutedEventHandler re = delegate { Apply(); };
            _rApps.Checked += re; _rGames.Checked += re;
            _search.TextChanged += delegate { Apply(); };
        }

        static DataGridTextColumn Col(string header, string path, DataGridLength w, bool bold)
        {
            DataGridTextColumn c = new DataGridTextColumn();
            c.Header = header;
            c.Binding = new Binding(path);
            c.Width = w;
            c.IsReadOnly = true;
            return c;
        }

        public override void Shown() { if (!_loaded) Load(); }

        void Load()
        {
            if (_loading) return;
            _loading = true;
            UK.Bg(delegate
            {
                if (!GameLib.Scanned) GameLib.Scan();
                List<AppRow>[] r = new List<AppRow>[2];
                r[0] = AppReader.ReadInstalled();
                r[1] = AppReader.GamesAsRows();
                return r;
            }, delegate(object o)
            {
                _loading = false; _loaded = true;
                List<AppRow>[] r = o as List<AppRow>[];
                if (r == null) { _count.Text = "Could not read the app list."; return; }
                _apps = r[0]; _games = r[1];
                Apply();
                if (_last != null) Update(_last);
            });
        }

        void Apply()
        {
            if (!_loaded) return;
            List<AppRow> src = _rGames.IsChecked == true ? _games : _apps;
            string f = (_search.Text ?? "").Trim();
            IEnumerable<AppRow> q = src;
            if (f.Length > 0) q = q.Where(a => a.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || (a.Publisher ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
            List<AppRow> list = q.OrderByDescending(a => a.SizeBytes).ThenBy(a => a.Name).ToList();
            _grid.ItemsSource = list;
            long total = list.Sum(a => a.SizeBytes);
            _count.Text = list.Count + (_rGames.IsChecked == true ? " game" : " app") + (list.Count == 1 ? "" : "s") + (total > 0 ? "  -  " + Util.Bytes(total) + " on disk" : "");
        }

        public override void Update(SystemSample s)
        {
            _last = s;
            if (!_loaded) return;
            List<ProcRow> rows = s.Rows.Where(r => !string.IsNullOrEmpty(r.Path)).ToList();
            List<string> lp = rows.Select(r => r.Path.ToLowerInvariant()).ToList();
            foreach (List<AppRow> src in new List<AppRow>[] { _apps, _games })
            {
                foreach (AppRow a in src)
                {
                    if (a.LocLower == null) { a.RunText = ""; continue; }
                    double ram = 0; int n = 0;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        if (lp[i].StartsWith(a.LocLower)) { ram += rows[i].RamMB; n++; }
                    }
                    a.RunText = n == 0 ? "" : "Running  " + Util.MB(ram);
                }
            }
        }

        AppRow Sel()
        {
            AppRow a = _grid.SelectedItem as AppRow;
            if (a == null) _msg.Text = "Select an entry in the list first.";
            return a;
        }

        void OpenFolder()
        {
            AppRow a = Sel();
            if (a == null) return;
            if (string.IsNullOrEmpty(a.Location) || !Directory.Exists(a.Location)) { _msg.Text = "Windows does not list an install folder for " + a.Name + "."; return; }
            try { Process.Start("explorer.exe", "\"" + a.Location + "\""); }
            catch (Exception ex) { _msg.Text = ex.Message; }
        }

        void ScanLeftovers()
        {
            AppRow a = Sel();
            if (a == null) return;
            _leftHost.Children.Clear();
            _msg.Text = "Scanning for leftovers of " + a.Name + "...";
            AppRow app = a;
            UK.Bg(delegate
            {
                bool still = Leftovers.StillInstalled(app);
                List<LeftoverItem> l = still ? new List<LeftoverItem>() : Leftovers.Scan(app);
                return new object[] { still, l };
            }, delegate(object o)
            {
                object[] r = (object[])o;
                bool still = (bool)r[0];
                _left = (List<LeftoverItem>)r[1]; _leftApp = app;
                if (still) { _msg.Text = app.Name + " is still installed. Uninstall it first, then scan for leftovers."; return; }
                ShowLeftovers();
            });
        }

        void ShowLeftovers()
        {
            _leftHost.Children.Clear();
            if (_left == null || _left.Count == 0) { _msg.Text = "No leftovers found for " + _leftApp.Name + ". The uninstaller cleaned up after itself."; return; }
            StackPanel sp = new StackPanel();
            for (int i = 0; i < _left.Count; i++)
            {
                LeftoverItem it = _left[i];
                System.Windows.Controls.Primitives.ToggleButton sw = UK.Sw(it.Selected);
                sw.Click += delegate { it.Selected = sw.IsChecked == true; };
                sp.Children.Add(UK.Row(it.Kind + ": " + it.Path, it.Why, UK.Right(it.Size > 0 ? Util.Bytes(it.Size) : ""), sw, i == _left.Count - 1));
            }
            StackPanel wrap = new StackPanel();
            wrap.Children.Add(sp);
            TextBlock note = UK.T("Registry keys are exported to a .reg backup before they are deleted. Folders and shortcuts go to the Recycle Bin, so everything can be restored. Only exact matches for the app name are listed, never a whole publisher folder.", "Muted");
            note.Margin = new Thickness(0, 10, 0, 8); wrap.Children.Add(note);
            Button rm = UK.Btn("Remove selected", true, delegate { RemoveLeftovers(); });
            wrap.Children.Add(rm);
            _leftHost.Children.Add(UK.Section("Leftovers of " + _leftApp.Name, _left.Count + " item" + (_left.Count == 1 ? "" : "s") + " found", wrap, true, null));
            _msg.Text = _left.Count + " leftover" + (_left.Count == 1 ? "" : "s") + " found. Untick anything you want to keep, then press Remove selected.";
        }

        void RemoveLeftovers()
        {
            int sel = _left.Count(x => x.Selected);
            if (sel == 0) { _msg.Text = "Nothing is ticked."; return; }
            if (Dlg.Show("Remove " + sel + " leftover item" + (sel == 1 ? "" : "s") + " of " + _leftApp.Name + "?\n\nRegistry keys are backed up first and files go to the Recycle Bin.", Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            AppRow app = _leftApp; List<LeftoverItem> items = _left;
            _msg.Text = "Removing...";
            UK.Bg(delegate { int ok, failed; string dir = Leftovers.Remove(app, items, out ok, out failed); return new object[] { ok, failed, dir }; }, delegate(object o)
            {
                object[] r = (object[])o;
                _msg.Text = "Removed " + r[0] + " item(s)" + ((int)r[1] > 0 ? ", " + r[1] + " could not be removed (see the log)" : "") + ". Backup and list: " + r[2];
                _leftHost.Children.Clear();
                _loaded = false;
            });
        }

        void DoUninstall()
        {
            AppRow a = Sel();
            if (a == null) return;
            if (string.IsNullOrEmpty(a.Uninstall)) { _msg.Text = a.Name + " has no uninstaller registered" + (a.Launcher != null ? " - uninstall games from " + a.Launcher + "." : "."); return; }
            MessageBoxResult r = Dlg.Show("Uninstall " + a.Name + "?\n\nThe app's own uninstaller will open.", Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"" + a.Uninstall + "\"");
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                Process.Start(psi);
                Log.Info("Uninstall launched: " + a.Name);
                _msg.Text = "Uninstaller started for " + a.Name + ". When it has finished, select the same entry and press Scan leftovers to find files and settings it left behind.";
                _loaded = false;
            }
            catch (Exception ex) { _msg.Text = "Could not start uninstaller: " + ex.Message; }
        }
    }
}
