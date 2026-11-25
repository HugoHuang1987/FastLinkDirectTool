using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FastLinkDirectTool.Modes;

namespace FastLinkDirectTool.Tests
{
    // In-memory fixtures only. No production startup, live snapshots or executors.
    internal static class NetworkModePlanningTests
    {
        private static int count;
        private static int failures;
        private const string CorporateId = "adapter-corporate-guid";
        private const string InternetId = "adapter-internet-guid";

        private static void Assert(bool condition, string message)
        { if (!condition) throw new Exception(message); }

        private static void Test(string name, Action test)
        {
            count++;
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }

        private static ModeRule Rule(string pattern, RuleTarget target)
        { return new ModeRule { Pattern = pattern, Target = target }; }

        private static ModeRequest Request()
        {
            return new ModeRequest
            {
                Mode = WorkMode.WiredCorporateWifiInternet,
                CorporateAdapterId = CorporateId,
                InternetAdapterId = InternetId,
                Rules = new List<ModeRule>
                {
                    Rule("portal.corp.example.test", RuleTarget.Corporate),
                    Rule(".notes.example.test", RuleTarget.Internet)
                },
                CorporateCidrs = new List<string> { "10.20.0.0/24" }
            };
        }

        private static NetworkSnapshot Snapshot()
        {
            return new NetworkSnapshot
            {
                CorporateAdmissionVerified = true,
                DualConnectionAllowed = true,
                InternetReachabilityVerified = true,
                ExternalDefaultPreferred = true,
                StableSeconds = 8,
                Adapters = new List<AdapterSnapshot>
                {
                    new AdapterSnapshot { Id = CorporateId, Name = "Office Ethernet", Kind = LinkKind.Ethernet,
                        IsUp = true, IsPhysical = true, Address = "192.0.2.20", PrefixLength = 24,
                        Gateway = "192.0.2.1", DnsServers = new List<string> { "192.0.2.53" } },
                    new AdapterSnapshot { Id = InternetId, Name = "External Wi-Fi", Kind = LinkKind.Wifi,
                        IsUp = true, IsPhysical = true, Address = "198.51.100.20", PrefixLength = 24,
                        Gateway = "198.51.100.1", DnsServers = new List<string> { "203.0.113.53" } }
                }
            };
        }

        private static void Blocked(ModePlan plan)
        {
            Assert(!plan.CanSimulate && plan.Status == "Blocked", "unsafe plan was not blocked");
            Assert(plan.Blockers.Count > 0, "blocked plan has no explanation");
            Assert(plan.Intents.Count == 0, "blocked plan leaked executable intentions");
        }

        private static void Waiting(ModePlan plan)
        {
            Assert(!plan.CanSimulate && plan.Status == "Waiting", "missing corporate network must wait");
            Assert(plan.Blockers.Count > 0 && plan.Intents.Count == 0, "waiting must preserve all existing paths");
        }

        private static void Ready(ModePlan plan)
        {
            Assert(plan.CanSimulate && plan.Status == "ReadyForSimulation", "valid fixture blocked: " + string.Join(";", plan.Blockers));
            Assert(plan.Blockers.Count == 0 && plan.Warnings.Count > 0, "simulation must disclose its limits");
            Assert(plan.Intents.Count > 0, "valid dual network has no plan");
            Assert(plan.Intents.Count(i => i.Kind == "ProtectInternetDefault") == 1, "external default not protected exactly once");
            Assert(plan.Intents.Where(i => i.Kind.StartsWith("Corporate", StringComparison.Ordinal)).All(i => i.InterfaceId == CorporateId), "corporate intentions crossed interfaces");
            Assert(plan.Intents.Where(i => i.Kind.StartsWith("Internet", StringComparison.Ordinal) || i.Kind == "ProtectInternetDefault").All(i => i.InterfaceId == InternetId), "internet intentions crossed interfaces");
            Assert(!plan.Intents.Any(i => i.Kind.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0 || i.Kind.IndexOf("Restart", StringComparison.OrdinalIgnoreCase) >= 0), "planner proposed deletion or restart");
        }

        private static void SnapshotBlock(string name, Action<NetworkSnapshot> mutate)
        { Test(name, delegate { NetworkSnapshot s = Snapshot(); mutate(s); Blocked(NetworkModePlanner.Build(Request(), s)); }); }

        private static void RequestBlock(string name, Action<ModeRequest> mutate)
        { Test(name, delegate { ModeRequest r = Request(); mutate(r); Blocked(NetworkModePlanner.Build(r, Snapshot())); }); }

        private static string State(ModeRequest request, NetworkSnapshot snapshot)
        {
            List<string> values = new List<string>
            {
                request.Mode.ToString(), request.CorporateAdapterId, request.InternetAdapterId,
                snapshot.SystemProxyActive.ToString(), snapshot.TunnelActive.ToString(), snapshot.CleanupPending.ToString(),
                snapshot.OtherDnsPolicyPresent.ToString(), snapshot.CorporateAdmissionVerified.ToString(),
                snapshot.DualConnectionAllowed.ToString(), snapshot.InternetReachabilityVerified.ToString(),
                snapshot.ExternalDefaultPreferred.ToString(), snapshot.Ipv6SafetyVerified.ToString(), snapshot.StableSeconds.ToString(CultureInfo.InvariantCulture)
            };
            values.AddRange(request.Rules.Select(r => r.Pattern + "|" + r.Target));
            values.AddRange(request.CorporateCidrs);
            foreach (AdapterSnapshot a in snapshot.Adapters)
            {
                values.Add(string.Join("|", new[] { a.Id, a.Name, a.Kind.ToString(), a.IsUp.ToString(), a.IsPhysical.ToString(),
                    a.Address, a.PrefixLength.ToString(CultureInfo.InvariantCulture), a.Gateway, a.HasIpv6Default.ToString(), a.Ipv6RouteSignature }));
                values.AddRange(a.DnsServers);
            }
            return string.Join("\n", values);
        }

        private static string PlanText(ModePlan plan)
        { return plan.Status + "|" + string.Join("\n", plan.Intents.Select(i => i.Kind + "|" + i.Destination + "|" + i.InterfaceId + "|" + i.Gateway).ToArray()); }

