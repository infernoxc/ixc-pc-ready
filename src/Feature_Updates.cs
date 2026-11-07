using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Win32;

namespace InfernoXC
{
    // ------------------------------------------------------------------ app updates (winget)

    public class AppUpdate
    {
        public string Name, Id, Version, Available, Source;
        public bool Selected = true;
        public bool Tracked = true;
        public string Status = "update";     // update | ok | untracked
        public ToggleButton Switch;
        public TextBlock State;
        public Border RowBox;
    }

    public static class UpdateKit
    {
        const string TaskName = "IXC PC Ready App Updates";
        static readonly Regex IdOk = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._\-+]{1,120}$");

        public static bool HasWinget(out string version)
        {
            Util.RunResult r = Util.Run("winget.exe", "--version", 20000);
            version = (r.Out ?? "").Trim();
            return r.Code == 0 && version.Length > 0;
        }

        /// <summary>Lists apps that winget knows a newer version for. Column positions come from the header line, so it works in any language.</summary>
        public static List<AppUpdate> Scan(out string error)
        {
            error = null;
            List<AppUpdate> l = new List<AppUpdate>();
            Util.RunResult r = Util.Run("winget.exe", "upgrade --include-unknown --accept-source-agreements --disable-interactivity", 180000);
            if (r.TimedOut) { error = "winget took too long to answer."; return l; }
            return ParseTable(r.Out, r.Code, out error);
        }

        public static List<AppUpdate> ParseTable(string text, int code, out string error)
        {
            error = null;
            List<AppUpdate> l = new List<AppUpdate>();
            string[] lines = (text ?? "").Replace("\r", "").Split('\n');
            int dash = -1;
            for (int i = 1; i < lines.Length; i++) if (lines[i].StartsWith("---") && lines[i - 1].Trim().Length > 5) { dash = i; break; }
            if (dash < 0) { if ((text ?? "").Trim().Length > 0 && code != 0) error = LastLine(text); return l; }
            List<int> starts = new List<int>();
            MatchCollection words = Regex.Matches(lines[dash - 1], @"\S+");
            // English header: five single words (columns can be only one space apart). Other languages: split where there are two or more spaces.
            foreach (Match m in (words.Count == 5 ? words : Regex.Matches(lines[dash - 1], @"\S+(?: \S+)*"))) starts.Add(m.Index);
            if (starts.Count < 4) { error = "winget returned a table this app could not read."; return l; }
            for (int i = dash + 1; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (ln.Trim().Length == 0) break;
                if (Regex.IsMatch(ln.Trim(), @"^\d+\s+(upgrades?|packages?)\b.*(available|following)", RegexOptions.IgnoreCase) || ln.Trim().EndsWith("available.", StringComparison.OrdinalIgnoreCase)) break;
                if (ln.Length <= starts[1]) continue;
                string[] f = new string[5];
                for (int c = 0; c < 5; c++)
                {
                    if (c >= starts.Count || starts[c] >= ln.Length) { f[c] = ""; continue; }
                    int end = c + 1 < starts.Count ? Math.Min(starts[c + 1], ln.Length) : ln.Length;
                    f[c] = ln.Substring(starts[c], end - starts[c]).Trim();
                }
                if (f[1].Length == 0 || f[1].Contains(" ") || !IdOk.IsMatch(f[1]) || f[2].TrimStart('<', '>', ' ').Length == 0) continue;   // summary lines like "3 upgrades available."
                l.Add(new AppUpdate { Name = f[0], Id = f[1], Version = f[2].TrimStart('<', '>', ' '), Available = f[3], Source = f[4] });
            }
            return l;
        }

