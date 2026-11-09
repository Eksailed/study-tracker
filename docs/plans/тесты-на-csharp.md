# Тесты на C# и разбор Program.cs: план реализации

Статус: в работе, план от 9 ноября 2026 года для версии 0.4. Спецификация одобрена, код ещё не менялся. Ничего не собиралось и не запускалось; все цифры (76 серверных и 7 контейнерных проверок) пересчитаны по `tests/test_*.py`.

Связано: [спецификация](../specs/тесты-на-csharp.md), [README](../../README.md), [план напоминаний](напоминания-в-telegram.md), [Program.cs](../../Program.cs), [CI](../../.github/workflows/check.yml).

> **Для исполнителей:** используйте `superpowers:subagent-driven-development` (рекомендуется) или `superpowers:executing-plans`. Шаги отмечаются флажками `- [ ]`.

**Цель.** Перенести 76 серверных Python-проверок на xUnit и разобрать `Program.cs` на небольшие части, не меняя поведения приложения.

**Подход.** Проект `tests/StudyTracker.Tests` ссылается на `StudyTracker.csproj`; HTTP-проверки идут через `WebApplicationFactory<Program>` (один хост и одна временная база на тест), время задаёт `FakeTimeProvider`, Telegram играет заглушка `HttpMessageHandler`, а таймаут и обрыв ответа проверяются на настоящем сокете. Рефакторинг идёт после переноса тестов.

