param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $taskRoot 'src\TranslatorAnywhere\TranslatorAnywhere.csproj'
$taskOutput = Join-Path $taskRoot 'dist\win-x64'
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.dotnet'
$env:DOTNET_NOLOGO = 'true'
$taskContained = if ($SelfContained) { 'true' } else { 'false' }
& dotnet publish $taskProject --configuration Release --runtime win-x64 --self-contained $taskContained --output $taskOutput --nologo
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination (Join-Path $taskOutput '使用说明.md') -Force
[xml]$taskProjectXml = Get-Content -LiteralPath $taskProject -Raw
$taskVersion = [string]$taskProjectXml.Project.PropertyGroup.Version
if ($taskVersion -notmatch '^\d+\.\d+\.\d+(?:[-.][A-Za-z0-9.-]+)?$') { throw '项目版本号无效。' }
$taskArchive = Join-Path $taskRoot "dist\TranslatorAnywhere-v$taskVersion-win-x64.zip"
# Package published files only. Never include the user's portable data directory.
$taskFiles = Get-ChildItem -LiteralPath $taskOutput -File | Where-Object { $_.Name -eq 'LICENSE' -or ($_.Name -ne 'app.log' -and $_.Extension -in '.exe','.dll','.json','.md') } | ForEach-Object { $_.FullName }
Compress-Archive -LiteralPath $taskFiles -DestinationPath $taskArchive -Force
Write-Output "程序已生成：$taskOutput\TranslatorAnywhere.exe"
Write-Output "压缩包已生成：$taskArchive"
if (-not $SelfContained) { Write-Output '此版本需要 .NET 10 Desktop Runtime。使用 -SelfContained 可以生成包含运行时的版本。' }
