using Godot;
using System;
using System.Collections.Generic;
using System.IO;

namespace OpenSwos.Assets;

/// <summary>
/// One-time importer for the SWOS 96/97 PC CD audio tree.
///
/// The core pitch SFX (KICKX, BGCRD3L, HOMEGOAL, whistles, crowd chants) and the
/// PIERCE commentary set ship ONLY on the SWOS 96/97 CD — they are absent from a
/// plain HD install. This class locates a CD image dropped into the user's PC
/// import folder (<see cref="DataPaths.PcInputFolderName"/> / its <c>cd/</c> subfolder,
/// or the in-repo dev image) and extracts just the <c>SFX\FX\</c> + <c>SFX\PIERCE\</c>
/// subtrees (~a few MB) into <c>user://cache/pc_sfx/</c>, once. <see cref="RawSample"/>
/// then discovers the loose *.RAW files from that cache.
///
/// CD image handling:
///   • <c>.iso</c>            — cooked ISO9660, 2048 bytes/sector, used as-is.
///   • <c>.img</c> / <c>.bin</c> — raw MODE1/2352: 2048 user bytes at byte offset 16
///                                of each 2352-byte sector (also probes MODE2/2352
///                                form1 at offset 24 and a plain 2048 layout).
///   • <c>.cue</c>            — parsed to find the referenced binary + track mode.
/// The ISO9660 parser reads the Primary Volume Descriptor at LBA 16, walks the
/// root directory record (PVD+156) and recurses only into SFX to keep it cheap.
/// Multi-byte directory fields exist in both endians; we read the little-endian
/// copies. ';1' version suffixes on file identifiers are stripped.
///
/// Writes ONLY to user://. The original CD image is never modified. All IO is
/// pure System.IO + GD.Print (thread-safe), so this runs on the background
/// preload thread that <see cref="RawSample"/> is driven from.
/// </summary>
public static class CdSfxImport
{
    // Cache location + population marker (holds the extracted file count).
    private const string CacheRel = "cache/pc_sfx";
    private const string MarkerName = ".extracted";

    // We only lift these two subtrees (skips other-language + FLC video — ~15 MB).
    private static readonly string[] WantedSubtrees = { "FX", "PIERCE" };

    // ISO9660 constants.
    private const int PvdLba = 16;
    private const int RootRecordOffset = 156;   // PVD + 156 → 34-byte root dir record

    private static readonly object s_lock = new();
    private static bool s_done;
    private static string s_cacheDir = "";

    /// <summary>
    /// Ensure the CD SFX tree is extracted into <c>user://cache/pc_sfx/</c> and
    /// return that directory (absolute), or "" if no CD image is available.
    /// Idempotent + thread-safe; the actual extraction runs at most once.
    /// </summary>
    public static string EnsureExtracted()
    {
        lock (s_lock)
        {
            if (s_done) return s_cacheDir;
            s_done = true;

            string cache = ProjectSettings.GlobalizePath("user://" + CacheRel);
            if (IsPopulated(cache)) { s_cacheDir = cache; return cache; }

            string image = FindCdImage();
            if (image.Length == 0) return "";   // no CD — caller degrades gracefully

            try
            {
                int count = Extract(image, cache);
                GD.Print($"[audio] CD SFX import complete: {count} files -> {cache}");
                s_cacheDir = cache;
            }
            catch (Exception e)
            {
                GD.PrintErr($"[audio] CD SFX import failed: {e.GetType().Name}: {e.Message}");
                s_cacheDir = "";
            }
            return s_cacheDir;
        }
    }

    // Cache counts as populated when the marker file is present.
    private static bool IsPopulated(string cache)
    {
        try { return File.Exists(Path.Combine(cache, MarkerName)); }
        catch { return false; }
    }

    // ── CD image discovery ────────────────────────────────────────────────────

    private static readonly string[] ImageExts = { ".cue", ".img", ".iso", ".bin" };

    // Search the PC import folders (+ a cd/ subfolder) and the dev reference path
    // for a CD image, preferring .cue (carries the track mode) then .img/.iso/.bin.
    internal static string FindCdImage()
    {
        var roots = new List<string>();
        foreach (string r in DataPaths.PcSearchRoots())
        {
            if (string.IsNullOrEmpty(r)) continue;
            roots.Add(r);
            roots.Add(Path.Combine(r, "cd"));
        }
        // Dev-machine reference image (read-only; never modified).
        roots.Add(ProjectSettings.GlobalizePath("res://../Swos9697_PC/SensiWs9/cd"));

        // Collect every candidate under the roots, then pick by extension priority.
        var found = new List<string>();
        foreach (string root in roots)
            CollectImages(root, found);

        foreach (string ext in ImageExts)
            foreach (string f in found)
                if (f.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return f;
        return "";
    }

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".godot", ".import", "bin", "obj", "node_modules", ".vs", ".idea", ".backups",
    };

