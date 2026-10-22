using System.Text.Json;
using PeerSync.Cli;
using PeerSync.Control.Contracts;
using PeerSync.Core.Sync;
using Shouldly;
using Xunit;

namespace PeerSync.Cli.Tests;

public sealed class StatusCommandTests
{
    [Fact]
    public async Task HostsCommandRequiresEndpointsOrAnExplicitClear()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        int missingEndpoints = await FolderCommand.RunAsync("hosts", ["C:\\PeerSync-Test\\Share"], output, error,
            TestContext.Current.CancellationToken);
        missingEndpoints.ShouldBe(2);
        error.ToString().ShouldContain("psp hosts <folder>");

        error.GetStringBuilder().Clear();
        int mixedClear = await FolderCommand.RunAsync("hosts",
            ["C:\\PeerSync-Test\\Share", "--clear", "peer.example:47821"], output, error,
            TestContext.Current.CancellationToken);
        mixedClear.ShouldBe(2);
        error.ToString().ShouldContain("--clear");
    }

    [Fact]
    public void JsonStatusKeepsFalseAndZeroAndIncludesPeerClockDetails()
    {
        Guid instance = Guid.NewGuid(), session = Guid.NewGuid();
        byte[] folder = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        byte[] device = Enumerable.Range(32, 32).Select(value => (byte)value).ToArray();
        var snapshot = new ControlStatusSnapshot(instance, 3, false,
            [new(folder, new PeerSessionClockStatus(session, device, new(PeerClockState.Accepted, 0), true))], false);

        using JsonDocument json = JsonDocument.Parse(StatusCommand.ToJson(snapshot));
        json.RootElement.GetProperty("productionSyncAvailable").GetBoolean().ShouldBeFalse();
        var peer = json.RootElement.GetProperty("peers")[0];
        peer.GetProperty("offsetTicks").GetInt64().ShouldBe(0);
        peer.GetProperty("offsetMinutes").GetDouble().ShouldBe(0);
        peer.GetProperty("clockState").GetString().ShouldBe("Accepted");
        peer.GetProperty("deviceId").GetString().ShouldBe(Convert.ToHexString(device));
        peer.TryGetProperty("folderFingerprint", out _).ShouldBeTrue();
    }

    [Fact]
    public void HumanStatusNamesUnavailableServiceAndClockSkew()
    {
        var snapshot = new ControlStatusSnapshot(Guid.NewGuid(), 1, false,
            [new(new byte[32], new(Guid.NewGuid(), new byte[32],
                new(PeerClockState.Skewed, 11 * TimeSpan.TicksPerMinute), false))], false);

        string output = StatusCommand.ToHuman(snapshot);
        output.ShouldContain("Production sync: unavailable");
        output.ShouldContain("clock differs by +11.00 min");
    }
}
