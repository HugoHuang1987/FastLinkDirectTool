using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;
using FastLinkDirectTool.Modes;

namespace FastLinkDirectTool.Tests
{
    public static class DualNetworkRuntimeTests
    {
        private const string Corp = "11111111-1111-1111-1111-111111111111";
        private const string Internet = "22222222-2222-2222-2222-222222222222";
        private static int passed;
        private static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        private static string Key(DualResource r) { return r.Kind + "|" + r.InterfaceId + "|" + r.Destination + "|" + r.NextHop + "|" + r.Namespace; }

        private sealed class FakeJournal : IDualJournal, IDualJournalArchive
        {
            internal DualJournalDocument Stored;
            internal int Saves, Loads, Deletes, FailSaveAt;
            internal bool FailLoad, FailDelete;
            internal int Archives;
            internal bool FailArchive;
            internal DualJournalDocument Archived;
            internal DualReconciliationReport Evidence;
            internal Action BeforeArchive;
            internal Action<DualJournalDocument> BeforeSave;
            private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
            private DualJournalDocument Clone(DualJournalDocument value)
            { return value == null ? null : serializer.Deserialize<DualJournalDocument>(serializer.Serialize(value)); }
            public DualJournalDocument Load() { Loads++; if (FailLoad) throw new Exception("fixture load"); return Clone(Stored); }
            public void Save(DualJournalDocument value) { Saves++; if (BeforeSave != null) BeforeSave(value); if (Saves == FailSaveAt) throw new Exception("fixture save"); Stored = Clone(value); }
            public void Delete() { Deletes++; if (FailDelete) throw new Exception("fixture delete"); Stored = null; }
            public string ArchiveVerified(DualJournalDocument expected, DualReconciliationReport evidence)
            {
                if (BeforeArchive != null) BeforeArchive();
                if (FailArchive) throw new IOException("fixture archive");
                Assert(Stored != null && DualJournalFingerprint.Compute(Stored) == DualJournalFingerprint.Compute(expected), "archive CAS mismatch");
                Archives++; Archived = Clone(Stored); Evidence = evidence; Stored = null; return "fixture-archive";
            }
        }

        private sealed class FakePlatform : IDualNetworkPlatform
        {
            internal readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            internal readonly List<string> Calls = new List<string>();
            internal FakeJournal Journal;
            internal NetworkSnapshot Snapshot;
            internal bool Administrator = true, FailRead, ChangeDuringNextWrite, RecomputeAutomatic;
            internal DualResourceKind? ConcurrentSameValueKind;
            internal string Identity = "fixture-machine-and-user";
            internal int Captures, Reads, Writes, Removes;
            internal DualResourceKind? FailWriteKind, CorruptWriteKind, FailRemoveKind;
            internal Action BeforeWrite;
            internal Action BeforeRead;
            internal Func<NetworkSnapshot> CaptureOverride;
            public bool IsAdministrator { get { return Administrator; } }
            public string ScopeIdentity { get { return Identity; } }
            public NetworkSnapshot Capture() { Interlocked.Increment(ref Captures); return CaptureOverride == null ? Snapshot : CaptureOverride(); }
            public string Read(DualResource r)
            { Reads++; if (BeforeRead != null) BeforeRead(); if (FailRead) throw new Exception("fixture read"); string value; return Values.TryGetValue(Key(r), out value) ? value : null; }
            public void Write(DualResource r, string value)
            {
                Assert(Journal.Stored != null && Journal.Stored.Entries.Any(delegate(DualJournalEntry e)
                    { return Key(e.Resource) == Key(r) && (e.After == value || e.Before == value); }), "Network write without saved journal.");
                Calls.Add("write:" + r.Kind); Writes++;
                if (BeforeWrite != null) BeforeWrite();
                if (ConcurrentSameValueKind == r.Kind) { ConcurrentSameValueKind = null; Values[Key(r)] = value; throw new Exception("fixture external same-value race"); }
                if (FailWriteKind == r.Kind) { FailWriteKind = null; throw new Exception("fixture write"); }
                if (ChangeDuringNextWrite) { ChangeDuringNextWrite = false; Values[Key(r)] = "external-edit"; throw new Exception("fixture concurrent edit"); }
                if (CorruptWriteKind == r.Kind) { CorruptWriteKind = null; Values[Key(r)] = "external-readback"; return; }
                Values[Key(r)] = RecomputeAutomatic && value.StartsWith("automatic=1;", StringComparison.Ordinal) ? "automatic=1;metric=77" : value;
            }
            public void Remove(DualResource r)
            {
                Assert(Journal.Stored != null, "Network deletion without journal.");
                Calls.Add("remove:" + r.Kind); Removes++;
                if (FailRemoveKind == r.Kind) { FailRemoveKind = null; throw new Exception("fixture remove"); }
                Values.Remove(Key(r));
            }
        }

        private sealed class FakeDns : IDualDnsService
        {
            internal List<string> Calls;
            internal bool Running, FailStart, FailStop, FailResume, FailPause;
            internal int Starts, Pauses, Stops, Resumes;
            internal long Generation;
            internal Func<long, RuleTarget, string[], bool> Callback;
            internal Action BeforeResume;
            internal Action AfterStartPaused;
            private readonly DualDnsAdmission admission = new DualDnsAdmission();
            public bool IsRunning { get { return Running; } }
            public long StartPaused(ModeRequest request, NetworkSnapshot snapshot, long generation, Func<long, RuleTarget, string[], bool> callback)
            {
                Starts++; Generation = generation; Callback = callback; if (FailStart) throw new Exception("fixture DNS start");
                long token = admission.StartPaused(DualDnsSession.Create(request, snapshot, generation, callback));
                Running = true; Calls.Add("dns:start"); if (AfterStartPaused != null) AfterStartPaused(); return token;
            }
            public bool Resume(long generation, long activationToken)
            {
                if (BeforeResume != null) BeforeResume();
                if (FailResume || !Running || !admission.Resume(generation, activationToken)) return false;
                Resumes++; Calls.Add("dns:resume"); return true;
            }
            internal bool ResolveRoute(string address)
            {
                DualDnsAdmission.State captured = admission.Capture();
                if (!Running || !admission.IsCurrent(captured)) return false;
                return captured.Session.RouteCallback(captured.Session.Generation, RuleTarget.Corporate, new[] { address }) && admission.IsCurrent(captured);
            }
            public void Pause() { admission.Pause(); Pauses++; Calls.Add("dns:pause"); if (FailPause) throw new TimeoutException("fixture DNS send drain timeout"); }
            public void Stop() { admission.Pause(); Stops++; Calls.Add("dns:stop"); if (FailStop) throw new Exception("fixture DNS stop"); Running = false; }
        }

