param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string[]]$Platforms = @("x86", "x64", "arm64"),

    [switch]$CreateBundle = $true,

    [switch]$CreateInstallPackage,

    [string]$InstallOutputDir = ""
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $projectDir "Zink.csproj"

$selectedPlatforms = $Platforms |
    ForEach-Object { $_ -split "," } |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ } |
    ForEach-Object {
        if ($_ -ieq "ARM64") { "arm64" }
        elseif ($_ -ieq "X64") { "x64" }
        elseif ($_ -ieq "X86") { "x86" }
        else { throw "Unsupported platform '$_'." }
    } |
    Select-Object -Unique

if ($selectedPlatforms.Count -eq 0) {
    throw "At least one platform must be selected."
}

$bundlePlatforms = $selectedPlatforms -join "|"
$packageRoot = Join-Path $env:LOCALAPPDATA "Zink\PackageOutput"
$packageDir = Join-Path $packageRoot ("PackageBuild-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$installDir = if ([string]::IsNullOrWhiteSpace($InstallOutputDir)) {
    Join-Path $packageDir "Install"
} else {
    $InstallOutputDir
}

Write-Host "Building Zink MSIX packages for: $bundlePlatforms"
Write-Host "Configuration: $Configuration"
Write-Host "Package directory: $packageDir"

foreach ($platform in $selectedPlatforms) {
    $runtimeIdentifier = if ($platform -eq "x86") { "win-x86" } elseif ($platform -eq "arm64") { "win-arm64" } else { "win-x64" }
    $platformPackageDir = ((Join-Path $packageDir $platform) + "/")

    Write-Host ""
    Write-Host "Restoring $platform ($runtimeIdentifier)..."
    dotnet restore $projectPath -p:Platform=$platform -p:RuntimeIdentifier=$runtimeIdentifier
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host "Building package for $platform..."
    dotnet build $projectPath `
        -c $Configuration `
        -p:Platform=$platform `
        -p:RuntimeIdentifier=$runtimeIdentifier `
        -p:GenerateAppxPackageOnBuild=true `
        -p:ZinkLocalPackageSigningEnabled=false `
        -p:AppxPackageSigningEnabled=false `
        -p:AppxBundle=Never `
        "-p:AppxPackageDir=$platformPackageDir"

    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($CreateBundle) {
    $sdkBin = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $makeAppx = Get-ChildItem -LiteralPath $sdkBin -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\makeappx.exe" } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (!$makeAppx) { throw "Install the Windows SDK to create the Store bundle." }
    $bundlePackageDir = Join-Path $packageDir "bundle"
    $bundleInputDir = Join-Path $bundlePackageDir "Input"
    New-Item -ItemType Directory -Path $bundleInputDir -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $bundleVersion = $null
    foreach ($platform in $selectedPlatforms) {
        $packages = @(Get-ChildItem -LiteralPath (Join-Path $packageDir $platform) -Recurse -File -Filter "Zink_*_$platform.msix" |
            Where-Object { $_.FullName -notmatch '[\\/]Dependencies[\\/]' })
        if ($packages.Count -ne 1) { throw "Expected one Zink MSIX for $platform; found $($packages.Count)." }
        $archive = [System.IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
        try {
            $reader = [System.IO.StreamReader]::new($archive.GetEntry("AppxManifest.xml").Open())
            try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $version = [string]$manifest.Package.Identity.Version
            if ($bundleVersion -and $bundleVersion -ne $version) { throw "Package versions differ across architectures." }
            $bundleVersion = $version
        } finally { $archive.Dispose() }
        Copy-Item -LiteralPath $packages[0].FullName -Destination $bundleInputDir
    }
    $storeBundle = Join-Path $bundlePackageDir "Zink_${bundleVersion}_neutral.msixbundle"
    & $makeAppx bundle /o /bv $bundleVersion /d $bundleInputDir /p $storeBundle
    if ($LASTEXITCODE -ne 0) { throw "Store bundle creation failed (exit $LASTEXITCODE)." }
    $storeBundles = @(Get-ChildItem -LiteralPath $bundlePackageDir -Recurse -File -Filter *.msixbundle)
    if ($storeBundles.Count -eq 0) { throw "The build did not create a Store MSIX bundle." }
    Write-Host "Upload this bundle to Partner Center:"
    $storeBundles | Select-Object -ExpandProperty FullName | Write-Host
}

if ($CreateInstallPackage) {
    Write-Host ""
    Write-Host "Building signed install package for local sideload/install..."
    Write-Host "Install package directory: $installDir"

    foreach ($platform in $selectedPlatforms) {
        $runtimeIdentifier = if ($platform -eq "x86") { "win-x86" } elseif ($platform -eq "arm64") { "win-arm64" } else { "win-x64" }
        $platformInstallDir = ((Join-Path $installDir $platform) + "/")

        Write-Host ""
        Write-Host "Building signed install package for $platform ($runtimeIdentifier)..."
        dotnet build $projectPath `
            -c $Configuration `
            -p:Platform=$platform `
            -p:RuntimeIdentifier=$runtimeIdentifier `
            -p:GenerateAppxPackageOnBuild=true `
            -p:AppxPackageSigningEnabled=true `
            -p:PackageCertificateKeyFile=Zink_TemporaryKey.pfx `
            -p:EnableSigningChecks=false `
            -p:GenerateTestArtifacts=true `
            -p:AppxBundle=Never `
            "-p:AppxPackageDir=$platformInstallDir"

        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    if ($CreateBundle -and $selectedPlatforms.Count -gt 1) {
        $primaryPlatform = if ($selectedPlatforms -contains "x64") { "x64" } else { $selectedPlatforms[0] }
        $bundleInstallDir = ((Join-Path $installDir "bundle") + "/")

        Write-Host ""
        Write-Host "Building signed combined install bundle for $bundlePlatforms..."
        dotnet build $projectPath `
            -c $Configuration `
            -p:Platform=$primaryPlatform `
            -p:GenerateAppxPackageOnBuild=true `
            -p:AppxPackageSigningEnabled=true `
            -p:PackageCertificateKeyFile=Zink_TemporaryKey.pfx `
            -p:EnableSigningChecks=false `
            -p:GenerateTestArtifacts=true `
            -p:AppxBundle=Always `
            "-p:AppxPackageDir=$bundleInstallDir" `
            "-p:AppxBuildConfigurationSelection=$bundlePlatforms" `
            "-p:AppxBundlePlatforms=$bundlePlatforms"

        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    Copy-Item -LiteralPath (Join-Path $projectDir "Install-ZinkPackage.ps1") -Destination $installDir -Force
    Copy-Item -LiteralPath (Join-Path $projectDir "Zink_TemporaryKey.cer") -Destination $installDir -Force
}

Write-Host "Package output:"
Get-ChildItem -LiteralPath $packageDir -Recurse -File |
    Where-Object { $_.Extension -in ".msixbundle", ".msix", ".msixupload", ".cer", ".ps1" } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 10 FullName, Length, LastWriteTime |
    Format-Table -AutoSize

if ($CreateInstallPackage) {
    Write-Host ""
    Write-Host "Installable package output:"
    Get-ChildItem -LiteralPath $installDir -Recurse -File |
        Where-Object { $_.Extension -in ".msixbundle", ".msix", ".cer", ".ps1" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object FullName, Length, LastWriteTime |
        Format-Table -AutoSize
}

