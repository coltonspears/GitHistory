using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GitHistory.Core.Models;
using GitHistory.Core.ViewModels;
using GitHistory.App.Behaviors;
using GitHistory.App.Converters;
using GitHistory.UI.Controls;

namespace GitHistory.App.Services;

/// <summary>Opt-in smoke testing renders the actual WPF controls, with an isolated sample workspace.</summary>
public static class UiVerification
{
    public static async Task RunAsync(Window window, MainViewModel model, string outputDirectory, TextWriterTraceListener bindings, string logDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        model.Settings.Theme = "Dark";
        model.ResetLayoutCommand.Execute(null);
        if (window.FindName("WorkspaceDock") is not WorkspaceLayout workspace || window.FindName("WorkspaceSidebar") is not Sidebar sidebar)
            throw new InvalidOperationException("The reusable workspace and sidebar did not initialize.");
        if (window.FindName("FilesGrid") is not DataGrid layoutGrid)
            throw new InvalidOperationException("The file workspace did not initialize.");
        if (window.FindName("HistoryGrid") is not DataGrid historyLayoutGrid)
            throw new InvalidOperationException("The history workspace did not initialize.");
        var defaultLayouts = workspace.CaptureLayouts().ToArray();
        var defaultSidebarWidth = sidebar.ExpandedWidth;
        var defaultSidebarExpanded = sidebar.IsExpanded;
        VerifyLegacyWorkspaceLayout(window, workspace, sidebar);
        model.ResetLayoutCommand.Execute(null);
        var layoutColumn = FindColumn(layoutGrid, "Path");
        var folderColumn = FindColumn(layoutGrid, "Folder");
        workspace.Layout = WorkspaceLayoutPreset.SideBySide;
        workspace.ColumnRatio = .61;
        workspace.RowRatio = .44;
        workspace.Layout = WorkspaceLayoutPreset.FocusDiff;
        workspace.ColumnRatio = .66;
        workspace.RowRatio = .57;
        var expectedLayouts = workspace.CaptureLayouts().ToArray();
        sidebar.ExpandedWidth = 330;
        sidebar.IsExpanded = false;
        layoutColumn.Width = new DataGridLength(275);
        layoutColumn.DisplayIndex = 2;
        layoutColumn.SortDirection = ListSortDirection.Descending;
        folderColumn.Visibility = Visibility.Collapsed;
        layoutGrid.Items.SortDescriptions.Add(new SortDescription("Path", ListSortDirection.Descending));
        layoutGrid.Items.GroupDescriptions.Add(new PropertyGroupDescription("Folder"));
        using (var layout = new MemoryStream())
        {
            AppearanceService.SaveWorkspaceLayout(window, layout);
            workspace.ResetLayouts();
            sidebar.ExpandedWidth = 240;
            sidebar.IsExpanded = true;
            layoutColumn.Width = new DataGridLength(400);
            layoutColumn.DisplayIndex = 0;
            layoutColumn.SortDirection = null;
            folderColumn.Visibility = Visibility.Visible;
            layoutGrid.Items.SortDescriptions.Clear();
            layoutGrid.Items.GroupDescriptions.Clear();
            historyLayoutGrid.Columns[0].Width = new DataGridLength(100);
            layout.Position = 0;
            AppearanceService.RestoreWorkspaceLayout(window, layout);
            await SettleAsync(window);
            if (workspace.Layout != WorkspaceLayoutPreset.FocusDiff || !workspace.CaptureLayouts().SequenceEqual(expectedLayouts) ||
                sidebar.IsExpanded || Math.Abs(sidebar.ExpandedWidth - 330) > 1 ||
                Math.Abs(layoutColumn.Width.Value - 275) > 1 || layoutColumn.DisplayIndex != 2 ||
                folderColumn.Visibility != Visibility.Collapsed || !historyLayoutGrid.Columns[0].Width.IsStar ||
                layoutColumn.SortDirection != ListSortDirection.Descending ||
                layoutGrid.Items.GroupDescriptions.SingleOrDefault() is not PropertyGroupDescription { PropertyName: "Folder" } ||
                layoutGrid.Items.SortDescriptions.SingleOrDefault() != new SortDescription("Path", ListSortDirection.Descending))
                throw new InvalidOperationException("The workspace did not restore each arrangement's proportions, selected layout, sidebar state, and file-column preferences.");
        }
        await VerifySupersededColumnRestoreAsync(window, historyLayoutGrid);
        model.ResetLayoutCommand.Execute(null);
        if (workspace.Layout != WorkspaceLayoutPreset.Balanced || !workspace.CaptureLayouts().SequenceEqual(defaultLayouts) ||
            sidebar.IsExpanded != defaultSidebarExpanded || Math.Abs(sidebar.ExpandedWidth - defaultSidebarWidth) > 1)
            throw new InvalidOperationException("Reset Layout did not restore sidebar and every arrangement's default proportions.");
        model.Browser.Mode = FileViewMode.RecentChanges;
        model.Browser.DatePreset = "7 days";
        await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
        await model.Details.LoadDiffCommand.ExecuteAsync(null);
        if (model.Browser.Files.Count == 0 || model.Details.DiffLines.Count == 0) throw new InvalidOperationException("The demo file browser or inline diff did not populate.");
        await SettleAsync(window);
        foreach (var scale in new[] { 1d, 1.5d, 2d }) Capture(window, Path.Combine(outputDirectory, $"dark-{scale * 100:0}.png"), scale);
        var originalSize = new Size(window.Width, window.Height);
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        await SettleAsync(window);
        Capture(window, Path.Combine(outputDirectory, "compact.png"), 1);
        window.Width = originalSize.Width;
        window.Height = originalSize.Height;
        await SettleAsync(window);
        var recentCount = model.Browser.Files.Count;
        if (window.FindName("FilesGrid") is not DataGrid filesGrid || filesGrid.ItemsSource != model.Browser.Files)
            throw new InvalidOperationException("The file grid is not bound to the file browser.");
        if (FindColumn(filesGrid, "Folder").Visibility != Visibility.Visible)
            throw new InvalidOperationException("The file list does not show the containing folder.");
        if (FindColumn(filesGrid, "LastChanged") is not DataGridTemplateColumn ageColumn ||
            ageColumn.CellTemplate.LoadContent() is not FrameworkElement { VerticalAlignment: VerticalAlignment.Center })
            throw new InvalidOperationException("The Last Changed cell is not vertically centered.");
        await VerifyWorkspaceArrangementsAsync(window, model, workspace, sidebar, outputDirectory);
        await VerifyEditorAndFocusGeometryAsync(window, filesGrid, outputDirectory);
        await VerifyWorkspaceFilteringAsync(window, model);
        var pathColumn = FindColumn(filesGrid, "Path");
        var pathHeader = VisualDescendants(filesGrid).OfType<DataGridColumnHeader>().FirstOrDefault(header => header.Column == pathColumn)
            ?? throw new InvalidOperationException("The file path column header is missing.");
        var headerPresenter = VisualDescendants(filesGrid).OfType<DataGridColumnHeadersPresenter>().First();
        var headerPresenterPeer = UIElementAutomationPeer.CreatePeerForElement(headerPresenter) as DataGridColumnHeadersPresenterAutomationPeer
            ?? throw new InvalidOperationException("The file column headers do not expose an automation peer.");
        var columnPeer = new DataGridColumnHeaderItemAutomationPeer(pathHeader.Content, pathColumn, headerPresenterPeer);
        if (columnPeer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider sortHeader)
            throw new InvalidOperationException("The file path column header does not support accessible sorting.");
        sortHeader.Invoke();
        await SettleAsync(window);
        if (pathColumn.SortDirection != ListSortDirection.Ascending || filesGrid.Items[0] is not FileRow firstVisible ||
            firstVisible.Path != model.Browser.Files.OrderBy(f => f.Path, StringComparer.CurrentCulture).First().Path)
            throw new InvalidOperationException("The file grid did not sort paths correctly.");
        await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
        await SettleAsync(window);
        if (pathColumn.SortDirection != ListSortDirection.Ascending || filesGrid.Items[0] is not FileRow refreshedFirst ||
            refreshedFirst.Path != model.Browser.Files.OrderBy(f => f.Path, StringComparer.CurrentCulture).First().Path)
            throw new InvalidOperationException("Refreshing the file query did not preserve the active column sort.");
        model.ResetLayoutCommand.Execute(null);
        if (pathColumn.SortDirection is not null || filesGrid.Items.SortDescriptions.Count != 0 || filesGrid.Items.GroupDescriptions.Count != 0)
            throw new InvalidOperationException("Reset Layout did not restore file column preferences.");
        await VerifyActivitySelectionAsync(window, model, outputDirectory);
        await VerifyCompactCustomLayoutAsync(window, model, workspace, sidebar, outputDirectory);
        model.Browser.Mode = FileViewMode.AllFiles;
        await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
        await model.Details.LoadDiffCommand.ExecuteAsync(null);
        await SettleAsync(window);
        Capture(window, Path.Combine(outputDirectory, "all-files.png"), 1);
        var allCount = model.Browser.Files.Count;
        await VerifyFolderFilteringAsync(window, model, outputDirectory);
        await VerifyRowContextMenuAsync(window, model, filesGrid, outputDirectory);
        await VerifyCommitContextMenuAsync(window, model);
        model.Browser.Search = "Repository";
        await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
        if (model.Browser.Files.Any(f => !f.Path.Contains("Repository", StringComparison.OrdinalIgnoreCase) && !f.Subject.Contains("Repository", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The search smoke check returned unrelated rows.");
        model.Browser.Search = "";
        model.Browser.Mode = FileViewMode.RecentChanges;
        await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
        model.Browser.SelectedFile = model.Browser.Files.FirstOrDefault();
        await model.Details.LoadDiffCommand.ExecuteAsync(null);
        await VerifyThemesAsync(window, model, outputDirectory);
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
        if (!VisualDescendants(window).OfType<BusyIndicator>().Any(indicator => indicator.IsVisible && indicator.ActualHeight > 0))
            throw new InvalidOperationException("The file-query loading state did not display an activity indicator.");
        Capture(window, Path.Combine(outputDirectory, "loading.png"), 1);
        model.Browser.IsBusy = false;
        if (window.FindName("ConnectRepositoryButton") is not Button primaryButton)
            throw new InvalidOperationException("The connect button is missing.");
        VerifyButtonContrast(primaryButton);
        primaryButton.Focus();
        await SettleAsync(window);
        VerifyButtonContrast(primaryButton);
        Capture(window, Path.Combine(outputDirectory, "keyboard-focus.png"), 1);
        bindings.Flush();
        File.Copy(Path.Combine(logDirectory, "bindings.log"), Path.Combine(outputDirectory, "bindings.log"), true);
        if (new FileInfo(Path.Combine(outputDirectory, "bindings.log")).Length != 0)
            throw new InvalidOperationException("Binding warnings or errors were recorded; inspect bindings.log.");
        var report = new UiVerificationReport(recentCount, allCount, model.Details.History.Count, model.Details.DiffLines.Count,
            window.InputBindings.Count, true, true, true, true, true, true, true, true, true, true, logDirectory, "100%, 150%, and 200% rendering density; physical monitor transitions require manual verification.")
        {
            FourThemesVerified = true,
            SearchCaretAlignmentVerified = true,
            WholeRowFocusVerified = true,
            SidebarAndLayoutModesVerified = true,
            CompactCustomRangeVerified = true,
            LegacyWorkspaceMigrationVerified = true
        };
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "ui-verification.json"), JsonSerializer.Serialize(report, UiVerificationJsonContext.Default.UiVerificationReport));
    }

    private static DataGridColumn FindColumn(DataGrid grid, string member) => grid.Columns.FirstOrDefault(column => column.SortMemberPath == member)
        ?? throw new InvalidOperationException($"The {member} column is missing its sorting member.");

    private static async Task VerifySupersededColumnRestoreAsync(Window window, DataGrid historyGrid)
    {
        using var starLayout = new MemoryStream();
        AppearanceService.SaveWorkspaceLayout(window, starLayout);
        historyGrid.Columns[0].Width = new DataGridLength(125);
        using var pixelLayout = new MemoryStream();
        AppearanceService.SaveWorkspaceLayout(window, pixelLayout);
        starLayout.Position = 0;
        pixelLayout.Position = 0;
        AppearanceService.RestoreWorkspaceLayout(window, starLayout);
        AppearanceService.RestoreWorkspaceLayout(window, pixelLayout);
        await SettleAsync(window);
        if (!historyGrid.Columns[0].Width.IsAbsolute || historyGrid.Columns[0].Width.Value != 125)
            throw new InvalidOperationException("A deferred star width from an earlier restore overrode the newer saved column preference.");
    }

    private static void VerifyLegacyWorkspaceLayout(Window window, WorkspaceLayout workspace, Sidebar sidebar)
    {
        const string legacy = """
            {"Panes":[{"Name":"FilesRow","Size":2,"Unit":2},{"Name":"InspectionRow","Size":3,"Unit":2},{"Name":"HistoryColumn","Size":310,"Unit":1},{"Name":"DiffColumn","Size":1,"Unit":2}],"Grids":[]}
            """;
        var expanded = sidebar.IsExpanded;
        var width = sidebar.ExpandedWidth;
        var available = workspace.ActualWidth - workspace.ColumnDefinitions[1].Width.Value;
        if (available <= 310) throw new InvalidOperationException("The migration fixture needs a rendered workspace wider than its saved pane.");
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(legacy));
        AppearanceService.RestoreWorkspaceLayout(window, input);
        if (workspace.Layout != WorkspaceLayoutPreset.Balanced || Math.Abs(workspace.RowRatio - .4) > .00001 ||
            Math.Abs(workspace.ColumnRatio - 310 / available) > .00001 || sidebar.IsExpanded != expanded || sidebar.ExpandedWidth != width)
            throw new InvalidOperationException("The earlier native pane format did not migrate its star and pixel sizes while preserving the sidebar.");
    }

