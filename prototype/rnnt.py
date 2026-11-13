"""GigaAM v3 RNNT с пунктуацией по фразам VAD: жадный поиск, beam search и hotwords словаря.

Запуск: python rnnt.py <seg/имя> <выход> <greedy|beam> [словарь] [сила], фразы — из bench.py.
Словарь — термин на строку, повторы убираются; hotwords идут только с beam. Для hotwords нужен
models/<модель>/bpe.vocab — его делает spm2vocab.py из tokenizer.model GigaAM.
"""
import json
import os
import sys
import tempfile
import time
import wave

import numpy as np
import sherpa_onnx

ROOT = os.path.dirname(os.path.abspath(__file__))
MODEL = os.path.join(os.path.dirname(ROOT), "models", "sherpa-onnx-nemo-transducer-punct-giga-am-v3-russian-2025-12-16")
THREADS = int(os.environ.get("THREADS", "8"))
seg_dir, out_path, mode = sys.argv[1], sys.argv[2], sys.argv[3]
glossary = sys.argv[4] if len(sys.argv) > 4 else None
score = float(sys.argv[5]) if len(sys.argv) > 5 else 1.5

config = dict(encoder=os.path.join(MODEL, "encoder.int8.onnx"), decoder=os.path.join(MODEL, "decoder.onnx"),
              joiner=os.path.join(MODEL, "joiner.onnx"), tokens=os.path.join(MODEL, "tokens.txt"),
              num_threads=THREADS, model_type="nemo_transducer")
if mode == "beam":
    config.update(decoding_method="modified_beam_search", max_active_paths=4)
if glossary:
    if mode != "beam":
        sys.exit("hotwords — только с beam")
    terms = sorted({line.strip() for line in open(glossary, encoding="utf-8") if line.strip()})
    hotwords = tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False, encoding="utf-8")
    hotwords.write("\n".join(terms) + "\n")
    hotwords.close()
    config.update(hotwords_file=hotwords.name, hotwords_score=score, modeling_unit="bpe",
                  bpe_vocab=os.path.join(MODEL, "bpe.vocab"))

t0 = time.perf_counter()
rec = sherpa_onnx.OfflineRecognizer.from_transducer(**config)
load = time.perf_counter() - t0
if glossary:
    os.unlink(hotwords.name)  # hotwords разобраны при создании распознавателя
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
label = mode + (f" hotwords {score}" if glossary else "")
print(f"[rnnt {label}] загрузка {load:.1f} с, распознавание {total:.1f} с, к речи {total / speech:.3f}, потоков {THREADS}")
