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
    public async Task Queue_CompletedWhileWaiting_DequeueEnds()
    {
        var queue = new PriorityWorkQueue<string, string>();
        var pending = queue.DequeueAsync(CancellationToken.None).AsTask();
        await Task.Delay(50);

        queue.Complete();

        await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Queue_ManyLowItemsThenHigh_AllServedInOrder()
    {
        var queue = new PriorityWorkQueue<string, string>();
        for (var i = 0; i < 50; i++)
        {
            queue.TryEnqueueLow("low" + i);
            Assert.Equal("low" + i, (await queue.DequeueAsync(CancellationToken.None)).Low);
        }

        queue.TryEnqueueHigh("align");
        Assert.Equal("align", (await queue.DequeueAsync(CancellationToken.None)).High);
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
    public void IsAnalysableFile_RegularVideo_True()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mkv");
        File.WriteAllText(path, "video");
        try
        {
            Assert.True(AutoAlignScope.IsAnalysableFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsAnalysableFile_StrmFile_False()
    {
        // A .strm is a text file pointing at a stream; ffsubsync can't analyse it.
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".strm");
        File.WriteAllText(path, "http://example.invalid/stream");
        try
        {
            Assert.False(AutoAlignScope.IsAnalysableFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsAnalysableFile_DiscFolderOrMissing_False()
    {
        // DVD/Blu-ray movies have a folder as their path.
        Assert.False(AutoAlignScope.IsAnalysableFile(Path.GetTempPath()));
        Assert.False(AutoAlignScope.IsAnalysableFile(Path.Combine(Path.GetTempPath(), "missing.mkv")));
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
