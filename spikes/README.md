# Spikes (Phase 0)

Throwaway experiments. **Never ship or copy this code into `src/`.** It uses late-bound COM,
which is banned in production by ADR-007. The *findings* live in the specs:
`specs/03-data-model.md` (S4) and `specs/04-explorer-integration.md` (S1, S2).

| Spike | Run | What it does |
|---|---|---|
| S1-tracker | `dotnet run -c Release -- auto [--no-poll]` · `-- watch` | Opens Explorer windows and tabs, navigates, switches, and measures tracker latency. `watch` prints the live active folder while you click around. |
| S2-dock | `dotnet build -c Release` then `bin\x64\Release\net9.0-windows10.0.19041.0\win-x64\S2Dock.exe auto` · `watch` | Docks a WinUI 3 panel to a fresh Explorer window. `auto` moves, snaps, maximizes, minimizes and closes it and checks the panel. `watch` lets you drag it by hand. |
| S3-infotip | `build.cmd`, then `regsvr32 out\FnS3InfoTip.dll` (HKCU only) and **restart Explorer**; `driver: S3Driver.exe bench` · `hover` (set `S3_SHOTS=<dir>` for screenshots). **Unregister afterwards** with `regsvr32 /u`, otherwise every folder shows the fake tip. | A C++ folder infotip handler that logs calls to `%TEMP%n-s3.log`. The driver measures latency and hovers folders in each view. |
| S4-identity | `dotnet run -c Release` | Rename/move/copy/recreate/recycle tests on NTFS file IDs. |

⚠️ `S1 auto` and `S2 auto` take over the desktop for 15–30 s (they open windows and send
keystrokes such as Ctrl+T, Ctrl+Tab and Win+Arrow). Don't type while they run.
