# Ставит твой профиль Ulanzi вместо заводского для деки D200X (то, что Ulanzi берёт для новой деки или после сброса).
# Сначала собери профиль: python tools\make-default-profile.py. Нужны права администратора (папка Ulanzi в Program Files) —
# скрипт сам попросит. Заводской файл сохраняется рядом как «.orig»; вернуть: -Restore.
param([switch]$Restore)
$ErrorActionPreference = 'Stop'
$me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Restore) { $a += '-Restore' }
    Start-Process powershell -Verb RunAs -ArgumentList $a
    return
}
$name = 'Default Profile.ulanziDeckProfile'
$src = Join-Path (Split-Path $PSScriptRoot) "profiles\$name"
$dirs = @(
    'C:\Program Files (x86)\Ulanzi Studio\Ulanzi\UlanziDeck\defProfile\D200X\Default_Profile',
    (Join-Path $env:APPDATA 'Ulanzi\UlanziDeck\defProfile\D200X\Default_Profile')
)
foreach ($d in $dirs) {
    if (-not (Test-Path $d)) { Write-Host "Нет папки: $d"; continue }
    $dst = Join-Path $d $name
    if ($Restore) {
        if (Test-Path "$dst.orig") { Copy-Item "$dst.orig" $dst -Force; Write-Host "Заводской профиль возвращён: $d" }
        continue
    }
    if (-not (Test-Path $src)) { throw "Нет $src — сначала python tools\make-default-profile.py" }
    if (-not (Test-Path "$dst.orig")) { Copy-Item $dst "$dst.orig" }
    Copy-Item $src $dst -Force
    Write-Host "Твой профиль стал заводским: $d"
}
Write-Host 'Готово. Окно можно закрыть.'
Read-Host | Out-Null
