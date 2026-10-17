using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Porta.Interop;
using Porta.Models;
using Porta.Services;
using Porta.Services.Games;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;
using Launcher = Porta.Services.Launcher;

namespace Porta;

/// <summary>The Start menu itself. Created once at startup and shown or hidden, never closed.</summary>
public sealed partial class MenuWindow : Window
{
    private const int MenuWidth = 760;   // device-independent pixels
    private const int MenuHeight = 640;
    private const int GapAboveTaskbar = 12;

    private readonly IntPtr _hwnd;
    private readonly ObservableCollection<AppEntry> _pinned = [];
    private readonly ObservableCollection<RecentItem> _recent = [];
    private readonly ObservableCollection<AppEntry> _results = [];
    private readonly ObservableCollection<GameEntry> _games = [];
    private readonly ObservableCollection<GameEntry> _store = []; // Steam store results, shown below installed items
    private CancellationTokenSource? _storeSearch;
    private DateTime _pictureStamp;
    private bool _isOpen;

    public MenuWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        SystemBackdrop = new PanelBackdrop();
        ExtendsContentIntoTitleBar = true;

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false; // keep it out of Alt+Tab and the taskbar

        // A hairline border a little lighter than the panel, instead of Windows' accent colour.
        Native.SetRoundCorners(_hwnd);
        ApplyFrameBorder();
        Root.ActualThemeChanged += (_, _) => ApplyFrameBorder();

