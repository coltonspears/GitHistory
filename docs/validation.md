# Delivery verification

The native WPF redesign was verified on September 14, 2026 on the Windows development machine described in [performance.md](performance.md).

| Check | Result |
|---|---|
| Release regression suite | 178 passed; three opt-in tests skipped; no failures |
| WPF compilation | Successful with zero warnings and zero errors |
| Windows x64 self-contained publish | Fresh `artifacts/publish/native-win-x64` output; published executable passed the complete UI smoke run and shut down cleanly |
| Dependency and reuse boundaries | Architecture tests prohibit DevExpress packages in the app and its lock file, prohibit package/project references and Git domain dependencies in `GitHistory.UI`, and keep UI assemblies out of Core |
| Actual native views | Recent Changes, All Files, inline diff, Dark/Light/Classic/Dusk themes, custom date range/calendar, connect form, command palette, settings, repository import, and loading overlays rendered |
| Grid behavior | Invoked the real column header's UI Automation sorting action and checked row order; the sort survived an asynchronous file-source replacement; Reset Layout cleared sorting and grouping |
| Workspace navigation | Search/pin filters preserved the active workspace and restored its highlight; routed mouse/keyboard input activated the intended workspace, while programmatic changes triggered no navigation |
| Folder navigation | Clicked an actual tree row; matching folders retained ancestors and their selection highlight; hiding and restoring results preserved the active folder |
| Context menus | Routed right-clicks selected the pointed file and commit rows; menu command parameters targeted those rows |
| Activity chart | Routed keyboard input filtered the selected day, including after an external selection change; range bindings preserved the exclusive end date |
| Workspace layouts | Selected Balanced, Side by side, and Focus diff through the bound picker; verified pane bounds, splitter keyboard resizing, retained file/commit selections, and each arrangement's remembered proportions |
| Sidebar | Verified collapse/expand through Ctrl+B, resize-grip drag and keyboard input, retained expanded width, and workspace expansion while the sidebar is collapsed |
| Persistence | Round-tripped all arrangement proportions, selected arrangement, sidebar state, and file-column width/order/visibility/grouping/sorting through the production JSON serializer; Reset Layout restored defaults; older native pane sizes migrated to Balanced |
| Deferred column widths | Pixel-to-star restore works after layout settles; a newer restore supersedes an older pending width; selected history subjects stay visible when window resizing coincides with an asynchronous source replacement |
| Shortcut bindings | Ctrl+D cycled themes, Ctrl+B toggled the sidebar, Ctrl+K opened the palette, and Escape dismissed it through resolved window key bindings |
| Input geometry and focus | Measured empty caret, placeholder text, and first typed character in actual WPF SearchBoxes with/without icons and with default/custom padding and fonts; FullRow selection has no cell outline while Cell selection retains its focus cue |
| Visual checks | Folder column visible; last-change cells centered; actual rendered primary-button text meets a 4.5:1 contrast threshold in all four themes; chart colors update with the theme; native calendar text is readable |
| Loading and diagnostics | Actual loading indicator appeared; the completed UI smoke run recorded zero binding warnings/errors |
| Rendering density and sizing | Actual WPF renders at 100%, 150%, and 200%, plus the 1100×720 minimum window; all layouts tested with the widest 420-pixel sidebar and custom dates, retaining a file search at least 200 pixels wide and readable history subjects; screenshots inspected |

The normal regression suite includes real Git fixture repositories and WPF-independent tests for queries, view models, cancellation, stale results, offline recovery, and architecture boundaries. It also covers provider authentication scoping, partial imports, settings/pin persistence, linked-folder path safety, late PR responses, and all four themes' persistence, cycling, and unknown-theme fallback.

The screenshot smoke command uses an isolated temporary data directory and renders the actual controls. It exercises application bindings without sending provider requests or changing the user's saved workspace. The committed [screenshots](screenshots/) came from the self-contained native Release executable. Local output, JSON reports, diagnostics, and binaries are under the ignored `artifacts` directory.

```powershell
dotnet test GitHistory.slnx -c Release
dotnet publish src/GitHistory.App -c Release -r win-x64 --self-contained true -o artifacts/publish/native-win-x64
& ./artifacts/publish/native-win-x64/GitHistory.App.exe --capture-screenshots artifacts/published-workspace-verified
```

## Previous engine and provider verification

These unchanged integration scenarios were verified before the UI migration; the migration's regression suite retains their automated fixture coverage. Live network and benchmark checks were not repeated for a control/template replacement.

| Check | Recorded result |
|---|---|
| Public HTTPS repository | Production service fetched ColtonStack, loaded a native diff, and reopened its completed cache |
| Private HTTPS repository | The same service fetched the private GitHistory remote using existing Git credentials, loaded a diff, and reopened the cache |
| GitHub discovery | Existing CLI sign-in discovered accessible public/private repositories; output recorded counts only |
| Azure DevOps | API fixtures cover discovery, PAT/Entra authentication headers, paging, links, and associated PR queries; live tenant access remains unverified |
| Large-history performance | Reproducible 100,000-commit / 10,000-file benchmark, measured separately from internet fetch latency |

The benchmark, remote smoke test, and provider discovery smoke test remain opt-in; see [engine results](performance.md) and [provider checks](providers.md).

## Manual verification limits

Physical monitor transitions, a complete keyboard-only walkthrough, physical pointer dragging, Explorer/Cursor behavior, Azure tenant sign-in, and SSH authentication still need manual verification in the user's environment. Automated routed input, sorting and layout checks do not replace those interactions. Native panes support three switchable arrangements with adjustable sizes; vendor-specific floating windows, arbitrary docking, and XML layouts are not carried forward. A clean Windows VM installation has not been tested.
