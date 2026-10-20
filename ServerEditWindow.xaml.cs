// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using VibeSSHMonitoring.Core;
using VibeSSHMonitoring.UI;

namespace VibeSSHMonitoring;

/// <summary>Server editor: connection, authentication, root access, groups and server-specific settings.</summary>
public partial class ServerEditWindow : Window
{
    private readonly AppController _app;
    private readonly ServerConfig _config;   // working copy (a host key approved during a test is recorded here)
    private readonly List<(ComboBox Combo, Action<ServerSettings, object?> Apply)> _overrides = new();
    private bool _busy;

    public ServerConfig Result { get; private set; } = null!;
    public ServerSecrets ResultSecrets { get; private set; } = null!;

    public ServerEditWindow(AppController app, ServerConfig config, ServerSecrets secrets, bool isNew)
    {
        _app = app;
        _config = config;
        InitializeComponent();
        SourceInitialized += (_, _) => DialogWindow.ApplyDarkFrame(this);

        Title = isNew ? Loc.T("Servers.Add") : Loc.T("Editor.EditTitle", config.DisplayName);
        TitleText.Text = isNew ? Loc.T("Servers.Add") : config.DisplayName;
        SaveButton.Content = Loc.T(isNew ? "Editor.AddButton" : "Common.Save");

        NameBox.Text = config.Name;
        HostBox.Text = config.Host;
        PortBox.Text = config.Port.ToString();
        UserBox.Text = config.Username;
        PasswordBox.Password = secrets.Password ?? "";
        KeyPathBox.Text = config.KeyPath ?? (SshKeys.HasKeyPair(SshKeys.DefaultKeyPath) ? SshKeys.DefaultKeyPath : "");
        PassphraseBox.Password = secrets.KeyPassphrase ?? "";
        (config.Auth == AuthMethod.PrivateKey ? AuthKeyRadio : AuthPasswordRadio).IsChecked = true;

        SudoSameSwitch.IsChecked = config.SudoSamePassword;
        if (config.Elevation == ElevationMethod.Su)
            RootPasswordBox.Password = secrets.ElevationPassword ?? "";
        else
            SudoPasswordBox.Password = secrets.ElevationPassword ?? "";
        (config.Elevation switch
        {
            ElevationMethod.Sudo => ElevSudoRadio,
            ElevationMethod.Su => ElevSuRadio,
            _ => ElevNoneRadio
        }).IsChecked = true;

        TagsBox.Text = string.Join(", ", config.Tags);
        SyncHostKey();
        BuildOverrides(config.Overrides);
        SyncAuth();
        SyncElevation();

        Loaded += (_, _) => (isNew ? HostBox : NameBox).Focus();
    }

    // ===== Authentication, elevation =====

    private void Auth_Checked(object sender, RoutedEventArgs e) => SyncAuth();

    private void SyncAuth()
    {
        if (PasswordPanel is null)
            return;
        bool key = AuthKeyRadio.IsChecked == true;
        PasswordPanel.Visibility = key ? Visibility.Collapsed : Visibility.Visible;
        KeyPanel.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
        SyncElevation();
    }

    private void UserBox_TextChanged(object sender, TextChangedEventArgs e) => SyncElevation();

    private void Elevation_Checked(object sender, RoutedEventArgs e) => SyncElevation();

    private void SudoSame_Changed(object sender, RoutedEventArgs e) => SyncElevation();

