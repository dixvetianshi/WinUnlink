param(
    [string]$Version,
    [ValidateSet('AnyCPU', 'x64', 'x86')]
    [string]$Platform = 'AnyCPU',
    [string]$OutputDirectory,
    [switch]$SkipBuild,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/releases'))
$versionSource = Get-Content -LiteralPath (Join-Path $projectRoot 'src/AssemblyInfo.cs') -Raw
$versionMatch = [regex]::Match($versionSource, 'AssemblyFileVersion\("(?<value>\d+\.\d+\.\d+\.\d+)"\)')
if (-not $versionMatch.Success) { throw 'AssemblyFileVersion was not found in src/AssemblyInfo.cs.' }
$assemblyVersion = [version]$versionMatch.Groups['value'].Value
if ($assemblyVersion.Revision -ne 0) { throw 'Release packaging expects a three-part version with assembly revision 0.' }
$sourceVersion = '{0}.{1}.{2}' -f $assemblyVersion.Major, $assemblyVersion.Minor, $assemblyVersion.Build
if ([string]::IsNullOrEmpty($Version)) { $Version = $sourceVersion }
if ($Version -cne $sourceVersion) { throw ('Release version must match AssemblyFileVersion: ' + $sourceVersion) }

if ([string]::IsNullOrEmpty($OutputDirectory)) {
    $OutputDirectory = Join-Path $releaseRoot ('v' + $Version)
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$releasePrefix = $releaseRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $OutputDirectory.StartsWith($releasePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release output must be a directory inside this project''s artifacts/releases directory.'
}

$architectureSuffix = if ($Platform -eq 'AnyCPU') { '' } else { '-' + $Platform.ToLowerInvariant() }
$archiveName = 'WinUnlink-v' + $Version + '-windows' + $architectureSuffix + '-portable.zip'
$archivePath = Join-Path $OutputDirectory $archiveName
$checksumPath = Join-Path $OutputDirectory 'SHA256SUMS.txt'
if (-not $Force -and ((Test-Path -LiteralPath $archivePath) -or (Test-Path -LiteralPath $checksumPath))) {
    throw 'Release output already exists. Use -Force to replace only the generated archive and its checksum file.'
}
if (-not $SkipBuild) { & (Join-Path $projectRoot 'build.ps1') -Platform $Platform }

$binaryDirectory = Join-Path $projectRoot 'artifacts/bin'
$executablePath = Join-Path $binaryDirectory 'WinUnlink.exe'
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) { throw 'Build WinUnlink.exe before packaging.' }
$binaryVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executablePath).FileVersion
if ($binaryVersion -cne $assemblyVersion.ToString()) { throw 'The executable version does not match src/AssemblyInfo.cs.' }
# AssemblyName.ProcessorArchitecture returns None on modern PowerShell/.NET.
# Read the PE machine and CLR flags instead of treating that omission as x86.
$binaryBytes = [System.IO.File]::ReadAllBytes($executablePath)
function Read-PeUInt16([long]$Offset) {
    if ($Offset -lt 0 -or $Offset + 2 -gt $binaryBytes.LongLength) { throw 'Executable contains a truncated PE field.' }
    return [System.BitConverter]::ToUInt16($binaryBytes, [int]$Offset)
}
function Read-PeUInt32([long]$Offset) {
    if ($Offset -lt 0 -or $Offset + 4 -gt $binaryBytes.LongLength) { throw 'Executable contains a truncated PE field.' }
    return [System.BitConverter]::ToUInt32($binaryBytes, [int]$Offset)
}
if ((Read-PeUInt16 0) -ne 0x5a4d) { throw 'Executable is not an MZ/PE image.' }
$peOffset = [long](Read-PeUInt32 0x3c)
if ((Read-PeUInt32 $peOffset) -ne 0x00004550) { throw 'Executable has an invalid PE signature.' }
$machine = Read-PeUInt16 ($peOffset + 4)
$sectionCount = Read-PeUInt16 ($peOffset + 6)
$optionalSize = Read-PeUInt16 ($peOffset + 20)
$optionalOffset = $peOffset + 24
$magic = Read-PeUInt16 $optionalOffset
$directoryOffset = if ($magic -eq 0x10b) { 96 } elseif ($magic -eq 0x20b) { 112 } else { throw 'Executable has an unsupported PE optional header.' }
if ($optionalSize -lt $directoryOffset + 15 * 8 -or (Read-PeUInt32 ($optionalOffset + $directoryOffset - 4)) -lt 15) {
    throw 'Executable does not declare the CLR header directory.'
}
$clrRva = [long](Read-PeUInt32 ($optionalOffset + $directoryOffset + 14 * 8))
$clrSize = Read-PeUInt32 ($optionalOffset + $directoryOffset + 14 * 8 + 4)
if ($clrRva -eq 0 -or $clrSize -lt 20) { throw 'Executable has no complete CLR header.' }
$clrOffset = -1L
$headersSize = Read-PeUInt32 ($optionalOffset + 60)
if ($clrRva + 20 -le $headersSize) { $clrOffset = $clrRva }
$sectionOffset = $optionalOffset + $optionalSize
for ($index = 0; $index -lt $sectionCount; $index++) {
    $row = $sectionOffset + $index * 40
    $sectionRva = [long](Read-PeUInt32 ($row + 12))
    $rawSize = [long](Read-PeUInt32 ($row + 16))
    $rawOffset = [long](Read-PeUInt32 ($row + 20))
    $delta = $clrRva - $sectionRva
    if ($delta -ge 0 -and $delta + 20 -le $rawSize) {
        if ($clrOffset -ne -1) { throw 'CLR header has ambiguous PE section coverage.' }
        $clrOffset = $rawOffset + $delta
    }
}
if ($clrOffset -lt 0 -or (Read-PeUInt32 $clrOffset) -lt 20) { throw 'CLR header is not backed by complete executable bytes.' }
$clrFlags = Read-PeUInt32 ($clrOffset + 16)
if (($clrFlags -band 1) -eq 0) { throw 'Release packaging expects an IL-only .NET executable.' }
$binaryArchitecture = if ($machine -eq 0x8664 -and $magic -eq 0x20b -and ($clrFlags -band 2) -eq 0) { 'x64' }
    elseif ($machine -eq 0x14c -and $magic -eq 0x10b) { if (($clrFlags -band 2) -ne 0) { 'x86' } else { 'AnyCPU' } }
    else { throw 'Executable has an unsupported or inconsistent PE/CLR architecture.' }
