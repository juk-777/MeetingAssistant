"""WER и CER вывода распознавания против эталона набора (`docs/measurements.md`, «Открытый набор»).

Запуск: python wer.py <каталог seg> <вывод>... ; python wer.py --pair <каталог seg> <вывод> <вывод> — по единицам,
сколько второй вывод лучше и хуже первого; python wer.py --test — проверка счёта на известных входах.
Каталог seg — от openstt.py: segments.json (отрезок VAD с номерами фраз набора, на которые он пришёлся) и
clips.json (фразы с эталоном).
Вывод — строки «[начало] текст» от ctc.py, rnnt.py или wnet; строка сопоставляется с отрезком по началу.
Строка turbo с GLUE=1 — кусок из нескольких отрезков подряд, поэтому счёт идёт по единицам: фразы набора, которые
накрыты одной строкой вывода, сливаются в одну единицу вместе с эталонами. Фраза без отрезков — пустая гипотеза.

Нормализация эталона и гипотезы: нижний регистр, ё → е, всё, кроме букв и цифр, — пробел, число — словами
(num2words, ru). Отдельно считаются ошибки, где слово гипотезы записано цифрами или латиницей: это расхождение
правил записи, а не слуха.
"""
import bisect
import json
import os
import re
import sys
import tempfile

from num2words import num2words

TOKEN = re.compile(r"\d+(?:[.,]\d+)?|[^\W\d_]+")


def normalize(text):
    """Слова после нормализации и признак «записано цифрами или латиницей» у каждого."""
    words = []
    for token in TOKEN.findall(text.lower().replace("ё", "е")):
        if token[0].isdigit():
            spoken = num2words(float(token.replace(",", ".")) if re.search("[.,]", token) else int(token), lang="ru")
            words += [(w, True) for w in TOKEN.findall(spoken.replace("ё", "е"))]
        else:
            words.append((token, bool(re.search("[a-z]", token))))
    return words


def distance(ref, hyp, flags=None):
    """Правок до совпадения и сколько из них приходится на помеченные слова гипотезы (замена или вставка)."""
    flags = flags or [False] * len(hyp)
    prev = [(j, sum(flags[:j])) for j in range(len(hyp) + 1)]
    for i in range(1, len(ref) + 1):
        cur = [(i, 0)]
        for j in range(1, len(hyp) + 1):
            if ref[i - 1] == hyp[j - 1]:
                cur.append(prev[j - 1])
                continue
            # Замена и вставка тратят слово гипотезы j, удаление — только слово эталона.
            sub, ins, dele = prev[j - 1], cur[j - 1], prev[j]
            if dele[0] < min(sub[0], ins[0]):
                cur.append((dele[0] + 1, dele[1]))
            else:
                best = sub if sub[0] <= ins[0] else ins
                cur.append((best[0] + 1, best[1] + flags[j - 1]))
        prev = cur
    return prev[-1]


def units(seg_dir, output):
    meta = json.load(open(os.path.join(seg_dir, "segments.json"), encoding="utf-8"))
    clips = json.load(open(os.path.join(seg_dir, "clips.json"), encoding="utf-8"))
    starts = [m["start"] for m in meta]
    lines = []
    for line in open(output, encoding="utf-8"):
        head, _, text = line.rstrip("\n").partition("]")
        text = text.removeprefix(" ")
        if not head.startswith("["):
            continue
        at = float(head[1:])
        k = min(range(max(0, bisect.bisect_left(starts, at) - 1), min(len(starts), bisect.bisect_left(starts, at) + 2)),
                key=lambda n: abs(starts[n] - at))
        lines.append((k, text))
    lines.sort()
    ranges = []
    for n, (k, text) in enumerate(lines):
        last = lines[n + 1][0] - 1 if n + 1 < len(lines) else len(meta) - 1
        first_clip, last_clip = meta[k]["clip"], max(m["last"] for m in meta[k:last + 1])
        if ranges and first_clip <= ranges[-1][1]:
            ranges[-1][1] = max(ranges[-1][1], last_clip)
            ranges[-1][2].append(text)
        else:
            ranges.append([first_clip, last_clip, [text]])
    covered = {c for first, last, _ in ranges for c in range(first, last + 1)}
    ranges += [[c, c, []] for c in range(len(clips)) if c not in covered]
    for first, last, texts in ranges:
        yield " ".join(clips[c]["ref"] for c in range(first, last + 1)), " ".join(texts)


def score(pairs):
    words = word_errors = marked = chars = char_errors = 0
    for ref_text, hyp_text in pairs:
        ref, hyp = normalize(ref_text), normalize(hyp_text)
        errors, flagged = distance([w for w, _ in ref], [w for w, _ in hyp], [f for _, f in hyp])
        words += len(ref)
        word_errors += errors
        marked += flagged
        ref_chars, hyp_chars = " ".join(w for w, _ in ref), " ".join(w for w, _ in hyp)
        chars += len(ref_chars)
        char_errors += distance(ref_chars, hyp_chars)[0]
    return dict(words=words, wer=word_errors / max(words, 1), cer=char_errors / max(chars, 1),
                errors=word_errors, marked=marked)


