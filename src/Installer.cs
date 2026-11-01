using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace InfernoXC
{
    /// <summary>
    /// One-file install: the downloaded exe installs itself the first time it runs.
    /// Copies itself to Program Files, adds Desktop and Start Menu shortcuts and an
    /// Apps and features entry, then starts the installed copy.
    /// </summary>
    public static class Installer
    {
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\IXC-PC-Ready";
        const string ExeName = "IXC-PC-Ready.exe";
        const string LinkName = "IXC PC Ready.lnk";

        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "InFerNoxC", "IXC PC Ready"); }
        }
        static string InstalledExe { get { return Path.Combine(InstallDir, ExeName); } }
        static string Me { get { return Assembly.GetExecutingAssembly().Location; } }

        static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static bool IsInstalledCopy() { return SamePath(Me, InstalledExe); }

        public static bool ShouldOffer(string[] args)
        {
            if (IsInstalledCopy()) return false;
            if (args.Any(a => a.Equals("--portable", StringComparison.OrdinalIgnoreCase) || a.Equals("--tray", StringComparison.OrdinalIgnoreCase))) return false;
            if (Settings.GetBool("install_declined", false)) return false;
            return true;
        }

        /// <summary>Returns true when this process should exit (installed copy started, or user cancelled).</summary>
        public static bool Offer()
        {
            bool already = File.Exists(InstalledExe);
            string text = already
                ? "IXC PC Ready is already installed.\n\nYes: update it and open it\nNo: just run this copy\nCancel: close"
                : "Install IXC PC Ready on this PC?\n\nIt adds an icon to your Desktop and Start Menu. You can remove it any time from Settings > Apps.\n\nYes: install and open it\nNo: run once without installing\nCancel: close";
            MessageBoxResult r = MessageBox.Show(text, "IXC PC Ready - made by InFerNoxC", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return true;
            if (r == MessageBoxResult.No) { Settings.SetBool("install_declined", true); return false; }
            string err = Install();
            if (err != null)
            {
                MessageBox.Show("Could not install:\n\n" + err + "\n\nIXC PC Ready will run without installing.", "IXC PC Ready", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            try { Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDir }); }
            catch (Exception ex) { Log.Err(ex); return false; }
            return true;
        }

        public static string Install()
        {
            try
            {
                Directory.CreateDirectory(InstallDir);
                foreach (Process p in Process.GetProcessesByName("IXC-PC-Ready"))
                {
                    try
                    {
                        if (p.Id != Process.GetCurrentProcess().Id && SamePath(p.MainModule.FileName, InstalledExe)) { p.Kill(); p.WaitForExit(4000); }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
                File.Copy(Me, InstalledExe, true);

                MakeLink(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), LinkName));
                MakeLink(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), LinkName));

                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", "IXC PC Ready");
                    k.SetValue("Publisher", "InFerNoxC");
                    k.SetValue("DisplayVersion", Brand.Version);
                    k.SetValue("DisplayIcon", InstalledExe);
                    k.SetValue("InstallLocation", InstallDir);
                    k.SetValue("URLInfoAbout", Brand.RepoUrl);
                    k.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
                }
                Log.Info("Installed to " + InstallDir);
                return null;
            }
            catch (Exception ex) { Log.Err(ex); return ex.Message; }
        }

        static void MakeLink(string path)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic sh = Activator.CreateInstance(t);
            dynamic lnk = sh.CreateShortcut(path);
            lnk.TargetPath = InstalledExe;
            lnk.WorkingDirectory = InstallDir;
            lnk.Description = "IXC PC Ready - made by InFerNoxC";
            lnk.IconLocation = InstalledExe + ",0";
            lnk.Save();
        }

        public static void Uninstall()
        {
            MessageBoxResult r = MessageBox.Show("Remove IXC PC Ready from this PC?\n\nYour settings and backups are kept so you can revert changes later.", "IXC PC Ready", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            try
            {
                foreach (Process p in Process.GetProcessesByName("IXC-PC-Ready"))
                {
                    try { if (p.Id != Process.GetCurrentProcess().Id) { p.Kill(); p.WaitForExit(4000); } }
                    catch { }
                    finally { p.Dispose(); }
                }
                try { AutoStart.Disable(); } catch { }
                try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), LinkName)); } catch { }
                try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), LinkName)); } catch { }
                try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch { }
                string dir = InstallDir;
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"" + dir + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
                MessageBox.Show("IXC PC Ready was removed.", "IXC PC Ready", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { Log.Err(ex); MessageBox.Show("Could not finish removing:\n\n" + ex.Message, "IXC PC Ready"); }
        }
    }
}
