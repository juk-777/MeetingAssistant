# MeetingAssistant

Локальный помощник на созвонах: запись двумя каналами, распознавание русской речи на CPU (GigaAM v3 через
sherpa-onnx), транскрипт в файл, подсказки рабочей сессии Claude Code по событиям — вопрос, термин, обращение.

## Установка (Windows)

Нужны .NET SDK 10 (в `PATH` или в `%USERPROFILE%\.dotnet`) и модели в `models/` репозитория
(`docs/measurements.md`, «Модели»). Из корня репозитория:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 [-MeetingsRoot <каталог встреч>]
```

Скрипт ставит `meeting` в `%LOCALAPPDATA%\MeetingAssistant\app` (со своим рантаймом .NET), модели —
в `%LOCALAPPDATA%\MeetingAssistant\models`, задаёт переменные пользователя `Storage__MeetingsRoot` (корень встреч,
по умолчанию `%USERPROFILE%\Meetings`) и `Speech__ModelsDirectory` и добавляет `app` в `PATH`. Скилл `/meeting` —
`scripts/install-skill.sh`; корень встреч он берёт у Windows.

- Обновление — тот же скрипт: программа заменяется, корень встреч и модели остаются. Во время встречи — отказ.
- Запуск: в новом окне терминала `meeting start "<имя>"` или из сессии Claude Code `/meeting start <имя>` —
  запись в отдельном окне; остановка — Ctrl+C или закрытие окна. По RDP — с перенаправлением звука (воспроизведение
  и запись на этом компьютере), иначе в сеансе нет устройств: `Audio.NoDevice`.
- Удаление: каталог `%LOCALAPPDATA%\MeetingAssistant`, переменные пользователя `Storage__MeetingsRoot`
  и `Speech__ModelsDirectory`, строка `…\MeetingAssistant\app` в `PATH` пользователя.

## Документы

- Устройство — `docs/architecture.md`
- Замеры — `docs/measurements.md`
- Решения — `docs/decisions.md`
- Задачи — [GitHub Issues](https://github.com/juk-777/MeetingAssistant/issues)
- Прототип и стенды — `prototype/README.md`
