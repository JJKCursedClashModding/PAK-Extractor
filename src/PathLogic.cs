using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace JjkccUnpacker
{
    /// <summary>
    /// All of the path discovery rules, mirroring Content/Python/import_game_assets.py
    /// (and the mod_tools.py helpers it reuses) from the JujutsuKaisenCC project.
    /// </summary>
    internal static class PathLogic
    {
        public const string AesKey = "0xBABFB8ACBA15424956C49B4E6CE9CFA43D5924D0B82FFDF8B6D5D70BE4F9DC82";

        /// <summary>Folder (relative to the pak Data folder) that holds the game's Content tree.</summary>
        public static readonly string[] DataContentParts = { "Jujutsu Kaisen CC", "Content" };

        /// <summary>Folder (relative to the pak Data folder) that holds the character-capture widgets.</summary>
        public static readonly string[] WbpCommonsParts = { "Jujutsu Kaisen CC", "Content", "Widgets", "Commons" };

        public static readonly string[] WbpCommonsStems =
        {
            "WBP_CharacterCaptureEnemy",
            "WBP_CharacterCapturePlayer",
            "WBP_CharacterCaptureSimple",
        };

        /// <summary>Matches the base name of every animation blueprint: ABP_* or *_ABP.</summary>
        public static readonly Regex AbpStemPattern =
            new Regex(@"^(?:ABP_.+|.+_ABP)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static readonly Regex AbpFilePattern =
            new Regex(@"^(?:ABP_.+|.+_ABP)\.uasset$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public const string GameExeRelative = @"Binaries\Win64\Jujutsu Kaisen CC.exe";
        public const string GameRootLeaf = @"Jujutsu Kaisen CC\Jujutsu Kaisen CC";
        public const string DefaultSteamRoot = @"C:\Program Files (x86)\Steam\steamapps\common\" + GameRootLeaf;

        public static string Combine(params string[] parts)
        {
            string result = parts[0];
            for (int i = 1; i < parts.Length; i++) result = Path.Combine(result, parts[i]);
            return result;
        }

        /// <summary>Combines a base folder with a set of relative path components.</summary>
        public static string CombineWith(string baseDir, string[] parts)
        {
            string result = baseDir;
            foreach (string part in parts) result = Path.Combine(result, part);
            return result;
        }

        public static string FullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path.Trim().Trim('"')); }
            catch { return null; }
        }

        // ── Paks discovery ────────────────────────────────────────────────────

        /// <summary>True when the folder is literally named "Paks" and holds .pak/.utoc/.ucas files.</summary>
        public static bool LooksLikePaksDir(string folder)
        {
            if (!Fs.DirExists(folder)) return false;
            if (!string.Equals(Path.GetFileName(folder.TrimEnd('\\', '/')), "Paks", StringComparison.OrdinalIgnoreCase))
                return false;
            return HasFileWithExtension(folder, ".pak")
                || HasFileWithExtension(folder, ".utoc")
                || HasFileWithExtension(folder, ".ucas");
        }

        private static bool HasFileWithExtension(string folder, string extension)
        {
            try { return Directory.EnumerateFiles(Fs.Lp(folder), "*" + extension).Any(); }
            catch { return false; }
        }

        /// <summary>
        /// Game root is normally &lt;exe&gt;\..\..\.. (Win64 → Binaries → game). If that folder has no
        /// Content\Paks we walk up from the exe so an unusual install layout still resolves.
        /// </summary>
        public static string GameRootFromExe(string exePath)
        {
            string exe = FullPath(exePath);
            if (exe == null) return null;
            string dir = Path.GetDirectoryName(exe);

            string conventional = null;
            try { conventional = Path.GetDirectoryName(Path.GetDirectoryName(dir)); } catch { }
            if (conventional != null && Fs.DirExists(Path.Combine(conventional, "Content", "Paks")))
                return conventional;

            for (string parent = dir; !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                if (Fs.DirExists(Path.Combine(parent, "Content", "Paks"))) return parent;
            }
            return conventional ?? dir;
        }

        /// <summary>
        /// Resolves Content\Paks from whatever the user typed: the game exe, a .pak file, the Paks
        /// folder itself, the game root, or any folder close to one of those.
        /// </summary>
        public static string FindPaksDir(string picked)
        {
            string path = FullPath(picked);
            if (path == null) return null;

            bool isFile = Fs.FileExists(path);
            if (isFile && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                string root = GameRootFromExe(path);
                if (root != null)
                {
                    string paks = Path.Combine(root, "Content", "Paks");
                    if (Fs.DirExists(paks)) return paks;
                }
            }

            string start = isFile ? Path.GetDirectoryName(path) : path;
            for (string dir = start; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                if (LooksLikePaksDir(dir)) return dir;

                string candidate = Path.Combine(dir, "Content", "Paks");
                if (LooksLikePaksDir(candidate)) return candidate;
                if (Fs.DirExists(candidate)) return candidate;
            }
            return null;
        }

        // ── Project / output discovery ────────────────────────────────────────

        /// <summary>Walks up from <paramref name="start"/> looking for a folder containing a .uproject.</summary>
        public static string FindProjectRoot(string start)
        {
            string dir = FullPath(start);
            if (dir == null) return null;
            if (Fs.FileExists(dir)) dir = Path.GetDirectoryName(dir);

            for (string cur = dir; !string.IsNullOrEmpty(cur); cur = Path.GetDirectoryName(cur))
            {
                try
                {
                    if (Directory.EnumerateFiles(Fs.Lp(cur), "*.uproject").Any()) return cur;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Returns the single .uproject file inside a project root, or null.</summary>
        public static string FindUprojectFile(string projectRoot)
        {
            if (projectRoot == null) return null;
            try
            {
                return Directory.EnumerateFiles(Fs.Lp(projectRoot), "*.uproject")
                                .Select(Fs.Strip)
                                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                .FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>
        /// Works out where extracted content should land.
        ///
        /// * output = project root (contains a .uproject)   → &lt;output&gt;\Content
        /// * output = the project's Content folder          → output
        /// * output = anything else                         → output (standalone unpack)
        /// </summary>
        public static string ResolveContentDir(string outputPath, out string projectRoot)
        {
            projectRoot = FindProjectRoot(outputPath);
            string dir = FullPath(outputPath);

            if (projectRoot != null)
            {
                string content = Path.Combine(projectRoot, "Content");
                return dir != null &&
                       string.Equals(Path.GetFileName(dir.TrimEnd('\\', '/')), "Content", StringComparison.OrdinalIgnoreCase)
                    ? dir
                    : content;
            }

            if (Fs.FileExists(dir))
                throw new PipelineException("The output path points at a file. Pick a folder instead.");

            return dir;
        }

        // ── Tool discovery ────────────────────────────────────────────────────

        /// <summary>
        /// Locates retoc.exe. Search order: next to this exe, a Tools folder beside it, the project's
        /// Tools folder (this is where the Unreal project keeps it), the paks folder tree, then PATH.
        /// </summary>
        public static string FindRetoc(string exeDir, string projectRoot, string paksDir)
        {
            var roots = new List<string>();

            string env = Environment.GetEnvironmentVariable("RETOC");
            if (!string.IsNullOrWhiteSpace(env) && Fs.FileExists(env)) return FullPath(env);

            if (exeDir != null)
            {
                roots.Add(exeDir);
                roots.Add(Path.Combine(exeDir, "Tools"));
                roots.Add(Path.Combine(exeDir, "tools"));
            }
            if (!string.IsNullOrEmpty(projectRoot))
            {
                roots.Add(Path.Combine(projectRoot, "Tools"));
                roots.Add(Path.Combine(projectRoot, "tools"));
            }
            if (!string.IsNullOrEmpty(paksDir))
            {
                roots.Add(paksDir);
                roots.Add(Path.Combine(paksDir, "Tools"));
            }

            foreach (string r in roots)
            {
                string candidate = Path.Combine(r, "retoc.exe");
                if (Fs.FileExists(candidate)) return FullPath(candidate);
            }

            try
            {
                foreach (string p in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                                     .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = Path.Combine(p.Trim(), "retoc.exe");
                    if (Fs.FileExists(candidate)) return FullPath(candidate);
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Locates UnrealEditor-Cmd.exe: the UNREAL_EDITOR_CMD override, the project's JJK Mod Kit
        /// setting, the Epic Launcher registry, then the well-known install folders.
        /// </summary>
        public static string FindUnrealEditorCmd(string projectRoot)
        {
            string env = Environment.GetEnvironmentVariable("UNREAL_EDITOR_CMD");
            if (!string.IsNullOrWhiteSpace(env) && Fs.FileExists(env)) return FullPath(env);

            foreach (string configured in ReadConfiguredEditorCmd(projectRoot))
                if (Fs.FileExists(configured)) return FullPath(configured);

            string association = ReadEngineAssociation(projectRoot);
            var versions = new List<string>();
            if (!string.IsNullOrWhiteSpace(association)) versions.Add(association.Trim());
            for (int minor = 7; minor >= 0; minor--)
            {
                string v = "5." + minor.ToString();
                if (!versions.Contains(v)) versions.Add(v);
            }

            foreach (string version in versions)
            {
                foreach (string hive in new[] { @"SOFTWARE\EpicGames\Unreal Engine\", @"SOFTWARE\WOW6432Node\EpicGames\Unreal Engine\" })
                {
                    string installed = ReadRegistryString(Registry.LocalMachine, hive + version, "InstalledDirectory");
                    if (!string.IsNullOrWhiteSpace(installed))
                    {
                        string cmd = Path.Combine(installed, @"Engine\Binaries\Win64\UnrealEditor-Cmd.exe");
                        if (Fs.FileExists(cmd)) return FullPath(cmd);
                    }
                }
            }

            var drives = new[] { "C:", "D:", "E:", "F:", "G:" };
            foreach (string drive in drives)
            {
                string epic = drive + @"\Program Files\Epic Games";
                if (!Fs.DirExists(epic)) continue;
                try
                {
                    var dirs = Directory.EnumerateDirectories(Fs.Lp(epic), "UE_5.*")
                                        .Select(Fs.Strip)
                                        .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase);
                    foreach (string d in dirs)
                    {
                        string cmd = Path.Combine(d, @"Engine\Binaries\Win64\UnrealEditor-Cmd.exe");
                        if (Fs.FileExists(cmd)) return FullPath(cmd);
                    }
                }
                catch { }
            }
            return null;
        }

        private static IEnumerable<string> ReadConfiguredEditorCmd(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot)) yield break;
            string[] inis =
            {
                Path.Combine(projectRoot, @"Config\DefaultGame.ini"),
                Path.Combine(projectRoot, @"Saved\Config\WindowsEditor\Game.ini"),
            };
            var pattern = new Regex(@"UeEditorCmdPath\s*=\s*\(?\s*(?:FilePath\s*=\s*)?""([^""]+)""",
                                    RegexOptions.IgnoreCase);
            foreach (string ini in inis)
            {
                if (!Fs.FileExists(ini)) continue;
                string text;
                try { text = File.ReadAllText(ini); } catch { continue; }
                foreach (Match m in pattern.Matches(text))
                    yield return m.Groups[1].Value.Replace("\\\\", "\\");
            }
        }

        /// <summary>Reads the Game Exe Path the Unreal project has stored in its Mod Kit settings.</summary>
        public static string ReadConfiguredGameExe(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot)) return null;
            string[] inis =
            {
                Path.Combine(projectRoot, @"Saved\Config\WindowsEditor\Game.ini"),
                Path.Combine(projectRoot, @"Config\DefaultGame.ini"),
            };
            var pattern = new Regex(@"GameExePath\s*=\s*\(\s*FilePath\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
            foreach (string ini in inis)
            {
                if (!Fs.FileExists(ini)) continue;
                string text;
                try { text = File.ReadAllText(ini); } catch { continue; }
                Match m = pattern.Match(text);
                if (m.Success)
                {
                    string value = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (Fs.FileExists(value)) return value;
                }
            }
            return null;
        }

        private static string ReadEngineAssociation(string projectRoot)
        {
            string uproject = FindUprojectFile(projectRoot);
            if (uproject == null) return null;
            try
            {
                string text = File.ReadAllText(uproject);
                Match m = Regex.Match(text, @"""EngineAssociation""\s*:\s*""([^""]*)""");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        private static string ReadRegistryString(RegistryKey hive, string subKey, string valueName)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(subKey))
                    return key == null ? null : key.GetValue(valueName) as string;
            }
            catch { return null; }
        }

        /// <summary>True when the project already contains skeleton assets (needed to build ABP stubs).</summary>
        public static bool ProjectHasSkeletons(string contentDir)
        {
            if (string.IsNullOrEmpty(contentDir) || !Fs.DirExists(contentDir)) return false;
            string fallback = Path.Combine(contentDir, @"Characters\Commons\Skeletons\SK_Human_Skeleton.uasset");
            if (Fs.FileExists(fallback)) return true;

            string characters = Path.Combine(contentDir, "Characters");
            if (!Fs.DirExists(characters)) return false;
            try
            {
                return Directory.EnumerateFiles(Fs.Lp(characters), "*.uasset", SearchOption.AllDirectories)
                                .Take(200000)
                                .Any(p => p.IndexOf(@"\Skeletons\", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { return false; }
        }

        /// <summary>Finds the first .pak inside a folder (used only for dialog pre-selection).</summary>
        public static string FirstPakIn(string folder)
        {
            if (!Fs.DirExists(folder)) return null;
            try
            {
                return Directory.EnumerateFiles(Fs.Lp(folder), "*.pak")
                                .Select(Fs.Strip)
                                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                .FirstOrDefault();
            }
            catch { return null; }
        }
    }

    /// <summary>Thrown for user-facing problems (bad paths, missing tools, …).</summary>
    internal sealed class PipelineException : Exception
    {
        public PipelineException(string message) : base(message) { }
    }
}
