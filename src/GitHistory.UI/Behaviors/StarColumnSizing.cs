using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GitHistory.UI.Behaviors;

/// <summary>
/// Recalculates star columns after combined viewport and source changes have settled.
/// Opt in on grids affected by WPF's deferred star-width redistribution race.
/// </summary>
public static class StarColumnSizing
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(StarColumnSizing), new PropertyMetadata(false, OnEnabledChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(StarColumnSizing), new PropertyMetadata(null));

    private static readonly DependencyPropertyDescriptor ItemsSourceDescriptor =
        DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid));

    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not DataGrid grid) return;
        if (grid.GetValue(StateProperty) is State previous) previous.Dispose();
        grid.ClearValue(StateProperty);
        if (args.NewValue is true)
        {
            var state = new State(grid);
            grid.SetValue(StateProperty, state);
            state.Attach();
        }
    }

    private sealed class State(DataGrid grid) : IDisposable
    {
        private DispatcherOperation? pending;
        private bool active;

        public void Attach()
        {
            grid.Loaded += OnLoaded;
            grid.Unloaded += OnUnloaded;
            if (grid.IsLoaded) Start();
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => Start();
        private void OnUnloaded(object sender, RoutedEventArgs args) => Stop();

        private void Start()
        {
            if (active) return;
            active = true;
            grid.SizeChanged += OnSizeChanged;
            ItemsSourceDescriptor.AddValueChanged(grid, OnSourceChanged);
            QueueRefresh();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs args) => QueueRefresh();
        private void OnSourceChanged(object? sender, EventArgs args) => QueueRefresh();

        private void QueueRefresh()
        {
            if (!active || pending is { Status: DispatcherOperationStatus.Pending }) return;
            // Both WPF's Render-priority width computation and Loaded-priority viewport
            // redistribution must finish before requesting a fresh star allocation.
            pending = grid.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)Refresh);
        }

        private void Refresh()
        {
            pending = null;
            if (!active || !grid.IsLoaded || !GetIsEnabled(grid) || grid.ActualWidth <= 0) return;
            foreach (var column in grid.Columns)
            {
                var width = column.Width;
                if (column.Visibility == Visibility.Visible && width.IsStar)
                    column.SetCurrentValue(DataGridColumn.WidthProperty, new DataGridLength(width.Value, DataGridLengthUnitType.Star));
            }
        }

        private void Stop()
        {
            pending?.Abort();
            pending = null;
            if (!active) return;
            active = false;
            grid.SizeChanged -= OnSizeChanged;
            ItemsSourceDescriptor.RemoveValueChanged(grid, OnSourceChanged);
        }

        public void Dispose()
        {
            Stop();
            grid.Loaded -= OnLoaded;
            grid.Unloaded -= OnUnloaded;
        }
    }
}
