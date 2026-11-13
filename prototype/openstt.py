"""Выборка проверочной части OpenSTT, нарезанная Silero VAD как в приложении (`docs/measurements.md`, «Открытый набор»).

Запуск: python openstt.py <часть>, например asr_calls_2_val. Архив <часть>.tar.gz лежит в samples/openstt/
(ссылка — prototype/README.md), распаковывается рядом при первом запуске.

Фразы части перемешиваются с зерном 35 и берутся по порядку, пока их длина не наберёт 3600 с. Выход:
- seg/openstt-<часть>/ — отрезки VAD потока фраз через секунду тишины, формат bench.py (его читают ctc.py, rnnt.py,
  wnet); в segments.json у отрезка — начало в потоке и номера фраз выборки, на которые он пришёлся (clip, last);
- seg/openstt-<часть>-whole/ — фраза целиком одним отрезком: контроль потерь на VAD;
- в обоих clips.json — фразы выборки: путь в части, длина, эталон.
"""
import bisect
import json
import os
import random
import sys
import tarfile
import wave

import numpy as np
import sherpa_onnx

ROOT = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(ROOT)
RATE = 16000
part = sys.argv[1]
base = os.path.join(REPO, "samples", "openstt")
if not os.path.isdir(os.path.join(base, part)):
    with tarfile.open(os.path.join(base, f"{part}.tar.gz")) as archive:
        archive.extractall(base, filter="data")

paths = sorted(os.path.relpath(os.path.join(d, f), base)
               for d, _, files in os.walk(os.path.join(base, part)) for f in files if f.endswith(".wav"))
random.Random(35).shuffle(paths)


def read(path):
    with wave.open(os.path.join(base, path)) as w:
        if (w.getframerate(), w.getnchannels(), w.getsampwidth()) != (RATE, 1, 2):
            sys.exit(f"{path}: ждали 16 кГц моно PCM 16")
        return np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768


# Параметры — как у SileroVoiceActivityDetector приложения.
config = sherpa_onnx.VadModelConfig()
config.silero_vad.model = os.path.join(REPO, "models", "silero_vad.onnx")
config.silero_vad.threshold = 0.5
config.silero_vad.min_silence_duration = 0.5
config.silero_vad.min_speech_duration = 0.25
config.silero_vad.max_speech_duration = 20
config.sample_rate = RATE
vad = sherpa_onnx.VoiceActivityDetector(config, buffer_size_in_seconds=120)
window = config.silero_vad.window_size


def write(directory, k, samples):
    with wave.open(os.path.join(directory, f"{k:04d}.wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes((np.clip(samples, -1, 1) * 32767).astype(np.int16).tobytes())


seg_dir = os.path.join(ROOT, "seg", f"openstt-{part}")
whole_dir = seg_dir + "-whole"
for directory in (seg_dir, whole_dir):
    if os.path.exists(directory):
        sys.exit(f"Каталог уже есть: {directory} — удалите, чтобы собрать заново.")
    os.makedirs(directory)

# Фразы набора вырезаны впритык к речи, а Silero без паузы перед речью теряет её начало. Поэтому выборка идёт
# в VAD одним потоком, как запись в приложении: фразы подряд, перед каждой и после последней — секунда тишины.
pause = np.zeros(RATE, dtype=np.float32)
clips, wholes, parts, duration, position = [], [], [], 0.0, 0
for path in paths:
    if duration >= 3600:
        break
    audio = read(path)
    reference = open(os.path.join(base, path[:-4] + ".txt"), encoding="utf-8").read().strip()
    clip = len(clips)
    position += len(pause)
    clips.append({"path": path, "start": position / RATE, "len": len(audio) / RATE, "ref": reference})
    write(whole_dir, clip, audio)
    wholes.append({"i": clip, "start": position / RATE, "len": len(audio) / RATE, "clip": clip, "last": clip})
    parts += [pause, audio]
    position += len(audio)
    duration += len(audio) / RATE
stream = np.concatenate(parts + [pause])

found = []
for i in range(0, len(stream), window):
    vad.accept_waveform(stream[i:i + window])
    while not vad.empty():
        found.append((vad.front.start, np.array(vad.front.samples, dtype=np.float32)))
        vad.pop()
vad.flush()
while not vad.empty():
    found.append((vad.front.start, np.array(vad.front.samples, dtype=np.float32)))
    vad.pop()

# Отрезок VAD относится к фразам, на которые приходятся его начало (clip) и конец (last): Silero не всегда закрывает
# отрезок на секунде цифровой тишины. Начало в паузе — у следующей за паузой фразы, конец в паузе — у предыдущей.
clip_starts = [c["start"] for c in clips]
segments = []
for k, (start, samples) in enumerate(found):
    begin, end = start / RATE, (start + len(samples)) / RATE
    write(seg_dir, k, samples)
    segments.append({"i": k, "start": round(begin, 3), "len": len(samples) / RATE,
                     "clip": max(0, bisect.bisect_right(clip_starts, begin + len(pause) / RATE) - 1),
                     "last": max(0, bisect.bisect_left(clip_starts, end) - 1)})
spanning = sum(s["clip"] != s["last"] for s in segments)

for directory, meta in ((seg_dir, segments), (whole_dir, wholes)):
    json.dump(meta, open(os.path.join(directory, "segments.json"), "w"), ensure_ascii=False, indent=1)
    json.dump(clips, open(os.path.join(directory, "clips.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
speech = sum(s["len"] for s in segments)
print(f"[{part}] фраз {len(clips)} из {len(paths)}, {duration:.0f} с; отрезков VAD {len(segments)}, речи {speech:.0f} с; "
      f"фраз без отрезков {len(clips) - len({s['clip'] for s in segments})}, отрезков через границу фраз {spanning}")