**Технологии.** .NET 10 (SDK из `%USERPROFILE%\.dotnet`, см. ниже), xUnit, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Extensions.TimeProvider.Testing`, EF Core 10 + SQLite.

## Общие правила

Действуют во всех задачах.

- В командах `dotnet` — это SDK 10: `~/.dotnet/dotnet.exe` (`global.json` требует `10.0.100`, а `dotnet` из `PATH` на этой машине — 9.0.318).
- Тесты запускаются командой `dotnet test tests/StudyTracker.Tests`. Файл решения (`.slnx`) не создаётся: два проекта в корне ломают `dotnet build` и `Dockerfile` (`MSB1011`).
- Поведение приложения не меняется. Исключения — только точки подмены из спецификации: `public partial class Program;`, `TelegramHandlerFactory`, `SendTimeoutSeconds` = 10 и `PollTimeoutSeconds` = 40 в `TelegramOptions`, параметр источника пароля в `AccountCommands.CreateAsync`.
- Тесты переноса фиксируют уже работающее поведение, поэтому сначала проходят, а не падают. Чтобы убедиться, что они что-то проверяют, в каждой такой задаче есть шаг «сломать и вернуть»: временно портим одну строку рабочего кода, видим красный тест, возвращаем.
- Значения, статусы и тексты в новых тестах — те же, что в переносимом Python-тесте. Его исходник живёт в `git log` до удаления; удалять Python-тест можно только после зелёного C#-аналога.
- Именование: `test_task_lifecycle_and_subject_protection` → метод `TaskLifecycleAndSubjectProtection`.
- Ветка `тесты-на-csharp`. Коммиты локальные, push и PR — только по просьбе пользователя. В сообщениях коммитов — по-русски, без строк об авторстве Claude.
- Перед завершением каждой задачи: `dotnet test tests/StudyTracker.Tests` зелёный и `python -m unittest discover -s tests -v` (остаток Python) тоже зелёный.

## Что проверяем сверх переноса

Входы и сбои, которые легко ломают такой набор, хотя ни одна старая проверка их не покрывала.

| Ситуация | Что ожидаем | Где закреплено |
| --- | --- | --- |
| На Windows SQLite держит файл базы после остановки хоста | Временная папка удаляется без ошибки | Задача 1, `DisposeReleasesDataDirectory` |
| Ограничитель 10 запросов в минуту: у тестового хоста нет IP, все запросы получают один ключ | Входы в одном тесте не дают `429` чужим тестам; `429` приходит ровно на 11-й вход | Задача 3, `AuthRateLimit` |
| Настройки не доходят до `Program.cs` (он читает `Tracker:DataDirectory` сразу после `CreateBuilder`) | Файл базы лежит во временной папке, а не в `App_Data` | Задача 1, `FactoryUsesTemporaryDataDirectory` |
| Фоновые службы Telegram гоняются с ручными вызовами | В режиме `ManualMode` сообщение уходит только после явного вызова | Задача 6, `FixtureSendsNothingByItself` |
| Токен бота попадает в журнал или вывод теста | Токена нет ни в одной записи журнала | Задача 6, `TelegramTokenIsNotLogged` |

---

## Задача 1: каркас проекта тестов и фабрика хоста

**Файлы:**
- Создать: `tests/StudyTracker.Tests/StudyTracker.Tests.csproj`, `tests/StudyTracker.Tests/Support/TrackerFactory.cs`, `tests/StudyTracker.Tests/Support/ApiClient.cs`, `tests/StudyTracker.Tests/FactoryTests.cs`
- Изменить: `StudyTracker.csproj`, `Program.cs` (в конец), `.github/workflows/check.yml`

**Интерфейсы (нужны всем следующим задачам):**
- `TrackerFactory : WebApplicationFactory<Program>`, конструктор `TrackerFactory(IReadOnlyDictionary<string,string?>? settings = null, string environment = "Development", Action<IServiceCollection>? services = null)`. Свойства: `string DataDirectory`. Методы: `ApiClient NewClient()`, `TrackerFactory Restart()` (новый хост на той же папке и с теми же настройками; папку удаляет только исходная фабрика), `T Scope<T>(Func<IServiceProvider, T>)` для обращения к DI.
- `ApiClient(HttpClient http)`: `Task<ApiResponse> Send(HttpMethod method, string path, object? body = null, bool csrf = true)`, сокращения `Get`, `Post`, `Put`, `Delete` (путь без `/api/`), `Register(email, password = DefaultPassword)`, `Login(email, password = DefaultPassword)`, `Logout()`, `RefreshToken()`, свойство `string? Token`. `DefaultPassword = "a-long-test-password"`. `ApiResponse(int Status, JsonElement Body, HttpResponseHeaders Headers)`; при пустом теле `Body.ValueKind == Undefined`.
- Поведение `ApiClient` как у `Client` в `tests/support.py`: токен берётся из `GET /api/token` и отправляется в `X-CSRF-TOKEN`, после `Login` и `Logout` токен обновляется.

- [ ] **Шаг 1.** Создать ветку `тесты-на-csharp` и закоммитить спецификацию с планом: «Добавлены спецификация и план версии 0.4».
- [ ] **Шаг 2.** Создать проект тестов шаблоном `dotnet new xunit` (SDK 10), добавить пакеты Mvc.Testing и TimeProvider.Testing, ссылку на `../../StudyTracker.csproj`. Версии — последние стабильные; выбор xUnit v2 или v3 записать в раздел «Решения» этого документа.
- [ ] **Шаг 3.** Написать тесты в `FactoryTests.cs`:
  - `FactoryUsesTemporaryDataDirectory`: после `GET /api/token` файл `tracker.db` существует в `factory.DataDirectory`, а `App_Data/v02/tracker.db` в корне проекта не создан.
  - `DisposeReleasesDataDirectory`: после `await factory.DisposeAsync()` папка `DataDirectory` удаляется без исключения.
  - `RegisterAndLoginWork`: `Register` возвращает `201`, `Login` — `200`, `Get("auth/me")` возвращает `email`.
- [ ] **Шаг 4.** Запустить `dotnet test tests/StudyTracker.Tests`. Ожидаем: сборка падает (нет `Program` для фабрики, нет `TrackerFactory`).
- [ ] **Шаг 5.** В `StudyTracker.csproj` добавить `<Compile Remove="tests/**" />`, `<Content Remove="tests/**" />`, `<None Remove="tests/**" />`, `<EmbeddedResource Remove="tests/**" />` (Web SDK иначе подхватит `*.json` из `tests/**/bin` как `Content`). В конец `Program.cs` добавить `public partial class Program;`.
- [ ] **Шаг 6.** Реализовать `TrackerFactory` и `ApiClient` по интерфейсам выше. Миграцию временной базы делать отдельным `TrackerDb` до запуска хоста (`DatabaseSetup.PrepareAsync` без `--migrate` падает на неприменённых миграциях). Настройки передавать через `UseSetting`; если `FactoryUsesTemporaryDataDirectory` останется красным, перейти на переменные окружения под общей блокировкой. В `Dispose` вызывать `SqliteConnection.ClearAllPools()` перед удалением папки.
- [ ] **Шаг 7.** Запустить `dotnet test tests/StudyTracker.Tests`. Ожидаем: 3 теста проходят.
- [ ] **Шаг 8.** Проверить, что в корне по-прежнему собирается один проект: `dotnet build --nologo` и `dotnet publish -c Release -o <пустая папка вне репозитория>`; в результате публикации нет файлов из `tests/`.
- [ ] **Шаг 9.** В `check.yml` после «Проверить синтаксис JavaScript» добавить шаг «Тесты на C#» с командой `dotnet test tests/StudyTracker.Tests --no-restore`. Шаг Python пока не трогать.
- [ ] **Шаг 10.** Коммит: «Добавлен проект тестов на C# и фабрика тестового хоста».

---

## Задача 2: проверки API предметов и заданий

**Файлы:**
- Создать: `tests/StudyTracker.Tests/SubjectApiTests.cs`, `AssignmentApiTests.cs`, `SecurityTests.cs`
- Изменить: `tests/test_api.py` (удалить перенесённые методы)

**Интерфейсы:** использует `TrackerFactory`, `ApiClient` из задачи 1. В начале каждого теста: хост, `Register` и `Login` пользователя `api@example.test`.

Переносим 6 проверок из `test_api.py`:

| Класс | Методы (из Python) | Что должно получиться |
| --- | --- | --- |
| `AssignmentApiTests` | `task_lifecycle_and_subject_protection`, `validation_rejects_invalid_assignments`, `missing_records_return_404` | Те же коды ответов: `200`, `409`, `204`, `404`, `400`; 8 вариантов невалидного ввода оформить как `[Theory]` |
| `SubjectApiTests` | `subject_validation_and_cyrillic_duplicates` | `409` для « программирование » при существующем «Программирование»; `400` для пустого, 81 символа, цвета `red` и `null` |
| `SecurityTests` | `csrf_required_for_all_mutations`, `static_page_and_security_headers` | `400` без токена; `/` отдаёт «Зачёт», `nosniff`, `frame-ancestors 'none'` в CSP, `/app.js` — `200`, `/api/subjects` — `Cache-Control: no-store` |

- [ ] **Шаг 1.** Написать три класса тестов.
- [ ] **Шаг 2.** Запустить `dotnet test tests/StudyTracker.Tests --filter "FullyQualifiedName~ApiTests|FullyQualifiedName~SecurityTests"`. Ожидаем: все проходят.
- [ ] **Шаг 3.** Сломать и вернуть: в `Program.cs` заменить `409` в `Results.Conflict` у предметов на `Results.BadRequest`. Ожидаем красный `TaskLifecycleAndSubjectProtection`; вернуть строку.
- [ ] **Шаг 4.** Удалить из `tests/test_api.py` шесть методов; `python -m unittest discover -s tests -v` остаётся зелёным.
- [ ] **Шаг 5.** Коммит: «Проверки API предметов и заданий перенесены на C#».

---

## Задача 3: аккаунты и команда `--create-account`

**Файлы:**
- Создать: `tests/StudyTracker.Tests/AuthTests.cs`, `AccountCommandsTests.cs`
- Изменить: `AccountCommands.cs`, `tests/test_auth.py` (удалить файл целиком, когда он опустеет)

**Интерфейсы:**
- Производит: `AccountCommands.CreateAsync(IServiceProvider services, string email, Func<string?>? readPassword = null) : Task`. При `null` поведение прежнее (консоль или перенаправленный ввод); иначе пароль берётся из функции.
- Использует: `TrackerFactory(settings, environment)`, `ApiClient`.

`AuthTests` — 10 проверок: `registration_and_login` (cookie `Zachet.Account`, `HttpOnly`, `SameSite=Lax` в заголовке `Set-Cookie`; хэш пароля в `AspNetUsers` не равен паролю и длиннее 50), `logout_ends_session`, `short_password_is_rejected` (11 и 129 символов — `400`, 12 — `201`), `login_is_locked_after_five_failures` (после 5 ошибок верный пароль даёт `401`, `LockoutEnd` заполнен), `auth_requires_csrf`, `registration_can_be_disabled` (`Accounts:AllowRegistration=false`: `allowRegistration` = `false`, регистрация — `403`), `auth_rate_limit` (10 входов — `401`, 11-й — `429`), `production_registration_is_closed_by_default` (`environment: "Production"`, `403`), `production_cookie_requires_https` (cookie `Secure`, `auth/me` по http — `401`), `login_rotates_csrf_identity`.

`AccountCommandsTests` — 2 проверки: создание аккаунта при закрытой регистрации (вход работает, `allowRegistration` остаётся `false`; повторный вызов с другим паролем бросает `InvalidOperationException`, прежний пароль продолжает работать) и короткий пароль (`InvalidOperationException`, вход невозможен — `401`).

- [ ] **Шаг 1.** Написать тесты `AccountCommandsTests`, вызвав `AccountCommands.CreateAsync(factory.Services, email, () => password)`. Запустить: сборка падает (нет параметра).
- [ ] **Шаг 2.** Добавить параметр `readPassword` в `AccountCommands.CreateAsync` и использовать его вместо консольного блока, когда он задан.
- [ ] **Шаг 3.** Написать `AuthTests`. Каждый тест — отдельный хост. Запустить `dotnet test tests/StudyTracker.Tests --filter "FullyQualifiedName~AuthTests|FullyQualifiedName~AccountCommandsTests"`. Ожидаем: 12 тестов проходят.
- [ ] **Шаг 4.** Сломать и вернуть: в `AuthEndpoints.cs` поставить `PermitLimit = 11`. Ожидаем красный `AuthRateLimit`; вернуть.
- [ ] **Шаг 5.** Удалить `tests/test_auth.py` и убедиться, что Python-набор зелёный.
- [ ] **Шаг 6.** Коммит: «Проверки аккаунтов перенесены на C#, пароль команды создания аккаунта передаётся параметром».

---

## Задача 4: владельцы, перезапуск и перенос старой базы

**Файлы:**
- Создать: `tests/StudyTracker.Tests/OwnershipTests.cs`, `PersistenceTests.cs`, `LegacyImportTests.cs`
- Изменить: `tests/test_ownership.py` (удалить файл), `tests/test_api.py` (метод `data_survives_restart`)

**Интерфейсы:** использует `TrackerFactory.Restart()`, `ApiClient`, `TrackerFactory.Scope`. `LegacyImport.RunAsync(IServiceProvider, string path, string email)` вызывается напрямую.

| Класс | Методы (из Python) | Что должно получиться |
| --- | --- | --- |
| `OwnershipTests` (7) | `anonymous_cannot_access_assignments`, `users_see_only_own_records`, `foreign_assignment_cannot_be_changed_or_deleted`, `foreign_subject_cannot_be_used`, `same_subject_name_for_different_users`, `cross_account_csrf_is_rejected`, `owner_id_from_body_is_ignored` | `401` для анонима (6 вариантов — `[Theory]`), `404` для чужих записей; в `foreign_subject_cannot_be_used` проверка внешнего ключа идёт через `UPDATE` в SQLite с `PRAGMA foreign_keys=ON` и ожидает `SqliteException`; `X-Account-Id` совпадает с `ownerId` |
| `PersistenceTests` (2) | `data_survives_restart` (из `test_api.py`), `two_users_survive_restart` | После `Restart()` задания на месте; статус `InProgress` и описание сохранены; у двух пользователей свои списки |
| `LegacyImportTests` (3) | `legacy_import_preserves_records`, `legacy_import_rolls_back_invalid_data`, `legacy_import_rejects_unknown_owner` | Старая база создаётся файлом SQLite в `Path.GetTempPath()`; её хэш SHA-256 не меняется; повторный импорт бросает `InvalidOperationException`; при `Status = 99` ничего не переносится |

- [ ] **Шаг 1.** Написать три класса тестов. Старая база для импорта создаётся скриптом из `legacy_database` в `test_ownership.py` через `Microsoft.Data.Sqlite`.
- [ ] **Шаг 2.** Запустить `dotnet test tests/StudyTracker.Tests --filter "FullyQualifiedName~OwnershipTests|FullyQualifiedName~PersistenceTests|FullyQualifiedName~LegacyImportTests"`. Ожидаем: 12 тестов проходят.
- [ ] **Шаг 3.** Сломать и вернуть: в `Program.cs` убрать условие `a.OwnerId == owner` в `MapPut` заданий. Ожидаем красные `ForeignAssignmentCannotBeChangedOrDeleted` и `ForeignSubjectCannotBeUsed`; вернуть.
- [ ] **Шаг 4.** Удалить `tests/test_ownership.py` и метод `data_survives_restart`; Python-набор зелёный.
- [ ] **Шаг 5.** Коммит: «Проверки владельцев, перезапуска и переноса старой базы перенесены на C#».

---

## Задача 5: миграции и защита старых баз

**Файлы:**
- Создать: `tests/StudyTracker.Tests/DatabaseSetupTests.cs`
- Изменить: `tests/test_api.py` (удалить четыре метода миграций), `tests/test_notifications.py` (метод `notification_migration_preserves_accounts`)

**Интерфейсы:** без фабрики хоста. Тест собирает `ServiceCollection` с `TrackerDb` на файле SQLite и вызывает `DatabaseSetup.PrepareAsync(IServiceProvider services, bool migrate)`; «код возврата 1» из Python — это `InvalidOperationException`.

Пять проверок: `MigrationsCreateEmptyDatabase` (таблицы `Subjects` и `Assignments` пусты, в `__EFMigrationsHistory` больше нуля строк), `MigrationsAreRepeatable` (запись предмета остаётся после повторной миграции), `LegacyDatabaseIsNotModified`, `SharedMigratedDatabaseIsNotModified` (в обоих случаях исключение и тот же SHA-256 файла), `NotificationMigrationPreservesAccounts` (удаляем 5 таблиц уведомлений и записи истории с миграции `_Notifications` включительно, затем `migrate: true`; пользователь и задание на месте). Таблицы и данные готовятся SQL-скриптами из Python-тестов.

- [ ] **Шаг 1.** Написать `DatabaseSetupTests`. Запустить `dotnet test tests/StudyTracker.Tests --filter "FullyQualifiedName~DatabaseSetupTests"`. Ожидаем: 5 тестов проходят.
- [ ] **Шаг 2.** Сломать и вернуть: в `DatabaseSetup.cs` убрать `throw` для базы без истории миграций. Ожидаем красный `LegacyDatabaseIsNotModified`; вернуть.
- [ ] **Шаг 3.** Удалить перенесённые Python-методы; Python-набор зелёный.
- [ ] **Шаг 4.** Коммит: «Проверки миграций перенесены на C#».

---

## Задача 6: подмена Telegram, точки подмены и привязка чата

**Файлы:**
- Создать: `tests/StudyTracker.Tests/Support/FakeTelegram.cs`, `Support/TelegramFixture.cs`, `TelegramBindingTests.cs`
- Изменить: `Notifications/TelegramClient.cs`, `Notifications/TelegramOptions.cs`, `Notifications/NotificationServices.cs`, `tests/test_telegram.py` (удалить файл)

**Интерфейсы:**
- Производит в рабочем коде: `public delegate HttpMessageHandler TelegramHandlerFactory();` (в `TelegramClient.cs`); `TelegramClient(TelegramOptions options, IWebHostEnvironment environment, TelegramHandlerFactory createHandler)`; в `NotificationServices.AddNotifications` регистрация по умолчанию `() => new SocketsHttpHandler { AllowAutoRedirect = false }`. `TelegramOptions.SendTimeoutSeconds` (10) и `PollTimeoutSeconds` (40) — `int` с `init`; `HttpClient.Timeout` берётся из них.
- Производит в тестах: `FakeTelegram : HttpMessageHandler` — `List<JsonElement> Messages` (тела `sendMessage`), `List<string> Calls`, `Queue<FakeReply> Responses`, `string Webhook`, `void AddUpdate(string code, long chat = 10000000001, string kind = "private", long? updateId = null)`. `FakeReply.Error(int code, int? retryAfter = null)` и `FakeReply.NetworkFailure` (бросает `HttpRequestException`). Методы `getMe` (id `123456`, имя `zachet_test_bot`), `getWebhookInfo`, `getUpdates` (обновления с `update_id >= offset`), `sendMessage` (ответ с `message_id` = номер сообщения), остальные — `404`. Токен `FakeTelegram.Token = "123456:fake-token-for-tests"`.
- `TelegramFixture : IAsyncLifetime`: свойства `TrackerFactory App`, `FakeTelegram Fake`, `FakeTimeProvider Clock` (старт `2026-11-11T15:00:00Z`), `ApiClient Client` (аккаунт `telegram@example.test`, уже вошёл). Методы: `Task<string> CodeAsync(ApiClient? client = null)`, `Task PollAsync()` (`TelegramUpdateWorker.PollAsync(token, timeout: 0)`), `Task BindAsync()` (код → обновление → опрос, очищает `Fake.Messages`), `Task SettingsAsync(Func<JsonObject, JsonObject>? change = null)` (PUT с `enabled=true`, `Europe/Moscow`, `19:00`/`09:00`), `Task<JsonElement> TaskAsync()` (предмет «Предмет», задание «Название задания», описание «Не передавать описание», срок `2026-11-12`), `Task<List<ReminderDelivery>> RowsAsync()`, `Task PlanAsync()`, `Task TickAsync()` (`RecoverAsync`, `PlanAsync`, `SendPendingAsync` по порядку), `Task AdvanceAsync(string utc)` (`Clock.SetUtcNow`). Хост: `Telegram:BotToken` = токен подмены, `Telegram:ManualMode=true`, `TimeProvider` и `TelegramHandlerFactory` заменены через `services`. После старта фикстура вызывает `TelegramClient.InitializeAsync`.

- [ ] **Шаг 1.** Написать тесты `FixtureTests` в `TelegramBindingTests.cs` (или рядом): `FixtureSendsNothingByItself` — привязать чат, запросить `notifications/test`, подождать 1500 мс реального времени, `Fake.Messages` пуст; `TelegramTokenIsNotLogged` — подключить провайдер журнала, собирающий записи, пройти привязку и опрос; ни одна запись (включая исключения) не содержит `FakeTelegram.Token`. Запустить: сборка падает.
- [ ] **Шаг 2.** Реализовать точки подмены в `TelegramClient`, `TelegramOptions`, `NotificationServices`. Обычный запуск не должен меняться: оба клиента по умолчанию получают такой же `SocketsHttpHandler`, таймауты 10 и 40 секунд.
- [ ] **Шаг 3.** Реализовать `FakeTelegram` и `TelegramFixture`.
- [ ] **Шаг 4.** Написать `TelegramBindingTests` (11 проверок из `test_telegram.py`): `binding_code_is_single_use`, `binding_code_expires_after_ten_minutes` (на `15:10:00Z` код не работает; в базе хранится не код, а его хэш), `code_works_at_nine_minutes_fifty_nine_seconds` (`15:09:59Z`), `new_code_invalidates_old_code`, `group_chat_cannot_bind` (и в чат ничего не отправлено), `chat_bound_to_another_user_is_not_reassigned`, `duplicate_update_is_ignored` (в `Fake.Messages` одно сообщение), `disconnect_clears_pending_code`, `code_issue_limit` (6-й запрос — `429`), `telegram_token_is_not_logged` (общий с шагом 1, не дублировать), `existing_webhook_is_not_removed` (после `InitializeAsync` с `Fake.Webhook` статус бота содержит `webhook`, `notifications/code` — `503`, `deleteWebhook` нет в `Fake.Calls`).
- [ ] **Шаг 5.** Запустить `dotnet test tests/StudyTracker.Tests --filter "FullyQualifiedName~Telegram|FullyQualifiedName~Fixture"`. Ожидаем: все проходят.
- [ ] **Шаг 6.** Сломать и вернуть: в `TelegramUpdateWorker.BindAsync` убрать проверку `type.GetString() != "private"`. Ожидаем красный `GroupChatCannotBind`; вернуть.
- [ ] **Шаг 7.** Удалить `tests/test_telegram.py`; Python-набор зелёный.
- [ ] **Шаг 8.** Проверить, что обычный запуск не изменился: `dotnet build --nologo`, затем `dotnet run -- --migrate` во временной папке (`Tracker__DataDirectory`) завершается без ошибок.
- [ ] **Шаг 9.** Коммит: «Добавлены точки подмены Telegram и проверки привязки чата на C#».

---

## Задача 7: расписание напоминаний

**Файлы:**
- Создать: `tests/StudyTracker.Tests/ReminderPlannerTests.cs`
- Изменить: `tests/test_planner.py` (удалить файл)

**Интерфейсы:** наследует `TelegramFixture` из задачи 6 (каждый тест: `BindAsync`, `SettingsAsync`, `TaskAsync`). `ReminderPlanner.PlanAsync` вызывается через `PlanAsync()` фикстуры. Состояния проверяются через `DeliveryState`.

Семь проверок: `MoscowReminderTimes` (на `2026-11-11T16:00:00Z` — запись `Before`, `ScheduledAt` = `2026-11-11T16:00:00Z`; на `2026-11-12T06:00:00Z` — запись `Due` с этим временем), `RestartDoesNotDuplicateReminder` (после второго `PlanAsync` на `16:01:00Z` одна запись), `CompletedAssignmentIsNotNotified` (статус `Done` — записей нет), `ChangedDueDateCancelsOldPendingReminder` (`Cancelled`), `DisconnectedChatCancelsPendingReminders` (`DELETE notifications/connection` → `Cancelled`), `MissedWindowIsSkipped` (`16:14:59Z` — `Pending`, `16:15:00Z` — `Skipped`), `ChangedTimeReplansUnsentReminder` (время `19:05`: старая запись `Cancelled`, на `16:05:00Z` новая `Pending`).

- [ ] **Шаг 1.** Написать тесты. Запустить `dotnet test tests/StudyTracker.Tests --filter "FullyQualifiedName~ReminderPlannerTests"`. Ожидаем: 7 тестов проходят.
- [ ] **Шаг 2.** Сломать и вернуть: в `ReminderPlanner.cs` заменить `900000` на `900001`. Ожидаем красный `MissedWindowIsSkipped`; вернуть.
- [ ] **Шаг 3.** Удалить `tests/test_planner.py`; Python-набор зелёный.
- [ ] **Шаг 4.** Коммит: «Проверки расписания напоминаний перенесены на C#».

---

## Задача 8: отправка напоминаний и обработка ошибок

**Файлы:**
- Создать: `tests/StudyTracker.Tests/ReminderSenderTests.cs`
- Изменить: `tests/test_sender.py` (только `test_timeout…` и `test_truncated…` остаются до задачи 10), `tests/test_notifications.py` (метод `restart_without_token_marks_inflight_unknown`)

**Интерфейсы:** наследует `TelegramFixture`; каждый тест начинается с `BindAsync`, `SettingsAsync`, `TaskAsync`, `AdvanceAsync("2026-11-11T16:00:00Z")`. Отправка — `TickAsync()`.

Десять проверок (значения из Python-тестов): `SuccessfulSendRecordsMessageId` (`Sent`, `MessageId` задан, второй `TickAsync` не шлёт), `Respects429RetryAfter` (`retry_after` 5: `Pending`, затем на `16:00:05Z` — `Sent`, всего 2 сообщения), `RetryLimitIsThree` (4 ответа `429` с `retry_after` 1 → `Failed`, 4 обращения), `Forbidden403DisablesDelivery` (`enabled` = `false`, `connectionProblem` не `null`, `Failed`), `NetworkFailureIsUnknownWithoutAutomaticRetry` (`FakeReply.NetworkFailure` → `Unknown`; через 5 секунд повтора нет), `Server500IsUnknownWithoutAutomaticRetry`, `RestartMarksInflightSendUnknown` (запись `Sending` + `RecoverAsync` → `Unknown`, сообщений нет), `ReminderContainsOnlyAllowedFields` (название, предмет, «12 ноября 2026»; нет описания, почты и `parse_mode`), `Bad400IsFailedWithoutRetry`, `RestartWithoutTokenMarksInflightUnknown` (хост без токена: запись `Sending` вставляется в базу, `Restart()`, `lastDelivery.state` = `Unknown`).

- [ ] **Шаг 1.** Написать тесты. Запустить `dotnet test tests/StudyTracker.Tests --filter "FullyQualifiedName~ReminderSenderTests"`. Ожидаем: 10 тестов проходят.
- [ ] **Шаг 2.** Сломать и вернуть: в `ReminderSender.cs` заменить `row.Attempts <= 3` на `row.Attempts <= 4`. Ожидаем красный `RetryLimitIsThree`; вернуть.
- [ ] **Шаг 3.** Удалить перенесённые Python-методы; Python-набор зелёный.
- [ ] **Шаг 4.** Коммит: «Проверки отправки и ошибок доставки перенесены на C#».

---

## Задача 9: настройки и действия уведомлений

**Файлы:**
- Создать: `tests/StudyTracker.Tests/NotificationSettingsTests.cs`, `NotificationActionsTests.cs`
- Изменить: `tests/test_notifications.py`, `tests/test_notification_actions.py` (удалить оба файла)

**Интерфейсы:** `NotificationSettingsTests` — на обычном `TrackerFactory` без токена; `NotificationActionsTests` — на `TelegramFixture`.

`NotificationSettingsTests` (5): `NotificationDefaults` (`enabled` = `false`, `configured` = `false`, `Europe/Moscow`, `19:00` и `09:00`), `SettingsArePrivate` (чужие настройки не видны), `NotificationSettingsRequireCsrf` (`400` без токена, `401` без входа), `InvalidTimezoneIsRejected` (`Unknown/Zone` и время `25:00` — `400`), `DuplicateReminderIsRejected` (повторная вставка в `ReminderDeliveries` даёт `SqliteException` из-за уникального индекса).

`NotificationActionsTests` (6): `TestMessageIsRateLimited` (`202`, `429`, на `15:01:00Z` снова `202`), `TestMessageRequiresExplicitRequest` (без запроса сообщений нет; после `notifications/test` и `TickAsync` одно сообщение, `Kind` = `Test`, `enabled` = `false`, `lastDelivery.state` = `Sent`), `DisconnectCancelsTestMessage`, `TestMessageRequiresCsrf`, `EnablingRequiresConnection` (`409`), `OtherAccountCannotUseConnection` (`409` и `lastDelivery` = `null`).

- [ ] **Шаг 1.** Написать тесты. Запустить `dotnet test tests/StudyTracker.Tests --filter "FullyQualifiedName~NotificationSettingsTests|FullyQualifiedName~NotificationActionsTests"`. Ожидаем: 11 тестов проходят.
- [ ] **Шаг 2.** Сломать и вернуть: в `TelegramEndpoints.cs` заменить `last + 60000` на `last + 30000`. Ожидаем красный `TestMessageIsRateLimited`; вернуть.
- [ ] **Шаг 3.** Удалить оба Python-файла; Python-набор зелёный.
- [ ] **Шаг 4.** Коммит: «Проверки настроек и действий уведомлений перенесены на C#».

---

## Задача 10: настоящий сокет для таймаута и обрыва ответа

**Файлы:**
- Создать: `tests/StudyTracker.Tests/Support/SocketTelegram.cs`, `TelegramSocketTests.cs`
- Изменить: `tests/test_sender.py` (удалить файл), `tests/telegram_support.py` (удалить), `tests/test_deployment.py` не трогать

**Интерфейсы:** `SocketTelegram : IAsyncDisposable` — маленькое Kestrel-приложение на `http://127.0.0.1:0` с методами `getMe`, `getWebhookInfo`, `sendMessage`; `string BaseUrl`; `int SendCount`; режим `Mode { Hang, Truncated }` (`Hang` ждёт до обрыва запроса клиентом; `Truncated` отдаёт заголовки с `Content-Length: 100` и тело `{"ok":`, затем закрывает соединение). Хост тестируется в `Development` с `Telegram:ApiBase` = `BaseUrl`, `Telegram:SendTimeoutSeconds` = `1`, без `TelegramHandlerFactory` (настоящий `SocketsHttpHandler`).

