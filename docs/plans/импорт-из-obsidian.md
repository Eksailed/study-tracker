# План: импорт заданий из Obsidian

Статус: в работе, версия 0.6, план от 9 ноября 2026 года. Сделаны задачи 1–3 из 6 (разбор заметки, колонка `ImportKey`, сервис импорта). Работа идёт сразу в `main`, после каждой задачи — коммит и push.

Связано: [спецификация](../specs/импорт-из-obsidian.md), [README](../../README.md), [план версии 0.5](эксплуатация-и-api.md), [модели](../../Models.cs), [CI](../../.github/workflows/check.yml).

> **Для исполнителей:** `superpowers:executing-plans` (способ «native» выбран пользователем заранее). Шаги отмечаются флажками `- [ ]`.

**Цель.** Дать пользователю кнопку «Импорт из Obsidian»: выбор папки `ДЗ`, предпросмотр, добавление заданий и предметов в аккаунт; повтор добавляет только новое.

**Подход.** Снизу вверх: чистый разбор одной заметки, затем колонка `ImportKey` в базе, затем сервис предпросмотра и добавления, затем адреса с лимитером, затем интерфейс и документы. Каждая проверка пишется раньше кода и после зелёного прогона проверяется шагом «сломать и вернуть».

**Технологии.** .NET 10 (`~/.dotnet/dotnet.exe`), EF Core 10 + SQLite (миграция через `dotnet tool run dotnet-ef`), xUnit с `WebApplicationFactory<Program>` (помощники в `tests/StudyTracker.Tests/Support/`), JavaScript без сборки.

## Общие правила

- Команды: сборка `~/.dotnet/dotnet.exe build --nologo`; тесты `~/.dotnet/dotnet.exe test tests/StudyTracker.Tests --nologo [--filter Имя]`; JS `node --check wwwroot/<файл>.js`.
- Тест сначала красный по нужной причине, потом зелёный; после зелёного — «сломать и вернуть»: временно испортить ключевую строку кода, увидеть красный тест, вернуть.
- Коммиты по-русски, без строк об авторстве Claude. После каждой задачи: полный `dotnet test`, коммит, `git push origin main`, строка в журнал `.superpowers/sdd/импорт-из-obsidian/progress.md`.
- Тексты ошибок и причин пропуска — по-русски, как в таблицах ниже.
- Образцы заметок для тестов — `tests/StudyTracker.Tests/Support/ObsidianSamples.cs` (константы со строками; взяты из формата реального хранилища).

## Уточнения к спецификации

Найдены при чтении кода; спецификация поправляется в задаче 6.

| Что | Как в спецификации | Как делаем |
| --- | --- | --- |
| Запрос без CSRF | `403` | `400` «Обновите страницу и повторите действие.»: так отвечает `SecurityMiddleware.cs` для всех адресов `/api` |
| Размер запроса | «тело не больше 3 МБ» | Считаем сами: сумма UTF-8 байт всех текстов не больше 2 МБ (`2 * 1024 * 1024`), иначе `400`; ограничение Kestrel на тело в тестах `TestServer` не действует |
| Заметка без `subject` | не описано | Пропуск «не указан предмет» |
| Лимитер | «10 в минуту на аккаунт» | Разделение по `ClaimTypes.NameIdentifier` пользователя |
| Токен API | `401` | Так и есть: схема `Smart` вне `/api/v1` отвечает `401` |

## Что проверяем сверх спецификации

- Заметка, где в описании встречается строка `---` или `## Задание` внутри блока кода: шапка берётся только из начала файла, раздел — первый `## Задание`.
- Два предмета в одной загрузке, различающиеся только регистром (`ИКТ` и `икт`): создаётся один.
- Эмодзи на границе обрезки описания в 2000 символов: пара суррогатов не рвётся.
- Загрузка с одними не-заданиями (например, вся папка `Предметы`): `200` с нулями, а не ошибка.
- Параллельный импорт того же ключа: `409`, база без частичных записей.

## Файлы

