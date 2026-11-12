# MeetingAssistant

Локальный помощник на созвонах: запись двумя каналами, распознавание русской речи на CPU (GigaAM v3 через
sherpa-onnx), транскрипт в файл, подсказки рабочей сессии Claude Code по событиям — вопрос, термин, обращение.

Две части:
- **приложение записи `meeting`** — пишет микрофон («Я») и системный звук («Собеседники»), распознаёт речь
  и дописывает транскрипт в каталог встречи. Пока только Windows: звук захватывается через WASAPI; Linux — #16;
- **скилл `/meeting` для Claude Code** — сессия того проекта, о котором созвон, запускает запись, просыпается
  по событиям и подсказывает по коду и докам проекта; после встречи пишет протокол.

Звук и транскрипт остаются на машине; наружу уходит только текст, который читает сессия Claude Code.

## Установка

### Приложение записи

PowerShell:

```powershell
iwr https://github.com/juk-777/MeetingAssistant/releases/latest/download/install.ps1 -OutFile $env:TEMP\ma-install.ps1 -UseBasicParsing
powershell -ExecutionPolicy Bypass -File $env:TEMP\ma-install.ps1
```

Из терминала WSL — то же через `powershell.exe`:

```bash
powershell.exe -c 'iwr https://github.com/juk-777/MeetingAssistant/releases/latest/download/install.ps1 -OutFile $env:TEMP\ma-install.ps1 -UseBasicParsing; powershell -ExecutionPolicy Bypass -File $env:TEMP\ma-install.ps1'
```

Скрипт берёт `meeting` из последнего релиза (со своим рантаймом .NET — ставить .NET не нужно) и модели
из релиза sherpa-onnx, сверяет контрольные суммы и ставит всё в `%LOCALAPPDATA%\MeetingAssistant`. Задаёт
переменные пользователя `Storage__MeetingsRoot` (корень встреч, по умолчанию `%USERPROFILE%\Meetings`; другой —
`-MeetingsRoot <каталог>`) и `Speech__ModelsDirectory`, добавляет `meeting` в `PATH`. Конкретная версия —
`-Version v0.1.0`.

### Скилл `/meeting`

В Claude Code, запущенном в WSL (нужен `jq`: `sudo apt install jq`):

```
/plugin marketplace add juk-777/MeetingAssistant
/plugin install meeting@meeting-assistant
```

Приложение записи ещё не стоит — `/meeting start` предложит поставить его сам. Claude Code прямо в Windows, без WSL,
со скиллом пока не проверялся (#15).

### Из исходников

В WSL — нужны .NET SDK 10, git, curl, python3, jq; клон в любом каталоге:

```bash
git clone https://github.com/juk-777/MeetingAssistant.git && cd MeetingAssistant
./scripts/install-wsl.sh        # сборка meeting в WSL и установка в Windows
./scripts/install-skill.sh      # скилл — ссылкой на рабочую копию
./scripts/download-models.sh    # модели в models/ — для тестов (./scripts/test.sh), 163 МБ
```

В PowerShell, без WSL — нужны .NET SDK 10 (в `PATH` или в `%USERPROFILE%\.dotnet`) и git:

```powershell
git clone https://github.com/juk-777/MeetingAssistant.git; cd MeetingAssistant
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -FromSource
```

Ссылка `install-skill.sh` вместо плагина: правка скилла в рабочей копии видна сразу. Порядок разработки —
`docs/development.md`.

## Встреча

В сессии Claude Code того проекта, о котором созвон:

- `/meeting start <имя>` — открывает окно записи и подключает сессию к встрече. Остановка — Ctrl+C или закрытие
  окна записи.
- `/meeting` — подключиться к уже идущей встрече (запущенной `meeting start "<имя>"` в терминале Windows).
- `/meeting протокол` — после встречи: темы, решения, задачи и открытые вопросы с таймкодами в `protocol.md`.

Встреча — каталог `<корень встреч>\<дата>_<время>_<имя>`: `audio.wav`, `transcript.md`, `events.log`, `hints.md`.

Во время созвона тяжёлое (сборки, IDE) на той же машине лучше не запускать: распознавание идёт на CPU.
По RDP — с перенаправлением звука (воспроизведение и запись на этом компьютере), иначе в сеансе нет устройств:
`Audio.NoDevice`.

## Обновление и удаление

- Приложение записи — та же команда установки (из исходников — `install-wsl.sh` или `install.ps1 -FromSource`): программа заменяется,
  корень встреч и модели остаются. Во время встречи — отказ.
- Скилл — в терминале `claude plugin marketplace update meeting-assistant && claude plugin update meeting@meeting-assistant`,
  затем перезапуск сессии Claude Code.
- Удаление: `/plugin uninstall meeting@meeting-assistant` (или ссылка `~/.claude/skills/meeting`), каталог
  `%LOCALAPPDATA%\MeetingAssistant`, переменные пользователя `Storage__MeetingsRoot` и `Speech__ModelsDirectory`,
  строка `…\MeetingAssistant\app` в `PATH` пользователя.

## Документы

- Устройство — `docs/architecture.md`
- Замеры — `docs/measurements.md`
- Решения — `docs/decisions.md`
- Разработка — `docs/development.md`
- Задачи — [GitHub Issues](https://github.com/juk-777/MeetingAssistant/issues)
- Прототип и стенды — `prototype/README.md`
