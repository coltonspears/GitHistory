using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace GitHistory.UI.Controls;

public enum WorkspaceLayoutPreset { Balanced, SideBySide, FocusDiff }
public enum WorkspaceRegion { Primary, History, Inspector }
public sealed record WorkspaceLayoutOption(WorkspaceLayoutPreset Value, string Name);
public sealed record WorkspaceLayoutState(WorkspaceLayoutPreset Layout, double ColumnRatio, double RowRatio);

/// <summary>Three persistent, resizable pane arrangements. Switching layouts retains the same child controls.</summary>
public class WorkspaceLayout : Grid
{
    private readonly GridSplitter mainSplitter;
    private readonly GridSplitter secondarySplitter;
    private readonly Dictionary<WorkspaceLayoutPreset, WorkspaceLayoutState> states = [];
    private bool applying;

    public static IReadOnlyList<WorkspaceLayoutOption> Presets { get; } = Array.AsReadOnly(new[]
    {
        new WorkspaceLayoutOption(WorkspaceLayoutPreset.Balanced, "Balanced"),
        new WorkspaceLayoutOption(WorkspaceLayoutPreset.SideBySide, "Side by side"),
        new WorkspaceLayoutOption(WorkspaceLayoutPreset.FocusDiff, "Focus diff")
    });

    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(WorkspaceLayoutPreset), typeof(WorkspaceLayout),
        new FrameworkPropertyMetadata(WorkspaceLayoutPreset.Balanced, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnLayoutChanged),
        value => Enum.IsDefined((WorkspaceLayoutPreset)value));
    public static readonly DependencyProperty ColumnRatioProperty = DependencyProperty.Register(
        nameof(ColumnRatio), typeof(double), typeof(WorkspaceLayout),
        new FrameworkPropertyMetadata(.28d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRatioChanged), IsRatio);
    public static readonly DependencyProperty RowRatioProperty = DependencyProperty.Register(
        nameof(RowRatio), typeof(double), typeof(WorkspaceLayout),
        new FrameworkPropertyMetadata(.46d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRatioChanged), IsRatio);
    public static readonly DependencyProperty RegionProperty = DependencyProperty.RegisterAttached(
        "Region", typeof(WorkspaceRegion), typeof(WorkspaceLayout), new FrameworkPropertyMetadata(WorkspaceRegion.Primary, OnRegionChanged),
        value => Enum.IsDefined((WorkspaceRegion)value));

    public WorkspaceLayoutPreset Layout { get => (WorkspaceLayoutPreset)GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
    public double ColumnRatio { get => (double)GetValue(ColumnRatioProperty); set => SetValue(ColumnRatioProperty, value); }
    public double RowRatio { get => (double)GetValue(RowRatioProperty); set => SetValue(RowRatioProperty, value); }
    public static WorkspaceRegion GetRegion(DependencyObject element) => (WorkspaceRegion)element.GetValue(RegionProperty);
    public static void SetRegion(DependencyObject element, WorkspaceRegion value) => element.SetValue(RegionProperty, value);
    private static bool IsRatio(object value) => value is double ratio && double.IsFinite(ratio) && ratio is > 0 and < 1;

    public WorkspaceLayout()
    {
        ClipToBounds = true;
        for (int index = 0; index < 3; index++)
        {
            RowDefinitions.Add(new RowDefinition());
            ColumnDefinitions.Add(new ColumnDefinition());
        }
        RowDefinitions[1].Height = new GridLength(10);
        ColumnDefinitions[1].Width = new GridLength(10);
        mainSplitter = CreateSplitter();
        secondarySplitter = CreateSplitter();
        Children.Add(mainSplitter);
        Children.Add(secondarySplitter);
        ApplyArrangement();
    }

    private GridSplitter CreateSplitter()
    {
        var splitter = new GridSplitter
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            KeyboardIncrement = 16,
            DragIncrement = 1,
            Focusable = true,
            Background = Brushes.Transparent
        };
        splitter.SetResourceReference(StyleProperty, "WorkspaceSplitter");
        splitter.DragCompleted += (_, _) => RememberCurrent(Layout);
        splitter.KeyUp += (_, _) => RememberCurrent(Layout);
        return splitter;
    }

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (mainSplitter is not null && secondarySplitter is not null) ArrangeRegions();
    }

    private static void OnRegionChanged(DependencyObject element, DependencyPropertyChangedEventArgs _)
    {
        if (element is Visual visual && VisualTreeHelper.GetParent(visual) is WorkspaceLayout owner) owner.ArrangeRegions();
    }

    private static void OnLayoutChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        var owner = (WorkspaceLayout)element;
        if (owner.applying) return;
        owner.RememberCurrent((WorkspaceLayoutPreset)args.OldValue);
        owner.ApplyState(owner.states.GetValueOrDefault((WorkspaceLayoutPreset)args.NewValue) ?? Defaults((WorkspaceLayoutPreset)args.NewValue));
    }

    private static void OnRatioChanged(DependencyObject element, DependencyPropertyChangedEventArgs _)
    {
        var owner = (WorkspaceLayout)element;
        if (!owner.applying) owner.ApplyProportions();
    }

    private static WorkspaceLayoutState Defaults(WorkspaceLayoutPreset preset) => preset switch
    {
        WorkspaceLayoutPreset.SideBySide => new(preset, .56, .34),
        WorkspaceLayoutPreset.FocusDiff => new(preset, .62, .36),
        _ => new(preset, .28, .46)
    };

    private void ApplyState(WorkspaceLayoutState state)
    {
        applying = true;
        SetCurrentValue(ColumnRatioProperty, state.ColumnRatio);
        SetCurrentValue(RowRatioProperty, state.RowRatio);
        applying = false;
        ApplyArrangement();
    }

    private void RememberCurrent(WorkspaceLayoutPreset preset)
    {
        double column = ColumnDefinitions[0].Width.Value / (ColumnDefinitions[0].Width.Value + ColumnDefinitions[2].Width.Value);
        double row = RowDefinitions[0].Height.Value / (RowDefinitions[0].Height.Value + RowDefinitions[2].Height.Value);
        applying = true;
        SetCurrentValue(ColumnRatioProperty, IsRatio(column) ? column : ColumnRatio);
        SetCurrentValue(RowRatioProperty, IsRatio(row) ? row : RowRatio);
        applying = false;
        states[preset] = new(preset, ColumnRatio, RowRatio);
    }

    public IReadOnlyList<WorkspaceLayoutState> CaptureLayouts()
    {
        RememberCurrent(Layout);
        return Presets.Select(preset => states.GetValueOrDefault(preset.Value) ?? Defaults(preset.Value)).ToArray();
    }

    public void RestoreLayouts(IEnumerable<WorkspaceLayoutState> saved, WorkspaceLayoutPreset selected)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (!Enum.IsDefined(selected)) throw new ArgumentOutOfRangeException(nameof(selected));
        var validated = saved.ToArray();
        if (validated.Any(state => state is null || !Enum.IsDefined(state.Layout) ||
            !IsRatio(state.ColumnRatio) || !IsRatio(state.RowRatio)))
            throw new ArgumentException("Invalid workspace proportions.", nameof(saved));
        states.Clear();
        foreach (var state in validated) states[state.Layout] = state;
        applying = true;
        SetCurrentValue(LayoutProperty, selected);
        applying = false;
        ApplyState(states.GetValueOrDefault(selected) ?? Defaults(selected));
    }

    public void ResetLayouts() => RestoreLayouts([], WorkspaceLayoutPreset.Balanced);

    private void ApplyProportions()
    {
        ColumnDefinitions[0].Width = new GridLength(ColumnRatio, GridUnitType.Star);
        ColumnDefinitions[2].Width = new GridLength(1 - ColumnRatio, GridUnitType.Star);
        RowDefinitions[0].Height = new GridLength(RowRatio, GridUnitType.Star);
        RowDefinitions[2].Height = new GridLength(1 - RowRatio, GridUnitType.Star);
    }

    private void ApplyArrangement()
    {
        ApplyProportions();
        ColumnDefinitions[0].MinWidth = Layout == WorkspaceLayoutPreset.Balanced ? 180 : 260;
        ColumnDefinitions[2].MinWidth = Layout == WorkspaceLayoutPreset.FocusDiff ? 200 : 260;
        RowDefinitions[0].MinHeight = Layout == WorkspaceLayoutPreset.SideBySide ? 110 : 120;
        RowDefinitions[2].MinHeight = 145;
        ArrangeRegions();
    }

    private void ArrangeRegions()
    {
        foreach (UIElement child in Children)
        {
            if (child == mainSplitter || child == secondarySplitter) continue;
            switch (Layout, GetRegion(child))
            {
                case (WorkspaceLayoutPreset.Balanced, WorkspaceRegion.Primary): Place(child, 0, 0, 1, 3); break;
                case (WorkspaceLayoutPreset.Balanced, WorkspaceRegion.History): Place(child, 2, 0); break;
                case (WorkspaceLayoutPreset.Balanced, WorkspaceRegion.Inspector): Place(child, 2, 2); break;
                case (WorkspaceLayoutPreset.SideBySide, WorkspaceRegion.Primary): Place(child, 0, 0, 3); break;
                case (WorkspaceLayoutPreset.SideBySide, WorkspaceRegion.History): Place(child, 0, 2); break;
                case (WorkspaceLayoutPreset.SideBySide, WorkspaceRegion.Inspector): Place(child, 2, 2); break;
                case (WorkspaceLayoutPreset.FocusDiff, WorkspaceRegion.Primary): Place(child, 0, 0); break;
                case (WorkspaceLayoutPreset.FocusDiff, WorkspaceRegion.History): Place(child, 0, 2); break;
                case (WorkspaceLayoutPreset.FocusDiff, WorkspaceRegion.Inspector): Place(child, 2, 0, 1, 3); break;
            }
        }
        bool side = Layout == WorkspaceLayoutPreset.SideBySide;
        mainSplitter.ResizeDirection = side ? GridResizeDirection.Columns : GridResizeDirection.Rows;
        secondarySplitter.ResizeDirection = side ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        if (side)
        {
            Place(mainSplitter, 0, 1, 3);
            Place(secondarySplitter, 1, 2);
        }
        else
        {
            Place(mainSplitter, 1, 0, 1, 3);
            Place(secondarySplitter, Layout == WorkspaceLayoutPreset.Balanced ? 2 : 0, 1);
        }
        foreach (var splitter in new[] { mainSplitter, secondarySplitter })
        {
            string name = splitter.ResizeDirection == GridResizeDirection.Rows ? "Resize pane heights" : "Resize pane widths";
            AutomationProperties.SetName(splitter, name);
            splitter.ToolTip = name + " · Drag or use arrow keys";
        }
    }

    private static void Place(UIElement child, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        SetRow(child, row);
        SetColumn(child, column);
        SetRowSpan(child, rowSpan);
        SetColumnSpan(child, columnSpan);
    }
}
