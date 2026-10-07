import json
from pathlib import Path
import struct
import subprocess
import sys
import zlib


def render(executable, document, page_index, scale):
    request = struct.pack("<8sIfQ", b"ZPDF0001", page_index, scale, len(document))
    process = subprocess.run(
        [str(executable), "--pdf-render-worker"],
        input=request + document,
        capture_output=True,
        timeout=35,
    )
    if process.returncode:
        raise RuntimeError(process.stderr.decode("utf-8", errors="replace"))
    magic, count, page_width, page_height, width, height = struct.unpack(
        "<8sIffII", process.stdout[:28]
    )
    pixels = process.stdout[28:]
    assert magic == b"ZIMG0001"
    assert len(pixels) == width * height * 4
    assert all(pixels[offset] == 255 for offset in range(3, len(pixels), 4))
    return count, page_width, page_height, width, height, pixels


def write_png(path, width, height, pixels):
    def chunk(kind, data):
        return (
            struct.pack(">I", len(data))
            + kind
            + data
            + struct.pack(">I", zlib.crc32(kind + data))
        )

    rows = b"".join(
        b"\0" + pixels[row * width * 4 : (row + 1) * width * 4]
        for row in range(height)
    )
    path.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(rows))
        + chunk(b"IEND", b"")
    )


def main():
    executable = Path(sys.argv[1]).resolve()
    output = Path(sys.argv[2])
    output.mkdir(parents=True, exist_ok=True)
    document = (Path(__file__).parent / "fixtures/two-pages.pdf").read_bytes()
    for page_index, scale, expected in [(0, 1.0, (200, 300)), (1, 2.0, (600, 400))]:
        count, page_width, page_height, width, height, pixels = render(
            executable, document, page_index, scale
        )
        assert count == 2
        assert (width, height) == expected
        assert (page_width, page_height) == (
            (200.0, 300.0) if page_index == 0 else (300.0, 200.0)
        )
        if page_index == 0:
            assert any(
                pixels[offset] > 200 and pixels[offset + 1] < 20
                for offset in range(0, len(pixels), 4)
            )
            assert any(
                max(pixels[offset : offset + 3]) < 100
                for offset in range(0, width * 80 * 4, 4)
            ), "Helvetica text did not render"
        else:
            assert any(
                pixels[offset + 2] > 200 and pixels[offset] < 20
                for offset in range(0, len(pixels), 4)
            ), "Embedded image did not render"
        write_png(output / f"page-{page_index + 1}.png", width, height, pixels)

    invalid = subprocess.run(
        [str(executable), "--pdf-render-worker"],
        input=struct.pack("<8sIfQ", b"ZPDF0001", 0, 1.0, 2**64 - 1),
        capture_output=True,
        timeout=5,
    )
    assert invalid.returncode != 0
    assert b"128 MiB" in invalid.stderr
    (output / "result.json").write_text(
        json.dumps({"pages_rendered": 2, "worker_protocol": "passed", "oversized_input": "rejected"}, indent=2)
        + "\n"
    )
    print("Worker process smoke test passed; rendered page PNGs saved")


if __name__ == "__main__":
    main()
