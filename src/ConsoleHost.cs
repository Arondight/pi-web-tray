// Pi Web Tray — console attachment for a WinExe.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PiWebTray
{
    internal static class ConsoleHost
    {
        private const int AttachParentProcess = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        private static bool _attached;

        // Attach to the parent console when started from cmd/PowerShell; otherwise return false
        internal static bool TryAttach()
        {
            if (_attached) return true;
            try
            {
                if (AttachConsole(AttachParentProcess))
                {
                    _attached = true;
                    // The console defaults to a local code page such as 936, so raw UTF-8 output is mojibake.
                    // Switching to 65001 makes CJK render correctly (the same thing pi itself does on Windows).
                    try { Console.OutputEncoding = new UTF8Encoding(false); }
                    catch (Exception) { }
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        internal static void WriteLine(string text)
        {
            try { Console.Out.WriteLine(text); } catch (Exception) { }
        }

        internal static void WriteError(string text)
        {
            try { Console.Error.WriteLine(text); } catch (Exception) { }
        }
    }
}
