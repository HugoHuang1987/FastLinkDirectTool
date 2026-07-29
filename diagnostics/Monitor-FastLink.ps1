param(
    [int]$DurationHours = 8,
    [int]$IntervalSeconds = 3,
    [string[]]$NodeCandidates = @(),
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
$logPath = Join-Path $diagnosticRoot ('fastlink-monitor-{0}.csv' -f $startedAt.ToString('yyyyMMdd-HHmmss'))
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
        [int]$TimeoutMilliseconds = 800
    )

    if ([string]::IsNullOrWhiteSpace($Address)) {
        return $false
    }

    $ping = $null
    try {
        $ping = New-Object System.Net.NetworkInformation.Ping
        $reply = $ping.Send($Address, $TimeoutMilliseconds)
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
        [int]$TimeoutMilliseconds = 5000
    )

    try {
        $request = [System.Net.HttpWebRequest]::Create($Uri)
        $request.Method = 'HEAD'
        $request.AllowAutoRedirect = $false
        $request.Timeout = $TimeoutMilliseconds
        $request.ReadWriteTimeout = $TimeoutMilliseconds
        $response = $request.GetResponse()
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
}

$knownNodes = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($candidate in $NodeCandidates) {
    foreach ($address in ($candidate -split ',')) {
        $parsedAddress = $null
        if ([System.Net.IPAddress]::TryParse($address.Trim(), [ref]$parsedAddress)) {
            [void]$knownNodes.Add($parsedAddress.ToString())
        }
    }
}

$deadline = $startedAt.AddHours([Math]::Max(1, $DurationHours))
$sampleCount = 0
try {
    while ((Get-Date) -lt $deadline) {
        $sampleTime = Get-Date
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
        if ($core) {
            $nodeConnections = @(
                Get-NetTCPConnection -OwningProcess $core.Id -State Established -ErrorAction SilentlyContinue |
                    Where-Object { $_.RemotePort -eq 10151 -and $_.RemoteAddress -notlike '198.18.*' }
            )
            foreach ($address in ($nodeConnections.RemoteAddress | Sort-Object -Unique)) {
                if (-not [string]::IsNullOrWhiteSpace($address)) {
                    [void]$knownNodes.Add($address)
                }
            }
        }

        $nodes = @($knownNodes | Select-Object -First 2)
        $node1 = if ($nodes.Count -ge 1) { $nodes[0] } else { $null }
        $node2 = if ($nodes.Count -ge 2) { $nodes[1] } else { $null }

        $sample = [pscustomobject]@{
            Timestamp              = $sampleTime.ToString('o')
            WlanUp                = [bool]($wlan -and $wlan.Status -eq 'Up')
            WlanIp                = $wlanIp
            Gateway               = $gateway
            GatewayReachable      = Test-Icmp -Address $gateway
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
            Node1                 = $node1
            Node1Reachable        = Test-TcpConnectionBound -RemoteAddress $node1 -RemotePort 10151 -LocalAddress $wlanIp
            Node2                 = $node2
            Node2Reachable        = Test-TcpConnectionBound -RemoteAddress $node2 -RemotePort 10151 -LocalAddress $wlanIp
            ChatGptTcp            = Test-TcpConnectionBound -RemoteAddress 'chatgpt.com' -RemotePort 443 -LocalAddress $null
            ChatGptHttps          = Test-HttpsTransport -Uri 'https://chatgpt.com/favicon.ico'
        }

        $sample | Export-Csv -LiteralPath $logPath -Append -NoTypeInformation -Encoding UTF8
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
