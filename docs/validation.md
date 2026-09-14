# Delivery verification

Verified on September 14, 2026 on the Windows development machine described in [performance.md](performance.md).

| Check | Result |
|---|---|
| Release regression suite | 163 passed; three opt-in tests skipped; no failures |
| WPF compilation | Successful with DevExpress DX1000/DX1001 evaluation warnings |
| Windows x64 self-contained publish | Successful; the published executable completed the UI smoke checks and shut down cleanly |
| Actual DevExpress views | Recent Changes, All Files, native diff, light/dark themes, connect form, command palette, settings, repository import, and loading overlays rendered |
| Grid behavior | Bound rows populated; path sorting verified against expected order; Reset Layout restored the column preference |
| Workspace-switch regression | Actual navigation searches/pin filters preserved the active workspace and restored its highlight; routed mouse and keyboard input selected the intended workspace, while programmatic changes triggered no navigation; unit tests cover old/null selection feedback and delayed catalog updates |
| Folder search | Matching folders retained ancestors and the active folder filter survived hidden results |
| Context menus | A routed right-click selected the pointed DevExpress row; menu bindings targeted that row rather than the previous selection |
| Visual fixes | Folder column visible; last-change cells centered; primary-button normal and keyboard-focus colors remained distinct |
| Docking layout | Changed history-pane width, serialized the layout, changed it again, and restored the saved width successfully |
| Persistence | Docking and file-grid XML files saved on shutdown; repository/branch/theme/view-mode persistence and interrupted initialization covered by view-model tests |
| Shortcut bindings | Ctrl+D switched themes, Ctrl+K opened the palette, and Escape dismissed it through the window's resolved key bindings |
| Binding diagnostics | Zero binding warnings/errors in the published-app smoke run |
| Runtime diagnostics | Clean start and shutdown with no application warnings or fatal errors in that run |
| Rendering density | Actual WPF renders at 100%, 150%, and 200%; screenshots visually inspected |
| Public HTTPS repository | Production service fetched ColtonStack, loaded a native diff, and reopened its completed cache |
| Private HTTPS repository | The same service fetched the new private GitHistory remote using existing Git credentials, loaded a diff, and reopened the cache |
| GitHub provider discovery | Live CLI sign-in discovered accessible public/private repositories; output recorded counts only |
| Azure DevOps provider | API fixtures cover repository discovery, PAT/Entra authentication headers, paging, links, and associated PR queries; live tenant access unverified |
| Large-history performance | Reproducible 100,000-commit / 10,000-file benchmark; measured separately from internet fetch latency |

The normal regression suite includes real Git fixture repositories and WPF-independent tests for queries, view models, cancellation, stale results, offline recovery, and architecture boundaries. It now also covers provider authentication scoping, partial imports, settings/pin persistence, linked-folder path safety, and late PR responses. The benchmark, Git remote smoke test, and provider discovery smoke test are opt-in; see [engine results](performance.md) and [provider checks](providers.md).

The screenshot smoke command uses an isolated temporary data directory and renders actual controls. The committed [screenshots](screenshots/) came from the self-contained Release executable, not a design mockup. Local output, reports, diagnostics, and published binaries are under the ignored `artifacts` directory.

```powershell
dotnet test GitHistory.slnx -c Release
dotnet publish src/GitHistory.App -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
& ./artifacts/publish/win-x64/GitHistory.App.exe --capture-screenshots artifacts/published-ui
```

Physical monitor transitions, interactive drag-and-drop docking, a complete keyboard-only walkthrough, Explorer/Cursor window behavior, Azure tenant sign-in, and SSH authentication still need manual verification in the user's environment. Programmatic shortcut, path, and layout checks do not replace those interactions. The primary-button template explicitly controls hover/pressed label colors, while automated rendering checks normal and keyboard-focus contrast. The self-contained executable was tested on the development machine, not a clean Windows VM.

No local DevExpress v26.1 key was registered during verification. Its evaluation diagnostics remain enabled. Register the user's trial or developer key as described in the [README](../README.md#run-locally); keys and published binaries are excluded from Git.

The official `dx-wpf@DevExpress-agent-skills` Codex plugin was installed and enabled (version 1.5.0, 14 WPF skills). Its bundled `dxdocs` MCP endpoint successfully handled initialization, tool discovery, a WPF documentation search, and a documentation-content lookup. A fresh Codex session may be needed to expose newly installed tool names.
