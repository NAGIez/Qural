# Собрать установщик: dist\QuralSetup-<версия>.exe — внутри Qural (App) и плагины Ulanzi Studio.
# Сначала собирает сам Qural (build.ps1). Перед запуском закройте Qural (трей → Выход).
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$projects = Split-Path $here -Parent
$repo = Split-Path $projects -Parent
$app = Join-Path $repo 'App'
$obj = Join-Path $here 'obj'
$dist = Join-Path $repo 'dist'

& (Join-Path $projects 'Qural\build.ps1')

$ver = (Get-Item "$app\Qural.exe").VersionInfo.FileVersion -replace '\.0$', ''
Write-Host "Версия: $ver"

# что кладём: программа без личных файлов, плагины без журналов и кэша
if (Test-Path $obj) { Remove-Item $obj -Recurse -Force }
$stage = Join-Path $obj 'payload'
New-Item -ItemType Directory -Force "$stage\app", "$stage\plugins", $dist | Out-Null
Get-ChildItem $app | Where-Object { $_.Name -ne 'Ярлыки для деки' } | ForEach-Object { Copy-Item $_.FullName "$stage\app" -Recurse -Force }
$skip = @('plugin.log', 'limits.json', 'test-media.txt')
foreach ($p in Get-ChildItem (Join-Path $projects 'QuralDeck') -Directory -Filter 'com.qural.*.ulanziPlugin') {
    $dst = Join-Path "$stage\plugins" $p.Name
    Copy-Item $p.FullName $dst -Recurse -Force
    Get-ChildItem $dst -Recurse -File | Where-Object { $skip -contains $_.Name } | Remove-Item -Force
    Get-ChildItem $dst -Recurse -Directory -Filter cache | Remove-Item -Recurse -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $obj 'payload.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host ("Архив: {0:N0} МБ" -f ((Get-Item $zip).Length / 1MB))

Set-Content (Join-Path $obj 'Version.g.cs') "static class Version { public const string Text = `"$ver`"; }" -Encoding UTF8
$ico = Join-Path $projects 'Qural\obj\app.ico'
$out = Join-Path $dist "QuralSetup-$ver.exe"
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
& "$fw\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /out:"$out" /win32icon:"$ico" `
    /resource:"$zip",payload.zip `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll `
    /r:"$fw\System.IO.Compression.dll" `
    "$here\Setup.cs" (Join-Path $obj 'Version.g.cs')
if ($LASTEXITCODE -ne 0) { throw "Ошибка сборки установщика" }
Remove-Item $stage -Recurse -Force
Write-Host ("Готово: {0} ({1:N0} МБ)" -f $out, ((Get-Item $out).Length / 1MB))
