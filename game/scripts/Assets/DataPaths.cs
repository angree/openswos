using Godot;
using System.Collections.Generic;
using System.IO;

namespace OpenSwos.Assets;

/// <summary>
/// Central, export-safe resolver for the original SWOS asset directories.
///
/// The game needs three source folders:
///   • Amiga <c>grafs/</c> — sprites, pitch tile-maps, charset (mandatory for visuals).
///   • Amiga <c>data/</c>  — Amiga TEAM.* (optional; RNC-compressed).
///   • PC <c>DATA/</c>     — PC TEAM.* + POOLPLYR.DAT (the main 1730-team list).
///
/// "Bring your own game files" is deliberately forgiving. There are two user folders,
/// both alongside the game binary (and, as a fallback, under <c>user://</c>):
///   • <c>original_swos_adf/</c>   — drop your SWOS 96/97 Amiga floppy images (*.adf) here.
///   • <c>original_swos_files/</c> — auto-extracted output; ALSO accepts already-loose
///                                   game files (e.g. a WHDLoad / hard-disk install).
///
/// Each source is resolved by SEARCHING RECURSIVELY (depth-capped) under the
/// <c>original_swos_files/</c> roots for the folder that actually contains its key file,
/// so it works whether the importer wrote <c>.../amiga/disk2/grafs/...</c> OR the user
/// dumped loose files directly into <c>original_swos_files/</c>. If neither user root
/// has it, we fall back to the in-repo dev tree as EXACT direct paths (unchanged), so
/// the repo dev run + headless smoke resolve with zero setup.
///
/// All returned paths are GLOBALIZED absolute OS paths (never res:// / user://),
/// so callers can hand them straight to System.IO / the CLI-shared loaders.
/// </summary>
public static class DataPaths
{
    /// <summary>User INPUT folder — drop *.adf floppy images here.</summary>
    public const string InputFolderName = "original_swos_adf";

    /// <summary>User OUTPUT folder — extracted files land here; also accepts loose files.</summary>
    public const string OutputFolderName = "original_swos_files";

    /// <summary>
    /// OPTIONAL user folder — drop the PC (DOS) SWOS <c>DATA/</c> folder here. It only
    /// adds a slightly larger team list (~1730 PC vs ~1616 Amiga teams); it is NOT
    /// required and does not change the graphics (those always come from the Amiga files).
    /// </summary>
    public const string PcInputFolderName = "original_swos_pc";

    // Key files that prove a candidate directory is the real thing.
    private const string AmigaGrafsKey = "CJCTEAM1.RAW";
    private const string AmigaDataKey = "TEAM.000";
    private const string PcDataKey = "TEAM.000";

