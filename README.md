# GitHistory

**Follow the change.** A native Windows workspace for discovering which files changed in a remote Git repository—and understanding the change without leaving the app.

Built with **.NET 10**, **native WPF**, and **CommunityToolkit.Mvvm**. The shared **GitHistory.UI** control library needs no commercial UI license. Supports HTTPS and SSH remotes from GitHub, GitLab, Azure DevOps, and other Git servers.

![Recent changes in the dark workspace](docs/screenshots/dark.png)

## Explore a repository

1. Launch the app. The clearly labeled **GitHistory Showcase** demo workspace lets you explore the interface without a network connection.
2. Choose **Connect repository** and paste an HTTPS or SSH remote URL. Git uses your existing credential helper or SSH configuration.
3. Pick a branch. GitHistory downloads its complete history into an app-owned bare cache, without checking out source files.
4. Use **Recent Changes** for a date range or **All Files** for the current tree ranked by its last branch change. Search paths, commit subjects, authors, and hashes; filter by author or folder.
5. Select a file, choose a history entry, and read its native inline diff. Copy the path or commit SHA when needed.

Click a day in the activity chart or drag across days to narrow the date window. Arrow keys select days; Shift+arrows extend the range. The file grid supports column sizing, reordering, and sorting, with a dedicated **Folder** column and full-path tooltips. Right-click a column heading to group results or choose visible columns. Search, author, folder, and date filters narrow the results.

Choose **Balanced**, **Side by side**, or **Focus diff** from the toolbar layout picker. Resize the panes with their dividers; each layout remembers its own sizes. Drag the sidebar's right edge to widen it, or use its arrow button / **Ctrl+B** to collapse and expand it. Theme, repository, branch, view mode, layout, sidebar width, and grid preferences survive normal shutdown; **Reset layout** restores the workspace defaults.

![All current files and their history](docs/screenshots/all-files.png)

## Workspaces, private repositories, and local tools

- Search workspaces by name or remote URL. Pin favorites and switch **Pinned** on for a compact list. The active workspace remains loaded when a search hides its navigation row.
- Search the folder tree without losing the active folder filter. Matching folders retain their ancestors; **Clear filters** resets the folder, author, and file search together.
- Choose **Import repositories** to discover GitHub or Azure DevOps repositories available to your account, including private repositories. Search the results, select several, and import them together. Successful imports remain saved if another repository fails or you cancel. Importing keeps the active workspace selected.
- Use **Settings** to choose **Dark**, **Light**, **Classic** (the original charcoal/teal colors), or **Dusk** (aubergine/lavender). Settings also controls the default date range, refresh on open, activity summary, automatic PR lookups, and Cursor's executable. Settings, pinned workspaces, and linked checkout folders persist on normal shutdown.

Theme previews: [Classic](docs/screenshots/classic.png) · [Dusk](docs/screenshots/dusk.png). Layout previews: [Side by side](docs/screenshots/layout-side-by-side.png) · [Focus diff](docs/screenshots/layout-focus-diff.png) · [Collapsed sidebar](docs/screenshots/sidebar-collapsed.png).

![Settings](docs/screenshots/settings.png)

GitHub discovery uses an existing `gh auth login` session; Azure DevOps discovery uses `az login` and an organization name or URL. An optional API token stays in process memory. Git connections and fetches use your existing Git Credential Manager or SSH credentials separately. See [private-repository setup and provider support](docs/providers.md).

![Import repositories](docs/screenshots/import.png)

Right-click a file or history entry for contextual actions:

| Action | Behavior |
|---|---|
| Open in Explorer | Selects the file in a linked local checkout; deleted files open the nearest existing parent folder |
| Open in Cursor | Opens the linked checkout and the selected file, when present locally |
| Copy path | Copies the exact repository-relative path |
| View file / commit in browser | Opens the corresponding GitHub or Azure DevOps revision; deleted files use the parent revision |
| Find associated pull requests | Loads provider PR titles, authors, state, and links for the selected commit |

