using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.SubtitleAutoAlign.Tests;

public class ConfigPageTests
{
    private static string ReadConfigPage()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(
            "Jellyfin.Plugin.SubtitleAutoAlign.Configuration.configPage.html");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// True when every script tag sits between the data-role="page" div and its own closing tag.
    /// </summary>
    private static bool ScriptsAreInsidePage(string html)
    {
        var pageStart = html.IndexOf("data-role=\"page\"", StringComparison.Ordinal);
        if (pageStart < 0)
        {
            return false;
        }

        var openTag = html.LastIndexOf("<div", pageStart, StringComparison.Ordinal);
        var depth = 0;
        var pageEnd = -1;
        foreach (Match tag in Regex.Matches(html[openTag..], "<div\\b|</div>"))
        {
            depth += tag.Value == "</div>" ? -1 : 1;
            if (depth == 0)
            {
                pageEnd = openTag + tag.Index;
                break;
            }
        }

        var scripts = Regex.Matches(html, "<script\\b");
        return pageEnd > 0 && scripts.Count > 0
            && scripts.All(s => s.Index > openTag && s.Index < pageEnd);
    }

    [Fact]
    public void Script_IsInsideThePageElement()
    {
        // Jellyfin's web client keeps only the data-role="page" element; a script
        // outside it never runs, so settings never load and Save does nothing.
        Assert.True(ScriptsAreInsidePage(ReadConfigPage()));
    }

    [Fact]
    public void ScriptInATrailingSiblingDiv_IsDetected()
    {
        const string html = """<div data-role="page"><div>form</div></div><div><script>x</script></div>""";

        Assert.False(ScriptsAreInsidePage(html));
    }

    [Fact]
    public void UpstreamPageWithScriptAfterThePage_IsDetected()
    {
        // The 0.1.5 page shape: the script follows the page div's closing tag.
        const string html = """<div data-role="page"><div><form></form></div></div><script>x</script>""";

        Assert.False(ScriptsAreInsidePage(html));
    }
}
