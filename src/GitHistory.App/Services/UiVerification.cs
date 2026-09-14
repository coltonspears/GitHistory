using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DevExpress.Data;
using DevExpress.Xpf.Docking;
using DevExpress.Xpf.Grid;
using GitHistory.Core.Models;
using GitHistory.Core.ViewModels;

namespace GitHistory.App.Services;

/// <summary>Opt-in smoke testing renders the actual WPF controls, with an isolated sample workspace.</summary>
public static class UiVerification
{
    public static async Task RunAsync(Window window, MainViewModel model, string outputDirectory, TextWriterTraceListener bindings, string logDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        model.ResetLayoutCommand.Execute(null);
        if (window.FindName("WorkspaceDock") is not DockLayoutManager docking || window.FindName("HistoryPanel") is not LayoutPanel historyPanel)
            throw new InvalidOperationException("The docking workspace did not initialize.");
        historyPanel.ItemWidth = new GridLength(360);
        using (var layout = new MemoryStream())
        {
            docking.SaveLayoutToStream(layout);
            historyPanel.ItemWidth = new GridLength(460);
            layout.Position = 0;
            docking.RestoreLayoutFromStream(layout);
            if (Math.Abs(historyPanel.ItemWidth.Value - 360) > 1)
                throw new InvalidOperationException("The docking layout did not restore the saved pane size.");
        }
        model.ResetLayoutCommand.Execute(null);
        model.Browser.Mode = FileViewMode.RecentChanges;
        model.Browser.DatePreset = "7 days";
        await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
        await model.Details.LoadDiffCommand.ExecuteAsync(null);
        if (model.Browser.Files.Count == 0 || model.Details.DiffLines.Count == 0) throw new InvalidOperationException("The demo file browser or inline diff did not populate.");
        await SettleAsync(window);
        foreach (var scale in new[] { 1d, 1.5d, 2d }) Capture(window, Path.Combine(outputDirectory, $"dark-{scale * 100:0}.png"), scale);
        var recentCount = model.Browser.Files.Count;
        if (window.FindName("FilesGrid") is not GridControl filesGrid || filesGrid.ItemsSource != model.Browser.Files)
            throw new InvalidOperationException("The DevExpress file grid is not bound to the file browser.");
        filesGrid.Columns["Path"].SortOrder = ColumnSortOrder.Ascending;
        await SettleAsync(window);
        if (filesGrid.GetRow(0) is not FileRow firstVisible || firstVisible.Path != model.Browser.Files.OrderBy(f => f.Path, StringComparer.CurrentCulture).First().Path)
            throw new InvalidOperationException("The DevExpress file grid did not sort paths correctly.");
        model.ResetLayoutCommand.Execute(null);
        if (filesGrid.Columns["Path"].SortOrder != ColumnSortOrder.None)
            throw new InvalidOperationException("Reset Layout did not restore file column preferences.");
        model.Browser.Mode = FileViewMode.AllFiles;
        await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
        await model.Details.LoadDiffCommand.ExecuteAsync(null);
        await SettleAsync(window);
        Capture(window, Path.Combine(outputDirectory, "all-files.png"), 1);
        var allCount = model.Browser.Files.Count;
        model.Browser.Search = "Repository";
        await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
        if (model.Browser.Files.Any(f => !f.Path.Contains("Repository", StringComparison.OrdinalIgnoreCase) && !f.Subject.Contains("Repository", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The search smoke check returned unrelated rows.");
        model.Browser.Search = "";
        model.Browser.Mode = FileViewMode.RecentChanges;
        await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
        model.Browser.SelectedFile = model.Browser.Files.FirstOrDefault();
        await model.Details.LoadDiffCommand.ExecuteAsync(null);
        ExecuteShortcut(window, Key.D, ModifierKeys.Control);
        await SettleAsync(window);
        Capture(window, Path.Combine(outputDirectory, "light.png"), 1);
        ExecuteShortcut(window, Key.D, ModifierKeys.Control);
        ExecuteShortcut(window, Key.K, ModifierKeys.Control);
        if (!model.IsPaletteOpen) throw new InvalidOperationException("The command palette shortcut was not connected.");
        await SettleAsync(window);
        Capture(window, Path.Combine(outputDirectory, "command-palette.png"), 1);
        ExecuteShortcut(window, Key.Escape, ModifierKeys.None);
        if (model.IsPaletteOpen) throw new InvalidOperationException("The Escape shortcut did not dismiss the palette.");
        model.Workspace.OpenConnectCommand.Execute(null);
        await SettleAsync(window);
        Capture(window, Path.Combine(outputDirectory, "connect.png"), 1);
        model.Workspace.CloseConnectCommand.Execute(null);
        bindings.Flush();
        File.Copy(Path.Combine(logDirectory, "bindings.log"), Path.Combine(outputDirectory, "bindings.log"), true);
        var report = new UiVerificationReport(recentCount, allCount, model.Details.History.Count, model.Details.DiffLines.Count,
            window.InputBindings.Count, true, true, true, logDirectory, "100%, 150%, and 200% rendering density; physical monitor transitions require manual verification.");
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "ui-verification.json"), JsonSerializer.Serialize(report, UiVerificationJsonContext.Default.UiVerificationReport));
    }
    private static void ExecuteShortcut(Window window, Key key, ModifierKeys modifiers)
    {
        var binding = window.InputBindings.OfType<KeyBinding>().Single(b => b.Key == key && b.Modifiers == modifiers);
        if (!binding.Command.CanExecute(binding.CommandParameter)) throw new InvalidOperationException($"The {modifiers}+{key} shortcut is unavailable.");
        binding.Command.Execute(binding.CommandParameter);
    }
    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        // DevExpress themes and animated indicators finish on subsequent dispatcher/render passes.
        await Task.Delay(400);
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void Capture(Window window, string path, double scale)
    {
        var width = (int)Math.Ceiling(window.ActualWidth * scale);
        var height = (int)Math.Ceiling(window.ActualHeight * scale);
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
public sealed record UiVerificationReport(int RecentFiles, int AllFiles, int HistoryEntries, int DiffLines, int WindowKeyBindings, bool GridSortingVerified, bool DockLayoutRoundTripVerified, bool ShortcutBindingsVerified, string DiagnosticsDirectory, string ScalingVerification);
[JsonSerializable(typeof(UiVerificationReport))]
internal sealed partial class UiVerificationJsonContext : JsonSerializerContext;
