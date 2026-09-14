using System.Runtime.CompilerServices;
using GitHistory.Core.Models;

namespace GitHistory.Core.Services;

/// <summary>Queries immutable snapshots without changing Git's first-parent ordering.</summary>
public sealed class HistoryQueryService : IHistoryQueryService
{
    private readonly ConditionalWeakTable<BranchSnapshot, SnapshotIndex> indexes = new();

    public IReadOnlyList<FileRow> Query(BranchSnapshot snapshot, HistoryFilter filter, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(filter);

        var index = indexes.GetValue(snapshot, static value => new SnapshotIndex(value));
        var matcher = new FilterMatcher(filter);
        var rows = new List<FileRow>();

        if (filter.Mode == FileViewMode.AllFiles)
        {
            foreach (var file in snapshot.Files)
            {
                index.Current.TryGetValue(file.Path, out var history);
                if (matcher.Matches(file.Path, history?.Change))
                    rows.Add(new FileRow(file.Path, history?.Change, history?.Count ?? 0, file.Mode, now));
            }
        }
        else
        {
            var qualifying = new Dictionary<string, (FileChange Latest, int Count)>(StringComparer.Ordinal);
            foreach (var change in snapshot.Changes)
            {
                if (!matcher.Matches(change.Path, change))
                    continue;

                // The first qualifying touch is the newest by ancestry, even if a
                // contributor's clock made an older commit's timestamp look newer.
                if (qualifying.TryGetValue(change.Path, out var previous))
                    qualifying[change.Path] = (previous.Latest, previous.Count + 1);
                else
                    qualifying.Add(change.Path, (change, 1));
            }

            foreach (var (path, entry) in qualifying)
            {
                string mode = entry.Latest.Kind == ChangeKind.Deleted ? entry.Latest.OldMode : entry.Latest.NewMode;
                rows.Add(new FileRow(path, entry.Latest, entry.Count, mode, now));
            }
        }

        rows.Sort(static (left, right) =>
        {
            int date = Nullable.Compare(right.LatestChange?.Commit.CommittedAt, left.LatestChange?.Commit.CommittedAt);
            return date != 0 ? date : StringComparer.Ordinal.Compare(left.Path, right.Path);
        });
        return rows;
    }

    public IReadOnlyList<FileChange> GetFileHistory(BranchSnapshot snapshot, FileRow row)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(row);

        var index = indexes.GetValue(snapshot, static value => new SnapshotIndex(value));
        HistoryNode? node;
        if (row.LatestChange is not null)
            index.ByChange.TryGetValue(row.LatestChange, out node);
        else
            index.Current.TryGetValue(row.Path, out node);

