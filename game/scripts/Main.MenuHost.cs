using Godot;
using OpenSwos.Sim;

namespace OpenSwos;

// Bridge between the SWOS-styled front-end (game/scripts/Menu/) and the game.
// Kept in its own partial so Main.cs stays lean and the menu module never
// reaches into gameplay internals directly — it only sees this interface.
//
// Every Step* here mirrors the exact wrap / de-dupe / re-apply logic the old
// plain-text TickMenu used, so the live preview underneath the menu stays
// correct.
public partial class Main : OpenSwos.Menu.IMenuHost
{
    // SWOS team-kit palette (kTeamPalette, swos-port color.cpp:9-13) — used for
    // the squad-screen kit swatches. Copied constant (licence: constants OK).
    private static readonly Color[] kTeamPaletteSwatch =
    {
        new Color(60/255f,184/255f,0), new Color(152/255f,152/255f,152/255f), new Color(1,1,1), new Color(0,0,0),
        new Color(100/255f,32/255f,0), new Color(184/255f,68/255f,0), new Color(252/255f,100/255f,0), new Color(140/255f,184/255f,0),
        new Color(0,32/255f,0), new Color(60/255f,184/255f,0), new Color(1,0,0), new Color(0,0,1),
        new Color(100/255f,0,32/255f), new Color(152/255f,152/255f,152/255f), new Color(0,220/255f,0), new Color(1,1,0),
    };

    // ---- teams ---------------------------------------------------------------
    int OpenSwos.Menu.IMenuHost.TeamCount => _allTeams.Count;
    int OpenSwos.Menu.IMenuHost.HomeIndex => _homeTeamIndex;
    int OpenSwos.Menu.IMenuHost.AwayIndex => _awayTeamIndex;
    // Compact, ASCII-only team label for the value box (the SWOS charset has no
    // '·'/'&', and the box is narrow). Just the club name — skill detail lives
    // on the TEAM SETUP squad screen (the old "AVGn" suffix was noise).
    string OpenSwos.Menu.IMenuHost.TeamTag(int idx)
    {
        if (idx < 0 || idx >= _allTeams.Count) return "?";
        string name = (_allTeams[idx].Name ?? "").Trim();
        if (name.Length > 22) name = name.Substring(0, 22);
        return name;
    }

    string OpenSwos.Menu.IMenuHost.TeamName(int idx)
    {
        if (idx < 0 || idx >= _allTeams.Count) return "?";
        return (_allTeams[idx].Name ?? "").Trim();
    }

    void OpenSwos.Menu.IMenuHost.StepHome(int delta)
    {
        int n = _allTeams.Count; if (n == 0) return;
        _homeTeamIndex = (_homeTeamIndex + delta + n) % n;
        if (_homeTeamIndex == _awayTeamIndex)
            _homeTeamIndex = (_homeTeamIndex + System.Math.Sign(delta == 0 ? 1 : delta) + n) % n;
        ApplyMatchSetup();
    }

    void OpenSwos.Menu.IMenuHost.StepAway(int delta)
    {
        int n = _allTeams.Count; if (n == 0) return;
        _awayTeamIndex = (_awayTeamIndex + delta + n) % n;
        if (_awayTeamIndex == _homeTeamIndex)
            _awayTeamIndex = (_awayTeamIndex + System.Math.Sign(delta == 0 ? 1 : delta) + n) % n;
        ApplyMatchSetup();
    }

    void OpenSwos.Menu.IMenuHost.SwapTeams()
    {
        (_homeTeamIndex, _awayTeamIndex) = (_awayTeamIndex, _homeTeamIndex);
        ApplyMatchSetup();
    }

    // Direct team selection (nation-filtered pickers). De-dupes like the
    // steppers: colliding with the other side nudges that side by one.
    void OpenSwos.Menu.IMenuHost.SetHomeTeam(int masterIndex)
    {
        int n = _allTeams.Count; if (n == 0) return;
        _homeTeamIndex = System.Math.Clamp(masterIndex, 0, n - 1);
        if (_awayTeamIndex == _homeTeamIndex) _awayTeamIndex = (_awayTeamIndex + 1) % n;
        ApplyMatchSetup();
    }

