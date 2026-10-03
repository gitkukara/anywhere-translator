param([string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
if (-not $ExecutablePath) {
    $ExecutablePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\win-x64\Anywhere Translator.exe'
}
$ExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath).Path
$taskPrograms = [Environment]::GetFolderPath('Programs')
$taskShortcutPath = Join-Path $taskPrograms 'Anywhere Translator.lnk'
$taskShell = New-Object -ComObject WScript.Shell
try {
    $taskShortcut = $taskShell.CreateShortcut($taskShortcutPath)
    $taskShortcut.TargetPath = $ExecutablePath
    $taskShortcut.Arguments = ''
    $taskShortcut.WorkingDirectory = Split-Path -Parent $ExecutablePath
    $taskShortcut.IconLocation = $ExecutablePath + ',0'
    $taskShortcut.Description = 'Anywhere Translator'
    $taskShortcut.Save()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskShortcut)
    Write-Output $taskShortcutPath
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskShell)
}