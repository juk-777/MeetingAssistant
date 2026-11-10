"""Сравнение распознавания на записи созвона: GigaAM v3 CTC по фразам VAD и T-one потоком.

Запуск: python bench.py <имя>, вход — samples/<имя>.wav (16 кГц моно), модели — models/.
Пишет фразы VAD в prototype/seg/ (для прогона Whisper.net) и расшифровки в prototype/out/.
T-one пропускается, если его модели нет в models/.
"""
import json
import os
import sys
import time
import wave

import numpy as np
import sherpa_onnx

ROOT = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(ROOT)
MODELS = os.path.join(REPO, "models")
THREADS = int(os.environ.get("THREADS", "2"))
name = sys.argv[1]
src = os.path.join(REPO, "samples", f"{name}.wav")

with wave.open(src) as w:
    sr = w.getframerate()
    audio = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768
dur = len(audio) / sr
os.makedirs(os.path.join(ROOT, "out"), exist_ok=True)
segdir = os.path.join(ROOT, "seg", name)
os.makedirs(segdir, exist_ok=True)

# 1. Фразы по паузам
vc = sherpa_onnx.VadModelConfig()
vc.silero_vad.model = os.path.join(MODELS, "silero_vad.onnx")
vc.silero_vad.threshold = 0.5
vc.silero_vad.min_silence_duration = 0.5
vc.silero_vad.min_speech_duration = 0.25
vc.silero_vad.max_speech_duration = 20
vc.sample_rate = sr
vad = sherpa_onnx.VoiceActivityDetector(vc, buffer_size_in_seconds=120)
win = vc.silero_vad.window_size
segs = []
t0 = time.perf_counter()
for i in range(0, len(audio), win):
    vad.accept_waveform(audio[i:i + win])
    while not vad.empty():
        segs.append((vad.front.start, np.array(vad.front.samples, dtype=np.float32)))
        vad.pop()
vad.flush()
while not vad.empty():
    segs.append((vad.front.start, np.array(vad.front.samples, dtype=np.float32)))
    vad.pop()
vad_time = time.perf_counter() - t0
meta = []
for k, (start, s) in enumerate(segs):
    p = os.path.join(segdir, f"{k:04d}.wav")
    with wave.open(p, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(sr)
        w.writeframes((np.clip(s, -1, 1) * 32767).astype(np.int16).tobytes())
    meta.append({"i": k, "start": start / sr, "len": len(s) / sr})
json.dump(meta, open(os.path.join(segdir, "segments.json"), "w"), ensure_ascii=False, indent=1)
lens = np.array([m["len"] for m in meta])
print(f"[{name}] длительность {dur:.1f} с, VAD {vad_time:.1f} с, фраз {len(segs)}, речи {lens.sum():.1f} с, "
      f"фраза p50 {np.median(lens):.1f} p90 {np.quantile(lens, .9):.1f} max {lens.max():.1f} с")

# 2. GigaAM v3 CTC с пунктуацией по фразам
g = os.path.join(MODELS, "sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16")
t0 = time.perf_counter()
rec = sherpa_onnx.OfflineRecognizer.from_nemo_ctc(model=os.path.join(g, "model.int8.onnx"),
                                                  tokens=os.path.join(g, "tokens.txt"), num_threads=THREADS)
load = time.perf_counter() - t0
lines, per = [], []
t0 = time.perf_counter()
for m, (start, s) in zip(meta, segs):
    a = time.perf_counter()
    st = rec.create_stream()
    st.accept_waveform(sr, s)
    rec.decode_stream(st)
    per.append(time.perf_counter() - a)
    lines.append(f"[{m['start']:7.1f}] {st.result.text}")
total = time.perf_counter() - t0
per = np.array(per)
print(f"[gigaam-v3-ctc-punct] загрузка {load:.1f} с, распознавание {total:.1f} с, RTF {total / dur:.3f}, "
      f"на фразу p50 {np.median(per):.2f} p90 {np.quantile(per, .9):.2f} max {per.max():.2f} с, потоков {THREADS}")
open(os.path.join(ROOT, "out", f"{name}.gigaam.txt"), "w").write("\n".join(lines) + "\n")

# 3. T-one потоком: куски по 100 мс, как с микрофона
t = os.path.join(MODELS, "sherpa-onnx-streaming-t-one-russian-2025-09-08")
if not os.path.isdir(t):
    sys.exit(0)
t0 = time.perf_counter()
orec = sherpa_onnx.OnlineRecognizer.from_t_one_ctc(tokens=os.path.join(t, "tokens.txt"), model=os.path.join(t, "model.onnx"),
                                                   num_threads=THREADS, enable_endpoint_detection=True,
                                                   rule1_min_trailing_silence=2.4, rule2_min_trailing_silence=0.8)
load = time.perf_counter() - t0
st = orec.create_stream()
step = sr // 10
out, chunk_times, utt_start = [], [], 0.0
t0 = time.perf_counter()
for i in range(0, len(audio), step):
    a = time.perf_counter()
    st.accept_waveform(sr, audio[i:i + step])
    while orec.is_ready(st):
        orec.decode_stream(st)
    chunk_times.append(time.perf_counter() - a)
    if orec.is_endpoint(st):
        text = orec.get_result(st).strip()
        if text:
            out.append(f"[{utt_start:7.1f}] {text}")
        orec.reset(st)
        utt_start = (i + step) / sr
st.input_finished()
while orec.is_ready(st):
    orec.decode_stream(st)
text = orec.get_result(st).strip()
if text:
    out.append(f"[{utt_start:7.1f}] {text}")
total = time.perf_counter() - t0
ct = np.array(chunk_times)
print(f"[t-one stream] загрузка {load:.1f} с, обработка {total:.1f} с, RTF {total / dur:.3f}, "
      f"кусок 100 мс: p50 {np.median(ct) * 1000:.0f} p99 {np.quantile(ct, .99) * 1000:.0f} max {ct.max() * 1000:.0f} мс, потоков {THREADS}")
open(os.path.join(ROOT, "out", f"{name}.tone.txt"), "w").write("\n".join(out) + "\n")
