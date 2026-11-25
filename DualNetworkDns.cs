using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FastLinkDirectTool.Modes
{
    // Constructing this type does not bind a socket. Only the explicit Start call
    // may bind loopback port 53; the runtime must own and verify NRPT separately.
    public sealed class DualNetworkDns : IDualDnsService
    {
        public const int MaximumConcurrentQueries = 48;
        private readonly object lifecycle = new object();
        private readonly object clientsGate = new object();
        private readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
        private readonly SemaphoreSlim slots = new SemaphoreSlim(MaximumConcurrentQueries, MaximumConcurrentQueries);
        private readonly IDualDnsTransport transport = new BoundDualDnsTransport();
        private Socket udp;
        private TcpListener tcp;
        private volatile bool running;
        private volatile bool paused = true;
        private long epoch;
        private DualDnsSession session;

        public bool IsRunning { get { return running; } }

        public void Start(ModeRequest request, NetworkSnapshot snapshot, long generation,
            Func<long, RuleTarget, string[], bool> routeCallback)
        {
            if (routeCallback == null) throw new ArgumentNullException("routeCallback");
            DualDnsSession next = DualDnsSession.Create(request, snapshot, generation, routeCallback);
            lock (lifecycle)
            {
                paused = true;
                Interlocked.Increment(ref epoch);
                if (!running)
                {
                    Socket newUdp = null;
                    TcpListener newTcp = null;
                    try
                    {
                        newUdp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        newUdp.ExclusiveAddressUse = true;
                        newUdp.ReceiveTimeout = 1000;
                        newUdp.SendTimeout = 1000;
                        newUdp.Bind(new IPEndPoint(IPAddress.Loopback, 53));
                        newTcp = new TcpListener(IPAddress.Loopback, 53);
                        newTcp.Server.ExclusiveAddressUse = true;
                        newTcp.Start(MaximumConcurrentQueries);
                        udp = newUdp;
                        tcp = newTcp;
                        running = true;
                        StartThread(delegate { UdpLoop(newUdp); }, "DualNetworkDns-UDP");
                        StartThread(delegate { TcpLoop(newTcp); }, "DualNetworkDns-TCP");
                    }
                    catch
                    {
                        running = false;
                        if (newUdp != null) newUdp.Close();
                        if (newTcp != null) newTcp.Stop();
                        udp = null; tcp = null;
                        throw;
                    }
                }
                session = next;
                paused = false;
            }
        }

        // Deliberately nonblocking. The runtime must invalidate its generation
        // under its own route-write lock BEFORE this call. Never hold our lock
        // while invoking its callback: that would invert the runtime lock order.
        public void Pause()
        {
            paused = true;
            Interlocked.Increment(ref epoch);
        }

        // Call only after the owner has verified that no NRPT depends on us.
        public void Stop()
        {
            Pause();
            lock (lifecycle)
            {
                running = false;
                if (udp != null) { udp.Close(); udp = null; }
                if (tcp != null) { tcp.Stop(); tcp = null; }
                session = null;
                lock (clientsGate)
                {
                    foreach (TcpClient client in clients) client.Close();
                    clients.Clear();
                }
            }
        }

        private static void StartThread(ThreadStart action, string name)
        {
            Thread thread = new Thread(action) { IsBackground = true, Name = name };
            thread.Start();
        }

        private byte[] Process(byte[] query, bool clientTcp)
        {
            long capturedEpoch = Interlocked.Read(ref epoch);
            DualDnsSession capturedSession = session;
            Func<bool> current = delegate
            {
                return running && !paused && capturedSession != null &&
                    object.ReferenceEquals(capturedSession, session) && Interlocked.Read(ref epoch) == capturedEpoch;
            };
            return DualDnsPacketProcessor.Handle(query, clientTcp, capturedSession, transport, current);
        }

        private void UdpLoop(Socket listener)
        {
            while (running && object.ReferenceEquals(listener, udp))
            {
                try
                {
                    byte[] buffer = new byte[DualDnsPacket.MaximumPacketLength + 1];
                    EndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
                    int count = listener.ReceiveFrom(buffer, ref endpoint);
                    IPEndPoint peer = endpoint as IPEndPoint;
                    if (peer == null || !IPAddress.IsLoopback(peer.Address)) continue;
                    byte[] query = new byte[count];
                    Buffer.BlockCopy(buffer, 0, query, 0, count);
                    if (!slots.Wait(0))
                    {
                        byte[] busy = DualDnsPacket.Error(query, 2);
                        listener.SendTo(busy, peer);
                        continue;
                    }
                    if (!ThreadPool.QueueUserWorkItem(delegate
                    {
                        try
                        {
                            long responseEpoch = Interlocked.Read(ref epoch);
                            byte[] reply = Process(query, false);
                            if (paused || responseEpoch != Interlocked.Read(ref epoch)) reply = DualDnsPacket.Error(query, 2);
                            if (running && object.ReferenceEquals(listener, udp)) listener.SendTo(reply, peer);
                        }
                        catch { /* Never let a client packet terminate the listener. */ }
                        finally { slots.Release(); }
                    })) slots.Release();
                }
                catch (SocketException) { if (!running || !object.ReferenceEquals(listener, udp)) return; }
                catch (ObjectDisposedException) { return; }
            }
        }

        private void TcpLoop(TcpListener listener)
        {
            while (running && object.ReferenceEquals(listener, tcp))
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException) { if (!running || !object.ReferenceEquals(listener, tcp)) return; else continue; }
                catch (ObjectDisposedException) { return; }
                IPEndPoint peer = client.Client.RemoteEndPoint as IPEndPoint;
                if (peer == null || !IPAddress.IsLoopback(peer.Address) || !slots.Wait(0)) { client.Close(); continue; }
                lock (clientsGate)
                {
                    if (!running || !object.ReferenceEquals(listener, tcp)) { client.Close(); slots.Release(); continue; }
                    clients.Add(client);
                }
                if (!ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        client.ReceiveTimeout = 2000; client.SendTimeout = 2000;
                        NetworkStream stream = client.GetStream();
                        Stopwatch readBudget = Stopwatch.StartNew();
                        byte[] prefix = DualDnsPacket.ReadExact(stream, 2, readBudget, 2000);
                        int length = DualDnsPacket.U16(prefix, 0);
                        if (length < 12 || length > DualDnsPacket.MaximumPacketLength) return;
                        byte[] query = DualDnsPacket.ReadExact(stream, length, readBudget, 2000);
                        long responseEpoch = Interlocked.Read(ref epoch);
                        byte[] reply = Process(query, true);
                        if (paused || responseEpoch != Interlocked.Read(ref epoch)) reply = DualDnsPacket.Error(query, 2);
                        if (!running || !object.ReferenceEquals(listener, tcp)) return;
                        byte[] framing = { (byte)(reply.Length >> 8), (byte)reply.Length };
                        stream.Write(framing, 0, 2); stream.Write(reply, 0, reply.Length);
                    }
                    catch { }
                    finally { lock (clientsGate) { clients.Remove(client); } client.Close(); slots.Release(); }
                })) { lock (clientsGate) { clients.Remove(client); } client.Close(); slots.Release(); }
            }
        }
    }

    // Injectable transport; tests never instantiate or open real sockets.
    internal interface IDualDnsTransport
    {
        byte[] Exchange(byte[] query, string sourceAddress, string dnsAddress, bool tcp, int timeoutMilliseconds);
    }

    internal sealed class BoundDualDnsTransport : IDualDnsTransport
    {
        public byte[] Exchange(byte[] query, string sourceAddress, string dnsAddress, bool tcp, int timeoutMilliseconds)
        {
            IPAddress source = IPAddress.Parse(sourceAddress), server = IPAddress.Parse(dnsAddress);
            if (source.AddressFamily != AddressFamily.InterNetwork || server.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidOperationException("IPv4 DNS transport required.");
            if (!tcp)
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.ReceiveTimeout = timeoutMilliseconds; socket.SendTimeout = timeoutMilliseconds;
                    socket.Bind(new IPEndPoint(source, 0));
                    socket.Connect(new IPEndPoint(server, 53)); // connected UDP rejects packets from other peers
                    socket.Send(query);
                    byte[] data = new byte[DualDnsPacket.MaximumPacketLength + 1];
                    int count = socket.Receive(data);
                    if (count > DualDnsPacket.MaximumPacketLength) throw new InvalidDataException("DNS response too large.");
                    byte[] result = new byte[count]; Buffer.BlockCopy(data, 0, result, 0, count); return result;
                }
            }
            using (TcpClient client = new TcpClient(new IPEndPoint(source, 0)))
            {
                Stopwatch budget = Stopwatch.StartNew();
                IAsyncResult connect = client.BeginConnect(server, 53, null, null);
                using (WaitHandle done = connect.AsyncWaitHandle)
                    if (!done.WaitOne(timeoutMilliseconds)) throw new TimeoutException("DNS connect timeout.");
                client.EndConnect(connect);
                int remaining = timeoutMilliseconds - (int)budget.ElapsedMilliseconds;
                if (remaining <= 0) throw new TimeoutException("DNS transport timeout.");
                client.ReceiveTimeout = remaining; client.SendTimeout = remaining;
                using (NetworkStream stream = client.GetStream())
                {
                    byte[] prefix = { (byte)(query.Length >> 8), (byte)query.Length };
                    stream.Write(prefix, 0, 2); stream.Write(query, 0, query.Length);
                    byte[] header = DualDnsPacket.ReadExact(stream, 2, budget, timeoutMilliseconds);
                    int count = DualDnsPacket.U16(header, 0);
                    if (count < 12 || count > DualDnsPacket.MaximumPacketLength) throw new InvalidDataException("DNS frame length invalid.");
                    return DualDnsPacket.ReadExact(stream, count, budget, timeoutMilliseconds);
                }
            }
        }
    }

    internal sealed class DualDnsSession
    {
        internal ModeRequest Request;
        internal NetworkSnapshot Snapshot;
        internal long Generation;
        internal Func<long, RuleTarget, string[], bool> RouteCallback;
        internal static DualDnsSession Create(ModeRequest request, NetworkSnapshot snapshot, long generation,
            Func<long, RuleTarget, string[], bool> callback)
        {
            if (request == null || snapshot == null || callback == null) throw new ArgumentNullException("DNS session inputs");
            ModeRequest copy = new ModeRequest
            {
                Mode = request.Mode, CorporateAdapterId = request.CorporateAdapterId, InternetAdapterId = request.InternetAdapterId,
                CorporateCidrs = new List<string>(request.CorporateCidrs ?? new List<string>()),
                Rules = (request.Rules ?? new List<ModeRule>()).Select(delegate(ModeRule r) { return r == null ? null : new ModeRule { Pattern = r.Pattern, Target = r.Target }; }).ToList()
            };
            NetworkSnapshot network = new NetworkSnapshot
            {
                SystemProxyActive = snapshot.SystemProxyActive, TunnelActive = snapshot.TunnelActive,
                CleanupPending = snapshot.CleanupPending, OtherDnsPolicyPresent = snapshot.OtherDnsPolicyPresent,
                CorporateAdmissionVerified = snapshot.CorporateAdmissionVerified, DualConnectionAllowed = snapshot.DualConnectionAllowed,
                InternetReachabilityVerified = snapshot.InternetReachabilityVerified, ExternalDefaultPreferred = snapshot.ExternalDefaultPreferred,
                Ipv6SafetyVerified = snapshot.Ipv6SafetyVerified, StableSeconds = snapshot.StableSeconds,
                Adapters = (snapshot.Adapters ?? new List<AdapterSnapshot>()).Select(delegate(AdapterSnapshot a)
                {
                    return a == null ? null : new AdapterSnapshot { Id = a.Id, Name = a.Name, Kind = a.Kind, IsUp = a.IsUp,
                        IsPhysical = a.IsPhysical, Address = CanonicalIpv4(a.Address), PrefixLength = a.PrefixLength, Gateway = CanonicalIpv4(a.Gateway),
                        DnsServers = (a.DnsServers ?? new List<string>()).Select(CanonicalIpv4).ToList(), HasIpv6Default = a.HasIpv6Default, Ipv6RouteSignature = a.Ipv6RouteSignature };
                }).ToList()
            };
            if (!NetworkModePlanner.Build(copy, network).CanSimulate) throw new InvalidOperationException("DNS start refused: mode safety gates not satisfied.");
            return new DualDnsSession { Request = copy, Snapshot = network, Generation = generation, RouteCallback = callback };
        }

        private static string CanonicalIpv4(string text)
        {
            uint address; string canonical;
            // Keep invalid input for the planner to reject; normalize valid
            // dotted decimal before IPAddress.Parse can interpret leading zeros.
            return PlanningValidation.TrySafeIpv4(text, out address, out canonical) ? canonical : text;
        }
    }

    internal static class DualDnsPacketProcessor
    {
        internal static byte[] Handle(byte[] packet, bool clientTcp, DualDnsSession session, IDualDnsTransport transport, Func<bool> current)
        {
            DualDnsQuestion question;
            try { question = DualDnsPacket.ReadQuery(packet); }
            catch { return DualDnsPacket.Error(packet, 1); }
            if (session == null || transport == null || current == null || !current()) return DualDnsPacket.Reply(question, null, 2, false);
            // IPv4-only v1. No AAAA / HTTPS / SVCB, independent CNAME queries,
            // SRV or arbitrary record-type forwarding can escape the A-route gate.
            if (question.Type == 28 || question.Type == 64 || question.Type == 65)
                return DualDnsPacket.Reply(question, null, 0, false);
            if (question.Type != 1) return DualDnsPacket.Reply(question, null, 5, false);
            IPAddress literal;
            string classificationAddress = IPAddress.TryParse(question.Name, out literal) ? literal.ToString() : "203.0.113.254";
            // Synthetic address is solely for selecting the rule target. Never
            // returned, sent to a server, or used in the actual route callback.
            RouteDecision classification = RuleRouter.Decide(session.Request.Rules, question.Name, new List<string> { classificationAddress });
            if (classification.Blocked) return DualDnsPacket.Reply(question, null, 2, false);
            string id = classification.Target == RuleTarget.Corporate ? session.Request.CorporateAdapterId : session.Request.InternetAdapterId;
            AdapterSnapshot adapter = session.Snapshot.Adapters.Single(delegate(AdapterSnapshot a) { return a != null && string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase); });
            byte[] query = DualDnsPacket.Query(question);
            Stopwatch budget = Stopwatch.StartNew();
            foreach (string server in adapter.DnsServers.Distinct(StringComparer.OrdinalIgnoreCase).Take(2))
            {
                if (!current()) break;
                try
                {
                    int timeout = Math.Min(1500, 4500 - (int)budget.ElapsedMilliseconds);
                    if (timeout <= 0) break;
                    byte[] response = transport.Exchange(query, adapter.Address, server, clientTcp, timeout);
                    bool truncated = DualDnsPacket.CheckEnvelope(response, question);
                    if (truncated)
                    {
                        if (clientTcp) throw new InvalidDataException("Truncated TCP answer.");
                        timeout = Math.Min(2000, 4500 - (int)budget.ElapsedMilliseconds);
                        if (timeout <= 0) break;
                        response = transport.Exchange(query, adapter.Address, server, true, timeout);
                    }
                    DualDnsAnswer answer = DualDnsPacket.ReadAnswer(response, question);
                    if (!current()) break;
                    if (answer.Rcode != 0 && answer.Rcode != 3) continue;
                    if (answer.Rcode == 3) return DualDnsPacket.Reply(question, null, 3, false);
                    if (answer.Addresses.Count == 0) return DualDnsPacket.Reply(question, null, 0, false);
                    RouteDecision routed = RuleRouter.Decide(session.Request.Rules, question.Name, answer.Addresses, session.Request, session.Snapshot);
                    if (routed.Blocked || routed.Target != classification.Target) return DualDnsPacket.Reply(question, null, 2, false);
                    // Flatten a verified CNAME chain into A records owned by the
                    // original question. Otherwise a cached alias could later be
                    // queried independently and fall through to the other role.
                    List<DualDnsRecord> safeRecords = answer.Records.Where(delegate(DualDnsRecord r) { return r.Type == 1; })
                        .Select(delegate(DualDnsRecord r) { return new DualDnsRecord { Owner = question.Name, Type = 1, Address = r.Address,
                            Ttl = answer.Records.Min(delegate(DualDnsRecord item) { return item.Ttl; }) }; }).ToList();
                    byte[] reply = DualDnsPacket.Reply(question, safeRecords, 0, false);
                    if (!clientTcp && reply.Length > 512) return DualDnsPacket.Reply(question, null, 0, true);
                    bool installed = false;
                    if (current())
                    {
                        try { installed = session.RouteCallback(session.Generation, routed.Target, routed.Addresses.ToArray()); }
                        catch { return DualDnsPacket.Reply(question, null, 2, false); }
                    }
                    if (!installed || !current()) return DualDnsPacket.Reply(question, null, 2, false);
                    return reply;
                }
                catch { /* A second server is allowed only within the SAME role. */ }
            }
            return DualDnsPacket.Reply(question, null, 2, false);
        }
    }

    internal sealed class DualDnsQuestion
    {
        internal ushort Id, Type, Class, Flags;
        internal string Name;
    }
    internal sealed class DualDnsRecord
    {
        internal string Owner, Alias, Address;
        internal ushort Type;
        internal uint Ttl;
    }
    internal sealed class DualDnsAnswer
    {
        internal int Rcode;
        internal List<DualDnsRecord> Records = new List<DualDnsRecord>();
        internal List<string> Addresses = new List<string>();
    }

    internal static class DualDnsPacket
    {
        internal const int MaximumPacketLength = 4096;
        internal const uint MaximumTtl = 60;
        private static void Need(byte[] data, int offset, int count)
        { if (data == null || offset < 0 || count < 0 || offset > data.Length - count) throw new InvalidDataException("Short DNS packet."); }
        internal static ushort U16(byte[] data, int offset)
        { Need(data, offset, 2); return (ushort)((data[offset] << 8) | data[offset + 1]); }
        private static uint U32(byte[] data, int offset)
        { return ((uint)U16(data, offset) << 16) | U16(data, offset + 2); }
        private static void W16(Stream stream, int value) { stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
        private static void W32(Stream stream, uint value) { W16(stream, (int)(value >> 16)); W16(stream, (int)value); }

        internal static byte[] ReadExact(Stream stream, int count)
        {
            byte[] data = new byte[count]; int used = 0;
            while (used < count) { int read = stream.Read(data, used, count - used); if (read == 0) throw new EndOfStreamException(); used += read; }
            return data;
        }

        internal static byte[] ReadExact(Stream stream, int count, Stopwatch budget, int totalMilliseconds)
        {
            if (count < 0 || count > MaximumPacketLength) throw new InvalidDataException("DNS frame size limit.");
            byte[] data = new byte[count]; int used = 0;
            while (used < count)
            {
                int remaining = totalMilliseconds - (int)budget.ElapsedMilliseconds;
                if (remaining <= 0) throw new TimeoutException("DNS frame read deadline.");
                if (stream.CanTimeout) stream.ReadTimeout = remaining;
                int read = stream.Read(data, used, count - used);
                if (read == 0) throw new EndOfStreamException();
                used += read;
            }
            return data;
        }

        private static string Name(byte[] data, ref int offset)
        {
            int position = offset, consumed = -1, hops = 0, wireLength = 1;
            HashSet<int> visited = new HashSet<int>();
            List<string> labels = new List<string>();
            while (true)
            {
                Need(data, position, 1);
                if (++hops > 128 || !visited.Add(position)) throw new InvalidDataException("DNS compression cycle.");
                int length = data[position++];
                if (length == 0) { if (consumed < 0) consumed = position; break; }
                if ((length & 192) == 192)
                {
                    Need(data, position, 1); int pointer = ((length & 63) << 8) | data[position++];
                    if (pointer < 12 || pointer >= position - 2) throw new InvalidDataException("Invalid DNS name pointer.");
                    if (consumed < 0) consumed = position;
                    position = pointer; continue;
                }
                if ((length & 192) != 0 || length > 63) throw new InvalidDataException("Invalid DNS label.");
                Need(data, position, length); wireLength += length + 1;
                if (wireLength > 255) throw new InvalidDataException("DNS name too long.");
                for (int i = 0; i < length; i++)
                {
                    byte c = data[position + i];
                    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_'))
                        throw new InvalidDataException("Unsupported DNS label character.");
                }
                labels.Add(Encoding.ASCII.GetString(data, position, length)); position += length;
            }
            offset = consumed; return string.Join(".", labels.ToArray());
        }

        private static void WriteName(Stream stream, string name)
        {
            foreach (string label in name.Split('.'))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(label);
                if (bytes.Length == 0 || bytes.Length > 63) throw new InvalidDataException("Invalid output name.");
                stream.WriteByte((byte)bytes.Length); stream.Write(bytes, 0, bytes.Length);
            }
            stream.WriteByte(0);
        }

        private static DualDnsQuestion Question(byte[] packet, ref int offset)
        {
            string name = Name(packet, ref offset);
            if (name.Length == 0) throw new InvalidDataException("Root question unsupported.");
            DualDnsQuestion q = new DualDnsQuestion { Id = U16(packet, 0), Flags = U16(packet, 2), Name = name,
                Type = U16(packet, offset), Class = U16(packet, offset + 2) };
            offset += 4; return q;
        }

        internal static DualDnsQuestion ReadQuery(byte[] packet)
        {
            Need(packet, 0, 12);
            if (packet.Length > MaximumPacketLength || (U16(packet, 2) & 0xF800) != 0 || U16(packet, 4) != 1 || U16(packet, 6) != 0 || U16(packet, 8) != 0 || U16(packet, 10) > 1)
                throw new InvalidDataException("Unsupported DNS query.");
            int offset = 12; DualDnsQuestion q = Question(packet, ref offset);
            if (q.Class != 1) throw new InvalidDataException("Only IN questions supported.");
            if (U16(packet, 10) == 1)
            {
                string owner = Name(packet, ref offset);
                if (owner.Length != 0 || U16(packet, offset) != 41) throw new InvalidDataException("Only EDNS metadata allowed.");
                int count = U16(packet, offset + 8); offset += 10; Need(packet, offset, count); offset += count;
            }
            if (offset != packet.Length) throw new InvalidDataException("Trailing DNS query data.");
            return q;
        }

        internal static bool CheckEnvelope(byte[] packet, DualDnsQuestion expected)
        {
            Need(packet, 0, 12);
            int flags = U16(packet, 2);
            if (packet.Length > MaximumPacketLength || U16(packet, 0) != expected.Id || (flags & 0xF800) != 0x8000 || U16(packet, 4) != 1)
                throw new InvalidDataException("DNS response identity mismatch.");
            int offset = 12; DualDnsQuestion q = Question(packet, ref offset);
            if (!string.Equals(q.Name, expected.Name, StringComparison.OrdinalIgnoreCase) || q.Type != expected.Type || q.Class != expected.Class)
                throw new InvalidDataException("DNS response question mismatch.");
            return (flags & 0x0200) != 0;
        }

        internal static DualDnsAnswer ReadAnswer(byte[] packet, DualDnsQuestion expected)
        {
            if (CheckEnvelope(packet, expected)) throw new InvalidDataException("Truncated DNS answer.");
            int offset = 12; Question(packet, ref offset);
            int answers = U16(packet, 6), total = answers + U16(packet, 8) + U16(packet, 10);
            if (total > 128) throw new InvalidDataException("Too many DNS records.");
            List<DualDnsRecord> records = new List<DualDnsRecord>();
            for (int index = 0; index < total; index++)
            {
                string owner = Name(packet, ref offset);
                ushort type = U16(packet, offset), recordClass = U16(packet, offset + 2);
                uint ttl = U32(packet, offset + 4); int length = U16(packet, offset + 8); offset += 10;
                Need(packet, offset, length); int end = offset + length;
                DualDnsRecord rr = new DualDnsRecord { Owner = owner, Type = type, Ttl = Math.Min(ttl, MaximumTtl) };
                if (type == 1)
                {
                    if (length != 4) throw new InvalidDataException("Invalid A length.");
                    rr.Address = new IPAddress(new byte[] { packet[offset], packet[offset + 1], packet[offset + 2], packet[offset + 3] }).ToString();
                }
                else if (type == 5)
                {
                    int position = offset; rr.Alias = Name(packet, ref position);
                    if (position != end || rr.Alias.Length == 0) throw new InvalidDataException("Invalid CNAME data.");
                }
                else if (type == 28 && length != 16) throw new InvalidDataException("Invalid AAAA length.");
                if (index < answers && recordClass == 1 && (type == 1 || type == 5)) records.Add(rr);
                offset = end;
            }
            if (offset != packet.Length) throw new InvalidDataException("Trailing DNS response data.");
            DualDnsAnswer result = new DualDnsAnswer { Rcode = U16(packet, 2) & 15 };
            if (result.Rcode != 0) return result;
            string current = expected.Name; HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int hop = 0; hop <= 16; hop++)
            {
                if (!seen.Add(current)) throw new InvalidDataException("CNAME cycle.");
                List<DualDnsRecord> owned = records.Where(delegate(DualDnsRecord r) { return string.Equals(r.Owner, current, StringComparison.OrdinalIgnoreCase); }).ToList();
                List<DualDnsRecord> aliases = owned.Where(delegate(DualDnsRecord r) { return r.Type == 5; }).ToList();
                List<DualDnsRecord> addresses = owned.Where(delegate(DualDnsRecord r) { return r.Type == 1; }).ToList();
                if (aliases.Count > 0)
                {
                    if (hop == 16 || addresses.Count > 0 || aliases.Select(delegate(DualDnsRecord r) { return r.Alias; }).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                        throw new InvalidDataException("Conflicting or excessive CNAME chain.");
                    DualDnsRecord alias = aliases[0]; alias.Ttl = aliases.Min(delegate(DualDnsRecord r) { return r.Ttl; });
                    result.Records.Add(alias); current = alias.Alias; continue;
                }
                foreach (IGrouping<string, DualDnsRecord> group in addresses.GroupBy(delegate(DualDnsRecord r) { return r.Address; }))
                {
                    DualDnsRecord rr = group.First(); rr.Ttl = group.Min(delegate(DualDnsRecord r) { return r.Ttl; });
                    result.Records.Add(rr); result.Addresses.Add(rr.Address);
                }
                if (result.Records.Count > 0 && result.Addresses.Count == 0)
                    throw new InvalidDataException("Incomplete CNAME chain cannot escape the original rule target.");
                return result;
            }
            throw new InvalidDataException("CNAME chain too long.");
        }

        internal static byte[] Query(DualDnsQuestion question)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                W16(stream, question.Id); W16(stream, question.Flags & 0x0100); W16(stream, 1);
                W16(stream, 0); W16(stream, 0); W16(stream, 0);
                WriteName(stream, question.Name); W16(stream, question.Type); W16(stream, question.Class); return stream.ToArray();
            }
        }

        internal static byte[] Reply(DualDnsQuestion question, List<DualDnsRecord> records, int rcode, bool truncated)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                W16(stream, question.Id); W16(stream, 0x8080 | (question.Flags & 0x0100) | (truncated ? 0x0200 : 0) | rcode);
                W16(stream, 1); W16(stream, records == null ? 0 : records.Count); W16(stream, 0); W16(stream, 0);
                WriteName(stream, question.Name); W16(stream, question.Type); W16(stream, question.Class);
                foreach (DualDnsRecord rr in records ?? new List<DualDnsRecord>())
                {
                    WriteName(stream, rr.Owner); W16(stream, rr.Type); W16(stream, 1); W32(stream, Math.Min(rr.Ttl, MaximumTtl));
                    if (rr.Type == 1) { W16(stream, 4); byte[] bytes = IPAddress.Parse(rr.Address).GetAddressBytes(); stream.Write(bytes, 0, bytes.Length); }
                    else if (rr.Type == 5)
                    {
                        using (MemoryStream name = new MemoryStream()) { WriteName(name, rr.Alias); byte[] bytes = name.ToArray(); W16(stream, bytes.Length); stream.Write(bytes, 0, bytes.Length); }
                    }
                    else throw new InvalidDataException("Unsupported response record.");
                }
                if (stream.Length > MaximumPacketLength) throw new InvalidDataException("Sanitized DNS answer too large.");
                return stream.ToArray();
            }
        }

        internal static byte[] Error(byte[] request, int rcode)
        {
            try { return Reply(ReadQuery(request), null, rcode, false); }
            catch
            {
                byte[] result = new byte[12];
                if (request != null && request.Length >= 2) { result[0] = request[0]; result[1] = request[1]; }
                result[2] = 128; result[3] = (byte)(128 | rcode); return result;
            }
        }
    }
}
