"""Стерео для `meeting replay` из собеседования в одну дорожку и его разметки по смыслу (#11, роль интервьюера).

Запуск: python interview_stereo.py <звук собеседования.wav> — 16 кГц моно; разметка —
prototype/out/interviews-text/<имя>.tsv и `<имя>.labels` (interview_transcript.py и разметка по смыслу). Фразы
интервьюера (I) идут в левый канал («Я»), кандидата и обоих сразу (C, M) — в правый («Собеседники»), вне фраз —
тишина. Пишет `<имя>.stereo.wav` рядом со входом (samples/ вне git).
"""
import os
import sys
import wave

import numpy as np

ROOT = os.path.dirname(os.path.abspath(__file__))
LABELLED = os.path.join(ROOT, "out", "interviews-text")
RATE = 16000

if len(sys.argv) != 2:
    sys.exit("Запуск: python interview_stereo.py <звук собеседования.wav>")
path = sys.argv[1]
name = os.path.splitext(os.path.basename(path))[0]
with wave.open(path) as w:
    if w.getframerate() != RATE or w.getnchannels() != 1 or w.getsampwidth() != 2:
        sys.exit(f"Нужен {RATE} Гц моно PCM 16: {path}")
    mono = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16)

labels = {}
with open(os.path.join(LABELLED, name + ".labels"), encoding="utf-8") as f:
    for line in f:
        if line.strip():
            number, label = line.split()
            labels[int(number)] = label

stereo = np.zeros((len(mono), 2), dtype=np.int16)
with open(os.path.join(LABELLED, name + ".tsv"), encoding="utf-8") as f:
    for line in f:
        number, start, end, _ = line.split("\t", 3)
        label = labels.get(int(number))
        if label is None:
            sys.exit(f"{name}: нет метки у фразы {number}")
        a, b = int(float(start) * RATE), min(len(mono), int(float(end) * RATE))
        stereo[a:b, 0 if label == "I" else 1] = mono[a:b]

out = os.path.join(os.path.dirname(path), name + ".stereo.wav")
with wave.open(out, "wb") as w:
    w.setnchannels(2)
    w.setsampwidth(2)
    w.setframerate(RATE)
    w.writeframes(stereo.tobytes())
print(f"{name}: {len(mono) / RATE / 60:.1f} мин → {out}")
