using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FastLinkDirectTool
{
    // Detection only. Never starts/stops a process or changes proxy/network state.
    internal sealed class FastLinkConnectionInfo
    {
        public bool Active { get; private set; }
        public string Mode { get; private set; }
        public int GuiPid { get; private set; }
        public int CorePid { get; private set; }
        public string CoreName { get; private set; }
        public string ProxyServerIdentity { get; private set; }
        public string Reason { get; private set; }

        internal FastLinkConnectionInfo(bool active, int guiPid, int corePid, string coreName, string identity, string reason)
        {
            Active = active;
            Mode = "system_proxy";
            GuiPid = guiPid;
            CorePid = corePid;
            CoreName = active ? coreName : string.Empty;
            ProxyServerIdentity = identity ?? string.Empty;
            Reason = reason;
        }
    }

    internal sealed class LoopbackProxyEndpoint
    {
        public string Scheme { get; private set; }
        public string Host { get; private set; }
        public IPAddress Address { get; private set; }
        public int Port { get; private set; }
        public bool IsLocalhost { get { return Address == null; } }
        public string CanonicalTarget
        {
            get
            {
                return (Address != null && Address.AddressFamily == AddressFamily.InterNetworkV6
                    ? "[" + Host + "]" : Host) + ":" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        internal LoopbackProxyEndpoint(string scheme, string host, IPAddress address, int port)
        {
            Scheme = scheme;
            Host = host;
            Address = address;
            Port = port;
        }
    }

    // Small, injectable snapshots keep identity and ownership policy unit-testable.
    internal sealed class CompatibilityProcessInfo
    {
        public int Pid;
        public string Name;
        public string ExecutablePath;
    }

    internal sealed class CompatibilityTcpListener
    {
        public int OwnerPid;
        public IPAddress Address;
        public int Port;
    }

    internal static class FastLinkConnectionCompatibility
    {
        private static readonly object CacheLock = new object();
        private static readonly Stopwatch CacheClock = Stopwatch.StartNew();
        private static FastLinkConnectionInfo cached;
        private static long cachedAt = -2000;
        private static bool cachedLegacyRecoveryAllowed;
        private static long cachedLegacyRecoveryAt = -2000;

        public static FastLinkConnectionInfo GetCurrent()
        {
            lock (CacheLock)
            {
                long now = CacheClock.ElapsedMilliseconds;
                if (cached != null && now - cachedAt < 2000) return cached;
                cached = Capture();
                cachedAt = CacheClock.ElapsedMilliseconds;
                return cached;
            }
        }

        // This grants only process-identity eligibility. Callers must additionally
        // require a live legacy TUN, explicit user opt-in, and existing safety gates.
        // An inactive system proxy is never evidence that legacy recovery is safe.
        public static bool CanUseLegacyRecovery()
        {
            lock (CacheLock)
            {
                long now = CacheClock.ElapsedMilliseconds;
                if (now - cachedLegacyRecoveryAt < 2000) return cachedLegacyRecoveryAllowed;
                List<CompatibilityProcessInfo> processes = new List<CompatibilityProcessInfo>();
                bool complete = true;
                foreach (string name in new[] { "Fastlink", "FastLinkCore", "ApexCore" })
                {
                    try
                    {
                        foreach (Process process in Process.GetProcessesByName(name))
                        {
                            using (process)
                            {
                                try
                                {
                                    processes.Add(new CompatibilityProcessInfo
                                    {
                                        Pid = process.Id,
                                        Name = process.ProcessName,
                                        ExecutablePath = process.MainModule.FileName
                                    });
                                }
                                catch { complete = false; }
                            }
                        }
                    }
                    catch { complete = false; }
                }
                cachedLegacyRecoveryAllowed = complete && CanUseLegacyRecovery(processes);
                cachedLegacyRecoveryAt = CacheClock.ElapsedMilliseconds;
                return cachedLegacyRecoveryAllowed;
            }
        }

        internal static bool CanUseLegacyRecovery(IEnumerable<CompatibilityProcessInfo> processSnapshot)
        {
            if (processSnapshot == null) return false;
            List<CompatibilityProcessInfo> processes = processSnapshot.ToList();
            if (processes.Any(p => p == null)) return false;
            // Legacy recovery stops processes by name. Multiple clients/cores or
            // ANY observed ApexCore are therefore ambiguous, even across folders.
            List<CompatibilityProcessInfo> guis = processes.Where(p =>
                string.Equals(p.Name, "Fastlink", StringComparison.OrdinalIgnoreCase)).ToList();
            List<CompatibilityProcessInfo> legacy = processes.Where(p =>
                string.Equals(p.Name, "FastLinkCore", StringComparison.OrdinalIgnoreCase)).ToList();
            if (processes.Any(p => string.Equals(p.Name, "ApexCore", StringComparison.OrdinalIgnoreCase))) return false;
            if (guis.Count != 1 || legacy.Count != 1) return false;
            return IsNamedExecutable(guis[0], "Fastlink") && IsNamedExecutable(legacy[0], "FastLinkCore") &&
                guis[0].Pid != legacy[0].Pid && SameDirectory(guis[0].ExecutablePath, legacy[0].ExecutablePath);
        }

        private static FastLinkConnectionInfo Inactive(string reason)
        {
            return new FastLinkConnectionInfo(false, 0, 0, string.Empty, string.Empty, reason);
        }

        private static FastLinkConnectionInfo Capture()
        {
            try
            {
                bool enabled = false;
                string server = null;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("ProxyEnable", 0);
                        enabled = value is int && (int)value == 1;
                        server = key.GetValue("ProxyServer", null) as string;
                    }
                }
                if (!enabled) return Inactive("proxy_disabled");
                List<LoopbackProxyEndpoint> endpoints;
                if (!TryParseProxyServer(server, out endpoints)) return Inactive("unsupported_proxy");

                List<CompatibilityProcessInfo> processes = new List<CompatibilityProcessInfo>();
                ReadProcesses("Fastlink", processes);
                ReadProcesses("ApexCore", processes);
                ReadProcesses("FastLinkCore", processes);
                if (!processes.Any(p => IsNamedExecutable(p, "Fastlink"))) return Inactive("gui_not_verified");
                if (!processes.Any(IsSupportedCore)) return Inactive("core_not_verified");

                List<CompatibilityTcpListener> listeners = new List<CompatibilityTcpListener>();
                bool ipv4 = TryReadListenerTable(2, listeners);
                bool ipv6 = TryReadListenerTable(23, listeners);
                if (!ipv4 && !ipv6) return Inactive("listener_table_unavailable");
                return Evaluate(enabled, server, processes, listeners);
            }
            catch
            {
                // Do not expose registry values, installation paths or exception text.
                return Inactive("snapshot_unavailable");
            }
        }

        private static void ReadProcesses(string name, List<CompatibilityProcessInfo> result)
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        result.Add(new CompatibilityProcessInfo
                        {
                            Pid = process.Id,
                            Name = process.ProcessName,
                            ExecutablePath = process.MainModule.FileName
                        });
                    }
                    catch { /* Exited or inaccessible processes are not positive evidence. */ }
                }
            }
        }

        internal static FastLinkConnectionInfo Evaluate(bool proxyEnabled, string proxyServer,
            IEnumerable<CompatibilityProcessInfo> processSnapshot,
            IEnumerable<CompatibilityTcpListener> listenerSnapshot)
        {
            if (!proxyEnabled) return Inactive("proxy_disabled");
            List<LoopbackProxyEndpoint> endpoints;
            if (!TryParseProxyServer(proxyServer, out endpoints)) return Inactive("unsupported_proxy");
            List<CompatibilityProcessInfo> processes = (processSnapshot ?? new CompatibilityProcessInfo[0]).Where(p => p != null).ToList();
            List<CompatibilityTcpListener> listeners = (listenerSnapshot ?? new CompatibilityTcpListener[0]).Where(p => p != null).ToList();
            List<CompatibilityProcessInfo> guis = processes.Where(p => IsNamedExecutable(p, "Fastlink")).ToList();
            if (guis.Count == 0) return Inactive("gui_not_verified");
            List<CompatibilityProcessInfo> cores = processes.Where(IsSupportedCore).ToList();
            if (cores.Count == 0) return Inactive("core_not_verified");

            foreach (CompatibilityProcessInfo gui in guis)
            {
                foreach (CompatibilityProcessInfo core in cores)
                {
                    if (gui.Pid == core.Pid || !SameDirectory(gui.ExecutablePath, core.ExecutablePath)) continue;
                    bool allOwned = endpoints.All(endpoint => listeners.Any(listener =>
                        listener.OwnerPid == core.Pid && listener.Port == endpoint.Port &&
                        listener.Address != null && IPAddress.IsLoopback(listener.Address) &&
                        (endpoint.IsLocalhost || listener.Address.Equals(endpoint.Address))));
                    if (!allOwned) continue;
                    string identity = string.Join(";", endpoints.OrderBy(p => p.Scheme, StringComparer.Ordinal)
                        .Select(p => p.Scheme + "=" + p.CanonicalTarget).ToArray());
                    string coreName = IsNamedExecutable(core, "ApexCore") ? "ApexCore" : "FastLinkCore";
                    return new FastLinkConnectionInfo(true, gui.Pid, core.Pid, coreName, identity, "verified_loopback_owner");
                }
            }
            return Inactive("core_directory_or_listener_mismatch");
        }

        private static bool IsSupportedCore(CompatibilityProcessInfo process)
        {
            return IsNamedExecutable(process, "ApexCore") || IsNamedExecutable(process, "FastLinkCore");
        }

        private static bool IsNamedExecutable(CompatibilityProcessInfo process, string name)
        {
            if (process == null || process.Pid <= 0 ||
                !string.Equals(process.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(process.ExecutablePath)) return false;
            try
            {
                return Path.IsPathRooted(process.ExecutablePath) &&
                    string.Equals(Path.GetFileName(process.ExecutablePath), name + ".exe", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool SameDirectory(string first, string second)
        {
            try
            {
                string a = Path.GetDirectoryName(Path.GetFullPath(first));
                string b = Path.GetDirectoryName(Path.GetFullPath(second));
                return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        internal static bool TryParseProxyServer(string value, out List<LoopbackProxyEndpoint> endpoints)
        {
            endpoints = new List<LoopbackProxyEndpoint>();
            if (string.IsNullOrWhiteSpace(value) || value.Length > 2048) return false;
            string text = value.Trim();
            if (text.IndexOf('=') < 0)
            {
                LoopbackProxyEndpoint http;
                LoopbackProxyEndpoint https;
                if (!TryParseEndpoint("http", text, out http) || !TryParseEndpoint("https", text, out https)) return false;
                endpoints.Add(http);
                endpoints.Add(https);
                return true;
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<LoopbackProxyEndpoint> parsed = new List<LoopbackProxyEndpoint>();
            foreach (string part in text.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                int equals = part.IndexOf('=');
                if (equals <= 0 || equals != part.LastIndexOf('=')) return false;
                string scheme = part.Substring(0, equals).Trim().ToLowerInvariant();
                if (!seen.Add(scheme)) return false;
                // Windows supports these four mapping keys. Unknown keys are ambiguous.
                if (scheme != "http" && scheme != "https" && scheme != "ftp" && scheme != "socks") return false;
                LoopbackProxyEndpoint endpoint;
                if (!TryParseEndpoint(scheme, part.Substring(equals + 1).Trim(), out endpoint)) return false;
                parsed.Add(endpoint);
            }
            if (!parsed.Any(p => p.Scheme == "http" || p.Scheme == "https")) return false;
            endpoints = parsed;
            return true;
        }

        private static bool TryParseEndpoint(string scheme, string value, out LoopbackProxyEndpoint endpoint)
        {
            endpoint = null;
            if (string.IsNullOrEmpty(value) || value.IndexOfAny(new[] { '@', '/', '\\', '?', '#', ';', '=' }) >= 0) return false;
            string host;
            string portText;
            bool bracketed = value[0] == '[';
            if (bracketed)
            {
                int close = value.IndexOf(']');
                if (close <= 1 || close + 1 >= value.Length || value[close + 1] != ':') return false;
                host = value.Substring(1, close - 1);
                portText = value.Substring(close + 2);
            }
            else
            {
                int colon = value.IndexOf(':');
                if (colon <= 0 || colon != value.LastIndexOf(':')) return false;
                host = value.Substring(0, colon);
                portText = value.Substring(colon + 1);
            }
            int port;
            if (portText.Length == 0 || portText.Any(c => c < '0' || c > '9') ||
                !int.TryParse(portText, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535) return false;
            if (!bracketed && string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = new LoopbackProxyEndpoint(scheme, "localhost", null, port);
                return true;
            }
            IPAddress address;
            if (!IPAddress.TryParse(host, out address) || !IPAddress.IsLoopback(address)) return false;
            if (bracketed)
            {
                if (address.AddressFamily != AddressFamily.InterNetworkV6 || host.IndexOf('%') >= 0) return false;
            }
            else
            {
                if (address.AddressFamily != AddressFamily.InterNetwork || host != address.ToString()) return false;
            }
            endpoint = new LoopbackProxyEndpoint(scheme, address.ToString(), address, port);
            return true;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

        private static bool TryReadListenerTable(int family, List<CompatibilityTcpListener> output)
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int size = 0;
                uint status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
                if (status != 122 && status != 0) return false;
                // TCP_TABLE_OWNER_PID_LISTENER is bounded and avoids enumerating traffic.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (size < 4 || size > 8 * 1024 * 1024) return false;
                    buffer = Marshal.AllocHGlobal(size);
                    int capacity = size;
                    status = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
                    if (status == 122)
                    {
                        Marshal.FreeHGlobal(buffer);
                        buffer = IntPtr.Zero;
                        continue;
                    }
                    if (status != 0) return false;
                    int count = Marshal.ReadInt32(buffer);
                    int stride = family == 2 ? 24 : 56;
                    if (count < 0 || count > (capacity - 4) / stride) return false;
                    for (int i = 0; i < count; i++)
                    {
                        int row = 4 + i * stride;
                        int stateOffset = family == 2 ? 0 : 48;
                        if (Marshal.ReadInt32(buffer, row + stateOffset) != 2) continue;
                        int addressOffset = family == 2 ? 4 : 0;
                        int portOffset = family == 2 ? 8 : 20;
                        int pidOffset = family == 2 ? 20 : 52;
                        byte[] addressBytes = new byte[family == 2 ? 4 : 16];
                        Marshal.Copy(IntPtr.Add(buffer, row + addressOffset), addressBytes, 0, addressBytes.Length);
                        IPAddress address = new IPAddress(addressBytes);
                        if (!IPAddress.IsLoopback(address)) continue;
                        int port = (Marshal.ReadByte(buffer, row + portOffset) << 8) | Marshal.ReadByte(buffer, row + portOffset + 1);
                        int pid = Marshal.ReadInt32(buffer, row + pidOffset);
                        if (pid <= 0 || port == 0) continue;
                        output.Add(new CompatibilityTcpListener { OwnerPid = pid, Address = address, Port = port });
                    }
                    return true;
                }
                return false;
            }
            catch { return false; }
            finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
        }
    }
}
