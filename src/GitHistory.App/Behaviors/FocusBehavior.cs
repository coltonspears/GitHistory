using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace GitHistory.App.Behaviors;

/// <summary>Reusable focus interactions for search shortcuts and inline modal editors.</summary>
public static class FocusBehavior
{
    public static readonly DependencyProperty SearchTargetProperty = DependencyProperty.RegisterAttached(
        "SearchTarget", typeof(UIElement), typeof(FocusBehavior), new PropertyMetadata(null, OnSearchTargetChanged));
    public static UIElement? GetSearchTarget(DependencyObject element) => (UIElement?)element.GetValue(SearchTargetProperty);
    public static void SetSearchTarget(DependencyObject element, UIElement value) => element.SetValue(SearchTargetProperty, value);

    public static readonly DependencyProperty NextTargetProperty = DependencyProperty.RegisterAttached(
        "NextTarget", typeof(UIElement), typeof(FocusBehavior), new PropertyMetadata(null, OnNextTargetChanged));
    public static UIElement? GetNextTarget(DependencyObject element) => (UIElement?)element.GetValue(NextTargetProperty);
    public static void SetNextTarget(DependencyObject element, UIElement value) => element.SetValue(NextTargetProperty, value);

    public static readonly DependencyProperty FocusWhenVisibleProperty = DependencyProperty.RegisterAttached(
        "FocusWhenVisible", typeof(bool), typeof(FocusBehavior), new PropertyMetadata(false, OnFocusWhenVisibleChanged));
    public static bool GetFocusWhenVisible(DependencyObject element) => (bool)element.GetValue(FocusWhenVisibleProperty);
    public static void SetFocusWhenVisible(DependencyObject element, bool value) => element.SetValue(FocusWhenVisibleProperty, value);

    private static void OnSearchTargetChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not UIElement source) return;
        source.PreviewKeyDown -= OnSearchKey;
        if (args.NewValue is UIElement) source.PreviewKeyDown += OnSearchKey;
    }

    private static void OnSearchKey(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;
        var target = GetSearchTarget((DependencyObject)sender);
        if (target is not { IsEnabled: true, IsVisible: true }) return;
        target.Focus();
        args.Handled = true;
    }

    private static void OnNextTargetChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not UIElement source) return;
        source.PreviewKeyDown -= OnNextKey;
        if (args.NewValue is UIElement) source.PreviewKeyDown += OnNextKey;
    }

    private static void OnNextKey(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Down || Keyboard.Modifiers != ModifierKeys.None) return;
        var target = GetNextTarget((DependencyObject)sender);
        if (target is not { IsEnabled: true, IsVisible: true }) return;
        if (target is ListBox list && list.Items.Count > 0)
        {
            list.SelectedIndex = Math.Max(0, list.SelectedIndex);
            list.ScrollIntoView(list.SelectedItem);
            if (list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is ListBoxItem item) item.Focus();
            else list.Focus();
        }
        else target.Focus();
        args.Handled = true;
    }

    private static void OnFocusWhenVisibleChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement target) return;
        target.IsVisibleChanged -= OnVisibilityChanged;
        if (args.NewValue is true) target.IsVisibleChanged += OnVisibilityChanged;
    }

    private static void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is not true || sender is not FrameworkElement target) return;
        target.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (target.IsVisible && target.IsEnabled) target.Focus();
        });
    }
}
