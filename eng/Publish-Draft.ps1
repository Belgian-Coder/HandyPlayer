#Requires -Version 7
# Dot-source only. The release adapter owns checkout/validation/native-host gates.

function Assert-HandyDraftNoLink {
    param([string]$Path)
    # Check lexical ancestors as well: a regular file below a junction still
    # redirects reads/writes outside the request's ordinary directory tree.
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        $item = $null
        try { $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop }
        catch [Management.Automation.ItemNotFoundException] { }
        if ($null -ne $item -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            ($item.PSObject.Properties['LinkType'] -and $item.LinkType))) {
            throw "Symbolic links and reparse points are not allowed in draft paths: $cursor"
        }
        $parent = [IO.Directory]::GetParent($cursor)
        $cursor = if ($null -eq $parent) { $null } else { $parent.FullName }
    }
}

function Write-HandyDraftText {
    param([string]$Path, [string]$Text)
    Assert-HandyDraftNoLink -Path $Path
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    if (Test-Path -LiteralPath $Path) {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -cne $hash) {
            throw "Existing request file '$([IO.Path]::GetFileName($Path))' differs; refusing to overwrite."
        }
        return
    }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}

function Invoke-HandyDraftGh {
    param([Parameter(Mandatory)][string[]]$Arguments)
    # Capture output: neither credentials nor a premature release URL reach callers.
    $PSNativeCommandUseErrorActionPreference = $false
    $output = @(& gh @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI failed (exit $LASTEXITCODE) during $($Arguments[0]) $($Arguments[1])."
    }
    return [string]::Join("`n", [string[]]$output)
}

function Get-HandyDraftJson {
    param([Parameter(Mandatory)][string]$Endpoint, [switch]$Paginate)
    $arguments = @('api', '--hostname', 'github.com', '--method', 'GET', $Endpoint)
    if ($Paginate) { $arguments += @('--paginate', '--slurp') }
    $json = Invoke-HandyDraftGh -Arguments $arguments
    return ,($json | ConvertFrom-Json -Depth 60 -NoEnumerate)
}

function Get-HandyDraftRelease {
    param([string]$Repository, [string]$Tag)
    # The list endpoint includes drafts for authenticated users with push access.
    $pages = Get-HandyDraftJson "repos/$Repository/releases?per_page=100" -Paginate
    $found = @(
        foreach ($page in $pages) {
            foreach ($release in $page) {
                if ($release.tag_name -ceq $Tag) { $release }
            }
        }
    )
    if ($found.Count -gt 1) { throw "Multiple releases use $Tag; refusing ambiguity." }
    if ($found.Count -eq 1) { return $found[0] }
    return $null
}

function Assert-HandyDraftTag {
    param([string]$Repository, [string]$Tag, [string]$SourceSha)
    $refs = Get-HandyDraftJson "repos/$Repository/git/matching-refs/tags/$Tag"
    foreach ($ref in $refs) {
        if ($ref.ref -ceq "refs/tags/$Tag") {
            # Annotated tags are deliberately refused; no implicit tag peeling.
            if ($ref.object.type -cne 'commit' -or $ref.object.sha -cne $SourceSha) {
                throw "Existing tag $Tag does not point directly to source commit $SourceSha."
            }
        }
    }
    # GitHub can leave a draft's tag absent until publication. Its full-SHA
    # target_commitish remains mandatory, whether a tag currently exists or not.
}

function Assert-HandyDraftRelease {
    param($Release, [string]$Repository, [string]$Tag, [string]$SourceSha)
    if ($null -eq $Release) { throw "Draft release $Tag was not found." }
    if ($Release.draft -isnot [bool] -or -not $Release.draft) {
        throw "Release $Tag is not a draft; refusing to change it."
    }
    if ($Release.tag_name -cne $Tag -or $Release.target_commitish -cne $SourceSha) {
        throw "Release $Tag does not target the exact source commit $SourceSha."
    }
    if ([string]$Release.html_url -notlike "https://github.com/$Repository/releases/*") {
        throw 'GitHub returned an unexpected release URL.'
    }
    Assert-HandyDraftTag -Repository $Repository -Tag $Tag -SourceSha $SourceSha
}

