param()
$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'diagnostics\AnyDesk-Observer.ps1')
$temp=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('AnyDeskObserver-tests-'+[guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $temp|Out-Null
$script:Passed=0
function Assert($condition,[string]$message){if(-not $condition){throw $message}}
function Test([string]$name,[scriptblock]$body){& $body;$script:Passed++;"PASS $name"}
function Line([string]$message,[string]$component='app.backend_session',[int]$processId=555555){
    'info '+[datetime]::UtcNow.AddSeconds(1).ToString('yyyy-MM-dd HH:mm:ss.fff')+' back '+$processId+' 100 2 '+$component+' - '+$message
}
$script:MockProcesses=@();$script:Slow=$false
function Get-Process {param($Name,$ErrorAction) if($script:Slow){Start-Sleep -Milliseconds 300};$script:MockProcesses}
try {
    $trace=Join-Path $temp 'ad.trace'
    [IO.File]::WriteAllText($trace,'',(New-Object Text.UTF8Encoding $false))
    Initialize-AnyDeskObserver -OutputRoot (Join-Path $temp 'logs') -TracePaths @($trace)
    Test 'privacy ignores usernames tokens clipboard and unrelated errors' {
        foreach($text in @('Incoming session request: PersonName (123456789)','password=TOP_SECRET','clipboard: private text','Session secret=TOP_SECRET')){
            Assert (-not (Convert-AnyDeskTraceEvent (Line $text))) 'Unexpected raw content was retained'
        }
        $r=Convert-AnyDeskTraceEvent (Line 'Session closed by remote side: SUPER_SECRET_TOKEN.' 'app.session')
        Assert ($r.reason -eq 'other' -and ($r|ConvertTo-Json) -notmatch 'SUPER_SECRET') 'Unknown error leaked raw text'
    }
    Test 'timestamps have explicit UTC source and local offset' {
        $r=Convert-AnyDeskTraceEvent 'info 2026-10-08 14:28:26.496 back 123 10 2 app.backend_session - Session started (ok).'
        Assert ($r.source_timestamp_utc -eq '2026-10-08T14:28:26.4960000Z') 'Trace UTC changed'
        Assert (([datetimeoffset]$r.timestamp).UtcDateTime.Hour -eq 14) 'Local offset lost'
    }
    Test 'connection type timeout and direct-failure allowlist' {
        Assert ((Convert-AnyDeskTraceEvent (Line 'Route type: tunnel (internet)' 'desk_rt.auto_adjust')).transport -eq 'tunnel') 'Tunnel event missing'
        Assert ((Convert-AnyDeskTraceEvent (Line 'Forced adjust because of a timeout (9999 kb/s).' 'desk_rt.auto_adjust')).event -eq 'frame_adjust_timeout') 'Timeout missing'
        Assert ((Convert-AnyDeskTraceEvent (Line 'All connect fibers have failed.' 'anynet.punch_connector')).event -eq 'peer_direct_failed') 'Direct failure missing'
        Assert ((Convert-AnyDeskTraceEvent (Line 'Connecting to relay relay-ab12.net.anydesk.com.cn (1/2)' 'anynet.relay_connector')).host -eq 'relay-ab12.net.anydesk.com.cn') 'CN relay not accepted'
        Assert (-not (Convert-AnyDeskTraceEvent (Line 'Connecting to relay secret.example.com (1/2)' 'anynet.relay_connector'))) 'Unexpected domain saved'
    }
    Test 'incremental reader preserves incomplete lines and does not reread unchanged data' {
        $p=Join-Path $temp 'partial.trace';[IO.File]::WriteAllText($p,"first`npart")
        $tail=New-Object FastLinkDiagnostics.TraceTail
        Assert (($tail.Read($p) -join ',') -eq 'first') 'Incomplete line escaped'
        [IO.File]::AppendAllText($p,"ial`n")
        Assert (($tail.Read($p) -join ',') -eq 'partial') 'Split line lost'
        Assert ($tail.Read($p).Count -eq 0 -and $tail.BytesRead -eq 0) 'Unchanged file reread'
        [IO.File]::WriteAllText($p,"new`n")
        Assert (($tail.Read($p) -join ',') -eq 'new' -and $tail.Reset) 'Truncation not detected'
    }
    Test 'trace bootstrap and ongoing read have hard byte limits' {
        $p=Join-Path $temp 'large.trace';[IO.File]::WriteAllText($p,(('line'+"`n")*450000))
        $tail=New-Object FastLinkDiagnostics.TraceTail;$null=$tail.Read($p)
        Assert ($tail.BytesRead -le 1MB) 'Bootstrap unbounded'
        [IO.File]::AppendAllText($p,(('more'+"`n")*70000));$null=$tail.Read($p)
        Assert ($tail.BytesRead -eq 262144 -and $tail.Backlog) 'Incremental read unbounded'
    }
    Test 'passive native metrics and port conversion are valid' {
        Assert ([FastLinkDiagnostics.NativeTelemetry]::Port(0xBB01) -eq 443) 'Port byte order incorrect'
        $tcp=[FastLinkDiagnostics.NativeTelemetry]::Tcp([int[]]@(555555))
        Assert ($tcp.IPv4Status -eq 0 -and $tcp.IPv6Status -eq 0 -and $tcp.Connections.Count -eq 0) 'TCP reader failed or leaked other apps'
        Assert ([FastLinkDiagnostics.NativeTelemetry]::CpuPercent(@(100,200,100),@(120,250,150)) -eq 80) 'CPU idle subtraction incorrect'
        Assert ($null -eq [FastLinkDiagnostics.NativeTelemetry]::CpuPercent($null,@(1,2,3))) 'First sample fabricated a CPU value'
    }
    $network=[pscustomobject]@{Timestamp=(Get-Date).ToString('o');WlanIp='192.0.2.10';WlanUp=$true;Gateway='192.0.2.1';GatewayReachable=$true;ChatGptHttps=$true;FastLinkCoreProcess=$true;FastLinkNodeSessions=1;TcpSegmentsResent=10L;TcpResetConnections=2L}
    Test 'no AnyDesk creates no high-frequency sample and uses idle cadence' {
        Invoke-AnyDeskObservation -NetworkSample $network -GatewayRttMs 1
        $status=Get-Content -LiteralPath (Join-Path $script:AnyDeskObserver.Root 'status.json') -Raw|ConvertFrom-Json
        Assert ($status.state -eq 'anydesk_not_running' -and $status.next_check_seconds -eq 30) 'Idle cadence incorrect'
    }
    Test 'session-start enables metadata sample and stop clears it' {
        $script:MockProcesses=@([pscustomobject]@{Id=555555;StartTime=(Get-Date).AddHours(-1);TotalProcessorTime=[timespan]::FromSeconds(1);WorkingSet64=12345678L})
        [IO.File]::AppendAllText($trace,((Line 'Session started (ok).')+"`n"+(Line 'Route type: tunnel (internet)' 'desk_rt.auto_adjust')+"`n"))
        $script:AnyDeskObserver.NextCheck=[datetime]::MinValue
        Invoke-AnyDeskObservation -NetworkSample $network -GatewayRttMs 2
        Assert ($script:AnyDeskObserver.Sessions.Count -eq 1) 'Active session missed'
        $samples=@(Get-Content -LiteralPath $script:AnyDeskObserver.LogPath|ForEach-Object {$_|ConvertFrom-Json}|Where-Object {$_.event -eq 'sample'})
        Assert ($samples.Count -gt 0 -and $samples[-1].active_sessions -eq 1 -and $samples[-1].gateway_rtt_ms -eq 2) 'Active evidence missing'
        [IO.File]::AppendAllText($trace,((Line 'Session stopped.')+"`n"))
        $script:AnyDeskObserver.NextCheck=[datetime]::MinValue;Invoke-AnyDeskObservation -NetworkSample $network
        Assert ($script:AnyDeskObserver.Sessions.Count -eq 0) 'Stopped session stayed active'
    }
    Test 'on-demand report distinguishes collected evidence from root-cause claims' {
        $reportScript=Join-Path (Split-Path $PSScriptRoot -Parent) 'diagnostics\Get-AnyDeskIncidentReport.ps1'
        $report=(& $reportScript -LogRoot $script:AnyDeskObserver.Root|Out-String)|ConvertFrom-Json
        Assert ($report.ActiveSamples -ge 1 -and $report.SessionStarts -ge 1) 'Report missed session evidence'
        Assert ($report.Limitations.Count -ge 4) 'Report lost evidence boundaries'
    }
    Test 'empty on-demand report does not fabricate a healthy session' {
        $empty=Join-Path $temp 'empty';New-Item -ItemType Directory -Path $empty|Out-Null
        $reportScript=Join-Path (Split-Path $PSScriptRoot -Parent) 'diagnostics\Get-AnyDeskIncidentReport.ps1'
        $report=(& $reportScript -LogRoot $empty|Out-String)|ConvertFrom-Json
        Assert ($report.ActiveSamples -eq 0 -and $report.SessionStarts -eq 0) 'Empty report invented a session'
    }
    Test 'slow collector automatically backs off' {
        $script:Slow=$true
        for($i=0;$i -lt 3;$i++){$script:AnyDeskObserver.NextCheck=[datetime]::MinValue;Invoke-AnyDeskObservation -NetworkSample $network}
        $script:Slow=$false
        Assert ($script:AnyDeskObserver.Skipped -ge 1 -and $script:AnyDeskObserver.NextCheck -gt (Get-Date).AddSeconds(50)) 'Slow sampling did not back off'
    }
    Test 'retention removes only owned files and enforces count' {
        $root=Join-Path $temp 'retention';New-Item -ItemType Directory -Path $root|Out-Null
        0..11|ForEach-Object {[IO.File]::WriteAllText((Join-Path $root ('anydesk-20990101-{0:000}.jsonl' -f $_)),'{}')}
        [IO.File]::WriteAllText((Join-Path $root 'unrelated.jsonl'),'keep')
        $old=Join-Path $root 'anydesk-20000101-000.jsonl';[IO.File]::WriteAllText($old,'{}');(Get-Item -LiteralPath $old).LastWriteTime=(Get-Date).AddDays(-15)
        Remove-AnyDeskOldLogs -Root $root
        Assert (@(Get-ChildItem -LiteralPath $root -Filter 'anydesk-*.jsonl').Count -eq 10) 'File limit failed'
        Assert (Test-Path -LiteralPath (Join-Path $root 'unrelated.jsonl')) 'Unrelated file deleted'
        Assert (-not (Test-Path -LiteralPath $old)) 'Expired log retained'
    }
    Test 'log rotation prevents individual files exceeding five MiB' {
        [IO.File]::WriteAllText($script:AnyDeskObserver.LogPath,('x'*5MB))
        $old=$script:AnyDeskObserver.LogPath
        Write-AnyDeskRecord @{timestamp=(Get-Date).ToString('o');event='rotation_test'}
        Assert ($old -ne $script:AnyDeskObserver.LogPath) 'Full file not rotated'
        Assert ((Get-Item -LiteralPath $script:AnyDeskObserver.LogPath).Length -lt 5MB) 'New file exceeds cap'
    }
    "ANYDESK_OBSERVER_TESTS_OK $script:Passed (synthetic sessions; no network changes or probes)"
}finally{
    if($temp.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path $temp -Leaf) -like 'AnyDeskObserver-tests-*'){Remove-Item -LiteralPath $temp -Recurse -Force}
}
