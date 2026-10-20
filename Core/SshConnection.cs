// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace VibeSSHMonitoring.Core;

public enum SshErrorKind
{
    Unreachable, Timeout, AuthFailed, HostKeyUnknown, HostKeyChanged,
    VaultLocked, ElevationFailed, KeyFile, Other
}

/// <summary>SSH error turned into a readable message, with its kind for the status display.</summary>
public sealed class SshOperationException : Exception
{
    public SshErrorKind Kind { get; }
    public string? HostKeyType { get; init; }
    public string? Fingerprint { get; init; }

    public SshOperationException(SshErrorKind kind, string message, Exception? inner = null)
        : base(message, inner) => Kind = kind;
}

public sealed record CommandResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Connected SSH client and the resources that must live as long as it does.</summary>
public sealed class ConnectedClient : IDisposable
{
    private readonly List<IDisposable> _resources;

    public SshClient Client { get; }
    public bool IsAlive => Client.IsConnected;

    internal ConnectedClient(SshClient client, List<IDisposable> resources)
    {
        Client = client;
        _resources = resources;
    }

    public void Dispose()
    {
        try { Client.Disconnect(); } catch { }
        Client.Dispose();
        foreach (var r in _resources)
            r.Dispose();
    }
}

public static class SshConnector
{
    /// <summary>
    /// Opens a connection. The host key must match the approved fingerprint: otherwise the
    /// connection is refused and the exception carries the received fingerprint, so that the
    /// interface can have it approved and try again.
    /// </summary>
    public static async Task<ConnectedClient> ConnectAsync(ServerConfig server, ServerSecrets secrets,
        bool vaultLocked, CancellationToken ct)
    {
        var resources = new List<IDisposable>();
        var methods = new List<AuthenticationMethod>();

        if (server.Auth == AuthMethod.Password)
        {
            if (string.IsNullOrEmpty(secrets.Password))
                throw vaultLocked
                    ? new SshOperationException(SshErrorKind.VaultLocked, Loc.T("Ssh.VaultLockedConnect"))
                    : new SshOperationException(SshErrorKind.AuthFailed, Loc.T("Ssh.NoPassword"));
            string password = secrets.Password;
            var passwordAuth = new PasswordAuthenticationMethod(server.Username, password);
            // Many servers only accept the password through "keyboard-interactive"
            var keyboardAuth = new KeyboardInteractiveAuthenticationMethod(server.Username);
            keyboardAuth.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                    prompt.Response = password;
            };
            methods.Add(passwordAuth);
            methods.Add(keyboardAuth);
            resources.Add(passwordAuth);
            resources.Add(keyboardAuth);
        }
        else
        {
            string path = ExpandPath(server.KeyPath);
            if (!File.Exists(path))
                throw new SshOperationException(SshErrorKind.KeyFile, Loc.T("Ssh.KeyNotFound", path));
            PrivateKeyFile key;
            try
            {
                key = string.IsNullOrEmpty(secrets.KeyPassphrase)
                    ? new PrivateKeyFile(path)
                    : new PrivateKeyFile(path, secrets.KeyPassphrase);
            }
            catch (SshPassPhraseNullOrEmptyException ex)
            {
                throw new SshOperationException(vaultLocked ? SshErrorKind.VaultLocked : SshErrorKind.KeyFile,
                    Loc.T(vaultLocked ? "Ssh.VaultLockedPassphrase" : "Ssh.KeyNeedsPassphrase"), ex);
            }
            catch (Exception ex) when (ex is SshException or InvalidOperationException or NotSupportedException or IOException)
            {
                throw new SshOperationException(SshErrorKind.KeyFile, Loc.T("Ssh.KeyUnreadable", ex.Message), ex);
            }
            resources.Add(key);
            var keyAuth = new PrivateKeyAuthenticationMethod(server.Username, key);
            methods.Add(keyAuth);
            resources.Add(keyAuth);
        }

