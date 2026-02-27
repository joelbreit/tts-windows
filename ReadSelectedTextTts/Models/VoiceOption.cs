using Windows.Media.SpeechSynthesis;

namespace ReadSelectedTextTts.Models;

public sealed class VoiceOption
{
    public VoiceOption(string displayName, string id, VoiceInformation voice)
    {
        DisplayName = displayName;
        Id = id;
        Voice = voice;
    }

    public string DisplayName { get; }

    public string Id { get; }

    public VoiceInformation Voice { get; }
}
