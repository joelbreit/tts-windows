using ReadSelectedTextTts.Models;

namespace ReadSelectedTextTts.Tts;

/// <summary>
/// A source of speech synthesis. Implementations are stateless with respect to
/// configuration: everything they need is supplied per call via
/// <see cref="IProviderConfig"/>. Register implementations in
/// <see cref="TtsProviderRegistry"/>.
/// </summary>
public interface ITtsProvider
{
    TtsProviderDescriptor Descriptor { get; }

    /// <summary>True if the provider has everything it needs to synthesize.</summary>
    bool IsConfigured(IProviderConfig config);

    /// <summary>Enumerates the voices available for the given configuration.</summary>
    Task<IReadOnlyList<VoiceOption>> GetVoicesAsync(IProviderConfig config);

    /// <summary>Synthesizes <paramref name="text"/> with <paramref name="voice"/>.</summary>
    Task<TtsSynthesisResult> SynthesizeAsync(
        string text,
        VoiceOption voice,
        IProviderConfig config,
        CancellationToken cancellationToken = default);
}