        var info = new ConnectionInfo(server.Host.Trim(), server.Port, server.Username, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(15),
            RetryAttempts = 1
        };
        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };

        string? receivedType = null, receivedFingerprint = null;
        bool hostKeyRejected = false;
        client.HostKeyReceived += (_, e) =>
        {
            receivedType = e.HostKeyName;
            receivedFingerprint = e.FingerPrintSHA256;
            e.CanTrust = server.HostKeyFingerprint is not null &&
                         string.Equals(server.HostKeyFingerprint, e.FingerPrintSHA256, StringComparison.Ordinal);
            hostKeyRejected = !e.CanTrust;
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            return new ConnectedClient(client, resources);
        }
        catch (Exception ex)
        {
            client.Dispose();
            foreach (var r in resources)
                r.Dispose();
            if (hostKeyRejected)
            {
                bool changed = server.HostKeyFingerprint is not null;
                throw new SshOperationException(changed ? SshErrorKind.HostKeyChanged : SshErrorKind.HostKeyUnknown,
                    Loc.T(changed ? "Ssh.HostKeyChanged" : "Ssh.HostKeyUnknown"), ex)
                {
                    HostKeyType = receivedType,
                    Fingerprint = receivedFingerprint
                };
            }
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            throw Translate(ex);
        }
    }

    public static Exception Translate(Exception ex) => ex switch
    {
        SshOperationException or OperationCanceledException when ex is not TaskCanceledException => ex,
        SshAuthenticationException => new SshOperationException(SshErrorKind.AuthFailed, Loc.T("Ssh.AuthFailed"), ex),
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => new SshOperationException(
            SshErrorKind.Unreachable, Loc.T("Ssh.HostNotFound"), ex),
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => new SshOperationException(
            SshErrorKind.Unreachable, Loc.T("Ssh.ConnectionRefused"), ex),
        SocketException { SocketErrorCode: SocketError.TimedOut } => new SshOperationException(
            SshErrorKind.Timeout, Loc.T("Ssh.Timeout"), ex),
        SocketException se => new SshOperationException(SshErrorKind.Unreachable, Loc.T("Ssh.Network", se.Message), ex),
        SshOperationTimeoutException or TimeoutException or TaskCanceledException => new SshOperationException(
            SshErrorKind.Timeout, Loc.T("Ssh.Timeout"), ex),
        SshConnectionException => new SshOperationException(SshErrorKind.Unreachable,
            Loc.T("Ssh.ConnectionLost", ex.Message), ex),
        _ => new SshOperationException(SshErrorKind.Other, ex.Message, ex)
    };

    public static string ExpandPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path.StartsWith('~'))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return path;
    }
}

/// <summary>Runs remote commands, as the connected user or as root (sudo / su).</summary>
public static partial class RemoteExec
{
    private const string Marker = "__SVM_RC_";

    [GeneratedRegex(@"__SVM_RC_(\d+)__")]
    private static partial Regex MarkerRegex();

