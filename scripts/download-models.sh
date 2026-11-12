#!/usr/bin/env bash
# Модели распознавания в models/ репозитория: GigaAM v3 CTC с пунктуацией и Silero VAD из релиза sherpa-onnx,
# со сверкой SHA-256. Уже лежащие не качаются. Нужны тестам и сборке из исходников; приложению их ставит install.ps1.
#
# Контрольные суммы — те же, что в scripts/install.ps1 (тест скилла сверяет оба места).
#
# Запуск из любого каталога: ./scripts/download-models.sh
set -euo pipefail

release=https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models
vad=silero_vad.onnx
vad_sha256=9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6
recognizer=sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16
recognizer_sha256=20e41e160efa6f2460a7ef6554cdbbb9e8fffb2b92e6bc708e9406bd8b9256ea

models="$(cd "$(dirname "$0")/.." && pwd)/models"
mkdir -p "$models"
cd "$models"

# fetch <файл> <sha256>: скачать во временный файл, сверить, только потом положить под своим именем.
fetch() {
    curl -fsSL --retry 3 -o "$1.part" "$release/$1"
    echo "$2  $1.part" | sha256sum -c --quiet - ||
        { rm -f "$1.part"; echo "Контрольная сумма $1 не совпала: файл повреждён или подменён." >&2; exit 1; }
    mv "$1.part" "$1"
}

if [ ! -f "$vad" ]; then
    fetch "$vad" "$vad_sha256"
fi

if [ ! -f "$recognizer/model.int8.onnx" ] || [ ! -f "$recognizer/tokens.txt" ]; then
    fetch "$recognizer.tar.bz2" "$recognizer_sha256"
    # bzip2 есть не везде (в WSL Ubuntu по умолчанию нет) — тогда распаковка модулем tarfile Python.
    if command -v bzip2 > /dev/null; then
        tar -xjf "$recognizer.tar.bz2"
    else
        python3 -c 'import sys, tarfile; tarfile.open(sys.argv[1]).extractall(".")' "$recognizer.tar.bz2"
    fi
    rm "$recognizer.tar.bz2"
fi

echo "Модели: $models"
