using System.Text;

namespace MegaRestore;

/// <summary>
/// Replays a Merit D2000 restore (DOS MAXX: Diamond, Double Diamond, Emerald, Emerald 2) onto
/// a new disk image, as the installer would on a blank drive (Docs/10):
///   1. partition like the factory MAXX drives: C: primary FAT16 at LBA 63, 4 192 902 sectors,
///      active; extended partition with logical D: of the same size (255 heads, 63 sectors);
///   2. FORMAT C: /S (system files first), FORMAT D:;
///   3. D2000 CreatDirs: every directory of @_DIRS_&lt;d&gt;.LST, in list order;
///   4. D2000 InstallDiskImage: every archive record, in archive order.
/// Boot code: the MBR is Merit's factory one (same on MAXX original, 2K and Emerald drives);
/// the boot sector is the loader in the disc's own ROM-DOS FORMAT.COM when the set has one
/// (Emerald era, byte-exact to the factory drives), else the factory Diamond-era loader. The
/// Diamond-era discs' C:\DOS tools are older than their IBMBIO.COM and do not boot it (tested).
/// </summary>
public sealed class DosRestore
{
    public const uint PartSectors = 4_192_902;       // factory C: and D: (cylinders 0-260 / 261-521)
    public const uint CStart = 63, ExtStart = 4_192_965, ExtSectors = 4_192_965;
    public const byte Heads = 255, Spt = 63;

    readonly DiscSet _set;
    readonly Action<string> _log;
    readonly Action<double> _progress;
    readonly CancellationToken _ct;
    byte[]? _mbrCode, _vbr;
    long _done, _total;

    public DosRestore(DiscSet set, Action<string> log, Action<double> progress, CancellationToken ct)
    {
        _set = set; _log = log; _progress = progress; _ct = ct;
    }

    public DateTime InstallTime { get; private set; }

