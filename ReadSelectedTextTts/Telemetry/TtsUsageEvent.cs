namespace ReadSelectedTextTts.Telemetry;

/// <summary>One synthesis attempt. Persisted as a line in telemetry.jsonl.</summary>
public sealed class TtsUsageEvent
{
    public DateTimeOffset Timestamp { get; set; }

    public string ProviderId { get; set; } = "";

    public string? VoiceId { get; set; }

    public int CharacterCount { get; set; }

    /// <summary>Time spent producing audio (before playback), in milliseconds.</summary>
    public long SynthesisMs { get; set; }

    public bool Success { get; set; }

    public string? Error { get; set; }

    /// <summary>What triggered the read: "selection", "clipboard", or "test".</summary>
    public string Source { get; set; } = "";
}
