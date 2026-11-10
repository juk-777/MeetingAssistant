#!/usr/bin/env bash
# hint.sh <epoch события> <текст | SKIP>: подсказка с задержкой от события
cd "$(dirname "$0")/live"
now=$(date +%s.%N)
lat=$(awk -v a="$now" -v b="$1" 'BEGIN{printf "%.1f", a-b}')
printf '%s\t%s\t%s\n' "$lat" "$(date +%T)" "$2" >> hints.md
echo "задержка ${lat} с"