        private static RouteDecision Decision(IEnumerable<ModeRule> rules, string host, params string[] answers)
        { return RuleRouter.Decide(rules == null ? null : rules.ToList(), host, answers == null ? null : answers.ToList()); }

        private static void Routed(RouteDecision decision, RuleTarget target)
        { Assert(!decision.Blocked && decision.Target == target, "wrong target or unexpected block: " + decision.Reason); }

        private static void Rejected(RouteDecision decision)
        { Assert(decision.Blocked && decision.Addresses.Count == 0 && !string.IsNullOrWhiteSpace(decision.Reason), "unsafe route answer was accepted"); }

        private static void PlanTests()
        {
            Test("wired corporate plus Wi-Fi internet produces isolated preview", delegate
            {
                ModePlan plan = NetworkModePlanner.Build(Request(), Snapshot()); Ready(plan);
                Assert(plan.Intents.Any(i => i.Kind == "CorporateDnsHostRoute" && i.Destination == "192.0.2.53/32"), "company DNS path missing");
                Assert(plan.Intents.Any(i => i.Kind == "CorporateCidrRoute" && i.Destination == "10.20.0.0/24"), "explicit CIDR missing");
                Assert(!plan.Intents.Any(i => i.Destination == "10.0.0.0/8" || i.Destination == "172.16.0.0/12" || i.Destination == "192.168.0.0/16"), "private routes broadened implicitly");
            });
            Test("reverse mode uses explicit roles not Wi-Fi preference", delegate
            {
                ModeRequest r = Request(); NetworkSnapshot s = Snapshot();
                r.Mode = WorkMode.WifiCorporateWiredInternet;
                s.Adapters[0].Kind = LinkKind.Wifi; s.Adapters[1].Kind = LinkKind.Ethernet;
                Ready(NetworkModePlanner.Build(r, s));
            });
            foreach (WorkMode mode in new[] { WorkMode.Normal, WorkMode.FastLinkSplit })
            {
                WorkMode captured = mode;
                Test(mode + " preserves old behavior and requires no live snapshot", delegate
                {
                    ModePlan p = NetworkModePlanner.Build(new ModeRequest { Mode = captured }, null);
                    Assert(!p.CanSimulate && p.Intents.Count == 0 && p.Blockers.Count == 0, "legacy/normal path was taken over");
                    Assert(p.Status == (captured == WorkMode.Normal ? "NormalPreserved" : "FastLinkPreserved"), "wrong preservation status");
                });
            }
            Test("null request fails closed", delegate { Blocked(NetworkModePlanner.Build(null, Snapshot())); });
            Test("null snapshot fails closed", delegate { Blocked(NetworkModePlanner.Build(Request(), null)); });
            RequestBlock("unknown mode rejected", r => r.Mode = (WorkMode)99);
            RequestBlock("corporate ID required", r => r.CorporateAdapterId = " ");
            RequestBlock("internet ID required", r => r.InternetAdapterId = null);
            RequestBlock("same adapter cannot fill both roles", r => r.CorporateAdapterId = InternetId.ToUpperInvariant());
            RequestBlock("display name does not substitute for stable adapter ID", r => r.InternetAdapterId = "External Wi-Fi");
            Test("missing corporate adapter waits without changing external path", delegate
            { NetworkSnapshot s = Snapshot(); s.Adapters.RemoveAt(0); Waiting(NetworkModePlanner.Build(Request(), s)); });
            Test("corporate link down waits without auto-switching", delegate
            { NetworkSnapshot s = Snapshot(); s.Adapters[0].IsUp = false; Waiting(NetworkModePlanner.Build(Request(), s)); });
            Test("removed corporate interface is not replaced by name lookalike", delegate
            { NetworkSnapshot s = Snapshot(); s.Adapters[0].Id = "replacement-different-guid"; Waiting(NetworkModePlanner.Build(Request(), s)); });
            SnapshotBlock("missing internet adapter blocks", s => s.Adapters.RemoveAt(1));
            SnapshotBlock("internet link down blocks", s => s.Adapters[1].IsUp = false);
            SnapshotBlock("empty adapter snapshot blocks", s => s.Adapters.Clear());
            SnapshotBlock("null adapter collection blocks", s => s.Adapters = null);
            SnapshotBlock("duplicate selected interface IDs block", s => s.Adapters.Add(s.Adapters[0]));
            SnapshotBlock("virtual corporate adapter blocks", s => s.Adapters[0].IsPhysical = false);
            SnapshotBlock("virtual internet adapter blocks", s => s.Adapters[1].IsPhysical = false);
            SnapshotBlock("wrong corporate link kind blocks", s => s.Adapters[0].Kind = LinkKind.Wifi);
            SnapshotBlock("unknown internet link kind blocks", s => s.Adapters[1].Kind = LinkKind.Other);
            Test("renamed interfaces retain stable role binding", delegate
            { NetworkSnapshot s = Snapshot(); s.Adapters[0].Name = "Renamed A"; s.Adapters[1].Name = "Renamed B"; Ready(NetworkModePlanner.Build(Request(), s)); });
            Test("unselected adapter and enumeration order do not change roles", delegate
            { NetworkSnapshot s = Snapshot(); s.Adapters.Add(null); s.Adapters.Add(new AdapterSnapshot { Id = "irrelevant", Kind = LinkKind.Wifi }); s.Adapters.Reverse(); Ready(NetworkModePlanner.Build(Request(), s)); });
            SnapshotBlock("system proxy conflict blocks without editing proxy", s => s.SystemProxyActive = true);
            SnapshotBlock("TUN conflict blocks without stopping VPN", s => s.TunnelActive = true);
            SnapshotBlock("pending cleanup blocks all new intentions", s => s.CleanupPending = true);
            SnapshotBlock("foreign DNS policy blocks takeover", s => s.OtherDnsPolicyPresent = true);
            SnapshotBlock("unverified corporate admission blocks", s => s.CorporateAdmissionVerified = false);
            SnapshotBlock("dual connection not explicitly allowed blocks", s => s.DualConnectionAllowed = false);
            SnapshotBlock("unverified internet reachability blocks", s => s.InternetReachabilityVerified = false);
            SnapshotBlock("external default not verified blocks", s => s.ExternalDefaultPreferred = false);
            SnapshotBlock("seven seconds stability is insufficient", s => s.StableSeconds = 7);
            SnapshotBlock("negative stability fails closed", s => s.StableSeconds = -1);
            Test("stable eight second boundary is accepted", delegate { Ready(NetworkModePlanner.Build(Request(), Snapshot())); });
            SnapshotBlock("corporate IPv6 default needs explicit verification", s => s.Adapters[0].HasIpv6Default = true);
            SnapshotBlock("internet IPv6 default needs explicit verification", s => s.Adapters[1].HasIpv6Default = true);
            Test("observed internet IPv6 context still plans only IPv4", delegate
            {
                NetworkSnapshot s = Snapshot(); s.Adapters[1].HasIpv6Default = true; s.Ipv6SafetyVerified = true;
                s.Adapters[1].Ipv6RouteSignature = "::/0|fe80::1|256|25";
                ModePlan p = NetworkModePlanner.Build(Request(), s); Ready(p);
                Assert(!p.Intents.Any(i => i.Destination.Contains(":")), "unexpected IPv6 mutation intent");
            });
            Test("mode three preserves observed external wired IPv6 without boolean override", delegate
            {
                ModeRequest r = Request(); r.Mode = WorkMode.WifiCorporateWiredInternet;
                NetworkSnapshot s = Snapshot(); s.Adapters[0].Kind = LinkKind.Wifi; s.Adapters[1].Kind = LinkKind.Ethernet;
                s.Adapters[1].HasIpv6Default = true; s.Adapters[1].Ipv6RouteSignature = "::/0|fe80::1|256|25";
                string before = State(r, s); ModePlan p = NetworkModePlanner.Build(r, s); Ready(p);
                Assert(!s.Ipv6SafetyVerified && before == State(r, s), "planner mutated or relied on boolean confirmation");
                Assert(!p.Intents.Any(i => i.Destination.Contains(":")), "IPv6 mutation planned");
            });
            Test("corporate IPv6 is rejected even with complete evidence and override", delegate
            {
                NetworkSnapshot s = Snapshot(); s.Adapters[0].HasIpv6Default = true; s.Adapters[0].Ipv6RouteSignature = "::/0|fe80::1|256|25";
                s.Ipv6SafetyVerified = true; Blocked(NetworkModePlanner.Build(Request(), s));
            });
            Test("unknown third or virtual IPv6 egress always blocks", delegate
            {
                foreach (bool up in new[] { false, true }) foreach (bool physical in new[] { false, true })
                {
                    NetworkSnapshot s = Snapshot(); s.Ipv6SafetyVerified = true;
                    s.Adapters.Add(new AdapterSnapshot { Id = "third", IsUp = up, IsPhysical = physical, Kind = LinkKind.Other,
                        HasIpv6Default = true, Ipv6RouteSignature = "::/1|fe80::1|1|5;8000::/1|fe80::1|1|5" });
                    Blocked(NetworkModePlanner.Build(Request(), s));
                }
            });
            Test("missing malformed or inconsistent IPv6 route evidence blocks", delegate
            {
                foreach (string signature in new[] { null, "", "::/0|fe80::1", "::/0|192.0.2.1|1|25", "::/0|::|1|25", "::/0|fe80::1|-1|25", "::/0|fe80::1|1|0" })
                {
                    NetworkSnapshot s = Snapshot(); s.Ipv6SafetyVerified = true; s.Adapters[1].HasIpv6Default = true;
                    s.Adapters[1].Ipv6RouteSignature = signature; Blocked(NetworkModePlanner.Build(Request(), s));
                }
                NetworkSnapshot inconsistent = Snapshot(); inconsistent.Adapters[1].Ipv6RouteSignature = "::/0|fe80::1|256|25";
                Blocked(NetworkModePlanner.Build(Request(), inconsistent));
            });
            Test("IPv6 preservation requires unique up physical external role", delegate
            {
                foreach (Action<NetworkSnapshot> change in new Action<NetworkSnapshot>[] {
                    s => s.Adapters[1].IsUp = false, s => s.Adapters[1].IsPhysical = false,
                    s => s.Adapters[1].Kind = LinkKind.Other, s => s.Adapters.Add(s.Adapters[1]) })
                {
                    NetworkSnapshot s = Snapshot(); s.Adapters[1].HasIpv6Default = true; s.Adapters[1].Ipv6RouteSignature = "::/0|fe80::1|256|25"; change(s);
                    Assert(NetworkModePlanner.Ipv6CompatibilityBlocker(Request(), s) != null, "ambiguous external IPv6 accepted");
                }
            });
            Test("preparation supports mode three external IPv6 before admission", delegate
            {
                ModeRequest r = Request(); r.Mode = WorkMode.WifiCorporateWiredInternet;
                NetworkSnapshot s = Snapshot(); s.Adapters[0].Kind = LinkKind.Wifi; s.Adapters[1].Kind = LinkKind.Ethernet;
                s.Adapters[1].HasIpv6Default = true; s.Adapters[1].Ipv6RouteSignature = "::/0|fe80::1|256|25";
                s.CorporateAdmissionVerified = false; s.ExternalDefaultPreferred = false;
                Assert(NetworkModePlanner.BuildPreparation(r, s).CanSimulate, "observed external IPv6 blocked preparation");
                Blocked(NetworkModePlanner.Build(r, s));
            });
            SnapshotBlock("missing corporate DNS blocks", s => s.Adapters[0].DnsServers.Clear());
            SnapshotBlock("missing internet DNS blocks", s => s.Adapters[1].DnsServers = null);
            SnapshotBlock("fake-IP DNS blocked", s => s.Adapters[0].DnsServers[0] = "198.18.0.53");
            SnapshotBlock("IPv6 DNS outside supported scope blocks", s => s.Adapters[0].DnsServers[0] = "2001:db8::53");
            SnapshotBlock("same DNS address for both roles blocks", s => s.Adapters[0].DnsServers[0] = s.Adapters[1].DnsServers[0]);
            SnapshotBlock("corporate DNS cannot capture external local path", s => s.Adapters[0].DnsServers[0] = "198.51.100.1");
            SnapshotBlock("overlapping directly connected subnets block", delegate(NetworkSnapshot s)
            { s.Adapters[0].Address = "198.51.100.30"; s.Adapters[0].Gateway = "198.51.100.2"; });
            SnapshotBlock("same local address blocks", delegate(NetworkSnapshot s)
            { s.Adapters[0].Address = s.Adapters[1].Address; s.Adapters[0].Gateway = "198.51.100.2"; });
            SnapshotBlock("invalid local IPv4 blocked", s => s.Adapters[0].Address = "not-an-address");
            SnapshotBlock("APIPA local address blocked", s => s.Adapters[0].Address = "169.254.2.20");
            SnapshotBlock("fake-IP local address blocked", s => s.Adapters[0].Address = "198.19.1.20");
            SnapshotBlock("IPv6-only adapter outside supported scope blocks", s => s.Adapters[0].Address = "2001:db8::20");
            SnapshotBlock("missing gateway is explicitly unsupported", s => s.Adapters[0].Gateway = null);
            SnapshotBlock("off-subnet gateway blocks", s => s.Adapters[0].Gateway = "203.0.113.1");
            SnapshotBlock("gateway equal to local address blocks", s => s.Adapters[0].Gateway = s.Adapters[0].Address);
            SnapshotBlock("network address cannot be gateway", s => s.Adapters[0].Gateway = "192.0.2.0");
            SnapshotBlock("broadcast address cannot be local host", s => s.Adapters[0].Address = "192.0.2.255");
            SnapshotBlock("zero prefix rejected", s => s.Adapters[0].PrefixLength = 0);
            SnapshotBlock("host prefix unsupported", s => s.Adapters[0].PrefixLength = 32);
            Test("point-to-point IPv4 slash31 has valid hosts", delegate
            { NetworkSnapshot s = Snapshot(); s.Adapters[0].Address = "192.0.2.0"; s.Adapters[0].Gateway = "192.0.2.1"; s.Adapters[0].PrefixLength = 31; Ready(NetworkModePlanner.Build(Request(), s)); });
            RequestBlock("unclassified migration rule blocks", r => r.Rules[0].Target = RuleTarget.Review);
            RequestBlock("invalid rule target blocks", r => r.Rules[0].Target = (RuleTarget)19);
            RequestBlock("null rule blocks", r => r.Rules.Add(null));
            RequestBlock("invalid wildcard rule blocks", r => r.Rules.Add(Rule("*", RuleTarget.Corporate)));
            RequestBlock("same exact rule opposite targets blocks", r => r.Rules.Add(Rule("PORTAL.CORP.EXAMPLE.TEST", RuleTarget.Internet)));
            RequestBlock("suffix and star suffix equal-priority conflict blocks", delegate(ModeRequest r)
            { r.Rules.Add(Rule(".corp.example.test", RuleTarget.Corporate)); r.Rules.Add(Rule("*.corp.example.test", RuleTarget.Internet)); });
            RequestBlock("explicit corporate CIDR overlapping external subnet blocks", r => r.CorporateCidrs.Add("198.51.100.0/24"));
            Test("broader corporate CIDR excludes the external subnet", delegate
            {
                ModeRequest r = Request(); r.CorporateCidrs.Add("198.51.0.0/16");
                ModePlan p = NetworkModePlanner.Build(r, Snapshot()); Ready(p);
                List<string> routes = p.Intents.Where(i => i.Kind == "CorporateCidrRoute").Select(i => i.Destination).ToList();
                Assert(!Covers(routes, "198.51.100.1") && !Covers(routes, "198.51.100.20") && !Covers(routes, "198.51.100.254"), "external subnet hijacked");
                Assert(Covers(routes, "198.51.99.254") && Covers(routes, "198.51.101.1"), "unprotected corporate remainder lost");
            });
            RequestBlock("corporate host route targeting external gateway blocks", r => r.Rules.Add(Rule("198.51.100.1", RuleTarget.Corporate)));
            RequestBlock("default route CIDR is never silently accepted", r => r.CorporateCidrs.Add("0.0.0.0/0"));
            RequestBlock("fake-IP CIDR is rejected", r => r.CorporateCidrs.Add("198.18.0.0/15"));
            RequestBlock("IPv6 CIDR is explicitly unsupported", r => r.CorporateCidrs.Add("2001:db8::/32"));
            RequestBlock("invalid CIDR prefix blocks", r => r.CorporateCidrs.Add("10.20.0.0/33"));
            Test("host-bit CIDRs normalize and deduplicate without broadening", delegate
            {
                ModeRequest r = Request(); r.CorporateCidrs.Add("10.20.0.42/24");
                ModePlan p = NetworkModePlanner.Build(r, Snapshot()); Ready(p);
                Assert(p.Intents.Count(i => i.Kind == "CorporateCidrRoute") == 1, "normalized duplicate CIDRs persisted");
            });
            Test("URL and IPv4 rules retain explicit destinations", delegate
            {
                ModeRequest r = Request(); r.Rules.Add(Rule("https://wiki.corp.example.test/pages/view?id=1", RuleTarget.Corporate));
                r.Rules.Add(Rule("http://10.20.0.50/tool/", RuleTarget.Corporate));
                ModePlan p = NetworkModePlanner.Build(r, Snapshot()); Ready(p);
                Assert(p.Intents.Any(i => i.Kind == "CorporateSplitDns" && i.Destination == "wiki.corp.example.test"), "URL host not normalized");
                Assert(p.Intents.Any(i => i.Kind == "CorporateIpRoute" && i.Destination == "10.20.0.50/32"), "IP URL not a host route");
                Assert(!p.Intents.Any(i => i.Destination.Contains("?")), "URL query leaked into plan");
            });
            Test("public direct-use rules are not migrated into corporate target", delegate
            {
                ModePlan p = NetworkModePlanner.Build(Request(), Snapshot()); Ready(p);
                Assert(p.Intents.Where(i => i.Destination == ".notes.example.test").All(i => i.InterfaceId == InternetId), "public site sent to corporate interface");
            });
            Test("repeated plan build is deterministic and does not mutate request snapshot", delegate
            {
                ModeRequest r = Request(); NetworkSnapshot s = Snapshot(); string before = State(r, s);
                ModePlan first = NetworkModePlanner.Build(r, s); ModePlan second = NetworkModePlanner.Build(r, s);
                Ready(first); Ready(second); Assert(before == State(r, s), "input changed");
                Assert(PlanText(first) == PlanText(second), "same facts produced different plans");
                first.Intents[0].Destination = "changed-only-returned-plan";
                Assert(!second.Intents.Any(i => i.Destination == "changed-only-returned-plan"), "plan objects share mutable state");
            });
            Test("blocked plan does not clear conflict or cleanup ownership flags", delegate
            {
                ModeRequest r = Request(); NetworkSnapshot s = Snapshot(); s.CleanupPending = true; s.SystemProxyActive = true;
                string before = State(r, s); Blocked(NetworkModePlanner.Build(r, s)); Assert(before == State(r, s), "blocked planning edited ownership facts");
            });
            Test("mode transitions wait for cleanup and never acknowledge another mode", delegate
            {
                ModeRequest r = Request(); NetworkSnapshot s = Snapshot(); Ready(NetworkModePlanner.Build(r, s));
                r.Mode = WorkMode.WifiCorporateWiredInternet; s.Adapters[0].Kind = LinkKind.Wifi; s.Adapters[1].Kind = LinkKind.Ethernet;
                s.CleanupPending = true; Blocked(NetworkModePlanner.Build(r, s));
                s.CleanupPending = false; s.StableSeconds = 0; Blocked(NetworkModePlanner.Build(r, s));
                s.StableSeconds = 8; Ready(NetworkModePlanner.Build(r, s));
                r.Mode = WorkMode.Normal; Assert(NetworkModePlanner.Build(r, s).Intents.Count == 0, "normal transition generated cleanup without executor ownership");
            });
            foreach (int size in new[] { 100, 200 })
            {
                int captured = size;
                Test(size + " rules remain complete isolated and stable", delegate
                {
                    ModeRequest r = Request(); r.Rules.Clear();
                    for (int n = 0; n < captured; n++) r.Rules.Add(Rule("site-" + n + ".example.test", n % 2 == 0 ? RuleTarget.Corporate : RuleTarget.Internet));
                    ModePlan p = NetworkModePlanner.Build(r, Snapshot()); Ready(p);
                    Assert(p.Intents.Count(i => i.Kind.EndsWith("SplitDns", StringComparison.Ordinal)) == captured, "full rule list truncated");
                    Assert(p.Intents.Count(i => i.Kind.EndsWith("DynamicIpRoute", StringComparison.Ordinal)) == captured, "route plan truncated");
                    for (int n = 0; n < captured; n++) Routed(Decision(r.Rules, "site-" + n + ".example.test", "10.20.0.50"), n % 2 == 0 ? RuleTarget.Corporate : RuleTarget.Internet);
                });
            }
        }

