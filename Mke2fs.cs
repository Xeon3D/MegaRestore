namespace MegaRestore;

/// <summary>
/// mke2fs as shipped on the Merit rescue CD (e2fsprogs 1.35), for the options the install
/// scripts use: [-m 0] [-j], then "tune2fs -c 0 -i 0". Defaults of that release:
/// &lt; 3 MB: 1 KiB blocks, 8192 bytes/inode; &lt; 512 MB: 1 KiB, 4096; else 4 KiB, 8192.
/// Revision 1, 128-byte inodes, first inode 11, features filetype + sparse_super, journal
/// (with -j) sized by ext2fs_default_journal_size. Layout per ext2fs_initialize /
/// ext2fs_allocate_tables; lost+found grown to 16 KiB. UUID derived from a seed so the output
/// is reproducible.
/// </summary>
public static class Mke2fs
{
    public sealed record Options(bool Journal, int ReservedPercent, uint Now, byte[] Uuid);

    static bool HasSuper(int g)
    {
        if (g <= 1) return true;
        foreach (int p in new[] { 3, 5, 7 })
        {
            long x = p;
            while (x < g) x *= p;
            if (x == g) return true;
        }
        return false;
    }

    /// <summary>ext2fs_default_journal_size of e2fsprogs 1.35 (verified: 887 583 blocks → 8192).</summary>
    public static int DefaultJournalBlocks(uint blocks) =>
        blocks < 2048 ? 0 : blocks < 32768 ? 1024 : blocks < 256 * 1024 ? 4096 : 8192;

