using Godot;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenSwos.Audio;

/// <summary>
/// Loader for the original SWOS PC audio samples (headerless RAW → Godot
/// <see cref="AudioStreamWav"/>) plus case-insensitive file discovery across the
/// scattered SFX\FX, SFX\PIERCE and HARD subtrees.
///
/// Format (docs/SWOS/sound.txt:13-15): headerless PCM, 8-bit **unsigned**, mono.
/// Sample rate is 22050 Hz for every file EXCEPT ENDGAMEW.RAW and FOUL.RAW which
/// are 11025 Hz (SoundSample.cpp:166-167, 229-233). Godot's FORMAT_8_BITS
/// expects **signed** 8-bit, so we convert on load by XOR 0x80.
///
/// This is a pure host-side asset helper — it never touches sim state and is
/// safe to call once at match start. Missing files degrade gracefully (return
/// null); the caller logs a single load summary.
/// </summary>
public static class RawSample
{
    // Files that ship at 11025 Hz instead of the usual 22050 Hz.
    private static readonly HashSet<string> Rate11025 =
        new(System.StringComparer.OrdinalIgnoreCase) { "ENDGAMEW.RAW", "FOUL.RAW" };

    // Directory names we never descend into during discovery.
    private static readonly HashSet<string> SkipDirs =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".godot", ".import", "bin", "obj", "node_modules", ".vs", ".idea", ".backups",
        };

    // filename (UPPER) → absolute OS path. Built once, lazily.
    private static Dictionary<string, string>? s_index;

    /// <summary>
    /// Build (once) the case-insensitive filename→path index by walking the PC
    /// asset roots in priority order. Later roots never overwrite earlier hits.
    /// </summary>
    private static Dictionary<string, string> Index()
    {
        if (s_index != null) return s_index;
        var index = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (string root in DiscoveryRoots())
            IndexRoot(root, index);

        s_index = index;
        return index;
    }

    // Candidate roots to search for *.RAW audio, in priority order:
    //   1. auto-imported CD SFX cache (user://cache/pc_sfx — the authoritative
    //      source of the CD-only core SFX + PIERCE commentary; extracted here)
    //   2. dev fallback: the in-repo extracted SFX tree (assets/extracted/pc)
    //   3. user import folders — a loose SFX/ tree the user copied straight off
    //      the CD (original_swos_pc), then the general extracted folder
    //   4. dev fallback: the read-only PC reference install (has HARD\, DATA\)
    private static IEnumerable<string> DiscoveryRoots()
    {
        // Trigger the one-time CD auto-import (no-op if already cached / no CD).
        // Runs on whatever thread first builds the index (the preload thread);
        // it is pure System.IO + GD.Print, so it is safe off the main thread.
        string cdCache = OpenSwos.Assets.CdSfxImport.EnsureExtracted();
        if (cdCache.Length > 0) yield return cdCache;

        yield return ProjectSettings.GlobalizePath("res://../assets/extracted/pc");
        foreach (string r in OpenSwos.Assets.DataPaths.PcSearchRoots())
            yield return r;
        foreach (string r in OpenSwos.Assets.DataPaths.OutputSearchRoots())
            yield return r;
        yield return ProjectSettings.GlobalizePath("res://../Swos9697_PC/SensiWs9/SOC");
    }

    private static void IndexRoot(string root, Dictionary<string, string> index, int maxDepth = 6)
    {
        if (string.IsNullOrEmpty(root)) return;
        try { if (!Directory.Exists(root)) return; } catch { return; }

        var queue = new Queue<(string dir, int depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();

            // Case-insensitive: "*.RAW" is an exact-case pattern on Linux/Android/R36S,
            // so a lowercased sample tree (kickx.raw …) indexed as EMPTY and PC sound
            // silently became unavailable — despite this class promising case-insensitive
            // discovery. Filter by extension instead of trusting the glob.
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*")
                    .Where(f => f.EndsWith(".RAW", System.StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
            catch { files = System.Array.Empty<string>(); }
            foreach (string f in files)
            {
                string key = Path.GetFileName(f).ToUpperInvariant();
                if (!index.ContainsKey(key)) index[key] = f;
            }

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
    }

    /// <summary>
    /// Resolve a bare sample id (case-insensitive) to an absolute path, or null.
    /// The extracted commentary set names files <c>&lt;id&gt;_.RAW</c> (e.g.
    /// <c>M158_1_.RAW</c>) while SFX use the plain <c>&lt;id&gt;.RAW</c>, so we try
    /// the plain form first, then the trailing-underscore variant.
    /// </summary>
    public static string? Resolve(string fileName)
    {
        string id = fileName.ToUpperInvariant();
        if (id.EndsWith(".RAW")) id = id.Substring(0, id.Length - 4);

        var index = Index();
        if (index.TryGetValue(id + ".RAW", out string? path)) return path;
        if (index.TryGetValue(id + "_.RAW", out path)) return path;
        return null;
    }

    /// <summary>
    /// True when the core PC pitch SFX are discoverable (used by the SOUND OPTIONS
    /// row to decide whether PC audio can be selected). Probes for KICKX.RAW — the
    /// same CD-only core sample <see cref="MatchAudio"/> loads first. Cached via the
    /// shared discovery index.
    /// </summary>
    public static bool PcAvailable() => Resolve("KICKX") != null;

    /// <summary>
    /// Load a RAW file into an <see cref="AudioStreamWav"/>. Returns null if the
    /// file is missing or unreadable. <paramref name="loop"/> configures forward
    /// looping (crowd bed + chants). The name (bare or path) decides the mix rate.
    /// </summary>
    public static AudioStreamWav? Load(string fileName, bool loop = false)
    {
        string? path = Resolve(fileName);
        if (path == null) return null;

        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch { return null; }
        if (data.Length == 0) return null;

        // sound.txt:15 — samples are 8-bit UNSIGNED; Godot FORMAT_8_BITS is
        // SIGNED, so flip the sign bit on every byte.
        for (int i = 0; i < data.Length; i++)
            data[i] ^= 0x80;

        int rate = Rate11025.Contains(Path.GetFileName(path)) ? 11025 : 22050;

        var wav = new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format8Bits,
            MixRate = rate,
            Stereo = false,
            Data = data,
        };
        if (loop)
        {
            // 8-bit mono → one frame per byte.
            wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
            wav.LoopBegin = 0;
            wav.LoopEnd = data.Length - 1;
        }
        return wav;
    }
}
