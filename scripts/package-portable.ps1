# Package the existing framework-dependent release without rebuilding or deploying.
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
[xml]$taskProject = Get-Content -LiteralPath (Join-Path $taskRoot 'src/TranslatorAnywhere/TranslatorAnywhere.csproj') -Raw
$taskVersion = [string]$taskProject.Project.PropertyGroup.Version
if ($taskVersion -notmatch '^\d+\.\d+\.\d+(?:[-.][A-Za-z0-9.-]+)?$') { throw 'Invalid version.' }
$taskExe = Join-Path $taskRoot "dist/Anywhere-Translator-v$taskVersion-win-x64.exe"
$taskHashFile = $taskExe + '.sha256'
if (-not (Test-Path -LiteralPath $taskExe) -or -not (Test-Path -LiteralPath $taskHashFile)) {
    throw 'Build the release first with scripts/build.ps1 -SkipDeploy.'
}
$taskExpectedHash = ((Get-Content -LiteralPath $taskHashFile -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash -ine $taskExpectedHash) { throw 'Release EXE checksum mismatch.' }
if ((Get-Item -LiteralPath $taskExe).VersionInfo.ProductVersion.Split('+')[0] -ne $taskVersion) { throw 'Release EXE version mismatch.' }
$taskName = "Anywhere-Translator-v$taskVersion-win-x64-portable"
$taskZipPath = Join-Path $taskRoot "dist/$taskName.zip"
# Explicit allowlist: never package the installed folder or its private data.
$taskFiles = [ordered]@{
    'Anywhere Translator.exe' = $taskExe
    'README.txt' = (Join-Path $taskRoot 'docs/PORTABLE.txt')
    'LICENSE.txt' = (Join-Path $taskRoot 'LICENSE')
    'THIRD-PARTY-NOTICES.md' = (Join-Path $taskRoot 'THIRD-PARTY-NOTICES.md')
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskStream = [IO.File]::Open($taskZipPath, [IO.FileMode]::Create)
try {
    $taskZip = [IO.Compression.ZipArchive]::new($taskStream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($taskFile in $taskFiles.GetEnumerator()) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip, $taskFile.Value,
                "$taskName/$($taskFile.Key)", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $taskZip.Dispose() }
} finally { $taskStream.Dispose() }
$taskHash = (Get-FileHash -LiteralPath $taskZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($taskZipPath + '.sha256', "$taskHash  $taskName.zip" + [Environment]::NewLine)
Write-Output "Portable ZIP: $taskZipPath"
Write-Output ('Size: {0:N2} MiB' -f ((Get-Item -LiteralPath $taskZipPath).Length / 1MB))
