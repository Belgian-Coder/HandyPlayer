#Requires -Version 7
# SDK-free publisher contracts: isolated copied scripts, mocked host tools, real ZIP/hashes.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publisher = Join-Path $root 'publish-win.ps1'
$fixture = Join-Path $root ('.harness/publish-contracts/' + [Guid]::NewGuid().ToString('N'))
$shell = (Get-Process -Id $PID).Path
[IO.Directory]::CreateDirectory($fixture) | Out-Null
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

$harnessStub = @'
function Write-MockCall([string]$Call) { Add-Content -LiteralPath "$PSScriptRoot/calls.txt" -Value $Call }
function New-HandyReleaseRoot([string]$Rid) {
    $destination = Join-Path $PSScriptRoot 'release'
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    return $destination
}
function dotnet {
    Write-MockCall ('dotnet ' + ($args -join ' '))
    if ($args[0] -eq 'msbuild') { $global:LASTEXITCODE = 0; return '1.2.3' }
    if ($args[0] -ne 'publish') { throw 'Unexpected dotnet command.' }
    if ($env:HANDY_PUBLISH_TEST_FAILURE -eq 'publish') { $global:LASTEXITCODE = 17; return }
    $output = $args[([array]::IndexOf($args, '-o') + 1)]
    [IO.Directory]::CreateDirectory($output) | Out-Null
    [IO.File]::WriteAllText((Join-Path $output 'HandyPlayer.exe'), 'mock executable')
    [IO.File]::WriteAllText((Join-Path $output 'libmpv-2.dll'), 'mock verified native dependency')
    $global:LASTEXITCODE = 0
}
function wix {
    Write-MockCall ('wix ' + ($args -join ' '))
    if ($args[0] -eq 'extension') {
        $global:LASTEXITCODE = if ($env:HANDY_PUBLISH_TEST_FAILURE -eq 'extension') { 18 } else { 0 }
        return
    }
    if ($args[0] -ne 'build') { throw 'Unexpected wix command.' }
    if ($env:HANDY_PUBLISH_TEST_FAILURE -eq 'msi') { $global:LASTEXITCODE = 18; return }
    if ($env:HANDY_PUBLISH_TEST_FAILURE -ne 'msi-missing-output') {
        [IO.File]::WriteAllText($args[([array]::IndexOf($args, '-o') + 1)], 'mock MSI')
    }
    $global:LASTEXITCODE = 0
}
function Invoke-MockIscc {
    Write-MockCall ('iscc ' + ($args -join ' '))
    if ($env:HANDY_PUBLISH_TEST_FAILURE -eq 'inno') { $global:LASTEXITCODE = 19; return }
    $output = ($args | Where-Object { $_ -like '/O*' } | Select-Object -First 1).Substring(2)
    if ($env:HANDY_PUBLISH_TEST_FAILURE -ne 'inno-missing-output') {
        [IO.File]::WriteAllText((Join-Path $output 'HandyPlayer-1.2.3-win-x64-setup.exe'), 'mock Inno installer')
    }
    $global:LASTEXITCODE = 0
}
function Get-Command {
    param([Parameter(Position=0)][string]$Name)
    Write-MockCall "discover $Name"
    switch ($Name) {
        'wix' { if ($env:HANDY_PUBLISH_TEST_WIX -eq 'yes') { return [pscustomobject]@{ Source = 'wix' } }; return }
        'iscc' { if ($env:HANDY_PUBLISH_TEST_INNO -eq 'yes') { return [pscustomobject]@{ Source = 'Invoke-MockIscc' } }; return }
        default { throw "Unexpected host tool discovery: $Name" }
    }
}
function Test-Path {
    param([Parameter(Position=0)][Alias('LiteralPath')][string]$Path, [string]$PathType)
    if ($Path -like '*Inno Setup*') { Write-MockCall "inno-probe $Path"; return $false }
    if ($PathType) { Microsoft.PowerShell.Management\Test-Path -LiteralPath $Path -PathType $PathType }
    else { Microsoft.PowerShell.Management\Test-Path -LiteralPath $Path }
}
function Get-FileHash {
    param([Parameter(Position=0)][string]$Path, [string]$Algorithm)
    if ($env:HANDY_PUBLISH_TEST_FAILURE -eq 'checksums') { throw 'Mock checksum failure.' }
    Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath $Path -Algorithm $Algorithm
}
'@
$nativeStub = @'
function Restore-HandyNativeDependency {
    param($CheckoutRoot, $Specification, $ArchiveCandidates, $EvidencePath)
    Write-MockCall 'native-restore'
    if ($env:HANDY_PUBLISH_TEST_FAILURE -eq 'native') { throw 'Mock native verification failure.' }
    '{"schemaVersion":1,"version":"mock","sourceKind":"fixture"}' | Set-Content -LiteralPath $EvidencePath
    return [pscustomobject]@{ version = 'mock'; sourceKind = 'fixture' }
}
'@
$runner = @'
param([string]$Profile, [switch]$RequireMsi, [switch]$NoRestore, [string]$Failure, [string]$Wix, [string]$Inno)
$ErrorActionPreference = 'Stop'
$env:HANDY_PUBLISH_TEST_FAILURE = $Failure
$env:HANDY_PUBLISH_TEST_WIX = $Wix
$env:HANDY_PUBLISH_TEST_INNO = $Inno
$parameters = @{}
if ($Profile) { $parameters.PackageProfile = $Profile }
if ($RequireMsi) { $parameters.RequireMsi = $true }
if ($NoRestore) { $parameters.NoRestore = $true }
try { & "$PSScriptRoot/publish-win.ps1" @parameters; exit $LASTEXITCODE }
catch { Write-Output $_; exit 1 }
'@

