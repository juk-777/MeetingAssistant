#!/usr/bin/env bash
# Задержка подсказки кандидату целиком (docs/specs/2026-11-16-interview-mode.md): реплика интервьюера дописывается в
# транскрипт встречи, `meeting-candidate` скилла отвечает настоящей моделью. По каждой реплике — время от записи строки
# до первого символа ответа в окне и до конца ответа. Звука и распознавания здесь нет: вживую к задержке прибавляются
# 0.5 с тишины, по которой VAD закрывает фразу, и распознавание фразы.
#
# candidate_latency.sh <модель> <сценарий> — сценарий: реплика интервьюера на строку (синтетика, не из встречи).
# Следующая реплика пишется, когда ответ на прошлую дописан. Модель — в mode.json встречи; effort у быстрого пути low.
set -u
# Посимвольное чтение — по символам UTF-8, а не по байтам: иначе «▶» не узнать.
export LC_ALL=C.UTF-8

model=$1
scenario=$2
repo=$(cd "$(dirname "$0")/.." && pwd)
scripts="$repo/plugins/meeting/skills/meeting/scripts"
work=$(mktemp -d)
candidate=""
# Выход стенда гасит и окно: каталог встречи удаляется, конца записи окно не увидит и само не выйдет.
trap 'kill "$candidate" 2> /dev/null; rm -rf "$work"' EXIT
trap 'exit 130' INT TERM HUP
meeting="$work/meeting"
mkdir -p "$meeting"
: > "$meeting/transcript.md"
: > "$meeting/events.log"
printf '{"mode": "candidate", "context": "собеседование по бэкенду (.NET), системный дизайн", "model": "%s"}\n' "$model" \
    > "$meeting/mode.json"

# window_failed <причина>: отказ стенда с выводом ошибок окна — каталог работы ловушка выхода удалит.
window_failed() {
    echo "$1. Ошибки окна:" >&2
    cat "$work/candidate.err" >&2
    exit 1
}

# Вывод окна с отметкой времени каждого символа: «▶» открывает ответ, пустая строка после текста — его конец. Разметка —
# подстановкой процесса, а не конвейером: так $! — само окно, и его гасит ловушка выхода.
bash "$scripts/meeting-candidate" "$meeting" 2> "$work/candidate.err" > >(
    while IFS= read -r -d '' -N 1 char; do
        [ "$char" != $'\n' ] || char='\n'
        printf '%s\t%s\n' "${EPOCHREALTIME/,/.}" "$char"
    done > "$work/out.tsv") &
candidate=$!
# Строки, дописанные до приветствия окна, оно считает прошлыми и не разбирает: первая реплика ждёт конца его строки.
deadline=$((SECONDS + 30))
until grep -q $'\t\\\\n$' "$work/out.tsv" 2> /dev/null; do
    kill -0 "$candidate" 2> /dev/null || window_failed "Окно вышло до приветствия"
    [ $SECONDS -lt $deadline ] || window_failed "Окно не открылось за 30 с"
    sleep 0.1
done

echo "$(claude --version), модель $model, effort low"
number=0
while IFS= read -r question; do
    [ -n "$question" ] || continue
    number=$((number + 1))
    lines_before=$(wc -l < "$work/out.tsv")
    written=${EPOCHREALTIME/,/.}
    printf '%s\t%s\t[00:%02d:00] Собеседники: %s\n' "$number" "$written" "$number" "$question" >> "$meeting/transcript.md"
    # Ответ дописан, когда в журнале встречи появился его блок.
    deadline=$((SECONDS + 120))
    until [ "$(grep -c '^## ' "$meeting/candidate.md" 2> /dev/null || echo 0)" -ge "$number" ]; do
        [ $SECONDS -lt $deadline ] || window_failed "Реплика $number: ответа нет за 120 с"
        sleep 0.2
    done
    tail -n +"$((lines_before + 1))" "$work/out.tsv" | awk -F'\t' -v written="$written" -v n="$number" '
        seen && !first && $2 != "\\n" && $2 != "" { first = $1 }
        $2 == "\\n" && marker && !seen { seen = 1 }
        $2 == "▶" { marker = 1 }
        { last = $1 }
        END { printf "реплика %d: первый символ %.1f с, ответ целиком %.1f с\n", n, first - written, last - written }'
done < "$scenario"
printf '1.0\t%s\tконец записи\n' "$number" >> "$meeting/events.log"
wait "$candidate" 2> /dev/null
