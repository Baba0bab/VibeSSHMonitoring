# Releasing / Publier une version

🇬🇧 [English](#english) · 🇫🇷 [Français](#français)

## English

A release is a git tag `vX.Y.Z`. Pushing it runs [`.github/workflows/release.yml`](../.github/workflows/release.yml), which tests, builds the installer and the update packages with [`scripts/pack.ps1`](../scripts/pack.ps1) (Velopack), and publishes them on GitHub Releases. **Every installed copy downloads it automatically**: a published release cannot really be taken back, check before tagging.

### 1. Choose the version ([SemVer](https://semver.org/))

| Change | Bump | Example |
|---|---|---|
| Bug fixes only | patch | 1.2.0 → 1.2.1 |
| New features, compatible | minor | 1.2.1 → 1.3.0 |
| Breaking change (config format, dropped feature) | major | 1.3.0 → 2.0.0 |
| Test build for volunteers | pre-release suffix | 1.3.0-beta.1 |

Pre-releases (`-beta.1`, `-rc.1`) are only offered to users who enabled *Inclure les préversions*. The version comes **only from the tag**: never edit `<VersionPrefix>` in the `.csproj`.

### 2. Prepare

```powershell
git switch main; git pull
gh run list --workflow ci.yml --branch main --limit 1     # last CI run must be green
dotnet build VibeSSHMonitoring.slnx -c Release             # 0 warnings
dotnet test Tests                                          # all tests pass
```

### 3. Update CHANGELOG.md

Move the *Unreleased* entries into a new section. **Its title must be exactly `## [X.Y.Z] - YYYY-MM-DD`**: `scripts/pack.ps1` extracts it as the release notes shown in the app and on GitHub (empty notes and a warning otherwise). Group entries under `### Added`, `### Changed`, `### Fixed`, `### Removed`, `### Security`, user-oriented, one line each. Update the links at the bottom:

```markdown
## [Unreleased]

## [1.3.0] - 2026-11-02
### Added
- …

[Unreleased]: https://github.com/Baba0bab/VibeSSHMonitoring/compare/v1.3.0...HEAD
[1.3.0]: https://github.com/Baba0bab/VibeSSHMonitoring/compare/v1.2.1...v1.3.0
```

### 4. Optional local dry run

```powershell
./scripts/pack.ps1 -Version 1.3.0 -OutputDir $env:TEMP\vsm-pack   # builds Setup.exe and packages, publishes nothing
Get-Content artifacts\release-notes.md                             # the notes that will be shipped
```

### 5. Commit, tag, push

```powershell
git add CHANGELOG.md
git commit -m "Release v1.3.0"
git push origin main
git tag -a v1.3.0 -m "Vibe SSH Monitoring 1.3.0"
git push origin v1.3.0                                             # ← starts the release
```

### 6. Follow and verify

```powershell
gh run watch (gh run list --workflow release.yml --limit 1 --json databaseId --jq '.[0].databaseId')
gh release view v1.3.0
```

Expected assets: `VibeSSHMonitoring-win-Setup.exe`, `VibeSSHMonitoring-win-Portable.zip`, `VibeSSHMonitoring-1.3.0-full.nupkg`, `VibeSSHMonitoring-1.3.0-delta.nupkg` (missing only for the very first release), `releases.win.json`, `assets.win.json`, `RELEASES`, `SHA256SUMS.txt`. The release must be published (not a draft) and marked pre-release only for suffixed versions. Finally, on a machine with the previous version installed: *Paramètres → Mises à jour de l'application → Rechercher* should find and install it.

### If something goes wrong

- **Workflow failed before *Publish GitHub release***: nothing is public yet. Fix the problem on `main`, then move the tag: `git tag -d v1.3.0; git push origin :refs/tags/v1.3.0`, re-tag and push again.
- **Release published but broken**: do **not** delete it or edit its packages, installed copies may already be updating. Publish a fixed **new** version (1.3.1) right away; Velopack moves everyone to it.
- A draft release left behind by a failed run can be deleted: `gh release delete v1.3.0 --yes` (without `--cleanup-tag` if the tag is still wanted).

### Never change

These identifiers are baked into every installed copy; changing them breaks updates or user data:

- package id `VibeSSHMonitoring` (`scripts/pack.ps1`) and the channel `win`;
- `AppUpdater.RepositoryUrl` (`https://github.com/Baba0bab/VibeSSHMonitoring`), if the repository is ever renamed, GitHub redirects, but keep the old URL working;
- the DPAPI entropy `SshVibeMonitoring.v1` in `Core/SecretVault.cs`, and the config folder names (`%APPDATA%\VibeSSHMonitoring`, legacy migration from `SshVibeMonitoring`);
- the asset names produced by Velopack (do not rename files on the release page).

---

## Français

Une version est un tag git `vX.Y.Z`. Le pousser lance [`.github/workflows/release.yml`](../.github/workflows/release.yml) : tests, construction de l'installateur et des paquets de mise à jour avec [`scripts/pack.ps1`](../scripts/pack.ps1) (Velopack), publication sur GitHub Releases. **Toutes les copies installées la téléchargent d'elles-mêmes** : une version publiée ne se retire pas vraiment, vérifiez avant de taguer.

### 1. Choisir le numéro ([SemVer](https://semver.org/lang/fr/))

| Changement | Incrément | Exemple |
|---|---|---|
| Corrections uniquement | correctif | 1.2.0 → 1.2.1 |
| Nouveautés compatibles | mineure | 1.2.1 → 1.3.0 |
| Rupture (format de config, fonction retirée) | majeure | 1.3.0 → 2.0.0 |
| Version de test pour volontaires | suffixe de préversion | 1.3.0-beta.1 |

Les préversions (`-beta.1`, `-rc.1`) ne sont proposées qu'aux utilisateurs ayant activé *Inclure les préversions*. La version vient **uniquement du tag** : ne modifiez jamais `<VersionPrefix>` dans le `.csproj`.

### 2. Préparer

```powershell
git switch main; git pull
gh run list --workflow ci.yml --branch main --limit 1     # la dernière CI doit être verte
dotnet build VibeSSHMonitoring.slnx -c Release             # 0 avertissement
dotnet test Tests                                          # tous les tests passent
```

### 3. Mettre à jour CHANGELOG.md

Déplacez les entrées de *Unreleased* dans une nouvelle section. **Son titre doit être exactement `## [X.Y.Z] - AAAA-MM-JJ`** : `scripts/pack.ps1` l'extrait comme notes de version, affichées dans l'application et sur GitHub (sinon notes vides et avertissement). Classez sous `### Added`, `### Changed`, `### Fixed`, `### Removed`, `### Security`, du point de vue de l'utilisateur, une ligne par entrée. Mettez à jour les liens en bas (voir l'exemple anglais ci-dessus).

