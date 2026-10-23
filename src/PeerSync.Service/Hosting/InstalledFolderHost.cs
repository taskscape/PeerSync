using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Net;
using PeerSync.Core.Diagnostics;
using PeerSync.Crypto;
using PeerSync.Engine.Scanning;
using PeerSync.Engine.Sync;
using PeerSync.Engine.Archive;
using PeerSync.Core.Settings;
using PeerSync.Core.Ignore;
using PeerSync.Net.Connections;
using PeerSync.Net.Discovery;
using PeerSync.Net.Quic;
using PeerSync.Net.Security;
using PeerSync.Service.Control;
using PeerSync.Storage.FolderDb;
using PeerSync.Storage.StateDb;

namespace PeerSync.Service.Hosting;

/// <summary>Owns the installed service's local folder engines and scan schedule.
/// The state database, indexes and keys remain alive until scans and transports drain.</summary>
internal sealed class InstalledFolderHost : IAsyncDisposable
{
    private readonly StateStore state;
    private readonly List<FolderRuntime> folders;
    private readonly object folderGate;
    private readonly SemaphoreSlim addGate = new(1, 1);
    private readonly Dictionary<FolderRuntime, (CancellationTokenSource Stop, Task Task)> folderSchedulers = [];
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ControlStatusRuntime status;
    private readonly DiagnosticJournal? diagnostics;
    private PeerNetworkRuntime? network;
    private readonly X509Certificate2? certificate;
    private readonly DeviceSettings? deviceSettings;
    private readonly TransferThrottle? uploadThrottle, downloadThrottle;
    private readonly string statePath;
    private readonly string actorSid;
    private readonly byte[] deviceId;
    private readonly object rescanGate = new();
    private Task? rescanTask;
    private long requestedRescanGeneration;
    private string pendingRescanReason = "network_change";
    private readonly object pauseGate = new();
    private CancellationTokenSource? autoResumeStop;
    private Task? autoResumeTask;
    private string[] autoResumeIds = [];
    private int disposed;

    private InstalledFolderHost(StateStore state, List<FolderRuntime> folders,
        ControlStatusRuntime status, DiagnosticJournal? diagnostics, PeerNetworkRuntime? network,
        string statePath, string actorSid, byte[] deviceId, X509Certificate2? certificate, DeviceSettings? deviceSettings,
        TransferThrottle? uploadThrottle, TransferThrottle? downloadThrottle, object folderGate)
    {
        this.state = state; this.folders = folders;
        this.status = status; this.diagnostics = diagnostics; this.network = network;
        this.statePath = statePath; this.actorSid = actorSid; this.deviceId = deviceId.ToArray();
        this.certificate = certificate; this.deviceSettings = deviceSettings;
        this.uploadThrottle = uploadThrottle; this.downloadThrottle = downloadThrottle;
        this.folderGate = folderGate;
        status.Resumed += OnResumed;
        status.NetworkChanged += OnNetworkChanged;
        foreach (var folder in folders) StartFolderScheduler(folder);
        if (network is not null) _ = ObserveComponentAsync(network.Completion, "service.peer_accept_failed");
    }

    public bool HasActiveFolders { get { lock (folderGate) return folders.Count != 0; } }
    public Task Completion => completion.Task;

    public static async Task<InstalledFolderHost> StartAsync(string statePath, string certificateThumbprint,
        DeviceSettings deviceSettings, ControlStatusRuntime status, DiagnosticJournal? diagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(certificateThumbprint);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(deviceSettings);
        X509Certificate2 certificate;
        try { certificate = LoadDeviceCertificate(certificateThumbprint); }
        catch (CryptographicException error)
        {
            diagnostics?.TryWrite(new("service.device_certificate_load_failed", "startup")
            { Level = DiagnosticLevel.Error, ErrorCode = error.GetType().Name, HResult = error.HResult });
            throw;
        }
        diagnostics?.TryWrite(new("service.device_certificate_loaded", "startup"));
        byte[] deviceId = PeerTls.DeviceId(certificate);
        string actorSid;
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            actorSid = identity.User?.Value ?? throw new InvalidOperationException("Service process has no Windows identity.");

        try { return await StartWithIdentityAsync(statePath, deviceId, actorSid, status, diagnostics,
            certificate, deviceSettings, cancellationToken).ConfigureAwait(false); }
        catch { certificate.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(deviceId); }
    }

    internal static Task<InstalledFolderHost> StartForTestAsync(string statePath, byte[] deviceId, string actorSid,
        ControlStatusRuntime status, DiagnosticJournal? diagnostics = null, CancellationToken cancellationToken = default)
        => StartWithIdentityAsync(statePath, deviceId, actorSid, status, diagnostics, null, null, cancellationToken);

