using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DevExpress.Data;
using DevExpress.Xpf.Docking;
using DevExpress.Xpf.Grid;
using DevExpress.Xpf.Accordion;
using DevExpress.Xpf.Core;
using GitHistory.Core.Models;
using GitHistory.Core.ViewModels;
using GitHistory.App.Behaviors;

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
        if (filesGrid.Columns["Folder"] is not { Visible: true })
            throw new InvalidOperationException("The file list does not show the containing folder.");
        if (filesGrid.Columns["Age"].CellDisplayTemplate.LoadContent() is not FrameworkElement { VerticalAlignment: VerticalAlignment.Center })
            throw new InvalidOperationException("The Last Changed cell is not vertically centered.");
        await VerifyWorkspaceFilteringAsync(window, model);
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
        await VerifyFolderFilteringAsync(window, model, outputDirectory);
        await VerifyRowContextMenuAsync(window, model, filesGrid, outputDirectory);
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
        model.OpenSettingsCommand.Execute(null);
        await SettleAsync(window);
        Capture(window, Path.Combine(outputDirectory, "settings.png"), 1);
        model.Settings.CloseCommand.Execute(null);
        model.OpenImportCommand.Execute(null);
        await SettleAsync(window);
        Capture(window, Path.Combine(outputDirectory, "import.png"), 1);
        model.Import.CloseCommand.Execute(null);
        model.Browser.IsBusy = true;
        await SettleAsync(window);
        Capture(window, Path.Combine(outputDirectory, "loading.png"), 1);
        model.Browser.IsBusy = false;
        if (window.FindName("ConnectRepositoryButton") is not SimpleButton primaryButton)
            throw new InvalidOperationException("The connect button is missing.");
        primaryButton.Focus();
        await SettleAsync(window);
        if (primaryButton.Template.FindName("ButtonLabel", primaryButton) is not TextBlock { Foreground: SolidColorBrush foreground } ||
            primaryButton.Template.FindName("Chrome", primaryButton) is not Border { Background: SolidColorBrush background } || foreground.Color == background.Color)
            throw new InvalidOperationException("The connect button foreground and background do not contrast.");
        Capture(window, Path.Combine(outputDirectory, "keyboard-focus.png"), 1);
        bindings.Flush();
        File.Copy(Path.Combine(logDirectory, "bindings.log"), Path.Combine(outputDirectory, "bindings.log"), true);
        var report = new UiVerificationReport(recentCount, allCount, model.Details.History.Count, model.Details.DiffLines.Count,
            window.InputBindings.Count, true, true, true, true, true, true, true, true, true, logDirectory, "100%, 150%, and 200% rendering density; physical monitor transitions require manual verification.");
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "ui-verification.json"), JsonSerializer.Serialize(report, UiVerificationJsonContext.Default.UiVerificationReport));
    }
    private static async Task VerifyWorkspaceFilteringAsync(Window window, MainViewModel model)
    {
        if (window.FindName("RepositoryNavigator") is not AccordionControl navigator) throw new InvalidOperationException("The workspace navigator is missing.");
        var active = model.Workspace.SelectedRepository;
        model.Workspace.RepositorySearch = "no-workspace-can-match-this-value";
        await SettleAsync(window);
        if (model.Workspace.VisibleRepositories.Count != 0 || model.Workspace.SelectedRepository != active)
            throw new InvalidOperationException("Workspace filtering changed the active repository.");
        model.Workspace.RepositorySearch = "";
        model.Workspace.ShowOnlyPinned = true;
        await SettleAsync(window);
        if (model.Workspace.SelectedRepository != active) throw new InvalidOperationException("The pin filter changed the active repository.");
        if (!model.Workspace.SelectedIsPinned) model.Workspace.TogglePinCommand.Execute(active);
        await SettleAsync(window);
        if (model.Workspace.VisibleRepositories.All(repository => repository.Id != active?.Id))
            throw new InvalidOperationException("The pinned workspace was not included in the navigator.");
        model.Workspace.ShowOnlyPinned = false;
        await SettleAsync(window);
        if (!ReferenceEquals(navigator.SelectedItem, active)) throw new InvalidOperationException("The navigator did not restore its active workspace highlight after filtering.");
        await VerifyWorkspaceActivationAsync(window, navigator);
        if (!ReferenceEquals(model.Workspace.SelectedRepository, active) || !ReferenceEquals(navigator.SelectedItem, active))
            throw new InvalidOperationException("The workspace input test did not restore the active workspace and its bindings.");
    }

    private static async Task VerifyWorkspaceActivationAsync(Window window, AccordionControl navigator)
    {
        var sourceBinding = BindingOperations.GetBindingBase(navigator, AccordionControl.ItemsSourceProperty)
            ?? throw new InvalidOperationException("The navigator source is not bound.");
        var selectedBinding = BindingOperations.GetBindingBase(navigator, AccordionControl.SelectedItemProperty)
            ?? throw new InvalidOperationException("The navigator selection is not bound.");
        var commandBinding = BindingOperations.GetBindingBase(navigator, ExplicitSelectionBehavior.SelectionCommandProperty)
            ?? throw new InvalidOperationException("The navigator selection command is not bound.");
        var first = new RepositoryInfo("ui-input-first", "First input fixture", "https://example.invalid/first.git", "main", ["main"]);
        var second = new RepositoryInfo("ui-input-second", "Second input fixture", "https://example.invalid/second.git", "main", ["main"]);
        RepositoryInfo? invoked = null;
        try
        {
            // Keep the real control and templates, while isolating commands from repository I/O.
            BindingOperations.ClearBinding(navigator, ExplicitSelectionBehavior.SelectionCommandProperty);
            BindingOperations.ClearBinding(navigator, AccordionControl.SelectedItemProperty);
            BindingOperations.ClearBinding(navigator, AccordionControl.ItemsSourceProperty);
            ExplicitSelectionBehavior.SetSelectionCommand(navigator, new RelayCommand<RepositoryInfo>(item => invoked = item));
            navigator.ItemsSource = new[] { first, second };
            navigator.SelectedItem = first;
            await SettleAsync(window);
            if (invoked is not null) throw new InvalidOperationException("Programmatic navigator updates invoked workspace navigation.");
            var secondLabel = VisualDescendants(navigator).OfType<TextBlock>()
                .FirstOrDefault(element => element.IsVisible && ReferenceEquals(element.DataContext, second))
                ?? throw new InvalidOperationException("The second workspace fixture did not render.");
            secondLabel.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseUpEvent
            });
            if (!ReferenceEquals(invoked, second)) throw new InvalidOperationException("Clicking a workspace row did not activate that workspace.");
            invoked = null;
            navigator.SelectedItem = second;
            await SettleAsync(window);
            if (invoked is not null) throw new InvalidOperationException("Programmatic selection invoked workspace navigation.");
            var source = PresentationSource.FromVisual(navigator) ?? throw new InvalidOperationException("The navigator has no presentation source.");
            navigator.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Down)
            {
                RoutedEvent = Keyboard.KeyUpEvent
            });
            if (!ReferenceEquals(invoked, second)) throw new InvalidOperationException("Keyboard workspace navigation did not activate the selected workspace.");
        }
        finally
        {
            ExplicitSelectionBehavior.SetSelectionCommand(navigator, null);
            BindingOperations.SetBinding(navigator, AccordionControl.ItemsSourceProperty, sourceBinding);
            BindingOperations.SetBinding(navigator, AccordionControl.SelectedItemProperty, selectedBinding);
            BindingOperations.SetBinding(navigator, ExplicitSelectionBehavior.SelectionCommandProperty, commandBinding);
            await SettleAsync(window);
        }
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    private static async Task VerifyFolderFilteringAsync(Window window, MainViewModel model, string outputDirectory)
    {
        var folder = model.Browser.TreeNodes.First(node => node.Path.Length > 0 && node.ParentId is not null);
        model.Browser.SelectFolderCommand.Execute(folder);
        await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
        model.Browser.FolderSearch = folder.Name;
        await SettleAsync(window);
        if (!model.Browser.VisibleTreeNodes.Any(node => node.Id == folder.Id) ||
            !model.Browser.VisibleTreeNodes.Any(node => node.Id == folder.ParentId))
            throw new InvalidOperationException("Folder search did not retain the matching folder and its ancestor.");
        Capture(window, Path.Combine(outputDirectory, "folder-search.png"), 1);
        model.Browser.FolderSearch = "no-folder-can-match-this-value";
        await SettleAsync(window);
        if (model.Browser.SelectedNode?.Id != folder.Id) throw new InvalidOperationException("Folder search changed the active folder.");
        model.Browser.ClearFiltersCommand.Execute(null);
        await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
        await model.Details.LoadDiffCommand.ExecuteAsync(null);
    }

    private static async Task VerifyRowContextMenuAsync(Window window, MainViewModel model, GridControl grid, string outputDirectory)
    {
        await SettleAsync(window);
        var clickedRow = grid.GetRow(1) as FileRow ?? throw new InvalidOperationException("The context menu test needs a second file row.");
        model.Browser.SelectedFile = grid.GetRow(0) as FileRow;
        grid.View.ScrollIntoView(1);
        await SettleAsync(window);
        var rowElement = grid.View.GetRowElementByRowHandle(1) as UIElement ?? throw new InvalidOperationException("The second file row is not rendered.");
        rowElement.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });
        await SettleAsync(window);
        if (model.Browser.SelectedFile != clickedRow) throw new InvalidOperationException("Right-click did not select the pointed file row.");
        var menu = grid.ContextMenu ?? throw new InvalidOperationException("File context actions are missing.");
        menu.PlacementTarget = grid;
        menu.ApplyTemplate();
        menu.Measure(new Size(310, 500));
        menu.Arrange(new Rect(0, 0, 310, menu.DesiredSize.Height));
        menu.UpdateLayout();
        await SettleAsync(window);
        var action = menu.Items.OfType<MenuItem>().First();
        if (action.Command != model.Actions.OpenFileBrowserCommand || !ReferenceEquals(action.CommandParameter, clickedRow))
            throw new InvalidOperationException("The context menu does not target the right-clicked file.");
        if (menu.ActualWidth > 0 && menu.ActualHeight > 0) CaptureElement(menu, Path.Combine(outputDirectory, "file-context-menu.png"));
    }

    private static void CaptureElement(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
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
public sealed record UiVerificationReport(int RecentFiles, int AllFiles, int HistoryEntries, int DiffLines, int WindowKeyBindings,
    bool GridSortingVerified, bool DockLayoutRoundTripVerified, bool ShortcutBindingsVerified, bool WorkspaceFilterSelectionVerified, bool WorkspaceActivationVerified,
    bool FolderSearchVerified, bool ContextMenuRowTargetVerified, bool FolderColumnAndAgeAlignmentVerified, bool PrimaryButtonContrastVerified,
    string DiagnosticsDirectory, string ScalingVerification);
[JsonSerializable(typeof(UiVerificationReport))]
internal sealed partial class UiVerificationJsonContext : JsonSerializerContext;