if ($binaryArchitecture -ne $Platform) { throw 'The executable architecture does not match -Platform. Rebuild for that platform before packaging.' }

# Only these product files are shipped. The build directory also contains local
# backups and debug outputs, so it must never be archived as a whole.
$payload = [ordered]@{
    'WinUnlink.exe' = $executablePath
    'WinUnlink.exe.config' = Join-Path $binaryDirectory 'WinUnlink.exe.config'
    'README.md' = Join-Path $projectRoot 'README.md'
    'docs/implementation-notes.md' = Join-Path $projectRoot 'docs/implementation-notes.md'
    'docs/verification.md' = Join-Path $projectRoot 'docs/verification.md'
}
if (Test-Path -LiteralPath (Join-Path $projectRoot 'LICENSE') -PathType Leaf) {
    $payload['LICENSE'] = Join-Path $projectRoot 'LICENSE'
}
foreach ($sourcePath in $payload.Values) {
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw ('Missing release input: ' + $sourcePath) }
}

$stageId = [Guid]::NewGuid().ToString('N')
$stageRoot = [System.IO.Path]::GetFullPath((Join-Path $releaseRoot ('staging/' + $stageId)))
$expectedStageParent = [System.IO.Path]::GetFullPath((Join-Path $releaseRoot 'staging'))
if ([System.IO.Path]::GetDirectoryName($stageRoot) -ne $expectedStageParent) { throw 'Release staging path is outside the expected directory.' }
[System.IO.Directory]::CreateDirectory($stageRoot) | Out-Null
$createdFiles = New-Object 'System.Collections.Generic.List[string]'
$createdDirectories = New-Object 'System.Collections.Generic.List[string]'
$utf8 = New-Object System.Text.UTF8Encoding($false)

