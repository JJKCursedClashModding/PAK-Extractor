using System;
using System.Collections.Generic;
using System.IO;

namespace JjkccUnpacker
{
    /// <summary>
    /// Filesystem helpers that are safe with paths longer than MAX_PATH (260 chars).
    ///
    /// The game install path alone (e.g.
    /// C:\Program Files (x86)\Steam\steamapps\common\Jujutsu Kaisen CC\Jujutsu Kaisen CC\
    /// Content\Paks\Data\Jujutsu Kaisen CC\Content\...) plus a deep cooked asset tree can
    /// easily exceed 260 characters, so every call goes through the \\?\ device prefix.
    /// </summary>
    internal static class Fs
    {
        private const string DevicePrefix = @"\\?\";
        private const string UncPrefix = @"\\?\UNC\";

        /// <summary>Prefixes an absolute path with \\?\ so the Win32 API skips MAX_PATH checks.</summary>
        public static string Lp(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return path; }

            if (full.StartsWith(DevicePrefix, StringComparison.Ordinal)) return full;
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return UncPrefix + full.Substring(2);
            return DevicePrefix + full;
        }

        /// <summary>Removes the \\?\ / \\?\UNC\ prefix so paths look normal again.</summary>
        public static string Strip(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(UncPrefix, StringComparison.Ordinal))
                return @"\\" + path.Substring(UncPrefix.Length);
            if (path.StartsWith(DevicePrefix, StringComparison.Ordinal))
                return path.Substring(DevicePrefix.Length);
            return path;
        }

        public static bool FileExists(string path)
        {
            try { return File.Exists(Lp(path)); } catch { return false; }
        }

        public static bool DirExists(string path)
        {
            try { return Directory.Exists(Lp(path)); } catch { return false; }
        }

        public static void CreateDir(string path)
        {
            if (!DirExists(path)) Directory.CreateDirectory(Lp(path));
        }

        /// <summary>True when the directory exists and contains at least one entry.</summary>
        public static bool HasEntries(string path)
        {
            try
            {
                using (var e = Directory.EnumerateFileSystemEntries(Lp(path)).GetEnumerator())
                    return e.MoveNext();
            }
            catch { return false; }
        }

        /// <summary>
        /// Yields every file below <paramref name="root"/> (depth-first). Paths are returned
        /// without the \\?\ prefix. Unreadable directories are skipped instead of throwing.
        /// </summary>
        public static IEnumerable<string> EnumerateFiles(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] files;
                try { files = Directory.GetFiles(Lp(dir)); }
                catch { files = new string[0]; }
                foreach (string f in files) yield return Strip(f);

                string[] subdirs;
                try { subdirs = Directory.GetDirectories(Lp(dir)); }
                catch { continue; }
                foreach (string d in subdirs) stack.Push(Strip(d));
            }
        }

        /// <summary>Counts every file below <paramref name="root"/>.</summary>
        public static long CountFiles(string root)
        {
            long n = 0;
            foreach (string unused in EnumerateFiles(root)) n++;
            return n;
        }

        /// <summary>Recursively deletes a directory. Returns false when something could not be removed.</summary>
        public static bool DeleteDirectory(string path)
        {
            if (!DirExists(path)) return true;
            try
            {
                Directory.Delete(Lp(path), true);
                return true;
            }
            catch
            {
                // Fall back to a manual walk: stray read-only files are a common cause.
                try
                {
                    foreach (string f in EnumerateFiles(path))
                    {
                        try
                        {
                            var attrs = File.GetAttributes(Lp(f));
                            if ((attrs & FileAttributes.ReadOnly) != 0)
                                File.SetAttributes(Lp(f), attrs & ~FileAttributes.ReadOnly);
                            File.Delete(Lp(f));
                        }
                        catch { }
                    }
                    Directory.Delete(Lp(path), true);
                    return true;
                }
                catch { return false; }
            }
        }

        /// <summary>Deletes a single file, clearing the read-only flag first if needed. Never throws.</summary>
        public static bool TryDeleteFile(string path, out string error)
        {
            error = null;
            try
            {
                var attrs = File.GetAttributes(Lp(path));
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(Lp(path), attrs & ~FileAttributes.ReadOnly);
                File.Delete(Lp(path));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Moves a file, overwriting an existing destination file (this matches shutil.move,
        /// which the in-editor pipeline uses). The source is only removed on success.
        /// </summary>
        public static bool TryMoveFile(string src, string dst, out string error)
        {
            error = null;
            try
            {
                File.Copy(Lp(src), Lp(dst), true);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            try { File.Delete(Lp(src)); }
            catch (Exception ex) { error = "copied but source could not be removed: " + ex.Message; }
            return true;
        }

        /// <summary>Counts files and totals their size in one walk.</summary>
        public static void Measure(string root, out long files, out long bytes)
        {
            files = 0;
            bytes = 0;
            foreach (string f in EnumerateFiles(root))
            {
                files++;
                try { bytes += new FileInfo(Lp(f)).Length; } catch { }
            }
        }

        /// <summary>Directory size in bytes, best effort (returns 0 for unreadable entries).</summary>
        public static long DirectorySize(string root)
        {
            long total = 0;
            foreach (string f in EnumerateFiles(root))
            {
                try { total += new FileInfo(Lp(f)).Length; } catch { }
            }
            return total;
        }

        public static string HumanSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return (u == 0 ? v.ToString("0") : v.ToString("0.0")) + " " + units[u];
        }
    }
}
