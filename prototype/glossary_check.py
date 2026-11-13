"""Счёт по выводам распознавания для замера словаря: слова, термины словаря, повторы подряд.

Запуск: python glossary_check.py <словарь> <вывод>... [--hits]. Вывод — строки «[начало] текст» (bench.py,
rnnt.py, wnet). Термин ищется без учёта регистра, пробелы и дефисы не в счёт. Повтор — слово три раза
подряд и больше или строка, совпавшая с предыдущей. --hits печатает каждое найденное вхождение:
начало строки и термин — по ним вставки сверяются с выводами без подсказки вручную.
"""
import re
import sys

args = [a for a in sys.argv[1:] if a != "--hits"]
show_hits = "--hits" in sys.argv
terms = sorted({line.strip() for line in open(args[0], encoding="utf-8") if line.strip()}, key=len, reverse=True)


def squash(text):
    return re.sub(r"[\s\-]", "", text.lower())


for path in args[1:]:
    words = hits = repeats = 0
    found = []
    previous = None
    for line in open(path, encoding="utf-8"):
        start, _, text = line.partition("] ")
        words += len(text.split())
        flat = squash(text)
        for term in terms:  # длинные первыми: «readiness probe» не считается ещё и «readiness»
            key = squash(term)
            count = flat.count(key)
            if count:
                hits += count
                found += [(start.strip("[ "), term)] * count
                flat = flat.replace(key, "\0")
        tokens = re.findall(r"\w+", text.lower())
        repeats += sum(1 for i in range(2, len(tokens)) if tokens[i] == tokens[i - 1] == tokens[i - 2])
        repeats += text.strip() != "" and text.strip() == previous
        previous = text.strip()
    print(f"{path}: слов {words}, терминов словаря {hits}, повторов {repeats}")
    if show_hits:
        for start, term in found:
            print(f"  [{start}] {term}")
