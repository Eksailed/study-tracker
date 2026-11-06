using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using StudyTracker;

var migrate = args.Contains("--migrate");
var builder = WebApplication.CreateBuilder(args.Where(a => a != "--migrate").ToArray());
var dataDirectory = builder.Configuration["Tracker:DataDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDirectory);
builder.Services.AddDbContext<TrackerDb>(options => options.UseSqlite(
    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
    { DataSource = Path.Combine(dataDirectory, "tracker.db"), ForeignKeys = true }.ToString()));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<AssignmentStatus>()));
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
try
{
    await DatabaseSetup.PrepareAsync(app.Services, migrate);
}
catch (InvalidOperationException error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
    return;
}
if (migrate) return;

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method)
            || HttpMethods.IsDelete(context.Request.Method))
        {
            try
            {
                await context.RequestServices.GetRequiredService<IAntiforgery>()
                    .ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "Обновите страницу и повторите действие." });
                return;
            }
        }
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/token", (HttpContext context, IAntiforgery antiforgery) =>
    Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));

app.MapGet("/api/subjects", async (TrackerDb db) =>
    Results.Ok(await db.Subjects.OrderBy(s => s.Name).Select(s => new { s.Id, s.Name, s.Color }).ToListAsync()));

var colors = new[] { "blue", "teal", "purple", "orange", "pink", "slate" };
app.MapPost("/api/subjects", async (SubjectInput input, TrackerDb db) =>
{
    var name = input.Name?.Trim() ?? "";
    if (name.Length is < 1 or > 80 || !colors.Contains(input.Color))
        return Results.BadRequest(new { error = "Укажите название предмета до 80 символов и выберите цвет." });
    var subject = new Subject { Name = name, NameKey = name.ToUpperInvariant(), Color = input.Color! };
    db.Subjects.Add(subject);
    try { await db.SaveChangesAsync(); }
    catch (DbUpdateException) { return Results.Conflict(new { error = "Такой предмет уже есть." }); }
    return Results.Created($"/api/subjects/{subject.Id}", new { subject.Id, subject.Name, subject.Color });
});

app.MapDelete("/api/subjects/{id:int}", async (int id, TrackerDb db) =>
{
    var subject = await db.Subjects.FindAsync(id);
    if (subject is null) return Results.NotFound();
    if (await db.Assignments.AnyAsync(a => a.SubjectId == id))
        return Results.Conflict(new { error = "Сначала удалите задания предмета или перенесите их в другой предмет." });
    db.Subjects.Remove(subject);
    try { await db.SaveChangesAsync(); }
    catch (DbUpdateException) { return Results.Conflict(new { error = "У предмета появились задания. Обновите страницу." }); }
    return Results.NoContent();
});

app.MapGet("/api/assignments", async (TrackerDb db) =>
    Results.Ok(await db.Assignments.AsNoTracking().OrderBy(a => a.DueDate).ThenBy(a => a.Id).ToListAsync()));

app.MapPost("/api/assignments", async (AssignmentInput input, TrackerDb db) =>
{
    var error = await ValidateAssignment(input, db);
    if (error is not null) return Results.BadRequest(new { error });
    var assignment = new Assignment { Title = input.Title!.Trim(), Description = input.Description?.Trim() ?? "",
        SubjectId = input.SubjectId, DueDate = input.DueDate, Status = input.Status };
    db.Assignments.Add(assignment);
    await db.SaveChangesAsync();
    return Results.Created($"/api/assignments/{assignment.Id}", assignment);
});

app.MapPut("/api/assignments/{id:int}", async (int id, AssignmentInput input, TrackerDb db) =>
{
    var assignment = await db.Assignments.FindAsync(id);
    if (assignment is null) return Results.NotFound();
    var error = await ValidateAssignment(input, db);
    if (error is not null) return Results.BadRequest(new { error });
    assignment.Title = input.Title!.Trim();
    assignment.Description = input.Description?.Trim() ?? "";
    assignment.SubjectId = input.SubjectId;
    assignment.DueDate = input.DueDate;
    assignment.Status = input.Status;
    await db.SaveChangesAsync();
    return Results.Ok(assignment);
});

app.MapDelete("/api/assignments/{id:int}", async (int id, TrackerDb db) =>
{
    var assignment = await db.Assignments.FindAsync(id);
    if (assignment is null) return Results.NotFound();
    db.Assignments.Remove(assignment);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.Run();

static async Task<string?> ValidateAssignment(AssignmentInput input, TrackerDb db)
{
    if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Trim().Length > 140)
        return "Введите название задания от 1 до 140 символов.";
    if (input.Description?.Length > 2000) return "Описание должно быть не длиннее 2000 символов.";
    if (!Enum.IsDefined(input.Status)) return "Выберите допустимый статус.";
    if (input.DueDate < new DateOnly(2000, 1, 1) || input.DueDate > new DateOnly(2100, 12, 31))
        return "Выберите срок между 2000 и 2100 годом.";
    if (!await db.Subjects.AnyAsync(s => s.Id == input.SubjectId)) return "Выберите существующий предмет.";
    return null;
}
