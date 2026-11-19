#!/usr/bin/env python3
"""Extract files from an Xbox 360 STFS package (CON / LIVE / PIRS).

Usage:
    python stfs_extract.py <package> <output_dir> [--list]

Handles both "female" (read-only, block separation bit set: LIVE/PIRS) and
"male" (CON) packages, contiguous and fragmented files, and nested folders.
Only the Python standard library is used.
"""
import argparse
import os
import struct
import sys

BLOCK = 0x1000
DATA_PER_L0 = 0xAA          # data blocks covered by one level-0 hash table
DATA_PER_L1 = 0xAA * 0xAA   # 0x70E4


def u24le(b, o):
    return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16)


def u24be(b, o):
    return (b[o] << 16) | (b[o + 1] << 8) | b[o + 2]


class StfsPackage:
    def __init__(self, path):
        with open(path, "rb") as f:
            self.data = f.read()
        d = self.data
        self.magic = d[0:4]
        if self.magic not in (b"CON ", b"LIVE", b"PIRS"):
            raise ValueError(f"not an STFS package (magic {self.magic!r})")
        header_size = struct.unpack_from(">I", d, 0x340)[0]
        self.content_type = struct.unpack_from(">I", d, 0x344)[0]
        self.title_id = struct.unpack_from(">I", d, 0x360)[0]
        self.descriptor_type = struct.unpack_from(">I", d, 0x3A9)[0]
        if self.descriptor_type != 0:
            raise ValueError("package uses SVOD, not STFS")
        vd = 0x379
        self.block_sep = d[vd + 2]
        self.ft_block_count = struct.unpack_from("<H", d, vd + 3)[0]
        self.ft_block_num = u24le(d, vd + 5)
        self.total_alloc = struct.unpack_from(">I", d, vd + 0x1C)[0]
        # "female" (read-only) packages have one hash table per level,
        # "male" packages keep two (active/inactive) copies.
        self.shift = 0 if (self.block_sep & 1) else 1
        self.step0 = 0xAB if self.shift == 0 else 0xAC
        self.step1 = 0x718F if self.shift == 0 else 0x723A
        self.base = (header_size + 0xFFF) & 0xF000
        self.display_name = d[0x411:0x411 + 0x80].decode("utf-16-be", "ignore").split("\0")[0]
        self.title_name = d[0x1691:0x1691 + 0x80].decode("utf-16-be", "ignore").split("\0")[0]

    # --- block addressing -------------------------------------------------
    def backing_block(self, n):
        r = (((n + DATA_PER_L0) // DATA_PER_L0) << self.shift) + n
        if n < DATA_PER_L0:
            return r
        r += ((n + DATA_PER_L1) // DATA_PER_L1) << self.shift
        if n < DATA_PER_L1:
            return r
        return r + (1 << self.shift)

    def block_offset(self, n):
        return self.base + self.backing_block(n) * BLOCK

    def l0_hash_block(self, n):
        if n < DATA_PER_L0:
            return 0
        num = (n // DATA_PER_L0) * self.step0
        num += ((n // DATA_PER_L1) + 1) << self.shift
        if n < DATA_PER_L1:
            return num
        return num + (1 << self.shift)

    def next_block(self, n):
        off = self.base + self.l0_hash_block(n) * BLOCK
        # Male (CON) packages keep two copies of each hash table; this uses the
        # first copy, which is correct for unmodified packages. Female
        # (LIVE/PIRS, e.g. XBLIG) packages have a single copy.
        entry = off + (n % DATA_PER_L0) * 0x18
        return u24be(self.data, entry + 0x15)

    def read_block(self, n):
        o = self.block_offset(n)
        return self.data[o:o + BLOCK]

    # --- file table ---------------------------------------------------------
    def entries(self):
        out = []
        blk = self.ft_block_num
        for i in range(self.ft_block_count):
            raw = self.read_block(blk)
            for j in range(0, BLOCK, 0x40):
                e = raw[j:j + 0x40]
                flags = e[0x28]
                nlen = flags & 0x3F
                if nlen == 0:
                    continue
                out.append({
                    "index": i * 0x40 + j // 0x40,
                    "name": e[:nlen].decode("ascii", "replace"),
                    "dir": bool(flags & 0x80),
                    "contig": bool(flags & 0x40),
                    "blocks": u24le(e, 0x29),
                    "start": u24le(e, 0x2F),
                    "parent": struct.unpack_from(">h", e, 0x32)[0],
                    "size": struct.unpack_from(">I", e, 0x34)[0],
                })
            if i + 1 < self.ft_block_count:
                blk = self.next_block(blk)
        self._by_index = {e["index"]: e for e in out}
        return out

    def path_of(self, e):
        parts = [e["name"]]
        p = e["parent"]
        seen = set()
        while p != -1 and p in self._by_index and p not in seen:
            seen.add(p)
            parts.append(self._by_index[p]["name"])
            p = self._by_index[p]["parent"]
        return "/".join(reversed(parts))

    def read_file(self, e):
        remaining = e["size"]
        buf = bytearray()
        blk = e["start"]
        for i in range(e["blocks"]):
            chunk = self.read_block(blk)
            buf += chunk[:min(BLOCK, remaining)]
            remaining -= BLOCK
            if remaining <= 0:
                break
            blk = blk + 1 if e["contig"] else self.next_block(blk)
        return bytes(buf)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("package")
    ap.add_argument("outdir", nargs="?")
    ap.add_argument("--list", action="store_true")
    a = ap.parse_args()

    pkg = StfsPackage(a.package)
    print(f"Magic: {pkg.magic.decode()}  Title ID: {pkg.title_id:08X}  "
          f"Content type: {pkg.content_type:08X}")
    print(f"Display name: {pkg.display_name!r}  Title name: {pkg.title_name!r}")
    print(f"Layout: {'female/read-only' if pkg.shift == 0 else 'male'}, "
          f"file table @ block {pkg.ft_block_num} x{pkg.ft_block_count}, "
          f"{pkg.total_alloc} blocks allocated")
    entries = pkg.entries()
    files = [e for e in entries if not e["dir"]]
    print(f"{len(files)} files, {len(entries) - len(files)} folders")
    if a.list or not a.outdir:
        for e in entries:
            print(f"{'<DIR>' if e['dir'] else e['size']:>10}  {pkg.path_of(e)}")
        return
    root = os.path.abspath(a.outdir)
    for e in entries:
        rel = pkg.path_of(e)
        dest = os.path.abspath(os.path.join(root, *rel.split("/")))
        if not dest.startswith(root + os.sep):
            print(f"skipping unsafe path {rel!r}", file=sys.stderr)
            continue
        if e["dir"]:
            os.makedirs(dest, exist_ok=True)
            continue
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        data = pkg.read_file(e)
        if len(data) != e["size"]:
            print(f"WARNING: {rel}: got {len(data)} of {e['size']} bytes", file=sys.stderr)
        with open(dest, "wb") as f:
            f.write(data)
    print(f"Extracted to {root}")


if __name__ == "__main__":
    main()
