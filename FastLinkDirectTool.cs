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

                MainForm form = new MainForm(autoStartLaunch);
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

    internal sealed class AppConfig
    {
        public bool AutoStart { get; set; }
        public bool AutoEnableWithFastLink { get; set; }
        public bool DirectPrivateNetworks { get; set; }
        public List<string> Rules { get; set; }

        public AppConfig()
        {
            AutoStart = true;
            AutoEnableWithFastLink = true;
            DirectPrivateNetworks = false;
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
    }

    internal static class ProcessRunner
    {
        public static ProcessResult Run(string fileName, string arguments, int timeoutMilliseconds)
        {
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
                    lock (outputSync)
                    lock (errorSync)
                    {
                        return new ProcessResult
                        {
                            ExitCode = -1,
                            Output = output.ToString(),
                            Error = error.ToString() + "Operation timed out."
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
                        Error = error.ToString()
                    };
                }
            }
        }

        public static ProcessResult RunPowerShell(string script, int timeoutMilliseconds)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, timeoutMilliseconds);
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

    internal sealed class PhysicalNetworkInfo
    {
        public string InterfaceName;
        public int InterfaceIndex;
        public IPAddress LocalAddress;
        public IPAddress Gateway;
        public IPAddress DnsServer;
        public IPAddress FastLinkDns;

        public string Identity
        {
            get
            {
                return InterfaceIndex.ToString(CultureInfo.InvariantCulture) + "|" +
                    Gateway + "|" + DnsServer + "|" + LocalAddress;
            }
        }
    }

    internal static class NetworkDetector
    {
        public static bool IsFastLinkActive()
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

        public static PhysicalNetworkInfo Detect()
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
                candidate.InterfaceIndex = ipv4.Index;
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
            selected.FastLinkDns = fastLinkDns ?? IPAddress.Parse("198.18.0.2");
            return selected;
        }

        private static bool IsFastLinkRange(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19);
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
        private RouteState state;
        private PhysicalNetworkInfo network;

        public RouteManager()
        {
            state = ConfigStore.LoadJson<RouteState>(ConfigStore.RouteStatePath) ?? new RouteState();
            if (state.Routes == null) state.Routes = new List<TrackedRoute>();
        }

        public void SetNetwork(PhysicalNetworkInfo value)
        {
            lock (sync) { network = value; }
        }

        public bool EnsureDnsRoute()
        {
            lock (sync)
            {
                if (network == null) return false;
                return EnsureRouteLocked(network.DnsServer, 0, true);
            }
        }

        public bool EnsureAddressRoute(IPAddress address, uint ttl)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork) return true;
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
            lock (sync)
            {
                if (network == null) return false;
                return EnsureRouteLocked(address, 0, true);
            }
        }

        public bool EnsurePrivateNetworkRoutes()
        {
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
            if (HasPhysicalRoute(destination, netmask)) return true;

            string arguments = string.Format(CultureInfo.InvariantCulture,
                "ADD {0} MASK {1} {2} METRIC 1 IF {3}",
                destination, netmask, network.Gateway, network.InterfaceIndex);
            ProcessResult result = ProcessRunner.Run("route.exe", arguments, 10000);
            if (result.ExitCode != 0 && !HasPhysicalRoute(destination, netmask)) return false;
            if (result.ExitCode == 0)
            {
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
                if (expiresTicks > tracked.ExpiresUtcTicks) tracked.ExpiresUtcTicks = expiresTicks;
                SaveLocked();
                return true;
            }

            if (HasPhysicalHostRoute(address)) return true;

            string arguments = string.Format(CultureInfo.InvariantCulture,
                "ADD {0} MASK 255.255.255.255 {1} METRIC 1 IF {2}",
                address, network.Gateway, network.InterfaceIndex);
            ProcessResult result = ProcessRunner.Run("route.exe", arguments, 10000);
            if (result.ExitCode != 0 && !HasPhysicalHostRoute(address)) return false;
            if (result.ExitCode == 0)
            {
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
            ProcessResult result = ProcessRunner.Run("route.exe", "PRINT -4 " + destination, 5000);
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
                    DeleteRoute(route);
                    state.Routes.Remove(route);
                }
                if (expired.Count > 0) SaveLocked();
            }
        }

        public void RemoveAll()
        {
            lock (sync)
            {
                foreach (TrackedRoute route in state.Routes.ToList()) DeleteRoute(route);
                state.Routes.Clear();
                SaveLocked();
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
                    DeleteRoute(route);
                    state.Routes.Remove(route);
                }
                if (privateRoutes.Count > 0) SaveLocked();
            }
        }

        private void DeleteRoute(TrackedRoute route)
        {
            string netmask = string.IsNullOrWhiteSpace(route.Netmask) ? "255.255.255.255" : route.Netmask;
            string arguments = string.Format(CultureInfo.InvariantCulture,
                "DELETE {0} MASK {1} {2} IF {3}",
                route.Address, netmask, route.Gateway, route.InterfaceIndex);
            ProcessRunner.Run("route.exe", arguments, 10000);
        }

        private void SaveLocked()
        {
            ConfigStore.SaveJson(ConfigStore.RouteStatePath, state);
        }
    }

    internal static class NrptManager
    {
        private const string Marker = "FastLinkDirectTool";
        private const string AppliedMarker = "__FASTLINK_NRPT_APPLIED__";

        public static void Apply(IEnumerable<string> namespaces)
        {
            StringBuilder script = new StringBuilder();
            script.AppendLine("$ErrorActionPreference='Stop'");
            AppendRemove(script);
            foreach (string value in namespaces.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string safe = value.Replace("'", "''");
                script.AppendLine("Add-DnsClientNrptRule -Namespace '" + safe + "' -NameServers '127.0.0.1' -DisplayName 'FastLink Direct - " + safe + "' -Comment '" + Marker + "' | Out-Null");
            }
            script.AppendLine("Write-Output '" + AppliedMarker + "'");
            ProcessResult result = ProcessRunner.RunPowerShell(script.ToString(), 15000);
            bool rulesApplied = result.Output.IndexOf(AppliedMarker, StringComparison.Ordinal) >= 0;
            if (!rulesApplied)
                throw new InvalidOperationException("无法应用 Windows DNS 规则：" + result.Error + result.Output);
            if (result.ExitCode != 0)
                AppLogger.Log("Windows DNS rules were applied before the command was force-closed.");
            FlushDnsCache();
        }

        public static void RemoveAll()
        {
            StringBuilder script = new StringBuilder();
            script.AppendLine("$ErrorActionPreference='SilentlyContinue'");
            AppendRemove(script);
            ProcessRunner.RunPowerShell(script.ToString(), 15000);
            FlushDnsCache();
        }

        private static void AppendRemove(StringBuilder script)
        {
            script.AppendLine("Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object { $_.Comment -eq '" + Marker + "' } | Remove-DnsClientNrptRule -Force -ErrorAction SilentlyContinue");
        }

        private static void FlushDnsCache()
        {
            ProcessRunner.Run("ipconfig.exe", "/flushdns", 5000);
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
        private volatile bool running;
        private volatile RuleSet ruleSet;
        private PhysicalNetworkInfo network;
        private RouteManager routes;
        private UdpClient udpListener;
        private TcpListener tcpListener;

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

        public void UpdateRules(RuleSet rules) { ruleSet = rules; }

        public void Stop()
        {
            running = false;
            try { udpListener.Close(); } catch { }
            try { tcpListener.Stop(); } catch { }
            udpListener = null;
            tcpListener = null;
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
            byte[] response = HandleQuery(query, false);
            if (response == null || !running) return;
            try { udpListener.Send(response, response.Length, client); } catch { }
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
                    byte[] response = HandleQuery(query, true);
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
            bool direct = ruleSet != null && ruleSet.Matches(host);
            if (direct) AppLogger.Log("DNS DIRECT  " + host + "  type=" + type + "  via=" + network.DnsServer);
            if (direct && (type == 28 || type == 64 || type == 65))
                return DnsPacket.BuildEmptyAnswer(query, questionEnd, 0);

            IPAddress upstream = direct ? network.DnsServer : network.FastLinkDns;
            byte[] response = tcp ? ForwardTcp(query, upstream) : ForwardUdp(query, upstream);
            if (response == null) return DnsPacket.BuildEmptyAnswer(query, questionEnd, 2);

            if (direct && type == 1)
            {
                List<DnsAnswerAddress> addresses = DnsPacket.ReadAAnswers(response);
                foreach (DnsAnswerAddress address in addresses)
                {
                    if (!routes.EnsureAddressRoute(address.Address, address.Ttl))
                        return DnsPacket.BuildEmptyAnswer(query, questionEnd, 2);
                }
                if (addresses.Count > 0)
                    AppLogger.Log("DNS ANSWER  " + host + " -> " + string.Join(",", addresses.Select(delegate(DnsAnswerAddress item) { return item.Address.ToString(); }).ToArray()));
            }
            return response;
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
        private bool privateNetworksEnabled;
        public bool Enabled { get; private set; }
        public PhysicalNetworkInfo Network { get; private set; }

        public RuntimeController()
        {
            routes = new RouteManager();
            proxy = new DnsProxy();
        }

        public void InitializeAndCleanLegacy()
        {
            AppLogger.Log("Application initialization and stale-state cleanup started.");
            NrptManager.RemoveAll();
            routes.RemoveAll();
            LegacyCleanup.Run();
            AppLogger.Log("Application initialization completed.");
        }

        public void Enable(RuleSet rules, bool directPrivateNetworks)
        {
            lock (sync)
            {
                if (Enabled) return;
                AppLogger.Log("Enable requested. Rules=" + rules.NormalizedRules.Count + ", privateNetworks=" + directPrivateNetworks);
                if (rules.NormalizedRules.Count == 0) throw new InvalidOperationException("请先添加至少一条域名规则。");
                PhysicalNetworkInfo network = NetworkDetector.Detect();
                if (network == null) throw new InvalidOperationException("没有找到可用的物理 Wi-Fi 或有线网卡。");
                Network = network;
                AppLogger.Log("Physical network detected: " + network.InterfaceName + ", DNS=" + network.DnsServer + ", gateway=" + network.Gateway);
                routes.SetNetwork(network);
                if (!routes.EnsureDnsRoute()) throw new InvalidOperationException("无法为物理 DNS 添加直连路由。");
                foreach (IPAddress address in rules.GetDirectIpAddresses())
                {
                    if (!routes.EnsureRuleAddressRoute(address))
                        throw new InvalidOperationException("无法为 IP 地址 " + address + " 添加直连路由。");
                }
                if (directPrivateNetworks && !routes.EnsurePrivateNetworkRoutes())
                    throw new InvalidOperationException("无法添加内网直连路由。");
                try
                {
                    proxy.Start(network, rules, routes);
                    AppLogger.Log("Local DNS proxy started; applying Windows DNS rules.");
                    NrptManager.Apply(rules.GetNrptNamespaces());
                    Enabled = true;
                    privateNetworksEnabled = directPrivateNetworks;
                    AppLogger.Log("Direct mode enabled on " + network.InterfaceName + ", DNS=" + network.DnsServer + ", gateway=" + network.Gateway);
                }
                catch
                {
                    NrptManager.RemoveAll();
                    proxy.Stop();
                    routes.RemoveAll();
                    Network = null;
                    AppLogger.Log("Enable failed; changes rolled back.");
                    throw;
                }
            }
        }

        public void Disable()
        {
            lock (sync)
            {
                NrptManager.RemoveAll();
                proxy.Stop();
                routes.RemoveAll();
                Network = null;
                Enabled = false;
                privateNetworksEnabled = false;
                AppLogger.Log("Direct mode disabled and routes removed.");
            }
        }

        public void UpdateRules(RuleSet rules, bool directPrivateNetworks)
        {
            lock (sync)
            {
                if (!Enabled) return;
                AppLogger.Log("Hot rule update started. Rules=" + rules.NormalizedRules.Count + ", privateNetworks=" + directPrivateNetworks);
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
                privateNetworksEnabled = directPrivateNetworks;
                AppLogger.Log("Hot rule update completed.");
            }
        }

        public void Restart(RuleSet rules, bool directPrivateNetworks)
        {
            Disable();
            Enable(rules, directPrivateNetworks);
        }

        public void RemoveExpiredRoutes() { routes.RemoveExpired(); }
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
        private readonly ListBox ruleList;
        private readonly TextBox ruleInput;
        private readonly TextBox logBox;
        private bool busy;
        private bool manualOffUntilReconnect;
        private bool lastFastLinkActive;
        private bool allowExit;

        public MainForm(bool autoStartLaunch)
        {
            Text = "FastLink 直连助手";
            AppLogger.Log("UI process started. AutoStartLaunch=" + autoStartLaunch);
            Icon = SystemIcons.Shield;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(680, 560);
            Size = new Size(720, 610);
            Font = new Font("Microsoft YaHei UI", 9F);

            config = ConfigStore.Load();
            runtime = new RuntimeController();

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(14);
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            Controls.Add(root);

            Panel statusPanel = new Panel();
            statusPanel.Dock = DockStyle.Fill;
            statusPanel.BackColor = Color.FromArgb(245, 247, 250);
            fastLinkStatus = NewStatusLabel("Fastlink：检测中…", 14);
            directStatus = NewStatusLabel("直连模式：关闭", 42);
            networkStatus = NewStatusLabel("物理网络：尚未识别", 70);
            statusPanel.Controls.Add(fastLinkStatus);
            statusPanel.Controls.Add(directStatus);
            statusPanel.Controls.Add(networkStatus);
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
            trayMenu.Items.Add("打开运行日志", null, delegate
            {
                try { Process.Start("notepad.exe", AppLogger.LogPath); } catch { }
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
                Task.Run(delegate
                {
                    try
                    {
                        runtime.InitializeAndCleanLegacy();
                        if (config.AutoStart && !StartupTask.IsInstalled()) StartupTask.Install();
                    }
                    catch (Exception ex) { SafeLog("初始化失败：" + ex.Message); }
                }).ContinueWith(delegate
                {
                    BeginInvoke((Action)delegate
                    {
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
            };
            FormClosed += delegate
            {
                monitorTimer.Stop();
                trayIcon.Visible = false;
                try { runtime.Disable(); } catch { }
            };

            monitorTimer = new System.Windows.Forms.Timer();
            monitorTimer.Interval = 2500;
            monitorTimer.Tick += delegate
            {
                CheckFastLink();
                if (runtime.Enabled) Task.Run(delegate { runtime.RemoveExpiredRoutes(); });
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

        private void CheckFastLink()
        {
            if (busy) return;
            bool active = NetworkDetector.IsFastLinkActive();
            fastLinkStatus.Text = active ? "Fastlink：已接通" : "Fastlink：未接通";
            fastLinkStatus.ForeColor = active ? Color.DarkGreen : Color.DimGray;

            if (!active)
            {
                manualOffUntilReconnect = false;
                if (runtime.Enabled) BeginDisable("Fastlink 已断开，直连规则已自动清理。");
            }
            else if (!lastFastLinkActive && config.AutoEnableWithFastLink && !manualOffUntilReconnect)
            {
                BeginEnable("检测到 Fastlink，正在自动开启直连…");
            }
            else if (active && config.AutoEnableWithFastLink && !runtime.Enabled && !manualOffUntilReconnect)
            {
                BeginEnable("正在自动开启直连…");
            }
            lastFastLinkActive = active;
            RefreshUi();
        }

        private void MainSwitchChanged(object sender, EventArgs e)
        {
            if (busy) return;
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
            if (busy || runtime.Enabled) return;
            busy = true;
            SafeLog(message);
            RuleSet rules = CurrentRules();
            Task.Run(delegate { runtime.Enable(rules, config.DirectPrivateNetworks); }).ContinueWith(delegate(Task task)
            {
                BeginInvoke((Action)delegate
                {
                    busy = false;
                    if (task.IsFaulted)
                    {
                        Exception error = task.Exception.GetBaseException();
                        SafeLog("开启失败：" + error.Message);
                        trayIcon.ShowBalloonTip(4000, "FastLink 直连助手", "开启失败：" + error.Message, ToolTipIcon.Error);
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
            if (!runtime.Enabled || busy) return;
            busy = true;
            RuleSet rules = CurrentRules();
            SafeLog("规则已修改，正在刷新…");
            Task.Run(delegate { runtime.UpdateRules(rules, config.DirectPrivateNetworks); }).ContinueWith(delegate(Task task)
            {
                BeginInvoke((Action)delegate
                {
                    busy = false;
                    SafeLog(task.IsFaulted ? "刷新失败：" + task.Exception.GetBaseException().Message : "规则刷新完成。");
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
            directStatus.Text = runtime.Enabled ? "直连模式：已开启" : "直连模式：关闭";
            directStatus.ForeColor = runtime.Enabled ? Color.DarkGreen : Color.DimGray;
            if (runtime.Network != null)
            {
                networkStatus.Text = string.Format("物理网络：{0}    DNS：{1}    网关：{2}",
                    runtime.Network.InterfaceName, runtime.Network.DnsServer, runtime.Network.Gateway);
            }
            else networkStatus.Text = "物理网络：尚未启用";

            mainSwitch.CheckedChanged -= MainSwitchChanged;
            mainSwitch.Checked = runtime.Enabled;
            mainSwitch.Text = runtime.Enabled ? "关闭直连" : "立即开启直连";
            mainSwitch.BackColor = runtime.Enabled ? Color.FromArgb(210, 242, 218) : SystemColors.Control;
            mainSwitch.CheckedChanged += MainSwitchChanged;
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
            allowExit = true;
            Close();
        }
    }
}
