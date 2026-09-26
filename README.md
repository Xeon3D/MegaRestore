# MegaRestore

Builds a pristine, bootable hard disk image from **Merit Megatouch MAXX restore CD images** — no
emulator, no original hard disk needed. Point it at disc 1 (or any disc) of a set; the other discs
are found in the same folder. A set takes 10–20 seconds.

Part of the TouchPPBox preservation project (an 86Box fork that runs unmodified Megatouch disks).

## Supported restore sets

| Family | Sets | How it installs |
|---|---|---|
| DOS (D2000 "Disk Upgrade Kit") | Diamond V6.03, Double Diamond V7.01, Emerald V8.04/V8.06, Emerald 2 V9.01 | ROM-DOS partitions + FAT16, archive replayed in the installer's order |
| Linux (partimage + LILO) | Ruby, Ruby 2, Sapphire, Jade, Jade 2, Crown (MAXX cabinet profile) | the disc's own install scripts, interpreted: sfdisk, mke2fs 1.35, partimage 0.6.1, tar, LILO 21.6 |

Not restorable by design: MAXX 2000 upgrade discs (`.UDF` deltas need an existing install).
Damaged rips (zero-filled sectors) and discs from a different rip of the same release are detected
and reported instead of producing a broken disk.

## Use

Run `MegaRestore.exe`: **Select ISO…**, **Save as…**, pick the drive size (6.4 GB Quantum Fireball
ST6.4A by default), **Start**. Output is a raw (sparse) disk image for 86Box.

Command line (for testing): `MegaRestore.exe --cli [--size-cyl N] [--hash] [--keep] out.img disc1.iso [disc2.iso …]`

## Fidelity

Checked against a real Crown install run in an emulator: MBR (partition table + LILO first stage)
byte-identical, `/boot/map` identical except bytes LILO leaves uninitialised, every file identical
in content, mode, owner and size. DOS images: every archive file byte-exact; Diamond and Double
Diamond boot into the game. Details and every reversed format: [docs/10](docs/10-restore-tool.md),
[docs/11](docs/11-linux-restore.md).

## Build

.NET 10 SDK: `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`

`boot/factory-mbr.bin` and `boot/factory-vbr.bin` are factory boot code extracted from original
Megatouch drives by `scripts/p10-extract-bootcode.py` (the Diamond-era discs carry no boot loader
matching their DOS). `*.py`: reverse-engineering and verification helpers (ISO, archive, FAT and
partimage readers; `verify.py`, `verify_linux.py`).

For preservation and interoperability of hardware you own.
