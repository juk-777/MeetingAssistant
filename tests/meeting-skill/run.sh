#!/usr/bin/env bash
# Тесты скриптов скилла /meeting на синтетических файлах встречи во временном каталоге.
#
# Запускается из scripts/test.sh вместе с тестовыми приложениями .NET; итог — строка «итого: N, упало: M».
set -uo pipefail

work=$(mktemp -d)
export XDG_STATE_HOME="$work/state"

# Скрипты — копией без бита исполнения: так их получает плагин, установленный в кеш Claude Code, и скилл зовёт их
# через bash. Скрипт, который зовёт соседа напрямую, здесь падает.
bin="$work/scripts"
cp -r "$(dirname "$0")/../../plugins/meeting/skills/meeting/scripts" "$bin"
chmod -x "$bin"/*

# Предел ожидания вывода: скрипт, который не отдал блок, роняет тест, а не вешает прогон.
LIMIT_SECONDS=5

watchers=()
passed=0
failed=0

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

# Тест идёт в этой же оболочке, а не в подоболочке: иначе его фоновые meeting-watch не остановить.
run() {
    local name=$1
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

# Заглушка wslpath: переводит любой путь в каталог встречи и пишет, что её вызвали.
stub_wslpath() {
    mkdir -p "$work/stub"
    printf '#!/usr/bin/env bash\necho "$*" >> "%s/wslpath.calls"\necho "%s"\n' "$work" "$meeting" > "$work/stub/wslpath"
    chmod +x "$work/stub/wslpath"
}

dir_WhenCurrentHasWindowsPath_TranslatesIt() {
    new_meeting
    stub_wslpath
    mkdir -p "$work/root"
    printf '{ "directory": "D:\\\\meetings\\\\2026-11-10_10-00-00_x" }\n' > "$work/root/current.json"

    local got
    got=$(PATH="$work/stub:$PATH" MEETINGS_ROOT="$work/root" bash "$bin/meeting-dir") || return 1

    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
    grep -qxF -- '-u D:\meetings\2026-11-10_10-00-00_x' "$work/wslpath.calls" || { cat "$work/wslpath.calls"; return 1; }
}

dir_WhenCurrentHasPosixPath_TakesItAsIs() {
    new_meeting
    mkdir -p "$work/posix-root"
    jq -n --arg d "$meeting" '{ directory: $d }' > "$work/posix-root/current.json"

    local got
    got=$(MEETINGS_ROOT="$work/posix-root" bash "$bin/meeting-dir") || return 1

    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
}

dir_WhenArgumentGiven_TranslatesWindowsPathWithoutCurrent() {
    new_meeting
    stub_wslpath

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
    rm -f "$work/wslpath.calls"
    mkdir -p "$work/stub" "$work/registry-root"
    printf '#!/usr/bin/env bash\necho "$*" >> "%s/wslpath.calls"\necho "%s/registry-root"\n' "$work" "$work" > "$work/stub/wslpath"
    chmod +x "$work/stub/wslpath"
    jq -n --arg d "$meeting" '{ directory: $d }' > "$work/registry-root/current.json"

    local got
    got=$(PATH="$work/stub-reg:$work/stub:$PATH" MEETINGS_ROOT="" bash "$bin/meeting-dir") || return 1

    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
    grep -qxF -- '-u D:\Мои встречи' "$work/wslpath.calls" || { cat "$work/wslpath.calls"; return 1; }
}

dir_WhenMeetingsRootSet_PrefersItOverRegistry() {
    new_meeting
    stub_reg "$work/registry-only"
    mkdir -p "$work/registry-only" "$work/own-root"
    echo '{ "directory": "/nonexistent" }' > "$work/registry-only/current.json"
    jq -n --arg d "$meeting" '{ directory: $d }' > "$work/own-root/current.json"

    local got
    got=$(PATH="$work/stub-reg:$PATH" MEETINGS_ROOT="$work/own-root" bash "$bin/meeting-dir") || return 1

    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
}

dir_WhenRootIsWindowsPath_TranslatesIt() {
    new_meeting
    mkdir -p "$work/stub"
    printf '#!/usr/bin/env bash\necho "%s/win-root"\n' "$work" > "$work/stub/wslpath"
    chmod +x "$work/stub/wslpath"
    mkdir -p "$work/win-root"
    jq -n --arg d "$meeting" '{ directory: $d }' > "$work/win-root/current.json"

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
    jq -n --arg d "$meeting" '{ id: "new", directory: $d }' > "$work/start.next"

    local got
    got=$(PATH="$work/stub-start:$PATH" MEETINGS_ROOT="$work/start-root" MEETING_START_TIMEOUT=5 \
        timeout 15 bash "$bin/meeting-start" 'Встреча с партнёром') || return 1

    [ "$got" = "$meeting" ] || { echo "получено: $got"; return 1; }
    [ "$(head -1 "$work/start.args")" = 'Встреча с партнёром' ] || { cat "$work/start.args"; return 1; }

    # Окну передан тот корень, где ждали встречу, — в виде, понятном Windows.
    local root="$work/start-root"
    ! command -v wslpath > /dev/null || root=$(wslpath -w "$root")
    [ "$(sed -n 3p "$work/start.args")" = "$root" ] || { cat "$work/start.args"; return 1; }
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

start_WhenGlossaryGiven_PassesItAsWindowsPath() {
    new_meeting
    stub_start
    jq -n --arg d "$meeting" '{ id: "new", directory: $d }' > "$work/start.next"
    printf '#!/usr/bin/env bash\necho "D:\\\\glossary.txt"\n' > "$work/stub-start/wslpath"
    chmod +x "$work/stub-start/wslpath"
    touch "$work/glossary.txt"

    PATH="$work/stub-start:$PATH" MEETINGS_ROOT="$work/start-root" MEETING_START_TIMEOUT=5 \
        timeout 15 bash "$bin/meeting-start" 'x' "$work/glossary.txt" > /dev/null || return 1

    [ "$(sed -n 2p "$work/start.args")" = 'D:\glossary.txt' ] || { cat "$work/start.args"; return 1; }
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

# Заглушка powershell.exe для meeting-install: пишет адрес релизов и команду, выходит с кодом из setup.code.
stub_setup() {
    rm -rf "$work/stub-setup"
    mkdir -p "$work/stub-setup"
    cat > "$work/stub-setup/powershell.exe" <<EOF
#!/usr/bin/env bash
printf '%s\n' "\$MEETING_RELEASES" > "$work/setup.releases"
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
    grep -q 'latest/download/install.ps1' "$work/setup.command" && grep -q -- '-File' "$work/setup.command" ||
        { cat "$work/setup.command"; return 1; }
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

# Скрипты выпуска и моделей репозитория — те же bash-тесты: отдельного набора ради двух скриптов нет.
models_WhenChecksumsListedTwice_TheyMatch() {
    local name ps sh
    for name in vad recognizer; do
        ps=$(grep -oP "^\\\$${name}Sha256 = '\K[0-9a-f]{64}" "$repo/scripts/install.ps1")
        sh=$(grep -oP "^${name}_sha256=\K[0-9a-f]{64}" "$repo/scripts/download-models.sh")
        [ -n "$ps" ] && [ "$ps" = "$sh" ] || { echo "$name: install.ps1 '$ps', download-models.sh '$sh'"; return 1; }
    done
}

release_WhenPluginVersionMatchesTag_PrintsVersion() {
    printf '{ "name": "meeting", "version": "1.2.3" }\n' > "$work/plugin.json"
    local got
    got=$(bash "$repo/scripts/release-version.sh" v1.2.3 "$work/plugin.json") || return 1
    [ "$got" = "1.2.3" ] || { echo "получено: $got"; return 1; }
}

release_WhenPluginVersionDiffers_Refuses() {
    printf '{ "name": "meeting", "version": "1.2.2" }\n' > "$work/plugin.json"
    if bash "$repo/scripts/release-version.sh" v1.2.3 "$work/plugin.json" > /dev/null 2>&1; then
        echo "отказа нет"
        return 1
    fi
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

echo "итого: $((passed + failed)), упало: $failed"
[ "$failed" -eq 0 ]
