using System.Collections.ObjectModel;
using System.Windows.Media;

namespace DynamicIsland.Core.Settings;

/// <summary>A card of rows on a settings page.</summary>
public sealed class SettingsSection
{
    public SettingsSection(string title, IReadOnlyList<SettingItem> items, string categoryKey = "", bool isSearchResult = false)
    {
        Title = title;
        Items = items;
        CategoryKey = categoryKey;
        IsSearchResult = isSearchResult;
    }

    public string Title { get; }
    public IReadOnlyList<SettingItem> Items { get; }
    public string CategoryKey { get; }
    /// <summary>Search results show "Category › Section" and the header jumps to the page.</summary>
    public bool IsSearchResult { get; }
}

/// <summary>One entry of the sidebar.</summary>
public sealed class SettingsCategory : ObservableObject
{
    private bool _isSelected;

    // One colour per category, used only for its sidebar tile and page header icon.
    private static readonly Dictionary<string, string> Tints = new()
    {
        ["overview"] = "#FF636366",
        ["connectivity"] = "#FF0A84FF",
        ["sound"] = "#FFFF375F",
        ["display"] = "#FF5E5CE6",
        ["power"] = "#FF30D158",
        ["notifications"] = "#FFFF453A",
        ["personalization"] = "#FFBF5AF2",
        ["applications"] = "#FFFF9F0A",
        ["privacy"] = "#FF40C8E0",
        ["accessibility"] = "#FF0A84FF",
        ["updates"] = "#FF34C759",
        ["system"] = "#FF8E8E93",
    };

    public SettingsCategory(string key, string title, string glyph, string description, IReadOnlyList<SettingsSection> sections,
        IReadOnlyList<SettingItem>? tiles = null)
    {
        Key = key;
        Title = title;
        Glyph = glyph;
        Description = description;
        Sections = sections;
        Tiles = tiles ?? Array.Empty<SettingItem>();
        var tint = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Tints.GetValueOrDefault(key, "#FF8E8E93")));
        tint.Freeze();
        Tint = tint;
        foreach (var section in sections)
        {
            foreach (var item in section.Items)
            {
                item.Category = title;
                item.CategoryKey = key;
                item.Section = section.Title;
                if (item.Glyph.Length == 0)
                {
                    item.Glyph = glyph;
                }
            }
        }
        foreach (var tile in Tiles)
        {
            tile.Category = title;
            tile.CategoryKey = key;
            if (tile.Glyph.Length == 0)
            {
                tile.Glyph = glyph;
            }
        }
    }

    /// <summary>The category's colour (sidebar tile and page header only).</summary>
    public Brush Tint { get; }

    /// <summary>Big status tiles at the top of the page (Overview).</summary>
    public IReadOnlyList<SettingItem> Tiles { get; }

    public string Key { get; }
    public string Title { get; }
    public string Glyph { get; }
    public string Description { get; }
    public IReadOnlyList<SettingsSection> Sections { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>
/// Navigation and search for the Settings panel: which page is open, the back stack, and the
/// rows currently on screen (a page, or search results rendered as live controls).
/// </summary>
public sealed class SettingsModel : ObservableObject
{
    private readonly Stack<string> _history = new();
    private SettingsCategory _current;
    private string _query = "";
    private IReadOnlyList<SettingsSection> _visible = Array.Empty<SettingsSection>();

    public SettingsModel(IReadOnlyList<SettingsCategory> categories)
    {
        Categories = categories;
        _current = categories[0];
        _current.IsSelected = true;
        UpdateVisible();
    }

    public IReadOnlyList<SettingsCategory> Categories { get; }

    public SettingsCategory Current => _current;

    public string PageTitle => IsSearching ? "Search" : _current.Title;

    public string PageDescription => IsSearching
        ? (VisibleSections.Count == 0 ? $"Nothing matches “{_query.Trim()}”." : $"Results for “{_query.Trim()}”")
        : _current.Description;

    public bool IsSearching => _query.Trim().Length > 0;

    public bool CanGoBack => IsSearching || _history.Count > 0;

    /// <summary>Status tiles of the open page (none while searching).</summary>
    public IReadOnlyList<SettingItem> Tiles => IsSearching ? Array.Empty<SettingItem>() : _current.Tiles;

    public bool HasTiles => Tiles.Count > 0;

    public IReadOnlyList<SettingsSection> VisibleSections
    {
        get => _visible;
        private set => Set(ref _visible, value);
    }

    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value ?? ""))
            {
                UpdateVisible();
            }
        }
    }

    /// <summary>Raised after the page changes, so the view can scroll to the top or a row.</summary>
    public event Action<SettingItem?>? Navigated;

    public void Navigate(string categoryKey, string? itemId = null)
    {
        var target = Categories.FirstOrDefault(c => c.Key == categoryKey);
        if (target is null)
        {
            return;
        }
        if (!ReferenceEquals(target, _current))
        {
            _history.Push(_current.Key);
            _current.IsSelected = false;
            _current = target;
            _current.IsSelected = true;
        }
        _query = "";
        OnPropertyChanged(nameof(Query));
        UpdateVisible();

        SettingItem? row = itemId is null ? null
            : _current.Sections.SelectMany(s => s.Items).FirstOrDefault(i => i.Id == itemId);
        Navigated?.Invoke(row);
    }

    /// <summary>Clears the search, or returns to the previous page. False when there is nowhere to go.</summary>
    public bool Back()
    {
        if (IsSearching)
        {
            Query = "";
            return true;
        }
        if (_history.Count == 0)
        {
            return false;
        }
        string key = _history.Pop();
        var target = Categories.First(c => c.Key == key);
        _current.IsSelected = false;
        _current = target;
        _current.IsSelected = true;
        UpdateVisible();
        Navigated?.Invoke(null);
        return true;
    }

    /// <summary>Start fresh on the Overview page (each time Settings opens).</summary>
    public void Reset()
    {
        _history.Clear();
        _current.IsSelected = false;
        _current = Categories[0];
        _current.IsSelected = true;
        _query = "";
        OnPropertyChanged(nameof(Query));
        UpdateVisible();
        Navigated?.Invoke(null);
    }

    /// <summary>Re-read every row on screen (called on system changes and on a slow tick).</summary>
    public void RefreshVisible()
    {
        foreach (var item in VisibleSections.SelectMany(s => s.Items).Concat(Tiles))
        {
            item.Refresh();
        }
    }

    private void UpdateVisible()
    {
        if (IsSearching)
        {
            var words = _query.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            VisibleSections = Categories
                .SelectMany(c => c.Sections.Select(s => (Category: c, Section: s)))
                .Select(x => new SettingsSection(
                    $"{x.Category.Title} › {x.Section.Title}",
                    x.Section.Items.Where(i => i.Matches(words)).ToList(),
                    x.Category.Key,
                    isSearchResult: true))
                .Where(s => s.Items.Count > 0)
                .ToList();
        }
        else
        {
            VisibleSections = _current.Sections;
        }
        RefreshVisible();
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageDescription));
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(Tiles));
        OnPropertyChanged(nameof(HasTiles));
    }
}
