#Requires -Version 7
# Offline contract tests: no SDK, gh process, credentials or network.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'eng/Publish-Draft.ps1')
$fixtureRoot = Join-Path $root ('.harness/draft-tests-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixtureRoot
$script:sha = '0123456789abcdef0123456789abcdef01234567'
$script:tag = "ci-test-win-x64-full-$script:sha"
$script:calls = [Collections.Generic.List[object]]::new()
$script:payloads = @{}
$script:release = $null
$script:failure = ''
$script:tagType = 'commit'
$script:tagSha = $script:sha
$script:hasTag = $false
$script:publishAfterUpload = $false
$script:noDigest = $false
$script:remoteCommitSha = $script:sha
$script:corruptDownload = $false
$script:waits = [Collections.Generic.List[int]]::new()

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_ }
    Assert ($null -ne $caught) "Expected failure matching '$Pattern'."
    Assert ($caught.Exception.Message -like $Pattern) "Wrong failure: $($caught.Exception.Message)"
    Assert ($caught.Exception.Message -notlike '*mock-sensitive-output*') 'CLI failure leaked raw output.'
}
function New-Request([string]$Name) {
    $directory = Join-Path $fixtureRoot $Name
    $null = New-Item -ItemType Directory -Path $directory
    return $directory
}
function Invoke-Upload([string]$Directory) {
    Invoke-HandyDraftUpload -Repository 'Belgian-Coder/HandyPlayer' -SourceSha $script:sha -Target win-x64 `
        -Artifacts @($script:artifact) -OutputDirectory $Directory -NativeAcceptance 'not performed'
}
function Reset-Mock {
    $script:calls.Clear()
    $script:payloads = @{}
    $script:release = $null
    $script:failure = ''
    $script:hasTag = $false
    $script:tagType = 'commit'
    $script:tagSha = $script:sha
    $script:publishAfterUpload = $false
    $script:noDigest = $false
    $script:remoteCommitSha = $script:sha
    $script:corruptDownload = $false
    $script:waits.Clear()
    $script:created = $false
    $script:visibilityMisses = 0
    $script:postCreateReads = 0
    $script:postCreateFailure = ''
    $global:LASTEXITCODE = 0
}
function Start-Sleep {
    param([int]$Milliseconds)
    $script:waits.Add($Milliseconds)
}
function New-MockRelease {
    return [pscustomobject]@{
        id = 123; tag_name = $script:tag; target_commitish = $script:sha; draft = $true
        html_url = 'https://github.com/Belgian-Coder/HandyPlayer/releases/tag/' + $script:tag
        assets = @()
    }
}
function gh {
    # This function shadows the executable for every library invocation.
    $argv = @($args)
    $script:calls.Add($argv)
    $global:LASTEXITCODE = 0
    $operation = if ($argv[0] -eq 'api') { 'api' } else { $argv[1] }
    if ($operation -eq $script:failure) {
        $global:LASTEXITCODE = 17
        return 'mock-sensitive-output'
    }
    if ($argv[0] -eq 'api') {
        Assert ($argv[1] -eq '--hostname' -and $argv[2] -eq 'github.com') 'API hostname must be explicit.'
        Assert ($argv[3] -eq '--method' -and $argv[4] -eq 'GET') 'API operations must be read-only.'
        $endpoint = $argv[5]
        if ($endpoint -eq "repos/Belgian-Coder/HandyPlayer/git/commits/$script:sha") {
            return (@{ sha = $script:remoteCommitSha } | ConvertTo-Json -Compress)
        }
        if ($endpoint -eq "repos/Belgian-Coder/HandyPlayer/git/matching-refs/tags/$script:tag") {
            if ($script:hasTag) {
                return ConvertTo-Json -InputObject @(@{ ref = "refs/tags/$script:tag"; object = @{
                    type = $script:tagType; sha = $script:tagSha
                } }) -Depth 8 -Compress
            }
            return '[]'
        }
        if ($endpoint -eq 'repos/Belgian-Coder/HandyPlayer/releases?per_page=100') {
            Assert ($argv -contains '--paginate' -and $argv -contains '--slurp') 'Draft lookup must paginate.'
            if ($script:created) {
                $script:postCreateReads++
                if ($script:postCreateFailure -eq 'api') {
                    $global:LASTEXITCODE = 17
                    return 'mock-sensitive-output'
                }
                if ($script:postCreateReads -le $script:visibilityMisses) { return '[[]]' }
            }
            $pages = [object[]]::new(1)
            $pages[0] = if ($null -eq $script:release) { @() } else { @($script:release) }
            return ConvertTo-Json -InputObject $pages -Depth 30 -Compress
        }
        throw "Unexpected mock endpoint: $endpoint"
    }
    Assert ($argv[0] -eq 'release' -and $argv[2] -eq $script:tag) 'Unexpected release/tag operation.'
    Assert ($argv -contains '--repo' -and $argv -contains 'github.com/Belgian-Coder/HandyPlayer') 'Destination must be explicit.'
    Assert ($argv -notcontains '--clobber') 'Asset overwrite must never be requested.'
    switch ($argv[1]) {
        'create' {
            Assert ($null -eq $script:release) 'Existing release must not be recreated.'
            Assert ($argv -contains '--draft') 'Create must always specify draft.'
            $targetIndex = [Array]::IndexOf($argv, '--target')
            Assert ($targetIndex -ge 0 -and $argv[$targetIndex + 1] -ceq $script:sha) 'Creation must target full source SHA.'
            $notesIndex = [Array]::IndexOf($argv, '--notes-file')
            Assert ($notesIndex -ge 0 -and (Test-Path -LiteralPath $argv[$notesIndex + 1])) 'Deterministic notes file missing.'
            Assert ($argv -notcontains '--generate-notes') 'Generated release notes are forbidden.'
            $script:release = New-MockRelease
            $script:created = $true
            switch ($script:postCreateFailure) {
                'non-draft' { $script:release.draft = $false }
                'wrong-source' { $script:release.target_commitish = 'main' }
                'wrong-asset' {
                    $script:release.assets = @([pscustomobject]@{
                        name = [IO.Path]::GetFileName($script:artifact.path); size = $script:artifact.size
                        state = 'uploaded'; digest = 'sha256:' + ('f' * 64)
                    })
                }
            }
            return 'https://github.com/premature-url-must-be-captured'
        }
        'upload' {
            $path = $argv[-1]
            $name = [IO.Path]::GetFileName($path)
            Assert ($name -cnotin @($script:release.assets | ForEach-Object { $_.name })) 'Duplicate assets must not be uploaded.'
            $bytes = [IO.File]::ReadAllBytes($path)
            $script:payloads[$name] = $bytes
            $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            $script:release.assets += [pscustomobject]@{
                name = $name; size = [long]$bytes.Length; state = 'uploaded'
                digest = $(if ($script:noDigest) { $null } else { "sha256:$hash" })
            }
            if ($script:publishAfterUpload) { $script:release.draft = $false }
            return 'mock-upload-output'
        }
        'download' {
            $patternIndex = [Array]::IndexOf($argv, '--pattern')
            $dirIndex = [Array]::IndexOf($argv, '--dir')
            $name = $argv[$patternIndex + 1]
            $directory = $argv[$dirIndex + 1]
            Assert ([IO.Path]::GetFullPath($directory).StartsWith($fixtureRoot)) 'Verification download escaped owned fixture.'
            Assert (@(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) 'Downloads must use fresh directories.'
            $bytes = [byte[]]$script:payloads[$name].Clone()
            if ($script:corruptDownload) { $bytes[0] = $bytes[0] -bxor 1 }
            [IO.File]::WriteAllBytes((Join-Path $directory $name), $bytes)
            return ''
        }
        default { throw "Unexpected mock operation: $($argv[1])" }
    }
}

try {
    $artifactPath = Join-Path $fixtureRoot 'HandyPlayer-ci-test-win-x64.zip'
    [IO.File]::WriteAllText($artifactPath, 'validated package fixture')
    $script:artifact = [pscustomobject]@{
        path = $artifactPath; size = [long](Get-Item -LiteralPath $artifactPath).Length
        sha256 = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    Reset-Mock
    $createdDirectory = New-Request 'created'
    $result = @(Invoke-Upload $createdDirectory)
    Assert ($result.Count -eq 1 -and $result[0].tag -ceq $script:tag) 'Uploader must return one verified result.'
    Assert ($result[0].artifacts.Count -eq 2) 'Package plus portable manifest must be uploaded.'
    Assert (@($script:calls | Where-Object { $_[0] -eq 'release' -and $_[1] -eq 'create' }).Count -eq 1) 'Draft was not created exactly once.'
    Assert (@($script:calls | Where-Object { $_[0] -eq 'release' -and $_[1] -eq 'upload' }).Count -eq 2) 'Missing assets were not uploaded exactly once.'
    Assert (Test-Path -LiteralPath (Join-Path $createdDirectory 'draft-upload.json')) 'Verified receipt missing.'
    $manifestText = [IO.File]::ReadAllText((Join-Path $createdDirectory 'draft-manifest.json'))
    Assert (-not $manifestText.Contains($fixtureRoot)) 'Portable manifest leaked an absolute path.'
    $manifest = $manifestText | ConvertFrom-Json
    Assert ($manifest.sourceSha -ceq $script:sha -and $manifest.target -ceq 'win-x64') 'Manifest identity missing.'
    Assert ($manifest.nativeAcceptance -ceq 'not performed') 'Native acceptance attestation missing.'
    Assert ($manifest.packageProfile -ceq 'Full' -and $result[0].packageProfile -ceq 'Full') 'Draft profile binding missing.'
    Assert ($script:waits.Count -eq 0) 'Immediately visible draft must not wait.'

    # Retry both the same request and a fresh request against exact existing assets.
    $script:calls.Clear()
    $retry = Invoke-Upload $createdDirectory
    $fresh = New-Request 'existing'
    $existing = Invoke-Upload $fresh
    Assert ($retry.url -ceq $existing.url) 'Idempotent retry changed release URL.'
    Assert (@($script:calls | Where-Object { $_[0] -eq 'release' -and $_[1] -in @('create', 'upload') }).Count -eq 0) 'Exact existing draft must not be mutated.'
    Assert ($script:waits.Count -eq 0) 'Existing draft must not enter creation visibility waits.'
    Assert ((Get-FileHash -LiteralPath (Join-Path $createdDirectory 'draft-manifest.json')).Hash -ceq
        (Get-FileHash -LiteralPath (Join-Path $fresh 'draft-manifest.json')).Hash) 'Portable manifest is not deterministic.'

    $script:noDigest = $true
    foreach ($asset in $script:release.assets) { $asset.PSObject.Properties.Remove('digest') }
    $script:calls.Clear()
    $null = Invoke-Upload (New-Request 'download-verify')
    Assert (@($script:calls | Where-Object { $_[0] -eq 'release' -and $_[1] -eq 'download' }).Count -ge 2) 'Missing digests must fall back to downloaded byte hashes.'

    $script:corruptDownload = $true
    $script:calls.Clear()
    Assert-Throws { Invoke-Upload (New-Request 'corrupt-download') } '*different SHA-256*'
    Assert (@($script:calls | Where-Object { $_[0] -eq 'release' -and $_[1] -in @('create', 'upload') }).Count -eq 0) 'Downloaded hash mismatch must prevent mutation.'
    $script:corruptDownload = $false
    $script:failure = 'download'
    Assert-Throws { Invoke-Upload (New-Request 'failed-download') } '*GitHub CLI failed (exit 17)*'

    Reset-Mock
    $script:visibilityMisses = 3
    $delayedDirectory = New-Request 'delayed-visibility'
    $null = Invoke-Upload $delayedDirectory
    Assert (($script:waits -join ',') -ceq '500,1000,2000') 'Delayed draft must use only the three bounded waits.'
    Assert (@($script:calls | Where-Object { $_[0] -eq 'release' -and $_[1] -eq 'create' }).Count -eq 1) 'Delayed draft must not be recreated.'
    Assert (@($script:calls | Where-Object { $_[0] -eq 'release' -and $_[1] -eq 'upload' }).Count -eq 2) 'Visible delayed draft must upload each missing asset once.'
    Assert (Test-Path -LiteralPath (Join-Path $delayedDirectory 'draft-upload.json')) 'Delayed visibility success receipt missing.'

    Reset-Mock
    $script:visibilityMisses = 4
    $absentDirectory = New-Request 'bounded-absence'
    Assert-Throws { Invoke-Upload $absentDirectory } '*Draft release*was not found*'
    Assert ($script:postCreateReads -eq 4 -and ($script:waits -join ',') -ceq '500,1000,2000') 'Absent draft lookup exceeded its four-attempt bound.'
    Assert (@($script:calls | Where-Object { $_[0] -eq 'release' -and $_[1] -eq 'create' }).Count -eq 1) 'Absent draft must not be recreated.'
    Assert (@($script:calls | Where-Object { $_[0] -eq 'release' -and $_[1] -eq 'upload' }).Count -eq 0) 'Absent draft must not upload assets.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $absentDirectory 'draft-upload.json'))) 'Absent draft must not write a success receipt.'

    foreach ($failureCase in @(
        @{ kind = 'api'; pattern = '*GitHub CLI failed (exit 17)*' },
        @{ kind = 'non-draft'; pattern = '*is not a draft*' },
        @{ kind = 'wrong-source'; pattern = '*exact source commit*' },
        @{ kind = 'wrong-asset'; pattern = '*different SHA-256*' }
    )) {
        Reset-Mock
        $script:postCreateFailure = $failureCase.kind
        $failedDirectory = New-Request ('post-create-' + $failureCase.kind)
        Assert-Throws { Invoke-Upload $failedDirectory } $failureCase.pattern
        Assert ($script:postCreateReads -eq 1 -and $script:waits.Count -eq 0) 'Post-create error or invalid visible draft must fail immediately.'
        Assert (@($script:calls | Where-Object { $_[0] -eq 'release' -and $_[1] -eq 'create' }).Count -eq 1) 'Post-create failure must not recreate the draft.'
        Assert (@($script:calls | Where-Object { $_[0] -eq 'release' -and $_[1] -eq 'upload' }).Count -eq 0) 'Post-create failure must prevent asset uploads.'
        Assert (-not (Test-Path -LiteralPath (Join-Path $failedDirectory 'draft-upload.json'))) 'Post-create failure must not write a success receipt.'
    }

    Reset-Mock
    $script:remoteCommitSha = 'f' * 40
    Assert-Throws { Invoke-Upload (New-Request 'remote-commit') } '*Source commit was not found exactly*'
    Assert (@($script:calls | Where-Object { $_[0] -eq 'release' }).Count -eq 0) 'Missing source must prevent mutation.'

    Reset-Mock
    $directory = New-Request 'tampered-receipt'
    $null = Invoke-Upload $directory
    $receiptPath = Join-Path $directory 'draft-upload.json'
    $receipt = [IO.File]::ReadAllText($receiptPath) | ConvertFrom-Json
    $receipt.target = 'linux-x64'
    [IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 8))
    $script:calls.Clear()
    Assert-Throws { Invoke-Upload $directory } '*differs; refusing to overwrite*'
    Assert ($script:calls.Count -eq 0) 'Conflicting local receipt must fail before network access.'

    Reset-Mock
    $script:release = New-MockRelease
    $script:release.draft = $false
    Assert-Throws { Invoke-Upload (New-Request 'non-draft') } '*is not a draft*'
    Assert (@($script:calls | Where-Object { $_[0] -eq 'release' }).Count -eq 0) 'Published release must not be mutated.'

    Reset-Mock
    $script:release = New-MockRelease
    $script:release.target_commitish = 'main'
    Assert-Throws { Invoke-Upload (New-Request 'wrong-target') } '*exact source commit*'
    Assert (@($script:calls | Where-Object { $_[0] -eq 'release' }).Count -eq 0) 'Wrong target must not be mutated.'

    foreach ($kind in @('wrong-commit', 'annotated')) {
        Reset-Mock
        $script:hasTag = $true
        if ($kind -eq 'wrong-commit') { $script:tagSha = 'f' * 40 } else { $script:tagType = 'tag' }
        Assert-Throws { Invoke-Upload (New-Request $kind) } '*does not point directly to source commit*'
        Assert (@($script:calls | Where-Object { $_[0] -eq 'release' }).Count -eq 0) 'Conflicting tag must prevent release mutation.'
    }

    Reset-Mock
    $script:release = New-MockRelease
    $script:release.assets = @([pscustomobject]@{
        name = [IO.Path]::GetFileName($artifactPath); size = $script:artifact.size
        state = 'uploaded'; digest = 'sha256:' + ('f' * 64)
    })
    Assert-Throws { Invoke-Upload (New-Request 'wrong-asset') } '*different SHA-256*'
    Assert (@($script:calls | Where-Object { $_[0] -eq 'release' }).Count -eq 0) 'Asset conflict must fail before missing asset uploads.'

    Reset-Mock
    $script:release = New-MockRelease
    $script:release.assets = @([pscustomobject]@{ name = 'unexpected.zip'; size = 1; state = 'uploaded'; digest = $null })
    Assert-Throws { Invoke-Upload (New-Request 'unexpected-asset') } '*Unexpected or duplicate release asset*'
    Assert (@($script:calls | Where-Object { $_[0] -eq 'release' }).Count -eq 0) 'Unexpected assets must prevent mutation.'

    foreach ($operation in @('api', 'create', 'upload')) {
        Reset-Mock
        $script:failure = $operation
        $directory = New-Request "failure-$operation"
        Assert-Throws { Invoke-Upload $directory } '*GitHub CLI failed (exit 17)*'
        Assert (-not (Test-Path -LiteralPath (Join-Path $directory 'draft-upload.json'))) 'Failure must not write a success receipt.'
        Assert ($script:waits.Count -eq 0) 'API, create and upload errors must not enter visibility waits.'
        $operations = @($script:calls | Where-Object { $_[0] -eq 'release' } | ForEach-Object { $_[1] })
        if ($operation -in @('api', 'create')) { Assert ($operations -notcontains 'upload') 'CLI failure must stop later uploads.' }
        if ($operation -eq 'upload') { Assert (@($operations | Where-Object { $_ -eq 'upload' }).Count -eq 1) 'Upload failure must stop later assets.' }
    }

    Reset-Mock
    $script:publishAfterUpload = $true
    $directory = New-Request 'racing-publication'
    Assert-Throws { Invoke-Upload $directory } '*is not a draft*'
    Assert (-not (Test-Path -LiteralPath (Join-Path $directory 'draft-upload.json'))) 'Concurrent publication must prevent success receipt.'
    Assert (@($script:calls | Where-Object { $_[0] -eq 'release' -and $_[1] -eq 'upload' }).Count -eq 1) 'Published state must prevent the next upload.'

    Reset-Mock
    $badArtifact = $script:artifact.PSObject.Copy()
    $badArtifact.sha256 = 'f' * 64
    $directory = New-Request 'wrong-local'
    Assert-Throws { Invoke-HandyDraftUpload -Repository 'Belgian-Coder/HandyPlayer' -SourceSha $script:sha -Target win-x64 `
        -Artifacts @($badArtifact) -OutputDirectory $directory -NativeAcceptance 'not performed' } '*validated size/SHA-256*'
    Assert ($script:calls.Count -eq 0) 'Invalid local bytes must fail before any gh invocation.'

    # A byte-identical target must never make a reserved/staged link acceptable.
    $canCreateFileLinks = $true
    try {
        $null = New-Item -ItemType SymbolicLink -Path (Join-Path $fixtureRoot 'link-permission-probe') -Target $artifactPath -ErrorAction Stop
    } catch {
        if ($_.CategoryInfo.Category -eq 'PermissionDenied' -or $_.Exception -is [UnauthorizedAccessException] -or
            $_.Exception.Message -match '(?i)privilege.*(held|require)|requires? (administrator|elevation)|permission denied|access.*denied') {
            $canCreateFileLinks = $false
            Write-Output 'SKIP: file symbolic-link refusal tests require an unavailable filesystem privilege.'
        } else { throw }
    }
    if ($canCreateFileLinks) {
        foreach ($reservedName in @('draft-manifest.json', 'draft-notes.md', 'draft-upload.json')) {
            Reset-Mock
            $directory = New-Request ("linked-" + $reservedName)
            $targetPath = Join-Path $createdDirectory $reservedName
            $targetHash = (Get-FileHash -LiteralPath $targetPath).Hash
            $null = New-Item -ItemType SymbolicLink -Path (Join-Path $directory $reservedName) -Target $targetPath
            Assert-Throws { Invoke-Upload $directory } '*Symbolic links and reparse points are not allowed*'
            Assert ($script:calls.Count -eq 0) 'Reserved links must fail before gh.'
            Assert (@(Get-ChildItem -LiteralPath $directory -Force).Count -eq 1) 'Reserved link refusal must not create request files.'
            Assert ((Get-FileHash -LiteralPath $targetPath).Hash -ceq $targetHash) 'Reserved link target must remain unchanged.'
        }

        Reset-Mock
        $directory = New-Request 'linked-staged-file'
        $staging = Join-Path $directory 'draft-assets'
        $null = New-Item -ItemType Directory -Path $staging
        $null = New-Item -ItemType SymbolicLink -Path (Join-Path $staging ([IO.Path]::GetFileName($artifactPath))) -Target $artifactPath
        Assert-Throws { Invoke-Upload $directory } '*Symbolic links and reparse points are not allowed*'
        Assert ($script:calls.Count -eq 0) 'Staged links must fail before gh.'
        Assert (@(Get-ChildItem -LiteralPath $directory -Force).Count -eq 1) 'Staged link refusal must not create request files.'
        Assert ((Get-FileHash -LiteralPath $artifactPath).Hash -ieq $script:artifact.sha256) 'Staged link target must remain unchanged.'

        Reset-Mock
        $directory = New-Request 'linked-artifact'
        $linkedArtifact = $script:artifact.PSObject.Copy()
        $linkedArtifact.path = Join-Path $directory 'linked-package.zip'
        $null = New-Item -ItemType SymbolicLink -Path $linkedArtifact.path -Target $artifactPath
        Assert-Throws { Invoke-HandyDraftUpload -Repository 'Belgian-Coder/HandyPlayer' -SourceSha $script:sha -Target win-x64 `
            -Artifacts @($linkedArtifact) -OutputDirectory $directory -NativeAcceptance 'not performed' } '*Symbolic links and reparse points are not allowed*'
        Assert ($script:calls.Count -eq 0) 'Artifact links must fail before gh.'
        Assert (@(Get-ChildItem -LiteralPath $directory -Force).Count -eq 1) 'Artifact link refusal must not create request files.'
    }

    if ($IsWindows -or $canCreateFileLinks) {
        Reset-Mock
        $directory = New-Request 'linked-output-target'
        $linkedDirectory = Join-Path $fixtureRoot 'linked-output'
        $linkKind = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
        $null = New-Item -ItemType $linkKind -Path $linkedDirectory -Target $directory
        Assert-Throws { Invoke-Upload $linkedDirectory } '*Symbolic links and reparse points are not allowed*'
        Assert ($script:calls.Count -eq 0) 'Linked output directory must fail before gh.'
        Assert (@(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) 'Linked output refusal must not create files in its target.'
        $child = Join-Path $directory 'child'
        $null = New-Item -ItemType Directory -Path $child
        Assert-Throws { Invoke-Upload (Join-Path $linkedDirectory 'child') } '*Symbolic links and reparse points are not allowed*'
        Assert ($script:calls.Count -eq 0) 'Linked output ancestor must fail before gh.'
        Assert (@(Get-ChildItem -LiteralPath $child -Force).Count -eq 0) 'Linked ancestor refusal must not create request files.'
    }

    Reset-Mock
    $profileDirectory = New-Request 'profile-bound'
    $null = Invoke-Upload $profileDirectory
    $script:calls.Clear()
    Assert-Throws { Invoke-HandyDraftUpload -Repository 'Belgian-Coder/HandyPlayer' -SourceSha $script:sha -Target win-x64 `
        -Artifacts @($script:artifact) -OutputDirectory $profileDirectory -NativeAcceptance 'not performed' -PackageProfile Candidate } '*differs; refusing to overwrite*'
    Assert ($script:calls.Count -eq 0) 'Wrong profile reached GitHub.'
    Reset-Mock
    $script:tag = "ci-test-win-x64-candidate-$script:sha"
    $candidateResult = Invoke-HandyDraftUpload -Repository 'Belgian-Coder/HandyPlayer' -SourceSha $script:sha -Target win-x64 `
        -Artifacts @($script:artifact) -OutputDirectory (New-Request 'candidate') -NativeAcceptance 'not performed' -PackageProfile Candidate
    Assert ($candidateResult.packageProfile -ceq 'Candidate' -and $candidateResult.tag -ceq $script:tag) 'Candidate draft profile/tag missing.'
    Reset-Mock
    $exePath = Join-Path $fixtureRoot 'HandyPlayer-test-win-x64-setup.exe'
    [IO.File]::WriteAllText($exePath, 'mock optional installer')
    $exeArtifact = [pscustomobject]@{ path=$exePath; size=(Get-Item -LiteralPath $exePath).Length
        sha256=(Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant() }
    $exeDirectory = New-Request 'candidate-exe-refused'
    Assert-Throws { Invoke-HandyDraftUpload -Repository 'Belgian-Coder/HandyPlayer' -SourceSha $script:sha -Target win-x64 `
        -Artifacts @($exeArtifact) -OutputDirectory $exeDirectory -NativeAcceptance 'not performed' -PackageProfile Candidate } '*must not upload an Inno EXE*'
    Assert ($script:calls.Count -eq 0 -and @(Get-ChildItem -LiteralPath $exeDirectory -Force).Count -eq 0) 'Candidate EXE refusal mutated draft state.'
    $global:LASTEXITCODE = 0
    Write-Output 'Draft uploader offline identity, draft-only, idempotence, hashes and failure checks passed.'
} finally {
    # Retain ignored fixture evidence; remove only our in-process command mock.
    Remove-Item Function:gh, Function:Start-Sleep
}