    void OpenSwos.Menu.IMenuHost.SetAwayTeam(int masterIndex)
    {
        int n = _allTeams.Count; if (n == 0) return;
        _awayTeamIndex = System.Math.Clamp(masterIndex, 0, n - 1);
        if (_homeTeamIndex == _awayTeamIndex) _homeTeamIndex = (_homeTeamIndex + 1) % n;
        ApplyMatchSetup();
    }

    // ---- pitch ---------------------------------------------------------------
    // Cycle is: RANDOM (default) then each available pitch. RANDOM rolls a fresh
    // ground at every kick-off (RollRandomPitch, called from match init).
    string OpenSwos.Menu.IMenuHost.PitchLabel =>
        _availablePitches.Count == 0 ? OpenSwos.Menu.Loc.Tr("val.pitch_none", "(NONE)")
        : _pitchRandom ? OpenSwos.Menu.Loc.Tr("val.pitch_random", "RANDOM")
        : $"SWCPICH{PitchId}  ({_pitchSlot + 1}/{_availablePitches.Count})";

    void OpenSwos.Menu.IMenuHost.StepPitch(int delta)
    {
        if (_availablePitches.Count == 0) return;
        int d = System.Math.Sign(delta == 0 ? 1 : delta);
        // Positions 0..N-1 = fixed pitches, position N = RANDOM.
        int n = _availablePitches.Count;
        int cur = _pitchRandom ? n : _pitchSlot;
        int next = ((cur + d) % (n + 1) + (n + 1)) % (n + 1);
        if (next == n) { _pitchRandom = true; }
        else { _pitchRandom = false; _pitchSlot = next; LoadPitchVariant(PitchId); }
    }

    // ---- opponent ------------------------------------------------------------
    string OpenSwos.Menu.IMenuHost.OpponentLabel => _opponentMode switch
    {
        OpponentMode.Ai => OpenSwos.Menu.Loc.Tr("val.opponent_ai", "AI"),
        OpponentMode.Player2 => OpenSwos.Menu.Loc.Tr("val.opponent_player2", "PLAYER 2  (WASD)"),
        OpponentMode.Demo => OpenSwos.Menu.Loc.Tr("val.opponent_demo", "DEMO  (AI V AI)"),
        _ => "?",
    };

    void OpenSwos.Menu.IMenuHost.StepOpponent(int delta) =>
        _opponentMode = (OpponentMode)(((int)_opponentMode + System.Math.Sign(delta == 0 ? 1 : delta) + 3) % 3);

    // ---- length --------------------------------------------------------------
    string OpenSwos.Menu.IMenuHost.LengthLabel =>
        TotalMatchSeconds < 60
            ? string.Format(OpenSwos.Menu.Loc.Tr("opt.len_sec_whole", "{0}S REAL  (WHOLE MATCH)"), TotalMatchSeconds)
            : string.Format(OpenSwos.Menu.Loc.Tr("opt.len_min_whole", "{0} MIN REAL  (WHOLE MATCH)"), TotalMatchSeconds / 60);

    void OpenSwos.Menu.IMenuHost.StepLength(int delta)
    {
        int d = System.Math.Sign(delta);
        _matchLengthSlot = (_matchLengthSlot + d + MatchLengthPresets.Length) % MatchLengthPresets.Length;
    }

    // ---- speed ---------------------------------------------------------------
    string OpenSwos.Menu.IMenuHost.SpeedLabel =>
        $"x{_gameSpeedScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}";

    void OpenSwos.Menu.IMenuHost.StepSpeed(int delta)
    {
        _gameSpeedScale = System.Math.Round(
            System.Math.Clamp(_gameSpeedScale + System.Math.Sign(delta == 0 ? 1 : delta) * SpeedScaleStep,
                              SpeedScaleMin, SpeedScaleMax), 2);
        SaveSettings();
    }