    private static void CollectImages(string root, List<string> outList, int maxDepth = 4)
    {
        if (string.IsNullOrEmpty(root)) return;
        try { if (!Directory.Exists(root)) return; } catch { return; }

        var queue = new Queue<(string dir, int depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { files = Array.Empty<string>(); }
            foreach (string f in files)
            {
                string ext = Path.GetExtension(f);
                foreach (string want in ImageExts)
                    if (ext.Equals(want, StringComparison.OrdinalIgnoreCase)) { outList.Add(f); break; }
            }

            if (depth >= maxDepth) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (string s in subs)
                if (!SkipDirs.Contains(Path.GetFileName(s)))
                    queue.Enqueue((s, depth + 1));
        }
    }

    // ── CD sector reader ──────────────────────────────────────────────────────

    // A thin cooked-sector view over a raw CD image: `userSize` payload bytes at
    // `userOffset` inside every `sectorSize`-byte physical sector.
    private sealed class CdReader : IDisposable
    {
        private readonly FileStream _fs;
        private readonly int _sectorSize, _userOffset, _userSize;

        public CdReader(string path, int sectorSize, int userOffset, int userSize)
        {
            _fs = File.OpenRead(path);
            _sectorSize = sectorSize; _userOffset = userOffset; _userSize = userSize;
        }

        public int UserSize => _userSize;

        // The 2048-ish user payload of one logical sector (LBA).
        public byte[] ReadSector(int lba)
        {
            var buf = new byte[_userSize];
            _fs.Seek((long)lba * _sectorSize + _userOffset, SeekOrigin.Begin);
            int read = 0;
            while (read < _userSize)
            {
                int n = _fs.Read(buf, read, _userSize - read);
                if (n <= 0) break;
                read += n;
            }
            return buf;
        }

        // The `length` bytes of an extent starting at `lba` (spanning whole sectors).
        public byte[] ReadExtent(int lba, int length)
        {
            int sectors = (length + _userSize - 1) / _userSize;
            var buf = new byte[sectors * _userSize];
            for (int k = 0; k < sectors; k++)
                Array.Copy(ReadSector(lba + k), 0, buf, k * _userSize, _userSize);
            if (buf.Length == length) return buf;
            var trimmed = new byte[length];
            Array.Copy(buf, trimmed, length);
            return trimmed;
        }

        public void Dispose() => _fs.Dispose();
    }

    // Resolve a chosen image path (.cue/.img/.iso/.bin) to the raw binary + layout,
    // then open a CdReader whose LBA 16 actually reads the ISO9660 "CD001" magic.
    private static CdReader OpenImage(string image)
    {
        string binary = image;
        string ext = Path.GetExtension(image).ToLowerInvariant();

        if (ext == ".cue")
        {
            binary = ResolveCueBinary(image);
            if (binary.Length == 0)
                throw new IOException($"cue references no readable binary: {image}");
        }

        // Layout candidates, in probe order. MODE1/2352 is by far the common case
        // for a .img; .iso is cooked 2048. We validate each by the PVD magic.
        (int sect, int off)[] layouts = ext == ".iso"
            ? new[] { (2048, 0), (2352, 16), (2352, 24) }
            : new[] { (2352, 16), (2048, 0), (2352, 24) };

        foreach (var (sect, off) in layouts)
        {
            CdReader? probe = null;
            try
            {
                probe = new CdReader(binary, sect, off, 2048);
                byte[] pvd = probe.ReadSector(PvdLba);
                if (pvd[1] == 'C' && pvd[2] == 'D' && pvd[3] == '0' && pvd[4] == '0' && pvd[5] == '1')
                    return probe;   // caller disposes
                probe.Dispose();
            }
            catch { probe?.Dispose(); }
        }
        throw new IOException($"no ISO9660 volume found in {binary} (unsupported CD layout)");
    }