    /// <summary>Password prompt of sudo / su, in the server's language.</summary>
    [GeneratedRegex(@"(?i)(password|mot de passe|passwort|kennwort|contrase[ñn]a|senha)[^\n]*[:：]\s*$")]
    public static partial Regex PasswordPromptRegex();

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)|\x1B[()][A-Za-z0-9]|\x1B[=>78]")]
    private static partial Regex AnsiRegex();

    public static string StripAnsi(string text) => AnsiRegex().Replace(text, "");

    /// <summary>Quotes a string for sh: 'abc' → 'a'"'"'bc'.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    /// <summary>Runs <paramref name="script"/> (sh) as the connected user.</summary>
    public static Task<CommandResult> RunAsync(SshClient client, string script, Action<string>? output,
        CancellationToken ct) =>
        ExecAsync(client, "sh -c " + Quote(script), null, output, ct);

    /// <summary>Runs <paramref name="script"/> as root, using the server's elevation method.</summary>
    public static async Task<CommandResult> RunAsRootAsync(SshClient client, ServerConfig server, ServerSecrets secrets,
        bool vaultLocked, string script, Action<string>? output, CancellationToken ct)
    {
        string? password = secrets.ElevationSecretFor(server);
        switch (server.EffectiveElevation)
        {
            case ElevationMethod.Sudo:
            {
                // The command's stdin is redirected: the password sent to sudo can never reach it
                string inner = "sh -c " + Quote("exec </dev/null; " + script);
                string command = password is null
                    ? "LC_ALL=C sudo -n -- " + inner
                    : "LC_ALL=C sudo -S -p '' -- " + inner;
                var result = await ExecAsync(client, command, password is null ? null : password + "\n", output, ct)
                    .ConfigureAwait(false);
                ThrowOnSudoFailure(result, password is null, vaultLocked);
                return result;
            }
            case ElevationMethod.Su:
                if (password is null)
                    throw new SshOperationException(vaultLocked ? SshErrorKind.VaultLocked : SshErrorKind.ElevationFailed,
                        Loc.T(vaultLocked ? "Ssh.VaultLockedRoot" : "Ssh.NoRootPassword"));
                return await ExecSuAsync(client, script, password, output, ct).ConfigureAwait(false);
            default:
            {
                var result = await RunAsync(client, script, output, ct).ConfigureAwait(false);
                if (!result.Success && !server.IsRootUser &&
                    (result.Output.Contains("are you root?") || result.Output.Contains("Permission denied")))
                    throw new SshOperationException(SshErrorKind.ElevationFailed, Loc.T("Ssh.NotRoot"));
                return result;
            }
        }
    }

    private static void ThrowOnSudoFailure(CommandResult result, bool noPassword, bool vaultLocked)
    {
        if (result.Success)
            return;
        string head = result.Output.Length > 600 ? result.Output[..600] : result.Output;
        string? key = null;
        if (head.Contains("a password is required") || head.Contains("a terminal is required"))
            key = vaultLocked ? "Ssh.VaultLockedSudo" : "Ssh.SudoNeedsPassword";
        else if (head.Contains("incorrect password") || head.Contains("Sorry, try again"))
            key = "Ssh.SudoWrongPassword";
        else if (head.Contains("not in the sudoers") || head.Contains("is not allowed to run sudo"))
            key = "Ssh.SudoNotAllowed";
        else if (result.ExitCode == 127 && head.Contains("sudo"))
            key = "Ssh.SudoMissing";
        if (key is not null)
            throw new SshOperationException(noPassword && vaultLocked ? SshErrorKind.VaultLocked : SshErrorKind.ElevationFailed,
                Loc.T(key));
    }

    /// <summary>
    /// Command on an "exec" channel: standard output and errors are merged and streamed to
    /// <paramref name="output"/>. <paramref name="stdin"/> is sent, then the input is closed.
    /// </summary>
    public static async Task<CommandResult> ExecAsync(SshClient client, string command, string? stdin,
        Action<string>? output, CancellationToken ct)
    {
        using var cmd = client.CreateCommand(command, Encoding.UTF8);
        var all = new StringBuilder();
        var execute = cmd.ExecuteAsync(ct);

        if (stdin is not null)
        {
            using var input = cmd.CreateInputStream();
            await input.WriteAsync(Encoding.UTF8.GetBytes(stdin), ct).ConfigureAwait(false);
        }
        else
        {
            // End of standard input (EOF); a very fast command may already be finished
            try { cmd.CreateInputStream().Dispose(); }
            catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException) { }
        }

        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumps = Task.WhenAll(
            Pump(cmd.OutputStream, all, output, pumpCts.Token),
            Pump(cmd.ExtendedOutputStream, all, output, pumpCts.Token));
        try
        {
            await execute.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            pumpCts.Cancel();
            throw SshConnector.Translate(ex);
        }
        // The streams end with the command; safety net if the server does not close them cleanly
        if (await Task.WhenAny(pumps, Task.Delay(3000, CancellationToken.None)).ConfigureAwait(false) != pumps)
            pumpCts.Cancel();
        try { await pumps.ConfigureAwait(false); } catch (OperationCanceledException) { }

        string text;
        lock (all)
            text = all.ToString();
        int code = cmd.ExitStatus ?? (cmd.ExitSignal is not null ? 128 : -1);
        return new CommandResult(code, text);
    }

    private static async Task Pump(Stream stream, StringBuilder all, Action<string>? output, CancellationToken ct)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        byte[] buffer = new byte[8192];
        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                int count = decoder.GetChars(buffer, 0, read, chars, 0);
                if (count == 0)
                    continue;
                string text = new(chars, 0, count);
                lock (all)
                    all.Append(text);
                output?.Invoke(text);
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// su needs a terminal to read the password: open a shell with a PTY, start su (in English,
    /// to recognise its messages), answer the prompt, then read the output up to the marker that
    /// carries the exit code.
    /// </summary>
    private static async Task<CommandResult> ExecSuAsync(SshClient client, string script, string password,
        Action<string>? output, CancellationToken ct)
    {
        string rootScript = "exec </dev/null; " + script + "\nrc=$?; echo; echo \"" + Marker + "${rc}__\"";
        string line = "LC_ALL=C exec su - root -s /bin/sh -c " + Quote(rootScript) + "\n";

        var modes = new Dictionary<TerminalModes, uint> { [TerminalModes.ECHO] = 0 };
        using var shell = client.CreateShellStream("dumb", 220, 50, 0, 0, 64 * 1024, modes);
        using var reg = ct.Register(() => { try { shell.Dispose(); } catch { } });
        shell.Write(line);
        shell.Flush();

        var preamble = new StringBuilder();   // before the prompt: our command line, a possible MOTD
        var body = new StringBuilder();       // useful output, after the password
        int emitted = 0;
        bool passwordSent = false;
        bool done = false;
        var promptDeadline = DateTime.UtcNow.AddSeconds(25);
        int holdBack = Marker.Length + 8;     // a marker split across two packets is never displayed

        await Task.Run(() =>
        {
            var decoder = Encoding.UTF8.GetDecoder();
            byte[] buffer = new byte[8192];
            char[] chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
            while (!done)
            {
                if (!passwordSent && DateTime.UtcNow > promptDeadline)
                    throw new SshOperationException(SshErrorKind.ElevationFailed, Loc.T("Ssh.SuNoPromptTimeout"));

                int read;
                try
                {
                    if (!passwordSent && !shell.DataAvailable)
                    {
                        Thread.Sleep(50);   // bounded wait for the prompt, without blocking on Read
                        continue;
                    }
                    read = shell.Read(buffer, 0, buffer.Length);
                }
                catch (ObjectDisposedException)
                {
                    ct.ThrowIfCancellationRequested();
                    break;
                }
                if (read == 0)
                    break;   // channel closed: su (started with exec) has finished

                int count = decoder.GetChars(buffer, 0, read, chars, 0);
                string chunk = StripAnsi(new string(chars, 0, count)).Replace("\r", "");

                if (!passwordSent)
                {
                    preamble.Append(chunk);
                    string pre = preamble.ToString();
                    if (MarkerRegex().IsMatch(pre))
                    {
                        body.Append(pre);   // no prompt (permissive PAM): everything is useful
                        passwordSent = true;
                        done = true;
                    }
                    else if (PasswordPromptRegex().IsMatch(pre))
                    {
                        shell.Write(password + "\n");
                        shell.Flush();
                        passwordSent = true;
                    }
                    continue;
                }

                body.Append(chunk);
                int tailStart = Math.Max(0, body.Length - chunk.Length - holdBack);
                string tail = body.ToString(tailStart, body.Length - tailStart);
                int markerAt = tail.IndexOf(Marker, StringComparison.Ordinal);
                if (markerAt >= 0 && MarkerRegex().IsMatch(tail, markerAt))
                    done = true;

                if (output is not null)
                {
                    int safeEnd = markerAt >= 0 ? tailStart + markerAt : Math.Max(emitted, body.Length - holdBack);
                    if (safeEnd > emitted)
                    {
                        output(body.ToString(emitted, safeEnd - emitted));
                        emitted = safeEnd;
                    }
                }
            }
        }, CancellationToken.None).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        string text = body.ToString();
        var match = MarkerRegex().Match(text);
        if (!match.Success)
        {
            if (text.Contains("Authentication failure") || text.Contains("incorrect password") || text.Contains("su: "))
                throw new SshOperationException(SshErrorKind.ElevationFailed, Loc.T("Ssh.SuWrongPassword"));
            if (!passwordSent)
                throw new SshOperationException(SshErrorKind.ElevationFailed, Loc.T("Ssh.SuNoPrompt", preamble.ToString().Trim()));
            throw new SshOperationException(SshErrorKind.Other, Loc.T("Ssh.Interrupted"));
        }
        string result = text[..match.Index].TrimStart('\n');
        if (result.EndsWith('\n'))
            result = result[..^1];   // the empty "echo" added before the marker
        return new CommandResult(int.Parse(match.Groups[1].Value), result);
    }
}

