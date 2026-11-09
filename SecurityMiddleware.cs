using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using StudyTracker.ApiTokens;

namespace StudyTracker;

public static class SecurityMiddleware
{
    /// <summary>Отдаёт <c>X-Account-Id</c> вошедшего пользователя.</summary>
    public static IApplicationBuilder UseAccountHeader(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.User.Identity?.IsAuthenticated == true)
            context.Response.Headers["X-Account-Id"] = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        await next();
    });

    /// <summary>Защитные заголовки (CSP, <c>nosniff</c>, <c>no-store</c> для <c>/api</c>) и проверка CSRF для изменяющих запросов.</summary>
    public static IApplicationBuilder UseTrackerSecurity(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
            "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.Headers.CacheControl = "no-store";
            // CSRF угрожает только запросам с cookie: токен браузер сам не подставляет.
            if (!ApiTokenAuth.IsToken(context.User) && (HttpMethods.IsPost(context.Request.Method)
                || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method)))
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
}
