using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TolkIsland.Services;

public sealed record ShelfItem(string Path, string Name, bool Exists)
{
    public static ShelfItem FromPath(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = path;
        }

        var local = ShelfPaths.IsLocal(path);
        return new ShelfItem(path, name, local && (File.Exists(path) || Directory.Exists(path)));
    }
}

internal static class ShelfPaths
{
    public static bool IsLocal(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var root = Path.GetPathRoot(path);
        return root is { Length: >= 2 } && char.IsLetter(root[0]) && root[1] == ':';
    }
}

public sealed class ShelfService
{
    private readonly object _sync = new();
    private readonly List<string> _paths;

    public ShelfService()
    {
        _paths = SettingsService.Current.ShelfFiles
            .Where(path => !string.IsNullOrWhiteSpace(path) && ShelfPaths.IsLocal(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    public event Action? Changed;

    public IReadOnlyList<ShelfItem> Items
    {
        get
        {
            lock (_sync)
            {
                return _paths.Select(ShelfItem.FromPath).ToList();
            }
        }
    }

    public bool IsEmpty
    {
        get
        {
            lock (_sync)
            {
                return _paths.Count == 0;
            }
        }
    }

    public int Add(IEnumerable<string> paths)
    {
        var added = 0;
        List<string>? snapshot = null;

        lock (_sync)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)
                    || !ShelfPaths.IsLocal(path)
                    || !(File.Exists(path) || Directory.Exists(path))
                    || _paths.Contains(path, StringComparer.OrdinalIgnoreCase)
                    || _paths.Count >= 12)
                {
                    continue;
                }

                _paths.Add(path);
                added++;
            }

            if (added > 0)
            {
                snapshot = _paths.ToList();
            }
        }

        if (snapshot is not null)
        {
            Persist(snapshot);
            Changed?.Invoke();
        }

        return added;
    }

    public void Remove(string path)
    {
        List<string>? snapshot = null;

        lock (_sync)
        {
            var index = _paths.FindIndex(item =>
                string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _paths.RemoveAt(index);
                snapshot = _paths.ToList();
            }
        }

        if (snapshot is null)
        {
            return;
        }

        Persist(snapshot);
        Changed?.Invoke();
    }

    public void Clear()
    {
        List<string>? snapshot = null;

        lock (_sync)
        {
            if (_paths.Count > 0)
            {
                _paths.Clear();
                snapshot = new List<string>();
            }
        }

        if (snapshot is null)
        {
            return;
        }

        Persist(snapshot);
        Changed?.Invoke();
    }

    private static void Persist(List<string> paths)
        => SettingsService.Update(settings => settings.ShelfFiles = paths);
}