Use **Link local checkout** to choose an existing working repository. GitHistory's bare history cache contains no working files and is not opened as a checkout. The local checkout may be on a different branch or contain edits; Explorer and Cursor open its existing contents. GitHistory does not modify or synchronize it. Repository-level actions also open the source control page, copy its remote URL, or open the linked folder.

PR references inferred from merge commit messages are labeled **unverified** until metadata is loaded. Network, indexing, filtering, diff, import, and PR operations show loading state in their affected panes; cancellation remains available. The command palette includes import, settings, pinned workspace, browser, and local-tool commands.

## Run locally

Requirements:

- Windows with the **.NET 10 SDK** for development.
- **Git for Windows** on `PATH`. HTTPS authentication uses your configured Git Credential Manager; SSH uses your installed client, agent, keys, and host trust.

```powershell
dotnet restore GitHistory.slnx
dotnet build GitHistory.slnx
dotnet run --project src/GitHistory.App
```

All packages restore from **https://api.nuget.org/v3/index.json**. No vendor feed, license key, or UI subscription is required.

### Publish for local evaluation

```powershell
dotnet publish src/GitHistory.App -c Release -r win-x64 --self-contained true -o artifacts/publish/native-win-x64
```

Run `artifacts/publish/native-win-x64/GitHistory.App.exe`. The self-contained output includes .NET; Git still needs to be installed. Published binaries remain outside Git.

## What “last changed” means

GitHistory answers **when the selected branch changed**. It walks that branch's first-parent history and compares each commit against its first parent. If a change was authored last week and merged into `main` today, the `main` row shows today's merge. Author details remain visible separately.

- Latest touch is selected by **ancestry order**, so skewed commit clocks cannot incorrectly replace a newer change with an older ancestor. Rows are then sorted by the selected touch's committer timestamp.
- Dates are stored in UTC and displayed in local time. Presets are rolling periods; custom dates include the selected end day.
- Recent Changes includes touched paths, including deletions and previous historical paths. It represents activity, not a net diff between two dates.
- In Recent Changes, each row shows its newest matching touch and counts matching touches. In All Files, search and author filters apply to each current file's latest touch.
- All Files includes only entries present at the selected branch tip. A deleted file is absent; its deletion remains visible in Recent Changes.
- Rename tracking uses Git's heuristic detection. History follows detected old paths; a deletion followed by a fresh addition starts a new lineage.
- Selecting an older filtered touch opens its complete lineage, including newer changes, with the clicked touch selected initially.
- Binary files receive a summary. Submodules show their commit pointers. Git LFS entries remain pointers; the app does not download external LFS content.
- Displayed text diffs are bounded to 2 MiB and 20,000 lines, with a visible truncation notice.
- Hover over a diff line to read and copy its full text in a scrollable tooltip, including lines wider than the grid.

Refresh runs when opening/selecting a repository or branch, or when requested manually. Disable **Refresh on open** in Settings to prefer a completed cache; a branch without a cache still fetches once. Cached results remain available if a fetch or index fails or is canceled. A refresh only replaces the completed snapshot after indexing succeeds. Force pushes rebuild the derived snapshot against the new branch tip. Previously cached branches remain selectable after remote deletion; selecting one shows the retained history and an explicit refresh failure.

## Controls and architecture

![Light theme](docs/screenshots/light.png)

| Area | Native controls |
|---|---|
| Shell and repository navigation | Window, reusable expandable Sidebar, styled ListBox |
| File browser and folder hierarchy | Virtualized DataGrid, TreeView |
| Activity and date selection | Reusable ActivityChart with mouse and keyboard range selection, DatePicker |
| Resizable details | Reusable WorkspaceLayout and Pane, native GridSplitter, TabControl |
| Native diff | Read-only DataGrid with semantic line colors and selectable full-line tooltips |
| Editors, progress, and appearance | Reusable SearchBox and BusyIndicator; four shared palettes and native control templates |

