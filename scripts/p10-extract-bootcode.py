"""Extract the factory ROM-DOS boot code that MegaRestore embeds (Docs/10).

    python scripts/p10-extract-bootcode.py "F:/TouchPPBox Folder/HDD Images" tools/MegaRestore/boot

factory-mbr.bin  446 B: MBR code of the factory MAXX drives (MAXX original, 2K, Emerald);
                 disk-signature bytes 0x1B8-0x1BD zeroed; partition table not included.
factory-vbr.bin  512 B: the "EB 40" DLDOS622 loader on every Diamond-era C: (MAXX 3.02 #2,
                 2K, 2K Plus, Diamond, Double Diamond); BPB 0x0B-0x3D zeroed (MegaRestore
                 writes its own). Needed because the Diamond-era discs carry no loader that
                 boots their IBMBIO.COM (their C:\\DOS FORMAT/SYS/FDISK are older; tested).
Checks that every listed source agrees before writing, prints SHA256 of inputs and outputs.
"""
import hashlib
import os
import sys

src, out = sys.argv[1], sys.argv[2]
MBR_SRC = ['MAXX (3.02)/Maxx original.img', 'MAXX 2K/Maxx 2K Image 1.img', 'MAXX Emerald/DOSEmerald.img']
VBR_SRC = ['MAXX (3.02)/Maxx original.img', 'MAXX 2K/Maxx 2K Image 1.img', 'MAXX Diamond/Diamond.img',
           'MAXX Double Diamond/Double Diamond.img']


def sha(b):
    return hashlib.sha256(b).hexdigest()


def sector(path, lba):
    with open(os.path.join(src, path), 'rb') as f:
        f.seek(lba * 512)
        return f.read(512)


mbrs = []
for p in MBR_SRC:
    m = bytearray(sector(p, 0)[:0x1BE])
    m[0x1B8:0x1BE] = bytes(6)
    mbrs.append(bytes(m))
    print(f'mbr  {sha(m)[:16]}  {p}')
vbrs = []
for p in VBR_SRC:
    v = bytearray(sector(p, 63))
    v[0x0B:0x3E] = bytes(0x33)
    vbrs.append(bytes(v))
    print(f'vbr  {sha(v)[:16]}  {p}')
if len(set(mbrs)) != 1 or len(set(vbrs)) != 1:
    sys.exit('sources disagree; not writing')
if vbrs[0][0:3] != b'\xeb\x40\x90' or vbrs[0][3:11] != b'DLDOS622' or vbrs[0][510:] != b'\x55\xaa':
    sys.exit('unexpected loader')
os.makedirs(out, exist_ok=True)
for name, data in (('factory-mbr.bin', mbrs[0]), ('factory-vbr.bin', vbrs[0])):
    with open(os.path.join(out, name), 'wb') as f:
        f.write(data)
    print(f'{sha(data)}  {name} ({len(data)} B)')
