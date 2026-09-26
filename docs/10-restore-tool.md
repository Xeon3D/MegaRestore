# 10 — MegaRestore: restore discs → pristine HDD image, without an emulator

Date: 2026-09-26. Scratch: `_work\p10\`. Tool: `tools/MegaRestore/` (C# WinForms, .NET 10),
published single-file to `F:\TouchPPBox Folder\Tools\MegaRestore\MegaRestore.exe`.
Python RE/verification twins: `tools/megarestore/` (`isofs.py`, `diskimg.py`, `fatread.py`, `verify.py`).

## Result

| Set | Build | `verify.py` (FAT + every archive file byte-exact) | Boots (scratch VM, 75 s) |
|---|---|---|---|
| Diamond V6.03 (PA0002) | ✓ | PASS, 9 234 files | **attract mode** (the original HDD image hangs) |
| Double Diamond V7.01 | ✓ | PASS, 9 396 files | **game menu** |
| Emerald V8.04 | ✓ | PASS, 9 941 files | ROM-DOS + AUTOEXEC, then stall after CDEX (= HANDOFF §5.2 machine issue) |
| Emerald 2 V9.01 | ✓ | PASS, 9 925 files | ROM-DOS, first-boot script runs (deletes `C:\TOOLS\USER`, copies Ghost) |
| Emerald V8.06 RevF (PA0033) | **refused** | — | disc 2 ISO damaged: 64 KB zero-filled at ISO offset 413 925 376 (bad rip) |
| MAXX 2000 upgrade discs | refused | — | `.UDF` delta patches (R01→…→5.00): need an existing install |
| Linux MAXX (Crown…Sapphire) | not yet | — | partimage + LILO, see below |

Each image builds in ~10 s (sparse file, only data written).

## The D2000 archive (`DISKIMG<drive>.NNN`)

Parts concatenated across discs (`.001` disc 1, `.002` disc 2 …; count and byte total in
`@INSTALL.DAT`). Records, no compression:
`u32 dir` (index into `@_DIRS_<d>.LST`) · 13 B name · `u32 size` · `u16 date` · `u16 time` ·
`u8 attr` · 3 B uninitialised struct padding (seen 00/B9/46/8200…; was first misread as a
marker, **corrected**) · data. Totals match `@INSTALL.DAT` exactly on every set.

## What the installer does (strings of D2000.EXE, both generations)

`RemoveAllFilesAndDirs` (keeps `@RESERVE.DAT`: `dialinfo.dat`, `NVRAM.DAT`) → `CreatDirs` (LST
order) → `InstallDiskImage` (archive order) → `REBOOT`. The Emerald-era D2000 also partitions
and formats a blank drive (`fdisk < keys.dat` / `fdisk2 /PRIO:2000`, `format C: /S /C /V`,
`format D: /C /V`, `ucmos1 R unicorn2.cms`); Diamond-era D2000 assumes a formatted drive.
MegaRestore replays this on a blank image: factory layout (C: FAT16 LBA 63 +4 192 902 active;
extended at 4 192 965 with logical D: +4 192 902; 255 heads; 32 KB clusters, 512 root entries),
system files first (as `FORMAT /S`), then all LST dirs, then all records. Dirs are stamped with
the disc's `DISKIMGC.001` date so the output is reproducible. Labels: C: `NO NAME`, D: blank
(as the one format-fresh factory disk, `maxx emerald 2.img`).
None of the factory HDD images is a clean D2000 result (all are file-copied, used machines),
so fidelity was checked by content + boot, not by whole-image hash.

## Boot code — what was tried

| Source | Result |
|---|---|
| Emerald-era discs: loader in root `FORMAT.COM` (`DLDOS622`, EB 3C) | byte-exact to factory Emerald VBRs; **used** |
| Diamond archive `C:\DOS\FORMAT.COM`/`SYS.COM` (`DLDOS6.0`) | older than the set's IBMBIO.COM: jumps into IBMBIO's header → hang. Its SYS parameter block (0x1E6 reloc seg, 0x1E8 entry, 0x1EA root LBA, 0x1EC/EE start, 0x1F0 count) was reversed from SYS.COM and implemented — still hangs. Dropped. |
| Diamond archive `C:\DOS\FDISK.COM` MBR | hangs. Dropped. |
| Emerald DLDOS622 loader on Diamond's IBMBIO | hangs |
| Factory Diamond-era loader (EB 40, identical on MAXX 3.02 #2, 2K, 2K Plus, Diamond, DD) | **boots** |

So MegaRestore embeds two blobs, extracted and cross-checked by
`scripts/p10-extract-bootcode.py`: `factory-mbr.bin` (`fc4e8030…`, the MBR on MAXX original, 2K
and Emerald drives) and `factory-vbr.bin` (`4d338adf…`, BPB zeroed). The disc's own loader wins
when present.

## Linux MAXX discs (not done)

isolinux → rescue Linux; `lib/images/partN_gz.NNN` = gzipped **partimage** volumes split across
discs; custom `/sbin/partimage` (Merit build) drives partitioning; `installboot.sh` mounts
hda5/hda1/hda7, untars `factorybackup/var.tar.gz` (+ `saved_var.tgz` from the last disc), runs
LILO in a chroot (`lilo.conf-install`, `lilo.conf.mbr`). A clone needs: partimage format reader,
ext2 writer for the var restore, and LILO map/boot-sector generation. `bin/installsize*` give
sizes per cabinet type.

## Open

- Emerald stall after CDEX: user's working (patched) Emerald 2 VM
  `F:\TouchPPBox Folder\Megatouch Emerald 2\` runs on `mb540n` + Pentium P54C + S3 Trio32, CD on
  primary slave — lead for HANDOFF §5.2 (patch may also bypass the probes).
- Emerald V8.06 needs a clean rip of disc 2 — it is also the likely genuine replacement for the
  tampered V8.05 HDD (HANDOFF §5.1).
- Linux restore support.
