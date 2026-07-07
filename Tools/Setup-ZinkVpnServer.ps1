# Run on a Windows Server machine from an elevated PowerShell session.
# This configures RRAS as a basic Zink VPN server using L2TP/IPsec with a pre-shared key.

[CmdletBinding()]
param(
    [string]$VpnUser = "zinkvpn",
    [securestring]$VpnUserPassword,
    [string]$PresharedKey,
    [string]$ClientPoolStart = "10.88.0.10",
    [string]$ClientPoolEnd = "10.88.0.250",
    [switch]$ServerBehindNat,
    [switch]$SkipLocalUserCreation
)

$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function New-StrongSecret {
    param([int]$Length = 40)

    $bytes = New-Object byte[] $Length
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $raw = [Convert]::ToBase64String($bytes)
    return ($raw -replace "[+/=]", "").Substring(0, [Math]::Min($Length, ($raw -replace "[+/=]", "").Length))
}

function ConvertFrom-SecureStringToPlainText {
    param([securestring]$SecureValue)

    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
}

if (-not (Test-IsAdministrator)) {
    throw "Run this script from an elevated PowerShell session on the Windows Server."
}

$os = Get-CimInstance Win32_OperatingSystem
if ($os.ProductType -eq 1) {
    throw "This script must be run on Windows Server, not a Windows client edition."
}

if ([string]::IsNullOrWhiteSpace($PresharedKey)) {
    $PresharedKey = New-StrongSecret
}

Write-Host "Installing Remote Access / RRAS roles..."
Install-WindowsFeature DirectAccess-VPN -IncludeManagementTools | Out-Host
Install-WindowsFeature Routing -IncludeManagementTools | Out-Host

Write-Host "Configuring RRAS VPN with client pool $ClientPoolStart - $ClientPoolEnd..."
try {
    Install-RemoteAccess -VpnType Vpn -IPAddressRange @($ClientPoolStart, $ClientPoolEnd) -Confirm:$false | Out-Host
}
catch {
    Write-Host "Install-RemoteAccess did not complete cleanly, continuing with netsh configuration: $($_.Exception.Message)"
}

& netsh ras set conf confstate=enabled | Out-Host
& netsh ras ip set addrassign method=pool | Out-Host
& netsh ras ip delete pool | Out-Null
& netsh ras ip add range from=$ClientPoolStart to=$ClientPoolEnd | Out-Host
& netsh ras ip set access mode=all | Out-Host
& netsh ras ip set negotiation mode=allow | Out-Host
& netsh ras aaaa set authentication provider=windows | Out-Host
& netsh ras aaaa set accounting provider=windows | Out-Host
& netsh ras aaaa set ipsecpolicy psk=enabled secret="$PresharedKey" | Out-Host
& netsh ras add authtype type=MSCHAPv2 | Out-Null

if ($ServerBehindNat) {
    Write-Host "Setting NAT-T registry support for L2TP/IPsec..."
    New-Item -Path "HKLM:\SYSTEM\CurrentControlSet\Services\PolicyAgent" -Force | Out-Null
    New-ItemProperty `
        -Path "HKLM:\SYSTEM\CurrentControlSet\Services\PolicyAgent" `
        -Name "AssumeUDPEncapsulationContextOnSendRule" `
        -PropertyType DWord `
        -Value 2 `
        -Force | Out-Null
}

if (-not $SkipLocalUserCreation) {
    if (-not $VpnUserPassword) {
        $VpnUserPassword = Read-Host "Enter a strong password for VPN user '$VpnUser'" -AsSecureString
    }

    $existingUser = Get-LocalUser -Name $VpnUser -ErrorAction SilentlyContinue
    if (-not $existingUser) {
        Write-Host "Creating local VPN user '$VpnUser'..."
        New-LocalUser -Name $VpnUser -Password $VpnUserPassword -PasswordNeverExpires -UserMayNotChangePassword | Out-Host
    }
    else {
        Write-Host "Local user '$VpnUser' already exists."
    }

    & netsh ras set user name="$VpnUser" dialin=permit | Out-Host
}

Write-Host "Opening Windows Firewall ports for L2TP/IPsec..."
$rules = @(
    @{ Name = "Zink VPN - IKE UDP 500"; Protocol = "UDP"; Port = 500 },
    @{ Name = "Zink VPN - IPsec NAT-T UDP 4500"; Protocol = "UDP"; Port = 4500 },
    @{ Name = "Zink VPN - L2TP UDP 1701"; Protocol = "UDP"; Port = 1701 }
)

foreach ($rule in $rules) {
    if (-not (Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule `
            -DisplayName $rule.Name `
            -Direction Inbound `
            -Action Allow `
            -Protocol $rule.Protocol `
            -LocalPort $rule.Port | Out-Host
    }
}

if (-not (Get-NetFirewallRule -DisplayName "Zink VPN - ESP Protocol 50" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule `
        -DisplayName "Zink VPN - ESP Protocol 50" `
        -Direction Inbound `
        -Action Allow `
        -Protocol 50 | Out-Host
}

Set-Service RemoteAccess -StartupType Automatic
Restart-Service RemoteAccess -Force

Write-Host ""
Write-Host "Zink VPN server setup is complete."
Write-Host "Pre-shared key: $PresharedKey"
Write-Host "VPN username: $VpnUser"
Write-Host ""
Write-Host "Forward UDP 500, UDP 4500, UDP 1701, and ESP/protocol 50 to this server if it is behind a router."
Write-Host "Use Tools\Install-ZinkVpnClientProfile.ps1 on your Windows PC to create the 'Zink VPN' client profile."
if ($ServerBehindNat) {
    Write-Host "Restart the server so the NAT-T registry setting fully applies."
}
