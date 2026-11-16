#Requires -Version 7
# Dot-source only. This library restores the reviewed Windows native lock without an SDK.
. (Join-Path $PSScriptRoot 'Project-Root.ps1')

function Assert-HandyNativeNoLink {
    param([Parameter(Mandatory)][string]$Path)
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        $item = $null
        try { $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop }
        catch [Management.Automation.ItemNotFoundException] { }
        if ($null -ne $item -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            ($item.PSObject.Properties['LinkType'] -and $item.LinkType))) {
            throw "Links and reparse points are not allowed in native dependency paths: $cursor"
        }
        $parent = [IO.Directory]::GetParent($cursor)
        $cursor = if ($null -eq $parent) { $null } else { $parent.FullName }
    }
}

function Assert-HandyNativeContainedPath {
    param([string]$Root, [string]$Path)
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'Native dependency paths must be absolute.' }
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root)) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Native dependency writable path must remain inside CheckoutRoot.'
    }
    Assert-HandyNativeNoLink $full
    return $full
}

function ConvertTo-HandyNativeRelativePath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -match '^[\\/]' -or $Path -match '[:\x00-\x1f]') {
        throw 'Unsafe native dependency relative path or archive member.'
    }
    $parts = $Path -split '[\\/]'
    foreach ($part in $parts) {
        if ($part -in @('', '.', '..') -or $part -match '[. ]$' -or $part -match '[<>"|?*]' -or
            $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
            throw 'Unsafe native dependency relative path or archive member.'
        }
    }
    return [string]::Join([IO.Path]::DirectorySeparatorChar, $parts)
}

function New-HandyNativeDirectory {
    param([string]$Root, [string]$Path)
    $full = Assert-HandyNativeContainedPath $Root $Path
    $null = [IO.Directory]::CreateDirectory($full)
    Assert-HandyNativeNoLink $full
}

function Assert-HandyNativeFile {
    param([string]$Path, [long]$Bytes, [string]$Sha256, [switch]$PeX64)
    Assert-HandyNativeNoLink $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required native dependency file is missing: $Path" }
    if ((Get-Item -LiteralPath $Path -Force).Length -ne $Bytes -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Sha256) {
        throw "Native dependency checksum or size mismatch: $Path"
    }
    if ($PeX64) {
        $stream = [IO.File]::OpenRead($Path)
        $reader = [IO.BinaryReader]::new($stream)
        try {
            if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5a4d) { throw 'Native DLL has an invalid DOS header.' }
            $stream.Position = 0x3c
            $offset = $reader.ReadUInt32()
            if ($offset -lt 64 -or [long]$offset + 24 -gt $stream.Length) { throw 'Native DLL has an invalid PE offset.' }
            $stream.Position = $offset
            if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
                throw 'Native DLL must have a valid x64 PE header (machine 0x8664).'
            }
            # The COFF file header must identify a DLL, rather than an x64 executable.
            $stream.Position = [long]$offset + 22
            if (($reader.ReadUInt16() -band 0x2000) -eq 0) { throw 'Native PE file is not a DLL.' }
        } finally { $reader.Dispose() }
    }
}

function Get-HandyNativeArchiveMember {
    param([string]$Listing, [string]$DllFileName)
    # The archive's own Path header precedes this separator and is not a member.
    $lines = $Listing -split '\r?\n'
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Trim() -eq '----------') { $start = $i + 1; break }
    }
    if ($start -lt 0) { throw '7-Zip archive member section is missing.' }
    $members = [Collections.Generic.List[object]]::new()
    $record = @{}
    for ($i = $start; $i -le $lines.Count; $i++) {
        $line = if ($i -eq $lines.Count) { '' } else { $lines[$i] }
        if ([string]::IsNullOrWhiteSpace($line)) {
            if ($record.Count) { $members.Add($record); $record = @{} }
        } elseif ($line -match '^([^=]+?) = (.*)$') {
            $key = $Matches[1].Trim()
            if ($record.ContainsKey($key)) { throw 'Duplicate 7-Zip member metadata.' }
            $record[$key] = $Matches[2]
        } else { throw 'Unrecognized 7-Zip member metadata.' }
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $matchesDll = 0
    foreach ($member in $members) {
        if (-not $member.ContainsKey('Path')) { throw 'Archive member has no path.' }
        $memberPath = ConvertTo-HandyNativeRelativePath $member.Path
        if (-not $seen.Add($memberPath)) { throw 'Duplicate archive member path.' }
        foreach ($key in $member.Keys) {
            if (($key -match '(?i)link|reparse' -and $member[$key] -notin @('', '-')) -or
                ($key -in @('Mode', 'Attributes') -and $member[$key] -match '(^|\s)l[rwxstST-]{9}(\s|$)|(^|\s)L(\s|$)')) {
                throw 'Archive links and reparse members are forbidden.'
            }
        }
        if ($memberPath -ieq $DllFileName) {
            if (($member.ContainsKey('Folder') -and $member.Folder -eq '+') -or
                ($member.ContainsKey('Attributes') -and $member.Attributes -match '^D')) {
                throw 'Root native DLL archive member must be a file.'
            }
            $matchesDll++
        }
    }
    if ($matchesDll -ne 1) { throw 'Archive must contain exactly one root native DLL member.' }
    return $DllFileName
}