Две проверки: `TimeoutIsUnknownWithoutAutomaticRetry` и `TruncatedResponseIsUnknownWithoutAutomaticRetry`. В обоих: запись переходит в `Unknown`, повторный `TickAsync` не создаёт второго обращения (`SendCount` = 1). Тест на таймаут не должен идти дольше нескольких секунд.

- [ ] **Шаг 1.** Написать тесты и подмену. Привязку чата сделать прямой записью в базу (чат `10000000001`, бот `123456`), без запуска опроса.
- [ ] **Шаг 2.** Запустить `dotnet test tests/StudyTracker.Tests --filter "FullyQualifiedName~TelegramSocketTests"`. Ожидаем: 2 теста проходят, каждый быстрее 5 секунд.
- [ ] **Шаг 3.** Сломать и вернуть: в `TelegramClient` не читать `SendTimeoutSeconds` (оставить 10 секунд). Ожидаем красный или зависший `TimeoutIsUnknownWithoutAutomaticRetry` (проверить с общим таймаутом теста 8 секунд); вернуть.
- [ ] **Шаг 4.** Удалить `tests/test_sender.py` и `tests/telegram_support.py`. Убедиться, что в `tests/` остались только `test_deployment.py`, `support.py`, `fake_telegram.py`.
- [ ] **Шаг 5.** Коммит: «Проверки таймаута и обрыва ответа Telegram перенесены на настоящий сокет».

