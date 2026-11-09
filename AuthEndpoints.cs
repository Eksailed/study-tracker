using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using StudyTracker.ApiTokens;

namespace StudyTracker;

public record Credentials(string? Email, string? Password);

public static class AuthEndpoints
{
    public static void AddTrackerAccounts(this IServiceCollection services, IWebHostEnvironment environment)
    {
        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 12;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        }).AddEntityFrameworkStores<TrackerDb>().AddDefaultTokenProviders();
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "Zachet.Account";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        services.AddApiTokenAuth();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.OnRejected = async (context, token) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                var message = context.HttpContext.User.FindFirst(ApiTokenAuth.TokenIdClaim) is null
                    ? "Слишком много попыток. Повторите через минуту."
                    : "Слишком много запросов с этим токеном. Повторите через минуту.";
                await context.HttpContext.Response.WriteAsJsonAsync(new { error = message }, token);
            };
            options.AddPolicy("accounts", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }

    private static bool RegistrationAllowed(IConfiguration config, IWebHostEnvironment environment) =>
        config.GetValue<bool?>("Accounts:AllowRegistration") ?? environment.IsDevelopment();

    public static void MapTrackerAccounts(this WebApplication app)
    {
        app.MapGet("/api/auth/options", (IConfiguration config, IWebHostEnvironment environment) =>
            Results.Ok(new { allowRegistration = RegistrationAllowed(config, environment) }));

        app.MapPost("/api/auth/register", async (Credentials input, UserManager<ApplicationUser> users,
            IConfiguration config, IWebHostEnvironment environment) =>
        {
            if (!RegistrationAllowed(config, environment))
                return Results.Json(new { error = "Регистрация закрыта." }, statusCode: 403);
            var email = input.Email?.Trim() ?? "";
            if (email.Length > 254 || !new EmailAddressAttribute().IsValid(email))
                return Results.BadRequest(new { error = "Введите корректный адрес электронной почты." });
            if (input.Password is null || input.Password.Length is < 12 or > 128)
                return Results.BadRequest(new { error = "Пароль должен содержать от 12 до 128 символов." });
            var user = new ApplicationUser { UserName = email, Email = email };
            IdentityResult result;
            try { result = await users.CreateAsync(user, input.Password); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "Не удалось зарегистрировать этот адрес." }); }
            if (!result.Succeeded)
                return Results.Conflict(new { error = "Не удалось зарегистрировать этот адрес. Проверьте данные или войдите." });
            return Results.Created("/api/auth/me", new { user.Id, user.Email });
        }).RequireRateLimiting("accounts");

        app.MapPost("/api/auth/login", async (Credentials input, UserManager<ApplicationUser> users,
            SignInManager<ApplicationUser> signIn) =>
        {
            if (input.Email?.Length > 254 || input.Password is null || input.Password.Length > 128)
                return Results.Json(new { error = "Неверная почта или пароль. После 5 ошибок вход блокируется на 15 минут." }, statusCode: 401);
            var user = await users.FindByEmailAsync(input.Email?.Trim() ?? "");
            var result = user is null ? SignInResult.Failed :
                await signIn.PasswordSignInAsync(user, input.Password, isPersistent: true, lockoutOnFailure: true);
            if (!result.Succeeded)
                return Results.Json(new { error = "Неверная почта или пароль. После 5 ошибок вход блокируется на 15 минут." }, statusCode: 401);
            return Results.Ok(new { user!.Id, user.Email });
        }).RequireRateLimiting("accounts");

        app.MapGet("/api/auth/me", async (System.Security.Claims.ClaimsPrincipal principal,
            UserManager<ApplicationUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            return user is null ? Results.Unauthorized() : Results.Ok(new { user.Id, user.Email });
        }).RequireAuthorization();

        app.MapPost("/api/auth/logout", async (SignInManager<ApplicationUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.NoContent();
        }).RequireAuthorization();
    }
}
