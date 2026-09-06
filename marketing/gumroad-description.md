# Source Manager for Unreal Engine
### The free tool that takes you from empty PC to a running Unreal Editor — no build engineer required.

**Setting up Unreal shouldn't feel like a boss fight.**

You know the drill: three wiki tabs open, a forum thread from 2021, guessing which Visual Studio components you actually need, an hour-long build that fails at 97%, and then — just when the editor finally starts — *"Unable to launch ShaderCompileWorker."*

Source Manager turns that entire gauntlet into buttons. Click them in order. Ship games instead.

---

## 🎮 Works with the engine you already have

Not just source builds. The app scans your PC and finds **every** Unreal Engine on it — precompiled Epic Games Launcher installs *and* GitHub source trees — and points itself at the one you pick.

Precompiled engine selected? Everything that can't apply to it quietly disappears: no clone, no Setup.bat, no engine compile. Perforce sync, project rebuilds, dedicated-server builds and launching all work exactly the same.

## 🕐 Your first hour, not your first week

The app opens on a built-in step-by-step guide. On a **completely fresh Windows machine**, a new team member can:

**1️⃣ Install everything** — Git, Perforce, and Visual Studio 2022 *with the exact workloads Unreal requires*, each one click. The app detects what's missing and fixes it. No more "which components do I need?" roulette.

**2️⃣ Get an engine** — pick one already installed on the PC, or verify GitHub↔Epic access, choose a branch (5.x, release, ue5-main) and run the whole chain with one button: clone → Setup.bat → GenerateProjectFiles.bat, with live logs and a disk-space reality check *before* you commit your evening to it.

**3️⃣ Build the editor** — any target, any configuration, real progress bar. ShaderCompileWorker builds automatically, so the classic startup error simply never happens.

**4️⃣ Connect Perforce** — server, login, workspace. Your password is never stored anywhere; only Perforce's own session ticket.

**5️⃣ Sync & Launch** — the daily-driver button: pull latest → rebuild engine → rebuild your project's modules → open the editor. Every step toggleable. Most mornings you'll use exactly one click.

## 📂 Pick your project — the engine follows

No more typing paths, and no more opening a 5.7 project against your 5.8 engine.

Source Manager finds the projects on your machine and puts them in a dropdown — recent projects, your Unreal Projects folder, and a quick sweep of your drives. **Pick one and the whole app switches to the engine that project actually belongs to.** It reads the project's engine association the way Unreal itself does, and when a bare version like "5.8" could mean two installs, the project's own generated files and last build decide which one it really is.

## 🌿 Branches — one profile per stream

Working in more than one stream? Make a branch for each. It remembers its own stream, workspace, sync path, changelist, project and engine — switch branch and *everything* repoints in one go.

Sync & Launch puts the workspace on that branch's stream **before** syncing, so you can never sync one branch and launch another. That's an afternoon you get to keep.

## 🔁 Perforce, without leaving the app

- Log in, pick a workspace, sync with force/parallel options, pinned to a changelist or label if you want
- `p4 switch` onto a stream from a dropdown
- See your pending changelists and every file each one holds
- Submit the one you pick, behind a confirmation naming the server, workspace and files

## 🖥️ Dedicated servers, one click

Creates your project's dedicated-server target if it doesn't have one, compiles it against the selected engine, and launches it with your own arguments. Works with a source build *and* with a precompiled launcher engine.

## ⬆️ Know when your engine is out of date

The app checks your engine version against Epic's release tags and tells you when a newer patch shipped. A cloned source tree can be moved onto it right there — checkout, Setup.bat for that version's dependencies, regenerate project files — and local changes to engine code block the update instead of being overwritten.

## 🔍 And when something *is* broken…

Hit **Sanity Check**. It tests the entire chain — tools, engine, dependencies, VS components, Perforce ticket, workspace, stream, built binaries — and tells you *exactly* which tab fixes each problem. It even knows things like "your login ticket expired" and "your workspace is on a different stream than this branch."

Moving a launcher-created project to your source build? The infamous **"Missing Modules"** dialog has a dedicated fix built in: one checkbox wipes the stale Intermediate/Binaries/DDC, regenerates project files and rebuilds your project's modules against the new engine on the way to launch — and it checks your enabled plugins exist before it starts, naming any that don't.

## 📦 What you get — completely free

- ✅ One-click Windows installer (per-user, **no admin rights needed**)
- ✅ Fully self-contained — no .NET, no runtimes, no dependencies to install first
- ✅ Nothing phones home; settings live in your own user profile
- ✅ Every long operation streams its output to a log you can read, and can be cancelled
- ✅ **Free forever. No license keys, no upsells, no "pro version".** A community tool, plain and simple.

## ✅ Requirements

- Windows 10/11 (64-bit)
- An Unreal Engine — from the Epic Games Launcher, or built from source with the app's help
- For a source build: an Epic Games account linked to GitHub (free, required by Epic for engine source access — the app walks you through it) and ~200 GB free disk space
- A Perforce server for the sync features (everything else works without one)

## ❓ FAQ

**Do I need a source build to use this?**
No. Launcher engines are fully supported — the app finds them automatically and hides everything that only makes sense for source trees.

**Does this include Unreal Engine source code?**
No — the source comes straight from Epic's official GitHub repository using *your* Epic-linked account, under Epic's own license. This tool automates everything around it.

**Which engine versions?**
Any UE5 branch on Epic's GitHub (release, 5.x, ue5-main), and any launcher-installed UE5 version.

**My teammates aren't build-pipeline people. Will they manage?**
That's exactly who this is for. Install → follow the built-in guide → click the buttons in order.

**Windows shows "Windows protected your PC"?**
It's an unsigned community tool — click "More info → Run anyway". The code does nothing your own terminal wouldn't: it drives Epic's official scripts, git, and p4.

---

💙 *Free community tool. If it saves your team a day of setup pain, tell another dev about it.*

*Not affiliated with, endorsed by, or sponsored by Epic Games, Inc. Unreal and Unreal Engine are trademarks or registered trademarks of Epic Games, Inc. in the US and elsewhere. Perforce is a trademark of Perforce Software, Inc.*
