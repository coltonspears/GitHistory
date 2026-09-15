using System.ComponentModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using GitHistory.Core.Services;
using GitHistory.UI.Controls;
using GitHistory.UI.Theming;
using Microsoft.Extensions.Logging;

namespace GitHistory.App.Services;

/// <summary>Owns view-only theme and native WPF workspace preferences, outside view models.</summary>
public sealed partial class AppearanceService(ILogger<AppearanceService> logger, string? dataDirectory = null) : IAppearanceService, IDisposable
{
    private const string LayoutFileName = "workspace-v1.json";
    private readonly string layoutDirectory = Path.Combine(dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHistory"), "layout");
    private Window? window;
    private bool persistLayout;
    private bool isLightTheme;
    private byte[]? defaultLayout;
    private readonly List<GridSubscription> gridSubscriptions = [];
    private static readonly DependencyProperty GridSortPreferencesProperty = DependencyProperty.RegisterAttached(
        "GridSortPreferences", typeof(SortPreference[]), typeof(AppearanceService), new PropertyMetadata(null));
    private static readonly DependencyProperty GridGroupPreferencesProperty = DependencyProperty.RegisterAttached(
        "GridGroupPreferences", typeof(string[]), typeof(AppearanceService), new PropertyMetadata(null));
    private static readonly DependencyProperty RestoreGenerationProperty = DependencyProperty.RegisterAttached(
        "RestoreGeneration", typeof(object), typeof(AppearanceService), new PropertyMetadata(null));
    private static readonly DependencyPropertyDescriptor ItemsSourceDescriptor = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid));

    public void SetTheme(string theme)
    {
        isLightTheme = ThemeCatalog.Apply(Application.Current, theme).IsLight;
        if (window is not null) NativeWindowTheme.Apply(window, isLightTheme);
    }

    public void AttachWindow(Window attachedWindow, bool persistLayout = true)
    {
        DetachWindow();
        window = attachedWindow;
        this.persistLayout = persistLayout;
        window.Loaded += OnLoaded;
        window.SourceInitialized += OnSourceInitialized;
        window.Closed += OnClosed;
        if (window.IsLoaded) OnLoaded(window, new RoutedEventArgs());
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        if (window is not null) NativeWindowTheme.Apply(window, isLightTheme);
    }

    public void ResetLayout()
    {
        if (window is null || defaultLayout is null) return;
        try
        {
            using var defaults = new MemoryStream(defaultLayout, writable: false);
            RestoreWorkspaceLayout(window, defaults);
            string savedPath = Path.Combine(layoutDirectory, LayoutFileName);
            if (persistLayout && File.Exists(savedPath)) File.Delete(savedPath);
        }
        catch (Exception exception) when (IsLayoutException(exception)) { LayoutFailure(logger, LayoutFileName, exception); }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (window is null) return;
        window.Loaded -= OnLoaded;
        NativeWindowTheme.Apply(window, isLightTheme);
        try
        {
            TrackGridSorting(window);
            using var defaults = new MemoryStream();
            SaveWorkspaceLayout(window, defaults);
            defaultLayout = defaults.ToArray();
            string path = Path.Combine(layoutDirectory, LayoutFileName);
            if (!persistLayout || !File.Exists(path)) return;
            try
            {
                using var saved = File.OpenRead(path);
                RestoreWorkspaceLayout(window, saved);
            }
            catch (Exception exception) when (IsLayoutException(exception))
            {
                // Restore defaults if a stale or corrupt preference file was only partially applied.
                using var fallback = new MemoryStream(defaultLayout, writable: false);
                RestoreWorkspaceLayout(window, fallback);
                LayoutFailure(logger, LayoutFileName, exception);
            }
        }
        catch (Exception exception) when (IsLayoutException(exception)) { LayoutFailure(logger, LayoutFileName, exception); }
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        if (persistLayout && window is not null && defaultLayout is not null)
        {
            try
            {
                Directory.CreateDirectory(layoutDirectory);
                string path = Path.Combine(layoutDirectory, LayoutFileName);
                string temporary = path + ".tmp";
                using (var stream = File.Create(temporary)) SaveWorkspaceLayout(window, stream);
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception exception) when (IsLayoutException(exception)) { LayoutFailure(logger, LayoutFileName, exception); }
        }
        DetachWindow();
    }

    /// <summary>Captures the same pane and column preferences used by startup persistence.</summary>
    public static void SaveWorkspaceLayout(Window window, Stream destination)
    {
        var workspace = window.FindName("WorkspaceDock") as WorkspaceLayout;
        var layout = workspace is null ? null : new LayoutPreference(workspace.Layout, workspace.CaptureLayouts().ToArray());
        var sidebar = window.FindName("WorkspaceSidebar") is Sidebar navigation
            ? new SidebarPreference(navigation.IsExpanded, navigation.ExpandedWidth) : null;
        var grids = new List<GridPreference>();
        foreach (string name in new[] { "FilesGrid", "HistoryGrid", "DiffGrid" })
        {
            if (window.FindName(name) is not DataGrid grid) continue;
            var columns = grid.Columns.Select((column, index) => new ColumnPreference(index, column.DisplayIndex,
                column.Width.Value, column.Width.UnitType, column.SortDirection, column.Visibility)).ToArray();
            var sorts = grid.ItemsSource is null ? grid.GetValue(GridSortPreferencesProperty) as SortPreference[] ?? [] : CaptureSorting(grid);
            var groups = grid.ItemsSource is null ? grid.GetValue(GridGroupPreferencesProperty) as string[] ?? [] : CaptureGrouping(grid);
            grids.Add(new(name, columns, sorts, groups));
        }
        JsonSerializer.Serialize(destination, new WorkspacePreferences([], grids.ToArray(), layout, sidebar), WorkspacePreferencesJsonContext.Default.WorkspacePreferences);
    }

    /// <summary>Applies saved sizes, column order, and sorting to standard WPF controls.</summary>
    public static void RestoreWorkspaceLayout(Window window, Stream source)
    {
        var preferences = JsonSerializer.Deserialize(source, WorkspacePreferencesJsonContext.Default.WorkspacePreferences)
            ?? throw new JsonException("Workspace preferences were empty.");
        var generation = new object();
        window.SetValue(RestoreGenerationProperty, generation);
        var deferredWidths = new List<(DataGrid Grid, DataGridColumn Column, DataGridLength Width)>();
        if (preferences.Panes is null || preferences.Grids is null) throw new JsonException("Workspace preferences were incomplete.");
        foreach (var pane in preferences.Panes)
        {
            if (pane is null || string.IsNullOrWhiteSpace(pane.Name)) throw new JsonException("Invalid pane preferences.");
            if (!double.IsFinite(pane.Size) || pane.Size < 0 || !Enum.IsDefined(pane.Unit)) throw new JsonException("Invalid pane size.");
            var size = new GridLength(pane.Size, pane.Unit);
            switch (window.FindName(pane.Name))
            {
                case RowDefinition row: row.Height = size; break;
                case ColumnDefinition column: column.Width = size; break;
            }
        }
        if (preferences.Layout is { } savedLayout && window.FindName("WorkspaceDock") is WorkspaceLayout workspace)
        {
            if (savedLayout.States is null) throw new JsonException("Workspace layouts were incomplete.");
            workspace.RestoreLayouts(savedLayout.States, savedLayout.Selected);
        }
        else if (preferences.Layout is null && window.FindName("WorkspaceDock") is WorkspaceLayout legacyWorkspace)
        {
            RestoreLegacyPanes(legacyWorkspace, preferences.Panes);
        }
        if (preferences.Sidebar is { } savedSidebar && window.FindName("WorkspaceSidebar") is Sidebar sidebar)
        {
            if (!double.IsFinite(savedSidebar.ExpandedWidth) || savedSidebar.ExpandedWidth is < 220 or > 420)
                throw new JsonException("Invalid sidebar width.");
            sidebar.SetCurrentValue(Sidebar.ExpandedWidthProperty, savedSidebar.ExpandedWidth);
            sidebar.SetCurrentValue(Sidebar.IsExpandedProperty, savedSidebar.IsExpanded);
        }
        foreach (var savedGrid in preferences.Grids)
        {
            if (savedGrid is null || string.IsNullOrWhiteSpace(savedGrid.Name)) throw new JsonException("Invalid grid preferences.");
            if (window.FindName(savedGrid.Name) is not DataGrid grid) continue;
            if (savedGrid.Columns is null || savedGrid.Sort is null || savedGrid.Columns.Any(column => column is null)) throw new JsonException("Grid preferences were incomplete.");
            // Column sets may change in later versions; retain defaults for newly added columns.
            foreach (var saved in savedGrid.Columns.OrderBy(column => column.DisplayIndex))
            {
                if (saved.Index < 0 || saved.Index >= grid.Columns.Count) continue;
                if (!double.IsFinite(saved.Width) || saved.Width < 0 || !Enum.IsDefined(saved.Unit) || !Enum.IsDefined(saved.Visibility) ||
                    (saved.Direction is { } direction && !Enum.IsDefined(direction))) throw new JsonException("Invalid column preferences.");
                var column = grid.Columns[saved.Index];
                // An equivalent new star length discards WPF's calculated display width. While
                // pane resizing is queued, its later viewport callback can then shrink it twice.
                if (column.Width.Value != saved.Width || column.Width.UnitType != saved.Unit)
                {
                    var width = new DataGridLength(saved.Width, saved.Unit);
                    if (width.IsStar) deferredWidths.Add((grid, column, width));
                    else column.Width = width;
                }
                column.DisplayIndex = Math.Clamp(saved.DisplayIndex, 0, grid.Columns.Count - 1);
                column.SortDirection = saved.Direction;
                column.Visibility = saved.Visibility;
            }
            if (savedGrid.Sort.Any(sort => sort is null || string.IsNullOrWhiteSpace(sort.Property) || !Enum.IsDefined(sort.Direction))) throw new JsonException("Invalid sort direction.");
            grid.SetValue(GridSortPreferencesProperty, savedGrid.Sort);
            grid.SetValue(GridGroupPreferencesProperty, savedGrid.Groups ?? []);
            ApplySorting(grid, savedGrid.Sort);
            ApplyGrouping(grid, savedGrid.Groups ?? []);
        }
        if (deferredWidths.Count > 0)
        {
            // Loaded-priority viewport updates must finish before recalculating star widths.
            // A newer restore/reset supersedes every callback from this operation.
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!ReferenceEquals(window.GetValue(RestoreGenerationProperty), generation)) return;
                foreach (var (grid, column, width) in deferredWidths)
                    if (grid.Columns.Contains(column)) column.Width = width;
            });
        }
    }

    private static void RestoreLegacyPanes(WorkspaceLayout workspace, PanePreference[] panes)
    {
        // The first native shell stored named GridLengths. Its arrangement corresponds to Balanced;
        // the newer control stores proportions so it can reuse the same panes in several arrangements.
        PanePreference? Find(string name) => panes.LastOrDefault(pane => pane.Name == name);
        double columnDefault = (double)WorkspaceLayout.ColumnRatioProperty.DefaultMetadata.DefaultValue;
        double rowDefault = (double)WorkspaceLayout.RowRatioProperty.DefaultMetadata.DefaultValue;
        double columnsAvailable = workspace.ActualWidth - workspace.ColumnDefinitions[1].Width.Value;
        double rowsAvailable = workspace.ActualHeight - workspace.RowDefinitions[1].Height.Value;
        double columnRatio = LegacyRatio(Find("HistoryColumn"), Find("DiffColumn"), columnsAvailable, columnDefault);
        double rowRatio = LegacyRatio(Find("FilesRow"), Find("InspectionRow"), rowsAvailable, rowDefault);
        workspace.RestoreLayouts([new(WorkspaceLayoutPreset.Balanced, columnRatio, rowRatio)], WorkspaceLayoutPreset.Balanced);
    }

    private static double LegacyRatio(PanePreference? first, PanePreference? second, double available, double fallback)
    {
        if (first is null || second is null || first.Unit == GridUnitType.Auto || second.Unit == GridUnitType.Auto) return fallback;
        double ratio;
        if (first.Unit == second.Unit)
        {
            // Normalize before summing so even large finite star weights cannot overflow.
            double scale = Math.Max(first.Size, second.Size);
            if (scale <= 0) return fallback;
            double firstWeight = first.Size / scale;
            double secondWeight = second.Size / scale;
            ratio = firstWeight / (firstWeight + secondWeight);
        }
        else
        {
            // A fixed pane followed by a star pane used the remaining workspace pixels. Preserve
            // its requested size when possible, leaving at least one pixel for either region.
            if (!double.IsFinite(available) || available <= 2) return fallback;
            bool firstIsPixel = first.Unit == GridUnitType.Pixel;
            double pixels = firstIsPixel ? first.Size : second.Size;
            double pixelRatio = Math.Clamp(pixels, 1, available - 1) / available;
            ratio = firstIsPixel ? pixelRatio : 1 - pixelRatio;
        }
        return double.IsFinite(ratio) && ratio is > 0 and < 1 ? ratio : fallback;
    }

    private void TrackGridSorting(Window window)
    {
        foreach (string name in new[] { "FilesGrid", "HistoryGrid", "DiffGrid" })
        {
            if (window.FindName(name) is not DataGrid grid) continue;
            grid.SetValue(GridSortPreferencesProperty, CaptureSorting(grid));
            grid.SetValue(GridGroupPreferencesProperty, CaptureGrouping(grid));
            ICollectionView? trackedView = null;
            NotifyCollectionChangedEventHandler groupingChanged = (_, _) => grid.SetValue(GridGroupPreferencesProperty, CaptureGrouping(grid));
            void TrackGrouping()
            {
                if (trackedView?.GroupDescriptions is { } previous) previous.CollectionChanged -= groupingChanged;
                trackedView = CollectionViewSource.GetDefaultView(grid.ItemsSource);
                if (trackedView?.GroupDescriptions is { } current) current.CollectionChanged += groupingChanged;
            }
            TrackGrouping();
            // DataGrid clears collection sorting when its bound source changes. Keep the user's
            // preferences across filters, refreshes, and startup's asynchronous first query.
            EventHandler sourceChanged = (_, _) => grid.Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
            {
                if (grid.GetValue(GridSortPreferencesProperty) is SortPreference[] sorts) ApplySorting(grid, sorts);
                if (grid.GetValue(GridGroupPreferencesProperty) is string[] groups) ApplyGrouping(grid, groups);
                TrackGrouping();
            });
            DataGridSortingEventHandler sorting = (_, _) => grid.Dispatcher.BeginInvoke(DispatcherPriority.DataBind,
                () => grid.SetValue(GridSortPreferencesProperty, CaptureSorting(grid)));
            ItemsSourceDescriptor.AddValueChanged(grid, sourceChanged);
            grid.Sorting += sorting;
            gridSubscriptions.Add(new(grid, sourceChanged, sorting, () =>
            {
                if (trackedView?.GroupDescriptions is { } current) current.CollectionChanged -= groupingChanged;
            }));
        }
    }

    private static SortPreference[] CaptureSorting(DataGrid grid) => CollectionViewSource.GetDefaultView(grid.ItemsSource)?.SortDescriptions
        .Select(sort => new SortPreference(sort.PropertyName, sort.Direction)).ToArray() ?? [];

    private static void ApplySorting(DataGrid grid, SortPreference[] sorts)
    {
        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        if (view is not { CanSort: true }) return;
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            foreach (var sort in sorts)
            {
                if (grid.Columns.Any(column => column.CanUserSort && column.SortMemberPath == sort.Property))
                    view.SortDescriptions.Add(new SortDescription(sort.Property, sort.Direction));
            }
            foreach (var column in grid.Columns) column.SortDirection = sorts.FirstOrDefault(sort => sort.Property == column.SortMemberPath)?.Direction;
        }
    }

    private static string[] CaptureGrouping(DataGrid grid) => CollectionViewSource.GetDefaultView(grid.ItemsSource)?.GroupDescriptions
        .OfType<PropertyGroupDescription>().Select(group => group.PropertyName).ToArray() ?? [];

    private static void ApplyGrouping(DataGrid grid, string[] groups)
    {
        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        if (view is not { CanGroup: true }) return;
        using (view.DeferRefresh())
        {
            view.GroupDescriptions.Clear();
            foreach (string property in groups)
                if (!string.IsNullOrWhiteSpace(property) && grid.Columns.Any(column => column.SortMemberPath == property))
                    view.GroupDescriptions.Add(new PropertyGroupDescription(property));
        }
    }

    private static bool IsLayoutException(Exception exception) => exception is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or ArgumentException;

    private void DetachWindow()
    {
        if (window is not null)
        {
            window.Loaded -= OnLoaded;
            window.SourceInitialized -= OnSourceInitialized;
            window.Closed -= OnClosed;
        }
        foreach (var subscription in gridSubscriptions)
        {
            ItemsSourceDescriptor.RemoveValueChanged(subscription.Grid, subscription.SourceChanged);
            subscription.Grid.Sorting -= subscription.Sorting;
            subscription.DetachGrouping();
        }
        gridSubscriptions.Clear();
        window = null;
        defaultLayout = null;
    }

    public void Dispose() => DetachWindow();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save or restore workspace layout {LayoutName}; using the available layout.")]
    private static partial void LayoutFailure(ILogger logger, string layoutName, Exception exception);

    private sealed record GridSubscription(DataGrid Grid, EventHandler SourceChanged, DataGridSortingEventHandler Sorting, Action DetachGrouping);
}

internal sealed record WorkspacePreferences(PanePreference[] Panes, GridPreference[] Grids, LayoutPreference? Layout = null, SidebarPreference? Sidebar = null);
internal sealed record LayoutPreference(WorkspaceLayoutPreset Selected, WorkspaceLayoutState[] States);
internal sealed record SidebarPreference(bool IsExpanded, double ExpandedWidth);
internal sealed record PanePreference(string Name, double Size, GridUnitType Unit);
internal sealed record GridPreference(string Name, ColumnPreference[] Columns, SortPreference[] Sort, string[]? Groups = null);
internal sealed record ColumnPreference(int Index, int DisplayIndex, double Width, DataGridLengthUnitType Unit, ListSortDirection? Direction, Visibility Visibility = Visibility.Visible);
internal sealed record SortPreference(string Property, ListSortDirection Direction);
[JsonSerializable(typeof(WorkspacePreferences))]
internal sealed partial class WorkspacePreferencesJsonContext : JsonSerializerContext;
