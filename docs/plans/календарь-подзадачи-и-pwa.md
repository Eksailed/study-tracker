# Календарь, подзадачи и приложение на телефоне: план

Статус: сделано, версия 0.7 от 9 ноября 2026 года. Исполнение — «native»: всё сделано в одной сессии, по одной задаче, после каждой коммит и `git push origin main`. Итог: сборка 0 предупреждений, 265 из 265 проверок (33 секунды), интерфейс проверен в Edge на 320, 390 и 1440 пикселей. Подписка из сторонних календарей, установка приложения, iPhone и Android, GitHub Actions и Docker не проверялись.

Связано: [спецификация](../specs/календарь-подзадачи-и-pwa.md), [план версии 0.6](импорт-из-obsidian.md), [README](../../README.md).

## Общие правила

- Работаем в `main`, ветки не нужны. Коммиты по-русски, без строк об авторстве Claude.
- Тесты: `~/.dotnet/dotnet.exe test tests/StudyTracker.Tests --nologo [--filter X]`. Сборка: `~/.dotnet/dotnet.exe build --nologo -v q`, нужно «Предупреждений: 0 / Ошибок: 0». Сейчас 220 проверок.
- Каждый тест сначала должен упасть (красный), потом пройти. Тест, который зелёный сразу, потому что описывает уже работающее, помечаем честно в разделе «Решения».
- После каждой задачи: «сломать и вернуть» (временно ломаем ключевое место, тест краснеет, возвращаем).
- Миграции: `~/.dotnet/dotnet.exe tool run dotnet-ef migrations add <Имя>`.
- Новый JS: `node --check` в `.github/workflows/check.yml`, подключение в `wwwroot/index.html`, и (после задачи 1) путь в список `SHELL` файла `wwwroot/sw.js`. Это проверяет тест `ShellCoversIndexAssets`.
- Многострочные правки файлов с CRLF — Python-скриптами в `Temp`; новые файлы пишем сразу.

## Что проверяем сверх спецификации