function Get-HandyNative7Zip {
    $command = Get-Command 7z -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) { return $command.Source }
    $fallback = 'C:\Program Files\7-Zip\7z.exe'
    if (Test-Path -LiteralPath $fallback -PathType Leaf) { return $fallback }
    throw '7-Zip is required; install it separately before native dependency restore.'
}

function Invoke-HandyNative7Zip {
    param([string]$Executable, [string[]]$Arguments)
    $PSNativeCommandUseErrorActionPreference = $false
    $global:LASTEXITCODE = 0
    try { $output = @(& $Executable @Arguments 2>&1) }
    catch { throw '7-Zip could not execute during native dependency restore.' }
    if ($LASTEXITCODE -ne 0) { throw "7-Zip failed during native dependency restore (exit $LASTEXITCODE)." }
    return [string]::Join("`n", [string[]]$output)
}

function Copy-HandyNativeLimitedStream {
    param([IO.Stream]$InputStream, [IO.Stream]$OutputStream, [long]$MaximumBytes,
        [Threading.CancellationToken]$CancellationToken = [Threading.CancellationToken]::None)
    $buffer = [byte[]]::new(65536)
    $total = 0L
    while (($count = $InputStream.ReadAsync($buffer, 0, $buffer.Length, $CancellationToken).GetAwaiter().GetResult()) -gt 0) {
        if ($total + $count -gt $MaximumBytes) { throw 'Native archive download exceeded the locked size.' }
        $OutputStream.Write($buffer, 0, $count)
        $total += $count
    }
}

function Invoke-HandyNativeDownload {
    param([string]$Url, [string]$Path, [long]$MaximumBytes)
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(10)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('HandyPlayer-native-restore/1.0')
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(10))
    $file = $null
    try {
        $file = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $uri = [uri]$Url
        for ($redirect = 0; $redirect -le 10; $redirect++) {
            if ($uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Fragment) { throw 'Native archive URL must use HTTPS without credentials or fragments.' }
            $response = $client.GetAsync($uri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $deadline.Token).GetAwaiter().GetResult()
            try {
                if ([int]$response.StatusCode -in @(301, 302, 303, 307, 308)) {
                    if ($redirect -eq 10 -or $null -eq $response.Headers.Location) { throw 'Native archive redirect limit or missing location.' }
                    $uri = [uri]::new($uri, $response.Headers.Location)
                    continue
                }
                $null = $response.EnsureSuccessStatusCode()
                if ($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -gt $MaximumBytes) {
                    throw 'Native archive download exceeded the locked size.'
                }
                $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                try { Copy-HandyNativeLimitedStream $inputStream $file $MaximumBytes -CancellationToken $deadline.Token }
                finally { $inputStream.Dispose() }
                return
            } finally { $response.Dispose() }
        }
    } catch { throw 'Native archive HTTPS download failed; partial evidence is retained.' }
    finally { if ($null -ne $file) { $file.Dispose() }; $client.Dispose(); $deadline.Dispose() }
}

