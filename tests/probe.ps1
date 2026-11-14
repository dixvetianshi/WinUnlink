param(
    [ValidateSet('AnyCPU', 'x86')]
    [string]$Platform = 'AnyCPU',
    [switch]$CompileOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$outputDirectory = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\probe'))
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
# The executable places each fixture run in its own GUID child here. Build
# outputs, reports and cases remain inside this project's artifacts directory.
$caseDirectory = Join-Path $outputDirectory 'cases'
[System.IO.Directory]::CreateDirectory($caseDirectory) | Out-Null
$frameworkDirectory = if ($Platform -eq 'x86') { 'Microsoft.NET\Framework\v4.0.30319' } else { 'Microsoft.NET\Framework64\v4.0.30319' }
$compiler = Join-Path ([Environment]::GetFolderPath('Windows')) ($frameworkDirectory + '\csc.exe')
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The installed .NET Framework C# compiler was not found.' }
$sourceFile = Join-Path $PSScriptRoot 'PosixProbe.cs'
$executable = Join-Path $outputDirectory ('PosixProbe-' + $Platform + '.exe')
$logFile = Join-Path $outputDirectory ('results-' + $Platform + '.txt')
& $compiler '/noconfig' '/nologo' '/target:exe' ('/platform:' + $Platform.ToLowerInvariant()) '/reference:System.dll' '/reference:System.Core.dll' ('/out:' + $executable) $sourceFile
$compileExit = $LASTEXITCODE
if ($compileExit -ne 0) { exit $compileExit }
if ($CompileOnly) { Write-Output ('Built probe without execution: ' + $executable); return }
& $executable | Tee-Object -FilePath $logFile
$probeExit = $LASTEXITCODE
exit $probeExit
