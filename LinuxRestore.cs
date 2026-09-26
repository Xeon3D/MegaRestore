using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MegaRestore;

/// <summary>
/// Replays a Merit Linux MAXX restore (Ruby, Sapphire, Jade, Jade 2, Crown, Ruby 2) onto a new
/// disk image, by interpreting the disc's own install scripts (Docs/11):
///   format script (dd, sfdisk -D -uM, mke2fs/tune2fs/mkswap) → partimage restores listed in
///   the partitions file → installboot.sh (tar of the factory /var, LILO in a chroot) →
///   finalize script. Scripts and tools come from disc 1's lib/root, as the rescue CD's /etc/rc
///   copies them; later discs only supply image volumes. The cabinet profile is MAXX (the
///   Crown-era driver picks "force" only for CPUs above 300 MHz).
/// The interpreter knows exactly the commands these scripts use and stops on anything else.
/// </summary>
public sealed class LinuxRestore
{
    public const int Heads = 255, Spt = 63, Cyl = Heads * Spt;

    readonly DiscSet _set;
    readonly Action<string> _log;
    readonly Action<double> _progress;
    readonly CancellationToken _ct;
    readonly IsoReader _disc1;
    Stream _img = Stream.Null;
    long _diskSectors;
    readonly Dictionary<int, (long start, long size, byte type)> _parts = new();
    readonly Dictionary<int, Ext2> _fsCache = new();
    readonly List<(string point, Ext2 fs, int part)> _mounts = new();
    readonly HashSet<string> _rescueDirs = new() { "/", "/install", "/mnt", "/tmp", "/proc" };
    readonly Dictionary<string, byte[]> _rescueFiles = new();
    string _root = "/";               // chroot
    string _cwd = "/";
    uint _now;
    long _done, _total;

    public LinuxRestore(DiscSet set, Action<string> log, Action<double> progress, CancellationToken ct)
    {
        _set = set; _log = log; _progress = progress; _ct = ct; _disc1 = set.Discs[0];
    }

    // ================================================================ entry point

