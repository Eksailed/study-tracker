using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace StudyTracker.ApiTokens;

public record TokenInput(string? Name, string[]? Scopes, int? Days);

public static class TokenEndpoints
{
    public const string ReadScope = "tasks:read";
    public const string WriteScope = "tasks:write";
    public const int ReadFlag = 1;
    public const int WriteFlag = 2;
    public const int MaxActive = 10;
    public const int DefaultDays = 90;

    public static IEnumerable<string> ScopeNames(int scopes)
    {
        if ((scopes & ReadFlag) != 0) yield return ReadScope;
        if ((scopes & WriteFlag) != 0) yield return WriteScope;
    }

    public static void MapTokens(this RouteGroupBuilder group)
    {
        group.MapGet("/tokens", async (TrackerDb db, ClaimsPrincipal user, TimeProvider clock) =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var tokens = await db.ApiTokens.AsNoTracking().Where(t => t.OwnerId == owner).OrderByDescending(t => t.Id).ToListAsync();
            return Results.Ok(tokens.Select(t => new
            {
                t.Id, t.Name, t.Prefix, scopes = ScopeNames(t.Scopes), t.CreatedAt, t.ExpiresAt, t.LastUsedAt,
                revoked = t.RevokedAt is not null, expired = t.ExpiresAt <= now,
            }));
        });

        group.MapPost("/tokens", async (TokenInput input, TrackerDb db, ClaimsPrincipal user, TimeProvider clock) =>
        {
            var name = input.Name?.Trim() ?? "";
            if (name.Length is < 1 or > 60)
                return Results.BadRequest(new { error = "Укажите название токена до 60 символов." });
            var scopes = 0;
            foreach (var scope in input.Scopes ?? [])
            {
                if (scope == ReadScope) scopes |= ReadFlag;
                else if (scope == WriteScope) scopes |= WriteFlag;
                else return Results.BadRequest(new { error = "Неизвестная область доступа. Допустимы tasks:read и tasks:write." });
            }
            if (scopes == 0)
                return Results.BadRequest(new { error = "Выберите хотя бы одну область доступа." });
            var days = input.Days ?? DefaultDays;
            if (days is < 1 or > 365)
                return Results.BadRequest(new { error = "Срок действия — от 1 до 365 дней." });

            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var now = clock.GetUtcNow();
            var nowMs = now.ToUnixTimeMilliseconds();
            var active = await db.ApiTokens.CountAsync(t => t.OwnerId == owner && t.RevokedAt == null && t.ExpiresAt > nowMs);
            if (active >= MaxActive)
                return Results.Conflict(new { error = $"Действующих токенов не больше {MaxActive}. Отзовите ненужный." });

            var (token, hash, prefix) = TokenGenerator.Create();
            var entity = new ApiToken { OwnerId = owner, Name = name, Hash = hash, Prefix = prefix, Scopes = scopes,
                CreatedAt = nowMs, ExpiresAt = now.AddDays(days).ToUnixTimeMilliseconds() };
            db.ApiTokens.Add(entity);
            await db.SaveChangesAsync();
            return Results.Created($"/api/tokens/{entity.Id}", new
            {
                entity.Id, entity.Name, entity.Prefix, scopes = ScopeNames(entity.Scopes), entity.ExpiresAt, token,
            });
        });

        group.MapDelete("/tokens/{id:int}", async (int id, TrackerDb db, ClaimsPrincipal user, TimeProvider clock) =>
        {
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var token = await db.ApiTokens.SingleOrDefaultAsync(t => t.Id == id && t.OwnerId == owner);
            if (token is null) return Results.NotFound();
            if (token.RevokedAt is null)
            {
                token.RevokedAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
                await db.SaveChangesAsync();
            }
            return Results.NoContent();
        });
    }
}
