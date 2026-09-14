# Git engine validation and performance

Measured on September 14, 2026 using the final Release build on this development machine. The benchmark ran with `--no-build` after the final lineage-query changes:

- Windows 11 Pro, build 10.0.26100.
- AMD Ryzen 9 5900X, 12 cores / 24 logical processors; approximately 95.9 GiB usable RAM.
- .NET SDK 10.0.400, .NET runtime 10.0.11, Git 2.45.1.windows.1.
- Microsoft.Data.Sqlite 10.0.12; local app-owned bare object cache and SQLite database.

## Reproducible large-history fixture

The opt-in benchmark uses Git `fast-import` to create **100,000 first-parent commits, 10,000 current files, and 109,999 path changes**. The first commit adds 100 directories with 100 files each. Subsequent commits modify one file at a time. Commits have increasing committer timestamps. The production service discovers the local source, fetches its full history, parses Git's NUL-delimited raw log, and publishes the snapshot.

| Operation | Measured time |
|---|---:|
| Fixture creation with fast-import | 24.783 s |
| Cold local fetch, complete history index, and snapshot publication | 28.970 s |
| Unchanged local fetch and reuse of the completed snapshot | 2.366 s |
| Cached snapshot open using a fresh service instance | 1,850.6 ms |
| Initial All Files query, including construction of its history index | 262.1 ms |
| Subsequent filtering of 10,000 file rows | 2.0 ms |

These are observed values, not guarantees. The cached-read and subsequent-filter targets of two seconds and 200 ms respectively were met in this final run. Initial query-index construction is measured separately and took 262.1 ms, exceeding 200 ms. Machine load and filesystem caching affect results; opening a fresh service does not clear the operating system's filesystem cache.

The source repository is local, so these timings include local Git transfer but **do not represent internet transfer or authentication latency**. This synthetic fixture exercises a long history and many current paths; it is not a claim that repositories with large binary objects, complex merges, or many simultaneous renames will have the same throughput. WPF rendering and application startup are outside this engine benchmark.

An unchanged fetched tip updates only the snapshot's refresh timestamp. It avoids regenerating the tree and rewriting 100,000 snapshot membership records.

Run the benchmark from PowerShell:

```powershell
$env:GITHISTORY_BENCHMARK = '1'
dotnet test tests/GitHistory.Tests/GitHistory.Tests.csproj -c Release --filter Category=Benchmark --logger 'console;verbosity=detailed'
Remove-Item Env:GITHISTORY_BENCHMARK
```

The report is also saved beside the test assembly as `git-performance-results.txt`. The fixture uses a uniquely named temporary directory and removes only that verified directory when finished.

## Production HTTPS smoke test

The production service successfully read `https://github.com/coltonspears/ColtonStack.git`: default branch `main`, tip `50c1c0cf6fcccac6930110b355593c4e8f866554`, seven first-parent commits, and 224 current files. Native diff loading and reopening the completed cache with a fresh service also passed. The test completed in approximately two seconds. This was a public HTTPS repository; private-repository authentication and SSH access were not exercised by this smoke test.

```powershell
$env:GITHISTORY_TEST_REMOTE = 'https://github.com/coltonspears/ColtonStack.git'
dotnet test tests/GitHistory.Tests/GitHistory.Tests.csproj -c Release --filter Category=RemoteSmoke --logger 'console;verbosity=detailed'
Remove-Item Env:GITHISTORY_TEST_REMOTE
```

This test only reads the supplied remote. Git Credential Manager, the user's Git configuration, `core.sshCommand`, `GIT_SSH`, and `GIT_SSH_COMMAND` remain available to the production runner. For SSH, configure a working key or key agent and establish host trust in a terminal first; operations are cancellable and do not collect credentials in app settings.

## Correctness coverage

Twenty-six infrastructure test cases passed using real local Git repositories and focused parser/settings tests. They cover:

- Full first-parent history, merges compared with their first parent, and clock skew without ancestry reordering.
- Rename, add, delete, native diff line numbers, binary files, submodule pointers, and Git LFS pointer content.
- UTF-8, tab, newline, pathspec-looking, and case-distinct paths preserved across Git parsing and SQLite persistence.
- Incremental commit reuse, unchanged refresh, force pushes, and access to previously fetched objects.
- Failed fetches and cancellation preserving the previous completed snapshot and its refresh timestamp.
- Remote URL validation, branch catalog refresh, SHA-256 object repositories, removal/reconnection, and settings recovery.
- Bounded patch capture at 2 MiB or 20,000 display lines with an explicit truncation notice.
- Custom Git output-format settings without corrupting UTF-8 metadata or blank-context line numbers.

The large-history benchmark and network smoke test are opt-in and are skipped by the normal test run.
