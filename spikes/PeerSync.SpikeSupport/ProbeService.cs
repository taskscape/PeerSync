using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using System.Security.Principal;

namespace PeerSync.Spikes;

public sealed class ProbeService : Probe.ProbeBase
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
        await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
            await responseStream.WriteAsync(Identify(context, request.Sequence), context.CancellationToken).ConfigureAwait(false);
    }
}
