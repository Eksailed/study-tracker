using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace StudyTracker.Tests.Support;

/// <summary>Приложение во временной папке с уже применёнными миграциями. Один хост и одна база на тест.</summary>
public sealed class TrackerFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> settings;
    private readonly string environment;
    private readonly Action<IServiceCollection>? services;
    private readonly bool ownsDirectory;
    private bool cleaned;
    private bool restarting;

    public string DataDirectory { get; }

    public TrackerFactory(IReadOnlyDictionary<string, string?>? settings = null, string environment = "Development",
        Action<IServiceCollection>? services = null)
        : this(settings, environment, services, Directory.CreateTempSubdirectory("zachet-tests-").FullName, owns: true)
    {
        using var db = new TrackerDb(new DbContextOptionsBuilder<TrackerDb>()
            .UseSqlite(ConnectionString(DataDirectory)).Options);
        db.Database.Migrate();
        SqliteConnection.ClearAllPools();
    }

    private TrackerFactory(IReadOnlyDictionary<string, string?>? settings, string environment,
        Action<IServiceCollection>? services, string directory, bool owns)
    {
        this.settings = settings ?? new Dictionary<string, string?>();
        this.environment = environment;
        this.services = services;
        DataDirectory = directory;
        ownsDirectory = owns;
    }

    /// <summary>Хост на готовой папке без миграций (например, после <c>--restore</c>). Папку удаляет вызывающий.</summary>
    public static TrackerFactory OnDirectory(string directory) => new(null, "Development", null, directory, owns: false);

    private static string ConnectionString(string directory) =>
        new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "tracker.db"), ForeignKeys = true }.ToString();

    public ApiClient NewClient() => new(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));

    /// <summary>Новый хост на той же папке и с теми же настройками. Папку удалит только исходная фабрика.</summary>
    public TrackerFactory Restart()
    {
        restarting = true;
        try { base.Dispose(true); }
        finally { restarting = false; }
        return new(settings, environment, services, DataDirectory, owns: false);
    }

    public T Scope<T>(Func<IServiceProvider, T> use)
    {
        using var scope = Services.CreateScope();
        return use(scope.ServiceProvider);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("Tracker:DataDirectory", DataDirectory);
        foreach (var (key, value) in settings) builder.UseSetting(key, value);
        if (services is not null) builder.ConfigureServices(services);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Cleanup();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Cleanup();
    }

    private void Cleanup()
    {
        if (cleaned || restarting) return;
        cleaned = true;
        SqliteConnection.ClearAllPools();
        if (!ownsDirectory) return;
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(DataDirectory, recursive: true); return; }
            catch (IOException) when (attempt < 5) { Thread.Sleep(100); }
        }
    }
}
