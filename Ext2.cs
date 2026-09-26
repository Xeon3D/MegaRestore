using System.Buffers.Binary;
using System.Text;

namespace MegaRestore;

/// <summary>
/// ext2/ext3 volume (revision 0/1, block-mapped files, linear directories) that MegaRestore
/// restores into and edits the way the Merit Linux installer's shell steps do (Docs/11).
/// A journal is left alone (the installer mounts ext3 volumes as ext2).
/// Allocation follows the Linux 2.4 ext2 policy closely enough to keep volumes e2fsck-clean;
/// block placement is not claimed to be identical to the kernel's.
/// </summary>
public sealed class Ext2
{
    public const int RootIno = 2;
    public const ushort S_IFMT = 0xF000, S_IFDIR = 0x4000, S_IFREG = 0x8000, S_IFLNK = 0xA000,
                        S_IFCHR = 0x2000, S_IFBLK = 0x6000, S_IFIFO = 0x1000, S_IFSOCK = 0xC000;

    readonly Stream _s;
    readonly long _base;
    public readonly byte[] Super = new byte[1024];
    public int BlockSize { get; private set; }
    public uint BlocksCount => U32(Super, 4);
    public uint InodesCount => U32(Super, 0);
    public uint FirstDataBlock => U32(Super, 20);
    public uint BlocksPerGroup => U32(Super, 32);
    public uint InodesPerGroup => U32(Super, 40);
    public int InodeSize => U32(Super, 76) >= 1 ? U16(Super, 88) : 128;
    public uint FeatureCompat => U32(Super, 92);
    public uint FeatureIncompat => U32(Super, 96);
    public uint FeatureRoCompat => U32(Super, 100);
    public bool HasFileType => (FeatureIncompat & 2) != 0;
    public int GroupCount => (int)((BlocksCount - FirstDataBlock + BlocksPerGroup - 1) / BlocksPerGroup);
    /// <summary>Clock used for ctime/mtime of what the installer creates (reproducible output).</summary>
    public uint Now { get; set; }

    byte[] _gdt = Array.Empty<byte>();
    readonly Dictionary<int, byte[]> _blockBitmaps = new(), _inodeBitmaps = new();
    readonly HashSet<int> _dirtyBB = new(), _dirtyIB = new();
    bool _gdtDirty, _superDirty;

    Ext2(Stream s, long baseOffset) { _s = s; _base = baseOffset; }

