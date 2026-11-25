using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace FastLinkDirectTool.Modes
{
    public enum DualPermissionSource { None = 0, UserAction = 1, PriorExplicitConversationConsent = 2 }

    // Durable permission is not a network fact. This record deliberately has no
    // InternetReachabilityVerified or CorporateAdmissionVerified field.
    public sealed class DualPermissionConfirmation
    {
        public string ScopeIdentity { get; set; }
        public string InterfaceId1 { get; set; }
        public string InterfaceId2 { get; set; }
        public DateTime ConfirmedAtUtc { get; set; }
        public DualPermissionSource Source { get; set; }

        internal bool IsWellFormed()
        {
            string first, second;
            return ValidScope(ScopeIdentity) && TryCanonicalPair(InterfaceId1, InterfaceId2, out first, out second) &&
                InterfaceId1 == first && InterfaceId2 == second &&
                ConfirmedAtUtc.Kind == DateTimeKind.Utc && ConfirmedAtUtc >= new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) &&
                ConfirmedAtUtc <= DateTime.UtcNow.AddMinutes(5) &&
                (Source == DualPermissionSource.UserAction || Source == DualPermissionSource.PriorExplicitConversationConsent);
        }

        internal static bool ValidScope(string scope)
        { return !string.IsNullOrWhiteSpace(scope) && scope.Length <= 512 && scope == scope.Trim(); }

        internal static bool TryCanonicalPair(string left, string right, out string first, out string second)
        {
            first = second = null; Guid a, b;
            if (!Guid.TryParse(left, out a) || !Guid.TryParse(right, out b) || a == Guid.Empty || b == Guid.Empty || a == b) return false;
            string x = a.ToString("D").ToLowerInvariant(), y = b.ToString("D").ToLowerInvariant();
            first = string.CompareOrdinal(x, y) < 0 ? x : y;
            second = string.CompareOrdinal(x, y) < 0 ? y : x;
            return true;
        }
    }

    public sealed class DualModeProfile
    {
        public WorkMode Mode { get; set; }
        public string CorporateAdapterId { get; set; }
        public string InternetAdapterId { get; set; }
        public List<ModeRule> Rules { get; set; }
        public List<string> CorporateCidrs { get; set; }
        public bool RulesConfirmed { get; set; }
        public DualPermissionConfirmation PermissionConfirmation { get; set; }
        public DualModeProfile()
        {
            Mode = WorkMode.WiredCorporateWifiInternet;
            Rules = new List<ModeRule>(); CorporateCidrs = new List<string>();
        }
        public bool IsPermissionConfirmed(string scopeIdentity, string corporateId, string internetId)
        {
            string first, second;
            DualPermissionConfirmation record = PermissionConfirmation;
            return record != null && record.IsWellFormed() &&
                string.Equals(scopeIdentity, record.ScopeIdentity, StringComparison.Ordinal) &&
                DualPermissionConfirmation.TryCanonicalPair(corporateId, internetId, out first, out second) &&
                record.InterfaceId1 == first && record.InterfaceId2 == second;
        }
        public void RecordPermission(string scopeIdentity, string corporateId, string internetId,
            DateTime confirmedAtUtc, DualPermissionSource source)
        {
            string first, second;
            if (!DualPermissionConfirmation.TryCanonicalPair(corporateId, internetId, out first, out second))
                throw new ArgumentException("公司双网许可需要两个不同的有效网卡 GUID。");
            var record = new DualPermissionConfirmation { ScopeIdentity = scopeIdentity, InterfaceId1 = first,
                InterfaceId2 = second, ConfirmedAtUtc = confirmedAtUtc, Source = source };
            if (!record.IsWellFormed()) throw new ArgumentException("公司双网许可的电脑用户范围、UTC 时间或明确许可来源无效。");
            // All validation happens first; a bad call cannot erase a prior grant.
            PermissionConfirmation = record;
        }
        public void ClearPermission() { PermissionConfirmation = null; }
        public ModeRequest ToRequest()
        {
            return new ModeRequest { Mode = Mode, CorporateAdapterId = CorporateAdapterId,
                InternetAdapterId = InternetAdapterId,
                Rules = Rules.Select(r => new ModeRule { Pattern = r.Pattern, Target = r.Target }).ToList(),
                CorporateCidrs = new List<string>(CorporateCidrs) };
        }
    }

    public static class DualRuleMigration
    {
        // Only public services are proposed automatically. Corporate domains and
        // private addresses require the user's explicit classification on first
        // import; existing saved profiles are loaded without rerunning migration.
        private static readonly string[] InternetSuffixes = { "youdao.com", "push.126.net", "wanproxy.127.net", "codebuddy.cn", "copilot.tencent.com", "galileotelemetry.tencent.com", "ydstatic.com", "lx.netease.com", "cowork.netease.com", "netease.com", "huawei.com", "hicloud.com", "dbankcdn.com", "dbankcdn.cn", "vmall.com", "datacenter-web.eastmoney.com", "sctapi.ftqq.com", "api.typeless.com", "typeless-static.com", "zoom.us", "zoom.com", "net.anydesk.com.cn", "net.anydesk.com", "teams.microsoft.com", "teams.cloud.microsoft" };
        public static List<ModeRule> Propose(IEnumerable<string> source)
        {
            List<ModeRule> rules = new List<ModeRule>();
            foreach (string raw in source ?? new string[0])
            {
                RulePattern parsed; string error;
                if (!RulePattern.TryCreate(raw, out parsed, out error)) throw new InvalidOperationException("旧规则有无效项，未修改配置：" + error);
                if (rules.Any(r => string.Equals(r.Pattern, parsed.Text, StringComparison.OrdinalIgnoreCase))) continue;
                string name = parsed.Text.TrimStart('*', '.');
                RuleTarget target = RuleTarget.Review;
                if (Matches(name, InternetSuffixes)) target = RuleTarget.Internet;
                rules.Add(new ModeRule { Pattern = parsed.Text, Target = target });
            }
            return rules;
        }
        private static bool Matches(string value, IEnumerable<string> suffixes)
        { return suffixes.Any(s => value == s || value.EndsWith("." + s, StringComparison.OrdinalIgnoreCase)); }
    }

    internal static class DualProfileStore
    {
        internal static string DirectoryPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastLinkDirectTool"); } }
        internal static string PathName { get { return Path.Combine(DirectoryPath, "dual-profile.json"); } }
        internal static DualModeProfile Load(IEnumerable<string> source)
        {
            return LoadFromPath(PathName, source);
        }
        // Purely read-only loading; the explicit path also permits isolated
        // saved-profile regression fixtures without touching LocalAppData.
        internal static DualModeProfile LoadFromPath(string path, IEnumerable<string> source)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("Profile path must be absolute.");
            if (!File.Exists(path)) return new DualModeProfile { Rules = DualRuleMigration.Propose(source) };
            FileInfo file = new FileInfo(path);
            if (file.Length > 1024 * 1024) throw new InvalidOperationException("双网配置过大，已停止读取。");
            DualModeProfile profile = new JavaScriptSerializer().Deserialize<DualModeProfile>(File.ReadAllText(path, Encoding.UTF8));
            Validate(profile);
            return profile;
        }
        internal static void Save(DualModeProfile profile)
        {
            Validate(profile);
            Directory.CreateDirectory(DirectoryPath);
            string temporary = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(profile), new UTF8Encoding(false));
                if (File.Exists(PathName)) File.Replace(temporary, PathName, PathName + ".bak");
                else File.Move(temporary, PathName);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static void Validate(DualModeProfile profile)
        {
            if (profile == null || profile.Rules == null || profile.CorporateCidrs == null || profile.Rules.Count > 1000 ||
                (profile.Mode != WorkMode.WiredCorporateWifiInternet && profile.Mode != WorkMode.WifiCorporateWiredInternet))
                throw new InvalidOperationException("双网配置无效；不会覆盖原文件。");
            foreach (ModeRule rule in profile.Rules)
            {
                RulePattern parsed; string error;
                if (rule == null || !Enum.IsDefined(typeof(RuleTarget), rule.Target) || !RulePattern.TryCreate(rule.Pattern, out parsed, out error))
                    throw new InvalidOperationException("双网规则无效；不会覆盖原文件。");
            }
        }
    }
}
