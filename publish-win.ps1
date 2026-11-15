# Publish HandyPlayer for Windows x64 (self-contained, portable, no .NET required)
#
#   pwsh ./publish-win.ps1              # zip + MSI if WiX is installed
#   pwsh ./publish-win.ps1 -RequireMsi  # fail when the MSI cannot be built
param(
    [string]$Configuration = "Release",
    # Release builds: treat a missing WiX toolset as an error instead of skipping the MSI.
    [switch]$RequireMsi,
    # Reuse an existing self-contained win-x64 restore in this checkout.
    [switch]$NoRestore,
    [ValidateRange(1, 64)]
    [int]$MaxParallelism = 2
)

$ErrorActionPreference = "Stop"
# All paths below are repo-relative; work from any current directory.
Set-Location $PSScriptRoot
. "$PSScriptRoot/harness-env.ps1"

# Evaluated version from MSBuild (robust against property-group order; no silent fallback).
$version = (dotnet msbuild src/HandyPlaylistPlayer.App/HandyPlaylistPlayer.App.csproj -getProperty:Version -nologo).Trim()
if ($LASTEXITCODE -ne 0 -or -not $version) {
    Write-Host "Could not read <Version> from HandyPlaylistPlayer.App.csproj" -ForegroundColor Red
    exit 1
}

$appName = "HandyPlayer"
$releaseRoot = New-HandyReleaseRoot 'win-x64'
try {
$portableDir = "$releaseRoot/$appName-$version-win-x64"
$buildDir = "$releaseRoot/win-x64-build"
$zipName = "$appName-$version-win-x64.zip"
$zipPath = "$releaseRoot/$zipName"
$msiName = "$appName-$version-win-x64.msi"
$msiPath = "$releaseRoot/$msiName"

Write-Host "Building $appName v$version for Windows x64..." -ForegroundColor Cyan

# All outputs belong to this fresh run; previous releases are retained.

# Publish self-contained. The Media.Mpv project fails this step if libmpv-2.dll is missing.
$restoreArgs = if ($NoRestore) { @('--no-restore') } else { @() }
dotnet publish src/HandyPlaylistPlayer.App `
    -c $Configuration `
    -r win-x64 `
    --self-contained `
    "-maxcpucount:$MaxParallelism" `
    -p:RestoreDisableParallel=true `
    -o $buildDir @restoreArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Create portable directory
Copy-Item -Recurse $buildDir $portableDir
Remove-Item -Recurse -Force $buildDir

Write-Host "Creating archive: $zipPath" -ForegroundColor Cyan

# Use .NET ZipFile directly (more reliable than Compress-Archive with locked files)
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Resolve-Path $portableDir).Path,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $true  # include base directory name in zip
)

$zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "  Archive: $zipPath ($zipSize MB)" -ForegroundColor White

# --- MSI installer (requires WiX: dotnet tool install --global wix --version 6.0.2) ---
$wixCmd = Get-Command wix -ErrorAction SilentlyContinue
if ($wixCmd) {
    Write-Host ""
    Write-Host "Building MSI installer: $msiPath" -ForegroundColor Cyan

    # Ensure the UI extension is available. WiX prints "already installed" notes on stderr; under Windows
    # PowerShell 5.1 that would be a terminating error with $ErrorActionPreference = Stop, so relax it here.
    & {
        $ErrorActionPreference = "Continue"
        wix extension add WixToolset.UI.wixext/6.0.2 2>&1 | Out-Null
    }

    wix build installer-win.wxs `
        -arch x64 `
        -ext WixToolset.UI.wixext `
        -d Version=$version `
        -d SourceDir=$portableDir `
        -o $msiPath

    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $msiPath)) {
        Write-Host "MSI build failed (wix build returned $LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
    $msiSize = [math]::Round((Get-Item $msiPath).Length / 1MB, 1)
    Write-Host "  Installer: $msiPath ($msiSize MB)" -ForegroundColor White
} elseif ($RequireMsi) {
    Write-Host "WiX not found and -RequireMsi was given. Install with:" -ForegroundColor Red
    Write-Host "  dotnet tool install --global wix --version 6.0.2" -ForegroundColor Red
    exit 1
} else {
    Write-Host ""
    Write-Host "Skipping MSI (WiX not found). Install with:" -ForegroundColor Yellow
    Write-Host "  dotnet tool install --global wix --version 6.0.2" -ForegroundColor Yellow
}

# --- setup.exe installer (per-user, optional; requires Inno Setup 6: winget install JRSoftware.InnoSetup) ---
$setupPath = "$releaseRoot/$appName-$version-win-x64-setup.exe"
$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
              "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if ($iscc) {
    Write-Host ""
    Write-Host "Building setup.exe installer: $setupPath" -ForegroundColor Cyan
    & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$portableDir" "/O$releaseRoot" installer-win.iss
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $setupPath)) {
        Write-Host "Inno Setup build failed (iscc returned $LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host ""
    Write-Host "Skipping setup.exe (Inno Setup not found)." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Build complete!" -ForegroundColor Green
Write-Host "  Portable: $portableDir/" -ForegroundColor White
Write-Host "  Archive:  $zipPath ($zipSize MB)" -ForegroundColor White
if (Test-Path $msiPath) {
    Write-Host "  Installer: $msiPath ($msiSize MB)" -ForegroundColor White
}

# SHA-256 checksums for everything shipped, next to the artifacts.
$artifacts = @(Get-Item $zipPath) + @(Get-Item $msiPath, $setupPath -ErrorAction SilentlyContinue)
$artifacts | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
    Set-Content "$releaseRoot/SHA256SUMS-win-x64.txt" -Encoding ascii
Write-Host "  Checksums: $releaseRoot/SHA256SUMS-win-x64.txt" -ForegroundColor White

Write-Host ""
Write-Host "Portable:  Extract the zip and run $appName.exe" -ForegroundColor Yellow
if (Test-Path $msiPath) {
    Write-Host "Installer: Run the .msi to install to Program Files" -ForegroundColor Yellow
}
} finally {
    Write-Host "Cleanup checkpoint: prior releases/caches retained; failed-run evidence retained on errors. Output: $releaseRoot"
}
