using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using PeerSync.Control.Contracts;
using PeerSync.Spikes;

if (args is ["service-state", "running", _, _, _])
{
    var (owner, processId) = InstalledControlEndpoint.ReadRunningService(args[2]);
    uint expectedProcessId = uint.Parse(args[4], CultureInfo.InvariantCulture);
    bool ownerVerified = owner.Value == args[3], processVerified = processId == expectedProcessId;
    Console.WriteLine(JsonSerializer.Serialize(new { service = args[2], state = "running", ownerVerified, processVerified }));
    return ownerVerified && processVerified ? 0 : 1;
}
if (args is ["service-state", "stopped", _])
{
    try { _ = InstalledControlEndpoint.ReadRunningService(args[2]); }
    catch (IOException ex) when (ex.Message == "The installed control service is not running.")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { service = args[2], state = "stopped", rejected = true }));
        return 0;
    }
    Console.Error.WriteLine("FAILED stopped service was accepted");
    return 1;
}
if (args is ["service-state", "missing", _])
{
    try { _ = InstalledControlEndpoint.ReadRunningService(args[2]); }
    catch (Win32Exception ex) when (ex.NativeErrorCode == 1060)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { service = args[2], state = "missing", nativeError = ex.NativeErrorCode, rejected = true }));
        return 0;
    }
    Console.Error.WriteLine("FAILED missing service was accepted");
    return 1;
}
if (args.Length < 3 || args[0] is not ("server" or "client"))
{
    Console.Error.WriteLine("server <pipeName> <allowedSID> [readiness-file] [startup-delay-ms] | client <pipeName> <expectedOwnerSID> | service-state <running|stopped|missing> <serviceName> [expectedOwnerSID expectedPID]");
    return 2;
}
string pipeName = args[1];
if (args[0] == "server")
{
    if (args.Length > 4)
    {
        if (!int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out int delay) || delay is < 0 or > 10000) return 2;
        await Task.Delay(delay);
    }
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
    if (args.Length > 3) await File.WriteAllTextAsync(args[3], JsonSerializer.Serialize(new { processId = Environment.ProcessId, readyUtc = DateTime.UtcNow }));
    Console.WriteLine("READY");
    Console.Out.Flush();
    await app.WaitForShutdownAsync();
    return 0;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var expectedOwner = new SecurityIdentifier(args[2]);
using var handler = new SocketsHttpHandler
{
    UseProxy = false,
    AllowAutoRedirect = false,
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
    var causes = new List<object>(); Exception? cause = ex;
    for (int depth = 0; depth < 8 && cause is not null; depth++)
    {
        causes.Add(new { type = cause.GetType().Name, hResult = cause.HResult,
            nativeError = (cause as System.ComponentModel.Win32Exception)?.NativeErrorCode });
        cause = cause.InnerException ?? (cause as Grpc.Core.RpcException)?.Status.DebugException;
    }
    Console.Error.WriteLine("FAILED " + JsonSerializer.Serialize(new { processId = Environment.ProcessId, stage = "identity", error = ex.GetType().Name,
        hResult = ex.HResult, rpcStatus = (ex as Grpc.Core.RpcException)?.StatusCode.ToString(), causes }));
    return 1;
}
