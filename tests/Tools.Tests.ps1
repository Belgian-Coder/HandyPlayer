#Requires -Version 7
# Offline contract checks for the opt-in project-local WiX restore.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$contractRoot = Join-Path $repoRoot ('.harness/tools-contracts/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($contractRoot) | Out-Null
$global:ToolsDotnetCalls = [Collections.Generic.List[object]]::new()
$global:ToolsDotnetMode = 'list-match'
$global:ToolsDotnetVersion = '6.0.2'
$global:ToolsWixReportedVersion = '6.0.2'
function global:dotnet {
    $global:ToolsDotnetCalls.Add(@($args))
    if ($args[0] -eq 'tool' -and $args[1] -eq 'list') {
        'Package Id Version Commands'
        '---------- ------- --------'
        "wix $global:ToolsDotnetVersion wix"
        $global:LASTEXITCODE = 0
        return
    }
    if ($args[0] -eq 'tool' -and $args[1] -eq 'install') {
        $targetIndex = [Array]::IndexOf($args, '--tool-path') + 1
        $target = $args[$targetIndex]
        [IO.Directory]::CreateDirectory($target) | Out-Null
        if ($global:ToolsDotnetMode -eq 'install-failure') {
            [IO.File]::WriteAllText((Join-Path $target 'partial.restore'), 'retained failure evidence')
            $global:LASTEXITCODE = 23
            return
        }
        [IO.File]::WriteAllText((Join-Path $target 'wix.exe'), 'offline fixture')
        $global:LASTEXITCODE = 0
        return
    }
    throw "Unexpected dotnet call: $($args -join ' ')"
}
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function New-ToolsFixtureRoot([string]$Name) {
    $root = Join-Path $contractRoot $Name
    [IO.Directory]::CreateDirectory((Join-Path $root 'eng')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'eng/restore-tools.ps1') -Destination (Join-Path $root 'eng/restore-tools.ps1')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'eng/Windows-Tools.ps1') -Destination (Join-Path $root 'eng/Windows-Tools.ps1')
    Add-Content -LiteralPath (Join-Path $root 'eng/Windows-Tools.ps1') -Value @'
function Get-HandyWixVersion([string]$WixPath) { return $global:ToolsWixReportedVersion }
'@
    Set-Content -LiteralPath (Join-Path $root 'harness-env.ps1') -Value '# Fixture: no process or filesystem setup.'
    return $root
}
function Invoke-ToolsRestore([string]$Root, [switch]$Plan) {
    $oldLocation = Get-Location
    try {
        if ($Plan) {
            $output = @(& (Join-Path $Root 'eng/restore-tools.ps1') -ProjectRoot $Root -Plan 2>&1 | ForEach-Object { $_.ToString() })
        } else {
            $output = @(& (Join-Path $Root 'eng/restore-tools.ps1') -ProjectRoot $Root 2>&1 | ForEach-Object { $_.ToString() })
        }
        return [pscustomobject]@{ Success = $true; Output = $output; Error = $null }
    } catch {
        return [pscustomobject]@{ Success = $false; Output = @(); Error = $_.Exception.Message }
    } finally {
        Set-Location -LiteralPath $oldLocation
    }
}

$planRoot = New-ToolsFixtureRoot 'plan'
$callsBefore = $global:ToolsDotnetCalls.Count
$cliHomeBefore = $env:DOTNET_CLI_HOME
$pathBefore = $env:PATH
$plan = Invoke-ToolsRestore $planRoot -Plan
Assert $plan.Success "Plan should succeed: $($plan.Error)"
$planData = $plan.Output[0] | ConvertFrom-Json
Assert ($planData.version -ceq '6.0.2') 'Plan must report the pinned version.'
Assert ($planData.command -match 'dotnet tool install wix .*--version 6\.0\.2 .*--tool-path') 'Plan must show the explicit local install command.'
Assert ($global:ToolsDotnetCalls.Count -eq $callsBefore) 'Plan must not invoke dotnet.'
Assert (-not (Test-Path -LiteralPath (Join-Path $planRoot '.harness'))) 'Plan must not create harness state.'
Assert ($env:DOTNET_CLI_HOME -ceq $cliHomeBefore -and $env:PATH -ceq $pathBefore) 'Plan must not change process environment.'

