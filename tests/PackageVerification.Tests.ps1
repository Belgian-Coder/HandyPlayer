#Requires -Version 7
# Offline contract tests for the read-only Windows MSI payload verifier.
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$verifier = Join-Path $repositoryRoot 'eng/verify-windows-package.ps1'
$testId = [Guid]::NewGuid().ToString('N')
$scratchRoot = Join-Path $repositoryRoot ".harness/package-verification-tests-$testId"
$outputRoot = Join-Path $repositoryRoot ".harness/msi-verifier-contract-tests-$testId"
$portable = Join-Path $scratchRoot 'portable'
$msi = Join-Path $scratchRoot 'fixture.msi'
$script:caseCount = 0
$script:testMsiPath = $msi
$script:testListing = $null
$script:testPayloadOverride = $null
$script:testPayloads = @{
    filApp = [Text.Encoding]::UTF8.GetBytes('app!')
    filReadme = [Text.Encoding]::UTF8.GetBytes('readme')
}

class PackageVerifierMockRecord {
    [string[]]$Values
    PackageVerifierMockRecord([string[]]$Values) { $this.Values = $Values }
    [string] StringData([int]$Index) { return $this.Values[$Index - 1] }
}

class PackageVerifierMockView {
    [object[]]$Rows
    [int]$Index = 0
    PackageVerifierMockView([object[]]$Rows) { $this.Rows = $Rows }
    [object] Execute() { return $null }
    [object] Fetch() {
        if ($this.Index -ge $this.Rows.Count) { return $null }
        $row = $this.Rows[$this.Index]
        $this.Index++
        return $row
    }
}

class PackageVerifierMockDatabase {
    [PackageVerifierMockView]$View
    PackageVerifierMockDatabase([PackageVerifierMockView]$View) { $this.View = $View }
    [object] OpenView([string]$Query) { return $this.View }
}

. $verifier

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_ }
    Assert ($null -ne $caught) "Expected failure matching '$Pattern'."
    Assert ($caught.Exception.Message -like $Pattern) "Wrong failure: $($caught.Exception.Message)"
}

function Case([string]$Name, [scriptblock]$Action) {
    & $Action
    $script:caseCount++
    Write-Host "PASS $Name"
}

function Get-HandyPackageMsiMetadata([string]$Path) {
    Assert ($Path -ceq $script:testMsiPath) 'Verifier read an unexpected MSI path.'
    return [pscustomobject]@{
        DirectoryRows = @(
            [pscustomobject]@{ Directory = 'INSTALLFOLDER'; Directory_Parent = 'ProgramFiles64Folder'; DefaultDir = 'HANDY|HandyPlayer' }
            [pscustomobject]@{ Directory = 'MEDIA_DIR'; Directory_Parent = 'INSTALLFOLDER'; DefaultDir = 'MEDIA|Media' }
        )
        ComponentRows = @(
            [pscustomobject]@{ Component = 'AppFiles'; Directory_ = 'INSTALLFOLDER'; KeyPath = 'filApp' }
            [pscustomobject]@{ Component = 'AssetFiles'; Directory_ = 'MEDIA_DIR'; KeyPath = 'filReadme' }
        )
        FileRows = @(
            [pscustomobject]@{ File = 'filApp'; Component_ = 'AppFiles'; FileName = 'HandyPlayer.exe'; FileSize = '4'; Sequence = '1' }
            [pscustomobject]@{ File = 'filReadme'; Component_ = 'AssetFiles'; FileName = 'README~1.TXT|README.txt'; FileSize = '6'; Sequence = '2' }
        )
        MediaRows = @([pscustomobject]@{ DiskId = '1'; LastSequence = '2'; Cabinet = '#cab1.cab' })
    }
}

function Get-HandyPackage7Zip { return 'mock-sevenzip.exe' }

function Invoke-HandyPackage7Zip {
    param([string]$Executable, [string[]]$Arguments)
    Assert ($Executable -ceq 'mock-sevenzip.exe') 'Verifier used an unexpected 7-Zip executable.'
    $argv = @($Arguments)
    $global:LASTEXITCODE = 0
    if ($argv[0] -eq 'l') { return $script:testListing }

    Assert ($argv[0] -eq 'x') 'Verifier issued an unexpected 7-Zip operation.'
    $output = @($argv | Where-Object { $_.StartsWith('-o', [StringComparison]::Ordinal) } | Select-Object -First 1)
    Assert ($output.Count -eq 1) '7-Zip extraction must name its fresh output directory.'
    $destination = $output[0].Substring(2)
    $null = [IO.Directory]::CreateDirectory($destination)
    if ($argv -contains $script:testMsiPath) {
        Assert ($argv -contains 'cab1.cab') 'MSI extraction must select the recognized embedded CAB only.'
        [IO.File]::WriteAllBytes((Join-Path $destination 'cab1.cab'), [Text.Encoding]::UTF8.GetBytes('mock cabinet'))
        return 'mock stream extracted'
    }

    $separator = [Array]::IndexOf($argv, '--')
    Assert ($separator -ge 0) 'CAB extraction must stop 7-Zip switch parsing.'
    $members = @($argv[($separator + 2)..($argv.Count - 1)])
    foreach ($member in $members) {
        $bytes = if ($script:testPayloadOverride -and $member -eq 'filApp') { $script:testPayloadOverride } else { $script:testPayloads[$member] }
        [IO.File]::WriteAllBytes((Join-Path $destination $member), $bytes)
    }
    return 'mock payload extracted'
}

