using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using PeerSync.Core.Settings;

namespace PeerSync.Service.Settings;

internal sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly JsonSerializerOptions ControlJson = new(Json)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    internal static DeviceSettings ParseControlUpdate(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > 65536) throw new ValidationException("Settings exceed size limit.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ValidationException("Settings must be an object.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name)) throw new ValidationException("Duplicate device setting.");
        var value = document.Deserialize<DeviceSettings>(ControlJson)
            ?? throw new ValidationException("Settings are missing.");
        Validate(value);
        return value;
    }

    public DeviceSettings? ReadIfPresent(TextWriter? warnings = null)
        => ReadIfPresent(out _, warnings);

    public DeviceSettings? ReadIfPresent(out bool recovered, TextWriter? warnings = null)
    {
        recovered = false;
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var settings = JsonSerializer.Deserialize<DeviceSettings>(File.ReadAllText(candidate), Json)
                    ?? throw new InvalidDataException("settings file contains no settings object.");
                Validate(settings);
                if (candidate != path)
                {
                    RestoreBackup(candidate);
                    recovered = true;
                    warnings?.WriteLine("Recovered settings.json from settings.json.bak.");
                }
                return settings;
            }
            catch (Exception exception) when (exception is IOException or JsonException or ValidationException)
            {
                warnings?.WriteLine($"Ignoring unusable {Path.GetFileName(candidate)}: {exception.GetType().Name}.");
            }
        }
        return null;
    }

    public void Save(DeviceSettings settings)
    {
        Validate(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, settings, Json);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static void Validate(DeviceSettings settings)
    {
        Validator.ValidateObject(settings, new ValidationContext(settings), validateAllProperties: true);
        if (settings.SchemaVersion != 1) throw new ValidationException("Unsupported settings schema version.");
        if (settings.ControlUserSids is null || settings.ControlUserSids.Length > 64
            || settings.ControlUserSids.Any(sid => !IsSid(sid)))
            throw new ValidationException("ControlUserSids must contain valid Windows SIDs.");
        if (string.IsNullOrWhiteSpace(settings.DeviceName) || settings.DeviceName.Length > 128
            || settings.RendezvousServers is null || settings.RendezvousServers.Length > 16)
            throw new ValidationException("Invalid device settings.");
    }

    private void RestoreBackup(string backupPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".restore.tmp";
        try
        {
            using (var source = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null, ignoreMetadataErrors: true);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool IsSid(string? value)
    {
        try { _ = new System.Security.Principal.SecurityIdentifier(value!); return true; }
        catch (ArgumentException) { return false; }
    }
}
