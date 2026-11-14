#Requires -Version 7
# No SDK, packages, application build, hardware or network required.
param([switch]$FailureProbe)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$global:HarnessCalls = [Collections.Generic.List[object]]::new()
$global:HarnessFailureProbe = $FailureProbe
function global:dotnet {
    $global:HarnessCalls.Add(@($args))
    $global:LASTEXITCODE = if ($global:HarnessFailureProbe) { 19 } else { 0 }
}
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
if ($FailureProbe) {
    & "$root/validate.ps1" 6>$null
    Assert ($global:HarnessCalls.Count -eq 1) 'A failed restore did not stop later stages.'
    exit $LASTEXITCODE
}
try {
    & "$root/validate.ps1" -Coverage 6>$null
    Assert ($global:HarnessCalls.Count -eq 3) 'Full validation must restore, build and test.'
    Assert ($global:HarnessCalls[0][1] -eq 'HandyPlaylistPlayer.sln') 'Default gate must retain the solution.'
    Assert ($global:HarnessCalls[1] -contains '-warnaserror') 'Warnings-as-errors gate lost.'
    Assert ($global:HarnessCalls[2] -contains '--coverlet') 'Coverage flag lost.'
    Assert ($global:HarnessCalls[2] -contains '--minimum-expected-tests') 'Empty test selections must fail.'
    Assert ($global:HarnessCalls[2] -contains 'Category=LiveDevice') 'Live device exclusion lost.'

    $global:HarnessCalls.Clear()
    & "$root/validate.ps1" -Suite Unit -Class '*PlaybackCoordinatorTests' -NoRestore 6>$null
    Assert ($global:HarnessCalls.Count -eq 2) 'NoRestore must still build and test.'
    Assert ($global:HarnessCalls[0][1] -eq 'tests/HandyPlaylistPlayer.Tests/HandyPlaylistPlayer.Tests.csproj') 'Unit build scope wrong.'
    Assert ($global:HarnessCalls[1] -contains '--project') 'MTP project selection lost.'
    Assert ($global:HarnessCalls[1] -contains '--filter-class') 'xUnit class filter lost.'
    Assert ($global:HarnessCalls[1] -contains '*PlaybackCoordinatorTests') 'Class glob changed.'

    $global:HarnessCalls.Clear()
    & "$root/validate.ps1" -Suite UI -NoBuild -IncludeLiveDevice 6>$null
    Assert ($global:HarnessCalls.Count -eq 1) 'NoBuild must run only tests.'
    Assert ($global:HarnessCalls[0][2] -eq 'tests/HandyPlaylistPlayer.UiTests/HandyPlaylistPlayer.UiTests.csproj') 'UI scope wrong.'
    Assert ($global:HarnessCalls[0] -notcontains '--filter-not-trait') 'Live device opt-in ignored.'

    $global:HarnessCalls.Clear()
    & "$root/validate.ps1" -Suite UI -Plan 6>$null
    Assert ($global:HarnessCalls.Count -eq 0) 'Plan must never invoke dotnet.'
    Assert ($env:DOTNET_CLI_HOME -eq (Join-Path $root '.harness/dotnet')) 'CLI home escaped checkout.'
    Assert ($env:TMPDIR -eq (Join-Path $root '.harness/tmp')) 'Temporary files escaped checkout.'
    $common = git -C $root rev-parse --path-format=absolute --git-common-dir
    Assert ($env:NUGET_PACKAGES.StartsWith((Split-Path $common -Parent))) 'Package cache escaped primary project.'
    Assert (Test-Path $env:NUGET_PACKAGES) 'Package cache not initialized.'

    & (Join-Path $PSHOME 'pwsh') -NoProfile -NonInteractive -File $PSCommandPath -FailureProbe
    Assert ($LASTEXITCODE -eq 19) 'Restore failure must preserve exit code and stop later stages.'
    $global:LASTEXITCODE = 0
    Write-Output 'Harness command, safety-gate, failure and containment checks passed.'
} finally {
    # Remove only the in-process mock function; no filesystem cleanup occurs.
    Remove-Item Function:global:dotnet
}