### Reuse the UI in another application

Reference `src/GitHistory.UI/GitHistory.UI.csproj` and merge its theme and control dictionaries. It has **no package references or dependencies on the GitHistory domain, services, or application**. `SearchBox`, `Pane`, `BusyIndicator`, `ActivityChart`, `Sidebar`, and `WorkspaceLayout` use dependency properties, commands, and standard WPF binding. Buttons, inputs, navigation, grids, tabs, menus, scrollbars, and focus states share semantic color resources. `ThemeCatalog` supplies theme choices and runtime switching. See the [UI library guide](src/GitHistory.UI/README.md) for setup and examples.

The workspace offers three arrangements with adjustable dividers, retaining the same controls and selections when switching layouts. Native layout/sidebar/column preferences are saved in `layout/workspace-v1.json`; other application settings remain compatible. Free-floating windows and vendor XML docking layouts are not supported.

`GitHistory.Core` contains immutable data models, query logic, application contracts, and sealed view models. `GitHistory.Infrastructure` implements Git execution, SQLite persistence, and source-generated JSON settings. `GitHistory.App` contains XAML, presentation behaviors, desktop services, and the Generic Host composition root. `GitHistory.Tests` exercises logic and real Git fixtures without starting WPF.

View models use public partial `[ObservableProperty]` properties and generated asynchronous `[RelayCommand]` commands. Constructor injection, typed messages, and small services keep control types and side effects out of the core. Views contain no business logic or event handlers. Architecture tests and banned-API analyzers enforce these boundaries, following the relevant [ColtonStack conventions](https://github.com/coltonspears/ColtonStack).

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+K | Command palette |
| Ctrl+F | Focus file search |
| F5 / Ctrl+R | Refresh selected repository |
| Ctrl+D | Cycle Dark, Light, Classic, and Dusk |
| Ctrl+B | Collapse / expand sidebar |
| Escape | Close the active overlay |

![Command palette](docs/screenshots/command-palette.png)

## Data and diagnostics

Application data lives under `%LOCALAPPDATA%\GitHistory`: bare repository caches, SQLite history, settings, saved layouts, and local diagnostic logs. The app does not store authentication secrets or change global Git configuration. Removing a repository forgets saved metadata/history while retaining its reusable local object cache.

Git is invoked directly using structured process arguments. Paths are parsed as NUL-delimited records and remain case-sensitive, including on Windows. External diff tools and text-conversion programs are disabled. Cached snapshots are published atomically, and canceled processes are terminated with their process trees.

There is no background polling, telemetry, source editing, checkout, commit, push, or branch-comparison workflow. Local-tool actions open an existing checkout in the user's chosen application.

## Tests and screenshots

```powershell
dotnet test tests/GitHistory.Tests -c Release
dotnet run --project src/GitHistory.App -- --capture-screenshots artifacts/ui-verification
```

The opt-in screenshot command starts an isolated demo workspace and renders the **actual WPF controls**, including all four themes, layout presets, sidebar states, file views, and overlays. It also records binding diagnostics, checks caret alignment and row focus styling, and renders at 100%, 150%, and 200% density. This does not replace testing physical transitions between monitors with different scaling.

Tests cover first-parent merge attribution, clock skew, rename and delete/re-add history, unusual filenames, binary/submodule/LFS entries, force pushes, cancellation, cache recovery, query behavior, stale UI results, and architecture boundaries. See [performance measurements](docs/performance.md) for the reproducible 100,000-commit benchmark.

See the [workspace update notes](docs/navigation-update.md) for the selection fix and new features, and the [delivery verification notes](docs/validation.md) for the tested publish, screenshots, checks, and remaining manual validation.

GitHub Actions runs Windows restore, tests, and build without a UI license secret.
