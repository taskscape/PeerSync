using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeerSync.Core.Settings;

public enum TransferPriority { Default = 0, SmallerFirst = 1, LargerFirst = 2, OlderFirst = 3, NewerFirst = 4 }

public sealed record FolderSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter<TransferPriority>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public int RescanIntervalSeconds { get; init; } = 600;
    public bool UseLan { get; init; } = true;
    public bool UseTracker { get; init; } = true;
    public bool UseRelay { get; init; } = true;
    public string[] PredefinedHosts { get; init; } = [];
    public bool RequireApproval { get; init; }
    public bool KeepBothCopies { get; init; }
    public bool OverwriteLocalChanges { get; init; }
    public int ArchiveDays { get; init; } = 30;
    public long ArchiveMaxFileBytes { get; init; } = 1000L * 1024 * 1024;
    public int ArchiveMaxPercentOfVolume { get; init; } = 10;
    public TransferPriority Priority { get; init; } = TransferPriority.Default;

    public void Validate()
    {
        if (RescanIntervalSeconds is < 1 or > 86400 || ArchiveDays is < 1 or > 36500
            || ArchiveMaxFileBytes < 0 || ArchiveMaxPercentOfVolume is < 1 or > 100 || !Enum.IsDefined(Priority))
            throw new ValidationException("Invalid folder settings range.");
        if (PredefinedHosts is null || PredefinedHosts.Length > 256) throw new ValidationException("Invalid predefined host count.");
        foreach (string host in PredefinedHosts)
        {
            if (string.IsNullOrWhiteSpace(host) || host.Length > 512 || host.Contains('\\')
                || !Uri.TryCreate("tcp://" + host, UriKind.Absolute, out var uri)
                || uri.HostNameType == UriHostNameType.Unknown || uri.Port is < 1 or > 65535
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || uri.AbsolutePath is not ("" or "/"))
                throw new ValidationException("Invalid predefined host endpoint.");
        }
    }

    public string ToJson()
    {
        Validate();
        return JsonSerializer.Serialize(this, Json);
    }

    public static FolderSettings FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > 65536) throw new ValidationException("Folder settings exceed size limit.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ValidationException("Folder settings must be an object.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name)) throw new ValidationException("Duplicate folder setting.");
        var settings = document.Deserialize<FolderSettings>(Json) ?? throw new ValidationException("Missing folder settings.");
        settings.Validate();
        return settings;
    }
}