---

## Задача 11: эндпоинты и проверка заданий отдельно

**Файлы:**
- Создать: `Endpoints/SubjectEndpoints.cs`, `Endpoints/AssignmentEndpoints.cs`, `Endpoints/AssignmentValidator.cs`
- Изменить: `Program.cs`, `LegacyImport.cs`

**Интерфейсы (производит):**
- `public static class SubjectEndpoints { public static readonly string[] Colors; public static void MapSubjects(this RouteGroupBuilder group); }`
- `public static class AssignmentEndpoints { public static void MapAssignments(this RouteGroupBuilder group); }`
- `public static class AssignmentValidator { public static string? Validate(AssignmentInput input); }` (тексты ошибок и границы — как сейчас в `ValidateAssignment`).
- В `Program.cs` остаётся `var data = app.MapGroup("/api").RequireAuthorization(); data.MapSubjects(); data.MapAssignments();`. `LegacyImport` берёт цвета из `SubjectEndpoints.Colors` вместо своего массива.

- [ ] **Шаг 1.** Убедиться, что набор зелёный: `dotnet test tests/StudyTracker.Tests`.
- [ ] **Шаг 2.** Вынести предметы, задания и проверку в новые файлы; тела обработчиков не менять.
- [ ] **Шаг 3.** Запустить `dotnet test tests/StudyTracker.Tests`. Ожидаем: все тесты, как и до выноса, проходят.
- [ ] **Шаг 4.** Коммит: «Эндпоинты предметов и заданий вынесены из Program.cs».

