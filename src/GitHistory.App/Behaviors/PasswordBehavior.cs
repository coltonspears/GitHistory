using System.Windows;
using System.Windows.Controls;

namespace GitHistory.App.Behaviors;

/// <summary>Connects a native PasswordBox to the transient token field without putting input handlers in a view.</summary>
public static class PasswordBehavior
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached(
        "Password", typeof(string), typeof(PasswordBehavior), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged, AttachInput));
    private static readonly DependencyProperty IsUpdatingProperty = DependencyProperty.RegisterAttached(
        "IsUpdating", typeof(bool), typeof(PasswordBehavior), new PropertyMetadata(false));
    private static readonly DependencyProperty IsAttachedProperty = DependencyProperty.RegisterAttached(
        "IsAttached", typeof(bool), typeof(PasswordBehavior), new PropertyMetadata(false));

    public static string GetPassword(DependencyObject target) => (string)target.GetValue(PasswordProperty);
    public static void SetPassword(DependencyObject target, string? value) => target.SetValue(PasswordProperty, value ?? string.Empty);

    private static object AttachInput(DependencyObject target, object value)
    {
        if (target is PasswordBox box && !(bool)box.GetValue(IsAttachedProperty))
        {
            box.PasswordChanged += OnInput;
            box.SetValue(IsAttachedProperty, true);
        }
        return value as string ?? string.Empty;
    }

    private static void OnPasswordChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not PasswordBox box) return;
        if ((bool)box.GetValue(IsUpdatingProperty)) return;
        try
        {
            box.SetValue(IsUpdatingProperty, true);
            box.Password = args.NewValue as string ?? string.Empty;
        }
        finally { box.SetValue(IsUpdatingProperty, false); }
    }

    private static void OnInput(object sender, RoutedEventArgs args)
    {
        var box = (PasswordBox)sender;
        if ((bool)box.GetValue(IsUpdatingProperty)) return;
        try
        {
            box.SetValue(IsUpdatingProperty, true);
            box.SetCurrentValue(PasswordProperty, box.Password);
        }
        finally { box.SetValue(IsUpdatingProperty, false); }
    }
}
