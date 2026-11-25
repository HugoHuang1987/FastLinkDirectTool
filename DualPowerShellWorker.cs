using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FastLinkDirectTool.Modes
{
    // A single private child, lazily started. Never attaches to an existing
    // PowerShell process; a failed/timed-out command is never automatically retried.
    internal sealed class DualPowerShellWorker : IDisposable
    {
        private const string Ready = "DUAL_WORKER_READY";
        private const string ResponsePrefix = "DUAL_WORKER_RESPONSE:";
        private const int OutputLimit = 1024 * 1024;
        private readonly object gate = new object();
        private readonly Func<Process, bool> terminateOwned;
        private State current;
        private bool disposed, poisoned;
        internal int StartedProcessCount { get; private set; }

        private sealed class State
        {
            internal Process Process;
            internal readonly object Sync = new object();
            internal readonly Queue<string> Lines = new Queue<string>();
            internal readonly AutoResetEvent Signal = new AutoResetEvent(false);
            internal bool ProtocolError, Closed;
        }

        private sealed class Response
        {
            public string Id { get; set; }
            public bool Success { get; set; }
            public string Output { get; set; }
            public string ErrorCode { get; set; }
        }

        internal DualPowerShellWorker() : this(TerminateOwned) { }
        internal DualPowerShellWorker(Func<Process, bool> stopOwnedProcess)
        { if (stopOwnedProcess == null) throw new ArgumentNullException("stopOwnedProcess"); terminateOwned = stopOwnedProcess; }

        internal DualCommandResult Execute(string script, int timeoutMilliseconds)
        {
            if (script == null || script.Length > 65536) throw new ArgumentException("Worker script size is invalid.");
            if (timeoutMilliseconds < 100 || timeoutMilliseconds > 60000) throw new ArgumentException("Worker timeout is invalid.");
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("DualPowerShellWorker");
                if (poisoned) return new DualCommandResult { ExitCode = 1, Output = string.Empty, ErrorCode = "worker_cleanup_pending" };
                Stopwatch timer = Stopwatch.StartNew();
                string stage = "startup";
                try
                {
                    if (current == null)
                    {
                        current = Start();
                        if (ReadLine(current, timer, timeoutMilliseconds) != Ready) throw new InvalidOperationException("Worker startup protocol failed.");
                    }
                    string id = Guid.NewGuid().ToString("N");
                    stage = "send";
                    string input = id + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
                    State state = current;
                    Task send = Task.Factory.StartNew(delegate
                    { state.Process.StandardInput.WriteLine(input); state.Process.StandardInput.Flush(); });
                    send.ContinueWith(delegate(Task t) { AggregateException observed = t.Exception; },
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    if (!send.Wait(Remaining(timer, timeoutMilliseconds))) throw new TimeoutException();
                    stage = "response";
                    string line = ReadLine(state, timer, timeoutMilliseconds);
                    stage = "decode";
                    if (!line.StartsWith(ResponsePrefix, StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected worker output.");
                    byte[] payload = Convert.FromBase64String(line.Substring(ResponsePrefix.Length));
                    if (payload.Length > 2 * OutputLimit) throw new InvalidOperationException("Worker response too large.");
                    Response response = new JavaScriptSerializer { MaxJsonLength = 2 * OutputLimit, RecursionLimit = 8 }
                        .Deserialize<Response>(Encoding.UTF8.GetString(payload));
                    if (response == null || response.Id != id || response.Output == null) throw new InvalidOperationException("Worker response identity mismatch.");
                    byte[] output = Convert.FromBase64String(response.Output);
                    if (output.Length > OutputLimit) throw new InvalidOperationException("Worker output too large.");
                    return new DualCommandResult { ExitCode = response.Success ? 0 : 1, Output = Encoding.UTF8.GetString(output),
                        ErrorCode = response.Success ? null : "worker_command:" + SafeCode(response.ErrorCode) };
                }
                catch (TimeoutException)
                {
                    StopOwned(); return new DualCommandResult { ExitCode = -1, TimedOut = true, Output = string.Empty, ErrorCode = poisoned ? "worker_cleanup_pending" : "worker_timeout:" + stage };
                }
                catch (Exception error)
                {
                    StopOwned(); return new DualCommandResult { ExitCode = 1, Output = string.Empty, ErrorCode = poisoned ? "worker_cleanup_pending" : "worker_" + stage + ":" + error.GetType().Name };
                }
            }
        }

        private static string SafeCode(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 100) return "unknown";
            foreach (char c in text) if (!char.IsLetterOrDigit(c) && c != '_' && c != '.') return "unknown";
            return text;
        }

        private static int Remaining(Stopwatch timer, int timeout)
        {
            long remaining = timeout - timer.ElapsedMilliseconds;
            if (remaining <= 0) throw new TimeoutException();
            return (int)Math.Min(int.MaxValue, remaining);
        }

        private static string ReadLine(State state, Stopwatch timer, int timeout)
        {
            while (true)
            {
                lock (state.Sync)
                {
                    if (state.ProtocolError) throw new InvalidOperationException("Worker output exceeded protocol limits.");
                    if (state.Lines.Count > 0) return state.Lines.Dequeue();
                    if (state.Closed) throw new InvalidOperationException("Worker exited before responding.");
                }
                if (!state.Signal.WaitOne(Remaining(timer, timeout))) throw new TimeoutException();
            }
        }

        private State Start()
        {
            State state = new State();
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
                Arguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(Bootstrap)),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            Process process = new Process { StartInfo = info, EnableRaisingEvents = true };
            state.Process = process;
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                lock (state.Sync)
                {
                    if (e.Data == null) state.Closed = true;
                    else if (e.Data.Length > 3 * OutputLimit || state.Lines.Count >= 4) state.ProtocolError = true;
                    else state.Lines.Enqueue(e.Data);
                    state.Signal.Set();
                }
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                lock (state.Sync) { state.ProtocolError = true; state.Signal.Set(); }
            };
            bool started = false;
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Private worker did not start.");
                started = true;
                StartedProcessCount++;
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                return state;
            }
            catch
            {
                if (started) { current = state; StopOwned(); }
                else { try { process.Dispose(); } catch { } }
                throw;
            }
        }

        private void StopOwned()
        {
            State old = current;
            if (old == null) return;
            // Only this Process object was created by Start(). No name/PID search,
            // no process-tree kill, and never a retry of the interrupted command.
            bool exited = false;
            try { exited = terminateOwned(old.Process) && old.Process.HasExited; } catch { }
            if (!exited)
            {
                // Keep the exact handle and refuse all future requests. In
                // particular, never run rollback alongside an unconfirmed write.
                poisoned = true; return;
            }
            current = null; poisoned = false;
            try { old.Process.StandardInput.Close(); } catch { }
            try { old.Process.Dispose(); } catch { }
        }

        private static bool TerminateOwned(Process process)
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            try { return process.WaitForExit(1500) && process.HasExited; } catch { return false; }
        }

        // Exercises the normal parent-pipe EOF path without killing the worker.
        internal bool CompleteInputAndWait(int timeoutMilliseconds)
        {
            lock (gate)
            {
                if (current == null) return true;
                if (poisoned) return false;
                State old = current;
                old.Process.StandardInput.Close();
                bool exited = old.Process.WaitForExit(timeoutMilliseconds);
                if (!exited) StopOwned();
                else { current = null; old.Process.Dispose(); }
                return exited;
            }
        }

        public void Dispose()
        { lock (gate) { if (disposed && current == null) return; disposed = true; StopOwned(); } }

        // Each script executes in a child scope. Only module import state is
        // intentionally retained; per-request variables/functions are discarded.
        private const string Bootstrap = @"
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';
[Console]::InputEncoding=New-Object Text.UTF8Encoding($false);
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false);
[Console]::WriteLine('DUAL_WORKER_READY');
while($true){
 $line=[Console]::ReadLine();if($null -eq $line){break};
 $id='';$ok=$false;$output='';$fault='';
 try{
  if($line.Length -gt 350000){throw 'request_limit'};
  $separator=$line.IndexOf('|');if($separator -ne 32){throw 'request_identity'};
  $id=$line.Substring(0,32);if($id -notmatch '^[a-f0-9]{32}$'){throw 'request_identity'};
  $bytes=[Convert]::FromBase64String($line.Substring(33));if($bytes.Length -gt 262144){throw 'script_limit'};
  $code=[Text.Encoding]::UTF8.GetString($bytes);$buffer=New-Object Text.StringBuilder;
  & ([ScriptBlock]::Create($code)) *>&1 | ForEach-Object {
   $text=[string]$_;if($buffer.Length+$text.Length+2 -gt 250000){throw 'output_limit'};
   [void]$buffer.AppendLine($text)
  };
  $output=$buffer.ToString();$ok=$true
 }catch{$output='';$ok=$false;$fault=$_.Exception.GetType().Name};
 $encoded=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($output));
 $response=@{Id=$id;Success=$ok;Output=$encoded;ErrorCode=$fault}|ConvertTo-Json -Compress -Depth 3;
 [Console]::WriteLine('DUAL_WORKER_RESPONSE:'+ [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($response)))
}
";
    }
}
