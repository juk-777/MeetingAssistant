#!/usr/bin/env bash
# Встреча без звука для прогона скилла /meeting: каталог встречи, в который строки транскрипта и события
# дописываются по сценарию, как это делает процесс записи (форматы — docs/architecture.md).
#
# Сценарий — TSV: <пауза перед строкой, с>\t<Я|Собеседники>\t<текст>\t<поводы через запятую или пусто>.
# Дописывать начинает, когда сессия подключилась: в каталоге состояния meeting-watch появился новый файл.
# В конце — событие «конец записи».
#
# meeting-feed.sh <каталог встречи> <сценарий>
set -euo pipefail

dir=$1
scenario=$2
mkdir -p "$dir"
: > "$dir/transcript.md"
: > "$dir/events.log"

state_dir="${XDG_STATE_HOME:-$HOME/.local/state}/meeting-assistant"
mkdir -p "$state_dir"
marker=$(mktemp)
until find "$state_dir" -type f -newer "$marker" | grep -q .; do sleep 0.5; done
rm -f "$marker"

started=${EPOCHREALTIME/,/.}
number=0
while IFS=$'\t' read -r pause channel text reasons; do
    [ -n "$pause" ] || continue
    sleep "$pause"
    number=$((number + 1))
    now=${EPOCHREALTIME/,/.}
    offset=$(awk -v n="$now" -v s="$started" 'BEGIN { t = int(n - s); printf "%02d:%02d:%02d", t / 3600, t % 3600 / 60, t % 60 }')
    printf '%s\t%s\t[%s] %s: %s\n' "$number" "$now" "$offset" "$channel" "$text" >> "$dir/transcript.md"
    [ -z "$reasons" ] || printf '%s\t%s\t%s\n' "$now" "$number" "$reasons" >> "$dir/events.log"
done < "$scenario"
sleep 5
printf '%s\t%s\t%s\n' "${EPOCHREALTIME/,/.}" "$number" "конец записи" >> "$dir/events.log"
