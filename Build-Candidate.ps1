[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'codex_tasks\network-modes-20261124\candidate' }
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$outputPath = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'FastLinkDirectTool.exe'
if ([IO.Path]::GetFullPath($outputPath) -eq [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'FastLinkDirectTool.exe'))) { throw 'Candidate build cannot replace the production EXE.' }
$sources = @(& (Join-Path $PSScriptRoot 'ProductionSources.ps1'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Xml.dll `
    ("/win32manifest:" + (Join-Path $PSScriptRoot 'app.manifest')) ("/out:" + $outputPath) $sources
if ($LASTEXITCODE -ne 0) { throw 'Candidate compilation failed.' }
Get-Item -LiteralPath $outputPath | Select-Object FullName, Length, LastWriteTime
'CANDIDATE_BUILD_OK (not launched, not deployed)'
