"""Merit D2000 "DISK UPGRADE KIT" archive (DISKIMG<drive>.NNN), reversed from the
restore discs (Docs/10).

The archive for a drive is split over the discs as DISKIMGC.001, .002, ... and is one
byte stream once concatenated. It is a sequence of records, no compression:

    u32  dir      index into @_DIRS_<drive>.LST (0 = drive root)
    13 B name     8.3, NUL padded
    u32  size
    u16  date     DOS format
    u16  time     DOS format
    u8   attr     DOS attributes
    3 B  padding  uninitialised struct padding (any value)
    size bytes    file data

Record order is D2000's copy order, so replaying it reproduces the FAT directory order.
"""
import struct

HDR = struct.Struct('<I13sIHHBHB')      # 29 bytes


class Entry:
    __slots__ = ('dir', 'name', 'size', 'date', 'time', 'attr')

    def __init__(self, d, name, size, date, time, attr):
        self.dir, self.name, self.size, self.date, self.time, self.attr = d, name, size, date, time, attr


class ByteStream:
    """Concatenate chunk iterators into one readable stream."""

    def __init__(self, iterators):
        self.its = iter(iterators)
        self.cur = iter(())
        self.buf = b''
        self.consumed = 0

    def read(self, n):
        while len(self.buf) < n:
            try:
                self.buf += next(self.cur)
            except StopIteration:
                try:
                    self.cur = next(self.its)
                except StopIteration:
                    break
        out, self.buf = self.buf[:n], self.buf[n:]
        self.consumed += len(out)
        return out


def records(stream):
    """Yield (Entry, data-chunk-generator). The caller must drain each generator."""
    while True:
        h = stream.read(HDR.size)
        if not h:
            return
        if len(h) != HDR.size:
            raise ValueError('archive truncated inside a record header')
        d, name, size, date, time, attr, zero, marker = HDR.unpack(h)
        name = name.split(b'\0')[0].decode('cp437')
        e = Entry(d, name, size, date, time, attr)

        def body(left=size):
            while left:
                b = stream.read(min(left, 1 << 20))
                if not b:
                    raise ValueError(f'archive truncated inside {name}')
                left -= len(b)
                yield b
        yield e, body()


def parse_install_dat(text):
    """@INSTALL.DAT: '[C] [2 DISK IMAGES] [1124483750 BYTES TOTAL ON THIS DRIVE]' per drive."""
    import re
    out = {}
    for m in re.finditer(r'\[(\w)\]\s*\[(\d+) DISK IMAGES?\]\s*\[(\d+) BYTES', text):
        out[m.group(1).upper()] = (int(m.group(2)), int(m.group(3)))
    return out
