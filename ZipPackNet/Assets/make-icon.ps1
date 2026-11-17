# Generates Assets\app.ico: a coloured rounded tile with a white "Z" whose diagonal is a zipper.
# Every size is drawn on its own (4x supersampled) so small sizes stay crisp: details such as the
# zipper teeth are only drawn where there are enough pixels to show them.
#
#   powershell -ExecutionPolicy Bypass -File ZipPackNet\Assets\make-icon.ps1 [-Theme Blue|Teal|Purple|Green|Orange|Graphite]
#       [-PreviewDir <folder>] [-PreviewOnly]
param(
    [ValidateSet('Blue', 'Teal', 'Purple', 'Green', 'Orange', 'Graphite')]
    [string]$Theme = 'Teal',
    [string]$PreviewDir,
    [switch]$PreviewOnly
)

# Each theme: tile gradient (top, bottom), the tile colour behind the letter (for the zipper notches),
# the shadow tint, and the slider gradient (top, bottom).
$themes = @{
    Blue     = @{ Top = 58, 150, 255;  Bottom = 14, 78, 204;  Mid = 30, 108, 228;  Shadow = 0, 25, 90;   SliderTop = 255, 214, 92;  SliderBottom = 255, 160, 28 }
    Teal     = @{ Top = 44, 212, 200;  Bottom = 0, 124, 136;  Mid = 14, 160, 162;  Shadow = 0, 56, 66;   SliderTop = 255, 214, 92;  SliderBottom = 255, 160, 28 }
    Purple   = @{ Top = 156, 118, 255; Bottom = 86, 42, 198;  Mid = 114, 72, 226;  Shadow = 38, 10, 98;  SliderTop = 255, 214, 92;  SliderBottom = 255, 160, 28 }
    Green    = @{ Top = 78, 206, 118;  Bottom = 14, 128, 70;  Mid = 38, 162, 90;   Shadow = 0, 58, 30;   SliderTop = 255, 214, 92;  SliderBottom = 255, 160, 28 }
    Orange   = @{ Top = 255, 166, 64;  Bottom = 230, 88, 18;  Mid = 242, 122, 38;  Shadow = 110, 38, 0;  SliderTop = 86, 98, 122;   SliderBottom = 38, 44, 60 }
    Graphite = @{ Top = 98, 108, 128;  Bottom = 38, 44, 58;   Mid = 64, 72, 90;    Shadow = 8, 10, 18;   SliderTop = 255, 214, 92;  SliderBottom = 255, 160, 28 }
}
$Palette = $themes[$Theme]
function Tint([int[]]$rgb, [int]$alpha = 255) { [System.Drawing.Color]::FromArgb($alpha, $rgb[0], $rgb[1], $rgb[2]) }

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function New-Pen([System.Drawing.Color]$color, [single]$width) {
    $pen = New-Object System.Drawing.Pen $color, $width
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $pen
}

function Draw-Z($g, [single]$s, [single]$dy, [System.Drawing.Color]$color, [bool]$teeth) {
    $l = [single](0.285 * $s); $r = [single](0.715 * $s)
    $t = [single](0.300 * $s + $dy); $b = [single](0.700 * $s + $dy)
    $stroke = [single](0.110 * $s)

    # The letter is always one solid stroke, so its silhouette reads at every size.
    $pen = New-Pen $color $stroke
    $g.DrawLines($pen, [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($l, $t), [System.Drawing.PointF]::new($r, $t),
        [System.Drawing.PointF]::new($l, $b), [System.Drawing.PointF]::new($r, $b)))
    $pen.Dispose()
    if (-not $teeth) { return }

    # Zipper teeth: notches cut into the diagonal alternately from either edge, below the slider.
    $len = [Math]::Sqrt(($l - $r) * ($l - $r) + ($b - $t) * ($b - $t))
    $ux = ($l - $r) / $len; $uy = ($b - $t) / $len      # along the diagonal, top-right to bottom-left
    $nx = -$uy; $ny = $ux                                # across it
    $notch = New-Object System.Drawing.Pen (Tint $Palette.Mid), ([single](0.026 * $s))
    $count = 6
    for ($i = 0; $i -lt $count; $i++) {
        $f = 0.47 + 0.40 * $i / ($count - 1)
        $cx = $r + $ux * $len * $f; $cy = $t + $uy * $len * $f
        $side = if ($i % 2 -eq 0) { 1 } else { -1 }
        $from = 0.62 * $stroke * $side; $to = -0.10 * $stroke * $side
        $g.DrawLine($notch, [single]($cx + $nx * $from), [single]($cy + $ny * $from), [single]($cx + $nx * $to), [single]($cy + $ny * $to))
    }
    $notch.Dispose()
}

