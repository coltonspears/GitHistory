using System.Windows;
using System.Windows.Controls;

namespace GitHistory.UI.Controls;

/// <summary>A compact activity indicator with an arbitrary status label.</summary>
public class BusyIndicator : ContentControl
{
    static BusyIndicator() => DefaultStyleKeyProperty.OverrideMetadata(typeof(BusyIndicator), new FrameworkPropertyMetadata(typeof(BusyIndicator)));
}
