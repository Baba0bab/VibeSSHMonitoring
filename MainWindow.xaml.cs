// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VibeSSHMonitoring.Core;
using VibeSSHMonitoring.UI;

namespace VibeSSHMonitoring;

/// <summary>Onglet du terminal intégré.</summary>
public sealed class TerminalTab : ObservableObject
{
    public TerminalView View { get; }
    public string Title { get; }

    public TerminalTab(TerminalView view)
    {
        View = view;
        Title = view.Server.DisplayName;
        view.StateChanged += () => OnPropertyChanged(nameof(StateBrush));
    }

    public Brush StateBrush => (Brush)Application.Current.FindResource(View.State switch
    {
        TerminalView.TerminalState.Connected => "SuccessBrush",
        TerminalView.TerminalState.Connecting => "WarningBrush",
        _ => "NeutralBrush"
    });
}

public partial class MainWindow : Window
{
    private const double NavExpandedWidth = 240;
    private const double NavCompactWidth = 56;
    private const double NavAutoCompactBelow = 900;
    private const double MinCardWidth = 340;

    public static readonly DependencyProperty CardWidthProperty =
        DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(360.0));

    /// <summary>Largeur des cartes : la grille remplit la page avec autant de colonnes que possible.</summary>
    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    private readonly AppController _app;
    private readonly RadioButton[] _navItems;
    private readonly ICollectionView _serversView;
    private readonly System.Collections.ObjectModel.ObservableCollection<TerminalTab> _terminals = new();
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private ServerViewModel? _detail;
    private string? _tagFilter;
    private bool _updatingUi = true;
    private bool _navCompact;
    private bool _wasNarrow;
    private int _page;

    public bool HasTerminals => _terminals.Count > 0;
    public int TerminalCount => _terminals.Count;

    public MainWindow(AppController app)
    {
        _app = app;
        InitializeComponent();

        _navItems = new[] { NavServers, NavTerminals, NavSettings };
        foreach (var page in new FrameworkElement[] { ServersPage, DetailPage, SettingsPage })
            page.RenderTransform = new TranslateTransform();

        _serversView = CollectionViewSource.GetDefaultView(_app.Servers.Servers);
        _serversView.Filter = FilterServer;
        ServersList.ItemsSource = _serversView;
        _app.Servers.Servers.CollectionChanged += OnServersChanged;
        foreach (var vm in _app.Servers.Servers)
            vm.PropertyChanged += OnServerPropertyChanged;

        TerminalTabs.ItemsSource = _terminals;
        _terminals.CollectionChanged += (_, _) => SyncTerminalCount();
        _toastTimer.Tick += (_, _) => HideToast();

        AboutCard.Description = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)} · supervision de serveurs Debian / Ubuntu en SSH · licence GPL-3.0";
        InitializeSettings();
        RebuildTagFilters();
        SyncEmptyState();
        SyncSelection();
        SyncSummary();
        SyncVault();
        SyncTerminalCount();
        SyncAppUpdate();

        NavServers.IsChecked = true;
        ShowPage(0, animate: false);
        _updatingUi = false;

        SourceInitialized += (_, _) => DialogWindow.ApplyDarkFrame(this);
        SizeChanged += OnSizeChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) => DisposeTerminals();
    }

    // ===== Navigation =====

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
            return;
        int index = Array.IndexOf(_navItems, sender);
        if (index >= 0)
            ShowPage(index);
    }

    private void NavigateTo(int index)
    {
        if (_navItems[index].IsChecked == true)
            ShowPage(index);
        else
            _navItems[index].IsChecked = true;
    }

    /// <summary>0 = serveurs (ou leur détail), 1 = terminaux, 2 = paramètres.</summary>
    private void ShowPage(int index, bool animate = true, bool detail = false)
    {
        _page = index;
        bool terminals = index == 1;
        PageScroller.Visibility = terminals ? Visibility.Collapsed : Visibility.Visible;
        TerminalsPage.Visibility = terminals ? Visibility.Visible : Visibility.Collapsed;
        if (!detail && index == 0)
            SetDetail(null);

        FrameworkElement? page = index switch
        {
            0 => _detail is not null ? DetailPage : ServersPage,
            2 => SettingsPage,
            _ => null
        };
        ServersPage.Visibility = page == ServersPage ? Visibility.Visible : Visibility.Collapsed;
        DetailPage.Visibility = page == DetailPage ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == SettingsPage ? Visibility.Visible : Visibility.Collapsed;
        PageScroller.ScrollToTop();

        if (terminals)
        {
            (TerminalTabs.SelectedItem as TerminalTab)?.View.FocusTerminal();
            return;
        }
        if (!animate || page is null)
            return;
        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        page.RenderTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, duration) { EasingFunction = ease });
    }

    private void NavToggleButton_Click(object sender, RoutedEventArgs e) => SetNavCompact(!_navCompact);

    private void SetNavCompact(bool compact)
    {
        _navCompact = compact;
        double width = compact ? NavCompactWidth : NavExpandedWidth;
        var animation = new DoubleAnimation(width, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) =>
        {
            if (_navCompact != compact)
                return;
            Sidebar.BeginAnimation(WidthProperty, null);
            Sidebar.Width = width;
        };
        Sidebar.BeginAnimation(WidthProperty, animation);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = ActualWidth < NavAutoCompactBelow;
        if (narrow != _wasNarrow)
        {
            _wasNarrow = narrow;
            SetNavCompact(narrow);
            PageHost.Margin = narrow ? new Thickness(20, 20, 20, 24) : new Thickness(36, 28, 36, 36);
        }
        DetailInfoGrid.Columns = ActualWidth < 980 ? 2 : 3;
    }

    private void PageScroller_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCardWidth();

    /// <summary>Largeur des cartes calculée sur la zone visible (hors barre de défilement).</summary>
    private void UpdateCardWidth()
    {
        double viewport = PageScroller.ViewportWidth > 0 ? PageScroller.ViewportWidth : PageScroller.ActualWidth - 12;
        double available = Math.Min(PageHost.MaxWidth, viewport - PageHost.Margin.Left - PageHost.Margin.Right)
                           + 12;   // 12 = marge droite de chaque carte, compensée par la marge négative de la liste
        if (available <= 0)
            return;
        int columns = Math.Max(1, (int)(available / (MinCardWidth + 12)));
        CardWidth = Math.Floor(available / columns);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && _page != 1)
        {
            RefreshAll_Click(this, e);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;
        int page = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            _ => -1
        };
        if (page >= 0)
        {
            NavigateTo(page);
            e.Handled = true;
        }
        else if (e.Key == Key.N && _page != 1)
        {
            AddServer_Click(this, e);
            e.Handled = true;
        }
    }

    // ===== Liste des serveurs =====

    private void OnServersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ServerViewModel vm in e.OldItems)
                vm.PropertyChanged -= OnServerPropertyChanged;
        if (e.NewItems is not null)
            foreach (ServerViewModel vm in e.NewItems)
                vm.PropertyChanged += OnServerPropertyChanged;
        if (_detail is not null && !_app.Servers.Servers.Contains(_detail))
            ShowPage(0);
        RebuildTagFilters();
        SyncEmptyState();
        SyncSelection();
    }

    private void OnServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServerViewModel.IsSelected) or "" or null)
            SyncSelection();
        if (sender == _detail && e.PropertyName is nameof(ServerViewModel.State) or nameof(ServerViewModel.Probe) or "" or null)
            SyncDetailExtras();
    }

    private bool FilterServer(object item)
    {
        if (item is not ServerViewModel vm)
            return false;
        if (_tagFilter is not null && !vm.Config.Tags.Contains(_tagFilter, StringComparer.OrdinalIgnoreCase))
            return false;
        string query = SearchBox.Text.Trim();
        if (query.Length == 0)
            return true;
        return vm.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || vm.Config.Host.Contains(query, StringComparison.OrdinalIgnoreCase)
               || vm.Config.Username.Contains(query, StringComparison.OrdinalIgnoreCase)
               || vm.Config.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase))
               || (vm.Probe?.Os?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_updatingUi)
            return;
        _serversView.Refresh();
        SyncEmptyState();
    }

    private void RebuildTagFilters()
    {
        var tags = _app.Servers.Servers.SelectMany(s => s.Config.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (_tagFilter is not null && !tags.Contains(_tagFilter, StringComparer.OrdinalIgnoreCase))
            _tagFilter = null;

        TagFilters.Children.Clear();
        if (tags.Count == 0)
            return;
        AddChip("Tous", null);
        foreach (var tag in tags)
            AddChip(tag, tag);

        void AddChip(string label, string? tag)
        {
            var chip = new ToggleButton
            {
                Style = (Style)FindResource("Chip"),
                Content = label,
                IsChecked = string.Equals(_tagFilter, tag, StringComparison.OrdinalIgnoreCase)
            };
            chip.Click += (_, _) =>
            {
                _tagFilter = tag;
                foreach (var other in TagFilters.Children.OfType<ToggleButton>())
                    other.IsChecked = other == chip;
                _serversView.Refresh();
                SyncEmptyState();
            };
            TagFilters.Children.Add(chip);
        }
    }

    private void SyncEmptyState()
    {
        bool none = _app.Servers.Servers.Count == 0;
        bool filteredOut = !none && _serversView.IsEmpty;
        EmptyState.Visibility = none || filteredOut ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = none ? "Aucun serveur pour l'instant" : "Aucun serveur ne correspond";
        EmptyText.Text = none
            ? "Ajoutez un VPS ou un serveur dédié (Debian / Ubuntu) : adresse, utilisateur et mot de passe ou clé SSH."
            : "Modifiez la recherche ou le groupe sélectionné.";
        EmptyAddButton.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<ServerViewModel> SelectedServers => _app.Servers.Servers.Where(s => s.IsSelected).ToList();

    private void SyncSelection()
    {
        int count = _app.Servers.Servers.Count(s => s.IsSelected);
        SelectionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = count == 1 ? "1 serveur sélectionné" : $"{count} serveurs sélectionnés";
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _serversView.Cast<ServerViewModel>())
            vm.IsSelected = true;
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _app.Servers.Servers)
            vm.IsSelected = false;
    }

    /// <summary>Clic sur la carte (hors boutons et case à cocher) : ouvre le détail.</summary>
    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || IsInside<ButtonBase>(e.OriginalSource as DependencyObject))
            return;
        if (sender is FrameworkElement { DataContext: ServerViewModel vm })
            ShowDetail(vm);
    }

    private static bool IsInside<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T)
                return true;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private static ServerViewModel? ServerOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ServerViewModel;

    // ===== Détail =====

    private void ShowDetail(ServerViewModel vm)
    {
        SetDetail(vm);
        if (_page != 0)
        {
            _updatingUi = true;
            NavServers.IsChecked = true;
            _updatingUi = false;
        }
        ShowPage(0, detail: true);
        LogBox.ScrollToEnd();
    }

    private void SetDetail(ServerViewModel? vm)
    {
        if (_detail == vm)
            return;
        if (_detail is not null)
        {
            _detail.LogAppended -= OnDetailLogAppended;
            _detail.Packages.CollectionChanged -= OnDetailPackagesChanged;
        }
        _detail = vm;
        DetailPage.DataContext = vm;
        if (vm is not null)
        {
            vm.LogAppended += OnDetailLogAppended;
            vm.Packages.CollectionChanged += OnDetailPackagesChanged;
            SyncDetailExtras();
        }
    }

    private void OnDetailLogAppended() => LogBox.ScrollToEnd();

    private void OnDetailPackagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncDetailExtras();

    private void SyncDetailExtras()
    {
        if (_detail is null)
            return;
        NoPackagesText.Visibility = _detail.Packages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_detail.Packages.Count == 0)
            NoPackagesText.Text = _detail.NoApt
                ? "apt-get est introuvable : ce serveur n'est pas une Debian / Ubuntu."
                : _detail.UpdateCount is null
                    ? "Liste inconnue pour l'instant : « Check updates » interroge les dépôts."
                    : "Aucune mise à jour en attente. « Check updates » rafraîchit la liste depuis les dépôts.";
        ApproveKeyButton.Visibility = _detail.State is ServerState.HostKeyUnknown or ServerState.HostKeyChanged
            ? Visibility.Visible : Visibility.Collapsed;
        UnlockButton.Visibility = _detail.State == ServerState.Locked ? Visibility.Visible : Visibility.Collapsed;
        FixButton.Visibility = _detail.State == ServerState.AuthFailed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BackToServers_Click(object sender, RoutedEventArgs e) => ShowPage(0);

    private void DetailRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (ServerOf(sender) is { } vm)
            _ = _app.Servers.RefreshNowAsync(vm);
    }

    private void ApproveKey_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is not null)
            _ = _app.Servers.RefreshNowAsync(_detail);   // la connexion propose l'empreinte à approuver
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e) => await _app.UnlockInteractiveAsync();

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _detail?.ClearLog();

    // ===== Actions =====

    private void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (ServerOf(sender) is { } vm)
            _ = _app.Servers.CheckUpdatesAsync(vm);
    }

    private async void Upgrade_Click(object sender, RoutedEventArgs e)
    {
        if (ServerOf(sender) is not { } vm)
            return;
        string verb = vm.Settings.Upgrade == UpgradeKind.FullUpgrade ? "full-upgrade" : "upgrade";
        string packages = vm.UpdateCount is > 0
            ? $"{vm.UpdatesText}{(vm.SecurityCount > 0 ? $" ({vm.UpdatesDetail})" : "")} connue(s) à ce jour."
            : "La liste des paquets sera rafraîchie juste avant.";
        if (!Dialogs.Confirm(this, $"Mettre à jour {vm.Name} ?",
                $"apt-get update puis apt-get {verb}, sans interaction (les fichiers de configuration modifiés sont conservés). {packages}",
                "Mettre à jour"))
            return;
        ShowDetail(vm);   // pour suivre le journal en direct
        await _app.Servers.UpgradeAsync(vm);
    }

    private void OpenSsh_Click(object sender, RoutedEventArgs e)
    {
        if (ServerOf(sender) is { } vm)
            _ = _app.Servers.OpenSshAsync(vm);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ServerOf(sender) is not { } vm)
            return;
        var menu = (ContextMenu)FindResource("ServerMenu");
        menu.DataContext = vm;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void MenuDetails_Click(object sender, RoutedEventArgs e)
    {
        if (ServerOf(sender) is { } vm)
            ShowDetail(vm);
    }

    private void MenuRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (ServerOf(sender) is { } vm)
            _ = _app.Servers.RefreshNowAsync(vm);
    }

    private async void MenuEdit_Click(object sender, RoutedEventArgs e)
    {
        if ((ServerOf(sender) ?? _detail) is { } vm)
            await EditServerAsync(vm, duplicate: false);
    }

    private async void MenuDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (ServerOf(sender) is { } vm)
            await EditServerAsync(vm, duplicate: true);
    }

    private async void MenuReboot_Click(object sender, RoutedEventArgs e)
    {
        if (ServerOf(sender) is not { } vm)
            return;
        if (Dialogs.Confirm(this, $"Redémarrer {vm.Name} ?",
                "Le serveur sera indisponible pendant quelques instants. Les sessions ouvertes dessus seront coupées.",
                "Redémarrer", danger: true))
            await _app.Servers.RebootAsync(vm);
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ServerOf(sender) is not { } vm)
            return;
        if (Dialogs.Confirm(this, $"Supprimer {vm.Name} ?",
                "Le serveur et ses identifiants enregistrés seront retirés de l'application. Rien n'est modifié sur le serveur.",
                "Supprimer", danger: true))
            _app.Servers.Remove(vm);
    }

    private void RefreshAll_Click(object sender, RoutedEventArgs e) =>
        _ = _app.Servers.RunForAllAsync(_app.Servers.Servers, vm => _app.Servers.RefreshAsync(vm));

    private void BulkRefresh_Click(object sender, RoutedEventArgs e) =>
        _ = _app.Servers.RunForAllAsync(SelectedServers, vm => _app.Servers.RefreshNowAsync(vm));

    private void BulkCheck_Click(object sender, RoutedEventArgs e) =>
        _ = _app.Servers.RunForAllAsync(SelectedServers, vm => _app.Servers.CheckUpdatesAsync(vm));

    private void BulkUpgrade_Click(object sender, RoutedEventArgs e)
    {
        var servers = SelectedServers;
        if (Dialogs.Confirm(this, $"Mettre à jour {servers.Count} serveur(s) ?",
                "apt-get update puis upgrade sur chaque serveur, quatre à la fois. Suivez la progression sur les cartes, et le détail dans le journal de chaque serveur.",
                "Tout mettre à jour", detail: string.Join("\n", servers.Select(s => $"• {s.Name} — {s.UpdatesText}"))))
            _ = _app.Servers.RunForAllAsync(servers, vm => _app.Servers.UpgradeAsync(vm));
    }

    private void BulkReboot_Click(object sender, RoutedEventArgs e)
    {
        var servers = SelectedServers;
        if (Dialogs.Confirm(this, $"Redémarrer {servers.Count} serveur(s) ?",
                "Chaque serveur sera indisponible quelques instants.", "Tout redémarrer", danger: true,
                detail: string.Join("\n", servers.Select(s => $"• {s.Name}"))))
            _ = _app.Servers.RunForAllAsync(servers, vm => _app.Servers.RebootAsync(vm));
    }

    // ===== Ajout / modification =====

    private async void AddServer_Click(object sender, RoutedEventArgs e) => await EditServerAsync(null, duplicate: false);

    private async Task EditServerAsync(ServerViewModel? vm, bool duplicate)
    {
        if (_app.Vault.IsLocked && !await _app.UnlockInteractiveAsync())
            return;

        bool isNew = vm is null || duplicate;
        var config = vm?.Config.Clone() ?? new ServerConfig();
        var secrets = vm is not null ? _app.Vault.Get(vm.Id) : new ServerSecrets();
        if (duplicate)
        {
            config.Id = Guid.NewGuid();
            config.Name = $"{config.DisplayName} (copie)";
            config.HostKeyFingerprint = null;
            config.HostKeyType = null;
            config.LastUpdateCheckUtc = null;
        }

        var editor = new ServerEditWindow(_app, config, secrets, isNew) { Owner = this };
        if (editor.ShowDialog() != true)
            return;
        if (isNew)
            ShowDetailIfSingle(_app.Servers.Add(editor.Result, editor.ResultSecrets));
        else
            _app.Servers.Update(vm!, editor.Result, editor.ResultSecrets);
        RebuildTagFilters();
        _serversView.Refresh();
        SyncEmptyState();
    }

    private void ShowDetailIfSingle(ServerViewModel vm)
    {
        if (_app.Servers.Servers.Count == 1)
            ShowDetail(vm);
    }

    // ===== Terminaux intégrés =====

    public void OpenTerminal(ServerConfig server)
    {
        var view = new TerminalView(_app.Servers, server);
        var tab = new TerminalTab(view);
        _terminals.Add(tab);
        TerminalHost.Children.Add(view);
        TerminalTabs.SelectedItem = tab;
        NavigateTo(1);
    }

    private void TerminalTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = TerminalTabs.SelectedItem as TerminalTab;
        foreach (var tab in _terminals)
            tab.View.Visibility = tab == selected ? Visibility.Visible : Visibility.Collapsed;
        if (_page == 1)
            selected?.View.FocusTerminal();
    }

    private void CloseTerminal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TerminalTab tab)
            return;
        int index = _terminals.IndexOf(tab);
        _terminals.Remove(tab);
        TerminalHost.Children.Remove(tab.View);
        tab.View.Dispose();
        if (_terminals.Count > 0)
            TerminalTabs.SelectedIndex = Math.Min(index, _terminals.Count - 1);
    }

    private void NewTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (_app.Servers.Servers.Count == 0)
        {
            Dialogs.Info(this, "Aucun serveur", "Ajoutez d'abord un serveur dans la page Serveurs.");
            return;
        }
        var menu = new ContextMenu();
        foreach (var vm in _app.Servers.Servers.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new MenuItem { Header = $"{vm.Name}   ({vm.Endpoint})", Tag = "" };
            item.Click += (_, _) => OpenTerminal(vm.Config);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SyncTerminalCount()
    {
        TerminalCountBadge.Visibility = _terminals.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TerminalCountText.Text = _terminals.Count.ToString();
        NoTerminalState.Visibility = _terminals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NewTerminalButton.Visibility = _terminals.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DisposeTerminals()
    {
        foreach (var tab in _terminals)
            tab.View.Dispose();
        _terminals.Clear();
    }

    // ===== Résumé, coffre =====

    internal void SyncSummary()
    {
        var servers = _app.Servers.Servers;
        SidebarSummaryText.Text = _app.SummaryText;
        int problems = servers.Count(s => s.HasProblem);
        int offline = servers.Count(s => s.State == ServerState.Offline);
        SidebarStatusDot.Fill = (Brush)FindResource(servers.Count == 0 ? "NeutralBrush" :
            problems == 0 ? "SuccessBrush" : problems == servers.Count ? "DangerBrush" : "WarningBrush");

        int updates = servers.Sum(s => s.UpdateCount ?? 0);
        int reboot = servers.Count(s => s.RebootRequired);
        var parts = new List<string> { servers.Count == 1 ? "1 serveur" : $"{servers.Count} serveurs" };
        if (servers.Count > 0)
            parts.Add($"{servers.Count(s => s.IsOnline)} en ligne");
        if (offline > 0)
            parts.Add($"{offline} hors ligne");
        if (problems - offline > 0)
            parts.Add($"{problems - offline} en erreur");
        if (updates > 0)
            parts.Add($"{updates} mise{(updates > 1 ? "s" : "")} à jour en attente");
        if (reboot > 0)
            parts.Add($"{reboot} à redémarrer");
        ServersSubtitle.Text = string.Join(" · ", parts);
    }

    internal void SyncVault()
    {
        var vault = _app.Vault;
        bool master = vault.Mode == StorageMode.MasterPassword;
        SidebarVaultIcon.Text = vault.IsLocked ? "" : "";
        SidebarVaultIcon.Foreground = (Brush)FindResource(vault.IsLocked ? "WarningBrush" : "TextSecondaryBrush");
        SidebarVaultText.Text = vault.Mode switch
        {
            StorageMode.MasterPassword => vault.IsLocked ? "Coffre verrouillé" : "Coffre déverrouillé",
            StorageMode.PlainText => "Mots de passe en clair",
            _ => "Coffre Windows"
        };
        SidebarVaultHost.ToolTip = vault.Mode switch
        {
            StorageMode.MasterPassword => "Mots de passe chiffrés par le mot de passe maître (Argon2id + AES-256-GCM)",
            StorageMode.PlainText => "Mots de passe stockés en clair dans le fichier secrets.json",
            _ => "Mots de passe chiffrés par Windows (DPAPI), liés à votre session"
        };
        SidebarLockButton.Visibility = master ? Visibility.Visible : Visibility.Collapsed;
        SidebarLockButton.Content = vault.IsLocked ? "" : "";
        SidebarLockButton.ToolTip = vault.IsLocked ? "Déverrouiller le coffre" : "Verrouiller le coffre";

        MasterPasswordCard.Visibility = master ? Visibility.Visible : Visibility.Collapsed;
        AutoLockCard.Visibility = master ? Visibility.Visible : Visibility.Collapsed;
        MasterPasswordCard.IsEnabled = !vault.IsLocked;
        StorageCard.Description = vault.Mode switch
        {
            StorageMode.MasterPassword => "Chiffrés avec votre mot de passe maître (Argon2id + AES-256-GCM), demandé au démarrage.",
            StorageMode.PlainText => "⚠ Lisibles par toute personne ou tout programme ayant accès à votre dossier %APPDATA%.",
            _ => "Chiffrés par Windows et liés à votre session : rien à saisir, illisibles depuis un autre compte ou un autre PC."
        };
        _updatingUi = true;
        CheckSegment(StorageSegments, vault.Mode.ToString());
        _updatingUi = false;
    }

    private async void SidebarLockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_app.Vault.IsLocked)
            await _app.UnlockInteractiveAsync();
        else
            _app.Vault.Lock();
    }

    public void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
    }

    // ===== Paramètres =====

    private void InitializeSettings()
    {
        var config = _app.Config;
        var d = config.Defaults;

        BindSegments(StorageSegments, _app.Vault.Mode.ToString(), tag => _ = ChangeStorageModeAsync(Enum.Parse<StorageMode>(tag)));
        BindSegments(AutoLockSegments, config.AutoLockMinutes.ToString(), tag =>
        {
            config.AutoLockMinutes = int.Parse(tag);
            config.Save();
        });

        foreach (var (mode, label) in TerminalChoices())
            TerminalCombo.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
        TerminalCombo.SelectedItem = TerminalCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (TerminalMode)i.Tag == d.Terminal);
        TerminalCombo.SelectionChanged += (_, _) =>
        {
            if (_updatingUi || TerminalCombo.SelectedItem is not ComboBoxItem { Tag: TerminalMode mode })
                return;
            d.Terminal = mode;
            SaveDefaults();
            CustomTerminalCard.Visibility = mode == TerminalMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        };
        CustomTerminalCard.Visibility = d.Terminal == TerminalMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        CustomTerminalBox.Text = config.CustomTerminalCommand;

        BindSwitch(CopyPasswordSwitch, config.CopyPasswordForExternal, v => { config.CopyPasswordForExternal = v; config.Save(); });
        BindSwitch(RootShellSwitch, d.RootShell == true, v => { d.RootShell = v; SaveDefaults(); });
        BindSwitch(ShowOnlineSwitch, d.ShowOnline == true, v => { d.ShowOnline = v; SaveDefaults(); });
        BindSwitch(ShowResourcesSwitch, d.ShowResources == true, v => { d.ShowResources = v; SaveDefaults(); });
        BindSwitch(ShowSystemSwitch, d.ShowSystem == true, v => { d.ShowSystem = v; SaveDefaults(); });
        BindSwitch(ShowUpdatesSwitch, d.ShowUpdates == true, v => { d.ShowUpdates = v; SaveDefaults(); });
        BindSegments(RefreshSegments, d.RefreshSeconds.ToString()!, tag => { d.RefreshSeconds = int.Parse(tag); SaveDefaults(); });
        BindSegments(AutoCheckSegments, d.AutoCheckHours.ToString()!, tag => { d.AutoCheckHours = int.Parse(tag); SaveDefaults(); });
        BindSwitch(NotifyUpdatesSwitch, d.NotifyUpdates == true, v => { d.NotifyUpdates = v; SaveDefaults(); });
        BindSwitch(NotifyOfflineSwitch, d.NotifyOffline == true, v => { d.NotifyOffline = v; SaveDefaults(); });
        BindSegments(UpgradeSegments, d.Upgrade.ToString()!, tag => { d.Upgrade = Enum.Parse<UpgradeKind>(tag); SaveDefaults(); });
        BindSwitch(AutoRemoveSwitch, d.AutoRemove == true, v => { d.AutoRemove = v; SaveDefaults(); });

        BindSwitch(StartupSwitch, StartupManager.IsEnabled(), v => StartupManager.SetEnabled(v, config.StartMinimized));
        BindSwitch(MinimizedSwitch, config.StartMinimized, v =>
        {
            config.StartMinimized = v;
            config.Save();
            if (StartupManager.IsEnabled())
                StartupManager.SetEnabled(true, v);
        });
        BindSwitch(CloseToTraySwitch, config.CloseToTray, v => { config.CloseToTray = v; config.Save(); });

        BindSwitch(AppUpdateCheckSwitch, config.AppUpdateCheck, v => { config.AppUpdateCheck = v; config.Save(); });
        BindSwitch(AppUpdateAutoInstallSwitch, config.AppUpdateAutoInstall, v =>
        {
            config.AppUpdateAutoInstall = v;
            config.Save();
            SyncAppUpdate();
        });
        BindSwitch(AppUpdatePrereleaseSwitch, config.AppUpdatePrerelease, v => _app.SetAppUpdatePrerelease(v));
    }

    // ===== Mises à jour de l'application =====

    internal void SyncAppUpdate()
    {
        var updater = _app.Updater;
        string? version = updater.AvailableVersion;
        AppUpdateCard.Header = $"Vibe SSH Monitoring {updater.CurrentVersion}";
        AppUpdateCard.Description = updater.State switch
        {
            AppUpdateState.Unavailable =>
                "Mises à jour automatiques indisponibles : cette copie n'a pas été installée avec l'installateur (version de développement).",
            AppUpdateState.Checking => "Recherche d'une nouvelle version…",
            AppUpdateState.Downloading => $"Téléchargement de la version {version}… {updater.Progress} %",
            AppUpdateState.Ready => _app.Config.AppUpdateAutoInstall
                ? $"Version {version} téléchargée : elle sera installée à la fermeture de l'application."
                : $"Version {version} téléchargée, prête à être installée.",
            AppUpdateState.UpToDate => $"Vous avez la dernière version · vérifié {Format.Ago(updater.LastCheckUtc ?? DateTime.UtcNow)}",
            AppUpdateState.Error => $"Recherche impossible : {updater.Error}",
            _ => updater.IsPortable ? "Version portable · pas encore vérifié." : "Pas encore vérifié."
        };

        bool installed = updater.IsInstalled;
        bool busy = updater.State is AppUpdateState.Checking or AppUpdateState.Downloading;
        bool ready = updater.State == AppUpdateState.Ready;
        AppUpdateAutoCheckCard.IsEnabled = installed;
        AppUpdateAutoInstallCard.IsEnabled = installed;
        AppUpdatePrereleaseCard.IsEnabled = installed;
        AppUpdateSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AppUpdateCheckButton.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        AppUpdateCheckButton.IsEnabled = installed && !busy;
        AppUpdateInstallButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;

        SidebarUpdateButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        SidebarUpdateButton.Content = $"Mettre à jour ({version})";
        SidebarUpdateButton.ToolTip = $"Vibe SSH Monitoring {version} est prête : redémarrer pour l'installer";

        string notes = PlainNotes(updater.ReleaseNotes);
        AppUpdateNotesCard.Visibility = ready && notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AppUpdateNotesTitle.Text = $"Nouveautés de la version {version}";
        AppUpdateNotesText.Text = notes;
    }

    /// <summary>Notes de version (Markdown) en texte simple, pour l'aperçu dans les paramètres.</summary>
    private static string PlainNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";
        var lines = markdown.Replace("\r", "").Split('\n')
            .Select(l => l.TrimEnd().TrimStart('#', ' ').Replace("**", "").Replace("`", ""))
            // Titre « [1.0.1] - date » (déjà affiché) et liens de référence « [1.0.1]: https://… »
            .Where(l => !l.StartsWith('[') && !l.StartsWith('|') && !l.StartsWith("---"))
            .Select(l => l.StartsWith("- ") ? "•  " + l[2..] : l);
        return string.Join("\n", lines).Trim();
    }

    private void AppUpdateCheck_Click(object sender, RoutedEventArgs e) => _ = _app.Updater.CheckAndDownloadAsync();

    private void AppUpdateInstall_Click(object sender, RoutedEventArgs e) => _app.RestartToUpdate();

    public static IEnumerable<(TerminalMode Mode, string Label)> TerminalChoices()
    {
        yield return (TerminalMode.Embedded, "Intégré (onglets)");
        yield return (TerminalMode.WindowsTerminal, "Windows Terminal");
        yield return (TerminalMode.PowerShell, "PowerShell");
        yield return (TerminalMode.Cmd, "Invite de commandes");
        yield return (TerminalMode.PuTTY, ExternalTerminal.FindPutty() is null ? "PuTTY (non détecté)" : "PuTTY");
        yield return (TerminalMode.Custom, "Personnalisé…");
    }

    private void SaveDefaults()
    {
        _app.Config.Save();
        _app.Servers.ApplySettings();
    }

    private void BindSegments(Panel panel, string current, Action<string> onChange)
    {
        foreach (var radio in panel.Children.OfType<RadioButton>())
        {
            radio.GroupName = panel.Name;
            radio.IsChecked = (string)radio.Tag == current;
            radio.Checked += (s, _) =>
            {
                if (!_updatingUi && s is RadioButton { Tag: string tag })
                    onChange(tag);
            };
        }
    }

    private void CheckSegment(Panel panel, string tag)
    {
        foreach (var radio in panel.Children.OfType<RadioButton>())
            radio.IsChecked = (string)radio.Tag == tag;
    }

    private void BindSwitch(CheckBox box, bool value, Action<bool> onChange)
    {
        box.IsChecked = value;
        box.Checked += (_, _) => { if (!_updatingUi) onChange(true); };
        box.Unchecked += (_, _) => { if (!_updatingUi) onChange(false); };
    }

    private void CustomTerminalBox_LostFocus(object sender, RoutedEventArgs e)
    {
        string value = CustomTerminalBox.Text.Trim();
        if (value.Length == 0 || value == _app.Config.CustomTerminalCommand)
            return;
        _app.Config.CustomTerminalCommand = value;
        _app.Config.Save();
    }

    private async Task ChangeStorageModeAsync(StorageMode mode)
    {
        var vault = _app.Vault;
        try
        {
            if (mode == vault.Mode)
                return;
            if (vault.IsLocked && !await _app.UnlockInteractiveAsync())
                return;

            switch (mode)
            {
                case StorageMode.MasterPassword:
                    Dialogs.Password(this, "Définir le mot de passe maître",
                        "Il chiffrera les identifiants de vos serveurs et sera demandé à chaque démarrage. Il ne peut pas être récupéré : notez-le en lieu sûr.",
                        confirm: true, async pw =>
                        {
                            await Task.Run(() => vault.ChangeMode(StorageMode.MasterPassword, pw));
                            return null;
                        }, okText: "Chiffrer");
                    break;
                case StorageMode.PlainText:
                    if (Dialogs.Confirm(this, "Stocker les mots de passe en clair ?",
                            "Toute personne ou tout programme ayant accès à votre session pourra les lire dans secrets.json. Le mode Windows (DPAPI) est aussi transparent, sans ce risque.",
                            "Stocker en clair", danger: true))
                        vault.ChangeMode(StorageMode.PlainText);
                    break;
                default:
                    vault.ChangeMode(StorageMode.Dpapi);
                    break;
            }
        }
        catch (Exception ex)
        {
            Dialogs.Info(this, "Changement impossible", ex.Message, error: true);
        }
        finally
        {
            _app.Config.Storage = vault.Mode;
            _app.Config.Save();
            SyncVault();
        }
    }

    private async void ChangeMasterPassword_Click(object sender, RoutedEventArgs e)
    {
        var vault = _app.Vault;
        if (vault.IsLocked && !await _app.UnlockInteractiveAsync())
            return;
        string? changed = Dialogs.Password(this, "Nouveau mot de passe maître", "Le coffre sera rechiffré avec ce mot de passe.",
            confirm: true, async pw =>
            {
                await Task.Run(() => vault.ChangeMode(StorageMode.MasterPassword, pw));
                return null;
            }, okText: "Changer");
        if (changed is not null)
            ShowToast("Mot de passe maître modifié.");
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppConfig.ConfigDirectory);
        Process.Start(new ProcessStartInfo(AppConfig.ConfigDirectory) { UseShellExecute = true })?.Dispose();
    }
}