    internal static async Task<InstalledFolderHost> StartForTestWithNetworkAsync(string statePath, byte[] deviceId,
        string actorSid, X509Certificate2 certificate, DeviceSettings deviceSettings, ControlStatusRuntime status,
        DiagnosticJournal? diagnostics = null, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await StartWithIdentityAsync(statePath, deviceId, actorSid, status, diagnostics,
                certificate, deviceSettings, cancellationToken, timeProvider).ConfigureAwait(false);
        }
        catch { certificate.Dispose(); throw; }
    }

    internal ValueTask ConnectToPeerForTestAsync(IPEndPoint endpoint, CancellationToken cancellationToken = default)
        => network?.ConnectToPeerForTestAsync(endpoint, cancellationToken)
            ?? ValueTask.FromException(new InvalidOperationException("Peer network is not running."));

    internal void SetPeerEndpointsForTest(byte[] deviceId,
        IReadOnlyList<(IPEndPoint Address, ConnectionSource Source)> peerEndpoints)
    {
        if (network is null) throw new InvalidOperationException("Peer network is not running.");
        network.SetPeerEndpointsForTest(deviceId, peerEndpoints);
    }

    internal ValueTask ConnectToPeerOverTcpForTestAsync(IPEndPoint endpoint, CancellationToken cancellationToken = default)
        => network?.ConnectToPeerOverTcpForTestAsync(endpoint, cancellationToken)
            ?? ValueTask.FromException(new InvalidOperationException("Peer network is not running."));

    private static async Task<InstalledFolderHost> StartWithIdentityAsync(string statePath, byte[] deviceId, string actorSid,
        ControlStatusRuntime status, DiagnosticJournal? diagnostics, X509Certificate2? certificate,
        DeviceSettings? deviceSettings, CancellationToken cancellationToken, TimeProvider? timeProvider = null)
    {
        if (deviceId.Length != 32) throw new ArgumentException("Device identifier must contain 32 bytes.", nameof(deviceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actorSid);
        byte[] ownDevice = deviceId.ToArray();

        StateStore? state = null;
        PeerNetworkRuntime? network = null;
        TransferThrottle? uploadThrottle = deviceSettings is { UploadLimitBytesPerSec: > 0 }
            ? new(deviceSettings.UploadLimitBytesPerSec) : null;
        TransferThrottle? downloadThrottle = deviceSettings is { DownloadLimitBytesPerSec: > 0 }
            ? new(deviceSettings.DownloadLimitBytesPerSec) : null;
        var folders = new List<FolderRuntime>();
        var folderGate = new object();
        try
        {
            state = await StateStore.OpenAsync(statePath, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var descriptor in state.ListFolders().ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (descriptor.Paused) continue;
                FolderIndex? index = null;
                FolderKeys? keys = null;
                Scanner? scanner = null;
                TransportFolder? transport = null;
                IDisposable? registration = null;
                try
                {
                    string id = Convert.ToHexString(descriptor.Id);
                    string indexPath = Path.Combine(Path.GetDirectoryName(statePath)!, "folders", id + ".db");
                    Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
                    index = await FolderIndex.OpenAsync(indexPath, cancellationToken).ConfigureAwait(false);
                    keys = await state.ReadFolderKeysAsync(descriptor.Id, actorSid, cancellationToken).ConfigureAwait(false);
                    EnsureFolderMarker(descriptor.Path, descriptor.Id);
                    if (!keys.CanRead) throw new InvalidOperationException("Encrypted-only folders require the opaque engine.");
                    var ignore = new IgnoreMatcher(descriptor.Settings.IgnoreRules);
                    transport = new TransportFolder(descriptor.Path, index, keys, ownDevice, descriptor.Settings,
                        timeProvider: timeProvider, journal: diagnostics, uploadThrottle: uploadThrottle,
                        downloadThrottle: downloadThrottle, ignore: ignore);
                    scanner = transport.Scanner;
                    await transport.RecoverBeforeExternalScanAsync(cancellationToken).ConfigureAwait(false);
                    registration = status.RegisterFolder(transport, scanner);
                    folders.Add(new(index, keys, scanner, transport, registration, descriptor.Settings, descriptor.Path));
                    index = null; keys = null; scanner = null; transport = null; registration = null;
                    diagnostics?.TryWrite(new("service.folder_engine_ready", "folder_host") { ItemsChanged = folders.Count });
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
                    or System.Security.Cryptography.CryptographicException or ArgumentException)
                {
                    diagnostics?.TryWrite(new("service.folder_engine_failed", "folder_host")
                    { Level = DiagnosticLevel.Error, ErrorCode = error.GetType().Name, HResult = error.HResult });
                }
                finally
                {
                    registration?.Dispose();
                    if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
                    if (index is not null) await index.DisposeAsync().ConfigureAwait(false);
                    keys?.Dispose();
                }
            }
            if (certificate is not null && deviceSettings is not null && folders.Count != 0)
                network = await PeerNetworkRuntime.StartAsync(certificate, ownDevice, () => { lock (folderGate) return folders.ToArray(); }, deviceSettings,
                    status, (folderId, remoteDevice) => RecordPending(state, folderId, remoteDevice, diagnostics),
                    diagnostics, cancellationToken, timeProvider).ConfigureAwait(false);
            diagnostics?.TryWrite(new("service.folder_host_ready", "folder_host") { ItemsChanged = folders.Count });
            InstalledFolderHost host = new(state, folders, status, diagnostics, network, statePath, actorSid,
                ownDevice, certificate, deviceSettings, uploadThrottle, downloadThrottle, folderGate);
            uploadThrottle = null; downloadThrottle = null;
            return host;
        }
        catch
        {
            if (network is not null) await network.DisposeAsync().ConfigureAwait(false);
            foreach (var folder in folders.AsEnumerable().Reverse()) await folder.DisposeAsync().ConfigureAwait(false);
            if (state is not null) await state.DisposeAsync().ConfigureAwait(false);
            uploadThrottle?.Dispose(); downloadThrottle?.Dispose();
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(ownDevice); }
    }

    private static X509Certificate2 LoadDeviceCertificate(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        // Device certificates are self-signed identities pinned by settings.json;
        // Windows chain trust is not their authentication mechanism.
        var certificate = FindDeviceCertificate(store.Certificates, thumbprint);
        return certificate ?? throw new CryptographicException("The configured device certificate is unavailable or invalid.");
    }

    internal static X509Certificate2? FindDeviceCertificate(X509Certificate2Collection certificates, string thumbprint)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);
        return certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
            .OfType<X509Certificate2>().FirstOrDefault(item => item.HasPrivateKey && PeerTls.IsDeviceCertificate(item));
    }

    private FolderRuntime[] SnapshotFolders()
    { lock (folderGate) return folders.ToArray(); }

    internal IReadOnlyList<FolderDescriptor> ListFolders() => state.ListFolders().ToArray();

    private FolderDescriptor FindDescriptor(string selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        var descriptors = state.ListFolders().ToArray();
        var byId = descriptors.FirstOrDefault(item =>
            string.Equals(Convert.ToHexString(item.Id), selector.Trim(), StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        string path;
        try { path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selector)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new KeyNotFoundException("Unknown folder selector."); }
        return descriptors.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("Unknown folder selector.");
    }

    internal FolderDescriptor GetFolderDescriptor(string selector) => FindDescriptor(selector);

    internal async Task<FolderDescriptor> PauseFolderAsync(string selector, string requestingActorSid,
        CancellationToken cancellationToken)
    {
        using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        await addGate.WaitAsync(requestStop.Token).ConfigureAwait(false);
        try
        {
            FolderDescriptor descriptor = FindDescriptor(selector);
            if (descriptor.Paused) return descriptor;
            await state.UpdateFolderAsync(descriptor.Id, descriptor.Settings, true, requestingActorSid,
                requestStop.Token).ConfigureAwait(false);
            FolderRuntime folder = ResolveFolder(selector);
            lock (folderGate) folders.Remove(folder);
            try
            {
                if (network is not null) await network.RefreshFolderMembershipAsync(stopping.Token).ConfigureAwait(false);
            }
            finally
            {
                try { await StopFolderSchedulerAsync(folder).ConfigureAwait(false); }
                finally
                {
                    Task? pendingRescans;
                    lock (rescanGate) pendingRescans = rescanTask;
                    try { if (pendingRescans is not null) await pendingRescans.ConfigureAwait(false); }
                    finally { await folder.DisposeAsync().ConfigureAwait(false); }
                }
            }
            diagnostics?.TryWrite(new("service.folder_paused", "folder_host") { ItemsChanged = 1 });
            return descriptor with { Paused = true };
        }
        finally { addGate.Release(); }
    }

    internal async Task<FolderDescriptor> ResumeFolderAsync(string selector, string requestingActorSid,
        CancellationToken cancellationToken)
    {
        using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        await addGate.WaitAsync(requestStop.Token).ConfigureAwait(false);
        FolderRuntime? folder = null;
        bool attached = false;
        try
        {
            FolderDescriptor descriptor = FindDescriptor(selector);
            if (!descriptor.Paused) return descriptor;
            folder = await OpenFolderRuntimeAsync(descriptor, null, requestStop.Token).ConfigureAwait(false);
            await state.UpdateFolderAsync(descriptor.Id, descriptor.Settings, false, requestingActorSid,
                requestStop.Token).ConfigureAwait(false);
            lock (folderGate) folders.Add(folder);
            attached = true;
            StartFolderScheduler(folder);
            if (network is null && certificate is not null && deviceSettings is not null)
            {
                network = await PeerNetworkRuntime.StartAsync(certificate, deviceId, () => SnapshotFolders(),
                    deviceSettings, status, (folderId, remoteDevice) => RecordPending(state, folderId, remoteDevice, diagnostics),
                    diagnostics, requestStop.Token).ConfigureAwait(false);
                _ = ObserveComponentAsync(network.Completion, "service.peer_accept_failed");
            }
            else network?.RequestNetworkRecovery("folder_resumed");
            diagnostics?.TryWrite(new("service.folder_resumed", "folder_host") { ItemsChanged = 1 });
            folder = null;
            return descriptor with { Paused = false };
        }
        catch
        {
            if (folder is not null)
            {
                if (attached) { lock (folderGate) folders.Remove(folder); }
                await StopFolderSchedulerAsync(folder).ConfigureAwait(false);
                await folder.DisposeAsync().ConfigureAwait(false);
                var descriptor = FindDescriptor(selector);
                if (!descriptor.Paused)
                    await state.UpdateFolderAsync(descriptor.Id, descriptor.Settings, true, requestingActorSid,
                        CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        finally { addGate.Release(); }
    }

    internal async Task RemoveFolderAsync(string selector, string requestingActorSid,
        CancellationToken cancellationToken)
    {
        FolderDescriptor descriptor = FindDescriptor(selector);
        if (!descriptor.Paused)
            descriptor = await PauseFolderAsync(selector, requestingActorSid, cancellationToken).ConfigureAwait(false);
        using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        await addGate.WaitAsync(requestStop.Token).ConfigureAwait(false);
        try
        {
            await state.RemoveFolderAsync(descriptor.Id, requestingActorSid, requestStop.Token).ConfigureAwait(false);
            RemoveFolderMarkerIfOwned(descriptor.Path, descriptor.Id);
            diagnostics?.TryWrite(new("service.folder_removed", "folder_host") { ItemsChanged = 1 });
        }
        finally { addGate.Release(); }
    }

    internal async Task<int> PauseAllAsync(long durationSeconds, string requestingActorSid,
        CancellationToken cancellationToken)
    {
        if (durationSeconds is < 0 or > 30 * 86400) throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        string[] active = state.ListFolders().Where(item => !item.Paused)
            .Select(item => Convert.ToHexString(item.Id)).ToArray();
        foreach (string id in active)
            await PauseFolderAsync(id, requestingActorSid, cancellationToken).ConfigureAwait(false);
        lock (pauseGate)
        {
            string[] pending = autoResumeIds;
            autoResumeStop?.Cancel();
            if (durationSeconds > 0)
            {
                var stop = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                string[] resume = active.Concat(pending).Distinct(StringComparer.Ordinal).ToArray();
                autoResumeStop = stop;
                autoResumeIds = resume;
                autoResumeTask = ResumeAfterDelayAsync(resume, durationSeconds, requestingActorSid, stop);
            }
            else { autoResumeStop = null; autoResumeTask = null; autoResumeIds = []; }
        }
        return active.Length;
    }

    private async Task ResumeAfterDelayAsync(string[] ids, long seconds, string actor, CancellationTokenSource stop)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), stop.Token).ConfigureAwait(false);
            foreach (string id in ids)
            {
                stop.Token.ThrowIfCancellationRequested();
                try { await ResumeFolderAsync(id, actor, stop.Token).ConfigureAwait(false); }
                catch (KeyNotFoundException) { }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            diagnostics?.TryWrite(new("service.auto_resume_failed", "folder_host")
                { Level = DiagnosticLevel.Error, ErrorCode = error.GetType().Name, HResult = error.HResult });
        }
        finally
        {
            lock (pauseGate)
                if (ReferenceEquals(autoResumeStop, stop))
                { autoResumeStop = null; autoResumeTask = null; autoResumeIds = []; }
            stop.Dispose();
        }
    }

    internal IReadOnlyList<PendingApproval> ListPendingDevices()
        => state.PendingApprovals.ReadAll().OrderBy(row => Convert.ToHexString(row.FolderId), StringComparer.Ordinal)
            .ThenBy(row => Convert.ToHexString(row.DeviceId), StringComparer.Ordinal).ToArray();

    internal Task<int> AuditControlUserAuthorizationAsync(string actorSid, string targetSid,
        CancellationToken cancellationToken)
        => state.AuditControlUserAuthorizationAsync(actorSid, targetSid, cancellationToken);

    internal Task<int> AuditDeviceSettingsUpdateRequestAsync(string actorSid, CancellationToken cancellationToken)
        => state.AuditDeviceSettingsUpdateRequestAsync(actorSid, cancellationToken);

    internal async Task DecideDeviceAsync(string selector, byte[] remoteDevice, DeviceApproval decision,
        string requestingActorSid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remoteDevice);
        if (remoteDevice.Length != 32 || decision is not (DeviceApproval.Approved or DeviceApproval.Blocked or DeviceApproval.Unknown))
            throw new ArgumentException("Invalid device decision.");
        using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        await addGate.WaitAsync(requestStop.Token).ConfigureAwait(false);
        try
        {
            var folder = ResolveFolder(selector);
            byte[] folderId = folder.Keys.FolderId();
            string action = decision switch
            {
                DeviceApproval.Approved => "device-approve",
                DeviceApproval.Blocked => "device-block",
                _ => "device-deny",
            };
            await state.RecordDeviceDecisionAsync(folderId, remoteDevice, action, requestingActorSid,
                requestStop.Token).ConfigureAwait(false);
            try
            {
                if (decision is not DeviceApproval.Unknown)
                {
                    DeviceRow row = folder.Index.Devices.Find(remoteDevice)
                        ?? new DeviceRow(remoteDevice.ToArray());
                    await folder.Index.Devices.UpsertAsync([row with { Approval = decision, Pinned = true }],
                        requestStop.Token).ConfigureAwait(false);
                    network?.RequestNetworkRecovery("device_decision");
                }
            }
            catch
            {
                await state.RecordPendingApprovalAsync(folderId, remoteDevice, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            diagnostics?.TryWrite(new("service.device_decision", "folder_host")
                { ErrorCode = action });
        }
        finally { addGate.Release(); }
    }

    private static void RecordPending(StateStore state, byte[] folderId, byte[] remoteDevice,
        DiagnosticJournal? diagnostics)
    {
        try
        {
            if (state.PendingApprovals.Find(folderId, remoteDevice) is not null) return;
            if (!state.RecordPendingApprovalAsync(folderId, remoteDevice).GetAwaiter().GetResult())
                diagnostics?.TryWrite(new("service.pending_device_limit", "peer_network")
                    { Level = DiagnosticLevel.Warning });
        }
        catch (Exception error)
        {
            diagnostics?.TryWrite(new("service.pending_device_record_failed", "peer_network")
                { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult });
        }
    }

    internal FolderSettings GetFolderSettings(string selector)
    {
        return FindDescriptor(selector).Settings;
    }

    internal async Task<(FolderSettings Settings, bool RestartRequired)> UpdateFolderSettingsAsync(
        string selector, FolderSettings updated, string requestingActorSid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updated);
        updated.Validate();
        using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        await addGate.WaitAsync(requestStop.Token).ConfigureAwait(false);
        try
        {
            FolderDescriptor descriptor = FindDescriptor(selector);
            FolderRuntime? folder = descriptor.Paused ? null : ResolveFolder(selector);
            FolderSettings prior = folder?.Settings ?? descriptor.Settings;
            bool restart = folder is not null && !string.Equals((prior with { PredefinedHosts = updated.PredefinedHosts }).ToJson(),
                updated.ToJson(), StringComparison.Ordinal);
            await state.UpdateFolderAsync(descriptor.Id, updated, descriptor.Paused, requestingActorSid,
                requestStop.Token).ConfigureAwait(false);
            folder?.UpdateSettings(restart ? prior with { PredefinedHosts = updated.PredefinedHosts } : updated);
            if (network is not null && folder is not null
                && !prior.PredefinedHosts.SequenceEqual(updated.PredefinedHosts, StringComparer.OrdinalIgnoreCase))
                await network.RefreshPredefinedHostsAsync(requestStop.Token).ConfigureAwait(false);
            diagnostics?.TryWrite(new("service.folder_settings_updated", "folder_host")
                { ItemsChanged = 1 });
            return (updated, restart);
        }
        finally { addGate.Release(); }
    }

    internal async Task<FileApplyOutcome> RestoreArchivedAsync(string selector, long archiveId,
        bool asCopy, string copyPath, CancellationToken cancellationToken)
    {
        FolderRuntime folder = ResolveFolder(selector);
        var applier = new FileApplier(folder.Path, folder.Index, folder.Keys, deviceId,
            folder.Settings, diagnostics);
        var restorer = new ArchiveRestorer(applier);
        FileApplyOutcome outcome = asCopy
            ? await restorer.RestoreAsCopyAsync(archiveId, copyPath, cancellationToken).ConfigureAwait(false)
            : await restorer.RestoreAsync(archiveId, cancellationToken).ConfigureAwait(false);
        if (outcome == FileApplyOutcome.Applied) RequestRescans("archive_restore");
        return outcome;
    }

    internal async Task<string[]> SetPredefinedHostsAsync(string selector, IEnumerable<string> hosts,
        string requestingActorSid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestingActorSid);
        using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        await addGate.WaitAsync(requestStop.Token).ConfigureAwait(false);
        try
        {
            FolderRuntime runtime = ResolveFolder(selector);
            string[] hostArray = hosts.ToArray();
            byte[] folderId = runtime.Keys.FolderId();
            try
            {
                FolderDescriptor descriptor = state.ListFolders().Single(item =>
                    CryptographicOperations.FixedTimeEquals(item.Id, folderId));
                FolderSettings updated = descriptor.Settings with { PredefinedHosts = hostArray };
                updated.Validate();
                await state.UpdateFolderAsync(folderId, updated, descriptor.Paused, requestingActorSid,
                    requestStop.Token).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(folderId); }

            runtime.UpdateSettings(runtime.Settings with { PredefinedHosts = hostArray });
            if (network is not null)
                await network.RefreshPredefinedHostsAsync(requestStop.Token).ConfigureAwait(false);
            return hostArray;
        }
        finally { addGate.Release(); }
    }

    internal Task<FolderKeys> ReadFolderKeysAsync(string selector, string requestingActorSid,
        CancellationToken cancellationToken = default)
    {
        FolderRuntime folder = ResolveFolder(selector);
        return state.ReadFolderKeysAsync(folder.Keys.FolderId(), requestingActorSid, cancellationToken);
    }

    internal FolderRuntime ResolveFolder(string selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        string value = selector.Trim();
        FolderRuntime? byId;
        lock (folderGate)
            byId = folders.FirstOrDefault(folder => string.Equals(Convert.ToHexString(folder.Keys.FolderId()), value,
                StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        string fullPath;
        try { fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new KeyNotFoundException("Unknown folder selector."); }
        lock (folderGate)
            return folders.FirstOrDefault(folder => string.Equals(folder.Path, fullPath, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException("Unknown folder selector.");
    }

    internal IReadOnlyList<DeviceRow> ListPeers(string selector)
        => ResolveFolder(selector).Index.Devices.ReadAll().OrderBy(row => Convert.ToHexString(row.DeviceId), StringComparer.Ordinal).ToArray();

    internal IEnumerable<LocalFileRow> ReadIndex(string selector)
        => ResolveFolder(selector).Index.LocalFiles.ReadAll();

    internal async Task<FolderDescriptor> AddFolderAsync(FolderKeys keys, string path,
        string? requestingActorSid = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Folder root must be absolute.", nameof(path));
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The selected folder does not exist.");
        using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        cancellationToken = requestStop.Token;
        await addGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        FolderRuntime? runtime = null;
        FolderDescriptor? descriptor = null;
        bool markerCreated = false;
        try
        {
            if (state.ListFolders().Any(item => string.Equals(item.Path, root, StringComparison.OrdinalIgnoreCase)))
                throw new FolderAlreadyRegisteredException();
            string auditActor = requestingActorSid ?? actorSid;
            descriptor = await state.RegisterFolderAsync(keys, root, new FolderSettings(), auditActor,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            markerCreated = EnsureFolderMarker(root, descriptor.Id);
            runtime = await OpenFolderRuntimeAsync(descriptor, keys, cancellationToken).ConfigureAwait(false);
            lock (folderGate) folders.Add(runtime);

            if (network is null && certificate is not null && deviceSettings is not null)
            {
                network = await PeerNetworkRuntime.StartAsync(certificate, deviceId, () => SnapshotFolders(),
                    deviceSettings, status, (folderId, remoteDevice) => RecordPending(state, folderId, remoteDevice, diagnostics),
                    diagnostics, cancellationToken).ConfigureAwait(false);
                _ = ObserveComponentAsync(network.Completion, "service.peer_accept_failed");
            }
            StartFolderScheduler(runtime);
            diagnostics?.TryWrite(new("service.folder_added", "folder_host") { ItemsChanged = 1 });
            runtime = null;
            return descriptor;
        }
        catch
        {
            if (descriptor is not null)
            {
                FolderRuntime? added = null;
                lock (folderGate)
                {
                    added = folders.FirstOrDefault(folder => folder.Keys.FolderId().AsSpan().SequenceEqual(descriptor.Id));
                    if (added is not null) folders.Remove(added);
                }
                if (added is not null)
                {
                    await StopFolderSchedulerAsync(added).ConfigureAwait(false);
                    await added.DisposeAsync().ConfigureAwait(false);
                }
                else if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
                await state.RemoveFolderAsync(descriptor.Id, requestingActorSid ?? actorSid, CancellationToken.None).ConfigureAwait(false);
            }
            else if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
            if (markerCreated && descriptor is not null) RemoveFolderMarkerIfOwned(root, descriptor.Id);
            throw;
        }
        finally { addGate.Release(); }
    }

    private async Task<FolderRuntime> OpenFolderRuntimeAsync(FolderDescriptor descriptor, FolderKeys? suppliedKeys,
        CancellationToken cancellationToken)
    {
        FolderIndex? index = null; FolderKeys? keys = suppliedKeys; Scanner? scanner = null;
        TransportFolder? transport = null; IDisposable? registration = null;
        try
        {
            string id = Convert.ToHexString(descriptor.Id);
            string indexPath = Path.Combine(Path.GetDirectoryName(statePath)!, "folders", id + ".db");
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            index = await FolderIndex.OpenAsync(indexPath, cancellationToken).ConfigureAwait(false);
            keys ??= await state.ReadFolderKeysAsync(descriptor.Id, actorSid, cancellationToken).ConfigureAwait(false);
            if (!keys.CanRead) throw new InvalidOperationException("Encrypted-only folders require the opaque engine.");
            var ignore = new IgnoreMatcher(descriptor.Settings.IgnoreRules);
            transport = new TransportFolder(descriptor.Path, index, keys, deviceId, descriptor.Settings,
                timeProvider: null, journal: diagnostics, uploadThrottle: uploadThrottle,
                downloadThrottle: downloadThrottle, ignore: ignore);
            scanner = transport.Scanner;
            await transport.RecoverBeforeExternalScanAsync(cancellationToken).ConfigureAwait(false);
            registration = status.RegisterFolder(transport, scanner);
            var result = new FolderRuntime(index, keys, scanner, transport, registration, descriptor.Settings, descriptor.Path);
            index = null; keys = null; scanner = null; transport = null; registration = null;
            return result;
        }
        finally
        {
            registration?.Dispose();
            if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
            if (index is not null) await index.DisposeAsync().ConfigureAwait(false);
            keys?.Dispose();
        }
    }

    private static bool EnsureFolderMarker(string root, byte[] folderId)
    {
        string privateDirectory = Path.Combine(root, ".psp");
        Directory.CreateDirectory(privateDirectory);
        if ((File.GetAttributes(privateDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The folder metadata directory is a reparse point.");
        string marker = Path.Combine(privateDirectory, "id");
        if (File.Exists(marker))
        {
            byte[] existing = File.ReadAllBytes(marker);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(existing, folderId))
                    throw new IOException("The folder identity marker does not match the registered key.");
            }
            finally { CryptographicOperations.ZeroMemory(existing); }
            return false;
        }
        try
        {
            using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, FileOptions.WriteThrough);
            stream.Write(folderId);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(marker))
        {
            byte[] existing = File.ReadAllBytes(marker);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(existing, folderId))
                    throw new IOException("The folder identity marker does not match the registered key.");
            }
            finally { CryptographicOperations.ZeroMemory(existing); }
            return false;
        }
        try { File.SetAttributes(privateDirectory, File.GetAttributes(privateDirectory) | FileAttributes.Hidden | FileAttributes.System); }
        catch (PlatformNotSupportedException) { }
        return true;
    }

    private static void RemoveFolderMarkerIfOwned(string root, byte[] folderId)
    {
        string marker = Path.Combine(root, ".psp", "id");
        try
        {
            byte[] existing = File.ReadAllBytes(marker);
            try { if (CryptographicOperations.FixedTimeEquals(existing, folderId)) File.Delete(marker); }
            finally { CryptographicOperations.ZeroMemory(existing); }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void StartFolderScheduler(FolderRuntime folder)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        var scheduler = new RescanScheduler([new(folder.Scanner, folder.Interval, Coordinator: folder.Transport)], journal: diagnostics);
        Task task = scheduler.RunAsync(stop.Token);
        folderSchedulers.Add(folder, (stop, task));
        _ = ObserveComponentAsync(task, "service.folder_scheduler_stopped", stop.Token);
    }

    private async Task StopFolderSchedulerAsync(FolderRuntime folder)
    {
        if (!folderSchedulers.Remove(folder, out var entry)) return;
        entry.Stop.Cancel();
        try { await entry.Task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { entry.Stop.Dispose(); }
    }

    private async Task ObserveComponentAsync(Task component, string eventName, CancellationToken? expectedStop = null)
    {
        try
        {
            await component.ConfigureAwait(false);
            if (!stopping.IsCancellationRequested && expectedStop?.IsCancellationRequested != true)
                completion.TrySetException(new InvalidOperationException("A supervised service component stopped unexpectedly."));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested || expectedStop?.IsCancellationRequested == true) { }
        catch (Exception error)
        {
            diagnostics?.TryWrite(new(eventName, "folder_host")
                { Level = DiagnosticLevel.Error, ErrorCode = error.GetType().Name, HResult = error.HResult });
            if (!stopping.IsCancellationRequested && expectedStop?.IsCancellationRequested != true) completion.TrySetException(error);
        }
    }

    private void RequestRescans(string reason)
    {
        lock (rescanGate)
        {
            if (Volatile.Read(ref disposed) != 0 || stopping.IsCancellationRequested) return;
            requestedRescanGeneration = checked(requestedRescanGeneration + 1);
            pendingRescanReason = reason;
            if (rescanTask is null || rescanTask.IsCompleted)
                rescanTask = Task.Run(ScanRequestedGenerationsAsync);
        }
    }

    private void OnResumed()
    {
        network?.RequestNetworkRecovery("power_resume");
        RequestRescans("power_resume");
    }

    private void OnNetworkChanged()
    {
        network?.RequestNetworkRecovery("network_change");
        RequestRescans("network_change");
    }

    private async Task ScanRequestedGenerationsAsync()
    {
        while (true)
        {
            long generation;
            string reason;
            lock (rescanGate)
            {
                generation = requestedRescanGeneration;
                reason = pendingRescanReason;
            }
            await ScanAllAfterResumeAsync(reason).ConfigureAwait(false);
            lock (rescanGate)
            {
                if (requestedRescanGeneration != generation) continue;
                rescanTask = null;
                return;
            }
        }
    }

    private async Task ScanAllAfterResumeAsync(string reason)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            foreach (var folder in SnapshotFolders())
            {
                var folderTimer = System.Diagnostics.Stopwatch.StartNew();
                folder.Transport.RequestScan();
                var result = await folder.Transport.RunPassAsync(stopping.Token).ConfigureAwait(false);
                if (result.ScansCompleted == 0) throw new IOException("Resume scan was deferred.");
                diagnostics?.TryWrite(new("service.resume_scan_complete", reason)
                    { ItemsVisited = result.ScannedEntries, ItemsDeferred = result.ScanProblems,
                        ElapsedMilliseconds = folderTimer.Elapsed.TotalMilliseconds });
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            diagnostics?.TryWrite(new("service.resume_scan_failed", reason)
                { Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult,
                    ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds });
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        status.Resumed -= OnResumed;
        status.NetworkChanged -= OnNetworkChanged;
        stopping.Cancel();
        Task? pendingAutoResume;
        lock (pauseGate) { autoResumeStop?.Cancel(); pendingAutoResume = autoResumeTask; }
        if (pendingAutoResume is not null) await pendingAutoResume.ConfigureAwait(false);
        await addGate.WaitAsync().ConfigureAwait(false);
        Exception? networkFailure = null;
        try
        {
        try { if (network is not null) await network.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { networkFailure = error; }
        Task? pendingRescans;
        lock (rescanGate) pendingRescans = rescanTask;
        foreach (var folder in folderSchedulers.Keys.ToArray())
            try { await StopFolderSchedulerAsync(folder).ConfigureAwait(false); }
            catch (Exception error)
            {
                diagnostics?.TryWrite(new("service.folder_scheduler_failed", "shutdown")
                    { Level = DiagnosticLevel.Error, ErrorCode = error.GetType().Name, HResult = error.HResult });
            }
        if (pendingRescans is not null)
            try { await pendingRescans.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        foreach (var folder in SnapshotFolders().Reverse()) await folder.DisposeAsync().ConfigureAwait(false);
        await state.DisposeAsync().ConfigureAwait(false);
        uploadThrottle?.Dispose(); downloadThrottle?.Dispose();
        certificate?.Dispose();
        CryptographicOperations.ZeroMemory(deviceId);
        }
        finally
        {
            addGate.Release();
            addGate.Dispose();
            stopping.Dispose();
        }
        if (networkFailure is not null) ExceptionDispatchInfo.Capture(networkFailure).Throw();
    }

    internal sealed class FolderRuntime(FolderIndex index, FolderKeys keys, Scanner scanner, TransportFolder transport,
        IDisposable registration, FolderSettings settings, string path) : IAsyncDisposable
    {
        private FolderSettings settings = settings;
        public FolderIndex Index { get; } = index;
        public FolderKeys Keys { get; } = keys;
        public Scanner Scanner { get; } = scanner;
        public TransportFolder Transport { get; } = transport;
        public FolderSettings Settings => Volatile.Read(ref settings);
        public void UpdateSettings(FolderSettings value) => Volatile.Write(ref settings, value);
        public string Path { get; } = path;
        public int Interval => Settings.RescanIntervalSeconds;
        public async ValueTask DisposeAsync()
        {
            registration.Dispose();
            await Transport.DisposeAsync().ConfigureAwait(false);
            await Index.DisposeAsync().ConfigureAwait(false);
            Keys.Dispose();
        }
    }

    internal sealed class FolderAlreadyRegisteredException() : InvalidOperationException("This folder path is already registered.");
}
