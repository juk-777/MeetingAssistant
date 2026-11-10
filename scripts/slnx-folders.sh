#!/usr/bin/env bash
# Пересобирает в MeetingAssistant.slnx папки с файлами репозитория: корневые файлы, документы, скрипты, прототип.
#
# Нужен потому, что .slnx перечисляет файлы поимённо и подстановок не понимает: новая спека или план
# не появятся в дереве решения сами. Берутся только файлы под git — модели, записи и прогоны прототипа
# в дерево не попадают. Папки с проектами (/src/, /tests/) скрипт не трогает — их ведёт IDE.
#
# Запуск из корня репозитория: ./scripts/slnx-folders.sh
set -euo pipefail

cd "$(dirname "$0")/.."

python3 -I - <<'PY'
import io
import posixpath
import re
import subprocess

SOLUTION = "MeetingAssistant.slnx"

# Всё файловое живёт под одной папкой решения, чтобы корень дерева читался: src, tests и она.
ROOT = "Solution Items"

# Файлы корня репозитория; порядок здесь — порядок в дереве.
ROOT_FILES = [
    "README.md",
    "CLAUDE.md",
    "AGENTS.md",
    "LICENSE",
    "global.json",
    ".editorconfig",
    ".gitignore",
    "Directory.Build.props",
    "Directory.Packages.props",
]

# Каталоги, файлы которых раскладываются по папкам решения; вложенный каталог — своя папка.
TREES = ["docs", "scripts", "prototype"]

tracked = subprocess.run(
    ["git", "ls-files", "--", *TREES, *ROOT_FILES],
    check=True,
    capture_output=True,
    text=True,
).stdout.split()


def render():
    lines = [f'  <Folder Name="/{ROOT}/">']
    lines.extend(f'    <File Path="{path}" />' for path in ROOT_FILES if path in tracked)
    lines.append("  </Folder>")

    folders = {}
    for path in tracked:
        if path.split("/", 1)[0] in TREES:
            folders.setdefault(posixpath.dirname(path), []).append(path)

    for folder in sorted(folders):
        lines.append(f'  <Folder Name="/{ROOT}/{folder}/">')
        lines.extend(f'    <File Path="{path}" />' for path in sorted(folders[folder]))
        lines.append("  </Folder>")

    return "\n".join(lines)


raw = io.open(SOLUTION, "rb").read()
bom = raw.startswith(b"\xef\xbb\xbf")
text = raw.decode("utf-8-sig")

# Всё от <Solution> до первой папки с проектами — папки файлов, они и пересобираются.
head = re.search(r"^\s*<Solution>\s*?\n", text)
tail = re.search(r'^\s*<Folder Name="/src/', text, re.MULTILINE)

if head is None or tail is None:
    raise SystemExit(f"{SOLUTION} не похож на решение: нет <Solution> или папки /src/")

rebuilt = text[: head.end()] + render() + "\n" + text[tail.start():]

if rebuilt == text:
    print("Папки решения уже совпадают с git")
else:
    io.open(SOLUTION, "wb").write((b"\xef\xbb\xbf" if bom else b"") + rebuilt.encode("utf-8"))
    print(f"Папки решения пересобраны: {rebuilt.count('<File Path=')} файлов")
PY
