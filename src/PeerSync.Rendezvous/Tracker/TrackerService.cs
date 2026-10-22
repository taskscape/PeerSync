using System.Net;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using PeerSync.Core.Diagnostics;
using PeerSync.Rendezvous.Contracts.V1;
using PeerSync.Rendezvous.Hosting;

namespace PeerSync.Rendezvous.Tracker;

internal sealed class TrackerService(PresenceRegistry presence, Relay.RelayRegistry relays,
    DiagnosticJournal diagnostics) : Contracts.V1.Rendezvous.RendezvousBase
{
    public override async Task Session(IAsyncStreamReader<ClientMessage> requests,
        IServerStreamWriter<ServerMessage> responses, ServerCallContext context)
    {
        byte[] deviceId;
        try { deviceId = await RendezvousRpcIdentity.RequireAsync(context).ConfigureAwait(false); }
        catch (RpcException)
        {
            RendezvousMetrics.AuthenticationRejected.Add(1);
            diagnostics.TryWrite(new("rendezvous.auth_rejected", "tracker")
            { Level = DiagnosticLevel.Warning, Transport = Transport(context), ErrorCode = "unauthenticated",
                RemoteAddress = context.GetHttpContext().Connection.RemoteIpAddress,
                RemotePort = context.GetHttpContext().Connection.RemotePort });
            throw;
        }
        IPEndPoint observed = new(context.GetHttpContext().Connection.RemoteIpAddress ?? IPAddress.None,
            context.GetHttpContext().Connection.RemotePort);
        PresenceSession session = presence.Open(deviceId, observed);
        RendezvousMetrics.TrackerSessions.Add(1);
        RendezvousMetrics.ActiveTrackerSessions.Add(1);
        diagnostics.TryWrite(new("rendezvous.session_opened", "tracker")
        { SessionId = session.SessionId, Transport = Transport(context), RemoteAddress = observed.Address,
            RemotePort = observed.Port });
        session.Outgoing.Writer.TryWrite(new ServerMessage
        {
            ObservedAddress = new ObservedAddress
            {
                Endpoint = new Contracts.V1.Endpoint { Address = observed.Address.ToString(), Port = (uint)observed.Port },
                SessionId = ByteString.CopyFrom(session.SessionId.ToByteArray()),
            },
        });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken,
            session.Lifetime.Token);
        Task writer = WriteLoopAsync(session.Outgoing.Reader, responses, linked.Token);
        try
        {
            bool first = true;
            while (true)
            {
                bool hasMessage;
                if (first)
                {
                    try
                    {
                        hasMessage = await requests.MoveNext(linked.Token)
                            .WaitAsync(PresenceRegistry.InitialAnnouncementLifetime, linked.Token)
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        throw new RpcException(new Status(StatusCode.DeadlineExceeded,
                            "Initial announcement was not received."));
                    }
                    first = false;
                }
                else hasMessage = await requests.MoveNext(linked.Token).ConfigureAwait(false);
                if (!hasMessage) break;
                if (session.LastAnnouncedUtc is null
                    && requests.Current.PayloadCase != ClientMessage.PayloadOneofCase.Announce)
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                        "The first tracker message must be an announcement."));
                if (!presence.TryTakeRequest(session))
                {
                    RendezvousMetrics.RequestsRateLimited.Add(1);
                    diagnostics.TryWrite(new("rendezvous.device_rate_limited", "tracker")
                    { Level = DiagnosticLevel.Warning, SessionId = session.SessionId });
                    throw new RpcException(new Status(StatusCode.ResourceExhausted, "Device request rate exceeded."));
                }
                ClientMessage request = requests.Current;
                switch (request.PayloadCase)
                {
                    case ClientMessage.PayloadOneofCase.Announce:
                        try { presence.Announce(session, request.Announce); }
                        catch (ArgumentException error)
                        { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
                        diagnostics.TryWrite(new("rendezvous.announced", "tracker")
                        { SessionId = session.SessionId, ItemsVisited = request.Announce.FolderIds.Count });
                        break;
                    case ClientMessage.PayloadOneofCase.PunchRequest:
                        if (!presence.RequestPunch(session, request.PunchRequest.DeviceId.Span,
                            request.PunchRequest.FolderId.Span))
                            throw new RpcException(new Status(StatusCode.PermissionDenied, "Target is unavailable for this folder."));
                        diagnostics.TryWrite(new("rendezvous.punch_introduced", "tracker")
                        { SessionId = session.SessionId });
                        break;
                    case ClientMessage.PayloadOneofCase.RelayRequest:
                        if (!presence.TryGetShared(session, request.RelayRequest.DeviceId.Span,
                            request.RelayRequest.FolderId.Span, out PresenceSession? target)
                            || target is null || !relays.TryOffer(session, target))
                            throw new RpcException(new Status(StatusCode.PermissionDenied, "Target is unavailable for relay."));
                        diagnostics.TryWrite(new("rendezvous.relay_offered", "tracker")
                        { SessionId = session.SessionId });
                        break;
                    default:
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "Unknown tracker message."));
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            RendezvousMetrics.ActiveTrackerSessions.Add(-1);
            presence.Close(session);
            await linked.CancelAsync().ConfigureAwait(false);
            try { await writer.ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            session.Lifetime.Dispose();
            diagnostics.TryWrite(new("rendezvous.session_closed", "tracker") { SessionId = session.SessionId });
        }
    }

    private static async Task WriteLoopAsync(ChannelReader<ServerMessage> messages,
        IServerStreamWriter<ServerMessage> responses, CancellationToken cancellationToken)
    {
        await foreach (ServerMessage message in messages.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            await responses.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private static DiagnosticTransport Transport(ServerCallContext context)
        => context.GetHttpContext().Request.Protocol == "HTTP/3" ? DiagnosticTransport.Http3 : DiagnosticTransport.Http2;
}
