#Requires -Version 7
# Exercises the real release environment identity functions with fake host commands and native files.
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $sourceRoot ('.harness/release-environment-contracts/' + [Guid]::NewGuid().ToString('N'))
$fakeBin = Join-Path $fixture 'bin'
[IO.Directory]::CreateDirectory($fakeBin) | Out-Null

function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Must-Fail([scriptblock]$Action, [string]$ExpectedPattern, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true; $script:ReleaseEnvironmentError = $_.Exception.Message }
    Assert $failed $Message
    Assert ($script:ReleaseEnvironmentError -like $ExpectedPattern) "$Message Unexpected refusal: $script:ReleaseEnvironmentError"
}
function Set-TargetContext([string]$Target) {
    $parts = $Target.Split('-')
    $script:nativePlatform = $parts[0]
    $script:nativeArchitecture = $parts[1]
    $script:processArchitecture = $parts[1]
}
function Write-FakeApplication([string]$Name, [string]$Output) {
    if ($IsWindows) {
        $path = Join-Path $fakeBin "$Name.cmd"
        if ($Name -eq 'dotnet') { $body = "@echo off`r`nif `"%1`"==`"--version`" echo $Output`r`nexit /b 0`r`n" }
        else { $body = "@echo off`r`nif `"%1`"==`"--prefix`" echo $Output`r`nexit /b 0`r`n" }
        [IO.File]::WriteAllText($path, $body, [Text.Encoding]::ASCII)
        return $path
    }
    $path = Join-Path $fakeBin $Name
    if ($Name -eq 'dotnet') { $body = "#!/bin/sh`necho '$Output'`n" }
    else { $body = "#!/bin/sh`necho '$Output'`n" }
    [IO.File]::WriteAllText($path, $body, [Text.Encoding]::ASCII)
    $mode = [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute
    [IO.File]::SetUnixFileMode($path, $mode)
    return $path
}

$source = Get-Content -LiteralPath (Join-Path $sourceRoot 'eng/release.ps1') -Raw
$tokens = $null
$parseErrors = $null
$sourceAst = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors) { throw "Release adapter source did not parse: $($parseErrors[0].Message)" }
$functionNames = @('Get-CanonicalNativeFile', 'Assert-ReleaseNativeLoaderOverrides', 'Get-ReleaseEnvironmentIdentity', 'Get-ReleaseEnvironmentFingerprint', 'Assert-ReleaseEnvironmentMatches')
$realFunctions = foreach ($name in $functionNames) {
    $functionAst = $sourceAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $functionAst) { throw "Release adapter function not found: $name" }
    $functionAst.Extent.Text
}
Invoke-Expression ($realFunctions -join "`r`n`r`n")

$savedPath = $env:PATH
$loaderNames = @('LD_LIBRARY_PATH', 'LD_PRELOAD', 'DYLD_LIBRARY_PATH', 'DYLD_FALLBACK_LIBRARY_PATH',
    'DYLD_INSERT_LIBRARIES', 'DYLD_FRAMEWORK_PATH', 'DYLD_FALLBACK_FRAMEWORK_PATH', 'DYLD_ROOT_PATH',
    'DYLD_VERSIONED_LIBRARY_PATH', 'DYLD_VERSIONED_FRAMEWORK_PATH', 'DYLD_IMAGE_SUFFIX')
$savedLoaderValues = @{}
foreach ($name in $loaderNames) {
    $savedLoaderValues[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $null, 'Process')
}
try {
    Write-FakeApplication 'dotnet' '11.0.100' | Out-Null
    $env:PATH = $fakeBin + [IO.Path]::PathSeparator + $savedPath

    # Windows records the actual DLL hash and the reviewed pin/lock metadata.
    $windowsRoot = Join-Path $fixture 'windows'
    $windowsNativeRelative = 'src/HandyPlaylistPlayer.Media.Mpv/runtimes/win-x64/native/libmpv-2.dll'
    $windowsNativePath = Join-Path $windowsRoot $windowsNativeRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    [IO.Directory]::CreateDirectory((Split-Path $windowsNativePath -Parent)) | Out-Null
    [IO.File]::WriteAllBytes($windowsNativePath, [Text.Encoding]::UTF8.GetBytes('reviewed-fixture-libmpv-dll'))
    $windowsNative = Get-Item -LiteralPath $windowsNativePath
    $windowsNativeSha = (Get-FileHash -LiteralPath $windowsNativePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $windowsLock = [ordered]@{
        schemaVersion = 1
        'win-x64' = [ordered]@{
            name = 'libmpv'; version = 'fixture-2026.11'; dllFileName = 'libmpv-2.dll'
            dllSha256 = $windowsNativeSha; dllBytes = [long]$windowsNative.Length; destination = $windowsNativeRelative
        }
    }
    $windowsLockPath = Join-Path $windowsRoot 'eng/native-dependencies.json'
    [IO.Directory]::CreateDirectory((Split-Path $windowsLockPath -Parent)) | Out-Null
    $windowsLock | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $windowsLockPath -Encoding utf8
    Set-TargetContext 'win-x64'
    $windowsIdentity = Get-ReleaseEnvironmentIdentity $windowsRoot 'win-x64'
    Assert ($windowsIdentity.schemaVersion -eq 2 -and $windowsIdentity.validationRuntime -ceq 'offline-unit-headless-ui' -and $windowsIdentity.nativeMedia -ceq 'not-exercised') 'Environment schema did not separate validation runtime from native media.'
    Assert ($windowsIdentity.dotnetSdkVersion -ceq '11.0.100') 'Identity did not resolve the fake dotnet --version executable.'
    Assert ($windowsIdentity.packagingNativeInput.version -ceq 'fixture-2026.11') 'Windows pin version was not recorded.'
    Assert ($windowsIdentity.packagingNativeInput.sha256 -ceq $windowsNativeSha) 'Windows native file hash did not match the fixture DLL.'
    Assert ($windowsIdentity.packagingNativeInput.lockFileSha256 -ceq (Get-FileHash -LiteralPath $windowsLockPath -Algorithm SHA256).Hash.ToLowerInvariant()) 'Windows lockfile hash was not recorded.'
    Assert (-not [string]::IsNullOrEmpty((Get-ReleaseEnvironmentFingerprint $windowsIdentity))) 'Windows environment identity was rejected by its fingerprint validator.'
    Write-FakeApplication 'dotnet' '11.0.101' | Out-Null
    $changedSdkIdentity = Get-ReleaseEnvironmentIdentity $windowsRoot 'win-x64'
    Must-Fail { Assert-ReleaseEnvironmentMatches $windowsIdentity $changedSdkIdentity 'after validation' } '*Native environment identity changed*' 'Changed resolved SDK version reused an environment identity.'
    Write-FakeApplication 'dotnet' '11.0.100' | Out-Null

    # macOS package selection prefers the actual Homebrew file that the publisher copies over the RID copy.
    $macRoot = Join-Path $fixture 'mac-homebrew'
    $macRuntime = Join-Path $macRoot 'src/HandyPlaylistPlayer.Media.Mpv/runtimes/osx-arm64/native/libmpv.2.dylib'
    [IO.Directory]::CreateDirectory((Split-Path $macRuntime -Parent)) | Out-Null
    [IO.File]::WriteAllBytes($macRuntime, [Text.Encoding]::UTF8.GetBytes('repo-rid-libmpv-different-content'))
    $brewPrefix = Join-Path $macRoot 'homebrew'
    $brewLink = Join-Path $brewPrefix 'lib/libmpv.2.dylib'
    $brewTarget = Join-Path $brewPrefix 'Cellar/mpv/0.42.1/lib/libmpv.2.dylib'
    [IO.Directory]::CreateDirectory((Split-Path $brewLink -Parent)) | Out-Null
    [IO.Directory]::CreateDirectory((Split-Path $brewTarget -Parent)) | Out-Null
    [IO.File]::WriteAllBytes($brewTarget, [Text.Encoding]::UTF8.GetBytes('homebrew-cellar-libmpv-selected'))
    [IO.File]::CreateSymbolicLink($brewLink, $brewTarget) | Out-Null
    Write-FakeApplication 'brew' $brewPrefix | Out-Null
    Set-TargetContext 'osx-arm64'
    $macIdentity = Get-ReleaseEnvironmentIdentity $macRoot 'osx-arm64'
    $brewSha = (Get-FileHash -LiteralPath $brewTarget -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert ($macIdentity.packagingNativeInput.kind -ceq 'homebrew-mpv') 'macOS identity did not record the publisher Homebrew selection.'
    Assert ($macIdentity.packagingNativeInput.path -ceq [IO.Path]::GetFullPath($brewTarget)) 'Homebrew symlink was not resolved to its canonical Cellar file.'
    Assert ($macIdentity.packagingNativeInput.sha256 -ceq $brewSha) 'macOS identity hashed the RID copy instead of the selected Homebrew input.'
    Assert ($macIdentity.packagingNativeInput.sha256 -cne (Get-FileHash -LiteralPath $macRuntime -Algorithm SHA256).Hash.ToLowerInvariant()) 'macOS fixture sources did not differ.'
    [IO.File]::WriteAllBytes($brewTarget, [Text.Encoding]::UTF8.GetBytes('mutated-homebrew-libmpv-after-validation'))
    $changedMacIdentity = Get-ReleaseEnvironmentIdentity $macRoot 'osx-arm64'
    Must-Fail { Assert-ReleaseEnvironmentMatches $macIdentity $changedMacIdentity 'before packaging' } '*Native environment identity changed*' 'Changed publisher native input reused a validation identity.'
    [IO.File]::WriteAllBytes($brewTarget, [Text.Encoding]::UTF8.GetBytes('homebrew-cellar-libmpv-selected'))
    $restoredMacIdentity = Get-ReleaseEnvironmentIdentity $macRoot 'osx-arm64'
    Assert-ReleaseEnvironmentMatches $macIdentity $restoredMacIdentity 'for unchanged reuse'

    # If publisher Homebrew input is absent, the RID runtime file is the next package source.
    $macFallbackRoot = Join-Path $fixture 'mac-rid-fallback'
    $macFallbackRuntime = Join-Path $macFallbackRoot 'src/HandyPlaylistPlayer.Media.Mpv/runtimes/osx-arm64/native/libmpv.2.dylib'
    [IO.Directory]::CreateDirectory((Split-Path $macFallbackRuntime -Parent)) | Out-Null
    [IO.File]::WriteAllBytes($macFallbackRuntime, [Text.Encoding]::UTF8.GetBytes('repo-fallback-libmpv'))
    $emptyBrewPrefix = Join-Path $macFallbackRoot 'homebrew-empty'
    [IO.Directory]::CreateDirectory($emptyBrewPrefix) | Out-Null
    Write-FakeApplication 'brew' $emptyBrewPrefix | Out-Null
    $macFallbackIdentity = Get-ReleaseEnvironmentIdentity $macFallbackRoot 'osx-arm64'
    Assert ($macFallbackIdentity.packagingNativeInput.kind -ceq 'repository-runtime') 'macOS fallback did not record the RID runtime copy.'
    Assert ($macFallbackIdentity.packagingNativeInput.sha256 -ceq (Get-FileHash -LiteralPath $macFallbackRuntime -Algorithm SHA256).Hash.ToLowerInvariant()) 'macOS fallback hash is not the RID runtime file hash.'

    # Linux direct publication binds the concrete RID library when present.
    $linuxRoot = Join-Path $fixture 'linux'
    $linuxNative = Join-Path $linuxRoot 'src/HandyPlaylistPlayer.Media.Mpv/runtimes/linux-x64/native/libmpv.so.2'
    [IO.Directory]::CreateDirectory((Split-Path $linuxNative -Parent)) | Out-Null
    [IO.File]::WriteAllBytes($linuxNative, [Text.Encoding]::UTF8.GetBytes('linux-rid-libmpv'))
    Set-TargetContext 'linux-x64'
    $linuxIdentity = Get-ReleaseEnvironmentIdentity $linuxRoot 'linux-x64'
    Assert ($linuxIdentity.packagingNativeInput.kind -ceq 'repository-runtime') 'Linux identity did not select its RID runtime file.'
    Assert ($linuxIdentity.packagingNativeInput.sha256 -ceq (Get-FileHash -LiteralPath $linuxNative -Algorithm SHA256).Hash.ToLowerInvariant()) 'Linux identity hash does not match the RID file.'

    foreach ($override in @('LD_LIBRARY_PATH', 'LD_PRELOAD')) {
        [Environment]::SetEnvironmentVariable($override, 'fixture-override', 'Process')
        Must-Fail { Get-ReleaseEnvironmentIdentity $linuxRoot 'linux-x64' } "*$override*unset*" "Linux loader override $override was accepted."
        [Environment]::SetEnvironmentVariable($override, $null, 'Process')
    }
    foreach ($override in @('DYLD_LIBRARY_PATH', 'DYLD_FALLBACK_LIBRARY_PATH', 'DYLD_INSERT_LIBRARIES',
            'DYLD_FRAMEWORK_PATH', 'DYLD_FALLBACK_FRAMEWORK_PATH', 'DYLD_ROOT_PATH',
            'DYLD_VERSIONED_LIBRARY_PATH', 'DYLD_VERSIONED_FRAMEWORK_PATH', 'DYLD_IMAGE_SUFFIX')) {
        [Environment]::SetEnvironmentVariable($override, 'fixture-override', 'Process')
        Must-Fail { Get-ReleaseEnvironmentIdentity $macFallbackRoot 'osx-arm64' } "*$override*unset*" "macOS loader override $override was accepted."
        [Environment]::SetEnvironmentVariable($override, $null, 'Process')
    }
    Write-Output 'Release environment schema, SDK resolution, Windows pin, macOS publisher/RID selection, symlink hashing, Linux RID hash and loader override contracts passed.'
} finally {
    $env:PATH = $savedPath
    foreach ($name in $loaderNames) { [Environment]::SetEnvironmentVariable($name, $savedLoaderValues[$name], 'Process') }
}
