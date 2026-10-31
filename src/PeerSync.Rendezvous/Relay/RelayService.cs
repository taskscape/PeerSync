using Grpc.Core;
using PeerSync.Core.Diagnostics;
using PeerSync.Rendezvous.Contracts.V1;
using PeerSync.Rendezvous.Hosting;

namespace PeerSync.Rendezvous.Relay;

internal sealed class RelayService(RelayRegistry registry, DiagnosticJournal diagnostics)
    : Contracts.V1.Relay.RelayBase
{
    public override async Task Pipe(IAsyncStreamReader<RelayChunk> requests,
        IServerStreamWriter<RelayChunk> responses, ServerCallContext context)
    {
        var connection = context.GetHttpContext().Connection;
        byte[] deviceId;
        try { deviceId = await RendezvousRpcIdentity.RequireAsync(context).ConfigureAwait(false); }
        catch (RpcException)
        {
            RendezvousMetrics.AuthenticationRejected.Add(1);
            diagnostics.TryWrite(new("rendezvous.auth_rejected", "relay")
            { Level = DiagnosticLevel.Warning, ErrorCode = "unauthenticated",
                Transport = context.GetHttpContext().Request.Protocol == "HTTP/3"
                    ? DiagnosticTransport.Http3 : DiagnosticTransport.Http2,
                RemoteAddress = connection.RemoteIpAddress,
                RemotePort = connection.RemotePort });
            throw;
        }
        string? encoded = context.RequestHeaders.FirstOrDefault(header => header.Key == "psp-relay-token")?.Value;
        if (encoded is null || encoded.Length != 64)
        {
            diagnostics.TryWrite(new("rendezvous.relay_join_rejected", "relay")
            { Level = DiagnosticLevel.Warning, ErrorCode = "missing-or-invalid-token", Transport = Transport(context),
                RemoteAddress = connection.RemoteIpAddress, RemotePort = connection.RemotePort });
            throw new RpcException(new Status(StatusCode.Unauthenticated, "A relay token is required."));
        }
        byte[] token;
        try { token = Convert.FromHexString(encoded); }
        catch (FormatException)
        {
            diagnostics.TryWrite(new("rendezvous.relay_join_rejected", "relay")
            { Level = DiagnosticLevel.Warning, ErrorCode = "invalid-token", Transport = Transport(context),
                RemoteAddress = connection.RemoteIpAddress, RemotePort = connection.RemotePort });
            throw new RpcException(new Status(StatusCode.Unauthenticated, "A valid relay token is required."));
        }
        RelayRegistry.RelayJoin join;
        try
        {
            join = await registry.JoinAsync(token, deviceId, responses,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (RpcException error)
        {
            diagnostics.TryWrite(new("rendezvous.relay_join_rejected", "relay")
            { Level = DiagnosticLevel.Warning, ErrorCode = error.StatusCode.ToString(), Transport = Transport(context),
                RemoteAddress = connection.RemoteIpAddress, RemotePort = connection.RemotePort });
            throw;
        }
        catch (OperationCanceledException error)
        {
            diagnostics.TryWrite(new("rendezvous.relay_join_rejected", "relay")
            { Level = context.CancellationToken.IsCancellationRequested ? DiagnosticLevel.Debug : DiagnosticLevel.Warning,
                ErrorCode = context.CancellationToken.IsCancellationRequested ? "client-cancelled" : "join-ended-or-timeout",
                Transport = Transport(context), HResult = error.HResult,
                RemoteAddress = connection.RemoteIpAddress, RemotePort = connection.RemotePort });
            throw;
        }
        diagnostics.TryWrite(new("rendezvous.relay_joined", "relay")
        { SessionId = join.SessionId, Transport = DiagnosticTransport.Relay });
        RendezvousMetrics.RelaySessions.Add(1);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken,
            join.SessionEnded);
        long bytes = 0;
        try
        {
            while (await requests.MoveNext(linked.Token).ConfigureAwait(false))
            {
                RelayChunk chunk = requests.Current;
                await registry.ThrottleAsync(join, chunk.Data.Length, linked.Token).ConfigureAwait(false);
                await join.PartnerWriter.WriteAsync(chunk, linked.Token).ConfigureAwait(false);
                bytes += chunk.Data.Length;
                RendezvousMetrics.RelayBytes.Add(chunk.Data.Length);
            }
        }
        catch (RpcException error)
        {
            diagnostics.TryWrite(new("rendezvous.relay_forward_failed", "relay")
            { Level = DiagnosticLevel.Warning, SessionId = join.SessionId, Exception = error,
                Transport = DiagnosticTransport.Relay, ErrorCode = error.StatusCode.ToString(), HResult = error.HResult });
            throw;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            diagnostics.TryWrite(new("rendezvous.relay_transport_failed", "relay")
            { Level = DiagnosticLevel.Warning, SessionId = join.SessionId,
                Transport = DiagnosticTransport.Relay, ErrorCode = error.GetType().Name,
                Exception = error, HResult = error.HResult });
            throw;
        }
        finally
        {
            registry.Leave(join);
            diagnostics.TryWrite(new("rendezvous.relay_left", "relay")
            { SessionId = join.SessionId, Transport = DiagnosticTransport.Relay, Bytes = bytes });
        }
    }

    private static DiagnosticTransport Transport(ServerCallContext context)
        => context.GetHttpContext().Request.Protocol == "HTTP/3" ? DiagnosticTransport.Http3 : DiagnosticTransport.Http2;
}
