#!/usr/bin/env bash
# Молчание окна кандидата: отвечает ли модель ровно «-», когда отвечать не на что, и по-русски ли отвечает на вопрос.
# Каждый прогон — `meeting-candidate` скилла с настоящей моделью на новой встрече: в транскрипт пишутся строки случая,
# ответ берётся из журнала окна (candidate.md). Так стенд меряет текущую редакцию запроса и вызова целиком.
#
# candidate_dash.sh <модель> <прогонов на случай> [каталог скриптов скилла] [append]
# Каталог скриптов — для сравнения с другой редакцией (`git archive` прошлого коммита); по умолчанию — рабочая копия.
# `append` — те же скрипты, но правила окна идут поверх системного промпта Claude Code (`--append-system-prompt-file`
# вместо `--system-prompt-file`, в копии скриптов).
# Итог по случаю: «-» из числа прогонов без сбоев, сколько ответов не «-» по-английски, сколько сбоев (отказ модели,
# оборванный или пустой ответ, нет ответа за 120 с). В stderr — первая строка каждого ответа не «-» (пояснение вместо
# молчания счётчик не отличает от ответа) и каждого сбоя.
# Случаи — синтетика, не из встречи: запись — моно-смесь, и последняя реплика «Собеседников» — речь кандидата,
# оборванная (cut), законченная (own) или с заминкой (hesitation); ожидается «-». Контроль (question) — явный вопрос
# интервьюера; ожидается ответ, а не «-». Ответ не по-русски — дефект в любом случае.
set -u
export LC_ALL=C.UTF-8

model=$1
runs=$2
repo=$(cd "$(dirname "$0")/.." && pwd)
scripts=${3:-$repo/plugins/meeting/skills/meeting/scripts}
[ -f "$scripts/meeting-candidate" ] || { echo "Нет $scripts/meeting-candidate" >&2; exit 1; }
work=$(mktemp -d)
candidate=""
# Прерванный стенд гасит и окно: оно опрашивает транскрипт до конца записи и само не выйдет.
trap 'kill "$candidate" 2> /dev/null; rm -rf "$work"' EXIT
trap 'exit 130' INT TERM HUP
if [ "${4:-}" = append ]; then
    cp -r "$scripts" "$work/scripts"
    scripts="$work/scripts"
    grep -q -- '--system-prompt-file' "$scripts/meeting-candidate" ||
        { echo "В meeting-candidate нет --system-prompt-file: режим append не к чему применить" >&2; exit 1; }
    sed -i 's/--system-prompt-file/--append-system-prompt-file/' "$scripts/meeting-candidate"
fi
mkdir "$work/bin"
ln -s "$(command -v claude)" "$work/bin/claude"
quiet_path="$work/bin:/usr/bin:/bin"

# answer_for <файл транскрипта>: ответ окна на последнюю реплику, «-» — если окно промолчало.
answer_for() {
    local meeting="$work/meeting"
    rm -rf "$meeting"
    mkdir -p "$meeting"
    : > "$meeting/transcript.md"
    : > "$meeting/events.log"
    printf '{"mode": "candidate", "model": "%s"}\n' "$model" > "$meeting/mode.json"
    # Без уведомлений: окно шлёт их через powershell.exe (WSL) или notify-send — стенду они не нужны.
    # Файл вывода обнуляется здесь, а не перенаправлением фоновой задачи: то срабатывает уже в дочернем процессе, и
    # ожидание ниже успело бы увидеть вывод прошлого прогона.
    : > "$work/window"
    PATH=$quiet_path bash "$scripts/meeting-candidate" "$meeting" >> "$work/window" 2>&1 &
    candidate=$!
    # Строки, дописанные до приветствия окна, оно считает прошлыми и не разбирает.
    local deadline=$((SECONDS + 30))
    until [ -s "$work/window" ]; do
        [ $SECONDS -lt $deadline ] || { echo "окно не открылось за 30 с" >&2; exit 1; }
        sleep 0.1
    done
    cat "$1" >> "$meeting/transcript.md"
    deadline=$((SECONDS + 120))
    until grep -q '^## ' "$meeting/candidate.md" 2> /dev/null; do
        [ $SECONDS -lt $deadline ] || { echo "ответа нет за 120 с" >&2; break; }
        sleep 0.2
    done
    printf '1.0\t1\tконец записи\n' >> "$meeting/events.log"
    wait "$candidate"
    candidate=""
    # Тело блока журнала: «(нет ответа)» пишется на «-»; журнала нет — пустой ответ, его стенд считает сбоем.
    awk 'NR > 2' "$meeting/candidate.md" 2> /dev/null | sed '/^$/d' | sed 's/^(нет ответа)$/-/'
}

