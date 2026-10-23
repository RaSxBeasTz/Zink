using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ZinkLyrics;

public sealed record LyricsResult(string Text, string Source)
{
    public bool HasLyrics => !string.IsNullOrWhiteSpace(Text);
    public string? SyncedLyrics { get; init; }
    public string? LyricsFile { get; init; }
    public int CacheVersion { get; init; }
}

/// <summary>Reads existing local lyrics without network access or modifying music files.</summary>
public static class LocalLyricsReader
{
    public static Task<LyricsResult> ReadAsync(string songPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(songPath);
        return Task.Run(async () =>
        {
            foreach (string extension in new[] { ".lyricsfile.yaml", ".lrc", ".txt" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.ChangeExtension(songPath, extension);
                if (!File.Exists(path)) continue;
                try
                {
                    string raw = await File.ReadAllTextAsync(path, cancellationToken);
                    var yamlLines = extension == ".lyricsfile.yaml" ? TimedLyrics.ParseLyricsFile(raw) : Array.Empty<LyricLine>();
                    string text = extension == ".lyricsfile.yaml"
                        ? string.Join(Environment.NewLine, yamlLines.Select(line => line.Text)) : FormatLyrics(raw);
                    if (!string.IsNullOrWhiteSpace(text))
                        return new LyricsResult(text, "Local file: " + Path.GetFileName(path))
                        { SyncedLyrics = extension == ".lyricsfile.yaml" ? null : raw,
                          LyricsFile = extension == ".lyricsfile.yaml" ? raw : null };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine("Local lyrics file: " + ex.Message);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var tags = TagLib.File.Create(songPath);
                string text = FormatLyrics(tags.Tag.Lyrics ?? "");
                return new LyricsResult(text, string.IsNullOrWhiteSpace(text) ? "" : "Lyrics embedded in this song")
                    { SyncedLyrics = tags.Tag.Lyrics };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
            {
                Debug.WriteLine("Embedded lyrics: " + ex.Message);
                return new LyricsResult("", "");
            }
        }, cancellationToken);
    }

    internal static string FormatLyrics(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return string.Join(Environment.NewLine, lines
            .Where(line => !Regex.IsMatch(line, @"^\s*\[(ar|al|ti|au|by|offset|re|ve|length):", RegexOptions.IgnoreCase))
            .Select(line => Regex.Replace(line, @"\[\d{1,3}:\d{2}(?:[.:]\d+)?\]|<\d{1,3}:\d{2}(?:[.:]\d+)?>", ""))).Trim();
    }
}
