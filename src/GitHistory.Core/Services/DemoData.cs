using System.Security.Cryptography;
using System.Text;
using GitHistory.Core.Models;

namespace GitHistory.Core.Services;

/// <summary>Local, deterministic example data. No network access or repository mutation.</summary>
public static class DemoData
{
    public static RepositoryInfo Repository { get; } = new(
        "demo", "GitHistory Showcase", "demo://githistory", "main", ["main", "develop", "feature/branch-insights"], IsDemo: true);

    public static BranchSnapshot CreateSnapshot(string branch = "main", DateTimeOffset? now = null)
    {
        DateTimeOffset anchor = now ?? DateTimeOffset.Now;
        string[] paths =
        [
            "src/GitHistory.App/App.xaml", "src/GitHistory.App/App.xaml.cs",
            "src/GitHistory.App/Views/WorkspaceView.xaml", "src/GitHistory.App/Views/RepositoryPicker.xaml",
            "src/GitHistory.App/Views/CommandPalette.xaml", "src/GitHistory.App/Resources/Colors.xaml",
            "src/GitHistory.App/Resources/Icons/repository.svg", "src/GitHistory.App/Resources/Icons/branch.svg",
            "src/GitHistory.App/Behaviors/DockingLayoutBehavior.cs", "src/GitHistory.App/Converters/AgeBrushConverter.cs",
            "src/GitHistory.Core/ViewModels/WorkspaceViewModel.cs", "src/GitHistory.Core/ViewModels/RepositoryViewModel.cs",
            "src/GitHistory.Core/ViewModels/FileHistoryViewModel.cs", "src/GitHistory.Core/Models/RepositoryInfo.cs",
            "src/GitHistory.Core/Models/FileChange.cs", "src/GitHistory.Core/Services/HistoryQueryService.cs",
            "src/GitHistory.Infrastructure/Git/GitProcess.cs", "src/GitHistory.Infrastructure/Git/RepositoryService.cs",
            "src/GitHistory.Infrastructure/Git/DiffParser.cs", "src/GitHistory.Infrastructure/Storage/SnapshotStore.cs",
            "tests/GitHistory.Tests/Git/FirstParentTests.cs", "tests/GitHistory.Tests/Git/RenameHistoryTests.cs",
            "tests/GitHistory.Tests/Queries/HistoryQueryTests.cs", "tests/GitHistory.Tests/ViewModels/WorkspaceTests.cs",
            "docs/architecture.md", "docs/getting-started.md", "docs/recency-semantics.md", "README.md",
            ".github/workflows/build.yml", ".editorconfig", "Directory.Build.props", "NuGet.config",
            "assets/workspace-preview.png", "src/GitHistory.App/Views/LegacyHistoryView.xaml", "vendor/diff-samples"
        ];
        string[] authors = ["Alex Rivera", "Morgan Chen", "Sam Taylor", "Jamie Brooks"];
        string[] subjects =
        [
            "Remember the selected repository between sessions", "Polish the command palette keyboard flow",
            "Render the branch activity chart", "Keep filename comparisons case-sensitive",
            "Persist completed snapshots atomically", "Add Windows 11 theme resources",
            "Follow renamed files through first-parent history", "Expose cancellation while fetching a branch",
            "Show old and new line numbers in the diff", "Add clear offline and empty states",
            "Document merge landing timestamps", "Restore docked panes and saved grid columns",
            "Improve search responsiveness for large histories", "Add branch and repository SVG icons",
            "Cover binary patches and submodule updates", "Merge pull request #42: workspace refinements"
        ];

        var commits = new List<CommitInfo>();
        var changes = new List<FileChange>();
        var current = new Dictionary<string, TreeFile>(StringComparer.Ordinal);
        int sequence = 1;

        CommitInfo AddCommit(DateTimeOffset time, string subject, int authorIndex)
        {
            string author = authors[authorIndex % authors.Length];
            var commit = new CommitInfo(Oid(sequence++), commits.LastOrDefault()?.Oid, author,
                author.Replace(' ', '.').ToLowerInvariant() + "@example.com", time.AddMinutes(-12), time, subject);
            commits.Add(commit);
            return commit;
        }

        void AddChange(CommitInfo commit, string path, ChangeKind kind = ChangeKind.Modified, string? oldPath = null)
        {
            current.TryGetValue(oldPath ?? path, out var previous);
            string mode = path == "vendor/diff-samples" ? "160000" : "100644";
            string? nextOid = kind == ChangeKind.Deleted ? null : Oid(sequence++ + 10000);
            changes.Add(new FileChange(commit, path, oldPath, kind, previous?.ObjectId, nextOid,
                previous?.Mode ?? "000000", kind == ChangeKind.Deleted ? "000000" : mode));
            if (kind == ChangeKind.Deleted)
                current.Remove(path);
            else
            {
                if (oldPath is not null)
                    current.Remove(oldPath);
                current[path] = new TreeFile(path, nextOid!, mode);
            }
        }

        var initial = AddCommit(anchor.AddDays(-56), "Create the GitHistory workspace", 0);
        foreach (string path in paths)
            AddChange(initial, path, ChangeKind.Added);

        // A spread of old files makes the age indicators useful, while the latest
        // week includes enough activity to explore the default Recent view.
        for (int i = 0; i < 44; i++)
        {
            double daysAgo = i < 30 ? 53 - i * 1.45 : 6.5 - (i - 30) * 0.47;
            var commit = AddCommit(anchor.AddDays(-daysAgo), subjects[i % subjects.Length], i);
            AddChange(commit, paths[(i * 7 + 2) % 32]);
            AddChange(commit, paths[(i * 7 + 12) % 32]);
            if (i % 4 == 0)
                AddChange(commit, paths[(i * 7 + 19) % 32]);
        }

        var rename = AddCommit(anchor.AddHours(-8), "Rename workspace view to match its role", 1);
        AddChange(rename, "src/GitHistory.App/Views/MainWindow.xaml", ChangeKind.Renamed, "src/GitHistory.App/Views/WorkspaceView.xaml");
        var cleanUp = AddCommit(anchor.AddHours(-6), "Remove the legacy history screen", 2);
        AddChange(cleanUp, "src/GitHistory.App/Views/LegacyHistoryView.xaml", ChangeKind.Deleted);
        var preview = AddCommit(anchor.AddHours(-4), "Refresh the workspace preview and diff samples", 3);
        AddChange(preview, "assets/workspace-preview.png");
        AddChange(preview, "vendor/diff-samples");
        var latest = AddCommit(anchor.AddMinutes(-42), "Merge pull request #58: fast, cancellable history search", 0);
        AddChange(latest, "src/GitHistory.Core/Services/HistoryQueryService.cs");
        AddChange(latest, "src/GitHistory.Core/ViewModels/WorkspaceViewModel.cs");
        AddChange(latest, "tests/GitHistory.Tests/Queries/HistoryQueryTests.cs");

        if (!branch.Equals("main", StringComparison.Ordinal))
        {
            var branchCommit = AddCommit(anchor.AddMinutes(-16), branch == "develop"
                ? "Prototype a compact file-history layout" : "Add branch activity insights", 1);
            AddChange(branchCommit, "src/GitHistory.App/Views/BranchInsights.xaml", ChangeKind.Added);
            AddChange(branchCommit, "src/GitHistory.Core/ViewModels/WorkspaceViewModel.cs");
        }

        commits.Reverse();
        // Reverse the commit groups rather than individual changes to retain a
        // pleasant, stable ordering within the selected commit.
        var orderedChanges = changes.GroupBy(static change => change.Commit.Oid).Reverse().SelectMany(static group => group).ToArray();
        return new BranchSnapshot(Repository.Id, branch, commits[0].Oid, anchor.AddMinutes(-3),
            commits.ToArray(), orderedChanges, current.Values.OrderBy(static file => file.Path, StringComparer.Ordinal).ToArray());
    }