    // Pull the FILE "..." reference out of a .cue sheet (BINARY track sheets).
    internal static string ResolveCueBinary(string cue)
    {
        string dir = Path.GetDirectoryName(cue) ?? "";
        try
        {
            foreach (string raw in File.ReadAllLines(cue))
            {
                string line = raw.Trim();
                if (!line.StartsWith("FILE", StringComparison.OrdinalIgnoreCase)) continue;
                int q1 = line.IndexOf('"');
                int q2 = q1 >= 0 ? line.IndexOf('"', q1 + 1) : -1;
                string name = (q1 >= 0 && q2 > q1)
                    ? line.Substring(q1 + 1, q2 - q1 - 1)
                    : line.Substring(4).Trim().Split(' ')[0];
                // Case-insensitive: the name comes from the USER's cue sheet text, which
                // very often disagrees in case with the actual file (different rippers,
                // later renames). An exact-case miss here killed CD SFX *and* the menu
                // music on Linux/Android for people whose image was perfectly fine.
                if (Path.IsPathRooted(name))
                {
                    if (File.Exists(name)) return name;
                    string rooted = DataPaths.ResolveFile(
                        Path.GetDirectoryName(name) ?? "", Path.GetFileName(name));
                    if (rooted.Length > 0) return rooted;
                    continue;
                }
                string cand = DataPaths.ResolveFile(dir, name);
                if (cand.Length > 0) return cand;
            }
        }
        catch { /* fall through */ }

        // Fallback: same-basename .img / .bin next to the cue (any case).
        foreach (string alt in new[] { ".img", ".bin" })
        {
            string cand = DataPaths.ResolveFile(
                dir, Path.GetFileNameWithoutExtension(cue) + alt);
            if (cand.Length > 0) return cand;
        }
        return "";
    }

    // ── ISO9660 walk + extraction ─────────────────────────────────────────────

    private readonly struct DirEntry
    {
        public readonly string Name;
        public readonly bool IsDir;
        public readonly int Lba;
        public readonly int Length;
        public DirEntry(string name, bool isDir, int lba, int length)
        { Name = name; IsDir = isDir; Lba = lba; Length = length; }
    }

    private static int Extract(string image, string cache)
    {
        using CdReader cd = OpenImage(image);
        GD.Print($"[audio] importing SFX from CD image {Path.GetFileName(image)} ...");

        byte[] pvd = cd.ReadSector(PvdLba);
        int rootLba = Le32(pvd, RootRecordOffset + 2);
        int rootLen = Le32(pvd, RootRecordOffset + 10);

        // Find the SFX directory under the root.
        DirEntry sfx = default;
        bool haveSfx = false;
        foreach (DirEntry e in ReadDir(cd, rootLba, rootLen))
            if (e.IsDir && e.Name.Equals("SFX", StringComparison.OrdinalIgnoreCase))
            { sfx = e; haveSfx = true; break; }
        if (!haveSfx)
            throw new IOException("SFX directory not present on CD image");

        Directory.CreateDirectory(cache);
        int count = 0;
        foreach (DirEntry e in ReadDir(cd, sfx.Lba, sfx.Length))
        {
            if (!e.IsDir) continue;
            foreach (string want in WantedSubtrees)
                if (e.Name.Equals(want, StringComparison.OrdinalIgnoreCase))
                {
                    count += ExtractSubtree(cd, e, Path.Combine(cache, e.Name));
                    break;
                }
        }

        File.WriteAllText(Path.Combine(cache, MarkerName), count.ToString());
        return count;
    }

    // Recursively extract a directory subtree to `destDir`. Returns files written.
    private static int ExtractSubtree(CdReader cd, DirEntry dir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        int count = 0;
        foreach (DirEntry e in ReadDir(cd, dir.Lba, dir.Length))
        {
            if (e.IsDir)
                count += ExtractSubtree(cd, e, Path.Combine(destDir, e.Name));
            else
            {
                byte[] data = cd.ReadExtent(e.Lba, e.Length);
                File.WriteAllBytes(Path.Combine(destDir, e.Name), data);
                count++;
            }
        }
        return count;
    }

    // Enumerate the (real) records of one directory extent, skipping "." / "..".
    private static IEnumerable<DirEntry> ReadDir(CdReader cd, int lba, int length)
    {
        byte[] data = cd.ReadExtent(lba, length);
        int usz = cd.UserSize;
        int i = 0;
        while (i < length)
        {
            int recLen = data[i];
            if (recLen == 0)
            {
                // Records never straddle a sector — pad to the next boundary.
                i = ((i / usz) + 1) * usz;
                continue;
            }
            int nameLen = data[i + 32];
            byte flags = data[i + 25];
            int extLba = Le32(data, i + 2);
            int extLen = Le32(data, i + 10);

            // '.' == 0x00, '..' == 0x01 (single-byte identifiers).
            if (!(nameLen == 1 && (data[i + 33] == 0x00 || data[i + 33] == 0x01)))
            {
                string name = CleanName(data, i + 33, nameLen);
                bool isDir = (flags & 0x02) != 0;
                yield return new DirEntry(name, isDir, extLba, extLen);
            }
            i += recLen;
        }
    }

    // Decode a file identifier, dropping a ';N' version suffix.
    private static string CleanName(byte[] data, int start, int len)
    {
        var chars = new char[len];
        int n = 0;
        for (int k = 0; k < len; k++)
        {
            char c = (char)data[start + k];
            if (c == ';') break;
            chars[n++] = c;
        }
        return new string(chars, 0, n);
    }

    private static int Le32(byte[] b, int off) =>
        b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24);
}
