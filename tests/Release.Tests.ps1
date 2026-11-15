#Requires -Version 7
# SDK-free adapter checks. Git source checks and canonical tools are mocked;
# output containment, receipts, locks, manifests and hashes use real local files.
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path $PSScriptRoot -Parent
$root = $sourceRoot
$adapterSource = Join-Path $sourceRoot 'eng/release.ps1'
$global:ReleaseRealGit = (Get-Command git -CommandType Application | Select-Object -First 1).Source
$global:ReleaseOriginalRoot = $sourceRoot
$global:ReleaseSha = '1' * 40
$global:ReleaseDirty = $false
$global:ReleaseFailure = $false
$global:ReleasePublisherProfile = $null
$global:ReleasePublisherCompression = $null
$global:ReleaseEnvironmentHost = [Environment]::MachineName
$global:ReleaseEnvironmentSdk = '11.0.100'
$global:ReleaseEnvironmentNativeSha = 'a' * 64
$global:ReleaseEnvironmentNativeLockSha = 'b' * 64
$global:ReleaseEnvironmentJobToken = 'validation-job'
$global:ReleaseChangeEnvironmentDuringValidation = $false
$global:ReleaseChangeEnvironmentDuringPackaging = $false
$global:ReleaseGitCalls = 0
$global:ReleaseCalls = [Collections.Generic.List[object]]::new()
$fixture = Join-Path $root ('.harness/release-contracts/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$root = $fixture
$global:ReleaseFixtureRoot = $root
$adapter = Join-Path $root 'eng/release.ps1'
[IO.Directory]::CreateDirectory((Join-Path $root 'eng')) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $root '.harness')) | Out-Null
Copy-Item -LiteralPath $adapterSource -Destination $adapter
Copy-Item -LiteralPath (Join-Path $sourceRoot 'harness-env.ps1') -Destination (Join-Path $root 'harness-env.ps1')
$adapterText = Get-Content -LiteralPath $adapter -Raw
$adapterTokens = $null
$adapterErrors = $null
$adapterAst = [Management.Automation.Language.Parser]::ParseInput($adapterText, [ref]$adapterTokens, [ref]$adapterErrors)
if ($adapterErrors) { throw "Release adapter source did not parse: $($adapterErrors[0].Message)" }
$identityFunction = $adapterAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ReleaseEnvironmentIdentity' }, $true)
if (-not $identityFunction) { throw 'Release adapter environment identity provider was not found.' }
$mockIdentityFunction = @'
function Get-ReleaseEnvironmentIdentity {
    param([string]$RepositoryRoot, [string]$ReleaseTarget)
    [pscustomobject]@{
        schemaVersion = 2
        hostName = $global:ReleaseEnvironmentHost
        nativeOS = $global:ReleaseEnvironmentOs
        nativeArchitecture = $global:ReleaseEnvironmentArchitecture
        processArchitecture = $global:ReleaseEnvironmentArchitecture
        dotnetSdkVersion = $global:ReleaseEnvironmentSdk
        validationRuntime = 'offline-unit-headless-ui'
        nativeMedia = 'not-exercised'
        packagingNativeInput = [pscustomobject]@{
            kind = 'test-native-file'; name = 'libmpv'; version = 'fixture'
            source = 'test-fixture'; path = 'native/libmpv.fixture'
            bytes = 128; sha256 = $global:ReleaseEnvironmentNativeSha
            lockFileSha256 = $global:ReleaseEnvironmentNativeLockSha
        }
    }
}
'@
$adapterText = $adapterText.Substring(0, $identityFunction.Extent.StartOffset) + $mockIdentityFunction + $adapterText.Substring($identityFunction.Extent.EndOffset)
Set-Content -LiteralPath $adapter -Value $adapterText -Encoding utf8
$target = if ($IsWindows) { 'win-x64' } elseif ($IsMacOS) { 'osx-arm64' } else { 'linux-x64' }
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Must-Fail([scriptblock]$Action, [string]$Message, [string]$ExpectedPattern = '*') {
    $failureObserved = $false
    try { & $Action | Out-Null } catch { $failureObserved = $true; $global:ReleaseLastError = $_.Exception.Message }
    Assert $failureObserved $Message
    Assert ($global:ReleaseLastError -like $ExpectedPattern) "$Message Unexpected refusal: $global:ReleaseLastError"
}
$targetParts = $target.Split('-')
$global:ReleaseEnvironmentOs = $targetParts[0]
$global:ReleaseEnvironmentArchitecture = $targetParts[1]
function global:git {
    $global:ReleaseGitCalls++
    if ($args[0] -eq 'rev-parse' -and $args[1] -eq 'HEAD') {
        $global:LASTEXITCODE = 0
        return $global:ReleaseSha
    }
    if ($args[0] -eq 'status') {
        $global:LASTEXITCODE = 0
        if ($global:ReleaseDirty) { return ' M preserve-wip.txt' }
        return
    }
    $realArgs = @($args)
    if ($realArgs.Count -ge 3 -and $realArgs[0] -eq '-C' -and
        ([string]$realArgs[1]).StartsWith($global:ReleaseFixtureRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if ($realArgs -contains 'check-ignore' -and ($realArgs[-1] -like '*unignored-release-output*')) {
            $global:LASTEXITCODE = 1
            return
        }
        $realArgs[1] = $global:ReleaseOriginalRoot
    }
    & $global:ReleaseRealGit @realArgs
}
function global:pwsh {
    $global:ReleaseCalls.Add(@($args))
    if ($global:ReleaseFailure) { $global:LASTEXITCODE = 19; return }
    if (($args -contains './validate.ps1') -and $global:ReleaseChangeEnvironmentDuringValidation) {
        $global:ReleaseEnvironmentSdk = '11.0.101'
    }
    if ($args -contains './publish-win.ps1') {
        $destination = New-HandyOutputRoot $env:HANDY_RELEASE_OUTPUT
        foreach ($name in @('HandyPlayer-test-win-x64.zip', 'HandyPlayer-test-win-x64.msi', 'SHA256SUMS-win-x64.txt', 'native-dependency.json')) {
            [IO.File]::WriteAllText((Join-Path $destination $name), 'mock package; not application output')
        }
        $profileIndex = [Array]::IndexOf($args, '-PackageProfile')
        $profile = $args[$profileIndex + 1]
        $resultProfile = if ($global:ReleasePublisherProfile) { $global:ReleasePublisherProfile } else { $profile }
        $metadata = @{ schemaVersion=1; packageProfile=$resultProfile; requiredMsi=$true
            msiCompression=$(if($global:ReleasePublisherCompression){$global:ReleasePublisherCompression}elseif($resultProfile -eq 'Candidate'){'medium'}else{'high'})
            innoStatus=$(if($resultProfile -eq 'Candidate'){'skipped-profile'}else{'unavailable'})
            stageSeconds=@{nativeRestore=0.1;publish=0.1;portable=0.1;zip=0.1;msi=0.1;inno=0;checksums=0.1} }
        Write-Output ('HANDY_PACKAGE_RESULT ' + ($metadata | ConvertTo-Json -Compress -Depth 4))
        if ($global:ReleaseChangeEnvironmentDuringPackaging) { $global:ReleaseEnvironmentSdk = '11.0.101' }
    }
    $global:LASTEXITCODE = 0
}
# macOS/Linux publishing uses bash; intercept it without invoking native tooling.
function global:bash {
    $global:ReleaseCalls.Add(@($args))
    $destination = New-HandyOutputRoot $env:HANDY_RELEASE_OUTPUT
    $names = if ($args[0] -eq './publish-mac.sh') {
        @('HandyPlayer-test-mac-arm64.zip', 'HandyPlayer-test-mac-arm64.dmg')
    } else { @('HandyPlayer-test-linux-x64.tar.gz', 'handyplayer-test-linux-x64.deb') }
    foreach ($name in $names) { [IO.File]::WriteAllText((Join-Path $destination $name), 'mock package; not application output') }
    if ($global:ReleaseChangeEnvironmentDuringPackaging) { $global:ReleaseEnvironmentSdk = '11.0.101' }
    $global:LASTEXITCODE = 0
}
try {
    $planOutput = Join-Path $fixture 'plan'
    & $adapter -Target $target -Mode plan -ExpectedSha $global:ReleaseSha -OutputDirectory $planOutput | ConvertFrom-Json | Out-Null
    Assert ($global:ReleaseGitCalls -eq 0 -and $global:ReleaseCalls.Count -eq 0) 'Plan invoked an external tool.'
    Assert (-not (Test-Path -LiteralPath $planOutput)) 'Plan created output.'

    $wrongSha = Join-Path $fixture 'wrong-sha'
    Must-Fail { & $adapter -Target $target -Mode validate -ExpectedSha ('2' * 40) -OutputDirectory $wrongSha } 'SHA mismatch accepted.' '*HEAD*'
    $global:ReleaseDirty = $true
    Must-Fail { & $adapter -Target $target -Mode package -ExpectedSha $global:ReleaseSha -OutputDirectory $wrongSha } 'Dirty source accepted.' '*clean committed checkout*'
    $global:ReleaseDirty = $false
    Assert (-not (Test-Path -LiteralPath $wrongSha)) 'Source refusal created output.'

    $wrongHost = Join-Path $fixture 'wrong-host'
    $otherTarget = if ($IsWindows) { 'osx-arm64' } else { 'win-x64' }
    Must-Fail { & $adapter -Target $otherTarget -Mode validate -ExpectedSha $global:ReleaseSha -OutputDirectory $wrongHost } 'Wrong native host accepted.' '*matching native host*'
    Assert (-not (Test-Path -LiteralPath $wrongHost)) 'Host refusal created output.'

    $candidate = Join-Path $fixture 'candidate'
    & $adapter -Target $target -Mode validate -ExpectedSha $global:ReleaseSha -OutputDirectory $candidate | Out-Null
    Assert ($global:ReleaseCalls.Count -eq 1) 'Validation did not invoke exactly one canonical gate.'
    & $adapter -Target $target -Mode package -ExpectedSha $global:ReleaseSha -OutputDirectory $candidate | Out-Null
    Assert ($global:ReleaseCalls.Count -eq 2) 'Packaging repeated the validated same-request gate.'
    $manifest = Get-Content -LiteralPath (Join-Path $candidate 'artifact-manifest.json') -Raw | ConvertFrom-Json
    Assert ($manifest.status -eq 'candidate' -and $manifest.sourceSha -eq $global:ReleaseSha) 'Candidate lost its exact source identity.'
    foreach ($artifact in $manifest.artifacts) {
        Assert ((Get-FileHash -LiteralPath (Join-Path $root $artifact.path) -Algorithm SHA256).Hash -eq $artifact.sha256) 'Manifest artifact hash is wrong.'
    }
    $global:ReleaseEnvironmentJobToken = 'package-job'
    & $adapter -Target $target -Mode package -ExpectedSha $global:ReleaseSha -OutputDirectory (Join-Path $fixture 'unchanged-environment') | Out-Null
    Assert ($global:ReleaseCalls.Count -eq 4) 'An unchanged native environment must allow a separate package job.'
    $global:ReleaseEnvironmentJobToken = 'validation-job'
    Must-Fail { & $adapter -Target $target -Mode package -ExpectedSha $global:ReleaseSha -OutputDirectory $candidate } 'Existing candidate output was reused.' '*already has a package manifest*'
    [IO.File]::AppendAllText((Join-Path $root $manifest.artifacts[0].path), 'tampered')
    Must-Fail { & $adapter -Target $target -Mode upload -ExpectedSha $global:ReleaseSha -OutputDirectory $candidate } 'Changed candidate was accepted for upload.' '*Candidate artifact changed*'
    Assert ($global:ReleaseCalls.Count -eq 4) 'Refusal invoked a canonical gate.'

    $failed = Join-Path $fixture 'failed'
    $global:ReleaseFailure = $true
    Must-Fail { & $adapter -Target $target -Mode validate -ExpectedSha $global:ReleaseSha -OutputDirectory $failed } 'Canonical failure did not propagate.' '*exit code 19*'
    $global:ReleaseFailure = $false
    Assert (-not (Test-Path -LiteralPath (Join-Path $failed 'validation.json'))) 'Failed validation wrote a passing receipt.'
    Assert (Test-Path -LiteralPath (Join-Path $failed 'validation.log')) "Failed run evidence was not retained: $global:ReleaseLastError"
    Must-Fail { & $adapter -Target $target -Mode validate -ExpectedSha $global:ReleaseSha -OutputDirectory $failed } 'Failed-run logs were overwritten.' '*Retain the existing validation.log*'

    $mismatch = Join-Path $fixture 'receipt-mismatch'
    & $adapter -Target $target -Mode validate -ExpectedSha $global:ReleaseSha -OutputDirectory $mismatch | Out-Null
    $mismatchPath = Join-Path $mismatch 'validation.json'
    $mismatchReceipt = Get-Content -LiteralPath $mismatchPath -Raw | ConvertFrom-Json
    $mismatchReceipt.sourceSha = '2' * 40
    $mismatchReceipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $mismatchPath
    $callCount = $global:ReleaseCalls.Count
    Must-Fail { & $adapter -Target $target -Mode package -ExpectedSha $global:ReleaseSha -OutputDirectory $mismatch } 'Mismatched receipt accepted.' '*receipt does not match*'
    Assert ($global:ReleaseCalls.Count -eq $callCount) 'Receipt refusal invoked packaging.'

    $direct = Join-Path $fixture 'direct-package'
    & $adapter -Target $target -Mode package -ExpectedSha $global:ReleaseSha -OutputDirectory $direct | Out-Null
    Assert ($global:ReleaseCalls.Count -eq ($callCount + 2)) 'Direct package must validate once before canonical packaging.'

    foreach ($mutation in @('host', 'sdk', 'native-pin')) {
        $environmentOutput = Join-Path $fixture "environment-$mutation"
        & $adapter -Target $target -Mode validate -ExpectedSha $global:ReleaseSha -OutputDirectory $environmentOutput | Out-Null
        $savedIdentity = @{
            host = $global:ReleaseEnvironmentHost; sdk = $global:ReleaseEnvironmentSdk
            nativeSha = $global:ReleaseEnvironmentNativeSha; lockSha = $global:ReleaseEnvironmentNativeLockSha
        }
        switch ($mutation) {
            'host' { $global:ReleaseEnvironmentHost = 'different-release-host' }
            'sdk' { $global:ReleaseEnvironmentSdk = '11.0.101' }
            'native-pin' {
                $global:ReleaseEnvironmentNativeSha = 'c' * 64
                $global:ReleaseEnvironmentNativeLockSha = 'd' * 64
            }
        }
        $beforeRefusalCalls = $global:ReleaseCalls.Count
        Must-Fail { & $adapter -Target $target -Mode package -ExpectedSha $global:ReleaseSha -OutputDirectory $environmentOutput } "$mutation environment change reused a validation receipt." '*Native environment identity changed*'
        Assert ($global:ReleaseCalls.Count -eq $beforeRefusalCalls) "$mutation environment refusal invoked packaging."
        $global:ReleaseEnvironmentHost = $savedIdentity.host
        $global:ReleaseEnvironmentSdk = $savedIdentity.sdk
        $global:ReleaseEnvironmentNativeSha = $savedIdentity.nativeSha
        $global:ReleaseEnvironmentNativeLockSha = $savedIdentity.lockSha
    }

    $legacyEnvironment = Join-Path $fixture 'receipt-without-environment'
    & $adapter -Target $target -Mode validate -ExpectedSha $global:ReleaseSha -OutputDirectory $legacyEnvironment | Out-Null
    $legacyEnvironmentPath = Join-Path $legacyEnvironment 'validation.json'
    $legacyEnvironmentReceipt = Get-Content -LiteralPath $legacyEnvironmentPath -Raw | ConvertFrom-Json
    $legacyEnvironmentReceipt.PSObject.Properties.Remove('environment')
    $legacyEnvironmentReceipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $legacyEnvironmentPath
    Must-Fail { & $adapter -Target $target -Mode package -ExpectedSha $global:ReleaseSha -OutputDirectory $legacyEnvironment } 'Receipt without a native environment identity was reused.' '*no bound native environment identity*'

    $legacyEnvironmentSchema = Join-Path $fixture 'receipt-with-legacy-environment-schema'
    & $adapter -Target $target -Mode validate -ExpectedSha $global:ReleaseSha -OutputDirectory $legacyEnvironmentSchema | Out-Null
    $legacySchemaPath = Join-Path $legacyEnvironmentSchema 'validation.json'
    $legacySchemaReceipt = Get-Content -LiteralPath $legacySchemaPath -Raw | ConvertFrom-Json
    $legacySchemaReceipt.environment.schemaVersion = 1
    $legacySchemaReceipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $legacySchemaPath
    Must-Fail { & $adapter -Target $target -Mode package -ExpectedSha $global:ReleaseSha -OutputDirectory $legacyEnvironmentSchema } 'Legacy environment identity schema was reused.' '*no bound native environment identity*'

    $changedDuringValidation = Join-Path $fixture 'environment-changed-during-validation'
    $global:ReleaseChangeEnvironmentDuringValidation = $true
    Must-Fail { & $adapter -Target $target -Mode validate -ExpectedSha $global:ReleaseSha -OutputDirectory $changedDuringValidation } 'Environment mutation during validation wrote a passing receipt.' '*Native environment identity changed*'
    $global:ReleaseChangeEnvironmentDuringValidation = $false
    $global:ReleaseEnvironmentSdk = '11.0.100'
    Assert (-not (Test-Path -LiteralPath (Join-Path $changedDuringValidation 'validation.json'))) 'Environment change during validation wrote a receipt.'

    $changedDuringPackaging = Join-Path $fixture 'environment-changed-during-packaging'
    & $adapter -Target $target -Mode validate -ExpectedSha $global:ReleaseSha -OutputDirectory $changedDuringPackaging | Out-Null
    $global:ReleaseChangeEnvironmentDuringPackaging = $true
    Must-Fail { & $adapter -Target $target -Mode package -ExpectedSha $global:ReleaseSha -OutputDirectory $changedDuringPackaging } 'Environment mutation during packaging wrote a candidate manifest.' '*Native environment identity changed*'
    $global:ReleaseChangeEnvironmentDuringPackaging = $false
    $global:ReleaseEnvironmentSdk = '11.0.100'
    Assert (-not (Test-Path -LiteralPath (Join-Path $changedDuringPackaging 'artifact-manifest.json'))) 'Environment change during packaging wrote a candidate manifest.'

    if ($IsWindows) {
        $profileMismatch = Join-Path $fixture 'profile-mismatch'
        & $adapter -Target $target -Mode validate -ExpectedSha $global:ReleaseSha -OutputDirectory $profileMismatch -PackageProfile Full | Out-Null
        $profileCallCount = $global:ReleaseCalls.Count
        Must-Fail { & $adapter -Target $target -Mode package -ExpectedSha $global:ReleaseSha -OutputDirectory $profileMismatch -PackageProfile Candidate } 'Wrong validation profile accepted.' '*Validation receipt packaging profile*'
        Assert ($global:ReleaseCalls.Count -eq $profileCallCount) 'Wrong profile invoked packaging.'
        $legacyReceiptPath = Join-Path $profileMismatch 'validation.json'
        $legacyReceipt = Get-Content -LiteralPath $legacyReceiptPath -Raw | ConvertFrom-Json
        $legacyReceipt.PSObject.Properties.Remove('packageProfile')
        $legacyReceipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $legacyReceiptPath
        Must-Fail { & $adapter -Target $target -Mode package -ExpectedSha $global:ReleaseSha -OutputDirectory $profileMismatch -PackageProfile Full } 'Legacy unbound receipt accepted.' '*Validation receipt packaging profile*'
        $candidateProfile = Join-Path $fixture 'candidate-profile'
        & $adapter -Target $target -Mode package -ExpectedSha $global:ReleaseSha -OutputDirectory $candidateProfile -PackageProfile Candidate | Out-Null
        $candidateManifestPath = Join-Path $candidateProfile 'artifact-manifest.json'
        $candidateManifest = Get-Content -LiteralPath $candidateManifestPath -Raw | ConvertFrom-Json
        Assert ($candidateManifest.packageProfile -ceq 'Candidate' -and $candidateManifest.packaging.innoStatus -ceq 'skipped-profile') 'Candidate profile/timings were not recorded.'
        Assert (@($candidateManifest.artifacts | Where-Object path -Like '*setup.exe').Count -eq 0) 'Candidate included an optional EXE.'
        $candidateManifest.packageProfile = 'Full'
        $candidateManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $candidateManifestPath
        Must-Fail { & $adapter -Target $target -Mode upload -ExpectedSha $global:ReleaseSha -OutputDirectory $candidateProfile -PackageProfile Candidate } 'Wrong candidate manifest profile accepted.' '*Candidate manifest packaging profile*'
        $badPublisher = Join-Path $fixture 'bad-publisher-profile'
        $global:ReleasePublisherProfile = 'Full'
        Must-Fail { & $adapter -Target $target -Mode package -ExpectedSha $global:ReleaseSha -OutputDirectory $badPublisher -PackageProfile Candidate } 'Wrong publisher profile accepted.' '*Publisher result does not match*'
        $global:ReleasePublisherProfile = $null
        $global:ReleasePublisherCompression = 'high'
        Must-Fail { & $adapter -Target $target -Mode package -ExpectedSha $global:ReleaseSha -OutputDirectory (Join-Path $fixture 'bad-publisher-compression') -PackageProfile Candidate } 'Wrong publisher compression accepted.' '*Publisher MSI compression*'
        $global:ReleasePublisherCompression = $null
    }

    Must-Fail { New-HandyOutputRoot (Join-Path (Split-Path $root -Parent) 'escaped-release-output') } 'Checkout escape accepted.'
    Must-Fail { New-HandyOutputRoot (Join-Path $root 'unignored-release-output') } 'Unignored output accepted.'
    Must-Fail { New-HandyOutputRoot $candidate } 'Existing output accepted by publisher.'
    $linkTarget = Join-Path $fixture 'link-target'
    [IO.Directory]::CreateDirectory($linkTarget) | Out-Null
    $link = Join-Path $fixture 'link'
    $linkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $linkType -Path $link -Target $linkTarget | Out-Null
    Must-Fail { New-HandyOutputRoot (Join-Path $link 'unsafe') } 'Redirected output ancestor accepted.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $linkTarget 'unsafe'))) 'Redirected output was written.'
    Write-Output 'Release adapter plan, source/host refusals, receipt reuse, hashes, failure evidence and output guards passed (mocked tools).'
} finally {
    Remove-Item Function:global:git, Function:global:pwsh, Function:global:bash
}
