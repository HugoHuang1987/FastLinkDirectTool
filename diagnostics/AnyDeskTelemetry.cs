using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace FastLinkDiagnostics
{
    // Read-only native calls. No packet capture, sockets, routes or EStats writes.
    public static class NativeTelemetry
    {
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [StructLayout(LayoutKind.Sequential)] class MemoryStatus
        {
            public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            public uint Load;
            public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);
        public static long[] SystemTimes() { long i, k, u; return GetSystemTimes(out i, out k, out u) ? new[] { i, k, u } : null; }
        public static long[] Memory() { MemoryStatus s = new MemoryStatus(); return GlobalMemoryStatusEx(s) ? new[] { (long)s.Load, (long)(s.AvailablePhysical / 1048576) } : null; }
        public static double? CpuPercent(long[] before, long[] after)
        {
            if (before == null || after == null) return null;
            long total = after[1] + after[2] - before[1] - before[2];
            long idle = after[0] - before[0];
            if (total <= 0 || idle < 0) return null;
            return Math.Round(Math.Max(0, Math.Min(100, 100.0 * (total - idle) / total)), 2);
        }
        [StructLayout(LayoutKind.Sequential)] struct Ssid { public uint Length; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Value; }
        [StructLayout(LayoutKind.Sequential)] struct Association
        {
            public Ssid Ssid; public int BssType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
            public int PhyType; public uint PhyIndex, Signal, RxRate, TxRate;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct Connection
        {
            public int State, Mode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Profile;
            public Association Association;
            public int SecurityEnabled, OneXEnabled, Authentication, Cipher;
        }
        [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] static extern uint WlanQueryInterface(IntPtr handle, ref Guid id, int opcode, IntPtr reserved, out int size, out IntPtr data, out int kind);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr data);
        [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
        public static uint[] Wifi(string id)
        {
            Guid guid; if (!Guid.TryParse(id, out guid)) return null;
            IntPtr handle = IntPtr.Zero, data = IntPtr.Zero;
            try
            {
                uint negotiated; int size, kind;
                if (WlanOpenHandle(2, IntPtr.Zero, out negotiated, out handle) != 0) return null;
                if (WlanQueryInterface(handle, ref guid, 7, IntPtr.Zero, out size, out data, out kind) != 0 ||
                    data == IntPtr.Zero || size < Marshal.SizeOf(typeof(Connection))) return null;
                Connection c = (Connection)Marshal.PtrToStructure(data, typeof(Connection));
                // Do not return SSID, MAC, profile or other identity fields.
                return c.State == 1 ? new[] { c.Association.Signal, c.Association.RxRate, c.Association.TxRate } : null;
            }
            finally { if (data != IntPtr.Zero) WlanFreeMemory(data); if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero); }
        }
        [StructLayout(LayoutKind.Sequential)] struct Tcp4 { public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, Pid; }
        [StructLayout(LayoutKind.Sequential)] struct Tcp6
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
            public uint LocalScope, LocalPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddress;
            public uint RemoteScope, RemotePort, State, Pid;
        }
        [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
        public sealed class Endpoint { public int Pid, State, LocalPort, RemotePort; public string LocalAddress, RemoteAddress; }
        public sealed class TcpResult { public uint IPv4Status, IPv6Status; public Endpoint[] Connections; }
        public static int Port(uint value) { return (int)(((value & 255) << 8) | ((value >> 8) & 255)); }
        public static TcpResult Tcp(int[] pids)
        {
            HashSet<int> wanted = new HashSet<int>(pids);
            List<Endpoint> rows = new List<Endpoint>();
            TcpResult result = new TcpResult();
            foreach (int family in new[] { 2, 23 })
            {
                int size = 0; uint status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 5, 0);
                IntPtr memory = IntPtr.Zero;
                try
                {
                    if (size > 0 && size <= 4194304)
                    {
                        memory = Marshal.AllocHGlobal(size);
                        status = GetExtendedTcpTable(memory, ref size, false, family, 5, 0);
                        if (status == 0)
                        {
                            int count = Marshal.ReadInt32(memory);
                            int width = Marshal.SizeOf(family == 2 ? typeof(Tcp4) : typeof(Tcp6));
                            count = Math.Min(count, (size - 4) / width);
                            for (int n = 0; n < count; n++)
                            {
                                IntPtr at = IntPtr.Add(memory, 4 + n * width);
                                Endpoint e;
                                if (family == 2)
                                {
                                    Tcp4 r = (Tcp4)Marshal.PtrToStructure(at, typeof(Tcp4));
                                    if (!wanted.Contains((int)r.Pid) || (r.State != 3 && r.State != 5 && r.State != 8)) continue;
                                    e = new Endpoint { Pid = (int)r.Pid, State = (int)r.State, LocalAddress = new IPAddress(r.LocalAddress).ToString(), RemoteAddress = new IPAddress(r.RemoteAddress).ToString(), LocalPort = Port(r.LocalPort), RemotePort = Port(r.RemotePort) };
                                }
                                else
                                {
                                    Tcp6 r = (Tcp6)Marshal.PtrToStructure(at, typeof(Tcp6));
                                    if (!wanted.Contains((int)r.Pid) || (r.State != 3 && r.State != 5 && r.State != 8)) continue;
                                    e = new Endpoint { Pid = (int)r.Pid, State = (int)r.State, LocalAddress = new IPAddress(r.LocalAddress, r.LocalScope).ToString(), RemoteAddress = new IPAddress(r.RemoteAddress, r.RemoteScope).ToString(), LocalPort = Port(r.LocalPort), RemotePort = Port(r.RemotePort) };
                                }
                                if (rows.Count < 32) rows.Add(e);
                            }
                        }
                    }
                }
                finally { if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory); }
                if (family == 2) result.IPv4Status = status; else result.IPv6Status = status;
            }
            result.Connections = rows.ToArray(); return result;
        }
    }

    public sealed class TraceTail
    {
        public long Offset { get; private set; }
        public bool Reset { get; private set; }
        public int BytesRead { get; private set; }
        public bool Backlog { get; private set; }
        private DateTime created;
        private string pending = "";
        private bool initialized;
        public string[] Read(string path)
        {
            Reset = false; BytesRead = 0; Backlog = false;
            if (!File.Exists(path)) return new string[0];
            using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                DateTime birth = File.GetCreationTimeUtc(path);
                bool boot = !initialized || birth != created || f.Length < Offset;
                if (boot)
                {
                    Reset = true; initialized = true; created = birth; pending = "";
                    Offset = Math.Max(0, f.Length - 1048576);
                }
                bool discardPartial = boot && Offset > 0;
                f.Position = Offset;
                int size = (int)Math.Min(f.Length - Offset, boot ? 1048576 : 262144);
                byte[] buffer = new byte[size];
                BytesRead = f.Read(buffer, 0, size); Offset += BytesRead;
                Backlog = Offset < f.Length;
                string text = pending + Encoding.UTF8.GetString(buffer, 0, BytesRead);
                string[] lines = text.Split('\n');
                pending = lines[lines.Length - 1];
                if (pending.Length > 8192) pending = "";
                List<string> complete = new List<string>();
                for (int n = discardPartial ? 1 : 0; n < lines.Length - 1; n++)
                    if (lines[n].Length <= 8192) complete.Add(lines[n].TrimEnd('\r'));
                return complete.ToArray();
            }
        }
    }
}
