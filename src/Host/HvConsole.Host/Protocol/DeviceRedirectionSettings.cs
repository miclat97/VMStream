// ============================================================================
// DeviceRedirectionSettings.cs - Persistent Host-to-Guest Device Toggles
// Project: VMStream - Host Console Client (HvConsole.Host)
// Stores user preferences for redirecting Touchscreen, Touchpad H-Scroll,
// Speakers, and Microphone into VM sessions.
// ============================================================================
using System;
using System.IO;
using System.Text.Json;

namespace HvConsole.Host.Protocol;

public sealed class DeviceRedirectionSettings
{
    private static readonly string SettingsDirPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VMStream"
    );

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirPath, "device_redirection.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public bool EnableTouchscreen { get; set; } = true;
    public bool EnableTouchpadHorizontalScroll { get; set; } = true;
    public bool EnableSpeakers { get; set; } = true;
    public bool EnableMicrophone { get; set; } = true;

    public bool AnyTouchChannelEnabled => EnableTouchscreen || EnableTouchpadHorizontalScroll;
    public bool AnyAudioChannelEnabled => EnableSpeakers || EnableMicrophone;

    public DeviceRedirectionSettings Clone() => new()
    {
        EnableTouchscreen = EnableTouchscreen,
        EnableTouchpadHorizontalScroll = EnableTouchpadHorizontalScroll,
        EnableSpeakers = EnableSpeakers,
        EnableMicrophone = EnableMicrophone
    };

    public static DeviceRedirectionSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                DeviceRedirectionSettings? loaded = JsonSerializer.Deserialize<DeviceRedirectionSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
        }

        return new DeviceRedirectionSettings();
    }

    public static void Save(DeviceRedirectionSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirPath);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
        }
    }
}
