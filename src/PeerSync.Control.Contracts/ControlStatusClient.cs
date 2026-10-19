using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Win32.SafeHandles;
using Wire = PeerSync.Control.Contracts.V1;

namespace PeerSync.Control.Contracts;

/// <summary>Shared local service client. Production callers cannot override
/// the installed service identity or route this channel onto a TCP socket.</summary>
public sealed class ControlStatusClient : IDisposable
{
    private readonly GrpcChannel channel;
    private readonly Wire.PeerSyncControl.PeerSyncControlClient client;
    private ControlStatusClient(VerifiedControlEndpoint endpoint)
    {
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, MaxConnectionsPerServer = 1,
            ConnectCallback = (_, token) => ControlPipeConnector.ConnectVerifiedAsync(endpoint, token) };
        channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true,
            MaxReceiveMessageSize = ControlStatusSnapshot.MaximumMessageBytes, MaxSendMessageSize = ControlStatusSnapshot.MaximumMessageBytes });
        client = new(channel);
    }
    public static ControlStatusClient CreateForInstalledService() => new(InstalledControlEndpoint.Resolve());
    internal static ControlStatusClient CreateFixture(VerifiedControlEndpoint endpoint) => new(endpoint);

    public async Task<ControlStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var call = client.GetStatusAsync(new(), deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken);
        return ControlStatusWire.Decode(await call.ResponseAsync.ConfigureAwait(false));
    }
    public async IAsyncEnumerable<ControlStatusSnapshot> WatchEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var call = client.WatchEvents(new(), cancellationToken: cancellationToken);
        await foreach (var update in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (update.Status is null) throw new InvalidDataException("Control event has no status.");
            yield return ControlStatusWire.Decode(update.Status);
        }
    }
    public void Dispose() => channel.Dispose();
    public override string ToString() => "Local control status client (redacted)";
}

internal static partial class ControlPipeConnector
{
    internal static async ValueTask<Stream> ConnectVerifiedAsync(VerifiedControlEndpoint endpoint, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            if (!endpoint.Owner.Equals(pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)))) throw new SecurityException("Unexpected control pipe owner.");
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (processId != endpoint.ProcessId) throw new SecurityException("Unexpected control server process.");
            return pipe;
        }
        catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
    }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