    private void SyncElevation()
    {
        if (ElevationHint is null || SudoPanel is null)
            return;
        bool root = UserBox.Text.Trim() == "root";
        ElevationHost.IsEnabled = !root;
        bool sudo = !root && ElevSudoRadio.IsChecked == true;
        bool su = !root && ElevSuRadio.IsChecked == true;
        SudoPanel.Visibility = sudo ? Visibility.Visible : Visibility.Collapsed;
        SuPanel.Visibility = su ? Visibility.Visible : Visibility.Collapsed;
        // With a key, there is no login password to reuse for sudo
        bool keyAuth = AuthKeyRadio.IsChecked == true;
        SudoPasswordPanel.Visibility = sudo && (SudoSameSwitch.IsChecked != true || keyAuth) ? Visibility.Visible : Visibility.Collapsed;

        ElevationHint.Text = Loc.T(root ? "Editor.HintRoot"
            : sudo ? "Editor.HintSudo"
            : su ? "Editor.HintSu"
            : "Editor.HintNone");
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        string ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Editor.BrowseTitle"),
            InitialDirectory = Directory.Exists(ssh) ? ssh : null,
            Filter = Loc.T("Editor.BrowseFilter")
        };
        if (dialog.ShowDialog(this) == true)
            KeyPathBox.Text = dialog.FileName;
    }

    // ===== Host key =====

    private void SyncHostKey()
    {
        HostKeySection.Visibility = _config.HostKeyFingerprint is null ? Visibility.Collapsed : Visibility.Visible;
        HostKeyCard.Header = _config.HostKeyType ?? Loc.T("Editor.HostKeyFallback");
        HostKeyCard.Description = $"SHA256:{_config.HostKeyFingerprint}";
    }

    private void ForgetHostKey_Click(object sender, RoutedEventArgs e)
    {
        _config.HostKeyFingerprint = null;
        _config.HostKeyType = null;
        SyncHostKey();
    }

    // ===== Server-specific settings =====

    private void BuildOverrides(ServerSettings current)
    {
        var d = _app.Config.Defaults;
        var onOff = new (string, object)[] { (Loc.T("Common.On"), true), (Loc.T("Common.Off"), false) };

        AddBool("", Loc.T("Settings.ShowOnline"), d.ShowOnline, current.ShowOnline, (o, v) => o.ShowOnline = (bool?)v);
        AddBool("", Loc.T("Editor.OverrideResources"), d.ShowResources, current.ShowResources, (o, v) => o.ShowResources = (bool?)v);
        AddBool("", Loc.T("Settings.ShowSystem"), d.ShowSystem, current.ShowSystem, (o, v) => o.ShowSystem = (bool?)v);
        AddBool("", Loc.T("Settings.ShowUpdates"), d.ShowUpdates, current.ShowUpdates, (o, v) => o.ShowUpdates = (bool?)v);
        Add("", Loc.T("Settings.RefreshInterval"),
            new (string, object)[] { ("30 s", 30), ("1 min", 60), ("5 min", 300), ("15 min", 900), ("1 h", 3600) },
            d.RefreshSeconds, current.RefreshSeconds, (o, v) => o.RefreshSeconds = (int?)v);
        Add("", Loc.T("Settings.AutoCheck"),
            new (string, object)[] { (Loc.T("Common.Never"), 0), (Loc.T("Editor.EveryHours", 6), 6), (Loc.T("Editor.EveryHours", 12), 12), (Loc.T("Editor.EveryHours", 24), 24) },
            d.AutoCheckHours, current.AutoCheckHours, (o, v) => o.AutoCheckHours = (int?)v);
        AddBool("", Loc.T("Settings.NotifyUpdates"), d.NotifyUpdates, current.NotifyUpdates, (o, v) => o.NotifyUpdates = (bool?)v);
        AddBool("", Loc.T("Editor.OverrideNotifyOffline"), d.NotifyOffline, current.NotifyOffline, (o, v) => o.NotifyOffline = (bool?)v);
        Add("", Loc.T("Settings.Terminal"),
            MainWindow.TerminalChoices().Select(c => (c.Label, (object)c.Mode)).ToArray(),
            d.Terminal, current.Terminal, (o, v) => o.Terminal = (TerminalMode?)v);
        AddBool("", Loc.T("Editor.OverrideRootShell"), d.RootShell, current.RootShell, (o, v) => o.RootShell = (bool?)v);
        Add("", Loc.T("Settings.UpgradeKind"),
            new (string, object)[] { ("upgrade", UpgradeKind.Upgrade), ("full-upgrade", UpgradeKind.FullUpgrade) },
            d.Upgrade, current.Upgrade, (o, v) => o.Upgrade = (UpgradeKind?)v);
        AddBool("", Loc.T("Editor.OverrideAutoRemove"), d.AutoRemove, current.AutoRemove, (o, v) => o.AutoRemove = (bool?)v);

        void AddBool(string icon, string header, bool? global, bool? value, Action<ServerSettings, object?> apply) =>
            Add(icon, header, onOff, global, value, apply);

        void Add(string icon, string header, (string Label, object Value)[] options, object? global, object? value,
            Action<ServerSettings, object?> apply)
        {
            var combo = new ComboBox { Width = 210 };
            string globalLabel = options.FirstOrDefault(o => Equals(o.Value, global)).Label ?? global?.ToString() ?? "—";
            combo.Items.Add(new ComboBoxItem { Content = Loc.T("Editor.Global", globalLabel), Tag = null });
            foreach (var (label, optionValue) in options)
                combo.Items.Add(new ComboBoxItem { Content = label, Tag = optionValue });
            if (value is not null && options.All(o => !Equals(o.Value, value)))
                combo.Items.Add(new ComboBoxItem { Content = value.ToString(), Tag = value });   // value typed by hand in the JSON file
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value)) ?? combo.Items[0];

            var card = new SettingCard { Icon = icon, Header = header, Content = combo, MinHeight = 56 };
            combo.SelectionChanged += (_, _) => card.Background = (Brush)FindResource(
                (combo.SelectedItem as ComboBoxItem)?.Tag is null ? "CardBrush" : "CardSelectedBrush");
            card.Background = (Brush)FindResource(value is null ? "CardBrush" : "CardSelectedBrush");
            OverridesPanel.Children.Add(card);
            _overrides.Add((combo, apply));
        }
    }

    // ===== Reading the form =====

    /// <summary>Configuration and secrets entered; null (with the faulty field flagged) when invalid.</summary>
    private (ServerConfig Config, ServerSecrets Secrets)? ReadForm()
    {
        foreach (var box in new[] { HostBox, PortBox, UserBox, KeyPathBox })
            box.Tag = null;

        string host = HostBox.Text.Trim();
        if (host.Length == 0 || host.Contains(' '))
            return Invalid(HostBox, Loc.T("Editor.InvalidHost"));
        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port is < 1 or > 65535)
            return Invalid(PortBox, Loc.T("Editor.InvalidPort"));
        string user = UserBox.Text.Trim();
        if (user.Length == 0 || user.Contains(' '))
            return Invalid(UserBox, Loc.T("Editor.InvalidUser"));

        bool keyAuth = AuthKeyRadio.IsChecked == true;
        string keyPath = KeyPathBox.Text.Trim().Trim('"');
        if (keyAuth && !File.Exists(SshConnector.ExpandPath(keyPath)))
            return Invalid(KeyPathBox, Loc.T("Editor.KeyFileMissing"));

        var config = _config.Clone();
        config.Name = NameBox.Text.Trim();
        config.Host = host;
        config.Port = port;
        config.Username = user;
        config.Auth = keyAuth ? AuthMethod.PrivateKey : AuthMethod.Password;
        config.KeyPath = keyPath.Length > 0 ? keyPath : null;
        config.Elevation = ElevSudoRadio.IsChecked == true ? ElevationMethod.Sudo
            : ElevSuRadio.IsChecked == true ? ElevationMethod.Su
            : ElevationMethod.None;
        config.SudoSamePassword = SudoSameSwitch.IsChecked == true && !keyAuth;
        config.Tags = TagsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var overrides = new ServerSettings();
        foreach (var (combo, apply) in _overrides)
            apply(overrides, (combo.SelectedItem as ComboBoxItem)?.Tag);
        config.Overrides = overrides;

        // The login password is kept even with a key: it may be used by sudo
        var secrets = new ServerSecrets
        {
            Password = NullIfEmpty(PasswordBox.Password),
            KeyPassphrase = keyAuth ? NullIfEmpty(PassphraseBox.Password) : null,
            ElevationPassword = config.Elevation switch
            {
                ElevationMethod.Su => NullIfEmpty(RootPasswordBox.Password),
                ElevationMethod.Sudo when !config.SudoSamePassword => NullIfEmpty(SudoPasswordBox.Password),
                _ => null
            }
        };
        if (!keyAuth && secrets.Password is null)
            return Invalid(PasswordBox, Loc.T("Editor.PasswordMissing"));
        return (config, secrets);
    }

    private (ServerConfig, ServerSecrets)? Invalid(Control field, string message)
    {
        field.Tag = "error";
        field.Focus();
        ShowResult(false, message);
        return null;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    // ===== Test, key deployment, save =====

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is not { } form)
            return;
        var (config, secrets) = form;
        await RunBusyAsync(Loc.T("Editor.Connecting"), async () =>
        {
            string message = await _app.Servers.TestConfigurationAsync(config, secrets);
            CopyHostKey(config);
            ShowResult(true, message);
        });
    }

    private async void DeployKey_Click(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is not { } form)
            return;
        var (config, secrets) = form;
        string keyPath = SshKeys.DefaultKeyPath;
        bool exists = SshKeys.HasKeyPair(keyPath);
        if (!Dialogs.Confirm(this, Loc.T("Editor.DeployTitle"),
                Loc.T(exists ? "Editor.DeployExisting" : "Editor.DeployNew", keyPath, config.Username, config.Host),
                Loc.T("Editor.DeployButton")))
            return;

        await RunBusyAsync(Loc.T("Editor.InstallingKey"), async () =>
        {
            string message = await _app.Servers.DeployKeyAsync(config, secrets, keyPath);
            CopyHostKey(config);
            KeyPathBox.Text = keyPath;
            PassphraseBox.Password = "";
            AuthKeyRadio.IsChecked = true;
            if (config.Elevation == ElevationMethod.Sudo && SudoSameSwitch.IsChecked == true)
            {
                // No more login password to reuse: it becomes the sudo password
                SudoSameSwitch.IsChecked = false;
                SudoPasswordBox.Password = secrets.Password ?? "";
            }
            ShowResult(true, message);
        });
    }

    /// <summary>Host key approved during a test: kept in the server settings.</summary>
    private void CopyHostKey(ServerConfig tested)
    {
        _config.HostKeyFingerprint = tested.HostKeyFingerprint;
        _config.HostKeyType = tested.HostKeyType;
        SyncHostKey();
    }

    private async Task RunBusyAsync(string message, Func<Task> action)
    {
        if (_busy)
            return;
        _busy = true;
        TestButton.IsEnabled = false;
        DeployKeyButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        TestResult.Visibility = Visibility.Visible;
        TestSpinner.Visibility = Visibility.Visible;
        TestIcon.Visibility = Visibility.Collapsed;
        TestText.Text = message;
        TestText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowResult(false, ex.Message);
        }
        finally
        {
            _busy = false;
            TestButton.IsEnabled = true;
            DeployKeyButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
            TestSpinner.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowResult(bool success, string message)
    {
        TestResult.Visibility = Visibility.Visible;
        TestSpinner.Visibility = Visibility.Collapsed;
        TestIcon.Visibility = Visibility.Visible;
        TestIcon.Text = success ? "" : "";
        TestIcon.Foreground = (Brush)FindResource(success ? "SuccessBrush" : "DangerBrush");
        TestText.Text = message;
        TestText.Foreground = (Brush)FindResource(success ? "TextBrush" : "DangerBrush");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ReadForm() is not { } form)
            return;
        var (config, secrets) = form;
        Result = config;
        ResultSecrets = secrets;
        DialogResult = true;
    }
}
