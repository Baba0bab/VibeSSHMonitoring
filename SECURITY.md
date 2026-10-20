# Security policy / Politique de sécurité

Vibe SSH Monitoring stores server credentials and runs commands as root on your servers: security reports are taken seriously.

## Supported versions

Only the [latest release](https://github.com/Baba0bab/VibeSSHMonitoring/releases/latest) receives security fixes.

## Reporting a vulnerability

**Do not open a public issue.** Use GitHub's private reporting: [**Report a vulnerability**](https://github.com/Baba0bab/VibeSSHMonitoring/security/advisories/new) (*Security* tab of the repository). Include the affected version, a description, the impact and steps to reproduce. You will get an answer within 7 days; please allow time for a fix before any public disclosure.

Examples of relevant issues: secrets written in clear text outside *plain text* mode, secrets leaking into logs, command lines or the clipboard history, host key verification bypass, command injection through server fields, weaknesses in the vault encryption.

---

## Versions maintenues

Seule la [dernière version](https://github.com/Baba0bab/VibeSSHMonitoring/releases/latest) reçoit des correctifs de sécurité.

## Signaler une faille

**N'ouvrez pas d'issue publique.** Utilisez le signalement privé de GitHub : [**Report a vulnerability**](https://github.com/Baba0bab/VibeSSHMonitoring/security/advisories/new) (onglet *Security* du dépôt). Indiquez la version concernée, une description, l'impact et les étapes pour reproduire. Réponse sous 7 jours ; merci de laisser le temps de publier un correctif avant toute divulgation.

Exemples de problèmes concernés : secrets écrits en clair hors du mode « en clair », secrets visibles dans les journaux, une ligne de commande ou l'historique du presse-papiers, contournement de la vérification des clés d'hôte, injection de commande via les champs d'un serveur, faiblesse du chiffrement du coffre.
