using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Controls;
using DynamicIsland.Core;
using DynamicIsland.Core.Settings;
using DynamicIsland.Services;

namespace DynamicIsland.Views;

/// <summary>Picks the row template for a setting by its kind.</summary>
public sealed class SettingRowSelector : DataTemplateSelector
{
    public DataTemplate? Info { get; set; }
    public DataTemplate? Toggle { get; set; }
    public DataTemplate? Slider { get; set; }
    public DataTemplate? Choice { get; set; }
    public DataTemplate? Link { get; set; }
    public DataTemplate? Action { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item is SettingItem setting
        ? setting.Kind switch
        {
            SettingKind.Toggle => Toggle,
            SettingKind.Slider => Slider,
            SettingKind.Choice => Choice,
            SettingKind.Link => Link,
            SettingKind.Action => Action,
            _ => Info,
        }
        : base.SelectTemplate(item, container);
}

/// <summary>Visible when false (search placeholder).</summary>
public sealed class InvertedBoolToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SettingsModel old)
            {
                old.Navigated -= OnNavigated;
            }
            if (e.NewValue is SettingsModel model)
            {
                model.Navigated += OnNavigated;
            }
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Close Settings. True: go back to the dashboard; false: collapse the island.</summary>
    public event Action<bool>? CloseRequested;

    private SettingsModel? Model => DataContext as SettingsModel;

    /// <summary>Put the caret in search; waits until the panel is actually on screen.</summary>
    public void FocusSearch()
    {
        if (SearchBox.IsVisible)
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            return;
        }
        DependencyPropertyChangedEventHandler? handler = null;
        handler = (_, _) =>
        {
            if (!SearchBox.IsVisible)
            {
                return;
            }
            SearchBox.IsVisibleChanged -= handler;
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        };
        SearchBox.IsVisibleChanged += handler;
    }

    public void SetStatus(string text) => StatusLine.Text = text;

    // ------------------------------------------------------------ navigation

    private void OnNavigated(SettingItem? row)
    {
        PageScroll.ScrollToTop();
        if (row is null)
        {
            return;
        }
        // Wait for the page to be laid out, then bring the row into view and flash it.
        Dispatcher.InvokeAsync(() =>
        {
            FindRowElement(PageSections, row)?.BringIntoView();
            row.IsHighlighted = true;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                row.IsHighlighted = false;
            };
            timer.Start();
        }, DispatcherPriority.Loaded);
    }

    private static FrameworkElement? FindRowElement(DependencyObject parent, SettingItem row)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ContentPresenter { Content: SettingItem item } presenter && ReferenceEquals(item, row))
            {
                return presenter;
            }
            if (FindRowElement(child, row) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var model = Model;
        if (model is null)
        {
            return;
        }
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (!model.Back())
            {
                CloseRequested?.Invoke(false);
            }
        }
        else if (ctrl && (e.Key == Key.Up || e.Key == Key.Down))
        {
            e.Handled = true;
            var list = model.Categories;
            int index = list.ToList().IndexOf(model.Current);
            index = Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, list.Count - 1);
            model.Navigate(list[index].Key);
        }
        else if (ctrl && e.Key == Key.F)
        {
            e.Handled = true;
            FocusSearch();
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && !model.Back())
        {
            CloseRequested?.Invoke(true); // nowhere left to go back to: return to the dashboard
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(false);

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model)
        {
            model.Query = "";
        }
        FocusSearch();
    }

    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SettingsCategory category })
        {
            Model?.Navigate(category.Key);
        }
    }

    private void Section_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SettingsSection { IsSearchResult: true } section })
        {
            Model?.Navigate(section.CategoryKey, section.Items.FirstOrDefault()?.Id);
        }
    }

    // ------------------------------------------------------------ rows

    private void Choice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChoiceOption option })
        {
            option.Owner.Choose(option.Index);
        }
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SettingItem { LinkKey: { } key } })
        {
            SettingsLinks.Open(key);
        }
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SettingItem item })
        {
            return;
        }
        try
        {
            item.Run?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"'{item.Title}' failed", ex);
        }
        Model?.RefreshVisible();
    }

    private void Slider_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not SlimSlider slider || slider.Tag is "wired")
        {
            return;
        }
        slider.Tag = "wired";
        slider.WheelStep = 0.02;
        slider.ValueChanging += level => (slider.DataContext as SettingItem)?.ApplyLevel(level);
        slider.DragCompleted += level => (slider.DataContext as SettingItem)?.ApplyLevel(level);
    }
}
