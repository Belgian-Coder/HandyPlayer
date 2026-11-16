#Requires -Version 7
# Real Git fixture contracts for canonical cache-root and independent release-clone ownership.
param([string]$PythonExecutable)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$helper = Join-Path $repositoryRoot 'eng/Project-Root.ps1'
if (-not (Test-Path -LiteralPath $helper -PathType Leaf)) { throw 'eng/Project-Root.ps1 must exist before cache-root contracts run.' }
$pwshExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
if ($PythonExecutable) {
    $PythonExecutable = (Resolve-Path -LiteralPath $PythonExecutable -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $PythonExecutable -PathType Leaf)) { throw 'PythonExecutable must name an existing executable.' }
} else {
    $pythonCommand = Get-Command python3 -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pythonCommand) { $PythonExecutable = $pythonCommand.Source }
}
$fixtureRoot = Join-Path $repositoryRoot ('.harness/cache-root-contracts/' + [guid]::NewGuid().ToString('N'))
$template = Join-Path $fixtureRoot 'empty-template'
$hooks = Join-Path $fixtureRoot 'empty-hooks'
$primary = Join-Path $fixtureRoot 'primary'
$clone = Join-Path $primary '.wt/release-win-x64'
$linked = Join-Path $primary '.wt/linked-worktree'
$wrongClone = Join-Path $primary '.wt/wrong'
$probe = Join-Path $fixtureRoot 'probe.ps1'
$canonicalOrigin = 'https://github.com/Belgian-Coder/HandyPlayer.git'
$manifest = @'
{
  "schemaVersion": 1,
  "project": "HandyPlayer",
  "stateDirectory": ".harness/remote-release",
  "worktreeDirectory": ".wt",
  "targets": {
    "win-x64": { "host": "fixture", "platform": "windows", "architecture": "x64" },
    "osx-arm64": { "host": "fixture", "platform": "macos", "architecture": "arm64" }
  }
}
'@

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Invoke-FixtureGit([string]$Directory, [string[]]$Arguments, [switch]$AllowFailure) {
    $gitCommand = Get-Command git -CommandType Application | Select-Object -First 1
    $start = [Diagnostics.ProcessStartInfo]::new($gitCommand.Source)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['GIT_CONFIG_NOSYSTEM'] = '1'
    $start.Environment['GIT_TEMPLATE_DIR'] = $template
    foreach ($argument in @('-c', "init.templateDir=$template", '-c', "core.hooksPath=$hooks", '-c', 'filter.lfs.process=', '-c', 'filter.lfs.smudge=',
        '-c', 'filter.lfs.required=false')) { $start.ArgumentList.Add($argument) }
    if ($Directory) { $start.ArgumentList.Add('-C'); $start.ArgumentList.Add($Directory) }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $process.Dispose()
    if ($exitCode -ne 0 -and -not $AllowFailure) { throw "git $($Arguments -join ' ') failed ($exitCode): $stderr" }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $stdout.Trim(); Error = $stderr.Trim() }
}

