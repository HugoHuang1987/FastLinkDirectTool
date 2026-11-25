[CmdletBinding()]
param([string]$OutputDirectory)

# Compile only. No elevation, production EXE replacement, task or network operation.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path (Split-Path -Parent $root) 'codex_tasks\network-modes-20261124\preview'
}
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$out = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'NetworkModePreview.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$productionSources = @(& (Join-Path $root 'ProductionSources.ps1'))
$args_ = @(
    '/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', '/codepage:65001',
    '/main:FastLinkDirectTool.Modes.NetworkModePreviewProgram',
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Xml.dll',
    ('/out:' + $out),
    $productionSources
    (Join-Path $root 'NetworkModePreview.cs')
)
& $compiler @args_
if ($LASTEXITCODE -ne 0) { throw 'Preview compilation failed.' }
Get-Item -LiteralPath $out | Select-Object FullName, Length, LastWriteTime
Write-Output 'PREVIEW_BUILD_OK (no production deployment, no network writes)'
