using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleAutoAlign.Configuration;
using Jellyfin.Plugin.SubtitleAutoAlign.Models;
using Jellyfin.Plugin.SubtitleAutoAlign.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SubtitleAutoAlign.Tests;

public sealed class SpeechCacheAlignmentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("saa-cache-align-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CacheDir => Path.Combine(_root, "cache");

    private string Video
    {
        get
        {
            var path = Path.Combine(_root, "Movie.mkv");
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "video");
            }

            return path;
        }
    }

    private string Subtitle
    {
        get
        {
            var path = Path.Combine(_root, "Movie.en.srt");
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nhi\n");
            }

            return path;
        }
    }

    private (SubtitleAlignmentService Service, FakeProcessRunner Runner, SpeechCache Cache) Create(PluginConfiguration? configuration = null)
    {
        configuration ??= new PluginConfiguration();
        var runner = new FakeProcessRunner
        {
            // Simulate ffsubsync writing <reference>.npz when asked to serialize.
            OnRun = args =>
            {
                if (args.Contains("--serialize-speech"))
                {
                    File.WriteAllText(SpeechCache.ProducedPath(args[0]), "analysis");
                }
            },
        };
        var cache = new SpeechCache(CacheDir);
        var service = new SubtitleAlignmentService(
            runner,
            new FfSubSyncLocator(_root),
            () => configuration,
            () => null,
            NullLogger<SubtitleAlignmentService>.Instance,
            cache);
        return (service, runner, cache);
    }

    private AlignmentRequest Request() => new(Guid.NewGuid(), Video, Subtitle);

    [Fact]
    public void Build_SerializeSpeech_AddsFlag()
    {
        var args = FfSubSyncArgumentBuilder.Build("v.mkv", "s.srt", "o.srt", serializeSpeech: true);

        Assert.Contains("--serialize-speech", args);
    }

    [Fact]
    public void Build_NoSubtitle_OmitsInputAndOutput()
    {
        var args = FfSubSyncArgumentBuilder.Build("v.mkv", null, null, serializeSpeech: true);

        Assert.Equal(["v.mkv", "--serialize-speech"], args);
    }

    [Fact]
    public async Task Align_NoCacheEntry_SerializesViaWorkLinkAndStores()
    {
        var (service, runner, cache) = Create();

        var result = await service.AlignAsync(Request(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("--serialize-speech", runner.LastArguments!);
        Assert.NotEqual(Video, runner.LastArguments![0]);
        Assert.StartsWith(CacheDir, runner.LastArguments[0], StringComparison.Ordinal);
        Assert.NotNull(cache.TryGet(Video, null));
        Assert.Empty(Directory.GetDirectories(Path.Combine(CacheDir, "work")));
    }

    [Fact]
    public async Task Align_CacheEntry_UsesItAsReference()
    {
        var (service, runner, cache) = Create();
        var produced = Path.Combine(_root, "x.npz");
        File.WriteAllText(produced, "analysis");
        cache.Store(Video, null, produced, cache.EntryPath(Video, null)!);

        await service.AlignAsync(Request(), CancellationToken.None);

        Assert.Equal(cache.TryGet(Video, null), runner.LastArguments![0]);
        Assert.DoesNotContain("--serialize-speech", runner.LastArguments);
    }

    [Fact]
    public async Task Align_Failure_StoresNothingAndCleansUp()
    {
        var (service, runner, cache) = Create();
        runner.ResultToReturn = new ProcessRunResult(1, string.Empty, "boom", false);

        var result = await service.AlignAsync(Request(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(cache.TryGet(Video, null));
        Assert.Empty(Directory.GetDirectories(Path.Combine(CacheDir, "work")));
    }

    [Fact]
    public async Task Align_WorkLinkUnavailable_FallsBackToRealVideo()
    {
        File.WriteAllText(CacheDir, "a file where the cache folder should be");
        var (service, runner, _) = Create();

        var result = await service.AlignAsync(Request(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(Video, runner.LastArguments![0]);
        Assert.DoesNotContain("--serialize-speech", runner.LastArguments);
    }

    [Fact]
    public async Task Align_CachedRunFails_RemovesEntryAndRetriesAgainstVideo()
    {
        var (service, runner, cache) = Create();
        var produced = Path.Combine(_root, "x.npz");
        File.WriteAllText(produced, "analysis");
        cache.Store(Video, null, produced, cache.EntryPath(Video, null)!);
        // First call (with the saved analysis) fails, the retry succeeds.
        runner.OnRun = _ => runner.ResultToReturn = runner.CallCount == 1
            ? new ProcessRunResult(1, string.Empty, "bad reference", false)
            : new ProcessRunResult(0, string.Empty, string.Empty, false);

        var result = await service.AlignAsync(Request(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, runner.CallCount);
        Assert.Equal(Video, runner.LastArguments![0]);
        Assert.DoesNotContain("--serialize-speech", runner.LastArguments);
        Assert.Null(cache.TryGet(Video, null));
    }

    [Fact]
    public async Task Align_UnsupportedOption_BypassesCache()
    {
        var (service, runner, _) = Create(new PluginConfiguration { ExtraArguments = "--pgs-ref-stream 0" });

        var result = await service.AlignAsync(Request(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, runner.CallCount);
        Assert.Equal(Video, runner.LastArguments![0]);
        Assert.DoesNotContain("--serialize-speech", runner.LastArguments);
        Assert.False(Directory.Exists(CacheDir));
    }

    [Fact]
    public async Task PreAnalyse_NoEntry_RunsWithoutSubtitleAndStores()
    {
        var (service, runner, cache) = Create();

        var cached = await service.PreAnalyseAsync(Video, CancellationToken.None);

        Assert.True(cached);
        Assert.Equal(1, runner.CallCount);
        Assert.DoesNotContain("-i", runner.LastArguments!);
        Assert.NotNull(cache.TryGet(Video, null));
    }

    [Fact]
    public async Task PreAnalyse_EntryExists_DoesNotRun()
    {
        var (service, runner, _) = Create();
        await service.PreAnalyseAsync(Video, CancellationToken.None);

        var cached = await service.PreAnalyseAsync(Video, CancellationToken.None);

        Assert.True(cached);
        Assert.Equal(1, runner.CallCount);
    }
}
