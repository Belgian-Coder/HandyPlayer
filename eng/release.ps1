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
    [ValidateRange(1, 64)][int]$MaxParallelism = 2
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
$validation = @{
    executable = 'pwsh'
    arguments = @('-NoProfile', '-NonInteractive', '-File', './validate.ps1', '-Coverage', '-MaxParallelism', "$MaxParallelism")
}
$packaging = switch ($Target) {
    'win-x64' { @{
        executable = 'pwsh'
        arguments = @('-NoProfile', '-NonInteractive', '-File', './publish-win.ps1', '-RequireMsi', '-MaxParallelism', "$MaxParallelism")
    } }
    'osx-arm64' { @{ executable = 'bash'; arguments = @('./publish-mac.sh', 'Release') } }
    'linux-x64' { @{ executable = 'bash'; arguments = @('./publish-linux.sh', 'Release') } }
}

if ($Mode -eq 'plan') {
    [ordered]@{
        schemaVersion = 1; project = 'HandyPlayer'; target = $Target; sourceSha = $ExpectedSha.ToLowerInvariant()
        outputDirectory = $OutputDirectory; validation = $validation; packaging = $packaging
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
        [ordered]@{
            schemaVersion = 1; project = 'HandyPlayer'; sourceSha = $ExpectedSha.ToLowerInvariant()
            target = $Target; configuration = 'Release'; checkout = $root; status = 'passed'
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

    $required = switch ($Target) {
        'win-x64' { @('HandyPlayer-*-win-x64.zip', 'HandyPlayer-*-win-x64.msi', 'SHA256SUMS-win-x64.txt') }
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
        . "$PSScriptRoot/Publish-Draft.ps1"
        $draft = Invoke-HandyDraftUpload -Repository 'Belgian-Coder/HandyPlayer' -SourceSha $ExpectedSha `
            -Target $Target -Artifacts $uploadFiles -OutputDirectory $output -NativeAcceptance $candidate.nativeAcceptance
        Write-Output "Draft release: $($draft.url)"
        exit 0
    }

    # Never inherit a caller's prebuilt/no-restore shortcut into an isolated request.
    $env:PREBUILT_DIR = $null
    $env:HANDY_NO_RESTORE = '0'
    $env:HANDY_MAX_PARALLELISM = "$MaxParallelism"
    $env:HANDY_RELEASE_OUTPUT = Join-Path $output 'artifacts'
    Invoke-Gate $packaging 'package.log'
    $allowed = @($required) + @(switch ($Target) {
        'win-x64' { 'HandyPlayer-*-win-x64-setup.exe' }
        'linux-x64' { 'HandyPlayer-*-linux-x64.AppImage' }
    })
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
        signing = $(if ($Target -eq 'osx-arm64') { 'ad-hoc verification by publish-mac.sh; Developer ID/notarization not performed' } else { 'no code-signing claim' })
        validationReceipt = [IO.Path]::GetRelativePath($root, $receiptPath).Replace('\', '/')
        nativeAcceptance = 'not performed'; artifacts = $artifacts
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    Write-Output "Candidate artifact manifest: $manifestPath"
} finally {
    if ($lock) { $lock.Dispose() }
}