def test():
    ten = "раз два три четыре пять шесть семь восемь девять десять"
    cases = [
        ("одинаковые", [(ten, ten)], 0.0),
        ("одно слово из десяти", [(ten, ten.replace("пять", "пятью"))], 0.1),
        ("регистр, ё и знаки", [("ещё вопрос по базе", "Еще вопрос: по базе?!")], 0.0),
        ("число цифрами", [("два сервиса", "2 сервиса")], 0.0),
        ("дробь цифрами", [("две целых пять десятых", "2,5")], 0.0),
        ("пустая гипотеза", [("три слова тут", "")], 1.0),
    ]
    failed = 0
    for name, pairs, expected in cases:
        got = score(pairs)["wer"]
        ok = abs(got - expected) < 1e-9
        failed += not ok
        print(f"{'ок ' if ok else 'НЕТ'} {name}: WER {got:.3f}, ждали {expected:.3f}")
    got = score([("абв где", "абг где")])["cer"]
    ok = abs(got - 1 / 7) < 1e-9
    failed += not ok
    print(f"{'ок ' if ok else 'НЕТ'} одна буква из семи знаков: CER {got:.3f}, ждали {1 / 7:.3f}")
    for name, pair in (("замена", ("питон и два", "Python и 3")), ("вставка", ("и два", "и Python два"))):
        marked = score([pair])
        expected = 2 if name == "замена" else 1
        ok = marked["errors"] == expected and marked["marked"] == expected
        failed += not ok
        print(f"{'ок ' if ok else 'НЕТ'} латиница и цифры помечены, {name}: ошибок {marked['errors']}, "
              f"помечено {marked['marked']}, ждали {expected}")

    # Фразы набора: 0 — два отрезка, 1 — один, 2 — без отрезков. Кусок turbo накрывает отрезки 0–1 фразы 0 и
    # фразу 1.
    with tempfile.TemporaryDirectory() as seg_dir:
        segments = [{"i": 0, "start": 0.0, "len": 1, "clip": 0, "last": 0},
                    {"i": 1, "start": 1.5, "len": 1, "clip": 0, "last": 0},
                    {"i": 2, "start": 3.0, "len": 1, "clip": 1, "last": 1}]
        json.dump(segments, open(os.path.join(seg_dir, "segments.json"), "w"))
        json.dump([{"ref": "первая фраза"}, {"ref": "вторая"}, {"ref": "третья"}],
                  open(os.path.join(seg_dir, "clips.json"), "w"), ensure_ascii=False)
        output = os.path.join(seg_dir, "out.txt")
        open(output, "w", encoding="utf-8").write("[   0.1] Первая фраза. Вторая.\n")
        got = list(units(seg_dir, output))
        ok = got == [("первая фраза вторая", "Первая фраза. Вторая."), ("третья", "")]
        failed += not ok
        print(f"{'ок ' if ok else 'НЕТ'} кусок из нескольких фраз и фраза без отрезков: {got}")
        # Фраза 0 разрезана между двумя кусками: оба куска и обе их фразы — одна единица. Начало второго куска вне
        # сетки начал (1.4 при отрезке с 1.5) ложится на ближайший отрезок, а не на предыдущий.
        open(output, "w", encoding="utf-8").write("[   0.0] Первая\n[   1.4] фраза. Вторая.\n")
        got = list(units(seg_dir, output))
        ok = got == [("первая фраза вторая", "Первая фраза. Вторая."), ("третья", "")]
        failed += not ok
        print(f"{'ок ' if ok else 'НЕТ'} фраза, разрезанная между кусками: {got}")
        # Отрезок 2 начался во фразе 1 и захватил фразу 2: обе фразы — одна единица, а не пустая гипотеза у второй.
        segments[2]["last"] = 2
        json.dump(segments, open(os.path.join(seg_dir, "segments.json"), "w"))
        open(output, "w", encoding="utf-8").write("[   0.0] Первая фраза\n[   1.5]\n[   3.0] Вторая. Третья.\n")
        got = list(units(seg_dir, output))
        ok = got == [("первая фраза", "Первая фраза "), ("вторая третья", "Вторая. Третья.")]
        failed += not ok
        print(f"{'ок ' if ok else 'НЕТ'} отрезок через границу фраз: {got}")
    return failed


def pair(seg_dir, first, second):
    """Сколько единиц второй вывод распознал лучше первого и сколько хуже: только для выводов с одной нарезкой."""
    better = worse = 0
    for (ref, a), (ref_b, b) in zip(units(seg_dir, first), units(seg_dir, second), strict=True):
        if ref != ref_b:
            sys.exit("Единицы выводов не совпадают: сравнение по фразам — только для одной нарезки.")
        words = [w for w, _ in normalize(ref)]
        errors_a = distance(words, [w for w, _ in normalize(a)])[0]
        errors_b = distance(words, [w for w, _ in normalize(b)])[0]
        better += errors_b < errors_a
        worse += errors_b > errors_a
    return better, worse


if __name__ == "__main__":
    if len(sys.argv) == 2 and sys.argv[1] == "--test":
        sys.exit(test())
    if len(sys.argv) == 5 and sys.argv[1] == "--pair":
        better, worse = pair(*sys.argv[2:])
        print(f"{os.path.basename(sys.argv[4])} против {os.path.basename(sys.argv[3])}: лучше в {better} единицах, "
              f"хуже в {worse}")
        sys.exit(0)
    if len(sys.argv) < 3:
        sys.exit("Запуск: python wer.py <seg/имя> <вывод>... | --pair <seg/имя> <вывод> <вывод> | --test")
    seg_dir = sys.argv[1]
    print("вывод\tслов\tWER\tCER\tошибок\tиз них цифры/латиница")
    for output in sys.argv[2:]:
        s = score(units(seg_dir, output))
        print(f"{os.path.basename(output)}\t{s['words']}\t{s['wer']:.3f}\t{s['cer']:.3f}\t{s['errors']}\t"
              f"{s['marked']} ({s['marked'] / max(s['errors'], 1):.0%})")
