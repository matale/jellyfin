namespace Jellyfin.Data.Dtos;

/// <summary>
/// A dto representing the capabilities and device profile last reported by a device.
/// </summary>
public class DeviceCapabilitiesDto
{
    /// <summary>
    /// Gets or sets the device id.
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Gets or sets the last client capabilities reported by the device, serialized as JSON.
    /// </summary>
    public string? Capabilities { get; set; }

    /// <summary>
    /// Gets or sets the last device profile the device sent with a playback info request, serialized as JSON.
    /// </summary>
    public string? PlaybackDeviceProfile { get; set; }
}