$reuseRoot = New-ToolsFixtureRoot 'reuse'
$reuseTarget = Join-Path $reuseRoot '.harness/tools/wix-6.0.2'
[IO.Directory]::CreateDirectory($reuseTarget) | Out-Null
[IO.File]::WriteAllText((Join-Path $reuseTarget 'wix.exe'), 'fixture executable')
. (Join-Path $repoRoot 'eng/Windows-Tools.ps1')
function Get-HandyWixVersion([string]$WixPath) { return $global:ToolsWixReportedVersion }
$parsedVersion = ConvertFrom-HandyWixVersionOutput @('6.0.2+b3f3403')
Assert ($parsedVersion -ceq '6.0.2+b3f3403') 'Version parser must preserve WiX build metadata.'
$unknownVersionRejected = $false
try { ConvertFrom-HandyWixVersionOutput @('WiX version unknown') | Out-Null } catch { $unknownVersionRejected = $true }
Assert $unknownVersionRejected 'Unknown WiX version output must be rejected.'
$global:ToolsWixReportedVersion = '6.0.2+b3f3403'
$wixPath = Get-HandyWixPath $reuseRoot
Assert ($wixPath -ceq (Join-Path $reuseTarget 'wix.exe')) 'The helper must resolve the fixed checkout-local wix.exe.'
$oldWixPath = $env:PATH
$oldWixExtensions = $env:WIX_EXTENSIONS
$env:PATH = "C:\global\wix;$reuseTarget;C:\Windows\System32"
$resolvedWix = Initialize-HandyWindowsToolEnvironment $reuseRoot
Assert ($resolvedWix -ceq $wixPath) 'Environment initialization must return the local WiX path.'
Assert ($env:PATH.StartsWith("$reuseTarget$([IO.Path]::PathSeparator)", [StringComparison]::OrdinalIgnoreCase)) 'Local WiX directory must be first in process PATH.'
Assert ($env:WIX_EXTENSIONS -ceq (Join-Path $reuseRoot '.harness/wix')) 'WiX extensions must use checkout-local storage.'
$env:PATH = $oldWixPath
$env:WIX_EXTENSIONS = $oldWixExtensions

$exeLinkRoot = New-ToolsFixtureRoot 'exe-link'
$exeLinkTarget = Join-Path $exeLinkRoot '.harness/tools/wix-6.0.2'
[IO.Directory]::CreateDirectory($exeLinkTarget) | Out-Null
$exeTargetFile = Join-Path $contractRoot 'wix-executable-target'
[IO.File]::WriteAllText($exeTargetFile, 'fixture target')
$exeLinkCreated = $false
try {
    [IO.File]::CreateSymbolicLink((Join-Path $exeLinkTarget 'wix.exe'), $exeTargetFile) | Out-Null
    $exeLinkCreated = $true
} catch {
    # Symlink creation can be disabled by the host.
}
if ($exeLinkCreated) {
    $exeLinkRejected = $false
    try { Get-HandyWixPath $exeLinkRoot | Out-Null } catch { $exeLinkRejected = $_.Exception.Message -like '*executable must not be a link*' }
    Assert $exeLinkRejected 'The local WiX executable itself must not be a link.'
}

$cacheLinkRoot = New-ToolsFixtureRoot 'extension-link'
$cacheLinkToolDirectory = Join-Path $cacheLinkRoot '.harness/tools/wix-6.0.2'
[IO.Directory]::CreateDirectory($cacheLinkToolDirectory) | Out-Null
[IO.File]::WriteAllText((Join-Path $cacheLinkToolDirectory 'wix.exe'), 'fixture executable')
$cacheLinkTarget = Join-Path $cacheLinkRoot '.harness/wix'
$cacheLinkCreated = $false
try {
    [IO.Directory]::CreateSymbolicLink($cacheLinkTarget, $contractRoot) | Out-Null
    $cacheLinkCreated = $true
} catch {
    # Symlink creation can be disabled by the host.
}
if ($cacheLinkCreated) {
    $pathBeforeCacheCheck = $env:PATH
    $extensionsBeforeCacheCheck = $env:WIX_EXTENSIONS
    $cacheLinkRejected = $false
    try { Initialize-HandyWindowsToolEnvironment $cacheLinkRoot | Out-Null } catch { $cacheLinkRejected = $_.Exception.Message -like '*must not use a link or junction*' }
    Assert $cacheLinkRejected 'The extension cache must not use a reparse ancestor.'
    Assert ($env:PATH -ceq $pathBeforeCacheCheck -and $env:WIX_EXTENSIONS -ceq $extensionsBeforeCacheCheck) 'Reparse refusal must happen before environment changes.'
}

$global:ToolsDotnetCalls.Clear()
$global:ToolsDotnetVersion = '6.0.2+b3f3403'
$reuse = Invoke-ToolsRestore $reuseRoot
Assert $reuse.Success 'An existing matching version should be reused.'
Assert ((($reuse.Output -join "`n") | ConvertFrom-Json).status -ceq 'reused') 'Matching tool should report reuse.'
Assert ($global:ToolsDotnetCalls.Count -eq 1) 'Reuse should inspect the project-local tool store once.'
$reuseCall = @($global:ToolsDotnetCalls[0])
Assert (($reuseCall[0..2] -join ' ') -ceq 'tool list --tool-path') 'Reuse must not provision or inspect global tools.'

