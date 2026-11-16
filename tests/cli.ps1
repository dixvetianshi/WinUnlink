param([Parameter(Mandatory=$true)][string]$Executable, [switch]$AllowRegistryTests)
$ErrorActionPreference = 'Stop'
$Executable = [System.IO.Path]::GetFullPath($Executable)
$testId = [Guid]::NewGuid().ToString('N')
$caseDirectory = Join-Path ([System.IO.Path]::GetDirectoryName($Executable)) 'cases'
$testRoot = Join-Path $caseDirectory ('cli-' + $testId)
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$fixturePath = Join-Path $testRoot '命令行 测试.txt'
$existingReport = Join-Path $testRoot 'existing.json'
$scanReport = Join-Path $testRoot 'scan.json'
$folderFixture = Join-Path $testRoot '文件夹 扫描'
$folderNested = Join-Path $folderFixture 'nested'
$folderEmpty = Join-Path $folderFixture 'empty'
$folderOne = Join-Path $folderFixture 'one.txt'
$folderTwo = Join-Path $folderNested 'two.txt'
$folderReport = Join-Path $testRoot 'folder-scan.json'
[System.IO.Directory]::CreateDirectory($folderNested) | Out-Null
[System.IO.Directory]::CreateDirectory($folderEmpty) | Out-Null
[System.IO.File]::WriteAllText($folderOne, 'folder-one')
[System.IO.File]::WriteAllText($folderTwo, 'folder-two')
[System.IO.File]::WriteAllText($fixturePath, 'original fixture', (New-Object System.Text.UTF8Encoding($false)))
[System.IO.File]::WriteAllText($existingReport, 'keep existing report')

function Invoke-TestCli([string[]]$Arguments) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = (($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' ')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $start.StandardErrorEncoding = New-Object System.Text.UTF8Encoding($false)
    $child = New-Object System.Diagnostics.Process
    $child.StartInfo = $start
    try {
        if (-not $child.Start()) { throw 'Could not start the owned CLI test process.' }
        $pinnedHandle = $child.Handle
        $outputTask = $child.StandardOutput.ReadToEndAsync()
        $errorTask = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(10000)) {
            $child.Kill()
            $child.WaitForExit(5000) | Out-Null
            throw 'Owned CLI test process timed out.'
        }
        return [pscustomobject]@{ Code=$child.ExitCode; Output=$outputTask.Result; Error=$errorTask.Result }
    } finally { $child.Dispose() }
}

function Get-MenuFingerprint {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes\*\shell\WinUnlink.FileTools')
    if ($null -eq $key) { return 'absent' }
    try {
        $values = [ordered]@{}
        foreach ($name in @($key.GetValueNames() | Sort-Object)) {
            $values[$name] = @($key.GetValueKind($name).ToString(), $key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames))
        }
        $values['childNames'] = @($key.GetSubKeyNames() | Sort-Object)
        $command = $key.OpenSubKey('command')
        if ($null -ne $command) {
            try { $values['command'] = $command.GetValue('') } finally { $command.Dispose() }
        }
        return ($values | ConvertTo-Json -Depth 8 -Compress)
    } finally { $key.Dispose() }
}

function Assert-Test([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL ' + $Name) }
    Write-Output ('PASS ' + $Name)
}

try {
    $before = $null
    if ($AllowRegistryTests) { $before = Get-MenuFingerprint }
    $result = Invoke-TestCli @('--scan',$fixturePath,'--report')
    Assert-Test ($result.Code -eq 1 -and $result.Error.Contains('缺少路径')) 'CLI missing scan report rejected; UTF-8 error preserved'
    $result = Invoke-TestCli @('--scan',$fixturePath,'--report',$existingReport)
    Assert-Test ($result.Code -eq 1 -and $result.Error.Contains('报告失败') -and [System.IO.File]::ReadAllText($existingReport) -eq 'keep existing report') 'CLI existing scan report preserved'
    $result = Invoke-TestCli @('--scan',$fixturePath,'--report',$scanReport)
    $scan = [System.IO.File]::ReadAllText($scanReport) | ConvertFrom-Json
    Assert-Test ($result.Code -eq 0 -and $scan.Success -and $scan.Target -eq $fixturePath -and $scan.Identity -and [System.IO.File]::ReadAllText($fixturePath) -eq 'original fixture') 'CLI readonly scan writes a valid JSON report'
    $result = Invoke-TestCli @('--scan',$fixturePath)
    $scan = $result.Output | ConvertFrom-Json
    Assert-Test ($result.Code -eq 0 -and $scan.Success -and $scan.Message.Contains('查询完成')) 'CLI JSON stdout preserves Chinese UTF-8 without an attached console'
    $result = Invoke-TestCli @('--scan',$folderFixture,'--report',$folderReport)
    $folderScan = [System.IO.File]::ReadAllText($folderReport) | ConvertFrom-Json
    Assert-Test ($result.Code -eq 0 -and $folderScan.Success -and $folderScan.IsDirectory -and
        $folderScan.FileCount -eq 2 -and $folderScan.DirectoryCount -eq 3 -and $folderScan.Target -eq $folderFixture -and
        [System.IO.File]::ReadAllText($folderOne) -eq 'folder-one' -and [System.IO.File]::ReadAllText($folderTwo) -eq 'folder-two') 'CLI folder scan preserves the tree and reports recursive counts'
    Write-Output 'RESULT CLI tests passed=5 failed=0'
    if ($AllowRegistryTests) {
        $result = Invoke-TestCli @('--install-menu','--yes','--report')
        Assert-Test ($result.Code -eq 1 -and $result.Error.Contains('缺少路径') -and (Get-MenuFingerprint) -eq $before) 'Explicit registry CLI regression: missing report rejects menu write'
        $result = Invoke-TestCli @('--install-menu','--yes','--report',$existingReport)
        Assert-Test ($result.Code -eq 1 -and [System.IO.File]::ReadAllText($existingReport) -eq 'keep existing report' -and (Get-MenuFingerprint) -eq $before) 'Explicit registry CLI regression: existing report rejects menu write'
    }
} finally {
    $verifiedRoot = [System.IO.Path]::GetFullPath((Join-Path (Join-Path ([System.IO.Path]::GetDirectoryName($Executable)) 'cases') ('cli-' + $testId)))
    if ([System.IO.Path]::GetFullPath($testRoot) -eq $verifiedRoot) {
        foreach ($ownedFile in @($fixturePath,$existingReport,$scanReport,$folderReport)) {
            if ([System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($ownedFile)) -ne $verifiedRoot) { throw 'Owned test cleanup path mismatch.' }
            [System.IO.File]::Delete($ownedFile)
        }
        if ([System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($folderFixture)) -ne $verifiedRoot) { throw 'Folder fixture cleanup path mismatch.' }
        [System.IO.File]::Delete($folderOne)
        [System.IO.File]::Delete($folderTwo)
        [System.IO.Directory]::Delete($folderNested, $false)
        [System.IO.Directory]::Delete($folderEmpty, $false)
        [System.IO.Directory]::Delete($folderFixture, $false)
        [System.IO.Directory]::Delete($verifiedRoot, $false)
    }
}
