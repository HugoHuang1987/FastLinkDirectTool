[CmdletBinding()]
param([switch]$Install)

$ErrorActionPreference = 'Stop'

function Get-FastLinkOpenShortcutPlan {
    [CmdletBinding()]
    param(
        [string]$ProgramFilesDirectory,
        [string]$DesktopDirectory,
        [ValidateSet('Missing', 'Shortcut', 'Other')][string]$ExistingEntryKind = 'Missing',
        [string]$ExistingTargetPath
    )
    # Pure planning only: no environment lookup, filesystem, COM or UI calls.
    if ([string]::IsNullOrWhiteSpace($ProgramFilesDirectory) -or
        $ProgramFilesDirectory -notmatch '^[A-Za-z]:[\\/]') {
        throw 'Program Files must be a local absolute directory.'
    }
    if ([string]::IsNullOrWhiteSpace($DesktopDirectory) -or -not [IO.Path]::IsPathRooted($DesktopDirectory)) {
        throw 'The current user desktop directory is unavailable or not absolute.'
    }
    $programFiles = [IO.Path]::GetFullPath($ProgramFilesDirectory).TrimEnd('\', '/')
    $desktop = [IO.Path]::GetFullPath($DesktopDirectory).TrimEnd('\', '/')
    $installDirectory = [IO.Path]::Combine($programFiles, 'FastLinkDirectTool')
    $target = [IO.Path]::GetFullPath([IO.Path]::Combine($installDirectory, 'FastLinkDirectTool.exe'))
    $shortcut = [IO.Path]::GetFullPath([IO.Path]::Combine($desktop, '直连助手.lnk'))
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($shortcut), $desktop, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Shortcut destination escaped the current desktop directory.'
    }
    if ($ExistingEntryKind -eq 'Other') { throw 'The desktop name is occupied by a non-shortcut entry; refusing to overwrite it.' }
    if ($ExistingEntryKind -eq 'Shortcut') {
        if ([string]::IsNullOrWhiteSpace($ExistingTargetPath) -or -not [IO.Path]::IsPathRooted($ExistingTargetPath) -or
            -not [string]::Equals([IO.Path]::GetFullPath($ExistingTargetPath), $target, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The existing shortcut does not target this installed helper; refusing to overwrite it.'
        }
    }
    [pscustomobject]@{
        ShortcutPath = $shortcut
        TargetPath = $target
        WorkingDirectory = $installDirectory
        Arguments = '--show --network-mode'
        Description = 'FastLinkDirectTool: 打开当前工作模式，不启停正在运行的分流'
        IconLocation = $target + ',0'
        Action = $(if ($ExistingEntryKind -eq 'Shortcut') { 'UpdateOwnedShortcut' } else { 'CreateShortcut' })
    }
}

# Dot-sourcing is an explicit definitions-only path for pure planning tests.
if ($MyInvocation.InvocationName -eq '.') { return }

$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$plan = Get-FastLinkOpenShortcutPlan -ProgramFilesDirectory $programFiles -DesktopDirectory $desktop
if (-not (Test-Path -LiteralPath $desktop -PathType Container)) { throw 'The current user desktop directory does not exist.' }
foreach ($installedPath in @($plan.WorkingDirectory, $plan.TargetPath)) {
    if (-not (Test-Path -LiteralPath $installedPath)) { throw ('Installed helper is missing: ' + $installedPath) }
    $entry = Get-Item -LiteralPath $installedPath -Force
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Refusing an installed helper path redirected by a reparse point.' }
}
if (-not (Test-Path -LiteralPath $plan.TargetPath -PathType Leaf)) { throw 'Installed target is not a regular executable file.' }
if ($Install -and [version](Get-Item -LiteralPath $plan.TargetPath).VersionInfo.FileVersion -lt [version]'2.6.8.0') {
    throw 'Install version 2.6.8 or newer before adding the current-window reopen shortcut.'
}

$shell = $null
$link = $null
$temporary = $null
$backup = $null
try {
    $existingHash = $null
    if (Test-Path -LiteralPath $plan.ShortcutPath) {
        $existing = Get-Item -LiteralPath $plan.ShortcutPath -Force
        if ($existing.PSIsContainer -or ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The desktop shortcut name is occupied by a directory or reparse point; refusing to overwrite it.'
        }
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut($plan.ShortcutPath)
        $plan = Get-FastLinkOpenShortcutPlan -ProgramFilesDirectory $programFiles -DesktopDirectory $desktop `
            -ExistingEntryKind Shortcut -ExistingTargetPath $link.TargetPath
        $existingHash = (Get-FileHash -LiteralPath $plan.ShortcutPath -Algorithm SHA256).Hash
        if ([string]::Equals($link.Arguments, $plan.Arguments, [StringComparison]::Ordinal) -and
            [string]::Equals($link.WorkingDirectory, $plan.WorkingDirectory, [StringComparison]::OrdinalIgnoreCase)) {
            $plan.Action = 'AlreadyConfigured'
        }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)
        $link = $null
    }
    if (-not $Install -or $plan.Action -eq 'AlreadyConfigured') {
        [pscustomobject]@{ Status = $(if ($plan.Action -eq 'AlreadyConfigured') { 'ALREADY_CONFIGURED' } else { 'READ_ONLY_PREVIEW' }); Changed = $false; Plan = $plan }
        return
    }

    if ($null -eq $shell) { $shell = New-Object -ComObject WScript.Shell }
    $temporary = [IO.Path]::Combine($desktop, '.FastLinkDirectTool-open-' + [Guid]::NewGuid().ToString('N') + '.lnk')
    $link = $shell.CreateShortcut($temporary)
    $link.TargetPath = $plan.TargetPath
    $link.Arguments = $plan.Arguments
    $link.WorkingDirectory = $plan.WorkingDirectory
    $link.Description = $plan.Description
    $link.IconLocation = $plan.IconLocation
    $link.Save()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)
    $link = $null
    if ($null -eq $existingHash) {
        # Move has no overwrite behavior; another file appearing wins the race.
        [IO.File]::Move($temporary, $plan.ShortcutPath)
    }
    else {
        $current = Get-Item -LiteralPath $plan.ShortcutPath -Force
        if ($current.PSIsContainer -or ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            (Get-FileHash -LiteralPath $plan.ShortcutPath -Algorithm SHA256).Hash -ne $existingHash) {
            throw 'The existing shortcut changed during installation; refusing to replace it.'
        }
        $backup = [IO.Path]::Combine($desktop, '.FastLinkDirectTool-shortcut-backup-' + [Guid]::NewGuid().ToString('N') + '.bak')
        [IO.File]::Replace($temporary, $plan.ShortcutPath, $backup)
    }
    $temporary = $null
    $readback = $shell.CreateShortcut($plan.ShortcutPath)
    try {
        if (-not [string]::Equals($readback.TargetPath, $plan.TargetPath, [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals($readback.Arguments, $plan.Arguments, [StringComparison]::Ordinal)) {
            throw 'Shortcut readback did not match the intended target and arguments.'
        }
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($readback) }
    [pscustomobject]@{ Status = 'INSTALLED_AND_VERIFIED'; Changed = $true; Plan = $plan }
}
finally {
    if ($null -ne $link) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
    if ($temporary -and (Test-Path -LiteralPath $temporary -PathType Leaf)) {
        # Only the exact unique temporary file created above, never a wildcard.
        Remove-Item -LiteralPath $temporary -Force
    }
    if ($null -ne $shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
