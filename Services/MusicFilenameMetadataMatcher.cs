using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Zink.Services
{
    internal static class MusicFilenameMetadataMatcher
    {
        public static string PrepareSearchTitle(string filenameTitle)
        {
            // Downloaders append the encoding and a download ID, neither of which
            // belongs to the artist or song title. Preserve numbers within titles.
            var title = Regex.Replace(filenameTitle,
                @"(?:_|\s|-)+(?:aac|mp3|m4a|opus|flac)(?:(?:_|\s|-)+\d+)?$", "",
                RegexOptions.IgnoreCase);
            title = title.Replace('_', ' ');
            title = Regex.Replace(title,
                @"\b(?:official\s+(?:(?:4k|hd)\s+)?(?:music\s+|lyrics?\s+)?video(?:\s+(?:4k|hd))?|official\s+(?:audio|visual)|(?:music|lyrics?|performance)\s+video|visuali[sz]er|m\s+v)\b",
                " ", RegexOptions.IgnoreCase);
            title = Regex.Replace(title, @"\(\s*lyrics?\s*\)|\[\s*lyrics?\s*\]", " ", RegexOptions.IgnoreCase);
            title = Regex.Replace(title, @"\(\s*\)|\[\s*\]", " ");
            return Regex.Replace(title, @"\s+", " ").Trim(' ', '-', '–', '—');
        }

        public static bool IsCanonicalFilename(string currentName, string desiredName) =>
            string.Equals(Path.GetExtension(currentName), Path.GetExtension(desiredName),
                System.StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(Path.GetFileNameWithoutExtension(currentName),
                "^" + Regex.Escape(Path.GetFileNameWithoutExtension(desiredName)) + @"(?: \(\d+\))?$",
                RegexOptions.IgnoreCase);

        public static string RemoveCollisionSuffix(string filenameTitle) =>
            Regex.Replace(filenameTitle, @" \(\d+\)$", "");

        public static bool Matches(string searchTitle, string artist, string title)
        {
            var filename = Normalize(searchTitle);
            return filename.Length > 0 &&
                (filename == Normalize(artist + " " + title) ||
                 filename == Normalize(title + " " + artist));
        }

        private static string Normalize(string value)
        {
            // Filenames often omit accents and punctuation, but the artist and
            // complete song name must still match; keep live/remix qualifiers.
            value = Regex.Replace(value, @"\b(?:ft|featuring)\.?\s", "feat ", RegexOptions.IgnoreCase);
            return new string(value.Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark &&
                    char.IsLetterOrDigit(c))
                .Select(char.ToLowerInvariant).ToArray());
        }
    }
}
