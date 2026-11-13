<#
.SYNOPSIS
  Runs the HandyPlaylistPlayer BenchmarkDotNet suite on one runtime.
.EXAMPLE
  pwsh D:\Projects\HandyPlayer\benchmarks\run-benchmarks.ps1 -OutDir C:\temp\bench\net11 -Framework net11.0
  pwsh D:\Projects\HandyPlayer\benchmarks\run-benchmarks.ps1 -OutDir C:\temp\bench\net11-noasync -Framework net11.0 -RuntimeAsync off -Filter '*AsyncHeavy*'
#>
param(
    [Parameter(Mandatory)][string]$OutDir,
    [ValidateSet('net10.0', 'net11.0')][string]$Framework = 'net11.0',
    [string]$Filter = '*',
    [ValidateSet('on', 'off')][string]$RuntimeAsync = 'on',
    [string]$Root = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
# The libraries must be built for both net10.0 and net11.0 (see Directory.Build.props).
$env:HandyMultiTarget = 'true'
$env:HandyRuntimeAsync = $RuntimeAsync

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$proj = Join-Path $Root 'benchmarks\HandyPlaylistPlayer.Benchmarks\HandyPlaylistPlayer.Benchmarks.csproj'
dotnet run -c Release -f $Framework --project $proj -- --filter $Filter --artifacts $OutDir
