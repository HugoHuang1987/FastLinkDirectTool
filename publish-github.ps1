param(
    [string]$RepositoryName = 'FastLinkDirectTool'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$resultFile = Join-Path $root 'publish-result.txt'
$logFile = Join-Path $root 'publish.log'
$toolRoot = Join-Path $root '.tools'
$gh = Join-Path $toolRoot 'gh.exe'

Set-Location $root
Remove-Item -LiteralPath $resultFile -Force -ErrorAction SilentlyContinue
Start-Transcript -LiteralPath $logFile -Force | Out-Null

Write-Host '1/5  Checking the release executable...'
if (-not (Test-Path (Join-Path $root 'FastLinkDirectTool.exe'))) { throw 'FastLinkDirectTool.exe was not found.' }

if (-not (Test-Path $gh)) {
    Write-Host '2/5  Preparing the official portable GitHub CLI...'
    $version = '2.94.0'
    $fileName = "gh_${version}_windows_amd64.zip"
    $downloadRoot = Join-Path $env:TEMP "FastLinkDirectTool-gh-$version"
    $archive = Join-Path $downloadRoot $fileName
    $checksums = Join-Path $downloadRoot 'checksums.txt'
    Remove-Item -LiteralPath $downloadRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $downloadRoot | Out-Null

    $baseUrl = "https://github.com/cli/cli/releases/download/v$version"
    Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/$fileName" -OutFile $archive
    Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/gh_${version}_checksums.txt" -OutFile $checksums

    $checksumLine = Get-Content $checksums | Where-Object { $_ -match [regex]::Escape($fileName) } | Select-Object -First 1
    if (-not $checksumLine) { throw 'GitHub CLI checksum was not found.' }
    $expected = ($checksumLine -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw 'GitHub CLI checksum verification failed.' }
    Unblock-File -LiteralPath $archive
    Expand-Archive -LiteralPath $archive -DestinationPath $downloadRoot -Force
    $downloadedGh = Get-ChildItem -Path $downloadRoot -Filter 'gh.exe' -Recurse | Select-Object -First 1
    if (-not $downloadedGh) { throw 'gh.exe was not found in the official archive.' }
    New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
    Copy-Item -LiteralPath $downloadedGh.FullName -Destination $gh -Force
    Unblock-File -LiteralPath $gh
}
else {
    Write-Host '2/5  GitHub CLI is ready.'
}

Write-Host '3/5  Checking GitHub login...'
$ErrorActionPreference = 'Continue'
& $gh auth status --hostname github.com *> $null
$authStatus = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($authStatus -ne 0) {
    Write-Host ''
    Write-Host 'A browser will open for GitHub login. Complete the confirmation in the browser.' -ForegroundColor Yellow
    $ErrorActionPreference = 'Continue'
    & $gh auth login --hostname github.com --git-protocol https --web --clipboard 2>&1 | Out-Host
    $loginStatus = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($loginStatus -ne 0) { throw 'GitHub login was not completed.' }
}
$ErrorActionPreference = 'Continue'
& $gh auth setup-git 2>&1 | Out-Host
$setupStatus = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($setupStatus -ne 0) { throw 'GitHub could not configure Git authentication.' }

$ErrorActionPreference = 'Continue'
$ownerOutput = & $gh api user --jq '.login' 2>$null
$ownerStatus = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($ownerStatus -ne 0) { throw 'Could not query the authenticated GitHub account.' }
$owner = ($ownerOutput | Select-Object -First 1).Trim()
if (-not $owner) { throw 'Could not read the authenticated GitHub account.' }

Write-Host '4/5  Creating the local Git commit...'
if (-not (Test-Path (Join-Path $root '.git'))) {
    $ErrorActionPreference = 'Continue'
    git init -b main 2>&1 | Out-Host
    $gitStatus = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($gitStatus -ne 0) { throw 'git init failed.' }
}
$ErrorActionPreference = 'Continue'
git add . 2>&1 | Out-Host
$gitStatus = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($gitStatus -ne 0) { throw 'git add failed.' }

$hasHead = $true
$ErrorActionPreference = 'Continue'
git rev-parse --verify HEAD *> $null
$headStatus = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($headStatus -ne 0) { $hasHead = $false }
$changes = git status --porcelain
if (-not $hasHead -or $changes) {
    $ErrorActionPreference = 'Continue'
    git commit -m 'Initial public release of FastLink Direct Tool' 2>&1 | Out-Host
    $commitStatus = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($commitStatus -ne 0) { throw 'git commit failed.' }
}

$fullName = "$owner/$RepositoryName"
$ErrorActionPreference = 'Continue'
& $gh repo view $fullName *> $null
$repoExistsStatus = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($repoExistsStatus -eq 0) {
    throw "GitHub repository $fullName already exists; upload stopped to avoid overwriting it."
}

Write-Host '5/5  Creating the public repository and pushing...'
$ErrorActionPreference = 'Continue'
& $gh repo create $fullName `
    --public `
    --source $root `
    --remote origin `
    --push `
    --description 'Windows split-routing helper that automatically follows FastLink tunnel state.' 2>&1 | Out-Host
$publishStatus = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($publishStatus -ne 0) { throw 'GitHub repository creation or push failed.' }

$url = "https://github.com/$fullName"
$url | Set-Content -LiteralPath $resultFile -Encoding UTF8
Write-Host ''
Write-Host "Published: $url" -ForegroundColor Green
Stop-Transcript | Out-Null
Start-Process $url
