using System.Collections.ObjectModel;
using ReadSelectedTextTts.Models;
using ReadSelectedTextTts.Tts;

namespace ReadSelectedTextTts.ViewModels;

/// <summary>
/// One provider in the Settings window: surfaces its descriptor metadata, its
/// editable config fields, configured/default state, and applies edits back to
/// the shared <see cref="ProviderSettings"/>.
/// </summary>
public sealed class ProviderRowViewModel : ObservableObject
{
    private readonly ITtsProvider _provider;
    private readonly ProviderSettings _settings;
    private bool _isDefault;

    public ProviderRowViewModel(ITtsProvider provider, ProviderSettings settings)
    {
        _provider = provider;
        _settings = settings;
        Fields = new ObservableCollection<ConfigFieldViewModel>(
            provider.Descriptor.ConfigFields.Select(field => new ConfigFieldViewModel(field, settings)));
    }

    public TtsProviderDescriptor Descriptor => _provider.Descriptor;

    public string Id => Descriptor.Id;

    public string DisplayName => Descriptor.DisplayName;

    public string Summary => Descriptor.Summary;

    public string Quality => Descriptor.Quality;

    public string Latency => Descriptor.Latency;

    public string Cost => Descriptor.Cost;

    public string FreeTier => Descriptor.FreeTier ?? "—";

    public bool IsOffline => Descriptor.IsOffline;

    public bool RequiresApiKey => Descriptor.RequiresApiKey;

    public string? InfoUrl => Descriptor.InfoUrl;

    public bool HasInfoUrl => !string.IsNullOrWhiteSpace(Descriptor.InfoUrl);

    public ObservableCollection<ConfigFieldViewModel> Fields { get; }

    public bool HasConfigFields => Fields.Count > 0;

    public bool IsConfigured => _provider.IsConfigured(new ProviderConfig(_settings));

    /// <summary>Status text for the provider list, e.g. "Default", "Ready", "Needs setup".</summary>
    public string StatusLabel =>
        IsDefault ? "Default"
        : !RequiresApiKey || IsConfigured ? "Ready"
        : "Needs setup";

    public bool IsDefault
    {
        get => _isDefault;
        set
        {
            if (SetProperty(ref _isDefault, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(CanSetDefault));
            }
        }
    }

    public bool CanSetDefault => !IsDefault;

    /// <summary>Persists all field edits into the provider's settings bucket.</summary>
    public void ApplyFields()
    {
        foreach (var field in Fields)
        {
            field.Apply(_settings);
        }

        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(StatusLabel));
    }
}
