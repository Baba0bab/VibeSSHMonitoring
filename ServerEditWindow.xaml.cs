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

/// <summary>Fiche d'un serveur : connexion, authentification, accès root, groupes et réglages propres.</summary>
public partial class ServerEditWindow : Window
{
    private readonly AppController _app;
    private readonly ServerConfig _config;   // copie de travail (la clé d'hôte approuvée pendant un test y est notée)
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

        Title = isNew ? "Ajouter un serveur" : $"Modifier {config.DisplayName}";
        TitleText.Text = isNew ? "Ajouter un serveur" : config.DisplayName;
        SaveButton.Content = isNew ? "Ajouter" : "Enregistrer";

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

    // ===== Authentification, élévation =====

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
        // Avec une clé, il n'y a pas de mot de passe de connexion à réutiliser pour sudo
        bool keyAuth = AuthKeyRadio.IsChecked == true;
        SudoPasswordPanel.Visibility = sudo && (SudoSameSwitch.IsChecked != true || keyAuth) ? Visibility.Visible : Visibility.Collapsed;

        ElevationHint.Text = root
            ? "Connexion directe en root : aucune élévation nécessaire."
            : sudo ? "Les commandes root passent par sudo (mot de passe de l'utilisateur, ou règle NOPASSWD)."
            : su ? "Les commandes root passent par su, avec le mot de passe de root (serveurs qui refusent la connexion directe en root)."
            : "Sans élévation, « Check updates », « Update » et le redémarrage échoueront faute de droits root.";
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        string ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var dialog = new OpenFileDialog
        {
            Title = "Clé privée SSH",
            InitialDirectory = Directory.Exists(ssh) ? ssh : null,
            Filter = "Clés privées|*.*|Clés PuTTY (*.ppk)|*.ppk"
        };
        if (dialog.ShowDialog(this) == true)
            KeyPathBox.Text = dialog.FileName;
    }

    // ===== Clé d'hôte =====

    private void SyncHostKey()
    {
        HostKeySection.Visibility = _config.HostKeyFingerprint is null ? Visibility.Collapsed : Visibility.Visible;
        HostKeyCard.Header = _config.HostKeyType ?? "Clé d'hôte";
        HostKeyCard.Description = $"SHA256:{_config.HostKeyFingerprint}";
    }

    private void ForgetHostKey_Click(object sender, RoutedEventArgs e)
    {
        _config.HostKeyFingerprint = null;
        _config.HostKeyType = null;
        SyncHostKey();
    }

    // ===== Réglages spécifiques =====

    private void BuildOverrides(ServerSettings current)
    {
        var d = _app.Config.Defaults;
        var onOff = new (string, object)[] { ("Activé", true), ("Désactivé", false) };

        AddBool("", "En ligne / hors ligne et latence", d.ShowOnline, current.ShowOnline, (o, v) => o.ShowOnline = (bool?)v);
        AddBool("", "Ressources (CPU, RAM, disque)", d.ShowResources, current.ShowResources, (o, v) => o.ShowResources = (bool?)v);
        AddBool("", "Informations système", d.ShowSystem, current.ShowSystem, (o, v) => o.ShowSystem = (bool?)v);
        AddBool("", "Mises à jour", d.ShowUpdates, current.ShowUpdates, (o, v) => o.ShowUpdates = (bool?)v);
        Add("", "Fréquence des relevés",
            new (string, object)[] { ("30 s", 30), ("1 min", 60), ("5 min", 300), ("15 min", 900), ("1 h", 3600) },
            d.RefreshSeconds, current.RefreshSeconds, (o, v) => o.RefreshSeconds = (int?)v);
        Add("", "Recherche automatique des mises à jour",
            new (string, object)[] { ("Jamais", 0), ("Toutes les 6 h", 6), ("Toutes les 12 h", 12), ("Toutes les 24 h", 24) },
            d.AutoCheckHours, current.AutoCheckHours, (o, v) => o.AutoCheckHours = (int?)v);
        AddBool("", "Notifier les nouvelles mises à jour", d.NotifyUpdates, current.NotifyUpdates, (o, v) => o.NotifyUpdates = (bool?)v);
        AddBool("", "Notifier si injoignable", d.NotifyOffline, current.NotifyOffline, (o, v) => o.NotifyOffline = (bool?)v);
        Add("", "Terminal",
            MainWindow.TerminalChoices().Select(c => (c.Label, (object)c.Mode)).ToArray(),
            d.Terminal, current.Terminal, (o, v) => o.Terminal = (TerminalMode?)v);
        AddBool("", "Terminal directement en root", d.RootShell, current.RootShell, (o, v) => o.RootShell = (bool?)v);
        Add("", "Type de mise à jour",
            new (string, object)[] { ("upgrade", UpgradeKind.Upgrade), ("full-upgrade", UpgradeKind.FullUpgrade) },
            d.Upgrade, current.Upgrade, (o, v) => o.Upgrade = (UpgradeKind?)v);
        AddBool("", "Nettoyer après la mise à jour (autoremove)", d.AutoRemove, current.AutoRemove, (o, v) => o.AutoRemove = (bool?)v);

        void AddBool(string icon, string header, bool? global, bool? value, Action<ServerSettings, object?> apply) =>
            Add(icon, header, onOff, global, value, apply);

        void Add(string icon, string header, (string Label, object Value)[] options, object? global, object? value,
            Action<ServerSettings, object?> apply)
        {
            var combo = new ComboBox { Width = 210 };
            string globalLabel = options.FirstOrDefault(o => Equals(o.Value, global)).Label ?? global?.ToString() ?? "—";
            combo.Items.Add(new ComboBoxItem { Content = $"Global ({globalLabel})", Tag = null });
            foreach (var (label, optionValue) in options)
                combo.Items.Add(new ComboBoxItem { Content = label, Tag = optionValue });
            if (value is not null && options.All(o => !Equals(o.Value, value)))
                combo.Items.Add(new ComboBoxItem { Content = value.ToString(), Tag = value });   // valeur saisie à la main dans le JSON
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value)) ?? combo.Items[0];

            var card = new SettingCard { Icon = icon, Header = header, Content = combo, MinHeight = 56 };
            combo.SelectionChanged += (_, _) => card.Background = (Brush)FindResource(
                (combo.SelectedItem as ComboBoxItem)?.Tag is null ? "CardBrush" : "CardSelectedBrush");
            card.Background = (Brush)FindResource(value is null ? "CardBrush" : "CardSelectedBrush");
            OverridesPanel.Children.Add(card);
            _overrides.Add((combo, apply));
        }
    }

    // ===== Lecture du formulaire =====

    /// <summary>Configuration et secrets saisis ; null (avec le champ fautif signalé) si invalides.</summary>
    private (ServerConfig Config, ServerSecrets Secrets)? ReadForm()
    {
        foreach (var box in new[] { HostBox, PortBox, UserBox, KeyPathBox })
            box.Tag = null;

        string host = HostBox.Text.Trim();
        if (host.Length == 0 || host.Contains(' '))
            return Invalid(HostBox, "Saisissez l'adresse IP ou le nom d'hôte du serveur.");
        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port is < 1 or > 65535)
            return Invalid(PortBox, "Le port doit être compris entre 1 et 65535.");
        string user = UserBox.Text.Trim();
        if (user.Length == 0 || user.Contains(' '))
            return Invalid(UserBox, "Saisissez l'utilisateur SSH.");

        bool keyAuth = AuthKeyRadio.IsChecked == true;
        string keyPath = KeyPathBox.Text.Trim().Trim('"');
        if (keyAuth && !File.Exists(SshConnector.ExpandPath(keyPath)))
            return Invalid(KeyPathBox, "Fichier de clé privée introuvable.");

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

        // Le mot de passe de connexion est gardé même avec une clé : il peut servir à sudo
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
            return Invalid(PasswordBox, "Saisissez le mot de passe (ou choisissez une clé SSH).");
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

    // ===== Test, déploiement de clé, enregistrement =====

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is not { } form)
            return;
        var (config, secrets) = form;
        await RunBusyAsync("Connexion au serveur…", async () =>
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
        if (!Dialogs.Confirm(this, "Déployer ma clé SSH ?",
                (exists ? $"Votre clé {keyPath}.pub" : $"Une nouvelle clé ed25519 ({keyPath}) va être créée, puis sa partie publique")
                + $" sera ajoutée à ~/.ssh/authorized_keys de {config.Username} sur {config.Host}. " +
                "La connexion par clé est ensuite vérifiée, puis la fiche passe en authentification par clé. Le mot de passe reste enregistré pour sudo.",
                "Déployer"))
            return;

        await RunBusyAsync("Installation de la clé…", async () =>
        {
            string message = await _app.Servers.DeployKeyAsync(config, secrets, keyPath);
            CopyHostKey(config);
            KeyPathBox.Text = keyPath;
            PassphraseBox.Password = "";
            AuthKeyRadio.IsChecked = true;
            if (config.Elevation == ElevationMethod.Sudo && SudoSameSwitch.IsChecked == true)
            {
                // Plus de mot de passe de connexion à réutiliser : il devient le mot de passe sudo
                SudoSameSwitch.IsChecked = false;
                SudoPasswordBox.Password = secrets.Password ?? "";
            }
            ShowResult(true, message);
        });
    }

    /// <summary>Clé d'hôte approuvée pendant un test : conservée dans la fiche.</summary>
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