| Файл | Действие | За что отвечает |
| --- | --- | --- |
| `Import/ObsidianNote.cs` | создать | Разбор одной заметки |
| `Import/ObsidianImport.cs` | создать | Предпросмотр и добавление |
| `Import/ImportEndpoints.cs` | создать | Адреса, лимитер `import` |
| `Models.cs` | изменить | `Assignment.ImportKey`, индекс |
| `Migrations/…_ImportKey.cs` | создать | Миграция |
| `Program.cs` | изменить | `AddImport()`, `MapImport()` |
| `wwwroot/import.js` | создать | Диалог импорта |
| `wwwroot/index.html`, `wwwroot/app.css` | изменить | Кнопка, диалог, стили |
| `wwwroot/app.js` | изменить | `import` в личных путях `api()` |
| `tests/StudyTracker.Tests/ObsidianNoteTests.cs`, `ObsidianImportTests.cs`, `ImportApiTests.cs`, `Support/ObsidianSamples.cs` | создать | Проверки |
| `README.md`, `.github/workflows/check.yml`, спецификация и этот план | изменить | Документы |

---

### Задача 1: разбор заметки

**Файлы:** создать `Import/ObsidianNote.cs`, `tests/StudyTracker.Tests/ObsidianNoteTests.cs`, `tests/StudyTracker.Tests/Support/ObsidianSamples.cs`.

**Интерфейс (даёт задачам 3–4):**

```csharp
namespace StudyTracker.Import;

public sealed record ParsedNote(string Path, string Title, string SubjectName, DateOnly Due,
    AssignmentStatus Status, string Description, bool TitleCut, bool DescriptionCut, string Key);

/// <summary>Либо Note, либо Skip (причина), либо NotHomework (заметка не задание, в список пропусков не попадает).</summary>
public sealed record NoteResult(ParsedNote? Note, string? Skip, bool NotHomework);

public static class ObsidianNote
{
    public const int MaxTitle = 140, MaxDescription = 2000, MaxSubject = 80, MaxKey = 300;
    public static NoteResult Parse(string path, string text, DateOnly? defaultDue);
}
```

