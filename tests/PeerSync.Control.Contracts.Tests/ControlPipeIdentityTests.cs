using System.ComponentModel;
using System.IO.Pipes;
using System.Security;
using System.Security.Principal;
using Grpc.Core;
using Shouldly;
using Xunit;

namespace PeerSync.Control.Contracts.Tests;

public sealed class ControlPipeIdentityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Name => "PeerSync.Control.Test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task NativeOwnerAndServerProcessChecksPermitTheActualStream()
    {
        using var identity = WindowsIdentity.GetCurrent(); string name = Name;
        await using var server = Server(name); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        Task accepting = server.WaitForConnectionAsync(timeout.Token);
        await using var client = await ControlPipeConnector.ConnectVerifiedAsync(new(name, identity.User!, (uint)Environment.ProcessId), timeout.Token);
        await accepting; byte[] received = new byte[1]; var reading = server.ReadAsync(received, timeout.Token).AsTask();
        await client.WriteAsync(new byte[] { 42 }, timeout.Token);
        (await reading).ShouldBe(1); received[0].ShouldBe((byte)42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongOwnerOrServerProcessClosesBeforeAnyRpcBytes(bool wrongProcess)
    {
        using var identity = WindowsIdentity.GetCurrent(); string name = Name;
        var endpoint = new VerifiedControlEndpoint(name, wrongProcess ? identity.User! : new(WellKnownSidType.BuiltinAdministratorsSid, null),
            checked((uint)Environment.ProcessId + (wrongProcess ? 1u : 0u)));
        await using var server = Server(name); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        Task accepting = server.WaitForConnectionAsync(timeout.Token); using var client = ControlStatusClient.CreateFixture(endpoint);
        await Should.ThrowAsync<RpcException>(() => client.GetStatusAsync(timeout.Token)); await accepting;
        (await server.ReadAsync(new byte[1], timeout.Token)).ShouldBe(0, "HTTP/2 and RPC data must follow native identity verification");
    }

    [Fact]
    public async Task CancellationReleasesPendingNativeConnectionPromptly()
    {
        using var identity = WindowsIdentity.GetCurrent(); using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        stop.CancelAfter(TimeSpan.FromMilliseconds(30));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await using var unused = await ControlPipeConnector.ConnectVerifiedAsync(new(Name, identity.User!, (uint)Environment.ProcessId), stop.Token);
        });
    }

    [Fact]
    public async Task MissingPipeHasABoundedConnectionDeadline()
    {
        using var identity = WindowsIdentity.GetCurrent();
        await Should.ThrowAsync<TimeoutException>(async () =>
        {
            await using var unused = await ControlPipeConnector.ConnectVerifiedAsync(new(Name, identity.User!, (uint)Environment.ProcessId), Token);
        });
    }

    [Fact]
    public void RunningWindowsServiceConfigurationResolvesRealSidAndProcess()
    {
        var running = InstalledControlEndpoint.ReadRunningService("RpcSs");
        running.ProcessId.ShouldBeGreaterThan(0u); running.Owner.Value.ShouldStartWith("S-1-5-");
        InstalledControlEndpoint.PipeName.ShouldBe("PeerSync.Control"); InstalledControlEndpoint.ServiceName.ShouldBe("PeerSync");
    }

    [Fact]
    public void MissingServiceFailsWithTheNativeMissingServiceCode()
    {
        var error = Should.Throw<Win32Exception>(() => InstalledControlEndpoint.ReadRunningService("PeerSync-Missing-" + Guid.NewGuid().ToString("N")));
        error.NativeErrorCode.ShouldBe(1060);
    }

    [Theory]
    [InlineData("LocalSystem", "S-1-5-18")]
    [InlineData("NT AUTHORITY\\LocalService", "S-1-5-19")]
    [InlineData("NT AUTHORITY\\NetworkService", "S-1-5-20")]
    public void WellKnownConfiguredAccountsResolveWithoutLocalizedNames(string account, string expectedSid) => InstalledControlEndpoint.ResolveAccount(account).Value.ShouldBe(expectedSid);

    [Fact]
    public void EndpointValidationAndDiagnosticStringsDoNotExposeIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var endpoint = new VerifiedControlEndpoint(Name, identity.User!, (uint)Environment.ProcessId);
        endpoint.ToString().ShouldNotContain(identity.User!.Value); endpoint.ToString().ShouldNotContain(endpoint.PipeName);
        Should.Throw<ArgumentException>(() => new VerifiedControlEndpoint("../pipe", identity.User!, 1));
        Should.Throw<ArgumentException>(() => new VerifiedControlEndpoint("valid", identity.User!, 0));
        Should.Throw<ArgumentException>(() => InstalledControlEndpoint.ReadRunningService("bad/name"));
    }
    private static NamedPipeServerStream Server(string name)
    {
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var identity = WindowsIdentity.GetCurrent(); var security = pipe.GetAccessControl(); security.SetOwner(identity.User!);
        pipe.SetAccessControl(security); return pipe;
    }
}