        private static void RouterTests()
        {
            Test("unmatched public domain defaults to internet", delegate { Routed(Decision(Request().Rules, "public.example.test", "203.0.113.80"), RuleTarget.Internet); });
            Test("null rules do not infer corporate destinations", delegate { Routed(Decision(null, "public.example.test"), RuleTarget.Internet); });
            Test("exact corporate host matches case and terminal dot", delegate { Routed(Decision(Request().Rules, "PORTAL.CORP.EXAMPLE.TEST.", "10.20.0.50"), RuleTarget.Corporate); });
            Test("exact match does not capture a subdomain", delegate { Routed(Decision(Request().Rules, "child.portal.corp.example.test", "203.0.113.80"), RuleTarget.Internet); });
            Test("suffix includes apex and descendants only", delegate
            {
                ModeRule[] r = { Rule(".corp.example.test", RuleTarget.Corporate) };
                Routed(Decision(r, "corp.example.test", "10.20.0.50"), RuleTarget.Corporate);
                Routed(Decision(r, "a.b.corp.example.test", "10.20.0.50"), RuleTarget.Corporate);
                Routed(Decision(r, "notcorp.example.test", "203.0.113.80"), RuleTarget.Internet);
            });
            Test("star suffix excludes apex but includes subdomains", delegate
            {
                ModeRule[] r = { Rule("*.corp.example.test", RuleTarget.Corporate) };
                Routed(Decision(r, "a.corp.example.test", "10.20.0.50"), RuleTarget.Corporate);
                Routed(Decision(r, "corp.example.test", "203.0.113.80"), RuleTarget.Internet);
            });
            Test("question-mark wildcard retains one-character semantics", delegate
            {
                ModeRule[] r = { Rule("wiki?.corp.example.test", RuleTarget.Corporate) };
                Routed(Decision(r, "wiki1.corp.example.test", "10.20.0.50"), RuleTarget.Corporate);
                Routed(Decision(r, "wiki12.corp.example.test", "203.0.113.80"), RuleTarget.Internet);
            });
            Test("exact exception outranks suffix regardless list order", delegate
            {
                List<ModeRule> r = new List<ModeRule> { Rule(".example.test", RuleTarget.Corporate), Rule("public.example.test", RuleTarget.Internet) };
                Routed(Decision(r, "public.example.test", "203.0.113.80"), RuleTarget.Internet);
                r.Reverse(); Routed(Decision(r, "public.example.test", "203.0.113.80"), RuleTarget.Internet);
            });
            Test("more specific suffix outranks broad corporate suffix", delegate
            {
                ModeRule[] r = { Rule(".example.test", RuleTarget.Corporate), Rule(".public.example.test", RuleTarget.Internet) };
                Routed(Decision(r, "cdn.public.example.test", "203.0.113.80"), RuleTarget.Internet);
            });
            Test("equal-priority opposing targets reject no arbitrary winner", delegate
            { Rejected(Decision(new[] { Rule(".example.test", RuleTarget.Corporate), Rule("*.example.test", RuleTarget.Internet) }, "portal.example.test", "10.20.0.50")); });
            Test("duplicate exact opposite targets reject", delegate
            { Rejected(Decision(new[] { Rule("portal.example.test", RuleTarget.Corporate), Rule("portal.example.test", RuleTarget.Internet) }, "portal.example.test", "10.20.0.50")); });
            Test("same-target duplicate rules are harmless", delegate
            { Routed(Decision(new[] { Rule("portal.example.test", RuleTarget.Corporate), Rule("portal.example.test", RuleTarget.Corporate) }, "portal.example.test", "10.20.0.50"), RuleTarget.Corporate); });
            Test("review match blocks rather than silently choosing internet", delegate
            { Rejected(Decision(new[] { Rule("portal.example.test", RuleTarget.Review) }, "portal.example.test", "10.20.0.50")); });
            Test("missing corporate DNS answers blocks without public fallback", delegate { Rejected(Decision(Request().Rules, "portal.corp.example.test")); });
            Test("null corporate DNS answers blocks", delegate { Rejected(Decision(Request().Rules, "portal.corp.example.test", (string[])null)); });
            Test("duplicate verified answers deduplicate", delegate
            { RouteDecision d = Decision(Request().Rules, "portal.corp.example.test", "10.20.0.50", "10.20.0.50"); Routed(d, RuleTarget.Corporate); Assert(d.Addresses.Count == 1, "duplicate routes"); });
            foreach (string unsafeAddress in new[] { "198.18.0.1", "198.19.255.254", "0.0.0.0", "127.0.0.1", "169.254.1.1", "224.0.0.1", "255.255.255.255", "2001:db8::1", "invalid", "1.2.3.999" })
            {
                string captured = unsafeAddress;
                Test("unsafe DNS answer rejected " + unsafeAddress, delegate { Rejected(Decision(Request().Rules, "portal.corp.example.test", "10.20.0.50", captured)); });
            }
            Test("public answer validation also rejects fake-IP", delegate { Rejected(Decision(Request().Rules, "public.example.test", "198.18.0.1")); });
            Test("fake-IP adjacent public blocks are not overblocked", delegate
            { Routed(Decision(Request().Rules, "public.example.test", "198.17.255.254", "198.20.0.1"), RuleTarget.Internet); });
            Test("null or wildcard request host rejected", delegate
            { Rejected(Decision(Request().Rules, null)); Rejected(Decision(Request().Rules, "*.corp.example.test")); });
            Test("URL rule extracts only host", delegate
            { Routed(Decision(new[] { Rule("https://wiki.corp.example.test/pages/view?id=1", RuleTarget.Corporate) }, "wiki.corp.example.test", "10.20.0.50"), RuleTarget.Corporate); });
            Test("explicit IPv4 route matches only requested address", delegate
            {
                ModeRule[] r = { Rule("http://10.20.0.50/path", RuleTarget.Corporate) };
                Routed(Decision(r, "10.20.0.50", "10.20.0.50"), RuleTarget.Corporate);
                Routed(Decision(r, "10.20.0.51", "10.20.0.51"), RuleTarget.Internet);
            });
            Test("router does not mutate answer list or rules", delegate
            {
                List<ModeRule> r = Request().Rules; List<string> answers = new List<string> { "10.20.0.50", "10.20.0.50" };
                RouteDecision d = RuleRouter.Decide(r, "portal.corp.example.test", answers); Routed(d, RuleTarget.Corporate);
                d.Addresses.Clear(); Assert(answers.Count == 2 && r.Count == 2 && r[0].Target == RuleTarget.Corporate, "input ownership mutated");
            });
        }