---

## Задача 12: командная строка и защитные заголовки отдельно

**Файлы:**
- Создать: `CommandLine.cs`, `SecurityMiddleware.cs`
- Изменить: `Program.cs`

**Интерфейсы (производит):**
- `public static class CommandLine { public static readonly string[] Flags; public static Task<bool> TryRunAsync(string[] args, WebApplication app); }` — выполняет `--migrate`, `--notifications-tick`, `--notifications-plan`, `--telegram-poll`, `--create-account`, `--import-legacy` и возвращает `true`, если команда выполнена (тогда `Program.cs` завершается). Коды возврата и тексты ошибок прежние; `Flags` заменяет массив `commandFlags`.
- `public static class SecurityMiddleware { public static IApplicationBuilder UseAccountHeader(this IApplicationBuilder app); public static IApplicationBuilder UseTrackerSecurity(this IApplicationBuilder app); }` — прежний `X-Account-Id` и прежний блок CSP, `nosniff`, `no-store`, CSRF.
- Порядок в `Program.cs` не меняется: `UseForwardedHeaders` → `UseHsts` → `UseAuthentication` → `UseAccountHeader` → `UseRateLimiter` → `UseAuthorization` → `UseTrackerSecurity` → `UseDefaultFiles` → `UseStaticFiles`.

