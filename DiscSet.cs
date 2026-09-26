using System.Text.RegularExpressions;

namespace MegaRestore;

public enum DiscKind { D2000Kit, UpgradePatch, LinuxPartimage, Unknown }

/// <summary>One restore set: the discs of one release, in order.</summary>
public sealed class DiscSet : IDisposable
{
    public string Title { get; private init; } = "";
    public DiscKind Kind { get; private init; }
    public List<IsoReader> Discs { get; } = new();

    public void Dispose() { foreach (var d in Discs) d.Dispose(); }

    static (string title, int no, int of)? DiskId(IsoReader iso)
    {
        var t = iso.ReadText("DISK.ID");
        if (t == null) return null;
        var lines = t.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0) return null;
        var m = lines.Length > 1 ? Regex.Match(lines[1], @"#\s*(\d+)\s*of\s*(\d+)", RegexOptions.IgnoreCase) : Match.Empty;
        return (lines[0], m.Success ? int.Parse(m.Groups[1].Value) : 1, m.Success ? int.Parse(m.Groups[2].Value) : 1);
    }

    static DiscKind Classify(IsoReader iso)
    {
        var root = iso.List();
        bool Has(string n) => root.Any(e => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        if (root.Any(e => e.Name.StartsWith("DISKIMG", StringComparison.OrdinalIgnoreCase)) || Has("D2000.EXE")) return DiscKind.D2000Kit;
        if (root.Any(e => e.Name.EndsWith(".UDF", StringComparison.OrdinalIgnoreCase))) return DiscKind.UpgradePatch;
        if (Has("RESCUECD") || iso.Find("lib/images") != null) return DiscKind.LinuxPartimage;
        return DiscKind.Unknown;
    }

    static bool IsLinuxDisc1(IsoReader iso) =>
        iso.Find("lib/root/sbin/installboot.sh") != null && (iso.Find("bin/partitions") ?? iso.Find("bin/partitions.maxx")) != null
        && iso.Find("isolinux") != null;

    /// <summary>A later Linux disc was picked: find the disc 1 in the same folder whose images it continues.</summary>
    static IsoReader FindLinuxDisc1(IsoReader picked, IReadOnlyList<string> paths, Action<string>? log)
    {
        var ids = new HashSet<ulong>();
        var images = picked.Find("lib/images");
        if (images != null)
            foreach (var e in picked.List(images))
                if (LinuxRestore.VolumeId(picked, e) is { } v) ids.Add(v.id);
        string dir = Path.GetDirectoryName(Path.GetFullPath(picked.Path))!;
        foreach (var f in paths.Skip(1).Concat(Directory.EnumerateFiles(dir, "*.iso")))
        {
            IsoReader? iso = null;
            try
            {
                iso = new IsoReader(f);
                if (IsLinuxDisc1(iso) && iso.Find("lib/images") is { } im
                    && iso.List(im).Any(e => LinuxRestore.VolumeId(iso, e) is { } v && ids.Contains(v.id)))
                {
                    log?.Invoke($"Disc 1: {Path.GetFileName(f)}");
                    return iso;
                }
            }
            catch (Exception) { }
            iso?.Dispose();
        }
        throw new FileNotFoundException($"{Path.GetFileName(picked.Path)} is a later disc of a Linux restore set, and its disc 1 was not found in the same folder.");
    }

    /// <summary>
    /// Builds the set from the ISO(s) the user picked. With one D2000 disc picked, the other
    /// discs of the same release are looked up next to it (same first line of DISK.ID).
    /// </summary>
    public static DiscSet Open(IReadOnlyList<string> picked, Action<string>? log = null)
    {
        var first = new IsoReader(picked[0]);
        var kind = Classify(first);
        var id = DiskId(first);
        var set = new DiscSet { Kind = kind, Title = id?.title ?? first.VolumeId };
        if (kind == DiscKind.LinuxPartimage)
        {
            var disc1 = IsLinuxDisc1(first) ? first : FindLinuxDisc1(first, picked, log);
            if (disc1 != first) first.Dispose();
            var ver = disc1.ReadText("bin/version")?.Split('\n')[0].Trim();
            string folder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(disc1.Path))!);
            var linux = new DiscSet { Kind = kind, Title = $"{folder} {ver ?? disc1.VolumeId}".Trim() };
            linux.Discs.Add(disc1);
            foreach (var p in picked.Skip(1))            // other discs the user picked join the search pool
                try { var iso = new IsoReader(p); if (iso.Path != disc1.Path) linux.Discs.Add(iso); } catch (Exception) { }
            return linux;
        }
        if (kind != DiscKind.D2000Kit)
        {
            // Not restorable: name it by its file. DISK.ID can't be trusted there (the "fixed"
            // MAXX 2000 upgrade disc 1 carries the 2K Plus update disc's label).
            var other = new DiscSet { Kind = kind, Title = Path.GetFileNameWithoutExtension(first.Path) };
            other.Discs.Add(first);
            return other;
        }
        var found = new Dictionary<int, IsoReader>();
        int count = id?.of ?? 1;
        found[id?.no ?? 1] = first;
        var candidates = picked.Skip(1).ToList();
        if (found.Count < count)
            candidates.AddRange(Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(picked[0]))!, "*.iso")
                .Where(p => !picked.Contains(p, StringComparer.OrdinalIgnoreCase)));
        foreach (var p in candidates)
        {
            if (found.Count >= count) break;
            IsoReader? iso = null;
            try
            {
                iso = new IsoReader(p);
                var i = DiskId(iso);
                if (i != null && i.Value.title.Equals(set.Title, StringComparison.OrdinalIgnoreCase) && !found.ContainsKey(i.Value.no))
                {
                    found[i.Value.no] = iso;
                    log?.Invoke($"Disc {i.Value.no} of {count}: {Path.GetFileName(p)}");
                    continue;
                }
            }
            catch (Exception) { }
            iso?.Dispose();
        }
        for (int n = 1; n <= count; n++)
        {
            if (!found.TryGetValue(n, out var iso))
            {
                foreach (var d in found.Values) d.Dispose();
                throw new FileNotFoundException(
                    $"\"{set.Title}\" has {count} discs, but disc {n} was not found. Put all the ISOs of the set in one folder, or select them together.");
            }
            set.Discs.Add(iso);
        }
        return set;
    }
}
