#!/usr/bin/env bash
# Сборка meeting из исходников в WSL и установка в Windows — без .NET SDK в Windows, клон в любом каталоге.
#
# Собирает самодостаточный win-x64 здесь (нативные библиотеки приходят из NuGet под win-x64, как в CI), кладёт
# архив, его SHA-256 и install.ps1 во временный каталог Windows и запускает оттуда install.ps1 с -ReleaseBase:
# установка та же, что из релиза, только релиз — свой. Аргументы уходят install.ps1 (-MeetingsRoot <каталог>).
# Моделей в профиле нет — install.ps1 качает их сам.
#
# Запуск из любого каталога: ./scripts/install-wsl.sh [-MeetingsRoot 'C:\Meetings']
set -euo pipefail

command -v powershell.exe > /dev/null && command -v wslpath > /dev/null ||
    { echo "Нужна WSL: сборка ставится в Windows через powershell.exe. Без WSL — scripts/install.ps1 -FromSource." >&2; exit 1; }
command -v dotnet > /dev/null || { echo "Нет .NET SDK 10 в WSL: https://dot.net" >&2; exit 1; }
command -v python3 > /dev/null || { echo "Нужен python3: он собирает zip." >&2; exit 1; }

repo="$(cd "$(dirname "$0")/.." && pwd)"
windows_temp=$(powershell.exe -NoProfile -NonInteractive -Command '[Console]::Out.Write($env:TEMP)' | tr -d '\r')
release="$(wslpath "$windows_temp")/meeting-assistant-build"
publish=$(mktemp -d)
trap 'rm -rf "$publish" "$release"' EXIT

rm -rf "$release" && mkdir -p "$release"
echo "Сборка meeting..."
dotnet publish "$repo/src/MeetingAssistant.Cli/MeetingAssistant.Cli.csproj" \
    -c Release -r win-x64 --self-contained true -o "$publish" --nologo -v q

python3 -c 'import os, sys, zipfile
with zipfile.ZipFile(sys.argv[2], "w", zipfile.ZIP_DEFLATED) as archive:
    for root, _, files in os.walk(sys.argv[1]):
        for name in files:
            path = os.path.join(root, name)
            archive.write(path, os.path.relpath(path, sys.argv[1]))' "$publish" "$release/meeting-win-x64.zip"
(cd "$release" && sha256sum meeting-win-x64.zip > meeting-win-x64.zip.sha256)
cp "$repo/scripts/install.ps1" "$release/"

windows_release=$(wslpath -w "$release")
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$windows_release\\install.ps1" -ReleaseBase "$windows_release" "$@"
