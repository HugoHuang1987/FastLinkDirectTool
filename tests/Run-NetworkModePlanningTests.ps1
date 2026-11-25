[CmdletBinding()]
param()

# Offline only: compile a separate test entry point into an owned temporary folder.
# This script never starts the helper, reads a live network, or changes network state.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$productionSources = @(& (Join-Path $projectRoot 'ProductionSources.ps1'))
$planner = Join-Path $projectRoot 'NetworkModePlanning.cs'
$testSource = Join-Path $PSScriptRoot 'NetworkModePlanningTests.cs'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

function Assert-OfflineSource([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw ('Required source is missing: ' + $Path)
    }
    $source = [IO.File]::ReadAllText($Path)
    # Ignore comments and literals; then reject live I/O or production entry points.
    # This is a fail-closed review guard, not a substitute for a code audit.
    $code = [regex]::Replace($source, '(?s)//[^\r\n]*|/\*.*?\*/|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|''(?:\\.|[^''\\])*''', ' ')
    $forbidden = @(
        '\busing\s+(?:[\w]+\s*=\s*)?(?:System\.(?:IO|Diagnostics|Reflection|Runtime\.InteropServices|Management|ServiceProcess|Threading|Security|Windows\.Forms|Net\.Sockets|Net\.NetworkInformation)|Microsoft\.Win32)(?:\.|\s*;)',
        '\b(?:DllImport|LibraryImport|extern|unsafe)\b',
        '\bSystem\.(?:IO\.|Diagnostics\.|Reflection\.|Runtime\.InteropServices\.|Net\.(?:Dns\b|Sockets\.|NetworkInformation\.|Http\.))',
        '\b(?:File|Directory|FileStream|StreamWriter|Process|ProcessStartInfo|Registry|RegistryKey|Dns|HttpClient|WebRequest|WebClient|TcpClient|UdpClient|Socket|Marshal|Activator|Assembly)\s*[.(]',
        '\b(?:ConfigStore|RuntimeController|MainForm|Program|ProcessRunner|RouteManager|DnsProxy|NrptManager|SystemProxyBypassManager|NetworkDetector|HealthLog|HealthEventLogger|AppLogger|Logger)\s*[.(]'
    )
    foreach ($pattern in $forbidden) {
        $match = [regex]::Match($code, $pattern)
        if ($match.Success) {
            throw ('Offline source guard rejected ' + [IO.Path]::GetFileName($Path) + ': ' + $match.Value)
        }
    }
}

Assert-OfflineSource $planner
Assert-OfflineSource $testSource
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The .NET Framework C# compiler is not available.'
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$testLeaf = 'FastLinkDirectTool-mode-planning-' + [Guid]::NewGuid().ToString('N')
$testDirectory = Join-Path $tempRoot $testLeaf
$null = New-Item -ItemType Directory -Path $testDirectory
$executable = Join-Path $testDirectory 'NetworkModePlanningTests.exe'
try {
    $arguments = @(
        '/nologo', '/target:exe', '/optimize+', '/platform:anycpu', '/codepage:65001',
        '/main:FastLinkDirectTool.Tests.NetworkModePlanningTests',
        '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
        '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Xml.dll',
        ('/out:' + $executable)
        $productionSources
        $testSource
    )
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw ('Offline test compilation failed: ' + $LASTEXITCODE) }
    $output = @(& $executable)
    $testExitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Output $_ }
    if ($testExitCode -ne 0) { throw ('Offline tests failed: ' + $testExitCode) }
    if (-not ($output -match '^NETWORK_MODE_PLANNING_TESTS_OK [1-9][0-9]*$')) {
        throw 'The offline test completion marker was not produced.'
    }
    Write-Output 'OFFLINE_SOURCE_GUARD_OK 2'
}
finally {
    if (Test-Path -LiteralPath $testDirectory) {
        $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $testDirectory).Path).TrimEnd('\', '/')
        $parent = [IO.Path]::GetFullPath((Split-Path -Parent $resolved)).TrimEnd('\', '/')
        if (-not [string]::Equals($parent, $tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals((Split-Path -Leaf $resolved), $testLeaf, [StringComparison]::Ordinal) -or
            -not $testLeaf.StartsWith('FastLinkDirectTool-mode-planning-', [StringComparison]::Ordinal)) {
            throw 'Refusing cleanup outside the exact owned temporary test folder.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
