// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Diagnostics;
using System.IO;
using System.Text;

namespace VibeSSHMonitoring.Core;

/// <summary>
/// Ouverture d'une session SSH dans un terminal externe, via le client OpenSSH de Windows.
/// Le mot de passe ne peut pas lui être transmis : ssh.exe le demande lui-même (l'interface
/// peut le placer temporairement dans le presse-papiers). Avec une clé, rien à saisir.
/// </summary>
public static class ExternalTerminal
{
    public static string SshExe
    {
        get
        {
            string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
            return File.Exists(system) ? system : "ssh.exe";
        }
    }

    public static string? FindPutty()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PuTTY", "putty.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PuTTY", "putty.exe")
        };
        foreach (var path in candidates)
            if (File.Exists(path))
                return path;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string path = Path.Combine(dir.Trim(), "putty.exe");
                if (File.Exists(path))
                    return path;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    /// <summary>Arguments de ssh.exe, un élément par argument (non échappés).</summary>
    public static List<string> SshArguments(ServerConfig server, bool rootShell)
    {
        var args = new List<string> { "-p", server.Port.ToString(), "-o", "ServerAliveInterval=30" };
        if (server.Auth == AuthMethod.PrivateKey && !string.IsNullOrWhiteSpace(server.KeyPath))
        {
            args.Add("-i");
            args.Add(SshConnector.ExpandPath(server.KeyPath));
        }
        string? rootCommand = rootShell ? server.EffectiveElevation switch
        {
            ElevationMethod.Sudo => "sudo -i",
            ElevationMethod.Su => "su -",
            _ => null
        } : null;
        if (rootCommand is not null)
            args.Add("-t");
        args.Add($"{server.Username}@{server.Host.Trim()}");
        if (rootCommand is not null)
            args.Add(rootCommand);
        return args;
    }

    public static void Launch(ServerConfig server, TerminalMode mode, bool rootShell, string customTemplate)
    {
        var args = SshArguments(server, rootShell);
        string title = server.DisplayName;
        ProcessStartInfo psi;

        switch (mode)
        {
            case TerminalMode.WindowsTerminal:
            {
                // wt.exe est un alias d'exécution d'application : lancé via le shell
                var wtArgs = new List<string> { "new-tab", "--title", title, SshExe };
                wtArgs.AddRange(args);
                psi = new ProcessStartInfo("wt.exe", JoinWindowsArgs(wtArgs)) { UseShellExecute = true };
                break;
            }
            case TerminalMode.PowerShell:
            {
                // Commande encodée : aucun souci d'échappement entre Windows et PowerShell
                var sb = new StringBuilder($"$Host.UI.RawUI.WindowTitle = {PsQuote(title)}; & {PsQuote(SshExe)}");
                foreach (var a in args)
                    sb.Append(' ').Append(PsQuote(a));
                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(sb.ToString()));
                psi = new ProcessStartInfo("powershell.exe", $"-NoLogo -NoExit -EncodedCommand {encoded}")
                {
                    UseShellExecute = true
                };
                break;
            }
            case TerminalMode.Cmd:
            {
                // cmd /k "…" : les guillemets extérieurs sont retirés par cmd, le reste est conservé
                string inner = $"title {title.Replace("&", "").Replace("|", "")} & {JoinWindowsArgs(new[] { SshExe }.Concat(args))}";
                psi = new ProcessStartInfo("cmd.exe", $"/k \"{inner}\"") { UseShellExecute = true };
                break;
            }
            case TerminalMode.PuTTY:
            {
                string putty = FindPutty() ?? throw new FileNotFoundException(
                    "PuTTY est introuvable (ni dans Program Files, ni dans le PATH).");
                var puttyArgs = new List<string> { "-ssh", "-P", server.Port.ToString() };
                if (server.Auth == AuthMethod.PrivateKey &&
                    SshConnector.ExpandPath(server.KeyPath).EndsWith(".ppk", StringComparison.OrdinalIgnoreCase))
                {
                    puttyArgs.Add("-i");
                    puttyArgs.Add(SshConnector.ExpandPath(server.KeyPath));
                }
                puttyArgs.Add($"{server.Username}@{server.Host.Trim()}");
                psi = new ProcessStartInfo(putty, JoinWindowsArgs(puttyArgs)) { UseShellExecute = false };
                break;
            }
            case TerminalMode.Custom:
            {
                string keyOpt = server.Auth == AuthMethod.PrivateKey && !string.IsNullOrWhiteSpace(server.KeyPath)
                    ? $"-i \"{SshConnector.ExpandPath(server.KeyPath)}\""
                    : "";
                string line = customTemplate
                    .Replace("{host}", server.Host.Trim())
                    .Replace("{port}", server.Port.ToString())
                    .Replace("{user}", server.Username)
                    .Replace("{name}", title)
                    .Replace("{keyopt}", keyOpt)
                    .Replace("{ssh}", $"\"{SshExe}\"")
                    .Trim();
                var (exe, rest) = SplitCommandLine(line);
                if (exe.Length == 0)
                    throw new InvalidOperationException("La commande du terminal personnalisé est vide.");
                psi = new ProcessStartInfo(exe, rest) { UseShellExecute = true };
                break;
            }
            default:
                throw new InvalidOperationException("Le terminal intégré ne s'ouvre pas dans une fenêtre externe.");
        }

        Process.Start(psi)?.Dispose();
    }

    private static (string Exe, string Args) SplitCommandLine(string line)
    {
        if (line.StartsWith('"'))
        {
            int end = line.IndexOf('"', 1);
            return end < 0 ? (line.Trim('"'), "") : (line[1..end], line[(end + 1)..].Trim());
        }
        int space = line.IndexOf(' ');
        return space < 0 ? (line, "") : (line[..space], line[(space + 1)..].Trim());
    }

    private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Échappement des arguments selon les règles de CommandLineToArgvW.</summary>
    private static string JoinWindowsArgs(IEnumerable<string> args) => string.Join(' ', args.Select(QuoteWindowsArg));

    private static string QuoteWindowsArg(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', ';' }) < 0)
            return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
                sb.Append('\\', backslashes * 2 + 1);
            else
                sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
