# Système de bannières

MaintenanceDeluxe inclut un mini-CMS d'annonces persistantes, séparé de la fonctionnalité maintenance. Pratique pour signaler des nouveautés, des règles serveur, ou des évènements ponctuels (vacances, ajout massif de contenu, soirée thématique).

Deux modes coexistent :

- **Permanent override** — un message unique, sélectionnable parmi une bibliothèque, affiché en haut de l'UI Jellyfin tant qu'il est actif.
- **Rotation** — plusieurs messages qui défilent en boucle (aléatoire ou ordre fixe), chacun avec sa propre programmation.

Les deux modes sont accessibles dans l'admin via **Tableau de bord → Plugins → MaintenanceDeluxe → onglets *Permanent* / *Rotation***.

---

## Permanent override

### Quand l'utiliser
Pour un message **stable** qui doit toujours être visible (ou jusqu'à ce que tu changes d'avis) : règles d'usage, signature de serveur, lien Discord communautaire, statut "serveur up depuis X jours", etc.

### Comportement
- Tu construis une **bibliothèque** d'entrées (texte, couleurs, URL optionnelle, schedule, routes).
- Tu sélectionnes **une seule** entrée active via le bouton radio dans la liste.
- Tant qu'elle est active ET que sa programmation est dans la fenêtre, elle s'affiche en haut de page.
- Si `permanentDismissible` est activé, les utilisateurs peuvent la fermer (persistance localStorage par utilisateur/navigateur).

### Champs par entrée
| Champ | Effet |
|---|---|
| Text | Message affiché |
| Bg / Color | Couleurs background + texte (hex `#rrggbb`) |
| URL (optionnel) | Cliquer la bannière ouvre un popup *Open / Copy / Cancel* (jamais une nav directe — évite les soucis de WebView mobile) |
| Schedule | Quand la bannière est éligible (voir section *Schedules* ci-dessous) |
| Routes | Sur quelles pages Jellyfin elle s'affiche (voir section *Routes* ci-dessous) |
| Preset label | Indicatif, pour identifier l'origine d'un preset de couleur |

---

## Rotation

### Quand l'utiliser
Pour faire **tourner** plusieurs annonces : nouveautés films de la semaine, films à voir absolument, rappels, etc.

### Comportement
- Chaque message tourne pendant `displayDuration` secondes (défaut 30).
- Entre deux messages, pause de `pauseDuration` secondes (défaut 60).
- Si `rotationShuffle` est `true`, l'ordre est aléatoire (Fisher-Yates) ; sinon ordre de saisie.
- Bouton *× (Dismiss)* par message + bouton *hide all* pour la session.
- Si `persistDismiss` est activé, les dismiss survivent au reload (localStorage).
- Sync cross-onglets via `BroadcastChannel` (un dismiss dans un onglet ferme la même bannière dans les autres).

### Champs par message
Identiques à *Permanent*, plus :
- `enabled` : participe ou non à la rotation (sans le supprimer)

---

## Schedules

Disponible sur **chaque** entrée (permanent ou rotation). 5 types :

| Type | Configuration | Cas d'usage |
|---|---|---|
| `always` | Aucune | Toujours visible |
| `fixed` | `fixedStart` + `fixedEnd` (YYYY-MM-DD HH:MM) | Évènement ponctuel ("Soirée Halloween le 31 octobre 20h-23h") |
| `annual` | `monthStart`/`dayStart` + `monthEnd`/`dayEnd` (+ `timeStart`/`timeEnd` optionnels) | Périodes récurrentes annuelles ("Décor de Noël du 1er au 31 décembre") |
| `weekly` | `weekDays` (0=Dim … 6=Sam) + `timeStart`/`timeEnd` | "Films familiaux le vendredi soir 19h-23h" |
| `daily` | `timeStart`/`timeEnd` | "Bannière sécurité tous les soirs après 22h" |

Évaluation côté client à chaque tick — pas de cron côté serveur, les bannières apparaissent/disparaissent automatiquement à l'heure pile.

---

## Routes (filtre par page)

Liste de patterns matchés contre la route Jellyfin (hash, sans le `#!`). Wildcard `*` supporté.

| Pattern | Match |
|---|---|
| Vide / non-renseigné | Toutes les pages |
| `home.html` | Uniquement la home |
| `details*` | Toute page de détail |
| `playback*` | Toute page de lecture |
| `home.html`, `movies*` | Home OU pages films |

L'admin n'a pas à connaître la liste complète des routes Jellyfin — il suffit de naviguer sur la page voulue et regarder l'URL après `#!` dans la barre d'adresse.

**Validation serveur** : caractères autorisés `A-Z a-z 0-9 - . _ / * ? = & # + %`, max 512 caractères. Tout pattern hors-norme est rejeté à la sauvegarde.

---

## Color presets

Bibliothèque réutilisable de couples (background + text) que tu peux nommer (« Maintenance », « Alerte », « Info »…).

- Clic sur un preset → applique les deux couleurs au message en cours d'édition.
- Édition d'un preset → les messages qui référencent ce preset (`presetLabel`) ne sont **pas** automatiquement mis à jour. Le `presetLabel` est juste un indicateur de provenance.
- Suppression d'un preset → les messages qui le référencent gardent leurs couleurs ; seul le `presetLabel` devient orphelin (affichage : « Preset supprimé »).

---

## Comportement client (banner.js)

- Hauteur de bannière (`bannerHeight`) : 24–80 px, défaut 36 px. Mobile (`≤ 600px`) : +6 px automatiquement.
- Police : `fontSize` 10–32 px, `fontBold` true/false, `textAlign` `center` / `left`.
- Transition fade/slide : `transitionSpeed` `none` / `fast` / `normal` / `slow`.
- Boutons : `× (dismiss)`, `hide all`, configurables en taille et visibilité.
- URL clickable → popup avec :
  - **Open link** : nav classique (sauf si appli mobile Jellyfin → grise *Open* et met *Copy* en avant)
  - **Copy URL** : `navigator.clipboard` avec fallback `execCommand` pour HTTP non-sécurisé
  - **Cancel**
- Suppression de l'admin dashboard (`showInDashboard: false`) : les bannières ne s'affichent pas dans les pages admin Jellyfin pour ne pas polluer ton travail de configuration.

---

## Endpoints API utilisés

| Méthode + route | Auth | Usage côté admin |
|---|---|---|
| `GET /MaintenanceDeluxe/config-admin` | admin | Lit la config complète au chargement de la page |
| `POST /MaintenanceDeluxe/config` | admin | Sauve permanentOverride + rotationMessages + colorPresets + settings globaux |
| `GET /MaintenanceDeluxe/config` | any auth | Lu par banner.js côté utilisateur (DTO scrubbé, sans `maintenanceMode`) |

---

## Limites connues

- **Pas de prévisualisation des bannières** (contrairement à la maintenance qui a son iframe live-preview). Les bannières sont simples et le rendu est prévisible — pas une vraie limitation, mais à savoir.
- **Pas d'analytics** : aucune info sur quel message a été vu/dismissé par qui. Le plugin est sans tracking par design.
- **Cross-tab dismiss** dépend de `BroadcastChannel` (Safari < 15.4 fallback : pas de sync, chaque onglet gère son propre dismiss).
