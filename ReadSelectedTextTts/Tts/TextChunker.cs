using System.Text;

namespace ReadSelectedTextTts.Tts;

/// <summary>
/// Splits a read request into sentence-aligned chunks so playback can start as soon as
/// the first chunk is synthesized instead of waiting for the whole selection. The first
/// chunk is deliberately small — it alone sets time-to-first-audio — while later chunks
/// are large, which keeps prosody natural and the request count low.
/// </summary>
public static class TextChunker
{
    /// <summary>Target size of the first chunk. Small: it decides how fast the voice starts.</summary>
    public const int FirstChunkTargetChars = 180;

    /// <summary>
    /// Ceiling for the first chunk. Text with no sentence breaks at all (a wall of prose,
    /// a code block) still has to start speaking promptly, so it is cut at a clause or space.
    /// </summary>
    public const int FirstChunkMaxChars = 400;

    /// <summary>Target size of every chunk after the first.</summary>
    public const int ChunkTargetChars = 900;

    /// <summary>Hard ceiling for a chunk; oversized sentences are broken at a clause or space.</summary>
    public const int MaxChunkChars = 1600;

    private static readonly char[] Terminators = ['.', '!', '?', '…', '。', '！', '？'];

    private static readonly char[] Closers = ['"', '\'', ')', ']', '}', '”', '’', '»'];

    // Trailing-period words that almost never end a sentence.
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "mt", "vs", "etc", "eg", "ie",
        "inc", "ltd", "llc", "co", "corp", "dept", "est", "approx", "fig", "no", "vol",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
        "am", "pm", "ph", "phd", "md", "ba", "ma", "bsc", "msc",
    };

    /// <summary>
    /// Breaks <paramref name="text"/> into playable chunks. Returns an empty list for
    /// blank input, and a single chunk for anything short enough to not benefit.
    /// </summary>
    public static IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var trimmed = text.Trim();
        if (trimmed.Length <= FirstChunkTargetChars)
        {
            return [trimmed];
        }

        var chunks = new List<string>();
        var builder = new StringBuilder();

        foreach (var sentence in EnumerateSentences(trimmed))
        {
            var remaining = sentence;

            while (remaining.Length > 0)
            {
                // The first chunk gets its own, much tighter budget.
                var isFirst = chunks.Count == 0;
                var target = isFirst ? FirstChunkTargetChars : ChunkTargetChars;
                var max = isFirst ? FirstChunkMaxChars : MaxChunkChars;

                // Adding this sentence would overshoot the target: close the chunk here,
                // on a boundary, and re-evaluate against the next chunk's larger budget.
                if (builder.Length > 0 && builder.Length + remaining.Length > target)
                {
                    AddChunk(chunks, builder);
                    continue;
                }

                var room = max - builder.Length;
                if (room <= 0)
                {
                    AddChunk(chunks, builder);
                    continue;
                }

                if (remaining.Length <= room)
                {
                    builder.Append(remaining);
                    break;
                }

                // A single sentence longer than the budget: break it at a clause or space.
                var cut = FindBreak(remaining, room);
                builder.Append(remaining[..cut]);
                remaining = remaining[cut..];
                AddChunk(chunks, builder);
            }
        }

        AddChunk(chunks, builder);
        return chunks;
    }

    private static void AddChunk(List<string> chunks, StringBuilder builder)
    {
        var chunk = builder.ToString().Trim();
        builder.Clear();

        // A chunk of only punctuation/whitespace would waste a round trip and confuse
        // providers that reject empty input.
        if (chunk.Length > 0 && chunk.Any(char.IsLetterOrDigit))
        {
            chunks.Add(chunk);
        }
    }

    /// <summary>
    /// Yields sentences including their trailing whitespace, so concatenating the results
    /// reproduces the input exactly. Line breaks are always treated as safe boundaries.
    /// </summary>
    private static IEnumerable<string> EnumerateSentences(string text)
    {
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c is not '\n' && Array.IndexOf(Terminators, c) < 0)
            {
                continue;
            }

            var end = i + 1;

            if (c != '\n')
            {
                // Absorb runs like "?!" and any closing quote/bracket that belongs to the sentence.
                while (end < text.Length && Array.IndexOf(Terminators, text[end]) >= 0)
                {
                    end++;
                }

                while (end < text.Length && Array.IndexOf(Closers, text[end]) >= 0)
                {
                    end++;
                }

                // A terminator not followed by whitespace is inside a word ("3.5", "e.g.co").
                if (end < text.Length && !char.IsWhiteSpace(text[end]))
                {
                    i = end - 1;
                    continue;
                }

                if (c == '.' && IsAbbreviation(text, i))
                {
                    i = end - 1;
                    continue;
                }
            }

            while (end < text.Length && char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            yield return text[start..end];
            start = end;
            i = end - 1;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    /// <summary>True if the period at <paramref name="dotIndex"/> ends an abbreviation or initial.</summary>
    private static bool IsAbbreviation(string text, int dotIndex)
    {
        var wordStart = dotIndex;
        while (wordStart > 0 && char.IsLetter(text[wordStart - 1]))
        {
            wordStart--;
        }

        var word = text[wordStart..dotIndex];
        if (word.Length == 0)
        {
            return false;
        }

        // A single letter is an initial ("J. Breit") or part of "e.g." / "i.e.".
        return word.Length == 1 || Abbreviations.Contains(word);
    }

    /// <summary>
    /// Picks a cut point at or before <paramref name="limit"/>, preferring a clause boundary,
    /// then any whitespace, and only falling back to a hard cut mid-word if the text has
    /// neither in the second half of the budget.
    /// </summary>
    private static int FindBreak(string text, int limit)
    {
        var floor = limit / 2;

        for (var i = limit - 1; i > floor; i--)
        {
            if (text[i] is ',' or ';' or ':' or '—' or '–')
            {
                var end = i + 1;
                while (end < text.Length && char.IsWhiteSpace(text[end]))
                {
                    end++;
                }

                return end;
            }
        }

        for (var i = limit - 1; i > floor; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i + 1;
            }
        }

        return limit;
    }
}
