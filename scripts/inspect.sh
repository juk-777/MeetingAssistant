#!/usr/bin/env bash
# Инспекции ReSharper по всему решению: падает на любом предупреждении — так же, как сборка падает
# на предупреждении компилятора (`TreatWarningsAsErrors`). В `dotnet build` инспекции ReSharper не встраиваются,
# поэтому — отдельным шагом после каждого этапа.
#
# Нужен `jb` (JetBrains.ReSharper.GlobalTools) той версии, что понимает SDK решения:
#   dotnet tool update -g JetBrains.ReSharper.GlobalTools
# Старый `jb` не загружает проекты SDK 10 («Unknown tools version») и молча находит не то — скрипт это ловит.
#
# Грузит процессор на несколько минут: во время созвона не запускать.
# Запуск из корня репозитория: ./scripts/inspect.sh
set -uo pipefail

cd "$(dirname "$0")/.."

command -v jb >/dev/null || { echo "Нет jb: dotnet tool install -g JetBrains.ReSharper.GlobalTools" >&2; exit 2; }

# Сборка не оставляет резидентных нод MSBuild: на машине с IDE они съедают память.
export MSBUILDDISABLENODEREUSE=1
dotnet build MeetingAssistant.slnx -v q --nologo -m:1 || exit 1

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

jb inspectcode MeetingAssistant.slnx --no-build --severity=WARNING --format=Sarif \
    -o="$work/inspect.sarif" >"$work/inspect.log" 2>&1
code=$?

if [ $code -ne 0 ] || grep -q -E 'Unknown tools version|LoggerException' "$work/inspect.log"; then
    echo "jb не загрузил решение (код $code). Хвост лога:" >&2
    grep -E 'Message =|error' "$work/inspect.log" | sort -u | head -5 >&2
    echo "Обновить: dotnet tool update -g JetBrains.ReSharper.GlobalTools" >&2
    exit 2
fi

python3 -I - "$work/inspect.sarif" <<'PY'
import json
import sys

results = json.load(open(sys.argv[1], encoding="utf-8-sig"))["runs"][0].get("results", [])

for result in results:
    location = result["locations"][0]["physicalLocation"]
    print(f'{location["artifactLocation"]["uri"]}:{location["region"]["startLine"]}: '
          f'{result["ruleId"]}: {result["message"]["text"]}')

print(f"Предупреждений: {len(results)}")
sys.exit(1 if results else 0)
PY
