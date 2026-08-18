namespace ReadSelectedTextTts.Telemetry;

/// <summary>One synthesis attempt. Persisted as a line in telemetry.jsonl.</summary>
public sealed class TtsUsageEvent
{
    public DateTimeOffset Timestamp { get; set; }

    public string ProviderId { get; set; } = "";

    public string? VoiceId { get; set; }

    public int CharacterCount { get; set; }

    /// <summary>
    /// Time from the request to the moment audio starts, in milliseconds. With chunked
    /// playback this is the first chunk only — later chunks are synthesized while the
    /// earlier ones play, so they cost the user nothing.
    /// </summary>
    public long SynthesisMs { get; set; }

    /// <summary>How many chunks the read was split into. 1 means it played as a single request.</summary>
    public int ChunkCount { get; set; }

    public bool Success { get; set; }

    public string? Error { get; set; }

    /// <summary>What triggered the read: "selection", "clipboard", or "test".</summary>
    public string Source { get; set; } = "";
}
