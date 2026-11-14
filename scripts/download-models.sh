#!/usr/bin/env bash
# Модели распознавания в models/ репозитория: GigaAM v3 RNNT с пунктуацией и Silero VAD из релиза sherpa-onnx,
# Whisper tiny (75 МБ) из whisper.cpp — со сверкой SHA-256. Уже лежащие не качаются. Нужны тестам и сборке
# из исходников; приложению модели ставит install.ps1. Whisper tiny — только тесту распознавателя уточнения:
# он проверяет связку с Whisper.net, а не качество; модель уточнения (turbo) приложению ставит install.ps1.
#
# Контрольные суммы — те же, что в scripts/install.ps1 (тест скилла сверяет оба места).
#
# Запуск из любого каталога: ./scripts/download-models.sh
set -euo pipefail

release=https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models
vad=silero_vad.onnx
vad_sha256=9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6
recognizer=sherpa-onnx-nemo-transducer-punct-giga-am-v3-russian-2025-12-16
recognizer_sha256=f9620a0099019c6afcee26525ef9ed3297fa50dd5691c1902af0c948fc1a470b
# Те же файлы, что проверяет приложение (SpeechModels в Infrastructure).
recognizer_files="encoder.int8.onnx decoder.onnx joiner.onnx tokens.txt"
# Ревизия закреплена: по main файл мог бы смениться и разойтись с суммой.
whisper=https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1
whisper_tiny=ggml-tiny.bin
whisper_tiny_sha256=be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21

models="$(cd "$(dirname "$0")/.." && pwd)/models"
mkdir -p "$models"
cd "$models"

# fetch <файл> <sha256> [адрес каталога]: скачать во временный файл, сверить, только потом положить под своим именем.
fetch() {
    curl -fsSL --retry 3 -o "$1.part" "${3:-$release}/$1"
    echo "$2  $1.part" | sha256sum -c --quiet - ||
        { rm -f "$1.part"; echo "Контрольная сумма $1 не совпала: файл повреждён или подменён." >&2; exit 1; }
    mv "$1.part" "$1"
}

if [ ! -f "$vad" ]; then
    fetch "$vad" "$vad_sha256"
fi

recognizer_complete=true
for file in $recognizer_files; do
    [ -f "$recognizer/$file" ] || recognizer_complete=false
done

if [ "$recognizer_complete" = false ]; then
    fetch "$recognizer.tar.bz2" "$recognizer_sha256"
    # Распаковка — во временный каталог: оборванная посередине не оставит в models/ модель, которая выглядит целой.
    rm -rf .unpack && mkdir .unpack
    # bzip2 есть не везде (в WSL Ubuntu по умолчанию нет) — тогда распаковка модулем tarfile Python.
    if command -v bzip2 > /dev/null; then
        tar -xjf "$recognizer.tar.bz2" -C .unpack
    else
        python3 -c 'import sys, tarfile; tarfile.open(sys.argv[1]).extractall(sys.argv[2])' "$recognizer.tar.bz2" .unpack
    fi
    rm -rf "$recognizer" && mv ".unpack/$recognizer" . && rm -rf .unpack "$recognizer.tar.bz2"
fi

if [ ! -f "$whisper_tiny" ]; then
    fetch "$whisper_tiny" "$whisper_tiny_sha256" "$whisper"
fi

echo "Модели: $models"
