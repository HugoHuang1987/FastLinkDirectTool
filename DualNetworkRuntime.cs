using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace FastLinkDirectTool.Modes
{
    public enum DualResourceKind { Metric4, Route4, Nrpt }

    public sealed class DualResource
    {
        public DualResourceKind Kind { get; set; }
        public string InterfaceId { get; set; }
        public string Destination { get; set; }
        public string NextHop { get; set; }
        public string Namespace { get; set; }
        public string Owner { get; set; }
    }

    public interface IDualNetworkPlatform
    {
        bool IsAdministrator { get; }
        string ScopeIdentity { get; }
        NetworkSnapshot Capture();
        // null means absent; read errors MUST throw. Values are canonical:
        // metric: automatic=0|1;metric=N, route: metric=N,
        // NRPT: server=127.0.0.1;owner=<exact session owner>.
        string Read(DualResource resource);
        void Write(DualResource resource, string value);
        void Remove(DualResource resource);
    }

    public sealed class DualJournalEntry
    {
        public DualResource Resource { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
        public bool Applied { get; set; }
        public bool Restored { get; set; }
    }

    public sealed class DualJournalDocument
    {
        public int Version { get; set; }
        public string Scope { get; set; }
        public string Owner { get; set; }
        public long Generation { get; set; }
        public string Phase { get; set; }
        public List<DualJournalEntry> Entries { get; set; }
        public DualJournalDocument() { Version = 1; Entries = new List<DualJournalEntry>(); }
    }

    public interface IDualJournal
    {
        DualJournalDocument Load();
        void Save(DualJournalDocument document);
        void Delete();
    }

    public interface IDualDnsService
    {
        bool IsRunning { get; }
        void Start(ModeRequest request, NetworkSnapshot snapshot, long generation,
            Func<long, RuleTarget, string[], bool> routeCallback);
        // Nonblocking: invalidate new/late queries. Never wait for a callback
        // while holding a DNS lock; the runtime owns the route-draining lock.
        void Pause();
        void Stop();
    }

    public sealed class DualOperationResult
    {
        public bool Success { get; set; }
        public bool Pending { get; set; }
        public string State { get; set; }
        public string Message { get; set; }
    }

    // All effects are injected. Constructing this class performs no IO. A UI
    // must ask for explicit approval before Arm/Start/Recover; no auto-restart.
    public sealed class DualNetworkRuntime
    {
        private const string OwnerPrefix = "FastLinkDirectTool.DualNetwork:";
        private readonly object sync = new object();
        private readonly IDualNetworkPlatform platform;
        private readonly IDualJournal journal;
        private readonly IDualDnsService dns;
        private readonly Func<DateTime> utcNow;
        private DualJournalDocument document;
        private ModeRequest request;
        private NetworkSnapshot activeSnapshot;
        private long generation;
        private volatile bool active;
        private volatile bool armed;
        private volatile bool pending;
        private volatile string state = "Idle";
        private readonly Dictionary<uint, RuleTarget> confirmedAddressTargets = new Dictionary<uint, RuleTarget>();
        private readonly Dictionary<uint, DateTime> addressVerifiedAt = new Dictionary<uint, DateTime>();
        private readonly Dictionary<uint, DualResource> addressRoutes = new Dictionary<uint, DualResource>();
        private readonly Dictionary<string, DualResource> confirmedRoutes = new Dictionary<string, DualResource>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> routeVerifiedAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly HashSet<string> borrowedResources = new HashSet<string>(StringComparer.Ordinal);
        private int validationCalls, routeAuditIndex;
        private const int MaximumResources = 4096;

        public bool IsActive { get { return active; } }
        public bool IsArmed { get { return armed; } }
        public bool HasPending
        {
            get
            {
                // UI status must not wait behind a slow Windows route command.
                // Busy state is conservatively treated as pending for gating.
                if (!Monitor.TryEnter(sync)) return true;
                try
                {
                    // Startup gating is read-only: never Capture, restore, or
                    // modify a journal merely because the UI reads this property.
                    if (!pending && document == null)
                    {
                        try { if (journal.Load() != null) { pending = true; state = "RecoveryRequired"; } }
                        catch { pending = true; state = "RecoveryRequired"; }
                    }
                    return pending;
                }
                finally { Monitor.Exit(sync); }
            }
        }
        public long Generation { get { return Interlocked.Read(ref generation); } }
        public string State { get { return state; } }

        public DualNetworkRuntime(IDualNetworkPlatform platform, IDualJournal journal, IDualDnsService dns)
            : this(platform, journal, dns, delegate { return DateTime.UtcNow; }) { }

        public DualNetworkRuntime(IDualNetworkPlatform platform, IDualJournal journal, IDualDnsService dns, Func<DateTime> utcNow)
        {
            if (platform == null || journal == null || dns == null || utcNow == null) throw new ArgumentNullException("dependencies");
            this.platform = platform; this.journal = journal; this.dns = dns;
            this.utcNow = utcNow;
        }

        public DualOperationResult Prepare(ModeRequest requested)
        {
            lock (sync)
            {
                try
                {
                    ValidateRequest(requested);
                    if (pending) return Result(false, "仍有待恢复状态，请先显式恢复或重试停止。");
                    if (document == null && journal.Load() != null)
                    { pending = true; state = "RecoveryRequired"; return Result(false, "发现上次会话日志，未执行任何网络变更；请显式恢复。"); }
                    NetworkSnapshot snapshot = platform.Capture();
                    ValidateArmSnapshot(requested, snapshot);
                    if (!active && !armed) state = CorporatePresent(requested, snapshot) ? "Prepared" : "WaitingForCorporate";
                    return Result(true, "预检查完成；没有修改网络。公司接口可稍后连接。");
                }
                catch (Exception error) { return Result(false, error.Message); }
            }
        }

        public DualOperationResult Arm(ModeRequest requested, bool manualAllowed)
        {
            lock (sync)
            {
                if (!manualAllowed) return Result(false, "未获得本次手动启用许可，未修改网络。");
                if (pending) return Result(false, "请先完成上次清理或恢复。");
                if (active) return SameRequest(requested) ? Result(true, "相同会话已经运行。") : Result(false, "须先停止当前模式才能更换请求。");
                // A rejected request must not roll back a previously prepared
                // external path. All read-only gates run outside rollback.
                try { Preflight(requested, false); }
                catch (Exception error) { return Result(false, error.Message); }
                try
                {
                    ArmCore(requested);
                    return Result(true, state == "WaitingForCorporate" ? "外网优先级保护已准备，等待公司接口。" : "两口 IPv4 优先级已准备；尚未启用 DNS 分流。");
                }
                catch (Exception error) { return FailAndRollback(error); }
            }
        }

        public DualOperationResult Start(ModeRequest requested, bool manualAllowed)
        {
            lock (sync)
            {
                if (!manualAllowed) return Result(false, "未获得本次手动启用许可，未修改网络。");
                if (pending) return Result(false, "请先完成上次清理或恢复。");
                if (active) return SameRequest(requested) ? Result(true, "相同会话已经运行。") : Result(false, "须先停止当前模式才能更换请求。");
                try { Preflight(requested, true); }
                catch (Exception error) { return Result(false, error.Message); }
                try
                {
                    ArmCore(requested);
                    NetworkSnapshot snapshot = platform.Capture();
                    ValidateStartSnapshot(request, snapshot);
                    activeSnapshot = CloneSnapshot(snapshot);
                    document.Generation = ++generation;
                    SavePhase("Starting");
                    EnsureDnsRoutes(snapshot);
                    EnsureStaticRoutes(snapshot);
                    // Callback acceptance begins before DNS Start, but Start must
                    // not synchronously wait for an engine callback.
                    active = true;
                    dns.Start(CloneRequest(request), CloneSnapshot(snapshot), generation, RouteAnswers);
                    if (!dns.IsRunning) throw new InvalidOperationException("DNS 服务未确认监听成功。");
                    foreach (string ns in OwnedNamespaces(request))
                        WriteOwned(new DualResource { Kind = DualResourceKind.Nrpt, Namespace = ns, Owner = document.Owner },
                            "server=127.0.0.1;owner=" + document.Owner, false);
                    VerifySnapshotUnchanged(snapshot, platform.Capture());
                    SavePhase("Running");
                    armed = true; state = "Running";
                    return Result(true, "双网分流事务已完成并读回核验。");
                }
                catch (Exception error) { return FailAndRollback(error); }
            }
        }

        public DualOperationResult Stop() { return Stop(false); }

        public DualOperationResult Stop(bool allowDefaultRouteChange)
        {
            lock (sync)
            {
                try
                {
                    if (document == null)
                    {
                        DualJournalDocument loaded = journal.Load();
                        if (loaded == null) { pending = false; state = "Idle"; return Result(true, "本执行器没有待清理的会话。"); }
                        ModeRequest recoveredRequest = RequestFromJournal(loaded);
                        ValidateJournal(loaded, recoveredRequest);
                        document = loaded; request = recoveredRequest;
                        generation = Math.Max(generation, document.Generation);
                    }
                    DualOperationResult risk = CheckRestoreRisk(allowDefaultRouteChange);
                    if (risk != null) return risk;
                    CleanupCore(false, allowDefaultRouteChange); return Result(true, "已核验清理本会话资源，未覆盖外部修改。");
                }
                catch (Exception error) { MarkPending(); return Result(false, "清理未完成，保留恢复日志：" + error.Message); }
            }
        }

        public DualOperationResult Recover(ModeRequest requested, bool manualAllowed)
        { return Recover(requested, manualAllowed, false); }

        public DualOperationResult Recover(ModeRequest requested, bool manualAllowed, bool allowDefaultRouteChange)
        {
            lock (sync)
            {
                if (!manualAllowed) return Result(false, "恢复需要本次手动许可。");
                if (active) return Result(false, "当前会话仍运行，请使用停止功能。");
                try
                {
                    ValidateRequest(requested); RequireAdministrator();
                    DualJournalDocument loaded = document ?? journal.Load();
                    if (loaded == null) { pending = false; state = "Idle"; return Result(true, "没有待恢复日志。"); }
                    ValidateJournal(loaded, requested);
                    document = loaded; request = CloneRequest(requested);
                    generation = Math.Max(generation, document.Generation);
                    DualOperationResult risk = CheckRestoreRisk(allowDefaultRouteChange);
                    if (risk != null) return risk;
                    CleanupCore(false, allowDefaultRouteChange);
                    return Result(true, "已恢复本用户、本机、相同模式和接口范围内的资源。");
                }
                catch (Exception error) { MarkPending(); return Result(false, "恢复未完成：" + error.Message); }
            }
        }

        // Reconciliation never uses Stop/Recover: restoring a historic baseline
        // would overwrite the user's now-working network. A live session cannot
        // be abandoned, even if an external change removed some of its resources.
        public DualReconciliationReport ReviewStaleSession()
        {
            lock (sync)
            {
                try { DualJournalDocument loaded; return ReviewStaleSessionCore(out loaded); }
                catch (Exception error)
                {
                    return new DualReconciliationReport { CanArchive = false, HasJournal = true,
                        ReviewedUtc = utcNow(), Message = "旧事务核对未完成，保留原记录：" + error.Message };
                }
            }
        }

        public DualOperationResult ArchiveStaleSession(bool manualAllowed)
        {
            lock (sync)
            {
                if (!manualAllowed) return Result(false, "归档旧事务需要本次明确许可，未改动记录或网络。");
                if (active || armed || dns.IsRunning) return Result(false, "当前执行器仍在运行、预备或监听 DNS，不能归档其事务。");
                if (!platform.IsAdministrator) return Result(false, "归档需要管理员权限，未尝试提升或修改系统。");
                bool committed = false;
                try
                {
                    RequireAdministrator();
                    DualJournalDocument firstDocument, secondDocument;
                    DualReconciliationReport first = ReviewStaleSessionCore(out firstDocument);
                    if (!first.HasJournal) return Result(true, "没有旧事务需要归档，未修改网络。");
                    if (!first.CanArchive) return Result(false, first.Message);
                    // A UI preview is never reused as evidence. Both full reads
                    // happen inside this explicit operation, under the runtime lock.
                    DualReconciliationReport second = ReviewStaleSessionCore(out secondDocument);
                    if (!second.CanArchive || first.JournalFingerprint != second.JournalFingerprint ||
                        DualJournalFingerprint.Serializer().Serialize(first.Entries) != DualJournalFingerprint.Serializer().Serialize(second.Entries))
                        throw new InvalidOperationException("两次核对之间资源或事务发生变化，保留原记录。");
                    IDualJournalArchive archive = journal as IDualJournalArchive;
                    if (archive == null) throw new InvalidOperationException("恢复记录存储不支持安全归档。");
                    string archivePath = archive.ArchiveVerified(secondDocument, second);
                    committed = true;
                    if (journal.Load() != null) throw new InvalidOperationException("归档后发现另一份待处理事务。");
                    ++generation;
                    document = null; request = null; activeSnapshot = null;
                    confirmedAddressTargets.Clear(); addressVerifiedAt.Clear(); addressRoutes.Clear();
                    confirmedRoutes.Clear(); routeVerifiedAt.Clear(); borrowedResources.Clear();
                    validationCalls = 0; routeAuditIndex = 0;
                    pending = false; armed = false; active = false; state = "Idle";
                    return Result(true, "旧事务及核对证据已归档，保留当前网络设置；尚未启用新模式。归档位置：" + archivePath);
                }
                catch (Exception error)
                {
                    // Do not save/mark entries Restored: this operation did not
                    // restore them. In particular, do not run rollback here.
                    pending = true; state = "RecoveryRequired";
                    return Result(false, (committed ? "旧事务已归档，但后续核验未完成，保持暂停：" : "未完成归档，原事务保留：") + error.Message);
                }
            }
        }

        private DualReconciliationReport ReviewStaleSessionCore(out DualJournalDocument loaded)
        {
            loaded = null;
            if (active || armed || dns.IsRunning)
                throw new InvalidOperationException("当前执行器仍在运行、预备或监听 DNS，不能归档其事务。");
            loaded = journal.Load();
            if (loaded == null)
            {
                if (document != null) throw new InvalidOperationException("内存中仍有事务但磁盘记录缺失，不能视为完成。");
                return new DualReconciliationReport { HasJournal = false, CanArchive = false,
                    ReviewedUtc = utcNow(), Message = "没有旧事务需要归档。" };
            }
            ModeRequest original = RequestFromJournal(loaded);
            ValidateJournal(loaded, original);
            string fingerprint = DualJournalFingerprint.Compute(loaded);
            if (document != null && DualJournalFingerprint.Compute(document) != fingerprint)
                throw new InvalidOperationException("磁盘记录与当前执行器的事务不一致。");
            NetworkSnapshot snapshot = platform.Capture();
            // No proxy/IPv6/admission gate here: this is purely observation and
            // file retirement, not permission to change any network setting.
            if (snapshot == null || snapshot.Adapters == null) throw new InvalidOperationException("无法核验接口身份。");
            foreach (string id in new[] { original.CorporateAdapterId, original.InternetAdapterId })
            {
                List<AdapterSnapshot> matches = snapshot.Adapters.Where(a => a != null && string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1 || !matches[0].IsPhysical)
                    throw new InvalidOperationException("旧事务接口缺失、重复或身份不明确，保留记录。");
            }
            var report = new DualReconciliationReport { HasJournal = true, CanArchive = true,
                Owner = loaded.Owner, Scope = loaded.Scope, Generation = loaded.Generation,
                ReviewedUtc = utcNow(), JournalFingerprint = fingerprint };
            // Inspect even entries marked Restored; a stale flag is not evidence
            // that its old network resource is absent now.
            foreach (DualJournalEntry entry in loaded.Entries)
            {
                string current = platform.Read(entry.Resource);
                var item = new DualReconciliationEntry { Resource = entry.Resource, ObservedValue = current };
                if (entry.Resource.Kind == DualResourceKind.Metric4)
                {
                    if (!ValidReconciliationMetric(entry.Before) || !ValidReconciliationMetric(current))
                        throw new InvalidOperationException("接口优先级缺失或格式异常，不能归档。");
                    if (ResourceValuesEqual(entry.Resource, current, entry.After))
                    { item.Disposition = "OwnedSettingStillPresent"; report.CanArchive = false; }
                    else item.Disposition = ResourceValuesEqual(entry.Resource, current, entry.Before) ? "AlreadyAtOriginalPolicy" : "ExternallyChangedPreserved";
                }
                else
                {
                    // Any route or DNS policy still present is a blocker, even
                    // if its metric/owner differs. Never relinquish it by guessing.
                    item.Disposition = current == null ? "Absent" : "ResourceStillPresent";
                    if (current != null) report.CanArchive = false;
                }
                report.Entries.Add(item);
            }
            DualJournalDocument reread = journal.Load();
            if (reread == null || DualJournalFingerprint.Compute(reread) != fingerprint)
                throw new InvalidOperationException("核对期间事务记录改变，保留记录。");
            if (!(journal is IDualJournalArchive))
            { report.CanArchive = false; report.Message = "存储不支持保留证据的安全归档。"; }
            else report.Message = report.CanArchive
                ? "核对时旧路由及 DNS 策略均已不存在，旧优先级已还原或被外部改变；可显式归档，保留当前网络。"
                : "仍有旧优先级、路由或 DNS 策略存在；禁止归档，未修改网络。";
            return report;
        }

        private static bool ValidReconciliationMetric(string value)
        {
            if (value == null || (!value.StartsWith("automatic=0;metric=", StringComparison.Ordinal) && !value.StartsWith("automatic=1;metric=", StringComparison.Ordinal))) return false;
            int metric;
            return int.TryParse(value.Substring("automatic=0;metric=".Length), NumberStyles.None, CultureInfo.InvariantCulture, out metric) && metric >= 1 && metric <= 9999;
        }

        private void Preflight(ModeRequest requested, bool enabling)
        {
            RequireAdministrator(); ValidateRequest(requested);
            if (document != null && !SameRequest(requested))
                throw new InvalidOperationException("已有会话请求不同，未改变现有外网保护；请先手动停止。");
            if (document == null && journal.Load() != null)
            { pending = true; state = "RecoveryRequired"; throw new InvalidOperationException("发现待恢复记录，未修改网络；请显式恢复。"); }
            NetworkSnapshot snapshot = platform.Capture();
            ValidateArmSnapshot(requested, snapshot);
            if (enabling || CorporatePresent(requested, snapshot))
            {
                ModePlan plan = NetworkModePlanner.BuildPreparation(requested, snapshot);
                if (!plan.CanSimulate) throw new InvalidOperationException(string.Join(" ", plan.Blockers.ToArray()));
            }
            if (enabling && !snapshot.CorporateAdmissionVerified)
                throw new InvalidOperationException("公司接口准入尚未确认，未改变现有外网保护。");
        }

        // A full stop restores the user's original automatic/manual metrics.
        // That can make the company cable win the default route again. This
        // one-operation approval is deliberately not stored in configuration.
        private DualOperationResult CheckRestoreRisk(bool allowed)
        {
            if (allowed || !document.Entries.Any(e => !e.Restored && e.Resource.Kind == DualResourceKind.Metric4)) return null;
            try
            {
                NetworkSnapshot snapshot = platform.Capture();
                AdapterSnapshot corporate = Find(snapshot, request.CorporateAdapterId), internet = Find(snapshot, request.InternetAdapterId);
                bool otherExit = snapshot.Adapters.Any(a => a != null && a.IsUp &&
                    !string.Equals(a.Id, request.InternetAdapterId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(a.Id, request.CorporateAdapterId, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(a.Gateway));
                if (corporate != null && internet != null && !corporate.IsUp && internet.IsUp && !otherExit) return null;
            }
            catch { /* Unknown topology is not consent to change the egress. */ }
            return new DualOperationResult { Success = false, Pending = pending, State = "EgressConfirmationRequired",
                Message = "恢复原网络优先级可能让公司接口接管外网；本次未改动会话。请先断开公司接口，或明确确认本次恢复出口。" };
        }

        private void ArmCore(ModeRequest requested)
        {
            RequireAdministrator(); ValidateRequest(requested);
            if (document != null && !SameRequest(requested)) throw new InvalidOperationException("已有会话请求不同，必须先清理。");
            NetworkSnapshot snapshot = platform.Capture();
            ValidateArmSnapshot(requested, snapshot);
            bool companyConnected = CorporatePresent(requested, snapshot);
            if (companyConnected)
            {
                // Authentication agents need their company paths BEFORE access
                // has been granted. This validates routing, never NAC compliance.
                ModePlan preparation = NetworkModePlanner.BuildPreparation(requested, snapshot);
                if (!preparation.CanSimulate) throw new InvalidOperationException(string.Join(" ", preparation.Blockers.ToArray()));
            }
            if (document == null)
            {
                if (journal.Load() != null) { pending = true; state = "RecoveryRequired"; throw new InvalidOperationException("发现上次会话日志，禁止覆盖，须先恢复。"); }
                request = CloneRequest(requested);
                document = new DualJournalDocument
                {
                    Scope = Scope(requested), Owner = OwnerPrefix + Guid.NewGuid().ToString("D"),
                    Generation = ++generation, Phase = "Preparing"
                };
                journal.Save(document);
            }
            AdapterSnapshot internet = Find(snapshot, request.InternetAdapterId);
            WriteOwned(Metric(internet.Id), "automatic=0;metric=5", false);
            AdapterSnapshot corporate = Find(snapshot, request.CorporateAdapterId);
            if (corporate != null) WriteOwned(Metric(corporate.Id), "automatic=0;metric=55", false);
            if (companyConnected)
            {
                SavePhase("PreparingCorporatePaths");
                EnsureDnsRoutes(snapshot);
                EnsureStaticRoutes(snapshot);
            }
            armed = true;
            state = CorporatePresent(request, snapshot) ? "Armed" : "WaitingForCorporate";
            SavePhase(state);
        }

        private bool RouteAnswers(long expectedGeneration, RuleTarget target, string[] addresses)
        {
            lock (sync)
            {
                if (!active || pending || document == null || expectedGeneration != generation ||
                    (target != RuleTarget.Corporate && target != RuleTarget.Internet)) return false;
                if (addresses == null || addresses.Length == 0) return target == RuleTarget.Internet;
                try
                {
                    AdapterSnapshot corporate = Find(activeSnapshot, request.CorporateAdapterId), internet = Find(activeSnapshot, request.InternetAdapterId);
                    List<string> safeAddresses = new List<string>();
                    List<string> expiredAddresses = new List<string>();
                    Dictionary<uint, DualResource> covered = new Dictionary<uint, DualResource>();
                    DateTime now = utcNow();
                    foreach (string raw in addresses)
                    {
                        uint value; string canonical;
                        if (!PlanningValidation.TrySafeIpv4(raw, out value, out canonical) ||
                            PlanningValidation.AddressConflict(activeSnapshot, corporate, internet, target, value) != null) return false;
                        RuleTarget known;
                        if (confirmedAddressTargets.TryGetValue(value, out known))
                        {
                            if (known != target) return false; // Shared IP cannot silently change egress.
                            DateTime verified;
                            if ((!addressVerifiedAt.TryGetValue(value, out verified) || now < verified || (now - verified).TotalSeconds >= 60) &&
                                !expiredAddresses.Contains(canonical)) expiredAddresses.Add(canonical);
                        }
                        else
                        {
                            DualResource covering = FindConfirmedCover(target, value);
                            if (covering != null) covered[value] = covering;
                            else if (!safeAddresses.Contains(canonical)) safeAddresses.Add(canonical);
                        }
                    }
                    // Ownership/target evidence never expires, only the readback
                    // cache does. A deleted borrowed route must not be recreated.
                    foreach (string address in expiredAddresses)
                    {
                        uint value; string canonical; PlanningValidation.TryIpv4(address, out value, out canonical);
                        DualResource route;
                        if (!addressRoutes.TryGetValue(value, out route)) throw new InvalidOperationException("已确认地址缺少路由关联。");
                        VerifyConfirmedRoute(route); addressVerifiedAt[value] = now;
                    }
                    if (confirmedAddressTargets.Count + covered.Count + safeAddresses.Count > MaximumResources)
                        throw new InvalidOperationException("已达到 DNS 地址缓存上限，停止并清理。");
                    foreach (KeyValuePair<uint, DualResource> item in covered)
                    {
                        DateTime verified;
                        if (!routeVerifiedAt.TryGetValue(ResourceKey(item.Value), out verified) || now < verified || (now - verified).TotalSeconds >= 60)
                            VerifyConfirmedRoute(item.Value);
                        confirmedAddressTargets[item.Key] = target; addressVerifiedAt[item.Key] = now; addressRoutes[item.Key] = item.Value;
                    }
                    if (safeAddresses.Count == 0) return true; // Within 60 seconds: ZERO platform calls.
                    if (document.Entries.Count + borrowedResources.Count + safeAddresses.Count > MaximumResources)
                        throw new InvalidOperationException("已达到 4096 个会话资源上限，停止并清理；路由保留至停用，不无限累积。");
                    NetworkSnapshot snapshot = platform.Capture(); // New destinations: one topology capture.
                    VerifySnapshotUnchanged(activeSnapshot, snapshot);
                    AdapterSnapshot adapter = Find(snapshot, target == RuleTarget.Corporate ? request.CorporateAdapterId : request.InternetAdapterId);
                    foreach (string address in safeAddresses) WriteOwned(Route(adapter, address + "/32"), "metric=1", true);
                    return true;
                }
                catch (Exception error) { FailAndRollback(error); return false; }
            }
        }

        private void EnsureDnsRoutes(NetworkSnapshot snapshot)
        {
            foreach (string id in new[] { request.InternetAdapterId, request.CorporateAdapterId })
            {
                AdapterSnapshot adapter = Find(snapshot, id);
                foreach (string raw in adapter.DnsServers)
                {
                    uint value; string canonical;
                    if (!PlanningValidation.TrySafeIpv4(raw, out value, out canonical)) throw new InvalidOperationException("DNS 地址无效。");
                    WriteOwned(Route(adapter, canonical + "/32"), "metric=1", true);
                }
            }
        }

        private void EnsureStaticRoutes(NetworkSnapshot snapshot)
        {
            foreach (ModeRule rule in request.Rules)
            {
                ParsedModeRule parsed; string error;
                if (!PlanningValidation.TryRule(rule, out parsed, out error)) throw new InvalidOperationException(error);
                System.Net.IPAddress ip = parsed.Pattern.GetDirectIpAddress();
                if (ip != null) WriteOwned(Route(Find(snapshot, rule.Target == RuleTarget.Corporate ? request.CorporateAdapterId : request.InternetAdapterId), ip + "/32"), "metric=1", true);
            }
            CorporateCidrPlan corporatePlan = CorporateCidrPlanner.Build(request, snapshot);
            if (!corporatePlan.IsValid) throw new InvalidOperationException(string.Join(" ", corporatePlan.Errors.ToArray()));
            foreach (string raw in corporatePlan.Routes)
            {
                uint first, last; string cidr;
                if (!PlanningValidation.TryCidr(raw, out first, out last, out cidr)) throw new InvalidOperationException("公司网段无效。");
                WriteOwned(Route(Find(snapshot, request.CorporateAdapterId), cidr), "metric=1", true);
            }
        }

        private void WriteOwned(DualResource resource, string after, bool shareExisting)
        {
            uint routeAddress = 0;
            bool exactRoute = resource.Kind == DualResourceKind.Route4 && resource.Destination != null && resource.Destination.EndsWith("/32", StringComparison.Ordinal);
            RuleTarget routeTarget = string.Equals(resource.InterfaceId, request.CorporateAdapterId, StringComparison.OrdinalIgnoreCase) ? RuleTarget.Corporate : RuleTarget.Internet;
            if (exactRoute)
            {
                string canonical;
                if (!PlanningValidation.TrySafeIpv4(resource.Destination.Substring(0, resource.Destination.Length - 3), out routeAddress, out canonical))
                    throw new InvalidOperationException("精确路由地址无效。");
                RuleTarget existingTarget;
                if (confirmedAddressTargets.TryGetValue(routeAddress, out existingTarget) && existingTarget != routeTarget)
                    throw new InvalidOperationException("相同 IP 被分配到相反出口，拒绝覆盖。");
            }
            string before = platform.Read(resource);
            DualJournalEntry existing = document.Entries.FirstOrDefault(delegate(DualJournalEntry entry)
                { return !entry.Restored && ResourceKey(entry.Resource) == ResourceKey(resource); });
            if (existing != null)
            {
                if (!ResourceValuesEqual(resource, before, existing.After) || !ResourceValuesEqual(resource, after, existing.After)) throw new InvalidOperationException("已管理资源被外部修改，拒绝覆盖。");
                RecordConfirmedRoute(resource);
                return;
            }
            if (shareExisting && before != null)
            {
                if (before != after) throw new InvalidOperationException("已存在路由不符合预期元组或跃点，拒绝借用或覆盖。");
                if (!borrowedResources.Contains(ResourceKey(resource)) && document.Entries.Count + borrowedResources.Count >= MaximumResources)
                    throw new InvalidOperationException("达到会话资源上限。");
                borrowedResources.Add(ResourceKey(resource));
                RecordConfirmedRoute(resource);
                return; // Do not claim or delete shared routes.
            }
            if (resource.Kind == DualResourceKind.Nrpt && before != null) throw new InvalidOperationException("该 DNS 命名空间已有规则，不自动接管。");
            if (ResourceValuesEqual(resource, before, after)) return;
            if (resource.Kind == DualResourceKind.Metric4 && before == null) throw new InvalidOperationException("接口跃点不可读取，拒绝猜测原值。");
            if (document.Entries.Count + borrowedResources.Count >= MaximumResources) throw new InvalidOperationException("达到会话资源上限。");
            DualJournalEntry write = new DualJournalEntry { Resource = resource, Before = before, After = after };
            document.Entries.Add(write);
            journal.Save(document); // Persist before ANY network mutation.
            if (!ResourceValuesEqual(resource, platform.Read(resource), before)) throw new InvalidOperationException("资源在写入前发生外部变化。");
            platform.Write(resource, after);
            // Applied means the platform positively confirmed THIS write, not
            // merely that the same value appeared. A concurrent external route
            // with the desired value must never become ours after a failed write.
            write.Applied = true;
            journal.Save(document);
            if (!ResourceValuesEqual(resource, platform.Read(resource), after)) throw new InvalidOperationException("资源写入后读回不一致。");
            RecordConfirmedRoute(resource);
        }

        private void RecordConfirmedRoute(DualResource resource)
        {
            if (resource.Kind != DualResourceKind.Route4) return;
            confirmedRoutes[ResourceKey(resource)] = resource;
            routeVerifiedAt[ResourceKey(resource)] = utcNow();
            if (!resource.Destination.EndsWith("/32", StringComparison.Ordinal)) return;
            uint address; string canonical;
            if (!PlanningValidation.TrySafeIpv4(resource.Destination.Substring(0, resource.Destination.Length - 3), out address, out canonical))
                throw new InvalidOperationException("已核验路由地址无效。");
            confirmedAddressTargets[address] = string.Equals(resource.InterfaceId, request.CorporateAdapterId, StringComparison.OrdinalIgnoreCase) ? RuleTarget.Corporate : RuleTarget.Internet;
            addressVerifiedAt[address] = utcNow();
            addressRoutes[address] = resource;
        }

        private DualResource FindConfirmedCover(RuleTarget target, uint address)
        {
            string adapterId = target == RuleTarget.Corporate ? request.CorporateAdapterId : request.InternetAdapterId;
            DualResource best = null; ulong bestSize = ulong.MaxValue;
            foreach (DualResource route in confirmedRoutes.Values)
            {
                if (!string.Equals(route.InterfaceId, adapterId, StringComparison.OrdinalIgnoreCase)) continue;
                uint first, last; string canonical;
                if (!PlanningValidation.TryCidr(route.Destination, out first, out last, out canonical) || address < first || address > last) continue;
                ulong size = (ulong)last - first;
                if (size < bestSize) { best = route; bestSize = size; }
            }
            return best;
        }

        private void VerifyConfirmedRoute(DualResource resource)
        {
            if (!confirmedRoutes.ContainsKey(ResourceKey(resource)) || platform.Read(resource) != "metric=1")
                throw new InvalidOperationException("已确认的路由已被删除或修改；停止本会话，不重建或覆盖外部变更。");
            RecordConfirmedRoute(resource);
        }

        private void CleanupCore(bool retainInternetPriority = false, bool allowDefaultRouteChange = false)
        {
            RequireAdministrator();
            active = false; ++generation;
            // This lock has drained any in-flight route callback. Pause cannot
            // wait for DNS IO/callbacks; late callbacks see the new generation.
            dns.Pause();
            document.Generation = generation;
            SavePhase("CleanupPending");
            RestoreKind(DualResourceKind.Nrpt); // Listener stays until verified.
            dns.Stop();
            if (dns.IsRunning) throw new InvalidOperationException("DNS 服务尚未确认停止。");
            RestoreKind(DualResourceKind.Route4);
            if (retainInternetPriority)
            {
                // Only already-owned, readback-confirmed settings remain. No
                // new route/metric is created while handling an error.
                VerifyRetainedInternetPriority();
                SavePhase("InternetProtected");
                confirmedAddressTargets.Clear(); addressVerifiedAt.Clear(); addressRoutes.Clear();
                confirmedRoutes.Clear(); routeVerifiedAt.Clear(); borrowedResources.Clear();
                pending = true; armed = true; state = "InternetProtected";
                return;
            }
            RestoreKind(DualResourceKind.Metric4, allowDefaultRouteChange);
            SavePhase("Restored");
            journal.Delete();
            document = null; request = null; activeSnapshot = null;
            confirmedAddressTargets.Clear(); addressVerifiedAt.Clear(); addressRoutes.Clear(); confirmedRoutes.Clear(); routeVerifiedAt.Clear(); borrowedResources.Clear();
            validationCalls = 0; routeAuditIndex = 0;
            pending = false; armed = false; state = "Idle";
        }

        private void RestoreKind(DualResourceKind kind, bool allowDefaultRouteChange = false)
        {
            foreach (DualJournalEntry entry in document.Entries.Where(delegate(DualJournalEntry e) { return !e.Restored && e.Resource.Kind == kind; }).Reverse().ToList())
            {
                string current = platform.Read(entry.Resource);
                if (!ResourceValuesEqual(entry.Resource, current, entry.Before))
                {
                    if (!ResourceValuesEqual(entry.Resource, current, entry.After)) throw new InvalidOperationException("检测到外部修改，保留该资源和日志，未覆盖。");
                    if (!entry.Applied && entry.Resource.Kind != DualResourceKind.Nrpt)
                        throw new InvalidOperationException("写入未经本会话确认，当前同值资源归属不确定；保留日志，不删除或覆盖。");
                    if (kind == DualResourceKind.Metric4)
                    {
                        // Route cleanup may take time. A cable reconnected after
                        // the entry gate is not permission to switch the egress.
                        DualOperationResult risk = CheckRestoreRisk(allowDefaultRouteChange);
                        if (risk != null) throw new InvalidOperationException(risk.Message);
                    }
                    // The write-ahead entry retains both states through retries.
                    if (entry.Before == null) platform.Remove(entry.Resource);
                    else platform.Write(entry.Resource, entry.Before);
                    if (!ResourceValuesEqual(entry.Resource, platform.Read(entry.Resource), entry.Before)) throw new InvalidOperationException("恢复后的资源未通过读回核验。");
                }
                entry.Restored = true;
                journal.Save(document);
            }
        }

        private DualOperationResult FailAndRollback(Exception error)
        {
            if (document == null) return Result(false, error.Message);
            try
            {
                bool keepPriority = document.Entries.Any(e => !e.Restored && e.Applied && e.Resource.Kind == DualResourceKind.Metric4);
                // Unknown or partial metric state stays pending. Never turn a
                // failed feature into an automatic switch of the default egress.
                if (keepPriority) VerifyRetainedInternetPriority();
                CleanupCore(keepPriority);
                return Result(false, error.Message + (keepPriority ?
                    " 分流已停止并清理；仅保留已核验的外网优先设置，等待手动处理，不自动重试。" : " 已核验回滚本次变更。"));
            }
            catch (Exception cleanup) { MarkPending(); return Result(false, error.Message + " 清理待重试：" + cleanup.Message); }
        }

        private void VerifyRetainedInternetPriority()
        {
            foreach (DualJournalEntry entry in document.Entries.Where(e => !e.Restored && e.Resource.Kind == DualResourceKind.Metric4))
                if (!entry.Applied || !ResourceValuesEqual(entry.Resource, platform.Read(entry.Resource), entry.After))
                    throw new InvalidOperationException("外网优先设置归属或读回不明确，保留记录，未恢复或覆盖任何接口优先级。");
            if (platform.Read(Metric(request.InternetAdapterId)) != "automatic=0;metric=5" ||
                platform.Read(Metric(request.CorporateAdapterId)) != "automatic=0;metric=55")
                throw new InvalidOperationException("外网优先设置尚未完整建立，保留记录供手动处理，不宣称外网已保护。");
            NetworkSnapshot snapshot = platform.Capture();
            AdapterSnapshot corporate = Find(snapshot, request.CorporateAdapterId), internet = Find(snapshot, request.InternetAdapterId);
            if (corporate == null || internet == null || !corporate.IsUp || !internet.IsUp ||
                !corporate.IsPhysical || !internet.IsPhysical || !snapshot.ExternalDefaultPreferred ||
                snapshot.SystemProxyActive || snapshot.TunnelActive || snapshot.OtherDnsPolicyPresent ||
                NetworkModePlanner.Ipv6CompatibilityBlocker(request, snapshot) != null)
                throw new InvalidOperationException("无法确认外网仍为默认出口，保留记录供手动处理。");
        }

        private void MarkPending()
        {
            active = false; pending = true; state = "CleanupPending"; ++generation;
            try { dns.Pause(); } catch { }
        }

        // Call from a low-frequency UI worker, not per DNS request. A mismatch
        // stops this transaction; it never switches Wi-Fi or restarts a VPN.
        public DualOperationResult ValidateCurrent()
        {
            long expectedGeneration;
            ModeRequest expectedRequest;
            NetworkSnapshot expectedSnapshot;
            lock (sync)
            {
                if (!active) return Result(!pending, "当前没有运行中的双网会话。");
                expectedGeneration = generation; expectedRequest = request; expectedSnapshot = activeSnapshot;
            }
            // Capture may take seconds. It must not hold the route-write lock:
            // already verified DNS answers require no platform calls and should
            // continue while this passive topology snapshot is being collected.
            NetworkSnapshot captured = null;
            Exception captureError = null;
            try { captured = platform.Capture(); }
            catch (Exception error) { captureError = error; }
            lock (sync)
            {
                // Stop or Start may have completed during Capture. A late result
                // (including a failure) belongs only to its original generation.
                if (!active || generation != expectedGeneration || !object.ReferenceEquals(request, expectedRequest) ||
                    !object.ReferenceEquals(activeSnapshot, expectedSnapshot))
                    return Result(!pending, "会话已变化，忽略上一代网络检查结果。");
                try
                {
                    if (captureError != null) throw captureError;
                    VerifySnapshotUnchanged(expectedSnapshot, captured);
                    // One route per six UI checks, not a claim of a full live
                    // route-table audit. DNS answers additionally expire at 60s.
                    if (++validationCalls >= 6)
                    {
                        validationCalls = 0;
                        if (confirmedRoutes.Count > 0)
                        {
                            int index = routeAuditIndex % confirmedRoutes.Count;
                            VerifyConfirmedRoute(confirmedRoutes.Values.ElementAt(index));
                            routeAuditIndex = index + 1;
                        }
                    }
                    return Result(true, "网络身份未变；路由使用有限缓存并低频轮转核验。");
                }
                catch (Exception error) { return FailAndRollback(error); }
            }
        }

        public static bool ResourceValuesEqual(DualResource resource, string left, string right)
        {
            if (string.Equals(left, right, StringComparison.Ordinal)) return true;
            if (resource == null || resource.Kind != DualResourceKind.Metric4) return false;
            int first, second;
            return TryAutomaticMetric(left, out first) && TryAutomaticMetric(right, out second);
        }
        private static bool TryAutomaticMetric(string value, out int metric)
        {
            metric = 0;
            const string prefix = "automatic=1;metric=";
            return value != null && value.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(value.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out metric) && metric >= 1 && metric <= 9999;
        }

        private void SavePhase(string phase) { document.Phase = phase; journal.Save(document); }
        private void RequireAdministrator() { if (!platform.IsAdministrator) throw new InvalidOperationException("需要管理员权限；未尝试提升权限或修改系统。"); }
        private DualOperationResult Result(bool success, string message)
        { return new DualOperationResult { Success = success, Pending = pending, State = state, Message = message }; }
        private string Scope(ModeRequest value)
        { return platform.ScopeIdentity + "|" + value.Mode + "|" + value.CorporateAdapterId.ToLowerInvariant() + "|" + value.InternetAdapterId.ToLowerInvariant(); }
        private bool SameRequest(ModeRequest value)
        {
            if (request == null || value == null || string.IsNullOrWhiteSpace(value.CorporateAdapterId) || string.IsNullOrWhiteSpace(value.InternetAdapterId)) return false;
            return Scope(request) == Scope(value) && string.Join(";", RequestRuleKeys(request)) == string.Join(";", RequestRuleKeys(value)) &&
                string.Join(";", request.CorporateCidrs.OrderBy(delegate(string s) { return s; }, StringComparer.OrdinalIgnoreCase)) ==
                string.Join(";", (value.CorporateCidrs ?? new List<string>()).OrderBy(delegate(string s) { return s; }, StringComparer.OrdinalIgnoreCase));
        }
        private static string[] RequestRuleKeys(ModeRequest value)
        { return (value.Rules ?? new List<ModeRule>()).Select(delegate(ModeRule rule) { return rule == null ? "<invalid>" : rule.Pattern + "|" + rule.Target; }).OrderBy(delegate(string s) { return s; }, StringComparer.OrdinalIgnoreCase).ToArray(); }
        private static string ResourceKey(DualResource resource)
        { return resource.Kind + "|" + (resource.InterfaceId ?? "").ToLowerInvariant() + "|" + resource.Destination + "|" + resource.NextHop + "|" + (resource.Namespace ?? "").ToLowerInvariant(); }
        private DualResource Metric(string id) { return new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = id, Owner = document.Owner }; }
        private DualResource Route(AdapterSnapshot adapter, string destination)
        {
            uint first, last, local; string normalized, ignored;
            if (!PlanningValidation.TryCidr(destination, out first, out last, out normalized) || !PlanningValidation.TryIpv4(adapter.Address, out local, out ignored))
                throw new InvalidOperationException("路由地址无效。");
            uint mask = PlanningValidation.Mask(adapter.PrefixLength);
            bool onLink = (first & mask) == (local & mask) && (last & mask) == (local & mask);
            return new DualResource { Kind = DualResourceKind.Route4, InterfaceId = adapter.Id, Destination = normalized, NextHop = onLink ? "0.0.0.0" : adapter.Gateway, Owner = document.Owner };
        }
        private static AdapterSnapshot Find(NetworkSnapshot snapshot, string id)
        { return snapshot.Adapters.FirstOrDefault(delegate(AdapterSnapshot a) { return a != null && string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase); }); }
        private static bool CorporatePresent(ModeRequest requested, NetworkSnapshot snapshot)
        { AdapterSnapshot value = Find(snapshot, requested.CorporateAdapterId); return value != null && value.IsUp; }

        private void ValidateRequest(ModeRequest requested)
        {
            Guid company, internet;
            if (requested == null || (requested.Mode != WorkMode.WiredCorporateWifiInternet && requested.Mode != WorkMode.WifiCorporateWiredInternet) ||
                !Guid.TryParse(requested.CorporateAdapterId, out company) || !Guid.TryParse(requested.InternetAdapterId, out internet) || company == internet)
                throw new InvalidOperationException("必须明确选择双网模式和两个不同的稳定接口 GUID。");
            if (string.IsNullOrWhiteSpace(platform.ScopeIdentity)) throw new InvalidOperationException("缺少当前机器和用户的所有权范围。");
            foreach (ModeRule rule in requested.Rules ?? new List<ModeRule>())
            {
                ParsedModeRule parsed; string error;
                if (!PlanningValidation.TryRule(rule, out parsed, out error) || parsed.Target == RuleTarget.Review)
                    throw new InvalidOperationException("规则尚未完成有效分类，禁止应用。");
            }
        }

        private static void ValidateArmSnapshot(ModeRequest requested, NetworkSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Adapters == null) throw new InvalidOperationException("无法读取网络快照。");
            if (snapshot.SystemProxyActive || snapshot.TunnelActive || snapshot.CleanupPending || snapshot.OtherDnsPolicyPresent)
                throw new InvalidOperationException("存在代理、隧道、待清理状态或非自有 DNS 策略，未修改网络。");
            string ipv6Blocker = NetworkModePlanner.Ipv6CompatibilityBlocker(requested, snapshot);
            if (ipv6Blocker != null) throw new InvalidOperationException(ipv6Blocker);
            if (!snapshot.DualConnectionAllowed || !snapshot.InternetReachabilityVerified || snapshot.StableSeconds < 8)
                throw new InvalidOperationException("双网许可、外网可用性或至少 8 秒稳定性尚未确认。");
            if (snapshot.Adapters.Count(delegate(AdapterSnapshot a) { return a != null && string.Equals(a.Id, requested.InternetAdapterId, StringComparison.OrdinalIgnoreCase); }) != 1 ||
                snapshot.Adapters.Count(delegate(AdapterSnapshot a) { return a != null && string.Equals(a.Id, requested.CorporateAdapterId, StringComparison.OrdinalIgnoreCase); }) > 1)
                throw new InvalidOperationException("接口 ID 缺失或重复。");
            AdapterSnapshot internet = Find(snapshot, requested.InternetAdapterId), corporate = Find(snapshot, requested.CorporateAdapterId);
            LinkKind expectedInternet = requested.Mode == WorkMode.WiredCorporateWifiInternet ? LinkKind.Wifi : LinkKind.Ethernet;
            if (internet == null || !internet.IsUp || !internet.IsPhysical || internet.Kind != expectedInternet)
                throw new InvalidOperationException("指定外网物理接口未连接或类型不匹配。");
            if (corporate != null && (!corporate.IsPhysical || corporate.Kind == expectedInternet || corporate.Kind == LinkKind.Other))
                throw new InvalidOperationException("指定公司接口类型不匹配。");
            uint ip, gateway; string text;
            if (internet.PrefixLength < 1 || internet.PrefixLength > 31 || !PlanningValidation.TrySafeIpv4(internet.Address, out ip, out text) ||
                !PlanningValidation.TrySafeIpv4(internet.Gateway, out gateway, out text) || ip == gateway ||
                (ip & PlanningValidation.Mask(internet.PrefixLength)) != (gateway & PlanningValidation.Mask(internet.PrefixLength)))
                throw new InvalidOperationException("外网接口的 IPv4 地址、前缀或网关无效。");
        }

        private static void ValidateStartSnapshot(ModeRequest requested, NetworkSnapshot snapshot)
        {
            ValidateArmSnapshot(requested, snapshot);
            ModePlan plan = NetworkModePlanner.Build(requested, snapshot);
            if (!plan.CanSimulate) throw new InvalidOperationException("安全门禁未通过：" + (plan.Blockers.FirstOrDefault() ?? plan.Status));
        }

        private void VerifySnapshotUnchanged(NetworkSnapshot expected, NetworkSnapshot actual)
        {
            ValidateStartSnapshot(request, actual);
            foreach (string id in new[] { request.CorporateAdapterId, request.InternetAdapterId })
                if (AdapterKey(Find(expected, id)) != AdapterKey(Find(actual, id)))
                    throw new InvalidOperationException("接口地址、DNS 或网关在事务期间变化；旧请求不再适用。");
        }
        private static string AdapterKey(AdapterSnapshot adapter)
        {
            if (adapter == null) return "<missing>";
            return adapter.Id.ToLowerInvariant() + "|" + adapter.Kind + "|" + adapter.IsUp + "|" + adapter.IsPhysical + "|" +
                adapter.Address + "/" + adapter.PrefixLength + "|" + adapter.Gateway + "|" +
                string.Join(",", (adapter.DnsServers ?? new List<string>()).OrderBy(delegate(string s) { return s; }, StringComparer.OrdinalIgnoreCase)) + "|" + adapter.HasIpv6Default + "|" + adapter.Ipv6RouteSignature;
        }

        private void ValidateJournal(DualJournalDocument loaded, ModeRequest requested)
        {
            Guid owner;
            if (loaded.Version != 1 || loaded.Scope != Scope(requested) || loaded.Owner == null || !loaded.Owner.StartsWith(OwnerPrefix, StringComparison.Ordinal) ||
                !Guid.TryParse(loaded.Owner.Substring(OwnerPrefix.Length), out owner) || loaded.Entries == null || loaded.Entries.Count > 4096)
                throw new InvalidOperationException("恢复日志与当前机器、用户、模式或接口范围不一致。");
            HashSet<string> keys = new HashSet<string>();
            foreach (DualJournalEntry entry in loaded.Entries)
            {
                if (entry == null || entry.Resource == null || entry.Resource.Owner != loaded.Owner || !Enum.IsDefined(typeof(DualResourceKind), entry.Resource.Kind) ||
                    entry.Before == entry.After || !keys.Add(ResourceKey(entry.Resource))) throw new InvalidOperationException("恢复日志资源记录无效。");
                if (entry.Resource.Kind != DualResourceKind.Nrpt &&
                    !string.Equals(entry.Resource.InterfaceId, requested.CorporateAdapterId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(entry.Resource.InterfaceId, requested.InternetAdapterId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("恢复日志包含所选接口以外的资源。");
                if (entry.Resource.Kind == DualResourceKind.Nrpt && entry.After != "server=127.0.0.1;owner=" + loaded.Owner)
                    throw new InvalidOperationException("恢复日志的 DNS 所有权不匹配。");
                if (entry.Resource.Kind == DualResourceKind.Route4)
                {
                    uint first, last, nextHop; string canonical, hop;
                    if (entry.Before != null || entry.After != "metric=1" ||
                        !PlanningValidation.TryCidr(entry.Resource.Destination, out first, out last, out canonical) ||
                        entry.Resource.Destination != canonical ||
                        !PlanningValidation.TryIpv4(entry.Resource.NextHop, out nextHop, out hop) ||
                        (nextHop != 0 && !PlanningValidation.SafeRange(nextHop, nextHop)))
                        throw new InvalidOperationException("恢复日志含无效路由或默认路由，拒绝操作。");
                }
                if (entry.Resource.Kind == DualResourceKind.Metric4 &&
                    (entry.Before == null || (entry.After != "automatic=0;metric=5" && entry.After != "automatic=0;metric=55")))
                    throw new InvalidOperationException("恢复日志的跃点记录无效。");
                if (entry.Resource.Kind == DualResourceKind.Nrpt && entry.Before != null)
                    throw new InvalidOperationException("恢复日志试图接管原有 DNS 规则。");
            }
        }

        private ModeRequest RequestFromJournal(DualJournalDocument loaded)
        {
            string prefix = platform.ScopeIdentity + "|";
            if (string.IsNullOrWhiteSpace(platform.ScopeIdentity) || loaded == null || loaded.Scope == null || !loaded.Scope.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("日志不属于当前机器和用户，拒绝自动恢复。");
            string[] parts = loaded.Scope.Substring(prefix.Length).Split('|');
            WorkMode mode;
            if (parts.Length != 3 || !Enum.TryParse<WorkMode>(parts[0], out mode)) throw new InvalidOperationException("恢复范围格式无效。");
            ModeRequest value = new ModeRequest { Mode = mode, CorporateAdapterId = parts[1], InternetAdapterId = parts[2] };
            ValidateRequest(value);
            return value;
        }

        private static IEnumerable<string> OwnedNamespaces(ModeRequest requested)
        {
            List<string> result = new List<string>();
            foreach (ModeRule rule in requested.Rules)
            {
                RulePattern pattern; string error;
                if (!RulePattern.TryCreate(rule.Pattern, out pattern, out error)) throw new InvalidOperationException(error);
                result.AddRange(pattern.GetNrptNamespaces());
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase);
        }
        private static ModeRequest CloneRequest(ModeRequest source)
        {
            return new ModeRequest { Mode = source.Mode, CorporateAdapterId = source.CorporateAdapterId, InternetAdapterId = source.InternetAdapterId,
                Rules = (source.Rules ?? new List<ModeRule>()).Select(delegate(ModeRule r) { return new ModeRule { Pattern = r.Pattern, Target = r.Target }; }).ToList(),
                CorporateCidrs = new List<string>(source.CorporateCidrs ?? new List<string>()) };
        }
        private static NetworkSnapshot CloneSnapshot(NetworkSnapshot source)
        {
            return new NetworkSnapshot { SystemProxyActive = source.SystemProxyActive, TunnelActive = source.TunnelActive, CleanupPending = source.CleanupPending,
                OtherDnsPolicyPresent = source.OtherDnsPolicyPresent, CorporateAdmissionVerified = source.CorporateAdmissionVerified, DualConnectionAllowed = source.DualConnectionAllowed,
                InternetReachabilityVerified = source.InternetReachabilityVerified, ExternalDefaultPreferred = source.ExternalDefaultPreferred, Ipv6SafetyVerified = source.Ipv6SafetyVerified,
                StableSeconds = source.StableSeconds, Adapters = source.Adapters.Where(delegate(AdapterSnapshot a) { return a != null; }).Select(delegate(AdapterSnapshot a)
                { return new AdapterSnapshot { Id = a.Id, Name = a.Name, Kind = a.Kind, IsUp = a.IsUp, IsPhysical = a.IsPhysical, Address = a.Address, PrefixLength = a.PrefixLength,
                    Gateway = a.Gateway, DnsServers = new List<string>(a.DnsServers ?? new List<string>()), HasIpv6Default = a.HasIpv6Default, Ipv6RouteSignature = a.Ipv6RouteSignature }; }).ToList() };
        }
    }
}
