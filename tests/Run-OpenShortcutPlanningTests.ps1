[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'Install-OpenShortcut.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw ($errors | Out-String) }
$function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-FastLinkOpenShortcutPlan' }, $true)
if ($null -eq $function) { throw 'Pure shortcut planner was not found.' }
$commands = @($function.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true))
if ($commands.Count -ne 0) { throw 'Pure shortcut planner must not execute commands.' }
# Dot-source exits after function definitions: no COM, desktop inspection or write.
. $scriptPath
$passed = 0
function Assert-Test([string]$Name, [scriptblock]$Body) {
    & $Body
    $script:passed++
    Write-Output ('PASS ' + $Name)
}
function Assert-True([bool]$Condition) { if (-not $Condition) { throw 'Assertion failed.' } }
function Assert-Rejected([scriptblock]$Body) {
    $rejected = $false
    try { $null = & $Body } catch { $rejected = $true }
    Assert-True $rejected
}
$argsBase = @{ ProgramFilesDirectory = 'C:\Program Files'; DesktopDirectory = 'C:\Users\test-user\Desktop' }
Assert-Test 'fixed installed target' {
    $plan = Get-FastLinkOpenShortcutPlan @argsBase
    Assert-True ($plan.TargetPath -eq 'C:\Program Files\FastLinkDirectTool\FastLinkDirectTool.exe')
}
Assert-Test 'fixed desktop shortcut name' {
    $plan = Get-FastLinkOpenShortcutPlan @argsBase
    Assert-True ($plan.ShortcutPath -eq 'C:\Users\test-user\Desktop\直连助手.lnk')
}
Assert-Test 'show-only mode arguments' {
    Assert-True ((Get-FastLinkOpenShortcutPlan @argsBase).Arguments -eq '--show --network-mode')
}
Assert-Test 'missing shortcut is create plan' {
    Assert-True ((Get-FastLinkOpenShortcutPlan @argsBase).Action -eq 'CreateShortcut')
}
Assert-Test 'owned shortcut may update' {
    Assert-True ((Get-FastLinkOpenShortcutPlan @argsBase -ExistingEntryKind Shortcut -ExistingTargetPath 'C:\Program Files\FastLinkDirectTool\FastLinkDirectTool.exe').Action -eq 'UpdateOwnedShortcut')
}
Assert-Test 'target comparison is case insensitive' {
    Assert-True ((Get-FastLinkOpenShortcutPlan @argsBase -ExistingEntryKind Shortcut -ExistingTargetPath 'c:\program files\fastlinkdirecttool\FASTLINKDIRECTTOOL.EXE').Action -eq 'UpdateOwnedShortcut')
}
Assert-Test 'foreign target rejected' {
    Assert-Rejected { Get-FastLinkOpenShortcutPlan @argsBase -ExistingEntryKind Shortcut -ExistingTargetPath 'C:\Windows\notepad.exe' }
}
Assert-Test 'same executable name outside install rejected' {
    Assert-Rejected { Get-FastLinkOpenShortcutPlan @argsBase -ExistingEntryKind Shortcut -ExistingTargetPath 'C:\Temp\FastLinkDirectTool.exe' }
}
Assert-Test 'non-shortcut collision rejected' {
    Assert-Rejected { Get-FastLinkOpenShortcutPlan @argsBase -ExistingEntryKind Other }
}
Assert-Test 'empty existing target rejected' {
    Assert-Rejected { Get-FastLinkOpenShortcutPlan @argsBase -ExistingEntryKind Shortcut }
}
Assert-Test 'relative Program Files rejected' {
    Assert-Rejected { Get-FastLinkOpenShortcutPlan -ProgramFilesDirectory 'Program Files' -DesktopDirectory 'C:\Users\test-user\Desktop' }
}
Assert-Test 'empty Program Files rejected' {
    Assert-Rejected { Get-FastLinkOpenShortcutPlan -ProgramFilesDirectory '' -DesktopDirectory 'C:\Users\test-user\Desktop' }
}
Assert-Test 'remote Program Files rejected' {
    Assert-Rejected { Get-FastLinkOpenShortcutPlan -ProgramFilesDirectory '\\server\share' -DesktopDirectory 'C:\Users\test-user\Desktop' }
}
Assert-Test 'relative desktop rejected' {
    Assert-Rejected { Get-FastLinkOpenShortcutPlan -ProgramFilesDirectory 'C:\Program Files' -DesktopDirectory 'Desktop' }
}
Assert-Test 'empty desktop rejected' {
    Assert-Rejected { Get-FastLinkOpenShortcutPlan -ProgramFilesDirectory 'C:\Program Files' -DesktopDirectory '' }
}
Assert-Test 'redirected current-user desktop accepted' {
    Assert-True ((Get-FastLinkOpenShortcutPlan -ProgramFilesDirectory 'D:\Program Files' -DesktopDirectory 'C:\Users\test-user\OneDrive\Desktop').ShortcutPath -eq 'C:\Users\test-user\OneDrive\Desktop\直连助手.lnk')
}
Write-Output ('OPEN_SHORTCUT_PLANNING_TESTS_OK ' + $passed)
Write-Output 'PURE_PLANNING_ONLY_NO_COM_NO_DESKTOP_WRITE'
