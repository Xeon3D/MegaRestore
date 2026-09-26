"""Check a MegaRestore Linux image (Docs/11): partition chain, e2fsck -fn (WSL) on every
ext2/3 partition, swap signature, LILO signature in the MBR.

    python verify_linux.py out.img
"""
import struct
import subprocess
import sys


def parts(f):
    f.seek(0)
    m = f.read(512)
    out = []
    for i in range(4):
        e = struct.unpack_from('<B3sB3sII', m, 0x1BE + 16 * i)
        if not e[2]:
            continue
        out.append((i + 1, e[2], e[4], e[5]))
        if e[2] in (5, 0xF):
            ext, cur, n = e[4], e[4], 5
            while True:
                f.seek(cur * 512)
                b = f.read(512)
                l = struct.unpack_from('<B3sB3sII', b, 0x1BE)
                nx = struct.unpack_from('<B3sB3sII', b, 0x1CE)
                if l[2]:
                    out.append((n, l[2], cur + l[4], l[5]))
                    n += 1
                if not nx[2]:
                    break
                cur = ext + nx[4]
    return m, out


def has_ext(f, start):
    f.seek(start * 512 + 1024 + 56)
    return f.read(2) == bytes([0x53, 0xEF])


def main():
    img = sys.argv[1]
    f = open(img, 'rb')
    mbr, ps = parts(f)
    ok = True
    print('MBR:', 'LILO' if mbr[6:10] == b'LILO' else 'no LILO signature', 'sig', mbr[510:].hex())
    ok &= mbr[6:10] == b'LILO' and mbr[510:] == b'\x55\xaa'
    wsl_img = '/mnt/' + img[0].lower() + img[2:].replace('\\', '/')
    for n, t, start, size in ps:
        line = f'hda{n}: type {t:02x} start {start} size {size}'
        if t == 0x82:
            f.seek(start * 512 + 4086)
            sig = f.read(10)
            line += f'  swap: {sig!r}'
            ok &= sig == b'SWAPSPACE2'
        elif t == 0x83 and not has_ext(f, start):
            line += '  raw: no file system (the install script does not format it)'
        elif t == 0x83:
            r = subprocess.run(['wsl', 'e2fsck', '-fn', f'{wsl_img}?offset={start * 512}'],
                               capture_output=True, text=True, env={'MSYS_NO_PATHCONV': '1'} | dict(__import__('os').environ))
            last = [l for l in r.stdout.splitlines() if l.strip()][-1] if r.stdout.strip() else r.stderr.strip()
            clean = r.returncode == 0
            ok &= clean
            line += f'  e2fsck: {"clean" if clean else "ERRORS"} - {last}'
        print(line)
    print('RESULT:', 'PASS' if ok else 'FAIL')
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
