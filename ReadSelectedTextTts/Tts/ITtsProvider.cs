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

    /// <summary>
    /// Synthesizes <paramref name="text"/> with <paramref name="voice"/>. Callers may have
    /// several of these in flight at once for consecutive chunks of one read, so
    /// implementations must be safe to call concurrently.
    /// </summary>
    Task<TtsSynthesisResult> SynthesizeAsync(
        string text,
        VoiceOption voice,
        IProviderConfig config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Optional: opens the network connection ahead of the first read so the DNS/TLS
    /// handshake isn't on the critical path. Best effort — callers ignore failures.
    /// </summary>
    Task WarmUpAsync(IProviderConfig config, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
