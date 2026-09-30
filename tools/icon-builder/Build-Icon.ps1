param(
    [string]$Source = (Join-Path $PSScriptRoot '../../src/XTranslate.App/Assets/xtranslate-source.png'),
    [string]$PngOutput = (Join-Path $PSScriptRoot '../../src/XTranslate.App/Assets/xtranslate.png'),
    [string]$IcoOutput = (Join-Path $PSScriptRoot '../../src/XTranslate.App/Assets/xtranslate.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Drawing.Common -ErrorAction SilentlyContinue

$sizes = [int[]](16, 20, 24, 32, 40, 48, 64, 128, 256)
function New-ResizedPngBytes {
    param(
        [System.Drawing.Image]$Image,
        [int]$Size
    )

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.DrawImage($Image, [System.Drawing.Rectangle]::new(0, 0, $Size, $Size))
        } finally {
            $graphics.Dispose()
        }

        $stream = [System.IO.MemoryStream]::new()
        try {
            # Use the explicit ImageFormat overload; PowerShell otherwise binds
            # ImageCodecInfo to the wrong Bitmap.Save overload on .NET 8.
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            # Unary comma keeps the byte[] as one pipeline value in PowerShell.
            return ,$stream.ToArray()
        } finally {
            $stream.Dispose()
        }
    } finally {
        $bitmap.Dispose()
    }
}

$sourcePath = [System.IO.Path]::GetFullPath($Source)
$pngPath = [System.IO.Path]::GetFullPath($PngOutput)
$icoPath = [System.IO.Path]::GetFullPath($IcoOutput)
foreach ($path in @($pngPath, $icoPath)) {
    $parent = [System.IO.Path]::GetDirectoryName($path)
    if ($parent) { [System.IO.Directory]::CreateDirectory($parent) | Out-Null }
}

$sourceImage = [System.Drawing.Image]::FromFile($sourcePath)
try {
    $png256 = New-ResizedPngBytes -Image $sourceImage -Size 256
    [System.IO.File]::WriteAllBytes($pngPath, $png256)

    $frames = foreach ($size in $sizes) {
        [pscustomobject]@{ Size = $size; Png = (New-ResizedPngBytes -Image $sourceImage -Size $size) }
    }
} finally {
    $sourceImage.Dispose()
}

# ICO directory followed by PNG-compressed image payloads. PNG frames preserve alpha.
$headerBytes = 6
$entryBytes = 16 * $frames.Count
$payloadOffset = $headerBytes + $entryBytes
$offsets = @()
$cursor = $payloadOffset
foreach ($frame in $frames) {
    $offsets += $cursor
    $cursor += $frame.Png.Length
}

$output = [System.IO.MemoryStream]::new()
try {
    $writer = [System.IO.BinaryWriter]::new($output)
    try {
        $writer.Write([uint16]0) # reserved
        $writer.Write([uint16]1) # icon type
        $writer.Write([uint16]$frames.Count)
        for ($i = 0; $i -lt $frames.Count; $i++) {
            $frame = $frames[$i]
            $dimension = if ($frame.Size -eq 256) { [byte]0 } else { [byte]$frame.Size }
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0) # palette entries
            $writer.Write([byte]0)
            $writer.Write([uint16]1) # color planes
            $writer.Write([uint16]32) # bits per pixel
            $writer.Write([uint32]$frame.Png.Length)
            $writer.Write([uint32]$offsets[$i])
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Png) }
        $writer.Flush()
        [System.IO.File]::WriteAllBytes($icoPath, $output.ToArray())
    } finally {
        $writer.Dispose()
    }
} finally {
    $output.Dispose()
}

Write-Output ("Wrote {0} and {1}; ICO frames: {2}" -f $pngPath, $icoPath, ($sizes -join ', '))
