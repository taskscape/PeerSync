using System.Security.Cryptography.X509Certificates;
using PeerSync.Core.Diagnostics;
using PeerSync.Net.Security;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Validation;

namespace PeerSync.Net.Quic;

public sealed class QuicPeerOptions
{
    public required X509Certificate2 Certificate { get; init; }
    public required Func<Hello> CreateHello { get; init; }
    public required IReadOnlyList<PeerFolderRegistration> Folders { get; init; }
    public required Func<byte[], bool> IsDevicePinned { get; init; }
    public string DeviceName { get; init; } = Environment.MachineName;
    public string ClientVersion { get; init; } = "PeerSync/1";
    public DiagnosticJournal? Diagnostics { get; init; }
    public Action<byte[], MessageFailure>? AddStrike { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumConcurrentBlockStreams { get; init; } = 8;

    internal PeerHandshakeOptions HandshakeOptions => new()
    {
        CreateHello = CreateHello, Folders = Folders,
        DeviceName = DeviceName, ClientVersion = ClientVersion, Diagnostics = Diagnostics,
        AddStrike = AddStrike, RequestTimeout = RequestTimeout, Transport = DiagnosticTransport.Quic,
    };

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Certificate); ArgumentNullException.ThrowIfNull(CreateHello);
        ArgumentNullException.ThrowIfNull(Folders); ArgumentNullException.ThrowIfNull(IsDevicePinned);
        if (Folders.Count > MessageValidator.MaximumAnnouncedFolders || Folders.Select(f => Convert.ToHexString(f.Keys.FolderId())).Distinct().Count() != Folders.Count)
            throw new ArgumentException("Folder registrations must be unique and bounded.");
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (MaximumConcurrentBlockStreams is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentBlockStreams));
        _ = PeerTls.Server(Certificate);
    }
}
