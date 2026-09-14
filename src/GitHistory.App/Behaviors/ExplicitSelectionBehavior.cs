using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Threading;
using DevExpress.Xpf.Accordion;
using DevExpress.Xpf.Grid;

namespace GitHistory.App.Behaviors;

/// <summary>Separates deliberate navigation from the selection coercion controls perform when their source changes.</summary>
public static class ExplicitSelectionBehavior
{
    public static readonly DependencyProperty SelectionCommandProperty = DependencyProperty.RegisterAttached(
        "SelectionCommand", typeof(ICommand), typeof(ExplicitSelectionBehavior), new PropertyMetadata(null, OnSelectionCommandChanged));

    public static ICommand? GetSelectionCommand(DependencyObject target) => (ICommand?)target.GetValue(SelectionCommandProperty);
    public static void SetSelectionCommand(DependencyObject target, ICommand? value) => target.SetValue(SelectionCommandProperty, value);

    private static void OnSelectionCommandChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is TreeListControl tree)
        {
            tree.PreviewMouseUp -= OnTreeMouseSelection;
            tree.KeyUp -= OnTreeKeyboardSelection;
            tree.TargetUpdated -= OnTargetUpdated;
            if (args.NewValue is not ICommand) return;
            tree.PreviewMouseUp += OnTreeMouseSelection;
            tree.KeyUp += OnTreeKeyboardSelection;
            tree.TargetUpdated += OnTargetUpdated;
            return;
        }
        if (target is not AccordionControl control) return;
        control.PreviewMouseUp -= OnMouseSelection;
        control.KeyUp -= OnKeyboardSelection;
        control.TargetUpdated -= OnTargetUpdated;
        if (args.NewValue is not ICommand) return;
        control.PreviewMouseUp += OnMouseSelection;
        control.KeyUp += OnKeyboardSelection;
        control.TargetUpdated += OnTargetUpdated;
    }

    private static void OnMouseSelection(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Left) return;
        var control = (AccordionControl)sender;
        for (var current = args.OriginalSource as DependencyObject; current is not null && current != control; current = Parent(current))
        {
            if (current is not FrameworkElement { DataContext: { } item } || !control.Items.Contains(item)) continue;
            Execute(control, item);
            return;
        }
    }

    private static void OnKeyboardSelection(object sender, KeyEventArgs args)
    {
        if (args.Key is not (Key.Up or Key.Down or Key.Home or Key.End or Key.Enter or Key.Space)) return;
        var control = (AccordionControl)sender;
        if (control.SelectedItem is { } item) Execute(control, item);
    }

    private static void OnTreeMouseSelection(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Left) return;
        var tree = (TreeListControl)sender;
        if (tree.View is not TreeListView view || args.OriginalSource is not DependencyObject source) return;
        var hit = view.CalcHitInfo(source);
        if (hit.RowHandle >= 0 && tree.GetRow(hit.RowHandle) is { } item) Execute(tree, item);
    }

    private static void OnTreeKeyboardSelection(object sender, KeyEventArgs args)
    {
        if (args.Key is not (Key.Up or Key.Down or Key.Home or Key.End or Key.Enter or Key.Space)) return;
        var tree = (TreeListControl)sender;
        if (tree.SelectedItem is { } item) Execute(tree, item);
    }

    private static void Execute(DependencyObject control, object item)
    {
        var command = GetSelectionCommand(control);
        if (command?.CanExecute(item) is true) command.Execute(item);
    }

    private static void OnTargetUpdated(object? sender, DataTransferEventArgs args)
    {
        if (sender is not FrameworkElement control || args.Property.Name != "ItemsSource") return;
        // Reapply the authoritative selection after the control has regenerated filtered item containers.
        // This updates only the binding target and never runs the navigation command.
        control.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (control is AccordionControl accordion) BindingOperations.GetBindingExpression(accordion, AccordionControl.SelectedItemProperty)?.UpdateTarget();
            else if (control is TreeListControl tree) BindingOperations.GetBindingExpression(tree, TreeListControl.SelectedItemProperty)?.UpdateTarget();
        });
    }

    private static DependencyObject? Parent(DependencyObject current) => current is Visual
        ? VisualTreeHelper.GetParent(current)
        : LogicalTreeHelper.GetParent(current);
}

/// <summary>Makes row context menus act on the row beneath the pointer, including when it was not selected.</summary>
public static class RowContextBehavior
{
    public static readonly DependencyProperty SelectOnRightClickProperty = DependencyProperty.RegisterAttached(
        "SelectOnRightClick", typeof(bool), typeof(RowContextBehavior), new PropertyMetadata(false, OnChanged));
    public static bool GetSelectOnRightClick(DependencyObject target) => (bool)target.GetValue(SelectOnRightClickProperty);
    public static void SetSelectOnRightClick(DependencyObject target, bool value) => target.SetValue(SelectOnRightClickProperty, value);

    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not GridControl grid) return;
        grid.PreviewMouseDown -= OnRightClick;
        if (args.NewValue is true) grid.PreviewMouseDown += OnRightClick;
    }

    private static void OnRightClick(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Right) return;
        var grid = (GridControl)sender;
        if (grid.View is not TableView view || args.OriginalSource is not DependencyObject source) return;
        var hit = view.CalcHitInfo(source);
        if (hit.RowHandle < 0) return;
        if (grid.GetRow(hit.RowHandle) is not { } row) return;
        grid.SelectedItem = row;
        view.FocusedRowHandle = hit.RowHandle;
    }
}
