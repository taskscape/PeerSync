using System.Security.Authentication;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.Collections;
using PeerSync.Core.Diagnostics;
using PeerSync.Net.Quic;
using PeerSync.Protocol;
using PeerSync.Protocol.Framing;
using PeerSync.Protocol.Handshake;
using PeerSync.Protocol.Validation;
using PeerSync.Protocol.Wire;

namespace PeerSync.Net.Security;

/// <summary>Transport-independent inputs for the certificate-bound PeerSync handshake.</summary>
internal sealed class PeerHandshakeOptions
{
    internal required Func<Hello> CreateHello { get; init; }
    internal required IReadOnlyList<PeerFolderRegistration> Folders { get; init; }
    internal required string DeviceName { get; init; }
    internal required string ClientVersion { get; init; }
    internal DiagnosticJournal? Diagnostics { get; init; }
    internal Action<byte[], MessageFailure>? AddStrike { get; init; }
    internal required TimeSpan RequestTimeout { get; init; }
    internal required DiagnosticTransport Transport { get; init; }
}

/// <summary>Implements the same Hello transcript and direction-bound folder proof on each transport.</summary>
internal static class PeerHandshake
{
    internal static async ValueTask<(PeerAuthenticationContext Authentication, Hello RemoteHello,
        List<AuthenticatedFolder> Folders)> AuthenticateAsync(PeerFrameStream control,
        PeerHandshakeOptions options, ReadOnlyMemory<byte> localDeviceId,
        ReadOnlyMemory<byte> remoteDeviceId, CancellationToken cancellationToken)
    {
        Hello localHello = CreateLocalHello(options, localDeviceId.Span);
        Hello remoteHello = await ExchangeInitialHelloAsync(control, localHello, options, remoteDeviceId, cancellationToken).ConfigureAwait(false);
        var authentication = new PeerAuthenticationContext(localDeviceId.Span, remoteDeviceId.Span,
            localHello.Nonce.Span, remoteHello.Nonce.Span);
        options.Diagnostics?.TryWrite(new("transport.hello_validated", options.Transport == DiagnosticTransport.Quic
            ? "quic_handshake" : "tcp_handshake")
            { SessionId = authentication.SessionId, Transport = options.Transport, ItemsVisited = 2 });
        List<AuthenticatedFolder> folders = await AuthenticateFoldersAsync(control, options, authentication,
            remoteHello, cancellationToken).ConfigureAwait(false);
        options.Diagnostics?.TryWrite(new("transport.folder_auth_completed", options.Transport == DiagnosticTransport.Quic
            ? "quic_handshake" : "tcp_handshake")
            { SessionId = authentication.SessionId, Transport = options.Transport,
                ItemsVisited = remoteHello.FolderIds.Count, ItemsChanged = folders.Count });
        return (authentication, remoteHello, folders);
    }

