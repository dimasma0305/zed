# Zyber

Zyber is the C#/.NET 10 + Avalonia rewrite of this Zed fork. The target is the
same appearance and retained functionality, with the application named Zyber.
**This is an early, incomplete implementation, not a feature-equivalent Zed
replacement.** See [FEATURE_PARITY.md](FEATURE_PARITY.md) for the remaining work.

## Run

Install the .NET 10 SDK specified in `global.json`, then from this directory:

```sh
dotnet restore Zyber.slnx --locked-mode
dotnet run --project src/Zyber -- /path/to/project /path/to/file.cs
```

The published Windows build is self-contained. Extract the entire archive and
open `Zyber.exe`; a separate .NET installation is unnecessary. Keep its DLLs,
fonts, and native libraries together. The Windows CI job also builds a per-user
installer with Start menu shortcuts and an uninstaller. It installs as Zyber
and does not modify the Zed installation or its files.

## Current functionality

- Open a folder, browse its tree, and open files in tabs. File watching refreshes
  the tree and clean buffers; conflicting unsaved edits and deleted-file buffers
  are preserved. Expanded folders remain expanded during refresh.
- Find project files with fuzzy matching and optional `:line:column` locations.
  Git projects respect Git ignore rules while retaining tracked ignored files.
- Search project text with case, whole-word, and regex options. Results open at
  their locations; skipped files, cancellation, and search limits are reported.
- Navigate nested document symbols with **Ctrl+Shift+B**. The outline filters by
  name, follows the caret, updates breadcrumbs, preserves collapsed nodes on
  refresh, pins a buffer across tab switches, and rejects stale navigation after edits. JSON/JSONC properties work
  locally; other code uses language-server document symbols when available.
- Show Git changes and the latest 100 commits, stage/unstage files, inspect
  read-only diffs, commit, fetch, and push with installed Git. Repository
  changes refresh automatically; Ctrl+Shift+G toggles the Git panel.
- Clone repositories with an installed Git, reopen persisted recent projects,
  and navigate back/forward between files.
- Edit text with selection, undo/redo, indentation, line numbers, syntax
  highlighting, word wrap, and find/replace through AvaloniaEdit.
- Preserve separate undo histories and cursor positions when switching tabs.
- Connect installed stdio language servers for automatic/manual plain-text
  completion, diagnostic underlines and a diagnostics tab, hover, definitions,
  references, and formatting. Completion and formatting participate in undo;
  stale responses are guarded against overwriting newer edits.
- Save and Save As; confirm before discarding changes or replacing a file.
- Detect on-disk changes before saving; refuse conflicting writes.
- Preserve UTF-8/UTF-16 byte order marks and existing line endings.
- Command palette, integrated terminal tabs, and external terminal launcher.
  The terminal uses ConPTY on Windows and PTY on Linux, with PowerShell/Bash
  defaults, ANSI colors from the source theme, and persistent sessions while the
  dock is hidden. Basic Linux PTY execution is tested; Windows execution is
  configured in CI but has not yet been verified in this workspace.
- Load the fork's One, Ayu, and Gruvbox theme JSON directly as build resources,
  plus custom Zed JSON theme families and locally installed extension themes.
  UI surfaces, editor, selection, caret, search, and mapped syntax categories use
  source theme values. Zed's RGBA alpha values are preserved.
- Bundle Zed's Lilex and IBM Plex Sans fonts. Default code size is 15, UI size is
  16, and code line spacing is 1.618. Tabs are 32 pixels and the default project
  panel is 240 pixels on the right. The screenshot reference guides the 300-pixel
  threads area, compact title bar, editor toolbar, and resizable bottom dock.
  The threads area has no AI implementation yet. Exact pixel parity is unverified.

Press **Ctrl+Shift+P → Theme selector: Toggle** to search themes. Arrow keys
preview the palette across the editor, panels, and terminal; Enter saves it,
and Escape restores the previous selection. **Theme: Import Zed theme…** copies
a validated JSON family into Zyber's themes directory without overwriting files.

Theme selection reads `Zyber/settings.json` first, then `Zed/settings.json`,
under `%APPDATA%` on Windows. On Linux it reads `zyber/settings.json` then
`zed/settings.json` under `$XDG_CONFIG_HOME` (or `~/.config`). String names and
Zed's mode/light/dark object are accepted, including JSON comments and trailing
commas. Defaults follow the OS with One Light/One Dark. Selecting a theme saves
only the `theme` value in Zyber's settings, preserving comments and other values;
Zed's files are read only. Invalid settings are left untouched.

Theme discovery includes both applications' configuration `themes/` directories
and data `extensions/installed/*/themes/` directories. Windows data lives under
`%LOCALAPPDATA%`; Linux data uses `$XDG_DATA_HOME` (default `~/.local/share`).
Zyber's user themes take precedence over Zed's user themes, then extensions,
then bundled themes. Partial themes inherit colors extracted from the pinned
Zed source; RGB/RGBA hex forms, syntax backgrounds, italic/oblique text and font
weights are supported for the editor's mapped syntax categories. Malformed
families are reported without preventing other themes from loading.

Theme files are limited to 8 MiB and 512 variants; scans are limited to 2000
JSON files per root and eight directory levels. This is theme-file support:
marketplace installation, icon-theme packages, theme setting overrides, window
transparency/blur, and exact rendering of every Zed control remain unfinished.

There is no Zed account or hosted-service connection. Local/API-key AI is part
of the compatibility target but has **not yet been implemented**.

