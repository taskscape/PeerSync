using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using System.Security.Principal;
using PeerSync.Core.Diagnostics;

namespace PeerSync.Spikes;

public sealed class ProbeService(DiagnosticJournal? diagnostics = null) : Probe.ProbeBase
{
    private static ProbeMessage Identify(ServerCallContext context, long sequence)
    {
        var connection = context.GetHttpContext().Connection;
        var response = new ProbeMessage { Sequence = sequence };
        if (connection.ClientCertificate is { } certificate)
            response.DeviceId = ByteString.CopyFrom(DeviceIdentity.FromCertificate(certificate));
        var pipe = context.GetHttpContext().Features.Get<IConnectionNamedPipeFeature>()?.NamedPipe;
        if (pipe is not null)
        {
            pipe.RunAsClient(() =>
            {
                using var caller = WindowsIdentity.GetCurrent(ifImpersonating: true);
                response.CallerSid = caller?.User?.Value ?? "";
            });
            if (response.CallerSid.Length == 0) throw new RpcException(new(StatusCode.PermissionDenied, "No caller identity."));
        }
        return response;
    }

    public override Task<ProbeMessage> Identity(ProbeMessage request, ServerCallContext context) => Task.FromResult(Identify(context, request.Sequence));

    public override async Task Session(IAsyncStreamReader<ProbeMessage> requestStream,
        IServerStreamWriter<ProbeMessage> responseStream, ServerCallContext context)
    {
        Guid? session = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
            {
                if (!Guid.TryParseExact(request.SessionId, "N", out Guid incoming))
                    throw new RpcException(new(StatusCode.InvalidArgument, "Invalid diagnostic session."));
                if (session is not null && session != incoming)
                    throw new RpcException(new(StatusCode.InvalidArgument, "Session changed mid-stream."));
                session = incoming;
                var response = Identify(context, request.Sequence);
                response.SessionId = incoming.ToString("N");
                await responseStream.WriteAsync(response, context.CancellationToken).ConfigureAwait(false);
                diagnostics?.Write(new("probe.session.message", "session") { Level = DiagnosticLevel.Debug, SessionId = session,
                    Transport = Transport(context), RemoteAddress = context.GetHttpContext().Connection.RemoteIpAddress });
            }
            diagnostics?.Write(new("probe.session.completed", "session") { SessionId = session,
                Transport = Transport(context), ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds });
        }
        catch (Exception ex) when (ex is RpcException or OperationCanceledException or IOException)
        {
            diagnostics?.Write(new("probe.session.failed", "session") { SessionId = session, Level = DiagnosticLevel.Error,
                Transport = Transport(context), ErrorCode = ex.GetType().Name, NativeError = ex.HResult,
                ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds });
            throw;
        }
    }

    private static DiagnosticTransport Transport(ServerCallContext context) =>
        context.GetHttpContext().Request.Protocol == "HTTP/3" ? DiagnosticTransport.Http3 : DiagnosticTransport.Http2;
}
