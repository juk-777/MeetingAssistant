# План: скилл сверяет версию приложения записи с версией плагина

**Статус:** в работе.

Спека: `docs/specs/2026-11-13-app-version.md`. Issue: #31. Кода в плане нет — он в репозитории.

| # | Этап | Готово |
|---|---|---|
| 1 | Скрипт `check-app-version`, вызов из `meeting-start` и `meeting-refine`, тесты скилла | ✅ |
| 2 | `SKILL.md` (код 4), README, `docs/architecture.md`, `docs/decisions.md`, версия плагина | ✅ |
| 3 | Живая проверка в WSL и Git Bash | ✅ |
| 4 | Закрытие: сверка со спекой, мутации, инспекция, саморевью, ретро | |

## 1. Сверка версии

`check-app-version`: версия плагина из `plugin.json` тремя каталогами выше скриптов, версия приложения —
`meeting --version` через `powershell.exe`; приложение старее или ответ не распознан — код 4, нет приложения —
код 3, `MEETING_ANY_APP_VERSION=1` или нет `plugin.json` — без сверки. `meeting-start` и `meeting-refine` зовут его
до окна.

**Готово, когда:** тесты скилла зелёные в WSL; мутации из критерия 1 спеки роняют свои тесты.

*Подтверждено:* тесты скилла — WSL 62 прогнано, 0 упало; Git Bash 58, 0 упало. Мутанты и чем убиты: снять сверку
в `meeting-start` / `meeting-refine` — `*_WhenAppOlderThanPlugin_RefusesBeforeWindow`; сравнение строками и
обратное сравнение — `*_WhenAppOlderThanPlugin_*` и `*_WhenAppSameOrNewerOrDevBuild_*`; без пропуска `-dev` и без
снятия `\r` — `*_WhenAppSameOrNewerOrDevBuild_*`; без флага — `*_WhenAnyVersionAllowed_*`,
`start_WhenOlderAppAllowed_OpensWindow`; нераспознанная проходит — `*_WhenVersionNotRecognized_*`; код 3 теряется —
`*_WhenAppNotInstalled_*`; нет `plugin.json` роняет скрипт — `*_WhenNoPluginManifest_*`. Выжил `pwd -P` → `pwd`:
`..` после ссылки на каталог разрешает система, физический путь не нужен — снят, спека поправлена.

## 2. Доки и скилл

**Готово, когда:** `SKILL.md` ведёт разговор по коду 4; README, `docs/architecture.md`, `docs/decisions.md`
описывают сверку; плагин 0.2.5.

## 3. Живая проверка

Приложение 0.2.4, скилл из рабочей копии (плагин 0.2.5): `meeting-start` — код 4 со строкой отказа; с
`MEETING_ANY_APP_VERSION=1` окно открывается.

*Подтверждено:* настоящий `powershell.exe`, установленное приложение 0.2.4. WSL: `check-app-version` — код 4,
«Приложение записи 0.2.4 старее скилла 0.2.5. Обновить — meeting-install.», 0.5 с; с флагом — код 0;
`meeting-start` — код 4, новой встречи в корне нет. Git Bash: `check-app-version` — код 4, та же строка. Окно
с флагом вживую не открывалось (запись встречи ради проверки) — его держит тест `start_WhenOlderAppAllowed_OpensWindow`.

## 4. Закрытие

Сверка критериев спеки, Git Bash в CI, саморевью, ретро, pull request.
