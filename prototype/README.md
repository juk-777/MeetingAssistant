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
  пишет `live/transcript.md` и `live/events.log` в формате прототипа. В продукте его заменяет `meeting replay`
  (`docs/architecture.md`, «Управление»): формат строк с каналом, правила событий из домена. Скрипт остаётся опорой
  прошлых замеров.
- `delta.sh` — новые строки транскрипта с прошлого вызова (смещение в `live/.offset`).
- `hint.sh <epoch события> <текст|SKIP>` — фиксирует подсказку и задержку от события в `live/hints.md`.
- `wnet/` — Whisper.net 1.9.1 по фразам из `seg/`: `dotnet run -c Release -- <seg/имя> <ggml-модель> <выход> <потоки> [prompt]`;
  `GLUE=1` — подряд идущие фразы кусками до 30 с, как `meeting refine`.
- `rnnt.py <seg/имя> <выход> <greedy|beam> [словарь] [сила]` — GigaAM v3 RNNT с пунктуацией по фразам из `seg/`;
  со словарём — hotwords (только `beam`). Потоки — `THREADS` (8).
- `spm2vocab.py <tokenizer.model> > <bpe.vocab>` — словарь токенизатора GigaAM для hotwords (`docs/measurements.md`,
  «Модели»).
- `glossary_check.py <словарь> <вывод>... [--hits]` — слова, термины словаря и повторы подряд по выводам
  распознавания (`docs/measurements.md`, «Термины словаря в распознавании»).
- `meeting-feed.sh <каталог встречи> <сценарий>` — встреча без звука для прогона скилла: строки и события
  по сценарию (`meeting-feed.tsv`) после подключения сессии; сессия — `claude -p "/meeting '<каталог>'"
  --output-format stream-json --verbose` с временем прихода каждой строки. `hint_latency.py <stream.tsv> <hints.md>` —
  задержка подсказки по частям (`docs/measurements.md`, «Скилл `/meeting`: общие подсказки и задержка по частям»).
- `terms-reading.txt` — текст для замера английских терминов (читается вслух, запись — в `samples/`),
  `terms-reading.terms.txt` — эталонный список терминов по порядку (`docs/measurements.md`, «Английские термины»).
- `channel_offset.py <audio.wav> [окно, с] [шаг, с]` — смещение каналов встречи по взаимной корреляции, когда
  микрофон слышит динамики (`docs/measurements.md`, «Смещение каналов»).

## Прогон в реальном темпе с сессией Claude Code

1. `GLOSSARY_FILE=<словарь> .venv/bin/python recorder_sim.py call1 &` — словарь: термин-регулярка на строку,
   лежит вне git (термины реального проекта в репозиторий не кладём).
2. В сессии — `Monitor` на `tail -F live/events.log`; на каждое событие: `./delta.sh`, ответ, `./hint.sh <epoch> <текст|SKIP>`.
3. Итог — `live/hints.md`: задержка, время, текст.
