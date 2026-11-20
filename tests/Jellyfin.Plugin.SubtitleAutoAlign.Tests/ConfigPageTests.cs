using System.IO;
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

    [Fact]
    public void Script_IsInsideThePageElement()
    {
        // Jellyfin's web client keeps only the data-role="page" element; a script
        // outside it never runs, so settings never load and Save does nothing.
        var html = ReadConfigPage();

        var pageStart = html.IndexOf("data-role=\"page\"", System.StringComparison.Ordinal);
        var scriptStart = html.LastIndexOf("<script", System.StringComparison.Ordinal);
        var pageEnd = html.LastIndexOf("</div>", System.StringComparison.Ordinal);

        Assert.True(pageStart >= 0, "page element missing");
        Assert.True(scriptStart > pageStart && scriptStart < pageEnd, "script must be inside the page element");
    }
}
