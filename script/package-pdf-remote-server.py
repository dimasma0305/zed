#!/usr/bin/env python3
"""Validate and package the fork's matching Linux server without opening a connection."""
import argparse
import gzip
import hashlib
import json
import pathlib
import re
import shutil
import subprocess
import tomllib

parser = argparse.ArgumentParser()
parser.add_argument("binary", type=pathlib.Path)
parser.add_argument("destination", type=pathlib.Path)
parser.add_argument("--commit", required=True)
arguments = parser.parse_args()
repository = pathlib.Path(__file__).resolve().parent.parent
with (repository / "crates/zed/Cargo.toml").open("rb") as source:
    package_version = tomllib.load(source)["package"]["version"]
protocol_source = (repository / "crates/rpc/src/rpc.rs").read_text(encoding="utf-8")
protocol_match = re.search(r"pub const PROTOCOL_VERSION: u32 = (\d+);", protocol_source)
if not protocol_match or not re.fullmatch(r"[0-9a-f]{40}", arguments.commit):
    raise SystemExit("Source identity could not be verified")
binary = arguments.binary.resolve(strict=True)
information = json.loads(subprocess.run(
    [str(binary), "build-info"], capture_output=True, check=True, timeout=20,
).stdout)
expected = {
    "source_commit": arguments.commit,
    "package_version": package_version,
    "protocol_version": int(protocol_match[1]),
    "release_channel": "stable",
    "target": "x86_64-unknown-linux-gnu",
}
if information != expected:
    raise SystemExit(f"Remote server build identity differs: {information}")
version = subprocess.run([str(binary), "version"], capture_output=True, check=True, timeout=20).stdout.decode().strip()
if version != package_version:
    raise SystemExit("Remote server version command does not match the client package")
with binary.open("rb") as source:
    header = source.read(20)
if header[:6] != b"\x7fELF\x02\x01" or int.from_bytes(header[18:20], "little") != 62:
    raise SystemExit("Expected a Linux x86_64 ELF executable")
arguments.destination.mkdir(parents=True, exist_ok=False)
archive = arguments.destination / "zed-remote-server-linux-x86_64.gz"
with binary.open("rb") as source, archive.open("wb") as output:
    with gzip.GzipFile(filename="", mode="wb", fileobj=output, mtime=0) as compressed:
        shutil.copyfileobj(source, compressed)
if not 0 < archive.stat().st_size <= 256 * 1024 * 1024:
    raise SystemExit("Remote server archive exceeds the client package limit")
with archive.open("rb") as source:
    archive_digest = hashlib.file_digest(source, "sha256").hexdigest()
manifest = {
    "format_version": 1,
    "release_channel": "stable",
    "package_version": package_version,
    "source_commit": arguments.commit,
    "protocol_version": expected["protocol_version"],
    "servers": [{
        "platform": "linux-x86_64",
        "archive": archive.name,
        "sha256": archive_digest,
    }],
}
(arguments.destination / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
(arguments.destination / "build-info.json").write_text(json.dumps(information, indent=2) + "\n", encoding="utf-8")
print("Verified same-commit Linux x86_64 server version/protocol; remote-server package created")
