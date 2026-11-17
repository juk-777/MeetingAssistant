"""Сверка событий `конец ответа` из `meeting replay` с концами ответов разметки по смыслу (#11, критерий 4 спеки).

Запуск: python interview_replay_check.py <имя> <каталог встречи replay> — replay стерео из interview_stereo.py в
реальном темпе (`--speed 1`); разметка — prototype/out/interviews-text/<имя>.tsv и `.labels`. Печатает только числа.

Время звука событий восстанавливается по часам: сдвиг часов от начала звука — наименьшее по строкам «Собеседников»
«время строки минус конец её фразы по разметке минус 0.5 с тишины VAD» (строка пишется после распознавания, поэтому
наименьшее — почти без него). Фраза строки находится по началу (таймкод строки, с точностью до секунды).

Ответы — как в interview_truth.py: подряд идущие фразы кандидата, «угу» интервьюера короче 1 с не прерывает, от 10 с.
Чистый конец ответа — дальше фраза интервьюера; конец внутри фразы обоих (M) в сверку не идёт. Печатает долю чистых
концов, у которых событие пришло в пределах WINDOW секунд, задержку событий и лишние события — не ближе 5 с после
конца любого ответа от 10 с.
"""
import os
import re
import sys

import numpy as np

ROOT = os.path.dirname(os.path.abspath(__file__))
DIRECTORY = os.path.join(ROOT, "out", "interviews-text")
BACKCHANNEL = 1.0
ANSWER_MIN = 10.0
VAD_SILENCE = 0.5
WINDOW = 3.0
NEAR = 5.0

if len(sys.argv) != 3:
    sys.exit("Запуск: python interview_replay_check.py <имя> <каталог встречи replay>")
name, meeting = sys.argv[1], sys.argv[2]

phrases = []
labels = {}
with open(os.path.join(DIRECTORY, name + ".labels"), encoding="utf-8") as f:
    for line in f:
        if line.strip():
            number, label = line.split()
            labels[int(number)] = label
with open(os.path.join(DIRECTORY, name + ".tsv"), encoding="utf-8") as f:
    for line in f:
        number, start, end, _ = line.split("\t", 3)
        phrases.append((float(start), float(end), labels[int(number)]))

# Концы ответов: (конец, чистый ли).
ends = []
answer_start = last_end = None
for start, end, label in phrases:
    if label == "C":
        answer_start = start if answer_start is None else answer_start
        last_end = end
        continue
    if label == "I" and end - start < BACKCHANNEL:
        continue
    if answer_start is not None and last_end - answer_start >= ANSWER_MIN:
        ends.append((last_end, label == "I"))
    answer_start = None

line_pattern = re.compile(r"^(\d+)\t([\d.]+)\t\[(\d+):(\d+):(\d+)\] Собеседники: ")
offsets = []
with open(os.path.join(meeting, "transcript.md"), encoding="utf-8") as f:
    for line in f:
        m = line_pattern.match(line)
        if not m:
            continue
        written = float(m.group(2))
        timecode = int(m.group(3)) * 3600 + int(m.group(4)) * 60 + int(m.group(5))
        candidates = [end for start, end, label in phrases if label != "I" and int(start) == timecode]
        if len(candidates) == 1:
            offsets.append(written - candidates[0] - VAD_SILENCE)
if not offsets:
    sys.exit("Ни одна строка «Собеседников» не нашла свою фразу в разметке")
offset = min(offsets)

events = []
with open(os.path.join(meeting, "events.log"), encoding="utf-8") as f:
    for line in f:
        epoch, _, reason = line.rstrip("\n").split("\t")
        if reason == "конец ответа":
            events.append(float(epoch) - offset)
events = np.array(events)

clean = [end for end, is_clean in ends if is_clean]
delays = []
for end in clean:
    after = events[(events >= end) & (events <= end + WINDOW)]
    if len(after):
        delays.append(after.min() - end)
extra = [e for e in events if not any(end <= e <= end + NEAR for end, _ in ends)]

print(f"{name}: строк для сдвига часов {len(offsets)}, ответов от {ANSWER_MIN:.0f} с {len(ends)}, чистых концов "
      f"{len(clean)}, событий {len(events)}")
print(f"  пойманы в пределах {WINDOW:.0f} с: {len(delays)} из {len(clean)} ({len(delays) / len(clean):.2f}); задержка "
      f"медиана {np.median(delays):.1f} с, p90 {np.quantile(delays, 0.9):.1f} с")
print(f"  лишних событий: {len(extra)}, на ответ {len(extra) / len(ends):.2f}")
