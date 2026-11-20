using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Plugin.SubtitleAutoAlign.Configuration;
using Jellyfin.Plugin.SubtitleAutoAlign.Services;
using Xunit;

namespace Jellyfin.Plugin.SubtitleAutoAlign.Tests;

public class PreAnalysisTests
{
    [Fact]
    public async Task Queue_HighBeforeLow_WhenBothQueued()
    {
        var queue = new PriorityWorkQueue<string, string>();
        queue.TryEnqueueLow("analyse");
        queue.TryEnqueueHigh("align");

        var first = await queue.DequeueAsync(CancellationToken.None);
        var second = await queue.DequeueAsync(CancellationToken.None);

        Assert.Equal("align", first.High);
        Assert.Equal("analyse", second.Low);
    }

    [Fact]
    public async Task Queue_LowServed_WhenNoHigh()
    {
        var queue = new PriorityWorkQueue<string, string>();
        queue.TryEnqueueLow("analyse");

        var item = await queue.DequeueAsync(CancellationToken.None);

        Assert.Null(item.High);
        Assert.Equal("analyse", item.Low);
    }

    [Fact]
    public async Task Queue_DequeueWaits_UntilItemArrives()
    {
        var queue = new PriorityWorkQueue<string, string>();
        var pending = queue.DequeueAsync(CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);

        queue.TryEnqueueHigh("align");

        var item = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("align", item.High);
    }

    [Fact]
    public async Task Queue_DuplicateLowItem_RejectedUntilTaken()
    {
        var queue = new PriorityWorkQueue<string, string>(low => low);

        Assert.True(queue.TryEnqueueLow("/m/a.mkv"));
        Assert.False(queue.TryEnqueueLow("/m/a.mkv"));
        await queue.DequeueAsync(CancellationToken.None);

        Assert.True(queue.TryEnqueueLow("/m/a.mkv"));
    }

    [Fact]
    public void IsInSelectedLibrary_NoneSelected_False()
    {
        Assert.False(AutoAlignScope.IsInSelectedLibrary([Guid.NewGuid()], []));
    }

    [Fact]
    public void IsInSelectedLibrary_Null_False()
    {
        Assert.False(AutoAlignScope.IsInSelectedLibrary([Guid.NewGuid()], null));
    }

    [Fact]
    public void IsInSelectedLibrary_MatchInAnyFormat_True()
    {
        var library = Guid.NewGuid();

        Assert.True(AutoAlignScope.IsInSelectedLibrary([Guid.NewGuid(), library], [library.ToString("D").ToUpperInvariant()]));
    }

    [Fact]
    public void Config_XmlFrom020_PreAnalyseLibraryIdsEmpty()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <EnableAutoAlign>true</EnableAutoAlign>
              <TimeoutSeconds>300</TimeoutSeconds>
              <DisabledLibraryIds />
            </PluginConfiguration>
            """;

        var config = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(new StringReader(xml))!;

        Assert.Empty(config.PreAnalyseLibraryIds);
    }
}
