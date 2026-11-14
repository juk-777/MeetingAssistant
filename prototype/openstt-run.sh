#!/usr/bin/env bash
# Замер на открытом наборе (docs/measurements.md, «Открытый набор») одной командой для части OpenSTT:
# выборка (openstt.py), GigaAM CTC и RNNT beam по отрезкам VAD на 2 потоках, контроль CTC по фразе целиком и по
# фразам отдельно (без паузы и с секундой тишины), turbo кусками до 30 с на половине логических ядер, WER/CER.
# Готовые выборки и выводы не пересчитываются — для повтора с нуля удалить seg/openstt-<часть>* и out/openstt/<часть>.*
#
# openstt-run.sh <asr_calls_2_val|public_youtube700_val>
set -euo pipefail
cd "$(dirname "$0")"
part=$1
py=".venv/bin/python -B"
seg=seg/openstt-$part
out=out/openstt/$part
mkdir -p out/openstt

[ -d "$seg" ] || $py openstt.py "$part"
for pause in 0 1; do
    [ -d "$seg-clips$pause" ] || $py openstt.py "$part" фразы "$pause"
done
run() { [ -s "$1" ] || "${@:2}"; }
run "$out.ctc.txt" env THREADS=2 $py ctc.py "$seg" "$out.ctc.txt"
run "$out.ctc-whole.txt" env THREADS=2 $py ctc.py "$seg-whole" "$out.ctc-whole.txt"
for pause in 0 1; do
    run "$out.ctc-clips$pause.txt" env THREADS=2 $py ctc.py "$seg-clips$pause" "$out.ctc-clips$pause.txt"
done
run "$out.rnnt-beam.txt" env THREADS=2 $py rnnt.py "$seg" "$out.rnnt-beam.txt" beam
run "$out.turbo.txt" env -C wnet GLUE=1 dotnet run -c Release -- "../$seg" ../../models/ggml-large-v3-turbo-q5_0.bin \
    "../$out.turbo.txt" $(($(nproc) / 2))

$py wer.py "$seg" "$out.ctc.txt" "$out.rnnt-beam.txt" "$out.turbo.txt"
$py wer.py "$seg-whole" "$out.ctc-whole.txt" | tail -n 1
for pause in 0 1; do
    $py wer.py "$seg-clips$pause" "$out.ctc-clips$pause.txt" | tail -n 1
done
$py wer.py --pair "$seg" "$out.ctc.txt" "$out.rnnt-beam.txt"
