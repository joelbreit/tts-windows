using Windows.Storage.Streams;

namespace ReadSelectedTextTts.Tts;

/// <summary>Helpers for turning provider audio bytes into a playable WinRT stream.</summary>
public static class AudioStreams
{
    /// <summary>Wraps raw audio bytes (e.g. MP3) in an in-memory random-access stream.</summary>
    public static IRandomAccessStream FromBytes(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
        }

        stream.Seek(0);
        return stream;
    }
}