| Ситуация | Ожидание | Где проверяется |
|---|---|---|
| Название задания с запятой, `;`, `\`, переводом строки | В ленте экранируется, лента остаётся разборчивой | задача 2, `TextIsEscaped` |
| Длинное название с русскими буквами и эмодзи | Строки сворачиваются по 75 байт, символ не режется пополам | задача 2, `LongLinesAreFolded` |
| Ключ ленты другого регистра, `key` указан дважды, пробелы | Только точное совпадение, иначе `404` | задача 2, `KeyMustMatchExactly` |
| Ключ перевыпущен два раза подряд | Работает только последний | задача 2, `FeedFollowsLifecycle` |
| Сервис-воркер получил ответ `500` или запрос `POST` | Не кэшируется, не перехватывается | задача 1, `ServiceWorkerNeverTouchesData` |
| `order` с лишним, повторным или чужим `id` | `400`, порядок не меняется | задача 4, `ReorderNeedsExactIds` |
| Удалили задание в другой вкладке, а шаг правят здесь | `404` в API, в интерфейсе тост и перезагрузка списка | задача 5, браузерная проверка |

## Задача 1: приложение на телефоне (PWA)

Файлы:
- Создать: `wwwroot/manifest.webmanifest`, `wwwroot/icon-192.png`, `wwwroot/icon-512.png`, `wwwroot/apple-touch-icon.png`, `wwwroot/sw.js`, `wwwroot/pwa.js`, `tests/StudyTracker.Tests/PwaTests.cs`.
- Изменить: `wwwroot/index.html` (ссылки в `<head>`, скрипт `pwa.js`), `.github/workflows/check.yml` (`node --check wwwroot/pwa.js` и `wwwroot/sw.js`).

Тесты в `PwaTests.cs` (обычный `HttpClient` фабрики, не `ApiClient`):
- `ManifestIsServedAsJson`: `GET /manifest.webmanifest` → 200, тип содержит `manifest+json`; в JSON `name` = «Зачёт», `lang` = `ru`, `start_url` = `/`, `display` = `standalone`; значки `192x192` и `512x512` отдаются как `image/png`.
- `ServiceWorkerNeverTouchesData`: `GET /sw.js` → 200, тип содержит `javascript`; в тексте объявлены `const SKIP = [...]` ровно с `/api`, `/health`, `/calendar.ics` и условие «только `GET`» и «только `response.ok`» перед записью в кэш.
- `ShellFilesAreServed`: все пути из `const SHELL = [...]` отдаются со статусом 200.
- `ShellCoversIndexAssets`: каждый `src` и `href` стилей и скриптов из `index.html` (кроме внешних) есть в `SHELL`; сам `/` тоже.
- `IndexLinksTheApp`: в `index.html` есть `rel="manifest"`, `rel="apple-touch-icon"`, `name="theme-color"` и `pwa.js`.

Реализация:
- Значки нарисовать по `favicon.svg`: отрисовать SVG в Edge через Playwright в PNG 192, 512 и 180 пикселей на сплошном фоне цвета темы (временный сценарий в `Temp`).
- `sw.js`: `const SHELL = ['/', '/index.html', '/app.css', '/app.js', '/notifications.js', '/api-access.js', '/import.js', '/pwa.js', '/favicon.svg', '/icon-192.png', '/icon-512.png']`; `const SKIP = ['/api', '/health', '/calendar.ics']`; `const CACHE = 'zachet-shell'`. `install`: положить `SHELL` в кэш и `skipWaiting()`. `activate`: удалить чужие кэши и `clients.claim()`. `fetch`: если не `GET`, или адрес чужой, или путь начинается с элемента `SKIP`, или пути нет в `SHELL` — не вмешиваться; иначе `fetch(request)`, при `response.ok` положить копию в кэш, при сбое сети отдать `caches.match(request, {ignoreSearch:true})`.
- `pwa.js`: если есть `'serviceWorker' in navigator`, на `load` вызвать `navigator.serviceWorker.register('/sw.js')`; ошибку регистрации молча игнорировать.

Проверка: `--filter PwaTests` — 5 зелёных; весь набор зелёный; в Edge на `localhost`: воркер зарегистрирован, в кэше `zachet-shell` нет ни одного адреса `/api`, при отключённой сети страница открывается и виден экран «Повторить подключение».

Коммит: «Приложение на телефоне: манифест, значки и сервис-воркер».

## Задача 2: календарная лента — база, лента и управление

Файлы:
- Создать: `Calendar/CalendarLink.cs` (сущность и ключи), `Calendar/CalendarFeed.cs` (сборка ленты), `Calendar/CalendarEndpoints.cs`, миграция `CalendarLinks`, `tests/StudyTracker.Tests/CalendarFeedTests.cs`, `CalendarApiTests.cs`, `CalendarMigrationTests.cs`.
- Изменить: `Models.cs` (`DbSet<CalendarLink>`, индексы, каскад), `Program.cs` (`builder.Services.AddCalendar()`, `data.MapCalendar()`, `app.MapCalendarFeed()`), `wwwroot/sw.js` не меняется (адрес `/calendar.ics` уже в `SKIP`).

Интерфейсы:
- `class CalendarLink { int Id; string OwnerId; string KeyHash; DateTime CreatedAt; }`, уникальные индексы по `OwnerId` и `KeyHash`, внешний ключ на пользователя с каскадом.
- `static class CalendarKeys { static string Create(); static string Hash(string key); }` — `Create` возвращает 43 символа base64url из 32 случайных байт; `Hash` — SHA-256 в нижнем шестнадцатеричном виде (64 символа).
- `static class CalendarFeed { static string Build(IEnumerable<Assignment> tasks, DateTime nowUtc); }` — берёт `Subject` из навигации, пропускает `Done`, порядок по `DueDate`, `Id`.
- `CalendarEndpoints.AddCalendar(this IServiceCollection)` — лимитер `"calendar"` 60 запросов в минуту по IP (`context.Connection.RemoteIpAddress`), по образцу `"accounts"` в `AuthEndpoints.cs`; `MapCalendar(this RouteGroupBuilder)` — `GET/POST/DELETE /calendar`; `MapCalendarFeed(this WebApplication)` — `GET /calendar.ics` с `.AllowAnonymous().RequireRateLimiting("calendar")`.

Тесты `CalendarFeedTests` (без сервера):
- `EventIsAllDayOnDueDate`: задание 5, предмет «Физика», «Лабораторная работа 3», срок 2026-11-20 → есть `DTSTART;VALUE=DATE:20261120`, `DTEND;VALUE=DATE:20261121`, `SUMMARY:Физика: Лабораторная работа 3`, `UID:task-5@zachet`.
- `EndOfYearRollsOver`: срок 2026-12-31 → `DTEND;VALUE=DATE:20270101`.
- `DoneTasksAreSkipped`: `Done` нет в ленте, `InProgress` и `Todo` есть.
- `TextIsEscaped`: название `а,б;в\г` и перевод строки → `SUMMARY:Предмет: а\,б\;в\\г\n…`.
- `LongLinesAreFolded`: название из 120 русских букв и эмодзи → ни одна физическая строка не длиннее 75 байт UTF-8, продолжения начинаются с пробела, после разворачивания получается исходный `SUMMARY`, нет разрезанных символов.
- `LinesEndWithCrlf`: в тексте нет `\n` без предшествующего `\r`.
- `EmptyFeedIsValid`: без заданий текст начинается с `BEGIN:VCALENDAR\r\nVERSION:2.0`, содержит `X-WR-CALNAME:Зачёт` и кончается `END:VCALENDAR\r\n`.
- `KeyIsRandomAndHashed`: два `Create` различаются, длина 43, только символы `A–Za–z0–9_-`; `Hash` даёт 64 символа, равен себе при повторе и не равен ключу.

Тесты `CalendarApiTests`:
- `FeedFollowsLifecycle`: `GET /api/calendar` → `exists:false`; `POST` → 201 и `key`; `GET /calendar.ics?key=…` → 200 `text/calendar`, видны невыполненные задания; второй `POST` → новый `key`, старый → 404, новый → 200; третий `POST`, второй ключ → 404; `DELETE` → 204, ключ → 404; повторный `DELETE` → 204.
- `WrongKeyIsAlways404`: без `key`, `key=`, случайный, ключ другого регистра, чужой отозванный → 404 и пустое тело.
- `KeyMustMatchExactly`: `key=<ключ>&key=чужой` и ключ с пробелом в конце → 404.
- `OnlyOwnTasks`: у двух аккаунтов свои ленты, в каждой только свои задания.
- `KeyStoredOnlyAsHash`: в `CalendarLinks.KeyHash` лежит `CalendarKeys.Hash(key)`, ключа в базе нет; `GET /api/calendar` ключа не отдаёт.
- `ManagementNeedsCookieLogin`: без входа `GET /api/calendar` → 401; токен API → 401; `POST` без CSRF → 400.
- `FeedHeaders`: у ответа `Cache-Control: no-store` и `Content-Type: text/calendar; charset=utf-8`.
- `FeedRateLimit`: 60 запросов проходят, 61-й → 429 с `Retry-After`.
- `LogDoesNotContainKey`: после запроса ленты ни одна строка журнала (через `LogCollector`) не содержит ключ.
- `DeletingAccountRemovesLink`: если в приложении есть удаление аккаунта (`AccountCommands`) — ссылка исчезает; если удаления аккаунта нет, тест заменить проверкой, что каскад описан в модели.

`CalendarMigrationTests` (по образцу `ImportKeyMigrationTests`): на базе без таблицы `CalendarLinks` миграция сохраняет старые данные и создаёт таблицу с двумя уникальными индексами.

Проверка: `--filter Calendar` зелёный, затем весь набор; сборка без предупреждений.

Коммит: «Календарная лента .ics и управление ссылкой».

## Задача 3: календарная лента — диалог «Календарь»

Файлы:
- Создать: `wwwroot/calendar.js`.
- Изменить: `wwwroot/index.html` (кнопка `#open-calendar` в навигации, `<symbol id="i-calendar-link">` если нет подходящей иконки, `<dialog id="calendar-dialog">`, скрипт), `wwwroot/app.css` (стили `.calendar-*` в конце), `wwwroot/app.js` (добавить `'calendar'` в список личных путей `api()`), `wwwroot/sw.js` (добавить `/calendar.js` в `SHELL`), `.github/workflows/check.yml`.

