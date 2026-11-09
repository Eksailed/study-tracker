using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace StudyTracker.Import;

public static class ImportEndpoints
{
    public const string LimiterPolicy = "import";

    public static IServiceCollection AddImport(this IServiceCollection services)
    {
        services.AddScoped<ObsidianImport>();
        services.AddRateLimiter(options => options.AddPolicy(LimiterPolicy, context =>
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } owner
                ? RateLimitPartition.GetFixedWindowLimiter(owner, _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                : RateLimitPartition.GetNoLimiter("anonymous")));
        return services;
    }

    /// <summary>Импорт доступен только вошедшему по cookie: токены API действуют лишь на <c>/api/v1</c>.</summary>
    public static void MapImport(this RouteGroupBuilder group)
    {
        var import = group.MapGroup("/import/obsidian").RequireRateLimiting(LimiterPolicy);

        import.MapPost("/preview", async (ImportRequest request, ObsidianImport service, ClaimsPrincipal user, CancellationToken token) =>
        {
            try { return Results.Ok(await service.PreviewAsync(user.FindFirstValue(ClaimTypes.NameIdentifier)!, request, token)); }
            catch (ImportException error) { return Results.BadRequest(new { error = error.Message }); }
        });

        import.MapPost("", async (ImportRequest request, ObsidianImport service, ClaimsPrincipal user, CancellationToken token) =>
        {
            try { return Results.Ok(await service.ApplyAsync(user.FindFirstValue(ClaimTypes.NameIdentifier)!, request, token)); }
            catch (ImportException error) { return Results.BadRequest(new { error = error.Message }); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "Импорт уже выполняется. Повторите через минуту." }); }
        });
    }
}
