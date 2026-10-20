// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Windows;
using System.Windows.Threading;
using VibeSSHMonitoring.UI;

namespace VibeSSHMonitoring;

public partial class App : Application
{
    private const string InstanceName = "VibeSSHMonitoring_SingleInstance";
    private static Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Instance unique : un second lancement réaffiche simplement la fenêtre existante
        _instanceMutex = new Mutex(true, InstanceName, out bool isNew);
        if (!isNew)
        {
            if (EventWaitHandle.TryOpenExisting(InstanceName + "_Show", out var signal))
            {
                signal.Set();
                signal.Dispose();
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        _controller = new AppController();
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "_Show");
        var thread = new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(() => _controller?.ShowWindow());
        }) { IsBackground = true };
        thread.Start();

        bool minimized = _controller.Config.StartMinimized || e.Args.Contains("--minimized");
        _controller.Start(showWindow: !minimized);
    }

    /// <summary>Une erreur inattendue ne doit pas fermer l'application (et ses terminaux).</summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Dialogs.Info(Dialogs.ActiveWindow(), "Erreur inattendue", e.Exception.Message, error: true,
            detail: e.Exception.ToString());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        if (_controller is not null)
            _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
