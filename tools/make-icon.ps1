# Generates Assets\tolk-island.ico (a black notch pill with the accent waveform).
# Run from the repo root:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\Assets\tolk-island.ico')
)

Add-Type -AssemblyName System.Drawing

$sizes  = 16, 24, 32, 48, 64, 128, 256
$black  = [System.Drawing.Color]::FromArgb(255, 0, 0, 0)
$accent = [System.Drawing.Color]::FromArgb(255, 255, 179, 64)

function New-RoundedPath {
    param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$R)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [single]([Math]::Max(0.01, $R * 2))
    $r = [single]([Math]::Min([Math]::Min($W, $H) / 2, $R))

    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

$images = New-Object System.Collections.ArrayList

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # The island silhouette: a full-width pill, slightly above centre.
    $pillW = [single]($size * 0.98)
    $pillH = [single]($size * 0.60)
    $pillX = [single](($size - $pillW) / 2)
    $pillY = [single](($size - $pillH) / 2)
    $pill = New-RoundedPath -X $pillX -Y $pillY -W $pillW -H $pillH -R ($pillH / 2)
    $blackBrush = New-Object System.Drawing.SolidBrush -ArgumentList $black
    $g.FillPath($blackBrush, $pill)

    # Three accent bars, the same shape as the now-playing waveform.
    $barW   = [single]([Math]::Max(1.0, $size * 0.085))
    $gap    = [single]([Math]::Max(0.6, $size * 0.075))
    $total  = [single](3 * $barW + 2 * $gap)
    $barX   = [single](($size - $total) / 2)
    $levels = @(0.30, 0.58, 0.42)
    $accentBrush = New-Object System.Drawing.SolidBrush -ArgumentList $accent

    for ($i = 0; $i -lt 3; $i++) {
        $barH = [single]($pillH * $levels[$i])
        $barY = [single](($size - $barH) / 2)
        $bar  = New-RoundedPath -X $barX -Y $barY -W $barW -H $barH -R ($barW / 2)
        $g.FillPath($accentBrush, $bar)
        $bar.Dispose()
        $barX = [single]($barX + $barW + $gap)
    }

    $g.Dispose()
    $pill.Dispose()
    $blackBrush.Dispose()
    $accentBrush.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    $bmp.Dispose()

    [void]$images.Add([pscustomobject]@{ Size = $size; Bytes = $bytes })
}

$output = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter -ArgumentList $output

$writer.Write([uint16]0)                 # reserved
$writer.Write([uint16]1)                 # type: icon
$writer.Write([uint16]$images.Count)     # image count

$offset = 6 + (16 * $images.Count)
foreach ($image in $images) {
    $dim = if ($image.Size -ge 256) { 0 } else { $image.Size }
    $writer.Write([byte]$dim)            # width (0 = 256)
    $writer.Write([byte]$dim)            # height
    $writer.Write([byte]0)               # palette
    $writer.Write([byte]0)               # reserved
    $writer.Write([uint16]1)             # planes
    $writer.Write([uint16]32)            # bpp
    $writer.Write([uint32]$image.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $image.Bytes.Length
}

foreach ($image in $images) {
    $writer.Write($image.Bytes)
}

$writer.Flush()
$target = [System.IO.Path]::GetFullPath($Out)
$directory = [System.IO.Path]::GetDirectoryName($target)
if (-not (Test-Path $directory)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

[System.IO.File]::WriteAllBytes($target, $output.ToArray())
$writer.Dispose()
$output.Dispose()

Write-Output "wrote $target ($([Math]::Round((Get-Item $target).Length / 1KB, 1)) KB, $($images.Count) sizes)"
