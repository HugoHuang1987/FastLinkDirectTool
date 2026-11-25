using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FastLinkDirectTool.Modes
{
    public sealed class DualInternetProbeResult
    {
        public bool Success { get; set; }
        public string AdapterId { get; set; }
        public string LocalAddress { get; set; }
        public int HttpStatus { get; set; }
        public DateTime CheckedAtUtc { get; set; }
        public string Message { get; set; }
    }

    // An on-demand, source-bound HTTPS check, not a network controller. This
    // neither persists InternetConfirmed nor establishes corporate admission.
    // The caller must revalidate topology before applying any network changes.
    public sealed class DualInternetProbe
    {
        private const int TimeoutMilliseconds = 8000;
        private readonly Func<string, int, DualCommandResult> runner;
        private readonly Func<DateTime> clock;

        public DualInternetProbe() : this(RunCurl, delegate { return DateTime.UtcNow; }) { }
        public DualInternetProbe(Func<string, int, DualCommandResult> commandRunner, Func<DateTime> utcClock)
        {
            if (commandRunner == null || utcClock == null) throw new ArgumentException("Missing probe dependency.");
            runner = commandRunner; clock = utcClock;
        }

        public DualInternetProbeResult Verify(string internetAdapterId, NetworkSnapshot snapshot)
        {
            DualInternetProbeResult result = new DualInternetProbeResult { AdapterId = internetAdapterId, Message = "外网尚未核验。" };
            Guid selected;
            if (!Guid.TryParse(internetAdapterId, out selected) || selected == Guid.Empty || snapshot == null || snapshot.Adapters == null)
                return Fail(result, "未明确选择外网接口，未执行联网检查。");
            AdapterSnapshot[] matches = snapshot.Adapters.Where(a => a != null && SameGuid(a.Id, selected)).ToArray();
            if (matches.Length != 1 || !matches[0].IsPhysical || !matches[0].IsUp ||
                (matches[0].Kind != LinkKind.Ethernet && matches[0].Kind != LinkKind.Wifi))
                return Fail(result, "所选外网接口未连接或身份不明确，未执行联网检查。");
            if (snapshot.SystemProxyActive || snapshot.TunnelActive || snapshot.CleanupPending)
                return Fail(result, "存在代理、隧道或待清理状态，不能确认物理外网。");
            IPAddress address;
            if (!TryPhysicalIpv4(matches[0].Address, out address))
                return Fail(result, "外网接口没有可核验的 IPv4 地址，未执行联网检查。");
            result.AdapterId = selected.ToString("D");
            result.LocalAddress = address.ToString();
            DualCommandResult command;
            try { command = runner(result.LocalAddress, TimeoutMilliseconds); }
            catch { return Fail(result, "外网检测未完成；未修改网络。"); }
            result.CheckedAtUtc = clock();
            if (command == null || command.TimedOut)
                return Fail(result, "外网 HTTPS 检查超时；未修改网络。");
            if (command.ExitCode != 0)
                return Fail(result, "外网 HTTPS 检查失败；未修改网络。");
            // No page content, headers, cookies, or stderr are retained. Curl's
            // fixed write-out marker is the only accepted result.
            string output = command.Output ?? "";
            if (output.Length > 256)
                return Fail(result, "外网检测结果异常；未修改网络。");
            string[] fields = output.Trim().Split('|');
            int status;
            IPAddress observed;
            if (fields.Length != 3 || fields[0] != "DUAL_PROBE" ||
                !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out status) ||
                !IPAddress.TryParse(fields[2], out observed) || observed.AddressFamily != AddressFamily.InterNetwork ||
                !observed.Equals(address))
                return Fail(result, "外网检测出口与选定接口不一致，未修改网络。");
            result.HttpStatus = status;
            if (status < 200 || status > 299)
                return Fail(result, "外网检测未获得正常响应（HTTP " + status.ToString(CultureInfo.InvariantCulture) + "）；未修改网络。");
            result.Success = true;
            result.Message = "外网 HTTPS 检查通过，来源为选定物理接口。";
            return result;
        }

        private static DualInternetProbeResult Fail(DualInternetProbeResult result, string message)
        { result.Success = false; result.Message = message; return result; }
        private static bool SameGuid(string value, Guid expected)
        { Guid found; return Guid.TryParse(value, out found) && found == expected; }
        private static bool TryPhysicalIpv4(string value, out IPAddress address)
        {
            if (!IPAddress.TryParse(value, out address) || address.AddressFamily != AddressFamily.InterNetwork ||
                address.ToString() != value || IPAddress.IsLoopback(address)) return false;
            byte[] bytes = address.GetAddressBytes();
            return bytes[0] != 0 && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19));
        }
        private static DualCommandResult RunCurl(string sourceAddress, int timeoutMilliseconds)
        {
            IPAddress checkedAddress;
            if (!TryPhysicalIpv4(sourceAddress, out checkedAddress)) throw new ArgumentException("Invalid source address.");
            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
            if (!File.Exists(executable)) return new DualCommandResult { ExitCode = -1, ErrorCode = "curl_missing" };
            // --disable MUST be the first argument: user curlrc files must not
            // inject a proxy, credentials, destinations or unsafe TLS options.
            ProcessStartInfo start = new ProcessStartInfo(executable,
                "--disable --ipv4 --interface " + checkedAddress +
                " --noproxy \"*\" --connect-timeout 3 --max-time 7 --silent --show-error --proto =https" +
                " --output NUL --write-out \"DUAL_PROBE|%{http_code}|%{local_ip}\" https://chatgpt.com/")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            StringBuilder output = new StringBuilder();
            object sync = new object();
            using (ManualResetEvent outputDone = new ManualResetEvent(false))
            using (ManualResetEvent errorDone = new ManualResetEvent(false))
            using (Process process = new Process { StartInfo = start })
            {
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) { try { outputDone.Set(); } catch (ObjectDisposedException) { } }
                    else lock (sync) { if (output.Length <= 256) output.Append(e.Data); }
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                { if (e.Data == null) { try { errorDone.Set(); } catch (ObjectDisposedException) { } } };
                Stopwatch elapsed = Stopwatch.StartNew();
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                if (!process.WaitForExit(Math.Max(0, timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds)))
                {
                    // Only this probe's child process is stopped, never a VPN,
                    // adapter, browser, assistant, or security product.
                    try { process.Kill(); } catch { }
                    return new DualCommandResult { ExitCode = -1, TimedOut = true, ErrorCode = "probe_timeout" };
                }
                // Drain callbacks within the same deadline; never add an
                // unbounded WaitForExit after a nominally bounded probe.
                if (!outputDone.WaitOne(Math.Max(0, timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds)) ||
                    !errorDone.WaitOne(Math.Max(0, timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds)))
                    return new DualCommandResult { ExitCode = -1, TimedOut = true, ErrorCode = "probe_output_timeout" };
                lock (sync) return new DualCommandResult { ExitCode = process.ExitCode, Output = output.ToString() };
            }
        }
    }
}
