# Qural

**Qural** (Құрал — «инструмент») — программа для Windows в трее: цвет мониторов, громкость программ, скриншоты и запись экрана по горячим клавишам и с кнопок Ulanzi Deck.

- **Регулировка цвета** — профили мониторов: яркость, контраст, гамма (как в Панели NVIDIA), цифровая интенсивность и оттенок NVIDIA.
- **Микшер звука** — громкость каждой программы своими клавишами, «активное окно».
- **Скриншоты** — PrtSc: область, окно, экран; стрелки, текст, размытие, подсветка, номера шагов, пипетка.
- **Запись экрана** — область, окно или экран; кодирует видеокарта (NVENC / AMF / QSV), звук ПК и микрофон, шумоподавление RNNoise, редактор (обрезка, вырезка, кадр, MP4 / GIF / MP3).
- **Плагины Ulanzi Studio** — громкость, профиль цвета, скриншот, запись, «Сейчас играет», нагрузка ПК, Discord, Claude.

## Установка

Скачайте `QuralSetup-<версия>.exe` из [Releases](../../releases) и запустите. Права администратора не нужны: Qural ставится в
`%LOCALAPPDATA%\Programs\Qural`, ярлык — в «Пуске», удаление — через «Параметры → Приложения». При первом запуске Qural спросит,
какие инструменты включить.

Нужно: Windows 10/11 x64, .NET Framework 4.8 и Microsoft Edge WebView2 Runtime (в Windows 11 уже есть).

Настройки: `%APPDATA%\Qural\` (`color.json`, `sound.json`, `general.json`, `shot.json`, `rec.json`).

## Сборка

```powershell
Projects\Qural\build.ps1                 # Qural → App\ (FFmpeg скачается сам при первой сборке)
Projects\QuralSetup\build-setup.ps1      # установщик → dist\QuralSetup-<версия>.exe
```

Ничего ставить не нужно: собирается встроенным в Windows компилятором C# (.NET Framework 4.x).

| Папка | Что внутри |
|---|---|
| `Projects\Qural` | Исходники Qural (`src`), страница окна (`src\ui`, GlassWise UI), значки для деки, `build.ps1`. Подробности — в его README. |
| `Projects\QuralDeck` | Плагины Ulanzi Studio и `install.ps1` для разработки. |
| `Projects\QuralSetup` | Установщик: один exe с программой и плагинами внутри. |
| `App`, `dist` | Результаты сборки (в репозиторий не попадают). |

Сторонние компоненты: FFmpeg (сборка [gyan.dev](https://www.gyan.dev/ffmpeg/builds/), GPL) — в установщике и в `App\ffmpeg`,
модель RNNoise [arnndn-models](https://github.com/richardpl/arnndn-models), Microsoft WebView2 SDK, `ws` (MIT) в плагинах.
