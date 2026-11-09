using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace StudyTracker.ApiTokens;

/// <summary>Вход по <c>Authorization: Bearer zt_…</c>: токен действует только на <c>/api/v1</c>.</summary>
public sealed class ApiTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly TimeSpan LastUsedStep = TimeSpan.FromMinutes(5);

    private bool OnV1 => Request.Path.StartsWithSegments(ApiTokenAuth.V1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.Length == 0) return AuthenticateResult.NoResult();
        if (!OnV1) return AuthenticateResult.Fail("Токен действует только на /api/v1.");
        var token = header.Length > 7 ? header[7..].Trim() : "";
        if (!token.StartsWith(TokenGenerator.Marker, StringComparison.Ordinal))
            return AuthenticateResult.Fail("Неверный токен.");

        var db = Context.RequestServices.GetRequiredService<TrackerDb>();
        var hash = TokenGenerator.Hash(token);
        var stored = await db.ApiTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Hash == hash);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (stored is null || stored.RevokedAt is not null || stored.ExpiresAt <= now)
            return AuthenticateResult.Fail("Неверный или просроченный токен.");

        if (stored.LastUsedAt is null || now - stored.LastUsedAt >= LastUsedStep.TotalMilliseconds)
            await db.ApiTokens.Where(t => t.Id == stored.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now));

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, stored.OwnerId),
            new(ApiTokenAuth.TokenIdClaim, stored.Id.ToString()),
        };
        claims.AddRange(TokenEndpoints.ScopeNames(stored.Scopes).Select(s => new Claim(ApiTokenAuth.ScopeClaim, s)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiTokenAuth.Scheme));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, ApiTokenAuth.Scheme));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.WWWAuthenticate = "Bearer";
        await Response.WriteAsJsonAsync(new
        {
            error = OnV1 ? "Неверный или просроченный токен." : "Токен действует только на адресах /api/v1.",
        });
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 403;
        await Response.WriteAsJsonAsync(new { error = "Токену не хватает прав для этого действия." });
    }
}

public static class ApiTokenAuth
{
    public const string Scheme = "ApiToken";
    public const string Smart = "Smart";
    public const string ScopePolicy = "ApiScope";
    public const string LimiterPolicy = "tokens";
    public const string TokenIdClaim = "token-id";
    public const string ScopeClaim = "scope";
    public const string V1 = "/api/v1";

    /// <summary>
    /// Схема-политика <c>Smart</c> выбирает схему по заголовку: есть <c>Bearer</c> — только токен (мусорный токен не
    /// «спасается» cookie), иначе cookie Identity. Вызывать после <c>AddIdentity</c>.
    /// </summary>
    public static void AddApiTokenAuth(this IServiceCollection services)
    {
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = Smart;
            options.DefaultChallengeScheme = Smart;
            options.DefaultForbidScheme = Smart;
        })
        .AddScheme<AuthenticationSchemeOptions, ApiTokenHandler>(Scheme, null)
        .AddPolicyScheme(Smart, null, options => options.ForwardDefaultSelector = context =>
            HasBearer(context.Request) ? Scheme : IdentityConstants.ApplicationScheme);
        services.AddAuthorization(options => options.AddPolicy(ScopePolicy, policy =>
            policy.RequireAuthenticatedUser().RequireAssertion(HasScope)));
        services.AddRateLimiter(options => options.AddPolicy(LimiterPolicy, context =>
            context.User.FindFirst(TokenIdClaim)?.Value is { } id
                ? RateLimitPartition.GetFixedWindowLimiter(id, _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                : RateLimitPartition.GetNoLimiter("cookie")));
    }

    public static bool IsToken(ClaimsPrincipal user) => user.Identity?.AuthenticationType == Scheme;

    private static bool HasBearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase) && (header.Length == 6 || header[6] == ' ');
    }

    /// <summary>Токену: чтение (<c>GET</c>, <c>HEAD</c>) требует <c>tasks:read</c>, остальное — <c>tasks:write</c>. Вошедшему по cookie хватает входа.</summary>
    private static bool HasScope(AuthorizationHandlerContext context)
    {
        if (!IsToken(context.User)) return true;
        var method = (context.Resource as HttpContext)?.Request.Method ?? "";
        var needed = HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
            ? TokenEndpoints.ReadScope : TokenEndpoints.WriteScope;
        return context.User.HasClaim(ScopeClaim, needed);
    }
}
