# Установка плагинов Qural в Ulanzi Studio: копирует папки плагинов и перезапускает Ulanzi Studio.
#   install.ps1                 — все плагины
#   install.ps1 volume          — только «Qural — громкость»
#   install.ps1 claude          — только «Qural — Claude» (заодно подключает хуки в ~/.claude/settings.json)
#   install.ps1 discord         — только «Qural — Discord»
param([string[]]$Only = @('volume', 'claude', 'discord'))
$ErrorActionPreference = 'Stop'
$exe = 'C:\Program Files (x86)\Ulanzi Studio\UlanziDeck.exe'
$node = 'C:\Program Files (x86)\Ulanzi Studio\nodejs\node.exe'
$names = $Only | ForEach-Object { "com.qural.$_.ulanziPlugin" }

$running = Get-Process UlanziDeck -ErrorAction SilentlyContinue
if ($running) { $running | Stop-Process -Force; Start-Sleep -Seconds 2 }

foreach ($name in $names) {
    $src = Join-Path $PSScriptRoot $name
    $dst = Join-Path $env:APPDATA "Ulanzi\UlanziDeck\Plugins\$name"
    # Node плагина мог остаться — он держит файлы
    Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { $_.CommandLine -like "*$name*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
    Copy-Item $src $dst -Recurse
    Write-Host "Плагин скопирован: $dst"
    if (Test-Path "$dst\hook\setup.cjs") { & $node "$dst\hook\setup.cjs" install }
}

if (Test-Path $exe) { Start-Process $exe -ArgumentList '--autorun'; Write-Host 'Ulanzi Studio запущена заново' }