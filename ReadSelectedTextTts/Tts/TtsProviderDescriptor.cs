namespace ReadSelectedTextTts.Tts;

/// <summary>
/// Static, user-facing metadata about a TTS provider. This is the single place a
/// new provider declares everything the UI surfaces: identity, quality/latency/cost
/// summaries, links, and the config fields it needs. Add a provider by creating an
/// <see cref="ITtsProvider"/> with one of these and registering it in
/// <see cref="TtsProviderRegistry"/>.
/// </summary>
public sealed class TtsProviderDescriptor
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>One or two sentence description shown in the provider details panel.</summary>
    public required string Summary { get; init; }

    /// <summary>Short quality indicator, e.g. "★★★★".</summary>
    public string Quality { get; init; } = "—";

    /// <summary>Short latency indicator, e.g. "~150ms" or "Instant (local)".</summary>
    public string Latency { get; init; } = "—";

    /// <summary>Short cost indicator, e.g. "$16 / 1M chars" or "Free".</summary>
    public string Cost { get; init; } = "—";

    /// <summary>Free-tier description, e.g. "500k chars/month".</summary>
    public string? FreeTier { get; init; }

    /// <summary>True if synthesis works fully offline.</summary>
    public bool IsOffline { get; init; }

    /// <summary>True if the provider needs an API key / credentials to work.</summary>
    public bool RequiresApiKey { get; init; }

    /// <summary>Optional link to pricing/docs.</summary>
    public string? InfoUrl { get; init; }

    /// <summary>Config inputs the provider needs; drives the Settings UI.</summary>
    public IReadOnlyList<ProviderConfigField> ConfigFields { get; init; } = [];
}
