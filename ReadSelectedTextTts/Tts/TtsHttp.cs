using System.Net;
using System.Net.Http;

namespace ReadSelectedTextTts.Tts;

/// <summary>
/// Shared <see cref="HttpClient"/> setup for cloud providers. Connection reuse matters a
/// lot here: a cold DNS + TCP + TLS handshake adds 100–300ms in front of the first chunk
/// of a read, which is exactly the latency the chunked playback pipeline exists to remove.
/// Providers hold one of these in a static field for the app's lifetime.
/// </summary>
public static class TtsHttp
{
    public static HttpClient Create(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            // Well beyond the default minute, so a warm connection survives between reads.
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(30),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.All,
        };

        return new HttpClient(handler)
        {
            Timeout = timeout,

            // HTTP/2 multiplexes the parallel chunk requests over one connection;
            // RequestVersionOrLower falls back to 1.1 if the endpoint declines.
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    /// <summary>
    /// Opens a pooled connection to <paramref name="origin"/> without sending credentials,
    /// so the handshake is already paid for when the first synthesis request goes out.
    /// Any failure (including a 4xx from the throwaway path) is fine — the connection is
    /// established either way.
    /// </summary>
    public static async Task WarmUpAsync(HttpClient http, string origin, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, origin);
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch
        {
            // Warm-up is best effort; a failure here just means the first read pays the
            // handshake as it did before.
        }
    }
}
