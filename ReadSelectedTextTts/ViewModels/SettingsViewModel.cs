using System.Collections.ObjectModel;
using ReadSelectedTextTts.Models;
using ReadSelectedTextTts.Telemetry;
using ReadSelectedTextTts.Tts;
using Log = Logger.Logger;

namespace ReadSelectedTextTts.ViewModels;

/// <summary>
/// Backs the Settings window: provider catalog, per-provider config editing,
/// machine-default selection, and the local usage telemetry summary. Operates on
/// the <em>same</em> <see cref="AppSettings"/> instance as the main view model and
/// persists via the supplied save callback.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly TelemetryService _telemetry;
    private readonly Func<Task> _saveAsync;
    private ProviderRowViewModel? _selectedProvider;
    private UsageSummary _usage = new();
    private string _statusMessage = string.Empty;

    public SettingsViewModel(
        AppSettings settings,
        TtsProviderRegistry registry,
        TelemetryService telemetry,
        Func<Task> saveAsync)
    {
        _settings = settings;
        _telemetry = telemetry;
        _saveAsync = saveAsync;

        Providers = new ObservableCollection<ProviderRowViewModel>(
            registry.Providers.Select(p => new ProviderRowViewModel(p, settings.GetProvider(p.Descriptor.Id))));

        foreach (var row in Providers)
        {
            row.IsDefault = string.Equals(row.Id, settings.SelectedProviderId, StringComparison.OrdinalIgnoreCase);
        }

        _selectedProvider = Providers.FirstOrDefault(p => p.IsDefault) ?? Providers.FirstOrDefault();

        SetDefaultCommand = new AsyncRelayCommand(SetDefaultAsync, () => SelectedProvider is { CanSetDefault: true });
        SaveProviderCommand = new AsyncRelayCommand(SaveProviderAsync, () => SelectedProvider is { HasConfigFields: true });
        RefreshUsageCommand = new AsyncRelayCommand(RefreshUsageAsync);
        ClearUsageCommand = new AsyncRelayCommand(ClearUsageAsync);
    }

    public ObservableCollection<ProviderRowViewModel> Providers { get; }

    public ProviderRowViewModel? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public UsageSummary Usage
    {
        get => _usage;
        private set => SetProperty(ref _usage, value);
    }

    public string TelemetryPath => _telemetry.FilePath;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public AsyncRelayCommand SetDefaultCommand { get; }

    public AsyncRelayCommand SaveProviderCommand { get; }

    public AsyncRelayCommand RefreshUsageCommand { get; }

    public AsyncRelayCommand ClearUsageCommand { get; }

    public async Task InitializeAsync()
    {
        await RefreshUsageAsync();
    }

    private async Task SetDefaultAsync()
    {
        if (SelectedProvider is null)
        {
            return;
        }

        _settings.SelectedProviderId = SelectedProvider.Id;
        foreach (var row in Providers)
        {
            row.IsDefault = ReferenceEquals(row, SelectedProvider);
        }

        Log.Inf($"Default TTS provider set to '{SelectedProvider.Id}'.");
        StatusMessage = $"{SelectedProvider.DisplayName} is now the default provider.";
        RaiseCommandStates();
        await _saveAsync();
    }

    private async Task SaveProviderAsync()
    {
        if (SelectedProvider is null)
        {
            return;
        }

        SelectedProvider.ApplyFields();
        Log.Inf($"Saved configuration for provider '{SelectedProvider.Id}'.");
        StatusMessage = $"Saved {SelectedProvider.DisplayName} settings.";
        await _saveAsync();
    }

    private async Task RefreshUsageAsync()
    {
        Usage = await _telemetry.LoadSummaryAsync();
    }

    private async Task ClearUsageAsync()
    {
        await _telemetry.ClearAsync();
        await RefreshUsageAsync();
        StatusMessage = "Usage history cleared.";
    }

    private void RaiseCommandStates()
    {
        SetDefaultCommand.RaiseCanExecuteChanged();
        SaveProviderCommand.RaiseCanExecuteChanged();
    }
}
