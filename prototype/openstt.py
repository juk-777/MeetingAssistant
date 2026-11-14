"""Выборка проверочной части OpenSTT, нарезанная Silero VAD как в приложении (`docs/measurements.md`, «Открытый набор»).

Запуск: python openstt.py <часть> [фразы <пауза, с>], часть — asr_calls_2_val или public_youtube700_val. Архив
части качается в samples/openstt/ со сверкой SHA-256 и распаковывается рядом при первом запуске.

Фразы части перемешиваются с зерном 35 и берутся по порядку, пока их длина не наберёт 3600 с. Выход:
- seg/openstt-<часть>/ — отрезки VAD потока фраз через секунду тишины, формат bench.py (его читают ctc.py, rnnt.py,
  wnet); в segments.json у отрезка — начало в потоке и номера фраз выборки, на которые он пришёлся (clip, last);
- seg/openstt-<часть>-whole/ — фраза целиком одним отрезком: контроль потерь на VAD;
- с «фразы <пауза>» вместо них — seg/openstt-<часть>-clips<пауза>/: каждая фраза в VAD отдельно, со сбросом и
  тишиной заданной длины до и после;
- везде clips.json — фразы выборки: путь в части, начало в потоке, длина, эталон.
"""
import bisect
import hashlib
import json
import os
import random
import sys
import tarfile
import urllib.request
import wave

import numpy as np
import sherpa_onnx

ROOT = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(ROOT)
RATE = 16000
URL = "https://azureopendatastorage.blob.core.windows.net/openstt/ru_open_stt_opus/archives/{}.tar.gz"
SHA256 = {
    "asr_calls_2_val": "aea64611133129e8c9b5d829953f5bfb9cc2d1bd95b9e429fa8e00e2c83f6461",
    "public_youtube700_val": "5d2131d14edd1c732f061b2b9c7d89d9b5cefb468ad0a54c6741c9de4fc936f2",
}
if len(sys.argv) not in (2, 4) or sys.argv[1] not in SHA256 or (len(sys.argv) == 4 and sys.argv[2] != "фразы"):
    sys.exit(f"Запуск: python openstt.py <{'|'.join(SHA256)}> [фразы <пауза, с>]")
part = sys.argv[1]
clip_pause = float(sys.argv[3]) if len(sys.argv) == 4 else None
base = os.path.join(REPO, "samples", "openstt")
archive_path = os.path.join(base, f"{part}.tar.gz")
if not os.path.isdir(os.path.join(base, part)):
    if not os.path.exists(archive_path):
        os.makedirs(base, exist_ok=True)
        urllib.request.urlretrieve(URL.format(part), archive_path + ".part")
        os.replace(archive_path + ".part", archive_path)
    digest = hashlib.sha256()
    with open(archive_path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    if digest.hexdigest() != SHA256[part]:
        sys.exit(f"{archive_path}: SHA-256 не совпал — удалите архив и запустите снова.")
    with tarfile.open(archive_path) as archive:
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


def cut(audio):
    """Отрезки VAD: (начало в отсчётах от начала audio, отсчёты)."""
    found = []
    for i in range(0, len(audio), window):
        vad.accept_waveform(audio[i:i + window])
        while not vad.empty():
            found.append((vad.front.start, np.array(vad.front.samples, dtype=np.float32)))
            vad.pop()
    vad.flush()
    while not vad.empty():
        found.append((vad.front.start, np.array(vad.front.samples, dtype=np.float32)))
        vad.pop()
    return found


def write(directory, k, samples):
    with wave.open(os.path.join(directory, f"{k:04d}.wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes((np.clip(samples, -1, 1) * 32767).astype(np.int16).tobytes())


seg_dir = os.path.join(ROOT, "seg", f"openstt-{part}")
outputs = [seg_dir + f"-clips{sys.argv[3]}"] if clip_pause is not None else [seg_dir, seg_dir + "-whole"]
for directory in outputs:
    if os.path.exists(directory):
        sys.exit(f"Каталог уже есть: {directory} — удалите, чтобы собрать заново.")
    os.makedirs(directory)

# Фразы набора вырезаны впритык к речи, а Silero без паузы перед речью теряет её начало. Поэтому выборка идёт
# в VAD одним потоком, как запись в приложении: фразы подряд, перед каждой и после последней — секунда тишины.
pause = np.zeros(RATE, dtype=np.float32)
clips, audios, parts, duration, position = [], [], [], 0.0, 0
for path in paths:
    if duration >= 3600:
        break
    audio = read(path)
    reference = open(os.path.join(base, path[:-4] + ".txt"), encoding="utf-8").read().strip()
    position += len(pause)
    clips.append({"path": path, "start": position / RATE, "len": len(audio) / RATE, "ref": reference})
    audios.append(audio)
    parts += [pause, audio]
    position += len(audio)
    duration += len(audio) / RATE

segments = []
if clip_pause is None:
    whole_dir = outputs[1]
    wholes = []
    for clip, audio in enumerate(audios):
        write(whole_dir, clip, audio)
        wholes.append({"i": clip, "start": clips[clip]["start"], "len": clips[clip]["len"], "clip": clip, "last": clip})
    json.dump(wholes, open(os.path.join(whole_dir, "segments.json"), "w"), ensure_ascii=False, indent=1)
    json.dump(clips, open(os.path.join(whole_dir, "clips.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    # Отрезок VAD относится к фразам, на которые приходятся его начало (clip) и конец (last): Silero не всегда
    # закрывает отрезок на секунде цифровой тишины. Начало в паузе — у следующей за паузой фразы, конец — у предыдущей.
    clip_starts = [c["start"] for c in clips]
    for start, samples in cut(np.concatenate(parts + [pause])):
        begin, end = start / RATE, (start + len(samples)) / RATE
        first = max(0, bisect.bisect_right(clip_starts, begin + len(pause) / RATE) - 1)
        segments.append((round(begin, 3), samples, first, max(0, bisect.bisect_left(clip_starts, end) - 1)))
else:
    silence = np.zeros(int(clip_pause * RATE), dtype=np.float32)
    for clip, audio in enumerate(audios):
        vad.reset()
        for start, samples in cut(np.concatenate([silence, audio, silence])):
            segments.append((round(clips[clip]["start"] + (start - len(silence)) / RATE, 3), samples, clip, clip))

meta = []
for k, (begin, samples, clip, last) in enumerate(segments):
    write(outputs[0], k, samples)
    meta.append({"i": k, "start": begin, "len": len(samples) / RATE, "clip": clip, "last": last})
json.dump(meta, open(os.path.join(outputs[0], "segments.json"), "w"), ensure_ascii=False, indent=1)
json.dump(clips, open(os.path.join(outputs[0], "clips.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
covered = {c for m in meta for c in range(m["clip"], m["last"] + 1)}
print(f"[{os.path.basename(outputs[0])}] фраз {len(clips)} из {len(paths)}, {duration:.0f} с; "
      f"отрезков VAD {len(meta)}, речи {sum(m['len'] for m in meta):.0f} с; фраз, не задетых отрезками, {len(clips) - len(covered)}, "
      f"отрезков через границу фраз {sum(m['clip'] != m['last'] for m in meta)}")
