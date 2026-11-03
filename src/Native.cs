using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace InfernoXC
{
    public static class Native
    {
        [DllImport("ntdll.dll")]
        static extern int NtSetSystemInformation(int infoClass, IntPtr info, int length);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr tok);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool LookupPrivilegeValue(string sys, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr tok, bool disableAll, ref TOKEN_PRIVILEGES st, int len, IntPtr prev, IntPtr ret);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attr; }

        static bool EnablePrivilege(string name)
        {
            IntPtr tok;
            if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, 0x28, out tok)) return false;
            try
            {
                long luid;
                if (!LookupPrivilegeValue(null, name, out luid)) return false;
                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.Count = 1; tp.Luid = luid; tp.Attr = 2;
                AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                return Marshal.GetLastWin32Error() == 0;
            }
            finally { CloseHandle(tok); }
        }


        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);

        /// <summary>Full path of a running process without loading its module list (fast). Null when it cannot be read.</summary>
        public static string ProcPath(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int n = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>cmd: 2 = empty working sets, 3 = flush modified list, 4 = purge standby, 5 = purge low-priority standby.</summary>
        public static int MemoryCommand(int cmd)
        {
            EnablePrivilege("SeProfileSingleProcessPrivilege");
            IntPtr p = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(p, cmd);
                return NtSetSystemInformation(80, p, 4);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY;
            public int dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);

        public class DisplayMode { public int W, H, Hz; }

        public static DisplayMode CurrentDisplay(out int maxHzAtRes)
        {
            maxHzAtRes = 0;
            DEVMODE dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (!EnumDisplaySettings(null, -1, ref dm)) return null;
            DisplayMode d = new DisplayMode();
            d.W = dm.dmPelsWidth; d.H = dm.dmPelsHeight; d.Hz = dm.dmDisplayFrequency;
            for (int i = 0; i < 400; i++)
            {
                DEVMODE m = new DEVMODE();
                m.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (!EnumDisplaySettings(null, i, ref m)) break;
                if (m.dmPelsWidth == d.W && m.dmPelsHeight == d.H && m.dmDisplayFrequency > maxHzAtRes) maxHzAtRes = m.dmDisplayFrequency;
            }
            return d;
        }
    }
}
