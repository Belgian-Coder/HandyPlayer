using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HandyPlaylistPlayer.App;

public class ViewLocator : IDataTemplate
{
    // Cache views so stateful controls survive navigation
    private readonly ConcurrentDictionary<Type, Control> _cache = new();

    public Control? Build(object? param)
    {
        if (param is null) return null;

        var vmType = param.GetType();
        if (_cache.TryGetValue(vmType, out var cached))
            return cached;

        var name = vmType.FullName!
            .Replace("ViewModels", "Views")
            .Replace("ViewModel", "View");

        var type = Type.GetType(name);

        if (type != null)
        {
            var control = (Control)Activator.CreateInstance(type)!;
            _cache[vmType] = control;
            return control;
        }

        return new TextBlock { Text = "View not found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ObservableObject;
    }
}
