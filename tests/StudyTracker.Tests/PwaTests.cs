using System.Text.Json;
using System.Text.RegularExpressions;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public partial class PwaTests
{
    [GeneratedRegex(@"const (SHELL|SKIP) = \[(?<items>[^\]]*)\]")]
    private static partial Regex ListPattern();

    [GeneratedRegex(@"'([^']+)'")]
    private static partial Regex QuotedPattern();

    [GeneratedRegex("(?:src|href)=\"(?<path>/[^\"]*)\"")]
    private static partial Regex AssetPattern();

    private static List<string> ListFrom(string worker, string name)
    {
        var match = ListPattern().Matches(worker).Single(m => m.Groups[1].Value == name);
        return QuotedPattern().Matches(match.Groups["items"].Value).Select(m => m.Groups[1].Value).ToList();
    }

    [Fact]
    public async Task ManifestIsServedAsJson()
    {
        await using var factory = new TrackerFactory();
        using var http = factory.CreateClient();

        using var response = await http.GetAsync("/manifest.webmanifest");
        var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Contains("manifest+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("Зачёт", manifest.GetProperty("name").GetString());
        Assert.Equal("ru", manifest.GetProperty("lang").GetString());
        Assert.Equal("/", manifest.GetProperty("start_url").GetString());
        Assert.Equal("standalone", manifest.GetProperty("display").GetString());
        var icons = manifest.GetProperty("icons").EnumerateArray().ToList();
        foreach (var size in new[] { "192x192", "512x512" })
        {
            var icon = Assert.Single(icons, i => i.GetProperty("sizes").GetString() == size);
            using var image = await http.GetAsync(icon.GetProperty("src").GetString());
            Assert.Equal(200, (int)image.StatusCode);
            Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        }
    }

    [Fact]
    public async Task ServiceWorkerNeverTouchesData()
    {
        await using var factory = new TrackerFactory();
        using var http = factory.CreateClient();

        using var response = await http.GetAsync("/sw.js");
        var worker = await response.Content.ReadAsStringAsync();

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Contains("javascript", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(["/api", "/health", "/calendar.ics"], ListFrom(worker, "SKIP"));
        Assert.Contains("request.method !== 'GET'", worker);
        Assert.Contains("response.ok", worker);
    }

    [Fact]
    public async Task ShellFilesAreServed()
    {
        await using var factory = new TrackerFactory();
        using var http = factory.CreateClient();
        var worker = await http.GetStringAsync("/sw.js");

        var shell = ListFrom(worker, "SHELL");

        Assert.NotEmpty(shell);
        foreach (var path in shell)
        {
            using var response = await http.GetAsync(path);
            Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task ShellCoversIndexAssets()
    {
        await using var factory = new TrackerFactory();
        using var http = factory.CreateClient();
        var shell = ListFrom(await http.GetStringAsync("/sw.js"), "SHELL");
        var index = await http.GetStringAsync("/");

        var assets = AssetPattern().Matches(index).Select(m => m.Groups["path"].Value)
            .Where(p => p.EndsWith(".js") || p.EndsWith(".css")).Distinct().ToList();

        Assert.Contains("/", shell);
        Assert.NotEmpty(assets);
        foreach (var asset in assets)
            Assert.True(shell.Contains(asset), $"{asset} есть в index.html, но нет в SHELL файла sw.js");
    }

    [Fact]
    public async Task IndexLinksTheApp()
    {
        await using var factory = new TrackerFactory();
        using var http = factory.CreateClient();

        var index = await http.GetStringAsync("/");

        Assert.Contains("rel=\"manifest\" href=\"/manifest.webmanifest\"", index);
        Assert.Contains("rel=\"apple-touch-icon\" href=\"/apple-touch-icon.png\"", index);
        Assert.Contains("name=\"theme-color\"", index);
        Assert.Contains("src=\"/pwa.js\"", index);
    }
}