        private static RouteDecision ContextDecision(ModeRequest request, NetworkSnapshot snapshot, string host, params string[] answers)
        { return RuleRouter.Decide(request.Rules, host, answers.ToList(), request, snapshot); }

        private static void InfrastructureProtectionTests()
        {
            SnapshotBlock("corporate DNS cannot capture off-link internet DNS", s => s.Adapters[0].DnsServers[0] = "203.0.113.53");
            SnapshotBlock("DNS aliases compare canonical decimal IPv4", s => s.Adapters[0].DnsServers[0] = "203.000.113.053");
            SnapshotBlock("internet DNS leading-zero alias is still protected", delegate(NetworkSnapshot s)
            { s.Adapters[1].DnsServers[0] = "203.000.113.053"; s.Adapters[0].DnsServers[0] = "203.0.113.53"; });
            SnapshotBlock("corporate DNS cannot forward to own local address", s => s.Adapters[0].DnsServers[0] = s.Adapters[0].Address);
            RequestBlock("corporate IP cannot capture off-link internet DNS", r => r.Rules.Add(Rule("203.0.113.53", RuleTarget.Corporate)));
            Test("corporate CIDR excludes off-link internet DNS", delegate
            {
                ModeRequest r = Request(); r.CorporateCidrs.Add("203.0.113.0/24");
                ModePlan p = NetworkModePlanner.Build(r, Snapshot()); Ready(p);
                List<string> routes = p.Intents.Where(i => i.Kind == "CorporateCidrRoute").Select(i => i.Destination).ToList();
                Assert(!Covers(routes, "203.0.113.53") && Covers(routes, "203.0.113.52") && Covers(routes, "203.0.113.54"), "DNS exclusion broadened or omitted");
            });
            RequestBlock("corporate host CIDR cannot capture off-link internet DNS", r => r.CorporateCidrs.Add("203.0.113.53/32"));
            RequestBlock("corporate IP cannot route own local address", r => r.Rules.Add(Rule("192.0.2.20", RuleTarget.Corporate)));
            Test("corporate IP cannot capture any other local interface address", delegate
            {
                ModeRequest r = Request(); NetworkSnapshot s = Snapshot();
                s.Adapters.Add(new AdapterSnapshot { Id = "other-interface", Address = "10.20.0.99", Kind = LinkKind.Other });
                r.Rules.Add(Rule("10.20.0.99", RuleTarget.Corporate)); Blocked(NetworkModePlanner.Build(r, s));
            });
            RequestBlock("internet IP cannot capture corporate DNS", r => r.Rules.Add(Rule("192.0.2.53", RuleTarget.Internet)));
            RequestBlock("internet IP cannot capture corporate gateway", r => r.Rules.Add(Rule("192.0.2.1", RuleTarget.Internet)));
            RequestBlock("internet IP cannot capture corporate local address", r => r.Rules.Add(Rule("192.0.2.20", RuleTarget.Internet)));
            Test("dynamic corporate answers respect selected network context", delegate
            { Routed(ContextDecision(Request(), Snapshot(), "portal.corp.example.test", "10.20.0.50"), RuleTarget.Corporate); });
            Test("dynamic public answers remain on internet context", delegate
            { Routed(ContextDecision(Request(), Snapshot(), "cdn.notes.example.test", "203.0.113.80"), RuleTarget.Internet); });
            Test("dynamic corporate answers reject external local subnet", delegate
            { Rejected(ContextDecision(Request(), Snapshot(), "portal.corp.example.test", "198.51.100.80")); });
            Test("dynamic corporate answers reject external gateway", delegate
            { Rejected(ContextDecision(Request(), Snapshot(), "portal.corp.example.test", "198.51.100.1")); });
            Test("dynamic corporate answers reject off-link internet DNS", delegate
            { Rejected(ContextDecision(Request(), Snapshot(), "portal.corp.example.test", "203.0.113.53")); });
            Test("dynamic corporate answers canonicalize DNS before protection", delegate
            { Rejected(ContextDecision(Request(), Snapshot(), "portal.corp.example.test", "203.000.113.053")); });
            Test("dynamic corporate answers protect canonicalized internet DNS snapshot", delegate
            {
                NetworkSnapshot s = Snapshot(); s.Adapters[1].DnsServers[0] = "203.000.113.053";
                Rejected(ContextDecision(Request(), s, "portal.corp.example.test", "203.0.113.53"));
            });
            Test("dynamic corporate answer cannot route this machine address", delegate
            { Rejected(ContextDecision(Request(), Snapshot(), "portal.corp.example.test", "192.0.2.20")); });
            Test("dynamic corporate answer cannot route another local interface address", delegate
            {
                NetworkSnapshot s = Snapshot(); s.Adapters.Add(new AdapterSnapshot { Id = "other-interface", Address = "10.20.0.99" });
                Rejected(ContextDecision(Request(), s, "portal.corp.example.test", "10.20.0.99"));
            });
            Test("dynamic internet answer cannot capture corporate DNS", delegate
            { Rejected(ContextDecision(Request(), Snapshot(), "cdn.notes.example.test", "192.0.2.53")); });
            Test("dynamic internet answer cannot capture corporate gateway", delegate
            { Rejected(ContextDecision(Request(), Snapshot(), "cdn.notes.example.test", "192.0.2.1")); });
            Test("dynamic internet answer cannot capture corporate local address", delegate
            { Rejected(ContextDecision(Request(), Snapshot(), "cdn.notes.example.test", "192.0.2.20")); });
            Test("mixed dynamic answers reject whole set without partial route leakage", delegate
            { Rejected(ContextDecision(Request(), Snapshot(), "portal.corp.example.test", "10.20.0.50", "203.0.113.53")); });
            Test("dynamic answers retain cleanup transition gate", delegate
            { NetworkSnapshot s = Snapshot(); s.CleanupPending = true; Rejected(ContextDecision(Request(), s, "portal.corp.example.test", "10.20.0.50")); });
            Test("dynamic answers retain network stability gate", delegate
            { NetworkSnapshot s = Snapshot(); s.StableSeconds = 7; Rejected(ContextDecision(Request(), s, "portal.corp.example.test", "10.20.0.50")); });
            Test("dynamic answers refuse stale removed company interface", delegate
            { NetworkSnapshot s = Snapshot(); s.Adapters.RemoveAt(0); Rejected(ContextDecision(Request(), s, "portal.corp.example.test", "10.20.0.50")); });
            Test("dynamic answers cannot start dual mode over old FastLink mode", delegate
            { ModeRequest r = Request(); r.Mode = WorkMode.FastLinkSplit; Rejected(ContextDecision(r, Snapshot(), "portal.corp.example.test", "10.20.0.50")); });
            Test("dynamic answers cannot start dual mode over normal mode", delegate
            { ModeRequest r = Request(); r.Mode = WorkMode.Normal; Rejected(ContextDecision(r, Snapshot(), "portal.corp.example.test", "10.20.0.50")); });
            Test("dynamic reversed roles remain valid without Wi-Fi assumptions", delegate
            {
                ModeRequest r = Request(); NetworkSnapshot s = Snapshot(); r.Mode = WorkMode.WifiCorporateWiredInternet;
                s.Adapters[0].Kind = LinkKind.Wifi; s.Adapters[1].Kind = LinkKind.Ethernet;
                Routed(ContextDecision(r, s, "portal.corp.example.test", "10.20.0.50"), RuleTarget.Corporate);
            });
            Test("unsafe literal host rejected without relying on DNS answer", delegate
            {
                foreach (string host in new[] { "127.0.0.1", "198.18.0.1", "198.19.0.1", "169.254.1.1" })
                    Rejected(Decision(null, host));
            });
            Test("literal IP request cannot acquire unrelated resolved address", delegate
            { Rejected(Decision(new[] { Rule("10.20.0.50", RuleTarget.Corporate) }, "10.20.0.50", "10.20.0.51")); });
            Test("dynamic rules cannot differ from the validated request revision", delegate
            {
                ModeRequest r = Request(); List<ModeRule> stale = new List<ModeRule> { Rule("portal.corp.example.test", RuleTarget.Internet) };
                Rejected(RuleRouter.Decide(stale, "portal.corp.example.test", new List<string> { "10.20.0.50" }, r, Snapshot()));
            });
            Test("equivalent rules permit casing order and duplicate normalization", delegate
            {
                ModeRequest r = Request(); List<ModeRule> equivalent = new List<ModeRule>
                {
                    Rule(".NOTES.EXAMPLE.TEST", RuleTarget.Internet),
                    Rule("PORTAL.CORP.EXAMPLE.TEST", RuleTarget.Corporate),
                    Rule("portal.corp.example.test", RuleTarget.Corporate)
                };
                Routed(RuleRouter.Decide(equivalent, "portal.corp.example.test", new List<string> { "10.20.0.50" }, r, Snapshot()), RuleTarget.Corporate);
            });
        }

