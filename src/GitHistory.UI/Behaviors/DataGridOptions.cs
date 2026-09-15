using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace GitHistory.UI.Behaviors;

/// <summary>Adds a reusable column chooser and grouping menu to native DataGrid headers.</summary>
public static class DataGridOptions
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(DataGridOptions), new PropertyMetadata(false, OnChanged));
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not DataGrid grid) return;
        grid.ContextMenuOpening -= OnContextMenuOpening;
        if (args.NewValue is true) grid.ContextMenuOpening += OnContextMenuOpening;
    }

    private static void OnContextMenuOpening(object sender, ContextMenuEventArgs args)
    {
        var grid = (DataGrid)sender;
        var header = FindHeader(args.OriginalSource as DependencyObject, grid);
        if (header?.Column is not { } column) return;
        args.Handled = true;
        var menu = CreateMenu(grid, column);
        menu.PlacementTarget = header;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Creates a current snapshot of the options, also usable by a toolbar or other menu host.</summary>
    public static ContextMenu CreateMenu(DataGrid grid, DataGridColumn column)
    {
        var menu = new ContextMenu();
        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        var group = new MenuItem
        {
            Header = $"Group by {column.Header}",
            IsEnabled = view?.CanGroup is true && !string.IsNullOrWhiteSpace(column.SortMemberPath),
            IsCheckable = true,
            IsChecked = view?.GroupDescriptions.OfType<PropertyGroupDescription>().Any(item => item.PropertyName == column.SortMemberPath) is true
        };
        group.Click += (_, _) =>
        {
            if (view is not { CanGroup: true }) return;
            using (view.DeferRefresh())
            {
                view.GroupDescriptions.Clear();
                if (group.IsChecked) view.GroupDescriptions.Add(new PropertyGroupDescription(column.SortMemberPath));
            }
        };
        menu.Items.Add(group);
        var clear = new MenuItem { Header = "Clear grouping", IsEnabled = view?.GroupDescriptions.Count > 0 };
        clear.Click += (_, _) => view?.GroupDescriptions.Clear();
        menu.Items.Add(clear);
        menu.Items.Add(new Separator());
        var chooser = new MenuItem { Header = "Columns" };
        foreach (var candidate in grid.Columns.OrderBy(item => item.DisplayIndex))
        {
            if (candidate.Header is null) continue;
            var item = new MenuItem
            {
                Header = candidate.Header,
                IsCheckable = true,
                IsChecked = candidate.Visibility == Visibility.Visible,
                StaysOpenOnClick = true
            };
            item.Click += (_, _) =>
            {
                if (!item.IsChecked && grid.Columns.Count(entry => entry.Visibility == Visibility.Visible) <= 1)
                {
                    item.IsChecked = true;
                    return;
                }
                candidate.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            };
            chooser.Items.Add(item);
        }
        menu.Items.Add(chooser);
        return menu;
    }

    private static DataGridColumnHeader? FindHeader(DependencyObject? source, DependencyObject grid)
    {
        for (var current = source; current is not null && current != grid; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is DataGridColumnHeader header) return header;
        return null;
    }
}
