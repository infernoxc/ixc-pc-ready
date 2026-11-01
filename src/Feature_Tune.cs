using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace InfernoXC
{
    public class RegEntry
    {
        public string Hive, Sub, Name, Kind;   // Kind: dword | string
        public object Want;
        public RegEntry(string hive, string sub, string name, string kind, object want) { Hive = hive; Sub = sub; Name = name; Kind = kind; Want = want; }
        RegistryKey Root { get { return Hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser; } }

        public object Read()
        {
            try { using (RegistryKey k = Root.OpenSubKey(Sub)) { return k == null ? null : k.GetValue(Name); } }
            catch { return null; }
        }
        public bool IsSet()
        {
            object v = Read();
            if (v == null) return false;
            if (Kind == "dword") return v is int && (int)v == (int)Want;
            return string.Equals(Convert.ToString(v), Convert.ToString(Want), StringComparison.OrdinalIgnoreCase);
        }
        public void Write()
        {
            using (RegistryKey k = Root.CreateSubKey(Sub))
                k.SetValue(Name, Want, Kind == "dword" ? RegistryValueKind.DWord : RegistryValueKind.String);
        }
        public void Delete()
        {
            using (RegistryKey k = Root.OpenSubKey(Sub, true))
                if (k != null) k.DeleteValue(Name, false);
        }
        public string Describe()
        {
            object v = Read();
            return v == null ? "not set" : Convert.ToString(v);
        }
    }

    public class Tweak
    {
        public string Id, Group, Title, Why, Skip;
        public Func<bool> IsOn;
        public Action On, Off;
        public Func<string> Current;
        public ToggleButton Switch;
        public TextBlock CurText;
    }

    public static class TuneKit
    {
        const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

        public static bool IsLaptop()
        {
            try { return (System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0; }
            catch { return false; }
        }

        static Tweak Reg(string id, string group, string title, string why, params RegEntry[] e)
        {
            Tweak t = new Tweak();
            t.Id = id; t.Group = group; t.Title = title; t.Why = why;
            t.IsOn = delegate { return e.All(x => x.IsSet()); };
            t.Current = delegate { return string.Join(", ", e.Select(x => x.Describe()).ToArray()); };
            t.On = delegate
            {
                for (int i = 0; i < e.Length; i++)
                {
                    string bk = "bk." + id + "." + i;
                    if (string.IsNullOrEmpty(Settings.Get(bk, "")))
                    {
                        object cur = e[i].Read();
                        Settings.Set(bk, cur == null ? "ABSENT" : "V:" + Convert.ToString(cur));
                    }
                    e[i].Write();
                }
            };
            t.Off = delegate
            {
                for (int i = 0; i < e.Length; i++)
                {
                    string bk = "bk." + id + "." + i;
                    string old = Settings.Get(bk, "");
                    if (string.IsNullOrEmpty(old)) continue;   // no saved original: this app never changed it, so leave it
                    if (old == "ABSENT") { e[i].Delete(); }
                    else
                    {
                        string v = old.Substring(2);
                        RegEntry r = e[i];
                        RegEntry back = new RegEntry(r.Hive, r.Sub, r.Name, r.Kind, r.Kind == "dword" ? (object)int.Parse(v) : (object)v);
                        back.Write();
                    }
                    Settings.Set(bk, "");
                }
            };
            return t;
        }

        static string ActivePlan()
        {
            Util.RunResult r = Util.Run("powercfg.exe", "/getactivescheme", 10000);
            Match m = Regex.Match(r.Out ?? "", @"([0-9a-fA-F\-]{36})\s*\(([^)]*)\)");
            return m.Success ? m.Groups[1].Value + "|" + m.Groups[2].Value : "|unknown";
        }

        static Tweak PowerPlan()
        {
            Tweak t = new Tweak();
            t.Id = "plan"; t.Group = "Power"; t.Title = "Ultimate Performance power plan"; t.Why = "Keeps the CPU from down-clocking between frames. Best for a desktop that stays plugged in.";
            t.IsOn = delegate { return ActivePlan().Split('|')[1].IndexOf("Ultimate", StringComparison.OrdinalIgnoreCase) >= 0; };
            t.Current = delegate { return ActivePlan().Split('|')[1]; };
            t.On = delegate
            {
                string cur = ActivePlan().Split('|')[0];
                if (!string.IsNullOrEmpty(cur) && Settings.Get("bk.plan", "") == "") Settings.Set("bk.plan", cur);
                Util.RunResult l = Util.Run("powercfg.exe", "/list", 10000);
                Match m = Regex.Match(l.Out ?? "", @"([0-9a-fA-F\-]{36})\s*\(Ultimate Performance\)", RegexOptions.IgnoreCase);
                string guid = m.Success ? m.Groups[1].Value : null;
                if (guid == null)
                {
                    Util.RunResult d = Util.Run("powercfg.exe", "/duplicatescheme " + Ultimate, 10000);
                    Match g = Regex.Match(d.Out ?? "", @"([0-9a-fA-F\-]{36})");
                    if (g.Success) guid = g.Groups[1].Value;
                }
                if (guid == null) throw new InvalidOperationException("Windows did not create the Ultimate Performance plan.");
                Util.Run("powercfg.exe", "/setactive " + guid, 10000);
            };
            t.Off = delegate
            {
                string back = Settings.Get("bk.plan", "");
                if (back.Length == 0) back = Balanced;
                Util.Run("powercfg.exe", "/setactive " + back, 10000);
                Settings.Set("bk.plan", "");
            };
            return t;
        }

        public static List<Tweak> Build(HardwareInfo hw)
        {
            bool laptop = IsLaptop();
            List<Tweak> l = new List<Tweak>();
            string prof = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

            l.Add(Reg("prio", "Scheduler and responsiveness", "Foreground boost for games", "Gives the app you are using longer CPU time slices than background tasks.",
                new RegEntry("HKLM", @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", "dword", 38)));
            l.Add(Reg("sysresp", "Scheduler and responsiveness", "Reserve less CPU for background media", "Windows holds back 20% of the CPU for background media tasks; this lowers it to 10%.",
                new RegEntry("HKLM", prof, "SystemResponsiveness", "dword", 10)));
            l.Add(Reg("netthr", "Scheduler and responsiveness", "Remove network throttling", "Stops Windows limiting network packets while media plays, which keeps ping steadier.",
                new RegEntry("HKLM", prof, "NetworkThrottlingIndex", "dword", -1)));
            l.Add(Reg("gamestask", "Scheduler and responsiveness", "Games get high scheduling priority", "Tells the multimedia scheduler to give games top GPU and CPU priority.",
                new RegEntry("HKLM", prof + @"\Tasks\Games", "GPU Priority", "dword", 8),
                new RegEntry("HKLM", prof + @"\Tasks\Games", "Priority", "dword", 6),
                new RegEntry("HKLM", prof + @"\Tasks\Games", "Scheduling Category", "string", "High"),
                new RegEntry("HKLM", prof + @"\Tasks\Games", "SFIO Priority", "string", "High")));
            Tweak pt = Reg("pthrottle", "Scheduler and responsiveness", "Turn off power throttling", "Stops Windows slowing apps it thinks are in the background, such as OBS while you game.",
                new RegEntry("HKLM", @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", "dword", 1));
            if (laptop) pt.Skip = "Skipped on laptops: it would cost battery life.";
            l.Add(pt);
            l.Add(Reg("startdelay", "Scheduler and responsiveness", "No delay for startup apps", "Windows waits about 10 seconds after sign-in before starting your startup apps; this removes the wait.",
                new RegEntry("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", "dword", 0)));

            l.Add(Reg("gamemode", "Gaming", "Windows Game Mode on", "Lets Windows focus resources on the running game and pause driver and update installs.",
                new RegEntry("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", "dword", 1),
                new RegEntry("HKCU", @"Software\Microsoft\GameBar", "AllowAutoGameMode", "dword", 1)));
            l.Add(Reg("dvr", "Gaming", "Turn off Xbox Game Bar background capture", "Stops the hidden recorder that costs frames. OBS is not affected.",
                new RegEntry("HKCU", @"System\GameConfigStore", "GameDVR_Enabled", "dword", 0),
                new RegEntry("HKCU", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", "dword", 0)));
            l.Add(Reg("bgapps", "Gaming", "Stop Store apps running in the background", "Frees RAM and CPU used by apps you are not looking at.",
                new RegEntry("HKCU", @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", "dword", 1)));
            l.Add(Reg("faststart", "Gaming", "Full shutdown instead of Fast Startup", "Fast Startup keeps stale driver state between sessions, a known source of odd crashes. A real shutdown starts clean.",
                new RegEntry("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", "dword", 0)));

            l.Add(Reg("mouse", "Input", "Mouse acceleration off", "Makes aim consistent: the same hand movement always moves the cursor the same distance. Applies after you sign out and in.",
                new RegEntry("HKCU", @"Control Panel\Mouse", "MouseSpeed", "string", "0"),
                new RegEntry("HKCU", @"Control Panel\Mouse", "MouseThreshold1", "string", "0"),
                new RegEntry("HKCU", @"Control Panel\Mouse", "MouseThreshold2", "string", "0")));

            Tweak plan = PowerPlan();
            if (laptop) plan.Skip = "Skipped on laptops: it would drain the battery.";
            l.Add(plan);
            return l;
        }

        /// <summary>Hardware-specific advice that cannot be applied from Windows.</summary>
        public static List<Finding> Advice(HardwareInfo hw)
        {
            List<Finding> l = new List<Finding>();
            string cpu = hw.CpuName ?? "";
            if (cpu.IndexOf("X3D", StringComparison.OrdinalIgnoreCase) >= 0)
                l.Add(new Finding(1, "X3D processor", "Your CPU's big cache works best when Windows decides where games run. Keep Game Mode on and do not pin games to specific cores with affinity tools. Install the latest AMD chipset driver."));
            else if (cpu.IndexOf("Ryzen", StringComparison.OrdinalIgnoreCase) >= 0)
                l.Add(new Finding(1, "AMD Ryzen", "Keep the AMD chipset driver current and use a High or Ultimate performance plan. In the BIOS, make sure DOCP / EXPO is on."));
            if (Regex.IsMatch(cpu, @"i[3579]-1[2-9]\d{3}|Core\(TM\) Ultra", RegexOptions.IgnoreCase))
                l.Add(new Finding(1, "Intel hybrid processor", "Games land on the fast cores when Windows Game Mode is on and the power plan is High performance. Do not disable E-cores unless a specific game needs it."));
            GpuInfo g = hw.MainGpu;
            if (g != null && !hw.Hags && g.VramMB >= 6000 && (g.Name ?? "").IndexOf("Intel", StringComparison.OrdinalIgnoreCase) < 0)
                l.Add(new Finding(1, "Hardware-accelerated GPU scheduling is off", "On a recent graphics card this can lower latency. Try it in Settings > System > Display > Graphics, then check your games feel the same or better. Leave it off if you see stutter."));
            if (g != null && (g.Name ?? "").IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0 && hw.Cores > 0)
                l.Add(new Finding(1, "Smart Access Memory", "If your motherboard and card support it, turn on Resizable BAR / Above 4G decoding in the BIOS. It is free performance on Radeon cards."));
            if (hw.Disks.Count > 0 && hw.Disks.Any(d => d.Kind == "HDD") && hw.Disks.Any(d => d.Kind.IndexOf("SSD") >= 0))
                l.Add(new Finding(1, "Install games on the SSD", "You have a hard disk and an SSD. Keep games and Windows on the SSD."));
            foreach (Finding f in hw.Findings.Where(x => x.Level >= 2)) l.Add(f);
            return l;
        }
    }

    public class TunePage : PageBase
    {
        List<Tweak> _tweaks;
        TextBlock _head, _msg;
        HardwareInfo _hw;
        bool _refreshing;

        public TunePage(HardwareInfo hw) : base("Smart Tune", "Settings chosen for your hardware. Every change is backed up and can be switched back.")
        {
            _hw = hw;
            _tweaks = TuneKit.Build(hw);

            Grid top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel left = new StackPanel();
            _head = UK.T("", "Big");
            _msg = UK.T("Your PC: " + (hw.CpuName ?? "CPU") + "  -  " + (hw.MainGpu != null ? hw.MainGpu.Name : "GPU") + "  -  " + hw.RamTotalGB.ToString("0") + " GB RAM", "Muted");
            _msg.Margin = new Thickness(0, 4, 0, 0);
            left.Children.Add(_head); left.Children.Add(_msg);
            top.Children.Add(left);
            StackPanel btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            btns.Children.Add(UK.Btn("Revert all", false, delegate { ApplyAll(false); }));
            Button ap = UK.Btn("Apply all recommended", true, delegate { ApplyAll(true); });
            ap.Margin = new Thickness(0);
            btns.Children.Add(ap);
            Grid.SetColumn(btns, 1);
            top.Children.Add(btns);
            Body.Children.Add(UK.Card(top));

            foreach (string group in _tweaks.Select(t => t.Group).Distinct())
            {
                List<Tweak> g = _tweaks.Where(t => t.Group == group).ToList();
                StackPanel sp = new StackPanel();
                for (int i = 0; i < g.Count; i++)
                {
                    Tweak t = g[i];
                    ToggleButton sw = UK.Sw(false);
                    t.Switch = sw;
                    t.CurText = UK.Right("");
                    if (t.Skip != null) sw.IsEnabled = false;
                    sw.Click += delegate
                    {
                        if (_refreshing) return;
                        bool want = sw.IsChecked == true;
                        try
                        {
                            if (want) t.On(); else t.Off();
                            Log.Info("Tweak " + t.Id + " -> " + (want ? "applied" : "reverted"));
                            _msg.Text = (want ? "Applied: " : "Reverted: ") + t.Title + ".";
                        }
                        catch (Exception ex)
                        {
                            Log.Err(ex);
                            _msg.Text = "Could not change " + t.Title + ": " + ex.Message;
                        }
                        Refresh();
                    };
                    sp.Children.Add(UK.Row(t.Title, t.Skip != null ? t.Skip : t.Why, t.CurText, sw, i == g.Count - 1));
                }
                Body.Children.Add(UK.Section(group, g.Count + " setting" + (g.Count == 1 ? "" : "s"), sp, true, null));
            }

            // hardware-specific advice
            StackPanel adv = new StackPanel();
            List<Finding> fl = TuneKit.Advice(hw);
            if (fl.Count == 0) adv.Children.Add(UK.T("Nothing else to do for this hardware.", "Muted"));
            foreach (Finding f in fl) adv.Children.Add(Ui.FindingRow(f));
            Body.Children.Add(UK.Section("For your hardware (manual steps)", fl.Count + " item" + (fl.Count == 1 ? "" : "s") + " that need the BIOS or a driver, not Windows", adv, true, null));

            Refresh();
        }

        public override void Shown() { Refresh(); }

        void Refresh()
        {
            _refreshing = true;
            try
            {
                int on = 0, total = 0;
                foreach (Tweak t in _tweaks)
                {
                    bool v = false;
                    try { v = t.IsOn(); } catch { }
                    t.Switch.IsChecked = v;
                    string cur = "";
                    try { cur = t.Current(); } catch { }
                    if (cur.Length > 34) cur = cur.Substring(0, 32) + "...";
                    t.CurText.Text = cur;
                    if (t.Skip == null) { total++; if (v) on++; }
                }
                _head.Text = on + " of " + total + " recommended settings applied";
            }
            finally { _refreshing = false; }
        }

        void ApplyAll(bool apply)
        {
            if (apply)
            {
                MessageBoxResult r = MessageBox.Show("Apply every recommended setting?\n\nOriginal values are saved, so Revert all puts everything back.", Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }
            int fail = 0;
            foreach (Tweak t in _tweaks)
            {
                if (t.Skip != null) continue;
                try
                {
                    bool on = t.IsOn();
                    if (apply && !on) t.On();
                    else if (!apply && on) t.Off();
                }
                catch (Exception ex) { fail++; Log.Err(ex); }
            }
            _msg.Text = (apply ? "Applied all recommended settings" : "Reverted all settings") + (fail > 0 ? " (" + fail + " could not be changed)" : "") + ". Some changes apply after a restart or sign-out.";
            Refresh();
        }
    }
}
