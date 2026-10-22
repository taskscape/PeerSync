using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using PeerSync.Core.Settings;

namespace PeerSync.Service.Settings;

internal sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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
        if (settings.ControlUserSids.Any(sid => !IsSid(sid)))
            throw new ValidationException("ControlUserSids must contain valid Windows SIDs.");
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
