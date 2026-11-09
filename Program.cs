using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using StudyTracker;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using StudyTracker.Notifications;

var migrate = args.Contains("--migrate");
var commandFlags = new[] { "--migrate", "--telegram-poll", "--notifications-tick", "--notifications-plan" };
var builder = WebApplication.CreateBuilder(args.Where(a => !commandFlags.Contains(a)).ToArray());
var dataDirectory = builder.Configuration["Tracker:DataDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "v02");
Directory.CreateDirectory(dataDirectory);
var keyDirectory = builder.Configuration["Tracker:KeyDirectory"] ?? Path.Combine(dataDirectory, "keys");
Directory.CreateDirectory(keyDirectory);
builder.Services.AddDataProtection().SetApplicationName("Zachet")
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
var proxyAddress = builder.Configuration["Proxy:KnownAddress"];
if (!string.IsNullOrWhiteSpace(proxyAddress))
{
    var address = IPAddress.Parse(proxyAddress);
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(address);
        options.KnownProxies.Add(address.MapToIPv6());
    });
}
builder.Services.AddDbContext<TrackerDb>(options => options.UseSqlite(
    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
    { DataSource = Path.Combine(dataDirectory, "tracker.db"), ForeignKeys = true }.ToString()));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<AssignmentStatus>()));
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddProblemDetails();
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddTrackerAccounts(builder.Environment);
builder.AddNotifications();

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
if (args.Contains("--notifications-tick"))
{
    var telegram = app.Services.GetRequiredService<TelegramClient>();
    if (await telegram.InitializeAsync(CancellationToken.None))
    {
        var sender = app.Services.GetRequiredService<ReminderSender>();
        await sender.RecoverAsync(CancellationToken.None);
        await app.Services.GetRequiredService<ReminderPlanner>().PlanAsync(CancellationToken.None);
        await sender.SendPendingAsync(CancellationToken.None);
    }
    else { Console.Error.WriteLine(telegram.Status); Environment.ExitCode = 1; }
    return;
}
if (args.Contains("--notifications-plan"))
{
    var telegram = app.Services.GetRequiredService<TelegramClient>();
    if (await telegram.InitializeAsync(CancellationToken.None))
        await app.Services.GetRequiredService<ReminderPlanner>().PlanAsync(CancellationToken.None);
    else { Console.Error.WriteLine(telegram.Status); Environment.ExitCode = 1; }
    return;
}
if (args.Contains("--telegram-poll"))
{
    var telegram = app.Services.GetRequiredService<TelegramClient>();
    if (await telegram.InitializeAsync(CancellationToken.None))
        await app.Services.GetRequiredService<TelegramUpdateWorker>().PollAsync(CancellationToken.None, timeout: 0);
    else { Console.Error.WriteLine(telegram.Status); Environment.ExitCode = 1; }
    return;
}
if (args.Contains("--create-account"))
{
    try
    {
        var index = Array.IndexOf(args, "--create-account");
        if (index + 1 >= args.Length) throw new InvalidOperationException("Укажите почту после --create-account.");
        await AccountCommands.CreateAsync(app.Services, args[index + 1]);
    }
    catch (InvalidOperationException error)
    {
        Console.Error.WriteLine(error.Message);
        Environment.ExitCode = 1;
    }
    return;
}
if (args.Contains("--import-legacy"))
{
    try
    {
        string Option(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("--"))
                throw new InvalidOperationException($"Укажите значение {name}.");
            return args[index + 1];
        }
        await LegacyImport.RunAsync(app.Services, Option("--import-legacy"), Option("--owner"));
    }
    catch (Exception error) when (error is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or FormatException or DbUpdateException)
    {
        Console.Error.WriteLine("Перенос отменён: " + error.Message);
        Environment.ExitCode = 1;
    }
    return;
}

if (!string.IsNullOrWhiteSpace(proxyAddress)) app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
        context.Response.Headers["X-Account-Id"] = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    await next();
});
app.UseRateLimiter();
app.UseAuthorization();

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
app.MapTrackerAccounts();
app.MapNotifications();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/token", (HttpContext context, IAntiforgery antiforgery) =>
    Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));

var data = app.MapGroup("/api").RequireAuthorization();
data.MapSubjects();
data.MapAssignments();

await app.Services.GetRequiredService<ReminderSender>().RecoverAsync(CancellationToken.None);
app.Run();

public partial class Program;
