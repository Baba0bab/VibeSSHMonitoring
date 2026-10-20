# Vibe SSH Monitoring : notes pour Claude (et les contributeurs)

Application Windows (C# / .NET 10 / WPF) de supervision et de mise à jour de serveurs Debian / Ubuntu en SSH.
Nom affiché **Vibe SSH Monitoring**, identifiant **VibeSSHMonitoring** (exe, namespace, paquet), dépôt
https://github.com/Baba0bab/VibeSSHMonitoring, licence GPL-3.0-or-later. Interface et commentaires du code
en **français** ; documentation publique **bilingue** (README.md anglais + README.fr.md, CONTRIBUTING, RELEASING).

## Commandes

```powershell
dotnet build VibeSSHMonitoring.slnx -c Release      # doit donner 0 avertissement
dotnet test Tests                                    # 21+ tests (coffre, parsing, scripts shell via le sh de Git)
$env:VIBESSHMONITORING_HOME = "$env:TEMP\vsm-test"; dotnet run   # configuration et coffre isolés
./scripts/pack.ps1 -Version 1.2.3 -OutputDir $env:TEMP\vsm-pack  # installateur + paquets Velopack, sans publier
```

Arrêter l'application avant de compiler (`Get-Process VibeSSHMonitoring | Stop-Process`) : l'exe en cours verrouille la sortie.

## Organisation

- `Core/` : sans WPF. `SshConnection.cs` (connexion, clé d'hôte, exécution simple / sudo / su), `RemoteCommands.cs` (sonde, apt, parsing), `SecretVault.cs` (DPAPI / mot de passe maître / clair), `AppUpdater.cs` (Velopack), `TerminalSession.cs`, `ExternalTerminal.cs`.
- `ServerManager.cs` (supervision, actions), `AppController.cs` (cycle de vie, coffre, zone de notification, mises à jour), `MainWindow.xaml` (pages), `ServerEditWindow.xaml` (fiche serveur), `Program.cs` (Velopack avant WPF).
- `Themes/Theme.xaml` : thème hérité de GoveeScreen (projet de l'auteur, `C:\serveurs\GoveeScreenV2`).
- `Assets/terminal/` : xterm.js hors ligne (WebView2). `scripts/pack.ps1` : packaging. `tools/ui-test/` : pilotage de l'interface.

## Invariants à ne jamais changer

Inscrits dans chaque copie installée ou dans les données des utilisateurs :
- entropie DPAPI `"SshVibeMonitoring.v1"` (`Core/SecretVault.cs`), sinon les coffres existants deviennent illisibles ;
- dossiers `%APPDATA%\VibeSSHMonitoring` (config, coffre) et `%LOCALAPPDATA%\VibeSSHMonitoring.Cache` ; la migration depuis l'ancien nom `SshVibeMonitoring` reste en place ;
- `%LOCALAPPDATA%\VibeSSHMonitoring` appartient à l'installateur (Velopack) : n'y rien écrire ;
- identifiant de paquet `VibeSSHMonitoring`, canal `win`, `AppUpdater.RepositoryUrl`, noms des fichiers de release ;
- aucun secret dans une ligne de commande, un journal ou `config.json` (sudo : mot de passe sur stdin ; commandes root via `sudo -- sh -c` avec `exec </dev/null`) ;
- commandes envoyées aux serveurs toujours non interactives (apt : `DEBIAN_FRONTEND=noninteractive`, `--force-confold`).

## Vérifier un changement

- **Pas de serveur Linux ici** (ni WSL ni Docker) : tout ce qui touche à un vrai serveur doit être validé par l'utilisateur sur ses serveurs. Les scripts shell sont au moins vérifiés par les tests (`sh -n` du sh de Git).
- **Interface** : lancer avec `VIBESSHMONITORING_HOME` (ne jamais toucher la vraie config), puis
  `tools/ui-test/Invoke-UiElement.ps1` (clic par nom, `-AutomationId x -Value y`, `-List`) et
  `tools/ui-test/Capture-Window.ps1 -Out shot.png` (capture même masquée). Les captures d'écran du bureau
  (computer-use) masquent les autres fenêtres de l'utilisateur et ne montrent pas l'application : utiliser ces scripts.
  Les boutons à glyphe ont pour nom leur caractère (`[char]0xE712` = « ⋯ ») ; les dialogues modaux construits en code (`UI/Dialogs.cs`) ne sont pas toujours exposés à UI Automation.
- **Installateur et mises à jour** (testé de bout en bout le 2026-10-20) : `pack.ps1` deux versions dans un même dossier (delta),
  installer la première avec `VibeSSHMonitoring-win-Setup.exe --silent` en définissant `VIBESSHMONITORING_HOME` et
  `VIBESSHMONITORING_UPDATE_SOURCE=<dossier des paquets>`, rechercher depuis les paramètres, puis **désinstaller**
  (`%LOCALAPPDATA%\VibeSSHMonitoring\Update.exe uninstall --silent`) pour laisser la machine propre.

## Publier une version

Procédure complète : [docs/RELEASING.md](docs/RELEASING.md). En bref : section `## [X.Y.Z] - AAAA-MM-JJ` dans
CHANGELOG.md (titre exact : c'est la source des notes de version), commit sur `main`, tag annoté `vX.Y.Z`, push du tag →
`.github/workflows/release.yml` construit et publie ; suivre avec `gh run watch`, vérifier les fichiers avec `gh release view`.

**Toujours demander la confirmation explicite de l'utilisateur avant** `git push`, la création ou le push d'un tag,
`gh release create/edit/delete` ou `gh repo create` : c'est public, et une release est téléchargée automatiquement par
toutes les copies installées. Ne jamais supprimer une release publiée : en cas de problème, publier un correctif.

## Pièges connus

- Bash (outil) : les heredocs contenant des apostrophes échouent parfois → écrire le script dans un fichier.
- Scripts `.ps1` avec accents : enregistrer en UTF-8 **avec BOM** (Windows PowerShell 5.1), cf. `.editorconfig`.
- PowerShell : les noms de variables ignorent la casse (`$window` = paramètre `$Window`) ; `$matches` est réservé.
- WebView2 (terminal intégré) s'affiche au-dessus du contenu WPF : pas d'animation ni de superposition sur cette page.
- Remplacements en Python : utiliser des chaînes brutes (`r'''…'''`) pour le C# contenant `\n` / `\r`.
