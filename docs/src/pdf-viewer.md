---
title: PDF Viewer
description: Open local PDFs in Zed tabs and navigate their pages.
---

# PDF Viewer

Open a local `.pdf` file from the Project Panel, the file finder, the file open
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

Use the minus and plus buttons to change zoom. Click the zoom percentage to
return to 100%. **Fit Page** fits the current page in the available space and
adjusts when you resize the pane. At larger zoom levels, use the scrollbars,
mouse wheel, or trackpad to move around the page.

| Command                         | Keybinding                  |
| ------------------------------- | --------------------------- |
| {#action pdf_viewer::ZoomIn}    | {#kb pdf_viewer::ZoomIn}    |
| {#action pdf_viewer::ZoomOut}   | {#kb pdf_viewer::ZoomOut}   |
| {#action pdf_viewer::ResetZoom} | {#kb pdf_viewer::ResetZoom} |
| {#action pdf_viewer::FitToPage} | {#kb pdf_viewer::FitToPage} |
| {#action pdf_viewer::Reload}    | {#kb pdf_viewer::Reload}    |

Changes on disk reload the PDF. The tab follows file renames in the project.
You can also click **Reload** to retry after a loading or rendering error.

## Supported files and limits

The viewer uses [Hayro](https://github.com/LaurenzV/hayro), a PDF renderer in
Rust, on Windows, macOS, and Linux. It displays text, vector graphics, and
embedded images, including cropped and rotated pages. Some PDF features and
fonts have rendering limitations. Password-protected and encrypted documents
are unsupported. The viewer does not yet offer text selection, search, form
editing, printing, or annotations editing. Remote project files must be
downloaded and opened locally.

Only the current page is rendered. Files are limited to 128 MiB and 10,000
pages. Page bitmaps are limited to 16 megapixels and 8192 pixels per dimension;
large pages use a lower rendering resolution. Zoom ranges from 10% to 800%.
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
The repository pins Rust 1.98.1. On Windows, the C++ build tools, SDK, CMake,
and Spectre-mitigated libraries listed in the Windows guide are required.

From the `feature/pdf-viewer` branch:

```sh
cargo test --locked -p pdf_renderer
cargo test --locked -p pdf_viewer
cargo run --locked -p zed -- crates/pdf_renderer/tests/fixtures/two-pages.pdf
```

The PDF helper is part of the Zed executable, so no extra PDF renderer binary
or runtime installation is required. The `pdf-viewer.yml` workflow checks this
fork on GitHub-hosted Linux and Windows runners. The upstream Zed workflows
restrict their build jobs to upstream repository owners.

When the Windows checks succeed, the workflow produces a
`pdf-zed-windows-development` artifact. Extract all its files into one folder
and run `zed.exe`. This is an unsigned development build with fonts, keymaps,
and other assets embedded using Zed's `util/debug-embed` feature. Its commit
and executable checksum are included. It requires Windows graphics drivers
that support Vulkan, as described in the Windows build guide.

To keep its project state and extensions in a separate directory, run:

```powershell
.\zed.exe --user-data-dir "$PWD\pdf-zed-data" "C:\path\to\report.pdf"
```

See [Finding and Navigating](./finding-navigating.md) for file navigation.
