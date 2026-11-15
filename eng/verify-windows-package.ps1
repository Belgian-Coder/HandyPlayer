#Requires -Version 7
[CmdletBinding()]
param(
    [string]$Msi,
    [string]$PortableDirectory,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:HandyPackageVerifierDotSourced = $MyInvocation.InvocationName -eq '.'

function Get-HandyPackageFullPath {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Name)
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw "$Name must be an absolute path." }
    return [IO.Path]::GetFullPath($Path)
}

function Assert-HandyPackageNoLinks {
    param([Parameter(Mandatory)][string]$Path)
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            ($item.PSObject.Properties['LinkType'] -and $item.LinkType))) {
            throw "Links and reparse points are not allowed in package verification paths: $cursor"
        }
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent -or $parent.FullName -eq $cursor) { break }
        $cursor = $parent.FullName
    }
}

function Assert-HandyPackageSafeSegment {
    param([Parameter(Mandatory)][string]$Segment)
    if ([string]::IsNullOrWhiteSpace($Segment) -or $Segment -in @('.', '..') -or
        $Segment -match '[:\x00-\x1f\\/<>"|?*]' -or $Segment -match '[. ]$' -or
        $Segment -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
        throw "Unsafe Windows package path segment: $Segment"
    }
}

function ConvertTo-HandyPackageRelativePath {
    param([Parameter(Mandatory)][string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -match '^[\\/]' -or $Path -match '[:\x00-\x1f]') {
        throw "Unsafe Windows package relative path: $Path"
    }
    $parts = $Path -split '[\\/]'
    foreach ($part in $parts) { Assert-HandyPackageSafeSegment $part }
    return [string]::Join('/', $parts)
}

function Get-HandyPackageTreeFiles {
    param([Parameter(Mandatory)][string]$Root)
    Assert-HandyPackageNoLinks $Root
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    $files = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Links and reparse points are not allowed in package trees: $entry"
            }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($entry)
                continue
            }
            $relative = [IO.Path]::GetRelativePath($Root, $entry).Replace([string][IO.Path]::DirectorySeparatorChar, '/')
            $relative = ConvertTo-HandyPackageRelativePath $relative
            if (-not $seen.Add($relative)) { throw "Duplicate Windows package path: $relative" }
            $item = Get-Item -LiteralPath $entry -Force
            $files.Add([pscustomobject]@{
                path = $relative
                fullPath = $item.FullName
                bytes = [long]$item.Length
                sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            })
        }
    }
    return $files.ToArray()
}

function Get-HandyPackagePortableManifestHash {
    param([Parameter(Mandatory)][object[]]$Files)
    $paths = [string[]]@($Files | ForEach-Object { $_.path })
    [Array]::Sort($paths, [StringComparer]::OrdinalIgnoreCase)
    $rows = foreach ($path in $paths) {
        $file = $Files | Where-Object { $_.path -ceq $path } | Select-Object -First 1
        "$($file.path)`t$($file.bytes)`t$($file.sha256)"
    }
    $canonical = ([string]::Join("`n", [string[]]$rows)) + "`n"
    $encoding = [Text.UTF8Encoding]::new($false)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($encoding.GetBytes($canonical))).ToLowerInvariant()
}

function Get-HandyPackageMsiRows {
    param(
        [Parameter(Mandatory)][object]$Database,
        [Parameter(Mandatory)][string]$Query,
        [Parameter(Mandatory)][string[]]$Columns
    )
    $view = $null
    try {
        $view = $Database.OpenView($Query)
        # COM returns VT_EMPTY here, which PowerShell can surface as a leading $null
        # pipeline item. Suppress it so callers only receive table rows.
        $null = $view.Execute()
        $rows = [Collections.Generic.List[object]]::new()
        while ($null -ne ($record = $view.Fetch())) {
            try {
                $row = [ordered]@{}
                for ($i = 0; $i -lt $Columns.Count; $i++) {
                    $row[$Columns[$i]] = [string]$record.StringData($i + 1)
                }
                $rows.Add([pscustomobject]$row)
            } finally {
                if ([Runtime.InteropServices.Marshal]::IsComObject($record)) {
                    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
                }
            }
        }
        return $rows.ToArray()
    } finally {
        if ($null -ne $view -and [Runtime.InteropServices.Marshal]::IsComObject($view)) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
        }
    }
}

