using System.Globalization;
using System.Text.RegularExpressions;

namespace ZinkLyrics;

public sealed record LyricWord(double Seconds, double? EndSeconds, int StartIndex, int Length);

public sealed record LyricLine(double Seconds, string Text)
{
    public double? EndSeconds { get; init; }
    public IReadOnlyList<LyricWord> Words { get; init; } = Array.Empty<LyricWord>();
    public bool HasEstimatedWords { get; init; }
    public bool HasEstimatedLineTiming { get; init; }
}

public static class TimedLyrics
{
    public static IReadOnlyList<LyricLine> CreateTimeline(LyricsResult result, double durationSeconds)
    {
        var fileLines = ParseLyricsFile(result.LyricsFile);
        var lrcLines = Parse(result.SyncedLyrics);
        IReadOnlyList<LyricLine> lines = fileLines.Any(line => line.Words.Count > 0) ? fileLines
            : lrcLines.Any(line => line.Words.Count > 0) ? lrcLines
            : fileLines.Count > 0 ? fileLines : lrcLines;
        // No guessed vocal starts or word durations: only source timestamps drive playback.
        return lines;
    }

    /// <summary>Provides visibly labelled approximate word progress when only line times exist.</summary>
    public static IReadOnlyList<LyricLine> WithWordProgress(IReadOnlyList<LyricLine> lines, double durationSeconds = 0)
    {
        var result = new List<LyricLine>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Words.Count > 0 || string.IsNullOrWhiteSpace(line.Text))
            {
                result.Add(line);
                continue;
            }
            var tokens = Regex.Matches(line.Text, @"\S+");
            double? next = i + 1 < lines.Count ? lines[i + 1].Seconds : null;
            double end = line.EndSeconds ?? next ?? (durationSeconds > line.Seconds ? durationSeconds
                : line.Seconds + Math.Max(2, tokens.Count * 0.5));
            // Avoid stretching the last sung word throughout a long instrumental gap.
            if (!line.HasEstimatedLineTiming)
                end = Math.Min(end, line.Seconds + Math.Max(8, tokens.Count * 0.7));
            if (next.HasValue) end = Math.Min(end, next.Value);
            if (end <= line.Seconds) { result.Add(line); continue; }
            var weights = tokens.Select(token => Math.Max(1, Math.Sqrt(token.Value.Count(char.IsLetterOrDigit)))).ToArray();
            double total = weights.Sum(), elapsed = 0;
            var words = new List<LyricWord>(tokens.Count);
            for (int word = 0; word < tokens.Count; word++)
            {
                double start = line.Seconds + (end - line.Seconds) * elapsed / total;
                elapsed += weights[word];
                double wordEnd = line.Seconds + (end - line.Seconds) * elapsed / total;
                words.Add(new LyricWord(start, wordEnd, tokens[word].Index, tokens[word].Length));
            }
            result.Add(line with { Words = words, HasEstimatedWords = true });
        }
        return result;
    }

    public static IReadOnlyList<LyricLine> Parse(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return Array.Empty<LyricLine>();
        double offset = 0;
        var offsetMatch = Regex.Match(lrc, @"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase);
        if (offsetMatch.Success && double.TryParse(offsetMatch.Groups[1].Value,
            NumberStyles.Integer, CultureInfo.InvariantCulture, out double milliseconds)) offset = milliseconds / 1000;
        var lines = new List<LyricLine>();
        foreach (string line in lrc.Replace("\r", "").Split('\n'))
        {
            var stamps = Regex.Matches(line, @"\[(\d{1,3}):(\d{2})(?:[.:](\d+))?\]");
            if (stamps.Count == 0) continue;
            string text = Regex.Replace(line, @"\[\d{1,3}:\d{2}(?:[.:]\d+)?\]|<\d{1,3}:\d{2}(?:[.:]\d+)?>", "").Trim();
            foreach (Match stamp in stamps)
            {
                int seconds = int.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture);
                if (seconds >= 60) continue;
                double fraction = stamp.Groups[3].Success
                    ? double.Parse("0." + stamp.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                double time = int.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture) * 60 + seconds + fraction - offset;
                var words = new List<LyricWord>();
                var wordStamps = Regex.Matches(line, @"<(\d{1,3}:\d{2}(?:[.:]\d+)?)>");
                int searchFrom = 0;
                double repeatShift = time - ReadStamp(stamps[0].Value.Trim('[', ']')) + offset;
                if (wordStamps.Count > 0)
                {
                    string prefix = Regex.Replace(line[..wordStamps[0].Index], @"\[\d{1,3}:\d{2}(?:[.:]\d+)?\]", "").Trim();
                    double firstWordTime = ReadStamp(wordStamps[0].Groups[1].Value) - offset + repeatShift;
                    if (prefix.Length > 0 && double.IsFinite(firstWordTime) && firstWordTime > time)
                    {
                        words.Add(new LyricWord(Math.Max(0, time), firstWordTime, 0, prefix.Length));
                        searchFrom = prefix.Length;
                    }
                }
                for (int i = 0; i < wordStamps.Count; i++)
                {
                    var marker = wordStamps[i];
                    int start = marker.Index + marker.Length;
                    int end = i + 1 < wordStamps.Count ? wordStamps[i + 1].Index : line.Length;
                    string segment = line[start..end].Trim();
                    if (segment.Length == 0) continue;
                    int index = text.IndexOf(segment, searchFrom, StringComparison.Ordinal);
                    if (index < 0) continue;
                    double wordStart = ReadStamp(marker.Groups[1].Value) - offset + repeatShift;
                    double? wordEnd = i + 1 < wordStamps.Count
                        ? ReadStamp(wordStamps[i + 1].Groups[1].Value) - offset + repeatShift : null;
                    if (!double.IsFinite(wordStart) || (wordEnd.HasValue && (!double.IsFinite(wordEnd.Value) || wordEnd <= wordStart))) continue;
                    words.Add(new LyricWord(Math.Max(0, wordStart), wordEnd, index, segment.Length));
                    searchFrom = index + segment.Length;
                }
                lines.Add(new LyricLine(Math.Max(0, time), text) { Words = words });
            }
        }
        return lines.OrderBy(line => line.Seconds).ToArray();
    }

    public static int FindCurrentLine(IReadOnlyList<LyricLine> lines, double positionSeconds)
    {
        int low = 0, high = lines.Count - 1, current = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (lines[middle].Seconds <= positionSeconds) { current = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return current >= 0 && lines[current].EndSeconds is double end && positionSeconds >= end ? -1 : current;
    }

    public static int FindCurrentWord(LyricLine line, double positionSeconds, double? nextLineSeconds = null)
    {
        for (int i = line.Words.Count - 1; i >= 0; i--)
        {
            var word = line.Words[i];
            double end = word.EndSeconds ?? (i + 1 < line.Words.Count ? line.Words[i + 1].Seconds
                : line.EndSeconds ?? nextLineSeconds ?? double.PositiveInfinity);
            if (positionSeconds >= word.Seconds && positionSeconds < end) return i;
        }
        return -1;
    }

    private static double ReadStamp(string stamp)
    {
        var parts = stamp.Replace(':', '.').Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int minutes)
            || !int.TryParse(parts[1], out int seconds) || seconds >= 60) return double.NaN;
        double fraction = parts.Length > 2 ? double.Parse("0." + parts[2], CultureInfo.InvariantCulture) : 0;
        return minutes * 60 + seconds + fraction;
    }

    public static IReadOnlyList<LyricLine> ParseLyricsFile(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml) || yaml.Length > 1_000_000) return Array.Empty<LyricLine>();
        try
        {
            var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
                .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties().WithDuplicateKeyChecking().Build();
            var file = deserializer.Deserialize<LyricsFileDocument>(yaml);
            if (file?.Version != "1.0") return Array.Empty<LyricLine>();
            var lines = new List<LyricLine>();
            foreach (var line in file?.Lines ?? [])
            {
                if (line.StartMs is not double start || !double.IsFinite(start) || start < 0 || line.Text is null) continue;
                double? end = line.EndMs is double endMs && double.IsFinite(endMs) && endMs > start ? endMs / 1000 : null;
                var words = new List<LyricWord>();
                int index = 0;
                // Exact text reconstruction preserves ranges for punctuation, repeats and Unicode.
                if (string.Concat((line.Words ?? []).Select(word => word.Text)) == line.Text)
                {
                    foreach (var word in line.Words ?? [])
                    {
                        string text = word.Text ?? "";
                        int leading = text.Length - text.TrimStart().Length;
                        int length = text.Trim().Length;
                        if (word.StartMs is double ws && double.IsFinite(ws) && ws >= start && length > 0
                            && (!end.HasValue || ws / 1000 < end)
                            && (word.EndMs is null || (double.IsFinite(word.EndMs.Value) && word.EndMs >= ws)))
                        {
                            double? wordEnd = word.EndMs / 1000;
                            if (end.HasValue && wordEnd.HasValue) wordEnd = Math.Min(wordEnd.Value, end.Value);
                            words.Add(new LyricWord(ws / 1000, wordEnd, index + leading, length));
                        }
                        index += text.Length;
                    }
                }
                lines.Add(new LyricLine(start / 1000, line.Text) { EndSeconds = end, Words = words.OrderBy(word => word.Seconds).ToArray() });
            }
            return lines.OrderBy(line => line.Seconds).ToArray();
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or ArgumentException or InvalidOperationException
            or FormatException or OverflowException)
        {
            return Array.Empty<LyricLine>();
        }
    }

    public sealed class LyricsFileDocument
    {
        public string? Version { get; set; }
        public List<FileLine>? Lines { get; set; }
    }
    public sealed class FileLine
    {
        public string? Text { get; set; }
        public double? StartMs { get; set; }
        public double? EndMs { get; set; }
        public List<FileWord>? Words { get; set; }
    }
    public sealed class FileWord
    {
        public string? Text { get; set; }
        public double? StartMs { get; set; }
        public double? EndMs { get; set; }
    }
}
