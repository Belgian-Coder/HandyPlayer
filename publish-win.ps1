# Publish HandyPlayer for Windows x64 (self-contained, portable, no .NET required)
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

# Read version from csproj
[xml]$csproj = Get-Content "src/HandyPlaylistPlayer.App/HandyPlaylistPlayer.App.csproj"
$version = $csproj.Project.PropertyGroup[0].Version
if (-not $version) { $version = "1.0.0" }

$appName = "HandyPlayer"
$portableDir = "publish/$appName-$version-win-x64"
$buildDir = "publish/win-x64-build"

Write-Host "Building $appName v$version for Windows x64..." -ForegroundColor Cyan

# Clean previous output
if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }
if (Test-Path $portableDir) { Remove-Item -Recurse -Force $portableDir }

# Publish self-contained
dotnet publish src/HandyPlaylistPlayer.App `
    -c $Configuration `
    -r win-x64 `
    --self-contained `
    -o $buildDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Create portable directory
Copy-Item -Recurse $buildDir $portableDir
Remove-Item -Recurse -Force $buildDir

# Create zip archive
$zipName = "$appName-$version-win-x64.zip"
$zipPath = "publish/$zipName"

if (Test-Path $zipPath) { Remove-Item $zipPath }

Write-Host "Creating archive: $zipPath" -ForegroundColor Cyan

# Use .NET ZipFile directly (more reliable than Compress-Archive with locked files)
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Resolve-Path $portableDir).Path,
    (Join-Path (Resolve-Path "publish").Path $zipName),
    [System.IO.Compression.CompressionLevel]::Optimal,
    $true  # include base directory name in zip
)

$zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "  Archive: $zipPath ($zipSize MB)" -ForegroundColor White

# --- MSI installer (requires WiX: dotnet tool install --global wix) ---
$msiName = "$appName-$version-win-x64.msi"
$msiPath = "publish/$msiName"

$wixCmd = Get-Command wix -ErrorAction SilentlyContinue
if ($wixCmd) {
    Write-Host ""
    Write-Host "Building MSI installer: $msiPath" -ForegroundColor Cyan

    # Ensure UI extension is available (suppress version warnings)
    wix extension add WixToolset.UI.wixext/6.0.2 2>$null

    wix build installer-win.wxs `
        -arch x64 `
        -ext WixToolset.UI.wixext `
        -d Version=$version `
        -d SourceDir=$portableDir `
        -o $msiPath

    if ($LASTEXITCODE -eq 0) {
        $msiSize = [math]::Round((Get-Item $msiPath).Length / 1MB, 1)
        Write-Host "  Installer: $msiPath ($msiSize MB)" -ForegroundColor White
    } else {
        Write-Host "  MSI build failed (wix build returned $LASTEXITCODE)" -ForegroundColor Yellow
    }
} else {
    Write-Host ""
    Write-Host "Skipping MSI (WiX not found). Install with:" -ForegroundColor Yellow
    Write-Host "  dotnet tool install --global wix" -ForegroundColor Yellow
    Write-Host "  wix extension add WixToolset.UI.wixext" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Build complete!" -ForegroundColor Green
Write-Host "  Portable: $portableDir/" -ForegroundColor White
Write-Host "  Archive:  $zipPath ($zipSize MB)" -ForegroundColor White
if (Test-Path $msiPath) {
    Write-Host "  Installer: $msiPath ($msiSize MB)" -ForegroundColor White
}
Write-Host ""
Write-Host "Portable:  Extract the zip and run $appName.exe" -ForegroundColor Yellow
if (Test-Path $msiPath) {
    Write-Host "Installer: Run the .msi to install to Program Files" -ForegroundColor Yellow
}
