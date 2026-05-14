# Changelog

Toutes les modifications notables de MaintenanceDeluxe sont consignées ici.

Le format est basé sur [Keep a Changelog](https://keepachangelog.com/fr/1.1.0/) et le projet suit le [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