    // ---- actions -------------------------------------------------------------
    void OpenSwos.Menu.IMenuHost.StartMatch()
    {
        _homeTeamOverride = null;
        _awayTeamOverride = null;
        _match = MatchState.NewMatch();
        EnterPreKickoff();
        _appState = AppState.Match;
    }

    void OpenSwos.Menu.IMenuHost.QuitGame() => GetTree().Quit();

    // ---- competition support ---------------------------------------------------
    // A competition match is a normal sim match with the HUMAN always on the
    // first (home) slot; the menu maps the (player, opponent) score back onto
    // the fixture's home/away orientation itself. The result is captured at the
    // FullTime -> Menu transition in _PhysicsProcess (see _competitionMatchPending
    // consumers in Main.cs) and handed over exactly once via
    // TakeLastCompetitionResult.
    private bool _competitionMatchPending;
    private (int player, int opponent)? _lastCompetitionResult;
    // The human (HOME, sim slots 0..10) team's real in-match energy consumption
    // as a 0..10 distance metric, captured at FullTime alongside the score and
    // handed to MatchEffects for the player's club. Null when none was computed.
    private int? _lastMatchHomeDistance;
    // The human (HOME) team's post-match injuries as (in-game slot, severity 1..7),
    // captured at the same FullTime; handed to the career layer for persistence.
    private System.Collections.Generic.List<(int slot, int severity)>? _lastMatchInjuries;

    // Optional per-match TeamRecord overrides. When set (career fixtures), the
    // sim seeds these LIVE squads instead of the read-only _allTeams master
    // records. Home = the human's slot (see StartCompetitionMatch). Cleared by
    // every non-competition launch. Consumed via EffectiveTeam() in Main.cs.
    private OpenSwos.Assets.TeamRecord? _homeTeamOverride;
    private OpenSwos.Assets.TeamRecord? _awayTeamOverride;

    void OpenSwos.Menu.IMenuHost.StartCompetitionMatch(int playerMasterIndex, int opponentMasterIndex,
        OpenSwos.Assets.TeamRecord? homeOverride, OpenSwos.Assets.TeamRecord? awayOverride)
    {
        int n = _allTeams.Count; if (n == 0) return;
        _homeTeamIndex = System.Math.Clamp(playerMasterIndex, 0, n - 1);
        _awayTeamIndex = System.Math.Clamp(opponentMasterIndex, 0, n - 1);
        if (_awayTeamIndex == _homeTeamIndex) _awayTeamIndex = (_awayTeamIndex + 1) % n;
        _homeTeamOverride = homeOverride;
        _awayTeamOverride = awayOverride;
        _opponentMode = OpponentMode.Ai;   // competition fixtures are vs CPU
        ApplyMatchSetup();
        _competitionMatchPending = true;
        _lastCompetitionResult = null;
        _lastMatchInjuries = null;
        _match = MatchState.NewMatch();
        EnterPreKickoff();
        _appState = AppState.Match;
    }

    (int player, int opponent)? OpenSwos.Menu.IMenuHost.TakeLastCompetitionResult()
    {
        var r = _lastCompetitionResult;
        _lastCompetitionResult = null;
        return r;
    }

    int? OpenSwos.Menu.IMenuHost.TakeLastMatchHomeDistance()
    {
        var d = _lastMatchHomeDistance;
        _lastMatchHomeDistance = null;
        return d;
    }

    System.Collections.Generic.List<(int slot, int severity)>?
        OpenSwos.Menu.IMenuHost.TakeLastMatchInjuries()
    {
        var inj = _lastMatchInjuries;
        _lastMatchInjuries = null;
        return inj;
    }

