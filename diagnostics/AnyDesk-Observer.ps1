# Dot-sourced by the existing hidden collector. No scheduler, network probes,
# application restarts, route edits or AI calls are made by this module.
function Convert-AnyDeskTraceEvent {
    param([string]$Line)
    if ($Line -notmatch '^\s*(?:info|warning|error)\s+(?<time>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})\s+\S+\s+(?<pid>\d+)\b.*?\s(?<component>[\w.]+)\s+-\s+(?<message>.*)$') { return }
    $time=[datetime]::ParseExact($Matches.time,'yyyy-MM-dd HH:mm:ss.fff',[Globalization.CultureInfo]::InvariantCulture,([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))
    $processId=[int]$Matches.pid; $component=$Matches.component; $message=$Matches.message
    $e=[ordered]@{timestamp=$time.ToLocalTime().ToString('o');source_timestamp_utc=$time.ToString('o');event='';process_id=$processId}
    if ($component -eq 'app.backend_session' -and $message -eq 'Session started (ok).') {$e.event='session_started'}
    elseif ($component -eq 'app.backend_session' -and $message -eq 'Session stopped.') {$e.event='session_stopped'}
    elseif ($component -eq 'desk_rt.auto_adjust' -and $message -match '^Route type: (tunnel|direct|lan)\b') {$e.event='session_transport';$e.transport=$Matches[1]}
    elseif ($component -eq 'desk_rt.auto_adjust' -and $message -like 'Forced adjust because of a timeout*') {$e.event='frame_adjust_timeout'}
    elseif ($component -eq 'anynet.punch_connector' -and $message -eq 'All connect fibers have failed.') {$e.event='peer_direct_failed'}
    elseif ($component -eq 'anynet.punch_connector' -and $message -match 'TLS handshake failed') {$e.event='peer_tls_failed'}
    elseif ($component -eq 'anynet.any_socket' -and $message -match '^Direct connection (established|closed)') {$e.event='peer_direct_'+$Matches[1]}
    elseif ($component -eq 'app.session' -and $message -match '^(Session closed by remote side|Net socket closed): (\w+)') {
        $e.event=if($Matches[1] -eq 'Session closed by remote side'){'remote_session_closed'}else{'session_socket_closed'}
        $e.reason=if($Matches[2] -in @('desk_rt_ipc_error','anynet_unknown','result_closed','desk_rt_closed','desk_rt_session_closed')){$Matches[2]}else{'other'}
    }
    elseif ($component -eq 'anynet.relay_connector' -and $message -match '^Connecting to relay ([a-z0-9-]+\.net\.anydesk\.(?:com\.cn|com))\s') {$e.event='relay_selected';$e.host=$Matches[1]}
    elseif ($component -eq 'anynet.relay_connector' -and $message -match '^Using IPv4: (\d{1,3}(?:\.\d{1,3}){3})$') {
        $address=$null
        if(-not [Net.IPAddress]::TryParse($Matches[1],[ref]$address)){return}
        $e.event='relay_address';$e.address=$address.ToString()
    }
    elseif ($component -eq 'anynet.relay_connector' -and $message -eq 'Connection established.') {$e.event='relay_connected'}
    elseif ($component -eq 'anynet.relay_connector' -and $message -eq 'Connection terminated: anynet_relay_full') {$e.event='relay_full'}
    if($e.event){[pscustomobject]$e}
}

function Remove-AnyDeskOldLogs {
    param([string]$Root,[int]$KeepFiles=10)
    # Delete only this collector's exact file naming pattern, never recursively.
    $files=@(Get-ChildItem -LiteralPath $Root -File -Filter 'anydesk-*.jsonl' | Where-Object {$_.Name -match '^anydesk-\d{8}-\d{3}\.jsonl$'} | Sort-Object Name)
    foreach($file in $files){if($file.LastWriteTime -lt (Get-Date).AddDays(-14)){Remove-Item -LiteralPath $file.FullName -Force}}
    $files=@(Get-ChildItem -LiteralPath $Root -File -Filter 'anydesk-*.jsonl' | Where-Object {$_.Name -match '^anydesk-\d{8}-\d{3}\.jsonl$'} | Sort-Object Name)
    if($files.Count -gt $KeepFiles){$files | Select-Object -First ($files.Count-$KeepFiles) | ForEach-Object {Remove-Item -LiteralPath $_.FullName -Force}}
}

function Write-AnyDeskRecord {
    param($Record)
    $s=$script:AnyDeskObserver
    $line=($Record|ConvertTo-Json -Depth 7 -Compress)+[Environment]::NewLine
    $bytes=[Text.Encoding]::UTF8.GetByteCount($line)
    if($bytes -gt 65536){throw 'Observer record exceeds size limit'}
    $date=(Get-Date).ToString('yyyyMMdd')
    if(-not $s.LogPath -or $s.LogDate -ne $date -or ((Get-Item -LiteralPath $s.LogPath -ErrorAction SilentlyContinue).Length+$bytes -gt 5MB)){
        Remove-AnyDeskOldLogs -Root $s.Root -KeepFiles 9
        $existing=@(Get-ChildItem -LiteralPath $s.Root -Filter "anydesk-$date-*.jsonl"|Where-Object {$_.Name -match '^anydesk-\d{8}-\d{3}\.jsonl$'}|Sort-Object Name)
        $index=0
        if($existing.Count){$index=[int]([regex]::Match($existing[-1].Name,'-(\d{3})\.jsonl$').Groups[1].Value)+1}
        if($index -gt 999){throw 'Daily observer rotation limit reached'}
        $s.LogPath=Join-Path $s.Root ('anydesk-{0}-{1:000}.jsonl' -f $date,$index);$s.LogDate=$date
    }
    [IO.File]::AppendAllText($s.LogPath,$line,(New-Object Text.UTF8Encoding $false))
}

function Initialize-AnyDeskObserver {
    param([string]$OutputRoot=(Join-Path $env:LOCALAPPDATA 'FastLinkDirectTool\diagnostics\anydesk'),[string[]]$TracePaths=@())
    $ErrorActionPreference='Stop'
    if(-not ('FastLinkDiagnostics.TraceTail' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot 'AnyDeskTelemetry.cs') -ErrorAction Stop}
    New-Item -ItemType Directory -Path $OutputRoot -Force -ErrorAction Stop|Out-Null
    if(-not $TracePaths.Count){$TracePaths=@((Join-Path $env:APPDATA 'AnyDesk\ad.trace'),(Join-Path $env:ProgramData 'AnyDesk\ad_svc.trace'))}
    $tails=@{};foreach($p in $TracePaths){$tails[$p]=New-Object FastLinkDiagnostics.TraceTail}
    $script:AnyDeskObserver=@{Root=$OutputRoot;Tails=$tails;Sessions=@{};Transports=@{};Cpu=@{};Previous=$null;LogPath=$null;LogDate='';NextCheck=[datetime]::MinValue;NextStatus=[datetime]::MinValue;NextIdleSample=[datetime]::MinValue;NextPrune=[datetime]::MinValue;NextConnection=[datetime]::MinValue;Connections=@();TcpStatus=$null;SlowCount=0;NextError=[datetime]::MinValue;StartedUtc=[datetime]::UtcNow;Skipped=0;LastElapsedMs=0}
    Remove-AnyDeskOldLogs -Root $OutputRoot
    Write-AnyDeskRecord @{timestamp=(Get-Date).ToString('o');event='collector_started';schema=1;collector_version='1.0';monitor_pid=$PID;idle_check_seconds=30;active_min_seconds=10;max_total_mib=50;retention_days=14;privacy='metadata_only';scope='this_pc_only'}
}

function Invoke-AnyDeskObservation {
    param($NetworkSample,[Nullable[long]]$GatewayRttMs=$null)
    $ErrorActionPreference='Stop'
    $s=$script:AnyDeskObserver
    $now=Get-Date
    if(-not $s -or $now -lt $s.NextCheck){return}
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $errorKind=$null; $active=$false; $processes=@();$traceAvailable=$false;$bytesRead=0
    try {
        $processes=@(Get-Process -Name AnyDesk -ErrorAction SilentlyContinue)
        $ids=@($processes|ForEach-Object {$_.Id})
        foreach($path in $s.Tails.Keys){
            $tail=$s.Tails[$path]
            if(Test-Path -LiteralPath $path){$traceAvailable=$true}
            $lines=$tail.Read($path);$bytesRead+=$tail.BytesRead
            if($tail.Reset){
                foreach($key in @($s.Sessions.Keys|Where-Object {$_.StartsWith($path+'|')})){$s.Sessions.Remove($key);$s.Transports.Remove($key)}
                if(-not $processes.Count){$lines=@()}
            }
            foreach($line in $lines){
                $e=Convert-AnyDeskTraceEvent $line
                if(-not $e){continue}
                $key=$path+'|'+$e.process_id
                if($e.event -eq 'session_started' -and $e.process_id -in $ids){$s.Sessions[$key]=$e.timestamp}
                if($e.event -eq 'session_stopped'){$s.Sessions.Remove($key);$s.Transports.Remove($key)}
                if($e.event -eq 'session_transport'){$s.Transports[$key]=$e.transport}
                # Bootstrap rebuilds state but does not re-emit historical events.
                if([datetimeoffset]$e.timestamp -ge [datetimeoffset]$s.StartedUtc){Write-AnyDeskRecord $e}
            }
            if($tail.Backlog){Write-AnyDeskRecord @{timestamp=$now.ToString('o');event='trace_backlog';byte_budget=262144}}
        }
        foreach($key in @($s.Sessions.Keys)){
            $backendId=[int]($key.Substring($key.LastIndexOf('|')+1))
            if($backendId -notin $ids){
                $s.Sessions.Remove($key);$s.Transports.Remove($key)
                Write-AnyDeskRecord @{timestamp=$now.ToString('o');event='session_process_gone';process_id=$backendId}
            }
        }
        foreach($key in @($s.Transports.Keys)){
            if([int]($key.Substring($key.LastIndexOf('|')+1)) -notin $ids){$s.Transports.Remove($key)}
        }
        $active=$s.Sessions.Count -gt 0
        if($processes.Count -gt 0 -and ($active -or $now -ge $s.NextIdleSample)){
            $cpuTimes=[FastLinkDiagnostics.NativeTelemetry]::SystemTimes()
            $memory=[FastLinkDiagnostics.NativeTelemetry]::Memory()
            $cpuPercent=$null;$anyDeskCpu=$null;$interval=$null
            if($s.Previous){$interval=($now-$s.Previous.Time).TotalSeconds;$cpuPercent=[FastLinkDiagnostics.NativeTelemetry]::CpuPercent($s.Previous.SystemTimes,$cpuTimes)}
            $nextCpu=@{};$cpuDelta=0.0;$rss=0L
            foreach($p in $processes){
                try {
                    $identity=$p.Id.ToString()+'|'+$p.StartTime.ToUniversalTime().Ticks
                    $value=$p.TotalProcessorTime.TotalSeconds;$nextCpu[$identity]=$value
                    if($s.Cpu.ContainsKey($identity)){$cpuDelta+=[Math]::Max(0,$value-$s.Cpu[$identity])}
                    $rss+=$p.WorkingSet64
                }catch{}
            }
            if($interval -gt 0){$anyDeskCpu=[Math]::Round(100*$cpuDelta/$interval/[Environment]::ProcessorCount,2)}
            $nic=[Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()|Where-Object {$_.Name -eq 'WLAN'}|Select-Object -First 1
            $wifi=$null;$stats=$null;$rx=$null;$tx=$null;$resent=$null;$resets=$null
            if($nic){$wifi=[FastLinkDiagnostics.NativeTelemetry]::Wifi($nic.Id);$stats=$nic.GetIPv4Statistics()}
            if($s.Previous -and $interval -gt 0){
                if($stats -and $s.Previous.NicId -eq $nic.Id -and $stats.BytesReceived -ge $s.Previous.Rx -and $stats.BytesSent -ge $s.Previous.Tx){
                    $rx=[Math]::Round(($stats.BytesReceived-$s.Previous.Rx)*8/$interval/1e6,3)
                    $tx=[Math]::Round(($stats.BytesSent-$s.Previous.Tx)*8/$interval/1e6,3)
                }
                if($NetworkSample.TcpSegmentsResent -ge $s.Previous.Resent){$resent=[long]$NetworkSample.TcpSegmentsResent-$s.Previous.Resent}
                if($NetworkSample.TcpResetConnections -ge $s.Previous.Resets){$resets=[long]$NetworkSample.TcpResetConnections-$s.Previous.Resets}
            }
            # Native passive TCP table reads avoid extra WMI/cmdlet processes.
            $tcp=[FastLinkDiagnostics.NativeTelemetry]::Tcp([int[]]$ids)
            $connections=@($tcp.Connections|ForEach-Object {
                $path=if($_.LocalAddress -match '^198\.(18|19)\.' -or $_.RemoteAddress -match '^198\.(18|19)\.'){'fastlink_tun_not_exit_proof'}elseif($_.LocalAddress -eq $NetworkSample.WlanIp){'physical_wlan_socket'}else{'other_or_unknown'}
                @{pid=$_.Pid;state=$_.State;local_ip=$_.LocalAddress;local_port=$_.LocalPort;remote_ip=$_.RemoteAddress;remote_port=$_.RemotePort;path=$path}
            })
            $record=[ordered]@{timestamp=$now.ToString('o');event='sample';schema=1;active_sessions=$s.Sessions.Count;trace_available=$traceAvailable;transport=@($s.Transports.Values|Sort-Object -Unique);anydesk_processes=$processes.Count;anydesk_cpu_percent=$anyDeskCpu;anydesk_memory_mib=[Math]::Round($rss/1MB,1);system_cpu_percent=$cpuPercent;memory_used_percent=if($memory){$memory[0]}else{$null};memory_available_mib=if($memory){$memory[1]}else{$null};wifi_signal_percent=if($wifi){$wifi[0]}else{$null};wifi_rx_link_rate_raw=if($wifi){$wifi[1]}else{$null};wifi_tx_link_rate_raw=if($wifi){$wifi[2]}else{$null};wlan_up=$NetworkSample.WlanUp;gateway_reachable=$NetworkSample.GatewayReachable;gateway_rtt_ms=$GatewayRttMs;gateway=$NetworkSample.Gateway;wlan_ip=$NetworkSample.WlanIp;all_apps_wlan_rx_mbps=$rx;all_apps_wlan_tx_mbps=$tx;system_tcp_retransmit_delta=$resent;system_tcp_reset_delta=$resets;proxy_probe_healthy=$NetworkSample.ChatGptHttps;fastlink_core_present=$NetworkSample.FastLinkCoreProcess;fastlink_node_sessions=$NetworkSample.FastLinkNodeSessions;tcp_ipv4_status=$tcp.IPv4Status;tcp_ipv6_status=$tcp.IPv6Status;connections=$connections;sample_interval_seconds=if($interval){[Math]::Round($interval,2)}else{$null};context_sample_timestamp=$NetworkSample.Timestamp;collector_elapsed_ms=$watch.ElapsedMilliseconds}
            Write-AnyDeskRecord $record
            $s.Cpu=$nextCpu
            $s.Previous=@{Time=$now;SystemTimes=$cpuTimes;NicId=if($nic){$nic.Id}else{''};Rx=if($stats){$stats.BytesReceived}else{0L};Tx=if($stats){$stats.BytesSent}else{0L};Resent=[long]$NetworkSample.TcpSegmentsResent;Resets=[long]$NetworkSample.TcpResetConnections}
            $s.NextIdleSample=$now.AddSeconds(60)
        }
        if(-not $processes.Count){$s.Previous=$null;$s.Cpu=@{}}
        if($now -ge $s.NextPrune){Remove-AnyDeskOldLogs -Root $s.Root;$s.NextPrune=$now.AddHours(1)}
    }catch {
        # Never copy exception messages, paths or arbitrary trace text into logs.
        $errorKind=$_.Exception.GetType().Name
        if($now -ge $s.NextError){Write-AnyDeskRecord @{timestamp=$now.ToString('o');event='collector_error';error_type=$errorKind};$s.NextError=$now.AddMinutes(5)}
    }finally {
        $elapsed=$watch.ElapsedMilliseconds;$s.LastElapsedMs=$elapsed
        $s.SlowCount=if($elapsed -gt 250){$s.SlowCount+1}else{0}
        $delay=if($active){10}else{30}
        if($s.SlowCount -ge 3 -or $errorKind){$delay=60;$s.Skipped++;$s.SlowCount=0}
        $s.NextCheck=(Get-Date).AddSeconds($delay)
        if($now -ge $s.NextStatus){
            @{timestamp=(Get-Date).ToString('o');collector_version='1.0';monitor_pid=$PID;state=if($errorKind){'error'}elseif($active){'active_session'}elseif($processes.Count){'anydesk_idle'}else{'anydesk_not_running'};active_sessions=$s.Sessions.Count;last_elapsed_ms=$elapsed;next_check_seconds=$delay;throttle_count=$s.Skipped;trace_available=$traceAvailable;trace_bytes_read=$bytesRead;log_path=$s.LogPath;error_type=$errorKind}|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $s.Root 'status.json') -Encoding UTF8
            $s.NextStatus=$now.AddSeconds(60)
        }
    }
}
