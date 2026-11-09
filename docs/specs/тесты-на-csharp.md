# Тесты на C# и разбор Program.cs

Статус: сделано, версия 0.4 от 9 ноября 2026 года. Все 76 серверных проверок перенесены на C#, в наборе 94 проверки и они проходят (`dotnet test tests/StudyTracker.Tests`); `Program.cs` разобран без изменения поведения. Контейнерные сценарии в этой работе заново не запускались: Docker Desktop был выключен.

Связано: [README](../../README.md), [план напоминаний](../plans/напоминания-в-telegram.md), [Program.cs](../../Program.cs), [клиент Telegram](../../Notifications/TelegramClient.cs), [существующие проверки](../../tests/), [CI](../../.github/workflows/check.yml).

## Идея

Сейчас все 76 серверных проверок написаны на Python: тест запускает собранный `StudyTracker.dll` отдельным процессом, ходит к нему через `urllib` и читает SQLite через `sqlite3`. Время двигается перезапуском сервера с `Testing__UtcNow`, Telegram играет `tests/fake_telegram.py`. На C# нет ни одного теста.

В версии 0.4 переносим эти проверки на xUnit и `WebApplicationFactory`, чтобы `dotnet test` проверял весь проект одним языком и быстро. Потом, уже под защитой тестов, разбираем `Program.cs` на небольшие части. Поведение приложения не меняется.

## Что не входит

- Новые функции, новые поля и миграции базы.
- Слой сервисов между эндпоинтами и `TrackerDb`, разбиение на проекты `Core` и `Web`.
- Перенос `tests/test_deployment.py`: проверки Docker и HTTPS остаются на Python.
- Подключение настоящего бота: все проверки идут на подменах.

## Решения по умолчанию

