using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
                "No data leaves your PC. There is no account, no telemetry and no network access."
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

        static void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Err(ex); }
        }
    }
}
