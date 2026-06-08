using Windows.Storage.Streams;

namespace ReadSelectedTextTts.Tts;

/// <summary>
/// Audio produced by a provider, ready to hand to the shared MediaPlayer. The
/// Windows provider returns a WinRT <c>SpeechSynthesisStream</c>; cloud providers
/// wrap their returned bytes (e.g. MP3) in an in-memory stream.
/// </summary>
public sealed class TtsSynthesisResult
{
    public required IRandomAccessStream Stream { get; init; }

    public required string ContentType { get; init; }
}
