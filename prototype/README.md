# Прототип и стенды

Python-прототип на sherpa-onnx и стенд Whisper.net, на которых сняты замеры `docs/measurements.md`. Не код продукта.
Модели — в `../models/`, записи — в `../samples/` (оба вне git).

## Окружение (WSL)

```
uv venv -q -p 3.12 .venv && uv pip install -q -p .venv sherpa-onnx==2.0.1 numpy
```

## Файлы

- `bench.py <имя>` — `samples/<имя>.wav`: нарезка Silero VAD (фразы в `seg/<имя>/` + `segments.json`), GigaAM v3 CTC
  по фразам, T-one потоком (если его модель есть в `models/`). Расшифровки — `out/`. Потоки — `THREADS` (2).
- `recorder_sim.py <имя>` — имитация приложения записи: подаёт запись в реальном темпе (`SPEED` для ускорения),
  пишет `live/transcript.md` и `live/events.log` в формате прототипа.
- `delta.sh` — новые строки транскрипта с прошлого вызова (смещение в `live/.offset`).
- `hint.sh <epoch события> <текст|SKIP>` — фиксирует подсказку и задержку от события в `live/hints.md`.
- `wnet/` — Whisper.net 1.9.1 по фразам из `seg/`: `dotnet run -c Release -- <seg/имя> <ggml-модель> <выход> <потоки> [prompt]`.

## Прогон в реальном темпе с сессией Claude Code

1. `GLOSSARY_FILE=<словарь> .venv/bin/python recorder_sim.py call1 &` — словарь: термин-регулярка на строку,
   лежит вне git (термины реального проекта в репозиторий не кладём).
2. В сессии — `Monitor` на `tail -F live/events.log`; на каждое событие: `./delta.sh`, ответ, `./hint.sh <epoch> <текст|SKIP>`.
3. Итог — `live/hints.md`: задержка, время, текст.
