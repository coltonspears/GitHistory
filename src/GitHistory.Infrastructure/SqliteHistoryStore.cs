using System.Globalization;
using System.Text.Json;
using GitHistory.Core.Models;
using Microsoft.Data.Sqlite;

namespace GitHistory.Infrastructure;

internal sealed record CommitBundle(CommitInfo Commit, List<FileChange> Changes);

/// <summary>Only complete snapshots become visible. All callers execute database work off the UI thread.</summary>
internal sealed class SqliteHistoryStore
{
    private readonly string _connectionString;

    public SqliteHistoryStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "history.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS repositories(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS commits(
                repo TEXT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
                oid TEXT NOT NULL, parent TEXT, author TEXT NOT NULL, email TEXT NOT NULL,
                authored TEXT NOT NULL, committed TEXT NOT NULL, subject TEXT NOT NULL,
                PRIMARY KEY(repo, oid));
            CREATE TABLE IF NOT EXISTS changes(
                repo TEXT NOT NULL, oid TEXT NOT NULL, ordinal INTEGER NOT NULL,
                path TEXT NOT NULL, oldPath TEXT, kind INTEGER NOT NULL,
                oldObject TEXT, newObject TEXT, oldMode TEXT NOT NULL, newMode TEXT NOT NULL,
                PRIMARY KEY(repo, oid, ordinal),
                FOREIGN KEY(repo, oid) REFERENCES commits(repo, oid) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS snapshots(
                repo TEXT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
                branch TEXT NOT NULL, tip TEXT NOT NULL, refreshed TEXT NOT NULL,
                PRIMARY KEY(repo, branch));
            CREATE TABLE IF NOT EXISTS snapshot_commits(
                repo TEXT NOT NULL, branch TEXT NOT NULL, ordinal INTEGER NOT NULL, oid TEXT NOT NULL,
                PRIMARY KEY(repo, branch, ordinal),
                FOREIGN KEY(repo, branch) REFERENCES snapshots(repo, branch) ON DELETE CASCADE,
                FOREIGN KEY(repo, oid) REFERENCES commits(repo, oid));
            CREATE TABLE IF NOT EXISTS snapshot_files(
                repo TEXT NOT NULL, branch TEXT NOT NULL, path TEXT NOT NULL, object TEXT NOT NULL, mode TEXT NOT NULL,
                PRIMARY KEY(repo, branch, path),
                FOREIGN KEY(repo, branch) REFERENCES snapshots(repo, branch) ON DELETE CASCADE);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public IReadOnlyList<RepositoryInfo> GetRepositories(CancellationToken token)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT json FROM repositories ORDER BY rowid";
        var repositories = new List<RepositoryInfo>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                repositories.Add(JsonSerializer.Deserialize(reader.GetString(0), InfrastructureJsonContext.Default.RepositoryInfo)!);
            }

        // A branch removed from the remote still has a useful completed snapshot.
        // Keep its actual ref name selectable while retaining the live default branch.
        var branches = repositories.ToDictionary(repository => repository.Id,
            repository => new SortedSet<string>(repository.Branches, StringComparer.Ordinal), StringComparer.Ordinal);
        command.CommandText = "SELECT repo,branch FROM snapshots";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (branches.TryGetValue(reader.GetString(0), out var names)) names.Add(reader.GetString(1));
            }
        for (var index = 0; index < repositories.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var repository = repositories[index];
            repositories[index] = repository with { Branches = branches[repository.Id].ToArray() };
        }
        return repositories;
    }

    public void SaveRepository(RepositoryInfo repository, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO repositories(id,json) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
        command.Parameters.AddWithValue("$id", repository.Id);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(repository, InfrastructureJsonContext.Default.RepositoryInfo));
        command.ExecuteNonQuery();
    }

    public Dictionary<string, CommitBundle> GetIndex(string repositoryId, CancellationToken token)
    {
        using var connection = Open();
        return ReadIndex(connection, repositoryId, null, token);
    }

    public BranchSnapshot? GetSnapshot(string repositoryId, string branch, CancellationToken token, string? requiredTip = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT tip,refreshed FROM snapshots WHERE repo=$repo AND branch=$branch";
        command.Parameters.AddWithValue("$repo", repositoryId);
        command.Parameters.AddWithValue("$branch", branch);
        string tip;
        DateTimeOffset refreshed;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            tip = reader.GetString(0);
            if (requiredTip is not null && tip != requiredTip) return null;
            refreshed = ParseDate(reader.GetString(1));
        }

        var commits = new List<CommitInfo>();
        var index = ReadIndex(connection, repositoryId, branch, token, transaction, commits);
        var changes = commits.SelectMany(commit => index[commit.Oid].Changes).ToArray();
        command.CommandText = "SELECT path,object,mode FROM snapshot_files WHERE repo=$repo AND branch=$branch ORDER BY path";
        using var fileReader = command.ExecuteReader();
        var files = new List<TreeFile>();
        while (fileReader.Read())
        {
            token.ThrowIfCancellationRequested();
            files.Add(new(fileReader.GetString(0), fileReader.GetString(1), fileReader.GetString(2)));
        }
        return new(repositoryId, branch, tip, refreshed, commits, changes, files);
    }

    private static Dictionary<string, CommitBundle> ReadIndex(SqliteConnection connection, string repo, string? branch, CancellationToken token, SqliteTransaction? transaction = null, List<CommitInfo>? orderedCommits = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$repo", repo);
        if (branch is not null) command.Parameters.AddWithValue("$branch", branch);
        var join = branch is null ? "" : " JOIN snapshot_commits s ON s.repo=c.repo AND s.oid=c.oid AND s.branch=$branch";
        command.CommandText = "SELECT c.oid,c.parent,c.author,c.email,c.authored,c.committed,c.subject FROM commits c" + join +
            " WHERE c.repo=$repo" + (branch is null ? "" : " ORDER BY s.ordinal");
        var index = new Dictionary<string, CommitBundle>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var commit = new CommitInfo(reader.GetString(0), NullableString(reader, 1), reader.GetString(2), reader.GetString(3),
                    ParseDate(reader.GetString(4)), ParseDate(reader.GetString(5)), reader.GetString(6));
                index.Add(commit.Oid, new(commit, []));
                orderedCommits?.Add(commit);
            }
        command.CommandText = "SELECT c.oid,c.path,c.oldPath,c.kind,c.oldObject,c.newObject,c.oldMode,c.newMode FROM changes c" + join +
            " WHERE c.repo=$repo ORDER BY " + (branch is null ? "c.oid,c.ordinal" : "s.ordinal,c.ordinal");
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var bundle = index[reader.GetString(0)];
                bundle.Changes.Add(new(bundle.Commit, reader.GetString(1), NullableString(reader, 2), (ChangeKind)reader.GetInt32(3),
                    NullableString(reader, 4), NullableString(reader, 5), reader.GetString(6), reader.GetString(7)));
            }
        return index;
    }

    public void PublishSnapshot(BranchSnapshot snapshot, IReadOnlyList<CommitBundle> newCommits, CancellationToken token)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var commitCommand = CreatePrepared(connection, transaction,
            "INSERT OR IGNORE INTO commits VALUES($repo,$oid,$parent,$author,$email,$authored,$committed,$subject)",
            "$repo", "$oid", "$parent", "$author", "$email", "$authored", "$committed", "$subject");
        using var changeCommand = CreatePrepared(connection, transaction,
            "INSERT OR IGNORE INTO changes VALUES($repo,$oid,$ordinal,$path,$oldPath,$kind,$oldObject,$newObject,$oldMode,$newMode)",
            "$repo", "$oid", "$ordinal", "$path", "$oldPath", "$kind", "$oldObject", "$newObject", "$oldMode", "$newMode");
        foreach (var bundle in newCommits)
        {
            token.ThrowIfCancellationRequested();
            var commit = bundle.Commit;
            Execute(commitCommand, snapshot.RepositoryId, commit.Oid, commit.ParentOid, commit.AuthorName, commit.AuthorEmail,
                FormatDate(commit.AuthoredAt), FormatDate(commit.CommittedAt), commit.Subject);
            for (var i = 0; i < bundle.Changes.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var change = bundle.Changes[i];
                Execute(changeCommand, snapshot.RepositoryId, commit.Oid, i, change.Path, change.OldPath, (int)change.Kind,
                    change.OldObjectId, change.NewObjectId, change.OldMode, change.NewMode);
            }
        }
        using var deleteCommand = CreatePrepared(connection, transaction, "DELETE FROM snapshots WHERE repo=$repo AND branch=$branch", "$repo", "$branch");
        Execute(deleteCommand, snapshot.RepositoryId, snapshot.Branch);
        using var snapshotCommand = CreatePrepared(connection, transaction, "INSERT INTO snapshots VALUES($repo,$branch,$tip,$refreshed)", "$repo", "$branch", "$tip", "$refreshed");
        Execute(snapshotCommand, snapshot.RepositoryId, snapshot.Branch, snapshot.TipOid, FormatDate(snapshot.RefreshedAt));
        using var membershipCommand = CreatePrepared(connection, transaction, "INSERT INTO snapshot_commits VALUES($repo,$branch,$ordinal,$oid)", "$repo", "$branch", "$ordinal", "$oid");
        for (var i = 0; i < snapshot.Commits.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            Execute(membershipCommand, snapshot.RepositoryId, snapshot.Branch, i, snapshot.Commits[i].Oid);
        }
        using var fileCommand = CreatePrepared(connection, transaction, "INSERT INTO snapshot_files VALUES($repo,$branch,$path,$object,$mode)", "$repo", "$branch", "$path", "$object", "$mode");
        foreach (var file in snapshot.Files)
        {
            token.ThrowIfCancellationRequested();
            Execute(fileCommand, snapshot.RepositoryId, snapshot.Branch, file.Path, file.ObjectId, file.Mode);
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public void UpdateRefreshTime(BranchSnapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE snapshots SET refreshed=$refreshed WHERE repo=$repo AND branch=$branch AND tip=$tip";
        command.Parameters.AddWithValue("$refreshed", FormatDate(snapshot.RefreshedAt));
        command.Parameters.AddWithValue("$repo", snapshot.RepositoryId);
        command.Parameters.AddWithValue("$branch", snapshot.Branch);
        command.Parameters.AddWithValue("$tip", snapshot.TipOid);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The saved branch changed during refresh. Refresh again to load its current snapshot.");
    }

    public void RemoveRepository(string repositoryId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = CreatePrepared(connection, transaction, "DELETE FROM snapshots WHERE repo=$repo", "$repo");
        Execute(command, repositoryId);
        command.CommandText = "DELETE FROM repositories WHERE id=$repo";
        command.ExecuteNonQuery();
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private static SqliteCommand CreatePrepared(SqliteConnection connection, SqliteTransaction transaction, string sql, params string[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var name in parameters) command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        command.Prepare();
        return command;
    }

    private static void Execute(SqliteCommand command, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++) command.Parameters[i].Value = values[i] ?? DBNull.Value;
        command.ExecuteNonQuery();
    }

    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string FormatDate(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
