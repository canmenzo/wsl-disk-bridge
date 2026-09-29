"""Smoke test for nbd_server.exe on a file-backed fake disk (Windows only).

Serves a random 8 MiB file with --offset/--size, then reads it back over NBD
with both handshakes nbd-client uses (EXPORT_NAME and GO) and compares bytes.
A second run stamps an ext4 superblock magic into the partition, leaves out
--bind, and checks filesystem detection and the loopback fallback.

    python tests/nbd_smoke.py path\\to\\nbd_server.exe
"""

import os
import socket
import struct
import subprocess
import sys
import time

EXE = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else "nbd_server.exe")
PORT = 10809
OFFSET = 1 << 20  # partition starts 1 MiB into the "disk"
SIZE = 4 << 20    # and is 4 MiB long

NBDMAGIC = 0x4E42444D41474943
IHAVEOPT = 0x49484156454F5054
REPLY_MAGIC = 0x3E889045565A9
REQUEST_MAGIC = 0x25609513
SIMPLE_REPLY_MAGIC = 0x67446698


def recv_exact(s, n):
    buf = b""
    while len(buf) < n:
        chunk = s.recv(n - len(buf))
        if not chunk:
            raise AssertionError("server closed the connection")
        buf += chunk
    return buf


def connect():
    deadline = time.time() + 15
    while True:
        try:
            s = socket.create_connection(("127.0.0.1", PORT), timeout=10)
            break
        except OSError:
            if time.time() > deadline:
                raise
            time.sleep(0.3)
    magic, opt, _flags = struct.unpack(">QQH", recv_exact(s, 18))
    assert (magic, opt) == (NBDMAGIC, IHAVEOPT), "bad greeting"
    s.sendall(struct.pack(">I", 3))  # FIXED_NEWSTYLE | NO_ZEROES
    return s


def handshake_export_name(s):
    s.sendall(struct.pack(">QII", IHAVEOPT, 1, 0))
    size, flags = struct.unpack(">QH", recv_exact(s, 10))
    return size, flags


def handshake_go(s):
    payload = struct.pack(">IH", 0, 0)  # empty export name, no info requests
    s.sendall(struct.pack(">QII", IHAVEOPT, 7, len(payload)) + payload)
    magic, opt, rtype, rlen = struct.unpack(">QIII", recv_exact(s, 20))
    assert (magic, opt, rtype) == (REPLY_MAGIC, 7, 3), "expected NBD_REP_INFO"
    info = recv_exact(s, rlen)
    _itype, size, flags = struct.unpack(">HQH", info)
    magic, opt, rtype, rlen = struct.unpack(">QIII", recv_exact(s, 20))
    assert (magic, opt, rtype, rlen) == (REPLY_MAGIC, 7, 1, 0), "expected NBD_REP_ACK"
    return size, flags


def read(s, handle, offset, length):
    s.sendall(struct.pack(">IHHQQI", REQUEST_MAGIC, 0, 0, handle, offset, length))
    magic, err, h = struct.unpack(">IIQ", recv_exact(s, 16))
    assert (magic, err, h) == (SIMPLE_REPLY_MAGIC, 0, handle), "bad read reply"
    return recv_exact(s, length)


def serve_and_check(disk, extra_args):
    image = os.path.abspath("test_disk.img")
    with open(image, "wb") as f:
        f.write(disk)
    part = disk[OFFSET:OFFSET + SIZE]

    server = subprocess.Popen(
        [EXE, "--disk", image, "--offset", str(OFFSET), "--size", str(SIZE),
         "--port", str(PORT)] + extra_args,
        stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    try:
        for name, handshake in (("EXPORT_NAME", handshake_export_name), ("GO", handshake_go)):
            s = connect()
            size, flags = handshake(s)
            assert size == SIZE, "%s: size %d != %d" % (name, size, SIZE)
            assert flags & 2, "%s: export is not read-only" % name
            cases = [(0, 4096), (512 * 3, 512), (12345, 1000), (SIZE - 512, 512), (65536, 131072)]
            for i, (off, length) in enumerate(cases):
                got = read(s, i + 1, off, length)
                assert got == part[off:off + length], "%s: data mismatch at %d+%d" % (name, off, length)
            s.sendall(struct.pack(">IHHQQI", REQUEST_MAGIC, 0, 2, 99, 0, 0))  # disconnect
            s.close()
            print("%s handshake: size ok, read-only, %d reads match" % (name, len(cases)))
    finally:
        server.kill()
        out = server.communicate()[0].decode(errors="replace")
        print("--- server output ---\n" + out)
    assert "listening on 127.0.0.1:%d" % PORT in out, "server did not bind to loopback"
    assert "FATAL" not in out, "server crashed"
    return out


def main():
    disk = os.urandom(8 << 20)
    serve_and_check(disk, ["--bind", "127.0.0.1"])

    # ext4 keeps its magic 0xEF53 at byte 0x438 of the partition (little-endian)
    ext4 = bytearray(disk)
    ext4[OFFSET + 0x438:OFFSET + 0x43A] = b"\x53\xef"
    out = serve_and_check(bytes(ext4), [])
    assert "Filesystem: ext2/ext3/ext4" in out, "ext4 not detected"
    assert "No WSL network adapter found" in out, "expected the loopback fallback"
    print("OK")


if __name__ == "__main__":
    main()