try {
    foreach ($entryName in $payload.Keys) {
        $destination = Join-Path $stageRoot ($entryName.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $destinationParent = [System.IO.Path]::GetDirectoryName($destination)
        if (-not [System.IO.Directory]::Exists($destinationParent)) {
            [System.IO.Directory]::CreateDirectory($destinationParent) | Out-Null
            $createdDirectories.Add($destinationParent)
        }
        $createdFiles.Add($destination)
        [System.IO.File]::Copy($payload[$entryName], $destination, $false)
    }

    $removeScript = @'
@echo off
setlocal
if not exist "%~dp0WinUnlink.exe" (
    echo WinUnlink.exe is missing. Extract the complete release archive first.
    pause
    exit /b 1
)
set "restore_report=%~dp0menu-removal-%RANDOM%-%RANDOM%.json"
start "" /wait "%~dp0WinUnlink.exe" --uninstall-menu --yes --report "%restore_report%"
if errorlevel 1 (
    echo Menu removal did not complete. See the report:
    echo %restore_report%
    pause
    exit /b 1
)
echo WinUnlink menu removed. Other menu entries are preserved.
echo %restore_report%
timeout /t 2 /nobreak >nul
exit /b 0
'@
    $removePath = Join-Path $stageRoot 'remove-context-menu.cmd'
    $createdFiles.Add($removePath)
    [System.IO.File]::WriteAllText($removePath, ($removeScript -replace '\r?\n', "`r`n") + "`r`n", [System.Text.Encoding]::ASCII)

    $entryNames = @($payload.Keys) + 'remove-context-menu.cmd'
    $entryNames = [string[]]$entryNames
    [System.Array]::Sort($entryNames, [System.StringComparer]::Ordinal)
    $manifestLines = foreach ($entryName in $entryNames) {
        $inputPath = Join-Path $stageRoot ($entryName.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $hash = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $hash + '  ' + $entryName
    }
    $manifestPath = Join-Path $stageRoot 'SHA256SUMS.txt'
    $createdFiles.Add($manifestPath)
    [System.IO.File]::WriteAllText($manifestPath, ($manifestLines -join "`n") + "`n", $utf8)
    $entryNames = [string[]](@($entryNames) + 'SHA256SUMS.txt')
    [System.Array]::Sort($entryNames, [System.StringComparer]::Ordinal)

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stagedArchivePath = Join-Path $stageRoot $archiveName
    $createdFiles.Add($stagedArchivePath)
    $zipStream = [System.IO.File]::Open($stagedArchivePath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $archive = $null
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create, $true, $utf8)
        # Fixed order and timestamps make the archive reproducible for identical
        # payload bytes. This does not claim byte-identical compiler outputs.
        $entryTimestamp = New-Object System.DateTimeOffset(1980, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
        foreach ($entryName in $entryNames) {
            $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $entryTimestamp
            $inputPath = Join-Path $stageRoot ($entryName.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
            $inputStream = [System.IO.File]::OpenRead($inputPath)
            $entryStream = $null
            try {
                $entryStream = $entry.Open()
                $inputStream.CopyTo($entryStream)
            } finally {
                if ($null -ne $entryStream) { $entryStream.Dispose() }
                $inputStream.Dispose()
            }
        }
    } finally {
        if ($null -ne $archive) { $archive.Dispose() }
        $zipStream.Dispose()
    }

    $archiveHash = (Get-FileHash -LiteralPath $stagedArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $stagedChecksum = Join-Path $stageRoot 'archive-SHA256SUMS.txt'
    $createdFiles.Add($stagedChecksum)
    [System.IO.File]::WriteAllText($stagedChecksum, $archiveHash + '  ' + $archiveName + "`n", $utf8)
    [System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
    [System.IO.File]::Copy($stagedArchivePath, $archivePath, $Force.IsPresent)
    [System.IO.File]::Copy($stagedChecksum, $checksumPath, $Force.IsPresent)
    Write-Output ('Release archive: ' + $archivePath)
    Write-Output ('Archive SHA-256: ' + $archiveHash)
    Write-Output ('Checksum file: ' + $checksumPath)
} finally {
    # The only deletions are explicit files and directories created by this run.
    $verifiedStageRoot = [System.IO.Path]::GetFullPath((Join-Path $expectedStageParent $stageId))
    if ($stageRoot -ne $verifiedStageRoot) { throw 'Refused to clean an unverified release staging directory.' }
    $stagePrefix = $stageRoot + [System.IO.Path]::DirectorySeparatorChar
    foreach ($createdPath in $createdFiles) {
        $resolved = [System.IO.Path]::GetFullPath($createdPath)
        if (-not $resolved.StartsWith($stagePrefix, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Refused to clean a file outside this release staging directory.' }
        [System.IO.File]::Delete($resolved)
    }
    foreach ($directory in @($createdDirectories | Sort-Object -Descending)) {
        if ([System.IO.Directory]::Exists($directory)) { [System.IO.Directory]::Delete($directory, $false) }
    }
    if ([System.IO.Directory]::Exists($stageRoot)) { [System.IO.Directory]::Delete($stageRoot, $false) }
}