        AppWindow.Closing += (_, e) => { e.Cancel = true; HideMenu(); }; // Alt+F4 hides instead of quitting
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && _isOpen) HideMenu();
        };

        PinnedGrid.ItemsSource = _pinned;
        RecentList.ItemsSource = _recent;
        ResultsList.ItemsSource = _results;
        GamesList.ItemsSource = _games;
        StoreList.ItemsSource = _store;

        var name = Native.GetDisplayName();
        UserNameText.Text = name;
        Avatar.DisplayName = name;
        _ = LoadAccountAsync();

        App.Catalog.Loaded += OnCatalogLoaded;
        App.Settings.Restored += () => DispatcherQueue.TryEnqueue(() =>
        {
            RebuildPinned();
            if (App.Catalog.IsLoaded) RebuildAllApps();
        });
    }

    private void ApplyFrameBorder()
    {
        if (Root.ActualTheme == ElementTheme.Light) Native.SetBorderColor(_hwnd, 0xD6, 0xD8, 0xE0);
        else Native.SetBorderColor(_hwnd, 0x3A, 0x3D, 0x48);
    }

    /// <summary>Window handle, for file pickers opened from the tray menu.</summary>
    public IntPtr Hwnd => _hwnd;

    // ---------- Showing and hiding ----------

    public void Toggle()
    {
        if (_isOpen) HideMenu(); else ShowMenu();
    }

    public void ShowMenu()
    {
        ResetView();
        RefreshRecent();
        RefreshPictureIfChanged();
        if (App.Catalog.IsLoaded) App.Games.RefreshIfStale(App.Catalog.Apps);
        ClockText.Text = DateTime.Now.ToString("t");
        DateText.Text = DateTime.Now.ToString("dddd, MMMM d");

        PlaceAboveTaskbar();
        AppWindow.Show(true);
        Native.ForceForeground(_hwnd);
        SearchBox.Focus(FocusState.Programmatic);
        _isOpen = true;
    }

    public void HideMenu()
    {
        _isOpen = false;
        AppWindow.Hide();
    }

    /// <summary>Opens centred above the taskbar on whichever screen the pointer is on.</summary>
    private void PlaceAboveTaskbar()
    {
        Native.GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
        double scale = Native.ScaleAtPoint(cursor);

        int width = (int)(MenuWidth * scale);
        int height = (int)(MenuHeight * scale);
        var work = area.WorkArea;
        int x = work.X + (work.Width - width) / 2;
        int y = work.Y + work.Height - height - (int)(GapAboveTaskbar * scale);

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void ResetView()
    {
        SearchBox.Text = "";
        ViewSelector.SelectedItem = PinnedTab;
        ShowPanel(PinnedPanel);
    }

    private void ShowPanel(UIElement panel)
    {
        PinnedPanel.Visibility = panel == PinnedPanel ? Visibility.Visible : Visibility.Collapsed;
        AllAppsList.Visibility = panel == AllAppsList ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = panel == SearchPanel ? Visibility.Visible : Visibility.Collapsed;
        ViewSelector.Visibility = panel == SearchPanel ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- Data ----------

    private void OnCatalogLoaded()
    {
        App.Settings.EnsureStarterPins(App.Catalog);
        RebuildPinned();

        RebuildAllApps();
    }

    /// <summary>A–Z list without the apps the user hid. Rebuilt after hiding or restoring.</summary>
    private void RebuildAllApps()
    {
        var groups = App.Catalog.Apps
            .Where(a => !App.Settings.IsHidden(a.Id))
            .GroupBy(a => GroupKey(a.Name))
            .OrderBy(g => g.Key == "#" ? "" : g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new AppGroup(g.Key, g))
            .ToList();
        AllAppsList.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;
    }

    private static string GroupKey(string name)
    {
        char c = name.Length > 0 ? char.ToUpperInvariant(name[0]) : '#';
        return char.IsLetter(c) ? c.ToString() : "#";
    }

    private void RebuildPinned()
    {
        _pinned.Clear();
        foreach (var pin in App.Settings.ActiveProfile.Pins)
        {
            if (pin.Kind == "App" && pin.AppId is not null && App.Catalog.TryGet(pin.AppId, out var app))
                _pinned.Add(app);
        }
    }

    private void RefreshRecent()
    {
        _recent.Clear();
        bool show = App.Settings.Current.ShowRecent;
        RecentHeader.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        RecentList.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        foreach (var item in RecentFiles.Load(3))
        {
            _recent.Add(item);
            App.Icons.Request(item, item.LinkPath, 32);
        }
        if (_recent.Count == 0) RecentHeader.Visibility = Visibility.Collapsed;
    }

    // ---------- Search ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        _storeSearch?.Cancel();
        _store.Clear();
        StoreSection.Visibility = Visibility.Collapsed;
        if (query.Length == 0)
        {
            ShowPanel(ViewSelector.SelectedItem == AllAppsTab ? AllAppsList : PinnedPanel);
            return;
        }

        ShowPanel(SearchPanel);
        ResultsScroller.ChangeView(null, 0, null, disableAnimation: true);

        if (CommandRunner.IsCommand(query))
        {
            _games.Clear();
            _results.Clear();
            var command = CommandRunner.CommandText(query);
            GamesSection.Visibility = Visibility.Collapsed;
            StoreLinks.Visibility = Visibility.Collapsed;
            NoResultsText.Text = command.Length == 0
                ? "Type a command to run, like > notepad or > ping 1.1.1.1"
                : $"Enter runs “{command}” · Ctrl+Shift+Enter runs it as administrator";
            NoResultsText.Visibility = Visibility.Visible;
            return;
        }

        // Games first: installed games from every launcher.
        _games.Clear();
        var games = query.Length >= 2 ? App.Games.Search(query) : [];
        foreach (var g in games)
        {
            if (g.Icon is null && g.CoverPath is not null && File.Exists(g.CoverPath))
                g.Icon = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(g.CoverPath)) { DecodePixelWidth = 184 };
            _games.Add(g);
        }

        // App results, minus the shortcuts for games already shown above.
        _results.Clear();
        foreach (var app in SearchService.Rank(App.Catalog.Apps, query))
        {
            if (games.Any(g => g.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase) || g.Target == app.ShellPath)) continue;
            _results.Add(app);
        }

        UpdateSearchSections(query);
        if (_results.Count > 0) ResultsList.SelectedIndex = 0;

        if (query.Length >= 3 && App.Settings.Current.SearchSteamStore && App.Games.Launchers.Contains(GameLauncher.Steam))
            _ = AddSteamStoreResultsAsync(query);
    }

    private void UpdateSearchSections(string query)
    {
        GamesSection.Visibility = _games.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        StoreSection.Visibility = _store.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool none = _results.Count == 0 && _games.Count == 0 && _store.Count == 0;
        NoResultsText.Text = $"Nothing matches “{query}”";
        NoResultsText.Visibility = none ? Visibility.Visible : Visibility.Collapsed;

        var launchers = App.Games.Launchers;
        SteamStoreLink.Visibility = launchers.Contains(GameLauncher.Steam) ? Visibility.Visible : Visibility.Collapsed;
        // Epic and Battle.net open in the browser, so they're offered even when the launcher isn't installed.
        MsStoreLink.Visibility = Visibility.Visible;
        // Offer store searches when the query looks like it might be a game: no app answered it,
        // or games already matched.
        StoreLinks.Visibility = query.Length >= 3 && (_games.Count > 0 || _results.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Steam store results for games that aren't installed. Online; Settings › Search can turn it off.</summary>
    private async Task AddSteamStoreResultsAsync(string query)
    {
        var cts = _storeSearch = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, cts.Token); // wait for typing to pause
            var installed = App.Games.Games.Where(g => g.Launcher == GameLauncher.Steam && g.LauncherId is not null)
                .Select(g => g.LauncherId!).ToHashSet();
            var store = await SteamLibrary.SearchStoreAsync(query, installed, cts.Token);
            if (cts.IsCancellationRequested || SearchBox.Text.Trim() != query) return;

            _store.Clear();
            foreach (var g in store)
            {
                if (_store.Count == 3) break;
                if (_games.Any(x => x.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase)) ||
                    _results.Any(x => x.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase))) continue;
                _store.Add(g);
                if (g.CoverPath is not null) g.Icon = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(g.CoverPath));
            }
            UpdateSearchSections(query);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Offline or Steam is slow: installed results are already showing, so stay quiet.
        }
    }

    private void Game_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not GameEntry game) return;
        HideMenu();
        GameLibrary.Launch(game);
    }

    private void StoreLink_Click(object sender, RoutedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        var url = ((FrameworkElement)sender).Tag switch
        {
            "steam" => $"steam://openurl/https://store.steampowered.com/search/?term={Uri.EscapeDataString(q)}",
            "epic" => EpicLibrary.StoreSearchUrl(q),
            "battlenet" => BattleNetLibrary.ShopUrl,
            _ => XboxLibrary.StoreSearchUrl(q),
        };
        HideMenu();
        Launcher.Open(url);
    }

    // ---------- Account picture ----------

    private async Task LoadAccountAsync()
    {
        var name = await AccountPicture.GetDisplayNameAsync();
        UserNameText.Text = name;
        Avatar.DisplayName = name;

        var path = AccountPicture.FindPicturePath() ?? await AccountPicture.FetchWithUserApiAsync();
        SetPicture(path);
    }

    /// <summary>Picks up a new account picture the next time the menu opens.</summary>
    private void RefreshPictureIfChanged()
    {
        var path = AccountPicture.FindPicturePath();
        if (path is null) return;
        var stamp = File.GetLastWriteTimeUtc(path);
        if (stamp != _pictureStamp) SetPicture(path);
    }

    private void SetPicture(string? path)
    {
        if (path is null || !File.Exists(path)) return; // PersonPicture shows initials instead
        _pictureStamp = File.GetLastWriteTimeUtc(path);
        Avatar.ProfilePicture = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path))
        {
            DecodePixelWidth = 128, // 64 px at 200%; plenty, and keeps memory low
        };
    }

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool searching = SearchPanel.Visibility == Visibility.Visible;

        switch (e.Key)
        {
            case VirtualKey.Escape:
                if (SearchBox.Text.Length > 0) SearchBox.Text = "";
                else HideMenu();
                e.Handled = true;
                break;

            case VirtualKey.Enter when searching && CommandRunner.IsCommand(SearchBox.Text):
            {
                var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                var error = CommandRunner.Run(CommandRunner.CommandText(SearchBox.Text), asAdmin: ctrl && shift);
                if (error is null) HideMenu(); else NoResultsText.Text = error;
                e.Handled = true;
                break;
            }

            case VirtualKey.Enter when searching:
                if ((ResultsList.SelectedItem ?? _results.FirstOrDefault()) is AppEntry best) Launch(best);
                else if (_games.FirstOrDefault() is { } game) { HideMenu(); GameLibrary.Launch(game); }
                e.Handled = true;
                break;

            case VirtualKey.Down when searching && _results.Count > 0:
                ResultsList.SelectedIndex = Math.Min(ResultsList.SelectedIndex + 1, _results.Count - 1);
                BringSelectedResultIntoView();
                e.Handled = true;
                break;

            case VirtualKey.Up when searching && _results.Count > 0:
                ResultsList.SelectedIndex = Math.Max(ResultsList.SelectedIndex - 1, 0);
                BringSelectedResultIntoView();
                e.Handled = true;
                break;
        }
    }

    /// <summary>The results list doesn't scroll by itself (the whole results column does), so scroll that.</summary>
    private void BringSelectedResultIntoView()
    {
        if (ResultsList.ContainerFromIndex(ResultsList.SelectedIndex) is UIElement row)
            row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    private void ViewSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (SearchBox.Text.Trim().Length > 0) return;
        ShowPanel(sender.SelectedItem == AllAppsTab ? AllAppsList : PinnedPanel);
    }

    // ---------- Launching ----------

    private void Launch(AppEntry app, bool asAdmin = false)
    {
        HideMenu();
        Launcher.OpenApp(app, asAdmin);
    }

    private void App_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AppEntry app) Launch(app);
    }

    private void Recent_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not RecentItem item) return;
        HideMenu();
        Launcher.Open(item.LinkPath);
    }

    private void Item_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not AppEntry app) return;

        bool pinned = App.Settings.IsPinned(app.Id);
        var pin = new MenuFlyoutItem
        {
            Text = pinned ? "Unpin from Start" : "Pin to Start",
            Icon = new FontIcon { Glyph = pinned ? "\uE77A" : "\uE718" },
        };
        pin.Click += (_, _) =>
        {
            if (pinned) App.Settings.Unpin(app.Id); else App.Settings.Pin(app.Id);
            RebuildPinned();
        };

        var admin = new MenuFlyoutItem { Text = "Run as administrator", Icon = new FontIcon { Glyph = "\uE7EF" } };
        admin.Click += (_, _) => Launch(app, asAdmin: true);

        bool hidden = App.Settings.IsHidden(app.Id);
        var hide = new MenuFlyoutItem
        {
            Text = hidden ? "Show in All apps" : "Hide from All apps",
            Icon = new FontIcon { Glyph = hidden ? "\uE7B3" : "\uED1A" },
        };
        hide.Click += (_, _) =>
        {
            App.Settings.SetHidden(app.Id, !hidden);
            RebuildAllApps();
        };

        var menu = new MenuFlyout();
        menu.Items.Add(pin);
        menu.Items.Add(admin);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(hide);
        menu.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    private void PinnedGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        App.Settings.SetPinOrder(_pinned.Select(a => a.Id));

    // ---------- Right-hand panel ----------

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        HideMenu();
        Launcher.OpenFolder((string)((Button)sender).Tag);
    }

    private void WindowsSettings_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.Open("ms-settings:"); }
    private void Run_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.ShowRunDialog(); }
    private void Lock_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.Lock(); }
    private void Sleep_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.Sleep(); }
    private void SignOut_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.SignOut(); }
    private void Restart_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.Restart(); }
    private void ShutDown_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.ShutDown(); }
}
