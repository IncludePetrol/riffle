# Genera riffle.ico e assets/riffle-*.png (eseguire dalla cartella del progetto, Windows PowerShell 5.1)
param([string]$preview = "")
Add-Type -AssemblyName System.Drawing
function RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}
function C($a, $r, $g, $b) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }
function Brush($c) { New-Object System.Drawing.SolidBrush -ArgumentList $c }
function P([single]$x, [single]$y) { New-Object System.Drawing.PointF $x, $y }

function DrawIcon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $small = $s -lt 24
    $cw = $s * $(if ($small) { 0.66 } else { 0.56 }); $ch = $s * $(if ($small) { 0.76 } else { 0.66 })
    $r = [Math]::Max(1.5, $s * 0.07)
    $px = $s * $(if ($small) { 0.56 } else { 0.58 }); $py = $s * 0.5 + $ch * 0.46   # perno in basso al centro
    $angles = if ($small) { , -14 } else { -22, -11 }
    $cols = if ($small) { , (C 255 58 108 200) } else { (C 255 42 76 150), (C 255 64 118 210) }
    for ($i = 0; $i -lt $angles.Count; $i++) {
        $st = $g.Save(); $g.TranslateTransform([single]$px, [single]$py); $g.RotateTransform([single]$angles[$i]); $g.TranslateTransform([single](-$cw / 2), [single](-$ch))
        $g.FillPath((Brush $cols[$i]), (RoundRect 0 0 $cw $ch $r)); $g.Restore($st)
    }
    $st = $g.Save(); $g.TranslateTransform([single]($px + $s * 0.03), [single]$py); $g.RotateTransform([single]4); $g.TranslateTransform([single](-$cw / 2), [single](-$ch))
    $card = RoundRect 0 0 $cw $ch $r
    $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList (P 0 0), (P $cw $ch), (C 255 125 210 255), (C 255 58 115 240)
    $g.FillPath($lg, $card)
    $g.SetClip($card)
    $sr = $cw * 0.13
    $g.FillEllipse((Brush (C 255 255 228 125)), [single]($cw * 0.60), [single]($ch * 0.17), [single]($sr * 2), [single]($sr * 2))
    $pts = [System.Drawing.PointF[]]@((P 0 ($ch * 0.86)), (P ($cw * 0.34) ($ch * 0.50)), (P ($cw * 0.56) ($ch * 0.71)), (P ($cw * 0.73) ($ch * 0.57)), (P $cw ($ch * 0.84)), (P $cw $ch), (P 0 $ch))
    $g.FillPolygon((Brush (C 240 255 255 255)), $pts)
    $g.ResetClip(); $g.Restore($st); $g.Dispose()
    return $bmp
}

New-Item -ItemType Directory -Force assets | Out-Null
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256; $pngs = @()
foreach ($s in $sizes) { $b = DrawIcon $s; $ms = New-Object IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $pngs += , $ms.ToArray(); $b.Dispose() }
foreach ($s in 32, 512) { $b = DrawIcon $s; $b.Save("$PWD\assets\riffle-$s.png"); $b.Dispose() }

$out = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count); $off = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]; $v = $(if ($s -ge 256) { 0 } else { $s })
    $bw.Write([byte]$v); $bw.Write([byte]$v); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$d.Length); $bw.Write([uint32]$off); $off += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
[IO.File]::WriteAllBytes("$PWD\riffle.ico", $out.ToArray())

if ($preview) {
    $prev = New-Object System.Drawing.Bitmap 700, 300; $g = [System.Drawing.Graphics]::FromImage($prev)
    $g.Clear((C 255 32 32 32)); $g.FillRectangle((Brush (C 255 240 240 240)), 350, 0, 350, 300)
    $i256 = DrawIcon 256; $i32 = DrawIcon 32; $i16 = DrawIcon 16
    foreach ($ox in 0, 350) { $g.DrawImage($i256, $ox + 20, 22, 256, 256); $g.DrawImage($i32, $ox + 290, 100); $g.DrawImage($i16, $ox + 298, 160) }
    $g.Dispose(); $prev.Save($preview)
}
"riffle.ico ok"
