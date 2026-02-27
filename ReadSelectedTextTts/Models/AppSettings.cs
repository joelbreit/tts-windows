namespace ReadSelectedTextTts.Models;

public sealed class AppSettings
{
    public string? VoiceId { get; set; }

    public double Speed { get; set; } = 1.0;

    public uint HotkeyModifiers { get; set; } = 0x0009;

    public uint HotkeyKey { get; set; } = 0x52;
}
