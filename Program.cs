using System.Text;
using System.Security.Cryptography;

namespace MegaRestore;

public static class Restore
{
    /// <summary>Stops early when the target drive plainly cannot hold the data (sparse image).</summary>
    public static void CheckSpace(string outPath, long needBytes, Action<string> log)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(outPath));
        if (string.IsNullOrEmpty(root)) return;
        long free = new DriveInfo(root).AvailableFreeSpace;
        log($"Space: about {needBytes / 1048576:N0} MB needed, {free / 1048576:N0} MB free on {root}");
        if (free < needBytes)
            throw new IOException($"Not enough free space on {root}: about {needBytes / 1048576:N0} MB are needed, {free / 1048576:N0} MB are free.");
    }

    public static void Run(IReadOnlyList<string> isos, string outPath, long sectors,
                           Action<string> log, Action<double> progress, CancellationToken ct)
    {
        using var set = DiscSet.Open(isos, log);
        switch (set.Kind)
        {
            case DiscKind.D2000Kit:
                new DosRestore(set, log, progress, ct).Run(outPath, sectors);
                break;
            case DiscKind.LinuxPartimage:
                new LinuxRestore(set, log, progress, ct).Run(outPath, sectors);
                break;
            default:
                throw new NotSupportedException($"{set.Title}: {MainForm.Describe(set.Kind)}.");
        }
    }
}