| Вопрос | Решение |
| --- | --- |
| Расположение | Проект `tests/StudyTracker.Tests/StudyTracker.Tests.csproj` рядом с Python-проверками, ссылка на `../../StudyTracker.csproj` |
| Файл решения | Не создаём. Корень остаётся с одним `StudyTracker.csproj`, поэтому `dotnet build`, `dotnet run` и `Dockerfile` не ломаются от двух проектов в папке (ошибка `MSB1011`). Проверки запускаются командой `dotnet test tests/StudyTracker.Tests` |
| Библиотеки | xUnit, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Extensions.TimeProvider.Testing`; версии — последние стабильные для .NET 10 на момент реализации |
| База в тестах | Временная папка с файлом SQLite на каждый тестовый класс; миграции применяются до запуска хоста |
| Время | `FakeTimeProvider` из DI вместо `NotificationClock`; настройка `Testing:UtcNow` остаётся только для контейнерных проверок |
| Telegram | Заглушка `HttpMessageHandler` для основной массы; два теста с настоящим сокетом (таймаут и обрыв тела ответа) |
| Таймауты Telegram | `SendTimeoutSeconds` = 10 и `PollTimeoutSeconds` = 40 в `TelegramOptions`; в тестах с сокетом `SendTimeoutSeconds` = 1 |
| Порядок | Сначала тесты и точки подмены, потом разбор `Program.cs` |

## Что меняется в рабочем коде

Только то, без чего не обойтись тестам. Поведение остаётся прежним.

| Файл | Изменение |
| --- | --- |
| `StudyTracker.csproj` | `<Compile Remove="tests/**" />`: иначе `.cs` из тестов попадут в приложение |
| `Program.cs` | В конце `public partial class Program;` для `WebApplicationFactory` |
| `Notifications/TelegramClient.cs` | `HttpMessageHandler` берётся из DI (по умолчанию `SocketsHttpHandler` с `AllowAutoRedirect = false`); таймауты читаются из `TelegramOptions` |
| `Notifications/TelegramOptions.cs` | Поля `SendTimeoutSeconds` (10) и `PollTimeoutSeconds` (40) |
| `AccountCommands.cs` | Источник пароля передаётся параметром (по умолчанию консоль), чтобы проверить `--create-account` без подпроцесса и без `Console.IsInputRedirected` |

`Program.cs` читает `Tracker:DataDirectory` сразу после `CreateBuilder`, а `ConfigureAppConfiguration` в фабрике срабатывает позже. Настройки в тестах поэтому задаются через `UseSetting` или переменные окружения. Это проверяется самым первым тестом.

`DatabaseSetup.PrepareAsync` без `--migrate` падает, если миграции не применены. Фабрика сначала мигрирует временную базу своим `TrackerDb` и только потом поднимает хост.

## Перенос проверок

Первым шагом плана составляется таблица «76 проверок → тест на C#» с названиями. Ни одна проверка не пропадает молча: если её отбрасываем, причина записывается. Ниже — распределение по классам; тесты внутри класса можно делить и объединять, если покрытие то же.

| Класс | Откуда | Число | Что проверяет |
| --- | --- | --- | --- |
| `AssignmentApiTests` | `test_api.py` | 3 | Жизненный цикл задания, ошибки ввода, `404` для чужих и отсутствующих записей |
| `SubjectApiTests` | `test_api.py` | 1 | Проверка названия, дубли на кириллице при разном регистре |
| `SecurityTests` | `test_api.py` | 2 | CSRF для всех изменяющих запросов, заголовки безопасности, страница |
| `PersistenceTests` | `test_api.py`, `test_ownership.py` | 2 | Данные одного и двух пользователей переживают перезапуск хоста |
| `DatabaseSetupTests` | `test_api.py`, `test_notifications.py` | 5 | Миграции на пустой базе, повтор, старая и общая база не меняются, миграция 0.3 сохраняет аккаунт |
| `AuthTests` | `test_auth.py` | 10 | Регистрация, вход, выход, пароль, блокировка после 5 ошибок, ограничение 10 запросов в минуту, CSRF, режим Production |
| `AccountCommandsTests` | `test_auth.py` | 2 | `--create-account` создаёт аккаунт, короткий пароль отклоняется |
| `OwnershipTests` | `test_ownership.py` | 7 | Чужие записи недоступны и не меняются, `OwnerId` из тела запроса игнорируется |
| `LegacyImportTests` | `test_ownership.py` | 3 | Перенос старой базы, откат при ошибке, неизвестный владелец |
| `ReminderPlannerTests` | `test_planner.py` | 7 | Московское время, нет дублей, выполненные и изменённые задания, окно 15 минут |
| `ReminderSenderTests` | `test_sender.py`, `test_notifications.py` | 10 | `message_id`, `429` и `retry_after`, лимит повторов, `400`, `403`, `5xx`, обрыв сети, восстановление после перезапуска, состав сообщения |
| `TelegramSocketTests` | `test_sender.py` | 2 | Настоящий таймаут (1 с) и обрыв тела ответа: результат `Unknown`, повтора нет |
| `TelegramBindingTests` | `test_telegram.py` | 11 | Одноразовый код, срок 10 минут, группа, чужой чат, webhook, токен не попадает в журнал |
| `NotificationSettingsTests` | `test_notifications.py` | 5 | Значения по умолчанию, приватность, CSRF, часовой пояс, дубль напоминания |
| `NotificationActionsTests` | `test_notification_actions.py` | 6 | Проверочное сообщение, лимит, согласие, отключение, чужой аккаунт |

Итого 76. Планировщик и отправщик вызываются прямо из DI, а не командами `--notifications-plan` и `--notifications-tick` в подпроцессе. Фоновая служба в тестах выключена (`ManualMode`). Прямое чтение таблиц делается через `TrackerDb`, а не `sqlite3`.

Python-файлы удаляются по мере переноса своей группы. `test_deployment.py` импортирует `Client` и `ROOT` из `support.py` и запускает `fake_telegram.py` в контейнере `fake-telegram`, поэтому эти два файла остаются. Удаляется `telegram_support.py` и все `test_*.py`, кроме `test_deployment.py`.

## Разбор Program.cs

После переноса тестов, поведение прежнее:

| Новый файл | Что внутри |
| --- | --- |
| `Endpoints/SubjectEndpoints.cs` | `MapSubjects`: список, создание, удаление предметов |
| `Endpoints/AssignmentEndpoints.cs` | `MapAssignments`: список, создание, изменение, удаление |
| `Endpoints/AssignmentValidator.cs` | `ValidateAssignment` и ограничения длины и дат |
| `CommandLine.cs` | Флаги `--migrate`, `--telegram-poll`, `--notifications-plan`, `--notifications-tick`, `--create-account`, `--import-legacy` |
| `SecurityMiddleware.cs` | Заголовки CSP и `nosniff`, `no-store` для `/api`, проверка CSRF |

`ReminderSender.SendPendingAsync` режется на методы с понятными названиями (выбор записи, проверка актуальности, сборка текста, разбор ответа Telegram). Порядок операций и транзакций не меняется. `Program.cs` остаётся сборкой приложения.

## CI, Docker и документация

- `check.yml`: шаг `dotnet test tests/StudyTracker.Tests`; шаг `python -m unittest discover` убирается, когда в `tests/` остался только `test_deployment.py`. Контейнерное задание не меняется.
- `Dockerfile` собирает только `StudyTracker.csproj`, а `.dockerignore` уже исключает `tests`; контейнерные проверки подтверждают, что образ не изменился.
- `README`: раздел «Проверка», таблица технологий и список «Как устроено». Документ версии: `docs/plans/тесты-на-csharp.md`.

## Риски

| Риск | Что делаем |
| --- | --- |
| Настройки не доходят до `Program.cs` через фабрику | Первый тест проверяет `Tracker:DataDirectory`; при сбое переходим на переменные окружения |
| Тесты мешают друг другу через общее время и базу | Отдельный экземпляр фабрики и папка на класс; `FakeTimeProvider` не общий |
| Тест на таймаут нестабилен на медленной машине | Таймаут 1 с, запас на ожидание заглушки сокета 5 с, повторов нет |
| Рефакторинг незаметно меняет поведение | Разбор только после зелёного набора; контейнерные проверки в конце |

## Готово, когда

- Все 76 проверок имеют тест на C# (таблица в плане заполнена без пропусков).
- `dotnet test tests/StudyTracker.Tests` проходит на Windows и Linux в GitHub Actions.
- Python остаётся только для `test_deployment.py` с его вспомогательными `support.py` и `fake_telegram.py`, и 7 контейнерных проверок проходят.
- В `Program.cs` нет ни обработчиков эндпоинтов, ни разбора флагов.
- Время полного прогона замерено и записано в `README` как есть.
