"""Partimage 0.6.1 image reader (Merit Linux restore discs, Docs/11).

    python partimage.py out.raw vol000.gz [vol001.gz ...]     (or ISO:path pairs via API)

Each volume file is its own gzip stream; decompressed, volume 0 is:
    CVolumeHeader 512 ("PaRtImAgE-VoLuMe", "0.6.1", ..., u32 volume number at 96)
    CMainHeader 16384 + u32 crc          (fs name at 0, e.g. "ext2fs")
    "MAGIC-BEGIN-MBRBACKUP" + MBR records (none on these discs)
    "MAGIC-BEGIN-EXT000".."EXT009", each + u32 0
    "MAGIC-BEGIN-LOCALHEADER" + CLocalHeader 16384 + u32 crc
         u64 block size, used blocks, block count, bitmap size, ...
    "MAGIC-BEGIN-BITMAP" + bitmap (bitmap size bytes; bit i of byte k = block 8k+i used)
    "MAGIC-BEGIN-INFO" + fs info 16384 + u32 crc
    "MAGIC-BEGIN-DATABLOCKS" + used blocks in order, a 16-byte "CHK\\0" record after
         every 65536 data bytes
    "MAGIC-BEGIN-TAIL" + tail
Later volumes: CVolumeHeader 512, then the stream continues where the previous ended.
"""
import struct
import sys
import zlib

CHUNK = 65536


class VolumeStream:
    """Decompressed bytes of all volumes, volume headers stripped."""

    def __init__(self, sources):
        self.sources = iter(sources)      # each: iterator of compressed chunks
        self.buf = bytearray()
        self.cur = None
        self.z = None
        self.first = True

    def _fill(self, n):
        while len(self.buf) < n:
            if self.cur is None:
                try:
                    self.cur = next(self.sources)
                except StopIteration:
                    return
                self.z = zlib.decompressobj(31)
                self.skip = 512 if not self.first else 0   # later volumes: drop their header
                self.first = False
            try:
                c = next(self.cur)
                out = self.z.decompress(c)
            except StopIteration:
                if not self.z.eof:
                    raise IOError('gzip volume truncated')
                self.cur = None
                continue
            if self.skip:
                k = min(self.skip, len(out))
                if out[:k] and self.skip == 512 and out[:16] != b'PaRtImAgE-VoLuMe':
                    raise IOError('volume does not start with a partimage header')
                out = out[k:]
                self.skip -= k
            self.buf += out

    def read(self, n):
        self._fill(n)
        out = bytes(self.buf[:n])
        del self.buf[:n]
        return out

    def expect(self, magic):
        got = self.read(len(magic))
        if got != magic:
            raise IOError(f'expected {magic!r}, got {got!r}')


def restore(sources, write_block, progress=None):
    """write_block(block_no, bytes). Returns (fs name, block size, block count, used)."""
    s = VolumeStream(sources)
    vh = s.read(512)
    if vh[:16] != b'PaRtImAgE-VoLuMe':
        raise IOError('not a partimage volume')
    mh = s.read(16384 + 4)
    fs = mh[:mh.index(b'\0')].decode()
    s.expect(b'MAGIC-BEGIN-MBRBACKUP')
    for k in range(10):
        s.expect(f'MAGIC-BEGIN-EXT{k:03d}'.encode())
        size = struct.unpack('<I', s.read(4))[0]
        s.read(size)
    s.expect(b'MAGIC-BEGIN-LOCALHEADER')
    lh = s.read(16384 + 4)
    bsize, used, count, bmsize = struct.unpack_from('<4Q', lh, 0)
    s.expect(b'MAGIC-BEGIN-BITMAP')
    bm = s.read(bmsize)
    s.expect(b'MAGIC-BEGIN-INFO')
    s.read(16384 + 4)
    s.expect(b'MAGIC-BEGIN-DATABLOCKS')
    since = 0
    done = 0
    for blk in range(count):
        if not bm[blk >> 3] >> (blk & 7) & 1:
            continue
        data = s.read(bsize)
        if len(data) != bsize:
            raise IOError(f'image ends at block {blk}')
        write_block(blk, data)
        since += bsize
        done += 1
        if since == CHUNK:
            chk = s.read(16)
            if chk[:4] != b'CHK\0':
                raise IOError(f'missing CHK record after block {blk}')
            since = 0
        if progress and done % 4096 == 0:
            progress(done, used)
    if done != used:
        raise IOError(f'bitmap has {done} used blocks, header says {used}')
    tail = s.read(16)
    if not tail.startswith(b'MAGIC-BEGIN-TAIL'):
        raise IOError(f'no tail after data: {tail!r}')
    return fs, bsize, count, used


def file_chunks(path, n=1 << 22):
    with open(path, 'rb') as f:
        while True:
            c = f.read(n)
            if not c:
                return
            yield c


if __name__ == '__main__':
    out = open(sys.argv[1], 'wb')

    def wb(blk, data):
        out.seek(blk * len(data))
        out.write(data)
    print(restore([file_chunks(p) for p in sys.argv[2:]], wb))