    private static async Task VerifyCompactCustomLayoutAsync(Window window, MainViewModel model, WorkspaceLayout workspace, Sidebar sidebar, string outputDirectory)
    {
        var size = new Size(window.Width, window.Height);
        var sidebarWidth = sidebar.ExpandedWidth;
        var sidebarExpanded = sidebar.IsExpanded;
        var layouts = workspace.CaptureLayouts();
        var selectedLayout = workspace.Layout;
        var datePreset = model.Browser.DatePreset;
        try
        {
            sidebar.IsExpanded = true;
            sidebar.ExpandedWidth = 420;
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            model.Browser.DatePreset = "Custom";
            await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
            await model.Details.LoadDiffCommand.ExecuteAsync(null);
            foreach (var preset in WorkspaceLayout.Presets)
            {
                workspace.SetCurrentValue(WorkspaceLayout.LayoutProperty, preset.Value);
                await SettleAsync(window);
                if (window.FindName("FileSearch") is not SearchBox { ActualWidth: >= 200 } search || !search.IsVisible)
                    throw new InvalidOperationException($"The minimum-size {preset.Name} workspace squeezed the custom-range file search below 200 pixels.");
                if (VisualDescendants(window).OfType<DatePicker>().Count(picker => picker.IsVisible && picker.ActualWidth > 0) < 2)
                    throw new InvalidOperationException("The custom date-range editors did not remain available in the minimum-size workspace.");
                VerifyArrangementBounds(workspace);
                if (window.FindName("HistoryGrid") is DataGrid historyGrid)
                {
                    await File.AppendAllTextAsync(Path.Combine(outputDirectory, "compact-history-diagnostics.txt"),
                        DescribeHistory(historyGrid, model.Details.SelectedChange, preset.Name) + Environment.NewLine);
                    var row = historyGrid.ItemContainerGenerator.ContainerFromItem(model.Details.SelectedChange) as DataGridRow;
                    var subject = row is null ? null : VisualDescendants(row).OfType<TextBlock>()
                        .FirstOrDefault(label => label.Text == model.Details.SelectedChange?.Subject);
                    var viewport = VisualDescendants(historyGrid).OfType<ScrollViewer>().FirstOrDefault()?.ViewportWidth ?? 0;
                    if (subject is not { IsVisible: true, ActualWidth: >= 80 } || viewport <= 0 ||
                        Math.Abs(historyGrid.Columns[0].ActualWidth - viewport) > 1)
                        throw new InvalidOperationException($"The minimum-size {preset.Name} workspace did not render the selected history subject at a readable width.");
                }
                var suffix = preset.Value switch { WorkspaceLayoutPreset.SideBySide => "side-by-side", WorkspaceLayoutPreset.FocusDiff => "focus-diff", _ => "balanced" };
                Capture(window, Path.Combine(outputDirectory, $"compact-custom-{suffix}.png"), 1);
                if (preset.Value == WorkspaceLayoutPreset.Balanced) Capture(window, Path.Combine(outputDirectory, "compact-custom.png"), 1);
            }
        }
        finally
        {
            window.Width = size.Width;
            window.Height = size.Height;
            sidebar.ExpandedWidth = sidebarWidth;
            sidebar.IsExpanded = sidebarExpanded;
            workspace.RestoreLayouts(layouts, selectedLayout);
            model.Browser.DatePreset = datePreset;
            await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
            await model.Details.LoadDiffCommand.ExecuteAsync(null);
            await SettleAsync(window);
        }
    }

