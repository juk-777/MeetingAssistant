"""Имитация приложения записи: запись подаётся в реальном темпе, фразы распознаются GigaAM,
транскрипт дописывается в transcript.md, поводы разбудить сессию — в events.log.

Строка транскрипта: <номер>\t<epoch конца фразы>\t[мм:сс] текст
Строка события:     <epoch>\t<номер строки транскрипта>\t<повод>
"""
import os
import re
import sys
import time
import wave

import numpy as np
import sherpa_onnx

HERE = os.path.dirname(os.path.abspath(__file__))
MODELS = os.path.join(os.path.dirname(HERE), "models")
LIVE = os.path.join(HERE, "live")
os.makedirs(LIVE, exist_ok=True)
src = os.path.join(os.path.dirname(HERE), "samples", f"{sys.argv[1]}.wav")
speed = float(os.environ.get("SPEED", "1"))

# Словарь встречи: файл с регуляркой-термином на строку (GLOSSARY_FILE); без файла повод «термин» не срабатывает
_terms = [t.strip() for t in open(os.environ["GLOSSARY_FILE"], encoding="utf-8")] if "GLOSSARY_FILE" in os.environ else []
GLOSSARY = re.compile("|".join(t for t in _terms if t), re.IGNORECASE) if any(_terms) else re.compile(r"(?!)")
WAKE = re.compile(r"\bклод\b|\bагент\b", re.IGNORECASE)
FALLBACK_SPEECH = 60.0

with wave.open(src) as w:
    sr = w.getframerate()
    audio = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768

vc = sherpa_onnx.VadModelConfig()
vc.silero_vad.model = os.path.join(MODELS, "silero_vad.onnx")
vc.silero_vad.min_silence_duration = 0.5
vc.silero_vad.min_speech_duration = 0.25
vc.silero_vad.max_speech_duration = 20
vc.sample_rate = sr
vad = sherpa_onnx.VoiceActivityDetector(vc, buffer_size_in_seconds=120)
g = os.path.join(MODELS, "sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16")
rec = sherpa_onnx.OfflineRecognizer.from_nemo_ctc(model=os.path.join(g, "model.int8.onnx"),
                                                  tokens=os.path.join(g, "tokens.txt"), num_threads=2)

tr = open(os.path.join(LIVE, "transcript.md"), "w", buffering=1)
ev = open(os.path.join(LIVE, "events.log"), "w", buffering=1)
line_no = 0
since_event = 0.0
started = time.time()


def emit_phrase(start, samples):
    global line_no, since_event
    st = rec.create_stream()
    st.accept_waveform(sr, samples)
    rec.decode_stream(st)
    text = st.result.text.strip()
    if not text:
        return
    line_no += 1
    now = time.time()
    t = start / sr
    tr.write(f"{line_no}\t{now:.3f}\t[{int(t // 60):02d}:{int(t % 60):02d}] {text}\n")
    since_event += len(samples) / sr
    reasons = []
    if WAKE.search(text):
        reasons.append("обращение")
    if "?" in text:
        reasons.append("вопрос")
    if GLOSSARY.search(text):
        reasons.append("термин")
    if not reasons and since_event >= FALLBACK_SPEECH:
        reasons.append("накопилось")
    if reasons:
        ev.write(f"{now:.3f}\t{line_no}\t{','.join(reasons)}\n")
        since_event = 0.0


win = vc.silero_vad.window_size
for i in range(0, len(audio), win):
    vad.accept_waveform(audio[i:i + win])
    while not vad.empty():
        emit_phrase(vad.front.start, np.array(vad.front.samples, dtype=np.float32))
        vad.pop()
    lag = started + (i + win) / sr / speed - time.time()
    if lag > 0:
        time.sleep(lag)
vad.flush()
while not vad.empty():
    emit_phrase(vad.front.start, np.array(vad.front.samples, dtype=np.float32))
    vad.pop()
ev.write(f"{time.time():.3f}\t{line_no}\tконец записи\n")
