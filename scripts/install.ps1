<#
.SYNOPSIS
Установка и обновление приложения записи `meeting` в профиле пользователя Windows.

.DESCRIPTION
Ставит `meeting` (самодостаточная сборка, свой рантайм .NET) в %LOCALAPPDATA%\MeetingAssistant\app, модели —
в %LOCALAPPDATA%\MeetingAssistant\models, задаёт переменные пользователя Speech__ModelsDirectory
и Storage__MeetingsRoot и добавляет app в PATH пользователя. Повторный запуск — обновление: app заменяется,
модели и корень встреч остаются. Устройство — docs/specs/2026-11-12-distribution.md.

Без ключей — сборка из релиза GitHub (последнего или -Version) со сверкой SHA-256. Моделей нет — качаются из релиза
sherpa-onnx и сверяются с SHA-256 ниже. -FromSource — путь разработчика: сборка из репозитория, где лежит скрипт
(нужен .NET SDK 10), модели — из его models/, если они там есть.

Пользователь (PowerShell; из WSL — то же через powershell.exe):
  iwr https://github.com/juk-777/MeetingAssistant/releases/latest/download/install.ps1 -OutFile $env:TEMP\ma-install.ps1
  powershell -ExecutionPolicy Bypass -File $env:TEMP\ma-install.ps1 [-MeetingsRoot <каталог>]
Разработчик, из корня репозитория:
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -FromSource [-MeetingsRoot <каталог>]

.PARAMETER MeetingsRoot
Корень встреч. Без параметра — заданный раньше, при первой установке — %USERPROFILE%\Meetings.

.PARAMETER Version
Тег релиза (v0.1.0). Без параметра — последний релиз.

.PARAMETER FromSource
Собрать из репозитория, где лежит скрипт, а не брать из релиза.

.PARAMETER ReleaseBase
Откуда брать файлы релиза: адрес релизов GitHub или каталог с ними (проверка установки без публикации).
#>
[CmdletBinding()]
param(
    [string]$MeetingsRoot,
    [string]$Version,
    [switch]$FromSource,
    [string]$ReleaseBase = 'https://github.com/juk-777/MeetingAssistant/releases'
)

$ErrorActionPreference = 'Stop'
# Индикатор Invoke-WebRequest в Windows PowerShell 5.1 замедляет загрузку в разы.
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$base = Join-Path $env:LOCALAPPDATA 'MeetingAssistant'
$app = Join-Path $base 'app'
$staging = Join-Path $base 'app.new'
$previous = Join-Path $base 'app.old'
$models = Join-Path $base 'models'
$downloads = Join-Path $base 'download'
$appArchive = 'meeting-win-x64.zip'

# Те же файлы, что проверяет само приложение (SpeechModels в Infrastructure).
$vadFile = 'silero_vad.onnx'
$recognizerDirectory = 'sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16'
$modelFiles = @($vadFile, "$recognizerDirectory\model.int8.onnx", "$recognizerDirectory\tokens.txt")

# Модели — из релиза sherpa-onnx; SHA-256 сняты с файлов релиза: подменённый или битый файл не ставится.
$modelRelease = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models'
$vadSha256 = '9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6'
$recognizerArchive = "$recognizerDirectory.tar.bz2"
$recognizerSha256 = '20e41e160efa6f2460a7ef6554cdbbb9e8fffb2b92e6bc708e9406bd8b9256ea'

function Test-Models([string]$directory) {
    -not ($modelFiles | Where-Object { -not (Test-Path (Join-Path $directory $_)) })
}

function Save-File([string]$source, [string]$destination) {
    if ($source -match '^https?://') {
        Invoke-WebRequest -Uri $source -OutFile $destination -UseBasicParsing
    }
    else {
        Copy-Item $source $destination -Force
    }
}

function Assert-Sha256([string]$file, [string]$expected) {
    $actual = (Get-FileHash $file -Algorithm SHA256).Hash
    if ($actual -ne $expected.Trim()) {
        Remove-Item $file -Force
        throw "Контрольная сумма $(Split-Path $file -Leaf) не совпала: файл повреждён или подменён. Ничего не изменено, повторите установку."
    }
}

function Get-ReleaseFile([string]$name) {
    if ($ReleaseBase -notmatch '^https?://') { return Join-Path $ReleaseBase $name }
    if ($Version) { return "$ReleaseBase/download/$Version/$name" }
    "$ReleaseBase/latest/download/$name"
}

