using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Porta;

/// <summary>
/// Desktop acrylic tinted to Porta's panel colours (design/Main.dc.html): a deep tint in dark mode,
/// a soft white in light mode, with the wallpaper's colour still showing through.
/// The built-in DesktopAcrylicBackdrop only uses the fixed system tints.
/// </summary>
internal sealed class PanelBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private FrameworkElement? _root;

    // Porta's own configuration instead of GetDefaultSystemBackdropConfiguration: for the menu window the
    // default one kept reporting IsInputActive = false even while the menu was the active foreground
    // window, so acrylic only ever drew its solid fallback colour. The menu hides as soon as it loses
    // focus, so whenever it's visible it is active.
    private readonly SystemBackdropConfiguration _config = new() { IsInputActive = true };

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        // Thin is Windows' lighter, more see-through acrylic recipe.
        _controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
        _controller.SetSystemBackdropConfiguration(_config);
        _controller.AddSystemBackdropTarget(connectedTarget);

        _root = xamlRoot.Content as FrameworkElement;
        if (_root is not null) _root.ActualThemeChanged += OnThemeChanged;
        ApplyTheme();
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        if (_root is not null) _root.ActualThemeChanged -= OnThemeChanged;
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
    }

    private void OnThemeChanged(FrameworkElement sender, object args) => ApplyTheme();

    private int _strength = 30;

    /// <summary>Glass strength, 0 (clearest) to 100 (most frosted). Applies immediately.</summary>
    public int Strength
    {
        get => _strength;
        set { _strength = Math.Clamp(value, 0, 100); ApplyTheme(); }
    }

    private bool IsLight => _root?.ActualTheme == ElementTheme.Light ||
                            (_root is null && Application.Current.RequestedTheme == ApplicationTheme.Light);

    /// <summary>
    /// Our own configuration doesn't follow the theme by itself, and custom tints replace the theme
    /// defaults, so both are set again whenever the theme changes.
    /// </summary>
    private void ApplyTheme()
    {
        _config.Theme = IsLight ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
        if (_controller is null) return;

        // Tint and luminosity rise together with strength. Even at 0 there's enough tint for text
        // to stay readable over a bright wallpaper; at 100 the wallpaper is only a soft colour cast.
        float s = _strength / 100f;
        if (IsLight)
        {
            _controller.TintColor = Color.FromArgb(255, 0xF7, 0xF7, 0xFA);
            _controller.TintOpacity = 0.15f + 0.55f * s;
            _controller.LuminosityOpacity = 0.40f + 0.50f * s;
            _controller.FallbackColor = Color.FromArgb(255, 0xF3, 0xF3, 0xF6);
        }
        else
        {
            _controller.TintColor = Color.FromArgb(255, 0x12, 0x14, 0x1C);
            _controller.TintOpacity = 0.15f + 0.60f * s;
            _controller.LuminosityOpacity = 0.35f + 0.55f * s;
            _controller.FallbackColor = Color.FromArgb(255, 0x17, 0x19, 0x22);
        }
    }
}
