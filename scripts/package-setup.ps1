param([string]$CompilerPath)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
[xml]$taskProject = Get-Content -LiteralPath (Join-Path $taskRoot 'src/TranslatorAnywhere/TranslatorAnywhere.csproj') -Raw
$taskVersion = [string]$taskProject.Project.PropertyGroup.Version
if ($taskVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
$taskExe = Join-Path $taskRoot "dist/Anywhere-Translator-v$taskVersion-win-x64.exe"
$taskExpected = ((Get-Content -LiteralPath ($taskExe + '.sha256') -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash -LiteralPath $taskExe).Hash -ine $taskExpected) { throw 'Release checksum mismatch.' }
if ((Get-Item -LiteralPath $taskExe).VersionInfo.ProductVersion.Split('+')[0] -ne $taskVersion) { throw 'Release version mismatch.' }
if (-not $CompilerPath) {
    $taskCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($taskCommand) { $CompilerPath = $taskCommand.Source }
    else { $CompilerPath = Join-Path $taskRoot 'artifacts/tools/inno/ISCC.exe' }
}
if (-not (Test-Path -LiteralPath $CompilerPath)) { throw 'Install Inno Setup 6 and pass -CompilerPath pointing to ISCC.exe.' }
& $CompilerPath "/DAppVersion=$taskVersion" (Join-Path $PSScriptRoot 'installer/AnywhereTranslator.iss')
if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }
$taskSetup = Join-Path $taskRoot "dist/Anywhere-Translator-v$taskVersion-win-x64-Setup.exe"
$taskHash = (Get-FileHash -LiteralPath $taskSetup).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($taskSetup + '.sha256', $taskHash + '  ' + [IO.Path]::GetFileName($taskSetup) + [Environment]::NewLine)
Write-Output ('Setup: {0} ({1:N2} MiB)' -f $taskSetup, ((Get-Item -LiteralPath $taskSetup).Length / 1MB))
