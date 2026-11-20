using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SubtitleAutoAlign.Services;
using Xunit;

namespace Jellyfin.Plugin.SubtitleAutoAlign.Tests;

public class FirstSightFilterTests
{
    private static readonly DateTime Started = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void KeepsOnlySubtitlesWrittenSinceTheWatcherStarted()
    {
        var writes = new Dictionary<string, DateTime>
        {
            ["/m/old.en.srt"] = Started.AddDays(-30),
            ["/m/new.en.srt"] = Started.AddMinutes(5),
        };

        var result = SubtitleChangeDetector.WrittenSince(writes.Keys, Started, path => writes[path]);

        Assert.Equal(["/m/new.en.srt"], result);
    }

    [Fact]
    public void UnreadableFile_IsSkipped()
    {
        var result = SubtitleChangeDetector.WrittenSince(
            ["/m/gone.en.srt"],
            Started,
            _ => throw new System.IO.FileNotFoundException());

        Assert.Empty(result);
    }
}
