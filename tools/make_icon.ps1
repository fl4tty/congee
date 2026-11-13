# Draws congee.ico (16-256 px, PNG-compressed entries) into the project root.
#   powershell -ExecutionPolicy Bypass -File tools\make_icon.ps1
# The design is a small rising line graph over a meter bar on a dark rounded tile,
# in the same Nord-ish colours as the default config.
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'congee.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function C($hex, $a = 255) { [System.Drawing.Color]::FromArgb($a, [Convert]::ToInt32($hex.Substring(0, 2), 16), [Convert]::ToInt32($hex.Substring(2, 2), 16), [Convert]::ToInt32($hex.Substring(4, 2), 16)) }

function Draw([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # tile
    $m = [math]::Max(0.5, $s * 0.03); $w = $s - 2 * $m; $r = $s * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, $r, $r, 180, 90); $path.AddArc($m + $w - $r, $m, $r, $r, 270, 90)
    $path.AddArc($m + $w - $r, $m + $w - $r, $r, $r, 0, 90); $path.AddArc($m, $m + $w - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), (C '434c5e'), (C '242933')
    $g.FillPath($grad, $path)
    if ($s -ge 32) { $g.DrawPath((New-Object System.Drawing.Pen (C '88c0d0' 90), ([math]::Max(1, $s / 64))), $path) }

    # graph
    $pts = @(@(0.17, 0.64), @(0.34, 0.47), @(0.50, 0.56), @(0.67, 0.31), @(0.83, 0.21)) |
        ForEach-Object { New-Object System.Drawing.PointF ($_[0] * $s), ($_[1] * $s) }
    $base = 0.72 * $s
    $area = New-Object System.Drawing.Drawing2D.GraphicsPath
    $area.AddLines([System.Drawing.PointF[]]($pts + @((New-Object System.Drawing.PointF $pts[-1].X, $base), (New-Object System.Drawing.PointF $pts[0].X, $base))))
    $area.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush (C '88c0d0' 70)), $area)
    $pen = New-Object System.Drawing.Pen (C '88c0d0'), ([math]::Max(1.4, $s * 0.075))
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)

    # meter bar
    $by = 0.78 * $s; $bh = [math]::Max(1.5, 0.07 * $s); $bx = 0.17 * $s; $bw = 0.66 * $s
    $g.FillRectangle((New-Object System.Drawing.SolidBrush (C '4c566a')), $bx, $by, $bw, $bh)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush (C 'a3be8c')), $bx, $by, $bw * 0.62, $bh)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}

$images = foreach ($s in $sizes) { , (Draw $s) }
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = $sizes[$i] % 256
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$images[$i].Length); $bw.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
Write-Host "Wrote $out ($((Get-Item $out).Length) bytes)"
