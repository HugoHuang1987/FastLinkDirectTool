param([switch]$RunAfterBuild)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator
)
if (-not $isAdmin) {
    $elevatedArgs = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $MyInvocation.MyCommand.Path)
    )
    if ($RunAfterBuild) { $elevatedArgs += '-RunAfterBuild' }
    $process = Start-Process powershell.exe -Verb RunAs -PassThru -WindowStyle Hidden -ArgumentList $elevatedArgs
    $process.WaitForExit()
    exit $process.ExitCode
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $root 'FastLinkDirectTool.cs'
$manifest = Join-Path $root 'app.manifest'
$output = Join-Path $root 'FastLinkDirectTool.exe'
$stagedOutput = Join-Path $root 'FastLinkDirectTool.new.exe'
$log = Join-Path $root 'build.log'
$productionSources = @(& (Join-Path $root 'ProductionSources.ps1'))

if (-not (Test-Path $compiler)) {
    throw 'Windows C# compiler was not found.'
}

$compilerOutput = & $compiler `
        /nologo `
        /target:winexe `
        /optimize+ `
        /platform:anycpu `
        /codepage:65001 `
        /win32manifest:$manifest `
        /reference:System.dll `
        /reference:System.Core.dll `
        /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll `
        /reference:System.Web.Extensions.dll `
        /reference:System.Xml.dll `
        /out:$stagedOutput `
        $productionSources 2>&1

$compilerOutput | Set-Content -LiteralPath $log -Encoding UTF8

if ($LASTEXITCODE -ne 0) {
    Add-Content -LiteralPath $log -Value "Build failed with exit code $LASTEXITCODE."
    throw "Build failed with exit code $LASTEXITCODE."
}

if (Get-Process -Name 'FastLinkDirectTool' -ErrorAction SilentlyContinue) {
    throw 'The helper is running. Use its tray Exit and Clean command before replacing it; never force-stop a DNS listener.'
}
Move-Item -LiteralPath $stagedOutput -Destination $output -Force

Get-Item $output | Select-Object FullName, Length, LastWriteTime
Add-Content -LiteralPath $log -Value 'BUILD_OK'

if ($RunAfterBuild) {
    Start-Process -FilePath $output -ArgumentList '--autostart' -WindowStyle Hidden
}
