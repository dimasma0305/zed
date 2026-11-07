# Zed → Zyber compatibility target

Reference: fork commit `736a40890c15bd16f893c6e67ac7d6dcf2dd2993`.
User requirement: preserve the GUI, color palette, interaction behavior, and
all retained features; change implementation to C# and name to Zyber.
The existing fork's removal of Zed login/hosted services remains intentional.
Local models and API-key providers must remain in the finished product.

This document records implementation status, not permission to drop missing
features. Zyber must not be called a complete Zed replacement until the remaining
items have implementation and verification evidence.

| Area | Current evidence | Remaining parity work |
| --- | --- | --- |
| Theme palette | Bundled and custom/installed-extension Zed JSON families; searchable live preview, saved selection, source-derived partial defaults, syntax style mapping; tests | Marketplace installation, overrides, icon themes, window transparency/blur, every control state and unimplemented surfaces |
| Typography | Original Lilex and IBM Plex Sans fonts; default sizes/line spacing | User font settings, fallback/shaping behavior, platform rasterization comparison |
| Workspace layout | Screenshot-guided sidebars, tabs, breadcrumbs, editor, terminal dock and status bar | Exact window chrome, icons, spacing, split panes, dock persistence, drag/drop, full navigation history, multi-window behavior |
| Welcome/palette | Basic welcome and searchable command picker | Exact component appearance, full command inventory and command fuzzy ranking |
| Text editing | Basic input, selection, indentation, undo, save tested | Zed multi-cursor/selection semantics, anchors, folding, snippets, large files, Unicode edge cases |
| Syntax | AvaloniaEdit definitions with Zed colors | Tree-sitter grammars, queries, injections, semantic tokens and exact classification |
| Outline/navigation | JSON/JSONC and LSP document symbols, hierarchy, filtering, caret following, buffer pinning, breadcrumbs and stale-result protection; native UI and real Pyright checked | Workspace-wide outline, full fuzzy matching, Tree-sitter outlines, navigation context menus and exact control layout |
| Project/filesystem | Local tree, UTF-8/UTF-16, file watching with protected buffers, fuzzy finder and project search tested; Git ignore listing | Full file operations, incremental indexing, search include/exclude filters and replacement, remote files, filesystem edge cases |
| Settings/keymaps | Theme preference compatibility and comment-preserving selection saving, basic shortcuts | Full schema, editor UI, context-sensitive keymaps, Vim/emulation modes, workspace settings |
| Sessions | In-memory tabs and persisted recent projects | Persisted buffers, recoverable unsaved work, full history and settings migration |
| Languages | Stdio LSP lifecycle/restart, plain completion, open-buffer diagnostics/underlines, hover, definition/references, formatting; real Pyright and TypeScript tests | Server installation/toolchains, snippets, completion resolution/commands, signature help, semantic tokens, code actions, rename/workspace edits, pull diagnostics, complete configuration and other servers |
| Git | Cloning, status, staged/unstaged/untracked diffs, staging, commits, latest 100 history entries, fetch/push; real Git and panel tests | Branch management, conflict resolution, blame, full history pagination and hosting-provider integration |
| Terminal/tasks | Integrated terminal tabs and ANSI theme mapping; real Linux PTY command tested | Windows ConPTY execution verification, complete emulator/input behavior, task runner, shell integration and remote terminal launcher |
| Debugging | Not implemented | DAP clients/adapters, breakpoints, stack/variables, evaluation and debugger UI |
| Extensions | Read theme files from installed extensions | WIT/component compatibility, extension installation/lifecycle, icon themes, grammars/language servers, sandbox and permission behavior |
| AI | Not implemented | Local/API-key providers, model selection, streaming/cancellation, agent tools, ACP/MCP and independent prediction providers |
| Viewers | Not implemented | Fork PDF renderer/viewer, binary/hex editor, markdown, images, SVG, tables, Mermaid and other retained previews |
| Remote | Not implemented | Matching remote server, SSH protocol, authentication, file transfers, reconnects and project/terminal parity |
| Accessibility/input | Avalonia primitives only | IME, screen readers, keyboard navigation, high DPI, clipboard and drag/drop comparison |
| Distribution | Portable Windows/Linux builds; Windows installer workflow | Executed Windows installer checks, signed releases if configured, update channel, CLI/PATH and file associations |
| Performance | Small initial application builds; no parity claim | Startup, peak memory, typing latency, scrolling, large projects, GC/frame pacing and repeatable build benchmarks |
| Zed account services | Intentionally absent | Do not reintroduce Zed login, cloud models, hosted predictions or account-dependent collaboration |

## Acceptance gates

1. Freeze reference builds and test projects for the pinned source commit.
2. Compare each screen on the same OS, DPI, theme, font, window size and content.
   Record any rasterization tolerance explicitly; do not hide layout differences.
3. Replay editing actions and compare text, selections, undo results and persisted
   state; include failure/cancellation paths and modified files on disk.
4. Exercise real language servers, extensions, terminals, debuggers, local/API-key
   AI test providers and remote connections.
5. Launch the installed application in CI, not just a helper or headless test.
6. Benchmark against the reference before making performance claims.

## Implementation order

Complete theme/control mapping and the visual comparison harness first. Then
complete core editing, workspace/session/settings behavior and project search.
Prototype extension-component execution and terminal rendering early, before
committing to their dependencies. Port languages, Git, tasks/debugging, AI,
viewers and remote support with feature-level tests. Only then assess a migration
release that can replace the reference editor.

## Current validation checkpoint

The user believes the supplied screenshot uses the default theme. One Dark is
therefore the dark-theme baseline, using the pinned source JSON directly rather
than inferred pixel colors. Linux native rendering is a visual check only; the
reference screenshot came from Windows and its exact theme/DPI are unconfirmed.
Native snapshots include `artifacts/zyber-reference-layout.png`,
`artifacts/zyber-git-changes.png`, `artifacts/zyber-git-history.png`, and
`artifacts/zyber-theme-preview.png`, and `artifacts/zyber-outline.png`.

The automated suite now exercises project file finding/search and preservation
of dirty buffers across watcher updates, alongside the editor/file tests. The
terminal test starts a real shell through the integrated control and verifies a
command's output on disk. These checks do not establish full terminal or editor
compatibility. Windows self-contained publishing is a cross-build; Windows
runtime and installer verification still require execution of the workflow.

The language checkpoint adds real Pyright and TypeScript server integration tests,
including keyboard completion, diagnostics, formatting with undo, and restart.
Transport tests cover UTF-8 frame lengths, fragmented input, cancellation, bad
headers, and disconnection. Native Linux rendering confirmed completion and
underlines; Windows execution remains a pending workflow check. These results
validate the implemented subset, not the remaining language compatibility target.

The theme checkpoint verifies short/full RGBA colors, partial default inheritance,
malformed-family isolation, user-theme precedence, preview/cancellation, and
comment-preserving selection persistence. These checks establish the implemented
theme subset, not exact appearance for all third-party themes. Original SVG icon
geometry is reused for the currently mapped controls, with its license included.

The outline checkpoint checks nested/escaped/Unicode JSON names, UTF-16 ranges,
real Pyright document symbols, filtering, collapse persistence, and stale
navigation after edits and tab changes, including pinned-buffer navigation and closing. A native Linux screenshot confirms the
JSON outline, selected key and breadcrumb. Workspace-wide outlines remain unimplemented.
