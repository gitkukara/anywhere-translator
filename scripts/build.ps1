param(
    [switch]$FrameworkDependent,
    [switch]$SelfContained,
    [switch]$SkipDeploy
)
$ErrorActionPreference = 'Stop'
if ($FrameworkDependent -and $SelfContained) { throw '不能同时指定 FrameworkDependent 和 SelfContained。' }
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $taskRoot 'src\TranslatorAnywhere\TranslatorAnywhere.csproj'
[xml]$taskProjectXml = Get-Content -LiteralPath $taskProject -Raw
$taskVersion = [string]$taskProjectXml.Project.PropertyGroup.Version
if ($taskVersion -notmatch '^\d+\.\d+\.\d+(?:[-.][A-Za-z0-9.-]+)?$') { throw '项目版本号无效。' }
$taskSuffix = if ($FrameworkDependent) { '-framework-dependent' } else { '' }
$taskOutput = Join-Path $taskRoot ('dist\win-x64' + $taskSuffix)
$taskArchive = Join-Path $taskRoot "dist\Anywhere-Translator-v$taskVersion-win-x64$taskSuffix.zip"
# Always publish into an empty staging directory. User data and stale runtime files
# from a previous build must never enter a release archive.
$taskStage = Join-Path $taskRoot ('artifacts\publish\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskStage -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $taskRoot 'dist') -Force | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.dotnet'
$env:DOTNET_NOLOGO = 'true'
$taskContained = if ($FrameworkDependent) { 'false' } else { 'true' }
& dotnet publish $taskProject --configuration Release --runtime win-x64 --self-contained $taskContained --output $taskStage --nologo -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination (Join-Path $taskStage '使用说明.md') -Force
$taskConfig = Get-Content -LiteralPath (Join-Path $taskStage 'TranslatorAnywhere.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $FrameworkDependent) {
    foreach ($taskRequired in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll', 'PresentationNative_cor3.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $taskStage $taskRequired))) { throw "缺少内置运行时文件：$taskRequired" }
    }
    if (-not $taskConfig.runtimeOptions.includedFrameworks -or $taskConfig.runtimeOptions.frameworks -or $taskConfig.runtimeOptions.framework) { throw '运行时配置不是自包含发布。' }
}
# Include every published file and subdirectory, including satellite assemblies and
# runtime licences. The fresh staging directory contains no portable data directory.
$taskFiles = @(Get-ChildItem -LiteralPath $taskStage -Force | ForEach-Object { $_.FullName })
Compress-Archive -LiteralPath $taskFiles -DestinationPath $taskArchive -Force
$taskHash = (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($taskArchive + '.sha256', $taskHash + '  ' + [IO.Path]::GetFileName($taskArchive) + [Environment]::NewLine)
if (-not $SkipDeploy) {
    New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
    foreach ($taskFile in $taskFiles) { Copy-Item -LiteralPath $taskFile -Destination $taskOutput -Recurse -Force }
    Write-Output "程序已生成：$taskOutput\Anywhere Translator.exe"
}
Write-Output "暂存目录：$taskStage"
Write-Output "压缩包已生成：$taskArchive"
if ($FrameworkDependent) { Write-Output '轻量版需要已安装 .NET 10 Desktop Runtime。' }
else { Write-Output '完整版已内置 .NET 运行时，解压即可运行，无需另外安装 .NET。' }
