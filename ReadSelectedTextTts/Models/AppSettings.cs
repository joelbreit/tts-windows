using ReadSelectedTextTts.Tts.Providers;

namespace ReadSelectedTextTts.Models;

public sealed class AppSettings
{
    /// <summary>
    /// Legacy single-voice id from before multi-provider support. Migrated into
    /// the Windows provider's <see cref="ProviderSettings.SelectedVoiceId"/> on load,
    /// then cleared. Kept only for backward-compatible deserialization.
    /// </summary>
    public string? VoiceId { get; set; }

    public double Speed { get; set; } = 1.0;

    public uint HotkeyModifiers { get; set; } = 0x0009;

    public uint HotkeyKey { get; set; } = 0x52;

    /// <summary>The provider used for synthesis ("machine default" provider).</summary>
    public string SelectedProviderId { get; set; } = WindowsTtsProvider.ProviderId;

    /// <summary>Per-provider configuration, keyed by provider id.</summary>
    public Dictionary<string, ProviderSettings> Providers { get; set; } = new();

    /// <summary>Gets (creating if needed) the settings bucket for a provider.</summary>
    public ProviderSettings GetProvider(string providerId)
    {
        if (!Providers.TryGetValue(providerId, out var settings))
        {
            settings = new ProviderSettings();
            Providers[providerId] = settings;
        }

        return settings;
    }
}