    public void Run(string outPath, long diskSectors)
    {
        if (diskSectors < ExtStart + ExtSectors)
            throw new ArgumentException($"The disk must hold at least {(ExtStart + ExtSectors) * 512L / 1_000_000} MB.");

        string? installDat = _set.Discs.Select(d => d.ReadText("@INSTALL.DAT")).FirstOrDefault(t => t != null);
        if (installDat == null) throw new InvalidDataException("@INSTALL.DAT not found: this is not a complete D2000 restore set.");
        var drives = D2000Archive.ParseInstallDat(installDat);
        if (!drives.ContainsKey('C')) throw new InvalidDataException("@INSTALL.DAT lists no C: image.");
        _log($"Release: {_set.Title}   ({string.Join(", ", drives.Select(kv => $"{kv.Key}: {kv.Value.Bytes:N0} bytes in {kv.Value.Parts} part(s)"))})");

        var parts = drives.ToDictionary(kv => kv.Key, kv => ArchiveParts(kv.Key, kv.Value.Parts));
        _total = parts.Values.SelectMany(p => p).Sum(p => p.e.Size);
        var c1 = parts['C'][0];
        InstallTime = c1.e.Date;
        _log($"Install clock set to the disc's date: {InstallTime:yyyy-MM-dd HH:mm}");

        _mbrCode = Resource("factory-mbr.bin");
        FindDiscBootSector();
        if (_vbr == null)
        {
            _vbr = Resource("factory-vbr.bin");
            _log("Boot sector: factory ROM-DOS 6.22 loader (the discs carry none for this IBMBIO.COM)");
        }

        Restore.CheckSpace(outPath, _total + _total / 10, _log);
        using var img = new FileStream(outPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        Sparse.TryMark(img);
        img.SetLength(diskSectors * 512);

        var c = new Fat16Builder(img, CStart, PartSectors, CStart);
        var d = new Fat16Builder(img, ExtStart + 63, PartSectors, 63);
        long cBytes = Install(c, 'C', parts['C'], systemFirst: true);
        long dBytes = drives.ContainsKey('D') ? Install(d, 'D', parts['D'], systemFirst: false) : 0;

        c.Finish(_vbr, "NO NAME", Fat16Builder.Serial(InstallTime), Heads, Spt);
        d.Finish(_vbr, "", Fat16Builder.Serial(InstallTime.AddMinutes(1)), Heads, Spt);
        WritePartitionTables(img);
        img.Flush(true);

        foreach (var (drv, n) in new[] { ('C', cBytes), ('D', dBytes) })
            if (drives.TryGetValue(drv, out var want) && want.Bytes != n)
                throw new InvalidDataException($"{drv}: wrote {n:N0} bytes but @INSTALL.DAT expects {want.Bytes:N0}.");
        _log($"C: {cBytes:N0} bytes, {c.FreeBytes / 1048576:N0} MB free.  D: {dBytes:N0} bytes.");
    }

    List<(IsoReader iso, IsoReader.Entry e)> ArchiveParts(char drive, int count)
    {
        var list = new List<(IsoReader, IsoReader.Entry)>();
        for (int n = 1; n <= count; n++)
        {
            string name = $"DISKIMG{drive}.{n:000}";
            var hit = _set.Discs.Select(iso => (iso, e: iso.Find(name))).FirstOrDefault(x => x.e != null);
            if (hit.e == null) throw new FileNotFoundException($"{name} is missing from the discs.");
            list.Add((hit.iso, hit.e));
        }
        return list;
    }

    static byte[] Resource(string name)
    {
        using var r = typeof(DosRestore).Assembly.GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException($"Missing embedded {name}.");
        using var m = new MemoryStream();
        r.CopyTo(m);
        return m.ToArray();
    }

    void FindDiscBootSector()
    {
        foreach (var iso in _set.Discs)
        {
            var f = iso.Find("FORMAT.COM");
            if (f != null && (_vbr = BootSectorFromFormat(iso.ReadFile(f))) != null)
            {
                _log($"Boot sector: ROM-DOS FORMAT.COM on {Path.GetFileName(iso.Path)}");
                return;
            }
        }
    }

    /// <summary>ROM-DOS FORMAT/SYS carry their boot sector as a 512-byte block, OEM "DLDOS".</summary>
    public static byte[]? BootSectorFromFormat(byte[] exe)
    {
        var key = "DLDOS"u8.ToArray();
        for (int i = exe.AsSpan().IndexOf(key); i >= 3; )
        {
            int s = i - 3;
            if (exe[s] == 0xEB && s + 512 <= exe.Length) return exe.AsSpan(s, 512).ToArray();
            int nxt = exe.AsSpan(i + 1).IndexOf(key);
            if (nxt < 0) break;
            i += 1 + nxt;
        }
        return null;
    }

    long Install(Fat16Builder fs, char drive, List<(IsoReader iso, IsoReader.Entry e)> parts, bool systemFirst)
    {
        var lst = _set.Discs.Select(d => d.ReadText($"@_DIRS_{drive}.LST")).FirstOrDefault(t => t != null)
                  ?? throw new FileNotFoundException($"@_DIRS_{drive}.LST not found.");
        var paths = lst.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var byIndex = new Fat16Builder.Dir?[paths.Count];
        var byPath = new Dictionary<string, Fat16Builder.Dir>(StringComparer.OrdinalIgnoreCase);
        var subdirs = new List<Fat16Builder.Dir>();
        ushort dirTime = Fat16Builder.DosTime(InstallTime), dirDate = Fat16Builder.DosDate(InstallTime);
        bool dirsMade = false;

        void CreatDirs()
        {
            for (int i = 0; i < paths.Count; i++)
            {
                string p = paths[i].TrimEnd('\\');
                if (p.Length <= 2) { byIndex[i] = fs.Root; byPath[p] = fs.Root; continue; }   // "C:\"
                int cut = p.LastIndexOf('\\');
                string parentPath = p[..cut];
                var parent = parentPath.Length <= 2 ? fs.Root
                    : byPath.TryGetValue(parentPath, out var pd) ? pd
                    : throw new InvalidDataException($"@_DIRS_{drive}.LST lists {p} before its parent.");
                var d = fs.MakeDir(parent, p[(cut + 1)..], dirTime, dirDate);
                byIndex[i] = d; byPath[p] = d; subdirs.Add(d);
            }
            dirsMade = true;
        }

        var sysNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IBMBIO.COM", "IBMDOS.COM", "COMMAND.COM" };
        long bytes = 0;
        _log($"Installing {drive}: from {string.Join(" + ", parts.Select(p => p.e.Name))}");
        using var s = new ConcatStream(parts.Select(p => (Func<Stream>)(() => p.iso.Open(p.e))));
        while (NextHeader(s, parts) is { } r)
        {
            _ct.ThrowIfCancellationRequested();
            bool sys = systemFirst && !dirsMade && r.Dir == 0 && sysNames.Contains(r.Name);
            if (!dirsMade && !sys) CreatDirs();
            var dir = r.Dir == 0 && !dirsMade ? fs.Root
                : r.Dir >= 0 && r.Dir < byIndex.Length && byIndex[r.Dir] != null ? byIndex[r.Dir]!
                : throw new InvalidDataException($"{r.Name}: directory index {r.Dir} is not in @_DIRS_{drive}.LST.");

            fs.WriteFile(dir, r.Name, r.Attr, r.Time, r.Date, r.Size, s, n =>
            {
                _done += n;
                _progress(_total == 0 ? 1 : (double)_done / _total);
            });
            bytes += r.Size;
        }
        if (!dirsMade) CreatDirs();
        foreach (var d in subdirs) fs.FlushDir(d);
        _log($"{drive}: {subdirs.Count} directories, {bytes:N0} bytes of files.");
        return bytes;
    }

    /// <summary>Reads a header; on a damaged archive names the disc and the offset.</summary>
    static D2000Archive.Record? NextHeader(ConcatStream s, List<(IsoReader iso, IsoReader.Entry e)> parts)
    {
        long at = s.Consumed;
        try { return D2000Archive.ReadHeader(s); }
        catch (DamagedArchiveException ex)
        {
            foreach (var (iso, e) in parts)
            {
                if (at < e.Size)
                    throw new InvalidDataException(
                        $"{e.Name} on {Path.GetFileName(iso.Path)} is damaged at byte {at:N0} (ISO offset {e.Lba * IsoReader.Sector + at:N0}): {ex.Message}. " +
                        (ex.Blank ? "Unreadable sectors are usually zero-filled when a scratched disc is ripped; " : "") +
                        "re-rip that disc or use another copy of the ISO.");
                at -= e.Size;
            }
            throw;
        }
    }

    static byte[] Chs(uint lba)
    {
        uint cyl = lba / (Heads * (uint)Spt), rem = lba % (Heads * (uint)Spt);
        uint head = rem / Spt, sec = rem % Spt + 1;
        if (cyl > 1023) { cyl = 1023; head = 254; sec = 63; }
        return new[] { (byte)head, (byte)(sec | (cyl >> 2 & 0xC0)), (byte)cyl };
    }

    static void PutEntry(byte[] sector, int slot, byte boot, byte type, uint start, uint count, uint absStart)
    {
        int o = 0x1BE + slot * 16;
        sector[o] = boot;
        Chs(absStart).CopyTo(sector, o + 1);
        sector[o + 4] = type;
        Chs(absStart + count - 1).CopyTo(sector, o + 5);
        BitConverter.TryWriteBytes(sector.AsSpan(o + 8), start);
        BitConverter.TryWriteBytes(sector.AsSpan(o + 12), count);
    }

    void WritePartitionTables(Stream img)
    {
        var mbr = new byte[512];
        _mbrCode!.CopyTo(mbr, 0);
        PutEntry(mbr, 0, 0x80, 0x06, CStart, PartSectors, CStart);
        PutEntry(mbr, 1, 0x00, 0x05, ExtStart, ExtSectors, ExtStart);
        mbr[510] = 0x55; mbr[511] = 0xAA;
        img.Seek(0, SeekOrigin.Begin); img.Write(mbr);

        var ebr = new byte[512];
        PutEntry(ebr, 0, 0x00, 0x06, 63, PartSectors, ExtStart + 63);
        ebr[510] = 0x55; ebr[511] = 0xAA;
        img.Seek(ExtStart * 512L, SeekOrigin.Begin); img.Write(ebr);
    }
}

static class Sparse
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, IntPtr inBuf, uint inSize,
                                       IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);

    /// <summary>Marks the new image sparse on NTFS so unwritten space costs nothing.</summary>
    public static void TryMark(FileStream f)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { DeviceIoControl(f.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); } catch { }
    }
}
