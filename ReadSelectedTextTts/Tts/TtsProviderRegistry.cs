using ReadSelectedTextTts.Models;
using ReadSelectedTextTts.Tts.Providers;

namespace ReadSelectedTextTts.Tts;

/// <summary>
/// The single catalog of known TTS providers. To add a provider: implement
/// <see cref="ITtsProvider"/> and register it in the constructor below — the
/// provider dropdown, Settings UI, and config storage all pick it up automatically.
/// </summary>
public sealed class TtsProviderRegistry
{
    private readonly Dictionary<string, ITtsProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ITtsProvider> _ordered = new();

    public TtsProviderRegistry()
    {
        Register(new WindowsTtsProvider());
        Register(new AzureTtsProvider());
        // Future providers go here, e.g.:
        // Register(new OpenAiTtsProvider());
    }

    private void Register(ITtsProvider provider)
    {
        _providers[provider.Descriptor.Id] = provider;
        _ordered.Add(provider);
    }

    public IReadOnlyList<ITtsProvider> Providers => _ordered;

    public ITtsProvider? Get(string? id) =>
        id is not null && _providers.TryGetValue(id, out var provider) ? provider : null;

    /// <summary>Resolves a provider by id, falling back to the local Windows provider.</summary>
    public ITtsProvider GetOrDefault(string? id) =>
        Get(id) ?? _providers[WindowsTtsProvider.ProviderId];

    /// <summary>Builds the resolved config for a provider from the app settings.</summary>
    public IProviderConfig ConfigFor(string providerId, AppSettings settings) =>
        new ProviderConfig(settings.GetProvider(providerId));
}
