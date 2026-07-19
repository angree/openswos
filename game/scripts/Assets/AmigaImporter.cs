using Godot;
using OpenSwos.Tools.AdfExtract;
using System.Collections.Generic;
using System.IO;

namespace OpenSwos.Assets;

/// <summary>
/// First-run importer: turns raw SWOS Amiga floppy images (*.adf) dropped into
/// <c>original_swos_adf/</c> into extracted files under <c>original_swos_files/</c>,
/// which <see cref="DataPaths"/> then resolves against.
///
/// This is a plain ADF→files extraction — it writes the STORED file bytes verbatim.
/// RNC decompression happens later, at load time (AmigaSpriteAtlas/AmigaPitch/…),
/// so nothing is decoded here.
///
/// It never throws: any failure just leaves the output incomplete, and callers fall
/// back to the "no data found" first-run message. If NO data and NO *.adf are found,
/// it scaffolds the two empty folders + a HOW_TO text file so the user knows what to do.
/// </summary>
public static class AmigaImporter
{
    /// <summary>Absolute path of the INPUT folder (set once EnsureImported has run).</summary>
    public static string InputFolder { get; private set; } = "";

    /// <summary>Absolute path of the OUTPUT folder (set once EnsureImported has run).</summary>
    public static string OutputFolder { get; private set; } = "";

    // ── User-facing diagnostics ──────────────────────────────────────────────
    //
    // When import fails the user sees "no SWOS data" and has NO idea why: wrong
    // folder? unreadable? files present but not matched? On Android and the R36S
    // handheld there is no console to check either. So we record exactly what was
    // searched and what was found, and the first-run screen prints it — the user can
    // photograph that screen and send it, and the cause is immediately obvious.
    private static readonly List<string> ScanLog = new();

    /// <summary>
    /// Human-readable report of the last import attempt: every folder searched, how many
    /// .adf were found in each, and whether a folder was missing or unreadable. Shown
    /// on the first-run screen so a user can report a precise cause instead of "it
    /// doesn't work". Empty before <see cref="EnsureImported"/> has run.
    /// </summary>
    public static string DiagnosticReport => ScanLog.Count == 0
        ? ""
        : string.Join("\n", ScanLog);

    private static void Diag(string line)
    {
        ScanLog.Add(line);
        GD.Print("[AmigaImporter] " + line);
    }

    // Version / usage text shown to the user (HOW_TO file + README + on-screen).
    private const string RequiredVersionText =
        "Sensible World of Soccer 96/97 - Amiga edition.\r\n" +
        "BOTH floppy disk images (.adf) are required. Any filenames are fine, and\r\n" +
        "capitalisation does not matter; OpenSWOS scans every .adf and takes what it\r\n" +
        "needs. Disk 2 holds the graphics and teams, DISK 1 HOLDS SFX.IN2 - without\r\n" +
        "disk 1 the game runs but has NO Amiga sound at all.\r\n" +
        "Earlier SWOS editions (94/95 etc.) are NOT compatible.\r\n" +
        "WHDLoad/hard-disk installs: if your game files are loose in a folder, copy\r\n" +
        "them into original_swos_files/ instead. (.hdf/.lha hard-file images are not\r\n" +
        "auto-read yet.)";

    /// <summary>
    /// Ensures the Amiga graphics are present. If <see cref="DataPaths"/> already resolves
    /// them (extracted output, loose files, or the dev tree), returns true immediately.
    /// Otherwise scans the input folders for *.adf and extracts them. If nothing is found
    /// at all, scaffolds the folders + a HOW_TO file. Returns whether graphics are available.
    /// </summary>
    public static bool EnsureImported()
    {
        InputFolder = DataPaths.PreferredInputPath();
        OutputFolder = DataPaths.PreferredOutputPath();

        // Import is complete only when BOTH the graphics and the sound files are
        // present. Gating on graphics alone meant a partial import could never be
        // finished: extract disk 2 (graphics + SFX.SNG/IN1), and the game would from
        // then on short-circuit here forever — so adding the missing disk 1 later
        // (which carries SFX.IN2, and therefore ALL Amiga sound) did nothing at all,
        // with no hint as to why. Re-scanning is cheap and idempotent.
        bool haveGfx = DataPaths.HasAmigaGraphics();
        bool haveSfx = OpenSwos.Audio.AmigaSfxBank.Available();
        if (haveGfx && haveSfx)
            return true;

        GD.Print("[AmigaImporter] Import incomplete — scanning for *.adf to import…");

        // Case-INSENSITIVE match: on Android/Linux a plain "*.adf" pattern is
        // case-sensitive, so DISK1.ADF / Swos.Adf were silently never found even
        // though the docs promise "any filenames are fine".
        var scanOpts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            IgnoreInaccessible = true,
        };

