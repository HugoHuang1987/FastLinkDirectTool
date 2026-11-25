using System;
using FastLinkDirectTool.Modes;

namespace FastLinkDirectTool.Tests
{
    internal static class DualInternetProbeTests
    {
        private const string Id = "11111111-1111-1111-1111-111111111111";
        private static int count;
        private static void Check(bool condition, string name)
        { if (!condition) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
        private static NetworkSnapshot Snapshot()
        {
            NetworkSnapshot value = new NetworkSnapshot();
            value.Adapters.Add(new AdapterSnapshot { Id = Id, IsUp = true, IsPhysical = true, Kind = LinkKind.Ethernet, Address = "192.0.2.10" });
            return value;
        }
        public static int Main()
        {
            try
            {
                int calls = 0;
                string output = "DUAL_PROBE|200|192.0.2.10";
                int exit = 0; bool timedOut = false;
                DateTime now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
                DualInternetProbe probe = new DualInternetProbe(delegate(string ip, int timeout)
                {
                    calls++;
                    if (ip != "192.0.2.10" || timeout != 8000) throw new Exception("invalid runner input");
                    return new DualCommandResult { Output = output, ExitCode = exit, TimedOut = timedOut };
                }, delegate { return now; });
                var ok = probe.Verify(Id, Snapshot());
                Check(ok.Success && ok.HttpStatus == 200 && ok.LocalAddress == "192.0.2.10" && ok.CheckedAtUtc == now && calls == 1, "bound HTTPS success records source and time");
                output = "DUAL_PROBE|204|192.0.2.10\r\n";
                Check(probe.Verify(Id, Snapshot()).Success, "2xx success with trailing newline");
                foreach (string code in new[] { "301", "302", "401", "403", "451", "500", "000", "199" })
                {
                    output = "DUAL_PROBE|" + code + "|192.0.2.10";
                    Check(!probe.Verify(Id, Snapshot()).Success, "non-success HTTP rejected " + code);
                }
                foreach (string bad in new[] { "DUAL_PROBE|200|127.0.0.1", "DUAL_PROBE|200|192.0.2.11", "DUAL_PROBE|200|::1", "200", "DUAL_PROBE|200|192.0.2.10|extra", "anythingDUAL_PROBE|200|192.0.2.10", new string('x', 300) })
                { output = bad; Check(!probe.Verify(Id, Snapshot()).Success, "wrong source or malformed output rejected"); }
                output = "DUAL_PROBE|200|192.0.2.10"; exit = 60;
                Check(!probe.Verify(Id, Snapshot()).Success, "TLS failure rejected even with success-like output");
                exit = 0; timedOut = true;
                Check(!probe.Verify(Id, Snapshot()).Success, "timeout rejected");
                timedOut = false;
                foreach (string address in new[] { "127.0.0.1", "169.254.1.2", "198.18.0.3", "198.19.4.5", "0.0.0.0", "224.0.0.1", "::1", "192.0.2.10 --unsafe", "192.0.2.010", "", null })
                {
                    var snapshot = Snapshot(); snapshot.Adapters[0].Address = address;
                    int before = calls;
                    Check(!probe.Verify(Id, snapshot).Success && calls == before, "invalid physical IPv4 never reaches runner");
                }
                foreach (Action<NetworkSnapshot> mutation in new Action<NetworkSnapshot>[] {
                    s => s.Adapters[0].IsPhysical = false, s => s.Adapters[0].IsUp = false,
                    s => s.Adapters[0].Kind = LinkKind.Other, s => s.Adapters.Clear(),
                    s => s.Adapters.Add(s.Adapters[0]), s => s.SystemProxyActive = true,
                    s => s.TunnelActive = true, s => s.CleanupPending = true })
                {
                    var snapshot = Snapshot(); mutation(snapshot); int before = calls;
                    Check(!probe.Verify(Id, snapshot).Success && calls == before, "ambiguous or conflicted interface never reaches runner");
                }
                int old = calls;
                Check(!probe.Verify("invalid", Snapshot()).Success && !probe.Verify(Guid.Empty.ToString(), Snapshot()).Success && !probe.Verify(Id, null).Success && calls == old, "invalid identity or missing snapshot rejected");
                Check(!new DualInternetProbe(delegate { return null; }, delegate { return now; }).Verify(Id, Snapshot()).Success, "missing result rejected");
                Check(!new DualInternetProbe(delegate { throw new Exception("secret must not leak"); }, delegate { return now; }).Verify(Id, Snapshot()).Message.Contains("secret"), "exception details not exposed");
                var untouched = Snapshot(); probe.Verify(Id, untouched);
                Check(!untouched.InternetReachabilityVerified && !untouched.CorporateAdmissionVerified && !untouched.DualConnectionAllowed, "probe cannot set runtime admission or permission gates");
                Console.WriteLine("DUAL_INTERNET_PROBE_TESTS_OK " + count); return 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL " + error.Message); return 1; }
        }
    }
}
