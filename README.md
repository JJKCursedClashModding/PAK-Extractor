# JJKCC-Unpacker

A small, dependency-free Windows tool that does the same thing as
**JJK Mod Kit → Setup → Import Game Assets** in the
[JujutsuKaisenCC](https://github.com/) Unreal project — without having to open the editor
first.

It is a single ~120 KB `JJKCC-Unpacker.exe` (WinForms, .NET Framework 4.8, which ships with
Windows) with three things in the window: an **input path**, an **output path** and a
**progress** display.

```
┌ JJK CC — Import Game Assets ──────────────────────────────────────────────┐
│ Input path  — Jujutsu Kaisen CC.exe, a game .pak, or the Content\Paks folder
│ [ C:\...\Jujutsu Kaisen CC.exe                        ] [File…] [Folder…]
│ Output path — the Unreal project folder (or its Content folder)
│ [ C:\...\JujutsuKaisenCC                              ] [Folder…]
│ Paks: C:\...\Content\Paks      Content: C:\...\JujutsuKaisenCC\Content
│ Step 1 / 6 — retoc: 12,345 / 49,404 files (25.0%)            [00:42]
│ [██████████░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░]
│ Progress log …                                            [Start Import]
└───────────────────────────────────────────────────────────────────────────┘
```

---

## What it does

Exactly the six steps of the in-editor pipeline, in the same order:

| # | Step | Detail |
|---|------|--------|
| 1 | **Unpack** | `retoc --aes-key <key> to-legacy . Data` inside the game's `Content\Paks`, producing loose files in `Paks\Data\Jujutsu Kaisen CC\Content` |
| 2 | **ABP stubs** | Creates a stub `AnimBlueprint` for every `ABP_*` / `*_ABP` found in the extracted content, via the project's own `stub_abp_from_folder.py` run in a **headless** `UnrealEditor-Cmd.exe` (nothing to click; it is skipped automatically when it cannot work) |
| 3 | **Delete ABPs** | Removes every `ABP_*` / `*_ABP` file (and its `.uexp`/`.ubulk` companions) from `Data`, plus `Content\Editor\ACLPlugin` |
| 4 | **Delete capture WPBs** | Removes `WBP_CharacterCaptureEnemy`, `WBP_CharacterCapturePlayer`, `WBP_CharacterCaptureSimple` from `Data\...\Widgets\Commons` |
| 5 | **Move** | Moves the extracted `Content` tree into the output folder, per-file progress, overwriting existing files |
| 6 | **Clean up** | Deletes the temporary `Paks\Data` folder (the ~50 GB) |

Expect step 1 to take 5–15 minutes and ~50 GB of free space on the drive that holds the game.
The editor is not required for steps 1 and 3–6; step 2 is skipped (with a clear note in the log)
when `UnrealEditor-Cmd.exe`, the project, or the skeletons are missing.

---

## Requirements

* Windows 10/11 (x64).
* The game installed via Steam (or anywhere else — point the input at the install).
* `retoc.exe` **plus `oo2core_9_win64.dll`** — normally `<project>\Tools\retoc.exe`; the tool
  also looks next to itself, in `.\Tools`, next to the paks, on `PATH`, or at `%RETOC%`.
* Unreal Engine 5.1 (only for step 2) — found through `%UNREAL_EDITOR_CMD%`, the project's
  JJK Mod Kit settings, the Epic Launcher registry entries, or `C:\Program Files\Epic Games\UE_5.*`.
* .NET Framework 4.8 (preinstalled on Windows 10 1903+).

---

## Build

```cmd
build.cmd
```

The exe lands in `dist\JJKCC-Unpacker.exe`. The build uses the .NET SDK (`dotnet build`) to
compile `src\JJKCC-Unpacker.csproj` for `net48`, so the result is a small framework-dependent
binary with no runtime to install.

Regenerate the icon with `python tools\make_icon.py` if you change it.

### Continuous integration

`.github/workflows/build.yml` builds the exe on `windows-latest`, runs the smoke test, and
uploads it as a workflow artifact. On every push to `main` (and on manual runs) it also
**creates or updates the [`beta` release](../../releases/tag/beta)** with the freshly built
`JJKCC-Unpacker.exe`, so

```
https://github.com/JJKCursedClashModding/PAK-Extractor/releases/download/beta/JJKCC-Unpacker.exe
```

always points at the newest good build. Pushing a `v*` tag additionally creates a normal
versioned release with the same asset.

Creating releases needs workflow write access — if the release step ever fails with a 403, set
**Settings → Actions → General → Workflow permissions → Read and write permissions**.

Run the same checks locally with:

```powershell
pwsh tools\smoke-test.ps1
```

---

## Usage

### Window

1. **Input path** — the game's `Jujutsu Kaisen CC.exe`, any `.pak` in `Content\Paks`, the
   `Content\Paks` folder, or the game root. `File…`/`Folder…` browse, or paste a path.
2. **Output path** — the Unreal project folder (the one with the `.uproject`). Its `Content`
   folder is used. You can also point it at any `Content` folder to do a standalone unpack.
3. Press **Start Import**, read the confirmation, and watch the progress.

Both fields are pre-filled: the output from the last run (or the first
`…\Documents\Unreal Projects\*` folder that has a `.uproject`), and the input from the project's
stored Game Exe Path (or the default Steam location).

Every run writes a full log to `%LOCALAPPDATA%\JJKCC-Unpacker\logs\` (the **Log folder** button
opens it).

If a `Data` folder is left over from a previous run, a dialog offers to **reuse** it (skip the
slow unpack) or **delete** it and unpack from scratch.

### Command line

```cmd
JJKCC-Unpacker.exe --cli --input "<game exe or paks>" --output "<project folder>" ^
                   [--no-stubs] [--reuse-data | --fresh] [--quiet]
```

Useful extras:

```cmd
:: check that retoc, the AES key and the install work — nothing is written
JJKCC-Unpacker.exe --verify-paks --input "<game exe>" --output "<project folder>"

:: print everything that would happen, change nothing
JJKCC-Unpacker.exe --dry-run --input "<game exe>" --output "<project folder>"

JJKCC-Unpacker.exe --help
```

Exit codes: `0` success, `1` failed, `2` cancelled, `3` finished with warnings.

From PowerShell, wait for the process (`Start-Process -Wait`) — like any GUI-subsystem exe it
does not block the prompt by itself. In `cmd.exe` use `start /wait`.

---

## Where paths and tools come from

* **Paks folder** — derived from the picked `.exe` (`<exe>\..\..\..\Content\Paks`, then any
  parent that has one) or by walking up from a picked `.pak` / folder.
* **Content folder** — `<project root>\Content` when the output folder (or a parent) contains a
  `.uproject`, otherwise the output folder itself.
* **retoc** — `%RETOC%`, next to the exe, `<exe>\Tools`, `<project>\Tools`, the paks folder, `PATH`.
* **UnrealEditor-Cmd** — `%UNREAL_EDITOR_CMD%`, the project's JJK Mod Kit setting
  (`UeEditorCmdPath`), `HKLM\SOFTWARE\EpicGames\Unreal Engine\<version>`, then
  `C:\Program Files\Epic Games\UE_5.*`.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| `retoc.exe was not found` | Put `retoc.exe` + `oo2core_9_win64.dll` in `<project>\Tools`, or set `%RETOC%` |
| `Could not find a Content\Paks folder…` | Point the input at `Jujutsu Kaisen CC.exe` or at `…\Content\Paks` |
| retoc exits with a non-zero code | Close the game (locked paks), check the log; `--verify-paks` isolates retoc from everything else |
| Step 2 is skipped | No `UnrealEditor-Cmd.exe` found, the editor was already running, or the project has no skeletons yet. Run **JJK Mod Kit → Asset Tools → Stub ABP From Folder** inside the editor afterwards |
| Assets missing in the Content Browser | Restart the editor — it indexes `Content` on startup |
| `retoc` progress bar stays at 0 % for a while | Normal: retoc hides its progress bar when its output is piped, so the bar animates instead |

---

## Layout

```
build.cmd                  one-command build -> dist\JJKCC-Unpacker.exe
.github\workflows\build.yml  CI: build + smoke test + 'beta' release
src\JJKCC-Unpacker.csproj  net48 WinForms project
src\Program.cs             entry point, CLI parsing, console attach, --diag
src\MainForm.cs            the window: input / output / progress
src\Pipeline.cs            the six steps
src\PathLogic.cs           path + tool discovery (mirrors the project's Python)
src\Fs.cs                  long-path-safe file helpers
src\AppSettings.cs         saved paths + run logs
src\Dpi.cs                 per-monitor DPI helpers
src\app.manifest           DPI awareness, long paths, asInvoker
tools\make_icon.py         icon generator
tools\smoke-test.ps1       build smoke test (also used by CI)
```
