namespace ReadSelectedTextTts.Models;

/// <summary>
/// Provider-agnostic representation of a voice. <see cref="Tag"/> carries an
/// opaque object that the owning provider understands (e.g. the WinRT
/// <c>VoiceInformation</c> for the Windows provider, or a voice id string for a
/// cloud provider).
/// </summary>
public sealed class VoiceOption
{
    public VoiceOption(string providerId, string id, string displayName, object? tag = null)
    {
        ProviderId = providerId;
        Id = id;
        DisplayName = displayName;
        Tag = tag;
    }

    /// <summary>Id of the provider that produced (and can synthesize) this voice.</summary>
    public string ProviderId { get; }

    /// <summary>Stable id used to persist the selected voice.</summary>
    public string Id { get; }

    public string DisplayName { get; }

    /// <summary>Provider-specific payload; only the owning provider should read it.</summary>
    public object? Tag { get; }
}
