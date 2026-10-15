using System.IO;
using ReadSelectedTextTts.Models;
using SherpaOnnx;
using Log = Logger.Logger;

namespace ReadSelectedTextTts.Tts.Providers;

/// <summary>Offline Piper synthesis using the sherpa-onnx VITS runtime.</summary>
public sealed class PiperTtsProvider : ITtsProvider
{
    public const string ProviderId = "piper";
    private const string ModelFolder = "vits-piper-en_US-lessac-medium";
    private const string ModelFile = "en_US-lessac-medium.onnx";
    private const string ModelDirectoryOption = "ModelDirectory";
    private readonly SemaphoreSlim _synthesisLock = new(1, 1);
    private readonly object _modelLock = new();
    private Task<OfflineTts>? _modelTask;

    public TtsProviderDescriptor Descriptor { get; } = new()
    {
        Id = ProviderId,
        DisplayName = "Piper (Local)",
        Summary = "Fast offline English speech using the Lessac medium voice. Download the model once.",
        Quality = "★★★",
        Latency = "Usually under a second to first phrase (CPU)",
        Cost = "Free",
        FreeTier = "Unlimited",
        IsOffline = true,
        RequiresApiKey = false,
        InfoUrl = "https://k2-fsa.github.io/sherpa/onnx/tts/pretrained_models/vits.html",
        ConfigFields =
        [
            new ProviderConfigField
            {
                Key = ModelDirectoryOption,
                Label = "Model directory",
                IsRequired = false,
                Placeholder = "Optional path to vits-piper-en_US-lessac-medium",
                HelpText = "Leave blank to use models/vits-piper-en_US-lessac-medium beside the app or in the project folder.",
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
                $"Piper model files were not found in '{directory}'. See README.md for the download command.");
        }

        IReadOnlyList<VoiceOption> voices =
        [
            new VoiceOption(ProviderId, "lessac", "Lessac (US English)", 0),
        ];
        return Task.FromResult(voices);
    }

    public async Task<TtsSynthesisResult> SynthesizeAsync(
        string text, VoiceOption voice, IProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        if (voice.ProviderId != ProviderId || voice.Id != "lessac")
        {
            throw new ArgumentException("Invalid Piper voice.", nameof(voice));
        }

        var model = await GetModelAsync(config).WaitAsync(cancellationToken);
        await _synthesisLock.WaitAsync(cancellationToken);
        try
        {
            var wave = await Task.Run(() =>
            {
                var generationConfig = new OfflineTtsGenerationConfig
                {
                    Sid = 0,
                    Speed = 1.0f,
                    SilenceScale = 0.2f,
                };
                OfflineTtsCallbackProgressWithArg callback = (_, _, _, _) =>
                    cancellationToken.IsCancellationRequested ? 0 : 1;
                var audio = model.GenerateWithConfig(text, generationConfig, callback);
                cancellationToken.ThrowIfCancellationRequested();
                if (audio.Samples.Length == 0)
                {
                    throw new InvalidOperationException("Piper generated no audio.");
                }

                return PcmWave.Create(audio.Samples, audio.SampleRate);
            }, cancellationToken);

            Log.Trc($"Piper synthesized {text.Length} chars, {wave.Length} WAV bytes.");
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
                    throw new DirectoryNotFoundException($"Piper model files were not found in '{directory}'.");
                }

                var modelConfig = new OfflineTtsConfig();
                modelConfig.Model.Vits.Model = Path.Combine(directory, ModelFile);
                modelConfig.Model.Vits.Tokens = Path.Combine(directory, "tokens.txt");
                modelConfig.Model.Vits.DataDir = Path.Combine(directory, "espeak-ng-data");
                modelConfig.Model.NumThreads = 2;
                modelConfig.Model.Provider = "cpu";
                var model = new OfflineTts(modelConfig);
                Log.Inf($"Piper loaded from '{directory}' ({model.NumSpeakers} speakers).");
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
        File.Exists(Path.Combine(directory, ModelFile)) &&
        File.Exists(Path.Combine(directory, "tokens.txt")) &&
        Directory.Exists(Path.Combine(directory, "espeak-ng-data"));
}
