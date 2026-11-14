#!/usr/bin/env bash
# Прогон скилла /meeting на записи созвона в реальном темпе: `meeting replay --speed 1` пишет встречу в отдельный
# корень встреч прогона (current.json владельца не трогается), `claude -p` со скиллом из рабочей копии подключается
# к ней сразу после старта записи. Каждая строка вывода сессии размечается временем прихода, как в meeting-run.sh.
# Разбор — hint_latency.py (задержка по частям), events_stats.py (пробуждения).
#
# meeting-replay-run.sh <новый каталог прогона> <meeting> <звук> <модель> <effort> <рабочий каталог сессии>
# <meeting> — исполняемый файл приложения записи (сборка Release). Итог: <прогон>/stream.tsv, <прогон>/replay.log,
# каталог встречи в <прогон>/meetings/.
set -u

run=$1
meeting=$2
audio=$3
model=$4
effort=$5
cwd=$6
repo=$(cd "$(dirname "$0")/.." && pwd)
[ ! -e "$run" ] || { echo "Каталог прогона уже есть: $run — нужен новый." >&2; exit 1; }
[ -d "$cwd" ] || { echo "Нет рабочего каталога сессии: $cwd" >&2; exit 1; }
# Срок сессии — длина записи и 10 минут сверху: упавший replay без «конца записи» не держит её часами.
seconds=$(python3 -c 'import sys, wave; w = wave.open(sys.argv[1]); print(int(w.getnframes() / w.getframerate()))' "$audio") ||
    exit 1
mkdir -p "$run/meetings"
run=$(cd "$run" && pwd)

Storage__MeetingsRoot="$run/meetings" "$meeting" replay "$audio" --name replay --speed 1 > "$run/replay.log" 2>&1 &
recorder=$!
until dir=$(find "$run/meetings" -mindepth 1 -maxdepth 1 -type d | head -1) && [ -n "$dir" ]; do
    kill -0 "$recorder" 2> /dev/null || { echo "replay завершился до начала встречи: $run/replay.log" >&2; exit 1; }
    sleep 0.2
done

clean_path=$(printf '%s' "$PATH" | tr ':' '\n' | grep -v '^/mnt/' | paste -sd:)
cd "$cwd" || exit 1
PATH=$clean_path timeout -k 30 $((seconds + 600)) claude -p "/meeting '$dir'" --model "$model" --effort "$effort" \
    --plugin-dir "$repo/plugins/meeting" --settings '{"enabledPlugins":{"meeting@meeting-assistant":false}}' \
    --allowedTools "Bash Read Grep Glob Monitor" --output-format stream-json --verbose < /dev/null 2>&1 \
    | while IFS= read -r line; do printf '%s\t%s\n' "${EPOCHREALTIME/,/.}" "$line"; done > "$run/stream.tsv"
echo "claude: код ${PIPESTATUS[0]}"
wait "$recorder"
tail -n 1 "$run/replay.log"
