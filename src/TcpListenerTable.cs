// Pi Web Tray — native TCP listener table (owner PID) lookup.

using System;
using System.Runtime.InteropServices;

namespace PiWebTray
{
    internal static class TcpListenerTable
    {
        private const int AfInet = 2;
        private const int AfInet6 = 23;
        private const int TcpTableOwnerPidListener = 3;
        private const uint ErrorInsufficientBuffer = 122;

        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcpRowOwnerPid
        {
            public uint State;
            public uint LocalAddress;
            public uint LocalPort;
            public uint RemoteAddress;
            public uint RemotePort;
            public uint OwningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcp6RowOwnerPid
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] LocalAddress;
            public uint LocalScopeId;
            public uint LocalPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] RemoteAddress;
            public uint RemoteScopeId;
            public uint RemotePort;
            public uint State;
            public uint OwningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
            bool order, int addressFamily, int tableClass, uint reserved);

        internal static bool IsListening(int port)
        {
            int pid;
            return Find(port, out pid);
        }

        internal static int ListenerPid(int port)
        {
            int pid;
            return Find(port, out pid) ? pid : 0;
        }

        // A listener can have PID 0 (kernel-owned), so a found row must be reported
        // separately from a non-zero PID. Prefer an IPv4/IPv6 row with a real PID.
        private static bool Find(int port, out int pid)
        {
            pid = 0;
            if (port <= 0) return false;

            bool found = false;
            int candidate;
            if (FindV4(port, out candidate))
            {
                found = true;
                if (candidate > 0) { pid = candidate; return true; }
            }
            if (FindV6(port, out candidate))
            {
                found = true;
                if (candidate > 0) { pid = candidate; return true; }
            }
            return found;
        }

        private static bool FindV4(int port, out int pid)
        {
            pid = 0;
            int size = 0;
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet,
                TcpTableOwnerPidListener, 0);
            if (result != 0 && result != ErrorInsufficientBuffer) return false;
            if (size <= 0) return false;

            IntPtr table = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(table, ref size, false, AfInet,
                    TcpTableOwnerPidListener, 0);
                if (result != 0) return false;

                int count = Marshal.ReadInt32(table);
                int rowSize = Marshal.SizeOf(typeof(MibTcpRowOwnerPid));
                if (count <= 0 || 4L + (long)count * rowSize > size) return false;

                for (int i = 0; i < count; i++)
                {
                    IntPtr row = new IntPtr(table.ToInt64() + 4 + (long)i * rowSize);
                    MibTcpRowOwnerPid entry = (MibTcpRowOwnerPid)Marshal.PtrToStructure(
                        row, typeof(MibTcpRowOwnerPid));
                    if (NetworkPort(entry.LocalPort) == port)
                    {
                        pid = (int)entry.OwningPid;
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }
        }

        private static bool FindV6(int port, out int pid)
        {
            pid = 0;
            int size = 0;
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet6,
                TcpTableOwnerPidListener, 0);
            if (result != 0 && result != ErrorInsufficientBuffer) return false;
            if (size <= 0) return false;

            IntPtr table = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(table, ref size, false, AfInet6,
                    TcpTableOwnerPidListener, 0);
                if (result != 0) return false;

                int count = Marshal.ReadInt32(table);
                int rowSize = Marshal.SizeOf(typeof(MibTcp6RowOwnerPid));
                if (count <= 0 || 4L + (long)count * rowSize > size) return false;

                for (int i = 0; i < count; i++)
                {
                    IntPtr row = new IntPtr(table.ToInt64() + 4 + (long)i * rowSize);
                    MibTcp6RowOwnerPid entry = (MibTcp6RowOwnerPid)Marshal.PtrToStructure(
                        row, typeof(MibTcp6RowOwnerPid));
                    if (NetworkPort(entry.LocalPort) == port)
                    {
                        pid = (int)entry.OwningPid;
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }
        }

        private static int NetworkPort(uint value)
        {
            return (int)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));
        }
    }
}
