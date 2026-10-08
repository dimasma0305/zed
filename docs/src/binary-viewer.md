---
title: Binary Viewer
description: Inspect and overwrite local and SSH file bytes in paged Zed tabs.
---

# Binary Viewer

Open a binary file from the Project Panel, file finder, file open dialog, or
command line. This fork opens files that its text reader identifies as binary in
a viewer with optional byte editing. PDFs and supported images keep their dedicated viewers.
Files above the text editor's 6 GiB limit also open in the paged viewer.

To inspect the saved bytes of any open file, run
{#action binary_viewer::OpenInBinaryViewer} from the command palette. This opens
a separate tab and preserves unsaved edits in the original editor. The viewer
reads the file on disk; unsaved edits are not included.

## Navigate bytes

The viewer loads at most 64 KiB at a time. **Hex + ASCII** shows 16 bytes per
row, with a hexadecimal absolute byte offset and a printable ASCII column.
Nonprintable bytes appear as dots in the ASCII column.

The pane toolbar uses the same breadcrumbs and compact controls as the image
and PDF viewers. Use the arrows to change pages, or the **First Page** and
**Last Page** commands. Click the offset to go to a byte. **Go to Offset** accepts a decimal byte offset or a hexadecimal value starting with
`0x`. Press `Enter` to confirm or `Escape` to cancel. **Reload** rereads the
current page and checks the file size. **First** returns to offset zero if a
file shrinks past the current position.

| Command                               | Keybinding                        |
| ------------------------------------- | --------------------------------- |
| {#action binary_viewer::PreviousPage} | {#kb binary_viewer::PreviousPage} |
| {#action binary_viewer::NextPage}     | {#kb binary_viewer::NextPage}     |
| {#action binary_viewer::FirstPage}    | {#kb binary_viewer::FirstPage}    |
| {#action binary_viewer::LastPage}     | {#kb binary_viewer::LastPage}     |
| {#action binary_viewer::GoToOffset}   | {#kb binary_viewer::GoToOffset}   |
| {#action binary_viewer::Reload}       | {#kb binary_viewer::Reload}       |

Scroll within the current page as you would in an editor. Select and copy
hexadecimal byte pairs, or displayed text in Text mode. Split a tab to inspect different offsets independently. The
file path, offset, display mode and encoding are saved for project restoration;
file contents are not saved to the workspace database.

## Choose a text encoding

Select **Text**, then choose **UTF-8**, **UTF-16 LE**, **UTF-16 BE**,
**Windows-1252**, or **Latin-1**. Changing the encoding uses the loaded page
without rereading the file. It changes only the displayed interpretation.

Text mode preserves tabs and line feeds. Other control characters, escape
characters and directional formatting controls appear as explicit Unicode
escapes. Invalid encoded sequences use replacement characters. A multibyte
character split at a page boundary can also appear as a replacement character;
use **Go to Offset** to inspect a range containing the complete character. Hex
mode shows the bytes with any pending edits applied.

## Edit bytes

Click the pencil in the toolbar or run {#action binary_viewer::ToggleEditing}
to enable overwrite mode. Click a hexadecimal byte and type two hexadecimal
digits. Each digit updates one nibble; the caret advances to the next byte.
The ASCII column reflects the same bytes. Text mode is a read-only decoding.

Paste hexadecimal pairs such as `00 FF 2A` using {#kb binary_viewer::Paste}.
Whitespace is allowed. Invalid characters, incomplete pairs and pastes beyond
the loaded page show an error without changing bytes. Editing preserves file
length; insertion and deletion are not supported.

Use the toolbar's undo and redo controls or {#kb binary_viewer::Undo} and
{#kb binary_viewer::Redo}. Zed shows the normal unsaved tab marker. Save with
{#kb workspace::Save}, and use the normal Save/Discard/Cancel prompt when closing a dirty tab.
The normal autosave setting applies. Split tabs share pending byte edits and
undo history, while their offsets remain independent. Reload asks before
throwing away pending edits.

Up to 64 KiB of changed bytes can be pending at once. Save before making more
changes. Saving streams the file through a temporary file in the same directory,
checks its size and the original bytes at changed offsets, then replaces the
original. It needs enough free disk space for a complete temporary copy.
A conflict or failed save leaves pending edits available and shows an error.
Edits that already match the replacement bytes can be retried after a lost SSH
response. Undo remains available after saving. Undo history is bounded.

Pending edits survive page navigation and a remote disconnect, but are held in
memory. They are not a crash recovery backup; save work before ending a session.
Saving requires write access to the file and its parent directory.

## Local and SSH projects

SSH pages use the project's existing authenticated connection and matching
fork remote server. Bundled builds cache servers by the full client source
commit so an older fork with the same Zed version cannot be reused. Reconnect
the project, then choose **Reload** after a
disconnect. Displayed page bytes are cleared when the connection is lost.
Collaborative projects without an SSH connection are not supported.

Pages are read into memory without a download cache. Reads check worktree
containment, file size, changes during the read and a bounded response length.
The protocol permits at most 256 KiB per read and four simultaneous reads per
project connection. Loading has a 120-second timeout and shows an error with
a retry path when the file is missing, inaccessible or changes during a read.

The viewer never executes file contents. Encodings do not launch tools,
scripts, plugins or interpreters. It provides raw byte inspection rather than
file format parsing, decompilation or a whole-file search index.

See [PDF Viewer](./pdf-viewer.md) for document viewing and
[Remote Development](./remote-development.md) for SSH project setup.
