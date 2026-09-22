param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $projectRoot 'VERSION'
$iss = Join-Path $projectRoot 'installer\A-Note.iss'
$publishScript = Join-Path $PSScriptRoot 'Publish-Release.ps1'

function Find-InnoSetupCompiler {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidates = @()

    if (${env:ProgramFiles(x86)}) {
        $candidates += (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    }
    if ($env:ProgramFiles) {
        $candidates += (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    }
    if ($env:LOCALAPPDATA) {
        $candidates += (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    $registryRoots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )

    foreach ($root in $registryRoots) {
        $apps = Get-ItemProperty $root -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like 'Inno Setup 6*' -and $_.InstallLocation }

        foreach ($app in $apps) {
            $candidate = Join-Path $app.InstallLocation 'ISCC.exe'
            if (Test-Path -LiteralPath $candidate) {
                return $candidate
            }
        }
    }

    return $null
}

function Ensure-InnoSetupCompiler {
    $iscc = Find-InnoSetupCompiler
    if ($iscc) { return $iscc }

    $winget = Get-Command 'winget.exe' -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw @'
Inno Setup 6 is required to create A-Note-Setup.exe, and it is not installed.
Windows Package Manager (winget) was also not found, so it cannot be installed automatically.
Install Inno Setup 6 once, then rerun this script.
'@
    }

    Write-Host 'Inno Setup 6 is not installed. Installing it automatically with winget...'
    & $winget.Source install `
        --id JRSoftware.InnoSetup `
        --exact `
        --silent `
        --accept-package-agreements `
        --accept-source-agreements `
        --disable-interactivity

    if ($LASTEXITCODE -ne 0) {
        throw "winget could not install Inno Setup 6 (exit code $LASTEXITCODE)."
    }

    # winget may update PATH/registry after the process exits; search all known locations again.
    $iscc = Find-InnoSetupCompiler
    if (-not $iscc) {
        throw 'Inno Setup 6 installation completed, but ISCC.exe still could not be located. Close PowerShell, reopen it, and rerun the build.'
    }

    return $iscc
}

if (-not (Test-Path -LiteralPath $versionFile)) { throw 'VERSION file is missing.' }
if (-not (Test-Path -LiteralPath $iss)) { throw 'installer\A-Note.iss is missing.' }
if (-not (Test-Path -LiteralPath $publishScript)) { throw 'scripts\Publish-Release.ps1 is missing.' }

$version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
    throw "VERSION must look like 1.0.0 or 1.0.0.0. Current value: $version"
}

# PowerShell scripts throw on failure. Do not inspect $LASTEXITCODE here because it
# belongs to native processes and may contain a stale value.
$publishDir = (& $publishScript -Configuration $Configuration | Select-Object -Last 1)
if ([string]::IsNullOrWhiteSpace($publishDir) -or -not (Test-Path -LiteralPath $publishDir)) {
    throw 'A-Note publish did not return a valid publish directory.'
}

$iscc = Ensure-InnoSetupCompiler
Write-Host "Using Inno Setup compiler: $iscc"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot 'artifacts\installer'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$oldSetup = Join-Path $OutputDirectory "A-Note-Setup-$version.exe"
if (Test-Path -LiteralPath $oldSetup) {
    Remove-Item -LiteralPath $oldSetup -Force
}

Write-Host "Building A-Note Setup $version..."
& $iscc `
    "/DMyAppVersion=$version" `
    "/DMyOutputDir=$OutputDirectory" `
    $iss

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup build failed (exit code $LASTEXITCODE)."
}

$setup = Join-Path $OutputDirectory "A-Note-Setup-$version.exe"
if (-not (Test-Path -LiteralPath $setup)) {
    throw "Inno Setup finished without an error, but the installer was not created at: $setup"
}

$setupInfo = Get-Item -LiteralPath $setup
if ($setupInfo.Length -lt 1MB) {
    throw "The generated installer is unexpectedly small ($([Math]::Round($setupInfo.Length / 1KB)) KB). Refusing to treat it as a valid release."
}

Write-Host ''
Write-Host '----------------------------------------------'
Write-Host 'A-NOTE INSTALLER READY'
Write-Host '----------------------------------------------'
Write-Host $setup
Write-Host ("Size: {0:N1} MB" -f ($setupInfo.Length / 1MB))
