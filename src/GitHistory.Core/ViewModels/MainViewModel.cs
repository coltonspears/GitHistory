using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHistory.Core.Models;
using GitHistory.Core.Services;

namespace GitHistory.Core.ViewModels;

public sealed partial class MainViewModel(
    WorkspaceViewModel workspace, BrowserViewModel browser, DetailsViewModel details,
    IUserSettingsStore settings, IAppearanceService appearance) : ObservableObject
{
    public WorkspaceViewModel Workspace { get; } = workspace;
    public BrowserViewModel Browser { get; } = browser;
    public DetailsViewModel Details { get; } = details;
    public bool IsInitialized { get; private set; }
    [ObservableProperty] public partial string Theme { get; set; } = "Dark";
    [ObservableProperty] public partial bool IsPaletteOpen { get; set; }
    [ObservableProperty] public partial string PaletteSearch { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<PaletteItem> PaletteItems { get; set; } = [];
    [ObservableProperty] public partial PaletteItem? SelectedPaletteItem { get; set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var saved = await settings.LoadAsync(cancellationToken);
        Theme = saved.Theme == "Light" ? "Light" : "Dark";
        appearance.SetTheme(Theme);
        if (Enum.TryParse<FileViewMode>(saved.ViewMode, out var mode)) Browser.Mode = mode;
        await Workspace.InitializeAsync(saved, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        IsInitialized = true;
    }
    public Task SaveAsync(CancellationToken cancellationToken = default) => !IsInitialized ? Task.CompletedTask : settings.SaveAsync(new UserSettings(
        Workspace.SelectedRepository?.Id, Workspace.SelectedBranch, Theme, Browser.Mode.ToString()), cancellationToken);
    [RelayCommand] private void ToggleTheme() { Theme = Theme == "Dark" ? "Light" : "Dark"; appearance.SetTheme(Theme); }
    [RelayCommand] private void ResetLayout() => appearance.ResetLayout();
    [RelayCommand] private void OpenPalette() { if (Workspace.IsConnectOpen) return; PaletteSearch = ""; UpdatePalette(); IsPaletteOpen = true; }
    [RelayCommand] private void ClosePalette() { IsPaletteOpen = false; Workspace.CloseConnectCommand.Execute(null); }
    partial void OnPaletteSearchChanged(string value) => UpdatePalette();
    private void UpdatePalette()
    {
        PaletteItem[] commands = [
            new("connect", "Connect repository", "Add an HTTPS or SSH Git remote"),
            new("refresh", "Refresh repository", "Fetch the latest branch history · F5"),
            new("recent", "Recent changes", "See files touched during a selected period"),
            new("all", "All files", "Explore the current tree by its last change"),
            new("theme", "Toggle dark / light theme", "Switch the workspace appearance · Ctrl+D"),
            new("layout", "Reset workspace layout", "Restore default panes and file columns")];
        PaletteItems = commands.Concat(Workspace.Repositories.Select(r => new PaletteItem($"repo:{r.Id}", r.Name, r.IsDemo ? "Open sample workspace" : r.RemoteUrl)))
            .Where(p => p.Title.Contains(PaletteSearch, StringComparison.OrdinalIgnoreCase) || p.Description.Contains(PaletteSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
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
}