**Правила разбора** (из раздела «Разбор заметки» спецификации): BOM и CRLF убираются; шапка — только если файл начинается с `---` на отдельной строке, до следующей строки `---`; `ключ: значение`, кавычки по краям снимаются, остальное не поддерживается; нет шапки или `type` не `homework` → `NotHomework`. Причины пропуска дословно: `удалено на сайте`, `не указан предмет`, `имя предмета длиннее 80 символов`, `нет срока`, `неверный срок`. Предмет: `[[Имя]]` или `[[Имя|Псевдоним]]` → `Имя`; значение без скобок берётся как есть. Название — первая строка `# …` после шапки, иначе имя файла без `.md` (последний сегмент пути по `/` и `\`). Срок — только `ГГГГ-ММ-ДД` в диапазоне 2000-01-01…2100-12-31. Статус: `done: true` → `Done`, иначе `Todo`. Описание — строки после первой строки, равной `## Задание`, до следующей строки, начинающейся с `## `, лишние пустые строки по краям убираются. Обрезка: название больше 140 → 139 символов и `…`, описание больше 2000 → 1999 и `…` (если на границе стоит первая половина суррогатной пары, отрезать на символ меньше). Ключ: `source` обрезанный до 300; без `source` — `ПРЕДМЕТ|НАЗВАНИЕ|2026-10-06` (первые два в `ToUpperInvariant()`, название до обрезки).

- [x] **Шаг 1. Тесты (красные).** В `ObsidianNoteTests` — методы с этими утверждениями:
  - `ParsesTypicalNote`: образец «Лабораторная работа 1» → `Title="Лабораторная работа 1"`, `SubjectName="ИКТ"`, `Due=2026-10-06`, `Status=Done`, `Key="https://xn--d1asac1a.xn--p1ai/?page=homework&id=11"`, `Description` начинается с `Срок сдачи ± месяц.` и не содержит `Отметка «сделал»`.
  - `HandlesCrlfAndBom`: тот же текст с `\r\n` и `﻿` в начале даёт тот же результат.
  - `SubjectAliasLink`: `"[[ИКТ|Информатика]]"` → `ИКТ`; `subject: ИКТ` без скобок → `ИКТ`.
  - `MissingSubjectIsSkipped`, `LongSubjectIsSkipped` (81 символ), `EmptyDueIsSkippedWithoutDefault` (`Skip="нет срока"`), `EmptyDueUsesDefault` (`Due` = переданной), `BadDueIsSkipped` (`2026-13-40`, `06.10.2026` → `неверный срок`), `DueOutOfRangeIsSkipped` (`1999-12-31`, `2101-01-01`).
  - `DoneVariants`: `true` → `Done`; `false`, пусто, `yes`, ключа нет → `Todo`.
  - `RemovedIsSkipped`: `removed: true` → `Skip="удалено на сайте"`.
  - `NoTaskSectionGivesEmptyDescription`; `DescriptionStopsAtNextSection`; `DescriptionKeepsSubheadings` (`### Подзаголовок` остаётся); `SeparatorInsideDescriptionIsKept` (строка `---` в теле).
  - `DescriptionLimit`: 2000 символов — без обрезки, `DescriptionCut=false`; 2001 — длина 2000, оканчивается `…`, `DescriptionCut=true`; `DescriptionCutKeepsSurrogatePair` (эмодзи на границе, результат валидный: нет одиночного суррогата).
  - `TitleLimit`: 140 — без обрезки; 141 — длина 140, оканчивается `…`, `TitleCut=true`; `TitleFromFileNameWithoutHeading` (путь `ДЗ\ИКТ\Лаба 1.md` → `Лаба 1`).
  - `NotHomeworkKinds`: `type: note`, нет шапки, пустой текст, шапка без закрытия → `NotHomework=true`, `Note` и `Skip` пусты.
  - `KeyFallbackWithoutSource`: ключ `ИКТ|ЛАБОРАТОРНАЯ РАБОТА 1|2026-10-06`; `KeyIsCutTo300`.
- [x] **Шаг 2.** `~/.dotnet/dotnet.exe test tests/StudyTracker.Tests --nologo --filter ObsidianNoteTests` → не собирается (нет `ObsidianNote`); создать пустую заглушку с сигнатурой, снова запустить → все красные по смыслу.
- [x] **Шаг 3.** Реализовать `ObsidianNote.Parse`: построчный разбор без регулярных выражений для шапки; чистая функция без базы и часов.
- [x] **Шаг 4.** Тот же запуск → зелёные. «Сломать и вернуть»: поменять `2000` на `2001` в обрезке, `Done` на `Todo` для `true`, убрать проверку суррогата — убедиться, что падают `DescriptionLimit`, `ParsesTypicalNote`, `DescriptionCutKeepsSurrogatePair`; вернуть.
- [x] **Шаг 5.** Полный `dotnet test`, коммит `Разбор заметок Obsidian`, push, строка в журнал.

---

### Задача 2: колонка `ImportKey`

**Файлы:** изменить `Models.cs`; создать миграцию; создать `tests/StudyTracker.Tests/ImportKeyMigrationTests.cs`.

**Интерфейс:** `Assignment.ImportKey` — `string?`; индекс `IX_Assignments_OwnerId_ImportKey`, уникальный, фильтр `"ImportKey" IS NOT NULL`. Ключ не попадает в ответы `/api/assignments` и `/api/v1/assignments` (там анонимные объекты без него — проверить, не менять).

- [x] **Шаг 1. Тесты (красные).**
  - `ImportKeyIsUniquePerOwner`: два задания одного владельца с одинаковым ключом → `DbUpdateException` на втором; одинаковый ключ у двух разных владельцев — можно; два задания с `ImportKey = null` у одного владельца — можно.
  - `ApiDoesNotShowImportKey`: задание с ключом через базу; `GET assignments` и `GET v1/assignments` (cookie) не содержат поля `importKey`.
  - `MigrationKeepsOldData`: по образцу `DatabaseSetupTests.NotificationMigrationPreservesAccounts` — база на миграции `ApiTokens`, аккаунт, предмет и задание; после применения новой миграции всё на месте, `ImportKey` пуст.
- [x] **Шаг 2.** Запуск → красные (нет свойства).
- [x] **Шаг 3.** Добавить свойство и индекс в `OnModelCreating`; создать миграцию: `~/.dotnet/dotnet.exe tool run dotnet-ef migrations add ImportKey` (без запуска сервера). Проверить, что в сгенерированном `Up` только `AddColumn` и `CreateIndex` с фильтром.
- [x] **Шаг 4.** Запуск → зелёные. «Сломать и вернуть»: убрать `IsUnique()` — `ImportKeyIsUniquePerOwner` красный; убрать `HasFilter` — тест с двумя `null` красный; вернуть.
- [x] **Шаг 5.** Полный `dotnet test` (старые `DatabaseSetupTests` должны пройти; если они откатывают историю и ломаются на новой колонке — дополнить по образцу правки задачи 5 версии 0.5 и записать решение), коммит `Колонка ImportKey для импорта`, push, журнал.

---

### Задача 3: сервис предпросмотра и добавления

**Файлы:** создать `Import/ObsidianImport.cs`, `tests/StudyTracker.Tests/ObsidianImportTests.cs`.

**Потребляет:** `ObsidianNote.Parse` (задача 1), `Assignment.ImportKey` (задача 2), `SubjectEndpoints.Colors`.

**Интерфейс (даёт задаче 4):**

```csharp
namespace StudyTracker.Import;

public record ImportFile(string? Path, string? Text);
public record ImportRequest(List<ImportFile>? Files, DateOnly? DefaultDue);
public sealed record ImportSkip(string Path, string Reason);
public sealed record ImportItem(string Path, string Title, string Subject, DateOnly Due, AssignmentStatus Status);
public sealed record ImportReport(int NewCount, int ExistingCount, int NotHomework, int Truncated,
    IReadOnlyList<string> NewSubjects, IReadOnlyList<ImportSkip> Skipped, IReadOnlyList<ImportItem> Items);

public sealed class ImportException(string message) : Exception(message);

public sealed class ObsidianImport(TrackerDb db)
{
    public const int MaxFiles = 200, MaxFileBytes = 100 * 1024, MaxTotalBytes = 2 * 1024 * 1024, MaxPath = 260;
    public Task<ImportReport> PreviewAsync(string ownerId, ImportRequest request, CancellationToken token = default);
    public Task<ImportReport> ApplyAsync(string ownerId, ImportRequest request, CancellationToken token = default);
}
```

**Правила.** Проверка запроса (`ImportException` → задача 4 отвечает `400`): `Files` пуст → «Выберите файлы заметок.»; больше 200 → «Слишком много файлов. Выберите папку с заданиями.»; сумма UTF-8 байт текстов больше 2 МБ → «Слишком большая загрузка. Выберите папку с заданиями.»; `DefaultDue` вне 2000-01-01…2100-12-31 → «Выберите срок между 2000 и 2100 годом.». По файлам: путь пуст или длиннее 260 → пропуск «неверный путь» (путь в отчёте обрезается до 260); текст больше 100 КБ → пропуск «файл больше 100 КБ»; пустой текст → `NotHomework`. Остальное — `ObsidianNote.Parse`. Затем: ключ уже у владельца в базе → `ExistingCount++`; ключ уже встречался в загрузке → пропуск «повтор в загрузке»; иначе в `Items`. Предметы: сопоставление по `NameKey = name.ToUpperInvariant()` с существующими предметами владельца и между собой в загрузке (первое написание сохраняется); новые предметы получают цвета `blue, teal, purple, orange, pink, slate` по кругу в порядке сортировки `NameKey` (`StringComparer.Ordinal`), `NewSubjects` — имена в том же порядке. `Truncated` — число заметок из `Items`, у которых `TitleCut` или `DescriptionCut`. `PreviewAsync` ничего не сохраняет; `ApplyAsync` создаёт предметы и задания (`CreatedAt = DateTime.UtcNow`, `ImportKey = Key`) и вызывает `SaveChangesAsync` один раз; `DbUpdateException` пробрасывается наружу.

- [x] **Шаг 1. Тесты (красные).** Через `TrackerFactory`, область `factory.Scope(...)` и два аккаунта; запросы — строки из `ObsidianSamples`:
  - `PreviewWritesNothing`: после предпросмотра в базе 0 предметов и 0 заданий; отчёт `NewCount=3`, `NewSubjects` отсортированы.
  - `ApplyCreatesSubjectsAndTasks`: предметы, статусы, сроки, описания и ключи совпадают с заметками; цвета `blue, teal, purple` для трёх новых предметов.
  - `SecondApplyAddsNothing`: повтор → `NewCount=0`, `ExistingCount=3`, в базе прежнее число записей; правка названия существующего задания не затирается.
  - `ExistingSubjectIsReused`: предмет `икт` создан вручную, импорт `ИКТ` не создаёт второй; `NewSubjects` пуст.
  - `SubjectsDifferingInCaseAreOne`: `ИКТ` и `икт` в одной загрузке → один предмет с первым написанием.
  - `DuplicateKeyInUploadIsSkipped`: две заметки с одним `source` → одна в `Items`, вторая в `Skipped` с причиной «повтор в загрузке».
  - `ManualTaskWithSameTitleIsNotMatched`: ручное задание с тем же названием не мешает, импорт добавляет своё.
  - `OtherOwnersAreSeparate`: у другого аккаунта те же заметки добавляются заново; его база не видит чужих ключей.
  - `LimitsAreChecked`: 200 файлов проходят, 201 → `ImportException`; пустой список → `ImportException`; файл ровно 100 КБ проходит, 100 КБ + 1 → пропуск «файл больше 100 КБ»; сумма 2 МБ проходит, 2 МБ + 1 байт → `ImportException`; путь 261 символ → пропуск «неверный путь»; `DefaultDue` 1999-12-31 → `ImportException`.
  - `OnlyNotHomeworkGivesZeros`: набор заметок других видов → `NewCount=0`, `Skipped` пуст, `NotHomework=N`.
  - `TruncatedIsCounted`: описание 2001 символ → `Truncated=1`.
  - `DefaultDueFillsEmptyDue`.
  - `ApplyIsAtomic`: подмена через `ConfigureDbContext<TrackerDb>` с перехватчиком `SavingChangesAsync`, который перед сохранением вставляет в ту же базу (отдельным подключением) задание с тем же `ImportKey`; `ApplyAsync` бросает `DbUpdateException`, а предметов и заданий этого запроса в базе нет (вставленное перехватчиком — одно).
- [x] **Шаг 2.** Запуск `--filter ObsidianImportTests` → красные.
- [x] **Шаг 3.** Реализовать `ObsidianImport`: один запрос за существующими ключами владельца (`Where(OwnerId && ImportKey in keys)`) и один за его предметами; никаких запросов в цикле.
- [x] **Шаг 4.** Запуск → зелёные. «Сломать и вернуть»: убрать условие `OwnerId` в запросе ключей (`OtherOwnersAreSeparate` красный); убрать проверку повтора в загрузке; поменять порядок сортировки предметов; вызвать `SaveChangesAsync` внутри предпросмотра (`PreviewWritesNothing` красный); вернуть.
- [x] **Шаг 5.** Полный `dotnet test`, коммит `Сервис импорта заданий из Obsidian`, push, журнал.

---

### Задача 4: адреса и лимитер

**Файлы:** создать `Import/ImportEndpoints.cs`, `tests/StudyTracker.Tests/ImportApiTests.cs`; изменить `Program.cs`.

**Потребляет:** `ObsidianImport`, `ImportRequest`, `ImportReport`, `ImportException` (задача 3).

**Интерфейс:**

```csharp
namespace StudyTracker.Import;

public static class ImportEndpoints
{
    public const string LimiterPolicy = "import";
    public static IServiceCollection AddImport(this IServiceCollection services); // регистрирует ObsidianImport (scoped) и лимитер
    public static void MapImport(this RouteGroupBuilder group); // POST /import/obsidian/preview и POST /import/obsidian
}
```

В `Program.cs`: `builder.Services.AddImport();` после `AddApiDocs()`, `data.MapImport();` после `data.MapTokens();`. Лимитер: фиксированное окно 10 запросов в минуту, `QueueLimit = 0`, раздел — `ClaimTypes.NameIdentifier`, без пользователя — `GetNoLimiter`. Ответы: `ImportException` → `400 {"error": сообщение}`; `DbUpdateException` при добавлении → `409 {"error": "Импорт уже выполняется. Повтори через минуту."}`; успех → `200` с `ImportReport` (поля в camelCase, статус строкой — это уже настроено в `ConfigureHttpJsonOptions`). Адреса остаются внутри группы `/api` с `RequireAuthorization()`.

- [ ] **Шаг 1. Тесты (красные)** в `ImportApiTests`, клиент `ApiClient` (после `SignIn`):
  - `PreviewReturnsReport`: `Post("import/obsidian/preview", new { files = …, defaultDue = (string?)null })` → `200`, `newCount`, `existingCount`, `notHomework`, `truncated`, `newSubjects`, `skipped[0].reason`, `items[0].status == "Done"`; в базе пусто.
  - `ApplyThenRepeat`: первый вызов — `newCount=N`, `GET assignments` длиной N; второй — `newCount=0`, `existingCount=N`.
  - `RequiresLogin`: без входа оба адреса → `401`.
  - `TokenIsRejected`: токен `tasks:write` → `401` (тест `Bearer` как в `ApiV1Tests`; вызов через `HttpClient` с `Authorization`, путь `/api/import/obsidian/preview`).
  - `RequiresCsrf`: `Post(..., csrf: false)` → `400`, в теле «Обновите страницу…».
  - `BadRequests`: пустой `files` → `400` с «Выберите файлы заметок.»; `defaultDue` = `"1999-12-31"` → `400`; `defaultDue` = `"не дата"` → `400`; тело не JSON → `400`.
  - `RateLimit`: 10 предпросмотров подряд → `200`, 11-й → `429` с `Retry-After`; второй аккаунт в то же время → `200`.
  - `ApplyConflictGives409`: перехватчик из `ApplyIsAtomic` через `TrackerFactory(services: …)` → `409`, в теле «Импорт уже выполняется».
  - `OtherAccountSeesNothing`: после импорта в аккаунте A у аккаунта B `GET assignments` пуст.
- [ ] **Шаг 2.** Запуск `--filter ImportApiTests` → красные (адресов нет → `404`).
- [ ] **Шаг 3.** Реализовать `ImportEndpoints`, подключить в `Program.cs`.
- [ ] **Шаг 4.** Запуск → зелёные. «Сломать и вернуть»: убрать `RequireRateLimiting` (`RateLimit` красный); заменить `NameIdentifier` постоянным ключом (второй аккаунт получает `429`); убрать обработку `DbUpdateException` (`500` вместо `409`); вернуть.
- [ ] **Шаг 5.** Полный `dotnet test`, коммит `Адреса импорта и лимит запросов`, push, журнал.

---

### Задача 5: диалог в интерфейсе

**Файлы:** создать `wwwroot/import.js`; изменить `wwwroot/index.html`, `wwwroot/app.css`, `wwwroot/app.js`, `.github/workflows/check.yml` (строка `node --check wwwroot/import.js`).

**Интерфейс:** глобальные из `app.js`: `$`, `api`, `notify`, `refreshAfterChange`, `submitForm` (читать перед правкой), `ready`, `currentUser`, `sessionGeneration`. Новые идентификаторы: кнопка `#open-import` («Импорт из Obsidian», символ `i-upload` в наборе иконок), диалог `#import-dialog`, `#import-folder` (`type=file`, `webkitdirectory`, скрыт), `#import-files` (`multiple`, `accept=".md,text/markdown"`, скрыт), кнопки `#import-pick-folder` и `#import-pick-files`, поле `#import-due` (`type=date`, `min=2000-01-01`, `max=2100-12-31`), блок `#import-summary`, список `#import-skipped`, список `#import-new`, `#import-problem`, кнопки `#import-apply` («Добавить») и закрытия. Функции `import.js`: `clearImportState()`, `pickNotes(fileList)` → `{files, skippedLocal}`, `runPreview()`, `runApply()`.

**Поведение.** `pickNotes` берёт только файлы `.md` (по имени), отбрасывает пути, где есть сегмент, начинающийся с точки, читает `file.text()` как UTF-8; больше 200 файлов — сообщение «Слишком много файлов. Выберите папку с заданиями.» без отправки; файл больше 100 КБ не читается и попадает в «пропущено» с причиной «файл больше 100 КБ». После выбора сразу `runPreview()` (`POST import/obsidian/preview`); «Добавить» включается только при `newCount > 0`. Сводка: «Новых: N · уже есть: N · пропущено: N · не задания: N · описаний обрезано: N · новых предметов: …». Все тексты из файлов и ответа — через `textContent`. Изменение поля срока повторяет предпросмотр. «Добавить» отключается на время запроса (защита от двойного нажатия); успех → `notify('Добавлено заданий: N')`, `await refreshAfterChange()`, диалог закрывается; `409` и `429` показывают текст ошибки сервера, `401` обрабатывает `api()`. `clearImportState` вызывается при закрытии диалога и при событии `account-cleared`; запоздавший ответ предпросмотра после очистки или смены аккаунта отбрасывается по счётчику версии, как в `api-access.js`.

- [ ] **Шаг 1.** Прочитать `wwwroot/app.js` (`api`, `refreshAfterChange`, `submitForm`), `wwwroot/api-access.js` как образец; добавить `'import'` в список личных путей `api()`.
- [ ] **Шаг 2.** Разметка и стили: кнопка в навигации под «Доступом к API», диалог по образцу `#tokens-dialog` (ширина `min(650px, calc(100% - 28px))`), скрытые поля выбора, понятные подписи (`<label>` для даты), `aria-live="polite"` у сводки, `role="alert"` у ошибки; в CSP нет inline-скриптов и стилей.
- [ ] **Шаг 3.** Написать `import.js`; `node --check` для него и `app.js`; добавить `node --check wwwroot/import.js` в `check.yml`.
- [ ] **Шаг 4. Проверка в браузере** (Python Playwright, `channel="msedge"`, скрипт во временной папке, запуск `python -I`; сервер из `bin/Debug/net10.0/StudyTracker.dll` с временной `Tracker__DataDirectory`, отдельный запуск с `--migrate`, `Accounts__AllowRegistration=true`). Копия 75 заметок кладётся во временную папку (хранилище не трогать и не писать в него); проверить: выбор папки → сводка с реальными числами (новых 62, пропущено 13 при пустой дате; 75 при заполненной), список пропусков виден; поле срока пересчитывает предпросмотр; «Добавить» → задания в списках; повторный выбор → «Новых: 0», кнопка недоступна; выбор файлов вместо папки; ширина 320, 390, 1440 без горизонтальной прокрутки; полный проход с клавиатуры; консоль без нарушений CSP; смена аккаунта при открытом диалоге очищает диалог. Остановить только свои серверы, удалить временные папки.
- [ ] **Шаг 5.** Полный `dotnet test`, коммит `Диалог импорта из Obsidian`, push, журнал (что проверено, что нет).

---

### Задача 6: документы, итоговая проверка

**Файлы:** изменить `README.md`, `docs/specs/импорт-из-obsidian.md` (статус и уточнения), этот план (статус и раздел «Решения»); проверить `.github/workflows/check.yml`.

- [ ] **Шаг 1.** `README.md`: версия 0.6; в «Что умеет» — импорт; раздел «Импорт из Obsidian» (как пользоваться, лимиты, что не делается, что бывает при повторе, риск дублей с ручными заданиями); структура проекта (`Import/`); `node --check wwwroot/import.js`; число проверок и время как есть из последнего запуска; таблица шагов 0.6; «Что дальше» — 0.7.
- [ ] **Шаг 2.** Спецификация: статус «сделано» с тем, что проверено и что нет; таблица «Уточнения» плана перенесена в текст (CSRF → `400`; размер по сумме байт текстов; пропуск «не указан предмет»; лимитер по аккаунту).
- [ ] **Шаг 3.** Итоговая проверка: `dotnet build --nologo` (0/0), `node --check` для пяти файлов JavaScript, полный `dotnet test`, `docker compose config --quiet`.
- [ ] **Шаг 4.** Самопроверка по разделу «Готово, когда» спецификации и по разделу «Что проверяем сверх спецификации» плана; одна волна исправлений, каждое сначала красным тестом.
- [ ] **Шаг 5.** Статус плана «сделано», раздел «Решения» по журналу; коммит `Версия 0.6: документация и итоговая проверка`, push.

---

## Решения

Заполняется по ходу работы: сюда попадают отклонения от плана, принятые решения и то, что не проверялось.
