[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Export the existing symbol as a single brand ink. The original supplied logo
# remains unchanged; its alpha preserves the original outline and counters.
$source = [Drawing.Bitmap]::new((Join-Path $PSScriptRoot 'TechbaseLogo.png'))
$crop = [Drawing.Rectangle]::new(18, 14, 122, 126)
$frames = [Collections.Generic.List[byte[]]]::new()
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$attributes = [Drawing.Imaging.ImageAttributes]::new()
$matrix = [Drawing.Imaging.ColorMatrix]::new()
$matrix.Matrix00 = 0; $matrix.Matrix11 = 0; $matrix.Matrix22 = 0
$matrix.Matrix40 = 1; $matrix.Matrix41 = 90.0 / 255; $matrix.Matrix42 = 0
$attributes.SetColorMatrix($matrix)
try {
    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $stream = [IO.MemoryStream]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $height = [int][Math]::Round($size * 0.80)
            $width = [int][Math]::Round($height * $crop.Width / $crop.Height)
            $destination = [Drawing.Rectangle]::new([int](($size - $width) / 2), [int](($size - $height) / 2), $width, $height)
            $graphics.DrawImage($source, $destination, $crop.X, $crop.Y, $crop.Width, $crop.Height,
                [Drawing.GraphicsUnit]::Pixel, $attributes)
            # GDI+ interpolation can introduce RGB rounding in translucent
            # pixels. Canonicalise the brand ink, retaining every alpha byte.
            $lock = $bitmap.LockBits([Drawing.Rectangle]::new(0, 0, $size, $size),
                [Drawing.Imaging.ImageLockMode]::ReadWrite, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                $pixels = [byte[]]::new($lock.Stride * $size)
                [Runtime.InteropServices.Marshal]::Copy($lock.Scan0, $pixels, 0, $pixels.Length)
                for ($pixel = 0; $pixel -lt $pixels.Length; $pixel += 4) {
                    if ($pixels[$pixel + 3] -gt 0) {
                        $pixels[$pixel] = 0; $pixels[$pixel + 1] = 90; $pixels[$pixel + 2] = 255
                    }
                }
                [Runtime.InteropServices.Marshal]::Copy($pixels, 0, $lock.Scan0, $pixels.Length)
            } finally { $bitmap.UnlockBits($lock) }
            # Store real RGBA PNG frames. Icon.Save can quantize a bitmap-backed
            # icon into an opaque palette, losing both alpha and brand colours.
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($stream.ToArray())
            if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'TechbaseIcon.png'), $stream.ToArray()) }
        } finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    $output = [IO.File]::Create((Join-Path $PSScriptRoot 'Techbase.ico'))
    $writer = [IO.BinaryWriter]::new($output)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
    } finally { $writer.Dispose(); $output.Dispose() }
} finally { $attributes.Dispose(); $source.Dispose() }
Write-Output 'Exported transparent #FF5A00 Techbase icon in nine Windows sizes.'
