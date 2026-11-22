# Сборка Qural.exe встроенным в Windows компилятором (.NET Framework 4.x), без установки SDK.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
# готовая программа — в G:\Project\Qural\App (две папки выше проекта), промежуточные файлы — в obj
$out  = [IO.Path]::GetFullPath((Join-Path $root '..\..\App'))
$obj  = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $out, $obj | Out-Null
if (Get-Process Qural -ErrorAction SilentlyContinue | ? { $_.Path -like "$out\*" }) { throw "Закройте Qural (правый щелчок по иконке в трее → Выход) и запустите сборку снова" }

# Иконка: три регулятора RGB (tools\IconGen.cs)
$ico = Join-Path $obj 'app.ico'
Add-Type -Path "$root\tools\IconGen.cs" -ReferencedAssemblies System.Drawing
[IconGen]::WriteIco($ico, (Join-Path $root 'logo.png'))

# WebView2 (окно настроек) и страница в оформлении GlassWise
Copy-Item "$root\lib\*.dll" $out -Force
if (Test-Path "$out\ui") { Remove-Item "$out\ui" -Recurse -Force }
Copy-Item "$root\src\ui" "$out\ui" -Recurse
# белые значки программ для кнопок Ulanzi Deck (свои можно класть и в %APPDATA%\Qural\deck-icons)
if (Test-Path "$out\deck-icons") { Remove-Item "$out\deck-icons" -Recurse -Force }
Copy-Item "$root\deck-icons" "$out\deck-icons" -Recurse

# FFmpeg для записи экрана (сборка gyan.dev, GPL) и модель шумоподавления RNNoise — копируем, только если поменялись
if (-not (Test-Path "$root\ffmpeg\ffmpeg.exe")) { & "$root\tools\get-ffmpeg.ps1" }
New-Item -ItemType Directory -Force "$out\ffmpeg" | Out-Null
foreach ($f in Get-ChildItem "$root\ffmpeg" -File) {
    $dst = Join-Path "$out\ffmpeg" $f.Name
    if (-not (Test-Path $dst) -or (Get-Item $dst).Length -ne $f.Length -or (Get-Item $dst).LastWriteTimeUtc -ne $f.LastWriteTimeUtc) { Copy-Item $f.FullName $dst -Force }
}
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    /out:"$out\Qural.exe" /win32icon:"$ico" /resource:"$ico",Qural.app.ico /win32manifest:"$root\src\app.manifest" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Runtime.Serialization.dll /r:System.Xml.dll `
    /r:System.Web.Extensions.dll /r:System.Management.dll /r:"$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll" /r:"$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.InteropServices.WindowsRuntime.dll" `
    /r:"$env:WINDIR\System32\WinMetadata\Windows.Foundation.winmd" /r:"$env:WINDIR\System32\WinMetadata\Windows.Media.winmd" /r:"$env:WINDIR\System32\WinMetadata\Windows.Storage.winmd" /r:"$root\lib\Microsoft.Web.WebView2.Core.dll" /r:"$root\lib\Microsoft.Web.WebView2.WinForms.dll" `
    "$root\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw "Ошибка сборки" }
# настройки .NET для Qural.exe (без фонового сборщика мусора, быстрый старт)
Copy-Item "$root\src\app.config" "$out\Qural.exe.config" -Force
Write-Host "Готово: $out\Qural.exe"
