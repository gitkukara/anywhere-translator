param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskCheck = Join-Path $taskRoot ('artifacts\single-file-startup\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskCheck -Force | Out-Null
$taskExe = Join-Path $taskCheck 'Anywhere Translator.exe'
Copy-Item -LiteralPath (Resolve-Path -LiteralPath $Executable).Path -Destination $taskExe
$taskSavedData = [Environment]::GetEnvironmentVariable('TRANSLATOR_ANYWHERE_DATA_DIR', 'Process')
$taskProcess = $null
try {
    [Environment]::SetEnvironmentVariable('TRANSLATOR_ANYWHERE_DATA_DIR', (Join-Path $taskCheck 'isolated-data'), 'Process')
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList '--fixture' -WorkingDirectory $taskCheck -WindowStyle Hidden -PassThru
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $taskProcess.Refresh()
        if ($taskProcess.HasExited) { throw '单文件程序启动失败；请确认已安装 .NET 10 Desktop Runtime（x64）。' }
    } until ($taskProcess.MainWindowHandle -ne 0 -or [DateTime]::UtcNow -gt $taskDeadline)
    if ($taskProcess.MainWindowHandle -eq 0) { throw '测试窗口没有创建。' }
    foreach ($taskDll in @('coreclr.dll', 'PresentationNative_cor3.dll')) {
        $taskModule = $taskProcess.Modules | Where-Object ModuleName -ieq $taskDll | Select-Object -First 1
        if (-not $taskModule -or $taskModule.FileName.StartsWith($taskCheck, [StringComparison]::OrdinalIgnoreCase) -or $taskModule.FileName -notmatch '\\shared\\Microsoft\.(NETCore|WindowsDesktop)\.App\\10\.') { throw "未从系统 .NET 10 运行时加载 $taskDll" }
        Write-Output "PASS: $taskDll loaded from system .NET 10."
    }
    $taskLoose = @(Get-ChildItem -LiteralPath $taskCheck -Force | Where-Object { $_.Name -ne 'isolated-data' })
    if ($taskLoose.Count -ne 1 -or $taskLoose[0].FullName -ine $taskExe) { throw '程序启动依赖额外的发布文件。' }
    Write-Output 'PASS: WPF window starts with only the downloaded EXE.'
} finally {
    if ($taskProcess -and -not $taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id }
    [Environment]::SetEnvironmentVariable('TRANSLATOR_ANYWHERE_DATA_DIR', $taskSavedData, 'Process')
}