### 4. Essai local (facultatif)

```powershell
./scripts/pack.ps1 -Version 1.3.0 -OutputDir $env:TEMP\vsm-pack   # construit Setup.exe et les paquets, ne publie rien
Get-Content artifacts\release-notes.md                             # les notes qui seront livrées
```

### 5. Commit, tag, push

```powershell
git add CHANGELOG.md
git commit -m "Release v1.3.0"
git push origin main
git tag -a v1.3.0 -m "Vibe SSH Monitoring 1.3.0"
git push origin v1.3.0                                             # ← lance la publication
```

### 6. Suivre et vérifier

```powershell
gh run watch (gh run list --workflow release.yml --limit 1 --json databaseId --jq '.[0].databaseId')
gh release view v1.3.0
```

Fichiers attendus : `VibeSSHMonitoring-win-Setup.exe`, `VibeSSHMonitoring-win-Portable.zip`, `VibeSSHMonitoring-1.3.0-full.nupkg`, `VibeSSHMonitoring-1.3.0-delta.nupkg` (absent seulement pour la toute première version), `releases.win.json`, `assets.win.json`, `RELEASES`, `SHA256SUMS.txt`. La version doit être publiée (pas en brouillon), et marquée préversion uniquement si elle a un suffixe. Enfin, sur une machine où la version précédente est installée : *Paramètres → Mises à jour de l'application → Rechercher* doit la trouver et l'installer.

### En cas de problème

- **Workflow en échec avant *Publish GitHub release*** : rien n'est public. Corrigez sur `main`, puis déplacez le tag : `git tag -d v1.3.0; git push origin :refs/tags/v1.3.0`, retaguez et repoussez.
- **Version publiée mais défectueuse** : ne la supprimez **pas** et ne modifiez pas ses paquets, des copies sont peut-être déjà en train de se mettre à jour. Publiez tout de suite une **nouvelle** version corrigée (1.3.1) ; Velopack y fait passer tout le monde.
- Un brouillon laissé par une exécution ratée peut être supprimé : `gh release delete v1.3.0 --yes` (sans `--cleanup-tag` si le tag doit rester).

### À ne jamais changer

Ces identifiants sont inscrits dans chaque copie installée ; les changer casse les mises à jour ou les données :

- l'identifiant de paquet `VibeSSHMonitoring` (`scripts/pack.ps1`) et le canal `win` ;
- `AppUpdater.RepositoryUrl` (`https://github.com/Baba0bab/VibeSSHMonitoring`), si le dépôt est renommé un jour, GitHub redirige, mais l'ancienne URL doit continuer de fonctionner ;
- l'entropie DPAPI `SshVibeMonitoring.v1` dans `Core/SecretVault.cs`, et les noms des dossiers de configuration (`%APPDATA%\VibeSSHMonitoring`, migration depuis `SshVibeMonitoring`) ;
- les noms des fichiers produits par Velopack (ne renommez rien sur la page de la release).
