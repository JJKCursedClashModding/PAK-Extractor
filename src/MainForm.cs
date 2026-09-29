using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace JjkccUnpacker
{
    /// <summary>
    /// The whole UI: an input path, an output path and a progress display
    /// (status line, progress bar and a live log).
    ///
    /// The layout is written once in 96-DPI pixels and re-applied with a DPI scale factor,
    /// which keeps it correct on 100%–400% displays (WinForms' own autoscaling is unreliable
    /// for fully code-built forms).
    /// </summary>
    internal sealed class MainForm : Form
    {
        private const int DesignWidth = 820;
        private const int DesignHeight = 540;

        private readonly TextBox _txtInput = new TextBox();
        private readonly TextBox _txtOutput = new TextBox();
        private readonly TextBox _txtLog = new TextBox();
        private readonly Label _lblInputCaption = new Label();
        private readonly Label _lblOutputCaption = new Label();
        private readonly Label _lblResolved = new Label();
        private readonly Label _lblStatus = new Label();
        private readonly Label _lblLogCaption = new Label();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly Button _btnStart = new Button();
        private readonly Button _btnCancel = new Button();
        private readonly Button _btnInputFile = new Button();
        private readonly Button _btnInputFolder = new Button();
        private readonly Button _btnOutputFolder = new Button();
        private readonly Button _btnOpenLogs = new Button();
        private readonly System.Windows.Forms.Timer _uiTimer = new System.Windows.Forms.Timer();

        private readonly List<Placement> _placements = new List<Placement>();
        private float _scale = 1f;

        private ImportPipeline _pipeline;
        private Thread _worker;
        private RunLog _runLog;

        /// <summary>Set by --diag so the form can measure itself after it is actually shown.</summary>
        internal Action ShownHook;

        private int _lastPercent;
        private string _statusBase = "Ready.";
        private DateTime _lastProgressAt = DateTime.MinValue;
        private DateTime _runStartedAt;
        private int _logChars;
        private bool _layoutReady;

        private sealed class Placement
        {
            public Control Control;
            public AnchorStyles Anchors;
            public int X, Y, Width, Height;
        }

        public MainForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            BuildUi();
        }

        private int S(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        /// <summary>Used by --diag to check DPI scaling on the current machine.</summary>
        public string DiagnosticsText
        {
            get
            {
                return "DeviceDpi=" + DeviceDpi +
                       "  windowDpi=" + Dpi.ForWindow(Handle) +
                       "  scale=" + _scale.ToString("0.###") +
                       "  ClientSize=" + ClientSize +
                       "  log=" + _txtLog.Bounds +
                       "  start=" + _btnStart.Bounds;
            }
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private void BuildUi()
        {
            Text = "JJK CC — Import Game Assets";
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

            _lblInputCaption.Text = "Input path — Jujutsu Kaisen CC.exe, a game .pak, or the Content\\Paks folder";
            _lblInputCaption.AutoSize = false;

            _txtInput.TextChanged += delegate { UpdateResolvedHint(); };

            _btnInputFile.Text = "File…";
            _btnInputFile.Click += BrowseInputFile;

            _btnInputFolder.Text = "Folder…";
            _btnInputFolder.Click += delegate { BrowseInputFolder(); };

            _lblOutputCaption.Text = "Output path — the Unreal project folder (or its Content folder)";
            _lblOutputCaption.AutoSize = false;

            _txtOutput.TextChanged += delegate { UpdateResolvedHint(); };

            _btnOutputFolder.Text = "Folder…";
            _btnOutputFolder.Click += delegate { BrowseOutputFolder(); };

            _lblResolved.ForeColor = SystemColors.GrayText;
            _lblResolved.AutoSize = false;
            _lblResolved.AutoEllipsis = true;
            _lblResolved.Text = "Paks: —     Content: —";

            _lblStatus.AutoSize = false;
            _lblStatus.AutoEllipsis = true;
            _lblStatus.Text = _statusBase;

            _progress.Minimum = 0;
            _progress.Maximum = 100;

            _lblLogCaption.AutoSize = false;
            _lblLogCaption.ForeColor = SystemColors.GrayText;
            _lblLogCaption.Text = "Progress log";

            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.WordWrap = false;
            _txtLog.BackColor = Color.FromArgb(250, 250, 250);
            _txtLog.Font = new Font("Consolas", 8.5f, FontStyle.Regular, GraphicsUnit.Point);

            _btnOpenLogs.Text = "Log folder";
            _btnOpenLogs.Click += delegate { OpenLogFolder(); };

            _btnCancel.Text = "Cancel";
            _btnCancel.Enabled = false;
            _btnCancel.Click += delegate { RequestCancel(); };

            _btnStart.Text = "Start Import";
            _btnStart.Click += delegate { StartImport(); };

            AcceptButton = _btnStart;

            //       control             anchors                                                 x    y    w    h
            Place(_lblInputCaption, AnchorStyles.Top | AnchorStyles.Left, 14, 12, 700, 18);
            Place(_txtInput, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, 14, 32, 580, 24);
            Place(_btnInputFile, AnchorStyles.Top | AnchorStyles.Right, 602, 31, 86, 26);
            Place(_btnInputFolder, AnchorStyles.Top | AnchorStyles.Right, 694, 31, 112, 26);
            Place(_lblOutputCaption, AnchorStyles.Top | AnchorStyles.Left, 14, 68, 700, 18);
            Place(_txtOutput, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, 14, 88, 580, 24);
            Place(_btnOutputFolder, AnchorStyles.Top | AnchorStyles.Right, 694, 87, 112, 26);
            Place(_lblResolved, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, 14, 120, 792, 18);
            Place(_lblStatus, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, 14, 148, 792, 18);
            Place(_progress, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, 14, 168, 792, 20);
            Place(_lblLogCaption, AnchorStyles.Top | AnchorStyles.Left, 14, 196, 300, 16);
            Place(_txtLog, AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                  14, 216, 792, 276);
            Place(_btnOpenLogs, AnchorStyles.Bottom | AnchorStyles.Left, 14, 502, 96, 28);
            Place(_btnCancel, AnchorStyles.Bottom | AnchorStyles.Right, 614, 502, 92, 28);
            Place(_btnStart, AnchorStyles.Bottom | AnchorStyles.Right, 714, 502, 92, 28);

            _uiTimer.Interval = 500;
            _uiTimer.Tick += delegate { OnUiTimer(); };
        }

        private void Place(Control control, AnchorStyles anchors, int x, int y, int width, int height)
        {
            control.Anchor = AnchorStyles.None;
            _placements.Add(new Placement
            {
                Control = control,
                Anchors = anchors,
                X = x,
                Y = y,
                Width = width,
                Height = height,
            });
            Controls.Add(control);
        }

        private void ApplyLayout()
        {
            if (!_layoutReady)
            {
                SuspendLayout();
                ClientSize = new Size(S(DesignWidth), S(DesignHeight));
                MinimumSize = new Size(S(640), S(460));
            }

            foreach (Placement p in _placements)
            {
                p.Control.Anchor = AnchorStyles.None;
                p.Control.SetBounds(S(p.X), S(p.Y), S(p.Width), S(p.Height));
            }
            foreach (Placement p in _placements)
                p.Control.Anchor = p.Anchors;

            if (!_layoutReady)
            {
                ResumeLayout(true);
                _layoutReady = true;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _scale = Dpi.ForWindow(Handle) / 96f;
            ApplyLayout();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            _uiTimer.Start();
            if (ShownHook != null) ShownHook();

            PrefillPaths();
            UpdateResolvedHint();

            AppendLog("JJK CC — Import Game Assets (standalone)");
            AppendLog("Runs the same six steps as JJK Mod Kit → Setup → Import Game Assets.");
            AppendLog("Step 1 unpacks the game paks with retoc; the rest cleans up and moves the content.");
            AppendLog("");
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            _scale = e.DeviceDpiNew / 96f;
            ApplyLayout();
        }

        private void PrefillPaths()
        {
            AppSettings.Load();

            _txtOutput.Text = AppSettings.LastOutput ?? string.Empty;
            _txtInput.Text = AppSettings.LastInput ?? string.Empty;

            if (string.IsNullOrWhiteSpace(_txtOutput.Text))
            {
                string guess = GuessProjectFolder();
                if (guess != null) _txtOutput.Text = guess;
            }

            if (string.IsNullOrWhiteSpace(_txtInput.Text))
            {
                string projectRoot = PathLogic.FindProjectRoot(_txtOutput.Text);
                string configured = PathLogic.ReadConfiguredGameExe(projectRoot);
                if (configured != null)
                {
                    _txtInput.Text = configured;
                }
                else if (Fs.DirExists(PathLogic.DefaultSteamRoot))
                {
                    _txtInput.Text = Path.Combine(PathLogic.DefaultSteamRoot, PathLogic.GameExeRelative);
                }
            }
        }

        /// <summary>Looks for a JujutsuKaisenCC project under the usual Unreal Projects folder.</summary>
        private static string GuessProjectFolder()
        {
            try
            {
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string unrealProjects = Path.Combine(documents, "Unreal Projects");
                if (!Fs.DirExists(unrealProjects)) return null;

                var candidates = Directory.EnumerateDirectories(Fs.Lp(unrealProjects))
                                          .Select(Fs.Strip)
                                          .Where(DirectoryContainsUproject)
                                          .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                                          .ToList();

                return candidates.FirstOrDefault(
                           d => Path.GetFileName(d).IndexOf("Jujutsu", StringComparison.OrdinalIgnoreCase) >= 0)
                       ?? candidates.FirstOrDefault();
            }
            catch { return null; }
        }

        private static bool DirectoryContainsUproject(string dir)
        {
            try { return Directory.EnumerateFiles(Fs.Lp(dir), "*.uproject").Any(); }
            catch { return false; }
        }

        // ── Browsing ──────────────────────────────────────────────────────────

        private void BrowseInputFile(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select Jujutsu Kaisen CC.exe or a game .pak file";
                dialog.Filter =
                    "Game executable or pak (*.exe;*.pak)|*.exe;*.pak|" +
                    "Jujutsu Kaisen CC.exe|Jujutsu Kaisen CC.exe|" +
                    "Pak files (*.pak)|*.pak|" +
                    "All files (*.*)|*.*";
                dialog.CheckFileExists = true;

                string start = StartFolderForInput();
                if (!string.IsNullOrEmpty(start)) dialog.InitialDirectory = start;

                if (dialog.ShowDialog(this) == DialogResult.OK) _txtInput.Text = dialog.FileName;
            }
        }

        private void BrowseInputFolder()
        {
            string picked = PickFolder(
                "Select the game's Content\\Paks folder (or the game install folder)",
                StartFolderForInput());
            if (picked != null) _txtInput.Text = picked;
        }

        private void BrowseOutputFolder()
        {
            string start = _txtOutput.Text.Trim();
            string projectRoot = PathLogic.FindProjectRoot(start);
            if (projectRoot != null) start = projectRoot;
            if (!Fs.DirExists(start)) start = null;

            string picked = PickFolder("Select the Unreal project folder (or its Content folder)", start);
            if (picked != null) _txtOutput.Text = picked;
        }

        private string StartFolderForInput()
        {
            string raw = _txtInput.Text.Trim();
            if (Fs.FileExists(raw)) return Path.GetDirectoryName(raw);
            if (Fs.DirExists(raw)) return raw;

            string paks = PathLogic.FindPaksDir(raw);
            if (paks != null) return paks;

            if (Fs.DirExists(PathLogic.DefaultSteamRoot)) return PathLogic.DefaultSteamRoot;
            return null;
        }

        private string PickFolder(string description, string selectedPath)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = description;
                dialog.ShowNewFolderButton = true;
                if (!string.IsNullOrEmpty(selectedPath) && Fs.DirExists(selectedPath))
                    dialog.SelectedPath = selectedPath;
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        // ── Path feedback ─────────────────────────────────────────────────────

        private void UpdateResolvedHint()
        {
            string paks = PathLogic.FindPaksDir(_txtInput.Text);
            string content = null;
            string projectRoot = null;
            try { content = PathLogic.ResolveContentDir(_txtOutput.Text, out projectRoot); }
            catch { }

            var sb = new StringBuilder();
            sb.Append("Paks: ");
            sb.Append(paks ?? "— not found from this path");
            sb.Append("     Content: ");
            sb.Append(content ?? "—");
            if (projectRoot == null && content != null) sb.Append("  (standalone)");

            string retoc = PathLogic.FindRetoc(AppDomain.CurrentDomain.BaseDirectory, projectRoot, paks);
            if (retoc == null && paks != null)
                sb.Append("     retoc.exe: not found (put it in <project>\\Tools)");

            _lblResolved.Text = sb.ToString();
        }

        // ── Run ───────────────────────────────────────────────────────────────

        private void StartImport()
        {
            if (_worker != null) return;

            var options = new ImportOptions
            {
                Input = _txtInput.Text.Trim(),
                Output = _txtOutput.Text.Trim(),
            };

            if (options.Input.Length == 0 || options.Output.Length == 0)
            {
                MessageBox.Show(this, "Fill in both the input and the output path first.",
                    "Import Game Assets", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            AppSettings.LastInput = options.Input;
            AppSettings.LastOutput = options.Output;
            AppSettings.Save();

            _runLog = new RunLog(AppSettings.CreateLogFile());
            if (_runLog.Path != null) AppendLog("Run log: " + _runLog.Path);
            AppendLog("Input  : " + options.Input);
            AppendLog("Output : " + options.Output);
            AppendLog("");

            _pipeline = new ImportPipeline(options, OnPipelineLog, OnPipelineProgress)
            {
                ConfirmStart = ConfirmStart,
                ConfirmReuseExistingData = ConfirmReuseExistingData,
            };

            _runStartedAt = DateTime.Now;
            _lastProgressAt = DateTime.Now;
            _lastPercent = 0;
            _progress.Style = ProgressBarStyle.Marquee;
            SetRunning(true);
            SetStatus("Starting…");

            _worker = new Thread(delegate ()
            {
                ImportResult result = null;
                try { result = _pipeline.Run(); }
                catch (Exception ex) { OnPipelineLog("ERROR: " + ex); }
                try { BeginInvoke(new Action(delegate { FinishRun(result); })); }
                catch { }
            });
            _worker.IsBackground = true;
            _worker.Name = "import-pipeline";
            _worker.Start();
        }

        private void RequestCancel()
        {
            if (_pipeline == null) return;
            _pipeline.CancelRequested = true;
            _btnCancel.Enabled = false;
            SetStatus("Cancelling…");
            AppendLog("");
            AppendLog("Cancel requested…");
            _pipeline.KillActiveProcess();
        }

        private void FinishRun(ImportResult result)
        {
            SetRunning(false);
            _worker = null;

            if (result == null)
            {
                SetStatus("Failed.");
                MessageBox.Show(this, "The import stopped unexpectedly. See the log for details.",
                    "Import Game Assets", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (result.Cancelled)
            {
                SetStatus("Cancelled.");
                MessageBox.Show(this, result.Message, "Import Game Assets — Cancelled",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var summary = new StringBuilder();
            if (!result.Success)
            {
                summary.AppendLine("Import failed.");
                summary.AppendLine();
                summary.AppendLine(result.Message);
            }
            else if (result.HadWarnings)
            {
                summary.AppendLine(result.Message);
            }
            else
            {
                summary.AppendLine("Import complete.");
            }
            summary.AppendLine();
            summary.AppendLine("Moved into the project : " + result.Moved.ToString("N0") + " file(s)");
            if (result.Skipped > 0)
                summary.AppendLine("Could not be moved     : " + result.Skipped.ToString("N0") + " file(s)");
            summary.AppendLine("ABP files deleted      : " + result.AbpDeleted.ToString("N0"));
            summary.AppendLine("Capture WBPs deleted   : " + result.WbpDeleted.ToString("N0"));
            if (result.StubsCreated >= 0)
                summary.AppendLine("ABP stubs created      : " + result.StubsCreated.ToString("N0"));
            if (result.Warnings.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("Warnings:");
                foreach (string warning in result.Warnings) summary.AppendLine("  • " + warning);
            }
            summary.AppendLine();
            summary.AppendLine("Restart the Unreal Editor so the new assets appear in the Content Browser.");

            bool failed = !result.Success;
            SetStatus(failed ? "Failed." : (result.HadWarnings ? "Finished with warnings." : "Done."));
            MessageBox.Show(this, summary.ToString(),
                failed ? "Import Game Assets — Failed"
                       : (result.HadWarnings ? "Import Game Assets — Finished with warnings"
                                             : "Import Game Assets — Done"),
                MessageBoxButtons.OK,
                failed ? MessageBoxIcon.Error
                       : (result.HadWarnings ? MessageBoxIcon.Warning : MessageBoxIcon.Information));
        }

        private bool ConfirmStart(string message)
        {
            return (bool)Invoke(new Func<bool>(delegate
            {
                return MessageBox.Show(this, message, "Import Game Assets — Confirmation",
                           MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)
                       == DialogResult.Yes;
            }));
        }

        private bool? ConfirmReuseExistingData(string dataDir)
        {
            SetStatus("Checking the existing Data folder…");
            long files, size;
            Fs.Measure(dataDir, out files, out size);

            return (bool?)Invoke(new Func<bool?>(delegate
            {
                string message =
                    "A Data folder from an earlier run was found:\n\n" +
                    dataDir + "\n\n" +
                    files.ToString("N0") + " file(s), " + Fs.HumanSize(size) + "\n\n" +
                    "Yes    — reuse it and skip the unpack step (fast; good for a re-run)\n" +
                    "No     — delete it and unpack the game paks from scratch\n" +
                    "Cancel — stop here";

                DialogResult answer = MessageBox.Show(this, message,
                    "Import Game Assets — existing Data folder",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);

                if (answer == DialogResult.Yes) return (bool?)true;
                if (answer == DialogResult.No) return (bool?)false;
                return null;
            }));
        }

        // ── Pipeline callbacks (called from the worker thread) ────────────────

        private void OnPipelineLog(string message)
        {
            if (_runLog != null) _runLog.Write(message);
            try { BeginInvoke(new Action(delegate { AppendLog(message); })); }
            catch { }
        }

        private void OnPipelineProgress(int percent, string status)
        {
            try
            {
                BeginInvoke(new Action(delegate
                {
                    _lastPercent = percent;
                    _lastProgressAt = DateTime.Now;

                    if (_progress.Style == ProgressBarStyle.Marquee)
                        _progress.Style = ProgressBarStyle.Blocks;
                    _progress.Value = Math.Max(_progress.Minimum, Math.Min(_progress.Maximum, percent));

                    SetStatus(status);
                }));
            }
            catch { }
        }

        private void AppendLog(string message)
        {
            if (IsDisposed) return;
            string line = message + Environment.NewLine;

            if (_logChars > 400000)
            {
                _txtLog.Clear();
                _logChars = 0;
                _txtLog.AppendText("… earlier log output trimmed …" + Environment.NewLine);
            }

            _txtLog.AppendText(line);
            _logChars += line.Length;
        }

        private void SetStatus(string text)
        {
            _statusBase = text;
            UpdateStatusLabel();
        }

        private void UpdateStatusLabel()
        {
            string elapsed = _worker != null
                ? "     [" + (DateTime.Now - _runStartedAt).ToString(@"mm\:ss") + "]"
                : "";
            _lblStatus.Text = _statusBase + elapsed;
        }

        private void OnUiTimer()
        {
            if (_worker == null) return;

            UpdateStatusLabel();

            // A long silent stretch (retoc's progress bar is not always readable when its output
            // is piped) should still show motion.
            bool stale = (DateTime.Now - _lastProgressAt).TotalSeconds > 2.5;
            if (stale && _progress.Style != ProgressBarStyle.Marquee)
            {
                _progress.Style = ProgressBarStyle.Marquee;
            }
            else if (!stale && _progress.Style == ProgressBarStyle.Marquee)
            {
                _progress.Style = ProgressBarStyle.Blocks;
                _progress.Value = Math.Max(0, Math.Min(100, _lastPercent));
            }
        }

        private void SetRunning(bool running)
        {
            _btnStart.Enabled = !running;
            _btnInputFile.Enabled = _btnInputFolder.Enabled = !running;
            _btnOutputFolder.Enabled = !running;
            _txtInput.ReadOnly = _txtOutput.ReadOnly = running;
            _btnCancel.Enabled = running;

            if (!running && _progress.Style == ProgressBarStyle.Marquee)
            {
                _progress.Style = ProgressBarStyle.Blocks;
                _progress.Value = Math.Max(0, Math.Min(100, _lastPercent));
            }
        }

        private void OpenLogFolder()
        {
            try
            {
                string dir = Path.Combine(AppSettings.DataDir, "logs");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open the log folder:\n" + ex.Message,
                    "Import Game Assets", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_worker != null)
            {
                e.Cancel = true;
                MessageBox.Show(this,
                    "The import is still running. Press Cancel and wait for it to stop before closing.",
                    "Import Game Assets", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
