using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using Forms = System.Windows.Forms;
using System.Windows.Threading;

namespace InfernoXC
{
    public interface IPage
    {
        FrameworkElement Root { get; }
        void Update(SystemSample s);
    }

    public static class Ui
    {
        public static FrameworkElement Load(string resName)
        {
            using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName))
            {
                if (st == null) throw new InvalidOperationException("Missing resource " + resName);
                return (FrameworkElement)XamlReader.Load(st);
            }
        }
        public static T F<T>(FrameworkElement root, string name) where T : class
        {
            T t = root.FindName(name) as T;
            if (t == null) throw new InvalidOperationException("XAML element not found: " + name);
            return t;
        }
        public static Brush B(string key) { return (Brush)Application.Current.Resources[key]; }

        public static UIElement KeyValue(string k, string v)
        {
            Grid g = new Grid();
            g.Margin = new Thickness(0, 0, 0, 9);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock a = new TextBlock { Text = k, Style = (Style)Application.Current.Resources["Muted"] };
            TextBlock b = new TextBlock { Text = v, Style = (Style)Application.Current.Resources["Body"] };
            Grid.SetColumn(b, 1);
            g.Children.Add(a); g.Children.Add(b);
            return g;
        }

        public static UIElement FindingRow(Finding f)
        {
            Brush c = f.Level == 0 ? B("GoodBrush") : f.Level == 1 ? B("AccentBrush") : f.Level == 2 ? B("WarnBrush") : B("BadBrush");
            Grid g = new Grid();
            g.Margin = new Thickness(0, 0, 0, 12);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Ellipse e = new Ellipse { Width = 8, Height = 8, Fill = c, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            StackPanel sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = f.Title, Style = (Style)Application.Current.Resources["Body"], FontWeight = FontWeights.SemiBold });
            sp.Children.Add(new TextBlock { Text = f.Detail, Style = (Style)Application.Current.Resources["Muted"], Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(sp, 1);
            g.Children.Add(e); g.Children.Add(sp);
            return g;
        }

        public static UIElement TopRow(string name, string val)
        {
            Grid g = new Grid();
            g.Margin = new Thickness(0, 0, 0, 6);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock a = new TextBlock { Text = name, Style = (Style)Application.Current.Resources["Body"], TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            TextBlock b = new TextBlock { Text = val, Style = (Style)Application.Current.Resources["Muted"], Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(b, 1);
            g.Children.Add(a); g.Children.Add(b);
            return g;
        }

        public static Brush LoadBrush(double pct)
        {
            return pct >= 90 ? B("BadBrush") : pct >= 70 ? B("WarnBrush") : B("AccentBrush");
        }
    }

    // ------------------------------------------------------------------ Dashboard
    public class DashboardPage : IPage
    {
        public FrameworkElement Root { get; private set; }
        TextBlock _cpuVal, _gpuVal, _ramVal, _vramVal, _cpuSub, _gpuSub, _ramSub, _vramSub;
        ProgressBar _cpuBar, _gpuBar, _ramBar, _vramBar;
        StackPanel _topCpu, _topGpu, _topRam;
        HardwareInfo _hw;

        public DashboardPage(HardwareInfo hw)
        {
            _hw = hw;
            Root = Ui.Load("Dashboard.xaml");
            _cpuVal = Ui.F<TextBlock>(Root, "CpuVal"); _gpuVal = Ui.F<TextBlock>(Root, "GpuVal");
            _ramVal = Ui.F<TextBlock>(Root, "RamVal"); _vramVal = Ui.F<TextBlock>(Root, "VramVal");
            _cpuSub = Ui.F<TextBlock>(Root, "CpuSub"); _gpuSub = Ui.F<TextBlock>(Root, "GpuSub");
            _ramSub = Ui.F<TextBlock>(Root, "RamSub"); _vramSub = Ui.F<TextBlock>(Root, "VramSub");
            _cpuBar = Ui.F<ProgressBar>(Root, "CpuBar"); _gpuBar = Ui.F<ProgressBar>(Root, "GpuBar");
            _ramBar = Ui.F<ProgressBar>(Root, "RamBar"); _vramBar = Ui.F<ProgressBar>(Root, "VramBar");
            _topCpu = Ui.F<StackPanel>(Root, "TopCpu"); _topGpu = Ui.F<StackPanel>(Root, "TopGpu"); _topRam = Ui.F<StackPanel>(Root, "TopRam");

            GpuInfo g = hw.MainGpu;
            Ui.F<TextBlock>(Root, "Sub").Text = (hw.Os ?? "Windows") + "  -  " + (hw.CpuName ?? "CPU") + "  -  " + (g != null ? g.Name : "no GPU found");
            _cpuSub.Text = hw.Cores + " cores / " + hw.Threads + " threads";
            _gpuSub.Text = g != null ? g.Name : "not detected";
            _ramSub.Text = hw.RamTotalGB.ToString("0") + " GB installed";
            _vramSub.Text = g != null && g.VramMB > 0 ? (g.VramMB / 1024.0).ToString("0") + " GB card" : "";

            StackPanel hl = Ui.F<StackPanel>(Root, "HwList");
            hl.Children.Add(Ui.KeyValue("Processor", (hw.CpuName ?? "-") + (hw.Cores > 0 ? "  (" + hw.Cores + "C / " + hw.Threads + "T)" : "")));
            foreach (GpuInfo gi in hw.Gpus)
                hl.Children.Add(Ui.KeyValue("Graphics", gi.Name + (gi.VramMB > 0 ? "  -  " + (gi.VramMB / 1024.0).ToString("0.#") + " GB" : "") + (string.IsNullOrEmpty(gi.Driver) ? "" : "\nDriver " + gi.Driver)));
            string ram = hw.RamTotalGB.ToString("0") + " GB";
            if (hw.Ram.Count > 0) ram += "  -  " + hw.Ram.Count + (hw.RamSlots > 0 ? " of " + hw.RamSlots : "") + " slots, " + (hw.Ram[0].Configured > 0 ? hw.Ram[0].Configured : hw.Ram[0].Speed) + " MT/s";
            hl.Children.Add(Ui.KeyValue("Memory", ram));
            hl.Children.Add(Ui.KeyValue("Motherboard", string.IsNullOrEmpty(hw.Board) ? "-" : hw.Board + (string.IsNullOrEmpty(hw.Bios) ? "" : "\nBIOS " + hw.Bios)));
            foreach (DiskInfo d in hw.Disks)
                hl.Children.Add(Ui.KeyValue("Storage", d.Model + "  -  " + d.Kind + ", " + d.SizeGB.ToString("0") + " GB"));
            int maxHz = 0;
            Native.DisplayMode dm = null;
            try { dm = Native.CurrentDisplay(out maxHz); } catch { }
            if (dm != null && dm.W > 0)
            {
                hl.Children.Add(Ui.KeyValue("Display", dm.W + " x " + dm.H + " @ " + dm.Hz + " Hz" + (maxHz > dm.Hz ? "\nMonitor supports up to " + maxHz + " Hz" : "")));
                if (maxHz >= dm.Hz + 20)
                    hw.Findings.Add(new Finding(2, "Monitor is not at its top refresh rate", "Windows is set to " + dm.Hz + " Hz but this display supports " + maxHz + " Hz. Set it in Settings > System > Display > Advanced display."));
            }
            else if (g != null && g.Hz > 0)
                hl.Children.Add(Ui.KeyValue("Display", (g.Res ?? "") + " @ " + g.Hz + " Hz"));
            hl.Children.Add(Ui.KeyValue("Windows", (hw.Os ?? "-") + "  (build " + hw.OsBuild + ")"));

            StackPanel fl = Ui.F<StackPanel>(Root, "FindList");
            foreach (Finding f in hw.Findings.OrderByDescending(x => x.Level))
                fl.Children.Add(Ui.FindingRow(f));
        }

        static void SetBar(ProgressBar b, double pct)
        {
            b.Value = Math.Max(0, Math.Min(100, pct));
            b.Foreground = Ui.LoadBrush(pct);
        }

        public void Update(SystemSample s)
        {
            _cpuVal.Text = s.Cpu.ToString("0") + "%"; SetBar(_cpuBar, s.Cpu);
            if (s.GpuOk) { _gpuVal.Text = s.Gpu.ToString("0") + "%"; SetBar(_gpuBar, s.Gpu); } else { _gpuVal.Text = "n/a"; }
            double rp = s.RamTotalMB > 0 ? s.RamUsedMB / s.RamTotalMB * 100 : 0;
            _ramVal.Text = rp.ToString("0") + "%"; SetBar(_ramBar, rp);
            _ramSub.Text = Util.MB(s.RamUsedMB) + " of " + Util.MB(s.RamTotalMB);
            GpuInfo g = _hw.MainGpu;
            if (g != null && g.VramMB > 0 && s.VramUsedMB > 0)
            {
                double vp = s.VramUsedMB / g.VramMB * 100;
                _vramVal.Text = vp.ToString("0") + "%"; SetBar(_vramBar, vp);
                _vramSub.Text = Util.MB(s.VramUsedMB) + " of " + Util.MB(g.VramMB);
            }
            else if (s.VramUsedMB > 0) { _vramVal.Text = Util.MB(s.VramUsedMB); }

            Fill(_topCpu, s.Rows.OrderByDescending(r => r.Cpu).Take(4), r => r.Cpu.ToString("0.0") + " %");
            Fill(_topGpu, s.Rows.Where(r => r.Gpu > 0.05).OrderByDescending(r => r.Gpu).Take(4), r => r.Gpu.ToString("0.0") + " %");
            Fill(_topRam, s.Rows.OrderByDescending(r => r.RamMB).Take(4), r => Util.MB(r.RamMB));
        }

        static void Fill(StackPanel p, IEnumerable<ProcRow> rows, Func<ProcRow, string> val)
        {
            p.Children.Clear();
            int n = 0;
            foreach (ProcRow r in rows) { p.Children.Add(Ui.TopRow(r.Name, val(r))); n++; }
            if (n == 0) p.Children.Add(Ui.TopRow("idle", ""));
        }
    }

    // ------------------------------------------------------------------ Processes
    public class ProcessesPage : IPage
    {
        public FrameworkElement Root { get; private set; }
        DataGrid _grid;
        RadioButton _sortCpu, _sortGpu, _sortRam, _sortVram, _showGames;
        TextBox _search;
        TextBlock _info;
        SystemSample _last;
        string _selName;
        bool _updating;

        public ProcessesPage()
        {
            Root = Ui.Load("Processes.xaml");
            _grid = Ui.F<DataGrid>(Root, "Grid");
            _sortCpu = Ui.F<RadioButton>(Root, "SortCpu"); _sortGpu = Ui.F<RadioButton>(Root, "SortGpu");
            _sortRam = Ui.F<RadioButton>(Root, "SortRam"); _sortVram = Ui.F<RadioButton>(Root, "SortVram");
            _showGames = Ui.F<RadioButton>(Root, "ShowGames");
            _search = Ui.F<TextBox>(Root, "Search");
            _info = Ui.F<TextBlock>(Root, "SelInfo");
            RoutedEventHandler re = delegate { Refresh(); };
            _sortCpu.Checked += re; _sortGpu.Checked += re; _sortRam.Checked += re; _sortVram.Checked += re;
            Ui.F<RadioButton>(Root, "ShowAll").Checked += re; _showGames.Checked += re;
            _search.TextChanged += delegate { Refresh(); };
            _grid.SelectionChanged += delegate
            {
                if (_updating) return;
                ProcRow r = _grid.SelectedItem as ProcRow;
                _selName = r == null ? null : r.Name;
                ShowInfo(r);
            };
            Ui.F<Button>(Root, "BtnHigh").Click += delegate { SetPriority(System.Diagnostics.ProcessPriorityClass.High); };
            Ui.F<Button>(Root, "BtnNormal").Click += delegate { SetPriority(System.Diagnostics.ProcessPriorityClass.Normal); };
            Ui.F<Button>(Root, "BtnLow").Click += delegate { SetPriority(System.Diagnostics.ProcessPriorityClass.BelowNormal); };
            Ui.F<Button>(Root, "BtnGpuHigh").Click += delegate { SetGpuPref(2); };
            Ui.F<Button>(Root, "BtnGpuAuto").Click += delegate { SetGpuPref(0); };
        }

        void ShowInfo(ProcRow r)
        {
            if (r == null) { _info.Text = "Select an app to tune it."; return; }
            _info.Text = r.Name + (string.IsNullOrEmpty(r.Path) ? "" : "   -   " + r.Path);
        }

        ProcRow Selected()
        {
            ProcRow r = _grid.SelectedItem as ProcRow;
            if (r == null) _info.Text = "Select an app in the list first.";
            return r;
        }

        void SetPriority(System.Diagnostics.ProcessPriorityClass pc)
        {
            ProcRow r = Selected();
            if (r == null) return;
            int ok = 0, fail = 0;
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName(r.Name))
            {
                try { p.PriorityClass = pc; ok++; } catch { fail++; }
                finally { p.Dispose(); }
            }
            _info.Text = r.Name + ": priority set to " + pc + " on " + ok + " process(es)" + (fail > 0 ? ", " + fail + " protected (skipped)" : "") + ". Lasts until the app closes.";
            Log.Info("Priority " + r.Name + " -> " + pc + " ok=" + ok + " fail=" + fail);
        }

        void SetGpuPref(int pref)
        {
            ProcRow r = Selected();
            if (r == null) return;
            if (string.IsNullOrEmpty(r.Path)) { _info.Text = r.Name + ": Windows does not expose this app's file path, cannot set a GPU preference."; return; }
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences"))
                {
                    if (pref == 0) k.DeleteValue(r.Path, false);
                    else k.SetValue(r.Path, "GpuPreference=" + pref + ";", Microsoft.Win32.RegistryValueKind.String);
                }
                _info.Text = r.Name + ": graphics preference " + (pref == 0 ? "reset to Windows default" : "set to High performance") + ". Restart the app for it to apply.";
                Log.Info("GPU pref " + r.Path + " = " + pref);
            }
            catch (Exception ex) { _info.Text = "Could not change preference: " + ex.Message; }
        }

        void Refresh()
        {
            if (_last == null) return;
            IEnumerable<ProcRow> q = _last.Rows;
            if (_showGames.IsChecked == true) q = q.Where(r => r.IsGame);
            string f = _search.Text.Trim();
            if (f.Length > 0) q = q.Where(r => r.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
            if (_sortGpu.IsChecked == true) q = q.OrderByDescending(r => r.Gpu).ThenByDescending(r => r.Cpu);
            else if (_sortRam.IsChecked == true) q = q.OrderByDescending(r => r.RamMB);
            else if (_sortVram.IsChecked == true) q = q.OrderByDescending(r => r.VramMB).ThenByDescending(r => r.Gpu);
            else q = q.OrderByDescending(r => r.Cpu).ThenByDescending(r => r.RamMB);
            List<ProcRow> list = q.Take(250).ToList();
            _updating = true;
            try
            {
                _grid.ItemsSource = list;
                if (_selName != null)
                {
                    ProcRow again = list.FirstOrDefault(r => r.Name == _selName);
                    if (again != null) _grid.SelectedItem = again;
                }
            }
            finally { _updating = false; }
        }

        public void Update(SystemSample s)
        {
            _last = s;
            Refresh();
        }
    }

    public class PlaceholderPage : IPage
    {
        public FrameworkElement Root { get; private set; }
        public PlaceholderPage(string title, string text)
        {
            StackPanel sp = new StackPanel { Margin = new Thickness(32, 28, 32, 28) };
            sp.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["H1"] });
            sp.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.Resources["Muted"], Margin = new Thickness(0, 6, 0, 0) });
            Root = sp;
        }
        public void Update(SystemSample s) { }
    }

    // ------------------------------------------------------------------ Window
    public class MainWindow : Window
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        FrameworkElement _shell;
        ContentControl _host;
        SysMonitor _mon = new SysMonitor();
        public HardwareInfo Hw;
        SystemSample _last;
        IPage _current;
        string _currentKey;
        Dictionary<string, IPage> _pages = new Dictionary<string, IPage>();
        Dictionary<string, RadioButton> _navs = new Dictionary<string, RadioButton>();
        DispatcherTimer _timer;
        bool _busy;
        public int SampleCount;
        Forms.NotifyIcon _tray;
        bool _exiting;

        public void ApplyTraySetting()
        {
            bool on = Settings.GetBool("tray", false);
            if (on && _tray == null)
            {
                _tray = new Forms.NotifyIcon();
                try
                {
                    using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.png"))
                    using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(st))
                        _tray.Icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
                }
                catch { }
                _tray.Text = Brand.Name;
                Forms.ContextMenuStrip m = new Forms.ContextMenuStrip();
                m.Items.Add("Open PC Ready", null, delegate { ShowFromTray(); });
                m.Items.Add("Start or end Game Mode", null, delegate { TrayGameMode(); });
                m.Items.Add("-");
                m.Items.Add("Exit", null, delegate { ExitApp(); });
                _tray.ContextMenuStrip = m;
                _tray.DoubleClick += delegate { ShowFromTray(); };
                _tray.Visible = true;
            }
            else if (!on && _tray != null)
            {
                _tray.Visible = false; _tray.Dispose(); _tray = null;
            }
        }

        public void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            if (Left < -10000)
            {
                Left = (SystemParameters.WorkArea.Width - Width) / 2;
                Top = (SystemParameters.WorkArea.Height - Height) / 2;
            }
            Activate();
            Tick();
        }

        void TrayGameMode()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string msg = GameMode.Active ? GameMode.End() : GameMode.Start();
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_tray != null) _tray.ShowBalloonTip(3000, Brand.Name, msg, Forms.ToolTipIcon.Info);
                }));
            });
        }

        public void ExitApp()
        {
            _exiting = true;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_exiting && _tray != null) { e.Cancel = true; Hide(); return; }
            try { if (GameMode.Active) GameMode.End(); } catch { }
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            base.OnClosing(e);
        }

        public MainWindow()
        {
            Title = Brand.Name;
            Width = 1180; Height = 760;
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Ui.B("BgBrush");
            UseLayoutRounding = true;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            try { Icon = BitmapFrame.Create(Assembly.GetExecutingAssembly().GetManifestResourceStream("app.png")); } catch { }

            _shell = Ui.Load("Shell.xaml");
            Content = _shell;
            _host = Ui.F<ContentControl>(_shell, "PageHost");
            Ui.F<TextBlock>(_shell, "FootVer").Text = "Version " + Brand.Version;
            Ui.F<TextBlock>(_shell, "FootMaker").Text = "Made by " + Brand.Maker;

            string[] keys = new string[] { "Dash", "Proc", "Game", "Clean", "Start", "Apps", "Crash", "Tune", "About" };
            foreach (string k in keys)
            {
                RadioButton rb = Ui.F<RadioButton>(_shell, "Nav" + k);
                string key = k;
                _navs[k] = rb;
                rb.Checked += delegate { Navigate(key); };
            }

            SourceInitialized += delegate
            {
                try
                {
                    IntPtr h = new WindowInteropHelper(this).Handle;
                    int on = 1; DwmSetWindowAttribute(h, 20, ref on, 4);
                    int cap = 0x0015110F; DwmSetWindowAttribute(h, 35, ref cap, 4);
                    int txt = 0x00EEE9E7; DwmSetWindowAttribute(h, 36, ref txt, 4);
                }
                catch { }
            };

            _host.Content = new TextBlock { Text = "Reading your hardware...", Style = (Style)Application.Current.Resources["Muted"], Margin = new Thickness(32, 32, 0, 0) };

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(1500);
            _timer.Tick += delegate { Tick(); };

            ThreadPool.QueueUserWorkItem(delegate
            {
                try { GameLib.Scan(); GameMode.RestoreIfLeftOver(); } catch (Exception ex) { Log.Err(ex); }
            });

            ThreadPool.QueueUserWorkItem(delegate
            {
                HardwareInfo hw = null;
                try { hw = HardwareInfo.Read(); } catch (Exception ex) { Log.Err(ex); }
                if (hw == null) hw = new HardwareInfo();
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    Hw = hw;
                    Navigate("Dash");
                    Tick();
                    _timer.Start();
                }));
            });
        }

        public IPage Page(string key)
        {
            IPage p;
            if (_pages.TryGetValue(key, out p)) return p;
            switch (key)
            {
                case "Dash": p = new DashboardPage(Hw); break;
                case "Proc": p = new ProcessesPage(); break;
                case "Game": p = new GamePage(Hw); break;
                case "Clean": p = new CleanPage(); break;
                case "Start": p = new StartPage(); break;
                case "Apps": p = new AppsPage(); break;
                case "Crash": p = new CrashPage(Hw); break;
                case "Tune": p = new TunePage(Hw); break;
                case "About": p = new AboutPage(this); break;
                default: p = new PlaceholderPage(key, "Unknown page."); break;
            }
            _pages[key] = p;
            return p;
        }

        public void Navigate(string key)
        {
            if (Hw == null) return;
            try
            {
                IPage p = Page(key);
                _current = p; _currentKey = key;
                _host.Content = p.Root;
                if (_last != null) p.Update(_last);
                PageBase pb = p as PageBase;
                if (pb != null) pb.Shown();
            }
            catch (Exception ex)
            {
                Log.Err(ex);
                _host.Content = new TextBlock { Text = "This page failed to load: " + ex.Message, Foreground = Ui.B("BadBrush"), Margin = new Thickness(32), TextWrapping = TextWrapping.Wrap };
            }
        }

        public void GoTo(string key)
        {
            RadioButton rb;
            if (_navs.TryGetValue(key, out rb)) rb.IsChecked = true;
        }

        void Tick()
        {
            if (_busy) return;
            if (!IsVisible && !GameMode.Active) return;
            _busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                SystemSample s = null;
                try { s = _mon.Sample(); } catch (Exception ex) { Log.Err(ex); }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _busy = false;
                    if (s == null) return;
                    _last = s; SampleCount++;
                    if (_current != null && IsVisible) { try { _current.Update(s); } catch (Exception ex) { Log.Err(ex); } }
                    try
                    {
                        if (GameMode.Tick(s)) ThreadPool.QueueUserWorkItem(delegate { GameMode.End(); });
                    }
                    catch (Exception ex) { Log.Err(ex); }
                }));
            });
        }

        public void RenderTo(string file)
        {
            _shell.UpdateLayout();
            int w = (int)Math.Max(1, _shell.ActualWidth), h = (int)Math.Max(1, _shell.ActualHeight);
            RenderTargetBitmap rt = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rt.Render(_shell);
            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rt));
            using (FileStream fs = File.Create(file)) enc.Save(fs);
        }
    }

    // ------------------------------------------------------------------ Entry
    public class App : Application
    {
        [STAThread]
        public static void Main(string[] args)
        {
            bool selftest = args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
            if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))) { Installer.Uninstall(); return; }
            if (!selftest && Installer.ShouldOffer(args) && Installer.Offer()) return;
            bool createdNew;
            Mutex mx = new Mutex(true, selftest ? "InfernoXC.PCReady.SelfTest" : "InfernoXC.PCReady.Main", out createdNew);
            if (!createdNew) return;

            App app = new App();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                Log.Err(e.Exception);
                e.Handled = true;
            };
            try
            {
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("Theme.xaml"))
                {
                    ResourceDictionary rd = (ResourceDictionary)XamlReader.Load(st);
                    app.Resources.MergedDictionaries.Add(rd);
                }
                MainWindow w = new MainWindow();
                bool trayStart = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
                if (!selftest) w.ApplyTraySetting();
                if (selftest) RunSelfTest(app, w);
                if (trayStart && !selftest && Settings.GetBool("tray", false))
                {
                    w.ShowInTaskbar = false;
                    w.WindowStartupLocation = WindowStartupLocation.Manual;
                    w.Left = -32000; w.Top = -32000;
                    w.Show();
                    w.Hide();
                    w.ShowInTaskbar = true;
                }
                else w.Show();
                app.Run();
            }
            catch (Exception ex)
            {
                Log.Err(ex);
                try { File.WriteAllText(Path.Combine(Paths.AppDir, "startup-error.txt"), ex.ToString()); } catch { }
                MessageBox.Show("PC Ready could not start:\n\n" + ex.Message, Brand.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            GC.KeepAlive(mx);
        }

        static void RunSelfTest(App app, MainWindow w)
        {
            string dir = Path.Combine(Paths.AppDir, "selftest");
            try { Directory.CreateDirectory(dir); } catch { }
            StringBuilder rep = new StringBuilder();
            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(500);
            DateTime start = DateTime.Now;
            string[] keys = new string[] { "Dash", "Proc", "Game", "Clean", "Start", "Apps", "Crash", "Tune", "About" };
            int[] waits = new int[] { 2, 2, 3, 24, 16, 12, 30, 3, 3 };   // ticks of 500 ms to let background loads finish
            int idx = -1, wait = 0;
            bool began = false;
            t.Tick += delegate
            {
                bool timeout = (DateTime.Now - start).TotalSeconds > 300;
                if (!began)
                {
                    if (!timeout && (w.Hw == null || w.SampleCount < 4)) return;
                    began = true;
                    rep.AppendLine("SELFTEST " + Brand.Version + " admin=" + new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
                    rep.AppendLine("samples=" + w.SampleCount + " timeout=" + timeout);
                    if (w.Hw != null) rep.AppendLine(w.Hw.Summary());
                }
                try
                {
                    if (wait > 0) { wait--; if (!timeout) return; }
                    if (idx >= 0)
                    {
                        try
                        {
                            w.RenderTo(Path.Combine(dir, "page-" + keys[idx] + ".png"));
                            rep.AppendLine("page " + keys[idx] + " OK");
                        }
                        catch (Exception ex) { rep.AppendLine("page " + keys[idx] + " FAIL " + ex.Message); }
                    }
                    idx++;
                    if (idx < keys.Length && !timeout)
                    {
                        w.GoTo(keys[idx]);
                        wait = waits[idx];
                        return;
                    }
                }
                catch (Exception ex) { rep.AppendLine("FATAL " + ex); }
                t.Stop();
                try { File.WriteAllText(Path.Combine(dir, "selftest.txt"), rep.ToString()); } catch { }
                app.Shutdown();
            };
            t.Start();
        }
    }
}
