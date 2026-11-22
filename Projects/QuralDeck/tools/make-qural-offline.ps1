# Картинки «Qural не отвечает» для плагина громкости: логотип Qural и жёлтый знак «!» в углу.
#   assets\qural_offline.png       — кнопка 196×196, чёрный фон
#   assets\qural_offline_dial.png  — экран крутилки 200×200, прозрачный фон
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root  = Split-Path $PSScriptRoot
$logo  = [Drawing.Image]::FromFile((Join-Path $root '..\Qural\logo.png'))
$out   = Join-Path $root 'com.qural.volume.ulanziPlugin\assets'
$amber = [Drawing.Color]::FromArgb(255, 184, 0)

function Save-Tile([int]$size, [bool]$opaque, [string]$name, [int]$logoSize, [int]$badge) {
    $b = New-Object Drawing.Bitmap $size, $size
    $g = [Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear($(if ($opaque) { [Drawing.Color]::Black } else { [Drawing.Color]::Transparent }))
    $x = [int](($size - $logoSize) / 2); $y = [int](($size - $logoSize) / 2) + 4
    $g.DrawImage($logo, $x, $y, $logoSize, $logoSize)
    # жёлтый кружок с «!» в правом верхнем углу, с чёрной обводкой — читается поверх логотипа
    $bx = $size - $badge - 16; $by = 14
    $g.FillEllipse([Drawing.Brushes]::Black, $bx - 5, $by - 5, $badge + 10, $badge + 10)
    $g.FillEllipse((New-Object Drawing.SolidBrush $amber), $bx, $by, $badge, $badge)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::Black), ($badge * 0.15)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $cx = $bx + $badge / 2
    $g.DrawLine($pen, $cx, $by + $badge * 0.22, $cx, $by + $badge * 0.56)
    $d = $badge * 0.17
    $g.FillEllipse([Drawing.Brushes]::Black, $cx - $d / 2, $by + $badge * 0.70, $d, $d)
    $b.Save((Join-Path $out $name), [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $b.Dispose()
}

Save-Tile 196 $true  'qural_offline.png'      116 54
Save-Tile 200 $false 'qural_offline_dial.png' 120 58
$logo.Dispose()
Write-Host "Готово: $out\qural_offline*.png"
