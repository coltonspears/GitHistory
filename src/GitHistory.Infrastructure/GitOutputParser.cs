using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GitHistory.Core.Models;

namespace GitHistory.Infrastructure;

internal static partial class GitOutputParser
{
    internal const int MaximumPatchBytes = 2 * 1024 * 1024;
    internal const int MaximumPatchLines = 20_000;
    internal const string LogFormat = "--format=%x00GHCOMMIT%x00%H%x00%P%x00%an%x00%ae%x00%aI%x00%cI%x00%s%x00";

    public static async Task<List<CommitBundle>> ReadLogAsync(Stream stream, IProgress<OperationProgress>? progress, int expected, CancellationToken token)
    {
        var result = new List<CommitBundle>();
        await using var tokens = GitProcessRunner.ReadNulTokensAsync(stream, token).GetAsyncEnumerator(token);
        CommitBundle? current = null;
        while (await tokens.MoveNextAsync().ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            var field = tokens.Current.TrimStart('\r', '\n');
            if (field.Length == 0) continue;
            if (field == "GHCOMMIT")
            {
                var oid = await RequiredTokenAsync(tokens).ConfigureAwait(false);
                var parents = await RequiredTokenAsync(tokens).ConfigureAwait(false);
                var author = await RequiredTokenAsync(tokens).ConfigureAwait(false);
                var email = await RequiredTokenAsync(tokens).ConfigureAwait(false);
                var authored = await RequiredTokenAsync(tokens).ConfigureAwait(false);
                var committed = await RequiredTokenAsync(tokens).ConfigureAwait(false);
                var subject = await RequiredTokenAsync(tokens).ConfigureAwait(false);
                var parent = parents.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                var commit = new CommitInfo(oid, parent, author, email, DateTimeOffset.Parse(authored, CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(committed, CultureInfo.InvariantCulture), subject);
                current = new(commit, []);
                result.Add(current);
                if (result.Count % 250 == 0) progress?.Report(new("Indexing", $"Read {result.Count:N0} new commits", result.Count, expected));
                continue;
            }
            if (current is null || !field.StartsWith(':')) throw new InvalidDataException("Git returned an unexpected history record.");
            var parts = field[1..].Split(' ');
            if (parts.Length != 5) throw new InvalidDataException("Git returned an invalid raw change record.");
            var firstPath = await RequiredTokenAsync(tokens).ConfigureAwait(false);
            var status = parts[4][0];
            var hasPreviousPath = status is 'R' or 'C';
            var path = hasPreviousPath ? await RequiredTokenAsync(tokens).ConfigureAwait(false) : firstPath;
            var kind = status switch
            {
                'A' => ChangeKind.Added,
                'D' => ChangeKind.Deleted,
                'R' => ChangeKind.Renamed,
                'C' => ChangeKind.Copied,
                'T' => ChangeKind.TypeChanged,
                'M' => ChangeKind.Modified,
                _ => throw new InvalidDataException($"Unsupported Git change status '{status}'.")
            };
            current.Changes.Add(new(current.Commit, path, hasPreviousPath ? firstPath : null, kind,
                NullIfZero(parts[2]), NullIfZero(parts[3]), parts[0], parts[1]));
        }
        return result;
    }

    public static async Task<IReadOnlyList<TreeFile>> ReadTreeAsync(Stream stream, CancellationToken token)
    {
        var files = new List<TreeFile>();
        await foreach (var field in GitProcessRunner.ReadNulTokensAsync(stream, token).ConfigureAwait(false))
        {
            if (field.Length == 0) continue;
            var tab = field.IndexOf('\t');
            if (tab < 0) throw new InvalidDataException("Git returned an invalid tree entry.");
            var metadata = field[..tab].Split(' ');
            if (metadata.Length != 3) throw new InvalidDataException("Git returned invalid tree metadata.");
            files.Add(new(field[(tab + 1)..], metadata[2], metadata[0]));
        }
        return files;
    }

    public static async Task<DiffResult> ReadPatchAsync(Stream stream, string summary, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        var truncated = false;
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            var take = Math.Min(count, MaximumPatchBytes - (int)output.Length);
            if (take > 0) output.Write(buffer, 0, take);
            if (take < count) truncated = true;
        }
        var patch = Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
        return ParsePatch(patch, summary, truncated);
    }

    internal static DiffResult ParsePatch(string patch, string summary, bool truncated = false)
    {
        var lines = new List<DiffLine>();
        var raw = patch.Split('\n');
        var binary = false;
        int? oldLine = null;
        int? newLine = null;
        var length = raw.Length;
        // Never display a partially captured line or an artificial trailing empty line.
        if (length > 0 && (truncated || raw[^1].Length == 0)) length--;
        for (var i = 0; i < length; i++)
        {
            if (lines.Count >= MaximumPatchLines) { truncated = true; break; }
            var line = raw[i].TrimEnd('\r');
            var hunk = HunkHeader().Match(line);
            if (hunk.Success)
            {
                oldLine = int.Parse(hunk.Groups[1].Value, CultureInfo.InvariantCulture);
                newLine = int.Parse(hunk.Groups[2].Value, CultureInfo.InvariantCulture);
                lines.Add(new(null, null, line, DiffLineKind.Header));
            }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch")
            {
                binary = true;
                lines.Add(new(null, null, "Binary content changed. A text diff is unavailable.", DiffLineKind.Notice));
            }
            else if (oldLine.HasValue && line.StartsWith('+')) lines.Add(new(null, newLine++, line[1..], DiffLineKind.Added));
            else if (oldLine.HasValue && line.StartsWith('-')) lines.Add(new(oldLine++, null, line[1..], DiffLineKind.Deleted));
            else if (oldLine.HasValue && line.StartsWith(' ')) lines.Add(new(oldLine++, newLine++, line[1..], DiffLineKind.Context));
            else lines.Add(new(null, null, line, line.StartsWith('\\') ? DiffLineKind.Notice : DiffLineKind.Header));
        }
        if (truncated) lines.Add(new(null, null, "Diff truncated at 2 MiB or 20,000 lines. Use your Git client to inspect the full patch.", DiffLineKind.Notice));
        if (lines.Count == 0) lines.Add(new(null, null, "File content is unchanged; only its path or mode changed.", DiffLineKind.Notice));
        return new(lines, summary, truncated, binary);
    }

    private static async ValueTask<string> RequiredTokenAsync(IAsyncEnumerator<string> tokens) =>
        await tokens.MoveNextAsync().ConfigureAwait(false) ? tokens.Current : throw new InvalidDataException("Git output ended in an incomplete record.");

    private static string? NullIfZero(string oid) => oid.All(character => character == '0') ? null : oid;

    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();
}
