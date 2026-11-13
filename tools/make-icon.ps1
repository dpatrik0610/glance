# Renders Glance.ico at every size Windows asks for, each drawn natively for crisp edges: the Glance mark (orange ring,
# white pupil looking up and right) on a dark glass squircle, so it reads on light and dark taskbars alike.
# Inside the app the mark is drawn live without the tile (Logo.cs). Usage: powershell -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\Glance.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
function C($hex, $a = 255) { [System.Drawing.Color]::FromArgb($a, [Convert]::ToInt32($hex.Substring(0, 2), 16), [Convert]::ToInt32($hex.Substring(2, 2), 16), [Convert]::ToInt32($hex.Substring(4, 2), 16)) }
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $u = $s / 100 # the design is laid out in a 100x100 box
    # Tile: rounded square, lighter at the top, with a faint edge
    $x = 4 * $u; $w = 92 * $u; $d = 48 * $u
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc($x, $x, $d, $d, 180, 90); $tile.AddArc($x + $w - $d, $x, $d, $d, 270, 90)
    $tile.AddArc($x + $w - $d, $x + $w - $d, $d, $d, 0, 90); $tile.AddArc($x, $x + $w - $d, $d, $d, 90, 90); $tile.CloseFigure()
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, $x)), ([System.Drawing.PointF]::new(0, $x + $w)), (C '34343B'), (C '17171B')
    $g.FillPath($fill, $tile)
    $edge = New-Object System.Drawing.Pen (C 'FFFFFF' 34), ([Math]::Max(1, 1.5 * $u))
    $g.DrawPath($edge, $tile)
    # Ring: warm gradient, a little thicker at small sizes so it survives 16px
    $warm = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(24 * $u, 24 * $u)), ([System.Drawing.PointF]::new(76 * $u, 76 * $u)), (C 'FF9A6A'), (C 'E8442A')
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = (C 'FF9A6A'), (C 'FF6A3D'), (C 'E8442A'); $blend.Positions = 0, 0.55, 1
    $warm.InterpolationColors = $blend
    $stroke = [Math]::Max(2, 11 * $u)
    $ring = New-Object System.Drawing.Pen $warm, $stroke
    $r = 26 * $u
    $g.DrawEllipse($ring, 50 * $u - $r, 50 * $u - $r, 2 * $r, 2 * $r)
    $p = [Math]::Max(1.6, 9.5 * $u)
    $g.FillEllipse([System.Drawing.Brushes]::White, 54.5 * $u - $p, 46 * $u - $p, 2 * $p, 2 * $p)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose(); $fill.Dispose(); $edge.Dispose(); $warm.Dispose(); $ring.Dispose(); $tile.Dispose()
    , $ms.ToArray()
}
# ICO container: header, one 16-byte directory entry per image, then the PNG payloads.
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] } # 0 means 256
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"wrote $out"
