using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FastLinkDirectTool.Modes;

namespace FastLinkDirectTool.Tests
{
    // Packet/admission-only fixtures. Never bind a production DNS listener.
    internal static class DualNetworkDnsTests
    {
        private static int count;
        private const string CompanyHost = "portal.corp.example.test";

        private sealed class Record
        {
            internal string Owner;
            internal ushort Type = 1, Class = 1;
            internal uint Ttl = 3600;
            internal byte[] Data;
            internal bool CompressedOwner;
        }
        private sealed class Call
        {
            internal string Source, Server;
            internal bool Tcp;
            internal int Timeout;
        }
        private sealed class Transport : IDualDnsTransport
        {
            internal List<Call> Calls = new List<Call>();
            internal Func<byte[], Call, byte[]> Handler;
            public byte[] Exchange(byte[] query, string sourceAddress, string dnsAddress, bool tcp, int timeoutMilliseconds)
            {
                Call call = new Call { Source = sourceAddress, Server = dnsAddress, Tcp = tcp, Timeout = timeoutMilliseconds };
                Calls.Add(call);
                return Handler(query, call);
            }
        }
        private sealed class Harness
        {
            internal bool Active = true, AcceptRoutes = true;
            internal int Routes;
            internal long Epoch;
            internal RuleTarget Target;
            internal string[] Addresses;
            internal Action BeforeRouteReturn;
            internal Transport Transport = new Transport();
            internal ModeRequest Request = RequestFixture();
            internal NetworkSnapshot Snapshot = SnapshotFixture();
            internal DualDnsSession Session;
            internal Harness()
            {
                Rebuild();
                Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { A(CompanyHost, "10.20.30.40") }); };
            }
            internal void Rebuild()
            {
                Session = DualDnsSession.Create(Request, Snapshot, 73, delegate(long generation, RuleTarget target, string[] addresses)
                {
                    Routes++; Epoch = generation; Target = target; Addresses = addresses;
                    if (BeforeRouteReturn != null) BeforeRouteReturn();
                    return AcceptRoutes;
                });
            }
            internal byte[] Handle(byte[] query, bool tcp = false)
            { return DualDnsPacketProcessor.Handle(query, tcp, Session, Transport, delegate { return Active; }); }
        }

        private static ModeRequest RequestFixture()
        {
            return new ModeRequest { Mode = WorkMode.WiredCorporateWifiInternet, CorporateAdapterId = "corp", InternetAdapterId = "internet",
                Rules = new List<ModeRule> { new ModeRule { Pattern = ".corp.example.test", Target = RuleTarget.Corporate } } };
        }
        private static NetworkSnapshot SnapshotFixture()
        {
            return new NetworkSnapshot { CorporateAdmissionVerified = true, DualConnectionAllowed = true, InternetReachabilityVerified = true,
                ExternalDefaultPreferred = true, StableSeconds = 20,
                Adapters = new List<AdapterSnapshot>
                {
                    new AdapterSnapshot { Id = "corp", IsPhysical = true, IsUp = true, Kind = LinkKind.Ethernet, Address = "192.0.2.20", PrefixLength = 24,
                        Gateway = "192.0.2.1", DnsServers = new List<string> { "192.0.2.53", "192.0.2.54" } },
                    new AdapterSnapshot { Id = "internet", IsPhysical = true, IsUp = true, Kind = LinkKind.Wifi, Address = "198.51.100.20", PrefixLength = 24,
                        Gateway = "198.51.100.1", DnsServers = new List<string> { "203.0.113.53" } }
                } };
        }
        private static void W16(Stream stream, int v) { stream.WriteByte((byte)(v >> 8)); stream.WriteByte((byte)v); }
        private static void W32(Stream stream, uint v) { W16(stream, (int)(v >> 16)); W16(stream, (int)v); }
        private static void Name(Stream stream, string name)
        {
            if (name.Length > 0) foreach (string label in name.Split('.'))
            { byte[] data = Encoding.ASCII.GetBytes(label); stream.WriteByte((byte)data.Length); stream.Write(data, 0, data.Length); }
            stream.WriteByte(0);
        }
        private static byte[] NameBytes(string name) { using (MemoryStream s = new MemoryStream()) { Name(s, name); return s.ToArray(); } }
        private static Record A(string owner, string ip)
        { return new Record { Owner = owner, Data = ip.Split('.').Select(byte.Parse).ToArray() }; }
        private static Record Cname(string owner, string alias)
        { return new Record { Owner = owner, Type = 5, Data = NameBytes(alias) }; }
        private static Record Service(string owner, string target)
        {
            using (MemoryStream s = new MemoryStream())
            {
                W16(s, 10); W16(s, 20); W16(s, 389); Name(s, target);
                return new Record { Owner = owner, Type = 33, Data = s.ToArray() };
            }
        }
        private static byte[] Query(string name = CompanyHost, ushort type = 1)
        {
            using (MemoryStream s = new MemoryStream())
            {
                W16(s, 0x19A7); W16(s, 0x0100); W16(s, 1); W16(s, 0); W16(s, 0); W16(s, 0);
                Name(s, name); W16(s, type); W16(s, 1); return s.ToArray();
            }
        }
        private static byte[] Answer(byte[] query, IEnumerable<Record> answer, IEnumerable<Record> additional = null, int flags = 0x81A0)
        {
            List<Record> answers = answer.ToList(), extras = (additional ?? new Record[0]).ToList();
            using (MemoryStream s = new MemoryStream())
            {
                W16(s, (query[0] << 8) | query[1]); W16(s, flags); W16(s, 1); W16(s, answers.Count); W16(s, 0); W16(s, extras.Count);
                s.Write(query, 12, query.Length - 12);
                foreach (Record rr in answers.Concat(extras))
                {
                    if (rr.CompressedOwner) { s.WriteByte(0xC0); s.WriteByte(0x0C); } else Name(s, rr.Owner);
                    W16(s, rr.Type); W16(s, rr.Class); W32(s, rr.Ttl); W16(s, rr.Data.Length); s.Write(rr.Data, 0, rr.Data.Length);
                }
                return s.ToArray();
            }
        }
        private static int Rcode(byte[] packet) { return packet[3] & 15; }
        private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        private static void Rejected(Harness h, byte[] query, int code = 2)
        { byte[] reply = h.Handle(query); Require(Rcode(reply) == code && h.Routes == 0 && DualDnsPacket.U16(reply, 6) == 0, "Unsafe packet was not rejected before route callback."); }
        private static void RejectPacket(Action body)
        { bool rejected = false; try { body(); } catch (InvalidDataException) { rejected = true; } Require(rejected, "Malformed packet accepted."); }
        private static byte[] Admitted(Harness h, DualDnsAdmission admission, byte[] query, bool tcp = false)
        {
            DualDnsAdmission.State captured = admission.Capture();
            return DualDnsPacketProcessor.Handle(query, tcp, captured == null ? null : captured.Session, h.Transport,
                delegate { return admission.IsCurrent(captured); });
        }

        internal static void Run(Action<string, Action> test)
        {
            test("pending admission rejects A and SRV over UDP and TCP before upstream or route work", delegate
            {
                Harness h = new Harness(); DualDnsAdmission admission = new DualDnsAdmission(); admission.StartPaused(h.Session);
                Stopwatch elapsed = Stopwatch.StartNew();
                foreach (bool tcp in new[] { false, true }) foreach (ushort type in new ushort[] { 1, 33 })
                {
                    byte[] reply = Admitted(h, admission, Query(type == 33 ? "_ldap._tcp.corp.example.test" : CompanyHost, type), tcp);
                    Require(Rcode(reply) == 2 && DualDnsPacket.U16(reply, 6) == 0, "Pending admission returned an answer.");
                }
                Require(h.Transport.Calls.Count == 0 && h.Routes == 0 && elapsed.ElapsedMilliseconds < 1000, "Pending admission blocked or touched upstream/routes.");
            });
            test("activation requires both exact generation and unique session ticket", delegate
            {
                Harness h = new Harness(); DualDnsAdmission admission = new DualDnsAdmission(); long first = admission.StartPaused(h.Session);
                Require(!admission.Resume(74, first) && !admission.Resume(73, first + 1), "Wrong generation or token activated DNS.");
                long second = admission.StartPaused(h.Session);
                Require(second != first && !admission.Resume(73, first), "Old startup activated a replacement session.");
                Require(admission.Resume(73, second) && !admission.Resume(73, second), "Activation did not consume its ticket exactly once.");
                Require(Rcode(Admitted(h, admission, Query())) == 0 && h.Routes == 1, "Ready DNS bypassed or lost the actual route callback.");
            });
            test("a packet captured while pending cannot inherit later activation", delegate
            {
                Harness h = new Harness(); DualDnsAdmission admission = new DualDnsAdmission(); long token = admission.StartPaused(h.Session);
                DualDnsAdmission.State before = admission.Capture(); Require(admission.Resume(73, token), "Fixture activation failed.");
                byte[] reply = DualDnsPacketProcessor.Handle(Query(), false, before.Session, h.Transport, delegate { return admission.IsCurrent(before); });
                Require(Rcode(reply) == 2 && h.Transport.Calls.Count == 0 && h.Routes == 0, "Pending packet inherited readiness.");
            });
            test("Pause invalidates pending activation and active packet tickets", delegate
            {
                Harness h = new Harness(); DualDnsAdmission admission = new DualDnsAdmission(); long token = admission.StartPaused(h.Session);
                admission.Pause(); Require(!admission.Resume(73, token), "Pause was undone by a late activation.");
                token = admission.StartPaused(h.Session); Require(admission.Resume(73, token), "Fixture activation failed.");
                DualDnsAdmission.State active = admission.Capture(); admission.Pause();
                Require(!admission.IsCurrent(active) && !admission.Resume(73, token), "Pause preserved an active/consumed ticket.");
                Require(Rcode(Admitted(h, admission, Query())) == 2 && h.Transport.Calls.Count == 0, "Paused DNS reached upstream.");
            });
            test("late A and SRV upstream replies cannot cross session replacement", delegate
            {
                foreach (ushort type in new ushort[] { 1, 33 })
                {
                    Harness h = new Harness(); DualDnsAdmission admission = new DualDnsAdmission(); long token = admission.StartPaused(h.Session);
                    Require(admission.Resume(73, token), "Fixture activation failed."); string host = type == 33 ? "_ldap._tcp.corp.example.test" : CompanyHost;
                    h.Transport.Handler = delegate(byte[] q, Call c)
                    {
                        admission.Pause(); h.Rebuild(); long next = admission.StartPaused(h.Session); Require(admission.Resume(73, next), "Replacement failed.");
                        return Answer(q, new[] { type == 33 ? Service(host, CompanyHost) : A(host, "10.20.30.40") });
                    };
                    byte[] reply = Admitted(h, admission, Query(host, type));
                    Require(Rcode(reply) == 2 && h.Routes == 0 && DualDnsPacket.U16(reply, 6) == 0, "Late upstream reply crossed the session boundary.");
                }
            });
            test("Pause racing Resume always leaves admission closed", delegate
            {
                for (int i = 0; i < 24; i++)
                {
                    Harness h = new Harness(); DualDnsAdmission admission = new DualDnsAdmission(); long token = admission.StartPaused(h.Session);
                    using (ManualResetEvent gate = new ManualResetEvent(false))
                    {
                        Thread resume = new Thread(delegate() { gate.WaitOne(); admission.Resume(73, token); });
                        Thread pause = new Thread(delegate() { gate.WaitOne(); admission.Pause(); });
                        resume.Start(); pause.Start(); gate.Set();
                        Require(resume.Join(2000) && pause.Join(2000), "Admission transition blocked.");
                    }
                    Require(!admission.IsCurrent(admission.Capture()) && !admission.Resume(73, token), "Resume resurrected a paused ticket.");
                }
            });
            test("queued packets retain receipt admission across Resume and session replacement", delegate
            {
                foreach (bool initiallyOpen in new[] { false, true })
                {
                    Harness h = new Harness(); DualDnsAdmission admission = new DualDnsAdmission(); long token = admission.StartPaused(h.Session);
                    if (initiallyOpen) Require(admission.Resume(73, token), "Fixture activation failed.");
                    DualDnsAdmission.State atReceipt = admission.Capture(); byte[] reply = null;
                    using (ManualResetEvent queued = new ManualResetEvent(false)) using (ManualResetEvent release = new ManualResetEvent(false))
                    {
                        Thread worker = new Thread(delegate()
                        {
                            queued.Set(); release.WaitOne();
                            reply = DualDnsPacketProcessor.Handle(Query(), false, atReceipt.Session, h.Transport, delegate { return admission.IsCurrent(atReceipt); });
                        });
                        worker.Start(); Require(queued.WaitOne(2000), "Worker was not queued.");
                        if (initiallyOpen) token = admission.StartPaused(h.Session);
                        Require(admission.Resume(73, token), "Later activation failed."); release.Set(); Require(worker.Join(2000), "Queued packet blocked.");
                    }
                    Require(Rcode(reply) == 2 && h.Routes == 0 && h.Transport.Calls.Count == 0, "Queued packet inherited a later admission state.");
                }
            });
            test("Pause after final-send validation drains that send before returning", delegate
            {
                Harness h = new Harness(); DualDnsAdmission admission = new DualDnsAdmission(); long token = admission.StartPaused(h.Session);
                Require(admission.Resume(73, token), "Fixture activation failed."); DualDnsAdmission.State captured = admission.Capture();
                int sends = 0; Exception pauseError = null;
                using (ManualResetEvent entered = new ManualResetEvent(false)) using (ManualResetEvent release = new ManualResetEvent(false)) using (ManualResetEvent paused = new ManualResetEvent(false))
                {
                    Thread sender = new Thread(delegate() { admission.TrySend(captured, delegate { entered.Set(); release.WaitOne(); Interlocked.Increment(ref sends); }); });
                    Thread pauser = new Thread(delegate() { try { admission.Pause(); } catch (Exception e) { pauseError = e; } finally { paused.Set(); } });
                    sender.Start(); Require(entered.WaitOne(2000), "Sender did not enter its final-send gate."); pauser.Start();
                    try
                    {
                        Require(SpinWait.SpinUntil(delegate { return admission.Capture() == null; }, 1000), "Pause did not invalidate admission immediately.");
                        Require(!paused.WaitOne(0), "Pause returned before the already-entered send completed.");
                        Require(!admission.TrySend(captured, delegate { Interlocked.Increment(ref sends); }), "Concurrent send queued behind blocked output.");
                    }
                    finally { release.Set(); Require(sender.Join(2000) && pauser.Join(2000), "Send/pause workers blocked."); }
                    Require(paused.WaitOne(0) && pauseError == null && sends == 1, "Pause did not linearize after its in-flight send.");
                    Require(!admission.TrySend(captured, delegate { sends++; }) && sends == 1, "Old answer sent after Pause returned.");
                }
            });
            test("send drain timeout fails closed without admitting another answer", delegate
            {
                Harness h = new Harness(); DualDnsAdmission admission = new DualDnsAdmission(); long token = admission.StartPaused(h.Session);
                Require(admission.Resume(73, token), "Fixture activation failed."); DualDnsAdmission.State captured = admission.Capture();
                using (ManualResetEvent entered = new ManualResetEvent(false)) using (ManualResetEvent release = new ManualResetEvent(false))
                {
                    Thread sender = new Thread(delegate() { admission.TrySend(captured, delegate { entered.Set(); release.WaitOne(); }); });
                    sender.Start(); Require(entered.WaitOne(2000), "Sender did not enter gate."); bool timeout = false; Stopwatch elapsed = Stopwatch.StartNew();
                    try { admission.Pause(25); } catch (TimeoutException) { timeout = true; }
                    finally { release.Set(); Require(sender.Join(2000), "Sender did not finish."); }
                    Require(timeout && elapsed.ElapsedMilliseconds < 1000 && !admission.IsCurrent(captured) && !admission.Resume(73, token), "Drain timeout reopened or waited without bound.");
                }
            });
            test("StartPaused cannot replace a session before its final send drains", delegate
            {
                Harness h = new Harness(); DualDnsAdmission admission = new DualDnsAdmission(); long token = admission.StartPaused(h.Session);
                Require(admission.Resume(73, token), "Fixture activation failed."); DualDnsAdmission.State captured = admission.Capture(); long next = 0;
                using (ManualResetEvent entered = new ManualResetEvent(false)) using (ManualResetEvent release = new ManualResetEvent(false)) using (ManualResetEvent replaced = new ManualResetEvent(false))
                {
                    Thread sender = new Thread(delegate() { admission.TrySend(captured, delegate { entered.Set(); release.WaitOne(); }); });
                    Thread replacement = new Thread(delegate() { next = admission.StartPaused(h.Session); replaced.Set(); });
                    sender.Start(); Require(entered.WaitOne(2000), "Sender did not enter gate."); replacement.Start();
                    try
                    {
                        Require(SpinWait.SpinUntil(delegate { return !object.ReferenceEquals(admission.Capture(), captured); }, 1000), "Replacement did not invalidate old admission.");
                        Require(!replaced.WaitOne(0), "Replacement overtook an old final send.");
                    }
                    finally { release.Set(); Require(sender.Join(2000) && replacement.Join(2000), "Replacement workers blocked."); }
                    Require(next != token && !admission.IsCurrent(captured) && !admission.Resume(73, token) && admission.Resume(73, next), "Replacement lost the exact new ticket gate.");
                }
            });
            test("slow final send does not serialize other current responses", delegate
            {
                Harness h = new Harness(); DualDnsAdmission admission = new DualDnsAdmission(); long token = admission.StartPaused(h.Session);
                Require(admission.Resume(73, token), "Fixture activation failed."); DualDnsAdmission.State captured = admission.Capture(); int completed = 0;
                using (ManualResetEvent entered = new ManualResetEvent(false)) using (ManualResetEvent release = new ManualResetEvent(false))
                {
                    Thread slow = new Thread(delegate() { admission.TrySend(captured, delegate { entered.Set(); release.WaitOne(); }); });
                    slow.Start(); Require(entered.WaitOne(2000), "Slow sender did not enter.");
                    try
                    {
                        Stopwatch timer = Stopwatch.StartNew();
                        for (int i = 0; i < 16; i++) Require(admission.TrySend(captured, delegate { completed++; }), "Current response dropped behind another sender.");
                        Require(completed == 16 && timer.ElapsedMilliseconds < 1000, "Current responses waited for slow output.");
                    }
                    finally { release.Set(); Require(slow.Join(2000), "Slow sender did not finish."); }
                }
            });
            test("Pause during StartPaused drain cancels that pending ticket without resurrection", delegate
            {
                Harness h = new Harness(); DualDnsAdmission admission = new DualDnsAdmission(); long token = admission.StartPaused(h.Session);
                Require(admission.Resume(73, token), "Fixture activation failed."); DualDnsAdmission.State captured = admission.Capture(); Exception cancelled = null;
                using (ManualResetEvent entered = new ManualResetEvent(false)) using (ManualResetEvent release = new ManualResetEvent(false))
                {
                    Thread sender = new Thread(delegate() { admission.TrySend(captured, delegate { entered.Set(); release.WaitOne(); }); });
                    Thread replacement = new Thread(delegate() { try { admission.StartPaused(h.Session); } catch (Exception e) { cancelled = e; } });
                    Thread pauser = new Thread(delegate() { admission.Pause(); });
                    sender.Start(); Require(entered.WaitOne(2000), "Sender did not enter."); replacement.Start();
                    try
                    {
                        Require(SpinWait.SpinUntil(delegate { return !object.ReferenceEquals(admission.Capture(), captured); }, 1000), "Replacement did not publish pending ticket.");
                        pauser.Start(); Require(SpinWait.SpinUntil(delegate { return admission.Capture() == null; }, 1000), "Pause did not cancel pending ticket.");
                    }
                    finally
                    {
                        release.Set(); Require(sender.Join(2000) && replacement.Join(2000), "Cancelled replacement blocked.");
                        if (pauser.ThreadState != System.Threading.ThreadState.Unstarted) Require(pauser.Join(2000), "Pauser blocked.");
                    }
                    Require(cancelled is InvalidOperationException && admission.Capture() == null && !admission.Resume(73, token), "In-flight StartPaused resurrected after Pause.");
                }
            });
            test("A route batch before response with epoch and TTL cap", delegate
            {
                Harness h = new Harness(); byte[] q = Query(); byte[] reply = h.Handle(q);
                DualDnsAnswer answer = DualDnsPacket.ReadAnswer(reply, DualDnsPacket.ReadQuery(q));
                Require(Rcode(reply) == 0 && h.Routes == 1 && h.Epoch == 73 && h.Target == RuleTarget.Corporate, "Missing route gate.");
                Require(h.Addresses.SequenceEqual(new[] { "10.20.30.40" }) && answer.Records.Single().Ttl == 60, "Wrong address/TTL.");
                Require(reply[0] == q[0] && reply[1] == q[1] && (DualDnsPacket.U16(reply, 2) & 0x0520) == 0x0100, "ID/RD/AA/AD invalid.");
            });
            test("corporate transport binds only company source and DNS", delegate
            {
                Harness h = new Harness(); h.Handle(Query()); Call c = h.Transport.Calls.Single();
                Require(c.Source == "192.0.2.20" && c.Server == "192.0.2.53" && !c.Tcp && c.Timeout <= 1500, "Unbound or wrong DNS.");
            });
            test("unmatched name uses external source and DNS", delegate
            {
                Harness h = new Harness(); string name = "public.example.test";
                h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { A(name, "93.184.216.34") }); };
                Require(Rcode(h.Handle(Query(name))) == 0 && h.Target == RuleTarget.Internet, "Unmatched target wrong.");
                Require(h.Transport.Calls.Single().Source == "198.51.100.20" && h.Transport.Calls.Single().Server == "203.0.113.53", "Company DNS used for outside name.");
            });
            test("corporate failures never use external DNS", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate { throw new TimeoutException(); }; Rejected(h, Query());
                Require(h.Transport.Calls.Count == 2 && h.Transport.Calls.All(delegate(Call c) { return c.Source == "192.0.2.20" && c.Server.StartsWith("192.0.2."); }), "DNS fallback leaked.");
            });
            test("CNAME chain validates owner and flattens original name", delegate
            {
                Harness h = new Harness(); Record alias = Cname(CompanyHost, "outside-alias.example.test"); alias.Ttl = 7;
                h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { alias, A("outside-alias.example.test", "10.20.30.40") }); };
                byte[] query = Query(); byte[] reply = h.Handle(query);
                DualDnsAnswer a = DualDnsPacket.ReadAnswer(reply, DualDnsPacket.ReadQuery(query));
                Require(h.Target == RuleTarget.Corporate && a.Records.Count == 1 && a.Records[0].Type == 1 && a.Records[0].Owner == CompanyHost && a.Records[0].Ttl == 7, "CNAME cache would escape original role or TTL.");
                Require(!Encoding.ASCII.GetString(reply).Contains("outside-alias"), "Alias leaked to client cache.");
            });
            test("zero TTL preserved across entire CNAME chain", delegate
            {
                Harness h = new Harness(); Record alias = Cname(CompanyHost, "alias.example.test"); alias.Ttl = 0;
                h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { alias, A("alias.example.test", "10.20.30.40") }); };
                byte[] q0 = Query(); Require(DualDnsPacket.ReadAnswer(h.Handle(q0), DualDnsPacket.ReadQuery(q0)).Records.Single().Ttl == 0, "TTL was increased.");
            });
            test("unrelated answer A ignored", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { A("evil.example.test", "10.0.0.9"), A(CompanyHost, "10.20.30.40") }); };
                h.Handle(Query()); Require(h.Addresses.SequenceEqual(new[] { "10.20.30.40" }), "Unrelated A routed.");
            });
            test("additional section A never routes or returns", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new Record[0], new[] { A(CompanyHost, "10.20.30.40") }); };
                byte[] reply = h.Handle(Query()); Require(Rcode(reply) == 0 && h.Routes == 0 && DualDnsPacket.U16(reply, 6) == 0 && DualDnsPacket.U16(reply, 10) == 0, "Additional A trusted.");
            });
            test("reverse evil CNAME does not authorize unrelated owner", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { Cname("evil.example.test", CompanyHost), A("evil.example.test", "10.0.0.9") }); };
                byte[] reply = h.Handle(Query()); Require(Rcode(reply) == 0 && h.Routes == 0 && DualDnsPacket.U16(reply, 6) == 0, "Reverse CNAME accepted.");
            });
            test("CNAME loop rejected", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { Cname(CompanyHost, "alias.example.test"), Cname("alias.example.test", CompanyHost) }); }; Rejected(h, Query());
            });
            test("CNAME owner A contradiction rejected", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { Cname(CompanyHost, "alias.example.test"), A(CompanyHost, "10.20.30.40"), A("alias.example.test", "10.20.30.41") }); }; Rejected(h, Query());
            });
            test("two different CNAME targets rejected", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { Cname(CompanyHost, "a.example.test"), Cname(CompanyHost, "b.example.test"), A("a.example.test", "10.20.30.40") }); }; Rejected(h, Query());
            });
            test("incomplete CNAME is not exposed for independent resolution", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { Cname(CompanyHost, "outside.example.test") }); }; Rejected(h, Query());
            });
            test("CNAME hop limit", delegate
            {
                Harness h = new Harness(); List<Record> records = new List<Record>(); string owner = CompanyHost;
                for (int i = 0; i < 18; i++) { string next = "a" + i + ".example.test"; records.Add(Cname(owner, next)); owner = next; } records.Add(A(owner, "10.20.30.40"));
                h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, records); }; Rejected(h, Query());
            });
            test("compressed answer owner accepted", delegate
            {
                Harness h = new Harness(); Record r = A(CompanyHost, "10.20.30.40"); r.CompressedOwner = true;
                h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { r }); }; Require(Rcode(h.Handle(Query())) == 0 && h.Routes == 1, "Valid compression rejected.");
            });
            test("compression self pointer rejected", delegate
            {
                byte[] q = Query(); byte[] bad = new byte[18]; Buffer.BlockCopy(q, 0, bad, 0, 12); bad[12] = 0xC0; bad[13] = 12; bad[15] = 1; bad[17] = 1;
                RejectPacket(delegate { DualDnsPacket.ReadQuery(bad); });
            });
            test("forward compression pointer rejected", delegate
            {
                byte[] q = Query(); byte[] bad = new byte[20]; Buffer.BlockCopy(q, 0, bad, 0, 12); bad[12] = 0xC0; bad[13] = 18;
                RejectPacket(delegate { DualDnsPacket.ReadQuery(bad); });
            });
            test("mismatched response ID rejected", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { byte[] r = Answer(q, new[] { A(CompanyHost, "10.20.30.40") }); r[1] ^= 1; return r; }; Rejected(h, Query());
            });
            test("mismatched response question rejected", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(Query("evil.example.test"), new[] { A(CompanyHost, "10.20.30.40") }); }; Rejected(h, Query());
            });
            test("mismatched response question type rejected", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(Query(CompanyHost, 28), new[] { A(CompanyHost, "10.20.30.40") }); }; Rejected(h, Query());
            });
            test("QR missing rejected", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { A(CompanyHost, "10.20.30.40") }, null, 0x0100); }; Rejected(h, Query());
            });
            test("fake IP and mixed real fake answers reject whole batch", delegate
            {
                foreach (string bad in new[] { "198.18.0.59", "198.19.1.2", "127.0.0.1", "169.254.1.2", "224.0.0.1", "0.0.0.0" })
                {
                    Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { A(CompanyHost, "10.20.30.40"), A(CompanyHost, bad) }); }; Rejected(h, Query());
                }
            });
            test("company answers cannot target external gateway DNS or local interface", delegate
            {
                foreach (string bad in new[] { "198.51.100.1", "203.0.113.53", "192.0.2.20" })
                {
                    Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { A(CompanyHost, bad) }); }; Rejected(h, Query());
                }
            });
            test("external answer cannot capture company DNS", delegate
            {
                Harness h = new Harness(); string host = "public.example.test";
                h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { A(host, "192.0.2.53") }); }; Rejected(h, Query(host));
            });
            test("late malformed RR prevents partial route installation", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { byte[] r = Answer(q, new[] { A(CompanyHost, "10.20.30.40"), A(CompanyHost, "10.20.30.41") }); return r.Take(r.Length - 1).ToArray(); }; Rejected(h, Query());
            });
            test("short A rdata rejected", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { new Record { Owner = CompanyHost, Data = new byte[] { 10, 20, 30 } } }); }; Rejected(h, Query());
            });
            test("trailing answer bytes rejected", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { A(CompanyHost, "10.20.30.40") }).Concat(new byte[] { 0 }).ToArray(); }; Rejected(h, Query());
            });
            test("AAAA SVCB HTTPS locally NODATA with no upstream or callback", delegate
            {
                foreach (ushort type in new ushort[] { 28, 64, 65 }) foreach (string host in new[] { CompanyHost, "public.example.test" })
                { Harness h = new Harness(); byte[] reply = h.Handle(Query(host, type)); Require(Rcode(reply) == 0 && h.Routes == 0 && h.Transport.Calls.Count == 0 && DualDnsPacket.U16(reply, 6) == 0, "IPv6/service-binding query leaked."); }
            });
            test("corporate SRV returns only same-role names and keeps subsequent A lookup gated", delegate
            {
                Harness h = new Harness(); string locator = "_ldap._tcp.dc._msdcs.corp.example.test", dc = "dc1.corp.example.test";
                h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { Service(locator, dc), Service("other.corp.example.test", "other.invalid") }, new[] { A(dc, "10.20.30.40") }); };
                byte[] query = Query(locator, 33), reply = h.Handle(query);
                DualDnsRecord r = DualDnsPacket.ReadAnswer(reply, DualDnsPacket.ReadQuery(query)).Records.Single();
                Require(Rcode(reply) == 0 && r.ServiceHost == dc && r.Priority == 10 && r.Weight == 20 && r.Port == 389 && r.Ttl == 60, "Wrong SRV answer.");
                Require(h.Routes == 0 && h.Transport.Calls.Single().Source == "192.0.2.20" && DualDnsPacket.U16(reply, 10) == 0, "SRV leaked glue or used wrong NIC.");
                h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { A(dc, "10.20.30.40") }); };
                Require(Rcode(h.Handle(Query(dc))) == 0 && h.Routes == 1 && h.Target == RuleTarget.Corporate, "DC lookup missed route gate.");
            });
            test("SRV rejects foreign discovery and targets and strips unrelated records", delegate
            {
                string locator = "_ldap._tcp.corp.example.test";
                Harness h = new Harness(); Rejected(h, Query("_ldap._tcp.external.example", 33), 5);
                Require(h.Transport.Calls.Count == 0, "Foreign SRV query leaked.");
                foreach (string target in new[] { "other.invalid", "127.0.0.1" })
                {
                    h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { Service(locator, target) }); };
                    Rejected(h, Query(locator, 33));
                }
                h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { Service(locator, "") }); };
                byte[] query = Query(locator, 33);
                Require(DualDnsPacket.ReadAnswer(h.Handle(query), DualDnsPacket.ReadQuery(query)).Records.Single().ServiceHost == "", "Unavailable service misparsed.");
            });
            test("SRV retries over TCP for large responses", delegate
            {
                string locator = "_ldap._tcp.dc._msdcs.corp.example.test";
                Harness h = new Harness();
                h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, Enumerable.Range(0, 18).Select(i => Service(locator, "server" + i + ".corp.example.test"))); };
                byte[] query = Query(locator, 33);
                Require(DualDnsPacket.CheckEnvelope(h.Handle(query), DualDnsPacket.ReadQuery(query)), "Oversized UDP answer was not truncated.");
                Require(DualDnsPacket.ReadAnswer(h.Handle(query, true), DualDnsPacket.ReadQuery(query)).Records.Count == 18, "TCP did not return complete SRV set.");
            });
            test("ordinary host SRV and unsupported record types never forwarded", delegate
            {
                foreach (ushort type in new ushort[] { 33, 16, 5, 255, 2 }) { Harness h = new Harness(); Rejected(h, Query(CompanyHost, type), 5); Require(h.Transport.Calls.Count == 0, "Unsupported type forwarded."); }
            });
            test("mode three external IPv6 preserves A-only policy and snapshot evidence", delegate
            {
                Harness h = new Harness(); h.Request.Mode = WorkMode.WifiCorporateWiredInternet;
                h.Snapshot.Adapters[0].Kind = LinkKind.Wifi; h.Snapshot.Adapters[1].Kind = LinkKind.Ethernet;
                h.Snapshot.Adapters[1].HasIpv6Default = true; h.Snapshot.Adapters[1].Ipv6RouteSignature = "::/0|fe80::1|256|25"; h.Rebuild();
                Require(h.Session.Snapshot.Adapters[1].Ipv6RouteSignature == h.Snapshot.Adapters[1].Ipv6RouteSignature, "IPv6 evidence lost in DNS snapshot");
                foreach (ushort type in new ushort[] { 28, 64, 65 }) foreach (string host in new[] { CompanyHost, "public.example.test" })
                {
                    byte[] reply = h.Handle(Query(host, type));
                    Require(Rcode(reply) == 0 && h.Routes == 0 && h.Transport.Calls.Count == 0 && DualDnsPacket.U16(reply, 6) == 0, "IPv6/service-binding escaped in mode three");
                }
                byte[] a = h.Handle(Query()); Require(Rcode(a) == 0 && h.Routes == 1 && h.Target == RuleTarget.Corporate, "corporate IPv4 routing stopped");
                Require(h.Transport.Calls.All(c => c.Source == "192.0.2.20"), "corporate DNS used external egress");
            });
            test("pause before query does not touch upstream", delegate
            { Harness h = new Harness(); h.Active = false; Rejected(h, Query()); Require(h.Transport.Calls.Count == 0, "Paused query forwarded."); });
            test("pause while upstream reply is pending prevents late callback", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { h.Active = false; return Answer(q, new[] { A(CompanyHost, "10.20.30.40") }); }; Rejected(h, Query());
            });
            test("epoch invalidated inside callback cannot return successful packet", delegate
            {
                Harness h = new Harness(); h.BeforeRouteReturn = delegate { h.Active = false; };
                Require(Rcode(h.Handle(Query())) == 2 && h.Routes == 1, "Stale callback response escaped.");
            });
            test("route rejection returns no A response", delegate
            {
                Harness h = new Harness(); h.AcceptRoutes = false; byte[] reply = h.Handle(Query()); Require(Rcode(reply) == 2 && DualDnsPacket.U16(reply, 6) == 0 && h.Routes == 1, "Failed route batch leaked A.");
            });
            test("route exception does not repeat mutation on another DNS server", delegate
            {
                Harness h = new Harness(); h.BeforeRouteReturn = delegate { throw new InvalidOperationException(); };
                Require(Rcode(h.Handle(Query())) == 2 && h.Routes == 1 && h.Transport.Calls.Count == 1, "Callback retried after exception.");
            });
            test("UDP TC triggers TCP on same source and server", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return c.Tcp ? Answer(q, new[] { A(CompanyHost, "10.20.30.40") }) : Answer(q, new Record[0], null, 0x8380); };
                Require(Rcode(h.Handle(Query())) == 0 && h.Routes == 1 && h.Transport.Calls.Count == 2, "TCP fallback missing.");
                Require(!h.Transport.Calls[0].Tcp && h.Transport.Calls[1].Tcp && h.Transport.Calls[0].Server == h.Transport.Calls[1].Server && h.Transport.Calls[0].Source == h.Transport.Calls[1].Source, "TCP fallback changed role.");
            });
            test("untrusted TC envelope never causes TCP fallback", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { byte[] r = Answer(q, new Record[0], null, 0x8380); r[1] ^= 1; return r; }; Rejected(h, Query()); Require(h.Transport.Calls.All(delegate(Call c) { return !c.Tcp; }), "Unverified TC followed.");
            });
            test("client TCP goes directly to TCP upstream", delegate
            { Harness h = new Harness(); Require(Rcode(h.Handle(Query(), true)) == 0 && h.Transport.Calls.Single().Tcp, "TCP not preserved."); });
            test("large UDP answer requests TCP without route callback", delegate
            {
                Harness h = new Harness(); List<Record> records = Enumerable.Range(1, 22).Select(delegate(int i) { return A(CompanyHost, "10.20.30." + i); }).ToList();
                h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, records); }; byte[] reply = h.Handle(Query());
                Require((DualDnsPacket.U16(reply, 2) & 0x200) != 0 && h.Routes == 0 && DualDnsPacket.U16(reply, 6) == 0, "Large UDP answer leaked partial records.");
                Require(Rcode(h.Handle(Query(), true)) == 0 && h.Routes == 1 && h.Addresses.Length == 22, "TCP batch incomplete.");
            });
            test("NXDOMAIN is returned without public fallback or routes", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new Record[0], null, 0x8183); };
                Require(Rcode(h.Handle(Query())) == 3 && h.Routes == 0 && h.Transport.Calls.Count == 1, "NXDOMAIN changed category.");
            });
            test("oversized packet rejected before upstream", delegate
            { Harness h = new Harness(); Rejected(h, new byte[4097], 1); Require(h.Transport.Calls.Count == 0, "Oversized query forwarded."); });
            test("session takes immutable copy of caller rules and endpoints", delegate
            {
                Harness h = new Harness(); h.Request.Rules.Clear(); h.Snapshot.Adapters[0].DnsServers[0] = "203.0.113.53";
                Require(Rcode(h.Handle(Query())) == 0 && h.Target == RuleTarget.Corporate && h.Transport.Calls[0].Server == "192.0.2.53", "Mutable input changed active epoch.");
            });
            test("incomplete safe snapshot refuses DNS session without socket operations", delegate
            {
                ModeRequest request = RequestFixture(); NetworkSnapshot snapshot = SnapshotFixture(); snapshot.Adapters[0].IsUp = false; bool failed = false;
                try { DualDnsSession.Create(request, snapshot, 1, delegate { return true; }); } catch (InvalidOperationException) { failed = true; }
                Require(failed, "Unsafe snapshot accepted.");
            });
            test("read deadline rejects expired budget without socket construction", delegate
            {
                bool failed = false; try { DualDnsPacket.ReadExact(new MemoryStream(new byte[2]), 2, Stopwatch.StartNew(), 0); } catch (TimeoutException) { failed = true; }
                Require(failed, "Expired frame budget accepted.");
            });
            test("bounded frame reader rejects partial payload", delegate
            {
                bool failed = false; try { DualDnsPacket.ReadExact(new MemoryStream(new byte[1]), 2, Stopwatch.StartNew(), 100); } catch (EndOfStreamException) { failed = true; }
                Require(failed, "Partial frame accepted.");
            });
            test("leading zero source DNS addresses use validated decimal canonical form", delegate
            {
                Harness h = new Harness(); h.Snapshot.Adapters[0].Address = "192.000.002.020"; h.Snapshot.Adapters[0].DnsServers[0] = "192.000.002.053"; h.Rebuild();
                Require(Rcode(h.Handle(Query())) == 0 && h.Transport.Calls[0].Source == "192.0.2.20" && h.Transport.Calls[0].Server == "192.0.2.53", "IPAddress octal interpretation diverged from validated address.");
            });
            test("authority section A never becomes a route", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c)
                {
                    byte[] r = Answer(q, new Record[0], new[] { A(CompanyHost, "10.20.30.40") }); r[9] = 1; r[11] = 0; return r;
                };
                byte[] reply = h.Handle(Query()); Require(Rcode(reply) == 0 && h.Routes == 0 && DualDnsPacket.U16(reply, 6) == 0, "Authority A trusted.");
            });
            test("AAAA included with A is stripped not returned or routed", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { A(CompanyHost, "10.20.30.40"), new Record { Owner = CompanyHost, Type = 28, Data = new byte[16] } }); };
                byte[] q0 = Query(); DualDnsAnswer a = DualDnsPacket.ReadAnswer(h.Handle(q0), DualDnsPacket.ReadQuery(q0));
                Require(a.Records.Count == 1 && a.Records[0].Type == 1 && h.Addresses.Length == 1, "AAAA record escaped.");
            });
            test("question class mismatch is rejected", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c)
                { byte[] altered = (byte[])q.Clone(); altered[altered.Length - 1] = 3; return Answer(altered, new[] { A(CompanyHost, "10.20.30.40") }); }; Rejected(h, Query());
            });
            test("TCP upstream truncation never passes", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new Record[0], null, 0x8380); };
                Require(Rcode(h.Handle(Query(), true)) == 2 && h.Routes == 0 && h.Transport.Calls.Count == 2, "Truncated TCP answer accepted.");
            });
            test("EDNS and DNSSEC flags are stripped from upstream and answer", delegate
            {
                Harness h = new Harness(); byte[] q = Query();
                using (MemoryStream s = new MemoryStream())
                {
                    q[11] = 1; q[3] = 0x20; s.Write(q, 0, q.Length); Name(s, ""); W16(s, 41); W16(s, 1232); W32(s, 0x8000); W16(s, 0); q = s.ToArray();
                }
                h.Transport.Handler = delegate(byte[] forwarded, Call c)
                {
                    Require(DualDnsPacket.U16(forwarded, 10) == 0 && (forwarded[3] & 0x20) == 0, "EDNS or AD leaked upstream.");
                    return Answer(forwarded, new[] { A(CompanyHost, "10.20.30.40") });
                };
                byte[] reply = h.Handle(q); Require(Rcode(reply) == 0 && (reply[3] & 0x20) == 0 && (reply[3] & 0x80) != 0, "Answer claimed authenticated data or no recursion support.");
            });
            test("malformed client metadata never contacts upstream", delegate
            {
                Harness h = new Harness(); byte[] q = Query(); q[11] = 1; Rejected(h, q, 1); Require(h.Transport.Calls.Count == 0, "Malformed metadata forwarded.");
            });
            test("record count bomb is rejected", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c) { byte[] r = Answer(q, new Record[0]); r[6] = 255; r[7] = 255; return r; }; Rejected(h, Query());
            });
            test("compressed CNAME target with valid previous suffix is accepted", delegate
            {
                Harness h = new Harness(); Record alias = new Record { Owner = CompanyHost, Type = 5, Data = new byte[] { 5, (byte)'a', (byte)'l', (byte)'i', (byte)'a', (byte)'s', 0xC0, 19 } };
                h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { alias, A("alias.corp.example.test", "10.20.30.40") }); };
                Require(Rcode(h.Handle(Query())) == 0 && h.Routes == 1 && h.Addresses.Single() == "10.20.30.40", "Valid compressed CNAME not resolved.");
            });
            test("self-compressed response owner is rejected before callback", delegate
            {
                Harness h = new Harness(); h.Transport.Handler = delegate(byte[] q, Call c)
                {
                    Record r = A(CompanyHost, "10.20.30.40"); r.CompressedOwner = true; byte[] answer = Answer(q, new[] { r });
                    answer[q.Length] = (byte)(0xC0 | (q.Length >> 8)); answer[q.Length + 1] = (byte)q.Length; return answer;
                }; Rejected(h, Query());
            });
            test("CNAME rdata trailing bytes invalidate whole packet", delegate
            {
                Harness h = new Harness(); Record alias = Cname(CompanyHost, "alias.corp.example.test"); alias.Data = alias.Data.Concat(new byte[] { 0 }).ToArray();
                h.Transport.Handler = delegate(byte[] q, Call c) { return Answer(q, new[] { alias, A("alias.corp.example.test", "10.20.30.40") }); }; Rejected(h, Query());
            });
        }

        private static void Test(string name, Action action)
        { action(); count++; Console.WriteLine("PASS " + name); }
        public static int Main()
        {
            try { Run(Test); Console.WriteLine("DUAL_NETWORK_DNS_TESTS_OK " + count); return 0; }
            catch (Exception ex) { Console.Error.WriteLine("DNS_TEST_FAILED " + ex.GetType().Name + ": " + ex.Message); return 1; }
        }
    }
}
