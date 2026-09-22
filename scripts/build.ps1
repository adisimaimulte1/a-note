param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $projectRoot 'A-Note.sln'
$project = Join-Path $projectRoot 'src\A-Note\A-Note.csproj'
$iconGenerator = Join-Path $PSScriptRoot 'Generate-AppIcon.ps1'

& $iconGenerator

dotnet build $solution -c $Configuration -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'A-Note build failed.' }

$output = Join-Path $projectRoot "src\A-Note\bin\x64\$Configuration\net8.0-windows10.0.19041.0"
$sdkBinRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
$makePri = Get-ChildItem -LiteralPath $sdkBinRoot -Filter makepri.exe -Recurse |
    Where-Object { $_.FullName -match '\\x64\\makepri\.exe$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if (-not $makePri) { throw 'Windows SDK makepri.exe was not found. Install Windows 10/11 SDK build tools.' }

$stage = Join-Path $projectRoot "src\A-Note\obj\x64\$Configuration\pri-stage"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $output 'App.xbf') -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $output 'MainWindow.xbf') -Destination $stage -Force

$config = Join-Path $stage 'priconfig.xml'
$pri = Join-Path $stage 'resources.pri'
& $makePri.FullName createconfig /cf $config /dq en-US /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'PRI configuration generation failed.' }
& $makePri.FullName new /pr $stage /cf $config /of $pri /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'PRI generation failed.' }
Copy-Item -LiteralPath $pri -Destination (Join-Path $output 'resources.pri') -Force

Write-Host "A-Note $Configuration build ready: $output\A-Note.exe"
