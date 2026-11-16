# Publish HandyPlayer for Windows x64 (self-contained, portable, no .NET required)
#
#   pwsh ./publish-win.ps1              # zip + MSI if WiX is installed
#   pwsh ./publish-win.ps1 -RequireMsi  # fail when the MSI cannot be built
param(
    [string]$Configuration = "Release",
    [ValidateSet('Candidate', 'Full')]
    [string]$PackageProfile = 'Full',
    # Release builds: treat a missing WiX toolset as an error instead of skipping the MSI.
    [switch]$RequireMsi,
    # Reuse an existing self-contained win-x64 restore in this checkout.
    [switch]$NoRestore,
    [ValidateRange(1, 64)]
    [int]$MaxParallelism = 2
)

$ErrorActionPreference = "Stop"
$PackageProfile = if ($PackageProfile -ieq 'Candidate') { 'Candidate' } else { 'Full' }
$requiredMsi = [bool]($RequireMsi -or $PackageProfile -eq 'Candidate')
$msiCompression = if ($PackageProfile -eq 'Candidate') { 'medium' } else { 'high' }
$stageSeconds = [ordered]@{ nativeRestore = 0.0; publish = 0.0; portable = 0.0; zip = 0.0; msi = 0.0; inno = 0.0; checksums = 0.0 }
function Complete-PackageStage([string]$Name, [Diagnostics.Stopwatch]$Timer) {
    $Timer.Stop()
    $stageSeconds[$Name] = $Timer.Elapsed.TotalSeconds
    Write-Host ("  Stage {0}: {1:N3}s" -f $Name, $stageSeconds[$Name]) -ForegroundColor DarkGray
}
# All paths below are repo-relative; work from any current directory.
Set-Location $PSScriptRoot
. "$PSScriptRoot/harness-env.ps1"
. "$PSScriptRoot/eng/Windows-Tools.ps1"
$localWix = Get-HandyWixPath -ProjectRoot $PSScriptRoot
if ($localWix) { $null = Initialize-HandyWindowsToolEnvironment -ProjectRoot $PSScriptRoot }

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
$nativeReceiptPath = "$releaseRoot/native-dependency.json"

# Restore the reviewed native build before publishing. Existing mismatching DLLs
# fail without replacement; the application's media prerequisite gate stays intact.
$stageTimer = [Diagnostics.Stopwatch]::StartNew()
. "$PSScriptRoot/eng/Native-Dependency.ps1"
$nativeLock = Get-Content -LiteralPath "$PSScriptRoot/eng/native-dependencies.json" -Raw | ConvertFrom-Json
if ($nativeLock.schemaVersion -ne 1) { throw 'Unsupported native dependency lock schema.' }
$nativeArchives = if ($env:HANDY_NATIVE_ARCHIVE) { @($env:HANDY_NATIVE_ARCHIVE) } else { @() }
$nativeReceipt = Restore-HandyNativeDependency -CheckoutRoot $PSScriptRoot -Specification $nativeLock.'win-x64' `
    -ArchiveCandidates $nativeArchives -EvidencePath $nativeReceiptPath
Write-Host "Native dependency verified: $($nativeReceipt.version) ($($nativeReceipt.sourceKind))" -ForegroundColor DarkGray
Complete-PackageStage 'nativeRestore' $stageTimer

Write-Host "Building $appName v$version for Windows x64..." -ForegroundColor Cyan

# All outputs belong to this fresh run; previous releases are retained.

# Publish self-contained. The Media.Mpv project fails this step if libmpv-2.dll is missing.
[string[]]$restoreArgs = @(if ($NoRestore) { '--no-restore' })
$stageTimer = [Diagnostics.Stopwatch]::StartNew()
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
Complete-PackageStage 'publish' $stageTimer

# Create portable directory
$stageTimer = [Diagnostics.Stopwatch]::StartNew()
Move-Item -LiteralPath $buildDir -Destination $portableDir
Complete-PackageStage 'portable' $stageTimer

Write-Host "Creating archive: $zipPath" -ForegroundColor Cyan

# Use .NET ZipFile directly (more reliable than Compress-Archive with locked files)
$stageTimer = [Diagnostics.Stopwatch]::StartNew()
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Resolve-Path $portableDir).Path,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $true  # include base directory name in zip
)
Complete-PackageStage 'zip' $stageTimer

$zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "  Archive: $zipPath ($zipSize MB)" -ForegroundColor White

# --- MSI installer (requires WiX: dotnet tool install --global wix --version 6.0.2) ---
$wixCmd = if ($localWix) { $localWix } else { (Get-Command wix -ErrorAction SilentlyContinue).Source }
if ($wixCmd) {
    $stageTimer = [Diagnostics.Stopwatch]::StartNew()
    Write-Host ""
    Write-Host "Building MSI installer: $msiPath" -ForegroundColor Cyan

    # WiX's user-cache base is process-local, so -g below still writes only
    # inside this checkout. Load the exact extension version, not the latest.
    $env:WIX_EXTENSIONS = Join-Path $PSScriptRoot '.harness/wix'
    Assert-HandyNoReparseAncestors $env:WIX_EXTENSIONS
    & {
        $ErrorActionPreference = "Continue"
        & $wixCmd extension add -g WixToolset.UI.wixext/6.0.2 2>&1 | Out-Null
    }
    if ($LASTEXITCODE -ne 0) { throw 'WiX UI extension restore failed.' }

    & $wixCmd build installer-win.wxs `
        -arch x64 `
        -ext WixToolset.UI.wixext/6.0.2 `
        -ct $MaxParallelism `
        -d Version=$version `
        -d CabCompression=$msiCompression `
        -d SourceDir=$portableDir `
        -o $msiPath

    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $msiPath)) {
        Write-Host "MSI build failed (wix build returned $LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
    $msiSize = [math]::Round((Get-Item $msiPath).Length / 1MB, 1)
    Write-Host "  Installer: $msiPath ($msiSize MB)" -ForegroundColor White
    Complete-PackageStage 'msi' $stageTimer
} elseif ($requiredMsi) {
    Write-Host "WiX not found and MSI is required (PackageProfile=$PackageProfile, RequireMsi=$RequireMsi). Install with:" -ForegroundColor Red
    Write-Host "  pwsh ./eng/restore-tools.ps1" -ForegroundColor Red
    exit 1
} else {
    Write-Host ""
    Write-Host "Skipping MSI (WiX not found). Install with:" -ForegroundColor Yellow
    Write-Host "  pwsh ./eng/restore-tools.ps1" -ForegroundColor Yellow
}