function Get-HandyPackageMsiMetadata {
    param([Parameter(Mandatory)][string]$Path)
    $installer = $null
    $database = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        # 0 is msiOpenDatabaseModeReadOnly. This never installs or transforms the package.
        $database = $installer.OpenDatabase($Path, 0)
        $directories = @(Get-HandyPackageMsiRows $database 'SELECT `Directory`, Directory_Parent, DefaultDir FROM `Directory`' @('Directory', 'Directory_Parent', 'DefaultDir'))
        $components = @(Get-HandyPackageMsiRows $database 'SELECT Component, Directory_, KeyPath FROM Component' @('Component', 'Directory_', 'KeyPath'))
        $files = @(Get-HandyPackageMsiRows $database 'SELECT `File`, Component_, FileName, FileSize, Sequence FROM `File`' @('File', 'Component_', 'FileName', 'FileSize', 'Sequence'))
        $media = @(Get-HandyPackageMsiRows $database 'SELECT DiskId, LastSequence, Cabinet FROM Media' @('DiskId', 'LastSequence', 'Cabinet'))
        return [pscustomobject]@{ DirectoryRows = $directories; ComponentRows = $components; FileRows = $files; MediaRows = $media }
    } catch {
        throw 'Could not read Windows Installer tables through the read-only WindowsInstaller.Installer COM interface.'
    } finally {
        if ($null -ne $database -and [Runtime.InteropServices.Marshal]::IsComObject($database)) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
        }
        if ($null -ne $installer -and [Runtime.InteropServices.Marshal]::IsComObject($installer)) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
        }
    }
}

function ConvertFrom-HandyPackageMsiLongName {
    param([Parameter(Mandatory)][string]$Value, [Parameter(Mandatory)][string]$Name)
    if ([string]::IsNullOrWhiteSpace($Value)) { throw "MSI $Name is empty." }
    if ([regex]::Matches($Value, '\|').Count -gt 1) { throw "MSI $Name has an invalid short|long name." }
    $parts = $Value.Split('|')
    $longName = if ($parts.Count -eq 2) { $parts[1] } else { $parts[0] }
    Assert-HandyPackageSafeSegment $longName
    return $longName
}

function ConvertFrom-HandyPackageDirectoryName {
    param([Parameter(Mandatory)][string]$DefaultDir)
    if ([string]::IsNullOrWhiteSpace($DefaultDir)) { throw 'MSI Directory.DefaultDir is empty.' }
    $parts = $DefaultDir.Split(':')
    if ($parts.Count -gt 2) { throw 'MSI Directory.DefaultDir has an invalid target:source value.' }
    $target = $parts[0]
    if ($target -eq '.') { return '' }
    if ([string]::IsNullOrWhiteSpace($target)) { throw 'MSI Directory.DefaultDir has an empty target name.' }
    if ([regex]::Matches($target, '\|').Count -gt 1) { throw 'MSI Directory.DefaultDir has an invalid short|long name.' }
    $nameParts = $target.Split('|')
    $name = if ($nameParts.Count -eq 2) { $nameParts[1] } else { $nameParts[0] }
    Assert-HandyPackageSafeSegment $name
    return $name
}

