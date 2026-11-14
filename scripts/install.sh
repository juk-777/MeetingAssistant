#!/usr/bin/env bash
# Установка и обновление приложения записи `meeting` в домашнем каталоге пользователя Linux (x86-64).
#
# Ставит meeting (самодостаточная сборка, свой рантайм .NET) в ~/.local/share/meeting-assistant/app, модели — в
# ~/.local/share/meeting-assistant/models, ссылку meeting — в ~/.local/bin, а Storage__MeetingsRoot,
# Speech__ModelsDirectory и ~/.local/bin в PATH — в ~/.profile, своим блоком. Повторный запуск — обновление: app
# заменяется, модели и корень встреч остаются. Устройство — docs/specs/2026-11-14-linux-capture.md, образец —
# install.ps1.
#
# Без ключей — сборка из релиза GitHub (последнего или --version) со сверкой SHA-256 из того же релиза. Моделей
# нет — качаются (GigaAM и VAD — из релиза sherpa-onnx, Whisper turbo для уточнения встречи — с Hugging Face) и
# сверяются с SHA-256, записанными ниже. Всё скачивается и проверяется до того, как тронут установленный app.
# --from-source — сборка из репозитория, где лежит скрипт (нужен .NET SDK 10), модели — из его models/, если они там
# есть. Нужны curl, tar, sha256sum; bzip2 или python3 — для распаковки модели.
#
# Из релиза:
#   curl -fsSL https://github.com/juk-777/MeetingAssistant/releases/latest/download/install.sh | bash
#   curl -fsSL …/install.sh | bash -s -- --meetings-root ~/Meetings
# Из исходников, из корня репозитория:
#   ./scripts/install.sh --from-source [--meetings-root <каталог>]
#
# Ключи:
#   --meetings-root <каталог>  корень встреч; без ключа — заданный раньше, при первой установке — ~/Meetings
#   --version <vX.Y.Z>         версия релиза; без ключа — последний релиз
#   --from-source              собрать из репозитория, где лежит скрипт
#   --release-base <адрес>     откуда брать файлы релиза: адрес релизов GitHub или каталог с ними
set -euo pipefail

meetings_root=
version=
from_source=
release_base=https://github.com/juk-777/MeetingAssistant/releases

while [ $# -gt 0 ]; do
    case "$1" in
        --meetings-root) meetings_root=${2:?"--meetings-root: нужен каталог"}; shift 2 ;;
        --version) version=${2:?"--version: нужна версия"}; shift 2 ;;
        --from-source) from_source=1; shift ;;
        --release-base) release_base=${2:?"--release-base: нужен адрес или каталог"}; shift 2 ;;
        *) echo "Неизвестный ключ: $1. Ключи — в начале scripts/install.sh." >&2; exit 2 ;;
    esac
done

fail() { echo "$1" >&2; exit 1; }

[ "$(uname -s)" = Linux ] && [ "$(uname -m)" = x86_64 ] ||
    fail "Скрипт ставит meeting на Linux x86-64. Windows — install.ps1."
for tool in curl tar sha256sum; do
    command -v "$tool" > /dev/null || fail "Нет $tool: поставьте его (Ubuntu: пакеты curl, tar, coreutils)."
done

base="${XDG_DATA_HOME:-$HOME/.local/share}/meeting-assistant"
app="$base/app"
staging="$base/app.new"
previous="$base/app.old"
models="$base/models"
downloads="$base/download"
bin="$HOME/.local/bin"
profile="$HOME/.profile"
app_archive=meeting-linux-x64.tar.gz
[ -z "$version" ] || [[ $version == v* ]] || version="v$version"

# Те же файлы, что проверяет само приложение (SpeechModels в Infrastructure).
vad=silero_vad.onnx
recognizer=sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16
model_files=("$vad" "$recognizer/model.int8.onnx" "$recognizer/tokens.txt")

# Модели — из релиза sherpa-onnx; суммы те же, что в install.ps1 и download-models.sh (тест скилла сверяет).
# Источник моделей можно подменить зеркалом или каталогом (MEETING_MODEL_RELEASE, MEETING_WHISPER_SOURCE): суммы
# остаются те же.
model_release=${MEETING_MODEL_RELEASE:-https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models}
vad_sha256=9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6
recognizer_sha256=20e41e160efa6f2460a7ef6554cdbbb9e8fffb2b92e6bc708e9406bd8b9256ea

# Whisper turbo — для уточнения встречи (meeting refine). Адрес закреплён на ревизии Hugging Face.
whisper_file=ggml-large-v3-turbo-q5_0.bin
whisper_source=${MEETING_WHISPER_SOURCE:-https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1}
whisper_sha256=394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2

has_models() {
    local file
    for file in "${model_files[@]}"; do [ -f "$1/$file" ] || return 1; done
}