        // A date or author filter selects an occurrence within a lineage. The
        // details pane still shows that lineage's complete branch history.
        if (node is not null) node = index.LatestByLineage[node.LineageKey];
        var result = new List<FileChange>(node?.Count ?? 0);
        for (; node is not null; node = node.Previous)
            result.Add(node.Change);
        return result;
    }

    public IReadOnlyList<ActivityPoint> GetActivity(BranchSnapshot snapshot, HistoryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(filter);

        var days = new Dictionary<DateTime, (HashSet<string> Commits, HashSet<string> Paths)>();
        foreach (var change in QualifyingChanges(snapshot, filter))
        {
            DateTime date = change.Commit.LocalDate.Date;
            if (!days.TryGetValue(date, out var day))
            {
                day = (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
                days.Add(date, day);
            }
            day.Commits.Add(change.Commit.Oid);
            day.Paths.Add(change.Path);
        }

        return days.OrderBy(static pair => pair.Key)
            .Select(static pair => new ActivityPoint(pair.Key, pair.Value.Commits.Count, pair.Value.Paths.Count))
            .ToArray();
    }

    public int GetContributorCount(BranchSnapshot snapshot, HistoryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(filter);
        var contributors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in QualifyingChanges(snapshot, filter))
        {
            string identity = string.IsNullOrWhiteSpace(change.Commit.AuthorEmail)
                ? change.Commit.AuthorName.Trim() : change.Commit.AuthorEmail.Trim();
            if (identity.Length > 0) contributors.Add(identity);
        }
        return contributors.Count;
    }

    private IEnumerable<FileChange> QualifyingChanges(BranchSnapshot snapshot, HistoryFilter filter)
    {
        var index = indexes.GetValue(snapshot, static value => new SnapshotIndex(value));
        var matcher = new FilterMatcher(filter);
        foreach (var change in snapshot.Changes)
        {
            if (filter.Mode == FileViewMode.AllFiles && !index.CurrentHistory.Contains(change)) continue;
            if (matcher.Matches(change.Path, change)) yield return change;
        }
    }

    private sealed class FilterMatcher(HistoryFilter filter)
    {
        private readonly string search = filter.Search.Trim();
        private readonly string author = filter.Author.Trim();
        private readonly string folder = filter.Folder.Trim('/');

        public bool Matches(string path, FileChange? change)
        {
            if (folder.Length > 0 && !path.StartsWith(folder + "/", StringComparison.Ordinal))
                return false;
            if (filter.From is { } from && (change is null || change.Commit.CommittedAt < from))
                return false;
            if (filter.Until is { } until && (change is null || change.Commit.CommittedAt >= until))
                return false;
            if (author.Length > 0 && (change is null ||
                !change.Commit.AuthorName.Equals(author, StringComparison.OrdinalIgnoreCase) &&
                !change.Commit.AuthorEmail.Equals(author, StringComparison.OrdinalIgnoreCase)))
                return false;

            return search.Length == 0 || Contains(path, search) ||
                change is not null && (Contains(change.OldPath, search) || Contains(change.Commit.Subject, search) ||
                    Contains(change.Commit.AuthorName, search) || Contains(change.Commit.Oid, search));
        }

        private static bool Contains(string? value, string term) => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
    }

    private sealed class HistoryNode(FileChange change, HistoryNode? previous)
    {
        public FileChange Change { get; } = change;
        public HistoryNode? Previous { get; } = previous;
        public FileChange LineageKey { get; } = previous?.LineageKey ?? change;
        public int Count { get; } = (previous?.Count ?? 0) + 1;
    }

    private sealed class SnapshotIndex
    {
        public Dictionary<string, HistoryNode> Current { get; } = new(StringComparer.Ordinal);
        public Dictionary<FileChange, HistoryNode> ByChange { get; } = new();
        public Dictionary<FileChange, HistoryNode> LatestByLineage { get; } = new();
        public HashSet<FileChange> CurrentHistory { get; } = [];

        public SnapshotIndex(BranchSnapshot snapshot)
        {
            var pending = new List<HistoryNode>();
            int end = snapshot.Changes.Count - 1;
            while (end >= 0)
            {
                int start = end;
                string commitId = snapshot.Changes[end].Commit.Oid;
                while (start > 0 && snapshot.Changes[start - 1].Commit.Oid == commitId)
                    start--;

                pending.Clear();
                for (int i = start; i <= end; i++)
                {
                    var change = snapshot.Changes[i];
                    HistoryNode? previous = null;
                    if (change.Kind is not ChangeKind.Added and not ChangeKind.Copied)
                        Current.TryGetValue(change.Kind == ChangeKind.Renamed ? change.OldPath ?? change.Path : change.Path, out previous);
                    var node = new HistoryNode(change, previous);
                    ByChange.Add(change, node);
                    LatestByLineage[node.LineageKey] = node;
                    pending.Add(node);
                }

                // Resolve all prior paths before applying a commit: a commit can
                // rename A to B and B to A without merging their file histories.
                foreach (var node in pending)
                {
                    if (node.Change.Kind == ChangeKind.Deleted)
                        Current.Remove(node.Change.Path);
                    else if (node.Change.Kind == ChangeKind.Renamed && node.Change.OldPath is { } oldPath)
                        Current.Remove(oldPath);
                }
                foreach (var node in pending)
                    if (node.Change.Kind != ChangeKind.Deleted)
                        Current[node.Change.Path] = node;

                end = start - 1;
            }

            foreach (var file in snapshot.Files)
            {
                Current.TryGetValue(file.Path, out var node);
                for (; node is not null && CurrentHistory.Add(node.Change); node = node.Previous) { }
            }
        }
    }
}
