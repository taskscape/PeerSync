using PeerSync.Core.Diagnostics;
using PeerSync.Service.Control;
using Shouldly;
using Xunit;

namespace PeerSync.Service.Tests;

public sealed class ControlStatusAlertTests
{
    [Fact]
    public void TransientNetworkWarningsStayInJournalButDoNotBecomeUserErrors()
    {
        string logs = Path.GetFullPath(Path.Combine("artifacts", "tests", "control-alerts",
            Guid.NewGuid().ToString("N")));
        using var journal = new DiagnosticJournal(logs, "service", debugEnabled: true);
        var status = new ControlStatusRuntime(journal);

        journal.Write(new DiagnosticEvent("connection.candidate_failed", "peer_network")
        { Level = DiagnosticLevel.Warning, ErrorCode = "SocketException" });
        status.ReadRecentErrors().ShouldBeEmpty();

        journal.Write(new DiagnosticEvent("service.folder_scan_failed", "folder_scan")
        { Level = DiagnosticLevel.Error, ErrorCode = "scan_failed" });
        var visible = status.ReadRecentErrors().Single();
        visible.Code.ShouldBe("scan_failed");
        visible.Source.ShouldBe("folder_scan");
        File.ReadAllText(Path.Combine(logs, "debug.jsonl")).ShouldContain("connection.candidate_failed");
    }
}
