using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;

namespace FastLinkDirectTool.Modes
{
    // Preview-only DTOs: this module has no environment, registry, file, socket,
    // process, or configuration-store dependencies. False is an unverified gate.
    public enum WorkMode { Normal, FastLinkSplit, WiredCorporateWifiInternet, WifiCorporateWiredInternet }
    public enum LinkKind { Wifi, Ethernet, Other }
    public enum RuleTarget { Review, Corporate, Internet }

    public sealed class AdapterSnapshot
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public LinkKind Kind { get; set; }
        public bool IsUp { get; set; }
        public bool IsPhysical { get; set; }
        public string Address { get; set; }
        public int PrefixLength { get; set; }
        public string Gateway { get; set; }
        public List<string> DnsServers { get; set; }
        public bool HasIpv6Default { get; set; }
        // Read-only broad IPv6 route tuples: prefix|next-hop|route-metric|interface-metric.
        // The helper never creates, removes or reprioritizes these routes.
        public string Ipv6RouteSignature { get; set; }
        public AdapterSnapshot() { DnsServers = new List<string>(); }
    }

    public sealed class NetworkSnapshot
    {
        public List<AdapterSnapshot> Adapters { get; set; }
        public bool SystemProxyActive { get; set; }
        public bool TunnelActive { get; set; }
        public bool CleanupPending { get; set; }
        public bool OtherDnsPolicyPresent { get; set; }
        public bool CorporateAdmissionVerified { get; set; }
        public bool DualConnectionAllowed { get; set; }
        public bool InternetReachabilityVerified { get; set; }
        public bool ExternalDefaultPreferred { get; set; }
        public bool Ipv6SafetyVerified { get; set; }
        public int StableSeconds { get; set; }
        public NetworkSnapshot() { Adapters = new List<AdapterSnapshot>(); }
    }

    public sealed class ModeRule
    {
        public string Pattern { get; set; }
        public RuleTarget Target { get; set; }
    }

    public sealed class ModeRequest
    {
        public WorkMode Mode { get; set; }
        public string CorporateAdapterId { get; set; }
        public string InternetAdapterId { get; set; }
        public List<ModeRule> Rules { get; set; }
        public List<string> CorporateCidrs { get; set; }
        public ModeRequest() { Rules = new List<ModeRule>(); CorporateCidrs = new List<string>(); }
    }

    public sealed class ModePlan
    {
        public bool CanSimulate { get; set; }
        public string Status { get; set; }
        public List<string> Blockers { get; set; }
        public List<string> Warnings { get; set; }
        public List<PlanIntent> Intents { get; set; }
        public ModePlan()
        {
            Status = "Blocked";
            Blockers = new List<string>(); Warnings = new List<string>(); Intents = new List<PlanIntent>();
        }
    }

    public sealed class PlanIntent
    {
        public string Kind { get; set; }
        public string Destination { get; set; }
        public string InterfaceId { get; set; }
        public string Gateway { get; set; }
        public string Description { get; set; }
    }

    public sealed class RouteDecision
    {
        public RuleTarget Target { get; set; }
        public bool Blocked { get; set; }
        public string Reason { get; set; }
        public List<string> Addresses { get; set; }
        public RouteDecision() { Target = RuleTarget.Review; Addresses = new List<string>(); }
    }

    internal sealed class ParsedModeRule
    {
        internal RulePattern Pattern;
        internal RuleTarget Target;
        internal int ExactRank;
        internal int SuffixRank;
    }

    internal static class PlanningValidation
    {
        internal static bool TryIpv4(string text, out uint value, out string canonical)
        {
            value = 0; canonical = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Trim().Split('.');
            if (parts.Length != 4) return false;
            byte[] bytes = new byte[4];
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length < 1 || parts[i].Length > 3 || parts[i].Any(delegate(char c) { return c < '0' || c > '9'; }) ||
                    !byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i])) return false;
                value = (value << 8) | bytes[i];
            }
            canonical = new IPAddress(bytes).ToString();
            return true;
        }

        internal static uint Mask(int prefix) { return prefix == 0 ? 0U : uint.MaxValue << (32 - prefix); }
        internal static bool Overlaps(uint first, uint last, uint otherFirst, uint otherLast)
        { return first <= otherLast && otherFirst <= last; }

        internal static bool SafeRange(uint first, uint last)
        {
            // Unspecified, loopback, link-local, fake-IP, multicast and reserved.
            return !Overlaps(first, last, 0x00000000U, 0x00FFFFFFU) &&
                !Overlaps(first, last, 0x7F000000U, 0x7FFFFFFFU) &&
                !Overlaps(first, last, 0xA9FE0000U, 0xA9FEFFFFU) &&
                !Overlaps(first, last, 0xC6120000U, 0xC613FFFFU) &&
                !Overlaps(first, last, 0xE0000000U, uint.MaxValue);
        }

        internal static bool TrySafeIpv4(string text, out uint value, out string canonical)
        { return TryIpv4(text, out value, out canonical) && SafeRange(value, value); }

        internal static HashSet<uint> AddressSet(IEnumerable<string> addresses)
        {
            HashSet<uint> result = new HashSet<uint>();
            foreach (string text in addresses ?? new string[0])
            {
                uint address; string canonical;
                if (TryIpv4(text, out address, out canonical)) result.Add(address);
            }
            return result;
        }

        // Shared by static planning and the guarded dynamic-answer overload.
        internal static string AddressConflict(NetworkSnapshot snapshot, AdapterSnapshot corporate,
            AdapterSnapshot internet, RuleTarget target, uint address)
        {
            if (target == RuleTarget.Corporate)
            {
                foreach (AdapterSnapshot adapter in snapshot.Adapters ?? new List<AdapterSnapshot>())
                {
                    uint local; string canonical;
                    if (adapter != null && TryIpv4(adapter.Address, out local, out canonical) && local == address)
                        return "公司目标不能指向本机任一接口地址。";
                }
                uint internetAddress; string internetCanonical;
                if (internet.PrefixLength >= 1 && internet.PrefixLength <= 31 &&
                    TryIpv4(internet.Address, out internetAddress, out internetCanonical) &&
                    (address & Mask(internet.PrefixLength)) == (internetAddress & Mask(internet.PrefixLength)))
                    return "公司目标落入外网接口直连子网，拒绝覆盖外网网关或本地设备。";
                if (AddressSet(internet.DnsServers).Contains(address))
                    return "公司目标覆盖外网 DNS 地址，可能破坏外网解析。";
            }
            else if (target == RuleTarget.Internet)
            {
                HashSet<uint> protectedAddresses = AddressSet(corporate.DnsServers);
                foreach (uint protectedAddress in AddressSet(new[] { corporate.Address, corporate.Gateway })) protectedAddresses.Add(protectedAddress);
                if (protectedAddresses.Contains(address)) return "外网目标覆盖公司 DNS、网关或本机公司接口地址。";
            }
            return null;
        }

        internal static bool TryCidr(string text, out uint first, out uint last, out string canonical)
        {
            first = 0; last = 0; canonical = null;
            string[] parts = (text ?? string.Empty).Trim().Split('/');
            int prefix; uint address; string ip;
            if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) ||
                prefix < 1 || prefix > 32 || !TryIpv4(parts[0], out address, out ip)) return false;
            uint mask = Mask(prefix);
            first = address & mask; last = first | ~mask;
            if (!SafeRange(first, last)) return false;
            canonical = new IPAddress(new byte[] { (byte)(first >> 24), (byte)(first >> 16), (byte)(first >> 8), (byte)first }).ToString() + "/" + prefix.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        internal static bool TryRule(ModeRule rule, out ParsedModeRule parsed, out string error)
        {
            parsed = null; error = null;
            if (rule == null || !Enum.IsDefined(typeof(RuleTarget), rule.Target)) { error = "规则或规则目标无效。"; return false; }
            RulePattern pattern;
            if (!RulePattern.TryCreate(rule.Pattern, out pattern, out error)) return false;
            IPAddress address = pattern.GetDirectIpAddress();
            if (address != null)
            {
                uint number; string canonical;
                if (!TrySafeIpv4(address.ToString(), out number, out canonical)) { error = "IP 规则包含伪 IP、回环、组播或其他不安全地址。"; return false; }
            }
            string normalized = pattern.Text;
            bool wildcard = normalized.IndexOfAny(new[] { '*', '?' }) >= 0;
            bool exact = !normalized.StartsWith(".", StringComparison.Ordinal) && !wildcard;
            string[] labels = normalized.TrimStart('.').Split('.');
            int fixedFrom = labels.Length;
            for (int i = labels.Length - 1; i >= 0; i--)
            {
                if (labels[i].IndexOfAny(new[] { '*', '?' }) >= 0) break;
                fixedFrom = i;
            }
            string suffix = string.Join(".", labels.Skip(fixedFrom).ToArray());
            parsed = new ParsedModeRule { Pattern = pattern, Target = rule.Target, ExactRank = exact ? 1 : 0, SuffixRank = suffix.Length };
            return true;
        }
    }

    public static class RuleRouter
    {
        // Addresses MUST already be validated answers for host or its verified
        // CNAME chain. This pure router neither queries DNS nor proves provenance.
        public static RouteDecision Decide(List<ModeRule> rules, string host, List<string> resolvedAddresses)
        {
            if (string.IsNullOrWhiteSpace(host)) return Reject("请求域名为空。");
            RulePattern normalizedHost; string hostError;
            if (!RulePattern.TryCreate(host, out normalizedHost, out hostError) ||
                normalizedHost.Text.StartsWith(".", StringComparison.Ordinal) || normalizedHost.Text.IndexOfAny(new[] { '*', '?' }) >= 0)
                return Reject("请求必须是精确域名或 IPv4 地址。");
            IPAddress literalHost = normalizedHost.GetDirectIpAddress();
            if (literalHost != null)
            {
                uint literalNumber; string literalCanonical;
                if (!PlanningValidation.TrySafeIpv4(literalHost.ToString(), out literalNumber, out literalCanonical))
                    return Reject("请求 IP 属于伪 IP、回环、组播或其他不安全地址。");
            }
            List<ParsedModeRule> matches = new List<ParsedModeRule>();
            foreach (ModeRule rule in rules ?? new List<ModeRule>())
            {
                ParsedModeRule parsed; string error;
                if (!PlanningValidation.TryRule(rule, out parsed, out error)) return Reject("规则无效：" + error);
                if (parsed.Pattern.Matches(normalizedHost.Text)) matches.Add(parsed);
            }
            RuleTarget target = RuleTarget.Internet;
            if (matches.Count > 0)
            {
                int exactRank = matches.Max(delegate(ParsedModeRule p) { return p.ExactRank; });
                List<ParsedModeRule> ranked = matches.Where(delegate(ParsedModeRule p) { return p.ExactRank == exactRank; }).ToList();
                int suffixRank = ranked.Max(delegate(ParsedModeRule p) { return p.SuffixRank; });
                List<RuleTarget> targets = ranked.Where(delegate(ParsedModeRule p) { return p.SuffixRank == suffixRank; })
                    .Select(delegate(ParsedModeRule p) { return p.Target; }).Distinct().ToList();
                if (targets.Count != 1) return Reject("同优先级规则目标冲突，未选择任何出口。");
                target = targets[0];
                if (target == RuleTarget.Review) return Reject("命中的规则尚未确认公司或外网目标。");
            }
            RouteDecision decision = new RouteDecision { Target = target, Reason = matches.Count == 0 ? "未命中规则，使用外网默认出口。" : "已按精确域名及固定后缀具体程度选择目标。" };
            foreach (string text in resolvedAddresses ?? new List<string>())
            {
                uint value; string canonical;
                if (!PlanningValidation.TrySafeIpv4(text, out value, out canonical)) return Reject("DNS 答案含无效、IPv6、伪 IP、回环、组播或其他不安全地址。", target);
                if (literalHost != null && canonical != literalHost.ToString()) return Reject("IP 请求的答案与原始地址不一致。", target);
                if (!decision.Addresses.Contains(canonical)) decision.Addresses.Add(canonical);
            }
            if (target == RuleTarget.Corporate && decision.Addresses.Count == 0) return Reject("公司目标缺少已验证的 IPv4 DNS 答案。", target);
            return decision;
        }

        // The UI must use this overload for dual-network previews. The older
        // overload only ranks rules; it cannot establish safe egress boundaries.
        public static RouteDecision Decide(List<ModeRule> rules, string host, List<string> resolvedAddresses,
            ModeRequest request, NetworkSnapshot snapshot)
        {
            RouteDecision decision = Decide(rules, host, resolvedAddresses);
            if (decision.Blocked) return decision;
            ModePlan plan = NetworkModePlanner.Build(request, snapshot);
            if (!plan.CanSimulate)
                return Reject("双网规划尚未通过安全门禁：" + (plan.Blockers.FirstOrDefault() ?? plan.Status), decision.Target);
            if (!EquivalentRules(rules, request.Rules))
                return Reject("域名规则与模式请求快照不一致，拒绝使用过期或不匹配的规则。", decision.Target);
            AdapterSnapshot corporate = snapshot.Adapters.Single(delegate(AdapterSnapshot a)
                { return a != null && string.Equals(a.Id, request.CorporateAdapterId, StringComparison.OrdinalIgnoreCase); });
            AdapterSnapshot internet = snapshot.Adapters.Single(delegate(AdapterSnapshot a)
                { return a != null && string.Equals(a.Id, request.InternetAdapterId, StringComparison.OrdinalIgnoreCase); });
            foreach (string text in decision.Addresses)
            {
                uint address; string canonical;
                if (!PlanningValidation.TrySafeIpv4(text, out address, out canonical)) return Reject("动态地址无效。", decision.Target);
                string conflict = PlanningValidation.AddressConflict(snapshot, corporate, internet, decision.Target, address);
                if (conflict != null) return Reject(conflict, decision.Target);
            }
            return decision;
        }

        private static bool EquivalentRules(List<ModeRule> left, List<ModeRule> right)
        {
            HashSet<string> leftKeys = RuleKeys(left), rightKeys = RuleKeys(right);
            return leftKeys != null && rightKeys != null && leftKeys.SetEquals(rightKeys);
        }

        private static HashSet<string> RuleKeys(List<ModeRule> rules)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModeRule rule in rules ?? new List<ModeRule>())
            {
                ParsedModeRule parsed; string error;
                if (!PlanningValidation.TryRule(rule, out parsed, out error)) return null;
                result.Add(parsed.Pattern.Text + "|" + parsed.Target.ToString());
            }
            return result;
        }

        private static RouteDecision Reject(string reason, RuleTarget target = RuleTarget.Review)
        { return new RouteDecision { Target = target, Blocked = true, Reason = reason }; }
    }

    public sealed class CorporateCidrPlan
    {
        public bool IsValid { get; set; }
        public List<string> Routes { get; set; }
        public List<string> ExcludedRanges { get; set; }
        public List<string> Errors { get; set; }
        public List<string> Warnings { get; set; }
        public CorporateCidrPlan()
        { Routes = new List<string>(); ExcludedRanges = new List<string>(); Errors = new List<string>(); Warnings = new List<string>(); }
    }

    // Pure set subtraction, not permission to apply network changes. Callers
    // must separately pass Build/BuildPreparation and preserve this snapshot.
    public static class CorporateCidrPlanner
    {
        public const int MaximumRouteCount = 256;
        private const int MaximumInputCidrs = 128;
        private sealed class Range
        {
            internal ulong First, Last;
            internal Range(ulong first, ulong last) { First = first; Last = last; }
        }

        public static CorporateCidrPlan Build(ModeRequest request, NetworkSnapshot snapshot)
        {
            CorporateCidrPlan result = new CorporateCidrPlan();
            if (request == null || snapshot == null || snapshot.Adapters == null ||
                (request.Mode != WorkMode.WiredCorporateWifiInternet && request.Mode != WorkMode.WifiCorporateWiredInternet) ||
                string.IsNullOrWhiteSpace(request.CorporateAdapterId) || string.IsNullOrWhiteSpace(request.InternetAdapterId) ||
                string.Equals(request.CorporateAdapterId, request.InternetAdapterId, StringComparison.OrdinalIgnoreCase))
                return Reject(result, "公司网段规划缺少明确的双网角色或快照。");
            if (snapshot.Adapters.Count > 128 || (request.CorporateCidrs != null && request.CorporateCidrs.Count > MaximumInputCidrs) ||
                (request.Rules != null && request.Rules.Count > 1000)) return Reject(result, "公司网段规划输入超过安全上限。");
            List<AdapterSnapshot> companyMatches = snapshot.Adapters.Where(a => a != null && string.Equals(a.Id, request.CorporateAdapterId, StringComparison.OrdinalIgnoreCase)).ToList();
            List<AdapterSnapshot> internetMatches = snapshot.Adapters.Where(a => a != null && string.Equals(a.Id, request.InternetAdapterId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (companyMatches.Count != 1 || internetMatches.Count != 1) return Reject(result, "公司网段规划的接口 ID 缺失或重复。");
            AdapterSnapshot company = companyMatches[0], internet = internetMatches[0];
            LinkKind companyKind = request.Mode == WorkMode.WiredCorporateWifiInternet ? LinkKind.Ethernet : LinkKind.Wifi;
            LinkKind internetKind = request.Mode == WorkMode.WiredCorporateWifiInternet ? LinkKind.Wifi : LinkKind.Ethernet;
            if (!company.IsPhysical || !internet.IsPhysical || company.Kind != companyKind || internet.Kind != internetKind || !internet.IsUp)
                return Reject(result, "公司网段规划要求可识别的物理接口，以及已连接的外网接口。");
            uint address, gateway; string canonical;
            if (internet.PrefixLength < 1 || internet.PrefixLength > 31 ||
                !PlanningValidation.TrySafeIpv4(internet.Address, out address, out canonical) ||
                !PlanningValidation.TrySafeIpv4(internet.Gateway, out gateway, out canonical))
                return Reject(result, "不能确认外网本地子网和网关，拒绝拆分公司网段。");
            uint mask = PlanningValidation.Mask(internet.PrefixLength), first = address & mask, last = first | ~mask;
            if ((gateway & mask) != first || gateway == address || !PlanningValidation.SafeRange(first, last) ||
                (internet.PrefixLength < 31 && (address == first || address == last || gateway == first || gateway == last)))
                return Reject(result, "外网地址或网关不满足同子网主机条件。");
            if (internet.DnsServers == null || internet.DnsServers.Count == 0 || internet.DnsServers.Count > 32)
                return Reject(result, "外网 DNS 保护范围缺失或过多。");
            List<Range> protectedRanges = new List<Range> { new Range(first, last), new Range(gateway, gateway) };
            foreach (string dns in internet.DnsServers)
            {
                uint value;
                if (!PlanningValidation.TrySafeIpv4(dns, out value, out canonical)) return Reject(result, "外网 DNS 保护地址无效。");
                protectedRanges.Add(new Range(value, value));
            }
            // Preserve every represented local IPv4, including the corporate
            // source IP; never install a gateway route to this computer itself.
            foreach (AdapterSnapshot adapter in snapshot.Adapters)
            {
                if (adapter == null || string.IsNullOrWhiteSpace(adapter.Address)) continue;
                uint value;
                if (!PlanningValidation.TryIpv4(adapter.Address, out value, out canonical)) return Reject(result, "本机接口地址无法安全解析。");
                protectedRanges.Add(new Range(value, value));
            }
            foreach (ModeRule rule in request.Rules ?? new List<ModeRule>())
            {
                ParsedModeRule parsed; string error;
                if (!PlanningValidation.TryRule(rule, out parsed, out error)) return Reject(result, "公司网段规划存在无效规则。");
                if (parsed.Target != RuleTarget.Internet) continue;
                IPAddress explicitIp = parsed.Pattern.GetDirectIpAddress();
                uint value;
                if (explicitIp != null && PlanningValidation.TrySafeIpv4(explicitIp.ToString(), out value, out canonical))
                    protectedRanges.Add(new Range(value, value));
            }
            List<Range> inputs = new List<Range>();
            foreach (string raw in request.CorporateCidrs ?? new List<string>())
            {
                uint lower, upper;
                if (!PlanningValidation.TryCidr(raw, out lower, out upper, out canonical)) return Reject(result, "公司网段无效或包含不安全地址。");
                inputs.Add(new Range(lower, upper));
            }
            List<Range> holes = Merge(protectedRanges), remaining = new List<Range>();
            bool removed = false;
            foreach (Range input in Merge(inputs))
            {
                ulong cursor = input.First; int before = remaining.Count;
                foreach (Range hole in holes)
                {
                    if (hole.Last < cursor) continue;
                    if (hole.First > input.Last) break;
                    removed = true;
                    if (hole.First > cursor) remaining.Add(new Range(cursor, Math.Min(input.Last, hole.First - 1)));
                    cursor = Math.Max(cursor, hole.Last + 1);
                    if (cursor > input.Last) break;
                }
                if (cursor <= input.Last) remaining.Add(new Range(cursor, input.Last));
                if (remaining.Count == before) return Reject(result, "指定公司网段完全属于外网或本机保护范围，拒绝生成劫持路由。");
            }
            result.Routes = ToCidrs(Merge(remaining), MaximumRouteCount);
            if (result.Routes == null) { result.Routes = new List<string>(); return Reject(result, "公司网段安全拆分后超过 256 条路由上限。"); }
            result.ExcludedRanges = ToCidrs(holes, 4096);
            if (result.ExcludedRanges == null) return Reject(result, "外网保护范围超过安全上限。");
            if (removed) result.Warnings.Add("已从显式公司网段扣除外网本地子网、DNS、本机地址及显式外网 IP；仅剩余部分走公司接口。");
            result.IsValid = true;
            return result;
        }

        private static CorporateCidrPlan Reject(CorporateCidrPlan result, string error)
        { result.IsValid = false; result.Routes.Clear(); result.Errors.Add(error); return result; }
        private static List<Range> Merge(IEnumerable<Range> ranges)
        {
            List<Range> merged = new List<Range>();
            foreach (Range current in ranges.OrderBy(r => r.First).ThenBy(r => r.Last))
            {
                Range previous = merged.LastOrDefault();
                if (previous == null || current.First > previous.Last + 1) merged.Add(new Range(current.First, current.Last));
                else previous.Last = Math.Max(previous.Last, current.Last);
            }
            return merged;
        }
        private static List<string> ToCidrs(List<Range> ranges, int limit)
        {
            List<string> result = new List<string>();
            foreach (Range range in ranges)
            {
                ulong cursor = range.First;
                while (cursor <= range.Last)
                {
                    ulong size = 1, available = range.Last - cursor + 1; int prefix = 32;
                    while (prefix > 0 && size * 2 <= available && cursor % (size * 2) == 0) { size *= 2; prefix--; }
                    uint value = (uint)cursor;
                    result.Add(new IPAddress(new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }).ToString() + "/" + prefix.ToString(CultureInfo.InvariantCulture));
                    if (result.Count > limit) return null;
                    cursor += size;
                }
            }
            return result;
        }
    }

    public static class NetworkModePlanner
    {
        public static string Ipv6CompatibilityBlocker(ModeRequest request, NetworkSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Adapters == null) return "缺少 IPv6 出口核验快照。";
            List<AdapterSnapshot> evidence = snapshot.Adapters.Where(a => a != null &&
                (a.HasIpv6Default || !string.IsNullOrEmpty(a.Ipv6RouteSignature))).ToList();
            if (evidence.Count == 0) return null;
            if (request == null || (request.Mode != WorkMode.WiredCorporateWifiInternet && request.Mode != WorkMode.WifiCorporateWiredInternet) ||
                string.IsNullOrWhiteSpace(request.InternetAdapterId) || string.IsNullOrWhiteSpace(request.CorporateAdapterId) ||
                string.Equals(request.InternetAdapterId, request.CorporateAdapterId, StringComparison.OrdinalIgnoreCase))
                return "IPv6 出口需要明确指定不同的内外网物理接口。";
            List<AdapterSnapshot> internet = snapshot.Adapters.Where(a => a != null &&
                string.Equals(a.Id, request.InternetAdapterId, StringComparison.OrdinalIgnoreCase)).ToList();
            LinkKind expected = request.Mode == WorkMode.WiredCorporateWifiInternet ? LinkKind.Wifi : LinkKind.Ethernet;
            if (internet.Count != 1 || !internet[0].IsUp || !internet[0].IsPhysical || internet[0].Kind != expected)
                return "无法核验 IPv6 外网出口为已连接的指定物理接口。";
            foreach (AdapterSnapshot adapter in evidence)
            {
                if (!string.Equals(adapter.Id, request.InternetAdapterId, StringComparison.OrdinalIgnoreCase))
                    return "公司或其他接口存在 IPv6 广域出口，暂停分流且不修改 IPv6 设置。";
                if (!adapter.HasIpv6Default || !ValidIpv6RouteSignature(adapter.Ipv6RouteSignature))
                    return "IPv6 出口路由证据不完整，不能仅凭确认标记放行。";
            }
            return null;
        }

        internal static bool ValidIpv6RouteSignature(string signature)
        {
            if (string.IsNullOrWhiteSpace(signature) || signature.Length > 16384) return false;
            string[] rows = signature.Split(';');
            if (rows.Length > 128) return false;
            foreach (string row in rows)
            {
                string[] fields = row.Split('|'); IPAddress nextHop; long routeMetric, interfaceMetric;
                if (fields.Length != 4 || !new[] { "::/0", "::/1", "8000::/1", "2000::/3" }.Contains(fields[0]) ||
                    !IPAddress.TryParse(fields[1], out nextHop) || nextHop.GetAddressBytes().Length != 16 ||
                    nextHop.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(nextHop) || nextHop.IsIPv6Multicast ||
                    !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out routeMetric) || routeMetric < 0 ||
                    !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out interfaceMetric) || interfaceMetric < 1)
                    return false;
            }
            return true;
        }

        public static ModePlan Build(ModeRequest request, NetworkSnapshot snapshot)
        { return BuildCore(request, snapshot, false); }

        // Preparation is not admission. Do not rewrite the caller's snapshot to
        // bypass the normal start gate; all other safety checks remain identical.
        public static ModePlan BuildPreparation(ModeRequest request, NetworkSnapshot snapshot)
        { return BuildCore(request, snapshot, true); }

        private static ModePlan BuildCore(ModeRequest request, NetworkSnapshot snapshot, bool preparation)
        {
            ModePlan plan = new ModePlan();
            if (request == null) return Block(plan, "缺少模式请求。");
            if (request.Mode == WorkMode.Normal || request.Mode == WorkMode.FastLinkSplit)
            {
                plan.Status = request.Mode == WorkMode.Normal ? "NormalPreserved" : "FastLinkPreserved";
                plan.Warnings.Add("保留原有模式；本规划器不修改或接管现有运行行为。");
                return plan;
            }
            if (request.Mode != WorkMode.WiredCorporateWifiInternet && request.Mode != WorkMode.WifiCorporateWiredInternet)
                return Block(plan, "不支持的工作模式。");
            if (snapshot == null) return Block(plan, "缺少网络快照。");
            if (string.IsNullOrWhiteSpace(request.CorporateAdapterId) || string.IsNullOrWhiteSpace(request.InternetAdapterId) ||
                string.Equals(request.CorporateAdapterId, request.InternetAdapterId, StringComparison.OrdinalIgnoreCase))
                return Block(plan, "必须明确选择两个不同的物理接口 ID，不自动猜测。");
            List<AdapterSnapshot> adapters = snapshot.Adapters ?? new List<AdapterSnapshot>();
            List<AdapterSnapshot> corporateMatches = adapters.Where(delegate(AdapterSnapshot a) { return a != null && string.Equals(a.Id, request.CorporateAdapterId, StringComparison.OrdinalIgnoreCase); }).ToList();
            List<AdapterSnapshot> internetMatches = adapters.Where(delegate(AdapterSnapshot a) { return a != null && string.Equals(a.Id, request.InternetAdapterId, StringComparison.OrdinalIgnoreCase); }).ToList();
            if (corporateMatches.Count > 1 || internetMatches.Count > 1) return Block(plan, "快照包含重复接口 ID，拒绝选择不确定的接口。");
            if (internetMatches.Count != 1 || !internetMatches[0].IsUp) return Block(plan, "指定外网接口未连接，保留当前网络不生成修改意图。");
            if (corporateMatches.Count != 1 || !corporateMatches[0].IsUp)
            {
                plan.Status = "Waiting"; plan.Blockers.Add("等待指定公司接口连接；不生成修改意图。"); return plan;
            }
            AdapterSnapshot corporate = corporateMatches[0], internet = internetMatches[0];
            LinkKind corporateKind = request.Mode == WorkMode.WiredCorporateWifiInternet ? LinkKind.Ethernet : LinkKind.Wifi;
            LinkKind internetKind = request.Mode == WorkMode.WiredCorporateWifiInternet ? LinkKind.Wifi : LinkKind.Ethernet;
            if (!corporate.IsPhysical || !internet.IsPhysical || corporate.Kind != corporateKind || internet.Kind != internetKind)
                plan.Blockers.Add("指定接口必须为此模式要求的物理有线/Wi-Fi，虚拟接口或类型不匹配不可用。");
            if (snapshot.SystemProxyActive || snapshot.TunnelActive) plan.Blockers.Add("检测到系统代理或隧道；不会关闭、改写或绕开现有代理。");
            if (snapshot.CleanupPending) plan.Blockers.Add("原模式尚有待完成清理，禁止规划切换。");
            if (snapshot.OtherDnsPolicyPresent) plan.Blockers.Add("存在非本工具 DNS 策略，禁止覆盖企业或其他软件策略。");
            if (!preparation && !snapshot.CorporateAdmissionVerified) plan.Blockers.Add("公司网络准入尚未确认。");
            if (!snapshot.DualConnectionAllowed) plan.Blockers.Add("公司是否允许双网同时连接尚未确认。");
            if (!snapshot.InternetReachabilityVerified) plan.Blockers.Add("指定外网接口的外网可用性尚未确认。");
            if (!preparation && !snapshot.ExternalDefaultPreferred) plan.Blockers.Add("尚未确认外网接口为首选默认出口，不能保证通信不中断。");
            if (snapshot.StableSeconds < 8) plan.Blockers.Add("所选网络尚未稳定至少 8 秒。");
            string ipv6Blocker = Ipv6CompatibilityBlocker(request, snapshot);
            if (ipv6Blocker != null) plan.Blockers.Add(ipv6Blocker);
            else if (internet.HasIpv6Default) plan.Warnings.Add("仅按规则进行 IPv4 分流，保留外网现有 IPv6；不接管应用缓存、独立 DNS 或 IPv6 直连。");

            uint corporateAddress, internetAddress, corporateMask, internetMask;
            ValidateAdapter(corporate, "公司", plan, out corporateAddress, out corporateMask);
            ValidateAdapter(internet, "外网", plan, out internetAddress, out internetMask);
            if (corporateAddress == internetAddress) plan.Blockers.Add("公司和外网接口地址相同，出口不可明确区分。");
            uint corporateFirst = corporateAddress & corporateMask, corporateLast = corporateFirst | ~corporateMask;
            uint internetFirstAddress = internetAddress & internetMask, internetLastAddress = internetFirstAddress | ~internetMask;
            if (PlanningValidation.Overlaps(corporateFirst, corporateLast, internetFirstAddress, internetLastAddress))
                plan.Blockers.Add("公司和外网接口的直连子网重叠，不能安全区分 DNS、网关和本地设备。");
            foreach (string dns in corporate.DnsServers ?? new List<string>())
            {
                uint dnsAddress; string canonicalDns;
                if (PlanningValidation.TryIpv4(dns, out dnsAddress, out canonicalDns))
                {
                    string conflict = PlanningValidation.AddressConflict(snapshot, corporate, internet, RuleTarget.Corporate, dnsAddress);
                    if (conflict != null) plan.Blockers.Add("公司 DNS：" + conflict);
                }
            }
            HashSet<uint> internetDnsAddresses = PlanningValidation.AddressSet(internet.DnsServers);
            if (PlanningValidation.AddressSet(corporate.DnsServers).Overlaps(internetDnsAddresses))
                plan.Blockers.Add("两个出口使用相同 DNS 地址，当前规划无法证明请求经正确接口发送。");

            List<ParsedModeRule> parsedRules = new List<ParsedModeRule>();
            foreach (ModeRule rule in request.Rules ?? new List<ModeRule>())
            {
                ParsedModeRule parsed; string error;
                if (!PlanningValidation.TryRule(rule, out parsed, out error)) { plan.Blockers.Add("规则无效：" + error); continue; }
                if (parsed.Target == RuleTarget.Review) plan.Blockers.Add("规则尚未分类：" + parsed.Pattern.Text);
                parsedRules.Add(parsed);
            }
            foreach (IGrouping<string, ParsedModeRule> group in parsedRules.GroupBy(delegate(ParsedModeRule p) { return p.Pattern.Text; }, StringComparer.OrdinalIgnoreCase))
                if (group.Select(delegate(ParsedModeRule p) { return p.Target; }).Distinct().Count() > 1) plan.Blockers.Add("相同规则存在相反目标：" + group.Key);
            // A suffix and a star-suffix have equal priority on their shared hosts.
            foreach (ParsedModeRule a in parsedRules)
                foreach (ParsedModeRule b in parsedRules)
                    if (a.Target != b.Target && a.Pattern.Text.StartsWith(".", StringComparison.Ordinal) && b.Pattern.Text == "*" + a.Pattern.Text)
                        plan.Blockers.Add("后缀和通配后缀目标冲突：" + a.Pattern.Text);

            CorporateCidrPlan cidrPlan = CorporateCidrPlanner.Build(request, snapshot);
            plan.Blockers.AddRange(cidrPlan.Errors); plan.Warnings.AddRange(cidrPlan.Warnings);
            List<string> cidrs = cidrPlan.Routes;
            foreach (ParsedModeRule rule in parsedRules)
            {
                IPAddress ip = rule.Pattern.GetDirectIpAddress();
                if (ip == null) continue;
                uint number; string canonical;
                if (PlanningValidation.TryIpv4(ip.ToString(), out number, out canonical))
                {
                    string conflict = PlanningValidation.AddressConflict(snapshot, corporate, internet, rule.Target, number);
                    if (conflict != null) plan.Blockers.Add("IP 规则 " + canonical + "：" + conflict);
                }
            }
            if (plan.Blockers.Count > 0) { plan.Blockers = plan.Blockers.Distinct().ToList(); return plan; }

            Add(plan, "ProtectInternetDefault", "0.0.0.0/0", internet, "保护并核对外网出口优先级；不删除任何现有默认路由。");
            Add(plan, "InternetDnsFallback", string.Join(",", internet.DnsServers.ToArray()), internet, "未命中公司规则的解析使用外网 DNS；不得泄露公司查询到外网。");
            foreach (string dns in corporate.DnsServers.Distinct(StringComparer.OrdinalIgnoreCase))
                Add(plan, "CorporateDnsHostRoute", dns + "/32", corporate, "确保公司 DNS 请求经指定公司接口；执行阶段需绑定源地址并核验路径。");
            foreach (ParsedModeRule rule in parsedRules.GroupBy(delegate(ParsedModeRule p) { return p.Pattern.Text + "|" + p.Target; }, StringComparer.OrdinalIgnoreCase).Select(delegate(IGrouping<string, ParsedModeRule> g) { return g.First(); }))
            {
                AdapterSnapshot target = rule.Target == RuleTarget.Corporate ? corporate : internet;
                IPAddress directIp = rule.Pattern.GetDirectIpAddress();
                if (directIp != null)
                    Add(plan, rule.Target == RuleTarget.Corporate ? "CorporateIpRoute" : "InternetIpRoute", directIp + "/32", target, "显式 IP 规则；仅规划此地址，不扩大到整个私网。");
                else
                {
                    Add(plan, rule.Target == RuleTarget.Corporate ? "CorporateSplitDns" : "InternetSplitDns", rule.Pattern.Text, target, "保留精确/后缀/通配规则语义，以规则路由器处理优先级。");
                    Add(plan, rule.Target == RuleTarget.Corporate ? "CorporateDynamicIpRoute" : "InternetDynamicIpRoute", rule.Pattern.Text, target, "仅为该域名或已验证 CNAME 链的 IPv4 答案规划 /32；过时代次回调必须拒绝。");
                }
            }
            foreach (string cidr in cidrs) Add(plan, "CorporateCidrRoute", cidr, corporate, "显式公司网段扣除外网与本机保护范围后的最小路由；不自动扩大网段。");
            plan.CanSimulate = true; plan.Status = preparation ? "ReadyForPreparation" : "ReadyForSimulation";
            if (preparation) plan.Warnings.Add("仅供准备公司认证路径；不表示准入通过，正式启用仍须满足完整准入和外网优先条件。");
            plan.Warnings.Add("仅为离线规划，未验证真实网络，也未应用任何配置。");
            plan.Warnings.Add("双网断连、系统策略、IPv6、应用自带 DNS/代理及共享 IP 均可能影响实际分流；没有自动切网或重启行为。");
            plan.Warnings.Add("已有规则必须逐项确认目标；有道、华为等公网域名不会自动判为公司内网。");
            return plan;
        }

        private static ModePlan Block(ModePlan plan, string reason) { plan.Blockers.Add(reason); return plan; }
        private static void Add(ModePlan plan, string kind, string destination, AdapterSnapshot adapter, string description)
        { plan.Intents.Add(new PlanIntent { Kind = kind, Destination = destination, InterfaceId = adapter.Id, Gateway = adapter.Gateway, Description = description }); }

        private static void ValidateAdapter(AdapterSnapshot adapter, string label, ModePlan plan, out uint address, out uint mask)
        {
            string canonical; uint gateway;
            bool addressValid = PlanningValidation.TrySafeIpv4(adapter.Address, out address, out canonical);
            bool prefixValid = adapter.PrefixLength >= 1 && adapter.PrefixLength <= 31;
            mask = prefixValid ? PlanningValidation.Mask(adapter.PrefixLength) : 0U;
            bool gatewayValid = PlanningValidation.TrySafeIpv4(adapter.Gateway, out gateway, out canonical);
            if (!addressValid || !prefixValid || !gatewayValid)
                plan.Blockers.Add(label + "接口缺少有效 IPv4、前缀或网关（暂不支持 IPv6/主机前缀接口）。");
            else
            {
                uint network = address & mask, broadcast = network | ~mask;
                if ((gateway & mask) != network || gateway == address ||
                    (adapter.PrefixLength < 31 && (address == network || address == broadcast || gateway == network || gateway == broadcast)))
                    plan.Blockers.Add(label + "接口的地址或网关不满足有效同子网主机条件。");
            }
            if (adapter.DnsServers == null || adapter.DnsServers.Count == 0) plan.Blockers.Add(label + "接口缺少明确的 DNS。");
            else foreach (string dns in adapter.DnsServers)
            {
                uint number;
                if (!PlanningValidation.TrySafeIpv4(dns, out number, out canonical)) plan.Blockers.Add(label + "接口 DNS 必须是有效且安全的 IPv4 地址。");
            }
        }
    }
}
