#!/usr/bin/env bash
# Молчание окна кандидата: отвечает ли модель ровно «-», когда отвечать не на что, и по-русски ли отвечает на вопрос.
# Запрос собирает сам `meeting-candidate` скилла (двойник `claude` сохраняет его), а настоящая модель зовётся тем же
# вызовом, что в окне, — так стенд меряет текущую редакцию запроса.
#
# candidate_dash.sh <модель> <прогонов на случай>
# В stderr — первая строка каждого ответа не «-»: пояснение вместо молчания счётчик не отличает от ответа.
# Случаи — синтетика, не из встречи: запись — моно-смесь, и последняя реплика «Собеседников» — речь кандидата,
# оборванная (cut), законченная (own) или с заминкой (hesitation); ожидается «-». Контроль (question) — явный вопрос
# интервьюера; ожидается ответ, а не «-». Ответ не по-русски — дефект в любом случае.
set -u
export LC_ALL=C.UTF-8

model=$1
runs=$2
repo=$(cd "$(dirname "$0")/.." && pwd)
scripts="$repo/plugins/meeting/skills/meeting/scripts"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# Двойник `claude`: сохраняет запрос и отвечает пустым потоком.
cat > "$work/claude" <<EOF
#!/usr/bin/env bash
cat > "$work/prompt.\$\$"
EOF
chmod +x "$work/claude"

# prompt_for <файл транскрипта>: запрос окна на последнюю реплику транскрипта.
prompt_for() {
    local meeting="$work/meeting"
    rm -rf "$meeting" "$work"/prompt.*
    mkdir -p "$meeting"
    : > "$meeting/transcript.md"
    : > "$meeting/events.log"
    printf '{"mode": "candidate", "model": "%s"}\n' "$model" > "$meeting/mode.json"
    MEETING_CLAUDE="$work/claude" bash "$scripts/meeting-candidate" "$meeting" > /dev/null 2>&1 &
    local candidate=$!
    sleep 0.5
    cat "$1" >> "$meeting/transcript.md"
    until compgen -G "$work/prompt.*" > /dev/null; do sleep 0.1; done
    sleep 0.2
    printf '1.0\t1\tконец записи\n' >> "$meeting/events.log"
    wait "$candidate"
    cat "$work"/prompt.*
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
    prompt_for "$work/$case.md" > "$work/$case.prompt"
    dash=0
    english=0
    for run in $(seq "$runs"); do
        answer=$(claude -p --model "$model" --effort low --tools "" --strict-mcp-config --setting-sources "" \
            --disable-slash-commands --no-session-persistence < "$work/$case.prompt" 2> /dev/null)
        [ "$answer" != "-" ] || dash=$((dash + 1))
        # По-английски — латиницы в ответе больше, чем кириллицы (термины латиницей в русском ответе допустимы).
        cyr=$(printf '%s' "$answer" | grep -oP '\p{Cyrillic}' | wc -l)
        lat=$(printf '%s' "$answer" | grep -oP '[A-Za-z]' | wc -l)
        [ "$answer" = "-" ] || [ "$lat" -le "$cyr" ] || english=$((english + 1))
        # Не «-» — первая строка ответа: пояснение вместо молчания видно только глазами.
        [ "$answer" = "-" ] || printf '  %s\n' "$(head -n 1 <<< "$answer" | cut -c1-120)" >&2
    done
    printf '%s: «-» %d из %d, по-английски %d\n' "$case" "$dash" "$runs" "$english"
done
