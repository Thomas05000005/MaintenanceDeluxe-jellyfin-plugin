# Changelog

Toutes les modifications notables de MaintenanceDeluxe sont consignées ici.

Le format est basé sur [Keep a Changelog](https://keepachangelog.com/fr/1.1.0/) et le projet suit le [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[0.3.2.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.2
[0.3.1.1]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.1.1
[0.3.1.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.1
[0.3.0.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.3.0
[0.2.3.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.2.3
[0.2.2.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.2.2
[0.2.1.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.2.1
[0.2.0.0]: https://github.com/Thomas05000005/MaintenanceDeluxe-jellyfin-plugin/releases/tag/v0.2.0
