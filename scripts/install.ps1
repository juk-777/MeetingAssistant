<#
.SYNOPSIS
Установка и обновление приложения записи `meeting` в профиле пользователя Windows.

.DESCRIPTION
Ставит `meeting` (самодостаточная сборка, свой рантайм .NET) в %LOCALAPPDATA%\MeetingAssistant\app, модели —
в %LOCALAPPDATA%\MeetingAssistant\models, задаёт переменные пользователя Speech__ModelsDirectory
и Storage__MeetingsRoot и добавляет app в PATH пользователя. Повторный запуск — обновление: app заменяется,
модели и корень встреч остаются. Устройство — docs/specs/2026-11-12-distribution.md.

Без ключей — сборка из релиза GitHub (последнего или -Version) со сверкой SHA-256 из того же релиза. Моделей нет —
качаются (GigaAM и VAD — из релиза sherpa-onnx, Whisper turbo для уточнения встречи — с Hugging Face) и сверяются
с SHA-256, записанными ниже. Всё скачивается и проверяется до того,
как тронут установленный app. -FromSource — сборка из репозитория, где лежит скрипт (нужен .NET SDK 10), модели —
из его models/, если они там есть.

Из релиза (PowerShell; из WSL — то же через powershell.exe):
  iwr https://github.com/juk-777/MeetingAssistant/releases/latest/download/install.ps1 -OutFile $env:TEMP\ma-install.ps1
  powershell -ExecutionPolicy Bypass -File $env:TEMP\ma-install.ps1 [-MeetingsRoot <каталог>]
Из исходников, из корня репозитория:
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -FromSource [-MeetingsRoot <каталог>]

.PARAMETER MeetingsRoot
Корень встреч. Без параметра — заданный раньше, при первой установке — %USERPROFILE%\Meetings.

.PARAMETER Version
Версия релиза (v0.1.0 или 0.1.0). Без параметра — последний релиз.

.PARAMETER FromSource
Собрать из репозитория, где лежит скрипт, а не брать из релиза.

.PARAMETER ReleaseBase
Откуда брать файлы релиза: адрес релизов GitHub или каталог с ними (сборка в WSL, проверка установки).
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
if ($Version -and $Version -notmatch '^v') { $Version = "v$Version" }

# Те же файлы, что проверяет само приложение (SpeechModels в Infrastructure).
$vadFile = 'silero_vad.onnx'
$recognizerDirectory = 'sherpa-onnx-nemo-transducer-punct-giga-am-v3-russian-2025-12-16'
$modelFiles = @($vadFile) + @('encoder.int8.onnx', 'decoder.onnx', 'joiner.onnx', 'tokens.txt' |
    ForEach-Object { "$recognizerDirectory\$_" })

# Модели — из релиза sherpa-onnx; SHA-256 сняты с файлов релиза и записаны здесь (те же — в download-models.sh):
# битый или подменённый файл не ставится.
$modelRelease = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models'
$vadSha256 = '9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6'
$recognizerArchive = "$recognizerDirectory.tar.bz2"
$recognizerSha256 = 'f9620a0099019c6afcee26525ef9ed3297fa50dd5691c1902af0c948fc1a470b'

# Whisper turbo — для уточнения встречи (meeting refine); имя — SpeechModels.WhisperFile. Адрес закреплён
# на ревизии Hugging Face: по main файл мог бы смениться и разойтись с суммой.
$whisperFile = 'ggml-large-v3-turbo-q5_0.bin'
$whisperSource = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/$whisperFile"
$whisperSha256 = '394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2'

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

function Assert-Sha256([string]$file, [string]$expected, [string]$meaning) {
    $actual = (Get-FileHash $file -Algorithm SHA256).Hash
    if ($actual -ne $expected.Trim()) {
        throw "Контрольная сумма $(Split-Path $file -Leaf) не совпала: $meaning Установленное не тронуто, повторите установку."
    }
}

function Get-ReleaseFile([string]$name) {
    if ($ReleaseBase -notmatch '^https?://') { return Join-Path $ReleaseBase $name }
    if ($Version) { return "$ReleaseBase/download/$Version/$name" }
    "$ReleaseBase/latest/download/$name"
}

# Вывод в UTF-8: запущенный из WSL или скиллом скрипт пишет в канал, и кодовая страница консоли испортила бы
# кириллицу. Прежняя кодировка консоли возвращается в конце.
$consoleEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = [Text.Encoding]::UTF8

