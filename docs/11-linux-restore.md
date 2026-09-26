# 11 — MegaRestore: Linux MAXX restore discs

Date: 2026-09-26. Scratch: `_work\p11\`. Code: `tools/MegaRestore/` (`LinuxRestore.cs`,
`PartImage.cs`, `Ext2.cs`, `Mke2fs.cs`, `Lilo.cs`, `IsoReader.cs`); checker
`tools/megarestore/verify_linux.py`. Same exe/GUI as Docs/10.

## Result

| Set | Result |
|---|---|
| Crown V16.10 (reva rip and "1 of 3" rip) | built; checked against a real install (below); boots to Merit's runtime |
| Jade V14.00 | built, `verify_linux.py` PASS |
| Jade 2 V15.10 (Joliet-only names) | built, PASS; reproduces the disk-full truncation of `/root/checksum` |
| Ruby 2 "10.0" (raw 2352-byte sector ISOs) | built, PASS |
| Sapphire V12.01 (Joliet-only names) | built, PASS; MBR partition table byte-identical to the factory Sapphire 2 disk |
| Ruby V10.07 | **no disc 2**: `MAXX Ruby\maxx_ruby_d2.iso` is Ruby 2's disc 2 (same image id as `Ruby 2 Disc 2`) |
| Ruby Update / `ruby_force_2003_v10-02_update.iso` | update disc / an HTML page (failed download) |
| Crown LiveCD [Mike] | community Xubuntu disc, not Merit's |

Each set builds in 10–20 s.

## How a Merit Linux disc installs (disc 1 drives everything)

isolinux → kernel + initrd; initrd `/etc/rc` mounts the CD, **copies `lib/root/*` over `/` with
the initrd's own small `cp`** (ignores modes: files become 0755), mounts the CD on `/usr/local`,
runs `/sbin/setup.sh` → Merit's `/sbin/partimage` (UPX-packed partimage 0.6.1 + svgalib UI):
1. `/proc/pci` whitelist (else Error #-514 = 0x202): 82439TX + (Rage IIC | 264VT3) + 82371;
   82437VX + Trio 64 + 82371; VT8501 + VT82C686 + CyberBlade; 82801 + 82810 + CM8738.
   Needs 64 MB. Cabinet: `/sbin/cpu` > 300 MHz → Force, else MAXX (Crown/Jade/Jade 2 have
   `.force/.maxx` files; older discs have one profile).
2. `format.maxx|format.sh` (popen, no args): `dd` zero, `sfdisk -D -uM` heredoc, `mke2fs`, `tune2fs`, `mkswap`.
3. `bin/partitions[.maxx]`: device / image pairs → partimage restores.
4. `installboot.sh`: mounts, `tar -xpzf factorybackup/var.tar.gz` (Sapphire: `boot/var.tgz`),
   `chroot /install /setup2.sh` → `lilo.safe -C /etc/lilo.conf-install`, `-C /etc/lilo.conf.mbr`.
5. `finalize.maxx|finalize.sh` (Crown-era and Sapphire drivers run it; Ruby's doesn't).
Later discs only supply image volumes (+ marker file `2`, `3`…). Scripts from later discs are never used.

MegaRestore runs those same scripts through a strict interpreter (only the commands they use;
anything else stops the run) over a virtual file system (mounted ext2 volumes, CD, rescue root).

## Formats and behaviours reversed

- **Partimage 0.6.1**: per volume a gzip file; volume header 512 (u32 number @96, **u64 image id
  @100**); main header 16384+crc; MBRBACKUP; EXT000-009 (+u32 len); LOCALHEADER (u64 bsize, used,
  count, bitmap size); BITMAP; INFO; DATABLOCKS (used blocks, 16-byte `CHK\0` record per 64 KiB);
  TAIL. Volumes are matched by image id: two different Crown rips sit in one folder.
- **sfdisk 3.07 -D -uM** (255×63): MiB rounded **up** to cylinders; first partition and logicals
  start 63 sectors into their cylinder; bare `;` = all-default partition; EBR link entry spans
  to the end of the extended partition; CHS of logicals from the partition start.
- **mke2fs 1.35**: <512 MB → 1 KiB blocks / 4096 B per inode, else 4 KiB / 8192; ipg rounded to
  whole table blocks then to a multiple of 8; lost+found grown to 16 KiB (counter from 0); journal
  size 1024 / 4096 / 8192 blocks (<32 k, <256 k, else) **plus one extra mapped block**; features
  filetype+sparse_super(+has_journal); TEA hash + seed; max mount 20+jitter, 180-day interval;
  `tune2fs -c 0` stores −1.
- **mkswap (util-linux 2.10f)**: v1 header, `SWAPSPACE2`.
- **LILO 21.6**: first stage = boot.b[0..0x1B6) with CHS SECTOR_ADDRs; second stage (11 sectors)
  addressed in place in boot.b; map: default cmdline (0x6B6D), 2 descriptor sectors (checksum
  0xABCD^words), zero sector, keytable, per image fallback + options (`ro root=305`) sectors and
  101-entry address pages; `/boot/boot.0300` kept → timestamp 0; map written as `map~` then renamed.
  Source 21.6 is lost; built from Debian's 21.4.3 source (sha1 `ad27e6c4…`) + the real output.
- Kernel/tool quirks kept: tar can't time-stamp symlinks (creation time); `cp` onto an existing
  file keeps its inode; directory mtimes follow entry changes; **ENOSPC**: a redirected write
  stops at the last block that fits and the script goes on. Jade 2's finalize fills `/` and
  truncates `/root/checksum` (the factory Jade 2 disk shows the same: 629×4096 bytes, same
  line order; ours stops one block later — its factorybackup tarballs differ).

## Checked against a real install (Crown, reference VM)

Scratch VM `_work\p11\vm-ref`: **ASUS P/I-P55TVP4 (430VX) + S3 Trio64V2/DX 4 MB**, 64 MB, WinChip
200, DVD drive (`hldtst_8163`; the merged 1.2 GB ISO is DVD-sized), merged ISO (disc 1 tree + later
discs' images + markers) → real installer ran to completion, rebooted into Crown (disk kept as
`_work\p11\ref-crown.img`; first boot's hardware check had changed /var, /etc a little).
MegaRestore vs reference: **MBR (partition table + LILO first stage) byte-identical**; EBRs
identical; `/boot/map` identical except the 26 bytes LILO leaves uninitialised (stack garbage) and
their checksum, on the same blocks; hda6/hda7 fresh file systems: superblocks, journals and
lost+found identical (bar UUID/times); every file on hda1/hda5/hda7 identical in content, mode,
owner and size except files first boot wrote. The built disk boots (LILO → kernel → rc scripts).

## Open

- Linux MAXX runtime on the emulator (HANDOFF §5.7): the Crown image stops at "Detecting Force
  modem" on the VX test VM. For the whitelist, the HP Pavilion 81xx board (`tx97xv`, 430TX +
  onboard 264VT3) is an exact match of the MAXX row and a TX97 sibling — candidate MAXX machine.
- Force cabinet profile (`resize2fs`) not implemented (MAXX only).
- Ruby V10.07 needs its real disc 2.
