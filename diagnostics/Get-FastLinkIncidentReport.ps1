param(
    [int]$LookbackHours = 24
)

$ErrorActionPreference = 'SilentlyContinue'
$root = Join-Path $env:LOCALAPPDATA 'FastLinkDirectTool'
$diagnosticRoot = Join-Path $root 'diagnostics'
$healthPath = Join-Path $root 'health-events.jsonl'
$reportPath = Join-Path $diagnosticRoot 'latest-incident-report.json'
$since = (Get-Date).AddHours(-[Math]::Max(1, $LookbackHours))

function Convert-ToBoolean {
    param($Value)
    if ($Value -is [bool]) { return $Value }
    return [string]$Value -eq 'True'
}

function Convert-ToInt {
    param($Value)
    $number = 0
    if ([int]::TryParse([string]$Value, [ref]$number)) { return $number }
    return 0
}

$healthEvents = @()
if (Test-Path -LiteralPath $healthPath) {
    $healthEvents = @(
        Get-Content -LiteralPath $healthPath |
            ForEach-Object {
                try { $_ | ConvertFrom-Json } catch { $null }
            } |
            Where-Object { $_ -and [datetime]$_.timestamp -ge $since }
    )
}

$monitorRows = @()
$monitorFiles = @(
    Get-ChildItem -LiteralPath $diagnosticRoot -Filter 'fastlink-monitor-*.csv' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $since.AddDays(-1) } |
        Sort-Object FullName -Unique
)
foreach ($file in $monitorFiles) {
    $monitorRows += @(
        Import-Csv -LiteralPath $file.FullName |
            Where-Object { $_.Timestamp -and [datetime]$_.Timestamp -ge $since }
    )
}
$monitorRows = @($monitorRows | Sort-Object { [datetime]$_.Timestamp })

