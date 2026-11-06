[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $repositoryRoot 'assets\brand\fluxreader-icon.ico'
$outputPath = Join-Path $repositoryRoot 'assets\brand\fluxreader-icon-unread.ico'
$sourceBytes = [IO.File]::ReadAllBytes($sourcePath)
$frameCount = [BitConverter]::ToUInt16($sourceBytes, 4)
$frames = [Collections.Generic.List[byte[]]]::new()
$redBrush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#F04747'))

try {
    # Preserve every original resolution; draw the indicator separately at each size.
    for ($frameIndex = 0; $frameIndex -lt $frameCount; $frameIndex++) {
        $entryOffset = 6 + 16 * $frameIndex
        $frameLength = [BitConverter]::ToUInt32($sourceBytes, $entryOffset + 8)
        $frameOffset = [BitConverter]::ToUInt32($sourceBytes, $entryOffset + 12)
        $sourceStream = [IO.MemoryStream]::new($sourceBytes, $frameOffset, $frameLength)
        $image = $null
        $bitmap = $null
        $graphics = $null
        $pngStream = [IO.MemoryStream]::new()
        try {
            $image = [Drawing.Image]::FromStream($sourceStream)
            $bitmap = [Drawing.Bitmap]::new($image)
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $size = $bitmap.Width
            # Match the reference badge: about 12 px on a 32 px tray icon,
            # overlapping the upper-right corner without a border.
            $graphics.FillEllipse($redBrush,
                [single]($size * 0.62), [single]($size * 0.01),
                [single]($size * 0.37), [single]($size * 0.37))
            $bitmap.Save($pngStream, [Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($pngStream.ToArray())
        }
        finally {
            if ($graphics) { $graphics.Dispose() }
            if ($bitmap) { $bitmap.Dispose() }
            if ($image) { $image.Dispose() }
            $pngStream.Dispose()
            $sourceStream.Dispose()
        }
    }
}
finally {
    $redBrush.Dispose()
}

$outputStream = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($outputStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frameCount)
    $outputOffset = 6 + 16 * $frameCount
    for ($frameIndex = 0; $frameIndex -lt $frameCount; $frameIndex++) {
        # Keep dimensions, color depth, and planes from the source directory.
        $writer.Write($sourceBytes, (6 + 16 * $frameIndex), 8)
        $writer.Write([uint32]$frames[$frameIndex].Length)
        $writer.Write([uint32]$outputOffset)
        $outputOffset += $frames[$frameIndex].Length
    }

    foreach ($frame in $frames) {
        $writer.Write($frame)
    }

    $writer.Flush()
    [IO.File]::WriteAllBytes($outputPath, $outputStream.ToArray())
}
finally {
    $writer.Dispose()
    $outputStream.Dispose()
}

Write-Output "Generated $outputPath ($frameCount resolutions)."

# The shell positions this standalone status dot over the taskbar base icon.
$overlayPath = Join-Path $repositoryRoot 'assets\brand\fluxreader-unread-overlay.ico'
$overlayBitmap = [Drawing.Bitmap]::new(16, 16)
$overlayGraphics = [Drawing.Graphics]::FromImage($overlayBitmap)
$overlayBrush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#F04747'))
$overlayPng = [IO.MemoryStream]::new()
$overlayStream = [IO.MemoryStream]::new()
$overlayWriter = [IO.BinaryWriter]::new($overlayStream)
try {
    $overlayGraphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    # Keep the required 16 px canvas, but use a small centered status dot.
    # Filling this canvas makes the dot dominate Windows 11 taskbar icons.
    $overlayGraphics.FillEllipse($overlayBrush, [single]4.5, [single]4.5, [single]7, [single]7)
    $overlayBitmap.Save($overlayPng, [Drawing.Imaging.ImageFormat]::Png)
    $overlayBytes = $overlayPng.ToArray()
    $overlayWriter.Write([uint16]0)
    $overlayWriter.Write([uint16]1)
    $overlayWriter.Write([uint16]1)
    $overlayWriter.Write([byte]16)
    $overlayWriter.Write([byte]16)
    $overlayWriter.Write([byte]0)
    $overlayWriter.Write([byte]0)
    $overlayWriter.Write([uint16]1)
    $overlayWriter.Write([uint16]32)
    $overlayWriter.Write([uint32]$overlayBytes.Length)
    $overlayWriter.Write([uint32]22)
    $overlayWriter.Write($overlayBytes)
    $overlayWriter.Flush()
    [IO.File]::WriteAllBytes($overlayPath, $overlayStream.ToArray())
}
finally {
    $overlayWriter.Dispose()
    $overlayStream.Dispose()
    $overlayPng.Dispose()
    $overlayBrush.Dispose()
    $overlayGraphics.Dispose()
    $overlayBitmap.Dispose()
}

Write-Output "Generated $overlayPath (16 px status overlay)."
