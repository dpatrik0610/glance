# Renders Glance.ico (orange ring + white dot) at every size Windows asks for, each drawn natively for crisp edges.
# Usage: powershell -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\Glance.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $stroke = [Math]::Max(2, $s * 0.16)
    $inset = $stroke / 2 + $s * 0.06
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 106, 61)), $stroke
    $g.DrawEllipse($pen, $inset, $inset, $s - 2 * $inset, $s - 2 * $inset)
    $dot = $s * 0.26
    $g.FillEllipse([System.Drawing.Brushes]::White, ($s - $dot) / 2, ($s - $dot) / 2, $dot, $dot)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose(); $pen.Dispose()
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