    public void Run(string outPath, long diskSectors)
    {
        _diskSectors = diskSectors;
        string version = _disc1.ReadText("bin/version")?.Split('\n')[0].Trim() ?? _set.Title;
        bool profiles = _disc1.Find("bin/partitions.maxx") != null;
        string partitionsFile = profiles ? "bin/partitions.maxx" : "bin/partitions";
        string formatScript = profiles ? "/sbin/format.maxx" : "/sbin/format.sh";
        string? finalizeScript = profiles ? "/sbin/finalize.maxx" : RescueExists("/sbin/finalize.sh") ? "/sbin/finalize.sh" : null;
        _log($"Release: {version}   (Linux; {(profiles ? "MAXX cabinet profile" : "single profile")})");

        var pairs = (_disc1.ReadText(partitionsFile) ?? throw new FileNotFoundException(partitionsFile))
            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var restores = new List<(string dev, List<(IsoReader, IsoReader.Entry)> vols)>();
        for (int k = 0; k + 1 < pairs.Count; k += 2)
            restores.Add((pairs[k], ImageVolumes(pairs[k + 1])));
        _total = restores.Sum(r => r.vols.Sum(v => v.Item2.Size));
        _now = (uint)(new DateTimeOffset(DateTime.SpecifyKind(_disc1.Find(pairs[1].Replace("/usr/local/", ""))!.Date, DateTimeKind.Utc)).ToUnixTimeSeconds());
        _log($"Install clock set to the image date: {DateTimeOffset.FromUnixTimeSeconds(_now):yyyy-MM-dd HH:mm} UTC");

        Restore.CheckSpace(outPath, _total * 5 / 2, _log);
        using var img = new FileStream(outPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        _img = img;
        Sparse.TryMark(img);
        img.SetLength(diskSectors * 512);

        _log($"Formatting ({formatScript})");
        RunScript(formatScript, Array.Empty<string>());
        foreach (var (dev, vols) in restores)
        {
            _log($"Restoring {dev} from {string.Join(" + ", vols.Select(v => v.Item2.Name))}");
            RestorePartition(dev, vols);
        }
        _log("Installing boot loader (installboot.sh)");
        RunScript("/sbin/installboot.sh", Array.Empty<string>());
        if (finalizeScript != null)
        {
            _log($"Finalizing ({finalizeScript})");
            RunScript(finalizeScript, Array.Empty<string>());
        }
        foreach (var fs in _fsCache.Values) fs.Flush();
        img.Flush(true);
        _log("Done.");
    }

    List<IsoReader>? _pool;

    /// <summary>The picked discs, then every other ISO in the same folder (opened once).</summary>
    IEnumerable<IsoReader> Pool()
    {
        if (_pool == null)
        {
            _pool = new List<IsoReader>(_set.Discs);
            var dir = Path.GetDirectoryName(Path.GetFullPath(_disc1.Path))!;
            foreach (var f in Directory.EnumerateFiles(dir, "*.iso").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (_pool.Any(d => string.Equals(Path.GetFullPath(d.Path), Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase))) continue;
                try { var iso = new IsoReader(f); if (iso.Find("lib/images") != null) _pool.Add(iso); else iso.Dispose(); }
                catch (Exception) { }
            }
        }
        return _pool;
    }

    internal static (uint no, ulong id)? VolumeId(IsoReader iso, IsoReader.Entry e)
    {
        try
        {
            using var gz = new GZipStream(iso.Open(e), CompressionMode.Decompress);
            var h = new byte[512];
            gz.ReadExactly(h);
            if (Encoding.ASCII.GetString(h, 0, 16) != "PaRtImAgE-VoLuMe") return null;
            return (BitConverter.ToUInt32(h, 96), BitConverter.ToUInt64(h, 100));
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// "/usr/local/lib/images/part5.gz.000" → volumes .000, .001, ... Later volumes may be on other
    /// discs; they are matched by the 8-byte image identifier in each partimage volume header,
    /// so two different rips of a release in one folder are never mixed.
    /// </summary>
    List<(IsoReader, IsoReader.Entry)> ImageVolumes(string firstPath)
    {
        string rel = firstPath.Replace("/usr/local/", "");
        string stem = rel[..^3];
        var first = _disc1.Find(rel) ?? throw new FileNotFoundException($"{rel} is not on disc 1.");
        var id = VolumeId(_disc1, first) ?? throw new InvalidDataException($"{rel} is not a partimage volume.");
        var list = new List<(IsoReader, IsoReader.Entry)> { (_disc1, first) };
        for (uint n = 1; n < 100; n++)
        {
            string name = stem + n.ToString("000");
            (IsoReader, IsoReader.Entry)? hit = null;
            bool anyName = false;
            foreach (var iso in Pool())
            {
                var e = iso.Find(name);
                if (e == null) continue;
                anyName = true;
                var v = VolumeId(iso, e);
                if (v is { } vv && vv.no == n && vv.id == id.id) { hit = (iso, e); break; }
            }
            if (hit == null)
            {
                if (anyName) throw new FileNotFoundException($"{name} was found, but only from a different rip of this release. Put the matching discs together.");
                break;       // no more volumes: the image ends here (verified by the partimage tail)
            }
            if (n == 1 || hit.Value.Item1 != list[^1].Item1) _log($"  {name}: {Path.GetFileName(hit.Value.Item1.Path)}");
            list.Add(hit.Value);
        }
        return list;
    }

    // ================================================================ partitions and devices

    static int PartNo(string dev)
    {
        var m = Regex.Match(dev, @"^/dev/hda(\d+)$");
        if (!m.Success) throw new NotSupportedException($"Device {dev} is not supported.");
        return int.Parse(m.Groups[1].Value);
    }

    (long start, long size) Part(int n) =>
        _parts.TryGetValue(n, out var p) ? (p.start, p.size) : throw new InvalidOperationException($"/dev/hda{n} does not exist.");

    Ext2 Fs(int n)
    {
        if (!_fsCache.TryGetValue(n, out var fs))
        {
            var (start, _) = Part(n);
            _fsCache[n] = fs = Ext2.Open(_img, start * 512);
        }
        fs.Now = _now;
        return fs;
    }

    void RestorePartition(string dev, List<(IsoReader iso, IsoReader.Entry e)> vols)
    {
        int n = PartNo(dev);
        var (start, size) = Part(n);
        _fsCache.Remove(n);
        var info = PartImage.Restore(vols.Select(v => (Func<Stream>)(() => v.iso.Open(v.e))), (blk, data) =>
        {
            long pos = start * 512 + blk * data.Length;
            if (pos + data.Length > (start + size) * 512) throw new IOException($"{dev} is too small for its image.");
            _img.Seek(pos, SeekOrigin.Begin);
            _img.Write(data);
        }, null, _ct);
        _done += vols.Sum(v => v.e.Size);
        _progress(_total == 0 ? 1 : (double)_done / _total);
        _log($"  {info.FileSystem}, {info.UsedBlocks:N0} of {info.BlockCount:N0} blocks of {info.BlockSize} bytes");
    }

    /// <summary>
    /// sfdisk 3.07 -D -uM --DOS-extended: sizes in MiB rounded up to whole cylinders (255×63);
    /// the first partition and every logical partition start one track (63 sectors) into their
    /// cylinder; empty size = rest of the disk; ",,E" is the extended partition.
    /// </summary>
    void Sfdisk(List<string> lines)
    {
        long totalCyl = _diskSectors / Cyl;
        long next = 0;                        // next free cylinder
        int slot = 1;
        long extStart = -1, extEnd = -1;
        var mbr = new byte[512];
        _img.Seek(0, SeekOrigin.Begin); _img.ReadExactly(mbr);
        Array.Clear(mbr, 0x1BE, 64);
        var logicals = new List<(long start, long size, byte type)>();
        foreach (var raw in lines)
        {
            if (raw.Trim().StartsWith('#') || raw.Trim().Length == 0) continue;
            var line = raw.Trim().TrimEnd(';');          // a bare ";" = a partition with all defaults
            var f = line.Split(',');
            string sStart = f.Length > 0 ? f[0].Trim() : "", sSize = f.Length > 1 ? f[1].Trim() : "",
                   sType = f.Length > 2 ? f[2].Trim() : "L", sBoot = f.Length > 3 ? f[3].Trim() : "";
            byte type = sType switch { "L" or "" => 0x83, "S" => 0x82, "E" => 0x05, _ => Convert.ToByte(sType, 16) };
            bool inExt = extStart >= 0 && type != 0x05;
            long startCyl = sStart.Length > 0 ? CylFromMb(long.Parse(sStart), false) : next;
            if (inExt && startCyl < extStart) startCyl = extStart;
            long endCyl = sSize.Length > 0 ? startCyl + CylFromMb(long.Parse(sSize), true) : (inExt ? extEnd : totalCyl);
            if (endCyl > totalCyl) throw new IOException("The disk is too small for the partition layout.");
            next = endCyl;
            if (type == 0x05)
            {
                extStart = startCyl; extEnd = endCyl;
                PutEntry(mbr, slot - 1, 0, type, startCyl * Cyl, (endCyl - startCyl) * Cyl);
                _parts[slot] = (startCyl * Cyl, (endCyl - startCyl) * Cyl, type);
                slot++;
                next = startCyl;
                continue;
            }
            if (inExt)
            {
                logicals.Add((startCyl * Cyl, (endCyl - startCyl) * Cyl, type));
                continue;
            }
            long s0 = startCyl == 0 ? Spt : startCyl * Cyl;
            PutEntry(mbr, slot - 1, sBoot == "*" ? (byte)0x80 : (byte)0, type, s0, endCyl * Cyl - s0);
            _parts[slot] = (s0, endCyl * Cyl - s0, type);
            slot++;
        }
        mbr[510] = 0x55; mbr[511] = 0xAA;
        _img.Seek(0, SeekOrigin.Begin); _img.Write(mbr);
        // Extended chain: an EBR at the start of every logical slot.
        for (int k = 0; k < logicals.Count; k++)
        {
            var (ls, lsize, lt) = logicals[k];
            var ebr = new byte[512];
            PutEntry(ebr, 0, 0, lt, Spt, lsize - Spt, ls + Spt);
            if (k + 1 < logicals.Count)
            {
                // sfdisk: the link runs from the next EBR to the end of the extended partition.
                var nx = logicals[k + 1];
                PutEntry(ebr, 1, 0, 0x05, nx.start - extStart * Cyl, extEnd * Cyl - nx.start, nx.start);
            }
            ebr[510] = 0x55; ebr[511] = 0xAA;
            _img.Seek(ls * 512, SeekOrigin.Begin); _img.Write(ebr);
            _parts[5 + k] = (ls + Spt, lsize - Spt, lt);
        }
        foreach (var kv in _parts.OrderBy(k => k.Key))
            _log($"  hda{kv.Key}: type {kv.Value.type:x2}, start {kv.Value.start}, {kv.Value.size * 512L / 1048576} MB");
    }

    static long CylFromMb(long mb, bool roundUp)
    {
        long sectors = mb * 2048;
        return roundUp ? (sectors + Cyl - 1) / Cyl : sectors / Cyl;
    }

    static byte[] Chs(long lba)
    {
        long cyl = lba / Cyl, rem = lba % Cyl, head = rem / Spt, sec = rem % Spt + 1;
        if (cyl > 1023) { cyl = 1023; head = Heads - 1; sec = Spt; }
        return new[] { (byte)head, (byte)(sec | (cyl >> 2 & 0xC0)), (byte)cyl };
    }

    /// <summary>start is table-relative, abs is the absolute LBA used for the CHS fields.</summary>
    static void PutEntry(byte[] s, int slot, byte boot, byte type, long start, long size, long abs = -1)
    {
        if (abs < 0) abs = start;
        int o = 0x1BE + slot * 16;
        s[o] = boot;
        Chs(abs).CopyTo(s, o + 1);
        s[o + 4] = type;
        Chs(abs + size - 1).CopyTo(s, o + 5);
        BitConverter.TryWriteBytes(s.AsSpan(o + 8), (uint)start);
        BitConverter.TryWriteBytes(s.AsSpan(o + 12), (uint)size);
    }

    /// <summary>util-linux 2.10f mkswap: new-style (v1) header in the first 4 KiB page.</summary>
    void Mkswap(int n)
    {
        var (start, size) = Part(n);
        long pages = size * 512 / 4096;
        var page = new byte[4096];
        BitConverter.TryWriteBytes(page.AsSpan(1024), 1u);
        BitConverter.TryWriteBytes(page.AsSpan(1028), (uint)(pages - 1));
        Encoding.ASCII.GetBytes("SWAPSPACE2").CopyTo(page, 4086);
        _img.Seek(start * 512, SeekOrigin.Begin);
        _img.Write(page);
    }

    // ================================================================ VFS

    string Abs(string p)
    {
        if (!p.StartsWith('/')) p = (_cwd.TrimEnd('/') + "/" + p);
        var parts = new List<string>();
        foreach (var s in p.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (s == ".") continue;
            if (s == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(s);
        }
        return "/" + string.Join('/', parts);
    }

    /// <summary>Absolute path (as the script sees it, inside the current chroot) → real path.</summary>
    string Real(string p)
    {
        var a = Abs(p);
        return _root == "/" ? a : (_root.TrimEnd('/') + (a == "/" ? "" : a));
    }

    /// <summary>Which volume holds a real path: (fs, path inside it) or null for the rescue root / CD.</summary>
    (Ext2 fs, string inner, int part)? Locate(string real)
    {
        foreach (var (point, fs, part) in _mounts.OrderByDescending(m => m.point.Length))
            if (real == point || real.StartsWith(point.TrimEnd('/') + "/"))
                return (fs, real.Length == point.Length ? "/" : real[point.TrimEnd('/').Length..], part);
        return null;
    }

    bool RescueExists(string real) =>
        _rescueFiles.ContainsKey(real) || _rescueDirs.Contains(real) || RescueIso(real) != null;

    IsoReader.Entry? RescueIso(string real)
    {
        if (real.StartsWith("/usr/local/")) return _disc1.Find(real["/usr/local/".Length..]);
        return _disc1.Find("lib/root" + real);
    }

    /// <summary>Follows symbolic links in the last path component (open() semantics), up to 8 levels.</summary>
    string Follow(string p)
    {
        p = Abs(p);
        for (int n = 0; n < 8; n++)
        {
            if (Locate(Real(p)) is not { } loc) return p;
            uint ino = loc.fs.Resolve(loc.inner);
            if (ino == 0) return p;
            var inode = loc.fs.ReadInode(ino);
            if ((Ext2.Mode(inode) & Ext2.S_IFMT) != Ext2.S_IFLNK) return p;
            string target = Encoding.Latin1.GetString(loc.fs.ReadData(ino));
            p = target.StartsWith('/') ? target : Abs(p[..(p.LastIndexOf('/') + 1)] + target);
        }
        throw new IOException($"Too many levels of symbolic links: {p}");
    }

    byte[] ReadFile(string p)
    {
        p = Follow(p);
        string real = Real(p);
        if (Locate(real) is { } loc)
        {
            uint ino = loc.fs.Resolve(loc.inner);
            if (ino == 0) throw new FileNotFoundException(p);
            return loc.fs.ReadData(ino);
        }
        if (_rescueFiles.TryGetValue(real, out var b)) return b;
        var e = RescueIso(real) ?? throw new FileNotFoundException(p);
        return _disc1.ReadFile(e);
    }

    /// <summary>
    /// Permission bits of a file. Rescue-root files are 0755 whatever the CD says: /etc/rc copies
    /// lib/root with the initrd's minimal cp, which ignores -p (verified on a real install:
    /// /etc/lilo.conf.mbr comes out 0755 although the CD stores 0444).
    /// </summary>
    int PermOf(string p)
    {
        p = Follow(p);
        string real = Real(p);
        if (Locate(real) is { } loc) return Ext2.Mode(loc.fs.ReadInode(loc.fs.Resolve(loc.inner))) & 0xFFF;
        return 0x1ED;
    }

    bool Exists(string p)
    {
        string real = Real(p);
        if (Locate(real) is { } loc) return loc.fs.Resolve(loc.inner) != 0;
        return RescueExists(real);
    }

    (Ext2 fs, uint dir, string name)? Parent(string p)
    {
        string real = Real(p);
        var loc = Locate(real);
        if (loc == null) return null;
        var inner = loc.Value.inner;
        int cut = inner.LastIndexOf('/');
        string dir = cut <= 0 ? "/" : inner[..cut], name = inner[(cut + 1)..];
        uint d = loc.Value.fs.Resolve(dir);
        if (d == 0) throw new DirectoryNotFoundException(p);
        return (loc.Value.fs, d, name);
    }

    /// <summary>
    /// Writes a file as a process would: on a full volume the data is cut at the last block that
    /// fit and the script goes on (the Jade 2 finalize truncates /root/checksum this way on a real
    /// install; the factory image shows it).
    /// </summary>
    void WriteFile(string p, byte[] data, ushort perm = 0x1A4)
    {
        var par = Parent(p);
        if (par == null) { _rescueFiles[Real(p)] = data; return; }
        var (fs, dir, name) = par.Value;
        uint ino = fs.Lookup(dir, name);
        if (ino != 0) fs.Overwrite(ino, new MemoryStream(data), data.Length, partialOnFull: true);
        else ino = fs.CreateFile(dir, name, perm, 0, 0, _now, _now, new MemoryStream(data), data.Length, partialOnFull: true);
        long stored = Ext2.Size(fs.ReadInode(ino));
        if (stored < data.Length) _log($"  {p}: file system full after {stored:N0} of {data.Length:N0} bytes (as on a real install)");
    }

    void MkdirP(string p)
    {
        string a = Abs(p), cur = "";
        foreach (var s in a.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cur += "/" + s;
            if (Exists(cur)) continue;
            var par = Parent(cur);
            if (par == null) { _rescueDirs.Add(Real(cur)); continue; }
            par.Value.fs.Mkdir(par.Value.dir, par.Value.name, 0x1ED, 0, 0, _now);
        }
    }

    void Remove(string p, bool recursive)
    {
        var par = Parent(p);
        if (par == null) { _rescueFiles.Remove(Real(p)); _rescueDirs.Remove(Real(p)); return; }
        par.Value.fs.Remove(par.Value.dir, par.Value.name, recursive);
    }

    void Mount(string dev, string dir, string type)
    {
        if (type == "proc" || dev == "proc" || type == "usbdevfs") return;
        int n = PartNo(dev);
        string real = Real(dir);
        if (!Exists(dir)) throw new DirectoryNotFoundException($"mount point {dir}");
        var fs = Fs(n);
        fs.Mounted();
        _mounts.Add((real, fs, n));
    }

    void Umount(string dir)
    {
        string real = Real(dir);
        int i = _mounts.FindIndex(m => m.point == real);
        if (i < 0) return;                        // proc and friends
        _mounts[i].fs.Unmount();
        _mounts.RemoveAt(i);
    }

    // ================================================================ tar

    Dictionary<string, int>? _passwd, _group;

    int IdFor(string? name, int numeric, bool user)
    {
        _passwd ??= ParseIds(ReadRescueText("/etc/passwd"));
        _group ??= ParseIds(ReadRescueText("/etc/group"));
        var map = user ? _passwd : _group;
        return name != null && map.TryGetValue(name, out int id) ? id : numeric;
    }

    string ReadRescueText(string p)
    {
        var e = RescueIso(p);
        return e == null ? "" : Encoding.Latin1.GetString(_disc1.ReadFile(e));
    }

    static Dictionary<string, int> ParseIds(string text)
    {
        var d = new Dictionary<string, int>();
        foreach (var l in text.Split('\n'))
        {
            var f = l.Split(':');
            if (f.Length > 2 && int.TryParse(f[2], out int id)) d.TryAdd(f[0], id);
        }
        return d;
    }

    /// <summary>GNU tar -x[p]zf as root: owners by name through the rescue system's passwd, then numeric.</summary>
    void TarExtract(string archive)
    {
        var data = ReadFile(archive);
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        var reader = new TarReader(gz);
        var dirTimes = new List<(string path, uint mtime, ushort perm, int uid, int gid)>();
        int count = 0;
        while (reader.GetNextEntry() is { } e)
        {
            _ct.ThrowIfCancellationRequested();
            string name = e.Name.TrimEnd('/');
            if (name.Length == 0 || name == ".") continue;
            string path = Abs(name);
            int uid = IdFor((e as PosixTarEntry)?.UserName, e.Uid, true), gid = IdFor((e as PosixTarEntry)?.GroupName, e.Gid, false);
            uint mtime = (uint)e.ModificationTime.ToUnixTimeSeconds();
            ushort perm = (ushort)((int)e.Mode & 0xFFF);
            int cut = path.LastIndexOf('/');
            MkdirP(cut <= 0 ? "/" : path[..cut]);
            var par = Parent(path) ?? throw new IOException($"tar: {path} is outside the disk");
            var (fs, dir, leaf) = par;
            if (leaf.Length == 0)
            {
                // The member is a mount point (e.g. "var/" onto /var): tar only sets its attributes.
                if (e.EntryType != TarEntryType.Directory) throw new IOException($"tar: {name} over a mount point");
                dirTimes.Add((path, mtime, perm, uid, gid));
                count++;
                continue;
            }
            uint existing = fs.Lookup(dir, leaf);
            switch (e.EntryType)
            {
                case TarEntryType.Directory:
                    if (existing == 0) fs.Mkdir(dir, leaf, perm, (ushort)uid, (ushort)gid, mtime);
                    dirTimes.Add((path, mtime, perm, uid, gid));
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    if (existing != 0) fs.Remove(dir, leaf, false);
                    var ms = new MemoryStream();
                    e.DataStream?.CopyTo(ms);
                    ms.Position = 0;
                    fs.CreateFile(dir, leaf, perm, (ushort)uid, (ushort)gid, _now, mtime, ms, ms.Length);
                    break;
                case TarEntryType.SymbolicLink:
                    if (existing != 0) fs.Remove(dir, leaf, false);
                    // tar of the time cannot set a symlink's times: it keeps its creation time.
                    fs.Symlink(dir, leaf, e.LinkName, (ushort)uid, (ushort)gid, _now);
                    break;
                case TarEntryType.HardLink:
                    if (existing != 0) fs.Remove(dir, leaf, false);
                    var tgt = Parent(Abs(e.LinkName)) ?? throw new IOException("tar: hard link outside the disk");
                    fs.Link(dir, leaf, tgt.fs.Lookup(tgt.dir, tgt.name));
                    break;
                case TarEntryType.CharacterDevice or TarEntryType.BlockDevice or TarEntryType.Fifo:
                    if (existing != 0) fs.Remove(dir, leaf, false);
                    ushort type = e.EntryType switch { TarEntryType.CharacterDevice => Ext2.S_IFCHR, TarEntryType.BlockDevice => Ext2.S_IFBLK, _ => Ext2.S_IFIFO };
                    fs.Mknod(dir, leaf, (ushort)(type | perm), (e as PosixTarEntry)?.DeviceMajor ?? 0, (e as PosixTarEntry)?.DeviceMinor ?? 0, (ushort)uid, (ushort)gid, mtime);
                    break;
                default:
                    throw new NotSupportedException($"tar: {name}: entry type {e.EntryType}");
            }
            count++;
        }
        // tar restores directory modes/times after their contents.
        foreach (var (path, mtime, perm, uid, gid) in dirTimes.AsEnumerable().Reverse())
        {
            var par = Parent(path)!.Value;
            uint ino = par.name.Length == 0 ? par.dir : par.fs.Lookup(par.dir, par.name);
            par.fs.SetAttr(ino, perm, (ushort)uid, (ushort)gid, _now, mtime);
        }
        _log($"  tar: {count:N0} entries from {archive}");
    }

    // ================================================================ shell

    void RunScript(string path, string[] args)
    {
        var text = Encoding.Latin1.GetString(ReadFile(path)).Replace("\r", "");
        Exec(text.Split('\n'), args, path);
    }

    void Exec(string[] lines, string[] args, string where)
    {
        var vars = new Dictionary<string, string>();
        var skip = new Stack<bool>();                 // if/else nesting: true = skipping
        for (int i = 0; i < lines.Length; i++)
        {
            _ct.ThrowIfCancellationRequested();
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string Expand(string s) => Regex.Replace(s, @"\$\{(\w+)\}|\$(\w+)", m =>
            {
                string v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (int.TryParse(v, out int k)) return k >= 1 && k <= args.Length ? args[k - 1] : "";
                return vars.TryGetValue(v, out var x) ? x : "";
            });
            bool skipping = skip.Count > 0 && skip.Peek();
            if (Regex.IsMatch(line, @"^if\s"))
            {
                bool cond = !skipping && Test(Expand(Regex.Match(line, @"\[(.*)\]").Groups[1].Value));
                skip.Push(skipping || !cond);
                continue;
            }
            if (line == "else") { bool parent = skip.Count > 1 && skip.ElementAt(1); var cur = skip.Pop(); skip.Push(parent || !cur); continue; }
            if (line == "fi") { skip.Pop(); continue; }
            if (skipping) { if (line.Contains("<< EOF")) while (lines[++i].Trim() != "EOF") { } continue; }

            // heredoc
            List<string>? here = null;
            if (line.Contains("<< EOF"))
            {
                here = new List<string>();
                while (lines[++i].Trim() != "EOF") here.Add(Expand(lines[i]));
                line = line[..line.IndexOf("<< EOF")];
            }
            line = Expand(line);
            // command lists: "a; b" and "a && b" (every command here succeeds or throws)
            foreach (var part in SplitList(line))
            {
                string cmdLine = part;
                string? outFile = null;
                cmdLine = Regex.Replace(cmdLine, @"\s*\d?>>?\s*/dev/null|\s*\d>&\d", "");
                var red = Regex.Match(cmdLine, @"\s>\s*(\S+)\s*$");
                if (red.Success) { outFile = red.Groups[1].Value; cmdLine = cmdLine[..red.Index]; }
                Command(cmdLine.Trim(), here, outFile, vars, where);
            }
        }
    }

    static IEnumerable<string> SplitList(string line)
    {
        var sb = new StringBuilder();
        char q = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (q != '\0') { if (ch == q) q = '\0'; sb.Append(ch); continue; }
            if (ch is '"' or '\'') { q = ch; sb.Append(ch); continue; }
            if (ch == ';' || (ch == '&' && i + 1 < line.Length && line[i + 1] == '&'))
            {
                if (ch == '&') i++;
                if (sb.ToString().Trim().Length > 0) yield return sb.ToString();
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        if (sb.ToString().Trim().Length > 0) yield return sb.ToString();
    }

    bool Test(string expr)
    {
        var t = Tokens(expr);
        if (t.Count == 2 && t[0] == "-e") return Exists(t[1]);
        if (t.Count == 3 && t[1] == "=") return t[0] == t[2];
        if (t.Count == 3 && t[1] == "!=") return t[0] != t[2];
        if (t.Count == 2 && t[0] == "!=") return t[1] != "";      // "" != "" collapsed
        if (t.Count == 1 && t[0] == "!=") return false;
        throw new NotSupportedException($"test [ {expr} ]");
    }

    static List<string> Tokens(string s)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(s, "\"([^\"]*)\"|'([^']*)'|(\\S+)"))
            list.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
        return list;
    }

    void Command(string line, List<string>? here, string? outFile, Dictionary<string, string> vars, string where)
    {
        if (line.Length == 0) return;
        if (line.Contains('|')) { Pipeline(line, outFile); return; }
        var t = Tokens(line);
        string cmd = Path.GetFileName(t[0]);
        var a = t.Skip(1).ToList();
        switch (cmd)
        {
            case "export":
                foreach (var kv in a) { int eq = kv.IndexOf('='); if (eq > 0) vars[kv[..eq]] = kv[(eq + 1)..].Trim('"', '\''); }
                return;
            case "echo": case "sync": case "sleep": case "true":
                return;
            case "dd":
            {
                var o = a.Select(x => x.Split('=')).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
                if (o["if"] != "/dev/zero" || o["of"] != "/dev/hda") throw new NotSupportedException(line);
                long bytes = long.Parse(o["bs"]) * long.Parse(o["count"]);
                _img.Seek(0, SeekOrigin.Begin); _img.Write(new byte[bytes]);
                _fsCache.Clear(); _parts.Clear();
                return;
            }
            case "sfdisk":
                Sfdisk(here ?? throw new NotSupportedException("sfdisk without input"));
                _fsCache.Clear();
                return;
            case "mke2fs":
            {
                bool j = a.Contains("-j");
                int m = a.IndexOf("-m") is int mi and >= 0 ? int.Parse(a[mi + 1]) : 5;
                int n = PartNo(a[^1]);
                var (start, size) = Part(n);
                var uuid = MD5.HashData(Encoding.ASCII.GetBytes($"{_set.Title}/{a[^1]}/{_now}"));
                _fsCache[n] = Mke2fs.Format(_img, start * 512, size * 512, new Mke2fs.Options(j, m, _now, uuid));
                return;
            }
            case "tune2fs":
            {
                var fs = Fs(PartNo(a[^1]));
                for (int k = 0; k + 1 < a.Count - 1; k += 2)
                {
                    if (a[k] == "-c") { int c = int.Parse(a[k + 1]); Ext2.P16(fs.Super, 54, (ushort)(c == 0 ? -1 : c)); }  // 1.35: 0 means -1
                    else if (a[k] == "-i") Ext2.P32(fs.Super, 68, uint.Parse(a[k + 1].TrimEnd('d', 'w', 'm')));
                    else throw new NotSupportedException(line);
                }
                fs.Flush();
                return;
            }
            case "mkswap": Mkswap(PartNo(a[^1])); return;
            case "cd": _cwd = Abs(a.Count > 0 ? a[0] : "/"); return;
            case "mkdir": MkdirP(a[^1]); return;
            case "mount":
            {
                string? type = null; var pos = new List<string>();
                for (int k = 0; k < a.Count; k++)
                {
                    if (a[k] == "-t") type = a[++k];
                    else if (a[k] == "-o" || a[k] == "-no") k++;
                    else if (a[k] == "-n" || a[k] == "-av") { }
                    else pos.Add(a[k]);
                }
                Mount(pos[0], pos[1], type ?? "ext2");
                return;
            }
            case "umount": Umount(a[^1]); return;
            case "tar":
                if (!Regex.IsMatch(a[0], "^-?x[pvz]*f$")) throw new NotSupportedException(line);
                TarExtract(a[1]);
                return;
            case "cp":
            {
                var files = a.Where(x => !x.StartsWith('-')).ToList();
                string dst = files[^1];
                var data = ReadFile(files[0]);
                if (Exists(dst) && IsDir(dst)) dst = dst.TrimEnd('/') + "/" + Path.GetFileName(files[0]);
                WriteFile(dst, data, (ushort)(PermOf(files[0]) & ~0x12));   // new file: source mode & ~umask 022
                return;
            }
            case "chmod":
                if (a[0] != "+x") throw new NotSupportedException(line);
                if (Parent(a[1]) is { } cp && cp.fs.Lookup(cp.dir, cp.name) is uint cino and not 0)
                    cp.fs.SetAttr(cino, (ushort)(Ext2.Mode(cp.fs.ReadInode(cino)) & 0xFFF | 0x49));
                return;
            case "chroot":
            {
                string saved = _root, savedCwd = _cwd;
                _root = Real(a[0]); _cwd = "/";
                try { RunScript(a[1], a.Skip(2).ToArray()); }
                finally { _root = saved; _cwd = savedCwd; }
                return;
            }
            case "rm":
            {
                bool rec = a.Any(x => x.StartsWith('-') && x.Contains('r'));
                foreach (var target in a.Where(x => !x.StartsWith('-')))
                    foreach (var gp in Glob(target)) if (Exists(gp)) Remove(gp, rec);
                return;
            }
            case "touch":
                foreach (var tp in a)
                {
                    if (Exists(tp)) { var par = Parent(tp)!.Value; par.fs.SetAttr(par.fs.Lookup(par.dir, par.name), atime: _now, mtime: _now); }
                    else WriteFile(tp, Array.Empty<byte>());
                }
                return;
            case "mv":
            {
                var files = a.Where(x => !x.StartsWith('-')).ToList();
                var from = Parent(files[0])!.Value; var to = Parent(files[1])!.Value;
                if (from.fs != to.fs) throw new NotSupportedException("mv across volumes");
                from.fs.Rename(from.dir, from.name, to.dir, to.name);
                return;
            }
            case "grep":
            {
                if (a[0] != "-v" || outFile == null) throw new NotSupportedException(line);
                string pat = a[1];
                var lines = Encoding.Latin1.GetString(ReadFile(a[2])).Split('\n');
                var kept = lines.Take(lines.Length - 1).Where(l => !l.Contains(pat)).Select(l => l + "\n");
                WriteFile(outFile, Encoding.Latin1.GetBytes(string.Concat(kept)));
                return;
            }
            case "md5sum":
                if (outFile == null) throw new NotSupportedException(line);
                Pipeline(line, outFile);
                return;
            case "lilo.safe": case "lilo":
            {
                int c = a.IndexOf("-C");
                string conf = c >= 0 ? a[c + 1] : "/etc/lilo.conf";
                Lilo.Install(this, conf);
                return;
            }
            case "resize2fs":
                throw new NotSupportedException("resize2fs (Force cabinet profile) is not supported.");
            default:
                if (t[0].StartsWith('/') && t[0].EndsWith(".sh")) { RunScript(t[0], a.ToArray()); return; }
                throw new NotSupportedException($"{where}: unsupported command: {line}");
        }
    }

    bool IsDir(string p)
    {
        string real = Real(p);
        if (Locate(real) is { } loc) { uint ino = loc.fs.Resolve(loc.inner); return ino != 0 && (Ext2.Mode(loc.fs.ReadInode(ino)) & Ext2.S_IFMT) == Ext2.S_IFDIR; }
        return _rescueDirs.Contains(real) || RescueIso(real)?.IsDir == true;
    }

    IEnumerable<string> Glob(string pattern)
    {
        if (!pattern.Contains('*')) { yield return pattern; yield break; }
        string abs = Abs(pattern);
        int cut = abs.LastIndexOf('/');
        string dir = cut <= 0 ? "/" : abs[..cut];
        var rx = new Regex("^" + Regex.Escape(abs[(cut + 1)..]).Replace("\\*", ".*") + "$");
        if (Locate(Real(dir)) is { } loc && loc.fs.Resolve(loc.inner) is uint d and not 0)
            foreach (var e in loc.fs.ListDir(d).ToList())
                if (e.Name != "." && e.Name != ".." && rx.IsMatch(e.Name)) yield return dir.TrimEnd('/') + "/" + e.Name;
    }

    /// <summary>md5sum FILE | sed -e"s/A/B/" > OUT  (the only pipeline the scripts use).</summary>
    void Pipeline(string line, string? outFile)
    {
        var stages = line.Split('|').Select(s => Tokens(s.Trim())).ToList();
        if (Path.GetFileName(stages[0][0]) != "md5sum" || outFile == null) throw new NotSupportedException(line);
        string file = stages[0][1];
        string text = $"{Convert.ToHexString(MD5.HashData(ReadFile(file))).ToLowerInvariant()}  {file}\n";
        foreach (var st in stages.Skip(1))
        {
            if (Path.GetFileName(st[0]) != "sed") throw new NotSupportedException(line);
            string expr = (st[^1].StartsWith("-e") ? st[^1][2..] : st[^1]).Trim('"', '\'');
            var m = Regex.Match(expr, @"^s/((?:\\.|[^/])*)/((?:\\.|[^/])*)/(g?)$");
            if (!m.Success) throw new NotSupportedException("sed " + expr);
            string from = Regex.Unescape(m.Groups[1].Value.Replace("\\/", "/")), to = m.Groups[2].Value.Replace("\\/", "/");
            var rx = new Regex(Regex.Escape(from));
            text = string.Join('\n', text.Split('\n').Select(l => m.Groups[3].Value == "g" ? rx.Replace(l, to) : rx.Replace(l, to, 1)));
        }
        WriteFile(outFile, Encoding.Latin1.GetBytes(text));
    }

    // ================================================================ for Lilo

    internal Stream Image => _img;
    internal long DiskSectors => _diskSectors;
    internal uint Now => _now;
    internal byte[] ReadPath(string p) => ReadFile(p);
    internal bool PathExists(string p) => Exists(p);
    internal void WritePath(string p, byte[] data, ushort perm) => WriteFile(p, data, perm);
    internal void Log(string s) => _log(s);
    internal (long start, long size) Partition(int n) => Part(n);
    /// <summary>Volume, inode and partition of a path (for LILO's sector maps).</summary>
    internal (Ext2 fs, uint ino, int part) Inode(string p)
    {
        p = Follow(p);
        var loc = Locate(Real(p)) ?? throw new IOException($"{p} is not on the hard disk");
        uint ino = loc.fs.Resolve(loc.inner);
        if (ino == 0) throw new FileNotFoundException(p);
        return (loc.fs, ino, loc.part);
    }
    internal void RemovePath(string p) { if (Exists(p)) Remove(p, false); }
    internal void OverwriteInPlace(string p, byte[] data)
    {
        var (fs, ino, _) = Inode(p);
        fs.WriteInPlace(ino, data);
    }
    internal void RenamePath(string from, string to)
    {
        var a = Parent(from)!.Value; var b = Parent(to)!.Value;
        if (a.fs != b.fs) throw new NotSupportedException("rename across volumes");
        a.fs.Rename(a.dir, a.name, b.dir, b.name);
    }
}
