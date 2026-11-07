using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace InfernoXC
{
    /// <summary>State of one scan or clean run. The UI reads it on a timer; workers write it.</summary>
    public class CleanJob
    {
        public volatile bool Cancelled;
        public ManualResetEvent Gate = new ManualResetEvent(true);
        public volatile string Operation = "";
        public volatile string Current = "";
        public long Scanned, Cleaned, Freed, Skipped, Errors;
        public readonly List<string> Lines = new List<string>();
        public readonly List<string> ErrorLines = new List<string>();
        readonly object _lk = new object();

        public bool Paused { get { return !Gate.WaitOne(0); } }
        public void Pause() { Gate.Reset(); }
        public void Resume() { Gate.Set(); }
        public void Cancel() { Cancelled = true; Gate.Set(); }

        /// <summary>Called between files: waits while paused and aborts when cancelled.</summary>
        public void Check()
        {
            Gate.WaitOne();
            if (Cancelled) throw new OperationCanceledException();
        }
        public void Note(string s)
        {
            lock (_lk) { Lines.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + s); if (Lines.Count > 4000) Lines.RemoveRange(0, 1000); }
        }
        public void Fail(string what, Exception ex)
        {
            Interlocked.Increment(ref Errors);
            string m = what + ": " + ex.Message;
            lock (_lk) { if (ErrorLines.Count < 200) ErrorLines.Add(m); }
            Note("ERROR " + m);
            Log.Err(what, ex);
        }
        public void Skip(string path, string why)
        {
            long n = Interlocked.Increment(ref Skipped);
            if (n <= 25) Note("skipped (" + why + "): " + path);
            else if (n == 26) Note("more files were skipped; only the first 25 are listed");
        }
        public string[] SnapshotLines(int from, out int total)
        {
            lock (_lk) { total = Lines.Count; if (from >= total) return new string[0]; return Lines.Skip(from).ToArray(); }
        }
    }

    public class CleanItem
    {
        public string Id, Title, Detail, Group, Risk;
        public bool Recommended, Routine, Shader;
        public Func<CleanJob, long> Scan;      // bytes that would be removed (-1 = unknown until cleaned)
        public Func<CleanJob, long> Run;       // bytes freed
        public long Size = -1;
        public bool Selected;
        public TextBlock SizeText;
        public ToggleButton Switch;
        public Border RowBox;
    }

    public static class CleanKit
    {
        static readonly string Local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        static readonly string Roaming = Environment.GetEnvironmentVariable("APPDATA") ?? "";
        static readonly string ProgData = Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData";
        static readonly string Win = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

        [StructLayout(LayoutKind.Sequential)] struct RBINFO { public uint cbSize; public long Size; public long Items; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBinW(string root, ref RBINFO info);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBinW(IntPtr hwnd, string root, uint flags);

        // ------------------------------------------------------------ file walking with progress, pause and cancel

        /// <summary>Plain size of a folder. Used by other pages; no progress.</summary>
        public static long DirSize(string dir) { return ScanDir(dir, null); }

        static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; } catch { return false; }
        }

        static long ScanDir(string dir, CleanJob job)
        {
            long total = 0;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
            Stack<string> st = new Stack<string>();
            st.Push(dir);
            while (st.Count > 0)
            {
                if (job != null) job.Check();
                string d = st.Pop();
                if (job != null) job.Current = d;
                try
                {
                    foreach (string f in Directory.EnumerateFiles(d))
                    {
                        try
                        {
                            total += new FileInfo(f).Length;
                            if (job != null) { long n = Interlocked.Increment(ref job.Scanned); if ((n & 63) == 0) { job.Check(); job.Current = f; } }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                    }
                    foreach (string s in Directory.EnumerateDirectories(d))
                        if (!IsLink(s)) st.Push(s);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { if (job != null) job.Skip(d, ex is UnauthorizedAccessException ? "no access" : "unreadable"); }
            }
            return total;
        }

        static long DelRec(string dir, DateTime cut, bool removeSelf, CleanJob job)
        {
            long freed = 0;
            job.Check();
            job.Current = dir;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir).ToList())
                {
                    job.Check();
                    try
                    {
                        FileInfo fi = new FileInfo(f);
                        Interlocked.Increment(ref job.Scanned);
                        if (fi.LastWriteTime > cut) continue;
                        long l = fi.Length;
                        fi.Attributes = FileAttributes.Normal;
                        fi.Delete();
                        freed += l;
                        Interlocked.Add(ref job.Freed, l);
                        long c = Interlocked.Increment(ref job.Cleaned);
                        if ((c & 31) == 0) job.Current = f;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { job.Skip(f, ex is UnauthorizedAccessException ? "in use or protected" : "in use"); }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { job.Skip(dir, ex.Message); }
            try
            {
                foreach (string s in Directory.EnumerateDirectories(dir).ToList())
                {
                    if (IsLink(s)) continue;
                    freed += DelRec(s, cut, true, job);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { job.Skip(dir, ex.Message); }
            if (removeSelf) { try { Directory.Delete(dir, false); } catch { } }
            return freed;
        }

        /// <summary>Used by the built-in functional test on a throw-away folder.</summary>
        public static long DeleteForTest(string dir, CleanJob job) { return DelMany(new string[] { dir }, 30, job); }
        public static long ScanForTest(string dir, CleanJob job) { return ScanDir(dir, job); }

        static long ScanMany(string[] dirs, CleanJob job) { long t = 0; foreach (string d in dirs) t += ScanDir(d, job); return t; }
        static long DelMany(string[] dirs, int ageMinutes, CleanJob job)
        {
            long t = 0;
            foreach (string d in dirs) if (Directory.Exists(d)) t += DelRec(d, DateTime.Now.AddMinutes(-ageMinutes), false, job);
            return t;
        }
        static string[] Existing(params string[] dirs) { return dirs.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).ToArray(); }

        static long FilesSize(string dir, string pattern, CleanJob job)
        {
            long t = 0;
            try
            {
                if (Directory.Exists(dir))
                    foreach (string f in Directory.EnumerateFiles(dir, pattern))
                    {
                        job.Check();
                        try { t += new FileInfo(f).Length; Interlocked.Increment(ref job.Scanned); } catch { }
                    }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { job.Skip(dir, ex.Message); }
            return t;
        }
        static long DeleteFiles(string dir, string pattern, CleanJob job)
        {
            long t = 0;
            try
            {
                if (Directory.Exists(dir))
                    foreach (string f in Directory.EnumerateFiles(dir, pattern).ToList())
                    {
                        job.Check();
                        try
                        {
                            FileInfo fi = new FileInfo(f); long l = fi.Length; fi.Attributes = FileAttributes.Normal; fi.Delete(); t += l;
                            Interlocked.Add(ref job.Freed, l); Interlocked.Increment(ref job.Cleaned); job.Current = f;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { job.Skip(f, ex.Message); }
                    }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { job.Skip(dir, ex.Message); }
            return t;
        }

        /// <summary>Runs a program, polling so Cancel can stop it.</summary>
        static Util.RunResult RunCancelable(string file, string args, CleanJob job, int timeoutMs)
        {
            Util.RunResult r = new Util.RunResult(); r.Out = ""; r.Err = ""; r.Code = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    StringBuilder so = new StringBuilder();
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (so) so.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                    while (!p.WaitForExit(400))
                    {
                        if (job.Cancelled) { try { p.Kill(); } catch { } throw new OperationCanceledException(); }
                        if (DateTime.UtcNow > end) { r.TimedOut = true; try { p.Kill(); } catch { } break; }
                    }
                    if (!r.TimedOut) { p.WaitForExit(); r.Code = p.ExitCode; }
                    lock (so) r.Out = so.ToString();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { r.Err = ex.Message; }
            return r;
        }

        static long FreeOnSystemDrive()
        {
            try { return new DriveInfo(Path.GetPathRoot(Win)).AvailableFreeSpace; } catch { return 0; }
        }

        // ------------------------------------------------------------ special operations

        static long EventLogSize(CleanJob job) { return FilesSize(Path.Combine(Win, @"System32\winevt\Logs"), "*.evtx", job); }

        static long ClearEventLogs(CleanJob job)
        {
            long before = EventLogSize(job);
            Util.RunResult r = RunCancelable("wevtutil.exe", "el", job, 20000);
            foreach (string line in (r.Out ?? "").Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                job.Check();
                job.Current = line.Trim();
                RunCancelable("wevtutil.exe", "cl \"" + line.Trim() + "\"", job, 20000);
            }
            long freed = Math.Max(0, before - EventLogSize(job));
            Interlocked.Add(ref job.Freed, freed);
            return freed;
        }

        static long CleanWindowsUpdate(CleanJob job)
        {
            string dir = Path.Combine(Win, @"SoftwareDistribution\Download");
            bool wu = GameMode.SvcRunning("wuauserv"), bits = GameMode.SvcRunning("BITS");
            long f = 0;
            try
            {
                job.Operation = "Pausing the update service";
                if (wu) RunCancelable("net.exe", "stop wuauserv /y", job, 30000);
                if (bits) RunCancelable("net.exe", "stop bits /y", job, 30000);
                job.Operation = "Deleting update downloads";
                f = DelRec(dir, DateTime.Now, false, job);
            }
            finally
            {
                // always give the services back, even when cancelled
                if (bits) Util.Run("net.exe", "start bits", 30000);
                if (wu) Util.Run("net.exe", "start wuauserv", 30000);
            }
            return f;
        }

        static long ComponentCleanup(CleanJob job)
        {
            long before = FreeOnSystemDrive();
            job.Current = "DISM is working. This step cannot be paused and can take several minutes.";
            RunCancelable("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", job, 40 * 60 * 1000);
            long freed = Math.Max(0, FreeOnSystemDrive() - before);
            Interlocked.Add(ref job.Freed, freed);
            return freed;
        }

        static long RamPurge(CleanJob job)
        {
            // Open apps are never emptied out of RAM (that makes them stutter while they page back in),
            // and nothing is touched while a game or OBS is running.
            if (GameMode.LiveCount > 0 || GameMode.ObsRunning()) { job.Note("Free memory now: skipped because a game or OBS is running"); return 0; }
            double before = GameMode.AvailMB();
            Native.MemoryCommand(3);
            Native.MemoryCommand(4);
            return (long)(Math.Max(0, GameMode.AvailMB() - before) * 1048576);   // memory, not disk: not added to space recovered
        }

        static string _trimOut = "";
        public static string LastTrim { get { return _trimOut; } }
        static long Trim(CleanJob job)
        {
            string ps = "$o=@(); foreach($v in Get-Volume | Where-Object { $_.DriveType -eq 'Fixed' -and $_.DriveLetter }){ try { $pd = Get-Partition -DriveLetter $v.DriveLetter | Get-Disk | Get-PhysicalDisk; if($pd.MediaType -eq 'SSD' -or $pd.BusType -eq 'NVMe'){ Optimize-Volume -DriveLetter $v.DriveLetter -ReTrim -ErrorAction Stop | Out-Null; $o += ($v.DriveLetter + ': trimmed') } else { $o += ($v.DriveLetter + ': skipped (not an SSD)') } } catch { $o += ($v.DriveLetter + ': ' + $_.Exception.Message) } }; $o -join '; '";
            job.Current = "Asking each SSD to trim free blocks";
            Util.RunResult r = RunCancelable("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + ps.Replace("\"", "\\\"") + "\"", job, 10 * 60 * 1000);
            _trimOut = (r.Out ?? "").Trim();
            job.Note("TRIM: " + _trimOut);
            return 0;
        }

        static long RecycleSize()
        {
            try { RBINFO i = new RBINFO(); i.cbSize = (uint)Marshal.SizeOf(typeof(RBINFO)); if (SHQueryRecycleBinW(null, ref i) == 0) return i.Size; }
            catch { }
            return 0;
        }
        static long EmptyRecycle(CleanJob job)
        {
            long before = RecycleSize();
            job.Current = "Emptying the Recycle Bin";
            int r = SHEmptyRecycleBinW(IntPtr.Zero, null, 0x7);
            if (r != 0 && r != unchecked((int)0x8000FFFF)) throw new InvalidOperationException("Recycle Bin returned 0x" + r.ToString("X"));
            Interlocked.Add(ref job.Freed, before);
            return before;
        }

        static string[] BrowserCaches()
        {
            List<string> l = new List<string>();
            string[] roots = new string[] { Path.Combine(Local, @"Google\Chrome\User Data"), Path.Combine(Local, @"Microsoft\Edge\User Data"), Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data") };
            foreach (string r in roots)
            {
                try
                {
                    if (!Directory.Exists(r)) continue;
                    foreach (string prof in Directory.GetDirectories(r))
                    {
                        string n = Path.GetFileName(prof);
                        if (n != "Default" && !n.StartsWith("Profile ")) continue;
                        foreach (string c in new string[] { "Cache", "Code Cache", "GPUCache" })
                        {
                            string p = Path.Combine(prof, c);
                            if (Directory.Exists(p)) l.Add(p);
                        }
                    }
                }
                catch { }
            }
            string ff = Path.Combine(Local, @"Mozilla\Firefox\Profiles");
            try { if (Directory.Exists(ff)) foreach (string prof in Directory.GetDirectories(ff)) { string p = Path.Combine(prof, "cache2"); if (Directory.Exists(p)) l.Add(p); } } catch { }
            return l.ToArray();
        }

        /// <summary>Name of a known game that is running, or null. Shader caches are never touched then.</summary>
        public static string RunningGame()
        {
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    try { if (SysMonitor.KnownGames.Contains(p.ProcessName)) return p.ProcessName; }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
            return null;
        }

        static long ShaderRun(string[] dirs, CleanJob job)
        {
            string g = RunningGame();
            if (g != null)
            {
                job.Note("Shader cache skipped: " + g + " is running. Close the game and try again.");
                job.Skip(string.Join("; ", dirs), "game running: " + g);
                return 0;
            }
            return DelMany(dirs, 0, job);
        }

        // ------------------------------------------------------------ the list

        static void Add(List<CleanItem> l, string group, string id, string title, string detail, string risk, bool rec, bool routine, bool shader, Func<CleanJob, long> scan, Func<CleanJob, long> run)
        {
            CleanItem c = new CleanItem();
            c.Group = group; c.Id = id; c.Title = title; c.Detail = detail; c.Risk = risk; c.Recommended = rec; c.Routine = routine; c.Shader = shader; c.Scan = scan; c.Run = run;
            l.Add(c);
        }

        public static List<CleanItem> Build()
        {
            List<CleanItem> l = new List<CleanItem>();
            string temp = Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath();
            string tWin = Path.Combine(Win, "Temp");

            Add(l, "Temporary files", "temp-user", "User temporary files", "Leftovers from installers and apps in your temp folder. Files used in the last 30 minutes are kept.", "Low", true, true, false,
                j => ScanDir(temp, j), j => DelMany(new string[] { temp }, 30, j));
            Add(l, "Temporary files", "temp-win", "Windows temporary files", "System-wide temporary files. Files used in the last 30 minutes are kept.", "Low", true, true, false,
                j => ScanDir(tWin, j), j => DelMany(new string[] { tWin }, 30, j));
            Add(l, "Temporary files", "thumbs", "Thumbnail and icon cache", "Explorer rebuilds it when you open folders. Skipped files are ones Explorer is using.", "Low", true, true, false,
                j => FilesSize(Path.Combine(Local, @"Microsoft\Windows\Explorer"), "*cache_*.db", j), j => DeleteFiles(Path.Combine(Local, @"Microsoft\Windows\Explorer"), "*cache_*.db", j));
            string[] wlogs = new string[] { Path.Combine(Win, @"Logs\CBS"), Path.Combine(Win, @"Logs\DISM") };
            Add(l, "Temporary files", "wlogs", "Windows logs", "Old servicing and DISM logs, older than 7 days. Event logs are a separate item.", "Low", true, true, false,
                j => ScanMany(Existing(wlogs), j), j => DelMany(Existing(wlogs), 7 * 24 * 60, j));
            string[] dumps = new string[] { Path.Combine(Win, "Minidump"), Path.Combine(Local, "CrashDumps"), Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportQueue") };
            Add(l, "Temporary files", "dumps", "Crash dumps and error reports", "Old dump files. Keep them while you are still investigating a crash.", "Low", false, false, false,
                j => ScanMany(Existing(dumps), j) + FileLen(Path.Combine(Win, "MEMORY.DMP")), j => DelMany(Existing(dumps), 0, j) + DelFile(Path.Combine(Win, "MEMORY.DMP"), j));
            Add(l, "Temporary files", "recycle", "Recycle Bin", "Permanently deletes what is in the Recycle Bin. Look inside first: this is the one item that holds your own files.", "Medium", false, false, false,
                j => RecycleSize(), EmptyRecycle);
            Add(l, "Temporary files", "prefetch", "Prefetch", "Launch hints. Windows rebuilds them, but apps start a little slower until it does. Rarely worth clearing.", "Low", false, false, false,
                j => ScanDir(Path.Combine(Win, "Prefetch"), j), j => DelMany(new string[] { Path.Combine(Win, "Prefetch") }, 0, j));

            // graphics caches: only the vendors present, never selected by default
            string[] amd = Existing(Path.Combine(Local, @"AMD\DxCache"), Path.Combine(Local, @"AMD\DxcCache"), Path.Combine(Local, @"AMD\GLCache"), Path.Combine(Local, @"AMD\VkCache"));
            if (amd.Length > 0) Add(l, "Graphics caches (troubleshooting only)", "sh-amd", "AMD shader cache", "Deleting it makes every game rebuild its shaders, which causes stutter until the cache is full again. Use it for graphics glitches or after a driver problem, not for routine cleaning.", "Medium", false, false, true, j => ScanMany(amd, j), j => ShaderRun(amd, j));
            string[] nv = Existing(Path.Combine(Local, @"NVIDIA\DXCache"), Path.Combine(Local, @"NVIDIA\GLCache"), Path.Combine(ProgData, @"NVIDIA Corporation\NV_Cache"));
            if (nv.Length > 0) Add(l, "Graphics caches (troubleshooting only)", "sh-nv", "NVIDIA shader cache", "Deleting it makes games recompile shaders and stutter at first. Use it only for graphics glitches or after a driver problem.", "Medium", false, false, true, j => ScanMany(nv, j), j => ShaderRun(nv, j));
            string[] intel = Existing(Path.Combine(Local, @"Intel\ShaderCache"), Path.Combine(Local, @"Intel\GPUCache"));
            if (intel.Length > 0) Add(l, "Graphics caches (troubleshooting only)", "sh-intel", "Intel graphics cache", "Same trade-off as the other shader caches: rebuilt on demand, with a short stutter. Troubleshooting only.", "Medium", false, false, true, j => ScanMany(intel, j), j => ShaderRun(intel, j));
            string[] d3d = Existing(Path.Combine(Local, "D3DSCache"));
            if (d3d.Length > 0) Add(l, "Graphics caches (troubleshooting only)", "sh-d3d", "DirectX shader cache", "Windows' own shader cache. Games rebuild it on demand, so expect a stutter on the first runs. Troubleshooting only.", "Medium", false, false, true, j => ScanMany(d3d, j), j => ShaderRun(d3d, j));

            string[] glogs = Existing(Path.Combine(Local, @"TslGame\Saved\Logs"), Path.Combine(Local, @"TslGame\Saved\Crashes"), Path.Combine(Local, @"VALORANT\Saved\Logs"), Path.Combine(Local, @"VALORANT\Saved\Crashes"));
            if (glogs.Length > 0) Add(l, "Apps and games", "glogs", "Game logs and crash folders", "PUBG and VALORANT log files. Not your settings or saves.", "Low", true, true, false, j => ScanMany(glogs, j), j => DelMany(glogs, 60, j));
            string[] appc = Existing(Path.Combine(Roaming, @"discord\Cache"), Path.Combine(Roaming, @"discord\Code Cache"), Path.Combine(Roaming, @"discord\GPUCache"),
                Path.Combine(Local, @"Steam\htmlcache"), Path.Combine(Roaming, @"Microsoft\Teams\Cache"));
            if (appc.Length > 0) Add(l, "Apps and games", "appcache", "App caches (Discord, Steam browser, Teams)", "Web caches these apps rebuild by themselves. You stay signed in. Files in use are skipped.", "Low", true, false, false, j => ScanMany(appc, j), j => DelMany(appc, 30, j));
            string[] br = BrowserCaches();
            if (br.Length > 0) Add(l, "Apps and games", "browser", "Browser caches", "Chrome, Edge, Brave and Firefox cache only. You stay signed in. Close the browser first.", "Low", false, false, false, j => ScanMany(br, j), j => DelMany(br, 30, j));

            string doDir = Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache");
            Add(l, "Windows", "wu", "Windows Update download cache", "Already-installed update files. The update service is paused for a moment, then started again.", "Low", true, true, false,
                j => ScanDir(Path.Combine(Win, @"SoftwareDistribution\Download"), j), CleanWindowsUpdate);
            Add(l, "Windows", "do", "Delivery Optimization cache", "Update pieces Windows keeps to share with other PCs.", "Low", true, true, false,
                j => ScanDir(doDir, j), j => DelMany(new string[] { doDir }, 0, j));
            Add(l, "Windows", "dism", "Windows component store cleanup", "Removes superseded system files with DISM. Size is measured when it finishes. Takes a few minutes and cannot be paused.", "Low", false, false, false,
                j => -1, ComponentCleanup);
            Add(l, "Windows", "events", "Event logs", "Clears the Windows Event Viewer logs. This also erases the history the Crash Center reads.", "High", false, false, false,
                j => EventLogSize(j), ClearEventLogs);

            Add(l, "Memory and drive", "ram", "Free memory now", "Releases Windows' cached (standby) memory. Open apps keep their memory and nothing is closed. Skipped while a game or OBS is running. This is memory, not disk space.", "Low", true, false, false,
                j => -1, RamPurge);
            Add(l, "Memory and drive", "trim", "SSD TRIM", "Tells your SSDs which blocks are free so they keep their speed. Hard disks are skipped.", "Low", true, true, false,
                j => -1, Trim);

            foreach (CleanItem c in l) c.Selected = Settings.GetBool("clean2_" + c.Id, c.Recommended);
            return l;
        }

        static long FileLen(string f) { try { return File.Exists(f) ? new FileInfo(f).Length : 0; } catch { return 0; } }
        static long DelFile(string f, CleanJob job)
        {
            try { if (!File.Exists(f)) return 0; long n = new FileInfo(f).Length; File.Delete(f); Interlocked.Add(ref job.Freed, n); return n; }
            catch (Exception ex) { job.Skip(f, ex.Message); return 0; }
        }

        // ------------------------------------------------------------ shared by the page and the scheduled task

        public static void RunItem(CleanItem c, CleanJob job)
        {
            job.Operation = c.Title;
            try
            {
                long f = c.Run(job);
                job.Note(c.Title + ": done" + (c.Id == "ram" ? ", " + Util.Bytes(f) + " of memory freed" : f > 0 ? ", " + Util.Bytes(f) + " freed" : ""));
            }
            catch (OperationCanceledException) { job.Note(c.Title + ": cancelled"); throw; }
            catch (Exception ex) { job.Fail(c.Title, ex); }
        }

        /// <summary>Used by the scheduled task: routine items only, never shader caches.</summary>
        public static int RunHeadless()
        {
            CleanJob job = new CleanJob();
            try
            {
                string g = RunningGame();
                job.Note("Scheduled cleanup started" + (g != null ? " (a game is running, so only light items run)" : ""));
                foreach (CleanItem c in Build())
                {
                    if (!c.Routine || c.Shader) continue;
                    if (g != null && (c.Id == "wu" || c.Id == "trim")) continue;
                    RunItem(c, job);
                }
            }
            catch (OperationCanceledException) { }
            job.Note("Finished. Freed " + Util.Bytes(job.Freed) + ", skipped " + job.Skipped + ", errors " + job.Errors);
            try { File.AppendAllText(Path.Combine(Paths.DataDir, "cleanup-log.txt"), string.Join("\r\n", job.Lines.ToArray()) + "\r\n\r\n", Encoding.UTF8); } catch { }
            return (int)job.Errors;
        }

        const string TaskName = "IXC PC Ready Cleanup";
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
            Util.RunResult r = Util.Run("schtasks.exe", "/Create /TN \"" + TaskName + "\" /TR \"\\\"" + exe + "\\\" --cleanup\" " + sc + " /ST 12:00 /RL HIGHEST /F", 20000);
            return r.Code == 0 ? null : ((r.Out ?? "") + (r.Err ?? "")).Trim();
        }
    }

    public class CleanPage : PageBase
    {
        List<CleanItem> _items;
        TextBlock _total, _status, _op, _cur, _count;
        ProgressBar _bar;
        Button _scanBtn, _cleanBtn, _pauseBtn, _cancelBtn;
        TextBox _log;
        StackPanel _sumHost;
        bool _busy, _scannedOnce;
        CleanJob _job;
        DispatcherTimer _timer;
        int _logShown;
        DateTime _t0;
        List<CleanItem> _runList;
        int _runIdx;
        Dictionary<string, Button> _sched = new Dictionary<string, Button>();
        TextBlock _schedMsg;

        delegate bool Pick(CleanItem c);
        Dictionary<string, Expander> _secs = new Dictionary<string, Expander>();
        List<KeyValuePair<RadioButton, Pick>> _presets = new List<KeyValuePair<RadioButton, Pick>>();
        DateTime _lastScan = DateTime.MinValue;
        bool _forceSelText;

        public CleanPage() : base("Clean and Optimize", "Scan first, choose what to remove, then clean. Nothing is removed until you press Clean.")
        {
            _items = CleanKit.Build();

            // ---- progress card
            StackPanel pc = new StackPanel();
            _total = UK.T("Ready to scan", "Big");
            _status = UK.T("Press Scan to measure each item. Sizes are estimates.", "Muted");
            _status.Margin = new Thickness(0, 4, 0, 10);
            _bar = new ProgressBar(); _bar.Style = UK.S("Thin"); _bar.Value = 0; _bar.Margin = new Thickness(0, 0, 0, 8);
            _op = UK.T("", "Body"); _op.FontWeight = FontWeights.SemiBold;
            _cur = UK.T("", "Muted"); _cur.TextTrimming = TextTrimming.CharacterEllipsis; _cur.TextWrapping = TextWrapping.NoWrap; _cur.Margin = new Thickness(0, 2, 0, 0);
            _count = UK.T("", "Muted"); _count.Margin = new Thickness(0, 4, 0, 10);
            pc.Children.Add(_total); pc.Children.Add(_status); pc.Children.Add(_bar); pc.Children.Add(_op); pc.Children.Add(_cur); pc.Children.Add(_count);

            WrapPanel btns = new WrapPanel();
            _scanBtn = UK.Btn("Scan", false, delegate { StartScan(_items); });
            _cleanBtn = UK.Btn("Clean selected", true, delegate { StartClean(_items.Where(i => i.Selected).ToList()); });
            _pauseBtn = UK.Btn("Pause", false, delegate { TogglePause(); });
            _cancelBtn = UK.Btn("Cancel", false, delegate { if (_job != null) { _job.Cancel(); _status.Text = "Cancelling..."; } });
            _pauseBtn.IsEnabled = false; _cancelBtn.IsEnabled = false;
            btns.Children.Add(_scanBtn); btns.Children.Add(_cleanBtn); btns.Children.Add(_pauseBtn); btns.Children.Add(_cancelBtn);
            pc.Children.Add(btns);
            WrapPanel pre = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            pre.Children.Add(UK.T("Quick choice:  ", "Muted"));
            ((TextBlock)pre.Children[0]).VerticalAlignment = VerticalAlignment.Center;
            AddPreset(pre, "Recommended", delegate(CleanItem c) { return c.Recommended; }, null);
            AddPreset(pre, "Everything safe", delegate(CleanItem c) { return !c.Shader && c.Risk != "High"; }, null);
            AddPreset(pre, "After a fresh Windows install", delegate(CleanItem c) { return c.Risk != "High" && c.Id != "recycle" && c.Id != "prefetch"; }, "Selected everything except the Recycle Bin, Prefetch and event logs, including graphics caches and the component store. Right after installing Windows or a new graphics driver is a good time to clear them.");
            AddPreset(pre, "None", delegate(CleanItem c) { return false; }, null);
            pc.Children.Add(pre);
            Body.Children.Add(UK.Card(pc));

            _sumHost = new StackPanel();
            Body.Children.Add(_sumHost);

            // ---- categories
            foreach (string group in _items.Select(i => i.Group).Distinct())
            {
                List<CleanItem> g = _items.Where(i => i.Group == group).ToList();
                StackPanel sp = new StackPanel();
                for (int i = 0; i < g.Count; i++) sp.Children.Add(BuildRow(g[i], i == g.Count - 1));
                bool shaderGroup = g.Any(x => x.Shader);
                Expander sec = UK.Section(group, shaderGroup ? "Off by default. Routine cleaning of these slows games down." : g.Count + " item" + (g.Count == 1 ? "" : "s"), sp, true, null);
                _secs[group] = sec;
                Body.Children.Add(sec);
            }

            // ---- schedule
            StackPanel sch = new StackPanel();
            sch.Children.Add(UK.T("Runs the routine items only (temp files, logs, update cache, TRIM). It never touches shader caches, the Recycle Bin, browser data or event logs.", "Muted"));
            WrapPanel sb = new WrapPanel(); sb.Margin = new Thickness(0, 10, 0, 0);
            foreach (string m in new string[] { "Off", "Weekly", "Monthly" })
            {
                string mode = m;
                Button b = UK.Btn(m, false, delegate { SetSchedule(mode); });
                _sched[m] = b; sb.Children.Add(b);
            }
            sch.Children.Add(sb);
            _schedMsg = UK.T("Checking the current schedule...", "Muted"); _schedMsg.Margin = new Thickness(0, 6, 0, 0);
            sch.Children.Add(_schedMsg);
            Body.Children.Add(UK.Section("Scheduled cleanup", "Optional. Manual cleaning stays the default.", sch, false, null));

            // ---- log
            _log = new TextBox(); _log.Style = UK.S("Box"); _log.IsReadOnly = true; _log.MinHeight = 140; _log.MaxHeight = 320;
            _log.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _log.TextWrapping = TextWrapping.NoWrap; _log.FontFamily = new FontFamily("Consolas");
            Body.Children.Add(UK.Section("Detailed log", "Every step, every skipped file, every error", _log, false, null));

            _timer = new DispatcherTimer(); _timer.Interval = TimeSpan.FromMilliseconds(250);
            _timer.Tick += delegate { Refresh(); };
            UpdateTotal();
            UK.Bg(delegate { return CleanKit.ScheduleState(); }, delegate(object o) { MarkSchedule((string)o ?? "Off"); });
        }

        void AddPreset(WrapPanel host, string label, Pick pick, string status)
        {
            RadioButton rb = new RadioButton { Style = UK.S("Seg"), GroupName = "cleanpreset", Content = label, Margin = new Thickness(0, 0, 0, 4) };
            Pick pk = pick; string st = status;
            rb.Click += delegate
            {
                Choose(pk);
                if (st != null) _status.Text = st;
            };
            _presets.Add(new KeyValuePair<RadioButton, Pick>(rb, pick));
            host.Children.Add(rb);
        }

        void Choose(Pick p)
        {
            if (_busy) return;
            foreach (CleanItem c in _items) { c.Selected = p(c); c.Switch.IsChecked = c.Selected; Settings.SetBool("clean2_" + c.Id, c.Selected); }
            _forceSelText = true;
            UpdateTotal();
        }

        /// <summary>Re-reads every choice from Settings so the screen always matches what will really be cleaned.</summary>
        void ReloadSelection()
        {
            foreach (CleanItem c in _items) { c.Selected = Settings.GetBool("clean2_" + c.Id, c.Recommended); if (c.Switch != null) c.Switch.IsChecked = c.Selected; }
            _forceSelText = true;
            UpdateTotal();
        }

        public override void Shown()
        {
            if (_busy) return;
            ReloadSelection();
            // scan again each time the page opens (cheap), so sizes and selection are always current
            if ((DateTime.Now - _lastScan).TotalSeconds > 45) StartScan(_items);
        }

        UIElement BuildRow(CleanItem c, bool last)
        {
            ToggleButton sw = UK.Sw(c.Selected);
            c.Switch = sw;
            sw.Click += delegate
            {
                c.Selected = sw.IsChecked == true;
                Settings.SetBool("clean2_" + c.Id, c.Selected);
                _forceSelText = true;
                UpdateTotal();
            };
            c.SizeText = UK.Right(c.Size < 0 ? "" : Util.Bytes(c.Size));
            Button scan = UK.Btn("Scan", false, delegate { if (!_busy) StartScan(new List<CleanItem> { c }); });
            Button clean = UK.Btn("Clean", false, delegate { if (!_busy) StartClean(new List<CleanItem> { c }); });
            scan.FontSize = 12; clean.FontSize = 12; scan.Margin = new Thickness(0, 0, 6, 0); clean.Margin = new Thickness(0, 0, 14, 0);
            StackPanel ctl = new StackPanel(); ctl.Orientation = Orientation.Horizontal;
            ctl.Children.Add(scan); ctl.Children.Add(clean); ctl.Children.Add(sw);
            Border row = UK.Row(c.Title, c.Detail, c.Risk, c.Recommended ? "Recommended" : "Not for routine use", c.SizeText, ctl, last);
            c.RowBox = row;
            return row;
        }

        void UpdateTotal()
        {
            // selected rows get the selection colour, groups show how many of their items are chosen, and a preset button lights up when the selection matches it exactly
            foreach (CleanItem c in _items)
                if (c.RowBox != null) c.RowBox.Background = c.Selected ? UK.Br("SelBrush") : System.Windows.Media.Brushes.Transparent;
            foreach (KeyValuePair<string, Expander> kv in _secs)
            {
                List<CleanItem> g = _items.Where(i => i.Group == kv.Key).ToList();
                TextBlock t = kv.Value.Tag as TextBlock;
                if (t != null) t.Text = g.Count(i => i.Selected) + " of " + g.Count + " selected" + (g.Any(i => i.Shader) ? "  -  off by default, routine cleaning slows games down" : "");
            }
            foreach (KeyValuePair<RadioButton, Pick> kv in _presets)
                kv.Key.IsChecked = _items.All(c => c.Selected == kv.Value(c));
            if (_busy) return;
            long sum = _items.Where(i => i.Selected && i.Size > 0).Sum(i => i.Size);
            List<CleanItem> sel = _items.Where(i => i.Selected).ToList();
            _total.Text = _scannedOnce ? Util.Bytes(sum) + " ready to clean" : sel.Count + " of " + _items.Count + " selected";
            _status.Text = sel.Count + " of " + _items.Count + " items selected" + (_scannedOnce ? ". Sizes are estimates." : ". Press Scan to measure them.");
            if (_job == null || _forceSelText) { _count.Text = sel.Count == 0 ? "Nothing is selected." : "Selected: " + string.Join(", ", sel.Select(i => i.Title).ToArray()); _forceSelText = false; }
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            _scanBtn.IsEnabled = !busy; _cleanBtn.IsEnabled = !busy;
            _pauseBtn.IsEnabled = busy; _cancelBtn.IsEnabled = busy;
            _pauseBtn.Content = "Pause";
            if (busy) _timer.Start(); else _timer.Stop();
        }

        void TogglePause()
        {
            if (_job == null) return;
            if (_job.Paused) { _job.Resume(); _pauseBtn.Content = "Pause"; _status.Text = "Resumed."; }
            else { _job.Pause(); _pauseBtn.Content = "Resume"; _status.Text = "Paused. Steps already running (DISM, TRIM) finish first."; }
        }

        void Refresh()
        {
            CleanJob j = _job;
            if (j == null) return;
            _op.Text = j.Operation;
            _cur.Text = j.Current;
            _count.Text = "Files looked at: " + Interlocked.Read(ref j.Scanned).ToString("N0") + "   Files removed: " + Interlocked.Read(ref j.Cleaned).ToString("N0") + "   Space recovered: " + Util.Bytes(Interlocked.Read(ref j.Freed)) + "   Skipped: " + Interlocked.Read(ref j.Skipped) + "   Errors: " + Interlocked.Read(ref j.Errors);
            if (_runList != null && _runList.Count > 0 && !j.Paused)
            {
                double pct = (double)_runIdx / _runList.Count * 100.0;
                _bar.Value = Math.Min(100, pct);
                double el = (DateTime.Now - _t0).TotalSeconds;
                if (pct > 8 && el > 6 && pct < 99)
                {
                    double left = el / pct * (100 - pct);
                    _status.Text = Math.Round(pct) + "%  -  about " + (left < 90 ? Math.Round(left) + " s" : Math.Round(left / 60) + " min") + " left (rough estimate)";
                }
                else _status.Text = Math.Round(pct) + "%";
            }
            int total; string[] lines = j.SnapshotLines(_logShown, out total);
            if (lines.Length > 0)
            {
                _log.AppendText(string.Join("\r\n", lines) + "\r\n");
                _log.ScrollToEnd();
                _logShown = total;
            }
        }

        void StartScan(List<CleanItem> list)
        {
            if (_busy) return;
            _job = new CleanJob(); _logShown = 0; _log.Clear(); _sumHost.Children.Clear();
            _runList = list; _runIdx = 0; _t0 = DateTime.Now;
            _total.Text = "Scanning..."; _bar.Value = 0;
            SetBusy(true);
            CleanJob job = _job;
            UK.Bg(delegate
            {
                bool cancelled = false;
                try
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        _runIdx = i;
                        CleanItem c = list[i];
                        job.Operation = "Scanning: " + c.Title;
                        long s = -1;
                        try { s = c.Scan(job); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { job.Fail("Scan " + c.Title, ex); }
                        c.Size = s;
                        CleanItem cc = c;
                        UK.UI(delegate { cc.SizeText.Text = cc.Size < 0 ? "" : Util.Bytes(cc.Size); });
                        job.Note("Scanned " + c.Title + ": " + (s < 0 ? "size is measured while cleaning" : Util.Bytes(s)));
                    }
                    _runIdx = list.Count;
                }
                catch (OperationCanceledException) { cancelled = true; job.Note("Scan cancelled"); }
                return cancelled;
            }, delegate(object res)
            {
                bool cancelled = res is bool && (bool)res;
                SetBusy(false); Refresh();
                if (!cancelled) _bar.Value = 100;
                if (list.Count == _items.Count && !cancelled) { _scannedOnce = true; _lastScan = DateTime.Now; }
                _total.Text = cancelled ? "Scan cancelled" : "Scan finished";
                UpdateTotal();
                if (cancelled) _status.Text = "Scan cancelled. Sizes found so far are shown.";
                else if (_job.Errors > 0) _status.Text = _job.Errors + " item(s) could not be scanned. See the log.";
            });
        }

        void StartClean(List<CleanItem> todo)
        {
            if (_busy) return;
            if (todo.Count == 0) { _status.Text = "Nothing selected."; return; }
            string list = string.Join("\n  ", todo.Select(i => i.Title + (i.Size > 0 ? "  (" + Util.Bytes(i.Size) + ")" : "")).ToArray());
            string extra = "";
            if (todo.Any(i => i.Id == "events")) extra += "\n\nThis includes the event logs, which erases your crash history.";
            if (todo.Any(i => i.Id == "recycle")) extra += "\n\nThis permanently empties the Recycle Bin.";
            if (todo.Any(i => i.Shader)) extra += "\n\nShader caches are included. Games will rebuild them and may stutter at first. This is meant for troubleshooting, not routine cleaning.";
            string game = todo.Any(i => i.Shader) ? CleanKit.RunningGame() : null;
            if (game != null) extra += "\n\n" + game + " is running, so shader caches will be skipped.";
            if (Dlg.Show("Clean these now?\n\n  " + list + extra, Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            _job = new CleanJob(); _logShown = 0; _log.Clear(); _sumHost.Children.Clear();
            _runList = todo; _runIdx = 0; _t0 = DateTime.Now;
            _total.Text = "Cleaning..."; _bar.Value = 0;
            SetBusy(true);
            CleanJob job = _job;
            UK.Bg(delegate
            {
                bool cancelled = false;
                try
                {
                    for (int i = 0; i < todo.Count; i++)
                    {
                        _runIdx = i;
                        job.Check();
                        CleanKit.RunItem(todo[i], job);
                    }
                    _runIdx = todo.Count;
                }
                catch (OperationCanceledException) { cancelled = true; }
                return cancelled;
            }, delegate(object res)
            {
                bool cancelled = res is bool && (bool)res;
                SetBusy(false); Refresh();
                if (!cancelled) _bar.Value = 100;
                _total.Text = (cancelled ? "Cancelled. Freed " : "Freed ") + Util.Bytes(_job.Freed);
                _status.Text = (cancelled ? "Stopped" : "Done") + " at " + DateTime.Now.ToString("HH:mm") + ".";
                if (CleanKit.LastTrim.Length > 0 && todo.Any(i => i.Id == "trim")) _status.Text += "  TRIM: " + CleanKit.LastTrim;
                ShowSummary(todo, cancelled);
                // re-measure only what was cleaned, quietly, so the summary stays on screen
                UK.Bg(delegate
                {
                    CleanJob quiet = new CleanJob();
                    foreach (CleanItem c in todo)
                    {
                        long s = -1;
                        try { s = c.Scan(quiet); } catch { }
                        c.Size = s;
                        CleanItem cc = c;
                        UK.UI(delegate { cc.SizeText.Text = cc.Size < 0 ? "" : Util.Bytes(cc.Size); });
                    }
                    return null;
                }, null);
            });
        }

        void ShowSummary(List<CleanItem> todo, bool cancelled)
        {
            _sumHost.Children.Clear();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Items processed: " + Math.Min(_runIdx, todo.Count) + " of " + todo.Count);
            sb.AppendLine("Files removed: " + _job.Cleaned.ToString("N0") + "    Space recovered: " + Util.Bytes(_job.Freed));
            sb.AppendLine("Skipped (in use or protected): " + _job.Skipped + "    Errors: " + _job.Errors);
            if (_job.Skipped > 0) sb.AppendLine("Skipped files are normal: Windows and running apps keep some files open. The first 25 are listed in the log.");
            if (_job.ErrorLines.Count > 0) { sb.AppendLine("Errors:"); foreach (string e in _job.ErrorLines.Take(12)) sb.AppendLine("  - " + e); }
            TextBlock t = UK.T(sb.ToString().TrimEnd(), "Body");
            _sumHost.Children.Add(UK.Section(cancelled ? "Summary (cancelled)" : "Summary", _job.Errors == 0 ? "Finished without errors" : _job.Errors + " error(s)", t, true, null));
        }

        void SetSchedule(string mode)
        {
            _schedMsg.Text = "Applying...";
            UK.Bg(delegate { string err = CleanKit.SetSchedule(mode); return err ?? ("ok:" + mode); }, delegate(object o)
            {
                string s = (string)o;
                if (s != null && s.StartsWith("ok:")) MarkSchedule(s.Substring(3));
                else _schedMsg.Text = "Could not change the schedule: " + s;
            });
        }
        void MarkSchedule(string mode)
        {
            foreach (KeyValuePair<string, Button> kv in _sched) kv.Value.Style = UK.S(kv.Key == mode ? "PrimaryBtn" : "GhostBtn");
            _schedMsg.Text = mode == "Off" ? "Scheduled cleanup is off." : "Scheduled: " + mode.ToLowerInvariant() + " at 12:00. It runs when the PC is on.";
        }
    }
}