function Write-HandyNativeReceipt {
    param([string]$Root, [string]$Path, [object]$Receipt)
    $full = Assert-HandyNativeContainedPath $Root $Path
    $text = ($Receipt | ConvertTo-Json -Depth 8) + "`n"
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
    if (Test-Path -LiteralPath $full) {
        if (-not (Test-Path -LiteralPath $full -PathType Leaf) -or
            [IO.File]::ReadAllText($full) -cne $text) { throw 'Existing native dependency receipt differs; refusing to overwrite.' }
        return
    }
    New-HandyNativeDirectory $Root ([IO.Path]::GetDirectoryName($full))
    Assert-HandyNativeNoLink $full
    $stream = [IO.File]::Open($full, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}

function Restore-HandyNativeDependency {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CheckoutRoot,
        [Parameter(Mandatory)][object]$Specification,
        [string[]]$ArchiveCandidates = @(),
        [string]$EvidencePath
    )
    if (-not $IsWindows) { throw 'Native dependency restore requires native Windows.' }
    if (-not [IO.Path]::IsPathFullyQualified($CheckoutRoot)) { throw 'CheckoutRoot must be absolute.' }
    $root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($CheckoutRoot))
    Assert-HandyNativeNoLink $root
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'CheckoutRoot must exist.' }
    foreach ($key in @('name', 'version', 'sourceUrl', 'sourcePage', 'archiveFileName', 'archiveSha256', 'archiveBytes',
        'dllFileName', 'dllSha256', 'dllBytes', 'peMachine', 'destination')) {
        if ($null -eq $Specification.$key -or [string]::IsNullOrWhiteSpace([string]$Specification.$key)) { throw "Native lock is missing $key." }
    }
    if ($Specification.archiveSha256 -cnotmatch '^[a-f0-9]{64}$' -or $Specification.dllSha256 -cnotmatch '^[a-f0-9]{64}$' -or
        [long]$Specification.archiveBytes -le 0 -or [long]$Specification.dllBytes -le 0 -or
        $Specification.peMachine -cne '0x8664') { throw 'Native lock requires SHA-256, positive sizes and PE machine 0x8664.' }
    $source = [uri]$Specification.sourceUrl
    if (-not $source.IsAbsoluteUri -or $source.Scheme -ne 'https' -or $source.UserInfo -or $source.Fragment) {
        throw 'Native lock sourceUrl must be HTTPS without credentials or fragments.'
    }
    $dllName = ConvertTo-HandyNativeRelativePath $Specification.dllFileName
    $archiveName = ConvertTo-HandyNativeRelativePath $Specification.archiveFileName
    if ($dllName -ne [IO.Path]::GetFileName($dllName) -or $archiveName -ne [IO.Path]::GetFileName($archiveName)) {
        throw 'Native lock archive and DLL names must be single file names.'
    }
    $destinationRelative = ConvertTo-HandyNativeRelativePath $Specification.destination
    $destination = Assert-HandyNativeContainedPath $root (Join-Path $root $destinationRelative)
    if ([IO.Path]::GetFileName($destination) -cne $dllName) { throw 'Native destination file name must match the locked DLL.' }
    if ($EvidencePath) { $EvidencePath = Assert-HandyNativeContainedPath $root $EvidencePath }
    # Validate even when an existing DLL can be reused; no foreign hint may bypass ownership.
    $cacheProject = Get-HandyCacheProjectRoot $root
    $cacheRoot = Assert-HandyNativeContainedPath $cacheProject (Join-Path $cacheProject ".harness-cache/windows-x64/native/$($Specification.archiveSha256)")
    $scratchRoot = Assert-HandyNativeContainedPath $cacheProject (Join-Path $cacheProject '.harness-cache/windows-x64/scratch')
    $attemptRoot = Assert-HandyNativeContainedPath $root (Join-Path $root ".harness/native/$($Specification.archiveSha256)")
    $receipt = [ordered]@{
        schemaVersion = 1; name = [string]$Specification.name; version = [string]$Specification.version
        architecture = 'win-x64'; sourceUrl = [string]$Specification.sourceUrl; sourcePage = [string]$Specification.sourcePage
        archiveSha256 = [string]$Specification.archiveSha256; archiveBytes = [long]$Specification.archiveBytes
        dllSha256 = [string]$Specification.dllSha256; dllBytes = [long]$Specification.dllBytes
        peMachine = '0x8664'; sourceKind = ''; destination = $destinationRelative.Replace('\', '/')
    }
    if (Test-Path -LiteralPath $destination) {
        Assert-HandyNativeFile $destination $Specification.dllBytes $Specification.dllSha256 -PeX64
        $receipt.sourceKind = 'existing-destination'
        if ($EvidencePath) { Write-HandyNativeReceipt $root $EvidencePath $receipt }
        return [pscustomobject]$receipt
    }
    New-HandyNativeDirectory $root $attemptRoot
    $attempt = Join-Path $attemptRoot ('attempt-' + [guid]::NewGuid().ToString('N'))
    New-HandyNativeDirectory $root $attempt
    $stage = 'archive-validation'
    $cacheLock = $null
    try {
        New-HandyNativeDirectory $cacheProject $cacheRoot
        New-HandyNativeDirectory $cacheProject $scratchRoot
        $lockPath = Join-Path $scratchRoot ("native-$($Specification.archiveSha256).lock")
        Assert-HandyNativeNoLink $lockPath
        $lockDeadline = [DateTime]::UtcNow.AddSeconds(60)
        while (-not $cacheLock) {
            try { $cacheLock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
            catch [IO.IOException] {
                if ([DateTime]::UtcNow -ge $lockDeadline) { throw 'Native archive cache is busy; preserve the owning request and retry after it finishes.' }
                Start-Sleep -Milliseconds 50
            }
        }
        $archive = Join-Path $cacheRoot $archiveName
        Assert-HandyNativeNoLink $archive
        if (Test-Path -LiteralPath $archive) {
            Assert-HandyNativeFile $archive $Specification.archiveBytes $Specification.archiveSha256
            $receipt.sourceKind = 'warm-cache'
        } else {
            $partial = Join-Path $attempt ($archiveName + '.partial')
            $candidate = $null
            # Reuse old verified download locations without moving their attempts or receipts.
            $legacy = @((Join-Path $cacheProject ".harness/native/$($Specification.archiveSha256)/$archiveName"),
                (Join-Path $root ".harness/native/$($Specification.archiveSha256)/$archiveName"))
            foreach ($path in @($ArchiveCandidates) + $legacy) {
                if (-not [IO.Path]::IsPathFullyQualified($path)) { throw 'Native archive candidate paths must be absolute.' }
                Assert-HandyNativeNoLink $path
                if (-not (Test-Path -LiteralPath $path)) { continue }
                Assert-HandyNativeFile $path $Specification.archiveBytes $Specification.archiveSha256
                $candidate = $path
                break
            }
            if ($candidate) {
                $stage = 'candidate-copy'
                [IO.File]::Copy($candidate, $partial, $false)
                $receipt.sourceKind = 'candidate-archive'
            } else {
                $stage = 'download'
                Invoke-HandyNativeDownload $Specification.sourceUrl $partial $Specification.archiveBytes
                $receipt.sourceKind = 'download'
            }
            $stage = 'archive-validation'
            Assert-HandyNativeFile $partial $Specification.archiveBytes $Specification.archiveSha256
            Assert-HandyNativeNoLink $archive
            [IO.File]::Move($partial, $archive, $false)
        }
        $stage = 'archive-listing'
        $sevenZip = Get-HandyNative7Zip
        $listing = Invoke-HandyNative7Zip $sevenZip @('l', '-slt', '--', $archive)
        $member = Get-HandyNativeArchiveMember $listing $dllName
        $stage = 'extraction'
        $extract = Join-Path $attempt 'extracted'
        New-HandyNativeDirectory $root $extract
        $null = Invoke-HandyNative7Zip $sevenZip @('x', '-y', '-spd', '-mmt=2', "-o$extract", '--', $archive, $member)
        $dll = Join-Path $extract $dllName
        Assert-HandyNativeFile $dll $Specification.dllBytes $Specification.dllSha256 -PeX64
        $stage = 'destination-copy'
        New-HandyNativeDirectory $root ([IO.Path]::GetDirectoryName($destination))
        $stagedDll = Join-Path $attempt 'validated-dll.partial'
        Assert-HandyNativeNoLink $destination
        [IO.File]::Copy($dll, $stagedDll, $false)
        Assert-HandyNativeFile $stagedDll $Specification.dllBytes $Specification.dllSha256 -PeX64
        Assert-HandyNativeNoLink $destination
        [IO.File]::Move($stagedDll, $destination, $false)
        Assert-HandyNativeFile $destination $Specification.dllBytes $Specification.dllSha256 -PeX64
        $stage = 'receipt'
        Write-HandyNativeReceipt $root (Join-Path $attempt 'receipt.json') $receipt
        if ($EvidencePath) { Write-HandyNativeReceipt $root $EvidencePath $receipt }
        return [pscustomobject]$receipt
    } catch {
        # Retain partial downloads and extracted files. Do not log native/network output.
        $failure = [ordered]@{ status = 'failed'; stage = $stage; version = [string]$Specification.version
            sourceUrl = [string]$Specification.sourceUrl; archiveSha256 = [string]$Specification.archiveSha256
            dllSha256 = [string]$Specification.dllSha256; destination = $destination }
        Write-HandyNativeReceipt $root (Join-Path $attempt 'failure.json') $failure
        throw
    } finally {
        if ($cacheLock) { $cacheLock.Dispose() }
    }
}
