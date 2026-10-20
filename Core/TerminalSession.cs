// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Text;
using Renci.SshNet;

namespace VibeSSHMonitoring.Core;

/// <summary>
/// Session interactive du terminal intégré : connexion dédiée (indépendante de la supervision),
/// shell avec PTY xterm-256color. Peut passer root automatiquement (sudo -i / su -) en
/// répondant elle-même à l'invite de mot de passe.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    private readonly ConnectedClient _connection;
    private readonly ShellStream _shell;
    private readonly string? _elevationPassword;
    private readonly string? _rootCommand;
    private readonly StringBuilder _promptWatch = new();
    private DateTime _promptWatchUntil;
    private Timer? _rootTimer;
    private bool _rootRequested;
    private int _closed;

    public ServerConfig Server { get; }

    /// <summary>Données brutes du terminal (UTF-8), sur un thread de lecture.</summary>
    public event Action<byte[]>? Output;
    public event Action? Closed;

    private TerminalSession(ServerConfig server, ConnectedClient connection, ShellStream shell,
        string? rootCommand, string? elevationPassword)
    {
        Server = server;
        _connection = connection;
        _shell = shell;
        _rootCommand = rootCommand;
        _elevationPassword = elevationPassword;
    }

    public static async Task<TerminalSession> OpenAsync(ServerConfig server, ServerSecrets secrets, bool vaultLocked,
        uint columns, uint rows, bool rootShell, CancellationToken ct)
    {
        var connection = await SshConnector.ConnectAsync(server, secrets, vaultLocked, ct).ConfigureAwait(false);
        try
        {
            var shell = connection.Client.CreateShellStream("xterm-256color", Math.Max(columns, 20), Math.Max(rows, 5),
                0, 0, 64 * 1024);
            string? rootCommand = rootShell ? server.EffectiveElevation switch
            {
                ElevationMethod.Sudo => "sudo -i",
                ElevationMethod.Su => "su -",
                _ => null
            } : null;
            return new TerminalSession(server, connection, shell, rootCommand, secrets.ElevationSecretFor(server));
        }
        catch (Exception ex)
        {
            connection.Dispose();
            throw SshConnector.Translate(ex);
        }
    }

    /// <summary>Démarre la lecture, une fois les gestionnaires d'événements branchés.</summary>
    public void Start()
    {
        var thread = new Thread(ReadLoop) { IsBackground = true, Name = $"Terminal {Server.DisplayName}" };
        thread.Start();
    }

    private void ReadLoop()
    {
        byte[] buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                int read = _shell.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                byte[] chunk = buffer.AsSpan(0, read).ToArray();
                Output?.Invoke(chunk);
                OnData(chunk);
            }
        }
        catch
        {
            // Connexion coupée : la session se termine
        }
        RaiseClosed();
    }

    /// <summary>
    /// Passage root : la commande part quand le shell a fini d'afficher son accueil, puis
    /// la première invite de mot de passe reçue dans les 15 s reçoit le mot de passe enregistré.
    /// </summary>
    private void OnData(byte[] chunk)
    {
        if (_rootCommand is not null && !_rootRequested)
        {
            // Relancé à chaque paquet : part après 400 ms de silence (fin du MOTD, invite affichée)
            _rootTimer ??= new Timer(_ => SendRootCommand());
            _rootTimer.Change(400, Timeout.Infinite);
            return;
        }
        if (_elevationPassword is null || DateTime.UtcNow > _promptWatchUntil)
            return;

        lock (_promptWatch)
        {
            _promptWatch.Append(RemoteExec.StripAnsi(Encoding.UTF8.GetString(chunk)).Replace("\r", ""));
            if (_promptWatch.Length > 400)
                _promptWatch.Remove(0, _promptWatch.Length - 400);
            if (RemoteExec.PasswordPromptRegex().IsMatch(_promptWatch.ToString()))
            {
                _promptWatchUntil = DateTime.MinValue;   // une seule tentative
                _promptWatch.Clear();
                Write(_elevationPassword + "\r");
            }
        }
    }

    private void SendRootCommand()
    {
        if (_rootRequested)
            return;
        _rootRequested = true;
        _promptWatchUntil = DateTime.UtcNow.AddSeconds(15);
        Write(_rootCommand + "\r");
        _rootTimer?.Dispose();
    }

    public void Write(string data)
    {
        if (_closed != 0)
            return;
        try
        {
            _shell.Write(data);
            _shell.Flush();
        }
        catch
        {
            RaiseClosed();
        }
    }

    public void Resize(uint columns, uint rows)
    {
        if (_closed != 0 || columns == 0 || rows == 0)
            return;
        try { _shell.ChangeWindowSize(columns, rows, 0, 0); } catch { }
    }

    private void RaiseClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
            Closed?.Invoke();
    }

    public void Dispose()
    {
        _closed = 1;
        _rootTimer?.Dispose();
        try { _shell.Dispose(); } catch { }
        _connection.Dispose();
    }
}
