"""Фильтр пробуждений на готовой разметке сессии (`docs/measurements.md`, «Фильтр пробуждений»).

Запуск: python wake_gate_eval.py <вариант запроса> <каталог прогона>... — каталоги meeting-replay-run.sh;
python wake_gate_eval.py rule <каталог прогона>... — только правило вопроса, без модели;
python wake_gate_eval.py --test — проверка счёта на известном входе.

По каждому событию, кроме «конца записи», метка — что сделала сессия: подсказка или молчание (hints.md, строка с тем
же временем события). Ворота — Haiku через `claude -p` с ключами быстрого запуска; на входе то, что видел бы
meeting-watch: последние строки, уже отданные сессии, новые строки с прошлого события и последние подсказки сессии
до события. Печатает только счётчики и время вызова: текст встречи на экран не выводится.

Правило вопроса — повод `вопрос` остаётся, только если в фразе есть предложение на «?» не короче
`WakePolicy.MinQuestionWords` слов (а не вся фраза VAD). Отдельно — фразы без «?», которые начинаются с
вопросительного слова: их ловит только `накопилось`.
"""
import concurrent.futures
import glob
import os
import re
import subprocess
import sys
import tempfile
import time

END_OF_RECORDING = "конец записи"
MIN_QUESTION_WORDS = 3  # WakePolicy.MinQuestionWords
SEEN_LINES = 15
NEW_LINES = 40
HINTS = 3
GATE = ["claude", "-p", "--model", "haiku", "--effort", "low", "--tools", "", "--strict-mcp-config",
        "--setting-sources", "", "--disable-slash-commands", "--no-session-persistence"]
QUESTION_START = re.compile(
    r"^(как|почему|зачем|что|где|когда|кто|какой|какая|какое|какие|каким|какую|сколько|чем|откуда|куда|можно ли|"
    r"правильно ли|а если|а что|а как|а почему|а где|а зачем|разве)\b", re.IGNORECASE)

PROMPTS = {
    "v1": (
        "Ты — фильтр перед ассистентом на рабочем созвоне. Ассистент подсказывает участникам: отвечает на вопросы "
        "о проекте и технологиях и поправляет неверные утверждения. Будить его дорого. Ниже — недавние реплики, "
        "новые реплики и последние подсказки ассистента. Есть ли в НОВЫХ репликах вопрос или утверждение, на "
        "которое ассистенту стоит дать подсказку, которой ещё не было? Если сомневаешься — да. Ответь одним "
        "словом: да или нет."
    ),
    "v2": (
        "Ты — фильтр перед ассистентом на рабочем созвоне. Ассистент подсказывает участникам: отвечает на вопросы "
        "о проекте и технологиях и поправляет неверные утверждения. Будить его дорого, большинство реплик подсказки "
        "не требуют. Ответь «нет», если в НОВЫХ репликах: вопрос оборван или ещё не договорён; участники сами "
        "отвечают или разбирают вопрос; тема та же, что у последней подсказки ассистента, и нового в ней нет; "
        "разговор организационный, бытовой или риторический. Ответь «да», только если прозвучал законченный "
        "вопрос или спорное утверждение, на которые нужна новая подсказка. Ответь одним словом: да или нет."
    ),
    "v3": (
        "Ты — фильтр перед ассистентом на рабочем созвоне. Ассистент подсказывает участникам: отвечает на вопросы "
        "о проекте и технологиях и поправляет неверные утверждения. Будить его дорого. Оцени по НОВЫМ репликам, "
        "насколько нужна новая подсказка: 0 — не нужна (вопрос оборван, участники разбирают сами, тема уже закрыта "
        "последней подсказкой, разговор организационный или бытовой), 10 — точно нужна (законченный вопрос или "
        "спорное утверждение без ответа). Ответь только числом от 0 до 10."
    ),
}
SCORED = {"v3"}


def text_of(line):
    """Текст строки транскрипта без номера, времени и канала."""
    return line.split("\t", 2)[2].split(": ", 1)[-1]


def words(text):
    return re.findall(r"[^\W_]+", text)


def keeps_question(text):
    sentences = re.findall(r"[^.!?…]*[.!?…]+|[^.!?…]+$", text)
    return any(s.rstrip().endswith("?") and len(words(s)) >= MIN_QUESTION_WORDS for s in sentences)


def load(run):
    transcripts = glob.glob(os.path.join(run, "meetings", "*", "transcript.md"))
    if len(transcripts) != 1:
        sys.exit(f"{run}: ждали одну встречу в meetings/, нашли {len(transcripts)}")
    meeting = os.path.dirname(transcripts[0])
    lines = {}
    for line in open(transcripts[0], encoding="utf-8"):
        number = int(line.split("\t", 1)[0])
        lines[number] = line.rstrip("\n")
    events = [line.rstrip("\n").split("\t") for line in open(os.path.join(meeting, "events.log"), encoding="utf-8")]
    hints = {}
    for line in open(os.path.join(meeting, "hints.md"), encoding="utf-8"):
        event, written, _, text = line.rstrip("\n").split("\t", 3)
        hints[round(float(event), 3)] = (float(written), text)
    return lines, events, hints


