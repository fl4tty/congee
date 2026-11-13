# Draws congee.ico (16-256 px, PNG-compressed entries) into the project root:
# a blue bowl of congee with red beans and a spoon. Tray sizes (16-24 px) drop the
# beans and the bowl's band and zoom in a little, so it still reads at 16 px.
#   powershell -ExecutionPolicy Bypass -File tools\make_icon.ps1
#   ... -Preview preview.png   also renders the icon at several sizes on light and dark
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'congee.ico'), [string]$Preview = '')
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
function C($hex, $a = 255) { [System.Drawing.Color]::FromArgb($a, [Convert]::ToInt32($hex.Substring(0, 2), 16), [Convert]::ToInt32($hex.Substring(2, 2), 16), [Convert]::ToInt32($hex.Substring(4, 2), 16)) }
function P($x, $y) { New-Object System.Drawing.PointF ([float]$x), ([float]$y) }

function Draw([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $small = $s -le 24
    # The drawing below spans y 0.19-0.90 (spoon tip to foot): centre it and scale it to fill the square
    $zoom = if ($small) { 1.17 } else { 1.13 }
    $g.TranslateTransform([float](0.5 * $s), [float](0.5 * $s)); $g.ScaleTransform($zoom, $zoom); $g.TranslateTransform([float](-0.5 * $s), [float](-0.545 * $s))

    # bowl body
    $body = New-Object System.Drawing.Drawing2D.GraphicsPath
    $body.AddBezier((P (0.08 * $s) (0.48 * $s)), (P (0.10 * $s) (0.74 * $s)), (P (0.30 * $s) (0.86 * $s)), (P (0.50 * $s) (0.86 * $s)))
    $body.AddBezier((P (0.50 * $s) (0.86 * $s)), (P (0.70 * $s) (0.86 * $s)), (P (0.90 * $s) (0.74 * $s)), (P (0.92 * $s) (0.48 * $s)))
    $body.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (P 0 (0.45 * $s)), (P 0 (0.9 * $s)), (C '5b9be0'), (C '1f4f99')
    # foot
    $g.FillRectangle((New-Object System.Drawing.SolidBrush (C '1a4282')), [float](0.34 * $s), [float](0.82 * $s), [float](0.32 * $s), [float]([math]::Max(1.5, 0.08 * $s)))
    $g.FillPath($grad, $body)
    if (-not $small) {  # decorative band
        $band = New-Object System.Drawing.Pen (C 'dbe9f8' 200), ([math]::Max(1, $s * 0.022))
        $g.DrawBezier($band, (P (0.15 * $s) (0.62 * $s)), (P (0.35 * $s) (0.70 * $s)), (P (0.65 * $s) (0.70 * $s)), (P (0.85 * $s) (0.62 * $s)))
    }

    # porridge surface + rim
    $rx = 0.42 * $s; $ry = 0.11 * $s; $cx = 0.5 * $s; $cy = 0.48 * $s
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (C '2c63b0')), [float]($cx - $rx), [float]($cy - $ry), [float](2 * $rx), [float](2 * $ry))
    $prx = $rx * 0.88; $pry = $ry * 0.80
    $pg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (P 0 ($cy - $pry)), (P 0 ($cy + $pry + 1)), (C 'fff8ea'), (C 'eadbbd')
    $g.FillEllipse($pg, [float]($cx - $prx), [float]($cy - $pry), [float](2 * $prx), [float](2 * $pry))
    $g.DrawEllipse((New-Object System.Drawing.Pen (C '8fc0f2'), ([math]::Max(1, $s * 0.025))), [float]($cx - $rx), [float]($cy - $ry), [float](2 * $rx), [float](2 * $ry))

    # toppings (light brown / tan, e.g. peanuts or fried shallots) at larger sizes
    if ($s -ge 32) {
        $tan = New-Object System.Drawing.SolidBrush (C 'c4986a'); $d = 0.055 * $s
        foreach ($t in @(@(0.36, 0.46), @(0.44, 0.50), @(0.31, 0.50))) { $g.FillEllipse($tan, [float]($t[0] * $s - $d / 2), [float]($t[1] * $s - $d / 2), [float]$d, [float]$d) }
        $tan2 = New-Object System.Drawing.SolidBrush (C 'a97b4f')
        $g.FillEllipse($tan2, [float](0.40 * $s - $d / 2), [float](0.445 * $s - $d / 2), [float]$d, [float]$d)
    }

    # spoon: bowl of the spoon in the porridge, handle out over the rim
    $spoonCol = C 'eef2f7'; $edge = C '6f86a3'
    $ow = [math]::Max(1, $s * 0.018)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $spoonCol), [float](0.53 * $s), [float](0.43 * $s), [float](0.16 * $s), [float](0.08 * $s))
    if ($s -ge 32) { $g.DrawEllipse((New-Object System.Drawing.Pen $edge, $ow), [float](0.53 * $s), [float](0.43 * $s), [float](0.16 * $s), [float](0.08 * $s)) }
    $hw = [math]::Max(1.6, $s * 0.06)
    $op = New-Object System.Drawing.Pen $edge, ($hw + 2 * $ow); $op.StartCap = 'Round'; $op.EndCap = 'Round'
    $g.DrawLine($op, (P (0.64 * $s) (0.46 * $s)), (P (0.88 * $s) (0.22 * $s)))
    $hp = New-Object System.Drawing.Pen $spoonCol, $hw
    $hp.StartCap = 'Round'; $hp.EndCap = 'Round'
    $g.DrawLine($hp, (P (0.64 * $s) (0.46 * $s)), (P (0.88 * $s) (0.22 * $s)))

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}

$images = foreach ($s in $sizes) { , (Draw $s) }
$fs = [System.IO.File]::Create($Out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = $sizes[$i] % 256
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$images[$i].Length); $bw.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()

if (-not $Preview) { Write-Host "Wrote $Out"; exit 0 }

# preview: big + 48/32/24/16 actual size and 4x zoom, on light and dark (like a taskbar)
Add-Type @'
using System; using System.Runtime.InteropServices;
public class BowlIco { [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr LoadImage(IntPtr h, string n, uint t, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr i, int cx, int cy, uint s, IntPtr b, uint f); }
'@
$prev = New-Object System.Drawing.Bitmap 1360, 300
$g = [System.Drawing.Graphics]::FromImage($prev)
$g.FillRectangle((New-Object System.Drawing.SolidBrush (C 'f3f3f3')), 0, 0, 680, 300)
$g.FillRectangle((New-Object System.Drawing.SolidBrush (C '202020')), 680, 0, 680, 300)
$g.InterpolationMode = 'NearestNeighbor'
foreach ($half in 0, 680) {
    $x = $half + 15
    foreach ($s in 256, 48, 32, 24, 16) {
        $h = [BowlIco]::LoadImage([IntPtr]::Zero, $Out, 1, $s, $s, 0x10)
        $tmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $tg = [System.Drawing.Graphics]::FromImage($tmp); $tg.Clear([System.Drawing.Color]::Transparent)
        $hdc = $tg.GetHdc(); [BowlIco]::DrawIconEx($hdc, 0, 0, $h, $s, $s, 0, [IntPtr]::Zero, 3) | Out-Null; $tg.ReleaseHdc($hdc); $tg.Dispose()
        if ($s -eq 256) { $g.DrawImage($tmp, $x, 15, 200, 200); $x += 215 }
        else { $g.DrawImage($tmp, $x, 240, $s, $s); $g.DrawImage($tmp, $x, 15, 96, 96); $x += 108 }
    }
}
$prev.Save($Preview)
Write-Host "Wrote $Out and $Preview"
