using System.Text;

namespace MegaRestore;

/// <summary>
/// LILO 21.6 installer, as run by the Merit install ("lilo.safe -C conf" in a chroot), for the
/// configurations on the discs: boot=/dev/hda, map=/boot/map, install=/boot/boot.b, one image.
/// Reimplemented from LILO 21.4.3 (bsect.c, map.c, boot.c, geometry.c) and checked against a
/// real 21.6 install (Docs/11). CHS addressing (no lba32/linear): 5-byte SECTOR_ADDR
/// {sector|cyl_hi, cyl, device 0x80, head, count}, geometry 255 heads × 63 sectors.
/// Map file: [0] default command line (0x6B6D = none), [1-2] descriptor table (checksum 0xABCD
/// xor all words), [3] zero sector, [4] keytable, then per image a fallback sector, the options
/// sector and the sector-address pages (101 entries per page, the next page's address in the last slot).
/// The first stage (boot.b's first 0x1B6 bytes) goes to the boot sector; the second stage is
/// addressed in place in boot.b. Bytes LILO leaves uninitialised (descriptor name/password
/// padding, initrd, vga_mode) are written as zeros.
/// </summary>
static class Lilo
{
    const int Sec = 512, MaxBootSize = 0x1B6, DescrSize = 52, MaxImages = (Sec * 2 - 2) / DescrSize;
    const int Heads = 255, Spt = 63, BiosDev = 0x80;

    sealed class Conf
    {
        public Dictionary<string, string?> Global = new();
        public List<(string image, Dictionary<string, string?> opts)> Images = new();
    }

