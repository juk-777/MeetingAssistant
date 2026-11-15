#!/usr/bin/env bash
# Тесты скриптов скилла /meeting на синтетических файлах встречи во временном каталоге.
#
# Запускается из scripts/test.sh вместе с тестовыми приложениями .NET; итог — строка «итого: N, упало: M,
# пропущено: K», где N — прогнанные тесты. Ни одного прогнанного — провал: всё пропущенное зелёным не считается.
# Те же тесты идут в Git Bash (Claude Code для Windows): docs/development.md.
set -uo pipefail

work=$(mktemp -d)
export XDG_STATE_HOME="$work/state"

# Скрипты — копией без бита исполнения: так их получает плагин, установленный в кеш Claude Code, и скилл зовёт их
# через bash. Скрипт, который зовёт соседа напрямую, здесь падает.
bin="$work/scripts"
cp -r "$(dirname "$0")/../../plugins/meeting/skills/meeting/scripts" "$bin"
chmod -x "$bin"/*

# jq в репозитории не нужен нигде: в Git for Windows его нет, JSON читает current-field. Заглушка роняет любой
# вызов и там, где jq стоит (WSL разработчика, раннер GitHub).
mkdir -p "$work/no-jq"
printf '#!/usr/bin/env bash\necho "jq вызван: $*" >&2\nexit 127\n' > "$work/no-jq/jq"
chmod +x "$work/no-jq/jq"
export PATH="$work/no-jq:$PATH"

# Предел ожидания вывода: скрипт, который не отдал блок, роняет тест, а не вешает прогон.
LIMIT_SECONDS=5

watchers=()
passed=0
failed=0
skipped=0

stop_watchers() {
    for pid in "${watchers[@]}"; do kill "$pid" 2>/dev/null; done
    watchers=()
}

cleanup() {
    stop_watchers
    rm -rf "$work"
}
trap cleanup EXIT

new_meeting() {
    meeting=$(mktemp -d "$work/meeting.XXXX")
}

utterance() {
    printf '%s\t1762774261.%03d\t[00:00:%02d] Собеседники: фраза %s\n' "$1" "$1" "$1" "$1" >> "$meeting/transcript.md"
}

# Строка, которую процесс записи ещё не дописал: без перевода строки.
partial_utterance() {
    printf '%s\t1762774261.%03d\t[00:00:%02d] Собеседники: фра' "$1" "$1" "$1" >> "$meeting/transcript.md"
}

finish_utterance() {
    printf 'за %s\n' "$1" >> "$meeting/transcript.md"
}

event() {
    printf '1762774262.%03d\t%s\tвопрос\n' "$1" "$1" >> "$meeting/events.log"
}

end_of_recording() {
    printf '1762774263.000\t%s\tконец записи\n' "$1" >> "$meeting/events.log"
}

line() {
    printf '[00:00:%02d] Собеседники: фраза %s' "$1" "$1"
}

end_header() {
    printf 'конец записи\t1762774263.000\t%s' "$1"
}

event_header() {
    printf 'событие\t1762774262.%03d\tвопрос\t%s' "$1" "$1"
}

# start_watch <проект> <файл вывода>: meeting-watch в фоне из каталога проекта, как его запускает Monitor.
start_watch() {
    mkdir -p "$1"
    (cd "$1" && exec bash "$bin/meeting-watch" "$meeting") > "$2" 2>&1 &
    watch_pid=$!
    watchers+=("$watch_pid")
}

stop_watch() {
    kill "$watch_pid" 2>/dev/null
    wait "$watch_pid" 2>/dev/null
}

# wait_lines <файл> <строк>: ждёт, пока в выводе наберётся столько строк, и ещё такт опроса — лишнее тоже видно.
wait_lines() {
    local deadline=$((SECONDS + LIMIT_SECONDS))
    while [ "$(wc -l < "$1")" -lt "$2" ] && [ $SECONDS -lt $deadline ]; do sleep 0.1; done
    sleep 0.7
}

# expect_output <файл> <ожидаемые строки...>
expect_output() {
    local file=$1
    shift
    local expected
    expected=$(printf '%s\n' "$@")
    wait_lines "$file" $#
    if [ "$(cat "$file")" != "$expected" ]; then
        printf 'ожидалось:\n%s\nполучено:\n%s\n' "$expected" "$(cat "$file")"
        return 1
    fi
}

# expect_exit <pid> <код>: процесс завершился сам с этим кодом в пределах ожидания.
expect_exit() {
    local deadline=$((SECONDS + LIMIT_SECONDS))
    while kill -0 "$1" 2>/dev/null && [ $SECONDS -lt $deadline ]; do sleep 0.1; done
    if kill -0 "$1" 2>/dev/null; then
        echo "процесс не завершился"
        return 1
    fi
    wait "$1"
    local code=$?
    [ "$code" -eq "$2" ] || { echo "код выхода $code вместо $2"; return 1; }
}

# current_json <файл> <id> <каталог>: current.json в том виде, в каком его пишет приложение (System.Text.Json):
# отступы, \ и " экранированы, кириллица как есть, без перевода строки в конце. Каталог — уже в виде JSON,
# если в нём нужны \uXXXX.
current_json() {
    local directory=${3//\\/\\\\}
    directory=${directory//\"/\\\"}
    printf '{\n  "id": "%s",\n  "name": "встреча",\n  "directory": "%s",\n  "startedAt": "2026-11-13T08:01:27.0395377+00:00"\n}' \
        "$2" "$directory" > "$1"
}

# Причина, по которой тест не идёт в этой оболочке; пусто — идёт.
skip_reason() {
    if [[ $OSTYPE == msys* || $OSTYPE == cygwin* ]]; then
        case $1 in
            install_*) echo "install-skill.sh — путь разработчика в WSL; в Windows скилл из рабочей копии — claude --plugin-dir" ;;
            linux*_*) echo "путь Linux (install.sh, окно эмулятора терминала, notify-send); в Windows — powershell.exe" ;;
            *_WhenSkillLinked_*) echo "ссылка на скилл — путь разработчика в WSL (install-skill.sh); ln -s Git Bash копирует" ;;
            *) return 1 ;;
        esac
    else
        case $1 in
            *_WhenCalledByWindowsPath_* | *InGitBash_*) echo "только в Git Bash" ;;
            *) return 1 ;;
        esac
    fi
}

# Тест идёт в этой же оболочке, а не в подоболочке: иначе его фоновые meeting-watch не остановить.
run() {
    local name=$1 reason
    if reason=$(skip_reason "$name"); then
        skipped=$((skipped + 1))
        echo "skip  $name — $reason"
        return 0
    fi
    if "$name" > "$work/test.log" 2>&1; then
        passed=$((passed + 1))
        echo "ok    $name"
    else
        failed=$((failed + 1))
        printf 'FAIL  %s\n%s\n' "$name" "$(cat "$work/test.log")"
    fi
    stop_watchers
}

watch_WhenFirstAttached_GivesCompleteLinesSoFarWithoutPastEvents() {
    new_meeting
    utterance 1
    utterance 2
    event 2
    partial_utterance 3
    start_watch "$work/project" "$work/out"

    expect_output "$work/out" $'начало\t2' "$(line 1)" "$(line 2)"
}

watch_WhenEventArrives_GivesAllLinesSinceLastRead() {
    new_meeting
    utterance 1
    start_watch "$work/project" "$work/out"
    wait_lines "$work/out" 2

    utterance 2
    utterance 3
    event 3

    expect_output "$work/out" $'начало\t1' "$(line 1)" "$(event_header 3)" "$(line 2)" "$(line 3)"
}

watch_WhenLineUnfinished_GivesItWithNextEvent() {
    new_meeting
    start_watch "$work/project" "$work/out"
    wait_lines "$work/out" 1

    utterance 1
    partial_utterance 2
    event 1
    wait_lines "$work/out" 3
    finish_utterance 2
    utterance 3
    event 3

    expect_output "$work/out" $'начало\t0' "$(event_header 1)" "$(line 1)" "$(event_header 3)" "$(line 2)" "$(line 3)"
}

watch_WhenRestarted_GivesEventWrittenWhileDownOnce() {
    new_meeting
    start_watch "$work/project" "$work/first"
    wait_lines "$work/first" 1
    utterance 1
    event 1
    wait_lines "$work/first" 3
    stop_watch

    utterance 2
    event 2
    start_watch "$work/project" "$work/second"

    expect_output "$work/second" "$(event_header 2)" "$(line 2)"
}

watch_WhenEndOfRecording_GivesRemainingLinesAndExits() {
    new_meeting
    start_watch "$work/project" "$work/out"
    wait_lines "$work/out" 1

    utterance 1
    end_of_recording 1

    expect_output "$work/out" $'начало\t0' "$(end_header 1)" "$(line 1)" || return 1
    expect_exit "$watch_pid" 0
}

watch_WhenRestartedAfterEnd_ExitsWithoutRepeatingLines() {
    new_meeting
    utterance 1
    start_watch "$work/project" "$work/first"
    wait_lines "$work/first" 2
    utterance 2
    end_of_recording 2
    expect_exit "$watch_pid" 0 || return 1

    start_watch "$work/project" "$work/second"

    expect_exit "$watch_pid" 0 || return 1
    expect_output "$work/second" "$(end_header 2)"
}

watch_WhenRestartedBeforeFirstEvent_DoesNotRepeatStart() {
    new_meeting
    utterance 1
    start_watch "$work/project" "$work/first"
    wait_lines "$work/first" 2
    stop_watch

    utterance 2
    event 2
    start_watch "$work/project" "$work/second"

    expect_output "$work/second" "$(event_header 2)" "$(line 2)"
}

watch_WhenEventAheadOfTranscript_WaitsForItsLine() {
    new_meeting
    start_watch "$work/project" "$work/out"
    wait_lines "$work/out" 1

    event 1
    sleep 1
    utterance 1

    expect_output "$work/out" $'начало\t0' "$(event_header 1)" "$(line 1)"
}

watch_WhenTwoProjects_ReadIndependently() {
    new_meeting
    utterance 1
    start_watch "$work/project-a" "$work/a"
    expect_output "$work/a" $'начало\t1' "$(line 1)" || return 1
    stop_watch

    start_watch "$work/project-b" "$work/b"

    expect_output "$work/b" $'начало\t1' "$(line 1)"
}

# stub_paths [каталог] [ответ]: заглушка перевода путей под обоими именами — cygpath (Git Bash) и wslpath (WSL),
# так тест одинаков в обеих оболочках. Любой путь переводит в ответ (по умолчанию — каталог встречи), аргументы
# дописывает в paths.calls.
stub_paths() {
    local dir=${1:-$work/stub} answer=${2:-$meeting} name
    mkdir -p "$dir"
    rm -f "$work/paths.calls"
    for name in cygpath wslpath; do
        printf '#!/usr/bin/env bash\necho "$*" >> "%s/paths.calls"\necho "%s"\n' "$work" "$answer" > "$dir/$name"
        chmod +x "$dir/$name"
    done
}

dir_WhenCurrentHasWindowsPath_TranslatesIt() {
    new_meeting
    stub_paths
    mkdir -p "$work/root"
    printf '{ "directory": "D:\\\\meetings\\\\2026-11-10_10-00-00_x" }\n' > "$work/root/current.json"

    local got
    got=$(PATH="$work/stub:$PATH" MEETINGS_ROOT="$work/root" bash "$bin/meeting-dir") || return 1

    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
    grep -qxF -- '-u D:\meetings\2026-11-10_10-00-00_x' "$work/paths.calls" || { cat "$work/paths.calls"; return 1; }
}

dir_WhenCurrentHasPosixPath_TakesItAsIs() {
    new_meeting
    mkdir -p "$work/posix-root"
    current_json "$work/posix-root/current.json" x "$meeting"

    local got
    got=$(MEETINGS_ROOT="$work/posix-root" bash "$bin/meeting-dir") || return 1

    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
}

# Приложение пишет символы вне BMP суррогатной парой \uXXXX\uXXXX, кириллицу — как есть; \u — по байту UTF-8
# от одного до четырёх.
dir_WhenCurrentHasEscapedWindowsPath_UnescapesIt() {
    new_meeting
    stub_paths
    mkdir -p "$work/escaped-root"
    printf '{\n  "id": "x",\n  "directory": "D:\\\\Мои встречи\\\\2026_x \\uD83D\\uDE00 \\u0436 \\u2028 \\u0041",\n  "startedAt": "2026"\n}' \
        > "$work/escaped-root/current.json"

    local got
    got=$(PATH="$work/stub:$PATH" MEETINGS_ROOT="$work/escaped-root" bash "$bin/meeting-dir") || return 1

    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
    grep -qxF -- "-u D:\\Мои встречи\\2026_x 😀 ж "$'\xe2\x80\xa8'" A" "$work/paths.calls" || { cat "$work/paths.calls"; return 1; }
}

# В Git Bash путь Windows из current.json открывается как есть, без wslpath.
dir_WhenWindowsPathInGitBash_OpensItAsIs() {
    new_meeting
    mkdir -p "$work/gitbash-root"
    current_json "$work/gitbash-root/current.json" x "$(cygpath -w "$meeting")"

    local got
    got=$(MEETINGS_ROOT="$work/gitbash-root" bash "$bin/meeting-dir") || return 1

    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
}

dir_WhenCurrentHasEscapedQuotesAndSlashes_TakesDirectoryWhole() {
    local quoted="$work/встреча \"a\" b"
    mkdir -p "$quoted" "$work/quoted-root"
    local escaped=${quoted//\"/\\\"}
    escaped=${escaped//\//\\/}
    printf '{\n  "id": "x",\n  "directory": "%s",\n  "name": "после"\n}' "$escaped" > "$work/quoted-root/current.json"

    local got
    got=$(MEETINGS_ROOT="$work/quoted-root" bash "$bin/meeting-dir") || return 1

    [ "$got" = "$quoted" ] || { echo "получено: $got"; return 1; }
}

dir_WhenArgumentGiven_TranslatesWindowsPathWithoutCurrent() {
    new_meeting
    stub_paths

    local got
    got=$(PATH="$work/stub:$PATH" MEETINGS_ROOT="" bash "$bin/meeting-dir" 'D:\meetings\x') || return 1

    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
}

# Заглушка powershell.exe для корня встреч из Windows: без аргумента переменной нет (пустой вывод), с аргументом —
# её значение, с переводом строки Windows, как его печатает PowerShell.
stub_reg() {
    mkdir -p "$work/stub-reg"
    printf '%s\r\n' "${1:-}" > "$work/reg.out"
    printf '#!/usr/bin/env bash\ncat "%s/reg.out"\n' "$work" > "$work/stub-reg/powershell.exe"
    chmod +x "$work/stub-reg/powershell.exe"
}

dir_WhenRootNotSet_ExitsAsNotInstalled() {
    stub_reg
    local code=0
    PATH="$work/stub-reg:$PATH" MEETINGS_ROOT="" bash "$bin/meeting-dir" > /dev/null 2>&1 || code=$?
    [ "$code" -eq 3 ] || { echo "код: $code"; return 1; }
}

dir_WhenRootOnlyInWindows_TakesItFromRegistry() {
    new_meeting
    stub_reg 'D:\Мои встречи'
    stub_paths "$work/stub" "$work/registry-root"
    mkdir -p "$work/registry-root"
    current_json "$work/registry-root/current.json" x "$meeting"

    local got
    got=$(PATH="$work/stub-reg:$work/stub:$PATH" MEETINGS_ROOT="" bash "$bin/meeting-dir") || return 1

    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
    grep -qxF -- '-u D:\Мои встречи' "$work/paths.calls" || { cat "$work/paths.calls"; return 1; }
}

dir_WhenMeetingsRootSet_PrefersItOverRegistry() {
    new_meeting
    stub_reg "$work/registry-only"
    mkdir -p "$work/registry-only" "$work/own-root"
    echo '{ "directory": "/nonexistent" }' > "$work/registry-only/current.json"
    current_json "$work/own-root/current.json" x "$meeting"

    local got
    got=$(PATH="$work/stub-reg:$PATH" MEETINGS_ROOT="$work/own-root" bash "$bin/meeting-dir") || return 1

    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
}

dir_WhenRootIsWindowsPath_TranslatesIt() {
    new_meeting
    stub_paths "$work/stub" "$work/win-root"
    mkdir -p "$work/win-root"
    current_json "$work/win-root/current.json" x "$meeting"

    local got
    got=$(PATH="$work/stub:$PATH" MEETINGS_ROOT='D:\meetings' bash "$bin/meeting-dir") || return 1

    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
}

dir_WhenCurrentHasNoDirectory_Fails() {
    mkdir -p "$work/broken-root"
    echo '{}' > "$work/broken-root/current.json"
    if MEETINGS_ROOT="$work/broken-root" bash "$bin/meeting-dir" > /dev/null 2>&1; then
        echo "отказа нет"
        return 1
    fi
}

dir_WhenNoCurrent_Fails() {
    mkdir -p "$work/empty-root"
    if MEETINGS_ROOT="$work/empty-root" bash "$bin/meeting-dir" > /dev/null 2>&1; then
        echo "отказа нет"
        return 1
    fi
}

# Заглушка powershell.exe для meeting-start: пишет переданные имя, словарь и корень в start.args; если подготовлен
# start.next — через секунду кладёт его в current.json корня, как встреча, начавшаяся после загрузки модели.
stub_start() {
    rm -rf "$work/stub-start" "$work/start.args" "$work/start.next"
    mkdir -p "$work/stub-start" "$work/start-root"
    cat > "$work/stub-start/powershell.exe" <<EOF
#!/usr/bin/env bash
printf '%s\n%s\n%s\n' "\$MEETING_START_NAME" "\$MEETING_START_GLOSSARY" "\$MEETING_START_ROOT" > "$work/start.args"
[ ! -f "$work/start.next" ] || ( sleep 1; cp "$work/start.next" "$work/start-root/current.json" ) > /dev/null 2>&1 &
EOF
    chmod +x "$work/stub-start/powershell.exe"
    echo '{ "id": "previous", "directory": "'"$work"'" }' > "$work/start-root/current.json"
}

start_WhenNewMeetingAppears_PrintsItsDirectoryAndPassesName() {
    new_meeting
    stub_start
    current_json "$work/start.next" new "$meeting"

    local got
    got=$(PATH="$work/stub-start:$PATH" MEETINGS_ROOT="$work/start-root" MEETING_START_TIMEOUT=5 \
        timeout 15 bash "$bin/meeting-start" 'Встреча с партнёром') || return 1

    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
    [ "$(head -1 "$work/start.args")" = 'Встреча с партнёром' ] || { cat "$work/start.args"; return 1; }

    # Окну передан тот корень, где ждали встречу, — в виде, понятном Windows.
    [ "$(sed -n 3p "$work/start.args")" = "$(windows_path "$work/start-root")" ] || { cat "$work/start.args"; return 1; }
}

start_WhenNoNewMeeting_FailsInsteadOfTakingPrevious() {
    stub_start

    local got
    if got=$(PATH="$work/stub-start:$PATH" MEETINGS_ROOT="$work/start-root" MEETING_START_TIMEOUT=2 \
        timeout 15 bash "$bin/meeting-start" 'x' 2> "$work/start.err"); then
        echo "отказа нет, получено: $got"
        return 1
    fi
    [ -z "$got" ] || { echo "напечатана прошлая встреча: $got"; return 1; }
    grep -q 'окне meeting' "$work/start.err" || { cat "$work/start.err"; return 1; }
}

start_WhenGlossaryGiven_PassesItAndRootAsWindowsPaths() {
    new_meeting
    stub_start
    current_json "$work/start.next" new "$meeting"
    stub_paths "$work/stub-start" 'C:\Windows-путь'
    touch "$work/glossary.txt"

    PATH="$work/stub-start:$PATH" MEETINGS_ROOT="$work/start-root" MEETING_START_TIMEOUT=5 \
        timeout 15 bash "$bin/meeting-start" 'x' "$work/glossary.txt" > /dev/null || return 1

    [ "$(sed -n 2p "$work/start.args")" = 'C:\Windows-путь' ] && [ "$(sed -n 3p "$work/start.args")" = 'C:\Windows-путь' ] ||
        { cat "$work/start.args"; return 1; }
    grep -qxF -- "-w $work/glossary.txt" "$work/paths.calls" && grep -qxF -- "-w $work/start-root" "$work/paths.calls" ||
        { cat "$work/paths.calls"; return 1; }
}

start_WhenAppNotInstalled_ExitsAsNotInstalledWithoutWaiting() {
    stub_start
    printf 'exit 3\n' >> "$work/stub-start/powershell.exe"

    local code=0
    PATH="$work/stub-start:$PATH" MEETINGS_ROOT="$work/start-root" MEETING_START_TIMEOUT=10 \
        timeout 5 bash "$bin/meeting-start" 'x' > /dev/null 2>&1 || code=$?
    [ "$code" -eq 3 ] || { echo "код: $code"; return 1; }
}

start_WhenRootNotSet_ExitsAsNotInstalled() {
    stub_reg
    local code=0
    PATH="$work/stub-reg:$PATH" MEETINGS_ROOT="" timeout 5 bash "$bin/meeting-start" 'x' > /dev/null 2>&1 || code=$?
    [ "$code" -eq 3 ] || { echo "код: $code"; return 1; }
}

# Заглушка powershell.exe для meeting-refine: пишет каталог, переданный окну, в refine.args.
stub_refine() {
    rm -rf "$work/stub-refine" "$work/refine.args"
    mkdir -p "$work/stub-refine"
    printf '#!/usr/bin/env bash\nprintf "%%s\\n" "$MEETING_REFINE_DIR" > "%s/refine.args"\n' "$work" > "$work/stub-refine/powershell.exe"
    chmod +x "$work/stub-refine/powershell.exe"
}

windows_path() {
    if command -v cygpath > /dev/null; then
        cygpath -w "$1"
    elif command -v wslpath > /dev/null; then
        wslpath -w "$1"
    else
        printf '%s\n' "$1"
    fi
}

# Только cygpath, как в Git Bash: в WSL рядом настоящий wslpath, и путь от него заглушку не обманет.
refine_WhenCygpathPresent_PassesItsWindowsPathToWindow() {
    new_meeting
    end_of_recording 0
    stub_refine
    stub_paths "$work/stub-paths" 'C:\встречи\x'
    rm "$work/stub-paths/wslpath"

    local got
    got=$(PATH="$work/stub-refine:$work/stub-paths:$PATH" timeout 5 bash "$bin/meeting-refine" "$meeting") || return 1

    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
    [ "$(cat "$work/refine.args")" = 'C:\встречи\x' ] || { cat "$work/refine.args"; return 1; }
    grep -qxF -- "-w $meeting" "$work/paths.calls" || { cat "$work/paths.calls"; return 1; }
}

# Claude Code в Windows называет каталог скилла путём Windows: скрипт, вызванный по нему, находит соседей
# (dirname Git Bash делит путь и по \).
scripts_WhenCalledByWindowsPath_FindNeighbours() {
    new_meeting
    end_of_recording 0
    local scripts got
    scripts=$(cygpath -w "$bin")

    mkdir -p "$work/backslash-root"
    current_json "$work/backslash-root/current.json" x "$meeting"
    got=$(MEETINGS_ROOT="$work/backslash-root" bash "$scripts\\meeting-dir") || return 1
    [ "$got" = "$meeting" ] || { echo "meeting-dir: $got"; return 1; }

    stub_refine
    got=$(PATH="$work/stub-refine:$PATH" timeout 5 bash "$scripts\\meeting-refine" "$meeting") || return 1
    [ "$got" = "$meeting" ] || { echo "meeting-refine: $got"; return 1; }

    stub_start
    current_json "$work/start.next" new "$meeting"
    got=$(PATH="$work/stub-start:$PATH" MEETINGS_ROOT="$work/start-root" MEETING_START_TIMEOUT=5 \
        timeout 15 bash "$scripts\\meeting-start" 'x') || return 1
    [ "$got" = "$meeting" ] || { echo "meeting-start: $got"; return 1; }
}

refine_WhenFinishedMeetingGiven_OpensWindowForItAndPrintsIt() {
    new_meeting
    utterance 1
    end_of_recording 1
    stub_refine

    local got
    got=$(PATH="$work/stub-refine:$PATH" timeout 5 bash "$bin/meeting-refine" "$meeting") || return 1

    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
    [ "$(cat "$work/refine.args")" = "$(windows_path "$meeting")" ] || { cat "$work/refine.args"; return 1; }
}

refine_WhenNoDirectoryGiven_TakesCurrentMeeting() {
    new_meeting
    end_of_recording 0
    stub_refine
    mkdir -p "$work/refine-root"
    current_json "$work/refine-root/current.json" x "$meeting"

    local got
    got=$(PATH="$work/stub-refine:$PATH" MEETINGS_ROOT="$work/refine-root" timeout 5 bash "$bin/meeting-refine") || return 1

    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
    [ "$(cat "$work/refine.args")" = "$(windows_path "$meeting")" ] || { cat "$work/refine.args"; return 1; }
}

refine_WhenRecordingNotFinished_RefusesWithoutWindow() {
    new_meeting
    utterance 1
    event 1
    stub_refine

    if PATH="$work/stub-refine:$PATH" timeout 5 bash "$bin/meeting-refine" "$meeting" > /dev/null 2> "$work/refine.err"; then
        echo "отказа нет"
        return 1
    fi
    [ ! -f "$work/refine.args" ] || { echo "окно открыто"; return 1; }
    grep -q 'не закончена' "$work/refine.err" || { cat "$work/refine.err"; return 1; }
}

refine_WhenAppNotInstalled_ExitsAsNotInstalled() {
    new_meeting
    end_of_recording 0
    stub_refine
    printf 'exit 3\n' >> "$work/stub-refine/powershell.exe"

    local code=0
    PATH="$work/stub-refine:$PATH" timeout 5 bash "$bin/meeting-refine" "$meeting" > /dev/null 2>&1 || code=$?
    [ "$code" -eq 3 ] || { echo "код: $code"; return 1; }
}

refine_WhenRootNotSet_ExitsAsNotInstalled() {
    stub_reg
    local code=0
    PATH="$work/stub-reg:$PATH" MEETINGS_ROOT="" timeout 5 bash "$bin/meeting-refine" > /dev/null 2>&1 || code=$?
    [ "$code" -eq 3 ] || { echo "код: $code"; return 1; }
}

# plugin_copy <версия>: скрипты скилла в раскладке плагина, как их ставит Claude Code, — plugin.json тремя
# каталогами выше скриптов. Скрипты — в $work/plugin/skills/meeting/scripts.
plugin_copy() {
    rm -rf "$work/plugin"
    mkdir -p "$work/plugin/.claude-plugin" "$work/plugin/skills/meeting"
    cp -r "$bin" "$work/plugin/skills/meeting/scripts"
    printf '{\n  "name": "meeting",\n  "version": "%s",\n  "description": "x"\n}\n' "$1" > "$work/plugin/.claude-plugin/plugin.json"
}

# stub_app_version <каталог> <версия>: вызов powershell.exe с --version (сверка) отвечает версией строкой Windows
# (\r\n) и кодом STUB_VERSION_CODE, вызов дописывается в version.calls; остальные вызовы (окно) идут в заглушку,
# которая уже лежала в каталоге.
stub_app_version() {
    mkdir -p "$1"
    [ ! -f "$1/powershell.exe" ] || mv "$1/powershell.exe" "$1/window.stub"
    rm -f "$work/version.calls"
    printf '%s\r\n' "$2" > "$1/app.version"
    cat > "$1/powershell.exe" <<EOF
#!/usr/bin/env bash
case "\$*" in
    *--version*) echo x >> "$work/version.calls"; cat "$1/app.version"; exit \${STUB_VERSION_CODE:-0} ;;
esac
[ ! -f "$1/window.stub" ] || exec bash "$1/window.stub" "\$@"
EOF
    chmod +x "$1/powershell.exe"
}

# check_version <приложение> <плагин>: сверка из копии плагина; код — в version.code, отказ — в version.err.
check_version() {
    plugin_copy "$2"
    rm -rf "$work/stub-version"
    stub_app_version "$work/stub-version" "$1"
    local code=0
    PATH="$work/stub-version:$PATH" timeout 5 bash "$work/plugin/skills/meeting/scripts/check-app-version" \
        > /dev/null 2> "$work/version.err" || code=$?
    echo "$code" > "$work/version.code"
}

appversion_WhenAppOlderThanPlugin_RefusesWithBothVersions() {
    local pair app plugin
    for pair in '0.2.9 0.2.10' '0.1.9 0.2.0' '0.9.9 1.0.0' '0.2.3 0.2.4' '0.2.08 0.2.9'; do
        app=${pair% *} plugin=${pair#* }
        check_version "$app" "$plugin"
        [ "$(cat "$work/version.code")" -eq 4 ] || { echo "$pair: код $(cat "$work/version.code")"; return 1; }
        grep -qF "записи $app старее скилла $plugin." "$work/version.err" && grep -q 'meeting-install' "$work/version.err" ||
            { echo "$pair:"; cat "$work/version.err"; return 1; }
    done
}

appversion_WhenAppSameOrNewerOrDevBuild_Passes() {
    local pair
    for pair in '0.2.4 0.2.4' '0.2.10 0.2.9' '1.0.0 0.9.9' '0.3.0 0.2.9' '0.0.0-dev 0.2.4'; do
        check_version "${pair% *}" "${pair#* }"
        [ "$(cat "$work/version.code")" -eq 0 ] || { echo "$pair: код $(cat "$work/version.code")"; cat "$work/version.err"; return 1; }
        [ -f "$work/version.calls" ] || { echo "$pair: версию не спросили"; return 1; }
    done
}

appversion_WhenVersionNotRecognized_RefusesWithCode4() {
    local app
    for app in '' '0.2' 'meeting 0.2.4' '0.2.4-beta'; do
        check_version "$app" 0.2.4
        [ "$(cat "$work/version.code")" -eq 4 ] || { echo "«$app»: код $(cat "$work/version.code")"; return 1; }
        grep -q 'не распознать' "$work/version.err" || { echo "«$app»:"; cat "$work/version.err"; return 1; }
    done
}

appversion_WhenAnyVersionAllowed_PassesWithoutAsking() {
    plugin_copy 0.2.4
    rm -rf "$work/stub-version"
    stub_app_version "$work/stub-version" 0.1.0

    PATH="$work/stub-version:$PATH" MEETING_ANY_APP_VERSION=1 timeout 5 \
        bash "$work/plugin/skills/meeting/scripts/check-app-version" || return 1
    [ ! -f "$work/version.calls" ] || { echo "версию спросили"; return 1; }
}

appversion_WhenAppNotInstalled_ExitsAsNotInstalled() {
    STUB_VERSION_CODE=3 check_version '' 0.2.4
    [ "$(cat "$work/version.code")" -eq 3 ] || { echo "код: $(cat "$work/version.code")"; return 1; }
}

appversion_WhenVersionQueryFails_RefusesWithCode1() {
    STUB_VERSION_CODE=1 check_version 0.2.4 0.2.4
    [ "$(cat "$work/version.code")" -eq 1 ] || { echo "код: $(cat "$work/version.code")"; return 1; }
    grep -q 'не узнать' "$work/version.err" || { cat "$work/version.err"; return 1; }
}

# Предрелиз плагина (не X.Y.Z) — сравнивать не по чему.
appversion_WhenPluginVersionNotRelease_PassesWithoutAsking() {
    check_version 0.0.1 0.3.0-rc.1
    [ "$(cat "$work/version.code")" -eq 0 ] || { echo "код: $(cat "$work/version.code")"; cat "$work/version.err"; return 1; }
    [ ! -f "$work/version.calls" ] || { echo "версию спросили"; return 1; }
}

# Подключение к идущей встрече версию не сверяет: обновить приложение во время записи нельзя.
dir_WhenPluginNewerThanApp_DoesNotAskVersion() {
    new_meeting
    plugin_copy 0.2.5
    rm -rf "$work/stub-version"
    stub_app_version "$work/stub-version" 0.0.1
    mkdir -p "$work/dir-root"
    current_json "$work/dir-root/current.json" x "$meeting"

    local got
    got=$(PATH="$work/stub-version:$PATH" MEETINGS_ROOT="$work/dir-root" timeout 5 \
        bash "$work/plugin/skills/meeting/scripts/meeting-dir") || return 1
    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
    [ ! -f "$work/version.calls" ] || { echo "версию спросили"; return 1; }
}

# Скилл вне плагина (скрипты без plugin.json тремя каталогами выше) — сравнивать не с чем.
appversion_WhenNoPluginManifest_PassesWithoutAsking() {
    rm -rf "$work/stub-version"
    stub_app_version "$work/stub-version" 0.0.1

    PATH="$work/stub-version:$PATH" timeout 5 bash "$bin/check-app-version" || return 1
    [ ! -f "$work/version.calls" ] || { echo "версию спросили"; return 1; }
}

# Путь разработчика: скилл — ссылкой на каталог скилла в рабочей копии (install-skill.sh).
appversion_WhenSkillLinked_FindsPluginManifest() {
    plugin_copy 0.2.5
    rm -rf "$work/stub-version" "$work/linked"
    stub_app_version "$work/stub-version" 0.2.4
    mkdir -p "$work/linked/skills"
    ln -s "$work/plugin/skills/meeting" "$work/linked/skills/meeting"

    local code=0
    PATH="$work/stub-version:$PATH" timeout 5 bash "$work/linked/skills/meeting/scripts/check-app-version" \
        > /dev/null 2>&1 || code=$?
    [ "$code" -eq 4 ] || { echo "код: $code"; return 1; }
}

start_WhenAppOlderThanPlugin_RefusesBeforeWindow() {
    plugin_copy 0.2.5
    stub_start
    stub_app_version "$work/stub-start" 0.2.4

    local code=0
    PATH="$work/stub-start:$PATH" MEETINGS_ROOT="$work/start-root" MEETING_START_TIMEOUT=5 \
        timeout 15 bash "$work/plugin/skills/meeting/scripts/meeting-start" 'x' > /dev/null 2> "$work/start.err" || code=$?
    [ "$code" -eq 4 ] || { echo "код: $code"; cat "$work/start.err"; return 1; }
    [ ! -f "$work/start.args" ] || { echo "окно открыто"; return 1; }
}

start_WhenOlderAppAllowed_OpensWindow() {
    new_meeting
    plugin_copy 0.2.5
    stub_start
    stub_app_version "$work/stub-start" 0.2.4
    current_json "$work/start.next" new "$meeting"

    local got
    got=$(PATH="$work/stub-start:$PATH" MEETINGS_ROOT="$work/start-root" MEETING_START_TIMEOUT=5 MEETING_ANY_APP_VERSION=1 \
        timeout 15 bash "$work/plugin/skills/meeting/scripts/meeting-start" 'x') || return 1
    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
}

refine_WhenAppOlderThanPlugin_RefusesBeforeWindow() {
    new_meeting
    end_of_recording 0
    plugin_copy 0.2.5
    stub_refine
    stub_app_version "$work/stub-refine" 0.2.4

    local code=0
    PATH="$work/stub-refine:$PATH" timeout 5 bash "$work/plugin/skills/meeting/scripts/meeting-refine" "$meeting" \
        > /dev/null 2> "$work/refine.err" || code=$?
    [ "$code" -eq 4 ] || { echo "код: $code"; cat "$work/refine.err"; return 1; }
    [ ! -f "$work/refine.args" ] || { echo "окно открыто"; return 1; }
}

# Заглушка powershell.exe для meeting-install: пишет адрес релизов и команду, выходит с кодом из setup.code.
stub_setup() {
    rm -rf "$work/stub-setup"
    mkdir -p "$work/stub-setup"
    cat > "$work/stub-setup/powershell.exe" <<EOF
#!/usr/bin/env bash
printf '%s\n' "\$MEETING_RELEASES" > "$work/setup.releases"
printf '%s\n' "\$WSLENV" > "$work/setup.wslenv"
printf '%s\n' "\$*" > "$work/setup.command"
exit \$(cat "$work/setup.code")
EOF
    chmod +x "$work/stub-setup/powershell.exe"
}

setup_WhenRun_RunsReleaseInstallerAndPassesItsExitCode() {
    stub_setup
    echo 7 > "$work/setup.code"

    local code=0
    PATH="$work/stub-setup:$PATH" bash "$bin/meeting-install" > /dev/null 2>&1 || code=$?

    [ "$code" -eq 7 ] || { echo "код: $code"; return 1; }
    [ "$(cat "$work/setup.releases")" = 'https://github.com/juk-777/MeetingAssistant/releases' ] ||
        { cat "$work/setup.releases"; return 1; }
    grep -q 'latest/download/install.ps1' "$work/setup.command" && grep -q -- '-File' "$work/setup.command" &&
        grep -q -- '-ReleaseBase \$releases' "$work/setup.command" || { cat "$work/setup.command"; return 1; }
}

setup_WhenRunFromWsl_PassesReleasesToWindows() {
    stub_setup
    echo 0 > "$work/setup.code"

    PATH="$work/stub-setup:$PATH" WSLENV="USERPROFILE/p" bash "$bin/meeting-install" > /dev/null 2>&1 || return 1

    [[ ":$(cat "$work/setup.wslenv"):" == *:MEETING_RELEASES:* ]] || { cat "$work/setup.wslenv"; return 1; }
}

setup_WhenReleasesGiven_UsesThem() {
    stub_setup
    echo 0 > "$work/setup.code"

    PATH="$work/stub-setup:$PATH" MEETING_RELEASES='D:\releases' bash "$bin/meeting-install" > /dev/null 2>&1 || return 1

    [ "$(cat "$work/setup.releases")" = 'D:\releases' ] || { cat "$work/setup.releases"; return 1; }
}

start_WhenNameBreaksWindowsCommandLine_RefusesWithoutStarting() {
    stub_start
    local name
    for name in 'a"b' 'a%PATH%b' 'a\'; do
        if PATH="$work/stub-start:$PATH" MEETINGS_ROOT="$work/start-root" bash "$bin/meeting-start" "$name" 2> /dev/null; then
            echo "отказа нет: $name"
            return 1
        fi
        [ ! -f "$work/start.args" ] || { echo "запуск был: $name"; return 1; }
    done
}

# expect_hint <ожидаемый текст>: в hints.md ровно одна строка, поля события сохранены, время записи — epoch.
expect_hint() {
    local rows at written number text
    rows=$(wc -l < "$meeting/hints.md")
    [ "$rows" -eq 1 ] || { echo "строк: $rows"; cat "$meeting/hints.md"; return 1; }
    IFS=$'\t' read -r at written number text < "$meeting/hints.md"
    [ "$at" = "1762774262.007" ] && [ "$number" = "7" ] || { echo "событие: $at, строка: $number"; return 1; }
    [[ $written =~ ^[0-9]+\.[0-9]{3}$ ]] || { echo "время записи: $written"; return 1; }
    [ "$text" = "$1" ] || { echo "текст: $text"; return 1; }
}

hint_WhenMultiline_WritesOneRowJoinedWithSlash() {
    new_meeting

    bash "$bin/meeting-hint" "$meeting" 1762774262.007 7 $'Суть подсказки\n\nИсточник: docs/x.md:12\n' || return 1

    expect_hint "Суть подсказки / Источник: docs/x.md:12"
}

hint_WhenTabsInText_ReplacesThemWithSpace() {
    new_meeting

    bash "$bin/meeting-hint" "$meeting" 1762774262.007 7 $'колонка\tвторая' || return 1

    expect_hint "колонка вторая"
}

hint_WhenCarriageReturns_DropsThem() {
    new_meeting

    bash "$bin/meeting-hint" "$meeting" 1762774262.007 7 $'первая\r\nвторая\r' || return 1

    expect_hint "первая / вторая"
}

hint_WhenSilence_WritesDash() {
    new_meeting

    bash "$bin/meeting-hint" "$meeting" 1762774262.007 7 "" || return 1

    expect_hint "-"
}

# Заглушка powershell.exe: записывает, с каким текстом и WSLENV её вызвали, — как его увидит Windows-процесс.
stub_powershell() {
    mkdir -p "$work/stub-ps"
    printf '#!/usr/bin/env bash\nprintf "%%s\\n" "$MEETING_NOTIFY_TEXT" > "%s/notify.text"\nprintf "%%s\\n" "$WSLENV" > "%s/notify.wslenv"\n' \
        "$work" "$work" > "$work/stub-ps/powershell.exe"
    chmod +x "$work/stub-ps/powershell.exe"
}

notify_WhenPowershellPresent_PassesTextThroughWslenv() {
    stub_powershell
    local text='Ответ: '\''да'\'' "нет" $HOME `x`'

    PATH="$work/stub-ps:$PATH" WSLENV="USERPROFILE/p" bash "$bin/meeting-notify" "$text"$'\r' || return 1

    [ "$(cat "$work/notify.text")" = "$text" ] || { echo "текст: $(cat "$work/notify.text")"; return 1; }
    [ "$(cat "$work/notify.wslenv")" = "USERPROFILE/p:MEETING_NOTIFY_TEXT" ] ||
        { echo "WSLENV: $(cat "$work/notify.wslenv")"; return 1; }
}

notify_WhenMultiline_JoinsLinesWithSpace() {
    stub_powershell

    PATH="$work/stub-ps:$PATH" bash "$bin/meeting-notify" $'первая\r\nвторая' || return 1

    [ "$(cat "$work/notify.text")" = "первая вторая" ] || { echo "текст: $(cat "$work/notify.text")"; return 1; }
}

notify_WhenNoWslenvBefore_SetsOnlyItsName() {
    stub_powershell

    PATH="$work/stub-ps:$PATH" WSLENV= bash "$bin/meeting-notify" "текст" || return 1

    [ "$(cat "$work/notify.wslenv")" = "MEETING_NOTIFY_TEXT" ] || { echo "WSLENV: $(cat "$work/notify.wslenv")"; return 1; }
}

# stub_native <каталог> <переменная>: заглушка powershell.exe отдаёт переменную настоящему процессу Windows
# (cmd.exe) и пишет, что тот увидел, в native.out. Git Bash переделывает похожие на путь значения переменных,
# когда запускает программу Windows, — заглушка-скрипт этого не видит, cmd.exe видит.
stub_native() {
    mkdir -p "$1"
    rm -f "$work/native.out"
    printf '#!/usr/bin/env bash\ncmd.exe //c "echo %%%s%%" | tr -d "\\r" > "%s/native.out"\n' "$2" "$work" > "$1/powershell.exe"
    chmod +x "$1/powershell.exe"
}

notify_WhenTextStartsWithSlashInGitBash_ReachesWindowsUnchanged() {
    stub_native "$work/stub-native" MEETING_NOTIFY_TEXT

    PATH="$work/stub-native:$PATH" bash "$bin/meeting-notify" '/api/orders 500' || return 1

    [ "$(cat "$work/native.out")" = '/api/orders 500' ] || { cat "$work/native.out"; return 1; }
}

start_WhenNameStartsWithSlashInGitBash_ReachesWindowsUnchanged() {
    stub_native "$work/stub-native" MEETING_START_NAME
    mkdir -p "$work/start-root"

    PATH="$work/stub-native:$PATH" MEETINGS_ROOT="$work/start-root" MEETING_START_TIMEOUT=1 \
        timeout 15 bash "$bin/meeting-start" '/retro' > /dev/null 2>&1

    [ "$(cat "$work/native.out")" = '/retro' ] || { cat "$work/native.out"; return 1; }
}

notify_WhenNoPowershell_ExitsQuietly() {
    mkdir -p "$work/no-ps"
    local output

    output=$(PATH="$work/no-ps" /bin/bash "$bin/meeting-notify" "текст" 2>&1) || { echo "отказ: $output"; return 1; }

    [ -z "$output" ] || { echo "вывод: $output"; return 1; }
}

repo="$(cd "$(dirname "$0")/../.." && pwd)"
install="$repo/scripts/install-skill.sh"
skill_dir="$(cd "$repo/plugins/meeting/skills/meeting" && pwd -P)"

# Установка — в свой каталог настроек Claude Code, чтобы не тронуть настоящий.
install_into() {
    CLAUDE_CONFIG_DIR="$1" "$install" > "$work/install.log" 2>&1
}

install_WhenEmpty_LinksSkillAndRepeatIsNoOp() {
    local config="$work/config-empty"

    install_into "$config" || { cat "$work/install.log"; return 1; }
    install_into "$config" || { cat "$work/install.log"; return 1; }

    [ -L "$config/skills/meeting" ] || { echo "ссылки нет"; return 1; }
    [ "$(readlink -f "$config/skills/meeting")" = "$skill_dir" ] || { readlink -f "$config/skills/meeting"; return 1; }
}

install_WhenRepositoryOpenedThroughLink_RepeatIsNoOp() {
    local config="$work/config-linked"
    ln -s "$repo" "$work/repo-link"

    CLAUDE_CONFIG_DIR="$config" "$work/repo-link/scripts/install-skill.sh" > "$work/install.log" 2>&1 ||
        { cat "$work/install.log"; return 1; }
    CLAUDE_CONFIG_DIR="$config" "$work/repo-link/scripts/install-skill.sh" > "$work/install.log" 2>&1 ||
        { cat "$work/install.log"; return 1; }
}

install_WhenTargetIsDirectory_RefusesAndKeepsIt() {
    local config="$work/config-taken"
    mkdir -p "$config/skills/meeting"
    touch "$config/skills/meeting/own.md"

    if install_into "$config"; then
        echo "отказа нет"
        return 1
    fi
    [ -f "$config/skills/meeting/own.md" ] && [ ! -L "$config/skills/meeting" ] || { echo "каталог тронут"; return 1; }
}

install_WhenLinkedElsewhere_RefusesAndKeepsLink() {
    local config="$work/config-other"
    mkdir -p "$config/skills" "$work/other-skill"
    ln -s "$work/other-skill" "$config/skills/meeting"

    if install_into "$config"; then
        echo "отказа нет"
        return 1
    fi
    [ "$(readlink -f "$config/skills/meeting")" = "$work/other-skill" ] || { echo "ссылка тронута"; return 1; }
}

# Скрипты выпуска и моделей репозитория — те же bash-тесты: отдельного набора ради нескольких скриптов нет.
# grep -P отказывает без локали UTF-8, а неинтерактивный Git Bash её не задаёт.
models_WhenChecksumsListedInSeveralScripts_TheyMatch() {
    local -x LC_ALL=C.UTF-8
    local name ps sh linux
    for name in vad recognizer; do
        ps=$(grep -oP "^\\\$${name}Sha256 = '\K[0-9a-f]{64}" "$repo/scripts/install.ps1")
        sh=$(grep -oP "^${name}_sha256=\K[0-9a-f]{64}" "$repo/scripts/download-models.sh")
        linux=$(grep -oP "^${name}_sha256=\K[0-9a-f]{64}" "$repo/scripts/install.sh")
        [ -n "$ps" ] && [ "$ps" = "$sh" ] && [ "$ps" = "$linux" ] ||
            { echo "$name: install.ps1 '$ps', download-models.sh '$sh', install.sh '$linux'"; return 1; }
    done
    ps=$(grep -oP "^\\\$whisperSha256 = '\K[0-9a-f]{64}" "$repo/scripts/install.ps1")
    linux=$(grep -oP "^whisper_sha256=\K[0-9a-f]{64}" "$repo/scripts/install.sh")
    [ -n "$ps" ] && [ "$ps" = "$linux" ] || { echo "whisper: install.ps1 '$ps', install.sh '$linux'"; return 1; }
}

models_WhenWhisperFileNamedInSeveralPlaces_NamesMatch() {
    local -x LC_ALL=C.UTF-8
    local code ps linux refine
    code=$(grep -oP 'WhisperFile = "\K[^"]+' "$repo/src/MeetingAssistant.Infrastructure/Speech/SpeechOptions.cs")
    ps=$(grep -oP "^\\\$whisperFile = '\K[^']+" "$repo/scripts/install.ps1")
    linux=$(grep -oP '^whisper_file=\K\S+' "$repo/scripts/install.sh")
    refine=$(grep -oP '^whisper_file=\K\S+' "$repo/plugins/meeting/skills/meeting/scripts/meeting-refine")
    [ -n "$code" ] && [ "$code" = "$ps" ] && [ "$code" = "$linux" ] && [ "$code" = "$refine" ] ||
        { echo "SpeechModels '$code', install.ps1 '$ps', install.sh '$linux', meeting-refine '$refine'"; return 1; }
}

# Приложение проверяет на старте те файлы модели, которые знает SpeechModels, а ставят их install.ps1, install.sh
# и download-models.sh (CI берёт модели последним): расхождение со скриптами установки тесты C# не видят.
models_WhenRecognizerNamedInSeveralPlaces_NamesMatch() {
    local -x LC_ALL=C.UTF-8
    local options="$repo/src/MeetingAssistant.Infrastructure/Speech/SpeechOptions.cs"
    local code ps sh linux code_files ps_files sh_files linux_files
    code=$(grep -oP 'RecognizerDirectory = "\K[^"]+' "$options")
    ps=$(grep -oP "^\\\$recognizerDirectory = '\K[^']+" "$repo/scripts/install.ps1")
    sh=$(grep -oP '^recognizer=\K\S+' "$repo/scripts/download-models.sh")
    linux=$(grep -oP '^recognizer=\K\S+' "$repo/scripts/install.sh")
    code_files=$(grep -oP 'Recognizer\w+File = "\K[^"]+' "$options" | sort | paste -sd' ')
    ps_files=$(grep '^\$modelFiles = ' "$repo/scripts/install.ps1" | grep -oP '\\\K[^"\\]+(?=")' | sort | paste -sd' ')
    sh_files=$(grep -oP '"\$recognizer/\K[^"]+' "$repo/scripts/download-models.sh" | sort -u | paste -sd' ')
    linux_files=$(grep '^model_files=' "$repo/scripts/install.sh" | grep -oP '"\$recognizer/\K[^"]+' | sort | paste -sd' ')
    [ -n "$code" ] && [ "$code" = "$ps" ] && [ "$code" = "$sh" ] && [ "$code" = "$linux" ] ||
        { echo "каталог: SpeechModels '$code', install.ps1 '$ps', download-models.sh '$sh', install.sh '$linux'"; return 1; }
    [ -n "$code_files" ] && [ "$code_files" = "$ps_files" ] && [ "$code_files" = "$sh_files" ] &&
        [ "$code_files" = "$linux_files" ] ||
        { echo "файлы: SpeechModels '$code_files', install.ps1 '$ps_files', download-models.sh '$sh_files'," \
            "install.sh '$linux_files'"; return 1; }
}

# install.sh — на подменённом домашнем каталоге и релизе в каталоге. Модели уже стоят: установка их не качает.
linuxsetup_home() {
    home=$(mktemp -d "$work/home.XXXX")
    local -x LC_ALL=C.UTF-8
    local models="$home/.local/share/meeting-assistant/models" recognizer
    recognizer=$(grep -oP '^recognizer=\K\S+' "$repo/scripts/install.sh")
    mkdir -p "$models/$recognizer"
    touch "$models/silero_vad.onnx" "$models/$recognizer/model.int8.onnx" "$models/$recognizer/tokens.txt" \
        "$models/$(grep -oP '^whisper_file=\K\S+' "$repo/scripts/install.sh")"
}

# linuxsetup_release <версия> [тело заглушки]: архив с заглушкой meeting и его сумма. Без тела заглушка на любой
# вызов печатает версию.
linuxsetup_release() {
    local stage="$work/release-stage"
    rm -rf "$stage" "$work/release" && mkdir -p "$stage" "$work/release"
    printf '#!/usr/bin/env bash\n%s\n' "${2:-echo $1}" > "$stage/meeting"
    chmod +x "$stage/meeting"
    tar -czf "$work/release/meeting-linux-x64.tar.gz" -C "$stage" .
    (cd "$work/release" && sha256sum meeting-linux-x64.tar.gz > meeting-linux-x64.tar.gz.sha256)
}

linuxsetup_run() {
    env -u XDG_DATA_HOME -u Storage__MeetingsRoot HOME="$home" \
        bash "$repo/scripts/install.sh" --release-base "$work/release" "$@" > "$work/setup.log" 2>&1
}

# linuxsetup_profile <переменная>: значение после входа в систему — ~/.profile, прочитанный чистой оболочкой.
linuxsetup_profile() {
    env -i HOME="$home" PATH=/usr/bin:/bin bash -c '. "$HOME/.profile"; printf "%s" "${!1}"' _ "$1"
}

linuxsetup_WhenReleaseInDirectory_InstallsAppLinkAndProfileVariables() {
    linuxsetup_home
    linuxsetup_release 1.0.0
    printf 'строка пользователя\n' > "$home/.profile"
    linuxsetup_run || { cat "$work/setup.log"; return 1; }
    [ "$(env -i HOME="$home" PATH="$(linuxsetup_profile PATH)" bash -c 'meeting --version')" = 1.0.0 ] ||
        { echo "meeting по PATH из ~/.profile не отвечает версией"; return 1; }
    [ "$(linuxsetup_profile Storage__MeetingsRoot)" = "$home/Meetings" ] && [ -d "$home/Meetings" ] ||
        { echo "корень встреч: '$(linuxsetup_profile Storage__MeetingsRoot)'"; return 1; }
    [ "$(linuxsetup_profile Speech__ModelsDirectory)" = "$home/.local/share/meeting-assistant/models" ] ||
        { echo "модели: '$(linuxsetup_profile Speech__ModelsDirectory)'"; return 1; }
    [ "$(head -1 "$home/.profile")" = 'строка пользователя' ] || { echo "строка пользователя потеряна"; return 1; }
}

linuxsetup_WhenRepeatedWithNewRelease_ReplacesAppKeepsRootAndOneBlock() {
    linuxsetup_home
    linuxsetup_release 1.0.0
    linuxsetup_run --meetings-root "$work/встречи с пробелом" || { cat "$work/setup.log"; return 1; }
    linuxsetup_release 2.0.0
    linuxsetup_run || { cat "$work/setup.log"; return 1; }
    [ "$("$home/.local/bin/meeting" --version)" = 2.0.0 ] || { echo "app не заменён"; return 1; }
    [ "$(linuxsetup_profile Storage__MeetingsRoot)" = "$work/встречи с пробелом" ] ||
        { echo "корень встреч: '$(linuxsetup_profile Storage__MeetingsRoot)'"; return 1; }
    [ "$(grep -c '^# >>> meeting-assistant >>>$' "$home/.profile")" = 1 ] || { cat "$home/.profile"; return 1; }
}

linuxsetup_WhenArchiveChecksumWrong_RefusesAndKeepsInstalledApp() {
    linuxsetup_home
    linuxsetup_release 1.0.0
    linuxsetup_run || { cat "$work/setup.log"; return 1; }
    linuxsetup_release 2.0.0
    printf '%064d  meeting-linux-x64.tar.gz\n' 0 > "$work/release/meeting-linux-x64.tar.gz.sha256"
    if linuxsetup_run; then
        echo "отказа нет"
        return 1
    fi
    grep -q 'Контрольная сумма' "$work/setup.log" || { cat "$work/setup.log"; return 1; }
    [ "$("$home/.local/bin/meeting" --version)" = 1.0.0 ] || { echo "установленный app тронут"; return 1; }
}

linuxsetup_WhenForeignFileHoldsLinkName_RefusesAndKeepsIt() {
    linuxsetup_home
    linuxsetup_release 1.0.0
    mkdir -p "$home/.local/bin"
    printf 'чужой\n' > "$home/.local/bin/meeting"
    if linuxsetup_run; then
        echo "отказа нет"
        return 1
    fi
    [ "$(cat "$home/.local/bin/meeting")" = 'чужой' ] || { echo "чужой файл тронут"; return 1; }
}

linuxsetup_WhenForeignLinkHoldsLinkName_RefusesAndKeepsIt() {
    linuxsetup_home
    linuxsetup_release 1.0.0
    mkdir -p "$home/.local/bin"
    ln -s "$work/своя-сборка" "$home/.local/bin/meeting"
    if linuxsetup_run; then
        echo "отказа нет"
        return 1
    fi
    [ "$(readlink "$home/.local/bin/meeting")" = "$work/своя-сборка" ] || { echo "чужая ссылка тронута"; return 1; }
    [ ! -e "$home/.local/share/meeting-assistant/app" ] || { echo "app поставлен до отказа"; return 1; }
}

linuxsetup_WhenProfileBlockHasNoEnd_RefusesAndKeepsProfile() {
    linuxsetup_home
    linuxsetup_release 1.0.0
    printf 'до\n# >>> meeting-assistant >>>\nexport X=1\nстрока пользователя\n' > "$home/.profile"
    cp "$home/.profile" "$work/profile.before"
    if linuxsetup_run; then
        echo "отказа нет"
        return 1
    fi
    cmp -s "$home/.profile" "$work/profile.before" || { echo "~/.profile тронут"; return 1; }
}

# Корень с кириллицей и кавычкой, установка в локали C: значение читает и sh (dash), не только bash.
linuxsetup_WhenRootNotAsciiInCLocale_ShReadsIt() {
    linuxsetup_home
    linuxsetup_release 1.0.0
    local root="$work/встречи 'и кавычка'" got
    LC_ALL=C linuxsetup_run --meetings-root "$root" || { cat "$work/setup.log"; return 1; }
    got=$(env -i HOME="$home" sh -c '. "$HOME/.profile"; printf "%s" "$Storage__MeetingsRoot"')
    [ "$got" = "$root" ] || { echo "sh прочёл: '$got'"; return 1; }
}

# Модели не стоят и качаются из каталога-подмены: файл с неверной суммой не ставится, app не тронут.
linuxsetup_WhenModelChecksumWrong_RefusesBeforeApp() {
    home=$(mktemp -d "$work/home.XXXX")
    linuxsetup_release 1.0.0
    mkdir -p "$work/model-release"
    printf 'не модель\n' > "$work/model-release/silero_vad.onnx"
    if MEETING_MODEL_RELEASE="$work/model-release" linuxsetup_run; then
        echo "отказа нет"
        return 1
    fi
    grep -q 'Контрольная сумма silero_vad.onnx' "$work/setup.log" || { cat "$work/setup.log"; return 1; }
    [ ! -e "$home/.local/share/meeting-assistant/app" ] || { echo "app поставлен"; return 1; }
}

linuxsetup_WhenWhisperChecksumWrong_RefusesAndDoesNotPlaceIt() {
    linuxsetup_home
    linuxsetup_release 1.0.0
    local whisper
    whisper=$(grep -oP '^whisper_file=\K\S+' "$repo/scripts/install.sh")
    rm "$home/.local/share/meeting-assistant/models/$whisper"
    mkdir -p "$work/whisper-source"
    printf 'не модель\n' > "$work/whisper-source/$whisper"
    if MEETING_WHISPER_SOURCE="$work/whisper-source" linuxsetup_run; then
        echo "отказа нет"
        return 1
    fi
    grep -q "Контрольная сумма $whisper" "$work/setup.log" || { cat "$work/setup.log"; return 1; }
    [ ! -e "$home/.local/share/meeting-assistant/models/$whisper" ] || { echo "Whisper поставлен"; return 1; }
}

# Скилл на Linux. PATH — каталог ссылок на программы машины без powershell.exe, эмуляторов терминала и notify-send
# (WSL видит Windows, у разработчика на Linux есть настоящий терминал), впереди — заглушки теста.
linux_bin() {
    [ ! -d "$work/linux-bin" ] || return 0
    mkdir -p "$work/linux-bin"
    local dir file name
    local IFS=:
    for dir in $PATH; do
        [[ $dir != /mnt/* ]] || continue
        for file in "$dir"/*; do
            name=${file##*/}
            case $name in
                powershell.exe | x-terminal-emulator | gnome-terminal | konsole | xfce4-terminal | xterm | notify-send) continue ;;
            esac
            if [ -x "$file" ] && [ ! -d "$file" ] && [ ! -e "$work/linux-bin/$name" ]; then
                ln -s "$file" "$work/linux-bin/$name"
            fi
        done
    done
}

