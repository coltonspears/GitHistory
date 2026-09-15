using System.Globalization;
using System.Windows.Data;
using GitHistory.Core.Models;

namespace GitHistory.App.Converters;

/// <summary>Adapts a flat domain folder catalog into a native WPF hierarchy without changing model identity.</summary>
public sealed class FolderHierarchyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<DirectoryNode> nodes) return Array.Empty<DirectoryTreeItem>();
        var items = nodes.Select(node => new DirectoryTreeItem(node)).ToArray();
        var byId = items.ToDictionary(item => item.Node.Id, StringComparer.Ordinal);
        var roots = new List<DirectoryTreeItem>();
        foreach (var item in items)
        {
            if (item.Node.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent)) parent.Children.Add(item);
            else roots.Add(item);
        }
        return roots;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class DirectoryTreeItem(DirectoryNode node)
{
    public DirectoryNode Node { get; } = node;
    public List<DirectoryTreeItem> Children { get; } = [];
}