    public static Ext2 Format(Stream s, long baseOffset, long bytes, Options o)
    {
        long mb = bytes / (1024 * 1024);
        int bs = mb < 3 ? 1024 : mb < 512 ? 1024 : 4096;
        int ratio = mb < 3 ? 8192 : mb < 512 ? 4096 : 8192;
        uint blocks = (uint)(bytes / bs);
        uint first = bs == 1024 ? 1u : 0u;
        uint bpg = (uint)bs * 8;
        int inodeSize = 128, ipb = bs / inodeSize;
        uint inodesWanted = (uint)((long)blocks * bs / ratio);

        int groups, descBlocks, itb;
        uint ipg;
        while (true)
        {
            groups = (int)((blocks - first + bpg - 1) / bpg);
            descBlocks = (groups * 32 + bs - 1) / bs;
            ipg = (uint)((inodesWanted + groups - 1) / groups);
            if (ipg > bs * 8) ipg = (uint)bs * 8;
            itb = (int)((ipg * inodeSize + bs - 1) / bs);
            ipg = (uint)(itb * bs / inodeSize) & ~7u;
            itb = (int)((ipg * inodeSize + bs - 1) / bs);
            // The last group must hold its own overhead plus some data.
            uint rem = (blocks - first) % bpg;
            int overhead = 2 + itb + (HasSuper(groups - 1) ? 1 + descBlocks : 0);
            if (rem != 0 && rem < overhead + 50) { blocks -= rem; continue; }
            break;
        }
        uint inodes = ipg * (uint)groups;

        var sb = new byte[1024];
        Ext2.P32(sb, 0, inodes);
        Ext2.P32(sb, 4, blocks);
        Ext2.P32(sb, 8, (uint)((long)blocks * o.ReservedPercent / 100));
        Ext2.P32(sb, 20, first);
        Ext2.P32(sb, 24, (uint)(bs == 1024 ? 0 : bs == 2048 ? 1 : 2));
        Ext2.P32(sb, 28, (uint)(bs == 1024 ? 0 : bs == 2048 ? 1 : 2));
        Ext2.P32(sb, 32, bpg); Ext2.P32(sb, 36, bpg); Ext2.P32(sb, 40, ipg);
        Ext2.P32(sb, 48, o.Now);                    // wtime
        Ext2.P16(sb, 52, 0);                        // mnt_count
        int jitter = 0; foreach (var b in o.Uuid.AsSpan(0, 16)) jitter += b;
        Ext2.P16(sb, 54, (ushort)(20 + jitter % 20)); // EXT2_DFL_MAX_MNT_COUNT + jitter from the UUID
        Ext2.P16(sb, 56, 0xEF53);
        Ext2.P16(sb, 58, 1);                        // clean
        Ext2.P16(sb, 60, 1);                        // errors = continue
        Ext2.P32(sb, 64, o.Now);                    // lastcheck
        Ext2.P32(sb, 68, 15552000);                 // EXT2_DFL_CHECKINTERVAL (180 days)
        Ext2.P32(sb, 72, 0);                        // creator OS Linux
        Ext2.P32(sb, 76, 1);                        // dynamic revision
        Ext2.P32(sb, 84, 11);                       // first inode
        Ext2.P16(sb, 88, (ushort)inodeSize);
        Ext2.P32(sb, 96, 0x2);                      // incompat: filetype
        Ext2.P32(sb, 100, 0x1);                     // ro_compat: sparse_super
        o.Uuid.AsSpan(0, 16).CopyTo(sb.AsSpan(104));
        var seed = System.Security.Cryptography.MD5.HashData(o.Uuid);  // s_hash_seed (random in mke2fs)
        seed.CopyTo(sb, 236);
        sb[252] = 2;                                // s_def_hash_version = TEA (0 legacy, 1 half_md4, 2 tea)

        // Group descriptors and table placement (ext2fs_allocate_group_table).
        var gdt = new byte[descBlocks * bs];
        var used = new bool[blocks];
        void Mark(uint b) { if (b < blocks) used[b] = true; }
        for (int g = 0; g < groups; g++)
        {
            uint gb = first + (uint)g * bpg;
            if (HasSuper(g)) for (uint k = 0; k <= descBlocks; k++) Mark(gb + k);
        }
        for (int g = 0; g < groups; g++)
        {
            uint gb = first + (uint)g * bpg, last = Math.Min(gb + bpg, blocks - 1);
            uint start = gb + 3 + (uint)descBlocks;
            if (start > last) start = gb;
            uint FindRun(uint from, int n)
            {
                for (uint b = from; b + n <= last + 1; b++)
                {
                    bool ok = true;
                    for (int k = 0; k < n && ok; k++) ok = !used[b + k];
                    if (ok) return b;
                }
                throw new IOException("Partition too small for the file system tables.");
            }
            uint it = FindRun(start, itb); for (int k = 0; k < itb; k++) Mark(it + (uint)k);
            uint bb = FindRun(gb, 1); Mark(bb);
            uint ib = FindRun(gb, 1); Mark(ib);
            Ext2.P32(gdt, g * 32, bb); Ext2.P32(gdt, g * 32 + 4, ib); Ext2.P32(gdt, g * 32 + 8, it);
        }

        // Zero the inode tables (mke2fs writes them) and the first blocks.
        var zero = new byte[bs * 64];
        for (int g = 0; g < groups; g++)
        {
            long it = Ext2.U32(gdt, g * 32 + 8), left = itb;
            while (left > 0)
            {
                int n = (int)Math.Min(64, left);
                s.Seek(baseOffset + it * bs, SeekOrigin.Begin);
                s.Write(zero, 0, n * bs);
                it += n; left -= n;
            }
        }
        s.Seek(baseOffset, SeekOrigin.Begin);
        s.Write(new byte[1024]);                    // boot block area (mke2fs zaps it)

        // Bitmaps: metadata used; padding beyond the end set.
        long freeBlocks = 0;
        for (int g = 0; g < groups; g++)
        {
            uint gb = first + (uint)g * bpg;
            uint n = Math.Min(bpg, blocks - gb);
            var bbm = new byte[bs];
            int gfree = 0;
            for (uint k = 0; k < bs * 8; k++)
            {
                bool set = k >= n || used[gb + k];
                if (set) bbm[k >> 3] |= (byte)(1 << (int)(k & 7));
                else gfree++;
            }
            var ibm = new byte[bs];
            for (uint k = ipg; k < bs * 8; k++) ibm[k >> 3] |= (byte)(1 << (int)(k & 7));
            Ext2.P16(gdt, g * 32 + 12, (ushort)gfree);
            Ext2.P16(gdt, g * 32 + 14, (ushort)ipg);
            freeBlocks += gfree;
        }
        Ext2.P32(sb, 12, (uint)freeBlocks);
        Ext2.P32(sb, 16, inodes);

        var fs = Ext2.CreateEmpty(s, baseOffset, bs, sb, gdt);
        fs.Now = o.Now;
        for (int g = 0; g < groups; g++)
        {
            uint gb = first + (uint)g * bpg, n = Math.Min(bpg, blocks - gb);
            var bbm = new byte[bs];
            for (uint k = 0; k < bs * 8; k++)
                if (k >= n || used[gb + k]) bbm[k >> 3] |= (byte)(1 << (int)(k & 7));
            var ibm = new byte[bs];
            for (uint k = ipg; k < bs * 8; k++) ibm[k >> 3] |= (byte)(1 << (int)(k & 7));
            fs.SetBitmaps(g, bbm, ibm);
        }
        fs.InitRootAndReserved(o.Now);
        fs.MakeLostFound(o.Now);
        if (o.Journal) fs.AddJournal(DefaultJournalBlocks(blocks), o.Uuid);
        fs.Flush();
        fs.WriteSuperBackups(groups, descBlocks, HasSuper);
        return fs;
    }
}
