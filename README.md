# MeetingAssistant

Локальный помощник на созвонах: запись двумя каналами, распознавание русской речи на CPU (GigaAM v3 через
sherpa-onnx), транскрипт в файл, подсказки рабочей сессии Claude Code по событиям — вопрос, термин, обращение.

Запись идёт нативным процессом Windows (`meeting`), подсказки даёт сессия Claude Code в WSL — та, что открыта
в репозитории проекта, о котором созвон: у неё под рукой его код и доки.

## Что нужно

- Windows 10/11 с WSL 2 (Ubuntu) — Claude Code и скилл `/meeting` работают в WSL.
- В Windows: .NET SDK 10 (в `PATH` или в `%USERPROFILE%\.dotnet`), git.
- В WSL: git, curl, python3, jq (`sudo apt install git curl python3 jq`), Claude Code.
- Наушники или динамики и микрофон — устройства Windows по умолчанию.

## Установка

Репозиторий — на диске Windows (не в файловой системе WSL): приложение собирается и запускается из Windows.
Команды — в терминале WSL.

1. Клон:

   ```bash
   cd /mnt/d && git clone https://github.com/juk-777/MeetingAssistant.git && cd MeetingAssistant
   ```

2. Модели — в `models/` (загрузка 163 МБ, после распаковки 216 МБ):

   ```bash
   base=https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models
   mkdir -p models && cd models
   curl -sSLO $base/sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16.tar.bz2
   curl -sSLO $base/silero_vad.onnx
   python3 -c "import tarfile;tarfile.open('sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16.tar.bz2').extractall('.')"
   rm sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16.tar.bz2 && cd ..
   ```

3. Приложение записи:

   ```bash
   powershell.exe -ExecutionPolicy Bypass -File scripts/install.ps1 [-MeetingsRoot 'D:\Meetings']
   ```

   Ставит `meeting` в `%LOCALAPPDATA%\MeetingAssistant\app` (со своим рантаймом .NET), модели — в
   `%LOCALAPPDATA%\MeetingAssistant\models`, задаёт переменные пользователя Windows `Storage__MeetingsRoot`
   (корень встреч, по умолчанию `%USERPROFILE%\Meetings`) и `Speech__ModelsDirectory`, добавляет `app` в `PATH`.

4. Скилл `/meeting` для Claude Code:

   ```bash
   ./scripts/install-skill.sh
   ```

   Ссылка `~/.claude/skills/meeting` на `skills/meeting` репозитория: `git pull` обновляет и скилл. Корень встреч
   скилл берёт у Windows.

## Встреча

В сессии Claude Code того проекта, о котором созвон:

- `/meeting start <имя>` — открывает окно записи и подключает сессию к встрече. Остановка — Ctrl+C или закрытие
  окна записи.
- `/meeting` — подключиться к уже идущей встрече (запущенной `meeting start "<имя>"` в терминале Windows).
- `/meeting протокол` — после встречи: темы, решения, задачи и открытые вопросы с таймкодами в `protocol.md`.

Встреча — каталог `<корень встреч>\<дата>_<время>_<имя>`: `audio.wav`, `transcript.md`, `events.log`,
`hints.md`. Звук и транскрипт остаются на машине; наружу уходит только текст, который читает сессия Claude Code.

Во время созвона тяжёлое (сборки, IDE) на той же машине лучше не запускать: распознавание идёт на CPU.
По RDP — с перенаправлением звука (воспроизведение и запись на этом компьютере), иначе в сеансе нет устройств:
`Audio.NoDevice`.

## Обновление и удаление

- Обновление: `git pull`, затем шаг 3 установки. Программа заменяется, корень встреч и модели остаются.
  Во время встречи — отказ.
- Удаление: каталог `%LOCALAPPDATA%\MeetingAssistant`, переменные пользователя `Storage__MeetingsRoot`
  и `Speech__ModelsDirectory`, строка `…\MeetingAssistant\app` в `PATH` пользователя, ссылка
  `~/.claude/skills/meeting` в WSL.

## Документы

- Устройство — `docs/architecture.md`
- Замеры — `docs/measurements.md`
- Решения — `docs/decisions.md`
- Разработка — `docs/development.md`
- Задачи — [GitHub Issues](https://github.com/juk-777/MeetingAssistant/issues)
- Прототип и стенды — `prototype/README.md`
