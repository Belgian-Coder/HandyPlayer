#Requires -Version 7
<#
.SYNOPSIS
    Runs canonical validation/packaging for an exact committed native checkout.
    The remote request client owns transport and scheduling; upload creates GitHub drafts only.
#>
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'osx-arm64', 'linux-x64')][string]$Target,
    [ValidateSet('plan', 'validate', 'package', 'upload')][string]$Mode = 'plan',
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedSha,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Candidate', 'Full')][string]$PackageProfile = 'Full',
    [ValidateRange(1, 64)][int]$MaxParallelism = 2
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$PackageProfile = if ($PackageProfile -ieq 'Candidate') { 'Candidate' } else { 'Full' }
if ($Target -ne 'win-x64' -and $PackageProfile -ne 'Full') { throw 'Candidate packaging profile applies only to Windows.' }
$root = Split-Path $PSScriptRoot -Parent
$validation = @{
    executable = 'pwsh'
    arguments = @('-NoProfile', '-NonInteractive', '-File', './validate.ps1', '-Coverage', '-MaxParallelism', "$MaxParallelism")
}
$packaging = switch ($Target) {
    'win-x64' { @{
        executable = 'pwsh'
        arguments = @('-NoProfile', '-NonInteractive', '-File', './publish-win.ps1', '-RequireMsi', '-PackageProfile', $PackageProfile, '-MaxParallelism', "$MaxParallelism")
    } }
    'osx-arm64' { @{ executable = 'bash'; arguments = @('./publish-mac.sh', 'Release') } }
    'linux-x64' { @{ executable = 'bash'; arguments = @('./publish-linux.sh', 'Release') } }
}

if ($Mode -eq 'plan') {
    [ordered]@{
        schemaVersion = 1; project = 'HandyPlayer'; target = $Target; sourceSha = $ExpectedSha.ToLowerInvariant()
        outputDirectory = $OutputDirectory; validation = $validation; packaging = $packaging
        packageProfile = $PackageProfile
        uploadPolicy = 'draft-only'; repository = 'Belgian-Coder/HandyPlayer'
        nativeAcceptance = 'required separately; not performed by this adapter'
    } | ConvertTo-Json -Depth 6
    exit 0
}

Set-Location $root
function Assert-Source {
    $actual = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $actual -ne $ExpectedSha) { throw 'Checkout HEAD does not match the requested commit.' }
    $changes = & git status --porcelain=v1 --untracked-files=all
    if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Release adapter requires a clean committed checkout; preserve WIP in its original worktree.' }
}

Assert-Source
$nativePlatform = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
$nativeArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
if ($Target -ne "$nativePlatform-$nativeArchitecture" -or $processArchitecture -ne $nativeArchitecture) {
    throw "Target $Target requires a matching native host and PowerShell process; current host is $nativePlatform-$nativeArchitecture ($processArchitecture process)."
}

. "$root/harness-env.ps1"

function Get-CanonicalNativeFile([string]$Path) {
    $currentPath = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    for ($depth = 0; $depth -lt 32; $depth++) {
        $item = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop
        if ($item.PSIsContainer) { throw "Native dependency path is not a file: $Path" }
        if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $item }
        $target = $item.ResolveLinkTarget($true)
        if (-not $target) { throw "Could not resolve native dependency link: $Path" }
        $currentPath = $target.FullName
    }
    throw "Native dependency link chain is too deep: $Path"
}