/// <summary>
/// Persistent connection to a server, shared by monitoring and actions (several channels in
/// parallel on the same SSH session). Recreated on demand if it drops.
/// </summary>
public sealed class ServerConnection : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<ServerSecrets> _secrets;
    private readonly Func<bool> _vaultLocked;
    private ConnectedClient? _client;

    public ServerConfig Server { get; set; }

    public ServerConnection(ServerConfig server, Func<ServerSecrets> secrets, Func<bool> vaultLocked)
    {
        Server = server;
        _secrets = secrets;
        _vaultLocked = vaultLocked;
    }

    private async Task<SshClient> GetClientAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is { IsAlive: true })
                return _client.Client;
            _client?.Dispose();
            _client = null;
            _client = await SshConnector.ConnectAsync(Server, _secrets(), _vaultLocked(), ct).ConfigureAwait(false);
            return _client.Client;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the connection (credentials or host key changed, network error).</summary>
    public void Reset()
    {
        _gate.Wait();
        try
        {
            _client?.Dispose();
            _client = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<CommandResult> RunAsync(string script, Action<string>? output, CancellationToken ct) =>
        WithRetry(client => RemoteExec.RunAsync(client, script, output, ct), ct);

    /// <summary>Script run as the connected user, with data on its standard input.</summary>
    public Task<CommandResult> RunWithInputAsync(string script, string stdin, CancellationToken ct) =>
        WithRetry(client => RemoteExec.ExecAsync(client, "sh -c " + RemoteExec.Quote(script), stdin, null, ct), ct);

    public Task<CommandResult> RunAsRootAsync(string script, Action<string>? output, CancellationToken ct) =>
        WithRetry(client => RemoteExec.RunAsRootAsync(client, Server, _secrets(), _vaultLocked(), script, output, ct), ct);

    /// <summary>A connection kept open may be dead without knowing it: one single retry.</summary>
    private async Task<CommandResult> WithRetry(Func<SshClient, Task<CommandResult>> action, CancellationToken ct)
    {
        var client = await GetClientAsync(ct).ConfigureAwait(false);
        try
        {
            return await action(client).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStaleConnection(ex) && !ct.IsCancellationRequested)
        {
            Reset();
            client = await GetClientAsync(ct).ConfigureAwait(false);
            return await action(client).ConfigureAwait(false);
        }
    }

    private static bool IsStaleConnection(Exception ex) =>
        ex is SshConnectionException or ObjectDisposedException or SocketException ||
        ex is SshOperationException { Kind: SshErrorKind.Unreachable, InnerException: SshConnectionException or ObjectDisposedException };

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
        _gate.Dispose();
    }
}
