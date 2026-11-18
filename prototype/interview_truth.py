"""Паузы «конец ответа кандидата» по разметке говорящих по смыслу (#11, роль интервьюера).

Запуск: python interview_truth.py <имя>... — prototype/out/interviews-text/<имя>.tsv (interview_transcript.py) и
`<имя>.labels` рядом (`номер<TAB>I|C|M`). Печатает только числа.

Фразы — как у приложения (Silero VAD, тишина 0.5 с), поэтому паузы между фразами — те, что видит приложение. Ответ —
подряд идущие фразы кандидата; фраза интервьюера короче BACKCHANNEL секунд («угу») его не прерывает, фраза обоих (M)
— прерывает, пауза до интервьюера в ней считается нулевой: смена говорящего быстрее тишины VAD. Ответ считается от ANSWER_MIN секунд. Для порога тишины T:
- лишних срабатываний на ответ: пауз внутри ответа длиннее T;
- доля концов ответа, пойманных тишиной до речи интервьюера;
- задержка подсказки по правилу «тишина T или речь интервьюера дольше BACKCHANNEL, что раньше» от конца ответа.
Сверка с разметкой по голосу: доля фраз, где близость к образцу (порог VOICE) даёт ту же сторону.
"""
import math
import os
import sys

import numpy as np

ROOT = os.path.dirname(os.path.abspath(__file__))
DIRECTORY = os.path.join(ROOT, "out", "interviews-text")
BACKCHANNEL = 1.0
ANSWER_MIN = 10.0
VOICE = 0.38
THRESHOLDS = (1.0, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0)

if len(sys.argv) < 2:
    sys.exit("Запуск: python interview_truth.py <имя>...")

pauses, end_gaps, answers = [], [], []
voice_same = voice_total = 0
inside = 0
hours = 0.0
for name in sys.argv[1:]:
    rows = {}
    with open(os.path.join(DIRECTORY, name + ".tsv"), encoding="utf-8") as f:
        for line in f:
            number, start, end, score, _ = line.rstrip("\n").split("\t", 4)
            rows[int(number)] = [float(start), float(end), float(score), None]
    with open(os.path.join(DIRECTORY, name + ".labels"), encoding="utf-8") as f:
        for line in f:
            if line.strip():
                number, label = line.split()
                rows[int(number)][3] = label
    missing = [n for n, r in rows.items() if r[3] not in ("I", "C", "M")]
    if missing:
        sys.exit(f"{name}: нет метки у {len(missing)} фраз, первая — {missing[0]}")
    phrases = [rows[n] for n in sorted(rows)]
    hours += phrases[-1][1] / 3600
    for start, end, score, label in phrases:
        if label != "M" and not math.isnan(score):
            voice_total += 1
            voice_same += (score >= VOICE) == (label == "I")

    answer_start = None
    answer_pauses = []
    last_end = None
    for start, end, _, label in phrases:
        if label == "C":
            if answer_start is None:
                answer_start = start
                answer_pauses = []
            elif last_end is not None:
                answer_pauses.append(start - last_end)
            last_end = end
            continue
        if label == "I" and end - start < BACKCHANNEL:
            continue
        if answer_start is not None:
            if last_end - answer_start >= ANSWER_MIN:
                answers.append(last_end - answer_start)
                pauses.extend(answer_pauses)
                # Фраза обоих: смена говорящего внутри неё, пауза короче тишины VAD — считается нулевой.
                end_gaps.append(0.0 if label == "M" else start - last_end)
                inside += label == "M"
            answer_start = None

pauses, end_gaps, answers = np.array(pauses), np.array(end_gaps), np.array(answers)
print(f"записей {len(sys.argv) - 1}, часов {hours:.1f}, ответов от {ANSWER_MIN:.0f} с: {len(answers)}, "
      f"длина медиана {np.median(answers):.0f} с, p90 {np.quantile(answers, 0.9):.0f} с")
print(f"сверка с разметкой по голосу (порог {VOICE}): совпало {voice_same / voice_total:.2f} из {voice_total} фраз")
print(f"паузы внутри ответов {len(pauses)}: медиана {np.median(pauses):.2f} с, p90 {np.quantile(pauses, 0.9):.2f} с, "
      f"p99 {np.quantile(pauses, 0.99):.2f} с")
print(f"концов ответа внутри фразы обоих (пауза < 0.5 с): {inside} из {len(end_gaps)}")
print(f"от конца ответа до речи интервьюера: медиана {np.median(end_gaps):.2f} с, p10 {np.quantile(end_gaps, 0.1):.2f} с, "
      f"p90 {np.quantile(end_gaps, 0.9):.2f} с")
print("\nпорог T: лишних на ответ | пойманных тишиной до речи интервьюера | задержка «тишина T или речь интервьюера», "
      "медиана / p90")
for t in THRESHOLDS:
    delay = np.minimum(t, end_gaps + BACKCHANNEL)
    print(f"  {t:.1f} с: {(pauses > t).sum() / len(answers):.2f} | {(end_gaps > t).mean():.2f} | "
          f"{np.median(delay):.1f} / {np.quantile(delay, 0.9):.1f} с")
