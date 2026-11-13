"""Задержка «событие → подсказка» по частям для прогона скилла на стенде meeting-feed.sh.

Запуск: python hint_latency.py <stream.tsv> <hints.md>. stream.tsv — вывод `claude -p --output-format stream-json
--verbose`, каждая строка с временем прихода через табуляцию. Части: доставка — от события до начала хода сессии
(`system/init` после события); ход — от начала хода до вызова `meeting-hint`; из него инструменты — сумма от вызова
до результата, кроме `meeting-hint`. Вид: ссылка, `общее` или молчание — по строке hints.md.
"""
import json
import sys

turns, tools, pending = [], [], {}
for line in open(sys.argv[1], encoding="utf-8"):
    at, _, payload = line.rstrip("\n").partition("\t")
    try:
        message = json.loads(payload)
    except json.JSONDecodeError:
        continue
    at = float(at)
    if message.get("type") == "system" and message.get("subtype") == "init":
        turns.append(at)
    elif message.get("type") == "assistant":
        for part in message["message"]["content"]:
            if part["type"] == "tool_use":
                is_hint = "meeting-hint" in json.dumps(part["input"], ensure_ascii=False)
                pending[part["id"]] = (at, is_hint)
    elif message.get("type") == "user" and isinstance(message["message"]["content"], list):
        for part in message["message"]["content"]:
            if part.get("type") == "tool_result" and part["tool_use_id"] in pending:
                start, is_hint = pending.pop(part["tool_use_id"])
                tools.append((start, at, is_hint))

print("событие\tдоставка, с\tход до meeting-hint, с\tиз них инструменты, с\tвсего, с\tвид")
for line in open(sys.argv[2], encoding="utf-8"):
    event, written, number, text = line.rstrip("\n").split("\t", 3)
    event, written = float(event), float(written)
    turn = min((t for t in turns if t >= event), default=None)
    if turn is None:
        continue
    used = sum(end - start for start, end, is_hint in tools if turn <= start < written and not is_hint)
    # Вид — по последней строке подсказки: «общее» (в любых кавычках и скобках) или источник; наличие чтения
    # перед ссылкой видно по колонке инструментов этого и прошлых ходов.
    tail = text.split(" / ")[-1].strip(" `«»()*.")
    kind = "молчание" if text == "-" else ("общее" if tail.endswith("общее") else "ссылка")
    print(f"{number}\t{turn - event:.1f}\t{written - turn:.1f}\t{used:.1f}\t{written - event:.1f}\t{kind}")
