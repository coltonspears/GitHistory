using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GitHistory.UI.Controls;

/// <summary>
/// A compact, domain-independent daily bar chart. Click selects an item, drag selects
/// a date interval, arrows select dates, and Shift+arrows extend the interval.
/// RangeEnd is exclusive. Items expose a date and numeric value through member paths.
/// </summary>
public class ActivityChart : Control
{
    private readonly List<Sample> _samples = [];
    private DateTime _first;
    private DateTime _end;
    private Point? _dragOrigin;
    private Point _dragCurrent;
    private bool _isDragging;
    private int _keyboardIndex = -1;
    private DateTime? _keyboardAnchor;

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(ActivityChart), new PropertyMetadata(null, OnItemsSourceChanged));
    public static readonly DependencyProperty DateMemberPathProperty = DependencyProperty.Register(
        nameof(DateMemberPath), typeof(string), typeof(ActivityChart), new PropertyMetadata("Date", OnMembersChanged));
    public static readonly DependencyProperty ValueMemberPathProperty = DependencyProperty.Register(
        nameof(ValueMemberPath), typeof(string), typeof(ActivityChart), new PropertyMetadata("Value", OnMembersChanged));
    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem), typeof(object), typeof(ActivityChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender, OnSelectedItemChanged));
    public static readonly DependencyProperty RangeStartProperty = DependencyProperty.Register(
        nameof(RangeStart), typeof(DateTime), typeof(ActivityChart), new FrameworkPropertyMetadata(DateTime.MinValue, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RangeEndProperty = DependencyProperty.Register(
        nameof(RangeEnd), typeof(DateTime), typeof(ActivityChart), new FrameworkPropertyMetadata(DateTime.MaxValue, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(
        nameof(SelectionBrush), typeof(Brush), typeof(ActivityChart), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(ActivityChart), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ActivityChart), new FrameworkPropertyMetadata(Brushes.LightSlateGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public string DateMemberPath { get => (string)GetValue(DateMemberPathProperty); set => SetValue(DateMemberPathProperty, value); }
    public string ValueMemberPath { get => (string)GetValue(ValueMemberPathProperty); set => SetValue(ValueMemberPathProperty, value); }
    public object? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public DateTime RangeStart { get => (DateTime)GetValue(RangeStartProperty); set => SetValue(RangeStartProperty, value); }
    public DateTime RangeEnd { get => (DateTime)GetValue(RangeEndProperty); set => SetValue(RangeEndProperty, value); }
    public Brush SelectionBrush { get => (Brush)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    public ActivityChart()
    {
        Focusable = true;
        MinHeight = 36;
        Cursor = Cursors.Cross;
        SetResourceReference(ForegroundProperty, "AccentBrush");
        SetResourceReference(BorderBrushProperty, "LineBrush");
        SetResourceReference(SelectionBrushProperty, "AccentSoftBrush");
        SetResourceReference(LabelBrushProperty, "MutedBrush");
        SetResourceReference(TrackBrushProperty, "ChartTrackBrush");
        ToolTip = "Click a day to filter. Drag to select a date range. Use arrow keys; hold Shift to extend the range.";
    }

    private static void OnItemsSourceChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var chart = (ActivityChart)target;
        if (args.OldValue is INotifyCollectionChanged oldCollection) CollectionChangedEventManager.RemoveHandler(oldCollection, chart.OnCollectionChanged);
        if (args.NewValue is INotifyCollectionChanged newCollection) CollectionChangedEventManager.AddHandler(newCollection, chart.OnCollectionChanged);
        chart.RefreshSamples();
    }

    private static void OnMembersChanged(DependencyObject target, DependencyPropertyChangedEventArgs args) => ((ActivityChart)target).RefreshSamples();
    private static void OnSelectedItemChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var chart = (ActivityChart)target;
        chart._keyboardIndex = chart._samples.FindIndex(sample => Equals(sample.Item, args.NewValue));
        chart._keyboardAnchor = null;
    }
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshSamples();

    private void RefreshSamples()
    {
        var keyboardDate = _keyboardIndex >= 0 && _keyboardIndex < _samples.Count ? _samples[_keyboardIndex].Date : (DateTime?)null;
        _samples.Clear();
        if (ItemsSource is not null)
        {
            foreach (var item in ItemsSource)
            {
                if (item is null) continue;
                var dateValue = ReadMember(item, DateMemberPath);
                var date = dateValue switch { DateTime timestamp => timestamp.Date, DateTimeOffset timestamp => timestamp.LocalDateTime.Date, DateOnly day => day.ToDateTime(TimeOnly.MinValue), _ => (DateTime?)null };
                if (date is null || ReadMember(item, ValueMemberPath) is not IConvertible numeric) continue;
                double value;
                try { value = numeric.ToDouble(CultureInfo.InvariantCulture); }
                catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException) { continue; }
                if (double.IsFinite(value)) _samples.Add(new Sample(item, date.Value, Math.Max(0, value)));
            }
        }
        _samples.Sort((left, right) => left.Date.CompareTo(right.Date));
        _keyboardIndex = keyboardDate is { } previousDate ? _samples.FindIndex(sample => sample.Date == previousDate) : _samples.FindIndex(sample => Equals(sample.Item, SelectedItem));
        if (_keyboardIndex < 0) _keyboardAnchor = null;
        if (_samples.Count > 0)
        {
            _first = _samples[0].Date;
            _end = NextDay(_samples[^1].Date);
        }
        InvalidateVisual();
    }

    private static object? ReadMember(object item, string path)
    {
        object? current = item;
        foreach (var part in path.Split('.'))
        {
            if (current is null) return null;
            current = TypeDescriptor.GetProperties(current)[part]?.GetValue(current);
        }
        return current;
    }

    protected override Size MeasureOverride(Size constraint) => new(double.IsInfinity(constraint.Width) ? 320 : constraint.Width, double.IsInfinity(constraint.Height) ? 62 : Math.Max(36, constraint.Height));

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 4 || height < 4) return;
        drawing.DrawRectangle(Background ?? Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (_samples.Count == 0)
        {
            DrawText(drawing, "No activity in this branch", new Point(0, Math.Max(0, (height - 14) / 2)));
            return;
        }
        var chartHeight = Math.Max(12, height - 20);
        var baseline = chartHeight - 3;
        var max = Math.Max(1, _samples.Max(sample => sample.Value));
        var days = Math.Max(1, (_end - _first).TotalDays);
        var unitWidth = width / days;
        var barWidth = Math.Max(1, Math.Min(22, unitWidth * 0.68));
        var (rangeStart, rangeEnd) = DisplayRange();
        var rangeX = PositionAt(rangeStart);
        var rangeRight = PositionAt(rangeEnd);
        if (rangeRight > rangeX)
            drawing.DrawRoundedRectangle(SelectionBrush, null, new Rect(rangeX, 0, rangeRight - rangeX, chartHeight), 3, 3);
        drawing.DrawLine(new Pen(BorderBrush, 1), new Point(0, baseline + 0.5), new Point(width, baseline + 0.5));
        foreach (var sample in _samples)
        {
            var x = PositionAt(sample.Date) + unitWidth / 2;
            var barHeight = Math.Max(2, (baseline - 3) * sample.Value / max);
            var active = sample.Date >= rangeStart && sample.Date < rangeEnd;
            drawing.DrawRoundedRectangle(active ? Foreground : TrackBrush, null, new Rect(Math.Max(0, x - barWidth / 2), baseline - barHeight, Math.Min(width, barWidth), barHeight), 2, 2);
            if (Equals(sample.Item, SelectedItem))
                drawing.DrawLine(new Pen(Foreground, 2), new Point(x, baseline + 1), new Point(x, chartHeight + 1));
        }
        // A separate rail makes the currently filtered interval visible even over empty days.
        drawing.DrawRoundedRectangle(TrackBrush, null, new Rect(0, chartHeight + 3, width, 3), 1.5, 1.5);
        if (rangeRight > rangeX)
            drawing.DrawRoundedRectangle(Foreground, null, new Rect(rangeX, chartHeight + 3, rangeRight - rangeX, 3), 1.5, 1.5);
        var labelY = chartHeight + 8;
        DrawText(drawing, _first.ToString("MMM d", CultureInfo.CurrentCulture), new Point(0, labelY));
        var last = Text(_samples[^1].Date.ToString("MMM d", CultureInfo.CurrentCulture));
        if (width > 100) drawing.DrawText(last, new Point(Math.Max(0, width - last.Width), labelY));
        if (IsKeyboardFocused) drawing.DrawRoundedRectangle(null, new Pen(Foreground, 1), new Rect(0.5, 0.5, width - 1, height - 1), 3, 3);
    }

    private FormattedText Text(string content) => new(content, CultureInfo.CurrentUICulture, FlowDirection, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), 9, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private void DrawText(DrawingContext drawing, string content, Point point) => drawing.DrawText(Text(content), point);
    private double PositionAt(DateTime date) => Math.Clamp((date - _first).TotalDays / Math.Max(1, (_end - _first).TotalDays) * ActualWidth, 0, ActualWidth);
    private DateTime DateAt(double x)
    {
        var dayCount = Math.Max(1, (_end - _first).Days);
        var day = Math.Clamp((int)Math.Floor(Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * dayCount), 0, dayCount - 1);
        return _first.AddDays(day);
    }
    private static DateTime NextDay(DateTime date) => date.Date == DateTime.MaxValue.Date ? DateTime.MaxValue : date.Date.AddDays(1);

    private (DateTime Start, DateTime End) DisplayRange()
    {
        if (_dragOrigin is not { } origin || !_isDragging) return (RangeStart, RangeEnd);
        var first = DateAt(origin.X);
        var last = DateAt(_dragCurrent.X);
        return first <= last ? (first, NextDay(last)) : (last, NextDay(first));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        base.OnMouseLeftButtonDown(args);
        if (_samples.Count == 0) return;
        Focus();
        _dragOrigin = args.GetPosition(this);
        _dragCurrent = _dragOrigin.Value;
        _isDragging = false;
        CaptureMouse();
        args.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        if (_samples.Count == 0) return;
        var point = args.GetPosition(this);
        if (_dragOrigin is { } origin)
        {
            _dragCurrent = point;
            _isDragging |= Math.Abs(point.X - origin.X) > SystemParameters.MinimumHorizontalDragDistance;
            InvalidateVisual();
        }
        else
        {
            var date = DateAt(point.X);
            var sample = _samples.FirstOrDefault(value => value.Date == date);
            ToolTip = sample is null ? $"{date:MMM d, yyyy} · no activity\nDrag to select a date range." : $"{sample.Date:MMM d, yyyy} · {sample.Value:N0} {ValueMemberPath.ToLowerInvariant()}\nClick a day or drag a date range.";
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        base.OnMouseLeftButtonUp(args);
        if (_dragOrigin is null) return;
        _dragCurrent = args.GetPosition(this);
        var dragging = _isDragging;
        var range = DisplayRange();
        var date = DateAt(args.GetPosition(this).X);
        _dragOrigin = null;
        _isDragging = false;
        ReleaseMouseCapture();
        if (dragging) ApplyRange(range.Start, range.End);
        else
        {
            var sample = _samples.MinBy(value => Math.Abs((value.Date - date).TotalDays));
            if (sample is not null) SelectSample(sample);
        }
        InvalidateVisual();
        args.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs args)
    {
        base.OnLostMouseCapture(args);
        _dragOrigin = null;
        _isDragging = false;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (_samples.Count == 0 || args.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        var current = _keyboardIndex >= 0 && _keyboardIndex < _samples.Count ? _keyboardIndex : _samples.FindIndex(sample => Equals(sample.Item, SelectedItem));
        var next = args.Key switch { Key.Home => 0, Key.End => _samples.Count - 1, Key.Left => Math.Max(0, current - 1), _ => Math.Min(_samples.Count - 1, current + 1) };
        var sample = _samples[next];
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            _keyboardAnchor ??= current >= 0 ? _samples[current].Date : sample.Date;
            var start = _keyboardAnchor.Value;
            _keyboardIndex = next;
            ApplyRange(sample.Date < start ? sample.Date : start, sample.Date < start ? NextDay(start) : NextDay(sample.Date));
            // Range binding is authoritative while extending; updating SelectedItem would ask
            // consumers to collapse their filter back to a single day.
        }
        else SelectSample(sample);
        args.Handled = true;
    }

    private void SelectSample(Sample sample)
    {
        _keyboardIndex = _samples.IndexOf(sample);
        _keyboardAnchor = null;
        ApplyRange(sample.Date, NextDay(sample.Date));
        SetCurrentValue(SelectedItemProperty, sample.Item);
        ToolTip = $"{sample.Date:MMM d, yyyy} · {sample.Value:N0} {ValueMemberPath.ToLowerInvariant()}";
    }

    private void ApplyRange(DateTime start, DateTime end)
    {
        // Set the expanding edge first to avoid an invalid interval in two-way consumers.
        if (start >= RangeEnd) { SetCurrentValue(RangeEndProperty, end); SetCurrentValue(RangeStartProperty, start); }
        else { SetCurrentValue(RangeStartProperty, start); SetCurrentValue(RangeEndProperty, end); }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs args) { base.OnGotKeyboardFocus(args); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs args) { base.OnLostKeyboardFocus(args); InvalidateVisual(); }
    protected override AutomationPeer OnCreateAutomationPeer() => new ActivityChartAutomationPeer(this);

    private sealed record Sample(object Item, DateTime Date, double Value);
    private sealed class ActivityChartAutomationPeer(ActivityChart owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(ActivityChart);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override string GetHelpTextCore() => "Daily activity. Left and Right select a day; Home and End move to the first and last days. Shift extends the date range. Drag with the mouse to select a date interval.";
    }
}
