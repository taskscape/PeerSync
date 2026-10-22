using PeerSync.Core.Diagnostics;
using PeerSync.Net.Security;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Validation;
using PeerSync.Protocol.Wire;

namespace PeerSync.Net.Tcp;

/// <summary>Folder and identity policy for the authenticated TCP session protocol.</summary>
public sealed class TcpPeerProtocolOptions
{
    public required Func<Hello> CreateHello { get; init; }
    public required IReadOnlyList<PeerFolderRegistration> Folders { get; init; }
    public string DeviceName { get; init; } = Environment.MachineName;
    public string ClientVersion { get; init; } = "PeerSync/1";
    public DiagnosticJournal? Diagnostics { get; init; }
    public Action<byte[], MessageFailure>? AddStrike { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumConcurrentBlockRequests { get; init; } = 3;

    internal PeerHandshakeOptions HandshakeOptions => new()
    {
        CreateHello = CreateHello, Folders = Folders, DeviceName = DeviceName,
        ClientVersion = ClientVersion, Diagnostics = Diagnostics, AddStrike = AddStrike,
        RequestTimeout = RequestTimeout, Transport = DiagnosticTransport.Tcp,
    };

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(CreateHello);
        ArgumentNullException.ThrowIfNull(Folders);
        if (Folders.Count > MessageValidator.MaximumAnnouncedFolders
            || Folders.Select(folder => Convert.ToHexString(folder.Keys.FolderId())).Distinct(StringComparer.Ordinal).Count() != Folders.Count)
            throw new ArgumentException("Folder registrations must be unique and bounded.", nameof(Folders));
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (MaximumConcurrentBlockRequests is < 1 or > TcpPeerSession.MaximumDataConnections)
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentBlockRequests));
    }
}