def cases(run):
    """События прогона: (повод, метка сессии, входной текст ворот, текст строки события)."""
    lines, events, hints = load(run)
    previous = 0
    for epoch, number, reasons in events:
        number = int(number)
        if reasons == END_OF_RECORDING:
            continue
        label = hints.get(round(float(epoch), 3))
        if label is None:
            sys.exit(f"{run}: у события {number} нет строки в hints.md")
        seen = [text_of(lines[n]) for n in sorted(lines) if previous - SEEN_LINES < n <= previous]
        new = [text_of(lines[n]) for n in sorted(lines) if previous < n <= number][-NEW_LINES:]
        given = [text for written, text in sorted(hints.values()) if written < float(epoch) and text != "-"][-HINTS:]
        prompt = ("Недавние реплики:\n" + "\n".join(seen) + "\n\nНовые реплики:\n" + "\n".join(new)
                  + "\n\nПоследние подсказки ассистента:\n" + ("\n".join(given) or "нет"))
        yield reasons, "молчание" if label[1] == "-" else "подсказка", prompt, text_of(lines.get(number, "\t\t"))
        previous = number


def ask(instruction, prompt):
    started = time.perf_counter()
    with tempfile.TemporaryDirectory() as empty:
        result = subprocess.run(GATE + [instruction + "\n\n" + prompt], capture_output=True, text=True, timeout=60,
                                cwd=empty, stdin=subprocess.DEVNULL)
    answer = result.stdout.strip().lower()
    # Ответ не по форме — как сбой ворот: событие проходит (оценка 10, «да»).
    score = re.match(r"\d+", answer)
    value = int(score.group()) if score else 10
    return (value if instruction == PROMPTS.get("v3") else answer.startswith("да") or not answer.startswith("нет"),
            time.perf_counter() - started)


def report(name, rows, times=None):
    total = {"подсказка": 0, "молчание": 0}
    passed = {"подсказка": 0, "молчание": 0}
    for label, woke in rows:
        total[label] += 1
        passed[label] += woke
    print(f"{name}: ответов сессии дошло {passed['подсказка']} из {total['подсказка']}, "
          f"молчаний отсечено {total['молчание'] - passed['молчание']} из {total['молчание']}")
    if times:
        times = sorted(times)
        print(f"  время вызова: p50 {times[len(times) // 2]:.1f} с, p90 {times[len(times) * 9 // 10]:.1f} с, "
              f"max {times[-1]:.1f} с ({len(times)})")
    return passed["подсказка"], total["подсказка"], total["молчание"] - passed["молчание"], total["молчание"]


def test():
    checks = [
        (keeps_question("Как у нас настроены повторы?"), True, "вопрос в одном предложении"),
        (keeps_question("Да? Ну хорошо, тогда идём дальше по плану."), False, "короткий «да?» в длинной фразе"),
        (keeps_question("Ладно. А где это лежит?"), True, "вопрос вторым предложением"),
        (keeps_question("Почему?"), False, "одно слово"),
        (bool(QUESTION_START.match("а если упадёт база")), True, "вопросительное начало"),
    ]
    rows = [("подсказка", True), ("подсказка", False), ("молчание", False), ("молчание", True), ("молчание", False)]
    failed = 0
    for got, expected, name in checks:
        failed += got != expected
        print(f"{'ок ' if got == expected else 'НЕТ'} {name}")
    counts = report("счёт", rows)
    failed += counts != (1, 2, 2, 3)
    print(f"{'ок ' if counts == (1, 2, 2, 3) else 'НЕТ'} счёт пропущенных ответов и отсечённых молчаний")
    return failed


if __name__ == "__main__":
    if sys.argv[1:] == ["--test"]:
        sys.exit(test())
    if len(sys.argv) < 3 or (sys.argv[1] != "rule" and sys.argv[1] not in PROMPTS):
        sys.exit(f"Запуск: python wake_gate_eval.py <{'|'.join(PROMPTS)}|rule> <каталог прогона>... | --test")
    variant, runs = sys.argv[1], sys.argv[2:]
    all_cases = [case for run in runs for case in cases(run)]
    if variant == "rule":
        rows = [(label, "вопрос" not in reasons.split(",") or keeps_question(line))
                for reasons, label, _, line in all_cases]
        report("правило вопроса по предложению (только события «вопрос» могут отпасть)", rows)
        lines = [text_of(line) for run in runs for line in load(run)[0].values()]
        no_mark = sum(1 for t in lines if "?" not in t and QUESTION_START.match(t.strip()))
        print(f"фраз без «?» с вопросительным словом в начале: {no_mark} из {len(lines)}")
        sys.exit(0)
    with concurrent.futures.ThreadPoolExecutor(4) as pool:
        answers = list(pool.map(lambda case: ask(PROMPTS[variant], case[2]), all_cases))
    times = [seconds for _, seconds in answers]
    thresholds = range(1, 11) if variant in SCORED else [None]
    for threshold in thresholds:
        rows = [(case[1], woke if threshold is None else woke >= threshold)
                for case, (woke, _) in zip(all_cases, answers)]
        name = f"ворота {variant}" + ("" if threshold is None else f", будить от {threshold}")
        report(name, rows, times if threshold in (None, 1) else None)
        if threshold is None:
            for reason in ("вопрос", "накопилось"):
                report(f"  повод {reason}", [row for row, case in zip(rows, all_cases)
                                             if reason in case[0].split(",")])
