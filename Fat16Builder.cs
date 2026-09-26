using System.Text;

namespace MegaRestore;

/// <summary>
/// Builds a FAT16 volume the way ROM-DOS FORMAT + file copies would leave it on a blank
/// partition: clusters handed out in increasing order, directories grown by one cluster
/// when full, entries appended in creation order. Parameters match the factory MAXX
/// drives (Docs/10): 512 root entries, 1 reserved sector, 2 FATs, 32 KB clusters.
/// </summary>
public sealed class Fat16Builder
{
    public sealed class Dir
    {
        internal readonly List<byte[]> Entries = new();
        internal readonly List<int> Clusters = new();
        internal readonly Dir? Parent;
        internal readonly bool IsRoot;
        internal Dir(Dir? parent, bool root) { Parent = parent; IsRoot = root; }
        internal int FirstCluster => IsRoot ? 0 : Clusters[0];
        internal int Find(string name83)
        {
            var key = Encoding.ASCII.GetBytes(name83);
            for (int i = 0; i < Entries.Count; i++)
                if (Entries[i][0] != 0xE5 && Entries[i].AsSpan(0, 11).SequenceEqual(key)) return i;
            return -1;
        }
    }

    readonly Stream _img;
    readonly long _base;                // byte offset of the partition
    public readonly uint TotalSectors, Hidden;
    public readonly int SecPerClus = 64, Reserved = 1, NumFats = 2, RootEntries = 512;
    public readonly int FatSectors, RootSectors, DataStart, ClusterCount;
    int ClusterBytes => SecPerClus * 512;
    readonly ushort[] _fat;
    int _next = 2;
    public Dir Root { get; }

    public Fat16Builder(Stream img, long partLba, uint totalSectors, uint hidden)
    {
        _img = img; _base = partLba * 512; TotalSectors = totalSectors; Hidden = hidden;
        RootSectors = RootEntries * 32 / 512;
        // Standard FORMAT computation (smallest FAT that covers all clusters).
        int fat = 1;
        while (true)
        {
            long data = totalSectors - Reserved - NumFats * fat - RootSectors;
            long clusters = data / SecPerClus;
            if ((clusters + 2) * 2 <= fat * 512L) { ClusterCount = (int)clusters; break; }
            fat++;
        }
        FatSectors = fat;
        if (ClusterCount < 4085 || ClusterCount > 65524) throw new InvalidOperationException("Partition size is outside FAT16 limits.");
        DataStart = Reserved + NumFats * FatSectors + RootSectors;
        _fat = new ushort[ClusterCount + 2];
        _fat[0] = 0xFFF8; _fat[1] = 0xFFFF;
        Root = new Dir(null, true);
    }

    public long FreeBytes => (long)(ClusterCount + 2 - _next) * ClusterBytes;

    long ClusterOffset(int c) => _base + (long)(DataStart + (c - 2) * SecPerClus) * 512;

    int Alloc(int n, int prev = 0)
    {
        if (n == 0) return 0;
        if (_next + n > ClusterCount + 2) throw new IOException("The partition is full.");
        int first = _next;
        for (int i = 0; i < n; i++)
        {
            int c = _next++;
            if (prev != 0) _fat[prev] = (ushort)c;
            _fat[c] = 0xFFFF;
            prev = c;
        }
        return first;
    }

    List<int> Chain(int c)
    {
        var l = new List<int>();
        while (c >= 2 && c < 0xFFF8) { l.Add(c); c = _fat[c]; }
        return l;
    }

    public static string To83(string name)
    {
        var up = name.ToUpperInvariant();
        int dot = up.LastIndexOf('.');
        string b = dot < 0 ? up : up[..dot], e = dot < 0 ? "" : up[(dot + 1)..];
        if (b.Length > 8 || e.Length > 3) throw new ArgumentException($"Not an 8.3 name: {name}");
        return b.PadRight(8) + e.PadRight(3);
    }

    static byte[] MakeEntry(string n83, byte attr, ushort time, ushort date, int cluster, uint size)
    {
        var e = new byte[32];
        Encoding.Latin1.GetBytes(n83, 0, 11, e, 0);
        if (e[0] == 0xE5) e[0] = 0x05;
        e[11] = attr;
        BitConverter.TryWriteBytes(e.AsSpan(22), time);
        BitConverter.TryWriteBytes(e.AsSpan(24), date);
        BitConverter.TryWriteBytes(e.AsSpan(26), (ushort)cluster);
        BitConverter.TryWriteBytes(e.AsSpan(28), size);
        return e;
    }

    void AddEntry(Dir d, byte[] e)
    {
        int perCluster = ClusterBytes / 32;
        if (d.IsRoot)
        {
            if (d.Entries.Count >= RootEntries) throw new IOException("Root directory is full.");
        }
        else if (d.Entries.Count >= d.Clusters.Count * perCluster)
        {
            d.Clusters.Add(Alloc(1, d.Clusters[^1]));     // DOS extends a full directory by one cluster
        }
        d.Entries.Add(e);
    }

    public Dir MakeDir(Dir parent, string name, ushort time, ushort date)
    {
        string n83 = To83(name);
        int i = parent.Find(n83);
        if (i >= 0) throw new IOException($"{name} already exists.");
        var d = new Dir(parent, false);
        d.Clusters.Add(Alloc(1));
        AddEntry(parent, MakeEntry(n83, 0x10, time, date, d.FirstCluster, 0));
        d.Entries.Add(MakeEntry(".          ", 0x10, time, date, d.FirstCluster, 0));
        d.Entries.Add(MakeEntry("..         ", 0x10, time, date, parent.FirstCluster, 0));
        return d;
    }