function ConvertTo-HandyPackageLong {
    param([Parameter(Mandatory)][string]$Value, [Parameter(Mandatory)][string]$Name)
    $number = 0L
    if (-not [long]::TryParse($Value, [Globalization.NumberStyles]::Integer,
        [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or $number -lt 0) {
        throw "MSI $Name is not a non-negative integer."
    }
    return $number
}

function Get-HandyPackageMsiFileMap {
    param([Parameter(Mandatory)][object]$Metadata)
    $directoryRows = @($Metadata.DirectoryRows)
    $componentRows = @($Metadata.ComponentRows)
    $fileRows = @($Metadata.FileRows)
    $mediaRows = @($Metadata.MediaRows)
    if ($directoryRows.Count -eq 0 -or $componentRows.Count -eq 0 -or $fileRows.Count -eq 0 -or $mediaRows.Count -eq 0) {
        throw 'MSI is missing required Directory, Component, File, or Media rows.'
    }

    $directories = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $directoryRows) {
        if ([string]::IsNullOrWhiteSpace($row.Directory) -or $directories.ContainsKey([string]$row.Directory)) {
            throw 'MSI Directory table contains an empty or duplicate identifier.'
        }
        $directories.Add([string]$row.Directory, $row)
    }
    if (-not $directories.ContainsKey('INSTALLFOLDER')) { throw 'MSI has no INSTALLFOLDER directory.' }

    $componentDirectories = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $componentRows) {
        if ([string]::IsNullOrWhiteSpace($row.Component) -or $componentDirectories.ContainsKey([string]$row.Component)) {
            throw 'MSI Component table contains an empty or duplicate identifier.'
        }
        $componentDirectories.Add([string]$row.Component, [string]$row.Directory_)
    }

    $relativeDirectories = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    $relativeDirectories.Add('INSTALLFOLDER', '')
    $remaining = [Collections.Generic.List[object]]::new()
    foreach ($row in $directoryRows) { if ([string]$row.Directory -ine 'INSTALLFOLDER') { $remaining.Add($row) } }
    $resolvedPathOwners = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($remaining.Count -gt 0) {
        $progress = $false
        for ($i = $remaining.Count - 1; $i -ge 0; $i--) {
            $row = $remaining[$i]
            $parentId = [string]$row.Directory_Parent
            if (-not $relativeDirectories.ContainsKey($parentId)) { continue }
            $parentPath = $relativeDirectories[$parentId]
            $segment = ConvertFrom-HandyPackageDirectoryName ([string]$row.DefaultDir)
            $relative = if (-not $segment) { $parentPath } elseif ($parentPath) { "$parentPath/$segment" } else { $segment }
            if ($resolvedPathOwners.ContainsKey($relative)) { throw "MSI Directory rows resolve to a duplicate install path: $relative" }
            $relativeDirectories.Add([string]$row.Directory, $relative)
            $resolvedPathOwners.Add($relative, [string]$row.Directory)
            $remaining.RemoveAt($i)
            $progress = $true
        }
        if (-not $progress) { break }
    }

    $files = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    $sequenceOwners = [Collections.Generic.Dictionary[long, string]]::new()
    foreach ($row in $fileRows) {
        $fileId = [string]$row.File
        if ($fileId -notmatch '^[A-Za-z_][A-Za-z0-9_.]{0,71}$' -or $files.ContainsKey($fileId)) {
            throw 'MSI File table contains an invalid or duplicate File identifier.'
        }
        if (-not $componentDirectories.ContainsKey([string]$row.Component_)) { throw "MSI File $fileId references an unknown Component." }
        $directoryId = $componentDirectories[[string]$row.Component_]
        if (-not $relativeDirectories.ContainsKey($directoryId)) { throw "MSI File $fileId is not beneath INSTALLFOLDER." }
        $size = ConvertTo-HandyPackageLong ([string]$row.FileSize) "FileSize for $fileId"
        $sequence = ConvertTo-HandyPackageLong ([string]$row.Sequence) "Sequence for $fileId"
        if ($sequence -le 0 -or $sequenceOwners.ContainsKey($sequence)) { throw 'MSI File sequences must be positive and unique.' }
        $name = ConvertFrom-HandyPackageMsiLongName ([string]$row.FileName) "FileName for $fileId"
        $parentPath = $relativeDirectories[$directoryId]
        $relativePath = if ($parentPath) { "$parentPath/$name" } else { $name }
        $relativePath = ConvertTo-HandyPackageRelativePath $relativePath
        $file = [pscustomobject]@{ fileId = $fileId; path = $relativePath; bytes = $size; sequence = $sequence }
        $files.Add($fileId, $file)
        $sequenceOwners.Add($sequence, $fileId)
    }

    $cabinetRows = [Collections.Generic.List[object]]::new()
    $cabinetNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $lastSequence = 0L
    foreach ($row in ($mediaRows | Sort-Object { ConvertTo-HandyPackageLong ([string]$_.LastSequence) 'Media.LastSequence' })) {
        $boundary = ConvertTo-HandyPackageLong ([string]$row.LastSequence) 'Media.LastSequence'
        if ($boundary -le $lastSequence) { throw 'MSI Media.LastSequence values must be positive and strictly increasing.' }
        $cabinet = [string]$row.Cabinet
        if ($cabinet -cnotmatch '^#([A-Za-z0-9_.-]+\.cab)$') { throw 'MSI Media rows must name safe embedded CAB streams.' }
        $cabinetName = $Matches[1]
        if ($cabinetName -match '(^|\.)\.($|\.)' -or -not $cabinetNames.Add($cabinetName)) {
            throw 'MSI Media contains an unsafe or duplicate embedded CAB name.'
        }
        $cabinetFiles = @($files.Values | Where-Object { $_.sequence -gt $lastSequence -and $_.sequence -le $boundary } | Sort-Object sequence)
        if ($cabinetFiles.Count -eq 0) { throw "MSI embedded CAB $cabinetName has no File rows." }
        $cabinetRows.Add([pscustomobject]@{ name = $cabinetName; firstSequence = $lastSequence + 1; lastSequence = $boundary; files = $cabinetFiles })
        $lastSequence = $boundary
    }
    foreach ($file in $files.Values) {
        if (-not ($cabinetRows | Where-Object { $_.files.fileId -contains $file.fileId })) {
            throw "MSI File $($file.fileId) is not assigned to an embedded CAB."
        }
    }

    $paths = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $files.Values) {
        if ($paths.ContainsKey($file.path)) { throw "MSI File rows resolve to a duplicate install path: $($file.path)" }
        $paths.Add($file.path, $file)
    }
    return [pscustomobject]@{ FilesById = $files; FilesByPath = $paths; Cabinets = $cabinetRows.ToArray() }
}

