# Workspace and productivity update

The workspace-switch regression came from two-way control selection reacting to repository-list replacement during refresh. That control callback could start loading another workspace before the original refresh finished publishing its catalog.

The fix guards the whole catalog update, retains equivalent repository object identities, and preserves the accepted selection by repository ID. Navigation now invokes commands only for deliberate mouse or keyboard selection. Replacing or filtering the visible items does not switch repositories. Delayed catalog reloads also check whether the user selected another workspace while the request was running.

Regression coverage includes simulated old/null selections during catalog replacement, identity stability, search and pin filters that hide the active workspace, delayed import completion, and refresh-on-open settings. WPF smoke checks apply these filters to the native WPF workspace list, check the restored highlight, and verify routed mouse/keyboard selection without navigation from programmatic changes. Clearing editor values is also covered by a view-model regression test.

The update also adds:

- Searchable, pinnable workspaces and a searchable folder tree that retains ancestor context.
- Explicit containing folders, centered last-change labels, and primary-button text colors controlled in every visual state.
- Loading indicators for queries, Git operations, native diffs, provider discovery, imports, local-tool actions, startup, and PR metadata.
- Settings for appearance, default date range, cached opening, activity visibility, PR lookups, and Cursor.
- Read-only GitHub/Azure DevOps import and associated PR information, with session-only API credentials.
- Right-click file and commit actions, safe links to provider pages, and optional local checkout links for Explorer/Cursor.

The native WPF redesign offers Balanced, Side by side, and Focus diff layouts with resizable file, history, and diff panes. Each layout retains its own divider proportions without replacing controls or losing the selected file/commit. The sidebar expands, collapses with Ctrl+B, and resizes from its right edge. Layout preferences, sidebar state, column sizes, order, visibility, grouping, and sorting are stored in `layout/workspace-v1.json`. Earlier native pane sizes migrate to Balanced; vendor-specific XML layouts remain untouched. Use Reset Layout to restore all arrangements, the sidebar, and file columns.

Settings offers Dark, Light, Classic (the original charcoal/teal palette), and Dusk; Ctrl+D cycles through them. Full-row selection uses a row focus cue without an overlapping cell outline. Placeholder fields align the hint, empty caret, and first typed character, including with custom padding. Narrow file panes scroll horizontally to preserve legible headings and change badges, and custom dates use their own filter row.

The reusable `GitHistory.UI` project provides themed controls and resources independently of the Git engine, including search inputs, loading indicators, the interactive activity chart, Sidebar, WorkspaceLayout, and ThemeCatalog. Workspace navigation uses a `ListBox`, folders use a hierarchical `TreeView`, and files/history/diffs use native `DataGrid` controls. The activity chart supports keyboard day selection and date range filtering.

See [provider setup](providers.md), the [README](../README.md), and [delivery verification](validation.md) for usage and validation limits.
