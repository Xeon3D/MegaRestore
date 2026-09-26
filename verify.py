"""Verify a MegaRestore DOS image against its restore discs (Docs/10).

    python verify.py out.img disc1.iso [disc2.iso ...]

Checks: partition table; per volume FAT copies equal, every cluster owned by at most one
chain, chain length == ceil(size / cluster), no lost clusters, '.'/'..' entries; then every
archive record's bytes against the file at its path in the image.
"""
import struct
import sys
import hashlib

from fatread import Fat16
from isofs import Iso
from diskimg import ByteStream, records, parse_install_dat


def check_volume(f, lba, label):
    fs = Fat16(f, lba)
    errs = []
    fats = [fs.sec(fs.fat_lba + i * fs.fatsz, fs.fatsz) for i in range(fs.nfats)]
    if any(x != fats[0] for x in fats):
        errs.append('FAT copies differ')
    owner = {}
    csize = fs.spc * 512
    nfiles = ndirs = 0

    def own(chain, who):
        for c in chain:
            if c in owner:
                errs.append(f'cross-link cluster {c}: {owner[c]} / {who}')
            owner[c] = who

    def walk(cl, path):
        nonlocal nfiles, ndirs
        for e in fs.entries(cl):
            if e[0] == 0xE5:
                continue
            n, attr, t, d, c, s = Fat16.decode(e)
            if attr & 0x08:
                continue
            if n in ('.', '..'):
                continue
            p = path + '\\' + n
            ch = fs.chain(c)
            if attr & 0x10:
                ndirs += 1
                own(ch, p)
                sub = list(fs.entries(c))
                dot = [Fat16.decode(x) for x in sub[:2]]
                if dot[0][0] != '.' or dot[0][4] != c or dot[1][0] != '..' or dot[1][4] != (cl or 0):
                    errs.append(f'bad . / .. in {p}')
                walk(c, p)
            else:
                nfiles += 1
                if len(ch) != (s + csize - 1) // csize:
                    errs.append(f'{p}: {len(ch)} clusters for {s} bytes')
                own(ch, p)
    walk(None, '')
    used = {c for c in range(2, len(fs.fat)) if fs.fat[c] != 0 and c < (fs.total - fs.data_lba) // fs.spc + 2}
    lost = used - set(owner)
    if lost:
        errs.append(f'{len(lost)} lost clusters')
    print(f'{label}: {ndirs} dirs, {nfiles} files, {len(owner)} clusters in use, '
          f'label {fs.bs[0x2b:0x36].decode()!r}, OEM {fs.bs[3:11].decode(errors="replace")!r}: '
          + ('OK' if not errs else f'{len(errs)} ERRORS'))
    for e in errs[:20]:
        print('   ', e)
    return fs, not errs


def lookup(fs, path):
    """path like 'MTOOLS\\MISC\\X.EXE' -> (attr, time, date, cluster, size) or None."""
    cl = None
    parts = [p for p in path.split('\\') if p]
    for i, part in enumerate(parts):
        hit = None
        for e in fs.entries(cl):
            if e[0] == 0xE5:
                continue
            n, attr, t, d, c, s = Fat16.decode(e)
            if n.upper() == part.upper() and not attr & 0x08:
                hit = (attr, t, d, c, s)
        if hit is None:
            return None
        if i < len(parts) - 1:
            cl = hit[3]
    return hit


def read_file(fs, c, s):
    out = bytearray()
    for cl in fs.chain(c):
        out += fs.clus(cl)
    return bytes(out[:s])


def main():
    img, isos = sys.argv[1], [Iso(p) for p in sys.argv[2:]]
    f = open(img, 'rb')
    mbr = f.read(512)
    parts = [struct.unpack_from('<B3sB3sII', mbr, 0x1BE + 16 * i) for i in range(4)]
    print('MBR:', [(hex(p[0]), hex(p[2]), p[4], p[5]) for p in parts if p[2]], 'sig', mbr[510:].hex())
    ext = next(p for p in parts if p[2] == 5)
    f.seek(ext[4] * 512)
    ebr = f.read(512)
    lp = struct.unpack_from('<B3sB3sII', ebr, 0x1BE)
    print('EBR:', hex(lp[2]), lp[4], lp[5], 'sig', ebr[510:].hex())
    vols = {'C': check_volume(f, parts[0][4], 'C:'), 'D': check_volume(f, ext[4] + lp[4], 'D:')}

    def find(name):
        for i in isos:
            e = i.find(name)
            if e:
                return i, e
        return None, None

    inst = parse_install_dat(next(i.find('@INSTALL.DAT').read().decode('latin1') for i in isos if i.find('@INSTALL.DAT')))
    bad = n = 0
    for drv, (count, total) in inst.items():
        fs = vols[drv][0]
        _, lst = find(f'@_DIRS_{drv}.LST')
        dirs = [x.strip() for x in lst.read().decode('cp437').split('\n') if x.strip()]
        streams = [find(f'DISKIMG{drv}.{k:03d}')[1].stream() for k in range(1, count + 1)]
        seen = {}
        for e, body in records(ByteStream(streams)):
            data = b''.join(body)
            path = dirs[e.dir].rstrip('\\')[2:] + '\\' + e.name
            seen[path.upper()] = (e, hashlib.sha1(data).digest())
        for path, (e, h) in seen.items():        # last record for a path wins, as on disk
            n += 1
            hit = lookup(fs, path)
            if hit is None or hit[4] != e.size or hit[0] != e.attr or hit[1] != e.time or hit[2] != e.date \
                    or hashlib.sha1(read_file(fs, hit[3], hit[4])).digest() != h:
                bad += 1
                if bad <= 10:
                    print('  MISMATCH', drv + ':' + path, hit and hit[:3], (e.attr, e.time, e.date))
    print(f'archive check: {n} files, {bad} mismatches')
    ok = all(v[1] for v in vols.values()) and bad == 0
    print('RESULT:', 'PASS' if ok else 'FAIL')
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
