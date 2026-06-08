using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReadSelectedTextTts.Models;
using Log = Logger.Logger;

namespace ReadSelectedTextTts.Tts.Providers;

/// <summary>
/// Cloud neural TTS via the Azure AI Speech REST API. Uses the resource key and
/// region directly (no token exchange): GETs the voice list and POSTs SSML to the
/// synthesis endpoint, returning MP3 audio. No SDK dependency.
/// </summary>
public sealed class AzureTtsProvider : ITtsProvider
{
    public const string ProviderId = "azure";

    private const string ApiKeyField = "ApiKey";
    private const string RegionField = "Region";
    private const string OutputFormat = "audio-24khz-48kbitrate-mono-mp3";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public TtsProviderDescriptor Descriptor { get; } = new()
    {
        Id = ProviderId,
        DisplayName = "Azure AI Speech (Neural)",
        Summary = "Microsoft's cloud neural voices — a large quality jump over the local SAPI voices, " +
                  "with ~300 voices across 140+ languages and low latency. The 500k chars/month free " +
                  "(F0) tier covers typical personal use at no cost.",
        Quality = "★★★★",
        Latency = "~100–200ms",
        Cost = "$16 / 1M chars (free tier: 500k/mo)",
        FreeTier = "500k chars/month (F0)",
        IsOffline = false,
        RequiresApiKey = true,
        InfoUrl = "https://azure.microsoft.com/pricing/details/cognitive-services/speech-services/",
        ConfigFields =
        [
            new ProviderConfigField
            {
                Key = ApiKeyField,
                Label = "Subscription key",
                IsSecret = true,
                Placeholder = "Azure Speech resource key",
                HelpText = "Speech resource → Keys and Endpoint → KEY 1.",
            },
            new ProviderConfigField
            {
                Key = RegionField,
                Label = "Region",
                IsSecret = false,
                DefaultValue = "eastus",
                Placeholder = "eastus",
                HelpText = "The resource's region/location, e.g. eastus, westeurope.",
            },
        ],
    };

    public bool IsConfigured(IProviderConfig config) =>
        !string.IsNullOrWhiteSpace(config.GetSecret(ApiKeyField)) &&
        !string.IsNullOrWhiteSpace(config.GetOption(RegionField));

    public async Task<IReadOnlyList<VoiceOption>> GetVoicesAsync(IProviderConfig config)
    {
        var (key, region) = RequireCredentials(config);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://{region}.tts.speech.microsoft.com/cognitiveservices/voices/list");
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);

        using var response = await Http.SendAsync(request);
        await EnsureSuccessAsync(response, "list voices");

        var json = await response.Content.ReadAsStringAsync();
        var voices = JsonSerializer.Deserialize<List<AzureVoice>>(json, JsonOptions) ?? [];

        var options = voices
            .Where(v => string.Equals(v.VoiceType, "Neural", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(v.Status, "Deprecated", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.Locale.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(v => v.Locale.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            .ThenBy(v => v.Locale, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.LocalName, StringComparer.OrdinalIgnoreCase)
            .Select(v => new VoiceOption(
                ProviderId,
                v.ShortName,
                $"{v.LocalName} ({v.Gender}) — {v.Locale}",
                v.Locale))
            .ToList();

        Log.Inf($"Azure provider loaded {options.Count} neural voice(s) from region '{region}'.");
        return options;
    }

    public async Task<TtsSynthesisResult> SynthesizeAsync(
        string text,
        VoiceOption voice,
        IProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        var (key, region) = RequireCredentials(config);
        var locale = voice.Tag as string ?? "en-US";
        var ssml = BuildSsml(text, voice.Id, locale);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1");
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);
        request.Headers.Add("X-Microsoft-OutputFormat", OutputFormat);
        request.Headers.Add("User-Agent", "ReadSelectedTextTts");
        request.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");

        using var response = await Http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "synthesize speech");

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        Log.Trc($"Azure synthesized {bytes.Length} bytes of MP3 for voice '{voice.Id}'.");
        return new TtsSynthesisResult { Stream = AudioStreams.FromBytes(bytes), ContentType = "audio/mpeg" };
    }

    private static (string Key, string Region) RequireCredentials(IProviderConfig config)
    {
        var key = config.GetSecret(ApiKeyField);
        var region = config.GetOption(RegionField);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(region))
        {
            throw new InvalidOperationException("Azure provider is not configured. Add a subscription key and region in Settings.");
        }

        return (key.Trim(), region.Trim());
    }

    private static string BuildSsml(string text, string voiceName, string locale) =>
        $"<speak version='1.0' xml:lang='{locale}'>" +
        $"<voice xml:lang='{locale}' name='{voiceName}'>{SecurityElement.Escape(text)}</voice>" +
        "</speak>";

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body.Trim();
        var hint = (int)response.StatusCode switch
        {
            401 or 403 => " (check the subscription key and region)",
            429 => " (rate limited or free-tier quota exhausted)",
            _ => string.Empty,
        };
        throw new InvalidOperationException($"Azure Speech failed to {action}: {(int)response.StatusCode} {detail}{hint}");
    }

    private sealed class AzureVoice
    {
        [JsonPropertyName("ShortName")] public string ShortName { get; set; } = "";

        [JsonPropertyName("LocalName")] public string LocalName { get; set; } = "";

        [JsonPropertyName("Locale")] public string Locale { get; set; } = "";

        [JsonPropertyName("Gender")] public string Gender { get; set; } = "";

        [JsonPropertyName("VoiceType")] public string VoiceType { get; set; } = "";

        [JsonPropertyName("Status")] public string Status { get; set; } = "";
    }
}
