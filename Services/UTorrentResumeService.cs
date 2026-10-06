using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Storage;

namespace Zink.Services
{
    internal sealed record UTorrentItem(string TorrentKey, string Name, string SavePath, long CompletedOn)
    {
        public int ReleaseYear
        {
            get
            {
                var match = Regex.Match(Name, @"(?<!\d)(?:19|20)\d{2}(?!\d)");
                return match.Success && int.TryParse(match.Value, out var year) ? year : 0;
            }
        }

        public override string ToString()
        {
            var completed = CompletedOn > 0
                ? DateTimeOffset.FromUnixTimeSeconds(CompletedOn).LocalDateTime.ToString("g", CultureInfo.CurrentCulture)
                : "Completed";
            return $"{Name}  •  {completed}";
        }
    }

    internal static class UTorrentResumeService
    {
        public static IReadOnlyList<UTorrentItem> ReadCompleted()
        {
            var movedIds = ReadMovedIds();
            var resumePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "uTorrent",
                "resume.dat");
            if (!File.Exists(resumePath))
                return Array.Empty<UTorrentItem>();

            using var stream = new FileStream(resumePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var root = new BencodeReader(stream).ReadValue() as Dictionary<string, object>;
            if (root is null)
                return Array.Empty<UTorrentItem>();

            var results = new List<UTorrentItem>();
            foreach (var pair in root)
            {
                if (pair.Value is not Dictionary<string, object> values)
                    continue;

                var size = GetLong(values, "size");
                var downloaded = GetLong(values, "downloaded");
                var completedOn = GetLong(values, "completed_on");
                if (completedOn <= 0 && (size <= 0 || downloaded < size))
                    continue;
                // µTorrent stores the exact red job error shown in its UI here.
                // Hide the entry only when µTorrent itself reports missing payload files.
                var downloadError = GetText(values, "dl_error");
                if (downloadError.Contains("Files missing from job", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = GetText(values, "caption");
                if (string.IsNullOrWhiteSpace(name))
                    name = Path.GetFileNameWithoutExtension(pair.Key);
                var savePath = GetText(values, "path");
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(savePath))
                    results.Add(new UTorrentItem(pair.Key, name, savePath, completedOn));
            }

            return results
                .GroupBy(GetStableId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Where(item => !IsMoved(item, movedIds))
                .OrderByDescending(item => item.CompletedOn)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }

        public static void MarkMoved(IEnumerable<UTorrentItem> torrents)
        {
            var movedIds = ReadMovedIds();
            foreach (var torrent in torrents)
            {
                movedIds.Add(GetStableId(torrent));
                movedIds.Add(GetRawTorrentId(torrent));
                movedIds.Add(GetCaptionId(torrent));
                // Keep the old identity too so history remains compatible with
                // builds that predate the stable torrent-key identity.
                movedIds.Add(GetLegacyId(torrent));
            }

            var historyPath = GetMovedHistoryPath();
            var historyDirectory = Path.GetDirectoryName(historyPath)!;
            Directory.CreateDirectory(historyDirectory);
            var temporaryPath = Path.Combine(historyDirectory, $"moved-torrents-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(movedIds.OrderBy(value => value)));
                File.Move(temporaryPath, historyPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        private static HashSet<string> ReadMovedIds()
        {
            var historyPath = GetMovedHistoryPath();
            if (!File.Exists(historyPath))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var values = JsonSerializer.Deserialize<string[]>(File.ReadAllText(historyPath))
                ?? throw new InvalidDataException("The moved-torrent history is empty or invalid.");
            return new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        }

        private static string GetMovedHistoryPath() =>
            Path.Combine(ApplicationData.Current.LocalFolder.Path, "FileMover", "moved-torrents.json");

        private static bool IsMoved(UTorrentItem torrent, ISet<string> movedIds)
        {
            if (movedIds.Contains(GetStableId(torrent)) ||
                movedIds.Contains(GetRawTorrentId(torrent)) ||
                movedIds.Contains(GetCaptionId(torrent)) ||
                movedIds.Contains(GetLegacyId(torrent)))
            {
                return true;
            }

            // Migrate histories written by older builds. Their IDs combined the
            // caption with a mutable save path, so compare the caption portion
            // when µTorrent has since rewritten or relocated that path.
            return movedIds.Any(id =>
            {
                var separator = id.IndexOf('\0');
                return separator > 0 && id[..separator].Equals(torrent.Name.Trim(), StringComparison.OrdinalIgnoreCase);
            });
        }

        private static string GetStableId(UTorrentItem torrent)
        {
            var key = torrent.TorrentKey.Trim();
            var fileName = Path.GetFileName(key);
            return $"torrent:{(string.IsNullOrWhiteSpace(fileName) ? key : fileName)}";
        }

        private static string GetRawTorrentId(UTorrentItem torrent) => $"torrent:{torrent.TorrentKey.Trim()}";

        private static string GetLegacyId(UTorrentItem torrent) => $"{torrent.Name}\0{torrent.SavePath}";

        private static string GetCaptionId(UTorrentItem torrent) => $"caption:{torrent.Name.Trim()}";

        public static IReadOnlyList<string> FindVideoFiles(UTorrentItem torrent, ISet<string> videoExtensions)
        {
            var candidates = new List<string>();
            if (File.Exists(torrent.SavePath))
                candidates.Add(torrent.SavePath);

            if (Directory.Exists(torrent.SavePath))
            {
                var namedPath = Path.Combine(torrent.SavePath, torrent.Name);
                if (File.Exists(namedPath))
                    candidates.Add(namedPath);
                else if (Directory.Exists(namedPath))
                    candidates.AddRange(Directory.EnumerateFiles(namedPath, "*", SearchOption.AllDirectories));
                else
                {
                    var topLevelVideos = Directory.EnumerateFiles(torrent.SavePath, "*", SearchOption.TopDirectoryOnly)
                        .Where(path => videoExtensions.Contains(Path.GetExtension(path)))
                        .ToArray();
                    var torrentKey = NormalizePayloadName(torrent.Name);
                    var matchingVideos = topLevelVideos
                        .Where(path =>
                        {
                            var fileKey = NormalizePayloadName(Path.GetFileName(path));
                            return fileKey.Equals(torrentKey, StringComparison.OrdinalIgnoreCase) ||
                                   fileKey.StartsWith(torrentKey, StringComparison.OrdinalIgnoreCase) ||
                                   torrentKey.StartsWith(fileKey, StringComparison.OrdinalIgnoreCase);
                        })
                        .ToArray();

                    candidates.AddRange(matchingVideos);

                    var matchingFolder = Directory.EnumerateDirectories(torrent.SavePath, "*", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault(path =>
                        {
                            var folderKey = NormalizePayloadName(Path.GetFileName(path));
                            return folderKey.Equals(torrentKey, StringComparison.OrdinalIgnoreCase) ||
                                   folderKey.StartsWith(torrentKey, StringComparison.OrdinalIgnoreCase) ||
                                   torrentKey.StartsWith(folderKey, StringComparison.OrdinalIgnoreCase);
                        });
                    if (matchingFolder is not null)
                        candidates.AddRange(Directory.EnumerateFiles(matchingFolder, "*", SearchOption.AllDirectories));

                    // A dedicated torrent save folder containing one video is unambiguous,
                    // even when µTorrent's caption and the payload filename are different.
                    if (candidates.Count == 0 && topLevelVideos.Length == 1)
                        candidates.Add(topLevelVideos[0]);
                }
            }

            return candidates
                .Where(path => videoExtensions.Contains(Path.GetExtension(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string NormalizePayloadName(string value)
        {
            var name = value.Trim();
            if (name.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                name = name[..^8];
            var extension = Path.GetExtension(name);
            if (!string.IsNullOrWhiteSpace(extension))
                name = Path.GetFileNameWithoutExtension(name);

            var characters = name
                .Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ')
                .ToArray();
            return string.Join(' ', new string(characters)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        private static string GetText(Dictionary<string, object> values, string key) =>
            values.TryGetValue(key, out var value) ? value as string ?? "" : "";

        private static long GetLong(Dictionary<string, object> values, string key) =>
            values.TryGetValue(key, out var value) && value is long number ? number : 0;

        private sealed class BencodeReader
        {
            private readonly Stream _stream;
            public BencodeReader(Stream stream) => _stream = stream;

            public object ReadValue()
            {
                var marker = _stream.ReadByte();
                if (marker < 0)
                    throw new EndOfStreamException();
                return marker switch
                {
                    (byte)'d' => ReadDictionary(),
                    (byte)'l' => ReadList(),
                    (byte)'i' => ReadInteger(),
                    >= (byte)'0' and <= (byte)'9' => ReadString(marker),
                    _ => throw new InvalidDataException("The µTorrent data file is not valid.")
                };
            }

            private Dictionary<string, object> ReadDictionary()
            {
                var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
                while (PeekByte() != 'e')
                {
                    var key = ReadValue() as string ?? "";
                    dictionary[key] = ReadValue();
                }
                _stream.ReadByte();
                return dictionary;
            }

            private List<object> ReadList()
            {
                var list = new List<object>();
                while (PeekByte() != 'e')
                    list.Add(ReadValue());
                _stream.ReadByte();
                return list;
            }

            private long ReadInteger()
            {
                var text = ReadUntil('e');
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
            }

            private string ReadString(int firstDigit)
            {
                var lengthText = ((char)firstDigit) + ReadUntil(':');
                var length = int.Parse(lengthText, CultureInfo.InvariantCulture);
                var bytes = new byte[length];
                var offset = 0;
                while (offset < length)
                {
                    var read = _stream.Read(bytes, offset, length - offset);
                    if (read == 0)
                        throw new EndOfStreamException();
                    offset += read;
                }
                return Encoding.UTF8.GetString(bytes);
            }

            private int PeekByte()
            {
                var value = _stream.ReadByte();
                if (value < 0)
                    throw new EndOfStreamException();
                _stream.Position--;
                return value;
            }

            private string ReadUntil(char terminator)
            {
                var builder = new StringBuilder();
                int value;
                while ((value = _stream.ReadByte()) >= 0 && value != terminator)
                    builder.Append((char)value);
                if (value < 0)
                    throw new EndOfStreamException();
                return builder.ToString();
            }
        }
    }
}
