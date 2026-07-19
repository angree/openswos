using Godot;
using System.Collections.Generic;
using System.IO;

namespace OpenSwos.Audio;

/// <summary>
/// Reader for the AMIGA SWOS in-match sound bank (RJP1 / "Richard Joseph Player"
/// module format). Paraphrase of our RE of the original 68k player in
/// external/original-amiga-swos (asm line citations inline; PARAPHRASE only —
/// no code copied). Produces an <see cref="AmigaSampleSet"/> that
/// <see cref="MatchAudio"/> consumes in AMIGA sound mode.
///
/// Files (all 8-bit SIGNED PCM; none RNC-compressed):
///   • sound/SFX.SNG — song/instrument metadata, magic "RJP1SMOD" (big-endian).
///   • sound/SFX.IN1 — first sample bank, magic "RJP1".
///   • sound/SFX.IN2 — raw continuation of the sample bank (no header).
/// The sample bank is bytes(IN1) ++ bytes(IN2) concatenated IN1-first; verified
/// against the original's RAM layout ($8CA5A-$67662 = 71500+81068 exactly,
/// asm 17435). insBase = 4 skips the "RJP1" magic of IN1.
///
/// SFX.SNG layout (all big-endian): 8-byte magic, then 7 sections each a u32
/// length + payload. Section 0 is the instrument table: 72 entries × 32 bytes
/// (asm 18234-18255, 19112, 19718-19827, 18594). Per-entry fields we use:
///   +0x00 u32 sampleOffset (relative to insBase)
///   +0x0E u16 defaultVolume (0..64)
///   +0x10 u16 oneShotStart  (in WORDS)
///   +0x12 u16 oneShotLength (in WORDS; 1 = dead slot → skipped)
///   +0x14 u16 loopStart     (in WORDS)
///   +0x16 u16 loopLength    (in WORDS; 1 = no loop)
/// Sample slice: fileOffset = insBase + sampleOffset + 2*oneShotStart,
/// byteLen = 2*oneShotLength; 8-bit signed mono, used AS-IS (Godot FORMAT_8_BITS
/// is signed — no XOR, unlike the PC RawSample path).
///
/// Pure host-side asset helper: never touches sim state, safe off the main
/// thread, degrades to null when the files are absent.
/// </summary>
public static class AmigaSfxBank
{
    private const int InsBase = 4;              // skip "RJP1" magic of the sample bank
    private const int InstrumentCount = 72;     // asm 18234
    private const int InstrumentStride = 32;
    private const int SectionZeroPayload = 8 + 4;  // magic + section-0 u32 length

    // PAL Paula reference: rateHz = 3546895 / period. All the rates below were
    // pre-computed from the original's note/period tables (asm 19832) for the
    // specific pattern notes each SFX is triggered with; hardcoding them keeps us
    // from parsing the full pattern stream. Citations are the pattern-note bytes.
    private const int RateKickA = 15694;   // instr 34, kick variant A (asm 17759)
    private const int RateKickB = 29557;   // instr 34, kick variant B (same sample)
    private const int RatePass = 24803;    // instr 32
    private const int RateBounce = 9309;   // instr 51
    private const int RatePost = 10462;    // instr 8 (post/crossbar hit)
    private const int RateKeeper = 20864;  // instr 35 (keeper catch)
    private const int RateWhistle = 16574; // instr 40
    private const int RateCrowd = 10462;   // instr 36 (near-miss "oooh")
    private const int RateCrowdDelayed = 12445; // instr 36, delayed reaction (asm 17963)
    // The bed rate IS in the pattern data after all — song 1 (patterns 1+2) plays instr
    // 1 on two detuned channels, notes $46/$18 -> periods 214/226 -> 16574/15694 Hz. The
    // old 8000 guess pitched the bed more than an octave down, and instr 1 is already the
    // quietest sample in the bank (peak 28/128), so the crowd was an inaudible rumble —
    // reported as "the Amiga version has no crowd at all".
    // We play ONE bed layer. The original runs a second, detuned copy at 15694 Hz on
    // another channel, which thickens the crowd; reproducing that needs a second mixer
    // voice wired through the same fade/boost envelope, so it is left for later rather
    // than bolted onto the path we are currently repairing.
    private const int RateBed = 16574;     // instr 1, song 1 channel A
    private const int RateChant = 16000;   // instrs 60/61/63/67 (~15.7-16.6 kHz)