Поведение (по образцу `api-access.js` и `import.js`):
- Открытие: `GET calendar` → показать «Ссылки нет» с кнопкой «Создать ссылку» или дату выдачи с кнопками «Перевыпустить и отозвать старую» (с подтверждением в диалоге, без `confirm()`) и «Выключить».
- Создание и перевыпуск: `POST calendar` → в поле только для чтения адрес `${location.origin}/calendar.ics?key=${key}`, кнопка «Скопировать» (`navigator.clipboard.writeText`, при отказе подсказка «Выделите адрес и скопируйте вручную»), предупреждение «Адрес показывается один раз».
- Закрытие диалога и событие `account-cleared` стирают адрес из страницы (`clearCalendarState`); счётчик версии отбрасывает запоздавшие ответы; все тексты через `textContent`.

Проверка: `node --check wwwroot/calendar.js`; `--filter PwaTests` (тест `ShellCoversIndexAssets` ловит забытый `SHELL`); Playwright в Edge (отдельная копия данных, порт 5188): создать → адрес виден → `GET` по адресу возвращает 200 и событие; перевыпустить → старый адрес 404; закрыть и открыть диалог → адреса нет; выключить → адрес 404; ширина 320, 390, 1440 без горизонтальной прокрутки.

Коммит: «Диалог «Календарь» для ссылки на ленту».

