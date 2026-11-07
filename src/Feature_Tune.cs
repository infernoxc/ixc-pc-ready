using System;
using System.Collections.Generic;
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
        public string Risk = "Low";
        public bool Restart;
        public bool Optional;   // never turned on by "Apply all recommended" or by a profile; the person picks these
        public Func<bool> IsOn;
        public Action On, Off;
        public Func<string> Current;
        public Func<bool> AppSet;     // true when this app changed it (so a saved original exists)
        public string Benefit = "";   // honest size of the effect: Noticeable / Small / Situational
        public ToggleButton Switch;
        public TextBlock CurText;
    }

    public static class TuneKit
    {
        const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

        // ---- levels: which settings each profile turns on
        public class Level { public string Name, Detail; public string[] Ids; }
        public static readonly Level[] Levels = new Level[]
        {
            new Level { Name = "Safe", Detail = "Only no-risk settings: Game Mode on, no hidden recorder, no Game Bar pop-ups, no startup delay.", Ids = new string[] { "gamemode", "dvr", "gamebar", "startdelay" } },
            new Level { Name = "Balanced", Detail = "Safe plus a small foreground boost and no background Store apps.", Ids = new string[] { "gamemode", "dvr", "gamebar", "startdelay", "prio", "sysresp", "bgapps" } },
            new Level { Name = "Gaming", Detail = "Balanced plus game scheduling priority, no network throttling, no power throttling, Ultimate Performance plan, clean full shutdown and fullscreen optimizations off.", Ids = new string[] { "gamemode", "dvr", "gamebar", "startdelay", "prio", "sysresp", "bgapps", "gamestask", "netthr", "pthrottle", "faststart", "plan", "fso" } },
            new Level { Name = "Streaming", Detail = "Gaming for a PC that also runs OBS: the same set, so OBS is not slowed down in the background. Nothing is closed.", Ids = new string[] { "gamemode", "dvr", "gamebar", "startdelay", "prio", "sysresp", "bgapps", "gamestask", "netthr", "pthrottle", "faststart", "plan", "fso" } },
            new Level { Name = "Competitive", Detail = "Everything that measurably helps: all of Gaming plus mouse acceleration off, TCP no-delay for steadier ping, honoured timer requests and fewer animations. This is the maximum set this app offers.", Ids = new string[] { "gamemode", "dvr", "gamebar", "startdelay", "prio", "sysresp", "bgapps", "gamestask", "netthr", "pthrottle", "faststart", "plan", "fso", "mouse", "nagle", "timer", "visualfx" } },
            new Level { Name = "Heavy games", Detail = "For big open-world titles: Gaming plus TCP no-delay and honoured timer requests. Keeps your normal look and mouse settings.", Ids = new string[] { "gamemode", "dvr", "gamebar", "startdelay", "prio", "sysresp", "bgapps", "gamestask", "netthr", "pthrottle", "faststart", "plan", "fso", "nagle", "timer" } },
            new Level { Name = "Low-end PC", Detail = "For 8 GB of RAM or fewer, or 4 cores or fewer: fewer animations, no background apps and no recorder, so more memory and CPU is left for the game.", Ids = new string[] { "gamemode", "dvr", "gamebar", "startdelay", "prio", "sysresp", "bgapps", "visualfx" } },
            new Level { Name = "Laptop", Detail = "Keeps battery life: no power-plan or throttling changes.", Ids = new string[] { "gamemode", "dvr", "gamebar", "startdelay", "prio", "sysresp", "bgapps" } }
        };

        /// <summary>Picks a level from the hardware, and says why. No guessing about performance numbers.</summary>
        public static string RecommendLevel(HardwareInfo hw, out string why)
        {
            if (IsLaptop()) { why = "This PC has a battery, so power-hungry settings are left alone."; return "Laptop"; }
            if (hw.RamTotalGB <= 8.5 || (hw.Cores > 0 && hw.Cores <= 4)) { why = "This PC has " + hw.RamTotalGB.ToString("0") + " GB of RAM and " + hw.Cores + " cores, so freeing memory and CPU matters most."; return "Low-end PC"; }
            why = "A plugged-in desktop with " + hw.Cores + " cores and " + hw.RamTotalGB.ToString("0") + " GB of RAM can take the full gaming set. Pick Competitive if you also want mouse acceleration off.";
            return "Gaming";
        }

        // ---- history of every change this app made
        public static string HistoryFile { get { return Path.Combine(Paths.DataDir, "tune-history.txt"); } }
        public static void Record(string line)
        {
            try { File.AppendAllText(HistoryFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "  " + line + "\r\n", Encoding.UTF8); } catch { }
        }
        public static string[] History(int max)
        {
            try { if (!File.Exists(HistoryFile)) return new string[0]; string[] a = File.ReadAllLines(HistoryFile); return a.Reverse().Take(max).ToArray(); }
            catch { return new string[0]; }
        }

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
            t.AppSet = delegate { for (int i = 0; i < e.Length; i++) if (!string.IsNullOrEmpty(Settings.Get("bk." + id + "." + i, ""))) return true; return false; };
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
            t.AppSet = delegate { return !string.IsNullOrEmpty(Settings.Get("bk.plan", "")); };
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

            Tweak vfx = Reg("visualfx", "Low-end PCs", "Fewer animations and no transparency", "Window animations and taskbar effects cost a little GPU and CPU. Useful on weak PCs and integrated graphics. Takes effect after sign-out.",
                new RegEntry("HKCU", @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "string", "0"),
                new RegEntry("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", "dword", 0),
                new RegEntry("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", "dword", 0));
            vfx.Restart = true;
            l.Add(vfx);

            Tweak plan = PowerPlan();
            if (laptop) plan.Skip = "Skipped on laptops: it would drain the battery.";
            l.Add(plan);

            l.Add(Reg("gamebar", "Gaming", "No Game Bar pop-ups", "Stops the Game Bar start-up panel and the controller button from opening the overlay in the middle of a match.",
                new RegEntry("HKCU", @"Software\Microsoft\GameBar", "ShowStartupPanel", "dword", 0),
                new RegEntry("HKCU", @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", "dword", 0)));
            l.Add(Reg("fso", "Gaming", "Fullscreen optimizations off (all games)", "Makes games use true fullscreen instead of Windows' windowed trick, which can add a little input delay. Set per game on the App Profiles page too.",
                new RegEntry("HKCU", @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", "dword", 2),
                new RegEntry("HKCU", @"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", "dword", 1),
                new RegEntry("HKCU", @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", "dword", 1)));
            l.Add(Reg("timer", "Scheduler and responsiveness", "Honour game timer requests", "Lets games ask for a finer system timer (0.5 ms) and keep it. Needs Windows 11 build 22621 or newer; harmless on older builds.",
                new RegEntry("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", "dword", 1)));
            {
                List<RegEntry> nag = new List<RegEntry>();
                try
                {
                    using (RegistryKey ifs = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces"))
                        if (ifs != null)
                            foreach (string n in ifs.GetSubKeyNames())
                                using (RegistryKey k = ifs.OpenSubKey(n))
                                {
                                    string ip = Convert.ToString(k.GetValue("DhcpIPAddress") ?? "");
                                    if (ip.Length == 0) { string[] sa = k.GetValue("IPAddress") as string[]; if (sa != null && sa.Length > 0) ip = Convert.ToString(sa[0]); }
                                    if (ip.Length == 0 || ip == "0.0.0.0") continue;
                                    string sub = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + n;
                                    nag.Add(new RegEntry("HKLM", sub, "TcpAckFrequency", "dword", 1));
                                    nag.Add(new RegEntry("HKLM", sub, "TCPNoDelay", "dword", 1));
                                }
                }
                catch { }
                Tweak nt = Reg("nagle", "Network", "TCP no-delay on your active network adapter", "Sends small TCP packets immediately instead of waiting to bundle them. Helps TCP traffic such as game lobbies and voice chat. Games that use UDP (most shooters) are not affected, so the gain is small.", nag.ToArray());
                if (nag.Count == 0) nt.Skip = "No active network adapter was found.";
                l.Add(nt);
            }

            // AI, ads and privacy. All are policy or per-user switches that Windows itself reads; each one reverts to the original value.
            string ai = "AI, ads and privacy";
            string cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
            l.Add(Reg("copilot", ai, "Turn off Windows Copilot", "Hides the Copilot button and stops it opening. Remove the app itself on the Debloat page.",
                new RegEntry("HKCU", @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", "dword", 1)));
            l.Add(Reg("recall", ai, "Turn off Recall and AI data analysis", "Stops Windows taking snapshots of your screen for AI search. Only exists on newer Windows 11 builds; harmless elsewhere.",
                new RegEntry("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", "dword", 1)));
            l.Add(Reg("bingsearch", ai, "No web results in Start search", "Start search stops sending what you type to Bing. It only searches your PC and apps.",
                new RegEntry("HKCU", @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", "dword", 1)));
            l.Add(Reg("adid", ai, "Turn off the advertising ID", "Stops apps using one ID to track you for ads.",
                new RegEntry("HKCU", @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", "dword", 0)));
            l.Add(Reg("suggest", ai, "No app suggestions, tips or ads in Windows", "Stops Start menu suggestions, lock-screen tips, silently installed sponsored apps and the welcome pop-ups.",
                new RegEntry("HKCU", cdm, "SystemPaneSuggestionsEnabled", "dword", 0),
                new RegEntry("HKCU", cdm, "SilentInstalledAppsEnabled", "dword", 0),
                new RegEntry("HKCU", cdm, "SoftLandingEnabled", "dword", 0),
                new RegEntry("HKCU", cdm, "SubscribedContent-338388Enabled", "dword", 0),
                new RegEntry("HKCU", cdm, "SubscribedContent-338389Enabled", "dword", 0),
                new RegEntry("HKCU", cdm, "SubscribedContent-310093Enabled", "dword", 0),
                new RegEntry("HKCU", cdm, "SubscribedContent-353698Enabled", "dword", 0)));
            l.Add(Reg("consumer", ai, "Stop Windows installing sponsored apps", "Blocks the consumer experience that adds Candy Crush and similar apps after updates and for new accounts.",
                new RegEntry("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", "dword", 1)));
            l.Add(Reg("widgets", ai, "Turn off Widgets and the news feed", "Removes the Widgets board that runs web content in the background.",
                new RegEntry("HKLM", @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", "dword", 0)));
            l.Add(Reg("activity", ai, "Turn off activity history upload", "Stops Windows recording the apps and files you open and syncing that history to Microsoft.",
                new RegEntry("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", "dword", 0),
                new RegEntry("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", "dword", 0)));
            l.Add(Reg("tailored", ai, "No tailored experiences from diagnostic data", "Stops Windows using your diagnostic data to pick tips and ads for you.",
                new RegEntry("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", "dword", 0)));
            foreach (Tweak t in l)
            {
                if (t.Id == "faststart" || t.Id == "plan" || t.Id == "pthrottle" || t.Id == "netthr") t.Risk = "Medium";
                if (t.Id == "mouse" || t.Id == "faststart" || t.Id == "visualfx") t.Restart = true;
                if (t.Group == "AI, ads and privacy") t.Optional = true;
                switch (t.Id)
                {
                    case "dvr": case "pthrottle": case "plan": case "visualfx": case "bgapps": t.Benefit = "Noticeable"; break;
                    case "mouse": t.Benefit = "Aim consistency"; break;
                    case "faststart": t.Benefit = "Stability"; break;
                    case "nagle": case "fso": case "timer": t.Benefit = "Situational"; break;
                    case "startdelay": t.Benefit = "Boot only"; break;
                    default: if (t.Group != "AI, ads and privacy") t.Benefit = "Small"; break;
                }
                if (t.Id == "nagle" || t.Id == "timer" || t.Id == "fso") t.Risk = "Low";
            }
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
            int? hv = Util.RegInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
            if (hv.HasValue && hv.Value == 1) l.Add(new Finding(2, "Memory integrity (core isolation) is on", "It protects against driver-level attacks but costs a few percent in some CPU-heavy games. Turning it off is a security trade-off, so it stays your decision: Windows Security > Device security > Core isolation."));
            else l.Add(new Finding(0, "Memory integrity is off", "That is the faster setting for games. Keep Defender and Windows Update on."));
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
        List<RadioButton> _lvBtns = new List<RadioButton>();
        TextBlock _lvDetail, _lvStatus;
        string _selLevel = Settings.Get("tune_level", "");

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

            // levels: one choice, applied on click, with a live count of how many of its settings are really on
            string why; string rec = TuneKit.RecommendLevel(hw, out why);
            StackPanel lv = new StackPanel();
            lv.Children.Add(UK.T("Recommended for this PC: " + rec + ". " + why, "Body"));
            WrapPanel lb = new WrapPanel(); lb.Margin = new Thickness(0, 12, 0, 4);
            foreach (TuneKit.Level L in TuneKit.Levels)
            {
                TuneKit.Level lvl = L;
                RadioButton rb = new RadioButton { Style = UK.S("Seg"), GroupName = "tunelevel", Tag = lvl, ToolTip = L.Detail, Margin = new Thickness(0, 0, 0, 6) };
                rb.Click += delegate { ApplyLevel(lvl); };
                _lvBtns.Add(rb); lb.Children.Add(rb);
            }
            lv.Children.Add(lb);
            _lvDetail = UK.T("", "Muted"); _lvDetail.Margin = new Thickness(0, 4, 0, 0);
            _lvStatus = UK.T("", "Body"); _lvStatus.Margin = new Thickness(0, 10, 0, 0);
            lv.Children.Add(_lvDetail); lv.Children.Add(_lvStatus);
            lv.Children.Add(UK.T("Clicking a level sets exactly that level: its settings are switched on, and settings this app switched on earlier that are not part of it are put back to your original values. The number on each button is how many of its settings are on right now, read from Windows.", "Muted"));
            ((TextBlock)lv.Children[lv.Children.Count - 1]).Margin = new Thickness(0, 10, 0, 0);
            Body.Children.Add(UK.Section("Profiles", "Pick how far to go", lv, true, null));

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
                            if (want)
                            {
                                string before = SafeCur(t);
                                t.On();
                                Log.Info("Tweak " + t.Id + " -> applied");
                                TuneKit.Record("Applied " + t.Title + "  [" + before + " -> " + SafeCur(t) + "]" + (t.Restart ? "  (needs sign-out or restart)" : ""));
                                _msg.Text = "Applied: " + t.Title + ".";
                            }
                            else if (RevertOne(t, ""))
                            {
                                Log.Info("Tweak " + t.Id + " -> reverted");
                                _msg.Text = "Reverted: " + t.Title + ".";
                            }
                            else _msg.Text = t.Title + NotOursMsg;
                        }
                        catch (Exception ex)
                        {
                            Log.Err(ex);
                            _msg.Text = "Could not change " + t.Title + ": " + ex.Message;
                        }
                        Refresh();
                    };
                    sp.Children.Add(UK.Row(t.Title, t.Skip != null ? t.Skip : t.Why, t.Skip != null ? null : t.Risk, t.Benefit.Length > 0 ? t.Benefit : null, t.CurText, sw, i == g.Count - 1));
                }
                if (g.All(x => x.Optional))
                {
                    List<Tweak> grp = g;
                    StackPanel gb = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                    gb.Children.Add(UK.Btn("Turn all on", false, delegate { SetGroup(grp, true); }));
                    gb.Children.Add(UK.Btn("Turn all off", false, delegate { SetGroup(grp, false); }));
                    sp.Children.Insert(0, gb);
                }
                Body.Children.Add(UK.Section(group, g.Count + " setting" + (g.Count == 1 ? "" : "s") + (g.All(x => x.Optional) ? " - your choice, not part of the recommended set" : ""), sp, true, null));
            }

            // hardware-specific advice
            StackPanel adv = new StackPanel();
            List<Finding> fl = TuneKit.Advice(hw);
            if (fl.Count == 0) adv.Children.Add(UK.T("Nothing else to do for this hardware.", "Muted"));
            foreach (Finding f in fl) adv.Children.Add(Ui.FindingRow(f));
            Body.Children.Add(UK.Section("For your hardware (manual steps)", fl.Count + " item" + (fl.Count == 1 ? "" : "s") + " that need the BIOS or a driver, not Windows", adv, true, null));

            _hist = new TextBox(); _hist.Style = UK.S("Box"); _hist.IsReadOnly = true; _hist.MinHeight = 90; _hist.MaxHeight = 260;
            _hist.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _hist.TextWrapping = TextWrapping.NoWrap; _hist.FontFamily = new System.Windows.Media.FontFamily("Consolas");
            Body.Children.Add(UK.Section("History", "Every change this app has made, newest first", _hist, false, null));

            Refresh();
        }

        TextBox _hist;

        static string SafeCur(Tweak t) { try { return t.Current(); } catch { return "?"; } }

        void ApplyLevel(TuneKit.Level lvl)
        {
            _selLevel = lvl.Name; Settings.Set("tune_level", lvl.Name);
            List<Tweak> inLvl = _tweaks.Where(t => lvl.Ids.Contains(t.Id)).ToList();
            List<Tweak> on = inLvl.Where(t => t.Skip == null && !SafeOn(t)).ToList();
            List<Tweak> off = _tweaks.Where(t => !t.Optional && !lvl.Ids.Contains(t.Id) && t.Skip == null && SafeOn(t) && t.AppSet != null && t.AppSet()).ToList();
            List<Tweak> skipped = inLvl.Where(t => t.Skip != null).ToList();
            if (on.Count == 0 && off.Count == 0)
            {
                _msg.Text = lvl.Name + ": already in place. All " + (inLvl.Count - skipped.Count) + " settings are on" + (skipped.Count > 0 ? " (" + skipped.Count + " skipped on this PC)" : "") + ".";
                Refresh(); return;
            }
            string txt = lvl.Name + " will change " + (on.Count + off.Count) + " setting" + (on.Count + off.Count == 1 ? "" : "s") + ":\n";
            if (on.Count > 0) txt += "\nTurn on:\n  " + string.Join("\n  ", on.Select(t => t.Title + (t.Restart ? "  (needs sign-out)" : "")).ToArray()) + "\n";
            if (off.Count > 0) txt += "\nPut back to your original value:\n  " + string.Join("\n  ", off.Select(t => t.Title).ToArray()) + "\n";
            txt += "\nOriginal values are saved first. Revert all undoes everything.";
            if (Dlg.Show(txt, Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { Refresh(); return; }
            List<string> failed = new List<string>(); int changed = 0;
            foreach (Tweak t in on)
            {
                try
                {
                    string before = SafeCur(t); t.On();
                    if (SafeOn(t)) { changed++; TuneKit.Record("Applied " + t.Title + "  [" + before + " -> " + SafeCur(t) + "]  (profile " + lvl.Name + ")"); }
                    else { failed.Add(t.Title); TuneKit.Record("DID NOT STICK " + t.Title + "  (Windows or a policy changed it back)"); }
                }
                catch (Exception ex) { failed.Add(t.Title + " (" + ex.Message + ")"); Log.Err(ex); TuneKit.Record("FAILED " + t.Title + ": " + ex.Message); }
            }
            foreach (Tweak t in off)
            {
                try { if (RevertOne(t, "profile " + lvl.Name)) changed++; }
                catch (Exception ex) { failed.Add(t.Title + " (" + ex.Message + ")"); Log.Err(ex); }
            }
            Refresh();
            _msg.Text = "Profile " + lvl.Name + ": " + changed + " change" + (changed == 1 ? "" : "s") + " made and checked in Windows" + (failed.Count > 0 ? ". Did not take effect: " + string.Join("; ", failed.ToArray()) : "") + (skipped.Count > 0 ? ". " + skipped.Count + " skipped for this PC" : "") + ". Some changes apply after a sign-out.";
        }

        static bool SafeOn(Tweak t) { try { return t.IsOn(); } catch { return false; } }

        /// <summary>True when this app saved the original value, so a revert can actually put it back.</summary>
        static bool CanRevert(Tweak t) { try { return t.AppSet == null || t.AppSet(); } catch { return false; } }

        /// <summary>Reverts one tweak. Returns true only when something was really put back; a setting this app
        /// never changed has no saved original and is left as it is (and is not logged as "Reverted").</summary>
        static bool RevertOne(Tweak t, string tag)
        {
            if (!CanRevert(t)) { Log.Info("Tweak " + t.Id + " left as is: not set by this app, no original saved"); return false; }
            string before = SafeCur(t);
            t.Off();
            string after = SafeCur(t);
            TuneKit.Record("Reverted " + t.Title + "  [" + before + " -> " + after + "]" + (tag.Length > 0 ? "  (" + tag + ")" : "") + (t.Restart ? "  (needs sign-out or restart)" : ""));
            return true;
        }

        const string NotOursMsg = " was already like this before IXC PC Ready, so there is no original to put back. Change it in Windows Settings if you want it different.";

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
                    if (t.Skip == null && !t.Optional) { total++; if (v) on++; }
                }
                _head.Text = on + " of " + total + " recommended settings applied";
                UpdateLevels();
                if (_hist != null) { string[] h = TuneKit.History(60); _hist.Text = h.Length == 0 ? "Nothing changed yet." : string.Join("\r\n", h); }
            }
            finally { _refreshing = false; }
        }

        void UpdateLevels()
        {
            TuneKit.Level best = null; int bestN = -1; TuneKit.Level sel = null;
            foreach (RadioButton rb in _lvBtns)
            {
                TuneKit.Level L = (TuneKit.Level)rb.Tag;
                List<Tweak> ts = _tweaks.Where(t => L.Ids.Contains(t.Id) && t.Skip == null).ToList();
                int n = ts.Count(t => SafeOn(t));
                rb.Content = L.Name + "   " + n + "/" + ts.Count;
                if (n == ts.Count && ts.Count > bestN) { best = L; bestN = ts.Count; }
                if (L.Name == _selLevel) sel = L;
            }
            TuneKit.Level show = sel ?? best;
            // the remembered choice only counts while it is really in place; otherwise show the best fully matching level
            if (sel != null)
            {
                List<Tweak> st = _tweaks.Where(t => sel.Ids.Contains(t.Id) && t.Skip == null).ToList();
                if (!st.All(t => SafeOn(t)) && best != null) show = best;
            }
            foreach (RadioButton rb in _lvBtns) rb.IsChecked = show != null && ((TuneKit.Level)rb.Tag).Name == show.Name;
            if (show == null) { _lvDetail.Text = "No level is fully in place yet. Click one to apply it."; _lvStatus.Text = ""; return; }
            List<Tweak> ts2 = _tweaks.Where(t => show.Ids.Contains(t.Id) && t.Skip == null).ToList();
            List<Tweak> missing = ts2.Where(t => !SafeOn(t)).ToList();
            int skip = _tweaks.Count(t => show.Ids.Contains(t.Id) && t.Skip != null);
            _lvDetail.Text = show.Detail;
            _lvStatus.Text = missing.Count == 0
                ? "Active: " + show.Name + " - all " + ts2.Count + " settings are on" + (skip > 0 ? " (" + skip + " skipped on this PC)" : "") + "."
                : show.Name + ": " + (ts2.Count - missing.Count) + " of " + ts2.Count + " settings are on. Not on yet: " + string.Join(", ", missing.Select(t => t.Title).Take(6).ToArray()) + (missing.Count > 6 ? "..." : "") + ". Click the level to apply them.";
        }

        void SetGroup(List<Tweak> g, bool apply)
        {
            if (apply && Dlg.Show("Turn on all " + g.Count + " settings in this group?\n\nOriginal values are saved and every switch can be turned back off.", Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            int fail = 0, done = 0, notOurs = 0;
            foreach (Tweak t in g)
            {
                if (t.Skip != null) continue;
                try
                {
                    bool on = t.IsOn(); string before = SafeCur(t);
                    if (apply && !on) { t.On(); done++; TuneKit.Record("Applied " + t.Title + "  [" + before + " -> " + SafeCur(t) + "]  (group)"); }
                    else if (!apply && on) { if (RevertOne(t, "group")) done++; else notOurs++; }
                }
                catch (Exception ex) { fail++; Log.Err(ex); }
            }
            _msg.Text = (apply ? "Turned on " + done + " setting" + (done == 1 ? "" : "s") + " in the group" : "Put back " + done + " setting" + (done == 1 ? "" : "s"))
                + (notOurs > 0 ? ". " + notOurs + " left as is because they were already set before IXC PC Ready (no original to put back)" : "")
                + (fail > 0 ? ". " + fail + " could not be changed" : "") + ".";
            Refresh();
        }

        void ApplyAll(bool apply)
        {
            if (apply)
            {
                MessageBoxResult r = Dlg.Show("Apply every recommended setting?\n\nOriginal values are saved, so Revert all puts everything back.", Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }
            int fail = 0, notOurs = 0;
            foreach (Tweak t in _tweaks)
            {
                if (t.Skip != null || (apply && t.Optional)) continue;
                try
                {
                    bool on = t.IsOn();
                    string before = SafeCur(t);
                    if (apply && !on) { t.On(); TuneKit.Record("Applied " + t.Title + "  [" + before + " -> " + SafeCur(t) + "]  (apply all)"); }
                    else if (!apply && on && !RevertOne(t, "revert all")) notOurs++;
                }
                catch (Exception ex) { fail++; Log.Err(ex); }
            }
            _msg.Text = (apply ? "Applied all recommended settings" : "Put back every setting this app changed")
                + (!apply && notOurs > 0 ? " (" + notOurs + " left as is: they were already set before IXC PC Ready)" : "")
                + (fail > 0 ? " (" + fail + " could not be changed)" : "") + ". Some changes apply after a restart or sign-out.";
            Refresh();
        }
    }
}
