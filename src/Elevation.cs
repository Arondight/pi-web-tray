// Pi Web Tray — token elevation / administrator-account detection.

using System;
using System.Runtime.InteropServices;

namespace PiWebTray
{
    internal static class Elevation
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation, int tokenInformationLength, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        internal static bool IsElevated()
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /*TOKEN_QUERY*/, out token)) return true;
                int elevation;
                int size;
                if (!GetTokenInformation(token, 20 /*TokenElevation*/, out elevation, 4, out size)) return true;
                return elevation != 0;
            }
            catch (Exception)
            {
                // Fail closed: callers use this to decide whether child-process
                // environment hardening and elevated-install checks are required.
                // Treating an unqueryable token as standard would skip both.
                return true;
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        // WindowsIdentity.Groups omits Administrators from a UAC-filtered token.
        // TokenElevationType.Limited proves that the account has a linked elevated
        // token, so the account (not just this filtered token) is an administrator.
        internal static bool IsAdministratorAccount()
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /*TOKEN_QUERY*/, out token)) return false;
                int elevationType;
                int size;
                if (!GetTokenInformation(token, 18 /*TokenElevationType*/,
                    out elevationType, 4, out size))
                    return false;
                return elevationType == 2 /*Full*/ || elevationType == 3 /*Limited*/;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }
    }
}
