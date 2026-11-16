param(
    [ValidateSet('AnyCPU', 'x64', 'x86')][string]$Platform = 'AnyCPU',
    [switch]$CompileOnly,
    [switch]$AllowRegistryTests
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$frameworkFolder = 'Framework64'
if ($Platform -eq 'x86') { $frameworkFolder = 'Framework' }
$frameworkDirectory = Join-Path $env:WINDIR ('Microsoft.NET/' + $frameworkFolder + '/v4.0.30319')
if (-not (Test-Path -LiteralPath $frameworkDirectory)) {
    $frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319'
}
$compilerPath = Join-Path $frameworkDirectory 'csc.exe'
$outputDirectory = Join-Path $projectRoot ('artifacts/tests/' + $Platform.ToLowerInvariant())
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$sourcePaths = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$commonArguments = @('/nologo', '/target:exe', '/optimize+', '/codepage:65001', '/utf8output',
    ('/platform:' + $Platform.ToLowerInvariant()),
    ('/win32manifest:' + (Join-Path $projectRoot 'src/app.manifest')),
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Xml.Linq.dll', '/r:System.Runtime.Serialization.dll')
$testNames = @('IntegrationTests', 'DirectoryIntegrationTests')
if ($AllowRegistryTests -or $CompileOnly) { $testNames += 'MenuRegistrationTests' }
if (-not $AllowRegistryTests -and -not $CompileOnly) {
    Write-Output 'SKIP registry tests: require explicit -AllowRegistryTests in an isolated Windows sandbox or VM.'
}
foreach ($testName in $testNames) {
    $outputPath = Join-Path $outputDirectory ($testName + '.exe')
    $compilerArguments = $commonArguments + @(('/main:WinUnlinkTests.' + $testName), ('/out:' + $outputPath),
        (Join-Path $projectRoot ('tests/' + $testName + '.cs'))) + $sourcePaths
    & $compilerPath @compilerArguments
    if ($LASTEXITCODE -ne 0) { throw ($testName + ' 编译失败，退出码 ' + $LASTEXITCODE) }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src/App.config') -Destination ($outputPath + '.config') -Force
    if (-not $CompileOnly) {
        if ($testName -eq 'MenuRegistrationTests') { & $outputPath '--allow-real-user-registry' }
        else { & $outputPath }
        if ($LASTEXITCODE -ne 0) { throw ($testName + ' 失败，退出码 ' + $LASTEXITCODE) }
    }
}
$cliOutputPath = Join-Path $outputDirectory 'WinUnlinkCli.exe'
$cliCompilerArguments = @('/nologo', '/target:winexe', '/main:WinUnlink.Program', '/optimize+', '/codepage:65001', '/utf8output',
    ('/platform:' + $Platform.ToLowerInvariant()), ('/out:' + $cliOutputPath),
    ('/win32manifest:' + (Join-Path $projectRoot 'src/app.manifest')),
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Xml.Linq.dll', '/r:System.Runtime.Serialization.dll') + $sourcePaths
& $compilerPath @cliCompilerArguments
if ($LASTEXITCODE -ne 0) { throw ('CLI test target compilation failed: ' + $LASTEXITCODE) }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src/App.config') -Destination ($cliOutputPath + '.config') -Force
if (-not $CompileOnly) {
    & (Join-Path $projectRoot 'tests/cli.ps1') -Executable $cliOutputPath -AllowRegistryTests:$AllowRegistryTests
} else { Write-Output 'Compile-only completed. No test programs or system integration were executed.' }
