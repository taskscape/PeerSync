using Microsoft.Extensions.Options;
using PeerSync.Core.Settings;

namespace PeerSync.Service.Settings;

internal sealed class DeviceSettingsValidator : IValidateOptions<DeviceSettings>
{
    public ValidateOptionsResult Validate(string? name, DeviceSettings settings)
    {
        try
        {
            SettingsStore.Validate(settings);
            return ValidateOptionsResult.Success;
        }
        catch (Exception error) when (error is ArgumentException or System.ComponentModel.DataAnnotations.ValidationException)
        {
            return ValidateOptionsResult.Fail(error.Message);
        }
    }
}