    public static DiffResult CreateDiff(FileChange change)
    {
        if (change.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return new DiffResult([new(null, null, "Binary image changed · preview data is not included in this demo.", DiffLineKind.Notice)],
                "Demo · Binary file changed", IsBinary: true);
        if (change.NewMode == "160000" || change.OldMode == "160000")
            return new DiffResult([
                new(null, null, "@@ -1 +1 @@", DiffLineKind.Header),
                new(1, null, "Subproject commit " + change.OldObjectId, DiffLineKind.Deleted),
                new(null, 1, "Subproject commit " + change.NewObjectId, DiffLineKind.Added)
            ], "Demo · Submodule pointer updated");

        var lines = new List<DiffLine>
        {
            new(null, null, $"diff --git a/{change.OldPath ?? change.Path} b/{change.Path}", DiffLineKind.Header)
        };
        if (change.Kind == ChangeKind.Renamed)
        {
            lines.Add(new(null, null, "rename from " + change.OldPath, DiffLineKind.Header));
            lines.Add(new(null, null, "rename to " + change.Path, DiffLineKind.Header));
            return new DiffResult(lines, "Demo · File renamed; content is unchanged");
        }
        if (change.Kind == ChangeKind.Deleted)
        {
            lines.Add(new(null, null, "@@ -1,3 +0,0 @@", DiffLineKind.Header));
            lines.Add(new(1, null, "<!-- Legacy history view -->", DiffLineKind.Deleted));
            lines.Add(new(2, null, "<UserControl>", DiffLineKind.Deleted));
            lines.Add(new(3, null, "</UserControl>", DiffLineKind.Deleted));
            return new DiffResult(lines, "Demo · 3 lines removed");
        }

        bool added = change.Kind is ChangeKind.Added or ChangeKind.Copied;
        string[] content = change.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
            ? ["<Grid Margin=\"24\">", "    <Grid.RowDefinitions>", "        <RowDefinition Height=\"Auto\" />", "        <RowDefinition Height=\"*\" />", "    </Grid.RowDefinitions>", "    <TextBlock Text=\"Last changed on this branch\" />", "    <dxg:GridControl Grid.Row=\"1\" ItemsSource=\"{Binding Files}\" />", "</Grid>"]
            : change.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                ? ["public async Task RefreshAsync(CancellationToken cancellationToken)", "{", "    IsBusy = true;", "    try", "    {", "        var snapshot = await repository.RefreshAsync(cancellationToken);", "        Files = queries.Query(snapshot, CurrentFilter, clock.GetUtcNow());", "    }", "    finally", "    {", "        IsBusy = false;", "    }", "}"]
                : ["# GitHistory", "", "Find the files that changed on your branch.", "", "- Browse complete first-parent history.", "- Follow renames and inspect native diffs.", "- Open a saved snapshot while offline."];

        if (added)
        {
            lines.Add(new(null, null, $"@@ -0,0 +1,{content.Length} @@", DiffLineKind.Header));
            for (int i = 0; i < content.Length; i++)
                lines.Add(new(null, i + 1, content[i], DiffLineKind.Added));
            return new DiffResult(lines, $"Demo · {content.Length} lines added");
        }

        lines.Add(new(null, null, $"@@ -1,{content.Length} +1,{content.Length + 1} @@", DiffLineKind.Header));
        int oldLine = 1;
        int newLine = 1;
        for (int i = 0; i < content.Length; i++)
        {
            if (i == 2)
            {
                lines.Add(new(oldLine++, null, change.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    ? "    // Refresh the selected repository." : "    <!-- Repository workspace -->", DiffLineKind.Deleted));
                lines.Add(new(null, newLine++, content[i], DiffLineKind.Added));
                lines.Add(new(null, newLine++, change.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    ? "    cancellationToken.ThrowIfCancellationRequested();" : "    <!-- Keep selection available while refreshing. -->", DiffLineKind.Added));
            }
            else
                lines.Add(new(oldLine++, newLine++, content[i], DiffLineKind.Context));
        }
        return new DiffResult(lines, "Demo · 2 additions, 1 deletion · illustrative patch");
    }

    private static string Oid(int value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"githistory-demo:{value}")))[..40];
}
