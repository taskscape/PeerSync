using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace PeerSync.Spikes;

/// <summary>A named, persisted CNG key suitable for Schannel. Disposable loopback certificates are not added to a store.</summary>
public sealed class SpikeCertificate : IDisposable
{
    private readonly CngKey key;
    private bool keepPersisted;
    public X509Certificate2 Certificate { get; }

    public SpikeCertificate(string? serviceSid = null)
    {
        key = CngKey.Create(CngAlgorithm.ECDsaP256, "PeerSync-Spike-" + Guid.NewGuid().ToString("N"), new()
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyCreationOptions = serviceSid is null ? CngKeyCreationOptions.None : CngKeyCreationOptions.MachineKey,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
        });
        try
        {
            if (serviceSid is not null) GrantRead(key, new SecurityIdentifier(serviceSid));
            using var ecdsa = new ECDsaCng(key);
            var request = new CertificateRequest("CN=PeerSync Spike", ecdsa, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
            {
                new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2"),
            }, false));
            Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        }
        catch { key.Delete(); key.Dispose(); throw; }
    }

    public void AddToMachineStore()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(Certificate);
        keepPersisted = true;
    }

    public void RemoveFromMachineStore()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Remove(Certificate);
    }

    private static void GrantRead(CngKey key, SecurityIdentifier sid)
    {
        const CngPropertyOptions dacl = (CngPropertyOptions)4;
        byte[] raw = key.GetProperty("Security Descr", dacl).GetValue()!;
        var descriptor = new CommonSecurityDescriptor(false, false, raw, 0);
        // Drop provider defaults: only SYSTEM, Administrators and this service may use the key.
        var acl = new DiscretionaryAcl(false, false, 3);
        foreach (var principal in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), sid })
        {
            int rights = principal.Equals(sid) ? unchecked((int)0x80000000) : unchecked((int)0x10000000);
            acl.AddAccess(AccessControlType.Allow, principal, rights, InheritanceFlags.None, PropagationFlags.None);
        }
        descriptor.DiscretionaryAcl = acl;
        descriptor.SetDiscretionaryAclProtection(true, false);
        byte[] updated = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(updated, 0);
        key.SetProperty(new("Security Descr", updated, CngPropertyOptions.Persist | dacl));
    }

    public void Dispose()
    {
        Certificate.Dispose();
        if (!keepPersisted) key.Delete();
        key.Dispose();
    }
}
