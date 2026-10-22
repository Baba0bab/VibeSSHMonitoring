// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using VibeSSHMonitoring.Core;
using VibeSSHMonitoring.UI;

namespace VibeSSHMonitoring;

public partial class App : Application
{
#if DEBUG
    // The demo mode runs beside an installed copy
    private static readonly string InstanceName = "VibeSSHMonitoring_SingleInstance" + (UpgradeDemo.Enabled ? "_Demo" : "");
#else
    private const string InstanceName = "VibeSSHMonitoring_SingleInstance";
#endif

    /// <summary>Argument of a restart (language change): wait for the previous instance to exit.</summary>
    public const string WaitForArgument = "--wait-for";

    private static Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        WaitForPreviousInstance(e.Args);

        // Single instance: a second launch simply shows the existing window again
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

    /// <summary>
    /// After a restart, the previous instance may still be closing: wait for it (15 s at most) so
    /// that this one takes over the single-instance lock instead of handing over to it.
    /// </summary>
    private static void WaitForPreviousInstance(string[] args)
    {
        int index = Array.IndexOf(args, WaitForArgument);
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out int pid))
            return;
        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(TimeSpan.FromSeconds(15));
        }
        catch (ArgumentException)
        {
            // Already gone
        }
    }

    /// <summary>An unexpected error must not close the application (and its terminals).</summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Dialogs.Info(Dialogs.ActiveWindow(), Loc.T("Error.Unexpected"), e.Exception.Message, error: true,
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
