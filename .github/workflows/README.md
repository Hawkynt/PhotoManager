# CI/CD Pipeline — PhotoManager

> Everything in this folder is the automated pipeline. Workflows live here, scripts live in `scripts/`.

## Files

| File                            | Trigger                             | Purpose                                 |
|---------------------------------|-------------------------------------|-----------------------------------------|
| `ci.yml`                        | push + PR + `workflow_call`         | Build + tests + coverage                |
| `release.yml`                   | tag push `v*`                       | GitHub Release + NuGet push             |
| `nightly.yml`                   | CI success on `main`/`master`       | `nightly-YYYY-MM-DD` prerelease + GFS   |
| `_build.yml`                    | `workflow_call` (internal)          | CLI + UI + NuGet build block            |
| `scripts/version.pl`            | invoked by workflows                | `X.Y.Z.BUILD` (csproj scan)             |
| `scripts/update-changelog.mjs`  | invoked by workflows                | Bucketise commits into CHANGELOG.md     |
| `scripts/prune-nightlies.mjs`   | invoked by workflows                | 7+4+3 GFS retention of nightlies        |

## How it works

```
push/PR ──► ci.yml ─────────────────┐
                                    │ on success on main
                                    ▼
  tag v* ──► release.yml ──► _build.yml ──► GH Release
                                    ▲
                                    │
                 workflow_run ──► nightly.yml ──► prerelease + GFS prune
```

## Why

- **No cron triggers** — event-driven only.
- **Release calls CI** via `workflow_call`; tests and releases stay in lockstep.
- **Nightly builds the SHA CI validated**, not branch tip.
- **`_build.yml` is shared** between release and nightly — one recipe, two consumers.
- **3-generation (GFS) retention**: 7 daily + 4 weekly + 3 monthly, never "keep last N".

## Release artifacts

| Artifact                                        | Produced by          | Destination         |
|-------------------------------------------------|----------------------|---------------------|
| `PhotoManager-CLI-win-x64-<version>.zip`        | release + nightly    | GitHub Release      |
| `PhotoManager-CLI-linux-x64-<version>.zip`      | release + nightly    | GitHub Release      |
| `PhotoManager-UI-win-x64-<version>.zip`         | release + nightly    | GitHub Release      |
| `PhotoManager.Core.<version>.nupkg`             | release only         | GitHub + nuget.org  |
