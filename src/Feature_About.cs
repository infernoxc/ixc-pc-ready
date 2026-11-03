using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InfernoXC
{
    public static class AutoStart
    {
        const string TaskName = "IXC PC Ready";

        public static bool IsOn()
        {
            Util.RunResult r = Util.Run("schtasks.exe", "/Query /TN \"" + TaskName + "\"", 15000);
            return r.Code == 0;
        }

        public static string Enable()
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            Util.RunResult r = Util.Run("schtasks.exe", "/Create /TN \"" + TaskName + "\" /TR \"\\\"" + exe + "\\\" --tray\" /SC ONLOGON /RL HIGHEST /F", 20000);
            return r.Code == 0 ? null : ((r.Out ?? "") + (r.Err ?? "")).Trim();
        }

        public static string Disable()
        {
            Util.RunResult r = Util.Run("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F", 15000);
            return r.Code == 0 ? null : ((r.Out ?? "") + (r.Err ?? "")).Trim();
        }
    }

    public class AboutPage : PageBase
    {
        TextBlock _msg;
        MainWindow _win;

        public AboutPage(MainWindow win) : base("About and Settings", "")
        {
            _win = win;

            // identity card
            Grid id = new Grid();
            id.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            id.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Image logo = new Image { Width = 64, Height = 64, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Top };
            try
            {
                BitmapImage bi = new BitmapImage();
                bi.BeginInit();
                bi.StreamSource = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.png");
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                logo.Source = bi;
            }
            catch { }
            id.Children.Add(logo);
            StackPanel sp = new StackPanel();
            sp.Children.Add(UK.T(Brand.Name, "H1"));
            sp.Children.Add(UK.T(Brand.Tagline, "Muted"));
            TextBlock mk = UK.T("Made by " + Brand.Maker, "Body");
            mk.FontWeight = FontWeights.SemiBold;
            mk.Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(mk);
            TextBlock ver = UK.T("Version " + Brand.Version + "   -   MIT licence   -   free and open source", "Muted");
            ver.Margin = new Thickness(0, 2, 0, 12);
            sp.Children.Add(ver);
            StackPanel b = new StackPanel { Orientation = Orientation.Horizontal };
            b.Children.Add(UK.Btn("Visit on GitHub", true, delegate { Open(Brand.RepoUrl); }));
            b.Children.Add(UK.Btn("Report a problem", false, delegate { Open(Brand.RepoUrl + "/issues"); }));
            b.Children.Add(UK.Btn("Open data folder", false, delegate { Open(Paths.DataDir); }));
            sp.Children.Add(b);
            TextBlock gh = UK.T("Official page: " + Brand.RepoUrl, "Muted");
            gh.Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(gh);
            Grid.SetColumn(sp, 1);
            id.Children.Add(sp);
            Body.Children.Add(UK.Card(id));

            // behaviour
            StackPanel beh = new StackPanel();
            ToggleButton tray = UK.Sw(Settings.GetBool("tray", false));
            tray.Click += delegate
            {
                Settings.SetBool("tray", tray.IsChecked == true);
                _win.ApplyTraySetting();
                _msg.Text = tray.IsChecked == true ? "PC Ready now stays in the notification area when you close the window." : "Closing the window now exits the app.";
            };
            beh.Children.Add(UK.Row("Keep running in the tray", "Closing the window hides it next to the clock so Game Mode and monitoring stay one click away.", null, tray, false));

            ToggleButton auto = UK.Sw(false);
            auto.IsEnabled = false;
            beh.Children.Add(UK.Row("Start with Windows", "Opens quietly in the tray after you sign in. Uses a sign-in task so there is no permission prompt. Off by default.", null, auto, true));
            Body.Children.Add(UK.Section("Behaviour", "Both are off until you turn them on", beh, true, null));
            UK.Bg(delegate { return AutoStart.IsOn(); }, delegate(object o)
            {
                auto.IsChecked = o is bool && (bool)o;
                auto.IsEnabled = true;
                auto.Click += delegate
                {
                    bool want = auto.IsChecked == true;
                    string err = want ? AutoStart.Enable() : AutoStart.Disable();
                    if (err != null) { auto.IsChecked = !want; _msg.Text = "Could not change start-up: " + err; return; }
                    if (want && !Settings.GetBool("tray", false))
                    {
                        Settings.SetBool("tray", true);
                        tray.IsChecked = true;
                        _win.ApplyTraySetting();
                    }
                    _msg.Text = want ? "PC Ready will start with Windows in the tray." : "PC Ready will no longer start with Windows.";
                };
            });

            // updates
            StackPanel up = new StackPanel();
            _upMsg = UK.T("Press Check to look for a newer version on GitHub. Nothing is downloaded until you press Install.", "Body");
            _upMsg.TextWrapping = TextWrapping.Wrap;
            up.Children.Add(_upMsg);
            _upNotes = UK.T("", "Muted"); _upNotes.TextWrapping = TextWrapping.Wrap; _upNotes.Margin = new Thickness(0, 6, 0, 0);
            up.Children.Add(_upNotes);
            WrapPanel ub = new WrapPanel(); ub.Margin = new Thickness(0, 10, 0, 0);
            ub.Children.Add(UK.Btn("Check for updates", true, delegate { CheckUpdate(); }));
            _installBtn = UK.Btn("Install update", false, delegate { InstallUpdate(); });
            _installBtn.IsEnabled = false;
            ub.Children.Add(_installBtn);
            ub.Children.Add(UK.Btn("What's new", false, delegate { Open(_upInfo != null && !string.IsNullOrEmpty(_upInfo.PageUrl) ? _upInfo.PageUrl : Brand.RepoUrl + "/releases"); }));
            up.Children.Add(ub);
            ToggleButton autoChk = UK.Sw(Settings.GetBool("update_check_start", false));
            autoChk.Click += delegate { Settings.SetBool("update_check_start", autoChk.IsChecked == true); _msg.Text = autoChk.IsChecked == true ? "The app will look for updates when it starts." : "The app will only check when you press the button."; };
            TextBlock gap = UK.T("", "Muted"); gap.Margin = new Thickness(0, 8, 0, 0); up.Children.Add(gap);
            up.Children.Add(UK.Row("Check when the app starts", "Contacts github.com once at start-up. Off by default.", null, autoChk, true));
            up.Children.Add(UK.T("An update is only installed if its SHA-256 matches the hash published with the release, and the previous version is kept until the new one has started. The check proves the file was not damaged or swapped in transit; it does not replace a code-signing certificate.", "Muted"));
            Body.Children.Add(UK.Section("Updates", "Version " + Brand.Version, up, true, null));
            if (Settings.GetBool("update_check_start", false) && !_autoChecked) { _autoChecked = true; CheckUpdate(); }

            // display and performance
            StackPanel disp = new StackPanel();
            string[] ivText = new string[] { "0.5 s (fastest)", "1 s", "1.5 s (default)", "2 s", "5 s (lightest)" };
            int[] ivMs = new int[] { 500, 1000, 1500, 2000, 5000 };
            int cur = Array.IndexOf(ivMs, MainWindow.IntervalMs()); if (cur < 0) cur = 2;
            Button ivBtn = UK.Cycle("Refresh every: ", ivText, cur, delegate(int i) { Settings.Set("interval_ms", ivMs[i].ToString()); _win.ApplyInterval(); _msg.Text = "Sensors refresh every " + ivText[i] + ". A slower rate uses less CPU on weak PCs."; });
            disp.Children.Add(UK.Row("Sensor refresh rate", "How often temperatures and usage are read while the window is open. Nothing is read while the window is hidden unless Game Mode is on.", null, ivBtn, true));
            Body.Children.Add(UK.Section("Performance of this app", "Keeps the monitor light on low-end PCs", disp, false, null));

            // backup / restore
            StackPanel cfg = new StackPanel();
            cfg.Children.Add(UK.T("Saves your settings, app profiles and Clean choices into one file you can keep or copy to another PC. Importing makes a backup of the current settings first.", "Muted"));
            WrapPanel cb = new WrapPanel(); cb.Margin = new Thickness(0, 10, 0, 0);
            cb.Children.Add(UK.Btn("Export settings", false, delegate { ExportConfig(); }));
            cb.Children.Add(UK.Btn("Import settings", false, delegate { ImportConfig(); }));
            cfg.Children.Add(cb);
            Body.Children.Add(UK.Section("Backup and restore", "Move your setup between PCs", cfg, false, null));

            // safety
            StackPanel safe = new StackPanel();
            string[] points = new string[]
            {
                "Nothing is changed until you press a button or flip a switch.",
                "Every Windows setting this app changes is backed up first and can be switched back.",
                "Startup items are disabled, never deleted.",
                "Anti-cheat, security, graphics, audio and network services are locked and never touched.",
                "Game Mode never closes your game, OBS or anti-cheat, and restores paused services when it ends.",
                "It never touches your FPS cap, in-game settings or files.",
                "No data leaves your PC. There is no account and no telemetry. The only network use is when you press a check or update button: the update check on GitHub, winget for app updates, and Windows Update for drivers. Nothing is sent about you."
            };
            foreach (string p in points)
            {
                TextBlock t = UK.T("-  " + p, "Body");
                t.Margin = new Thickness(0, 0, 0, 6);
                safe.Children.Add(t);
            }
            Body.Children.Add(UK.Section("What this app will and will not do", "Read this before you change anything", safe, false, null));

            // diagnostics
            StackPanel diag = new StackPanel();
            diag.Children.Add(UK.T("Log file: " + Path.Combine(Paths.DataDir, "pc-ready.log"), "Muted"));
            diag.Children.Add(UK.T("Settings and backups: " + Paths.DataDir, "Muted"));
            Body.Children.Add(UK.Section("Files", "Where this app keeps its data", diag, false, null));

            _msg = UK.T("", "Muted");
            _msg.Margin = new Thickness(0, 4, 0, 0);
            Body.Children.Add(_msg);
        }

        TextBlock _upMsg, _upNotes;
        Button _installBtn;
        UpdateInfo _upInfo;
        static bool _autoChecked;

        void CheckUpdate()
        {
            _upMsg.Text = "Checking GitHub...";
            _installBtn.IsEnabled = false;
            UK.Bg(delegate { return Updater.Check(); }, delegate(object o)
            {
                _upInfo = o as UpdateInfo;
                if (_upInfo == null) { _upMsg.Text = "The check failed."; return; }
                _upMsg.Text = _upInfo.Summary;
                string n = (_upInfo.Notes ?? "").Trim();
                if (n.Length > 600) n = n.Substring(0, 600) + "...";
                _upNotes.Text = _upInfo.Ok && _upInfo.Newer ? n : "";
                _installBtn.IsEnabled = _upInfo.Ok && _upInfo.Newer;
                if (_upInfo.Ok && _upInfo.Newer && string.IsNullOrEmpty(_upInfo.HashUrl)) _upMsg.Text += ". This release has no published SHA-256 file, so it cannot be installed from here. Download it from GitHub instead.";
            });
        }

        void InstallUpdate()
        {
            if (_upInfo == null || !_upInfo.Newer) return;
            if (Dlg.Show("Install version " + _upInfo.Tag.TrimStart('v', 'V') + "?\n\nThe file is downloaded from GitHub and checked against its published SHA-256 before it replaces this one. The app restarts when it is done.", Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _installBtn.IsEnabled = false; _upMsg.Text = "Downloading and verifying...";
            UpdateInfo info = _upInfo;
            UK.Bg(delegate { string exe; string err = Updater.Install(info, out exe); return new string[] { err, exe }; }, delegate(object o)
            {
                string[] r = (string[])o;
                if (r[0] != null) { _upMsg.Text = r[0]; _installBtn.IsEnabled = true; return; }
                _upMsg.Text = "Updated. Restarting...";
                try { Process.Start(new ProcessStartInfo(r[1]) { UseShellExecute = true }); } catch (Exception ex) { _upMsg.Text = "Updated, but the new version could not start: " + ex.Message; return; }
                Application.Current.Shutdown();
            });
        }

        void ExportConfig()
        {
            try
            {
                Microsoft.Win32.SaveFileDialog d = new Microsoft.Win32.SaveFileDialog();
                d.FileName = "IXC-PC-Ready-settings-" + DateTime.Now.ToString("yyyyMMdd") + ".ixcconfig"; d.Filter = "IXC PC Ready settings (*.ixcconfig)|*.ixcconfig";
                if (d.ShowDialog() != true) return;
                StringBuilder sb = new StringBuilder();
                foreach (string f in new string[] { "settings.ini", "app-profiles.tsv" })
                {
                    string p = Path.Combine(Paths.DataDir, f);
                    if (!File.Exists(p)) continue;
                    sb.AppendLine("[[file:" + f + "]]");
                    sb.AppendLine(File.ReadAllText(p, Encoding.UTF8).TrimEnd());
                }
                File.WriteAllText(d.FileName, sb.ToString(), Encoding.UTF8);
                _msg.Text = "Settings exported to " + d.FileName;
            }
            catch (Exception ex) { _msg.Text = "Could not export: " + ex.Message; }
        }

        void ImportConfig()
        {
            try
            {
                Microsoft.Win32.OpenFileDialog d = new Microsoft.Win32.OpenFileDialog();
                d.Filter = "IXC PC Ready settings (*.ixcconfig)|*.ixcconfig";
                if (d.ShowDialog() != true) return;
                string[] lines = File.ReadAllLines(d.FileName, Encoding.UTF8);
                Dictionary<string, StringBuilder> files = new Dictionary<string, StringBuilder>();
                StringBuilder cur = null;
                foreach (string line in lines)
                {
                    if (line.StartsWith("[[file:") && line.EndsWith("]]")) { string name = line.Substring(7, line.Length - 9); if (name != "settings.ini" && name != "app-profiles.tsv") { cur = null; continue; } cur = new StringBuilder(); files[name] = cur; }
                    else if (cur != null) cur.AppendLine(line);
                }
                if (files.Count == 0) { _msg.Text = "That file is not an IXC PC Ready settings file."; return; }
                string bk = Path.Combine(Paths.Sub("settings-backups"), DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(bk);
                foreach (KeyValuePair<string, StringBuilder> kv in files)
                {
                    string p = Path.Combine(Paths.DataDir, kv.Key);
                    if (File.Exists(p)) File.Copy(p, Path.Combine(bk, kv.Key), true);
                    File.WriteAllText(p, kv.Value.ToString(), Encoding.UTF8);
                }
                _msg.Text = "Imported " + files.Count + " file(s). Your previous settings are in " + bk + ". Restart the app to load everything.";
            }
            catch (Exception ex) { _msg.Text = "Could not import: " + ex.Message; }
        }

        static void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Err(ex); }
        }
    }
}
