param(
    [ValidateSet('AnyCPU', 'x64', 'x86')]
    [string]$Platform = 'AnyCPU',
    [switch]$IncludeDebugSymbols
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
if (-not (Test-Path -LiteralPath $compilerPath)) {
    throw '找不到 Windows 自带的 .NET Framework C# 编译器。请启用或安装 .NET Framework 4.8 后重试。'
}
$outputDirectory = Join-Path $projectRoot 'artifacts/bin'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$outputPath = Join-Path $outputDirectory 'WinUnlink.exe'
$sourcePaths = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$debugOption = '/debug-'
if ($IncludeDebugSymbols) { $debugOption = '/debug:pdbonly' }
$compilerArguments = @(
    '/nologo', '/target:winexe', '/optimize+', $debugOption, '/codepage:65001', '/utf8output',
    ('/platform:' + $Platform.ToLowerInvariant()), ('/out:' + $outputPath),
    ('/win32manifest:' + (Join-Path $projectRoot 'src/app.manifest')),
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Xml.Linq.dll',
    '/r:System.Runtime.Serialization.dll'
) + $sourcePaths
& $compilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) { throw ('编译失败，退出码 ' + $LASTEXITCODE) }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src/App.config') -Destination ($outputPath + '.config') -Force
Write-Output ('已构建：' + $outputPath)
