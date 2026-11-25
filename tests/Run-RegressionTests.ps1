[CmdletBinding()]
param()

# Non-invasive tests: compile the production source with a separate test Main.
# All NRPT cmdlets are replaced with local mocks before any generated script runs.
# No application startup, actual DNS/route changes, or credentials are used.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$productionSources = @(& (Join-Path $projectRoot 'ProductionSources.ps1'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testDirectory = Join-Path $tempRoot ('FastLinkDirectTool-regression-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testDirectory
$executable = Join-Path $testDirectory 'NrptRegressionTests.exe'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function New-MockRules {
    @(
        [pscustomobject]@{ Name = 'owned-1'; Comment = 'FastLinkDirectTool'; NameServers = '127.0.0.1'; Namespace = 'one.example.test' }
        [pscustomobject]@{ Name = 'owned-2'; Comment = 'FastLinkDirectTool'; NameServers = '127.0.0.1'; Namespace = 'two.example.test' }
        [pscustomobject]@{ Name = 'other-local'; Comment = 'AnotherTool'; NameServers = '127.0.0.1'; Namespace = 'other.example.test' }
        [pscustomobject]@{ Name = 'other-corporate'; Comment = 'CorporatePolicy'; NameServers = '192.0.2.53'; Namespace = 'corp.example.test' }
    )
}

function Invoke-MockedRemoval([string]$ScriptText, [string]$Scenario, [int]$ExpectedCount = 0) {
    $script:MockRules = @(New-MockRules)
    if ($Scenario -eq 'already-clean') { $script:MockRules = @($script:MockRules | Where-Object Comment -ne 'FastLinkDirectTool') }
    $script:MockGetCount = 0
    $script:MockRemoved = New-Object 'System.Collections.Generic.List[string]'
    $script:MockAdded = New-Object 'System.Collections.Generic.List[string]'
    $script:MockOutput = New-Object 'System.Collections.Generic.List[string]'
    $script:MockScenario = $Scenario
    $script:MockExpectedCount = $ExpectedCount

    function Get-DnsClientNrptRule {
        [CmdletBinding()]
        param()
        Assert-True ($PSBoundParameters['ErrorAction'] -eq 'Stop') 'NRPT reads must use -ErrorAction Stop.'
        $script:MockGetCount++
        if ($script:MockScenario -eq 'readback-error' -and $script:MockGetCount -ge 2) {
            throw 'simulated NRPT readback failure'
        }
        return $script:MockRules
    }

    function Remove-DnsClientNrptRule {
        [CmdletBinding()]
        param([string[]]$Name, [switch]$Force)
        Assert-True ($PSBoundParameters['ErrorAction'] -eq 'Stop') 'NRPT deletes must use -ErrorAction Stop.'
        Assert-True $Force.IsPresent 'Mock deletion expected the noninteractive Force switch.'
        foreach ($ruleName in $Name) {
            Assert-True ($ruleName -in @('owned-1', 'owned-2')) 'Generated cleanup targeted an unrelated rule.'
            if ($script:MockScenario -eq 'delete-error') { throw 'simulated NRPT delete failure' }
            $script:MockRemoved.Add($ruleName)
            if ($script:MockScenario -ne 'residual-rule') {
                $script:MockRules = @($script:MockRules | Where-Object Name -ne $ruleName)
            }
        }
    }

    function Add-DnsClientNrptRule {
        [CmdletBinding()]
        param([string[]]$Namespace, [string]$NameServers, [string]$DisplayName, [string]$Comment)
        Assert-True ($ErrorActionPreference -eq 'Stop') 'Generated apply script must stop on errors.'
        Assert-True ($Comment -eq 'FastLinkDirectTool' -and $NameServers -eq '127.0.0.1') 'Unexpected ownership or DNS target in apply script.'
        foreach ($ns in $Namespace) {
            Assert-True ($ns -match '^regression-[0-9]+\.invalid$') 'Mock apply received an unexpected namespace.'
            $script:MockAdded.Add($ns)
            if ($script:MockScenario -eq 'apply-drop-last' -and $script:MockAdded.Count -eq $script:MockExpectedCount) { continue }
            $storedDns = $NameServers
            if ($script:MockScenario -eq 'apply-wrong-dns' -and $script:MockAdded.Count -eq $script:MockExpectedCount) { $storedDns = '192.0.2.53' }
            $created = [pscustomobject]@{ Name = ('added-' + $script:MockAdded.Count); Comment = $Comment; NameServers = $storedDns; Namespace = @($ns) }
            $script:MockRules += $created
            $created
        }
    }

    # Fail before execution if production script ever gains another command.
    # This prevents a later code change from escaping the mocked command set.
    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($ScriptText, [ref]$tokens, [ref]$parseErrors)
    Assert-True (@($parseErrors).Count -eq 0) 'Generated removal script has PowerShell parse errors.'
    $commands = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true))
    foreach ($command in $commands) {
        Assert-True ($command.GetCommandName() -in @('Get-DnsClientNrptRule', 'Remove-DnsClientNrptRule', 'Add-DnsClientNrptRule', 'Where-Object', 'Write-Output', 'Out-Null')) 'Generated script contains an unmocked command.'
    }
    Assert-True ((Get-Command Get-DnsClientNrptRule).CommandType -eq 'Function') 'NRPT read mock is not active.'
    Assert-True ((Get-Command Remove-DnsClientNrptRule).CommandType -eq 'Function') 'NRPT delete mock is not active.'
    Assert-True ((Get-Command Add-DnsClientNrptRule).CommandType -eq 'Function') 'NRPT add mock is not active.'

    $failure = $null
    try {
        & ([scriptblock]::Create($ScriptText)) | ForEach-Object { $script:MockOutput.Add([string]$_) }
    }
    catch { $failure = $_ }

    [pscustomobject]@{
        Failed = ($null -ne $failure)
        Output = @($script:MockOutput.ToArray())
        Removed = @($script:MockRemoved.ToArray())
        Added = @($script:MockAdded.ToArray())
        RemainingNames = @($script:MockRules.Name)
        ReadCount = $script:MockGetCount
    }
}

