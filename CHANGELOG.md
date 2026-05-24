# Changelog

Toutes les modifications notables de MaintenanceDeluxe sont consignées ici.

Le format est basé sur [Keep a Changelog](https://keepachangelog.com/fr/1.1.0/) et le projet suit le [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.8.1.0] — 2026-05-21

🔴 **HOTFIX CRITIQUE — Compatibilité Jellyfin 10.11.9**. Le plugin v0.8.0 crashait sur les serveurs Jellyfin ≥ 10.11.x avec un `MissingMethodException: IUserManager.get_Users()`, cascadant dans les handlers `ItemAdded`/`Updated`/`Removed` (via la stack DI partagée) et **cassant des fonctionnalités core de Jellyfin**.

### Cause

`IUserManager.Users` (propriété `IEnumerable<User>`) a été supprimée dans Jellyfin 10.11.x au profit de la **méthode** `GetUsers()`. Breaking change introduit en patch (entre 10.11.6 et 10.11.9), pas en minor — exceptionnel mais ça arrive. Notre csproj pinnait 10.11.6, l'utilisateur tourne sur 10.11.9.

### Symptômes

```
System.MissingMethodException: Method not found:
'System.Collections.Generic.IEnumerable`1<...User> 
 MediaBrowser.Controller.Library.IUserManager.get_Users()'
```

3 routes/sites cassés :
- `GET /MaintenanceDeluxe/users-summary` (widget whitelist admin)
- `GET /MaintenanceDeluxe/announcements/admin` (totalUsers count)
- `MaintenanceHelper.ActivateAsync` (boucle disable users à l'activation maintenance)

### Fix

- **SDK bump** : `Jellyfin.Controller` + `Jellyfin.Model` de `10.11.6` → `10.11.9`. Csproj plugin **et** csproj tests.
- **3 substitutions** : `_userManager.Users` → `_userManager.GetUsers()` (sites listés ci-dessus). API substitution mécanique, aucune logique modifiée.
- **`targetAbi: 10.11.9.0`** dans manifest.json.

### Compatibilité

| Jellyfin server | Plugin version |
|---|---|
| 10.11.6 → 10.11.8 | v0.8.0 (dernière compatible) |
| 10.11.9+ | **v0.8.1+** |

Le downgrade SDK n'est pas backward-compatible — un plugin build contre 10.11.9 ne tournera pas sur 10.11.6. Si tu es bloqué sur Jellyfin ≤ 10.11.8, reste sur v0.8.0.

### Tests

- 279/279 tests verts sur le nouveau SDK 10.11.9 (aucun test modifié, l'API change est dans `IUserManager` qui n'est pas mocké dans nos tests purs).
- `node --check` + ASCII check toujours OK.

### Note

Pas de feature change. Strictement un hotfix ABI. Si tu utilises **JellyfinEnhanced** (autre plugin) qui crashe avec la même `MissingMethodException` dans `WatchlistMonitor`, c'est le même type de breaking change — à signaler à son auteur (n'a rien à voir avec MaintenanceDeluxe).

## [0.8.0.0] — 2026-05-21

♿ **Tour de fixes a11y + UX + CI robustesse** (4 agents en parallèle, +13 tests = 279 verts). Aucun nouveau champ — uniquement des améliorations défensives.

### A11y (8 fixes)

- **Focus trap** dans toutes les modales : `dangerConfirm` (admin), `showAnnouncementModal`, mode `carousel`, mode `stack` (banner.js). Tab et Shift+Tab cyclent dans la modale, le focus est restauré au close.
- **Scroll lock body** lors d'affichage modale annonce — `overflow:hidden` sur `documentElement` + `body` au open, restore au close. Empêche le scroll du contenu derrière.
- **Compteur carousel SR-friendly** — `counterEl` créé une fois hors `render()` avec `aria-atomic="true"`, mutation via `textContent` au lieu d'`innerHTML` wipe (sinon screen reader rate les changements).
- **`prefers-reduced-motion` respecté** sur thème néon pulse, badge dirty pulse, `jfDconfFade` overlay animation, preview néon glow.
- **Contraste WCAG AA** sur les badges (`draft`/`expired`/`expiring`/`schedule-incomplete`) — couleurs éclaircies (`#FFD27A`, `#FF8A87`) + opacity backgrounds augmentés.
- **Touch targets 44x44** sur `.jf-dconf-btn` (padding 12+) et `.jf-url-btn` (`min-height: 44px` + flexbox center).
- **Focus visible** outline doré sur les inputs `.jf-ann-field` via `:focus-visible`.
- **`backdrop-filter` fallback** solide via `@supports not (backdrop-filter)` — fallback opaque pour les vieux Chromium TV (Tizen 4-5, webOS 3-4).

### UX (5 fixes)

- **Webhook test button race** — désactivé pendant la requête avec texte "Test en cours…", `reenable()` partagé entre `.then()` et `.catch()`.
- **Import config Cancel** — `_pendingImport = null` sur Cancel / ESC / backdrop. Bouton Apply garde-fou + feedback "Sélectionne d'abord un fichier" si pas de payload.
- **Release notes delete confirmation** — `dangerConfirm` si la section a du contenu (titre/body/icon non vides), delete direct si slot vide.
- **Draft autosave feedback** — `_draftSaveFailed` flag + `console.warn` one-shot si quota localStorage dépassé. Plus de silent discard.
- **datetime-local fallback** — `placeholder="YYYY-MM-DDTHH:MM"` + `pattern` regex sur les 3 inputs (`scheduledStart`/`End`/`Restart`) pour les vieux Safari mobile.

### Infrastructure CI (3 fixes)

- **`escape_non_ascii.py`** : `encoding="utf-8-sig"` (strippe BOM UTF-8 automatiquement).
- **`check_version_coherence.py`** : try/except sur parsing JSON/XML malformés → exit code 2 avec message clair.
- **`WebhookNotifier.SanitiseExceptionMessage`** + **`TryGetRetryAfterSeconds`** passées `private` → `internal` pour les tests directs.

### Tests (+13 cas)

- `SanitiseExceptionMessage_RedactsUrlAndHost` Theory (7 cas) — couvre URL stripping, host stripping, edge cases null/empty.
- `TryGetRetryAfterSeconds_*` (4 cas) — null response, delta capped 60s, delta within limit, past date returns 0.
- `IsTargetedAtUser_DraftWithRoleAndUuidFilters_NeverDelivered` — regression guard si l'ordre des checks change.
- `IsScheduleActive_Daily_MidnightWrap_ExactBoundaries` — boundary midnight + 1min / 22:59 / 23:00 / 01:01.

**Total : 266 → 279 tests verts**.

### Méthodologie

Cette release a été produite par **4 agents en parallèle** sur des fichiers distincts pour éviter les conflits de merge :
- Agent A — `banner.js` (focus trap × 3 modales, scroll lock, counter SR, prefers-reduced-motion)
- Agent B — `admin.css` (touch targets, contraste, prefers-reduced-motion CSS, backdrop-filter fallback)
- Agent C — `admin.js` (webhook race, import cancel, release notes confirm, draft feedback)
- Agent D — `scripts/*.py` + `configPage.html` + `WebhookNotifier.cs` (private→internal) + 13 tests xUnit

### Notes techniques

- Aucune migration : tous les fixes sont défensifs ou cosmétiques. Configs v0.7.x chargent telles quelles.
- `banner.js` reste pure ASCII (vérifié via `escape_non_ascii.py --check`).

## [0.7.0.0] — 2026-05-20

🔍 **Audit secondaire** : zones non couvertes par l'audit pré-v0.6.1 (code legacy, concurrence, webhooks). 11 fixes appliqués avec vérification avant/après pour chaque, +32 tests xUnit (234 → 266).

### Sécurité

- **🛡️ SSRF defense webhook URL** — nouvelle helper `IsWebhookHostSafe(url)` qui rejette :
  - Loopback IPv4/IPv6 (`127.0.0.0/8`, `::1`)
  - Link-local `169.254.0.0/16` — inclut **AWS / Azure / GCP metadata endpoints** (`169.254.169.254`, `169.254.170.2`)
  - RFC1918 privé (`10/8`, `172.16/12`, `192.168/16`) + IPv6 ULA (`fc00::/7`)
  - Hostnames `.localhost` / `.internal` / `.local` + literal `localhost`
  Appliquée sur `SaveMaintenance` (webhook URL) **et** `TestWebhook` (sinon admin peut tester avant save).
- **🔐 WebhookNotifier log sanitization** — l'URL contient un token (Discord/Slack) qui se retrouvait dans les exception messages loggués. Nouvelle helper `SanitiseExceptionMessage` strippe l'URL+hostname. `TestAsync` retourne un message générique au lieu d'`ex.Message` qui pouvait leak hostnames internes via SSL cert errors.

### Corrigé

- **🔴 `checkTimeWindow` JS sans midnight wrap** — bannières/annonces avec fenêtre daily `22:00-06:00` étaient invisibles la nuit (return `false` après minuit), alors que le C# `MatchesTimeWindow` gérait correctement. Divergence client/serveur fermée. **Test reproduction avant/après** : 15 cas couvrant overnight evening/early-morning/middle-of-day/boundary + cas normaux + open-ended.
- **🔴 Init defensif `Announcements` / `AnnouncementsSeen`** — propriétés `List<>` sans initializer property-level. Le constructeur les init mais `XmlSerializer` peut le bypass sur XML legacy avec `xsi:nil`, laissant `null` → `NullReferenceException` à `config.Announcements.FirstOrDefault(...)`. Ajout de `= new()`.
- **🟠 `ValidateSchedules` `WeekDays` empty / null** — v0.6.1 rejetait au save (régression). v0.7.0 auto-normalise en `type = "always"` pour préserver la compat avec configs legacy qui avaient ce cas comme "never-active" silencieux.
- **🟠 `DetectFormat` URL host parsing** — substring match remplacé par `Uri.Host` exact match (incl. `canary.discord.com`, `ptb.discord.com`). Avant : `https://relay.com/discord-bot/api/webhooks/proxy` classifié Discord par erreur, relay reçoit du Discord JSON et le rejette.
- **🟠 `GetActiveSessions` race condition** — `_sessionManager.Sessions.Where(...)` est une LINQ sur collection mutable. Snapshot via `.ToList()` avant les opérateurs.
- **🟠 `ClonePublicFields` réutilisé par `MaintenanceScheduleTask`** — auparavant snapshot field-by-field hand-rolled qui manquait `ScheduledStart` / `ScheduledEnd` / `ActivatedAt`. Webhook deactivation notification reçoit maintenant tous les champs.
- **🟠 WebhookNotifier respect `Retry-After` sur 429** — au lieu d'un retry immédiat qui re-foire, attend `Retry-After` (seconds ou HTTP-date) si ≤ 4s. Si > 4s, surface le 429 au caller au lieu de retry agressif.
- **🟠 Webhook payload size cap 32KB** — évite Discord/Slack/proxy 400 sur payloads trop gros (admin avec `customTitle` + `customSubtitle` + `message` très longs). Log warning + retour `(0, "Payload too large…")`.
- **🟡 `banner.js` `tick()` defensive null check sur `CONFIG`** — si `/config` fetch fail (plugin disabled mid-session, network blip, server restart), `CONFIG` reste `null` et chaque `CONFIG.*` throw. `hideBanner(); return;` early.
- **🟡 `LastModified` monotone** — `Math.Max(config.LastModified, now)` au lieu d'écraser. Protège contre les horloges qui reculent (NTP correction, admin manuel) qui casseraient le polling client `if-modified-since`.

### Notes techniques

- `ValidateSchedules` rendue `internal` (était `private`) pour permettre les tests directs depuis `IsWebhookHostSafe`.
- `ClonePublicFields` rendue `internal` (était `private`) pour réutilisation depuis `MaintenanceScheduleTask`.
- `IsWebhookHostSafe` ne fait **pas** de DNS lookup pour éviter latence + TOCTOU. Blocklist coarse sur hostnames + IP literals.

### Tests

- 32 nouveaux cas xUnit :
  - `WebhookNotifierTests` : 5 cas regression substring-match (relay.com/discord-bot/... etc), 13 cas SSRF blocklist hosts, 4 boundary public ranges (172.15, 172.32, 11.x, 192.169)
  - `NormalisationTests` : `ValidateSchedules_Weekly_EmptyDaysAutoCorrectedToAlways` + `NullDaysAutoCorrectedToAlways`
  - Tests JS manuels (node CLI) sur `checkTimeWindow` : 15 cas overnight + boundary
- 234 v0.6.1 → **266 tests v0.7.0** (+32)

### Faux positifs identifiés et rejetés
- "`SaveConfiguration` hors du mutex" → faux : déjà dans le `try { ... }` couvert par `_mutex.WaitAsync()`.
- "Custom theme exposé via /config = info leak" → faux : ajouté volontairement en v0.6.0 pour cohérence, documenté.
- "Double-escaping dans `mdToHtml`" → faux : le `&quot;` est correctement interprété dans l'attribut `href` par le navigateur.

## [0.6.1.0] — 2026-05-20

🩹 **Patch hotfix install + audit logique**. v0.6.0 ne pouvait pas être installé via le manifest Jellyfin à cause d'un typo `sourceUrl`. Cette release corrige ça + applique tous les fixes identifiés par l'audit logique pré-release.

### Corrigé

- **🔴 CRITIQUE — Install Jellyfin v0.6.0 cassée** : le `sourceUrl` du manifest utilisait `maintenance-deluxe-jellyfin-plugin` en minuscules. GitHub URLs sont case-sensitive sur les paths releases → `404 Not Found` → Jellyfin affichait "Une erreur s'est produite durant l'installation de l'extension". Tous les `sourceUrl` corrigés en `MaintenanceDeluxe-jellyfin-plugin`. Le hotfix initial a déjà été pushé sur main (commit `2a31e5f`) pour débloquer les installs v0.6.0 ; cette release le formalise.
- **🟠 Custom theme invisible dans la preview admin** : `.theme-custom` n'existait pas dans `admin.css`, donc l'aperçu live d'une annonce custom affichait les styles velours par défaut. Ajouté `.jf-ann-preview-modal.theme-custom` avec vars CSS dynamiques injectées par `renderAnnouncementPreview` depuis `_annData.customTheme`.
- **🟠 `ValidateSchedules` ne vérifiait que le `type`** : un admin pouvait sauvegarder un schedule `fixed` avec dates inversées, `annual` avec `monthStart=13`, ou `weekly` avec `weekDays=[]` — l'annonce devenait invisible en silence. La validation est étendue :
  - `fixed` : rejet si `fixedStart >= fixedEnd`
  - `annual` : rejet si `monthStart/monthEnd ∉ [1,12]` ou `dayStart/dayEnd ∉ [1,31]`
  - `weekly` : rejet si `weekDays` null/vide ou jours `∉ [0,6]`
- **🟠 `_pluginVersion: '0.3.2.0'`** hardcodée dans le payload d'export config — pas mise à jour depuis 7 releases. Extrait en constante `PLUGIN_VERSION` au top d'`admin.js`.
- **🟠 Sélecteur "Personnalisé" cliquable sans config** : si `customAnnouncementTheme = null`, l'option du segmented control est maintenant **disabled** avec opacity 0.4 + tooltip explicatif. Auto-fallback velours si l'admin supprime sa config custom.
- **🟠 Badge schedule trompeur** : `Fenêtre fixe`, `Annuel`, `Hebdo (aucun jour)` s'affichaient en bleu même quand le schedule était incomplet → annonce silencieusement jamais livrée. Nouveau badge rouge dashed `Fenêtre fixe (non configurée)` / `Annuel (non configuré)` / `Hebdo (aucun jour)` pour flagger les configs cassées.
- **🟠 `expireAfterDays` pas clampé client** : taper `999` passait, serveur normalisait à `365`, admin pensait que `999` était persisté. Clamp client-side 1-365 + mise à jour de l'input pour refléter la valeur effective.
- **🟠 `NormaliseCssColor` failles mineures** :
  - `rgb(256, 0, 0)` accepté → CSS invalide silencieux. Bounds R/G/B in [0,255] enforced.
  - `RGBA(...)` uppercase rejeté → admin perplexe. Regex passe en `IgnoreCase`.

### Modifié

- **`SAFE_URL_RE`** constante module-level dans `banner.js`. Remplace 4 regex protocol-relative-safe identiques dispersées (depuis v0.4.1). Plus de risque de divergence future.
- **`ANN_TEMPLATES`** enrichis avec les champs v0.5.x :
  - `🎃 Soirée Halloween` — schedule `annual` Oct 25 → Nov 1 préconfiguré
  - `🎄 Fin d'année` — schedule `annual` Dec 20 → Jan 5 (wrap)
  - `🎬 Nouveaux films` — `expireAfterDays: 14` pour que les "nouveaux contenus" disparaissent automatiquement après 2 semaines
- **`docs/announcements.md`** complètement réécrit pour Phase 2 :
  - Tableau du statut des 8 features Phase 2 (toutes ✅)
  - Tableau complet des 19 champs `Announcement` avec types et defaults
  - "Truth matrix" expliquant que les 6 filtres de livraison sont AND-combined
  - Doc de la projection serveur `/announcements/active` (champs stripped + ajoutés)

### Tests

- 21 nouveaux cas xUnit :
  - `ValidateSchedules_Fixed_InvertedDates_Rejected`
  - `ValidateSchedules_Fixed_OpenEndedOk`
  - `ValidateSchedules_Annual_OutOfRangeRejected` Theory (6 cas)
  - `ValidateSchedules_Annual_ValidWrapAccepted`
  - `ValidateSchedules_Weekly_EmptyDaysRejected`
  - `ValidateSchedules_Weekly_NullDaysRejected`
  - `ValidateSchedules_Weekly_OutOfRangeDayRejected` Theory (3 cas)
  - `ValidateSchedules_Daily_NoStructuralConstraint`
  - `NormaliseCssColor` étendu avec 6 nouveaux InlineData (case-insensitive RGBA, RGB out-of-range, boundary 255)
- 213 tests v0.6.0 → **234 tests v0.6.1** (+21)

### Notes techniques

- Pas de migration : aucun champ ajouté, fixes défensifs uniquement.
- `ValidateSchedules` est devenue `internal` (était `private`) pour permettre les tests directs.
- Toutes les configs persistées v0.5.x / v0.6.0 chargent telles quelles.

## [0.6.0.0] — 2026-05-20

🎉 **Phase 2 annonces complète**. Cette release ajoute la dernière feature manquante : un éditeur de thème custom. Tu peux maintenant créer ton propre look d'annonce sans toucher au code.

### Ajouté

- **🎨 Éditeur de thème custom** dans l'onglet Annonces. Un bloc `<details>` repliable avec 7 champs :
  - **Nom** (label affiché dans l'admin)
  - **Couleur d'accent** (hex `#RRGGBB`)
  - **Backdrop** (hex ou `rgba(...)` pour la transparence)
  - **Fond carte** (hex ou rgba)
  - **Couleur texte** (hex)
  - **Police** (système / Inter / JetBrains Mono / Space Grotesk / Manrope)
  - **Bordure** (solide / halo lumineux / pointillés / aucune)
- **"Custom" devient le 5ème thème** dans le sélecteur global + le sélecteur par annonce. Si aucun custom n'est configuré, le client retombe sur Velours.
- **Champs vides = défaut Velours** pour ce champ — partial customisation OK (ex. juste changer l'accent).
- **Hot-reload dans la live preview** : modifier un champ du custom theme et l'aperçu se met à jour instantanément (la CSS est ré-injectée via signature JSON pour éviter le pile-up de `<style>` tags).

### Modifié

- **`Announcement`** indirect : nouveau champ `customTheme` joint à chaque item de `/announcements/active` qui a `theme = "custom"`. Le client reçoit la définition complète sans appel API supplémentaire. Items avec theme ≠ custom n'ont pas ce champ (économie de ~200 bytes/item).
- **`PluginConfiguration.CustomAnnouncementTheme`** nouvelle propriété (`CustomAnnouncementTheme?` nullable). Persistée XML + JSON.
- **`NormaliseAnnouncementTheme`** : `"custom"` joint la whitelist.
- **`POST /announcements/admin`** : nouveau champ `customTheme` accepté à la racine du payload. Validation server-side dans `NormaliseCustomAnnouncementTheme`.

### Nouveau côté serveur

- **`NormaliseCustomAnnouncementTheme(input)`** — valide chaque champ indépendamment, retourne `null` si tout est vide (= drop le block persisté).
- **`NormaliseCssColor(value)`** — accepte hex `#RRGGBB` ET `rgb()`/`rgba()` function notation. Regex strict anti-injection : rejette `red`, `hsl(...)`, `var(--x)`, ou toute concaténation CSS.

### Nouveau côté client (`banner.js`)

- **`buildCustomThemeCss(t)`** — génère les règles CSS dynamiquement à partir du DTO custom theme. Mirror les rules `.jf-ann-theme-velours` avec les valeurs admin substituées.
- **`injectAnnTheme(themeKey, customTheme)`** — accepte le block custom et re-injecte le `<style>` à chaque changement (signature JSON pour dédupliquer).
- **`resolveAnnTheme(a)`** — retourne `"custom"` tel quel quand `a.theme === "custom"` (au lieu de fallback velours).
- Fonctionne dans **les 3 modes** : one-at-a-time, carousel (chaque slide peut avoir son custom), stack (toutes les cards utilisent le custom de la première).

### Tests

- **28 nouveaux cas xUnit** :
  - `NormaliseAnnouncementTheme` whitelist mise à jour (5 thèmes au lieu de 4)
  - `NormaliseCustomAnnouncementTheme` : null input, all-empty → null, partial valid kept-with-dropped-invalid
  - `FontFamily` whitelist Theory (5 cas + case-insensitive)
  - `BorderStyle` whitelist Theory (5 cas)
  - `NormaliseCssColor` Theory (11 cas couvrant hex, rgba, rejet keyword/HSL/var/CSS injection)
- 185 tests v0.5.4 → **213 tests v0.6.0** (+28).

### Notes techniques

- Pas de migration : `CustomAnnouncementTheme` est nullable, les configs v0.5.4 chargent avec `null` (= pas de custom configuré, le sélecteur global "Personnalisé" disponible mais fait un fallback Velours sur le rendu).
- `BannerClientConfig` mirror le champ (exposé sur `/config`) pour cohérence avec `announcementTheme`, même si `banner.js` lit le custom theme uniquement via `/announcements/active`.

## [0.5.4.0] — 2026-05-20

Phase 2 — **viewport simulator admin**. Tu peux maintenant prévisualiser l'overlay maintenance dans plusieurs tailles de devices sans changer de browser.

### Ajouté

- **🪟 Toolbar viewport** au-dessus de l'aperçu live (onglet Apparence) avec 6 presets :
  - **Auto** (responsive, défaut) — largeur du panneau
  - **📱 Portrait** 375×667
  - **📱 Paysage** 667×375
  - **📋 Tablet** 768×1024
  - **🖥️ Desktop** 1366×768
  - **📺 TV** 1920×1080
- **Persistance localStorage** (`jf-md-vp-preset`) : la sélection survit aux refresh.
- **Indicateur dimensions** à droite de la toolbar (ex. `1920 x 1080`).

### Notes techniques

- 100% client-side. Le backend C# est inchangé.
- 5 classes CSS scopées `.jf-vp-frame.preset-<name>` qui forcent `width`/`height`/`min-height` sur l'iframe via `!important`.
- Desktop/TV dépassent souvent le panneau admin → `max-width:95vw` + scroll horizontal du wrapper pour permettre l'inspection.
- 185 tests xUnit inchangés.

## [0.5.3.0] — 2026-05-20

Phase 2 annonces — **image hero**. Nouveau champ optionnel `ImageUrl` (+ alt text) sur les annonces, rendu entre le meta header et le body de la modale.

### Ajouté

- **🖼️ Champ `ImageUrl` + `ImageAlt`** sur `Announcement` (les deux nullable). Rendu sous forme de `<img loading="lazy">` entre meta et body. Mêmes règles d'URL safe que `ctaUrl` (`http://`, `https://`, ou single-leading-slash). Cap 2000 chars URL / 200 chars alt.
- **Admin UI** : nouvelle fieldset "Image (optionnelle)" avec deux inputs (URL + alt). Aperçu live admin rend l'image en temps réel.
- **CSS scopé** : `.jf-ann-image` (client) + `.pv-image` (admin preview), `border-radius:8px`, bordure subtile coherente.

### Modifié

- **`POST /announcements/admin`** : valide `ImageUrl` via `IsUrlSafe` (rejet 400 si scheme non-safe). Trim + cap `ImageAlt`.
- **`buildAnnouncementCardHtml`** : insertion conditionnelle de l'image entre `<meta>` et `<body>`. Reuse du regex protocol-relative-safe. Bénéficie automatiquement aux modes **carousel** (chaque slide a sa propre image) et **stack** (cartes empilées).

### Notes techniques

- `loading="lazy"` : l'image n'est téléchargée que quand elle entre dans le viewport — utile pour le mode stack avec plusieurs annonces qui ont chacune une image.
- Pas de migration : champs nullable, configs v0.5.2 chargent avec `ImageUrl = null` (= aucune image).

### Tests

- 2 nouveaux tests : `Announcement_ImageUrl_DefaultsToNull` + `SelectDeliverableForUser_PreservesImageFields` (regression guard pour le round-trip serveur → client).
- 183 → **185 tests** (+2).

## [0.5.2.0] — 2026-05-20

Phase 2 annonces — **Schedule** : les annonces bénéficient maintenant du même système de planning que les rotation messages des bannières. Five types supportés : `always`, `fixed`, `annual`, `weekly`, `daily`.

### Ajouté

- **🗓️ Champ `Schedule` (`BannerSchedule?`) sur `Announcement`** — réutilise le type existant `BannerSchedule`. Types supportés :
  - `always` (défaut) — pas de filtre temporel
  - `fixed` — fenêtre précise (`YYYY-MM-DD HH:MM` start/end)
  - `annual` — récurrent par mois/jour avec time window optionnelle (gère le wrap d'année type Christmas Dec 20 → Jan 5)
  - `weekly` — jours de la semaine au choix + fenêtre horaire
  - `daily` — fenêtre horaire quotidienne (gère les fenêtres overnight 22:00 → 06:00)
- **`AnnouncementHelper.IsScheduleActive(schedule, now)`** — nouvelle helper C# qui mirror exactement la fonction JS `isInSchedule` existante des banner messages. Tests xUnit prouvant l'équivalence comportementale sur 14 cas.
- **Validation au save** : `SaveAdminAnnouncements` réutilise `ValidateSchedules` existant — un admin ne peut pas envoyer un type inconnu (whitelist `always / fixed / annual / weekly / daily`).
- **🪟 Admin UI fieldset "Planning"** — réutilise les helpers `getScheduleDetailHtml` + `wireScheduleEditor` + `populateSchedule` + `collectSchedule` existants des rotation messages. Composant identique, juste rendu dans le contexte de l'éditeur d'annonce.
- **🏷️ Badge summary "Planning"** (bleu) — affiché en plus du badge état quand `schedule != always` : `Fenêtre fixe` / `Annuel` / `Hebdo: Lu Ma Me` / `Quotidien 09:00-17:00`.

### Modifié

- **`IsTargetedAtUser`** — nouveau filtre Schedule en plus de Active / Draft / Expired / Roles / UserIds. Toutes les conditions sont AND-combined.
- **`POST /announcements/admin`** — valide `Schedule.Type` via `ValidateSchedules` (méthode rendue accessible aux annonces).
- Le client reçoit la liste **déjà filtrée par schedule**. Aucune logique client modifiée pour ça : `banner.js` est inchangé, le filtrage time-based se fait 100% serveur (contrairement aux banner messages où c'est client).

### Tests

- **14 nouveaux cas xUnit** : null/always (3 cas), fixed avec bornes ouvertes/fermées (4 cas), annual avec year-wrap Christmas (3 cas), weekly day-of-week + empty list (3 cas), daily avec overnight window Theory (8 sous-cas), unknown type fallback, intégration `IsTargetedAtUser_RespectsSchedule`.
- 169 tests v0.5.1 → **183 tests v0.5.2** (+14).

### Notes techniques

- Le serveur calcule l'expiration avec `DateTimeOffset.UtcNow` ; le client lui n'a aucune logique time-zone pour les annonces (tout est délégué). Les `TimeStart`/`TimeEnd` sont en heure locale serveur (cohérent avec les banner messages existants).
- Aucune migration : `Schedule` est nullable, les annonces existantes ont `Schedule = null` ce qui équivaut à `always`.

## [0.5.1.0] — 2026-05-20

Phase 2 annonces — deux nouveaux champs admin pour réduire la friction sur les annonces : préparation à l'avance et nettoyage automatique.

### Ajouté

- **🟠 Brouillon** : nouveau toggle dans l'éditeur d'annonce, séparé de `Active`. Une annonce en brouillon reste visible dans la liste admin avec un badge orange dashed, mais le serveur ne la sert jamais aux users finaux même si `Active` est coché. Permet de préparer le contenu d'une annonce à l'avance puis de la publier en un clic en décochant `Brouillon`. Filtre serveur dans `AnnouncementHelper.IsTargetedAtUser`.
- **⏰ Auto-expire après N jours** : nouveau champ `Expire après (jours)` dans l'éditeur (1-365, vide = jamais). Quand renseigné, le serveur filtre automatiquement les annonces dont `PublishedAt + ExpireAfterDays` est passé. Filtrage stateless dans `AnnouncementHelper.IsExpired` — pas de mutation, pas d'auto-archive. L'annonce reste visible en admin avec un badge rouge barré "Expirée" pour que l'admin sache pourquoi elle n'est plus servie.
- **🎨 Badges summary enrichis** : la pilule à droite de chaque ligne annonce affiche maintenant 5 états possibles :
  - **Active** (vert) — annonce livrée normalement
  - **Inactive** (gris) — pause manuelle
  - **Brouillon** (orange dashed) — préparation, pas encore publiée
  - **Expirée** (rouge barré) — date d'expiration dépassée
  - **Active (Xj)** (orange) — expire dans X jours ≤ 7
  - Priorité : draft > expired > inactive > active.

### Modifié

- **`POST /announcements/admin`** : nouveau clamp `ExpireAfterDays` à 1-365. Valeurs ≤ 0 sont remplacées par `null` (= no expiration), valeurs > 365 sont clampées à 365 (évite typos `365000`). `IsDraft` est un bool simple, pas de normalisation.
- **`Announcement`** : nouveaux champs `IsDraft` (bool, default false) et `ExpireAfterDays` (int nullable). Reflétés automatiquement dans la réponse `GET /announcements/admin` et `GET /announcements/active`.

### Notes techniques

- **Cas limites couverts** : 0 ou négatif = traité comme "no expiration" (drop à la sauvegarde par le clamp serveur). `PublishedAt` null = jamais expiré (safe default).
- **Pas de migration** : les configs persistées en v0.5.0 chargent telles quelles, les deux nouveaux champs prennent leurs defaults (false/null) sur les annonces existantes.

### Tests

- **13 nouveaux cas xUnit** couvrant : draft never-delivered (2 cas), draft visible-in-admin (déjà testé via SelectDeliverable), expired filtre out, IsExpired boundaries (-10j, -7j exact, -3j, 0j, days=0, days=-5), null PublishedAt safe default, intégration SelectDeliverableForUser avec mix live/draft/expired.
- 156 tests v0.5.0 → **169 tests v0.5.1** (+13).

## [0.5.0.0] — 2026-05-20

Phase 2 annonces partielle : les modes **carousel** et **stack** sont enfin actifs côté client. Depuis v0.3.9 (Phase 1), le sélecteur "Mode d'affichage" de l'admin proposait trois choix mais le client ignorait deux d'entre eux. v0.5.0 ferme la promesse.

### Ajouté

- **🎠 Mode carousel** : si plusieurs annonces non-vues à délivrer, une seule modale avec deux chevrons gauche/droite et un compteur "1 / N" en bas. Chaque slide applique son propre thème (palette + police + animation) à l'overlay. Le marquage "vu" fire-and-forget se fait quand on quitte la slide (via flèches) OU au close final. Navigation clavier : `←`/`→` pour naviguer, `Escape` pour fermer. Sur mobile (≤700px), les chevrons collapsent en boutons flottants en bas de l'écran.
- **📚 Mode stack** : toutes les annonces empilées verticalement dans une seule modale scrollable. Header "N annonces" en haut, un bouton sticky "Tout marquer comme vu" en bas. Pas de bouton "Compris" par carte — un seul bouton global qui POST `/seen` sur les N IDs en parallèle (fire-and-forget). Visuellement, toutes les cartes partagent le thème de la première pour rester cohérent (mixer 4 thèmes empilés serait illisible).

### Modifié

- **Refactor `buildAnnouncementCardHtml(a, withOk)`** : fonction pure qui retourne le HTML de la carte annonce (sans overlay). Réutilisée par les 3 modes.
- **Refactor `createAnnouncementOverlay(themeKey, accent)`** : crée un overlay chromeless vide (positioning, backdrop, thème, accent var). Réutilisé par les 3 modes.
- **`buildAnnouncementModal(a)`** devient un wrapper de 5 lignes au-dessus des deux helpers.

### Notes techniques

- **Edge case** : si une seule annonce non-vue est disponible, le mode admin est ignoré et on retombe sur la modale classique (pas de carousel-à-1-slide ridicule ni de stack-à-1-carte vide).
- 26 nouvelles règles CSS scopées via `#jf-ann-overlay.jf-ann-carousel` et `#jf-ann-overlay.jf-ann-stack` — zéro impact sur le mode one-at-a-time existant.
- Backend C# inchangé : la sélection serveur (`SelectDeliverableForUser`) retourne déjà la liste complète des annonces non-vues, le client la traite selon le mode.
- 156 tests xUnit toujours verts (aucun test C# modifié).
- Chevrons `‹` / `›` échappés en `‹` / `›` pour respecter la contrainte ASCII-safe du `banner.js` (CI lint).

### Phase 2 — reste à faire

- Viewport simulator admin (presets mobile/tablet/TV)
- Schedule fixed/annual/weekly/daily sur annonces
- Support image / screenshot
- Brouillon/publié, auto-expire
- Éditeur de thème custom (Phase 2 thèmes, planifiée v0.4.0)

## [0.4.1.0] — 2026-05-20

Audit complet de revue de code après v0.4.0 (14 fixes : 1 sécurité + 13 qualité/perf/UX). Aucun changement de comportement pour les configs valides — toutes les améliorations sont défensives ou portent sur la performance et l'UX.

### Sécurité

- **🛡️ Faille XSS via URL protocol-relative bouchée** dans le rendu Markdown des annonces et des bannières. L'allowlist `^(https?:\/\/|\/)` acceptait `[click](//evil.com)` (un seul `/` en tête match le second alternant), qui générait `<a href="//evil.com">` et naviguait vers le host attaquant. Le regex passe à `^(https?:\/\/[^\/]|\/(?!\/))` (lookahead négatif sur `/`). **3 endroits patchés client** (`banner.js` ligne 510 `safeUrl`, ligne 797 `linkSafeUrl`, ligne 1848 `ctaUrl`) + **2 endroits patchés admin** (`admin.js` `isUrlSafe` ligne 1464, `annMdRender` ligne 3196) + **1 endroit patché serveur** (`BannerController.IsUrlSafe`). Pas de désync entre les whitelists. Nouveaux tests xUnit : `//evil.com`, `//evil.com/path`, `///triple-slash` tous rejetés.

### Modifié — Validation renforcée

- **🎨 Validation des couleurs CSS** dans `SaveConfig` : `ColorPresets[].bg|color`, `RotationMessages[].bg|color`, `PermanentOverride.Entries[].bg|color` — toutes les valeurs invalides (`red;position:fixed;top:0`) sont rebasées sur le défaut du type (`#1976d2` / `#2e7d32` / `#ffffff`) au lieu d'être persistées telles quelles. Nouvelle helper `NormaliseHexColorOrDefault(value, fallback)` testée par 7 cas.
- **🚧 ValidateRoutes hygiène** : rejet explicite des séquences `..` et `//` consécutives dans les patterns de route (défense en profondeur — aucun exploit connu, mais les patterns Jellyfin réels n'en contiennent jamais). Méthode rendue `internal` pour test direct. 8 nouveaux tests.

### Modifié — UX

- **⚠️ Modale `dangerConfirm` custom** pour les actions destructives, remplace `window.confirm()` :
  - **Suppression d'annonce** : affiche le titre + nombre d'utilisateurs ayant vu, délai 3s avant que le bouton danger devienne cliquable.
  - **Reset seen** : affiche le titre de l'annonce, délai 2s.
  - **Activation maintenance avec sessions actives** : affiche la liste des users en train de streamer, délai 1s (3s si > 3 users).
  - Style dédié (`.jf-dconf-*`) avec backdrop blur, animation fade-in, ESC pour annuler, Enter pour confirmer (si non-bloqué), clic hors carte pour annuler.

### Modifié — Performance

- **⚡ `SelectDeliverableForUser` passe de O(announcements × seenEntries × usersPerEntry) à O(announcements + seenEntries)** via un `HashSet<string>` construit une seule fois pour le user concerné. Sur 1000 users × 50 annonces × 20 users par entry, ~50k comparaisons string → ~1k. Test stress xUnit sur 100 annonces / 50 entries.
- **🎛️ Debounce 120ms sur la preview Markdown admin** (`updatePreview` était appelée à chaque keystroke et reparsait tout le markdown → re-parse 20×/s en saisie rapide).
- **📡 Coalesce des bursts `md-preview-update`** côté `banner.js` via `setTimeout(33ms)` (~30 fps) : un parent floodant l'iframe ne peut plus pegger le CPU.

### Corrigé — Robustesse

- **🔐 Race condition token annonces** : `maybeShowAnnouncements` capturait le token au moment du déclenchement mais le `setTimeout(800ms)` pouvait toujours tirer même si l'user s'était déconnecté entre temps. Désormais le token est capturé dans une closure puis revalidé via `getToken()` après le délai — si mismatch, le fetch est abandonné.
- **🔁 Overlay annonce dédupliqué** : si une modale est encore en transition de sortie quand la suivante est déclenchée, l'ancienne est explicitement retirée du DOM avant la création de la nouvelle (évite deux éléments avec `id="jf-ann-overlay"` simultanés).
- **🎯 `postMessage` admin→preview pinned à `window.location.origin`** au lieu de `'*'` (l'iframe est same-origin, donc aucune raison de wildcarder le target).
- **📅 Validation dates schedule** : `new Date('garbage')` retourne `Invalid Date`, et `NaN <= NaN` est `false` silencieux. Désormais les deux dates sont vérifiées avec `isNaN(d.getTime())` avant la comparaison.
- **🔘 `togglePanel` admin résilient** : itère sur `childNodes` pour trouver le premier `nodeType === 3` (text node) au lieu de présumer `firstChild` (qui peut être une icône SVG selon le markup).

### Tests

- 136 tests v0.4.0 → **156 tests v0.4.1** (+20) : URL protocol-relative (3 cas), `NormaliseHexColorOrDefault` (7 cas), `ValidateRoutes` hygiène (7 cas + 1 fact longueur), perf stress `SelectDeliverableForUser`, regression guard `UserIds == null` sur seen entries.

### Notes techniques

- `ValidateRoutes` est devenue `internal` (était `private`) pour permettre le test direct via `InternalsVisibleTo`.
- Aucune migration de config : tous les fixes sont rétrocompatibles, les configs persistées en v0.4.0 chargent telles quelles en v0.4.1.
- Aucune nouvelle dépendance.

## [0.4.0.0] — 2026-05-14

Première bump mineure : système de thèmes visuels pour les annonces avec 4 styles au choix, polices web premium embarquées, sélection globale + override par annonce, aperçu live thématisé.

### Ajouté
- **🎨 4 thèmes visuels pour la modal d'annonces** :
  - **Velours & Or** *(défaut)* — palette sombre + accent or, police Inter premium SaaS
  - **OLED Total Black** — fond `#000` pur monochrome, JetBrains Mono, idéal écrans OLED
  - **Glow Néon (sobre)** — halo coloré pulsant 4.5s selon l'importance, Space Grotesk géométrique, text-shadow lumineux
  - **Glassmorphism** — fond très flouté (28px blur saturate 1.5x), carte translucide avec bordure lumineuse, Manrope
- **🔤 4 polices web embarquées** (140 KB total, latin-only variable woff2) servies via `GET /MaintenanceDeluxe/fonts/{slug}.woff2` avec `Cache-Control: public, max-age=31536000, immutable`. Chargement lazy : aucune font téléchargée tant que l'utilisateur reste sur Velours, et `font-display: swap` garde le texte visible pendant le streaming.
- **🪟 Sélecteur global** dans l'onglet Annonces : segmented control avec les 4 thèmes, auto-save au changement, re-render immédiat de toutes les previews avant la requête réseau.
- **🪟 Override par annonce** dans l'éditeur : nouveau select `Thème pour cette annonce` avec option `Hériter du défaut global` (par défaut) + les 4 thèmes nommés. Permet de mixer plusieurs styles dans la même liste (par ex. critical-warning en Néon rouge pulsant, annonces classiques en Velours).
- **👁️ Aperçu live thématisé** : la preview admin applique le thème complet (palette + police + animations), avec un badge en haut affichant le thème actif (étoile `*` si overridden, sans étoile si hérité du défaut global, tooltip explicatif).
- **🧪 Tests** : 15 nouveaux tests xUnit couvrant `NormaliseAnnouncementTheme` (whitelist + fallback `velours`) et `NormaliseAnnouncementThemeOverride` (sémantique unknown → `null` = inherit). 136 tests verts au total.

### Modifié
- **`PluginConfiguration`** : nouveau champ `AnnouncementTheme` (default `"velours"`). Reflété automatiquement dans `BannerClientConfig`.
- **`Announcement`** : nouveau champ nullable `Theme` (override par annonce, `null` = inherit).
- **`GET /announcements/active`** : retourne maintenant le `theme` effectif (override ?? global) résolu côté serveur, le client n'a pas à connaître le default.
- **`GET /announcements/admin`** : retourne le `theme` global en plus du `multiMode` pour pré-remplir le sélecteur admin.
- **`POST /announcements/admin`** : accepte un nouveau champ `theme` (whitelist + fallback `velours`).

### Notes techniques
- **Path traversal impossible** sur l'endpoint fonts : le slug est matché contre un `Dictionary<string, string>` figé, jamais concaténé.
- **Scoping CSS strict** : toutes les règles thème sont scopées via `#jf-ann-overlay.jf-ann-theme-X .jf-ann-modal {...}`, aucun risque de leak hors de la modal.
- Plusieurs thèmes peuvent **coexister dans le même DOM** (utile pour l'aperçu admin qui bascule entre thèmes sans page reload).
- **Phase 2 planifiée** : éditeur de thème custom (palette / animation / police pickable) comme la fenêtre de maintenance, pour créer ses propres styles sans modifier le code.

## [0.3.13.0] — 2026-05-14

Modal d'annonce enfin centrée, fond premium, et 7 templates rapides pour gagner 30 secondes par annonce.

### Corrigé
- **🐛 Modal en haut à gauche au lieu de centrée** : un ancestor Jellyfin (body ou skinBody) avec `transform` / `filter` / `will-change` créait un containing block pour `position:fixed`, qui se réancrait à cet ancestor au lieu du viewport. Résultat : la modal s'affichait comme un mini-toast en coin, sans le fond dimmed, sans le blur, sans rien. Le `!important` de la v0.3.12 ne suffisait pas non plus quand Jellyfin Media Player servait depuis son cache Electron l'ancienne banner.js sans `!important`.
- **🛡️ Triple défense pour garantir le centrage** : (1) CSS injecté en `!important` avec `100vw`/`100vh` explicites et `position:fixed` sur tous les bords, (2) overlay attaché à `document.documentElement` (la balise `<html>`) au lieu de `document.body` — `html` n'a jamais de transform parent, (3) `overlay.style.cssText` inline avec `!important` directement sur l'élément. Les styles inline battent toutes les classes CSS, donc même si un ancien `banner.js` est servi depuis le cache navigateur, la modal s'affiche correctement.
- **🎨 Style premium renforcé** : fond passe à `rgba(0,0,0,.68)` (plus contrasté), backdrop blur passe de 6 à 8 px, transition d'opacity gérée via `overlay.style.opacity` directement (pas via classe CSS, pour rester dans le même tier de spécificité que cssText).

### Ajouté
- **⚡ 7 templates rapides d'annonce** dans l'éditeur admin (`ANN_TEMPLATES`) : Mise à jour serveur, Nouveaux films, Maintenance prévue, Amélioration des perfs (avec comparisons before/after pré-remplies), Annonce communautaire, Évènement / soirée, Alerte critique. Chaque template a un emoji, un titre, un body pré-écrit, une importance par défaut et un targeting par défaut (ex. user-only pour les nouveaux contenus).
- **🪟 UI templates picker** : nouveau bloc *Modèles rapides* au-dessus de la liste des annonces avec un bouton par template. Click sur un bouton → annonce pré-remplie ajoutée à la liste et automatiquement dépliée pour édition immédiate.
- **🎨 CSS dédiée** (`.jf-ann-templates`, `.jf-ann-template-btn`) — design or sur fond légèrement teinté, hover state.
- Le bouton historique **`+ Nouvelle annonce`** est renommé **`+ Annonce vide`** pour clarifier l'alternative aux templates.

### Notes techniques
- Pas de changement de format `Announcement` côté serveur — les templates sont du sucre 100% client.
- 121 tests xUnit inchangés.
- ETag-based cache busting de v0.3.11 garantit que `banner.js` + `admin.js` sont rafraîchis dès l'upgrade, donc les templates apparaissent immédiatement après install.

## [0.3.12.0] — 2026-05-14

Fix critique : la modal d'annonce ne s'affichait pas après login (Phase 1 v0.3.9 → v0.3.10).

### Corrigé
- **🐛 Modal d'annonce manquante post-login** : le hook `fetchAndShowAnnouncements` ne tournait qu'au load initial de `banner.js`. Si l'user était sur la page login (no token) au chargement du script, le code exit early avant le hook. Après login → SPA navigate → pas de re-trigger. Seuls les users déjà loggués à l'arrivée du script voyaient la modal — pas le flow normal.
- **🔧 Nouvelle fonction `maybeShowAnnouncements()` idempotente** : track le token pour lequel on a déjà checké, reset au logout, délai 800 ms avant le pop pour laisser la home page se mounter. Appelée à chaque navigation SPA via `refetchAndApplyMaintenance` (hashchange / popstate / viewshow), ainsi qu'au load initial.

### Notes techniques
- L'ancien `setTimeout(fetchAndShowAnnouncements, 1200)` direct est remplacé par le simple appel à `maybeShowAnnouncements()` (déduplication garantie via la garde `_announcementsCheckedForToken === tok`).
- Reset auto au logout (`getToken()` devient null) pour que la prochaine connexion re-trigger.
- 121 tests xUnit inchangés (pas de logique business modifiée, juste le wiring du hook côté client).

## [0.3.11.0] — 2026-05-14

Fix critique : cache busting des assets admin après upgrade plugin.

### Corrigé
- **🐛 Stale admin UI après upgrade** : la classe de bug qui faisait que `admin.js` / `admin.css` / `banner.js` restaient cachés 5 min après une upgrade plugin (Cache-Control `max-age=300` aveugle introduit en v0.3.4). Frappait v0.3.10 frontalement : le user voyait toujours l'UI v0.3.9 après l'install v0.3.10 jusqu'à un `Ctrl+Shift+R` manuel.
- **🔧 Stratégie ETag-based revalidation** : `ServeEmbeddedAsset` calcule désormais un ETag basé sur `Assembly.GetName().Version`, posé sur les responses + header `Cache-Control: public, no-cache, must-revalidate`. Le browser revalide via `If-None-Match` à chaque navigation — 304 si version inchangée (zero body, très rapide), 200 si upgrade détecté.

### Détails techniques
- ETag calculé une seule fois au startup (`static readonly`) → zero overhead par request.
- 304 Not Modified shortcut sans body, conforme RFC 7232.
- Plus jamais besoin de hard-refresh après upgrade : à partir de v0.3.11, chaque release force un fetch frais automatiquement.
- 121 tests xUnit inchangés.

## [0.3.10.0] — 2026-05-14

Refonte complète de l'éditeur Annonces — réponse aux trois problèmes critiques remontés sur la Phase 1 (v0.3.9) : save global qui ignorait les annonces, layout désordonné, pas de prévisualisation.

### Corrigé
- **🛡️ Bouton Enregistrer global save désormais les annonces.** Le handler global lance 3 saves en parallèle via `Promise.allSettled` (config + maintenance + announcements) avec rapport d'erreur unifié.
- **📐 Layout grid `1fr 1fr`** : form à gauche, live preview à droite (collapse en 1 colonne sous 1100 px). Champs alignés via fieldsets thématiques (Identité / Corps / État / Cible / Comparaisons / CTA).
- **📊 Comparaisons en grid 5-colonnes** : Label / Before / After / Highlight / × — passe en 2 colonnes sous 800 px.

### Ajouté
- **🪟 Live preview à droite de chaque éditeur** : rendu local de la modal (HTML pur, pas d'iframe), background radial-gradient or/midnight, accent colorisé selon l'importance, mise à jour à chaque keystroke, sticky `top: 1em` pour suivre le scroll du form.
- **💾 Bouton *Enregistrer cette annonce*** dédié par row (vert primary) avec badge dirty pulsant orange (`@keyframes jf-ann-pulse`) dès qu'un champ change. Feedback de succès "✓ Enregistré" pendant 1.5 s après save.
- **174 lignes de CSS dédié** dans `admin.css` (`.jf-ann-*` + `.pv-*`).
- **`window.__md_saveAllAnnouncements`** exposé sur `window` pour bridge entre le scope IIFE et le handler de save global.

### Modifié
- **`readAnnouncementFromRow(row, existing)`** : nouvelle fonction pure qui lit une row UI vers un objet `Announcement`. Réutilisée par le live preview ET le save — garantit que aperçu = état sauvegardé.
- **Markdown rendering admin-side identique au client** : `annMdRender` mirroir `mdToHtml` (bold, italic, lists, `[texte](url)`).

### Notes techniques
- Format de stockage `Announcement` inchangé — les annonces v0.3.9 sont préservées à l'identique en migration.
- 121 tests xUnit inchangés (les pure helpers de v0.3.9 couvrent toujours la logique business).
- Viewport simulator + modes carousel/stack restent en Phase 2.

## [0.3.9.0] — 2026-04-29

Système d'annonces post-login ("What's New" modal) — nouvelle feature majeure.

### Ajouté
- **Système d'annonces** : modal post-login dismissible affichée aux utilisateurs, tracking serveur "vu par X / Y users". Chaque utilisateur voit chaque annonce **une seule fois** sauf reset explicite par l'admin. Différent des bannières (bandeau haut) et de l'overlay maintenance (full-screen bloquant).
- **Ciblage fin** : par rôle (`user` / `admin`) ET par UUID utilisateur spécifique. Les deux filtres sont AND-combinés (« admin ET (Alice ou Bob) »).
- **Niveaux d'importance** : `info` (bleu) / `update` (vert) / `warning` (orange) / `critical` (rouge) — change la couleur d'accent de la modal.
- **Comparaisons structurées avant / après** : liste de lignes (Label / Before / After / Highlight optionnel) rendues comme une mini-table dans la modal. Format adapté pour stats perf, ex. « Latence streaming · 200 ms → 140 ms · -30 % ».
- **Bouton CTA optionnel** : label + URL (safe-scheme http(s)/relatif uniquement). Ouvre dans un nouvel onglet.
- **Markdown enrichi** : ajout du support `[texte](url)` dans le corps des annonces ET dans les release notes maintenance existantes. URL validée avec la même whitelist que `statusUrl`, échappement HTML appliqué avant le rendu.
- **Modal responsive** : mobile portrait/paysage (comparisons en stack vertical), tablet, desktop (max 700 px), TV ≥ 1920 px (max 900 px + font +25 % + padding +50 % pour 10-foot UI).
- **Admin UI** : nouvel onglet *Annonces* avec liste collapsible, éditeur complet (icon, titre, version, corps markdown avec aperçu live, importance, active toggle, ciblage rôle + utilisateurs, comparisons, CTA), boutons *Réinitialiser 'vue par'* et *Supprimer* par annonce.
- **5 nouveaux endpoints REST** :
  - `GET /MaintenanceDeluxe/announcements/active` — any auth, retourne les non-vues filtrées par user
  - `POST /MaintenanceDeluxe/announcements/{id}/seen` — any auth, mark seen
  - `GET /MaintenanceDeluxe/announcements/admin` — admin, list + counts
  - `POST /MaintenanceDeluxe/announcements/admin` — admin, save
  - `POST /MaintenanceDeluxe/announcements/admin/{id}/reset-seen` — admin, reset tracking
- **`AnnouncementHelper`** : nouvelle classe avec 9 fonctions pures (`IsTargetedAtUser`, `HasUserSeen`, `SelectDeliverableForUser`, `MarkSeen`, `ResetSeen`, `PruneOrphanedSeenEntries`, `NormaliseImportance`, `NormaliseMultiMode`, `NormaliseTargetRoles`, `NormaliseTargetUserIds`). Toute la logique de filtrage / ciblage / tracking est isolée du Plugin singleton.
- **`docs/announcements.md`** : documentation complète (sécurité, responsive, endpoints, limites Phase 1).
- **32 nouveaux tests xUnit** dans `AnnouncementHelperTests` couvrant tous les cas de filtrage / ciblage / tracking / normalisation. **Total 121 tests.**

### Modifié
- **Test anti-drift `DtoMirrorsAllNonMaintenanceJsonPropertiesOfPluginConfiguration`** : nouvelle whitelist explicite `IntentionallyAdminOnly` (maintenanceMode + announcements + announcementsSeen) — au lieu de hardcoder seulement `maintenanceMode`. Ajouter un nouveau champ admin-only au plugin nécessite désormais de le whitelister consciemment (revue de sécurité forcée).

### Phase 1 — limites connues
- **Mode multi-annonces** : seul `one-at-a-time` est implémenté côté client. Les modes `carousel` (flèches) et `stack` (empilé) sont stockés server-side mais retombent sur `one-at-a-time` à l'affichage. Phase 2.
- **Pas de viewport simulator** dans l'admin UI (preview avec presets mobile/tablet/TV). Phase 2.
- **Pas d'image / schedule fixed-annual-weekly-daily / brouillon-publié / auto-expire**. Phase 2.

## [0.3.8.0] — 2026-04-29

Audit fonctionnel complet — nettoyage de mort code, nouvelle feature pre-flight check, documentation enrichie.

### Ajouté
- **Pre-flight check sessions actives** avant l'activation manuelle de la maintenance. Nouveau endpoint `GET /MaintenanceDeluxe/active-sessions` (admin-only) qui retourne les utilisateurs en train de streamer (`NowPlayingItem != null`). Avant le POST `/maintenance` avec `isActive=true`, la page admin affiche un modal de confirmation listant les noms + titres en cours + appareils. Évite de couper sa famille en plein film.
- **`Configuration/admin.css`** : nouveau fichier embedded, contient 359 lignes de styles extraits de `configPage.html`. Servi via nouveau endpoint `GET /MaintenanceDeluxe/admin.css` (public, même modèle que `admin.js`).
- **`docs/banners.md`** : documentation complète du système de bannières (rotation + permanent override + schedules 5 types + routes wildcards + color presets + comportements client). Le système était puissant mais sous-promu — il a maintenant sa doc dédiée. Lien depuis le README.
- **CI : nouveau garde-fou anti-inline-style** dans `configPage.html`. Symétrique au guard anti-inline-script ajouté en v0.3.7. Toute régression bloquée à la PR.

### Supprimé
- **`scripts/check_inline_script.py`** : mort code depuis v0.3.7 où le bloc inline JS a été extrait dans `admin.js`. Plus utilisé par aucun workflow.

### Modifié
- **`configPage.html`** : passe de 1329 à 971 lignes (-27 %). Cumul depuis v0.3.5 : **-78 %**, de 4376 à 971 lignes.
- **`BannerController`** : les 3 endpoints servant des assets embedded (`banner.js`, `admin.js`, `admin.css`) factorisés via un helper privé `ServeEmbeddedAsset(resourceName, contentType)`. Headers `X-Content-Type-Options: nosniff` + `Cache-Control: public, max-age=300` posés à un seul endroit. Suggestion follow-up du review v0.3.7 appliquée.
- **Logging C#** : retrait du préfixe redondant `[MaintenanceDeluxe]` sur tous les `LogXxx`. Depuis v0.3.7 (`BeginScope`) et grâce au `LogCategory` automatique de `Microsoft.Extensions.Logging` (qui contient déjà le namespace de la classe), ce préfixe était du bruit. Banner.js conserve son préfixe `[MaintenanceDeluxe]` dans les `console.debug`/`console.warn` — utile côté DevTools pour identifier la source dans la console browser.
- **README** : table des endpoints à jour (11 routes au lieu de 9) + section *Banner system* qui pointe vers `docs/banners.md`.

### Notes techniques
- L'audit JS Injector confirme que la reflection est l'**API publique officielle** du plugin tiers (commentaire explicite dans leur `PluginInterface.cs` : *"This method is designed to be called by other plugins via reflection"*). Le pattern actuel est volontaire, pas un workaround.
- Les counts de tests xUnit dans le CHANGELOG sont déjà cohérents : 61 (v0.3.2) → 74 (v0.3.3) → 83 (v0.3.5) → 89 (v0.3.7). L'incohérence ressentie venait des versions yanked (v0.3.6/v0.3.7 initial) qui annonçaient des counts différents avant rollback. État actuel : **89 tests, tous verts**.

## [0.3.7.0] — 2026-04-29

Refactor qualité (suite audit interne). Aucun changement de comportement observable côté utilisateur final.

### Tests
- **2 nouveaux helpers purs** dans `MaintenanceHelper` :
  - `PartitionDeactivationTargets(trackedIds, userLookup)` — classifie les IDs en 3 buckets (`ToReEnable` / `MalformedIds` / `MissingUserIds`). `DeactivateAsync` en dépend désormais.
  - `SelectUsersNeedingReDisable(trackedIds, userLookup)` — retourne les users actuellement enabled qui devraient être disabled (drift). `EnsureUsersDisabledAsync` en dépend.
- **6 nouveaux tests xUnit** (`MaintenanceHelperTests`) :
  - `PartitionDeactivationTargets_ClassifiesIntoThreeBuckets` (mix valides + malformés + ghosts)
  - `PartitionDeactivationTargets_EmptyInput_ReturnsEmptyPlan`
  - `PartitionDeactivationTargets_AllValidUsers_NoDataLoss`
  - `SelectUsersNeedingReDisable_OnlyReturnsCurrentlyEnabledUsers`
  - `SelectUsersNeedingReDisable_SilentlySkipsMalformedAndMissing`
  - `SelectUsersNeedingReDisable_NoDriftedUsers_ReturnsEmpty`
- Total **89 tests**, tous verts. La logique business critique (qui se fait disable/re-enable/re-disable) est désormais protégée à 100% par tests.

### Modifié
- **Logging structuré** : `BeginScope("Activate")` / `BeginScope("Deactivate")` / `BeginScope("DriftCheck")` autour des opérations multi-step de `MaintenanceHelper`. Permet de filtrer les logs par opération dans tout système d'observabilité (Seq, Loki, Datadog…). Aucun changement du format de message.
- **`LogTrace` pour le drift no-op** (`Drift check ran, no drift detected (N tracked users still consistent)`). Au niveau `Trace` (silencieux par défaut) — utile pour debug ciblé sans polluer `Information`.

### Refactor majeur
- **`configPage.html` passe de 4376 à 1329 lignes** (-70%). Les 3048 lignes de JavaScript inline sont extraites dans un nouveau fichier `Jellyfin.Plugin.MaintenanceDeluxe/Configuration/admin.js` (EmbeddedResource).
- Nouveau endpoint `GET /MaintenanceDeluxe/admin.js` (public, même modèle de sécurité que `banner.js` : le code n'est qu'une UI, toutes les actions admin restent gardées server-side par les attributs `[Authorize(Policy="RequiresElevation")]` sur leurs endpoints respectifs).
- **Pourquoi c'est important** : l'historique du plugin a deux fix critiques (v0.1.11 et v0.1.12) où l'admin UI était totalement morte à cause d'un bug de parsing JS inline silencieux. Avec un fichier .js séparé, le `node --check` du CI valide le JS isolément et la classe de bug ne peut plus survenir.

### CI
- Le workflow CI remplace l'extraction Python du inline JS (`scripts/check_inline_script.py`) par un `node --check` direct sur `Configuration/admin.js`.
- Nouveau garde-fou : la CI **refuse** désormais tout commit qui réintroduit du JS inline dans `configPage.html`.

## [0.3.6.0] — 2026-04-29

Aperçu admin agrandi : la carte remplit la zone visible.

### Modifié
- **Mode "Agrandir l'aperçu" (côté admin) : la carte s'élargit pour occuper la zone**. Quand l'admin clique sur le bouton *Agrandir l'aperçu* dans la section Apparence, la carte de prévisualisation passe de 640 px (compact) à `min(1400px, 92vw)` — elle remplit maintenant la zone visible au lieu d'être centrée et perdue dans le fond aurora.
- **Le rendu côté utilisateurs finaux est strictement inchangé.** Cette élargissement n'est appliqué que dans le contexte live-preview de la page admin (via une classe CSS `jf-md-preview-expanded` qui n'est jamais émise sur les sessions Jellyfin réelles).

### Détails techniques
- `configPage.html` : nouvelle fonction `notifyPreviewExpanded(bool)` qui envoie un `postMessage({type: 'md-preview-expanded', expanded})` à l'iframe à chaque entrée/sortie du mode agrandi (`enterExpand` / `exitExpand`).
- `banner.js` : le handler `message` du mode live-preview reconnaît désormais `md-preview-expanded` et add/remove la classe `jf-md-preview-expanded` sur l'overlay. Same-origin guard et `event.source === window.parent` toujours en place.
- CSS pure : `#jf-md-overlay.jf-md-preview-expanded .jf-md-card { max-width: min(1400px, 92vw) !important; }` — la limite 92vw garantit qu'on ne déborde jamais du viewport, y compris si l'admin a un écran 1280px.

## [0.3.5.0] — 2026-04-29

Couverture tests + dette CI/CD réduite.

### Ajouté
- **9 nouveaux tests xUnit** sur la logique de partition utilisateurs (`MaintenanceHelperTests`) :
  - `SelectUsersToDisable_PicksOnlyEnabledNonAdminsNotInWhitelist` — vérifie que l'activation cible exactement les bons users
  - `SelectUsersToDisable_EmptyWhitelist_DisablesAllEnabledNonAdmins`
  - `SelectUsersToDisable_AllUsersAreAdmins_ReturnsEmpty`
  - `SelectUsersToDisable_NoUsers_ReturnsEmpty`
  - `SelectPreDisabledIds_OnlyReturnsAlreadyDisabledNonAdmins` — vérifie que les admins déjà disabled ne polluent pas la liste
  - `SelectPreDisabledIds_AndSelectUsersToDisable_AreDisjoint` — **invariant critique** : un user ne peut jamais être à la fois "à désactiver" et "pré-disabled" (sinon `DeactivateAsync` ré-activerait à tort)
  - `IsAdmin_RecognisesAdministratorPermission` (incluant le cas admin disabled)
  - `IsDisabled_RecognisesDisabledPermission`
  - `SelectUsersToDisable_WhitelistTakesPrecedenceOverEnabledStatus`
- Total **83 tests** (74 v0.3.4 + 9 nouveaux), tous verts.

### Modifié
- **`MaintenanceHelper`** : 4 fonctions pures extraites (`IsAdmin`, `IsDisabled`, `SelectUsersToDisable`, `SelectPreDisabledIds`) en `internal static` pour permettre les tests sans Plugin singleton ni mock IUserManager. `ActivateAsync` réécrit pour les utiliser. **Aucun changement de comportement observable runtime** — seulement l'organisation interne devient testable. Pour les futurs refactorings, ces invariants sont désormais protégés par tests.
- **GitHub Actions bumpées aux dernières majeures** : `actions/checkout@v6` (v4 → v6), `setup-dotnet@v5` (v4 → v5), `setup-node@v6` (v4 → v6), `setup-python@v6` (v5 → v6). Le `FORCE_JAVASCRIPT_ACTIONS_TO_NODE24=true` ajouté en v0.3.4 est retiré : les nouvelles actions ciblent Node.js 24 nativement.

## [0.3.4.0] — 2026-04-29

Polish post-audit, suite directe à v0.3.3.

### Ajouté
- **Checkbox `Notifier avant un redémarrage serveur`** dans le tab Maintenance, section *Notifications*. Pilote le champ `WebhookSettings.NotifyOnRestart` ajouté en v0.3.3 mais qui n'avait pas encore de surface UI. Default `true` ; les anciens configs sans le champ sont rétro-compatibles.
- **Headers HTTP de durcissement** sur les endpoints publics :
  - `GET /MaintenanceDeluxe/banner.js` → `X-Content-Type-Options: nosniff` + `Cache-Control: public, max-age=300` (le script ne change qu'à un upgrade plugin → DLL nouveau, donc 5 min de cache économise des fetchs sans masquer une release).
  - `GET /MaintenanceDeluxe/preview.html` → `X-Content-Type-Options: nosniff` + `X-Frame-Options: SAMEORIGIN` (anti-clickjacking ; l'iframe legitimate est servie par Jellyfin lui-même donc same-origin suffit).
- **Documentation des endpoints API** dans le `README.md` (table récapitulative) + flag `?md-debug=1` documenté.
- Section `Restarting` + warning host inconnu ajoutés à `docs/webhooks.md`.

### Modifié
- **`EnsureUsersDisabledAsync`** : log `Information` liste désormais les usernames des comptes ré-disabled, plus seulement leurs UUIDs. Debug d'une dérive (un user re-enable manuellement pendant la maintenance) bien plus rapide.
- **Workflows GitHub Actions** : opt-in Node.js 24 via `FORCE_JAVASCRIPT_ACTIONS_TO_NODE24=true` au niveau env. Les runners GHA forceront Node 24 par défaut en juin 2026 et retireront Node 20 en septembre 2026 ; mieux vaut surfacer toute incompatibilité maintenant tant que le fallback existe.

### Documentation
- `docs/webhooks.md` à jour avec l'événement `Restarting`, le champ `NotifyOnRestart`, le warning sur les hosts inconnus, et la note sur le rate-limit global v0.3.3.
- `README.md` : table récapitulative des 9 endpoints REST exposés par le plugin avec leurs niveaux d'auth, plus une section *Debug flag*.

## [0.3.3.0] — 2026-04-29

Audit complet du plugin (12 fixes).

### Sécurité
- **`GET /config` ne fuit plus les secrets aux non-admins.** Retourne désormais un DTO `BannerClientConfig` qui mirroir `PluginConfiguration` **sauf `MaintenanceMode`**. L'URL webhook (secret Discord/Slack qui permet de poster dans le canal) et les listes UUID utilisateurs (`MaintenanceDisabledUserIds`, `PreDisabledUserIds`, `WhitelistedUserIds`) ne sont plus accessibles aux utilisateurs Jellyfin standards. Nouvel endpoint `GET /config-admin` (`[Authorize(Policy="RequiresElevation")]`) pour la page de config admin.
- **Drift check périodique pendant la maintenance.** `EnsureUsersDisabledAsync` est désormais appelée à chaque tick (1 min) du scheduled task quand maintenance est active, plus uniquement au startup. Si un autre admin re-enable un utilisateur via la dashboard Jellyfin pendant la maintenance, il est ré-disabled dans la minute — la promesse de maintenance ne peut plus être silencieusement cassée jusqu'au prochain restart serveur.
- **Webhook URL : warning sur host inconnu.** Log `Warning` côté serveur si l'URL webhook pointe vers un host autre que Discord/Slack. Les webhooks génériques restent acceptés (pas de blocage), mais un typo ou une URL pasted attacker-controlled est désormais visible dans les logs.
- **`escHtml` côté admin échappe l'apostrophe** (`'` → `&#39;`). Latent — pas d'XSS aujourd'hui car tous les usages sont en text content ou attribut double-quoted, mais durci pour les futurs ajouts.

### Ajouté
- **Notification webhook `Restarting`** envoyée juste avant un redémarrage serveur planifié (`scheduledRestart`). `await`ée (avec timeout interne 5 s du `WebhookNotifier`) pour laisser la requête HTTP partir avant le shutdown Jellyfin. Toggle via nouveau champ `WebhookSettings.NotifyOnRestart` (default `true`).
- **`Cache-Control: public, max-age=10`** sur `GET /MaintenanceDeluxe/maintenance` (endpoint anonyme). banner.js poll à chaque navigation SPA — le cache court réduit la charge sans masquer les toggles admin > 10 s.
- **Borne supérieure de 30 jours sur `scheduledRestart`.** Refus avec `400 Bad Request` si l'admin saisit une date > 30 jours dans le futur (typos et configs polluées détectables).
- **13 nouveaux tests xUnit** : 3 sur `BannerClientConfig.From` (incluant un *guard test* anti-drift via reflection des `JsonPropertyName`) + 10 sur `IsKnownWebhookHost` (incluant le cas `evil.discord.com.attacker.tld` → `false`). Total **74 tests**.

### Modifié
- **`MaintenanceScheduleTask.ExecuteAsync`** : un seul `Plugin.Instance` read en début de tick ; `plugin.Configuration.MaintenanceMode` re-lu aux frontières de branches activate/deactivate/restart pour voir les modifications faites par les helpers. Plus cohérent face aux modifications inter-branches.
- **JS Injector retry** : backoff exponentiel `5/15/45/120/300/600 s` (~18 min total) au lieu de `3 × 5 s`. Donne le temps au plugin de se rattacher sur les hosts lents (low-CPU NAS, cold-cache containers).
- **`Plugin.ScheduleRetry`** : nouveau helper qui encapsule le `Task.Run` avec try/catch propre. Plus de fire-and-forget orphelin qui pourrait remonter à `TaskScheduler.UnobservedTaskException`.
- **`SaveMaintenance` (controller)** : plus de double `SaveConfiguration()` dans les branches activate/deactivate. Le helper save unique en fin de section critique. Fenêtre de race fermée où un `GET /config-admin` concurrent voyait un état intermédiaire.

### Corrigé
- **Rate-limit `/test-webhook`** : remplacé `ConcurrentDictionary<string, DateTime>` per-IP par un `long` global atomique (`Interlocked.CompareExchange`). (a) Plus de memory leak — la map croissait sans limite ; (b) plus de bypass derrière reverse proxy — toutes les IP réelles partageaient le bucket de l'IP du proxy.

### Tests
- 74 tests passent (61 v0.3.2 + 13 nouveaux).

## [0.3.2.0] — 2026-04-28

### Ajouté
- **Export / Import** de la config en JSON dans le tab Maintenance. L'URL du webhook est strippée à l'export par sécurité (placeholder `<redacted>`) ; à l'import, l'URL existante est préservée si le fichier contient le placeholder. Modal de prévisualisation avant application au formulaire (le serveur n'est jamais touché tant que l'admin n'a pas cliqué *Enregistrer*).
- **Helper Uptime Kuma** : bouton à côté du champ *URL de la page de statut* qui ouvre une modal pour construire l'URL à partir de l'instance Kuma + slug, avec test HTTP HEAD optionnel.
- **Avertissement de contraste WCAG** sous le sélecteur de couleur d'accent : affiche le ratio actuel et un message si < 4.5:1 (norme AA pour le texte normal). Soft warning, n'empêche pas la sauvegarde.
- **Projet de tests xUnit** `Jellyfin.Plugin.MaintenanceDeluxe.Tests` avec 61 tests sur les fonctions pures (`WebhookNotifier.DetectFormat`, `NormaliseHexColor`, `NormaliseTheme`, `NormaliseAnimationSpeed`, `NormaliseParticleDensity`, `NormaliseBorderStyle`, `NormaliseReleaseNotes`, `NormaliseOptionalString`, `IsUrlSafe`).
- Étape `dotnet test` ajoutée au workflow CI pour fail toute PR qui casse une fonction normalisation ou la détection de format webhook.
- Ce fichier `CHANGELOG.md` (backfill complet depuis v0.2.0).

### Modifié
- `WebhookFormat` enum passé de `internal` à `public` pour permettre les tests cross-assembly.
- Les méthodes `Normalise*` et `IsUrlSafe` de `BannerController` passent de `private static` à `internal static` (avec `InternalsVisibleTo` côté plugin) pour permettre les tests.

## [0.3.1.1] — 2026-04-28

### Corrigé
- **Liste blanche** : les checkboxes natives Jellyfin (`is="emby-checkbox"`) chevauchaient les noms d'utilisateurs en mode flex. Le widget passe en grid 2 colonnes (1.6em fixe pour la box, 1fr pour le label), checkboxes HTML natives avec sizing explicite — plus aucun chevauchement possible quel que soit le nombre d'utilisateurs.

### Modifié
- `release.yml` supporte maintenant les tags à 4 segments (ex. `v0.3.1.1` → DLL `0.3.1.1` sans `.0` ajouté en suffixe).

## [0.3.1.0] — 2026-04-28

### Ajouté
- **Notifications webhook** Discord / Slack / JSON générique : envoi d'un payload HTTP à chaque transition d'état (activation, désactivation). Format auto-détecté depuis l'URL. Bouton *Tester la notification* avec rate-limit 1 appel/5 s par IP. Documentation des payloads dans `docs/webhooks.md`.
- **Liste blanche utilisateurs** : nouveau widget multi-select dans le tab Maintenance (alimenté par `GET /MaintenanceDeluxe/users-summary`). Les utilisateurs cochés conservent leur accès pendant la maintenance. Gestion des "ghost IDs" pour les comptes supprimés.
- Endpoints `POST /maintenance/test-webhook` et `GET /users-summary`, tous deux `[Authorize(Policy="RequiresElevation")]`.
- DTO `PublicMaintenanceSnapshot` pour l'endpoint public `GET /maintenance` qui ne renvoie plus les UUID des utilisateurs ni l'URL du webhook.

### Sécurité
- L'URL du webhook doit être en `https://` (validation serveur-side, rejet de `http://` pour éviter qu'un secret ne transite en clair).
- Les listes UUID (`MaintenanceDisabledUserIds`, `PreDisabledUserIds`, `WhitelistedUserIds`) et `Webhook.Url` ne fuitent plus à des callers anonymes via l'endpoint public. L'admin UI utilise désormais `/config` (auth) pour récupérer le state complet.

### Robustesse
- Le `WebhookNotifier` ne throw jamais : timeout 5 s + 1 retry sur 5xx/timeout, exceptions capturées et loggées en `Warning`. Un webhook injoignable ne peut pas bloquer une activation/désactivation de maintenance.

## [0.3.0.0] — 2026-04-23

### Corrigé
- `BannerController` retourne `503 Service Unavailable` quand `Plugin.Instance` n'est pas encore initialisé, plutôt que de servir des valeurs par défaut fantômes.
- `ScheduledRestart` est préservé quand il est planifié après la fenêtre de maintenance (n'est effacé que s'il tombe à l'intérieur de la fenêtre).
- `MutationObserver` et `hideScrollObserver` sont correctement détachés au `pagehide` — plus de fuite mémoire après une longue session.
- Le receiver `postMessage` du live-preview impose un check same-origin pour empêcher l'injection cross-origin de config overlay.

### Ajouté
- **Anti-perte de données** : badge orange ● à côté de *Enregistrer* si modifs non sauvegardées, `beforeunload` warning à la fermeture d'onglet, autosave localStorage toutes les 500 ms avec prompt de restauration au reload.
- Validation inline : alerte si `scheduledEnd` < `scheduledStart` ou si `scheduledRestart` est dans le passé.
- Fallback texte sous l'iframe d'aperçu si elle ne signale pas *ready* sous 5 s.

### Modifié
- **UI admin 100 % française** : tab Maintenance, bouton *Enregistrer*, modal de confirmation, alertes et validations entièrement traduits.

## [0.2.3.0] — 2026-04-23

### Ajouté
- Bordure de carte *Rotative* : dégradé doré qui tourne autour de la carte avec un halo pulsant léger. La vitesse suit le curseur d'animation (incluant le mode *off*).

### Corrigé
- Le bouton *Agrandir l'aperçu* qui rendait avec un fond blanc en thème sombre utilise maintenant le même style neutre-translucide que le bouton *Réinitialiser*.

## [0.2.2.0] — 2026-04-23

### Ajouté
- Inputs numériques de précision pour la vitesse d'animation (0–5×) et la densité de particules (0–500) à côté des presets.

### Modifié
- L'aperçu d'apparence est maintenant *sticky* (suit le scroll de la colonne contrôles).
- Iframe min-height 540 px pour les notes de version longues.

## [0.2.1.0] — 2026-04-23

### Corrigé
- L'iframe de live-preview chargeait avec une 401 — `preview.html` et `banner.js` n'exigent plus `[Authorize]` (les browsers ne portent pas le header sur l'iframe nav).

### Modifié
- Sélecteurs *vitesse anim*, *densité particules*, *bordure carte* passent en pill controls (au lieu de radio buttons).
- Bouton *Réinitialiser apparence* restylé pour être lisible sur les deux thèmes.
- Labels, alertes et copy de l'aperçu traduits en français.

## [0.2.0.0] — 2026-04-23

### Ajouté
- **Personnalisation d'apparence** avec live preview : couleur d'accent, teinte de fond, opacité de carte, vitesse d'animation, densité de particules, style de bordure. Bouton d'agrandissement plein écran avec hotkey `H` pour masquer le panneau et `Esc` pour quitter.
- Toutes les couleurs sont dérivées d'une couleur d'accent unique pour garder la palette cohérente quel que soit le hue choisi.

[0.3.12.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.12
[0.3.11.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.11
[0.3.10.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.10
[0.3.9.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.9
[0.3.8.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.8
[0.3.7.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.7
[0.3.6.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.6
[0.3.5.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.5
[0.3.4.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.4
[0.3.3.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.3
[0.3.2.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.2
[0.3.1.1]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.1.1
[0.3.1.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.1
[0.3.0.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.0
[0.2.3.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.2.3
[0.2.2.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.2.2
[0.2.1.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.2.1
[0.2.0.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.2.0
