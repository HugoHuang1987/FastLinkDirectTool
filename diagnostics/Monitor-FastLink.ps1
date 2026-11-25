param(
    [int]$DurationHours = 8,
    [int]$IntervalSeconds = 3,
    [string[]]$NodeCandidates = @(),
    [int[]]$NodePorts = @(10131, 10141, 10151),
    [int]$MaxSamples = 0
)

$ErrorActionPreference = 'SilentlyContinue'
$createdNew = $false
$mutex = [System.Threading.Mutex]::new(
    $true,
    'Local\FastLinkDirectTool.DiagnosticMonitor',
    [ref]$createdNew
)
if (-not $createdNew) {
    exit 0
}

$diagnosticRoot = Join-Path $env:LOCALAPPDATA 'FastLinkDirectTool\diagnostics'
New-Item -ItemType Directory -Path $diagnosticRoot -Force | Out-Null
$startedAt = Get-Date
$currentLogDate = $startedAt.Date
$logPath = Join-Path $diagnosticRoot ('fastlink-monitor-{0}-v3.csv' -f $currentLogDate.ToString('yyyyMMdd'))
$latestPath = Join-Path $diagnosticRoot 'latest-monitor.txt'
$logPath | Set-Content -LiteralPath $latestPath -Encoding UTF8

function Test-TcpConnectionBound {
    param(
        [string]$RemoteAddress,
        [int]$RemotePort,
        [string]$LocalAddress,
        [int]$TimeoutMilliseconds = 1200
    )

    if ([string]::IsNullOrWhiteSpace($RemoteAddress)) {
        return $false
    }

    $client = $null
    try {
        $client = New-Object System.Net.Sockets.TcpClient([System.Net.Sockets.AddressFamily]::InterNetwork)
        if (-not [string]::IsNullOrWhiteSpace($LocalAddress)) {
            $localEndpoint = New-Object System.Net.IPEndPoint(
                [System.Net.IPAddress]::Parse($LocalAddress),
                0
            )
            $client.Client.Bind($localEndpoint)
        }
        $asyncResult = $client.BeginConnect($RemoteAddress, $RemotePort, $null, $null)
        if (-not $asyncResult.AsyncWaitHandle.WaitOne($TimeoutMilliseconds, $false)) {
            return $false
        }
        $client.EndConnect($asyncResult)
        return $client.Connected
    }
    catch {
        return $false
    }
    finally {
        if ($client) {
            try { $client.Close() } catch {}
        }
    }
}

function Test-Icmp {
    param(
        [string]$Address,
        [int]$TimeoutMilliseconds = 800,
        [ref]$RoundtripMilliseconds
    )

    if ($RoundtripMilliseconds) { $RoundtripMilliseconds.Value = $null }

    if ([string]::IsNullOrWhiteSpace($Address)) {
        return $false
    }

    $ping = $null
    try {
        $ping = New-Object System.Net.NetworkInformation.Ping
        $reply = $ping.Send($Address, $TimeoutMilliseconds)
        if ($RoundtripMilliseconds -and $reply.Status -eq [System.Net.NetworkInformation.IPStatus]::Success) {
            $RoundtripMilliseconds.Value = [long]$reply.RoundtripTime
        }
        return $reply.Status -eq [System.Net.NetworkInformation.IPStatus]::Success
    }
    catch {
        return $false
    }
    finally {
        if ($ping) {
            $ping.Dispose()
        }
    }
}

function Test-HttpsTransport {
    param(
        [string]$Uri,
        [int]$TimeoutMilliseconds = 7000
    )

    $request = $null
    $asyncResult = $null
    $response = $null
    try {
        $request = [System.Net.HttpWebRequest]::Create($Uri)
        $request.Method = 'HEAD'
        $request.AllowAutoRedirect = $false
        $request.Timeout = $TimeoutMilliseconds
        $request.ReadWriteTimeout = $TimeoutMilliseconds
        $asyncResult = $request.BeginGetResponse($null, $null)
        if (-not $asyncResult.AsyncWaitHandle.WaitOne($TimeoutMilliseconds, $false)) {
            try { $request.Abort() } catch {}
            return $false
        }
        $response = $request.EndGetResponse($asyncResult)
        $response.Close()
        return $true
    }
    catch [System.Net.WebException] {
        if ($_.Exception.Response) {
            try { $_.Exception.Response.Close() } catch {}
            return $true
        }
        return $false
    }
    catch {
        return $false
    }
    finally {
        if ($response) {
            try { $response.Close() } catch {}
        }
        if ($asyncResult) {
            try { $asyncResult.AsyncWaitHandle.Close() } catch {}
        }
    }
}

