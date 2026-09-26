using System.Text;

namespace MegaRestore;

/// <summary>
/// Merit D2000 "DISK UPGRADE KIT" archive, DISKIMG&lt;drive&gt;.NNN (Docs/10).
/// The parts, concatenated in order across the discs, are one stream of records:
///   u32 dir (index into @_DIRS_&lt;drive&gt;.LST), 13 B name (8.3, NUL padded), u32 size,
///   u16 DOS date, u16 DOS time, u8 attr, 3 bytes struct padding (uninitialised), size bytes.
/// No compression. Record order = D2000's copy order.
/// </summary>
public static class D2000Archive
{
    public const int HeaderSize = 29;

    public sealed record Record(int Dir, string Name, uint Size, ushort Date, ushort Time, byte Attr);

    static readonly Encoding Cp437 = CodePagesEncodingProvider.Instance.GetEncoding(437) ?? Encoding.Latin1;

    /// <summary>Reads one header; null at a clean end of stream.</summary>
    public static Record? ReadHeader(Stream s)
    {
        var h = new byte[HeaderSize];
        int got = s.ReadAtLeast(h, HeaderSize, throwOnEndOfStream: false);
        if (got == 0) return null;
        if (got != HeaderSize) throw new InvalidDataException("Archive ends inside a record header.");
        int dir = BitConverter.ToInt32(h, 0);
        int nlen = Array.IndexOf(h, (byte)0, 4, 13);
        string name = Cp437.GetString(h, 4, (nlen < 0 ? 17 : nlen) - 4);
        var r = new Record(dir, name, BitConverter.ToUInt32(h, 17), BitConverter.ToUInt16(h, 21),
                           BitConverter.ToUInt16(h, 23), h[25]);
        return Plausible(r) ? r : throw new DamagedArchiveException(h.All(b => b == 0));
    }

    /// <summary>A real record has an 8.3 name of printable characters and a sane size.</summary>
    static bool Plausible(Record r)
    {
        if (r.Dir < 0 || r.Dir > 100_000 || r.Size > 1_000_000_000 || r.Name.Length == 0) return false;
        int dot = r.Name.IndexOf('.');
        if ((dot < 0 ? r.Name.Length : dot) > 8 || (dot >= 0 && r.Name.Length - dot - 1 > 3)) return false;
        return r.Name.All(c => c > ' ' && c < 0x7F && @"""*+,/:;<=>?[\]|".IndexOf(c) < 0);
    }

    /// <summary>@INSTALL.DAT: "[C] [2 DISK IMAGES] [1124483750 BYTES TOTAL ON THIS DRIVE]".</summary>
    public static Dictionary<char, (int Parts, long Bytes)> ParseInstallDat(string text)
    {
        var d = new Dictionary<char, (int, long)>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     text, @"\[(\w)\]\s*\[(\d+) DISK IMAGES?\]\s*\[(\d+) BYTES", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            d[char.ToUpperInvariant(m.Groups[1].Value[0])] = (int.Parse(m.Groups[2].Value), long.Parse(m.Groups[3].Value));
        return d;
    }
}

public sealed class DamagedArchiveException(bool blank) : IOException(blank
    ? "the archive data is blank (zero-filled) there"
    : "the archive data there is not a valid record")
{
    public bool Blank { get; } = blank;
}

/// <summary>Concatenates several streams (the DISKIMG parts on successive discs).</summary>
public sealed class ConcatStream(IEnumerable<Func<Stream>> parts) : Stream
{
    readonly IEnumerator<Func<Stream>> _it = parts.GetEnumerator();
    Stream? _cur;
    public long Consumed { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            if (_cur == null)
            {
                if (!_it.MoveNext()) return 0;
                _cur = _it.Current();
            }
            int n = _cur.Read(buffer, offset, count);
            if (n > 0) { Consumed += n; return n; }
            _cur.Dispose();
            _cur = null;
        }
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => Consumed; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { _cur?.Dispose(); base.Dispose(disposing); }
}
