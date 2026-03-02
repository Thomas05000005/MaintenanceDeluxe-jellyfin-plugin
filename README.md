# JellyFlare — Jellyfin Plugin

![Icon](./assets/icon.png)

Put announcements where your users will actually see them — a customisable banner at the top of Jellyfin, with rotating messages, scheduling, and link support.

## Features

- 🔄 Rotating messages with configurable display and pause durations
- 🎨 Per-message background and text colour with a named preset palette
- 📅 Flexible per-message scheduling: always, fixed date range, annual (e.g. Christmas), weekly, or daily time window
- 📌 Permanent banner library — save multiple entries and pick the active one; supersedes all rotation messages when enabled
- 🎛️ Configurable dismiss controls: show/hide × and "hide all" buttons, with custom sizes and label
- 🙈 Option to hide the banner while browsing the admin dashboard

## Prerequisites

- Jellyfin **10.11.6** or later
- [JavaScript Injector](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector) — delivers the banner script to the browser
- [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) — required by JS Injector

> **Note:** use the **10.11 builds** of both plugins
> (from the [10.11 manifest](https://raw.githubusercontent.com/n00bcodr/jellyfin-plugins/main/10.11/manifest.json)).
> The 10.10 builds silently fail on Jellyfin 10.11.

## Installation

### Via plugin repository _(recommended)_

1. Go to **Dashboard → Plugins → Repositories** and add:

   ```plain
   https://raw.githubusercontent.com/MorganKryze/jellyflare/main/manifest.json
   ```

2. Go to **Catalog**, find **JellyFlare**, and install it.
3. Restart Jellyfin, then open **Dashboard → Plugins → JellyFlare**.

### Manual

1. Download the ZIP from the [latest release](https://github.com/MorganKryze/jellyflare/releases/latest).
2. Unzip `Jellyfin.Plugin.JellyFlare.dll` into `<data-dir>/plugins/JellyFlare/`.
3. Restart Jellyfin, then open **Dashboard → Plugins → JellyFlare**.

## Configuration

The plugin page has three tabs. See [docs/configuration.md](./docs/configuration.md) for field-level details.

### 📌 Permanent tab

![Permanent tab](./assets/screenshots/permanent.png)

### 🔄 Rotation tab

![Rotation tab](./assets/screenshots/rotation.png)

### ⚙️ Settings tab

![Settings tab](./assets/screenshots/settings.png)

## Development

See [docs/development.md](./docs/development.md) for build instructions, the Docker dev loop, and the release workflow.

## License

GNU General Public License v3 — see [LICENSE](LICENSE).
