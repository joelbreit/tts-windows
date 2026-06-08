namespace ReadSelectedTextTts.Tts;

/// <summary>
/// Describes one configuration input a provider needs (e.g. an API key or
/// region). Drives the dynamic config UI in the Settings window so new providers
/// can surface their own fields without bespoke XAML.
/// </summary>
public sealed class ProviderConfigField
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    /// <summary>If true, the value is treated as a secret (DPAPI-encrypted at rest).</summary>
    public bool IsSecret { get; init; }

    /// <summary>If true, the app cannot synthesize with this provider until the field is set.</summary>
    public bool IsRequired { get; init; } = true;

    public string? Placeholder { get; init; }

    public string? HelpText { get; init; }

    public string? DefaultValue { get; init; }
}
