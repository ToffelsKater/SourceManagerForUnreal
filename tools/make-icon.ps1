# Generates Assets\app.ico — the Source Manager (USM) app icon.
# Original mark: bold "U" on a dark rounded square with the app's accent blue.
# Re-run any time to tweak colors/shape; then rebuild via build-installer.ps1.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
$icoPath = Join-Path $assets 'app.ico'

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Dark rounded-square background
    $r = [Math]::Max(2, [int]($size * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $rect = New-Object System.Drawing.Rectangle(0, 0, ($size - 1), ($size - 1))
    $d = $r * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $bgTop = [System.Drawing.Color]::FromArgb(255, 38, 42, 54)
    $bgBot = [System.Drawing.Color]::FromArgb(255, 22, 24, 31)
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point(0, $size)), $bgTop, $bgBot)
    $g.FillPath($bgBrush, $path)

    # Accent border (skip at tiny sizes where it just muddies pixels)
    if ($size -ge 32) {
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(200, 76, 141, 255), [Math]::Max(1, $size / 42))
        $g.DrawPath($pen, $path)
        $pen.Dispose()
    }

    # "USM" (Source Manager for Unreal Engine) in accent blue gradient.
    # Three letters need a much smaller face than a single glyph to fit the square.
    $fontSize = [float]($size * 0.34)
    $font = New-Object System.Drawing.Font('Segoe UI Black', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $textTop = [System.Drawing.Color]::FromArgb(255, 122, 174, 255)
    $textBot = [System.Drawing.Color]::FromArgb(255, 62, 111, 217)
    $textBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, [int]($size * 0.25))), (New-Object System.Drawing.Point(0, [int]($size * 0.8))), $textTop, $textBot)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'
    $fmt.LineAlignment = 'Center'
    $fmt.FormatFlags = [System.Drawing.StringFormatFlags]::NoWrap
    $g.DrawString('USM', $font, $textBrush, (New-Object System.Drawing.RectangleF(0, ($size * 0.01), $size, $size)), $fmt)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    # -NoEnumerate: return the byte[] as one object, not thousands of pipeline items
    Write-Output -NoEnumerate $ms.ToArray()
}

# Pack the PNGs into a single multi-resolution .ico
$sizes = 256, 128, 64, 48, 32, 16
$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) { $images.Add([byte[]](New-IconPng $s)) }

$stream = [System.IO.File]::Create($icoPath)
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([uint16]0)                 # reserved
$writer.Write([uint16]1)                 # type: icon
$writer.Write([uint16]$sizes.Count)      # image count
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $writer.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # width (0 = 256)
    $writer.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # height
    $writer.Write([byte]0)               # palette colors
    $writer.Write([byte]0)               # reserved
    $writer.Write([uint16]1)             # planes
    $writer.Write([uint16]32)            # bits per pixel
    $writer.Write([uint32]$images[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $writer.Write([byte[]]$img) }
$writer.Dispose()
$stream.Dispose()

Write-Host "Wrote $icoPath ($((Get-Item $icoPath).Length) bytes, sizes: $($sizes -join ', '))"
