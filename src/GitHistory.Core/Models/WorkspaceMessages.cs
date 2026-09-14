namespace GitHistory.Core.Models;

public sealed record CommitSelected(RepositoryInfo? Repository, FileChange? Change);

public sealed record SnapshotChanged(RepositoryInfo Repository, BranchSnapshot? Snapshot);
public sealed record FileSelected(RepositoryInfo? Repository, BranchSnapshot? Snapshot, FileRow? File);
public sealed record DirectoryNode(string Id, string? ParentId, string Name, string Path, int Count);
public sealed record PaletteItem(string Key, string Title, string Description);
