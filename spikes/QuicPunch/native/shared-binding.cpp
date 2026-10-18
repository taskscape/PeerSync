// P0.2.2(b): binding feasibility only. Not a production transport or auth implementation.
#include "msquic.h"
#include <wincrypt.h>
#include <cstdio>
#include <vector>

static const QUIC_API_TABLE* Api;
struct State {
    HANDLE Connected = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE Closed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HQUIC Accepted = nullptr;
    HQUIC Configuration = nullptr;
    QUIC_STATUS Failure = QUIC_STATUS_SUCCESS;
    ~State() { CloseHandle(Connected); CloseHandle(Closed); }
};
static QUIC_STATUS QUIC_API ConnectionCallback(HQUIC, void* context, QUIC_CONNECTION_EVENT* event) {
    auto* state = static_cast<State*>(context);
    if (event->Type == QUIC_CONNECTION_EVENT_CONNECTED) SetEvent(state->Connected);
    if (event->Type == QUIC_CONNECTION_EVENT_SHUTDOWN_INITIATED_BY_TRANSPORT)
        state->Failure = event->SHUTDOWN_INITIATED_BY_TRANSPORT.Status;
    if (event->Type == QUIC_CONNECTION_EVENT_SHUTDOWN_COMPLETE) SetEvent(state->Closed);
    return QUIC_STATUS_SUCCESS;
}
static QUIC_STATUS QUIC_API ListenerCallback(HQUIC, void* context, QUIC_LISTENER_EVENT* event) {
    auto* state = static_cast<State*>(context);
    if (event->Type != QUIC_LISTENER_EVENT_NEW_CONNECTION) return QUIC_STATUS_SUCCESS;
    state->Accepted = event->NEW_CONNECTION.Connection;
    Api->SetCallbackHandler(state->Accepted, reinterpret_cast<void*>(ConnectionCallback), state);
    return Api->ConnectionSetConfiguration(state->Accepted, state->Configuration);
}
static bool Check(QUIC_STATUS status, const char* step) {
    if (QUIC_SUCCEEDED(status)) return true;
    std::printf("{\"step\":\"%s\",\"status\":%ld,\"success\":false}\n", step, static_cast<long>(status));
    return false;
}
int wmain(int argc, wchar_t** argv) {
    if (argc != 3) return 2; // <absolute msquic.dll> <CurrentUser/My spike certificate thumbprint>
    HMODULE module = LoadLibraryW(argv[1]);
    if (!module) return 2;
    auto open = reinterpret_cast<QUIC_STATUS(QUIC_API*)(uint32_t, const void**)>(GetProcAddress(module, "MsQuicOpenVersion"));
    auto close = reinterpret_cast<void(QUIC_API*)(const QUIC_API_TABLE*)>(GetProcAddress(module, "MsQuicClose"));
    if (!open || !close || !Check(open(2, reinterpret_cast<const void**>(&Api)), "open")) return 2;
    BYTE hash[20]; DWORD hashLength = sizeof(hash);
    if (!CryptStringToBinaryW(argv[2], 0, CRYPT_STRING_HEXRAW, hash, &hashLength, nullptr, nullptr)) return 2;
    CRYPT_HASH_BLOB search{hashLength, hash};
    HCERTSTORE store = CertOpenSystemStoreW(0, L"MY");
    PCCERT_CONTEXT certificate = CertFindCertificateInStore(store, X509_ASN_ENCODING, 0, CERT_FIND_HASH, &search, nullptr);
    if (!certificate) return 2;
    HQUIC registration = nullptr, clientConfig = nullptr, serverConfig = nullptr;
    HQUIC localListener = nullptr, remoteListener = nullptr, client = nullptr;
    State localState, serverState, clientState;
    int result = 1;
    do {
        QUIC_REGISTRATION_CONFIG config{"PeerSync-P0-binding", QUIC_EXECUTION_PROFILE_LOW_LATENCY};
        if (!Check(Api->RegistrationOpen(&config, &registration), "registration")) break;
        QUIC_BUFFER alpn{5, reinterpret_cast<uint8_t*>(const_cast<char*>("psp/1"))};
        QUIC_SETTINGS settings{};
        settings.IdleTimeoutMs = 10000; settings.IsSet.IdleTimeoutMs = TRUE;
        if (!Check(Api->ConfigurationOpen(registration, &alpn, 1, &settings, sizeof(settings), nullptr, &serverConfig), "serverConfig")) break;
        if (!Check(Api->ConfigurationOpen(registration, &alpn, 1, &settings, sizeof(settings), nullptr, &clientConfig), "clientConfig")) break;
        QUIC_CREDENTIAL_CONFIG credentials{};
        credentials.Type = QUIC_CREDENTIAL_TYPE_CERTIFICATE_CONTEXT;
        credentials.CertificateContext = const_cast<CERT_CONTEXT*>(certificate);
        credentials.Flags = QUIC_CREDENTIAL_FLAG_NO_CERTIFICATE_VALIDATION | QUIC_CREDENTIAL_FLAG_REQUIRE_CLIENT_AUTHENTICATION;
        if (!Check(Api->ConfigurationLoadCredential(serverConfig, &credentials), "serverCredential")) break;
        credentials.Flags = QUIC_CREDENTIAL_FLAG_NO_CERTIFICATE_VALIDATION | QUIC_CREDENTIAL_FLAG_CLIENT;
        if (!Check(Api->ConfigurationLoadCredential(clientConfig, &credentials), "clientCredential")) break;
        localState.Configuration = serverConfig; serverState.Configuration = serverConfig;
        if (!Check(Api->ListenerOpen(registration, ListenerCallback, &localState, &localListener), "localListener")) break;
        if (!Check(Api->ListenerOpen(registration, ListenerCallback, &serverState, &remoteListener), "remoteListener")) break;
        QUIC_ADDR local{}; QuicAddrSetFamily(&local, QUIC_ADDRESS_FAMILY_INET);
        if (!Check(Api->ListenerStart(localListener, &alpn, 1, &local), "listenLocal")) break;
        if (!Check(Api->ListenerStart(remoteListener, &alpn, 1, &local), "listenRemote")) break;
        QUIC_ADDR remote{}; uint32_t size = sizeof(local);
        if (!Check(Api->GetParam(localListener, QUIC_PARAM_LISTENER_LOCAL_ADDRESS, &size, &local), "localAddress")) break;
        size = sizeof(remote);
        if (!Check(Api->GetParam(remoteListener, QUIC_PARAM_LISTENER_LOCAL_ADDRESS, &size, &remote), "remoteAddress")) break;
        uint16_t listenerPort = QuicAddrGetPort(&local);
        // Same wildcard address family as the listener binding; remote is IPv4 loopback.
        if (!Check(Api->ConnectionOpen(registration, ConnectionCallback, &clientState, &client), "connection")) break;
        BOOLEAN share = TRUE;
        if (!Check(Api->SetParam(client, QUIC_PARAM_CONN_SHARE_UDP_BINDING, sizeof(share), &share), "shareBinding")) break;
        if (!Check(Api->SetParam(client, QUIC_PARAM_CONN_LOCAL_ADDRESS, sizeof(local), &local), "bind")) break;
        if (!Check(Api->ConnectionStart(client, clientConfig, QUIC_ADDRESS_FAMILY_INET, "127.0.0.1", QuicAddrGetPort(&remote)), "start")) break;
        HANDLE events[]{clientState.Connected, clientState.Closed};
        DWORD outcome = WaitForMultipleObjects(2, events, FALSE, 15000);
        size = sizeof(local);
        if (!Check(Api->GetParam(client, QUIC_PARAM_CONN_LOCAL_ADDRESS, &size, &local), "actualLocalAddress")) break;
        bool success = outcome == WAIT_OBJECT_0 && listenerPort == QuicAddrGetPort(&local);
        std::printf("{\"option\":\"MsQuic.SHARE_UDP_BINDING\",\"topology\":\"loopback\",\"listenerPort\":%u,\"sourcePort\":%u,\"transportStatus\":%ld,\"success\":%s,\"natMatrixVerified\":false}\n",
            listenerPort, QuicAddrGetPort(&local), static_cast<long>(clientState.Failure), success ? "true" : "false");
        result = success ? 0 : 1;
    } while (false);
    if (client) { Api->ConnectionShutdown(client, QUIC_CONNECTION_SHUTDOWN_FLAG_NONE, 0); WaitForSingleObject(clientState.Closed, 3000); Api->ConnectionClose(client); }
    if (localListener) Api->ListenerClose(localListener);
    if (remoteListener) Api->ListenerClose(remoteListener);
    if (serverState.Accepted) { WaitForSingleObject(serverState.Closed, 3000); Api->ConnectionClose(serverState.Accepted); }
    if (localState.Accepted) Api->ConnectionClose(localState.Accepted);
    if (clientConfig) Api->ConfigurationClose(clientConfig);
    if (serverConfig) Api->ConfigurationClose(serverConfig);
    if (registration) Api->RegistrationClose(registration);
    CertFreeCertificateContext(certificate); CertCloseStore(store, 0);
    close(Api); FreeLibrary(module);
    return result;
}
