param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration }
$exe = Join-Path $projectRoot "src\A-Note\bin\x64\$Configuration\net8.0-windows10.0.19041.0\A-Note.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "A-Note executable was not found: $exe" }
Start-Process -FilePath $exe
