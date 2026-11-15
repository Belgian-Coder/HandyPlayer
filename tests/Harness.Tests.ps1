#Requires -Version 7
# No SDK, packages, application build, hardware or network required.
param([ValidateSet('restore', 'build', 'test')][string]$FailureStep)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$global:HarnessCalls = [Collections.Generic.List[object]]::new()
$global:HarnessFailureStep = $FailureStep
function global:dotnet {
    $global:HarnessCalls.Add(@($args))
    $global:LASTEXITCODE = if ($args[0] -eq $global:HarnessFailureStep) { 19 } else { 0 }
}
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
if ($FailureStep) {
    & "$root/validate.ps1" 6>$null
    $failureExit = $LASTEXITCODE
    $expectedCalls = [Array]::IndexOf(@('restore', 'build', 'test'), $FailureStep) + 1
    Assert ($global:HarnessCalls.Count -eq $expectedCalls) "A failed $FailureStep did not stop later stages."
    exit $failureExit
}
try {
    # Check the plan before any invocation can initialize the environment.
    $stateBefore = Test-Path (Join-Path $root '.harness')
    $cliHomeBefore = $env:DOTNET_CLI_HOME
    & "$root/validate.ps1" -Suite UI -Plan 6>$null
    Assert ($global:HarnessCalls.Count -eq 0) 'Plan must never invoke dotnet.'
    Assert ((Test-Path (Join-Path $root '.harness')) -eq $stateBefore) 'Plan must not create harness state.'
    Assert ($env:DOTNET_CLI_HOME -eq $cliHomeBefore) 'Plan must not change the environment.'

    & "$root/validate.ps1" -Coverage 6>$null
    Assert ($global:HarnessCalls.Count -eq 3) 'Full validation must restore, build and test.'
    Assert ($global:HarnessCalls[0][1] -eq 'HandyPlaylistPlayer.sln') 'Default gate must retain the solution.'
    Assert ($global:HarnessCalls[1] -contains '-warnaserror') 'Warnings-as-errors gate lost.'
    Assert ($global:HarnessCalls[2] -contains '--coverlet') 'Coverage flag lost.'
    Assert ($global:HarnessCalls[2] -contains '--minimum-expected-tests') 'Empty test selections must fail.'
    Assert ($global:HarnessCalls[2] -contains 'Category=LiveDevice') 'Live device exclusion lost.'
    Assert ($global:HarnessCalls[0] -contains '--disable-parallel') 'Restore project concurrency must be bounded.'
    Assert ($global:HarnessCalls[0] -contains '-maxcpucount:2') 'Restore workers must be bounded.'
    Assert ($global:HarnessCalls[1] -contains '-maxcpucount:2') 'Build workers must be bounded.'
    $moduleLimitIndex = [Array]::IndexOf($global:HarnessCalls[2], '--max-parallel-test-modules')
    Assert ($moduleLimitIndex -ge 0 -and $global:HarnessCalls[2][$moduleLimitIndex + 1] -eq '2') 'Test module concurrency must be bounded.'

    $global:HarnessCalls.Clear()
    & "$root/validate.ps1" -Suite Unit -Class '*PlaybackCoordinatorTests' -NoRestore -MaxParallelism 1 6>$null
    Assert ($global:HarnessCalls.Count -eq 2) 'NoRestore must still build and test.'
    Assert ($global:HarnessCalls[0][1] -eq 'tests/HandyPlaylistPlayer.Tests/HandyPlaylistPlayer.Tests.csproj') 'Unit build scope wrong.'
    Assert ($global:HarnessCalls[1] -contains '--project') 'MTP project selection lost.'
    Assert ($global:HarnessCalls[1] -contains '--filter-class') 'xUnit class filter lost.'
    Assert ($global:HarnessCalls[1] -contains '*PlaybackCoordinatorTests') 'Class glob changed.'
    Assert ($global:HarnessCalls[0] -contains '-maxcpucount:1') 'Build ignored the selected worker limit.'
    $moduleLimitIndex = [Array]::IndexOf($global:HarnessCalls[1], '--max-parallel-test-modules')
    Assert ($moduleLimitIndex -ge 0 -and $global:HarnessCalls[1][$moduleLimitIndex + 1] -eq '1') 'Tests ignored the selected module limit.'

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
    Assert ($env:DOTNET_GENERATE_ASPNET_CERTIFICATE -eq 'false') 'Harness must not create unsolicited ASP.NET development certificates.'
    $common = git -C $root rev-parse --path-format=absolute --git-common-dir
    Assert ($env:NUGET_PACKAGES.StartsWith((Split-Path $common -Parent))) 'Package cache escaped primary project.'
    Assert (Test-Path $env:NUGET_PACKAGES) 'Package cache not initialized.'

    foreach ($step in @('restore', 'build', 'test')) {
        & (Join-Path $PSHOME 'pwsh') -NoProfile -NonInteractive -File $PSCommandPath -FailureStep $step
        Assert ($LASTEXITCODE -eq 19) "$step failure must preserve exit code and stop later stages."
    }
    $global:LASTEXITCODE = 0
    Write-Output 'Harness command, safety-gate, failure and containment checks passed.'
} finally {
    # Remove only the in-process mock function; no filesystem cleanup occurs.
    Remove-Item Function:global:dotnet
}