        ScanLog.Clear();
        if (DataPaths.IsAndroid() && !DataPaths.HasStorageAccess())
            Diag("STORAGE ACCESS DENIED - allow it, the game retries by itself.");
        if (haveGfx && !haveSfx)
            Diag("graphics OK but Amiga SOUND incomplete - SFX.IN2 is on DISK 1, "
                 + "so BOTH .adf are needed for sound.");

        var adfs = new List<string>();
        foreach (string dir in DataPaths.InputSearchRoots())
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    // NOTE: a permission-denied directory also reports "does not
                    // exist", so say both — this is the most confusing failure of all.
                    Diag($"- missing or unreadable: {dir}");
                    continue;
                }
                int before = adfs.Count;
                foreach (string f in Directory.GetFiles(dir, "*.adf", scanOpts))
                    adfs.Add(f);
                Diag($"- {adfs.Count - before} .adf in {dir}");
            }
            catch (System.Exception ex)
            {
                Diag($"- ERROR reading {dir}: {ex.Message}");
            }
        }

        if (adfs.Count == 0)
            Diag("=> found NO .adf anywhere (any capitalisation is accepted).");
        else
            Diag($"=> found {adfs.Count} .adf total, extracting...");

        string outRoot = adfs.Count > 0 ? DataPaths.FirstWritableRoot(DataPaths.OutputFolderName) : "";
        if (adfs.Count > 0 && outRoot.Length == 0)
            GD.PrintErr("[AmigaImporter] No writable output folder — cannot extract.");
        else if (outRoot.Length > 0)
            OutputFolder = outRoot;

        foreach (string adf in adfs)
        {
            if (outRoot.Length == 0) break;
            try { ExtractAdf(adf, outRoot); }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[AmigaImporter] Failed to extract '{adf}': {ex.Message}");
            }
        }

        // Extraction just wrote new files; the sound bank's availability probe is cached
        // for the process lifetime, so drop it or the freshly-extracted SFX would still
        // report as missing (and the SOUND option would stay stuck on an unusable pick).
        if (adfs.Count > 0) OpenSwos.Audio.AmigaSfxBank.InvalidateProbe();

        bool ok = DataPaths.HasAmigaGraphics();
        if (ok)
        {
            GD.Print($"[AmigaImporter] Import complete — graphics available at {DataPaths.AmigaGrafsDir()}");
        }
        else
        {
            GD.Print("[AmigaImporter] No SWOS data yet.");
            // Only scaffold when there was genuinely nothing to work with.
            if (adfs.Count == 0) ScaffoldFirstRun();
        }
        return ok;
    }

    // Extract every file of a single ADF into <outRoot>/amiga/<diskTag>/<entry.FullPath>.
    // AdfDisk.Open throws on anything that isn't a valid 880K DD floppy — caught upstream.
    private static void ExtractAdf(string adfPath, string outRoot)
    {
        var disk = AdfDisk.Open(adfPath);

        // Materialise the walk once — we need it both to pick a disk tag and to extract.
        var files = new List<AdfEntry>();
        bool hasGrafs = false;
        foreach (var e in disk.Walk())
        {
            if (!e.IsFile) continue;
            files.Add(e);
            if (e.FullPath.Replace('\\', '/').StartsWith("grafs/", System.StringComparison.OrdinalIgnoreCase))
                hasGrafs = true;
        }

        // The disk carrying grafs/ is disk2 (the runtime graphics disk); the other is disk1.
        string diskTag = hasGrafs ? "disk2" : "disk1";
        string diskRoot = Path.Combine(outRoot, "amiga", diskTag);

        GD.Print($"[AmigaImporter] {Path.GetFileName(adfPath)} → {diskTag} ({files.Count} files, vol '{disk.VolumeName}')");

        foreach (var e in files)
        {
            string rel = e.FullPath.Replace('/', Path.DirectorySeparatorChar);
            string dest = Path.Combine(diskRoot, rel);
            try
            {
                string? destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                File.WriteAllBytes(dest, disk.ReadFile(e));
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[AmigaImporter]   could not write '{e.FullPath}': {ex.Message}");
            }
        }
    }

    // Create the empty input/output folders + a HOW_TO text file so a brand-new user
    // knows exactly where to drop their game files. Never throws.
    private static void ScaffoldFirstRun()
    {
        try
        {
            string inRoot = DataPaths.FirstWritableRoot(DataPaths.InputFolderName);
            string outRoot = DataPaths.FirstWritableRoot(DataPaths.OutputFolderName);
            // OPTIONAL PC (DOS) DATA folder — created empty so the user can discover it.
            string pcRoot = DataPaths.FirstWritableRoot(DataPaths.PcInputFolderName);

            // These strings are shown to the user as "drop your files HERE". Only adopt
            // the writable root when it is somewhere the user can actually reach. On
            // Android FirstWritableRoot can fall back to user:// (app-private, invisible
            // to every file manager) — displaying that told users to put files in a
            // place they cannot open, on the exact screen explaining what to do.
            bool inReachable = inRoot.Length > 0
                && (!DataPaths.IsAndroid() || inRoot == DataPaths.PreferredInputPath());
            bool outReachable = outRoot.Length > 0
                && (!DataPaths.IsAndroid() || outRoot == DataPaths.PreferredOutputPath());
            if (inReachable) InputFolder = inRoot;
            if (outReachable) OutputFolder = outRoot;

            if (inRoot.Length > 0)
            {
                string txt = Path.Combine(inRoot, "HOW_TO_ADD_SWOS_FILES.txt");
                File.WriteAllText(txt,
                    "HOW TO ADD YOUR SWOS GAME FILES\r\n" +
                    "===============================\r\n\r\n" +
                    RequiredVersionText + "\r\n\r\n" +
                    "OPTION A - floppy images (recommended)\r\n" +
                    "  Drop your two SWOS 96/97 Amiga floppy images (*.adf) into THIS folder\r\n" +
                    "  (" + inRoot + ")\r\n" +
                    "  then restart OpenSWOS. It extracts what it needs automatically into\r\n" +
                    "  the '" + DataPaths.OutputFolderName + "' folder.\r\n\r\n" +
                    "OPTION B - loose game files (WHDLoad / hard-disk install)\r\n" +
                    "  If you already have the game files unpacked in a folder, copy them into\r\n" +
                    "  the '" + DataPaths.OutputFolderName + "' folder (next to this one) instead,\r\n" +
                    "  then restart.\r\n\r\n" +
                    "OPTIONAL - PC (DOS) version\r\n" +
                    "  Only adds a slightly larger team list (~1730 vs ~1616 teams). It is NOT\r\n" +
                    "  required and does NOT change the graphics. If you want it, drop the PC\r\n" +
                    "  (DOS) SWOS 'DATA' folder into the '" + DataPaths.PcInputFolderName + "' folder\r\n" +
                    "  (next to this one), then restart.\r\n");
                GD.Print($"[AmigaImporter] Wrote first-run instructions → {txt}");
            }

            GD.Print($"[AmigaImporter] First-run folders ready: input='{InputFolder}', output='{OutputFolder}', pc='{pcRoot}'.");
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[AmigaImporter] Could not scaffold first-run folders: {ex.Message}");
        }
    }
}
