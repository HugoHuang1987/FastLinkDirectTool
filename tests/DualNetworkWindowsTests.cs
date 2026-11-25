using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using FastLinkDirectTool.Modes;

namespace FastLinkDirectTool.Tests
{
    internal static class DualNetworkWindowsTests
    {
        private const string Corp = "11111111-1111-1111-1111-111111111111";
        private const string Internet = "22222222-2222-2222-2222-222222222222";
        private const string Owner = "FastLinkDirectTool.DualNetwork:33333333-3333-3333-3333-333333333333";
        private static int count, failed;
        private static readonly List<string> scripts = new List<string>();
        private static void Assert(bool value, string text) { if (!value) throw new Exception(text); }
        private static void Throws(Action action) { bool threw = false; try { action(); } catch { threw = true; } Assert(threw, "expected rejection"); }
        private static void Test(string name, Action test)
        { count++; try { test(); Console.WriteLine("PASS " + name); } catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); } }
        private static DualCommandResult Json(object value)
        { return new DualCommandResult { Output = "DUAL_JSON:" + new JavaScriptSerializer().Serialize(value) }; }
        private static DualCommandResult Value(string value) { return Json(new { Value = value }); }

        private static DualCommandResult CaptureJson(int corporateIndex, string corporateAddress, bool ipv6, bool proxy)
        {
            return Json(new
            {
                Adapters = new[]
                {
                    new { Id=Corp,Name="Corp",Index=corporateIndex,Kind="Ethernet",IsUp=true,IsPhysical=true,Address=corporateAddress,PrefixLength=24,Gateway="192.0.2.1",DnsServers=new[]{"192.0.2.53"},HasIpv6Default=ipv6,Ipv6RouteSignature=ipv6?"::/0|fe80::1|256|25":"",Metric=55,Automatic=1 },
                    new { Id=Internet,Name="Internet",Index=20,Kind="Wifi",IsUp=true,IsPhysical=true,Address="198.51.100.20",PrefixLength=24,Gateway="198.51.100.1",DnsServers=new[]{"203.0.113.53"},HasIpv6Default=false,Ipv6RouteSignature="",Metric=5,Automatic=0 }
                },
                Defaults = new[] { new { Id=Corp,Cost=60L },new {Id=Internet,Cost=10L}},
                SystemProxyActive=proxy,TunnelActive=false,OtherDnsPolicyPresent=false
            });
        }

        private static Dictionary<string, object> ModeThreeIpv6Capture(string signature)
        {
            DualCommandResult original = CaptureJson(10, "192.0.2.20", false, false);
            Dictionary<string, object> raw = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(original.Output.Substring("DUAL_JSON:".Length));
            object[] adapters = (object[])raw["Adapters"];
            ((Dictionary<string, object>)adapters[0])["Kind"] = "Wifi";
            Dictionary<string, object> internet = (Dictionary<string, object>)adapters[1];
            internet["Kind"] = "Ethernet"; internet["HasIpv6Default"] = true; internet["Ipv6RouteSignature"] = signature;
            return raw;
        }

        private sealed class Fake
        {
            internal readonly Queue<DualCommandResult> Results = new Queue<DualCommandResult>();
            internal readonly List<string> Commands = new List<string>();
            internal bool Admin = true;
            internal DateTime Now = new DateTime(2026, 11, 24, 1, 0, 0, DateTimeKind.Utc);
            internal readonly DualNetworkWindowsPlatform Platform;
            internal Fake()
            {
                Platform = new DualNetworkWindowsPlatform(delegate(string command, int timeout)
                {
                    Assert(timeout >= 1000 && timeout <= 20000, "unbounded timeout");
                    Commands.Add(command); scripts.Add(command);
                    Assert(Results.Count > 0, "unexpected command execution"); return Results.Dequeue();
                }, delegate { return Admin; }, delegate { return Now; }, "fake-machine-user");
                Platform.CorporateAdapterId = Corp; Platform.InternetAdapterId = Internet;
            }
            internal void Capture() { Results.Enqueue(CaptureJson(10, "192.0.2.20", false, false)); Platform.Capture(); }
        }

        private static DualResource Metric() { return new DualResource { Kind=DualResourceKind.Metric4, InterfaceId=Corp }; }
        private static DualResource Route(string next, string destination)
        { return new DualResource { Kind=DualResourceKind.Route4, InterfaceId=Corp, NextHop=next, Destination=destination }; }
        private static DualResource Nrpt() { return new DualResource { Kind=DualResourceKind.Nrpt, Namespace=".corp.example.test", Owner=Owner }; }

        private static void PlatformTests()
        {
            Test("construction is inert and injected scope is stable", delegate { Fake f = new Fake(); Assert(f.Commands.Count == 0 && f.Platform.ScopeIdentity == "fake-machine-user", "construction performed IO"); });
            Test("capture gates remain unconfirmed and IPv6 unverified", delegate
            { Fake f = new Fake(); f.Results.Enqueue(CaptureJson(10,"192.0.2.20",true,true)); NetworkSnapshot s=f.Platform.Capture(); Assert(!s.CorporateAdmissionVerified&&!s.DualConnectionAllowed&&!s.InternetReachabilityVerified&&!s.Ipv6SafetyVerified&&s.SystemProxyActive&&s.Adapters[0].HasIpv6Default,"invented confirmation"); });
            Test("stable signature starts at zero and reaches eight seconds", delegate
            { Fake f = new Fake(); f.Capture(); f.Now=f.Now.AddSeconds(8); f.Results.Enqueue(CaptureJson(10,"192.0.2.20",false,false)); Assert(f.Platform.Capture().StableSeconds==8,"stable time wrong"); });
            Test("ifIndex reuse or address change resets stability", delegate
            { Fake f = new Fake(); f.Capture(); f.Now=f.Now.AddSeconds(30); f.Results.Enqueue(CaptureJson(11,"192.0.2.21",false,false)); Assert(f.Platform.Capture().StableSeconds==0,"changed identity treated stable"); });
            Test("changing assigned role resets stability", delegate
            { Fake f=new Fake();f.Capture();f.Now=f.Now.AddSeconds(30);f.Platform.CorporateAdapterId=Internet;f.Results.Enqueue(CaptureJson(10,"192.0.2.20",false,false));Assert(f.Platform.Capture().StableSeconds==0,"roles not in signature"); });
            Test("confirmation flags and observed external preference are separate", delegate
            { Fake f=new Fake();f.Platform.CorporateAdmissionConfirmed=true;f.Platform.DualConnectionConfirmed=true;f.Platform.InternetConfirmed=true;f.Results.Enqueue(CaptureJson(10,"192.0.2.20",false,false));NetworkSnapshot s=f.Platform.Capture();Assert(s.CorporateAdmissionVerified&&s.DualConnectionAllowed&&!s.InternetReachabilityVerified&&s.ExternalDefaultPreferred,"permission or preference was confused with HTTPS proof"); });
            Test("Internet proof requires a captured topology and remains read-only", delegate
            {
                Fake f = new Fake(); Throws(delegate { f.Platform.ConfirmInternetForCurrentTopology(); });
                f.Capture(); int commands = f.Commands.Count; f.Platform.ConfirmInternetForCurrentTopology();
                Assert(f.Commands.Count == commands, "proof changed network or probed again");
                f.Now = f.Now.AddSeconds(8); f.Results.Enqueue(CaptureJson(10, "192.0.2.20", false, false));
                Assert(f.Platform.Capture().InternetReachabilityVerified, "fresh proof rejected");
            });
            Test("Internet proof expires before first use but not during unchanged session", delegate
            {
                Fake f = new Fake(); f.Capture(); f.Platform.ConfirmInternetForCurrentTopology();
                f.Now = f.Now.AddSeconds(61); f.Results.Enqueue(CaptureJson(10, "192.0.2.20", false, false));
                Assert(!f.Platform.Capture().InternetReachabilityVerified, "stale unused proof accepted");
                f.Platform.ConfirmInternetForCurrentTopology();
                f.Now = f.Now.AddSeconds(1); f.Capture();
                f.Now = f.Now.AddHours(3); f.Results.Enqueue(CaptureJson(10, "192.0.2.20", false, false));
                Assert(f.Platform.Capture().InternetReachabilityVerified, "ongoing unchanged session lost proof by age alone");
            });
            Test("cleared Internet proof cannot be revived by setting checkbox flag", delegate
            {
                Fake f = new Fake(); f.Capture(); f.Platform.ConfirmInternetForCurrentTopology(); f.Capture();
                f.Platform.InternetConfirmed = false; f.Platform.InternetConfirmed = true;
                f.Results.Enqueue(CaptureJson(10, "192.0.2.20", false, false));
                Assert(!f.Platform.Capture().InternetReachabilityVerified, "old proof revived without new HTTPS check");
            });
            Test("changed topology invalidates Internet proof even when old topology returns", delegate
            {
                Fake f = new Fake(); f.Capture(); f.Platform.ConfirmInternetForCurrentTopology(); f.Capture();
                f.Results.Enqueue(CaptureJson(10, "192.0.2.21", false, false));
                Assert(!f.Platform.Capture().InternetReachabilityVerified, "changed address retained proof");
                f.Results.Enqueue(CaptureJson(10, "192.0.2.20", false, false));
                Assert(!f.Platform.Capture().InternetReachabilityVerified, "return to old topology revived proof");
            });
            Test("proxy conflict or unreadable snapshot permanently invalidates Internet proof", delegate
            {
                foreach (bool unreadable in new[] { false, true })
                {
                    Fake f = new Fake(); f.Capture(); f.Platform.ConfirmInternetForCurrentTopology(); f.Capture();
                    if (unreadable)
                    { f.Results.Enqueue(new DualCommandResult { TimedOut = true }); Throws(delegate { f.Platform.Capture(); }); }
                    else
                    { f.Results.Enqueue(CaptureJson(10, "192.0.2.20", false, true)); Assert(!f.Platform.Capture().InternetReachabilityVerified, "proxy conflict retained proof"); }
                    f.Results.Enqueue(CaptureJson(10, "192.0.2.20", false, false));
                    Assert(!f.Platform.Capture().InternetReachabilityVerified, "uncertain interval revived proof");
                }
            });
            Test("clock reversal invalidates consumed proof without periodic probes", delegate
            {
                Fake f = new Fake(); f.Capture(); f.Platform.ConfirmInternetForCurrentTopology(); f.Capture();
                f.Now = f.Now.AddMinutes(5); f.Capture(); f.Now = f.Now.AddMinutes(-1);
                f.Results.Enqueue(CaptureJson(10, "192.0.2.20", false, false));
                NetworkSnapshot s = f.Platform.Capture();
                Assert(!s.InternetReachabilityVerified && s.StableSeconds == 0, "clock reversal retained trusted interval");
            });
            Test("capture script is read-only and never prints credentials", delegate
            { string s=DualNetworkWindowsPlatform.BuildCaptureScript(new string[0]);scripts.Add(s);foreach(string bad in new[]{"Set-Net","New-Net","Remove-Net","Add-Dns","Stop-Process","Start-Process","ProxyServer","password","Credential"})Assert(s.IndexOf(bad,StringComparison.OrdinalIgnoreCase)<0,"unexpected capture capability "+bad);Assert(s.Contains("Get-NetAdapter -Physical")&&s.Contains("PhysicalAdapter=True")&&s.Contains("Get-DnsClientNrptPolicy -Effective"),"incomplete physical/policy snapshot"); });
            Test("generic virtual broad routes and IPv6 are present in read-only capture logic", delegate
            { string s=DualNetworkWindowsPlatform.BuildCaptureScript(new string[0]);Assert(s.Contains("'0.0.0.0/0','0.0.0.0/1','128.0.0.0/1'")&&s.Contains("$physicalIds -notcontains")&&s.Contains("$broad -contains"),"generic split default not checked");Assert(s.Contains("Kind='Other'")&&s.Contains("HasIpv6Default=$true")&&s.Contains("IsPhysical=$false"),"virtual IPv6 evidence omitted"); });
            Test("nonphysical IPv6 evidence stays blocked and non-selectable", delegate
            {
                Fake f=new Fake();DualCommandResult original=CaptureJson(10,"192.0.2.20",false,false);
                Dictionary<string,object> raw=(Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(original.Output.Substring("DUAL_JSON:".Length));
                List<object> adapters=((object[])raw["Adapters"]).ToList();adapters.Add(new {Id="44444444-4444-4444-4444-444444444444",Name="Generic Adapter",Index=30,Kind="Other",IsUp=true,IsPhysical=false,Address="",PrefixLength=0,Gateway="",DnsServers=new string[0],HasIpv6Default=true,Ipv6RouteSignature="::/0|fe80::1|256|25",Metric=0,Automatic=0});
                raw["Adapters"]=adapters;raw["TunnelActive"]=true;f.Results.Enqueue(Json(raw));NetworkSnapshot s=f.Platform.Capture();
                Assert(s.TunnelActive&&s.Adapters.Count(a=>a.IsPhysical)==2&&s.Adapters.Any(a=>!a.IsPhysical&&a.HasIpv6Default),"virtual evidence lost or offered as physical role");
            });
            Test("mode three observed external IPv6 is derived not user confirmed", delegate
            {
                Fake f = new Fake(); f.Results.Enqueue(Json(ModeThreeIpv6Capture("::/0|fe80::1|256|25")));
                NetworkSnapshot s = f.Platform.Capture();
                Assert(s.Ipv6SafetyVerified && s.Adapters[1].Ipv6RouteSignature == "::/0|fe80::1|256|25" && !s.DualConnectionAllowed, "observed evidence or permission gate lost");
            });
            Test("IPv6 gateway metric and split default changes reset stability", delegate
            {
                foreach (string changed in new[] { "::/0|fe80::2|256|25", "::/0|fe80::1|255|25", "::/0|fe80::1|256|30", "::/1|fe80::1|256|25;8000::/1|fe80::1|256|25" })
                {
                    Fake f = new Fake(); f.Results.Enqueue(Json(ModeThreeIpv6Capture("::/0|fe80::1|256|25"))); f.Platform.Capture();
                    f.Now = f.Now.AddSeconds(30); f.Results.Enqueue(Json(ModeThreeIpv6Capture(changed)));
                    Assert(f.Platform.Capture().StableSeconds == 0, "changed IPv6 evidence remained stable");
                }
            });
            Test("missing malformed IPv6 evidence is not a safe default", delegate
            {
                foreach (string bad in new[] { null, "", "confirmed", "::/0|192.0.2.1|1|25", "::/0|fe80::1|1|0" })
                { Fake f = new Fake(); f.Results.Enqueue(Json(ModeThreeIpv6Capture(bad))); Throws(delegate { f.Platform.Capture(); }); }
                Dictionary<string, object> raw = ModeThreeIpv6Capture("::/0|fe80::1|256|25");
                ((Dictionary<string, object>)((object[])raw["Adapters"])[1]).Remove("HasIpv6Default");
                Fake missing = new Fake(); missing.Results.Enqueue(Json(raw)); Throws(delegate { missing.Platform.Capture(); });
            });
            Test("IPv6 broad route capture rejects unmapped interfaces and retains signatures", delegate
            {
                string s = DualNetworkWindowsPlatform.BuildCaptureScript(new string[0]); scripts.Add(s);
                Assert(s.Contains("'::/0','::/1','8000::/1','2000::/3'") && s.Contains("ipv6_route_interface_unknown") && s.Contains("ipv6_route_metric_unknown"), "broad or unknown IPv6 routes ignored");
                Assert(s.Contains("Ipv6RouteSignature=$signature6") && s.Contains("Sort-Object DestinationPrefix,NextHop,RouteMetric"), "deterministic IPv6 evidence missing");
            });
            Test("effective NRPT must match actual sole loopback resolver", delegate
            { string s=DualNetworkWindowsPlatform.BuildCaptureScript(new[]{Owner});Assert(s.Contains("$policy.NameServers")&&s.Contains("$effectiveServers.Count -eq 1")&&s.Contains("$effectiveServers[0] -ceq '127.0.0.1'")&&s.Contains("-or -not $effectiveOwned"),"effective GPO resolver can hide behind local marker"); });
            Test("snapshot timeout is not an empty successful network", delegate { Fake f=new Fake();f.Results.Enqueue(new DualCommandResult{TimedOut=true});Throws(delegate{f.Platform.Capture();}); });
            Test("command failure is not absence", delegate { Fake f=new Fake();f.Results.Enqueue(new DualCommandResult{ExitCode=1});Throws(delegate{f.Platform.Read(Nrpt());}); });
            Test("missing or duplicate completion marker rejected", delegate
            { Fake f=new Fake();f.Results.Enqueue(new DualCommandResult{Output="{}"});Throws(delegate{f.Platform.Capture();});f.Results.Enqueue(new DualCommandResult{Output="DUAL_JSON:{}\nDUAL_JSON:{}"});Throws(delegate{f.Platform.Capture();}); });
            Test("administrator failure performs no command or escalation", delegate
            { Fake f=new Fake();f.Admin=false;Throws(delegate{f.Platform.Write(Metric(),"automatic=0;metric=5");});Throws(delegate{f.Platform.Remove(Nrpt());});Assert(f.Commands.Count==0,"non-admin executed mutation"); });
            Test("resource writes need a prior observed read", delegate
            { Fake f=new Fake();f.Capture();int n=f.Commands.Count;Throws(delegate{f.Platform.Write(Metric(),"automatic=0;metric=5");});Assert(f.Commands.Count==n,"unobserved write executed"); });
            Test("metric write preserves automatic flag and compare-before-write", delegate
            { Fake f=new Fake();f.Capture();f.Results.Enqueue(Value("automatic=1;metric=55"));Assert(f.Platform.Read(Metric())=="automatic=1;metric=55","read metric lost");f.Results.Enqueue(Value("automatic=0;metric=5"));f.Platform.Write(Metric(),"automatic=0;metric=5");string s=f.Commands.Last();Assert(s.Contains("metric_concurrent_change")&&s.Contains("-AutomaticMetric Disabled")&&s.Contains("-PolicyStore ActiveStore")&&s.Contains("$ix=10"),"missing atomic identity or value guards"); });
            Test("metric restore uses original automatic setting", delegate
            { Fake f=new Fake();f.Capture();f.Results.Enqueue(Value("automatic=0;metric=5"));f.Platform.Read(Metric());f.Results.Enqueue(Value("automatic=1;metric=55"));f.Platform.Write(Metric(),"automatic=1;metric=55");string s=f.Commands.Last();Assert(s.Contains("-AutomaticMetric Enabled")&&!s.Contains("-InterfaceMetric"),"automatic restoration must not also set a manual metric"); });
            Test("both incident automatic baselines restore policy without old numeric metric", delegate
            {
                foreach (string baseline in new[] { "automatic=1;metric=5", "automatic=1;metric=35" })
                {
                    Fake f=new Fake();f.Capture();f.Results.Enqueue(Value("automatic=0;metric=55"));f.Platform.Read(Metric());
                    f.Results.Enqueue(Value("automatic=1;metric=65"));f.Platform.Write(Metric(),baseline);
                    string s=f.Commands.Last();
                    Assert(s.Contains("Set-NetIPInterface -InterfaceIndex $ix -AddressFamily IPv4 -PolicyStore ActiveStore -AutomaticMetric Enabled -ErrorAction Stop")&&!s.Contains("-InterfaceMetric"),"conflicting automatic/manual parameters generated");
                    Assert(s.Contains("metric_concurrent_change")&&s.Contains("$v=Read-Current"),"automatic restore skipped concurrency or readback guards");
                }
            });
            Test("manual baseline restoration still sets disabled and exact metric", delegate
            {
                Fake f=new Fake();f.Capture();f.Results.Enqueue(Value("automatic=0;metric=5"));f.Platform.Read(Metric());
                f.Results.Enqueue(Value("automatic=0;metric=35"));f.Platform.Write(Metric(),"automatic=0;metric=35");
                string s=f.Commands.Last();
                Assert(s.Contains("-AutomaticMetric Disabled -InterfaceMetric 35 -ErrorAction Stop")&&!s.Contains("-AutomaticMetric Enabled"),"manual restoration lost its exact policy/value");
            });
            Test("automatic metric restore accepts legitimate recomputation", delegate
            { Fake f=new Fake();f.Capture();f.Results.Enqueue(Value("automatic=0;metric=5"));f.Platform.Read(Metric());f.Results.Enqueue(Value("automatic=1;metric=65"));f.Platform.Write(Metric(),"automatic=1;metric=55");Assert(f.Commands.Last().Contains("^automatic=1;metric="),"CAS automatic semantics not preserved"); });
            Test("metric restore and preparation do not require connected IP", delegate
            { Fake f=new Fake();DualCommandResult capture=CaptureJson(10,"",false,false);capture.Output=capture.Output.Replace("\"IsUp\":true","\"IsUp\":false");f.Results.Enqueue(capture);f.Platform.Capture();f.Results.Enqueue(Value("automatic=0;metric=5"));f.Platform.Read(Metric());f.Results.Enqueue(Value("automatic=1;metric=55"));f.Platform.Write(Metric(),"automatic=1;metric=55");string s=f.Commands.Last();Assert(!s.Contains("Get-NetIPAddress")&&!s.Contains("interface_disconnected")&&s.Contains("physical_identity_unverified"),"metric restore still depends on connection"); });
            Test("post-crash resource read bootstraps bindings with read-only capture", delegate
            { Fake f=new Fake();f.Results.Enqueue(CaptureJson(10,"192.0.2.20",false,false));f.Results.Enqueue(Value("automatic=0;metric=5"));Assert(f.Platform.Read(Metric())=="automatic=0;metric=5"&&f.Commands.Count==2,"cold cleanup did not load identity");Assert(!f.Commands[0].Contains("Set-Net"),"cleanup bootstrap mutated network"); });
            Test("metric cannot be removed", delegate { Fake f=new Fake();Throws(delegate{f.Platform.Remove(Metric());});Assert(f.Commands.Count==0,"metric removal executed"); });
            Test("write readback mismatch fails closed", delegate
            { Fake f=new Fake();f.Capture();f.Results.Enqueue(Value("automatic=1;metric=55"));f.Platform.Read(Metric());f.Results.Enqueue(Value("automatic=0;metric=9"));Throws(delegate{f.Platform.Write(Metric(),"automatic=0;metric=5");}); });
            Test("route add uses exact tuple and never overwrites raced route", delegate
            { Fake f=new Fake();f.Capture();DualResource r=Route("192.0.2.1","10.20.0.50/32");f.Results.Enqueue(Value(null));f.Platform.Read(r);f.Results.Enqueue(Value("metric=1"));f.Platform.Write(r,"metric=1");string s=f.Commands.Last();Assert(s.Contains("existing_route_not_owned")&&s.Contains("-NextHop $next")&&s.Contains("-DestinationPrefix $destination")&&s.Contains("interface_identity_changed")&&s.Contains("interface_address_changed"),"incomplete route safety"); });
            Test("route writes recheck current gateway and full DNS set", delegate
            { Fake f=new Fake();f.Capture();DualResource r=Route("192.0.2.1","10.20.0.50/32");f.Results.Enqueue(Value(null));f.Platform.Read(r);string s=f.Platform.BuildResourceScript(r,"write","metric=1");scripts.Add(s);Assert(s.Contains("interface_gateway_changed")&&s.Contains("interface_dns_changed")&&s.Contains("Sort-Object -Unique")&&s.Contains("Get-DnsClientServerAddress -InterfaceIndex $ix"),"write uses stale gateway/DNS"); });
            Test("existing routes can be observed but not claimed", delegate
            { Fake f=new Fake();f.Capture();DualResource r=Route("192.0.2.1","10.20.0.50/32");f.Results.Enqueue(Value("metric=99"));Assert(f.Platform.Read(r)=="metric=99","route metric lost");int n=f.Commands.Count;Throws(delegate{f.Platform.Write(r,"metric=1");});Assert(f.Commands.Count==n,"existing route overwritten"); });
            Test("route reads and writes reject a competing exact destination", delegate
            { Fake f=new Fake();f.Capture();DualResource r=Route("192.0.2.1","10.20.0.50/32");f.Results.Enqueue(Value(null));f.Platform.Read(r);string read=f.Commands.Last();Assert(read.Contains("conflicting_destination_route")&&read.Contains("$_.InterfaceIndex -ne $ix -or $_.NextHop -ne $next"),"other paths were ignored");string write=f.Platform.BuildResourceScript(r,"write","metric=1");scripts.Add(write);Assert(write.Contains("conflicting_destination_route"),"write race missed competing path"); });
            Test("malformed value envelope is never treated as absence", delegate
            { Fake f=new Fake();f.Results.Enqueue(new DualCommandResult{Output="DUAL_JSON:{}"});Throws(delegate{f.Platform.Read(Nrpt());}); });
            Test("route removal binds exact tuple and observed metric", delegate
            { Fake f=new Fake();f.Capture();DualResource r=Route("192.0.2.1","10.20.0.50/32");f.Results.Enqueue(Value("metric=1"));f.Platform.Read(r);f.Results.Enqueue(Value(null));f.Platform.Remove(r);string s=f.Commands.Last();Assert(s.Contains("route_concurrent_change")&&s.Contains("$r[0] | Remove-NetRoute")&&!s.Contains("0.0.0.0/0"),"broad route removal"); });
            Test("on-link route accepted only wholly inside confirmed subnet", delegate
            { Fake f=new Fake();f.Capture();DualResource r=Route("0.0.0.0","192.0.2.53/32");f.Results.Enqueue(Value(null));f.Platform.Read(r);f.Results.Enqueue(Value("metric=1"));f.Platform.Write(r,"metric=1");DualResource bad=Route("0.0.0.0","10.20.0.0/24");Throws(delegate{f.Platform.BuildResourceScript(bad,"write","metric=1");}); });
            Test("default route, fake-IP and command injection rejected before runner", delegate
            { Fake f=new Fake();foreach(DualResource r in new[]{Route("192.0.2.1","0.0.0.0/0"),Route("192.0.2.1","198.18.0.0/15"),Route("x';Stop-Process x;#","10.20.0.50/32"),new DualResource{Kind=DualResourceKind.Metric4,InterfaceId="1;evil"}})Throws(delegate{f.Platform.Read(r);});Assert(f.Commands.Count==0,"invalid input reached runner"); });
            Test("NRPT owner must be session-specific and namespace bounded", delegate
            { Fake f=new Fake();DualResource r=Nrpt();r.Owner="FastLinkDirectTool.DualNetwork";Throws(delegate{f.Platform.Read(r);});r=Nrpt();r.Namespace="*';evil";Throws(delegate{f.Platform.Read(r);});Assert(f.Commands.Count==0,"invalid NRPT input reached runner"); });
            Test("NRPT add uses only loopback and exact session marker", delegate
            { Fake f=new Fake();f.Capture();DualResource r=Nrpt();f.Results.Enqueue(Value(null));f.Platform.Read(r);f.Results.Enqueue(Value("server=127.0.0.1;owner="+Owner));f.Platform.Write(r,"server=127.0.0.1;owner="+Owner);string s=f.Commands.Last();Assert(s.Contains("-NameServers '127.0.0.1'")&&s.Contains("-Comment $owner")&&s.Contains("existing_nrpt_not_claimed")&&s.Contains("interface_identity_changed"),"unsafe NRPT add"); });
            Test("NRPT write validates gateway and DNS of both roles", delegate
            { Fake f=new Fake();f.Capture();f.Results.Enqueue(Value(null));f.Platform.Read(Nrpt());string s=f.Platform.BuildResourceScript(Nrpt(),"write","server=127.0.0.1;owner="+Owner);scripts.Add(s);Assert(s.Split(new[]{"interface_dns_changed"},StringSplitOptions.None).Length==3&&s.Split(new[]{"interface_gateway_changed"},StringSplitOptions.None).Length==3,"both network roles not rechecked"); });
            Test("foreign NRPT remains observed not claimed", delegate
            { Fake f=new Fake();f.Results.Enqueue(Value("server=192.0.2.53;owner=corporate-policy"));Assert(f.Platform.Read(Nrpt())!=null,"foreign NRPT treated absent");int n=f.Commands.Count;Throws(delegate{f.Platform.Write(Nrpt(),"server=127.0.0.1;owner="+Owner);});Assert(f.Commands.Count==n,"foreign NRPT claimed"); });
            Test("NRPT cleanup precedes need for any connected interface", delegate
            { Fake f=new Fake();f.Results.Enqueue(Value("server=127.0.0.1;owner="+Owner));f.Platform.Read(Nrpt());f.Results.Enqueue(Value(null));f.Platform.Remove(Nrpt());string s=f.Commands.Last();Assert(!s.Contains("Get-NetAdapter")&&s.Contains("nrpt_ownership_conflict")&&s.Contains("-Name $r[0].Name"),"cleanup depends on live adapter or deletes broadly"); });
            Test("remove nonempty readback fails", delegate
            { Fake f=new Fake();f.Results.Enqueue(Value("server=127.0.0.1;owner="+Owner));f.Platform.Read(Nrpt());f.Results.Enqueue(Value("server=127.0.0.1;owner="+Owner));Throws(delegate{f.Platform.Remove(Nrpt());}); });
        }

        private static DualJournalDocument Document()
        { return new DualJournalDocument {Scope="fake-machine-user|mode|roles",Owner=Owner,Generation=1,Phase="Applying",Entries=new List<DualJournalEntry>{new DualJournalEntry{Resource=Nrpt(),Before=null,After="server=127.0.0.1;owner="+Owner,Applied=false}}}; }

        private static void JournalTests()
        {
            string configured=Environment.GetEnvironmentVariable("DUAL_JOURNAL_TEST_ROOT");
            string parent=Path.GetFullPath(string.IsNullOrWhiteSpace(configured)?Path.GetTempPath():configured).TrimEnd(Path.DirectorySeparatorChar);
            string directory=Path.Combine(parent,"DualNetworkWindowsTests-"+Guid.NewGuid().ToString("N"));
            string path=Path.Combine(directory,"dual-session.json");
            try
            {
                Test("journal construction and missing load do not create directory",delegate {DualFileJournal j=new DualFileJournal(path);Assert(j.Load()==null&&!Directory.Exists(directory),"missing journal was written");});
                Test("journal refuses other filenames and relative paths",delegate{Throws(delegate{new DualFileJournal("dual-session.json");});Throws(delegate{new DualFileJournal(Path.Combine(directory,"config.json"));});});
                Test("journal atomically saves prewrite and replacement states",delegate{DualFileJournal j=new DualFileJournal(path);DualJournalDocument d=Document();j.Save(d);Assert(!j.Load().Entries[0].Applied,"prewrite marker lost");d.Entries[0].Applied=true;d.Phase="Active";j.Save(d);Assert(j.Load().Phase=="Active"&&Directory.GetFiles(directory).Length==1,"replace or cleanup incomplete");});
                Test("journal cannot overwrite foreign session or scope",delegate{DualFileJournal j=new DualFileJournal(path);DualJournalDocument d=Document();d.Scope="another-user";Throws(delegate{j.Save(d);});Assert(j.Load().Scope==Document().Scope,"foreign scope overwritten");});
                Test("journal delete validates then removes only owned filename",delegate{DualFileJournal j=new DualFileJournal(path);j.Delete();Assert(j.Load()==null,"journal remains");});
                Test("corrupt journal is retained and never silently defaults",delegate{File.WriteAllText(path,"{broken");DualFileJournal j=new DualFileJournal(path);Throws(delegate{j.Load();});Throws(delegate{j.Save(Document());});Throws(delegate{j.Delete();});Assert(File.ReadAllText(path)=="{broken","corrupt ownership destroyed");});
            }
            finally
            {
                string resolved=Path.GetFullPath(directory);
                if(Path.GetDirectoryName(resolved)!=parent||!Path.GetFileName(resolved).StartsWith("DualNetworkWindowsTests-",StringComparison.Ordinal))throw new Exception("unsafe test cleanup path");
                if(Directory.Exists(resolved))Directory.Delete(resolved,true);
            }
        }

        private static void WorkerTests()
        {
            Test("worker creation and invalid inputs do not launch a process", delegate
            { using(DualPowerShellWorker w=new DualPowerShellWorker()){Assert(w.StartedProcessCount==0,"eager child");Throws(delegate{w.Execute(new string(' ',65537),1000);});Throws(delegate{w.Execute("1",0);});Assert(w.StartedProcessCount==0,"invalid input spawned worker");} });
            Test("worker reuses one process and preserves Unicode", delegate
            { using(DualPowerShellWorker w=new DualPowerShellWorker()){DualCommandResult a=w.Execute("Write-Output (20+22)",10000);DualCommandResult b=w.Execute("Write-Output '中文网卡'",10000);Assert(a.ExitCode==0&&a.Output.Trim()=="42"&&b.Output.Trim()=="中文网卡"&&w.StartedProcessCount==1,"reuse or UTF8 transport failed: "+a.ErrorCode+" / "+b.ErrorCode);} });
            Test("worker request scopes do not retain local variables or functions", delegate
            { using(DualPowerShellWorker w=new DualPowerShellWorker()){Assert(w.Execute("$transientValue=17;function Test-Transient {'leak'};Write-Output 1",10000).ExitCode==0,"setup failed");DualCommandResult r=w.Execute("if(Get-Variable transientValue -ErrorAction SilentlyContinue){throw 'variable_leak'};if(Get-Command Test-Transient -ErrorAction SilentlyContinue){throw 'function_leak'};Write-Output 2",10000);Assert(r.ExitCode==0&&r.Output.Trim()=="2","child-scope leak");} });
            Test("worker command errors do not poison next response", delegate
            { using(DualPowerShellWorker w=new DualPowerShellWorker()){Assert(w.Execute("throw 'simulated'",10000).ExitCode!=0,"throw accepted");DualCommandResult r=w.Execute("Write-Output 9",10000);Assert(r.ExitCode==0&&r.Output.Trim()=="9"&&w.StartedProcessCount==1,"error poisoned worker");} });
            Test("worker successful empty output still completes", delegate
            { using(DualPowerShellWorker w=new DualPowerShellWorker()){DualCommandResult r=w.Execute("$local=1",10000);Assert(r.ExitCode==0&&!r.TimedOut&&r.Output.Length==0,"empty script did not complete");} });
            Test("worker timeout stops only own child and never reuses stale response", delegate
            { using(DualPowerShellWorker w=new DualPowerShellWorker()){Assert(w.Execute("Write-Output 'warm'",10000).ExitCode==0,"warmup failed");DualCommandResult timed=w.Execute("Start-Sleep -Milliseconds 1500;Write-Output 'stale'",200);Assert(timed.TimedOut&&w.StartedProcessCount==1,"timeout auto retried");DualCommandResult next=w.Execute("Write-Output 'fresh'",10000);Assert(next.ExitCode==0&&next.Output.Trim()=="fresh"&&w.StartedProcessCount==2,"stale response reached new request");} });
            Test("unconfirmed worker termination poisons transport and forbids replacement", delegate
            {
                bool allowStop=false;int stopAttempts=0;
                DualPowerShellWorker w=new DualPowerShellWorker(delegate(System.Diagnostics.Process owned)
                {stopAttempts++;if(!allowStop)return false;if(!owned.HasExited)owned.Kill();return owned.WaitForExit(1500);});
                try
                {
                    Assert(w.Execute("Write-Output 'warm'",10000).ExitCode==0,"warmup failed");
                    DualCommandResult first=w.Execute("Start-Sleep -Milliseconds 2000;Write-Output 'late'",150);
                    Assert(first.TimedOut&&first.ErrorCode=="worker_cleanup_pending","unconfirmed stop not reported");
                    DualCommandResult next=w.Execute("Write-Output 'must-not-run'",10000);
                    Assert(next.ExitCode!=0&&next.ErrorCode=="worker_cleanup_pending"&&w.StartedProcessCount==1&&stopAttempts==1,"poisoned transport started another command");
                    Assert(!w.CompleteInputAndWait(1),"EOF bypassed poisoned cleanup gate");
                }
                finally{allowStop=true;w.Dispose();}
                Assert(stopAttempts==2,"explicit cleanup did not retain the owned process handle");
            });
            Test("worker oversized output is bounded and next request survives", delegate
            { using(DualPowerShellWorker w=new DualPowerShellWorker()){Assert(w.Execute("Write-Output ('x'*260000)",10000).ExitCode!=0,"output limit missing");DualCommandResult next=w.Execute("Write-Output 3",10000);Assert(next.ExitCode==0&&next.Output.Trim()=="3","output overflow poisoned worker");} });
            Test("worker closes naturally on parent stdin EOF", delegate
            { using(DualPowerShellWorker w=new DualPowerShellWorker()){Assert(w.Execute("Write-Output 1",10000).ExitCode==0,"warmup failed");Assert(w.CompleteInputAndWait(3000),"stdin EOF did not exit worker");Assert(w.StartedProcessCount==1,"EOF restarted worker");} });
            Test("worker serialized concurrent commands retain response identity", delegate
            {
                using(DualPowerShellWorker w=new DualPowerShellWorker())
                {
                    var jobs=new List<System.Threading.Tasks.Task<DualCommandResult>>();
                    for(int i=0;i<4;i++){int n=i;jobs.Add(System.Threading.Tasks.Task.Factory.StartNew(delegate{return w.Execute("Write-Output "+n,10000);}));}
                    System.Threading.Tasks.Task.WaitAll(jobs.ToArray());for(int i=0;i<4;i++)Assert(jobs[i].Result.ExitCode==0&&jobs[i].Result.Output.Trim()==i.ToString(),"crossed worker responses");
                    Assert(w.StartedProcessCount==1,"concurrent startup spawned multiple workers");
                }
            });
        }

        public static int Main(string[] args)
        {
            PlatformTests();JournalTests();
            if(args!=null&&args.Contains("--worker-tests"))WorkerTests();
            if(args!=null&&args.Contains("--emit-scripts"))foreach(string script in scripts.Distinct())Console.WriteLine("DUAL_SCRIPT_BASE64:"+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(script)));
            Console.WriteLine((failed==0?"DUAL_NETWORK_WINDOWS_TESTS_OK ":"DUAL_NETWORK_WINDOWS_TESTS_FAILED ")+(failed==0?count:failed));
            return failed==0?0:1;
        }
    }
}
