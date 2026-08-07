using System;
using System.IO;
using System.Linq;

namespace GerberViewer.Stitching.Stitching
{
    // [Codex] [Change time: 2026-07-27] [Purpose: Centralize application-owned run publication and failure cleanup safety.]
    public static class RunOutputLifecycle
    {
        public static void Publish(string finalRunDirectory, string creatingDirectory)
        {
            ValidateOwnedCreatingDirectory(finalRunDirectory, creatingDirectory);
            if (!Directory.Exists(creatingDirectory)) throw new DirectoryNotFoundException("Creating directory missing before publish: " + creatingDirectory);

            var entries = Directory.GetFileSystemEntries(creatingDirectory);
            foreach (var entry in entries)
            {
                var target = Path.Combine(finalRunDirectory, Path.GetFileName(entry));
                if (File.Exists(target) || Directory.Exists(target))
                    throw new IOException("Publish conflict: existing run output will not be replaced: " + target);
            }

            foreach (var entry in entries)
            {
                var target = Path.Combine(finalRunDirectory, Path.GetFileName(entry));
                if (Directory.Exists(entry)) Directory.Move(entry, target);
                else File.Move(entry, target);
            }
            Directory.Delete(creatingDirectory, false);
        }

        public static void Cleanup(string finalRunDirectory, string creatingDirectory)
        {
            try
            {
                ValidateOwnedCreatingDirectory(finalRunDirectory, creatingDirectory);
                if (Directory.Exists(creatingDirectory)) Directory.Delete(creatingDirectory, true);
                if (Directory.Exists(finalRunDirectory) && !Directory.EnumerateFileSystemEntries(finalRunDirectory).Any()) Directory.Delete(finalRunDirectory, false);
            }
            catch
            {
                // Cleanup is best effort so it cannot obscure cancellation/runtime errors.
            }
        }

        private static void ValidateOwnedCreatingDirectory(string finalRunDirectory, string creatingDirectory)
        {
            if (string.IsNullOrWhiteSpace(finalRunDirectory)) throw new ArgumentException("Final run directory is required.", "finalRunDirectory");
            var expected = Path.GetFullPath(Path.Combine(finalRunDirectory, ".creating")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var actual = Path.GetFullPath(creatingDirectory ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cleanup/publish is restricted to the current run's application-owned .creating directory.");
        }
    }
}