## Задача 4: подзадачи и важность — база и API

Файлы:
- Создать: миграция `SubtasksAndPriority`, `Endpoints/SubtaskEndpoints.cs`, `tests/StudyTracker.Tests/SubtaskApiTests.cs`, `PriorityApiTests.cs`, `SubtaskMigrationTests.cs`.
- Изменить: `Models.cs`, `Program.cs` (`data.MapSubtasks()`; конвертер строки для `AssignmentPriority` рядом с конвертером статуса), `Endpoints/AssignmentEndpoints.cs`, `Endpoints/AssignmentValidator.cs`, `tests/StudyTracker.Tests/OpenApiTests.cs`, `ApiV1Tests.cs`.

Интерфейсы:
- `enum AssignmentPriority { Normal, Important, Urgent }`; `Assignment.Priority` (по умолчанию `Normal`) и `Assignment.Subtasks` (`List<Subtask>`).
- `class Subtask { int Id; [JsonIgnore] int AssignmentId; string Title; bool Done; int Position; }`; внешний ключ на `Assignment` с каскадом, уникальный индекс `(AssignmentId, Position)` не нужен (порядок пересчитывается в одной транзакции).
- `AssignmentInput` получает последним параметром `AssignmentPriority? Priority = null`. `AssignmentValidator.Validate` отклоняет значение вне перечисления: «Выберите важность из списка.».
- `GET /assignments` отдаёт `Include(a => a.Subtasks.OrderBy(s => s.Position))`; `POST` и `PUT` задания возвращают задание с шагами; `PUT` без `priority` важность не трогает, `POST` без `priority` ставит `Normal`.
- `SubtaskEndpoints.MapSubtasks(this RouteGroupBuilder group)` — четыре адреса из спецификации; только `/api`, не `/api/v1`. Входы: `SubtaskInput(string? Title, bool? Done)` и `SubtaskOrder(int[]? Ids)`.
- Тексты ошибок `400`: «Введите название шага.», «Название шага не длиннее 140 символов.», «У задания не больше 30 шагов.», «Передайте название или отметку.», «Укажите все шаги задания по одному разу.».

