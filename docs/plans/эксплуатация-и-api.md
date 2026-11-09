# Эксплуатация и API: план реализации

Статус: сделано, версия 0.5 от 9 ноября 2026 года. Все 9 задач выполнены, каждая закоммичена и отправлена в `main` отдельно. Итог: сборка 0 предупреждений, 160 из 160 проверок за 28 секунд. Не проверено: Docker и контейнерные сценарии, результат GitHub Actions.

Связано: [спецификация](../specs/эксплуатация-и-api.md), [README](../../README.md), [запуск на сервере](../запуск-на-сервере.md), [план тестов на C#](тесты-на-csharp.md), [Program.cs](../../Program.cs), [CI](../../.github/workflows/check.yml).

> **Для исполнителей:** `superpowers:executing-plans` (способ «native» выбран пользователем заранее). Шаги отмечаются флажками `- [ ]`.

**Цель.** Добавить в «Зачёт» проверку готовности, журнал запросов с номером, команды `--backup` и `--restore`, токены доступа к `/api/v1` и документацию API, ничего не ломая в существующем `/api`.

**Подход.** Сначала то, что не трогает остальной код (готовность, журнал, копии), потом токены: таблица и выпуск, затем вход по токену на `/api/v1`, затем документация и интерфейс. Каждая проверка пишется раньше кода и после зелёного прогона проверяется шагом «сломать и вернуть».

**Технологии.** .NET 10 (`~/.dotnet/dotnet.exe`), EF Core 10 + SQLite, xUnit с `WebApplicationFactory<Program>` (помощники в `tests/StudyTracker.Tests/Support/`), новый пакет `Microsoft.AspNetCore.OpenApi` 10.0.14.

## Общие правила

Действуют во всех задачах.

- Команды `dotnet` — это `~/.dotnet/dotnet.exe` (`global.json` требует `10.0.100`, а в `PATH` лежит 9.0.318). Тесты: `~/.dotnet/dotnet.exe test tests/StudyTracker.Tests --nologo`. Перед задачей — 94 зелёных проверки, после — на новые больше.
- Новые тесты закрепляют новое поведение, поэтому сначала должны упасть (красный шаг), потом пройти. Проверки, которые сразу проходят (границы, защита в глубину), получают шаг «сломать и вернуть»: портим одну строку рабочего кода, видим красный тест, возвращаем.
- Работа в `main`. После задачи: `git add`, `git commit` с сообщением по-русски, **без строк `Co-Authored-By` и «Generated with Claude Code»** (прямое указание пользователя в `CLAUDE.md`), затем `git push origin main`.
- Существующее поведение `/api` не меняется: все 94 текущие проверки остаются зелёными после каждой задачи.
- Формат ошибок прежний: `{"error": "текст"}`. Тексты по-русски.
- Время токенов (срок, `LastUsedAt`, отзыв) считать через `TimeProvider` (в тестах — `FakeTimeProvider`), а не `DateTime.UtcNow`, чтобы срок и «не чаще раза в 5 минут» проверялись без ожидания.
- Docker Desktop выключен: контейнерные сценарии и `docker build` не запускаются, об этом прямо пишется в README.

## Что проверяем сверх спецификации

Входы и сбои, которые легко пропустить; каждый закреплён тестом в задаче-владельце.

| Ситуация | Что ожидаем | Где закреплено |
| --- | --- | --- |
| В запросе и cookie, и неверный `Bearer` | `401`, на cookie не откатываемся | Задача 6, `GarbageTokenDoesNotFallBackToCookie` |
| Верный токен на `/api/subjects` (не `/api/v1`) | `401` | Задача 6, `TokenIsRejectedOutsideV1` |
| Токен создаёт токен (`POST /api/tokens` с `Bearer`) | `401` | Задача 5, `TokenCannotManageTokens` |
| Токен, срок которого истёк ровно сейчас | `401` (граница `ExpiresAt <= now`) | Задача 6, `ExpiredTokenIsRejected` |
| Токен или query в журнале | Ни `zt_…`, ни query в записях | Задача 6, `TokenAndQueryAreNotLogged` |
| База версии 0.4 с данными проходит миграцию `ApiTokens` | Аккаунт и задание целы | Задача 5, `MigrationKeepsExistingData` |
| `/health/ready` при поломке: в теле исключение или путь | Только имена проверок и `ok`/`fail` | Задача 1, `ReadyHidesExceptionDetails` |
| Копия при открытом соединении с записью | Копия целая | Задача 3, `BackupWhileConnectionOpen` |
| Неисправная копия | Файл копии удалён, код `1` | Задача 3, `FailedIntegrityRemovesBackup` |

---

## Файлы

| Файл | Ответственность |
| --- | --- |
| `Health.cs` (новый) | `MapHealth`: `/health` и `/health/ready`, проверки `database`, `schema`, `keys`, `telegram` |
| `RequestLogging.cs` (новый) | Middleware: `X-Request-Id`, одна строка на запрос |
| `BackupCommands.cs` (новый) | `BackupAsync`, `RestoreAsync` |
| `CommandLine.cs` | Флаги `--backup`, `--restore` |
| `ApiTokens/TokenGenerator.cs` (новый) | `zt_…` и SHA-256 |
| `ApiTokens/TokenEndpoints.cs` (новый) | `GET/POST /api/tokens`, `DELETE /api/tokens/{id}` |
| `ApiTokens/ApiTokenAuth.cs` (новый) | Схема `ApiToken`, области, лимитер токена |
| `Models.cs`, `Migrations/*_ApiTokens.cs` | `ApiToken` и таблица |
| `Endpoints/SubjectEndpoints.cs`, `Endpoints/AssignmentEndpoints.cs` | Параметр `prefix` для `Location` |
| `SecurityMiddleware.cs` | CSRF не проверяется для запросов с токеном |
| `ApiTokens/ApiDocs.cs` (новый) | `/api/v1/openapi.json` |
| `wwwroot/api-docs.html`, `api-docs.js` (новые) | Страница `/api/docs` |
| `wwwroot/index.html`, `app.js`, `app.css` | Раздел «Доступ к API» |

---

### Задача 1: готовность `/health/ready`

**Файлы:** создать `Health.cs`, `tests/StudyTracker.Tests/HealthTests.cs`; изменить `Program.cs` (убрать строку `app.MapGet("/health", …)`, вызвать `app.MapHealth()`).

**Интерфейс.**
- Производит: `public static void MapHealth(this WebApplication app)`. `GET /health` → `200 {"status":"ok"}` без проверок. `GET /health/ready` → `200` или `503` и JSON `{"status":"ok"|"fail","checks":{"database":"ok","schema":"ok","keys":"ok","telegram":"disabled"|"ready"|"error"}}`. Код `503`, если провалилась любая из `database`, `schema`, `keys`; `telegram` на код не влияет. Подробности исключений пишутся в журнал (`ILogger<Health>` на уровне `Warning`), в ответ не попадают.
- Проверки: `database` — `SELECT 1`; `schema` — `GetPendingMigrationsAsync()` пуст; `keys` — создать и удалить временный файл в `Tracker:KeyDirectory` (по умолчанию `<DataDirectory>/keys`, как в `Program.cs`); `telegram` — `disabled`, если `TelegramOptions.Configured` ложно, `ready`, если `TelegramClient.Ready`, иначе `error`.

**Тесты** (`HealthTests`):
- `LiveAlwaysOk` — `GET /health` → `200`, `status` = `ok`. Проходит сразу (поведение уже есть).
- `ReadyOnHealthyHost` — `GET /health/ready` → `200`, `checks.database/schema/keys` = `ok`, `checks.telegram` = `disabled`.
- `ReadyFailsWhenKeysDirectoryIsGone` — после старта хоста удалить папку ключей (`Directory.Delete`) → `503`, `checks.keys` = `fail`, `checks.database` = `ok`.
- `ReadyFailsOnPendingMigration` — после старта удалить последнюю строку из `__EFMigrationsHistory` (SQL через `factory.Scope`) → `503`, `checks.schema` = `fail`.
- `ReadyFailsWhenDatabaseUnavailable` — фабрика подменяет `DbContextOptions<TrackerDb>` на настройки с перехватчиком подключения, который по флагу бросает исключение при открытии; флаг включается после старта → `503`, `checks.database` = `fail`.
- `ReadyHidesExceptionDetails` — в теле ответа (текст) нет подстрок `Exception`, `Sqlite`, пути временной папки и слова `at `.
- `TelegramStateDoesNotChangeCode` — хост с `Telegram:BotToken`, но без подключённого бота (`ManualMode`, `InitializeAsync` не вызывался) → `200`, `checks.telegram` = `error`.

- [x] Шаг 1. Написать `HealthTests`, кроме `LiveAlwaysOk`. Запустить `~/.dotnet/dotnet.exe test tests/StudyTracker.Tests --nologo --filter HealthTests`. Ожидается: красные (на `/health/ready` пока `404`).
- [x] Шаг 2. Реализовать `Health.cs`, подключить в `Program.cs`.
- [x] Шаг 3. Тот же запуск. Ожидается: `HealthTests` зелёные.
- [x] Шаг 4. «Сломать и вернуть»: временно заменить `GetPendingMigrationsAsync().Any()` на `false` → красный `ReadyFailsOnPendingMigration`; вернуть. Так же для проверки `keys`.
- [x] Шаг 5. Полный прогон `dotnet test tests/StudyTracker.Tests --nologo` → все зелёные (94 + новые).
- [x] Шаг 6. Коммит `Добавлена проверка готовности /health/ready`, `git push origin main`.

---

### Задача 2: журнал запросов с номером

**Файлы:** создать `RequestLogging.cs`, `tests/StudyTracker.Tests/RequestLoggingTests.cs`; изменить `Program.cs`.

**Интерфейс.**
- Производит: `public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app)`. Подключается **первым**, до `UseExceptionHandler()`, чтобы видеть итоговый статус `500`.
- Поведение: в ответ добавляется `X-Request-Id` = `HttpContext.TraceIdentifier` (через `Response.OnStarting`). После запроса — одна запись `Information` в `ILogger<RequestLogging>`: `"{Method} {Path} {StatusCode} {ElapsedMs} мс {RequestId}"`, где `Path` — без query. Пути `/health` и `/health/ready` не пишутся. Запрос, оборвавшийся исключением, пишется как `500`.
- В Production (`!IsDevelopment()`) в `Program.cs`: `builder.Logging.AddJsonConsole()`; в Development остаётся текстовый вывод.

**Тесты** (`RequestLoggingTests`, `LogCollector` подключается через `ConfigureLogging` в фабрике):
- `ResponseHasRequestId` — любой ответ `/api/token` содержит непустой `X-Request-Id`; два запроса дают разные значения.
- `RequestLineHasMethodPathStatusAndId` — после `GET /api/subjects` без входа в записях ровно одна строка с `GET`, `/api/subjects`, `401` и значением `X-Request-Id` из ответа.
- `QueryIsNotLogged` — `GET /api/subjects?secret=abc123` → ни в одной записи нет `secret` и `abc123`.
- `HealthIsNotLogged` — запросы к `/health` и `/health/ready` не оставляют записей `RequestLogging`.
- `ServerErrorIsLoggedAs500` — если для теста есть адрес, падающий исключением (в тестовой фабрике добавляется минимальный адрес через `services`/`Configure`), запись содержит `500` и заголовок `X-Request-Id`. Если такой адрес поднять нельзя, ruling в журнале и проверка вручную.

- [x] Шаг 1. Написать тесты; запустить `--filter RequestLoggingTests`. Ожидается: красные, кроме `QueryIsNotLogged` и `HealthIsNotLogged` (они пока проходят — для них нужен шаг «сломать и вернуть»).
- [x] Шаг 2. Реализовать middleware, подключить, добавить `AddJsonConsole` для Production.
- [x] Шаг 3. Запустить `--filter RequestLoggingTests` → зелёные.
- [x] Шаг 4. «Сломать и вернуть»: временно писать `Request.Path + Request.QueryString` → красный `QueryIsNotLogged`; убрать условие про `/health` → красный `HealthIsNotLogged`; вернуть.
- [x] Шаг 5. Проверить вручную: `ASPNETCORE_ENVIRONMENT=Production` и `Tracker:DataDirectory` во временной папке, запустить, сделать один запрос — строка журнала в консоли это JSON. Записать результат в журнал работы.
- [x] Шаг 6. Полный прогон → зелёные. Коммит `Журнал запросов: X-Request-Id и одна строка на запрос`, push.

---

### Задача 3: резервная копия `--backup`

**Файлы:** создать `BackupCommands.cs`, `tests/StudyTracker.Tests/BackupTests.cs`; изменить `CommandLine.cs` (добавить `--backup`, `--restore` в `Flags`; `Flags` нужен, чтобы значение после флага не уходило в конфигурацию как аргумент).

**Интерфейс.**
- Производит: `public static class BackupCommands { public static Task<string> BackupAsync(IServiceProvider services, string folder, TimeProvider time); }` — создаёт `folder/tracker-ГГГГММДД-ЧЧммсс.db` онлайн-копией SQLite (`SqliteConnection.BackupDatabase`), копирует папку ключей в `folder/tracker-ГГГГММДД-ЧЧммсс-keys`, проверяет копию `PRAGMA integrity_check`; возвращает путь к `.db`. Время в имени — UTC от `TimeProvider`. Папка `folder` создаётся, если её нет.
- Если `integrity_check` вернул не `ok`: файл копии (и копия ключей) удаляются, бросается `InvalidOperationException` с русским текстом; `CommandLine` печатает его в `stderr` и ставит `Environment.ExitCode = 1`.
- Печатает в консоль пути копии базы и ключей. Старые копии не удаляет. Если `--backup` без значения — `Укажите папку после --backup.` и код `1`.

**Тесты** (`BackupTests`, команда вызывается как метод `BackupAsync` на сервисах хоста; `FakeTimeProvider` задаёт время `2026-11-10 08:09:10 UTC`):
- `BackupCreatesOpenableCopyWithRecords` — аккаунт и предмет с заданием; после `BackupAsync` существует `tracker-20261110-080910.db`, открывается через `SqliteConnection` и содержит 1 предмет и 1 задание; существует папка `tracker-20261110-080910-keys` и в ней есть файл.
- `BackupPassesIntegrityCheck` — `PRAGMA integrity_check` копии = `ok`.
- `BackupWhileConnectionOpen` — открыта запись через ещё одно подключение (транзакция не закрыта) → копия делается, открывается, не содержит незавершённой записи.
- `FailedIntegrityRemovesBackup` — искусственно портим результат проверки (в тесте подменяется способ проверки, например параметр `Func<string, Task<string>> integrity` по умолчанию делает `PRAGMA`) → `InvalidOperationException`, в папке копий нет `.db` и `-keys`.
- `MissingFolderArgumentFails` — `CommandLine` с `--backup` без значения → код `1`, сообщение в `stderr`.
- `OldBackupsAreKept` — две копии с разным временем лежат обе.

- [x] Шаг 1. Написать тесты → `--filter BackupTests` красные.
- [x] Шаг 2. Реализовать `BackupAsync` и разбор `--backup` в `CommandLine`.
- [x] Шаг 3. `--filter BackupTests` → зелёные.
- [x] Шаг 4. «Сломать и вернуть»: временно не копировать ключи → красный `BackupCreatesOpenableCopyWithRecords`; временно не удалять файл при ошибке → красный `FailedIntegrityRemovesBackup`; вернуть.
- [x] Шаг 5. Вручную: `dotnet run --no-build -- --backup <временная папка>` на локальной базе, убедиться, что пути напечатаны и файлы есть.
- [x] Шаг 6. Полный прогон → зелёные. Коммит `Команда --backup: проверенная копия базы и ключей`, push.

---

### Задача 4: восстановление `--restore`

**Файлы:** изменить `BackupCommands.cs`, `CommandLine.cs`, `tests/StudyTracker.Tests/BackupTests.cs`.

**Интерфейс.**
- Производит: `public static Task RestoreAsync(string backupDb, string dataDirectory, string keyDirectory)`. Берёт копию базы `X.db` и её папку ключей `X-keys` (то же имя без `.db` плюс `-keys`) и кладёт `tracker.db` и ключи в папку данных.
- Отказывает с `InvalidOperationException` (русский текст, код `1`), если: в папке данных уже есть `tracker.db`; нет файла копии; нет парной папки ключей. Ничего не перезаписывает.
- В `CommandLine`: `--restore <копия.db>` восстанавливает в `Tracker:DataDirectory`. Важно: `Program.cs` вызывает `DatabaseSetup.PrepareAsync` **до** `CommandLine.TryRunAsync`, а на пустой папке `PrepareAsync` без `--migrate` падает «Схема базы не обновлена». Поэтому `--restore` обрабатывается раньше, в `Program.cs` до подготовки базы (рядом с разбором `migrate`), либо `PrepareAsync` пропускается для этой команды. Решение записать в журнал работы как ruling.

**Тесты** (`BackupTests`):
- `RestoreBringsBackAccountAndTask` — копия хоста с аккаунтом и заданием; новая пустая папка; `RestoreAsync` → новый хост на этой папке: вход тем же паролем даёт `200`, `GET /api/assignments` возвращает то же задание.
- `RestoreRefusesNonEmptyDirectory` — в папке уже есть `tracker.db` → `InvalidOperationException`, содержимое `tracker.db` не изменилось (сравнение размера и времени записи).
- `RestoreRefusesMissingKeys` — нет `-keys` → отказ, `tracker.db` не создан.
- `RestoreKeepsSessions` — до копии выполнен вход, cookie сохранён; после восстановления и запуска нового хоста `GET /api/auth/me` с тем же cookie → `200` (ключи Data Protection восстановились).
- `RestoreCommandExitsWithOne` — через `CommandLine`/запуск процесса или вызов точки входа: `--restore` на непустую папку → код `1`.

- [x] Шаг 1. Написать тесты → красные.
- [x] Шаг 2. Реализовать `RestoreAsync` и разбор `--restore`; решить порядок с `PrepareAsync`.
- [x] Шаг 3. `--filter BackupTests` → зелёные.
- [x] Шаг 4. «Сломать и вернуть»: убрать проверку `File.Exists(tracker.db)` → красный `RestoreRefusesNonEmptyDirectory`; не копировать ключи → красный `RestoreKeepsSessions`; вернуть.
- [x] Шаг 5. Полный прогон → зелёные. Коммит `Команда --restore: восстановление в пустую папку данных`, push.

---

### Задача 5: токены — таблица, выпуск, список, отзыв

**Файлы:** создать `ApiTokens/TokenGenerator.cs`, `ApiTokens/TokenEndpoints.cs`, миграцию `Migrations/*_ApiTokens.cs` (командой `~/.dotnet/dotnet.exe ef migrations add ApiTokens`, если `dotnet ef` недоступен — вручную по образцу `Notifications`), `tests/StudyTracker.Tests/ApiTokenTests.cs`; изменить `Models.cs`, `Program.cs`.

**Интерфейс.**
- Производит: `public class ApiToken { int Id; string OwnerId; string Name; string Hash; string Prefix; int Scopes; long CreatedAt; long ExpiresAt; long? LastUsedAt; long? RevokedAt }` (время — UTC, миллисекунды), `DbSet<ApiToken> ApiTokens`; внешний ключ `OwnerId` → аккаунт с каскадным удалением; уникальный индекс по `Hash`; индекс по `OwnerId`.
- `public static class TokenGenerator { static (string Token, string Hash, string Prefix) Create(); static string Hash(string token); }` — `Token` = `zt_` + 43 символа base64url из 32 случайных байт (`RandomNumberGenerator`); `Hash` — SHA-256 в base64; `Prefix` — первые 8 символов токена.
- `public static void MapTokens(this RouteGroupBuilder group)` на группе `/api` (cookie + CSRF, как остальное):
  - `GET /api/tokens` → список `{id, name, prefix, scopes:[…], createdAt, expiresAt, lastUsedAt, revoked, expired}` без хэшей;
  - `POST /api/tokens` с `{name, scopes, days}` → `201`, тело `{id, name, prefix, scopes, expiresAt, token}`; заголовок `Location: /api/tokens/{id}`; сам токен только здесь;
  - `DELETE /api/tokens/{id}` → `204`, ставит `RevokedAt`; повтор → `204`, время отзыва не меняется; чужой или несуществующий → `404`.
- Проверки: имя после `Trim()` 1–60 символов; `scopes` — непустой список из `tasks:read`, `tasks:write`, без неизвестных (повторы допустимы, схлопываются); `days` — целое 1–365, по умолчанию 90 при отсутствии; всё остальное `400` с русским текстом. Действующих (не отозванных и не просроченных) токенов не больше 10: одиннадцатый → `409`.
- Время берётся из `TimeProvider` (зарегистрировать `TimeProvider.System` в `Program.cs`, если ещё нет; тесты подменяют `FakeTimeProvider`).

**Тесты** (`ApiTokenTests`, вход по cookie):
- `CreateReturnsTokenOnceAndStoresOnlyHash` — `POST` → `201`, `token` начинается с `zt_`, длина 46; `GET /api/tokens` токен не содержит; в базе поле `Hash` ≠ токен, равно `TokenGenerator.Hash(token)`, `Prefix` = первые 8 символов.
- `ListShowsOwnTokensOnly` — второй аккаунт не видит чужих токенов; `DELETE` чужого → `404`.
- `RevokeIsIdempotent` — первый `DELETE` → `204`, второй → `204`, `RevokedAt` тот же.
- `ValidationRejectsBadInput` — пустое имя, имя из 61 символа, неизвестная область, пустой список областей, `days` = 0 и 366 → `400` во всех случаях; имя из 60 символов и `days` = 1 и 365 → `201`.
- `DefaultDaysIs90` — без `days` срок = `now + 90 дней` (по `FakeTimeProvider`).
- `LimitOfTenActiveTokens` — 10 → `201`, 11-й → `409`; после отзыва одного 11-й → `201`; просроченный токен (время сдвинуто) не считается действующим.
- `TokenCannotManageTokens` — запрос `GET/POST/DELETE /api/tokens` с `Authorization: Bearer <корректный токен>` без cookie → `401` (проверка закрывается в задаче 6, когда появится схема; в этой задаче тест проходит, так как Bearer просто не распознаётся; оставить для «сломать и вернуть» в задаче 6).
- `ManagementNeedsCsrf` — `POST /api/tokens` без CSRF → `400`.
- `MigrationKeepsExistingData` — база, созданная миграциями до `RecordOwners`/`DeliveryThrottle` (последняя миграция версии 0.4) с аккаунтом и заданием, затем `Migrate()` до конца → аккаунт и задание на месте, таблица `ApiTokens` пуста.
- `DeletingAccountDeletesTokens` — после удаления пользователя из `UserManager` строки токена нет (каскад).

- [x] Шаг 1. Написать тесты (модели ещё нет, поэтому тесты сначала не собираются; добавить пустые типы, чтобы увидеть красные, а не ошибки сборки).
- [x] Шаг 2. Добавить `ApiToken`, `DbSet`, `OnModelCreating`, создать миграцию; убедиться по `git diff`, что миграция только добавляет `ApiTokens`.
- [x] Шаг 3. Реализовать `TokenGenerator` и `MapTokens`, подключить `data.MapTokens()` в `Program.cs`.
- [x] Шаг 4. `--filter ApiTokenTests` → зелёные.
- [x] Шаг 5. «Сломать и вернуть»: сохранять токен вместо хэша → красный `CreateReturnsTokenOnceAndStoresOnlyHash`; проверять `OwnerId` не по аккаунту → красный `ListShowsOwnTokensOnly`; предел 10 заменить на 11 → красный `LimitOfTenActiveTokens`; вернуть.
- [x] Шаг 6. Полный прогон → зелёные. Коммит `Токены доступа: таблица, выпуск, список и отзыв`, push.

---

### Задача 6: вход по токену на `/api/v1`

**Файлы:** создать `ApiTokens/ApiTokenAuth.cs`, `tests/StudyTracker.Tests/ApiV1Tests.cs`; изменить `Program.cs`, `SecurityMiddleware.cs`, `Endpoints/SubjectEndpoints.cs`, `Endpoints/AssignmentEndpoints.cs`, `AuthEndpoints.cs` (лимитер и `Retry-After`), `ApiTokenTests.cs`; расширить `ApiClient` методом отправки с заголовком `Authorization`.

**Интерфейс.**
- Производит: схема аутентификации `ApiToken` (`AuthenticationHandler`):
  - заголовка `Authorization: Bearer …` нет → `NoResult`;
  - путь не под `/api/v1` (включая `/api/subjects`, `/api/tokens`, `/api/auth`) и заголовок есть → `Fail` (итог `401`);
  - токен не `zt_…`, не найден по хэшу, `RevokedAt` задан, `ExpiresAt <= now` → `Fail`;
  - успех → принципал с `NameIdentifier` = `OwnerId`, claim `token-id`, claims `scope` (`tasks:read`/`tasks:write`), `AuthenticationType` = `ApiToken`; `LastUsedAt` обновляется не чаще раза в 5 минут;
  - `Challenge` → `401` с `WWW-Authenticate: Bearer` и телом `{"error":"Неверный или просроченный токен."}`; `Forbid` → `403` с телом `{"error":"Токену не хватает прав для этого действия."}`.
- Выбор схемы: схема-политика `Smart` с `ForwardDefaultSelector`: есть заголовок `Authorization: Bearer …` → `ApiToken`, иначе `Identity.Application` (cookie). `Smart` назначается схемой аутентификации, вызова и запрета по умолчанию после `AddIdentity`. Нельзя использовать несколько схем в одной политике авторизации: тогда мусорный токен «спасётся» cookie.
- Группа `app.MapGroup("/api/v1")` с `RequireAuthorization("ApiScope")` и `RequireRateLimiting("tokens")`, внутри `MapSubjects("/api/v1")` и `MapAssignments("/api/v1")`. `MapSubjects`/`MapAssignments` получают параметр `string prefix = "/api"`, который попадает в `Location` (`$"{prefix}/subjects/{id}"`); существующие вызовы не меняются.
- Политика `ApiScope`: если принципал аутентифицирован схемой `ApiToken` — `GET` требует claim `scope=tasks:read`, `POST`/`PUT`/`DELETE` — `tasks:write`, иначе `Forbid` (`403`); если по cookie — достаточно входа.
- Лимитер `tokens`: ключ — `token-id` (для запросов по cookie — без ограничения), окно 1 минута, 120 запросов, `QueueLimit = 0`. `OnRejected` добавляет `Retry-After` в секундах (из метаданных аренды) — и для политики `accounts`.
- `UseTrackerSecurity`: если принципал аутентифицирован схемой `ApiToken`, проверка CSRF пропускается; для cookie-запросов на `/api/v1` проверка остаётся.

**Тесты** (`ApiV1Tests`, `ApiTokenTests`; токен выпускается по cookie, затем запросы идут отдельным клиентом без cookie с `Authorization`):
- `TokenReadsSubjectsAndAssignments` — токен с `tasks:read`: `GET /api/v1/subjects` и `/api/v1/assignments` → `200`, данные владельца.
- `TokenWritesWithoutCsrf` — токен с `tasks:read, tasks:write`: `POST /api/v1/subjects` без CSRF → `201`, `Location` = `/api/v1/subjects/{id}`; `POST /api/v1/assignments` → `201` с `Location` `/api/v1/assignments/{id}`; `PUT` → `200`; `DELETE` → `204`.
- `ReadScopeCannotWrite` — токен только `tasks:read`: `POST` → `403`, ничего не создано.
- `WriteScopeCannotRead` — токен только `tasks:write`: `GET` → `403`, `POST` проходит.
- `CookieOnV1StillNeedsCsrf` — сеанс по cookie: `POST /api/v1/subjects` без CSRF → `400`, с CSRF → `201`; `Location` с префиксом `/api/v1`.
- `OldApiLocationUnchanged` — `POST /api/subjects` → `Location` = `/api/subjects/{id}`.
- `TokenIsRejectedOutsideV1` — верный токен на `/api/subjects`, `/api/assignments`, `/api/auth/me`, `/api/tokens` → `401`.
- `TokenCannotManageTokens` — теперь с реальной схемой: `GET/POST/DELETE /api/tokens` по токену → `401`.
- `GarbageTokenDoesNotFallBackToCookie` — клиент со входом по cookie и заголовком `Authorization: Bearer zt_garbage` → `GET /api/v1/subjects` → `401` и `WWW-Authenticate: Bearer`.
- `RevokedTokenIsRejected` — после `DELETE /api/tokens/{id}` токен → `401`.
- `ExpiredTokenIsRejected` — `FakeTimeProvider` сдвинут ровно на `days` дней → `401`; на 1 мс раньше → `200`.
- `UnknownAndMalformedTokensAreRejected` — `Bearer` без значения, `Basic abc`, `Bearer abc` (без `zt_`) → `401`; `Basic abc` без `Bearer` на `/api/v1` → `401` (нет входа).
- `OwnersAreIsolated` — токен первого аккаунта не видит предметов второго; `OwnerId` в тексте запроса игнорируется.
- `RateLimitPerToken` — 120 запросов подряд → `200`, 121-й → `429` с `Retry-After`; другой токен того же аккаунта в тот же момент → `200`; сеанс по cookie не ограничивается этим лимитом.
- `LastUsedIsUpdatedAtMostEveryFiveMinutes` — первое использование ставит `LastUsedAt`; повтор через 4 минуты не меняет; через 5 минут меняет.
- `TokenAndQueryAreNotLogged` — после запросов с токеном и `?token=…` в записях журнала нет `zt_`, значения токена и query, нет слова `Authorization`.
- `RestoreKeepsToken` — `--backup`, `--restore` в пустую папку, новый хост: выпущенный до копии токен работает на `/api/v1/subjects` (`200`).

- [x] Шаг 1. Написать тесты; `--filter "ApiV1Tests|ApiTokenTests"` → красные.
- [x] Шаг 2. Реализовать схему `ApiToken`, `Smart`, группу `/api/v1`, политику `ApiScope`, лимитер, `prefix`, пропуск CSRF.
- [x] Шаг 3. Тот же запуск → зелёные. Если подводные камни из раздела «Решения» всплыли иначе, записать ruling.
- [x] Шаг 4. «Сломать и вернуть» (каждый пункт отдельно): убрать проверку `RevokedAt` → красный `RevokedTokenIsRejected`; заменить `<=` на `<` в проверке срока → красный `ExpiredTokenIsRejected`; убрать условие пути `/api/v1` → красный `TokenIsRejectedOutsideV1`; пропускать CSRF для всех → красный `CookieOnV1StillNeedsCsrf`; поменять области местами → красные `ReadScopeCannotWrite` и `WriteScopeCannotRead`; вернуть.
- [x] Шаг 5. Полный прогон → зелёные. Коммит `Вход по токену на /api/v1: области, лимит и CSRF только для cookie`, push.

---

### Задача 7: документация API

**Файлы:** создать `ApiTokens/ApiDocs.cs`, `wwwroot/api-docs.html`, `wwwroot/api-docs.js`, `tests/StudyTracker.Tests/OpenApiTests.cs`; изменить `StudyTracker.csproj` (пакет `Microsoft.AspNetCore.OpenApi` 10.0.14), `Program.cs`.

**Интерфейс.**
- Производит: `GET /api/v1/openapi.json` — OpenAPI-документ только из адресов группы `/api/v1` (`subjects`, `assignments`), со схемой безопасности `Bearer` (тип `http`, схема `bearer`); доступен без входа и без токена (`AllowAnonymous`). Документ не содержит `/api/auth`, `/api/notifications`, `/api/tokens`, `/api/token`, `/api/subjects` (без `v1`). Для адресов указаны коды ошибок `400`, `401`, `403`, `404`, `409`, `429` и формат `{"error": …}`.
- Способ построить документ (встроенный генератор по группе или добавленные описания) выбирается на первом тесте и записывается как ruling; нельзя ослаблять CSP.
- `GET /api/docs` отдаёт `wwwroot/api-docs.html` (редирект `/api/docs` → статическая страница или `UseDefaultFiles`-подобный маппинг; в журнал работы записать выбранное). Страница и `api-docs.js` не используют внешних скриптов, `eval` и inline-обработчиков (CSP `script-src 'self'`); `api-docs.js` читает `/api/v1/openapi.json` и показывает адреса, методы, параметры, области, пример `curl` с `Authorization: Bearer zt_…` и список кодов.

**Тесты** (`OpenApiTests`):
- `OpenApiIsPublicAndListsV1` — без входа `GET /api/v1/openapi.json` → `200`, JSON; есть пути `/api/v1/subjects`, `/api/v1/subjects/{id}`, `/api/v1/assignments`, `/api/v1/assignments/{id}`.
- `OpenApiHasBearerScheme` — `components.securitySchemes` содержит схему типа `http`/`bearer`.
- `OpenApiHidesInternalPaths` — нет путей, начинающихся с `/api/auth`, `/api/notifications`, `/api/tokens`, `/api/token`, и нет `/api/subjects`, `/api/assignments`.
- `DocsPageIsPublicAndStrict` — `GET /api/docs` без входа → `200`, текст содержит `api-docs.js`; в нём нет `http://`/`https://` `<script src>` и нет `onclick=`; заголовок `Content-Security-Policy` содержит `script-src 'self'`.
- `DocsScriptIsServed` — `GET /api-docs.js` → `200`, тип JavaScript.

- [x] Шаг 1. Написать тесты → красные.
- [x] Шаг 2. Добавить пакет, `ApiDocs.cs`, страницу и скрипт; подключить в `Program.cs`.
- [x] Шаг 3. `--filter OpenApiTests` → зелёные; `node --check wwwroot/api-docs.js`, `wwwroot/app.js`, `wwwroot/notifications.js` без ошибок.
- [x] Шаг 4. «Сломать и вернуть»: поместить `/api/tokens` в документ → красный `OpenApiHidesInternalPaths`; вернуть.
- [x] Шаг 5. Вручную в браузере открыть `/api/docs` (локальный запуск, `http://localhost:5184`): страница читается, на 320 и 1440 пикселей без горизонтальной прокрутки, консоль без ошибок CSP. Результат записать.
- [x] Шаг 6. Полный прогон → зелёные. Коммит `Документация API: openapi.json и страница /api/docs`, push.

---

### Задача 8: раздел «Доступ к API» в интерфейсе

**Файлы:** изменить `wwwroot/index.html`, `wwwroot/app.js`, `wwwroot/app.css`.

**Интерфейс.** Блок «Доступ к API» (рядом с «Напоминаниями в Telegram», по тому же образцу вёрстки и стилей): список токенов (название, начало токена `zt_xxxxxxxx…`, области, срок, последнее использование, состояние «действует/отозван/просрочен»), кнопка «Отозвать» (с подтверждением, как у других удалений), форма создания: название (до 60 символов), флажки «Чтение заданий» и «Запись заданий», срок в днях (по умолчанию 90, 1–365), кнопка «Создать». После создания показывается токен в поле только для чтения с кнопкой «Скопировать» и предупреждением «Токен показывается один раз». Ссылка «Документация API» на `/api/docs`. При выходе и смене аккаунта показанный токен и форма очищаются так же, как ссылка Telegram. Ошибки сервера (`400`, `409`) показываются текстом из `error`. Без `innerHTML` для данных пользователя, без inline-скриптов (CSP).

Автоматических тестов у интерфейса нет, проверка ручная.

- [x] Шаг 1. Реализовать разметку, стили и логику; `node --check wwwroot/app.js`.
- [x] Шаг 2. Собрать (`dotnet build --nologo` → 0 ошибок, 0 предупреждений), запустить `run --launch-profile StudyTracker --no-build`, открыть `http://localhost:5184`.
- [x] Шаг 3. Проверить в браузере: создание токена, показ один раз, копирование, повторное открытие страницы (токен не показывается), отзыв, ошибки (пустое название, 11-й токен), выход очищает показанный токен; ширины 320, 390, 1440; управление клавиатурой (Tab, Enter, Esc). Записать, что проверено и что нет. Остановить сервер по PID.
- [x] Шаг 4. Полный прогон тестов → зелёные. Коммит `Раздел «Доступ к API» в интерфейсе`, push.

---

### Задача 9: документы, итоговая проверка

**Файлы:** изменить `README.md`, `docs/запуск-на-сервере.md`, `docs/specs/эксплуатация-и-api.md` (статус), этот план (статус и раздел «Решения»); проверить `.github/workflows/check.yml`.

- [x] Шаг 1. `README.md`: версия 0.5, раздел «Что умеет» (токены, готовность, копии), структура проекта (новые файлы), таблица команд (`--backup`, `--restore`), число проверок и время прогона как есть из последнего запуска, строка о том, что Docker и контейнерные сценарии не запускались, «Что дальше» — 0.6 и 0.7.
- [x] Шаг 2. `docs/запуск-на-сервере.md`: в «Резервной копии» команды `docker compose run --rm migrate --backup /backup` с оговорками (том для папки, права записи) и пример `cron`; в «Восстановлении» — `--restore` в пустой том; проверка готовности `/health/ready` для внешнего мониторинга; про `X-Request-Id` и JSON-журнал.
- [x] Шаг 3. Итоговая проверка: `dotnet build --nologo` (0 ошибок, 0 предупреждений), `node --check` для трёх JS-файлов, полный `dotnet test tests/StudyTracker.Tests --nologo`; `docker compose config --quiet`.
- [x] Шаг 4. Статус спецификации и плана → «сделано», раздел «Решения» плана заполнить принятыми решениями (по ledger).
- [x] Шаг 5. Самопроверка всей работы по разделу «Готово, когда» спецификации; одна волна исправлений, каждое исправление сначала красным тестом. Коммит `Версия 0.5: документация и итоговая проверка`, push.

---

## Решения

Отклонения от плана, принятые решения и то, что не проверялось.

| Задача | Решение | Что будет, если ошибся |
| --- | --- | --- |
| 3 | Копия переводится в `journal_mode=DELETE`, чтобы рядом не оставались `-wal` и `-shm`: копия — один самодостаточный файл. | Копию нельзя было бы просто перенести одним файлом. |
| 3 | `BackupCommands.RunAsync` возвращает код, а `Environment.ExitCode` ставит `CommandLine`: иначе параллельные тесты портят код выхода процесса. | Тесты нестабильны. |
| 3 | `BackupAsync` повторяет снимок при `SQLITE_BUSY` и `SQLITE_LOCKED`: до 20 раз с паузой 0,5 с. Без этого полный прогон падал примерно раз в шесть с «database is locked». Тест `BackupWaitsWhileDatabaseIsBusy`; после правки 10 полных прогонов подряд зелёные. | При блокировке дольше 10 с `--backup` завершится кодом `1`. |
| 4 | `--restore` обрабатывается в `Program.cs` до подготовки базы: иначе подготовка создаёт `tracker.db` в пустой папке, и восстановление отказывает само себе. | Восстановление в пустую папку не работало бы. |
| 5 | Тест `DatabaseSetupTests.NotificationMigrationPreservesAccounts` дополнен `DROP TABLE ApiTokens`: он откатывает историю до уведомлений и заново применяет поздние миграции. | Старый тест падал бы на новой таблице. |
| 6 | Вне `/api/v1` отказ по токену отвечает «Токен действует только на адресах /api/v1.» вместо общего текста: иначе непонятно, почему верный токен не принят. | Другой текст в теле `401`, поведение то же. |
| 6 | Отказ обработчика токена: `Fail` и `NoResult` дают один итог (`401`). Защиту от отката на cookie даёт селектор схемы `Smart`, на него и «сломать и вернуть». | Проверка нужна именно на селектор, а не на обработчик. |
| 7 | OpenAPI строит встроенный `Microsoft.AspNetCore.OpenApi` 10.0.14: `AddOpenApi("v1")`, `ShouldInclude` по префиксу `api/v1/`, схему `Bearer` и коды ошибок добавляют трансформеры (`ApiTokens/ApiDocs.cs`). | Правка одного файла. |
| 7 | В документе только те коды ошибок, которые адрес действительно отдаёт (например, у `POST /assignments` нет `409`), а не все шесть везде. Тест `OpenApiDescribesErrors` сверяет точные наборы. | Иначе документация обещала бы то, чего нет. |
| 7 | `/api/docs` отдаёт `api-docs.html` через `MapGet` и `Results.File` без редиректа; добавлен `wwwroot/api-docs.css`, потому что CSP `style-src 'self'` запрещает встроенные стили. | Страница без стилей. |
| 8 | Логика раздела «Доступ к API» вынесена в `wwwroot/api-access.js` (по образцу `notifications.js`); в `app.js` остался общий диалог подтверждения (вид `token`, кнопка «Отозвать») и `tokens` в списке личных путей `api()`. | `app.js` распух бы. |

Не проверялось:

- Docker Desktop на машине разработки выключен: контейнерные сценарии и команды `--backup` и `--restore` в контейнере не запускались, команды в `docs/запуск-на-сервере.md` написаны по `compose.yaml` и `Dockerfile`.
- Результат GitHub Actions после загрузки не просматривался.
- Раздел «Доступ к API» проверен в Edge (Playwright) на 320, 390 и 1440 пикселей, страница `/api/docs` — на 500 и 1440. Экранные читалки, Firefox и Safari не проверялись.
- Старые временные папки `zachet-tests-*` в `%TEMP%` остались от прогонов тестов; массовое удаление автоматически не выполнялось.
