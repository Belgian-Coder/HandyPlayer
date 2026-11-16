# Dot-source only. Resolve dependency ownership without creating state or touching an index.
function Assert-HandyProjectDirectory([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path -match '[\x00-\x1f\x7f]' -or
        ($Path -split '[\\/]' | Where-Object { $_ -in @('.', '..') })) {
        throw 'Build project root must be an absolute canonical existing directory.'
    }
    $full = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    $nativeText = $Path.Replace([IO.Path]::AltDirectorySeparatorChar, [IO.Path]::DirectorySeparatorChar)
    if ($full -cne $nativeText) { throw 'Build project root must use a canonical directory path.' }
    $cursor = $full
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            ($item.PSObject.Properties['LinkType'] -and $item.LinkType)) {
            throw 'Build project directory must not use a link/junction or non-directory ancestor.'
        }
        $parent = [IO.Directory]::GetParent($cursor)
        $cursor = if ($parent) { $parent.FullName } else { $null }
    }
    return $full
}

function Assert-HandyProjectStateDirectory([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        $item = $null
        try { $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop }
        catch [Management.Automation.ItemNotFoundException] { }
        if ($item -and (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            ($item.PSObject.Properties['LinkType'] -and $item.LinkType))) {
            throw 'Build state/cache directory must not use a link/junction or non-directory ancestor.'
        }
        $parent = [IO.Directory]::GetParent($cursor)
        $cursor = if ($parent) { $parent.FullName } else { $null }
    }
}

function Read-HandyProjectJson([string]$Path) {
    $parent = Assert-HandyProjectDirectory ([IO.Path]::GetDirectoryName($Path))
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        ($item.PSObject.Properties['LinkType'] -and $item.LinkType)) { throw 'Build project metadata must be a regular non-link file.' }
    if ($item.Length -gt 1MB) { throw 'Build project metadata is too large.' }
    $text = [IO.File]::ReadAllText($Path)
    $document = [Text.Json.JsonDocument]::Parse($text)
    try {
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'Build project metadata must be an object.' }
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $document.RootElement.EnumerateObject()) {
            if (-not $names.Add($property.Name)) { throw 'Build project metadata must not repeat keys.' }
        }
        return ($text | ConvertFrom-Json)
    } finally { $document.Dispose() }
}

function Assert-HandyProjectOwner($Owner, [string[]]$Keys) {
    $actual = @($Owner.PSObject.Properties.Name)
    if ($actual.Count -ne $Keys.Count -or @($actual | Where-Object { $Keys -cnotcontains $_ }).Count -or
        $Owner.owner -isnot [string] -or $Owner.project -isnot [string] -or
        $Owner.owner -cne 'request-release' -or $Owner.project -cne 'HandyPlayer' -or
        -not ($Owner.version -is [int] -or $Owner.version -is [long]) -or $Owner.version -ne 1) {
        throw 'Build project marker is not owned by this project with the exact identity.'
    }
}

function Get-HandyProjectGitValue([string]$Root, [string[]]$Arguments) {
    $value = & git --no-optional-locks -C $Root @Arguments 2>$null
    if ($LASTEXITCODE -ne 0 -or @($value).Count -ne 1) { throw 'Build project Git metadata could not be read.' }
    return [string]$value
}

function Test-HandyCredentialFreeOrigin([string]$Origin) {
    # Match transport identity without admitting credentials, query tokens or fragments.
    if ($Origin -match '[\x00-\x20\x7f]') { return $false }
    if ($Origin -cmatch '^https://[^/@?#\s]+/[^?#\s]+$') {
        $uri = $null
        return ([uri]::TryCreate($Origin, [UriKind]::Absolute, [ref]$uri) -and
            $uri.Scheme -ceq 'https' -and $uri.Host -and -not $uri.UserInfo -and
            -not $uri.Query -and -not $uri.Fragment -and $uri.AbsolutePath.Length -gt 1)
    }
    return $Origin -cmatch '^git@[^:/@?#\s]+:[^?#\s]+$'
}