$incidents = @()
$failedEvents = @($healthEvents | Where-Object { $_.event -eq 'proxy_probe_failed' })
foreach ($group in ($failedEvents | Group-Object incident_id)) {
    $incidentId = [string]$group.Name
    $failures = @($group.Group | Sort-Object { [datetime]$_.timestamp })
    if ($failures.Count -eq 0) { continue }
    $startObserved = [datetime]$failures[0].timestamp
    $firstProbeElapsed = Convert-ToInt $failures[0].probe_elapsed_ms
    $start = $startObserved.AddMilliseconds(-$firstProbeElapsed)
    $terminal = @(
        $healthEvents | Where-Object {
            [string]$_.incident_id -eq $incidentId -and
            $_.event -in @('proxy_probe_recovered', 'proxy_recovery_completed', 'proxy_recovery_failed') -and
            [datetime]$_.timestamp -ge $start
        } | Sort-Object { [datetime]$_.timestamp } | Select-Object -First 1
    )
    $end = if ($terminal.Count -gt 0) { [datetime]$terminal[0].timestamp } else { Get-Date }
    $windowRows = @(
        $monitorRows | Where-Object {
            $time = [datetime]$_.Timestamp
            $time -ge $start.AddSeconds(-15) -and $time -le $end.AddSeconds(15)
        }
    )
    $routeEvents = @(
        $healthEvents | Where-Object {
            $_.event -in @('route_command_timeout', 'route_mutation_suppressed', 'dns_route_failure') -and
            [datetime]$_.timestamp -ge $start.AddSeconds(-15) -and
            [datetime]$_.timestamp -le $end.AddSeconds(15)
        }
    )
    $routeRootEvents = @($routeEvents | Where-Object {
        [datetime]$_.timestamp -le $startObserved.AddSeconds(2)
    })

    $allWlanUp = $windowRows.Count -gt 0 -and @($windowRows | Where-Object {
        -not (Convert-ToBoolean $_.WlanUp)
    }).Count -eq 0
    $gatewayObserved = @($windowRows | Where-Object { Convert-ToBoolean $_.GatewayReachable }).Count -gt 0
    $coreHealthy = $windowRows.Count -gt 0 -and @($windowRows | Where-Object {
        -not (Convert-ToBoolean $_.FastLinkAdapterUp) -or -not (Convert-ToBoolean $_.FastLinkCoreProcess)
    }).Count -eq 0
    $v2Rows = @($windowRows | Where-Object { (Convert-ToInt $_.SchemaVersion) -ge 2 })
    $nodeEvidenceAvailable = $v2Rows.Count -gt 0
    $nodeHealthy = $nodeEvidenceAvailable -and @($v2Rows | Where-Object {
        (Convert-ToInt $_.FastLinkNodeSessions) -gt 0
    }).Count -gt 0
    $wlanHealthy = $allWlanUp -and ($gatewayObserved -or $nodeHealthy -or @($windowRows | Where-Object {
        Convert-ToBoolean $_.ChatGptTcp
    }).Count -gt 0)
    $httpsFailures = @($windowRows | Where-Object { -not (Convert-ToBoolean $_.ChatGptHttps) }).Count
    $maxSynSent443 = 0
    $maxNodeConnecting = 0
    $minNodeSessions = 0
    $failedConnectionDelta = 0
    $resetConnectionDelta = 0
    $segmentsResentDelta = 0
    if ($v2Rows.Count -gt 0) {
        $maxSynSent443 = ($v2Rows | ForEach-Object { Convert-ToInt $_.FastLinkCoreSynSent443 } | Measure-Object -Maximum).Maximum
        $maxNodeConnecting = ($v2Rows | ForEach-Object { Convert-ToInt $_.FastLinkNodeConnecting } | Measure-Object -Maximum).Maximum
        $minNodeSessions = ($v2Rows | ForEach-Object { Convert-ToInt $_.FastLinkNodeSessions } | Measure-Object -Minimum).Minimum
        $orderedV2 = @($v2Rows | Sort-Object { [datetime]$_.Timestamp })
        if ($orderedV2.Count -ge 2) {
            $failedConnectionDelta = (Convert-ToInt $orderedV2[-1].TcpFailedConnections) - (Convert-ToInt $orderedV2[0].TcpFailedConnections)
            $resetConnectionDelta = (Convert-ToInt $orderedV2[-1].TcpResetConnections) - (Convert-ToInt $orderedV2[0].TcpResetConnections)
            $segmentsResentDelta = (Convert-ToInt $orderedV2[-1].TcpSegmentsResent) - (Convert-ToInt $orderedV2[0].TcpSegmentsResent)
        }
    }

    $classification = 'insufficient_monitor_evidence'
    if ($routeRootEvents.Count -gt 0) {
        $classification = 'local_route_or_dns_failure'
    }
    elseif ($windowRows.Count -gt 0 -and -not $wlanHealthy) {
        $classification = 'physical_wlan_or_gateway_failure'
    }
    elseif ($windowRows.Count -gt 0 -and -not $coreHealthy) {
        $classification = 'fastlink_adapter_or_core_failure'
    }
    elseif ($nodeEvidenceAvailable -and -not $nodeHealthy) {
        $classification = 'fastlink_node_connection_failure'
    }
    elseif ($nodeEvidenceAvailable -and $maxNodeConnecting -ge 3) {
        $classification = 'fastlink_node_pool_degradation'
    }
    elseif ($nodeEvidenceAvailable -and $maxSynSent443 -ge 10) {
        $classification = 'fastlink_proxy_exit_congestion'
    }
    elseif ($wlanHealthy -and $coreHealthy -and ($nodeHealthy -or -not $nodeEvidenceAvailable)) {
        $classification = 'fastlink_proxy_transport_or_exit_congestion'
    }

    $incidents += [pscustomobject]@{
        IncidentId             = $incidentId
        StartedAt              = $start.ToString('o')
        EndedAt                = if ($terminal.Count -gt 0) { $end.ToString('o') } else { $null }
        DurationSeconds        = [Math]::Round(($end - $start).TotalSeconds, 1)
        ProbeFailureCount      = $failures.Count
        TerminalEvent          = if ($terminal.Count -gt 0) { [string]$terminal[0].event } else { $null }
        AutomaticRestartUsed   = @($healthEvents | Where-Object {
            [string]$_.incident_id -eq $incidentId -and $_.event -eq 'proxy_recovery_started'
        }).Count -gt 0
        Classification         = $classification
        MonitorSamples         = $windowRows.Count
        WlanAndGatewayHealthy  = $wlanHealthy
        FastLinkCoreHealthy    = $coreHealthy
        NodeEvidenceAvailable  = $nodeEvidenceAvailable
        NodeSessionsHealthy    = $nodeHealthy
        ChatGptHttpsFailures   = $httpsFailures
        MaxCoreSynSent443      = $maxSynSent443
        MaxNodeConnecting      = $maxNodeConnecting
        MinNodeSessions        = $minNodeSessions
        TcpFailedConnectionDelta = [Math]::Max(0, $failedConnectionDelta)
        TcpResetConnectionDelta = [Math]::Max(0, $resetConnectionDelta)
        TcpSegmentsResentDelta = [Math]::Max(0, $segmentsResentDelta)
        RouteOrDnsEvents       = $routeEvents.Count
        RouteOrDnsRootEvents   = $routeRootEvents.Count
    }
}

