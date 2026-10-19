using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using PeerSync.Core.Settings;
using Shouldly;
using Xunit;

namespace PeerSync.Core.Tests;

public sealed class FolderSettingsTests
{
    [Fact]
    public void SerializedSettingsCannotExceedTheReadersLimitEvenWhenEachHostIsValid()
    {
        string host = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61)) + ":47821";
        var settings = new FolderSettings { PredefinedHosts = Enumerable.Repeat(host, 256).ToArray() };
        settings.Validate();
        Should.Throw<ValidationException>(() => settings.ToJson());
        var fitting = settings with { PredefinedHosts = Enumerable.Repeat(host, 200).ToArray() };
        FolderSettings.FromJson(fitting.ToJson()).ToJson().ShouldBe(fitting.ToJson());
    }

    [Fact]
    public void MissingPropertiesUseThePlansDefaults()
    {
        var settings = FolderSettings.FromJson("{}");
        settings.RescanIntervalSeconds.ShouldBe(600);
        settings.UseLan.ShouldBeTrue(); settings.UseTracker.ShouldBeTrue(); settings.UseRelay.ShouldBeTrue();
        settings.ArchiveDays.ShouldBe(30); settings.ArchiveMaxFileBytes.ShouldBe(1000L * 1024 * 1024);
        settings.ArchiveMaxPercentOfVolume.ShouldBe(10); settings.Priority.ShouldBe(TransferPriority.Default);
        settings.KeepBothCopies.ShouldBeFalse(); settings.OverwriteLocalChanges.ShouldBeFalse();
    }

    [Theory]
    [InlineData(TransferPriority.Default)]
    [InlineData(TransferPriority.SmallerFirst)]
    [InlineData(TransferPriority.LargerFirst)]
    [InlineData(TransferPriority.OlderFirst)]
    [InlineData(TransferPriority.NewerFirst)]
    public void ConfiguredFieldsAndHostEndpointsRoundTrip(TransferPriority priority)
    {
        var settings = new FolderSettings
        {
            RescanIntervalSeconds = 90, UseLan = false, UseTracker = false, UseRelay = false,
            PredefinedHosts = ["example.test:47821", "192.0.2.1:1234", "[2001:db8::1]:47821"],
            RequireApproval = true, KeepBothCopies = true, OverwriteLocalChanges = true,
            ArchiveDays = 7, ArchiveMaxFileBytes = 0, ArchiveMaxPercentOfVolume = 20, Priority = priority,
        };
        var copy = FolderSettings.FromJson(settings.ToJson());
        copy.ToJson().ShouldBe(settings.ToJson()); copy.PredefinedHosts.ShouldBe(settings.PredefinedHosts);
    }

    [Theory]
    [InlineData("{\"folderKey\":\"not permitted\"}")]
    [InlineData("{\"priority\":99}")]
    public void UnknownPropertiesAndNumericPrioritiesAreRejected(string json) =>
        Should.Throw<JsonException>(() => FolderSettings.FromJson(json));

    [Theory]
    [InlineData("{\"useLan\":true,\"UseLan\":false}")]
    [InlineData("{\"rescanIntervalSeconds\":0}")]
    [InlineData("{\"archiveDays\":0}")]
    [InlineData("{\"archiveMaxPercentOfVolume\":101}")]
    [InlineData("{\"predefinedHosts\":[\"user:password@example.test:1234\"]}")]
    [InlineData("{\"predefinedHosts\":[\"example.test\"]}")]
    [InlineData("{\"predefinedHosts\":[\"example.test:1234/path\"]}")]
    [InlineData("[]")]
    public void InvalidRangesDuplicateNamesAndEndpointsAreRejected(string json) =>
        Should.Throw<ValidationException>(() => FolderSettings.FromJson(json));
}