function Get-HandyPipeProjectRoot([string]$CheckoutRoot, [string]$DefaultRoot, [string]$Hint) {
    $default = Assert-HandyProjectDirectory $DefaultRoot
    if (-not $Hint) { return $default }
    $hintRoot = Assert-HandyProjectDirectory $Hint
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $top = Get-HandyProjectGitValue $hintRoot @('rev-parse', '--show-toplevel')
    if (-not [IO.Path]::GetFullPath($top).Equals($hintRoot, $comparison)) { throw 'Build project hint is not the exact Git root.' }
    if ($hintRoot.Equals($default, $comparison)) { return $default }

    $checkout = Assert-HandyProjectDirectory $CheckoutRoot
    $checkoutTop = Get-HandyProjectGitValue $checkout @('rev-parse', '--show-toplevel')
    if (-not [IO.Path]::GetFullPath($checkoutTop).Equals($checkout, $comparison) -or
        -not (Test-Path -LiteralPath (Join-Path $checkout '.git') -PathType Container)) {
        throw 'A different build project hint requires an independent owned release clone.'
    }
    $null = Assert-HandyProjectDirectory (Join-Path $checkout '.git')
    $manifest = Read-HandyProjectJson (Join-Path $checkout 'eng/release-targets.json')
    if (-not ($manifest.schemaVersion -is [int] -or $manifest.schemaVersion -is [long]) -or
        $manifest.schemaVersion -ne 1 -or $manifest.project -cne 'HandyPlayer' -or
        $manifest.worktreeDirectory -isnot [string] -or $manifest.targets -isnot [pscustomobject] -or
        @($manifest.targets.PSObject.Properties).Count -eq 0) { throw 'Build project manifest identity differs.' }
    $worktrees = [string]$manifest.worktreeDirectory
    if (-not $worktrees -or $worktrees.Length -gt 240 -or $worktrees -match '^[\\/]|[\\:\x00-\x1f\x7f{}<>"|?*\[\]]' -or
        @($worktrees -split '[\\/]' | Where-Object { $_ -in @('', '.', '..') -or $_ -ieq '.git' -or $_ -match '[. ]$' -or $_ -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' }).Count) {
        throw 'Build project manifest worktree directory is unsafe.'
    }
    $owner = Read-HandyProjectJson (Join-Path $checkout '.request-release-owner.json')
    Assert-HandyProjectOwner $owner @('owner', 'version', 'project', 'target', 'root')
    if ($owner.target -isnot [string] -or $owner.root -isnot [string] -or
        $owner.target -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$' -or
        @($manifest.targets.PSObject.Properties.Name) -cnotcontains $owner.target -or $owner.root -cne $hintRoot) {
        throw 'Build project clone marker target/root differs.'
    }
    $expected = [IO.Path]::GetFullPath((Join-Path (Join-Path $hintRoot $worktrees) ('release-' + $owner.target)))
    if (-not $checkout.Equals($expected, $comparison)) { throw 'Build project hint must contain this checkout at the exact declared release target path.' }
    $outerMarker = Join-Path $hintRoot '.request-release-owner.json'
    if (Get-Item -LiteralPath $outerMarker -Force -ErrorAction SilentlyContinue) {
        Assert-HandyProjectOwner (Read-HandyProjectJson $outerMarker) @('owner', 'version', 'project')
    }
    $hintOrigin = Get-HandyProjectGitValue $hintRoot @('config', '--get', 'remote.origin.url')
    $checkoutOrigin = Get-HandyProjectGitValue $checkout @('config', '--get', 'remote.origin.url')
    if (-not (Test-HandyCredentialFreeOrigin $hintOrigin) -or -not (Test-HandyCredentialFreeOrigin $checkoutOrigin) -or
        $hintOrigin -cne $checkoutOrigin) { throw 'Build project root must have the same credential-free canonical origin as this checkout.' }
    return $hintRoot
}

function Get-HandyCacheProjectRoot([string]$CheckoutRoot, [string]$Hint = $env:BUILD_RELEASE_PROJECT_ROOT) {
    $common = Get-HandyProjectGitValue $CheckoutRoot @('rev-parse', '--path-format=absolute', '--git-common-dir')
    return Get-HandyPipeProjectRoot $CheckoutRoot (Split-Path $common -Parent) $Hint
}
