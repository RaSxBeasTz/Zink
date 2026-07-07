# Run on the Windows PC where Zink is installed.
# This creates a "Zink VPN" Windows profile pointed at hide.me's free SSTP endpoint.
# SSTP uses TCP 443, so it often works when IKEv2 is blocked by a router, firewall, or ISP.

[CmdletBinding()]
param(
    [string]$ProfileName = "Zink VPN",
    [string]$ServerAddress = "free-nl.hide.me",
    [switch]$AllUserConnection
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

Write-Host "Creating free SSTP VPN profile '$ProfileName' using $ServerAddress..."
$parameters = @{
    Name = $ProfileName
    ServerAddress = $ServerAddress
    TunnelType = "Sstp"
    AuthenticationMethod = "MSChapv2"
    EncryptionLevel = "Required"
    RememberCredential = $true
    Force = $true
    PassThru = $true
}

if ($AllUserConnection) {
    $parameters.AllUserConnection = $true
}

Add-VpnConnection @parameters | Out-Host

Write-Host ""
Write-Host "Profile '$ProfileName' is ready using SSTP."
Write-Host "First connection may ask for your hide.me free account username/password in Windows."
Write-Host "After credentials are saved, Zink > Zink VPN > Connect can connect this profile."
