param([string]$Root = $PSScriptRoot)
# Shared compiler inputs only; no startup or configuration work.
@('FastLinkDirectTool.cs', 'FastLinkConnectionCompatibility.cs', 'SystemProxyBypass.cs',
  'NetworkModePlanning.cs', 'DualNetworkRuntime.cs', 'DualNetworkWindows.cs', 'DualJournalReconciliation.cs', 'DualPowerShellWorker.cs', 'DualInternetProbe.cs',
  'DualNetworkDns.cs', 'DualModeProfile.cs', 'DualModeForm.cs') | ForEach-Object { Join-Path $Root $_ }
