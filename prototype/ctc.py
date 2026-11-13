"""GigaAM v3 CTC с пунктуацией (модель приложения) по отрезкам каталога seg.

Запуск: python ctc.py <seg/имя> <выход>; отрезки — от bench.py или openstt.py. Потоки — `THREADS` (2, как вживую).
"""
import json
import os
import sys
import time
import wave

import numpy as np
import sherpa_onnx

ROOT = os.path.dirname(os.path.abspath(__file__))
MODEL = os.path.join(os.path.dirname(ROOT), "models", "sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16")
THREADS = int(os.environ.get("THREADS", "2"))
seg_dir, out_path = sys.argv[1], sys.argv[2]

t0 = time.perf_counter()
rec = sherpa_onnx.OfflineRecognizer.from_nemo_ctc(model=os.path.join(MODEL, "model.int8.onnx"),
                                                  tokens=os.path.join(MODEL, "tokens.txt"), num_threads=THREADS)
load = time.perf_counter() - t0
meta = json.load(open(os.path.join(seg_dir, "segments.json")))
lines, speech, total = [], 0.0, 0.0
for m in meta:
    with wave.open(os.path.join(seg_dir, f"{m['i']:04d}.wav")) as w:
        audio = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768
    speech += m["len"]
    t0 = time.perf_counter()
    stream = rec.create_stream()
    stream.accept_waveform(16000, audio)
    rec.decode_stream(stream)
    total += time.perf_counter() - t0
    lines.append(f"[{m['start']:7.1f}] {stream.result.text}")
open(out_path, "w", encoding="utf-8").write("\n".join(lines) + "\n")
print(f"[ctc] загрузка {load:.1f} с, распознавание {total:.1f} с, к речи {total / speech:.3f}, потоков {THREADS}")