function Draw-Icon([int]$size) {
    $scale = 4
    $s = [single]($size * $scale)
    $bmp = New-Object System.Drawing.Bitmap ([int]$s), ([int]$s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $detailed = $size -ge 64     # zipper teeth and slider
    $shaded = $size -ge 32       # gloss, rim and shadow

    # Tile ------------------------------------------------------------------------------------------
    $m = [single](0.045 * $s); $w = [single]($s - 2 * $m)
    $tile = New-RoundedRect $m $m $w $w ([single](0.225 * $w))
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
        ([System.Drawing.PointF]::new(0, $m)), ([System.Drawing.PointF]::new(0, $m + $w)), `
        (Tint $Palette.Top), (Tint $Palette.Bottom)
    $g.FillPath($fill, $tile); $fill.Dispose()

    if ($shaded) {
        # A soft light from the top-left corner gives the tile some depth.
        $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
        $glow.AddEllipse([single](-0.35 * $s), [single](-0.55 * $s), [single](1.30 * $s), [single](1.05 * $s))
        $light = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
        $light.CenterColor = [System.Drawing.Color]::FromArgb(70, 255, 255, 255)
        $light.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
        $g.SetClip($tile); $g.FillPath($light, $glow); $g.ResetClip()
        $light.Dispose(); $glow.Dispose()

        # Hairline rim: light on top, dark at the bottom.
        $inset = [single](0.012 * $s)
        $rimPath = New-RoundedRect ($m + $inset) ($m + $inset) ($w - 2 * $inset) ($w - 2 * $inset) ([single](0.225 * $w - $inset))
        $rimBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
            ([System.Drawing.PointF]::new(0, $m)), ([System.Drawing.PointF]::new(0, $m + $w)), `
            ([System.Drawing.Color]::FromArgb(90, 255, 255, 255)), (Tint $Palette.Shadow 60)
        $rim = New-Object System.Drawing.Pen $rimBrush, ([single](0.016 * $s))
        $g.DrawPath($rim, $rimPath); $rim.Dispose(); $rimBrush.Dispose(); $rimPath.Dispose()

        # Drop shadow under the letter.
        Draw-Z $g $s ([single](0.022 * $s)) (Tint $Palette.Shadow 70) $false
    }
    $tile.Dispose()

    # Letter ----------------------------------------------------------------------------------------
    Draw-Z $g $s 0 ([System.Drawing.Color]::White) $detailed

    if ($detailed) {
        # The zipper slider, in a warm accent, rides on the upper part of the diagonal.
        $cx = [single]((0.715 - 0.430 * 0.27) * $s); $cy = [single]((0.300 + 0.400 * 0.27) * $s)
        $state = $g.Save()
        $g.TranslateTransform($cx, $cy); $g.RotateTransform([single]([Math]::Atan2(0.430, 0.400) * 180 / [Math]::PI))
        $bw = [single](0.165 * $s); $bh = [single](0.200 * $s)
        $shadow = New-RoundedRect ([single](-$bw / 2)) ([single](-$bh / 2 + 0.018 * $s)) $bw $bh ([single](0.045 * $s))
        $shadowBrush = New-Object System.Drawing.SolidBrush (Tint $Palette.Shadow 80)
        $g.FillPath($shadowBrush, $shadow); $shadowBrush.Dispose(); $shadow.Dispose()

        $body = New-RoundedRect ([single](-$bw / 2)) ([single](-$bh / 2)) $bw $bh ([single](0.045 * $s))
        $gold = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
            ([System.Drawing.PointF]::new(0, -$bh / 2)), ([System.Drawing.PointF]::new(0, $bh / 2)), `
            (Tint $Palette.SliderTop), (Tint $Palette.SliderBottom)
        $g.FillPath($gold, $body); $gold.Dispose(); $body.Dispose()

        $hole = New-RoundedRect ([single](-0.030 * $s)) ([single](-0.052 * $s)) ([single](0.060 * $s)) ([single](0.060 * $s)) ([single](0.022 * $s))
        $holeBrush = New-Object System.Drawing.SolidBrush (Tint $Palette.Mid)
        $g.FillPath($holeBrush, $hole); $holeBrush.Dispose(); $hole.Dispose()
        $g.Restore($state)
    }
    $g.Dispose()

    # Downsample to the final size.
    $final = New-Object System.Drawing.Bitmap $size, $size
    $fg = [System.Drawing.Graphics]::FromImage($final)
    $fg.InterpolationMode = 'HighQualityBicubic'; $fg.PixelOffsetMode = 'HighQuality'; $fg.CompositingQuality = 'HighQuality'
    $fg.DrawImage($bmp, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
    $fg.Dispose(); $bmp.Dispose()
    $final
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($size in $sizes) {
    $bitmap = Draw-Icon $size
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($PreviewDir) { $bitmap.Save((Join-Path $PreviewDir "icon-$Theme-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bitmap.Dispose()
    , $stream.ToArray()
}

# ICO container with PNG-compressed entries.
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $writer.Write([byte]($sizes[$i] % 256)); $writer.Write([byte]($sizes[$i] % 256))
    $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$pngs[$i].Length); $writer.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $writer.Write($png) }

if ($PreviewOnly) { "Rendered $Theme previews to $PreviewDir"; return }
$target = Join-Path $PSScriptRoot 'app.ico'
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
"Wrote $target ($($out.Length) bytes, sizes: $($sizes -join ', '))"
