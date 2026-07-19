using Godot;
using OpenSwos.SwosVm;

namespace OpenSwos.Audio;

/// <summary>
/// In-match audio hub — a scene Node owned by Main.cs. Paraphrase of swos-port's
/// src/audio/{sfx,chants,comments}.cpp channel model (docs/SWOS/sound.txt:108-171):
///   • a pool of one-shot SFX players (never interrupt each other),
///   • a dedicated looping crowd bed (BGCRD3L),
///   • a dedicated looping chant channel (fade in/out, driven by ChantEngine),
///   • one logical commentary channel (big comments interrupt, small yield),
///   • a dedicated referee-whistle channel.
///
/// DETERMINISM: every public trigger is static and no-op safe when
/// <see cref="Instance"/> is null (headless / harness / before a match). Audio
/// may READ sim memory (score, kit colour) but NEVER writes it, and uses its own
/// <see cref="System.Random"/> — never the lockstep sim RNG — so netplay stays
/// pure. Volumes map the original 0..127 AIL scale to dB via LinearToDb.
/// </summary>
public partial class MatchAudio : Node
{
    public static MatchAudio? Instance { get; private set; }

    /// <summary>Which original's in-match audio to play.</summary>
    public enum SoundSource { Pc, Amiga }

    /// <summary>Selected source for the NEXT match — set by Main from the SOUND
    /// OPTIONS row (resolved against availability) just before OnMatchInit.
    /// Captured into <see cref="_source"/> once per match so it can't change
    /// mid-match.</summary>
    public static SoundSource Source = SoundSource.Pc;

    /// <summary>OPTIONS "COMMENTATOR" toggle. When false the spoken-commentary
    /// engine is silenced even on PC sound (Amiga never has commentary anyway).
    /// Applied to CommentaryEngine at match start via LoadPcSamples().</summary>
    public static bool CommentatorEnabled = true;

    /// <summary>True when the core PC pitch SFX are discoverable (SOUND OPTIONS).</summary>
    public static bool PcAvailable() => RawSample.PcAvailable();

    /// <summary>True when the Amiga RJP1 sound bank is discoverable (SOUND OPTIONS).</summary>
    public static bool AmigaAvailable() => AmigaSfxBank.Available();

    // Master scalar (no OPTIONS entry yet — separate task).
    private const float Master = 1.0f;

    // Original per-sample volumes on the 0..127 scale (sound.txt:287-299 / sfx.cpp).
    private const int VolKick = 25;      // sfx.cpp:129-132
    private const int VolBounce = 42;    // sfx.cpp:134-137
    private const int VolWhistle = 42;   // sfx.cpp:119-127
    private const int VolGoal = 127;     // sfx.cpp:103-117
    private const int VolMiss = 127;     // sfx.cpp:103-106
    private const int VolCrowdBed = 100; // sfx.cpp:75-79
    private const int VolEndCrowd = 127; // comments.cpp:500-523

    // Crowd-bed fade-in duration (deviation from the original, user-approved — the
    // original masked the silent start behind a loading screen; see task notes).
    private const float CrowdFadeSeconds = 1.5f;

    private const int PoolSize = 12;
    private AudioStreamPlayer[] _pool = System.Array.Empty<AudioStreamPlayer>();
    private int _poolCursor;
    private AudioStreamPlayer _crowdBed = null!;
    private AudioStreamPlayer _chant = null!;
    private AudioStreamPlayer _commentary = null!;
    private AudioStreamPlayer _whistle = null!;

    // One-shot SFX + crowd samples, loaded once (on the background preload thread).
    private AudioStreamWav? _kick, _bounce, _whistleWav, _foulWhistle, _endWhistle,
                            _goal, _missGoal, _crowdBedWav, _homeWinl, _booWhisl, _cheer;

    private System.Random _rng = new();
    private ChantEngine _chants = null!;
    private CommentaryEngine _comments = null!;
    private bool _initialized;

    // ── AMIGA sound source ────────────────────────────────────────────────────
    private SoundSource _source;         // captured from Source at OnMatchInit
    private AmigaSampleSet? _amigaSet;    // null in PC mode / when files absent

