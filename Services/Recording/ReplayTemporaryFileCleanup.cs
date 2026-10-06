using System;
using System.IO;

namespace Zink.Services.Recording
{
    internal static class ReplayTemporaryFileCleanup
    {
        private const string ReplayFolderName = "Replay120";

        internal static string FolderPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Zink",
            ReplayFolderName);

        internal static void DeleteContents()
        {
            string replayFolder = Path.GetFullPath(FolderPath);
            string expectedParent = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Zink"));

            if (!string.Equals(
                    Directory.GetParent(replayFolder)?.FullName,
                    expectedParent,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    Path.GetFileName(replayFolder),
                    ReplayFolderName,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (!Directory.Exists(replayFolder))
                return;

            foreach (string file in Directory.EnumerateFiles(replayFolder))
            {
                try { File.Delete(file); } catch { }
            }

            foreach (string directory in Directory.EnumerateDirectories(replayFolder))
            {
                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        }
    }
}