try {
    # Запущенный meeting держит файлы app: замена посреди встречи оборвала бы запись.
    $running = @(Get-Process -Name meeting -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($app, [StringComparison]::OrdinalIgnoreCase) })
    if ($running.Count -gt 0) {
        throw "Запущен meeting (PID $($running[0].Id)) — запись или уточнение встречи: остановите его — Ctrl+C в его окне — и повторите."
    }

    foreach ($leftover in $staging, $downloads) {
        if (Test-Path $leftover) { Remove-Item $leftover -Recurse -Force }
    }
    New-Item -ItemType Directory -Force -Path $base, $downloads | Out-Null

    # 1. Новый app — в app.new.
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
        $from = if ($ReleaseBase -notmatch '^https?://') { $ReleaseBase } elseif ($Version) { $Version } else { 'последний релиз' }
        Write-Host "Загрузка meeting ($from)..."
        $archive = Join-Path $downloads $appArchive
        $checksum = "$archive.sha256"
        Save-File (Get-ReleaseFile "$appArchive.sha256") $checksum
        Save-File (Get-ReleaseFile $appArchive) $archive
        # Сумма — из того же релиза: защищает от порчи при загрузке, а не от подмены релиза.
        Assert-Sha256 $archive ((Get-Content $checksum -Raw) -split '\s+')[0] 'архив повреждён при загрузке.'
        Expand-Archive $archive $staging -Force
    }

    # 2. Модели — тоже до замены app: отказ при загрузке или распаковке оставляет установленное как было.
    $sourceModels = if ($FromSource) { Join-Path (Split-Path $PSScriptRoot -Parent) 'models' }
    $copyFromRepository = $sourceModels -and (Test-Models $sourceModels)
    $unpacked = Join-Path $downloads 'models'
    $download = -not $copyFromRepository -and -not (Test-Models $models)
    if ($download) {
        Write-Host 'Загрузка моделей (172 МБ)...'
        New-Item -ItemType Directory -Force -Path $unpacked | Out-Null
        $vad = Join-Path $unpacked $vadFile
        Save-File "$modelRelease/$vadFile" $vad
        Assert-Sha256 $vad $vadSha256 'файл повреждён или подменён.'
        $recognizer = Join-Path $downloads $recognizerArchive
        Save-File "$modelRelease/$recognizerArchive" $recognizer
        Assert-Sha256 $recognizer $recognizerSha256 'файл повреждён или подменён.'
        tar.exe -xjf $recognizer -C $unpacked
        if ($LASTEXITCODE -ne 0) { throw "Распаковка модели: tar завершился с кодом $LASTEXITCODE." }
        if (-not (Test-Models $unpacked)) { throw "Модель распакована не полностью: нет одного из $($modelFiles -join ', ')." }
    }

    # Whisper — отдельно от GigaAM: у прежней установки его нет, а GigaAM уже стоит. Сверка — и для копии
    # из репозитория: models/ репозитория кладут руками.
    $whisperStaged = Join-Path $downloads $whisperFile
    $whisperFromRepository = $sourceModels -and (Test-Path (Join-Path $sourceModels $whisperFile))
    $whisperNeeded = -not (Test-Path (Join-Path $models $whisperFile))
    if ($whisperNeeded) {
        if ($whisperFromRepository) {
            Write-Host 'Модель Whisper из репозитория...'
            Copy-Item (Join-Path $sourceModels $whisperFile) $whisperStaged
        }
        else {
            Write-Host 'Загрузка модели Whisper (574 МБ)...'
            Save-File $whisperSource $whisperStaged
        }
        Assert-Sha256 $whisperStaged $whisperSha256 'файл повреждён или подменён.'
    }

    # 3. Замена app. Старый сначала переименовывается: занятый файл даёт отказ здесь, а app остаётся целым.
    if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }
    if (Test-Path $app) { Rename-Item $app (Split-Path $previous -Leaf) }
    Move-Item $staging $app
    if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }

    # 4. Модели на место: из репозитория (совпадающие robocopy не трогает) или скачанные и проверенные.
    New-Item -ItemType Directory -Force -Path $models | Out-Null
    if ($copyFromRepository) {
        Write-Host 'Модели из репозитория...'
        Copy-Item (Join-Path $sourceModels $vadFile) $models -Force
        robocopy (Join-Path $sourceModels $recognizerDirectory) (Join-Path $models $recognizerDirectory) /E /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Копирование моделей: robocopy завершился с кодом $LASTEXITCODE." }
    }
    elseif ($download) {
        # Неполный каталог модели от прошлой попытки заменяется целиком.
        $target = Join-Path $models $recognizerDirectory
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        Move-Item (Join-Path $unpacked $recognizerDirectory) $models
        Move-Item (Join-Path $unpacked $vadFile) $models -Force
    }
    if ($whisperNeeded) {
        Move-Item $whisperStaged $models
    }

    # 5. PATH пользователя — сырым значением реестра и того же типа: через GetEnvironmentVariable записи вида %X%\bin
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
}
finally {
    foreach ($leftover in $staging, $downloads) {
        if (Test-Path $leftover) { Remove-Item $leftover -Recurse -Force -ErrorAction SilentlyContinue }
    }
    [Console]::OutputEncoding = $consoleEncoding
}
