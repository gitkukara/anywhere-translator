$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskExecutable = Join-Path $taskRoot 'dist\win-x64\TranslatorAnywhere.exe'
if (-not (Test-Path -LiteralPath $taskExecutable)) { & (Join-Path $PSScriptRoot 'build.ps1') }
Start-Process -FilePath $taskExecutable -WorkingDirectory (Split-Path -Parent $taskExecutable)
