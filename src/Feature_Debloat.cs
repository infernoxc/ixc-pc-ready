using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace InfernoXC
{
    // ------------------------------------------------------------------ Store apps

    public class BloatApp
    {
        public string Pattern, Title, Category, Risk, Why;
        public bool Recommended;
        public string FullName = "", Family = "", Name = "";
        public bool Installed, Selected;
        public ToggleButton Switch;
        public Border RowBox;
    }

    public static class Debloater
    {
        static BloatApp A(string pattern, string title, string cat, string risk, bool rec, string why)
        {
            return new BloatApp { Pattern = pattern, Title = title, Category = cat, Risk = risk, Recommended = rec, Why = why };
        }

        public static List<BloatApp> Catalog()
        {
            string ms = "Microsoft consumer apps", ai = "AI and Copilot", xb = "Xbox and gaming apps", sp = "Sponsored and trial apps", mr = "3D and mixed reality", co = "Communication and phone";
            return new List<BloatApp>
            {
                A("Microsoft.BingNews", "News", ms, "Low", true, "Headlines app. Nothing else depends on it."),
                A("Microsoft.BingWeather", "Weather", ms, "Low", true, "Weather app. The taskbar weather is separate."),
                A("Microsoft.BingFinance", "Money", ms, "Low", true, "Finance news app."),
                A("Microsoft.BingSports", "Sports", ms, "Low", true, "Sports news app."),
                A("Microsoft.BingSearch", "Bing search app", ms, "Low", true, "Web search shortcut."),
                A("Microsoft.MicrosoftSolitaireCollection", "Solitaire and casual games", ms, "Low", true, "Ad-supported card games."),
                A("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office) hub", ms, "Low", true, "An ad for Microsoft 365. Installed Office apps are not affected."),
                A("Microsoft.WindowsFeedbackHub", "Feedback Hub", ms, "Low", true, "Sends feedback to Microsoft."),
                A("Microsoft.GetHelp", "Get Help", ms, "Low", true, "Support chat shortcut."),
                A("Microsoft.Getstarted", "Tips", ms, "Low", true, "Pop-up tips."),
                A("Microsoft.People", "People", ms, "Low", true, "Contacts app."),
                A("Microsoft.WindowsMaps", "Maps", ms, "Low", true, "Offline maps app."),
                A("Microsoft.ZuneVideo", "Movies and TV", ms, "Low", true, "Video store app. VLC and other players are not affected."),
                A("Microsoft.ZuneMusic", "Media Player", ms, "Medium", false, "The built-in music and video player. Keep it if you use it."),
                A("Microsoft.Todos", "Microsoft To Do", ms, "Low", false, "Task list app."),
                A("Microsoft.MicrosoftStickyNotes", "Sticky Notes", ms, "Low", false, "Desktop notes."),
                A("Microsoft.PowerAutomateDesktop", "Power Automate", ms, "Low", true, "Automation tool most people never open."),
                A("Microsoft.Windows.DevHome", "Dev Home", ms, "Low", true, "Developer dashboard."),
                A("Microsoft.OutlookForWindows", "New Outlook", ms, "Low", true, "Mail app. Remove it if you use a browser or another mail program."),
                A("Microsoft.windowscommunicationsapps", "Mail and Calendar (old)", ms, "Low", true, "Retired mail app."),
                A("MicrosoftCorporationII.MicrosoftFamily", "Microsoft Family", ms, "Low", true, "Parental controls front end."),
                A("Clipchamp.Clipchamp", "Clipchamp video editor", ms, "Low", true, "Online-first video editor."),
                A("Microsoft.549981C3F5F10", "Cortana", ms, "Low", true, "Retired assistant."),

                A("Microsoft.Copilot", "Copilot app", ai, "Low", true, "The Copilot assistant app."),
                A("Microsoft.Windows.Ai.Copilot.Provider", "Copilot provider", ai, "Low", true, "Background part of Copilot."),
                A("MicrosoftWindows.Client.WebExperience", "Widgets (Windows Web Experience Pack)", ai, "Medium", false, "Powers the Widgets board and news feed. Removing it turns the Widgets button off."),

                A("Microsoft.XboxApp", "Xbox Console Companion", xb, "Medium", false, "Old Xbox app."),
                A("Microsoft.GamingApp", "Xbox app", xb, "Medium", false, "Needed for Game Pass and Microsoft Store games. Keep it if you use either."),
                A("Microsoft.XboxGamingOverlay", "Xbox Game Bar", xb, "Medium", false, "Win+G overlay and capture. OBS does not need it."),
                A("Microsoft.XboxGameOverlay", "Xbox Game overlay", xb, "Medium", false, "Part of Game Bar."),
                A("Microsoft.XboxSpeechToTextOverlay", "Xbox speech overlay", xb, "Medium", false, "Part of Game Bar."),
                A("Microsoft.Xbox.TCUI", "Xbox sign-in UI", xb, "Medium", false, "Needed by some Store games to sign in."),

                A("Microsoft.MixedReality.Portal", "Mixed Reality Portal", mr, "Low", true, "VR headset setup. Not for SteamVR."),
                A("Microsoft.Microsoft3DViewer", "3D Viewer", mr, "Low", true, "3D model viewer."),
                A("Microsoft.Print3D", "Print 3D", mr, "Low", true, "3D printing app."),
                A("Microsoft.3DBuilder", "3D Builder", mr, "Low", true, "3D modelling app."),

                A("Microsoft.YourPhone", "Phone Link", co, "Medium", false, "Links an Android phone or iPhone. Keep it if you use it."),
                A("Microsoft.SkypeApp", "Skype", co, "Low", true, "Retired chat app."),
                A("MicrosoftTeams", "Microsoft Teams (personal)", co, "Low", true, "Consumer Teams chat."),
                A("MSTeams", "Microsoft Teams (personal, new)", co, "Low", true, "Consumer Teams chat."),

                A("king.com.*", "Candy Crush and King games", sp, "Low", true, "Pre-installed sponsored games."),
                A("*Disney*", "Disney+", sp, "Low", true, "Streaming app placed by the PC maker."),
                A("*TikTok*", "TikTok", sp, "Low", true, "Sponsored app."),
                A("*Instagram*", "Instagram", sp, "Low", true, "Sponsored app."),
                A("*Facebook*", "Facebook", sp, "Low", true, "Sponsored app."),
                A("*Netflix*", "Netflix", sp, "Low", true, "Streaming app. You can still watch in the browser."),
                A("*PrimeVideo*", "Prime Video", sp, "Low", true, "Streaming app."),
                A("*LinkedIn*", "LinkedIn", sp, "Low", true, "Sponsored app."),
                A("*Twitter*", "Twitter / X", sp, "Low", true, "Sponsored app."),
                A("*Hulu*", "Hulu", sp, "Low", true, "Streaming app."),
                A("*AmazonAlexa*", "Amazon Alexa", sp, "Low", true, "Voice assistant app."),
                A("*SpotifyMusic*", "Spotify (Store version)", sp, "Low", false, "Remove only if you use the Spotify desktop installer or the web player.")
            };
        }

        // names that must never be offered or removed
        static readonly string[] NeverRemove = new string[]
        {
            "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.SecHealthUI", "Microsoft.Windows.SecHealthUI",
            "Microsoft.WindowsTerminal", "Microsoft.WindowsCalculator", "Microsoft.WindowsNotepad", "Microsoft.Paint", "Microsoft.MSPaint", "Microsoft.Windows.Photos", "Microsoft.WindowsCamera",
            "Microsoft.XboxIdentityProvider", "Microsoft.WebMediaExtensions", "Microsoft.HEIFImageExtension", "Microsoft.VP9VideoExtensions", "Microsoft.WebpImageExtension",
            "MicrosoftWindows.Client.CBS", "Microsoft.AAD.BrokerPlugin", "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost", "Microsoft.UI.Xaml",
            "NVIDIACorp.NVIDIAControlPanel", "AdvancedMicroDevicesInc-2.AMDRadeonSoftware", "RealtekSemiconductorCorp.RealtekAudioControl", "Microsoft.MicrosoftEdge", "MicrosoftCorporationII.QuickAssist"
        };

        public static bool Protected(string name)
        {
            if (NeverRemove.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) return true;
            string l = name.ToLowerInvariant();
            return l.Contains("vclibs") || l.Contains("net.native") || l.Contains("framework") || l.Contains("webview") || l.Contains("runtime") || l.Contains("nvidia") || l.Contains("radeon") || l.Contains("realtek") || l.Contains("advancedmicro") || l.StartsWith("amd") || l.Contains(".amd") || l.Contains("intelcorp") || l.StartsWith("intel") || l.Contains("security") || l.Contains("riotgames") || l.Contains("valve") || l.Contains(".steam") || l.StartsWith("steam") || l.Contains("battleye");
        }

        static bool Match(string pattern, string name)
        {
            if (pattern.StartsWith("*") && pattern.EndsWith("*") && pattern.Length > 2) return name.IndexOf(pattern.Trim('*'), StringComparison.OrdinalIgnoreCase) >= 0;
            if (pattern.EndsWith("*")) return name.StartsWith(pattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase);
            return string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);
        }

        public class Pkg { public string Name, Full, Family; public bool NonRemovable, Framework; }

        public static List<Pkg> Installed(out string error)
        {
            error = null;
            List<Pkg> l = new List<Pkg>();
            Util.RunResult r = Util.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-AppxPackage | ForEach-Object { $_.Name + '|' + $_.PackageFullName + '|' + $_.PackageFamilyName + '|' + $_.NonRemovable + '|' + $_.IsFramework }\"", 90000);
            if (r.TimedOut) { error = "Windows took too long to list the Store apps."; return l; }
            foreach (string line in (r.Out ?? "").Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = line.Trim().Split('|');
                if (f.Length < 5) continue;
                l.Add(new Pkg { Name = f[0], Full = f[1], Family = f[2], NonRemovable = f[3] == "True", Framework = f[4] == "True" });
            }
            if (l.Count == 0) error = "No Store apps were listed. " + (r.Err ?? "").Trim();
            return l;
        }

        /// <summary>Catalog entries that are installed, plus other removable Store apps for manual choice.</summary>
        public static List<BloatApp> Scan(out string error)
        {
            List<Pkg> pk = Installed(out error);
            List<BloatApp> res = new List<BloatApp>();
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BloatApp c in Catalog())
            {
                foreach (Pkg p in pk)
                {
                    if (p.Framework || p.NonRemovable || Protected(p.Name) || used.Contains(p.Full)) continue;
                    if (!Match(c.Pattern, p.Name)) continue;
                    used.Add(p.Full);
                    res.Add(new BloatApp { Pattern = c.Pattern, Title = c.Title, Category = c.Category, Risk = c.Risk, Recommended = c.Recommended, Why = c.Why, FullName = p.Full, Family = p.Family, Name = p.Name, Installed = true, Selected = c.Recommended });
                }
            }
            foreach (Pkg p in pk.OrderBy(x => x.Name))
            {
                if (p.Framework || p.NonRemovable || Protected(p.Name) || used.Contains(p.Full)) continue;
                if (p.Name.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("windows.", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("Microsoft.UI", StringComparison.OrdinalIgnoreCase)) continue;
                res.Add(new BloatApp { Title = p.Name, Category = "Other Store apps (your choice)", Risk = "Medium", Recommended = false, Why = "Not in the recommended list. Remove it only if you know what it is.", FullName = p.Full, Family = p.Family, Name = p.Name, Installed = true });
            }
            return res;
        }

        static string RemovedFile { get { return Path.Combine(Paths.DataDir, "debloat-removed.tsv"); } }

        public static string Remove(BloatApp a)
        {
            if (Protected(a.Name)) return "Protected app, not removed.";
            if (!System.Text.RegularExpressions.Regex.IsMatch(a.FullName, @"^[A-Za-z0-9._\-~]+$")) return "Unexpected package name.";
            Util.RunResult r = Util.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"Remove-AppxPackage -Package '" + a.FullName + "' -ErrorAction Stop\"", 120000);
            if (r.Code != 0) return ((r.Err ?? "").Trim() + " " + (r.Out ?? "").Trim()).Trim().Split('\n')[0];
            try { File.AppendAllText(RemovedFile, a.Family + "\t" + a.Title + "\t" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\r\n", Encoding.UTF8); } catch { }
            Log.Info("Removed Store app " + a.Name);
            return null;
        }

        public static List<string[]> Removed()
        {
            List<string[]> l = new List<string[]>();
            try { if (File.Exists(RemovedFile)) foreach (string s in File.ReadAllLines(RemovedFile)) { string[] f = s.Split('\t'); if (f.Length >= 3) l.Add(f); } }
            catch { }
            l.Reverse();
            return l;
        }

        public static void Reinstall(string family)
        {
            try { Process.Start(new ProcessStartInfo("ms-windows-store://pdp/?PFN=" + family) { UseShellExecute = true }); } catch (Exception ex) { Log.Err(ex); }
        }
    }

    // ------------------------------------------------------------------ services by category

    public class SvcEntry
    {
        public string Name, Title, Why, Category, Risk;
        public bool Exists;
        public int Start = -1;        // current registry Start value (2 auto, 3 manual, 4 disabled)
        public bool Delayed;
        public ToggleButton Switch;
        public TextBlock State;
    }

    public static class SvcKit
    {
        static SvcEntry S(string cat, string name, string title, string risk, string why) { return new SvcEntry { Category = cat, Name = name, Title = title, Risk = risk, Why = why }; }

        public static List<SvcEntry> Catalog()
        {
            return new List<SvcEntry>
            {
                S("Telemetry and diagnostics", "DiagTrack", "Connected User Experiences and Telemetry", "Low", "Collects usage data and sends it to Microsoft."),
                S("Telemetry and diagnostics", "dmwappushservice", "WAP push message routing", "Low", "Used by telemetry and mobile-style push messages."),
                S("Telemetry and diagnostics", "WerSvc", "Windows Error Reporting", "Low", "Uploads crash reports. Crash Center reads the event log, not this service."),
                S("Telemetry and diagnostics", "PcaSvc", "Program Compatibility Assistant", "Low", "Pops up compatibility prompts for old programs."),
                S("Telemetry and diagnostics", "diagnosticshub.standardcollector.service", "Diagnostics Hub collector", "Low", "Developer diagnostics. Not used while gaming."),

                S("Xbox and Game Pass (only if you do not use them)", "XblAuthManager", "Xbox Live Auth Manager", "Medium", "Sign-in for Xbox services. Games from the Store and Game Pass need it."),
                S("Xbox and Game Pass (only if you do not use them)", "XblGameSave", "Xbox Live Game Save", "Medium", "Cloud saves for Xbox and Store games."),
                S("Xbox and Game Pass (only if you do not use them)", "XboxNetApiSvc", "Xbox Live Networking", "Medium", "Xbox party chat and multiplayer for Store games."),
                S("Xbox and Game Pass (only if you do not use them)", "XboxGipSvc", "Xbox Accessory Management", "Medium", "Xbox controller extras. Controllers still work as plain gamepads."),

                S("Printing and fax", "Spooler", "Print Spooler", "Medium", "Needed to print. Safe to disable on a PC with no printer. Also closes a long-running source of security bugs."),
                S("Printing and fax", "PrintNotify", "Printer Extensions and Notifications", "Low", "Pop-ups from printers."),
                S("Printing and fax", "Fax", "Fax", "Low", "Fax support. Almost nobody uses it."),

                S("Remote access", "RemoteRegistry", "Remote Registry", "Low", "Lets other PCs edit your registry. Off is the safe state."),
                S("Remote access", "TermService", "Remote Desktop Services", "Medium", "Lets you or others connect to this PC remotely. Keep it if you use Remote Desktop."),
                S("Remote access", "SessionEnv", "Remote Desktop Configuration", "Medium", "Goes with Remote Desktop."),
                S("Remote access", "UmRdpService", "Remote Desktop port redirector", "Medium", "Goes with Remote Desktop."),
                S("Remote access", "WinRM", "Windows Remote Management", "Low", "Remote administration. Not needed at home."),
                S("Remote access", "RemoteAccess", "Routing and Remote Access", "Low", "Turns the PC into a router or VPN server."),

                S("Network discovery and sharing", "lltdsvc", "Link-Layer Topology Discovery Mapper", "Low", "Draws the network map. Not needed."),
                S("Network discovery and sharing", "FDResPub", "Function Discovery Resource Publication", "Low", "Shows this PC to others on the network."),
                S("Network discovery and sharing", "SSDPSRV", "SSDP Discovery", "Medium", "Finds UPnP devices. Some routers use UPnP for game port forwarding."),
                S("Network discovery and sharing", "upnphost", "UPnP Device Host", "Medium", "Hosts UPnP devices on this PC. Not needed for games."),
                S("Network discovery and sharing", "SharedAccess", "Internet Connection Sharing", "Low", "Shares this PC's internet with others."),
                S("Network discovery and sharing", "icssvc", "Windows Mobile Hotspot Service", "Low", "Mobile hotspot from this PC."),

                S("Bluetooth and wireless (only if you have none)", "bthserv", "Bluetooth Support Service", "Medium", "Needed for any Bluetooth device. Disable only on a PC with no Bluetooth."),
                S("Bluetooth and wireless (only if you have none)", "BTAGService", "Bluetooth Audio Gateway", "Low", "Bluetooth headsets as speakerphones."),
                S("Bluetooth and wireless (only if you have none)", "WFDSConMgrSvc", "Wi-Fi Direct Services Connection Manager", "Low", "Wi-Fi Direct to printers and TVs."),

                S("Search and indexing", "WSearch", "Windows Search indexer", "Medium", "Makes Start and Explorer searches instant. Without it search is slower but works."),

                S("Location, sensors and consumer extras", "lfsvc", "Geolocation Service", "Low", "Location for apps. Not needed on a desktop."),
                S("Location, sensors and consumer extras", "SensrSvc", "Sensor Monitoring Service", "Low", "Light and tilt sensors, tablets only."),
                S("Location, sensors and consumer extras", "SensorService", "Sensor Service", "Low", "Tablet sensors."),
                S("Location, sensors and consumer extras", "MapsBroker", "Downloaded Maps Manager", "Low", "Offline maps updates."),
                S("Location, sensors and consumer extras", "RetailDemo", "Retail Demo Service", "Low", "Shop demo mode. Never needed."),
                S("Location, sensors and consumer extras", "PhoneSvc", "Phone Service", "Low", "Phone Link calling."),
                S("Location, sensors and consumer extras", "WalletService", "Wallet Service", "Low", "Digital wallet."),
                S("Location, sensors and consumer extras", "WMPNetworkSvc", "Windows Media Player Network Sharing", "Low", "Shares your media library on the network."),
                S("Location, sensors and consumer extras", "TabletInputService", "Touch Keyboard and Handwriting Panel", "Low", "Touch screens and pens only."),
                S("Location, sensors and consumer extras", "SCardSvr", "Smart Card", "Low", "Smart-card readers. Needed in some offices."),
                S("Location, sensors and consumer extras", "ScDeviceEnum", "Smart Card Device Enumeration", "Low", "Goes with Smart Card."),
                S("Location, sensors and consumer extras", "WpcMonSvc", "Parental Controls", "Low", "Family safety rules."),
                S("Location, sensors and consumer extras", "wisvc", "Windows Insider Service", "Low", "Insider preview builds."),

                S("Virtual machine guest services (host PCs do not need them)", "vmicheartbeat", "Hyper-V Heartbeat", "Low", "Only used inside a virtual machine."),
                S("Virtual machine guest services (host PCs do not need them)", "vmicshutdown", "Hyper-V Guest Shutdown", "Low", "Only used inside a virtual machine."),
                S("Virtual machine guest services (host PCs do not need them)", "vmictimesync", "Hyper-V Time Synchronization", "Low", "Only used inside a virtual machine."),
                S("Virtual machine guest services (host PCs do not need them)", "vmickvpexchange", "Hyper-V Data Exchange", "Low", "Only used inside a virtual machine."),
                S("Virtual machine guest services (host PCs do not need them)", "vmicrdv", "Hyper-V Remote Desktop Virtualization", "Low", "Only used inside a virtual machine.")
            };
        }

        const string Key = @"SYSTEM\CurrentControlSet\Services\";

        /// <summary>Fills in which services exist on this PC and their current start type.</summary>
        public static void Read(List<SvcEntry> list)
        {
            foreach (SvcEntry s in list)
            {
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(Key + s.Name))
                    {
                        if (k == null) { s.Exists = false; continue; }
                        s.Exists = true;
                        object v = k.GetValue("Start"); s.Start = v is int ? (int)v : -1;
                        object d = k.GetValue("DelayedAutostart"); s.Delayed = d is int && (int)d == 1;
                    }
                }
                catch { s.Exists = false; }
            }
        }

        public static string StartText(SvcEntry s)
        {
            switch (s.Start) { case 2: return s.Delayed ? "Automatic (delayed)" : "Automatic"; case 3: return "Manual"; case 4: return "Disabled"; case 0: case 1: return "Driver"; default: return "?"; }
        }

        static string Bk(string name) { return "svcbk." + name; }

        /// <summary>Disables a service. The original start type is saved first so Restore puts it back exactly.</summary>
        public static string Disable(SvcEntry s)
        {
            try
            {
                if (s.Start == 4) return null;
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(Key + s.Name, true))
                {
                    if (k == null) return "Service not found.";
                    if (string.IsNullOrEmpty(Settings.Get(Bk(s.Name), "")))
                    {
                        object v = k.GetValue("Start"), d = k.GetValue("DelayedAutostart");
                        Settings.Set(Bk(s.Name), (v is int ? (int)v : 3) + "|" + (d is int ? (int)d : 0));
                    }
                    k.SetValue("Start", 4, RegistryValueKind.DWord);
                }
                Util.Run("sc.exe", "stop \"" + s.Name + "\"", 15000);
                SvcKit.Record("Disabled service " + s.Name + " (" + s.Title + ")");
                return null;
            }
            catch (Exception ex) { Log.Err("disable service " + s.Name, ex); return ex.Message; }
        }

        public static string Restore(SvcEntry s)
        {
            try
            {
                string bk = Settings.Get(Bk(s.Name), "");
                int start = 3, delayed = 0;
                if (bk.Length > 0) { string[] p = bk.Split('|'); int.TryParse(p[0], out start); if (p.Length > 1) int.TryParse(p[1], out delayed); }
                else if (s.Start != 4) return null;   // this app never changed it
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(Key + s.Name, true))
                {
                    if (k == null) return "Service not found.";
                    k.SetValue("Start", start, RegistryValueKind.DWord);
                    if (delayed == 1) k.SetValue("DelayedAutostart", 1, RegistryValueKind.DWord); else k.DeleteValue("DelayedAutostart", false);
                }
                Settings.Set(Bk(s.Name), "");
                SvcKit.Record("Restored service " + s.Name + " to " + (start == 2 ? "automatic" : "manual"));
                return null;
            }
            catch (Exception ex) { Log.Err("restore service " + s.Name, ex); return ex.Message; }
        }

        public static void Record(string line) { TuneKit.Record(line); }
    }

    // ------------------------------------------------------------------ the page

    public class DebloatPage : PageBase
    {
        RadioButton _rApps, _rSvc;
        StackPanel _appsHost, _svcHost;
        TextBlock _head, _msg;
        List<BloatApp> _apps = new List<BloatApp>();
        List<SvcEntry> _svcs;
        bool _loadedApps, _loadingApps, _busy;

        public DebloatPage() : base("Debloat", "Remove Windows apps you never use, and switch off services you do not need. Grouped by category, with the risk shown.")
        {
            StackPanel top = new StackPanel();
            _head = UK.T("", "Big");
            _msg = UK.T("Pick a category below. Nothing changes until you confirm.", "Muted"); _msg.Margin = new Thickness(0, 4, 0, 10);
            top.Children.Add(_head); top.Children.Add(_msg);
            StackPanel seg = new StackPanel { Orientation = Orientation.Horizontal };
            _rApps = new RadioButton { Content = "Windows apps", Style = UK.S("Seg"), IsChecked = true, GroupName = "debloatmode" };
            _rSvc = new RadioButton { Content = "Services", Style = UK.S("Seg"), GroupName = "debloatmode" };
            seg.Children.Add(_rApps); seg.Children.Add(_rSvc);
            top.Children.Add(seg);
            Body.Children.Add(UK.Card(top));

            _appsHost = new StackPanel(); _svcHost = new StackPanel();
            Body.Children.Add(_appsHost); Body.Children.Add(_svcHost);
            RoutedEventHandler re = delegate { SwitchMode(); };
            _rApps.Checked += re; _rSvc.Checked += re;
            _svcs = SvcKit.Catalog();
            SwitchMode();
        }

        public override void Shown() { SwitchMode(); }
        public void ShowAlt() { _rSvc.IsChecked = true; }

        void SwitchMode()
        {
            if (_appsHost == null) return;
            bool apps = _rApps.IsChecked == true;
            _appsHost.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
            _svcHost.Visibility = apps ? Visibility.Collapsed : Visibility.Visible;
            if (apps) { if (!_loadedApps) LoadApps(); else UpdateHeadApps(); }
            else BuildServices();
        }

        // ---------------- apps
        void LoadApps()
        {
            if (_loadingApps) return;
            _loadingApps = true;
            _head.Text = "Reading your Windows apps...";
            UK.Bg(delegate { string err; List<BloatApp> l = Debloater.Scan(out err); return new object[] { l, err }; }, delegate(object o)
            {
                _loadingApps = false; _loadedApps = true;
                object[] r = (object[])o;
                _apps = (List<BloatApp>)r[0];
                if (r[1] != null && _apps.Count == 0) { _head.Text = "Could not read the app list"; _msg.Text = (string)r[1]; }
                BuildApps();
            });
        }

        void UpdateHeadApps()
        {
            int sel = _apps.Count(a => a.Selected);
            foreach (BloatApp a in _apps) if (a.RowBox != null) a.RowBox.Background = a.Selected ? UK.Br("SelBrush") : System.Windows.Media.Brushes.Transparent;
            _head.Text = _apps.Count + " removable app" + (_apps.Count == 1 ? "" : "s") + " found, " + sel + " selected";
        }

        void BuildApps()
        {
            _appsHost.Children.Clear();
            StackPanel bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            bar.Children.Add(UK.Btn("Select recommended", false, delegate { SelectApps(delegate(BloatApp a) { return a.Recommended; }); }));
            bar.Children.Add(UK.Btn("Clear selection", false, delegate { SelectApps(delegate(BloatApp a) { return false; }); }));
            bar.Children.Add(UK.Btn("Remove selected", true, delegate { RemoveSelected(); }));
            bar.Children.Add(UK.Btn("Rescan", false, delegate { _loadedApps = false; LoadApps(); }));
            _appsHost.Children.Add(bar);

            foreach (string cat in _apps.Select(a => a.Category).Distinct())
            {
                List<BloatApp> g = _apps.Where(a => a.Category == cat).ToList();
                StackPanel sp = new StackPanel();
                for (int i = 0; i < g.Count; i++)
                {
                    BloatApp a = g[i];
                    ToggleButton sw = UK.Sw(a.Selected); a.Switch = sw;
                    sw.Click += delegate { a.Selected = sw.IsChecked == true; UpdateHeadApps(); };
                    Border arow = UK.Row(a.Title, a.Why, a.Risk, a.Recommended ? "Recommended" : "Optional", UK.Right(""), sw, i == g.Count - 1);
                    a.RowBox = arow;
                    if (a.Selected) arow.Background = UK.Br("SelBrush");
                    sp.Children.Add(arow);
                }
                _appsHost.Children.Add(UK.Section(cat, g.Count + " found, " + g.Count(x => x.Recommended) + " recommended", sp, !cat.StartsWith("Other") && !cat.StartsWith("Xbox"), null));
            }

            List<string[]> rem = Debloater.Removed();
            if (rem.Count > 0)
            {
                StackPanel rp = new StackPanel();
                for (int i = 0; i < rem.Count; i++)
                {
                    string[] f = rem[i];
                    Button b = UK.Btn("Reinstall from Store", false, delegate { Debloater.Reinstall(f[0]); });
                    b.FontSize = 12;
                    rp.Children.Add(UK.Row(f[1], "Removed " + f[2], null, b, i == rem.Count - 1));
                }
                _appsHost.Children.Add(UK.Section("Removed by this app", rem.Count + " app" + (rem.Count == 1 ? "" : "s") + " - every one can be reinstalled free from the Microsoft Store", rp, false, null));
            }
            TextBlock note = UK.T("Apps are removed for your Windows account only. Windows itself, the Store, Security, Notepad, Paint, Calculator, Photos, Terminal, runtimes and graphics, audio and anti-cheat software are protected and never listed. Store apps have no backup file, so the way back is the Store: use Reinstall.", "Muted");
            note.Margin = new Thickness(0, 4, 0, 0);
            _appsHost.Children.Add(note);
            UpdateHeadApps();
        }

        delegate bool AppPick(BloatApp a);
        void SelectApps(AppPick p)
        {
            foreach (BloatApp a in _apps) { a.Selected = p(a); if (a.Switch != null) a.Switch.IsChecked = a.Selected; }
            UpdateHeadApps();
        }

        void RemoveSelected()
        {
            if (_busy) return;
            List<BloatApp> todo = _apps.Where(a => a.Selected).ToList();
            if (todo.Count == 0) { _msg.Text = "Nothing is selected."; return; }
            string list = string.Join("\n  ", todo.Take(25).Select(a => a.Title + "  [" + a.Risk + "]").ToArray()) + (todo.Count > 25 ? "\n  ... and " + (todo.Count - 25) + " more" : "");
            if (Dlg.Show("Remove " + todo.Count + " app" + (todo.Count == 1 ? "" : "s") + "?\n\n  " + list + "\n\nYou can reinstall any of them from the Microsoft Store.", Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _busy = true; _head.Text = "Removing..."; _msg.Text = "This can take a minute. Please wait.";
            UK.Bg(delegate
            {
                List<string> fails = new List<string>(); int ok = 0;
                foreach (BloatApp a in todo) { string e = Debloater.Remove(a); if (e == null) ok++; else fails.Add(a.Title + ": " + e); }
                return new object[] { ok, fails };
            }, delegate(object o)
            {
                _busy = false;
                object[] r = (object[])o; List<string> fails = (List<string>)r[1];
                _msg.Text = "Removed " + r[0] + " app(s)." + (fails.Count > 0 ? " Could not remove " + fails.Count + ": " + string.Join("; ", fails.Take(3).ToArray()) : "");
                _loadedApps = false; LoadApps();
            });
        }

        // ---------------- services
        void BuildServices()
        {
            SvcKit.Read(_svcs);
            _svcHost.Children.Clear();
            List<SvcEntry> here = _svcs.Where(s => s.Exists).ToList();
            int off = here.Count(s => s.Start == 4);
            _head.Text = here.Count + " optional services on this PC, " + off + " disabled";
            TextBlock warn = UK.T("Disabling takes effect after a restart. The original start type of each service is saved, and Restore puts it back exactly. Security, firewall, Windows Update, networking, audio, graphics, Bluetooth audio drivers and anti-cheat services are not in this list.", "Muted");
            warn.Margin = new Thickness(0, 0, 0, 12);
            _svcHost.Children.Add(warn);

            foreach (string cat in here.Select(s => s.Category).Distinct())
            {
                List<SvcEntry> g = here.Where(s => s.Category == cat).ToList();
                StackPanel sp = new StackPanel();
                StackPanel bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                bar.Children.Add(UK.Btn("Disable all low-risk here", false, delegate { ApplyCategory(g, true); }));
                bar.Children.Add(UK.Btn("Restore all here", false, delegate { ApplyCategory(g, false); }));
                sp.Children.Add(bar);
                for (int i = 0; i < g.Count; i++)
                {
                    SvcEntry s = g[i];
                    ToggleButton sw = UK.Sw(s.Start == 4); s.Switch = sw;
                    s.State = UK.Right(SvcKit.StartText(s));
                    sw.Click += delegate { Toggle(s, sw.IsChecked == true); };
                    Border srow = UK.Row(s.Title, s.Why + "\n" + s.Name + "  -  switch on = disabled", s.Risk, s.Risk == "Low" ? "Recommended" : s.Risk == "Medium" ? "Only if you know you do not need it" : "Avoid", s.State, sw, i == g.Count - 1);
                    if (s.Start == 4) srow.Background = UK.Br("SelBrush");
                    SvcEntry sref = s; sw.Click += delegate { srow.Background = sw.IsChecked == true ? UK.Br("SelBrush") : System.Windows.Media.Brushes.Transparent; };
                    sp.Children.Add(srow);
                }
                _svcHost.Children.Add(UK.Section(cat, g.Count + " service" + (g.Count == 1 ? "" : "s") + ", " + g.Count(x => x.Start == 4) + " disabled", sp, g.Any(x => x.Risk == "Low") && !cat.StartsWith("Xbox"), null));
            }
        }

        void Toggle(SvcEntry s, bool disable)
        {
            string err = disable ? SvcKit.Disable(s) : SvcKit.Restore(s);
            if (err != null) { _msg.Text = "Could not change " + s.Title + ": " + err; s.Switch.IsChecked = !disable; return; }
            SvcKit.Read(new List<SvcEntry> { s });
            s.State.Text = SvcKit.StartText(s);
            _msg.Text = (disable ? "Disabled: " : "Restored: ") + s.Title + ". Takes effect after a restart.";
        }

        void ApplyCategory(List<SvcEntry> g, bool disable)
        {
            int n = 0;
            foreach (SvcEntry s in g)
            {
                if (disable && s.Risk != "Low") continue;
                string err = disable ? SvcKit.Disable(s) : SvcKit.Restore(s);
                if (err == null) n++;
            }
            _msg.Text = (disable ? "Disabled " : "Restored ") + n + " service(s). Takes effect after a restart.";
            BuildServices();
        }
    }
}