- [ ] **Шаг 1.** Вынести команды в `CommandLine`, middleware — в `SecurityMiddleware`.
- [ ] **Шаг 2.** Запустить `dotnet test tests/StudyTracker.Tests`. Ожидаем: все проходят.
- [ ] **Шаг 3.** Проверить команды вручную во временной папке (`Tracker__DataDirectory`): `dotnet run --no-launch-profile -- --migrate` — «Миграции применены. База готова.»; `--import-legacy` без значения — код возврата `1` и сообщение «Укажите значение --import-legacy.».
- [ ] **Шаг 4.** Коммит: «Командная строка и защитные заголовки вынесены из Program.cs».

---

## Задача 13: разбор `ReminderSender.SendPendingAsync`

**Файлы:**
- Изменить: `Notifications/ReminderSender.cs`

**Интерфейсы (производит, приватные):** `Task<string> BuildTextAsync(TrackerDb db, ReminderDelivery row, Assignment? task, CancellationToken token)` — текст напоминания с ссылкой из `PublicUrl` только для внешнего HTTPS; `Task ApplyResultAsync(TrackerDb db, ReminderDelivery row, TelegramConnection connection, NotificationSettings settings, long targetChat, TelegramResult result)` — разбор ответа Telegram (`Sent`, `429`, `403`, `400/401/404`, остальное — `Unknown`). Порядок транзакций, сохранений и перезагрузок записей не меняется.

