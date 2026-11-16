#Requires -Version 7
# SDK-free Unix IPC path-budget and isolated namespace checks.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. "$root/harness-env.ps1"
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Must-Fail([scriptblock]$Action, [string]$Pattern) {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_ }
    Assert ($caught -and $caught.Exception.Message -like $Pattern) "Expected refusal: $Pattern"
}
$primary = '/Users/tiebird/Projects/HandyPlayer'
$checkout = "$primary/.wt/release-osx-arm64"
$selected = Get-HandyPipeDirectory $checkout $primary 'macos' 'arm64'
Assert ($selected.Fingerprint.Length -eq 64) 'Long Mac checkout did not select an owned fallback.'
Assert ([Text.Encoding]::UTF8.GetByteCount($selected.Path) + 47 -le 103) 'Mac socket still exceeds the Unix byte limit.'
Assert ((Get-HandyPipeDirectory $checkout $primary 'macos' 'arm64').Path -ceq $selected.Path) 'Namespace is not stable.'
foreach ($variant in @(
    (Get-HandyPipeDirectory "$primary/.wt/another-task" $primary 'macos' 'arm64'),
    (Get-HandyPipeDirectory $checkout $primary 'linux' 'arm64'),
    (Get-HandyPipeDirectory $checkout $primary 'macos' 'x64')
)) { Assert ($variant.Path -cne $selected.Path) 'Writable IPC namespace was shared across checkout/OS/architecture.' }
$boundary = Get-HandyPipeDirectory ('/' + ('a' * 42)) '/p' 'macos' 'arm64'
Assert (-not $boundary.Fingerprint -and [Text.Encoding]::UTF8.GetByteCount($boundary.Path) + 47 -eq 103) 'Exact byte boundary was rejected.'
$unicode = Get-HandyPipeDirectory ('/' + ('é' * 22)) '/p' 'macos' 'arm64'
Assert ($null -ne $unicode.Fingerprint) 'UTF-8 bytes were incorrectly counted as characters.'
Must-Fail { Get-HandyPipeDirectory $checkout ('/' + ('b' * 80)) 'macos' 'arm64' } '*shorter native project root*'

$fixture = Join-Path $root ('.harness/pipe-contracts/' + [Guid]::NewGuid().ToString('N'))
$owned = [pscustomobject]@{ Path = (Join-Path $fixture 'sockets'); Fingerprint = $selected.Fingerprint }
$created = Initialize-HandyPipeDirectory $owned
Assert ($created -eq $owned.Path) 'Namespace initialization returned a different directory.'
$marker = Join-Path $owned.Path '.owner'
Assert ([IO.File]::ReadAllText($marker) -ceq $selected.Fingerprint) 'Full owner fingerprint was not saved.'
Initialize-HandyPipeDirectory $owned | Out-Null
$otherOwner = [pscustomobject]@{ Path = $owned.Path; Fingerprint = ('f' * 64) }
Must-Fail { Initialize-HandyPipeDirectory $otherOwner } '*another checkout/platform/architecture*'
Assert ([IO.File]::ReadAllText($marker) -ceq $selected.Fingerprint) 'Collision overwrote the original owner.'
$unknown = [pscustomobject]@{ Path = (Join-Path $fixture 'unknown'); Fingerprint = $selected.Fingerprint }
[IO.Directory]::CreateDirectory($unknown.Path) | Out-Null
Must-Fail { Initialize-HandyPipeDirectory $unknown } '*Unknown existing test socket namespace*'
Assert (-not (Test-Path -LiteralPath (Join-Path $unknown.Path '.owner'))) 'Unknown namespace was adopted.'
$link = Join-Path $fixture 'linked'
$linkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
New-Item -ItemType $linkType -Path $link -Target $owned.Path | Out-Null
Must-Fail { Initialize-HandyPipeDirectory ([pscustomobject]@{ Path = (Join-Path $link 'unsafe'); Fingerprint = $selected.Fingerprint }) } '*link/junction*'
Assert (-not (Test-Path -LiteralPath (Join-Path $owned.Path 'unsafe'))) 'Redirected socket output was created.'

# Private, ignored Git fixtures exercise hint provenance without changing real roots.
$project = Join-Path $fixture 'project'
$clone = Join-Path $project '.wt/clone'
[IO.Directory]::CreateDirectory($clone) | Out-Null
foreach ($gitRoot in @($project, $clone)) {
    & git -C $gitRoot init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize private Git fixture.' }
    & git -C $gitRoot config remote.origin.url 'https://github.com/Belgian-Coder/HandyPlayer.git'
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure private Git fixture origin.' }
}
$projectMarker = Join-Path $project '.request-release-owner.json'
[IO.File]::WriteAllText($projectMarker, '{"owner":"request-release","version":1,"project":"HandyPlayer"}')
Assert ((Get-HandyPipeProjectRoot $clone $clone $project) -eq $project) 'Validated outer root hint was not used.'
& git -C $project config remote.origin.url 'https://github.com/example/unrelated.git'
Must-Fail { Get-HandyPipeProjectRoot $clone $clone $project } '*same canonical origin*'
& git -C $project config remote.origin.url 'https://github.com/Belgian-Coder/HandyPlayer.git'
[IO.File]::WriteAllText($projectMarker, '{"owner":"request-release","version":1,"project":"Unrelated"}')
Must-Fail { Get-HandyPipeProjectRoot $clone $clone $project } '*not owned by this project*'
Must-Fail { Get-HandyPipeProjectRoot $root $root $project } '*must contain this checkout*'
Write-Output 'Unix IPC byte budget, checkout/OS/architecture isolation, owner/link guards and hinted Git-project provenance passed.'
