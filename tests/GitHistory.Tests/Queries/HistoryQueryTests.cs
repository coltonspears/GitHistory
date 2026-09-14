using GitHistory.Core.Models;
using GitHistory.Core.Services;

namespace GitHistory.Tests.Queries;

public sealed class HistoryQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly HistoryQueryService service = new();

    [Fact]
    public void Latest_touch_uses_ancestry_even_when_committer_clocks_are_skewed()
    {
        var newest = Change("3", "src/a.cs", ChangeKind.Modified, Now.AddDays(-2));
        var older = Change("2", "src/a.cs", ChangeKind.Modified, Now.AddDays(-1));
        var initial = Change("1", "src/a.cs", ChangeKind.Added, Now.AddDays(-8));
        var snapshot = Snapshot([newest, older, initial], "src/a.cs");

        var recent = Assert.Single(service.Query(snapshot, Filter(), Now));
        Assert.Same(newest, recent.LatestChange);
        Assert.Equal(2, recent.TouchCount);
        var all = Assert.Single(service.Query(snapshot, Filter(FileViewMode.AllFiles, from: null), Now));
        Assert.Same(newest, all.LatestChange);
        Assert.Equal(3, all.TouchCount);
        Assert.Equal([newest, older, initial], service.GetFileHistory(snapshot, all));
    }

    [Fact]
    public void Recent_includes_deleted_and_renamed_paths_but_all_files_matches_tip_tree()
    {
        var rename = Change("3", "src/new.cs", ChangeKind.Renamed, Now.AddDays(-1), "src/old.cs");
        var delete = Change("3", "removed.txt", ChangeKind.Deleted, Now.AddDays(-1));
        var initialOld = Change("1", "src/old.cs", ChangeKind.Added, Now.AddDays(-20));
        var initialRemoved = Change("1", "removed.txt", ChangeKind.Added, Now.AddDays(-20));
        var snapshot = Snapshot([rename, delete, initialOld, initialRemoved], "src/new.cs");

        var recent = service.Query(snapshot, Filter(), Now);
        Assert.Equal(2, recent.Count);
        Assert.Contains(recent, row => row.ChangeType == "Deleted" && row.Path == "removed.txt");
        Assert.Contains(recent, row => row.ChangeType == "Renamed" && row.PreviousPath == "src/old.cs");
        var all = Assert.Single(service.Query(snapshot, Filter(FileViewMode.AllFiles, from: null), Now));
        Assert.Equal("src/new.cs", all.Path);
        Assert.Equal([rename, initialOld], service.GetFileHistory(snapshot, all));
    }

    [Fact]
    public void File_history_includes_newer_touches_when_a_date_or_author_filter_selects_an_older_change()
    {
        var newest = Change("3", "shared.cs", ChangeKind.Modified, Now, author: "Bob");
        var older = Change("2", "shared.cs", ChangeKind.Modified, Now.AddDays(-2), author: "Alice");
        var added = Change("1", "shared.cs", ChangeKind.Added, Now.AddDays(-8), author: "Bob");
        var snapshot = Snapshot([newest, older, added], "shared.cs");

        var byDate = Assert.Single(service.Query(snapshot, Filter(until: Now.AddDays(-1)), Now));
        var byAuthor = Assert.Single(service.Query(snapshot, Filter() with { Author = "Alice" }, Now));
        Assert.Same(older, byDate.LatestChange);
        Assert.Same(older, byAuthor.LatestChange);
        Assert.Equal([newest, older, added], service.GetFileHistory(snapshot, byDate));
        Assert.Equal([newest, older, added], service.GetFileHistory(snapshot, byAuthor));
    }

    [Fact]
    public void File_history_follows_a_filtered_old_path_through_later_renames_and_modifications()
    {
        var latest = Change("4", "new.cs", ChangeKind.Modified, Now.AddDays(-1));
        var rename = Change("3", "new.cs", ChangeKind.Renamed, Now.AddDays(-2), "old.cs");
        var earlier = Change("2", "old.cs", ChangeKind.Modified, Now.AddDays(-3));
        var added = Change("1", "old.cs", ChangeKind.Added, Now.AddDays(-4));
        var snapshot = Snapshot([latest, rename, earlier, added], "new.cs");

        var row = Assert.Single(service.Query(snapshot, Filter(until: Now.AddDays(-2.5)), Now));
        Assert.Equal("old.cs", row.Path);
        Assert.Same(earlier, row.LatestChange);
        Assert.Equal([latest, rename, earlier, added], service.GetFileHistory(snapshot, row));
    }

    [Fact]
    public void Readded_file_and_previous_deleted_occurrence_have_separate_histories()
    {
        var editNew = Change("5", "config.json", ChangeKind.Modified, Now);
        var readd = Change("4", "config.json", ChangeKind.Added, Now.AddDays(-1));
        var deletion = Change("3", "config.json", ChangeKind.Deleted, Now.AddDays(-3));
        var editOld = Change("2", "config.json", ChangeKind.Modified, Now.AddDays(-4));
        var addOld = Change("1", "config.json", ChangeKind.Added, Now.AddDays(-10));
        var snapshot = Snapshot([editNew, readd, deletion, editOld, addOld], "config.json");

        var current = Assert.Single(service.Query(snapshot, Filter(FileViewMode.AllFiles, from: null), Now));
        Assert.Equal([editNew, readd], service.GetFileHistory(snapshot, current));
        Assert.Equal(2, current.TouchCount);

        var historical = Assert.Single(service.Query(snapshot, Filter(until: Now.AddDays(-2)), Now));
        Assert.Same(deletion, historical.LatestChange);
        Assert.Equal([deletion, editOld, addOld], service.GetFileHistory(snapshot, historical));

        var beforeDeletion = Assert.Single(service.Query(snapshot, Filter(until: Now.AddDays(-3.5)), Now));
        Assert.Same(editOld, beforeDeletion.LatestChange);
        Assert.Equal([deletion, editOld, addOld], service.GetFileHistory(snapshot, beforeDeletion));
    }

    [Fact]
    public void Rename_chain_follows_old_paths_and_a_copy_begins_a_new_lineage()
    {
        var renameAgain = Change("4", "c.cs", ChangeKind.Renamed, Now, "b.cs");
        var copy = Change("3", "copy.cs", ChangeKind.Copied, Now.AddDays(-1), "b.cs");
        var rename = Change("2", "b.cs", ChangeKind.Renamed, Now.AddDays(-2), "a.cs");
        var added = Change("1", "a.cs", ChangeKind.Added, Now.AddDays(-3));
        var snapshot = Snapshot([renameAgain, copy, rename, added], "c.cs", "copy.cs");

        var rows = service.Query(snapshot, Filter(FileViewMode.AllFiles, from: null), Now);
        Assert.Equal([renameAgain, rename, added], service.GetFileHistory(snapshot, rows.Single(row => row.Path == "c.cs")));
        Assert.Equal([copy], service.GetFileHistory(snapshot, rows.Single(row => row.Path == "copy.cs")));

        var beforeRename = Assert.Single(service.Query(snapshot, Filter(until: Now.AddDays(-2.5)), Now));
        Assert.Equal([renameAgain, rename, added], service.GetFileHistory(snapshot, beforeRename));
    }

    [Fact]
    public void Simultaneous_rename_swap_keeps_both_prior_file_lineages()
    {
        var toB = Change("2", "b.cs", ChangeKind.Renamed, Now, "a.cs");
        var toA = Change("2", "a.cs", ChangeKind.Renamed, Now, "b.cs");
        var addA = Change("1", "a.cs", ChangeKind.Added, Now.AddDays(-1));
        var addB = Change("1", "b.cs", ChangeKind.Added, Now.AddDays(-1));
        var snapshot = Snapshot([toB, toA, addA, addB], "a.cs", "b.cs");

        var rows = service.Query(snapshot, Filter(FileViewMode.AllFiles, from: null), Now);
        Assert.Equal([toA, addB], service.GetFileHistory(snapshot, rows.Single(row => row.Path == "a.cs")));
        Assert.Equal([toB, addA], service.GetFileHistory(snapshot, rows.Single(row => row.Path == "b.cs")));

        var beforeSwap = service.Query(snapshot, Filter(until: Now.AddHours(-1)), Now);
        Assert.Equal([toB, addA], service.GetFileHistory(snapshot, beforeSwap.Single(row => row.Path == "a.cs")));
        Assert.Equal([toA, addB], service.GetFileHistory(snapshot, beforeSwap.Single(row => row.Path == "b.cs")));
    }

    [Fact]
    public void Paths_remain_case_sensitive_search_is_case_insensitive_and_folder_respects_boundaries()
    {
        var snapshot = Snapshot([
            Change("1", "src/数据/Foo.cs", ChangeKind.Added, Now),
            Change("1", "src/数据/foo.cs", ChangeKind.Added, Now),
            Change("1", "src-other/数据/Foo.cs", ChangeKind.Added, Now),
            Change("1", "Src/数据/Foo.cs", ChangeKind.Added, Now)
        ], "src/数据/Foo.cs", "src/数据/foo.cs", "src-other/数据/Foo.cs", "Src/数据/Foo.cs");

        var rows = service.Query(snapshot, Filter() with { Search = "FOO", Folder = "/src/数据/" }, Now);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Path.EndsWith("Foo.cs", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Path.EndsWith("foo.cs", StringComparison.Ordinal));
        Assert.Equal(4, service.Query(snapshot, Filter() with { Search = "数据" }, Now).Count);
    }

    [Fact]
    public void Dates_are_start_inclusive_end_exclusive_and_use_committer_time_author_matches_name_or_email()
    {
        var from = Change("3", "from.cs", ChangeKind.Added, Now.AddDays(-7), author: "Morgan Chen");
        var until = Change("2", "until.cs", ChangeKind.Added, Now, author: "Morgan Chen");
        var outside = Change("1", "old.cs", ChangeKind.Added, Now.AddDays(-7).AddTicks(-1), author: "Morgan Chen");
        var otherAuthor = Change("4", "other.cs", ChangeKind.Added, Now, author: "Morgan");
        // An old authored time must not hide a change merged onto this branch today.
        until = until with { Commit = until.Commit with { AuthoredAt = Now.AddDays(-100) } };
        var snapshot = Snapshot([otherAuthor, from, until, outside], "from.cs", "until.cs", "old.cs", "other.cs");

        var rows = service.Query(snapshot, Filter(until: Now.AddTicks(1)) with { Author = "morgan chen" }, Now);
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, row => row.Path == "old.cs");
        Assert.Equal(2, service.Query(snapshot, Filter(until: Now.AddTicks(1)) with { Author = "MORGAN.CHEN@EXAMPLE.COM" }, Now).Count);
        Assert.Same(from, Assert.Single(service.Query(snapshot, Filter(until: Now) with { Author = "Morgan Chen" }, Now)).LatestChange);
    }

    [Fact]
    public void Search_can_find_a_previous_name_and_commit_subject()
    {
        var rename = Change("2", "new.cs", ChangeKind.Renamed, Now, "old.cs") with
        {
            Commit = Commit("2", Now) with { Subject = "Fix cancellation handling" }
        };
        var snapshot = Snapshot([rename, Change("1", "old.cs", ChangeKind.Added, Now.AddDays(-1))], "new.cs");

        Assert.Single(service.Query(snapshot, Filter(FileViewMode.AllFiles, from: null) with { Search = "OLD.CS" }, Now));
        Assert.Single(service.Query(snapshot, Filter() with { Search = "cancellation" }, Now));
    }

    [Fact]
    public void Activity_counts_distinct_commits_and_exact_paths_by_local_committer_day()
    {
        var today = Now.ToLocalTime();
        var one = Commit("3", today);
        var two = Commit("2", today.AddMinutes(-30));
        var yesterday = Commit("1", today.AddDays(-1));
        var changes = new[]
        {
            Change(one, "src/A.cs"), Change(one, "src/a.cs"), Change(one, "other/no.cs"),
            Change(two, "src/A.cs"), Change(yesterday, "src/A.cs")
        };
        var snapshot = Snapshot(changes, "src/A.cs", "src/a.cs", "other/no.cs");

        var activity = service.GetActivity(snapshot, Filter() with { Folder = "src", Search = ".CS", Author = "Alex Rivera" });
        Assert.Equal(2, activity.Count);
        Assert.Equal(new ActivityPoint(yesterday.LocalDate.Date, 1, 1), activity[0]);
        Assert.Equal(new ActivityPoint(one.LocalDate.Date, 2, 2), activity[1]);
        Assert.Single(service.GetActivity(snapshot, Filter(from: today.AddMinutes(-10)) with { Folder = "src" }));
    }

    [Fact]
    public void All_files_activity_excludes_deleted_file_occurrences()
    {
        var delete = Change("3", "gone.cs", ChangeKind.Deleted, Now);
        var add = Change("2", "gone.cs", ChangeKind.Added, Now.AddDays(-1));
        var kept = Change("1", "kept.cs", ChangeKind.Added, Now.AddDays(-2));
        var snapshot = Snapshot([delete, add, kept], "kept.cs");

        Assert.Equal(3, service.GetActivity(snapshot, Filter()).Count);
        Assert.Equal([new ActivityPoint(kept.Commit.LocalDate.Date, 1, 1)], service.GetActivity(snapshot, Filter(FileViewMode.AllFiles, from: null)));
    }

    [Fact]
    public void Contributor_count_includes_every_matching_author_even_when_they_touched_the_same_file()
    {
        var bob = Change("3", "src/shared.cs", ChangeKind.Modified, Now, author: "Bob");
        var alice = Change("2", "src/shared.cs", ChangeKind.Added, Now.AddDays(-1), author: "Alice");
        var carol = Change("1", "other/old.cs", ChangeKind.Added, Now.AddDays(-20), author: "Carol");
        var snapshot = Snapshot([bob, alice, carol], "src/shared.cs", "other/old.cs");

        Assert.Single(service.Query(snapshot, Filter(), Now));
        Assert.Equal(2, service.GetContributorCount(snapshot, Filter()));
        Assert.Equal(2, service.GetContributorCount(snapshot, Filter() with { Folder = "src", Search = "SHARED" }));
        Assert.Equal(1, service.GetContributorCount(snapshot, Filter() with { Author = "Alice" }));
        Assert.Equal(1, service.GetContributorCount(snapshot, Filter(until: Now)));
        Assert.Equal(3, service.GetContributorCount(snapshot, Filter(FileViewMode.AllFiles, from: null)));
    }

    [Fact]
    public void Contributor_count_uses_email_identity_and_all_files_excludes_previous_deleted_occurrences()
    {
        var recreate = Change("4", "same.cs", ChangeKind.Added, Now, author: "New Author");
        var deleted = Change("3", "same.cs", ChangeKind.Deleted, Now.AddDays(-1), author: "Previous Author");
        var previous = Change("2", "same.cs", ChangeKind.Added, Now.AddDays(-2), author: "Previous Author");
        var alias = Change("1", "alias.cs", ChangeKind.Added, Now.AddDays(-3), author: "Alias") with
        {
            Commit = Commit("1", Now.AddDays(-3), "Alias") with { AuthorEmail = "NEW.AUTHOR@EXAMPLE.COM" }
        };
        var snapshot = Snapshot([recreate, deleted, previous, alias], "same.cs", "alias.cs");

        Assert.Equal(2, service.GetContributorCount(snapshot, Filter()));
        Assert.Equal(1, service.GetContributorCount(snapshot, Filter(FileViewMode.AllFiles, from: null)));
    }

    [Fact]
    public void Empty_snapshots_unknown_touches_and_inverted_date_ranges_are_safe()
    {
        Assert.Empty(service.Query(Snapshot([]), Filter(), Now));
        Assert.Empty(service.GetActivity(Snapshot([]), Filter()));
        var snapshot = Snapshot([], "unindexed.cs");
        var unknown = Assert.Single(service.Query(snapshot, Filter(FileViewMode.AllFiles, from: null), Now));
        Assert.Null(unknown.LatestChange);
        Assert.Equal("Unknown", unknown.Age);
        Assert.Empty(service.GetFileHistory(snapshot, unknown));
        Assert.Empty(service.Query(snapshot, Filter(FileViewMode.AllFiles, from: Now.AddDays(-7)), Now));
        Assert.Empty(service.Query(Snapshot([Change("1", "a", ChangeKind.Added, Now)], "a"),
            Filter(from: Now.AddDays(1), until: Now), Now));
    }

    [Fact]
    public void Demo_has_recent_old_deleted_renamed_binary_and_submodule_examples()
    {
        var snapshot = DemoData.CreateSnapshot(now: Now);
        Assert.True(DemoData.Repository.IsDemo);
        Assert.True(snapshot.Files.Count >= 30);
        Assert.Contains(snapshot.Changes, change => change.Kind == ChangeKind.Deleted);
        Assert.Contains(snapshot.Changes, change => change.Kind == ChangeKind.Renamed);
        Assert.True(snapshot.Commits[0].CommittedAt > Now.AddHours(-1));
        Assert.True(snapshot.Commits[^1].CommittedAt <= Now.AddDays(-56));
        Assert.NotEmpty(service.Query(snapshot, Filter(), Now));
        var binary = snapshot.Changes.First(change => change.Path.EndsWith(".png", StringComparison.Ordinal));
        Assert.True(DemoData.CreateDiff(binary).IsBinary);
        var submodule = snapshot.Changes.First(change => change.NewMode == "160000");
        Assert.Contains(DemoData.CreateDiff(submodule).Lines, line => line.Text.StartsWith("Subproject commit", StringComparison.Ordinal));
        Assert.Contains(DemoData.CreateSnapshot("develop", Now).Files, file => file.Path.EndsWith("BranchInsights.xaml", StringComparison.Ordinal));
    }

    private static HistoryFilter Filter(FileViewMode mode = FileViewMode.RecentChanges, DateTimeOffset? from = default, DateTimeOffset? until = null) =>
        new(mode, from ?? (mode == FileViewMode.RecentChanges ? Now.AddDays(-7) : null), until);

    private static CommitInfo Commit(string oid, DateTimeOffset time, string author = "Alex Rivera") =>
        new(oid, null, author, author.Replace(' ', '.').ToLowerInvariant() + "@example.com", time, time, "Update history");

    private static FileChange Change(string oid, string path, ChangeKind kind, DateTimeOffset time, string? oldPath = null, string author = "Alex Rivera") =>
        Change(Commit(oid, time, author), path, kind, oldPath);

    private static FileChange Change(CommitInfo commit, string path, ChangeKind kind = ChangeKind.Modified, string? oldPath = null) =>
        new(commit, path, oldPath, kind, kind == ChangeKind.Added ? null : "old", kind == ChangeKind.Deleted ? null : "new",
            kind == ChangeKind.Added ? "000000" : "100644", kind == ChangeKind.Deleted ? "000000" : "100644");

    private static BranchSnapshot Snapshot(IReadOnlyList<FileChange> changes, params string[] paths) =>
        new("repo", "main", changes.FirstOrDefault()?.Commit.Oid ?? "empty", Now,
            changes.Select(change => change.Commit).DistinctBy(commit => commit.Oid).ToArray(), changes,
            paths.Select(path => new TreeFile(path, "new", "100644")).ToArray());
}