    int OpenSwos.Menu.IMenuHost.TeamStrength(int idx)
    {
        if (idx < 0 || idx >= _allTeams.Count) return 3;
        var t = _allTeams[idx];
        int n = t.Players.Count, sum = 0;
        foreach (var p in t.Players)
            sum += p.Passing + p.Shooting + p.Heading + p.Tackling + p.Control + p.Speed + p.Finishing;
        return (n > 0) ? System.Math.Clamp(sum / (n * 7), 1, 7) : 3;
    }

    int OpenSwos.Menu.IMenuHost.TeamDivision(int idx) =>
        (idx >= 0 && idx < _allTeams.Count) ? _allTeams[idx].Division : 0;

    int OpenSwos.Menu.IMenuHost.TeamNation(int idx) =>
        (idx >= 0 && idx < _allTeams.Count) ? _allTeams[idx].Nation : 0;

    ushort OpenSwos.Menu.IMenuHost.TeamGlobalId(int idx) =>
        (idx >= 0 && idx < _allTeams.Count) ? _allTeams[idx].GlobalId : (ushort)0;

    System.Collections.Generic.List<int> OpenSwos.Menu.IMenuHost.TeamsByNationDivision(int nation, int division, int max)
    {
        var result = new System.Collections.Generic.List<int>();
        for (int i = 0; i < _allTeams.Count && result.Count < max; i++)
        {
            var t = _allTeams[i];
            if (t.Nation != nation) continue;
            if (division >= 0 && t.Division != division) continue;
            result.Add(i);
        }
        return result;
    }

