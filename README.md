# Folder Notes

Pin notes to Windows folders ("client files, don't delete until 2027", "build output, safe to wipe").
A side panel shows the note whenever the folder is open in File Explorer, hovering a folder shows
a tooltip, and you can search every note.

> **Status:** Phase 1 (core library). Not usable yet. See [`specs/07-tasks.md`](specs/07-tasks.md).

This project is **spec-driven**: read [`CLAUDE.md`](CLAUDE.md) and [`specs/`](specs/) before changing anything.

## Prerequisites

| Tool | Version | Used for |
|---|---|---|
| Windows | 10 22H2+ / 11 | |
| .NET SDK | 10.0.403+ (pinned in `global.json`) | Core, App, tests |
| Visual Studio 2022+ with **Desktop development with C++** | MSVC 14.44+, Windows SDK 10.0.26100+ | `FolderNotes.ShellExt` (C++), plus the bundled CMake + Ninja |

Visual Studio itself isn't needed for the C# parts: everything builds with the `dotnet` CLI.

## Build & test

```powershell
dotnet build FolderNotes.slnx -c Release
dotnet test --solution FolderNotes.slnx -c Release
eng\build-shellext.cmd Release x64      # -> artifacts\shellext\x64-Release\FolderNotes.ShellExt.dll
```

Run the app (scaffold only for now):

```powershell
src\FolderNotes.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\FolderNotes.exe
```

## Layout

```
specs/      source of truth (vision, requirements, architecture, tasks, ADRs)
spikes/     Phase 0 throwaway experiments, never shipped
src/
  FolderNotes.Core/      domain, storage, search, folder identity (no UI)
  FolderNotes.App/       WinUI 3 tray app, panel, search palette
  FolderNotes.ShellExt/  C++ in-proc shell extension (CMake)
tests/
  FolderNotes.Core.Tests/
eng/        build scripts
```
