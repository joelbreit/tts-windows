using ReadSelectedTextTts.Models;
using ReadSelectedTextTts.Security;
using ReadSelectedTextTts.Tts;

namespace ReadSelectedTextTts.ViewModels;

/// <summary>
/// Editable view model for one <see cref="ProviderConfigField"/>. Loads the current
/// value (decrypting secrets) and writes it back (encrypting secrets) on save.
/// </summary>
public sealed class ConfigFieldViewModel : ObservableObject
{
    private readonly ProviderConfigField _field;
    private string _value;

    public ConfigFieldViewModel(ProviderConfigField field, ProviderSettings settings)
    {
        _field = field;

        if (field.IsSecret)
        {
            _value = settings.Secrets.TryGetValue(field.Key, out var encrypted)
                ? SecretProtector.Unprotect(encrypted) ?? string.Empty
                : string.Empty;
        }
        else
        {
            _value = settings.Options.TryGetValue(field.Key, out var stored)
                ? stored
                : field.DefaultValue ?? string.Empty;
        }
    }

    public string Key => _field.Key;

    public string Label => _field.Label;

    public bool IsSecret => _field.IsSecret;

    public string? Placeholder => _field.Placeholder;

    public string? HelpText => _field.HelpText;

    public bool HasHelpText => !string.IsNullOrWhiteSpace(_field.HelpText);

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public bool HasValue => !string.IsNullOrWhiteSpace(_value);

    /// <summary>Writes the current value back into the provider's settings bucket.</summary>
    public void Apply(ProviderSettings settings)
    {
        var trimmed = _value.Trim();
        var target = _field.IsSecret ? settings.Secrets : settings.Options;

        if (string.IsNullOrEmpty(trimmed))
        {
            target.Remove(_field.Key);
            return;
        }

        target[_field.Key] = _field.IsSecret ? SecretProtector.Protect(trimmed) : trimmed;
    }
}
