# Folder Notes — Working Instructions

This project uses **spec-driven development**. The `.md` files in `specs/` are the source of truth.
Code implements the specs; the specs do not describe the code after the fact.

## The workflow (follow it every time)

1. **Read before you build.** Before starting any task, read:
   - `specs/00-vision.md` (always: it has the non-negotiable principles)
   - the spec(s) linked from the task in `specs/07-tasks.md`
2. **Spec first, code second.** If a change alters behavior, UI, data shape or architecture,
   update the relevant spec **in the same change, before writing code**. If a spec is ambiguous,
   resolve the ambiguity in the spec (or ask) rather than guessing in code.
3. **Trace everything.** Every task references requirement IDs (`REQ-xxx`). Every requirement has
   acceptance criteria. Tests are named after or tagged with the requirement they verify.
4. **One task at a time.** Pick the next unchecked task in `specs/07-tasks.md`, implement it,
   verify its "Done when" criteria, then tick it `[x]`. Don't start the next phase while the
   current phase's exit criteria are unmet.
5. **Record decisions.** Any non-obvious technical choice goes into `specs/08-decisions.md` as a
   short ADR (context → decision → consequences). Don't silently reverse an ADR; supersede it.
6. **Spikes produce knowledge, not product code.** Spike tasks (Phase 0) end with findings written
   into the relevant spec plus an ADR. Spike code lives in `spikes/` and is never shipped.
7. **Track progress.** At the end of every task or session, update `specs/09-progress.md`: task
   row (status, date, commit), phase counts, measurements, open items, blockers, and a log entry.
   Start each session by reading it to see where things stand.

## Spec index

| File | What it defines |
|---|---|
| `specs/00-vision.md` | Problem, users, goals, non-goals, principles |
| `specs/01-requirements.md` | User stories + EARS requirements with acceptance criteria |
| `specs/02-architecture.md` | Components, processes, tech stack, IPC |
| `specs/03-data-model.md` | Storage schema, folder identity, search index |
| `specs/04-explorer-integration.md` | Context menu, companion panel, tooltip shell extension |
| `specs/05-ui-design.md` | Screens, layout, visual language, interactions |
| `specs/06-testing.md` | Test strategy, what must be tested and how |
| `specs/07-tasks.md` | Phased implementation plan (the backlog) |
| `specs/08-decisions.md` | Architecture decision records |
| `specs/09-progress.md` | **Where we are:** phase/task status, measurements, open items, blockers, log |

## Engineering rules

- **Never destabilize Explorer.** Anything that loads inside `explorer.exe` (the shell extension)
  must be native, tiny, read-only, time-boxed, and must fail silently. See `specs/04`.
- **Never modify user folders without explicit opt-in.** No `desktop.ini`, no sidecar files, no
  attribute changes unless the user turns that feature on for a folder.
- Target: Windows 10 22H2+ and Windows 11. x64 first, ARM64 later.
- C#: .NET 10 (LTS), Windows App SDK 2.x, nullable enabled, warnings as errors. Typed COM interop
  only, never `dynamic` (ADR-007). The app ships a PerMonitorV2 `app.manifest` (S2). 
- C++: C++20, `/utf-8 /W4 /WX /MT`, no exceptions crossing COM boundaries.