# --- setup.exe installer (per-user, optional; requires Inno Setup 6: winget install JRSoftware.InnoSetup) ---
$setupPath = "$releaseRoot/$appName-$version-win-x64-setup.exe"
$innoStatus = 'skipped-profile'
if ($PackageProfile -eq 'Candidate') {
    Write-Host "Skipping setup.exe (Candidate profile omits the optional Inno Setup installer)." -ForegroundColor Yellow
} else {
    $innoStatus = 'unavailable'
    $iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
    if (-not $iscc) {
        $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if ($iscc) {
        $stageTimer = [Diagnostics.Stopwatch]::StartNew()
        Write-Host ""
        Write-Host "Building setup.exe installer: $setupPath" -ForegroundColor Cyan
        & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$portableDir" "/O$releaseRoot" installer-win.iss
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $setupPath)) {
            Write-Host "Inno Setup build failed (iscc returned $LASTEXITCODE)" -ForegroundColor Red
            exit 1
        }
        $innoStatus = 'built'
        Complete-PackageStage 'inno' $stageTimer
    } else {
        Write-Host ""
        Write-Host "Skipping setup.exe (Inno Setup not found)." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Build complete!" -ForegroundColor Green
Write-Host "  Portable: $portableDir/" -ForegroundColor White
Write-Host "  Archive:  $zipPath ($zipSize MB)" -ForegroundColor White
if (Test-Path $msiPath) {
    Write-Host "  Installer: $msiPath ($msiSize MB)" -ForegroundColor White
}

# SHA-256 checksums for everything shipped, next to the artifacts.
$stageTimer = [Diagnostics.Stopwatch]::StartNew()
$artifacts = @(Get-Item $zipPath, $nativeReceiptPath) + @(Get-Item $msiPath, $setupPath -ErrorAction SilentlyContinue)
$artifacts | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
    Set-Content "$releaseRoot/SHA256SUMS-win-x64.txt" -Encoding ascii
Write-Host "  Checksums: $releaseRoot/SHA256SUMS-win-x64.txt" -ForegroundColor White
Complete-PackageStage 'checksums' $stageTimer

Write-Host ""
Write-Host "Portable:  Extract the zip and run $appName.exe" -ForegroundColor Yellow
if (Test-Path $msiPath) {
    Write-Host "Installer: Run the .msi to install to Program Files" -ForegroundColor Yellow
}
Write-Output ('HANDY_PACKAGE_RESULT ' + ([ordered]@{
    schemaVersion = 1
    packageProfile = $PackageProfile
    requiredMsi = $requiredMsi
    innoStatus = $innoStatus
    msiCompression = $msiCompression
    stageSeconds = $stageSeconds
} | ConvertTo-Json -Depth 3 -Compress))
} finally {
    Write-Host "Cleanup checkpoint: prior releases/caches retained; failed-run evidence retained on errors. Output: $releaseRoot"
}
