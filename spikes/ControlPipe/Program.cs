using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using PeerSync.Spikes;

if (args.Length < 3 || args[0] is not ("server" or "client"))
{
    Console.Error.WriteLine("server <pipeName> <allowedSID> [--service] | client <pipeName> <expectedOwnerSID>");
    return 2;
}
string pipeName = args[1];
if (args[0] == "server")
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
    builder.Logging.ClearProviders();
    builder.Services.AddWindowsService(o => o.ServiceName = "PeerSyncControlSpike");
    builder.Services.AddGrpc();
    using var identity = WindowsIdentity.GetCurrent();
    var self = identity.User!;
    var security = new PipeSecurity();
    security.SetAccessRuleProtection(true, false);
    security.SetOwner(self);
    security.AddAccessRule(new(self, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
    security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
    security.AddAccessRule(new(new SecurityIdentifier(args[2]), PipeAccessRights.ReadWrite, AccessControlType.Allow));
    builder.WebHost.UseNamedPipes(o => { o.CurrentUserOnly = false; o.PipeSecurity = security; });
    builder.WebHost.ConfigureKestrel(k => k.ListenNamedPipe(pipeName, o => o.Protocols = HttpProtocols.Http2));
    await using var app = builder.Build();
    app.MapGrpcService<ProbeService>();
    await app.StartAsync();
    if (args.Length > 3) await File.WriteAllTextAsync(args[3], "ready");
    Console.WriteLine("READY");
    Console.Out.Flush();
    await app.WaitForShutdownAsync();
    return 0;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var expectedOwner = new SecurityIdentifier(args[2]);
using var handler = new SocketsHttpHandler
{
    ConnectCallback = async (_, ct) =>
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(TimeSpan.FromSeconds(3), ct);
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (!expectedOwner.Equals(owner)) throw new SecurityException("Unexpected control pipe owner.");
            return pipe;
        }
        catch { await pipe.DisposeAsync(); throw; }
    },
};
using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
try
{
    var response = await new Probe.ProbeClient(channel).IdentityAsync(new ProbeMessage { Sequence = 123 }, cancellationToken: timeout.Token);
    using var identity = WindowsIdentity.GetCurrent();
    bool verified = response.CallerSid == identity.User!.Value && response.Sequence == 123;
    Console.WriteLine(JsonSerializer.Serialize(new { ownerVerified = true, returnedCallerSid = response.CallerSid, callerVerified = verified }));
    return verified ? 0 : 1;
}
catch (Exception ex) when (ex is Grpc.Core.RpcException or HttpRequestException or OperationCanceledException)
{
    Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"FAILED {ex.GetType().Name} 0x{ex.HResult:X8}"));
    return 1;
}