- [ ] **Шаг 1.** Убедиться, что `ReminderSenderTests` и `NotificationActionsTests` зелёные.
- [ ] **Шаг 2.** Выделить два метода; публичные сигнатуры не менять.
- [ ] **Шаг 3.** Запустить `dotnet test tests/StudyTracker.Tests`. Ожидаем: все проходят.
- [ ] **Шаг 4.** Коммит: «ReminderSender разбит на методы без изменения поведения».

---

## Задача 14: CI, документация и итоговая проверка

**Файлы:**
- Изменить: `.github/workflows/check.yml`, `README.md`, `docs/specs/тесты-на-csharp.md`, этот документ
- Проверить: `Dockerfile`, `.dockerignore`

- [ ] **Шаг 1.** В `check.yml` убрать шаг `python -m unittest discover -s tests -v` (в `tests/` остался только `test_deployment.py`, он запускается отдельным заданием). Шаг `dotnet test` оставить.
- [ ] **Шаг 2.** Сверить покрытие: количество методов `[Fact]` плюс вариантов `[Theory]` в `tests/StudyTracker.Tests` не меньше 76 (посчитать `dotnet test --list-tests`); записать число в `README`. Ни одно из 76 имён Python-тестов (список в `git log` до задачи 2) не должно остаться без C#-аналога; отброшенные проверки с причиной перечислить в этом документе.
- [ ] **Шаг 3.** Замерить время: `dotnet test tests/StudyTracker.Tests` на этой машине. Записать фактические секунды в `README` как есть. Для сравнения взять `python -m unittest discover -s tests -v` на коммите до задачи 2.
- [ ] **Шаг 4.** Обновить `README`: статус «версия 0.4», раздел «Проверка» (команда `dotnet test tests/StudyTracker.Tests`, Python только для контейнеров), таблицу технологий (проверки: xUnit, Python для контейнерных сценариев), схему «Как устроено» (`tests/StudyTracker.Tests/`, `Endpoints/`, `CommandLine.cs`, `SecurityMiddleware.cs`), абзац про набор тестов. Прямо написать, что собрано и проверено, а что нет.
- [ ] **Шаг 5.** Проверить контейнеры. Если Docker есть: `docker compose config --quiet`, `docker build -t study-tracker:local .`, `python tests/test_deployment.py`; ожидаем 7 проходящих проверок. Если Docker недоступен — так и написать в `README` и в ответе, опереться на контейнерное задание GitHub Actions.
- [ ] **Шаг 6.** Обновить статус спецификации и этого плана («сделано» с датой и тем, что проверено); в разделе «Решения» этого документа записать версию xUnit.
- [ ] **Шаг 7.** Запустить итог: `dotnet build --nologo`, `node --check wwwroot/app.js`, `node --check wwwroot/notifications.js`, `dotnet test tests/StudyTracker.Tests`. Ожидаем: сборка без ошибок, все тесты зелёные.
- [ ] **Шаг 8.** Коммит: «Документация и CI версии 0.4: тесты на C#».

## Решения

Заполняется при реализации: версия xUnit (v2 или v3), способ передачи настроек в `Program.cs` (`UseSetting` или переменные окружения), отброшенные проверки с причинами, замеренное время.
