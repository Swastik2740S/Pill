namespace DynamicIsland.Core.Settings;

public enum SettingKind
{
    /// <summary>A live value (read only).</summary>
    Info,
    /// <summary>An on/off switch that really changes something.</summary>
    Toggle,
    /// <summary>A 0..1 level (volume, brightness).</summary>
    Slider,
    /// <summary>One of a few options.</summary>
    Choice,
    /// <summary>Opens a Windows Settings page (from the allowlist).</summary>
    Link,
    /// <summary>A one-off action (clear history, open folder).</summary>
    Action,
    /// <summary>An editable list of apps (the Home card's quick apps). Payload: QuickAppsModel.</summary>
    AppList,
}

/// <summary>One option of a <see cref="SettingKind.Choice"/> row.</summary>
public sealed class ChoiceOption : ObservableObject
{
    private bool _isSelected;

    public ChoiceOption(SettingItem owner, int index, string text)
    {
        Owner = owner;
        Index = index;
        Text = text;
    }

    public SettingItem Owner { get; }
    public int Index { get; }
    public string Text { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>
/// One row in Settings. Rows hold no state of their own: every value is read from the system
/// (or the island's settings) through a delegate, and every change goes back the same way.
/// <see cref="Refresh"/> re-reads after a change or when the system reports one.
/// </summary>
public sealed class SettingItem : ObservableObject
{
    private IReadOnlyList<ChoiceOption>? _choices;
    private bool _isHighlighted;

    public SettingItem(string id, SettingKind kind, string title)
    {
        Id = id;
        Kind = kind;
        Title = title;
    }

    public string Id { get; }
    public SettingKind Kind { get; }
    public string Title { get; }
    public string Description { get; init; } = "";
    /// <summary>Extra words for search ("screen" finds brightness).</summary>
    public string Keywords { get; init; } = "";
    /// <summary>Small tag next to the title: "Read only", "Unofficial API", ...</summary>
    public string Note { get; init; } = "";

    public Func<string>? Read { get; init; }
    public Func<bool>? GetOn { get; init; }
    public Action<bool>? SetOn { get; init; }
    public Func<double>? GetLevel { get; init; }
    public Action<double>? SetLevel { get; init; }
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
    public Func<int>? GetChoice { get; init; }
    /// <summary>Applies an option; returns an error to show, or null on success.</summary>
    public Func<int, string?>? SetChoice { get; init; }
    public string? LinkKey { get; init; }
    public string ActionLabel { get; init; } = "";
    public Action? Run { get; init; }
    /// <summary>Model for rows that need more than a value (AppList: the quick apps).</summary>
    public object? Payload { get; init; }
    /// <summary>Why the control can't act right now (null when it can).</summary>
    public Func<string?>? Unavailable { get; init; }

    // Filled in by the catalog.
    /// <summary>Segoe Fluent glyph shown in the row's icon tile (falls back to the category's).</summary>
    public string Glyph { get; internal set; } = "";
    public string Category { get; internal set; } = "";
    public string CategoryKey { get; internal set; } = "";
    public string Section { get; internal set; } = "";

    public bool HasDescription => Description.Length > 0;
    public bool HasNote => Note.Length > 0;

    public string Value => Safe(() => Read?.Invoke() ?? "", "Not available");

    public bool IsOn
    {
        get => Safe(() => GetOn?.Invoke() ?? false, false);
        set
        {
            try
            {
                SetOn?.Invoke(value);
            }
            catch (Exception ex)
            {
                Log.Warn($"Setting '{Title}' failed", ex);
            }
            Refresh();
        }
    }

    public double Level => Safe(() => GetLevel?.Invoke() ?? 0, 0);

    public void ApplyLevel(double level)
    {
        try
        {
            SetLevel?.Invoke(Math.Clamp(level, 0, 1));
        }
        catch (Exception ex)
        {
            Log.Warn($"Setting '{Title}' failed", ex);
        }
        OnPropertyChanged(nameof(Value));
    }

    public IReadOnlyList<ChoiceOption> Choices
    {
        get
        {
            if (_choices is null)
            {
                int selected = Safe(() => GetChoice?.Invoke() ?? -1, -1);
                _choices = Options.Select((text, i) => new ChoiceOption(this, i, text) { IsSelected = i == selected }).ToList();
            }
            return _choices;
        }
    }

    private string? _choiceError;
    public string? ChoiceError { get => _choiceError; private set => Set(ref _choiceError, value); }

    public void Choose(int index)
    {
        string? error = null;
        try
        {
            error = SetChoice?.Invoke(index);
        }
        catch (Exception ex)
        {
            Log.Warn($"Setting '{Title}' failed", ex);
            error = "That didn't work. Details are in the log.";
        }
        ChoiceError = error;
        Refresh();
    }

    public string? UnavailableReason => Safe(() => Unavailable?.Invoke(), null);
    public bool IsAvailable => UnavailableReason is null;

    /// <summary>Briefly true after search jumps to this row.</summary>
    public bool IsHighlighted { get => _isHighlighted; set => Set(ref _isHighlighted, value); }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(Level));
        OnPropertyChanged(nameof(UnavailableReason));
        OnPropertyChanged(nameof(IsAvailable));
        if (_choices is not null)
        {
            int selected = Safe(() => GetChoice?.Invoke() ?? -1, -1);
            foreach (var choice in _choices)
            {
                choice.IsSelected = choice.Index == selected;
            }
        }
    }

    /// <summary>True when every word of the query appears in the title, keywords or place.</summary>
    public bool Matches(IReadOnlyList<string> words)
    {
        string haystack = $"{Title} {Keywords} {Description} {Section} {Category}".ToLowerInvariant();
        return words.All(w => haystack.Contains(w, StringComparison.Ordinal));
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Log.Warn("Reading a setting failed", ex);
            return fallback;
        }
    }
}
