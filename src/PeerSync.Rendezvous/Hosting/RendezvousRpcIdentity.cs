using Grpc.Core;
using PeerSync.Net.Security;

namespace PeerSync.Rendezvous.Hosting;

internal static class RendezvousRpcIdentity
{
    internal static async ValueTask<byte[]> RequireAsync(ServerCallContext context)
    {
        var certificate = await context.GetHttpContext().Connection.GetClientCertificateAsync()
            .ConfigureAwait(false);
        if (!PeerTls.IsDeviceCertificate(certificate))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "A current P-256 device certificate is required."));
        return PeerTls.DeviceId(certificate!);
    }
}
