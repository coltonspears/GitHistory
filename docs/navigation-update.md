# Workspace and productivity update

The workspace-switch regression came from two-way control selection reacting to repository-list replacement during refresh. That control callback could start loading another workspace before the original refresh finished publishing its catalog.

The fix guards the whole catalog update, retains equivalent repository object identities, and preserves the accepted selection by repository ID. Navigation now invokes commands only for deliberate mouse or keyboard selection. Replacing or filtering the visible items does not switch repositories. Delayed catalog reloads also check whether the user selected another workspace while the request was running.

Regression coverage includes simulated old/null selections during catalog replacement, identity stability, search and pin filters that hide the active workspace, delayed import completion, and refresh-on-open settings. WPF smoke checks apply these filters to the actual DevExpress navigator, check the restored highlight, and verify routed mouse/keyboard selection without navigation from programmatic changes. Clearing editor values is also covered by a view-model regression test.

The update also adds:

- Searchable, pinnable workspaces and a searchable folder tree that retains ancestor context.
- Explicit containing folders, centered last-change labels, and primary-button text colors controlled in every visual state.
- Loading indicators for queries, Git operations, native diffs, provider discovery, imports, local-tool actions, startup, and PR metadata.
- Settings for appearance, default date range, cached opening, activity visibility, PR lookups, and Cursor.
- Read-only GitHub/Azure DevOps import and associated PR information, with session-only API credentials.
- Right-click file and commit actions, safe links to provider pages, and optional local checkout links for Explorer/Cursor.

The file-grid layout format advances to version 2 so existing installations receive the new Folder column. Docking preferences remain compatible. Use Reset Layout to return all panes and file columns to defaults.

See [provider setup](providers.md), the [README](../README.md), and [delivery verification](validation.md) for usage and validation limits.
