using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using GitHistory.Core.Models;
using GitHistory.Core.Services;

namespace GitHistory.Core.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IUserSettingsStore settings;
    private readonly IAppearanceService appearance;
    public WorkspaceViewModel Workspace { get; }
    public BrowserViewModel Browser { get; }
    public DetailsViewModel Details { get; }
    public SettingsViewModel Settings { get; }
    public RepositoryImportViewModel Import { get; }
    public RepositoryActionsViewModel Actions { get; }
    public bool IsInitialized { get; private set; }
    [ObservableProperty] public partial bool IsInitializing { get; set; }
    [ObservableProperty] public partial string Theme { get; set; } = "Dark";
    [ObservableProperty] public partial bool IsPaletteOpen { get; set; }
    [ObservableProperty] public partial string PaletteSearch { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<PaletteItem> PaletteItems { get; set; } = [];
    [ObservableProperty] public partial PaletteItem? SelectedPaletteItem { get; set; }

    public MainViewModel(WorkspaceViewModel workspace, BrowserViewModel browser, DetailsViewModel details,
        IUserSettingsStore settings, IAppearanceService appearance, SettingsViewModel preferences,
        RepositoryImportViewModel import, RepositoryActionsViewModel actions)
    {
        Workspace = workspace; Browser = browser; Details = details;
        this.settings = settings; this.appearance = appearance;
        Settings = preferences; Import = import; Actions = actions;
        Settings.PropertyChanged += OnSettingChanged;
    }

    partial void OnThemeChanged(string value) => Settings.Theme = value;
    private void OnSettingChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SettingsViewModel.Theme)) Theme = Settings.Theme;
        else if (args.PropertyName == nameof(SettingsViewModel.RefreshOnOpen)) Workspace.RefreshOnOpen = Settings.RefreshOnOpen;
        else if (args.PropertyName == nameof(SettingsViewModel.DefaultDatePreset)) Browser.DatePreset = Settings.DefaultDatePreset;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsInitializing = true;
        try
        {
            var saved = await settings.LoadAsync(cancellationToken);
            Settings.Load(saved); Actions.Load(saved); Theme = Settings.Theme;
            Browser.DatePreset = Settings.DefaultDatePreset;
            if (Enum.TryParse<FileViewMode>(saved.ViewMode, out var mode)) Browser.Mode = mode;
            await Workspace.InitializeAsync(saved, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            IsInitialized = true;
        }
        finally { IsInitializing = false; }
    }
    public Task SaveAsync(CancellationToken cancellationToken = default) => !IsInitialized ? Task.CompletedTask : settings.SaveAsync(new UserSettings(
        Workspace.SelectedRepository?.Id, Workspace.SelectedBranch, Theme, Browser.Mode.ToString(),
        Workspace.PinnedRepositoryIds.Count == 0 ? null : Workspace.PinnedRepositoryIds.ToArray(), Workspace.ShowOnlyPinned,
        Settings.RefreshOnOpen, Settings.DefaultDatePreset, Settings.ShowActivityChart, Settings.AutoLoadPullRequests,
        Settings.CursorCommand, Actions.LocalFolders.Count == 0 ? null : Actions.LocalFolders), cancellationToken);
    [RelayCommand] private void ToggleTheme() => Settings.CycleTheme();
    [RelayCommand] private void ResetLayout() => appearance.ResetLayout();
    [RelayCommand] private void OpenSettings() { ClosePalette(); Settings.IsOpen = true; }
    [RelayCommand] private void OpenImport() { ClosePalette(); Import.IsOpen = true; }
    [RelayCommand] private void OpenPalette() { if (Workspace.IsConnectOpen || Settings.IsOpen || Import.IsOpen) return; PaletteSearch = ""; UpdatePalette(); IsPaletteOpen = true; }
    [RelayCommand] private void ClosePalette() { IsPaletteOpen = false; Workspace.CloseConnectCommand.Execute(null); Settings.IsOpen = false; Import.CloseCommand.Execute(null); }
    partial void OnPaletteSearchChanged(string value) => UpdatePalette();
    private void UpdatePalette()
    {
        string search = PaletteSearch ?? "";
        PaletteItem[] commands = [
            new("connect", "Connect repository", "Add an HTTPS or SSH Git remote"),
            new("import", "Import private repositories", "Discover repositories from GitHub or Azure DevOps"),
            new("settings", "Settings", "Appearance, refresh, pull requests, and editor options"),
            new("browser", "Open source control in browser", "Open the selected branch on GitHub or Azure DevOps"),
            new("explorer", "Open local checkout in Explorer", "Link or open a local working folder"),
            new("cursor", "Open local checkout in Cursor", "Continue working in your editor"),
            new("pin", "Pin / unpin this workspace", "Keep favorite repositories at the top"),
            new("clear", "Clear file and folder filters", "Show every matching file in the date range"),
            new("refresh", "Refresh repository", "Fetch the latest branch history · F5"),
            new("recent", "Recent changes", "See files touched during a selected period"),
            new("all", "All files", "Explore the current tree by its last change"),
            new("theme", "Cycle theme", "Choose Dark, Light, Classic, or Dusk · Ctrl+D"),
            new("layout", "Reset workspace layout", "Restore default panes and file columns")];
        PaletteItems = commands.Concat(Workspace.Repositories.Select(r => new PaletteItem($"repo:{r.Id}", r.Name, r.IsDemo ? "Open sample workspace" : r.RemoteUrl)))
            .Where(p => p.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || p.Description.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        SelectedPaletteItem = PaletteItems.FirstOrDefault();
    }
    [RelayCommand]
    private async Task ExecutePaletteAsync(PaletteItem? item)
    {
        item ??= SelectedPaletteItem;
        if (item is null) return;
        IsPaletteOpen = false;
        switch (item.Key)
        {
            case "connect": Workspace.OpenConnectCommand.Execute(null); break;
            case "import": OpenImport(); break;
            case "settings": OpenSettings(); break;
            case "browser": if (Actions.OpenRepositoryBrowserCommand.CanExecute(null)) Actions.OpenRepositoryBrowserCommand.Execute(null); break;
            case "explorer": if (Actions.OpenExplorerCommand.CanExecute(null)) await Actions.OpenExplorerCommand.ExecuteAsync(null); break;
            case "cursor": if (Actions.OpenCursorCommand.CanExecute(null)) await Actions.OpenCursorCommand.ExecuteAsync(null); break;
            case "pin": Workspace.TogglePinCommand.Execute(null); break;
            case "clear": Browser.ClearFiltersCommand.Execute(null); break;
            case "refresh": if (Workspace.RefreshCommand.CanExecute(null)) await Workspace.RefreshCommand.ExecuteAsync(null); break;
            case "recent": Browser.Mode = FileViewMode.RecentChanges; break;
            case "all": Browser.Mode = FileViewMode.AllFiles; break;
            case "theme": ToggleTheme(); break;
            case "layout": ResetLayout(); break;
            default:
                if (item.Key.StartsWith("repo:", StringComparison.Ordinal)) Workspace.SelectedRepository = Workspace.Repositories.FirstOrDefault(r => r.Id == item.Key[5..]);
                break;
        }
    }
    public void Dispose() => Settings.PropertyChanged -= OnSettingChanged;
}