function Assert-HandyDraftAssets {
    param($Release, [object[]]$Expected, [string]$Repository, [string]$Tag,
        [string]$OutputDirectory, [switch]$RequireAll)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($asset in $Release.assets) {
        $match = @($Expected | Where-Object { $_.name -ceq $asset.name })
        if ($match.Count -ne 1 -or -not $seen.Add([string]$asset.name)) {
            throw "Unexpected or duplicate release asset '$($asset.name)'; refusing changes."
        }
        $expectedAsset = $match[0]
        if ($asset.state -cne 'uploaded' -or [long]$asset.size -ne $expectedAsset.size) {
            throw "Release asset '$($asset.name)' is incomplete or has a different size."
        }
        $digest = if ($asset.PSObject.Properties['digest']) { [string]$asset.digest } else { '' }
        if ($digest -cmatch '^sha256:([0-9a-fA-F]{64})$') {
            $remoteHash = $Matches[1].ToLowerInvariant()
        } else {
            # Old assets may lack GitHub's SHA-256 digest. Download into a new
            # request-owned directory and verify bytes; never overwrite a file.
            $verifyDir = Join-Path $OutputDirectory ('verify-' + [guid]::NewGuid().ToString('N'))
            $null = New-Item -ItemType Directory -Path $verifyDir -ErrorAction Stop
            $null = Invoke-HandyDraftGh -Arguments @('release', 'download', $Tag, '--repo',
                "github.com/$Repository", '--pattern', $asset.name, '--dir', $verifyDir)
            $download = Join-Path $verifyDir $asset.name
            Assert-HandyDraftNoLink -Path $download
            if (-not (Test-Path -LiteralPath $download -PathType Leaf) -or
                (Get-Item -LiteralPath $download).Length -ne $expectedAsset.size) {
                throw "Downloaded asset '$($asset.name)' has a different size or is missing."
            }
            $remoteHash = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        if ($remoteHash -cne $expectedAsset.sha256) {
            throw "Release asset '$($asset.name)' has a different SHA-256; assets are never overwritten."
        }
    }
    if ($RequireAll -and $seen.Count -ne $Expected.Count) {
        throw 'Draft release is missing expected assets after upload.'
    }
}

function Invoke-HandyDraftUpload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Belgian-Coder/HandyPlayer')][string]$Repository,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SourceSha,
        [Parameter(Mandatory)][ValidateSet('win-x64', 'osx-arm64', 'linux-x64')][string]$Target,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][object[]]$Artifacts,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$NativeAcceptance,
        [ValidateSet('Candidate', 'Full')][string]$PackageProfile = 'Full'
    )
    $SourceSha = $SourceSha.ToLowerInvariant()
    $PackageProfile = if ($PackageProfile -ieq 'Candidate') { 'Candidate' } else { 'Full' }
    if ($Target -ne 'win-x64' -and $PackageProfile -ne 'Full') { throw 'Candidate packaging profile applies only to Windows.' }
    $tag = if ($Target -eq 'win-x64') { "ci-test-$Target-$($PackageProfile.ToLowerInvariant())-$SourceSha" } else { "ci-test-$Target-$SourceSha" }
    if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) {
        throw 'OutputDirectory must be an existing absolute request directory.'
    }
    Assert-HandyDraftNoLink -Path $OutputDirectory
    if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
        throw 'OutputDirectory must be an existing absolute request directory.'
    }
    $outputRoot = (Get-Item -LiteralPath $OutputDirectory).FullName
    $stage = Join-Path $outputRoot 'draft-assets'
    $manifestPath = Join-Path $outputRoot 'draft-manifest.json'
    $notesPath = Join-Path $outputRoot 'draft-notes.md'
    $receiptPath = Join-Path $outputRoot 'draft-upload.json'
    foreach ($reserved in @($stage, $manifestPath, $notesPath, $receiptPath)) {
        Assert-HandyDraftNoLink -Path $reserved
    }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $packages = @(
        foreach ($artifact in $Artifacts) {
            $path = [string]$artifact.path
            if (-not [IO.Path]::IsPathFullyQualified($path)) {
                throw 'Every artifact must be an existing absolute file path.'
            }
            Assert-HandyDraftNoLink -Path $path
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw 'Every artifact must be an existing absolute file path.'
            }
            $file = Get-Item -LiteralPath $path
            $name = $file.Name
            if ($name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or
                $name -ieq 'draft-manifest.json' -or -not $names.Add($name)) {
                throw "Artifact name '$name' is unsafe, reserved or duplicated."
            }
            $hash = [string]$artifact.sha256
            if ($hash -notmatch '^[0-9a-fA-F]{64}$' -or [string]$artifact.size -notmatch '^\d+$' -or
                [long]$artifact.size -ne $file.Length -or
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $hash) {
                throw "Local artifact '$name' does not match its validated size/SHA-256."
            }
            [pscustomobject][ordered]@{
                name = $name; size = [long]$file.Length; sha256 = $hash.ToLowerInvariant(); path = $file.FullName
            }
        }
    )
    if ($Target -eq 'win-x64' -and $PackageProfile -eq 'Candidate' -and
        @($packages | Where-Object name -Like '*-win-x64-setup.exe').Count) {
        throw 'Candidate packaging profile must not upload an Inno EXE.'
    }
    [Array]::Sort($packages, [Comparison[object]]{
        param($left, $right)
        [StringComparer]::Ordinal.Compare($left.name, $right.name)
    })
    foreach ($package in $packages) {
        Assert-HandyDraftNoLink -Path (Join-Path $stage $package.name)
    }
    if (-not (Test-Path -LiteralPath $stage)) {
        $null = New-Item -ItemType Directory -Path $stage -ErrorAction Stop
    }
    if (-not (Test-Path -LiteralPath $stage -PathType Container) -or
        ((Get-Item -LiteralPath $stage).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Request staging path must be a regular directory.'
    }
    foreach ($package in $packages) {
        $destination = Join-Path $stage $package.name
        Assert-HandyDraftNoLink -Path $package.path
        Assert-HandyDraftNoLink -Path $destination
        if (-not (Test-Path -LiteralPath $destination)) {
            [IO.File]::Copy($package.path, $destination, $false)
        }
        if ((Get-Item -LiteralPath $destination).Length -ne $package.size -or
            (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ine $package.sha256) {
            throw "Artifact '$($package.name)' changed while staging."
        }
        $package.path = $destination
    }
    $portable = @($packages | Select-Object name, size, sha256)
    $manifest = [ordered]@{
        schemaVersion = 1; sourceSha = $SourceSha; target = $Target
        packageProfile = $PackageProfile
        nativeAcceptance = $NativeAcceptance; artifacts = $portable
    }
    $manifestJson = ($manifest | ConvertTo-Json -Depth 8).Replace("`r`n", "`n") + "`n"
    Write-HandyDraftText -Path $manifestPath -Text $manifestJson
    $notes = "ci-test $Target $($SourceSha.Substring(0, 12))`n`nSource commit: $SourceSha`nTarget: $Target`nPackaging profile: $PackageProfile`nNative acceptance: $NativeAcceptance`n`nDraft testing candidate. Asset hashes and source identity are in draft-manifest.json.`n"
    Write-HandyDraftText -Path $notesPath -Text $notes
    $expected = @($packages) + @([pscustomobject]@{
        name = 'draft-manifest.json'; size = [long](Get-Item -LiteralPath $manifestPath).Length
        sha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        path = $manifestPath
    })
    $expectedPortable = @($expected | Select-Object name, size, sha256)
    if (Test-Path -LiteralPath $receiptPath) {
        $prior = [IO.File]::ReadAllText($receiptPath) | ConvertFrom-Json -Depth 12
        if ([string]$prior.url -notlike "https://github.com/$Repository/releases/*") {
            throw 'Existing upload receipt has an unexpected release URL.'
        }
        $priorExpected = [ordered]@{
            url = [string]$prior.url; tag = $tag; sourceSha = $SourceSha; target = $Target
            packageProfile = $PackageProfile
            artifacts = $expectedPortable
        }
        Write-HandyDraftText -Path $receiptPath -Text (($priorExpected | ConvertTo-Json -Depth 8).Replace("`r`n", "`n") + "`n")
    }

    # Validate remote identity before performing any mutation.
    $commit = Get-HandyDraftJson "repos/$Repository/git/commits/$SourceSha"
    if ($commit.sha -cne $SourceSha) { throw 'Source commit was not found exactly in the destination repository.' }
    Assert-HandyDraftTag -Repository $Repository -Tag $tag -SourceSha $SourceSha
    $release = Get-HandyDraftRelease -Repository $Repository -Tag $tag
    if ($null -eq $release) {
        $null = Invoke-HandyDraftGh -Arguments @('release', 'create', $tag, '--repo', "github.com/$Repository",
            '--draft', '--target', $SourceSha, '--title', "ci-test $Target $PackageProfile $($SourceSha.Substring(0, 12))",
            '--notes-file', $notesPath)
        # A newly created draft can briefly be absent from the release list.
        # Retry only absence after successful creation; API errors and any
        # visible release still pass straight to the existing identity gates.
        foreach ($delay in @(0, 500, 1000, 2000)) {
            if ($delay -gt 0) { Start-Sleep -Milliseconds $delay }
            $release = Get-HandyDraftRelease -Repository $Repository -Tag $tag
            if ($null -ne $release) { break }
        }
    }
    Assert-HandyDraftRelease -Release $release -Repository $Repository -Tag $tag -SourceSha $SourceSha
    Assert-HandyDraftAssets -Release $release -Expected $expected -Repository $Repository -Tag $tag -OutputDirectory $outputRoot
    $remoteNames = @($release.assets | ForEach-Object { $_.name })
    $missing = @($expected | Where-Object { $_.name -cnotin $remoteNames })
    foreach ($asset in $missing) {
        # Recheck before each mutation, including every existing asset. A racing
        # publisher/editor cannot silently change the observed release state.
        $release = Get-HandyDraftRelease -Repository $Repository -Tag $tag
        Assert-HandyDraftRelease -Release $release -Repository $Repository -Tag $tag -SourceSha $SourceSha
        Assert-HandyDraftAssets -Release $release -Expected $expected -Repository $Repository -Tag $tag -OutputDirectory $outputRoot
        if ($asset.name -cin @($release.assets | ForEach-Object { $_.name })) { continue }
        Assert-HandyDraftNoLink -Path $asset.path
        if ((Get-Item -LiteralPath $asset.path).Length -ne $asset.size -or
            (Get-FileHash -LiteralPath $asset.path -Algorithm SHA256).Hash -ine $asset.sha256) {
            throw "Staged artifact '$($asset.name)' changed before upload."
        }
        $null = Invoke-HandyDraftGh -Arguments @('release', 'upload', $tag, '--repo', "github.com/$Repository", $asset.path)
    }
    $release = Get-HandyDraftRelease -Repository $Repository -Tag $tag
    Assert-HandyDraftRelease -Release $release -Repository $Repository -Tag $tag -SourceSha $SourceSha
    Assert-HandyDraftAssets -Release $release -Expected $expected -Repository $Repository -Tag $tag -OutputDirectory $outputRoot -RequireAll
    $result = [pscustomobject][ordered]@{
        url = [string]$release.html_url; tag = $tag; sourceSha = $SourceSha; target = $Target
        packageProfile = $PackageProfile
        artifacts = $expectedPortable
    }
    Write-HandyDraftText -Path $receiptPath -Text (($result | ConvertTo-Json -Depth 8).Replace("`r`n", "`n") + "`n")
    return $result
}
