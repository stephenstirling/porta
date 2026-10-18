using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;
using Porta.Models;

namespace Porta.Services;

/// <summary>Loads icons one by one on a background thread so the menu never waits for them.</summary>
public sealed class IconLoader
{
    private readonly BlockingCollection<(IconItem Item, string Path, int Size)> _queue = new();
    private readonly DispatcherQueue _ui;

    public IconLoader(DispatcherQueue ui)
    {
        _ui = ui;
        var worker = new Thread(Work) { IsBackground = true, Name = "Porta icons" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public void Request(IconItem item, string path, int size = 64) => _queue.Add((item, path, size));

    private void Work()
    {
        foreach (var (item, path, size) in _queue.GetConsumingEnumerable())
        {
            var pixels = ShellIcons.GetPixels(path, size);
            if (pixels is not null)
                _ui.TryEnqueue(() =>
                {
                    // A store cover may have arrived while this executable icon was loading.
                    if (item.Icon is null) item.Icon = ShellIcons.ToBitmap(pixels);
                });
        }
    }
}