    // Crowd-reaction window (asm 17771-17779): while a crowd "oooh"/reaction is
    // sounding, kicks are suppressed. Counted down in TickInstance (sim ticks).
    private const int CrowdReactionWindow = 25;
    private int _crowdReactionTicks;
    // Delayed crowd reaction after post-hit / keeper-catch (asm 17963,17988):
    // instr 36 @ 12445 Hz played ~10 sim ticks later. -1 = inactive.
    private const int DelayedCrowdTicks = 10;
    private int _delayedCrowdTimer = -1;
    // Triple end-game whistle (pattern $16 = whistle + 2 variants): 3 blasts
    // spaced ~0.35 s, sequenced from _Process. (Approximation of the 3-instrument
    // layered blast with a single whistle sample.)
    private int _endWhistleBlasts;
    private float _endWhistleTimer;
    // Goal cheer bed boost (approximation of the layered "song 6" goal crowd —
    // see AmigaGoalCheer). Raises the crowd bed briefly after a goal.
    private const float BedBoostDuration = 1.2f;
    private float _bedBoostSeconds;

    // Background preload state. `_samplesReady` gates every trigger (one-shots
    // no-op until the samples are in) and is published with a release write after
    // all fields are assigned, so main-thread readers see fully-loaded state.
    private System.Threading.Tasks.Task? _loadTask;
    private volatile bool _samplesReady;
    private int _loadedCount, _missingCount;

    // Crowd-bed fade-in progress (main thread only, driven from _Process).
    private bool _crowdStarted;
    private float _crowdFadeElapsed;

    private bool SamplesReady => _samplesReady;

