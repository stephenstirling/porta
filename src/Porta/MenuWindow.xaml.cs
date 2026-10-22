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
    private const int MenuHeight = 620;
    private const int AvatarOverhang = 36; // half the account picture's circle sticks out above the panel
    private const int RightPaneWidth = 200;
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
    private readonly ObservableCollection<WikiEntry> _wiki = []; // Wikipedia articles, below the store results
    private CancellationTokenSource? _wikiSearch;
    private readonly ObservableCollection<PersonEntry> _people = []; // people from Ocula, below apps, games and tools
    private DateTime _pictureStamp;
    private bool _isOpen;

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
            if (e.WindowActivationState == WindowActivationState.Deactivated && _isOpen) HideMenu();
        };

        PinnedGrid.ItemsSource = _pinned;
        RecentList.ItemsSource = _recent;
        ResultsList.ItemsSource = _results;
        GamesList.ItemsSource = _games;
        StoreList.ItemsSource = _store;
        SystemList.ItemsSource = _system;
        WikiList.ItemsSource = _wiki;
        PeopleList.ItemsSource = _people;
        WikipediaToggle.IsOn = App.Settings.Current.SearchWikipedia;
        WikipediaPicturesToggle.IsOn = App.Settings.Current.WikipediaPictures;
        WikipediaPicturesToggle.IsEnabled = WikipediaToggle.IsOn; // pictures only matter while Wikipedia search is on
        OculaPeopleToggle.IsOn = App.Settings.Current.SearchOculaPeople;

        var name = Native.GetDisplayName();
        UserNameText.Text = name;
        Avatar.DisplayName = name;
        _ = LoadAccountAsync();

        App.Catalog.Loaded += OnCatalogLoaded;
        App.Settings.Restored += () => DispatcherQueue.TryEnqueue(() =>
        {
            RebuildPinned();
            if (App.Catalog.IsLoaded) RebuildAllApps();
            GlassSlider.Value = App.Settings.Current.GlassStrength; // also updates the glass
            WikipediaToggle.IsOn = App.Settings.Current.SearchWikipedia;
            WikipediaPicturesToggle.IsOn = App.Settings.Current.WikipediaPictures;
            WikipediaPicturesToggle.IsEnabled = WikipediaToggle.IsOn;
            if (!App.Settings.Current.WikipediaPictures) DropWikiPictures();
            OculaPeopleToggle.IsOn = App.Settings.Current.SearchOculaPeople;
            if (!OculaPeopleToggle.IsOn) ClearPeople();
        });
        GlassSlider.Value = App.Settings.Current.GlassStrength;
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

        Native.MakeToolWindow(_hwnd); // again before placing: WinUI can restore its frame styles
        PlaceAboveTaskbar();
        AppWindow.Show(true);
        Native.ForceForeground(_hwnd);
        SearchBox.Focus(FocusState.Programmatic);
        _isOpen = true;
    }

    public void HideMenu()
    {
        _confirmDialog?.Hide(); // don't leave a Restart/Shut down question waiting in the hidden menu
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
        int height = (int)((MenuHeight + AvatarOverhang) * scale);
        var work = area.WorkArea;
        int x = work.X + (work.Width - width) / 2;
        int y = work.Y + work.Height - height - (int)(GapAboveTaskbar * scale);

        _scale = scale;
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        CutWindowToPanelAndPicture(scale);
    }

    private double _scale = 1.0; // display scale the menu was last placed at

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

        int radius = (int)Math.Round(38 * scale);
        int cx = dx + client.Right - (int)Math.Round(RightPaneWidth / 2.0 * scale);
        int cy = dy + overhang;
        var circle = Native.CreateEllipticRgn(cx - radius, cy - radius, cx + radius + 1, cy + radius + 1);

        Native.CombineRgn(panel, panel, circle, Native.RGN_OR);
        Native.DeleteObject(circle);
        Native.SetWindowRgn(_hwnd, panel, true); // Windows owns the region from here
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
        _wikiSearch?.Cancel();
        _wiki.Clear();
        WikiSection.Visibility = Visibility.Collapsed;
        _people.Clear();
        PeopleSection.Visibility = Visibility.Collapsed;
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

        // App results, minus the shortcuts for games already shown above.
        _results.Clear();
        foreach (var app in SearchService.Rank(App.Catalog.Apps, query))
        {
            if (games.Any(g => g.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase) || g.Target == app.ShellPath)) continue;
            _results.Add(app);
        }

        // Windows tools, Settings pages and power actions, minus any already listed as an app.
        _system.Clear();
        foreach (var entry in SystemCatalog.Search(query))
        {
            if (_results.Any(a => a.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))) continue;
            _system.Add(entry);
        }

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

        UpdateSearchSections(query);
        if (_results.Count > 0) ResultsList.SelectedIndex = 0;

        if (query.Length >= 3 && App.Settings.Current.SearchSteamStore && App.Games.Launchers.Contains(GameLauncher.Steam))
            _ = AddSteamStoreResultsAsync(query);
        if (query.Length >= 3 && App.Settings.Current.SearchWikipedia)
            _ = AddWikipediaResultsAsync(query);
    }

    private void UpdateSearchSections(string query)
    {
        GamesSection.Visibility = _games.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        SystemSection.Visibility = _system.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PeopleSection.Visibility = _people.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StoreSection.Visibility = _store.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        WikiSection.Visibility = _wiki.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // "Nothing matches" only when every section is empty.
        bool none = _results.Count == 0 && _games.Count == 0 && _system.Count == 0 && _store.Count == 0 && _wiki.Count == 0 && _people.Count == 0;
        NoResultsText.Text = $"Nothing matches “{query}”";
        NoResultsText.Visibility = none ? Visibility.Visible : Visibility.Collapsed;

        var launchers = App.Games.Launchers;
        SteamStoreLink.Visibility = launchers.Contains(GameLauncher.Steam) ? Visibility.Visible : Visibility.Collapsed;
        // Epic and Battle.net open in the browser, so they're offered even when the launcher isn't installed.
        MsStoreLink.Visibility = Visibility.Visible;
        // Offer store searches when the query looks like it might be a game: nothing else answered it,
        // or games already matched.
        StoreLinks.Visibility = query.Length >= 3 && (_games.Count > 0 || (_results.Count == 0 && _system.Count == 0))
            ? Visibility.Visible : Visibility.Collapsed;
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
                _wiki.Add(new WikiEntry { Title = a.Title, Description = a.Description, Url = a.Url, ThumbnailUrl = pictures ? a.ThumbnailUrl : null });
            UpdateSearchSections(query);

            if (pictures)
                foreach (var entry in _wiki.ToList())
                    if (entry.ThumbnailUrl is not null) _ = LoadWikiPictureAsync(entry, cts.Token);
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
        foreach (var entry in _wiki) entry.Icon = null;
        WikiPictureCache.Clear();
    }

    private void WikipediaPicturesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.WikipediaPictures == WikipediaPicturesToggle.IsOn) return;
        App.Settings.Current.WikipediaPictures = WikipediaPicturesToggle.IsOn;
        App.Settings.Save();
        if (!WikipediaPicturesToggle.IsOn) DropWikiPictures(); // no more picture requests; saved pictures deleted
    }

    private void Wiki_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WikiEntry article) return;
        HideMenu();
        Launcher.Open(article.Url);
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
        if (e.ClickedItem is not PersonEntry { CanOpen: true } person) return; // no Ocula, no action
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
        }
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
                Content = "Save your work first. Open apps will close.",
                PrimaryButtonText = entry.Name,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            var answer = await _confirmDialog.ShowAsync();
            _confirmDialog = null;
            if (answer != ContentDialogResult.Primary) return;
        }
        HideMenu();
        Launcher.OpenSystem(entry);
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
                else if (_system.FirstOrDefault() is { } entry) _ = OpenSystemAsync(entry);
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
    private void Run_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.ShowRunDialog(); }
    private void Lock_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.Lock(); }
    private void Sleep_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.Sleep(); }
    private void SignOut_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.SignOut(); }
    private void Restart_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.Restart(); }
    private void ShutDown_Click(object sender, RoutedEventArgs e) { HideMenu(); Launcher.ShutDown(); }
}
