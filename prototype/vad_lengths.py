"""Длины фраз Silero VAD с параметрами приложения (`docs/measurements.md`, «Длина фраз VAD»).

Запуск: python vad_lengths.py <max_speech_duration, с> <звук.wav>... — 16 кГц моно, модель — models/silero_vad.onnx.
Звук подаётся окнами Silero, как в `SileroVoiceActivityDetector`; остальные параметры — его константы. Печатает
только счётчики по всем файлам вместе: число фраз, перцентили длины, долю фраз и речи во фразах длиннее порогов.
Ожидание текста фразы — от её начала до конца: столько слова в начале длинной фразы ждут распознавания.
"""
import os
import sys
import wave

import numpy as np
import sherpa_onnx

ROOT = os.path.dirname(os.path.abspath(__file__))
MODEL = os.path.join(os.path.dirname(ROOT), "models", "silero_vad.onnx")

if len(sys.argv) < 3:
    sys.exit("Запуск: python vad_lengths.py <max_speech_duration, с> <звук.wav>...")
max_speech = float(sys.argv[1])
lengths = []
hours = 0.0
for path in sys.argv[2:]:
    with wave.open(path) as w:
        sr = w.getframerate()
        if sr != 16000 or w.getnchannels() != 1:
            sys.exit(f"Нужен 16 кГц моно: {path}")
        audio = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768
    hours += len(audio) / sr / 3600
    vc = sherpa_onnx.VadModelConfig()
    vc.silero_vad.model = MODEL
    vc.silero_vad.threshold = 0.5
    vc.silero_vad.min_silence_duration = 0.5
    vc.silero_vad.min_speech_duration = 0.25
    vc.silero_vad.max_speech_duration = max_speech
    vc.sample_rate = sr
    vad = sherpa_onnx.VoiceActivityDetector(vc, buffer_size_in_seconds=120)
    win = vc.silero_vad.window_size
    full = len(audio) // win * win
    for i in range(0, full, win):
        vad.accept_waveform(audio[i:i + win])
        while not vad.empty():
            lengths.append(len(vad.front.samples) / sr)
            vad.pop()
    vad.flush()
    while not vad.empty():
        lengths.append(len(vad.front.samples) / sr)
        vad.pop()

values = np.array(lengths)
speech = values.sum()
print(f"max_speech_duration {max_speech:g} с; файлов {len(sys.argv) - 2}, звука {hours:.2f} ч, "
      f"речи во фразах {speech / 3600:.2f} ч, фраз {len(values)}")
print("длина фразы: p50 {:.1f}, p90 {:.1f}, p99 {:.1f}, max {:.1f} с".format(
    *np.percentile(values, [50, 90, 99]), values.max()))
for limit in (10, 20, 30):
    longer = values[values > limit]
    print(f"длиннее {limit} с: фраз {len(longer)} ({len(longer) / len(values):.1%}), "
          f"речи в них {longer.sum() / speech:.1%}")
