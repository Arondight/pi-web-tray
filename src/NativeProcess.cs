// Pi Web Tray — native process inspection
//
// Reading the command line and parent process through WMI requires the WMI service and
// can be slow or unavailable on locked-down machines. The tray only needs two small
// pieces of information, so use the Win32 process APIs directly:
//
//   * Toolhelp32 for the parent PID;
//   * NtQueryInformationProcess + ReadProcessMemory for the command line (PEB);
//   * QueryFullProcessImageName for a diagnostic executable path.
//
// Everything is best-effort. A protected/elevated process can deny access; the caller
// then falls back to the local HTTP probe.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PiWebTray
{
    internal static class NativeProcess
    {
        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_VM_READ = 0x0010;
        private const int ProcessBasicInformation = 0;
        private const int ProcessWow64Information = 26;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address,
            byte[] buffer, IntPtr size, out IntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process(IntPtr process, out bool wow64);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags,
            StringBuilder name, ref int size);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int informationClass,
            ref PROCESS_BASIC_INFORMATION information, int informationLength, out int returnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int informationClass,
            ref IntPtr information, int informationLength, out int returnLength);

        internal static int ParentPid(int pid)
        {
            if (pid <= 0) return 0;

            IntPtr snapshot = IntPtr.Zero;
            try
            {
                snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return 0;

                PROCESSENTRY32W entry = new PROCESSENTRY32W();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
                if (!Process32FirstW(snapshot, ref entry)) return 0;

                do
                {
                    if (entry.th32ProcessID == (uint)pid)
                        return (int)entry.th32ParentProcessID;
                }
                while (Process32NextW(snapshot, ref entry));
            }
            catch (Exception)
            {
            }
            finally
            {
                if (snapshot != IntPtr.Zero && snapshot != new IntPtr(-1))
                {
                    try { CloseHandle(snapshot); } catch (Exception) { }
                }
            }
            return 0;
        }

        internal static string ImagePath(int pid)
        {
            if (pid <= 0) return "";

            IntPtr process = IntPtr.Zero;
            try
            {
                process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (process == IntPtr.Zero) return "";

                StringBuilder name = new StringBuilder(1024);
                int size = name.Capacity;
                if (!QueryFullProcessImageNameW(process, 0, name, ref size)) return "";
                return name.ToString(0, Math.Max(0, Math.Min(size, name.Length)));
            }
            catch (Exception)
            {
                return "";
            }
            finally
            {
                if (process != IntPtr.Zero)
                {
                    try { CloseHandle(process); } catch (Exception) { }
                }
            }
        }

        internal static string CommandLine(int pid)
        {
            if (pid <= 0) return "";

            IntPtr process = IntPtr.Zero;
            try
            {
                process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
                if (process == IntPtr.Zero) return "";

                bool targetWow64 = false;
                try { IsWow64Process(process, out targetWow64); } catch (Exception) { }

                // A 64-bit target has a 64-bit PEB. A 32-bit tray cannot address it.
                int pointerSize = Environment.Is64BitOperatingSystem && !targetWow64 ? 8 : 4;
                if (pointerSize > IntPtr.Size) return "";

                PROCESS_BASIC_INFORMATION basic = new PROCESS_BASIC_INFORMATION();
                int returned;
                int status = NtQueryInformationProcess(process, ProcessBasicInformation,
                    ref basic, Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)), out returned);
                if (status != 0 || basic.PebBaseAddress == IntPtr.Zero) return "";

                // ProcessBasicInformation returns the native PEB. A WOW64 target has a
                // second 32-bit PEB, and only that one uses 32-bit pointers; ask for it
                // explicitly instead of reading the 64-bit PEB with 32-bit offsets.
                IntPtr peb = basic.PebBaseAddress;
                if (targetWow64 && IntPtr.Size == 8)
                {
                    IntPtr peb32 = IntPtr.Zero;
                    int wowReturned;
                    if (NtQueryInformationProcess(process, ProcessWow64Information,
                            ref peb32, IntPtr.Size, out wowReturned) != 0
                        || peb32 == IntPtr.Zero)
                    {
                        return "";
                    }
                    peb = peb32;
                }

                // Offsets in PEB and RTL_USER_PROCESS_PARAMETERS are stable for the
                // supported Windows versions. The command-line UNICODE_STRING is at
                // +0x70 on 64-bit and +0x40 on 32-bit.
                int parametersOffset = pointerSize == 8 ? 0x20 : 0x10;
                int commandLineOffset = pointerSize == 8 ? 0x70 : 0x40;
                IntPtr parameters = ReadPointer(process, Add(peb, parametersOffset), pointerSize);
                if (parameters == IntPtr.Zero) return "";

                int headerSize = pointerSize == 8 ? 16 : 8;
                byte[] header = ReadBytes(process, Add(parameters, commandLineOffset), headerSize);
                if (header == null || header.Length < headerSize) return "";

                int length = BitConverter.ToUInt16(header, 0);
                if (length <= 0 || length > 32766) return "";

                IntPtr buffer = pointerSize == 8
                    ? new IntPtr(BitConverter.ToInt64(header, 8))
                    : new IntPtr(BitConverter.ToUInt32(header, 4));
                if (buffer == IntPtr.Zero) return "";

                byte[] text = ReadBytes(process, buffer, length);
                if (text == null || text.Length != length) return "";
                return Encoding.Unicode.GetString(text);
            }
            catch (Exception)
            {
                return "";
            }
            finally
            {
                if (process != IntPtr.Zero)
                {
                    try { CloseHandle(process); } catch (Exception) { }
                }
            }
        }

        private static IntPtr Add(IntPtr address, int offset)
        {
            return new IntPtr(address.ToInt64() + offset);
        }

        private static IntPtr ReadPointer(IntPtr process, IntPtr address, int pointerSize)
        {
            byte[] bytes = ReadBytes(process, address, pointerSize);
            if (bytes == null || bytes.Length != pointerSize) return IntPtr.Zero;
            return pointerSize == 8
                ? new IntPtr(BitConverter.ToInt64(bytes, 0))
                : new IntPtr(BitConverter.ToUInt32(bytes, 0));
        }

        private static byte[] ReadBytes(IntPtr process, IntPtr address, int count)
        {
            if (count <= 0) return new byte[0];
            byte[] buffer = new byte[count];
            IntPtr read;
            if (!ReadProcessMemory(process, address, buffer, new IntPtr(count), out read)) return null;
            if (read.ToInt64() != count) return null;
            return buffer;
        }
    }
}