function Assert-ReleaseNativeLoaderOverrides([string]$ReleaseTarget) {
    $names = switch -Regex ($ReleaseTarget) {
        '^osx-' { @('DYLD_LIBRARY_PATH', 'DYLD_FALLBACK_LIBRARY_PATH', 'DYLD_INSERT_LIBRARIES',
            'DYLD_FRAMEWORK_PATH', 'DYLD_FALLBACK_FRAMEWORK_PATH', 'DYLD_ROOT_PATH',
            'DYLD_VERSIONED_LIBRARY_PATH', 'DYLD_VERSIONED_FRAMEWORK_PATH', 'DYLD_IMAGE_SUFFIX'); break }
        '^linux-' { @('LD_LIBRARY_PATH', 'LD_PRELOAD'); break }
        default { @() }
    }
    foreach ($name in $names) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($null -ne $value -and $value.Length -gt 0) {
            throw "Release adapter rejects nonempty $name because it can override native-library resolution; unset it in the invoking process or rerun in an isolated child process with it unset."
        }
    }
}

function Get-ReleaseEnvironmentIdentity {
    param([string]$RepositoryRoot, [string]$ReleaseTarget)

    Assert-ReleaseNativeLoaderOverrides $ReleaseTarget
    $dotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $dotnet) { throw 'The resolved .NET SDK is unavailable; install the repository-pinned SDK before validation or receipt reuse.' }
    $sdkOutput = @(& $dotnet.Source --version 2>&1)
    $sdkExitCode = $LASTEXITCODE
    $sdkLines = @($sdkOutput | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
    if ($sdkExitCode -ne 0 -or $sdkLines.Count -ne 1) {
        throw "Could not resolve one .NET SDK version using '$($dotnet.Source) --version'."
    }

    $packagingNativeInput = $null
    if ($ReleaseTarget -eq 'win-x64') {
        $lockPath = Join-Path $RepositoryRoot 'eng/native-dependencies.json'
        $lockItem = Get-Item -LiteralPath $lockPath -Force -ErrorAction Stop
        if ($lockItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Native dependency lockfile must not be a link.' }
        $lockSha256 = (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
        if ($lock.schemaVersion -ne 1) { throw 'Windows native dependency lockfile schema is unsupported.' }
        $spec = $lock.PSObject.Properties['win-x64'].Value
        if (-not $spec -or $spec.name -ne 'libmpv' -or $spec.dllFileName -ne 'libmpv-2.dll' -or
            $spec.dllSha256 -notmatch '^[0-9a-fA-F]{64}$' -or $spec.destination -notmatch '^src/HandyPlaylistPlayer\.Media\.Mpv/runtimes/win-x64/native/libmpv-2\.dll$') {
            throw 'Windows native dependency lockfile does not contain the reviewed libmpv pin.'
        }
        $relativePath = $spec.destination.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $nativePath = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $relativePath))
        $rootPrefix = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $nativePath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Windows native dependency path escaped the checkout.' }
        $nativeItem = Get-Item -LiteralPath $nativePath -Force -ErrorAction Stop
        if ($nativeItem.PSIsContainer -or ($nativeItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Windows native dependency must be a regular file.' }
        $nativeSha256 = (Get-FileHash -LiteralPath $nativePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($nativeSha256 -ne $spec.dllSha256.ToLowerInvariant() -or $nativeItem.Length -ne [long]$spec.dllBytes) {
            throw 'Windows native libmpv file does not match the reviewed eng/native-dependencies.json pin.'
        }
        $packagingNativeInput = [ordered]@{
            kind = 'reviewed-repository-pin'; name = [string]$spec.name; version = [string]$spec.version
            source = 'eng/native-dependencies.json'; path = $spec.destination
            bytes = [long]$nativeItem.Length; sha256 = $nativeSha256; lockFileSha256 = $lockSha256
        }
    } elseif ($ReleaseTarget -eq 'osx-arm64') {
        $runtimePath = Join-Path $RepositoryRoot 'src/HandyPlaylistPlayer.Media.Mpv/runtimes/osx-arm64/native/libmpv.2.dylib'
        $brewLib = $null
        $brew = Get-Command brew -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($brew) {
            $brewPrefixOutput = @(& $brew.Source --prefix 2>$null)
            $brewExitCode = $LASTEXITCODE
            $brewPrefixLines = @($brewPrefixOutput | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
            if ($brewExitCode -eq 0 -and $brewPrefixLines.Count -eq 1) {
                $candidate = Join-Path $brewPrefixLines[0] 'lib/libmpv.2.dylib'
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $brewLib = $candidate }
            }
        }
        # publish-mac.sh copies this Homebrew file over the app's RID copy when
        # available; otherwise the RID copy is the app-local resolver's first hit.
        if ($brewLib) {
            $nativePath = $brewLib
            $nativeSource = 'homebrew-mpv'
        } elseif (Test-Path -LiteralPath $runtimePath -PathType Leaf) {
            $nativePath = $runtimePath
            $nativeSource = 'repository-runtime'
        } else {
            # MpvInterop explicitly searches these directories after app-local lookup.
            $nativePath = $null
            foreach ($directory in @('/opt/homebrew/lib', '/usr/local/lib')) {
                foreach ($name in @('libmpv.2.dylib', 'libmpv.dylib')) {
                    $candidate = Join-Path $directory $name
                    if (Test-Path -LiteralPath $candidate -PathType Leaf) { $nativePath = $candidate; break }
                }
                if ($nativePath) { break }
            }
            if (-not $nativePath) { throw 'Could not identify libmpv for macOS: no publisher Homebrew library, repository runtime copy, or MpvInterop resolver path was found.' }
            $nativeSource = 'system-resolver-path'
        }
        $nativeItem = Get-CanonicalNativeFile $nativePath
        $resolvedPath = $nativeItem.FullName
        $nativeSha256 = (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $packagingNativeInput = [ordered]@{
            kind = $nativeSource; name = 'libmpv'; version = $null; source = $nativeSource
            path = $resolvedPath; bytes = [long]$nativeItem.Length; sha256 = $nativeSha256; lockFileSha256 = $null
        }
    } else {
        $runtimePath = Join-Path $RepositoryRoot 'src/HandyPlaylistPlayer.Media.Mpv/runtimes/linux-x64/native/libmpv.so.2'
        $nativePath = if (Test-Path -LiteralPath $runtimePath -PathType Leaf) { $runtimePath } else { $null }
        $nativeSource = 'repository-runtime'
        if (-not $nativePath) {
            $ldconfig = Get-Command ldconfig -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($ldconfig) {
                $ldconfigOutput = @(& $ldconfig.Source -p 2>$null)
                if ($LASTEXITCODE -eq 0) {
                    foreach ($line in $ldconfigOutput) {
                        if ([string]$line -match '^\s*libmpv\.so\.2\s+\([^)]*\)\s+=>\s+(.+?)\s*$') {
                            $candidate = $Matches[1]
                            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $nativePath = $candidate; break }
                        }
                    }
                }
            }
            if (-not $nativePath) {
                foreach ($directory in @('/lib/x86_64-linux-gnu', '/usr/lib/x86_64-linux-gnu', '/usr/local/lib', '/lib', '/usr/lib')) {
                    $candidate = Join-Path $directory 'libmpv.so.2'
                    if (Test-Path -LiteralPath $candidate -PathType Leaf) { $nativePath = $candidate; break }
                }
            }
            if (-not $nativePath) { throw 'Could not identify libmpv for Linux: no RID runtime copy or concrete system library path was found.' }
            $nativeSource = 'system-resolver-path'
        }
        $nativeItem = Get-CanonicalNativeFile $nativePath
        $resolvedPath = $nativeItem.FullName
        $nativeSha256 = (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $packagingNativeInput = [ordered]@{
            kind = $nativeSource; name = 'libmpv'; version = $null; source = $nativeSource
            path = $resolvedPath; bytes = [long]$nativeItem.Length; sha256 = $nativeSha256; lockFileSha256 = $null
        }
    }

    [ordered]@{
        schemaVersion = 2
        hostName = [Environment]::MachineName
        nativeOS = $nativePlatform
        nativeArchitecture = $nativeArchitecture
        processArchitecture = $processArchitecture
        dotnetSdkVersion = $sdkLines[0]
        validationRuntime = 'offline-unit-headless-ui'
        nativeMedia = 'not-exercised'
        packagingNativeInput = $packagingNativeInput
    }
}

function Get-ReleaseEnvironmentFingerprint([object]$Identity) {
    if ($null -eq $Identity -or $Identity.schemaVersion -ne 2 -or
        $Identity.validationRuntime -cne 'offline-unit-headless-ui' -or $Identity.nativeMedia -cne 'not-exercised' -or
        $null -eq $Identity.packagingNativeInput) { return $null }
    $nativeInput = $Identity.packagingNativeInput
    foreach ($field in @('hostName', 'nativeOS', 'nativeArchitecture', 'processArchitecture', 'dotnetSdkVersion')) {
        if ($null -eq $Identity.$field -or [string]::IsNullOrWhiteSpace([string]$Identity.$field)) { return $null }
    }
    foreach ($field in @('kind', 'name', 'source', 'path', 'sha256')) {
        if ($null -eq $nativeInput.$field -or [string]::IsNullOrWhiteSpace([string]$nativeInput.$field)) { return $null }
    }
    $dependencyFields = @('kind', 'name', 'version', 'source', 'path', 'bytes', 'sha256', 'lockFileSha256')
    foreach ($field in $dependencyFields) {
        $hasField = if ($nativeInput -is [Collections.IDictionary]) { $nativeInput.Contains($field) } else { $null -ne $nativeInput.PSObject.Properties[$field] }
        if (-not $hasField) { return $null }
    }
    if ([long]$nativeInput.bytes -le 0) { return $null }
    if ([string]$nativeInput.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        ($null -ne $nativeInput.lockFileSha256 -and [string]$nativeInput.lockFileSha256 -notmatch '^[0-9a-fA-F]{64}$')) { return $null }
    [ordered]@{
        schemaVersion = [int]$Identity.schemaVersion
        hostName = [string]$Identity.hostName
        nativeOS = [string]$Identity.nativeOS
        nativeArchitecture = [string]$Identity.nativeArchitecture
        processArchitecture = [string]$Identity.processArchitecture
        dotnetSdkVersion = [string]$Identity.dotnetSdkVersion
        validationRuntime = [string]$Identity.validationRuntime
        nativeMedia = [string]$Identity.nativeMedia
        packagingNativeInput = [ordered]@{
            kind = [string]$nativeInput.kind; name = [string]$nativeInput.name
            version = if ($null -eq $nativeInput.version) { $null } else { [string]$nativeInput.version }
            source = [string]$nativeInput.source; path = [string]$nativeInput.path
            bytes = [long]$nativeInput.bytes; sha256 = ([string]$nativeInput.sha256).ToLowerInvariant()
            lockFileSha256 = if ($null -eq $nativeInput.lockFileSha256) { $null } else { ([string]$nativeInput.lockFileSha256).ToLowerInvariant() }
        }
    } | ConvertTo-Json -Compress -Depth 6
}

function Assert-ReleaseEnvironmentMatches([object]$Expected, [object]$Actual, [string]$Context) {
    $expectedFingerprint = Get-ReleaseEnvironmentFingerprint $Expected
    $actualFingerprint = Get-ReleaseEnvironmentFingerprint $Actual
    if (-not $expectedFingerprint) {
        throw 'Validation receipt has no bound native environment identity; validate a fresh request output.'
    }
    if (-not $actualFingerprint -or $expectedFingerprint -cne $actualFingerprint) {
        throw "Native environment identity changed or does not match the validation receipt $Context."
    }
}

$environmentIdentity = Get-ReleaseEnvironmentIdentity $root $Target
if (-not (Get-ReleaseEnvironmentFingerprint $environmentIdentity)) { throw 'Native environment identity is incomplete.' }

$lock = $null
try {
    # Direct callers share this checkout lock; the request client also locks its remote job.
    $lock = [IO.File]::Open((Join-Path $root '.harness/release.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    Assert-Source
    $output = New-HandyOutputRoot $OutputDirectory -AllowExisting
    $receiptPath = Join-Path $output 'validation.json'
    $manifestPath = Join-Path $output 'artifact-manifest.json'
    $existingManifest = Get-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue
    if ($existingManifest -and ($existingManifest.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Package manifest must not be a link.' }
    if ($existingManifest -and $Mode -ne 'upload') { throw 'This output already has a package manifest; choose a fresh request output.' }
    $existingReceipt = Get-Item -LiteralPath $receiptPath -Force -ErrorAction SilentlyContinue
    if ($existingReceipt -and ($existingReceipt.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Validation receipt must not be a link.' }

    if ($Mode -eq 'upload' -and (-not $existingManifest -or -not $existingReceipt)) {
        throw 'Draft upload requires the same request validation receipt and candidate artifact manifest.'
    }

    function Invoke-Gate([hashtable]$Command, [string]$LogName) {
        $executable = $Command.executable
        $cliArguments = [string[]]$Command.arguments
        $logPath = Join-Path $output $LogName
        if (Get-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue) { throw "Retain the existing $LogName and choose a fresh request output." }
        $Command | ConvertTo-Json -Compress | Set-Content -LiteralPath $logPath -Encoding utf8
        & $executable @cliArguments 2>&1 | Tee-Object -FilePath $logPath -Append | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "$executable failed with exit code $LASTEXITCODE; see $LogName in $output." }
        Assert-Source
    }

    if ($Mode -eq 'validate' -or -not (Test-Path -LiteralPath $receiptPath)) {
        if (Test-Path -LiteralPath $receiptPath) { throw 'Validation evidence already exists; choose a fresh request output.' }
        Invoke-Gate $validation 'validation.log'
        $postValidationEnvironment = Get-ReleaseEnvironmentIdentity $root $Target
        Assert-ReleaseEnvironmentMatches $environmentIdentity $postValidationEnvironment 'after validation'
        $environmentIdentity = $postValidationEnvironment
        [ordered]@{
            schemaVersion = 1; project = 'HandyPlayer'; sourceSha = $ExpectedSha.ToLowerInvariant()
            target = $Target; configuration = 'Release'; checkout = $root; status = 'passed'
            packageProfile = $PackageProfile
            environment = $environmentIdentity
            command = $validation; completedUtc = [DateTime]::UtcNow.ToString('o')
            nativeAcceptance = 'not performed'
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $receiptPath -Encoding utf8
    }
    if ($Mode -eq 'validate') { Write-Output "Validation receipt: $receiptPath"; exit 0 }

    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($receipt.schemaVersion -ne 1 -or $receipt.project -ne 'HandyPlayer' -or
        $receipt.sourceSha -ne $ExpectedSha -or $receipt.target -ne $Target -or
        $receipt.configuration -ne 'Release' -or $receipt.checkout -ne $root -or $receipt.status -ne 'passed') {
        throw 'Validation receipt does not match this exact checkout, commit, target and configuration.'
    }
    if ($receipt.packageProfile -cne $PackageProfile) { throw 'Validation receipt packaging profile does not match this request.' }
    $currentEnvironment = Get-ReleaseEnvironmentIdentity $root $Target
    Assert-ReleaseEnvironmentMatches $receipt.environment $currentEnvironment 'for package/upload reuse'
    $environmentIdentity = $currentEnvironment

    $required = switch ($Target) {
        'win-x64' { @('HandyPlayer-*-win-x64.zip', 'HandyPlayer-*-win-x64.msi', 'SHA256SUMS-win-x64.txt', 'native-dependency.json') }
        'osx-arm64' { @('HandyPlayer-*-mac-arm64.zip', 'HandyPlayer-*-mac-arm64.dmg') }
        'linux-x64' { @('HandyPlayer-*-linux-x64.tar.gz', 'handyplayer-*-linux-x64.deb') }
    }
    if ($Mode -eq 'upload') {
        $candidate = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($candidate.schemaVersion -ne 1 -or $candidate.project -ne 'HandyPlayer' -or
            $candidate.sourceSha -ne $ExpectedSha -or $candidate.target -ne $Target -or
            $candidate.configuration -ne 'Release' -or $candidate.status -ne 'candidate') {
            throw 'Candidate manifest does not match the validated source, target and configuration.'
        }
        if ($candidate.packageProfile -cne $PackageProfile) { throw 'Candidate manifest packaging profile does not match this request.' }
        $uploadFiles = @($candidate.artifacts | ForEach-Object {
            $filePath = [IO.Path]::GetFullPath((Join-Path $root $_.path))
            $fileName = [IO.Path]::GetFileName($filePath)
            $expectedPath = Join-Path (Join-Path $output 'artifacts') $fileName
            if ($filePath -ne $expectedPath -or $_.path -ne [IO.Path]::GetRelativePath($root, $filePath).Replace('\', '/')) {
                throw 'Candidate artifact must be a direct file in this request artifacts directory.'
            }
            $file = Get-Item -LiteralPath $filePath -Force
            $artifactDirectory = Get-Item -LiteralPath (Split-Path $filePath -Parent) -Force
            if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                ($artifactDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                $file.Length -eq 0 -or $file.Length -ne $_.size -or $_.sha256 -notmatch '^[0-9a-f]{64}$' -or
                (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $_.sha256) {
                throw "Candidate artifact changed or is unsafe: $fileName"
            }
            @{ path = $filePath; size = $file.Length; sha256 = $_.sha256 }
        })
        foreach ($pattern in $required) {
            if (@($uploadFiles | Where-Object { [IO.Path]::GetFileName($_.path) -like $pattern }).Count -ne 1) {
                throw "Candidate manifest must contain exactly one required artifact: $pattern"
            }
        }
        if ($Target -eq 'win-x64' -and $PackageProfile -eq 'Candidate' -and
            @($uploadFiles | Where-Object { [IO.Path]::GetFileName($_.path) -like '*-win-x64-setup.exe' }).Count) {
            throw 'Candidate packaging profile must not contain an Inno EXE.'
        }
        $currentEnvironment = Get-ReleaseEnvironmentIdentity $root $Target
        Assert-ReleaseEnvironmentMatches $receipt.environment $currentEnvironment 'before draft upload'
        . "$PSScriptRoot/Publish-Draft.ps1"
        $draft = Invoke-HandyDraftUpload -Repository 'Belgian-Coder/HandyPlayer' -SourceSha $ExpectedSha `
            -Target $Target -Artifacts $uploadFiles -OutputDirectory $output -NativeAcceptance $candidate.nativeAcceptance -PackageProfile $PackageProfile
        Write-Output "Draft release: $($draft.url)"
        exit 0
    }

    # Never inherit a caller's prebuilt/no-restore shortcut into an isolated request.
    $env:PREBUILT_DIR = $null
    $env:HANDY_NO_RESTORE = '0'
    $env:HANDY_MAX_PARALLELISM = "$MaxParallelism"
    $env:HANDY_RELEASE_OUTPUT = Join-Path $output 'artifacts'
    $currentEnvironment = Get-ReleaseEnvironmentIdentity $root $Target
    Assert-ReleaseEnvironmentMatches $receipt.environment $currentEnvironment 'before packaging'
    Invoke-Gate $packaging 'package.log'
    $currentEnvironment = Get-ReleaseEnvironmentIdentity $root $Target
    Assert-ReleaseEnvironmentMatches $receipt.environment $currentEnvironment 'after packaging'
    $packageMetadata = $null
    if ($Target -eq 'win-x64') {
        $marker = 'HANDY_PACKAGE_RESULT '
        $results = @(Get-Content -LiteralPath (Join-Path $output 'package.log') | Where-Object { $_.StartsWith($marker, [StringComparison]::Ordinal) })
        if ($results.Count -ne 1) { throw 'Publisher must emit exactly one successful packaging result.' }
        $packageMetadata = $results[0].Substring($marker.Length) | ConvertFrom-Json
        if ($packageMetadata.schemaVersion -ne 1 -or $packageMetadata.packageProfile -cne $PackageProfile -or
            $packageMetadata.requiredMsi -isnot [bool] -or -not $packageMetadata.requiredMsi) {
            throw 'Publisher result does not match the requested profile and required MSI gate.'
        }
        $expectedCompression = if ($PackageProfile -eq 'Candidate') { 'medium' } else { 'high' }
        if ($packageMetadata.msiCompression -cne $expectedCompression) {
            throw 'Publisher MSI compression does not match the requested profile.'
        }
        $stageKeys = @('nativeRestore', 'publish', 'portable', 'zip', 'msi', 'inno', 'checksums')
        if (@($packageMetadata.stageSeconds.PSObject.Properties).Count -ne $stageKeys.Count) { throw 'Publisher stage timings are incomplete.' }
        foreach ($key in $stageKeys) {
            $value = $packageMetadata.stageSeconds.$key
            if (($value -isnot [double] -and $value -isnot [long] -and $value -isnot [int]) -or
                -not [double]::IsFinite([double]$value) -or $value -lt 0) { throw 'Publisher stage timings must be finite nonnegative numbers.' }
        }
        if (($PackageProfile -eq 'Candidate' -and ($packageMetadata.innoStatus -cne 'skipped-profile' -or $packageMetadata.stageSeconds.inno -ne 0)) -or
            ($PackageProfile -eq 'Full' -and $packageMetadata.innoStatus -cnotin @('built', 'unavailable'))) {
            throw 'Publisher Inno result does not match the selected packaging profile.'
        }
    }
    $allowed = @($required) + @(switch ($Target) {
        'win-x64' { if ($PackageProfile -eq 'Full') { 'HandyPlayer-*-win-x64-setup.exe' } }
        'linux-x64' { 'HandyPlayer-*-linux-x64.AppImage' }
    })
    if ($Target -eq 'win-x64' -and $PackageProfile -eq 'Candidate' -and
        @(Get-ChildItem -LiteralPath $env:HANDY_RELEASE_OUTPUT -File -Filter '*-win-x64-setup.exe').Count) {
        throw 'Candidate packaging profile must not produce an Inno EXE.'
    }
    $files = @(Get-ChildItem -LiteralPath $env:HANDY_RELEASE_OUTPUT -File | Where-Object {
        $name = $_.Name
        @($allowed | Where-Object { $name -like $_ }).Count -gt 0
    })
    foreach ($pattern in $required) {
        $matches = @($files | Where-Object Name -Like $pattern)
        if ($matches.Count -ne 1 -or $matches[0].Length -eq 0) { throw "Required package artifact missing, empty or ambiguous: $pattern" }
    }
    $artifacts = @($files | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Artifact must not be a link.' }
        [ordered]@{
            path = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
            size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    [ordered]@{
        schemaVersion = 1; project = 'HandyPlayer'; sourceSha = $ExpectedSha.ToLowerInvariant()
        target = $Target; configuration = 'Release'; status = 'candidate'; uploadPolicy = 'draft-only'
        packageProfile = $PackageProfile; packaging = $packageMetadata
        signing = $(if ($Target -eq 'osx-arm64') { 'ad-hoc verification by publish-mac.sh; Developer ID/notarization not performed' } else { 'no code-signing claim' })
        validationReceipt = [IO.Path]::GetRelativePath($root, $receiptPath).Replace('\', '/')
        nativeAcceptance = 'not performed'; artifacts = $artifacts
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    Write-Output "Candidate artifact manifest: $manifestPath"
} finally {
    if ($lock) { $lock.Dispose() }
}
