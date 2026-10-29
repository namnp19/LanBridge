param(
  [ValidateSet('Install','Remove')]
  [string]$Action = 'Install',
  [string]$ExePath = ''
)
$ErrorActionPreference = 'Stop'
$rules = @(
    @{ Name='LanBridge-LAN-TCP'; Protocol='TCP'; Port=47321 },
    @{ Name='LanBridge-LAN-UDP'; Protocol='UDP'; Port=47322 }
)
foreach ($rule in $rules) {
    $old = Get-NetFirewallRule -Name $rule.Name -ErrorAction SilentlyContinue
    if ($old) { $old | Remove-NetFirewallRule -ErrorAction Stop }
    if ($Action -eq 'Install') {
        if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
            throw "LanBridge executable not found: $ExePath"
        }
        New-NetFirewallRule -Name $rule.Name -DisplayName ("LanBridge LAN " + $rule.Protocol) `
            -Description 'LanBridge: paired Windows PCs on private local network only' `
            -Enabled True -Direction Inbound -Action Allow -Profile Private `
            -Program $ExePath -Protocol $rule.Protocol -LocalPort $rule.Port `
            -RemoteAddress LocalSubnet -ErrorAction Stop | Out-Null
    }
}
Write-Output "LanBridge firewall action completed: $Action"