$knownNodes = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($candidate in $NodeCandidates) {
    foreach ($endpoint in ($candidate -split ',')) {
        $parts = $endpoint.Trim() -split ':'
        $address = $parts[0]
        $port = if ($parts.Count -ge 2) { [int]$parts[1] } else { $NodePorts[0] }
        $parsedAddress = $null
        if ([System.Net.IPAddress]::TryParse($address.Trim(), [ref]$parsedAddress) -and $port -gt 0) {
            [void]$knownNodes.Add(('{0}:{1}' -f $parsedAddress, $port))
        }
    }
}

$deadline = $startedAt.AddHours([Math]::Max(1, $DurationHours))
$sampleCount = 0
$anyDeskObserverReady = $false
try {
    . (Join-Path $PSScriptRoot 'AnyDesk-Observer.ps1')
    Initialize-AnyDeskObserver
    $anyDeskObserverReady = $true
}
catch {
    # The optional observer must never stop the existing network collector.
    @{ timestamp=(Get-Date).ToString('o'); event='anydesk_observer_init_failed'; error_type=$_.Exception.GetType().Name } |
        ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $diagnosticRoot 'observer-init-error.json') -Encoding UTF8
}
try {
    while ((Get-Date) -lt $deadline) {
        $sampleTime = Get-Date
        if ($sampleTime.Date -ne $currentLogDate) {
            $currentLogDate = $sampleTime.Date
            $logPath = Join-Path $diagnosticRoot ('fastlink-monitor-{0}-v3.csv' -f $currentLogDate.ToString('yyyyMMdd'))
            $logPath | Set-Content -LiteralPath $latestPath -Encoding UTF8
        }
        $wlan = Get-NetAdapter -Name 'WLAN' -ErrorAction SilentlyContinue
        $fastLink = Get-NetAdapter -Name 'FastLink' -ErrorAction SilentlyContinue
        $wlanConfig = Get-NetIPConfiguration -InterfaceAlias 'WLAN' -ErrorAction SilentlyContinue
        $wlanDhcp = Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True' -ErrorAction SilentlyContinue |
            Where-Object { $_.Description -match 'Intel.*Wi-Fi 6E AX211' } |
            Select-Object -First 1
        $wlanIp = @($wlanConfig.IPv4Address | Select-Object -First 1 -ExpandProperty IPAddress)[0]
        $gateway = @($wlanConfig.IPv4DefaultGateway | Select-Object -First 1 -ExpandProperty NextHop)[0]
        $core = Get-Process -Name 'FastLinkCore' -ErrorAction SilentlyContinue | Select-Object -First 1
        $gui = Get-Process -Name 'FastLink' -ErrorAction SilentlyContinue | Select-Object -First 1
        $helper = Get-Process -Name 'FastLinkDirectTool' -ErrorAction SilentlyContinue | Select-Object -First 1
        $fastLinkDefaultRoute = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
            Where-Object { $_.InterfaceAlias -eq 'FastLink' } |
            Select-Object -First 1

        $nodeConnections = @()
        $nodeConnecting = @()
        $coreExternalConnections = @()
        $coreOutboundConnecting = @()
        if ($core) {
            $coreTcp = @(Get-NetTCPConnection -OwningProcess $core.Id -ErrorAction SilentlyContinue)
            $nodeConnections = @($coreTcp | Where-Object {
                $_.State -eq 'Established' -and $_.RemotePort -in $NodePorts -and $_.RemoteAddress -notlike '198.18.*'
            })
            $nodeConnecting = @($coreTcp | Where-Object {
                $_.State -eq 'SynSent' -and $_.RemotePort -in $NodePorts -and $_.RemoteAddress -notlike '198.18.*'
            })
            $coreExternalConnections = @($coreTcp | Where-Object {
                $_.State -eq 'Established' -and $_.RemoteAddress -notlike '198.18.*' -and $_.RemoteAddress -ne '127.0.0.1'
            })
            $coreOutboundConnecting = @($coreTcp | Where-Object {
                $_.State -eq 'SynSent' -and $_.RemotePort -eq 443 -and $_.RemoteAddress -notlike '198.18.*'
            })
            foreach ($connection in $nodeConnections) {
                if (-not [string]::IsNullOrWhiteSpace($connection.RemoteAddress)) {
                    [void]$knownNodes.Add(('{0}:{1}' -f $connection.RemoteAddress, $connection.RemotePort))
                }
            }
        }

        $nodes = @($knownNodes | Select-Object -First 2)
        $node1 = if ($nodes.Count -ge 1) { [string]$nodes[0] } else { $null }
        $node2 = if ($nodes.Count -ge 2) { [string]$nodes[1] } else { $null }
        $node1Parts = if ($node1) { $node1 -split ':' } else { @() }
        $node2Parts = if ($node2) { $node2 -split ':' } else { @() }
        $node1Address = if ($node1Parts.Count -ge 1) { $node1Parts[0] } else { $null }
        $node2Address = if ($node2Parts.Count -ge 1) { $node2Parts[0] } else { $null }
        $node1Port = if ($node1Parts.Count -ge 2) { [int]$node1Parts[1] } else { 0 }
        $node2Port = if ($node2Parts.Count -ge 2) { [int]$node2Parts[1] } else { 0 }
        $tcpStats = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetTcpIPv4Statistics()
        $gatewayRtt = $null
        $gatewayReachable = Test-Icmp -Address $gateway -RoundtripMilliseconds ([ref]$gatewayRtt)

        $sample = [pscustomobject]@{
            SchemaVersion          = 3
            Timestamp              = $sampleTime.ToString('o')
            WlanUp                = [bool]($wlan -and $wlan.Status -eq 'Up')
            WlanIp                = $wlanIp
            Gateway               = $gateway
            GatewayReachable      = $gatewayReachable
            DhcpServer             = [string]$wlanDhcp.DHCPServer
            DhcpLeaseObtained      = if ($wlanDhcp) { ([datetime]$wlanDhcp.DHCPLeaseObtained).ToString('o') } else { $null }
            DhcpLeaseExpires       = if ($wlanDhcp) { ([datetime]$wlanDhcp.DHCPLeaseExpires).ToString('o') } else { $null }
            PhysicalInternetTcp   = Test-TcpConnectionBound -RemoteAddress '1.1.1.1' -RemotePort 853 -LocalAddress $wlanIp
            FastLinkAdapterUp     = [bool]($fastLink -and $fastLink.Status -eq 'Up')
            FastLinkDefaultRoute  = [bool]$fastLinkDefaultRoute
            FastLinkProcess       = [bool]$gui
            FastLinkCoreProcess   = [bool]$core
            DirectHelperProcess   = [bool]$helper
            FastLinkNodeSessions  = $nodeConnections.Count
            FastLinkNodeConnecting = $nodeConnecting.Count
            FastLinkCoreExternalSessions = $coreExternalConnections.Count
            FastLinkCoreSynSent443 = $coreOutboundConnecting.Count
            FastLinkCoreWorkingSetMB = if ($core) { [Math]::Round($core.WorkingSet64 / 1MB, 1) } else { $null }
            FastLinkCoreCpuSeconds = if ($core) { [Math]::Round($core.TotalProcessorTime.TotalSeconds, 1) } else { $null }
            FastLinkCoreHandles    = if ($core) { $core.HandleCount } else { $null }
            FastLinkCoreThreads    = if ($core) { $core.Threads.Count } else { $null }
            TcpFailedConnections   = if ($tcpStats) { $tcpStats.FailedConnectionAttempts } else { $null }
            TcpResetConnections    = if ($tcpStats) { $tcpStats.ResetConnections } else { $null }
            TcpSegmentsResent      = if ($tcpStats) { $tcpStats.SegmentsResent } else { $null }
            Node1                 = $node1
            Node1Reachable        = Test-TcpConnectionBound -RemoteAddress $node1Address -RemotePort $node1Port -LocalAddress $wlanIp
            Node2                 = $node2
            Node2Reachable        = Test-TcpConnectionBound -RemoteAddress $node2Address -RemotePort $node2Port -LocalAddress $wlanIp
            ChatGptTcp            = Test-TcpConnectionBound -RemoteAddress 'chatgpt.com' -RemotePort 443 -LocalAddress $null
            ChatGptHttps          = Test-HttpsTransport -Uri 'https://chatgpt.com/favicon.ico'
        }

        $sample | Export-Csv -LiteralPath $logPath -Append -NoTypeInformation -Encoding UTF8
        if ($anyDeskObserverReady) {
            try { Invoke-AnyDeskObservation -NetworkSample $sample -GatewayRttMs $gatewayRtt }
            catch { } # Observer I/O failure cannot interrupt base diagnostics.
        }
        $sampleCount++
        if ($MaxSamples -gt 0 -and $sampleCount -ge $MaxSamples) {
            break
        }
        Start-Sleep -Seconds ([Math]::Max(2, $IntervalSeconds))
    }
}
finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
