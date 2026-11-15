// (c) 2017-2026 Sector 7G Consulting LLC
using System;
using System.Runtime.InteropServices;
using System.Text;
using S7G.mbpm.Interop;

namespace S7G.mbpm.Collection
{
    /// <summary>
    /// The account a process runs as (DOMAIN\name, e.g. IIS APPPOOL\DefaultAppPool), read from its
    /// token. Needs elevation for other accounts' processes; null when it can't be read.
    /// </summary>
    internal static class ProcessOwner
    {
        public static string Get(int pid)
        {
            IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero)
            {
                return null;
            }
            IntPtr token = IntPtr.Zero;
            IntPtr info = IntPtr.Zero;
            try
            {
                if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out token))
                {
                    return null;
                }
                int length;
                NativeMethods.GetTokenInformation(token, NativeMethods.TokenUser, IntPtr.Zero, 0, out length);
                if (length <= 0)
                {
                    return null;
                }
                info = Marshal.AllocHGlobal(length);
                if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenUser, info, length, out length))
                {
                    return null;
                }
                IntPtr sid = Marshal.ReadIntPtr(info);      // TOKEN_USER.User.Sid

                var name = new StringBuilder(256);
                var domain = new StringBuilder(256);
                int nameLength = name.Capacity;
                int domainLength = domain.Capacity;
                int use;
                if (!NativeMethods.LookupAccountSid(null, sid, name, ref nameLength, domain, ref domainLength, out use))
                {
                    return null;
                }
                return domain.Length > 0 ? domain + "\\" + name : name.ToString();
            }
            finally
            {
                if (info != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(info);
                }
                if (token != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(token);
                }
                NativeMethods.CloseHandle(process);
            }
        }
    }
}
