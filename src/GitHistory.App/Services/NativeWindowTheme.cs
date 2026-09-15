using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GitHistory.App.Services;

/// <summary>Keeps the system title bar in the app palette while retaining native window behavior.</summary>
internal static class NativeWindowTheme
{
    public static void Apply(Window window, bool isLight)
    {
        // These attributes are officially supported by Windows 11. Older Windows keeps its system chrome.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        int dark = isLight ? 0 : 1;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        SetColor(handle, 35, window.TryFindResource("CanvasBrush") as SolidColorBrush);
        SetColor(handle, 36, window.TryFindResource("TextBrush") as SolidColorBrush);
    }

    private static void SetColor(nint handle, int attribute, SolidColorBrush? brush)
    {
        if (brush is null) return;
        int color = brush.Color.R | (brush.Color.G << 8) | (brush.Color.B << 16);
        _ = DwmSetWindowAttribute(handle, attribute, ref color, sizeof(int));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int valueSize);
}
