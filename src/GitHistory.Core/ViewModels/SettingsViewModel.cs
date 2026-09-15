using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHistory.Core.Models;
using GitHistory.Core.Services;

namespace GitHistory.Core.ViewModels;

public sealed partial class SettingsViewModel(IAppearanceService appearance, IDesktopIntegration desktop) : ObservableObject
{
    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial string Theme { get; set; } = "Dark";
    [ObservableProperty] public partial string DefaultDatePreset { get; set; } = "7 days";
    [ObservableProperty] public partial bool RefreshOnOpen { get; set; } = true;
    [ObservableProperty] public partial bool ShowActivityChart { get; set; } = true;
    [ObservableProperty] public partial bool AutoLoadPullRequests { get; set; }
    [ObservableProperty] public partial string CursorCommand { get; set; } = "cursor";
    [ObservableProperty] public partial string Error { get; set; } = "";
    public IReadOnlyList<string> Themes { get; } = Array.AsReadOnly(new[] { "Dark", "Light", "Classic", "Dusk" });
    public IReadOnlyList<string> DatePresets { get; } = ["24 hours", "7 days", "30 days"];
    partial void OnThemeChanged(string value)
    {
        string theme = NormalizeTheme(value);
        if (value != theme) { Theme = theme; return; }
        appearance.SetTheme(theme);
    }
    private string NormalizeTheme(string? value) => Themes.FirstOrDefault(theme => string.Equals(theme, value, StringComparison.OrdinalIgnoreCase)) ?? "Dark";
    public void CycleTheme() => Theme = Themes[(Themes.ToList().IndexOf(Theme) + 1) % Themes.Count];
    public void Load(UserSettings settings)
    {
        Theme = NormalizeTheme(settings.Theme);
        appearance.SetTheme(Theme);
        DefaultDatePreset = DatePresets.Contains(settings.DefaultDatePreset) ? settings.DefaultDatePreset : "7 days";
        RefreshOnOpen = settings.RefreshOnOpen;
        ShowActivityChart = settings.ShowActivityChart;
        AutoLoadPullRequests = settings.AutoLoadPullRequests;
        CursorCommand = string.IsNullOrWhiteSpace(settings.CursorCommand) ? "cursor" : settings.CursorCommand;
    }
    [RelayCommand] private void Close() => IsOpen = false;
    [RelayCommand] private void OpenDataFolder()
    {
        try { desktop.OpenDataFolder(); Error = ""; }
        catch (Exception ex) { Error = ex.Message; }
    }
}
