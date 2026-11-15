// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace S7G.mbpm.Interop
{
    internal static class NativeMethods
    {
        // ---- Console ----

        // HandlerRoutine: BOOL WINAPI HandlerRoutine(DWORD dwCtrlType). Return true when handled.
        internal delegate bool ConsoleCtrlHandler(uint ctrlType);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);

        internal const int STD_OUTPUT_HANDLE = -11;
        internal const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GetStdHandle(int handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetConsoleMode(IntPtr handle, out uint mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetConsoleMode(IntPtr handle, uint mode);

        // ---- Memory ----

        [StructLayout(LayoutKind.Sequential)]
        internal sealed class MEMORYSTATUSEX
        {
            public uint dwLength = 64;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);

        // ---- Console font (classic console window only; Windows Terminal ignores these) ----

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct CONSOLE_FONT_INFOEX
        {
            public uint cbSize;
            public uint nFont;
            public short dwFontSizeX;       // COORD dwFontSize
            public short dwFontSizeY;
            public int FontFamily;
            public int FontWeight;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string FaceName;

            public static CONSOLE_FONT_INFOEX Create()
            {
                var font = new CONSOLE_FONT_INFOEX();
                font.cbSize = (uint)Marshal.SizeOf(typeof(CONSOLE_FONT_INFOEX));
                return font;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetCurrentConsoleFontEx(IntPtr output, bool maximumWindow, ref CONSOLE_FONT_INFOEX font);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetCurrentConsoleFontEx(IntPtr output, bool maximumWindow, ref CONSOLE_FONT_INFOEX font);

        // ---- Own CPU time (no allocation, unlike Process.GetCurrentProcess()) ----

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        /// <summary>This process's kernel + user CPU time, in 100 ns units; 0 if unavailable.</summary>
        internal static long OwnCpuTime()
        {
            long creation, exit, kernel, user;
            return GetProcessTimes(GetCurrentProcess(), out creation, out exit, out kernel, out user) ? kernel + user : 0;
        }

        // ---- Process owner (the account a worker process runs as) ----

        internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        internal const uint TOKEN_QUERY = 0x0008;
        internal const int TokenUser = 1;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

        [DllImport("advapi32.dll", EntryPoint = "LookupAccountSidW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool LookupAccountSid(string systemName, IntPtr sid, StringBuilder name, ref int nameLength,
            StringBuilder domain, ref int domainLength, out int use);

        // ---- PDH (pdh.dll) ----

        internal const uint ERROR_SUCCESS = 0;
        internal const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
        internal const uint PDH_CSTATUS_NEW_DATA = 0x00000001;
        internal const uint PDH_MORE_DATA = 0x800007D2;
        internal const uint PDH_NO_DATA = 0x800007D5;

        internal const uint PDH_FMT_DOUBLE = 0x00000200;
        internal const uint PDH_FMT_NOCAP100 = 0x00008000;   // per-process % Processor Time can exceed 100

        [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
        internal static extern uint PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

        // English counter names work whatever the target's display language is.
        [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
        internal static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        internal static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
        internal static extern uint PdhGetFormattedCounterArray(
            IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

        [DllImport("pdh.dll")]
        internal static extern uint PdhGetFormattedCounterValue(
            IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

        [DllImport("pdh.dll")]
        internal static extern uint PdhCloseQuery(IntPtr query);

        [StructLayout(LayoutKind.Sequential)]
        internal struct PDH_FMT_COUNTERVALUE
        {
            public uint CStatus;
            public double DoubleValue;      // union in C; only the double member is requested here
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PDH_FMT_COUNTERVALUE_ITEM
        {
            public IntPtr Name;             // LPWSTR, owned by the item buffer
            public PDH_FMT_COUNTERVALUE FmtValue;
        }

        // ---- Error text for PDH status codes ----

        private const uint FORMAT_MESSAGE_FROM_HMODULE = 0x00000800;
        private const uint FORMAT_MESSAGE_FROM_SYSTEM = 0x00001000;
        private const uint FORMAT_MESSAGE_IGNORE_INSERTS = 0x00000200;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint FormatMessage(uint flags, IntPtr source, uint messageId, uint languageId,
            StringBuilder buffer, uint size, IntPtr arguments);

        internal static string DescribePdhStatus(uint status)
        {
            var text = new StringBuilder(512);
            uint flags = FORMAT_MESSAGE_FROM_HMODULE | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS;
            uint length = FormatMessage(flags, GetModuleHandle("pdh.dll"), status, 0, text, (uint)text.Capacity, IntPtr.Zero);
            string message = length > 0 ? text.ToString().Trim() : "unknown PDH status";
            return string.Format("0x{0:X8}: {1}", status, message);
        }
    }
}
