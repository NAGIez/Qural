# Скачать FFmpeg (сборка gyan.dev «essentials», GPL) в папку ffmpeg рядом с build.ps1.
# В репозитории его нет: ffmpeg.exe и ffprobe.exe больше 100 МБ — GitHub такие файлы не принимает.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dst = Join-Path $root 'ffmpeg'
if ((Test-Path "$dst\ffmpeg.exe") -and (Test-Path "$dst\ffprobe.exe")) { Write-Host "FFmpeg уже есть: $dst"; return }
$tmp = Join-Path $root 'obj\ffmpeg-download'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Force $tmp, $dst | Out-Null
$ProgressPreference = 'SilentlyContinue'
$url = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
Write-Host "Скачиваю FFmpeg с gyan.dev (~110 МБ)…"
Invoke-WebRequest "$url.sha256" -OutFile "$tmp\ffmpeg.sha256" -UseBasicParsing
Invoke-WebRequest $url -OutFile "$tmp\ffmpeg.zip" -UseBasicParsing
$expected = (Get-Content "$tmp\ffmpeg.sha256" -Raw).Trim().Split(' ')[0].ToLower()
$actual = (Get-FileHash "$tmp\ffmpeg.zip" -Algorithm SHA256).Hash.ToLower()
if ($expected -ne $actual) { throw "Контрольная сумма FFmpeg не совпала — файл повреждён или подменён" }
Expand-Archive "$tmp\ffmpeg.zip" -DestinationPath "$tmp\x" -Force
$bin = Get-ChildItem "$tmp\x" -Recurse -Filter ffmpeg.exe | Select-Object -First 1
Copy-Item $bin.FullName, (Join-Path $bin.DirectoryName 'ffprobe.exe') $dst -Force
$lic = Get-ChildItem "$tmp\x" -Recurse -Filter 'LICENSE*' | Select-Object -First 1
if ($lic) { Copy-Item $lic.FullName "$dst\LICENSE-ffmpeg.txt" -Force }
Remove-Item $tmp -Recurse -Force
Write-Host "FFmpeg готов: $dst"
