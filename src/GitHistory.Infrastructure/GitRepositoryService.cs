using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GitHistory.Core.Models;
using GitHistory.Core.Services;
using Microsoft.Extensions.Logging;

namespace GitHistory.Infrastructure;

public sealed partial class GitRepositoryService : IRepositoryService, IDisposable
{
    private readonly string _cacheDirectory;
    private readonly ILogger<GitRepositoryService> _logger;
    private readonly GitProcessRunner _git = new();
    private readonly SqliteHistoryStore _store;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly bool _allowLocalRemotes;

    public GitRepositoryService(string dataDirectory, ILogger<GitRepositoryService> logger) : this(dataDirectory, logger, false) { }

    internal GitRepositoryService(string dataDirectory, ILogger<GitRepositoryService> logger, bool allowLocalRemotes)
    {
        _cacheDirectory = Path.GetFullPath(Path.Combine(dataDirectory, "repositories"));
        Directory.CreateDirectory(_cacheDirectory);
        _store = new(dataDirectory);
        _logger = logger;
        _allowLocalRemotes = allowLocalRemotes;
    }

    public Task<IReadOnlyList<RepositoryInfo>> GetRepositoriesAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => _store.GetRepositories(cancellationToken), cancellationToken);

    public Task<BranchSnapshot?> GetCachedSnapshotAsync(RepositoryInfo repository, string branch, CancellationToken cancellationToken) =>
        Task.Run(() => _store.GetSnapshot(repository.Id, branch, cancellationToken), cancellationToken);

    public Task<RepositoryInfo> ConnectAsync(string remoteUrl, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var remote = ValidateRemote(remoteUrl, _allowLocalRemotes);
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                progress?.Report(new("Connecting", "Discovering remote branches using your Git credentials"));
                var discovery = await DiscoverAsync(remote, cancellationToken).ConfigureAwait(false);
                var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(remote)))[..24];
                var name = RemoteName(remote);
                var repository = new RepositoryInfo(id, name, remote, discovery.DefaultBranch, discovery.Branches);
                _store.SaveRepository(repository, cancellationToken);
                return repository;
            }
            finally { _mutationGate.Release(); }
        }, cancellationToken);

    public Task<BranchSnapshot> RefreshAsync(RepositoryInfo repository, string branch, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            ValidateRemote(repository.RemoteUrl, _allowLocalRemotes);
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _git.RunAsync(null, ["check-ref-format", "refs/heads/" + branch], cancellationToken).ConfigureAwait(false);
                progress?.Report(new("Connecting", "Refreshing the remote branch catalog"));
                var discovery = await DiscoverAsync(repository.RemoteUrl, cancellationToken).ConfigureAwait(false);
                _store.SaveRepository(repository with { DefaultBranch = discovery.DefaultBranch, Branches = discovery.Branches }, cancellationToken);
                var gitDirectory = CachePath(repository.Id);
                if (!File.Exists(Path.Combine(gitDirectory, "HEAD")))
                {
                    progress?.Report(new("Preparing", "Creating the local bare repository cache"));
                    await _git.RunAsync(null, ["init", "--bare", "--object-format=" + discovery.ObjectFormat, "--", gitDirectory], cancellationToken).ConfigureAwait(false);
                }
                await _git.RunAsync(gitDirectory, ["config", "remote.origin.url", repository.RemoteUrl], cancellationToken).ConfigureAwait(false);
                // Keep fetched objects even after a force push, so completed snapshots remain browsable.
                await _git.RunAsync(gitDirectory, ["config", "gc.auto", "0"], cancellationToken).ConfigureAwait(false);
                await _git.RunAsync(gitDirectory, ["config", "gc.pruneExpire", "never"], cancellationToken).ConfigureAwait(false);
                progress?.Report(new("Fetching", $"Downloading full history for {branch}"));
                await _git.RunAsync(gitDirectory,
                    ["fetch", "--no-tags", "--no-recurse-submodules", "--no-write-fetch-head", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"], cancellationToken).ConfigureAwait(false);

                var reference = $"refs/remotes/origin/{branch}";
                var tip = (await _git.RunAsync(gitDirectory, ["rev-parse", "--verify", reference + "^{commit}"], cancellationToken).ConfigureAwait(false)).Trim();
                var unchanged = _store.GetSnapshot(repository.Id, branch, cancellationToken, requiredTip: tip);
                if (unchanged is not null)
                {
                    progress?.Report(new("Saving", "Branch is current; reusing the completed snapshot", unchanged.Commits.Count, unchanged.Commits.Count));
                    unchanged = unchanged with { RefreshedAt = DateTimeOffset.UtcNow };
                    _store.UpdateRefreshTime(unchanged, cancellationToken);
                    SnapshotPublished(_logger, repository.Name, branch, unchanged.Commits.Count, 0, unchanged.Files.Count);
                    return unchanged;
                }
                progress?.Report(new("Indexing", "Reading the branch's first-parent ancestry"));
                var ancestryText = await _git.RunAsync(gitDirectory, ["rev-list", "--first-parent", "--topo-order", tip, "--"], cancellationToken).ConfigureAwait(false);
                var ancestry = ancestryText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                var index = _store.GetIndex(repository.Id, cancellationToken);
                var firstKnown = Array.FindIndex(ancestry, index.ContainsKey);
                var missingCount = firstKnown < 0 ? ancestry.Length : firstKnown;
                var added = new List<CommitBundle>();
                if (missingCount > 0)
                {
                    var arguments = new List<string>
                    {
                        "log", "--first-parent", "--topo-order", "--exclude-first-parent-only", "--diff-merges=first-parent", "--root",
                        "--raw", "-z", "--no-abbrev", "--find-renames", "--no-ext-diff", "--no-textconv", GitOutputParser.LogFormat, tip
                    };
                    if (firstKnown >= 0) arguments.Add("^" + ancestry[firstKnown]);
                    arguments.Add("--");
                    added = await _git.RunAsync(gitDirectory, arguments,
                        (stream, token) => GitOutputParser.ReadLogAsync(stream, progress, missingCount, token), cancellationToken).ConfigureAwait(false);
                    foreach (var bundle in added) index[bundle.Commit.Oid] = bundle;
                }
                var commits = new List<CommitInfo>(ancestry.Length);
                var changes = new List<FileChange>();
                foreach (var oid in ancestry)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!index.TryGetValue(oid, out var bundle)) throw new InvalidDataException("The branch index is incomplete; its previous snapshot was preserved.");
                    commits.Add(bundle.Commit);
                    changes.AddRange(bundle.Changes);
                }
                progress?.Report(new("Indexing", "Reading the selected branch's current files", ancestry.Length, ancestry.Length));
                var files = await _git.RunAsync(gitDirectory, ["ls-tree", "-r", "-z", "--full-tree", tip], GitOutputParser.ReadTreeAsync, cancellationToken).ConfigureAwait(false);
                var snapshot = new BranchSnapshot(repository.Id, branch, tip, DateTimeOffset.UtcNow, commits, changes, files);
                progress?.Report(new("Saving", "Publishing the completed snapshot", ancestry.Length, ancestry.Length));
                _store.PublishSnapshot(snapshot, added, cancellationToken);
                SnapshotPublished(_logger, repository.Name, branch, commits.Count, added.Count, files.Count);
                return snapshot;
            }
            finally { _mutationGate.Release(); }
        }, cancellationToken);

    public async Task<DiffResult> GetDiffAsync(RepositoryInfo repository, FileChange change, CancellationToken cancellationToken)
    {
        var summary = change.Kind == ChangeKind.Renamed ? $"{change.OldPath} → {change.Path}" : change.Path;
        summary += $" · {change.Kind} · {change.Commit.ShortSha}";
        if (change.OldMode == "160000" || change.NewMode == "160000")
            return new([new(null, null, $"Submodule pointer: {change.OldObjectId ?? "(absent)"} → {change.NewObjectId ?? "(absent)"}", DiffLineKind.Notice)], summary);
        if (change.NewObjectId == change.OldObjectId)
            return new([new(null, null, $"Content unchanged. Mode {change.OldMode} → {change.NewMode}.", DiffLineKind.Notice)], summary);
        var directory = CachePath(repository.Id);
        // Empty blob is generated by Git so SHA-256 repositories work too.
        string? empty = null;
        if (change.OldObjectId is null || change.NewObjectId is null)
        {
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { empty = (await _git.RunAsync(directory, ["hash-object", "-w", "--stdin"], cancellationToken).ConfigureAwait(false)).Trim(); }
            finally { _mutationGate.Release(); }
        }
        var oldObject = ValidateObjectId(change.OldObjectId ?? empty!);
        var newObject = ValidateObjectId(change.NewObjectId ?? empty!);
        return await _git.RunAsync(directory,
            ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--no-renames", "--unified=3", "--output-indicator-new=+", "--output-indicator-old=-", "--output-indicator-context= ", oldObject, newObject, "--"],
            (stream, token) => GitOutputParser.ReadPatchAsync(stream, summary, token), cancellationToken).ConfigureAwait(false);
    }

    public Task RemoveAsync(RepositoryInfo repository, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Forget saved data atomically. The app-owned object cache remains reusable on reconnect.
            _store.RemoveRepository(repository.Id, cancellationToken);
        }
        finally { _mutationGate.Release(); }
    }, cancellationToken);

    private async Task<(string DefaultBranch, IReadOnlyList<string> Branches, string ObjectFormat)> DiscoverAsync(string remote, CancellationToken token)
    {
        var text = await _git.RunAsync(null, ["ls-remote", "--symref", "--", remote, "HEAD", "refs/heads/*"], token).ConfigureAwait(false);
        string? defaultBranch = null;
        var branches = new SortedSet<string>(StringComparer.Ordinal);
        string? headObject = null;
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length != 2) continue;
            if (fields[0].StartsWith("ref: refs/heads/", StringComparison.Ordinal) && fields[1] == "HEAD") defaultBranch = fields[0][16..];
            else if (fields[1] == "HEAD") headObject = fields[0];
            else if (fields[1].StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                var branch = fields[1][11..];
                branches.Add(branch);
                refs[branch] = fields[0];
            }
        }
        if (branches.Count == 0) throw new InvalidOperationException("This remote has no branches yet. Push an initial commit before connecting.");
        if (defaultBranch is null || !branches.Contains(defaultBranch))
            defaultBranch = refs.FirstOrDefault(pair => pair.Value == headObject).Key ?? (branches.Contains("main") ? "main" : branches.First());
        return (defaultBranch, branches.ToArray(), refs.Values.First().Length == 64 ? "sha256" : "sha1");
    }

    private string CachePath(string repositoryId)
    {
        if (repositoryId.Length != 24 || !repositoryId.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid repository identifier.", nameof(repositoryId));
        return Path.Combine(_cacheDirectory, repositoryId + ".git");
    }

    internal static string ValidateRemote(string remoteUrl, bool allowLocalRemotes = false)
    {
        var remote = remoteUrl.Trim();
        if (remote.Length == 0 || remote.Any(char.IsControl) || remote.StartsWith('-'))
            throw new ArgumentException("Enter an HTTPS or SSH Git remote URL.", nameof(remoteUrl));
        if (allowLocalRemotes && Path.IsPathFullyQualified(remote) && Directory.Exists(remote)) return Path.GetFullPath(remote);
        if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "ssh")
        {
            if (string.IsNullOrWhiteSpace(uri.Host) || uri.AbsolutePath is "" or "/") throw new ArgumentException("The remote URL must include a host and repository path.", nameof(remoteUrl));
            if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Contains(':') || (uri.Scheme == "https" && uri.UserInfo.Length > 0))
                throw new ArgumentException("Use a remote URL without passwords, tokens, query strings, or fragments. Git Credential Manager or SSH handles authentication.", nameof(remoteUrl));
            return remote;
        }
        if (!remote.Contains("://", StringComparison.Ordinal) && ScpRemote().IsMatch(remote) && !Path.IsPathFullyQualified(remote)) return remote;
        throw new ArgumentException("Use an HTTPS or SSH remote, such as https://github.com/owner/repo.git or git@github.com:owner/repo.git.", nameof(remoteUrl));
    }

    private static string RemoteName(string remote)
    {
        var name = remote.TrimEnd('/', '\\').Split('/', '\\', ':').Last();
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static string ValidateObjectId(string oid) => oid.Length is 40 or 64 && oid.All(Uri.IsHexDigit)
        ? oid : throw new InvalidDataException("Invalid object ID in cached history.");

    [GeneratedRegex(@"^(?:[A-Za-z0-9._-]+@)?[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?:[^\s:][^\r\n]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpRemote();

    [LoggerMessage(Level = LogLevel.Information, Message = "Published {Repository} / {Branch}: {CommitCount} commits ({NewCommitCount} new), {FileCount} files")]
    private static partial void SnapshotPublished(ILogger logger, string repository, string branch, int commitCount, int newCommitCount, int fileCount);

    public void Dispose() => _mutationGate.Dispose();
}
