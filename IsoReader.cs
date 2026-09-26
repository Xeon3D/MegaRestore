using System.Text;

namespace MegaRestore;

/// <summary>
/// Read-only ISO 9660 reader: primary volume descriptor, Rock Ridge names (NM/SL/PX, CE
/// continuation areas), and both cooked (2048-byte) and raw (2352-byte Mode 1) sector images.
/// Merit's Linux discs rely on Rock Ridge ("part5.gz.001"; the 8.3 name is "part5_gz.001" or
/// "PART5GZ.001"); Ruby 2 was ripped as raw sectors.
/// </summary>
public sealed class IsoReader : IDisposable
{
    public const int Sector = 2048;

    public sealed record Entry(string Name, long Lba, long Size, bool IsDir, DateTime Date,
                               string? SymlinkTarget = null, int Mode = 0, bool HasRrName = false);

    public string Path { get; }
    public string VolumeId { get; }
    public bool RawSectors { get; }
    readonly FileStream _f;
    Entry _root;
    int _suspSkip = -1;       // -1: no Rock Ridge
    bool _joliet;             // names from the Joliet tree (Rock Ridge present but without NM)

    public IsoReader(string path)
    {
        Path = path;
        _f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var head = new byte[16];
        _f.ReadAtLeast(head, 16, throwOnEndOfStream: false);
        RawSectors = head[0] == 0 && head.AsSpan(1, 10).ToArray().All(b => b == 0xFF) && head[11] == 0 && head[15] == 1;
        var pvd = Read(16L * Sector, Sector);
        if (pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001")
            throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not an ISO 9660 image.");
        VolumeId = Encoding.ASCII.GetString(pvd, 40, 32).TrimEnd(' ', '\0');
        _root = Record(pvd, 156, false);
        // Rock Ridge: the root's "." record carries an SUSP "SP" entry.
        var rootDir = Read(_root.Lba * Sector, Sector);
        int len = rootDir[0], nlen = rootDir[32], su = 33 + nlen + (nlen % 2 == 0 ? 1 : 0);
        if (su + 7 <= len && rootDir[su] == 'S' && rootDir[su + 1] == 'P' && rootDir[su + 4] == 0xBE && rootDir[su + 5] == 0xEF)
            _suspSkip = rootDir[su + 6];
        if (_suspSkip >= 0) _root = _root with { Name = "" };
        // Some re-mastered discs (Jade 2, Sapphire) kept PX/TF but lost the NM names; their
        // Joliet tree still has the real names, so use it then.
        bool rrNames = _suspSkip >= 0 && List().Any(e => e.HasRrName);
        if (!rrNames)
        {
            for (int s = 17; s < 32; s++)
            {
                var vd = Read((long)s * Sector, Sector);
                if (vd[0] == 255 || Encoding.ASCII.GetString(vd, 1, 5) != "CD001") break;
                if (vd[0] == 2 && vd[88] == '%' && vd[89] == '/' && (vd[90] == '@' || vd[90] == 'C' || vd[90] == 'E'))
                {
                    _joliet = true;
                    _suspSkip = -1;
                    _root = Record(vd, 156, false) with { Name = "" };
                    break;
                }
            }
        }
    }

    public void Dispose() => _f.Dispose();

    /// <summary>Reads n bytes at a logical (2048-byte-sector) position.</summary>
    public byte[] Read(long pos, int n)
    {
        var b = new byte[n];
        ReadInto(pos, b);
        return b;
    }

    void ReadInto(long pos, Span<byte> dst)
    {
        if (!RawSectors)
        {
            _f.Seek(pos, SeekOrigin.Begin);
            _f.ReadExactly(dst);
            return;
        }
        while (dst.Length > 0)
        {
            long sec = pos / Sector;
            int off = (int)(pos % Sector), k = Math.Min(Sector - off, dst.Length);
            _f.Seek(sec * 2352 + 16 + off, SeekOrigin.Begin);
            _f.ReadExactly(dst[..k]);
            dst = dst[k..];
            pos += k;
        }
    }

    Entry Record(byte[] r, int o, bool rockRidge)
    {
        long lba = BitConverter.ToUInt32(r, o + 2), size = BitConverter.ToUInt32(r, o + 10);
        DateTime date;
        try { date = new DateTime(1900 + r[o + 18], r[o + 19], r[o + 20], r[o + 21], r[o + 22], r[o + 23]); }
        catch { date = new DateTime(1998, 1, 1); }
        int flags = r[o + 25], nlen = r[o + 32];
        var name = (_joliet ? Encoding.BigEndianUnicode.GetString(r, o + 33, nlen) : Encoding.ASCII.GetString(r, o + 33, nlen))
                   .Split(';')[0].TrimEnd('.');
        string? link = null;
        int mode = 0;
        bool rrName = false;
        if (rockRidge)
        {
            int su = o + 33 + nlen + (nlen % 2 == 0 ? 1 : 0) + _suspSkip, end = o + r[o];
            var rr = ParseSusp(r, su, end);
            if (rr.name != null) { name = rr.name; rrName = true; }
            link = rr.link;
            mode = rr.mode;
        }
        return new Entry(name, lba, size, (flags & 2) != 0, date, link, mode, rrName);
    }

    (string? name, string? link, int mode) ParseSusp(byte[] r, int p, int end)
    {
        var name = new StringBuilder();
        var link = new StringBuilder();
        bool haveName = false, haveLink = false;
        int mode = 0;
        var areas = new Queue<(byte[] buf, int p, int end)>();
        areas.Enqueue((r, p, end));
        while (areas.Count > 0)
        {
            var (b, q, e) = areas.Dequeue();
            while (q + 4 <= e)
            {
                char s1 = (char)b[q], s2 = (char)b[q + 1];
                int len = b[q + 2];
                if (len < 4 || q + len > e) break;
                if (s1 == 'N' && s2 == 'M')
                {
                    haveName = true;
                    int fl = b[q + 4];
                    if ((fl & 2) != 0) name.Append('.');
                    else if ((fl & 4) != 0) name.Append("..");
                    else name.Append(Encoding.Latin1.GetString(b, q + 5, len - 5));
                }
                else if (s1 == 'S' && s2 == 'L')
                {
                    haveLink = true;
                    int c = q + 5;
                    while (c + 2 <= q + len)
                    {
                        int cf = b[c], cl = b[c + 1];
                        if (link.Length > 0 && link[^1] != '/') link.Append('/');
                        if ((cf & 2) != 0) link.Append('.');
                        else if ((cf & 4) != 0) link.Append("..");
                        else if ((cf & 8) != 0) link.Append('/');
                        else link.Append(Encoding.Latin1.GetString(b, c + 2, cl));
                        c += 2 + cl;
                    }
                }
                else if (s1 == 'P' && s2 == 'X') mode = (int)BitConverter.ToUInt32(b, q + 4);
                else if (s1 == 'C' && s2 == 'E')
                {
                    long blk = BitConverter.ToUInt32(b, q + 4), off = BitConverter.ToUInt32(b, q + 12), clen = BitConverter.ToUInt32(b, q + 20);
                    var ce = Read(blk * Sector + off, (int)clen);
                    areas.Enqueue((ce, 0, ce.Length));
                }
                else if (s1 == 'S' && s2 == 'T') break;
                q += len;
            }
        }
        return (haveName ? name.ToString() : null, haveLink ? link.ToString() : null, mode);
    }

    public Entry Root => _root;

    public List<Entry> List(Entry? dir = null)
    {
        dir ??= _root;
        var data = Read(dir.Lba * Sector, (int)dir.Size);
        var list = new List<Entry>();
        int pos = 0;
        while (pos < data.Length)
        {
            int rl = data[pos];
            if (rl == 0) { pos = (pos / Sector + 1) * Sector; continue; }   // records never span sectors
            if (!(data[pos + 32] == 1 && data[pos + 33] <= 1)) list.Add(Record(data, pos, _suspSkip >= 0));
            pos += rl;
        }
        return list;
    }

    /// <summary>Case-insensitive lookup by path; null when absent.</summary>
    public Entry? Find(string path)
    {
        Entry cur = _root;
        foreach (var part in path.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var items = List(cur);
            var next = items.FirstOrDefault(e => e.Name == part)
                       ?? items.FirstOrDefault(e => e.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (next == null) return null;
            cur = next;
        }
        return cur;
    }

    public byte[] ReadFile(Entry e) => Read(e.Lba * Sector, (int)e.Size);

    public string? ReadText(string path)
    {
        var e = Find(path);
        return e == null || e.IsDir ? null : Encoding.Latin1.GetString(ReadFile(e));
    }

    /// <summary>A stream over one file's extent (files on these discs are contiguous).</summary>
    public Stream Open(Entry e) => new ExtentStream(this, e.Lba * Sector, e.Size);

    sealed class ExtentStream(IsoReader iso, long start, long length) : Stream
    {
        long _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            long left = length - _pos;
            if (left <= 0) return 0;
            if (count > left) count = (int)left;
            iso.ReadInto(start + _pos, buffer.AsSpan(offset, count));
            _pos += count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
