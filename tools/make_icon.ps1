# Draws the app icon (a cube, half bare metal, half painted, on an SE2 menu-style tile) and writes app.ico
# with PNG frames 256/64/48/32/16. Run: powershell -File tools\make_icon.ps1 <output.ico>
param([string]$Out)
Add-Type -AssemblyName System.Drawing
function Frame([int]$s) {
  $b = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($b)
  $g.SmoothingMode = 'AntiAlias'
  $k = $s / 256.0
  $r = New-Object System.Drawing.RectangleF (8*$k), (8*$k), (240*$k), (240*$k)
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $rad = 28*$k
  $path.AddArc($r.X, $r.Y, $rad, $rad, 180, 90); $path.AddArc($r.Right-$rad, $r.Y, $rad, $rad, 270, 90)
  $path.AddArc($r.Right-$rad, $r.Bottom-$rad, $rad, $rad, 0, 90); $path.AddArc($r.X, $r.Bottom-$rad, $rad, $rad, 90, 90); $path.CloseFigure()
  $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $r, ([System.Drawing.Color]::FromArgb(255,35,56,63)), ([System.Drawing.Color]::FromArgb(255,8,15,18)), 90
  $g.FillPath($bg, $path)
  $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255,242,155,38)), ([float](10*$k))), $path)
  $c = 128*$k; $h = 70*$k; $top = 52*$k
  $T = @((New-Object System.Drawing.PointF $c, $top), (New-Object System.Drawing.PointF ($c+$h*1.05), ($top+$h*0.55)), (New-Object System.Drawing.PointF $c, ($top+$h*1.1)), (New-Object System.Drawing.PointF ($c-$h*1.05), ($top+$h*0.55)))
  $L = @($T[3], $T[2], (New-Object System.Drawing.PointF $c, ($top+$h*2.3)), (New-Object System.Drawing.PointF ($c-$h*1.05), ($top+$h*1.75)))
  $R = @($T[2], $T[1], (New-Object System.Drawing.PointF ($c+$h*1.05), ($top+$h*1.75)), (New-Object System.Drawing.PointF $c, ($top+$h*2.3)))
  $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,200,206,210))), $T)
  $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,120,128,134))), $L)
  $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,22,104,190))), $R)
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255,240,244,246)), ([float]([Math]::Max(1, 5*$k)))
  $pen.LineJoin = 'Round'
  $g.DrawPolygon($pen, $T); $g.DrawPolygon($pen, $L); $g.DrawPolygon($pen, $R)
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
  return ,$ms.ToArray()
}
$sizes = 256, 64, 48, 32, 16
$frames = foreach ($s in $sizes) { ,(Frame $s) }
$fs = [System.IO.File]::Create($Out); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $d = $frames[$i]
  $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$d.Length); $w.Write([UInt32]$offset)
  $offset += $d.Length
}
foreach ($d in $frames) { $w.Write($d) }
$w.Close()
"wrote $Out"
