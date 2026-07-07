# Run on the Windows PC where Zink is installed.
# This creates the Windows VPN profile that the Zink VPN page connects to.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServerAddress,

    [Parameter(Mandatory = $true)]
    [string]$PresharedKey,

    [string]$ProfileName = "Zink VPN",
    [switch]$AllUserConnection,
    [switch]$ServerBehindNat
)

$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if ($AllUserConnection -and -not (Test-IsAdministrator)) {
    throw "Run PowerShell as Administrator when using -AllUserConnection."
}

$existing = Get-VpnConnection -Name $ProfileName -AllUserConnection:$AllUserConnection -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Removing existing VPN profile '$ProfileName'..."
    Remove-VpnConnection -Name $ProfileName -AllUserConnection:$AllUserConnection -Force
}

Write-Host "Creating VPN profile '$ProfileName' for $ServerAddress..."
$parameters = @{
    Name = $ProfileName
    ServerAddress = $ServerAddress
    TunnelType = "L2tp"
    L2tpPsk = $PresharedKey
    AuthenticationMethod = "MSChapv2"
    EncryptionLevel = "Required"
    SplitTunneling = $false
    RememberCredential = $true
    Force = $true
    PassThru = $true
}

if ($AllUserConnection) {
    $parameters.AllUserConnection = $true
}

Add-VpnConnection @parameters | Out-Host

if ($ServerBehindNat) {
    if (-not (Test-IsAdministrator)) {
        throw "Run PowerShell as Administrator to set -ServerBehindNat."
    }

    Write-Host "Setting NAT-T registry support for L2TP/IPsec..."
    New-Item -Path "HKLM:\SYSTEM\CurrentControlSet\Services\PolicyAgent" -Force | Out-Null
    New-ItemProperty `
        -Path "HKLM:\SYSTEM\CurrentControlSet\Services\PolicyAgent" `
        -Name "AssumeUDPEncapsulationContextOnSendRule" `
        -PropertyType DWord `
        -Value 2 `
        -Force | Out-Null

    Write-Host "Restart Windows before connecting if the VPN server is behind NAT."
}

Write-Host ""
Write-Host "Client profile '$ProfileName' is ready. Open Zink > Zink VPN > Connect."
