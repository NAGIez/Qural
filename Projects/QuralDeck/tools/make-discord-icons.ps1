# Перерисовать картинки плагина «Qural — Discord» (tools\DiscordTiles.cs → assets\*.png)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$exe  = Join-Path $env:TEMP 'qural-discord-tiles.exe'
$csc  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /codepage:65001 /out:$exe /r:System.Drawing.dll (Join-Path $PSScriptRoot 'DiscordTiles.cs')
if ($LASTEXITCODE -ne 0) { throw 'Ошибка сборки DiscordTiles.cs' }
& $exe (Join-Path $root 'com.qural.discord.ulanziPlugin\assets')
Remove-Item $exe -ErrorAction SilentlyContinue