Тесты `SubtaskApiTests`:
- `AddSubtasksInOrder`: три `POST` → 201, позиции 0, 1, 2; `GET assignments` отдаёт шаги в этом порядке, `done:false`.
- `RenameAndCheck`: `{done:true}` оставляет название; `{title:"  Новое  "}` → «Новое»; `{}` → 400 «Передайте название или отметку.».
- `TitleValidation`: пустое и из пробелов → 400 «Введите название шага.»; 141 символ → 400, 140 символов → 201.
- `LimitIs30`: 30 шагов → 201, 31-й → 400 «У задания не больше 30 шагов.», в базе 30.
- `DeleteRenumbers`: удаление среднего из трёх → позиции оставшихся 0 и 1, `DELETE` → 204.
- `ReorderNeedsExactIds`: `[3,1,2]` → 200 и новый порядок; пропущенный, лишний, повторный и чужой `id`, пустой список → 400 и порядок не изменился.
- `OtherAccountGets404`: четыре адреса чужого задания → 404; шаг одного задания через адрес другого своего задания → 404.
- `DeletingTaskDeletesSubtasks`: после `DELETE` задания в таблице `Subtasks` ноль строк.
- `EditingTaskKeepsSubtasks`: `PUT` задания без изменения шагов — шаги на месте и в ответе.
- `RequiresLoginAndCsrf`: без входа 401, без CSRF 400, токен API 401.

Тесты `PriorityApiTests`:
- `PriorityDefaultsToNormal`: `POST` без `priority` → `"priority":"Normal"`.
- `PriorityRoundTrip`: `POST` с `"Urgent"` → `Urgent`; `PUT` с `"Important"` → `Important`.
- `PriorityKeptWhenOmitted`: `PUT` без `priority` оставляет `Urgent`.
- `BadPriorityIs400`: `"Epic"` и `7` → 400.
- `V1ShowsSubtasksAndPriority`: `GET /api/v1/assignments` с токеном отдаёт `priority` и `subtasks`.
- `V1SetsPriorityButCannotEditSubtasks`: `POST /api/v1/assignments` с `"Important"` работает; `POST /api/v1/assignments/{id}/subtasks` → 404, шагов в базе нет.
- `ImportKeepsNormalPriority`: после импорта из Obsidian у новых заданий `Normal`.

`SubtaskMigrationTests`: старая база с заданием; после миграции задание на месте, `Priority` = 0, таблица `Subtasks` пуста, внешний ключ с каскадом есть. В `OpenApiTests` — схема задания содержит `priority` (перечисление строк) и `subtasks`.

Проверка: `--filter "Subtask|Priority"` зелёный, весь набор зелёный, сборка без предупреждений.

Коммит: «Подзадачи и важность: база и API».

## Задача 5: подзадачи и важность — интерфейс

Файлы:
- Создать: `wwwroot/subtasks.js` (разметка и обработчики шагов, чтобы не раздувать `app.js`).
- Изменить: `wwwroot/app.js` (в `renderAssignments` — значок важности, счётчик и панель шагов из `subtasks.js`; сортировка `priority`; передача `priority` из формы), `wwwroot/index.html` (поле «Важность» `#task-priority` в форме, пункт «По важности» в `#sort`, скрипт), `wwwroot/app.css`, `wwwroot/sw.js` (`SHELL`), `.github/workflows/check.yml`, `wwwroot/api-docs.html` (если там перечислены поля задания).

Поведение:
- Карточка: значок важности только для «Важного» и «Срочного» (текст «Важно», «Срочно», не только цвет); счётчик «2 из 5»; кнопка-стрелка раскрывает панель шагов (`aria-expanded`); состояние раскрытия хранится в `state` и не теряется при перерисовке.
- Панель: список шагов с чекбоксом, названием (правка по нажатию Enter/потере фокуса), кнопками «вверх», «вниз», «удалить»; поле «Добавить шаг» с отправкой по Enter; на 30 шагах поле блокируется с подсказкой.
- Строка «Все шаги готовы. Отметить задание выполненным?» с кнопкой — когда у задания есть шаги, все отмечены, а статус не `Done`; кнопка вызывает ту же функцию, что круглая отметка задания. Статус по галочкам сам не меняется.
- Сортировка «По важности»: `Urgent`, `Important`, `Normal`, внутри — срок, затем `id`.
- Ошибка `404` от шага — тост «Задание изменилось. Список обновлён.» и перезагрузка списка; изменения шагов отправляются по одному запросу, кнопки на время запроса блокируются; запоздавший ответ после выхода из аккаунта отбрасывается (`sessionGeneration`).
- Тексты через `textContent` или `escapeHtml`, без inline-скриптов и стилей.

