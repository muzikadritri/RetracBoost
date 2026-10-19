# Erzeugt app.ico (PNG-komprimiert, 16-256 px): dunkle Kachel mit mintgrünem Puls.
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = [single]($size * 0.22)
    $d = $r * 2
    $rect = New-Object System.Drawing.RectangleF(([single]($size * 0.03)), ([single]($size * 0.03)), ([single]($size * 0.94)), ([single]($size * 0.94)))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(255, 24, 29, 41), [System.Drawing.Color]::FromArgb(255, 11, 13, 18), 90)
    $g.FillPath($fill, $path)
    $edge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 42, 49, 68), [single]([Math]::Max(1, $size * 0.012)))
    $g.DrawPath($edge, $path)

    $pts = @(
        @(0.17, 0.54), @(0.35, 0.54), @(0.44, 0.27), @(0.56, 0.75), @(0.65, 0.54), @(0.83, 0.54)
    ) | ForEach-Object { New-Object System.Drawing.PointF(([single]($_[0] * $size)), ([single]($_[1] * $size))) }
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 61, 220, 151), [single]($size * 0.075))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Frame $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length)
    $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'app.ico'), $out.ToArray())
"app.ico geschrieben ($($out.Length) Bytes)"
