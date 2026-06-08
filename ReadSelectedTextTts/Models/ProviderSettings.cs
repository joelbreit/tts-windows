namespace ReadSelectedTextTts.Models;

/// <summary>
/// Persisted per-provider configuration. Secret values (API keys) are stored
/// DPAPI-encrypted (base64); non-secret values (e.g. region) are stored plainly.
/// </summary>
public sealed class ProviderSettings
{
    /// <summary>The voice selected while this provider is active.</summary>
    public string? SelectedVoiceId { get; set; }

    /// <summary>Encrypted secret fields, keyed by config field key (e.g. "ApiKey").</summary>
    public Dictionary<string, string> Secrets { get; set; } = new();

    /// <summary>Non-secret option fields, keyed by config field key (e.g. "Region").</summary>
    public Dictionary<string, string> Options { get; set; } = new();
}
