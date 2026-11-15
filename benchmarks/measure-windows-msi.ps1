#Requires -Version 7
<#
.SYNOPSIS
    Compares CAB compression against the same already-published Windows files.
.EXAMPLE
    ./benchmarks/measure-windows-msi.ps1 -PortableDirectory <qualified-portable-folder>
#>
param(
    [Parameter(Mandatory)][string]$PortableDirectory,
    [ValidateSet('high', 'medium', 'low', 'mszip')][string[]]$Levels = @('high', 'medium'),
    [ValidateRange(1, 64)][int]$MaxParallelism = 2
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
. "$root/harness-env.ps1"
. "$root/eng/Native-Dependency.ps1"
. "$root/eng/Windows-Tools.ps1"
$wix = Get-HandyWixPath -ProjectRoot $root
$null = Initialize-HandyWindowsToolEnvironment -ProjectRoot $root
Assert-HandyNativeNoLink $PortableDirectory
$portable = (Get-Item -LiteralPath $PortableDirectory -ErrorAction Stop).FullName
if (-not (Test-Path -LiteralPath (Join-Path $portable 'HandyPlayer.exe') -PathType Leaf)) {
    throw 'PortableDirectory must contain an already-published HandyPlayer.exe.'
}
$output = New-HandyOutputRoot (Join-Path $root ('.harness/msi-profile/' + [guid]::NewGuid().ToString('N')))
$inputs = @(Get-ChildItem -LiteralPath $portable -Recurse -File | Sort-Object FullName | ForEach-Object {
    Assert-HandyNativeNoLink $_.FullName
    [ordered]@{
        path = [IO.Path]::GetRelativePath($portable, $_.FullName).Replace('\', '/')
        size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$inputJson = $inputs | ConvertTo-Json -Depth 4
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
        [ordered]@{
            compression = $level; buildSeconds = $buildSeconds; size = (Get-Item -LiteralPath $msi).Length
            sha256 = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant()
            iceValidationPassed = $true; payloadVerified = $true
        }
    }
)
# The comparison remains valid only if every variant consumed the same bytes.
foreach ($inputFile in $inputs) {
    $file = Join-Path $portable $inputFile.path
    if ((Get-Item -LiteralPath $file).Length -ne $inputFile.size -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $inputFile.sha256) {
        throw "Portable input changed during measurement: $($inputFile.path)"
    }
}
[ordered]@{
    sourceSha = (& git rev-parse HEAD); host = [Environment]::MachineName
    wixVersion = (& $wix --version); jobs = $MaxParallelism; portableDirectory = $portable
    inputManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $output 'inputs.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    measurements = $measurements
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'measurements.json')
Get-Content -LiteralPath (Join-Path $output 'measurements.json')
Write-Output "MSI comparison evidence: $output"