    // Directory names we never want to descend into during a recursive search.
    private static readonly HashSet<string> SkipDirs = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".godot", ".import", "bin", "obj", "node_modules", ".vs", ".idea", ".backups",
    };

    // ── Android public-storage roots ─────────────────────────────────────────
    //
    // On Android 13+ the app's user:// dir (Android/data/org.openswos.game/files)
    // is UNREACHABLE by third-party file managers, so users cannot drop their SWOS
    // files there. We therefore ALSO look in — and prefer — the app's own
    // Android/media/<package> directory. CRUCIAL: an app can freely read/write its
    // own Android/media/<pkg> tree with NO storage permission at all (the Android
    // 13/14 SAF lockdown targets Android/data + Android/obb, NOT Android/media), and
    // on-device file managers + USB/MTP CAN still browse it. This lets us ship with
    // ZERO storage permissions (no scary "all files access" prompt) while keeping a
    // user-reachable drop folder. Checked FIRST on Android; inert on every other OS.
    // SOURCE (drop) folders the user puts files into. These MUST live somewhere a
    // file manager can actually write, which in practice means public storage —
    // Download first. Reading NON-MEDIA files (.adf) from here requires all-files
    // access on Android 11+ (READ_EXTERNAL_STORAGE only covers media since 11 and is
    // inert on 13+), which is why the permission still exists. Replacing it properly
    // means a SAF folder picker, not another path.
    private static readonly string[] AndroidExternalBases =
    {
        "/storage/emulated/0/Download/OpenSWOS",
        "/storage/emulated/0/OpenSWOS",
    };

    // Additional SEARCH-only base: the app's own Android/media tree. Readable and
    // writable with no permission at all, but many file managers/OEMs make it awkward
    // for users to drop files into, so it is never advertised as THE drop folder —
    // it just means files found there still work.
    private static readonly string[] AndroidSecondaryBases =
    {
        "/storage/emulated/0/Android/media/org.openswos.game/OpenSWOS",
    };

    /// <summary>True when running on Android (feature flag OR OS name).</summary>
    public static bool IsAndroid() => OS.HasFeature("android") || OS.GetName() == "Android";

    /// <summary>
    /// Bases we may WRITE to. DELIBERATELY EMPTY on Android: extraction output must go
    /// to the app-private dir (user://), which always works and needs no permission.
    /// Writing into public storage was the fragile part — a failed write there used to
    /// leave a half-extracted tree the user could not even see.
    /// </summary>
    public static IEnumerable<string> AndroidWritableBaseRoots()
    {
        yield break;
    }

    /// <summary>
    /// Bases we SEARCH for user-supplied files, in priority order: the public drop
    /// folders first, then the app's own Android/media tree (Android only).
    /// </summary>
    public static IEnumerable<string> AndroidExternalBaseRoots()
    {
        if (!IsAndroid()) yield break;
        foreach (string b in AndroidExternalBases) yield return b;
        foreach (string b in AndroidSecondaryBases) yield return b;
    }

    /// <summary>Directory of the running executable (empty when it can't be determined).</summary>
    public static string ExeDir()
    {
        string exe = OS.GetExecutablePath();
        return string.IsNullOrEmpty(exe) ? "" : (Path.GetDirectoryName(exe) ?? "");
    }

    // ── User-folder roots ────────────────────────────────────────────────────

    /// <summary>Recursive-search roots for extracted / loose game files, in priority order.</summary>
    public static IEnumerable<string> OutputSearchRoots()
    {
        // App-owned Android/media folder first — file-manager-reachable, no permission.
        foreach (string b in AndroidExternalBaseRoots())
            yield return Path.Combine(b, OutputFolderName);
        string exeDir = ExeDir();
        if (exeDir.Length > 0)
            yield return Path.Combine(exeDir, OutputFolderName);
        yield return ProjectSettings.GlobalizePath("user://" + OutputFolderName);
    }

    /// <summary>
    /// Recursive-search roots for the OPTIONAL PC (DOS) DATA folder, in priority order.
    /// Mirrors <see cref="OutputSearchRoots"/> but points at <c>original_swos_pc/</c>.
    /// </summary>
    public static IEnumerable<string> PcSearchRoots()
    {
        foreach (string b in AndroidExternalBaseRoots())
            yield return Path.Combine(b, PcInputFolderName);
        string exeDir = ExeDir();
        if (exeDir.Length > 0)
            yield return Path.Combine(exeDir, PcInputFolderName);
        yield return ProjectSettings.GlobalizePath("user://" + PcInputFolderName);
    }

    /// <summary>Folders scanned for *.adf floppy images, in priority order.</summary>
    public static IEnumerable<string> InputSearchRoots()
    {
        foreach (string b in AndroidExternalBaseRoots())
            yield return Path.Combine(b, InputFolderName);
        string exeDir = ExeDir();
        if (exeDir.Length > 0)
            yield return Path.Combine(exeDir, InputFolderName);
        yield return ProjectSettings.GlobalizePath("user://" + InputFolderName);
        // Dev fallback — the read-only reference ADFs shipped in the repo.
        yield return ProjectSettings.GlobalizePath("res://../Swos9697_Amiga");
    }

    /// <summary>Preferred (non-creating) display path for the INPUT folder.</summary>
    public static string PreferredInputPath() => PreferredPath(InputFolderName);

    /// <summary>Preferred (non-creating) display path for the OUTPUT folder.</summary>
    public static string PreferredOutputPath() => PreferredPath(OutputFolderName);

    /// <summary>Preferred (non-creating) display path for the OPTIONAL PC folder.</summary>
    public static string PreferredPcInputPath() => PreferredPath(PcInputFolderName);

    private static string PreferredPath(string folder)
    {
        // On Android the user-reachable location is the app's Android/media folder,
        // so the hint must point there (not at the unreachable user:// dir).
        if (IsAndroid())
            return Path.Combine(AndroidExternalBases[0], folder);
        string exeDir = ExeDir();
        return exeDir.Length > 0
            ? Path.Combine(exeDir, folder)
            : ProjectSettings.GlobalizePath("user://" + folder);
    }

    /// <summary>
    /// First writable location for <paramref name="folder"/> (exeDir then user://),
    /// creating it. Some install dirs are read-only, so we fall back automatically.
    /// Returns "" only if even user:// can't be written.
    /// </summary>
    public static string FirstWritableRoot(string folder)
    {
        // Android: this loop is intentionally empty (see AndroidWritableBaseRoots) so we
        // fall straight through to user:// — the app-private dir, which is always
        // writable and needs no permission. Extraction output belongs there; only the
        // SOURCE files the user supplies live in public storage.
        foreach (string b in AndroidWritableBaseRoots())
        {
            string ext = Path.Combine(b, folder);
            if (CanWrite(ext)) return ext;
        }
        string exeDir = ExeDir();
        if (exeDir.Length > 0)
        {
            string cand = Path.Combine(exeDir, folder);
            if (CanWrite(cand)) return cand;
        }
        string user = ProjectSettings.GlobalizePath("user://" + folder);
        return CanWrite(user) ? user : "";
    }

    // ── Android startup ──────────────────────────────────────────────────────

    /// <summary>
    /// Android-only startup hook. Requests storage access, then best-effort creates the
    /// public drop folders (<c>Download/OpenSWOS/...</c>) so users can find them in a file
    /// manager, plus the <c>user://</c> folders so import never has zero targets.
    /// <para>
    /// The permission is unavoidable while the drop folder lives in public storage:
    /// reading a NON-MEDIA file (.adf) by path needs all-files access on Android 11+
    /// (READ_EXTERNAL_STORAGE covers only media since 11, and is inert on 13+). The grant
    /// is ASYNCHRONOUS, so the very first boot runs unpermissioned — callers must re-scan
    /// once <see cref="HasStorageAccess"/> flips instead of demanding an app restart.
    /// </para>
    /// Every step is silent on failure; no-op off Android.
    /// </summary>
    public static void AndroidStartupInit()
    {
        if (!IsAndroid()) return;

        try { OS.RequestPermissions(); } catch { /* not fatal */ }

        string[] subs = { InputFolderName, OutputFolderName, PcInputFolderName };

        // Public drop folders (need the grant to succeed; harmless no-op without it).
        foreach (string b in AndroidExternalBases)
            foreach (string sub in subs)
                TryMakeDir(b + "/" + sub);

        // user:// fallback folders — always created so import never has zero targets
        // (fixes the report that NO import folders existed on Android).
        foreach (string sub in subs)
            TryMakeDir("user://" + sub);
    }

    /// <summary>
    /// True when the public drop folders are actually reachable right now. Used to detect
    /// the moment an asynchronous all-files grant lands, so import can be retried without
    /// making the user restart the app. A permission-denied directory reports itself as
    /// "does not exist", so reachability IS the permission test. Always true off Android.
    /// </summary>
    public static bool HasStorageAccess()
    {
        if (!IsAndroid()) return true;
        foreach (string b in AndroidExternalBases)
        {
            try { if (Directory.Exists(b)) return true; } catch { /* denied */ }
        }
        return false;
    }

    // Best-effort recursive mkdir via Godot's DirAccess (handles both absolute OS paths
    // and user:// scheme). Never throws.
    private static void TryMakeDir(string path)
    {
        try { DirAccess.MakeDirRecursiveAbsolute(path); } catch { /* ignore */ }
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write_probe_" + System.Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    // ── Recursive search helper ──────────────────────────────────────────────

    // Breadth-first search under `root` (depth-capped) for the first directory that
    // satisfies `match`. Skips unreadable / irrelevant trees. Returns "" if none.
    private static string FindDir(string root, System.Func<string, bool> match, int maxDepth = 4)
    {
        if (string.IsNullOrEmpty(root)) return "";
        try { if (!Directory.Exists(root)) return ""; } catch { return ""; }

        var queue = new Queue<(string dir, int depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            try { if (match(dir)) return dir; }
            catch { /* unreadable candidate — keep going */ }

            if (depth >= maxDepth) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (string s in subs)
            {
                if (SkipDirs.Contains(Path.GetFileName(s))) continue;
                queue.Enqueue((s, depth + 1));
            }
        }
        return "";
    }

    // ── Case-insensitive resolution ──────────────────────────────────────────
    //
    // Linux, Android and the R36S handheld all use case-SENSITIVE filesystems;
    // Windows does not. Every lookup of a SWOS data file therefore has to be
    // case-insensitive, because the casing is whatever the USER's copy happens to
    // have: a WHDLoad/hard-disk install, a non-retail ADF, or — very commonly — a
    // DOS install copied off a FAT partition on Linux, which lowercases 8.3 names
    // (team.000, cjcteam1.raw). Matching exact case is why "no SWOS data" was
    // reported on Linux/Android by users whose files were in exactly the right
    // place. This never reproduces on a Windows dev machine.

    /// <summary>
    /// Resolve <paramref name="name"/> inside <paramref name="dir"/> ignoring case, and
    /// return its REAL path (the actual on-disk casing) so callers can open it directly.
    /// Returns "" when absent. Exact case is tried first, so Windows costs nothing.
    /// </summary>
    public static string ResolveFile(string dir, string name)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return "";
        try
        {
            string exact = Path.Combine(dir, name);
            if (File.Exists(exact)) return exact;

            foreach (string hit in Directory.EnumerateFiles(dir, name, CaseInsensitiveScan))
                return hit;
        }
        catch { /* unreadable / denied */ }
        return "";
    }

    /// <summary>
    /// Case-insensitive sibling-directory lookup (e.g. the Amiga <c>data/</c> folder,
    /// which a non-retail dump may store as <c>DATA/</c>). "" when absent.
    /// </summary>
    public static string ResolveDir(string parent, string name)
    {
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)) return "";
        try
        {
            string exact = Path.Combine(parent, name);
            if (Directory.Exists(exact)) return exact;

            foreach (string hit in Directory.EnumerateDirectories(parent, name, CaseInsensitiveScan))
                return hit;
        }
        catch { /* unreadable / denied */ }
        return "";
    }

    /// <summary>Shared options for every case-insensitive probe in the project.</summary>
    internal static EnumerationOptions CaseInsensitiveScan => new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        IgnoreInaccessible = true,
    };

    private static bool HasFile(string dir, string file) => ResolveFile(dir, file).Length > 0;

    // Search all original_swos_files roots for a dir matching `match`; "" if none.
    private static string SearchOutputRoots(System.Func<string, bool> match)
    {
        foreach (string root in OutputSearchRoots())
        {
            string hit = FindDir(root, match);
            if (hit.Length > 0) return hit;
        }
        return "";
    }

    // Search all original_swos_pc roots for a dir matching `match`; "" if none.
    private static string SearchPcRoots(System.Func<string, bool> match)
    {
        foreach (string root in PcSearchRoots())
        {
            string hit = FindDir(root, match);
            if (hit.Length > 0) return hit;
        }
        return "";
    }

    // Returns `dir` if it exists and contains `keyFile`, else "".
    private static string DirWith(string dir, string keyFile)
    {
        if (string.IsNullOrEmpty(dir)) return "";
        return HasFile(dir, keyFile) ? dir : "";
    }

    // ── Resolvers ────────────────────────────────────────────────────────────

    /// <summary>Resolved Amiga <c>grafs/</c> directory, or "" if none found.</summary>
    public static string AmigaGrafsDir()
    {
        string hit = SearchOutputRoots(d => HasFile(d, AmigaGrafsKey));
        if (hit.Length > 0) return hit;
        // Dev fallback — exact direct path (kept working for repo dev + smoke).
        return DirWith(
            ProjectSettings.GlobalizePath("res://../assets/extracted/amiga/disk2/grafs"),
            AmigaGrafsKey);
    }

    /// <summary>
    /// Resolved Amiga <c>data/</c> directory (Amiga TEAM.* + pitch maps live near grafs),
    /// or "" if none found. Prefers the Amiga tree so it never picks up PC's TEAM.000.
    /// </summary>
    public static string AmigaDataDir()
    {
        string grafs = AmigaGrafsDir();
        if (grafs.Length > 0)
        {
            // Loose all-in-one folder: Amiga TEAM.* sit right next to the graphics.
            if (HasFile(grafs, AmigaDataKey)) return grafs;
            // Extracted tree: `data` is the sibling of `grafs`.
            string? parent = Path.GetDirectoryName(grafs);
            if (!string.IsNullOrEmpty(parent))
            {
                // Case-insensitive: a non-retail dump may store DATA/ or Data/.
                string sibling = ResolveDir(parent, "data");
                if (sibling.Length > 0 && HasFile(sibling, AmigaDataKey)) return sibling;
            }
        }

        // General recursive fallback: any RNC-compressed TEAM.000 (Amiga TEAM.* are
        // RNC-packed; PC's are raw). POOLPLYR.DAT exists in BOTH the Amiga and PC data
        // folders, so it cannot discriminate — compression is the reliable marker.
        string hit = SearchOutputRoots(d => IsRnc(ResolveFile(d, AmigaDataKey)));
        if (hit.Length > 0) return hit;

        // Dev fallback — exact direct path.
        return DirWith(
            ProjectSettings.GlobalizePath("res://../assets/extracted/amiga/disk2/data"),
            AmigaDataKey);
    }

    /// <summary>Resolved PC <c>DATA/</c> directory, or "" if none found (best-effort).</summary>
    public static string PcDataDir()
    {
        // A PC DATA dir has a RAW (uncompressed) TEAM.000. Both the PC and Amiga data
        // folders contain TEAM.000 + POOLPLYR.DAT, so those cannot discriminate; the
        // Amiga TEAM.* are RNC-compressed while the PC ones are raw — that IS the marker.
        // (Without this check, an Amiga-only import made PcDataDir point at the Amiga
        // data folder, PcAssetSource failed to parse the RNC bytes, _allTeams stayed
        // empty, and the menu refused to start.)

        // 1) The dedicated OPTIONAL folder for the PC (DOS) DATA — preferred.
        string pc = SearchPcRoots(d => HasFile(d, PcDataKey) && !IsRnc(ResolveFile(d, PcDataKey)));
        if (pc.Length > 0) return pc;

        // 2) Back-compat: PC DATA dropped into the general original_swos_files/ tree.
        string hit = SearchOutputRoots(d => HasFile(d, PcDataKey) && !IsRnc(ResolveFile(d, PcDataKey)));
        if (hit.Length > 0) return hit;

        // Dev fallback — exact direct path.
        return DirWith(
            ProjectSettings.GlobalizePath("res://../Swos9697_PC/SensiWs9/SOC/DATA"),
            PcDataKey);
    }

    // True if the file begins with the RNC ProPack signature ("RNC"). Amiga SWOS
    // stores TEAM.* / graphics RNC-compressed; the PC release stores TEAM.* raw.
    private static bool IsRnc(string file)
    {
        try
        {
            using FileStream fs = File.OpenRead(file);
            return fs.ReadByte() == 'R' && fs.ReadByte() == 'N' && fs.ReadByte() == 'C';
        }
        catch { return false; }
    }

    /// <summary>True when the mandatory Amiga graphics (grafs/CJCTEAM1.RAW) are available.</summary>
    public static bool HasAmigaGraphics() => AmigaGrafsDir().Length > 0;
}
