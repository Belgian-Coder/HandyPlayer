# Shared environment initialization for the existing validation and release entry points.
# Only immutable NuGet packages are shared; mutable state stays in this checkout.
$commonDir = & git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir 2>$null
if ($LASTEXITCODE -ne 0) { throw 'A Git checkout is required to locate the project cache.' }
$primaryRoot = Split-Path $commonDir -Parent
$platform = if ([Environment]::OSVersion.Platform -eq 'Win32NT') { 'windows' } elseif ($IsMacOS) { 'macos' } else { 'linux' }
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$state = Join-Path $PSScriptRoot '.harness'
$env:NUGET_PACKAGES = Join-Path $primaryRoot ".harness-cache/$platform-$architecture/packages"
$env:DOTNET_CLI_HOME = Join-Path $state 'dotnet'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $state 'nuget-http'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $state 'nuget-plugins'
$env:TEMP = $env:TMP = $env:TMPDIR = Join-Path $state 'tmp'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
foreach ($directory in @($env:NUGET_PACKAGES, $env:DOTNET_CLI_HOME,
    $env:NUGET_HTTP_CACHE_PATH, $env:NUGET_PLUGINS_CACHE_PATH, $env:TEMP)) {
    [IO.Directory]::CreateDirectory($directory) | Out-Null
}