        private static bool Covers(IEnumerable<string> routes, string ip)
        {
            uint value; string canonical;
            Assert(PlanningValidation.TryIpv4(ip, out value, out canonical), "invalid test IP");
            foreach (string cidr in routes)
            {
                uint first, last;
                Assert(PlanningValidation.TryCidr(cidr, out first, out last, out canonical), "invalid output CIDR");
                if (value >= first && value <= last) return true;
            }
            return false;
        }

        private static void PreparationAndCidrTests()
        {
            Test("preparation skips only admission and already-preferred default without mutating input", delegate
            {
                ModeRequest r = Request(); NetworkSnapshot s = Snapshot();
                s.CorporateAdmissionVerified = false; s.ExternalDefaultPreferred = false;
                string before = State(r, s);
                ModePlan p = NetworkModePlanner.BuildPreparation(r, s);
                Assert(p.CanSimulate && p.Status == "ReadyForPreparation" && p.Intents.Any(i => i.Kind == "CorporateCidrRoute"), "preparation blocked before authentication path exists");
                Blocked(NetworkModePlanner.Build(r, s));
                Assert(before == State(r, s) && !s.CorporateAdmissionVerified && !s.ExternalDefaultPreferred, "preparation faked admission or changed input");
            });
            Test("preparation retains proxy policy role DNS stability and permission gates", delegate
            {
                foreach (Action<NetworkSnapshot> mutate in new Action<NetworkSnapshot>[] {
                    s => s.SystemProxyActive = true, s => s.TunnelActive = true, s => s.CleanupPending = true,
                    s => s.OtherDnsPolicyPresent = true, s => s.DualConnectionAllowed = false,
                    s => s.InternetReachabilityVerified = false, s => s.StableSeconds = 7,
                    s => s.Adapters[0].IsPhysical = false, s => s.Adapters[1].Kind = LinkKind.Ethernet,
                    s => s.Adapters[0].DnsServers.Clear(), s => s.Adapters[1].Gateway = "203.0.113.1",
                    s => s.Adapters[1].HasIpv6Default = true })
                {
                    NetworkSnapshot s = Snapshot(); s.CorporateAdmissionVerified = false; s.ExternalDefaultPreferred = false; mutate(s);
                    Blocked(NetworkModePlanner.BuildPreparation(Request(), s));
                }
                ModeRequest r = Request(); r.Rules[0].Target = RuleTarget.Review;
                Blocked(NetworkModePlanner.BuildPreparation(r, Snapshot()));
                NetworkSnapshot absent = Snapshot(); absent.Adapters[0].IsUp = false;
                Waiting(NetworkModePlanner.BuildPreparation(Request(), absent));
            });
            Test("explicit ten slash eight excludes external LAN and corporate local IP exactly", delegate
            {
                ModeRequest r = Request(); r.CorporateCidrs = new List<string> { "10.0.0.0/8" };
                NetworkSnapshot s = Snapshot();
                s.Adapters[0].Address = "10.44.1.98"; s.Adapters[0].PrefixLength = 21; s.Adapters[0].Gateway = "10.44.0.1";
                s.Adapters[0].DnsServers = new List<string> { "10.88.2.53", "10.99.161.53" };
                s.Adapters[1].Address = "10.77.9.121"; s.Adapters[1].Gateway = "10.77.9.1";
                s.Adapters[1].DnsServers = new List<string> { "8.8.8.8" };
                string before = State(r, s); CorporateCidrPlan split = CorporateCidrPlanner.Build(r, s);
                Assert(split.IsValid && split.Routes.Count > 1 && split.Routes.Count <= CorporateCidrPlanner.MaximumRouteCount, "valid broad range blocked");
                ulong coverage = 0; uint previousLast = 0; bool seen = false;
                foreach (string route in split.Routes)
                {
                    uint first, last; string canonical;
                    Assert(PlanningValidation.TryCidr(route, out first, out last, out canonical) && canonical == route, "noncanonical range");
                    Assert(first >= 0x0A000000U && last <= 0x0AFFFFFFU && (!seen || first > previousLast), "overlap or broadened range");
                    coverage += (ulong)last - first + 1; previousLast = last; seen = true;
                }
                Assert(coverage == 16777216UL - 256UL - 1UL, "subtraction removed or retained unintended addresses");
                Assert(!Covers(split.Routes, "10.77.9.0") && !Covers(split.Routes, "10.77.9.255") && !Covers(split.Routes, "10.44.1.98") && !Covers(split.Routes, "8.8.8.8"), "protected infrastructure covered");
                Assert(Covers(split.Routes, "10.66.16.104") && Covers(split.Routes, "10.88.2.53"), "company service or DNS lost");
                Ready(NetworkModePlanner.Build(r, s));
                Rejected(ContextDecision(r, s, "portal.corp.example.test", "10.77.9.1"));
                Assert(before == State(r, s) && r.CorporateCidrs.Single() == "10.0.0.0/8", "request changed during split");
                Console.WriteLine("CORPORATE_CIDR_FIXTURE_ROUTES " + split.Routes.Count);
            });
            Test("explicit internet IP and off-link DNS holes never enter corporate routes", delegate
            {
                ModeRequest r = Request(); r.Rules.Add(Rule("10.20.0.77", RuleTarget.Internet));
                NetworkSnapshot s = Snapshot(); s.Adapters[1].DnsServers = new List<string> { "10.20.0.53" };
                CorporateCidrPlan split = CorporateCidrPlanner.Build(r, s);
                Assert(split.IsValid && !Covers(split.Routes, "10.20.0.77") && !Covers(split.Routes, "10.20.0.53") && Covers(split.Routes, "10.20.0.78"), "explicit internet holes not preserved");
            });
            Test("subtraction merges duplicates and adjacent original ranges minimally", delegate
            {
                ModeRequest r = Request(); r.CorporateCidrs = new List<string> { "10.20.0.0/25", "10.20.0.128/25", "10.20.0.77/25" };
                CorporateCidrPlan split = CorporateCidrPlanner.Build(r, Snapshot());
                Assert(split.IsValid && split.Routes.SequenceEqual(new[] { "10.20.0.0/24" }), "union is not minimal or deduplicated");
            });
            Test("fragmentation above route limit fails closed with no partial routes", delegate
            {
                ModeRequest r = Request(); r.CorporateCidrs = new List<string> { "10.0.0.0/8" };
                for (int i = 0; i < 100; i++) r.Rules.Add(Rule("10.30." + i + ".77", RuleTarget.Internet));
                CorporateCidrPlan split = CorporateCidrPlanner.Build(r, Snapshot());
                Assert(!split.IsValid && split.Errors.Count > 0 && split.Routes.Count == 0, "over-budget partial route plan escaped");
            });
        }

        public static int Main()
        {
            PlanTests(); RouterTests(); InfrastructureProtectionTests(); PreparationAndCidrTests();
            if (failures != 0) { Console.WriteLine("NETWORK_MODE_PLANNING_TESTS_FAILED " + failures + "/" + count); return 1; }
            Console.WriteLine("NETWORK_MODE_PLANNING_TESTS_OK " + count);
            return 0;
        }
    }
}