    internal static Hello CreateLocalHello(PeerHandshakeOptions options, ReadOnlySpan<byte> deviceId)
    {
        Hello hello = options.CreateHello().Clone();
        hello.DeviceId = ByteString.CopyFrom(deviceId);
        hello.DeviceName = options.DeviceName;
        hello.ClientVersion = options.ClientVersion;
        hello.Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32));
        hello.RequestId = NewRequestId(); hello.Response = false; hello.FolderIds.Clear();
        foreach (var folder in options.Folders.OrderBy(folder => Convert.ToHexString(folder.Keys.FolderId()), StringComparer.Ordinal))
            hello.FolderIds.Add(ByteString.CopyFrom(folder.Keys.FolderId()));
        MessageValidator.Validate(hello);
        return hello;
    }

    private static async ValueTask<Hello> ExchangeInitialHelloAsync(PeerFrameStream control,
        Hello localRequest, PeerHandshakeOptions options, ReadOnlyMemory<byte> certificateDeviceId, CancellationToken token)
    {
        await control.WriteAsync(localRequest, token).ConfigureAwait(false);
        Hello remoteRequest = await ReadMessageAsync<Hello>(control, options, certificateDeviceId, token).ConfigureAwait(false);
        if (remoteRequest.Response || remoteRequest.RequestId == 0
            || !CryptographicOperations.FixedTimeEquals(remoteRequest.DeviceId.Span, certificateDeviceId.Span))
            throw new AuthenticationException("The Hello identity or request does not match the TCP peer certificate.");

        Hello response = localRequest.Clone();
        response.RequestId = remoteRequest.RequestId;
        response.Response = true;
        await control.WriteAsync(response, token).ConfigureAwait(false);

        Hello remoteResponse = await ReadMessageAsync<Hello>(control, options, certificateDeviceId, token).ConfigureAwait(false);
        if (!remoteResponse.Response || remoteResponse.RequestId != localRequest.RequestId
            || !CryptographicOperations.FixedTimeEquals(remoteResponse.DeviceId.Span, certificateDeviceId.Span)
            || !CryptographicOperations.FixedTimeEquals(remoteResponse.Nonce.Span, remoteRequest.Nonce.Span)
            || remoteResponse.ProtocolVersion != remoteRequest.ProtocolVersion
            || remoteResponse.DeviceName != remoteRequest.DeviceName
            || remoteResponse.ClientVersion != remoteRequest.ClientVersion
            || remoteResponse.Capabilities != remoteRequest.Capabilities
            || remoteResponse.UtcTicks != remoteRequest.UtcTicks
            || !SameFolders(remoteResponse.FolderIds, remoteRequest.FolderIds))
            throw new AuthenticationException("The Hello response does not match the authenticated peer request.");
        return remoteResponse;
    }

    private static async ValueTask<List<AuthenticatedFolder>> AuthenticateFoldersAsync(PeerFrameStream control,
        PeerHandshakeOptions options, PeerAuthenticationContext authentication, Hello remoteHello, CancellationToken token)
    {
        var remoteFolders = remoteHello.FolderIds.Select(id => Convert.ToHexString(id.Span)).ToHashSet(StringComparer.Ordinal);
        var common = options.Folders.Where(folder => remoteFolders.Contains(Convert.ToHexString(folder.Keys.FolderId())))
            .OrderBy(folder => Convert.ToHexString(folder.Keys.FolderId()), StringComparer.Ordinal).ToArray();
        var pending = new Dictionary<ulong, PeerFolderRegistration>();
        foreach (var folder in common)
        {
            ulong requestId = NewRequestId();
            pending.Add(requestId, folder);
            await control.WriteAsync(FolderProof.Create(folder.Keys, authentication, requestId), token).ConfigureAwait(false);
        }

        var localDecisions = new Dictionary<string, AuthDecision>(StringComparer.Ordinal);
        var seenRemoteFolders = new HashSet<string>(StringComparer.Ordinal);
        var seenRemoteRequests = new HashSet<ulong>();
        bool invalidRemoteProof = false;
        for (int index = 0; index < common.Length; index++)
        {
            FolderAuth remoteProof = await ReadMessageAsync<FolderAuth>(control, options,
                authentication.RemoteDeviceId, token).ConfigureAwait(false);
            string folderId = Convert.ToHexString(remoteProof.FolderId.Span);
            PeerFolderRegistration? registration = common.FirstOrDefault(folder =>
                CryptographicOperations.FixedTimeEquals(folder.Keys.FolderId(), remoteProof.FolderId.Span));
            if (registration is null || !seenRemoteFolders.Add(folderId) || !seenRemoteRequests.Add(remoteProof.RequestId))
                throw new AuthenticationException("The peer sent an unexpected or duplicate folder proof.");

            bool proofValid = FolderProof.TryVerifyRemote(remoteProof, registration.Keys, authentication, out _);
            AuthDecision decision = SafeAuthorize(registration, authentication.RemoteDeviceId);
            if (!proofValid)
            {
                invalidRemoteProof = true;
                decision = AuthDecision.Denied;
                options.AddStrike?.Invoke(authentication.RemoteDeviceId, MessageFailure.InvalidAuthentication);
                options.Diagnostics?.TryWrite(new("transport.folder_auth_rejected", options.Transport == DiagnosticTransport.Quic
                    ? "quic_handshake" : "tcp_handshake")
                    { SessionId = authentication.SessionId, Transport = options.Transport,
                        Level = DiagnosticLevel.Warning, ErrorCode = "invalid_remote_proof" });
            }
            localDecisions.Add(folderId, decision);
            LogFolderDecision(options, authentication.SessionId, "local_decision", decision);
            var result = new FolderAuthResult
            {
                FolderId = remoteProof.FolderId,
                RequestId = remoteProof.RequestId,
                Decision = decision,
                Reason = decision == AuthDecision.Allowed ? CloseReason.Unspecified : CloseReason.NotAuthorized,
            };
            if (decision == AuthDecision.Allowed)
                result.ServerProof = FolderProof.Create(registration.Keys, authentication, remoteProof.RequestId);
            MessageValidator.Validate(result);
            await control.WriteAsync(result, token).ConfigureAwait(false);
        }
        if (invalidRemoteProof) throw new AuthenticationException("A remote folder authentication proof was invalid.");

        var authenticated = new List<AuthenticatedFolder>();
        try
        {
            var seenResults = new HashSet<ulong>();
            for (int index = 0; index < common.Length; index++)
            {
                FolderAuthResult result = await ReadMessageAsync<FolderAuthResult>(control, options,
                    authentication.RemoteDeviceId, token).ConfigureAwait(false);
                if (!pending.TryGetValue(result.RequestId, out var registration) || !seenResults.Add(result.RequestId)
                    || !CryptographicOperations.FixedTimeEquals(registration.Keys.FolderId(), result.FolderId.Span))
                    throw new AuthenticationException("The peer sent an unmatched folder authorization result.");

                string folderId = Convert.ToHexString(result.FolderId.Span);
                LogFolderDecision(options, authentication.SessionId, "remote_decision", result.Decision);
                bool remoteProofValid = result.Decision == AuthDecision.Allowed
                    && result.ServerProof is not null
                    && FolderProof.TryVerifyRemote(result.ServerProof, registration.Keys, authentication, out _);
                if (result.Decision == AuthDecision.Allowed && !remoteProofValid)
                {
                    options.AddStrike?.Invoke(authentication.RemoteDeviceId, MessageFailure.InvalidAuthentication);
                    throw new AuthenticationException("The peer's allowed result has an invalid folder proof.");
                }
                if (result.Decision == AuthDecision.Allowed && localDecisions.GetValueOrDefault(folderId) == AuthDecision.Allowed)
                {
                    IPeerRequestHandler handler = registration.CreateHandler(authentication.RemoteDeviceId.ToArray())
                        ?? throw new InvalidOperationException("The authenticated folder handler factory returned null.");
                    authenticated.Add(new AuthenticatedFolder(registration.Keys, result.ServerProof!.Access, handler));
                }
            }
            return authenticated;
        }
        catch
        {
            foreach (var folder in authenticated)
                try { await folder.Handler.DisposeAsync().ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private static AuthDecision SafeAuthorize(PeerFolderRegistration registration, byte[] remoteDevice)
    {
        AuthDecision decision = registration.Authorize(remoteDevice.ToArray());
        return decision is AuthDecision.Allowed or AuthDecision.Pending or AuthDecision.Denied
            ? decision : AuthDecision.Denied;
    }

    private static void LogFolderDecision(PeerHandshakeOptions options, Guid sessionId, string source, AuthDecision decision)
    {
        string outcome = decision switch { AuthDecision.Allowed => "allowed", AuthDecision.Pending => "pending", _ => "denied" };
        options.Diagnostics?.TryWrite(new($"transport.folder_auth_{outcome}", options.Transport == DiagnosticTransport.Quic
            ? "quic_handshake" : "tcp_handshake")
            { SessionId = sessionId, Transport = options.Transport,
                ErrorCode = source == "local_decision" ? $"local_policy_{outcome}" : $"remote_policy_{outcome}" });
    }

    private static async ValueTask<T> ReadMessageAsync<T>(PeerFrameStream stream, PeerHandshakeOptions options,
        ReadOnlyMemory<byte> certificateDeviceId, CancellationToken token)
        where T : class, IMessage
    {
        try
        {
            ProtocolFrame? frame = await stream.ReadAsync(token).ConfigureAwait(false);
            if (frame is null) throw new EndOfStreamException("The peer closed during authentication.");
            IMessage message = WireMessageCodec.Decode(frame.Value);
            if (message is not T typed) throw new ProtocolException(ProtocolError.UnknownMessageType);
            MessageValidator.Validate(message);
            return typed;
        }
        catch (MessageValidationException error)
        {
            await RejectRemoteMessageAsync(stream, options, certificateDeviceId, error.Failure).ConfigureAwait(false);
            throw;
        }
        catch (ProtocolException)
        {
            await RejectRemoteMessageAsync(stream, options, certificateDeviceId, MessageFailure.InvalidFraming).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask RejectRemoteMessageAsync(PeerFrameStream stream, PeerHandshakeOptions options,
        ReadOnlyMemory<byte> certificateDeviceId, MessageFailure failure)
    {
        ProtocolViolationStrike.Record(options.AddStrike, certificateDeviceId.Span, failure,
            options.Diagnostics, options.Transport, sessionId: null,
            options.Transport == DiagnosticTransport.Quic ? "quic_handshake" : "tcp_handshake");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try { await stream.WriteAsync(new Close { Reason = CloseReason.ProtocolViolation }, timeout.Token).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static bool SameFolders(RepeatedField<ByteString> left, RepeatedField<ByteString> right)
    {
        if (left.Count != right.Count) return false;
        for (int index = 0; index < left.Count; index++)
            if (!CryptographicOperations.FixedTimeEquals(left[index].Span, right[index].Span)) return false;
        return true;
    }

    private static ulong NewRequestId()
    {
        ulong id = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(sizeof(ulong)));
        return id == 0 ? 1 : id;
    }
}
