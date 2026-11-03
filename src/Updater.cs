using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace InfernoXC
{
    public class UpdateInfo
    {
        public bool Ok;
        public string Error = "";
        public string Tag = "", Notes = "", PageUrl = "", ExeUrl = "", HashUrl = "";
        public long ExeSize;
        public Version Latest;
        public bool Newer;
        public string Summary { get { return Ok ? (Newer ? "Version " + Tag.TrimStart('v', 'V') + " is available" : "You have the latest version (" + Brand.Version + ")") : Error; } }
    }

    /// <summary>
    /// Checks GitHub Releases and installs a newer build only when its SHA-256 matches the hash published with the release.
    /// Nothing is downloaded or installed without a click, and the old file is kept until the new one has started.
    /// </summary>
    public static class Updater
    {
        static string Api { get { return Brand.RepoUrl.Replace("https://github.com/", "https://api.github.com/repos/") + "/releases/latest"; } }

        static void Prepare()
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288 | (SecurityProtocolType)768; }
            catch { try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { } }
        }

        static HttpWebRequest Req(string url)
        {
            Prepare();
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = "IXC-PC-Ready/" + Brand.Version;
            r.Timeout = 15000; r.ReadWriteTimeout = 30000;
            r.AllowAutoRedirect = true;
            return r;
        }

        static string GetText(string url)
        {
            using (WebResponse w = Req(url).GetResponse())
            using (StreamReader sr = new StreamReader(w.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
        }

        public static Version Parse(string tag)
        {
            Match m = Regex.Match(tag ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return null;
            return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }

        public static UpdateInfo Check()
        {
            UpdateInfo u = new UpdateInfo();
            try
            {
                string json = GetText(Api);
                JavaScriptSerializer js = new JavaScriptSerializer();
                Dictionary<string, object> d = (Dictionary<string, object>)js.DeserializeObject(json);
                u.Tag = Convert.ToString(d["tag_name"]);
                u.Notes = d.ContainsKey("body") ? Convert.ToString(d["body"]) : "";
                u.PageUrl = d.ContainsKey("html_url") ? Convert.ToString(d["html_url"]) : Brand.RepoUrl;
                u.Latest = Parse(u.Tag);
                if (u.Latest == null) { u.Error = "The latest release has no version number in its tag (" + u.Tag + ")."; return u; }
                object[] assets = d.ContainsKey("assets") ? d["assets"] as object[] : null;
                if (assets != null)
                    foreach (object o in assets)
                    {
                        Dictionary<string, object> a = (Dictionary<string, object>)o;
                        string name = Convert.ToString(a["name"]), url = Convert.ToString(a["browser_download_url"]);
                        if (string.Equals(name, "IXC-PC-Ready.exe", StringComparison.OrdinalIgnoreCase)) { u.ExeUrl = url; try { u.ExeSize = Convert.ToInt64(a["size"]); } catch { } }
                        else if (string.Equals(name, "IXC-PC-Ready.exe.sha256", StringComparison.OrdinalIgnoreCase)) u.HashUrl = url;
                    }
                Version cur = Parse(Brand.Version);
                u.Newer = cur != null && u.Latest > cur;
                u.Ok = true;
            }
            catch (WebException ex)
            {
                HttpWebResponse r = ex.Response as HttpWebResponse;
                u.Error = r != null && r.StatusCode == HttpStatusCode.NotFound ? "No release has been published yet." : "Could not reach GitHub: " + ex.Message;
            }
            catch (Exception ex) { u.Error = "Could not check for updates: " + ex.Message; Log.Err("update check", ex); }
            return u;
        }

        /// <summary>Downloads, verifies and swaps the program file. Returns null when the new version is ready to start.</summary>
        public static string Install(UpdateInfo u, out string newExe)
        {
            newExe = null;
            try
            {
                if (u == null || !u.Ok || !u.Newer) return "There is no newer version to install.";
                if (string.IsNullOrEmpty(u.ExeUrl)) return "The release has no IXC-PC-Ready.exe file attached.";
                if (string.IsNullOrEmpty(u.HashUrl)) return "The release has no published SHA-256 file, so the download cannot be verified. Nothing was installed.";
                if (!u.ExeUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) || !u.HashUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
                    return "The download is not hosted on GitHub. Nothing was installed.";

                string expected = Regex.Match(GetText(u.HashUrl), @"\b[0-9a-fA-F]{64}\b").Value.ToLowerInvariant();
                if (expected.Length != 64) return "The published SHA-256 file is not valid. Nothing was installed.";

                string self = Assembly.GetExecutingAssembly().Location;
                string dir = Path.GetDirectoryName(self);
                string tmp = Path.Combine(dir, "IXC-PC-Ready.update.tmp");
                using (WebResponse w = Req(u.ExeUrl).GetResponse())
                using (Stream rs = w.GetResponseStream())
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                    rs.CopyTo(fs);

                string actual;
                using (FileStream fs = File.OpenRead(tmp))
                using (SHA256 sha = SHA256.Create())
                    actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                if (actual != expected) { try { File.Delete(tmp); } catch { } return "The download does not match its published SHA-256, so it was discarded. Nothing was installed."; }

                byte[] head = new byte[2];
                using (FileStream fs = File.OpenRead(tmp)) fs.Read(head, 0, 2);
                if (head[0] != 'M' || head[1] != 'Z') { try { File.Delete(tmp); } catch { } return "The download is not a Windows program. Nothing was installed."; }

                // a running program cannot be overwritten but it can be renamed: keep the old one until the new one has started
                string old = self + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(self, old);
                try { File.Move(tmp, self); }
                catch { File.Move(old, self); throw; }
                newExe = self;
                Log.Info("Updated to " + u.Tag + " (sha256 verified)");
                return null;
            }
            catch (UnauthorizedAccessException) { return "Windows would not let the program file be replaced. Run the app as administrator and try again."; }
            catch (Exception ex) { Log.Err("update install", ex); return "The update failed: " + ex.Message + " Your current version was not changed."; }
        }

        /// <summary>Called once at start: removes the previous version left by the last update.</summary>
        public static void CleanupOld()
        {
            try
            {
                string self = Assembly.GetExecutingAssembly().Location;
                string old = self + ".old";
                if (File.Exists(old)) File.Delete(old);
                string tmp = Path.Combine(Path.GetDirectoryName(self), "IXC-PC-Ready.update.tmp");
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch { }
        }
    }
}
