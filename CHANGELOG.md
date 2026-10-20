# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

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

[Unreleased]: https://github.com/Baba0bab/VibeSSHMonitoring/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/Baba0bab/VibeSSHMonitoring/releases/tag/v1.0.0
