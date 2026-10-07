---
title: Binary Viewer
description: Inspect local and SSH file bytes in read-only, paged Zed tabs.
---

# Binary Viewer

Open a binary file from the Project Panel, file finder, file open dialog, or
command line. This fork opens files that its text reader identifies as binary in
a read-only viewer. PDFs and supported images keep their dedicated viewers.
Files above the text editor's 6 GiB limit also open in the paged viewer.

To inspect the saved bytes of any open file, run
{#action binary_viewer::OpenInBinaryViewer} from the command palette. This opens
a separate tab and preserves unsaved edits in the original editor. The viewer
reads the file on disk; unsaved edits are not included.

## Navigate bytes

The viewer loads at most 64 KiB at a time. **Hex + ASCII** shows 16 bytes per
row, with a hexadecimal absolute byte offset and a printable ASCII column.
Nonprintable bytes appear as dots in the ASCII column.

Use **First**, **Previous**, **Next**, and **Last** to change pages. **Go to
Offset** accepts a decimal byte offset or a hexadecimal value starting with
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
displayed text. Split a tab to inspect different offsets independently. The
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
mode always shows the original bytes.

## Local and SSH projects

SSH pages use the project's existing authenticated connection and matching
fork remote server. Reconnect the project, then choose **Reload** after a
disconnect. Displayed page bytes are cleared when the connection is lost.
Collaborative projects without an SSH connection are not supported.

Pages are read into memory without a download cache. Reads check worktree
containment, file size, changes during the read and a bounded response length.
The protocol permits at most 256 KiB per read and four simultaneous reads per
project connection. Loading has a 120-second timeout and shows an error with
a retry path when the file is missing, inaccessible or changes during a read.

The viewer has no save or execute operation. Encodings do not launch tools,
scripts, plugins or interpreters. It provides raw byte inspection rather than
file format parsing, decompilation or a whole-file search index.

See [PDF Viewer](./pdf-viewer.md) for document viewing and
[Remote Development](./remote-development.md) for SSH project setup.
