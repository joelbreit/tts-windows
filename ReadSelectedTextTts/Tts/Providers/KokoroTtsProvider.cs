using System.IO;
using ReadSelectedTextTts.Models;
using SherpaOnnx;
using Log = Logger.Logger;

namespace ReadSelectedTextTts.Tts.Providers;

/// <summary>Offline Kokoro v1.0 synthesis using sherpa-onnx's CPU runtime.</summary>
public sealed class KokoroTtsProvider : ITtsProvider
{
    public const string ProviderId = "kokoro";
    private const string ModelFolder = "kokoro-multi-lang-v1_0";
    private const string ModelDirectoryOption = "ModelDirectory";
    private readonly SemaphoreSlim _synthesisLock = new(1, 1);
    private readonly object _modelLock = new();
    private Task<OfflineTts>? _modelTask;

    // IDs in the official sherpa-onnx Kokoro v1.0 model package. Limit the list
    // to the English speakers until language selection is supported in the UI.
    private static readonly string[] EnglishVoices =
    [
        "af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore",
        "af_nicole", "af_nova", "af_river", "af_sarah", "af_sky", "am_adam",
        "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael", "am_onyx",
        "am_puck", "am_santa", "bf_alice", "bf_emma", "bf_isabella", "bf_lily",
        "bm_daniel", "bm_fable", "bm_george", "bm_lewis",
    ];

    public TtsProviderDescriptor Descriptor { get; } = new()
    {
        Id = ProviderId,
        DisplayName = "Kokoro (Local)",
        Summary = "Natural offline voices from the Kokoro v1.0 model. Download the model " +
                  "once; speech generation then runs entirely on this PC.",
        Quality = "★★★★",
        Latency = "A few seconds to first phrase (CPU)",
        Cost = "Free",
        FreeTier = "Unlimited",
        IsOffline = true,
        RequiresApiKey = false,
        InfoUrl = "https://k2-fsa.github.io/sherpa/onnx/tts/pretrained_models/kokoro.html",
        ConfigFields =
        [
            new ProviderConfigField
            {
                Key = ModelDirectoryOption,
                Label = "Model directory",
                IsRequired = false,
                Placeholder = "Optional path to kokoro-multi-lang-v1_0",
                HelpText = "Leave blank to use models/kokoro-multi-lang-v1_0 beside the app or in the project folder.",
            },
        ],
    };

    public bool IsConfigured(IProviderConfig config) => HasModelFiles(FindModelDirectory(config));

    public Task<IReadOnlyList<VoiceOption>> GetVoicesAsync(IProviderConfig config)
    {
        var directory = FindModelDirectory(config);
        if (!HasModelFiles(directory))
        {
            throw new DirectoryNotFoundException(
                $"Kokoro model files were not found in '{directory}'. See README.md for the download command.");
        }

        IReadOnlyList<VoiceOption> voices = EnglishVoices
            .Select((name, id) => new VoiceOption(ProviderId, id.ToString(),
                $"{name} ({(name[0] == 'a' ? "US" : "British")})", id))
            .ToArray();
        return Task.FromResult(voices);
    }

    public async Task<TtsSynthesisResult> SynthesizeAsync(
        string text, VoiceOption voice, IProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        if (voice.Tag is not int speakerId || speakerId < 0 || speakerId >= EnglishVoices.Length)
        {
            throw new ArgumentException("Invalid Kokoro voice.", nameof(voice));
        }

        var model = await GetModelAsync(config).WaitAsync(cancellationToken);
        await _synthesisLock.WaitAsync(cancellationToken);
        try
        {
            var wave = await Task.Run(() =>
            {
                var generationConfig = new OfflineTtsGenerationConfig
                {
                    Sid = speakerId,
                    Speed = 1.0f,
                    SilenceScale = 0.2f,
                };
                OfflineTtsCallbackProgressWithArg callback = (_, _, _, _) =>
                    cancellationToken.IsCancellationRequested ? 0 : 1;
                var audio = model.GenerateWithConfig(text, generationConfig, callback);
                cancellationToken.ThrowIfCancellationRequested();
                if (audio.Samples.Length == 0)
                {
                    throw new InvalidOperationException("Kokoro generated no audio.");
                }

                return CreateWave(audio.Samples, audio.SampleRate);
            }, cancellationToken);

            Log.Trc($"Kokoro synthesized {text.Length} chars, {wave.Length} WAV bytes, voice {voice.Id}.");
            return new TtsSynthesisResult { Stream = AudioStreams.FromBytes(wave), ContentType = "audio/wav" };
        }
        finally
        {
            _synthesisLock.Release();
        }
    }

    public async Task WarmUpAsync(IProviderConfig config, CancellationToken cancellationToken = default) =>
        _ = await GetModelAsync(config).WaitAsync(cancellationToken);

    private Task<OfflineTts> GetModelAsync(IProviderConfig config)
    {
        lock (_modelLock)
        {
            return _modelTask ??= Task.Run(() =>
            {
                var directory = FindModelDirectory(config);
                if (!HasModelFiles(directory))
                {
                    throw new DirectoryNotFoundException($"Kokoro model files were not found in '{directory}'.");
                }

                var modelConfig = new OfflineTtsConfig();
                modelConfig.Model.Kokoro.Model = Path.Combine(directory, "model.onnx");
                modelConfig.Model.Kokoro.Voices = Path.Combine(directory, "voices.bin");
                modelConfig.Model.Kokoro.Tokens = Path.Combine(directory, "tokens.txt");
                modelConfig.Model.Kokoro.DataDir = Path.Combine(directory, "espeak-ng-data");
                modelConfig.Model.Kokoro.Lexicon = Path.Combine(directory, "lexicon-us-en.txt");
                modelConfig.Model.NumThreads = 4;
                modelConfig.Model.Provider = "cpu";
                var model = new OfflineTts(modelConfig);
                Log.Inf($"Kokoro loaded from '{directory}' ({model.NumSpeakers} speakers).");
                return model;
            });
        }
    }

    private static string FindModelDirectory(IProviderConfig config)
    {
        var configured = config.GetOption(ModelDirectoryOption);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
        }

        for (var ancestor = new DirectoryInfo(AppContext.BaseDirectory); ancestor is not null; ancestor = ancestor.Parent)
        {
            var candidate = Path.Combine(ancestor.FullName, "models", ModelFolder);
            if (HasModelFiles(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "models", ModelFolder);
    }

    private static bool HasModelFiles(string directory) =>
        File.Exists(Path.Combine(directory, "model.onnx")) &&
        File.Exists(Path.Combine(directory, "voices.bin")) &&
        File.Exists(Path.Combine(directory, "tokens.txt")) &&
        File.Exists(Path.Combine(directory, "lexicon-us-en.txt")) &&
        Directory.Exists(Path.Combine(directory, "espeak-ng-data"));

    private static byte[] CreateWave(float[] samples, int sampleRate)
    {
        using var stream = new MemoryStream(44 + samples.Length * sizeof(short));
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * sizeof(short));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // Mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * sizeof(short));
        foreach (var sample in samples)
        {
            writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
        }

        return stream.ToArray();
    }
}
