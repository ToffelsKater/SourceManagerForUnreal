# Source Manager for Unreal Engine

*(free community tool — not affiliated with Epic Games; "Unreal" and "Unreal Engine" are trademarks of Epic Games, Inc.)*

A Windows desktop app (WPF / .NET 9) that walks you through building **Unreal Engine from source** end‑to‑end:
check dependencies → clone the source → build → connect Perforce → one‑click **Sync & Launch**.

## Requirements

- Windows 10/11
- .NET 9 SDK (to build this app)
- A GitHub account [linked to your Epic Games account](https://www.unrealengine.com/ue-on-github) (the UnrealEngine repo is private)
- ~200 GB free disk for a full source build

The app itself checks for and can help install the rest (Git, Visual Studio 2022, Perforce).

## Run

```powershell
dotnet run --project D:\UnrealManager\UnrealManager.csproj
```

Or build a standalone exe:

```powershell
dotnet publish D:\UnrealManager\UnrealManager.csproj -c Release -r win-x64 --self-contained false
```

## Distributing to your team

Build the installer (requires Inno Setup 6: `winget install JRSoftware.InnoSetup`):

```powershell
.\build-installer.ps1          # -> dist\SourceManagerSetup.exe (Inno Setup)
.\build-installer.ps1 -Msi     # -> additionally builds the WiX MSI variant
```

Team members just double-click **`SourceManagerSetup.exe`**:

- Installs per-user (no admin needed); admins can pick an all-users install in the wizard
- Start Menu shortcut, optional desktop icon, "Launch after install" checkbox
- Shows up in *Apps & Features* for clean uninstall
- Running a newer installer upgrades the old version in place (the `AppId` in
  `installer\setup.iss` — and `UpgradeCode` in `installer\Product.wxs` — must never change;
  they're how upgrades find the old install)

Each user's settings live in their own `%AppData%\UnrealManager\config.json`.
Silent deployment (e.g. via a software-distribution tool): `SourceManagerSetup.exe /VERYSILENT /NORESTART`.

## The five tabs

| Tab | What it does |
|-----|--------------|
| **Dependencies** | Detects Visual Studio instances and verifies every workload/component Unreal needs (Desktop C++, Game C++, MSVC v143, Windows SDK, .NET). One click launches the VS Installer to add what's missing, or installs VS 2022 Community via winget. Also checks Git / p4 / p4v / dotnet on PATH. |
| **Get Source** | Tests GitHub access, lists branches, clones the repo, runs `Setup.bat` (binary dependencies) and `GenerateProjectFiles.bat` — individually or all in one click. Shows disk‑space warnings. |
| **Build** | Compiles any engine target (UnrealEditor, ShaderCompileWorker, …) in any configuration via `Build.bat`, with a live progress bar parsed from `[n/total]` compiler output. |
| **Perforce** | Connects to a Helix Core server, logs in (password piped to `p4 login` stdin — never stored), lists workspaces and streams, and syncs with optional force/parallel, optionally pinned to a changelist or label. Manages **branches** — one per stream, each holding its own stream, workspace, sync path, project and launch arguments — and switches the workspace between streams with `p4 switch`. Lists pending changelists with their open files and submits the selected one (`p4 submit`), after a confirmation showing server, workspace and file list. Launches P4V. |
| **Sync & Launch** | The UGS‑style workflow: pick a branch, then sync latest from Perforce → rebuild changed engine code → launch the editor, each step toggleable. The project is picked from a list of the `.uproject`s found on this PC (recent projects, the Unreal Projects folder and a bounded sweep of the fixed drives), and picking one switches the app to the engine that project's `EngineAssociation` names. A branch that names a stream switches the workspace onto it before syncing, so one branch is never synced and another launched. |

Every operation streams its output to the shared **Output Log** at the bottom, and all settings persist to
`%AppData%\UnrealManager\config.json`.

## Architecture

- **`Services/`** — process runner (async, streaming, cancellable, kills process trees), plus one service each for Visual Studio (`vswhere`/installer), the engine (`git`/batch files), Perforce (`p4`), and config persistence.
- **`ViewModels/`** — one MVVM view model per tab, all deriving from `PageViewModel` (busy state + cancellation + logging helpers).
- **`Views/` & `Themes/`** — WPF `DataTemplate`s per page, a dark theme, and the batched log console.

Nothing here shells out to anything you couldn't run by hand; the app is a friendly front‑end over Epic's own
`Setup.bat` / `GenerateProjectFiles.bat` / `Build.bat` and the `git` / `p4` CLIs.
