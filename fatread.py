"""Read-only FAT16 walker, used to check built images against factory disks."""
import struct


class Fat16:
    def __init__(self, f, part_lba):
        self.f, self.base = f, part_lba * 512
        f.seek(self.base)
        b = f.read(512)
        self.bs = b
        (self.bps, self.spc, self.rsv, self.nfats, self.nroot, tot16, self.media, self.fatsz,
         self.spt, self.heads, self.hidden, tot32) = struct.unpack_from('<HBHBHHBHHHII', b, 11)
        self.total = tot16 or tot32
        self.fat_lba = self.rsv
        self.root_lba = self.rsv + self.nfats * self.fatsz
        self.data_lba = self.root_lba + (self.nroot * 32 + 511) // 512
        f.seek(self.base + self.fat_lba * 512)
        self.fat = struct.unpack(f'<{self.fatsz * 256}H', f.read(self.fatsz * 512))

    def sec(self, lba, n=1):
        self.f.seek(self.base + lba * 512)
        return self.f.read(n * 512)

    def chain(self, c):
        out = []
        while 2 <= c < 0xFFF8:
            out.append(c)
            c = self.fat[c]
        return out

    def clus(self, c):
        return self.sec(self.data_lba + (c - 2) * self.spc, self.spc)

    def entries(self, cl=None):
        raw = self.sec(self.root_lba, self.data_lba - self.root_lba) if cl is None else \
            b''.join(self.clus(c) for c in self.chain(cl))
        for i in range(0, len(raw), 32):
            e = raw[i:i + 32]
            if e[0] == 0:
                break
            yield e

    @staticmethod
    def decode(e):
        name = e[0:8].decode('cp437').rstrip()
        ext = e[8:11].decode('cp437').rstrip()
        attr = e[11]
        time, date, cl, size = struct.unpack_from('<HHHI', e, 22)
        return (name + ('.' + ext if ext else '')), attr, time, date, cl, size

    def walk(self, cl=None, path=''):
        """Yield (path, attr, time, date, cluster, size, raw entry) depth first, in directory order."""
        for e in self.entries(cl):
            if e[0] == 0xE5:
                yield (path + '\\<deleted>', 0, 0, 0, 0, 0, e)
                continue
            n, attr, t, d, c, s = self.decode(e)
            if attr == 0x0F or n in ('.', '..'):
                continue
            yield (path + '\\' + n, attr, t, d, c, s, e)
            if attr & 0x10 and not attr & 0x08:
                yield from self.walk(c, path + '\\' + n)