    /// <summary>
    /// Creates (or rewrites, as DOS "open for write" would) a file and streams its data.
    /// A rewrite keeps the entry's slot and reuses the old clusters when they suffice.
    /// </summary>
    public void WriteFile(Dir d, string name, byte attr, ushort time, ushort date, uint size, Stream src, Action<long>? progress = null)
    {
        string n83 = To83(name);
        int need = (int)((size + (uint)ClusterBytes - 1) / (uint)ClusterBytes);
        int idx = d.Find(n83), first;
        if (idx >= 0)
        {
            var old = Chain(BitConverter.ToUInt16(d.Entries[idx], 26));
            if (old.Count >= need && IsContiguous(old, need))
            {
                for (int k = need; k < old.Count; k++) _fat[old[k]] = 0;
                if (need > 0) _fat[old[need - 1]] = 0xFFFF;
                first = need > 0 ? old[0] : 0;
            }
            else
            {
                foreach (var c in old) _fat[c] = 0;
                first = Alloc(need);
            }
            d.Entries[idx] = MakeEntry(n83, attr, time, date, first, size);
        }
        else
        {
            first = Alloc(need);
            AddEntry(d, MakeEntry(n83, attr, time, date, first, size));
        }
        // Clusters of one file are contiguous here, so the data is one sequential write.
        if (size > 0) _img.Seek(ClusterOffset(first), SeekOrigin.Begin);
        var buf = new byte[1 << 20];
        long left = size;
        while (left > 0)
        {
            int n = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
            if (n <= 0) throw new EndOfStreamException($"Archive ends inside {name}.");
            _img.Write(buf, 0, n);
            left -= n;
            progress?.Invoke(n);
        }
    }

    static bool IsContiguous(List<int> chain, int n)
    {
        for (int i = 1; i < n; i++) if (chain[i] != chain[i - 1] + 1) return false;
        return true;
    }

    /// <summary>Writes boot sector, FATs and directories. bootTemplate: 512-byte ROM-DOS boot sector.</summary>
    public void Finish(byte[] bootTemplate, string label, uint serial, byte heads, byte spt)
    {
        var bs = (byte[])bootTemplate.Clone();
        BitConverter.TryWriteBytes(bs.AsSpan(0x0B), (ushort)512);
        bs[0x0D] = (byte)SecPerClus;
        BitConverter.TryWriteBytes(bs.AsSpan(0x0E), (ushort)Reserved);
        bs[0x10] = (byte)NumFats;
        BitConverter.TryWriteBytes(bs.AsSpan(0x11), (ushort)RootEntries);
        BitConverter.TryWriteBytes(bs.AsSpan(0x13), (ushort)(TotalSectors < 65536 ? TotalSectors : 0));
        bs[0x15] = 0xF8;
        BitConverter.TryWriteBytes(bs.AsSpan(0x16), (ushort)FatSectors);
        BitConverter.TryWriteBytes(bs.AsSpan(0x18), (ushort)spt);
        BitConverter.TryWriteBytes(bs.AsSpan(0x1A), (ushort)heads);
        BitConverter.TryWriteBytes(bs.AsSpan(0x1C), Hidden);
        BitConverter.TryWriteBytes(bs.AsSpan(0x20), TotalSectors < 65536 ? 0u : TotalSectors);
        bs[0x24] = 0x80; bs[0x25] = 0; bs[0x26] = 0x29;
        BitConverter.TryWriteBytes(bs.AsSpan(0x27), serial);
        Encoding.ASCII.GetBytes(label.PadRight(11)[..11], 0, 11, bs, 0x2B);
        Encoding.ASCII.GetBytes("FAT16   ", 0, 8, bs, 0x36);
        bs[510] = 0x55; bs[511] = 0xAA;
        WriteAt(_base, bs);

        var fat = new byte[FatSectors * 512];
        Buffer.BlockCopy(_fat, 0, fat, 0, _fat.Length * 2);
        for (int i = 0; i < NumFats; i++) WriteAt(_base + (long)(Reserved + i * FatSectors) * 512, fat);

        var root = new byte[RootSectors * 512];
        for (int i = 0; i < Root.Entries.Count; i++) Buffer.BlockCopy(Root.Entries[i], 0, root, i * 32, 32);
        WriteAt(_base + (long)(Reserved + NumFats * FatSectors) * 512, root);
    }

    /// <summary>Directory clusters; call for every subdirectory the caller created.</summary>
    public void FlushDir(Dir d)
    {
        int per = ClusterBytes / 32;
        for (int k = 0; k < d.Clusters.Count; k++)
        {
            var buf = new byte[ClusterBytes];
            for (int i = k * per; i < Math.Min(d.Entries.Count, (k + 1) * per); i++)
                Buffer.BlockCopy(d.Entries[i], 0, buf, (i - k * per) * 32, 32);
            WriteAt(ClusterOffset(d.Clusters[k]), buf);
        }
    }

    void WriteAt(long pos, byte[] b)
    {
        _img.Seek(pos, SeekOrigin.Begin);
        _img.Write(b);
    }

    /// <summary>MS-DOS style volume serial from the format time.</summary>
    public static uint Serial(DateTime t)
    {
        uint lo = (uint)((t.Month << 8 | t.Day) + (t.Second << 8 | t.Millisecond / 10)) & 0xFFFF;
        uint hi = (uint)((t.Hour << 8 | t.Minute) + t.Year) & 0xFFFF;
        return hi << 16 | lo;
    }

    public static ushort DosDate(DateTime t) => (ushort)((t.Year - 1980) << 9 | t.Month << 5 | t.Day);
    public static ushort DosTime(DateTime t) => (ushort)(t.Hour << 11 | t.Minute << 5 | t.Second / 2);
}
