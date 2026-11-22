# Показать на кнопке «Статус» нужное состояние — проверить картинки на деке.
#   test-status.ps1 working      — «работает» (держится, пока не выключите)
#   test-status.ps1 all          — все состояния по очереди, по 5 с, потом обычная работа
#   test-status.ps1 off          — вернуть настоящий статус
# Состояния: idle (ваш ход), working (работает), thinking (думает), permission (разреши?), question (выберите),
#            done (готово), error (ошибка), full (сжать?), offline (нет связи), fun (сценка по нажатию)
param([string]$State = 'all', [int]$Seconds = 5)
$file = Join-Path $env:APPDATA 'Ulanzi\UlanziDeck\Plugins\com.qural.claude.ulanziPlugin\test-status.txt'
$all = 'idle', 'working', 'thinking', 'permission', 'question', 'done', 'error', 'full', 'offline', 'fun'

if ($State -eq 'off') { Remove-Item $file -ErrorAction SilentlyContinue; 'Статус — обычный'; return }
if ($State -eq 'all') {
    foreach ($s in $all) { Set-Content $file $s -NoNewline -Encoding ascii; "сейчас: $s"; Start-Sleep $Seconds }
    Remove-Item $file -ErrorAction SilentlyContinue; 'Статус — обычный'; return
}
if ($all -notcontains $State) { "Нет такого состояния: $State. Есть: $($all -join ', ')"; return }
Set-Content $file $State -NoNewline -Encoding ascii
"Показываю: $State (вернуть обычный — test-status.ps1 off)"
