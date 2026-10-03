param([Parameter(Mandatory=$true)][string]$Archive)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskExtract = Join-Path $taskRoot ('artifacts\portable-check\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskExtract -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskZip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Archive).Path)
try {
    foreach ($taskEntry in $taskZip.Entries) {
        $taskName = $taskEntry.FullName.Replace('\','/')
        if ($taskName -match '(^/|^[A-Za-z]:|(^|/)\.\.(/|$)|(^|/)(data|settings\.json|api-key)|\.(dpapi|log|pdb)$)') { throw "不应包含的发布文件：$taskName" }
    }
} finally { $taskZip.Dispose() }
Expand-Archive -LiteralPath $Archive -DestinationPath $taskExtract
$taskConfig = Get-Content (Join-Path $taskExtract 'TranslatorAnywhere.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($taskConfig.runtimeOptions.frameworks -or $taskConfig.runtimeOptions.framework -or -not $taskConfig.runtimeOptions.includedFrameworks) { throw '发布包依赖外部 .NET 运行时。' }
if (-not (Test-Path (Join-Path $taskExtract 'zh-Hans\PresentationFramework.resources.dll'))) { throw '发布包缺少 WPF 中文语言资源。' }
$taskEnvNames = @('DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_MULTILEVEL_LOOKUP','TRANSLATOR_ANYWHERE_DATA_DIR')
$taskSaved = @{}
foreach ($taskName in $taskEnvNames) { $taskSaved[$taskName] = [Environment]::GetEnvironmentVariable($taskName,'Process') }
$taskProcess = $null
try {
    $taskEmpty = Join-Path $taskExtract 'empty-runtime-root'
    New-Item -ItemType Directory -Path $taskEmpty | Out-Null
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT',$taskEmpty,'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64',$taskEmpty,'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_MULTILEVEL_LOOKUP','0','Process')
    [Environment]::SetEnvironmentVariable('TRANSLATOR_ANYWHERE_DATA_DIR',(Join-Path $taskExtract 'isolated-test-data'),'Process')
    $taskProcess = Start-Process -FilePath (Join-Path $taskExtract 'TranslatorAnywhere.exe') -ArgumentList '--fixture' -WorkingDirectory $taskExtract -WindowStyle Hidden -PassThru
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $taskProcess.Refresh()
        if ($taskProcess.HasExited) { throw '便携程序启动失败。' }
    } until ($taskProcess.MainWindowHandle -ne 0 -or [DateTime]::UtcNow -gt $taskDeadline)
    if ($taskProcess.MainWindowHandle -eq 0) { throw '测试窗口没有创建。' }
    foreach ($taskDll in @('coreclr.dll','PresentationNative_cor3.dll')) {
        $taskModule = $taskProcess.Modules | Where-Object ModuleName -ieq $taskDll | Select-Object -First 1
        if (-not $taskModule -or $taskModule.FileName -ine (Join-Path $taskExtract $taskDll)) { throw "没有从发布包加载 $taskDll" }
        Write-Output "PASS: $taskDll loaded from extracted package."
    }
    Write-Output ('PASS: standalone WPF window, bundled resources, clean ZIP. Frameworks: ' + (($taskConfig.runtimeOptions.includedFrameworks | ForEach-Object { $_.name + ' ' + $_.version }) -join ', '))
} finally {
    if ($taskProcess -and -not $taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id }
    foreach ($taskName in $taskEnvNames) { [Environment]::SetEnvironmentVariable($taskName,$taskSaved[$taskName],'Process') }
}