# Моно-смесь: обе стороны — «Собеседники», фразы длинные, вопрос и ответ сливаются в одну строку.
context='1	1.0	[00:00:01] Собеседники: Давай начнём с проекта. Расскажи, как у вас была устроена загрузка больших файлов от клиентов? Ну, файл сначала падал в хранилище.
2	2.0	[00:00:15] Собеседники: А потом мы ставили задачу в очередь, и там уже обработчик его разбирал по частям, э-э, по строкам, и писал в базу.
3	3.0	[00:00:31] Собеседники: Угу. А если обработчик падал посередине, что происходило с файлом? Ну, мы, э-э, там была отметка, до какой строки дошли.
4	4.0	[00:00:48] Собеседники: И с неё уже продолжали. Угу. Понял. А кто у вас решал, как это делать, ты сам или кто-то ещё?'
printf '%s\n%s\n' "$context" '5	5.0	[00:01:05] Собеседники: Ну, в основном я, но обсуждали с начальником, где-то устно, где-то там, ну, в задаче писали, и потом, когда задача' > "$work/cut.md"
printf '%s\n%s\n' "$context" '5	5.0	[00:01:05] Собеседники: То есть обсуждение у нас по ситуации. Угу. Понял. Хорошо. Где-то устно хватает, где-то схему рисуем, но прямо отдельных документов на решения у нас нет.' > "$work/own.md"
printf '%s\n%s\n' "$context" '5	5.0	[00:01:05] Собеседники: Вы про то, кто решает, да? Хмм. Ну. Ладно, я имел в виду, как это выглядело для меня, как для, э-э, разработчика, который..' > "$work/hesitation.md"
printf '%s\n%s\n' "$context" '5	5.0	[00:01:05] Собеседники: Хорошо, понял. А чем отличается оптимистичная блокировка от пессимистичной и когда что выбирать?' > "$work/question.md"

echo "$(claude --version), модель $model, effort low"
for case in cut own hesitation question; do
    dash=0
    english=0
    failed=0
    for run in $(seq "$runs"); do
        # Без подоболочки: ловушка выхода видит окно этого прогона и гасит его.
        answer_for "$work/$case.md" > "$work/answer"
        answer=$(< "$work/answer")
        # Отказ модели, оборванный или пустой ответ — не ответ и не молчание: в счёт не идёт.
        if [ -z "$answer" ] || [[ $answer == "(модель не ответила"* ]] || [[ $answer == *"(прервано)" ]]; then
            failed=$((failed + 1))
            printf '  сбой: %s\n' "$(head -n 1 <<< "$answer" | cut -c1-120)" >&2
            continue
        fi
        [ "$answer" != "-" ] || dash=$((dash + 1))
        # По-английски — латиницы в ответе больше, чем кириллицы (термины латиницей в русском ответе допустимы).
        cyr=$(printf '%s' "$answer" | grep -oP '\p{Cyrillic}' | wc -l)
        lat=$(printf '%s' "$answer" | grep -oP '[A-Za-z]' | wc -l)
        [ "$answer" = "-" ] || [ "$lat" -le "$cyr" ] || english=$((english + 1))
        # Не «-» — первая строка ответа: пояснение вместо молчания видно только глазами.
        [ "$answer" = "-" ] || printf '  %s\n' "$(head -n 1 <<< "$answer" | cut -c1-120)" >&2
    done
    printf '%s: «-» %d из %d, по-английски %d, сбоев %d\n' "$case" "$dash" "$((runs - failed))" "$english" "$failed"
done