$mismatchRoot = New-ToolsFixtureRoot 'mismatch'
$mismatchTarget = Join-Path $mismatchRoot '.harness/tools/wix-6.0.2'
[IO.Directory]::CreateDirectory($mismatchTarget) | Out-Null
$global:ToolsWixReportedVersion = '6.0.3'
[IO.File]::WriteAllText((Join-Path $mismatchTarget 'wix.exe'), 'mismatched fixture executable')
$global:ToolsDotnetVersion = '6.0.3'
$global:ToolsDotnetCalls.Clear()
$mismatch = Invoke-ToolsRestore $mismatchRoot
Assert (-not $mismatch.Success -and $mismatch.Error -like '*does not match pinned version*') 'A different installed version must fail clearly.'
Assert (Test-Path -LiteralPath (Join-Path $mismatchTarget 'wix.exe')) 'A mismatching target must be preserved.'
Assert ($global:ToolsDotnetCalls.Count -eq 0) 'A mismatching target must not be reinstalled or inspected after its version check fails.'

$unknownRoot = New-ToolsFixtureRoot 'unknown'
$unknownTarget = Join-Path $unknownRoot '.harness/tools/wix-6.0.2'
[IO.Directory]::CreateDirectory($unknownTarget) | Out-Null
[IO.File]::WriteAllText((Join-Path $unknownTarget 'unknown.bin'), 'preserve')
$global:ToolsDotnetCalls.Clear()
$unknown = Invoke-ToolsRestore $unknownRoot
Assert (-not $unknown.Success -and $unknown.Error -like '*unknown*preserving it*') 'An unknown existing target must be refused.'
Assert (Test-Path -LiteralPath (Join-Path $unknownTarget 'unknown.bin')) 'Unknown target contents must be preserved.'
Assert ($global:ToolsDotnetCalls.Count -eq 0) 'Unknown target refusal must happen before dotnet.'

$failureRoot = New-ToolsFixtureRoot 'failure'
$failureTarget = Join-Path $failureRoot '.harness/tools/wix-6.0.2'
$global:ToolsDotnetMode = 'install-failure'
$global:ToolsDotnetCalls.Clear()
$failedInstall = Invoke-ToolsRestore $failureRoot
Assert (-not $failedInstall.Success -and $failedInstall.Error -like '*retaining any partial files*') 'Install failure should be reported.'
Assert (Test-Path -LiteralPath (Join-Path $failureTarget 'partial.restore')) 'Install failure evidence must be retained.'
$installCall = $global:ToolsDotnetCalls[0]
Assert (($installCall -join ' ') -match '^tool install wix --version 6\.0\.2 --tool-path ') 'Install must pin the exact tool and use a tool path.'
Assert ($installCall -notcontains '--global' -and $installCall -notcontains 'cache') 'Install must not use global tools or cache clearing.'

$successRoot = New-ToolsFixtureRoot 'install'
$global:ToolsDotnetMode = 'install-success'
$global:ToolsDotnetVersion = '6.0.2'
$global:ToolsWixReportedVersion = '6.0.2'
$global:ToolsDotnetCalls.Clear()
$installed = Invoke-ToolsRestore $successRoot
Assert $installed.Success 'A successful pinned local install should complete.'
$installResult = ($installed.Output -join "`n") | ConvertFrom-Json
Assert ($installResult.status -ceq 'installed' -and $installResult.version -ceq '6.0.2') 'Install should report its exact version and status.'
Assert ($global:ToolsDotnetCalls.Count -eq 2) 'Successful install must install, then verify the local tool store.'
Assert ((Test-Path -LiteralPath (Join-Path $successRoot '.harness/tools/wix-6.0.2/wix.exe'))) 'Install output must remain in the checkout-local path.'

$linkRoot = Join-Path $contractRoot 'linked-root'
$linkCreated = $false
try {
    [IO.Directory]::CreateSymbolicLink($linkRoot, $reuseRoot) | Out-Null
    $linkCreated = $true
} catch {
    # Symlink creation can be disabled by the host; all other safety cases still run.
}
if ($linkCreated) {
    $global:ToolsDotnetCalls.Clear()
    $linked = Invoke-ToolsRestore $linkRoot
    Assert (-not $linked.Success -and $linked.Error -like '*must not use a link or junction*') 'A reparse ancestor must be refused.'
    Assert ($global:ToolsDotnetCalls.Count -eq 0) 'Reparse refusal must happen before dotnet.'
}

$global:ToolsDotnetMode = 'list-match'
$global:ToolsDotnetVersion = '6.0.2'
Remove-Item Function:global:dotnet
Write-Output 'WiX local restore plan, reuse, refusal, isolation and retention checks passed.'
