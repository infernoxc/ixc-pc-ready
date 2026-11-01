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
            Body.Children.Add(btns);
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

        void DoUninstall()
        {
            AppRow a = Sel();
            if (a == null) return;
            if (string.IsNullOrEmpty(a.Uninstall)) { _msg.Text = a.Name + " has no uninstaller registered" + (a.Launcher != null ? " - uninstall games from " + a.Launcher + "." : "."); return; }
            MessageBoxResult r = MessageBox.Show("Uninstall " + a.Name + "?\n\nThe app's own uninstaller will open.", Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"" + a.Uninstall + "\"");
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                Process.Start(psi);
                Log.Info("Uninstall launched: " + a.Name);
                _msg.Text = "Uninstaller started for " + a.Name + ". Refresh the list afterwards by switching tabs.";
                _loaded = false;
            }
            catch (Exception ex) { _msg.Text = "Could not start uninstaller: " + ex.Message; }
        }
    }
}
