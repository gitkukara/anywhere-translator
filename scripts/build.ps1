param(
    # Compatibility alias: all builds now depend on the installed .NET runtime.
    [switch]$FrameworkDependent,
    [switch]$SkipDeploy
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$taskProject = Join-Path $taskRoot 'src\TranslatorAnywhere\TranslatorAnywhere.csproj'
[xml]$taskProjectXml = Get-Content -LiteralPath $taskProject -Raw
$taskVersion = [string]$taskProjectXml.Project.PropertyGroup.Version
if ($taskVersion -notmatch '^\d+\.\d+\.\d+(?:[-.][A-Za-z0-9.-]+)?$') { throw '项目版本号无效。' }
$taskOutput = Join-Path $taskRoot 'dist\win-x64'
$taskDownload = Join-Path $taskRoot "dist\Anywhere-Translator-v$taskVersion-win-x64.exe"
# Always publish into an empty staging directory. User data and stale runtime files
# from a previous build must never enter the distributed executable.
$taskStage = Join-Path $taskRoot ('artifacts\publish\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskStage -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $taskRoot 'dist') -Force | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.dotnet'
$env:DOTNET_NOLOGO = 'true'
& dotnet publish $taskProject --configuration Release --runtime win-x64 --self-contained false --output $taskStage --nologo -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishDocumentationFiles=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
$taskExe = Join-Path $taskStage 'Anywhere Translator.exe'
$taskFiles = @(Get-ChildItem -LiteralPath $taskStage -Force -Recurse)
if ($taskFiles.Count -ne 1 -or -not (Test-Path -LiteralPath $taskExe -PathType Leaf)) { throw '单文件发布包含额外文件。' }
Copy-Item -LiteralPath $taskExe -Destination $taskDownload -Force
$taskHash = (Get-FileHash -LiteralPath $taskDownload -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($taskDownload + '.sha256', $taskHash + '  ' + [IO.Path]::GetFileName($taskDownload) + [Environment]::NewLine)
if (-not $SkipDeploy) {
    $taskInstalledExe = Join-Path $taskOutput 'Anywhere Translator.exe'
    $taskRunning = @(Get-Process -Name 'Anywhere Translator', 'TranslatorAnywhere' -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $taskInstalledExe })
    if ($taskRunning.Count -gt 0) { throw '程序正在运行。请先从托盘退出，或使用 -SkipDeploy 只生成 EXE。' }
    New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
    # Back up the old multi-file program, preserving portable user data in place.
    $taskOldFiles = @(Get-ChildItem -LiteralPath $taskOutput -Force | Where-Object Name -ne 'data')
    if ($taskOldFiles.Count -gt 0) {
        $taskBackup = Join-Path $taskRoot ('artifacts\local-update-backups\' + [guid]::NewGuid().ToString('N') + '\program')
        foreach ($taskPath in @($taskOutput, $taskBackup)) {
            if (-not ([IO.Path]::GetFullPath($taskPath)).StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '发布目录超出工作区。' }
        }
        New-Item -ItemType Directory -Path $taskBackup -Force | Out-Null
        foreach ($taskItem in $taskOldFiles) { Move-Item -LiteralPath $taskItem.FullName -Destination $taskBackup }
        Write-Output "旧程序文件已备份：$taskBackup"
    }
    Copy-Item -LiteralPath $taskExe -Destination $taskInstalledExe -Force
    Write-Output "程序已生成：$taskOutput\Anywhere Translator.exe"
}
Write-Output "暂存目录：$taskStage"
Write-Output "单文件已生成：$taskDownload"
Write-Output '运行需要 .NET 10 Desktop Runtime（x64）和 Microsoft Edge WebView2 Runtime。无需解压或附带 DLL、字体、脚本。'
