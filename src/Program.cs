using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace JjkccUnpacker
{
    internal static class Program
    {
        private const string MutexName = "Global\\JJKCC-Unpacker-ImportGameAssets";

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        [STAThread]
        private static int Main(string[] args)
        {
            Dpi.EnableAwareness();
            var cli = CliOptions.Parse(args);

            if (cli.Help)
            {
                AttachParentConsole();
                PrintHelp();
                return 0;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    string message = "JJKCC-Unpacker is already running.";
                    if (cli.Cli) Console.Error.WriteLine(message);
                    else MessageBox.Show(message, "Import Game Assets", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
                }

                if (cli.Diag) return RunDiagnostics();
                if (cli.Cli) return RunCli(cli);
                return RunGui();
            }
        }

        private static int RunGui()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object sender, ThreadExceptionEventArgs e)
            {
                MessageBox.Show("Unexpected error:\n\n" + e.Exception, "Import Game Assets",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Application.Run(new MainForm());
            return 0;
        }

        private static int RunCli(CliOptions cli)
        {
            AttachParentConsole();

            string logFile = AppSettings.CreateLogFile();
            var log = new RunLog(logFile);

            var options = new ImportOptions
            {
                Input = cli.Input,
                Output = cli.Output,
                DryRun = cli.DryRun,
                NonInteractive = true,
                SkipEditorStubs = cli.NoStubs,
                ReuseExistingData = cli.ReuseData,
                FreshData = cli.Fresh,
                VerifyPaks = cli.VerifyPaks,
            };

            if (string.IsNullOrWhiteSpace(options.Input) || string.IsNullOrWhiteSpace(options.Output))
            {
                Console.Error.WriteLine("Both --input and --output are required in CLI mode.");
                Console.Error.WriteLine();
                PrintHelp();
                return 1;
            }

            Console.WriteLine("JJK CC — Import Game Assets");
            if (logFile != null) Console.WriteLine("Log: " + logFile);
            Console.WriteLine();

            var pipeline = new ImportPipeline(options,
                delegate (string line) { Console.WriteLine(line); log.Write(line); },
                delegate (int percent, string status)
                {
                    if (!cli.Quiet)
                    {
                        Console.Write("\r" + status.PadRight(110).Substring(0, 110) + "  " + percent.ToString().PadLeft(3) + "%");
                    }
                });

            ImportResult result = pipeline.Run();
            if (!cli.Quiet) Console.WriteLine();

            Console.WriteLine();
            Console.WriteLine(result.Message);
            if (result.Warnings.Count > 0)
            {
                Console.WriteLine("Warnings:");
                foreach (string warning in result.Warnings) Console.WriteLine("  - " + warning);
            }
            Console.WriteLine("Moved: {0:N0}   Failed: {1:N0}", result.Moved, result.Skipped);

            if (result.Cancelled) return 2;
            if (!result.Success) return 1;
            return result.HadWarnings ? 3 : 0;
        }

        private static int RunDiagnostics()
        {
            AttachParentConsole();
            using (var form = new MainForm())
            {
                Console.WriteLine("before show: " + form.DiagnosticsText);
                Console.WriteLine("DeviceDpi                 = " + form.DeviceDpi);
                Console.WriteLine("AutoScaleDimensions       = " + form.AutoScaleDimensions);
                Console.WriteLine("CurrentAutoScaleDimensions= " + form.CurrentAutoScaleDimensions);
                Console.WriteLine("Size                      = " + form.Size);
                Console.WriteLine("MinimumSize               = " + form.MinimumSize);

                form.ShownHook = delegate
                {
                    Console.WriteLine("after show : " + form.DiagnosticsText);
                };
                form.Show();
                for (int i = 0; i < 20; i++)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(50);
                }
                Console.WriteLine("settled    : " + form.DiagnosticsText);
            }
            return 0;
        }

        private static void AttachParentConsole()
        {
            try
            {
                if (!AttachConsole(-1)) return;
                var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                Console.SetOut(stdout);
                Console.SetError(stderr);
            }
            catch { }
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"JJK CC — Import Game Assets (standalone)
Replicates JJK Mod Kit → Setup → Import Game Assets.

Usage:
  JJKCC-Unpacker.exe                       Launch the window (input, output, progress)
  JJKCC-Unpacker.exe --cli --input <p> --output <p> [options]

Options:
  --input  <path>   Game executable, a .pak file, the Content\Paks folder, or the game root.
  --output <path>   Unreal project folder (with the .uproject) or a Content folder to unpack into.
  --cli             Run without a window (writes progress to this console).
  --dry-run         Resolve everything, print the plan, change nothing.
  --yes             Non-interactive (implied by --cli).
  --no-stubs        Skip the headless-editor AnimBlueprint stub step.
  --reuse-data      Reuse an existing Paks\Data folder instead of unpacking again.
  --fresh           Delete an existing Paks\Data folder and unpack from scratch.
  --verify-paks     Ask retoc to read the game paks without writing anything
                    (a safe way to check that retoc, the AES key and the install work).
                    Stops after step 1.
  --quiet           No progress line in CLI mode.
  --diag            Print UI scaling diagnostics and exit.
  --help            This text.

Exit codes (CLI):
  0 success, 1 failed, 2 cancelled, 3 finished with warnings.

Environment:
  RETOC                 Path to retoc.exe (default: searched next to this tool / <project>\Tools).
  UNREAL_EDITOR_CMD     Path to UnrealEditor-Cmd.exe (used only for the ABP stub step).");
        }
    }

    internal sealed class CliOptions
    {
        public bool Cli;
        public bool Help;
        public bool DryRun;
        public bool NoStubs;
        public bool ReuseData;
        public bool Fresh;
        public bool VerifyPaks;
        public bool Quiet;
        public bool Diag;
        public string Input;
        public string Output;

        public static CliOptions Parse(string[] args)
        {
            var options = new CliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a.ToLowerInvariant())
                {
                    case "--cli": options.Cli = true; break;
                    case "--help": case "-h": case "/?": options.Help = true; break;
                    case "--dry-run": options.DryRun = true; options.Cli = true; break;
                    case "--yes": case "-y": break; // --cli is already non-interactive
                    case "--no-stubs": options.NoStubs = true; break;
                    case "--reuse-data": options.ReuseData = true; break;
                    case "--fresh": options.Fresh = true; break;
                    case "--verify-paks": options.VerifyPaks = true; options.Cli = true; break;
                    case "--quiet": case "-q": options.Quiet = true; break;
                    case "--diag": options.Diag = true; options.Cli = true; break;
                    case "--input": case "-i": options.Input = Next(args, ref i); options.Cli = true; break;
                    case "--output": case "-o": options.Output = Next(args, ref i); options.Cli = true; break;
                    default:
                        if (!a.StartsWith("-") && options.Input == null) { options.Input = a; options.Cli = true; }
                        else if (!a.StartsWith("-") && options.Output == null) { options.Output = a; options.Cli = true; }
                        break;
                }
            }
            return options;
        }

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 < args.Length) return args[++i];
            return null;
        }
    }
}
