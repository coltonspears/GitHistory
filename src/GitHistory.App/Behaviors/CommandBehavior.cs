using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GitHistory.App.Behaviors;

public static class CommandBehavior
{
    public static readonly DependencyProperty DoubleClickCommandProperty = DependencyProperty.RegisterAttached(
        "DoubleClickCommand", typeof(ICommand), typeof(CommandBehavior), new PropertyMetadata(null, OnCommandChanged));
    public static ICommand? GetDoubleClickCommand(DependencyObject target) => (ICommand?)target.GetValue(DoubleClickCommandProperty);
    public static void SetDoubleClickCommand(DependencyObject target, ICommand value) => target.SetValue(DoubleClickCommandProperty, value);

    private static void OnCommandChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Control control) return;
        control.MouseDoubleClick -= OnDoubleClick;
        if (args.NewValue is ICommand) control.MouseDoubleClick += OnDoubleClick;
    }

    private static void OnDoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Left) return;
        var command = GetDoubleClickCommand((DependencyObject)sender);
        if (command?.CanExecute(null) is true)
        {
            command.Execute(null);
            args.Handled = true;
        }
    }
}
