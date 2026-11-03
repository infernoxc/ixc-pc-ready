using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace InfernoXC
{
    public class LeftoverItem
    {
        public string Kind;      // Folder | Registry | Shortcut
        public string Path;      // for Registry: HIVE\subkey
        public string Why;
        public long Size;
        public bool Selected = true;
    }

    /// <summary>
    /// Finds what an uninstaller usually leaves behind. Matching is deliberately strict (exact names only,
    /// never a publisher folder) because deleting too much is worse than missing a leftover.
    /// </summary>
    public static class Leftovers
    {
        static readonly string PF = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
        static readonly string PF86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
        static readonly string PD = Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData";
        static readonly string Local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        static readonly string Roam = Environment.GetEnvironmentVariable("APPDATA") ?? "";
        static readonly string Win = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

        static readonly string[] BadNames = new string[] { "microsoft", "windows", "google", "intel", "amd", "nvidia", "common files", "system32", "temp", "packages", "programs", "apps" };

        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
            return s.Trim().TrimEnd('.');
        }

        static bool Safe(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            string f = folder.TrimEnd('\\');
            string[] roots = new string[] { PF, PF86, PD, Local, Roam, Win, Environment.GetEnvironmentVariable("USERPROFILE") ?? "", Path.GetPathRoot(Win) };
            foreach (string r in roots) if (string.Equals(f, (r ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
            string name = Path.GetFileName(f).ToLowerInvariant();
            if (name.Length < 4 || BadNames.Contains(name)) return false;
            return true;
        }

        public static bool StillInstalled(AppRow a)
        {
            try { return AppReader.ReadInstalled().Any(x => string.Equals(x.Name, a.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Version ?? "", a.Version ?? "", StringComparison.OrdinalIgnoreCase)); }
            catch { return false; }
        }

        public static List<LeftoverItem> Scan(AppRow a)
        {
            List<LeftoverItem> res = new List<LeftoverItem>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string name = Clean(a.Name), pub = Clean(a.Publisher);
            if (name.Length < 4) return res;
            bool genericPub = pub.Length == 0 || BadNames.Any(b => pub.ToLowerInvariant().StartsWith(b));

            List<string> bases = new List<string> { PF, PF86, PD, Local, Roam, Path.Combine(Local, "Programs") };
            foreach (string b in bases)
            {
                if (string.IsNullOrEmpty(b)) continue;
                List<string> cands = new List<string> { Path.Combine(b, name) };
                if (!genericPub) cands.Add(Path.Combine(b, pub, name));
                foreach (string c in cands)
                {
                    try
                    {
                        if (Directory.Exists(c) && Safe(c) && seen.Add(c.ToLowerInvariant()))
                            res.Add(new LeftoverItem { Kind = "Folder", Path = c, Why = "Folder named after the app", Size = CleanKit.DirSize(c) });
                    }
                    catch { }
                }
            }
            if (!string.IsNullOrEmpty(a.Location))
            {
                try
                {
                    string loc = a.Location.Trim('"').TrimEnd('\\');
                    if (Directory.Exists(loc) && Safe(loc) && seen.Add(loc.ToLowerInvariant()))
                        res.Add(new LeftoverItem { Kind = "Folder", Path = loc, Why = "The app's recorded install folder is still there", Size = CleanKit.DirSize(loc) });
                }
                catch { }
            }

            // Start Menu entries
            foreach (string sm in new string[] { Path.Combine(Roam, @"Microsoft\Windows\Start Menu\Programs"), Path.Combine(PD, @"Microsoft\Windows\Start Menu\Programs") })
            {
                try
                {
                    string d = Path.Combine(sm, name);
                    if (Directory.Exists(d) && seen.Add(d.ToLowerInvariant())) res.Add(new LeftoverItem { Kind = "Shortcut", Path = d, Why = "Start Menu folder", Size = CleanKit.DirSize(d) });
                    string l = Path.Combine(sm, name + ".lnk");
                    if (File.Exists(l) && seen.Add(l.ToLowerInvariant())) res.Add(new LeftoverItem { Kind = "Shortcut", Path = l, Why = "Start Menu shortcut", Size = new FileInfo(l).Length });
                }
                catch { }
            }

            // registry keys named after the app
            List<string> keys = new List<string>();
            foreach (string root in new string[] { @"HKCU\Software", @"HKLM\SOFTWARE", @"HKLM\SOFTWARE\WOW6432Node" })
            {
                keys.Add(root + "\\" + name);
                if (!genericPub) keys.Add(root + "\\" + pub + "\\" + name);
            }
            foreach (string k in keys)
            {
                try { if (RegExists(k) && seen.Add(k.ToLowerInvariant())) res.Add(new LeftoverItem { Kind = "Registry", Path = k, Why = "Settings key named after the app" }); }
                catch { }
            }
            // orphan uninstall entries: listed in Apps & features but the program is gone
            foreach (string up in new string[] { @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using (RegistryKey rk = Open(up))
                    {
                        if (rk == null) continue;
                        foreach (string sub in rk.GetSubKeyNames())
                        {
                            using (RegistryKey sk = rk.OpenSubKey(sub))
                            {
                                if (sk == null) continue;
                                string dn = Convert.ToString(sk.GetValue("DisplayName") ?? "");
                                if (!string.Equals(dn, a.Name, StringComparison.OrdinalIgnoreCase)) continue;
                                string il = Convert.ToString(sk.GetValue("InstallLocation") ?? "").Trim('"');
                                string us = Convert.ToString(sk.GetValue("UninstallString") ?? "");
                                bool gone = (il.Length > 0 && !Directory.Exists(il)) || (il.Length == 0 && us.Length > 0 && !UninstallerExists(us));
                                string full = up + "\\" + sub;
                                if (gone && seen.Add(full.ToLowerInvariant())) res.Add(new LeftoverItem { Kind = "Registry", Path = full, Why = "Apps & features still lists it, but its files are gone" });
                            }
                        }
                    }
                }
                catch { }
            }
            return res;
        }

        static bool UninstallerExists(string cmd)
        {
            try
            {
                string c = cmd.Trim();
                if (c.StartsWith("\"")) { int e = c.IndexOf('"', 1); if (e > 1) return File.Exists(c.Substring(1, e - 1)); }
                if (c.StartsWith("msiexec", StringComparison.OrdinalIgnoreCase)) return true;   // MSI entries are checked by Windows itself
                int sp = c.IndexOf(' ');
                return File.Exists(sp > 0 ? c.Substring(0, sp) : c);
            }
            catch { return true; }
        }

        static RegistryKey Open(string path)
        {
            int i = path.IndexOf('\\');
            string hive = path.Substring(0, i), sub = path.Substring(i + 1);
            RegistryKey root = hive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
            return root.OpenSubKey(sub);
        }
        static bool RegExists(string path) { using (RegistryKey k = Open(path)) return k != null; }

        // ----------------------------------------------------------------- removal, always with a way back

        public static string BackupDir(string appName)
        {
            string d = Path.Combine(Paths.Sub("uninstall-backups"), DateTime.Now.ToString("yyyyMMdd-HHmm") + "-" + Clean(appName));
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>Removes the selected items. Registry keys are exported to .reg files first; folders and shortcuts go to the Recycle Bin.</summary>
        public static string Remove(AppRow a, List<LeftoverItem> items, out int ok, out int failed)
        {
            ok = 0; failed = 0;
            string dir = BackupDir(a.Name);
            StringBuilder man = new StringBuilder();
            man.AppendLine("Removed by " + Brand.Name + " " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " after uninstalling " + a.Name);
            int n = 0;
            foreach (LeftoverItem it in items.Where(x => x.Selected))
            {
                try
                {
                    if (it.Kind == "Registry")
                    {
                        string file = Path.Combine(dir, "registry-" + (++n) + ".reg");
                        Util.RunResult r = Util.Run("reg.exe", "export \"" + it.Path + "\" \"" + file + "\" /y", 20000);
                        if (r.Code != 0 || !File.Exists(file)) throw new InvalidOperationException("Could not back up the key first, so it was left alone.");
                        int i = it.Path.IndexOf('\\');
                        RegistryKey root = it.Path.Substring(0, i) == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
                        root.DeleteSubKeyTree(it.Path.Substring(i + 1), false);
                        man.AppendLine("registry  " + it.Path + "   (backup: " + Path.GetFileName(file) + ", double-click it to restore)");
                    }
                    else if (it.Kind == "Folder" || (it.Kind == "Shortcut" && Directory.Exists(it.Path)))
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(it.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        man.AppendLine("folder    " + it.Path + "   (in the Recycle Bin)");
                    }
                    else
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(it.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        man.AppendLine("shortcut  " + it.Path + "   (in the Recycle Bin)");
                    }
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    man.AppendLine("FAILED    " + it.Path + ": " + ex.Message);
                    Log.Err("leftover " + it.Path, ex);
                }
            }
            try { File.WriteAllText(Path.Combine(dir, "removed.txt"), man.ToString(), Encoding.UTF8); } catch { }
            return dir;
        }
    }
}
