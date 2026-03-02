# JellyFlare — Jellyfin Plugin

![Icon](./assets/icon.png)

A Jellyfin plugin that displays rotating JellyFlares at the top of the web UI.
Messages are managed entirely through the Jellyfin admin dashboard — no file editing required.

## Features

- Rotating messages with configurable display and pause durations (enable/disable without clearing the list)
- Per-message background and text colour with preset palette
- Optional date-range scheduling per message (show only between two dates/times)
- Permanent banner library: save multiple permanent banners and select the active one with a radio button; the active entry supersedes all rotation messages when enabled
- Configurable dismiss controls: show/hide the × button and the "hide all" button, with individual font-size and a custom label for the latter
- Option to hide the banner while browsing the admin dashboard
- Admin configuration page with tabbed layout (Permanent / Rotation / Settings) accessible from the Jellyfin dashboard sidebar

## Prerequisites

- Jellyfin **10.11.6** or later
- [JavaScript Injector](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector) plugin — delivers the banner script to the browser
- [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin — required by JS Injector to inject its loader into the web UI

> **Version note:** both JS Injector and File Transformation must be the **10.11 builds**
> (from the [10.11 manifest](https://raw.githubusercontent.com/n00bcodr/jellyfin-plugins/main/10.11/manifest.json)).
> Using the 10.10 builds on Jellyfin 10.11 will silently fail.

## Installation

### Via plugin repository (recommended)

1. In Jellyfin, go to **Dashboard → Administration → Plugins → Repositories**.
2. Add the following repository URL:

   ```plain
   https://raw.githubusercontent.com/MorganKryze/jellyflare/main/manifest.json
   ```

3. Go to **Catalog**, search for **JellyFlare**, and click **Install**.
4. Restart Jellyfin.
5. Open the dashboard: **Dashboard → Plugins → JellyFlare**.

### Manual

1. Download the ZIP from the [latest release](https://github.com/MorganKryze/jellyflare/releases/latest) or build from source (see below).
2. Copy `Jellyfin.Plugin.JellyFlare.dll` into your Jellyfin plugins directory:

   ```plain
   <jellyfin-data-dir>/plugins/JellyFlare/
   ```

3. Restart Jellyfin.
4. Open the dashboard: **Dashboard → Plugins → JellyFlare**.

## Configuration

Navigate to **Dashboard → Plugins → JellyFlare**. The page has three tabs.

### Permanent tab

A library of permanent banners that take priority over all rotation messages.
Select the active entry with its radio button; use the enable toggle to pause without losing any entries.

| Field             | Description                                                    |
| ----------------- | -------------------------------------------------------------- |
| Enable            | Toggle the permanent banner on/off (all entries)               |
| Radio button      | Select which entry is currently active                         |
| Text              | Message to display                                             |
| Background colour | CSS colour value, e.g. `#2e7d32`                               |
| Text colour       | CSS colour value, e.g. `#fff`                                  |
| Start date        | Optional — only show from this date/time                       |
| End date          | Optional — stop showing after this date/time                   |

Each row is collapsed by default — click the row body (not the radio) to expand and edit.
Rows with empty text are ignored on save. The `activeIndex` stored in config tracks which entry is selected.

### Rotation tab

| Field             | Description                                  |
| ----------------- | -------------------------------------------- |
| Enable            | Toggle all rotation banners on/off           |
| Text              | Message to display                           |
| Background colour | CSS colour value, e.g. `#1976d2`             |
| Text colour       | CSS colour value, e.g. `#fff`                |
| Start date        | Optional — only show from this date/time     |
| End date          | Optional — stop showing after this date/time |

Each message row has its own enable checkbox in the summary bar — uncheck to pause a single message without removing it.
Messages are shown in random order. Each row is collapsed by default — click the row to expand and edit.
Messages that are disabled or whose date range has not started or has already passed are silently skipped.

### Settings tab

#### Visibility

| Field                          | Default | Description                                      |
| ------------------------------ | ------- | ------------------------------------------------ |
| Show banner in admin dashboard | on      | Hide the banner whenever an admin page is active |

#### Timing

| Field                | Default | Description                                   |
| -------------------- | ------- | --------------------------------------------- |
| Display duration (s) | 120     | How long each message is shown before cycling |
| Pause duration (s)   | 60      | Gap between messages (0 = no pause)           |

#### Controls

Each subsection heading has a small `restore` icon button that resets only that subsection to defaults.

| Field                       | Default    | Description                                     |
| --------------------------- | ---------- | ----------------------------------------------- |
| Show dismiss button (×)     | on         | Whether the per-message close button is visible |
| Dismiss button size (px)    | 18         | Font size of the × button                       |
| Show "hide all" button      | on         | Whether the "hide all" button is visible        |
| "Hide all" button size (px) | 9          | Font size of the "hide all" button              |
| "Hide all" button label     | `hide all` | Custom label for the "hide all" button          |

#### Presets

A list of named colour presets available in all message editors. Each preset has a label, background colour, and text colour. Presets can be added, edited, reordered, and deleted. Deleting a preset keeps existing message colours but removes the selection indicator on affected messages.

The `restore` icon next to the **Presets** heading resets the list to the 8 built-in defaults.

#### Danger Zone

| Button               | Scope                                                                       |
| -------------------- | --------------------------------------------------------------------------- |
| Reset settings       | Restores Timing, Controls, and Presets to defaults; messages unchanged      |
| Wipe all plugin data | Clears the permanent banner, all rotation messages, and resets all settings |

Both buttons require confirmation before applying. Changes only take effect after clicking **Save**.

## Building from source

```bash
git clone https://github.com/MorganKryze/jellyflare.git
cd jellyflare
dotnet build --configuration Release
```

The output DLL is at:

```plain
Jellyfin.Plugin.JellyFlare/bin/Release/net9.0/Jellyfin.Plugin.JellyFlare.dll
```

## Local testing with Docker

A Docker Compose file and a `Makefile` are provided for local development using the
LinuxServer.io Jellyfin image.

### Quick start

```bash
make deploy  # setup → build → start (run once)
```

Open `http://localhost:8096` and complete the setup wizard.

### Dev loop

After editing source files:

```bash
make update  # build → copy DLL → restart container → tail logs
```

### Available targets

| Target    | Description                                                   |
| --------- | ------------------------------------------------------------- |
| `deploy`  | First-time bootstrap: download deps, build, start container   |
| `setup`   | Download JS Injector + File Transformation, write `meta.json` |
| `build`   | Build DLL and copy it to the plugin directory                 |
| `update`  | Build, copy DLL, restart container, tail logs                 |
| `up`      | `docker compose up -d` + tail logs                            |
| `down`    | `docker compose down`                                         |
| `restart` | Restart the Jellyfin container                                |
| `logs`    | Tail Jellyfin container logs                                  |
| `clean`   | Remove `bin/`, `obj/`, and `docker/config/`                   |
| `bump`    | Bump version: `make bump V=1.1.0`                             |

## Releasing a new version

1. On GitHub: **Releases → Draft a new release** → create tag `v1.2.0` targeting `main`.
   Write your release notes in the body — they become the `changelog` field in `manifest.json`
   (what Jellyfin users see in the plugin catalog). Then publish.
2. CI automatically:
   - patches `AssemblyVersion` and `FileVersion` in the csproj from the tag
   - builds and zips the DLL + icon
   - prepends a new entry to `manifest.json` (with MD5 checksum and your release notes)
   - pushes a `chore: update manifest for vX.X.X` commit back to `main`
   - attaches the ZIP to the GitHub release
3. Run `git pull` before your next local change — the CI pushes a manifest commit back to `main`.

> `make bump V=1.2.0` is still available if you need to bump the csproj locally (e.g. to verify a build), but it is no longer required before releasing.

## License

GNU General Public License v3 — see [LICENSE](LICENSE).
