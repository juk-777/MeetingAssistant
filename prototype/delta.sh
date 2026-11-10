#!/usr/bin/env bash
# Новые строки транскрипта с прошлого чтения: номер строки — в .offset
cd "$(dirname "$0")/live"
last=$(cat .offset 2>/dev/null || echo 0)
awk -F'\t' -v l="$last" '$1>l {print $3}' transcript.md
awk -F'\t' 'END{print $1+0}' transcript.md > .offset
