using System.IO;
using System.Windows;
using DevExpress.Xpf.Core;
using DevExpress.Xpf.Docking;
using DevExpress.Xpf.Grid;
using GitHistory.Core.Services;
using Microsoft.Extensions.Logging;

namespace GitHistory.App.Services;

/// <summary>Owns view-only theme and DevExpress layout persistence, outside view models.</summary>
public sealed partial class AppearanceService(ILogger<AppearanceService> logger, string? dataDirectory = null) : IAppearanceService, IDisposable
{
    private readonly string layoutDirectory = Path.Combine(dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHistory"), "layout");
    private Window? window;
    private bool persistLayout;
    private readonly List<LayoutRegistration> registrations = [];

    public void SetTheme(string theme)
    {
        bool isLight = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
        ApplicationThemeHelper.ApplicationThemeName = isLight ? "Win11Light" : "Win11Dark";
        var resources = System.Windows.Application.Current.Resources.MergedDictionaries;
        var current = resources.FirstOrDefault(dictionary => dictionary.Source?.OriginalString is "Themes/Dark.xaml" or "Themes/Light.xaml");
        var replacement = new ResourceDictionary { Source = new Uri(isLight ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative) };
        if (current is null) resources.Insert(0, replacement);
        else resources[resources.IndexOf(current)] = replacement;
    }

    public void AttachWindow(Window attachedWindow, bool persistLayout = true)
    {
        DetachWindow();
        window = attachedWindow;
        this.persistLayout = persistLayout;
        window.Loaded += OnLoaded;
        window.Closed += OnClosed;
    }

    public void ResetLayout()
    {
        foreach (var registration in registrations)
        {
            try
            {
                using var stream = new MemoryStream(registration.DefaultLayout, writable: false);
                registration.Restore(stream);
                string savedPath = Path.Combine(layoutDirectory, registration.FileName);
                if (persistLayout && File.Exists(savedPath)) File.Delete(savedPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Xml.XmlException)
            {
                LayoutFailure(logger, registration.FileName, exception);
            }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (window is null) return;
        window.Loaded -= OnLoaded;
        if (window.FindName("WorkspaceDock") is DockLayoutManager docking)
            Register("dock-v1.xml", docking.SaveLayoutToStream, docking.RestoreLayoutFromStream);
        if (window.FindName("FilesGrid") is GridControl files)
            Register("files-v1.xml", files.SaveLayoutToStream, files.RestoreLayoutFromStream);
    }

    private void Register(string fileName, Action<Stream> save, Action<Stream> restore)
    {
        try
        {
            using var defaults = new MemoryStream();
            save(defaults);
            var registration = new LayoutRegistration(fileName, defaults.ToArray(), save, restore);
            registrations.Add(registration);
            string path = Path.Combine(layoutDirectory, fileName);
            if (persistLayout && File.Exists(path))
            {
                try
                {
                    using var saved = File.OpenRead(path);
                    restore(saved);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Xml.XmlException or ArgumentException)
                {
                    // A layout can be partially applied before a serializer rejects it.
                    // Reapply the captured default to leave a usable workspace.
                    using var fallback = new MemoryStream(registration.DefaultLayout, writable: false);
                    restore(fallback);
                    LayoutFailure(logger, fileName, exception);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Xml.XmlException)
        {
            LayoutFailure(logger, fileName, exception);
        }
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        if (persistLayout)
        {
            foreach (var registration in registrations)
            {
                try
                {
                    Directory.CreateDirectory(layoutDirectory);
                    string path = Path.Combine(layoutDirectory, registration.FileName);
                    string temporary = path + ".tmp";
                    using (var stream = File.Create(temporary)) registration.Save(stream);
                    File.Move(temporary, path, overwrite: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Xml.XmlException)
                {
                    LayoutFailure(logger, registration.FileName, exception);
                }
            }
        }
        DetachWindow();
    }

    private void DetachWindow()
    {
        if (window is not null)
        {
            window.Loaded -= OnLoaded;
            window.Closed -= OnClosed;
        }
        window = null;
        registrations.Clear();
    }

    public void Dispose() => DetachWindow();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save or restore workspace layout {LayoutName}; using the available layout.")]
    private static partial void LayoutFailure(ILogger logger, string layoutName, Exception exception);

    private sealed record LayoutRegistration(string FileName, byte[] DefaultLayout, Action<Stream> Save, Action<Stream> Restore);
}
