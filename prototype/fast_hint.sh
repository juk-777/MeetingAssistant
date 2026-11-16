#!/usr/bin/env bash
# Быстрый путь подсказки: один вызов `claude -p` на вопрос, мимо хода сессии `/meeting`, ответ потоком.
# Меряет время до первого текста и до конца ответа (`docs/specs/2026-11-16-interview-mode.md`).
#
# fast_hint.sh <модель> <effort> <прогонов>
# Вопрос — синтетический (системный дизайн), не из встречи. Запуск без инструментов, MCP, настроек и сессии:
# так вызов стоит дешевле всего по времени. Модель и effort — явно: умолчание у каждого своё.
set -u

model=$1
effort=$2
runs=$3
prompt='Ты подсказываешь кандидату на собеседовании по системному дизайну (бэкенд, .NET). Интервьюер спросил: «Как бы вы спроектировали сокращатель ссылок на 100 миллионов переходов в день?» Дай ответ кандидату: первая строка — суть, дальше 3–4 тезиса, коротко.'
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
cd "$work" || exit 1

echo "$(claude --version), модель $model, effort $effort"
for run in $(seq "$runs"); do
    start=${EPOCHREALTIME/,/.}
    first=""
    claude -p "$prompt" --model "$model" --effort "$effort" --tools "" --strict-mcp-config --setting-sources "" \
        --disable-slash-commands --no-session-persistence --output-format stream-json --verbose \
        --include-partial-messages < /dev/null 2> /dev/null |
        while IFS= read -r line; do
            now=${EPOCHREALTIME/,/.}
            case "$line" in
                *'"text_delta"'*) [ -n "$first" ] || { first=$now; printf 'прогон %s: первый текст %.1f с' "$run" "$(echo "$now - $start" | bc)"; } ;;
                *'"type":"result"'*) printf ', ответ целиком %.1f с\n' "$(echo "$now - $start" | bc)" ;;
            esac
        done
done
