using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace GitHistory.UI.Controls;

/// <summary>A collapsible navigation surface with a mouse and keyboard accessible resize grip.</summary>
[TemplatePart(Name = "PART_ResizeGrip", Type = typeof(Thumb))]
public class Sidebar : HeaderedContentControl
{
    private Thumb? resizeGrip;
    public static readonly RoutedUICommand ToggleCommand = new("Toggle sidebar", nameof(ToggleCommand), typeof(Sidebar));

    static Sidebar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Sidebar), new FrameworkPropertyMetadata(typeof(Sidebar)));
        CommandManager.RegisterClassCommandBinding(typeof(Sidebar), new CommandBinding(ToggleCommand,
            (sender, _) => ((Sidebar)sender).SetCurrentValue(IsExpandedProperty, !((Sidebar)sender).IsExpanded)));
    }

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(Sidebar), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty ExpandedWidthProperty = DependencyProperty.Register(
        nameof(ExpandedWidth), typeof(double), typeof(Sidebar), new FrameworkPropertyMetadata(252d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, null,
            (_, value) => Math.Clamp((double)value, 220d, 420d)), IsFinite);
    public static readonly DependencyProperty CollapsedWidthProperty = DependencyProperty.Register(
        nameof(CollapsedWidth), typeof(double), typeof(Sidebar), new FrameworkPropertyMetadata(48d, null,
            (_, value) => Math.Clamp((double)value, 40d, 100d)), IsFinite);

    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public double ExpandedWidth { get => (double)GetValue(ExpandedWidthProperty); set => SetValue(ExpandedWidthProperty, value); }
    public double CollapsedWidth { get => (double)GetValue(CollapsedWidthProperty); set => SetValue(CollapsedWidthProperty, value); }
    private static bool IsFinite(object value) => double.IsFinite((double)value);

    public override void OnApplyTemplate()
    {
        if (resizeGrip is not null)
        {
            resizeGrip.DragDelta -= OnResize;
            resizeGrip.PreviewKeyDown -= OnResizeKey;
        }
        base.OnApplyTemplate();
        resizeGrip = GetTemplateChild("PART_ResizeGrip") as Thumb;
        if (resizeGrip is not null)
        {
            resizeGrip.DragDelta += OnResize;
            resizeGrip.PreviewKeyDown += OnResizeKey;
        }
    }

    private void OnResize(object sender, DragDeltaEventArgs args) => SetCurrentValue(ExpandedWidthProperty, ExpandedWidth + args.HorizontalChange);

    private void OnResizeKey(object sender, KeyEventArgs args)
    {
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 36 : 12;
        if (args.Key is Key.Left or Key.Right)
        {
            SetCurrentValue(ExpandedWidthProperty, ExpandedWidth + (args.Key == Key.Right ? step : -step));
            args.Handled = true;
        }
        else if (args.Key == Key.Home)
        {
            SetCurrentValue(ExpandedWidthProperty, 252d);
            args.Handled = true;
        }
    }
}
