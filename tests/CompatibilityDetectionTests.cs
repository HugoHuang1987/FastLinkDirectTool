using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace FastLinkDirectTool.Tests
{
    internal static class CompatibilityDetectionTests
    {
        private static int count;
        private static Action<string, Action> testCallback;
        private static void Assert(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }
        private static void Test(string name, Action action)
        {
            if (testCallback != null) testCallback(name, action);
            else
            {
                action();
                Console.WriteLine("PASS " + name);
            }
            count++;
        }
        internal static void Run(Action<string, Action> callback)
        {
            if (callback == null) throw new ArgumentNullException("callback");
            Action<string, Action> previous = testCallback;
            int previousCount = count;
            testCallback = callback;
            count = 0;
            try { RunAll(); }
            finally { testCallback = previous; count = previousCount; }
        }
        private static CompatibilityProcessInfo Process(int pid, string name, string directory)
        {
            return new CompatibilityProcessInfo { Pid = pid, Name = name, ExecutablePath = directory + "\\" + name + ".exe" };
        }
        private static CompatibilityTcpListener Listener(int pid, string address, int port)
        {
            return new CompatibilityTcpListener { OwnerPid = pid, Address = IPAddress.Parse(address), Port = port };
        }
        private static CompatibilityProcessInfo[] Pair(string core)
        {
            return new[] { Process(10, "Fastlink", @"D:\Program Files (x86)\Fastlink-new"),
                Process(20, core, @"D:\Program Files (x86)\Fastlink-new") };
        }
        private static FastLinkConnectionInfo Evaluate(string proxy, CompatibilityProcessInfo[] processes, params CompatibilityTcpListener[] listeners)
        {
            return FastLinkConnectionCompatibility.Evaluate(true, proxy, processes, listeners);
        }
        internal static void RunAll()
        {
            Test("single IPv4 loopback endpoint expands HTTP and HTTPS", delegate
            {
                List<LoopbackProxyEndpoint> endpoints;
                Assert(FastLinkConnectionCompatibility.TryParseProxyServer("127.0.0.1:7890", out endpoints), "not parsed");
                Assert(endpoints.Count == 2 && endpoints.All(e => e.Port == 7890), "incorrect endpoints");
            });
            Test("separate HTTP HTTPS IPv4 IPv6 mappings", delegate
            {
                List<LoopbackProxyEndpoint> endpoints;
                Assert(FastLinkConnectionCompatibility.TryParseProxyServer("http=127.0.0.1:7890;https=[::1]:7891", out endpoints), "not parsed");
                Assert(endpoints.Count == 2 && endpoints[1].Address.Equals(IPAddress.IPv6Loopback), "IPv6 lost");
            });
            Test("localhost is accepted without DNS resolution", delegate
            {
                List<LoopbackProxyEndpoint> endpoints;
                Assert(FastLinkConnectionCompatibility.TryParseProxyServer("LOCALHOST:7890", out endpoints) && endpoints.All(e => e.IsLocalhost), "localhost rejected");
            });
            foreach (string bad in new[] {
                "192.0.2.1:7890", "proxy.example.test:7890", "0.0.0.0:7890", "[::]:7890",
                "user:password@127.0.0.1:7890", "http://127.0.0.1:7890", "127.0.0.1:0",
                "127.0.0.1:65536", "127.0.0.1:+7890", "127.0.0.1: 7890", "127.1:7890",
                "2130706433:7890", "::1:7890", "[::1%1]:7890", "socks=127.0.0.1:7890",
                "http=127.0.0.1:7890;http=127.0.0.1:7891", "http=127.0.0.1:7890;https=192.0.2.1:7890",
                "http=127.0.0.1:7890;other=127.0.0.1:7890", "http=127.0.0.1:7890;https=", "" })
            {
                string value = bad;
                Test("unsafe or ambiguous proxy rejected fixture " + (count + 1), delegate
                {
                    List<LoopbackProxyEndpoint> endpoints;
                    Assert(!FastLinkConnectionCompatibility.TryParseProxyServer(value, out endpoints), "unsafe proxy parsed");
                });
            }
            Test("new ApexCore same directory and owning listener accepted", delegate
            {
                FastLinkConnectionInfo info = Evaluate("127.0.0.1:7890", Pair("ApexCore"), Listener(20, "127.0.0.1", 7890));
                Assert(info.Active && info.CoreName == "ApexCore" && info.GuiPid == 10 && info.CorePid == 20, "owner rejected");
                Assert(info.ProxyServerIdentity == "http=127.0.0.1:7890;https=127.0.0.1:7890", "unsafe identity");
            });
            Test("legacy FastLinkCore same directory and owning listener accepted", delegate
            {
                Assert(Evaluate("127.0.0.1:7890", Pair("FastLinkCore"), Listener(20, "127.0.0.1", 7890)).Active, "legacy rejected");
            });
            Test("disabled registry flag rejected", delegate
            {
                Assert(!FastLinkConnectionCompatibility.Evaluate(false, "127.0.0.1:7890", Pair("ApexCore"), new[] { Listener(20, "127.0.0.1", 7890) }).Active, "disabled accepted");
            });
            Test("missing GUI rejected", delegate
            {
                Assert(!Evaluate("127.0.0.1:7890", new[] { Pair("ApexCore")[1] }, Listener(20, "127.0.0.1", 7890)).Active, "GUI missing accepted");
            });
            Test("missing core rejected", delegate
            {
                Assert(!Evaluate("127.0.0.1:7890", new[] { Pair("ApexCore")[0] }, Listener(20, "127.0.0.1", 7890)).Active, "core missing accepted");
            });
            Test("unknown generic core rejected", delegate
            {
                Assert(!Evaluate("127.0.0.1:7890", Pair("mihomo"), Listener(20, "127.0.0.1", 7890)).Active, "generic accepted");
            });
            Test("core in unrelated directory rejected", delegate
            {
                CompatibilityProcessInfo[] pair = Pair("ApexCore");
                pair[1].ExecutablePath = @"D:\Unrelated\ApexCore.exe";
                Assert(!Evaluate("127.0.0.1:7890", pair, Listener(20, "127.0.0.1", 7890)).Active, "wrong directory accepted");
            });
            Test("process name executable mismatch rejected", delegate
            {
                CompatibilityProcessInfo[] pair = Pair("ApexCore");
                pair[1].ExecutablePath = @"D:\Program Files (x86)\Fastlink-new\other.exe";
                Assert(!Evaluate("127.0.0.1:7890", pair, Listener(20, "127.0.0.1", 7890)).Active, "wrong executable accepted");
            });
            Test("other process owning proxy listener rejected", delegate
            {
                Assert(!Evaluate("127.0.0.1:7890", Pair("ApexCore"), Listener(30, "127.0.0.1", 7890)).Active, "wrong owner accepted");
            });
            Test("wildcard listener does not prove loopback ownership", delegate
            {
                Assert(!Evaluate("127.0.0.1:7890", Pair("ApexCore"), Listener(20, "0.0.0.0", 7890)).Active, "wildcard accepted");
            });
            Test("port mismatch rejected", delegate
            {
                Assert(!Evaluate("127.0.0.1:7890", Pair("ApexCore"), Listener(20, "127.0.0.1", 7891)).Active, "wrong port accepted");
            });
            Test("explicit IPv4 endpoint cannot be satisfied by IPv6 listener", delegate
            {
                Assert(!Evaluate("127.0.0.1:7890", Pair("ApexCore"), Listener(20, "::1", 7890)).Active, "address mismatch accepted");
            });
            Test("localhost accepts verified IPv6 loopback listener", delegate
            {
                Assert(Evaluate("localhost:7890", Pair("ApexCore"), Listener(20, "::1", 7890)).Active, "localhost IPv6 rejected");
            });
            Test("every mapped endpoint must have same verified core owner", delegate
            {
                Assert(!Evaluate("http=127.0.0.1:7890;https=[::1]:7891", Pair("ApexCore"),
                    Listener(20, "127.0.0.1", 7890), Listener(30, "::1", 7891)).Active, "mixed owners accepted");
                Assert(Evaluate("http=127.0.0.1:7890;https=[::1]:7891", Pair("ApexCore"),
                    Listener(20, "127.0.0.1", 7890), Listener(20, "::1", 7891)).Active, "mapped listeners rejected");
            });
            Test("additional SOCKS mapping also requires verified ownership", delegate
            {
                Assert(!Evaluate("http=127.0.0.1:7890;socks=127.0.0.1:7891", Pair("ApexCore"),
                    Listener(20, "127.0.0.1", 7890), Listener(30, "127.0.0.1", 7891)).Active, "foreign SOCKS accepted");
            });
            Test("inactive result never exposes raw proxy input", delegate
            {
                FastLinkConnectionInfo info = Evaluate("user:password@proxy.example.test:7890", Pair("ApexCore"));
                Assert(!info.Active && info.ProxyServerIdentity == "" && info.GuiPid == 0 && info.CorePid == 0, "unsafe inactive metadata");
            });
            Test("null snapshots fail closed", delegate
            {
                Assert(!FastLinkConnectionCompatibility.Evaluate(true, "127.0.0.1:7890", null, null).Active, "null snapshots accepted");
            });
            Test("legacy recovery requires a positively verified same-directory pair", delegate
            {
                Assert(FastLinkConnectionCompatibility.CanUseLegacyRecovery(Pair("FastLinkCore")), "verified legacy pair rejected");
            });
            Test("proxy-disabled state never converts modern core into legacy permission", delegate
            {
                CompatibilityProcessInfo[] processes = Pair("ApexCore");
                Assert(!FastLinkConnectionCompatibility.Evaluate(false, null, processes, null).Active, "disabled proxy active");
                Assert(!FastLinkConnectionCompatibility.CanUseLegacyRecovery(processes), "modern core allowed");
            });
            Test("legacy recovery rejects null empty and incomplete process evidence", delegate
            {
                Assert(!FastLinkConnectionCompatibility.CanUseLegacyRecovery((IEnumerable<CompatibilityProcessInfo>)null), "null allowed");
                Assert(!FastLinkConnectionCompatibility.CanUseLegacyRecovery(new CompatibilityProcessInfo[0]), "empty allowed");
                Assert(!FastLinkConnectionCompatibility.CanUseLegacyRecovery(new[] { Pair("FastLinkCore")[0] }), "GUI only allowed");
                Assert(!FastLinkConnectionCompatibility.CanUseLegacyRecovery(new[] { Pair("FastLinkCore")[1] }), "core only allowed");
                Assert(!FastLinkConnectionCompatibility.CanUseLegacyRecovery(Pair("FastLinkCore").Concat(new CompatibilityProcessInfo[] { null })), "incomplete allowed");
            });
            Test("legacy recovery rejects same-directory ApexCore coexistence", delegate
            {
                Assert(!FastLinkConnectionCompatibility.CanUseLegacyRecovery(Pair("FastLinkCore").Concat(new[] {
                    Process(30, "ApexCore", @"D:\Program Files (x86)\Fastlink-new") })), "mixed cores allowed");
            });
            Test("legacy recovery conservatively rejects another ApexCore installation", delegate
            {
                Assert(!FastLinkConnectionCompatibility.CanUseLegacyRecovery(Pair("FastLinkCore").Concat(new[] {
                    Process(30, "ApexCore", @"D:\AnotherFastlink") })), "other modern installation allowed");
            });
            Test("legacy recovery rejects unknown ApexCore path rather than ignoring it", delegate
            {
                Assert(!FastLinkConnectionCompatibility.CanUseLegacyRecovery(Pair("FastLinkCore").Concat(new[] {
                    new CompatibilityProcessInfo { Pid = 30, Name = "ApexCore", ExecutablePath = null } })), "unknown modern path allowed");
            });
            Test("legacy recovery rejects mismatched or inaccessible executable paths", delegate
            {
                CompatibilityProcessInfo[] processes = Pair("FastLinkCore");
                processes[1].ExecutablePath = @"D:\Other\FastLinkCore.exe";
                Assert(!FastLinkConnectionCompatibility.CanUseLegacyRecovery(processes), "mismatched path allowed");
                processes[1].ExecutablePath = null;
                Assert(!FastLinkConnectionCompatibility.CanUseLegacyRecovery(processes), "inaccessible path allowed");
            });
            Test("legacy recovery rejects multiple GUI or legacy core processes", delegate
            {
                Assert(!FastLinkConnectionCompatibility.CanUseLegacyRecovery(Pair("FastLinkCore").Concat(new[] {
                    Process(30, "Fastlink", @"D:\Other") })), "multiple GUIs allowed");
                Assert(!FastLinkConnectionCompatibility.CanUseLegacyRecovery(Pair("FastLinkCore").Concat(new[] {
                    Process(30, "FastLinkCore", @"D:\Program Files (x86)\Fastlink-new") })), "multiple cores allowed");
            });
            Test("legacy recovery rejects nonpositive or shared process ids", delegate
            {
                CompatibilityProcessInfo[] processes = Pair("FastLinkCore");
                processes[1].Pid = 0;
                Assert(!FastLinkConnectionCompatibility.CanUseLegacyRecovery(processes), "zero PID allowed");
                processes[1].Pid = processes[0].Pid;
                Assert(!FastLinkConnectionCompatibility.CanUseLegacyRecovery(processes), "shared PID allowed");
            });
            Test("legacy recovery rejects generic replacement core", delegate
            {
                Assert(!FastLinkConnectionCompatibility.CanUseLegacyRecovery(Pair("mihomo")), "unknown core allowed");
            });
            Console.WriteLine("ALL_COMPATIBILITY_TESTS_OK " + count);
        }

        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--snapshot")
                {
                    FastLinkConnectionInfo info = FastLinkConnectionCompatibility.GetCurrent();
                    Console.WriteLine("Active=" + info.Active + ";Mode=" + info.Mode + ";GuiPid=" + info.GuiPid +
                        ";CorePid=" + info.CorePid + ";CoreName=" + info.CoreName +
                        ";ProxyServerIdentity=" + info.ProxyServerIdentity + ";Reason=" + info.Reason +
                        ";LegacyRecoveryIdentityAllowed=" + FastLinkConnectionCompatibility.CanUseLegacyRecovery());
                    return 0;
                }
                RunAll();
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }
    }
}
