$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $projectRoot 'assets\logo\A-Note_Logo_Original_HQ.png'
$output = Join-Path $projectRoot 'assets\logo\A-Note.ico'

if (-not (Test-Path -LiteralPath $source)) {
    throw "A-Note logo was not found: $source"
}

$sourceInfo = Get-Item -LiteralPath $source
if (Test-Path -LiteralPath $output) {
    $outputInfo = Get-Item -LiteralPath $output
    if ($outputInfo.LastWriteTimeUtc -ge $sourceInfo.LastWriteTimeUtc) {
        return
    }
}

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$stream = [System.IO.File]::OpenRead($source)
try {
    $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
        $stream,
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
    )
    $frame = $decoder.Frames[0]
}
finally {
    $stream.Dispose()
}

$size = 256.0
$scale = [Math]::Min($size / $frame.PixelWidth, $size / $frame.PixelHeight)
$drawWidth = $frame.PixelWidth * $scale
$drawHeight = $frame.PixelHeight * $scale
$x = ($size - $drawWidth) / 2.0
$y = ($size - $drawHeight) / 2.0

$visual = New-Object System.Windows.Media.DrawingVisual
$context = $visual.RenderOpen()
try {
    $context.DrawImage($frame, (New-Object System.Windows.Rect($x, $y, $drawWidth, $drawHeight)))
}
finally {
    $context.Close()
}

$bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(256, 256, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($visual)

$encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$pngStream = New-Object System.IO.MemoryStream
$encoder.Save($pngStream)
$pngBytes = $pngStream.ToArray()
$pngStream.Dispose()

$directory = Split-Path -Parent $output
New-Item -ItemType Directory -Path $directory -Force | Out-Null

$fileStream = [System.IO.File]::Create($output)
$writer = New-Object System.IO.BinaryWriter($fileStream)
try {
    # ICONDIR
    $writer.Write([UInt16]0) # reserved
    $writer.Write([UInt16]1) # icon
    $writer.Write([UInt16]1) # one image

    # ICONDIRENTRY. Width/height 0 means 256 px in ICO files.
    $writer.Write([Byte]0)
    $writer.Write([Byte]0)
    $writer.Write([Byte]0)
    $writer.Write([Byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$pngBytes.Length)
    $writer.Write([UInt32]22)
    $writer.Write($pngBytes)
}
finally {
    $writer.Dispose()
    $fileStream.Dispose()
}

Write-Host "A-Note icon generated: $output"
