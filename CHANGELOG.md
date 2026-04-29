# Changelog

Toutes les modifications notables de MaintenanceDeluxe sont consignées ici.

Le format est basé sur [Keep a Changelog](https://keepachangelog.com/fr/1.1.0/) et le projet suit le [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
