# PDF renderer fixtures

`two-pages.pdf` is an original, generated test document with no third-party
content. Page 1 contains a red vector rectangle and Helvetica text. Page 2
contains a blue RGB image. It uses an ordinary cross-reference table with
uncompressed content streams, making it small and inspectable.

The renderer tests check both pages, scaled dimensions, pixel colors, malformed
input, invalid page numbers, allocation bounds, and the binary worker protocol.