function Get-HandyPackage7Zip {
    $command = Get-Command 7z -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) { return $command.Source }
    $fallback = 'C:\Program Files\7-Zip\7z.exe'
    if (Test-Path -LiteralPath $fallback -PathType Leaf) { return $fallback }
    throw '7-Zip is required for read-only MSI cabinet payload verification.'
}

function Invoke-HandyPackage7Zip {
    param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string[]]$Arguments)
    $PSNativeCommandUseErrorActionPreference = $false
    $global:LASTEXITCODE = 0
    try { $output = @(& $Executable @Arguments 2>&1) }
    catch { throw '7-Zip could not execute during package payload verification.' }
    if ($LASTEXITCODE -ne 0) { throw "7-Zip failed during package payload verification (exit $LASTEXITCODE)." }
    return [string]::Join("`n", [string[]]$output)
}

function Get-HandyPackage7ZipMembers {
    param([Parameter(Mandatory)][string]$Listing)
    $lines = $Listing -split '\r?\n'
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Trim() -eq '----------') { $start = $i + 1; break }
    }
    if ($start -lt 0) { throw '7-Zip cabinet member listing is missing its member section.' }
    $members = [Collections.Generic.List[object]]::new()
    $record = @{}
    for ($i = $start; $i -le $lines.Count; $i++) {
        $line = if ($i -eq $lines.Count) { '' } else { $lines[$i] }
        if ([string]::IsNullOrWhiteSpace($line)) {
            if ($record.Count -gt 0) { $members.Add($record); $record = @{} }
        } elseif ($line -match '^([^=]+?) = (.*)$') {
            $key = $Matches[1].Trim()
            if ($record.ContainsKey($key)) { throw '7-Zip cabinet listing has duplicate member metadata.' }
            $record[$key] = $Matches[2]
        } else { throw '7-Zip cabinet listing has unrecognized member metadata.' }
    }
    return $members.ToArray()
}

