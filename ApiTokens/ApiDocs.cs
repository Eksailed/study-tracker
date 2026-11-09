using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace StudyTracker.ApiTokens;

/// <summary>Описание <c>/api/v1</c> для сторонних приложений: <c>/api/v1/openapi.json</c> и страница <c>/api/docs</c>.</summary>
public static class ApiDocs
{
    private const string SchemeName = "Bearer";

    public static void AddApiDocs(this IServiceCollection services) =>
        services.AddOpenApi("v1", options =>
        {
            // Только группа /api/v1: внутренние адреса интерфейса (/api/auth, /api/tokens и т. д.) в документ не попадают.
            options.ShouldInclude = description => description.RelativePath?.StartsWith("api/v1/", StringComparison.Ordinal) == true;
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Зачёт — API";
                document.Info.Version = "v1";
                document.Info.Description = "Предметы и задания. Токен выпускается в разделе «Доступ к API» и передаётся "
                    + "в заголовке `Authorization: Bearer zt_…`. Запрос чтения требует область `tasks:read`, "
                    + "остальные — `tasks:write`. Не больше 120 запросов в минуту на токен.";
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    Description = "Токен доступа вида zt_…, выпущенный в интерфейсе.",
                };
                // Одни и те же типы служат и интерфейсу, где группы есть, и токену, где их нет.
                foreach (var schema in document.Components.Schemas?.Values.OfType<OpenApiSchema>() ?? [])
                    if (schema.Properties?.TryGetValue("groupId", out var groupId) == true && groupId is OpenApiSchema property)
                        property.Description = "Группы в токене API не поддерживаются: в запросе поле запрещено (ответ `400`), в ответе всегда `null`.";
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, context, _) =>
            {
                var path = context.Description.RelativePath ?? "";
                var method = context.Description.HttpMethod ?? "";
                operation.Security =
                [
                    new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(SchemeName, context.Document)] = [] },
                ];
                operation.Responses ??= new OpenApiResponses();
                foreach (var (code, text) in ErrorCodes(path, method))
                    operation.Responses.TryAdd(code, new OpenApiResponse
                    {
                        Description = text,
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new() { Schema = ErrorBody },
                        },
                    });
                return Task.CompletedTask;
            });
        });

    /// <summary>Тело ошибки: <c>{"error": "…"}</c>.</summary>
    private static OpenApiSchema ErrorBody => new()
    {
        Type = JsonSchemaType.Object,
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["error"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Что пошло не так, по-русски." },
        },
    };

    /// <summary>Только коды, которые адрес действительно отдаёт, по возрастанию.</summary>
    private static IEnumerable<(string Code, string Text)> ErrorCodes(string path, string method)
    {
        var writes = method is "POST" or "PUT";
        var byId = path.Contains('{');
        var subjects = path.Contains("/subjects");
        if (writes) yield return ("400", "Неверные данные.");
        yield return ("401", "Нет токена, он неверный, просрочен или отозван.");
        yield return ("403", "Токену не хватает области `tasks:read` или `tasks:write`.");
        if (byId || (path.EndsWith("/assignments") && writes)) yield return ("404", "Не найдено: нет такого элемента или он чужой.");
        if (subjects && (writes || method == "DELETE")) yield return ("409", "Конфликт: такой предмет уже есть или у него остались задания.");
        yield return ("429", "Больше 120 запросов в минуту на токен; смотрите заголовок `Retry-After`.");
    }

    public static void MapApiDocs(this WebApplication app)
    {
        app.MapOpenApi("/api/v1/openapi.json").AllowAnonymous();
        app.MapGet("/api/docs", (IWebHostEnvironment environment) =>
            Results.File(environment.WebRootFileProvider.GetFileInfo("api-docs.html").CreateReadStream(), "text/html; charset=utf-8"))
            .AllowAnonymous();
    }
}
