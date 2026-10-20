# Contributing / Contribuer

🇬🇧 [English](#english) · 🇫🇷 [Français](#français)

## English

Thanks for your interest! Bug reports, feature ideas and pull requests are welcome.

### Reporting a bug

Open an [issue](https://github.com/Baba0bab/VibeSSHMonitoring/issues/new/choose) with the app version (*Paramètres → À propos*), your Windows version, the server's distribution, the steps to reproduce and the error message or the server's *Journal des opérations*. **Remove IP addresses, user names and anything sensitive** before posting. Security issues: do **not** open a public issue, see [SECURITY.md](SECURITY.md).

### Development setup

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (and optionally Git for Windows, used by the shell-script tests).
2. `dotnet run` builds and starts the app; `dotnet test Tests` runs the tests.
3. Use a separate data folder so you don't touch your real servers: `$env:VIBESSHMONITORING_HOME = "$env:TEMP\vsm-dev"`.
4. Test against a disposable Debian / Ubuntu machine (VM, container with `openssh-server`, cheap VPS), never against production first.

### Pull requests

- One topic per pull request, branched from `main`; describe what changed and how you tested it.
- Follow the existing style (see [`.editorconfig`](.editorconfig)): file-scoped namespaces, `_camelCase` private fields, comments in French like the rest of the code base. The user interface is in French.
- Keep `Core/` free of WPF dependencies; add or update tests in `Tests/` for parsing, scripts and security-sensitive code.
- Any command sent to servers must stay non-interactive and must never put a secret on a command line.
- `dotnet build` must produce **no warnings**, and `dotnet test Tests` must pass (the CI checks both).
- Add a line to the *Unreleased* section of [CHANGELOG.md](CHANGELOG.md).

By contributing, you agree that your contribution is licensed under the GPL-3.0-or-later, like the project.

## Français

Merci de votre intérêt ! Signalements de bugs, idées et pull requests sont les bienvenus.

### Signaler un bug

Ouvrez une [issue](https://github.com/Baba0bab/VibeSSHMonitoring/issues/new/choose) en indiquant la version de l'application (*Paramètres → À propos*), votre version de Windows, la distribution du serveur, les étapes pour reproduire et le message d'erreur ou le *Journal des opérations* du serveur. **Retirez adresses IP, noms d'utilisateur et toute information sensible** avant de publier. Faille de sécurité : **n'ouvrez pas** d'issue publique, voir [SECURITY.md](SECURITY.md).

### Environnement de développement

1. Installez le [SDK .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) (et si possible Git for Windows, utilisé par les tests de scripts shell).
2. `dotnet run` compile et lance l'application ; `dotnet test Tests` lance les tests.
3. Utilisez un dossier de données séparé pour ne pas toucher à vos vrais serveurs : `$env:VIBESSHMONITORING_HOME = "$env:TEMP\vsm-dev"`.
4. Testez sur une machine Debian / Ubuntu jetable (VM, conteneur avec `openssh-server`, petit VPS), jamais d'abord en production.

### Pull requests

- Un sujet par pull request, à partir de `main` ; décrivez le changement et comment vous l'avez testé.
- Suivez le style existant (voir [`.editorconfig`](.editorconfig)) : namespaces de fichier, champs privés `_camelCase`, commentaires en français comme le reste du code. L'interface est en français.
- Gardez `Core/` sans dépendance à WPF ; ajoutez ou mettez à jour les tests dans `Tests/` pour l'analyse des sorties, les scripts et le code sensible.
- Toute commande envoyée aux serveurs doit rester non interactive et ne jamais placer de secret dans une ligne de commande.
- `dotnet build` ne doit produire **aucun avertissement** et `dotnet test Tests` doit réussir (la CI vérifie les deux).
- Ajoutez une ligne dans la section *Unreleased* de [CHANGELOG.md](CHANGELOG.md).

En contribuant, vous acceptez que votre contribution soit distribuée sous GPL-3.0-or-later, comme le projet.
