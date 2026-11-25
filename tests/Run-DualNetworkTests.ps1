[CmdletBinding()]
param([string[]]$Suite = @('DualNetworkRuntime','DualNetworkWindows','DualNetworkDns','DualRuleMigration','DualModeUi','DualInternetProbe'), [switch]$WorkerTests)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$sources = @(& (Join-Path $project 'ProductionSources.ps1'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$leaf = 'FastLink-dual-tests-' + [guid]::NewGuid().ToString('N')
$dir = Join-Path $parent $leaf
New-Item -ItemType Directory -Path $dir |Out-Null
try {
    foreach($s in $Suite) {
        if($s -notin @('DualNetworkRuntime','DualNetworkWindows','DualNetworkDns','DualRuleMigration','DualModeUi','DualInternetProbe')){throw 'Unknown test suite'}
        $test = Join-Path $PSScriptRoot ($s + 'Tests.cs')
        if(-not(Test-Path -LiteralPath $test)){throw ('Test file not ready: '+$test)}
        $out = Join-Path $dir ($s + '.exe')
        & $compiler /nologo /target:exe /optimize+ /platform:anycpu /codepage:65001 `
            /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
            /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Xml.dll `
            ('/main:FastLinkDirectTool.Tests.' + $s + 'Tests') ('/out:' + $out) $sources $test
        if($LASTEXITCODE -ne 0){throw ($s + ' compile failed')}
        if ($s -eq 'DualNetworkWindows' -and $WorkerTests) {
            # Private hidden child: arithmetic, Unicode, bounded sleep/timeout;
            # no real network commands and no production application startup.
            & $out --worker-tests
        } else { & $out }
        if($LASTEXITCODE -ne 0){throw ($s + ' test failed')}
    }
    'DUAL_TEST_RUN_COMPLETE (injected mock platforms/packets; no production startup)'
}
finally {
    if(Test-Path -LiteralPath $dir){
        $resolved=(Resolve-Path -LiteralPath $dir).Path
        if([IO.Path]::GetFullPath((Split-Path -Parent $resolved)).TrimEnd('\') -ne $parent -or (Split-Path -Leaf $resolved) -ne $leaf){throw 'Refusing unsafe cleanup'}
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
