using System;
using System.Linq;
using System.Diagnostics;
using System.Text;
using FastLinkDirectTool.Modes;
namespace FastLinkDirectTool.Tests
{
    // Explicit diagnostics entry point: Capture only, no runtime, DNS or writer.
    internal static class DualReadOnlyProbe
    {
        public static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
                int count = args.Length == 1 && args[0] == "--timing" ? 3 : 1;
                var platform = new DualNetworkWindowsPlatform();
                NetworkSnapshot snapshot = null;
                for (int i = 0; i < count; i++)
                {
                    var timer = Stopwatch.StartNew();
                    snapshot = platform.Capture();
                    Console.WriteLine("READ_ONLY_CAPTURE_" + (i + 1) + "_MS=" + timer.ElapsedMilliseconds);
                }
                Console.WriteLine("READ_ONLY_SNAPSHOT_OK");
                Console.WriteLine("ProxyOrPac=" + snapshot.SystemProxyActive + ";Tunnel=" + snapshot.TunnelActive + ";ForeignDns=" + snapshot.OtherDnsPolicyPresent);
                foreach(var a in snapshot.Adapters)
                    Console.WriteLine(a.Name + " | " + a.Kind + " | physical=" + a.IsPhysical + " | up=" + a.IsUp + " | v6default=" + a.HasIpv6Default + " | id=" + a.Id);
                if (count > 1)
                {
                    var adapter = snapshot.Adapters.First(a => a.IsPhysical && a.IsUp);
                    // Read a documentation-only prefix; never add/remove it.
                    var resource = new DualResource { Kind = DualResourceKind.Route4, InterfaceId = adapter.Id,
                        Destination = "203.0.113.254/32", NextHop = adapter.Gateway };
                    for (int i = 0; i < 2; i++)
                    {
                        var timer = Stopwatch.StartNew();
                        string value = platform.Read(resource);
                        Console.WriteLine("READ_ONLY_ROUTE_" + (i + 1) + "_MS=" + timer.ElapsedMilliseconds + ";present=" + (value != null));
                    }
                }
                Console.WriteLine("READ_ONLY_NO_NETWORK_MUTATIONS"); return 0;
            }
            catch(Exception ex) { Console.WriteLine("READ_ONLY_SNAPSHOT_FAILED " + ex.Message); return 1; }
        }
    }
}