    public static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    public static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
    public static void P32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    public static void P16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);

    // ---------------------------------------------------------------- raw I/O

    public byte[] ReadBlock(long blk)
    {
        var b = new byte[BlockSize];
        _s.Seek(_base + blk * BlockSize, SeekOrigin.Begin);
        _s.ReadExactly(b);
        return b;
    }

    public void WriteBlock(long blk, ReadOnlySpan<byte> data)
    {
        if (data.Length != BlockSize) throw new ArgumentException("block size");
        _s.Seek(_base + blk * BlockSize, SeekOrigin.Begin);
        _s.Write(data);
    }

    public static Ext2 Open(Stream s, long baseOffset)
    {
        var fs = new Ext2(s, baseOffset);
        s.Seek(baseOffset + 1024, SeekOrigin.Begin);
        s.ReadExactly(fs.Super);
        if (U16(fs.Super, 56) != 0xEF53) throw new InvalidDataException("Not an ext2 file system.");
        fs.BlockSize = 1024 << (int)U32(fs.Super, 24);
        int gdtBytes = fs.GroupCount * 32, gdtBlocks = (gdtBytes + fs.BlockSize - 1) / fs.BlockSize;
        fs._gdt = new byte[gdtBlocks * fs.BlockSize];
        s.Seek(baseOffset + (fs.FirstDataBlock + 1) * fs.BlockSize, SeekOrigin.Begin);
        s.ReadExactly(fs._gdt);
        if ((fs.FeatureIncompat & ~0x6u) != 0)   // filetype, needs_recovery tolerated
            throw new NotSupportedException($"ext2 incompatible features 0x{fs.FeatureIncompat:x} are not supported.");
        fs.Now = U32(fs.Super, 48);
        return fs;
    }

    internal static Ext2 CreateEmpty(Stream s, long baseOffset, int blockSize, byte[] super, byte[] gdt)
    {
        var fs = new Ext2(s, baseOffset) { BlockSize = blockSize };
        super.CopyTo(fs.Super, 0);
        fs._gdt = gdt;
        fs._gdtDirty = fs._superDirty = true;
        return fs;
    }

    // ---------------------------------------------------------------- group descriptors / bitmaps

    uint GdBlockBitmap(int g) => U32(_gdt, g * 32);
    uint GdInodeBitmap(int g) => U32(_gdt, g * 32 + 4);
    uint GdInodeTable(int g) => U32(_gdt, g * 32 + 8);
    ushort GdFreeBlocks(int g) => U16(_gdt, g * 32 + 12);
    ushort GdFreeInodes(int g) => U16(_gdt, g * 32 + 14);
    ushort GdUsedDirs(int g) => U16(_gdt, g * 32 + 16);
    void GdAdd(int g, int off, int delta) { P16(_gdt, g * 32 + off, (ushort)(U16(_gdt, g * 32 + off) + delta)); _gdtDirty = true; }

    byte[] BlockBitmap(int g)
    {
        if (!_blockBitmaps.TryGetValue(g, out var b)) _blockBitmaps[g] = b = ReadBlock(GdBlockBitmap(g));
        return b;
    }
    byte[] InodeBitmap(int g)
    {
        if (!_inodeBitmaps.TryGetValue(g, out var b)) _inodeBitmaps[g] = b = ReadBlock(GdInodeBitmap(g));
        return b;
    }
    internal void SetBitmaps(int g, byte[] bb, byte[] ib)
    {
        _blockBitmaps[g] = bb; _inodeBitmaps[g] = ib; _dirtyBB.Add(g); _dirtyIB.Add(g);
    }

    uint GroupFirstBlock(int g) => FirstDataBlock + (uint)g * BlocksPerGroup;

    uint FreeBlocksTotal { get => U32(Super, 12); set { P32(Super, 12, value); _superDirty = true; } }
    uint FreeInodesTotal { get => U32(Super, 16); set { P32(Super, 16, value); _superDirty = true; } }

    /// <summary>Allocates one block, searching from goal (Linux: goal, then rest of goal group, then other groups).</summary>
    public uint AllocBlock(uint goal)
    {
        if (goal < FirstDataBlock || goal >= BlocksCount) goal = FirstDataBlock;
        int g0 = (int)((goal - FirstDataBlock) / BlocksPerGroup);
        for (int k = 0; k <= GroupCount; k++)
        {
            int g = (g0 + k) % GroupCount;
            if (GdFreeBlocks(g) == 0) continue;
            var bm = BlockBitmap(g);
            uint start = GroupFirstBlock(g), n = Math.Min(BlocksPerGroup, BlocksCount - start);
            uint from = k == 0 ? goal - start : 0;
            for (uint pass = 0; pass < 2; pass++)
            {
                uint lo = pass == 0 ? from : 0, hi = pass == 0 ? n : from;
                for (uint i = lo; i < hi; i++)
                {
                    if ((bm[i >> 3] & (1 << (int)(i & 7))) != 0) continue;
                    bm[i >> 3] |= (byte)(1 << (int)(i & 7));
                    _dirtyBB.Add(g);
                    GdAdd(g, 12, -1);
                    FreeBlocksTotal--;
                    return start + i;
                }
            }
        }
        throw new DiskFullException();
    }

    public void FreeBlock(uint blk)
    {
        int g = (int)((blk - FirstDataBlock) / BlocksPerGroup);
        uint i = blk - GroupFirstBlock(g);
        var bm = BlockBitmap(g);
        if ((bm[i >> 3] & (1 << (int)(i & 7))) == 0) throw new InvalidOperationException($"double free of block {blk}");
        bm[i >> 3] &= (byte)~(1 << (int)(i & 7));
        _dirtyBB.Add(g);
        GdAdd(g, 12, 1);
        FreeBlocksTotal++;
    }

    /// <summary>Linux 2.4 ext2_new_inode: directories spread (find_group_dir), files stay near the parent.</summary>
    public uint AllocInode(uint parentIno, bool dir, bool spread = true)
    {
        int ng = GroupCount, pg = (int)((parentIno - 1) / InodesPerGroup), g = -1;
        if (dir && spread)
        {
            uint avefree = FreeInodesTotal / (uint)ng;
            int best = -1;
            for (int i = 0; i < ng; i++)
                if (GdFreeInodes(i) > 0 && GdFreeInodes(i) >= avefree && (best < 0 || GdFreeBlocks(i) > GdFreeBlocks(best))) best = i;
            g = best;
        }
        else
        {
            if (GdFreeInodes(pg) > 0 && GdFreeBlocks(pg) > 0) g = pg;
            else
            {
                int i = pg;
                for (int j = 1; j < ng; j <<= 1)
                {
                    i += j; if (i >= ng) i -= ng;
                    if (GdFreeInodes(i) > 0 && GdFreeBlocks(i) > 0) { g = i; break; }
                }
                if (g < 0)
                    for (int j = 1; j < ng; j++)
                    {
                        int k = (pg + j) % ng;
                        if (GdFreeInodes(k) > 0) { g = k; break; }
                    }
            }
        }
        if (g < 0) throw new IOException("No free inodes.");
        var bm = InodeBitmap(g);
        uint first = g == 0 ? U32(Super, 76) >= 1 ? U32(Super, 84) - 1 : 10 : 0;
        for (uint i = first; i < InodesPerGroup; i++)
        {
            if ((bm[i >> 3] & (1 << (int)(i & 7))) != 0) continue;
            bm[i >> 3] |= (byte)(1 << (int)(i & 7));
            _dirtyIB.Add(g);
            GdAdd(g, 14, -1);
            if (dir) GdAdd(g, 16, 1);
            FreeInodesTotal--;
            return (uint)g * InodesPerGroup + i + 1;
        }
        throw new IOException("Inode bitmap and group descriptor disagree.");
    }

    void FreeInode(uint ino, bool dir)
    {
        int g = (int)((ino - 1) / InodesPerGroup);
        uint i = (ino - 1) % InodesPerGroup;
        var bm = InodeBitmap(g);
        bm[i >> 3] &= (byte)~(1 << (int)(i & 7));
        _dirtyIB.Add(g);
        GdAdd(g, 14, 1);
        if (dir) GdAdd(g, 16, -1);
        FreeInodesTotal++;
    }

    // ---------------------------------------------------------------- inodes

    long InodePos(uint ino)
    {
        int g = (int)((ino - 1) / InodesPerGroup);
        long idx = (ino - 1) % InodesPerGroup;
        return _base + GdInodeTable(g) * (long)BlockSize + idx * InodeSize;
    }

    public byte[] ReadInode(uint ino)
    {
        var b = new byte[128];
        _s.Seek(InodePos(ino), SeekOrigin.Begin);
        _s.ReadExactly(b);
        return b;
    }

    public void WriteInode(uint ino, byte[] b)
    {
        _s.Seek(InodePos(ino), SeekOrigin.Begin);
        _s.Write(b, 0, 128);
    }

    public static ushort Mode(byte[] i) => U16(i, 0);
    public static uint Size(byte[] i) => U32(i, 4);

    /// <summary>Physical block of logical block n, 0 for a hole.</summary>
    public uint Bmap(byte[] inode, long n)
    {
        int per = BlockSize / 4;
        if (n < 12) return U32(inode, 40 + (int)n * 4);
        n -= 12;
        uint[] path;
        uint root;
        if (n < per) { root = U32(inode, 88); path = new[] { (uint)n }; }
        else if ((n -= per) < (long)per * per) { root = U32(inode, 92); path = new[] { (uint)(n / per), (uint)(n % per) }; }
        else { n -= (long)per * per; root = U32(inode, 96); path = new[] { (uint)(n / per / per), (uint)(n / per % per), (uint)(n % per) }; }
        uint b = root;
        foreach (var k in path)
        {
            if (b == 0) return 0;
            b = U32(ReadBlock(b), (int)k * 4);
        }
        return b;
    }

    public byte[] ReadData(uint ino)
    {
        var i = ReadInode(ino);
        uint size = Size(i);
        if ((Mode(i) & S_IFMT) == S_IFLNK && size < 60 && U32(i, 104) == 0 && U32(i, 28) == 0)
            return i.AsSpan(40, (int)size).ToArray();
        var outp = new byte[size];
        long nb = (size + BlockSize - 1) / BlockSize;
        for (long k = 0; k < nb; k++)
        {
            uint p = Bmap(i, k);
            int len = (int)Math.Min(BlockSize, size - k * BlockSize);
            if (p != 0) ReadBlock(p).AsSpan(0, len).CopyTo(outp.AsSpan((int)(k * BlockSize)));
        }
        return outp;
    }

    /// <summary>Frees all data and indirect blocks of an inode (i_blocks, size and pointers reset).</summary>
    void FreeData(byte[] inode)
    {
        bool fastLink = (Mode(inode) & S_IFMT) == S_IFLNK && U32(inode, 28) == 0;
        bool dev = (Mode(inode) & S_IFMT) is S_IFCHR or S_IFBLK or S_IFIFO or S_IFSOCK;
        if (!fastLink && !dev)
        {
            for (int k = 0; k < 12; k++) { uint b = U32(inode, 40 + k * 4); if (b != 0) FreeBlock(b); }
            FreeIndirect(U32(inode, 88), 1);
            FreeIndirect(U32(inode, 92), 2);
            FreeIndirect(U32(inode, 96), 3);
        }
        Array.Clear(inode, 40, 60);
        P32(inode, 28, 0);
        P32(inode, 4, 0);
    }

    void FreeIndirect(uint blk, int depth)
    {
        if (blk == 0) return;
        var b = ReadBlock(blk);
        for (int k = 0; k < BlockSize / 4; k++)
        {
            uint c = U32(b, k * 4);
            if (c == 0) continue;
            if (depth > 1) FreeIndirect(c, depth - 1); else FreeBlock(c);
        }
        FreeBlock(blk);
    }

    /// <summary>
    /// Writes data as the (empty) inode's contents, allocating like Linux: an indirect block is
    /// allocated just before the first data block that needs it. i_blocks counts 512-byte sectors.
    /// </summary>
    /// <returns>Bytes stored. With partialOnFull, running out of space stops the write like
    /// the kernel's ENOSPC (whole blocks kept, a half-allocated branch released).</returns>
    long WriteDataBlocks(uint ino, byte[] inode, Stream data, long size, bool partialOnFull = false)
    {
        int per = BlockSize / 4;
        uint goal = GroupFirstBlock((int)((ino - 1) / InodesPerGroup));
        long nb = (size + BlockSize - 1) / BlockSize;
        uint iblocks = 0, spb = (uint)(BlockSize / 512);
        var pending = new Dictionary<uint, byte[]>();          // indirect block -> contents
        uint Alloc() { uint b = AllocBlock(goal); goal = b + 1; iblocks += spb; return b; }
        // Returns the pointer slot (buffer, offset) for logical block k, creating indirect blocks.
        (byte[] buf, int off) Slot(long k)
        {
            if (k < 12) return (inode, 40 + (int)k * 4);
            k -= 12;
            if (k < per) return (Child(inode, 88), (int)k * 4);
            k -= per;
            if (k < (long)per * per) return (Child(Child(inode, 92), (int)(k / per) * 4), (int)(k % per) * 4);
            throw new NotSupportedException("File too large (triple indirect).");
        }
        byte[] Child(byte[] parent, int off)
        {
            uint b = U32(parent, off);
            if (b == 0) { b = Alloc(); P32(parent, off, b); pending[b] = new byte[BlockSize]; }
            return pending[b];
        }
        var buf = new byte[BlockSize];
        long written = size;
        for (long k = 0; k < nb; k++)
        {
            Array.Clear(buf);
            data.ReadExactly(buf.AsSpan(0, (int)Math.Min(BlockSize, size - k * BlockSize)));
            var before = pending.Keys.ToHashSet();
            var inodeSnap = (byte[])inode.Clone();
            try
            {
                var (sbuf, soff) = Slot(k);
                uint p = Alloc();
                P32(sbuf, soff, p);
                WriteBlock(p, buf);
            }
            catch (DiskFullException) when (partialOnFull)
            {
                // ext2_alloc_branch: release indirect blocks allocated for this block.
                foreach (var b in pending.Keys.Where(b => !before.Contains(b)).ToList())
                {
                    FreeBlock(b); iblocks -= spb; pending.Remove(b);
                    foreach (var parent in pending.Values) for (int o = 0; o < BlockSize; o += 4) if (U32(parent, o) == b) P32(parent, o, 0);
                }
                Array.Copy(inodeSnap, 40, inode, 40, 60);
                written = k * BlockSize;
                break;
            }
        }
        foreach (var (b, content) in pending) WriteBlock(b, content);
        P32(inode, 4, (uint)written);
        P32(inode, 28, iblocks);
        return written;
    }

    // ---------------------------------------------------------------- directories

    public sealed record DirEntry(string Name, uint Ino, byte Type);

    public IEnumerable<DirEntry> ListDir(uint dirIno)
    {
        var i = ReadInode(dirIno);
        long nb = Size(i) / BlockSize;
        for (long k = 0; k < nb; k++)
        {
            uint p = Bmap(i, k);
            if (p == 0) continue;
            var b = ReadBlock(p);
            int o = 0;
            while (o < BlockSize)
            {
                uint ino = U32(b, o); int rec = U16(b, o + 4), nl = b[o + 6];
                if (rec < 8) break;
                if (ino != 0) yield return new DirEntry(Encoding.Latin1.GetString(b, o + 8, nl), ino, b[o + 7]);
                o += rec;
            }
        }
    }

    public uint Lookup(uint dirIno, string name)
    {
        foreach (var e in ListDir(dirIno)) if (e.Name == name) return e.Ino;
        return 0;
    }

    static byte FileType(ushort mode) => (mode & S_IFMT) switch
    {
        S_IFREG => 1, S_IFDIR => 2, S_IFCHR => 3, S_IFBLK => 4, S_IFIFO => 5, S_IFSOCK => 6, S_IFLNK => 7, _ => 0,
    };

    void AddEntry(uint dirIno, string name, uint ino, ushort mode)
    {
        var nameBytes = Encoding.Latin1.GetBytes(name);
        if (nameBytes.Length > 255) throw new ArgumentException("name too long");
        int need = (8 + nameBytes.Length + 3) & ~3;
        var di = ReadInode(dirIno);
        if ((U32(di, 32) & 0x1000) != 0) { P32(di, 32, U32(di, 32) & ~0x1000u); WriteInode(dirIno, di); }  // drop htree index
        long nb = Size(di) / BlockSize;
        for (long k = 0; k < nb; k++)
        {
            uint p = Bmap(di, k);
            var b = ReadBlock(p);
            int o = 0;
            while (o < BlockSize)
            {
                uint eino = U32(b, o); int rec = U16(b, o + 4), nl = b[o + 6];
                int used = eino == 0 ? 0 : (8 + nl + 3) & ~3;
                if (rec - used >= need)
                {
                    int at = o;
                    if (eino != 0) { P16(b, o + 4, (ushort)used); at = o + used; rec -= used; }
                    PutDirent(b, at, ino, rec, nameBytes, mode);
                    WriteBlock(p, b);
                    di = ReadInode(dirIno);
                    P32(di, 16, Now); P32(di, 12, Now);       // the kernel stamps the directory
                    WriteInode(dirIno, di);
                    return;
                }
                o += rec;
            }
        }
        // new directory block
        var nbuf = new byte[BlockSize];
        PutDirent(nbuf, 0, ino, BlockSize, nameBytes, mode);
        AppendBlock(dirIno, di, nbuf);
    }

    void PutDirent(byte[] b, int o, uint ino, int rec, byte[] name, ushort mode)
    {
        P32(b, o, ino);
        P16(b, o + 4, (ushort)rec);
        b[o + 6] = (byte)name.Length;
        b[o + 7] = HasFileType ? FileType(mode) : (byte)0;
        name.CopyTo(b, o + 8);
    }

    void AppendBlock(uint ino, byte[] inode, byte[] data)
    {
        long n = Size(inode) / BlockSize;
        if (n >= 12) throw new NotSupportedException("Directory grows past 12 blocks.");
        uint goal = n > 0 ? U32(inode, 40 + (int)(n - 1) * 4) + 1 : GroupFirstBlock((int)((ino - 1) / InodesPerGroup));
        uint p = AllocBlock(goal);
        WriteBlock(p, data);
        P32(inode, 40 + (int)n * 4, p);
        P32(inode, 4, (uint)((n + 1) * BlockSize));
        P32(inode, 28, U32(inode, 28) + (uint)(BlockSize / 512));
        P32(inode, 16, Now); P32(inode, 12, Now);
        WriteInode(ino, inode);
    }

    bool RemoveEntry(uint dirIno, string name)
    {
        var di = ReadInode(dirIno);
        long nb = Size(di) / BlockSize;
        for (long k = 0; k < nb; k++)
        {
            uint p = Bmap(di, k);
            var b = ReadBlock(p);
            int o = 0, prev = -1;
            while (o < BlockSize)
            {
                uint eino = U32(b, o); int rec = U16(b, o + 4), nl = b[o + 6];
                if (eino != 0 && Encoding.Latin1.GetString(b, o + 8, nl) == name)
                {
                    if (prev >= 0) P16(b, prev + 4, (ushort)(U16(b, prev + 4) + rec));
                    else P32(b, o, 0);
                    WriteBlock(p, b);
                    P32(di, 16, Now); P32(di, 12, Now);
                    WriteInode(dirIno, di);
                    return true;
                }
                prev = o;
                o += rec;
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- public operations

    byte[] NewInode(ushort mode, ushort uid, ushort gid, uint atime, uint mtime, ushort links)
    {
        var i = new byte[128];
        P16(i, 0, mode); P16(i, 2, uid);
        P32(i, 8, atime); P32(i, 12, Now); P32(i, 16, mtime);
        P16(i, 24, gid); P16(i, 26, links);
        return i;
    }

    public uint Mkdir(uint parent, string name, ushort perm, ushort uid, ushort gid, uint mtime, bool libext2fs = false)
    {
        // The kernel spreads directories over groups; libext2fs (mke2fs) takes the first free inode.
        uint ino = AllocInode(parent, dir: true, spread: !libext2fs);
        var i = NewInode((ushort)(S_IFDIR | (perm & 0xFFF)), uid, gid, mtime, mtime, 2);
        var blk = new byte[BlockSize];
        PutDirent(blk, 0, ino, 12, "."u8.ToArray(), S_IFDIR);
        PutDirent(blk, 12, parent, BlockSize - 12, ".."u8.ToArray(), S_IFDIR);
        uint p = AllocBlock(GroupFirstBlock((int)((ino - 1) / InodesPerGroup)));
        WriteBlock(p, blk);
        P32(i, 40, p); P32(i, 4, (uint)BlockSize); P32(i, 28, (uint)(BlockSize / 512));
        WriteInode(ino, i);
        AddEntry(parent, name, ino, S_IFDIR);
        var pi = ReadInode(parent);
        P16(pi, 26, (ushort)(U16(pi, 26) + 1));
        P32(pi, 16, Now); P32(pi, 12, Now);
        WriteInode(parent, pi);
        return ino;
    }

    public uint CreateFile(uint parent, string name, ushort perm, ushort uid, ushort gid, uint atime, uint mtime, Stream data, long size, bool partialOnFull = false)
    {
        uint ino = AllocInode(parent, dir: false);
        var i = NewInode((ushort)(S_IFREG | (perm & 0xFFF)), uid, gid, atime, mtime, 1);
        WriteDataBlocks(ino, i, data, size, partialOnFull);
        WriteInode(ino, i);
        AddEntry(parent, name, ino, S_IFREG);
        return ino;
    }

    /// <summary>Replaces a regular file's contents in place (O_TRUNC semantics: same inode).</summary>
    public void Overwrite(uint ino, Stream data, long size, uint? mtime = null, bool partialOnFull = false)
    {
        var i = ReadInode(ino);
        FreeData(i);
        WriteDataBlocks(ino, i, data, size, partialOnFull);
        P32(i, 16, mtime ?? Now); P32(i, 12, Now);
        WriteInode(ino, i);
    }

    /// <summary>Rewrites a file's existing blocks (same size), as writes through an open file do.</summary>
    public void WriteInPlace(uint ino, byte[] data)
    {
        var i = ReadInode(ino);
        if (Size(i) != data.Length) throw new ArgumentException("size differs");
        var buf = new byte[BlockSize];
        for (long k = 0; k * BlockSize < data.Length; k++)
        {
            uint p = Bmap(i, k);
            if (p == 0) throw new IOException("hole in file");
            Array.Clear(buf);
            data.AsSpan((int)(k * BlockSize), (int)Math.Min(BlockSize, data.Length - k * BlockSize)).CopyTo(buf);
            WriteBlock(p, buf);
        }
        P32(i, 16, Now); P32(i, 12, Now);
        WriteInode(ino, i);
    }

    public uint Symlink(uint parent, string name, string target, ushort uid, ushort gid, uint mtime)
    {
        uint ino = AllocInode(parent, dir: false);
        var i = NewInode(S_IFLNK | 0x1FF, uid, gid, mtime, mtime, 1);
        var t = Encoding.Latin1.GetBytes(target);
        if (t.Length < 60) { t.CopyTo(i, 40); P32(i, 4, (uint)t.Length); }
        else WriteDataBlocks(ino, i, new MemoryStream(t), t.Length);
        WriteInode(ino, i);
        AddEntry(parent, name, ino, S_IFLNK);
        return ino;
    }

    /// <summary>Device node / fifo / socket. Linux 2.4 stores the old 16-bit dev_t in i_block[0].</summary>
    public uint Mknod(uint parent, string name, ushort mode, int major, int minor, ushort uid, ushort gid, uint mtime)
    {
        uint ino = AllocInode(parent, dir: false);
        var i = NewInode(mode, uid, gid, mtime, mtime, 1);
        if ((mode & S_IFMT) is S_IFCHR or S_IFBLK) P32(i, 40, (uint)(major << 8 | minor & 0xFF));
        WriteInode(ino, i);
        AddEntry(parent, name, ino, mode);
        return ino;
    }

    public void Link(uint parent, string name, uint ino)
    {
        var i = ReadInode(ino);
        P16(i, 26, (ushort)(U16(i, 26) + 1));
        P32(i, 12, Now);
        WriteInode(ino, i);
        AddEntry(parent, name, ino, Mode(i));
    }

    /// <summary>unlink / rm -rf of one name.</summary>
    public void Remove(uint parent, string name, bool recursive)
    {
        uint ino = Lookup(parent, name);
        if (ino == 0) return;
        var i = ReadInode(ino);
        bool isDir = (Mode(i) & S_IFMT) == S_IFDIR;
        if (isDir)
        {
            if (!recursive) throw new IOException($"{name} is a directory");
            foreach (var e in ListDir(ino).ToList())
                if (e.Name != "." && e.Name != "..") Remove(ino, e.Name, true);
            i = ReadInode(ino);
        }
        RemoveEntry(parent, name);
        int links = U16(i, 26) - (isDir ? 2 : 1);
        if (isDir)
        {
            var pi = ReadInode(parent);
            P16(pi, 26, (ushort)(U16(pi, 26) - 1));
            WriteInode(parent, pi);
        }
        if (links > 0) { P16(i, 26, (ushort)links); P32(i, 12, Now); WriteInode(ino, i); return; }
        FreeData(i);
        P16(i, 26, 0);
        P32(i, 20, Now);                          // dtime
        WriteInode(ino, i);
        FreeInode(ino, isDir);
    }

    public void Rename(uint fromDir, string fromName, uint toDir, string toName)
    {
        uint ino = Lookup(fromDir, fromName);
        if (ino == 0) throw new FileNotFoundException(fromName);
        var i = ReadInode(ino);
        if (Lookup(toDir, toName) != 0) Remove(toDir, toName, false);
        AddEntry(toDir, toName, ino, Mode(i));
        RemoveEntry(fromDir, fromName);
        P32(i, 12, Now);
        WriteInode(ino, i);
    }

    public void SetAttr(uint ino, ushort? perm = null, ushort? uid = null, ushort? gid = null, uint? atime = null, uint? mtime = null)
    {
        var i = ReadInode(ino);
        if (perm is { } p) P16(i, 0, (ushort)(Mode(i) & S_IFMT | p & 0xFFF));
        if (uid is { } u) P16(i, 2, u);
        if (gid is { } g) P16(i, 24, g);
        if (atime is { } a) P32(i, 8, a);
        if (mtime is { } m) P32(i, 16, m);
        P32(i, 12, Now);
        WriteInode(ino, i);
    }

    /// <summary>Resolves a path inside this volume (no symlink following). 0 when absent.</summary>
    public uint Resolve(string path)
    {
        uint cur = RootIno;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            cur = Lookup(cur, part);
            if (cur == 0) return 0;
        }
        return cur;
    }

    // ---------------------------------------------------------------- mke2fs helpers

    internal void InitRootAndReserved(uint now)
    {
        var bm = InodeBitmap(0);
        uint firstIno = U32(Super, 84);
        for (uint i = 0; i < firstIno - 1; i++) bm[i >> 3] |= (byte)(1 << (int)(i & 7));
        _dirtyIB.Add(0);
        GdAdd(0, 14, -(int)(firstIno - 1));
        FreeInodesTotal -= firstIno - 1;
        // Root directory (inode 2) is its own parent.
        var i2 = NewInode(S_IFDIR | 0x1ED, 0, 0, now, now, 2);
        var blk = new byte[BlockSize];
        PutDirent(blk, 0, RootIno, 12, "."u8.ToArray(), S_IFDIR);
        PutDirent(blk, 12, RootIno, BlockSize - 12, ".."u8.ToArray(), S_IFDIR);
        uint p = AllocBlock(FirstDataBlock);
        WriteBlock(p, blk);
        P32(i2, 40, p); P32(i2, 4, (uint)BlockSize); P32(i2, 28, (uint)(BlockSize / 512));
        WriteInode(RootIno, i2);
        GdAdd(0, 16, 1);
    }

    internal void MakeLostFound(uint now)
    {
        uint ino = Mkdir(RootIno, "lost+found", 0x1C0, 0, 0, now, libext2fs: true);
        var li = ReadInode(ino);
        int size = 0;                               // mke2fs: lpf_size starts at 0
        for (int k = 1; k < 12; k++)
        {
            if ((size += BlockSize) >= 16 * 1024) break;
            var empty = new byte[BlockSize];
            P16(empty, 4, (ushort)BlockSize);
            AppendBlock(ino, li, empty);
            li = ReadInode(ino);
        }
    }

    /// <summary>Internal ext3 journal (inode 8), JBD superblock v2, blocks allocated from the start.</summary>
    internal void AddJournal(int blocks, byte[] uuid)
    {
        if (blocks == 0) return;
        const uint ino = 8;
        var i = NewInode(S_IFREG | 0x180, 0, 0, Now, Now, 1);
        var data = new MemoryStream();
        var jsb = new byte[BlockSize];
        void B32(int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(jsb.AsSpan(o), v);
        B32(0, 0xC03B3998); B32(4, 4); B32(8, 0);
        B32(12, (uint)BlockSize); B32(16, (uint)blocks); B32(20, 1); B32(24, 1); B32(28, 0);
        uuid.AsSpan(0, 16).CopyTo(jsb.AsSpan(48));
        B32(64, 1);                                   // s_nr_users
        data.Write(jsb);
        // e2fsprogs 1.35 maps one block more than the journal size (i_size stays at the size).
        data.SetLength((long)(blocks + 1) * BlockSize);
        data.Position = 0;
        WriteDataBlocks(ino, i, data, data.Length);
        P32(i, 4, (uint)(blocks * BlockSize));
        WriteInode(ino, i);
        P32(Super, 224, ino);
        P32(Super, 92, FeatureCompat | 4);
        _superDirty = true;
    }

    internal void WriteSuperBackups(int groups, int descBlocks, Func<int, bool> hasSuper)
    {
        for (int g = 1; g < groups; g++)
        {
            if (!hasSuper(g)) continue;
            long gb = FirstDataBlock + (long)g * BlocksPerGroup;
            var copy = (byte[])Super.Clone();
            P16(copy, 90, (ushort)g);
            var blk = new byte[BlockSize];
            copy.CopyTo(blk, BlockSize == 1024 ? 0 : 0);
            WriteBlock(gb, blk);
            _s.Seek(_base + (gb + 1) * BlockSize, SeekOrigin.Begin);
            _s.Write(_gdt);
        }
    }

    /// <summary>Kernel mount bookkeeping: mount count, mount time, state "not clean" while mounted.</summary>
    public void Mounted()
    {
        P16(Super, 52, (ushort)(U16(Super, 52) + 1));
        P32(Super, 44, Now);
        P16(Super, 58, (ushort)(U16(Super, 58) & ~1));
        _superDirty = true;
    }

    /// <summary>Writes bitmaps, group descriptors and superblock; marks the volume clean (umount).</summary>
    public void Unmount()
    {
        P16(Super, 58, (ushort)(U16(Super, 58) | 1));
        P32(Super, 48, Now);
        _superDirty = true;
        Flush();
    }

    public void Flush()
    {
        foreach (int g in _dirtyBB) WriteBlock(GdBlockBitmap(g), _blockBitmaps[g]);
        foreach (int g in _dirtyIB) WriteBlock(GdInodeBitmap(g), _inodeBitmaps[g]);
        _dirtyBB.Clear(); _dirtyIB.Clear();
        if (_gdtDirty)
        {
            _s.Seek(_base + (FirstDataBlock + 1) * BlockSize, SeekOrigin.Begin);
            _s.Write(_gdt);
            _gdtDirty = false;
        }
        if (_superDirty)
        {
            _s.Seek(_base + 1024, SeekOrigin.Begin);
            _s.Write(Super);
            _superDirty = false;
        }
    }
}

/// <summary>ENOSPC.</summary>
public sealed class DiskFullException() : IOException("The file system is full.");
