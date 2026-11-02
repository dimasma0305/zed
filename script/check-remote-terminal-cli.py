#!/usr/bin/env python3
"""Exercise the actual remote launcher binary with a local Unix-socket peer."""

import json
import os
from pathlib import Path
import socket
import struct
import subprocess
import sys
import tempfile


def receive_exact(connection, size):
    data = bytearray()
    while len(data) < size:
        part = connection.recv(size - len(data))
        if not part:
            raise AssertionError("launcher disconnected before sending its request")
        data.extend(part)
    return bytes(data)


def main():
    server = Path(sys.argv[1]).resolve(strict=True)
    with tempfile.TemporaryDirectory(prefix="zed-cli-check-") as temporary:
        root = Path(temporary)
        launcher = root / "zed"
        launcher.symlink_to(server)
        folder = root / "folder with spaces"
        folder.mkdir()
        other = root / "other"
        other.mkdir()
        regular_file = root / "file.txt"
        regular_file.write_text("fixture", encoding="utf-8")
        socket_path = root / "cli.sock"
        environment = {
            **os.environ,
            "ZED_REMOTE_CLI_SOCKET": str(socket_path),
            "ZED_REMOTE_CLI_DIRECTORY": str(root),
        }
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
            listener.bind(str(socket_path))
            listener.listen(1)
            listener.settimeout(15)
            cases = [
                ([], [folder], {"Ok": None}, 0),
                (["."], [folder], {"Ok": None}, 0),
                ([str(other)], [other], {"Ok": None}, 0),
                (["--add", "../other", "."], [other, folder], {"Ok": None}, 0),
                (["."], [folder], {"Err": "fixture: project is closed"}, 1),
                (None, [folder], {"Ok": None}, 0),
            ]
            for arguments, expected, response, expected_exit in cases:
                command = (["/bin/bash", "--noprofile", "--norc", "-i"]
                           if arguments is None else [str(launcher), *arguments])
                process = subprocess.Popen(
                    command, cwd=folder, env=environment, stdin=subprocess.PIPE,
                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                )
                try:
                    if arguments is None:
                        process.stdin.write(
                            'PATH=/usr/bin:/bin\n'
                            'export PATH="$ZED_REMOTE_CLI_DIRECTORY:$PATH"\n'
                            'zed .\nexit\n'
                        )
                        process.stdin.flush()
                    with listener.accept()[0] as connection:
                        connection.settimeout(15)
                        length = struct.unpack("!I", receive_exact(connection, 4))[0]
                        assert 0 < length <= 65536
                        request = json.loads(receive_exact(connection, length))
                        assert request == {
                            "paths": [str(path.resolve()) for path in expected],
                            "add": arguments is not None and "--add" in arguments,
                        }, request
                        body = json.dumps(response).encode()
                        connection.sendall(struct.pack("!I", len(body)) + body)
                    stdout, stderr = process.communicate(timeout=15)
                    assert process.returncode == expected_exit, (stdout, stderr)
                    if expected_exit:
                        assert "fixture: project is closed" in stderr
                finally:
                    if process.poll() is None:
                        process.kill()
                        process.communicate()

        for arguments in [[str(regular_file)], ["missing"], ["--new"], ["."]]:
            result = subprocess.run(
                [str(launcher), *arguments], cwd=folder, env=environment,
                capture_output=True, text=True, timeout=15,
            )
            assert result.returncode != 0, arguments
            assert result.stderr, arguments
        help_result = subprocess.run(
            [str(launcher), "--help"], capture_output=True, text=True, timeout=15,
        )
        assert help_result.returncode == 0
        assert "current folder" in help_result.stdout
        print("Remote launcher: 6 IPC/startup cases, 4 error cases and help passed")


if __name__ == "__main__":
    main()
