using System.Text.Json;
using System.Text.RegularExpressions;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class OpenApiTests
{
    private static async Task<JsonElement> Document(ApiClient client)
    {
        var response = await client.Get("v1/openapi.json");
        Assert.Equal(200, response.Status);
        return response.Body;
    }

    private static string[] Paths(JsonElement document) =>
        document.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public async Task OpenApiIsPublicAndListsV1()
    {
        await using var factory = new TrackerFactory();
        var paths = Paths(await Document(factory.NewClient()));

        Assert.Contains("/api/v1/subjects", paths);
        Assert.Contains("/api/v1/subjects/{id}", paths);
        Assert.Contains("/api/v1/assignments", paths);
        Assert.Contains("/api/v1/assignments/{id}", paths);
    }

    [Fact]
    public async Task OpenApiHasBearerScheme()
    {
        await using var factory = new TrackerFactory();
        var document = await Document(factory.NewClient());

        var scheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
    }

    [Fact]
    public async Task OpenApiHidesInternalPaths()
    {
        await using var factory = new TrackerFactory();
        var paths = Paths(await Document(factory.NewClient()));

        Assert.NotEmpty(paths);
        Assert.All(paths, path => Assert.StartsWith("/api/v1/", path));
        Assert.DoesNotContain(paths, p => p.StartsWith("/api/auth") || p.StartsWith("/api/notifications")
            || p.StartsWith("/api/tokens") || p.StartsWith("/api/token") || p == "/api/subjects" || p == "/api/assignments"
            || p.StartsWith("/api/subjects/") || p.StartsWith("/api/assignments/"));
    }

    [Fact]
    public async Task OpenApiDescribesErrors()
    {
        await using var factory = new TrackerFactory();
        var paths = (await Document(factory.NewClient())).GetProperty("paths");
        string[] Codes(string path, string method) =>
            paths.GetProperty(path).GetProperty(method).GetProperty("responses").EnumerateObject().Select(r => r.Name).ToArray();

        // Только коды, которые адрес действительно отдаёт.
        Assert.Equal(new[] { "400", "401", "403", "404", "429" }, Codes("/api/v1/assignments", "post").Where(c => c[0] != '2'));
        Assert.Equal(new[] { "400", "401", "403", "409", "429" }, Codes("/api/v1/subjects", "post").Where(c => c[0] != '2'));
        Assert.Equal(new[] { "401", "403", "404", "409", "429" }, Codes("/api/v1/subjects/{id}", "delete").Where(c => c[0] != '2'));
        Assert.Equal(new[] { "401", "403", "429" }, Codes("/api/v1/subjects", "get").Where(c => c[0] != '2'));

        var body = paths.GetProperty("/api/v1/assignments").GetProperty("post").GetProperty("responses").GetProperty("400").GetRawText();
        Assert.Contains("\"error\"", body);
    }

    [Fact]
    public async Task OpenApiDescribesPriorityAndSubtasks()
    {
        await using var factory = new TrackerFactory();
        var document = await Document(factory.NewClient());

        var assignment = document.GetProperty("components").GetProperty("schemas").GetProperty("Assignment").GetProperty("properties");
        Assert.True(assignment.TryGetProperty("priority", out _), "В схеме задания нет priority");
        Assert.True(assignment.TryGetProperty("subtasks", out var subtasks), "В схеме задания нет subtasks");
        Assert.Equal("array", subtasks.GetProperty("type").GetString());
        var text = document.GetRawText();
        foreach (var value in new[] { "Normal", "Important", "Urgent" }) Assert.Contains($"\"{value}\"", text);
        Assert.DoesNotContain("assignmentId", text);
        Assert.DoesNotContain("/subtasks", text);
    }

    [Fact]
    public async Task OpenApiSaysGroupsAreNotSupported()
    {
        await using var factory = new TrackerFactory();
        var document = await Document(factory.NewClient());

        var schemas = document.GetProperty("components").GetProperty("schemas");
        var described = 0;
        foreach (var schema in schemas.EnumerateObject())
        {
            if (!schema.Value.TryGetProperty("properties", out var properties) || !properties.TryGetProperty("groupId", out var groupId)) continue;
            Assert.Contains("не поддерживаются", groupId.GetProperty("description").GetString());
            described++;
        }
        Assert.True(described >= 2, "groupId должен быть описан в схемах предмета и задания");
        Assert.DoesNotContain("/progress", document.GetRawText());
    }

    [Fact]
    public async Task DocsPageIsPublicAndStrict()
    {
        await using var factory = new TrackerFactory();
        var page = await factory.NewClient().Page("/api/docs");

        Assert.Equal(200, page.Status);
        Assert.Contains("api-docs.js", page.Text);
        Assert.DoesNotMatch(new Regex(@"<script[^>]+src=""https?://", RegexOptions.IgnoreCase), page.Text);
        Assert.DoesNotMatch(new Regex(@"\son\w+\s*=", RegexOptions.IgnoreCase), page.Text);
        Assert.DoesNotContain("<script>", page.Text);
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task DocsScriptIsServed()
    {
        await using var factory = new TrackerFactory();
        var script = await factory.NewClient().Page("/api-docs.js");

        Assert.Equal(200, script.Status);
        Assert.DoesNotContain("eval(", script.Text);
        Assert.DoesNotContain("innerHTML", script.Text);
    }
}
