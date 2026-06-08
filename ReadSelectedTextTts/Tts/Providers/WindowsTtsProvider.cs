using ReadSelectedTextTts.Models;
using Windows.Media.SpeechSynthesis;
using Log = Logger.Logger;

namespace ReadSelectedTextTts.Tts.Providers;

/// <summary>
/// Local, offline TTS using the built-in Windows SAPI voices via
/// <c>Windows.Media.SpeechSynthesis</c>. Note: Windows 11 "Natural"/"Natural HD"
/// voices are NOT reachable through this (or any public) API — see
/// docs/windows-natural-voices-unavailable.md.
/// </summary>
public sealed class WindowsTtsProvider : ITtsProvider
{
    public const string ProviderId = "windows";

    public TtsProviderDescriptor Descriptor { get; } = new()
    {
        Id = ProviderId,
        DisplayName = "Windows (Local)",
        Summary = "Built-in offline Windows voices (David, Mark, Zira). Zero setup, zero cost, " +
                  "fully offline — but noticeably robotic. The Windows 11 \"Natural\" voices are " +
                  "not available to apps; see docs/windows-natural-voices-unavailable.md.",
        Quality = "★★",
        Latency = "Instant (local)",
        Cost = "Free",
        FreeTier = "Unlimited",
        IsOffline = true,
        RequiresApiKey = false,
        ConfigFields = [],
    };

    public bool IsConfigured(IProviderConfig config) => true;

    public Task<IReadOnlyList<VoiceOption>> GetVoicesAsync(IProviderConfig config)
    {
        var voices = SpeechSynthesizer.AllVoices
            .Select(voice => new VoiceOption(ProviderId, voice.Id, voice.DisplayName, voice))
            .OrderBy(voice => voice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Log.Inf($"Windows provider loaded {voices.Count} voice(s).");
        return Task.FromResult<IReadOnlyList<VoiceOption>>(voices);
    }

    public async Task<TtsSynthesisResult> SynthesizeAsync(
        string text,
        VoiceOption voice,
        IProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        if (voice.Tag is not VoiceInformation voiceInfo)
        {
            throw new InvalidOperationException("Windows voice is missing its VoiceInformation payload.");
        }

        var synth = new SpeechSynthesizer { Voice = voiceInfo };
        try
        {
            var stream = await synth.SynthesizeTextToStreamAsync(text).AsTask(cancellationToken);
            return new TtsSynthesisResult { Stream = stream, ContentType = stream.ContentType };
        }
        finally
        {
            synth.Dispose();
        }
    }
}
