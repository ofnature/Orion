# Draws Orion's icon (a crosshair with the three belt stars across its centre, gold on navy, in
# the style of Daedalus's) and writes images/icon.png at 512x512.
#
#   powershell -File Tools\render_icon.ps1

Add-Type -AssemblyName System.Drawing

$size = 512
$bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bitmap)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.Clear([System.Drawing.Color]::Transparent)

$navy = [System.Drawing.ColorTranslator]::FromHtml('#0e1420')
$edge = [System.Drawing.ColorTranslator]::FromHtml('#6b5a1e')
$gold = [System.Drawing.ColorTranslator]::FromHtml('#d4af37')
$star = [System.Drawing.ColorTranslator]::FromHtml('#f5e6a8')

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# The tile: navy, with a thin olive line just inside its edge.
$tile = New-RoundedRect 4 4 504 504 84
$g.FillPath((New-Object System.Drawing.SolidBrush $navy), $tile)
$inner = New-RoundedRect 14 14 484 484 74
$g.DrawPath((New-Object System.Drawing.Pen $edge, 4), $inner)

# The sight: a ring and four ticks crossing it.
$pen = New-Object System.Drawing.Pen $gold, 18
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawEllipse($pen, 110, 110, 292, 292)
$g.DrawLine($pen, 256, 62, 256, 142)
$g.DrawLine($pen, 256, 370, 256, 450)
$g.DrawLine($pen, 62, 256, 142, 256)
$g.DrawLine($pen, 370, 256, 450, 256)

# The belt: three stars on a slant, joined by a faint line.
$belt = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, $gold)), 7
$belt.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$belt.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawLine($belt, 197, 282, 315, 230)
$brush = New-Object System.Drawing.SolidBrush $star
foreach ($p in @(@(197, 282), @(256, 256), @(315, 230))) {
    $g.FillEllipse($brush, $p[0] - 19, $p[1] - 19, 38, 38)
}

$g.Dispose()
$target = Join-Path (Split-Path $PSScriptRoot -Parent) 'images\icon.png'
New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
$bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
"wrote $target"
