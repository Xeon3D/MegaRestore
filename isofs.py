"""Minimal read-only ISO 9660 reader (primary volume descriptor, 8.3 names).

Enough for the Megatouch restore discs: find a file by path, read it or stream it.
"""
import struct

SECTOR = 2048


class IsoFile:
    def __init__(self, iso, name, lba, size, is_dir):
        self.iso, self.name, self.lba, self.size, self.is_dir = iso, name, lba, size, is_dir

    def read(self):
        return self.iso._read(self.lba * SECTOR, self.size)

    def stream(self, chunk=1 << 20):
        """Yield the file's bytes in chunks (the big DISKIMG archives)."""
        pos, left = self.lba * SECTOR, self.size
        while left:
            n = min(chunk, left)
            yield self.iso._read(pos, n)
            pos += n
            left -= n


class Iso:
    def __init__(self, path):
        self.path = path
        self.f = open(path, 'rb')
        pvd = self._read(16 * SECTOR, SECTOR)
        if pvd[1:6] != b'CD001' or pvd[0] != 1:
            raise ValueError(f'{path}: not an ISO 9660 image')
        self.volume_id = pvd[40:72].decode('ascii', 'replace').strip()
        self.root = self._record(pvd[156:156 + 34])

    def close(self):
        self.f.close()

    def _read(self, pos, n):
        self.f.seek(pos)
        d = self.f.read(n)
        if len(d) != n:
            raise IOError(f'{self.path}: short read at {pos}')
        return d

    def _record(self, r):
        lba, size = struct.unpack_from('<I', r, 2)[0], struct.unpack_from('<I', r, 10)[0]
        flags, nlen = r[25], r[32]
        name = r[33:33 + nlen].decode('ascii', 'replace')
        name = name.split(';')[0].rstrip('.')
        return IsoFile(self, name, lba, size, bool(flags & 2))

    def listdir(self, d=None):
        d = d or self.root
        data, out, pos = d.read(), [], 0
        while pos < len(data):
            rl = data[pos]
            if rl == 0:                      # records never span sectors
                pos = (pos // SECTOR + 1) * SECTOR
                continue
            r = data[pos:pos + rl]
            if r[32] == 1 and r[33] in (0, 1):   # . and ..
                pos += rl
                continue
            out.append(self._record(r))
            pos += rl
        return out

    def find(self, path):
        """Case-insensitive lookup, path with / or \\ separators. None if absent."""
        cur = self.root
        for part in path.replace('\\', '/').strip('/').split('/'):
            nxt = [e for e in self.listdir(cur) if e.name.upper() == part.upper()]
            if not nxt:
                return None
            cur = nxt[0]
        return cur
