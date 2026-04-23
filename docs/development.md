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

A helper Python script (`_escape_all.py`, not committed) does the conversion before each build.

## Publishing a new version

1. Bump `<AssemblyVersion>` and `<FileVersion>` in `MaintenanceDeluxe.csproj`.
2. Run the ASCII-escape helper on `banner.js` if you edited it.
3. `dotnet build --configuration Release`.
4. Zip `Jellyfin.Plugin.MaintenanceDeluxe.dll` + a matching `meta.json` into a flat archive.
5. Compute the MD5 of the zip.
6. `gh release create vX.Y.Z path/to/zip --title "vX.Y.Z" --notes "..."`.
7. Update `manifest.json` at the repo root: bump `version`, `sourceUrl`, `checksum`, `timestamp`.
8. Commit and push — Jellyfin clients that already added the manifest URL will see the update on their next catalog refresh.
