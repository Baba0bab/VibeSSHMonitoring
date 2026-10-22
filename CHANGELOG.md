# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.2] - 2026-10-22

### Added
- New setting *Open the server page when updating* (*Settings → Server updates*, and per server in its editor): turn it off to stay on the current page when clicking **Update** and follow the progress on the card. On by default, as before.
- New setting *Concise labels* (*Settings → General*, under the language): uses shorter texts for the buttons that French, German and Spanish make long, such as *Check updates*. Applied after a restart.
- The *Reboot required* badge now has a tooltip explaining why: the packages that asked for the reboot and the running kernel.

### Changed
- On a server card, the last reading time and the running operation now sit beside the update badges, saving a line (they wrap below on narrow cards).

### Fixed
- The *Check updates*, *Update* and *Open SSH* buttons, and the texts that mention them, are now translated (they stayed in English in every language).
- The "could not start ssh-keygen" error is now translated.

## [1.0.1] - 2026-10-21

### Added
- The interface is now available in English, French, German and Spanish. It follows the Windows display language by default; choose another one in *Settings → General → Language* (applied after a restart, offered by the app).

### Changed
- English is now the default language of the app when the Windows display language is not supported (it was French only before).

## [1.0.0] - 2026-10-20

### Added
- Server list with background monitoring: online / offline and latency, CPU, RAM, disk, load, OS, kernel, uptime, pending updates (including security) and *reboot required*.
- **Check updates** (`apt-get update` + `apt list --upgradable`) and non-interactive **Update** (`upgrade` or `full-upgrade`, optional `autoremove`) with a live log.
- Root access per server: direct root, sudo (password or NOPASSWD) or su.
- **Open SSH**: built-in terminal (xterm.js in WebView2, tabs, automatic root shell) or external terminal (Windows Terminal, PowerShell, Command Prompt, PuTTY, custom command).
- Password or SSH key authentication, one-click SSH key deployment.
- Password vault: Windows DPAPI, master password (Argon2id + AES-256-GCM, auto-lock) or plain text.
- Host key approval and verification on every connection.
- Bulk actions, groups / tags with filters, search, reboot.
- System tray with Windows notifications and scheduled update checks.
- Global settings with per-server overrides.
- Windows installer (per-user, no admin rights, Start menu and desktop shortcuts, clean uninstall) and portable version.
- Automatic application updates from GitHub Releases (delta downloads, install on exit or on restart, optional pre-releases).

[Unreleased]: https://github.com/Baba0bab/VibeSSHMonitoring/compare/v1.0.2...HEAD
[1.0.2]: https://github.com/Baba0bab/VibeSSHMonitoring/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/Baba0bab/VibeSSHMonitoring/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/Baba0bab/VibeSSHMonitoring/releases/tag/v1.0.0
