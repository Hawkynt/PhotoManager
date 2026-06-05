# Agent guide — PhotoManager

Working agreement for **all** coding agents (Claude Code, Codex, Copilot, …)
and human contributors working in this repository. These rules are not
optional. The full house spec lives in the `Hawkynt/project-template` repo
(`STANDARD.md`); this file is the per-repo distillation (it replaces the old
`CLAUDE.md`).

## What this is

A cross-platform **photo manager / lightweight DAM**: no database, no cloud —
folders + sidecar XMP are the source of truth. Solution `PhotoManager.slnx`
with `Hawkynt.PhotoManager.*` assemblies: `.Core` (metadata, ML keywording,
non-destructive develop), `.UI` (Avalonia), `.CLI`, `.Tests`, `.Benchmarks`.
Image format support comes from the `Hawkynt.FileFormats.Images` package
(sourced via the project-scoped `NuGet.Config` / `.local-nuget` while the
public listing validates).

## Commits

- **Group changes semantically/logically** — one service/feature/concern per
  commit; keep the detailed multi-line commit-body style used here.
- **Every subject line starts with a prefix**: `+` added · `-` removed ·
  `*` changed · `#` bug fixed · `!` critical todo.
- Never start a subject with "fix"/"bugfix"/"changed"/"modified".
- **No AI traces anywhere**: no `Co-Authored-By` AI lines, no "Generated
  with" footers, no agent mentions in messages, comments, or authorship.

## The loop (always, in this order)

1. **Before committing**: `dotnet build PhotoManager.slnx -c Release` and
   `dotnet test PhotoManager.Tests -c Release` (unit tier required;
   integration/performance tiers advisory) until green. UI changes get a
   manual Avalonia run. Update README/TODO.md when behavior or scope
   changes.
2. **Commit** (rules above) and **push**.
3. **Wait for CI**; on `main` a green CI triggers the nightly (prerelease +
   GFS prune, same-day replace). Fix and loop until everything is green.

Stable releases are **manual** (`gh workflow run release.yml`) — never cut
one unless explicitly asked.

## Code conventions

- Latest C# features; namespaces/assemblies are `Hawkynt.PhotoManager.*`
  (folders on disk stay `PhotoManager.*` for history continuity).
- **Non-destructive is the law**: originals are never modified — all edits
  live in sidecars; any code path that could touch an original needs an
  explicit test proving it doesn't.
- Core stays UI-free; everything Avalonia lives in `.UI`; automation goes
  through `.CLI`.

## README & repo conventions

- Standard frame: title → badges → one-line `>` blockquote; fixed emoji
  mapping for the standard sections (`## ✨ Features`,
  `## 🛠️ Build Instructions`, `## ❤️ Support`, `## 📜 License`).
- License is LGPL-3.0-or-later; the `## ❤️ Support` section and
  `.github/FUNDING.yml` stay intact.
