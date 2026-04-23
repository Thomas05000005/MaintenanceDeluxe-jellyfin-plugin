# Development

## Building

```
dotnet build --configuration Release
```

- Requires .NET SDK **9.0** (any 9.x).
- Output DLL: `Jellyfin.Plugin.MaintenanceDeluxe/bin/Release/net9.0/Jellyfin.Plugin.MaintenanceDeluxe.dll`.
- The `banner.js` and `configPage.html` resources are embedded in the DLL via `<EmbeddedResource>` entries in the csproj — there's no external JS file to ship.

## Structure

```
Jellyfin.Plugin.MaintenanceDeluxe/
├── Api/
│   └── BannerController.cs       — REST endpoints: GET/POST /MaintenanceDeluxe/{maintenance,config,banner.js}
├── Configuration/
│   ├── configPage.html           — admin UI (tabs, form, release-notes builder)
│   └── (PluginConfiguration.cs resides one level up; see below)
├── Resources/
│   └── banner.js                 — client script: injected by JavaScript Injector, renders the overlay
├── ScheduledTasks/
│   └── MaintenanceScheduleTask.cs — 1-min tick: scheduled activation/deactivation, scheduled restart, startup consistency
├── MaintenanceHelper.cs          — activate/deactivate logic shared between controller and scheduler
├── Plugin.cs                     — entry point: GUID registration, JS Injector wiring
├── PluginConfiguration.cs        — XML-serialisable config model (MaintenanceSetting, ReleaseNoteSection, BannerMessage, etc.)
└── MaintenanceDeluxe.csproj
```

## Local test loop

A `docker/` folder provides a Compose file based on the LinuxServer.io Jellyfin image. The accompanying `Makefile` wires things up:

| Target     | Effect                                                                 |
|------------|------------------------------------------------------------------------|
| `deploy`   | First-run: fetch JavaScript Injector + File Transformation, build, up |
| `build`    | `dotnet build` and copy the DLL to the docker plugin dir               |
| `update`   | Build → copy DLL → restart container → tail logs                       |
| `up/down`  | Compose up/down                                                        |
| `restart`  | Just restart the jellyfin container                                    |
| `logs`     | Tail jellyfin container logs                                           |
| `clean`    | Remove `bin/`, `obj/`, and the docker `config/`                        |

Note that the Makefile uses bash-isms (`sed -i ''`, `jq`, `curl`, `python3`); on Windows run it from Git Bash or WSL.

## Client script — how the overlay gets on screen

1. `Plugin.cs` constructor locates the JavaScript Injector assembly via reflection, reads `Resources/banner.js` as an embedded resource, and calls `PluginInterface.RegisterScript(...)` with `requiresAuthentication: false`.
2. JavaScript Injector persists the script in its own config and serves it as part of `/JavaScriptInjector/public.js` (public endpoint, `[AllowAnonymous]`).
3. File Transformation intercepts every request for `index.html` and injects `<script defer src="../JavaScriptInjector/public.js">` just before `</body>`.
4. At page load, `banner.js` runs:
   - Short-circuits into preview mode if `md-preview=1` is in the URL or sessionStorage.
   - Otherwise fetches `/MaintenanceDeluxe/maintenance` (public endpoint) and calls `applyMaintenanceState()`.
5. `applyMaintenanceState()` either shows or removes the overlay depending on `isActive`. It also installs navigation watchers (`hashchange`, `popstate`, `viewshow`) and a `MutationObserver` so the overlay survives React-driven SPA transitions.

## Why a MutationObserver

Jellyfin 10.11 uses React Router and occasionally remounts large chunks of the body during navigation. Without the observer, our overlay would be removed the first time the admin navigated after maintenance was activated, never to come back until the next full reload. With it, if the overlay element disappears while `MAINTENANCE.isActive` is true and the admin hasn't dismissed it, we re-append immediately.

## Why an `@font-face` in base64

Safari in private mode and some Firefox configs refuse to download external fonts for privacy reasons, which would leave the title rendering in the fallback `Georgia`. Embedding Instrument Serif as base64 (~28 KB) keeps the visual identical everywhere.

## Why escape non-ASCII as `\uXXXX`

JavaScript Injector's `CustomJavaScriptController.cs` responds with `Content-Type: application/javascript` without a `charset`. Some browsers default to Latin-1 in that case, which would mangle French accents. Escaping every non-ASCII character in `banner.js` to a JS Unicode escape (`é`, `—`, etc.) sidesteps the issue entirely — the JS parser decodes the escape at parse time, regardless of the byte-level charset.

The helper lives at `scripts/escape_non_ascii.py`. Run it in two modes:
- `python scripts/escape_non_ascii.py <file.js>` — rewrites the file in place.
- `python scripts/escape_non_ascii.py --check <file.js>` — exits 1 if any non-ASCII char is present (used by CI).

## Continuous integration

Two GitHub Actions workflows guard the repo:

- **`.github/workflows/ci.yml`** — runs on every PR and push to `main`. Validates:
  - C# compiles (`dotnet build --configuration Release`).
  - `banner.js` contains only ASCII (via `scripts/escape_non_ascii.py --check`).
  - `banner.js` is syntactically valid JS (`node --check`).
  - The inline `<script>` block in `configPage.html` is syntactically valid JS (extracted via `scripts/check_inline_script.py` then `node --check`). This check would have caught the v0.1.11 and v0.1.12 regressions that silently froze the admin UI.
  - The version string agrees across `MaintenanceDeluxe.csproj`, `deploy/MaintenanceDeluxe/meta.json`, and `manifest.json` (via `scripts/check_version_coherence.py`).

- **`.github/workflows/release.yml`** — runs on any pushed tag matching `v*`. Re-runs all `ci.yml` checks as a gate, verifies the tag matches the declared versions, builds the DLL, zips it with `meta.json`, computes MD5, creates a GitHub release with the zip attached, patches `manifest.json` in place to set the real checksum + timestamp, and commits the patched manifest back to `main`.

## Publishing a new version

The workflow is now tag-driven. Everything the release workflow does is automated — you only decide the version number and changelog.

1. `make bump V=X.Y.Z` — sets `<AssemblyVersion>` and `<FileVersion>` to `X.Y.Z.0`.
2. Edit `deploy/MaintenanceDeluxe/meta.json`: update `version` to `X.Y.Z.0`, rewrite `changelog`, set `timestamp`.
3. Prepend a new entry to the `versions` array in `manifest.json` at the repo root. The `checksum` and `timestamp` fields are filled in by the release workflow — leave them as placeholders (`""` is fine) or reuse the previous values; they get overwritten.
4. If you edited `banner.js` and introduced non-ASCII characters, run `python scripts/escape_non_ascii.py Jellyfin.Plugin.MaintenanceDeluxe/Resources/banner.js`.
5. Commit everything, push `main`.
6. `git tag vX.Y.Z && git push origin vX.Y.Z` — the release workflow fires on the tag, runs the full gate, builds, creates the GitHub release, patches `manifest.json`'s checksum+timestamp, and commits the patched file back to `main`.
7. Wait for the workflow run to turn green on the Actions tab. Jellyfin clients that already added the manifest URL will see the new version on their next catalog refresh.

To test the release pipeline without publishing a real version, push an rc tag: `git tag v0.2.0-rc1 && git push origin v0.2.0-rc1`. The workflow treats it like any `v*` tag.
