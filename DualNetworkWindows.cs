using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace FastLinkDirectTool.Modes
{
    public sealed class DualCommandResult
    {
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public string Output { get; set; }
        public string ErrorCode { get; set; }
    }

    internal sealed class DualAdapterData
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int Index { get; set; }
        public string Kind { get; set; }
        public bool IsUp { get; set; }
        public bool IsPhysical { get; set; }
        public string Address { get; set; }
        public int PrefixLength { get; set; }
        public string Gateway { get; set; }
        public List<string> DnsServers { get; set; }
        public bool HasIpv6Default { get; set; }
        public string Ipv6RouteSignature { get; set; }
        public int Metric { get; set; }
        public int Automatic { get; set; }
    }

    internal sealed class DualDefaultData
    {
        public string Id { get; set; }
        public long Cost { get; set; }
    }

    internal sealed class DualCaptureData
    {
        public List<DualAdapterData> Adapters { get; set; }
        public List<DualDefaultData> Defaults { get; set; }
        public bool SystemProxyActive { get; set; }
        public bool TunnelActive { get; set; }
        public bool OtherDnsPolicyPresent { get; set; }
    }

    internal sealed class DualValueData
    {
        public string Value { get; set; }
    }

    // Construction is inert. Capture performs bounded reads only. Network writes
    // are exposed solely through the transaction interface and revalidate identity.
    public sealed class DualNetworkWindowsPlatform : IDualNetworkPlatform
    {
        private const string Marker = "DUAL_JSON:";
        private const string OwnerPrefix = "FastLinkDirectTool.DualNetwork:";
        private static class WorkerHolder
        {
            internal static readonly DualPowerShellWorker Worker = new DualPowerShellWorker();
            static WorkerHolder() { AppDomain.CurrentDomain.ProcessExit += delegate { Worker.Dispose(); }; }
        }
        private readonly Func<string, int, DualCommandResult> runner;
        private readonly Func<bool> administrator;
        private readonly Func<DateTime> clock;
        private readonly string scope;
        private readonly object sync = new object();
        private readonly Dictionary<string, DualAdapterData> bindings = new Dictionary<string, DualAdapterData>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> sessionOwners = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> observed = new Dictionary<string, string>(StringComparer.Ordinal);
        private string stableSignature;
        private DateTime stableSince;
        private string verifiedInternetSignature;
        private DateTime internetVerificationExpires;
        private DateTime internetVerificationStarted, lastCaptureAt;
        private bool internetConfirmed, internetVerificationConsumed;

        public bool CorporateAdmissionConfirmed { get; set; }
        public bool DualConnectionConfirmed { get; set; }
        public bool InternetConfirmed
        {
            get { lock (sync) return internetConfirmed; }
            set
            {
                lock (sync)
                {
                    internetConfirmed = value;
                    if (!value) ClearInternetProof();
                }
            }
        }
        // Called only after a source-bound HTTPS probe and unchanged snapshots.
        // The first subsequent Capture must consume it within sixty seconds.
        // It then remains session evidence while the same topology is observed;
        // it is not a promise of continuous Internet reachability. The UI clears
        // it before every explicit Arm/Start and re-probes, never periodically.
        public void ConfirmInternetForCurrentTopology()
        {
            lock (sync)
            {
                if (string.IsNullOrEmpty(stableSignature)) throw new InvalidOperationException("没有可验证的网络快照。");
                verifiedInternetSignature = stableSignature;
                internetVerificationStarted = clock();
                internetVerificationExpires = internetVerificationStarted.AddSeconds(60);
                internetVerificationConsumed = false;
                internetConfirmed = true;
            }
        }
        private void ClearInternetProof()
        {
            verifiedInternetSignature = null;
            internetVerificationStarted = internetVerificationExpires = default(DateTime);
            internetVerificationConsumed = internetConfirmed = false;
        }
        public string CorporateAdapterId { get; set; }
        public string InternetAdapterId { get; set; }
        public bool IsAdministrator { get { return administrator(); } }
        public string ScopeIdentity { get { return scope; } }

        public DualNetworkWindowsPlatform() : this(RunHidden, CurrentAdministrator, delegate { return DateTime.UtcNow; }, CurrentScope()) { }

        public DualNetworkWindowsPlatform(Func<string, int, DualCommandResult> commandRunner,
            Func<bool> isAdministrator, Func<DateTime> utcClock, string scopeIdentity)
        {
            if (commandRunner == null || isAdministrator == null || utcClock == null || string.IsNullOrWhiteSpace(scopeIdentity))
                throw new ArgumentException("Missing platform dependency.");
            runner = commandRunner; administrator = isAdministrator; clock = utcClock; scope = scopeIdentity;
        }

        private static bool CurrentAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static string CurrentScope()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            using (SHA256 sha = SHA256.Create())
            {
                if (identity.User == null) throw new InvalidOperationException("无法确认当前用户身份。");
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(Environment.MachineName + "|" + identity.User.Value));
                return "windows:" + BitConverter.ToString(digest).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static DualCommandResult RunHidden(string script, int timeout)
        {
            return WorkerHolder.Worker.Execute(script, timeout);
        }

        private T Execute<T>(string script, int timeout)
        {
            DualCommandResult result = runner(script, timeout);
            if (result == null || result.TimedOut) throw new InvalidOperationException("双网命令超时；保留事务记录，未假定操作成功。");
            if (result.ExitCode != 0) throw new InvalidOperationException("双网命令失败；保留事务记录，请检查权限或网络状态。" + (string.IsNullOrEmpty(result.ErrorCode) ? "" : " [" + result.ErrorCode + "]"));
            string[] records = (result.Output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith(Marker, StringComparison.Ordinal)).ToArray();
            if (records.Length != 1 || records[0].Length > 1024 * 1024) throw new InvalidOperationException("网络命令返回格式不完整，未信任结果。");
            try
            {
                string json = records[0].Substring(Marker.Length);
                JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 32 };
                Dictionary<string, object> envelope = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (envelope == null) throw new InvalidOperationException();
                if (typeof(T) == typeof(DualValueData) && (!envelope.ContainsKey("Value") || (envelope["Value"] != null && !(envelope["Value"] is string)))) throw new InvalidOperationException();
                if (typeof(T) == typeof(DualCaptureData))
                {
                    foreach (string flag in new[] { "SystemProxyActive", "TunnelActive", "OtherDnsPolicyPresent" })
                        if (!envelope.ContainsKey(flag) || !(envelope[flag] is bool)) throw new InvalidOperationException();
                    object rawAdapters;
                    if (!envelope.TryGetValue("Adapters", out rawAdapters) || !(rawAdapters is object[])) throw new InvalidOperationException();
                    foreach (object raw in (object[])rawAdapters)
                    {
                        Dictionary<string, object> adapter = raw as Dictionary<string, object>;
                        if (adapter == null || !adapter.ContainsKey("HasIpv6Default") || !(adapter["HasIpv6Default"] is bool)) throw new InvalidOperationException();
                        object signature;
                        if (adapter.TryGetValue("Ipv6RouteSignature", out signature) && signature != null && !(signature is string)) throw new InvalidOperationException();
                        if ((bool)adapter["HasIpv6Default"] && !NetworkModePlanner.ValidIpv6RouteSignature(signature as string)) throw new InvalidOperationException();
                        if (!(bool)adapter["HasIpv6Default"] && !string.IsNullOrEmpty(signature as string)) throw new InvalidOperationException();
                    }
                }
                T value = serializer.Deserialize<T>(json);
                if (value == null) throw new InvalidOperationException();
                return value;
            }
            catch (Exception error) { throw new InvalidOperationException("网络命令返回数据无法核验。", error); }
        }

        public NetworkSnapshot Capture()
        {
            lock (sync)
            {
                try
                {
                DualCaptureData data = Execute<DualCaptureData>(BuildCaptureScript(sessionOwners), 20000);
                if (data.Adapters == null || data.Defaults == null) throw new InvalidOperationException("网络快照缺少必要字段。");
                Dictionary<string, DualAdapterData> fresh = new Dictionary<string, DualAdapterData>(StringComparer.OrdinalIgnoreCase);
                NetworkSnapshot snapshot = new NetworkSnapshot
                {
                    SystemProxyActive = data.SystemProxyActive, TunnelActive = data.TunnelActive,
                    OtherDnsPolicyPresent = data.OtherDnsPolicyPresent,
                    CorporateAdmissionVerified = CorporateAdmissionConfirmed,
                    DualConnectionAllowed = DualConnectionConfirmed,
                    InternetReachabilityVerified = false,
                    Ipv6SafetyVerified = false
                };
                foreach (DualAdapterData a in data.Adapters)
                {
                    if (a == null) throw new InvalidOperationException("网络快照包含空接口。");
                    a.Id = CanonicalGuid(a.Id);
                    if (a.Index <= 0 || fresh.ContainsKey(a.Id)) throw new InvalidOperationException("接口标识重复或无效。");
                    fresh.Add(a.Id, a);
                    LinkKind kind;
                    if (!Enum.TryParse(a.Kind, out kind) || !Enum.IsDefined(typeof(LinkKind), kind)) kind = LinkKind.Other;
                    snapshot.Adapters.Add(new AdapterSnapshot
                    {
                        Id = a.Id, Name = a.Name, Kind = kind, IsUp = a.IsUp, IsPhysical = a.IsPhysical,
                        Address = a.Address, PrefixLength = a.PrefixLength, Gateway = a.Gateway,
                        DnsServers = a.DnsServers == null ? new List<string>() : new List<string>(a.DnsServers),
                        HasIpv6Default = a.HasIpv6Default, Ipv6RouteSignature = a.Ipv6RouteSignature
                    });
                }
                // Keep the exact GUID/index pair used for the latest snapshot. Every
                // later command validates it again before touching an interface.
                bindings.Clear(); foreach (KeyValuePair<string, DualAdapterData> item in fresh) bindings.Add(item.Key, item.Value);
                string internet = TryGuid(InternetAdapterId);
                AdapterSnapshot internetAdapter = snapshot.Adapters.FirstOrDefault(a => string.Equals(a.Id, internet, StringComparison.OrdinalIgnoreCase));
                snapshot.Ipv6SafetyVerified = NetworkModePlanner.Ipv6CompatibilityBlocker(new ModeRequest {
                    CorporateAdapterId = CorporateAdapterId, InternetAdapterId = InternetAdapterId,
                    Mode = internetAdapter != null && internetAdapter.Kind == LinkKind.Ethernet ? WorkMode.WifiCorporateWiredInternet : WorkMode.WiredCorporateWifiInternet
                }, snapshot) == null;
                List<DualDefaultData> defaults = data.Defaults.Where(d => d != null && d.Cost >= 0 && TryGuid(d.Id) != null).ToList();
                if (internet != null && defaults.Any(d => string.Equals(TryGuid(d.Id), internet, StringComparison.OrdinalIgnoreCase)))
                {
                    long best = defaults.Min(d => d.Cost);
                    snapshot.ExternalDefaultPreferred = defaults.Where(d => d.Cost == best).All(d => string.Equals(TryGuid(d.Id), internet, StringComparison.OrdinalIgnoreCase));
                }
                string signature = (CorporateAdapterId ?? "") + "|" + (InternetAdapterId ?? "") + "|" +
                    string.Join(";", fresh.Values.OrderBy(a => a.Id).Select(a => a.Id + "|" + a.Index + "|" + a.IsPhysical + "|" + a.Kind + "|" + a.IsUp + "|" + a.Address + "|" + a.PrefixLength + "|" + a.Gateway + "|" + a.HasIpv6Default + "|" + a.Ipv6RouteSignature + "|" + string.Join(",", a.DnsServers ?? new List<string>())).ToArray());
                DateTime now = clock();
                bool clockMovedBack = lastCaptureAt != default(DateTime) && now < lastCaptureAt;
                lastCaptureAt = now;
                if (!string.Equals(stableSignature, signature, StringComparison.Ordinal) || now < stableSince || clockMovedBack)
                { stableSignature = signature; stableSince = now; }
                snapshot.StableSeconds = (int)Math.Min(int.MaxValue, Math.Max(0, (now - stableSince).TotalSeconds));
                if (verifiedInternetSignature != null)
                {
                    bool valid = internetConfirmed && !clockMovedBack && now >= internetVerificationStarted &&
                        (internetVerificationConsumed || now <= internetVerificationExpires) &&
                        string.Equals(signature, verifiedInternetSignature, StringComparison.Ordinal) &&
                        !snapshot.SystemProxyActive && !snapshot.TunnelActive && !snapshot.OtherDnsPolicyPresent;
                    if (valid)
                    {
                        internetVerificationConsumed = true;
                        snapshot.InternetReachabilityVerified = true;
                    }
                    else ClearInternetProof(); // A former topology cannot revive old evidence.
                }
                return snapshot;
                }
                catch
                {
                    ClearInternetProof();
                    throw;
                }
            }
        }

        public string Read(DualResource resource)
        {
            lock (sync)
            {
                ValidateResource(resource);
                if (resource.Kind != DualResourceKind.Nrpt && !bindings.ContainsKey(CanonicalGuid(resource.InterfaceId))) Capture();
                if (resource.Kind == DualResourceKind.Nrpt) sessionOwners.Add(resource.Owner);
                string value = Execute<DualValueData>(BuildResourceScript(resource, "read", null), 15000).Value;
                observed[ResourceKey(resource)] = value;
                return value;
            }
        }

        public void Write(DualResource resource, string value)
        {
            lock (sync)
            {
                if (!IsAdministrator) throw new InvalidOperationException("双网操作需要管理员权限；不会自动提权。");
                ValidateResource(resource); ValidateValue(resource, value);
                if (resource.Kind == DualResourceKind.Nrpt) sessionOwners.Add(resource.Owner);
                DualValueData result = Execute<DualValueData>(BuildResourceScript(resource, "write", value), 20000);
                if (!DualNetworkRuntime.ResourceValuesEqual(resource, result.Value, value)) throw new InvalidOperationException("写入后读回不一致，保留恢复记录。");
                observed[ResourceKey(resource)] = result.Value;
            }
        }

        public void Remove(DualResource resource)
        {
            lock (sync)
            {
                if (!IsAdministrator) throw new InvalidOperationException("清理双网设置需要管理员权限；不会自动提权。");
                ValidateResource(resource);
                if (resource.Kind == DualResourceKind.Metric4) throw new InvalidOperationException("接口跃点不能删除；必须恢复原值。");
                DualValueData result = Execute<DualValueData>(BuildResourceScript(resource, "remove", null), 20000);
                if (result.Value != null) throw new InvalidOperationException("移除后仍有对应设置，保留恢复记录。");
                observed[ResourceKey(resource)] = null;
            }
        }

        private static string TryGuid(string text)
        { Guid value; return Guid.TryParse(text, out value) && value != Guid.Empty ? value.ToString("D") : null; }
        private static string CanonicalGuid(string text)
        { string value = TryGuid(text); if (value == null) throw new ArgumentException("必须提供有效接口 GUID。"); return value; }
        private static string Q(string text) { return "'" + (text ?? "").Replace("'", "''") + "'"; }
        private static string ResourceKey(DualResource resource)
        { return resource.Kind + "|" + (TryGuid(resource.InterfaceId) ?? "") + "|" + resource.Destination + "|" + resource.NextHop + "|" + (resource.Namespace ?? "").ToLowerInvariant() + "|" + resource.Owner; }
        private static string SafeIp(string text)
        {
            uint value; string canonical;
            if (!PlanningValidation.TrySafeIpv4(text, out value, out canonical) || text != canonical) throw new ArgumentException("必须提供规范且安全的 IPv4 地址。");
            return canonical;
        }
        private static void ValidateOwner(string owner)
        {
            if (owner == null || !owner.StartsWith(OwnerPrefix, StringComparison.Ordinal) || TryGuid(owner.Substring(OwnerPrefix.Length)) == null)
                throw new ArgumentException("缺少有效的本次会话所有权标识。");
        }

        private static void ValidateResource(DualResource resource)
        {
            if (resource == null || !Enum.IsDefined(typeof(DualResourceKind), resource.Kind)) throw new ArgumentException("无效资源。");
            if (resource.Kind != DualResourceKind.Nrpt) CanonicalGuid(resource.InterfaceId);
            if (resource.Kind == DualResourceKind.Route4)
            {
                uint first, last; string canonical;
                if (!PlanningValidation.TryCidr(resource.Destination, out first, out last, out canonical) || canonical != resource.Destination)
                    throw new ArgumentException("路由目标必须为规范 IPv4 前缀，禁止默认路由和伪地址。");
                if (resource.NextHop != "0.0.0.0") SafeIp(resource.NextHop);
            }
            if (resource.Kind == DualResourceKind.Nrpt)
            {
                ValidateOwner(resource.Owner);
                string ns = resource.Namespace;
                if (string.IsNullOrEmpty(ns) || ns.Length > 254 || !Regex.IsMatch(ns, @"^\.?[a-z0-9-]+(?:\.[a-z0-9-]+)+$", RegexOptions.IgnoreCase))
                    throw new ArgumentException("NRPT 只接受明确域名或后缀，不接受命令或 URL。");
            }
        }

        private static void ValidateValue(DualResource resource, string value)
        {
            Match match;
            if (resource.Kind == DualResourceKind.Metric4)
            {
                match = Regex.Match(value ?? "", @"^automatic=([01]);metric=([1-9][0-9]{0,4})$");
                if (!match.Success || int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) > 9999) throw new ArgumentException("无效接口跃点值。");
            }
            else if (resource.Kind == DualResourceKind.Route4)
            {
                match = Regex.Match(value ?? "", @"^metric=([1-9][0-9]{0,3})$");
                if (!match.Success) throw new ArgumentException("无效路由跃点值。");
            }
            else if (value != "server=127.0.0.1;owner=" + resource.Owner) throw new ArgumentException("NRPT 仅允许指向本地 DNS 和本会话标识。");
        }

        private string IdentityGuard(DualResource resource, bool writing)
        {
            string id = CanonicalGuid(resource.InterfaceId); DualAdapterData bound;
            if (!bindings.TryGetValue(id, out bound) || !bound.IsPhysical) throw new InvalidOperationException("请先读取并确认物理接口快照。");
            if (resource.Kind == DualResourceKind.Route4 && writing)
            {
                if (!bound.IsUp) throw new InvalidOperationException("指定路由接口已离线。");
                if (resource.NextHop == "0.0.0.0")
                {
                    uint address, first, last; string canonical;
                    if (bound.PrefixLength < 1 || bound.PrefixLength > 31 || !PlanningValidation.TryIpv4(bound.Address, out address, out canonical) ||
                        !PlanningValidation.TryCidr(resource.Destination, out first, out last, out canonical)) throw new InvalidOperationException("On-link 路由无法核验。");
                    uint mask = PlanningValidation.Mask(bound.PrefixLength);
                    if ((first & mask) != (address & mask) || (last & mask) != (address & mask)) throw new InvalidOperationException("On-link 路由不在指定接口子网内。");
                }
                else if (resource.NextHop != bound.Gateway) throw new InvalidOperationException("路由下一跳不属于已确认接口。");
            }
            string guard = "$id=" + Q(id) + ";$ix=" + bound.Index.ToString(CultureInfo.InvariantCulture) + ";\n" +
                "$a=@(Get-NetAdapter -Physical -ErrorAction Stop | Where-Object { $_.InterfaceGuid.ToString().Trim('{}') -ieq $id });" +
                "if($a.Count -ne 1 -or [int]$a[0].ifIndex -ne $ix){throw 'interface_identity_changed'};" +
                "$hw=@(Get-CimInstance Win32_NetworkAdapter -Filter 'PhysicalAdapter=True' -ErrorAction Stop | Where-Object { $_.GUID -and $_.GUID.Trim('{}') -ieq $id -and [int]$_.InterfaceIndex -eq $ix });" +
                "if($hw.Count -ne 1){throw 'physical_identity_unverified'};\n";
            if (writing && resource.Kind != DualResourceKind.Metric4)
            {
                guard += "if([string]$a[0].Status -ne 'Up'){throw 'interface_disconnected'};" +
                    "$ip=@(Get-NetIPAddress -InterfaceIndex $ix -AddressFamily IPv4 -PolicyStore ActiveStore -ErrorAction Stop | Where-Object { $_.IPAddress -eq " + Q(bound.Address) + " -and [int]$_.PrefixLength -eq " + bound.PrefixLength + " -and $_.AddressState -eq 'Preferred' });if($ip.Count -ne 1){throw 'interface_address_changed'};\n";
                guard += GatewayDnsGuard(bound);
            }
            return guard;
        }

        private string ConnectedRoleGuard(string role)
        {
            string id = CanonicalGuid(role); DualAdapterData bound;
            if (!bindings.TryGetValue(id, out bound) || !bound.IsPhysical || !bound.IsUp) throw new InvalidOperationException("DNS 策略要求两个物理接口已连接。");
            return IdentityGuard(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = id }, false) +
                "if([string]$a[0].Status -ne 'Up'){throw 'interface_disconnected'};" +
                "$ip=@(Get-NetIPAddress -InterfaceIndex $ix -AddressFamily IPv4 -PolicyStore ActiveStore -ErrorAction Stop | Where-Object {$_.IPAddress -eq " + Q(bound.Address) + " -and [int]$_.PrefixLength -eq " + bound.PrefixLength + " -and $_.AddressState -eq 'Preferred'});if($ip.Count -ne 1){throw 'interface_address_changed'};\n" + GatewayDnsGuard(bound);
        }

        private static string GatewayDnsGuard(DualAdapterData bound)
        {
            string expectedDns = string.Join(",", (bound.DnsServers ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray());
            return "$gateNow=@(Get-NetRoute -InterfaceIndex $ix -AddressFamily IPv4 -PolicyStore ActiveStore -ErrorAction Stop | Where-Object {$_.DestinationPrefix -eq '0.0.0.0/0'} | Sort-Object RouteMetric);" +
                "if($gateNow.Count -lt 1 -or [string]$gateNow[0].NextHop -cne " + Q(bound.Gateway) + "){throw 'interface_gateway_changed'};" +
                "$dnsNow=@(Get-DnsClientServerAddress -InterfaceIndex $ix -AddressFamily IPv4 -ErrorAction Stop | ForEach-Object {$_.ServerAddresses} | Sort-Object -Unique);" +
                "if(($dnsNow -join ',') -cne " + Q(expectedDns) + "){throw 'interface_dns_changed'};\n";
        }

        public string BuildResourceScript(DualResource resource, string operation, string value)
        {
            ValidateResource(resource);
            if (operation != "read" && operation != "write" && operation != "remove") throw new ArgumentException("Unknown operation.");
            if (operation == "write") ValidateValue(resource, value);
            StringBuilder script = new StringBuilder("$ErrorActionPreference='Stop';Set-StrictMode -Version 2.0;\n");
            if (resource.Kind != DualResourceKind.Nrpt) script.Append(IdentityGuard(resource, operation == "write"));
            string expected = null;
            if (operation != "read" && !observed.TryGetValue(ResourceKey(resource), out expected))
                throw new InvalidOperationException("操作前必须读取当前资源；不接受未经核验的写入或删除。");
            if (operation == "write" && resource.Kind != DualResourceKind.Metric4 && expected != null)
                throw new InvalidOperationException("已有资源只能借用，不允许重新认领或覆盖。");
            if (resource.Kind == DualResourceKind.Metric4)
            {
                if (operation == "remove") throw new ArgumentException("Metrics cannot be removed.");
                script.Append("function Read-Current { $p=@(Get-NetIPInterface -InterfaceIndex $ix -AddressFamily IPv4 -PolicyStore ActiveStore -ErrorAction Stop);if($p.Count -ne 1){throw 'ambiguous_ip_interface'};$auto=0;if([string]$p[0].AutomaticMetric -eq 'Enabled'){$auto=1};return ('automatic='+$auto+';metric='+[int]$p[0].InterfaceMetric) };\n");
                if (operation == "write")
                {
                    Match m = Regex.Match(value, @"^automatic=([01]);metric=(\d+)$");
                    script.Append("$before=Read-Current;$expected=").Append(Q(expected)).Append(";if($before -cne $expected -and -not ($before -match '^automatic=1;metric=[1-9][0-9]{0,3}$' -and $expected -match '^automatic=1;metric=[1-9][0-9]{0,3}$')){throw 'metric_concurrent_change'};Set-NetIPInterface -InterfaceIndex $ix -AddressFamily IPv4 -PolicyStore ActiveStore");
                    // Supplying InterfaceMetric itself disables AutomaticMetric.
                    // Restore automatic policy without forcing its old computed
                    // number; the readback comparison already allows recalculation.
                    if (m.Groups[1].Value == "1") script.Append(" -AutomaticMetric Enabled");
                    else script.Append(" -AutomaticMetric Disabled -InterfaceMetric ").Append(m.Groups[2].Value);
                    script.Append(" -ErrorAction Stop;\n");
                }
                script.Append("$v=Read-Current;");
            }
            else if (resource.Kind == DualResourceKind.Route4)
            {
                script.Append("$destination=").Append(Q(resource.Destination)).Append(";$next=").Append(Q(resource.NextHop)).Append(";\n")
                    .Append("function Read-Routes { return @(Get-NetRoute -AddressFamily IPv4 -InterfaceIndex $ix -PolicyStore ActiveStore -ErrorAction Stop | Where-Object { $_.DestinationPrefix -eq $destination -and $_.NextHop -eq $next }) };$r=@(Read-Routes);if($r.Count -gt 1){throw 'ambiguous_exact_route'};\n");
                if (operation != "remove")
                    script.Append("$competing=@(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore -ErrorAction Stop | Where-Object { $_.DestinationPrefix -eq $destination -and ($_.InterfaceIndex -ne $ix -or $_.NextHop -ne $next) });if($competing.Count -gt 0){throw 'conflicting_destination_route'};\n");
                if (operation == "write")
                {
                    string metric = value.Substring("metric=".Length);
                    script.Append("if($r.Count -gt 0){throw 'existing_route_not_owned'}else{New-NetRoute -AddressFamily IPv4 -InterfaceIndex $ix -DestinationPrefix $destination -NextHop $next -RouteMetric ")
                        .Append(metric).Append(" -PolicyStore ActiveStore -ErrorAction Stop | Out-Null};\n");
                }
                else if (operation == "remove")
                    script.Append("if($r.Count -eq 1){if(('metric='+[int]$r[0].RouteMetric) -cne ").Append(Q(expected)).Append("){throw 'route_concurrent_change'};$r[0] | Remove-NetRoute -Confirm:$false -ErrorAction Stop};\n");
                script.Append("$r=@(Read-Routes);if($r.Count -gt 1){throw 'ambiguous_exact_route'};$v=$null;if($r.Count -eq 1){$v='metric='+[int]$r[0].RouteMetric};");
            }
            else
            {
                script.Append("$ns=").Append(Q(resource.Namespace.ToLowerInvariant())).Append(";$owner=").Append(Q(resource.Owner)).Append(";\n")
                    .Append("function Read-Nrpt { return @(Get-DnsClientNrptRule -ErrorAction Stop | Where-Object { @($_.Namespace) -icontains $ns }) };$r=@(Read-Nrpt);if($r.Count -gt 1){throw 'ambiguous_nrpt_namespace'};\n");
                if (operation == "write")
                {
                    foreach (string role in new[] { CorporateAdapterId, InternetAdapterId })
                    {
                        if (TryGuid(role) == null) throw new InvalidOperationException("写入 DNS 策略前必须确认两个物理接口。");
                        script.Append(ConnectedRoleGuard(role));
                    }
                    script.Append("if($r.Count -gt 0){throw 'existing_nrpt_not_claimed'};Add-DnsClientNrptRule -Namespace $ns -NameServers '127.0.0.1' -Comment $owner -DisplayName $owner -ErrorAction Stop | Out-Null;\n");
                }
                else if (operation == "remove")
                    script.Append("if($r.Count -eq 1){if(").Append(Q(expected)).Append(" -cne ('server=127.0.0.1;owner='+$owner) -or $r[0].Comment -cne $owner -or @($r[0].Namespace).Count -ne 1 -or [string]$r[0].NameServers -ne '127.0.0.1'){throw 'nrpt_ownership_conflict'};Remove-DnsClientNrptRule -Name $r[0].Name -Force -ErrorAction Stop};\n");
                script.Append("$r=@(Read-Nrpt);if($r.Count -gt 1){throw 'ambiguous_nrpt_namespace'};$v=$null;if($r.Count -eq 1){$v='server='+(@($r[0].NameServers)-join ',')+';owner='+[string]$r[0].Comment};");
            }
            script.Append("Write-Output ('DUAL_JSON:'+(@{Value=$v}|ConvertTo-Json -Compress -Depth 4));");
            return script.ToString();
        }

        public static string BuildCaptureScript(IEnumerable<string> owners)
        {
            string[] validOwners = (owners ?? new string[0]).ToArray(); foreach (string owner in validOwners) ValidateOwner(owner);
            return "$ErrorActionPreference='Stop';Set-StrictMode -Version 2.0;$owned=@(" + string.Join(",", validOwners.Select(Q).ToArray()) + ");\n" + @"
$physical=@(Get-NetAdapter -Physical -ErrorAction Stop);
$hardware=@(Get-CimInstance Win32_NetworkAdapter -Filter 'PhysicalAdapter=True' -ErrorAction Stop);
$all=@(Get-NetAdapter -IncludeHidden -ErrorAction Stop);
$ipif=@(Get-NetIPInterface -AddressFamily IPv4 -PolicyStore ActiveStore -ErrorAction Stop);
$routes=@(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore -ErrorAction Stop);
$routes6=@(Get-NetRoute -AddressFamily IPv6 -PolicyStore ActiveStore -ErrorAction Stop);
$ipif6=@(Get-NetIPInterface -AddressFamily IPv6 -PolicyStore ActiveStore -ErrorAction Stop);
$broad6=@($routes6|Where-Object {$_.DestinationPrefix -in @('::/0','::/1','8000::/1','2000::/3')});
foreach($r in $broad6){
 if(@($all|Where-Object {$_.ifIndex -eq $r.InterfaceIndex}).Count -ne 1){throw 'ipv6_route_interface_unknown'};
 if(@($ipif6|Where-Object {$_.InterfaceIndex -eq $r.InterfaceIndex}).Count -ne 1){throw 'ipv6_route_metric_unknown'}
}
function Read-Ipv6Signature([int]$index){
 $rows=@($broad6|Where-Object {$_.InterfaceIndex -eq $index}|Sort-Object DestinationPrefix,NextHop,RouteMetric);
 if($rows.Count -eq 0){return ''};
 $if6=@($ipif6|Where-Object {$_.InterfaceIndex -eq $index});
 if($if6.Count -ne 1){throw 'ipv6_route_metric_unknown'};
 return (@($rows|ForEach-Object {[string]$_.DestinationPrefix+'|'+[string]$_.NextHop+'|'+[string]$_.RouteMetric+'|'+[string]$if6[0].InterfaceMetric}) -join ';')
}
$addresses=@(Get-NetIPAddress -AddressFamily IPv4 -PolicyStore ActiveStore -ErrorAction Stop);
$dns=@(Get-DnsClientServerAddress -AddressFamily IPv4 -ErrorAction Stop);
$items=@();$defaults=@();
foreach($a in $physical){
 $id=$a.InterfaceGuid.ToString().Trim('{}').ToLowerInvariant();$ix=[int]$a.ifIndex;
 $hw=@($hardware|Where-Object {$_.GUID -and $_.GUID.Trim('{}') -ieq $id -and [int]$_.InterfaceIndex -eq $ix});
 $ips=@($addresses|Where-Object {$_.InterfaceIndex -eq $ix -and $_.AddressState -eq 'Preferred' -and -not $_.SkipAsSource});
 $gate=@($routes|Where-Object {$_.InterfaceIndex -eq $ix -and $_.DestinationPrefix -eq '0.0.0.0/0'}|Sort-Object RouteMetric);
 $v4=@($ipif|Where-Object {$_.InterfaceIndex -eq $ix});$metric=0;$auto=0;if($v4.Count -eq 1){$metric=[int]$v4[0].InterfaceMetric;if([string]$v4[0].AutomaticMetric -eq 'Enabled'){$auto=1}};
 $ip='';$prefix=0;if($ips.Count -eq 1){$ip=[string]$ips[0].IPAddress;$prefix=[int]$ips[0].PrefixLength};
 $gateway='';if($gate.Count -ge 1){$gateway=[string]$gate[0].NextHop};
 $servers=@($dns|Where-Object {$_.InterfaceIndex -eq $ix}|ForEach-Object {$_.ServerAddresses});
 $kind='Other';if([int]$a.NdisPhysicalMedium -in @(1,9)){$kind='Wifi'}elseif([int]$a.NdisPhysicalMedium -eq 14 -or ($hw.Count -eq 1 -and [int]$hw[0].AdapterTypeID -eq 0)){$kind='Ethernet'};
 $up=([string]$a.Status -eq 'Up');$signature6=Read-Ipv6Signature $ix;$v6=-not [string]::IsNullOrEmpty($signature6);
 $items+=@{Id=$id;Name=[string]$a.Name;Index=$ix;Kind=$kind;IsUp=$up;IsPhysical=($hw.Count -eq 1);Address=$ip;PrefixLength=$prefix;Gateway=$gateway;DnsServers=$servers;HasIpv6Default=$v6;Ipv6RouteSignature=$signature6;Metric=$metric;Automatic=$auto};
}
$physicalIds=@($items|Where-Object {$_.IsPhysical}|ForEach-Object {$_.Id});
# Retain non-selectable IPv6-default evidence, including generically named VPNs.
foreach($a in $all){
 $id=$a.InterfaceGuid.ToString().Trim('{}').ToLowerInvariant();$ix=[int]$a.ifIndex;
 $signature6=Read-Ipv6Signature $ix;$v6=-not [string]::IsNullOrEmpty($signature6);
 if($v6 -and @($items|Where-Object {$_.Id -eq $id}).Count -eq 0){
  $items+=@{Id=$id;Name=[string]$a.Name;Index=$ix;Kind='Other';IsUp=([string]$a.Status -eq 'Up');IsPhysical=$false;Address='';PrefixLength=0;Gateway='';DnsServers=@();HasIpv6Default=$true;Ipv6RouteSignature=$signature6;Metric=0;Automatic=0}
 }
}
foreach($r in @($routes|Where-Object {$_.DestinationPrefix -eq '0.0.0.0/0'})){
 $a=@($all|Where-Object {$_.ifIndex -eq $r.InterfaceIndex -and [string]$_.Status -eq 'Up'});$p=@($ipif|Where-Object {$_.InterfaceIndex -eq $r.InterfaceIndex});
 if($a.Count -eq 1 -and $p.Count -eq 1){$defaults+=@{Id=$a[0].InterfaceGuid.ToString().Trim('{}').ToLowerInvariant();Cost=([long]$r.RouteMetric+[long]$p[0].InterfaceMetric)}}
}
$proxy=Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction Stop;
$proxyActive=$false;if($proxy.PSObject.Properties.Name -contains 'ProxyEnable'){$proxyActive=([int]$proxy.ProxyEnable -ne 0)};
if($proxy.PSObject.Properties.Name -contains 'AutoConfigURL'){$proxyActive=$proxyActive -or -not [string]::IsNullOrWhiteSpace([string]$proxy.AutoConfigURL)};
if($proxy.PSObject.Properties.Name -contains 'AutoDetect'){$proxyActive=$proxyActive -or [int]$proxy.AutoDetect -ne 0};
$tunnel=@($all|Where-Object {[string]$_.Status -eq 'Up' -and -not $_.HardwareInterface -and ($_.Name+' '+$_.InterfaceDescription) -match '(?i)fastlink|atrust|sangfor|wireguard|wintun|vpn|tunnel|\btap\b|\btun\b'}).Count -gt 0;
$broad=@($routes|Where-Object {$_.DestinationPrefix -in @('0.0.0.0/0','0.0.0.0/1','128.0.0.0/1')}|ForEach-Object {[int]$_.InterfaceIndex});
foreach($a in $all){if([string]$a.Status -eq 'Up' -and $broad -contains [int]$a.ifIndex -and $physicalIds -notcontains $a.InterfaceGuid.ToString().Trim('{}').ToLowerInvariant()){$tunnel=$true}};
$nrpt=@(Get-DnsClientNrptRule -ErrorAction Stop);$effective=@(Get-DnsClientNrptPolicy -Effective -ErrorAction Stop);
$foreign=@($nrpt|Where-Object {$owned -cnotcontains [string]$_.Comment}).Count -gt 0;
foreach($policy in $effective){
 $effectiveServers=@();if($policy.PSObject.Properties.Name -contains 'NameServers'){$effectiveServers=@($policy.NameServers|ForEach-Object {([string]$_).Split(@(',',';',' '),[StringSplitOptions]::RemoveEmptyEntries)})};
 $effectiveOwned=($effectiveServers.Count -eq 1 -and $effectiveServers[0] -ceq '127.0.0.1');
 if($policy.PSObject.Properties.Name -contains 'DirectAccessEnabled'){if($policy.DirectAccessEnabled){$effectiveOwned=$false}};
 foreach($ns in @($policy.Namespace)){$matching=@($nrpt|Where-Object {$owned -ccontains [string]$_.Comment -and @($_.Namespace) -icontains $ns -and [string]$_.NameServers -eq '127.0.0.1'});if($matching.Count -ne 1 -or -not $effectiveOwned){$foreign=$true}}
};
Write-Output ('DUAL_JSON:'+(@{Adapters=$items;Defaults=$defaults;SystemProxyActive=$proxyActive;TunnelActive=$tunnel;OtherDnsPolicyPresent=$foreign}|ConvertTo-Json -Compress -Depth 7));
";
        }
    }

    // A corrupt or foreign journal is a hard error, never an empty state.
    public sealed partial class DualFileJournal : IDualJournal
    {
        private readonly string path;
        private readonly object sync = new object();
        public string JournalPath { get { return path; } }
        public DualFileJournal() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastLinkDirectTool", "dual-session.json")) { }
        public DualFileJournal(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath)) throw new ArgumentException("Journal needs an absolute path.");
            path = Path.GetFullPath(filePath);
            if (!string.Equals(Path.GetFileName(path), "dual-session.json", StringComparison.Ordinal)) throw new ArgumentException("Unexpected journal filename.");
        }

        private void CheckPath()
        {
            string directory = Path.GetDirectoryName(path);
            // The OS may legitimately redirect LocalAppData/Temp (packaged apps
            // and migrated profiles). Reject redirection of our owned leaf,
            // not the trusted parent selected by the OS or test harness.
            DirectoryInfo d = new DirectoryInfo(directory);
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Journal directory is redirected; refusing access.");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Journal file is redirected.");
        }

        private static void Validate(DualJournalDocument document)
        {
            if (document == null || document.Version != 1 || string.IsNullOrWhiteSpace(document.Scope) ||
                string.IsNullOrWhiteSpace(document.Phase) || document.Generation < 0 || document.Entries == null || document.Entries.Count > 10000)
                throw new InvalidDataException("双网恢复记录无效；不会跳过清理状态。");
            Guid owner;
            const string prefix = "FastLinkDirectTool.DualNetwork:";
            if (document.Owner == null || !document.Owner.StartsWith(prefix, StringComparison.Ordinal) ||
                !Guid.TryParse(document.Owner.Substring(prefix.Length), out owner) || owner == Guid.Empty)
                throw new InvalidDataException("双网恢复记录缺少有效会话所有权。");
            foreach (DualJournalEntry entry in document.Entries)
                if (entry == null || entry.Resource == null || !Enum.IsDefined(typeof(DualResourceKind), entry.Resource.Kind))
                    throw new InvalidDataException("双网恢复条目无效。");
                else if (entry.Resource.Kind == DualResourceKind.Nrpt && entry.Resource.Owner != document.Owner)
                    throw new InvalidDataException("恢复记录中的 DNS 所有权不一致。");
        }

        public DualJournalDocument Load()
        {
            lock (sync)
            {
                CheckPath(); if (!File.Exists(path)) return null;
                FileInfo info = new FileInfo(path); if (info.Length < 2 || info.Length > 4 * 1024 * 1024) throw new InvalidDataException("双网恢复记录大小异常。");
                try
                {
                    DualJournalDocument document = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 32 }.Deserialize<DualJournalDocument>(File.ReadAllText(path, Encoding.UTF8));
                    Validate(document); return document;
                }
                catch (Exception error) { throw new InvalidDataException("双网恢复记录损坏；保留原文件并停止自动操作。", error); }
            }
        }

        public void Save(DualJournalDocument document)
        {
            lock (sync)
            {
                Validate(document); CheckPath();
                if (File.Exists(path))
                {
                    DualJournalDocument previous = Load();
                    if (previous.Scope != document.Scope || previous.Owner != document.Owner || previous.Generation > document.Generation)
                        throw new InvalidDataException("不允许覆盖其他会话或更新代次的恢复记录。");
                }
                byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Serialize(document));
                if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Journal too large.");
                string directory = Path.GetDirectoryName(path); Directory.CreateDirectory(directory); CheckPath();
                string temporary = Path.Combine(directory, "dual-session." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                    if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        public void Delete()
        {
            lock (sync) { CheckPath(); if (!File.Exists(path)) return; Load(); File.Delete(path); if (File.Exists(path)) throw new IOException("恢复记录删除后仍然存在。"); }
        }
    }
}
