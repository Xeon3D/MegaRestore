using System.IO.Compression;
using System.Text;

namespace MegaRestore;

/// <summary>
/// Partimage 0.6.1 image reader (Merit Linux restore discs, Docs/11). Every volume file
/// (partN.gz.000, .001, ...) is its own gzip stream starting with a 512-byte volume header.
/// Volume 0 then holds: main header 16384+crc, "MAGIC-BEGIN-MBRBACKUP" (+ none), "EXT000".."EXT009"
/// (each + u32 length), "LOCALHEADER" + 16384+crc (u64 block size, used, count, bitmap size),
/// "BITMAP" + bitmap (bit i of byte k = block 8k+i used), "INFO" + 16384+crc, "DATABLOCKS" +
/// the used blocks in order with a 16-byte "CHK\0" record after every 65536 data bytes, "TAIL".
/// Later volumes continue the byte stream after their own volume header.
/// </summary>
public static class PartImage
{
    const int Chunk = 65536;

    sealed class VolumeStream(IEnumerable<Func<Stream>> volumes)
    {
        readonly IEnumerator<Func<Stream>> _it = volumes.GetEnumerator();
        Stream? _cur;
        bool _first = true;

        public void ReadExactly(Span<byte> dst)
        {
            while (dst.Length > 0)
            {
                if (_cur == null)
                {
                    if (!_it.MoveNext()) throw new EndOfStreamException("The partition image ends early (a disc or volume is missing).");
                    _cur = new GZipStream(_it.Current(), CompressionMode.Decompress);
                    if (!_first)
                    {
                        var vh = new byte[512];
                        _cur.ReadExactly(vh);
                        if (Encoding.ASCII.GetString(vh, 0, 16) != "PaRtImAgE-VoLuMe")
                            throw new InvalidDataException("A partition image volume does not start with a partimage header.");
                    }
                    _first = false;
                }
                int n = _cur.Read(dst);
                if (n == 0) { _cur.Dispose(); _cur = null; continue; }
                dst = dst[n..];
            }
        }

        public byte[] Read(int n) { var b = new byte[n]; ReadExactly(b); return b; }

        public void Expect(string magic)
        {
            var got = Encoding.ASCII.GetString(Read(magic.Length));
            if (got != magic) throw new InvalidDataException($"Partition image: expected {magic}, found {got}.");
        }

        public void Close() => _cur?.Dispose();
    }

    public sealed record Info(string FileSystem, int BlockSize, long BlockCount, long UsedBlocks);

    /// <summary>
    /// Streams the image; writeBlock(blockNumber, data) receives every used block.
    /// Returns the header facts. progress(bytes) is called per block.
    /// </summary>
    public static Info Restore(IEnumerable<Func<Stream>> volumes, Action<long, byte[]> writeBlock,
                               Action<long>? progress = null, CancellationToken ct = default)
    {
        var s = new VolumeStream(volumes);
        try
        {
            var vh = s.Read(512);
            if (Encoding.ASCII.GetString(vh, 0, 16) != "PaRtImAgE-VoLuMe")
                throw new InvalidDataException("Not a partimage image.");
            var mh = s.Read(16384 + 4);
            string fs = Encoding.ASCII.GetString(mh, 0, Array.IndexOf(mh, (byte)0));
            s.Expect("MAGIC-BEGIN-MBRBACKUP");
            for (int k = 0; k < 10; k++)
            {
                s.Expect($"MAGIC-BEGIN-EXT{k:000}");
                int len = BitConverter.ToInt32(s.Read(4));
                if (len > 0) s.Read(len);
            }
            s.Expect("MAGIC-BEGIN-LOCALHEADER");
            var lh = s.Read(16384 + 4);
            long bsize = BitConverter.ToInt64(lh, 0), used = BitConverter.ToInt64(lh, 8),
                 count = BitConverter.ToInt64(lh, 16), bmsize = BitConverter.ToInt64(lh, 24);
            if (bsize is < 512 or > 65536 || count <= 0 || bmsize < (count + 7) / 8)
                throw new InvalidDataException("Partition image has an implausible header.");
            s.Expect("MAGIC-BEGIN-BITMAP");
            var bm = s.Read((int)bmsize);
            s.Expect("MAGIC-BEGIN-INFO");
            s.Read(16384 + 4);
            s.Expect("MAGIC-BEGIN-DATABLOCKS");
            long since = 0, done = 0;
            for (long blk = 0; blk < count; blk++)
            {
                if ((bm[blk >> 3] >> (int)(blk & 7) & 1) == 0) continue;
                var data = s.Read((int)bsize);
                writeBlock(blk, data);
                done++;
                since += bsize;
                if (since == Chunk)
                {
                    var chk = s.Read(16);
                    if (chk[0] != 'C' || chk[1] != 'H' || chk[2] != 'K' || chk[3] != 0)
                        throw new InvalidDataException($"Partition image is damaged near block {blk} (missing check record).");
                    since = 0;
                }
                progress?.Invoke(bsize);
                if ((done & 1023) == 0) ct.ThrowIfCancellationRequested();
            }
            if (done != used) throw new InvalidDataException($"Partition image bitmap has {done} used blocks, header says {used}.");
            s.Expect("MAGIC-BEGIN-TAIL");
            return new Info(fs, (int)bsize, count, used);
        }
        finally { s.Close(); }
    }
}
