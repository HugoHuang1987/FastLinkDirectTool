using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml;

[assembly: AssemblyVersion("2.6.7.0")]
[assembly: AssemblyFileVersion("2.6.7.0")]

namespace FastLinkDirectTool
{
    internal static class Program
    {
        private const string WindowTitle = "FastLink 直连助手";

        [STAThread]
        private static void Main(string[] args)
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Global\\FastLinkDirectTool.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    NativeMethods.ShowExistingWindow(WindowTitle);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool autoStartLaunch = args.Any(delegate(string value)
                {
                    return string.Equals(value, "--autostart", StringComparison.OrdinalIgnoreCase);
                });

                bool openNetworkMode = args.Any(value => string.Equals(value, "--network-mode", StringComparison.OrdinalIgnoreCase));
                bool prepareNetworkMode = !autoStartLaunch && openNetworkMode && args.Any(value => string.Equals(value, "--prepare-network-mode", StringComparison.OrdinalIgnoreCase));
                MainForm form = new MainForm(autoStartLaunch, openNetworkMode, prepareNetworkMode);
                Application.Run(form);
            }
        }
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        public static void ShowExistingWindow(string title)
        {
            IntPtr handle = FindWindow(null, title);
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, 9);
                SetForegroundWindow(handle);
            }
        }
    }

    internal static class NativeWifiReader
    {
        private const uint ClientVersion = 2;
        private const int CurrentConnectionOpcode = 7;
        private const int ConnectedState = 1;

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential,
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct WlanConnectionAttributes
        {
            public int State;
            public int ConnectionMode;
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 256)]
            public string ProfileName;
            public WlanAssociationAttributes Association;
            public WlanSecurityAttributes Security;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct WlanAssociationAttributes
        {
            public Dot11Ssid Ssid;
            public int BssType;
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] Bssid;
            public int PhyType;
            public uint PhyIndex;
            public uint SignalQuality;
            public uint ReceiveRate;
            public uint TransmitRate;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Dot11Ssid
        {
            public uint Length;
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] Value;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct WlanSecurityAttributes
        {
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            public bool SecurityEnabled;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            public bool OneXEnabled;
            public int AuthenticationAlgorithm;
            public int CipherAlgorithm;
        }

        [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
        private static extern uint WlanOpenHandle(
            uint clientVersion,
            IntPtr reserved,
            out uint negotiatedVersion,
            out IntPtr clientHandle);

        [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
        private static extern uint WlanQueryInterface(
            IntPtr clientHandle,
            ref Guid interfaceGuid,
            int opcode,
            IntPtr reserved,
            out int dataSize,
            out IntPtr data,
            out int opcodeValueType);

        [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
        private static extern void WlanFreeMemory(IntPtr memory);

        [System.Runtime.InteropServices.DllImport("wlanapi.dll")]
        private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

        public static NetworkDetector.WifiConnectionInfo GetConnectedWifi(
            string interfaceName, string interfaceId)
        {
            Guid interfaceGuid;
            if (!Guid.TryParse(interfaceId, out interfaceGuid)) return null;

            IntPtr handle = IntPtr.Zero;
            IntPtr data = IntPtr.Zero;
            try
            {
                uint negotiatedVersion;
                if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out negotiatedVersion, out handle) != 0)
                    return null;

                int dataSize;
                int opcodeValueType;
                if (WlanQueryInterface(handle, ref interfaceGuid, CurrentConnectionOpcode, IntPtr.Zero,
                    out dataSize, out data, out opcodeValueType) != 0 || data == IntPtr.Zero)
                    return null;

                WlanConnectionAttributes attributes =
                    (WlanConnectionAttributes)System.Runtime.InteropServices.Marshal.PtrToStructure(
                        data, typeof(WlanConnectionAttributes));
                if (attributes.State != ConnectedState || attributes.Association.Ssid.Value == null)
                    return null;

                int ssidLength = Math.Min((int)attributes.Association.Ssid.Length,
                    attributes.Association.Ssid.Value.Length);
                string ssid = Encoding.UTF8.GetString(attributes.Association.Ssid.Value, 0, ssidLength);
                if (string.IsNullOrWhiteSpace(ssid)) return null;

                return new NetworkDetector.WifiConnectionInfo
                {
                    InterfaceName = interfaceName,
                    Ssid = ssid,
                    Profile = string.IsNullOrWhiteSpace(attributes.ProfileName)
                        ? null : attributes.ProfileName.Trim()
                };
            }
            catch (Exception ex)
            {
                AppLogger.Log("Native Wi-Fi query failed: " + ex.Message);
                return null;
            }
            finally
            {
                if (data != IntPtr.Zero) WlanFreeMemory(data);
                if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
            }
        }
    }

    internal sealed class AppConfig
    {
        public bool AutoStart { get; set; }
        public bool AutoEnableWithFastLink { get; set; }
        public bool DirectPrivateNetworks { get; set; }
        public bool AutoRestartFastLink { get; set; }
        public List<string> Rules { get; set; }

        public AppConfig()
        {
            AutoStart = true;
            AutoEnableWithFastLink = true;
            DirectPrivateNetworks = false;
            AutoRestartFastLink = false;
            Rules = new List<string>();
        }
    }

    internal static class ConfigStore
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        public static readonly string DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FastLinkDirectTool");
        public static readonly string ConfigPath = Path.Combine(DataDirectory, "config.json");
        public static readonly string RouteStatePath = Path.Combine(DataDirectory, "routes.json");

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    AppConfig config = Serializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath, Encoding.UTF8));
                    if (config != null)
                    {
                        if (config.Rules == null) config.Rules = new List<string>();
                        return config;
                    }
                }
            }
            catch { }

            AppConfig defaults = new AppConfig();
            Save(defaults);
            return defaults;
        }

        public static void Save(AppConfig config)
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(ConfigPath, Serializer.Serialize(config), new UTF8Encoding(false));
        }

        public static T LoadJson<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                return Serializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch { return null; }
        }

        public static void SaveJson<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Serializer.Serialize(value), new UTF8Encoding(false));
        }
    }

    internal sealed class ProcessResult
    {
        public int ExitCode;
        public string Output;
        public string Error;
        public bool TimedOut;
        public long ElapsedMilliseconds;
    }

    internal static class ProcessRunner
    {
        public static ProcessResult Run(string fileName, string arguments, int timeoutMilliseconds)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = fileName;
            info.Arguments = arguments;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;

            StringBuilder output = new StringBuilder();
            StringBuilder error = new StringBuilder();
            object outputSync = new object();
            object errorSync = new object();
            using (Process process = new Process())
            {
                process.StartInfo = info;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (outputSync) output.AppendLine(e.Data);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (errorSync) error.AppendLine(e.Data);
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    try { process.Kill(); } catch { }
                    try { process.WaitForExit(2000); } catch { }
                    AppLogger.Log("System command timed out: " + fileName);
                    HealthEventLogger.Write("system_command_timeout", new Dictionary<string, object>
                    {
                        { "file", Path.GetFileName(fileName) },
                        { "timeout_ms", timeoutMilliseconds },
                        { "elapsed_ms", stopwatch.ElapsedMilliseconds }
                    });
                    lock (outputSync)
                    lock (errorSync)
                    {
                        return new ProcessResult
                        {
                            ExitCode = -1,
                            Output = output.ToString(),
                            Error = error.ToString() + "Operation timed out.",
                            TimedOut = true,
                            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                        };
                    }
                }
                process.WaitForExit();
                lock (outputSync)
                lock (errorSync)
                {
                    return new ProcessResult
                    {
                        ExitCode = process.ExitCode,
                        Output = output.ToString(),
                        Error = error.ToString(),
                        TimedOut = false,
                        ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                    };
                }
            }
        }

        public static ProcessResult RunPowerShell(string script, int timeoutMilliseconds)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            if (encoded.Length < 24000)
                return Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, timeoutMilliseconds);
            // CreateProcess has a command-line length limit. Large rule sets use a
            // unique temporary script, with a BOM for Windows PowerShell 5.1.
            string path = Path.Combine(Path.GetTempPath(), "FastLinkDirectTool-" + Guid.NewGuid().ToString("N") + ".ps1");
            try
            {
                File.WriteAllText(path, script, new UTF8Encoding(true));
                return Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + path + "\"", timeoutMilliseconds);
            }
            finally
            {
                try { File.Delete(path); } catch { AppLogger.Log("Temporary helper script cleanup failed: " + Path.GetFileName(path)); }
            }
        }
    }

    internal static class AppLogger
    {
        private static readonly object Sync = new object();
        public static readonly string LogPath = Path.Combine(Application.StartupPath, "runtime.log");

        public static void Log(string message)
        {
            lock (Sync)
            {
                try
                {
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2 * 1024 * 1024)
                    {
                        string oldPath = LogPath + ".old";
                        File.Delete(oldPath);
                        File.Move(LogPath, oldPath);
                    }
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch { }
            }
        }
    }

    internal static class HealthEventLogger
    {
        private static readonly object Sync = new object();
        public static readonly string LogPath = Path.Combine(
            ConfigStore.DataDirectory, "health-events.jsonl");

        public static void Write(string eventName)
        {
            Write(eventName, null);
        }

        public static void Write(string eventName, IDictionary<string, object> details)
        {
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(ConfigStore.DataDirectory);
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 5 * 1024 * 1024)
                    {
                        string oldPath = LogPath + ".old";
                        if (File.Exists(oldPath)) File.Delete(oldPath);
                        File.Move(LogPath, oldPath);
                    }

                    Dictionary<string, object> record = new Dictionary<string, object>();
                    record["timestamp"] = DateTime.Now.ToString("o");
                    record["timestamp_utc"] = DateTime.UtcNow.ToString("o");
                    record["event"] = eventName;
                    record["process_id"] = Process.GetCurrentProcess().Id;
                    if (details != null)
                    {
                        foreach (KeyValuePair<string, object> item in details)
                            record[item.Key] = item.Value;
                    }

                    string json = new JavaScriptSerializer().Serialize(record);
                    File.AppendAllText(LogPath, json + Environment.NewLine, new UTF8Encoding(false));
                }
                catch { }
            }
        }
    }

    internal sealed class PhysicalNetworkInfo
    {
        public string InterfaceName;
        public string InterfaceId;
        public int InterfaceIndex;
        public bool IsWireless;
        public IPAddress LocalAddress;
        public IPAddress Gateway;
        public IPAddress DnsServer;
        public IPAddress FastLinkDns;
        public string ProxyConnectionIdentity;
        public string WifiSsid;
        public string WifiProfile;

        public string Identity
        {
            get
            {
                return InterfaceIndex.ToString(CultureInfo.InvariantCulture) + "|" +
                    Gateway + "|" + DnsServer + "|" + LocalAddress + "|" +
                    FastLinkDns + "|" + (ProxyConnectionIdentity ?? string.Empty);
            }
        }
    }

    internal static class NetworkDetector
    {
        internal sealed class WifiConnectionInfo
        {
            public string InterfaceName;
            public string Ssid;
            public string Profile;
        }

        public static bool IsFastLinkActive()
        {
            return IsLegacyTunnelActive() || FastLinkConnectionCompatibility.GetCurrent().Active;
        }

        public static bool IsLegacyTunnelActive()
        {
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    string text = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
                    if (text.Contains("fastlink") && adapter.OperationalStatus == OperationalStatus.Up)
                    {
                        IPInterfaceProperties properties = adapter.GetIPProperties();
                        bool hasGateway = properties.GatewayAddresses.Any(delegate(GatewayIPAddressInformation gateway)
                        {
                            return gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                                !gateway.Address.Equals(IPAddress.Any);
                        });
                        if (hasGateway) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static PhysicalNetworkInfo Detect(bool includeWifiDetails = false)
        {
            IPAddress fastLinkDns = null;
            List<Tuple<int, PhysicalNetworkInfo>> candidates = new List<Tuple<int, PhysicalNetworkInfo>>();

            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                string text = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
                IPInterfaceProperties properties;
                try { properties = adapter.GetIPProperties(); }
                catch { continue; }

                if (text.Contains("fastlink"))
                {
                    fastLinkDns = properties.DnsAddresses.FirstOrDefault(delegate(IPAddress address)
                    {
                        return address.AddressFamily == AddressFamily.InterNetwork;
                    });
                    continue;
                }

                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                    text.Contains("vpn") || text.Contains("tap") || text.Contains("virtual"))
                    continue;

                GatewayIPAddressInformation gatewayInfo = properties.GatewayAddresses.FirstOrDefault(delegate(GatewayIPAddressInformation value)
                {
                    return value.Address.AddressFamily == AddressFamily.InterNetwork && !value.Address.Equals(IPAddress.Any);
                });
                UnicastIPAddressInformation localInfo = properties.UnicastAddresses.FirstOrDefault(delegate(UnicastIPAddressInformation value)
                {
                    return value.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(value.Address);
                });
                IPAddress dns = properties.DnsAddresses.FirstOrDefault(delegate(IPAddress value)
                {
                    return value.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(value) && !IsFastLinkRange(value);
                });

                if (gatewayInfo == null || localInfo == null || dns == null) continue;
                IPv4InterfaceProperties ipv4;
                try { ipv4 = properties.GetIPv4Properties(); }
                catch { continue; }

                PhysicalNetworkInfo candidate = new PhysicalNetworkInfo();
                candidate.InterfaceName = adapter.Name;
                candidate.InterfaceId = adapter.Id;
                candidate.InterfaceIndex = ipv4.Index;
                candidate.IsWireless = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                candidate.LocalAddress = localInfo.Address;
                candidate.Gateway = gatewayInfo.Address;
                candidate.DnsServer = dns;
                int priority = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1;
                candidates.Add(Tuple.Create(priority, candidate));
            }

            PhysicalNetworkInfo selected = candidates.OrderBy(delegate(Tuple<int, PhysicalNetworkInfo> item) { return item.Item1; })
                .Select(delegate(Tuple<int, PhysicalNetworkInfo> item) { return item.Item2; })
                .FirstOrDefault();
            if (selected == null) return null;
            // No TUN exists in the new system-proxy-only mode. Unmatched DNS
            // requests must use physical DNS, not an unreachable fake-IP server.
            selected.FastLinkDns = fastLinkDns ?? selected.DnsServer;
            FastLinkConnectionInfo connection = FastLinkConnectionCompatibility.GetCurrent();
            selected.ProxyConnectionIdentity = connection.Active ? connection.ProxyServerIdentity : string.Empty;
            if (includeWifiDetails && selected.IsWireless)
            {
                WifiConnectionInfo wifi = null;
                for (int attempt = 0; attempt < 3 && wifi == null; attempt++)
                {
                    wifi = GetConnectedWifi(selected.InterfaceName, selected.InterfaceId);
                    if (wifi == null && attempt < 2) Thread.Sleep(400);
                }
                if (wifi != null)
                {
                    selected.WifiSsid = wifi.Ssid;
                    selected.WifiProfile = wifi.Profile;
                }
                else
                {
                    AppLogger.Log("Wi-Fi is usable, but its SSID could not be read after three attempts.");
                }
            }
            return selected;
        }

        public static WifiConnectionInfo GetConnectedWifi(string interfaceName, string interfaceId = null)
        {
            if (string.IsNullOrWhiteSpace(interfaceName)) return null;
            WifiConnectionInfo native = NativeWifiReader.GetConnectedWifi(interfaceName, interfaceId);
            if (native != null) return native;
            try
            {
                ProcessResult result = ProcessRunner.Run(
                    "netsh.exe", "wlan show interfaces name=\"" +
                    interfaceName.Replace("\"", string.Empty) + "\"", 5000);
                if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output)) return null;

                WifiConnectionInfo current = null;
                foreach (string rawLine in result.Output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    Match nameMatch = Regex.Match(
                        rawLine, @"^\s*(?:Name|名称)\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase);
                    if (nameMatch.Success)
                    {
                        if (current != null &&
                            string.Equals(current.InterfaceName, interfaceName, StringComparison.OrdinalIgnoreCase))
                            return string.IsNullOrWhiteSpace(current.Ssid) ? null : current;
                        current = new WifiConnectionInfo { InterfaceName = nameMatch.Groups[1].Value.Trim() };
                        continue;
                    }
                    if (current == null) continue;

                    Match ssidMatch = Regex.Match(
                        rawLine, @"^\s*SSID\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase);
                    if (ssidMatch.Success)
                    {
                        current.Ssid = ssidMatch.Groups[1].Value.Trim();
                        continue;
                    }
                    Match profileMatch = Regex.Match(
                        rawLine, @"^\s*(?:Profile|配置文件)\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase);
                    if (profileMatch.Success)
                        current.Profile = profileMatch.Groups[1].Value.Trim();
                }

                if (current != null &&
                    string.Equals(current.InterfaceName, interfaceName, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(current.Ssid))
                    return current;
            }
            catch (Exception ex)
            {
                AppLogger.Log("Could not read the connected Wi-Fi name: " + ex.Message);
            }
            return null;
        }

        private static bool IsFastLinkRange(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19);
        }
    }

    internal sealed class FastLinkRecoveryResult
    {
        public bool PhysicalNetworkRecovered;
        public bool FastLinkRecovered;
        public bool PreferredWifiRecovered;
        public bool UsedFallbackWifi;
        public string PreferredWifiSsid;
        public string RecoveredWifiSsid;
        public string Detail;
        public ProxyProbeResult ProxyProbe;
        public bool DirectRulesRebuilt;
        public long RecoveryElapsedMilliseconds;
        public int ProxyVerificationSuccesses;
    }

    internal static class FastLinkRecovery
    {
        public static bool IsPhysicalNetworkUsable(string interfaceName)
        {
            if (string.IsNullOrWhiteSpace(interfaceName)) return false;
            try
            {
                NetworkInterface adapter = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(delegate(NetworkInterface value)
                    {
                        return string.Equals(value.Name, interfaceName, StringComparison.OrdinalIgnoreCase);
                    });
                if (adapter == null || adapter.OperationalStatus != OperationalStatus.Up) return false;

                IPInterfaceProperties properties = adapter.GetIPProperties();
                bool hasAddress = properties.UnicastAddresses.Any(delegate(UnicastIPAddressInformation value)
                {
                    if (value.Address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(value.Address) ||
                        value.Address.Equals(IPAddress.Any))
                        return false;
                    byte[] bytes = value.Address.GetAddressBytes();
                    return bytes.Length == 4 && !(bytes[0] == 169 && bytes[1] == 254);
                });
                bool hasGateway = properties.GatewayAddresses.Any(delegate(GatewayIPAddressInformation value)
                {
                    return value.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !value.Address.Equals(IPAddress.Any);
                });
                return hasAddress && hasGateway;
            }
            catch { return false; }
        }

        public static FastLinkRecoveryResult Recover(
            string interfaceName, string interfaceId, string preferredWifiSsid, string preferredWifiProfile)
        {
            FastLinkRecoveryResult result = new FastLinkRecoveryResult();
            result.PreferredWifiSsid = preferredWifiSsid;
            string executablePath = FindFastLinkExecutable();
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                result.Detail = "FastLink executable could not be located.";
                return result;
            }

            AppLogger.Log("Controlled FastLink recovery started on " + interfaceName +
                (string.IsNullOrWhiteSpace(preferredWifiSsid) ? "." : ", preferred SSID=" + preferredWifiSsid + "."));
            StopProcesses("FastLink");
            StopProcesses("FastLinkCore");
            WaitUntilFastLinkStops(8);

            if (!string.IsNullOrWhiteSpace(preferredWifiSsid))
                RequestPreferredWifi(interfaceName, preferredWifiSsid, preferredWifiProfile);

            ProcessResult renew = ProcessRunner.Run(
                "ipconfig.exe",
                "/renew \"" + EscapeArgument(interfaceName) + "\"",
                30000);
            if (!string.IsNullOrWhiteSpace(preferredWifiSsid))
                result.PreferredWifiRecovered = WaitUntilPreferredWifiUsable(
                    interfaceName, interfaceId, preferredWifiSsid, preferredWifiProfile, 45);

            result.PhysicalNetworkRecovered = result.PreferredWifiRecovered ||
                WaitUntilPhysicalNetworkUsable(interfaceName, string.IsNullOrWhiteSpace(preferredWifiSsid) ? 30 : 5);
            NetworkDetector.WifiConnectionInfo recoveredWifi =
                NetworkDetector.GetConnectedWifi(interfaceName, interfaceId);
            result.RecoveredWifiSsid = recoveredWifi == null ? null : recoveredWifi.Ssid;
            result.UsedFallbackWifi = result.PhysicalNetworkRecovered &&
                !string.IsNullOrWhiteSpace(preferredWifiSsid) && !result.PreferredWifiRecovered;
            if (!result.PhysicalNetworkRecovered)
            {
                result.Detail = "FastLink was stopped, but WLAN did not obtain a usable address.";
                AppLogger.Log(result.Detail + " RenewExitCode=" + renew.ExitCode);
                return result;
            }

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = executablePath;
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                result.Detail = "WLAN recovered, but FastLink could not be started: " + ex.Message;
                AppLogger.Log(result.Detail);
                return result;
            }

            result.FastLinkRecovered = WaitUntilFastLinkStarts(45);
            if (result.FastLinkRecovered && result.UsedFallbackWifi)
                result.Detail = "FastLink recovered on fallback Wi-Fi " +
                    (string.IsNullOrWhiteSpace(result.RecoveredWifiSsid) ? "(unknown)" : result.RecoveredWifiSsid) +
                    "; preferred Wi-Fi " + preferredWifiSsid + " is still unavailable.";
            else if (result.FastLinkRecovered)
                result.Detail = "Preferred WLAN and FastLink recovered automatically.";
            else
                result.Detail = "WLAN recovered, but FastLink did not reconnect automatically.";
            AppLogger.Log(result.Detail);
            return result;
        }

        private static void RequestPreferredWifi(string interfaceName, string ssid, string profile)
        {
            string profileName = string.IsNullOrWhiteSpace(profile) ? ssid : profile;
            ProcessResult connect = ProcessRunner.Run(
                "netsh.exe",
                "wlan connect name=\"" + EscapeArgument(profileName) + "\" ssid=\"" +
                    EscapeArgument(ssid) + "\" interface=\"" + EscapeArgument(interfaceName) + "\"",
                10000);
            AppLogger.Log("Requested preferred Wi-Fi " + ssid + ". ExitCode=" + connect.ExitCode);
        }

        private static bool WaitUntilPreferredWifiUsable(
            string interfaceName, string interfaceId, string preferredWifiSsid,
            string preferredWifiProfile, int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                if (i > 0 && i % 10 == 0)
                    RequestPreferredWifi(interfaceName, preferredWifiSsid, preferredWifiProfile);

                if (i % 2 == 0)
                {
                    NetworkDetector.WifiConnectionInfo wifi =
                        NetworkDetector.GetConnectedWifi(interfaceName, interfaceId);
                    if (wifi != null &&
                        string.Equals(wifi.Ssid, preferredWifiSsid, StringComparison.OrdinalIgnoreCase) &&
                        IsPhysicalNetworkUsable(interfaceName))
                        return true;
                }
                Thread.Sleep(1000);
            }

            NetworkDetector.WifiConnectionInfo finalWifi =
                NetworkDetector.GetConnectedWifi(interfaceName, interfaceId);
            return finalWifi != null &&
                string.Equals(finalWifi.Ssid, preferredWifiSsid, StringComparison.OrdinalIgnoreCase) &&
                IsPhysicalNetworkUsable(interfaceName);
        }

        public static FastLinkRecoveryResult RestartFastLink(string interfaceName)
        {
            FastLinkRecoveryResult result = new FastLinkRecoveryResult();
            result.PhysicalNetworkRecovered = IsPhysicalNetworkUsable(interfaceName);
            if (!result.PhysicalNetworkRecovered)
            {
                result.Detail = "Physical network is not usable; FastLink restart was skipped.";
                AppLogger.Log(result.Detail);
                return result;
            }

            string executablePath = FindFastLinkExecutable();
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                result.Detail = "FastLink executable could not be located.";
                return result;
            }

            AppLogger.Log("Controlled FastLink-only recovery started on " + interfaceName + ".");
            StopProcesses("FastLink");
            StopProcesses("FastLinkCore");
            WaitUntilFastLinkStops(8);

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = executablePath;
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                result.Detail = "FastLink could not be restarted: " + ex.Message;
                AppLogger.Log(result.Detail);
                return result;
            }

            result.FastLinkRecovered = WaitUntilFastLinkStarts(45);
            result.Detail = result.FastLinkRecovered
                ? "FastLink restarted automatically after a proxy health failure."
                : "FastLink did not reconnect after the proxy health failure.";
            AppLogger.Log(result.Detail);
            return result;
        }

        private static string FindFastLinkExecutable()
        {
            try
            {
                Process current = Process.GetProcessesByName("FastLink").FirstOrDefault();
                if (current != null)
                {
                    string path = current.MainModule.FileName;
                    if (File.Exists(path)) return path;
                }
            }
            catch { }

            string[] candidates =
            {
                @"D:\Programs\FastLink\FastLink.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FastLink", "FastLink.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "FastLink", "FastLink.exe")
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private static void StopProcesses(string processName)
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    AppLogger.Log("Could not stop " + processName + ": " + ex.Message);
                }
                finally { process.Dispose(); }
            }
        }

        private static bool WaitUntilPhysicalNetworkUsable(string interfaceName, int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                if (IsPhysicalNetworkUsable(interfaceName)) return true;
                Thread.Sleep(1000);
            }
            return IsPhysicalNetworkUsable(interfaceName);
        }

        private static void WaitUntilFastLinkStops(int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                if (!NetworkDetector.IsFastLinkActive()) return;
                Thread.Sleep(1000);
            }
        }

        private static bool WaitUntilFastLinkStarts(int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                if (NetworkDetector.IsFastLinkActive()) return true;
                Thread.Sleep(1000);
            }
            return NetworkDetector.IsFastLinkActive();
        }

        private static string EscapeArgument(string value)
        {
            return (value ?? string.Empty).Replace("\"", string.Empty);
        }
    }

    internal sealed class ProxyProbeResult
    {
        public bool Healthy;
        public bool TimedOut;
        public int StatusCode;
        public long ElapsedMilliseconds;
        public string Error;
    }

    internal static class ProxyHealthProbe
    {
        public static bool HasWorkingTransport()
        {
            return Probe("https://chatgpt.com/favicon.ico", 7000).Healthy;
        }

        public static bool HasWorkingTransport(string uri, int timeoutMilliseconds)
        {
            return Probe(uri, timeoutMilliseconds).Healthy;
        }

        public static ProxyProbeResult Probe()
        {
            return Probe("https://chatgpt.com/favicon.ico", 7000);
        }

        public static ProxyProbeResult Probe(string uri, int timeoutMilliseconds)
        {
            ProxyProbeResult result = new ProxyProbeResult();
            Stopwatch stopwatch = Stopwatch.StartNew();
            HttpWebResponse response = null;
            HttpWebRequest request = null;
            IAsyncResult asyncResult = null;
            try
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                }
                catch { }

                request = (HttpWebRequest)WebRequest.Create(uri);
                request.Method = "HEAD";
                request.AllowAutoRedirect = false;
                request.Timeout = timeoutMilliseconds;
                request.ReadWriteTimeout = timeoutMilliseconds;
                request.UserAgent = "FastLinkDirectTool/2.5.1";
                request.Headers[HttpRequestHeader.CacheControl] = "no-cache";
                asyncResult = request.BeginGetResponse(null, null);
                if (!asyncResult.AsyncWaitHandle.WaitOne(timeoutMilliseconds))
                {
                    result.TimedOut = true;
                    result.Error = "hard_timeout";
                    try { request.Abort(); } catch { }
                    return result;
                }
                response = (HttpWebResponse)request.EndGetResponse(asyncResult);
                result.Healthy = true;
                result.StatusCode = (int)response.StatusCode;
                return result;
            }
            catch (WebException ex)
            {
                response = ex.Response as HttpWebResponse;
                result.Healthy = response != null;
                result.StatusCode = response == null ? 0 : (int)response.StatusCode;
                result.Error = ex.Status.ToString();
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.GetType().Name + ": " + ex.Message;
                return result;
            }
            finally
            {
                stopwatch.Stop();
                result.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                if (response != null) response.Close();
                if (asyncResult != null)
                {
                    try { asyncResult.AsyncWaitHandle.Close(); } catch { }
                }
            }
        }
    }

    internal sealed class RulePattern
    {
        public string Text { get; private set; }
        private Regex Matcher { get; set; }
        private string SuffixBase { get; set; }
        private bool IsSuffixRule { get; set; }
        private bool IsExactRule { get; set; }
        private IPAddress DirectIpAddress { get; set; }

        private RulePattern() { }

        public bool Matches(string host)
        {
            host = host.TrimEnd('.').ToLowerInvariant();
            if (DirectIpAddress != null) return string.Equals(host, DirectIpAddress.ToString(), StringComparison.OrdinalIgnoreCase);
            if (IsExactRule) return string.Equals(host, Text, StringComparison.OrdinalIgnoreCase);
            if (IsSuffixRule)
            {
                return string.Equals(host, SuffixBase, StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith("." + SuffixBase, StringComparison.OrdinalIgnoreCase);
            }
            return Matcher.IsMatch(host);
        }

        public IEnumerable<string> GetNrptNamespaces()
        {
            if (DirectIpAddress != null) return new string[0];
            if (IsExactRule) return new[] { Text };
            if (IsSuffixRule) return new[] { SuffixBase, "." + SuffixBase };
            return new[] { "." + SuffixBase };
        }

        public static bool TryCreate(string input, out RulePattern pattern, out string error)
        {
            pattern = null;
            error = null;
            string value = NormalizeInput(input);
            if (string.IsNullOrWhiteSpace(value))
            {
                error = "请输入域名规则。";
                return false;
            }
            if (!Regex.IsMatch(value, "^[a-z0-9._*?-]+$", RegexOptions.IgnoreCase) || !value.Contains("."))
            {
                error = "规则只能包含域名字符以及 * 和 ?。";
                return false;
            }

            RulePattern result = new RulePattern();
            result.Text = value.ToLowerInvariant();
            IPAddress parsedAddress;
            if (IPAddress.TryParse(result.Text, out parsedAddress))
            {
                if (parsedAddress.AddressFamily != AddressFamily.InterNetwork)
                {
                    error = "当前版本仅支持 IPv4 直连规则。";
                    return false;
                }
                result.DirectIpAddress = parsedAddress;
                result.IsExactRule = true;
                result.Text = parsedAddress.ToString();
                result.SuffixBase = result.Text;
            }
            else if (result.Text.StartsWith(".", StringComparison.Ordinal) && result.Text.IndexOfAny(new[] { '*', '?' }) < 0)
            {
                result.IsSuffixRule = true;
                result.SuffixBase = result.Text.Substring(1);
                if (!IsValidFixedSuffix(result.SuffixBase))
                {
                    error = "后缀规则至少需要类似 .example.com 的两段域名。";
                    return false;
                }
            }
            else if (result.Text.IndexOfAny(new[] { '*', '?' }) < 0)
            {
                result.IsExactRule = true;
                result.SuffixBase = result.Text;
            }
            else
            {
                string[] labels = result.Text.Split('.');
                int firstFixed = -1;
                for (int i = 0; i < labels.Length; i++)
                {
                    if (labels[i].IndexOf('*') < 0 && labels[i].IndexOf('?') < 0)
                    {
                        bool remainingFixed = true;
                        for (int j = i; j < labels.Length; j++)
                        {
                            if (labels[j].IndexOf('*') >= 0 || labels[j].IndexOf('?') >= 0)
                            {
                                remainingFixed = false;
                                break;
                            }
                        }
                        if (remainingFixed) { firstFixed = i; break; }
                    }
                }
                if (firstFixed < 0)
                {
                    error = "通配规则末尾必须保留固定域名，例如 *.example.com。";
                    return false;
                }
                result.SuffixBase = string.Join(".", labels.Skip(firstFixed).ToArray());
                if (!IsValidFixedSuffix(result.SuffixBase))
                {
                    error = "通配规则末尾至少需要 example.com 这样的两段固定域名。";
                    return false;
                }
                string expression = "^" + Regex.Escape(result.Text).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                result.Matcher = new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            pattern = result;
            return true;
        }

        public IPAddress GetDirectIpAddress()
        {
            return DirectIpAddress;
        }

        private static string NormalizeInput(string input)
        {
            string value = (input ?? string.Empty).Trim().ToLowerInvariant();
            Uri uri;
            if (Uri.TryCreate(value, UriKind.Absolute, out uri) && !string.IsNullOrWhiteSpace(uri.Host))
                return uri.IdnHost.TrimEnd('.');
            int slash = value.IndexOf('/');
            if (slash >= 0) value = value.Substring(0, slash);
            int colon = value.IndexOf(':');
            if (colon >= 0) value = value.Substring(0, colon);
            return value.TrimEnd('.');
        }

        private static bool IsValidFixedSuffix(string value)
        {
            string[] labels = value.Split('.');
            return labels.Length >= 2 && labels.All(delegate(string label)
            {
                return label.Length > 0 && Regex.IsMatch(label, "^[a-z0-9-]+$", RegexOptions.IgnoreCase);
            });
        }
    }

    internal sealed class RuleSet
    {
        private readonly List<RulePattern> patterns;
        public List<string> NormalizedRules { get; private set; }

        public RuleSet(IEnumerable<string> rules)
        {
            patterns = new List<RulePattern>();
            NormalizedRules = new List<string>();
            foreach (string rule in rules)
            {
                RulePattern pattern;
                string error;
                if (RulePattern.TryCreate(rule, out pattern, out error) &&
                    !NormalizedRules.Contains(pattern.Text, StringComparer.OrdinalIgnoreCase))
                {
                    patterns.Add(pattern);
                    NormalizedRules.Add(pattern.Text);
                }
            }
        }

        public bool Matches(string host)
        {
            return patterns.Any(delegate(RulePattern pattern) { return pattern.Matches(host); });
        }

        public List<string> GetNrptNamespaces()
        {
            return patterns.SelectMany(delegate(RulePattern pattern) { return pattern.GetNrptNamespaces(); })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public List<IPAddress> GetDirectIpAddresses()
        {
            return patterns.Select(delegate(RulePattern pattern) { return pattern.GetDirectIpAddress(); })
                .Where(delegate(IPAddress address) { return address != null; })
                .Distinct()
                .ToList();
        }
    }

    internal sealed class TrackedRoute
    {
        public string Address { get; set; }
        public string Netmask { get; set; }
        public int InterfaceIndex { get; set; }
        public string Gateway { get; set; }
        public string LocalAddress { get; set; }
        public long ExpiresUtcTicks { get; set; }
        public bool Infrastructure { get; set; }
    }

    internal sealed class RouteState
    {
        public List<TrackedRoute> Routes { get; set; }
        public RouteState() { Routes = new List<TrackedRoute>(); }
    }

    internal sealed class RouteManager
    {
        private readonly object sync = new object();
        private readonly Func<string, string, int, ProcessResult> runCommand;
        private readonly Action<RouteState> saveState;
        private RouteState state;
        private PhysicalNetworkInfo network;
        private long ensureRequests;
        private long addAttempts;
        private long addSucceeded;
        private long commandTimeouts;
        private long suppressedMutations;
        private long routeMutationBlockedUntilUtcTicks;
        private long lastSuppressedLogUtcTicks;

        public RouteManager() : this(
            ConfigStore.LoadJson<RouteState>(ConfigStore.RouteStatePath) ?? new RouteState(),
            ProcessRunner.Run,
            delegate(RouteState value) { ConfigStore.SaveJson(ConfigStore.RouteStatePath, value); })
        {
        }

        // Tests inject all command/persistence boundaries; no real routes or files.
        internal RouteManager(RouteState initialState, Func<string, string, int, ProcessResult> command,
            Action<RouteState> save)
        {
            if (initialState == null) throw new ArgumentNullException("initialState");
            if (command == null) throw new ArgumentNullException("command");
            if (save == null) throw new ArgumentNullException("save");
            state = initialState;
            runCommand = command;
            saveState = save;
            if (state.Routes == null) state.Routes = new List<TrackedRoute>();
        }

        public void SetNetwork(PhysicalNetworkInfo value)
        {
            // A null route network drains an in-flight write and prevents DNS
            // callbacks from starting more writes. It does NOT stop the DNS
            // listener or clear DnsProxy's separate upstream snapshot, and it
            // deliberately leaves explicit tracked-route cleanup available.
            lock (sync) { network = value; }
        }

        public bool EnsureDnsRoute()
        {
            Interlocked.Increment(ref ensureRequests);
            lock (sync)
            {
                if (network == null) return false;
                return EnsureRouteLocked(network.DnsServer, 0, true) && HasPhysicalHostRoute(network.DnsServer);
            }
        }

        public bool EnsureAddressRoute(IPAddress address, uint ttl)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork) return true;
            if (IsFakeIp(address)) return false;
            Interlocked.Increment(ref ensureRequests);
            lock (sync)
            {
                if (network == null) return false;
                long expires = DateTime.UtcNow.AddSeconds(Math.Max(60, Math.Min(3600, ttl))).Ticks;
                return EnsureRouteLocked(address, expires, false);
            }
        }

        public bool EnsureRuleAddressRoute(IPAddress address)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork) return false;
            if (IsFakeIp(address)) return false;
            Interlocked.Increment(ref ensureRequests);
            lock (sync)
            {
                if (network == null) return false;
                return EnsureRouteLocked(address, 0, true);
            }
        }

        internal static bool IsFakeIp(IPAddress address)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] bytes = address.GetAddressBytes();
            return bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19);
        }

        public bool EnsurePrivateNetworkRoutes()
        {
            Interlocked.Increment(ref ensureRequests);
            lock (sync)
            {
                if (network == null) return false;
                return EnsureNetworkRouteLocked("10.0.0.0", "255.0.0.0") &&
                    EnsureNetworkRouteLocked("172.16.0.0", "255.240.0.0") &&
                    EnsureNetworkRouteLocked("192.168.0.0", "255.255.0.0");
            }
        }

        private bool EnsureNetworkRouteLocked(string destination, string netmask)
        {
            TrackedRoute tracked = state.Routes.FirstOrDefault(delegate(TrackedRoute route)
            {
                return string.Equals(route.Address, destination, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(route.Netmask ?? "255.255.255.255", netmask, StringComparison.OrdinalIgnoreCase) &&
                    route.InterfaceIndex == network.InterfaceIndex &&
                    string.Equals(route.Gateway, network.Gateway.ToString(), StringComparison.OrdinalIgnoreCase);
            });
            if (tracked != null) return true;
            if (!CanMutateLocked(destination)) return false;

            string arguments = string.Format(CultureInfo.InvariantCulture,
                "ADD {0} MASK {1} {2} METRIC 1 IF {3}",
                destination, netmask, network.Gateway, network.InterfaceIndex);
            Interlocked.Increment(ref addAttempts);
            ProcessResult result = runCommand("route.exe", arguments, 5000);
            if (result.TimedOut)
            {
                RecordTimeoutLocked("add_network", destination, result);
                return false;
            }
            if (result.ExitCode != 0 && !HasPhysicalRoute(destination, netmask)) return false;
            if (result.ExitCode == 0)
            {
                Interlocked.Increment(ref addSucceeded);
                state.Routes.Add(new TrackedRoute
                {
                    Address = destination,
                    Netmask = netmask,
                    InterfaceIndex = network.InterfaceIndex,
                    Gateway = network.Gateway.ToString(),
                    LocalAddress = network.LocalAddress.ToString(),
                    ExpiresUtcTicks = 0,
                    Infrastructure = true
                });
                SaveLocked();
            }
            return true;
        }

        private bool EnsureRouteLocked(IPAddress address, long expiresTicks, bool infrastructure)
        {
            TrackedRoute tracked = state.Routes.FirstOrDefault(delegate(TrackedRoute route)
            {
                return string.Equals(route.Address, address.ToString(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(route.Netmask ?? "255.255.255.255", "255.255.255.255", StringComparison.OrdinalIgnoreCase) &&
                    route.InterfaceIndex == network.InterfaceIndex &&
                    string.Equals(route.Gateway, network.Gateway.ToString(), StringComparison.OrdinalIgnoreCase);
            });
            if (tracked != null)
            {
                if (expiresTicks > tracked.ExpiresUtcTicks + TimeSpan.FromSeconds(30).Ticks)
                {
                    tracked.ExpiresUtcTicks = expiresTicks;
                    SaveLocked();
                }
                return true;
            }

            if (!CanMutateLocked(address.ToString())) return false;

            string arguments = string.Format(CultureInfo.InvariantCulture,
                "ADD {0} MASK 255.255.255.255 {1} METRIC 1 IF {2}",
                address, network.Gateway, network.InterfaceIndex);
            Interlocked.Increment(ref addAttempts);
            ProcessResult result = runCommand("route.exe", arguments, 5000);
            if (result.TimedOut)
            {
                RecordTimeoutLocked("add_host", address.ToString(), result);
                return false;
            }
            if (result.ExitCode != 0 && !HasPhysicalHostRoute(address)) return false;
            if (result.ExitCode == 0)
            {
                Interlocked.Increment(ref addSucceeded);
                state.Routes.Add(new TrackedRoute
                {
                    Address = address.ToString(),
                    Netmask = "255.255.255.255",
                    InterfaceIndex = network.InterfaceIndex,
                    Gateway = network.Gateway.ToString(),
                    LocalAddress = network.LocalAddress.ToString(),
                    ExpiresUtcTicks = expiresTicks,
                    Infrastructure = infrastructure
                });
                SaveLocked();
            }
            return true;
        }

        private bool HasPhysicalHostRoute(IPAddress address)
        {
            return HasPhysicalRoute(address.ToString(), "255.255.255.255");
        }

        private bool HasPhysicalRoute(string destination, string netmask)
        {
            ProcessResult result = runCommand("route.exe", "PRINT -4 " + destination, 5000);
            if (result.ExitCode != 0) return false;
            string[] lines = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string[] parts = Regex.Split(line.Trim(), "\\s+");
                if (parts.Length >= 5 && parts[0] == destination && parts[1] == netmask &&
                    parts[2] == network.Gateway.ToString() && parts[3] == network.LocalAddress.ToString())
                    return true;
            }
            return false;
        }

        public void RemoveExpired()
        {
            lock (sync)
            {
                long now = DateTime.UtcNow.Ticks;
                List<TrackedRoute> expired = state.Routes.Where(delegate(TrackedRoute route)
                {
                    return !route.Infrastructure && route.ExpiresUtcTicks > 0 && route.ExpiresUtcTicks < now;
                }).ToList();
                foreach (TrackedRoute route in expired)
                {
                    if (DeleteRouteLocked(route)) state.Routes.Remove(route);
                }
                if (expired.Count > 0) SaveLocked();
            }
        }

        public void RemoveAll()
        {
            lock (sync)
            {
                foreach (TrackedRoute route in state.Routes.ToList())
                {
                    if (DeleteRouteLocked(route)) state.Routes.Remove(route);
                }
                SaveLocked();
                if (state.Routes.Count != 0)
                    throw new InvalidOperationException("直连路由尚有残留，已保留记录，请稍后重试清理。");
            }
        }

        public void RemovePrivateNetworkRoutes()
        {
            lock (sync)
            {
                List<TrackedRoute> privateRoutes = state.Routes.Where(delegate(TrackedRoute route)
                {
                    string mask = route.Netmask ?? "255.255.255.255";
                    return (route.Address == "10.0.0.0" && mask == "255.0.0.0") ||
                        (route.Address == "172.16.0.0" && mask == "255.240.0.0") ||
                        (route.Address == "192.168.0.0" && mask == "255.255.0.0");
                }).ToList();
                foreach (TrackedRoute route in privateRoutes)
                {
                    if (DeleteRouteLocked(route)) state.Routes.Remove(route);
                }
                if (privateRoutes.Count > 0) SaveLocked();
            }
        }

        private bool DeleteRouteLocked(TrackedRoute route)
        {
            if (!CanMutateLocked(route.Address)) return false;
            string netmask = string.IsNullOrWhiteSpace(route.Netmask) ? "255.255.255.255" : route.Netmask;
            string arguments = string.Format(CultureInfo.InvariantCulture,
                "DELETE {0} MASK {1} {2} IF {3}",
                route.Address, netmask, route.Gateway, route.InterfaceIndex);
            ProcessResult result = runCommand("route.exe", arguments, 5000);
            if (result.TimedOut)
            {
                RecordTimeoutLocked("delete", route.Address, result);
                return false;
            }
            ProcessResult check = runCommand("route.exe", "PRINT -4 " + route.Address, 5000);
            if (check.TimedOut || check.ExitCode != 0) return false;
            foreach (string line in check.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = Regex.Split(line.Trim(), "\\s+");
                if (parts.Length >= 5 && parts[0] == route.Address && parts[1] == netmask &&
                    parts[2] == route.Gateway && parts[3] == route.LocalAddress) return false;
            }
            return true;
        }

        private bool CanMutateLocked(string destination)
        {
            long now = DateTime.UtcNow.Ticks;
            if (now >= routeMutationBlockedUntilUtcTicks) return true;
            Interlocked.Increment(ref suppressedMutations);
            if (now - lastSuppressedLogUtcTicks > TimeSpan.FromSeconds(10).Ticks)
            {
                lastSuppressedLogUtcTicks = now;
                HealthEventLogger.Write("route_mutation_suppressed", new Dictionary<string, object>
                {
                    { "destination", destination },
                    { "blocked_until_utc", new DateTime(routeMutationBlockedUntilUtcTicks, DateTimeKind.Utc).ToString("o") }
                });
            }
            return false;
        }

        private void RecordTimeoutLocked(string operation, string destination, ProcessResult result)
        {
            Interlocked.Increment(ref commandTimeouts);
            routeMutationBlockedUntilUtcTicks = DateTime.UtcNow.AddSeconds(25).Ticks;
            HealthEventLogger.Write("route_command_timeout", new Dictionary<string, object>
            {
                { "operation", operation },
                { "destination", destination },
                { "elapsed_ms", result.ElapsedMilliseconds },
                { "blocked_seconds", 25 }
            });
        }

        public IDictionary<string, object> GetHealthDetails()
        {
            lock (sync)
            {
                Dictionary<string, object> details = new Dictionary<string, object>();
                details["route_ensure_requests"] = Interlocked.Read(ref ensureRequests);
                details["route_add_attempts"] = Interlocked.Read(ref addAttempts);
                details["route_add_succeeded"] = Interlocked.Read(ref addSucceeded);
                details["route_command_timeouts"] = Interlocked.Read(ref commandTimeouts);
                details["route_mutations_suppressed"] = Interlocked.Read(ref suppressedMutations);
                details["route_tracked_count"] = state.Routes.Count;
                details["route_circuit_open"] = DateTime.UtcNow.Ticks < routeMutationBlockedUntilUtcTicks;
                details["route_dns_writes_paused"] = network == null;
                return details;
            }
        }

        private void SaveLocked()
        {
            saveState(state);
        }
    }

    internal static class NrptManager
    {
        private const string Marker = "FastLinkDirectTool";
        private const string AppliedMarker = "__FASTLINK_NRPT_APPLIED__";
        private const string RemovedMarker = "__FASTLINK_NRPT_REMOVED__";

        public static void Apply(IEnumerable<string> namespaces)
        {
            List<string> expected = namespaces.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            ProcessResult result = ProcessRunner.RunPowerShell(BuildApplyScript(expected), ApplyTimeoutMilliseconds(expected.Count));
            LogResult("apply", result, expected.Count);
            VerifyCommandResult(result, AppliedMarker, "应用 Windows DNS 规则");
            FlushDnsCache();
        }

        internal static int ApplyTimeoutMilliseconds(int namespaceCount)
        {
            // Large lists invoke Windows management once per rule. Allow bounded
            // progress without turning a slow operation into an endless retry loop.
            return (int)Math.Min(300000L, Math.Max(120000L, 30000L + Math.Max(0, namespaceCount) * 3000L));
        }

        private static void LogResult(string operation, ProcessResult result, int expectedCount)
        {
            string progress = (result.Output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(delegate(string line) { return line.StartsWith("__FASTLINK_NRPT_STAGE__"); }) ?? "no_progress_output";
            HealthEventLogger.Write("nrpt_command_result", new Dictionary<string, object>
            {
                { "operation", operation }, { "expected_count", expectedCount },
                { "timed_out", result.TimedOut }, { "exit_code", result.ExitCode },
                { "elapsed_ms", result.ElapsedMilliseconds }, { "last_progress", progress }
            });
        }

        internal static string BuildApplyScript(IEnumerable<string> namespaces)
        {
            List<string> expected = namespaces.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            StringBuilder script = new StringBuilder();
            script.AppendLine("$ErrorActionPreference='Stop'");
            script.AppendLine("$ProgressPreference='SilentlyContinue'");
            AppendRemove(script);
            script.AppendLine("Write-Output '__FASTLINK_NRPT_STAGE__ apply_start'");
            int added = 0;
            foreach (string value in expected)
            {
                string safe = value.Replace("'", "''");
                script.AppendLine("Add-DnsClientNrptRule -Namespace '" + safe + "' -NameServers '127.0.0.1' -DisplayName 'FastLink Direct - " + safe + "' -Comment '" + Marker + "' | Out-Null");
                added++;
                script.AppendLine("Write-Output '__FASTLINK_NRPT_STAGE__ applied " + added + "/" + expected.Count + "'");
            }
            script.AppendLine("Write-Output '__FASTLINK_NRPT_STAGE__ apply_readback'");
            script.AppendLine("$actual=@(Get-DnsClientNrptRule -ErrorAction Stop | Where-Object { $_.Comment -eq '" + Marker + "' })");
            script.AppendLine("if ($actual.Count -ne " + expected.Count.ToString(CultureInfo.InvariantCulture) + ") { throw 'NRPT apply readback count mismatch.' }");
            script.AppendLine("$expected=@(" + string.Join(",", expected.Select(delegate(string value) { return "'" + value.Replace("'", "''") + "'"; }).ToArray()) + ")");
            script.AppendLine("foreach ($ns in $expected) { if (-not @($actual | Where-Object { $_.Namespace -contains $ns -and $_.NameServers -eq '127.0.0.1' }).Count) { throw 'NRPT apply readback mismatch.' } }");
            script.AppendLine("Write-Output '" + AppliedMarker + "'");
            return script.ToString();
        }

        public static void RemoveAll()
        {
            ProcessResult result = ProcessRunner.RunPowerShell(BuildRemoveScript(), 120000);
            LogResult("remove", result, 0);
            VerifyCommandResult(result, RemovedMarker, "清理 Windows DNS 规则");
            FlushDnsCache();
        }

        internal static string BuildRemoveScript()
        {
            StringBuilder script = new StringBuilder();
            script.AppendLine("$ErrorActionPreference='Stop'");
            script.AppendLine("$ProgressPreference='SilentlyContinue'");
            AppendRemove(script);
            script.AppendLine("Write-Output '" + RemovedMarker + "'");
            return script.ToString();
        }

        internal static void VerifyCommandResult(ProcessResult result, string marker, string operation)
        {
            bool verified = result != null && !result.TimedOut && result.ExitCode == 0 &&
                (result.Output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Any(delegate(string line) { return line.Trim() == marker; });
            if (!verified)
            {
                throw new InvalidOperationException(operation + "未确认成功；保留 DNS 服务并停止后续清理。" +
                    (result == null ? string.Empty : result.Error));
            }
        }

        private static void AppendRemove(StringBuilder script)
        {
            script.AppendLine("Write-Output '__FASTLINK_NRPT_STAGE__ cleanup_read'");
            script.AppendLine("$owned=@(Get-DnsClientNrptRule -ErrorAction Stop | Where-Object { $_.Comment -eq '" + Marker + "' })");
            script.AppendLine("$removed=0");
            script.AppendLine("foreach ($rule in $owned) { Remove-DnsClientNrptRule -Name $rule.Name -Force -ErrorAction Stop; $removed++; Write-Output ('__FASTLINK_NRPT_STAGE__ removed ' + $removed + '/' + $owned.Count) }");
            script.AppendLine("Write-Output '__FASTLINK_NRPT_STAGE__ cleanup_readback'");
            script.AppendLine("$remaining=@(Get-DnsClientNrptRule -ErrorAction Stop | Where-Object { $_.Comment -eq '" + Marker + "' })");
            script.AppendLine("if ($remaining.Count -ne 0) { throw ('NRPT cleanup incomplete: ' + $remaining.Count) }");
        }

        private static void FlushDnsCache()
        {
            ProcessResult result = ProcessRunner.Run("ipconfig.exe", "/flushdns", 5000);
            if (result.TimedOut || result.ExitCode != 0)
                throw new InvalidOperationException("DNS 缓存刷新未完成，请稍后重试；尚未停止 DNS 服务。");
        }
    }

    internal static class SafeCleanup
    {
        internal static void Run(Action pauseRouteWrites, Action removeDnsRules,
            Action removeProxyBypass, Action stopDns, Action removeRoutes)
        {
            pauseRouteWrites();
            Run(delegate
            {
                // Keep direct exceptions until DNS-rule removal is verified.
                // Either failure must preserve the still-required DNS listener.
                removeDnsRules();
                removeProxyBypass();
            }, stopDns, removeRoutes);
        }

        internal static void Run(Action removeDnsRules, Action stopDns, Action removeRoutes)
        {
            // Do not stop a listener while Windows may still send it DNS queries.
            removeDnsRules();
            stopDns();
            removeRoutes();
        }
    }

    internal sealed class DnsAnswerAddress
    {
        public IPAddress Address;
        public uint Ttl;
    }

    internal static class DnsPacket
    {
        public static bool TryReadQuestion(byte[] packet, out string name, out ushort type, out int questionEnd)
        {
            name = null;
            type = 0;
            questionEnd = 0;
            if (packet == null || packet.Length < 17) return false;
            int offset = 12;
            StringBuilder builder = new StringBuilder();
            while (offset < packet.Length)
            {
                int length = packet[offset++];
                if (length == 0) break;
                if (length > 63 || offset + length > packet.Length) return false;
                if (builder.Length > 0) builder.Append('.');
                builder.Append(Encoding.ASCII.GetString(packet, offset, length));
                offset += length;
            }
            if (offset + 4 > packet.Length) return false;
            type = ReadUInt16(packet, offset);
            questionEnd = offset + 4;
            name = builder.ToString().TrimEnd('.').ToLowerInvariant();
            return name.Length > 0;
        }

        public static List<DnsAnswerAddress> ReadAAnswers(byte[] packet)
        {
            List<DnsAnswerAddress> values = new List<DnsAnswerAddress>();
            if (packet == null || packet.Length < 12) return values;
            int questions = ReadUInt16(packet, 4);
            int answers = ReadUInt16(packet, 6);
            int offset = 12;
            for (int i = 0; i < questions; i++)
            {
                if (!SkipName(packet, ref offset) || offset + 4 > packet.Length) return values;
                offset += 4;
            }
            for (int i = 0; i < answers; i++)
            {
                if (!SkipName(packet, ref offset) || offset + 10 > packet.Length) return values;
                ushort type = ReadUInt16(packet, offset); offset += 2;
                offset += 2;
                uint ttl = ReadUInt32(packet, offset); offset += 4;
                ushort length = ReadUInt16(packet, offset); offset += 2;
                if (offset + length > packet.Length) return values;
                if (type == 1 && length == 4)
                {
                    byte[] address = new byte[4];
                    Buffer.BlockCopy(packet, offset, address, 0, 4);
                    values.Add(new DnsAnswerAddress { Address = new IPAddress(address), Ttl = ttl });
                }
                offset += length;
            }
            return values;
        }

        public static byte[] BuildEmptyAnswer(byte[] query, int questionEnd, int rcode)
        {
            if (query == null || questionEnd < 12 || questionEnd > query.Length) return query;
            byte[] response = new byte[questionEnd];
            Buffer.BlockCopy(query, 0, response, 0, questionEnd);
            response[2] = (byte)((response[2] & 0x01) | 0x80);
            response[3] = (byte)(0x80 | (rcode & 0x0F));
            response[6] = response[7] = response[8] = response[9] = response[10] = response[11] = 0;
            return response;
        }

        private static bool SkipName(byte[] packet, ref int offset)
        {
            int guard = 0;
            while (offset < packet.Length && guard++ < 128)
            {
                byte value = packet[offset++];
                if (value == 0) return true;
                if ((value & 0xC0) == 0xC0)
                {
                    if (offset >= packet.Length) return false;
                    offset++;
                    return true;
                }
                if (value > 63 || offset + value > packet.Length) return false;
                offset += value;
            }
            return false;
        }

        private static ushort ReadUInt16(byte[] value, int offset)
        {
            return (ushort)((value[offset] << 8) | value[offset + 1]);
        }

        private static uint ReadUInt32(byte[] value, int offset)
        {
            return ((uint)value[offset] << 24) | ((uint)value[offset + 1] << 16) |
                ((uint)value[offset + 2] << 8) | value[offset + 3];
        }
    }

    internal sealed class DnsProxy : IDisposable
    {
        private sealed class DnsCacheEntry
        {
            public byte[] Response;
            public long ExpiresUtcTicks;
        }

        private volatile bool running;
        public bool IsRunning { get { return running; } }
        private volatile RuleSet ruleSet;
        private PhysicalNetworkInfo network;
        private RouteManager routes;
        private UdpClient udpListener;
        private TcpListener tcpListener;
        private readonly object querySync = new object();
        private readonly Dictionary<string, DnsCacheEntry> responseCache = new Dictionary<string, DnsCacheEntry>();
        private readonly Dictionary<string, TaskCompletionSource<byte[]>> inFlight = new Dictionary<string, TaskCompletionSource<byte[]>>();
        private readonly SemaphoreSlim querySlots = new SemaphoreSlim(48, 48);
        private long totalQueries;
        private long directQueries;
        private long cacheHits;
        private long coalescedQueries;
        private long upstreamFailures;
        private long routeFailures;
        private long overloadedQueries;
        private long lastFailureLogUtcTicks;

        public void Start(PhysicalNetworkInfo networkInfo, RuleSet rules, RouteManager routeManager)
        {
            if (running) return;
            network = networkInfo;
            ruleSet = rules;
            routes = routeManager;

            udpListener = new UdpClient(AddressFamily.InterNetwork);
            udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            udpListener.Client.Bind(new IPEndPoint(IPAddress.Loopback, 53));
            tcpListener = new TcpListener(IPAddress.Loopback, 53);
            tcpListener.Start();
            running = true;
            Task.Run((Action)UdpLoop);
            Task.Run((Action)TcpLoop);
        }

        public void UpdateRules(RuleSet rules)
        {
            ruleSet = rules;
            lock (querySync) responseCache.Clear();
        }

        public void Stop()
        {
            running = false;
            try { udpListener.Close(); } catch { }
            try { tcpListener.Stop(); } catch { }
            udpListener = null;
            tcpListener = null;
            lock (querySync) responseCache.Clear();
        }

        private void UdpLoop()
        {
            while (running)
            {
                try
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] query = udpListener.Receive(ref remote);
                    Task.Run(delegate { HandleUdp(query, remote); });
                }
                catch { if (!running) return; }
            }
        }

        private void HandleUdp(byte[] query, IPEndPoint client)
        {
            bool entered = querySlots.Wait(0);
            try
            {
                byte[] response;
                if (entered) response = HandleQuery(query, false);
                else
                {
                    Interlocked.Increment(ref overloadedQueries);
                    response = BuildServFail(query);
                }
                if (response == null || !running) return;
                try { udpListener.Send(response, response.Length, client); } catch { }
            }
            finally
            {
                if (entered) querySlots.Release();
            }
        }

        private void TcpLoop()
        {
            while (running)
            {
                try
                {
                    TcpClient client = tcpListener.AcceptTcpClient();
                    Task.Run(delegate { HandleTcp(client); });
                }
                catch { if (!running) return; }
            }
        }

        private void HandleTcp(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    NetworkStream stream = client.GetStream();
                    byte[] lengthBytes = ReadExact(stream, 2);
                    if (lengthBytes == null) return;
                    int length = (lengthBytes[0] << 8) | lengthBytes[1];
                    byte[] query = ReadExact(stream, length);
                    if (query == null) return;
                    bool entered = querySlots.Wait(0);
                    byte[] response;
                    try
                    {
                        if (entered) response = HandleQuery(query, true);
                        else
                        {
                            Interlocked.Increment(ref overloadedQueries);
                            response = BuildServFail(query);
                        }
                    }
                    finally
                    {
                        if (entered) querySlots.Release();
                    }
                    if (response == null) return;
                    stream.WriteByte((byte)(response.Length >> 8));
                    stream.WriteByte((byte)(response.Length & 0xFF));
                    stream.Write(response, 0, response.Length);
                }
                catch { }
            }
        }

        private byte[] HandleQuery(byte[] query, bool tcp)
        {
            string host;
            ushort type;
            int questionEnd;
            if (!DnsPacket.TryReadQuestion(query, out host, out type, out questionEnd)) return null;
            Interlocked.Increment(ref totalQueries);
            bool direct = ruleSet != null && ruleSet.Matches(host);
            if (direct) Interlocked.Increment(ref directQueries);
            if (direct && (type == 28 || type == 64 || type == 65))
                return DnsPacket.BuildEmptyAnswer(query, questionEnd, 0);

            IPAddress upstream = direct ? network.DnsServer : network.FastLinkDns;
            if (!direct)
            {
                byte[] normalResponse = tcp ? ForwardTcp(query, upstream) : ForwardUdp(query, upstream);
                if (normalResponse == null)
                {
                    Interlocked.Increment(ref upstreamFailures);
                    LogFailure("dns_upstream_failure", host, type, upstream, "no_response");
                    return DnsPacket.BuildEmptyAnswer(query, questionEnd, 2);
                }
                return normalResponse;
            }

            string key = host + "|" + type.ToString(CultureInfo.InvariantCulture);
            TaskCompletionSource<byte[]> owner = null;
            Task<byte[]> sharedTask = null;
            long now = DateTime.UtcNow.Ticks;
            lock (querySync)
            {
                DnsCacheEntry cached;
                if (responseCache.TryGetValue(key, out cached))
                {
                    if (cached.ExpiresUtcTicks > now)
                    {
                        Interlocked.Increment(ref cacheHits);
                        return CopyWithTransactionId(cached.Response, query);
                    }
                    responseCache.Remove(key);
                }

                TaskCompletionSource<byte[]> existing;
                if (inFlight.TryGetValue(key, out existing))
                {
                    sharedTask = existing.Task;
                    Interlocked.Increment(ref coalescedQueries);
                }
                else
                {
                    owner = new TaskCompletionSource<byte[]>();
                    inFlight[key] = owner;
                }
            }

            if (owner == null)
            {
                try
                {
                    if (sharedTask.Wait(6500))
                        return CopyWithTransactionId(sharedTask.Result, query);
                }
                catch { }
                return DnsPacket.BuildEmptyAnswer(query, questionEnd, 2);
            }

            byte[] response = null;
            try
            {
                response = tcp ? ForwardTcp(query, upstream) : ForwardUdp(query, upstream);
                if (response == null)
                {
                    Interlocked.Increment(ref upstreamFailures);
                    LogFailure("dns_upstream_failure", host, type, upstream, "no_response");
                    response = DnsPacket.BuildEmptyAnswer(query, questionEnd, 2);
                }

                if (type == 1)
                {
                    List<DnsAnswerAddress> addresses = DnsPacket.ReadAAnswers(response);
                    foreach (DnsAnswerAddress address in addresses)
                    {
                        if (!routes.EnsureAddressRoute(address.Address, address.Ttl))
                        {
                            Interlocked.Increment(ref routeFailures);
                            LogFailure("dns_route_failure", host, type, upstream, address.Address.ToString());
                            response = DnsPacket.BuildEmptyAnswer(query, questionEnd, 2);
                            break;
                        }
                    }
                }

                int cacheSeconds = IsSuccessfulResponse(response) ? 30 : 2;
                lock (querySync)
                {
                    if (responseCache.Count > 512)
                    {
                        foreach (string expiredKey in responseCache.Where(delegate(KeyValuePair<string, DnsCacheEntry> item)
                        {
                            return item.Value.ExpiresUtcTicks <= DateTime.UtcNow.Ticks;
                        }).Select(delegate(KeyValuePair<string, DnsCacheEntry> item) { return item.Key; }).ToList())
                            responseCache.Remove(expiredKey);
                        if (responseCache.Count > 512) responseCache.Clear();
                    }
                    responseCache[key] = new DnsCacheEntry
                    {
                        Response = (byte[])response.Clone(),
                        ExpiresUtcTicks = DateTime.UtcNow.AddSeconds(cacheSeconds).Ticks
                    };
                }
                return response;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref upstreamFailures);
                LogFailure("dns_query_failure", host, type, upstream, ex.GetType().Name + ": " + ex.Message);
                response = DnsPacket.BuildEmptyAnswer(query, questionEnd, 2);
                return response;
            }
            finally
            {
                lock (querySync) inFlight.Remove(key);
                try { owner.SetResult(response); } catch { }
            }
        }

        private static bool IsSuccessfulResponse(byte[] response)
        {
            return response != null && response.Length >= 4 && (response[3] & 0x0F) == 0;
        }

        private static byte[] CopyWithTransactionId(byte[] response, byte[] query)
        {
            if (response == null) return null;
            byte[] copy = (byte[])response.Clone();
            if (copy.Length >= 2 && query != null && query.Length >= 2)
            {
                copy[0] = query[0];
                copy[1] = query[1];
            }
            return copy;
        }

        private static byte[] BuildServFail(byte[] query)
        {
            string host;
            ushort type;
            int questionEnd;
            if (!DnsPacket.TryReadQuestion(query, out host, out type, out questionEnd)) return null;
            return DnsPacket.BuildEmptyAnswer(query, questionEnd, 2);
        }

        private void LogFailure(string eventName, string host, ushort type, IPAddress upstream, string reason)
        {
            long now = DateTime.UtcNow.Ticks;
            long previous = Interlocked.Read(ref lastFailureLogUtcTicks);
            if (now - previous < TimeSpan.FromSeconds(10).Ticks) return;
            if (Interlocked.CompareExchange(ref lastFailureLogUtcTicks, now, previous) != previous) return;
            HealthEventLogger.Write(eventName, new Dictionary<string, object>
            {
                { "host", host },
                { "query_type", type },
                { "upstream", upstream == null ? string.Empty : upstream.ToString() },
                { "reason", reason }
            });
        }

        public IDictionary<string, object> GetHealthDetails()
        {
            Dictionary<string, object> details = new Dictionary<string, object>();
            details["dns_total_queries"] = Interlocked.Read(ref totalQueries);
            details["dns_direct_queries"] = Interlocked.Read(ref directQueries);
            details["dns_cache_hits"] = Interlocked.Read(ref cacheHits);
            details["dns_coalesced_queries"] = Interlocked.Read(ref coalescedQueries);
            details["dns_upstream_failures"] = Interlocked.Read(ref upstreamFailures);
            details["dns_route_failures"] = Interlocked.Read(ref routeFailures);
            details["dns_overloaded_queries"] = Interlocked.Read(ref overloadedQueries);
            lock (querySync)
            {
                details["dns_cache_entries"] = responseCache.Count;
                details["dns_inflight_queries"] = inFlight.Count;
            }
            return details;
        }

        private static byte[] ForwardUdp(byte[] query, IPAddress upstream)
        {
            try
            {
                using (UdpClient client = new UdpClient(AddressFamily.InterNetwork))
                {
                    client.Client.ReceiveTimeout = 4000;
                    client.Connect(upstream, 53);
                    client.Send(query, query.Length);
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    return client.Receive(ref remote);
                }
            }
            catch { return null; }
        }

        private static byte[] ForwardTcp(byte[] query, IPAddress upstream)
        {
            try
            {
                using (TcpClient client = new TcpClient(AddressFamily.InterNetwork))
                {
                    IAsyncResult result = client.BeginConnect(upstream, 53, null, null);
                    if (!result.AsyncWaitHandle.WaitOne(4000)) return null;
                    client.EndConnect(result);
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    NetworkStream stream = client.GetStream();
                    stream.WriteByte((byte)(query.Length >> 8));
                    stream.WriteByte((byte)(query.Length & 0xFF));
                    stream.Write(query, 0, query.Length);
                    byte[] lengthBytes = ReadExact(stream, 2);
                    if (lengthBytes == null) return null;
                    int length = (lengthBytes[0] << 8) | lengthBytes[1];
                    return ReadExact(stream, length);
                }
            }
            catch { return null; }
        }

        private static byte[] ReadExact(Stream stream, int length)
        {
            byte[] data = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(data, offset, length - offset);
                if (read <= 0) return null;
                offset += read;
            }
            return data;
        }

        public void Dispose() { Stop(); }
    }

    internal sealed class RuntimeController
    {
        private readonly object sync = new object();
        private readonly RouteManager routes;
        private readonly DnsProxy proxy;
        private readonly SystemProxyBypassManager systemProxyBypass;
        private bool privateNetworksEnabled;
        public bool Enabled { get; private set; }
        public bool CleanupPending { get; private set; }
        public PhysicalNetworkInfo Network { get; private set; }

        public RuntimeController()
        {
            routes = new RouteManager();
            proxy = new DnsProxy();
            systemProxyBypass = new SystemProxyBypassManager();
        }

        private void EnterCleanupPending()
        {
            // Runtime/UI gates do not cover asynchronous DNS callbacks. Detach
            // their route writer too, retaining DNS and its upstream snapshot.
            routes.SetNetwork(null);
            CleanupPending = true;
        }

        public void InitializeAndCleanLegacy()
        {
            AppLogger.Log("Application initialization and stale-state cleanup started.");
            try
            {
                SafeCleanup.Run(delegate { routes.SetNetwork(null); }, NrptManager.RemoveAll,
                    systemProxyBypass.Remove, delegate { }, routes.RemoveAll);
                LegacyCleanup.Run();
                CleanupPending = false;
                AppLogger.Log("Application initialization completed.");
            }
            catch
            {
                EnterCleanupPending();
                throw;
            }
        }

        public void Enable(RuleSet rules, bool directPrivateNetworks)
        {
            lock (sync)
            {
                if (Enabled) return;
                if (CleanupPending) throw new InvalidOperationException("仍有待完成的清理，请先关闭直连重试清理。");
                AppLogger.Log("Enable requested. Rules=" + rules.NormalizedRules.Count + ", privateNetworks=" + directPrivateNetworks);
                if (rules.NormalizedRules.Count == 0) throw new InvalidOperationException("请先添加至少一条域名规则。");
                PhysicalNetworkInfo network = NetworkDetector.Detect(true);
                if (network == null) throw new InvalidOperationException("没有找到可用的物理 Wi-Fi 或有线网卡。");
                Network = network;
                AppLogger.Log("Physical network detected: " + network.InterfaceName +
                    (string.IsNullOrWhiteSpace(network.WifiSsid) ? string.Empty : ", SSID=" + network.WifiSsid) +
                    ", DNS=" + network.DnsServer + ", gateway=" + network.Gateway);
                routes.SetNetwork(network);
                try
                {
                    if (!routes.EnsureDnsRoute()) throw new InvalidOperationException("无法为物理 DNS 添加直连路由。");
                    foreach (IPAddress address in rules.GetDirectIpAddresses())
                    {
                        if (!routes.EnsureRuleAddressRoute(address))
                            throw new InvalidOperationException("无法为 IP 地址 " + address + " 添加直连路由。");
                    }
                    if (directPrivateNetworks && !routes.EnsurePrivateNetworkRoutes())
                        throw new InvalidOperationException("无法添加内网直连路由。");
                    proxy.Start(network, rules, routes);
                    AppLogger.Log("Local DNS proxy started; applying Windows DNS rules.");
                    NrptManager.Apply(rules.GetNrptNamespaces());
                    SyncSystemProxyBypass(rules, directPrivateNetworks);
                    Enabled = true;
                    CleanupPending = false;
                    privateNetworksEnabled = directPrivateNetworks;
                    AppLogger.Log("Direct mode enabled on " + network.InterfaceName + ", DNS=" + network.DnsServer + ", gateway=" + network.Gateway);
                }
                catch (Exception enableError)
                {
                    try
                    {
                        Disable();
                        AppLogger.Log("Enable failed; rollback verified.");
                    }
                    catch (Exception cleanupError)
                    {
                        AppLogger.Log("Enable rollback incomplete; DNS listener retained when required: " + cleanupError.Message);
                        throw new AggregateException("开启失败且清理未完成；请重试清理，勿强制结束助手。", enableError, cleanupError);
                    }
                    throw;
                }
            }
        }

        public void Disable()
        {
            lock (sync)
            {
                try
                {
                    // Suspend DNS-triggered writes BEFORE potentially slow NRPT
                    // commands. Cleanup failure must not let the DNS workers
                    // resume ADD operations when the short route circuit expires.
                    SafeCleanup.Run(delegate { routes.SetNetwork(null); }, NrptManager.RemoveAll,
                        systemProxyBypass.Remove, proxy.Stop, routes.RemoveAll);
                    Network = null;
                    Enabled = false;
                    CleanupPending = false;
                    privateNetworksEnabled = false;
                    AppLogger.Log("Direct mode disabled; DNS rule and route cleanup verified.");
                }
                catch (Exception error)
                {
                    EnterCleanupPending();
                    Enabled = proxy.IsRunning;
                    AppLogger.Log("Cleanup incomplete; listener running=" + proxy.IsRunning + ". " + error.Message);
                    HealthEventLogger.Write("cleanup_incomplete", new Dictionary<string, object>
                    {
                        { "dns_listener_running", proxy.IsRunning },
                        { "error", error.Message }
                    });
                    throw;
                }
            }
        }

        public void UpdateRules(RuleSet rules, bool directPrivateNetworks)
        {
            lock (sync)
            {
                if (CleanupPending) throw new InvalidOperationException("清理未完成，已暂停规则刷新；请先重试安全清理。");
                if (!Enabled) return;
                AppLogger.Log("Hot rule update started. Rules=" + rules.NormalizedRules.Count + ", privateNetworks=" + directPrivateNetworks);
                try
                {
                    proxy.UpdateRules(rules);
                    foreach (IPAddress address in rules.GetDirectIpAddresses())
                    {
                        if (!routes.EnsureRuleAddressRoute(address))
                            throw new InvalidOperationException("无法为 IP 地址 " + address + " 添加直连路由。");
                    }
                    if (directPrivateNetworks && !privateNetworksEnabled)
                    {
                        if (!routes.EnsurePrivateNetworkRoutes()) throw new InvalidOperationException("无法添加内网直连路由。");
                    }
                    else if (!directPrivateNetworks && privateNetworksEnabled)
                    {
                        routes.RemovePrivateNetworkRoutes();
                    }
                    NrptManager.Apply(rules.GetNrptNamespaces());
                    SyncSystemProxyBypass(rules, directPrivateNetworks);
                }
                catch (Exception error)
                {
                    EnterCleanupPending();
                    HealthEventLogger.Write("rule_update_incomplete", new Dictionary<string, object> { { "error", error.Message } });
                    throw;
                }
                privateNetworksEnabled = directPrivateNetworks;
                AppLogger.Log("Hot rule update completed.");
            }
        }

        public void Restart(RuleSet rules, bool directPrivateNetworks)
        {
            Disable();
            Enable(rules, directPrivateNetworks);
        }

        private void SyncSystemProxyBypass(RuleSet rules, bool directPrivateNetworks)
        {
            FastLinkConnectionInfo verified = FastLinkConnectionCompatibility.GetCurrent();
            if (verified.Active)
                systemProxyBypass.Apply(rules, directPrivateNetworks, verified.ProxyServerIdentity);
            else
                systemProxyBypass.Remove();
        }

        public void RemoveExpiredRoutes()
        {
            lock (sync)
            {
                if (!Enabled || CleanupPending) return;
                routes.RemoveExpired();
            }
        }

        public void MaintainSystemProxyBypass()
        {
            lock (sync)
            {
                if (!Enabled || CleanupPending || !FastLinkConnectionCompatibility.GetCurrent().Active) return;
                try
                {
                    if (systemProxyBypass.EnsureCurrent())
                        AppLogger.Log("System proxy direct exceptions restored after external refresh.");
                }
                catch
                {
                    // Keep DNS alive, pause further mutations, and expose failure
                    // through the existing explicit safe-cleanup UI gate.
                    EnterCleanupPending();
                    throw;
                }
            }
        }

        public IDictionary<string, object> GetHealthDetails()
        {
            Dictionary<string, object> details = new Dictionary<string, object>();
            details["direct_enabled"] = Enabled;
            details["cleanup_pending"] = CleanupPending;
            details["system_proxy_owned_bypass_count"] = systemProxyBypass.OwnedTokenCount;
            PhysicalNetworkInfo current = Network;
            if (current != null)
            {
                details["physical_interface"] = current.InterfaceName;
                details["physical_interface_index"] = current.InterfaceIndex;
                details["physical_local_address"] = current.LocalAddress == null ? string.Empty : current.LocalAddress.ToString();
                details["physical_gateway"] = current.Gateway == null ? string.Empty : current.Gateway.ToString();
                details["physical_dns"] = current.DnsServer == null ? string.Empty : current.DnsServer.ToString();
                details["wifi_ssid"] = current.WifiSsid ?? string.Empty;
            }
            foreach (KeyValuePair<string, object> item in routes.GetHealthDetails()) details[item.Key] = item.Value;
            foreach (KeyValuePair<string, object> item in proxy.GetHealthDetails()) details[item.Key] = item.Value;
            return details;
        }
    }

    internal static class LegacyCleanup
    {
        private sealed class LegacyRoute
        {
            public string Prefix { get; set; }
            public int InterfaceIndex { get; set; }
            public string NextHop { get; set; }
        }

        private sealed class LegacyState
        {
            public List<LegacyRoute> CreatedRoutes { get; set; }
        }

        public static void Run()
        {
            List<string> paths = new List<string>();
            paths.Add(Path.Combine(Application.StartupPath, "ep-direct-test-state.json"));
            DirectoryInfo parent = Directory.GetParent(Application.StartupPath);
            if (parent != null) paths.Add(Path.Combine(parent.FullName, "ep-direct-test-state.json"));

            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                LegacyState state = ConfigStore.LoadJson<LegacyState>(path);
                if (state == null || state.CreatedRoutes == null) continue;
                foreach (LegacyRoute route in state.CreatedRoutes)
                {
                    string address = (route.Prefix ?? string.Empty).Split('/')[0];
                    if (!string.IsNullOrWhiteSpace(address))
                    {
                        string arguments = string.Format(CultureInfo.InvariantCulture,
                            "DELETE {0} MASK 255.255.255.255 {1} IF {2}",
                            address, route.NextHop, route.InterfaceIndex);
                        ProcessRunner.Run("route.exe", arguments, 10000);
                    }
                }
                try { File.Delete(path); } catch { }
            }
        }
    }

    internal static class StartupTask
    {
        private const string TaskName = "FastLinkDirectTool";

        public static bool IsInstalled()
        {
            return ProcessRunner.Run("schtasks.exe", "/Query /TN \"" + TaskName + "\"", 10000).ExitCode == 0;
        }

        public static void Install()
        {
            string temp = Path.Combine(Path.GetTempPath(), "FastLinkDirectTool-task.xml");
            string sid = WindowsIdentity.GetCurrent().User.Value;
            XmlWriterSettings settings = new XmlWriterSettings();
            settings.Indent = true;
            // Task Scheduler on some Chinese Windows installations rejects UTF-8
            // task XML with "cannot switch encoding". UTF-16 is accepted reliably.
            settings.Encoding = Encoding.Unicode;
            using (XmlWriter writer = XmlWriter.Create(temp, settings))
            {
                string taskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                writer.WriteStartDocument();
                writer.WriteStartElement("Task", taskNamespace);
                writer.WriteAttributeString("version", "1.4");
                writer.WriteStartElement("RegistrationInfo", taskNamespace);
                writer.WriteElementString("Description", taskNamespace, "FastLink direct routing helper");
                writer.WriteEndElement();
                writer.WriteStartElement("Triggers", taskNamespace);
                writer.WriteStartElement("LogonTrigger", taskNamespace);
                writer.WriteElementString("Enabled", taskNamespace, "true");
                writer.WriteElementString("UserId", taskNamespace, sid);
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteStartElement("Principals", taskNamespace);
                writer.WriteStartElement("Principal", taskNamespace);
                writer.WriteAttributeString("id", "Author");
                writer.WriteElementString("UserId", taskNamespace, sid);
                writer.WriteElementString("LogonType", taskNamespace, "InteractiveToken");
                writer.WriteElementString("RunLevel", taskNamespace, "HighestAvailable");
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteStartElement("Settings", taskNamespace);
                writer.WriteElementString("MultipleInstancesPolicy", taskNamespace, "IgnoreNew");
                writer.WriteElementString("DisallowStartIfOnBatteries", taskNamespace, "false");
                writer.WriteElementString("StopIfGoingOnBatteries", taskNamespace, "false");
                writer.WriteElementString("AllowHardTerminate", taskNamespace, "true");
                writer.WriteElementString("StartWhenAvailable", taskNamespace, "true");
                writer.WriteElementString("RunOnlyIfNetworkAvailable", taskNamespace, "false");
                writer.WriteElementString("AllowStartOnDemand", taskNamespace, "true");
                writer.WriteElementString("Enabled", taskNamespace, "true");
                writer.WriteElementString("Hidden", taskNamespace, "false");
                writer.WriteElementString("RunOnlyIfIdle", taskNamespace, "false");
                writer.WriteElementString("WakeToRun", taskNamespace, "false");
                writer.WriteElementString("ExecutionTimeLimit", taskNamespace, "PT0S");
                writer.WriteElementString("Priority", taskNamespace, "7");
                writer.WriteEndElement();
                writer.WriteStartElement("Actions", taskNamespace);
                writer.WriteAttributeString("Context", "Author");
                writer.WriteStartElement("Exec", taskNamespace);
                writer.WriteElementString("Command", taskNamespace, Application.ExecutablePath);
                writer.WriteElementString("Arguments", taskNamespace, "--autostart");
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
            ProcessResult result = ProcessRunner.Run("schtasks.exe", "/Create /TN \"" + TaskName + "\" /XML \"" + temp + "\" /F", 20000);
            try { File.Delete(temp); } catch { }
            if (result.ExitCode != 0) throw new InvalidOperationException("无法创建开机自启动任务：" + result.Error + result.Output);
        }

        public static void Uninstall()
        {
            ProcessRunner.Run("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F", 10000);
        }
    }

    internal sealed class NetworkRefreshPolicy
    {
        private string candidate;
        private DateTime candidateSinceUtc;
        public bool Paused { get; private set; }

        internal static bool CanWork(bool busy, bool enabled, bool cleanupPending)
        {
            return !busy && enabled && !cleanupPending;
        }

        public bool Observe(string current, string target, DateTime nowUtc)
        {
            if (Paused) return false;
            if (string.IsNullOrEmpty(target) || string.Equals(current, target, StringComparison.Ordinal))
            {
                ResetCandidate();
                return false;
            }
            if (!string.Equals(candidate, target, StringComparison.Ordinal))
            {
                candidate = target;
                candidateSinceUtc = nowUtc;
                return false;
            }
            return nowUtc - candidateSinceUtc >= TimeSpan.FromSeconds(8);
        }

        public void ResetCandidate() { candidate = null; }
        public void Pause() { Paused = true; ResetCandidate(); }
        public void Reset() { Paused = false; ResetCandidate(); }
    }

    // Accessed only on the UI thread. Completing an older snapshot must not
    // acknowledge edits made while that snapshot was being applied.
    internal sealed class RuleRefreshQueue
    {
        public long RequestedRevision { get; private set; }
        public long AppliedRevision { get; private set; }
        public bool Paused { get; private set; }
        public bool HasPending { get { return RequestedRevision > AppliedRevision; } }

        public void Request() { RequestedRevision++; }

        public bool CanApply(bool busy, bool enabled, bool cleanupPending)
        {
            return HasPending && !Paused && NetworkRefreshPolicy.CanWork(busy, enabled, cleanupPending);
        }

        public void Complete(long revision, bool succeeded)
        {
            if (!succeeded) { Paused = true; return; }
            AppliedRevision = Math.Max(AppliedRevision, revision);
            Paused = false;
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly AppConfig config;
        private readonly RuntimeController runtime;
        private readonly System.Windows.Forms.Timer monitorTimer;
        private readonly NotifyIcon trayIcon;
        private readonly Label fastLinkStatus;
        private readonly Label directStatus;
        private readonly Label networkStatus;
        private readonly CheckBox mainSwitch;
        private readonly CheckBox autoEnable;
        private readonly CheckBox autoStart;
        private readonly CheckBox directPrivateNetworks;
        private readonly CheckBox autoRestartFastLink;
        private readonly ListBox ruleList;
        private readonly TextBox ruleInput;
        private readonly TextBox logBox;
        private bool busy;
        private bool manualOffUntilReconnect;
        private bool lastFastLinkActive;
        private bool allowExit;
        private DateTime physicalNetworkMissingSince = DateTime.MinValue;
        private DateTime nextNetworkRecoveryAllowed = DateTime.MinValue;
        private bool proxyHealthCheckInProgress;
        private int consecutiveProxyHealthFailures;
        private DateTime nextProxyHealthCheck = DateTime.MinValue;
        private DateTime nextProxyRecoveryAllowed = DateTime.Now.AddMinutes(5);
        private DateTime lastProxyRecoveryCompleted = DateTime.MinValue;
        private DateTime proxyHealthFailureSince = DateTime.MinValue;
        private string proxyIncidentId;
        private int routeMaintenanceInProgress;
        private int systemProxyMaintenanceInProgress;
        private DateTime nextSystemProxyMaintenance = DateTime.MinValue;
        private DateTime nextRouteMaintenance = DateTime.MinValue;
        private int healthHeartbeatInProgress;
        private DateTime nextHealthHeartbeat = DateTime.Now.AddMinutes(1);
        private DateTime nextEnableAllowed = DateTime.MinValue;
        private readonly NetworkRefreshPolicy networkRefreshPolicy = new NetworkRefreshPolicy();
        private readonly RuleRefreshQueue ruleRefreshQueue = new RuleRefreshQueue();
        private bool dualModeSessionSuppressed;
        private bool dualRecoveryPending;
        private bool prepareNetworkModeOnce;

        public MainForm(bool autoStartLaunch, bool openNetworkMode = false, bool prepareNetworkMode = false)
        {
            prepareNetworkModeOnce = !autoStartLaunch && openNetworkMode && prepareNetworkMode;
            Text = "FastLink 直连助手";
            AppLogger.Log("UI process started. AutoStartLaunch=" + autoStartLaunch);
            Icon = SystemIcons.Shield;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(680, 560);
            Size = new Size(720, 610);
            Font = new Font("Microsoft YaHei UI", 9F);

            config = ConfigStore.Load();
            runtime = new RuntimeController();
            HealthEventLogger.Write("app_start", new Dictionary<string, object>
            {
                { "version", Assembly.GetExecutingAssembly().GetName().Version.ToString() },
                { "autostart", autoStartLaunch }
            });

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(14);
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            Controls.Add(root);

            Panel statusPanel = new Panel();
            statusPanel.Dock = DockStyle.Fill;
            statusPanel.BackColor = Color.FromArgb(245, 247, 250);
            fastLinkStatus = NewStatusLabel("Fastlink：检测中…", 14);
            directStatus = NewStatusLabel("模式一（FastLink 分流）：关闭", 42);
            networkStatus = NewStatusLabel("物理网络：尚未识别", 78);
            statusPanel.Controls.Add(fastLinkStatus);
            statusPanel.Controls.Add(directStatus);
            statusPanel.Controls.Add(networkStatus);
            Button networkModes = new Button();
            networkModes.Text = "工作模式（1 / 2 / 3）";
            networkModes.Size = new Size(190, 30);
            networkModes.Left = 14; networkModes.Top = 8;
            Button exitAssistant = new Button();
            exitAssistant.Text = "退出助手（自动清理）";
            exitAssistant.Size = new Size(190, 28);
            exitAssistant.Left = 14; exitAssistant.Top = 44;
            exitAssistant.Click += delegate { ExitAndClean(); };
            statusPanel.Resize += delegate
            {
                networkModes.Left = Math.Max(14, statusPanel.ClientSize.Width - 204);
                exitAssistant.Left = networkModes.Left;
                fastLinkStatus.Width = Math.Max(80, networkModes.Left - fastLinkStatus.Left - 12);
                directStatus.Width = Math.Max(80, exitAssistant.Left - directStatus.Left - 12);
            };
            networkModes.Click += delegate { OpenDualNetworkMode(); };
            statusPanel.Controls.Add(networkModes);
            statusPanel.Controls.Add(exitAssistant);
            root.Controls.Add(statusPanel, 0, 0);

            FlowLayoutPanel switchPanel = new FlowLayoutPanel();
            switchPanel.Dock = DockStyle.Fill;
            switchPanel.FlowDirection = FlowDirection.LeftToRight;
            mainSwitch = new CheckBox();
            mainSwitch.Appearance = Appearance.Button;
            mainSwitch.AutoSize = false;
            mainSwitch.Size = new Size(170, 38);
            mainSwitch.TextAlign = ContentAlignment.MiddleCenter;
            mainSwitch.Text = "立即开启直连";
            mainSwitch.CheckedChanged += MainSwitchChanged;
            switchPanel.Controls.Add(mainSwitch);
            autoEnable = new CheckBox();
            autoEnable.Text = "Fastlink 接通时自动开启";
            autoEnable.AutoSize = true;
            autoEnable.Padding = new Padding(12, 10, 0, 0);
            autoEnable.Checked = config.AutoEnableWithFastLink;
            autoEnable.CheckedChanged += delegate
            {
                config.AutoEnableWithFastLink = autoEnable.Checked;
                ConfigStore.Save(config);
            };
            switchPanel.Controls.Add(autoEnable);
            directPrivateNetworks = new CheckBox();
            directPrivateNetworks.Text = "直连内网网段（邮箱/公司服务）";
            directPrivateNetworks.AutoSize = true;
            directPrivateNetworks.Padding = new Padding(12, 10, 0, 0);
            directPrivateNetworks.Checked = config.DirectPrivateNetworks;
            directPrivateNetworks.CheckedChanged += delegate
            {
                config.DirectPrivateNetworks = directPrivateNetworks.Checked;
                ConfigStore.Save(config);
                RestartForRuleChange();
            };
            switchPanel.Controls.Add(directPrivateNetworks);
            autoRestartFastLink = new CheckBox();
            autoRestartFastLink.Text = "网络异常时自动重启 FastLink（默认关闭）";
            autoRestartFastLink.AutoSize = true;
            autoRestartFastLink.Padding = new Padding(12, 10, 0, 0);
            autoRestartFastLink.Checked = config.AutoRestartFastLink;
            autoRestartFastLink.CheckedChanged += delegate
            {
                config.AutoRestartFastLink = autoRestartFastLink.Checked;
                ConfigStore.Save(config);
                SafeLog(autoRestartFastLink.Checked
                    ? "已允许网络异常时自动重启 FastLink。"
                    : "已关闭 FastLink 自动重启；异常只记录日志。");
            };
            switchPanel.Controls.Add(autoRestartFastLink);
            autoStart = new CheckBox();
            autoStart.Text = "Windows 登录后自启动";
            autoStart.AutoSize = true;
            autoStart.Padding = new Padding(12, 10, 0, 0);
            autoStart.Checked = config.AutoStart;
            autoStart.CheckedChanged += AutoStartChanged;
            switchPanel.Controls.Add(autoStart);
            root.Controls.Add(switchPanel, 0, 1);

            GroupBox ruleGroup = new GroupBox();
            ruleGroup.Text = "需要直连的域名规则";
            ruleGroup.Dock = DockStyle.Fill;
            TableLayoutPanel rulesLayout = new TableLayoutPanel();
            rulesLayout.Dock = DockStyle.Fill;
            rulesLayout.Padding = new Padding(8);
            rulesLayout.ColumnCount = 3;
            rulesLayout.RowCount = 2;
            rulesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rulesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            rulesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            rulesLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rulesLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            ruleList = new ListBox();
            ruleList.Dock = DockStyle.Fill;
            ruleList.HorizontalScrollbar = true;
            foreach (string rule in config.Rules) ruleList.Items.Add(rule);
            rulesLayout.Controls.Add(ruleList, 0, 0);
            rulesLayout.SetColumnSpan(ruleList, 3);
            ruleInput = new TextBox();
            ruleInput.Dock = DockStyle.Fill;
            ruleInput.Margin = new Padding(0, 8, 8, 0);
            ruleInput.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { AddRule(); e.SuppressKeyPress = true; }
            };
            rulesLayout.Controls.Add(ruleInput, 0, 1);
            Button addButton = new Button();
            addButton.Text = "添加";
            addButton.Dock = DockStyle.Fill;
            addButton.Margin = new Padding(0, 6, 6, 0);
            addButton.Click += delegate { AddRule(); };
            rulesLayout.Controls.Add(addButton, 1, 1);
            Button removeButton = new Button();
            removeButton.Text = "删除";
            removeButton.Dock = DockStyle.Fill;
            removeButton.Margin = new Padding(0, 6, 0, 0);
            removeButton.Click += delegate { RemoveSelectedRule(); };
            rulesLayout.Controls.Add(removeButton, 2, 1);
            ruleGroup.Controls.Add(rulesLayout);
            root.Controls.Add(ruleGroup, 0, 2);

            Label help = new Label();
            help.Dock = DockStyle.Fill;
            help.Padding = new Padding(4, 8, 4, 0);
            help.Text = "示例：portal.example.com（精确）    .example.com（主域及全部子域）\r\n" +
                "*.example.com（所有子域）    10.0.0.25 或完整 URL（IP 直连）";
            root.Controls.Add(help, 0, 3);

            logBox = new TextBox();
            logBox.Dock = DockStyle.Fill;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = Color.White;
            root.Controls.Add(logBox, 0, 4);

            ContextMenuStrip trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("打开界面", null, delegate { ShowFromTray(); });
            trayMenu.Items.Add("工作模式（1 / 2 / 3）", null, delegate { ShowFromTray(); OpenDualNetworkMode(); });
            trayMenu.Items.Add("打开运行日志", null, delegate
            {
                try { Process.Start("notepad.exe", AppLogger.LogPath); } catch { }
            });
            trayMenu.Items.Add("打开健康日志", null, delegate
            {
                try { Process.Start("notepad.exe", HealthEventLogger.LogPath); } catch { }
            });
            trayMenu.Items.Add("退出并清理", null, delegate { ExitAndClean(); });
            trayIcon = new NotifyIcon();
            trayIcon.Icon = SystemIcons.Shield;
            trayIcon.Text = "FastLink 直连助手";
            trayIcon.Visible = true;
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.DoubleClick += delegate { ShowFromTray(); };

            Shown += delegate
            {
                busy = true;
                Task.Run(delegate
                {
                    // Recovery detection only: do not resume either mode across a
                    // crashed dual-network transaction or corrupt ownership file.
                    var dualRecovery = new Modes.DualNetworkRuntime(new Modes.DualNetworkWindowsPlatform(),
                        new Modes.DualFileJournal(), new Modes.DualNetworkDns());
                    dualRecoveryPending = dualRecovery.HasPending || dualRecovery.IsArmed || dualRecovery.IsActive;
                    if (dualRecoveryPending)
                    {
                        dualModeSessionSuppressed = true;
                        throw new InvalidOperationException("发现双网模式待恢复记录；请打开“工作模式（1 / 2 / 3）”执行恢复清理。旧模式已暂停。");
                    }
                    runtime.InitializeAndCleanLegacy();
                    if (config.AutoStart && !StartupTask.IsInstalled()) StartupTask.Install();
                }).ContinueWith(delegate(Task task)
                {
                    BeginInvoke((Action)delegate
                    {
                        busy = false;
                        if (task.IsFaulted)
                        {
                            dualModeSessionSuppressed = true;
                            string message = "初始化清理失败，已停止自动开启：" + task.Exception.GetBaseException().Message;
                            SafeLog(message);
                            trayIcon.ShowBalloonTip(5000, "FastLink 直连助手", message, ToolTipIcon.Error);
                            RefreshUi();
                            if (openNetworkMode && dualRecoveryPending) BeginInvoke((Action)OpenDualNetworkMode);
                            return;
                        }
                        if (openNetworkMode)
                        {
                            dualModeSessionSuppressed = true;
                            BeginInvoke((Action)OpenDualNetworkMode);
                            return;
                        }
                        monitorTimer.Start();
                        CheckFastLink();
                    });
                });
                if (autoStartLaunch) HideToTray();
            };

            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!allowExit && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    HideToTray();
                }
                else if (!allowExit)
                {
                    e.Cancel = true;
                    ExitAndClean();
                }
            };
            FormClosed += delegate
            {
                monitorTimer.Stop();
                trayIcon.Visible = false;
            };

            monitorTimer = new System.Windows.Forms.Timer();
            monitorTimer.Interval = 2500;
            monitorTimer.Tick += delegate
            {
                CheckFastLink();
                QueueRouteMaintenance();
                QueueSystemProxyMaintenance();
                QueueHealthHeartbeat();
            };
        }

        private Label NewStatusLabel(string text, int top)
        {
            Label label = new Label();
            label.AutoSize = false;
            label.Left = 14;
            label.Top = top;
            label.Width = 640;
            label.Height = 26;
            label.Text = text;
            return label;
        }

        private void QueueRouteMaintenance()
        {
            if (!NetworkRefreshPolicy.CanWork(busy, runtime.Enabled, runtime.CleanupPending) || DateTime.Now < nextRouteMaintenance) return;
            if (Interlocked.CompareExchange(ref routeMaintenanceInProgress, 1, 0) != 0) return;
            nextRouteMaintenance = DateTime.Now.AddMinutes(1);
            Task.Run(delegate { runtime.RemoveExpiredRoutes(); }).ContinueWith(delegate(Task task)
            {
                Interlocked.Exchange(ref routeMaintenanceInProgress, 0);
                if (task.IsFaulted)
                {
                    HealthEventLogger.Write("route_maintenance_failed", new Dictionary<string, object>
                    {
                        { "error", task.Exception.GetBaseException().Message }
                    });
                }
            });
        }

        private void QueueSystemProxyMaintenance()
        {
            if (!NetworkRefreshPolicy.CanWork(busy, runtime.Enabled, runtime.CleanupPending) ||
                DateTime.Now < nextSystemProxyMaintenance || !FastLinkConnectionCompatibility.GetCurrent().Active) return;
            if (Interlocked.CompareExchange(ref systemProxyMaintenanceInProgress, 1, 0) != 0) return;
            nextSystemProxyMaintenance = DateTime.Now.AddSeconds(10);
            Task.Run(delegate { runtime.MaintainSystemProxyBypass(); }).ContinueWith(delegate(Task task)
            {
                Interlocked.Exchange(ref systemProxyMaintenanceInProgress, 0);
                if (task.IsFaulted)
                {
                    string message = "系统代理例外维护失败，已暂停重试：" + task.Exception.GetBaseException().Message;
                    SafeLog(message);
                    HealthEventLogger.Write("system_proxy_bypass_failed", new Dictionary<string, object> { { "error", message } });
                }
            });
        }

        private void QueueHealthHeartbeat()
        {
            if (DateTime.Now < nextHealthHeartbeat) return;
            if (Interlocked.CompareExchange(ref healthHeartbeatInProgress, 1, 0) != 0) return;
            nextHealthHeartbeat = DateTime.Now.AddMinutes(5);
            Task.Run(delegate
            {
                HealthEventLogger.Write("health_heartbeat", BuildHealthDetails());
            }).ContinueWith(delegate { Interlocked.Exchange(ref healthHeartbeatInProgress, 0); });
        }

        private IDictionary<string, object> BuildHealthDetails()
        {
            Dictionary<string, object> details = new Dictionary<string, object>();
            try
            {
                foreach (KeyValuePair<string, object> item in runtime.GetHealthDetails()) details[item.Key] = item.Value;
            }
            catch (Exception ex) { details["runtime_snapshot_error"] = ex.Message; }

            details["fastlink_adapter_active"] = NetworkDetector.IsLegacyTunnelActive();
            FastLinkConnectionInfo compatibility = FastLinkConnectionCompatibility.GetCurrent();
            details["fastlink_system_proxy_active"] = compatibility.Active;
            details["fastlink_connection_mode"] = compatibility.Active ? "system_proxy" :
                (NetworkDetector.IsLegacyTunnelActive() ? "tun" : "inactive");
            try
            {
                Process[] fastLink = Process.GetProcessesByName("FastLink");
                Process[] cores = Process.GetProcessesByName("FastLinkCore");
                if (compatibility.Active && compatibility.CoreName == "ApexCore")
                    cores = cores.Concat(Process.GetProcessesByName("ApexCore")).ToArray();
                details["fastlink_process_count"] = fastLink.Length;
                details["fastlink_core_count"] = cores.Length;
                if (cores.Length > 0)
                {
                    details["fastlink_core_pid"] = cores[0].Id;
                    try { details["fastlink_core_working_set_mb"] = Math.Round(cores[0].WorkingSet64 / 1048576.0, 1); } catch { }
                }
                foreach (Process process in fastLink) process.Dispose();
                foreach (Process process in cores) process.Dispose();
            }
            catch (Exception ex) { details["fastlink_process_error"] = ex.Message; }

            try
            {
                TcpConnectionInformation[] connections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
                int nodeSessions = connections.Count(
                    delegate(TcpConnectionInformation connection)
                    {
                        return connection.State == TcpState.Established &&
                            (connection.RemoteEndPoint.Port == 10131 || connection.RemoteEndPoint.Port == 10141 || connection.RemoteEndPoint.Port == 10151);
                    });
                int nodeConnecting = connections.Count(delegate(TcpConnectionInformation connection)
                {
                    return connection.State == TcpState.SynSent &&
                        (connection.RemoteEndPoint.Port == 10131 || connection.RemoteEndPoint.Port == 10141 || connection.RemoteEndPoint.Port == 10151);
                });
                int outboundConnecting = connections.Count(delegate(TcpConnectionInformation connection)
                {
                    return connection.State == TcpState.SynSent && connection.RemoteEndPoint.Port == 443;
                });
                details["fastlink_node_sessions"] = nodeSessions;
                details["fastlink_node_connecting"] = nodeConnecting;
                details["outbound_443_connecting"] = outboundConnecting;
            }
            catch (Exception ex) { details["node_session_error"] = ex.Message; }
            return details;
        }

        private static void AddProbeDetails(IDictionary<string, object> details, ProxyProbeResult probe)
        {
            if (probe == null) return;
            details["probe_healthy"] = probe.Healthy;
            details["probe_timed_out"] = probe.TimedOut;
            details["probe_status_code"] = probe.StatusCode;
            details["probe_elapsed_ms"] = probe.ElapsedMilliseconds;
            details["probe_error"] = probe.Error ?? string.Empty;
        }

        private void ClearProxyIncident()
        {
            consecutiveProxyHealthFailures = 0;
            proxyHealthFailureSince = DateTime.MinValue;
            proxyIncidentId = null;
        }

        private void CheckFastLink()
        {
            if (busy || dualModeSessionSuppressed || dualRecoveryPending) return;
            bool active = NetworkDetector.IsFastLinkActive();
            FastLinkConnectionInfo connection = FastLinkConnectionCompatibility.GetCurrent();
            fastLinkStatus.Text = active ? (connection.Active ? "Fastlink：已接通（系统代理）" : "Fastlink：已接通（虚拟网卡）") : "Fastlink：未接通";
            fastLinkStatus.ForeColor = active ? Color.DarkGreen : Color.DimGray;

            // DNS must stay alive in this state, but no background operation may
            // repeat a partially completed cleanup, rule apply, or route removal.
            if (runtime.CleanupPending)
            {
                lastFastLinkActive = active;
                RefreshUi();
                return;
            }

            PhysicalNetworkInfo currentNetwork = null;
            if (active && runtime.Enabled && runtime.Network != null)
            {
                currentNetwork = NetworkDetector.Detect();
                if (currentNetwork != null &&
                    !string.Equals(currentNetwork.Identity, runtime.Network.Identity, StringComparison.Ordinal))
                {
                    physicalNetworkMissingSince = DateTime.MinValue;
                    ClearProxyIncident();
                    if (networkRefreshPolicy.Observe(runtime.Network.Identity, currentNetwork.Identity, DateTime.UtcNow))
                        BeginPhysicalNetworkRefresh();
                    lastFastLinkActive = active;
                    return;
                }

                networkRefreshPolicy.ResetCandidate();
                if (currentNetwork == null)
                {
                    ClearProxyIncident();
                    if (physicalNetworkMissingSince == DateTime.MinValue)
                    {
                        physicalNetworkMissingSince = DateTime.Now;
                        SafeLog("检测到物理网络失去有效 IP，正在观察…");
                    }
                    networkStatus.Text = "物理网络：连接异常，等待自动恢复";
                    networkStatus.ForeColor = Color.DarkOrange;
                    if (DateTime.Now - physicalNetworkMissingSince >= TimeSpan.FromSeconds(12) &&
                        DateTime.Now >= nextNetworkRecoveryAllowed)
                    {
                        PhysicalNetworkInfo replacement = NetworkDetector.Detect();
                        if (replacement != null)
                        {
                            physicalNetworkMissingSince = DateTime.MinValue;
                            // Let the next tick observe and stabilize the replacement.
                            networkRefreshPolicy.ResetCandidate();
                        }
                        else if (CanAutomaticallyRecoverFastLink())
                        {
                            BeginFastLinkRecovery();
                        }
                        else if (DateTime.Now >= nextNetworkRecoveryAllowed)
                        {
                            nextNetworkRecoveryAllowed = DateTime.Now.AddMinutes(10);
                            SafeLog("物理网络持续异常；FastLink 自动重启已关闭，仅记录监控。");
                            HealthEventLogger.Write("network_recovery_suppressed", new Dictionary<string, object>
                            {
                                { "reason", "user_disabled" },
                                { "failure_duration_seconds", Math.Round((DateTime.Now - physicalNetworkMissingSince).TotalSeconds, 1) }
                            });
                        }
                    }
                    lastFastLinkActive = active;
                    return;
                }
            }
            physicalNetworkMissingSince = DateTime.MinValue;
            networkStatus.ForeColor = SystemColors.ControlText;

            if (!active)
            {
                ClearProxyIncident();
                manualOffUntilReconnect = false;
                if (runtime.Enabled) BeginDisable("Fastlink 已断开，直连规则已自动清理。");
            }
            else if (!lastFastLinkActive && config.AutoEnableWithFastLink && !manualOffUntilReconnect && !runtime.CleanupPending)
            {
                networkRefreshPolicy.Reset();
                if (NetworkDetector.Detect() != null)
                    BeginEnable("检测到 Fastlink，正在自动开启直连…");
            }
            else if (active && config.AutoEnableWithFastLink && !runtime.Enabled && !manualOffUntilReconnect && !runtime.CleanupPending)
            {
                if (NetworkDetector.Detect() != null)
                    BeginEnable("正在自动开启直连…");
            }
            else if (active && runtime.Enabled && currentNetwork != null)
            {
                ApplyPendingRuleChanges();
                if (!busy) QueueProxyHealthCheck();
            }
            lastFastLinkActive = active;
            RefreshUi();
        }

        private void BeginPhysicalNetworkRefresh()
        {
            if (!NetworkRefreshPolicy.CanWork(busy, runtime.Enabled, runtime.CleanupPending) || networkRefreshPolicy.Paused) return;
            busy = true;
            RuleSet rules = CurrentRules();
            long rulesRevision = ruleRefreshQueue.RequestedRevision;
            bool privateNetworks = config.DirectPrivateNetworks;
            Stopwatch refreshWatch = Stopwatch.StartNew();
            HealthEventLogger.Write("network_refresh_started", new Dictionary<string, object>
            {
                { "previous_network", runtime.Network == null ? string.Empty : runtime.Network.Identity },
                { "rule_count", rules.NormalizedRules.Count }
            });
            SafeLog("检测到 Wi-Fi、DNS 或网关变化，正在刷新直连规则…");
            Task.Run(delegate
            {
                runtime.Restart(rules, privateNetworks);
            }).ContinueWith(delegate(Task task)
            {
                BeginInvoke((Action)delegate
                {
                    busy = false;
                    ruleRefreshQueue.Complete(rulesRevision, !task.IsFaulted);
                    if (task.IsFaulted)
                    {
                        networkRefreshPolicy.Pause();
                        manualOffUntilReconnect = true;
                        string message = "网络切换后刷新失败，已暂停自动重试；请点击主开关处理：" + task.Exception.GetBaseException().Message;
                        SafeLog(message);
                        HealthEventLogger.Write("network_refresh_failed", new Dictionary<string, object>
                        {
                            { "elapsed_ms", refreshWatch.ElapsedMilliseconds }, { "cleanup_pending", runtime.CleanupPending },
                            { "auto_retry_paused", true }, { "error", task.Exception.GetBaseException().Message }
                        });
                        trayIcon.ShowBalloonTip(5000, "FastLink 直连助手", message, ToolTipIcon.Error);
                    }
                    else
                    {
                        networkRefreshPolicy.Reset();
                        HealthEventLogger.Write("network_refresh_completed", new Dictionary<string, object> { { "elapsed_ms", refreshWatch.ElapsedMilliseconds } });
                        SafeLog("网络已切换，直连规则已刷新。");
                    }
                    RefreshUi();
                });
            });
        }

        private void QueueProxyHealthCheck()
        {
            if (proxyHealthCheckInProgress || DateTime.Now < nextProxyHealthCheck) return;
            proxyHealthCheckInProgress = true;
            nextProxyHealthCheck = DateTime.Now.AddSeconds(25);

            Task.Run(delegate
            {
                return ProxyHealthProbe.Probe();
            }).ContinueWith(delegate(Task<ProxyProbeResult> task)
            {
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        proxyHealthCheckInProgress = false;
                        if (!NetworkDetector.IsFastLinkActive() || !runtime.Enabled)
                        {
                            ClearProxyIncident();
                            return;
                        }

                        ProxyProbeResult probe = task.IsFaulted ? new ProxyProbeResult
                        {
                            Healthy = false,
                            Error = task.Exception.GetBaseException().Message
                        } : task.Result;
                        if (probe.Healthy)
                        {
                            if (consecutiveProxyHealthFailures > 0)
                            {
                                SafeLog("FastLink 代理健康检查已恢复。");
                                Dictionary<string, object> recovered = new Dictionary<string, object>();
                                recovered["incident_id"] = proxyIncidentId ?? string.Empty;
                                recovered["failure_count"] = consecutiveProxyHealthFailures;
                                recovered["failure_duration_seconds"] = proxyHealthFailureSince == DateTime.MinValue
                                    ? 0 : Math.Round((DateTime.Now - proxyHealthFailureSince).TotalSeconds, 1);
                                AddProbeDetails(recovered, probe);
                                HealthEventLogger.Write("proxy_probe_recovered", recovered);
                            }
                            ClearProxyIncident();
                            return;
                        }

                        if (proxyHealthFailureSince == DateTime.MinValue)
                        {
                            proxyHealthFailureSince = DateTime.Now;
                            proxyIncidentId = Guid.NewGuid().ToString("N");
                        }
                        consecutiveProxyHealthFailures++;
                        Dictionary<string, object> failed = new Dictionary<string, object>();
                        failed["incident_id"] = proxyIncidentId;
                        failed["failure_count"] = consecutiveProxyHealthFailures;
                        failed["failure_duration_seconds"] = Math.Round((DateTime.Now - proxyHealthFailureSince).TotalSeconds, 1);
                        AddProbeDetails(failed, probe);
                        HealthEventLogger.Write("proxy_probe_failed", failed);
                        if (consecutiveProxyHealthFailures == 1)
                            SafeLog("FastLink 显示已连接，但代理访问失败，正在复核…");

                        if (consecutiveProxyHealthFailures >= 3 &&
                            DateTime.Now >= nextProxyRecoveryAllowed &&
                            !busy && CanAutomaticallyRecoverFastLink())
                        {
                            BeginProxyRecovery();
                        }
                        else if (consecutiveProxyHealthFailures >= 3 &&
                            !config.AutoRestartFastLink &&
                            (consecutiveProxyHealthFailures == 3 || consecutiveProxyHealthFailures % 12 == 0))
                        {
                            SafeLog("代理持续异常；FastLink 自动重启已关闭，仅记录监控。");
                            HealthEventLogger.Write("proxy_recovery_suppressed", new Dictionary<string, object>
                            {
                                { "incident_id", proxyIncidentId ?? string.Empty },
                                { "failure_count", consecutiveProxyHealthFailures },
                                { "failure_duration_seconds", Math.Round((DateTime.Now - proxyHealthFailureSince).TotalSeconds, 1) },
                                { "reason", "user_disabled" }
                            });
                        }
                    });
                }
                catch
                {
                    proxyHealthCheckInProgress = false;
                }
            });
        }

        private bool CanAutomaticallyRecoverFastLink()
        {
            return config.AutoRestartFastLink && NetworkDetector.IsLegacyTunnelActive() &&
                FastLinkConnectionCompatibility.CanUseLegacyRecovery();
        }

        private void BeginProxyRecovery()
        {
            if (!CanAutomaticallyRecoverFastLink() || busy || runtime.Network == null) return;
            busy = true;
            nextProxyRecoveryAllowed = DateTime.Now.AddMinutes(5);
            nextProxyHealthCheck = DateTime.Now.AddMinutes(1);
            string interfaceName = runtime.Network.InterfaceName;
            string originalIdentity = runtime.Network.Identity;
            RuleSet rules = CurrentRules();
            string incidentId = proxyIncidentId ?? Guid.NewGuid().ToString("N");
            double failureDuration = proxyHealthFailureSince == DateTime.MinValue
                ? 0 : (DateTime.Now - proxyHealthFailureSince).TotalSeconds;
            SafeLog("代理连续检测失败，正在自动重启 FastLink 核心…");
            HealthEventLogger.Write("proxy_recovery_started", new Dictionary<string, object>
            {
                { "incident_id", incidentId },
                { "failure_count", consecutiveProxyHealthFailures },
                { "failure_duration_seconds", Math.Round(failureDuration, 1) },
                { "strategy", "restart_fastlink_keep_direct_rules" },
                { "physical_interface", interfaceName }
            });

            Task.Run(delegate
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                FastLinkRecoveryResult recovery = FastLinkRecovery.RestartFastLink(interfaceName);
                if (recovery.FastLinkRecovered)
                {
                    ProxyProbeResult verification = null;
                    int consecutiveHealthy = 0;
                    for (int attempt = 0; attempt < 6; attempt++)
                    {
                        verification = ProxyHealthProbe.Probe();
                        if (verification.Healthy) consecutiveHealthy++;
                        else consecutiveHealthy = 0;
                        if (consecutiveHealthy >= 2) break;
                        if (attempt < 5) Thread.Sleep(3000);
                    }
                    recovery.ProxyProbe = verification;
                    recovery.ProxyVerificationSuccesses = consecutiveHealthy;
                    if (verification == null || consecutiveHealthy < 2)
                    {
                        recovery.FastLinkRecovered = false;
                        recovery.Detail = "FastLink restarted, but two consecutive proxy checks did not succeed.";
                    }
                    else
                    {
                        PhysicalNetworkInfo detected = NetworkDetector.Detect();
                        if (detected != null && !string.Equals(detected.Identity, originalIdentity, StringComparison.Ordinal))
                        {
                            runtime.Restart(rules, config.DirectPrivateNetworks);
                            recovery.DirectRulesRebuilt = true;
                        }
                    }
                }
                stopwatch.Stop();
                recovery.RecoveryElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                return recovery;
            }).ContinueWith(delegate(Task<FastLinkRecoveryResult> task)
            {
                BeginInvoke((Action)delegate
                {
                    busy = false;
                    if (task.IsFaulted)
                    {
                        string message = "代理自动恢复失败：" + task.Exception.GetBaseException().Message;
                        SafeLog(message);
                        HealthEventLogger.Write("proxy_recovery_failed", new Dictionary<string, object>
                        {
                            { "incident_id", incidentId },
                            { "error", task.Exception.GetBaseException().Message }
                        });
                        trayIcon.ShowBalloonTip(6000, "FastLink 直连助手", message, ToolTipIcon.Error);
                    }
                    else if (task.Result.FastLinkRecovered && runtime.Enabled)
                    {
                        lastProxyRecoveryCompleted = DateTime.Now;
                        nextProxyRecoveryAllowed = DateTime.Now.AddMinutes(5);
                        SafeLog(task.Result.DirectRulesRebuilt
                            ? "FastLink 核心已恢复；因网络发生变化，直连规则也已刷新。"
                            : "FastLink 核心已自动恢复，直连规则全程保持可用。");
                        Dictionary<string, object> completed = new Dictionary<string, object>();
                        completed["incident_id"] = incidentId;
                        completed["recovery_elapsed_ms"] = task.Result.RecoveryElapsedMilliseconds;
                        completed["direct_rules_rebuilt"] = task.Result.DirectRulesRebuilt;
                        completed["verification_successes"] = task.Result.ProxyVerificationSuccesses;
                        completed["detail"] = task.Result.Detail ?? string.Empty;
                        AddProbeDetails(completed, task.Result.ProxyProbe);
                        HealthEventLogger.Write("proxy_recovery_completed", completed);
                        trayIcon.ShowBalloonTip(5000, "FastLink 直连助手",
                            "检测到代理假连接，FastLink 已自动重启", ToolTipIcon.Info);
                    }
                    else
                    {
                        nextProxyRecoveryAllowed = DateTime.Now.AddMinutes(10);
                        SafeLog("代理自动恢复未成功：" + task.Result.Detail);
                        Dictionary<string, object> failed = new Dictionary<string, object>();
                        failed["incident_id"] = incidentId;
                        failed["recovery_elapsed_ms"] = task.Result.RecoveryElapsedMilliseconds;
                        failed["detail"] = task.Result.Detail ?? string.Empty;
                        failed["verification_successes"] = task.Result.ProxyVerificationSuccesses;
                        AddProbeDetails(failed, task.Result.ProxyProbe);
                        HealthEventLogger.Write("proxy_recovery_failed", failed);
                        trayIcon.ShowBalloonTip(7000, "FastLink 直连助手",
                            "FastLink 仍不可用，请检查节点或服务状态。", ToolTipIcon.Warning);
                    }
                    ClearProxyIncident();
                    RefreshUi();
                });
            });
        }

        private void BeginFastLinkRecovery()
        {
            if (!CanAutomaticallyRecoverFastLink() || busy || runtime.Network == null) return;
            busy = true;
            nextNetworkRecoveryAllowed = DateTime.Now.AddMinutes(5);
            string interfaceName = runtime.Network.InterfaceName;
            string interfaceId = runtime.Network.InterfaceId;
            string preferredWifiSsid = runtime.Network.WifiSsid;
            string preferredWifiProfile = runtime.Network.WifiProfile;
            RuleSet rules = CurrentRules();
            SafeLog(string.IsNullOrWhiteSpace(preferredWifiSsid)
                ? "正在自动重启 FastLink 并恢复 WLAN…"
                : "正在恢复原 Wi-Fi（" + preferredWifiSsid + "）并重启 FastLink…");

            Task.Run(delegate
            {
                runtime.Disable();
                FastLinkRecoveryResult recovery = FastLinkRecovery.Recover(
                    interfaceName, interfaceId, preferredWifiSsid, preferredWifiProfile);
                if (recovery.FastLinkRecovered)
                    runtime.Enable(rules, config.DirectPrivateNetworks);
                return recovery;
            }).ContinueWith(delegate(Task<FastLinkRecoveryResult> task)
            {
                BeginInvoke((Action)delegate
                {
                    busy = false;
                    physicalNetworkMissingSince = DateTime.MinValue;
                    if (task.IsFaulted)
                    {
                        string message = "自动恢复失败：" + task.Exception.GetBaseException().Message;
                        SafeLog(message);
                        trayIcon.ShowBalloonTip(5000, "FastLink 直连助手", message, ToolTipIcon.Error);
                    }
                    else if (task.Result.FastLinkRecovered && runtime.Enabled && task.Result.UsedFallbackWifi)
                    {
                        string fallback = string.IsNullOrWhiteSpace(task.Result.RecoveredWifiSsid)
                            ? "其他 Wi-Fi" : task.Result.RecoveredWifiSsid;
                        SafeLog("原 Wi-Fi 暂不可用，当前临时连接到 " + fallback + "。");
                        trayIcon.ShowBalloonTip(7000, "FastLink 直连助手",
                            "已临时连接 " + fallback + "；原 Wi-Fi " +
                            task.Result.PreferredWifiSsid + " 暂不可用。", ToolTipIcon.Warning);
                    }
                    else if (task.Result.FastLinkRecovered && runtime.Enabled)
                    {
                        SafeLog("WLAN、FastLink 和直连规则已自动恢复。");
                        trayIcon.ShowBalloonTip(4000, "FastLink 直连助手",
                            "网络、FastLink 和直连规则已自动恢复", ToolTipIcon.Info);
                    }
                    else if (task.Result.PhysicalNetworkRecovered)
                    {
                        SafeLog("WLAN 已恢复，但 FastLink 未自动连接。");
                        trayIcon.ShowBalloonTip(6000, "FastLink 直连助手",
                            "WLAN 已恢复，请检查 FastLink 自动运行设置。", ToolTipIcon.Warning);
                    }
                    else
                    {
                        SafeLog("自动恢复未成功：" + task.Result.Detail);
                        trayIcon.ShowBalloonTip(6000, "FastLink 直连助手",
                            "WLAN 仍未恢复，请检查 Wi-Fi。", ToolTipIcon.Error);
                    }
                    RefreshUi();
                });
            });
        }

        private void MainSwitchChanged(object sender, EventArgs e)
        {
            if (dualRecoveryPending) { SafeLog("请先在双网模式窗口完成恢复清理。"); return; }
            if (!busy) dualModeSessionSuppressed = false;
            if (busy) return;
            networkRefreshPolicy.Reset();
            if (runtime.CleanupPending)
            {
                manualOffUntilReconnect = true;
                BeginDisable("残留清理已完成。");
                return;
            }
            if (mainSwitch.Checked)
            {
                manualOffUntilReconnect = false;
                BeginEnable("正在开启直连…");
            }
            else if (runtime.Enabled)
            {
                manualOffUntilReconnect = true;
                BeginDisable("已手动关闭直连；Fastlink 下次重新接通时会自动开启。");
            }
        }

        private RuleSet CurrentRules()
        {
            return new RuleSet(ruleList.Items.Cast<string>().ToList());
        }

        private void BeginEnable(string message)
        {
            if (busy || runtime.Enabled || runtime.CleanupPending || DateTime.Now < nextEnableAllowed) return;
            busy = true;
            SafeLog(message);
            RuleSet rules = CurrentRules();
            long rulesRevision = ruleRefreshQueue.RequestedRevision;
            bool privateNetworks = config.DirectPrivateNetworks;
            Task.Run(delegate { runtime.Enable(rules, privateNetworks); }).ContinueWith(delegate(Task task)
            {
                BeginInvoke((Action)delegate
                {
                    busy = false;
                    ruleRefreshQueue.Complete(rulesRevision, !task.IsFaulted);
                    if (task.IsFaulted)
                    {
                        nextEnableAllowed = DateTime.Now.AddMinutes(1);
                        manualOffUntilReconnect = true;
                        Exception error = task.Exception.GetBaseException();
                        SafeLog("开启失败，已暂停自动重试；请手动重试或等待 FastLink 下次接通：" + error.Message);
                        trayIcon.ShowBalloonTip(4000, "FastLink 直连助手", "开启失败，已暂停自动重试：" + error.Message, ToolTipIcon.Error);
                    }
                    else
                    {
                        SafeLog("直连已开启。规则数：" + rules.NormalizedRules.Count);
                        trayIcon.ShowBalloonTip(2500, "FastLink 直连助手", "直连模式已自动开启", ToolTipIcon.Info);
                    }
                    RefreshUi();
                });
            });
        }

        private void BeginDisable(string message)
        {
            if (busy) return;
            busy = true;
            Task.Run(delegate { runtime.Disable(); }).ContinueWith(delegate(Task task)
            {
                BeginInvoke((Action)delegate
                {
                    busy = false;
                    SafeLog(task.IsFaulted ? "清理失败：" + task.Exception.GetBaseException().Message : message);
                    RefreshUi();
                });
            });
        }

        private void RestartForRuleChange()
        {
            ruleRefreshQueue.Request();
            HealthEventLogger.Write("rule_change_queued", new Dictionary<string, object>
            {
                { "requested_revision", ruleRefreshQueue.RequestedRevision },
                { "applied_revision", ruleRefreshQueue.AppliedRevision },
                { "rule_count", ruleList.Items.Count }, { "busy", busy },
                { "cleanup_pending", runtime.CleanupPending }, { "paused", ruleRefreshQueue.Paused }
            });
            if (runtime.CleanupPending)
            {
                SafeLog("规则已保存；清理未完成，暂不应用。请先点击“重试安全清理”。");
                RefreshUi();
                return;
            }
            if (ruleRefreshQueue.Paused)
                SafeLog("规则已保存；上次刷新失败，已暂停自动重试。请关闭再开启直连。");
            else if (busy)
                SafeLog("规则已保存并排队；当前操作完成后会自动应用最新完整列表。");
            else if (!runtime.Enabled)
                SafeLog("规则已保存；下次开启直连时应用。");
            // The existing timer drains pending changes after any busy operation,
            // including enable/disable and network refresh. Reuse network checks
            // here so edits cannot bypass the eight-second stability window.
            if (!busy && runtime.Enabled) CheckFastLink();
            RefreshUi();
        }

        private void ApplyPendingRuleChanges()
        {
            if (networkRefreshPolicy.Paused ||
                !ruleRefreshQueue.CanApply(busy, runtime.Enabled, runtime.CleanupPending)) return;
            busy = true;
            RuleSet rules = CurrentRules();
            long rulesRevision = ruleRefreshQueue.RequestedRevision;
            bool privateNetworks = config.DirectPrivateNetworks;
            Stopwatch watch = Stopwatch.StartNew();
            SafeLog("规则已修改，正在刷新…");
            HealthEventLogger.Write("rule_refresh_started", new Dictionary<string, object>
            {
                { "revision", rulesRevision }, { "rule_count", rules.NormalizedRules.Count },
                { "namespace_count", rules.GetNrptNamespaces().Count }, { "private_networks", privateNetworks }
            });
            Task.Run(delegate { runtime.UpdateRules(rules, privateNetworks); }).ContinueWith(delegate(Task task)
            {
                BeginInvoke((Action)delegate
                {
                    busy = false;
                    ruleRefreshQueue.Complete(rulesRevision, !task.IsFaulted);
                    HealthEventLogger.Write(task.IsFaulted ? "rule_refresh_failed" : "rule_refresh_completed",
                        new Dictionary<string, object>
                        {
                            { "revision", rulesRevision }, { "requested_revision", ruleRefreshQueue.RequestedRevision },
                            { "pending", ruleRefreshQueue.HasPending }, { "paused", ruleRefreshQueue.Paused },
                            { "rule_count", rules.NormalizedRules.Count }, { "elapsed_ms", watch.ElapsedMilliseconds },
                            { "error", task.IsFaulted ? task.Exception.GetBaseException().Message : string.Empty }
                        });
                    if (task.IsFaulted)
                        SafeLog("刷新失败，已暂停自动重试；请通过主开关处理：" + task.Exception.GetBaseException().Message);
                    else
                        SafeLog(ruleRefreshQueue.HasPending
                            ? "本批规则已刷新；还有新修改排队，将继续应用最新完整列表。"
                            : "规则刷新完成，最新完整列表已生效。");
                    RefreshUi();
                });
            });
        }

        private void AddRule()
        {
            RulePattern pattern;
            string error;
            if (!RulePattern.TryCreate(ruleInput.Text, out pattern, out error))
            {
                MessageBox.Show(this, error, "规则无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (ruleList.Items.Cast<string>().Any(delegate(string value)
            {
                return string.Equals(value, pattern.Text, StringComparison.OrdinalIgnoreCase);
            }))
            {
                ruleInput.Clear();
                return;
            }
            ruleList.Items.Add(pattern.Text);
            ruleInput.Clear();
            SaveRules();
            RestartForRuleChange();
        }

        private void RemoveSelectedRule()
        {
            if (ruleList.SelectedIndex < 0) return;
            ruleList.Items.RemoveAt(ruleList.SelectedIndex);
            SaveRules();
            RestartForRuleChange();
        }

        private void SaveRules()
        {
            config.Rules = ruleList.Items.Cast<string>().ToList();
            ConfigStore.Save(config);
        }

        private void AutoStartChanged(object sender, EventArgs e)
        {
            if (busy) return;
            config.AutoStart = autoStart.Checked;
            ConfigStore.Save(config);
            Task.Run(delegate
            {
                try
                {
                    if (config.AutoStart) StartupTask.Install(); else StartupTask.Uninstall();
                    SafeLog(config.AutoStart ? "已启用 Windows 自启动。" : "已关闭 Windows 自启动。");
                }
                catch (Exception ex) { SafeLog(ex.Message); }
            });
        }

        private void RefreshUi()
        {
            bool recoverySupported = NetworkDetector.IsLegacyTunnelActive() && FastLinkConnectionCompatibility.CanUseLegacyRecovery();
            autoRestartFastLink.Enabled = recoverySupported;
            autoRestartFastLink.Text = recoverySupported ? "网络异常时自动重启 FastLink（默认关闭）" : "当前模式：自动重启暂不启用";
            directStatus.Text = runtime.CleanupPending ? "模式一：清理未完成，请重试" : (runtime.Enabled ? "模式一（FastLink 分流）：已开启" : "模式一（FastLink 分流）：关闭");
            directStatus.ForeColor = runtime.CleanupPending ? Color.DarkOrange : (runtime.Enabled ? Color.DarkGreen : Color.DimGray);
            if (!runtime.CleanupPending && runtime.Enabled && ruleRefreshQueue.HasPending)
            {
                directStatus.Text = ruleRefreshQueue.Paused
                    ? "模式一：规则刷新失败，请关闭再开启"
                    : (busy ? "模式一：正在刷新规则（新修改会自动排队）" : "模式一：规则等待刷新");
                directStatus.ForeColor = Color.DarkOrange;
            }
            if (runtime.Network != null)
            {
                string networkName = runtime.Network.InterfaceName;
                if (!string.IsNullOrWhiteSpace(runtime.Network.WifiSsid))
                    networkName += "（" + runtime.Network.WifiSsid + "）";
                networkStatus.Text = string.Format("物理网络：{0}    DNS：{1}    网关：{2}",
                    networkName, runtime.Network.DnsServer, runtime.Network.Gateway);
            }
            else networkStatus.Text = "物理网络：尚未启用";

            mainSwitch.CheckedChanged -= MainSwitchChanged;
            mainSwitch.Checked = runtime.Enabled;
            mainSwitch.Text = runtime.CleanupPending ? "重试安全清理" : (runtime.Enabled ? "关闭直连" : "立即开启直连");
            mainSwitch.BackColor = runtime.Enabled ? Color.FromArgb(210, 242, 218) : SystemColors.Control;
            mainSwitch.CheckedChanged += MainSwitchChanged;
            mainSwitch.Enabled = !dualRecoveryPending;
            if (dualRecoveryPending) directStatus.Text = "双网模式：待恢复，请打开网络模式窗口";
        }

        private void SafeLog(string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((Action)delegate { SafeLog(message); }); } catch { }
                return;
            }
            logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            AppLogger.Log(message);
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
        }

        private void ShowFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ExitAndClean()
        {
            if (dualRecoveryPending)
            {
                SafeLog("双网恢复尚未确认，请先打开网络模式窗口清理；不会提前退出。");
                return;
            }
            if (busy)
            {
                SafeLog("当前操作尚未完成，请稍后重试退出。");
                return;
            }
            busy = true;
            SafeLog("正在安全退出：先清理并核实 DNS 规则，再停止服务。");
            Task.Run(delegate { runtime.Disable(); }).ContinueWith(delegate(Task task)
            {
                BeginInvoke((Action)delegate
                {
                    busy = false;
                    if (task.IsFaulted)
                    {
                        string message = "安全清理未完成，助手保持运行，请重试退出：" + task.Exception.GetBaseException().Message;
                        SafeLog(message);
                        trayIcon.ShowBalloonTip(5000, "FastLink 直连助手", message, ToolTipIcon.Error);
                        RefreshUi();
                        return;
                    }
                    allowExit = true;
                    Close();
                });
            });
        }

        private void OpenDualNetworkMode()
        {
            if (busy) { SafeLog("当前操作未完成，请稍后进入网络模式。"); return; }
            busy = true;
            bool wasMonitoring = monitorTimer.Enabled;
            monitorTimer.Stop();
            if ((runtime.Enabled || runtime.CleanupPending) && MessageBox.Show(this,
                "进入双网模式前，需要先安全停用旧的 FastLink 直连分流。不会关闭 FastLink 或切换 Wi-Fi，但已有公司连接可能受影响。继续？",
                "切换模式前确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                busy = false;
                if (wasMonitoring) monitorTimer.Start();
                return;
            }
            dualModeSessionSuppressed = true;
            manualOffUntilReconnect = true;
            Task.Run(delegate
            {
                if (runtime.Enabled || runtime.CleanupPending) runtime.Disable();
            }).ContinueWith(task => BeginInvoke((Action)delegate
            {
                if (task.IsFaulted)
                {
                    busy = false;
                    SafeLog("旧模式清理未完成，不能进入双网模式：" + task.Exception.GetBaseException().Message);
                    RefreshUi();
                    return;
                }
                bool exitRequested = false;
                try
                {
                    // Modal ownership keeps the legacy monitor/UI from mutating
                    // routes until the dual session has verified its own cleanup.
                    bool legacyRequested = false;
                    bool prepareOnce = prepareNetworkModeOnce;
                    prepareNetworkModeOnce = false;
                    using (Modes.DualModeForm form = new Modes.DualModeForm(config.Rules, SafeLog, prepareOnOpen: prepareOnce))
                    {
                        bool mainTrayWasVisible = trayIcon.Visible;
                        bool mainTaskbarWasVisible = ShowInTaskbar;
                        try
                        {
                            // The modal mode window owns the one visible tray
                            // icon. Its minimize/restore never starts cleanup.
                            trayIcon.Visible = false;
                            ShowInTaskbar = false;
                            form.ShowDialog(this);
                        }
                        finally
                        {
                            trayIcon.Visible = mainTrayWasVisible;
                            ShowInTaskbar = mainTaskbarWasVisible;
                        }
                        dualRecoveryPending = form.HasOwnedState;
                        legacyRequested = form.RequestedLegacyMode && !dualRecoveryPending;
                        exitRequested = form.RequestedApplicationExit && !dualRecoveryPending;
                    }
                    // A selection is not permission to start the old controller.
                    // Keep suppression until the user clicks its main switch.
                    if (!exitRequested) SafeLog(legacyRequested
                        ? "已切回模式一界面，原规则保留。连接 FastLink 后，请点击“立即开启直连”；尚未自动启用任何分流。"
                        : "已返回主界面；原自动分流保持暂停，需点击主开关才重新启用。");
                }
                catch (Exception error)
                {
                    // A malformed profile alone must not invent a network
                    // transaction. Read the journal; ambiguous reads still gate
                    // the original controller conservatively.
                    try
                    {
                        var recovery = new Modes.DualNetworkRuntime(new Modes.DualNetworkWindowsPlatform(),
                            new Modes.DualFileJournal(), new Modes.DualNetworkDns());
                        dualRecoveryPending = recovery.HasPending;
                    }
                    catch { dualRecoveryPending = true; }
                    SafeLog("双网界面未能正常打开；旧模式仍暂停" + (dualRecoveryPending ? "，保留待恢复状态" : "，没有发现网络事务记录") + "：" + error.Message);
                }
                finally { busy = false; RefreshUi(); if (!exitRequested) monitorTimer.Start(); }
                if (exitRequested) ExitAndClean();
            }));
        }
    }
}
