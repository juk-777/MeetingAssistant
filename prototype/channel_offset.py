"""Смещение каналов audio.wav встречи по взаимной корреляции: насколько «Я» (L) отстаёт от «Собеседников» (R).

Запуск: python channel_offset.py <audio.wav> [окно, с] [шаг, с]
Звук из динамиков без наушников микрофон слышит с задержкой распространения: в окнах, где корреляция высокая,
смещение — это эхо плюс расхождение каналов. Его изменение от начала к концу записи — расхождение часов устройств.
Печатает на окно: начало, смещение в мс (плюс — L позже R), пик нормированной корреляции; окна, где тихо
в L или в R, пропускаются.
"""
import sys
import wave

import numpy as np

MAX_LAG_S = 0.5
MIN_RMS = 1e-3

path = sys.argv[1]
window_s = float(sys.argv[2]) if len(sys.argv) > 2 else 20.0
step_s = float(sys.argv[3]) if len(sys.argv) > 3 else 60.0

with wave.open(path) as w:
    assert w.getnchannels() == 2 and w.getsampwidth() == 2, "нужен стерео PCM 16"
    sr = w.getframerate()
    pcm = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).reshape(-1, 2).astype(np.float64) / 32768

left, right = pcm[:, 0], pcm[:, 1]
window, step, max_lag = int(window_s * sr), int(step_s * sr), int(MAX_LAG_S * sr)
print(f"{path}: {len(pcm) / sr:.1f} с, окно {window_s:g} с, шаг {step_s:g} с")

for start in range(0, len(pcm) - window + 1, step):
    l = left[start:start + window] - left[start:start + window].mean()
    r = right[start:start + window] - right[start:start + window].mean()
    if np.sqrt((r ** 2).mean()) < MIN_RMS or np.sqrt((l ** 2).mean()) < MIN_RMS:
        print(f"{start / sr:8.1f} с  тихо")
        continue
    n = 1 << (2 * window - 1).bit_length()
    corr = np.fft.irfft(np.fft.rfft(l, n) * np.conj(np.fft.rfft(r, n)), n)
    lags = np.concatenate([np.arange(0, max_lag + 1), np.arange(-max_lag, 0)])
    values = np.concatenate([corr[:max_lag + 1], corr[-max_lag:]])
    best = int(np.argmax(np.abs(values)))
    peak = abs(values[best]) / np.sqrt((l ** 2).sum() * (r ** 2).sum())
    print(f"{start / sr:8.1f} с  {lags[best] * 1000 / sr:+8.2f} мс  пик {peak:.2f}")
