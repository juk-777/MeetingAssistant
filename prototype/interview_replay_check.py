"""Сверка событий `конец ответа` из `meeting replay` с концами ответов разметки по смыслу (#11, критерий 4 спеки).

Запуск: python interview_replay_check.py <имя> <каталог встречи replay> — replay стерео из interview_stereo.py;
разметка — prototype/out/interviews-text/<имя>.tsv и `.labels`. Печатает только числа.

Событие сверяется не по часам, а по строке, на которую оно указывает. Фразы replay режет свой детектор на канале
кандидата, и он склеивает или делит фразы иначе, чем разметка: строка «Собеседников» покрывает фразы разметки (не
интервьюера), которые начались от её таймкода до таймкода следующей такой строки (допуск 1 с). Событие пришлось на
конец ответа, если среди покрытых фраз — последняя фраза ответа. Время события по часам места в звуке не даёт: распознавание в replay может отставать от темпа звука, а
события идут той же очередью после строк. Задержку от конца ответа даёт правило (`AnswerEndPolicy`), её меряет живой
прогон.

Ответы — как в interview_truth.py: подряд идущие фразы кандидата, «угу» интервьюера короче 1 с не прерывает, от 10 с.
Чистый конец ответа — дальше фраза интервьюера; конец внутри фразы обоих (M) в долю пойманных не идёт. Событие на
фразе обоих — отдельным счётом: в стерео такая фраза целиком в канале кандидата, и ответ кончается внутри неё, а где
именно — неизвестно. Лишнее событие — на фразе кандидата, которая не последняя фраза ответа от 10 с.
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
DIRECTORY = os.path.join(ROOT, "out", "interviews-text")
BACKCHANNEL = 1.0
ANSWER_MIN = 10.0
MATCH_SECONDS = 1.0

if len(sys.argv) != 3:
    sys.exit("Запуск: python interview_replay_check.py <имя> <каталог встречи replay>")
name, meeting = sys.argv[1], sys.argv[2]

labels = {}
with open(os.path.join(DIRECTORY, name + ".labels"), encoding="utf-8") as f:
    for line in f:
        if line.strip():
            number, label = line.split()
            labels[int(number)] = label
phrases = []
with open(os.path.join(DIRECTORY, name + ".tsv"), encoding="utf-8") as f:
    for line in f:
        number, start, end, _ = line.split("\t", 3)
        phrases.append((float(start), float(end), labels[int(number)]))

# Последняя фраза каждого ответа от ANSWER_MIN: индекс фразы → чистый ли конец.
answer_ends = {}
answer_start = last = None
for index, (start, end, label) in enumerate(phrases):
    if label == "C":
        answer_start = start if answer_start is None else answer_start
        last = index
        continue
    if label == "I" and end - start < BACKCHANNEL:
        continue
    if answer_start is not None and phrases[last][1] - answer_start >= ANSWER_MIN:
        answer_ends[last] = label == "I"
    answer_start = None

line_pattern = re.compile(r"^(\d+)\t[\d.]+\t\[(\d+):(\d+):(\d+)\] Собеседники: ")
others_lines = []
with open(os.path.join(meeting, "transcript.md"), encoding="utf-8") as f:
    for line in f:
        m = line_pattern.match(line)
        if m:
            others_lines.append((int(m.group(1)), int(m.group(2)) * 3600 + int(m.group(3)) * 60 + int(m.group(4))))
line_phrases = {}
for k, (number, timecode) in enumerate(others_lines):
    upto = others_lines[k + 1][1] if k + 1 < len(others_lines) else float("inf")
    line_phrases[number] = [i for i, (start, _, label) in enumerate(phrases)
                            if label != "I" and timecode - MATCH_SECONDS <= start < upto - MATCH_SECONDS]

caught, extra, unmatched, both, events = set(), 0, 0, 0, 0
with open(os.path.join(meeting, "events.log"), encoding="utf-8") as f:
    for line in f:
        _, number, reason = line.rstrip("\n").split("\t")
        if reason != "конец ответа":
            continue
        events += 1
        covered = line_phrases.get(int(number), [])
        ends = [i for i in covered if i in answer_ends]
        if not covered:
            unmatched += 1
        elif ends:
            caught.update(ends)
        elif any(phrases[i][2] == "M" for i in covered):
            both += 1
        else:
            extra += 1

clean = [i for i, is_clean in answer_ends.items() if is_clean]
caught_clean = [i for i in clean if i in caught]
print(f"{name}: ответов от {ANSWER_MIN:.0f} с {len(answer_ends)}, чистых концов {len(clean)}, событий {events}, "
      f"строка не найдена в разметке {unmatched}")
print(f"  чистых концов с событием на последней фразе: {len(caught_clean)} из {len(clean)} "
      f"({len(caught_clean) / len(clean):.2f}); концов внутри фразы обоих с событием: "
      f"{len(caught) - len(caught_clean)} из {len(answer_ends) - len(clean)}; событий на фразе обоих {both}")
print(f"  лишних событий: {extra}, на ответ {extra / len(answer_ends):.2f}")
