param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $projectRoot 'VERSION') -Raw).Trim()
$releaseDirectory = Join-Path $projectRoot "artifacts\release\$version"
$installerDirectory = Join-Path $projectRoot 'artifacts\installer'
$publishDirectory = Join-Path $projectRoot 'artifacts\publish\win-x64'
$notesSource = Join-Path $projectRoot "RELEASE_NOTES_$version.md"

if ($version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
    throw "VERSION must contain a numeric release version. Current value: $version"
}

& (Join-Path $PSScriptRoot 'Build-Installer.ps1') -Configuration $Configuration -OutputDirectory $installerDirectory

if (Test-Path -LiteralPath $releaseDirectory) {
    Remove-Item -LiteralPath $releaseDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null

$installerName = "A-Note-Setup-$version.exe"
$installer = Join-Path $installerDirectory $installerName
if (-not (Test-Path -LiteralPath $installer)) { throw "Installer is missing: $installer" }
Copy-Item -LiteralPath $installer -Destination (Join-Path $releaseDirectory $installerName)

$portableName = "A-Note-Portable-$version-win-x64.zip"
$portable = Join-Path $releaseDirectory $portableName
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $portable -CompressionLevel Optimal

if (-not (Test-Path -LiteralPath $notesSource)) { throw "Release notes are missing: $notesSource" }
Copy-Item -LiteralPath $notesSource -Destination (Join-Path $releaseDirectory 'RELEASE_NOTES.md')

$checksumFiles = @($installerName, $portableName)
$checksumLines = foreach ($name in $checksumFiles) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $releaseDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name"
}
[System.IO.File]::WriteAllLines((Join-Path $releaseDirectory 'SHA256SUMS.txt'), $checksumLines, [System.Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host 'GitHub release files ready:'
Get-ChildItem -LiteralPath $releaseDirectory -File | Sort-Object Name | ForEach-Object {
    Write-Host ("{0} ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
}
Write-Output $releaseDirectory
