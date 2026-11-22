# Иконка Qural для Mac: значок Windows-версии (три регулятора, Projects\Qural\tools\IconGen.cs) на светлом
# скруглённом квадрате, как у программ macOS. Пишет набор PNG в Icon.iconset — build.sh собирает из него AppIcon.icns.
$ErrorActionPreference = 'Stop'
$here = Split-Path $PSScriptRoot -Parent
$gen = Join-Path (Split-Path $here -Parent) 'Qural\tools\IconGen.cs'
Add-Type -AssemblyName System.Drawing
Add-Type -Path $gen -ReferencedAssemblies System.Drawing

function New-Squircle([System.Drawing.RectangleF]$r, [float]$rad) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $rad * 2
    $p.AddArc($r.X, $r.Y, $d, $d, 180, 90); $p.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $p.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90); $p.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

# основа 1024×1024: поле 824 с отступом 100 (сетка значков macOS), тень, светлый градиент, значок по центру
$S = 1024
$big = New-Object System.Drawing.Bitmap $S, $S, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($big)
$g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
$g.Clear([System.Drawing.Color]::Transparent)
$card = New-Object System.Drawing.RectangleF 100, 100, 824, 824
for ($i = 14; $i -ge 1; $i--) {
    $sh = New-Object System.Drawing.RectangleF ($card.X - $i), ($card.Y - $i + 12), ($card.Width + 2 * $i), ($card.Height + 2 * $i)
    $p = New-Squircle $sh (186 + $i)
    $b = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(6, 0, 0, 0)); $g.FillPath($b, $p); $b.Dispose(); $p.Dispose()
}
$p = New-Squircle $card 186
$lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $card, ([System.Drawing.Color]::FromArgb(255, 255, 255)), ([System.Drawing.Color]::FromArgb(228, 233, 242)), 90
$g.FillPath($lg, $p); $lg.Dispose()
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(30, 16, 32, 64)), 2
$g.DrawPath($pen, $p); $pen.Dispose(); $p.Dispose()
$glyph = [IconGen]::Render(600)
$g.DrawImage($glyph, 212, 212, 600, 600)
$glyph.Dispose(); $g.Dispose()

$set = Join-Path $here 'Icon.iconset'
New-Item -ItemType Directory -Force $set | Out-Null
$sizes = @{ 'icon_16x16' = 16; 'icon_16x16@2x' = 32; 'icon_32x32' = 32; 'icon_32x32@2x' = 64; 'icon_128x128' = 128;
            'icon_128x128@2x' = 256; 'icon_256x256' = 256; 'icon_256x256@2x' = 512; 'icon_512x512' = 512; 'icon_512x512@2x' = 1024 }
foreach ($k in $sizes.Keys) {
    $n = $sizes[$k]
    $bmp = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $gg = [System.Drawing.Graphics]::FromImage($bmp)
    $gg.InterpolationMode = 'HighQualityBicubic'; $gg.PixelOffsetMode = 'HighQuality'; $gg.SmoothingMode = 'AntiAlias'
    $gg.DrawImage($big, 0, 0, $n, $n); $gg.Dispose()
    $bmp.Save((Join-Path $set "$k.png"), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
$big.Dispose()
Write-Host "Готово: $set"
