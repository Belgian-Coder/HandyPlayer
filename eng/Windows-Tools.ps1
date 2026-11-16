# Shared checkout-local Windows packaging tool paths and process environment.
function Get-HandyWixToolDirectory([string]$ProjectRoot) {
    if (-not $ProjectRoot) { throw 'Project root is required.' }
    return [IO.Path]::GetFullPath((Join-Path $ProjectRoot '.harness/tools/wix-6.0.2'))
}

function Assert-HandyNoReparseAncestors([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Windows tool path must not use a link or junction: $cursor"
        }
        $parent = [IO.Directory]::GetParent($cursor)
        if (-not $parent -or $parent.FullName -eq $cursor) { break }
        $cursor = $parent.FullName
    }
}

function ConvertFrom-HandyWixVersionOutput([string[]]$Output) {
    $versions = @(
        foreach ($line in $Output) {
            if ($line.Trim() -match '^(?:WiX\s+Toolset\s+)?v?(\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?)$') {
                $Matches[1]
            }
        }
    )
    if ($versions.Count -ne 1) { throw 'WiX --version returned an unknown version response.' }
    return $versions[0]
}

function Get-HandyWixVersion([string]$WixPath) {
    $output = @(& $WixPath --version 2>&1 | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) { throw "WiX version check failed with exit code $LASTEXITCODE." }
    return ConvertFrom-HandyWixVersionOutput $output
}

function Get-HandyWixPath([string]$ProjectRoot) {
    $directory = Get-HandyWixToolDirectory $ProjectRoot
    Assert-HandyNoReparseAncestors $directory
    if (-not (Test-Path -LiteralPath $directory)) { return $null }
    $item = Get-Item -LiteralPath $directory -Force
    if (-not $item.PSIsContainer) { throw "WiX tool target is not a directory; preserving it: $directory" }
    $path = Join-Path $directory 'wix.exe'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Existing WiX tool target is unknown; preserving it: $directory"
    }
    $exe = Get-Item -LiteralPath $path -Force
    if ($exe.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Windows tool executable must not be a link or junction: $path"
    }
    $version = Get-HandyWixVersion $path
    if ($version -notmatch '^6\.0\.2(?:\+[0-9A-Za-z.-]+)?$') {
        throw "Existing WiX version '$version' does not match pinned version 6.0.2; preserving it."
    }
    return $path
}

function Initialize-HandyWindowsToolEnvironment([string]$ProjectRoot) {
    $wixPath = Get-HandyWixPath $ProjectRoot
    if (-not $wixPath) { throw 'The checkout-local WiX 6.0.2 tool is not restored.' }
    $directory = Split-Path -Parent $wixPath
    $extensionCache = [IO.Path]::GetFullPath((Join-Path $ProjectRoot '.harness/wix'))
    Assert-HandyNoReparseAncestors $extensionCache

    # Inspect only PATH's first item; old or relative user entries are left untouched.
    $pathSeparator = [string][IO.Path]::PathSeparator
    $firstPathEntry = if ($env:PATH) { ($env:PATH -split [regex]::Escape($pathSeparator), 2)[0] } else { '' }
    $firstIsLocal = $false
    if ($firstPathEntry -and [IO.Path]::IsPathFullyQualified($firstPathEntry)) {
        try {
            $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
            $firstFullPath = [IO.Path]::GetFullPath($firstPathEntry).TrimEnd('\', '/')
            $localFullPath = $directory.TrimEnd('\', '/')
            $firstIsLocal = [string]::Equals($firstFullPath, $localFullPath, $comparison)
        } catch { $firstIsLocal = $false }
    }
    if (-not $firstIsLocal) {
        if ($env:PATH) { $env:PATH = "$directory$pathSeparator$env:PATH" }
        else { $env:PATH = $directory }
    }
    $env:WIX_EXTENSIONS = $extensionCache
    return $wixPath
}
