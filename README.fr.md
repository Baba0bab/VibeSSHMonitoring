<p align="center">
  <img src="docs/icon.png" width="96" alt="Vibe SSH Monitoring">
</p>

<h1 align="center">Vibe SSH Monitoring</h1>

<p align="center">
  Supervisez et mettez à jour vos serveurs Debian / Ubuntu (VPS, dédiés, Raspberry Pi…) en SSH, depuis une application Windows native.
</p>

<p align="center">
  <a href="https://github.com/Baba0bab/VibeSSHMonitoring/actions/workflows/ci.yml"><img src="https://github.com/Baba0bab/VibeSSHMonitoring/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/Baba0bab/VibeSSHMonitoring/releases/latest"><img src="https://img.shields.io/github/v/release/Baba0bab/VibeSSHMonitoring?include_prereleases" alt="Dernière version"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/licence-GPL--3.0-blue" alt="Licence : GPL-3.0"></a>
  <img src="https://img.shields.io/badge/plateforme-Windows%2010%20%7C%2011-0078D4" alt="Windows 10 | 11">
</p>

<p align="center">
  <a href="README.md">🇬🇧 English</a> · 🇫🇷 Français
</p>

> L'interface est disponible en **anglais, français, allemand et espagnol**. Elle suit la langue d'affichage de Windows par défaut ; changez-la dans *Paramètres → Général → Langue*.

![Tableau de bord des serveurs](docs/screenshots/servers.png)

## Fonctionnalités

