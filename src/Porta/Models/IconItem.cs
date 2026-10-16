using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Porta.Models;

/// <summary>Anything shown with an icon that loads in the background.</summary>
public abstract class IconItem : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            OnChanged(nameof(Icon));
            OnChanged(nameof(InitialVisibility));
        }
    }

    /// <summary>The letter placeholder shows until the real icon arrives.</summary>
    public Visibility InitialVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
