param(
    [string]$PackagePath = "",
    [string]$CertificatePath = ""
)

$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Get-ChildItem -LiteralPath $scriptDir -Recurse -File |
        Where-Object { $_.Name -like "Zink_*.msixbundle" -or $_.Name -like "Zink_*.msix" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    $CertificatePath = Join-Path $scriptDir "Zink_TemporaryKey.cer"
}

if (-not (Test-Path -LiteralPath $PackagePath)) {
    throw "Could not find the Zink package. Put this script next to Zink_*.msixbundle or pass -PackagePath."
}

if (-not (Test-Path -LiteralPath $CertificatePath)) {
    throw "Could not find Zink_TemporaryKey.cer next to this script."
}

$storeRoot = if (Test-IsAdministrator) { "Cert:\LocalMachine" } else { "Cert:\CurrentUser" }
$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CertificatePath)

Write-Host "Trusting Zink package certificate: $($cert.Subject)"
Import-Certificate -FilePath $CertificatePath -CertStoreLocation "$storeRoot\TrustedPeople" | Out-Null
Import-Certificate -FilePath $CertificatePath -CertStoreLocation "$storeRoot\Root" | Out-Null

Write-Host "Installing Zink package: $PackagePath"
$packageDir = Split-Path -Parent $PackagePath
$dependenciesDir = Join-Path $packageDir "Dependencies"
$dependencyPaths = @()

if (Test-Path -LiteralPath $dependenciesDir) {
    $dependencyPaths = Get-ChildItem -LiteralPath $dependenciesDir -Recurse -File |
        Where-Object { $_.Extension -in ".appx", ".msix" } |
        Select-Object -ExpandProperty FullName
}

if ($dependencyPaths.Count -gt 0) {
    Add-AppxPackage -Path $PackagePath -DependencyPath $dependencyPaths -ForceApplicationShutdown
} else {
    Add-AppxPackage -Path $PackagePath -ForceApplicationShutdown
}

Write-Host "Zink installed successfully."
