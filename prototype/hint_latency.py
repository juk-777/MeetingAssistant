"""Задержка «событие → подсказка» по частям для прогонов скилла на стендах meeting-run.sh и meeting-replay-run.sh.

Запуск: python hint_latency.py <каталог прогона>... В каталоге прогона — stream.tsv (вывод `claude -p
--output-format stream-json --verbose`, каждая строка с временем прихода через табуляцию) и каталог встречи с
hints.md и events.log (`meeting/` у meeting-run.sh, `meetings/<встреча>/` у meeting-replay-run.sh).

Печатает строку на событие, затем сводку p50 / p90 / max по всем прогонам вместе и подсказки с молчаниями по поводам.
Событие «конец записи» в сводку не идёт: это не пробуждение.

Части:
- доставка — от события до начала хода сессии: последний `system/init` до записи подсказки; событие, взятое посреди
  идущего хода, — без доставки;
- ход — от начала хода до вызова `meeting-hint`; из него инструменты — сумма от вызова до результата, кроме
  `meeting-hint`;
- вид — ссылка, `общее` или молчание, по строке hints.md.

Повод — из строки events.log с тем же временем. p50 — верхняя медиана; число значений — в скобках.
"""
import glob
import json
import os
import sys

END_OF_RECORDING = "конец записи"


def session(stream):
    turns, tools, pending = [], [], {}
    for line in open(stream, encoding="utf-8"):
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
    return turns, tools


if len(sys.argv) < 2:
    sys.exit("Запуск: python hint_latency.py <каталог прогона>...")
parts = {"доставка": [], "ход": [], "всего": [], "всего, только подсказки": []}
by_cause = {}
print("прогон\tсобытие\tдоставка, с\tход до meeting-hint, с\tиз них инструменты, с\tвсего, с\tвид")
for run in sys.argv[1:]:
    meetings = (glob.glob(os.path.join(run, "meeting", "hints.md"))
                + glob.glob(os.path.join(run, "meetings", "*", "hints.md")))
    if len(meetings) != 1:
        sys.exit(f"{run}: ждали один hints.md в meeting/ или meetings/*/, нашли {len(meetings)}")
    hints = meetings[0]
    turns, tools = session(os.path.join(run, "stream.tsv"))
    causes = {}
    for line in open(os.path.join(os.path.dirname(hints), "events.log"), encoding="utf-8"):
        epoch, _, reasons = line.rstrip("\n").split("\t")
        causes[round(float(epoch), 3)] = reasons
    for line in open(hints, encoding="utf-8"):
        event, written, number, text = line.rstrip("\n").split("\t", 3)
        event, written = float(event), float(written)
        # Ход, в котором записана подсказка, — последний начатый до записи. Событие, пришедшее посреди идущего хода,
        # сессия берёт без нового system/init: тогда доставки нет, ход считается от события.
        turn = max((t for t in turns if t <= written), default=None)
        if turn is None:
            continue
        turn = max(turn, event)
        used = sum(end - start for start, end, is_hint in tools if turn <= start < written and not is_hint)
        # Вид — по последней строке подсказки: «общее» (в любых кавычках и скобках) или источник; наличие чтения
        # перед ссылкой видно по колонке инструментов этого и прошлых ходов.
        tail = text.split(" / ")[-1].strip(" `«»()*.")
        kind = "молчание" if text == "-" else ("общее" if tail.endswith("общее") else "ссылка")
        print(f"{os.path.basename(os.path.normpath(run))}\t{number}\t{turn - event:.1f}\t{written - turn:.1f}\t"
              f"{used:.1f}\t{written - event:.1f}\t{kind}")
        cause = causes.get(round(event, 3), "?")
        if cause == END_OF_RECORDING:
            continue
        parts["доставка"].append(turn - event)
        parts["ход"].append(written - turn)
        parts["всего"].append(written - event)
        if kind != "молчание":
            parts["всего, только подсказки"].append(written - event)
        counts = by_cause.setdefault(cause, {})
        counts[kind] = counts.get(kind, 0) + 1

for name, values in parts.items():
    if values:
        values.sort()
        print(f"{name}: p50 {values[len(values) // 2]:.1f} с, p90 {values[len(values) * 9 // 10]:.1f} с, "
              f"max {values[-1]:.1f} с ({len(values)})")
for cause, counts in sorted(by_cause.items()):
    print(f"{cause}: " + ", ".join(f"{kind} {n}" for kind, n in sorted(counts.items())))
