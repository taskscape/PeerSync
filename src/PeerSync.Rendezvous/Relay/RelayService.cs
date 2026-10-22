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
        byte[] deviceId;
        try { deviceId = await RendezvousRpcIdentity.RequireAsync(context).ConfigureAwait(false); }
        catch (RpcException)
        {
            RendezvousMetrics.AuthenticationRejected.Add(1);
            diagnostics.TryWrite(new("rendezvous.auth_rejected", "relay")
            { Level = DiagnosticLevel.Warning, ErrorCode = "unauthenticated",
                Transport = context.GetHttpContext().Request.Protocol == "HTTP/3"
                    ? DiagnosticTransport.Http3 : DiagnosticTransport.Http2,
                RemoteAddress = context.GetHttpContext().Connection.RemoteIpAddress,
                RemotePort = context.GetHttpContext().Connection.RemotePort });
            throw;
        }
        string? encoded = context.RequestHeaders.FirstOrDefault(header => header.Key == "psp-relay-token")?.Value;
        if (encoded is null || encoded.Length != 64)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "A relay token is required."));
        byte[] token;
        try { token = Convert.FromHexString(encoded); }
        catch (FormatException)
        { throw new RpcException(new Status(StatusCode.Unauthenticated, "A valid relay token is required.")); }
        RelayRegistry.RelayJoin join;
        try
        {
            join = await registry.JoinAsync(token, deviceId, responses,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (RpcException error)
        {
            diagnostics.TryWrite(new("rendezvous.relay_join_rejected", "relay")
            { Level = DiagnosticLevel.Warning, ErrorCode = error.StatusCode.ToString() });
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
            { Level = DiagnosticLevel.Warning, SessionId = join.SessionId,
                Transport = DiagnosticTransport.Relay, ErrorCode = error.StatusCode.ToString() });
            throw;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            diagnostics.TryWrite(new("rendezvous.relay_transport_failed", "relay")
            { Level = DiagnosticLevel.Warning, SessionId = join.SessionId,
                Transport = DiagnosticTransport.Relay, ErrorCode = error.GetType().Name,
                HResult = error.HResult });
            throw;
        }
        finally
        {
            registry.Leave(join);
            diagnostics.TryWrite(new("rendezvous.relay_left", "relay")
            { SessionId = join.SessionId, Transport = DiagnosticTransport.Relay, Bytes = bytes });
        }
    }
}