# linux_skill <каталог заглушек> <скрипт или путь> <аргументы…>: скрипт скилла как на Linux, домашний каталог — $home.
linux_skill() {
    local stubs=$1 script=$2
    shift 2
    [[ $script == */* ]] || script="$bin/$script"
    linux_bin
    mkdir -p "$stubs"
    env -u XDG_DATA_HOME -u Storage__MeetingsRoot -u Speech__ModelsDirectory -u MEETINGS_ROOT \
        HOME="$home" PATH="$stubs:$work/linux-bin" bash "$script" "$@"
}

# linux_installed: install.sh ставит в $home заглушку meeting. На --version она отвечает 9.9.9, на остальное пишет
# аргументы и настройки в linux-meeting.* и кладёт приготовленный start.next как current.json корня встреч.
linux_installed() {
    linuxsetup_home
    linuxsetup_release 9.9.9 "$(cat <<EOF
[ "\$1" != --version ] || { echo 9.9.9; exit 0; }
printf '%s\n' "\$@" > "$work/linux-meeting.args"
printf '%s\n%s\n' "\$Storage__MeetingsRoot" "\$Speech__ModelsDirectory" > "$work/linux-meeting.env"
[ ! -f "$work/start.next" ] || cp "$work/start.next" "\$Storage__MeetingsRoot/current.json"
EOF
)"
    rm -rf "$work/linux-meeting.args" "$work/linux-meeting.env" "$work/start.next" "$work/linux-root" \
        "$work/terminal.used" "$work/stub-term"
    linuxsetup_run --meetings-root "$work/linux-root" || { cat "$work/setup.log"; return 1; }
}

# stub_terminal <каталог> <эмулятор> <ключ>: принимает команду после своего ключа и исполняет её, как окно.
stub_terminal() {
    mkdir -p "$1"
    cat > "$1/$2" <<EOF
#!/usr/bin/env bash
[ "\$1" = '$3' ] || { echo "ключ \$1 вместо $3" >&2; exit 9; }
shift
echo '$2' > "$work/terminal.used"
"\$@"
EOF
    chmod +x "$1/$2"
}

linuxdir_WhenInstalledByInstallSh_FindsItsRootModelsAndApp() {
    linux_installed || return 1
    local got
    got=$(linux_skill "$work/stub-none" meeting-dir --root) || return 1
    [ "$got" = "$work/linux-root" ] || { echo "корень: $got"; return 1; }
    got=$(linux_skill "$work/stub-none" linux-setting models) || return 1
    [ "$got" = "$home/.local/share/meeting-assistant/models" ] || { echo "модели: $got"; return 1; }
    got=$(linux_skill "$work/stub-none" linux-setting app) || return 1
    [ "$("$got" --version)" = 9.9.9 ] || { echo "meeting: $got"; return 1; }
}

linuxdir_WhenProfileHasNoBlock_TakesEnvironment() {
    home=$(mktemp -d "$work/home.XXXX")
    linux_bin
    local got
    got=$(env -u MEETINGS_ROOT HOME="$home" PATH="$work/linux-bin" Storage__MeetingsRoot="$work/env-root" \
        bash "$bin/meeting-dir" --root) || return 1
    [ "$got" = "$work/env-root" ] || { echo "корень: $got"; return 1; }
}

linuxdir_WhenNothingInstalled_ExitsAsNotInstalled() {
    home=$(mktemp -d "$work/home.XXXX")
    local code=0
    linux_skill "$work/stub-none" meeting-dir --root > /dev/null 2>&1 || code=$?
    [ "$code" -eq 3 ] || { echo "код: $code"; return 1; }
}

linuxstart_WhenNewMeetingAppears_PassesNameAsIsAndPrintsIt() {
    linux_installed || return 1
    new_meeting
    current_json "$work/start.next" new "$meeting"
    stub_terminal "$work/stub-term" x-terminal-emulator -e
    printf 'термин\n' > "$work/словарь.txt"
    local name='Встреча "с" $HOME и `id`' got
    got=$(cd "$work" && MEETING_START_TIMEOUT=5 linux_skill "$work/stub-term" meeting-start "$name" 'словарь.txt') ||
        return 1
    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
    [ "$(cat "$work/linux-meeting.args")" = "$(printf '%s\n' start "$name" --glossary "$work/словарь.txt")" ] ||
        { cat "$work/linux-meeting.args"; return 1; }
    [ "$(cat "$work/linux-meeting.env")" = "$(printf '%s\n' "$work/linux-root" "$home/.local/share/meeting-assistant/models")" ] ||
        { cat "$work/linux-meeting.env"; return 1; }
}

linuxstart_WhenOnlyOneTerminal_OpensItWithItsKey() {
    linux_installed || return 1
    local pair terminal number=0
    for pair in 'x-terminal-emulator -e' 'gnome-terminal --' 'konsole -e' 'xfce4-terminal -x' 'xterm -e'; do
        terminal=${pair% *}
        number=$((number + 1))
        new_meeting
        current_json "$work/start.next" "new$number" "$meeting"
        rm -rf "$work/stub-term" "$work/terminal.used"
        stub_terminal "$work/stub-term" "$terminal" "${pair#* }"
        MEETING_START_TIMEOUT=5 linux_skill "$work/stub-term" meeting-start 'x' > /dev/null 2> "$work/start.err" ||
            { echo "$terminal:"; cat "$work/start.err"; return 1; }
        [ "$(cat "$work/terminal.used")" = "$terminal" ] || { echo "$terminal: открыт $(cat "$work/terminal.used")"; return 1; }
    done
}

linuxstart_WhenSeveralTerminals_TakesSystemChoiceFirst() {
    linux_installed || return 1
    new_meeting
    current_json "$work/start.next" new "$meeting"
    stub_terminal "$work/stub-term" gnome-terminal --
    stub_terminal "$work/stub-term" x-terminal-emulator -e
    MEETING_START_TIMEOUT=5 linux_skill "$work/stub-term" meeting-start 'x' > /dev/null || return 1
    [ "$(cat "$work/terminal.used")" = x-terminal-emulator ] || { cat "$work/terminal.used"; return 1; }
}

linuxstart_WhenNoTerminal_RefusesWithCommandToRun() {
    linux_installed || return 1
    if MEETING_START_TIMEOUT=2 linux_skill "$work/stub-none" meeting-start 'x' > /dev/null 2> "$work/start.err"; then
        echo "отказа нет"
        return 1
    fi
    grep -qF "$home/.local/share/meeting-assistant/app/meeting start x" "$work/start.err" ||
        { cat "$work/start.err"; return 1; }
    [ ! -f "$work/linux-meeting.args" ] || { echo "meeting запущен"; return 1; }
}

linuxstart_WhenAppNotInstalled_ExitsAsNotInstalled() {
    linux_installed || return 1
    rm -rf "$home/.local/share/meeting-assistant/app"
    stub_terminal "$work/stub-term" x-terminal-emulator -e
    local code=0
    MEETING_START_TIMEOUT=2 linux_skill "$work/stub-term" meeting-start 'x' > /dev/null 2>&1 || code=$?
    [ "$code" -eq 3 ] || { echo "код: $code"; return 1; }
}

linuxrefine_WhenMeetingFinished_OpensWindowForItAndPrintsIt() {
    linux_installed || return 1
    new_meeting
    printf '1\t1762774261.000\tконец записи\n' > "$meeting/events.log"
    stub_terminal "$work/stub-term" x-terminal-emulator -e
    local got
    got=$(linux_skill "$work/stub-term" meeting-refine "$meeting") || return 1
    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
    [ "$(cat "$work/linux-meeting.args")" = "$(printf '%s\n' refine "$meeting")" ] ||
        { cat "$work/linux-meeting.args"; return 1; }
}

linuxrefine_WhenWhisperMissing_ExitsAsNotInstalledWithoutWindow() {
    linux_installed || return 1
    rm "$home/.local/share/meeting-assistant/models/$(grep -oP '^whisper_file=\K\S+' "$repo/scripts/install.sh")"
    new_meeting
    printf '1\t1762774261.000\tконец записи\n' > "$meeting/events.log"
    stub_terminal "$work/stub-term" x-terminal-emulator -e
    local code=0
    linux_skill "$work/stub-term" meeting-refine "$meeting" > /dev/null 2>&1 || code=$?
    [ "$code" -eq 3 ] || { echo "код: $code"; return 1; }
    [ ! -f "$work/terminal.used" ] || { echo "окно открыто"; return 1; }
}

linuxappversion_WhenInstalledAppOlderThanPlugin_RefusesWithCode4() {
    linux_installed || return 1
    local plugin code
    for plugin in 10.0.0 9.9.9 1.0.0; do
        plugin_copy "$plugin"
        code=0
        linux_skill "$work/stub-none" "$work/plugin/skills/meeting/scripts/check-app-version" > /dev/null 2>&1 || code=$?
        if [ "$plugin" = 10.0.0 ]; then
            [ "$code" -eq 4 ] || { echo "$plugin: код $code"; return 1; }
        else
            [ "$code" -eq 0 ] || { echo "$plugin: код $code"; return 1; }
        fi
    done
}

linuxappversion_WhenAppNotInstalled_ExitsAsNotInstalled() {
    home=$(mktemp -d "$work/home.XXXX")
    plugin_copy 1.0.0
    local code=0
    linux_skill "$work/stub-none" "$work/plugin/skills/meeting/scripts/check-app-version" > /dev/null 2>&1 || code=$?
    [ "$code" -eq 3 ] || { echo "код: $code"; return 1; }
}

linuxinstall_WhenReleasesInDirectory_RunsTheirInstallSh() {
    linuxsetup_home
    linuxsetup_release 1.2.3
    cp "$repo/scripts/install.sh" "$work/release/"
    MEETING_RELEASES="$work/release" linux_skill "$work/stub-none" meeting-install > "$work/setup.log" 2>&1 ||
        { cat "$work/setup.log"; return 1; }
    [ "$("$home/.local/bin/meeting" --version)" = 1.2.3 ] || { echo "meeting не поставлен"; return 1; }
}

linuxnotify_WhenNotifySendPresent_PassesTitleAndJoinedText() {
    home=$(mktemp -d "$work/home.XXXX")
    mkdir -p "$work/stub-notify"
    printf '#!/usr/bin/env bash\nprintf "%%s\\n" "$@" > "%s/notify.args"\n' "$work" > "$work/stub-notify/notify-send"
    chmod +x "$work/stub-notify/notify-send"
    linux_skill "$work/stub-notify" meeting-notify $'-первая\nвторая' || return 1
    [ "$(tail -3 "$work/notify.args")" = "$(printf '%s\n' -- MeetingAssistant '-первая вторая')" ] ||
        { cat "$work/notify.args"; return 1; }
}

linuxnotify_WhenNoNotifySend_ExitsQuietly() {
    home=$(mktemp -d "$work/home.XXXX")
    local output
    output=$(linux_skill "$work/stub-none" meeting-notify 'текст' 2>&1) || { echo "отказ: $output"; return 1; }
    [ -z "$output" ] || { echo "вывод: $output"; return 1; }
}

release_WhenPluginVersionMatchesTag_PrintsVersion() {
    local version got
    for version in 1.2.3 1.2.3-rc.1; do
        printf '{\n  "name": "meeting",\n  "version": "%s",\n  "keywords": ["x"]\n}\n' "$version" > "$work/plugin.json"
        got=$(bash "$repo/scripts/release-version.sh" "v$version" "$work/plugin.json") || return 1
        [ "$got" = "$version" ] || { echo "получено: $got"; return 1; }
    done
}

release_WhenPluginVersionDiffers_Refuses() {
    printf '{ "name": "meeting", "version": "1.2.2" }\n' > "$work/plugin.json"
    if bash "$repo/scripts/release-version.sh" v1.2.3 "$work/plugin.json" > /dev/null 2> "$work/release.err"; then
        echo "отказа нет"
        return 1
    fi
    grep -q 'не совпадает' "$work/release.err" || { cat "$work/release.err"; return 1; }
}

# Так шаг выпуска зовёт CI: без второго аргумента, манифест — плагин репозитория.
release_WhenManifestNotGiven_ReadsRepositoryPlugin() {
    local version got
    version=$(grep -oE '"version": *"[^"]+"' "$repo/plugins/meeting/.claude-plugin/plugin.json" | grep -oE '[0-9][^"]*')
    got=$(cd "$work" && bash "$repo/scripts/release-version.sh" "v$version") || return 1
    [ "$got" = "$version" ] || { echo "получено: $got"; return 1; }
}

release_WhenManifestMissing_RefusesNamingIt() {
    if bash "$repo/scripts/release-version.sh" v1.2.3 "$work/нет.json" > /dev/null 2> "$work/release.err"; then
        echo "отказа нет"
        return 1
    fi
    grep -q 'Нет манифеста' "$work/release.err" || { cat "$work/release.err"; return 1; }
}

release_WhenPluginHasNoVersion_RefusesAsMismatch() {
    printf '{ "name": "meeting" }\n' > "$work/plugin.json"
    if bash "$repo/scripts/release-version.sh" v1.2.3 "$work/plugin.json" > /dev/null 2> "$work/release.err"; then
        echo "отказа нет"
        return 1
    fi
    grep -q 'не совпадает' "$work/release.err" || { cat "$work/release.err"; return 1; }
}

release_WhenTagIsNotVersion_Refuses() {
    printf '{ "name": "meeting", "version": "1.2.3" }\n' > "$work/plugin.json"
    local tag
    for tag in 1.2.3 v1.2 release-1.2.3; do
        if bash "$repo/scripts/release-version.sh" "$tag" "$work/plugin.json" > /dev/null 2>&1; then
            echo "отказа нет: $tag"
            return 1
        fi
    done
}

for test in $(declare -F | awk '{ print $3 }' | grep -E '^[a-z]+_When'); do
    run "$test"
done

echo "итого: $((passed + failed)), упало: $failed, пропущено: $skipped"
[ "$passed" -gt 0 ] || { echo "ни одного прогнанного теста"; exit 1; }
[ "$failed" -eq 0 ]