    public override void _Ready()
    {
        Instance = this;

        _pool = new AudioStreamPlayer[PoolSize];
        for (int i = 0; i < PoolSize; i++)
        {
            _pool[i] = new AudioStreamPlayer { Name = $"SfxPool{i}" };
            AddChild(_pool[i]);
        }
        _crowdBed = new AudioStreamPlayer { Name = "CrowdBed" };
        _chant = new AudioStreamPlayer { Name = "Chant" };
        _commentary = new AudioStreamPlayer { Name = "Commentary" };
        _whistle = new AudioStreamPlayer { Name = "Whistle" };
        AddChild(_crowdBed);
        AddChild(_chant);
        AddChild(_commentary);
        AddChild(_whistle);

        _chants = new ChantEngine(this, _rng);
        _comments = new CommentaryEngine(this, _rng);
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    // ── volume mapping ────────────────────────────────────────────────────────
    private static float Vol127ToDb(int v)
    {
        float lin = Mathf.Clamp(v, 0, 127) / 127f * Master;
        return lin <= 0.0001f ? -80f : Mathf.LinearToDb(lin);
    }

    // ── per-match lifecycle (instance) ────────────────────────────────────────

    /// <summary>
    /// Called once from Main as the match scene is set up (sim memory already
    /// seeded, so the intro chant sees the home kit colour). Kicks the ~107-sample
    /// load onto a background thread so the match scene / walk-in never blocks on
    /// disk IO. The crowd bed is started + faded in from <see cref="_Process"/> the
    /// moment the load completes. Idempotent across the half-time re-seed.
    /// </summary>
    public void OnMatchInit()
    {
        if (_initialized) return;
        _initialized = true;

        _source = Source;   // lock the source for the whole match

        // Timer/channel reset touches nodes → main thread, before the load starts.
        _comments.Reset();
        _chants.Reset();

        _loadTask = System.Threading.Tasks.Task.Run(LoadAllSamples);
    }

    // Background-thread body: pure resource loading, NO scene-node access. Every
    // AudioStreamWav is a data Resource (safe to build off the main thread); the
    // AudioStreamPlayer nodes are only ever touched on the main thread. Publishes
    // `_samplesReady` last so readers observe fully-assigned fields.
    private void LoadAllSamples()
    {
        if (_source == SoundSource.Amiga) { LoadAmigaSamples(); return; }
        LoadPcSamples();
    }

    // AMIGA: parse the RJP1 bank, wire the crowd bed + chant channel, and disable
    // the (PC-only) commentary engine. See game/scripts/Audio/AmigaSfxBank.cs.
    private void LoadAmigaSamples()
    {
        _comments.SetEnabled(false);   // Amiga has no spoken commentary
        _amigaSet = AmigaSfxBank.Load();

        int loaded = _amigaSet?.MappedSamples ?? 0;
        _crowdBedWav = _amigaSet?.CrowdBed;

        var (cl, cm) = _chants.LoadAmiga(_amigaSet?.Chants);
        loaded += cl;

        _loadedCount = loaded;
        _missingCount = cm;
        _samplesReady = true;

        int instr = _amigaSet?.InstrumentCount ?? 0;
        int mapped = _amigaSet?.MappedSamples ?? 0;
        GD.Print($"[audio] amiga bank: {instr} instruments, {mapped} samples mapped");
        if (_amigaSet == null)
            GD.Print("[audio] AMIGA sound files not found (sound/SFX.SNG+IN1+IN2) — " +
                     "in-match sounds disabled.");
    }

    private void LoadPcSamples()
    {
        _comments.SetEnabled(CommentatorEnabled);
        int loaded = 0, missing = 0;
        void Track(AudioStreamWav? s) { if (s != null) loaded++; else missing++; }

        _kick = RawSample.Load("KICKX"); Track(_kick);
        _bounce = RawSample.Load("BOUNCEX"); Track(_bounce);
        _whistleWav = RawSample.Load("WHISTLE"); Track(_whistleWav);
        _foulWhistle = RawSample.Load("FOUL"); Track(_foulWhistle);
        _endWhistle = RawSample.Load("ENDGAMEW"); Track(_endWhistle);
        _goal = RawSample.Load("HOMEGOAL"); Track(_goal);
        _missGoal = RawSample.Load("MISSGOAL"); Track(_missGoal);
        _crowdBedWav = RawSample.Load("BGCRD3L", loop: true); Track(_crowdBedWav);
        _homeWinl = RawSample.Load("HOMEWINL"); Track(_homeWinl);
        _booWhisl = RawSample.Load("BOOWHISL"); Track(_booWhisl);
        _cheer = RawSample.Load("CHEER"); Track(_cheer);

        var (cl, cm) = _chants.Load();
        loaded += cl; missing += cm;
        var (ml, mm) = _comments.Load();
        loaded += ml; missing += mm;

        _loadedCount = loaded;
        _missingCount = missing;
        _samplesReady = true;   // release: publishes every field written above

        GD.Print($"[audio] {loaded} samples loaded ({missing} missing)");
        if (_kick == null && _crowdBedWav == null && _whistleWav == null)
            GD.Print("[audio] PC sound files not found — kick/whistle/crowd sounds " +
                     "disabled. Put your SWOS CD image or SFX folder into " +
                     OpenSwos.Assets.DataPaths.PreferredPcInputPath() + ".");
    }

    /// <summary>
    /// Block until the background preload finishes. Used ONLY by synchronous
    /// harnesses (e.g. <c>--swos-smoke</c>) that run every sim tick in one call
    /// and quit before an idle <see cref="_Process"/> frame could ever fire.
    /// No-op in the normal interactive path.
    /// </summary>
    public static void EnsurePreloadComplete() => Instance?._loadTask?.Wait();

    // Main-thread per-frame: start + fade the crowd bed once the samples land.
    // Runs during the pre-kickoff walk-in ceremony (MatchAudio is in the scene
    // tree from match init), so the crowd is up well before the first sim tick.
    public override void _Process(double delta)
    {
        if (!SamplesReady) return;

        if (!_crowdStarted)
        {
            _crowdStarted = true;
            if (_crowdBedWav != null)
            {
                _crowdBed.Stream = _crowdBedWav;
                _crowdBed.VolumeDb = -80f;   // start silent, ramp up below
                _crowdBed.Play();
            }
        }

        // sfx.cpp:75-79 — crowd bed target volume is 100/127; ramp 0→target over
        // CrowdFadeSeconds instead of the original hard cut-in.
        if (_crowdBedWav != null && _crowdFadeElapsed < CrowdFadeSeconds)
        {
            _crowdFadeElapsed += (float)delta;
            float t = Mathf.Clamp(_crowdFadeElapsed / CrowdFadeSeconds, 0f, 1f);
            float lin = t * (VolCrowdBed / 127f) * Master;
            _crowdBed.VolumeDb = lin <= 0.0001f ? -80f : Mathf.LinearToDb(lin);
        }

        // AMIGA triple end-game whistle — 3 blasts spaced ~0.35 s (pattern $16).
        if (_endWhistleBlasts > 0)
        {
            _endWhistleTimer -= (float)delta;
            if (_endWhistleTimer <= 0f)
            {
                if (_amigaSet != null) PlayWhistleChannel(_amigaSet.Whistle);
                _endWhistleBlasts--;
                _endWhistleTimer = 0.35f;
            }
        }

        // AMIGA goal-cheer bed boost — briefly lift the crowd bed after a goal,
        // then settle back to the normal target (crowd-fade must have completed).
        if (_crowdBedWav != null && _bedBoostSeconds > 0f && _crowdFadeElapsed >= CrowdFadeSeconds)
        {
            _bedBoostSeconds -= (float)delta;
            if (_bedBoostSeconds > 0f)
            {
                float lin = Mathf.Min(1f, ((VolCrowdBed + 27) / 127f) * Master);
                _crowdBed.VolumeDb = Mathf.LinearToDb(lin);
            }
            else
            {
                float lin = (VolCrowdBed / 127f) * Master;
                _crowdBed.VolumeDb = lin <= 0.0001f ? -80f : Mathf.LinearToDb(lin);
            }
        }
    }

    // ── channel primitives (used by the engines) ──────────────────────────────
    private void PlayOneShot(AudioStreamWav? sample, int vol127)
    {
        if (!SamplesReady || sample == null) return;
        // First-free channel; never interrupt a busy one (sound.txt:153-171).
        AudioStreamPlayer? p = null;
        for (int i = 0; i < _pool.Length; i++)
            if (!_pool[i].Playing) { p = _pool[i]; break; }
        if (p == null) { p = _pool[_poolCursor]; _poolCursor = (_poolCursor + 1) % _pool.Length; }
        p.Stream = sample;
        p.VolumeDb = Vol127ToDb(vol127);
        p.Play();
    }

    private void PlayWhistleChannel(AudioStreamWav? sample)
    {
        if (!SamplesReady || sample == null) return;
        _whistle.Stream = sample;
        _whistle.VolumeDb = Vol127ToDb(VolWhistle);
        _whistle.Play();
    }

    // Chant channel — driven by ChantEngine.
    internal void PlayChant(AudioStreamWav? sample)
    {
        if (sample == null) { _chant.Stop(); return; }
        _chant.Stream = sample;
        _chant.VolumeDb = Vol127ToDb(0);   // fade-in ramps it up from silence
        _chant.Play();
    }

    internal void SetChantVolume127(int v) => _chant.VolumeDb = Vol127ToDb(v);
    internal void StopChant() => _chant.Stop();

    // Commentary channel — big interrupts, small yields (sound.txt:120-127).
    internal void PlayCommentary(AudioStreamWav? sample, bool interrupt)
    {
        if (!SamplesReady || sample == null) return;
        if (!interrupt && _commentary.Playing) return;   // small comment yields
        _commentary.Stream = sample;
        _commentary.VolumeDb = Vol127ToDb(127);           // commentary is always max
        _commentary.Play();
    }

    // ── source-branching one-shot triggers ────────────────────────────────────
    // Each maps a sim event to the PC sample or the Amiga RJP1 sample. Amiga-only
    // behaviours (kick A/B alternation, crowd-reaction suppression, dedicated
    // keeper/post samples + delayed crowd, triple whistle, goal cheer) live here.

    private void DoKick()
    {
        if (_source == SoundSource.Amiga)
        {
            if (_amigaSet == null) return;
            if (_crowdReactionTicks > 0) return;    // suppressed during a crowd reaction
            // Alternate kick variant A/B via the AUDIO rng (never the sim RNG).
            AudioStreamWav? s = _rng.Next(2) == 0 ? _amigaSet.KickA : _amigaSet.KickB;
            PlayOneShot(s, VolKick);
        }
        else PlayOneShot(_kick, VolKick);
    }

    private void DoBounce()
    {
        if (_source == SoundSource.Amiga) { if (_amigaSet != null) PlayOneShot(_amigaSet.Bounce, VolBounce); }
        else PlayOneShot(_bounce, VolBounce);
    }

    private void DoWhistle()
    {
        if (_source == SoundSource.Amiga) { if (_amigaSet != null) PlayWhistleChannel(_amigaSet.Whistle); }
        else PlayWhistleChannel(_whistleWav);
    }

    private void DoFoulWhistle()
    {
        if (_source == SoundSource.Amiga) { if (_amigaSet != null) PlayWhistleChannel(_amigaSet.Whistle); }
        else PlayWhistleChannel(_foulWhistle);
    }

    private void DoEndGameWhistle()
    {
        if (_source == SoundSource.Amiga)
        {
            if (_amigaSet != null) { _endWhistleBlasts = 3; _endWhistleTimer = 0f; }  // _Process sequences it
        }
        else PlayWhistleChannel(_endWhistle);
    }

    private void DoGoal()
    {
        if (_source == SoundSource.Amiga) AmigaGoalCheer();
        else PlayOneShot(_goal, VolGoal);
    }

    private void DoMissGoal()
    {
        if (_source == SoundSource.Amiga)
        {
            if (_amigaSet != null) { PlayOneShot(_amigaSet.CrowdOooh, VolMiss); _crowdReactionTicks = CrowdReactionWindow; }
        }
        else PlayOneShot(_missGoal, VolMiss);
    }

    // Goal cheer — the original's "song 6" roar: instr 28 @ 16574 layered with instr 27
    // @ 27928, both looped under an envelope for ~3 s.
    //
    // This USED to play CrowdOooh (instr 36) — but instr 36 appears only in the
    // near-miss and delayed-reaction patterns: it is exclusively the DISAPPOINTED
    // "ooooh". So every goal was announced with the sound of a crowd groaning at a
    // miss, and a goal and a miss were literally the same sample. Song 6 is the actual
    // cheer, and is what the original also fires on a penalty award, at half time and
    // at full time.
    private void AmigaGoalCheer()
    {
        if (_amigaSet == null) return;
        PlayOneShot(_amigaSet.Cheer, VolGoal);
        PlayOneShot(_amigaSet.Cheer2, VolGoal * 7 / 10);   // upper layer, quieter
        _bedBoostSeconds = BedBoostDuration;
        _crowdReactionTicks = CrowdReactionWindow;
    }

    // Post/crossbar hit — dedicated Amiga sample + 10-tick delayed crowd reaction.
    private void AmigaPostHit()
    {
        if (_amigaSet == null) return;
        PlayOneShot(_amigaSet.Post, VolBounce);
        _delayedCrowdTimer = DelayedCrowdTicks;
    }

    // Keeper catch/save — dedicated Amiga sample + 10-tick delayed crowd reaction.
    private void AmigaKeeperCatch()
    {
        if (_amigaSet == null) return;
        PlayOneShot(_amigaSet.KeeperCatch, VolKick);
        _delayedCrowdTimer = DelayedCrowdTicks;
    }

    // ── per-frame drain (called from the sim's PlayEnqueuedSamples) ───────────
    private void TickInstance()
    {
        if (!SamplesReady) return;
        _comments.Tick();   // no-op in Amiga mode (commentary disabled)
        _chants.Tick();

        if (_source == SoundSource.Amiga)
        {
            if (_crowdReactionTicks > 0) _crowdReactionTicks--;
            if (_delayedCrowdTimer >= 0 && --_delayedCrowdTimer < 0 && _amigaSet != null)
            {
                PlayOneShot(_amigaSet.CrowdOohDelayed, VolMiss);
                _crowdReactionTicks = CrowdReactionWindow;
            }
        }
    }

    // End-of-match crowd + result comment (comments.cpp:110-138).
    private void PlayEndGameCrowdInstance()
    {
        int g1 = Memory.ReadWord(Memory.Addr.team1TotalGoals);
        int g2 = Memory.ReadWord(Memory.Addr.team2TotalGoals);

        // Amiga has no separate win/lose/draw crowd samples — the original fires the
        // same song-6 roar at full time as it does at a goal. Without this branch the
        // Amiga path was SILENT at the final whistle: _cheer/_homeWinl/_booWhisl are
        // only ever loaded by LoadPcSamples.
        if (_source == SoundSource.Amiga)
        {
            if (_amigaSet != null)
            {
                PlayOneShot(_amigaSet.Cheer, VolEndCrowd);
                PlayOneShot(_amigaSet.Cheer2, VolEndCrowd * 7 / 10);
            }
            return;   // no spoken commentary on Amiga
        }

        // comments.cpp:115-127 — draw → cheer, team2 losing → homewinl (home
        // fans cheer), team1 losing → boowhisl (home fans boo).
        AudioStreamWav? crowd;
        if (g1 == g2) crowd = _cheer;
        else if (g1 > g2) crowd = _homeWinl;
        else crowd = _booWhisl;
        PlayOneShot(crowd, VolEndCrowd);

        _comments.PlayEndGameComment(g1, g2);
    }

    // ── static trigger API (no-op safe) ───────────────────────────────────────
    // SFX (source-branching: PC sample or Amiga RJP1 sample)
    public static void PlayKick() => Instance?.DoKick();
    public static void PlayBounce() => Instance?.DoBounce();
    public static void PlayWhistle() => Instance?.DoWhistle();
    public static void PlayFoulWhistle() => Instance?.DoFoulWhistle();
    public static void PlayEndGameWhistle() => Instance?.DoEndGameWhistle();
    public static void PlayGoal() => Instance?.DoGoal();
    public static void PlayMissGoal() => Instance?.DoMissGoal();

    // Commentary — big. In Amiga mode the commentary engine is disabled (no-op);
    // the events that DO make an Amiga crowd sound (post/bar hit, keeper catch)
    // branch to their dedicated RJP1 samples + delayed crowd reaction.
    public static void GoalComment() => Instance?._comments.PlayGoal();
    public static void OwnGoalComment() => Instance?._comments.PlayOwnGoal();
    public static void NearMissComment() => Instance?._comments.PlayNearMiss();
    public static void KeeperSavedComment()
    { var m = Instance; if (m == null) return; if (m._source == SoundSource.Amiga) m.AmigaKeeperCatch(); else m._comments.PlayKeeperSaved(); }
    public static void KeeperClaimedComment()
    { var m = Instance; if (m == null) return; if (m._source == SoundSource.Amiga) m.AmigaKeeperCatch(); else m._comments.PlayKeeperClaimed(); }
    public static void PostHitComment()
    { var m = Instance; if (m == null) return; if (m._source == SoundSource.Amiga) m.AmigaPostHit(); else m._comments.PlayPostHit(); }
    public static void BarHitComment()
    { var m = Instance; if (m == null) return; if (m._source == SoundSource.Amiga) m.AmigaPostHit(); else m._comments.PlayBarHit(); }
    public static void FoulComment() => Instance?._comments.PlayFoul();
    public static void DangerousPlayComment() => Instance?._comments.PlayDangerousPlay();
    public static void PenaltyComment() => Instance?._comments.PlayPenalty();
    public static void InjuryComment() => Instance?._comments.PlayInjury();
    // Commentary — small
    public static void GoodTackleComment() => Instance?._comments.PlayGoodTackle();
    public static void HeaderComment() => Instance?._comments.PlayHeader();
    // Good pass (10-tick delayed small comment)
    public static void EnqueueGoodPass() => Instance?._comments.EnqueueGoodPass();
    public static void CancelGoodPass() => Instance?._comments.CancelGoodPass();

    // Commentary — enqueued (on-demand)
    public static void EnqueueCorner() => Instance?._comments.EnqueueCorner();
    public static void EnqueueThrowIn() => Instance?._comments.EnqueueThrowIn();
    public static void EnqueueSubstitute() => Instance?._comments.EnqueueSubstitute();
    public static void EnqueueTactics() => Instance?._comments.EnqueueTactics();
    public static void EnqueueRedCard() => Instance?._comments.EnqueueRedCard();
    public static void EnqueueYellowCard() => Instance?._comments.EnqueueYellowCard();

    // Chants / crowd
    public static void LoadCrowdChant() => Instance?._chants.OnGoal();
    public static void PlayEndGameCrowd() => Instance?.PlayEndGameCrowdInstance();

    // Per-frame drain from the deterministic sim loop.
    public static void Tick() => Instance?.TickInstance();
}
