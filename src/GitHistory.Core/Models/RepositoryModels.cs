namespace GitHistory.Core.Models;

public sealed record RepositoryInfo(string Id, string Name, string RemoteUrl, string DefaultBranch, IReadOnlyList<string> Branches, bool IsDemo = false);
public sealed record CommitInfo(string Oid, string? ParentOid, string AuthorName, string AuthorEmail, DateTimeOffset AuthoredAt, DateTimeOffset CommittedAt, string Subject)
{
    public string ShortSha => Oid.Length > 8 ? Oid[..8] : Oid;
    public DateTime LocalDate => CommittedAt.LocalDateTime;
}
public enum ChangeKind { Added, Modified, Deleted, Renamed, Copied, TypeChanged }
public sealed record FileChange(CommitInfo Commit, string Path, string? OldPath, ChangeKind Kind, string? OldObjectId, string? NewObjectId, string OldMode, string NewMode)
{
    public string ShortSha => Commit.ShortSha;
    public string Author => Commit.AuthorName;
    public string Subject => Commit.Subject;
    public DateTime LocalDate => Commit.LocalDate;
    public string ChangeType => Kind.ToString();
}
public sealed record TreeFile(string Path, string ObjectId, string Mode);
/// <summary>Commits and changes are ordered newest to oldest by first-parent ancestry.</summary>
public sealed record BranchSnapshot(string RepositoryId, string Branch, string TipOid, DateTimeOffset RefreshedAt, IReadOnlyList<CommitInfo> Commits, IReadOnlyList<FileChange> Changes, IReadOnlyList<TreeFile> Files);
public sealed record OperationProgress(string Stage, string Detail, int Completed = 0, int? Total = null);
public enum FileViewMode { RecentChanges, AllFiles }
public sealed record HistoryFilter(FileViewMode Mode, DateTimeOffset? From, DateTimeOffset? Until, string Search = "", string Author = "", string Folder = "");
public sealed record FileRow(string Path, FileChange? LatestChange, int TouchCount, string Mode, DateTimeOffset Now)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
    public string Folder => Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : "/";
    public string ChangeType => LatestChange?.Kind.ToString() ?? "Unchanged";
    public string Author => LatestChange?.Commit.AuthorName ?? "";
    public string Subject => LatestChange?.Commit.Subject ?? "";
    public string ShortSha => LatestChange?.ShortSha ?? "";
    public DateTime? LastChanged => LatestChange?.Commit.LocalDate;
    public double AgeDays => LatestChange is null ? double.PositiveInfinity : Math.Max(0, (Now - LatestChange.Commit.CommittedAt).TotalDays);
    public string Age => LatestChange is null ? "Unknown" : AgeDays < 1 ? "Today" : AgeDays < 2 ? "Yesterday" : $"{(int)AgeDays:N0} days ago";
    public string PreviousPath => LatestChange?.OldPath ?? "";
    public bool IsSubmodule => Mode == "160000";
}
public sealed record ActivityPoint(DateTime Date, int Commits, int Files);
public enum DiffLineKind { Context, Added, Deleted, Header, Notice }
public sealed record DiffLine(int? OldLine, int? NewLine, string Text, DiffLineKind Kind)
{
    public string Marker => Kind switch { DiffLineKind.Added => "+", DiffLineKind.Deleted => "−", _ => " " };
}
public sealed record DiffResult(IReadOnlyList<DiffLine> Lines, string Summary, bool IsTruncated = false, bool IsBinary = false);
public sealed record UserSettings(string? SelectedRepositoryId = null, string? SelectedBranch = null, string Theme = "Dark", string ViewMode = "RecentChanges");
