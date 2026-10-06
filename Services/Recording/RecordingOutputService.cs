using System;
using System.IO;
using System.Threading.Tasks;

namespace Zink.Services.Recording
{
    public static class RecordingOutputService
    {
        public static Task<string> CreateNewOutputPathAsync(string prefix, string extension = ".mp4")
        {
            string videosPath = Environment.GetFolderPath(
                Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(videosPath))
            {
                videosPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Videos");
            }

            return Task.FromResult(CreateUniqueOutputPath(videosPath, prefix, extension));
        }

        public static string CreateFallbackOutputPath(string prefix)
        {
            string downloadsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            return CreateUniqueOutputPath(downloadsPath, prefix, ".mp4");
        }

        public static string CreateFallbackOutputPath(string prefix, string extension)
        {
            string downloadsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            return CreateUniqueOutputPath(downloadsPath, prefix, extension);
        }

        private static string CreateUniqueOutputPath(string parentPath, string prefix, string extension)
        {
            if (string.IsNullOrWhiteSpace(extension) || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("A valid recording extension is required.", nameof(extension));
            if (!extension.StartsWith(".", StringComparison.Ordinal))
                extension = "." + extension;

            string capturesPath = Path.Combine(parentPath, "Zink Captures");
            Directory.CreateDirectory(capturesPath);

            string baseName = $"{prefix} {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
            string outputPath = Path.Combine(capturesPath, baseName + extension);
            int suffix = 2;
            while (File.Exists(outputPath))
            {
                outputPath = Path.Combine(
                    capturesPath,
                    $"{baseName} ({suffix++}){extension}");
            }

            return outputPath;
        }
    }
}