The outline currently covers one active or pinned buffer, with a 5000-symbol limit and
128 nesting levels. JSON syntax errors are shown in the panel. Workspace-wide
outlines, Tree-sitter outlines for other languages, and exact
Zed filtering/ranking remain pending.

## Language servers

Servers are currently installed separately. Zyber discovers `pyright-langserver`
for Python, `typescript-language-server` for TypeScript/JavaScript, `rust-analyzer`,
`csharp-ls`, `gopls`, and `clangd` on `PATH`. Only Pyright and the TypeScript server
have real-server integration coverage so far; discovery does not establish
compatibility with every version of the other servers.

For Python, install Node.js and run `npm install -g pyright`. For JavaScript and
TypeScript, run `npm install -g typescript-language-server typescript` (current
server versions require a recent Node runtime). Servers are separate processes;
Zyber's core application remains C#/.NET and does not require Node to start.
Use **Language Server: Restart** in the command palette after installing a server
or changing its configuration.

The `lsp` section in **Zyber's** settings file supports executable paths,
argument arrays, and server settings, for example:

```json
{
  "theme": "One Dark",
  "lsp": {
    "pyright": {
      "binary": { "path": "pyright-langserver", "arguments": ["--stdio"] },
      "settings": { "python": { "analysis": { "typeCheckingMode": "basic" } } }
    }
  }
}
```

Executables may be absolute paths or commands on `PATH`. Known npm shims on
Windows are launched through Node directly. Other shell scripts should be
configured as an executable interpreter plus script arguments. Configuration is
read from the user's settings, not an executable command embedded in a project.
The status-bar diagnostic counter opens diagnostics; its tooltip shows server
status. Hover documentation currently displays plain text, including any Markdown
markup supplied by a server.

Remaining language work includes automatic server installation, language
extensions, snippets, completion-item resolution/commands, signature help,
semantic tokens, code actions, rename/workspace edits, pull diagnostics, and
configuration/protocol behavior beyond the supported subset. Diagnostics are
currently tracked for open buffers. This is not complete Zed language parity.

## Shortcuts

| Action | Windows/Linux |
| --- | --- |
| New file | Ctrl+N |
| Open file | Ctrl+O |
| Open folder | Ctrl+Shift+O or Ctrl+K, Ctrl+O |
| Find project file | Ctrl+P / Ctrl+E |
| Search project | Ctrl+Shift+F |
| Previous / next tab | Ctrl+PageUp / Ctrl+PageDown |
| Toggle integrated terminal | Ctrl+` |
| Save / Save As | Ctrl+S / Ctrl+Shift+S |
| Close tab | Ctrl+W |
| Command palette | Ctrl+Shift+P |
| Find | Ctrl+F |
| Show completions | Ctrl+Space |
| Go to definition | F12 |
| Find references | Shift+Alt+F12 |
| Hover | Ctrl+K, Ctrl+I |
| Format document | Shift+Alt+F |
| Project diagnostics | Ctrl+Shift+M |
| Undo / Redo | Ctrl+Z / Ctrl+Y |

These initial shortcuts are not the complete Zed keymap. Text files currently
have a 16 MiB limit. Binary data and unsupported encodings are rejected without
modification. Clean-buffer reloads reset their undo history. Search currently
limits results to 2,000 and indexing to 100,000 files, with visible notices.
Non-Git folders use a filesystem walk; Git ignore rules require installed Git.
Symlink entries are excluded from
folder browsing; directly opened symlink files resolve to their target.
Conflict checking uses fingerprints before replacement, but does not establish
a cross-process filesystem transaction against every possible concurrent writer.

## Build and verify

```sh
dotnet test Zyber.slnx -c Release --no-restore
dotnet publish src/Zyber -c Release -r win-x64 --self-contained true --no-restore -o artifacts/win-x64
python scripts/package-notices.py artifacts/win-x64
```

The solution also supports `linux-x64` publishing. Dependency versions and
NuGet lockfiles are committed. The `Zyber desktop` workflow runs tests on Windows
and Linux, publishes both builds, opens the actual Windows executable, builds
an installer, and launches the installed application before uploading artifacts.
It does not publish incomplete builds as stable GitHub releases.

Real-server tests use Pyright 1.1.414, typescript-language-server 6.0.3, and
TypeScript 5.9.3. CI installs these test dependencies and runs them on both OSes.
For local integration tests, set `ZYBER_TEST_PYRIGHT` to Pyright's
`langserver.index.js` and `ZYBER_TEST_TYPESCRIPT` to the TypeScript server's
`lib/cli.mjs`; Node must be on `PATH`. Without those variables, the corresponding
integration tests report explicit skips.

Tests cover editor input, tab switching, undo history, keyboard saving, unsaved
work protection, encoding preservation, external-change conflicts, size limits,
source theme color/alpha mapping, fuzzy file picking, Git ignore behavior,
project search, real PTY execution, file watching, recent history, and local
repository cloning, protocol framing/cancellation/disconnection, UTF-16 and mixed
line-ending positions, real language diagnostics/navigation/completion, and
formatting/restart with undo. Native screenshots are still required to
establish full visual parity; using the same color tokens alone is insufficient.

## License

GPL-3.0-or-later; see [LICENSE](LICENSE). Reused fonts, themes, and dependencies
retain their licenses. `scripts/package-notices.py` bundles available package
license notices and metadata alongside the application. The implementation uses
AvaloniaEdit; it is not a mechanical translation of Zed's Rust editor engine.
