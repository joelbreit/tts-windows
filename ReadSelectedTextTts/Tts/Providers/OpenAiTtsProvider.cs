using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ReadSelectedTextTts.Models;
using Log = Logger.Logger;

namespace ReadSelectedTextTts.Tts.Providers;

/// <summary>
/// Cloud TTS via the OpenAI audio/speech REST API. Voices are a fixed set (no list
/// endpoint), so they are declared statically. Playback speed is handled by the app's
/// MediaPlayer rate, so no <c>speed</c> param is sent.
/// </summary>
public sealed class OpenAiTtsProvider : ITtsProvider
{
    public const string ProviderId = "openai";

    private const string ApiKeyField = "ApiKey";
    private const string ModelField = "Model";
    private const string DefaultModel = "gpt-4o-mini-tts";
    private const string Endpoint = "https://api.openai.com/v1/audio/speech";

    // OpenAI exposes a fixed voice set; (id, friendly description).
    private static readonly (string Id, string Description)[] VoiceCatalog =
    [
        ("alloy", "Neutral, balanced"),
        ("ash", "Warm, expressive"),
        ("ballad", "Soft, lyrical"),
        ("coral", "Bright, friendly"),
        ("echo", "Calm, measured"),
        ("fable", "Storytelling, animated"),
        ("nova", "Energetic, upbeat"),
        ("onyx", "Deep, authoritative"),
        ("sage", "Gentle, soothing"),
        ("shimmer", "Light, airy"),
        ("verse", "Versatile, natural"),
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public TtsProviderDescriptor Descriptor { get; } = new()
    {
        Id = ProviderId,
        DisplayName = "OpenAI TTS",
        Summary = "OpenAI's text-to-speech — very natural voices with a dead-simple API. " +
                  "Pay-as-you-go (no free tier), but only ~$1–2/month at personal-use volumes. " +
                  "If you already have an OpenAI key, this is the lightest option to run.",
        Quality = "★★★★",
        Latency = "~300–600ms",
        Cost = "$15/1M (tts-1) · token-based (gpt-4o-mini-tts)",
        FreeTier = null,
        IsOffline = false,
        RequiresApiKey = true,
        InfoUrl = "https://platform.openai.com/docs/guides/text-to-speech",
        ConfigFields =
        [
            new ProviderConfigField
            {
                Key = ApiKeyField,
                Label = "API key",
                IsSecret = true,
                Placeholder = "sk-...",
                HelpText = "Create one at platform.openai.com/api-keys.",
            },
            new ProviderConfigField
            {
                Key = ModelField,
                Label = "Model",
                IsSecret = false,
                IsRequired = false,
                DefaultValue = DefaultModel,
                Placeholder = DefaultModel,
                HelpText = "tts-1 (fast, cheap), tts-1-hd (higher quality), or gpt-4o-mini-tts (newest).",
            },
        ],
    };

    public bool IsConfigured(IProviderConfig config) =>
        !string.IsNullOrWhiteSpace(config.GetSecret(ApiKeyField));

    public Task<IReadOnlyList<VoiceOption>> GetVoicesAsync(IProviderConfig config)
    {
        var voices = VoiceCatalog
            .Select(v => new VoiceOption(
                ProviderId,
                v.Id,
                $"{char.ToUpperInvariant(v.Id[0])}{v.Id[1..]} — {v.Description}"))
            .ToList();

        Log.Inf($"OpenAI provider offers {voices.Count} voice(s).");
        return Task.FromResult<IReadOnlyList<VoiceOption>>(voices);
    }

    public async Task<TtsSynthesisResult> SynthesizeAsync(
        string text,
        VoiceOption voice,
        IProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        var key = config.GetSecret(ApiKeyField);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("OpenAI provider is not configured. Add an API key in Settings.");
        }

        var model = config.GetOption(ModelField);
        if (string.IsNullOrWhiteSpace(model))
        {
            model = DefaultModel;
        }

        var payload = JsonSerializer.Serialize(new
        {
            model = model.Trim(),
            input = text,
            voice = voice.Id,
            response_format = "mp3",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response);

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        Log.Trc($"OpenAI synthesized {bytes.Length} bytes of MP3 for voice '{voice.Id}' (model '{model}').");
        return new TtsSynthesisResult { Stream = AudioStreams.FromBytes(bytes), ContentType = "audio/mpeg" };
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        var message = TryExtractError(body) ?? response.ReasonPhrase;
        var hint = (int)response.StatusCode switch
        {
            401 => " (check the API key)",
            429 => " (rate limited or quota/billing exhausted)",
            _ => string.Empty,
        };
        throw new InvalidOperationException($"OpenAI TTS failed: {(int)response.StatusCode} {message}{hint}");
    }

    private static string? TryExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
            {
                return message.GetString();
            }
        }
        catch
        {
            // Non-JSON error body; fall through.
        }

        return body.Trim();
    }
}