function Invoke-PublisherCase {
    param([string]$Name, [string]$Profile = '', [switch]$RequireMsi, [switch]$NoRestore, [string]$Failure = '',
        [string]$Wix = 'yes', [string]$Inno = 'yes', [bool]$Success = $true, [int]$SchemaVersion = 1)
    $directory = Join-Path $fixture $Name
    [IO.Directory]::CreateDirectory((Join-Path $directory 'eng')) | Out-Null
    Copy-Item -LiteralPath $publisher -Destination (Join-Path $directory 'publish-win.ps1')
    Copy-Item -LiteralPath (Join-Path $root 'eng/Windows-Tools.ps1') -Destination (Join-Path $directory 'eng/Windows-Tools.ps1')
    Set-Content -LiteralPath (Join-Path $directory 'harness-env.ps1') -Value $harnessStub
    Set-Content -LiteralPath (Join-Path $directory 'eng/Native-Dependency.ps1') -Value $nativeStub
    @{ schemaVersion = $SchemaVersion; 'win-x64' = @{ version = 'mock' } } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'eng/native-dependencies.json')
    Set-Content -LiteralPath (Join-Path $directory 'installer-win.wxs') -Value 'fixture input'
    Set-Content -LiteralPath (Join-Path $directory 'installer-win.iss') -Value 'fixture input'
    Set-Content -LiteralPath (Join-Path $directory 'runner.ps1') -Value $runner
    $arguments = @('-NoProfile', '-File', (Join-Path $directory 'runner.ps1'), '-Failure', $Failure, '-Wix', $Wix, '-Inno', $Inno)
    if ($Profile) { $arguments += @('-Profile', $Profile) }
    if ($RequireMsi) { $arguments += '-RequireMsi' }
    if ($NoRestore) { $arguments += '-NoRestore' }
    $output = @(& $shell @arguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    $output | Set-Content -LiteralPath (Join-Path $directory 'publisher.log')
    $callsPath = Join-Path $directory 'calls.txt'
    $calls = if (Test-Path -LiteralPath $callsPath) { @(Get-Content -LiteralPath $callsPath) } else { @() }
    $markers = @($output | Where-Object { $_.StartsWith('HANDY_PACKAGE_RESULT ') })
    Assert (($exitCode -eq 0) -eq $Success) "$Name unexpected exit $exitCode. Evidence: $directory"
    Assert ($markers.Count -eq [int]$Success) "$Name wrong success marker count: $($markers.Count)."
    $metadata = if ($Success) { $markers[0].Substring('HANDY_PACKAGE_RESULT '.Length) | ConvertFrom-Json } else { $null }
    return [pscustomobject]@{ Directory = $directory; Calls = $calls; Output = $output; Metadata = $metadata }
}
function Assert-Metadata($Case, [string]$Profile, [bool]$RequiredMsi, [string]$InnoStatus) {
    $metadata = $Case.Metadata
    Assert ($metadata.schemaVersion -eq 1 -and $metadata.packageProfile -ceq $Profile) 'Wrong package result schema/profile.'
    Assert ($metadata.requiredMsi -is [bool] -and $metadata.requiredMsi -eq $RequiredMsi) 'Wrong required MSI metadata.'
    Assert ($metadata.innoStatus -ceq $InnoStatus) 'Wrong Inno status.'
    $expectedCompression = if ($Profile -eq 'Candidate') { 'medium' } else { 'high' }
    Assert ($metadata.msiCompression -ceq $expectedCompression) 'Wrong MSI compression profile.'
    if ($RequiredMsi) {
        Assert (@($Case.Calls | Where-Object { $_ -like "wix build *-d CabCompression=$expectedCompression *" }).Count -eq 1) 'MSI build did not use its profile compression.'
    }
    $keys = @('nativeRestore', 'publish', 'portable', 'zip', 'msi', 'inno', 'checksums')
    Assert ((($metadata.stageSeconds.PSObject.Properties.Name | Sort-Object) -join ',') -ceq (($keys | Sort-Object) -join ',')) 'Wrong timing stage keys.'
    foreach ($key in $keys) {
        $value = $metadata.stageSeconds.$key
        Assert (($value -is [double] -or $value -is [long] -or $value -is [int]) -and $value -ge 0) "Non-numeric/negative timing for $key."
    }
    if ($InnoStatus -ne 'built') { Assert ($metadata.stageSeconds.inno -eq 0) 'Skipped Inno has nonzero time.' }
}
function Assert-Artifacts($Case, [bool]$Msi, [bool]$Inno) {
    $release = Join-Path $Case.Directory 'release'
    $zip = Join-Path $release 'HandyPlayer-1.2.3-win-x64.zip'
    Assert (Test-Path -LiteralPath $zip) 'ZIP missing.'
    Assert ((Test-Path -LiteralPath (Join-Path $release 'HandyPlayer-1.2.3-win-x64.msi')) -eq $Msi) 'Wrong MSI presence.'
    Assert ((Test-Path -LiteralPath (Join-Path $release 'HandyPlayer-1.2.3-win-x64-setup.exe')) -eq $Inno) 'Wrong setup.exe presence.'
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try { Assert ($archive.Entries.FullName -contains 'HandyPlayer-1.2.3-win-x64/HandyPlayer.exe') 'ZIP lost its portable base directory.' }
    finally { $archive.Dispose() }
    $checksums = @(Get-Content -LiteralPath (Join-Path $release 'SHA256SUMS-win-x64.txt'))
    Assert ($checksums.Count -eq (2 + [int]$Msi + [int]$Inno)) 'Checksum artifact set changed.'
    foreach ($entry in $checksums) {
        $parts = $entry -split '  ', 2
        Assert ((Get-FileHash -LiteralPath (Join-Path $release $parts[1]) -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $parts[0]) 'Incorrect artifact checksum.'
    }
}

$full = Invoke-PublisherCase 'default-full'
Assert-Metadata $full 'Full' $false 'built'
Assert-Artifacts $full $true $true
Assert (@($full.Calls | Where-Object { $_ -like 'iscc *' }).Count -eq 1) 'Default Full must invoke Inno exactly once.'

$candidate = Invoke-PublisherCase 'candidate-inno-present' -Profile 'candidate'
Assert-Metadata $candidate 'Candidate' $true 'skipped-profile'
Assert-Artifacts $candidate $true $false
Assert (@($candidate.Calls | Where-Object { $_ -match '^(discover iscc|inno-probe |iscc )' }).Count -eq 0) 'Candidate discovered, probed or invoked Inno.'
Assert (@($candidate.Calls | Where-Object { $_ -like 'wix build *' }).Count -eq 1) 'Candidate did not build MSI.'
Assert (@($candidate.Calls | Where-Object { $_ -like 'wix extension *' }).Count -eq 1) 'Candidate bypassed WiX extension preparation.'
Assert (@($candidate.Calls | Where-Object { $_ -eq 'wix extension add -g WixToolset.UI.wixext/6.0.2' }).Count -eq 1) 'Extension restore must use the checkout-owned cache and exact version.'
Assert (@($candidate.Calls | Where-Object { $_ -like 'wix build *-ext WixToolset.UI.wixext/6.0.2 -ct 2 *' }).Count -eq 1) 'MSI build must load the exact extension and bound cabinet workers.'
Assert (@($candidate.Calls | Where-Object { $_ -eq 'native-restore' }).Count -eq 1) 'Candidate bypassed native restore.'
Assert (@($candidate.Calls | Where-Object { $_ -like 'dotnet publish *--self-contained*' }).Count -eq 1) 'Candidate bypassed self-contained publish.'
Assert (@($candidate.Output | Where-Object { $_ -like '*Skipping setup.exe (Candidate profile*' }).Count -eq 1) 'Candidate skip log missing.'

$reusedRestore = Invoke-PublisherCase 'candidate-no-restore' -Profile Candidate -NoRestore
Assert (@($reusedRestore.Calls | Where-Object { $_ -match '^dotnet publish .* --no-restore$' }).Count -eq 1) 'NoRestore must remain a single native CLI argument.'
Assert-Artifacts $reusedRestore $true $false

$missingCandidate = Invoke-PublisherCase 'candidate-wix-missing' -Profile Candidate -Wix no -Success $false
Assert (@($missingCandidate.Calls | Where-Object { $_ -match '^(discover iscc|inno-probe |iscc )' }).Count -eq 0) 'Missing required MSI reached Inno.'
$null = Invoke-PublisherCase 'full-required-wix-missing' -Profile Full -RequireMsi -Wix no -Success $false
$optional = Invoke-PublisherCase 'default-full-wix-missing' -Wix no
Assert-Metadata $optional 'Full' $false 'built'
Assert-Artifacts $optional $false $true
Assert ($optional.Metadata.stageSeconds.msi -eq 0) 'Unavailable optional MSI has nonzero time.'
$unavailable = Invoke-PublisherCase 'full-inno-unavailable' -Profile fUlL -Inno no
Assert-Metadata $unavailable 'Full' $false 'unavailable'
Assert-Artifacts $unavailable $true $false
Assert (@($unavailable.Calls | Where-Object { $_ -like 'inno-probe *' }).Count -ge 1) 'Full lost Inno fallback discovery.'

foreach ($failure in @('native', 'publish', 'extension', 'msi', 'msi-missing-output', 'checksums')) {
    $failed = Invoke-PublisherCase "candidate-failure-$failure" -Profile Candidate -Failure $failure -Success $false
    Assert (@($failed.Calls | Where-Object { $_ -match '^(discover iscc|inno-probe |iscc )' }).Count -eq 0) "Failed Candidate reached Inno: $failure"
    if ($failure -eq 'native') { Assert (@($failed.Calls | Where-Object { $_ -like 'dotnet publish *' }).Count -eq 0) 'Native failure did not stop publishing.' }
    if ($failure -eq 'publish') { Assert (@($failed.Calls | Where-Object { $_ -like 'wix *' }).Count -eq 0) 'Publish failure did not stop installers.' }
    if ($failure -eq 'extension') { Assert (@($failed.Calls | Where-Object { $_ -like 'wix build *' }).Count -eq 0) 'Extension restore failure did not stop MSI build.' }
}
foreach ($failure in @('inno', 'inno-missing-output')) {
    $failed = Invoke-PublisherCase "full-failure-$failure" -Failure $failure -Success $false
    Assert (-not (Test-Path -LiteralPath (Join-Path $failed.Directory 'release/SHA256SUMS-win-x64.txt'))) 'Failed Inno reached checksum success.'
}
$invalidLock = Invoke-PublisherCase 'candidate-invalid-native-lock' -Profile Candidate -SchemaVersion 2 -Success $false
Assert (@($invalidLock.Calls | Where-Object { $_ -eq 'native-restore' -or $_ -like 'dotnet publish *' }).Count -eq 0) 'Candidate bypassed native lock schema gate.'
Write-Output "Publish profile, artifact, timing metadata and failure-gate contracts passed (mocked host tools). Evidence: $fixture"
