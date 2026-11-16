#Requires -Version 7
# Real concurrent restores from an offline, task-owned feed. No app build,
# package deletion, network feed or existing package-cache entry is required.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
. "$root/harness-env.ps1"
$fixture = New-HandyOutputRoot (Join-Path $root ('.harness/nuget-contracts/' + [guid]::NewGuid().ToString('N')))
$feed = Join-Path $fixture 'feed'
$null = New-Item -ItemType Directory -Path $feed
$id = 'HandyHarness.Concurrent.' + [guid]::NewGuid().ToString('N')
$package = Join-Path $feed "$id.1.0.0.nupkg"
$payload = [Text.Encoding]::UTF8.GetBytes(('offline concurrent extraction fixture ' * 32768))
$zip = [IO.Compression.ZipFile]::Open($package, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in @(
        @{ name="$id.nuspec"; bytes=[Text.Encoding]::UTF8.GetBytes("<package><metadata><id>$id</id><version>1.0.0</version><authors>Harness</authors><description>Offline concurrency fixture</description></metadata></package>") },
        @{ name='content/probe.txt'; bytes=$payload }
    )) {
        $stream = $zip.CreateEntry($entry.name).Open()
        try { $stream.Write($entry.bytes, 0, $entry.bytes.Length) } finally { $stream.Dispose() }
    }
} finally { $zip.Dispose() }
$packageCache = Join-Path $env:NUGET_PACKAGES $id.ToLowerInvariant()
if (Test-Path -LiteralPath $packageCache) { throw 'Unique fixture package unexpectedly exists; no cache entries are removed.' }
$common = & git -C $root rev-parse --path-format=absolute --git-common-dir
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve fixture shared project cache.' }
$runner = @'
param([string]$Checkout, [string]$Common, [string]$Barrier, [string]$Ready)
$ErrorActionPreference='Stop'
# Both disposable consumers are inside the same real project. The metadata helper
# discovers its common root without overriding Git or changing the real index.
$env:TESTINGPLATFORM_PIPE_DIRECTORY=$null
$env:BUILD_RELEASE_PROJECT_ROOT=$null
. (Join-Path $Checkout 'harness-env.ps1')
if ([IO.Path]::GetFullPath($primaryRoot) -ne [IO.Path]::GetFullPath((Split-Path $Common -Parent))) {
    throw 'Real project discovery selected a different canonical package owner.'
}
[ordered]@{
    packages=$env:NUGET_PACKAGES; scratch=$env:NUGET_SCRATCH
    cliHome=$env:DOTNET_CLI_HOME; http=$env:NUGET_HTTP_CACHE_PATH; temp=$env:TEMP; plugins=$env:NUGET_PLUGINS_CACHE_PATH
} | ConvertTo-Json | Set-Content (Join-Path $Checkout 'environment.json')
[IO.File]::WriteAllText($Ready,'ready')
$limit=[DateTime]::UtcNow.AddSeconds(30)
while (-not (Test-Path -LiteralPath $Barrier)) {
    if ([DateTime]::UtcNow -gt $limit) { throw 'Concurrent fixture start barrier timed out.' }
    Start-Sleep -Milliseconds 25
}
Set-Location $Checkout
& dotnet restore consumer.csproj --configfile NuGet.Config --disable-parallel -maxcpucount:1 --nologo
exit $LASTEXITCODE
'@
$runnerPath = Join-Path $fixture 'restore.ps1'
$runner | Set-Content -LiteralPath $runnerPath
$barrier = Join-Path $fixture 'start'
$jobs = @()
try {
    foreach ($index in 1..2) {
        $checkout = Join-Path $fixture "checkout-$index"
        $null = New-Item -ItemType Directory -Path $checkout
        Copy-Item -LiteralPath "$root/harness-env.ps1" -Destination (Join-Path $checkout 'harness-env.ps1')
        $null = New-Item -ItemType Directory -Path (Join-Path $checkout 'eng')
        Copy-Item -LiteralPath "$root/eng/Project-Root.ps1" -Destination (Join-Path $checkout 'eng/Project-Root.ps1')
        "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net11.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><PackageReference Include=`"$id`" Version=`"1.0.0`" /></ItemGroup></Project>" | Set-Content (Join-Path $checkout 'consumer.csproj')
        $feedXml = [Security.SecurityElement]::Escape($feed)
        "<configuration><packageSources><clear/><add key=`"fixture`" value=`"$feedXml`"/></packageSources></configuration>" | Set-Content (Join-Path $checkout 'NuGet.Config')
        $ready = Join-Path $checkout 'ready'
        $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        foreach ($argument in @('-NoProfile','-NonInteractive','-File',$runnerPath,'-Checkout',$checkout,'-Common',$common,'-Barrier',$barrier,'-Ready',$ready)) {
            $start.ArgumentList.Add($argument)
        }
        $process = [Diagnostics.Process]::Start($start)
        $jobs += [pscustomobject]@{ Process=$process; Checkout=$checkout; Ready=$ready
            Stdout=$process.StandardOutput.ReadToEndAsync(); Stderr=$process.StandardError.ReadToEndAsync() }
    }
    $limit = [DateTime]::UtcNow.AddSeconds(30)
    while (@($jobs | Where-Object { -not (Test-Path -LiteralPath $_.Ready) }).Count) {
        if ([DateTime]::UtcNow -gt $limit -or @($jobs | Where-Object { $_.Process.HasExited }).Count) {
            throw 'A restore fixture did not reach the shared start barrier.'
        }
        Start-Sleep -Milliseconds 25
    }
    [IO.File]::WriteAllText($barrier,'start')
    foreach ($job in $jobs) {
        if (-not $job.Process.WaitForExit(60000)) { throw 'Concurrent fixture restore timed out.' }
        $job.Stdout.GetAwaiter().GetResult() | Set-Content (Join-Path $job.Checkout 'restore.log')
        $job.Stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $job.Checkout 'restore.stderr.log')
        if ($job.Process.ExitCode -ne 0) { throw "Offline restore failed; inspect $($job.Checkout)/restore.log." }
    }
    $envs = @($jobs | ForEach-Object { Get-Content (Join-Path $_.Checkout 'environment.json') -Raw | ConvertFrom-Json })
    if ($envs[0].packages -ne $envs[1].packages -or $envs[0].scratch -ne $envs[1].scratch -or
        $envs[0].scratch -ne (Join-Path (Split-Path $envs[0].packages -Parent) 'scratch')) {
        throw 'Consumers of the shared package cache did not share NuGet coordination locks.'
    }
    if ($envs[0].http -ne $envs[1].http -or $envs[0].http -ne (Join-Path (Split-Path $envs[0].packages -Parent) 'http')) {
        throw 'Consumers did not reuse the same OS/architecture HTTP download cache.'
    }
    foreach ($name in @('cliHome','plugins','temp')) {
        if ($envs[0].$name -eq $envs[1].$name) { throw "Checkout mutable $name was shared." }
    }
    $cached = Join-Path $packageCache '1.0.0'
    if ((Get-FileHash -LiteralPath (Join-Path $cached ($id.ToLowerInvariant()+'.1.0.0.nupkg'))).Hash -ne (Get-FileHash -LiteralPath $package).Hash) {
        throw 'Concurrent extraction corrupted the cached archive.'
    }
    $expectedPayload = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($payload))
    if ((Get-FileHash -LiteralPath (Join-Path $cached 'content/probe.txt')).Hash -ne $expectedPayload -or
        -not (Test-Path -LiteralPath (Join-Path $cached '.nupkg.metadata'))) {
        throw 'Concurrent extraction is incomplete or corrupt.'
    }
    Write-Output "Two concurrent offline missing-package restores passed with shared package/HTTP/scratch and isolated CLI/plugins/TEMP. Evidence: $fixture"
} finally {
    foreach ($job in $jobs) {
        if (-not $job.Process.HasExited) { $job.Process.Kill($true); $job.Process.WaitForExit() }
        $job.Process.Dispose()
    }
    # Keep the unique feed, package, logs and warm cache entry as evidence.
}
