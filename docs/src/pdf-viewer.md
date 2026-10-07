---
title: PDF Viewer
description: Open local and SSH project PDFs in Zed tabs and navigate their pages.
---

# PDF Viewer

Open a `.pdf` file from the Project Panel, the file finder, the file open
dialog, or the command line. Zed displays it in a read-only tab. You can split
the tab and view different pages side by side. Zed restores the file and page
number when you reopen a project.

## Navigation

Use the arrows above the page to move between pages. Click the page number,
enter a number from 1 to the page count, and press `Enter`. Press `Escape` to
cancel. You can also use these commands while the PDF tab has focus:

| Command                            | Keybinding                     |
| ---------------------------------- | ------------------------------ |
| {#action pdf_viewer::PreviousPage} | {#kb pdf_viewer::PreviousPage} |
| {#action pdf_viewer::NextPage}     | {#kb pdf_viewer::NextPage}     |
| {#action pdf_viewer::FirstPage}    | {#kb pdf_viewer::FirstPage}    |
| {#action pdf_viewer::LastPage}     | {#kb pdf_viewer::LastPage}     |
| {#action pdf_viewer::GoToPage}     | {#kb pdf_viewer::GoToPage}     |

## Zoom and reload

Hold `Ctrl` and use the mouse wheel or trackpad to zoom around the pointer,
using the same sensitivity as Zed's image viewer. On macOS, `Cmd` also works.
Native trackpad pinch gestures zoom around the gesture center when the
platform and hardware report them to Zed. Ordinary wheel and two-finger scroll
move around the current page without changing the page number. Drag with the
left or middle mouse button to pan, or use the scrollbars.

The minus and plus buttons change zoom in 10% steps. Click the zoom percentage
to enter a value from 10% to 800%, and press `Enter` to apply or `Escape` to
cancel. Right-click the percentage or use **Reset Zoom** to return to 100%.
**Fit Page** fits the current page in the available space and adjusts when you
resize the pane. Zooming manually leaves Fit Page mode.

The point under the pointer stays fixed while zooming, except where the page
fits in the pane or reaches its scroll boundary. PDF panning stays within the
page bounds. During a gesture, Zed scales the current bitmap immediately and
renders a sharper replacement after the gesture settles.

| Command                         | Keybinding                  |
| ------------------------------- | --------------------------- |
| {#action pdf_viewer::ZoomIn}    | {#kb pdf_viewer::ZoomIn}    |
| {#action pdf_viewer::ZoomOut}   | {#kb pdf_viewer::ZoomOut}   |
| {#action pdf_viewer::ResetZoom} | {#kb pdf_viewer::ResetZoom} |
| {#action pdf_viewer::FitToPage} | {#kb pdf_viewer::FitToPage} |
| {#action pdf_viewer::Reload}    | {#kb pdf_viewer::Reload}    |

Changes on disk reload the PDF. The tab follows file renames in the project.
You can also click **Reload** to retry after a loading or rendering error.

## SSH projects

Open a PDF in an already connected SSH project to view it in the same native
tab. File bytes travel through Zed's existing authenticated project connection
and are rendered on your computer. No additional login, public upload, or local
download cache is used. Closing or reloading the tab releases a pending read's
partial bytes; a disconnect releases the document and page image. Reconnect
the project and click **Reload** to retry. Page and zoom remain selected when
reloading a document.

Both the server and client enforce the 128 MiB limit. At most four in-memory
file transfers can run at once, and a PDF load times out after 120 seconds.
Symlink targets must remain inside the remote worktree. This fork requires its
matching remote server bundle; shared collaboration projects are not supported
by the PDF byte transfer. A cancelled read can leave already queued bounded
transport messages in flight, which the client discards.

## Supported files and limits

The viewer uses [Hayro](https://github.com/LaurenzV/hayro), a PDF renderer in
Rust, on Windows, macOS, and Linux. It displays text, vector graphics, and
embedded images, including cropped and rotated pages. Some PDF features and
fonts have rendering limitations. Password-protected and encrypted documents
are unsupported. The viewer does not yet offer text selection, search, form
editing, printing, or annotations editing.

Only the current page is rendered. Files are limited to 128 MiB and 10,000
pages. Page bitmaps are limited to 16 megapixels and 8192 pixels per dimension;
large pages use a lower rendering resolution. Zoom ranges from 10% to 800%.
Page dimensions above 1,000,000 points are rejected before reaching the UI.
A render that takes longer than 30 seconds stops with an error.

PDF parsing and rendering run in a separate process. Changing pages, reloading,
or closing a tab cancels its pending render. The viewer does not execute PDF
JavaScript, launch links, extract attachments, or fetch resources from the
network. The helper has the user's process permissions. Process isolation
contains renderer crashes; it does not enforce an operating-system sandbox or
a total process memory limit.

## Build this fork

Follow [Building Zed for Windows](./development/windows.md),
[macOS](./development/macos.md), or [Linux](./development/linux.md).
The repository pins Rust 1.98.1. On Windows, the C++ build tools, Windows SDK
26100, CMake, and Spectre-mitigated libraries listed in the Windows guide are
required.

From this fork's `main` or `feature/pdf-remote-files` branch:

```sh
cargo test --locked -p pdf_renderer
cargo test --locked -p pdf_viewer
cargo test --locked -p project bounded_file::tests
cargo test --locked -p remote_server test_remote_pdf
cargo run --locked -p zed -- crates/pdf_renderer/tests/fixtures/two-pages.pdf
```

The PDF helper is part of the Zed executable, so no extra PDF renderer binary
or runtime installation is required. The `pdf-viewer.yml` workflow checks this
fork on GitHub-hosted Linux and Windows runners. The upstream Zed workflows
restrict their build jobs to upstream repository owners.

Download the complete Windows package from this fork's GitHub Releases page.
Extract every file, including the `remote-servers` folder, and run `zed.exe`.
This is an unsigned development build with fonts, keymaps,
and other assets embedded using Zed's `util/debug-embed` feature. The Windows
renderer uses Zed's release shader compilation path through a package-specific
Cargo profile override, so it does not need the build machine's source files.
The application uses Zed's normal Windows GUI entry point, without opening a
development console window.
Its commit and executable checksum are included. Use current Windows graphics
drivers; see the Windows build guide for graphics troubleshooting.

To produce the same portable Windows development executable locally, generate
the dependency license notices with `script/generate-licenses.ps1`, then run:

```powershell
$env:ZED_RELEASE_CHANNEL = "stable"
$env:RELEASE_CHANNEL = "stable"
$env:ZED_UPDATE_EXPLANATION = "Download updates from https://github.com/dimasma0305/zed/releases."
cargo --config 'profile.dev.package.gpui_windows.debug-assertions=false' --config 'profile.dev.package.zed.debug-assertions=false' --config 'profile.dev.package.remote.debug-assertions=false' build --locked -p zed -p cli --bin zed --bin cli --features util/debug-embed,remote/bundled-remote-server
```

The workflow builds a Linux x86_64 remote server from the same source commit.
For a local package, place the `pdf-zed-linux-remote-server` artifact's contents
in `remote-servers` beside the Windows executable. The client checks the source
commit, package version, release channel, RPC protocol, platform, and archive
checksum before using it. A missing or mismatched bundle produces an error
instead of compiling a server or downloading one from upstream.

This package supports remote Linux x86_64 hosts with Ubuntu 24.04-compatible
runtime libraries. Other remote architectures and operating systems need a
matching server built and packaged for that platform. The Windows and Linux
artifacts must come from the same workflow run; the Windows artifact alone is
incomplete for remote development. Zig is not required on the user's PC.

The Windows package uses Zed's stable settings and workspace database. Upstream
automatic updates are disabled through the build's `ZED_UPDATE_EXPLANATION`
override. Download subsequent fork builds from the fork's GitHub Releases page.
Back up an existing installation and its data before replacing its binaries.

To keep its project state and extensions in a separate directory, run:

```powershell
.\zed.exe --user-data-dir "$PWD\pdf-zed-data" "C:\path\to\report.pdf"
```

See [Finding and Navigating](./finding-navigating.md) for file navigation.
