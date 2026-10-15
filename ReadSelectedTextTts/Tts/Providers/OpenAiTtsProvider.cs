using System.Diagnostics;
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

    // A slow request gets one backup request. Whichever finishes successfully first
    // supplies the audio; the other response is cancelled and its stream discarded.
    private static readonly TimeSpan HedgeDelay = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ShortRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LongRequestTimeout = TimeSpan.FromSeconds(30);

    // Per-request deadlines below control synthesis. Keep the client timeout above
    // them so it also covers best-effort connection warm-up.
    private static readonly HttpClient Http = TtsHttp.Create(TimeSpan.FromSeconds(120));
    private static readonly HttpClient BackupHttp = TtsHttp.Create(TimeSpan.FromSeconds(120));

    public TtsProviderDescriptor Descriptor { get; } = new()
    {
        Id = ProviderId,
        DisplayName = "OpenAI TTS",
        Summary = "OpenAI's text-to-speech — very natural voices with a dead-simple API. " +
                  "Pay-as-you-go (no free tier), but only ~$1–2/month at personal-use volumes. " +
                  "If you already have an OpenAI key, this is the lightest option to run.",
        Quality = "★★★★",
        Latency = "~300–600ms to first word",
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
                HelpText = "tts-1 starts speaking fastest, tts-1-hd sounds best, gpt-4o-mini-tts is " +
                           "newest but slowest to first audio. Switch to tts-1 if reads feel sluggish.",
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

        var timeout = text.Length <= 500 ? ShortRequestTimeout : LongRequestTimeout;
        var primaryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        primaryCts.CancelAfter(timeout);
        var primary = SendSpeechRequestAsync(Http, payload, key.Trim(), text.Length, voice.Id, model, primaryCts.Token);
        CancellationTokenSource? backupCts = null;
        Task<TtsSynthesisResult>? backup = null;
        Task<TtsSynthesisResult>? winner = null;

        try
        {
            if (await Task.WhenAny(primary, Task.Delay(HedgeDelay, cancellationToken)) != primary
                && !primary.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Log.Wrn($"OpenAI speech request still pending after {HedgeDelay.TotalSeconds:F0}s for {text.Length} chars; starting backup request.");
                backupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                backupCts.CancelAfter(timeout);
                backup = SendSpeechRequestAsync(BackupHttp, payload, key.Trim(), text.Length, voice.Id, model, backupCts.Token);
            }

            var pending = backup is null
                ? new List<Task<TtsSynthesisResult>> { primary }
                : new List<Task<TtsSynthesisResult>> { primary, backup };
            Exception? lastFailure = null;

            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                try
                {
                    var result = await completed;
                    winner = completed;
                    return result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    Log.Wrn($"OpenAI speech request timed out for {text.Length} chars, voice '{voice.Id}' (model '{model}').");
                    lastFailure = ex;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                }
            }

            throw lastFailure!;
        }
        finally
        {
            primaryCts.Cancel();
            _ = DrainRequestAsync(primary, primaryCts, ReferenceEquals(primary, winner));
            if (backup is not null && backupCts is not null)
            {
                backupCts.Cancel();
                _ = DrainRequestAsync(backup, backupCts, ReferenceEquals(backup, winner));
            }
        }
    }

    private static async Task<TtsSynthesisResult> SendSpeechRequestAsync(
        HttpClient http, string payload, string key, int textLength, string voiceId, string model,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response);

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            Log.Trc($"OpenAI synthesized {bytes.Length} bytes of MP3 in {stopwatch.ElapsedMilliseconds} ms for {textLength} chars, voice '{voiceId}' (model '{model}').");
            return new TtsSynthesisResult { Stream = AudioStreams.FromBytes(bytes), ContentType = "audio/mpeg" };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Trc($"OpenAI speech request cancelled after {stopwatch.ElapsedMilliseconds} ms for {textLength} chars, voice '{voiceId}' (model '{model}').");
            throw;
        }
    }

    private static async Task DrainRequestAsync(
        Task<TtsSynthesisResult> task, CancellationTokenSource cancellation, bool handedOff)
    {
        try
        {
            var result = await task.ConfigureAwait(false);
            if (!handedOff)
            {
                result.Stream.Dispose();
            }
        }
        catch
        {
            // The caller already handled the failure, or this was the cancelled backup.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    public Task WarmUpAsync(IProviderConfig config, CancellationToken cancellationToken = default) =>
        TtsHttp.WarmUpAsync(Http, Endpoint, cancellationToken);

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
