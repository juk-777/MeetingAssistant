"""Транскрипт собеседования по фразам с временем — для разметки говорящих по смыслу (#11, роль интервьюера).

Запуск: python interview_transcript.py <audio.wav встречи>... -- <звук собеседования.wav>...

Фразы режет Silero VAD с параметрами приложения (тишина 0.5 с, фраза до 20 с), распознаёт GigaAM v3 CTC — как вживую.
У каждой фразы — близость к образцу голоса интервьюера (левый канал встреч `meeting start`, как в
interview_owner.py). Пишет prototype/out/interviews-text/<имя>.tsv: `номер<TAB>начало<TAB>конец<TAB>близость<TAB>текст`.
Файл с текстом живых людей — только локально: prototype/out вне git, в доки и issue идут только числа.
Разметку по смыслу кладут рядом: `<имя>.labels` — строка `номер<TAB>I|C|M` (интервьюер, кандидат, обе стороны в одной
фразе); паузы по ней считает interview_truth.py.
"""
import os
import sys
import wave

import numpy as np
import sherpa_onnx

ROOT = os.path.dirname(os.path.abspath(__file__))
MODELS = os.path.join(os.path.dirname(ROOT), "models")
ASR = os.path.join(MODELS, "sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16")
EMBEDDING = os.path.join(MODELS, "3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx")
VAD = os.path.join(MODELS, "silero_vad.onnx")
OUT = os.path.join(ROOT, "out", "interviews-text")
RATE = 16000

if "--" not in sys.argv[1:]:
    sys.exit("Запуск: python interview_transcript.py <audio.wav встречи>... -- <звук собеседования.wav>...")
split = sys.argv.index("--")
references, interviews = sys.argv[1:split], sys.argv[split + 1:]
for model in (os.path.join(ASR, "model.int8.onnx"), EMBEDDING, VAD):
    if not os.path.isfile(model):
        sys.exit(f"Нет модели: {model}")

recognizer = sherpa_onnx.OfflineRecognizer.from_nemo_ctc(
    model=os.path.join(ASR, "model.int8.onnx"), tokens=os.path.join(ASR, "tokens.txt"), num_threads=4)
extractor = sherpa_onnx.SpeakerEmbeddingExtractor(
    sherpa_onnx.SpeakerEmbeddingExtractorConfig(model=EMBEDDING, num_threads=4))


def read(path):
    with wave.open(path) as w:
        if w.getframerate() != RATE or w.getsampwidth() != 2:
            sys.exit(f"Нужен {RATE} Гц PCM 16: {path}")
        channels = w.getnchannels()
        audio = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768
    return audio.reshape(-1, channels).T


def embed(audio):
    stream = extractor.create_stream()
    stream.accept_waveform(RATE, audio)
    stream.input_finished()
    v = np.array(extractor.compute(stream), dtype=np.float32)
    return v / np.linalg.norm(v)


def phrases(audio):
    """(начало, конец, звук) фраз Silero VAD с параметрами приложения."""
    config = sherpa_onnx.VadModelConfig()
    config.silero_vad.model = VAD
    config.silero_vad.threshold = 0.5
    config.silero_vad.min_silence_duration = 0.5
    config.silero_vad.min_speech_duration = 0.25
    config.silero_vad.max_speech_duration = 20
    config.sample_rate = RATE
    vad = sherpa_onnx.VoiceActivityDetector(config, buffer_size_in_seconds=120)
    window = config.silero_vad.window_size
    result = []
    for i in range(0, len(audio) // window * window, window):
        vad.accept_waveform(audio[i:i + window])
        while not vad.empty():
            samples = np.array(vad.front.samples, dtype=np.float32)
            start = vad.front.start / RATE
            result.append((start, start + len(samples) / RATE, samples))
            vad.pop()
    return result


mine = []
for path in references:
    channels = read(path)
    mine.extend(embed(s) for _, _, s in phrases(channels[0]) if len(s) >= RATE)
if not mine:
    sys.exit("В левом канале встреч нет речи для образца голоса")
reference = np.mean(mine, axis=0)
reference /= np.linalg.norm(reference)

os.makedirs(OUT, exist_ok=True)
for path in interviews:
    name = os.path.splitext(os.path.basename(path))[0]
    audio = read(path)[0]
    with open(os.path.join(OUT, name + ".tsv"), "w", encoding="utf-8") as f:
        for number, (start, end, samples) in enumerate(phrases(audio), 1):
            stream = recognizer.create_stream()
            stream.accept_waveform(RATE, samples)
            recognizer.decode_stream(stream)
            text = stream.result.text.strip()
            score = float(embed(samples) @ reference) if len(samples) >= RATE // 2 else float("nan")
            f.write(f"{number}\t{start:.2f}\t{end:.2f}\t{score:.2f}\t{text}\n")
    print(f"{name}: {number} фраз", flush=True)
