using System.Windows;

namespace GitHistory.UI.Theming;

/// <summary>A named palette that can be reused by any WPF host of this library.</summary>
public sealed record ThemeDescriptor(string Name, Uri Source, bool IsLight);

/// <summary>Swaps palette resources without replacing control templates or application-specific overrides.</summary>
public static class ThemeCatalog
{
    public static IReadOnlyList<ThemeDescriptor> Themes { get; } = Array.AsReadOnly(new[]
    {
        Create("Dark"),
        Create("Light", isLight: true),
        Create("Classic"),
        Create("Dusk")
    });

    public static ThemeDescriptor Get(string? name) => Themes.FirstOrDefault(theme =>
        string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];

    /// <summary>Applies a palette on the application's dispatcher and returns its canonical descriptor.</summary>
    public static ThemeDescriptor Apply(Application application, string? name)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.Dispatcher.VerifyAccess();
        var theme = Get(name);
        // Load before changing the active resources so a resource failure leaves the current palette intact.
        var replacement = new ResourceDictionary { Source = theme.Source };
        var dictionaries = application.Resources.MergedDictionaries;
        var paletteIndices = dictionaries.Select((dictionary, index) => (dictionary, index))
            .Where(item => IsPalette(item.dictionary.Source)).Select(item => item.index).ToArray();
        if (paletteIndices.Length == 0) dictionaries.Insert(0, replacement);
        else
        {
            dictionaries[paletteIndices[0]] = replacement;
            // Multiple library palettes would allow stale colors to override the selected theme.
            foreach (int index in paletteIndices.Skip(1).Reverse()) dictionaries.RemoveAt(index);
        }
        return theme;
    }

    private static bool IsPalette(Uri? source) => source is not null && Themes.Any(theme =>
        source.OriginalString.EndsWith(theme.Source.OriginalString, StringComparison.OrdinalIgnoreCase));

    private static ThemeDescriptor Create(string name, bool isLight = false) =>
        new(name, new Uri($"/GitHistory.UI;component/Themes/{name}.xaml", UriKind.Relative), isLight);
}
