# Notifications par webhook

Depuis la version **0.3.1**, MaintenanceDeluxe peut envoyer une notification HTTP à chaque transition de maintenance (activation, désactivation). Depuis **0.3.3**, un événement supplémentaire est émis juste avant un redémarrage serveur planifié. Le format du payload est auto-détecté à partir de l'URL : Discord, Slack ou JSON générique.

## Configuration

Dans **Tableau de bord → Plugins → MaintenanceDeluxe → tab Maintenance**, section *Notifications* :

| Champ | Description |
|---|---|
| URL du webhook | URL HTTPS complète. Tout autre schéma (`http://`, `ftp://`, etc.) est rejeté côté serveur. |
| Activer les notifications | Coupe-circuit global. Si décoché, aucune requête HTTP n'est envoyée. |
| Notifier à l'activation | Envoie le payload quand la maintenance démarre (activation manuelle ou programmée). |
| Notifier à la désactivation | Envoie le payload quand la maintenance se termine. |
| Notifier avant un redémarrage *(v0.3.3+)* | Envoie le payload juste avant un `scheduledRestart` planifié — la requête est `await`ée (timeout 5 s) avant que Jellyfin ne shutdown, pour laisser le temps au message de partir. |
| Tester la notification | Envoie immédiatement un payload de test. Rate-limit global de 1 appel toutes les 5 secondes (tous admins confondus, depuis v0.3.3). |

Une URL invalide (autre que `https://...`) bloque l'enregistrement avec un message d'erreur. Les secrets webhook ne doivent jamais transiter en clair, donc `http://` est explicitement refusé.

> **Note v0.3.3** : si l'URL pointe vers un host qui n'est ni `discord.com`, ni `discordapp.com`, ni `hooks.slack.com`, le serveur log un `Warning` (sans bloquer la sauvegarde). Les webhooks génériques restent acceptés ; ce log permet juste de repérer un typo ou une URL pasted-by-accident.

## Auto-détection du format

| URL contient | Format envoyé |
|---|---|
| `discord.com/api/webhooks/` ou `discordapp.com/api/webhooks/` | Discord (Embed) |
| `hooks.slack.com/services/` | Slack (Block Kit) |
| autre chose | JSON générique |

## Format Discord

```json
{
  "embeds": [
    {
      "title": "🔧 Maintenance activée",
      "description": "Sauvegarde mensuelle, retour vers 18 h.",
      "color": 13211502,
      "fields": [
        { "name": "Fin programmée", "value": "15/05/2026 18:00 UTC", "inline": true },
        { "name": "Utilisateurs désactivés", "value": "5", "inline": true },
        { "name": "Whitelistés", "value": "2", "inline": true }
      ],
      "timestamp": "2026-04-28T14:32:11.0000000Z"
    }
  ]
}
```

À la désactivation, le titre devient `✅ Maintenance terminée` et la couleur passe au vert.

À l'événement `Restarting` (v0.3.3+), le titre est `🔁 Redémarrage en cours`, la description `Le serveur Jellyfin redémarre, retour imminent.` et la couleur rouge clair (`0xE57373`).

## Format Slack

```json
{
  "blocks": [
    { "type": "header", "text": { "type": "plain_text", "text": "🔧 Maintenance activée" } },
    { "type": "section", "text": { "type": "mrkdwn", "text": "Sauvegarde mensuelle, retour vers 18 h." } },
    {
      "type": "section",
      "fields": [
        { "type": "mrkdwn", "text": "*Fin :*\n15/05/2026 18:00 UTC" },
        { "type": "mrkdwn", "text": "*Désactivés :*\n5" }
      ]
    }
  ]
}
```

## Format générique (JSON brut)

Pour les intégrations custom (Home Assistant, n8n, ntfy, IFTTT…) :

```json
{
  "event": "maintenance_activated",
  "title": "🔧 Maintenance activée",
  "description": "Sauvegarde mensuelle, retour vers 18 h.",
  "isActive": true,
  "scheduledStart": "2026-04-28T14:30:00.0000000Z",
  "scheduledEnd": "2026-04-28T18:00:00.0000000Z",
  "scheduledRestart": null,
  "activatedAt": "2026-04-28T14:32:11.0000000Z",
  "disabledUserCount": 5,
  "whitelistedUserCount": 2,
  "statusUrl": "https://status.example.com",
  "timestamp": "2026-04-28T14:32:11.0000000Z"
}
```

Champ `event` :
- `maintenance_activated` : transition vers actif
- `maintenance_deactivated` : transition vers inactif
- `server_restarting` *(v0.3.3+)* : juste avant un redémarrage serveur planifié
- `maintenance_test` : payload de test envoyé via le bouton

## Robustesse

- **Timeout** : 5 secondes par requête.
- **Retry** : 1 nouvelle tentative en cas de timeout ou de réponse 5xx ; les 4xx ne sont **pas** retentées (config invalide).
- **Erreurs silencieuses** : si le webhook est injoignable, la maintenance s'active/se désactive **quand même**. Aucune erreur webhook ne peut bloquer la mise en maintenance. Le détail de l'échec est loggé en `Warning` côté Jellyfin.

## Créer un webhook Discord

1. Paramètres du salon → **Intégrations** → **Webhooks** → **Nouveau webhook**
2. Choisir un nom (ex. `Maintenance MediaServer`) et un avatar
3. **Copier l'URL du webhook**
4. La coller dans le champ *URL du webhook* de MaintenanceDeluxe
5. Cliquer **Tester la notification** pour vérifier

## Créer un webhook Slack

1. <https://api.slack.com/apps> → **Create New App** → *From scratch*
2. **Incoming Webhooks** → activer → **Add New Webhook to Workspace**
3. Choisir le canal cible et autoriser
4. **Copier l'URL** (format `https://hooks.slack.com/services/T.../B.../...`)
5. La coller dans MaintenanceDeluxe et tester
