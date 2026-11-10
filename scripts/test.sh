#!/usr/bin/env bash
# Прогон всего набора тестов.
#
# `dotnet test` здесь не работает: SDK 10 говорит с тестовым приложением протоколом server mode,
# а xunit.v3 отвечает на него нулём тестов. Поэтому набор гоняется запуском самих тестовых
# приложений — они и есть исполняемые файлы. Один проект: `dotnet run --project tests/<проект>`.
#
# Аргументы уходят каждому приложению как есть: ./scripts/test.sh --filter-class '*WakePolicyTests'
# Приложение без тестов под фильтр отвечает кодом 8 — это не падение, а «не моё». Ноль приложений или
# ноль тестов в сумме — провал: фильтр с опечаткой или потерянный каталог сборки не выдают себя за зелёный.
set -uo pipefail

cd "$(dirname "$0")/.."

# Сборка не оставляет резидентных нод MSBuild: на машине с IDE они съедают память.
export MSBUILDDISABLENODEREUSE=1

dotnet build MeetingAssistant.slnx -v q --nologo -m:1 || exit 1

failed=0
total=0
apps_run=0

# Код Microsoft.Testing.Platform «ни один тест не запущен».
ZERO_TESTS_EXIT_CODE=8

# Куда легли сборки, решает Directory.Build.props: под WSL выход уходит из дерева в общий каталог.
# Путь спрашивается у MSBuild, а не собирается по памяти: в ключе каталога стоит хеш пути дерева.
probe=$(ls -d tests/*/*.csproj 2>/dev/null | head -1)
base=$(dotnet msbuild "$probe" -getProperty:BaseOutputPath 2>/dev/null | tr -d '\r')

case "$base" in
    /*) apps="${base%/}"; apps="$(dirname "$apps")"/*/Debug/net10.0/MeetingAssistant.*Tests ;;
    *)  apps="tests/*/bin/Debug/net10.0/MeetingAssistant.*Tests" ;;
esac

for app in $apps; do
    [ -x "$app" ] || continue
    apps_run=$((apps_run + 1))

    output=$("$app" "$@" 2>&1)
    code=$?
    count=$(printf '%s' "$output" | grep -oP 'total: \K\d+' | tail -1)
    total=$((total + ${count:-0}))

    if [ $code -eq $ZERO_TESTS_EXIT_CODE ]; then
        echo "--  $(basename "$app") — нет тестов под фильтр"
    elif [ $code -ne 0 ]; then
        failed=1
        printf '%s\n' "$output"
    else
        echo "ok  $(basename "$app") — ${count:-0}"
    fi
done

echo "итого тестов: $total"

if [ $apps_run -eq 0 ] || [ $total -eq 0 ]; then
    echo "ни одного теста не запущено: приложений $apps_run — фильтр или каталог сборки"
    exit 1
fi

exit $failed