# Запущенный meeting держит файлы app: замена посреди встречи оборвала бы запись.
$running = @(Get-Process -Name meeting -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($app, [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count -gt 0) {
    throw "Идёт встреча (meeting, PID $($running[0].Id)): остановите её — Ctrl+C в окне записи — и повторите."
}

New-Item -ItemType Directory -Force -Path $base, $downloads | Out-Null
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

if ($FromSource) {
    $repo = Split-Path $PSScriptRoot -Parent
    # SDK 10: в PATH или в профиле пользователя — системный dotnet может быть старше.
    $dotnet = @("$env:USERPROFILE\.dotnet\dotnet.exe", (Get-Command dotnet.exe -ErrorAction SilentlyContinue).Source) |
        Where-Object { $_ -and (Test-Path $_) -and ((& $_ --list-sdks) -match '^10\.') } |
        Select-Object -First 1
    if (-not $dotnet) {
        throw 'Нет .NET SDK 10: ни в PATH, ни в %USERPROFILE%\.dotnet. Установить: https://dot.net'
    }

    Write-Host "Сборка meeting ($dotnet)..."
    & $dotnet publish (Join-Path $repo 'src\MeetingAssistant.Cli\MeetingAssistant.Cli.csproj') `
        -c Release -r win-x64 --self-contained true -o $staging --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE." }
}
else {
    Write-Host "Загрузка meeting ($(if ($Version) { $Version } else { 'последний релиз' }))..."
    $archive = Join-Path $downloads $appArchive
    $checksum = "$archive.sha256"
    Save-File (Get-ReleaseFile "$appArchive.sha256") $checksum
    Save-File (Get-ReleaseFile $appArchive) $archive
    Assert-Sha256 $archive ((Get-Content $checksum -Raw) -split '\s+')[0]
    Expand-Archive $archive $staging -Force
    Remove-Item $archive, $checksum -Force
}

# Старый app сначала переименовывается: занятый файл даёт отказ здесь, а app остаётся целым.
if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }
if (Test-Path $app) { Rename-Item $app (Split-Path $previous -Leaf) }
Move-Item $staging $app
if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }

# Модели: у разработчика — из models/ репозитория (совпадающие robocopy не трогает); нет их — из релиза sherpa-onnx.
$sourceModels = if ($FromSource) { Join-Path (Split-Path $PSScriptRoot -Parent) 'models' }
if ($sourceModels -and (Test-Models $sourceModels)) {
    Write-Host 'Модели из репозитория...'
    New-Item -ItemType Directory -Force -Path $models | Out-Null
    Copy-Item (Join-Path $sourceModels $vadFile) $models -Force
    robocopy (Join-Path $sourceModels $recognizerDirectory) (Join-Path $models $recognizerDirectory) /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Копирование моделей: robocopy завершился с кодом $LASTEXITCODE." }
}
elseif (-not (Test-Models $models)) {
    Write-Host 'Загрузка моделей (163 МБ)...'
    New-Item -ItemType Directory -Force -Path $models | Out-Null
    $vad = Join-Path $downloads $vadFile
    Save-File "$modelRelease/$vadFile" $vad
    Assert-Sha256 $vad $vadSha256
    $recognizer = Join-Path $downloads $recognizerArchive
    Save-File "$modelRelease/$recognizerArchive" $recognizer
    Assert-Sha256 $recognizer $recognizerSha256
    tar.exe -xjf $recognizer -C $models
    if ($LASTEXITCODE -ne 0) { throw "Распаковка модели: tar завершился с кодом $LASTEXITCODE." }
    Move-Item $vad $models -Force
    Remove-Item $recognizer -Force
    if (-not (Test-Models $models)) { throw "Модели распакованы не полностью: нет одного из $($modelFiles -join ', ') в $models." }
}
Remove-Item $downloads -Recurse -Force -ErrorAction SilentlyContinue

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

$installed = & (Join-Path $app 'meeting.exe') --version
$size = (Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Готово: meeting {0}, {1} ({2:N0} МБ)" -f $installed, $app, $size)
Write-Host "Корень встреч: $root"
Write-Host 'Новое окно терминала: meeting start "<имя>"; из сессии Claude Code: /meeting start <имя>.'