    private static string DescribeHistory(DataGrid grid, FileChange? selected, string stage)
    {
        var row = selected is null ? null : grid.ItemContainerGenerator.ContainerFromItem(selected) as DataGridRow;
        var cells = row is null ? [] : VisualDescendants(row).OfType<DataGridCell>().ToArray();
        var text = row is null ? [] : VisualDescendants(row).OfType<TextBlock>().ToArray();
        return $"{stage}: grid={grid.ActualWidth}x{grid.ActualHeight}; columnVirtualization={grid.EnableColumnVirtualization}; cellsPanelOffset={grid.CellsPanelHorizontalOffset}; column={grid.Columns[0].Width}, actual={grid.Columns[0].ActualWidth}; selected={selected?.Subject}; row={(row is null ? "missing" : $"{row.ActualWidth}x{row.ActualHeight}, visible={row.IsVisible}")}; cells=[{string.Join("; ", cells.Select(cell => $"{cell.ActualWidth}x{cell.ActualHeight}, visible={cell.IsVisible}, x={cell.TransformToAncestor(grid).Transform(new Point()).X}"))}]; text=[{string.Join("; ", text.Select(label => $"{label.Text}, {label.ActualWidth}x{label.ActualHeight}, visible={label.IsVisible}"))}]; scrolling=[{string.Join("; ", VisualDescendants(grid).OfType<ScrollViewer>().Select(scroll => $"offset={scroll.HorizontalOffset},{scroll.VerticalOffset}, extent={scroll.ExtentWidth}x{scroll.ExtentHeight}, viewport={scroll.ViewportWidth}x{scroll.ViewportHeight}"))}]";
    }

