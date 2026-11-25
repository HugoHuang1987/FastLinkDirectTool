using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace FastLinkDirectTool
{
    internal sealed class ProxyBypassSnapshot
    {
        public int ProxyEnable;
        public string ProxyServer;
        public string AutoConfigHash;
        public bool HasOverride;
        public string Override;
    }

    internal sealed class ProxyBypassJournal
    {
        public int Version { get; set; }
        public bool InitialOverrideExists { get; set; }
        public List<string> OwnedTokens { get; set; }
        public string PendingBeforeHash { get; set; }
        public string PendingAfterHash { get; set; }
        public List<string> PendingOwnedTokens { get; set; }
    }

    // The injectable boundary keeps every unit test off the real registry/network.
    internal interface ISystemProxyBypassStorage
    {
        ProxyBypassSnapshot Read();
        void CompareAndWrite(ProxyBypassSnapshot expected, bool hasOverride, string value);
        void NotifyChanged();
        ProxyBypassJournal Load();
        void Save(ProxyBypassJournal journal);
        void Delete();
    }

    internal sealed class WindowsSystemProxyBypassStorage : ISystemProxyBypassStorage
    {
        private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        private readonly string path = Path.Combine(ConfigStore.DataDirectory, "system-proxy-bypass-state.json");
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int length);

        public ProxyBypassSnapshot Read()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath, false))
            {
                if (key == null) throw new InvalidOperationException("Windows 系统代理配置不可读取，未修改例外规则。");
                return ReadKey(key);
            }
        }

        private static ProxyBypassSnapshot ReadKey(RegistryKey key)
        {
            object value = key.GetValue("ProxyOverride", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value != null && (!(value is string) || key.GetValueKind("ProxyOverride") != RegistryValueKind.String))
                throw new InvalidOperationException("Windows 代理例外不是标准文本配置，拒绝覆盖。");
            return new ProxyBypassSnapshot
            {
                ProxyEnable = Convert.ToInt32(key.GetValue("ProxyEnable", 0)),
                ProxyServer = Convert.ToString(key.GetValue("ProxyServer", "", RegistryValueOptions.DoNotExpandEnvironmentNames)),
                AutoConfigHash = SystemProxyBypassManager.HashText(Convert.ToString(key.GetValue("AutoConfigURL", "", RegistryValueOptions.DoNotExpandEnvironmentNames))),
                HasOverride = value != null,
                Override = (string)value ?? ""
            };
        }

        public void CompareAndWrite(ProxyBypassSnapshot expected, bool hasOverride, string value)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath, true))
            {
                if (key == null) throw new InvalidOperationException("Windows 系统代理配置不可写入。");
                ProxyBypassSnapshot actual = ReadKey(key);
                if (!SystemProxyBypassManager.SameSnapshot(actual, expected))
                    throw new InvalidOperationException("代理设置已被其他程序更新；本次未覆盖，请稍后重试。");
                // Never write ProxyEnable, ProxyServer, AutoConfigURL, or any connection blob.
                if (hasOverride) key.SetValue("ProxyOverride", value ?? "", RegistryValueKind.String);
                else key.DeleteValue("ProxyOverride", false);
                ProxyBypassSnapshot after = ReadKey(key);
                if (after.HasOverride != hasOverride || !string.Equals(after.Override, value ?? "", StringComparison.Ordinal))
                    throw new InvalidOperationException("系统代理例外写入后核验失败，保留恢复记录。");
                if (after.ProxyEnable != expected.ProxyEnable || after.ProxyServer != expected.ProxyServer || after.AutoConfigHash != expected.AutoConfigHash)
                    throw new InvalidOperationException("代理连接配置在操作期间变化，保留恢复记录。");
            }
        }

        public void NotifyChanged()
        {
            bool changed = InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // SETTINGS_CHANGED
            bool refreshed = InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // REFRESH
            if (!changed || !refreshed)
                throw new InvalidOperationException("例外规则已写入，但 Windows 代理刷新通知失败；保留恢复记录。");
        }

        public ProxyBypassJournal Load()
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 262144) throw new InvalidOperationException("代理例外恢复记录异常，未执行清理。");
            // Unlike ConfigStore.LoadJson, corruption/read failures must not look like no ownership.
            ProxyBypassJournal journal = serializer.Deserialize<ProxyBypassJournal>(File.ReadAllText(path, Encoding.UTF8));
            if (journal == null) throw new InvalidOperationException("代理例外恢复记录不可读取，未执行清理。");
            return journal;
        }

        public void Save(ProxyBypassJournal journal)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(serializer.Serialize(journal));
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public void Delete() { if (File.Exists(path)) File.Delete(path); }
    }

    internal sealed class SystemProxyBypassManager
    {
        private readonly object sync = new object();
        private readonly ISystemProxyBypassStorage storage;
        private List<string> desired;
        private string activeProxyIdentity;
        public int OwnedTokenCount { get; private set; }

        public SystemProxyBypassManager() : this(new WindowsSystemProxyBypassStorage()) { }
        internal SystemProxyBypassManager(ISystemProxyBypassStorage storage) { this.storage = storage; }

        internal static string HashText(string value)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "");
        }

        private static string OverrideHash(bool exists, string value) { return HashText((exists ? "1|" : "0|") + (value ?? "")); }

        internal static bool SameSnapshot(ProxyBypassSnapshot a, ProxyBypassSnapshot b)
        {
            return a.ProxyEnable == b.ProxyEnable && a.ProxyServer == b.ProxyServer && a.AutoConfigHash == b.AutoConfigHash &&
                a.HasOverride == b.HasOverride && string.Equals(a.Override, b.Override, StringComparison.Ordinal);
        }

        internal static List<string> BuildTokens(RuleSet rules, bool privateNetworks)
        {
            List<string> tokens = new List<string>();
            foreach (string rule in rules.NormalizedRules)
            {
                if (rule.Contains("?")) throw new InvalidOperationException("新版系统代理模式暂不支持 ? 规则；请使用精确域名或 *，不会自动扩大直连范围。");
                if (rule.StartsWith(".", StringComparison.Ordinal))
                {
                    tokens.Add(rule.Substring(1));
                    tokens.Add("*" + rule);
                }
                else tokens.Add(rule);
            }
            if (privateNetworks)
            {
                tokens.Add("localhost"); tokens.Add("127.*"); tokens.Add("10.*"); tokens.Add("192.168.*");
                for (int i = 16; i <= 31; i++) tokens.Add("172." + i + ".*");
            }
            return tokens.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void ValidateTokens(List<string> tokens)
        {
            if (tokens == null || tokens.Count > 4096 || tokens.Any(delegate(string value)
                { return string.IsNullOrWhiteSpace(value) || value.Length > 512 || !Regex.IsMatch(value, @"^[a-z0-9._*\-]+$", RegexOptions.IgnoreCase); }))
                throw new InvalidOperationException("代理例外归属记录异常，拒绝扩大或清理未知规则。");
        }

        private ProxyBypassJournal LoadResolved(ProxyBypassSnapshot current)
        {
            ProxyBypassJournal journal = storage.Load();
            if (journal == null) return null;
            if (journal.Version != 1) throw new InvalidOperationException("代理例外恢复记录版本不支持。");
            ValidateTokens(journal.OwnedTokens);
            if (journal.PendingBeforeHash != null || journal.PendingAfterHash != null || journal.PendingOwnedTokens != null)
            {
                ValidateTokens(journal.PendingOwnedTokens);
                string hash = OverrideHash(current.HasOverride, current.Override);
                if (hash == journal.PendingAfterHash)
                {
                    // A crash/notification failure may have happened after the registry write.
                    storage.NotifyChanged();
                    journal.OwnedTokens = journal.PendingOwnedTokens;
                }
                else if (hash != journal.PendingBeforeHash)
                    throw new InvalidOperationException("未确认的代理例外写入后又发生外部修改；为保护其他设置，暂停自动清理。");
                journal.PendingBeforeHash = null; journal.PendingAfterHash = null; journal.PendingOwnedTokens = null;
                storage.Save(journal);
            }
            OwnedTokenCount = journal.OwnedTokens.Count;
            return journal;
        }

        private static string GetProxyIdentity(string proxyServer)
        {
            List<LoopbackProxyEndpoint> endpoints;
            if (!FastLinkConnectionCompatibility.TryParseProxyServer(proxyServer, out endpoints)) return null;
            // Keep the same scheme ordering and canonical target format as the detector.
            return string.Join(";", endpoints.OrderBy(p => p.Scheme, StringComparer.Ordinal)
                .Select(p => p.Scheme + "=" + p.CanonicalTarget).ToArray());
        }

        // Unit-test convenience only: production storage must always receive the
        // identity verified by the process/listener detector at the call site.
        internal void Apply(RuleSet rules, bool directPrivateNetworks)
        {
            if (storage is WindowsSystemProxyBypassStorage)
                throw new InvalidOperationException("生产代理例外应用必须提供已验证的 FastLink 代理身份。");
            Apply(rules, directPrivateNetworks, GetProxyIdentity(storage.Read().ProxyServer));
        }

        public void Apply(RuleSet rules, bool directPrivateNetworks, string expectedProxyIdentity)
        {
            if (string.IsNullOrWhiteSpace(expectedProxyIdentity))
                throw new InvalidOperationException("缺少已验证的 FastLink 代理身份，未修改例外规则。");
            List<string> requested = BuildTokens(rules, directPrivateNetworks); // Validate before any writes.
            lock (sync)
            {
                ProxyBypassSnapshot snapshot = storage.Read();
                if (snapshot.ProxyEnable != 1) throw new InvalidOperationException("系统代理已经关闭，未应用代理例外。");
                if (!string.Equals(GetProxyIdentity(snapshot.ProxyServer), expectedProxyIdentity, StringComparison.Ordinal))
                    throw new InvalidOperationException("系统代理端点已变化或不再属于本次验证的 FastLink，未修改例外规则。");
                ApplyTokens(snapshot, requested);
                desired = requested;
                activeProxyIdentity = expectedProxyIdentity;
            }
        }

        public bool EnsureCurrent()
        {
            lock (sync)
            {
                if (desired == null) return false;
                ProxyBypassSnapshot snapshot = storage.Read();
                if (snapshot.ProxyEnable != 1 || !string.Equals(GetProxyIdentity(snapshot.ProxyServer), activeProxyIdentity, StringComparison.Ordinal)) return false;
                return ApplyTokens(snapshot, desired);
            }
        }

        private bool ApplyTokens(ProxyBypassSnapshot snapshot, List<string> requested)
        {
            ProxyBypassJournal journal = LoadResolved(snapshot) ?? new ProxyBypassJournal
            { Version = 1, InitialOverrideExists = snapshot.HasOverride, OwnedTokens = new List<string>() };
            HashSet<string> requestedSet = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
            HashSet<string> oldOwned = new HashSet<string>(journal.OwnedTokens, StringComparer.OrdinalIgnoreCase);
            List<string> parts = (snapshot.Override ?? "").Split(';').Where(delegate(string value)
                { return !oldOwned.Contains(value.Trim()) || requestedSet.Contains(value.Trim()); }).ToList();
            if (parts.Count == 1 && parts[0].Length == 0) parts.Clear();
            HashSet<string> present = new HashSet<string>(parts.Select(delegate(string value) { return value.Trim(); }), StringComparer.OrdinalIgnoreCase);
            List<string> nextOwned = journal.OwnedTokens.Where(delegate(string value) { return requestedSet.Contains(value); }).ToList();
            foreach (string token in requested)
            {
                if (present.Add(token))
                {
                    parts.Add(token);
                    if (!nextOwned.Contains(token, StringComparer.OrdinalIgnoreCase)) nextOwned.Add(token);
                }
            }
            string replacement = string.Join(";", parts);
            bool hasOverride = snapshot.HasOverride || replacement.Length > 0;
            return Commit(snapshot, journal, hasOverride, replacement, nextOwned);
        }

        private bool Commit(ProxyBypassSnapshot snapshot, ProxyBypassJournal journal, bool hasOverride, string replacement, List<string> nextOwned)
        {
            bool changed = snapshot.HasOverride != hasOverride || snapshot.Override != replacement;
            bool ownershipChanged = !journal.OwnedTokens.SequenceEqual(nextOwned, StringComparer.OrdinalIgnoreCase);
            // The once-per-minute maintenance path must remain read-only when
            // the registry and ownership are unchanged. No journal write/flush,
            // Windows notification, or fresh state file is needed in this case.
            if (!changed && !ownershipChanged)
            {
                OwnedTokenCount = nextOwned.Count;
                return false;
            }
            if (changed)
            {
                journal.PendingBeforeHash = OverrideHash(snapshot.HasOverride, snapshot.Override);
                journal.PendingAfterHash = OverrideHash(hasOverride, replacement);
                journal.PendingOwnedTokens = nextOwned;
                storage.Save(journal); // Write-ahead ownership, before the registry changes.
                storage.CompareAndWrite(snapshot, hasOverride, replacement);
                storage.NotifyChanged();
            }
            journal.OwnedTokens = nextOwned;
            journal.PendingBeforeHash = null; journal.PendingAfterHash = null; journal.PendingOwnedTokens = null;
            storage.Save(journal);
            OwnedTokenCount = nextOwned.Count;
            return changed;
        }

        public void Remove()
        {
            lock (sync)
            {
                desired = null; activeProxyIdentity = null;
                ProxyBypassSnapshot snapshot = storage.Read();
                ProxyBypassJournal journal = LoadResolved(snapshot);
                if (journal == null) { OwnedTokenCount = 0; return; }
                HashSet<string> owned = new HashSet<string>(journal.OwnedTokens, StringComparer.OrdinalIgnoreCase);
                string replacement = string.Join(";", (snapshot.Override ?? "").Split(';').Where(delegate(string value) { return !owned.Contains(value.Trim()); }));
                // If somebody already removed our entries/property, cleanup must not recreate it.
                bool hasOverride = replacement == snapshot.Override ? snapshot.HasOverride :
                    (replacement.Length > 0 || journal.InitialOverrideExists);
                Commit(snapshot, journal, hasOverride, replacement, new List<string>());
                storage.Delete();
            }
        }
    }
}
