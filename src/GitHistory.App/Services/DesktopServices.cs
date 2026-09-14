using System.Windows;
using System.Windows.Threading;
using GitHistory.Core.Services;

namespace GitHistory.App.Services;

public sealed class UiDispatcher : IUiDispatcher
{
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(action, DispatcherPriority.DataBind, cancellationToken).Task;
}
public sealed class ClipboardService : IClipboardService
{
    public void SetText(string text) => System.Windows.Clipboard.SetText(text);
}
public sealed class DialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(System.Windows.MessageBox.Show(
        System.Windows.Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
}
