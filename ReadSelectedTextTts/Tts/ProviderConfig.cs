using ReadSelectedTextTts.Models;
using ReadSelectedTextTts.Security;

namespace ReadSelectedTextTts.Tts;

/// <summary>
/// <see cref="IProviderConfig"/> backed by persisted <see cref="ProviderSettings"/>.
/// Decrypts secrets on read.
/// </summary>
public sealed class ProviderConfig : IProviderConfig
{
    private readonly ProviderSettings _settings;

    public ProviderConfig(ProviderSettings settings)
    {
        _settings = settings;
    }

    public string? GetSecret(string key) =>
        _settings.Secrets.TryGetValue(key, out var encrypted) ? SecretProtector.Unprotect(encrypted) : null;

    public string? GetOption(string key) =>
        _settings.Options.TryGetValue(key, out var value) ? value : null;
}
