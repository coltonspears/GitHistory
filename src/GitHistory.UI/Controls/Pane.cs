using System.Windows;
using System.Windows.Controls;

namespace GitHistory.UI.Controls;

/// <summary>A bordered section with a heading, optional description, actions, and arbitrary content.</summary>
public class Pane : HeaderedContentControl
{
    static Pane() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Pane), new FrameworkPropertyMetadata(typeof(Pane)));

    public static readonly DependencyProperty HeaderDescriptionProperty = DependencyProperty.Register(
        nameof(HeaderDescription), typeof(string), typeof(Pane), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HeaderActionsProperty = DependencyProperty.Register(
        nameof(HeaderActions), typeof(object), typeof(Pane), new PropertyMetadata(null));

    public string HeaderDescription
    {
        get => (string)GetValue(HeaderDescriptionProperty);
        set => SetValue(HeaderDescriptionProperty, value);
    }

    public object? HeaderActions
    {
        get => GetValue(HeaderActionsProperty);
        set => SetValue(HeaderActionsProperty, value);
    }
}
