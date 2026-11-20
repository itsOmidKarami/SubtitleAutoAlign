using System;
using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.SubtitleAutoAlign.Configuration;
using Jellyfin.Plugin.SubtitleAutoAlign.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.SubtitleAutoAlign.Tests;

public class AutoAlignScopeTests
{
    [Fact]
    public void IsWatchedType_Movie_True() => Assert.True(AutoAlignScope.IsWatchedType(new Movie()));

    [Fact]
    public void IsWatchedType_Episode_True() => Assert.True(AutoAlignScope.IsWatchedType(new Episode()));

    [Fact]
    public void IsWatchedType_Trailer_False() => Assert.False(AutoAlignScope.IsWatchedType(new Trailer()));

    [Fact]
    public void IsInEnabledLibrary_NoDisabled_True()
    {
        Assert.True(AutoAlignScope.IsInEnabledLibrary([Guid.NewGuid()], []));
    }

    [Fact]
    public void IsInEnabledLibrary_LibraryDisabled_False()
    {
        var library = Guid.NewGuid();

        Assert.False(AutoAlignScope.IsInEnabledLibrary([library], [library.ToString("N")]));
    }

    [Fact]
    public void IsInEnabledLibrary_DashedAndUndashedIdsMatch_False()
    {
        var library = Guid.NewGuid();

        Assert.False(AutoAlignScope.IsInEnabledLibrary([library], [library.ToString("D").ToUpperInvariant()]));
    }

    [Fact]
    public void IsInEnabledLibrary_NoLibraries_True()
    {
        Assert.True(AutoAlignScope.IsInEnabledLibrary([], [Guid.NewGuid().ToString("N")]));
    }

    [Fact]
    public void IsInEnabledLibrary_StaleOrGarbageDisabledIds_Ignored_True()
    {
        Assert.True(AutoAlignScope.IsInEnabledLibrary([Guid.NewGuid()], [Guid.NewGuid().ToString("N"), "not-a-guid"]));
    }

    [Fact]
    public void Config_DefaultDisabledLibraryIds_IsEmpty()
    {
        Assert.Empty(new PluginConfiguration().DisabledLibraryIds);
    }

    [Fact]
    public void Config_XmlWithoutDisabledLibraryIds_DeserializesToEmpty()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <EnableAutoAlign>true</EnableAutoAlign>
              <FfSubSyncPath />
              <ExtraArguments />
              <TimeoutSeconds>300</TimeoutSeconds>
              <OverwriteExistingAlignedSubtitle>false</OverwriteExistingAlignedSubtitle>
            </PluginConfiguration>
            """;

        var config = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(new StringReader(xml))!;

        Assert.Empty(config.DisabledLibraryIds);
        Assert.Equal(300, config.TimeoutSeconds);
    }
}
