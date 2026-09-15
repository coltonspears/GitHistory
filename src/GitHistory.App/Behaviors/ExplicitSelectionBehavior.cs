using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GitHistory.App.Converters;

namespace GitHistory.App.Behaviors;

/// <summary>Only explicit mouse or keyboard gestures navigate; filtering and source updates only refresh the highlight.</summary>
public static class ExplicitSelectionBehavior
{
    public static readonly DependencyProperty SelectionCommandProperty = DependencyProperty.RegisterAttached(
        "SelectionCommand", typeof(ICommand), typeof(ExplicitSelectionBehavior), new PropertyMetadata(null, OnSelectionCommandChanged));
    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.RegisterAttached(
        "SelectedItem", typeof(object), typeof(ExplicitSelectionBehavior), new PropertyMetadata(null, OnSelectedItemChanged));

    public static ICommand? GetSelectionCommand(DependencyObject target) => (ICommand?)target.GetValue(SelectionCommandProperty);
    public static void SetSelectionCommand(DependencyObject target, ICommand? value) => target.SetValue(SelectionCommandProperty, value);
    public static object? GetSelectedItem(DependencyObject target) => target.GetValue(SelectedItemProperty);
    public static void SetSelectedItem(DependencyObject target, object? value) => target.SetValue(SelectedItemProperty, value);

    private static void OnSelectionCommandChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement control || control is not (ListBox or TreeView)) return;
        control.PreviewMouseUp -= OnMouseSelection;
        control.KeyUp -= OnKeyboardSelection;
        control.TargetUpdated -= OnTargetUpdated;
        control.Loaded -= OnLoaded;
        if (args.NewValue is not ICommand) return;
        control.PreviewMouseUp += OnMouseSelection;
        control.KeyUp += OnKeyboardSelection;
        control.TargetUpdated += OnTargetUpdated;
        control.Loaded += OnLoaded;
    }

    private static void OnMouseSelection(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Left) return;
        var control = (FrameworkElement)sender;
        // Clicking the tree disclosure toggle only expands it; it does not change the active folder.
        for (var current = args.OriginalSource as DependencyObject; current is not null && current != control; current = Parent(current))
        {
            if (current is ToggleButton) return;
            if (control is ListBox list && current is ListBoxItem listItem && ItemsControl.ItemsControlFromItemContainer(listItem) == list)
            {
                Execute(control, list.ItemContainerGenerator.ItemFromContainer(listItem));
                return;
            }
            if (control is TreeView && current is TreeViewItem treeItem)
            {
                Execute(control, Unwrap(treeItem.Header));
                return;
            }
        }
    }

    private static void OnKeyboardSelection(object sender, KeyEventArgs args)
    {
        if (args.Key is not (Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.Enter or Key.Space)) return;
        var control = (FrameworkElement)sender;
        object? selected = control switch { ListBox list => list.SelectedItem, TreeView tree => Unwrap(tree.SelectedItem), _ => null };
        if (selected is not null) Execute(control, selected);
    }

    private static object? Unwrap(object? item) => item is DirectoryTreeItem folder ? folder.Node : item;

    private static void Execute(DependencyObject control, object? item)
    {
        var command = GetSelectionCommand(control);
        if (item is not null && item != DependencyProperty.UnsetValue && command?.CanExecute(item) is true) command.Execute(item);
    }

    private static void OnSelectedItemChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is TreeView tree) QueueHighlight(tree);
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) => QueueHighlight((FrameworkElement)sender);

    private static void OnTargetUpdated(object? sender, DataTransferEventArgs args)
    {
        if (sender is FrameworkElement control && args.Property == ItemsControl.ItemsSourceProperty) QueueHighlight(control);
    }

    private static void QueueHighlight(FrameworkElement control)
    {
        control.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (control is ListBox list) BindingOperations.GetBindingExpression(list, Selector.SelectedItemProperty)?.UpdateTarget();
            else if (control is TreeView tree)
            {
                tree.UpdateLayout();
                Highlight(tree, GetSelectedItem(tree));
            }
        });
    }

    private static void Highlight(ItemsControl control, object? selected)
    {
        foreach (var item in control.Items)
        {
            if (control.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) continue;
            container.SetCurrentValue(TreeViewItem.IsSelectedProperty, selected is not null && Equals(Unwrap(item), selected));
            if (item is DirectoryTreeItem folder && folder.Children.Any(child => Contains(child, selected)))
            {
                container.SetCurrentValue(TreeViewItem.IsExpandedProperty, true);
                container.UpdateLayout();
            }
            if (container.IsExpanded) Highlight(container, selected);
        }
    }

    private static bool Contains(DirectoryTreeItem item, object? selected) => Equals(item.Node, selected) || item.Children.Any(child => Contains(child, selected));

    private static DependencyObject? Parent(DependencyObject current) => current is Visual
        ? VisualTreeHelper.GetParent(current)
        : LogicalTreeHelper.GetParent(current);
}

/// <summary>Makes row context actions target the row under the pointer, including an unselected row.</summary>
public static class RowContextBehavior
{
    public static readonly DependencyProperty SelectOnRightClickProperty = DependencyProperty.RegisterAttached(
        "SelectOnRightClick", typeof(bool), typeof(RowContextBehavior), new PropertyMetadata(false, OnChanged));
    public static bool GetSelectOnRightClick(DependencyObject target) => (bool)target.GetValue(SelectOnRightClickProperty);
    public static void SetSelectOnRightClick(DependencyObject target, bool value) => target.SetValue(SelectOnRightClickProperty, value);

    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not DataGrid grid) return;
        grid.PreviewMouseDown -= OnRightClick;
        if (args.NewValue is true) grid.PreviewMouseDown += OnRightClick;
    }

    private static void OnRightClick(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Right || args.OriginalSource is not DependencyObject source) return;
        var grid = (DataGrid)sender;
        if (ItemsControl.ContainerFromElement(grid, source) is not DataGridRow row) return;
        grid.SetCurrentValue(Selector.SelectedItemProperty, row.Item);
        grid.CurrentItem = row.Item;
        row.Focus();
    }
}