function Write-Owner([string]$Path, [string]$Target = 'win-x64', [string]$Root = $primary, [hashtable]$Extra = @{}) {
    $owner = [ordered]@{ owner = 'request-release'; version = 1; project = 'HandyPlayer'; target = $Target; root = $Root }
    foreach ($key in $Extra.Keys) { $owner[$key] = $Extra[$key] }
    [IO.File]::WriteAllText($Path, ($owner | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
}

function Invoke-HarnessProbe([string]$Checkout, [string]$Hint = '') {
    $start = [Diagnostics.ProcessStartInfo]::new($pwshExecutable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', $probe, '-Checkout', $Checkout)) {
        $start.ArgumentList.Add($argument)
    }
    $start.Environment['BUILD_RELEASE_PROJECT_ROOT'] = $Hint
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $process.Dispose()
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $stdout.Trim(); Error = $stderr.Trim() }
}

function Invoke-PythonRoot([string]$Checkout, [string]$DefaultRoot, [string]$Hint) {
    $script = Join-Path $Checkout 'eng/project-root.py'
    $start = [Diagnostics.ProcessStartInfo]::new($PythonExecutable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-I', '-B', $script, $Checkout, $DefaultRoot, $Hint)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $process.Dispose()
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $stdout.Trim(); Error = $stderr.Trim() }
}

function Copy-FixtureHarnessWithoutUnixPipeSetup([string]$Destination) {
    $source = Get-Content -LiteralPath (Join-Path $repositoryRoot 'harness-env.ps1') -Raw
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'Could not parse production harness for the isolated cache fixture.' }
    $candidates = @($ast.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.IfStatementAst] -and
        $_.Clauses.Count -gt 0 -and $_.Clauses[0].Item1.Extent.Text.Trim() -ceq '-not $IsWindows'
    })
    if ($candidates.Count -ne 1) { throw 'Expected exactly one top-level Unix pipe initialization block in harness-env.ps1.' }
    $block = $candidates[0].Extent
    $comment = "# Fixture-only: preserve real cache selection, validation, environment exports and directory creation; the long fixture skips Unix socket initialization, which tests/Pipe.Tests.ps1 exercises."
    $fixtureText = $source.Substring(0, $block.StartOffset) + $comment + [Environment]::NewLine +
        $source.Substring($block.EndOffset)
    [IO.File]::WriteAllText($Destination, $fixtureText, [Text.UTF8Encoding]::new($false))
}

function Invoke-BashHarnessProbe([string]$Checkout, [string]$Hint) {
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command bash -CommandType Application | Select-Object -First 1).Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-c', 'set -e; cd "$1"; . ./harness-env.sh; echo unexpected-success', '_', $Checkout)) {
        $start.ArgumentList.Add($argument)
    }
    $start.Environment['BUILD_RELEASE_PROJECT_ROOT'] = $Hint
    $start.Environment['TESTINGPLATFORM_PIPE_DIRECTORY'] = ''
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $process.Dispose()
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $stdout.Trim(); Error = $stderr.Trim() }
}

function Get-StateSnapshot([string]$Checkout = $clone) {
    $paths = @((Join-Path $primary '.harness-cache'), (Join-Path $Checkout '.harness-cache'), (Join-Path $primary '.harness'),
        (Join-Path $clone '.harness'), (Join-Path $linked '.harness'), (Join-Path $Checkout '.harness'))
    $records = [Collections.Generic.List[string]]::new()
    foreach ($path in $paths) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        foreach ($item in Get-ChildItem -LiteralPath $path -Force -Recurse) {
            if ($item.PSIsContainer) { $records.Add("$($item.FullName)|dir") }
            else { $records.Add("$($item.FullName)|$((Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash)") }
        }
    }
    return (@($records | Sort-Object) -join "`n")
}

function Assert-RefusedWithoutWrites([string]$Label, [string]$Checkout = $clone, [string]$Hint = $primary) {
    $before = Get-StateSnapshot $Checkout
    $result = Invoke-HarnessProbe $Checkout $Hint
    Assert ($result.ExitCode -ne 0) "$Label was accepted. stdout=$($result.Output) stderr=$($result.Error)"
    $after = Get-StateSnapshot $Checkout
    Assert ($before -ceq $after) "$Label changed harness/cache state before refusing. stderr=$($result.Error)"
    if ($PythonExecutable) {
        $commonDir = (Invoke-FixtureGit $Checkout @('rev-parse', '--path-format=absolute', '--git-common-dir')).Output
        $defaultRoot = Split-Path $commonDir -Parent
        $pythonBefore = Get-StateSnapshot $Checkout
        $pythonResult = Invoke-PythonRoot $Checkout $defaultRoot $Hint
        Assert ($pythonResult.ExitCode -ne 0) "Python selector accepted $Label. stdout=$($pythonResult.Output) stderr=$($pythonResult.Error)"
        Assert ((Get-StateSnapshot $Checkout) -ceq $pythonBefore) "Python selector changed harness/cache state while refusing $Label."
    }
}