$latestMonitor = if ($monitorRows.Count -gt 0) { $monitorRows[-1] } else { $null }
$report = [pscustomobject]@{
    SchemaVersion = 1
    GeneratedAt = (Get-Date).ToString('o')
    LookbackHours = $LookbackHours
    Current = if ($latestMonitor) {
        [pscustomobject]@{
            SampleAt = [string]$latestMonitor.Timestamp
            WlanUp = Convert-ToBoolean $latestMonitor.WlanUp
            GatewayReachable = Convert-ToBoolean $latestMonitor.GatewayReachable
            FastLinkAdapterUp = Convert-ToBoolean $latestMonitor.FastLinkAdapterUp
            FastLinkCoreProcess = Convert-ToBoolean $latestMonitor.FastLinkCoreProcess
            FastLinkNodeSessions = Convert-ToInt $latestMonitor.FastLinkNodeSessions
            FastLinkNodeConnecting = Convert-ToInt $latestMonitor.FastLinkNodeConnecting
            FastLinkCoreSynSent443 = Convert-ToInt $latestMonitor.FastLinkCoreSynSent443
            ChatGptTcp = Convert-ToBoolean $latestMonitor.ChatGptTcp
            ChatGptHttps = Convert-ToBoolean $latestMonitor.ChatGptHttps
        }
    } else { $null }
    Summary = [pscustomobject]@{
        IncidentCount = $incidents.Count
        RouteOrDnsIncidentCount = @($incidents | Where-Object { $_.Classification -eq 'local_route_or_dns_failure' }).Count
        PhysicalNetworkIncidentCount = @($incidents | Where-Object { $_.Classification -eq 'physical_wlan_or_gateway_failure' }).Count
        FastLinkCoreIncidentCount = @($incidents | Where-Object { $_.Classification -eq 'fastlink_adapter_or_core_failure' }).Count
        FastLinkNodeIncidentCount = @($incidents | Where-Object {
            $_.Classification -in @('fastlink_node_connection_failure', 'fastlink_node_pool_degradation')
        }).Count
        FastLinkTransportIncidentCount = @($incidents | Where-Object {
            $_.Classification -in @('fastlink_proxy_transport_or_exit_congestion', 'fastlink_proxy_exit_congestion')
        }).Count
    }
    Incidents = $incidents
}

New-Item -ItemType Directory -Path $diagnosticRoot -Force | Out-Null
$json = $report | ConvertTo-Json -Depth 8
$json | Set-Content -LiteralPath $reportPath -Encoding UTF8
$json
