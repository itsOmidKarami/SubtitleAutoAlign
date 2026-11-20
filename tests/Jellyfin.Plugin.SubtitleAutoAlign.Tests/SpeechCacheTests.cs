using System;
using System.IO;
using Jellyfin.Plugin.SubtitleAutoAlign.Services;
using Xunit;

namespace Jellyfin.Plugin.SubtitleAutoAlign.Tests;

public sealed class SpeechCacheTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("saa-cache-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CacheDir => Path.Combine(_root, "cache");

    private string Video(string content = "video")
    {
        var path = Path.Combine(_root, "Movie.mkv");
        File.WriteAllText(path, content);
        return path;
    }

    private string Npz(string content = "speech")
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".npz");
        File.WriteAllText(path, content);
        return path;
    }

    private static bool StoreNow(SpeechCache cache, string video, string npz, string? extra = null) =>
        cache.Store(video, extra, npz, cache.EntryPath(video, extra)!);

    [Fact]
    public void TryGet_NoEntry_Null()
    {
        Assert.Null(new SpeechCache(CacheDir).TryGet(Video(), null));
    }

    [Fact]
    public void Store_ThenTryGet_ReturnsEntry()
    {
        var cache = new SpeechCache(CacheDir);
        var video = Video();

        Assert.True(StoreNow(cache, video, Npz("analysis")));

        var entry = cache.TryGet(video, null);
        Assert.NotNull(entry);
        Assert.Equal("analysis", File.ReadAllText(entry));
        Assert.StartsWith(CacheDir, entry, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGet_VideoChangedAfterStore_Null()
    {
        var cache = new SpeechCache(CacheDir);
        var video = Video("short");
        StoreNow(cache, video, Npz());

        File.WriteAllText(video, "a different, longer video file");

        Assert.Null(cache.TryGet(video, null));
    }

    [Fact]
    public void Store_VideoChangedDuringRun_Discarded()
    {
        // A still-copying video: the key is taken before the run and the file grows meanwhile.
        var cache = new SpeechCache(CacheDir);
        var video = Video("partial");
        var expected = cache.EntryPath(video, null)!;
        var produced = Npz();
        File.WriteAllText(video, "partial plus the rest of the copy");

        Assert.False(cache.Store(video, null, produced, expected));

        Assert.Null(cache.TryGet(video, null));
        Assert.False(File.Exists(produced));
    }

    [Fact]
    public void TryGet_DifferentExtraArguments_Null()
    {
        var cache = new SpeechCache(CacheDir);
        var video = Video();
        StoreNow(cache, video, Npz(), "--vad webrtc");

        Assert.Null(cache.TryGet(video, "--vad auditok"));
        Assert.NotNull(cache.TryGet(video, "--vad webrtc"));
    }

    [Fact]
    public void Store_ReplacesOlderEntryForSameVideo()
    {
        var cache = new SpeechCache(CacheDir);
        var video = Video("v1");
        StoreNow(cache, video, Npz("old"));
        File.WriteAllText(video, "v2 longer");

        StoreNow(cache, video, Npz("new"));

        Assert.Single(Directory.GetFiles(CacheDir, "*.npz"));
        Assert.Equal("new", File.ReadAllText(cache.TryGet(video, null)!));
    }

    [Fact]
    public void Remove_DeletesEntries()
    {
        var cache = new SpeechCache(CacheDir);
        var video = Video();
        StoreNow(cache, video, Npz(), "--vad webrtc");
        StoreNow(cache, video, Npz());

        cache.Remove(video);

        Assert.Empty(Directory.GetFiles(CacheDir, "*.npz"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("--max-offset-seconds 60", true)]
    [InlineData("--pgs-ref-stream 0", false)]
    [InlineData("--whisper-weights base.bin", false)]
    public void CanCache_RejectsOptionsThatIgnoreASavedAnalysis(string? extra, bool expected)
    {
        Assert.Equal(expected, SpeechCache.CanCache(extra));
    }

    [Fact]
    public void CreateWorkLink_PointsAtVideo()
    {
        var cache = new SpeechCache(CacheDir);
        var video = Video();

        var link = cache.CreateWorkLink(video);

        Assert.NotNull(link);
        Assert.Equal(video, new FileInfo(link).LinkTarget);
        Assert.EndsWith("ref.npz", SpeechCache.ProducedPath(link), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateWorkLink_CacheDirIsAFile_Null()
    {
        File.WriteAllText(CacheDir, "not a directory");

        Assert.Null(new SpeechCache(CacheDir).CreateWorkLink(Video()));
    }

    [Fact]
    public void DeleteWorkLink_RemovesFolder()
    {
        var cache = new SpeechCache(CacheDir);
        var link = cache.CreateWorkLink(Video())!;

        cache.DeleteWorkLink(link);

        Assert.False(Directory.Exists(Path.GetDirectoryName(link)));
    }

    [Fact]
    public void DeleteStaleWorkFolders_RemovesLeftoversButKeepsEntries()
    {
        var cache = new SpeechCache(CacheDir);
        var video = Video();
        StoreNow(cache, video, Npz());
        cache.CreateWorkLink(video);

        cache.DeleteStaleWorkFolders();

        Assert.False(Directory.Exists(Path.Combine(CacheDir, "work")));
        Assert.NotNull(cache.TryGet(video, null));
    }
}
