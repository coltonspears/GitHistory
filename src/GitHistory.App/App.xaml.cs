using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using DevExpress.Xpf.Core;
using GitHistory.App.Services;
using GitHistory.Core.Services;
using GitHistory.Core.ViewModels;
using GitHistory.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GitHistory.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private MainViewModel? _viewModel;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _stopping;
    private TextWriterTraceListener? _bindings;
    private string _logDirectory = "";

    static App()
    {
        CompatibilitySettings.UseLightweightThemes = true;
        ApplicationThemeHelper.ApplicationThemeName = LightweightTheme.Win11Dark.Name;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var captureIndex = Array.IndexOf(e.Args, "--capture-screenshots");
        var captureDirectory = captureIndex >= 0 && captureIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[captureIndex + 1]) : null;
        var dataDirectory = captureDirectory is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHistory")
            : Path.Combine(Path.GetTempPath(), "GitHistory-ui-" + Guid.NewGuid().ToString("N"));
        _logDirectory = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(_logDirectory);
        _bindings = new TextWriterTraceListener(Path.Combine(_logDirectory, "bindings.log"));
        PresentationTraceSources.DataBindingSource.Listeners.Add(_bindings);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        DispatcherUnhandledException += async (_, args) =>
        {
            File.AppendAllText(Path.Combine(_logDirectory, "fatal.log"), args.Exception + Environment.NewLine);
            if (captureDirectory is null) System.Windows.MessageBox.Show(args.Exception.Message, "GitHistory could not continue", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            await StopAsync(1);
        };
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(_logDirectory, "app.log")));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<IMessenger, WeakReferenceMessenger>();
            builder.Services.AddSingleton<IUiDispatcher, UiDispatcher>();
            builder.Services.AddSingleton<IClipboardService, ClipboardService>();
            builder.Services.AddSingleton<IDialogService, Services.DialogService>();
            builder.Services.AddSingleton<AppearanceService>(services => new(services.GetRequiredService<ILogger<AppearanceService>>(), dataDirectory));
            builder.Services.AddSingleton<IAppearanceService>(services => services.GetRequiredService<AppearanceService>());
            builder.Services.AddSingleton<GitRepositoryService>(services => new(dataDirectory, services.GetRequiredService<ILogger<GitRepositoryService>>()));
            builder.Services.AddSingleton<IRepositoryService>(services => new DemoRepositoryService(services.GetRequiredService<GitRepositoryService>(), services.GetRequiredService<TimeProvider>()));
            builder.Services.AddSingleton<IUserSettingsStore>(new JsonUserSettingsStore(dataDirectory));
            builder.Services.AddSingleton<IHistoryQueryService, HistoryQueryService>();
            builder.Services.AddSingleton<BrowserViewModel>();
            builder.Services.AddSingleton<DetailsViewModel>();
            builder.Services.AddSingleton<WorkspaceViewModel>();
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddSingleton<MainWindow>();
            _host = builder.Build();
            await _host.StartAsync(_lifetime.Token);
            _viewModel = _host.Services.GetRequiredService<MainViewModel>();
            var window = _host.Services.GetRequiredService<MainWindow>();
            window.DataContext = _viewModel;
            MainWindow = window;
            _host.Services.GetRequiredService<AppearanceService>().AttachWindow(window);
            if (captureDirectory is not null) { window.Left = -20000; window.Top = -20000; window.ShowInTaskbar = false; }
            window.Closed += async (_, _) => await StopAsync();
            window.Show();
            await _viewModel.InitializeAsync(_lifetime.Token);
            if (captureDirectory is not null)
            {
                await UiVerification.RunAsync(window, _viewModel, captureDirectory, _bindings, _logDirectory);
                window.Close();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            File.AppendAllText(Path.Combine(_logDirectory, "fatal.log"), ex + Environment.NewLine);
            if (captureDirectory is not null) { Directory.CreateDirectory(captureDirectory); File.WriteAllText(Path.Combine(captureDirectory, "failure.txt"), ex.ToString()); }
            else System.Windows.MessageBox.Show($"GitHistory could not start.\n\n{ex.Message}\n\nDetails: {_logDirectory}", "GitHistory", MessageBoxButton.OK, MessageBoxImage.Error);
            await StopAsync(1);
        }
    }

    private async Task StopAsync(int exitCode = 0)
    {
        if (_stopping) return;
        _stopping = true;
        _lifetime.Cancel();
        try
        {
            if (_viewModel is not null) await Task.WhenAll(_viewModel.Workspace.ShutdownAsync(), _viewModel.Details.ShutdownAsync(), _viewModel.Browser.ShutdownAsync());
            if (_viewModel?.IsInitialized == true) await _viewModel.SaveAsync();
            if (_host is not null) { await _host.StopAsync(TimeSpan.FromSeconds(5)); _host.Dispose(); }
        }
        catch (Exception ex) { File.AppendAllText(Path.Combine(_logDirectory, "fatal.log"), ex + Environment.NewLine); exitCode = 1; }
        finally { _bindings?.Flush(); _bindings?.Dispose(); _lifetime.Dispose(); Shutdown(exitCode); }
    }
}