try {
    Assert-True (Test-Path -LiteralPath $compiler) 'Windows .NET Framework C# compiler not found.'
    $arguments = @(
        '/nologo', '/target:exe', '/optimize+', '/platform:anycpu', '/codepage:65001',
        '/main:FastLinkDirectTool.Tests.NrptRegressionTests',
        '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
        '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Xml.dll',
        ('/out:' + $executable)
        $productionSources
        (Join-Path $PSScriptRoot 'NrptRegressionTests.cs'),
        (Join-Path $PSScriptRoot 'CompatibilityDetectionTests.cs'),
        (Join-Path $PSScriptRoot 'SystemProxyBypassTests.cs')
    )
    & $compiler @arguments
    Assert-True ($LASTEXITCODE -eq 0) 'Regression test compilation failed.'
    $testOutput = @(& $executable)
    $testExitCode = $LASTEXITCODE
    $testOutput | Write-Output
    Assert-True ($testExitCode -eq 0) 'C# regression tests failed.'
    $scriptLines = @(& $executable '--print-remove-script')
    Assert-True ($LASTEXITCODE -eq 0) 'Could not obtain the production removal script.'
    $scriptText = $scriptLines -join [Environment]::NewLine
    $marker = '__FASTLINK_NRPT_REMOVED__'

    $result = Invoke-MockedRemoval $scriptText 'success'
    Assert-True (-not $result.Failed) 'Owned-rule cleanup failed with healthy mocks.'
    Assert-True (($result.Removed -join ',') -eq 'owned-1,owned-2') 'Cleanup did not remove exactly the owned rules.'
    Assert-True (($result.RemainingNames -join ',') -eq 'other-local,other-corporate') 'Cleanup altered unrelated NRPT rules.'
    Assert-True ($result.ReadCount -ge 2) 'Cleanup did not read back NRPT after removal.'
    Assert-True ($marker -in $result.Output) 'Verified cleanup omitted the success marker.'
    Write-Output 'PASS generated script deletes only owned rules with strict errors and readback'

    $result = Invoke-MockedRemoval $scriptText 'already-clean'
    Assert-True (-not $result.Failed -and $result.Removed.Count -eq 0 -and $marker -in $result.Output) 'Already-clean state was not handled idempotently.'
    Write-Output 'PASS generated script accepts an already-clean state'

    foreach ($scenario in @('delete-error', 'residual-rule', 'readback-error')) {
        $result = Invoke-MockedRemoval $scriptText $scenario
        Assert-True $result.Failed ('Cleanup incorrectly succeeded for ' + $scenario)
        Assert-True ($marker -notin $result.Output) ('Cleanup emitted a success marker for ' + $scenario)
        Assert-True ('other-local' -in $result.RemainingNames -and 'other-corporate' -in $result.RemainingNames) 'Failure path altered unrelated rules.'
        Write-Output ('PASS generated script rejects ' + $scenario + ' without a success marker')
    }
    $applyMarker = '__FASTLINK_NRPT_APPLIED__'
    foreach ($count in @(100, 200)) {
        $applyLines = @(& $executable '--print-apply-script' ([string]$count))
        Assert-True ($LASTEXITCODE -eq 0) 'Could not obtain the production apply script.'
        $applyText = $applyLines -join [Environment]::NewLine
        $result = Invoke-MockedRemoval $applyText 'apply-success' $count
        Assert-True (-not $result.Failed) ('Large-rule apply failed for ' + $count + ' namespaces.')
        Assert-True ($result.Added.Count -eq $count -and $result.RemainingNames.Count -eq ($count + 2)) 'Large-rule apply count mismatch.'
        Assert-True ($applyMarker -in $result.Output -and $result.ReadCount -ge 3) 'Large-rule apply was not verified by readback.'
        Assert-True ('other-local' -in $result.RemainingNames -and 'other-corporate' -in $result.RemainingNames) 'Large-rule apply changed unrelated rules.'
        Write-Output ('PASS generated apply script verifies ' + $count + ' mocked namespaces')
    }
    foreach ($scenario in @('apply-drop-last', 'apply-wrong-dns')) {
        $result = Invoke-MockedRemoval $applyText $scenario 200
        Assert-True ($result.Failed -and $applyMarker -notin $result.Output) ('Large-rule readback incorrectly accepted ' + $scenario)
        Write-Output ('PASS large-rule apply rejects ' + $scenario + ' without a success marker')
    }
    $countLine = @($testOutput | Where-Object { $_ -match '^C_SHARP_TESTS_OK [0-9]+$' })[-1]
    Assert-True ([bool]$countLine) 'Missing C# test-count evidence.'
    $totalTests = [int]($countLine -replace '^C_SHARP_TESTS_OK ', '') + 9
    Write-Output ("ALL_REGRESSION_TESTS_OK {0} (no live DNS or route operations)" -f $totalTests)
}
finally {
    # Remove only the unique temporary directory created by this test run.
    if (Test-Path -LiteralPath $testDirectory) {
        $resolved = (Resolve-Path -LiteralPath $testDirectory).Path
        $expectedPrefix = $tempRoot.TrimEnd('\') + '\FastLinkDirectTool-regression-'
        if (-not $resolved.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to clean an unexpected temporary path.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
