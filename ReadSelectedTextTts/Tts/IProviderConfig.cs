namespace ReadSelectedTextTts.Tts;

/// <summary>
/// Read access to a provider's resolved configuration at synthesis time. Secrets
/// are returned already decrypted. Providers should treat values as possibly null.
/// </summary>
public interface IProviderConfig
{
    string? GetSecret(string key);

    string? GetOption(string key);
}
