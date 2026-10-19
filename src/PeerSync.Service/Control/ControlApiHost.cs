using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using PeerSync.Control.Contracts;
using PeerSync.Core.Diagnostics;

namespace PeerSync.Service.Control;

public sealed class ControlApiHost : IAsyncDisposable
{
    private readonly WebApplication application;
    private readonly ControlServerLifetime lifetime;
    private readonly CancellationTokenRegistration stopping;
    private readonly ControlSubscriptionBudget subscriptions;
    private readonly object gate = new();
    private Task? closed;
    private ControlApiHost(WebApplication application, ControlServerLifetime lifetime, CancellationTokenRegistration stopping, ControlSubscriptionBudget subscriptions)
    { this.application = application; this.lifetime = lifetime; this.stopping = stopping; this.subscriptions = subscriptions; }
    internal int ActiveSubscriptions => subscriptions.Active;
    internal IReadOnlyCollection<string> BoundAddresses => application.Urls.ToArray();

    public static Task<ControlApiHost> StartInstalledAsync(ControlStatusRuntime runtime, DiagnosticJournal? diagnostics = null, CancellationToken cancellationToken = default)
    {
        var group = (SecurityIdentifier)new NTAccount(Environment.MachineName, ControlServiceNames.UsersGroup).Translate(typeof(SecurityIdentifier));
        return StartAsync(ControlServiceNames.PipeName, runtime, [group], true, diagnostics, cancellationToken);
    }
    internal static Task<ControlApiHost> StartFixtureAsync(string pipe, ControlStatusRuntime runtime, IReadOnlyList<SecurityIdentifier> allowed,
        DiagnosticJournal? diagnostics = null, CancellationToken cancellationToken = default) => StartAsync(pipe, runtime, allowed, false, diagnostics, cancellationToken);

    private static async Task<ControlApiHost> StartAsync(string pipe, ControlStatusRuntime runtime, IReadOnlyList<SecurityIdentifier> allowed, bool administrators,
        DiagnosticJournal? diagnostics, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(runtime); ArgumentNullException.ThrowIfNull(allowed);
        using var identity = WindowsIdentity.GetCurrent(); var self = identity.User ?? throw new InvalidOperationException("Control server has no Windows identity.");
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(self);
        security.AddAccessRule(new(self, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        foreach (var sid in allowed) security.AddAccessRule(new(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        if (administrators) security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders(); builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, "");
        builder.Services.AddWindowsService(options => options.ServiceName = ControlServiceNames.ServiceName);
        builder.Services.AddSingleton(runtime); builder.Services.AddSingleton(new ControlCallerPolicy(allowed, administrators));
        var shutdown = new ControlServerLifetime(); var budget = new ControlSubscriptionBudget();
        builder.Services.AddSingleton(shutdown); builder.Services.AddSingleton(budget);
        if (diagnostics is not null) builder.Services.AddSingleton(diagnostics);
        builder.Services.AddGrpc(options => { options.EnableDetailedErrors = false; options.MaxReceiveMessageSize = ControlStatusSnapshot.MaximumMessageBytes; options.MaxSendMessageSize = ControlStatusSnapshot.MaximumMessageBytes; });
        builder.WebHost.UseNamedPipes(options => { options.CurrentUserOnly = false; options.PipeSecurity = security; });
        builder.WebHost.ConfigureKestrel(options => options.ListenNamedPipe(pipe, listen => listen.Protocols = HttpProtocols.Http2));
        var app = builder.Build(); var stopping = app.Lifetime.ApplicationStopping.Register(shutdown.Cancel);
        app.MapGrpcService<ControlStatusRpc>();
        try { await app.StartAsync(token).ConfigureAwait(false); return new(app, shutdown, stopping, budget); }
        catch { stopping.Dispose(); shutdown.Dispose(); await app.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) => application.WaitForShutdownAsync(cancellationToken);
    public ValueTask DisposeAsync()
    { lock (gate) return new(closed ??= CloseAsync()); }
    private async Task CloseAsync()
    {
        lifetime.Cancel(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await application.StopAsync(deadline.Token).ConfigureAwait(false); }
        finally { stopping.Dispose(); await application.DisposeAsync().ConfigureAwait(false); lifetime.Dispose(); }
    }
}
