using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Porta.Interop;
using Porta.Services;

namespace Porta;

public partial class App : Application
{
    public static SettingsStore Settings { get; } = new();
    public static AppCatalog Catalog { get; } = new();
    public static IconLoader Icons { get; private set; } = null!;
    public static Services.Games.GameLibrary Games { get; } = new();

    /// <summary>Where "Buy me a coffee" links go, in the tray menu and (from milestone 2) Settings › About.</summary>
    public const string DonateUrl = "https://buymeacoffee.com/stephenstirling";

    private static App? _current;
    private MenuWindow? _menu;
    private KeyboardHook? _hook;
    private TaskbarIcon? _tray;
    private Windows.UI.ViewManagement.UISettings? _uiSettings; // kept alive so theme-change events keep firing

    public App()
    {
        InitializeComponent();
        _current = this;
        // Porta lives in the background; hiding the menu must not quit the app.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Settings.Load();
        Icons = new IconLoader(DispatcherQueue.GetForCurrentThread());

        _menu = new MenuWindow();

        _hook = new KeyboardHook();
        _hook.WinKeyTapped += () => _menu.DispatcherQueue.TryEnqueue(() =>
        {
            if (Settings.Current.OpenWithWinKey) _menu.Toggle();
        });
        _hook.ShouldIntercept = () => Settings.Current.OpenWithWinKey;
        _hook.Install();

        CreateTrayIcon();

        await Catalog.LoadAsync();
        Games.RefreshIfStale(Catalog.Apps);
    }

    /// <summary>Called when the user starts Porta again while it is already running.</summary>
    public static void ShowMenuFromAnotherLaunch() =>
        _current?._menu?.DispatcherQueue.TryEnqueue(() => _current._menu.ShowMenu());

    private void CreateTrayIcon()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = "Open Porta", Command = new RelayCommand(() => _menu?.ShowMenu()) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Back up settings…", Command = new RelayCommand(() => _ = BackUpSettingsAsync()) });
        menu.Items.Add(new MenuFlyoutItem { Text = "Restore settings…", Command = new RelayCommand(() => _ = RestoreSettingsAsync()) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Buy me a coffee",
            Command = new RelayCommand(() => Launcher.Open(DonateUrl)),
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Exit Porta", Command = new RelayCommand(ExitPorta) });

        _tray = new TaskbarIcon
        {
            ToolTipText = "Porta",
            IconSource = TrayIconForTheme(),
            LeftClickCommand = new RelayCommand(() => _menu?.Toggle()),
            NoLeftClickDelay = true,
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
        };
        _tray.ForceCreate();

        // The tray icon is single-colour: white on a dark taskbar, black on a light one.
        _uiSettings = new Windows.UI.ViewManagement.UISettings();
        _uiSettings.ColorValuesChanged += (_, _) => _menu?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_tray is not null) _tray.IconSource = TrayIconForTheme();
        });
    }

    private static BitmapImage TrayIconForTheme()
    {
        var file = TaskbarIsLight() ? "PortaTrayBlack.ico" : "PortaTrayWhite.ico";
        return new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", file)));
    }

    /// <summary>The taskbar follows Windows mode (not app mode), stored per user in the registry.</summary>
    private static bool TaskbarIsLight()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
    }

    // ---------- Backup and restore (moves to Settings › General once that window exists) ----------

    private async Task BackUpSettingsAsync()
    {
        if (_menu is null) return;
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"Porta settings {DateTime.Now:yyyy-MM-dd}",
        };
        picker.FileTypeChoices.Add("Porta settings backup", [".portabackup"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _menu.Hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            Settings.ExportTo(file.Path);
            Notify("Settings backed up", file.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify("Couldn't back up settings", ex.Message);
        }
    }

    private async Task RestoreSettingsAsync()
    {
        if (_menu is null) return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(".portabackup");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _menu.Hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            Settings.ImportFrom(file.Path);
            Notify("Settings restored", "Your previous settings were kept as settings.before-restore.json.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Notify("Couldn't restore settings", ex.Message);
        }
    }

    private void Notify(string title, string message) =>
        _tray?.ShowNotification(title, message);

    private void ExitPorta()
    {
        _hook?.Dispose();
        _tray?.Dispose();
        Exit();
    }
}
