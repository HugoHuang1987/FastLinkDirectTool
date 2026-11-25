using System;
using System.Collections.Generic;
using System.Net;
using System.IO;
using System.Text;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace FastLinkDirectTool.Tests
{
    internal static class NrptRegressionTests
    {
        private const string RemovedMarker = "__FASTLINK_NRPT_REMOVED__";
        private static int passed;

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void Test(string name, Action body)
        {
            body();
            passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void Reject(ProcessResult result)
        {
            bool rejected = false;
            try { NrptManager.VerifyCommandResult(result, RemovedMarker, "test cleanup"); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "An unverified command result was accepted.");
        }

        private static ProcessResult Result(int code, bool timeout, string output)
        {
            return new ProcessResult { ExitCode = code, TimedOut = timeout, Output = output, Error = "" };
        }

        private static PhysicalNetworkInfo RouteTestNetwork()
        {
            return new PhysicalNetworkInfo
            {
                InterfaceIndex = 123, InterfaceName = "fixture-only",
                LocalAddress = IPAddress.Parse("192.0.2.10"), Gateway = IPAddress.Parse("192.0.2.1"),
                DnsServer = IPAddress.Parse("192.0.2.53")
            };
        }

        private static TrackedRoute RouteTestEntry()
        {
            return new TrackedRoute
            {
                Address = "203.0.113.10", Netmask = "255.255.255.255", InterfaceIndex = 123,
                Gateway = "192.0.2.1", LocalAddress = "192.0.2.10", Infrastructure = false,
                ExpiresUtcTicks = DateTime.UtcNow.Ticks
            };
        }

        private static void TestCleanupRoutePause()
        {
            Test("cleanup pauses writes then removes NRPT before bypass DNS and routes", delegate
            {
                List<string> calls = new List<string>();
                SafeCleanup.Run(delegate { calls.Add("pause"); }, delegate { calls.Add("nrpt"); },
                    delegate { calls.Add("bypass"); }, delegate { calls.Add("dns"); }, delegate { calls.Add("routes"); });
                Assert(string.Join(",", calls.ToArray()) == "pause,nrpt,bypass,dns,routes", "Five-phase production cleanup order changed.");
            });
            Test("failed NRPT cleanup preserves bypass and DNS but blocks DNS route callbacks", delegate
            {
                int commands = 0, saves = 0;
                RouteManager manager = new RouteManager(new RouteState(),
                    delegate(string file, string arguments, int timeout) { commands++; return Result(0, false, ""); },
                    delegate { saves++; });
                manager.SetNetwork(RouteTestNetwork());
                List<string> calls = new List<string>();
                Exception failure = new InvalidOperationException("fixture NRPT timeout");
                Exception observed = null;
                try
                {
                    SafeCleanup.Run(delegate { manager.SetNetwork(null); calls.Add("pause"); },
                        delegate { calls.Add("nrpt"); throw failure; }, delegate { calls.Add("bypass"); },
                        delegate { calls.Add("dns"); }, delegate { calls.Add("routes"); manager.RemoveAll(); });
                }
                catch (Exception error) { observed = error; }
                Assert(object.ReferenceEquals(failure, observed), "Original failure was lost.");
                Assert(string.Join(",", calls.ToArray()) == "pause,nrpt", "Unverified NRPT removal changed bypass or stopped DNS.");
                Assert(!manager.EnsureAddressRoute(IPAddress.Parse("203.0.113.20"), 300), "Late DNS answer resumed route writes.");
                Assert(commands == 0 && saves == 0, "Pending cleanup mutated route state.");
            });
            Test("failed bypass cleanup after verified NRPT retains DNS and tracked routes", delegate
            {
                List<string> calls = new List<string>();
                Exception failure = new InvalidOperationException("fixture bypass failure");
                Exception observed = null;
                try
                {
                    SafeCleanup.Run(delegate { calls.Add("pause"); }, delegate { calls.Add("nrpt"); },
                        delegate { calls.Add("bypass"); throw failure; }, delegate { calls.Add("dns"); }, delegate { calls.Add("routes"); });
                }
                catch (Exception error) { observed = error; }
                Assert(object.ReferenceEquals(failure, observed), "Original bypass failure was lost.");
                Assert(string.Join(",", calls.ToArray()) == "pause,nrpt,bypass", "DNS/routes stopped before both dependency cleanups succeeded.");
            });
            Test("paused route writer rejects all ensures and tracked TTL updates without persistence", delegate
            {
                RouteState state = new RouteState();
                TrackedRoute entry = RouteTestEntry(); state.Routes.Add(entry);
                long originalExpiry = entry.ExpiresUtcTicks;
                int commands = 0, saves = 0;
                RouteManager manager = new RouteManager(state,
                    delegate(string file, string arguments, int timeout) { commands++; return Result(0, false, ""); },
                    delegate { saves++; });
                manager.SetNetwork(RouteTestNetwork());
                manager.SetNetwork(null);
                Assert(!manager.EnsureAddressRoute(IPAddress.Parse(entry.Address), 3600), "Tracked TTL refresh was accepted while paused.");
                Assert(!manager.EnsureAddressRoute(IPAddress.Parse("203.0.113.20"), 300), "New DNS route accepted while paused.");
                Assert(!manager.EnsureDnsRoute(), "DNS infrastructure write accepted while paused.");
                Assert(!manager.EnsureRuleAddressRoute(IPAddress.Parse("203.0.113.30")), "Rule route accepted while paused.");
                Assert(!manager.EnsurePrivateNetworkRoutes(), "Private routes accepted while paused.");
                Assert(entry.ExpiresUtcTicks == originalExpiry && commands == 0 && saves == 0, "Pause changed tracked state or ran a command.");
                Assert((bool)manager.GetHealthDetails()["route_dns_writes_paused"], "Pause is not visible in diagnostics.");
            });
            Test("route circuit expiry cannot reopen a cleanup-paused route writer", delegate
            {
                int commands = 0;
                RouteManager manager = new RouteManager(new RouteState(),
                    delegate(string file, string arguments, int timeout) { commands++; return Result(0, false, ""); }, delegate { });
                manager.SetNetwork(RouteTestNetwork()); manager.SetNetwork(null);
                FieldInfo until = typeof(RouteManager).GetField("routeMutationBlockedUntilUtcTicks", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(until != null, "Circuit test field unavailable.");
                until.SetValue(manager, DateTime.UtcNow.AddMinutes(1).Ticks);
                Assert(!manager.EnsureAddressRoute(IPAddress.Parse("203.0.113.20"), 300), "Open circuit allowed route write.");
                until.SetValue(manager, DateTime.UtcNow.AddMinutes(-1).Ticks);
                Assert(!manager.EnsureAddressRoute(IPAddress.Parse("203.0.113.20"), 300), "Expired circuit resumed a paused writer.");
                Assert(commands == 0, "Pause depended on the temporary circuit timeout.");
            });
            Test("explicit tracked-route cleanup remains possible while DNS writes are paused", delegate
            {
                RouteState state = new RouteState(); state.Routes.Add(RouteTestEntry());
                List<string> commands = new List<string>();
                RouteManager manager = new RouteManager(state,
                    delegate(string file, string arguments, int timeout) { commands.Add(arguments); return Result(0, false, ""); }, delegate { });
                manager.SetNetwork(RouteTestNetwork()); manager.SetNetwork(null);
                manager.RemoveAll();
                Assert(state.Routes.Count == 0 && commands.Count == 2, "Explicit cleanup was blocked by the DNS pause.");
                Assert(commands[0].StartsWith("DELETE ") && commands[1].StartsWith("PRINT -4 "), "Cleanup failed to verify deletion.");
            });
            Test("only an explicit new route network resumes additions after pause", delegate
            {
                int commands = 0, saves = 0;
                RouteManager manager = new RouteManager(new RouteState(),
                    delegate(string file, string arguments, int timeout) { commands++; return Result(0, false, ""); }, delegate { saves++; });
                manager.SetNetwork(RouteTestNetwork()); manager.SetNetwork(null);
                Assert(!manager.EnsureAddressRoute(IPAddress.Parse("203.0.113.20"), 300), "Paused writer resumed itself.");
                manager.SetNetwork(RouteTestNetwork());
                Assert(manager.EnsureAddressRoute(IPAddress.Parse("203.0.113.20"), 300), "Explicit enable failed to resume writer.");
                Assert(commands == 1 && saves == 1 && !(bool)manager.GetHealthDetails()["route_dns_writes_paused"], "Explicit resume did not add exactly one tracked route.");
            });
            Test("DNS query started before pause cannot add its delayed answer after pause", delegate
            {
                int commands = 0;
                RouteManager manager = new RouteManager(new RouteState(),
                    delegate(string file, string arguments, int timeout) { Interlocked.Increment(ref commands); return Result(0, false, ""); }, delegate { });
                manager.SetNetwork(RouteTestNetwork());
                using (ManualResetEventSlim waiting = new ManualResetEventSlim(false))
                using (ManualResetEventSlim upstreamFinished = new ManualResetEventSlim(false))
                {
                    Task<bool> callback = Task.Factory.StartNew(delegate
                    {
                        waiting.Set();
                        if (!upstreamFinished.Wait(5000)) throw new Exception("Fixture upstream release timed out.");
                        return manager.EnsureAddressRoute(IPAddress.Parse("203.0.113.20"), 300);
                    });
                    try
                    {
                        Assert(waiting.Wait(3000), "Fixture DNS query did not start.");
                        manager.SetNetwork(null);
                    }
                    finally { upstreamFinished.Set(); }
                    Assert(callback.Wait(3000) && !callback.Result && commands == 0, "Delayed DNS answer escaped pause gate.");
                }
            });
            Test("pausing drains the in-flight route command before returning", delegate
            {
                int commands = 0;
                using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
                using (ManualResetEventSlim release = new ManualResetEventSlim(false))
                using (ManualResetEventSlim pauseStarted = new ManualResetEventSlim(false))
                using (ManualResetEventSlim pauseFinished = new ManualResetEventSlim(false))
                {
                    RouteManager manager = new RouteManager(new RouteState(),
                        delegate(string file, string arguments, int timeout)
                        {
                            Interlocked.Increment(ref commands); entered.Set();
                            if (!release.Wait(5000)) throw new Exception("Fixture command release timed out.");
                            return Result(0, false, "");
                        }, delegate { });
                    manager.SetNetwork(RouteTestNetwork());
                    Task<bool> ensure = Task.Factory.StartNew(delegate { return manager.EnsureAddressRoute(IPAddress.Parse("203.0.113.20"), 300); });
                    Task pause = null;
                    try
                    {
                        Assert(entered.Wait(3000), "Fixture route command did not start.");
                        pause = Task.Factory.StartNew(delegate { pauseStarted.Set(); manager.SetNetwork(null); pauseFinished.Set(); });
                        Assert(pauseStarted.Wait(3000), "Fixture pause did not start.");
                        Assert(!pauseFinished.Wait(100), "Pause returned while a route command was still executing.");
                    }
                    finally { release.Set(); }
                    Assert(ensure.Wait(3000) && ensure.Result && pause != null && pause.Wait(3000), "In-flight work did not drain.");
                    Assert(!manager.EnsureAddressRoute(IPAddress.Parse("203.0.113.21"), 300) && commands == 1,
                        "A route command started after pause returned.");
                }
            });
        }

        private static void CheckLongScript(int exitCode)
        {
            string marker = "__FASTLINK_HARMLESS_LONG_SCRIPT__";
            string script = "# " + new string('x', 20000) + "\r\n" +
                "Write-Output '" + marker + "'\r\n" +
                "Write-Output ('SCRIPT_PATH=' + $PSCommandPath)\r\n" +
                "$bytes=[IO.File]::ReadAllBytes($PSCommandPath)\r\n" +
                "if ($bytes[0] -ne 239 -or $bytes[1] -ne 187 -or $bytes[2] -ne 191) { exit 89 }\r\n" +
                "exit " + exitCode + "\r\n";
            Assert(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)).Length > 24000, "Script did not exceed the encoded command threshold.");
            ProcessResult result = ProcessRunner.RunPowerShell(script, 15000);
            Assert(!result.TimedOut && result.ExitCode == exitCode, "Long harmless script did not complete with the expected exit code.");
            Assert((result.Output ?? "").Contains(marker), "Long script did not execute.");
            string scriptPath = null;
            foreach (string line in (result.Output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (line.StartsWith("SCRIPT_PATH=")) scriptPath = line.Substring("SCRIPT_PATH=".Length);
            Assert(!string.IsNullOrWhiteSpace(scriptPath), "Long script did not use a temporary script file.");
            Assert(Path.GetFileName(scriptPath).StartsWith("FastLinkDirectTool-"), "Unexpected temporary script file name.");
            Assert(!File.Exists(scriptPath), "Temporary script was not cleaned after execution.");
        }

        public static int Main(string[] args)
        {
            // This entry point never initializes the application, DNS listener,
            // routes, UI, task scheduler, or credentials.
            if (args.Length == 1 && args[0] == "--print-remove-script")
            {
                Console.Write(NrptManager.BuildRemoveScript());
                return 0;
            }
            if (args.Length == 2 && args[0] == "--print-apply-script")
            {
                int count = int.Parse(args[1]);
                List<string> namespaces = new List<string>();
                for (int index = 0; index < count; index++) namespaces.Add("regression-" + index + ".invalid");
                Console.Write(NrptManager.BuildApplyScript(namespaces));
                return 0;
            }

            try
            {
                TestCleanupRoutePause();
                if (args.Length == 1 && args[0] == "--cleanup-pause-tests")
                {
                    Console.WriteLine("CLEANUP_ROUTE_PAUSE_TESTS_OK " + passed);
                    return 0;
                }
                Test("null command result rejected", delegate { Reject(null); });
                Test("timeout rejected even with success marker", delegate { Reject(Result(0, true, RemovedMarker)); });
                Test("nonzero exit rejected even with success marker", delegate { Reject(Result(1, false, RemovedMarker)); });
                Test("missing success marker rejected", delegate { Reject(Result(0, false, "ordinary command output")); });
                Test("null output rejected", delegate { Reject(Result(0, false, null)); });
                Test("marker substring rejected", delegate { Reject(Result(0, false, "not-success: " + RemovedMarker)); });
                Test("exact successful marker accepted", delegate
                {
                    NrptManager.VerifyCommandResult(Result(0, false, RemovedMarker), RemovedMarker, "test cleanup");
                });
                Test("success marker accepted as complete output line", delegate
                {
                    NrptManager.VerifyCommandResult(Result(0, false, "diagnostic\r\n " + RemovedMarker + " \r\n"), RemovedMarker, "test cleanup");
                });
                Test("cleanup removes DNS rules before stopping DNS before routes", delegate
                {
                    List<string> calls = new List<string>();
                    SafeCleanup.Run(delegate { calls.Add("rules"); }, delegate { calls.Add("dns"); }, delegate { calls.Add("routes"); });
                    Assert(string.Join(",", calls.ToArray()) == "rules,dns,routes", "Cleanup order was unsafe.");
                });
                Test("failed DNS rule cleanup preserves DNS and routes", delegate
                {
                    List<string> calls = new List<string>();
                    InvalidOperationException expected = new InvalidOperationException("simulated rule failure");
                    Exception observed = null;
                    try
                    {
                        SafeCleanup.Run(delegate { calls.Add("rules"); throw expected; }, delegate { calls.Add("dns"); }, delegate { calls.Add("routes"); });
                    }
                    catch (Exception ex) { observed = ex; }
                    Assert(object.ReferenceEquals(expected, observed), "The original cleanup failure was lost.");
                    Assert(string.Join(",", calls.ToArray()) == "rules", "DNS or routes changed after rule cleanup failed.");
                });
                Test("failed DNS stop does not remove routes", delegate
                {
                    List<string> calls = new List<string>();
                    InvalidOperationException expected = new InvalidOperationException("simulated DNS stop failure");
                    Exception observed = null;
                    try
                    {
                        SafeCleanup.Run(delegate { calls.Add("rules"); }, delegate { calls.Add("dns"); throw expected; }, delegate { calls.Add("routes"); });
                    }
                    catch (Exception ex) { observed = ex; }
                    Assert(object.ReferenceEquals(expected, observed), "The original DNS stop failure was lost.");
                    Assert(string.Join(",", calls.ToArray()) == "rules,dns", "Routes changed after DNS stop failed.");
                });
                Test("fake-IP range includes both 198.18 and 198.19 boundaries", delegate
                {
                    foreach (string value in new[] { "198.18.0.0", "198.18.0.1", "198.18.255.255", "198.19.0.0", "198.19.255.255" })
                        Assert(RouteManager.IsFakeIp(IPAddress.Parse(value)), "Fake IP was not rejected: " + value);
                });
                Test("fake-IP detector excludes neighboring IPv4 ranges", delegate
                {
                    foreach (string value in new[] { "198.17.255.255", "198.20.0.0", "197.18.0.1", "199.18.0.1" })
                        Assert(!RouteManager.IsFakeIp(IPAddress.Parse(value)), "Unrelated IPv4 range was rejected: " + value);
                });
                Test("fake-IP detector preserves physical DNS and ordinary IPs", delegate
                {
                    foreach (string value in new[] { "10.88.2.53", "192.168.1.1", "172.16.1.1", "127.0.0.1", "8.8.8.8" })
                        Assert(!RouteManager.IsFakeIp(IPAddress.Parse(value)), "Ordinary address was classified as fake: " + value);
                });
                Test("fake-IP detector handles null and IPv6 without IPv4 indexing", delegate
                {
                    Assert(!RouteManager.IsFakeIp(null), "Null address was classified as fake.");
                    Assert(!RouteManager.IsFakeIp(IPAddress.IPv6Loopback), "IPv6 address was classified as IPv4 fake IP.");
                    Assert(!RouteManager.IsFakeIp(IPAddress.Parse("2001:db8::1")), "IPv6 documentation address was classified as fake.");
                });
                Test("long harmless PowerShell script uses BOM temporary file and cleans it", delegate { CheckLongScript(0); });
                Test("long failed PowerShell script still cleans its temporary file", delegate { CheckLongScript(7); });
                Test("automatic work blocked while cleanup is pending even if DNS is running", delegate
                {
                    Assert(!NetworkRefreshPolicy.CanWork(false, true, true), "Cleanup-pending state allowed automatic work.");
                    Assert(!NetworkRefreshPolicy.CanWork(true, true, false), "Busy state allowed concurrent work.");
                    Assert(!NetworkRefreshPolicy.CanWork(false, false, false), "Disabled state allowed work.");
                    Assert(NetworkRefreshPolicy.CanWork(false, true, false), "Healthy state was blocked.");
                });
                Test("network change waits for eight stable seconds", delegate
                {
                    NetworkRefreshPolicy policy = new NetworkRefreshPolicy();
                    DateTime now = DateTime.UtcNow;
                    Assert(!policy.Observe("old", "new", now), "First observation started refresh.");
                    Assert(!policy.Observe("old", "new", now.AddSeconds(7)), "Unstable change started refresh.");
                    Assert(policy.Observe("old", "new", now.AddSeconds(8)), "Stable change was not accepted.");
                });
                Test("flapping network restarts stability window", delegate
                {
                    NetworkRefreshPolicy policy = new NetworkRefreshPolicy();
                    DateTime now = DateTime.UtcNow;
                    policy.Observe("old", "wifi-a", now);
                    Assert(!policy.Observe("old", "wifi-b", now.AddSeconds(7)), "Changed candidate was accepted immediately.");
                    Assert(!policy.Observe("old", "wifi-b", now.AddSeconds(14)), "Second candidate did not wait.");
                    Assert(policy.Observe("old", "wifi-b", now.AddSeconds(15)), "Stable second candidate was not accepted.");
                });
                Test("failed refresh stays paused without repeated retries", delegate
                {
                    NetworkRefreshPolicy policy = new NetworkRefreshPolicy();
                    DateTime now = DateTime.UtcNow;
                    policy.Observe("old", "new", now);
                    policy.Pause();
                    for (int index = 0; index < 100; index++)
                        Assert(!policy.Observe("old", "new", now.AddMinutes(index + 1)), "Paused refresh retried.");
                    Assert(policy.Paused, "Pause was silently cleared.");
                });
                Test("explicit recovery resets pause but still waits for stability", delegate
                {
                    NetworkRefreshPolicy policy = new NetworkRefreshPolicy();
                    DateTime now = DateTime.UtcNow;
                    policy.Pause();
                    policy.Reset();
                    Assert(!policy.Paused && !policy.Observe("old", "new", now), "Recovery skipped stability window.");
                    Assert(policy.Observe("old", "new", now.AddSeconds(8)), "Recovery never became ready.");
                });
                Test("missing or unchanged network never triggers refresh", delegate
                {
                    NetworkRefreshPolicy policy = new NetworkRefreshPolicy();
                    DateTime now = DateTime.UtcNow;
                    policy.Observe("old", "new", now);
                    Assert(!policy.Observe("old", null, now.AddSeconds(10)), "Missing network triggered refresh.");
                    Assert(!policy.Observe("old", "old", now.AddSeconds(20)), "Unchanged network triggered refresh.");
                    Assert(!policy.Observe("old", "new", now.AddSeconds(30)), "Reappearing network reused stale stability time.");
                });
                Test("NRPT timeout scales for full rule sets and remains bounded", delegate
                {
                    Assert(NrptManager.ApplyTimeoutMilliseconds(55) == 195000, "55-rule timeout does not allow per-rule work.");
                    Assert(NrptManager.ApplyTimeoutMilliseconds(0) == 120000, "Minimum timeout changed.");
                    Assert(NrptManager.ApplyTimeoutMilliseconds(int.MaxValue) == 300000, "Large count overflowed or became unbounded.");
                });
                Test("38 mixed input rules expand to 55 verified DNS namespaces", delegate
                {
                    List<string> inputs = new List<string>();
                    for (int i = 0; i < 18; i++) inputs.Add(".suffix-" + i + ".invalid");
                    for (int i = 0; i < 19; i++) inputs.Add("exact-" + i + ".invalid");
                    inputs.Add("192.0.2.10");
                    RuleSet rules = new RuleSet(inputs);
                    Assert(rules.GetNrptNamespaces().Count == 55, "Full mixed input expansion is incorrect.");
                    string script = NrptManager.BuildApplyScript(rules.GetNrptNamespaces());
                    Assert(script.Contains("applied 55/55") && script.Contains("apply_readback"), "Full apply is missing phase diagnostics.");
                });
                Test("rule refresh starts only after a change and acknowledges success", delegate
                {
                    RuleRefreshQueue queue = new RuleRefreshQueue();
                    Assert(!queue.HasPending && !queue.CanApply(false, true, false), "Idle queue triggered work.");
                    queue.Request();
                    Assert(queue.HasPending && queue.CanApply(false, true, false), "New edit was not queued.");
                    queue.Complete(queue.RequestedRevision, true);
                    Assert(!queue.HasPending && !queue.CanApply(false, true, false), "Successful apply repeated forever.");
                });
                Test("rapid AnyDesk additions apply both suffixes after the older batch completes", delegate
                {
                    RuleRefreshQueue queue = new RuleRefreshQueue();
                    List<string> desired = new List<string> { ".net.anydesk.com.cn" };
                    queue.Request();
                    long firstRevision = queue.RequestedRevision;
                    RuleSet first = new RuleSet(desired);
                    desired.Add(".net.anydesk.com");
                    queue.Request();
                    Assert(!queue.CanApply(true, true, false), "Second edit overlapped the in-flight batch.");
                    Assert(first.GetNrptNamespaces().Count == 2, "In-flight snapshot was mutated.");
                    queue.Complete(firstRevision, true);
                    Assert(queue.HasPending && queue.CanApply(false, true, false), "Second edit was dropped.");
                    RuleSet latest = new RuleSet(desired);
                    List<string> namespaces = latest.GetNrptNamespaces();
                    Assert(namespaces.Count == 4 && namespaces.Contains(".net.anydesk.com") &&
                        namespaces.Contains(".net.anydesk.com.cn"), "Latest complete AnyDesk list was not applied.");
                    queue.Complete(queue.RequestedRevision, true);
                    Assert(!queue.HasPending, "Latest batch was not acknowledged.");
                });
                Test("many additions and deletion coalesce into the latest complete rule list", delegate
                {
                    RuleRefreshQueue queue = new RuleRefreshQueue();
                    List<string> desired = new List<string> { "removed.invalid" };
                    queue.Request();
                    long firstRevision = queue.RequestedRevision;
                    for (int i = 0; i < 200; i++) { desired.Add("batch-" + i + ".invalid"); queue.Request(); }
                    desired.Remove("removed.invalid");
                    queue.Request();
                    queue.Complete(firstRevision, true);
                    Assert(queue.CanApply(false, true, false), "Batched edits were lost.");
                    RuleSet latest = new RuleSet(desired);
                    Assert(latest.NormalizedRules.Count == 200 && !latest.NormalizedRules.Contains("removed.invalid"),
                        "Follow-up apply replayed an obsolete or partial list.");
                    queue.Complete(queue.RequestedRevision, true);
                    Assert(!queue.HasPending, "Batched apply failed to drain the queue.");
                });
                Test("pending edits respect busy disabled and cleanup-pending gates", delegate
                {
                    RuleRefreshQueue queue = new RuleRefreshQueue();
                    queue.Request();
                    Assert(!queue.CanApply(true, true, false), "Busy operation was bypassed.");
                    Assert(!queue.CanApply(false, false, false), "Saved edit enabled a disabled helper.");
                    Assert(!queue.CanApply(false, true, true), "Pending cleanup was bypassed.");
                    Assert(queue.HasPending && queue.CanApply(false, true, false), "Gating discarded the saved edit.");
                });
                Test("edits during initial enable survive completion of the startup snapshot", delegate
                {
                    RuleRefreshQueue queue = new RuleRefreshQueue();
                    long startupRevision = queue.RequestedRevision;
                    queue.Request();
                    queue.Request();
                    queue.Complete(startupRevision, true);
                    Assert(queue.HasPending && queue.CanApply(false, true, false), "Startup swallowed later edits.");
                });
                Test("network rebuild acknowledges only the revision it captured", delegate
                {
                    RuleRefreshQueue queue = new RuleRefreshQueue();
                    queue.Request();
                    long networkRevision = queue.RequestedRevision;
                    queue.Request(); // A private-network toggle or domain edit during rebuild.
                    queue.Complete(networkRevision, true);
                    Assert(queue.AppliedRevision == networkRevision && queue.HasPending,
                        "Network rebuild incorrectly acknowledged a later setting change.");
                });
                Test("failed rule refresh pauses instead of looping even after another edit", delegate
                {
                    RuleRefreshQueue queue = new RuleRefreshQueue();
                    queue.Request();
                    queue.Complete(queue.RequestedRevision, false);
                    queue.Request();
                    for (int i = 0; i < 100; i++)
                        Assert(!queue.CanApply(false, true, false), "Failure triggered an automatic retry storm.");
                    Assert(queue.Paused && queue.HasPending && queue.AppliedRevision == 0, "Failed edit was reported applied.");
                });
                Test("successful explicit re-enable resumes queue without losing newer edits", delegate
                {
                    RuleRefreshQueue queue = new RuleRefreshQueue();
                    queue.Request();
                    queue.Complete(queue.RequestedRevision, false);
                    long retryRevision = queue.RequestedRevision;
                    queue.Request();
                    queue.Complete(retryRevision, true);
                    Assert(!queue.Paused && queue.CanApply(false, true, false), "Recovery did not preserve pending work.");
                    queue.Complete(queue.RequestedRevision, true);
                    Assert(!queue.HasPending, "Recovery failed to apply the newest list.");
                });
                Test("older completion cannot roll back acknowledged rule revision", delegate
                {
                    RuleRefreshQueue queue = new RuleRefreshQueue();
                    queue.Request();
                    queue.Request();
                    queue.Complete(2, true);
                    queue.Complete(1, true);
                    Assert(queue.AppliedRevision == 2 && !queue.HasPending, "Older snapshot rolled applied state back.");
                });
                CompatibilityDetectionTests.Run(Test);
                SystemProxyBypassTests.Run(Test);
                Console.WriteLine("C_SHARP_TESTS_OK " + passed);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex.Message);
                return 1;
            }
        }
    }
}