    // Crowd ROAR (song 6): the real goal / penalty / half-end / full-time cheer. Two
    // looping layers. Instr 36 is NOT this — it appears only in the near-miss and
    // delayed-reaction patterns, i.e. it is exclusively the disappointed "ooooh".
    private const int RateCheer = 16574;   // instr 28, song 6 pattern 20
    private const int RateCheer2 = 27928;  // instr 27, song 6 pattern 21

    // Instrument indices used in-match (asm 17759, 17963, 17988; pattern tables).
    private const int InstrKick = 34, InstrPass = 32, InstrBounce = 51, InstrPost = 8,
                      InstrKeeper = 35, InstrWhistle = 40, InstrCrowd = 36, InstrBed = 1;
    private const int InstrCheer = 28, InstrCheer2 = 27;
    private static readonly int[] ChantInstruments = { 60, 61, 63, 67 };

    // ── discovery ─────────────────────────────────────────────────────────────

    private static bool s_probed;
    private static bool s_available;
    private static string s_sng = "", s_in1 = "", s_in2 = "";

    // Names never descended into during the recursive file search.
    private static readonly HashSet<string> SkipDirs =
        new(System.StringComparer.OrdinalIgnoreCase)
        { ".git", ".godot", ".import", "bin", "obj", "node_modules", ".vs", ".idea", ".backups" };

    /// <summary>
    /// True when all three Amiga sound files (SFX.SNG + SFX.IN1 + SFX.IN2) are
    /// discoverable. Cheap after the first probe (result is cached). SFX.IN1 lives
    /// on disk2 and SFX.IN2 on disk1, so each file is located independently.
    /// </summary>
    public static bool Available()
    {
        Probe();
        return s_available;
    }

    /// <summary>
    /// Drop the cached probe so the next <see cref="Available"/> re-scans the disk.
    /// Must be called after an import writes new files: the probe result is cached for
    /// the process lifetime, so a bank that was incomplete at boot would stay reported
    /// as missing even once the extraction that fixes it has just run.
    /// </summary>
    public static void InvalidateProbe()
    {
        s_probed = false;
        s_available = false;
        s_sng = ""; s_in1 = ""; s_in2 = "";
    }

    private static void Probe()
    {
        if (s_probed) return;
        s_probed = true;

        // filename(UPPER) → absolute path, first hit wins.
        var index = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (string root in DiscoveryRoots())
            IndexRoot(root, index);

        s_sng = index.TryGetValue("SFX.SNG", out string? p1) ? p1 : "";
        s_in1 = index.TryGetValue("SFX.IN1", out string? p2) ? p2 : "";
        s_in2 = index.TryGetValue("SFX.IN2", out string? p3) ? p3 : "";
        s_available = s_sng.Length > 0 && s_in1.Length > 0 && s_in2.Length > 0;
    }

