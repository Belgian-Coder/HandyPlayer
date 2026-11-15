#Requires -Version 7
<#
.SYNOPSIS
    Compares CAB compression against the same already-published Windows files.
.EXAMPLE
    ./benchmarks/measure-windows-msi.ps1 -PortableDirectory <qualified-portable-folder>
#>
param(
    [Parameter(Mandatory)][string]$PortableDirectory,
    [ValidateCount(1, 4)][ValidateSet('high', 'medium', 'low', 'mszip')][string[]]$Levels = @('high', 'medium'),
    [ValidateRange(1, 64)][int]$MaxParallelism = 2
)
$ErrorActionPreference = 'Stop'
function Assert-HandyMsiBenchmarkInputs([string]$Directory, [string]$ExpectedHash, [int]$ExpectedCount) {
    $current = @(Get-HandyPackageTreeFiles $Directory)
    if ($current.Count -ne $ExpectedCount -or
        (Get-HandyPackagePortableManifestHash $current) -cne $ExpectedHash) {
        throw 'Portable input file set or bytes changed during measurement.'
    }
}
function Assert-HandyMsiBenchmarkVerification($Verification, [string]$ExpectedHash, [int]$ExpectedCount) {
    if ($Verification.result -cne 'passed' -or
        $Verification.portableManifest.fileCount -ne $ExpectedCount -or
        $Verification.portableManifest.sha256 -cne $ExpectedHash) {
        throw 'MSI verification does not match the initial portable input manifest.'
    }
}
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
. "$root/harness-env.ps1"
. "$root/eng/Native-Dependency.ps1"
. "$root/eng/Windows-Tools.ps1"
. "$root/eng/verify-windows-package.ps1" -PortableDirectory $PortableDirectory
$wix = Get-HandyWixPath -ProjectRoot $root
$null = Initialize-HandyWindowsToolEnvironment -ProjectRoot $root
Assert-HandyNativeNoLink $PortableDirectory
$portable = (Get-Item -LiteralPath $PortableDirectory -ErrorAction Stop).FullName
if (-not (Test-Path -LiteralPath (Join-Path $portable 'HandyPlayer.exe') -PathType Leaf)) {
    throw 'PortableDirectory must contain an already-published HandyPlayer.exe.'
}
$output = New-HandyOutputRoot (Join-Path $root ('.harness/msi-profile/' + [guid]::NewGuid().ToString('N')))
$inputs = @(Get-HandyPackageTreeFiles $portable)
$inputManifestHash = Get-HandyPackagePortableManifestHash $inputs
$inputJson = $inputs | Select-Object path, bytes, sha256 | ConvertTo-Json -Depth 4
$inputJson | Set-Content -LiteralPath (Join-Path $output 'inputs.json')
$source = [IO.File]::ReadAllText((Join-Path $root 'installer-win.wxs'))
if ([regex]::Matches($source, [regex]::Escape('CompressionLevel="$(var.CabCompression)"')).Count -ne 1) {
    throw 'Expected the canonical CabCompression-controlled MediaTemplate.'
}
$version = (dotnet msbuild src/HandyPlaylistPlayer.App/HandyPlaylistPlayer.App.csproj -getProperty:Version -nologo).Trim()
if ($LASTEXITCODE -ne 0 -or -not $version) { throw 'Could not evaluate application version.' }
& $wix extension add -g WixToolset.UI.wixext/6.0.2 *> (Join-Path $output 'extension.log')
if ($LASTEXITCODE -ne 0) { throw 'Pinned UI extension restore failed; see extension.log.' }
$measurements = @(
    foreach ($level in $Levels) {
        Assert-HandyMsiBenchmarkInputs $portable $inputManifestHash $inputs.Count
        $directory = Join-Path $output $level
        $null = New-Item -ItemType Directory -Path $directory -ErrorAction Stop
        $wxs = Join-Path $directory 'installer-win.wxs'
        [IO.File]::WriteAllText($wxs, $source)
        $msi = Join-Path $directory "HandyPlayer-$version-win-x64.msi"
        $timer = [Diagnostics.Stopwatch]::StartNew()
        & $wix build $wxs -arch x64 -ext WixToolset.UI.wixext/6.0.2 -ct $MaxParallelism `
            -d "Version=$version" -d "CabCompression=$level" -d "SourceDir=$portable" -o $msi *> (Join-Path $directory 'build.log')
        $timer.Stop()
        if ($LASTEXITCODE -ne 0) { throw "MSI $level build failed; see $directory/build.log." }
        $buildSeconds = $timer.Elapsed.TotalSeconds
        & $wix msi validate $msi *> (Join-Path $directory 'validate.log')
        if ($LASTEXITCODE -ne 0) { throw "MSI $level ICE validation failed; see $directory/validate.log." }
        & "$root/eng/verify-windows-package.ps1" -Msi $msi -PortableDirectory $portable `
            -OutputDirectory (Join-Path $directory 'verification') *> (Join-Path $directory 'verification.log')
        if ($LASTEXITCODE -ne 0) { throw "MSI $level payload verification failed; see $directory/verification.log." }
        $verification = Get-Content -LiteralPath (Join-Path $directory 'verification/verification.json') -Raw | ConvertFrom-Json
        Assert-HandyMsiBenchmarkVerification $verification $inputManifestHash $inputs.Count
        Assert-HandyMsiBenchmarkInputs $portable $inputManifestHash $inputs.Count
        [ordered]@{
            compression = $level; buildSeconds = $buildSeconds; size = (Get-Item -LiteralPath $msi).Length
            sha256 = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant()
            iceValidationPassed = $true; payloadVerified = $true
        }
    }
)
# The comparison remains valid only if every variant consumed the same bytes.
Assert-HandyMsiBenchmarkInputs $portable $inputManifestHash $inputs.Count
[ordered]@{
    sourceSha = (& git rev-parse HEAD); host = [Environment]::MachineName
    wixVersion = (& $wix --version); jobs = $MaxParallelism; portableDirectory = $portable
    inputManifestSha256 = $inputManifestHash; inputFileCount = $inputs.Count
    measurements = $measurements
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'measurements.json')
Get-Content -LiteralPath (Join-Path $output 'measurements.json')
Write-Output "MSI comparison evidence: $output"
