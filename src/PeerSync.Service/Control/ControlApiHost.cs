using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using PeerSync.Control.Contracts;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Service.Hosting;
using PeerSync.Service.Events;
using PeerSync.Service.Settings;

namespace PeerSync.Service.Control;

public sealed class ControlApiHost : IAsyncDisposable
{
    private readonly WebApplication application;
    private readonly ControlServerLifetime lifetime;
    private readonly CancellationTokenRegistration stopping;
    private readonly ControlSubscriptionBudget subscriptions;
    private readonly PowerEvents power;
    private readonly NetworkChangeMonitor networkChanges;
    private readonly object gate = new();
    private Task? closed;
    private ControlApiHost(WebApplication application, ControlServerLifetime lifetime, CancellationTokenRegistration stopping,
        ControlSubscriptionBudget subscriptions, PowerEvents power, NetworkChangeMonitor networkChanges)
    { this.application = application; this.lifetime = lifetime; this.stopping = stopping; this.subscriptions = subscriptions; this.power = power; this.networkChanges = networkChanges; }
    internal int ActiveSubscriptions => subscriptions.Active;
    internal TimeSpan ShutdownTimeout => application.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout;
    internal IReadOnlyCollection<string> BoundAddresses => application.Urls.ToArray();
    internal PowerEvents Power => power;
    internal Task RestartRequested => lifetime.RestartRequested;

    public static Task<ControlApiHost> StartInstalledAsync(ControlStatusRuntime runtime, DiagnosticJournal? diagnostics = null, CancellationToken cancellationToken = default)
    {
        var group = (SecurityIdentifier)new NTAccount(Environment.MachineName, ControlServiceNames.UsersGroup).Translate(typeof(SecurityIdentifier));
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PeerSync", "config", "settings.json");
        var current = new SettingsStore(settingsPath).ReadIfPresent()
            ?? throw new InvalidOperationException("Device settings are unavailable.");
        var allowed = new List<SecurityIdentifier> { group };
        allowed.AddRange(current.ControlUserSids.Select(sid => new SecurityIdentifier(sid)));
        return StartAsync(ControlServiceNames.PipeName, runtime, allowed, true, settingsPath, diagnostics, null, cancellationToken);
    }
    internal static Task<ControlApiHost> StartFixtureAsync(string pipe, ControlStatusRuntime runtime, IReadOnlyList<SecurityIdentifier> allowed,
        DiagnosticJournal? diagnostics = null, INetworkChangeSource? networkSource = null, CancellationToken cancellationToken = default)
        => StartAsync(pipe, runtime, allowed, false, null, diagnostics, networkSource, cancellationToken);

    private static async Task<ControlApiHost> StartAsync(string pipe, ControlStatusRuntime runtime, IReadOnlyList<SecurityIdentifier> allowed, bool administrators,
        string? settingsPath, DiagnosticJournal? diagnostics, INetworkChangeSource? networkSource, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(runtime); ArgumentNullException.ThrowIfNull(allowed);
        using var identity = WindowsIdentity.GetCurrent(); var self = identity.User ?? throw new InvalidOperationException("Control server has no Windows identity.");
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(self);
        security.AddAccessRule(new(self, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        foreach (var sid in allowed) security.AddAccessRule(new(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        if (administrators) security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));
        builder.Configuration.Sources.Clear();
        if (settingsPath is null) builder.Configuration.AddInMemoryCollection();
        else
        {
            builder.Configuration.AddJsonFile(settingsPath, optional: false, reloadOnChange: true);
            builder.Services.AddSingleton<IValidateOptions<DeviceSettings>, DeviceSettingsValidator>();
            builder.Services.AddOptions<DeviceSettings>().Bind(builder.Configuration).ValidateOnStart();
        }
        builder.Logging.ClearProviders(); builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, "");
        builder.Services.AddWindowsService(options => options.ServiceName = ControlServiceNames.ServiceName);
        var power = new PowerEvents(diagnostics);
        power.Suspending += runtime.NotifySuspending;
        power.Resumed += runtime.NotifyResumed;
        builder.Services.AddSingleton(power);
        var networkChanges = new NetworkChangeMonitor(diagnostics, networkSource);
        networkChanges.Changed += runtime.NotifyNetworkChanged;
        builder.Services.AddSingleton(networkChanges);
        if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
            builder.Services.AddSingleton<IHostLifetime, PeerSyncServiceLifetime>();
        builder.Services.AddSingleton(runtime); builder.Services.AddSingleton(new ControlCallerPolicy(allowed, administrators));
        builder.Services.AddSingleton<ControlIdentityInterceptor>();
        builder.Services.AddSingleton(new ControlSettingsRuntime(settingsPath));
        var shutdown = new ControlServerLifetime(); var budget = new ControlSubscriptionBudget();
        builder.Services.AddSingleton(shutdown); builder.Services.AddSingleton(budget);
        if (diagnostics is not null) builder.Services.AddSingleton(diagnostics);
        builder.Services.AddGrpc(options => { options.EnableDetailedErrors = false; options.MaxReceiveMessageSize = ControlStatusSnapshot.MaximumMessageBytes; options.MaxSendMessageSize = ControlStatusSnapshot.MaximumMessageBytes; options.Interceptors.Add<ControlIdentityInterceptor>(); });
        builder.WebHost.UseNamedPipes(options => { options.CurrentUserOnly = false; options.PipeSecurity = security; });
        builder.WebHost.ConfigureKestrel(options => options.ListenNamedPipe(pipe, listen => listen.Protocols = HttpProtocols.Http2));
        var app = builder.Build(); var stopping = app.Lifetime.ApplicationStopping.Register(shutdown.Cancel);
        app.MapGrpcService<ControlStatusRpc>();
        try { await app.StartAsync(token).ConfigureAwait(false); return new(app, shutdown, stopping, budget, power, networkChanges); }
        catch
        {
            networkChanges.Changed -= runtime.NotifyNetworkChanged;
            networkChanges.Dispose(); stopping.Dispose(); shutdown.Dispose(); await app.DisposeAsync().ConfigureAwait(false); throw;
        }
    }
    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) => application.WaitForShutdownAsync(cancellationToken);
    public ValueTask DisposeAsync()
    { lock (gate) return new(closed ??= CloseAsync()); }
    private async Task CloseAsync()
    {
        lifetime.Cancel(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await application.StopAsync(deadline.Token).ConfigureAwait(false); }
        finally
        {
            networkChanges.Changed -= application.Services.GetRequiredService<ControlStatusRuntime>().NotifyNetworkChanged;
            networkChanges.Dispose(); stopping.Dispose(); await application.DisposeAsync().ConfigureAwait(false); lifetime.Dispose();
        }
    }
}
