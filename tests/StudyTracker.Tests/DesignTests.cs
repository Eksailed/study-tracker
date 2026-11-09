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
    public async Task ThemeColorIsAccent()
    {
        await using var factory = new TrackerFactory();
        using var http = factory.CreateClient();

        var index = await http.GetStringAsync("/");
        var manifest = await http.GetStringAsync("/manifest.webmanifest");

        Assert.Contains("<meta name=\"theme-color\" content=\"#6c47ff\">", index);
        Assert.Contains("\"theme_color\": \"#6c47ff\"", manifest);
    }
}
