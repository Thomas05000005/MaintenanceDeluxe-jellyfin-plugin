# Configuration reference

All settings live on a single config page exposed at **Dashboard → My plugins → MaintenanceDeluxe**. The page has four tabs; only the **Maintenance** tab matters for the overlay feature.

## Maintenance tab — everything you can configure

### Maintenance mode

- **Active** — master switch. When ticked and saved, every non-admin, non-already-disabled user is disabled at the API level and shown the overlay. Users that were *already* disabled before activation are tracked separately and *not* re-enabled when you flip it back off.
- **Overlay message** — legacy short message (kept for backwards compatibility). In v0.1+ the subtitle and release notes are what your users actually read; this field is effectively a fallback.
- **Status page URL** — optional `https://` link rendered as **Voir le statut détaillé ↗** in the overlay footer. Use it to send users to an external status page (e.g. Uptime Kuma) for details beyond what fits in the overlay.

### Automation

- **Scheduled start / end** — UTC datetimes for automatic activation and deactivation. Both are optional. The scheduled task ticks once a minute and applies whichever boundaries have been crossed.
- **Scheduled restart** — optional UTC datetime. When reached, the server restarts once and the field clears itself.

### Overlay content

- **Title** — the H1 shown in the card. Empty falls back to **Serveur en maintenance**.
- **Subtitle** — one-line empathetic message under the title. Empty falls back to **On peaufine le serveur. Rendez-vous juste après.**
- **Preview** button — opens `/?md-preview=1` in a new tab, forcing the overlay to render with your real saved settings (title, subtitle, release notes). A small orange **Prévisualisation** badge in the top-right distinguishes preview from real maintenance. Does not activate maintenance or disable anyone.
- **Release notes** — up to 20 sections. Each section is a triplet:
  - **Icon** — any emoji (2 chars max) or a short glyph. A palette of common presets is shown under the field; click one to set.
  - **Title** — short, one line (max 200 chars). Rendered in gold.
  - **Body** — longer description (max 4000 chars). Supports a safe markdown subset:
    - `**bold**` → bold
    - `*italic*` → italic
    - Lines starting with `- ` → bulleted list
  - Reorder with the up/down buttons, delete with the trash icon.

A live markdown preview is rendered just below each body textarea so you can see the final rendering without leaving the config page. Character counters on title and body warn you before the limits.

## What users actually see

1. **Full-screen overlay** with a velours background, drifting gold + midnight blue aurora, a slowly rotating film reel icon, and (on capable browsers) a brushed-metal card border.
2. **Title + subtitle** at the top.
3. **Time box** with three lines: absolute target time ("Retour prévu à …"), a relative hint, and a progress bar. If the scheduled end is passed the bar pulses amber and the relative text becomes "Finalisation en cours (+N min)".
4. **Release notes** rendered as cards with icon/title/body.
5. **Footer** with the status URL link (if set) and the admin dismiss button.

## Saving

Hit **Save** at the bottom of the Maintenance tab. The plugin normalises your input server-side:
- Title truncated to 200 chars, subtitle to 500 chars.
- Release notes capped at 20 entries; each title/body trimmed and truncated; empty rows dropped silently.
- Theme whitelisted to known values (`velours`); accent colour validated as `#RRGGBB` hex.
- Status URL validated as `http://`, `https://`, or a relative path — anything else rejected with a 400.

After saving, you can click **Preview** to see the overlay reflect your changes immediately.
