param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\diagnostics')
)

$ErrorActionPreference = 'Stop'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$destination = Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory)) "a-note-crash-$stamp"
New-Item -ItemType Directory -Path $destination -Force | Out-Null

$appData = Join-Path $env:LOCALAPPDATA 'A-Note'
foreach ($name in @('open-trace.log', 'last-open-error.txt', 'last-crash.txt')) {
    $source = Join-Path $appData $name
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $destination $name)
    }
}

$events = Get-WinEvent -FilterHashtable @{
    LogName = 'Application'
    StartTime = (Get-Date).AddHours(-4)
} -ErrorAction SilentlyContinue | Where-Object {
    $_.Message -match 'A-Note.exe' -or ($_.ProviderName -eq '.NET Runtime' -and $_.Message -match 'ANote')
} | Select-Object -First 30 TimeCreated, ProviderName, Id, LevelDisplayName, Message

$events | Format-List | Out-File -LiteralPath (Join-Path $destination 'windows-events.txt') -Encoding utf8

$werRoot = 'C:\ProgramData\Microsoft\Windows\WER\ReportArchive'
if (Test-Path -LiteralPath $werRoot) {
    $reports = Get-ChildItem -LiteralPath $werRoot -Directory -Filter 'AppCrash_A-Note.exe*' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 3
    foreach ($report in $reports) {
        $reportFile = Join-Path $report.FullName 'Report.wer'
        if (Test-Path -LiteralPath $reportFile) {
            Copy-Item -LiteralPath $reportFile -Destination (Join-Path $destination ("Report-{0}.wer" -f $report.LastWriteTime.ToString('yyyyMMdd-HHmmss')))
        }
    }
}

$archive = "$destination.zip"
Compress-Archive -LiteralPath $destination -DestinationPath $archive -Force
Write-Host "A-Note crash diagnostics collected:"
Write-Host $archive