- **Liste des serveurs** avec leur état, relevé en tâche de fond : en ligne / hors ligne et latence, CPU, RAM, disque, charge, distribution, noyau, durée de fonctionnement, nombre de mises à jour (dont sécurité) et « redémarrage requis ».
- **Vérifier les mises à jour** : lance `apt-get update` en root, puis affiche la liste de `apt list --upgradable`, paquet par paquet (version actuelle → nouvelle, dépôt, badge sécurité).
- **Mettre à jour** : `apt-get upgrade` (ou `full-upgrade`) non interactif, avec journal en direct. Les fichiers de configuration modifiés localement sont conservés (`--force-confold`), needrestart redémarre les services concernés, `autoremove` en option.
- **Accès root, par serveur** : connexion directe en `root`, **sudo**, ou **su** (connexion avec un utilisateur simple, puis passage root avec le mot de passe root pour les serveurs qui refusent la connexion directe en root).
- **Ouvrir SSH** : terminal intégré (onglets, xterm.js : couleurs, `htop`, `nano`, copier / coller) ou externe : Windows Terminal, PowerShell, invite de commandes, PuTTY ou commande personnalisée. Option « ouvrir directement en root » (`sudo -i` / `su -`, mot de passe saisi pour vous dans le terminal intégré).
- **Actions groupées** sur une sélection de serveurs (actualiser, vérifier les mises à jour, mettre à jour, redémarrer quatre serveurs à la fois), **groupes / tags** avec filtres, recherche, bouton **Redémarrer**.
- **Zone de notification** : la supervision continue fenêtre fermée ; notifications Windows (nouvelles mises à jour, serveur injoignable, clé d'hôte modifiée) ; recherche automatique des mises à jour (toutes les 6 / 12 / 24 h).
- **Paramètres globaux + remplacements par serveur** pour la supervision, le terminal et les mises à jour.
- **Installateur et mises à jour automatiques** : installateur par utilisateur (sans droits administrateur) ; l'application se met à jour toute seule depuis les Releases GitHub (petits téléchargements delta, installés à la fermeture).
- **Multilingue** : anglais, français, allemand et espagnol.

| Paramètres | Fiche d'un serveur |
|---|---|
| ![Paramètres](docs/screenshots/settings.png) | ![Fiche d'un serveur](docs/screenshots/server-editor.png) |

## Prérequis

| | |
|---|---|
| **Votre PC** | Windows 10 ou Windows 11, x64. Rien d'autre à installer (le runtime .NET est inclus). Le terminal intégré utilise le [runtime Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/), inclus dans Windows 11 et dans un Windows 10 à jour. |
| **Vos serveurs** | Debian, Ubuntu ou dérivée (tout système avec `apt-get`), accessible en SSH. Authentification par mot de passe ou par clé. |

## Installation

1. Téléchargez **`VibeSSHMonitoring-win-Setup.exe`** depuis la [dernière version](https://github.com/Baba0bab/VibeSSHMonitoring/releases/latest).
2. Lancez-le. L'application s'installe pour votre compte Windows uniquement, **sans droits administrateur**, dans `%LOCALAPPDATA%\VibeSSHMonitoring`, avec un raccourci dans le menu Démarrer et sur le bureau, puis démarre.

> **Windows SmartScreen** : l'installateur n'est pas signé numériquement, Windows peut donc afficher « Windows a protégé votre ordinateur ». Cliquez sur **Informations complémentaires → Exécuter quand même**. Chaque version publie un fichier `SHA256SUMS.txt` pour vérifier le téléchargement :
> ```powershell
> Get-FileHash .\VibeSSHMonitoring-win-Setup.exe -Algorithm SHA256
> ```

**Version portable** : `VibeSSHMonitoring-win-Portable.zip`, décompressez-la où vous voulez (clé USB…) et lancez `VibeSSHMonitoring.exe`. Elle se met aussi à jour toute seule.

### Mises à jour

L'application interroge les Releases GitHub au démarrage puis toutes les 12 heures, télécharge les nouvelles versions en arrière-plan (le plus souvent un petit *delta*) et les installe **à la fermeture de l'application**, ou tout de suite avec **Redémarrer et mettre à jour** (*Paramètres → Mises à jour de l'application*, ou le bouton qui apparaît dans le menu latéral). La recherche et l'installation automatiques peuvent être désactivées, et les préversions activées, dans cette même section.

### Désinstallation

*Paramètres Windows → Applications → Applications installées → Vibe SSH Monitoring → Désinstaller*. L'application, ses raccourcis, le lancement avec Windows et le cache du terminal intégré sont supprimés. Vos serveurs et mots de passe (`%APPDATA%\VibeSSHMonitoring`) sont **conservés** pour une réinstallation : supprimez ce dossier pour les effacer aussi.

## Premiers pas

1. **Ajouter un serveur** : adresse, port, utilisateur, puis mot de passe ou clé SSH.
2. Choisissez l'**accès root** (*Aucune* si vous vous connectez en root, *sudo* ou *su*).
3. Cliquez sur **Tester la connexion** : la connexion puis l'accès root sont vérifiés. À la première connexion, comparez l'empreinte de la clé d'hôte affichée avec celle du serveur (`ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`) avant de l'approuver.
4. Facultatif mais conseillé : **Déployer ma clé** crée une clé SSH (`~/.ssh/id_ed25519`) si besoin, l'installe sur le serveur et bascule la fiche en authentification par clé.

### Accès root côté serveur

L'application lance ses commandes root sous la forme `sudo -- sh -c '…'` (ou via `su`). Au choix :

- **sudo avec mot de passe** (par défaut) : enregistrez le mot de passe sudo dans la fiche du serveur ; il est transmis sur l'entrée standard de sudo, jamais dans une ligne de commande.
- **sudo sans mot de passe** : laissez le mot de passe sudo vide et autorisez sudo sans mot de passe pour cet utilisateur :
  ```
  # /etc/sudoers.d/vibe-ssh-monitoring  (à éditer avec : sudo visudo -f /etc/sudoers.d/vibe-ssh-monitoring)
  votreutilisateur ALL=(root) NOPASSWD: ALL
  ```
  Une règle limitée à `apt-get` ne fonctionne **pas** : les commandes passent par `sh -c`, et autoriser `sh` revient de toute façon à donner un accès root complet.
- **su** : enregistrez le mot de passe root ; pour les serveurs où `root` ne peut pas se connecter en SSH.

## Sécurité

| Sujet | Ce que fait l'application |
|---|---|
| Stockage des mots de passe | Au choix : **Windows (DPAPI)** - chiffré et lié à votre session Windows, rien à saisir (par défaut) ; **mot de passe maître** - Argon2id + AES-256-GCM, demandé au démarrage, verrouillage automatique optionnel ; **en clair** - avec avertissement. Les secrets sont dans `secrets.json`, jamais dans `config.json`. |
| Clés d'hôte | L'empreinte SHA-256 est approuvée à la première connexion, puis **vérifiée à chaque connexion** : si elle change, la connexion est refusée et une alerte s'affiche. |
| sudo | Le mot de passe passe par l'entrée standard (`sudo -S`), jamais dans une ligne de commande ; la commande elle-même s'exécute avec l'entrée redirigée vers `/dev/null`. |
| Terminaux externes | `ssh.exe` ne peut pas recevoir de mot de passe : il est copié dans le presse-papiers (exclu de l'historique Win+V et de la synchronisation cloud) et effacé après 30 s. Avec une clé SSH, rien à saisir. |
| Réseau | L'application se connecte à vos serveurs, et à GitHub pour rechercher ses mises à jour (désactivable). La page du terminal intégré est locale et hors ligne (aucun CDN). Aucune télémétrie. |

Emplacement des données : `%APPDATA%\VibeSSHMonitoring\config.json` (serveurs, réglages), `%APPDATA%\VibeSSHMonitoring\secrets.json` (coffre). Une faille de sécurité ? Voir [SECURITY.md](SECURITY.md).

## Compiler depuis les sources

Prérequis : [SDK .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) et Windows. Visual Studio 2022 (17.14+), Rider ou VS Code sont facultatifs.

```powershell
git clone https://github.com/Baba0bab/VibeSSHMonitoring.git
cd VibeSSHMonitoring
dotnet run                          # compile et lance (Debug), avec vos vrais serveurs
dotnet run --launch-profile Test    # mode test : données séparées et un serveur de démonstration
dotnet test Tests                   # tests unitaires
```

Une copie lancée avec `dotnet run` n'est pas installée : les mises à jour automatiques y sont désactivées (la page des paramètres l'indique).

**Les tests** couvrent le coffre (mot de passe maître, mauvais mot de passe, changements de mode), la lecture des sorties (`apt list`, `/proc`), la résolution des réglages, les arguments SSH, les traductions (mêmes clés et mêmes paramètres dans chaque langue) et la syntaxe de chaque script shell envoyé aux serveurs, vérifiée avec le `sh` de Git for Windows s'il est installé.

`dotnet run` ouvre toujours votre vraie configuration, même dans un terminal où des variables de test sont encore définies ; quittez d'abord la copie installée (icône de la zone de notification › Quitter), sinon seule sa fenêtre s'affiche.

**Mode test** : `dotnet run --launch-profile Test` utilise une configuration et un coffre séparés (`%TEMP%\VibeSSHMonitoring.Demo`) et ajoute un serveur de démonstration qui rejoue une mise à jour en boucle. Il tourne à côté de la copie installée. Pour choisir le dossier, définissez `VIBESSHMONITORING_HOME` avant cette commande, ou avant de lancer directement l'exe compilé.

### Construire l'installateur

L'installateur et les paquets de mise à jour sont produits avec [Velopack](https://velopack.io) (`vpk`, version figée dans [`dotnet-tools.json`](dotnet-tools.json)) par [`scripts/pack.ps1`](scripts/pack.ps1), le même script que le workflow de release :

```powershell
./scripts/pack.ps1 -Version 1.0.1          # → Releases\VibeSSHMonitoring-win-Setup.exe, -Portable.zip, *.nupkg
```

Les notes de version intégrées au paquet (et affichées dans l'application) viennent de la section `## [x.y.z]` correspondante de [CHANGELOG.md](CHANGELOG.md). Si les paquets de la version précédente sont dans `Releases\`, un paquet delta est aussi généré.

Pour tester les mises à jour en local sans GitHub, faites pointer une copie installée vers un dossier contenant des paquets plus récents :

```powershell
$env:VIBESSHMONITORING_UPDATE_SOURCE = "C:\chemin\vers\Releases"
& "$env:LOCALAPPDATA\VibeSSHMonitoring\current\VibeSSHMonitoring.exe"
```

### Publier une version

Les versions sont construites par GitHub Actions ([`.github/workflows/release.yml`](.github/workflows/release.yml)) : ajoutez une section `## [x.y.z] - date` dans [CHANGELOG.md](CHANGELOG.md), commitez, puis poussez un tag de version. Le workflow lance les tests, construit l'installateur et les paquets de mise à jour (complet + delta), puis les publie sur GitHub Releases, les copies installées récupèrent la mise à jour d'elles-mêmes. Un tag avec suffixe (`v1.1.0-beta.1`) crée une préversion, proposée uniquement aux utilisateurs qui ont activé les préversions.

```powershell
git tag -a v1.0.1 -m "Vibe SSH Monitoring 1.0.1"
git push origin v1.0.1
```

## Organisation du code

```
├─ Program.cs            point d'entrée (crochets de l'installateur, puis WPF)
├─ Core/                 SSH, commandes distantes, coffre, configuration, mises à jour (sans interface)
│  ├─ Loc.cs            traductions (Localization/*.json, repli sur l'anglais)
│  ├─ SshConnection.cs   connexion, vérification de la clé d'hôte, exécution simple / sudo / su
│  ├─ RemoteCommands.cs  sonde d'état, commandes apt, lecture des résultats
│  ├─ SecretVault.cs     DPAPI, mot de passe maître (Argon2id + AES-GCM) ou clair
│  ├─ TerminalSession.cs session du terminal intégré (passage root automatique)
│  ├─ ExternalTerminal.cs Windows Terminal, PowerShell, cmd, PuTTY, personnalisé
│  └─ AppUpdater.cs      mises à jour depuis les Releases GitHub (Velopack)
├─ UI/                   modèle de vue, dialogues, icône de notification, vue du terminal intégré
├─ Assets/terminal/      page xterm.js chargée hors ligne par WebView2
├─ Localization/         textes de l'interface : en.json (référence), fr.json, de.json, es.json
├─ Themes/Theme.xaml     couleurs et styles des contrôles
├─ ServerManager.cs      boucle de supervision, actions, actions groupées, notifications
├─ AppController.cs      cycle de vie : config, coffre, fenêtre, zone de notification, verrouillage
├─ MainWindow.xaml       serveurs, détail d'un serveur, terminaux, paramètres
├─ ServerEditWindow.xaml fiche serveur, test de connexion, déploiement de clé
├─ scripts/pack.ps1     installateur et paquets de mise à jour (Velopack)
└─ Tests/                tests xUnit
```

## Contribuer

Signalements de bugs, idées, traductions et pull requests sont les bienvenus, voir [CONTRIBUTING.md](CONTRIBUTING.md). Pour ajouter une langue, copiez `Localization/en.json` vers `<code>.json`, traduisez les valeurs et ajoutez la langue à `Loc.Languages` dans `Core/Loc.cs`. Mainteneurs : procédure de publication dans [docs/RELEASING.md](docs/RELEASING.md).

## Licence

Copyright © 2026 Baba0bab. Distribué sous la **GNU General Public License v3.0 ou ultérieure**, voir [LICENSE](LICENSE). Vous pouvez utiliser, étudier, modifier et redistribuer ce logiciel ; les versions modifiées redistribuées doivent rester sous GPL et fournir leur code source.

Les composants tiers (SSH.NET, xterm.js, SDK WebView2, Velopack, Argon2…) sont listés dans [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
