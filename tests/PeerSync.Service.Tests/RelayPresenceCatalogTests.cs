using PeerSync.Service.Hosting;
using Shouldly;
using Xunit;

namespace PeerSync.Service.Tests;

public sealed class RelayPresenceCatalogTests
{
    [Fact]
    public void RelayCandidateUsesOnlyTheServerThatAnnouncedTheSharedFolder()
    {
        var catalog = new RelayPresenceCatalog();
        var first = new Uri("https://first.example:99");
        var second = new Uri("https://second.example:99");
        byte[] folder = new byte[32];
        folder[0] = 7;
        string peer = new('A', 64);
        DateTimeOffset now = new(2026, 10, 22, 20, 0, 0, TimeSpan.Zero);
        catalog.Update(first, folder, new HashSet<string> { peer }, true, now);

        catalog.TryGetFolder(second, peer, _ => true, now, out _).ShouldBeFalse();
        catalog.TryGetFolder(first, peer, _ => false, now, out _).ShouldBeFalse();
        catalog.TryGetFolder(first, peer, _ => true, now, out byte[]? found).ShouldBeTrue();
        found.ShouldBe(folder);

        catalog.Update(first, folder, new HashSet<string>(), true, now);
        catalog.TryGetFolder(first, peer, _ => true, now, out _).ShouldBeFalse();
        catalog.Update(first, folder, new HashSet<string> { peer }, true, now);
        catalog.TryGetFolder(first, peer, _ => true, now.AddMinutes(15), out _).ShouldBeFalse();
        catalog.Update(first, folder, new HashSet<string> { peer }, false, now);
        catalog.TryGetFolder(first, peer, _ => true, now, out _).ShouldBeFalse();
    }
}
