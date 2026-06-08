namespace ReadSelectedTextTts.Telemetry;

public sealed class ProviderUsage
{
    public string ProviderId { get; set; } = "";

    public int Reads { get; set; }

    public long Characters { get; set; }

    public int Failures { get; set; }

    public double AvgSynthesisMs { get; set; }

    // Convenience display props for binding.
    public string AvgSynthesisDisplay => Reads == 0 ? "—" : $"{AvgSynthesisMs:F0} ms";

    public string CharactersDisplay => Characters.ToString("N0");
}

public sealed class UsageSummary
{
    public int TotalReads { get; set; }

    public long TotalCharacters { get; set; }

    public IReadOnlyList<ProviderUsage> ByProvider { get; set; } = [];

    public string TotalCharactersDisplay => TotalCharacters.ToString("N0");
}
