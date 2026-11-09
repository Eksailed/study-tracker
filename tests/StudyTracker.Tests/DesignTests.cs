using System.Text.RegularExpressions;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

/// <summary>Охранные проверки нового дизайна: политика стилей, id для скриптов, цвет темы. Сам вид проверяется вручную.</summary>
public partial class DesignTests
{
    [GeneratedRegex("<script[^>]*\\ssrc=\"(?<path>/[^\"]+\\.js)\"")]
    private static partial Regex ScriptPattern();

    [GeneratedRegex("\\sstyle\\s*=|setAttribute\\(\\s*['\"]style['\"]")]
    private static partial Regex InlineStylePattern();

    private static readonly string[] ScriptIds =
    [
        "open-group", "open-owner", "open-notifications", "open-tokens", "open-import", "open-calendar",
        "add-assignment", "add-subject", "logout", "account-email", "active-count", "subjects",
        "scope-filter", "assignment-list", "progress-circle"
    ];

    [Fact]
    public async Task PagesHaveNoInlineStyles()
    {
        await using var factory = new TrackerFactory();
        using var http = factory.CreateClient();
        var pages = new[] { "/", "/g.html" };

        foreach (var page in pages)
        {
            var html = await http.GetStringAsync(page);
            Assert.False(InlineStylePattern().IsMatch(html), $"{page}: найден style= в разметке");
            foreach (var script in ScriptPattern().Matches(html).Select(m => m.Groups["path"].Value))
            {
                var code = await http.GetStringAsync(script);
                var found = InlineStylePattern().Match(code);
                Assert.False(found.Success, $"{script}: инлайн-стиль «{found.Value.Trim()}» запрещён политикой style-src 'self'");
            }
        }
    }

    [Fact]
    public async Task IndexKeepsScriptIds()
    {
        await using var factory = new TrackerFactory();
        using var http = factory.CreateClient();

        var index = await http.GetStringAsync("/");

        foreach (var id in ScriptIds)
            Assert.True(index.Contains($"id=\"{id}\""), $"в index.html пропал id=\"{id}\", на него опираются скрипты");
    }

    [Fact]
    public async Task IndexHasTopNavigation()
    {
        await using var factory = new TrackerFactory();
        using var http = factory.CreateClient();

        var index = await http.GetStringAsync("/");

        Assert.Contains("class=\"main-nav\"", index);
        Assert.Contains("id=\"account-button\"", index);
        Assert.Contains("id=\"account-menu\"", index);
        Assert.Contains("src=\"/topnav.js\"", index);
        Assert.DoesNotContain("class=\"sidebar\"", index);
        Assert.DoesNotContain("id=\"breadcrumb\"", index);
    }

    [Fact]
    public async Task IndexHasFloatingAddButton()
    {
        await using var factory = new TrackerFactory();
        using var http = factory.CreateClient();

        var index = await http.GetStringAsync("/");

        Assert.Matches(@"<button[^>]*class=""[^""]*\bfab\b[^""]*""[^>]*id=""add-assignment""|<button[^>]*id=""add-assignment""[^>]*class=""[^""]*\bfab\b", index);
        Assert.Contains("id=\"group-card\"", index);
        Assert.Contains("id=\"group-card-open\"", index);
    }

    [Fact]
    public async Task AppScriptWritesRingThroughStyleObject()
    {
        await using var factory = new TrackerFactory();
        using var http = factory.CreateClient();

        var code = await http.GetStringAsync("/app.js");

        Assert.Contains("style.strokeDasharray", code);
        Assert.DoesNotContain("setAttribute('stroke-dasharray'", code);
    }

    [Fact]
    public async Task ThemeColorIsAccent()
    {
        await using var factory = new TrackerFactory();
        using var http = factory.CreateClient();

        var index = await http.GetStringAsync("/");
        var manifest = await http.GetStringAsync("/manifest.webmanifest");

        Assert.Contains("<meta name=\"theme-color\" content=\"#6c47ff\">", index);
        Assert.Contains("\"theme_color\": \"#6c47ff\"", manifest);
    }

    [Fact]
    public async Task EveryPageLoadsThemeScriptBeforeStyles()
    {
        await using var factory = new TrackerFactory();
        using var http = factory.CreateClient();

        foreach (var page in new[] { "/", "/g.html" })
        {
            var html = await http.GetStringAsync(page);
            var script = html.IndexOf("<script src=\"/theme.js\"></script>", StringComparison.Ordinal);
            var styles = html.IndexOf("<link rel=\"stylesheet\"", StringComparison.Ordinal);
            Assert.True(script >= 0, $"{page}: theme.js должен подключаться без defer, иначе страница мигнёт светлой темой");
            Assert.True(script < styles, $"{page}: theme.js должен идти до стилей");
        }
    }

    [Fact]
    public async Task IndexHasThemeToggle()
    {
        await using var factory = new TrackerFactory();
        using var http = factory.CreateClient();

        var index = await http.GetStringAsync("/");

        Assert.Contains("id=\"theme-toggle\"", index);
        Assert.Contains("href=\"#i-sun\"", index);
        Assert.Contains("href=\"#i-moon\"", index);
    }

    [Theory]
    [InlineData("/app.css")]
    [InlineData("/g.css")]
    public async Task StylesDefineDarkTheme(string path)
    {
        await using var factory = new TrackerFactory();
        using var http = factory.CreateClient();

        var css = await http.GetStringAsync(path);

        Assert.Contains("[data-theme=dark]", css);
        Assert.Contains("color-scheme:dark", css);
        Assert.DoesNotContain("background:#fff;", css);
        Assert.DoesNotContain("background:white", css);
    }

    [Fact]
    public async Task ThemeScriptStoresChoiceAndIsCached()
    {
        await using var factory = new TrackerFactory();
        using var http = factory.CreateClient();

        var code = await http.GetStringAsync("/theme.js");
        var worker = await http.GetStringAsync("/sw.js");

        Assert.Contains("localStorage", code);
        Assert.Contains("prefers-color-scheme", code);
        Assert.Contains("'/theme.js'", worker);
    }
}
