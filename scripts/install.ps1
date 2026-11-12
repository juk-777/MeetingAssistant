<#
.SYNOPSIS
Установка и обновление приложения записи `meeting` в профиле пользователя Windows.

.DESCRIPTION
Публикует `meeting` самодостаточным (свой рантайм .NET) в %LOCALAPPDATA%\MeetingAssistant\app, кладёт модели
в %LOCALAPPDATA%\MeetingAssistant\models, задаёт переменные пользователя Speech__ModelsDirectory
и Storage__MeetingsRoot и добавляет app в PATH пользователя. Повторный запуск — обновление: app заменяется,
модели и корень встреч остаются. Устройство — docs/specs/2026-11-12-install-and-start.md.

Запуск из корня репозитория:
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 [-MeetingsRoot <каталог>]
Из WSL — то же через powershell.exe. Нужен .NET SDK 10 (в PATH или в %USERPROFILE%\.dotnet).

.PARAMETER MeetingsRoot
Корень встреч. Без параметра — заданный раньше, при первой установке — %USERPROFILE%\Meetings.
#>
[CmdletBinding()]
param([string]$MeetingsRoot)

$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$base = Join-Path $env:LOCALAPPDATA 'MeetingAssistant'
$app = Join-Path $base 'app'
$staging = Join-Path $base 'app.new'
$previous = Join-Path $base 'app.old'
$models = Join-Path $base 'models'
# Те же файлы, что проверяет само приложение (SpeechModels в Infrastructure).
$vadFile = 'silero_vad.onnx'
$recognizerDirectory = 'sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16'
$modelFiles = @($vadFile, "$recognizerDirectory\model.int8.onnx", "$recognizerDirectory\tokens.txt")

function Test-Models([string]$directory) {
    -not ($modelFiles | Where-Object { -not (Test-Path (Join-Path $directory $_)) })
}

# Запущенный meeting держит файлы app: замена посреди встречи оборвала бы запись.
$running = @(Get-Process -Name meeting -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($app, [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count -gt 0) {
    throw "Идёт встреча (meeting, PID $($running[0].Id)): остановите её — Ctrl+C в окне записи — и повторите."
}

# SDK 10: в PATH или в профиле пользователя — системный dotnet может быть старше.
$dotnet = @("$env:USERPROFILE\.dotnet\dotnet.exe", (Get-Command dotnet.exe -ErrorAction SilentlyContinue).Source) |
    Where-Object { $_ -and (Test-Path $_) -and ((& $_ --list-sdks) -match '^10\.') } |
    Select-Object -First 1
if (-not $dotnet) {
    throw 'Нет .NET SDK 10: ни в PATH, ни в %USERPROFILE%\.dotnet. Установить: https://dot.net'
}

Write-Host "Сборка meeting ($dotnet)..."
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
& $dotnet publish (Join-Path $repo 'src\MeetingAssistant.Cli\MeetingAssistant.Cli.csproj') `
    -c Release -r win-x64 --self-contained true -o $staging --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE." }
# Старый app сначала переименовывается: занятый файл даёт отказ здесь, а app остаётся целым.
if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }
if (Test-Path $app) { Rename-Item $app (Split-Path $previous -Leaf) }
Move-Item $staging $app
if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }

# Модели — из models/ репозитория; уже лежащие и совпадающие robocopy не трогает.
$sourceModels = Join-Path $repo 'models'
if (Test-Models $sourceModels) {
    Write-Host 'Модели...'
    New-Item -ItemType Directory -Force -Path $models | Out-Null
    Copy-Item (Join-Path $sourceModels $vadFile) $models -Force
    robocopy (Join-Path $sourceModels $recognizerDirectory) (Join-Path $models $recognizerDirectory) /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Копирование моделей: robocopy завершился с кодом $LASTEXITCODE." }
}
elseif (-not (Test-Models $models)) {
    throw "Нет моделей ни в $sourceModels, ни в $models. Скачать в models/ репозитория — docs/measurements.md, раздел «Модели»."
}

# PATH пользователя — сырым значением реестра и того же типа: через GetEnvironmentVariable записи вида %X%\bin
# вернулись бы раскрытыми и перестали следовать за X. Об изменении Windows узнаёт от SetEnvironmentVariable ниже.
$environment = Get-Item 'HKCU:\Environment'
$path = $environment.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
$kind = if ($environment.GetValueNames() -contains 'Path') { $environment.GetValueKind('Path') } else { 'ExpandString' }
$entries = @($path -split ';' | Where-Object { $_ })
if (-not ($entries | Where-Object { $_.TrimEnd('\') -ieq $app })) {
    Set-ItemProperty 'HKCU:\Environment' -Name Path -Value (($entries + $app) -join ';') -Type $kind
}

# Переменные пользователя: их читают meeting и скилл /meeting (из реестра).
$root = if ($MeetingsRoot) {
    # Относительный путь — от каталога PowerShell, а не от текущего каталога процесса .NET.
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($MeetingsRoot)
}
elseif ([Environment]::GetEnvironmentVariable('Storage__MeetingsRoot', 'User')) {
    [Environment]::GetEnvironmentVariable('Storage__MeetingsRoot', 'User')
}
else {
    Join-Path $env:USERPROFILE 'Meetings'
}
New-Item -ItemType Directory -Force -Path $root | Out-Null
[Environment]::SetEnvironmentVariable('Storage__MeetingsRoot', $root, 'User')
[Environment]::SetEnvironmentVariable('Speech__ModelsDirectory', $models, 'User')

$size = (Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Готово: {0} ({1:N0} МБ)" -f $app, $size)
Write-Host "Корень встреч: $root"
Write-Host 'Новое окно терминала: meeting start "<имя>"; из сессии Claude Code: /meeting start <имя>.'
