"""CTC против RNNT с turbo третьим мнением на записях без эталона (`docs/measurements.md`, «Живое распознавание:
RNNT против CTC»).

Запуск: python referee.py <имя>... — для каждого имени нужны seg/<имя>/ и вывод bench.py (out/<имя>.gigaam.txt),
rnnt.py beam (out/<имя>.rnnt-beam.txt), wnet с GLUE=1 (out/<имя>.turbo.txt). Печатает только счётчики.

«Совпало с turbo» — слов, которые `difflib` находит общими у вывода модели и turbo. Turbo сам выбрасывает слова-паразиты
и ошибается, поэтому это не WER, а сравнение двух моделей с одним и тем же третьим мнением. Отдельно — фразы VAD,
которые turbo распознал одни в своём куске (кусок совпадает с фразой), по длине до и больше 20.5 с.
"""
import difflib
import json
import re
import sys
from collections import defaultdict


def words(text):
    return re.findall(r"\w+", text.lower().replace("ё", "е"))


def lines(path):
    return [words(line.split("] ", 1)[-1]) for line in open(path, encoding="utf-8")]


def agree(a, b):
    return sum(block.size for block in difflib.SequenceMatcher(None, a, b, autojunk=False).get_matching_blocks())


# Длинная фраза — длиннее предела VAD (20 с) с запасом: обрезанные по пределу фразы (20.0–20.2 с) — не длинные.
LONG = 20.5
totals = defaultdict(int)
alone = defaultdict(lambda: defaultdict(int))
for name in sys.argv[1:]:
    meta = json.load(open(f"seg/{name}/segments.json"))
    ctc, rnnt = lines(f"out/{name}.gigaam.txt"), lines(f"out/{name}.rnnt-beam.txt")
    starts = {round(m["start"], 1): k for k, m in enumerate(meta)}
    turbo = [(starts.get(round(float(line[1:line.index("]")]), 1)), words(line.split("] ", 1)[-1]))
             for line in open(f"out/{name}.turbo.txt", encoding="utf-8")]
    all_ctc, all_rnnt, all_turbo = sum(ctc, []), sum(rnnt, []), sum((w for _, w in turbo), [])
    totals["ctc"] += len(all_ctc)
    totals["rnnt"] += len(all_rnnt)
    totals["turbo"] += len(all_turbo)
    totals["ctc_agree"] += agree(all_ctc, all_turbo)
    totals["rnnt_agree"] += agree(all_rnnt, all_turbo)
    print(f"{name}: слов CTC {len(all_ctc)}, RNNT {len(all_rnnt)}, turbo {len(all_turbo)}; совпало с turbo — "
          f"CTC {agree(all_ctc, all_turbo)}, RNNT {agree(all_rnnt, all_turbo)}")
    for (k, turbo_words), (next_k, _) in zip(turbo, turbo[1:] + [(len(meta), None)]):
        if k is None or next_k != k + 1:
            continue
        bucket = alone["> 20.5 с" if meta[k]["len"] > LONG else "≤ 20.5 с"]
        bucket["phrases"] += 1
        bucket["ctc"] += len(ctc[k])
        bucket["rnnt"] += len(rnnt[k])
        bucket["ctc_agree"] += agree(ctc[k], turbo_words)
        bucket["rnnt_agree"] += agree(rnnt[k], turbo_words)

print(f"всего: слов CTC {totals['ctc']}, RNNT {totals['rnnt']}, turbo {totals['turbo']}; совпало с turbo — "
      f"CTC {totals['ctc_agree']}, RNNT {totals['rnnt_agree']}")
for length, b in sorted(alone.items()):
    print(f"фразы {length}, одни в куске turbo: {b['phrases']}; слов CTC {b['ctc']}, RNNT {b['rnnt']}; совпало с "
          f"turbo — CTC {b['ctc_agree']}, RNNT {b['rnnt_agree']}")