    System.Collections.Generic.List<int> OpenSwos.Menu.IMenuHost.RandomTeams(int count, int mustInclude)
    {
        // Deterministic per (count, mustInclude): entrant draws must not depend
        // on wall-clock so a re-created competition with the same picks gets the
        // same field (lockstep-friendly; see docs/decisions/01-netcode-model.md).
        var result = new System.Collections.Generic.List<int>();
        int n = _allTeams.Count; if (n == 0) return result;
        if (mustInclude >= 0 && mustInclude < n) result.Add(mustInclude);
        uint rng = (uint)(mustInclude * 2654435761 + count * 40503 + 0x1234567);
        int guard = 0;
        while (result.Count < System.Math.Min(count, n) && guard++ < 10000)
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;   // xorshift32
            int pick = (int)(rng % (uint)n);
            if (!result.Contains(pick)) result.Add(pick);
        }
        return result;
    }

    void OpenSwos.Menu.IMenuHost.StartLocalMultiplayerMatch()
    {
        _homeTeamOverride = null;
        _awayTeamOverride = null;
        _opponentMode = OpponentMode.Player2;
        ApplyMatchSetup();
        _match = MatchState.NewMatch();
        EnterPreKickoff();
        _appState = AppState.Match;
    }

    // ---- gameplay options ------------------------------------------------------
    // Faithful ScaleSkill price/team-value feedback (task #196) — ON by default,
    // toggleable from OPTIONS page 2. Takes effect on the NEXT match load
    // (TeamDataLoader consults SkillScaling.Enabled when writing skills).
    bool OpenSwos.Menu.IMenuHost.SkillScalingEnabled => OpenSwos.Sim.Port.SkillScaling.Enabled;

    void OpenSwos.Menu.IMenuHost.ToggleSkillScaling()
    {
        OpenSwos.Sim.Port.SkillScaling.Enabled = !OpenSwos.Sim.Port.SkillScaling.Enabled;
        SaveSettings();
        ApplyMatchSetup();   // re-seed the preview/next match with the new mode
    }

    // ALL TEAMS EQUAL — the original's "all player teams equal" gameplay option:
    // ScaleSkill's teamAppPercent equalizer only activates for player-coached
    // teams when this is on (swos.asm:35761-35825; SkillScaling.TeamAppPercent).
    bool OpenSwos.Menu.IMenuHost.AllPlayerTeamsEqual => OpenSwos.Sim.Port.SkillScaling.AllPlayerTeamsEqual;

    void OpenSwos.Menu.IMenuHost.ToggleAllPlayerTeamsEqual()
    {
        OpenSwos.Sim.Port.SkillScaling.AllPlayerTeamsEqual = !OpenSwos.Sim.Port.SkillScaling.AllPlayerTeamsEqual;
        SaveSettings();
        ApplyMatchSetup();
    }

    // PRE MATCH MENUS — gates the versus + stadium-lineups screens (the
    // original's "show pre-match menus" gameplay option). Menu-side gate; the
    // host only persists the preference.
    private bool _showPreMatchMenus = true;
    bool OpenSwos.Menu.IMenuHost.ShowPreMatchMenus => _showPreMatchMenus;

    void OpenSwos.Menu.IMenuHost.ToggleShowPreMatchMenus()
    {
        _showPreMatchMenus = !_showPreMatchMenus;
        SaveSettings();
    }

    // ENERGY BAR — renderer-only overlay visibility (energy is always tracked in
    // the sim; this just shows/hides the on-pitch bars). PLAYER FATIGUE — the
    // master toggle that enables the speed penalty in NON-career matches
    // (competition/career always enable it, see InitSwosVmFromMatchSetup).
    // Energy bar is only meaningful when the fatigue simulation is on. When
    // PLAYER FATIGUE is off the bar reads/forces OFF and the toggle is inert
    // (user spec: "can't enable the bars when fatigue mode is off").
    bool OpenSwos.Menu.IMenuHost.EnergyBar => _energyBar && _fatigueSim;
    void OpenSwos.Menu.IMenuHost.ToggleEnergyBar()
    {
        if (!_fatigueSim) { _energyBar = false; return; }   // locked off while fatigue is off
        _energyBar = !_energyBar;
        SaveSettings();
    }
    bool OpenSwos.Menu.IMenuHost.FatigueSim => _fatigueSim;
    void OpenSwos.Menu.IMenuHost.ToggleFatigueSim()
    {
        _fatigueSim = !_fatigueSim;
        if (!_fatigueSim) _energyBar = false;   // turning fatigue off also clears the bar
        SaveSettings();
    }

    // ---- audio (SOUND: PC / AMIGA) -------------------------------------------
    // The player's PREFERENCE (persisted). What actually plays is resolved against
    // availability at match start (ResolveSoundSource), so a persisted-but-missing
    // source degrades gracefully. Availability probes are cheap+cached.
    private OpenSwos.Audio.MatchAudio.SoundSource _soundSource =
        OpenSwos.Audio.MatchAudio.SoundSource.Pc;

    private static bool SoundSourceAvailable(OpenSwos.Audio.MatchAudio.SoundSource s) =>
        s == OpenSwos.Audio.MatchAudio.SoundSource.Amiga
            ? OpenSwos.Audio.MatchAudio.AmigaAvailable()
            : OpenSwos.Audio.MatchAudio.PcAvailable();

    string OpenSwos.Menu.IMenuHost.SoundSourceLabel
    {
        get
        {
            string name = _soundSource == OpenSwos.Audio.MatchAudio.SoundSource.Amiga
                ? OpenSwos.Menu.Loc.Tr("val.sound_amiga", "AMIGA")
                : OpenSwos.Menu.Loc.Tr("val.sound_pc", "PC");
            return SoundSourceAvailable(_soundSource) ? name : name + " (" + OpenSwos.Menu.Loc.Tr("common.na", "N/A") + ")";
        }
    }

    void OpenSwos.Menu.IMenuHost.StepSoundSource(int delta)
    {
        // Only two sources — cycling ALWAYS toggles to the other one. It used to refuse
        // when the other source was unavailable, which made this a DEAD BUTTON: with no
        // PC files installed the row could not be moved off PC at all, and the player
        // just got silence with no explanation. The label marks an unavailable choice
        // "(N/A)" and ResolveSoundSource still degrades to whatever exists at match
        // start, so letting the preference move is safe and far less confusing.
        _soundSource = _soundSource == OpenSwos.Audio.MatchAudio.SoundSource.Pc
            ? OpenSwos.Audio.MatchAudio.SoundSource.Amiga
            : OpenSwos.Audio.MatchAudio.SoundSource.Pc;
        SaveSettings();
    }

    /// <summary>
    /// Point the SOUND preference at a source that actually exists. Called once after
    /// settings load, when asset availability is finally known. Without this a fresh
    /// install defaults to PC, and a player with only Amiga files gets NO match sound
    /// until they find the option themselves — the common case, since PC sound needs
    /// the CD. Only moves the preference when the current pick is unusable and the
    /// other one works, so an explicit choice is never overridden silently.
    /// </summary>
    private void NormalizeSoundSource()
    {
        if (SoundSourceAvailable(_soundSource)) return;
        var other = _soundSource == OpenSwos.Audio.MatchAudio.SoundSource.Pc
            ? OpenSwos.Audio.MatchAudio.SoundSource.Amiga
            : OpenSwos.Audio.MatchAudio.SoundSource.Pc;
        if (!SoundSourceAvailable(other)) return;
        GD.Print($"[Audio] SOUND preference {_soundSource} unavailable -> auto-selected {other}.");
        _soundSource = other;
        SaveSettings();
    }

    // OPTIONS "COMMENTATOR" row — ON / OFF. Silences spoken commentary even on
    // PC sound (see MatchAudio.CommentatorEnabled, applied at match start).
    string OpenSwos.Menu.IMenuHost.CommentatorLabel =>
        _commentator ? OpenSwos.Menu.Loc.Tr("common.on", "ON") : OpenSwos.Menu.Loc.Tr("common.off", "OFF");

    void OpenSwos.Menu.IMenuHost.StepCommentator(int delta)
    {
        // Two-state toggle — any delta flips it.
        _commentator = !_commentator;
        SaveSettings();
    }

    // Resolve the PREFERENCE against availability for the next match: prefer the
    // player's choice if available, else the other if available, else PC (silent).
    private OpenSwos.Audio.MatchAudio.SoundSource ResolveSoundSource()
    {
        if (SoundSourceAvailable(_soundSource)) return _soundSource;
        var other = _soundSource == OpenSwos.Audio.MatchAudio.SoundSource.Pc
            ? OpenSwos.Audio.MatchAudio.SoundSource.Amiga
            : OpenSwos.Audio.MatchAudio.SoundSource.Pc;
        if (SoundSourceAvailable(other)) return other;
        return OpenSwos.Audio.MatchAudio.SoundSource.Pc;
    }

    // ---- menu music (MUSIC: AMIGA / PC / CUSTOM / OFF) -----------------------
    // The player's front-end music PREFERENCE (persisted, field _menuMusic in
    // Main.cs). What actually plays is resolved against availability by
    // ResolveMenuMusic() and applied by UpdateMenuMusic(). Availability probes
    // are cheap+cached. Mirrors the SOUND idiom above but for 4 states.
    private static bool MenuMusicSourceAvailable(OpenSwos.Audio.MenuMusic.MusicSource s) => s switch
    {
        OpenSwos.Audio.MenuMusic.MusicSource.Amiga  => OpenSwos.Audio.MenuMusic.AmigaAvailable(),
        OpenSwos.Audio.MenuMusic.MusicSource.Pc     => OpenSwos.Audio.MenuMusic.PcAvailable(),
        OpenSwos.Audio.MenuMusic.MusicSource.Custom => OpenSwos.Audio.MenuMusic.CustomAvailable(),
        _ => true,  // Off always "available"
    };
    private static string MenuMusicName(OpenSwos.Audio.MenuMusic.MusicSource s) => s switch
    {
        OpenSwos.Audio.MenuMusic.MusicSource.Amiga => OpenSwos.Menu.Loc.Tr("val.sound_amiga", "AMIGA"),
        OpenSwos.Audio.MenuMusic.MusicSource.Pc => OpenSwos.Menu.Loc.Tr("val.sound_pc", "PC"),
        OpenSwos.Audio.MenuMusic.MusicSource.Custom => OpenSwos.Menu.Loc.Tr("val.music_custom", "CUSTOM"),
        _ => OpenSwos.Menu.Loc.Tr("val.music_off", "OFF"),
    };
    string OpenSwos.Menu.IMenuHost.MenuMusicLabel
    {
        get { string n = MenuMusicName(_menuMusic); return MenuMusicSourceAvailable(_menuMusic) ? n : n + " (" + OpenSwos.Menu.Loc.Tr("common.na", "N/A") + ")"; }
    }
    void OpenSwos.Menu.IMenuHost.StepMenuMusic(int delta)
    {
        // cycle AMIGA->PC->CUSTOM->OFF (delta>0) skipping unavailable sources
        var order = new[] { OpenSwos.Audio.MenuMusic.MusicSource.Amiga, OpenSwos.Audio.MenuMusic.MusicSource.Pc,
                            OpenSwos.Audio.MenuMusic.MusicSource.Custom, OpenSwos.Audio.MenuMusic.MusicSource.Off };
        int i = System.Array.IndexOf(order, _menuMusic); if (i < 0) i = 0;
        int step = delta >= 0 ? 1 : -1;
        for (int k = 0; k < order.Length; k++)
        {
            i = ((i + step) % order.Length + order.Length) % order.Length;
            if (MenuMusicSourceAvailable(order[i])) { _menuMusic = order[i]; break; }
        }
        SaveSettings();
        // Apply immediately so the user can audition (we are in the menu).
        OpenSwos.Audio.MenuMusic.Instance?.Play(ResolveMenuMusic());
    }

    // True exactly once after a NON-competition match returns to the menu —
    // drives the post-friendly PLAY AGAIN / EXIT screen (replayExit.mnu).
    private bool _friendlyJustEnded;
    bool OpenSwos.Menu.IMenuHost.TakeFriendlyJustEnded()
    {
        bool v = _friendlyJustEnded;
        _friendlyJustEnded = false;
        return v;
    }

    // ---- display / fullscreen --------------------------------------------------
    // OPTIONS "DISPLAY" row — mirrors the F11 hotkey exactly (cycle Windowed ->
    // Fullscreen Fill -> Fullscreen Integer). The label uses the same uppercase
    // ASCII text the hotkey path uses (SWOS charset has no lowercase).
    string OpenSwos.Menu.IMenuHost.DisplayModeLabel => DisplayModeToLabel(_displayMode);

    void OpenSwos.Menu.IMenuHost.CycleDisplayMode() => CycleDisplayModeInternal();

    // OPTIONS "SMOOTHING" row — OFF / SHARP. Flips the root viewport present
    // filter (see ApplySmoothing) and persists the choice.
    string OpenSwos.Menu.IMenuHost.SmoothingLabel =>
        _smoothing ? OpenSwos.Menu.Loc.Tr("opt.smoothing_sharp", "SHARP") : OpenSwos.Menu.Loc.Tr("common.off", "OFF");
    void OpenSwos.Menu.IMenuHost.CycleSmoothing()
    {
        _smoothing = !_smoothing;
        ApplySmoothing();
        SaveSettings();
    }

    // Persist the UI language after the user picks it in OPTIONS.
    void OpenSwos.Menu.IMenuHost.OnLanguageChanged() => SaveSettings();

    // ---- team-config data ----------------------------------------------------
    OpenSwos.Assets.TeamRecord OpenSwos.Menu.IMenuHost.Team(int idx)
    {
        if (idx < 0 || idx >= _allTeams.Count) return new OpenSwos.Assets.TeamRecord();
        return _allTeams[idx];
    }

    Color OpenSwos.Menu.IMenuHost.KitSwatch(byte colorByte) => kTeamPaletteSwatch[colorByte & 15];
}