        private sealed class Fixture
        {
            internal FakeJournal Journal = new FakeJournal();
            internal FakePlatform Platform;
            internal FakeDns Dns;
            internal DualNetworkRuntime Runtime;
            internal ModeRequest Request;
            internal DateTime Now = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);
            internal Fixture()
            {
                Request = new ModeRequest { Mode = WorkMode.WiredCorporateWifiInternet, CorporateAdapterId = Corp, InternetAdapterId = Internet,
                    Rules = new List<ModeRule> { new ModeRule { Pattern = "corp.example.test", Target = RuleTarget.Corporate }, new ModeRule { Pattern = "public.example.test", Target = RuleTarget.Internet } } };
                Platform = new FakePlatform { Journal = Journal, Snapshot = new NetworkSnapshot
                {
                    CorporateAdmissionVerified = true, DualConnectionAllowed = true, InternetReachabilityVerified = true,
                    ExternalDefaultPreferred = true, StableSeconds = 12,
                    Adapters = new List<AdapterSnapshot>
                    {
                        new AdapterSnapshot { Id = Corp, Name = "fixture-corp", Kind = LinkKind.Ethernet, IsUp = true, IsPhysical = true,
                            Address = "192.0.2.10", PrefixLength = 24, Gateway = "192.0.2.1", DnsServers = new List<string> { "192.0.2.53" } },
                        new AdapterSnapshot { Id = Internet, Name = "fixture-internet", Kind = LinkKind.Wifi, IsUp = true, IsPhysical = true,
                            Address = "198.51.100.10", PrefixLength = 24, Gateway = "198.51.100.1", DnsServers = new List<string> { "203.0.113.53" } }
                    }
                } };
                Platform.Values[Key(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = Corp })] = "automatic=1;metric=25";
                Platform.Values[Key(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = Internet })] = "automatic=1;metric=40";
                Dns = new FakeDns { Calls = Platform.Calls };
                Runtime = new DualNetworkRuntime(Platform, Journal, Dns, delegate { return Now; });
            }
            internal void Start() { DualOperationResult result = Runtime.Start(Request, true); Assert(result.Success, result.Message); }
            internal void AssertClean()
            {
                Assert(!Runtime.IsActive && !Runtime.HasPending && !Dns.Running && Journal.Stored == null, "Session not clean.");
                Assert(Platform.Values.Count == 2, "Owned resources remain.");
            }
            internal void AssertProtected()
            {
                Assert(!Runtime.IsActive && Runtime.IsArmed && Runtime.HasPending && !Dns.Running, "Protected state flags incorrect.");
                Assert(Runtime.State == "InternetProtected" && Journal.Stored != null && Journal.Stored.Phase == "InternetProtected", "Protection journal missing.");
                Assert(Journal.Stored.Entries.Where(e => !e.Restored).All(e => e.Resource.Kind == DualResourceKind.Metric4), "Business resources not cleaned.");
                Assert(Platform.Values[Key(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = Internet })] == "automatic=0;metric=5", "Internet priority lost.");
                Assert(Platform.Values[Key(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = Corp })] == "automatic=0;metric=55", "Company priority restored prematurely.");
            }
        }

        private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
        private static Fixture PreparedReadFixture()
        {
            Fixture f = new Fixture(); f.Request.CorporateCidrs.Add("10.0.0.0/8");
            Assert(f.Runtime.Arm(f.Request, true).Success, "fixture arm failed");
            f.Platform.Writes = f.Platform.Removes = f.Platform.Reads = 0;
            f.Journal.Saves = f.Journal.Deletes = 0;
            return f;
        }
        private static void AssertPreparedPreserved(Fixture f, string fingerprint)
        {
            AssertNoReconciliationEffects(f);
            Assert(DualJournalFingerprint.Compute(f.Journal.Stored) == fingerprint, "Prepared journal or authentication routes changed.");
        }
        private static Fixture StaleFixture(bool wasRunning)
        {
            Fixture f = new Fixture();
            if (wasRunning) f.Start(); else Assert(f.Runtime.Arm(f.Request, true).Success, "fixture arm failed");
            foreach (string key in f.Platform.Values.Keys.Where(k => !k.StartsWith("Metric4|", StringComparison.Ordinal)).ToList()) f.Platform.Values.Remove(key);
            f.Platform.Values[Key(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = Corp })] = "automatic=1;metric=25";
            f.Platform.Values[Key(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = Internet })] = "automatic=1;metric=40";
            f.Dns = new FakeDns { Calls = f.Platform.Calls };
            f.Runtime = new DualNetworkRuntime(f.Platform, f.Journal, f.Dns, delegate { return f.Now; });
            f.Platform.Writes = f.Platform.Removes = f.Platform.Reads = 0; f.Journal.Saves = f.Journal.Deletes = 0;
            f.Platform.Calls.Clear(); return f;
        }
        private static void AssertNoReconciliationEffects(Fixture f)
        {
            Assert(f.Platform.Writes == 0 && f.Platform.Removes == 0 && f.Journal.Saves == 0 && f.Journal.Deletes == 0 &&
                f.Dns.Starts == 0 && f.Dns.Stops == 0 && f.Dns.Pauses == 0, "Reconciliation changed network or deleted original journal.");
        }
        private static void WithJournalDirectory(Action<string> action)
        {
            string root = Environment.GetEnvironmentVariable("DUAL_JOURNAL_TEST_ROOT");
            if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            string leaf = "dual-archive-test-" + Guid.NewGuid().ToString("N");
            string directory = Path.Combine(root, leaf); Directory.CreateDirectory(directory);
            try { action(directory); }
            catch { Console.Error.WriteLine("Archive fixture files: " + string.Join(";", Directory.GetFiles(directory, "*", SearchOption.AllDirectories))); throw; }
            finally
            {
                Assert(Path.GetDirectoryName(Path.GetFullPath(directory)).TrimEnd(Path.DirectorySeparatorChar) == root && Path.GetFileName(directory) == leaf, "Unsafe test cleanup path.");
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
        private static Fixture ProtectedModeThree()
        {
            Fixture f = new Fixture();
            f.Request.Mode = WorkMode.WifiCorporateWiredInternet;
            f.Platform.Snapshot.Adapters[0].Kind = LinkKind.Wifi;
            f.Platform.Snapshot.Adapters[1].Kind = LinkKind.Ethernet;
            f.Start();
            // Trigger an ordinary readback failure, then restore the fixture's
            // healthy ordinary topology before the next manual Start.
            f.Platform.Snapshot.Adapters[0].Address = "192.0.2.11";
            Assert(!f.Runtime.ValidateCurrent().Success, "failed topology fixture did not stop");
            f.AssertProtected();
            f.Platform.Snapshot.Adapters[0].Address = "192.0.2.10";
            return f;
        }

        public static int Main()
        {
            try
            {
                Test("constructor is effect-free and pending gate is read-only", delegate
                {
                    Fixture f = new Fixture(); Assert(f.Platform.Captures == 0 && f.Journal.Loads == 0 && f.Journal.Saves == 0, "Constructor performed IO.");
                    Assert(!f.Runtime.HasPending && f.Platform.Captures == 0 && f.Journal.Saves == 0 && f.Dns.Pauses == 0, "Gate performed effects.");
                });
                Test("prepare does not mutate network or ownership", delegate
                { Fixture f = new Fixture(); Assert(f.Runtime.Prepare(f.Request).Success, "Prepare failed."); Assert(f.Platform.Writes == 0 && f.Journal.Saves == 0 && f.Dns.Starts == 0, "Prepare mutated."); });
                Test("manual permission and administrator are mandatory", delegate
                {
                    Fixture f = new Fixture(); Assert(!f.Runtime.Start(f.Request, false).Success, "Missing permission accepted.");
                    f.Platform.Administrator = false; Assert(!f.Runtime.Start(f.Request, true).Success, "Nonadministrator accepted.");
                    Assert(f.Platform.Writes == 0 && f.Journal.Saves == 0 && f.Dns.Starts == 0, "Rejected start changed state.");
                });
                Test("arm protects internet and can wait with corporate cable absent", delegate
                {
                    Fixture f = new Fixture(); f.Platform.Snapshot.Adapters.RemoveAt(0);
                    Assert(f.Runtime.Arm(f.Request, true).Success && f.Runtime.IsArmed && f.Runtime.State == "WaitingForCorporate", "Arm waiting failed.");
                    Assert(f.Platform.Writes == 1 && f.Dns.Starts == 0, "Waiting arm changed unavailable corporate/DNS.");
                    Assert(f.Runtime.Stop(true).Success, "Arm rollback failed."); f.AssertClean();
                });
                Test("prepared validation reads actual role metrics without changing journal or routes", delegate
                {
                    Fixture f = PreparedReadFixture(); string original = DualJournalFingerprint.Compute(f.Journal.Stored);
                    DualOperationResult result = f.Runtime.ValidateCurrent();
                    Assert(result.Success && f.Runtime.IsArmed && !f.Runtime.HasPending && f.Platform.Reads == 2, "Prepared metrics were not checked.");
                    Assert(result.VerifiedPreparationSnapshot != null && !object.ReferenceEquals(result.VerifiedPreparationSnapshot, f.Platform.Snapshot) &&
                        !object.ReferenceEquals(result.VerifiedPreparationSnapshot.Adapters[0], f.Platform.Snapshot.Adapters[0]), "Verified display snapshot was absent or shared mutable state.");
                    AssertPreparedPreserved(f, original);
                });
                Test("prepared unplugged company adapter remains valid with the same GUID and metric", delegate
                {
                    Fixture f = PreparedReadFixture(); string original = DualJournalFingerprint.Compute(f.Journal.Stored);
                    f.Platform.Snapshot.Adapters[0].IsUp = false; f.Platform.Snapshot.StableSeconds = 0;
                    f.Platform.Snapshot.CorporateAdmissionVerified = f.Platform.Snapshot.InternetReachabilityVerified = false;
                    Assert(f.Runtime.ValidateCurrent().Success && f.Runtime.IsArmed, "Unplugged same-GUID preparation was revoked.");
                    AssertPreparedPreserved(f, original);
                });
                Test("prepared originally absent company adapter can still wait without inventing its metric", delegate
                {
                    Fixture f = new Fixture(); f.Platform.Snapshot.Adapters.RemoveAt(0);
                    Assert(f.Runtime.Arm(f.Request, true).Success, "Waiting arm failed.");
                    f.Platform.Writes = f.Platform.Removes = f.Platform.Reads = f.Journal.Saves = f.Journal.Deletes = 0;
                    string original = DualJournalFingerprint.Compute(f.Journal.Stored);
                    Assert(f.Runtime.ValidateCurrent().Success && f.Runtime.IsArmed && f.Platform.Reads == 1, "Absent corporate adapter was guessed or invalidated.");
                    AssertPreparedPreserved(f, original);
                });
                foreach (string changed in new[] { Corp, Internet })
                    Test("prepared metric drift pauses without cleanup or priority restoration " + changed, delegate
                    {
                        Fixture f = PreparedReadFixture(); string original = DualJournalFingerprint.Compute(f.Journal.Stored);
                        string metric = Key(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = changed });
                        f.Platform.Values[metric] = "automatic=1;metric=25";
                        DualOperationResult result = f.Runtime.ValidateCurrent();
                        Assert(!result.Success && result.Pending && result.State == "PreparedInvalidated" && !f.Runtime.IsArmed, "Drift retained prepared success.");
                        Assert(f.Platform.Values[metric] == "automatic=1;metric=25", "External metric was overwritten.");
                        if (changed == Corp) Assert(f.Platform.Values[Key(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = Internet })] == "automatic=0;metric=5", "Internet priority restored.");
                        for (int i = 0; i < 3; i++) Assert(f.Runtime.ValidateCurrent().State == "PreparedInvalidated", "Pending cause disappeared.");
                        Assert(f.Runtime.Stop(false).State == "EgressConfirmationRequired", "Explicit default restore no longer requires approval.");
                        AssertPreparedPreserved(f, original);
                    });
                foreach (bool replace in new[] { false, true })
                    Test("prepared company GUID loss or replacement is not a read timeout replace=" + replace, delegate
                    {
                        Fixture f = PreparedReadFixture(); string original = DualJournalFingerprint.Compute(f.Journal.Stored);
                        if (replace) f.Platform.Snapshot.Adapters[0].Id = "33333333-3333-3333-3333-333333333333";
                        else f.Platform.Snapshot.Adapters.RemoveAt(0);
                        Assert(f.Runtime.ValidateCurrent().State == "PreparedInvalidated" && !f.Runtime.LastFailure.Deferred, "Missing GUID deferred or substituted by name.");
                        AssertPreparedPreserved(f, original);
                    });
                Test("prepared unowned preexisting correct metric is still checked by GUID", delegate
                {
                    Fixture f = new Fixture();
                    f.Platform.Values[Key(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = Corp })] = "automatic=0;metric=55";
                    Assert(f.Runtime.Arm(f.Request, true).Success, "Preexisting metric arm failed.");
                    f.Platform.Writes = f.Platform.Removes = f.Journal.Saves = f.Journal.Deletes = 0;
                    string original = DualJournalFingerprint.Compute(f.Journal.Stored); f.Platform.Snapshot.Adapters.RemoveAt(0);
                    Assert(f.Runtime.ValidateCurrent().State == "PreparedInvalidated", "GUID without owned metric was not checked.");
                    AssertPreparedPreserved(f, original);
                });
                Test("prepared unknown read failure invalidates without cleanup", delegate
                {
                    Fixture f = PreparedReadFixture(); string original = DualJournalFingerprint.Compute(f.Journal.Stored); f.Platform.FailRead = true;
                    Assert(f.Runtime.ValidateCurrent().State == "PreparedInvalidated" && !f.Runtime.LastFailure.Deferred, "Unknown read was successful or deferred.");
                    AssertPreparedPreserved(f, original);
                });
                Test("prepared confirmed read timeout has cooldown and requires a successful metric retry", delegate
                {
                    Fixture f = PreparedReadFixture(); string original = DualJournalFingerprint.Compute(f.Journal.Stored); int attempts = 0;
                    f.Platform.BeforeRead = delegate { if (++attempts == 1) throw new DualReadUnavailableException("worker_timeout:response"); };
                    DualOperationResult result = f.Runtime.ValidateCurrent();
                    Assert(!result.Success && result.State == "ReadDeferred" && f.Runtime.IsArmed, "Read timeout claimed preparation success.");
                    int captures = f.Platform.Captures;
                    Assert(!f.Runtime.ValidateCurrent().Success && f.Platform.Captures == captures && attempts == 1, "Cooldown retried IO or claimed success.");
                    f.Now = f.Now.AddSeconds(5);
                    Assert(f.Runtime.ValidateCurrent().Success && f.Runtime.LastFailure == null && attempts == 3, "Metrics were not actually rechecked.");
                    AssertPreparedPreserved(f, original);
                });
                foreach (bool expire in new[] { false, true })
                    Test("prepared typed read failure is bounded without automatic rollback expire=" + expire, delegate
                    {
                        Fixture f = PreparedReadFixture(); string original = DualJournalFingerprint.Compute(f.Journal.Stored);
                        f.Platform.CaptureOverride = delegate { throw new DualReadUnavailableException("worker_timeout:response"); };
                        Assert(f.Runtime.ValidateCurrent().State == "ReadDeferred", "First timeout not deferred.");
                        if (!expire) { f.Now = f.Now.AddSeconds(5); Assert(f.Runtime.ValidateCurrent().State == "ReadDeferred", "Second timeout not deferred."); }
                        f.Now = f.Now.AddSeconds(expire ? 31 : 5);
                        Assert(f.Runtime.ValidateCurrent().State == "PreparedInvalidated" && !f.Runtime.IsArmed, "Uncertainty budget was extended indefinitely.");
                        AssertPreparedPreserved(f, original);
                    });
                Test("IPv6 default is rejected even when preview safety boolean is true", delegate
                {
                    Fixture f = new Fixture(); f.Platform.Snapshot.Adapters[1].HasIpv6Default = true; f.Platform.Snapshot.Ipv6SafetyVerified = true;
                    Assert(!f.Runtime.Start(f.Request, true).Success && f.Platform.Writes == 0, "IPv6 apply accepted.");
                });
                Test("connected preparation routes authentication networks before admission without DNS interception", delegate
                {
                    Fixture f = new Fixture(); f.Request.CorporateCidrs.Add("10.0.0.0/8");
                    f.Platform.Snapshot.CorporateAdmissionVerified = false;
                    Assert(f.Runtime.Arm(f.Request, true).Success, "Authentication preparation failed.");
                    Assert(!f.Platform.Snapshot.CorporateAdmissionVerified && f.Dns.Starts == 0, "Preparation fabricated admission or started DNS.");
                    Assert(f.Journal.Stored.Entries.Any(e => e.Resource.Kind == DualResourceKind.Route4 && e.Resource.Destination == "10.0.0.0/8"), "Company aggregate missing before admission.");
                    Assert(!f.Journal.Stored.Entries.Any(e => e.Resource.Kind == DualResourceKind.Nrpt), "Preparation intercepted DNS prematurely.");
                    Assert(f.Runtime.Stop(true).Success, "Prepared resources failed cleanup."); f.AssertClean();
                });
                Test("company prefix answers use verified route without per-IP writes and keep expiry protection", delegate
                {
                    Fixture f = new Fixture(); f.Request.CorporateCidrs.Add("10.0.0.0/8"); f.Start();
                    int reads=f.Platform.Reads, writes=f.Platform.Writes, captures=f.Platform.Captures;
                    Assert(f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[]{"10.66.16.104"}), "Covered answer rejected.");
                    Assert(f.Platform.Reads==reads && f.Platform.Writes==writes && f.Platform.Captures==captures, "Covered answer performed slow commands.");
                    Assert(!f.Dns.Callback(f.Dns.Generation, RuleTarget.Internet, new[]{"10.66.16.104"}), "Shared IP switched target.");
                    f.Now=f.Now.AddSeconds(61);
                    DualResource prefix=new DualResource{Kind=DualResourceKind.Route4,InterfaceId=Corp,Destination="10.0.0.0/8",NextHop="192.0.2.1"};
                    f.Platform.Values.Remove(Key(prefix));
                    Assert(!f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[]{"10.66.16.104"}), "Removed company prefix was accepted.");
                    Assert(f.Platform.Writes==writes, "Failure rewrote a prefix or restored default-route priority."); f.AssertProtected();
                });
                Test("unsafe proxy tunnel DNS policy and unstable snapshots block arm", delegate
                {
                    Action<NetworkSnapshot>[] mutations = { delegate(NetworkSnapshot s) { s.SystemProxyActive = true; }, delegate(NetworkSnapshot s) { s.TunnelActive = true; },
                        delegate(NetworkSnapshot s) { s.OtherDnsPolicyPresent = true; }, delegate(NetworkSnapshot s) { s.CleanupPending = true; },
                        delegate(NetworkSnapshot s) { s.StableSeconds = 7; }, delegate(NetworkSnapshot s) { s.DualConnectionAllowed = false; }, delegate(NetworkSnapshot s) { s.InternetReachabilityVerified = false; } };
                    foreach (Action<NetworkSnapshot> change in mutations) { Fixture f = new Fixture(); change(f.Platform.Snapshot); Assert(!f.Runtime.Arm(f.Request, true).Success && f.Platform.Writes == 0, "Unsafe arm accepted."); }
                });
                Test("successful start readbacks all owned states and duplicate start is inert", delegate
                {
                    Fixture f = new Fixture(); f.Start(); int writes = f.Platform.Writes, starts = f.Dns.Starts, captures = f.Platform.Captures;
                    Assert(f.Runtime.Start(f.Request, true).Success && f.Platform.Writes == writes && f.Dns.Starts == starts && f.Platform.Captures == captures, "Duplicate start mutated.");
                    Assert(f.Journal.Stored.Entries.All(delegate(DualJournalEntry e) { return e.Applied; }), "Unverified entries reported active.");
                    Assert(f.Runtime.Stop(true).Success, "Stop failed."); f.AssertClean();
                });
                Test("startup DNS admission fails fast until NRPT final snapshot and Running save finish", delegate
                {
                    Fixture f = new Fixture(); ManualResetEvent entered = new ManualResetEvent(false), release = new ManualResetEvent(false);
                    bool finalSnapshot = false, queryAccepted = true; DualOperationResult result = null; Exception startError = null;
                    f.Platform.CaptureOverride = delegate { if (f.Dns.Running) finalSnapshot = true; return f.Platform.Snapshot; };
                    f.Platform.BeforeWrite = delegate
                    {
                        if (f.Dns.Running && !entered.WaitOne(0)) { entered.Set(); if (!release.WaitOne(5000)) throw new Exception("fixture startup wait"); }
                    };
                    f.Dns.BeforeResume = delegate
                    {
                        Assert(finalSnapshot && f.Journal.Stored.Phase == "Running", "DNS opened before final snapshot or Running persistence.");
                        Assert(f.Journal.Stored.Entries.Count(e => e.Resource.Kind == DualResourceKind.Nrpt && e.Applied && !e.Restored) == 2, "DNS opened before all NRPT readbacks.");
                    };
                    Thread starter = new Thread(delegate() { try { result = f.Runtime.Start(f.Request, true); } catch (Exception ex) { startError = ex; } });
                    Thread query = new Thread(delegate() { queryAccepted = f.Dns.ResolveRoute("192.0.2.90"); });
                    starter.Start();
                    try
                    {
                        Assert(entered.WaitOne(2000), "Startup did not reach frozen NRPT write.");
                        Assert(f.Journal.Stored.Phase == "Starting" && f.Dns.Resumes == 0, "Startup admitted queries early.");
                        query.Start(); Assert(query.Join(1000) && !queryAccepted, "DNS query waited behind the startup lock or returned an unverified answer.");
                    }
                    finally
                    {
                        release.Set(); Assert(starter.Join(5000), "Startup worker did not finish.");
                        if (query.ThreadState != System.Threading.ThreadState.Unstarted) Assert(query.Join(5000), "Query worker did not finish.");
                        f.Platform.BeforeWrite = null;
                        entered.Dispose(); release.Dispose();
                    }
                    Assert(startError == null && result != null && result.Success && f.Dns.Resumes == 1, "Startup failed after releasing the gate.");
                    Assert(f.Dns.ResolveRoute("192.0.2.90"), "Completed startup did not retain the route ownership gate.");
                });
                Test("same-thread DNS callbacks cannot write routes before startup persistence commits", delegate
                {
                    Fixture f = new Fixture(); int rejected = 0;
                    Action probe = delegate
                    {
                        int writes = f.Platform.Writes, reads = f.Platform.Reads, captures = f.Platform.Captures;
                        Assert(!f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "192.0.2.90" }), "Startup callback was accepted.");
                        Assert(f.Platform.Writes == writes && f.Platform.Reads == reads && f.Platform.Captures == captures, "Startup callback performed route work."); rejected++;
                    };
                    f.Dns.AfterStartPaused = probe;
                    f.Journal.BeforeSave = delegate(DualJournalDocument value) { if (value.Phase == "Running") probe(); };
                    f.Start(); f.Journal.BeforeSave = null;
                    Assert(rejected == 2 && f.Dns.ResolveRoute("192.0.2.90"), "Committed callback did not retain route validation.");
                });
                Test("startup failures before activation never open DNS admission", delegate
                {
                    for (int stage = 0; stage < 4; stage++)
                    {
                        Fixture f = new Fixture();
                        if (stage == 0) f.Platform.FailWriteKind = DualResourceKind.Nrpt;
                        if (stage == 1) f.Platform.CaptureOverride = delegate
                        { if (f.Dns.Running) { f.Platform.CaptureOverride = null; throw new Exception("fixture final snapshot"); } return f.Platform.Snapshot; };
                        if (stage == 2) f.Journal.BeforeSave = delegate(DualJournalDocument value)
                        { if (value.Phase == "Running") { f.Journal.BeforeSave = null; throw new Exception("fixture Running save"); } };
                        if (stage == 3) f.Dns.FailResume = true;
                        Assert(!f.Runtime.Start(f.Request, true).Success && f.Dns.Resumes == 0 && !f.Dns.ResolveRoute("192.0.2.90"), "Failed startup admitted DNS at stage " + stage);
                        f.AssertProtected();
                    }
                });
                Test("activation cancelled by Pause cannot reopen and NRPT cleanup still precedes DNS stop", delegate
                {
                    Fixture f = new Fixture(); f.Dns.BeforeResume = delegate { f.Dns.Pause(); };
                    Assert(!f.Runtime.Start(f.Request, true).Success && f.Dns.Resumes == 0, "Cancelled activation reopened DNS.");
                    Assert(f.Platform.Calls.IndexOf("remove:Nrpt") < f.Platform.Calls.IndexOf("dns:stop"), "Activation failure stopped DNS before NRPT removal.");
                    f.AssertProtected();
                });
                Test("activation failure with NRPT cleanup pending retains a paused listener", delegate
                {
                    Fixture f = new Fixture(); f.Dns.FailResume = true; f.Platform.FailRemoveKind = DualResourceKind.Nrpt;
                    Assert(!f.Runtime.Start(f.Request, true).Success && f.Runtime.HasPending && f.Dns.Running && f.Dns.Stops == 0, "Pending NRPT lost its listener.");
                    Assert(f.Dns.Resumes == 0 && !f.Dns.ResolveRoute("192.0.2.90"), "Pending startup admitted a DNS answer.");
                    Assert(f.Runtime.Stop(true).Success, "Explicit cleanup failed."); f.AssertClean();
                });
                Test("send drain failure leaves NRPT DNS routes and priorities pending on Stop and Recover", delegate
                {
                    Fixture f = new Fixture(); f.Start(); f.Dns.FailPause = true;
                    string journal = DualJournalFingerprint.Compute(f.Journal.Stored); int writes = f.Platform.Writes, removes = f.Platform.Removes;
                    Assert(!f.Runtime.Stop(true).Success && f.Runtime.HasPending && f.Dns.Running && f.Dns.Stops == 0, "Stop ignored send drain failure.");
                    Assert(f.Platform.Writes == writes && f.Platform.Removes == removes && DualJournalFingerprint.Compute(f.Journal.Stored) == journal, "Stop removed dependencies or changed journal after failed drain.");
                    Assert(!f.Dns.ResolveRoute("192.0.2.90"), "Failed drain kept DNS admission open.");
                    Assert(!f.Runtime.Recover(f.Request, true, true).Success && f.Dns.Stops == 0 && f.Platform.Writes == writes && f.Platform.Removes == removes, "Recover ignored send drain failure.");
                    f.Dns.FailPause = false; Assert(f.Runtime.Stop(true).Success, "Later explicit cleanup failed."); f.AssertClean();
                });
                Test("failed activation plus failed send drain cannot continue automatic dependency cleanup", delegate
                {
                    Fixture f = new Fixture(); f.Dns.FailResume = true; f.Dns.FailPause = true;
                    Assert(!f.Runtime.Start(f.Request, true).Success && f.Runtime.HasPending && f.Dns.Running && f.Dns.Stops == 0 && f.Platform.Removes == 0, "Automatic rollback ignored send drain failure.");
                    Assert(f.Journal.Stored.Entries.Any(e => e.Resource.Kind == DualResourceKind.Nrpt && e.Applied && !e.Restored), "Pending NRPT journal was discarded.");
                    Assert(!f.Dns.ResolveRoute("192.0.2.90"), "Automatic failure admitted an answer.");
                    f.Dns.FailPause = false; Assert(f.Runtime.Stop(true).Success, "Later explicit cleanup failed."); f.AssertClean();
                });
                Test("cleanup order keeps DNS until NRPT verified then routes then metrics", delegate
                {
                    Fixture f = new Fixture(); f.Start(); f.Platform.Calls.Clear(); Assert(f.Runtime.Stop(true).Success, "Stop failed.");
                    int pause = f.Platform.Calls.IndexOf("dns:pause"), nrpt = f.Platform.Calls.IndexOf("remove:Nrpt"), dns = f.Platform.Calls.IndexOf("dns:stop"), routes = f.Platform.Calls.IndexOf("remove:Route4"), metric = f.Platform.Calls.IndexOf("write:Metric4");
                    Assert(pause < nrpt && nrpt < dns && dns < routes && routes < metric, "Unsafe cleanup order.");
                });
                Test("write-ahead save failure performs no network write", delegate
                { Fixture f = new Fixture(); f.Journal.FailSaveAt = 2; Assert(!f.Runtime.Start(f.Request, true).Success && f.Platform.Writes == 0, "Wrote without journal."); f.AssertClean(); });
                Test("failures before metrics clean up while later failures retain internet protection", delegate
                {
                    foreach (DualResourceKind kind in new[] { DualResourceKind.Metric4, DualResourceKind.Route4, DualResourceKind.Nrpt })
                    { Fixture f = new Fixture(); f.Platform.FailWriteKind = kind; Assert(!f.Runtime.Start(f.Request, true).Success, "Injected write failure accepted."); if (kind == DualResourceKind.Metric4) f.AssertClean(); else f.AssertProtected(); }
                    Fixture dnsFixture = new Fixture(); dnsFixture.Dns.FailStart = true; Assert(!dnsFixture.Runtime.Start(dnsFixture.Request, true).Success, "DNS start failure accepted."); dnsFixture.AssertProtected();
                });
                Test("readback mismatch remains pending without clobbering external value", delegate
                {
                    Fixture f = new Fixture(); f.Platform.CorruptWriteKind = DualResourceKind.Nrpt;
                    Assert(!f.Runtime.Start(f.Request, true).Success && f.Runtime.HasPending && f.Dns.Running, "Unverified NRPT stopped DNS.");
                    Assert(f.Platform.Values.Values.Contains("external-readback") && f.Journal.Stored != null, "Unknown state overwritten.");
                });
                Test("unconfirmed same-value concurrent route creation is never deleted", delegate
                {
                    Fixture f = new Fixture(); f.Platform.ConcurrentSameValueKind = DualResourceKind.Route4;
                    Assert(!f.Runtime.Start(f.Request, true).Success && f.Runtime.HasPending, "Ambiguous write accepted.");
                    Assert(f.Platform.Values.Values.Contains("metric=1") && f.Platform.Removes == 0, "Externally created same-value route deleted.");
                    Assert(f.Journal.Stored.Entries.Any(delegate(DualJournalEntry e) { return e.Resource.Kind == DualResourceKind.Route4 && !e.Applied && !e.Restored; }), "Ambiguous ownership was discarded.");
                    Assert(!f.Runtime.Stop(true).Success && f.Platform.Removes == 0, "Retry guessed ambiguous route ownership.");
                });
                Test("unconfirmed same-value concurrent metric modification is not restored", delegate
                {
                    Fixture f = new Fixture(); f.Platform.ConcurrentSameValueKind = DualResourceKind.Metric4;
                    Assert(!f.Runtime.Start(f.Request, true).Success && f.Runtime.HasPending, "Ambiguous metric accepted.");
                    Assert(f.Platform.Values.Values.Contains("automatic=0;metric=5") && f.Platform.Writes == 1, "Concurrent metric was overwritten.");
                });
                Test("failed NRPT cleanup retains listener and later retry succeeds", delegate
                {
                    Fixture f = new Fixture(); f.Start(); f.Platform.FailRemoveKind = DualResourceKind.Nrpt;
                    Assert(!f.Runtime.Stop(true).Success && f.Runtime.HasPending && f.Dns.Running, "Failure stopped required listener.");
                    Assert(f.Runtime.Stop(true).Success, "Cleanup retry failed."); f.AssertClean();
                });
                Test("failed DNS stop prevents route cleanup", delegate
                {
                    Fixture f = new Fixture(); f.Start(); f.Platform.Calls.Clear(); f.Dns.FailStop = true;
                    Assert(!f.Runtime.Stop(true).Success && !f.Platform.Calls.Contains("remove:Route4"), "Routes removed before DNS stop.");
                    f.Dns.FailStop = false; Assert(f.Runtime.Stop(true).Success, "DNS retry failed."); f.AssertClean();
                });
                Test("route cleanup failure retains journal for explicit retry", delegate
                { Fixture f = new Fixture(); f.Start(); f.Platform.FailRemoveKind = DualResourceKind.Route4; Assert(!f.Runtime.Stop(true).Success && f.Runtime.HasPending && !f.Dns.Running, "Route failure mishandled."); Assert(f.Runtime.Stop(true).Success, "Route retry failed."); f.AssertClean(); });
                Test("external manual metric modification is not overwritten", delegate
                {
                    Fixture f = new Fixture(); f.Start(); string key = Key(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = Corp }); f.Platform.Values[key] = "automatic=0;metric=99";
                    Assert(!f.Runtime.Stop(true).Success && f.Runtime.HasPending && f.Platform.Values[key] == "automatic=0;metric=99", "External metric overwritten.");
                });
                Test("automatic metric restoration permits Windows numeric recalculation", delegate
                {
                    Fixture f = new Fixture(); f.Start(); f.Platform.RecomputeAutomatic = true;
                    Assert(f.Runtime.Stop(true).Success, "Automatic metric recalculation rejected."); f.AssertClean();
                    Assert(f.Platform.Values.Values.All(delegate(string v) { return v == "automatic=1;metric=77"; }), "Automatic mode not restored.");
                });
                Test("equal metric semantics never equate manual and automatic modes", delegate
                {
                    DualResource metric = new DualResource { Kind = DualResourceKind.Metric4 };
                    Assert(DualNetworkRuntime.ResourceValuesEqual(metric, "automatic=1;metric=25", "automatic=1;metric=77"), "Auto values not semantically equal.");
                    Assert(!DualNetworkRuntime.ResourceValuesEqual(metric, "automatic=0;metric=25", "automatic=1;metric=25"), "Manual mode mistaken as auto.");
                    Assert(!DualNetworkRuntime.ResourceValuesEqual(metric, "automatic=1;metric=0", "automatic=1;metric=77"), "Invalid auto metric accepted.");
                });
                Test("shared exact route is not claimed or deleted", delegate
                {
                    Fixture f = new Fixture(); DualResource shared = new DualResource { Kind = DualResourceKind.Route4, InterfaceId = Corp, Destination = "192.0.2.53/32", NextHop = "0.0.0.0" };
                    f.Platform.Values[Key(shared)] = "metric=1"; f.Start(); Assert(!f.Journal.Stored.Entries.Any(delegate(DualJournalEntry e) { return Key(e.Resource) == Key(shared); }), "Shared route claimed.");
                    Assert(f.Runtime.Stop(true).Success && f.Platform.Values[Key(shared)] == "metric=1", "Shared route removed.");
                });
                Test("conflicting or differently weighted existing route is rejected", delegate
                {
                    foreach (string existing in new[] { "metric=2", "conflict", "invalid" })
                    {
                        Fixture f = new Fixture(); DualResource shared = new DualResource { Kind = DualResourceKind.Route4, InterfaceId = Corp, Destination = "192.0.2.53/32", NextHop = "0.0.0.0" };
                        f.Platform.Values[Key(shared)] = existing; Assert(!f.Runtime.Start(f.Request, true).Success && f.Platform.Values[Key(shared)] == existing, "Invalid shared route borrowed/modified.");
                    }
                });
                Test("late DNS callback after stop and new generation is inert", delegate
                {
                    Fixture f = new Fixture(); f.Start(); long old = f.Dns.Generation; Func<long, RuleTarget, string[], bool> callback = f.Dns.Callback;
                    Assert(f.Runtime.Stop(true).Success, "Stop failed."); f.Start(); int writes = f.Platform.Writes, captures = f.Platform.Captures;
                    Assert(!callback(old, RuleTarget.Corporate, new[] { "192.0.2.90" }) && f.Platform.Writes == writes && f.Platform.Captures == captures, "Late callback escaped generation gate.");
                });
                Test("cached DNS address hot path performs zero Windows calls", delegate
                {
                    Fixture f = new Fixture(); f.Start(); int captures = f.Platform.Captures;
                    Assert(f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "192.0.2.90" }), "First callback failed.");
                    Assert(f.Platform.Captures == captures + 1, "New destination captured topology more than once.");
                    captures = f.Platform.Captures; int reads = f.Platform.Reads, writes = f.Platform.Writes;
                    for (int i = 0; i < 50; i++) Assert(f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "192.0.2.90" }), "Cached callback failed.");
                    Assert(f.Platform.Captures == captures && f.Platform.Reads == reads && f.Platform.Writes == writes, "DNS hot path called Windows.");
                });
                Test("same resolved IP cannot switch to the opposite target", delegate
                {
                    Fixture f = new Fixture(); f.Start(); Assert(f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "203.0.113.90" }), "Initial destination rejected.");
                    int writes = f.Platform.Writes; Assert(!f.Dns.Callback(f.Dns.Generation, RuleTarget.Internet, new[] { "203.0.113.90" }) && f.Platform.Writes == writes, "Shared IP changed egress.");
                });
                Test("route confirmation expires after 60 seconds without losing target ownership", delegate
                {
                    Fixture f = new Fixture(); f.Start(); Assert(f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "203.0.113.90" }), "First route failed.");
                    int reads = f.Platform.Reads, captures = f.Platform.Captures, writes = f.Platform.Writes, saves = f.Journal.Saves;
                    f.Now = f.Now.AddSeconds(59);
                    Assert(f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "203.0.113.90" }) && f.Platform.Reads == reads, "Fresh route re-read.");
                    f.Now = f.Now.AddSeconds(1);
                    Assert(f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "203.0.113.90" }) && f.Platform.Reads == reads + 1, "Expired route not checked exactly once.");
                    Assert(f.Platform.Captures == captures && f.Platform.Writes == writes && f.Journal.Saves == saves, "Expiry rewrote or captured unnecessarily.");
                    Assert(!f.Dns.Callback(f.Dns.Generation, RuleTarget.Internet, new[] { "203.0.113.90" }), "Expiry forgot original egress.");
                });
                Test("expired deleted owned route stops without silently recreating it", delegate
                {
                    Fixture f = new Fixture(); f.Start(); Assert(f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "192.0.2.90" }), "First route failed.");
                    DualResource route = new DualResource { Kind = DualResourceKind.Route4, InterfaceId = Corp, Destination = "192.0.2.90/32", NextHop = "0.0.0.0" };
                    f.Platform.Values.Remove(Key(route)); f.Now = f.Now.AddSeconds(60);
                    Assert(!f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "192.0.2.90" }), "Deleted cached route accepted."); f.AssertProtected();
                });
                Test("expired modified borrowed route is preserved and session stops", delegate
                {
                    Fixture f = new Fixture(); DualResource route = new DualResource { Kind = DualResourceKind.Route4, InterfaceId = Corp, Destination = "192.0.2.90/32", NextHop = "0.0.0.0" };
                    f.Platform.Values[Key(route)] = "metric=1"; f.Start(); Assert(f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "192.0.2.90" }), "Borrow failed.");
                    f.Platform.Values[Key(route)] = "metric=20"; f.Now = f.Now.AddSeconds(60);
                    Assert(!f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "192.0.2.90" }) && !f.Runtime.IsActive && f.Runtime.HasPending && f.Platform.Values[Key(route)] == "metric=20", "Borrowed edit overwritten."); f.AssertProtected();
                });
                Test("backward clock invalidates confirmation instead of extending it", delegate
                {
                    Fixture f = new Fixture(); f.Start(); Assert(f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "192.0.2.90" }), "First route failed.");
                    int reads = f.Platform.Reads; f.Now = f.Now.AddSeconds(-1);
                    Assert(f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "192.0.2.90" }) && f.Platform.Reads == reads + 1, "Backward clock retained stale confirmation.");
                });
                Test("UI status reads do not wait behind blocked platform write", delegate
                {
                    Fixture f = new Fixture(); f.Start(); ManualResetEvent entered = new ManualResetEvent(false), release = new ManualResetEvent(false);
                    f.Platform.BeforeWrite = delegate { entered.Set(); if (!release.WaitOne(5000)) throw new Exception("fixture wait expired"); };
                    Thread worker = new Thread(delegate() { f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "192.0.2.90" }); });
                    worker.Start();
                    try
                    {
                        Assert(entered.WaitOne(2000), "Write not entered."); Stopwatch timer = Stopwatch.StartNew();
                        bool active = f.Runtime.IsActive, armed = f.Runtime.IsArmed, pending = f.Runtime.HasPending; string state = f.Runtime.State; long generation = f.Runtime.Generation;
                        timer.Stop(); Assert(timer.ElapsedMilliseconds < 100 && pending && active && armed && state == "Running" && generation > 0, "Status read blocked or did not conservatively gate.");
                    }
                    finally { release.Set(); worker.Join(5000); entered.Dispose(); release.Dispose(); }
                });
                Test("low frequency route audit catches deletion with no DNS queries", delegate
                {
                    Fixture f = new Fixture(); f.Start();
                    foreach (DualJournalEntry entry in f.Journal.Stored.Entries.Where(delegate(DualJournalEntry e) { return e.Resource.Kind == DualResourceKind.Route4; })) f.Platform.Values.Remove(Key(entry.Resource));
                    int reads = f.Platform.Reads;
                    for (int i = 0; i < 5; i++) Assert(f.Runtime.ValidateCurrent().Success, "Audit ran too early.");
                    Assert(f.Platform.Reads == reads && !f.Runtime.ValidateCurrent().Success, "Round-robin route audit missing."); f.AssertProtected();
                });
                Test("dynamic answers cannot hijack protected external or company addresses", delegate
                {
                    Fixture f = new Fixture(); f.Start(); int writes = f.Platform.Writes;
                    Assert(!f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "203.0.113.53" }), "External DNS hijacked.");
                    Assert(!f.Dns.Callback(f.Dns.Generation, RuleTarget.Internet, new[] { "192.0.2.53" }), "Corporate DNS hijacked.");
                    Assert(!f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "198.18.0.8" }), "Fake IP accepted.");
                    Assert(f.Platform.Writes == writes, "Rejected answer wrote network state.");
                });
                Test("resource cap prevents unbounded routes and safely stops session", delegate
                {
                    Fixture f = new Fixture(); f.Start(); string[] addresses = Enumerable.Range(1, 4097).Select(delegate(int i) { return "100.64." + (i / 250) + "." + (i % 250 + 1); }).ToArray();
                    Assert(!f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, addresses), "Over-limit routes accepted."); f.AssertProtected();
                });
                Test("topology drift on new DNS answer rolls back instead of writing old path", delegate
                {
                    Fixture f = new Fixture(); f.Start(); f.Platform.Snapshot.Adapters[0].Gateway = "192.0.2.2";
                    Assert(!f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "192.0.2.91" }), "Stale topology accepted."); f.AssertProtected();
                });
                Test("low-frequency topology check detects loss without DNS query", delegate
                { Fixture f = new Fixture(); f.Start(); f.Platform.Snapshot.Adapters[1].IsUp = false; Assert(!f.Runtime.ValidateCurrent().Success, "Lost network accepted."); Assert(f.Runtime.HasPending && f.Runtime.State == "CleanupPending", "Unknown topology claimed protected or silently restored metrics."); });
                Test("blocked topology Capture does not block cached DNS answers", delegate
                {
                    Fixture f = new Fixture(); f.Start(); Assert(f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "192.0.2.90" }), "Initial route failed.");
                    ManualResetEvent entered = new ManualResetEvent(false), release = new ManualResetEvent(false);
                    f.Platform.CaptureOverride = delegate { entered.Set(); if (!release.WaitOne(5000)) throw new Exception("fixture capture wait"); return f.Platform.Snapshot; };
                    DualOperationResult checkedResult = null;
                    Thread checker = new Thread(delegate() { checkedResult = f.Runtime.ValidateCurrent(); }); checker.Start();
                    try
                    {
                        Assert(entered.WaitOne(2000), "Capture not entered."); Stopwatch timer = Stopwatch.StartNew();
                        bool ok = f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "192.0.2.90" }); timer.Stop();
                        Assert(ok && timer.ElapsedMilliseconds < 100, "Cached DNS blocked behind Capture.");
                    }
                    finally { release.Set(); Assert(checker.Join(5000), "Capture worker did not finish."); entered.Dispose(); release.Dispose(); }
                    Assert(checkedResult != null && checkedResult.Success && f.Runtime.IsActive, "Passive check unexpectedly stopped session.");
                });
                Test("late Capture failure cannot stop a new generation", delegate
                {
                    Fixture f = new Fixture(); f.Start(); long previous = f.Runtime.Generation;
                    ManualResetEvent entered = new ManualResetEvent(false), release = new ManualResetEvent(false); int first = 0;
                    f.Platform.CaptureOverride = delegate
                    {
                        if (Interlocked.CompareExchange(ref first, 1, 0) == 0)
                        { entered.Set(); if (!release.WaitOne(5000)) throw new Exception("fixture capture wait"); throw new Exception("old generation capture failure"); }
                        return f.Platform.Snapshot;
                    };
                    DualOperationResult checkedResult = null;
                    Thread checker = new Thread(delegate() { checkedResult = f.Runtime.ValidateCurrent(); }); checker.Start();
                    try
                    {
                        Assert(entered.WaitOne(2000), "Capture not entered."); Assert(f.Runtime.Stop(true).Success, "Stop blocked or failed during Capture."); f.Start();
                        Assert(f.Runtime.Generation > previous, "Generation not advanced.");
                    }
                    finally { release.Set(); Assert(checker.Join(5000), "Capture worker did not finish."); entered.Dispose(); release.Dispose(); }
                    Assert(checkedResult != null && checkedResult.Success && f.Runtime.IsActive && !f.Runtime.HasPending && f.Dns.Running, "Stale capture failure affected new generation.");
                });
                Test("DNS rollback cause survives a concurrently completing older validation", delegate
                {
                    Fixture f = new Fixture(); f.Request.CorporateCidrs.Add("10.0.0.0/8"); f.Start();
                    Assert(f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[]{"10.66.16.104"}), "Covered route fixture failed.");
                    DualResource covering = f.Journal.Stored.Entries.First(e => e.Resource.Kind == DualResourceKind.Route4 && e.Resource.Destination == "10.0.0.0/8").Resource;
                    f.Now = f.Now.AddSeconds(61);
                    using(ManualResetEvent entered = new ManualResetEvent(false))
                    using(ManualResetEvent release = new ManualResetEvent(false))
                    {
                        int first = 0;
                        f.Platform.CaptureOverride = delegate
                        {
                            if(Interlocked.CompareExchange(ref first, 1, 0) == 0)
                            { entered.Set(); if(!release.WaitOne(5000))throw new Exception("fixture capture wait"); }
                            return f.Platform.Snapshot;
                        };
                        DualOperationResult result = null;
                        Thread checker = new Thread(delegate(){result=f.Runtime.ValidateCurrent();});checker.Start();
                        try
                        {
                            Assert(entered.WaitOne(2000), "Concurrent validation did not enter Capture.");
                            f.Platform.Values.Remove(Key(covering));
                            Assert(!f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[]{"10.66.16.104"}), "Deleted covering route was accepted.");
                            f.AssertProtected();
                        }
                        finally{release.Set();Assert(checker.Join(5000),"Concurrent validation did not finish.");}
                        Assert(result!=null&&!result.Success&&result.Pending&&result.State=="InternetProtected", "Background failure state was lost.");
                        Assert(result.Message.Contains("已确认的路由已被删除或修改"), "The original DNS rollback cause was replaced by a stale-generation message.");
                    }
                });
                Test("late invalid Capture result after Stop does not resurrect or clean twice", delegate
                {
                    Fixture f = new Fixture(); f.Start(); ManualResetEvent entered = new ManualResetEvent(false), release = new ManualResetEvent(false);
                    f.Platform.CaptureOverride = delegate { entered.Set(); if (!release.WaitOne(5000)) throw new Exception("fixture capture wait"); return new NetworkSnapshot(); };
                    DualOperationResult checkedResult = null; Thread checker = new Thread(delegate() { checkedResult = f.Runtime.ValidateCurrent(); }); checker.Start();
                    int writes = 0, removes = 0;
                    try
                    { Assert(entered.WaitOne(2000), "Capture not entered."); Assert(f.Runtime.Stop(true).Success, "Stop failed."); writes = f.Platform.Writes; removes = f.Platform.Removes; }
                    finally { release.Set(); Assert(checker.Join(5000), "Capture worker did not finish."); entered.Dispose(); release.Dispose(); }
                    Assert(checkedResult != null && checkedResult.Success && f.Platform.Writes == writes && f.Platform.Removes == removes, "Late result changed stopped session."); f.AssertClean();
                });
                Test("confirmed read-only timeout rejects one DNS answer without rolling back and retry recovers", delegate
                {
                    Fixture f=new Fixture();f.Start();int captures=0;
                    f.Platform.CaptureOverride=delegate{if(++captures==1)throw new DualReadUnavailableException("worker_timeout:response");return f.Platform.Snapshot;};
                    int writes=f.Platform.Writes,removes=f.Platform.Removes,entries=f.Journal.Stored.Entries.Count,saves=f.Journal.Saves;
                    long epoch=f.Runtime.Generation;
                    Assert(!f.Dns.Callback(epoch,RuleTarget.Corporate,new[]{"192.0.2.90"}),"Unverified DNS answer escaped.");
                    Assert(f.Runtime.IsActive&&!f.Runtime.HasPending&&f.Dns.Running&&f.Runtime.Generation==epoch&&f.Platform.Writes==writes&&f.Platform.Removes==removes&&f.Journal.Saves==saves&&f.Journal.Stored.Entries.Count==entries,"Read-only failure mutated the session.");
                    DualRuntimeFailure failure=f.Runtime.LastFailure;
                    Assert(failure!=null&&failure.Deferred&&failure.Stage=="dns_capture"&&!object.ReferenceEquals(failure,f.Runtime.LastFailure),"Failure context missing or not copied.");
                    Assert(!f.Dns.Callback(epoch,RuleTarget.Corporate,new[]{"192.0.2.90"})&&captures==1,"Read cooldown retried too early.");
                    f.Now=f.Now.AddSeconds(5);
                    Assert(f.Dns.Callback(epoch,RuleTarget.Corporate,new[]{"192.0.2.90"})&&f.Runtime.LastFailure==null&&f.Runtime.IsActive,"Successful real retry did not clear warning.");
                });
                Test("cached-route read timeout retains route but changed ownership on retry remains fatal", delegate
                {
                    Fixture f=new Fixture();f.Start();Assert(f.Dns.Callback(f.Dns.Generation,RuleTarget.Corporate,new[]{"192.0.2.90"}),"Initial DNS route failed.");f.Now=f.Now.AddSeconds(61);
                    int writes=f.Platform.Writes,removes=f.Platform.Removes;
                    f.Platform.BeforeRead=delegate{f.Platform.BeforeRead=null;throw new DualReadUnavailableException("worker_timeout:response");};
                    Assert(!f.Dns.Callback(f.Dns.Generation,RuleTarget.Corporate,new[]{"192.0.2.90"})&&f.Runtime.IsActive&&f.Platform.Writes==writes&&f.Platform.Removes==removes,"Transient read tore down cached routes.");
                    DualResource route=f.Journal.Stored.Entries.First(e=>e.Resource.Destination=="192.0.2.90/32").Resource;
                    f.Platform.Values.Remove(Key(route));f.Now=f.Now.AddSeconds(5);
                    Assert(!f.Dns.Callback(f.Dns.Generation,RuleTarget.Corporate,new[]{"192.0.2.90"}),"Deleted route was accepted on retry.");f.AssertProtected();
                    Assert(f.Runtime.LastFailure.Code=="route_readback_changed"&&!f.Runtime.LastFailure.Deferred,"Ownership mismatch was softened.");
                });
                Test("periodic validation has read-only bounded backoff and resumes only after actual capture", delegate
                {
                    Fixture f=new Fixture();f.Start();int attempts=0;
                    f.Platform.CaptureOverride=delegate{if(++attempts==1)throw new DualReadUnavailableException("worker_timeout:startup");return f.Platform.Snapshot;};
                    int writes=f.Platform.Writes,removes=f.Platform.Removes;
                    DualOperationResult deferred=f.Runtime.ValidateCurrent();
                    Assert(!deferred.Success&&!deferred.Pending&&deferred.State=="ReadDeferred"&&f.Runtime.IsActive,"Validation claimed success or stopped on one confirmed timeout.");
                    Assert(f.Runtime.ValidateCurrent().State=="ReadDeferred"&&attempts==1&&f.Platform.Writes==writes&&f.Platform.Removes==removes,"Cooldown did IO or changed routes.");
                    f.Now=f.Now.AddSeconds(5);Assert(f.Runtime.ValidateCurrent().Success&&f.Runtime.LastFailure==null&&attempts==2,"Successful capture did not restore validation.");
                });
                Test("third consecutive confirmed read timeout falls back to protected cleanup", delegate
                {
                    Fixture f=new Fixture();f.Start();int attempts=0;
                    f.Platform.CaptureOverride=delegate{if(++attempts<=3)throw new DualReadUnavailableException("worker_timeout:response");return f.Platform.Snapshot;};
                    for(int i=0;i<2;i++){Assert(f.Runtime.ValidateCurrent().State=="ReadDeferred"&&f.Runtime.IsActive,"Early timeout was not deferred.");f.Now=f.Now.AddSeconds(5);}
                    DualOperationResult fatal=f.Runtime.ValidateCurrent();Assert(!fatal.Success&&fatal.Pending,"Unbounded read failures retained active DNS.");f.AssertProtected();
                });
                Test("read deferral has a total-time limit and cannot be extended by fresh cached answers", delegate
                {
                    Fixture f=new Fixture();f.Start();Assert(f.Dns.Callback(f.Dns.Generation,RuleTarget.Corporate,new[]{"192.0.2.90"}),"Cache fixture failed.");int attempts=0;
                    f.Platform.CaptureOverride=delegate{if(++attempts==1)throw new DualReadUnavailableException("worker_timeout:response");return f.Platform.Snapshot;};
                    Assert(f.Runtime.ValidateCurrent().State=="ReadDeferred","Missing timeout warning.");
                    Assert(f.Dns.Callback(f.Dns.Generation,RuleTarget.Corporate,new[]{"192.0.2.90"})&&f.Runtime.LastFailure!=null,"Cache hit falsely renewed verification.");
                    f.Now=f.Now.AddSeconds(31);Assert(!f.Runtime.ValidateCurrent().Success,"Expired uncertainty retained active service.");f.AssertProtected();
                });
                Test("unknown CIM error remains fatal and pending diagnostics never reexecute network operations", delegate
                {
                    Fixture f=new Fixture();f.Start();int attempts=0;
                    f.Platform.CaptureOverride=delegate{if(++attempts==1)throw new InvalidOperationException("fixture [worker_command:CimException]");return f.Platform.Snapshot;};
                    Assert(!f.Dns.Callback(f.Dns.Generation,RuleTarget.Corporate,new[]{"192.0.2.90"}),"Unknown failure escaped.");f.AssertProtected();
                    int captures=f.Platform.Captures,reads=f.Platform.Reads,writes=f.Platform.Writes,removes=f.Platform.Removes;
                    for(int i=0;i<5;i++)Assert(f.Runtime.ValidateCurrent().Message.Contains("worker_command:CimException"),"Pending cause disappeared.");
                    Assert(f.Platform.Captures==captures&&f.Platform.Reads==reads&&f.Platform.Writes==writes&&f.Platform.Removes==removes,"Reading pending status performed network operations.");
                });
                Test("unrelated expired cache read cannot reset another DNS operation timeout budget", delegate
                {
                    Fixture f=new Fixture();f.Start();
                    Assert(f.Dns.Callback(f.Dns.Generation,RuleTarget.Corporate,new[]{"192.0.2.90"})&&f.Dns.Callback(f.Dns.Generation,RuleTarget.Corporate,new[]{"192.0.2.91"}),"Cache fixtures failed.");
                    f.Now=f.Now.AddSeconds(61);f.Platform.BeforeRead=delegate{f.Platform.BeforeRead=null;throw new DualReadUnavailableException("worker_timeout:response");};
                    Assert(!f.Dns.Callback(f.Dns.Generation,RuleTarget.Corporate,new[]{"192.0.2.90"}),"Unverified cached answer escaped.");
                    f.Now=f.Now.AddSeconds(5);
                    Assert(f.Dns.Callback(f.Dns.Generation,RuleTarget.Corporate,new[]{"192.0.2.91"})&&f.Runtime.LastFailure!=null,"Unrelated real read cleared the pending operation warning.");
                    f.Platform.BeforeRead=delegate{f.Platform.BeforeRead=null;throw new DualReadUnavailableException("worker_timeout:send");};
                    Assert(f.Runtime.ValidateCurrent().State=="ReadDeferred"&&f.Runtime.LastFailure.Stage=="validate_deferred_dns_route","Unrelated topology success cleared outstanding DNS read uncertainty.");
                    f.Now=f.Now.AddSeconds(26);Assert(!f.Runtime.ValidateCurrent().Success,"Other cache traffic extended the total uncertainty budget.");f.AssertProtected();
                    Assert(f.Runtime.LastFailure.Code=="read_verification_deadline"&&f.Runtime.LastFailure.Message.Contains("worker_timeout:send"),"Read deadline fabricated a new timeout or lost the original operation.");
                });
                Test("successful retry clears only its own failure when two DNS operations are unverified", delegate
                {
                    Fixture f=new Fixture();f.Start();int failures=0;
                    f.Platform.CaptureOverride=delegate{if(++failures<=2)throw new DualReadUnavailableException("worker_timeout:response");return f.Platform.Snapshot;};
                    Assert(!f.Dns.Callback(f.Dns.Generation,RuleTarget.Corporate,new[]{"192.0.2.90"}),"First timeout escaped.");f.Now=f.Now.AddSeconds(5);
                    Assert(!f.Dns.Callback(f.Dns.Generation,RuleTarget.Corporate,new[]{"192.0.2.91"}),"Second timeout escaped.");f.Now=f.Now.AddSeconds(5);
                    Assert(f.Dns.Callback(f.Dns.Generation,RuleTarget.Corporate,new[]{"192.0.2.90"})&&f.Runtime.LastFailure!=null,"One retry cleared another outstanding timeout.");
                    Assert(f.Dns.Callback(f.Dns.Generation,RuleTarget.Corporate,new[]{"192.0.2.91"})&&f.Runtime.LastFailure==null,"Both recovered operations did not clear warning.");
                });
                Test("validation rechecks a timed out cached route when the DNS client never repeats", delegate
                {
                    Fixture f = new Fixture(); f.Start();
                    long epoch = f.Runtime.Generation;
                    Assert(f.Dns.Callback(epoch, RuleTarget.Corporate, new[] { "192.0.2.90" }), "Initial DNS route failed.");
                    f.Now = f.Now.AddSeconds(61);
                    int writes = f.Platform.Writes, removes = f.Platform.Removes, saves = f.Journal.Saves;
                    f.Platform.BeforeRead = delegate { f.Platform.BeforeRead = null; throw new DualReadUnavailableException("worker_timeout:send"); };
                    Assert(!f.Dns.Callback(epoch, RuleTarget.Corporate, new[] { "192.0.2.90" }), "Timed out DNS answer escaped.");
                    int reads = f.Platform.Reads, captures = f.Platform.Captures;
                    Assert(f.Runtime.ValidateCurrent().State == "ReadDeferred" && f.Platform.Reads == reads && f.Platform.Captures == captures, "Cooldown performed I/O.");
                    f.Now = f.Now.AddSeconds(5);
                    Assert(f.Runtime.ValidateCurrent().Success && f.Platform.Reads == reads + 1 && f.Platform.Captures == captures + 1 && f.Runtime.LastFailure == null, "A successful targeted recheck did not clear the deferred warning.");
                    f.Now = f.Now.AddSeconds(31);
                    Assert(f.Runtime.ValidateCurrent().Success && f.Runtime.IsActive && f.Runtime.Generation == epoch && f.Platform.Writes == writes &&
                        f.Platform.Removes == removes && f.Journal.Saves == saves, "Retried DNS callback or retained a stale deadline.");
                });
                Test("validation verifies the exact failed DNS route and preserves external removal", delegate
                {
                    Fixture f = new Fixture(); f.Start();
                    Assert(f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "192.0.2.91" }), "Initial DNS route failed.");
                    DualResource route = f.Journal.Stored.Entries.Single(e => e.Resource.Destination == "192.0.2.91/32").Resource;
                    f.Now = f.Now.AddSeconds(61);
                    f.Platform.BeforeRead = delegate { f.Platform.BeforeRead = null; throw new DualReadUnavailableException("worker_timeout:send"); };
                    Assert(!f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "192.0.2.91" }), "Timed out DNS answer escaped.");
                    int writes = f.Platform.Writes;
                    f.Platform.Values.Remove(Key(route));
                    f.Now = f.Now.AddSeconds(5);
                    Assert(!f.Runtime.ValidateCurrent().Success && f.Runtime.LastFailure.Code == "route_readback_changed" &&
                        f.Platform.Writes == writes && !f.Platform.Values.ContainsKey(Key(route)), "Changed route was ignored or recreated.");
                    f.AssertProtected();
                });
                Test("validation cannot acknowledge an unissued DNS route or a late read", delegate
                {
                    Fixture f = new Fixture(); f.Start();
                    int writes = f.Platform.Writes;
                    f.Platform.CaptureOverride = delegate { f.Platform.CaptureOverride = null; throw new DualReadUnavailableException("worker_timeout:send"); };
                    Assert(!f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "192.0.2.90" }), "New destination accepted without proof.");
                    f.Now = f.Now.AddSeconds(5);
                    Assert(f.Runtime.ValidateCurrent().State == "ReadDeferred" && f.Platform.Writes == writes, "Background check created or accepted a new route.");
                    f.Platform.CaptureOverride = delegate { f.Now = f.Now.AddSeconds(26); return f.Platform.Snapshot; };
                    Assert(!f.Runtime.ValidateCurrent().Success && f.Runtime.LastFailure.Code == "read_verification_deadline" &&
                        f.Runtime.LastFailure.Message.Contains("dns_capture/worker_timeout:send"), "Long capture overran the original bounded deadline.");
                    f.AssertProtected();
                });
                Test("periodic route-read timeout retries the same audit before clearing deferred verification", delegate
                {
                    Fixture f=new Fixture();f.Start();for(int i=0;i<5;i++)Assert(f.Runtime.ValidateCurrent().Success,"Initial validation failed.");
                    f.Platform.BeforeRead=delegate{f.Platform.BeforeRead=null;throw new DualReadUnavailableException("worker_timeout:response");};
                    Assert(f.Runtime.ValidateCurrent().State=="ReadDeferred","Audit timeout was not deferred.");int reads=f.Platform.Reads;
                    f.Now=f.Now.AddSeconds(5);Assert(f.Runtime.ValidateCurrent().Success&&f.Platform.Reads==reads+1&&f.Runtime.LastFailure==null,"Retry skipped the still-unverified route audit.");
                });
                Test("read timeout after new write-ahead entry is not treated as harmless read-only failure", delegate
                {
                    Fixture f=new Fixture();f.Start();int reads=0;
                    f.Platform.BeforeRead=delegate{if(++reads==2){f.Platform.BeforeRead=null;throw new DualReadUnavailableException("worker_timeout:response");}};
                    Assert(!f.Dns.Callback(f.Dns.Generation,RuleTarget.Corporate,new[]{"192.0.2.90"}),"Uncertain write-ahead was accepted.");f.AssertProtected();
                    Assert(!f.Runtime.LastFailure.Deferred,"Write-ahead uncertainty was softened.");
                });
                Test("typed read timeout after a network write cannot preserve an uncertain partial update", delegate
                {
                    Fixture f=new Fixture();f.Start();
                    f.Platform.BeforeWrite=delegate{f.Platform.BeforeWrite=null;f.Platform.BeforeRead=delegate{f.Platform.BeforeRead=null;throw new DualReadUnavailableException("worker_timeout:response");};};
                    Assert(!f.Dns.Callback(f.Dns.Generation,RuleTarget.Corporate,new[]{"192.0.2.90"}),"Unverified written route was accepted.");f.AssertProtected();
                    Assert(!f.Runtime.LastFailure.Deferred,"Post-write uncertainty was softened.");
                });
                Test("crash journal startup gate reads only and Stop auto-loads own scope", delegate
                {
                    Fixture f = new Fixture(); f.Start(); f.Dns.Running = false;
                    DualNetworkRuntime restarted = new DualNetworkRuntime(f.Platform, f.Journal, new FakeDns { Calls = f.Platform.Calls });
                    int captures = f.Platform.Captures, writes = f.Platform.Writes, saves = f.Journal.Saves;
                    Assert(restarted.HasPending && f.Platform.Captures == captures && f.Platform.Writes == writes && f.Journal.Saves == saves, "Pending gate changed network.");
                    Assert(restarted.Stop(true).Success && !restarted.HasPending && f.Journal.Stored == null && f.Platform.Values.Count == 2, "Crash cleanup failed.");
                });
                Test("different user machine or interface scope cannot recover journal", delegate
                {
                    Fixture f = new Fixture(); f.Start(); f.Platform.Identity = "another-machine-user";
                    DualNetworkRuntime other = new DualNetworkRuntime(f.Platform, f.Journal, new FakeDns { Calls = f.Platform.Calls });
                    int writes = f.Platform.Writes, removes = f.Platform.Removes;
                    Assert(!other.Recover(f.Request, true, true).Success && !other.Stop(true).Success && f.Platform.Writes == writes && f.Platform.Removes == removes, "Foreign scope recovered.");
                });
                Test("journal read failures do not look like successful empty cleanup", delegate
                {
                    Fixture f = new Fixture(); f.Journal.FailLoad = true; Assert(f.Runtime.HasPending && !f.Runtime.Stop(true).Success && f.Platform.Writes == 0, "Unreadable journal ignored.");
                });
                Test("journal delete failure keeps retryable restored state", delegate
                {
                    Fixture f = new Fixture(); f.Start(); f.Journal.FailDelete = true; Assert(!f.Runtime.Stop(true).Success && f.Runtime.HasPending, "Delete failure ignored.");
                    int writes = f.Platform.Writes, removes = f.Platform.Removes; f.Journal.FailDelete = false;
                    Assert(f.Runtime.Stop(true).Success && f.Platform.Writes == writes && f.Platform.Removes == removes, "Restored resources changed again."); f.AssertClean();
                });
                Test("platform read failure retains unresolved journal without mutation", delegate
                {
                    Fixture f = new Fixture(); f.Start(); f.Platform.FailRead = true; int writes = f.Platform.Writes, removes = f.Platform.Removes;
                    Assert(!f.Runtime.Stop(true).Success && f.Dns.Running && f.Platform.Writes == writes && f.Platform.Removes == removes, "Read failure treated as absent.");
                    f.Platform.FailRead = false; Assert(f.Runtime.Stop(true).Success, "Read retry failed."); f.AssertClean();
                });
                Test("armed admission rejection preserves every prepared resource without writes", delegate
                {
                    Fixture f = new Fixture(); Assert(f.Runtime.Arm(f.Request, true).Success, "Arm failed.");
                    f.Platform.Snapshot.CorporateAdmissionVerified = false;
                    int writes = f.Platform.Writes, removes = f.Platform.Removes, saves = f.Journal.Saves;
                    Assert(!f.Runtime.Start(f.Request, true).Success && f.Runtime.IsArmed && !f.Runtime.HasPending, "Rejected start damaged preparation.");
                    Assert(f.Platform.Writes == writes && f.Platform.Removes == removes && f.Journal.Saves == saves && f.Dns.Pauses == 0, "Read-only rejection changed state.");
                });
                Test("fresh start without admission has zero writes", delegate
                {
                    Fixture f = new Fixture(); f.Platform.Snapshot.CorporateAdmissionVerified = false;
                    Assert(!f.Runtime.Start(f.Request, true).Success && f.Platform.Writes == 0 && f.Journal.Saves == 0, "Admission checked after mutation.");
                });
                Test("different request cannot tear down existing armed protection", delegate
                {
                    Fixture f = new Fixture(); Assert(f.Runtime.Arm(f.Request, true).Success, "Arm failed.");
                    int writes = f.Platform.Writes, saves = f.Journal.Saves;
                    f.Request.Rules.Add(new ModeRule { Pattern = "new.example.test", Target = RuleTarget.Corporate });
                    Assert(!f.Runtime.Arm(f.Request, true).Success && f.Runtime.IsArmed && f.Platform.Writes == writes && f.Journal.Saves == saves, "Different request tore down protection.");
                });
                Test("unapproved dual-up stop does not pause DNS or change resources", delegate
                {
                    Fixture f = new Fixture(); f.Start(); int writes = f.Platform.Writes, removes = f.Platform.Removes, saves = f.Journal.Saves, pauses = f.Dns.Pauses;
                    DualOperationResult result = f.Runtime.Stop();
                    Assert(!result.Success && result.State == "EgressConfirmationRequired", "Missing egress confirmation accepted.");
                    Assert(f.Runtime.IsActive && f.Dns.Running && f.Runtime.State == "Running" && f.Platform.Writes == writes && f.Platform.Removes == removes && f.Journal.Saves == saves && f.Dns.Pauses == pauses, "Rejected stop mutated state.");
                    Assert(f.Runtime.Stop(true).Success, "Explicit stop failed."); f.AssertClean();
                });
                Test("crash recovery does not restore unsafe baseline without separate approval", delegate
                {
                    Fixture f = new Fixture(); f.Start(); f.Dns.Running = false;
                    DualNetworkRuntime recovered = new DualNetworkRuntime(f.Platform, f.Journal, f.Dns);
                    int writes = f.Platform.Writes, saves = f.Journal.Saves, pauses = f.Dns.Pauses;
                    DualOperationResult result = recovered.Recover(f.Request, true);
                    Assert(!result.Success && result.State == "EgressConfirmationRequired" && f.Platform.Writes == writes && f.Journal.Saves == saves && f.Dns.Pauses == pauses, "Recovery changed egress before consent.");
                    Assert(recovered.Recover(f.Request, true, true).Success && f.Journal.Stored == null, "Approved recovery failed.");
                });
                Test("unplugged company interface allows ordinary full cleanup", delegate
                {
                    Fixture f = new Fixture(); f.Start(); f.Platform.Snapshot.Adapters[0].IsUp = false;
                    Assert(f.Runtime.Stop().Success, "Confirmed disconnected company path blocked cleanup."); f.AssertClean();
                });
                Test("missing interface or failed snapshot cannot grant egress approval", delegate
                {
                    foreach (bool throwCapture in new[] { false, true })
                    {
                        Fixture f = new Fixture(); f.Start(); int writes = f.Platform.Writes, pauses = f.Dns.Pauses, saves = f.Journal.Saves;
                        if (throwCapture) f.Platform.CaptureOverride = delegate { throw new Exception("fixture snapshot failure"); };
                        else f.Platform.Snapshot.Adapters.RemoveAt(0);
                        Assert(!f.Runtime.Stop().Success && f.Runtime.IsActive && f.Platform.Writes == writes && f.Dns.Pauses == pauses && f.Journal.Saves == saves, "Unknown topology triggered cleanup.");
                    }
                });
                Test("explicit mode three Start resumes protected metrics on warm and cold owners", delegate
                {
                    foreach (bool cold in new[] { false, true })
                    {
                        Fixture f = ProtectedModeThree();
                        string owner = f.Journal.Stored.Owner;
                        long generation = f.Journal.Stored.Generation;
                        if (cold) f.Runtime = new DualNetworkRuntime(f.Platform, f.Journal, f.Dns, delegate { return f.Now; });
                        int oldMetricWrites = f.Platform.Calls.Count(c => c == "write:Metric4");
                        Assert(f.Runtime.HasPending && !f.Runtime.Start(f.Request, false).Success && !f.Runtime.Arm(f.Request, true).Success,
                            "pending protection accepted automatic Start/Arm");
                        f.Start();
                        Assert(!f.Runtime.HasPending && f.Runtime.IsActive && f.Journal.Stored.Owner == owner &&
                            f.Journal.Stored.Generation > generation && f.Journal.Stored.Phase == "Running", "transaction not resumed");
                        Assert(f.Platform.Calls.Count(c => c == "write:Metric4") == oldMetricWrites, "retry rewrote or restored egress");
                        Assert(f.Journal.Stored.Entries.GroupBy(e => Key(e.Resource)).All(g => g.Count() == 1), "retry duplicated a journal key");
                        int saves = f.Journal.Saves, writes = f.Platform.Writes;
                        f.Start();
                        Assert(f.Journal.Saves == saves && f.Platform.Writes == writes, "running Start was not idempotent");
                        Assert(f.Runtime.Stop(true).Success, "original baselines could not be recovered");
                        f.AssertClean();
                        Assert(f.Platform.Values[Key(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = Corp })] == "automatic=1;metric=25" &&
                            f.Platform.Values[Key(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = Internet })] == "automatic=1;metric=40",
                            "original metric baselines lost");
                    }
                });
                Test("mode three protected retry rejects conflicting policy, resources and evidence without writes", delegate
                {
                    foreach (string conflict in new[] { "tunnel", "admission", "reachability", "permission", "default", "ipv6", "thirdPartyDns", "residual", "partial", "foreignOwner", "metric", "role", "journalRace", "saveFailure" })
                    {
                        Fixture f = ProtectedModeThree();
                        f.Runtime = new DualNetworkRuntime(f.Platform, f.Journal, f.Dns, delegate { return f.Now; });
                        switch (conflict)
                        {
                            case "tunnel": f.Platform.Snapshot.TunnelActive = true; break;
                            case "admission": f.Platform.Snapshot.CorporateAdmissionVerified = false; break;
                            case "reachability": f.Platform.Snapshot.InternetReachabilityVerified = false; break;
                            case "permission": f.Platform.Snapshot.DualConnectionAllowed = false; break;
                            case "default": f.Platform.Snapshot.ExternalDefaultPreferred = false; break;
                            case "ipv6": f.Platform.Snapshot.Adapters[0].HasIpv6Default = true; break;
                            case "thirdPartyDns": f.Platform.Snapshot.OtherDnsPolicyPresent = true; break;
                            case "residual":
                                DualJournalEntry old = f.Journal.Stored.Entries.First(e => e.Resource.Kind == DualResourceKind.Route4);
                                f.Platform.Values[Key(old.Resource)] = old.After; break;
                            case "partial": f.Journal.Stored.Entries.First(e => e.Resource.Kind == DualResourceKind.Nrpt).Restored = false; break;
                            case "foreignOwner": f.Journal.Stored.Scope += "-foreign"; break;
                            case "metric": f.Platform.Values[Key(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = Internet })] = "automatic=0;metric=8"; break;
                            case "role": f.Platform.Snapshot.Adapters[0].Kind = LinkKind.Ethernet; break;
                            case "journalRace": f.Platform.BeforeRead = delegate { f.Journal.Stored.Generation++; f.Platform.BeforeRead = null; }; break;
                            case "saveFailure": f.Journal.FailSaveAt = f.Journal.Saves + 1; break;
                        }
                        string fingerprint = DualJournalFingerprint.Compute(f.Journal.Stored);
                        int writes = f.Platform.Writes, removes = f.Platform.Removes, starts = f.Dns.Starts;
                        Assert(!f.Runtime.Start(f.Request, true).Success && f.Runtime.HasPending && !f.Dns.Running, "accepted " + conflict);
                        Assert(f.Platform.Writes == writes && f.Platform.Removes == removes && f.Dns.Starts == starts, "changed network for " + conflict);
                        Assert(conflict == "journalRace" || DualJournalFingerprint.Compute(f.Journal.Stored) == fingerprint, "lost original record for " + conflict);
                    }
                });
                Test("failed resumed DNS start can retry again without accumulating stale resource keys", delegate
                {
                    Fixture f = ProtectedModeThree();
                    int oldMetricWrites = f.Platform.Calls.Count(c => c == "write:Metric4");
                    f.Dns.FailStart = true;
                    Assert(!f.Runtime.Start(f.Request, true).Success, "failed resumed DNS accepted"); f.AssertProtected();
                    f.Dns.FailStart = false; f.Start();
                    Assert(f.Platform.Calls.Count(c => c == "write:Metric4") == oldMetricWrites, "failure touched egress");
                    Assert(f.Runtime.Stop(true).Success, "failed retry left unrecoverable journal"); f.AssertClean();
                });
                Test("failed DNS start retains protection through repeated validation and restart", delegate
                {
                    Fixture f = new Fixture(); f.Dns.FailStart = true;
                    Assert(!f.Runtime.Start(f.Request, true).Success, "Injected DNS start accepted."); f.AssertProtected();
                    int writes = f.Platform.Writes, removes = f.Platform.Removes, saves = f.Journal.Saves;
                    for (int i = 0; i < 5; i++) Assert(!f.Runtime.ValidateCurrent().Success, "Pending reported running.");
                    Assert(!f.Runtime.Start(f.Request, true).Success && !f.Runtime.Arm(f.Request, true).Success, "Pending silently restarted.");
                    DualNetworkRuntime recovered = new DualNetworkRuntime(f.Platform, f.Journal, f.Dns);
                    Assert(recovered.HasPending && !recovered.Stop().Success, "Restart silently dropped protection.");
                    Assert(f.Platform.Writes == writes && f.Platform.Removes == removes && f.Journal.Saves == saves, "Protected idle mutated itself.");
                    Assert(recovered.Stop(true).Success, "Explicit protected cleanup failed.");
                });
                Test("partial metric write never claims complete internet protection", delegate
                {
                    Fixture f = new Fixture(); int count = 0;
                    f.Platform.BeforeWrite = delegate { if (++count == 2) throw new Exception("fixture company metric failure"); };
                    Assert(!f.Runtime.Start(f.Request, true).Success && f.Runtime.HasPending && f.Runtime.State == "CleanupPending", "Partial metrics claimed protected.");
                    Assert(f.Platform.Writes == 2 && f.Platform.Values[Key(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = Internet })] == "automatic=0;metric=5", "Partial failure restored external priority.");
                    f.Platform.BeforeWrite = null; Assert(f.Runtime.Stop(true).Success, "Partial cleanup failed."); f.AssertClean();
                });
                Test("unverified external default or external metric edit never reports protection", delegate
                {
                    foreach (bool edited in new[] { false, true })
                    {
                        Fixture f = new Fixture(); f.Start(); int writes = f.Platform.Writes;
                        if (edited) f.Platform.Values[Key(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = Internet })] = "automatic=0;metric=88";
                        else f.Platform.Snapshot.ExternalDefaultPreferred = false;
                        f.Platform.Snapshot.CorporateAdmissionVerified = false;
                        Assert(!f.Runtime.ValidateCurrent().Success && f.Runtime.HasPending && f.Runtime.State == "CleanupPending" && f.Platform.Writes == writes, "Unknown egress overwritten or claimed protected.");
                    }
                });
                Test("company reconnection during cleanup cannot silently restore default metrics", delegate
                {
                    Fixture f = new Fixture(); f.Start(); int writes = f.Platform.Writes, captures = 0;
                    f.Platform.CaptureOverride = delegate
                    {
                        f.Platform.Snapshot.Adapters[0].IsUp = ++captures > 1;
                        return f.Platform.Snapshot;
                    };
                    Assert(!f.Runtime.Stop().Success && f.Runtime.HasPending && f.Platform.Writes == writes, "Reconnected company path silently restored defaults.");
                    Assert(f.Journal.Stored != null && f.Journal.Stored.Entries.Any(e => !e.Restored && e.Resource.Kind == DualResourceKind.Metric4), "Metric ownership lost.");
                    Assert(f.Runtime.Stop(true).Success, "Explicit retry failed."); f.AssertClean();
                });
                Test("third active gateway prevents implicit egress restoration", delegate
                {
                    Fixture f = new Fixture(); f.Start(); f.Platform.Snapshot.Adapters[0].IsUp = false;
                    f.Platform.Snapshot.Adapters.Add(new AdapterSnapshot { Id = "dddddddd-dddd-dddd-dddd-dddddddddddd", IsUp = true, IsPhysical = true, Gateway = "203.0.113.1" });
                    int writes = f.Platform.Writes, pauses = f.Dns.Pauses;
                    Assert(!f.Runtime.Stop().Success && f.Platform.Writes == writes && f.Dns.Pauses == pauses, "Unknown third exit allowed metric restore.");
                });
                Test("new proxy tunnel foreign DNS or IPv6 cannot be called internet protected", delegate
                {
                    Action<NetworkSnapshot>[] changes = {
                        s => s.SystemProxyActive = true, s => s.TunnelActive = true,
                        s => s.OtherDnsPolicyPresent = true, s => s.Adapters[0].HasIpv6Default = true
                    };
                    foreach (Action<NetworkSnapshot> change in changes)
                    {
                        Fixture f = new Fixture(); f.Start(); int writes = f.Platform.Writes;
                        change(f.Platform.Snapshot);
                        Assert(!f.Runtime.ValidateCurrent().Success && f.Runtime.State == "CleanupPending" && f.Runtime.HasPending && f.Platform.Writes == writes, "Conflict reported a protected egress or changed priority.");
                    }
                });
                Test("read-only stale review permits absent paths and recomputed automatic policy", delegate
                {
                    Fixture f = StaleFixture(false); f.Platform.Snapshot.TunnelActive = true; f.Platform.Snapshot.Adapters[0].HasIpv6Default = true;
                    f.Platform.Values[Key(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = Corp })] = "automatic=1;metric=77";
                    string before = DualJournalFingerprint.Compute(f.Journal.Stored);
                    DualReconciliationReport report = f.Runtime.ReviewStaleSession();
                    Assert(report.CanArchive && report.HasJournal && report.Entries.Count == f.Journal.Stored.Entries.Count, report.Message);
                    Assert(report.Entries.Where(e => e.Resource.Kind == DualResourceKind.Metric4).All(e => e.Disposition == "AlreadyAtOriginalPolicy"), "Automatic policy was treated as a manual change.");
                    Assert(DualJournalFingerprint.Compute(f.Journal.Stored) == before && f.Runtime.State == "Idle" && f.Journal.Archives == 0, "Review modified state."); AssertNoReconciliationEffects(f);
                });
                Test("explicit archive preserves external metric and unlocks a new mode", delegate
                {
                    Fixture f = StaleFixture(false); f.Platform.Values[Key(new DualResource { Kind = DualResourceKind.Metric4, InterfaceId = Corp })] = "automatic=0;metric=19";
                    Assert(f.Runtime.HasPending, "Stale journal gate missing.");
                    Assert(!f.Runtime.ArchiveStaleSession(false).Success && f.Journal.Archives == 0, "Consent omitted.");
                    Assert(f.Runtime.ArchiveStaleSession(true).Success && f.Journal.Archives == 1 && f.Journal.Stored == null && !f.Runtime.HasPending, "Archive failed.");
                    Assert(f.Journal.Archived != null && f.Journal.Evidence.Entries.Any(e => e.Disposition == "ExternallyChangedPreserved"), "Original or evidence lost.");
                    Assert(f.Platform.Values.Values.Contains("automatic=0;metric=19"), "New external metric overwritten."); AssertNoReconciliationEffects(f);
                });
                Test("live armed active or DNS-running session cannot be archived", delegate
                {
                    foreach (int variant in new[] { 0, 1, 2 })
                    {
                        Fixture f = new Fixture();
                        if (variant == 0) Assert(f.Runtime.Arm(f.Request, true).Success, "fixture arm");
                        else if (variant == 1) f.Start(); else f.Dns.Running = true;
                        string state = f.Runtime.State; int writes = f.Platform.Writes, saves = f.Journal.Saves;
                        Assert(!f.Runtime.ReviewStaleSession().CanArchive && !f.Runtime.ArchiveStaleSession(true).Success && f.Journal.Archives == 0, "Live session abandoned.");
                        Assert(f.Runtime.State == state && f.Platform.Writes == writes && f.Journal.Saves == saves, "Rejected archive changed live state.");
                    }
                });
                Test("any existing route NRPT or owned metric prevents retirement", delegate
                {
                    foreach (int variant in new[] { 0, 1, 2, 3 })
                    {
                        Fixture f = StaleFixture(true);
                        DualJournalEntry e = f.Journal.Stored.Entries.First(x => x.Resource.Kind == (variant == 0 ? DualResourceKind.Metric4 : variant == 3 ? DualResourceKind.Nrpt : DualResourceKind.Route4));
                        f.Platform.Values[Key(e.Resource)] = variant == 2 ? "metric=99" : variant == 3 ? "server=192.0.2.53;owner=external" : e.After;
                        Assert(!f.Runtime.ReviewStaleSession().CanArchive && !f.Runtime.ArchiveStaleSession(true).Success && f.Journal.Stored != null && f.Journal.Archives == 0, "Present resource abandoned."); AssertNoReconciliationEffects(f);
                    }
                });
                Test("restored flags cannot hide a still-existing resource", delegate
                {
                    Fixture f = StaleFixture(false); DualJournalEntry e = f.Journal.Stored.Entries.First(x => x.Resource.Kind == DualResourceKind.Route4);
                    e.Restored = true; f.Platform.Values[Key(e.Resource)] = e.After;
                    Assert(!f.Runtime.ReviewStaleSession().CanArchive && !f.Runtime.ArchiveStaleSession(true).Success, "Restored flag trusted without readback."); AssertNoReconciliationEffects(f);
                });
                Test("missing duplicate or nonphysical role blocks stale archive", delegate
                {
                    foreach (int variant in new[] { 0, 1, 2 })
                    {
                        Fixture f = StaleFixture(false);
                        if (variant == 0) f.Platform.Snapshot.Adapters.RemoveAt(1);
                        else if (variant == 1) f.Platform.Snapshot.Adapters.Add(f.Platform.Snapshot.Adapters[1]);
                        else f.Platform.Snapshot.Adapters[1].IsPhysical = false;
                        Assert(!f.Runtime.ArchiveStaleSession(true).Success && f.Journal.Stored != null && f.Journal.Archives == 0, "Unverified role accepted."); AssertNoReconciliationEffects(f);
                    }
                });
                Test("read errors invalid metric and foreign scope retain journal", delegate
                {
                    foreach (int variant in new[] { 0, 1, 2, 3, 4 })
                    {
                        Fixture f = StaleFixture(false);
                        if (variant == 0) f.Platform.FailRead = true;
                        else if (variant == 1) f.Journal.FailLoad = true;
                        else if (variant == 2) f.Platform.Values[Key(f.Journal.Stored.Entries.First(x => x.Resource.Kind == DualResourceKind.Metric4).Resource)] = "automatic=1;metric=0";
                        else if (variant == 3) f.Platform.Identity = "another-user";
                        else f.Platform.CaptureOverride = delegate { throw new IOException("capture failure"); };
                        Assert(!f.Runtime.ArchiveStaleSession(true).Success && f.Journal.Stored != null && f.Journal.Archives == 0, "Uncertain record retired."); AssertNoReconciliationEffects(f);
                    }
                });
                Test("resource changes between complete reads prevent archive", delegate
                {
                    Fixture f = StaleFixture(false); int count = f.Journal.Stored.Entries.Count;
                    DualJournalEntry metric = f.Journal.Stored.Entries.First(x => x.Resource.Kind == DualResourceKind.Metric4);
                    f.Platform.BeforeRead = delegate { if (f.Platform.Reads == count + 1) f.Platform.Values[Key(metric.Resource)] = "automatic=0;metric=22"; };
                    Assert(!f.Runtime.ArchiveStaleSession(true).Success && f.Journal.Stored != null && f.Journal.Archives == 0, "Changed observations accepted."); AssertNoReconciliationEffects(f);
                });
                Test("whole-journal CAS rejects concurrent phase or entry edit", delegate
                {
                    foreach (int variant in new[] { 0, 1 })
                    {
                        Fixture f = StaleFixture(false); f.Journal.BeforeArchive = delegate { if (variant == 0) f.Journal.Stored.Phase = "new-phase"; else f.Journal.Stored.Entries[0].Applied = false; };
                        Assert(!f.Runtime.ArchiveStaleSession(true).Success && f.Journal.Stored != null && f.Journal.Archives == 0, "Concurrent journal edit ignored."); AssertNoReconciliationEffects(f);
                    }
                });
                Test("archive failure retains original and a retry rereads everything", delegate
                {
                    Fixture f = StaleFixture(false); f.Journal.FailArchive = true; string before = DualJournalFingerprint.Compute(f.Journal.Stored);
                    Assert(!f.Runtime.ArchiveStaleSession(true).Success && DualJournalFingerprint.Compute(f.Journal.Stored) == before, "Failed archive lost original.");
                    f.Journal.FailArchive = false; int reads = f.Platform.Reads;
                    Assert(f.Runtime.ArchiveStaleSession(true).Success && f.Platform.Reads >= reads + 2 * f.Journal.Archived.Entries.Count, "Retry reused stale review."); AssertNoReconciliationEffects(f);
                });
                Test("no-journal review is inert and archive needs administrator", delegate
                {
                    Fixture f = new Fixture(); Assert(!f.Runtime.ReviewStaleSession().HasJournal && f.Platform.Captures == 0, "Empty review performed network queries.");
                    f.Platform.Administrator = false; Assert(!f.Runtime.ArchiveStaleSession(true).Success && f.Runtime.State == "Idle", "Nonadministrator archive attempted."); AssertNoReconciliationEffects(f);
                });
                Test("mode three prepares external wire then company wifi and DNS with guarded stop", delegate
                {
                    Fixture f = new Fixture(); f.Request.Mode = WorkMode.WifiCorporateWiredInternet;
                    f.Platform.Snapshot.Adapters[0].Kind = LinkKind.Wifi; f.Platform.Snapshot.Adapters[0].IsUp = false;
                    f.Platform.Snapshot.Adapters[1].Kind = LinkKind.Ethernet;
                    f.Platform.Snapshot.CorporateAdmissionVerified = false;
                    Assert(f.Runtime.Arm(f.Request, true).Success && f.Runtime.State == "WaitingForCorporate", "External-only arm failed.");
                    Assert(f.Journal.Stored.Entries.Count == 2 && f.Dns.Starts == 0, "Disconnected company got paths or DNS.");
                    f.Platform.Snapshot.Adapters[0].IsUp = true;
                    Assert(f.Runtime.Arm(f.Request, true).Success && f.Runtime.State == "Armed" && f.Journal.Stored.Entries.Any(e => e.Resource.Kind == DualResourceKind.Route4) && f.Dns.Starts == 0, "Company preparation failed.");
                    f.Platform.Snapshot.CorporateAdmissionVerified = true; f.Start();
                    Assert(f.Dns.Callback(f.Dns.Generation, RuleTarget.Corporate, new[] { "10.20.30.40" }), "Mode three DNS route callback failed.");
                    Assert(f.Journal.Stored.Entries.Any(e => e.Resource.Kind == DualResourceKind.Route4 && e.Resource.Destination == "10.20.30.40/32" && e.Resource.InterfaceId == Corp), "Company answer routed to wrong role.");
                    int writes = f.Platform.Writes, pauses = f.Dns.Pauses;
                    Assert(!f.Runtime.Stop().Success && f.Platform.Writes == writes && f.Dns.Pauses == pauses, "Unapproved stop changed mode-three egress.");
                    Assert(f.Runtime.Stop(true).Success, "Approved stop failed."); f.AssertClean();
                });
                Test("mode three failure retains wired priority and late answers cannot write", delegate
                {
                    Fixture f = new Fixture(); f.Request.Mode = WorkMode.WifiCorporateWiredInternet;
                    f.Platform.Snapshot.Adapters[0].Kind = LinkKind.Wifi; f.Platform.Snapshot.Adapters[1].Kind = LinkKind.Ethernet;
                    f.Start(); var callback = f.Dns.Callback; long epoch = f.Dns.Generation;
                    f.Platform.Snapshot.CorporateAdmissionVerified = false;
                    Assert(!f.Runtime.ValidateCurrent().Success, "Admission change ignored."); f.AssertProtected();
                    int writes = f.Platform.Writes;
                    Assert(!callback(epoch, RuleTarget.Corporate, new[] { "10.20.30.41" }) && f.Platform.Writes == writes, "Late callback changed retained wired egress.");
                });
                Test("file archive atomically preserves original bytes and durable evidence", delegate
                {
                    WithJournalDirectory(delegate(string directory)
                    {
                        Fixture f = StaleFixture(false); string path = Path.Combine(directory, "dual-session.json");
                        DualFileJournal journal = new DualFileJournal(path); journal.Save(f.Journal.Stored);
                        byte[] original = File.ReadAllBytes(path);
                        DualNetworkRuntime runtime = new DualNetworkRuntime(f.Platform, journal, f.Dns, delegate { return f.Now; });
                        DualOperationResult result = runtime.ArchiveStaleSession(true); Assert(result.Success, result.Message);
                        Assert(!File.Exists(path) && !runtime.HasPending && runtime.State == "Idle", "Active journal not retired.");
                        string[] archives = Directory.GetDirectories(Path.Combine(directory, "journal-archive")); Assert(archives.Length == 1, "Archive count wrong.");
                        Assert(original.SequenceEqual(File.ReadAllBytes(Path.Combine(archives[0], "dual-session.json"))), "Original bytes changed.");
                        var evidence = new JavaScriptSerializer().Deserialize<DualReconciliationReport>(File.ReadAllText(Path.Combine(archives[0], "reconciliation.json")));
                        Assert(evidence.CanArchive && evidence.JournalFingerprint == DualJournalFingerprint.Compute(f.Journal.Stored) && evidence.Entries.Count == f.Journal.Stored.Entries.Count, "Durable evidence incomplete."); AssertNoReconciliationEffects(f);
                    });
                });
                Test("file archive CAS refuses modified original and retains it", delegate
                {
                    WithJournalDirectory(delegate(string directory)
                    {
                        Fixture f = StaleFixture(false); string path = Path.Combine(directory, "dual-session.json");
                        DualReconciliationReport report = f.Runtime.ReviewStaleSession();
                        DualFileJournal journal = new DualFileJournal(path); journal.Save(f.Journal.Stored);
                        DualJournalDocument changed = journal.Load(); changed.Entries[0].Applied = !changed.Entries[0].Applied; journal.Save(changed);
                        byte[] bytes = File.ReadAllBytes(path); bool failed = false;
                        try { journal.ArchiveVerified(f.Journal.Stored, report); } catch { failed = true; }
                        Assert(failed && File.Exists(path) && bytes.SequenceEqual(File.ReadAllBytes(path)), "CAS changed or lost newer original."); AssertNoReconciliationEffects(f);
                    });
                });
                Test("file archive evidence failure or locked original never deletes journal", delegate
                {
                    foreach (bool lockOriginal in new[] { false, true }) WithJournalDirectory(delegate(string directory)
                    {
                        Fixture f = StaleFixture(false); string path = Path.Combine(directory, "dual-session.json");
                        DualFileJournal journal = new DualFileJournal(path); journal.Save(f.Journal.Stored); byte[] bytes = File.ReadAllBytes(path);
                        DualReconciliationReport report = f.Runtime.ReviewStaleSession(); bool failed = false;
                        if (lockOriginal)
                        {
                            using (FileStream busy = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                                try { journal.ArchiveVerified(f.Journal.Stored, report); } catch { failed = true; }
                        }
                        else
                        {
                            File.WriteAllText(Path.Combine(directory, "journal-archive"), "fixture directory blocker");
                            try { journal.ArchiveVerified(f.Journal.Stored, report); } catch { failed = true; }
                        }
                        Assert(failed && File.Exists(path) && bytes.SequenceEqual(File.ReadAllBytes(path)), "Failed archive removed original."); AssertNoReconciliationEffects(f);
                    });
                });
                Test("mode three permits verified external IPv6 without changing its routes", delegate
                {
                    Fixture f = new Fixture(); f.Request.Mode = WorkMode.WifiCorporateWiredInternet;
                    f.Platform.Snapshot.Adapters[0].Kind = LinkKind.Wifi; f.Platform.Snapshot.Adapters[1].Kind = LinkKind.Ethernet;
                    f.Platform.Snapshot.Adapters[1].HasIpv6Default = true; f.Platform.Snapshot.Adapters[1].Ipv6RouteSignature = "::/0|fe80::1|256|25";
                    f.Start(); Assert(f.Runtime.IsActive && f.Platform.Snapshot.Adapters[1].Ipv6RouteSignature == "::/0|fe80::1|256|25", "External IPv6 start failed or altered route.");
                    Assert(f.Runtime.Stop(true).Success, "IPv6 mode three cleanup failed."); f.AssertClean();
                });
                Test("mode three IPv6 gateway change stops DNS but keeps external metric", delegate
                {
                    Fixture f = new Fixture(); f.Request.Mode = WorkMode.WifiCorporateWiredInternet;
                    f.Platform.Snapshot.Adapters[0].Kind = LinkKind.Wifi; f.Platform.Snapshot.Adapters[1].Kind = LinkKind.Ethernet;
                    f.Platform.Snapshot.Adapters[1].HasIpv6Default = true; f.Platform.Snapshot.Adapters[1].Ipv6RouteSignature = "::/0|fe80::1|256|25";
                    f.Start(); int writes = f.Platform.Writes;
                    f.Platform.Snapshot.Adapters[1].Ipv6RouteSignature = "::/0|fe80::2|256|25";
                    Assert(!f.Runtime.ValidateCurrent().Success && f.Platform.Writes == writes, "IPv6 drift ignored or default metric restored."); f.AssertProtected();
                });
                Test("mode three company or third IPv6 route blocks all writes", delegate
                {
                    foreach (bool third in new[] { false, true })
                    {
                        Fixture f = new Fixture(); f.Request.Mode = WorkMode.WifiCorporateWiredInternet;
                        f.Platform.Snapshot.Adapters[0].Kind = LinkKind.Wifi; f.Platform.Snapshot.Adapters[1].Kind = LinkKind.Ethernet;
                        AdapterSnapshot changed = third ? new AdapterSnapshot { Id = "33333333-3333-3333-3333-333333333333", Kind = LinkKind.Ethernet, IsPhysical = true, IsUp = true } : f.Platform.Snapshot.Adapters[0];
                        changed.HasIpv6Default = true; changed.Ipv6RouteSignature = "::/0|fe80::1|256|25";
                        if (third) f.Platform.Snapshot.Adapters.Add(changed);
                        Assert(!f.Runtime.Arm(f.Request, true).Success && !f.Runtime.Start(f.Request, true).Success && f.Platform.Writes == 0 && f.Journal.Saves == 0, "Unsafe IPv6 topology mutated network.");
                    }
                });
                Console.WriteLine("DUAL_RUNTIME_TESTS_PASSED=" + passed); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine("FAIL " + error); return 1; }
        }
    }
}