function Assert-HandyPackageCabinetMembers {
    param([Parameter(Mandatory)][object[]]$Members, [Parameter(Mandatory)][object[]]$ExpectedFiles)
    $expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $ExpectedFiles) { $expected.Add([string]$file.fileId, $file) }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($member in $Members) {
        if (-not $member.ContainsKey('Path')) { throw '7-Zip CAB member has no path.' }
        $memberPath = [string]$member.Path
        if ($memberPath -notmatch '^[A-Za-z_][A-Za-z0-9_.]{0,71}$' -or $memberPath -in @('.', '..')) {
            throw 'Unsafe CAB member path; expected a simple MSI FileId.'
        }
        if (-not $seen.Add($memberPath)) { throw 'CAB contains a duplicate MSI FileId.' }
        if (-not $expected.ContainsKey($memberPath)) { throw "CAB contains an unexpected MSI FileId: $memberPath" }
        if ($memberPath -cne $expected[$memberPath].fileId) { throw "CAB FileId casing differs from MSI File table: $memberPath" }
        foreach ($key in $member.Keys) {
            if (($key -match '(?i)link|reparse' -and $member[$key] -notin @('', '-')) -or
                ($key -in @('Mode', 'Attributes') -and $member[$key] -match '(^|\s)l[rwxstST-]{9}(\s|$)|(^|\s)L(\s|$)')) {
                throw 'CAB links and reparse members are forbidden.'
            }
        }
        if (($member.ContainsKey('Folder') -and $member.Folder -eq '+') -or
            ($member.ContainsKey('Attributes') -and $member.Attributes -match '^D')) {
            throw 'CAB members must be files, not directories.'
        }
        $size = ConvertTo-HandyPackageLong ([string]$member.Size) "CAB size for $memberPath"
        if ($size -ne [long]$expected[$memberPath].bytes) { throw "CAB member size differs from MSI File table: $memberPath" }
    }
    if ($seen.Count -ne $expected.Count) { throw 'CAB is missing one or more MSI FileIds.' }
}

function New-HandyPackageDirectory {
    param([Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Path) { throw "Package verification output path already exists; preserving it: $Path" }
    $null = New-Item -ItemType Directory -Path $Path -ErrorAction Stop
    Assert-HandyPackageNoLinks $Path
}

function Assert-HandyPackageIgnoredOutput {
    param([Parameter(Mandatory)][string]$ProjectRoot, [Parameter(Mandatory)][string]$OutputDirectory)
    $harnessRoot = [IO.Path]::GetFullPath((Join-Path $ProjectRoot '.harness'))
    $prefix = [IO.Path]::TrimEndingDirectorySeparator($harnessRoot) + [IO.Path]::DirectorySeparatorChar
    if (-not $OutputDirectory.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OutputDirectory must be a new child of this checkout .harness directory.'
    }
    $relative = [IO.Path]::GetRelativePath($ProjectRoot, $OutputDirectory).Replace([string][IO.Path]::DirectorySeparatorChar, '/')
    $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $git) { throw 'Git is required to confirm OutputDirectory is ignored by this checkout.' }
    $PSNativeCommandUseErrorActionPreference = $false
    $global:LASTEXITCODE = 0
    & $git.Source -C $ProjectRoot check-ignore --quiet --no-index -- $relative *> $null
    if ($LASTEXITCODE -ne 0) { throw 'OutputDirectory is not ignored by this checkout; no verification output was created.' }
}

