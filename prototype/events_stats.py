"""Сводка пробуждений по каталогам встреч приложения (`docs/measurements.md`, «События на записях созвонов»).

Запуск: python events_stats.py <каталог встречи>[,<звук>]... — например, все встречи одного корня после
`meeting replay`. Печатает только счётчики, текст фраз не выводит: записи — голос и слова живых людей.

Длительность — по заголовку звука: audio.wav встречи, а у встречи из `meeting replay` (своего audio.wav нет) —
поданного файла после запятой; строки и слова — по transcript.md; события — по events.log. «Фраз
с ?» — строки со знаком вопроса: разница с событиями `вопрос` — то, что отсеял порог слов `WakePolicy`.
Интервал — между соседними событиями одной встречи, кроме «конец записи», по таймкоду строки события (время
встречи, а не подачи replay).
"""
import os
import re
import sys
import wave
from collections import Counter

hours = lines = words = questioned = agent = 0
reasons = Counter()
gaps = []
for argument in sys.argv[1:]:
    meeting, _, audio = argument.partition(",")
    with wave.open(audio or os.path.join(meeting, "audio.wav")) as w:
        hours += w.getnframes() / w.getframerate() / 3600
    transcript = os.path.join(meeting, "transcript.md")
    offsets = {}
    if os.path.exists(transcript):
        for line in open(transcript, encoding="utf-8"):
            number, _, rest = line.rstrip("\n").split("\t", 2)
            h, m, s = map(int, rest[1:9].split(":"))
            offsets[number] = h * 3600 + m * 60 + s
            text = rest.split(": ", 1)[-1]
            lines += 1
            words += len(re.findall(r"\w+", text))
            questioned += "?" in text
            agent += len(re.findall(r"\bагент\b", text.lower()))
    events = os.path.join(meeting, "events.log")
    times = []
    if os.path.exists(events):
        for line in open(events, encoding="utf-8"):
            _, number, causes = line.rstrip("\n").split("\t")
            causes = causes.split(",")
            reasons.update(causes)
            if causes != ["конец записи"]:
                times.append(offsets[number])
    gaps += [b - a for a, b in zip(times, times[1:])]

wakes = sum(n for reason, n in reasons.items() if reason != "конец записи")
gaps.sort()
print(f"встреч {len(sys.argv) - 1}, звука {hours:.2f} ч, строк {lines}, слов {words}")
print(f"пробуждений {wakes} ({wakes / hours:.0f} в час): "
      + ", ".join(f"{reason} {n} ({n / hours:.0f} в час)" for reason, n in reasons.most_common() if reason != "конец записи"))
print(f"фраз с ? {questioned}, из них событием «вопрос» {reasons['вопрос']}; слово «агент» в тексте {agent}")
if gaps:
    print(f"интервал между пробуждениями по таймкодам встречи: p10 {gaps[len(gaps) // 10]:.0f} с, "
          f"p50 {gaps[len(gaps) // 2]:.0f} с, p90 {gaps[len(gaps) * 9 // 10]:.0f} с")
