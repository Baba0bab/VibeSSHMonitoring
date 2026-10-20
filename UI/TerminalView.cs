// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VibeSSHMonitoring.Core;

namespace VibeSSHMonitoring.UI;

/// <summary>
/// Terminal intégré : xterm.js dans WebView2 (page locale, hors ligne), relié à une
/// <see cref="TerminalSession"/>. Entrée après une déconnexion = reconnexion.
/// </summary>
public sealed class TerminalView : Border, IDisposable
{
    private const string HostName = "terminal.svm";
    private static Task<CoreWebView2Environment>? _environment;

    private readonly ServerManager _manager;
    private readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x17, 0x17, 0x1E) };
    private readonly MemoryStream _pending = new();
    private TerminalSession? _session;
    private bool _flushScheduled;
    private bool _connecting;
    private bool _disposed;
    private uint _columns = 120, _rows = 30;

    public ServerConfig Server { get; }
    public TerminalState State { get; private set; } = TerminalState.Connecting;
    public event Action? StateChanged;

    public enum TerminalState { Connecting, Connected, Closed }

    public TerminalView(ServerManager manager, ServerConfig server)
    {
        _manager = manager;
        Server = server;
        Background = (Brush)Application.Current.FindResource("LayerBrush");
        Child = _web;
        _ = InitializeAsync();
    }

    private static Task<CoreWebView2Environment> GetEnvironment()
    {
        // Données WebView2 dans le cache local : le dossier de l'exe est géré par l'installateur
        string folder = Path.Combine(AppConfig.CacheDirectory, "WebView2");
        return _environment ??= CoreWebView2Environment.CreateAsync(null, folder);
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async(await GetEnvironment());
        }
        catch (Exception ex)
        {
            ShowFallback(ex is WebView2RuntimeNotFoundException
                ? "Le runtime Microsoft Edge WebView2 est introuvable. Installez-le, ou choisissez un terminal externe dans les paramètres."
                : $"Impossible de démarrer le terminal intégré : {ex.Message}");
            return;
        }
        if (_disposed)
            return;

        var core = _web.CoreWebView2;
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;   // F5, Ctrl+F… vont au terminal
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;

        string assets = Path.Combine(AppContext.BaseDirectory, "Assets", "terminal");
        core.SetVirtualHostNameToFolderMapping(HostName, assets, CoreWebView2HostResourceAccessKind.Deny);
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"https://{HostName}/", StringComparison.OrdinalIgnoreCase))
                e.Cancel = true;   // la page ne quitte jamais le terminal local
        };
        core.WebMessageReceived += OnWebMessage;
        core.Navigate($"https://{HostName}/index.html");
    }

    private void ShowFallback(string message)
    {
        Child = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24),
            Foreground = (Brush)Application.Current.FindResource("TextSecondaryBrush")
        };
        SetState(TerminalState.Closed);
    }

    // ===== Messages de la page =====

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement message;
        try
        {
            message = JsonDocument.Parse(e.WebMessageAsJson).RootElement;
        }
        catch (JsonException)
        {
            return;
        }
        switch (message.GetProperty("t").GetString())
        {
            case "ready":
                _columns = message.GetProperty("cols").GetUInt32();
                _rows = message.GetProperty("rows").GetUInt32();
                if (_session is null)
                    _ = ConnectAsync();
                break;
            case "in":
                string data = message.GetProperty("d").GetString() ?? "";
                if (_session is not null && State == TerminalState.Connected)
                    _session.Write(data);
                else if (State == TerminalState.Closed && data.Contains('\r'))
                    _ = ConnectAsync();
                break;
            case "resize":
                _columns = message.GetProperty("cols").GetUInt32();
                _rows = message.GetProperty("rows").GetUInt32();
                _session?.Resize(_columns, _rows);
                break;
            case "copy":
                ClipboardHelper.SetText(message.GetProperty("text").GetString() ?? "");
                break;
            case "paste":
                Post(new { t = "paste", text = ClipboardHelper.GetText() });
                break;
            case "link":
                string url = message.GetProperty("url").GetString() ?? "";
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http"))
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                break;
        }
    }

    private void Post(object message)
    {
        if (_disposed || _web.CoreWebView2 is null)
            return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void Status(string text) => Post(new { t = "status", text });

    public void FocusTerminal()
    {
        _web.Focus();
        Post(new { t = "focus" });
    }

    // ===== Session =====

    private async Task ConnectAsync()
    {
        if (_connecting || _disposed)
            return;
        _connecting = true;
        SetState(TerminalState.Connecting);
        Status($"Connexion à {Server.Endpoint}…");
        try
        {
            var session = await _manager.OpenTerminalSessionAsync(Server, _columns, _rows);
            if (session is null)
            {
                Status("Connexion annulée. Appuyez sur Entrée pour réessayer.");
                SetState(TerminalState.Closed);
                return;
            }
            if (_disposed)
            {
                session.Dispose();
                return;
            }
            _session?.Dispose();
            _session = session;
            session.Output += OnOutput;
            session.Closed += () => Dispatcher.BeginInvoke(() => OnSessionClosed(session));
            session.Start();
            SetState(TerminalState.Connected);
            FocusTerminal();
        }
        catch (SshOperationException ex)
        {
            Status($"✖ {ex.Message}");
            Status("Appuyez sur Entrée pour réessayer.");
            SetState(TerminalState.Closed);
        }
        catch (Exception ex)
        {
            Status($"✖ {ex.Message}");
            SetState(TerminalState.Closed);
        }
        finally
        {
            _connecting = false;
        }
    }

    private void OnSessionClosed(TerminalSession session)
    {
        if (_session != session || _disposed)
            return;
        FlushOutput();
        _session = null;
        session.Dispose();
        Status("Session terminée. Appuyez sur Entrée pour vous reconnecter.");
        SetState(TerminalState.Closed);
    }

    /// <summary>Thread de lecture : les paquets sont regroupés en un message par passage du Dispatcher.</summary>
    private void OnOutput(byte[] data)
    {
        lock (_pending)
        {
            _pending.Write(data);
            if (_flushScheduled)
                return;
            _flushScheduled = true;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, FlushOutput);
    }

    private void FlushOutput()
    {
        byte[] data;
        lock (_pending)
        {
            _flushScheduled = false;
            if (_pending.Length == 0)
                return;
            data = _pending.ToArray();
            _pending.SetLength(0);
        }
        Post(new { t = "out", b = Convert.ToBase64String(data) });
    }

    private void SetState(TerminalState state)
    {
        State = state;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var session = _session;
        _session = null;
        if (session is not null)
            _ = Task.Run(session.Dispose);
        _web.Dispose();
    }
}
