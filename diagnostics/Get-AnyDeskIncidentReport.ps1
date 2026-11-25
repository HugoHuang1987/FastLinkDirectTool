param([ValidateRange(1,336)][int]$LookbackHours=24,[string]$LogRoot=(Join-Path $env:LOCALAPPDATA 'FastLinkDirectTool\diagnostics\anydesk'))
$ErrorActionPreference='Stop'
# On demand only. This script is never invoked by the collector or scheduler.
$since=[datetimeoffset]::Now.AddHours(-$LookbackHours)
$events=New-Object 'System.Collections.Generic.List[object]'
foreach($file in (Get-ChildItem -LiteralPath $LogRoot -Filter 'anydesk-*.jsonl'|Where-Object {$_.Name -match '^anydesk-\d{8}-\d{3}\.jsonl$'}|Sort-Object Name)){
    foreach($line in [IO.File]::ReadLines($file.FullName)){
        try{$e=$line|ConvertFrom-Json;if($e.timestamp -and [datetimeoffset]$e.timestamp -ge $since){$events.Add($e)}}catch{}
    }
}
$samples=@($events|Where-Object {$_.event -eq 'sample' -and $_.active_sessions -gt 0})
$status=if(Test-Path -LiteralPath (Join-Path $LogRoot 'status.json')){Get-Content -LiteralPath (Join-Path $LogRoot 'status.json') -Raw|ConvertFrom-Json}else{$null}
$report=[ordered]@{
    GeneratedAt=(Get-Date).ToString('o');Since=$since.ToString('o');CollectorStatus=$status
    ActiveSamples=$samples.Count
    SessionStarts=@($events|Where-Object {$_.event -eq 'session_started'}).Count
    PeerDirectFailures=@($events|Where-Object {$_.event -eq 'peer_direct_failed'}).Count
    FrameAdjustmentTimeouts=@($events|Where-Object {$_.event -eq 'frame_adjust_timeout'}).Count
    RemoteSessionErrors=@($events|Where-Object {$_.event -eq 'remote_session_closed'}).Count
    WifiLowSignalSamples=@($samples|Where-Object {$null -ne $_.wifi_signal_percent -and $_.wifi_signal_percent -lt 40}).Count
    HostHighCpuSamples=@($samples|Where-Object {$null -ne $_.system_cpu_percent -and $_.system_cpu_percent -ge 85}).Count
    HighMemoryPressureSamples=@($samples|Where-Object {$null -ne $_.memory_used_percent -and $_.memory_used_percent -ge 90}).Count
    GatewayFailedSamples=@($samples|Where-Object {$null -ne $_.gateway_reachable -and -not $_.gateway_reachable}).Count
    ProxyProbeFailedSamples=@($samples|Where-Object {$null -ne $_.proxy_probe_healthy -and -not $_.proxy_probe_healthy}).Count
    MaxGatewayRttMs=($samples|Where-Object {$null -ne $_.gateway_rtt_ms}|Measure-Object gateway_rtt_ms -Maximum).Maximum
    MaxCollectorElapsedMs=($samples|Measure-Object collector_elapsed_ms -Maximum).Maximum
    Timeline=@($events|Where-Object {$_.event -notin @('sample','collector_started')}|Select-Object -Last 200)
    Limitations=@('Only this PC is observed; the connecting PC is not monitored.','System TCP retransmits and WLAN traffic include all applications.','FastLink fake-IP handling does not prove use of an overseas proxy.','No per-session video FPS, end-to-end RTT or payload capture is available.','Samples and thresholds are clues, not proof of a unique root cause.')
}
$path=Join-Path $LogRoot 'latest-anydesk-report.json'
$report|ConvertTo-Json -Depth 9|Set-Content -LiteralPath $path -Encoding UTF8
$report|ConvertTo-Json -Depth 9
