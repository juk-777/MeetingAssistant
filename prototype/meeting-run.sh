#!/usr/bin/env bash
# Прогон скилла /meeting без звука: meeting-feed.sh дописывает встречу по сценарию, к ней подключается `claude -p`
# со скиллом из рабочей копии; каждая строка его вывода размечается временем прихода. Без /mnt/* в PATH:
# уведомление Windows (powershell.exe) на прогоне не показывается. Разбор — hint_latency.py.
#
# meeting-run.sh <новый каталог прогона> <модель> <effort> <рабочий каталог сессии>
# Итог: <каталог прогона>/stream.tsv, <каталог прогона>/meeting/hints.md.
set -u

run=$1
model=$2
effort=$3
cwd=$4
repo=$(cd "$(dirname "$0")/.." && pwd)
[ ! -e "$run" ] || { echo "Каталог прогона уже есть: $run — нужен новый." >&2; exit 1; }
mkdir -p "$run"
meeting="$run/meeting"

bash "$repo/prototype/meeting-feed.sh" "$meeting" "$repo/prototype/meeting-feed.tsv" &
feeder=$!
clean_path=$(printf '%s' "$PATH" | tr ':' '\n' | grep -v '^/mnt/' | paste -sd:)
cd "$cwd" || exit 1
PATH=$clean_path timeout -k 30 900 claude -p "/meeting '$meeting'" --model "$model" --effort "$effort" \
    --plugin-dir "$repo/plugins/meeting" --settings '{"enabledPlugins":{"meeting@meeting-assistant":false}}' \
    --allowedTools "Bash Read Grep Glob Monitor" --output-format stream-json --verbose < /dev/null 2>&1 \
    | while IFS= read -r line; do printf '%s\t%s\n' "${EPOCHREALTIME/,/.}" "$line"; done > "$run/stream.tsv"
echo "claude: код ${PIPESTATUS[0]}"
wait "$feeder"
