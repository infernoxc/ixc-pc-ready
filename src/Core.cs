using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace InfernoXC
{
    public static class Brand
    {
        public const string Name = "IXC PC Ready";
        public const string Maker = "InFerNoxC";
        public const string Version = "1.0.0";
        public const string Tagline = "Performance toolkit for Windows gamers";
        public const string RepoUrl = "https://github.com/infernoxc/ixc-pc-ready";
    }

    public static class Paths
    {
        static string _data;
        public static string DataDir
        {
            get
            {
                if (_data == null)
                {
                    string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "InFerNoxC", "PC-Ready");
                    try { Directory.CreateDirectory(d); } catch { }
                    _data = d;
                }
                return _data;
            }
        }
        public static string Sub(string name)
        {
            string d = Path.Combine(DataDir, name);
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
        public static string AppDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }
    }

    public static class Log
    {
        static readonly object _lk = new object();
        public static void Info(string msg) { Write("INFO", msg); }
        public static void Err(Exception ex) { Write("ERROR", ex == null ? "null" : ex.ToString()); }
        public static void Err(string where, Exception ex) { Write("ERROR", where + ": " + (ex == null ? "null" : ex.Message)); }
        static void Write(string lvl, string msg)
        {
            try
            {
                lock (_lk)
                {
                    string f = Path.Combine(Paths.DataDir, "pc-ready.log");
                    try { FileInfo fi = new FileInfo(f); if (fi.Exists && fi.Length > 1500000) File.Delete(f); } catch { }
                    File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + lvl + "] " + msg + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    public static class Util
    {
        public static string Bytes(double b)
        {
            string[] u = new string[] { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return (i <= 1 ? b.ToString("0") : b.ToString("0.0")) + " " + u[i];
        }
        public static string MB(double mb)
        {
            if (mb >= 1024) return (mb / 1024).ToString("0.0") + " GB";
            return mb.ToString("0") + " MB";
        }

        public class RunResult { public int Code; public string Out; public string Err; public bool TimedOut; }

        public static RunResult Run(string file, string args, int timeoutMs)
        {
            RunResult r = new RunResult();
            r.Out = ""; r.Err = ""; r.Code = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    StringBuilder so = new StringBuilder();
                    StringBuilder se = new StringBuilder();
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) so.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) se.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        r.TimedOut = true;
                        try { p.Kill(); } catch { }
                    }
                    else { p.WaitForExit(); r.Code = p.ExitCode; }
                    r.Out = so.ToString();
                    r.Err = se.ToString();
                }
            }
            catch (Exception ex) { r.Err = ex.Message; }
            return r;
        }

        public static string RegString(Microsoft.Win32.RegistryKey root, string path, string name)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = root.OpenSubKey(path))
                {
                    if (k == null) return null;
                    object v = k.GetValue(name);
                    return v == null ? null : v.ToString();
                }
            }
            catch { return null; }
        }

        public static int? RegInt(Microsoft.Win32.RegistryKey root, string path, string name)
        {
            string s = RegString(root, path, name);
            int n;
            if (s != null && int.TryParse(s, out n)) return n;
            return null;
        }
    }

    /// <summary>Tiny key=value settings store in ProgramData.</summary>
    public static class Settings
    {
        static Dictionary<string, string> _d;
        static readonly object _lk = new object();
        static string FilePath { get { return Path.Combine(Paths.DataDir, "settings.ini"); } }

        static void Load()
        {
            if (_d != null) return;
            _d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) _d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                    }
            }
            catch { }
        }
        public static string Get(string key, string def)
        {
            lock (_lk) { Load(); string v; return _d.TryGetValue(key, out v) ? v : def; }
        }
        public static bool GetBool(string key, bool def)
        {
            string v = Get(key, def ? "1" : "0");
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        public static void Set(string key, string val)
        {
            lock (_lk)
            {
                Load();
                _d[key] = val;
                try
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (KeyValuePair<string, string> kv in _d) sb.AppendLine(kv.Key + "=" + kv.Value);
                    File.WriteAllText(FilePath, sb.ToString());
                }
                catch { }
            }
        }
        public static void SetBool(string key, bool v) { Set(key, v ? "1" : "0"); }
    }
}