# save <источник> <файл>: адрес качается, путь копируется.
save() {
    if [[ $1 =~ ^https?:// ]]; then curl -fsSL --retry 3 -o "$2" "$1"; else cp "$1" "$2"; fi
}

check_sha256() {
    echo "$2  $1" | sha256sum -c --quiet - > /dev/null 2>&1 ||
        fail "Контрольная сумма $(basename "$1") не совпала: $3 Установленное не тронуто, повторите установку."
}

release_file() {
    if ! [[ $release_base =~ ^https?:// ]]; then echo "$release_base/$1"
    elif [ -n "$version" ]; then echo "$release_base/download/$version/$1"
    else echo "$release_base/latest/download/$1"
    fi
}

# Значение в одинарных кавычках: его читают и bash, и sh (dash), в любой локали.
quote() { printf "'%s'" "${1//\'/\'\\\'\'}"; }

cleanup() { rm -rf "$staging" "$downloads"; }
trap cleanup EXIT
cleanup
mkdir -p "$base" "$downloads"

# 0. Всё, на чём могут отказать ссылка, ~/.profile и корень встреч, — до замены app.
if [ -L "$bin/meeting" ]; then
    [ "$(readlink "$bin/meeting")" = "$app/meeting" ] ||
        fail "$bin/meeting ведёт в $(readlink "$bin/meeting"), а не в установку: уберите ссылку и повторите."
elif [ -e "$bin/meeting" ]; then
    fail "$bin/meeting — не ссылка на установку: уберите его и повторите."
fi

begin='# >>> meeting-assistant >>>'
end='# <<< meeting-assistant <<<'
if [ -f "$profile" ] && [ "$(grep -cxF "$begin" "$profile" || true)" != "$(grep -cxF "$end" "$profile" || true)" ]; then
    fail "В ~/.profile блок meeting-assistant без начала или конца: поправьте руками (строки «$begin» и «$end») и повторите."
fi

previous_root=
if [ -f "$profile" ]; then
    # Строка export из блока, выполненная в подоболочке, возвращает значение как было.
    previous_root=$(sed -n "/^$begin\$/,/^$end\$/{/^export Storage__MeetingsRoot=/p}" "$profile" |
        (unset Storage__MeetingsRoot; . /dev/stdin; printf '%s' "${Storage__MeetingsRoot:-}"))
fi
if [ -n "$meetings_root" ]; then
    root=$(realpath -m -- "$meetings_root")
elif [ -n "$previous_root" ]; then
    root=$previous_root
else
    root=${Storage__MeetingsRoot:-$HOME/Meetings}
fi
mkdir -p "$root" 2> /dev/null || fail "Не создать корень встреч $root: задайте другой — --meetings-root <каталог>."

# 1. Новый app — в app.new. Запущенный meeting замене не мешает: Linux держит открытые файлы прежней версии до
# конца процесса.
if [ -n "$from_source" ]; then
    repo="$(cd "$(dirname "$0")/.." && pwd)"
    dotnet=
    for candidate in "$HOME/.dotnet/dotnet" "$(command -v dotnet || true)"; do
        if [ -n "$candidate" ] && [ -x "$candidate" ] && "$candidate" --list-sdks | grep -q '^10\.'; then
            dotnet=$candidate; break
        fi
    done
    [ -n "$dotnet" ] || fail "Нет .NET SDK 10: ни в PATH, ни в ~/.dotnet. Установить: https://dot.net"
    echo "Сборка meeting ($dotnet)..."
    "$dotnet" publish "$repo/src/MeetingAssistant.Cli/MeetingAssistant.Cli.csproj" \
        -c Release -r linux-x64 --self-contained true -o "$staging" --nologo -v q
else
    if ! [[ $release_base =~ ^https?:// ]]; then from=$release_base; else from=${version:-последний релиз}; fi
    echo "Загрузка meeting ($from)..."
    archive="$downloads/$app_archive"
    save "$(release_file "$app_archive.sha256")" "$archive.sha256"
    save "$(release_file "$app_archive")" "$archive"
    # Сумма — из того же релиза: защищает от порчи при загрузке, а не от подмены релиза.
    check_sha256 "$archive" "$(cut -d' ' -f1 "$archive.sha256")" "архив повреждён при загрузке."
    mkdir -p "$staging"
    tar -xzf "$archive" -C "$staging"
fi
[ -x "$staging/meeting" ] || fail "В сборке нет исполняемого meeting: установленное не тронуто."

# 2. Модели — тоже до замены app: отказ при загрузке или распаковке оставляет установленное как было.
source_models=
[ -z "$from_source" ] || source_models="$repo/models"
copy_from_repository=
if [ -n "$source_models" ] && has_models "$source_models"; then copy_from_repository=1; fi
unpacked="$downloads/models"
download=
if [ -z "$copy_from_repository" ] && ! has_models "$models"; then download=1; fi
if [ -n "$download" ]; then
    echo "Загрузка моделей (163 МБ)..."
    mkdir -p "$unpacked"
    save "$model_release/$vad" "$unpacked/$vad"
    check_sha256 "$unpacked/$vad" "$vad_sha256" "файл повреждён или подменён."
    save "$model_release/$recognizer.tar.bz2" "$downloads/$recognizer.tar.bz2"
    check_sha256 "$downloads/$recognizer.tar.bz2" "$recognizer_sha256" "файл повреждён или подменён."
    # bzip2 есть не везде — тогда распаковка модулем tarfile Python.
    if command -v bzip2 > /dev/null; then
        tar -xjf "$downloads/$recognizer.tar.bz2" -C "$unpacked"
    else
        command -v python3 > /dev/null || fail "Нет ни bzip2, ни python3 для распаковки модели: sudo apt install bzip2."
        python3 -c 'import sys, tarfile; tarfile.open(sys.argv[1]).extractall(sys.argv[2])' \
            "$downloads/$recognizer.tar.bz2" "$unpacked"
    fi
    has_models "$unpacked" || fail "Модель распакована не полностью: нет одного из ${model_files[*]}."
fi

# Whisper — отдельно от GigaAM: сверка и для копии из репозитория, models/ репозитория кладут руками.
whisper_staged="$downloads/$whisper_file"
whisper_needed=
[ -f "$models/$whisper_file" ] || whisper_needed=1
if [ -n "$whisper_needed" ]; then
    if [ -n "$source_models" ] && [ -f "$source_models/$whisper_file" ]; then
        echo "Модель Whisper из репозитория..."
        cp "$source_models/$whisper_file" "$whisper_staged"
    else
        echo "Загрузка модели Whisper (574 МБ)..."
        save "$whisper_source/$whisper_file" "$whisper_staged"
    fi
    check_sha256 "$whisper_staged" "$whisper_sha256" "файл повреждён или подменён."
fi

# 3. Замена app.
rm -rf "$previous"
[ ! -e "$app" ] || mv "$app" "$previous"
mv "$staging" "$app"
rm -rf "$previous"

# 4. Модели на место.
mkdir -p "$models"
if [ -n "$copy_from_repository" ]; then
    echo "Модели из репозитория..."
    cp "$source_models/$vad" "$models/"
    mkdir -p "$models/$recognizer"
    cp -r "$source_models/$recognizer/." "$models/$recognizer/"
elif [ -n "$download" ]; then
    # Неполный каталог модели от прошлой попытки заменяется целиком.
    rm -rf "${models:?}/$recognizer"
    mv "$unpacked/$recognizer" "$models/"
    mv -f "$unpacked/$vad" "$models/"
fi
[ -z "$whisper_needed" ] || mv "$whisper_staged" "$models/"

# 5. Ссылка meeting в ~/.local/bin (чужой файл и чужую ссылку шаг 0 уже отсёк).
mkdir -p "$bin"
ln -sfn "$app/meeting" "$bin/meeting"

# 6. Переменные — в ~/.profile своим блоком: его читают вход в систему и login-оболочки. Повтор заменяет блок.
block=$(
    echo "$begin"
    echo "export Storage__MeetingsRoot=$(quote "$root")"
    echo "export Speech__ModelsDirectory=$(quote "$models")"
    # shellcheck disable=SC2016 # раскрывается при входе, а не сейчас
    printf '%s\n' 'case ":$PATH:" in *":$HOME/.local/bin:"*) ;; *) export PATH="$HOME/.local/bin:$PATH" ;; esac'
    echo "$end"
)
kept=
[ ! -f "$profile" ] || kept=$(sed "/^$begin\$/,/^$end\$/d" "$profile")
updated="$downloads/profile"
{
    [ -z "$kept" ] || printf '%s\n' "$kept"
    printf '%s\n' "$block"
} > "$updated"
# Запись поверх, а не замена файла: ~/.profile бывает ссылкой в каталог настроек, и его права остаются.
cat "$updated" > "$profile"

installed=$("$app/meeting" --version)
size=$(du -sm "$app" | cut -f1)
echo "Готово: meeting $installed, $app ($size МБ)"
echo "Корень встреч: $root"
echo "Переменные — в ~/.profile: они действуют со следующего входа в систему; сейчас — source ~/.profile."
# Login-bash при ~/.bash_profile или ~/.bash_login читает их, а не ~/.profile.
for own in "$HOME/.bash_profile" "$HOME/.bash_login"; do
    if [ -f "$own" ] && ! grep -q '\.profile' "$own"; then
        echo "Есть $own: login-bash не читает ~/.profile — добавьте в $own строку «. ~/.profile»."
    fi
done
echo 'Новое окно терминала: meeting start "<имя>"; из сессии Claude Code: /meeting start <имя>.'
