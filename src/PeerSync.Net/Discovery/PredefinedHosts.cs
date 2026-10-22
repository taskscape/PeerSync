using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using PeerSync.Core.Diagnostics;

namespace PeerSync.Net.Discovery;

/// <summary>Periodically resolves configured host:port entries and hands deduplicated endpoints to the dial layer.</summary>
public sealed class PredefinedHosts : IAsyncDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private readonly Func<IReadOnlyCollection<string>> getHosts;
    private readonly Func<IPEndPoint, CancellationToken, ValueTask> dialAsync;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolveAsync;
    private readonly DiagnosticJournal? diagnostics;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, byte> active = new(StringComparer.OrdinalIgnoreCase);
    private Task? runner;
    private int started;
    private int disposed;

    public PredefinedHosts(Func<IReadOnlyCollection<string>> getHosts,
        Func<IPEndPoint, CancellationToken, ValueTask> dialAsync,
        DiagnosticJournal? diagnostics = null, TimeProvider? timeProvider = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolveAsync = null)
    {
        this.getHosts = getHosts ?? throw new ArgumentNullException(nameof(getHosts));
        this.dialAsync = dialAsync ?? throw new ArgumentNullException(nameof(dialAsync));
        this.diagnostics = diagnostics;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.resolveAsync = resolveAsync ?? Dns.GetHostAddressesAsync;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        runner = RunAsync(stop.Token);
    }

    /// <summary>Immediately resolves the current configured host list, in addition to the periodic refresh.</summary>
    public Task RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return ResolveAndDialOnceAsync(cancellationToken);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await ResolveAndDialOnceAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(RefreshInterval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    internal async Task ResolveAndDialOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string> configured = getHosts();
        var attemptedEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string value in configured.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParse(value, out string host, out int port))
            {
                diagnostics?.TryWrite(new("discovery.host_invalid", "predefined_hosts")
                { Level = DiagnosticLevel.Warning, ErrorCode = "invalid_endpoint" });
                continue;
            }

            IPAddress[] addresses;
            try { addresses = await resolveAsync(host, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is SocketException or ArgumentException)
            {
                diagnostics?.TryWrite(new("discovery.host_resolution_failed", "predefined_hosts")
                { Level = DiagnosticLevel.Debug, ErrorCode = error.GetType().Name, HResult = error.HResult });
                continue;
            }

            foreach (IPAddress address in addresses.Distinct())
            {
                var endpoint = new IPEndPoint(address, port);
                string key = endpoint.ToString();
                if (!attemptedEndpoints.Add(key)) continue;
                if (!active.TryAdd(key, 0)) continue;
                try
                {
                    diagnostics?.TryWrite(new("discovery.host_dial_started", "predefined_hosts"));
                    await dialAsync(endpoint, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    diagnostics?.TryWrite(new("discovery.host_dial_failed", "predefined_hosts")
                    { Level = DiagnosticLevel.Debug, ErrorCode = error.GetType().Name, HResult = error.HResult });
                }
                finally { active.TryRemove(key, out _); }
            }
        }
    }

    private static bool TryParse(string? value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Contains('\\')) return false;
        if (!Uri.TryCreate("tcp://" + value, UriKind.Absolute, out Uri? uri)
            || uri.HostNameType == UriHostNameType.Unknown || uri.Port is < 1 or > 65535
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath is not ("" or "/")) return false;
        host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host[1..^1] : uri.Host;
        port = uri.Port;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        Task? task = runner;
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        stop.Dispose();
    }
}