function New-HarnessProbe {
    @'
param([string]$Checkout)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $Checkout
. (Join-Path $Checkout 'harness-env.ps1')
[ordered]@{
    packages = $env:NUGET_PACKAGES
    scratch = $env:NUGET_SCRATCH
    http = $env:NUGET_HTTP_CACHE_PATH
    cli = $env:DOTNET_CLI_HOME
    plugins = $env:NUGET_PLUGINS_CACHE_PATH
    temp = $env:TEMP
    pipe = $env:TESTINGPLATFORM_PIPE_DIRECTORY
} | ConvertTo-Json -Compress
'@ | Set-Content -LiteralPath $probe -Encoding utf8
}

function Read-HarnessEnvironment([string]$Checkout, [string]$Hint = '') {
    $result = Invoke-HarnessProbe $Checkout $Hint
    Assert ($result.ExitCode -eq 0) "Harness initialization failed for $Checkout (hint=$Hint): $($result.Error)"
    return $result.Output | ConvertFrom-Json
}

$oldHint = $env:BUILD_RELEASE_PROJECT_ROOT
$oldLocation = Get-Location
$oldErrorPreference = $ErrorActionPreference
try {
    $null = [IO.Directory]::CreateDirectory($template)
    $null = [IO.Directory]::CreateDirectory($hooks)
    $null = [IO.Directory]::CreateDirectory($primary)
    $null = [IO.Directory]::CreateDirectory((Join-Path $primary 'eng'))
    $null = [IO.Directory]::CreateDirectory((Join-Path $primary '.wt'))
    '# fixture state is ignored' | Set-Content -LiteralPath (Join-Path $primary '.gitignore') -Encoding ascii
    '.harness/' | Add-Content -LiteralPath (Join-Path $primary '.gitignore') -Encoding ascii
    '.harness-cache/' | Add-Content -LiteralPath (Join-Path $primary '.gitignore') -Encoding ascii
    '.wt/' | Add-Content -LiteralPath (Join-Path $primary '.gitignore') -Encoding ascii
    Copy-FixtureHarnessWithoutUnixPipeSetup (Join-Path $primary 'harness-env.ps1')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'harness-env.sh') -Destination $primary
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'eng/release.ps1') -Destination (Join-Path $primary 'eng/release.ps1')
    Copy-Item -LiteralPath $helper -Destination (Join-Path $primary 'eng/Project-Root.ps1')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'eng/project-root.py') -Destination (Join-Path $primary 'eng/project-root.py')
    $manifest | Set-Content -LiteralPath (Join-Path $primary 'eng/release-targets.json') -Encoding utf8
    $null = Invoke-FixtureGit '' @('init', '--quiet', '--template', $template, $primary)
    $null = Invoke-FixtureGit $primary @('config', 'user.name', 'Cache Root Fixture')
    $null = Invoke-FixtureGit $primary @('config', 'user.email', 'cache-root-fixture@example.invalid')
    $null = Invoke-FixtureGit $primary @('config', 'remote.origin.url', $canonicalOrigin)
    $null = Invoke-FixtureGit $primary @('add', '.gitignore', 'harness-env.ps1', 'harness-env.sh', 'eng/Project-Root.ps1', 'eng/project-root.py', 'eng/release.ps1', 'eng/release-targets.json')
    $null = Invoke-FixtureGit $primary @('commit', '--quiet', '-m', 'fixture project')
    $null = Invoke-FixtureGit $primary @('worktree', 'add', '--quiet', $linked, 'HEAD')
    $null = Invoke-FixtureGit '' @('clone', '--quiet', '--local', '--no-hardlinks', $primary, $clone)
    $null = Invoke-FixtureGit $clone @('config', 'remote.origin.url', $canonicalOrigin)
    foreach ($gitRoot in @($primary, $clone)) {
        $hookDirectory = Join-Path $gitRoot '.git/hooks'
        Assert (-not (Test-Path -LiteralPath $hookDirectory) -or @(Get-ChildItem -LiteralPath $hookDirectory -Force).Count -eq 0) 'Git fixture installed hooks from a template.'
    }
    $null = [IO.Directory]::CreateDirectory((Join-Path $clone 'eng'))
    $cloneOwnerPath = Join-Path $clone '.request-release-owner.json'
    Write-Owner $cloneOwnerPath
    New-HarnessProbe

    # The canonical helper keeps the primary and its linked worktree on the primary Git common root.
    . $helper
    $env:BUILD_RELEASE_PROJECT_ROOT = $null
    Assert ((Get-HandyCacheProjectRoot $primary) -ceq $primary) 'Primary checkout without a hint did not select itself.'
    Assert ((Get-HandyCacheProjectRoot $linked) -ceq $primary) 'Linked worktree without a hint did not select the primary common root.'
    Assert ((Get-HandyCacheProjectRoot $clone) -ceq $clone) 'Independent clone without a hint did not select itself.'
    Assert ((Get-HandyCacheProjectRoot $primary $primary) -ceq $primary) 'Primary checkout with its own hint changed roots.'
    Assert ((Get-HandyCacheProjectRoot $clone $primary) -ceq $primary) 'Valid independent release clone hint did not select the primary root.'
    if ($PythonExecutable) {
        foreach ($route in @(
            @{ checkout = $primary; default = $primary; hint = ''; expected = $primary; label = 'primary' },
            @{ checkout = $linked; default = $primary; hint = ''; expected = $primary; label = 'linked worktree' },
            @{ checkout = $clone; default = $clone; hint = ''; expected = $clone; label = 'independent clone default' },
            @{ checkout = $clone; default = $clone; hint = $primary; expected = $primary; label = 'independent release clone hint' }
        )) {
            $pythonResult = Invoke-PythonRoot $route.checkout $route.default $route.hint
            Assert ($pythonResult.ExitCode -eq 0 -and $pythonResult.Output -ceq $route.expected) "Python selector failed $($route.label) routing: $($pythonResult.Error)"
        }
    }

    $primaryEnvironment = Read-HarnessEnvironment $primary
    $platform = if ($IsWindows) { 'windows' } elseif ($IsMacOS) { 'macos' } else { 'linux' }
    $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $cacheBase = Join-Path $primary ".harness-cache/$platform-$architecture"
    Assert ($primaryEnvironment.packages -ceq (Join-Path $cacheBase 'packages')) 'Primary NuGet package cache path is wrong.'
    Assert ($primaryEnvironment.scratch -ceq (Join-Path $cacheBase 'scratch')) 'Primary NuGet scratch path is wrong.'
    Assert ($primaryEnvironment.http -ceq (Join-Path $cacheBase 'http')) 'Primary NuGet HTTP cache path is wrong.'
    $packageProof = Join-Path $primaryEnvironment.packages ('cache-root-proof-' + [guid]::NewGuid().ToString('N'))
    $httpProof = Join-Path $primaryEnvironment.http ('cache-root-proof-' + [guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText($packageProof, 'warm package proof')
    [IO.File]::WriteAllText($httpProof, 'warm HTTP proof')

    $linkedEnvironment = Read-HarnessEnvironment $linked
    Assert ($linkedEnvironment.packages -ceq $primaryEnvironment.packages -and $linkedEnvironment.scratch -ceq $primaryEnvironment.scratch -and
        $linkedEnvironment.http -ceq $primaryEnvironment.http) 'Linked worktree did not share the primary immutable cache roots.'

    # Independent clone validates the exact release-target marker and reuses the warm immutable cache.
    $cloneEnvironment = Read-HarnessEnvironment $clone $primary
    Assert ($cloneEnvironment.packages -ceq $primaryEnvironment.packages -and $cloneEnvironment.scratch -ceq $primaryEnvironment.scratch -and
        $cloneEnvironment.http -ceq $primaryEnvironment.http) 'Independent release clone did not share package, scratch and HTTP cache roots.'
    Assert ((Get-Content -LiteralPath $packageProof -Raw) -ceq 'warm package proof') 'Warm package proof was lost.'
    Assert ((Get-Content -LiteralPath $httpProof -Raw) -ceq 'warm HTTP proof') 'Warm HTTP proof was lost.'
    foreach ($name in @('cli', 'plugins', 'temp')) {
        Assert ($cloneEnvironment.$name -cne $primaryEnvironment.$name) "Independent clone shared mutable $name state."
        Assert ($cloneEnvironment.$name.StartsWith((Join-Path $clone '.harness'), [StringComparison]::OrdinalIgnoreCase)) "Clone $name state escaped its checkout."
    }

    # An optional legacy outer marker is accepted only in its exact three-field form.
    $outerOwnerPath = Join-Path $primary '.request-release-owner.json'
    [IO.File]::WriteAllText($outerOwnerPath, '{"owner":"request-release","version":1,"project":"HandyPlayer"}', [Text.UTF8Encoding]::new($false))
    $null = Read-HarnessEnvironment $clone $primary

    # A dirty primary (index and private ref included) remains readable without being rewritten.
    $dirtyPath = Join-Path $primary 'fixture-wip.txt'
    [IO.File]::WriteAllText($dirtyPath, 'staged fixture work')
    $null = Invoke-FixtureGit $primary @('add', 'fixture-wip.txt')
    $null = Invoke-FixtureGit $primary @('update-ref', 'refs/cache-root-fixture/wip', 'HEAD')
    $indexBefore = (Get-FileHash -LiteralPath (Join-Path $primary '.git/index') -Algorithm SHA256).Hash
    $refBefore = Invoke-FixtureGit $primary @('rev-parse', 'refs/cache-root-fixture/wip')
    Assert ((Get-HandyCacheProjectRoot $clone $primary) -ceq $primary) 'Dirty primary was refused as a cache root.'
    $indexAfter = (Get-FileHash -LiteralPath (Join-Path $primary '.git/index') -Algorithm SHA256).Hash
    $refAfter = Invoke-FixtureGit $primary @('rev-parse', 'refs/cache-root-fixture/wip')
    Assert ($indexAfter -ceq $indexBefore -and $refAfter.Output -ceq $refBefore.Output) 'Read-only root resolution changed index or refs.'
    Assert (Test-Path -LiteralPath $dirtyPath) 'Read-only root resolution removed primary WIP.'

    # Invalid metadata and origins fail before creating or changing any harness/cache state.
    $goodOwner = [IO.File]::ReadAllText($cloneOwnerPath)
    $badOwners = @(
        @{ label = 'wrong owner'; extra = @{ owner = 'other' } },
        @{ label = 'array owner'; extra = @{ owner = @('request-release') } },
        @{ label = 'wrong project'; extra = @{ project = 'Other' } },
        @{ label = 'array project'; extra = @{ project = @('HandyPlayer') } },
        @{ label = 'wrong version'; extra = @{ version = 2 } },
        @{ label = 'extra clone marker field'; extra = @{ extra = $true } },
        @{ label = 'undeclared target'; extra = @{ target = 'linux-x64' } },
        @{ label = 'wrong declared target for clone path'; extra = @{ target = 'osx-arm64' } },
        @{ label = 'wrong marker root'; extra = @{ root = $fixtureRoot } }
    )
    foreach ($bad in $badOwners) {
        Write-Owner $cloneOwnerPath -Extra $bad.extra
        Assert-RefusedWithoutWrites $bad.label
    }
    [IO.File]::WriteAllText($cloneOwnerPath, $goodOwner, [Text.UTF8Encoding]::new($false))

    # A malformed optional outer marker is rejected too.
    [IO.File]::WriteAllText($outerOwnerPath, '{"owner":"request-release","version":1,"project":"HandyPlayer","target":"win-x64"}', [Text.UTF8Encoding]::new($false))
    Assert-RefusedWithoutWrites 'extra outer marker field'
    [IO.File]::WriteAllText($outerOwnerPath, '{"owner":"request-release","version":1,"project":"HandyPlayer"}', [Text.UTF8Encoding]::new($false))

    $null = Invoke-FixtureGit $clone @('config', 'remote.origin.url', 'https://github.com/example/HandyPlayer.git')
    Assert-RefusedWithoutWrites 'foreign clone origin'
    if ($PythonExecutable) {
        $pythonResult = Invoke-PythonRoot $clone $clone $primary
        Assert ($pythonResult.ExitCode -ne 0 -and $pythonResult.Error -match '(?i)origin') 'Python selector accepted a foreign clone origin.'
    }
    $null = Invoke-FixtureGit $clone @('config', 'remote.origin.url', $canonicalOrigin)
    $credentialOrigin = 'https://fixture-token@github.com/Belgian-Coder/HandyPlayer.git'
    $null = Invoke-FixtureGit $primary @('config', 'remote.origin.url', $credentialOrigin)
    $null = Invoke-FixtureGit $clone @('config', 'remote.origin.url', $credentialOrigin)
    Assert-RefusedWithoutWrites 'credential-bearing matching origins'
    if ($PythonExecutable) {
        $pythonResult = Invoke-PythonRoot $clone $clone $primary
        Assert ($pythonResult.ExitCode -ne 0 -and $pythonResult.Error -match '(?i)credential') 'Python selector accepted credential-bearing matching origins.'
    }
    $null = Invoke-FixtureGit $primary @('config', 'remote.origin.url', $canonicalOrigin)
    $null = Invoke-FixtureGit $clone @('config', 'remote.origin.url', $canonicalOrigin)

    Assert-RefusedWithoutWrites 'relative hint' $clone '.wt/release-win-x64'
    Assert-RefusedWithoutWrites 'noncanonical hint' $clone ($primary + [IO.Path]::DirectorySeparatorChar + '.')

    # A normal child under the manifest worktree directory is not an owned target path.
    $null = Invoke-FixtureGit '' @('clone', '--quiet', '--local', '--no-hardlinks', $primary, $wrongClone)
    $null = Invoke-FixtureGit $wrongClone @('config', 'remote.origin.url', $canonicalOrigin)
    $null = [IO.Directory]::CreateDirectory((Join-Path $wrongClone 'eng'))
    Write-Owner (Join-Path $wrongClone '.request-release-owner.json')
    Assert-RefusedWithoutWrites 'wrong release-target containment' $wrongClone $primary

    # Missing owner marker and every reparse route are rejected before state changes.
    Move-Item -LiteralPath $cloneOwnerPath -Destination ($cloneOwnerPath + '.saved')
    Assert-RefusedWithoutWrites 'missing clone marker'
    Move-Item -LiteralPath ($cloneOwnerPath + '.saved') -Destination $cloneOwnerPath

    $linkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    $hintLink = Join-Path $fixtureRoot 'primary-link'
    $null = New-Item -ItemType $linkType -Path $hintLink -Target $primary
    Assert-RefusedWithoutWrites 'reparse hint root' $clone $hintLink
    $cloneLink = Join-Path $primary '.wt/release-link'
    $null = New-Item -ItemType $linkType -Path $cloneLink -Target $clone
    Assert-RefusedWithoutWrites 'reparse clone root' $cloneLink $primary
    $markerTarget = Join-Path $fixtureRoot 'marker-target'
    $null = [IO.Directory]::CreateDirectory($markerTarget)
    [IO.File]::WriteAllText((Join-Path $markerTarget 'marker.json'), $goodOwner)
    Move-Item -LiteralPath $cloneOwnerPath -Destination ($cloneOwnerPath + '.saved')
    $null = New-Item -ItemType $linkType -Path $cloneOwnerPath -Target $markerTarget
    Assert-RefusedWithoutWrites 'reparse clone marker'
    Remove-Item -LiteralPath $cloneOwnerPath
    Move-Item -LiteralPath ($cloneOwnerPath + '.saved') -Destination $cloneOwnerPath

    # Check a linked ancestor independently from the root itself.
    $linkedAncestor = Join-Path $fixtureRoot 'linked-ancestor'
    $null = New-Item -ItemType $linkType -Path $linkedAncestor -Target $fixtureRoot
    Assert-RefusedWithoutWrites 'reparse ancestor of hint root' $clone (Join-Path $linkedAncestor 'primary')

    # The actual release adapter must reject a bad hint before its source/host gates or any output/lock/runtime write.
    $adapterOutput = Join-Path $clone '.harness/fresh-adapter-output'
    $adapterLock = Join-Path $clone '.harness/release.lock'
    Assert (-not (Test-Path -LiteralPath $adapterOutput) -and -not (Test-Path -LiteralPath $adapterLock)) 'Adapter fixture unexpectedly has prior output or a release lock.'
    $head = (Invoke-FixtureGit $clone @('rev-parse', 'HEAD')).Output
    $adapterStart = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $adapterStart.UseShellExecute = $false
    $adapterStart.CreateNoWindow = $true
    $adapterStart.RedirectStandardOutput = $true
    $adapterStart.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', (Join-Path $clone 'eng/release.ps1'),
        '-Target', 'win-x64', '-Mode', 'validate', '-ExpectedSha', $head, '-OutputDirectory', $adapterOutput)) {
        $adapterStart.ArgumentList.Add($argument)
    }
    $adapterStart.Environment['BUILD_RELEASE_PROJECT_ROOT'] = $primary + [IO.Path]::DirectorySeparatorChar + '.'
    $adapterBefore = Get-StateSnapshot $clone
    $adapterProcess = [Diagnostics.Process]::Start($adapterStart)
    $adapterStdout = $adapterProcess.StandardOutput.ReadToEnd()
    $adapterStderr = $adapterProcess.StandardError.ReadToEnd()
    $adapterProcess.WaitForExit()
    $adapterExit = $adapterProcess.ExitCode
    $adapterProcess.Dispose()
    Assert ($adapterExit -ne 0) 'Release adapter accepted a noncanonical cache-root hint.'
    Assert (($adapterStdout + $adapterStderr) -match '(?i)(hint|project root|canonical)') 'Release adapter failed before reporting the invalid project-root hint.'
    Assert ((Get-StateSnapshot $clone) -ceq $adapterBefore) 'Release adapter changed cache/output/lock/runtime state before refusing the hint.'
    Assert (-not (Test-Path -LiteralPath $adapterOutput) -and -not (Test-Path -LiteralPath $adapterLock)) 'Release adapter created output or a release lock before rejecting the hint.'

    # On Unix hosts, source the real Bash environment in a child with an invalid hint.
    # The root can be too long for the MTP socket fallback, so this assertion exercises the early refusal path only.
    if (-not $IsWindows -and (Get-Command bash -CommandType Application -ErrorAction SilentlyContinue) -and
        (Get-Command python3 -CommandType Application -ErrorAction SilentlyContinue)) {
        $bashBefore = Get-StateSnapshot $clone
        $bashResult = Invoke-BashHarnessProbe $clone ($primary + [IO.Path]::DirectorySeparatorChar + '.')
        Assert ($bashResult.ExitCode -ne 0) 'Bash harness accepted a noncanonical BUILD_RELEASE_PROJECT_ROOT hint.'
        Assert (($bashResult.Output + $bashResult.Error) -match '(?i)(hint|project root|canonical)') 'Bash harness failed without reporting invalid project-root provenance.'
        Assert ((Get-StateSnapshot $clone) -ceq $bashBefore) 'Bash harness changed cache/harness state before refusing the hint.'
    }

    Write-Output "Cache root ownership and pre-write contracts passed. Fixture retained: $fixtureRoot"
} finally {
    $env:BUILD_RELEASE_PROJECT_ROOT = $oldHint
    Set-Location -LiteralPath $oldLocation
    $ErrorActionPreference = $oldErrorPreference
}