function Test-HandyPackagePathWithin {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Root)
    $trimmedRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
    $fullPath = [IO.Path]::GetFullPath($Path)
    return $fullPath.Equals($trimmedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith($trimmedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Invoke-HandyWindowsPackageVerification {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Msi,
        [Parameter(Mandatory)][string]$PortableDirectory,
        [Parameter(Mandatory)][string]$OutputDirectory
    )
    if (-not $IsWindows) { throw 'Windows package verification requires native Windows and WindowsInstaller.Installer COM.' }
    $projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    $msiPath = Get-HandyPackageFullPath $Msi 'Msi'
    $portablePath = Get-HandyPackageFullPath $PortableDirectory 'PortableDirectory'
    $outputPath = Get-HandyPackageFullPath $OutputDirectory 'OutputDirectory'
    Assert-HandyPackageNoLinks $msiPath
    Assert-HandyPackageNoLinks $portablePath
    Assert-HandyPackageNoLinks $outputPath
    $msiItem = Get-Item -LiteralPath $msiPath -Force -ErrorAction Stop
    if ($msiItem.PSIsContainer) { throw 'Msi must name an existing file.' }
    $portableItem = Get-Item -LiteralPath $portablePath -Force -ErrorAction Stop
    if (-not $portableItem.PSIsContainer) { throw 'PortableDirectory must name an existing directory.' }
    if (Test-HandyPackagePathWithin $msiPath $portablePath) { throw 'The MSI file must not be inside PortableDirectory.' }
    if ((Test-HandyPackagePathWithin $outputPath $portablePath) -or (Test-HandyPackagePathWithin $portablePath $outputPath) -or
        (Test-HandyPackagePathWithin $outputPath $msiPath)) {
        throw 'OutputDirectory must not overlap either input path.'
    }
    Assert-HandyPackageIgnoredOutput $projectRoot $outputPath
    if (Test-Path -LiteralPath $outputPath) { throw "OutputDirectory must be fresh and absent; preserving it: $outputPath" }

    $portableFiles = @(Get-HandyPackageTreeFiles $portablePath)
    if ($portableFiles.Count -eq 0) { throw 'PortableDirectory contains no files.' }
    $portableByPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $portableFiles) { $portableByPath.Add([string]$file.path, $file) }
    $portableManifestHash = Get-HandyPackagePortableManifestHash $portableFiles
    $msiHash = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $metadata = Get-HandyPackageMsiMetadata $msiPath
    $package = Get-HandyPackageMsiFileMap $metadata

    if ($package.FilesByPath.Count -ne $portableByPath.Count) {
        throw "MSI and portable file counts differ (MSI=$($package.FilesByPath.Count), portable=$($portableByPath.Count))."
    }
    foreach ($entry in $package.FilesByPath.Values) {
        if (-not $portableByPath.ContainsKey($entry.path)) { throw "Portable package is missing MSI file: $($entry.path)" }
        $portableFile = $portableByPath[$entry.path]
        if ($portableFile.path -cne $entry.path) { throw "Portable file path casing differs from MSI: $($entry.path)" }
        if ($portableFile.bytes -ne $entry.bytes) { throw "Portable file size differs from MSI File table: $($entry.path)" }
    }
    foreach ($portableFile in $portableByPath.Values) {
        if (-not $package.FilesByPath.ContainsKey($portableFile.path)) { throw "Portable package has an extra file: $($portableFile.path)" }
    }

    $sevenZip = Get-HandyPackage7Zip
    Assert-HandyPackageNoLinks $sevenZip
    $outputParent = [IO.Path]::GetDirectoryName($outputPath)
    if (-not (Test-Path -LiteralPath $outputParent -PathType Container)) {
        $null = [IO.Directory]::CreateDirectory($outputParent)
    }
    Assert-HandyPackageNoLinks $outputParent
    New-HandyPackageDirectory $outputPath
    $cabinetRoot = Join-Path $outputPath 'cabinets'
    New-HandyPackageDirectory $cabinetRoot
    $verifiedFiles = [Collections.Generic.List[object]]::new()
    $totalBytes = 0L

    foreach ($cabinet in $package.Cabinets) {
        $safeCabinet = ConvertTo-HandyPackageRelativePath $cabinet.name
        if ($safeCabinet.Contains('/')) { throw 'MSI embedded cabinet names must be single file names.' }
        $cabinetDirectory = Join-Path $cabinetRoot ([IO.Path]::GetFileNameWithoutExtension($safeCabinet))
        New-HandyPackageDirectory $cabinetDirectory
        $streamDirectory = Join-Path $cabinetDirectory 'stream'
        $payloadDirectory = Join-Path $cabinetDirectory 'payload'
        New-HandyPackageDirectory $streamDirectory
        New-HandyPackageDirectory $payloadDirectory
        $null = Invoke-HandyPackage7Zip $sevenZip @('x', '-y', '-spd', '-mmt=2', "-o$streamDirectory", '--', $msiPath, $safeCabinet)
        $cabPath = Join-Path $streamDirectory $safeCabinet
        Assert-HandyPackageNoLinks $cabPath
        if (-not (Test-Path -LiteralPath $cabPath -PathType Leaf)) { throw "7-Zip did not extract embedded CAB stream: $safeCabinet" }
        $streamFiles = @(Get-HandyPackageTreeFiles $streamDirectory)
        if ($streamFiles.Count -ne 1 -or $streamFiles[0].path -cne $safeCabinet) { throw "7-Zip extracted unexpected files from MSI stream $safeCabinet." }

        $listing = Invoke-HandyPackage7Zip $sevenZip @('l', '-slt', '--', $cabPath)
        $members = @(Get-HandyPackage7ZipMembers $listing)
        Assert-HandyPackageCabinetMembers $members $cabinet.files
        $memberNames = [string[]]@($members | ForEach-Object { [string]$_.Path })
        $extractArgs = @('x', '-y', '-spd', '-mmt=2', "-o$payloadDirectory", '--', $cabPath) + $memberNames
        $null = Invoke-HandyPackage7Zip $sevenZip $extractArgs
        $extractedFiles = @(Get-HandyPackageTreeFiles $payloadDirectory)
        if ($extractedFiles.Count -ne $cabinet.files.Count) { throw "7-Zip extracted an unexpected number of files from $safeCabinet." }
        $extractedById = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $extractedFiles) {
            if ($file.path.Contains('/')) { throw "7-Zip extracted a non-FileId path from $safeCabinet." }
            $extractedById.Add($file.path, $file)
        }
        foreach ($expected in $cabinet.files) {
            if (-not $extractedById.ContainsKey($expected.fileId)) { throw "7-Zip did not extract MSI FileId $($expected.fileId)." }
            $extracted = $extractedById[$expected.fileId]
            if ($extracted.path -cne $expected.fileId) { throw "Extracted MSI FileId casing differs: $($expected.fileId)" }
            if ($extracted.bytes -ne $expected.bytes) { throw "Extracted MSI payload size differs from File table: $($expected.path)" }
            $portableFile = $portableByPath[$expected.path]
            if ($extracted.sha256 -cne $portableFile.sha256) { throw "MSI payload bytes differ from portable file: $($expected.path)" }
            $totalBytes += $expected.bytes
            $verifiedFiles.Add([ordered]@{
                path = $expected.path
                fileId = $expected.fileId
                bytes = $expected.bytes
                portableSha256 = $portableFile.sha256
                msiPayloadSha256 = $extracted.sha256
                verified = $true
            })
        }
    }

    if ($verifiedFiles.Count -ne $portableFiles.Count) { throw 'MSI embedded CAB payload did not verify every portable file exactly once.' }
    $report = [ordered]@{
        schemaVersion = 1
        result = 'passed'
        scope = 'Read-only comparison of the MSI File table and embedded CAB payload with the supplied portable directory.'
        sourceMsi = [ordered]@{ sha256 = $msiHash; bytes = [long]$msiItem.Length }
        portableManifest = [ordered]@{
            sha256 = $portableManifestHash
            format = 'SHA-256 of UTF-8 path<TAB>bytes<TAB>sha256 rows sorted by path ordinal-ignore-case, LF terminated.'
            fileCount = $portableFiles.Count
            totalBytes = [long](($portableFiles | Measure-Object -Property bytes -Sum).Sum)
        }
        exactFileVerification = [ordered]@{
            fileSetMatch = $true
            fileCount = $verifiedFiles.Count
            totalBytes = $totalBytes
            files = $verifiedFiles.ToArray()
        }
        installationPerformed = $false
        applicationLaunched = $false
        installedAcceptancePerformed = $false
    }
    $summaryPath = Join-Path $outputPath 'verification.json'
    Assert-HandyPackageNoLinks $summaryPath
    $json = ConvertTo-Json -InputObject $report -Depth 8
    $encoding = [Text.UTF8Encoding]::new($false)
    $bytes = $encoding.GetBytes($json + "`n")
    $stream = [IO.File]::Open($summaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    Write-Host "Exact MSI payload match: $($verifiedFiles.Count) files, $totalBytes bytes."
    Write-Host "Summary: $summaryPath"
    return [pscustomobject]$report
}

if (-not $script:HandyPackageVerifierDotSourced) {
    if (-not $Msi -or -not $PortableDirectory -or -not $OutputDirectory) {
        throw 'Usage: verify-windows-package.ps1 -Msi <absolute.msi> -PortableDirectory <absolute-directory> -OutputDirectory <fresh ignored checkout-local .harness directory>'
    }
    $null = Invoke-HandyWindowsPackageVerification -Msi $Msi -PortableDirectory $PortableDirectory -OutputDirectory $OutputDirectory
}