        static string LastLine(string s)
        {
            string[] a = (s ?? "").Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 3 && !x.All(ch => "-\\|/ ".IndexOf(ch) >= 0)).ToArray();
            return a.Length == 0 ? "" : a[a.Length - 1];
        }

        /// <summary>Everything winget can see as installed, with update status. Apps winget cannot match to a package come back with Tracked = false.</summary>
        public static List<AppUpdate> ListAll(out string error)
        {
            error = null;
            List<AppUpdate> all = new List<AppUpdate>();
            Util.RunResult r = Util.Run("winget.exe", "list --accept-source-agreements --disable-interactivity", 180000);
            if (r.TimedOut) { error = "winget took too long to list the installed apps."; return all; }
            string perr;
            List<AppUpdate> l = ParseAll(r.Out, r.Code, out perr);
            if (perr != null) error = perr;
            return l;
        }

        /// <summary>Same table reader as the update list, but keeps every row (installed apps that winget cannot track included) and copes with a missing Available column.</summary>
        public static List<AppUpdate> ParseAll(string text, int code, out string error)
        {
            error = null;
            List<AppUpdate> l = new List<AppUpdate>();
            string[] lines = (text ?? "").Replace("\r", "").Split('\n');
            int dash = -1;
            for (int i = 1; i < lines.Length; i++) if (lines[i].StartsWith("---") && lines[i - 1].Trim().Length > 5) { dash = i; break; }
            if (dash < 0) { if ((text ?? "").Trim().Length > 0 && code != 0) error = LastLine(text); return l; }
            List<int> starts = new List<int>();
            MatchCollection words = Regex.Matches(lines[dash - 1], @"\S+");
            foreach (Match m in ((words.Count == 4 || words.Count == 5) ? words : Regex.Matches(lines[dash - 1], @"\S+(?: \S+)*"))) starts.Add(m.Index);
            if (starts.Count < 3) { error = "winget returned a table this app could not read."; return l; }
            bool hasAvail = starts.Count >= 5;
            for (int i = dash + 1; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (ln.Trim().Length == 0) break;
                if (Regex.IsMatch(ln.Trim(), @"^\d+\s+(upgrades?|packages?)\b.*(available|following)", RegexOptions.IgnoreCase) || ln.Trim().EndsWith("available.", StringComparison.OrdinalIgnoreCase)) break;
                if (ln.Length <= starts[1]) continue;
                string[] f = new string[starts.Count];
                for (int c = 0; c < starts.Count; c++)
                {
                    if (starts[c] >= ln.Length) { f[c] = ""; continue; }
                    int end = c + 1 < starts.Count ? Math.Min(starts[c + 1], ln.Length) : ln.Length;
                    f[c] = ln.Substring(starts[c], end - starts[c]).Trim();
                }
                if (f[0].Length == 0 || f[1].Length == 0) continue;
                if (f[0].EndsWith("available.") || f[0].IndexOf("upgrades available", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                AppUpdate a = new AppUpdate();
                a.Name = f[0]; a.Id = f[1]; a.Version = f[2].TrimStart('<', '>', ' ');
                a.Available = hasAvail ? f[3] : "";
                a.Source = starts.Count >= 4 ? f[starts.Count - 1] : "";
                if (!hasAvail && starts.Count == 4) a.Source = f[3];
                a.Tracked = a.Source.Length > 0 && IdOk.IsMatch(a.Id);
                a.Status = a.Available.Length > 0 && a.Tracked ? "update" : a.Tracked ? "ok" : "untracked";
                a.Selected = a.Status == "update";
                l.Add(a);
            }
            return l;
        }

        // ---- running an update with live output

        static void ReadStream(StreamReader r, Action<string> line, StringBuilder all)
        {
            StringBuilder cur = new StringBuilder();
            int lastPct = -100;
            Action flush = delegate
            {
                string t = cur.ToString().Trim(); cur.Length = 0;
                if (t.Length == 0 || t.All(ch => "-\\|/ ".IndexOf(ch) >= 0)) return;
                if (t.IndexOf('\u2588') >= 0 || t.IndexOf('\u2592') >= 0)
                {
                    Match pm = Regex.Match(t, @"(\d+)\s*%");
                    int pct = pm.Success ? int.Parse(pm.Groups[1].Value) : -1;
                    if (pct >= 0 && pct < 100 && pct - lastPct < 10) return;
                    if (pct >= 0) lastPct = pct;
                    t = "Downloading  " + (pm.Success ? pm.Groups[1].Value + "%" : "") + "  " + Regex.Replace(t, @"[\u2588\u2592\s]+", " ").Trim();
                }
                lock (all) all.AppendLine(t);
                if (line != null) line(t);
            };
            int ch2;
            while ((ch2 = r.Read()) >= 0)
            {
                char c = (char)ch2;
                if (c == '\r' || c == '\n') flush(); else cur.Append(c);
            }
            flush();
        }

        public static int RunLive(string file, string args, int timeoutMs, Action<string> line, out string output, out bool timedOut)
        {
            timedOut = false;
            StringBuilder all = new StringBuilder();
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
            using (Process p = Process.Start(psi))
            {
                System.Threading.Thread t1 = new System.Threading.Thread(delegate() { try { ReadStream(p.StandardOutput, line, all); } catch { } });
                System.Threading.Thread t2 = new System.Threading.Thread(delegate() { try { ReadStream(p.StandardError, line, all); } catch { } });
                t1.IsBackground = t2.IsBackground = true; t1.Start(); t2.Start();
                if (!p.WaitForExit(timeoutMs)) { timedOut = true; try { p.Kill(); } catch { } }
                t1.Join(3000); t2.Join(3000);
                output = all.ToString();
                return timedOut ? -1 : p.ExitCode;
            }
        }

        public static string LogFile { get { return Path.Combine(Paths.DataDir, "app-update-last.log"); } }

        /// <summary>Processes that belong to an installed app (matched by its install folder), so the page can offer to close them first.</summary>
        public static List<Process> RunningFor(AppUpdate a)
        {
            List<Process> l = new List<Process>();
            try
            {
                string key = (a.Name ?? "").ToLowerInvariant();
                AppRow row = AppReader.ReadInstalled().FirstOrDefault(x => x.Name != null && (x.Name.ToLowerInvariant() == key || x.Name.ToLowerInvariant().StartsWith(key) || key.StartsWith(x.Name.ToLowerInvariant())) && !string.IsNullOrEmpty(x.LocLower));
                if (row == null) return l;
                string me = Process.GetCurrentProcess().ProcessName;
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        string path = Native.ProcPath(p.Id);
                        if (path != null && path.ToLowerInvariant().StartsWith(row.LocLower) && !p.ProcessName.Equals(me, StringComparison.OrdinalIgnoreCase)) l.Add(p); else p.Dispose();
                    }
                    catch { p.Dispose(); }
                }
            }
            catch (Exception ex) { Log.Err("running for", ex); }
            return l;
        }

        public static string CloseProcesses(List<Process> ps)
        {
            foreach (Process p in ps) { try { p.CloseMainWindow(); } catch { } }
            for (int i = 0; i < 16; i++) { System.Threading.Thread.Sleep(250); if (ps.All(p => { try { return p.HasExited; } catch { return true; } })) break; }
            int forced = 0;
            foreach (Process p in ps) { try { if (!p.HasExited) { p.Kill(); forced++; } } catch { } }
            System.Threading.Thread.Sleep(600);
            return forced > 0 ? "closed (" + forced + " had to be forced)" : "closed";
        }

        /// <summary>winget 0x8A150056: the installer cannot run from an administrator context (Spotify and other per-user apps).</summary>
        public const int ErrUserOnly = unchecked((int)0x8A150056);
        public static bool IsUserOnly(int code, string lowOutput)
        {
            return code == ErrUserOnly || (lowOutput ?? "").Contains("cannot be run from an administrator context");
        }
        const string UserOnlyKey = "upd_user_only";
        public static bool KnownUserOnly(string id)
        {
            return Settings.Get(UserOnlyKey, "").Split(',').Any(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
        static void RememberUserOnly(string id)
        {
            if (KnownUserOnly(id)) return;
            string cur = Settings.Get(UserOnlyKey, "");
            Settings.Set(UserOnlyKey, cur.Length == 0 ? id : cur + "," + id);
        }

        /// <summary>Turns winget's output and exit code into a plain reason. Returns null when the update worked.</summary>
        public static string Explain(int code, string output, bool timedOut)
        {
            if (timedOut) return "It ran for more than 25 minutes, so it was stopped. The installer may be waiting for something on screen or for another install to finish.";
            string o = output ?? "";
            string low = o.ToLowerInvariant();
            Match im = Regex.Match(o, @"exit code:?\s*(-?\d+)", RegexOptions.IgnoreCase);
            int inner = im.Success ? int.Parse(im.Groups[1].Value) : int.MinValue;
            if (code == 0 || inner == 3010 || inner == 1641 || low.Contains("successfully installed")) return null;
            string hex = "0x" + ((uint)code).ToString("X8");
            string why;
            if (IsUserOnly(code, low)) why = "This app only installs for your own Windows account, and its installer refuses to run from an administrator program like this one. Open the app and update it from inside (Spotify updates itself), or run 'winget upgrade' in a normal, non-admin window. It is skipped in Update all from now on.";
            else if (low.Contains("no installed package found") || low.Contains("no applicable upgrade") || low.Contains("no newer package")) why = "winget cannot match the copy on this PC to the package it knows (it was installed another way, or it is a different edition). Update it inside the app, or reinstall it once through winget.";
            else if (low.Contains("hash") && (low.Contains("does not match") || low.Contains("mismatch"))) why = "The downloaded installer did not match the published checksum, so winget refused to run it. The publisher may have just replaced the file. Try again later.";
            else if (low.Contains("failed in attempting to update the source") || low.Contains("unable to connect") || low.Contains("download failed") || low.Contains("0x80072ee7") || low.Contains("0x80072efd")) why = "winget could not reach its servers or the download site. Check the internet connection, a VPN or a firewall, then try again.";
            else if (low.Contains("does not support the installation") || low.Contains("no applicable installer")) why = "This package has no installer for this PC (architecture, Windows version or install scope).";
            else if (low.Contains("in use") || low.Contains("is running") || low.Contains("close the application")) why = "The app is open. Close it and run the update again.";
            else if (inner == 1618) why = "Another installation is running right now (Windows Installer error 1618). Wait for it to finish, then try again.";
            else if (inner == 1602 || inner == 1223) why = "The installer was cancelled before it finished.";
            else if (inner == 5 || inner == 740) why = "The installer was refused permission (error " + inner + ").";
            else if (inner == 1603) why = "The installer stopped with a fatal error (1603). This is most often because the app was still open or one of its files was locked.";
            else if (inner != int.MinValue) why = "The installer itself failed with its own exit code " + inner + ".";
            else why = "winget stopped with an error it did not describe.";
            string[] lines = o.Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 4 && !x.StartsWith("Downloading")).ToArray();
            string tail = lines.Length == 0 ? "" : "\nwinget said: " + string.Join(" / ", lines.Skip(Math.Max(0, lines.Length - 3)).ToArray());
            return why + "\nTechnical: exit code " + hex + (inner != int.MinValue ? ", installer code " + inner : "") + "." + tail;
        }

        /// <summary>Updates one app silently while streaming winget's own output. Returns null on success, otherwise the reason.</summary>
        public static string UpdateLive(AppUpdate a, Action<string> line)
        {
            if (!IdOk.IsMatch(a.Id)) return "Unexpected package id.";
            if (KnownUserOnly(a.Id))
            {
                string skip = "Skipped: " + a.Name + " only updates from your own (non-admin) account. Open it and let it update itself.";
                if (line != null) line(skip);
                return skip;
            }
            string log = Path.Combine(Paths.Sub("scripts"), "winget-" + Regex.Replace(a.Id, @"[^A-Za-z0-9.]", "_") + ".log");
            string args = "upgrade --id \"" + a.Id + "\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --log \"" + log + "\"";
            string output; bool timedOut;
            int code = RunLive("winget.exe", args, 1500000, line, out output, out timedOut);
            if (code != 0 && (output ?? "").IndexOf("--log", StringComparison.OrdinalIgnoreCase) >= 0 && (output ?? "").IndexOf("not", StringComparison.OrdinalIgnoreCase) >= 0 && (output ?? "").Length < 600)
                code = RunLive("winget.exe", args.Substring(0, args.IndexOf(" --log")), 1500000, line, out output, out timedOut);
            string reason = Explain(code, output, timedOut);
            try { File.WriteAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + a.Name + " (" + a.Id + ")  exit " + code + "\r\n" + output + "\r\n" + (reason ?? "OK") + "\r\nwinget log: " + log, Encoding.UTF8); } catch { }
            if (reason == null) { Log.Info("Updated " + a.Name + " to " + a.Available + " (winget)"); TuneKit.Record("Updated app " + a.Name + " to " + a.Available); return null; }
            if (IsUserOnly(code, (output ?? "").ToLowerInvariant())) RememberUserOnly(a.Id);
            Log.Info("Update of " + a.Name + " failed: " + reason.Replace("\n", " | "));
            return reason + "\nFull log: " + log;
        }

        /// <summary>Kept for the scheduled task and tests.</summary>
        public static string Update(AppUpdate a) { return UpdateLive(a, null); }

        public static int RunHeadless()
        {
            try
            {
                string v; if (!HasWinget(out v)) { Log.Info("App updates skipped: winget not found"); return 2; }
                Util.RunResult r = Util.Run("winget.exe", "upgrade --all --silent --accept-package-agreements --accept-source-agreements --disable-interactivity", 3000000);
                try { File.AppendAllText(Path.Combine(Paths.DataDir, "app-update-log.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\r\n" + (r.Out ?? "") + "\r\n", Encoding.UTF8); } catch { }
                Log.Info("Scheduled app update finished, code " + r.Code);
                return 0;
            }
            catch (Exception ex) { Log.Err(ex); return 1; }
        }

        public static string ScheduleState()
        {
            Util.RunResult r = Util.Run("schtasks.exe", "/Query /TN \"" + TaskName + "\" /FO LIST /V", 15000);
            if (r.Code != 0) return "Off";
            return (r.Out ?? "").IndexOf("Monthly", StringComparison.OrdinalIgnoreCase) >= 0 ? "Monthly" : "Weekly";
        }

        public static string SetSchedule(string mode)
        {
            if (mode == "Off") { Util.Run("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F", 15000); return null; }
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string sc = mode == "Monthly" ? "/SC MONTHLY /D 1" : "/SC WEEKLY /D SUN";
            Util.RunResult r = Util.Run("schtasks.exe", "/Create /TN \"" + TaskName + "\" /TR \"\\\"" + exe + "\\\" --appupdate\" " + sc + " /ST 13:00 /RL HIGHEST /F", 20000);
            return r.Code == 0 ? null : ((r.Out ?? "") + (r.Err ?? "")).Trim();
        }
    }

    // ------------------------------------------------------------------ drivers

    public class DriverRow
    {
        public string Category, Device, Maker, Version, Link, LinkText, Note;
        public DateTime? Date;
        public int AgeMonths = -1;
        public bool Builtin, Old;
    }

    public class WuDriver
    {
        public string Id, Title, Maker, Model, Date, Cls;
        public long Size;
        public TextBlock State;
        public Button Btn;
    }

    public static class DriverKit
    {
        static readonly Regex Guid = new Regex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");

        static string Cat(string cls, string name)
        {
            switch ((cls ?? "").ToUpperInvariant())
            {
                case "DISPLAY": return "Graphics";
                case "NET": return "Network";
                case "MEDIA": return "Audio";
                case "BLUETOOTH": return "Bluetooth";
                case "HDC": case "SCSIADAPTER": case "DISKDRIVE": return "Storage";
                case "SYSTEM": return Regex.IsMatch(name ?? "", @"AMD|Chipset|Management Engine|SMBus|PSP|GPIO|Ryzen|Intel\(R\).*(Host Bridge|LPC|PCI Express Root)", RegexOptions.IgnoreCase) ? "Chipset and system" : null;
                default: return null;
            }
        }

        static void Link(DriverRow d)
        {
            string m = (d.Maker ?? "") + " " + (d.Device ?? "");
            if (Regex.IsMatch(m, "nvidia", RegexOptions.IgnoreCase)) { d.Link = "https://www.nvidia.com/en-us/drivers/"; d.LinkText = "NVIDIA drivers"; }
            else if (Regex.IsMatch(m, @"advanced micro|\bAMD\b|radeon|ryzen", RegexOptions.IgnoreCase)) { d.Link = "https://www.amd.com/en/support/download/drivers.html"; d.LinkText = "AMD drivers"; }
            else if (Regex.IsMatch(m, "intel", RegexOptions.IgnoreCase)) { d.Link = "https://www.intel.com/content/www/us/en/support/detect.html"; d.LinkText = "Intel driver tool"; }
            else if (Regex.IsMatch(m, "realtek", RegexOptions.IgnoreCase)) { d.Link = "https://www.realtek.com/Download"; d.LinkText = "Realtek downloads"; }
            else if (Regex.IsMatch(m, "qualcomm|atheros|killer|mediatek|ralink|broadcom", RegexOptions.IgnoreCase)) { d.Link = "ms-settings:windowsupdate-optionalupdates"; d.LinkText = "Windows optional updates"; }
        }

        /// <summary>Drivers that matter for games and stability, with the date Windows has on record for each.</summary>
        public static List<DriverRow> Installed(out List<string> problems)
        {
            problems = new List<string>();
            List<DriverRow> res = new List<DriverRow>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT DeviceName,Manufacturer,DriverVersion,DriverDate,DeviceClass,DriverProviderName FROM Win32_PnPSignedDriver"))
                    foreach (ManagementObject o in s.Get())
                    {
                        string name = Convert.ToString(o["DeviceName"]);
                        string cat = Cat(Convert.ToString(o["DeviceClass"]), name);
                        if (cat == null || string.IsNullOrEmpty(name)) continue;
                        string maker = Convert.ToString(o["Manufacturer"]);
                        string prov = Convert.ToString(o["DriverProviderName"]);
                        if (Regex.IsMatch(name, @"Virtual|WAN Miniport|Kernel Debug|Bluetooth Device \(|Microsoft Basic|Remote Desktop|Teredo|ISATAP|6to4|Loopback|Hyper-V|Standard (SATA|NVM)", RegexOptions.IgnoreCase) && cat != "Graphics") continue;
                        string ver = Convert.ToString(o["DriverVersion"]);
                        DriverRow d = new DriverRow { Category = cat, Device = name, Maker = string.IsNullOrEmpty(maker) ? prov : maker, Version = ver };
                        try { string ds = Convert.ToString(o["DriverDate"]); if (!string.IsNullOrEmpty(ds)) d.Date = ManagementDateTimeConverter.ToDateTime(ds); } catch { }
                        if (!seen.Add(cat + "|" + name + "|" + ver)) continue;
                        d.Builtin = Regex.IsMatch(d.Maker ?? "", "^Microsoft", RegexOptions.IgnoreCase) || (d.Date.HasValue && d.Date.Value.Year <= 2009);
                        if (d.Date.HasValue)
                        {
                            DateTime n = DateTime.Now;
                            d.AgeMonths = (n.Year - d.Date.Value.Year) * 12 + n.Month - d.Date.Value.Month;
                            int limit = cat == "Graphics" ? 9 : cat == "Chipset and system" ? 24 : 30;
                            d.Old = !d.Builtin && d.AgeMonths > limit;
                        }
                        Link(d);
                        res.Add(d);
                    }
            }
            catch (Exception ex) { Log.Err("driver list", ex); }
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Name,ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
                    foreach (ManagementObject o in s.Get())
                    {
                        int code = Convert.ToInt32(o["ConfigManagerErrorCode"]);
                        if (code == 22) continue;   // disabled on purpose
                        problems.Add(Convert.ToString(o["Name"]) + "  (error code " + code + (code == 28 ? ": no driver installed" : code == 10 ? ": device cannot start" : "") + ")");
                    }
            }
            catch { }
            return res.OrderBy(x => x.Category).ThenBy(x => x.Device).ToList();
        }

        public static string AgeText(DriverRow d)
        {
            if (d.AgeMonths < 0) return "date unknown";
            if (d.Builtin && d.AgeMonths > 30) return "Windows built-in driver";
            if (d.AgeMonths < 1) return "this month";
            if (d.AgeMonths < 24) return d.AgeMonths + " month" + (d.AgeMonths == 1 ? "" : "s") + " old";
            return (d.AgeMonths / 12) + " years old";
        }

        /// <summary>Whether Windows Update is allowed to deliver drivers on its own.</summary>
        public static string AutoDriverState()
        {
            int? p = Util.RegInt(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate");
            if (p.HasValue && p.Value == 1) return "Windows Update is blocked from delivering drivers (a policy or tweak on this PC sets that).";
            int? m = Util.RegInt(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Device Metadata", "PreventDeviceMetadataFromNetwork");
            int? s = Util.RegInt(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", "SearchOrderConfig");
            if (s.HasValue && s.Value == 0) return "Windows is set not to look for drivers on Windows Update by itself, so none arrive automatically. The search button below still checks Windows Update when you ask.";
            return "Windows Update may deliver driver updates automatically with its monthly updates. You can also search for them yourself below.";
        }

        static string Script(string body)
        {
            string f = Path.Combine(Paths.Sub("scripts"), "wu-" + Guid_N() + ".ps1");
            File.WriteAllText(f, body, new UTF8Encoding(true));
            return f;
        }
        static string Guid_N() { return System.Guid.NewGuid().ToString("N").Substring(0, 8); }

        public static List<WuDriver> SearchWindowsUpdate(out string error)
        {
            error = null;
            List<WuDriver> l = new List<WuDriver>();
            string f = null;
            try
            {
                f = Script(@"$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.Encoding]::UTF8
try {
  $s=New-Object -ComObject Microsoft.Update.Session
  $se=$s.CreateUpdateSearcher()
  $r=$se.Search(""IsInstalled=0 and Type='Driver' and IsHidden=0"")
  foreach($u in $r.Updates){
    $d=''; try{$d=$u.DriverVerDate.ToString('yyyy-MM-dd')}catch{}
    $sz=0; try{$sz=[int64]$u.MaxDownloadSize}catch{}
    Write-Output ('DRV|' + $u.Identity.UpdateID + '|' + $u.Title + '|' + $u.DriverManufacturer + '|' + $u.DriverModel + '|' + $d + '|' + $u.DriverClass + '|' + $sz)
  }
  Write-Output 'DONE'
} catch { Write-Output ('ERR|' + $_.Exception.Message) }");
                Util.RunResult r = Util.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + f + "\"", 240000);
                if (r.TimedOut) { error = "Windows Update took too long to answer."; return l; }
                bool done = false;
                foreach (string line in (r.Out ?? "").Replace("\r", "").Split('\n'))
                {
                    if (line.StartsWith("ERR|")) { error = "Windows Update said: " + line.Substring(4); return l; }
                    if (line.StartsWith("DONE")) done = true;
                    if (!line.StartsWith("DRV|")) continue;
                    string[] p = line.Split('|');
                    if (p.Length < 8 || !Guid.IsMatch(p[1])) continue;
                    long sz; long.TryParse(p[7], out sz);
                    l.Add(new WuDriver { Id = p[1], Title = p[2], Maker = p[3], Model = p[4], Date = p[5], Cls = p[6], Size = sz });
                }
                if (!done && l.Count == 0) error = "Windows Update gave no answer. " + (r.Err ?? "").Trim();
            }
            catch (Exception ex) { Log.Err("wu search", ex); error = ex.Message; }
            finally { try { if (f != null) File.Delete(f); } catch { } }
            return l;
        }

        /// <summary>Downloads and installs one driver offered by Windows Update. Returns null on success; reboot says if a restart is needed.</summary>
        public static string InstallWindowsUpdate(WuDriver d, out bool reboot)
        {
            reboot = false;
            if (!Guid.IsMatch(d.Id)) return "Unexpected update id.";
            string f = null;
            try
            {
                f = Script(@"$ErrorActionPreference='Stop'
try {
  $id='" + d.Id + @"'
  $s=New-Object -ComObject Microsoft.Update.Session
  $se=$s.CreateUpdateSearcher()
  $r=$se.Search(""IsInstalled=0 and Type='Driver' and IsHidden=0"")
  $c=New-Object -ComObject Microsoft.Update.UpdateColl
  foreach($u in $r.Updates){ if($u.Identity.UpdateID -eq $id){ if(-not $u.EulaAccepted){$u.AcceptEula()}; [void]$c.Add($u) } }
  if($c.Count -eq 0){ Write-Output 'RES|NOTFOUND'; exit 0 }
  $dl=$s.CreateUpdateDownloader(); $dl.Updates=$c; [void]$dl.Download()
  $in=$s.CreateUpdateInstaller(); $in.Updates=$c; $ir=$in.Install()
  Write-Output ('RES|' + $ir.ResultCode + '|' + $ir.RebootRequired)
} catch { Write-Output ('RES|ERR|' + $_.Exception.Message) }");
                Util.RunResult r = Util.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + f + "\"", 1500000);
                if (r.TimedOut) return "Took more than 25 minutes, stopped waiting.";
                string res = (r.Out ?? "").Replace("\r", "").Split('\n').FirstOrDefault(x => x.StartsWith("RES|"));
                if (res == null) return "Windows Update gave no answer.";
                string[] p = res.Split('|');
                if (p[1] == "NOTFOUND") return "Windows Update no longer offers this driver.";
                if (p[1] == "ERR") return p.Length > 2 ? p[2] : "Failed.";
                reboot = p.Length > 2 && p[2] == "True";
                if (p[1] == "2" || p[1] == "3") { Log.Info("Installed driver via Windows Update: " + d.Title); TuneKit.Record("Installed driver " + d.Title); return null; }
                return "Windows Update result code " + p[1] + ".";
            }
            catch (Exception ex) { Log.Err("wu install", ex); return ex.Message; }
            finally { try { if (f != null) File.Delete(f); } catch { } }
        }
    }

    // ------------------------------------------------------------------ the page

    public class UpdatesPage : PageBase
    {
        RadioButton _rApps, _rDrv;
        StackPanel _appsHost, _drvHost;
        TextBlock _head, _msg;
        List<AppUpdate> _apps = new List<AppUpdate>();
        bool _appsLoaded, _appsBusy, _drvLoaded, _drvBusy, _wuBusy;
        StackPanel _wuList; TextBlock _wuMsg;
        List<WuDriver> _wu = new List<WuDriver>();

        public UpdatesPage() : base("Updates", "See which apps and drivers have a newer version, then update them from here. Nothing is installed until you press a button.")
        {
            StackPanel top = new StackPanel();
            _head = UK.T("", "Big");
            _msg = UK.T("", "Muted"); _msg.Margin = new Thickness(0, 4, 0, 10); _msg.TextWrapping = TextWrapping.Wrap;
            top.Children.Add(_head); top.Children.Add(_msg);
            StackPanel seg = new StackPanel { Orientation = Orientation.Horizontal };
            _rApps = new RadioButton { Content = "App updates", Style = UK.S("Seg"), IsChecked = true, GroupName = "updmode" };
            _rDrv = new RadioButton { Content = "Driver updates", Style = UK.S("Seg"), GroupName = "updmode" };
            seg.Children.Add(_rApps); seg.Children.Add(_rDrv);
            top.Children.Add(seg);
            Body.Children.Add(UK.Card(top));
            _appsHost = new StackPanel(); _drvHost = new StackPanel();
            Body.Children.Add(_appsHost); Body.Children.Add(_drvHost);
            RoutedEventHandler re = delegate { SwitchMode(); };
            _rApps.Checked += re; _rDrv.Checked += re;
            SwitchMode();
        }

        public override void Shown() { SwitchMode(); }
        public void ShowAlt() { _rDrv.IsChecked = true; }

        void SwitchMode()
        {
            if (_appsHost == null) return;
            bool apps = _rApps.IsChecked == true;
            _appsHost.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
            _drvHost.Visibility = apps ? Visibility.Collapsed : Visibility.Visible;
            if (apps) { if (!_appsLoaded) LoadApps(); else HeadApps(); }
            else { if (!_drvLoaded) LoadDrivers(); }
        }

        // ---------------- apps
        string _appFilter = "Updates";
        TextBox _appSearch, _liveBox;
        StackPanel _appRows;
        TextBlock _appCount;
        Expander _liveSec;
        List<RadioButton> _appFilterBtns = new List<RadioButton>();

        void LoadApps()
        {
            if (_appsBusy) return;
            _appsBusy = true;
            _head.Text = "Checking every installed app...";
            _msg.Text = "Asking winget (the Windows package manager) about every installed program. This takes up to a minute.";
            UK.Bg(delegate
            {
                string v; bool has = UpdateKit.HasWinget(out v);
                if (!has) return new object[] { null, "winget was not found on this PC. Install or update \"App Installer\" from the Microsoft Store, then press Check again.", v };
                string err1, err2;
                List<AppUpdate> all = UpdateKit.ListAll(out err1);
                List<AppUpdate> ups = UpdateKit.Scan(out err2);
                // the update scan is the authority on what can be updated; merge it into the full list
                foreach (AppUpdate u in ups)
                {
                    AppUpdate hit = all.FirstOrDefault(x => string.Equals(x.Id, u.Id, StringComparison.OrdinalIgnoreCase));
                    if (hit != null) { hit.Available = u.Available; hit.Version = u.Version; hit.Status = "update"; hit.Tracked = true; hit.Selected = true; if (hit.Source.Length == 0) hit.Source = u.Source; }
                    else { u.Status = "update"; u.Tracked = true; all.Add(u); }
                }
                string err = all.Count == 0 ? (err1 ?? err2) : null;
                return new object[] { all, err, v };
            }, delegate(object o)
            {
                _appsBusy = false; _appsLoaded = true;
                object[] r = (object[])o;
                if (r == null) { _head.Text = "Could not check"; BuildApps(null, "Something went wrong while checking."); return; }
                _apps = r[0] == null ? new List<AppUpdate>() : ((List<AppUpdate>)r[0]).OrderBy(x => x.Status == "update" ? 0 : x.Status == "ok" ? 1 : 2).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
                BuildApps(r[0] == null ? "winget missing" : null, (string)r[1]);
            });
        }

        void HeadApps()
        {
            int upd = _apps.Count(a => a.Status == "update"), ok = _apps.Count(a => a.Status == "ok"), un = _apps.Count(a => a.Status == "untracked");
            int sel = _apps.Count(a => a.Status == "update" && a.Selected);
            _head.Text = upd == 0 ? "No app updates found" : upd + " app update" + (upd == 1 ? "" : "s") + " available, " + sel + " selected";
            _msg.Text = _apps.Count + " installed apps checked:  " + upd + " can be updated,  " + ok + " are up to date,  " + un + " cannot be checked by winget (they update themselves, or need the maker's own updater).";
        }

        void BuildApps(string flag, string err)
        {
            _appsHost.Children.Clear();
            StackPanel bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            bar.Children.Add(UK.Btn("Update selected", true, delegate { UpdateSelected(); }));
            bar.Children.Add(UK.Btn("Select all updates", false, delegate { PickApps(true); }));
            bar.Children.Add(UK.Btn("Clear", false, delegate { PickApps(false); }));
            bar.Children.Add(UK.Btn("Check again", false, delegate { _appsLoaded = false; LoadApps(); }));
            _appsHost.Children.Add(bar);

            if (flag != null || (err != null && _apps.Count == 0)) { _msg.Text = err ?? ""; _head.Text = flag != null ? "winget is not available" : "Could not read the app list"; }
            else HeadApps();

            if (_apps.Count > 0)
            {
                StackPanel left = new StackPanel();
                _appSearch = new TextBox { Style = UK.S("Box"), Height = 34, Margin = new Thickness(0, 0, 0, 8), Tag = "Search installed apps...", ToolTip = "Search installed apps" };
                _appSearch.TextChanged += delegate { FillAppRows(); };
                left.Children.Add(_appSearch);
                WrapPanel fl = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
                _appFilterBtns.Clear();
                foreach (string f in new string[] { "Updates", "All apps", "Up to date", "Not tracked" })
                {
                    string name = f;
                    RadioButton rb = new RadioButton { Style = UK.S("Seg"), GroupName = "appfilter", Content = f, IsChecked = f == _appFilter, Margin = new Thickness(0, 0, 0, 4) };
                    rb.Click += delegate { _appFilter = name; FillAppRows(); };
                    _appFilterBtns.Add(rb); fl.Children.Add(rb);
                }
                left.Children.Add(fl);
                _appCount = UK.T("", "Muted"); _appCount.Margin = new Thickness(0, 0, 0, 4);
                left.Children.Add(_appCount);
                _appRows = new StackPanel();
                left.Children.Add(_appRows);
                _appsHost.Children.Add(UK.Card(left));
                FillAppRows();
            }

            // live output
            _liveBox = new TextBox(); _liveBox.Style = UK.S("Box"); _liveBox.IsReadOnly = true; _liveBox.MinHeight = 160; _liveBox.MaxHeight = 320;
            _liveBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _liveBox.TextWrapping = TextWrapping.Wrap; _liveBox.FontFamily = new System.Windows.Media.FontFamily("Consolas");
            _liveBox.Text = File.Exists(UpdateKit.LogFile) ? "Last update run:\r\n" + File.ReadAllText(UpdateKit.LogFile) : "Nothing has been updated from here yet. When you press Update, everything winget says appears here live, with the exact reason if it fails.";
            _liveSec = UK.Section("Update output", "Live, exactly what winget says, plus why it failed when it does", _liveBox, false, null);
            _appsHost.Children.Add(_liveSec);

            // automatic updates
            StackPanel auto = new StackPanel();
            auto.Children.Add(UK.T("Let this app update the winget-tracked apps by itself on a schedule. It runs in the background with no window and writes what it did to app-update-log.txt in the data folder. Manual checking stays the default.", "Muted"));
            StackPanel sb = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            TextBlock am = UK.T("Checking the current schedule...", "Muted"); am.Margin = new Thickness(0, 6, 0, 0);
            Dictionary<string, Button> btns = new Dictionary<string, Button>();
            foreach (string m in new string[] { "Off", "Weekly", "Monthly" })
            {
                string mode = m;
                Button b = UK.Btn(m, false, delegate
                {
                    am.Text = "Saving...";
                    UK.Bg(delegate { string e = UpdateKit.SetSchedule(mode); return new object[] { e, UpdateKit.ScheduleState() }; }, delegate(object o)
                    {
                        object[] r = (object[])o;
                        am.Text = r[0] == null ? (mode == "Off" ? "Automatic app updates are off." : "Apps will be updated " + mode.ToLowerInvariant() + " (Sundays or the 1st, 1 PM, when the PC is on).") : "Could not save: " + r[0];
                        MarkAuto(btns, (string)r[1]);
                    });
                });
                btns[m] = b; sb.Children.Add(b);
            }
            auto.Children.Add(sb); auto.Children.Add(am);
            _appsHost.Children.Add(UK.Section("Automatic app updates", "Optional", auto, false, null));
            UK.Bg(delegate { return UpdateKit.ScheduleState(); }, delegate(object o) { string s = (string)o ?? "Off"; MarkAuto(btns, s); am.Text = s == "Off" ? "Automatic app updates are off." : "Automatic app updates run " + s.ToLowerInvariant() + "."; });
        }

        void FillAppRows()
        {
            if (_appRows == null) return;
            _appRows.Children.Clear();
            string q = (_appSearch == null ? "" : _appSearch.Text ?? "").Trim().ToLowerInvariant();
            IEnumerable<AppUpdate> e = _apps;
            if (_appFilter == "Updates") e = e.Where(a => a.Status == "update");
            else if (_appFilter == "Up to date") e = e.Where(a => a.Status == "ok");
            else if (_appFilter == "Not tracked") e = e.Where(a => a.Status == "untracked");
            if (q.Length > 0) e = e.Where(a => a.Name.ToLowerInvariant().Contains(q) || a.Id.ToLowerInvariant().Contains(q));
            List<AppUpdate> l = e.ToList();
            int shown = 0;
            for (int i = 0; i < l.Count && shown < 400; i++, shown++) _appRows.Children.Add(BuildAppRow(l[i], i == l.Count - 1));
            _appCount.Text = l.Count == 0 ? (_appFilter == "Updates" ? "Everything winget tracks is up to date." : "Nothing matches.") : l.Count + " shown" + (l.Count > 400 ? " (first 400, use search)" : "");
        }

        UIElement BuildAppRow(AppUpdate a, bool last)
        {
            UIElement ctl = null;
            TextBlock right = null;
            if (a.Status == "update")
            {
                ToggleButton sw = UK.Sw(a.Selected); a.Switch = sw;
                sw.Click += delegate { a.Selected = sw.IsChecked == true; if (a.RowBox != null) a.RowBox.Background = a.Selected ? UK.Br("SelBrush") : System.Windows.Media.Brushes.Transparent; HeadApps(); };
                a.State = UK.Right(a.Version + "  >  " + a.Available);
                right = a.State;
                Button one = UK.Btn("Update", false, delegate { UpdateOne(a); });
                StackPanel c = new StackPanel { Orientation = Orientation.Horizontal };
                c.Children.Add(one); c.Children.Add(sw);
                ctl = c;
            }
            else { a.State = UK.Right(a.Version); right = a.State; }
            string detail = a.Status == "untracked" ? "Not known to winget. Use the app's own Check for updates, or the maker's website." : a.Id + (string.IsNullOrEmpty(a.Source) ? "" : "  -  " + a.Source);
            Border row = UK.Row(a.Name, detail, right, ctl, last);
            StackPanel chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            if (a.Status == "update") chips.Children.Add(UK.Chip("UPDATE AVAILABLE", "Good"));
            else if (a.Status == "ok") chips.Children.Add(UK.Chip("UP TO DATE", "Info"));
            else chips.Children.Add(UK.Chip("NOT TRACKED BY WINGET", "Warn"));
            ((StackPanel)((Grid)row.Child).Children[0]).Children.Add(chips);
            a.RowBox = row;
            if (a.Status == "update" && a.Selected) row.Background = UK.Br("SelBrush");
            return row;
        }

        static void MarkAuto(Dictionary<string, Button> b, string cur)
        {
            foreach (KeyValuePair<string, Button> kv in b) kv.Value.Style = UK.S(kv.Key == cur ? "PrimaryBtn" : "GhostBtn");
        }

        void PickApps(bool on)
        {
            foreach (AppUpdate a in _apps.Where(x => x.Status == "update")) { a.Selected = on; if (a.Switch != null) a.Switch.IsChecked = on; if (a.RowBox != null) a.RowBox.Background = on ? UK.Br("SelBrush") : System.Windows.Media.Brushes.Transparent; }
            HeadApps();
        }

        void UpdateOne(AppUpdate a) { RunUpdates(new List<AppUpdate> { a }); }
        void UpdateSelected()
        {
            List<AppUpdate> l = _apps.Where(a => a.Status == "update" && a.Selected).ToList();
            if (l.Count == 0) { _msg.Text = "Nothing is selected. Switch on the apps you want in the list."; return; }
            if (l.Count > 1 && Dlg.Show("Update " + l.Count + " apps now?\n\n" + string.Join("\n", l.Select(x => "  " + x.Name + "  " + x.Version + " > " + x.Available).ToArray()) + "\n\nThey update one after another, silently.", Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            RunUpdates(l);
        }

        void Say(string t)
        {
            UK.UI(delegate { if (_liveBox != null) { _liveBox.AppendText(t + "\r\n"); _liveBox.ScrollToEnd(); } });
        }

        void RunUpdates(List<AppUpdate> todo)
        {
            if (_appsBusy) { _msg.Text = "Another update is still running."; return; }
            _appsBusy = true;
            int total = todo.Count;
            if (_liveSec != null) _liveSec.IsExpanded = true;
            if (_liveBox != null) _liveBox.Clear();
            Dispatcher disp = Application.Current.Dispatcher;
            UK.Bg(delegate
            {
                int ok = 0; List<string> fails = new List<string>();
                for (int i = 0; i < todo.Count; i++)
                {
                    AppUpdate a = todo[i]; int n = i + 1;
                    UK.UI(delegate { _head.Text = "Updating " + n + " of " + total + ": " + a.Name; _msg.Text = a.Version + " to " + a.Available; if (a.State != null) a.State.Text = "updating..."; });
                    Say("=== " + a.Name + "   " + a.Version + " > " + a.Available + "   (" + a.Id + ")");
                    // an open app blocks its own installer: ask, then close it
                    List<Process> running = UpdateKit.RunningFor(a);
                    if (running.Count > 0)
                    {
                        string names = string.Join(", ", running.Select(p => { try { return p.ProcessName; } catch { return "?"; } }).Distinct().ToArray());
                        Say("It is open right now (" + names + "). The installer cannot replace files that are in use.");
                        bool close = (bool)disp.Invoke(new Func<bool>(delegate { return Dlg.Show(a.Name + " is open (" + names + ").\n\nIt has to be closed so the update can replace its files. Unsaved work in it is lost.\n\nClose it and update now?", Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes; }));
                        if (!close) { Say("Skipped, you chose not to close it."); fails.Add(a.Name + ": skipped, the app was open"); UK.UI(delegate { if (a.State != null) a.State.Text = "skipped"; }); foreach (Process p in running) p.Dispose(); continue; }
                        Say("Closing " + names + "...");
                        Say("   " + UpdateKit.CloseProcesses(running));
                        foreach (Process p in running) p.Dispose();
                    }
                    Say("Starting winget...");
                    string e = UpdateKit.UpdateLive(a, delegate(string line) { Say("   " + line); });
                    if (e == null) { ok++; Say("DONE: " + a.Name + " updated."); a.Status = "ok"; a.Version = a.Available; a.Available = ""; a.Selected = false; UK.UI(delegate { if (a.State != null) a.State.Text = "updated"; }); }
                    else { fails.Add(a.Name + ": " + e); Say("FAILED: " + e); UK.UI(delegate { if (a.State != null) a.State.Text = "failed"; }); }
                }
                return new object[] { ok, fails };
            }, delegate(object o)
            {
                _appsBusy = false;
                object[] r = (object[])o; List<string> fails = r == null ? new List<string>() : (List<string>)r[1];
                string summary = "Updated " + (r == null ? 0 : (int)r[0]) + " of " + total + "." + (fails.Count > 0 ? "  Not updated: " + string.Join("; ", fails.Select(f => f.Split('\n')[0]).Take(3).ToArray()) : "");
                FillAppRows(); HeadApps();
                _msg.Text = summary + "   " + _msg.Text;
            });
        }

        // ---------------- drivers
        void LoadDrivers()
        {
            if (_drvBusy) return;
            _drvBusy = true;
            _head.Text = "Reading your drivers...";
            _msg.Text = DriverKit.AutoDriverState();
            UK.Bg(delegate { List<string> pr; List<DriverRow> l = DriverKit.Installed(out pr); return new object[] { l, pr }; }, delegate(object o)
            {
                _drvBusy = false; _drvLoaded = true;
                object[] r = (object[])o;
                BuildDrivers(r == null ? new List<DriverRow>() : (List<DriverRow>)r[0], r == null ? new List<string>() : (List<string>)r[1]);
            });
        }

        void BuildDrivers(List<DriverRow> rows, List<string> problems)
        {
            _drvHost.Children.Clear();
            int old = rows.Count(d => d.Old);
            _head.Text = rows.Count + " key drivers checked, " + old + " look old" + (problems.Count > 0 ? ", " + problems.Count + " device(s) need attention" : "");
            _msg.Text = DriverKit.AutoDriverState();

            StackPanel bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            bar.Children.Add(UK.Btn("Search Windows Update for drivers", true, delegate { SearchWu(); }));
            bar.Children.Add(UK.Btn("Open Windows optional updates", false, delegate { OpenLink("ms-settings:windowsupdate-optionalupdates"); }));
            bar.Children.Add(UK.Btn("Re-read drivers", false, delegate { _drvLoaded = false; LoadDrivers(); }));
            _drvHost.Children.Add(bar);

            // Windows Update results
            _wuList = new StackPanel(); _wuMsg = UK.T("Press the blue button to ask Windows Update which drivers it has for this PC. It can take up to a minute.", "Muted");
            _wuMsg.TextWrapping = TextWrapping.Wrap; _wuMsg.Margin = new Thickness(0, 0, 0, 8);
            StackPanel wuBox = new StackPanel(); wuBox.Children.Add(_wuMsg); wuBox.Children.Add(_wuList);
            _drvHost.Children.Add(UK.Section("Driver updates from Windows Update", "Install them one by one or all at once", wuBox, true, null));
            if (_wu.Count > 0) ShowWu();

            if (problems.Count > 0)
            {
                StackPanel pp = new StackPanel();
                for (int i = 0; i < problems.Count; i++) pp.Children.Add(UK.Row(problems[i], "Open Device Manager to see it, or search Windows Update above.", null, null, i == problems.Count - 1));
                _drvHost.Children.Add(UK.Section("Needs attention", problems.Count + " device(s) report a problem", pp, true, null));
            }

            foreach (string cat in rows.Select(d => d.Category).Distinct())
            {
                List<DriverRow> g = rows.Where(d => d.Category == cat).ToList();
                StackPanel sp = new StackPanel();
                for (int i = 0; i < g.Count; i++)
                {
                    DriverRow d = g[i];
                    string det = (string.IsNullOrEmpty(d.Maker) ? "" : d.Maker + "  -  ") + "version " + d.Version + (d.Date.HasValue ? "  -  " + d.Date.Value.ToString("yyyy-MM-dd") : "");
                    TextBlock right = UK.Right(DriverKit.AgeText(d));
                    if (d.Old) right.Foreground = UK.Br("WarnBrush");
                    UIElement ctl = null;
                    if (d.Link != null) { string url = d.Link; Button b = UK.Btn(d.LinkText, false, delegate { OpenLink(url); }); b.FontSize = 12; ctl = b; }
                    sp.Children.Add(UK.Row(d.Device, det, right, ctl, i == g.Count - 1));
                }
                _drvHost.Children.Add(UK.Section(cat, g.Count + " driver" + (g.Count == 1 ? "" : "s") + ", " + g.Count(x => x.Old) + " look old", sp, cat == "Graphics" || g.Any(x => x.Old), null));
            }
            TextBlock note = UK.T("How to read this: the age is the date stored inside the installed driver, not proof that a newer one exists. Windows built-in drivers are normally fine. For the graphics card, the chipset and the network card the maker's own site usually has the newest version, so use the buttons. Windows Update drivers are signed by Microsoft, but its graphics drivers are often older than the maker's. Some drivers need a restart.", "Muted");
            note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 4, 0, 0);
            _drvHost.Children.Add(note);
        }

        static void OpenLink(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { Log.Err(ex); }
        }

        void SearchWu()
        {
            if (_wuBusy) return;
            _wuBusy = true; _wuMsg.Text = "Asking Windows Update... please wait.";
            UK.Bg(delegate { string e; List<WuDriver> l = DriverKit.SearchWindowsUpdate(out e); return new object[] { l, e }; }, delegate(object o)
            {
                _wuBusy = false;
                object[] r = (object[])o;
                if (r == null) { _wuMsg.Text = "The search failed."; return; }
                _wu = (List<WuDriver>)r[0];
                if (r[1] != null) { _wuMsg.Text = (string)r[1]; return; }
                _wuMsg.Text = _wu.Count == 0 ? "Windows Update has no driver updates for this PC right now." : _wu.Count + " driver update" + (_wu.Count == 1 ? "" : "s") + " offered.";
                ShowWu();
            });
        }

        void ShowWu()
        {
            _wuList.Children.Clear();
            if (_wu.Count == 0) return;
            for (int i = 0; i < _wu.Count; i++)
            {
                WuDriver d = _wu[i];
                d.State = UK.Right(d.Date + (d.Size > 0 ? "  -  " + Util.Bytes(d.Size) : ""));
                d.Btn = UK.Btn("Install", false, delegate { InstallWu(new List<WuDriver> { d }); });
                _wuList.Children.Add(UK.Row(d.Title, (string.IsNullOrEmpty(d.Maker) ? "" : d.Maker + "  -  ") + (string.IsNullOrEmpty(d.Cls) ? "" : d.Cls), d.State, d.Btn, i == _wu.Count - 1));
            }
            StackPanel b = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            b.Children.Add(UK.Btn("Install all listed", true, delegate
            {
                if (Dlg.Show("Install " + _wu.Count + " driver(s) from Windows Update?\n\nThe screen may flicker and some drivers need a restart. Save your work first.", Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) InstallWu(_wu.ToList());
            }));
            _wuList.Children.Add(b);
        }

        void InstallWu(List<WuDriver> todo)
        {
            if (_wuBusy) { _wuMsg.Text = "Windows Update is busy."; return; }
            _wuBusy = true;
            UK.Bg(delegate
            {
                int ok = 0; bool reboot = false; List<string> fails = new List<string>();
                for (int i = 0; i < todo.Count; i++)
                {
                    WuDriver d = todo[i]; int n = i + 1;
                    UK.UI(delegate { _wuMsg.Text = "Installing " + n + " of " + todo.Count + ": " + d.Title; if (d.State != null) d.State.Text = "installing..."; });
                    bool rb; string e = DriverKit.InstallWindowsUpdate(d, out rb);
                    if (e == null) { ok++; if (rb) reboot = true; UK.UI(delegate { if (d.State != null) d.State.Text = "installed"; if (d.Btn != null) d.Btn.IsEnabled = false; }); }
                    else { fails.Add(d.Title + ": " + e); UK.UI(delegate { if (d.State != null) d.State.Text = "failed"; }); }
                }
                return new object[] { ok, reboot, fails };
            }, delegate(object o)
            {
                _wuBusy = false;
                object[] r = (object[])o; if (r == null) return;
                List<string> fails = (List<string>)r[2];
                _wuMsg.Text = "Installed " + r[0] + " of " + todo.Count + "." + ((bool)r[1] ? " Restart the PC to finish." : "") + (fails.Count > 0 ? " Could not install: " + string.Join("; ", fails.Take(2).ToArray()) : "");
                _drvLoaded = false;
            });
        }
    }
}