    private static async Task VerifyWorkspaceArrangementsAsync(Window window, MainViewModel model, WorkspaceLayout workspace, Sidebar sidebar, string outputDirectory)
    {
        if (window.FindName("LayoutPicker") is not ComboBox picker)
            throw new InvalidOperationException("The workspace arrangement picker is missing.");
        var filesGrid = window.FindName("FilesGrid");
        var historyGrid = window.FindName("HistoryGrid");
        var diffGrid = window.FindName("DiffGrid");
        var selectedFile = model.Browser.SelectedFile;
        var selectedChange = model.Details.SelectedChange;
        var savedProportions = new Dictionary<WorkspaceLayoutPreset, WorkspaceLayoutState>();
        var source = PresentationSource.FromVisual(workspace) ?? throw new InvalidOperationException("The workspace has no presentation source.");
        foreach (var preset in WorkspaceLayout.Presets)
        {
            picker.SetCurrentValue(Selector.SelectedValueProperty, preset.Value);
            await SettleAsync(window);
            if (workspace.Layout != preset.Value) throw new InvalidOperationException("The layout picker did not change the workspace arrangement.");
            var splitter = workspace.Children.OfType<GridSplitter>().First(item => item.ResizeDirection == GridResizeDirection.Columns);
            var previousRatio = workspace.ColumnRatio;
            splitter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
            splitter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.KeyUpEvent });
            await SettleAsync(window);
            if (Math.Abs(workspace.ColumnRatio - previousRatio) < .00001)
                throw new InvalidOperationException($"The {preset.Name} pane splitter did not respond to keyboard resizing.");
            savedProportions[preset.Value] = workspace.CaptureLayouts().Single(state => state.Layout == preset.Value);
            VerifyArrangementBounds(workspace);
            if (!ReferenceEquals(window.FindName("FilesGrid"), filesGrid) || !ReferenceEquals(window.FindName("HistoryGrid"), historyGrid) ||
                !ReferenceEquals(window.FindName("DiffGrid"), diffGrid) || !ReferenceEquals(model.Browser.SelectedFile, selectedFile) ||
                !ReferenceEquals(model.Details.SelectedChange, selectedChange))
                throw new InvalidOperationException("Switching workspace arrangements replaced a pane or lost the active file/commit.");
            var suffix = preset.Value switch { WorkspaceLayoutPreset.SideBySide => "side-by-side", WorkspaceLayoutPreset.FocusDiff => "focus-diff", _ => "balanced" };
            Capture(window, Path.Combine(outputDirectory, $"layout-{suffix}.png"), 1);
        }
        foreach (var saved in savedProportions.Values)
        {
            picker.SetCurrentValue(Selector.SelectedValueProperty, saved.Layout);
            await SettleAsync(window);
            if (Math.Abs(workspace.ColumnRatio - saved.ColumnRatio) > .00001 || Math.Abs(workspace.RowRatio - saved.RowRatio) > .00001)
                throw new InvalidOperationException("Returning to a workspace arrangement lost its resized proportions.");
        }
        sidebar.ExpandedWidth = 300;
        await SettleAsync(window);
        if (sidebar.Template.FindName("PART_ResizeGrip", sidebar) is not Thumb grip)
            throw new InvalidOperationException("The sidebar resize grip is missing.");
        grip.RaiseEvent(new DragDeltaEventArgs(24, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        await SettleAsync(window);
        if (Math.Abs(sidebar.ExpandedWidth - 324) > 1) throw new InvalidOperationException("Dragging the sidebar grip did not resize it.");
        grip.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Left) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await SettleAsync(window);
        if (sidebar.ExpandedWidth >= 324) throw new InvalidOperationException("The sidebar grip did not support keyboard resizing.");
        var expandedWidth = sidebar.ExpandedWidth;
        var expandedWorkspaceWidth = workspace.ActualWidth;
        Capture(window, Path.Combine(outputDirectory, "sidebar-expanded.png"), 1);
        ExecuteShortcut(window, Key.B, ModifierKeys.Control);
        await SettleAsync(window);
        if (sidebar.IsExpanded || Math.Abs(sidebar.ActualWidth - sidebar.CollapsedWidth) > 1 || workspace.ActualWidth <= expandedWorkspaceWidth)
            throw new InvalidOperationException("Collapsing the sidebar did not give its width back to the workspace.");
        Capture(window, Path.Combine(outputDirectory, "sidebar-collapsed.png"), 1);
        ExecuteShortcut(window, Key.B, ModifierKeys.Control);
        await SettleAsync(window);
        if (!sidebar.IsExpanded || Math.Abs(sidebar.ActualWidth - expandedWidth) > 1)
            throw new InvalidOperationException("Expanding the sidebar did not restore its previous width.");
        model.ResetLayoutCommand.Execute(null);
        await SettleAsync(window);
    }

    private static void VerifyArrangementBounds(WorkspaceLayout workspace)
    {
        Rect Bounds(WorkspaceRegion region)
        {
            var pane = workspace.Children.OfType<FrameworkElement>().Single(child => child is not GridSplitter && WorkspaceLayout.GetRegion(child) == region);
            var bounds = pane.TransformToAncestor(workspace).TransformBounds(new Rect(new Size(pane.ActualWidth, pane.ActualHeight)));
            if (!pane.IsVisible || bounds.Width <= 0 || bounds.Height <= 0 || bounds.Right > workspace.ActualWidth + 1 || bounds.Bottom > workspace.ActualHeight + 1)
                throw new InvalidOperationException($"The {region} pane is clipped or missing in {workspace.Layout}.");
            return bounds;
        }
        var primary = Bounds(WorkspaceRegion.Primary);
        var history = Bounds(WorkspaceRegion.History);
        var inspector = Bounds(WorkspaceRegion.Inspector);
        bool arranged = workspace.Layout switch
        {
            WorkspaceLayoutPreset.Balanced => primary.Bottom <= history.Top + 1 && primary.Bottom <= inspector.Top + 1 && history.Right <= inspector.Left + 1,
            WorkspaceLayoutPreset.SideBySide => primary.Right <= history.Left + 1 && primary.Right <= inspector.Left + 1 && history.Bottom <= inspector.Top + 1,
            WorkspaceLayoutPreset.FocusDiff => primary.Right <= history.Left + 1 && primary.Bottom <= inspector.Top + 1 && history.Bottom <= inspector.Top + 1,
            _ => false
        };
        if (!arranged) throw new InvalidOperationException($"The {workspace.Layout} pane positions do not match the selected arrangement.");
    }

    private static async Task VerifyThemesAsync(Window window, MainViewModel model, string outputDirectory)
    {
        var beforeShortcut = model.Settings.Theme;
        ExecuteShortcut(window, Key.D, ModifierKeys.Control);
        await SettleAsync(window);
        if (model.Settings.Theme == beforeShortcut || !model.Settings.Themes.Contains(model.Settings.Theme))
            throw new InvalidOperationException("The theme keyboard shortcut did not activate another supported theme.");
        var palettes = new HashSet<(Color Background, Color Accent)>();
        foreach (var theme in new[] { "Dark", "Light", "Classic", "Dusk" })
        {
            if (!model.Settings.Themes.Contains(theme)) throw new InvalidOperationException($"The {theme} theme is missing from settings.");
            model.Settings.Theme = theme;
            await SettleAsync(window);
            if (model.Theme != theme) throw new InvalidOperationException($"The {theme} theme did not synchronize with the saved main preference.");
            Capture(window, Path.Combine(outputDirectory, theme.ToLowerInvariant() + ".png"), 1);
            VerifyButtonContrast((Button)window.FindName("ConnectRepositoryButton"));
            if (window.Background is not SolidColorBrush background || window.FindResource("AccentBrush") is not SolidColorBrush accent)
                throw new InvalidOperationException($"The {theme} theme did not provide its background and accent colors.");
            palettes.Add((background.Color, accent.Color));
            var chart = VisualDescendants(window).OfType<ActivityChart>().First();
            if (chart.Foreground is not SolidColorBrush chartAccent || chartAccent.Color != accent.Color)
                throw new InvalidOperationException($"The activity chart did not update its {theme} palette.");
        }
        if (palettes.Count != 4) throw new InvalidOperationException("The four appearance choices did not produce distinct palettes.");
        model.Settings.Theme = "Dark";
        await SettleAsync(window);
    }

    private static async Task VerifyEditorAndFocusGeometryAsync(Window window, DataGrid filesGrid, string outputDirectory)
    {
        foreach (var editor in VisualDescendants(window).OfType<SearchBox>().Where(editor => editor.IsVisible && editor.Text.Length == 0))
            VerifyCaretAlignment(editor);
        if (window.Content is not Panel root) throw new InvalidOperationException("The input verification needs a rendered window content panel.");
        var fixture = new StackPanel
        {
            Width = 480,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)window.FindResource("SurfaceBrush")
        };
        var metrics = new List<string>();
        Panel.SetZIndex(fixture, 10000);
        root.Children.Add(fixture);
        try
        {
            var starts = new Dictionary<(bool Icon, bool Custom), double>();
            foreach (var icon in new[] { true, false })
            foreach (var custom in new[] { false, true })
            {
                var editor = new SearchBox
                {
                    Placeholder = "Type at the beginning of this line",
                    ShowSearchIcon = icon,
                    Height = 54,
                    Margin = new Thickness(12, 6, 12, 6),
                    FontSize = custom ? 19 : 12,
                    Padding = custom ? new Thickness(19, 10, 23, 7) : new Thickness(10, 6, 10, 6)
                };
                fixture.Children.Add(editor);
                await SettleAsync(window);
                var empty = VerifyCaretAlignment(editor);
                starts[(icon, custom)] = empty.X;
                editor.Text = "M";
                await SettleAsync(window);
                var typed = editor.GetRectFromCharacterIndex(0);
                if (typed.IsEmpty || Math.Abs(typed.X - empty.X) > 1 || Math.Abs(typed.Y - empty.Y) > 1)
                    throw new InvalidOperationException($"Typing shifted the search input start: empty {empty}, typed {typed}.");
                metrics.Add($"Icon={icon}, CustomPadding={custom}, FontSize={editor.FontSize}: empty={empty}, typed={typed}");
                editor.Text = "";
            }
            foreach (var icon in new[] { true, false })
                if (Math.Abs(starts[(icon, true)] - starts[(icon, false)] - 9) > 1)
                    throw new InvalidOperationException("Search input text did not respect the requested left padding.");
            var cellGrid = new DataGrid { Height = 100, Margin = new Thickness(12), ItemsSource = new[] { "Cell focus remains available" }, SelectionUnit = DataGridSelectionUnit.Cell };
            cellGrid.Columns.Add(new DataGridTextColumn { Header = "Cell selection fixture", Binding = new Binding(), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            fixture.Children.Add(cellGrid);
            await SettleAsync(window);
            var cell = VisualDescendants(cellGrid).OfType<DataGridCell>().First();
            if (!cell.Focus()) throw new InvalidOperationException("The cell-selection fixture could not receive focus.");
            await SettleAsync(window);
            if (!cell.IsKeyboardFocusWithin || cell.Template.FindName("Chrome", cell) is not Border chrome || !HasVisibleStroke(chrome))
                throw new InvalidOperationException("A grid with explicit cell selection lost its keyboard focus indicator.");
            Capture(window, Path.Combine(outputDirectory, "input-geometry.png"), 1);
        }
        finally { root.Children.Remove(fixture); }
        await File.WriteAllLinesAsync(Path.Combine(outputDirectory, "input-geometry.txt"), metrics);
        filesGrid.ScrollIntoView(filesGrid.SelectedItem ?? filesGrid.Items[0], filesGrid.Columns[0]);
        await SettleAsync(window);
        var selectedRow = VisualDescendants(filesGrid).OfType<DataGridRow>().FirstOrDefault(row => row.IsSelected)
            ?? throw new InvalidOperationException("The file grid has no selected row for keyboard verification.");
        var focusedCell = VisualDescendants(selectedRow).OfType<DataGridCell>().First();
        if (!focusedCell.Focus()) throw new InvalidOperationException("The selected file row could not receive keyboard focus.");
        await SettleAsync(window);
        if (!focusedCell.IsKeyboardFocusWithin || filesGrid.SelectionUnit != DataGridSelectionUnit.FullRow || !selectedRow.IsSelected)
            throw new InvalidOperationException("The file grid did not retain whole-row selection under keyboard focus.");
        if (focusedCell.Template.FindName("Chrome", focusedCell) is not Border rowChrome || HasVisibleStroke(rowChrome))
            throw new InvalidOperationException("Whole-row selection still draws a separate cell focus border.");
        Capture(window, Path.Combine(outputDirectory, "row-focus.png"), 1);
    }

    private static Rect VerifyCaretAlignment(SearchBox editor)
    {
        if (editor.Template.FindName("Placeholder", editor) is not TextBlock placeholder || !placeholder.IsVisible)
            throw new InvalidOperationException("An empty search input did not display its placeholder.");
        var caret = editor.GetRectFromCharacterIndex(0);
        var glyph = placeholder.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        if (caret.IsEmpty || glyph.IsEmpty) throw new InvalidOperationException("Search input text geometry did not become available after layout.");
        var origin = placeholder.TransformToAncestor(editor).Transform(glyph.TopLeft);
        if (Math.Abs(caret.X - origin.X) > 1 || Math.Abs(caret.Y - origin.Y) > 1)
            throw new InvalidOperationException($"Search caret and placeholder are misaligned: caret {caret.TopLeft}, placeholder {origin}, padding {editor.Padding}, icon {editor.ShowSearchIcon}.");
        return caret;
    }

    private static bool HasVisibleStroke(Border border) =>
        border.BorderThickness != new Thickness(0) && border.BorderBrush is { Opacity: > 0 } brush &&
        (brush is not SolidColorBrush solid || solid.Color.A != 0);

    private static void VerifyButtonContrast(Button button)
    {
        var label = VisualDescendants(button).OfType<TextBlock>().FirstOrDefault(element => element.IsVisible && element.Text == button.Content?.ToString());
        if (label?.Foreground is not SolidColorBrush foreground ||
            button.Template.FindName("Chrome", button) is not Border { Background: SolidColorBrush background })
            throw new InvalidOperationException("The primary button does not expose its rendered foreground and background.");
        static double Luminance(Color color)
        {
            static double Linear(byte channel) => channel / 255d <= 0.04045 ? channel / 255d / 12.92 : Math.Pow((channel / 255d + 0.055) / 1.055, 2.4);
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        var first = Luminance(foreground.Color);
        var second = Luminance(background.Color);
        var contrast = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
        if (contrast < 4.5) throw new InvalidOperationException($"The primary-button text contrast is only {contrast:0.00}:1.");
    }

    private static async Task VerifyActivitySelectionAsync(Window window, MainViewModel model, string outputDirectory)
    {
        var chart = VisualDescendants(window).OfType<ActivityChart>().FirstOrDefault()
            ?? throw new InvalidOperationException("The activity chart did not render.");
        if (!ReferenceEquals(chart.ItemsSource, model.Browser.Activity) || chart.ActualWidth <= 0 || chart.ActualHeight <= 0)
            throw new InvalidOperationException("The activity chart is not displaying the branch activity.");
        model.Browser.SelectedActivity = null;
        var source = PresentationSource.FromVisual(chart) ?? throw new InvalidOperationException("The activity chart has no presentation source.");
        chart.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Right)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
        await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
        if (model.Browser.SelectedActivity is not { } selected || model.Browser.DatePreset != "Custom" ||
            model.Browser.FromDate != selected.Date.Date || model.Browser.UntilDate != selected.Date.Date)
            throw new InvalidOperationException("Keyboard activity selection did not filter the selected day.");
        var activity = model.Browser.Activity.OrderBy(point => point.Date).ToArray();
        if (activity.Length >= 2)
        {
            model.Browser.SelectedActivity = activity[^1];
            await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
            await SettleAsync(window);
            chart.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Left)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });
            await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
            if (model.Browser.SelectedActivity?.Date != activity[^2].Date)
                throw new InvalidOperationException("Activity keyboard navigation did not follow an externally updated selected day.");
            selected = model.Browser.SelectedActivity;
        }
        chart.SetCurrentValue(ActivityChart.RangeStartProperty, selected.Date.Date.AddDays(-1));
        chart.SetCurrentValue(ActivityChart.RangeEndProperty, selected.Date.Date.AddDays(1));
        await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
        if (model.Browser.FromDate != selected.Date.Date.AddDays(-1) || model.Browser.UntilDate != selected.Date.Date)
            throw new InvalidOperationException("The activity range did not preserve its exclusive end date.");
        await SettleAsync(window);
        Capture(window, Path.Combine(outputDirectory, "custom-range.png"), 1);
        var datePicker = VisualDescendants(window).OfType<DatePicker>().FirstOrDefault(picker => picker.IsVisible)
            ?? throw new InvalidOperationException("Custom ranges did not expose a date editor.");
        if (datePicker.SelectedDate is null) throw new InvalidOperationException("The custom date editor is not bound to a date.");
        datePicker.IsDropDownOpen = true;
        await SettleAsync(window);
        if (datePicker.Template.FindName("PART_Popup", datePicker) is not Popup { Child: FrameworkElement calendar } ||
            calendar.ActualWidth <= 0 || calendar.ActualHeight <= 0)
            throw new InvalidOperationException("The native date picker did not open its calendar.");
        CaptureElement(calendar, Path.Combine(outputDirectory, "date-picker.png"));
        datePicker.IsDropDownOpen = false;
        model.Browser.SelectedActivity = null;
        model.Browser.DatePreset = "7 days";
        await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
        await SettleAsync(window);
    }
    private static async Task VerifyWorkspaceFilteringAsync(Window window, MainViewModel model)
    {
        if (window.FindName("RepositoryNavigator") is not ListBox navigator) throw new InvalidOperationException("The workspace navigator is missing.");
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

    private static async Task VerifyWorkspaceActivationAsync(Window window, ListBox navigator)
    {
        var sourceBinding = BindingOperations.GetBindingBase(navigator, ListBox.ItemsSourceProperty)
            ?? throw new InvalidOperationException("The navigator source is not bound.");
        var selectedBinding = BindingOperations.GetBindingBase(navigator, ListBox.SelectedItemProperty)
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
            BindingOperations.ClearBinding(navigator, ListBox.SelectedItemProperty);
            BindingOperations.ClearBinding(navigator, ListBox.ItemsSourceProperty);
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
            BindingOperations.SetBinding(navigator, ListBox.ItemsSourceProperty, sourceBinding);
            BindingOperations.SetBinding(navigator, ListBox.SelectedItemProperty, selectedBinding);
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
        if (window.FindName("FolderTree") is not TreeView tree)
            throw new InvalidOperationException("The native folder tree is missing.");
        var folder = model.Browser.TreeNodes.First(node => node.Path.Length > 0 && node.ParentId is not null);
        await SettleAsync(window);
        var folderRow = VisualDescendants(tree).OfType<TreeViewItem>()
            .FirstOrDefault(item => item.Header is DirectoryTreeItem wrapped && wrapped.Node.Id == folder.Id)
            ?? throw new InvalidOperationException("The folder hierarchy did not render its children.");
        folderRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseUpEvent
        });
        await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
        if (model.Browser.SelectedNode?.Id != folder.Id)
            throw new InvalidOperationException("Clicking the folder row did not activate that folder.");
        model.Browser.FolderSearch = folder.Name;
        await SettleAsync(window);
        if (!model.Browser.VisibleTreeNodes.Any(node => node.Id == folder.Id) ||
            !model.Browser.VisibleTreeNodes.Any(node => node.Id == folder.ParentId))
            throw new InvalidOperationException("Folder search did not retain the matching folder and its ancestor.");
        if (tree.SelectedItem is not DirectoryTreeItem selected || selected.Node.Id != folder.Id)
            throw new InvalidOperationException("Folder search did not restore the active folder's tree highlight.");
        Capture(window, Path.Combine(outputDirectory, "folder-search.png"), 1);
        model.Browser.FolderSearch = "no-folder-can-match-this-value";
        await SettleAsync(window);
        if (model.Browser.SelectedNode?.Id != folder.Id) throw new InvalidOperationException("Folder search changed the active folder.");
        model.Browser.FolderSearch = "";
        await SettleAsync(window);
        if (tree.SelectedItem is not DirectoryTreeItem restored || restored.Node.Id != folder.Id)
            throw new InvalidOperationException("Clearing folder search did not restore the active folder's tree highlight.");
        model.Browser.ClearFiltersCommand.Execute(null);
        await model.Browser.ApplyFilterCommand.ExecuteAsync(null);
        await model.Details.LoadDiffCommand.ExecuteAsync(null);
    }

    private static async Task VerifyRowContextMenuAsync(Window window, MainViewModel model, DataGrid grid, string outputDirectory)
    {
        await SettleAsync(window);
        var clickedRow = grid.Items[1] as FileRow ?? throw new InvalidOperationException("The context menu test needs a second file row.");
        model.Browser.SelectedFile = grid.Items[0] as FileRow;
        grid.ScrollIntoView(clickedRow);
        await SettleAsync(window);
        var rowElement = grid.ItemContainerGenerator.ContainerFromItem(clickedRow) as DataGridRow ?? throw new InvalidOperationException("The second file row is not rendered.");
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
        var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(element) { AutoLayoutContent = false, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds }, null, bounds);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task VerifyCommitContextMenuAsync(Window window, MainViewModel model)
    {
        if (window.FindName("HistoryGrid") is not DataGrid grid || grid.ItemsSource != model.Details.History)
            throw new InvalidOperationException("The history grid is not bound to the selected file history.");
        foreach (var file in model.Browser.Files)
        {
            model.Browser.SelectedFile = file;
            if (model.Details.History.Count >= 2) break;
        }
        await model.Details.LoadDiffCommand.ExecuteAsync(null);
        await SettleAsync(window);
        if (grid.Items.Count < 2 || grid.Items[1] is not FileChange clickedCommit)
            throw new InvalidOperationException("The commit context-menu test needs two history entries.");
        model.Details.SelectedChange = (FileChange)grid.Items[0];
        grid.ScrollIntoView(clickedCommit);
        await SettleAsync(window);
        var row = grid.ItemContainerGenerator.ContainerFromItem(clickedCommit) as DataGridRow
            ?? throw new InvalidOperationException("The second history row did not render.");
        row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });
        await SettleAsync(window);
        if (!ReferenceEquals(model.Details.SelectedChange, clickedCommit))
            throw new InvalidOperationException("Right-click did not select the pointed commit.");
        var menu = grid.ContextMenu ?? throw new InvalidOperationException("Commit context actions are missing.");
        menu.PlacementTarget = grid;
        menu.ApplyTemplate();
        await SettleAsync(window);
        var action = menu.Items.OfType<MenuItem>().First();
        if (action.Command != model.Actions.OpenCommitBrowserCommand || !ReferenceEquals(action.CommandParameter, clickedCommit))
            throw new InvalidOperationException("The commit context menu did not target the right-clicked commit.");
    }
    private static void ExecuteShortcut(Window window, Key key, ModifierKeys modifiers)
    {
        var binding = window.InputBindings.OfType<KeyBinding>().Single(b => b.Key == key && b.Modifiers == modifiers);
        if (binding.Command is RoutedCommand routed)
        {
            var target = binding.CommandTarget ?? window;
            if (!routed.CanExecute(binding.CommandParameter, target)) throw new InvalidOperationException($"The {modifiers}+{key} shortcut is unavailable.");
            routed.Execute(binding.CommandParameter, target);
            return;
        }
        if (!binding.Command.CanExecute(binding.CommandParameter)) throw new InvalidOperationException($"The {modifiers}+{key} shortcut is unavailable.");
        binding.Command.Execute(binding.CommandParameter);
    }
    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        // Allow binding updates, virtualization and animated indicators to reach their next render pass.
        await Task.Delay(400);
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void Capture(Window window, string path, double scale)
    {
        var content = window.Content as FrameworkElement ?? window;
        var width = (int)Math.Ceiling(content.ActualWidth * scale);
        var height = (int)Math.Ceiling(content.ActualHeight * scale);
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
public sealed record UiVerificationReport(int RecentFiles, int AllFiles, int HistoryEntries, int DiffLines, int WindowKeyBindings,
    bool GridSortingVerified, bool WorkspaceLayoutRoundTripVerified, bool ShortcutBindingsVerified, bool WorkspaceFilterSelectionVerified, bool WorkspaceActivationVerified,
    bool FolderSearchVerified, bool ContextMenuRowTargetVerified, bool FolderColumnAndAgeAlignmentVerified, bool PrimaryButtonContrastVerified,
    bool ActivitySelectionAndRangeVerified, string DiagnosticsDirectory, string ScalingVerification)
{
    public bool FourThemesVerified { get; init; }
    public bool SearchCaretAlignmentVerified { get; init; }
    public bool WholeRowFocusVerified { get; init; }
    public bool SidebarAndLayoutModesVerified { get; init; }
    public bool CompactCustomRangeVerified { get; init; }
    public bool LegacyWorkspaceMigrationVerified { get; init; }
}
[JsonSerializable(typeof(UiVerificationReport))]
internal sealed partial class UiVerificationJsonContext : JsonSerializerContext;
