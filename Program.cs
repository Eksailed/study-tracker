using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using StudyTracker;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using StudyTracker.Notifications;
using StudyTracker.ApiTokens;
using StudyTracker.Import;
using StudyTracker.Calendar;

var migrate = args.Contains("--migrate");
var builder = WebApplication.CreateBuilder(args.Where(a => !CommandLine.Flags.Contains(a)).ToArray());
if (!builder.Environment.IsDevelopment()) builder.Logging.AddJsonConsole();
var dataDirectory = builder.Configuration["Tracker:DataDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "v02");
Directory.CreateDirectory(dataDirectory);
var keyDirectory = builder.Configuration["Tracker:KeyDirectory"] ?? Path.Combine(dataDirectory, "keys");
Directory.CreateDirectory(keyDirectory);
builder.Services.AddSingleton(new TrackerPaths(dataDirectory, keyDirectory));
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
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<AssignmentStatus>());
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<AssignmentPriority>());
});
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddProblemDetails();
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddTrackerAccounts(builder.Environment);
builder.AddNotifications();
builder.Services.AddApiDocs();
builder.Services.AddImport();
builder.Services.AddCalendar();

var app = builder.Build();
app.UseRequestLogging();
app.UseExceptionHandler();
if (args.Contains("--restore"))
{
    // Папка данных должна остаться пустой: подготовка базы создала бы tracker.db, и восстановление отказало бы.
    Environment.ExitCode = await BackupCommands.RunAsync(args, app.Services, Console.Out, Console.Error);
    return;
}
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
if (await CommandLine.TryRunAsync(args, app)) return;

if (!string.IsNullOrWhiteSpace(proxyAddress)) app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseAuthentication();
app.UseAccountHeader();
app.UseRateLimiter();
app.UseAuthorization();
app.UseTrackerSecurity();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapTrackerAccounts();
app.MapNotifications();
app.MapHealth();
app.MapApiDocs();
app.MapCalendarFeed();

app.MapGet("/api/token", (HttpContext context, IAntiforgery antiforgery) =>
    Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));

var data = app.MapGroup("/api").RequireAuthorization();
data.MapSubjects();
data.MapAssignments();
data.MapSubtasks();
data.MapTokens();
data.MapImport();
data.MapCalendar();

var v1 = app.MapGroup(ApiTokenAuth.V1).RequireAuthorization(ApiTokenAuth.ScopePolicy).RequireRateLimiting(ApiTokenAuth.LimiterPolicy);
v1.MapSubjects(ApiTokenAuth.V1);
v1.MapAssignments(ApiTokenAuth.V1);

await app.Services.GetRequiredService<ReminderSender>().RecoverAsync(CancellationToken.None);
app.Run();

public partial class Program;
