using System.Windows;
using System.Windows.Controls;

namespace GitHistory.UI.Controls;

/// <summary>A normal WPF text editor with a search glyph and non-interactive placeholder.</summary>
public class SearchBox : TextBox
{
    static SearchBox() => DefaultStyleKeyProperty.OverrideMetadata(typeof(SearchBox), new FrameworkPropertyMetadata(typeof(SearchBox)));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(SearchBox), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ShowSearchIconProperty = DependencyProperty.Register(
        nameof(ShowSearchIcon), typeof(bool), typeof(SearchBox), new PropertyMetadata(true));

    public bool ShowSearchIcon
    {
        get => (bool)GetValue(ShowSearchIconProperty);
        set => SetValue(ShowSearchIconProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }
}
