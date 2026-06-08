using System.IO;
using System.Text.Json;
using Log = Logger.Logger;

namespace ReadSelectedTextTts.Telemetry;

/// <summary>
/// Append-only local usage telemetry stored as JSON Lines
/// (<c>%AppData%\ReadSelectedTextTts\telemetry.jsonl</c>). Never leaves the machine.
/// </summary>
public sealed class TelemetryService
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = false };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TelemetryService(string appDirectoryPath)
    {
        FilePath = Path.Combine(appDirectoryPath, "telemetry.jsonl");
    }

    public string FilePath { get; }

    public async Task RecordAsync(TtsUsageEvent usageEvent)
    {
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var line = JsonSerializer.Serialize(usageEvent, WriteOptions);
            await File.AppendAllTextAsync(FilePath, line + Environment.NewLine);
            Log.Trc($"Telemetry recorded: provider={usageEvent.ProviderId}, chars={usageEvent.CharacterCount}, ok={usageEvent.Success}");
        }
        catch (Exception ex)
        {
            Log.Wrn($"Failed to record telemetry: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<UsageSummary> LoadSummaryAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(FilePath))
            {
                return new UsageSummary();
            }

            var events = new List<TtsUsageEvent>();
            foreach (var line in await File.ReadAllLinesAsync(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var parsed = JsonSerializer.Deserialize<TtsUsageEvent>(line);
                    if (parsed is not null)
                    {
                        events.Add(parsed);
                    }
                }
                catch
                {
                    // Skip malformed lines rather than failing the whole summary.
                }
            }

            var byProvider = events
                .GroupBy(e => e.ProviderId)
                .Select(g => new ProviderUsage
                {
                    ProviderId = g.Key,
                    Reads = g.Count(),
                    Characters = g.Sum(e => (long)e.CharacterCount),
                    Failures = g.Count(e => !e.Success),
                    AvgSynthesisMs = g.Where(e => e.Success)
                        .Select(e => (double)e.SynthesisMs)
                        .DefaultIfEmpty(0)
                        .Average(),
                })
                .OrderByDescending(p => p.Reads)
                .ToList();

            return new UsageSummary
            {
                TotalReads = events.Count,
                TotalCharacters = events.Sum(e => (long)e.CharacterCount),
                ByProvider = byProvider,
            };
        }
        catch (Exception ex)
        {
            Log.Wrn($"Failed to load telemetry summary: {ex.Message}");
            return new UsageSummary();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
                Log.Inf("Telemetry cleared.");
            }
        }
        catch (Exception ex)
        {
            Log.Wrn($"Failed to clear telemetry: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }
}
