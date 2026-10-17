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

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
        _controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot));
        _controller.AddSystemBackdropTarget(connectedTarget);

        _root = xamlRoot.Content as FrameworkElement;
        if (_root is not null) _root.ActualThemeChanged += OnThemeChanged;
        ApplyTint();
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        if (_root is not null) _root.ActualThemeChanged -= OnThemeChanged;
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
    }

    private void OnThemeChanged(FrameworkElement sender, object args) => ApplyTint();

    /// <summary>Custom tints replace the theme defaults, so they're set again whenever the theme changes.</summary>
    private void ApplyTint()
    {
        if (_controller is null) return;
        bool light = _root?.ActualTheme == ElementTheme.Light ||
                     (_root is null && Application.Current.RequestedTheme == ApplicationTheme.Light);
        if (light)
        {
            _controller.TintColor = Color.FromArgb(255, 0xF7, 0xF7, 0xFA);
            _controller.TintOpacity = 0.20f;
            _controller.LuminosityOpacity = 0.62f;
            _controller.FallbackColor = Color.FromArgb(255, 0xF3, 0xF3, 0xF6);
        }
        else
        {
            _controller.TintColor = Color.FromArgb(255, 0x12, 0x14, 0x1C);
            // Low tint and luminosity let more wallpaper colour through; white text stays readable.
            _controller.TintOpacity = 0.25f;
            _controller.LuminosityOpacity = 0.55f;
            _controller.FallbackColor = Color.FromArgb(255, 0x17, 0x19, 0x22);
        }
    }
}
