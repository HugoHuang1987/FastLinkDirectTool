[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path (Split-Path -Parent $projectRoot) 'codex_tasks\network-modes-20261125\window-activation-tests'
}
$testSource = Join-Path $PSScriptRoot 'WindowActivationTests.cs'
$code = [regex]::Replace([IO.File]::ReadAllText($testSource), '(?s)//[^\r\n]*|/\*.*?\*/|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|''(?:\\.|[^''\\])*''', ' ')
foreach ($pattern in @(
    '\busing\s+System\.(?:IO|Diagnostics|Reflection|Windows\.Forms|Net|Management|Runtime\.InteropServices)\b',
    '\b(?:Form|Application|MainForm|Program|RuntimeController|ConfigStore|NetworkDetector|RouteManager|DnsProxy|NrptManager|DualModeForm|DualNetworkRuntime|DualNetworkWindowsPlatform|DualNetworkDns)\s*[.(]',
    '\b(?:File|Directory|Process|Registry|Dns|Socket|HttpClient|WebRequest|WebClient|Activator|Assembly)\s*[.(]',
    '\b(?:DllImport|LibraryImport|extern|unsafe)\b'
)) {
    if ([regex]::IsMatch($code, $pattern)) { throw ('Console-only test source guard rejected: ' + $pattern) }
}
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$executable = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'WindowActivationTests.exe'
$sources = @(& (Join-Path $projectRoot 'ProductionSources.ps1'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /optimize+ /platform:anycpu /codepage:65001 `
    /main:FastLinkDirectTool.Tests.WindowActivationTests `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Xml.dll `
    ("/out:" + $executable) $sources $testSource
if ($LASTEXITCODE -ne 0) { throw 'Window activation test compilation failed.' }
$output = @(& $executable)
$testExitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Output $_ }
if ($testExitCode -ne 0 -or -not ($output -match '^WINDOW_ACTIVATION_TESTS_OK [1-9][0-9]*$')) {
    throw 'Window activation tests failed.'
}
'CONSOLE_ONLY_NO_FORMS_NO_NETWORK'