    // Amiga game-file roots to search for sound/SFX.*. Reuses the same "bring your
    // own files" roots the CJCTEAM*.RAW graphics come from (#205/#206), plus the
    // in-repo dev tree (covers both disk1/ and disk2/ under one root).
    private static IEnumerable<string> DiscoveryRoots()
    {
        foreach (string r in OpenSwos.Assets.DataPaths.OutputSearchRoots())
            yield return r;
        yield return ProjectSettings.GlobalizePath("res://../assets/extracted/amiga");
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

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { files = System.Array.Empty<string>(); }
            foreach (string f in files)
            {
                string key = Path.GetFileName(f).ToUpperInvariant();
                if ((key == "SFX.SNG" || key == "SFX.IN1" || key == "SFX.IN2")
                    && !index.ContainsKey(key))
                    index[key] = f;
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

    // ── parse + build ─────────────────────────────────────────────────────────

    private readonly struct Instrument
    {
        public readonly int SampleOffset, OneShotStart, OneShotLength, LoopStart, LoopLength, Volume;
        public bool Dead => OneShotLength <= 1;
        public Instrument(int off, int vol, int oss, int osl, int ls, int ll)
        { SampleOffset = off; Volume = vol; OneShotStart = oss; OneShotLength = osl;
          LoopStart = ls; LoopLength = ll; }
    }

    /// <summary>
    /// Discover, read and parse the Amiga sound bank into an
    /// <see cref="AmigaSampleSet"/>, or null if the files are missing / malformed.
    /// Also logs a per-instrument summary + the known-size sanity checks.
    /// </summary>
    public static AmigaSampleSet? Load()
    {
        Probe();
        if (!s_available) return null;

        byte[] sng, in1, in2;
        try
        {
            sng = File.ReadAllBytes(s_sng);
            in1 = File.ReadAllBytes(s_in1);
            in2 = File.ReadAllBytes(s_in2);
        }
        catch { return null; }

        if (sng.Length < SectionZeroPayload + InstrumentCount * InstrumentStride) return null;

        // bank = IN1 ++ IN2 (asm 17435). insBase=4 skips "RJP1".
        var bank = new byte[in1.Length + in2.Length];
        System.Array.Copy(in1, 0, bank, 0, in1.Length);
        System.Array.Copy(in2, 0, bank, in1.Length, in2.Length);

        var instr = new Instrument[InstrumentCount];
        int liveCount = 0;
        for (int i = 0; i < InstrumentCount; i++)
        {
            int e = SectionZeroPayload + i * InstrumentStride;
            instr[i] = new Instrument(
                Be32(sng, e + 0x00), Be16(sng, e + 0x0E),
                Be16(sng, e + 0x10), Be16(sng, e + 0x12),
                Be16(sng, e + 0x14), Be16(sng, e + 0x16));
            if (!instr[i].Dead) liveCount++;
        }

        var set = new AmigaSampleSet { InstrumentCount = liveCount };

        int mapped = 0;
        AudioStreamWav? Build(int idx, int rate, bool loop, int repeats = 1, float fadeTail = 0f)
        {
            var w = BuildWav(bank, instr, idx, rate, loop, repeats, fadeTail);
            if (w != null) mapped++;
            return w;
        }

        set.KickA = Build(InstrKick, RateKickA, false);
        set.KickB = Build(InstrKick, RateKickB, false);
        set.Pass = Build(InstrPass, RatePass, false);
        set.Bounce = Build(InstrBounce, RateBounce, false);
        set.Post = Build(InstrPost, RatePost, false);
        set.KeeperCatch = Build(InstrKeeper, RateKeeper, false);
        set.Whistle = Build(InstrWhistle, RateWhistle, false);
        // Crowd reactions: instr 36 ends hot, and the original fades it out via ramp 10
        // (sustain 0) plus an explicit RELease. Bake that decay so it stops sounding
        // chopped off.
        set.CrowdOooh = Build(InstrCrowd, RateCrowd, false, 1, 0.35f);
        set.CrowdOohDelayed = Build(InstrCrowd, RateCrowdDelayed, false, 1, 0.35f);
        set.CrowdBed = Build(InstrBed, RateBed, true);

        // The real goal/penalty/full-time roar (song 6). Each slice is only ~0.38 s and
        // the original loops it under an envelope for roughly three seconds, so bake
        // repetitions plus a decay tail rather than looping forever.
        set.Cheer = Build(InstrCheer, RateCheer, false, 7, 0.35f);
        set.Cheer2 = Build(InstrCheer2, RateCheer2, false, 7, 0.35f);

        set.Chants = new AudioStreamWav?[ChantInstruments.Length];
        for (int c = 0; c < ChantInstruments.Length; c++)
            set.Chants[c] = Build(ChantInstruments[c], RateChant, true);

        set.MappedSamples = mapped;

        LogSummary(instr, set);
        return set;
    }

    // Slice one instrument's one-shot region out of the bank and wrap it as an
    // AudioStreamWav. `loop` loops the whole buffer (crowd bed + chants).
    //
    // `repeats` bakes N copies of the slice back-to-back, and `fadeTail` fades that
    // final buffer linearly to silence over the last fraction of its length.
    // Together they stand in for the module's volume ramp, which this port does not
    // interpret: SFX.SNG carries an ADSR-ish ramp per instrument (the +0x0C field) and
    // the patterns issue an explicit RELease. Ignoring it meant every crowd reaction
    // played at flat volume and then stopped dead ON A HOT SAMPLE — instr 36's last
    // 100 bytes average |amp| 24/128 — which is audible as a hard chop at the end of
    // every shot reaction. Baking the decay into the PCM fixes it without a streaming
    // envelope. Likewise the original HOLDS looping crowd instruments for seconds;
    // `repeats` reproduces that duration for a one-shot playback.
    private static AudioStreamWav? BuildWav(byte[] bank, Instrument[] instr, int idx, int rate,
                                            bool loop, int repeats = 1, float fadeTail = 0f)
    {
        if (idx < 0 || idx >= instr.Length) return null;
        var it = instr[idx];
        if (it.Dead) return null;

        int start = InsBase + it.SampleOffset + 2 * it.OneShotStart;
        int len = 2 * it.OneShotLength;
        if (start < 0 || len <= 0 || start + len > bank.Length) return null;

        if (repeats < 1) repeats = 1;
        var data = new byte[len * repeats];
        for (int r = 0; r < repeats; r++)
            System.Array.Copy(bank, start, data, r * len, len);   // 8-bit signed, AS-IS

        if (fadeTail > 0f)
        {
            // Samples are 8-bit SIGNED; scale toward 0, not toward 0x80.
            int fadeLen = (int)(data.Length * Mathf.Clamp(fadeTail, 0f, 1f));
            int fadeStart = data.Length - fadeLen;
            for (int i = fadeStart; i < data.Length; i++)
            {
                float g = 1f - (i - fadeStart) / (float)Mathf.Max(1, fadeLen);
                data[i] = (byte)(sbyte)Mathf.RoundToInt((sbyte)data[i] * g);
            }
        }

        var wav = new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format8Bits,
            MixRate = rate,
            Stereo = false,
            Data = data,
        };
        if (loop)
        {
            wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
            wav.LoopBegin = 0;
            wav.LoopEnd = data.Length - 1;   // 8-bit mono → one frame per byte
        }
        return wav;
    }

    // Verification / debug: per-mapped-instrument line + known-size sanity check.
    private static void LogSummary(Instrument[] instr, AmigaSampleSet set)
    {
        void Line(string name, int idx, int rate)
        {
            if (idx < 0 || idx >= instr.Length) return;
            var it = instr[idx];
            GD.Print($"[audio]   {name,-12} instr {idx,2}  off={it.SampleOffset,-7} " +
                     $"byteLen={2 * it.OneShotLength,-6} loop={(it.LoopLength > 1 ? "yes" : "no")} " +
                     $"rate={rate}");
        }
        Line("kickA", InstrKick, RateKickA);
        Line("kickB", InstrKick, RateKickB);
        Line("pass", InstrPass, RatePass);
        Line("bounce", InstrBounce, RateBounce);
        Line("post", InstrPost, RatePost);
        Line("keeperCatch", InstrKeeper, RateKeeper);
        Line("whistle", InstrWhistle, RateWhistle);
        Line("crowdOooh", InstrCrowd, RateCrowd);
        Line("crowdBed", InstrBed, RateBed);
        for (int c = 0; c < ChantInstruments.Length; c++)
            Line($"chant{c}", ChantInstruments[c], RateChant);

        // Known-size assertions from our RE (byteLen = 2*oneShotLength).
        void Check(string name, int idx, int expect)
        {
            int got = (idx >= 0 && idx < instr.Length) ? 2 * instr[idx].OneShotLength : -1;
            GD.Print($"[audio]   SANITY {name}: byteLen={got} expected={expect} " +
                     (got == expect ? "OK" : "MISMATCH"));
        }
        Check("kick(34)", InstrKick, 2362);
        Check("pass(32)", InstrPass, 2420);
        Check("whistle(40)", InstrWhistle, 1262);
        Check("chant0(60)", 60, 35544);
        Check("chant1(61)", 61, 23280);
        Check("chant2(63)", 63, 19242);
        Check("chant3(67)", 67, 26282);
    }

    private static int Be16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
    private static int Be32(byte[] b, int o) =>
        (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
}

/// <summary>
/// The parsed Amiga in-match sample set: one <see cref="AudioStreamWav"/> per
/// trigger, at its original playback rate. Null slots = that instrument was
/// missing / dead (the corresponding trigger stays silent). Consumed by
/// <see cref="MatchAudio"/> in AMIGA sound mode.
/// </summary>
public sealed class AmigaSampleSet
{
    public AudioStreamWav? KickA, KickB;   // kick alternates A/B per hit (asm 17759)
    public AudioStreamWav? Pass, Bounce, Post, KeeperCatch, Whistle;
    public AudioStreamWav? CrowdOooh;      // near-miss reaction (instr 36 @ 10462)
    public AudioStreamWav? CrowdOohDelayed; // delayed reaction (instr 36 @ 12445, +10 ticks)
    public AudioStreamWav? CrowdBed;       // looped ambient bed (instr 1 @ 16574)
    public AudioStreamWav? Cheer;          // GOAL roar, song 6 (instr 28 @ 16574)
    public AudioStreamWav? Cheer2;         // roar upper layer, song 6 (instr 27 @ 27928)
    public AudioStreamWav?[] Chants = System.Array.Empty<AudioStreamWav?>();  // 4 looped chants

    public int InstrumentCount;   // live (non-dead) instruments parsed
    public int MappedSamples;     // trigger wavs actually built
}
