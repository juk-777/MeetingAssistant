#!/usr/bin/env bash
# Мутационный прогон по одному файлу: ломает механизм и проверяет, что падают тесты.
#
# Зелёный прогон сам по себе не доказывает ничего — он доказывает, что тесты не падают.
# Мутация отвечает на другой вопрос: заметит ли набор, что механизм сломан. Выживший мутант —
# это место, где поведение можно испортить, и никто не узнает.
#
# Stryker.NET здесь не работает: он не поддерживает Microsoft.Testing.Platform, на которой стоит
# весь набор. Поэтому мутации свои, простые и по одному файлу за раз.
#
#   ./scripts/mutate.sh src/MeetingAssistant.Domain/Meetings/WakePolicy.cs tests/MeetingAssistant.Domain.UnitTests
#
# Правки временные: файл восстанавливается из копии после каждой мутации и в конце прогона,
# в том числе при обрыве по Ctrl+C. Прогон долгий — сборка и тесты на каждую мутацию.
#
# Пока прогон идёт, рабочее дерево изменено: `git add` в этот момент коммитит мутацию. Держать
# прогон в фоне и параллельно коммитить нельзя — так сломанный код однажды уже уехал в историю.
set -uo pipefail

cd "$(dirname "$0")/.."

TARGET="${1:-}"
TEST_PROJECT="${2:-}"

if [ -z "$TARGET" ] || [ -z "$TEST_PROJECT" ]; then
    echo "Использование: ./scripts/mutate.sh <файл под мутацию> <тестовый проект>"
    exit 2
fi

if [ ! -f "$TARGET" ]; then
    echo "Нет файла: $TARGET"
    exit 2
fi

export MSBUILDDISABLENODEREUSE=1

BACKUP=$(mktemp)
COLLECT=$(mktemp)
cp "$TARGET" "$BACKUP"
trap 'cp "$BACKUP" "$TARGET"; rm -f "$BACKUP" "$COLLECT"' EXIT INT TERM

# Пары «что ищем ~ на что меняем». Правится ровно одно вхождение в одной строке за прогон:
# мутация должна быть точечной, иначе непонятно, что именно поймали тесты.
MUTATIONS=('>=~>' '<=~<' '&&~||' '||~&&' '==~!=' '!=~==' '.Any(~.All(' '.All(~.Any(')

cat > "$COLLECT" <<'PYTHON'
import io, sys

path = sys.argv[1]
pairs = [pair.split('~', 1) for pair in sys.argv[2:]]
lines = io.open(path, encoding='utf-8-sig').read().split('\n')

for number, line in enumerate(lines, start=1):
    stripped = line.strip()

    # Комментарий механизма не несёт: мутация в нём ничего не ломает.
    if stripped.startswith(('//', '///', '*', '/*')):
        continue

    for old, new in pairs:
        if old in line:
            print(f'{number}:{old}~{new}')
PYTHON

APPLY=$(mktemp)
trap 'cp "$BACKUP" "$TARGET"; rm -f "$BACKUP" "$COLLECT" "$APPLY"' EXIT INT TERM

cat > "$APPLY" <<'PYTHON'
import io, sys

path, number, old, new = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4]
raw = io.open(path, 'rb').read()
bom = raw.startswith(b'\xef\xbb\xbf')
lines = raw.decode('utf-8-sig').split('\n')
lines[number - 1] = lines[number - 1].replace(old, new, 1)
io.open(path, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + '\n'.join(lines).encode('utf-8'))
PYTHON

# Список собирается заранее в массив: цикл, читающий из потока, отдаёт свой stdin вложенным
# командам — `dotnet` его выпивает, остаток списка теряется, и правки до файла не доезжают.
mapfile -t PLACES < <(python3 "$COLLECT" "$TARGET" "${MUTATIONS[@]}")

echo "Файл:  $TARGET"
echo "Тесты: $TEST_PROJECT"
echo "Мест под мутацию: ${#PLACES[@]}"
echo

killed=0
survived=0
broken=0

for place in "${PLACES[@]}"; do
    number="${place%%:*}"
    pair="${place#*:}"
    from="${pair%%~*}"
    to="${pair#*~}"

    python3 "$APPLY" "$TARGET" "$number" "$from" "$to"

    if ! dotnet build "$TEST_PROJECT" -v q --nologo -m:1 >/dev/null 2>&1 </dev/null; then
        printf '  строка %-5s %-8s → %-8s не собралось\n' "$number" "$from" "$to"
        broken=$((broken + 1))

    # Вывод забирается целиком, а не грепается на лету: `grep -q` закрывает конвейер на первом
    # совпадении, прогон получает SIGPIPE, и `pipefail` объявляет ветку ложной — убитый мутант
    # выглядит выжившим.
    elif grep -qE '^[[:space:]]+failed: [1-9]' <<< "$(dotnet run --project "$TEST_PROJECT" --no-build </dev/null 2>&1)"; then
        printf '  строка %-5s %-8s → %-8s убит\n' "$number" "$from" "$to"
        killed=$((killed + 1))
    else
        printf '  строка %-5s %-8s → %-8s ВЫЖИЛ\n' "$number" "$from" "$to"
        survived=$((survived + 1))
    fi

    cp "$BACKUP" "$TARGET"
done

echo
echo "убито: $killed, выжило: $survived, не собралось: $broken"
echo "Выживший мутант — дыра в наборе, а не в коде: поведение сломано, тесты молчат."

# Файл возвращается на место до финальной сборки: иначе в каталоге сборки остаётся последняя
# мутация, и следующий прогон тестов соврёт.
cp "$BACKUP" "$TARGET"
dotnet build "$TEST_PROJECT" -v q --nologo -m:1 >/dev/null 2>&1 </dev/null

[ "$survived" -eq 0 ]
