# Перерисовать картинки кнопок плагина «Qural — Claude» (tools\ClaudeTiles.cs → assets\*.png)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$exe  = Join-Path $env:TEMP 'qural-claude-tiles.exe'
$csc  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /codepage:65001 /out:$exe /r:System.Drawing.dll (Join-Path $PSScriptRoot 'ClaudeTiles.cs')
if ($LASTEXITCODE -ne 0) { throw 'Ошибка сборки ClaudeTiles.cs' }
& $exe (Join-Path $PSScriptRoot 'glyphs') (Join-Path $root 'com.qural.claude.ulanziPlugin\assets')
Remove-Item $exe -ErrorAction SilentlyContinue
