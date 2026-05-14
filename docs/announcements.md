# Système d'annonces ("What's New" modal)

Depuis **v0.3.9**, MaintenanceDeluxe sait afficher une modal post-login à tes utilisateurs pour annoncer une mise à jour, un évènement, un changement important, ou simplement leur passer un message.

Différent des bannières (bandeau en haut) et de l'overlay de maintenance (plein écran bloquant) : c'est une **modal centrée**, **dismissible**, **affichée une seule fois par utilisateur** (tracking serveur, pas localStorage).

---

## Accès

**Dashboard → Plugins → MaintenanceDeluxe → onglet Annonces.**

Tu vois la liste des annonces existantes avec leur statut (Active / Inactive) et le ratio « vu par X / Y utilisateurs ».

Bouton **+ Nouvelle annonce** pour en créer une.

## Champs par annonce

| Champ | Effet |
|---|---|
| **Icon** | Emoji (📣 par défaut) ou identifiant court (max 8 chars) — affiché en grand à gauche du titre |
| **Titre** | En-tête de la modal (max 200 chars) |
| **Version** | Tag libre affiché en méta (ex. "v0.3.9") — utile pour grouper / s'y retrouver |
| **Corps** | Markdown enrichi (voir section ci-dessous) |
| **Aperçu** | Rendu live à droite du textarea du corps |
| **Importance** | `info` (bleu, défaut) / `update` (vert) / `warning` (orange) / `critical` (rouge) — change la couleur d'accent de la modal |
| **Active** | Toggle ; si décochée, l'annonce n'est jamais délivrée (utile pour brouillon) |
| **Cible : rôles** | Aucune coche = tous. Cocher *Utilisateurs* = seulement non-admin. Cocher *Admins* = seulement admins. Cocher les deux = tous |
| **Cible : utilisateurs spécifiques** | Liste de checkboxes des comptes Jellyfin. Aucune coche = pas de filtre par utilisateur. **Les deux filtres sont ET-combinés** : "admin ET (Alice ou Bob)" |
| **Comparaisons avant/après** | Liste de lignes structurées (Label / Before / After / Highlight). Rendues comme une mini-table dans la modal |
| **Bouton CTA (label + URL)** | Optionnel. Bouton secondaire dans la modal, ouvre l'URL dans un nouvel onglet. URL doit être `https://`, `http://` ou relative |

## Markdown enrichi dans le corps

Syntaxe supportée (la même qu'utilise les release notes maintenance depuis v0.3.9) :

```
Texte normal avec **du gras** et *de l'italique*.

- Item liste 1
- Item liste 2

Lien vers le [Discord du serveur](https://discord.gg/xxx) ou
le [changelog complet](https://github.com/.../releases/v0.3.9).
```

- `**texte**` → **texte**
- `*texte*` → *texte*
- Ligne commençant par `- ` → puce
- `[texte](url)` → lien cliquable (URL validée : http(s) ou relative seulement)
- Ligne vide → paragraphe suivant

Tout autre HTML / JS est échappé. C'est volontairement minimal — pas d'images, pas de tableaux, pas de classes CSS personnalisées.

## Comportement client

À chaque chargement de page Jellyfin par un utilisateur connecté :

1. `banner.js` fait `GET /MaintenanceDeluxe/announcements/active` (avec son token).
2. Le serveur filtre **les annonces actives qui le ciblent (rôle + UUID) ET qu'il n'a pas encore vues**.
3. Si la liste est non-vide, la première (la plus récente par `publishedAt`) est affichée dans une modal centrée.
4. À la dismiss (bouton "Compris", touche Échap, click hors-modal), `POST /announcements/{id}/seen` est appelé.
5. Mode **"une à la fois"** : après dismiss, le code re-fetch — la suivante (si elle existe) s'affiche au prochain login.

**Si la maintenance est active** : la modal d'annonce est **suppressed** (on ne stack pas deux modals plein écran). Elle reviendra au prochain login post-maintenance.

## Responsive

La modal s'adapte aux 3 grandes catégories de viewport :

| Viewport | Largeur max | Adaptation |
|---|---|---|
| Mobile (≤ 600 px) | 92vw | Padding réduit, comparisons en stack vertical (label sur 1 ligne, avant/après en-dessous) |
| Desktop / tablet (600–1920 px) | 700 px | Table comparisons en 3 colonnes |
| TV (≥ 1920 px) | 900 px | Font-size +25 %, padding +50 %, boutons plus gros (10-foot UI) |

Testé sur Chrome / Firefox / Safari desktop, et sur les WebView mobiles Jellyfin. Les apps natives TV Samsung / LG / Android ne voient pas la modal (limitation déjà connue pour la maintenance overlay).

## Sécurité

- Le DTO retourné aux utilisateurs (`GET /announcements/active`) **n'expose pas** les listes `targetRoles` ni `targetUserIds` — un user lambda ne peut pas inférer qui d'autre a été ciblé par la même annonce.
- `ctaUrl` est validée server-side via `IsUrlSafe` (même whitelist que `statusUrl` : http/https/relatif uniquement, rejette `javascript:`, `data:`, etc.).
- Le markdown du corps est **toujours HTML-échappé avant** l'application des règles d'enrichissement — pas d'injection HTML possible via le corps.
- Le tracking "vu par" stocke des UUIDs, pas de PII supplémentaire.

## Réinitialiser "vue par"

Tu as corrigé une faute, ajouté une comparaison oubliée, ou simplement veux que tout le monde voie une annonce passée — bouton **Réinitialiser 'vue par'** dans l'éditeur de chaque annonce. Confirme et le tracking serveur est vidé pour cette annonce — au prochain login, tous les utilisateurs ciblés la re-voient.

## Limites Phase 1 (v0.3.9)

- **Mode d'affichage** : seul `one-at-a-time` est implémenté côté client. Les modes `carousel` (flèches navigation) et `stack` (toutes empilées) sont stockés mais retombent sur `one-at-a-time` à l'affichage. Implémentation prévue Phase 2.
- **Viewport simulator dans l'admin** : pas de preview avec presets "mobile portrait / paysage / tablet / TV". Pour tester un rendu, créer l'annonce, l'activer pour ton compte uniquement, et ouvrir Jellyfin dans la fenêtre de la taille voulue. Phase 2.
- **Image / screenshot** : non supporté pour l'instant. Phase 2.
- **Schedule fixed/annual/weekly/daily** : non supporté (utilise juste `isActive`). Si tu as besoin de schedule, considère un toggle manuel ou attends Phase 2.

## Endpoints API

| Méthode + route | Auth | Usage |
|---|---|---|
| `GET /MaintenanceDeluxe/announcements/active` | any auth | Liste des annonces non-vues pour l'utilisateur courant. Retourne un tableau JSON (vide si aucune). |
| `POST /MaintenanceDeluxe/announcements/{id}/seen` | any auth | Marque l'annonce comme vue par l'user courant. Idempotent. Renvoie 204. |
| `GET /MaintenanceDeluxe/announcements/admin` | admin | Liste complète + counts "vu par X/Y". Renvoie `{ multiMode, items: [{ announcement, seenCount, totalUsers }] }`. |
| `POST /MaintenanceDeluxe/announcements/admin` | admin | Remplace la liste complète + `multiMode`. Body : `{ announcements: [...], multiMode: "one-at-a-time" }`. |
| `POST /MaintenanceDeluxe/announcements/admin/{id}/reset-seen` | admin | Vide le tracking "vu par" d'une annonce. Renvoie 204. |
