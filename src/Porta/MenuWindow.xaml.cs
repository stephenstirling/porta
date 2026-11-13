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
    private const int MenuWidth = 676;   // device-independent pixels
    private const int MenuHeight = 560;
    private const int AvatarOverhang = 32; // half the account picture's circle sticks out above the panel
    private const int RightPaneWidth = 216;
    private const int GapAboveTaskbar = 12;

    private readonly IntPtr _hwnd;
    private readonly PanelBackdrop _backdrop;
    private readonly ObservableCollection<AppEntry> _pinned = [];
    private readonly ObservableCollection<RecentItem> _recent = [];
    private readonly ObservableCollection<AppEntry> _results = [];
    private readonly ObservableCollection<GameEntry> _games = [];
    private readonly ObservableCollection<GameEntry> _store = []; // Steam store results, shown below installed items
    private readonly ObservableCollection<SystemEntry> _system = []; // Windows tools, Settings pages, power actions
    private ContentDialog? _confirmDialog;
    private CancellationTokenSource? _storeSearch;
    private readonly ObservableCollection<GameEntry> _battleNet = []; // Battle.net games from Porta's list, between the Steam store rows and Wikipedia
    private readonly ObservableCollection<WikiEntry> _wiki = []; // Wikipedia articles, below the store results
    private CancellationTokenSource? _wikiSearch;
    private readonly ObservableCollection<QuickRow> _quick = []; // the calculation/conversion answer, on top
    private readonly ObservableCollection<QuickRow> _web = []; // "Search the web", at the bottom
    private CancellationTokenSource? _currencySearch;
    private CancellationTokenSource? _battleNetPictures;
    private List<GameEntry> _battleNetLoading = []; // rows showing the transparent "picture loading" placeholder
    private bool _loadingQuickSettings; // filling the pop-up from saved settings must not count as the user picking
    private readonly ObservableCollection<PathEntry> _paths = []; // recent files and folders, below apps, games and tools
    private ContentDialog? _excludeDialog;
    private readonly ObservableCollection<PersonEntry> _people = []; // people from Ocula, below apps, games and tools
    private DateTime _pictureStamp;
    private bool _isOpen;
    private bool _dragging; // a pinned app is being dragged to a new place
    private long _shownAt; // Environment.TickCount64 when the menu last opened
    private long _closedByFocusAt; // ... and when focus loss last closed it (0 = never)
    private readonly MouseHook _outsideClicks = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _focusTimer;
    private const int FocusGraceMs = 150; // focus loss this soon after opening is the menu still being activated
    private const int FocusCheckMs = 100; // wait this long after focus loss, in case it comes straight back

    public MenuWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        _backdrop = new PanelBackdrop { Strength = App.Settings.Current.GlassStrength };
        SystemBackdrop = _backdrop;
        ExtendsContentIntoTitleBar = true;

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false; // keep it out of Alt+Tab and the taskbar

        // The window is cut to the panel plus the avatar circle (CutWindowToPanelAndPicture). Windows would
        // still draw its border around the whole window rectangle, so it's turned off; the menu draws its
        // own hairline around the panel shape (MenuWindow.xaml).
        Native.HideBorder(_hwnd);
        // A plain tool window, so title-bar add-ons (e.g. DisplayFusion's buttons) don't draw over the menu.
        Native.MakeToolWindow(_hwnd);
        // Keep the cut in step with the window: after any move or resize, including a DPI change.
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidSizeChange || e.DidPositionChange) CutWindowToPanelAndPicture(Root.XamlRoot?.RasterizationScale ?? _scale);
        };

        AppWindow.Closing += (_, e) => { e.Cancel = true; HideMenu(); }; // Alt+F4 hides instead of quitting
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) OnFocusLost();
        };
        _focusTimer = DispatcherQueue.CreateTimer();
        _focusTimer.Interval = TimeSpan.FromMilliseconds(FocusCheckMs);
        _focusTimer.IsRepeating = false;
        _focusTimer.Tick += (_, _) => CheckFocus();
        _outsideClicks.ClickedElsewhere += () => DispatcherQueue.TryEnqueue(OnClickedElsewhere);

        PinnedGrid.ItemsSource = _pinned;
        RecentList.ItemsSource = _recent;
        ResultsList.ItemsSource = _results;
        GamesList.ItemsSource = _games;
        StoreList.ItemsSource = _store;
        SystemList.ItemsSource = _system;
        WikiList.ItemsSource = _wiki;
        BattleNetList.ItemsSource = _battleNet;
        CloseElsewhereToggle.IsOn = App.Settings.Current.CloseWhenClickedElsewhere;
        StartButtonToggle.IsOn = App.Settings.Current.OpenFromStartButton;
        SyncLayoutSwitches();
        BattleNetGamesToggle.IsOn = App.Settings.Current.SearchBattleNetGames;
        PeopleList.ItemsSource = _people;
        PathsList.ItemsSource = _paths;
        RecentFilesToggle.IsOn = App.Settings.Current.SearchRecentFiles;
        ExcludeFoldersButton.IsEnabled = RecentFilesToggle.IsOn;
        if (App.Settings.Current.SearchRecentFiles) RecentItems.Enable();
        RecentItems.Loaded += () => DispatcherQueue.TryEnqueue(RefreshPathRows); // the list was rebuilt: show new matches
        QuickList.ItemsSource = _quick;
        WebList.ItemsSource = _web;
        LoadQuickSettings();
        WikipediaToggle.IsOn = App.Settings.Current.SearchWikipedia;
        EpicLibraryToggle.IsOn = App.Settings.Current.SearchEpicLibrary;
        EpicCoversToggle.IsOn = App.Settings.Current.EpicCoverImages;
        WikipediaPicturesToggle.IsOn = App.Settings.Current.WikipediaPictures;
        WikipediaPicturesToggle.IsEnabled = WikipediaToggle.IsOn; // pictures only matter while Wikipedia search is on
        OculaPeopleToggle.IsOn = App.Settings.Current.SearchOculaPeople;

        var name = Native.GetDisplayName();
        SetUserName(name);
        _ = LoadAccountAsync();

        App.Catalog.Loaded += OnCatalogLoaded;
        App.Settings.Restored += () => DispatcherQueue.TryEnqueue(() =>
        {
            RebuildPinned();
            if (App.Catalog.IsLoaded) RebuildAllApps();
            GlassSlider.Value = App.Settings.Current.GlassStrength; // also updates the glass
            CloseElsewhereToggle.IsOn = App.Settings.Current.CloseWhenClickedElsewhere;
            UpdateOutsideClickWatch();
            StartButtonToggle.IsOn = App.Settings.Current.OpenFromStartButton;
        SyncLayoutSwitches();
            WikipediaToggle.IsOn = App.Settings.Current.SearchWikipedia;
            EpicLibraryToggle.IsOn = App.Settings.Current.SearchEpicLibrary;
            EpicCoversToggle.IsOn = App.Settings.Current.EpicCoverImages;
            RecentFilesToggle.IsOn = App.Settings.Current.SearchRecentFiles;
            ExcludeFoldersButton.IsEnabled = RecentFilesToggle.IsOn;
            if (RecentFilesToggle.IsOn) RecentItems.Enable(); else StopRecentFiles();
            RefreshPathRows(); // exclusions may have changed
            BattleNetGamesToggle.IsOn = App.Settings.Current.SearchBattleNetGames;
            if (!BattleNetGamesToggle.IsOn) { _battleNet.Clear(); BattleNetSection.Visibility = Visibility.Collapsed; }
            if (!EpicCoversToggle.IsOn) EpicCoverCache.ForgetCovers(App.Games.Games.Concat(EpicCatalogCache.Games));
            WikipediaPicturesToggle.IsOn = App.Settings.Current.WikipediaPictures;
            WikipediaPicturesToggle.IsEnabled = WikipediaToggle.IsOn;
            if (!App.Settings.Current.WikipediaPictures) DropWikiPictures();
            else if (!App.Settings.Current.SearchWikipedia) { _battleNetPictures?.Cancel(); BattleNetCatalog.ForgetPictures(); }
            OculaPeopleToggle.IsOn = App.Settings.Current.SearchOculaPeople;
            if (!OculaPeopleToggle.IsOn) ClearPeople();
            LoadQuickSettings();
        });
        GlassSlider.Value = App.Settings.Current.GlassStrength;
    }

    /// <summary>Window handle, for file pickers opened from the tray menu.</summary>
    public IntPtr Hwnd => _hwnd;

    // ---------- Showing and hiding ----------

    public void Toggle(bool fromMouse = false)
    {
        if (_isOpen) HideMenu();
        else if (fromMouse && Environment.TickCount64 - _closedByFocusAt < 400) return; // that click just closed it
        else ShowMenu();
    }

    public void ShowMenu()
    {
        ResetView();
        RefreshRecent();
        RefreshPictureIfChanged();
        if (App.Catalog.IsLoaded) App.Games.RefreshIfStale(App.Catalog.Apps);
        if (App.Settings.Current.SearchEpicLibrary) EpicCatalogCache.RefreshIfChanged(); // background, only if the file changed
        _ = RefreshUpdateOptionsAsync(); // background, at most once a minute
        ClockText.Text = DateTime.Now.ToString("t");
        DateText.Text = DateTime.Now.ToString("dddd, MMMM d");

        Native.MakeToolWindow(_hwnd); // again before placing: WinUI can restore its frame styles
        PlaceAboveTaskbar();
        AppWindow.Show(true);
        Native.ForceForeground(_hwnd);
        try { _shadow.Show(_hwnd, _shape, _scale, Root.ActualTheme == ElementTheme.Light); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine("Porta: couldn't draw the shadow: " + ex.Message); // the menu works without it
        }
        SearchBox.Focus(FocusState.Programmatic);
        _isOpen = true;
        _shownAt = Environment.TickCount64;
        _dragging = false;
        UpdateOutsideClickWatch();
    }

    public void HideMenu()
    {
        _confirmDialog?.Hide(); // don't leave a Restart/Shut down question waiting in the hidden menu
        _excludeDialog?.Hide();
        _focusTimer.Stop();
        _isOpen = false;
        UpdateOutsideClickWatch();
        _shadow.Hide();
        AppWindow.Hide();
    }

    // ---------- Closing when focus moves elsewhere ----------

    /// <summary>The mouse watcher runs only while the menu is open and "Close when I click elsewhere" is on.</summary>
    private void UpdateOutsideClickWatch()
    {
        try
        {
            if (_isOpen && App.Settings.Current.CloseWhenClickedElsewhere) _outsideClicks.Install();
            else _outsideClicks.Uninstall();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Porta: couldn't watch clicks: " + ex.Message); // focus loss still closes it
        }
    }

    /// <summary>The menu stopped being the active window. Checked again shortly, because focus often comes straight back.</summary>
    private void OnFocusLost()
    {
        if (_isOpen && App.Settings.Current.CloseWhenClickedElsewhere) _focusTimer.Start();
    }

    private void CheckFocus()
    {
        if (!_isOpen || !App.Settings.Current.CloseWhenClickedElsewhere) return;
        if (_dragging) { _focusTimer.Start(); return; } // look again when the drag is over
        // Still opening: Windows is handing the menu focus. Look again once that is done.
        if (Environment.TickCount64 - _shownAt < FocusGraceMs) { _focusTimer.Start(); return; }
        // The active window is one of Porta's own (palette pop-up, right-click menu, Exclude folders, a file picker).
        if (ForegroundIsPorta()) return;
        CloseForFocusLoss();
    }

    /// <summary>A click landed on another program's window, possibly one that never takes focus.</summary>
    private void OnClickedElsewhere()
    {
        if (!_isOpen || !App.Settings.Current.CloseWhenClickedElsewhere || _dragging) return;
        if (Environment.TickCount64 - _shownAt < FocusGraceMs) return;
        CloseForFocusLoss();
    }

    private void CloseForFocusLoss()
    {
        _closedByFocusAt = Environment.TickCount64; // so the tray icon click that caused this doesn't reopen it
        HideMenu();
    }

    private static bool ForegroundIsPorta()
    {
        var foreground = Native.GetForegroundWindow();
        return foreground != IntPtr.Zero
            && Native.GetWindowThreadProcessId(foreground, out uint process) != 0
            && process == (uint)Environment.ProcessId;
    }

    private void PinnedGrid_DragItemsStarting(object sender, DragItemsStartingEventArgs e) => _dragging = true;

    /// <summary>Opens centred above the taskbar on whichever screen the pointer is on.</summary>
    private void PlaceAboveTaskbar()
    {
        Native.GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
        double scale = Native.ScaleAtPoint(cursor);

        int width = (int)(MenuWidth * scale);
        int height = (int)((MenuHeight + AvatarOverhang) * scale);
        var work = area.WorkArea;
        int x = work.X + (work.Width - width) / 2;
        int y = work.Y + work.Height - height - (int)(GapAboveTaskbar * scale);

        _scale = scale;
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        CutWindowToPanelAndPicture(scale);
    }

    private double _scale = 1.0; // display scale the menu was last placed at
    private readonly ShadowWindow _shadow = new();
    private ShadowWindow.Shape _shape;

    /// <summary>
    /// Cuts the window to the panel plus the account picture's circle above its top edge, so the strip
    /// above the panel is neither drawn nor clickable. Windows draws a cut window without its own
    /// rounded corners, border or shadow, and the cut edges aren't smoothed.
    /// </summary>
    private void CutWindowToPanelAndPicture(double scale)
    {
        Native.GetWindowRect(_hwnd, out var window);
        var origin = new Native.POINT();
        Native.ClientToScreen(_hwnd, ref origin);
        Native.GetClientRect(_hwnd, out var client);
        int dx = origin.X - window.Left, dy = origin.Y - window.Top; // region is relative to the window, content to the client

        int overhang = (int)Math.Round(AvatarOverhang * scale);
        int corner = (int)Math.Round(16 * scale); // 8 px radius, like Windows' own corners
        var panel = Native.CreateRoundRectRgn(dx, dy + overhang, dx + client.Right + 1, dy + client.Bottom + 1, corner, corner);

        int radius = (int)Math.Round(32 * scale);
        int cx = dx + client.Right - (int)Math.Round(RightPaneWidth / 2.0 * scale);
        int cy = dy + overhang;
        var circle = Native.CreateEllipticRgn(cx - radius, cy - radius, cx + radius + 1, cy + radius + 1);

        // Remember the shape in screen pixels: the shadow behind the menu follows it.
        _shape = new ShadowWindow.Shape(origin.X, origin.Y + overhang, origin.X + client.Right + 1, origin.Y + client.Bottom + 1,
            corner / 2.0, origin.X + client.Right - Math.Round(RightPaneWidth / 2.0 * scale), origin.Y + overhang, radius);

        Native.CombineRgn(panel, panel, circle, Native.RGN_OR);
        Native.DeleteObject(circle);
        Native.SetWindowRgn(_hwnd, panel, true); // Windows owns the region from here
    }

    private void ResetView()
    {
        SearchBox.Text = "";
        _showingAllApps = false;
        AllAppsZoom.IsZoomedInViewActive = true;
        ShowPanel(PinnedPanel);
    }

    private bool _showingAllApps; // Pinned or All apps, whichever the header shows when nothing is typed

    private void ShowPanel(UIElement panel)
    {
        PinnedPanel.Visibility = panel == PinnedPanel ? Visibility.Visible : Visibility.Collapsed;
        AllAppsZoom.Visibility = panel == AllAppsZoom ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = panel == SearchPanel ? Visibility.Visible : Visibility.Collapsed;
        ViewHeader.Visibility = panel == SearchPanel ? Visibility.Collapsed : Visibility.Visible;
        ViewTitle.Text = _showingAllApps ? "All apps" : "Pinned";
        ViewToggleButton.Content = _showingAllApps ? "‹  Back" : "All apps  ›";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ViewToggleButton, _showingAllApps ? "Back to pinned apps" : "All apps");
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
        var view = new CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;
        AllAppsList.ItemsSource = view;
        AllAppsLetters.ItemsSource = view.CollectionGroups; // the zoomed-out view: one tile per letter
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
        _navigated = false; // typing starts the highlight over at the top row
        SetSelected(null, null);
        _storeSearch?.Cancel();
        _store.Clear();
        StoreSection.Visibility = Visibility.Collapsed;
        _battleNet.Clear();
        BattleNetSection.Visibility = Visibility.Collapsed;
        _paths.Clear();
        PathsSection.Visibility = Visibility.Collapsed;
        _wikiSearch?.Cancel();
        _wiki.Clear();
        WikiSection.Visibility = Visibility.Collapsed;
        _people.Clear();
        PeopleSection.Visibility = Visibility.Collapsed;
        _currencySearch?.Cancel();
        _battleNetPictures?.Cancel();
        _quick.Clear();
        _web.Clear();
        QuickList.Visibility = Visibility.Collapsed;
        WebList.Visibility = Visibility.Collapsed;
        if (query.Length == 0)
        {
            ShowPanel(_showingAllApps ? AllAppsZoom : PinnedPanel);
            return;
        }

        ShowPanel(SearchPanel);
        ResultsScroller.ChangeView(null, 0, null, disableAnimation: true);

        if (CommandRunner.IsCommand(query))
        {
            _games.Clear();
            _results.Clear();
            _system.Clear();
            SystemSection.Visibility = Visibility.Collapsed;
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

        // Then Epic games from the launcher's local catalog cache (offline). The same game installed from
        // Epic is shown once; a Steam game with the same title still shows, each with its store label.
        if (query.Length >= 2 && App.Settings.Current.SearchEpicLibrary)
        {
            foreach (var g in GameLibrary.Rank(EpicCatalogCache.Games, query))
            {
                if (games.Any(x => x.Launcher == GameLauncher.Epic && x.Name.Equals(g.Name, StringComparison.Ordinal))) continue;
                _games.Add(g);
            }
        }

        // Epic covers: only for the results on screen.
        foreach (var g in _games) EpicCoverCache.Request(g);

        // App results, minus the shortcuts for games already shown above.
        _results.Clear();
        foreach (var app in SearchService.Rank(App.Catalog.Apps, query))
        {
            if (games.Any(g => g.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase) || g.Target == app.ShellPath)) continue;
            _results.Add(app);
        }

        // Windows tools, Settings pages and power actions, minus any already listed as an app.
        _system.Clear();
        foreach (var entry in SystemCatalog.Search(query, UpdatesOffered))
        {
            if (_results.Any(a => a.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))) continue;
            _system.Add(entry);
        }

        AddPathRows(query);

        // Battle.net games from Porta's own list (offline): installed ones play, the others open their page.
        if (query.Length >= 2 && App.Settings.Current.SearchBattleNetGames)
            foreach (var g in BattleNetCatalog.Search(query, App.Games.Games, _games)) _battleNet.Add(g);

        // People named in Ocula: only when switched on; read from Ocula's own file on this PC, never online.
        if (query.Length >= 2 && App.Settings.Current.SearchOculaPeople)
        {
            var oculaInstalled = OculaPeople.IsInstalled();
            foreach (var person in OculaPeople.Search(query))
            {
                person.CanOpen = oculaInstalled;
                if (person.Icon is null && person.ThumbnailPath is not null) LoadPersonPicture(person);
                _people.Add(person);
            }
        }

        AddQuickAnswer(query);
        AddWebRow(query);

        UpdateSearchSections(query); // also puts the highlight on the top row
        _ = LoadBattleNetPicturesAsync(); // after the text is up

        if (query.Length >= 3 && App.Settings.Current.SearchSteamStore && App.Games.Launchers.Contains(GameLauncher.Steam))
            _ = AddSteamStoreResultsAsync(query);
        if (query.Length >= 3 && App.Settings.Current.SearchWikipedia)
            _ = AddWikipediaResultsAsync(query);
    }

    private void UpdateSearchSections(string query)
    {
        QuickList.Visibility = _quick.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WebList.Visibility = _web.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GamesSection.Visibility = _games.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        SystemSection.Visibility = _system.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PeopleSection.Visibility = _people.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PathsSection.Visibility = _paths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StoreSection.Visibility = _store.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BattleNetSection.Visibility = _battleNet.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        WikiSection.Visibility = _wiki.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // "Nothing matches" only when every section is empty.
        bool none = _results.Count == 0 && _games.Count == 0 && _system.Count == 0 && _store.Count == 0 && _wiki.Count == 0 && _people.Count == 0 && _quick.Count == 0 && _battleNet.Count == 0 && _paths.Count == 0;
        NoResultsText.Text = $"Nothing matches “{query}”";
        // The quick answer and web rows sit at the top when nothing else matched; keep the message below them.
        NoResultsText.Margin = new Thickness(0, 40 + 56 * (_quick.Count + _web.Count), 0, 0);
        NoResultsText.Visibility = none ? Visibility.Visible : Visibility.Collapsed;

        var launchers = App.Games.Launchers;
        SteamStoreLink.Visibility = launchers.Contains(GameLauncher.Steam) ? Visibility.Visible : Visibility.Collapsed;
        // Epic and Battle.net open in the browser, so they're offered even when the launcher isn't installed.
        MsStoreLink.Visibility = Visibility.Visible;
        // Offer store searches when the query looks like it might be a game: nothing else answered it,
        // or games already matched.
        StoreLinks.Visibility = query.Length >= 3 && (_games.Count > 0 || (_results.Count == 0 && _system.Count == 0))
            ? Visibility.Visible : Visibility.Collapsed;

        EnsureSelection();
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
                // Skip a store result only for a game already shown from Steam itself; the same title from
                // Epic stays, so both stores show with their labels.
                if (_games.Any(x => x.Launcher == GameLauncher.Steam && x.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase)) ||
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

    /// <summary>
    /// Wikipedia articles, after typing pauses. Runs beside the other results and never holds them up;
    /// offline, slow or failing lookups just show nothing. Nothing is sent when the setting is off.
    /// </summary>
    private async Task AddWikipediaResultsAsync(string query)
    {
        var cts = _wikiSearch = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, cts.Token); // wait for typing to pause
            var pictures = App.Settings.Current.WikipediaPictures;
            var articles = await WikipediaSearch.SearchAsync(query, cts.Token, pictures);
            if (cts.IsCancellationRequested || SearchBox.Text.Trim() != query || !App.Settings.Current.SearchWikipedia) return;

            // Text first; the pictures follow one by one in the background.
            _wiki.Clear();
            foreach (var a in articles)
                _wiki.Add(new WikiEntry { Title = a.Title, Description = a.Description, Url = a.Url, ThumbnailUrl = pictures ? a.ThumbnailUrl : null, IsDisambiguation = a.IsDisambiguation });
            UpdateSearchSections(query);

            if (pictures)
                foreach (var entry in _wiki.ToList())
                    _ = entry.ThumbnailUrl is not null ? LoadWikiPictureAsync(entry, cts.Token) : LoadWikiFallbackPictureAsync(entry, cts.Token);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// One more try for a row with no picture: the article's own summary, in the background after the text is up.
    /// Disambiguation pages are skipped, and an article that really has no picture keeps its letter tile.
    /// </summary>
    private async Task LoadWikiFallbackPictureAsync(WikiEntry entry, CancellationToken ct)
    {
        try
        {
            if (entry.IsDisambiguation) return;
            var url = await WikipediaSearch.FallbackPictureAsync(entry.Url, ct); // continues on the UI thread
            if (url is null || ct.IsCancellationRequested || !_wiki.Contains(entry) ||
                !App.Settings.Current.SearchWikipedia || !App.Settings.Current.WikipediaPictures) return;
            entry.ThumbnailUrl = url;
            await LoadWikiPictureAsync(entry, ct);
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadWikiPictureAsync(WikiEntry entry, CancellationToken ct)
    {
        try
        {
            var path = await WikiPictureCache.GetFileAsync(entry.ThumbnailUrl!, ct); // continues on the UI thread
            if (path is null || ct.IsCancellationRequested || !_wiki.Contains(entry) ||
                !App.Settings.Current.SearchWikipedia || !App.Settings.Current.WikipediaPictures) return;

            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 96 };
            bitmap.ImageFailed += (_, _) => entry.Icon = null; // a bad picture brings the letter tile back
            bitmap.UriSource = new Uri(path);
            entry.Icon = bitmap;
        }
        catch (Exception ex) when (ex is OperationCanceledException or UriFormatException or ArgumentException) { }
    }

    /// <summary>Back to letter tiles, for when pictures or Wikipedia search are switched off.</summary>
    private void DropWikiPictures()
    {
        _battleNetPictures?.Cancel();
        BattleNetCatalog.ForgetPictures();
        foreach (var entry in _wiki) entry.Icon = null;
        WikiPictureCache.Clear();
    }

    private void WikipediaPicturesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.WikipediaPictures == WikipediaPicturesToggle.IsOn) return;
        App.Settings.Current.WikipediaPictures = WikipediaPicturesToggle.IsOn;
        App.Settings.Save();
        if (!WikipediaPicturesToggle.IsOn) DropWikiPictures(); // no more picture requests; saved pictures deleted
        else _ = LoadBattleNetPicturesAsync();
    }

    private void Wiki_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WikiEntry article) OpenWiki(article);
    }

    private void OpenWiki(WikiEntry article)
    {
        HideMenu();
        Launcher.Open(article.Url);
    }

    private void EpicLibraryToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.SearchEpicLibrary == EpicLibraryToggle.IsOn) return;
        App.Settings.Current.SearchEpicLibrary = EpicLibraryToggle.IsOn;
        App.Settings.Save();
    }

    // ---------- Recent files and folders ----------

    private void AddPathRows(string query)
    {
        if (query.Length < 2 || !App.Settings.Current.SearchRecentFiles) return;
        foreach (var entry in RecentItems.Search(query, App.Settings.Current.ExcludedFolders)) _paths.Add(entry);
    }

    /// <summary>Redoes the recent files rows for what is typed (the list was rebuilt, or the exclusions changed).</summary>
    private void RefreshPathRows()
    {
        var query = SearchBox.Text.Trim();
        if (SearchPanel.Visibility != Visibility.Visible || query.Length == 0 || CommandRunner.IsCommand(query)) return;
        var before = _paths.Select(p => p.FullPath).ToList();
        _paths.Clear();
        AddPathRows(query);
        if (!before.SequenceEqual(_paths.Select(p => p.FullPath))) UpdateSearchSections(query);
    }

    private void StopRecentFiles()
    {
        RecentItems.Disable(); // stops watching the Recent folder and forgets the list
        _paths.Clear();
        PathsSection.Visibility = Visibility.Collapsed;
    }

    private void RecentFilesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.SearchRecentFiles == RecentFilesToggle.IsOn) return;
        App.Settings.Current.SearchRecentFiles = RecentFilesToggle.IsOn;
        App.Settings.Save();
        ExcludeFoldersButton.IsEnabled = RecentFilesToggle.IsOn;
        if (RecentFilesToggle.IsOn) RecentItems.Enable(); else StopRecentFiles();
        RefreshQuickRows(); // also re-lays out the "nothing matches" message
    }

    private void Path_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PathEntry entry) OpenPath(entry);
    }

    private void OpenPath(PathEntry entry)
    {
        HideMenu();
        try
        {
            Launcher.Open(entry.FullPath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine("Porta: couldn't open " + entry.FullPath + ": " + ex.Message);
        }
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PathEntry entry) ShowInFolder(entry);
    }

    private void ShowInFolder(PathEntry entry)
    {
        HideMenu();
        try
        {
            // Explorer opens the folder with the file selected.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{entry.FullPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine("Porta: couldn't show " + entry.FullPath + ": " + ex.Message);
        }
    }

    /// <summary>A small window to list the folders to hide: type or paste a path, Add; or remove one.</summary>
    private async void ExcludeFolders_Click(object sender, RoutedEventArgs e)
    {
        PaletteFlyout.Hide();
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, MaxHeight = 220 };
        var empty = new TextBlock { Text = "No folders excluded.", Opacity = 0.7, Margin = new Thickness(0, 4, 0, 4) };
        var input = new TextBox { PlaceholderText = @"Folder path, for example D:\Private", Width = 330 };
        var add = new Button { Content = "Add" };
        var message = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Colors.OrangeRed) };

        void Refresh()
        {
            list.Items.Clear();
            foreach (var path in App.Settings.Current.ExcludedFolders.ToList())
            {
                var remove = new Button { Content = "Remove", Padding = new Thickness(8, 2, 8, 2) };
                remove.Click += (_, _) =>
                {
                    App.Settings.Current.ExcludedFolders.Remove(path);
                    App.Settings.Save();
                    Refresh();
                    RefreshPathRows();
                };
                var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = path, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
                list.Items.Add(row);
            }
            empty.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void TryAdd()
        {
            var path = RecentItems.NormalizeFolder(input.Text);
            if (path is null || !Path.IsPathRooted(path) || path.StartsWith(@"\\"))
            {
                message.Text = @"Enter a full folder path, such as D:\Private (network shares aren't searched anyway).";
                return;
            }
            message.Text = "";
            if (!App.Settings.Current.ExcludedFolders.Any(p => string.Equals(RecentItems.NormalizeFolder(p), path, StringComparison.OrdinalIgnoreCase)))
            {
                App.Settings.Current.ExcludedFolders.Add(path);
                App.Settings.Save();
                RefreshPathRows();
            }
            input.Text = "";
            Refresh();
        }

        add.Click += (_, _) => TryAdd();
        input.KeyDown += (_, k) => { if (k.Key == VirtualKey.Enter) { TryAdd(); k.Handled = true; } };
        Refresh();

        var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        inputRow.Children.Add(input);
        inputRow.Children.Add(add);
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "Files and folders inside these folders never show up in recent files and folders search.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(list);
        content.Children.Add(empty);
        content.Children.Add(inputRow);
        content.Children.Add(message);

        _excludeDialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Exclude folders",
            Content = content,
            CloseButtonText = "Done",
            DefaultButton = ContentDialogButton.Close,
        };
        try { await _excludeDialog.ShowAsync(); }
        finally { _excludeDialog = null; }
    }

    /// <summary>
    /// Pictures for Battle.net rows that have no program icon: the lead picture of the game's Wikipedia article, in the
    /// background after the text shows, one request for all rows on screen. Only while "Search Battle.net games",
    /// "Wikipedia pictures" and "Search Wikipedia" are all on; nothing is requested otherwise, and nothing is ever
    /// downloaded from blizzard.com or battle.net. A missing or failed picture leaves the letter tile.
    /// </summary>
    private async Task LoadBattleNetPicturesAsync()
    {
        static bool Allowed() => App.Settings.Current is { SearchBattleNetGames: true, WikipediaPictures: true, SearchWikipedia: true };
        foreach (var old in _battleNetLoading) old.PictureLoading = false; // rows of an earlier search that was replaced
        _battleNetLoading = [];
        if (!Allowed()) return;
        var rows = _battleNet.Where(r => r.WikipediaTitle is not null && r.Icon is null && r.IconSource is null && !r.WikipediaPictureTried).ToList();
        if (rows.Count == 0) return;

        var cts = _battleNetPictures = new CancellationTokenSource();
        _battleNetLoading = rows;
        foreach (var row in rows) row.PictureLoading = true; // nothing behind the picture while it loads, not the gray letter tile
        try
        {
            await Task.Delay(300, cts.Token); // wait for typing to pause
            if (!Allowed()) return;
            var links = await WikipediaSearch.PicturesForTitlesAsync(rows.Select(r => r.WikipediaTitle!), cts.Token); // continues on the UI thread
            if (links is null) return; // offline: letter tiles stay, and the next search tries again

            foreach (var row in rows)
            {
                if (cts.IsCancellationRequested || !Allowed()) return;
                row.WikipediaPictureTried = true;
                if (!links.TryGetValue(row.WikipediaTitle!, out var link) || link is null) continue; // the article has no picture
                var path = await WikiPictureCache.GetFileAsync(link, cts.Token);
                if (path is null || cts.IsCancellationRequested || !Allowed() || row.Icon is not null) continue;

                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 256 }; // the 250 px copy at full size
                bitmap.ImageFailed += (_, _) => { row.Icon = null; row.IconFromWikipedia = false; }; // a bad picture brings the letter tile back
                bitmap.ImageOpened += (_, _) => row.PictureShiftY = GameEntry.ShiftFor(bitmap.PixelWidth, bitmap.PixelHeight);
                bitmap.UriSource = new Uri(path);
                row.Icon = bitmap;
                row.IconFromWikipedia = true;
                row.PictureLoading = false;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or UriFormatException or ArgumentException) { }
        finally
        {
            // Done, failed or cancelled: rows without a picture get their letter tile back. A newer request clears the rest itself.
            if (ReferenceEquals(_battleNetPictures, cts))
            {
                foreach (var row in rows) row.PictureLoading = false;
                _battleNetLoading = [];
            }
        }
    }

    private void BattleNetGamesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.SearchBattleNetGames == BattleNetGamesToggle.IsOn) return;
        App.Settings.Current.SearchBattleNetGames = BattleNetGamesToggle.IsOn;
        App.Settings.Save();
        if (!BattleNetGamesToggle.IsOn) { _battleNet.Clear(); BattleNetSection.Visibility = Visibility.Collapsed; }
    }

    private void EpicCoversToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.EpicCoverImages == EpicCoversToggle.IsOn) return;
        App.Settings.Current.EpicCoverImages = EpicCoversToggle.IsOn;
        App.Settings.Save();
        if (!EpicCoversToggle.IsOn) EpicCoverCache.ForgetCovers(App.Games.Games.Concat(EpicCatalogCache.Games));
    }

    // ---------- Quick answers and "Search the web" ----------

    private const string CalculatorGlyph = "\uE8EF";
    private const string GlobeGlyph = "\uE774";

    private static QuickRow RowFor(QuickAnswer a) => new()
    {
        Glyph = CalculatorGlyph,
        Title = a.Title,
        Subtitle = a.Subtitle,
        ActionText = a.CopyText is null ? "" : "Copy",
        CopyText = a.CopyText,
    };

    /// <summary>The one answer row for a calculation or conversion. Maths and units are local; currency needs the online switch.</summary>
    private void AddQuickAnswer(string query)
    {
        if (!App.Settings.Current.ShowQuickAnswers) return;
        switch (QuickAnswers.Evaluate(query, System.Globalization.CultureInfo.CurrentCulture))
        {
            case QuickAnswer answer:
                _quick.Add(RowFor(answer));
                break;
            case PendingCurrency pending when !App.Settings.Current.OnlineCurrencyRates:
                _quick.Add(new QuickRow
                {
                    Glyph = CalculatorGlyph,
                    Title = pending.Request.Expression,
                    Subtitle = "Turn on “Online currency rates” (glass-strength pop-up) to convert currencies. Nothing is sent until then.",
                });
                break;
            case PendingCurrency pending when CurrencyRates.TryGetFresh(out var table):
                _quick.Add(CurrencyRow(pending.Request, table));
                break;
            case PendingCurrency pending:
                _ = AddCurrencyAnswerAsync(query, pending.Request); // fetches the rates only now that one was typed
                break;
        }
    }

    private static QuickRow CurrencyRow(CurrencyRequest request, RateTable? table)
    {
        var answer = table is null ? null : CurrencyRates.Convert(request, table, System.Globalization.CultureInfo.CurrentCulture);
        if (answer is not null) return RowFor(answer);
        return new QuickRow
        {
            Glyph = CalculatorGlyph,
            Title = request.Expression,
            Subtitle = table is null ? "Couldn't get exchange rates. Check your connection." : "No exchange rate for that currency.",
        };
    }

    private async Task AddCurrencyAnswerAsync(string query, CurrencyRequest request)
    {
        var cts = _currencySearch = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, cts.Token); // wait for typing to pause
            var table = await CurrencyRates.GetAsync(cts.Token);
            if (cts.IsCancellationRequested || SearchBox.Text.Trim() != query ||
                !App.Settings.Current.ShowQuickAnswers || !App.Settings.Current.OnlineCurrencyRates) return;

            _quick.Clear();
            _quick.Add(CurrencyRow(request, table));
            UpdateSearchSections(query);
        }
        catch (OperationCanceledException) { }
    }

    private void AddWebRow(string query)
    {
        if (!App.Settings.Current.SearchTheWeb || query.Length < 2) return;
        var engine = WebSearch.Normalize(App.Settings.Current.WebSearchEngine);
        _web.Add(new QuickRow
        {
            Glyph = GlobeGlyph,
            Title = $"Search the web for “{query}”",
            Subtitle = $"Opens {engine} in your browser",
            ActionText = "Search",
            WebQuery = query,
        });
    }

    /// <summary>Redoes the quick and web rows for what is typed (after a switch or the engine changed).</summary>
    private void RefreshQuickRows()
    {
        var query = SearchBox.Text.Trim();
        if (SearchPanel.Visibility != Visibility.Visible || query.Length == 0 || CommandRunner.IsCommand(query)) return;
        _currencySearch?.Cancel();
        _quick.Clear();
        _web.Clear();
        AddQuickAnswer(query);
        AddWebRow(query);
        UpdateSearchSections(query);
    }

    private void CopyAnswer(QuickRow row)
    {
        if (row.CopyText is null) return;
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(row.CopyText);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush(); // still there after Porta closes
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine("Porta: couldn't copy: " + ex.Message); // clipboard busy
            return;
        }
        HideMenu();
    }

    private void OpenWeb(QuickRow row)
    {
        if (row.WebQuery is null) return;
        var url = WebSearch.UrlFor(App.Settings.Current.WebSearchEngine, row.WebQuery);
        HideMenu();
        try
        {
            Launcher.Open(url);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine("Porta: couldn't open the browser: " + ex.Message);
        }
    }

    private void Quick_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is QuickRow row) CopyAnswer(row);
    }

    private void Web_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is QuickRow row) OpenWeb(row);
    }

    /// <summary>Puts the saved choices into the pop-up's controls (at start and after a restore).</summary>
    private void LoadQuickSettings()
    {
        var s = App.Settings.Current;
        _loadingQuickSettings = true;
        QuickAnswersToggle.IsOn = s.ShowQuickAnswers;
        CurrencyRatesToggle.IsOn = s.OnlineCurrencyRates;
        CurrencyRatesToggle.IsEnabled = QuickAnswersToggle.IsOn; // rates only matter while quick answers are on
        WebSearchToggle.IsOn = s.SearchTheWeb;
        WebEngineBox.SelectedIndex = Array.IndexOf(WebSearch.Engines, WebSearch.Normalize(s.WebSearchEngine));
        WebEngineBox.IsEnabled = WebSearchToggle.IsOn;
        _loadingQuickSettings = false;
        if (!s.OnlineCurrencyRates || !s.ShowQuickAnswers) CurrencyRates.Clear();
    }

    private void QuickAnswersToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ShowQuickAnswers == QuickAnswersToggle.IsOn) return;
        App.Settings.Current.ShowQuickAnswers = QuickAnswersToggle.IsOn;
        App.Settings.Save();
        CurrencyRatesToggle.IsEnabled = QuickAnswersToggle.IsOn;
        if (!QuickAnswersToggle.IsOn) { _currencySearch?.Cancel(); CurrencyRates.Clear(); }
        RefreshQuickRows();
    }

    private void CurrencyRatesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.OnlineCurrencyRates == CurrencyRatesToggle.IsOn) return;
        App.Settings.Current.OnlineCurrencyRates = CurrencyRatesToggle.IsOn;
        App.Settings.Save();
        if (!CurrencyRatesToggle.IsOn) { _currencySearch?.Cancel(); CurrencyRates.Clear(); } // no more requests; rates forgotten
        RefreshQuickRows();
    }

    // ---------- Layout switches (what the right-hand panel and the pinned view show) ----------

    private void SetUserName(string name)
    {
        UserNameText.Text = name;
        UserFolderText.Text = name;
        Avatar.DisplayName = name;
    }

    /// <summary>Sets the five layout switches from the saved settings and shows or hides the parts they control.</summary>
    private void SyncLayoutSwitches()
    {
        var s = App.Settings.Current;
        ShowRecentToggle.IsOn = s.ShowRecent;
        ShowUserFolderToggle.IsOn = s.ShowUserFolderLink;
        ShowControlPanelToggle.IsOn = s.ShowControlPanel;
        ShowClockToggle.IsOn = s.ShowClockCard;
        ShowLockToggle.IsOn = s.ShowLockButton;
        ShowUpdateOptionsToggle.IsOn = s.ShowUpdateOptions;
        ApplyLayoutSettings();
    }

    private void ApplyLayoutSettings()
    {
        var s = App.Settings.Current;
        UserFolderRow.Visibility = s.ShowUserFolderLink ? Visibility.Visible : Visibility.Collapsed;
        ControlPanelRow.Visibility = s.ShowControlPanel ? Visibility.Visible : Visibility.Collapsed;
        ClockCard.Visibility = s.ShowClockCard ? Visibility.Visible : Visibility.Collapsed;
        LockButton.Visibility = s.ShowLockButton ? Visibility.Visible : Visibility.Collapsed;
        ApplyUpdateOptions();
        RefreshRecent();
    }

    private void ShowRecentToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ShowRecent == ShowRecentToggle.IsOn) return;
        App.Settings.Current.ShowRecent = ShowRecentToggle.IsOn;
        App.Settings.Save();
        ApplyLayoutSettings();
    }

    private void ShowUserFolderToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ShowUserFolderLink == ShowUserFolderToggle.IsOn) return;
        App.Settings.Current.ShowUserFolderLink = ShowUserFolderToggle.IsOn;
        App.Settings.Save();
        ApplyLayoutSettings();
    }

    private void ShowControlPanelToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ShowControlPanel == ShowControlPanelToggle.IsOn) return;
        App.Settings.Current.ShowControlPanel = ShowControlPanelToggle.IsOn;
        App.Settings.Save();
        ApplyLayoutSettings();
    }

    private void ShowClockToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ShowClockCard == ShowClockToggle.IsOn) return;
        App.Settings.Current.ShowClockCard = ShowClockToggle.IsOn;
        App.Settings.Save();
        ApplyLayoutSettings();
    }

    private void ShowLockToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ShowLockButton == ShowLockToggle.IsOn) return;
        App.Settings.Current.ShowLockButton = ShowLockToggle.IsOn;
        App.Settings.Save();
        ApplyLayoutSettings();
    }

    private void ShowUpdateOptionsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ShowUpdateOptions == ShowUpdateOptionsToggle.IsOn) return;
        App.Settings.Current.ShowUpdateOptions = ShowUpdateOptionsToggle.IsOn;
        App.Settings.Save();
        ApplyLayoutSettings();
        if (ShowUpdateOptionsToggle.IsOn) _ = RefreshUpdateOptionsAsync();
    }

    // ---------- Update and restart / Update and shut down ----------

    /// <summary>Windows has updates waiting and the switch is on: offer the update actions.</summary>
    private static bool UpdatesOffered => App.Settings.Current.ShowUpdateOptions && UpdateStatus.IsPending;

    /// <summary>Asks Windows (or uses the answer from the last minute), then shows or hides the dot and the menu items.</summary>
    private async Task RefreshUpdateOptionsAsync()
    {
        if (!App.Settings.Current.ShowUpdateOptions) return;
        await UpdateStatus.CheckAsync();
        ApplyUpdateOptions();
    }

    private void ApplyUpdateOptions()
    {
        var shown = UpdatesOffered ? Visibility.Visible : Visibility.Collapsed;
        UpdateDot.Visibility = shown;
        UpdateRestartItem.Visibility = shown;
        UpdateShutDownItem.Visibility = shown;
        ToolTipService.SetToolTip(PowerButton, UpdatesOffered ? "Power (updates are waiting)" : "Power");
    }

    private void PowerFlyout_Opening(object? sender, object e)
    {
        ApplyUpdateOptions();
        _ = RefreshUpdateOptionsAsync();
    }

    private void UpdateAndRestart_Click(object sender, RoutedEventArgs e) => _ = OpenSystemAsync(PowerEntry(PowerAction.UpdateAndRestart));
    private void UpdateAndShutDown_Click(object sender, RoutedEventArgs e) => _ = OpenSystemAsync(PowerEntry(PowerAction.UpdateAndShutDown));

    private static SystemEntry PowerEntry(PowerAction action) => SystemCatalog.All.First(x => x.Power == action);

    /// <summary>Starts the update-and-restart or update-and-shut-down. With --simulate-pending-update it only says what would happen.</summary>
    private async Task RunUpdateActionAsync(SystemEntry entry)
    {
        var restart = entry.Power == PowerAction.UpdateAndRestart;
        if (UpdateStatus.Simulated)
        {
            await ShowMessageAsync("Simulated update", $"Porta would now install the waiting updates and {(restart ? "restart" : "shut down")}. " +
                "Nothing was done, because Porta was started with --simulate-pending-update.");
            return;
        }
        var error = restart ? Launcher.UpdateAndRestart() : Launcher.UpdateAndShutDown();
        if (error == 0) { HideMenu(); return; }
        await ShowMessageAsync("Windows didn't start it", $"Windows said no ({new System.ComponentModel.Win32Exception((int)error).Message}). " +
            "Try again, or use Settings > Windows Update.");
    }

    private async Task ShowMessageAsync(string title, string text)
    {
        _confirmDialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = text, CloseButtonText = "OK", DefaultButton = ContentDialogButton.Close };
        await _confirmDialog.ShowAsync();
        _confirmDialog = null;
    }

    private void CloseElsewhereToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.CloseWhenClickedElsewhere == CloseElsewhereToggle.IsOn) return;
        App.Settings.Current.CloseWhenClickedElsewhere = CloseElsewhereToggle.IsOn;
        App.Settings.Save();
        UpdateOutsideClickWatch();
    }

    private void StartButtonToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.OpenFromStartButton == StartButtonToggle.IsOn) return;
        App.Settings.Current.OpenFromStartButton = StartButtonToggle.IsOn;
        App.Settings.Save();
        App.ApplyStartButtonSetting();
    }

    private void WebSearchToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.SearchTheWeb == WebSearchToggle.IsOn) return;
        App.Settings.Current.SearchTheWeb = WebSearchToggle.IsOn;
        App.Settings.Save();
        WebEngineBox.IsEnabled = WebSearchToggle.IsOn;
        RefreshQuickRows();
    }

    private void WebEngineBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingQuickSettings || (WebEngineBox.SelectedItem as ComboBoxItem)?.Content is not string engine) return;
        engine = WebSearch.Normalize(engine);
        if (App.Settings.Current.WebSearchEngine == engine) return;
        App.Settings.Current.WebSearchEngine = engine;
        App.Settings.Save();
        RefreshQuickRows();
    }

    /// <summary>Face thumbnail from the local file; if it can't be decoded the letter tile comes back.</summary>
    private static void LoadPersonPicture(PersonEntry person)
    {
        try
        {
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 80 };
            bitmap.ImageFailed += (_, _) => person.Icon = null;
            bitmap.UriSource = new Uri(person.ThumbnailPath!);
            person.Icon = bitmap;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            // Odd path: keep the letter tile.
        }
    }

    private void Person_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PersonEntry person) OpenPerson(person);
    }

    private void OpenPerson(PersonEntry person)
    {
        if (!person.CanOpen) return; // no Ocula, no action
        HideMenu();
        try
        {
            Launcher.Open(OculaPeople.OpenLink(person));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine("Porta: couldn't open Ocula: " + ex.Message);
        }
    }

    private void ClearPeople()
    {
        OculaPeople.Clear();
        _people.Clear();
        PeopleSection.Visibility = Visibility.Collapsed;
    }

    private void OculaPeopleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.SearchOculaPeople == OculaPeopleToggle.IsOn) return;
        App.Settings.Current.SearchOculaPeople = OculaPeopleToggle.IsOn;
        App.Settings.Save();
        if (!OculaPeopleToggle.IsOn) ClearPeople(); // the file is not read again and nothing is kept
    }

    private void WikipediaToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.SearchWikipedia == WikipediaToggle.IsOn) return;
        App.Settings.Current.SearchWikipedia = WikipediaToggle.IsOn;
        App.Settings.Save();
        WikipediaPicturesToggle.IsEnabled = WikipediaToggle.IsOn;
        if (!WikipediaToggle.IsOn)
        {
            _wikiSearch?.Cancel(); // stop anything in flight; nothing more is sent
            _wiki.Clear();
            WikiSection.Visibility = Visibility.Collapsed;
            _battleNetPictures?.Cancel(); // Battle.net rows take their pictures from Wikipedia too
            BattleNetCatalog.ForgetPictures();
        }
        else _ = LoadBattleNetPicturesAsync();
    }

    private void System_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SystemEntry entry) _ = OpenSystemAsync(entry);
    }

    /// <summary>Opens a tool or Settings page, or runs a power action. Restart and Shut down ask first.</summary>
    private async Task OpenSystemAsync(SystemEntry entry)
    {
        if (entry.Confirm)
        {
            _confirmDialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = $"{entry.Name} now?",
                Content = entry.IsUpdateAction
                    ? $"Windows installs the waiting updates, then {(entry.Power == PowerAction.UpdateAndRestart ? "restarts" : "turns off")}. Save your work first: apps with unsaved work will ask before closing."
                    : "Save your work first. Open apps will close.",
                PrimaryButtonText = entry.IsUpdateAction ? "Yes" : entry.Name,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            var answer = await _confirmDialog.ShowAsync();
            _confirmDialog = null;
            if (answer != ContentDialogResult.Primary) return;
        }
        if (entry.IsUpdateAction)
        {
            await RunUpdateActionAsync(entry);
            return;
        }
        HideMenu();
        Launcher.OpenSystem(entry);
    }

    private void Game_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GameEntry game) OpenGame(game);
    }

    private void OpenGame(GameEntry game)
    {
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
        SetUserName(name);

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
                ActivateSelected();
                e.Handled = true;
                break;

            case VirtualKey.Down when searching:
                MoveSelection(1);
                e.Handled = true;
                break;

            case VirtualKey.Up when searching:
                MoveSelection(-1);
                e.Handled = true;
                break;

            case VirtualKey.PageDown when searching:
                MoveSelection(PageSize());
                e.Handled = true;
                break;

            case VirtualKey.PageUp when searching:
                MoveSelection(-PageSize());
                e.Handled = true;
                break;

            // Home and End jump through the results; Ctrl+Home and Ctrl+End still move the cursor in the text.
            case VirtualKey.Home when searching && !IsKeyDown(VirtualKey.Control):
                MoveSelection(int.MinValue);
                e.Handled = true;
                break;

            case VirtualKey.End when searching && !IsKeyDown(VirtualKey.Control):
                MoveSelection(int.MaxValue);
                e.Handled = true;
                break;

            // Tab and Shift+Tab step between a row's own action and its second one ("Show in folder"). Focus stays
            // in the search box, so they never wander off into the rest of the menu while results are showing.
            case VirtualKey.Tab when searching && ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), SearchBox):
                StepAction(back: IsKeyDown(VirtualKey.Shift));
                e.Handled = true;
                break;
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // ---------- Keyboard and mouse selection in search results ----------

    // The highlighted row is remembered as the result itself, so it stays put when rows are added around it or
    // pictures load. Every search list is in SelectionMode.Single, but only one of them has a selection at a time.
    private ListView? _selectedList;
    private object? _selectedItem;
    private bool _navigated; // the arrow keys or the mouse chose a row since the last typing

    private IEnumerable<ListView> SearchLists =>
        [QuickList, GamesList, ResultsList, SystemList, PathsList, PeopleList, StoreList, BattleNetList, WikiList, WebList];

    /// <summary>Every row the keyboard can reach, top to bottom as they appear. Rows that only show a message are left out.</summary>
    private List<(ListView List, object Item)> NavRows()
    {
        var rows = new List<(ListView List, object Item)>();
        void Add(ListView list, System.Collections.IEnumerable items, Func<object, bool>? usable = null)
        {
            foreach (var item in items)
                if (usable is null || usable(item)) rows.Add((list, item));
        }

        Add(QuickList, _quick, r => r is QuickRow { CopyText: not null } or QuickRow { WebQuery: not null });
        Add(GamesList, _games);
        Add(ResultsList, _results);
        Add(SystemList, _system);
        Add(PathsList, _paths);
        Add(PeopleList, _people, p => p is PersonEntry { CanOpen: true });
        Add(StoreList, _store);
        Add(BattleNetList, _battleNet);
        Add(WikiList, _wiki);
        Add(WebList, _web, r => r is QuickRow { WebQuery: not null });
        return rows;
    }

    private static int IndexOf(List<(ListView List, object Item)> rows, object? item)
    {
        if (item is null) return -1;
        return rows.FindIndex(r => ReferenceEquals(r.Item, item));
    }

    private void SetSelected(ListView? list, object? item)
    {
        if (_selectedItem is PathEntry old && !ReferenceEquals(old, item)) old.SecondaryActive = false;
        foreach (var other in SearchLists)
            if (!ReferenceEquals(other, list) && other.SelectedItem is not null) other.SelectedItem = null;
        if (list is not null && !ReferenceEquals(list.SelectedItem, item)) list.SelectedItem = item;
        _selectedList = list;
        _selectedItem = item;
    }

    /// <summary>
    /// After the rows change: the top row is highlighted until the user moves; after that the chosen row stays
    /// chosen, and falls back to the top row only if it has gone.
    /// </summary>
    private void EnsureSelection()
    {
        var rows = NavRows();
        if (rows.Count == 0) { SetSelected(null, null); return; }
        int at = IndexOf(rows, _selectedItem);
        if (!_navigated && at != 0)
        {
            SetSelected(rows[0].List, rows[0].Item);
            ResultsScroller.ChangeView(null, 0, null, disableAnimation: true);
        }
        else if (_navigated && at < 0 && _selectedItem is not null)
        {
            SetSelected(rows[0].List, rows[0].Item);
            ScrollSelectedIntoView();
        }
    }

    /// <summary>Down/Up by one, PageDown/PageUp by a screenful, Home/End to the ends (int.MinValue / int.MaxValue).</summary>
    private void MoveSelection(int step)
    {
        var rows = NavRows();
        if (rows.Count == 0) return;
        _navigated = true;
        int at = IndexOf(rows, _selectedItem);
        int next;
        if (step == int.MinValue) next = 0;
        else if (step == int.MaxValue) next = rows.Count - 1;
        else if (Math.Abs(step) == 1)
        {
            // One step wraps round at the bottom; at the top it goes back to the search box (no row highlighted).
            if (at < 0) next = step > 0 ? 0 : rows.Count - 1;
            else if (step > 0) next = at == rows.Count - 1 ? 0 : at + 1;
            else next = at == 0 ? -1 : at - 1;
        }
        else next = Math.Clamp((at < 0 ? (step > 0 ? -1 : rows.Count) : at) + step, 0, rows.Count - 1);

        if (next < 0)
        {
            SetSelected(null, null);
            ResultsScroller.ChangeView(null, 0, null, disableAnimation: true);
            return;
        }
        SetSelected(rows[next].List, rows[next].Item);
        ScrollSelectedIntoView(next == 0);
    }

    private int PageSize() => Math.Max(1, (int)(ResultsScroller.ViewportHeight / 56));

    /// <summary>Tab moves to the row's second action if it has one; Shift+Tab goes back to its main action.</summary>
    private void StepAction(bool back)
    {
        if (_selectedItem is PathEntry entry) entry.SecondaryActive = !back;
    }

    private void ActivateSelected()
    {
        var rows = NavRows();
        if (rows.Count == 0) return;
        int at = IndexOf(rows, _selectedItem);
        var item = rows[at < 0 ? 0 : at].Item; // nothing highlighted (Up from the top row): Enter takes the best match
        Activate(item, secondary: item is PathEntry { SecondaryActive: true });
    }

    /// <summary>What Enter does on a row: the same as clicking it, or its second action after Tab.</summary>
    private void Activate(object item, bool secondary)
    {
        switch (item)
        {
            case AppEntry app: Launch(app); break;
            case GameEntry game: OpenGame(game); break;
            case SystemEntry entry: _ = OpenSystemAsync(entry); break;
            case PathEntry path when secondary: ShowInFolder(path); break;
            case PathEntry path: OpenPath(path); break;
            case PersonEntry person: OpenPerson(person); break;
            case WikiEntry article: OpenWiki(article); break;
            case QuickRow { CopyText: not null } answer: CopyAnswer(answer); break;
            case QuickRow web: OpenWeb(web); break;
        }
    }

    /// <summary>
    /// Scrolls the results column to the highlighted row. The lists do not scroll by themselves (the whole column
    /// does). The first row of a section brings its heading along, and the very first row shows the column top.
    /// </summary>
    private void ScrollSelectedIntoView(bool veryFirst = false)
    {
        if (_selectedList is not { } list || _selectedItem is null) return;
        if (veryFirst)
        {
            ResultsScroller.ChangeView(null, 0, null, disableAnimation: true);
            return;
        }
        var row = list.ContainerFromItem(_selectedItem) as UIElement;
        if (row is null)
        {
            list.UpdateLayout();
            row = list.ContainerFromItem(_selectedItem) as UIElement;
            if (row is null) return;
        }
        double headingRoom = list.Items.Count > 0 && ReferenceEquals(list.Items[0], _selectedItem) ? 28 : 0;
        row.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = false,
            TargetRect = new Windows.Foundation.Rect(0, -headingRoom, row.ActualSize.X, row.ActualSize.Y + headingRoom),
        });
    }

    /// <summary>Hovering a row highlights it, so the mouse and the keyboard share one highlight.</summary>
    private void Results_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not ListViewItem container) continue;
            if (ItemsControl.ItemsControlFromItemContainer(container) is not ListView list) return;
            var item = list.ItemFromContainer(container);
            if (item is null || ReferenceEquals(item, _selectedItem)) return;
            if (!NavRows().Any(r => ReferenceEquals(r.Item, item))) return; // a message row: nothing to select
            _navigated = true;
            SetSelected(list, item);
            return;
        }
    }

    /// <summary>"All apps ›" shows the A–Z list; "‹ Back" returns to the pinned apps.</summary>
    private void ViewToggle_Click(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text.Trim().Length > 0) return;
        _showingAllApps = !_showingAllApps;
        AllAppsZoom.IsZoomedInViewActive = true; // always open at the apps, not the letters
        ShowPanel(_showingAllApps ? AllAppsZoom : PinnedPanel);
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

    private void PinnedGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _dragging = false;
        App.Settings.SetPinOrder(_pinned.Select(a => a.Id));
    }

    // ---------- Right-hand panel ----------

    /// <summary>Live preview while dragging; saved when the flyout closes, not on every step.</summary>
    private void GlassSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        _backdrop.Strength = (int)e.NewValue;

    private void GlassFlyout_Closed(object sender, object e)
    {
        if (App.Settings.Current.GlassStrength == _backdrop.Strength) return;
        App.Settings.Current.GlassStrength = _backdrop.Strength;
        App.Settings.Save();
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        HideMenu();
        Launcher.OpenFolder((string)((Button)sender).Tag);
    }

    private void WindowsSettings_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.Open("ms-settings:"); }
    private void ControlPanel_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.Open("control.exe"); }
    private void Run_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.ShowRunDialog(); }
    private void Lock_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.Lock(); }
    private void Sleep_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.Sleep(); }
    private void SignOut_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.SignOut(); }
    private void Restart_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.Restart(); }
    private void ShutDown_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.ShutDown(); }
}
