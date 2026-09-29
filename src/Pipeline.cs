using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace JjkccUnpacker
{
    internal sealed class ImportOptions
    {
        public string Input;
        public string Output;
        public bool DryRun;
        public bool NonInteractive;    // --yes
        public bool SkipEditorStubs;   // --no-stubs
        public bool ReuseExistingData; // --reuse-data
        public bool FreshData;         // --fresh
        public bool VerifyPaks;        // --verify-paks (retoc dry run, stops after step 1)
    }

    internal sealed class ImportResult
    {
        /// <summary>True when every step ran (warnings are reported separately).</summary>
        public bool Success;
        public bool Cancelled;
        public string Message = string.Empty;
        public readonly List<string> Warnings = new List<string>();
        public int Moved;
        public int Skipped;
        public int AbpDeleted;
        public int WbpDeleted;
        public int StubsCreated = -1;

        public bool HadWarnings { get { return Warnings.Count > 0; } }
    }

    /// <summary>
    /// The standalone equivalent of JJK Mod Kit → Setup → Import Game Assets.
    ///
    ///   1. retoc  : convert the cooked IoStore paks to legacy loose files  (Paks\Data)
    ///   2. stubs  : create AnimBlueprint stubs for every ABP_* / *_ABP found (via a headless
    ///               UnrealEditor-Cmd python commandlet, exactly the script the menu runs)
    ///   3. delete : remove every ABP_* / *_ABP file from the Data folder (+ Editor\ACLPlugin)
    ///   4. delete : remove the three character-capture WBPs from Data\...\Widgets\Commons
    ///   5. move   : move the extracted Content tree into the project's Content folder
    ///   6. clean  : delete the temporary Data folder
    /// </summary>
    internal sealed class ImportPipeline
    {
        private const int RangeUnpackStart = 0, RangeUnpackEnd = 55;
        private const int RangeStubsStart = 55, RangeStubsEnd = 63;
        private const int RangeDeleteAbpStart = 63, RangeDeleteAbpEnd = 69;
        private const int RangeDeleteWbpStart = 69, RangeDeleteWbpEnd = 71;
        private const int RangeMoveStart = 71, RangeMoveEnd = 96;
        private const int RangeCleanStart = 96, RangeCleanEnd = 100;

        private static readonly Regex ProgressPattern = new Regex(@"(\d+)\s*/\s*(\d+)\s*$");

        private readonly ImportOptions _options;
        private readonly Action<string> _log;
        private readonly Action<int, string> _progress;

        private int _lastPercent = -1;
        private DateTime _lastProgressAt = DateTime.MinValue;
        private volatile Process _activeProcess;

        // Resolved during Run()
        private string _paksDir;
        private string _dataDir;
        private string _dataContentDir;
        private string _contentDir;
        private string _projectRoot;
        private string _retocExe;
        private string _editorCmd;
        private bool _editorRunning;

        public volatile bool CancelRequested;

        /// <summary>Shown before anything is touched. Return false to abort.</summary>
        public Func<string, bool> ConfirmStart;

        /// <summary>Asked when a Data folder from an earlier run is found. true = reuse, false = wipe, null = abort.</summary>
        public Func<string, bool?> ConfirmReuseExistingData;

        public ImportPipeline(ImportOptions options, Action<string> log, Action<int, string> progress)
        {
            _options = options;
            _log = log ?? delegate { };
            _progress = progress ?? delegate { };
        }

        public ImportResult Run()
        {
            var result = new ImportResult();
            try
            {
                Resolve();

                if (_options.DryRun)
                {
                    ReportPlan();
                    result.Success = true;
                    result.Message = "Dry run only — nothing was changed.";
                    return result;
                }

                if (_options.VerifyPaks)
                {
                    _log("Verifying the game paks with retoc (nothing is written)…");
                }
                else if (ConfirmStart != null && !ConfirmStart(BuildConfirmationText()))
                {
                    result.Cancelled = true;
                    result.Message = "Cancelled before anything was changed.";
                    return result;
                }

                Phase1Unpack(result);
                if (Cancelled(result)) return result;

                if (_options.VerifyPaks)
                {
                    SetProgress(100, "Pak verification complete.");
                    result.Success = true;
                    result.Message = "Pak verification finished — retoc read the game files successfully.";
                    return result;
                }

                Phase2AbpStubs(result);
                if (Cancelled(result)) return result;

                Phase3DeleteAbps(result);
                if (Cancelled(result)) return result;

                Phase4DeleteWbps(result);
                if (Cancelled(result)) return result;

                Phase5MoveContent(result);
                if (Cancelled(result)) return result;

                Phase6Cleanup(result);

                SetProgress(100, "Done.");
                result.Success = true;
                result.Message = result.HadWarnings
                    ? "Import finished with warnings (see the log)."
                    : "All steps completed successfully.";
                return result;
            }
            catch (PipelineException ex)
            {
                result.Success = false;
                result.Message = ex.Message;
                _log("ERROR: " + ex.Message);
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.Message;
                _log("ERROR: " + ex);
                return result;
            }
        }

        private bool Cancelled(ImportResult result)
        {
            if (!CancelRequested) return false;
            result.Cancelled = true;
            result.Message = "Cancelled by the user.";
            _log("Cancelled by the user.");
            return true;
        }

        // ── Path resolution ───────────────────────────────────────────────────

        private void Resolve()
        {
            _paksDir = PathLogic.FindPaksDir(_options.Input);
            if (_paksDir == null)
            {
                throw new PipelineException(
                    "Could not find a Content\\Paks folder from:\n" + _options.Input + "\n\n" +
                    "Point the input at Jujutsu Kaisen CC.exe, any .pak inside the game's\n" +
                    "Content\\Paks folder, or the Paks folder itself.");
            }

            _dataDir = Path.Combine(_paksDir, "Data");
            _dataContentDir = PathLogic.CombineWith(_dataDir, PathLogic.DataContentParts);

            _contentDir = PathLogic.ResolveContentDir(_options.Output, out _projectRoot);
            if (string.IsNullOrWhiteSpace(_contentDir))
                throw new PipelineException("The output path is empty.");

            _retocExe = PathLogic.FindRetoc(AppDomain.CurrentDomain.BaseDirectory, _projectRoot, _paksDir);
            _editorCmd = PathLogic.FindUnrealEditorCmd(_projectRoot);

            if (!PathLogic.LooksLikePaksDir(_paksDir))
            {
                _log("Note: " + _paksDir + " does not look like a populated Paks folder " +
                     "(no .pak/.utoc/.ucas found).");
            }
        }

        private string BuildConfirmationText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("This runs the same pipeline as JJK Mod Kit → Import Game Assets.");
            sb.AppendLine();
            sb.AppendLine("It uses roughly 50 GB of temporary disk space and can take");
            sb.AppendLine("5–15 minutes (step 1 is the slow one).");
            sb.AppendLine();
            sb.AppendLine("  1. Unpack the game paks with retoc          (Paks\\Data)");
            sb.AppendLine("  2. Create AnimBlueprint stubs               (ABP_* / *_ABP)");
            sb.AppendLine("  3. Delete the ABPs + Editor\\ACLPlugin from Data");
            sb.AppendLine("  4. Delete the 3 character-capture WBPs from Data");
            sb.AppendLine("  5. Move the extracted Content into the output folder");
            sb.AppendLine("  6. Delete the temporary Data folder");
            sb.AppendLine();
            sb.AppendLine("Game paks : " + _paksDir);
            sb.AppendLine("Output    : " + _contentDir);
            if (_projectRoot != null) sb.AppendLine("Project   : " + _projectRoot);
            sb.AppendLine();
            if (IsAnyEditorRunning())
            {
                _editorRunning = true;
                sb.AppendLine("Note: an Unreal Editor is running. Step 2 (ABP stubs) will be");
                sb.AppendLine("skipped — run JJK Mod Kit → Asset Tools → Stub ABP From Folder");
                sb.AppendLine("there afterwards, then restart the editor.");
                sb.AppendLine();
            }
            sb.Append("Continue?");
            return sb.ToString();
        }

        private void ReportPlan()
        {
            _log("── Dry run ──────────────────────────────────────────────");
            _log("Input           : " + _options.Input);
            _log("Paks folder     : " + _paksDir);
            _log("Data folder     : " + _dataDir + (Fs.HasEntries(_dataDir) ? "  (exists)" : "  (new)"));
            _log("Data content    : " + _dataContentDir);
            _log("Output          : " + _options.Output);
            _log("Content folder  : " + _contentDir);
            _log("Project root    : " + (_projectRoot ?? "(none — standalone unpack)"));
            _log("retoc.exe       : " + (_retocExe ?? "(not found)"));
            _log("UnrealEditor-Cmd: " + (_editorCmd ?? "(not found — ABP stubs will be skipped)"));
            _log("Skeletons ready : " + (PathLogic.ProjectHasSkeletons(_contentDir) ? "yes" : "no"));
            _log("─────────────────────────────────────────────────────────");
            SetProgress(100, "Dry run complete.");
        }

        // ── Phase 1 — retoc ───────────────────────────────────────────────────

        private void Phase1Unpack(ImportResult result)
        {
            bool reuse = false;
            if (!_options.VerifyPaks && Fs.HasEntries(_dataDir))
            {
                if (_options.ReuseExistingData) reuse = true;
                else if (_options.FreshData) reuse = false;
                else if (_options.NonInteractive)
                {
                    // Non-interactive: reuse a folder that already contains extracted content.
                    reuse = Fs.HasEntries(_dataContentDir);
                    _log("Existing Data folder found — " + (reuse ? "reusing it (non-interactive)." : "wiping it (non-interactive)."));
                }
                else if (ConfirmReuseExistingData != null)
                {
                    bool? answer = ConfirmReuseExistingData(_dataDir);
                    if (answer == null)
                    {
                        CancelRequested = true;
                        return;
                    }
                    reuse = answer.Value;
                }
            }

            if (reuse)
            {
                _log("Step 1 / 6 — reusing the existing Data folder, skipping the unpack.");
                SetProgress(RangeUnpackEnd, "Step 1 / 6 — reusing existing Data folder.");
                return;
            }

            if (!_options.VerifyPaks && Fs.HasEntries(_dataDir))
            {
                _log("Step 1 / 6 — deleting the previous Data folder…");
                SetProgress(RangeUnpackStart, "Step 1 / 6 — deleting the previous Data folder…");
                if (!Fs.DeleteDirectory(_dataDir))
                    _log("Warning: the old Data folder could not be fully deleted — retoc may merge into it.");
            }

            SetProgress(RangeUnpackStart,
                "Step 1 / 6 — unpacking game paks with retoc (slow, 5–15 min)…");

            if (_retocExe == null)
            {
                throw new PipelineException(
                    "retoc.exe was not found.\n\n" +
                    "Put retoc.exe (and oo2core_9_win64.dll) in the project's Tools folder,\n" +
                    "next to this tool, or set the RETOC environment variable.");
            }
            if (!Fs.FileExists(Path.Combine(Path.GetDirectoryName(_retocExe), "oo2core_9_win64.dll")))
            {
                _log("Note: oo2core_9_win64.dll is not next to retoc.exe — Oodle-compressed data may fail.");
            }

            var outputName = _options.VerifyPaks ? "data-check" : "Data";
            var commandLine = "--aes-key " + PathLogic.AesKey + " to-legacy " +
                              (_options.VerifyPaks ? "-d " : "") + ". " + outputName;
            _log("── Phase 1: retoc ──");
            _log("  working dir : " + _paksDir);
            _log("  command     : \"" + _retocExe + "\" " + commandLine);

            int exitCode = RunProcess(_retocExe, commandLine, _paksDir, "retoc",
                delegate (string line)
                {
                    Match m = ProgressPattern.Match(line);
                    if (m.Success)
                    {
                        long done = long.Parse(m.Groups[1].Value);
                        long total = long.Parse(m.Groups[2].Value);
                        if (total > 0)
                        {
                            double fraction = Math.Min(1.0, (double)done / total);
                            int percent = RangeUnpackStart +
                                (int)Math.Round(fraction * (RangeUnpackEnd - RangeUnpackStart));
                            SetProgress(percent, string.Format(
                                "Step 1 / 6 — retoc: {0:N0} / {1:N0} files ({2:0.0}%)",
                                done, total, fraction * 100.0));
                        }
                        return;
                    }

                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) return;
                    if (trimmed.Contains("Extracted") || trimmed.Contains("Shader Library") ||
                        trimmed.Contains("Detected package version") || trimmed.Contains("error") ||
                        trimmed.Contains("Error") || trimmed.Contains("failed"))
                    {
                        _log("[retoc] " + trimmed);
                    }
                });

            if (CancelRequested) return;

            if (_options.VerifyPaks)
            {
                // retoc's dry run should not have written anything; make sure anyway.
                Fs.DeleteDirectory(Path.Combine(_paksDir, outputName));
            }

            if (exitCode != 0)
            {
                throw new PipelineException(
                    "retoc exited with code " + exitCode + ".\n\n" +
                    "Check the log for details. Common causes: the game paks are locked\n" +
                    "(close the game), or oo2core_9_win64.dll is missing next to retoc.exe.");
            }

            _log("✓ Phase 1 complete — pak conversion done.");
            SetProgress(RangeUnpackEnd, "Step 1 / 6 — unpack complete.");
        }

        // ── Phase 2 — AnimBlueprint stubs ─────────────────────────────────────

        private void Phase2AbpStubs(ImportResult result)
        {
            SetProgress(RangeStubsStart, "Step 2 / 6 — creating AnimBlueprint stubs…");
            _log("── Phase 2: AnimBlueprint stubs ──");

            if (!Fs.DirExists(_dataContentDir))
            {
                Warn(result, "Phase 2 skipped — " + _dataContentDir + " does not exist.");
                return;
            }
            if (_options.SkipEditorStubs)
            {
                _log("Phase 2 skipped — stub creation was disabled.");
                return;
            }

            var abpFiles = new List<string>();
            foreach (string file in Fs.EnumerateFiles(_dataContentDir))
                if (PathLogic.AbpFilePattern.IsMatch(Path.GetFileName(file)))
                    abpFiles.Add(file);

            _log("  ABP assets found: " + abpFiles.Count);
            if (abpFiles.Count == 0)
            {
                _log("  Nothing to stub.");
                return;
            }

            if (_projectRoot == null)
            {
                Warn(result, "Phase 2 skipped — the output is not an Unreal project " +
                             "(no .uproject found), so stubs cannot be created.");
                return;
            }
            if (!PathLogic.ProjectHasSkeletons(_contentDir))
            {
                Warn(result, "Phase 2 skipped — no skeleton assets exist in the project yet, " +
                             "so every ABP stub would fail. Re-run the import once the content " +
                             "is in place, or use JJK Mod Kit → Asset Tools → Stub ABP From Folder.");
                return;
            }
            if (_editorCmd == null)
            {
                Warn(result, "Phase 2 skipped — UnrealEditor-Cmd.exe was not found. " +
                             "Run JJK Mod Kit → Asset Tools → Stub ABP From Folder in the editor.");
                return;
            }
            if (_editorRunning || IsAnyEditorRunning())
            {
                Warn(result, "Phase 2 skipped — an Unreal Editor is already running. Close it and " +
                             "re-run, or use JJK Mod Kit → Asset Tools → Stub ABP From Folder.");
                return;
            }

            string uproject = PathLogic.FindUprojectFile(_projectRoot);
            string pythonDir = Path.Combine(_projectRoot, "Content", "Python");
            if (uproject == null || !Fs.DirExists(pythonDir))
            {
                Warn(result, "Phase 2 skipped — the project has no .uproject / Content\\Python folder.");
                return;
            }

            string tag = Process.GetCurrentProcess().Id.ToString();
            string scriptPath = Path.Combine(Path.GetTempPath(), "jjkcc_stub_abp_" + tag + ".py");
            string resultPath = Path.Combine(Path.GetTempPath(), "jjkcc_stub_abp_" + tag + ".result");
            string script = BuildStubScript(pythonDir, _dataContentDir, resultPath);
            File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
            try { File.Delete(resultPath); } catch { }

            string arguments = "\"" + uproject + "\" -run=pythonscript -Script=\"" + scriptPath + "\"" +
                               " -unattended -nosplash -nopause -NoLogTimes -stdout";
            _log("  editor      : " + _editorCmd);
            _log("  command     : \"" + _editorCmd + "\" " + arguments);
            _log("  (a headless editor launch — this usually takes 1–3 minutes)");

            int exitCode = RunProcess(_editorCmd, arguments, _projectRoot, "editor",
                delegate (string line)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) return;
                    if (trimmed.IndexOf("[StubABPFromFolder]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        trimmed.IndexOf("Traceback", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        trimmed.IndexOf("Error:", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _log("[stub] " + trimmed);
                    }
                },
                TimeSpan.FromMinutes(30));

            try { File.Delete(scriptPath); } catch { }

            if (CancelRequested) return;

            // The editor's own exit code is not a reliable signal (this project reports
            // pre-existing asset-manager errors), so the generated script writes a result file.
            string status, error;
            int found, present;
            ReadStubResult(resultPath, out status, out found, out present, out error);
            try { File.Delete(resultPath); } catch { }

            if (status == "ok")
            {
                result.StubsCreated = present;
                _log("✓ Phase 2 complete — " + present + " of " + found +
                     " ABP stub(s) verified on disk.");
            }
            else
            {
                Warn(result, "Phase 2 — the ABP stub step did not complete" +
                             (exitCode != 0 ? " (editor exit code " + exitCode + ")" : "") +
                             ". Run JJK Mod Kit → Asset Tools → Stub ABP From Folder in the editor " +
                             "if ABP assets are missing." +
                             (string.IsNullOrEmpty(error) ? "" : " Details: " + error));
            }
            SetProgress(RangeStubsEnd, "Step 2 / 6 — stubs done.");
        }

        private static void ReadStubResult(string path, out string status, out int found,
                                           out int present, out string error)
        {
            status = "missing";
            found = 0;
            present = 0;
            error = null;
            if (!Fs.FileExists(path)) return;
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = raw.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = raw.Substring(eq + 1).Trim();
                    if (key == "status") status = value;
                    else if (key == "abp") int.TryParse(value, out found);
                    else if (key == "present") int.TryParse(value, out present);
                    else if (key == "error" && value.Length > 0) error = value;
                }
            }
            catch { }
        }

        private static string BuildStubScript(string pythonDir, string sourceFolder, string resultPath)
        {
            Func<string, string> py = p => p.Replace("\\", "/").Replace("\"", "\\\"");
            var sb = new StringBuilder();
            sb.AppendLine("# Generated by JJKCC-Unpacker — creates ABP stubs from the extracted Data folder.");
            sb.AppendLine("# Mirrors JJK Mod Kit → Asset Tools → Stub ABP From Folder.");
            sb.AppendLine("import importlib");
            sb.AppendLine("import os");
            sb.AppendLine("import re");
            sb.AppendLine("import sys");
            sb.AppendLine("import traceback");
            sb.AppendLine();
            sb.AppendLine("SOURCE = r\"" + py(sourceFolder) + "\"");
            sb.AppendLine("RESULT = r\"" + py(resultPath) + "\"");
            sb.AppendLine();
            sb.AppendLine("def write_result(status, abp, present, error):");
            sb.AppendLine("    try:");
            sb.AppendLine("        with open(RESULT, 'w', encoding='utf-8') as fh:");
            sb.AppendLine("            fh.write('status=%s\\n' % status)");
            sb.AppendLine("            fh.write('abp=%s\\n' % abp)");
            sb.AppendLine("            fh.write('present=%s\\n' % present)");
            sb.AppendLine("            fh.write('error=%s\\n' % ' '.join(str(error).split()))");
            sb.AppendLine("    except Exception:");
            sb.AppendLine("        pass");
            sb.AppendLine();
            sb.AppendLine("status = 'error'");
            sb.AppendLine("error = ''");
            sb.AppendLine("abp_files = []");
            sb.AppendLine("present = 0");
            sb.AppendLine();
            sb.AppendLine("try:");
            sb.AppendLine("    sys.path.insert(0, r\"" + py(pythonDir) + "\")");
            sb.AppendLine("    import unreal");
            sb.AppendLine("    import stub_abp_from_folder");
            sb.AppendLine("    importlib.reload(stub_abp_from_folder)");
            sb.AppendLine();
            sb.AppendLine("    pattern = re.compile(r'^(?:ABP_.+|.+_ABP)\\.uasset$', re.IGNORECASE)");
            sb.AppendLine("    for root, _dirs, names in os.walk(SOURCE):");
            sb.AppendLine("        for name in names:");
            sb.AppendLine("            if pattern.match(name):");
            sb.AppendLine("                abp_files.append(os.path.join(root, name))");
            sb.AppendLine();
            sb.AppendLine("    content = unreal.Paths.convert_relative_path_to_full(unreal.Paths.project_content_dir())");
            sb.AppendLine("    content = content.replace('\\\\', '/').rstrip('/')");
            sb.AppendLine();
            sb.AppendLine("    stub_abp_from_folder.run(SOURCE)");
            sb.AppendLine();
            sb.AppendLine("    for path in abp_files:");
            sb.AppendLine("        rel = os.path.relpath(path, SOURCE).replace('\\\\', '/')");
            sb.AppendLine("        rel = rel[:-len('.uasset')] + '.uasset'");
            sb.AppendLine("        if os.path.exists(os.path.join(content, rel)):");
            sb.AppendLine("            present += 1");
            sb.AppendLine("    status = 'ok'");
            sb.AppendLine("except Exception:");
            sb.AppendLine("    error = traceback.format_exc()");
            sb.AppendLine("finally:");
            sb.AppendLine("    write_result(status, len(abp_files), present, error)");
            return sb.ToString();
        }

        private static bool IsAnyEditorRunning()
        {
            try
            {
                return Process.GetProcesses()
                              .Any(p =>
                              {
                                  try
                                  {
                                      string name = p.ProcessName;
                                      return name.StartsWith("UnrealEditor", StringComparison.OrdinalIgnoreCase);
                                  }
                                  catch { return false; }
                                  finally { try { p.Dispose(); } catch { } }
                              });
            }
            catch { return false; }
        }

        // ── Phase 3 — delete the ABPs from Data ───────────────────────────────

        private void Phase3DeleteAbps(ImportResult result)
        {
            SetProgress(RangeDeleteAbpStart, "Step 3 / 6 — deleting ABP files from the Data folder…");
            _log("── Phase 3: delete ABP files ──");

            if (!Fs.DirExists(_dataDir))
            {
                Warn(result, "Phase 3 skipped — " + _dataDir + " does not exist.");
                return;
            }

            int deleted = DeleteFilesByStem(_dataDir, PathLogic.AbpStemPattern, null);
            result.AbpDeleted = deleted;
            _log("  ABP files deleted: " + deleted);

            string aclFolder = Path.Combine(_dataContentDir, "Editor", "ACLPlugin");
            if (Fs.DirExists(aclFolder))
            {
                if (Fs.DeleteDirectory(aclFolder)) _log("  Deleted folder: " + aclFolder);
                else _log("  Warning: could not delete " + aclFolder);
            }
            else
            {
                _log("  ACLPlugin folder not found (skipping): " + aclFolder);
            }

            _log("✓ Phase 3 complete.");
            SetProgress(RangeDeleteAbpEnd, "Step 3 / 6 — ABP cleanup done.");
        }

        // ── Phase 4 — delete the character-capture WBPs ───────────────────────

        private void Phase4DeleteWbps(ImportResult result)
        {
            SetProgress(RangeDeleteWbpStart, "Step 4 / 6 — deleting character-capture WBP files…");
            _log("── Phase 4: delete WBP files ──");

            string wbpDir = PathLogic.CombineWith(_dataDir, PathLogic.WbpCommonsParts);
            if (!Fs.DirExists(wbpDir))
            {
                _log("  WBP directory not found — skipping: " + wbpDir);
                SetProgress(RangeDeleteWbpEnd, "Step 4 / 6 — WBP cleanup skipped.");
                return;
            }

            int deleted = DeleteFilesByStem(wbpDir, null, new HashSet<string>(
                PathLogic.WbpCommonsStems, StringComparer.OrdinalIgnoreCase),
                recursive: false);

            result.WbpDeleted = deleted;
            _log("✓ Phase 4 complete — " + deleted + " WBP file(s) deleted.");
            SetProgress(RangeDeleteWbpEnd, "Step 4 / 6 — WBP cleanup done.");
        }

        /// <summary>
        /// Deletes files whose base name (without extension) matches <paramref name="pattern"/>,
        /// or whose stem is in <paramref name="stems"/>. Companion files (.uexp/.ubulk/…) share the
        /// stem, so they are removed too — same behaviour as the Python implementation.
        /// </summary>
        private int DeleteFilesByStem(string folder, Regex pattern, HashSet<string> stems, bool recursive = true)
        {
            int deleted = 0;
            foreach (string dir in EnumerateDirectories(folder, recursive))
            {
                if (CancelRequested) break;
                string[] files;
                try { files = Directory.GetFiles(Fs.Lp(dir)); }
                catch { continue; }

                var stemsToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    string stem = Path.GetFileNameWithoutExtension(Fs.Strip(file));
                    if (pattern != null && pattern.IsMatch(stem)) stemsToDelete.Add(stem);
                    if (stems != null && stems.Contains(stem)) stemsToDelete.Add(stem);
                }
                if (stemsToDelete.Count == 0) continue;

                foreach (string file in files)
                {
                    string full = Fs.Strip(file);
                    if (!stemsToDelete.Contains(Path.GetFileNameWithoutExtension(full))) continue;
                    string error;
                    if (Fs.TryDeleteFile(full, out error))
                    {
                        deleted++;
                        _log("    Deleted: " + full);
                    }
                    else
                    {
                        _log("    Could not delete " + full + ": " + error);
                    }
                }
            }
            return deleted;
        }

        private static IEnumerable<string> EnumerateDirectories(string root, bool recursive)
        {
            if (recursive) return EnumerateDirectoriesRecursive(root);
            return new[] { root };
        }

        private static IEnumerable<string> EnumerateDirectoriesRecursive(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                yield return dir;
                string[] subdirs;
                try { subdirs = Directory.GetDirectories(Fs.Lp(dir)); }
                catch { continue; }
                foreach (string d in subdirs) stack.Push(Fs.Strip(d));
            }
        }

        // ── Phase 5 — move Data\Content → project Content ─────────────────────

        private void Phase5MoveContent(ImportResult result)
        {
            SetProgress(RangeMoveStart, "Step 5 / 6 — moving content into the output folder…");
            _log("── Phase 5: move content ──");

            if (!Fs.DirExists(_dataContentDir))
            {
                Warn(result, "Phase 5 skipped — " + _dataContentDir + " does not exist.");
                return;
            }

            Fs.CreateDir(_contentDir);
            _log("  " + _dataContentDir + "  →  " + _contentDir);

            // Gather the file list first so the progress bar is meaningful.
            var sources = new List<string>();
            foreach (string file in Fs.EnumerateFiles(_dataContentDir))
            {
                if (CancelRequested) break;
                sources.Add(file);
                if (sources.Count % 20000 == 0)
                {
                    SetProgress(RangeMoveStart, string.Format(
                        "Step 5 / 6 — scanning: {0:N0} file(s) so far…", sources.Count), force: true);
                }
            }

            int total = sources.Count;
            _log("  files to move: " + total.ToString("N0"));
            if (total == 0)
            {
                SetProgress(RangeMoveEnd, "Step 5 / 6 — nothing to move.");
                return;
            }

            var createdDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int index = 0;

            foreach (string src in sources)
            {
                if (CancelRequested) return;

                index++;
                string relative = src.Substring(_dataContentDir.Length).TrimStart('\\', '/');
                string dst = Path.Combine(_contentDir, relative);
                string dstDir = Path.GetDirectoryName(dst);

                if (dstDir != null && createdDirs.Add(dstDir))
                {
                    try { Fs.CreateDir(dstDir); }
                    catch (Exception ex)
                    {
                        _log("    Could not create " + dstDir + ": " + ex.Message);
                        result.Skipped++;
                        continue;
                    }
                }

                string error;
                if (Fs.TryMoveFile(src, dst, out error))
                {
                    result.Moved++;
                }
                else
                {
                    result.Skipped++;
                    _log("    Move failed: " + src + " → " + dst + ": " + error);
                }

                if (index % 25 == 0 || index == total)
                {
                    int percent = RangeMoveStart +
                        (int)Math.Round((double)index / total * (RangeMoveEnd - RangeMoveStart));
                    SetProgress(percent, string.Format("Step 5 / 6 — moving files: {0:N0} / {1:N0}",
                        index, total), force: index == total);
                }
            }

            _log(string.Format("✓ Phase 5 complete — moved: {0:N0}, failed: {1:N0}, total: {2:N0}",
                result.Moved, result.Skipped, total));
            SetProgress(RangeMoveEnd, "Step 5 / 6 — move done.");
        }

        // ── Phase 6 — cleanup ─────────────────────────────────────────────────

        private void Phase6Cleanup(ImportResult result)
        {
            SetProgress(RangeCleanStart, "Step 6 / 6 — removing the temporary Data folder…");
            _log("── Cleanup: removing Data folder ──");

            if (!Fs.DirExists(_dataDir))
            {
                _log("  Data folder already gone: " + _dataDir);
                SetProgress(RangeCleanEnd, "Step 6 / 6 — nothing to clean.");
                return;
            }

            if (Fs.DeleteDirectory(_dataDir))
            {
                _log("✓ Deleted Data folder: " + _dataDir);
            }
            else
            {
                Warn(result, "Cleanup — could not fully delete " + _dataDir +
                             ". Remove it manually to reclaim the disk space.");
            }
            SetProgress(RangeCleanEnd, "Step 6 / 6 — cleanup done.");
        }

        // ── Process plumbing ──────────────────────────────────────────────────

        /// <summary>
        /// Runs a console process, streaming stdout+stderr line by line (splitting on \r as well as
        /// \n so retoc's progress bar is visible). Returns the exit code, or -1 when it did not run.
        /// </summary>
        private int RunProcess(string fileName, string arguments, string workingDirectory,
                               string tag, Action<string> onLine, TimeSpan? timeout = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new PipelineException("Could not start " + fileName + ":\n" + ex.Message);
            }

            _activeProcess = process;
            var pumpOut = new Thread(delegate () { Pump(process.StandardOutput, onLine); });
            var pumpErr = new Thread(delegate () { Pump(process.StandardError, onLine); });
            pumpOut.IsBackground = pumpErr.IsBackground = true;
            pumpOut.Start();
            pumpErr.Start();

            DateTime started = DateTime.UtcNow;
            while (!process.WaitForExit(250))
            {
                if (CancelRequested)
                {
                    _log("Cancelling — terminating " + tag + "…");
                    try { process.Kill(); } catch { }
                    break;
                }
                if (timeout.HasValue && DateTime.UtcNow - started > timeout.Value)
                {
                    _log("Timed out after " + timeout.Value.TotalMinutes.ToString("0") +
                         " minutes — terminating " + tag + "…");
                    try { process.Kill(); } catch { }
                    break;
                }
            }

            try { process.WaitForExit(5000); } catch { }
            try { pumpOut.Join(2000); } catch { }
            try { pumpErr.Join(2000); } catch { }

            int exitCode = -1;
            try { exitCode = process.ExitCode; } catch { }
            try { process.Dispose(); } catch { }
            _activeProcess = null;
            return exitCode;
        }

        /// <summary>Kills whatever child process is currently running (called from the UI thread).</summary>
        public void KillActiveProcess()
        {
            Process p = _activeProcess;
            if (p == null) return;
            try { p.Kill(); } catch { }
        }

        private static void Pump(StreamReader reader, Action<string> onLine)
        {
            var sb = new StringBuilder(512);
            try
            {
                int c;
                while ((c = reader.Read()) >= 0)
                {
                    char ch = (char)c;
                    if (ch == '\r' || ch == '\n')
                    {
                        if (sb.Length > 0)
                        {
                            onLine(sb.ToString());
                            sb.Clear();
                        }
                    }
                    else if (ch != '\b')
                    {
                        sb.Append(ch);
                    }
                }
            }
            catch { }
            if (sb.Length > 0)
            {
                try { onLine(sb.ToString()); } catch { }
            }
        }

        // ── Progress helpers ──────────────────────────────────────────────────

        private void SetProgress(int percent, string status, bool force = false)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            DateTime now = DateTime.UtcNow;
            if (!force && percent == _lastPercent && (now - _lastProgressAt).TotalMilliseconds < 200)
                return;
            _lastPercent = percent;
            _lastProgressAt = now;
            try { _progress(percent, status); } catch { }
        }

        private void Warn(ImportResult result, string message)
        {
            result.Warnings.Add(message);
            _log("Warning: " + message);
        }
    }
}
