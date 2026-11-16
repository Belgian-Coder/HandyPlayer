#Requires -Version 7
# Real file-set/hash regression for comparisons whose inputs change between MSIs.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. "$root/eng/verify-windows-package.ps1"
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile("$root/benchmarks/measure-windows-msi.ps1",[ref]$tokens,[ref]$errors)
if ($errors) { throw 'MSI benchmark does not parse.' }
foreach ($name in @('Assert-HandyMsiBenchmarkInputs','Assert-HandyMsiBenchmarkVerification')) {
    $function=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
    if (-not $function) { throw "Benchmark guard missing: $name" }
    . ([scriptblock]::Create($function.Extent.Text))
}
$fixture=Join-Path $root ('.harness/msi-benchmark-contracts/'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $fixture
function Must-Fail([scriptblock]$Action,[string]$Pattern) {
    $caught=$null
    try { & $Action } catch { $caught=$_ }
    if (-not $caught -or $caught.Exception.Message -notlike $Pattern) { throw "Expected refusal: $Pattern" }
}
foreach ($change in @('add','rename','same-size-bytes')) {
    $directory=Join-Path $fixture $change
    $null=New-Item -ItemType Directory -Path $directory
    $file=Join-Path $directory 'HandyPlayer.exe'
    [IO.File]::WriteAllText($file,'original')
    $initial=@(Get-HandyPackageTreeFiles $directory)
    $hash=Get-HandyPackagePortableManifestHash $initial
    Assert-HandyMsiBenchmarkInputs $directory $hash $initial.Count
    $first=[pscustomobject]@{result='passed';portableManifest=[pscustomobject]@{sha256=$hash;fileCount=$initial.Count}}
    Assert-HandyMsiBenchmarkVerification $first $hash $initial.Count
    switch ($change) {
        'add' { [IO.File]::WriteAllText((Join-Path $directory 'new-file.dat'),'added between variants') }
        'rename' { Move-Item -LiteralPath $file -Destination (Join-Path $directory 'Renamed.exe') }
        'same-size-bytes' { [IO.File]::WriteAllText($file,'modified') }
    }
    Must-Fail { Assert-HandyMsiBenchmarkInputs $directory $hash $initial.Count } '*file set or bytes changed*'
    $later=@(Get-HandyPackageTreeFiles $directory)
    # Each MSI may individually match its then-current tree; the comparison
    # must still reject that second successful verifier result.
    $second=[pscustomobject]@{result='passed';portableManifest=[pscustomobject]@{
        sha256=(Get-HandyPackagePortableManifestHash $later);fileCount=$later.Count}}
    Must-Fail { Assert-HandyMsiBenchmarkVerification $second $hash $initial.Count } '*initial portable input manifest*'
}
# Exercise the real benchmark entry point too, mocking only tool/verification
# boundaries. This catches mutations after a variant's pre-build snapshot.
$entryRoot=Join-Path $fixture 'entry'
foreach ($child in @('eng','benchmarks')) { $null=New-Item -ItemType Directory -Path (Join-Path $entryRoot $child) -Force }
Copy-Item "$root/benchmarks/measure-windows-msi.ps1" (Join-Path $entryRoot 'benchmarks/measure-windows-msi.ps1')
Copy-Item "$root/installer-win.wxs" (Join-Path $entryRoot 'installer-win.wxs')
@'
function New-HandyOutputRoot([string]$Path) {
    $global:BenchmarkLastOutput=$Path
    $null=New-Item -ItemType Directory -Path $Path -Force
    return $Path
}
function dotnet { $global:LASTEXITCODE=0; return '1.1.1' }
function Invoke-MockBenchmarkWix {
    $global:LASTEXITCODE=0
    if ($args[0] -eq '--version') { return '6.0.2' }
    if ($args[0] -eq 'build') {
        $destination=$args[([array]::IndexOf($args,'-o')+1)]
        [IO.File]::WriteAllText($destination,'mock MSI')
        if ($global:BenchmarkMutation -and $args -contains 'CabCompression=medium') {
            $sourceArgument=$args | Where-Object { $_ -like 'SourceDir=*' }
            [IO.File]::WriteAllText((Join-Path $sourceArgument.Substring(10) 'added-between-variants.dat'),'added')
        }
    }
}
'@ | Set-Content (Join-Path $entryRoot 'harness-env.ps1')
'function Assert-HandyNativeNoLink([string]$Path) {}' | Set-Content (Join-Path $entryRoot 'eng/Native-Dependency.ps1')
@'
function Get-HandyWixPath([string]$ProjectRoot) { return 'Invoke-MockBenchmarkWix' }
function Initialize-HandyWindowsToolEnvironment([string]$ProjectRoot) {}
'@ | Set-Content (Join-Path $entryRoot 'eng/Windows-Tools.ps1')
$verifierWrapper=@'
param([string]$Msi,[string]$PortableDirectory,[string]$OutputDirectory)
$wrapperDotSourced=$MyInvocation.InvocationName -eq '.'
. '__REAL_VERIFIER__' -Msi $Msi -PortableDirectory $PortableDirectory -OutputDirectory $OutputDirectory
if (-not $wrapperDotSourced) {
    $files=@(Get-HandyPackageTreeFiles $PortableDirectory)
    $null=New-Item -ItemType Directory -Path $OutputDirectory
    @{result='passed';portableManifest=@{sha256=(Get-HandyPackagePortableManifestHash $files);fileCount=$files.Count}} |
        ConvertTo-Json -Depth 3 | Set-Content (Join-Path $OutputDirectory 'verification.json')
}
'@
$verifierWrapper.Replace('__REAL_VERIFIER__',"$root/eng/verify-windows-package.ps1") | Set-Content (Join-Path $entryRoot 'eng/verify-windows-package.ps1')
foreach ($mutate in @($false,$true)) {
    $directory=Join-Path $fixture ('entry-portable-'+$mutate)
    $null=New-Item -ItemType Directory -Path $directory
    [IO.File]::WriteAllText((Join-Path $directory 'HandyPlayer.exe'),'original')
    $global:BenchmarkMutation=$mutate
    if ($mutate) {
        Must-Fail { & (Join-Path $entryRoot 'benchmarks/measure-windows-msi.ps1') -PortableDirectory $directory | Out-Null } '*initial portable input manifest*'
        if (Test-Path (Join-Path $global:BenchmarkLastOutput 'measurements.json')) { throw 'Changed inputs produced a successful comparison report.' }
    } else {
        & (Join-Path $entryRoot 'benchmarks/measure-windows-msi.ps1') -PortableDirectory $directory | Out-Null
        $report=Get-Content (Join-Path $global:BenchmarkLastOutput 'measurements.json') -Raw | ConvertFrom-Json
        if ($report.measurements.Count -ne 2 -or $report.inputFileCount -ne 1) { throw 'Unchanged full benchmark fixture did not compare both variants.' }
    }
}
Write-Output "MSI benchmark entry point and additions/renames/same-size byte guards passed. Evidence: $fixture"