Проверка: `node --check` всех JS; весь набор тестов зелёный; Playwright в Edge на ширине 320, 390 и 1440: добавить, отметить, переставить и удалить шаг; отметить все шаги → видно предложение, статус не поменялся; нажать → задание выполнено; сортировка по важности; форма сохраняет важность; клавиатура: Tab и Enter в поле шага, Escape закрывает диалоги.

Коммит: «Подзадачи и важность в интерфейсе».

## Задача 6: документы и итоговая проверка

Файлы: `README.md` (версия 0.7 в статусе, разделы «Календарная лента», «Подзадачи и важность», «Приложение на телефоне», предупреждение про журнал Caddy и про ссылку-секрет, `Calendar/` и новые файлы в структуре, новые `node --check`, число проверок и время прогона, таблица «Шаг 0.7», раздел «Дальше по дорожной карте» с запасными идеями), `docs/specs/календарь-подзадачи-и-pwa.md` и этот план (статус «сделано», что собрано и проверено, что нет, раздел «Решения»), `.github/workflows/check.yml`.

Шаги:
- Прогнать весь набор тестов и сборку, записать число проверок и время.
- Самопроверка по всей ветке: `git diff bca31dc..HEAD`, сверка со спецификацией, поиск забытых `TODO`, лишних файлов и секретов; записать «Final review: self-review (no subagent tool)».
- Обновить документы, закоммитить и отправить.

Коммит: «Версия 0.7: документация и итоговая проверка».

## Решения

Отступления от плана и решения, принятые при исполнении. У каждого указана цена ошибки.

- **Задача 1.** Проверка в браузере ложно показала `/api-access.js` в кэше: он начинается с `/api`. Воркер сравнивает путь целиком или с `/` после префикса, поэтому `/api/*` в кэше нет. Цена ошибки: нулевая.
- **Задача 2.** `CalendarFeed.Build(tasks)` не принимает текущее время: `DTSTAMP` берётся из `CreatedAt` задания, и лента между запросами не меняется. Цена: нулевая.
- **Задача 2.** В `DatabaseSetupTests.NotificationMigrationPreservesAccounts` добавлен `DROP TABLE CalendarLinks`: тест откатывает все миграции после уведомлений, и без этого повторное применение падает. Цена: нулевая.
- **Задача 2.** Тесты календаря не были «красными до кода» (до кода они не компилировались). Вместо этого «сломать и вернуть» для свёртки строк, экранирования `;` и лимита. Фильтр `Done` в запросе к базе дублирует фильтр в `Build`, поэтому его поломку тесты не ловят: страховка двойная. Цена: непроверенный фильтр в запросе.
- **Задача 4.** Красный этап API-тестов не наблюдался (тесты и код писались подряд). Вместо него «сломать и вернуть» в пяти местах: повторы в порядке, владелец в `Find`, сброс важности в `PUT`, пересчёт позиций, лимит 30; каждая поломка краснила свой тест. Цена: один непроверенный тест.
- **Задача 4.** В документе OpenAPI ответов заданий не было вообще, поэтому добавлены `.Produces<Assignment>()` для `GET`, `POST` и `PUT`: иначе схема с `priority` и `subtasks` в документ не попадает. Цена: ответы заданий описаны и для внутренних адресов (в документ они не попадают).
- **Задача 4.** В `DatabaseSetupTests` откат дополнен `DROP TABLE Subtasks` и `DROP COLUMN Priority`: иначе повторная миграция падает на существующих объектах. Цена: нулевая.
- **Задача 5.** В проекте нет тестов на JavaScript, поэтому красный этап заменён проверкой в браузере (Playwright в Edge на 320, 390, 1440) и «сломать и вернуть»: без `/subtasks.js` в `SHELL` падает `PwaTests.ShellCoversIndexAssets`. Цена: поломку интерфейса шагов автотест не поймает.
- **Итоговая проверка.** Подагентов в этой сессии не было, обзор всей ветки сделан самим исполнителем (`git diff bca31dc..HEAD`). Критичных и важных замечаний не нашлось. Отложено: длинное название шага на 390 пикселей обрезается в поле (видно при правке).
