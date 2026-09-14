# Delivery verification

Verified on September 14, 2026 on the Windows development machine described in [performance.md](performance.md).

| Check | Result |
|---|---|
| Release regression suite | 76 passed; two opt-in tests skipped; no failures |
| WPF compilation | Successful with DevExpress DX1000/DX1001 evaluation warnings |
| Windows x64 self-contained publish | Successful; the published executable completed the UI smoke checks and shut down cleanly |
| Actual DevExpress views | Recent Changes, All Files, native diff, light/dark themes, connect form, and command palette rendered |
| Grid behavior | Bound rows populated; path sorting verified against expected order; Reset Layout restored the column preference |
| Docking layout | Changed history-pane width, serialized the layout, changed it again, and restored the saved width successfully |
| Persistence | Docking and file-grid XML files saved on shutdown; repository/branch/theme/view-mode persistence and interrupted initialization covered by view-model tests |
| Shortcut bindings | Ctrl+D switched themes, Ctrl+K opened the palette, and Escape dismissed it through the window's resolved key bindings |
| Binding diagnostics | Zero binding warnings/errors in the published-app smoke run |
| Runtime diagnostics | Clean start and shutdown with no application warnings or fatal errors in that run |
| Rendering density | Actual WPF renders at 100%, 150%, and 200%; screenshots visually inspected |
| Public HTTPS repository | Production service fetched ColtonStack, loaded a native diff, and reopened its completed cache |
| Large-history performance | Reproducible 100,000-commit / 10,000-file benchmark; measured separately from internet fetch latency |

The normal regression suite includes real Git fixture repositories and WPF-independent tests for queries, view models, cancellation, stale results, offline recovery, and architecture boundaries. The benchmark and network smoke test are opt-in; see [their commands and results](performance.md).

The screenshot smoke command uses an isolated temporary data directory and renders actual controls. The committed [screenshots](screenshots/) came from the self-contained Release executable, not a design mockup. Local output, reports, diagnostics, and published binaries are under the ignored `artifacts` directory.

```powershell
dotnet test GitHistory.slnx -c Release
dotnet publish src/GitHistory.App -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
& ./artifacts/publish/win-x64/GitHistory.App.exe --capture-screenshots artifacts/published-ui
```

Physical monitor transitions, interactive drag-and-drop docking, a complete keyboard-only walkthrough, and private HTTPS/SSH authentication still need manual verification in the user's environment. Programmatic shortcut and layout checks do not replace those interactions. The self-contained executable was tested on the development machine, not a clean Windows VM.

No local DevExpress v26.1 key was registered during verification. Its evaluation diagnostics remain enabled. Register the user's trial or developer key as described in the [README](../README.md#run-locally); keys and published binaries are excluded from Git.

The official `dx-wpf@DevExpress-agent-skills` Codex plugin was installed and enabled (version 1.5.0, 14 WPF skills). Its bundled `dxdocs` MCP endpoint successfully handled initialization, tool discovery, a WPF documentation search, and a documentation-content lookup. A fresh Codex session may be needed to expose newly installed tool names.
