# MaintenanceDeluxe

A premium cinema-themed maintenance overlay for [Jellyfin](https://jellyfin.org).

When you flip maintenance on from the admin dashboard, every non-admin user is disabled at the API level and greeted by a full-screen overlay showing your release notes, a live countdown and a time-based progress bar — so they know exactly what you're doing and when they can come back.

## Why this exists

Jellyfin's default behaviour when you want to take the server down for updates is:

1. Disable users manually one by one, or
2. Just stop the container and let people hit a connection error.

Neither is great. This plugin gives your users a polished, informative "please wait" screen while you work — and makes it a small window of anticipation rather than a dead end.

## Features

- **Full-screen maintenance overlay** shown on top of the Jellyfin UI — and on the login page too, so freshly-disabled users see the same message instead of a raw "account disabled" error.
- **Live countdown** that adapts with the remaining time: an absolute target hour (e.g. "Retour prévu à 21h30"), a rough relative hint ("≈ 25 min"), and precise minutes+seconds once you drop below 5 minutes.
- **Time-based progress bar** driven by the scheduled start/end. Pulses amber when you overshoot the window, so you're never "stuck at 99%".
- **Admin-configurable release notes** — add as many sections as you need, each with an emoji, a title, and a markdown body (supports `**bold**`, `*italic*`, and bullet lists).
- **Live markdown preview** and character counters right next to the textarea in the admin UI.
- **Preview mode** via a button in the config page — opens the overlay with your real saved settings in a new tab, without activating maintenance or kicking anyone.
- **Cinema theme** — warm velours palette, drifting aurora background with a gold/midnight contrast, slowly-rotating film reel, gently crackling ember particles, brushed-metal card border on capable browsers. All designed to degrade cleanly on low-end smart TVs.
- **Three performance tiers**, auto-detected:
  - `full` — desktop and modern mobile browsers (all effects)
  - `reduced` — Tizen/webOS/Fire TV/Android TV (no particles, static background)
  - `minimal` — anything with `prefers-reduced-motion` set (solid background, no animation)
- **Embedded typography** — the title font (Instrument Serif) is inlined as base64 so the overlay looks identical on every device, regardless of what's installed locally.
- **Scheduled activation / deactivation** and **scheduled restart** — set a window once and let it run automatically.
- **Admin dismiss button** so you can keep working on the server without being boxed out by your own overlay. On the login page the same button appears as "Accès administrateur →" so you can always reach the login form.

## Requirements

- Jellyfin **10.11.6** or newer (`targetAbi: 10.11.6.0`).
- Two prerequisite plugins, both already active:
  - **JavaScript Injector** ([n00bcodr/Jellyfin-JavaScript-Injector](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector)) — injects our script into the web UI.
  - **File Transformation** ([IAmParadox27/jellyfin-plugin-file-transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)) — used by JavaScript Injector to modify `index.html`.

Without those two, MaintenanceDeluxe has no way to render anything.

## Installation

In Jellyfin → **Dashboard → Plugins → Repositories**, add this URL:

```
https://raw.githubusercontent.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/main/manifest.json
```

Then go to the **Catalog** tab, install **MaintenanceDeluxe**, and restart Jellyfin.

After the restart, the plugin appears under **My plugins** with a full config page.

## Usage

### Flipping maintenance on

1. Go to **My plugins → MaintenanceDeluxe → Maintenance** tab.
2. Tick **Active**, optionally set a **Scheduled end** time.
3. Fill in the **Overlay content** section:
   - Title (defaults to "Serveur en maintenance")
   - Subtitle (defaults to "On peaufine le serveur. Rendez-vous juste après.")
   - As many release-notes sections as you want (click **+ Add a section**).
4. Click **Save**.

Your users on the web, mobile browsers, or opening `/web/` in their TV's browser will see the overlay until you flip maintenance back off. Users of the official native Samsung/LG TV apps will just see a "disabled account" error — that's a constraint of those apps, not of this plugin.

### The preview button

Next to the **Overlay content** heading there's a **Preview** button. It opens a new tab with `?md-preview=1`, which loads your real saved settings (title, subtitle, release notes) into the overlay — but doesn't activate maintenance or disable anyone. Use it freely to iterate on your wording and content.

### The admin dismiss

Once you're logged in as admin and maintenance is active, you'll see a small **Accès admin** button at the bottom of the overlay. Click it and it goes away for the rest of the session, so you can keep using Jellyfin. It reappears on the next tab/reload or after deactivation.

On the login page (before auth) the same button reads **Accès administrateur →** — so you can always get back into the admin even if your own maintenance is on.

## Building from source

Requires .NET SDK 9.0.

```
dotnet build --configuration Release
```

Output DLL: `Jellyfin.Plugin.MaintenanceDeluxe/bin/Release/net9.0/Jellyfin.Plugin.MaintenanceDeluxe.dll`

## Preview.html — design iteration without Jellyfin

A standalone `preview.html` sits at the root of the repo. Open it in any browser and you get the overlay rendered with slider controls to tweak remaining time, total duration, performance tier, and number of release notes. Useful for CSS tweaks before touching the plugin.

## Endpoints (v0.3.3+)

| Method + path | Auth | Purpose |
|---|---|---|
| `GET /MaintenanceDeluxe/maintenance` | public | Stripped public snapshot (`PublicMaintenanceSnapshot`) — used by the login-page overlay. UUID lists and webhook URL are removed. |
| `GET /MaintenanceDeluxe/banner.js` | public | Banner client script, served identically by JS Injector. |
| `GET /MaintenanceDeluxe/preview.html` | public | HTML shell for the admin live-preview iframe. |
| `GET /MaintenanceDeluxe/config` | any auth | Banner-client view (`BannerClientConfig`) — full plugin settings **without** `maintenanceMode`. |
| `GET /MaintenanceDeluxe/config-admin` | admin | Full `PluginConfiguration` including `maintenanceMode` (webhook URL, user UUID lists). |
| `POST /MaintenanceDeluxe/config` | admin | Save general settings. |
| `POST /MaintenanceDeluxe/maintenance` | admin | Toggle maintenance / save maintenance fields. |
| `POST /MaintenanceDeluxe/maintenance/test-webhook` | admin | Send a test payload. Rate-limited to 1 call / 5 s globally. |
| `GET /MaintenanceDeluxe/users-summary` | admin | Lightweight user list for the whitelist multi-select. |

## Debug flag

Append `?md-debug=1` to any Jellyfin URL to enable verbose `console.debug` output from the banner script (script-load notice, init traces). Without the flag the banner is silent in DevTools so end-users don't see plugin chatter.

## License

GPL-3.0. See `LICENSE`.