static class Program
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool AttachConsole(int pid);

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--cli")
        {
            AttachConsole(-1);          // WinExe: print to the console we were started from
            return Cli(args[1..]);
        }
        if (args.Length == 4 && args[0] == "--cat")       // debugging: copy one file out of an ISO
        {
            using var iso = new IsoReader(args[1]);
            var e = iso.Find(args[2]) ?? throw new FileNotFoundException(args[2]);
            using var o = File.Create(args[3]);
            iso.Open(e).CopyTo(o);
            return 0;
        }
        if (args.Length == 4 && args[0] == "--selftest-ext2")   // out.img sizeMB journal(0/1)
        {
            using var f = new FileStream(args[1], FileMode.Create, FileAccess.ReadWrite);
            long bytes = long.Parse(args[2]) << 20;
            f.SetLength(bytes);
            var fs = Mke2fs.Format(f, 0, bytes, new Mke2fs.Options(args[3] == "1", 0, 1_100_000_000, new byte[16]));
            fs.Mounted();
            var rnd = new Random(1);
            uint d = fs.Mkdir(Ext2.RootIno, "etc", 0x1ED, 0, 0, 1_000_000_000);
            uint sub = fs.Mkdir(d, "deep", 0x1ED, 0, 0, 1_000_000_000);
            foreach (int size in new[] { 0, 1, 1000, 12 * 1024, 13 * 1024, 300 * 1024, 5 << 20 })
            {
                var data = new byte[size]; rnd.NextBytes(data);
                fs.CreateFile(sub, $"f{size}", 0x1A4, 0, 0, 1_000_000_000, 1_000_000_000, new MemoryStream(data), size);
            }
            for (int k = 0; k < 400; k++)
                fs.CreateFile(d, $"many{k:000}.conf", 0x1A4, 0, 0, 1, 1, new MemoryStream(new byte[k * 37]), k * 37);
            fs.Symlink(d, "short", "deep/f1000", 0, 0, 1);
            fs.Symlink(d, "long", new string('x', 100), 0, 0, 1);
            fs.Mknod(d, "hda", Ext2.S_IFBLK | 0x1B0, 3, 0, 0, 6, 1);
            fs.Mknod(d, "fifo", Ext2.S_IFIFO | 0x1A4, 0, 0, 0, 0, 1);
            fs.Link(d, "hard", fs.Lookup(sub, "f1000"));
            for (int k = 0; k < 400; k += 3) fs.Remove(d, $"many{k:000}.conf", false);
            fs.Remove(sub, "f300", false);
            fs.Rename(sub, "f13312", d, "moved");
            fs.Overwrite(fs.Lookup(sub, "f12288"), new MemoryStream(new byte[70000]), 70000);
            fs.Remove(Ext2.RootIno, "lost+found", true);
            fs.Unmount();
            Console.WriteLine("ok");
            return 0;
        }
        if (args.Length == 3 && args[0] == "--walk")      // debugging: img sector → every path with metadata + md5
        {
            AttachConsole(-1);
            using var f = File.OpenRead(args[1]);
            var fs = Ext2.Open(f, long.Parse(args[2]) * 512);
            var outp = new StringBuilder();
            void Walk(uint dir, string path)
            {
                foreach (var e in fs.ListDir(dir).OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (e.Name is "." or "..") continue;
                    var i = fs.ReadInode(e.Ino);
                    ushort mode = Ext2.Mode(i);
                    string p = path + "/" + e.Name, extra = "";
                    int t = mode & Ext2.S_IFMT;
                    if (t == Ext2.S_IFREG) extra = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(fs.ReadData(e.Ino))).ToLowerInvariant();
                    else if (t == Ext2.S_IFLNK) extra = "-> " + Encoding.Latin1.GetString(fs.ReadData(e.Ino));
                    else if (t is Ext2.S_IFCHR or Ext2.S_IFBLK) extra = "dev " + Ext2.U32(i, 40).ToString("x");
                    outp.Append($"{p}\t{Convert.ToString(mode, 8)}\t{Ext2.U16(i, 2)}\t{Ext2.U16(i, 24)}\t{(t == Ext2.S_IFDIR ? 0 : Ext2.Size(i))}\t{Ext2.U32(i, 16)}\t{Ext2.U16(i, 26)}\t{extra}\n");
                    if (t == Ext2.S_IFDIR) Walk(e.Ino, p);
                }
            }
            Walk(Ext2.RootIno, "");
            Console.Write(outp);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--ls")        // debugging: list an ISO directory
        {
            AttachConsole(-1);
            using var iso = new IsoReader(args[1]);
            var d = iso.Find(args[2]);
            Console.WriteLine($"{iso.VolumeId} raw={iso.RawSectors}");
            foreach (var e in d == null ? [] : d.IsDir ? iso.List(d) : [d])
                Console.WriteLine($"{(e.IsDir ? "d" : "-")} {Convert.ToString(e.Mode & 0xFFF, 8),4} {e.Size,11} {e.Name}{(e.SymlinkTarget != null ? " -> " + e.SymlinkTarget : "")}");
            return 0;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>MegaRestore --cli [--size-cyl N] [--hash] out.img disc1.iso [disc2.iso ...]</summary>
    static int Cli(string[] a)
    {
        long cyl = MainForm.Sizes[0].Cyl;
        bool hash = false, keep = false;
        var rest = new List<string>();
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == "--size-cyl") cyl = long.Parse(a[++i]);
            else if (a[i] == "--hash") hash = true;
            else if (a[i] == "--keep") keep = true;          // debugging: keep the image on failure
            else rest.Add(a[i]);
        }
        if (rest.Count < 2) { Console.Error.WriteLine("usage: MegaRestore --cli [--size-cyl N] [--hash] out.img disc1.iso [disc2.iso ...]"); return 2; }
        if (File.Exists(rest[0])) { Console.Error.WriteLine($"Error: {rest[0]} already exists."); return 1; }
        int last = -1;
        try
        {
            Restore.Run(rest.Skip(1).ToList(), rest[0], cyl * 16 * 63, Console.WriteLine, f =>
            {
                int p = (int)(f * 20);
                if (p != last) { last = p; Console.WriteLine($"  {f:P0}"); }
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            if (!keep) try { File.Delete(rest[0]); } catch { }          // ours: it did not exist before
            return 1;
        }
        if (hash)
        {
            using var f = File.OpenRead(rest[0]);
            Console.WriteLine($"SHA256 {Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant()}  {rest[0]}");
        }
        return 0;
    }
}