    static Conf Parse(string text)
    {
        var c = new Conf();
        Dictionary<string, string?> cur = c.Global;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            int eq = line.IndexOf('=');
            string key = (eq < 0 ? line : line[..eq]).Trim().ToLowerInvariant();
            string? val = eq < 0 ? null : line[(eq + 1)..].Trim().Trim('"');
            if (key is "image" or "other")
            {
                if (key == "other") throw new NotSupportedException("LILO: other= sections are not supported.");
                var d = new Dictionary<string, string?>();
                c.Images.Add((val!, d));
                cur = d;
                continue;
            }
            cur[key] = val;
        }
        return c;
    }

    static byte[] Addr(long lba)
    {
        long sector = lba % Spt + 1, t = lba / Spt, head = t % Heads, cyl = t / Heads;
        if (cyl >= 1024) throw new IOException($"LILO: cylinder {cyl} is beyond the BIOS limit (a file needs lba32).");
        return new[] { (byte)(sector | (cyl >> 8) << 6), (byte)(cyl & 0xFF), (byte)BiosDev, (byte)head, (byte)1 };
    }

    /// <summary>Absolute LBA of every 512-byte sector of a file (0 = hole).</summary>
    static long[] FileSectors(LinuxRestore r, string path)
    {
        var (fs, ino, part) = r.Inode(path);
        long start = r.Partition(part).start;
        var inode = fs.ReadInode(ino);
        long size = Ext2.Size(inode);
        int spb = fs.BlockSize / Sec;
        long n = (size + Sec - 1) / Sec;
        var list = new long[n];
        for (long k = 0; k < n; k++)
        {
            uint b = fs.Bmap(inode, k / spb);
            list[k] = b == 0 ? 0 : start + (long)b * spb + k % spb;
        }
        return list;
    }

    static int DevNumber(string dev)
    {
        var m = System.Text.RegularExpressions.Regex.Match(dev, @"^/dev/hd([a-d])(\d*)$");
        if (!m.Success) throw new NotSupportedException($"LILO: root device {dev}");
        int major = m.Groups[1].Value[0] < 'c' ? 3 : 22;
        int minor = ((m.Groups[1].Value[0] - 'a') % 2) * 64 + (m.Groups[2].Value.Length > 0 ? int.Parse(m.Groups[2].Value) : 0);
        return major << 8 | minor;
    }

    public static void Install(LinuxRestore r, string confPath)
    {
        var c = Parse(Encoding.Latin1.GetString(r.ReadPath(confPath)));
        string G(string k, string d) => c.Global.TryGetValue(k, out var v) && v != null ? v : d;
        bool F(Dictionary<string, string?> d, string k) => d.ContainsKey(k);
        if (F(c.Global, "lba32") || F(c.Global, "linear")) throw new NotSupportedException("LILO: lba32/linear");
        string boot = G("boot", "/dev/hda"), mapName = G("map", "/boot/map"), install = G("install", "/boot/boot.b");
        if (boot != "/dev/hda") throw new NotSupportedException($"LILO: boot={boot}");
        if (c.Images.Count == 0) throw new InvalidDataException("LILO: no image");

        // Boot sector: the current MBR, merged with boot.b's first stage.
        var img = r.Image;
        var bsect = new byte[Sec];
        img.Seek(0, SeekOrigin.Begin); img.ReadExactly(bsect);
        var orig = (byte[])bsect.Clone();
        var bootb = r.ReadPath(install);
        uint timestamp = BitConverter.ToUInt32(bsect, 20);
        Array.Copy(bootb, bsect, MaxBootSize);
        BitConverter.TryWriteBytes(bsect.AsSpan(20), timestamp);
        if (Encoding.ASCII.GetString(bsect, 6, 4) != "LILO" || BitConverter.ToUInt16(bsect, 10) != 1)
            throw new InvalidDataException($"LILO: {install} is not a LILO first stage.");
        // Second stage: boot.b sectors 1.. in place; room up to the first stage's code.
        var bootSecs = FileSectors(r, install);
        int secCap = 0;
        while (52 + (secCap + 1) * 5 <= MaxBootSize && bootb.AsSpan(52 + secCap * 5, 5).IndexOfAnyExcept((byte)0) < 0) secCap++;
        if (bootSecs.Length - 1 > secCap - 1) throw new IOException("LILO: second stage too big for the first stage's table.");
        for (int k = 1; k < bootSecs.Length; k++) Addr(bootSecs[k]).CopyTo(bsect, 52 + (k - 1) * 5);
        Array.Clear(bsect, 52 + (bootSecs.Length - 1) * 5, 5);
        bsect[510] = 0x55; bsect[511] = 0xAA;
        bsect[39] = (byte)(F(c.Global, "prompt") ? 1 : 0);
        int delay = int.Parse(G("delay", "0")), timeout = int.Parse(G("timeout", "-1"));
        BitConverter.TryWriteBytes(bsect.AsSpan(16), (ushort)(delay * 100 / 55));
        BitConverter.TryWriteBytes(bsect.AsSpan(14), timeout < 0 ? (ushort)0xFFFF : (ushort)(timeout * 100 / 55));
        if (c.Global.ContainsKey("message") || c.Global.ContainsKey("serial") || c.Global.ContainsKey("keytable"))
            throw new NotSupportedException("LILO: message/serial/keytable options");

        // Map contents, in the order LILO writes them.
        var sectors = new List<byte[]>();                    // map file sectors
        var descrs = new byte[Sec * 2];
        byte[] Zero() => new byte[Sec];
        var dc = Zero(); BitConverter.TryWriteBytes(dc, (ushort)0x6B6D);
        sectors.Add(dc); sectors.Add(Zero()); sectors.Add(Zero()); sectors.Add(Zero());
        var keytab = Zero(); for (int i = 0; i < 256; i++) keytab[i] = (byte)i;
        sectors.Add(keytab);                                 // map sector 4
        // Per-image address lists refer to map sectors by index; resolved after allocation.
        var pending = new List<(int descr, List<(bool map, long v)> list)>();
        var pendingPages = new List<(int slot, int firstPage, int pages)>();
        string? deflt = G("default", null!);
        int imageBase = deflt != null ? 1 : 0, next = imageBase;
        foreach (var (image, o) in c.Images)
        {
            string label = o.TryGetValue("label", out var l) && l != null ? l : Path.GetFileName(image);
            var opts = new StringBuilder();
            if (F(o, "read-only") || F(c.Global, "read-only")) opts.Append("ro ");
            if (F(o, "read-write") || F(c.Global, "read-write")) opts.Append("rw ");
            string? root = o.TryGetValue("root", out var rt) ? rt : c.Global.GetValueOrDefault("root");
            if (root != null) opts.Append($"root={DevNumber(root):x} ");
            if ((o.GetValueOrDefault("append") ?? c.Global.GetValueOrDefault("append")) is { } app) opts.Append(app);
            if (o.ContainsKey("initrd") || o.ContainsKey("vga") || o.ContainsKey("password"))
                throw new NotSupportedException("LILO: initrd/vga/password");
            string ostr = opts.ToString();
            if (ostr.EndsWith(' ')) ostr = ostr[..^1];

            int fallbackIdx = sectors.Count; sectors.Add(Zero());
            var os = Zero(); Encoding.ASCII.GetBytes(ostr).CopyTo(os, 0);
            int optionsIdx = sectors.Count; sectors.Add(os);

            var kernel = r.ReadPath(image);
            int setup = kernel[497] != 0 ? kernel[497] : 4;
            bool modern = Encoding.ASCII.GetString(kernel, 0x202, 4) == "HdrS" && BitConverter.ToUInt16(kernel, 0x206) >= 0x200;
            bool high = modern && (kernel[0x211] & 1) != 0;
            uint hstart = BitConverter.ToUInt32(kernel, 0x214);
            var ksecs = FileSectors(r, image);
            if (!high && ksecs.Length - setup - 2 > 1024) throw new IOException("LILO: kernel too big to load low.");

            var list = new List<(bool map, long v)> { (true, fallbackIdx), (true, optionsIdx) };
            foreach (var s in ksecs) list.Add(s == 0 ? (true, 3) : (false, s));   // holes → zero sector

            int slot;
            if (deflt != null && label == deflt) slot = 0;
            else slot = next++;
            if (slot >= MaxImages) throw new IOException("LILO: too many images");
            var d = new byte[DescrSize];
            Encoding.ASCII.GetBytes(label).CopyTo(d, 0);
            ushort flags = 16;                                // FLAG_KERNEL
            if (modern) flags |= 8;                           // FLAG_MODKRN
            if (high) BitConverter.TryWriteBytes(d.AsSpan(46), (ushort)(hstart / 4096));
            BitConverter.TryWriteBytes(d.AsSpan(48), flags);
            d.CopyTo(descrs, 2 + slot * DescrSize);
            // Address pages follow; each page holds 101 entries, the 102nd slot links the next page.
            int pages = (list.Count + 100) / 101;
            int firstPage = sectors.Count;
            for (int p = 0; p < pages; p++) sectors.Add(Zero());
            pending.Add((slot, list));
            pendingPages.Add((slot, firstPage, pages));
            r.Log($"  LILO: added {label}{(slot == 0 ? " *" : "")} ({ostr})");
        }

        // Create the temporary map, then fill in sector addresses now that its blocks are known.
        string tmp = mapName + "~";
        r.WritePath(tmp, new byte[sectors.Count * Sec], 0x180);
        var mapSecs = FileSectors(r, tmp);
        byte[] A(bool isMap, long v) => Addr(isMap ? mapSecs[v] : v);
        foreach (var (slot, firstPage, pages) in pendingPages)
        {
            var list = pending.First(x => x.descr == slot).list;
            A(true, firstPage).CopyTo(descrs, 2 + slot * DescrSize + 41);      // descr.start
            for (int p = 0; p < pages; p++)
            {
                var page = sectors[firstPage + p];
                int n = Math.Min(101, list.Count - p * 101);
                for (int k = 0; k < n; k++) { var (m, v) = list[p * 101 + k]; A(m, v).CopyTo(page, k * 5); }
                if (p + 1 < pages) A(true, firstPage + p + 1).CopyTo(page, 505);
            }
        }
        ushort cks = 0xABCD;
        for (int i = 1; i < Sec; i++) cks ^= BitConverter.ToUInt16(descrs, i * 2);
        BitConverter.TryWriteBytes(descrs.AsSpan(0), cks);
        Array.Copy(descrs, 0, sectors[1], 0, Sec);
        Array.Copy(descrs, Sec, sectors[2], 0, Sec);
        A(true, 1).CopyTo(bsect, 24); A(true, 2).CopyTo(bsect, 29); A(true, 0).CopyTo(bsect, 34);
        A(true, 4).CopyTo(bsect, 47);                         // keytable

        var map = new byte[sectors.Count * Sec];
        for (int i = 0; i < sectors.Count; i++) sectors[i].CopyTo(map, i * Sec);
        r.OverwriteInPlace(tmp, map);

        // Backup of the original boot sector (LILO keeps an existing one), boot sector, rename.
        string backup = $"/boot/boot.{DevNumber(boot):X4}";
        if (!r.PathExists(backup))
        {
            r.WritePath(backup, orig, 0x1A4);
            BitConverter.TryWriteBytes(bsect.AsSpan(20), r.Now);
        }
        img.Seek(0, SeekOrigin.Begin); img.Write(bsect);
        r.RenamePath(tmp, mapName);
    }

}
