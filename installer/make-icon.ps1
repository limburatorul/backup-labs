<#
    Draws app.ico: dark tile, a clock-with-arrow ("history") glyph in accent -> violet with a soft glow.
    The recipe (and the ICO writer) are File Labs' make-icon.ps1. Every size is drawn natively so small
    sizes stay crisp. Run it again after changing anything here; the build picks up app.ico.
#>
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\app.ico'

$tileTop = '#1A2232'; $tileBottom = '#0B0F17'; $accent = '#4C8DFF'; $violet = '#B15CFF'

function C($hex, $a = 255) { $c = [System.Drawing.ColorTranslator]::FromHtml($hex); [System.Drawing.Color]::FromArgb($a, $c) }
function LG($x, $y, $w, $h, $c1, $c2, $angle) { New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF $x, $y, $w, $h), $c1, $c2, $angle }
function RR([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath; $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $u = $size / 128.0
    $g.FillPath((LG (4*$u) (4*$u) (120*$u) (120*$u) (C $tileTop) (C $tileBottom) 90), (RR (4*$u) (4*$u) (120*$u) (120*$u) (26*$u)))

    # the glyph as a path, centred; small sizes get a slightly larger glyph to stay legible
    $glyph = New-Object System.Drawing.Drawing2D.GraphicsPath
    $em = if ($size -le 24) { 96 * $u } else { 84 * $u }
    $glyph.AddString([string][char]0xE81C, (New-Object System.Drawing.FontFamily 'Segoe MDL2 Assets'), 0, $em, (New-Object System.Drawing.PointF 0, 0), [System.Drawing.StringFormat]::GenericTypographic)
    $b = $glyph.GetBounds()
    $m = New-Object System.Drawing.Drawing2D.Matrix; $m.Translate(64*$u - $b.X - $b.Width / 2, 64*$u - $b.Y - $b.Height / 2); $glyph.Transform($m)
    $b = $glyph.GetBounds()

    if ($size -ge 32) {   # glow only where there are pixels for it; at 16/20 px it just smears
        foreach ($w in 9, 6, 3.5) {
            $pen = New-Object System.Drawing.Pen (LG $b.X $b.Y $b.Width $b.Height (C $accent 30) (C $violet 30) 45), ($w * $u)
            $pen.LineJoin = 'Round'; $g.DrawPath($pen, $glyph)
        }
    }
    $g.FillPath((LG $b.X $b.Y $b.Width $b.Height (C $accent) (C $violet) 45), $glyph)
    $g.Dispose(); $bmp
}

# 256 as PNG; smaller sizes as classic 32-bit DIBs, which every consumer reads (resource
# compiler, Inno Setup, older shell code) — PNG entries below 256 are not universally supported.
function Dib([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width; $ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ms
    $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2)); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $c = $bmp.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) } }
    $bw.Write((New-Object byte[] ([int]([Math]::Ceiling($s / 32.0) * 4) * $s)))   # AND mask: alpha does the work
    $bw.Flush(); , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = Draw $s
    if ($s -ge 256) { $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); , $ms.ToArray() } else { Dib $bmp }
}

$fs = [IO.File]::Create($out); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
Write-Host "wrote $out"
