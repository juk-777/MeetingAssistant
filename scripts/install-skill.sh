#!/usr/bin/env bash
# Ставит скилл /meeting в Claude Code: символическая ссылка ~/.claude/skills/meeting на skills/meeting репозитория.
#
# Ссылка, а не копия: обновление репозитория сразу меняет скилл. Повторный запуск ничего не меняет. На месте
# ссылки уже лежит что-то другое — отказ, а не перезапись.
#
# Запуск из любого каталога: ./scripts/install-skill.sh
set -euo pipefail

# Физический путь (-P): с ним сравнивается readlink -f уже стоящей ссылки, даже если репозиторий открыт по ссылке.
source_dir="$(cd "$(dirname "$0")/../skills/meeting" && pwd -P)"
skills="${CLAUDE_CONFIG_DIR:-$HOME/.claude}/skills"
target="$skills/meeting"

mkdir -p "$skills"

if [ -L "$target" ]; then
    current=$(readlink -f "$target")
    if [ "$current" = "$source_dir" ]; then
        echo "Уже стоит: $target → $source_dir"
        exit 0
    fi
    echo "$target — ссылка на $current, а не на этот репозиторий. Снять её (rm \"$target\") и повторить." >&2
    exit 1
fi

if [ -e "$target" ]; then
    echo "$target уже есть, и это не ссылка: не перезаписываю. Разобрать вручную и повторить." >&2
    exit 1
fi

ln -s "$source_dir" "$target"
echo "Поставлен: $target → $source_dir"
echo "Корень встреч скилл берёт у Windows (переменная Storage__MeetingsRoot, её задаёт scripts/install.ps1); MEETINGS_ROOT в env ~/.claude/settings.json — только чтобы задать другой."
