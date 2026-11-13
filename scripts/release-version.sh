#!/usr/bin/env bash
# Версия выпуска из тега: печатает X.Y.Z для тега vX.Y.Z, если версия плагина в plugin.json такая же.
#
# Источник версии — тег; плагин несёт её копию, чтобы скилл у пользователей обновлялся вместе с выпуском.
# Разошлись — отказ: релиз с программой одной версии и скиллом другой не выходит.
#
# release-version.sh <тег> [plugin.json]
set -euo pipefail

tag=${1:?release-version.sh <тег> [plugin.json]}
manifest=${2:-"$(dirname "$0")/../plugins/meeting/.claude-plugin/plugin.json"}

[[ $tag =~ ^v([0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?)$ ]] ||
    { echo "Тег $tag — не версия: нужен vX.Y.Z." >&2; exit 1; }
version=${BASH_REMATCH[1]}

# Тем же разбором JSON, что и скилл: jq в репозитории не нужен нигде.
plugin=$(bash "$(dirname "$0")/../plugins/meeting/skills/meeting/scripts/current-field" "$manifest" version) || plugin=""
[ "$plugin" = "$version" ] ||
    { echo "Версия плагина ($plugin, $manifest) не совпадает с тегом $tag: поднять её в pull request выпуска." >&2; exit 1; }

echo "$version"
