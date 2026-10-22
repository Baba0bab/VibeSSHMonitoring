// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Diagnostics;
using System.IO;
using System.Text;

namespace VibeSSHMonitoring.Core;

/// <summary>
/// Opens an SSH session in an external terminal, through the Windows OpenSSH client.
/// The password cannot be handed over: ssh.exe asks for it itself (the interface can put it
/// in the clipboard for a short time). With a key, there is nothing to type.
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

    /// <summary>ssh.exe arguments, one item per argument (not escaped).</summary>
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

    /// <summary>
    /// With <paramref name="background"/>, the window starts minimized so that it does not take
    /// the focus: the user can open several before starting to work.
    /// </summary>
    public static void Launch(ServerConfig server, TerminalMode mode, bool rootShell, string customTemplate,
        bool background = false)
    {
        var args = SshArguments(server, rootShell);
        string title = server.DisplayName;
        ProcessStartInfo psi;

        switch (mode)
        {
            case TerminalMode.WindowsTerminal:
            {
                // wt.exe is an app execution alias: started through the shell
                var wtArgs = new List<string> { "new-tab", "--title", title, SshExe };
                wtArgs.AddRange(args);
                psi = new ProcessStartInfo("wt.exe", JoinWindowsArgs(wtArgs)) { UseShellExecute = true };
                break;
            }
            case TerminalMode.PowerShell:
            {
                // Encoded command: no escaping issue between Windows and PowerShell
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
                // cmd /k "…": cmd strips the outer quotes and keeps the rest
                string inner = $"title {title.Replace("&", "").Replace("|", "")} & {JoinWindowsArgs(new[] { SshExe }.Concat(args))}";
                psi = new ProcessStartInfo("cmd.exe", $"/k \"{inner}\"") { UseShellExecute = true };
                break;
            }
            case TerminalMode.PuTTY:
            {
                string putty = FindPutty() ?? throw new FileNotFoundException(
                    Loc.T("Terminal.PuttyNotFound"));
                var puttyArgs = new List<string> { "-ssh", "-P", server.Port.ToString() };
                if (server.Auth == AuthMethod.PrivateKey &&
                    SshConnector.ExpandPath(server.KeyPath).EndsWith(".ppk", StringComparison.OrdinalIgnoreCase))
                {
                    puttyArgs.Add("-i");
                    puttyArgs.Add(SshConnector.ExpandPath(server.KeyPath));
                }
                puttyArgs.Add($"{server.Username}@{server.Host.Trim()}");
                // WindowStyle only applies through the shell
                psi = new ProcessStartInfo(putty, JoinWindowsArgs(puttyArgs)) { UseShellExecute = background };
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
                    throw new InvalidOperationException(Loc.T("Terminal.CustomCommandEmpty"));
                psi = new ProcessStartInfo(exe, rest) { UseShellExecute = true };
                break;
            }
            default:
                throw new InvalidOperationException(Loc.T("Terminal.EmbeddedNotExternal"));
        }

        if (background)
            psi.WindowStyle = ProcessWindowStyle.Minimized;
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

    /// <summary>Argument escaping following the CommandLineToArgvW rules.</summary>
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
