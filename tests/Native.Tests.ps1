#Requires -Version 7
# Offline contract tests: synthetic archives/PE files; no SDK, network or installed extractor.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $repositoryRoot 'eng/Native-Dependency.ps1')
$fixtureRoot = Join-Path $repositoryRoot ('.harness/native-tests-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($fixtureRoot)
$emptyGit = Join-Path $fixtureRoot 'empty-git-template'
$null = [IO.Directory]::CreateDirectory($emptyGit)
$script:caseCount = 0
$script:archiveBytes = [Text.Encoding]::UTF8.GetBytes('offline-reviewed-archive-fixture')
$script:dllBytes = [byte[]]::new(512)
$script:dllBytes[0] = 0x4d; $script:dllBytes[1] = 0x5a
[BitConverter]::GetBytes([uint32]128).CopyTo($script:dllBytes, 0x3c)
[BitConverter]::GetBytes([uint32]0x4550).CopyTo($script:dllBytes, 128)
[BitConverter]::GetBytes([uint16]0x8664).CopyTo($script:dllBytes, 132)
[BitConverter]::GetBytes([uint16]0x2000).CopyTo($script:dllBytes, 150)
$script:spec = [pscustomobject]@{
    name = 'libmpv'; version = 'offline-test'; sourceUrl = 'https://example.invalid/reviewed.7z'
    sourcePage = 'https://example.invalid/reviewed.7z/download'; archiveFileName = 'reviewed.7z'
    archiveSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($script:archiveBytes)).ToLowerInvariant()
    archiveBytes = $script:archiveBytes.Length; dllFileName = 'libmpv-2.dll'
    dllSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($script:dllBytes)).ToLowerInvariant()
    dllBytes = $script:dllBytes.Length; peMachine = '0x8664'
    destination = 'src/media/runtimes/win-x64/native/libmpv-2.dll'
}

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_ }
    Assert ($null -ne $caught) "Expected failure matching '$Pattern'."
    Assert ($caught.Exception.Message -like $Pattern) "Wrong failure: $($caught.Exception.Message)"
    Assert ($caught.Exception.Message -notlike '*mock-sensitive-output*') 'Failure leaked extractor output.'
}
function Get-Listing([string[]]$Members = @('libmpv-2.dll')) {
    $records = foreach ($member in $Members) { "Path = $member`nSize = 512`nAttributes = A`n" }
    return "7-Zip offline fixture`nPath = C:\ignored\archive-header.7z`nType = 7z`n`n----------`n" + ($records -join "`n")
}
function Reset-NativeMock {
    $script:listing = Get-Listing
    $script:extractedBytes = $script:dllBytes.Clone()
    $script:downloadBytes = $script:archiveBytes.Clone()
    $script:downloadFailure = $false
    $script:extractFailure = $false
    $script:listFailure = $false
    $script:downloads = 0
    $script:extractCalls = 0
    $global:LASTEXITCODE = 0
}
function Get-HandyNative7Zip { return 'Invoke-NativeTest7Zip' }
function Invoke-NativeTest7Zip {
    $argv = @($args)
    $global:LASTEXITCODE = 0
    Assert ($argv -contains '--') 'Extractor must stop option parsing before archive/member paths.'
    if ($argv[0] -eq 'l') {
        Assert ($argv -contains '-slt') 'Listing must use structured member metadata.'
        if ($script:listFailure) { $global:LASTEXITCODE = 9; return 'mock-sensitive-output' }
        return $script:listing
    }
    Assert ($argv[0] -eq 'x' -and $argv[-1] -ceq $script:spec.dllFileName) 'Extraction must select only the root DLL.'
    Assert ($argv -contains '-spd') 'Extraction must disable wildcard matching.'
    $script:extractCalls++
    $out = @($argv | Where-Object { $_ -like '-o*' })[0].Substring(2)
    [IO.File]::WriteAllBytes((Join-Path $out $script:spec.dllFileName), $script:extractedBytes)
    if ($script:extractFailure) { $global:LASTEXITCODE = 2; return 'mock-sensitive-output' }
    return 'mock-success-output must not escape the receipt'
}
function Invoke-HandyNativeDownload {
    param([string]$Url, [string]$Path, [long]$MaximumBytes)
    Assert ($Url -ceq $script:spec.sourceUrl) 'Download URL must match the lock exactly.'
    Assert ($MaximumBytes -eq $script:spec.archiveBytes) 'Download limit must match the lock.'
    $script:downloads++
    [IO.File]::WriteAllBytes($Path, $script:downloadBytes)
    if ($script:downloadFailure) { throw 'Mock HTTPS download failed; partial evidence retained.' }
}
function Invoke-NativeFixtureGit([string]$Directory, [string[]]$Arguments) {
    & git -c "init.templateDir=$emptyGit" -c "core.hooksPath=$emptyGit" -c filter.lfs.process= `
        -c filter.lfs.smudge= -c filter.lfs.required=false -C $Directory @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Isolated native Git fixture failed.' }
}
function New-Checkout([string]$Name) {
    Reset-NativeMock
    $directory = Join-Path $fixtureRoot $Name
    $null = [IO.Directory]::CreateDirectory($directory)
    Invoke-NativeFixtureGit $directory @('init', '--quiet')
    return $directory
}
function Set-WarmArchive([string]$Checkout) {
    $cache = Join-Path $Checkout ".harness-cache/windows-x64/native/$($script:spec.archiveSha256)"
    $null = [IO.Directory]::CreateDirectory($cache)
    $path = Join-Path $cache $script:spec.archiveFileName
    [IO.File]::WriteAllBytes($path, $script:archiveBytes)
    return $path
}
function Set-Destination([string]$Checkout, [byte[]]$Bytes) {
    $path = Join-Path $Checkout $script:spec.destination
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllBytes($path, $Bytes)
    return $path
}
function Assert-FailureEvidence([string]$Checkout, [string]$Stage) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $Checkout '.harness/native') -Filter failure.json -Recurse -File)
    Assert ($files.Count -eq 1) 'Failure must retain one fresh attempt receipt.'
    $receipt = Get-Content -LiteralPath $files[0].FullName -Raw | ConvertFrom-Json
    Assert ($receipt.stage -ceq $Stage) "Wrong failure stage: $($receipt.stage)"
    Assert ($receipt.archiveSha256 -ceq $script:spec.archiveSha256) 'Failure must retain locked provenance.'
    return $files[0].DirectoryName
}
function Case([string]$Name, [scriptblock]$Action) {
    & $Action
    $script:caseCount++
    Write-Host "PASS $Name"
}

Case 'identical destination reused without archive/network/extractor' {
    $checkout = New-Checkout 'existing'
    $null = Set-Destination $checkout $script:dllBytes
    $receiptPath = Join-Path $checkout '.harness/request/native-dependency.json'
    $results = @(Restore-HandyNativeDependency $checkout $script:spec -EvidencePath $receiptPath)
    Assert ($results.Count -eq 1 -and $results[0].sourceKind -ceq 'existing-destination') 'Restore must output exactly one receipt.'
    Assert ($results[0].destination -ceq $script:spec.destination) 'Receipt destination must be portable and checkout-relative.'
    $null = Restore-HandyNativeDependency $checkout $script:spec -EvidencePath $receiptPath
    Assert ($script:downloads -eq 0 -and $script:extractCalls -eq 0) 'Existing DLL reuse touched downloader/extractor.'
}
Case 'changed destination is preserved' {
    $checkout = New-Checkout 'existing-mismatch'
    $path = Set-Destination $checkout ([byte[]]@(1, 2, 3))
    Assert-Throws { Restore-HandyNativeDependency $checkout $script:spec } '*checksum or size mismatch*'
    Assert ([IO.File]::ReadAllBytes($path).Length -eq 3 -and $script:downloads -eq 0) 'Unknown existing DLL was modified.'
}
Case 'warm verified archive reused and final DLL verified' {
    $checkout = New-Checkout 'warm'
    $archive = Set-WarmArchive $checkout
    $result = @(Restore-HandyNativeDependency $checkout $script:spec)
    Assert ($result.Count -eq 1 -and $result[0].sourceKind -ceq 'warm-cache') 'Warm archive source kind is incorrect.'
    Assert ($script:downloads -eq 0 -and $script:extractCalls -eq 1) 'Warm archive unexpectedly downloaded.'
    Assert-HandyNativeFile (Join-Path $checkout $script:spec.destination) $script:spec.dllBytes $script:spec.dllSha256 -PeX64
    Assert-HandyNativeFile $archive $script:spec.archiveBytes $script:spec.archiveSha256
}
Case 'two real independent clones reuse one verified primary archive and isolate extraction' {
    $primary = New-Checkout 'canonical-archive-primary'
    $canonicalOrigin = 'https://github.com/Belgian-Coder/HandyPlayer.git'
    Invoke-NativeFixtureGit $primary @('config', 'remote.origin.url', $canonicalOrigin)
    $null = [IO.Directory]::CreateDirectory((Join-Path $primary 'eng'))
    @{ schemaVersion=1; project='HandyPlayer'; worktreeDirectory='.wt'; targets=@{ 'win-x64'=@{}; 'win-x64-second'=@{} } } |
        ConvertTo-Json -Depth 4 | Set-Content (Join-Path $primary 'eng/release-targets.json')
    Invoke-NativeFixtureGit $primary @('add', 'eng/release-targets.json')
    Invoke-NativeFixtureGit $primary @('-c', 'user.name=Cache Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '--quiet', '-m', 'Native cache fixture')
    $oldHint = $env:BUILD_RELEASE_PROJECT_ROOT
    try {
        $env:BUILD_RELEASE_PROJECT_ROOT = $primary
        foreach ($target in @('win-x64', 'win-x64-second')) {
            $checkout = Join-Path $primary ".wt/release-$target"
            Invoke-NativeFixtureGit $primary @('clone', '--quiet', '--no-hardlinks', "--template=$emptyGit", $primary, $checkout)
            Invoke-NativeFixtureGit $checkout @('config', 'remote.origin.url', $canonicalOrigin)
            @{owner='request-release';version=1;project='HandyPlayer';target=$target;root=$primary} |
                ConvertTo-Json | Set-Content (Join-Path $checkout '.request-release-owner.json')
            Reset-NativeMock
            $result = Restore-HandyNativeDependency $checkout $script:spec
            if ($target -eq 'win-x64') {
                Assert ($result.sourceKind -ceq 'download' -and $script:downloads -eq 1) 'First clone did not fill the primary archive cache.'
            } else {
                Assert ($result.sourceKind -ceq 'warm-cache' -and $script:downloads -eq 0) 'Second clone redownloaded the immutable native archive.'
            }
            Assert (@(Get-ChildItem -LiteralPath (Join-Path $checkout '.harness/native') -Filter receipt.json -Recurse).Count -eq 1) 'Extraction/receipt escaped its checkout.'
            Assert (-not (Test-Path (Join-Path $checkout '.harness-cache/windows-x64/native'))) 'Independent clone created a private archive cache.'
        }
        Assert-HandyNativeFile (Join-Path $primary ".harness-cache/windows-x64/native/$($script:spec.archiveSha256)/$($script:spec.archiveFileName)") $script:spec.archiveBytes $script:spec.archiveSha256
        Assert (-not (Test-Path (Join-Path $primary '.harness/native'))) 'Primary received mutable extraction output.'
    } finally { $env:BUILD_RELEASE_PROJECT_ROOT = $oldHint }
}
Case 'legacy verified archive is imported without moving old evidence' {
    $checkout = New-Checkout 'legacy-cache'
    $legacyRoot = Join-Path $checkout ".harness/native/$($script:spec.archiveSha256)"
    $null = [IO.Directory]::CreateDirectory($legacyRoot)
    $legacy = Join-Path $legacyRoot $script:spec.archiveFileName
    [IO.File]::WriteAllBytes($legacy, $script:archiveBytes)
    $result = Restore-HandyNativeDependency $checkout $script:spec
    Assert ($result.sourceKind -ceq 'candidate-archive' -and $script:downloads -eq 0) 'Verified legacy download was not reused.'
    Assert-HandyNativeFile $legacy $script:spec.archiveBytes $script:spec.archiveSha256
}
Case 'verified candidate archive copied without mutation' {
    $checkout = New-Checkout 'candidate'
    $supplier = Join-Path $fixtureRoot 'supplier.7z'
    [IO.File]::WriteAllBytes($supplier, $script:archiveBytes)
    $result = Restore-HandyNativeDependency $checkout $script:spec -ArchiveCandidates @($supplier)
    Assert ($result.sourceKind -ceq 'candidate-archive' -and $script:downloads -eq 0) 'Candidate was not reused.'
    Assert-HandyNativeFile $supplier $script:spec.archiveBytes $script:spec.archiveSha256
}
Case 'mismatching warm archive fails without overwrite or fallback' {
    $checkout = New-Checkout 'bad-warm'
    $archive = Set-WarmArchive $checkout
    [IO.File]::WriteAllBytes($archive, [byte[]]@(9, 8, 7))
    Assert-Throws { Restore-HandyNativeDependency $checkout $script:spec } '*checksum or size mismatch*'
    Assert ([IO.File]::ReadAllBytes($archive).Length -eq 3 -and $script:downloads -eq 0) 'Mismatching warm cache was changed.'
    $null = Assert-FailureEvidence $checkout 'archive-validation'
}
Case 'downloaded checksum mismatch retains partial evidence' {
    $checkout = New-Checkout 'bad-download-hash'
    $script:downloadBytes[0] = 0xff
    Assert-Throws { Restore-HandyNativeDependency $checkout $script:spec } '*checksum or size mismatch*'
    $attempt = Assert-FailureEvidence $checkout 'archive-validation'
    Assert (Test-Path -LiteralPath (Join-Path $attempt 'reviewed.7z.partial')) 'Mismatching download evidence was removed.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $checkout $script:spec.destination))) 'Unverified DLL was installed.'
}
Case 'successful download produces exactly one provenance receipt' {
    $checkout = New-Checkout 'download'
    $result = @(Restore-HandyNativeDependency $checkout $script:spec)
    Assert ($result.Count -eq 1 -and $result[0].sourceKind -ceq 'download' -and $script:downloads -eq 1) 'Download output contract failed.'
    Assert ($result[0].sourceUrl -ceq $script:spec.sourceUrl -and $result[0].dllSha256 -ceq $script:spec.dllSha256) 'Receipt omitted provenance.'
}
Case 'bounded streaming rejects excess bytes before writing excess' {
    $inputStream = [IO.MemoryStream]::new([byte[]]@(1, 2, 3, 4))
    $outputStream = [IO.MemoryStream]::new()
    try {
        Assert-Throws { Copy-HandyNativeLimitedStream $inputStream $outputStream 3 } '*exceeded the locked size*'
        Assert ($outputStream.Length -le 3) 'Downloader wrote beyond its locked maximum.'
    } finally { $inputStream.Dispose(); $outputStream.Dispose() }
}
Case 'archive header Path ignored and safe nested files accepted' {
    $listing = (Get-Listing @('include', 'include/mpv.h', 'libmpv-2.dll')).Replace('C:\ignored\archive-header.7z', '..\not-a-member')
    Assert ((Get-HandyNativeArchiveMember $listing $script:spec.dllFileName) -ceq 'libmpv-2.dll') 'Archive header was interpreted as a member.'
}
Case 'unsafe archive members rejected before extraction' {
    foreach ($member in @('../escape', 'dir/../escape', '/absolute', '\\server\share\x', 'C:\escape', 'file:stream', 'NUL.txt', 'dir./file')) {
        $checkout = New-Checkout ('unsafe-member-' + [guid]::NewGuid().ToString('N'))
        $null = Set-WarmArchive $checkout
        $script:listing = Get-Listing @($member, 'libmpv-2.dll')
        Assert-Throws { Restore-HandyNativeDependency $checkout $script:spec } '*Unsafe*'
        Assert ($script:extractCalls -eq 0) 'Unsafe archive reached extraction.'
        $null = Assert-FailureEvidence $checkout 'archive-listing'
    }
}
Case 'duplicate DLL and case-colliding members rejected' {
    Assert-Throws { Get-HandyNativeArchiveMember (Get-Listing @('libmpv-2.dll', 'LIBMPV-2.DLL')) 'libmpv-2.dll' } '*Duplicate archive member*'
    Assert-Throws { Get-HandyNativeArchiveMember (Get-Listing @('include/mpv.h', 'INCLUDE/mpv.h', 'libmpv-2.dll')) 'libmpv-2.dll' } '*Duplicate archive member*'
}
Case 'missing or nested DLL and missing member section rejected' {
    Assert-Throws { Get-HandyNativeArchiveMember (Get-Listing @('include/mpv.h')) 'libmpv-2.dll' } '*exactly one root*'
    Assert-Throws { Get-HandyNativeArchiveMember (Get-Listing @('nested/libmpv-2.dll')) 'libmpv-2.dll' } '*exactly one root*'
    Assert-Throws { Get-HandyNativeArchiveMember 'Path = libmpv-2.dll' 'libmpv-2.dll' } '*member section is missing*'
}
Case 'symlink hardlink and Unix link mode metadata rejected' {
    foreach ($metadata in @('Symbolic Link = target', 'Hard Link = target', 'Attributes = A_ lrwxrwxrwx', 'Mode = lrwxrwxrwx')) {
        $listing = "Path = ignored.7z`n----------`nPath = libmpv-2.dll`n$metadata`n"
        Assert-Throws { Get-HandyNativeArchiveMember $listing 'libmpv-2.dll' } '*links and reparse members*'
    }
}
Case 'wrong PE machine rejected even when hash and size match lock' {
    $checkout = New-Checkout 'wrong-machine'
    $null = Set-WarmArchive $checkout
    [BitConverter]::GetBytes([uint16]0x14c).CopyTo($script:extractedBytes, 132)
    $wrongSpec = $script:spec | Select-Object *
    $wrongSpec.dllSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($script:extractedBytes)).ToLowerInvariant()
    Assert-Throws { Restore-HandyNativeDependency $checkout $wrongSpec } '*valid x64 PE header*'
    Assert (-not (Test-Path -LiteralPath (Join-Path $checkout $script:spec.destination))) 'Wrong-machine DLL was installed.'
    $null = Assert-FailureEvidence $checkout 'extraction'
}
Case 'invalid PE offsets and non-DLL executables rejected' {
    $checkout = New-Checkout 'bad-pe'
    foreach ($kind in @('offset', 'not-dll')) {
        $bytes = $script:dllBytes.Clone()
        if ($kind -eq 'offset') { [BitConverter]::GetBytes([uint32]4294967280).CopyTo($bytes, 0x3c) }
        else { [BitConverter]::GetBytes([uint16]0).CopyTo($bytes, 150) }
        $path = Join-Path $checkout "$kind.dll"
        [IO.File]::WriteAllBytes($path, $bytes)
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        Assert-Throws { Assert-HandyNativeFile $path $bytes.Length $hash -PeX64 } '*Native*'
    }
}
Case 'download and extractor failures retain evidence and hide raw output' {
    $checkout = New-Checkout 'download-failure'
    $script:downloadFailure = $true
    Assert-Throws { Restore-HandyNativeDependency $checkout $script:spec } '*HTTPS download failed*'
    $attempt = Assert-FailureEvidence $checkout 'download'
    Assert (Test-Path -LiteralPath (Join-Path $attempt 'reviewed.7z.partial')) 'Failed download partial was discarded.'
    $checkout = New-Checkout 'extract-failure'
    $null = Set-WarmArchive $checkout
    $script:extractFailure = $true
    Assert-Throws { Restore-HandyNativeDependency $checkout $script:spec } '*7-Zip failed*exit 2*'
    $attempt = Assert-FailureEvidence $checkout 'extraction'
    Assert (Test-Path -LiteralPath (Join-Path $attempt 'extracted/libmpv-2.dll')) 'Failed extraction evidence was discarded.'
    $checkout = New-Checkout 'list-failure'
    $null = Set-WarmArchive $checkout
    $script:listFailure = $true
    Assert-Throws { Restore-HandyNativeDependency $checkout $script:spec } '*7-Zip failed*exit 9*'
    Assert ($script:extractCalls -eq 0) 'Failed listing reached extraction.'
}
Case 'unsafe destination and receipt escape fail without writes' {
    $checkout = New-Checkout 'unsafe-destination'
    foreach ($path in @('../libmpv-2.dll', 'C:\libmpv-2.dll', '/libmpv-2.dll', 'src:stream/libmpv-2.dll')) {
        $badSpec = $script:spec | Select-Object *
        $badSpec.destination = $path
        Assert-Throws { Restore-HandyNativeDependency $checkout $badSpec } '*Unsafe*'
    }
    Assert-Throws { Restore-HandyNativeDependency $checkout $script:spec -EvidencePath (Join-Path $fixtureRoot 'escaped.json') } '*remain inside CheckoutRoot*'
    Assert (-not (Test-Path -LiteralPath (Join-Path $checkout '.harness'))) 'Invalid paths caused cache writes.'
}
Case 'junction destinations cache paths and receipt paths rejected' {
    $target = Join-Path $fixtureRoot 'junction-target'
    $null = [IO.Directory]::CreateDirectory($target)
    foreach ($kind in @('destination', 'cache', 'receipt', 'checkout')) {
        $checkout = New-Checkout ('junction-' + $kind)
        $link = switch ($kind) {
            'destination' { Join-Path $checkout 'src' }
            'cache' { Join-Path $checkout '.harness' }
            'receipt' { Join-Path $checkout 'request' }
            'checkout' { Join-Path $checkout 'redirected' }
        }
        $null = New-Item -ItemType Junction -Path $link -Target $target
        if ($kind -eq 'checkout') {
            Assert-Throws { Restore-HandyNativeDependency $link $script:spec } '*Links and reparse points*'
        } elseif ($kind -eq 'receipt') {
            Assert-Throws { Restore-HandyNativeDependency $checkout $script:spec -EvidencePath (Join-Path $link 'receipt.json') } '*Links and reparse points*'
        } else { Assert-Throws { Restore-HandyNativeDependency $checkout $script:spec } '*Links and reparse points*' }
    }
    Assert (@(Get-ChildItem -LiteralPath $target -Force).Count -eq 0) 'Restore wrote through a redirected path.'
}
Case 'changed receipt preserved without overwrite' {
    $checkout = New-Checkout 'receipt-mismatch'
    $null = Set-Destination $checkout $script:dllBytes
    $receipt = Join-Path $checkout '.harness/request/receipt.json'
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($receipt))
    [IO.File]::WriteAllText($receipt, 'unknown receipt')
    Assert-Throws { Restore-HandyNativeDependency $checkout $script:spec -EvidencePath $receipt } '*receipt differs*'
    Assert ([IO.File]::ReadAllText($receipt) -ceq 'unknown receipt') 'Unknown existing receipt was overwritten.'
}

Write-Host "Native dependency offline contract tests passed: $script:caseCount cases. Fixtures retained under $fixtureRoot"
