# Скачать net-merge (github.com/net-merge/net-merge, MIT) — переносную сборку для Windows — в папку netmerge рядом
# с build.ps1. Qural запускает его сам (вкладка «Сеть»): без значка в трее и без отдельной установки.
# В репозитории бинарников нет; версия и SHA-256 закреплены ниже (сверены с GitHub Releases).
$ErrorActionPreference = 'Stop'
$version = '0.4.2'
$sha256 = '9216212e161dab3d920ca642eaa5556d01a13be4d03d5607d5becc2620440da6'
$root = Split-Path $PSScriptRoot -Parent
$dst = Join-Path $root 'netmerge'
if ((Test-Path "$dst\net-merge.exe") -and (Test-Path "$dst\VERSION") -and ((Get-Content "$dst\VERSION" -Raw).Trim() -eq $version)) { Write-Host "net-merge $version уже есть: $dst"; return }
$tmp = Join-Path $root 'obj\netmerge-download'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Force $tmp, $dst | Out-Null
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$url = "https://github.com/net-merge/net-merge/releases/download/v$version/net-merge-$version-portable.zip"
Write-Host "Скачиваю net-merge $version (~10 МБ)…"
Invoke-WebRequest $url -OutFile "$tmp\nm.zip" -UseBasicParsing
$actual = (Get-FileHash "$tmp\nm.zip" -Algorithm SHA256).Hash.ToLower()
if ($actual -ne $sha256) { throw "Контрольная сумма net-merge не совпала — файл повреждён или подменён" }
Expand-Archive "$tmp\nm.zip" -DestinationPath "$tmp\x" -Force
Get-ChildItem $dst -File | Remove-Item -Force
Get-ChildItem "$tmp\x" -Recurse -File | ForEach-Object { Copy-Item $_.FullName $dst -Force }
Set-Content "$dst\VERSION" $version -Encoding ASCII
Remove-Item $tmp -Recurse -Force
Write-Host "net-merge готов: $dst"
Get-ChildItem $dst | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Host