function New-TestListing([string]$FirstPath = 'filApp') {
    return "7-Zip fixture`nPath = cab1.cab`nType = Cab`n`n----------`nPath = $FirstPath`nSize = 4`nAttributes = A`n`nPath = filReadme`nSize = 6`nAttributes = A`n"
}

function Invoke-TestVerification([string]$DirectoryName) {
    $destination = Join-Path $outputRoot $DirectoryName
    return Invoke-HandyWindowsPackageVerification -Msi $script:testMsiPath -PortableDirectory $portable -OutputDirectory $destination
}

$null = [IO.Directory]::CreateDirectory($portable)
    [IO.File]::WriteAllBytes((Join-Path $portable 'HandyPlayer.exe'), $script:testPayloads.filApp)
    $assets = Join-Path $portable 'Media'
    $null = [IO.Directory]::CreateDirectory($assets)
    [IO.File]::WriteAllBytes((Join-Path $assets 'README.txt'), $script:testPayloads.filReadme)
    [IO.File]::WriteAllBytes($script:testMsiPath, [Text.Encoding]::UTF8.GetBytes('mock msi source'))
    $script:testListing = New-TestListing

Case 'MSI COM Execute output is suppressed from table rows' {
    $mockRows = [object[]]@([PackageVerifierMockRecord]::new([string[]]@('INSTALLFOLDER', 'ProgramFiles64Folder', 'HandyPlayer')))
    $mockView = [PackageVerifierMockView]::new($mockRows)
    $mockDatabase = [PackageVerifierMockDatabase]::new($mockView)
    $rows = @(Get-HandyPackageMsiRows $mockDatabase 'mock query' @('Directory', 'Directory_Parent', 'DefaultDir'))
    Assert ($rows.Count -eq 1) 'MSI table reader included a non-row output from COM Execute.'
    Assert ($rows[0].Directory -ceq 'INSTALLFOLDER') 'MSI table reader lost the first actual row.'
}

Case 'MSI directory target precedes its optional source name' {
    Assert ((ConvertFrom-HandyPackageDirectoryName 'TARGET~1|Target Folder:SOURCE~1|Source Folder') -ceq 'Target Folder') 'Directory target:source resolution selected the source name.'
}

    Case 'exact portable-to-MSI payload match and JSON report' {
        $result = Invoke-TestVerification 'success'
        $summary = Get-Content -LiteralPath (Join-Path (Join-Path $outputRoot 'success') 'verification.json') -Raw | ConvertFrom-Json
        Assert ($summary.result -ceq 'passed') 'Verifier did not write a passed result.'
        Assert ($summary.sourceMsi.sha256 -match '^[a-f0-9]{64}$') 'MSI source SHA-256 is missing.'
        Assert ($summary.portableManifest.sha256 -match '^[a-f0-9]{64}$') 'Portable manifest SHA-256 is missing.'
        Assert ($summary.exactFileVerification.fileCount -eq 2) 'Exact file verification count is wrong.'
        Assert ($summary.exactFileVerification.totalBytes -eq 10) 'Exact verified byte count is wrong.'
        Assert (-not $summary.installationPerformed -and -not $summary.applicationLaunched -and -not $summary.installedAcceptancePerformed) 'Report overstates installed acceptance.'
        Assert ($result.exactFileVerification.files[1].path -ceq 'Media/README.txt') 'MSI short|long filename resolution failed.'
    }

    Case 'mismatching extracted bytes fail and retain no success summary' {
        $script:testPayloadOverride = [Text.Encoding]::UTF8.GetBytes('bad!')
        Assert-Throws { Invoke-TestVerification 'hash-mismatch' } '*MSI payload bytes differ from portable file*'
        Assert (-not (Test-Path -LiteralPath (Join-Path (Join-Path $outputRoot 'hash-mismatch') 'verification.json'))) 'Mismatch wrote a success summary.'
        $script:testPayloadOverride = $null
    }

    Case 'unsafe cabinet member is rejected before payload extraction' {
        $script:testListing = New-TestListing '../filApp'
        Assert-Throws { Invoke-TestVerification 'unsafe-member' } '*Unsafe CAB member path*'
        Assert (-not (Test-Path -LiteralPath (Join-Path (Join-Path $outputRoot 'unsafe-member') 'verification.json'))) 'Unsafe CAB wrote a success summary.'
        $script:testListing = New-TestListing
    }

    Case 'outside output path is rejected' {
        $outside = Join-Path $repositoryRoot "package-verification-outside-$testId"
        Assert-Throws {
            Invoke-HandyWindowsPackageVerification -Msi $script:testMsiPath -PortableDirectory $portable -OutputDirectory $outside
        } '*OutputDirectory must be a new child of this checkout .harness directory*'
    }

    Case 'existing output is preserved' {
        $existing = Join-Path $outputRoot 'existing'
        $null = [IO.Directory]::CreateDirectory($existing)
        $sentinel = Join-Path $existing 'keep.txt'
        [IO.File]::WriteAllText($sentinel, 'preserve')
        Assert-Throws { Invoke-TestVerification 'existing' } '*OutputDirectory must be fresh and absent*'
        Assert ((Get-Content -LiteralPath $sentinel -Raw) -ceq 'preserve') 'Verifier changed existing output.'
    }

Write-Host "Passed $script:caseCount package-verification contract cases."
