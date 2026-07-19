namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Per-tick game orchestrator ported from external/swos-port/src/game/gameLoop.cpp.
//
// The SWOS gameLoop has two layers:
//   1. The OUTER loop (gameLoop:84-135) — owns init/fade/replay/exit and
//      drives match-level lifecycle. It contains a `while (true)` that calls
//      the per-tick functions below. We do NOT port the outer loop — Godot's
//      `_PhysicsProcess` owns frame pacing instead.
//   2. The INNER per-tick body (gameLoop:95-130). Inside that we focus on
//      `coreGameUpdate()` (gameLoop:289-317), which is the deterministic
//      "advance one match-tick" pipeline. Everything else (loadCrowdChantSample,
//      updateTimers, handlePauseAndStats, handleKeys, drawFrame, handleHighlights,
//      updateScreen, gameFadeIn/Out) is audio/render/input which Godot handles.
//
// `Tick()` is the entry from Main._PhysicsProcess when `_useSwosPort` is set.
// It mirrors only the per-tick body (updateTimers stub + CoreGameUpdate).
//
// `CoreGameUpdate()` mirrors gameLoop.cpp:289-317 exactly, in order:
//      moveCamera                          → stub (camera owned by Godot)
//      playEnqueuedSamples                 → stub (audio owned by Godot)
//      updateGameTime                      → GameTime.UpdateGameTime
//      initGoalSprites                     → stub (sprites owned by Godot)
//      updateGameTimersAndCameraBreakMode  → UpdateGameTimersAndCameraBreakMode
//      if (!updateFireBlocked()) {
//          team = selectTeamForUpdate
//          updateTeamControls(team)        → stub (input owned by Godot)
//          updatePlayers(team)             → stub (separate port)
//          postUpdateTeamControls(team)    → stub
//      }
//      updateBall                          → BallUpdate.Tick
//      movePlayers                         → stub
//      updateReferee                       → Referee.UpdateReferee
//      updateCornerFlags                   → stub
//      updateSpinningLogo                  → stub
//      DoGoalkeeperSprites                 → stub
//      updateControlledPlayerNumbers       → stub
//      markPlayer                          → stub
//      updateCurrentPlayerName             → stub
//      updateBookedPlayerNumberSprite      → stub
//      updateResult                        → stub
//      SWOS::DrawAnimatedPatterns          → stub
//      updateBench                         → stub
//      updateStatistics                    → stub (separate stats port)
//
// gameLoop.cpp explicitly does NOT call updateGoals() — `goalScored` flag is
// set by ball.cpp Section 4 (out-of-play / goal detection) and consumed by
// `gameOver()` / replay handlers in the outer loop. We don't call it either.
// The stat counters are bumped from BallUpdate.GoalScored() event-style.
public static class GameLoop
{
    // gameControls.cpp:60 — team selector returns top or bottom each frame
    // (alternates via ++counter & 1). We surface it as an enum for callers.
    public enum SelectedTeam
    {
        Top,
        Bottom,
    }

    // Per-tick entry. Mirrors the deterministic portion of gameLoop.cpp:95-130
    // (the per-tick body inside the inner while-loop). Audio/render/input are
    // delegated to Godot via stubs.
    public static void Tick()
    {
        // from gameLoop.cpp:99 — loadCrowdChantSampleIfNeeded (audio)
        StubLoadCrowdChantSampleIfNeeded();

        // from gameLoop.cpp:100 — updateTimers (frame counter + space-replay timer)
        UpdateTimers();

        // from gameLoop.cpp:101 — handlePauseAndStats (input/UI)
        StubHandlePauseAndStats();

        // from gameLoop.cpp:103 — handleKeys (input)
        StubHandleKeys();

        // from gameLoop.cpp:105 — coreGameUpdate (per-tick sim body)
        CoreGameUpdate();

        // gameLoop.cpp:106 (drawFrame), :117 (handleHighlightsAndReplays),
        // :120-126 (gameFadeIn/Out / updateScreen) are render/replay layer.
        // Godot owns rendering; replays are deferred until later port phases.
    }

    // gameLoop.cpp:265-273 — updateTimers.
    public static void UpdateTimers()
    {
        // from gameLoop.cpp:267 — ReadTimerDelta (HW timer tick read) is a
        // platform abstraction; Godot's process_delta supplies the same.
        ReadTimerDelta();

        // from gameLoop.cpp:269 — `swos.frameCount++` (dword).
        int frame = Memory.ReadSignedDword(Memory.Addr.frameCounter) + 1;
        Memory.WriteDword(Memory.Addr.frameCounter, frame);

        // from external/swos-port/src/video/timer.cpp:154-157 — timerProc:
        //   swos.currentTick     += framesElapsed;
        //   if (!isGamePaused()) swos.currentGameTick += framesElapsed;
        // swos-port's timerProc runs from an SDL timer thread; we don't have
        // one (Godot owns the tick clock). Fold the increment into UpdateTimers
        // so the same per-tick contract holds — every CoreGameUpdate sees a
        // currentGameTick that's bumped by lastFrameTicks (==1 in normal play).
        // Without this, every AI bit-mask (e.g. `currentGameTick & 0x20` jitter
        // checks scattered across updatePlayers/AiBrain) reads zero forever.
        short lastFrameTicks = Memory.ReadSignedWord(Memory.Addr.lastFrameTicks);
        if (lastFrameTicks <= 0) lastFrameTicks = 1;
        // Pause path is currently no-op in our port (StubHandlePauseAndStats);
        // when pause lands it should gate this increment via isGamePaused().
        ushort gameTick = Memory.ReadWord(Memory.Addr.currentGameTick);
        Memory.WriteWord(Memory.Addr.currentGameTick, (ushort)(gameTick + (ushort)lastFrameTicks));

        // from gameLoop.cpp:271-272 — `if (swos.spaceReplayTimer) swos.spaceReplayTimer--;`
        short spaceReplay = Memory.ReadSignedWord(Memory.Addr.spaceReplayTimer);
        if (spaceReplay != 0)
            Memory.WriteWord(Memory.Addr.spaceReplayTimer, spaceReplay - 1);
    }

    // gameLoop.cpp:289-317 — coreGameUpdate.
    // Mechanical mirror of the C++ entry order. Each call site cites its
    // origin line; stubs replace external deps (audio / render / input).
    public static void CoreGameUpdate()
    {
        // from gameLoop.cpp:291 — moveCamera. Wired from camera.cpp:97-119
        // (picks mode: bookingPlayer / penaltyShootout / bench / leavingBench /
        // standard, then runs updateCameraCoordinates + updateCameraLeaving).
        // Without this call, camera{X,Y}Position stay at their initial value,
        // breakCameraMode never transitions from -1, and the AI's
        // `cameraDirection` reads stale.
        // Source: external/swos-port/src/game/camera.cpp:97.
        Camera.MoveCamera();

        // from gameLoop.cpp:292 — playEnqueuedSamples. Audio sample timers
        // are stubbed (no audio bus wired yet) but the goalCounter--
        // side-effect at comments.cpp:206-207 IS the canonical SWOS
        // celebration timer countdown. Co-locating it here matches the
        // original call ordering — runs BEFORE updateGameTime / Timers /
        // PostGoalRestart so any of those see the freshly-decremented value.
        PlayEnqueuedSamples();

        // from gameLoop.cpp:293 — updateGameTime (clock + half/full-time FSM)
        GameTime.UpdateGameTime();

        // from gameLoop.cpp:294 — initGoalSprites (one-shot per frame sprite reset)
        InitGoalSprites();

        // from gameLoop.cpp:295 — updateGameTimersAndCameraBreakMode.
        // This is the FULL faithful stoppage/restart state machine
        // (gameLoop.cpp:426-1853): stoppage timers, the fire-press skip paths,
        // the l_stoppage_event_triggered dispatcher (half-time walk, game-end,
        // extra-time, kickoff restarts) and the breakCameraMode 0→8 ladder.
        // The previous port-only collapses (UpdatePostGoalRestart,
        // TickHalftimeCeremony, TickFullTimeCeremony, SetPieces.TickSetPieces)
        // are deleted — every restart now flows through this machine like the
        // original.
        UpdateGameTimersAndCameraBreakMode();

        // from gameLoop.cpp:296-301 — fire-gated team controls + player update.
        if (!UpdateFireBlocked())
        {
            SelectedTeam team = SelectTeamForUpdate();
            // gameControls.cpp:67-90 — UpdateTeamControls(team).
            // Previously stubbed here — without it, the per-tick joystick
            // bitmask in Memory.Addr.ic_pl{1,2}Events never propagates into
            // team.currentAllowedDirection, so PlayerControlled.RunControlledBranch
            // always reads -1 (kNoDirection) and the human player just stands
            // there. InputControls.UpdateTeamControls is the real ported entry
            // (InputControls.cs:159).
            bool top = team == SelectedTeam.Top;
            InputControls.UpdateTeamControls(top);
            UpdatePlayers(team);
            // gameControls.cpp:92-99 — PostUpdateTeamControls(team).
            // Clears headerOrTackle + zeroes that player's fire counter so the
            // same fire can't trigger a second header on the next tick.
            InputControls.PostUpdateTeamControls(top);
        }

        // from gameLoop.cpp:302 — updateBall (full physics + state machine)
        BallUpdate.Tick();

        // from gameLoop.cpp:303 — movePlayers (sprite-world update for all players)
        // Ported to SpriteUpdate.MoveAllPlayers (updateSprite.cpp:145-155).
        SpriteUpdate.MoveAllPlayers();

        // from gameLoop.cpp:304 — updateReferee (referee state machine)
        Referee.UpdateReferee();

        // from gameLoop.cpp:305 — updateCornerFlags (animation)
        // Ported to GameSprites.UpdateCornerFlags (gameSprites.cpp:252-279).
        GameSprites.UpdateCornerFlags();

        // from gameLoop.cpp:306 — updateSpinningLogo (UI)
        // Ported to SpinningLogo.UpdateSpinningLogo (spinningLogo.cpp:17-26).
        SpinningLogo.UpdateSpinningLogo();

        // from gameLoop.cpp:308 — DoGoalkeeperSprites (animation override)
        DoGoalkeeperSprites();

        // from gameLoop.cpp:309 — updateControlledPlayerNumbers (UI badge)
        // Ported to GameSprites.UpdateControlledPlayerNumbers (gameSprites.cpp:281-312).
        GameSprites.UpdateControlledPlayerNumbers();

        // from gameLoop.cpp:310 — markPlayer (controlled-player arrow)
        MarkPlayer();

        // from gameLoop.cpp:311 — updateCurrentPlayerName (UI banner)
        // Ported to PlayerNameDisplay.UpdateCurrentPlayerName (playerNameDisplay.cpp:20-55).
        PlayerNameDisplay.UpdateCurrentPlayerName();

        // from gameLoop.cpp:312 — updateBookedPlayerNumberSprite. Full port
        // lives in Referee.UpdateBookedPlayerNumberSprite (referee.cpp:101-151):
        // clears digit image each frame, advances refTimer while booking,
        // indexes the 30-byte blink table; the `-1` sentinel at table[29] is
        // what triggers PutRefereeToLeavingState + SendPlayerAway. Without
        // this call, refTimer never advances past 0, so the sentinel never
        // fires and red-carded players never leave the pitch.
        // Source: external/swos-port/src/game/referee.cpp:101.
        Referee.UpdateBookedPlayerNumberSprite();

        // from gameLoop.cpp:313 — updateResult. Full port lives in
        // Result.UpdateResult (result.cpp:120-137): drives the resultTimer
        // countdown, transitions from showResult=1 (visible) → showResult=0
        // (hidden) when timer expires, handles sentinel values
        // (kEndOfHalfResult etc.) for first-frame initialisation. Without
        // this call, resultTimer never decrements, so res_showResult stays
        // latched and AI / set-piece pipelines that gate on
        // `resultTimer != 0` (Referee.UpdateBookedPlayerNumberSprite, all
        // SetPieces.Tick* helpers) sit out forever.
        // Source: external/swos-port/src/game/result.cpp:120.
        Result.UpdateResult();

        // from gameLoop.cpp:314 — DrawAnimatedPatterns (UI flashes)
        DrawAnimatedPatterns();

        // from gameLoop.cpp:315 — updateBench. Full port lives in
        // Bench.UpdateBench (bench.cpp:36-40 + updateBench.cpp:169-192):
        // benchCheckControls() returns true only on the tick the bench is
        // FIRST invoked (out-of-bench → in-bench transition); invokeBench()
        // seeds m_bench{Y} and sets g_inSubstitutesMenu = 1. The full UI
        // path is owned by the Godot host — Bench.UpdateBench encapsulates
        // the "while in-bench, no-op / while out-of-bench, never auto-invoke"
        // contract that's safe for the smoke path.
        // Source: external/swos-port/src/game/bench/bench.cpp:36-40
        //         external/swos-port/src/game/bench/updateBench.cpp:169-192.
        Bench.UpdateBench();

        // from gameLoop.cpp:316 — updateStatistics. Full port lives in
        // Stats.UpdateStatistics (stats.cpp:74-112): bumps the lastTeamPlayed
        // possession counter every tick during ST_GAME_IN_PROGRESS, latches
        // / clears st_isGoalAttempt when the ball enters/leaves the keeper
        // area, and increments goalAttempts / onTarget on the defending
        // team's TeamStatsData. Without this call, possession % stays 0/0
        // forever and the stats overlay shows blank totals.
        // Source: external/swos-port/src/game/stats.cpp:74.
        Stats.UpdateStatistics();
    }

    // gameControls.cpp:48-56 — updateFireBlocked.
    // While the fireBlocked latch is set, the player-update branch is skipped
    // until both players have released the fire button. Latch is cleared once
    // `isAnyPlayerFiring()` returns false.
    public static bool UpdateFireBlocked()
    {
        ushort fireBlocked = Memory.ReadWord(Memory.Addr.fireBlocked);
        if (fireBlocked != 0)
        {
            // from gameControls.cpp:51 — `if (!isAnyPlayerFiring()) swos.fireBlocked = 0;`
            if (!InputControls.IsAnyPlayerFiring())
                Memory.WriteWord(Memory.Addr.fireBlocked, 0);
            return true;
        }
        return false;
    }

    // gameControls.cpp:58-62 — selectTeamForUpdate.
    // `auto team = ++counter & 1 ? &topTeamData : &bottomTeamData`.
    // Pre-increment; bit 0 picks top (odd) vs bottom (even) each tick.
    public static SelectedTeam SelectTeamForUpdate()
    {
        int counter = Memory.ReadSignedDword(Memory.Addr.teamSwitchCounter) + 1;
        Memory.WriteDword(Memory.Addr.teamSwitchCounter, counter);
        return (counter & 1) != 0 ? SelectedTeam.Top : SelectedTeam.Bottom;
    }

    // gameLoop.cpp:426-1853 — updateGameTimersAndCameraBreakMode.
    //
    // FULL mechanical port of the asm-translated stoppage/restart state
    // machine. Layout mirrors the original label structure:
    //   :430-469   penalty shootout inter-pen pause  (SetPieces.AdvancePenaltiesTimer)
    //   :472-497   gameStatePl==100 → inGameCounter += lastFrameTicks, return
    //   :499-517   not in progress → counters + stoppageTimerTotal
    //   :519-575   gameStatePl==102 (ST_WAITING_ON_PLAYER) → stoppageTimerActive
    //              accumulator + CPU 825-tick prepareForInitialKick fallback
    //   :577-816   l_not_waiting_on_player — fire-press skip paths for
    //              gameState 21..30 (end-game transmission fast-forward)
    //   :818-843   l_game_started — stoppageEventTimer countdown
    //   :845-1108  l_stoppage_event_triggered — state dispatcher
    //   :1110-1846 l_game_running — breakCameraMode ladder 0→1→2→4→3→5→6→7→8
    public static void UpdateGameTimersAndCameraBreakMode()
    {
        // PORT NOTE (interval seeds): gameLoop.cpp:53-54 declare
        //   static int m_goalCameraInterval = 55;
        //   static int m_allowPlayerControlCameraInterval = 550;
        // but Memory.Init currently seeds 50 / 75 into those slots (stale
        // values from an earlier partial port; Memory.cs is off-limits in this
        // session). Correct exactly the known-stale seeds here — idempotent,
        // leaves any deliberate SetGoalCameraInterval / SetAllowPlayerControl-
        // CameraInterval override intact (those write different values).
        if (Memory.ReadSignedWord(Memory.Addr.m_goalCameraInterval) == 50)
            Memory.WriteWord(Memory.Addr.m_goalCameraInterval, 55);
        if (Memory.ReadSignedWord(Memory.Addr.m_allowPlayerControlCameraInterval) == 75)
            Memory.WriteWord(Memory.Addr.m_allowPlayerControlCameraInterval, 550);

        // from gameLoop.cpp:430-469 — penalty shootout inter-pen pause.
        // Bumps `penaltiesTimer` while shootout is active and gameState !=
        // ST_PENALTIES; fires NextPenalty() once it hits m_penaltiesInterval.
        // Asm order puts this branch BEFORE the in-progress split so shootout
        // progression keeps ticking regardless of gameStatePl.
        SetPieces.AdvancePenaltiesTimer();

        // from gameLoop.cpp:472-497 — in-progress path:
        //   if (gameStatePl == ST_GAME_IN_PROGRESS) {
        //       D0 = lastFrameTicks; inGameCounter += D0; return;
        //   }
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        const short kGameStateInProgress = 100;
        if (gameStatePl == kGameStateInProgress)
        {
            short ticks = Memory.ReadSignedWord(Memory.Addr.lastFrameTicks);
            short ig = Memory.ReadSignedWord(Memory.Addr.inGameCounter);
            Memory.WriteWord(Memory.Addr.inGameCounter, (short)(ig + ticks));
            return;
        }

        // l_game_not_in_progress:
        // from gameLoop.cpp:499-507 — `add gameNotInProgressCounterWriteOnly, 1`
        // (g_memByte[523104] — the same slot the state setters below zero).
        short notIn = Memory.ReadSignedWord(Memory.Addr.gameNotInProgressCounterWriteOnly);
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, (short)(notIn + 1));

        // from gameLoop.cpp:508-517 — `add stoppageTimerTotal, lastFrameTicks`.
        short ticks2 = Memory.ReadSignedWord(Memory.Addr.lastFrameTicks);
        short st = Memory.ReadSignedWord(Memory.Addr.stoppageTimerTotal);
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, (short)(st + ticks2));

        // from gameLoop.cpp:518-528 — `cmp gameStatePl, ST_WAITING_ON_PLAYER (102);
        // jnz @@not_waiting_on_player`.
        const short kWaitingOnPlayer = 102;
        if (gameStatePl == kWaitingOnPlayer)
        {
            // gameLoop.cpp:530-537 — `add stoppageTimerActive, ax` (ax==lastFrameTicks).
            short active = Memory.ReadSignedWord(Memory.Addr.stoppageTimerActive);
            active = (short)(active + ticks2);
            Memory.WriteWord(Memory.Addr.stoppageTimerActive, active);

            // gameLoop.cpp:538-541 — A6 = lastTeamPlayedBeforeBreak;
            // ax = [A6+TeamGeneralInfo.playerNumber]. NULL guard is port-side
            // defensive (the original would fault on a null A6).
            int teamPtr = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
            if (teamPtr == 0)
                return;
            short playerNumber = Memory.ReadSignedWord(teamPtr + TeamData.OffPlayerNumber);

            short interval = Memory.ReadSignedWord(Memory.Addr.m_initalKickInterval);

            // === PORT-ONLY LAST-RESORT SAFETY NET =========================
            // NOT in the original. If the faithful paths below (AI presses
            // fire via updatePlayers, or the 825-tick prepareForInitialKick
            // fallback) both fail to restart play, force-fire the kick once
            // stoppageTimerActive exceeds 2 × m_initalKickInterval (1650).
            // CPU-controlled kicking team only (playerNumber == 0). Logs
            // loudly so smoke tests reveal that the faithful path failed.
            if (playerNumber == 0 && (ushort)active >= (ushort)(2 * interval))
            {
                int kicker = Memory.ReadSignedDword(teamPtr + TeamData.OffControlledPlayer);
                if (kicker == 0)
                    kicker = Memory.ReadSignedDword(teamPtr + TeamData.OffPassingKickingPlayer);
                Godot.GD.Print(
                    $"[PORT-SAFETY] gameStatePl=102 stalled {active} ticks " +
                    $"(gameState={Memory.ReadSignedWord(Memory.Addr.gameState)}, " +
                    $"breakState={Memory.ReadSignedWord(Memory.Addr.breakState)}) — " +
                    "faithful AI kick path failed; force-firing PlayerKickingBall.");
                if (kicker != 0)
                    PlayerActions.PlayerKickingBall(teamPtr, kicker);
                Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);
                return;
            }
            // === end PORT-ONLY safety net =================================

            // gameLoop.cpp:542-546 — `or ax, ax; jnz @@jmp_out`. Human team
            // (playerNumber != 0) keeps waiting — the human must press fire.
            if (playerNumber != 0)
                return;

            // gameLoop.cpp:548-558 — `cmp stoppageTimerActive, m_initalKickInterval;
            // jb @@jmp_out`. Unsigned `jb` ⇒ act once `active >= interval` (825).
            if ((ushort)active < (ushort)interval)
                return;

            // gameLoop.cpp:560 — `call PrepareForInitialKick` (no-arg faithful
            // port, gameLoop.cpp:2113-2157 — reads teamStarting/teamPlayingUp
            // itself, resets ball to (336,449), gameState=0, gameStatePl=101,
            // breakCameraMode=-1, stops all players).
            Kickoff.PrepareForInitialKick();

            // gameLoop.cpp:561-571 — `add initialKickWriteOnlyTicks, 1`. Write-only
            // counter (no readers in our ported scope); incremented for parity.
            short kickTicks = Memory.ReadSignedWord(Memory.Addr.initialKickWriteOnlyTicks);
            Memory.WriteWord(Memory.Addr.initialKickWriteOnlyTicks, (short)(kickTicks + 1));

            // gameLoop.cpp:572 — `jmp @@out`.
            return;
        }

        // l_not_waiting_on_player — gameLoop.cpp:577-816.
        // Fire-press fast-forward for the "transmission" states 21..30
        // (starting-game intro, half-time result, end-game result, shower
        // walks). A fire press skips the dwell.
        short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);

        // gameLoop.cpp:577-600 — `cmp gameState, ST_STARTING_GAME (21); jb
        // @@game_started` then `cmp gameState, ST_GAME_ENDED (30); ja
        // @@game_started`.
        if ((ushort)gameState >= 21 && (ushort)gameState <= 30)
        {
            bool firePressed = false;

            // gameLoop.cpp:602-607 — `mov al, topTeamData.firePressed` (byte at
            // topTeamData+50); jnz @@end_game_transmission.
            if (Memory.ReadByte(TeamData.TopBase + TeamData.OffFirePressed) != 0)
                firePressed = true;
            // gameLoop.cpp:609-614 — bottomTeamData.firePressed.
            else if (Memory.ReadByte(TeamData.BottomBase + TeamData.OffFirePressed) != 0)
                firePressed = true;
            else
            {
                // gameLoop.cpp:616-638 — if either team's playerCoachNumber == 1
                // → l_check_pl1_fire: Player1StatusProc + `mov al, pl1Fire`.
                // Player1StatusProc is the SDL input poll; in our port Main.cs
                // refreshes the input latches before every Tick, so reading
                // the ic_pl1Fire latch is the equivalent observable.
                short topCoach = Memory.ReadSignedWord(TeamData.TopBase + TeamData.OffPlayerCoachNumber);
                short bottomCoach = Memory.ReadSignedWord(TeamData.BottomBase + TeamData.OffPlayerCoachNumber);
                if (topCoach == 1 || bottomCoach == 1)
                {
                    // gameLoop.cpp:640-647.
                    if (Memory.ReadSignedWord(Memory.Addr.ic_pl1Fire) != 0)
                        firePressed = true;
                }
                if (!firePressed && (topCoach == 2 || bottomCoach == 2))
                {
                    // gameLoop.cpp:649-681 — l_check_pl2_fire: Player2StatusProc
                    // + `mov al, pl2Fire`.
                    if (Memory.ReadSignedWord(Memory.Addr.ic_pl2Fire) != 0)
                        firePressed = true;
                }
                if (!firePressed)
                {
                    // gameLoop.cpp:683-710 — l_check_control_states_again: only
                    // for ST_RESULT_AFTER_THE_GAME (26), poll BOTH players.
                    if (gameState == 26 &&
                        (Memory.ReadSignedWord(Memory.Addr.ic_pl1Fire) != 0 ||
                         Memory.ReadSignedWord(Memory.Addr.ic_pl2Fire) != 0))
                        firePressed = true;
                }
            }

            if (firePressed)
            {
                // l_end_game_transmission — gameLoop.cpp:712-816.
                if (gameState == 25) // ST_RESULT_ON_HALFTIME
                {
                    // gameLoop.cpp:712-746 — neg resultTimer / timeVar /
                    // statsTimer; SetCameraMovingToShowerState;
                    // PrepareForInitialKick; stoppageEventTimer = 0.
                    NegateResultPanelTimers();
                    SetCameraMovingToShowerState();
                    Kickoff.PrepareForInitialKick();
                    Memory.WriteWord(Memory.Addr.stoppageEventTimer, 0);
                    return;
                }
                if (gameState == 26) // ST_RESULT_AFTER_THE_GAME
                {
                    // gameLoop.cpp:748-781 — neg timers; playGame = 0;
                    // stoppageEventTimer = 0.
                    NegateResultPanelTimers();
                    Memory.WriteWord(Memory.Addr.playGame, 0);
                    Memory.WriteWord(Memory.Addr.stoppageEventTimer, 0);
                    return;
                }
                if (gameState == 21) // ST_STARTING_GAME
                {
                    // gameLoop.cpp:783-799 — showFansCounter = 0;
                    // PrepareForInitialKick; stoppageEventTimer = 0.
                    Memory.WriteWord(Memory.Addr.showFansCounter, 0);
                    Kickoff.PrepareForInitialKick();
                    Memory.WriteWord(Memory.Addr.stoppageEventTimer, 0);
                    return;
                }
                if (gameState == 22) // ST_CAMERA_GOING_TO_SHOWERS
                {
                    // gameLoop.cpp:801-816 — PrepareForInitialKick;
                    // stoppageEventTimer = 0.
                    Kickoff.PrepareForInitialKick();
                    Memory.WriteWord(Memory.Addr.stoppageEventTimer, 0);
                    return;
                }
                // Other states in 21..30 fall through to l_game_started —
                // matches the original (only 25/26/21/22 have skip handlers).
            }
        }

        // l_game_started — gameLoop.cpp:818-843. stoppageEventTimer countdown.
        //   or ax, ax; jz @@stoppage_event_triggered      (already 0 → fire)
        //   sub stoppageEventTimer, lastFrameTicks
        //   js  @@stoppage_event_triggered                (went negative → fire)
        //   jnz @@out                                     (still positive → wait)
        //   (fall through — hit exactly 0 → fire)
        short evt = Memory.ReadSignedWord(Memory.Addr.stoppageEventTimer);
        if (evt != 0)
        {
            evt -= ticks2;
            Memory.WriteWord(Memory.Addr.stoppageEventTimer, evt);
            if (evt > 0)
                return;
        }

        // l_stoppage_event_triggered — gameLoop.cpp:845-1846.
        DispatchStoppageEventTriggered(gameStatePl);
    }

    // gameLoop.cpp:725-742 / 761-778 / 874-891 / 908-925 / 990-1007 — the
    // recurring `neg resultTimer ; neg timeVar ; neg statsTimer` triple that
    // snaps the result panel away (Result.UpdateResult and Stats read the
    // sign as "hide now"). Word-granular negation in the original; our
    // resultTimer slot is a signed dword — same numeric result for the
    // 30000/31000 magnitudes involved.
    private static void NegateResultPanelTimers()
    {
        int rt = Memory.ReadSignedDword(Memory.Addr.resultTimer);
        Memory.WriteDword(Memory.Addr.resultTimer, -rt);
        short tv = Memory.ReadSignedWord(Memory.Addr.timeVar);
        Memory.WriteWord(Memory.Addr.timeVar, (short)(-tv));
        short stt = Memory.ReadSignedWord(Memory.Addr.statsTimer);
        Memory.WriteWord(Memory.Addr.statsTimer, (short)(-stt));
    }

    // gameLoop.cpp:845-1108 — l_stoppage_event_triggered dispatcher.
    //
    // Entered when stoppageEventTimer hits zero (or is already zero). Two
    // layers:
    //   1. gameStatePl == 101 (ST_STOPPED): dispatch by gameState — result
    //      panels, half-time / full-time walks, kickoff restarts
    //      (gameLoop.cpp:862-1073), fall-through arms the break-camera ladder
    //      (cseg_73993, :1076-1108).
    //   2. otherwise: l_game_running — the breakCameraMode 0→8 ladder
    //      (gameLoop.cpp:1110-1846, DispatchBreakCameraMode below).
    //
    // Source: external/swos-port/src/game/gameLoop.cpp:845-1846.
    private static void DispatchStoppageEventTriggered(short gameStatePl)
    {
        // gameLoop.cpp:846 — `mov stoppageEventTimer, 0`. Clear before any
        // state-machine work so callers can re-arm it.
        Memory.WriteWord(Memory.Addr.stoppageEventTimer, 0);

        // gameLoop.cpp:847-849 — A6 = lastTeamPlayedBeforeBreak; A5 = offset
        // ballSprite. Register loads consumed inside the ladder — the C# modes
        // re-read them where needed.

        // gameLoop.cpp:851-860 — `cmp gameStatePl, 101 (ST_STOPPED); jnz l_game_running`.
        if (gameStatePl != 101)
        {
            DispatchBreakCameraMode();
            return;
        }

        short gs = Memory.ReadSignedWord(Memory.Addr.gameState);

        if (gs == 25) // ST_RESULT_ON_HALFTIME
        {
            // gameLoop.cpp:862-893 — neg resultTimer / timeVar / statsTimer,
            // then SetCameraMovingToShowerState (half-time result dwell over;
            // swap sides + walk to showers; gameState → 22).
            NegateResultPanelTimers();
            SetCameraMovingToShowerState();
            return;
        }

        if (gs == 26) // ST_RESULT_AFTER_THE_GAME
        {
            // gameLoop.cpp:895-927 — neg timers; playGame = 0 (match over —
            // the outer loop exits; Main.SyncMatchFromSwosPort watches this).
            NegateResultPanelTimers();
            Memory.WriteWord(Memory.Addr.playGame, 0);
            return;
        }

        if (gs == 21) // ST_STARTING_GAME
        {
            // gameLoop.cpp:929-943 — l_not_showing_end_game_result →
            // prepareForInitialKick (first kickoff after the fans intro).
            Kickoff.PrepareForInitialKick();
            return;
        }

        if (gs == 22) // ST_CAMERA_GOING_TO_SHOWERS
        {
            // gameLoop.cpp:945-959 — prepareForInitialKick (second-half /
            // post-shower kickoff; teamStarting was already swapped by
            // SetCameraMovingToShowerState).
            Kickoff.PrepareForInitialKick();
            return;
        }

        if (gs == 29) // ST_FIRST_HALF_ENDED
        {
            // gameLoop.cpp:961-975 — firstHalfJustEnded (gameState → 23,
            // stoppageEventTimer = 275).
            FirstHalfJustEnded();
            return;
        }

        if (gs == 23) // ST_GOING_TO_HALFTIME
        {
            // gameLoop.cpp:977-1009 — neg resultTimer / timeVar / statsTimer,
            // then goToHalftime (gameState → 25, result panel seeded,
            // stoppageEventTimer = 770).
            NegateResultPanelTimers();
            GoToHalftime();
            return;
        }

        if (gs == 30) // ST_GAME_ENDED
        {
            // gameLoop.cpp:1011-1025 — playersLeavingPitch (gameState → 24).
            PlayersLeavingPitch();
            return;
        }

        if (gs == 24) // ST_PLAYERS_GOING_TO_SHOWER
        {
            // gameLoop.cpp:1027-1041 — SWOS::GameOver (gameState → 26, result
            // panel + camera anchor, stoppageEventTimer = 1650).
            GameOver();
            return;
        }

        if (gs == 27) // ST_FIRST_EXTRA_STARTING
        {
            // gameLoop.cpp:1043-1057 — prepareForInitialKick (ET-1 kickoff).
            Kickoff.PrepareForInitialKick();
            return;
        }

        if (gs == 28) // ST_FIRST_EXTRA_ENDED
        {
            // gameLoop.cpp:1059-1073 — prepareForInitialKick (ET-2 kickoff).
            Kickoff.PrepareForInitialKick();
            return;
        }

        // l_first_extra_not_ended — gameLoop.cpp:1075-1108. Plain stoppage
        // (kickoff walk gameState=0, keeper-hold 3, corner/foul/free-kick/
        // throw-in/penalty states): copy gameState → gameStatePl and arm the
        // break-camera ladder at mode 0.
        //
        // gameLoop.cpp:1076-1077 — `mov ax, gameState; mov gameStatePl, ax`.
        Memory.WriteWord(Memory.Addr.gameStatePl, gs);

        // gameLoop.cpp:1078-1104 — keeper-holds special: if gameState ==
        // ST_KEEPER_HOLDS_BALL (3) AND !g_inSubstitutesMenu AND
        // !g_cameraLeavingSubsTimer → timeVar = 31000 (re-arms the clock
        // panel display during the hold).
        if (gs == 3
            && Memory.ReadSignedWord(Memory.Addr.g_inSubstitutesMenu) == 0
            && Memory.ReadSignedWord(Memory.Addr.g_cameraLeavingSubsTimer) == 0)
        {
            Memory.WriteWord(Memory.Addr.timeVar, 31000);
        }

        // cseg_73993 — gameLoop.cpp:1106-1108 — `mov breakCameraMode, 0`.
        Memory.WriteWord(Memory.Addr.breakCameraMode, 0);
    }

    // gameLoop.cpp:1110-1846 — l_game_running. Branches on breakCameraMode (D7).
    // Ladder progression: 0 → 1 → 2 → 4 → 3 → 5 → 6 → 7 → 8 (terminal).
    private static void DispatchBreakCameraMode()
    {
        // gameLoop.cpp:1111-1112 — D7 = breakCameraMode (read before guards).
        short mode = Memory.ReadSignedWord(Memory.Addr.breakCameraMode);

        // gameLoop.cpp:1113-1118 — guard: g_inSubstitutesMenu != 0 → return.
        if (Memory.ReadSignedWord(Memory.Addr.g_inSubstitutesMenu) != 0) return;
        // gameLoop.cpp:1120-1125 — guard: g_waitForPlayerToGoInTimer != 0 → return.
        if (Memory.ReadSignedWord(Memory.Addr.g_waitForPlayerToGoInTimer) != 0) return;

        short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);

        if (mode == 0) { Mode0(gameState); return; }  // gameLoop.cpp:1127-1175
        if (mode == 1) { Mode1(gameState); return; }  // gameLoop.cpp:1177-1344
        if (mode == 2) { Mode2(gameState); return; }  // gameLoop.cpp:1346-1470
        if (mode == 4) { Mode4(gameState); return; }  // gameLoop.cpp:1472-1492
        if (mode == 3) { Mode3(gameState); return; }  // gameLoop.cpp:1494-1640
        if (mode == 5) { Mode5(gameState); return; }  // gameLoop.cpp:1642-1657
        if (mode == 6) { Mode6(gameState); return; }  // gameLoop.cpp:1659-1688
        if (mode == 7) { Mode7(gameState); return; }  // gameLoop.cpp:1690-1809
        if (mode == 8) { Mode8(gameState); return; }  // gameLoop.cpp:1811-1846

        // l_assert_failed — gameLoop.cpp:1848-1852: `int 3` + endless loop.
        // Port-side: unknown mode is a no-op (a -1 mode can transiently be
        // observed between a state setter writing breakCameraMode=-1 and the
        // next dispatcher pass copying gameState → gameStatePl).
    }

    // gameLoop.cpp:1127-1175 — breakCameraMode == 0 branch.
    // Waits until the ball has stopped (deltaX == 0 && deltaY == 0), then
    // transitions to mode 1. For non-keeper stoppages, also arms
    // stoppageEventTimer with m_goalCameraInterval (55, gameLoop.cpp:53 — or
    // 75 when goalCameraMode is active after a goal, gameLoop.cpp:1174).
    private static void Mode0(short gameState)
    {
        // gameLoop.cpp:1138-1144 — `mov eax, [esi+Sprite.deltaX]; or eax,eax; jnz @@out`.
        // A5 is the ball sprite (`ballSprite`) in the original. Read raw dword.
        int dx = Memory.ReadSignedDword(OpenSwos.SwosVm.BallSprite.Base + OpenSwos.SwosVm.BallSprite.OffDeltaX);
        if (dx != 0) return;
        int dy = Memory.ReadSignedDword(OpenSwos.SwosVm.BallSprite.Base + OpenSwos.SwosVm.BallSprite.OffDeltaY);
        if (dy != 0) return;

        // gameLoop.cpp:1153 — `mov breakCameraMode, 1`.
        Memory.WriteWord(Memory.Addr.breakCameraMode, 1);

        // gameLoop.cpp:1154-1164 — if gameState == ST_KEEPER_HOLDS_BALL (3) return.
        // Skip arming stoppageEventTimer; the keeper-holds path lets the next
        // tick flow into Mode1 immediately (stoppageEventTimer stays at 0,
        // which we treat as "re-fire dispatcher" via the `evt == 0` re-trigger
        // path above).
        if (gameState == 3) return;

        // gameLoop.cpp:1166 — `mov stoppageEventTimer, m_goalCameraInterval`.
        short interval = Memory.ReadSignedWord(Memory.Addr.m_goalCameraInterval);
        Memory.WriteWord(Memory.Addr.stoppageEventTimer, interval);
        // gameLoop.cpp:1167-1175 — `if (goalCameraMode != 0) stoppageEventTimer = 75`.
        short goalCameraMode = Memory.ReadSignedWord(Memory.Addr.goalCameraMode);
        if (goalCameraMode != 0)
            Memory.WriteWord(Memory.Addr.stoppageEventTimer, 75);
    }

    // gameLoop.cpp:1177-1344 — breakCameraMode == 1 branch.
    // Decides whether to enqueue a replay (auto-replay path), then teleports
    // the ball to the foul coordinates and advances to mode 2. The replay
    // subsystem isn't ported; the flag writes ARE (write-only for now).
    private static void Mode1(short gameState)
    {
        // gameLoop.cpp:1189-1254 — the asm compares gameState against
        // ST_PENALTY (14), ST_PENALTIES (31), ST_KEEPER_HOLDS_BALL (3),
        // ST_FOUL (13), ST_FREE_KICK_LEFT1 (6) .. ST_FREE_KICK_RIGHT3 (12)
        // with `jz/jb @@game_stopped` on each — but the FINAL comparison at
        // :1249-1254 (`cmp gameState, 12`) has NO conditional jump and falls
        // straight through into l_game_stopped. Net effect: EVERY gameState
        // reaches l_game_stopped; the comparisons are dead flag updates.
        // Preserved here as this comment for asm parity (no code).

        // l_game_stopped — gameLoop.cpp:1256-1262 —
        // `if (playingPenalties) goto l_no_auto_replay`.
        if (Memory.ReadSignedWord(Memory.Addr.playingPenalties) != 0)
            goto l_no_auto_replay;

        // gameLoop.cpp:1264-1269 — `if (g_inSubstitutesMenu) goto l_no_auto_replay`.
        // (Already guarded at DispatchBreakCameraMode entry.)

        // gameLoop.cpp:1271-1276 — `if (goalCameraMode == 0) goto l_no_auto_replay`.
        // The replay machinery only runs after a goal; for normal stoppage
        // (no goal camera) we skip it and fall straight to the position-set.
        short goalCameraMode = Memory.ReadSignedWord(Memory.Addr.goalCameraMode);
        if (goalCameraMode == 0)
            goto l_no_auto_replay;

        // gameLoop.cpp:1278-1287 — `if (g_autoSaveHighlights) saveHighlightScene = 1`.
        if (Memory.ReadSignedWord(Memory.Addr.g_autoSaveHighlights) != 0)
            Memory.WriteWord(Memory.Addr.saveHighlightScene, 1);

        // gameLoop.cpp:1288 — enqueueCrowdChantsReload (audio; stubbed).
        Memory.WriteWord(Memory.Addr.loadCrowdChantSampleFlag, 1);

        // gameLoop.cpp:1289-1294 — `if (g_autoReplays == 0) goto l_no_auto_replay`.
        if (Memory.ReadSignedWord(Memory.Addr.g_autoReplays) == 0)
            goto l_no_auto_replay;

        // gameLoop.cpp:1296-1298 — fire the replay request.
        Memory.WriteWord(Memory.Addr.userRequestedReplay, 0);
        Memory.WriteWord(Memory.Addr.instantReplayFlag, 1);
        Memory.WriteWord(Memory.Addr.loadCrowdChantSampleFlag, 1);

    l_no_auto_replay:
        // gameLoop.cpp:1300-1344 — currentScorer = 0, then the position-set +
        // breakCameraMode = 2 transition.
        Memory.WriteDword(Memory.Addr.currentScorer, 0);

        // gameLoop.cpp:1302-1321 — playingPenalties != 0 && gameState != PENALTIES → return.
        short playingPenalties = Memory.ReadSignedWord(Memory.Addr.playingPenalties);
        if (playingPenalties != 0)
        {
            if (gameState != 31) // ST_PENALTIES
                return;
        }

        // gameLoop.cpp:1323-1341 — if gameState != ST_KEEPER_HOLDS_BALL:
        // D1 = foulXCoordinate; D2 = foulYCoordinate; setBallPosition(D1, D2).
        if (gameState != 3)
        {
            short foulX = Memory.ReadSignedWord(Memory.Addr.foulXCoordinate);
            short foulY = Memory.ReadSignedWord(Memory.Addr.foulYCoordinate);
            BallUpdate.SetBallPosition(foulX, foulY);
        }

        // l_keeper_holds_the_ball — gameLoop.cpp:1342-1344 — `mov breakCameraMode, 2`.
        Memory.WriteWord(Memory.Addr.breakCameraMode, 2);
    }

    // gameLoop.cpp:1346-1470 — breakCameraMode == 2 branch (cseg_73B28).
    // Gated on whichCard == 0 AND cameraCoordinatesValid != 0 (the latter is
    // re-asserted every frame by Camera.MoveCamera, mirroring pitch.cpp:145).
    // Marks every outfield sprite destReachedState = 1 (the "start walking to
    // your restart position" latch consumed by UpdatePlayers), keepers = 3 for
    // keeper-hold, then advances to mode 4.
    private static void Mode2(short gameState)
    {
        // gameLoop.cpp:1358-1363 — `if (whichCard != 0) return`. (Card animation
        // in progress; defer.)
        if (Memory.ReadSignedWord(Memory.Addr.whichCard) != 0) return;

        // gameLoop.cpp:1365-1370 — `if (cameraCoordinatesValid == 0) return`.
        if (Memory.ReadSignedWord(Memory.Addr.cameraCoordinatesValid) == 0) return;

        // gameLoop.cpp:1372 — `mov goalScored, 0` (clear pre-goal flag).
        Memory.WriteWord(Memory.Addr.goalScored, 0);

        // gameLoop.cpp:1373-1388 — `if (gameState == ST_KEEPER_HOLDS_BALL):
        //   gameStatePl = 102; inGameCounter = 0; breakState = gameState`.
        // Keeper-hold reaches ST_WAITING_ON_PLAYER already here (the
        // gameStatePl==102 accumulator branch then owns every subsequent tick,
        // so modes 3..8 never run for keeper-hold — play resumes when the
        // keeper releases the ball or the 825-tick fallback fires).
        if (gameState == 3) // ST_KEEPER_HOLDS_BALL
        {
            Memory.WriteWord(Memory.Addr.gameStatePl, 102);
            Memory.WriteWord(Memory.Addr.inGameCounter, 0);
            Memory.WriteWord(Memory.Addr.breakState, gameState);
        }

        // cseg_73B85 — gameLoop.cpp:1391-1419 — if topTeamData.resetControls
        // == 0: walk topTeamData.spritesTable (11 dword pointers; `mov D0, 10`
        // then `dec/jns` = 11 iterations) and set each
        // Sprite.destReachedState = 1.
        if (Memory.ReadSignedWord(TeamData.TopBase + TeamData.OffResetControls) == 0)
        {
            int table = Memory.ReadSignedDword(TeamData.TopBase + TeamData.OffPlayers);
            if (table != 0)
            {
                for (int i = 0; i <= 10; i++)
                {
                    int spriteAddr = Memory.ReadSignedDword(table + i * 4);
                    if (spriteAddr == 0) continue;   // port-side null guard
                    // gameLoop.cpp:1414 — `mov [esi+Sprite.destReachedState], 1`.
                    Memory.WriteWord(spriteAddr + PlayerSprite.OffDestReachedState, 1);
                }
            }
        }

        // cseg_73BCE — gameLoop.cpp:1422-1450 — same loop for bottomTeamData.
        if (Memory.ReadSignedWord(TeamData.BottomBase + TeamData.OffResetControls) == 0)
        {
            int table = Memory.ReadSignedDword(TeamData.BottomBase + TeamData.OffPlayers);
            if (table != 0)
            {
                for (int i = 0; i <= 10; i++)
                {
                    int spriteAddr = Memory.ReadSignedDword(table + i * 4);
                    if (spriteAddr == 0) continue;
                    // gameLoop.cpp:1445 — `mov [esi+Sprite.destReachedState], 1`.
                    Memory.WriteWord(spriteAddr + PlayerSprite.OffDestReachedState, 1);
                }
            }
        }

        // cseg_73C17 — gameLoop.cpp:1453-1466 — if gameState ==
        // ST_KEEPER_HOLDS_BALL: goalie1/goalie2 destReachedState = 3 (keepers
        // don't walk anywhere; they're already "arrived").
        if (gameState == 3)
        {
            Memory.WriteWord(PlayerSprite.Base(PlayerSprite.SlotGoalie1) + PlayerSprite.OffDestReachedState, 3);
            Memory.WriteWord(PlayerSprite.Base(PlayerSprite.SlotGoalie2) + PlayerSprite.OffDestReachedState, 3);
        }

        // cseg_73C33 — gameLoop.cpp:1468-1470 — `mov breakCameraMode, 4`.
        Memory.WriteWord(Memory.Addr.breakCameraMode, 4);
    }

    // gameLoop.cpp:1472-1492 — breakCameraMode == 4 branch (cseg_73C3D).
    // Bridge between the destReachedState=1 latch (mode 2) and the arrival
    // wait (mode 3). Only gate: substitutes menu must not be open.
    private static void Mode4(short gameState)
    {
        // gameLoop.cpp:1484-1489 — `if (g_inSubstitutesMenu) return`. (No subs
        // menu exists in our port yet — the flag stays 0.)
        if (Memory.ReadSignedWord(Memory.Addr.g_inSubstitutesMenu) != 0) return;

        // gameLoop.cpp:1491-1492 — `mov breakCameraMode, 3`.
        Memory.WriteWord(Memory.Addr.breakCameraMode, 3);
    }

    // gameLoop.cpp:1494-1640 — breakCameraMode == 3 branch (cseg_73C60).
    // Waits until ALL 22 player sprites have destReachedState == 3 ("arrived
    // at restart position" — the 1→2 transition is written by UpdatePlayers
    // (updatePlayers.cpp:8885-8900) and 2→3 by SpriteUpdate
    // (updateSprite.cpp:215-229, gated on breakCameraMode == 3)). Also gates
    // on the referee being idle and no permanent-injury stoppage.
    private static void Mode3(short gameState)
    {
        // gameLoop.cpp:1506-1511 — `if (refState != 0) return`.
        if (Memory.ReadSignedWord(Memory.Addr.refState) != 0) return;

        // gameLoop.cpp:1513-1518 — `if (injuriesForever != 0) return`.
        if (Memory.ReadSignedWord(Memory.Addr.injuriesForever) != 0) return;

        // gameLoop.cpp:1520-1596 — A0 = offset team1SpritesTable (330096 — a
        // contiguous 22-pointer table covering BOTH teams); D0 = 21; loop.
        // Our sprite pool exposes the same 22 sprites as slots 0..21
        // (PlayerSprite.Base) — iterate those directly.
        for (int slot = 0; slot < PlayerSprite.TotalSlots; slot++)
        {
            int spriteAddr = PlayerSprite.Base(slot);

            // gameLoop.cpp:1533-1567 — for gameState ∈ { ST_PLAYERS_TO_INITIAL_
            // POSITIONS (0), ST_PENALTY (14), ST_PENALTIES (31) } every sprite
            // must arrive; otherwise only sprites with onScreen != 0 are
            // checked (off-screen players are exempt from the walk).
            bool checkThisSprite =
                gameState == 0 || gameState == 14 || gameState == 31;
            if (!checkThisSprite)
            {
                // gameLoop.cpp:1569-1575 — `mov ax, [esi+Sprite.onScreen];
                // or ax, ax; jz @@check_if_next_player_arrived`.
                if (Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffOnScreen) == 0)
                    continue;
            }

            // l_check_if_player_arrived — gameLoop.cpp:1577-1589 —
            // `cmp [esi+Sprite.destReachedState], 3; jnz @@out`.
            //
            // CROSS-FILE CONTRACT: the 1 → 2 promotion is UpdatePlayers'
            // l_update_destination_reached_state (updatePlayers.cpp:8885-8900)
            // and the 2 → 3 promotion is SpriteUpdate's
            // updateAnimationTableAndDestinationReached (updateSprite.cpp:
            // 215-229, gated on breakCameraMode == 3). NOTE for the keeper:
            // in the original a stopped, non-controlled, non-pass-target
            // goalkeeper falls into the SAME not-controlled walk-back tail
            // via updatePlayers.cpp:1130-1141 (`cmp gameStatePl, 100 ; jnz
            // l_not_controlled_player` inside l_goalie_not_catching_the_ball)
            // — without that routing the keeper's destReachedState never
            // leaves 1 and this wait blocks forever.
            if (Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffDestReachedState) != 3)
                return;
        }

        // gameLoop.cpp:1598 — `mov runSlower, 0` (post-goal celebration slow-mo
        // ends once everyone is back in position).
        Memory.WriteWord(Memory.Addr.runSlower, 0);

        // gameLoop.cpp:1599-1636 — `if (goalCameraMode != 0)` the original
        // pushes all registers and calls ResetAnimatedPatternsForBothTeams —
        // which is COMMENTED OUT in the swos-port source (gameLoop.cpp:1621).
        // Preserved as this note; no observable side-effect.

        // cseg_73DCB — gameLoop.cpp:1638-1640 — `mov breakCameraMode, 5`.
        Memory.WriteWord(Memory.Addr.breakCameraMode, 5);
    }

    // gameLoop.cpp:1642-1657 — breakCameraMode == 5 branch (cseg_73DD5).
    // Clears the booking-card latch, then advances.
    private static void Mode5(short gameState)
    {
        // gameLoop.cpp:1654 — `mov whichCard, 0`.
        Memory.WriteWord(Memory.Addr.whichCard, 0);
        // gameLoop.cpp:1655 — `mov bookedPlayer, 0` (dword pointer).
        Memory.WriteDword(Memory.Addr.bookedPlayer, 0);
        // gameLoop.cpp:1656-1657 — `mov breakCameraMode, 6`.
        Memory.WriteWord(Memory.Addr.breakCameraMode, 6);
    }

    // gameLoop.cpp:1659-1688 — breakCameraMode == 6 branch (cseg_73DFC).
    // Zeroes the ball-out-of-game timer and re-arms the clock panel display
    // (timeVar) for every stoppage except keeper-hold.
    private static void Mode6(short gameState)
    {
        // gameLoop.cpp:1671 — `mov ballOutOfGameTimer, 0`.
        Memory.WriteWord(Memory.Addr.ballOutOfGameTimer, 0);

        // gameLoop.cpp:1672-1684 — `if (gameState != ST_KEEPER_HOLDS_BALL)
        // timeVar = 31000`.
        if (gameState != 3)
            Memory.WriteWord(Memory.Addr.timeVar, 31000);

        // cseg_73E22 — gameLoop.cpp:1686-1688 — `mov breakCameraMode, 7`.
        Memory.WriteWord(Memory.Addr.breakCameraMode, 7);
    }

    // gameLoop.cpp:1690-1809 — breakCameraMode == 7 branch (cseg_73E2C).
    // Marks the restarting team's ball as in-play, waits for that team to
    // have a controlledPlayer (the designated kicker), sets the kicker's
    // standing / about-to-throw-in animation, arms the score panel
    // (resultTimer = 31000), then advances to mode 8. If no controlled player
    // materialises within m_allowPlayerControlCameraInterval (550) ticks,
    // falls back to per-tick CheckIfGoalkeeperClaimedTheBall probing.
    private static void Mode7(short gameState)
    {
        // gameLoop.cpp:1702-1703 — A6 = lastTeamPlayedBeforeBreak;
        // `mov [esi+TeamGeneralInfo.ballInPlay], 1`.
        int a6 = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
        if (a6 == 0) return;   // port-side null guard (original would fault)
        Memory.WriteWord(a6 + TeamData.OffBallInPlay, 1);

        // gameLoop.cpp:1704-1711 — `add ballOutOfGameTimer, 1`.
        short boogTimer = Memory.ReadSignedWord(Memory.Addr.ballOutOfGameTimer);
        boogTimer = (short)(boogTimer + 1);
        Memory.WriteWord(Memory.Addr.ballOutOfGameTimer, boogTimer);

        // gameLoop.cpp:1712-1722 — `cmp ballOutOfGameTimer,
        // m_allowPlayerControlCameraInterval; jb cseg_73E8B`.
        short allowInterval = Memory.ReadSignedWord(Memory.Addr.m_allowPlayerControlCameraInterval);
        if ((ushort)boogTimer >= (ushort)allowInterval)
        {
            // gameLoop.cpp:1724-1751 — the timeout path bumps four debug
            // variables (deadCameraBreakVar01 @455952 ++, deadCameraBreakVar02
            // @455954 = gameStatePl, deadCameraBreakVar03 @455956 = gameState,
            // deadCameraBreakVar04 @455932 ++) — all write-only with zero
            // readers ("dead" per the symbol names); no Memory.Addr slots are
            // allocated for them, so the writes are elided — then calls
            // CheckIfGoalkeeperClaimedTheBall (game.cpp:1218) and returns
            // (stays in mode 7).
            Bench.CheckIfGoalkeeperClaimedTheBall();
            return;
        }

        // cseg_73E8B — gameLoop.cpp:1753-1760 — `mov eax,
        // [esi+TeamGeneralInfo.controlledPlayer]; or eax, eax; jz @@out`.
        // WAIT until the restarting team has a controlled player.
        int controlled = Memory.ReadSignedDword(a6 + TeamData.OffControlledPlayer);
        if (controlled == 0)
            return;

        // gameLoop.cpp:1762-1783 — pick the kicker's animation table:
        // PL_THROW_IN (5) → aboutToThrowInAnimTable (453788), otherwise
        // playerNormalStandingAnimTable (453234). SetPlayerAnimationTable
        // (swos.asm:104309) ported in PlayerActions.
        byte playerState = Memory.ReadByte(controlled + PlayerSprite.OffPlayerState);
        if (playerState == 5) // PL_THROW_IN
        {
            // l_set_throw_in_anim_table — gameLoop.cpp:1781-1783.
            PlayerActions.SetPlayerAnimationTable(controlled, Memory.Addr.aboutToThrowInAnimTable);
        }
        else
        {
            // gameLoop.cpp:1777-1779.
            PlayerActions.SetPlayerAnimationTable(controlled, Memory.Addr.playerNormalStandingAnimTable);
        }

        // cseg_73ED6 — gameLoop.cpp:1785-1787 —
        // `mov [esi+TeamGeneralInfo.ballOutOfPlay], 1`.
        Memory.WriteWord(a6 + TeamData.OffBallOutOfPlay, 1);

        // gameLoop.cpp:1788 — `mov breakCameraMode, 8`.
        Memory.WriteWord(Memory.Addr.breakCameraMode, 8);

        // gameLoop.cpp:1789-1799 — `if (gameState == ST_KEEPER_HOLDS_BALL)
        // return` (keeper-hold skips the score-panel re-arm).
        if (gameState == 3)
            return;

        // gameLoop.cpp:1801-1806 — `if (g_inSubstitutesMenu) return`.
        if (Memory.ReadSignedWord(Memory.Addr.g_inSubstitutesMenu) != 0)
            return;

        // gameLoop.cpp:1808 — `mov resultTimer, 31000` — arms the score panel
        // shown during the restart.
        Memory.WriteDword(Memory.Addr.resultTimer, 31000);
    }

    // gameLoop.cpp:1811-1846 — breakCameraMode == 8 branch (cseg_73F0A).
    // Terminal ladder state: whistle, gameStatePl = ST_WAITING_ON_PLAYER (102),
    // breakState snapshot. Play resumes when the waiting player kicks
    // (UpdatePlayers sets gameStatePl = 100) or the 825-tick fallback in
    // UpdateGameTimersAndCameraBreakMode fires.
    private static void Mode8(short gameState)
    {
        // gameLoop.cpp:1823 — `mov writeOnlyVar03, 0`.
        Memory.WriteWord(Memory.Addr.writeOnlyVar03, 0);

        // gameLoop.cpp:1824-1836 — `if (gameState != ST_KEEPER_HOLDS_BALL)
        // PlayRefereeWhistleSample()` — audio-only, stubbed.
        if (gameState != 3)
            StubPlayRefereeWhistleSample();

        // cseg_73F2C — gameLoop.cpp:1838-1843.
        Memory.WriteWord(Memory.Addr.goalCameraMode, 0);       // :1839
        Memory.WriteWord(Memory.Addr.gameStatePl, 102);        // :1840
        Memory.WriteWord(Memory.Addr.inGameCounter, 0);        // :1841
        Memory.WriteWord(Memory.Addr.breakState, gameState);   // :1842-1843
    }

    // sfx.cpp PlayRefereeWhistleSample — whistle before a restart (gameLoop.cpp:1824).
    private static void StubPlayRefereeWhistleSample() => OpenSwos.Audio.MatchAudio.PlayWhistle();

    // updatePlayers.cpp — main game engine update. The largest single function
    // in the port (~25 000+ LOC asm). Drives all AI, kick handling, pass
    // routing, slide tackles, header attempts, formation positioning, keeper
    // logic. Ported to OpenSwos.Sim.Port.UpdatePlayers.
    private static void UpdatePlayers(SelectedTeam team)
    {
        // updatePlayers.cpp:47 — entry point. SelectedTeam → teamIndex
        // mapping: Top=0, Bottom=1 (matches UpdatePlayers.Update sanity check).
        int teamIndex = team == SelectedTeam.Top ? 0 : 1;
        OpenSwos.Sim.Port.UpdatePlayers.Update(teamIndex);
    }

    // ---- Stubs ---------------------------------------------------------------
    // Each stub stands in for either an external dep (audio/render/input
    // owned by Godot) or a not-yet-ported callee. None throw — they're
    // no-ops so the orchestrator can wire up early.

    // Mechanical port of gameLoop.cpp:1903-1909 — loadCrowdChantSampleIfNeeded.
    // Original:
    //   if (swos.loadCrowdChantSampleFlag) {
    //       loadCrowdChantSample();
    //       swos.loadCrowdChantSampleFlag = 0;
    //   }
    // The producer of the flag is the auto-replay branch in gameLoop.cpp:1298
    // and chants.cpp:enqueueCrowdChantsReload() (after the SWOS bug fix). The
    // consumer (loadCrowdChantSample) is audio-bus-only — handled by Godot's
    // audio layer when wired. We still need to CLEAR the flag every tick the
    // gate fires so a one-shot remains one-shot; otherwise a producer
    // (e.g. a future replay port) would see the flag latched and re-enqueue
    // chants forever.
    // Source: external/swos-port/src/game/gameLoop.cpp:1903-1909
    private static void StubLoadCrowdChantSampleIfNeeded()
    {
        if (Memory.ReadWord(Memory.Addr.loadCrowdChantSampleFlag) != 0)
        {
            // gameLoop.cpp:1904 — loadCrowdChantSample(): recompute the result
            // chant from the current score. Reads sim state only (audio side).
            OpenSwos.Audio.MatchAudio.LoadCrowdChant();
            // flag clear matches the original "fire once" contract.
            Memory.WriteWord(Memory.Addr.loadCrowdChantSampleFlag, 0);
        }
    }

    // Mechanical port of swos.asm:18874-18889 — ReadTimerDelta.
    // Original (m68k/x86):
    //   D0 = currentGameTick - lastGameTick
    //   if (D0 == 0) D0 = 1     ; never zero
    //   lastFrameTicks = D0
    //   lastGameTick   = currentGameTick
    //
    // In the original, currentGameTick is bumped from the SDL timer thread
    // (timer.cpp:155, timerProc). Our port has no timer thread — UpdateTimers
    // bumps currentGameTick by lastFrameTicks AFTER this call (gameLoop.cpp:269
    // -> UpdateTimers above). Therefore at the call site currentGameTick still
    // holds the previous frame's value and the unguarded subtraction yields 0,
    // which the `add D0, 1` clamp lifts to 1. Net result: deterministic 1 every
    // tick. lastGameTick still must be refreshed so future readers see the
    // "previous-tick" baseline.
    // Source: external/swos-port/swos/swos.asm:18874-18889
    private static void ReadTimerDelta()
    {
        // swos.asm:18876-18879 — D0 = currentGameTick - lastGameTick (word math).
        ushort cur = Memory.ReadWord(Memory.Addr.currentGameTick);
        ushort last = Memory.ReadWord(Memory.Addr.lastGameTick);
        ushort delta = (ushort)(cur - last);
        // swos.asm:18880-18881 — `jnz @@difference_not_zero; add D0, 1`.
        if (delta == 0) delta = 1;
        // swos.asm:18884-18887 — write back.
        Memory.WriteWord(Memory.Addr.lastFrameTicks, (short)delta);
        Memory.WriteWord(Memory.Addr.lastGameTick, cur);
    }

    // Mechanical port of gameLoop.cpp:275-281 — handlePauseAndStats.
    // Original:
    //   static void handlePauseAndStats() {
    //       pausedLoop();
    //       if (statsEnqueued())
    //           showStatsLoop();
    //   }
    //
    // `pausedLoop` (gameLoop.cpp:1855-1885) and `showStatsLoop`
    // (gameLoop.cpp:1887-1900) are SDL-driven blocking loops that own the
    // outer frame while pause / stats is active. Godot's _PhysicsProcess
    // is frame-driven and never blocks, so we can't faithfully port the
    // while-loop body. The OBSERVABLE behaviour we need is "while paused,
    // CoreGameUpdate is skipped" — i.e. ball + players don't advance.
    //
    // We mirror the input flag (Memory.Addr.gl_isPaused) so a future input
    // bridge writes 1 when ESC is pressed and 0 on resume. The pause LOOP
    // ITSELF is the caller's job — see `Tick()` above (currently the gate
    // is in Main._PhysicsProcess via MatchPhase.Paused). The stats loop is
    // pure UI; the trigger flag stays as a no-op until a stats overlay is
    // wired through Memory.Addr.gl_statsEnqueued.
    //
    // For parity with the original we DO clear the statsEnqueued flag after
    // the (no-op) showStatsLoop so a producer that sets it sees the
    // "consumed" transition and doesn't latch forever.
    // Source: external/swos-port/src/game/gameLoop.cpp:275-281.
    private static void StubHandlePauseAndStats()
    {
        // gameLoop.cpp:277 — pausedLoop(). Pause-handling done by Main.cs at
        // the orchestrator level (it gates _PhysicsProcess). Nothing to do
        // inside the per-tick body; gl_isPaused is informational.
        // gameLoop.cpp:279-280 — statsEnqueued() / showStatsLoop().
        short statsEnqueued = Memory.ReadSignedWord(Memory.Addr.gl_statsEnqueued);
        if (statsEnqueued != 0)
        {
            // showStatsLoop() is a blocking renderer loop; Godot draws the
            // stats overlay from C# side. Consume the flag for parity (so a
            // future trigger is one-shot — matches the original which exits
            // showStatsLoop when isAnyPlayerFiring sets fireBlocked at
            // gameLoop.cpp:1897, then hideStats() clears the queue).
            Memory.WriteWord(Memory.Addr.gl_statsEnqueued, 0);
        }
    }

    // Mechanical port of gameLoop.cpp:283-287 — handleKeys.
    // Original:
    //   static void handleKeys() {
    //       processControlEvents();
    //       checkGameKeys();
    //   }
    //
    // `processControlEvents` is SDL's "pump the event queue" — Godot's
    // _Input + _PhysicsProcess pipeline already does this. `checkGameKeys`
    // (gameLoop.cpp ~1820) checks global game-control keys (F-keys,
    // screenshot, replay-save, etc). The user-facing bindings are handled
    // by Main.cs via Godot's Input API. The ONLY side-effect that affects
    // the sim from this path is the `m_fadeAndInstantReplay` /
    // `m_fadeAndSaveReplay` flag setters; those are still no-ops in our
    // port (see InputControls.RequestFadeAndInstantReplay below) but the
    // flag-storage is now wired into Memory so when the replay port lands
    // the producer side already works.
    // Source: external/swos-port/src/game/gameLoop.cpp:283-287.
    private static void StubHandleKeys()
    {
        // gameLoop.cpp:285 — processControlEvents(): SDL_PumpEvents +
        // dispatch. Godot owns input pumping; the per-frame
        // ic_pl{1,2}Events memory slot is refreshed by
        // Main.cs:TickSwosPort before GameLoop.Tick fires, so by the time
        // we land here every event consumer already has the right state.
        //
        // gameLoop.cpp:286 — checkGameKeys(): F-key shortcuts +
        // replay/zoom/screenshot. These are Godot-bound; Main.cs handles
        // them out-of-band. Nothing per-tick to do here.
    }

    // (moveCamera wire-through inlined at the CoreGameUpdate call site —
    //  Camera.MoveCamera directly.)

    // Mechanical port of comments.cpp:180-210 — playEnqueuedSamples.
    //
    // Original drives six audio-sample timers (yellow card, red card, good
    // pass, throw-in, corner, substitute, tactics change) plus the
    // goalCounter celebration decrement plus a playCrowdChants() call.
    //
    // We do NOT have an audio bus wired yet, so the six sample timers are
    // intentionally omitted: their gameplay impact is zero (they only gate
    // an `Mix_PlayChannel` call). They will land alongside the audio
    // wiring port. Public producer-side setters (enqueueYellowCardSample
    // etc.) stay where they are in Referee.cs — they write Memory state
    // that future audio code reads.
    //
    // The TWO real side-effects that DO matter, ported here:
    //   1. goalCounter-- while > 0  (comments.cpp:206-207) — the
    //      "celebrate then restart" countdown. Original calls this from
    //      audio.cpp every tick, so it decrements unconditionally — NOT
    //      gated on gameState == 0 the way UpdateGoals.UpdatePostGoalRestart
    //      historically did. Restoring that contract here means the
    //      counter ticks down at the canonical position in the frame so
    //      anything reading `goalCounter` (GameTime.cs:779, GameTime.cs:820)
    //      sees the post-decrement value, matching the original.
    //   2. playCrowdChants() (comments.cpp:209, chants.cpp). Pure audio —
    //      omitted until chants port lands.
    //
    // Source: external/swos-port/src/audio/comments.cpp:180-210
    //         (in particular line 206-207 for the goalCounter decrement).
    private static void PlayEnqueuedSamples()
    {
        // Audio sample timer countdowns at comments.cpp:182-203 — intentionally
        // omitted (no audio bus). See header comment above for justification.

        // comments.cpp:206-207 — "strange place to decrement this..." per the
        // original author. Decrements goalCounter every tick while it's > 0,
        // regardless of gameState. This is the canonical celebration timer
        // countdown after a goal — read by UpdateGoals.UpdatePostGoalRestart
        // when gameState=0 to trigger the restart, and by GameTime.cs to gate
        // the half-end / period-end logic on `goalCounter == 0`.
        short goalCounter = Memory.ReadSignedWord(Memory.Addr.goalCounter);
        if (goalCounter != 0)
        {
            Memory.WriteWord(Memory.Addr.goalCounter, (short)(goalCounter - 1));
        }

        // comments.cpp:180-209 — drain the enqueued commentary timers + advance
        // the crowd-chant state machine (playCrowdChants). Audio-only, no-op
        // headless (MatchAudio.Instance is null). Uses its own RNG so the
        // lockstep sim RNG stream is untouched.
        OpenSwos.Audio.MatchAudio.Tick();
    }

    // Mechanical port of gameLoop.cpp:1911-1915 — initGoalSprites.
    // Original:
    //   static void initGoalSprites() {
    //       swos.goal1TopSprite.setImage(kTopGoalSprite);
    //       swos.goal2BottomSprite.setImage(kBottomGoalSprite);
    //   }
    // setImage just assigns sprite->imageIndex. We mirror that into the
    // memory slots so any downstream sprite-list consumer (renderer dirty
    // tracker, debug dumper) sees the bound image-id without per-tick
    // poking from Godot. kTopGoalSprite=1205, kBottomGoalSprite=1206
    // come from external/swos-port/src/sprites/sprites.h:67-68.
    // Source: external/swos-port/src/game/gameLoop.cpp:1911-1915
    //         external/swos-port/src/sprites/sprites.h:67-68
    private const int kTopGoalSprite    = 1205;
    private const int kBottomGoalSprite = 1206;
    private static void InitGoalSprites()
    {
        Memory.WriteWord(Memory.Addr.goal1TopSprite_ImageIndex,    (short)kTopGoalSprite);
        Memory.WriteWord(Memory.Addr.goal2BottomSprite_ImageIndex, (short)kBottomGoalSprite);
    }

    // Mechanical port of swos.asm:110867-110991 — DoGoalkeeperSprites.
    // (The function lives in swos-04.obj only; no C++ in swos-port repo —
    //  the asm is the canonical source.)
    //
    // Drives ballSprite.x/y/z + speed/deltaZ during a keeper dive animation.
    // While a keeper is diving, the ball is "pinned" to the keeper's hands
    // each frame so the catch animation reads correctly. Two branches:
    //   - Diving-right: ball.z indexed off dseg_17DEF4 by (imageIndex - 971)
    //     or (imageIndex - 1029) for goalie1, or (- 1087) / (- 1145) for
    //     goalie2; clamped to <28; doubled before indexing the word table.
    //   - Diving-left:  ball.z indexed off kGoalKeeperClaimingBallHeight by
    //     sprite.frameSwitchCounter * 2.
    //
    // Without this call, a diving keeper's animation plays out but the ball
    // sits wherever it was the moment the dive triggered; the catch never
    // visually connects and downstream "keeper claimed ball" logic that
    // gates on ball-near-keeper-Z still works (it reads keeper state, not
    // ball Z), but a future replay/screenshot port would render the ball
    // floating mid-pitch.
    //
    // Source: external/swos-port/swos/swos.asm:110867-110991 (DoGoalkeeperSprites)
    //         external/swos-port/swos/swos.asm:245601-245604 (data tables)
    private static void DoGoalkeeperSprites()
    {
        // asm:110868-110885 — pick which keeper is diving, in which direction.
        // Walks topTeamData first (right then left), then bottomTeamData
        // (right then left). Returns early if nobody is diving.
        bool topRight    = Memory.ReadSignedWord(TeamData.TopBase    + TeamData.OffGoalkeeperDivingRight) != 0;
        bool topLeft     = Memory.ReadSignedWord(TeamData.TopBase    + TeamData.OffGoalkeeperDivingLeft)  != 0;
        bool bottomRight = Memory.ReadSignedWord(TeamData.BottomBase + TeamData.OffGoalkeeperDivingRight) != 0;
        bool bottomLeft  = Memory.ReadSignedWord(TeamData.BottomBase + TeamData.OffGoalkeeperDivingLeft)  != 0;

        // The asm's branch-order mirrors the four-way else-if below. d0Offset
        // is the +1/-1 Y-delta the asm computes (`mov word ptr D0, 1` /
        // `mov word ptr D0, -1`) before adding sprite.y.whole. It is tied to
        // which TeamData structure carries the dive flag (topTeamData → +1,
        // bottomTeamData → -1), i.e. to the physical end, NOT to the keeper's
        // identity.
        // a6TeamBase is the diving keeper's TeamData base.
        // diveRight tells us which sub-branch (right vs left) to take.
        // a5KeeperBase (the diving keeper's SPRITE) is resolved BELOW from the
        // team's teamNumber — see the bug-fix note.
        int a6TeamBase;
        short d0Offset;
        bool diveRight;

        if (topRight)
        {
            // asm:110889-110891 (@@goalie1_diving_right): D0 = 1.
            d0Offset = 1;
            a6TeamBase   = TeamData.TopBase;
            diveRight = true;
        }
        else if (topLeft)
        {
            // asm:110952-110954 (@@goalie1_diving_left): D0 = 1.
            d0Offset = 1;
            a6TeamBase   = TeamData.TopBase;
            diveRight = false;
        }
        else if (bottomRight)
        {
            // asm:110894-110896 (@@goalie2_diving_right): D0 = -1.
            d0Offset = -1;
            a6TeamBase   = TeamData.BottomBase;
            diveRight = true;
        }
        else if (bottomLeft)
        {
            // asm:110957-110958 (@@goalie2_diving_left): D0 = -1.
            d0Offset = -1;
            a6TeamBase   = TeamData.BottomBase;
            diveRight = false;
        }
        else
        {
            // asm:110886 — `retn` if nobody is diving.
            return;
        }

        // USER-VISIBLE BUG FIX (task #186, "ball darts toward the halfway line
        // when the keeper catches, then returns"):
        // The diving keeper's SPRITE (a5) is NOT determined by which physical
        // end (top/bottom) carries the dive flag — it is determined by the
        // diving team's teamNumber, exactly as the asm does at
        // swos.asm:110907-110921 (@@set_goalie2_picture_index) and
        // swos.asm:110970-110978 (@@diving_side_determined):
        //     mov  A5, offset goalie1Sprite
        //     cmp  [A6+TeamGeneralInfo.teamNumber], 1
        //     jz   @@team1            ; teamNumber == 1 → goalie1Sprite
        //     mov  A5, offset goalie2Sprite   ; else       → goalie2Sprite
        // goalie1Sprite is ALWAYS team-1's keeper and goalie2Sprite ALWAYS
        // team-2's keeper, for the whole match. The previous port hard-coded
        // top→goalie1 / bottom→goalie2, which is only true in the FIRST half.
        // After the half-time side swap, the bottom team is team 1 (its keeper
        // is goalie1Sprite), so the bottomLeft/bottomRight branch pinned the
        // ball to goalie2Sprite — the OPPOSITE keeper ~600 px up the pitch —
        // for the whole ~15-tick catch animation, then snapped it back on
        // claim completion. Selecting the sprite by teamNumber (below) is the
        // faithful asm behaviour and is identical to the old code in the first
        // half (topTeamData is team 1 there).
        // Source: external/swos-port/swos/swos.asm:110907-110921 / 110970-110978.
        short divingTeamNumber = Memory.ReadSignedWord(a6TeamBase + TeamData.OffTeamNumber);
        int a5KeeperBase = (divingTeamNumber == 1)
            ? PlayerSprite.Base(PlayerSprite.SlotGoalie1)
            : PlayerSprite.Base(PlayerSprite.SlotGoalie2);

        // Z-table lookup. Both branches share the same final "pin ball to
        // keeper" tail at @@team1 (asm:110923-110949) but pick the table
        // and table-key differently.
        short zPixels;

        if (diveRight)
        {
            // asm:110897-110922 — @@set_goalie2_picture_index path.
            //
            // The asm always loads goalie1Sprite into A5 first (line 110898),
            // sets D1 = 971 (the goalie1 base imageIndex). If the keeper
            // imageIndex is in [1029, 1057), D1 becomes 1029 (alt animation).
            // Then checks team.teamNumber — if not 1, switches A5 to
            // goalie2Sprite, D1 = 1087, and re-checks the [1145, 1173)
            // alt-anim window.
            //
            // The final lookup is dseg_17DEF4[(imageIndex - D1) << 1]
            // (or no shift if (imageIndex - D1) >= 28; asm:110941-110943
            // uses jb to skip the shift when the diff is >= 28). The
            // off-by-one of `jb short $+2` is asm-specific micro-opt;
            // semantically it just means "no shift if diff >= 28".
            //
            // Although the asm does the goalie1-base-load first then
            // overrides for goalie2, we've already picked a5KeeperBase
            // above. Re-derive D1 / table-key the same way using the
            // already-selected keeper sprite + team.
            short teamNumber = Memory.ReadSignedWord(a6TeamBase + TeamData.OffTeamNumber);
            short imageIndex = Memory.ReadSignedWord(a5KeeperBase + PlayerSprite.OffImageIndex);

            short d1Base;
            if (a5KeeperBase == PlayerSprite.Base(PlayerSprite.SlotGoalie1))
            {
                // asm:110898-110906 — goalie1: D1 starts at 971, may flip to 1029.
                d1Base = 971;
                if (imageIndex >= 1029 && imageIndex < 1057) d1Base = 1029;
            }
            else
            {
                // asm:110912-110921 — goalie2: D1 starts at 1087, may flip to 1145.
                d1Base = 1087;
                if (imageIndex >= 1145 && imageIndex < 1173) d1Base = 1145;
            }
            // teamNumber is read for asm parity (the original branch uses it
            // to pick the alt-anim window). Our redundant variable read
            // matches the original control flow; remove only when the
            // diff-by-diff equivalence proof is complete.
            _ = teamNumber;

            // asm:110941-110943 — `cmp word ptr D0, 28; jb short $+2;
            // shl word ptr D0, 1`. The `jb $+2` target IS the shl itself
            // (jump-to-next-instruction) — the guard is DEAD CODE and the
            // index is ALWAYS doubled. For diff >= 28 the doubled index reads
            // past dseg_17DEF4 into the adjacent kGoalKeeperClaimingBallHeight
            // words (contiguous at swos.asm:245601-245604). Our Memory map
            // separates the two tables, so route the overflow explicitly.
            // (2026-07-02: the previous "no shift when diff >= 28" reading of
            // the asm produced MISALIGNED odd-offset word reads — the bogus
            // ball Z = 2304/2816 px transients seen during dive-claims.)
            int diff = imageIndex - d1Base;
            int tableIndex = diff * 2;                   // shl word ptr D0, 1 (always)
            if (tableIndex < 0) tableIndex = 0;
            if (tableIndex <= 27 * 2)
            {
                zPixels = Memory.ReadSignedWord(Memory.Addr.dseg_17DEF4 + tableIndex);
            }
            else
            {
                int overIndex = tableIndex - 28 * 2;     // bytes past the 28-word table
                if (overIndex > 6 * 2) overIndex = 6 * 2;
                zPixels = Memory.ReadSignedWord(Memory.Addr.kGoalKeeperClaimingBallHeight + overIndex);
            }
        }
        else
        {
            // asm:110960-110988 — @@diving_side_determined path. Mirrors the
            // right-side pick but uses sprite.frameSwitchCounter * 2 as the
            // table-key into kGoalKeeperClaimingBallHeight (7 words).
            //
            // asm:110961-110967 — same goalie1 / goalie2 disambiguation,
            // result d1Base unused on this branch (kept for asm parity).
            // The actual lookup is on frameSwitchCounter (asm:110982-110988).
            short frameSwitchCounter = Memory.ReadSignedWord(a5KeeperBase + PlayerSprite.OffFrameSwitchCounter);
            int tableIndex = frameSwitchCounter * 2;     // shl word ptr D0, 1
            if (tableIndex < 0)        tableIndex = 0;
            if (tableIndex > 6 * 2)    tableIndex = 6 * 2;
            zPixels = Memory.ReadSignedWord(Memory.Addr.kGoalKeeperClaimingBallHeight + tableIndex);
        }

        // Common tail — pin ball to keeper. asm:110925-110948 / 110970-110989.
        //   ballSprite.x.whole = keeper.x.whole
        //   ballSprite.y.whole = keeper.y.whole + D0   (D0 = ±1 from above)
        //   ballSprite.z.whole = 0   then set to zPixels from table
        //   ballSprite.deltaZ  = 0
        //   ballSprite.speed   = 0
        short kx = Memory.ReadSignedWord(a5KeeperBase + PlayerSprite.OffX + 2);  // x.whole
        short ky = Memory.ReadSignedWord(a5KeeperBase + PlayerSprite.OffY + 2);  // y.whole
        Memory.WriteWord(BallSprite.Base + BallSprite.OffX + 2, kx);
        Memory.WriteWord(BallSprite.Base + BallSprite.OffY + 2, (short)(ky + d0Offset));
        // asm:110933-110935 — z=0, deltaZ=0, speed=0 BEFORE the final z write.
        Memory.WriteWord(BallSprite.Base + BallSprite.OffZ + 2, 0);
        Memory.WriteDword(BallSprite.Base + BallSprite.OffDeltaZ, 0);
        Memory.WriteWord(BallSprite.Base + BallSprite.OffSpeed, 0);
        // asm:110948 — final z write from the table lookup.
        Memory.WriteWord(BallSprite.Base + BallSprite.OffZ + 2, zPixels);
    }

    // Mechanical port of gameLoop.cpp:1919-2010 — markPlayer.
    // Positions the playerMarkSprite (controlled-player arrow) above whichever
    // sprite the team has marked via TeamGame.markedPlayer. The bit-16 of
    // currentGameTick chooses which team's marker to draw each frame — the
    // alternation lets both teams' markers be visible on alternate ticks
    // (a single sprite drawn 50%/50% on each side).
    //
    // The cpp source uses an asm-translated structure (writeMemory + Sprite
    // field offsets); we collapse it to native field accesses while keeping
    // the original control flow + cite trail per call.
    //
    // Read by the renderer only — no gameplay branch consumes the sprite's
    // image index or coordinates. The Memory slot reflection lets future
    // ports (replay, screenshot, debug overlay) see the same state.
    //
    // Source: external/swos-port/src/game/gameLoop.cpp:1919-2010.
    private const int kPlayerMarkSprite = 1204;   // sprites.h:66
    private static void MarkPlayer()
    {
        // gameLoop.cpp:1921-1933 — pick team based on bit 4 of currentGameTick
        // (`test byte ptr currentGameTick, 10h`). Zero bit → top team, else
        // bottom team.
        ushort tick = Memory.ReadWord(Memory.Addr.currentGameTick);
        int a1TeamBase = ((tick & 0x10) == 0)
            ? TeamData.TopBase
            : TeamData.BottomBase;

        // gameLoop.cpp:1934-1948 — playerMarkSprite.imageIndex = -1 (hidden);
        // read team.inGameTeamPtr → A2; read TeamGame.markedPlayer (word at +20).
        // If markedPlayer < 0 (sign bit), return early.
        Memory.WriteWord(Memory.Addr.playerMarkSprite_ImageIndex, -1);

        int inGameTeamPtr = Memory.ReadSignedDword(a1TeamBase + TeamData.OffInGameTeamPtr);
        if (inGameTeamPtr == 0) return;       // safety: TeamGame not wired yet
        const int kTeamGameOffMarkedPlayer = 20;
        short markedPlayer = Memory.ReadSignedWord(inGameTeamPtr + kTeamGameOffMarkedPlayer);
        if (markedPlayer < 0) return;          // gameLoop.cpp:1947 — `js @@out`

        // gameLoop.cpp:1949-1959 — `cmp D0, 11` ... `jnb @@out`.
        // markedPlayer is a 1-based slot in TeamGame.players[] (size 16).
        // Bail out if >= 11 — only outfielders 1..10 + keeper 0 are addressable
        // via the spritesTable (which has 11 entries).
        if (markedPlayer >= 11) return;

        // gameLoop.cpp:1961-1970 — A1 = team.spritesTable; D0 <<= 2;
        // A1 = spritesTable[markedPlayer] (dword pointer to PlayerSprite).
        int spritesTableAddr = Memory.ReadSignedDword(a1TeamBase + TeamData.OffPlayers);
        if (spritesTableAddr == 0) return;
        int targetSpriteAddr = Memory.ReadSignedDword(spritesTableAddr + markedPlayer * 4);
        if (targetSpriteAddr == 0) return;

        // gameLoop.cpp:1972-1983 — `cmp sprite.playerState, PL_NORMAL (0); jnz @@out`.
        // Don't draw the arrow if the marked player isn't in normal state
        // (Down=10, GoalieDiving=6/7 etc.).
        byte playerState = Memory.ReadByte(targetSpriteAddr + PlayerSprite.OffPlayerState);
        if (playerState != 0) return;

        // gameLoop.cpp:1985-2009 — playerMarkSprite.imageIndex = SPR_PLAYER_MARK
        // (1204); X = target.x.whole; Z = target.z.whole + 20; Y = target.y.whole.
        // (Z+20 is the arrow's float height above the player's head.)
        Memory.WriteWord(Memory.Addr.playerMarkSprite_ImageIndex, (short)kPlayerMarkSprite);
        short tx = Memory.ReadSignedWord(targetSpriteAddr + PlayerSprite.OffX + 2);
        short ty = Memory.ReadSignedWord(targetSpriteAddr + PlayerSprite.OffY + 2);
        short tz = Memory.ReadSignedWord(targetSpriteAddr + PlayerSprite.OffZ + 2);
        Memory.WriteWord(Memory.Addr.playerMarkSprite_XWhole, tx);
        Memory.WriteWord(Memory.Addr.playerMarkSprite_YWhole, ty);
        Memory.WriteWord(Memory.Addr.playerMarkSprite_ZWhole, (short)(tz + 20));
    }

    // (updateBookedPlayerNumberSprite + updateResult wire-throughs inlined
    //  at the CoreGameUpdate call sites — Referee.UpdateBookedPlayerNumberSprite
    //  + Result.UpdateResult directly.)

    // Mechanical port of pitch.cpp:351-357 — SWOS::DrawAnimatedPatterns.
    // Original:
    //   void SWOS::DrawAnimatedPatterns() {
    //       if (!replayingNow() && !swos.g_trainingGame && swos.showFansCounter)
    //           swos.showFansCounter--;
    //       //...
    //   }
    // The body is otherwise pure render (texture-batched animated pitch
    // patterns); the only gameplay-visible side-effect is the showFansCounter
    // decrement that Camera.MoveCamera reads at camera.cpp:99 to early-out
    // while the "fans entering the stadium" intro animation is playing.
    // Without this decrement, showFansCounter could in principle latch at a
    // non-zero value (set by future menu / replay logic) and freeze the
    // camera. Replay subsystem isn't ported, so replayingNow() is always
    // false in our port; g_trainingGame defaults to 0 (free play).
    // Source: external/swos-port/src/game/pitch/pitch.cpp:351-357
    private static void DrawAnimatedPatterns()
    {
        // replayingNow() — replay system not ported; defaults to false.
        // pitch.cpp:354 — gate on `!replayingNow() && !g_trainingGame && showFansCounter`.
        if (Memory.ReadSignedWord(Memory.Addr.g_trainingGame) != 0) return;
        short fansCounter = Memory.ReadSignedWord(Memory.Addr.showFansCounter);
        if (fansCounter != 0)
        {
            Memory.WriteWord(Memory.Addr.showFansCounter, (short)(fansCounter - 1));
        }
    }

    // (updateBench + updateStatistics wire-throughs inlined at the
    //  CoreGameUpdate call sites — Bench.UpdateBench + Stats.UpdateStatistics
    //  directly.)

    // isAnyPlayerFiring — wired to InputControls.IsAnyPlayerFiring()
    // (gameControls.cpp:218-221).

    // ---- Half-end / ET-end / shower state transitions ----------------------
    // The four helpers below are mechanical ports of the small "set state"
    // helpers in gameLoop.cpp / game.cpp. They are called from the giant
    // `updateGameTimersAndCameraBreakMode` dispatcher (gameLoop.cpp:426-1300)
    // and from `nextPenalty` (game.cpp:828). That dispatcher is still stubbed
    // in `UpdateGameTimersAndCameraBreakMode` above, but having these
    // state-set helpers available as public statics means GameTime.cs (which
    // already drives EndFirstHalf / EndOfGame at the appropriate game-time
    // thresholds) and any future dispatcher port can call them mechanically.
    //
    // All four are pure scalar resets — write `gameState`, `gameStatePl`,
    // `breakCameraMode`, `cameraDirection`, `stoppageEventTimer`, clear
    // stoppage timers, clear camera velocities, point
    // `lastTeamPlayedBeforeBreak` at the top team, then `StopAllPlayers`.
    // No floats; word-grained writes only.

    // GameState enum values used by the half-end transitions. Read from the
    // inline `mov gameState, NN` comments in gameLoop.cpp:2050/2078/2100/2111
    // and game.cpp:711.
    private const short kStCameraGoingToShowers = 22;
    private const short kStGoingToHalftime      = 23;
    private const short kStPlayersGoingToShower = 24;
    private const short kStResultOnHalftime     = 25;
    private const short kStStopped              = 101;

    // Mechanical port of gameLoop.cpp:2012-2072 — setCameraMovingToShowerState.
    //
    // Triggered at end of extra-time second half (state machine in
    // updateGameTimersAndCameraBreakMode): swaps teamPlayingUp / teamStarting
    // (neg + 3 = 3-old so 1↔2), sets halfNumber=2, repositions the ball to
    // the (1672, 449) off-pitch shower-camera anchor, re-inits team data,
    // moves gameState into ST_CAMERA_GOING_TO_SHOWERS, and prepares the
    // post-game animation timers.
    //
    // Source: external/swos-port/src/game/gameLoop.cpp:2012.
    public static void SetCameraMovingToShowerState()
    {
        // gameLoop.cpp:2014 — mov halfNumber, 2.
        Memory.WriteWord(Memory.Addr.halfNumber, 2);

        // gameLoop.cpp:2015-2027 — neg teamPlayingUp ; add teamPlayingUp, 3.
        // (1 → -1 + 3 = 2, and 2 → -2 + 3 = 1.) Identical recipe in
        // GameTime.EndFirstExtraTime — preserved verbatim for parity.
        short tpu = Memory.ReadSignedWord(Memory.Addr.teamPlayingUp);
        Memory.WriteWord(Memory.Addr.teamPlayingUp, (short)(3 - tpu));
        // gameLoop.cpp:2028-2043 — neg teamStarting ; add teamStarting, 3.
        short ts = Memory.ReadSignedWord(Memory.Addr.teamStarting);
        Memory.WriteWord(Memory.Addr.teamStarting, (short)(3 - ts));

        // gameLoop.cpp:2044-2046 — setBallPosition(1672, 449). Off-pitch
        // shower-camera anchor X-coordinate.
        BallUpdate.SetBallPosition(1672, 449);
        // gameLoop.cpp:2047 — mov hideBall, 0.
        Memory.WriteWord(Memory.Addr.hideBall, 0);

        // gameLoop.cpp:2048 — call InitTeamsData. Two parts:
        //   (a) the game.cpp:396-421 scalar resets (subset below), and
        //   (b) the game.cpp:422-548 per-team pointer RESEAT / end-swap, ported
        //       in Kickoff.ReseatTeamsForNewHalf. Part (b) is the actual
        //       half-time end-swap (task #173 BUG2 / #148): with teamPlayingUp
        //       just flipped above, it reassigns which top/bottom TeamGeneralInfo
        //       owns which team's sprites/tactics/inGameTeamPtr/teamNumber/
        //       playerNumber — flipping ends, the goal each team attacks, and
        //       the human's controlled end. Without it both halves played the
        //       same way (user report). Source: game.cpp:422-548.
        Kickoff.ReseatTeamsForNewHalf();
        Memory.WriteDword(Memory.Addr.currentScorer, 0);
        Memory.WriteDword(Memory.Addr.lastPlayerBeforeGoalkeeper, 0);
        Memory.WriteWord(Memory.Addr.goalScored, 0);
        Memory.WriteWord(Memory.Addr.runSlower, 0);
        Memory.WriteWord(Memory.Addr.penalty, 0);

        // gameLoop.cpp:2049 — mov stoppageEventTimer, 110.
        Memory.WriteWord(Memory.Addr.stoppageEventTimer, 110);
        // gameLoop.cpp:2050 — mov gameState, ST_CAMERA_GOING_TO_SHOWERS (22).
        Memory.WriteWord(Memory.Addr.gameState, kStCameraGoingToShowers);
        // gameLoop.cpp:2051 — mov breakCameraMode, -1.
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        // gameLoop.cpp:2052 — mov gameStatePl, 101.
        Memory.WriteWord(Memory.Addr.gameStatePl, kStStopped);
        // gameLoop.cpp:2053 — mov gameNotInProgressCounterWriteOnly, 0.
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);
        // gameLoop.cpp:2054 — mov cameraDirection, -1.
        Memory.WriteWord(Memory.Addr.cameraDirection, -1);
        // gameLoop.cpp:2055 — mov lastTeamPlayedBeforeBreak, offset topTeamData.
        Memory.WriteDword(Memory.Addr.lastTeamPlayedBeforeBreak, TeamData.TopBase);
        // gameLoop.cpp:2056-2057 — clear stoppage timers.
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);
        // gameLoop.cpp:2058 — call stopAllPlayers().
        TeamPort.StopAllPlayers();
        // gameLoop.cpp:2059-2060 — zero camera velocities.
        Memory.WriteWord(Memory.Addr.cameraXVelocity, 0);
        Memory.WriteWord(Memory.Addr.cameraYVelocity, 0);

        // gameLoop.cpp:2061-2068 — `or ax, ax; jz cseg_75B53; mov nullSampleTimer, 385`.
        // nullSampleTimer is an audio-only field (not wired in our Memory.Addr
        // table). The gate is `playGame != 0`; the action is gated audio
        // setup — preserved as a no-op for parity.
        _ = Memory.ReadSignedWord(Memory.Addr.playGame);

        // gameLoop.cpp:2071 — jmp initPlayersBeforeEnteringPitch().
        // initPlayersBeforeEnteringPitch is the post-match leaving-pitch
        // formation loop; not yet ported. The state transition above is the
        // gameplay-visible part — gameState=22 lets the orchestrator (and
        // future dispatcher) move on.
    }

    // Mechanical port of gameLoop.cpp:2074-2090 — firstHalfJustEnded.
    //
    // Triggered the first frame the clock crosses 45:00. Sets the camera-
    // break-mode FSM into the half-time approach: gameState=23, stoppageEvent
    // timer = 275 ticks (~3.9s @ 70Hz before the result panel shows),
    // gameStatePl=101 (stopped), all stoppage / camera vars cleared,
    // stateGoal (goal-celebration state) reset to 0.
    //
    // Source: external/swos-port/src/game/gameLoop.cpp:2074.
    public static void FirstHalfJustEnded()
    {
        // OpenSWOS enhancement (not in the original): half-time recovery. The ~15 min
        // break restores a flat fraction of each player's lost energy — fires exactly
        // once here, the first frame the clock crosses 45:00. See PlayerEnergy.
        PlayerEnergy.RecoverAtHalfTime();

        // gameLoop.cpp:2076 — mov hideBall, 0.
        Memory.WriteWord(Memory.Addr.hideBall, 0);
        // gameLoop.cpp:2077 — mov stoppageEventTimer, 275.
        Memory.WriteWord(Memory.Addr.stoppageEventTimer, 275);
        // gameLoop.cpp:2078 — mov gameState, ST_GOING_TO_HALFTIME (23).
        Memory.WriteWord(Memory.Addr.gameState, kStGoingToHalftime);
        // gameLoop.cpp:2079 — mov breakCameraMode, -1.
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        // gameLoop.cpp:2080 — mov gameStatePl, ST_STOPPED (101).
        Memory.WriteWord(Memory.Addr.gameStatePl, kStStopped);
        // gameLoop.cpp:2081 — mov gameNotInProgressCounterWriteOnly, 0.
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);
        // gameLoop.cpp:2082 — mov cameraDirection, -1.
        Memory.WriteWord(Memory.Addr.cameraDirection, -1);
        // gameLoop.cpp:2083 — mov lastTeamPlayedBeforeBreak, offset topTeamData.
        Memory.WriteDword(Memory.Addr.lastTeamPlayedBeforeBreak, TeamData.TopBase);
        // gameLoop.cpp:2084-2085 — clear stoppage timers.
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);
        // gameLoop.cpp:2086 — call stopAllPlayers().
        TeamPort.StopAllPlayers();
        // gameLoop.cpp:2087-2088 — zero camera velocities.
        Memory.WriteWord(Memory.Addr.cameraXVelocity, 0);
        Memory.WriteWord(Memory.Addr.cameraYVelocity, 0);
        // gameLoop.cpp:2089 — mov stateGoal, 0.
        Memory.WriteWord(Memory.Addr.stateGoal, 0);
    }

    // Mechanical port of gameLoop.cpp:2092-2111 — goToHalftime.
    //
    // Triggered after the FirstHalfJustEnded dwell expires. Ball moves to
    // (1672, 449) off-pitch (same shower anchor used by
    // SetCameraMovingToShowerState), result panel timers seeded
    // (resultTimer=30000 / timeVar=32000), gameState → ST_RESULT_ON_HALFTIME (25),
    // stoppageEventTimer=770 (~11s dwell while result is shown).
    //
    // Source: external/swos-port/src/game/gameLoop.cpp:2092.
    public static void GoToHalftime()
    {
        // gameLoop.cpp:2094-2096 — setBallPosition(1672, 449).
        BallUpdate.SetBallPosition(1672, 449);
        // gameLoop.cpp:2097 — mov resultTimer, 30000.
        Memory.WriteDword(Memory.Addr.resultTimer, 30000);
        // gameLoop.cpp:2098 — mov timeVar, 32000.
        Memory.WriteWord(Memory.Addr.timeVar, 32000);
        // gameLoop.cpp:2099 — mov stoppageEventTimer, 770.
        Memory.WriteWord(Memory.Addr.stoppageEventTimer, 770);
        // gameLoop.cpp:2100 — mov gameState, ST_RESULT_ON_HALFTIME (25).
        Memory.WriteWord(Memory.Addr.gameState, kStResultOnHalftime);
        // gameLoop.cpp:2101 — mov breakCameraMode, -1.
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        // gameLoop.cpp:2102 — mov gameStatePl, 101.
        Memory.WriteWord(Memory.Addr.gameStatePl, kStStopped);
        // gameLoop.cpp:2103 — mov gameNotInProgressCounterWriteOnly, 0.
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);
        // gameLoop.cpp:2104 — mov cameraDirection, -1.
        Memory.WriteWord(Memory.Addr.cameraDirection, -1);
        // gameLoop.cpp:2105 — mov lastTeamPlayedBeforeBreak, offset topTeamData.
        Memory.WriteDword(Memory.Addr.lastTeamPlayedBeforeBreak, TeamData.TopBase);
        // gameLoop.cpp:2106-2107 — clear stoppage timers.
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);
        // gameLoop.cpp:2108 — call stopAllPlayers().
        TeamPort.StopAllPlayers();
        // gameLoop.cpp:2109-2110 — zero camera velocities.
        Memory.WriteWord(Memory.Addr.cameraXVelocity, 0);
        Memory.WriteWord(Memory.Addr.cameraYVelocity, 0);
    }

    // Mechanical port of game.cpp:707-723 — playersLeavingPitch.
    //
    // Triggered when the penalty shootout decides the match (NextPenalty's
    // l_finish_penalties branch) AND when the result panel dwell ends after
    // full-time. Drives the post-match "players walking off" sequence.
    // Mirrors firstHalfJustEnded structurally but sets gameState=24
    // (ST_PLAYERS_GOING_TO_SHOWER) and clears stateGoal.
    //
    // Source: external/swos-port/src/game/game.cpp:707.
    public static void PlayersLeavingPitch()
    {
        // game.cpp:709 — mov hideBall, 0.
        Memory.WriteWord(Memory.Addr.hideBall, 0);
        // game.cpp:710 — mov stoppageEventTimer, 275.
        Memory.WriteWord(Memory.Addr.stoppageEventTimer, 275);
        // game.cpp:711 — mov gameState, ST_PLAYERS_GOING_TO_SHOWER (24).
        Memory.WriteWord(Memory.Addr.gameState, kStPlayersGoingToShower);
        // game.cpp:712 — mov breakCameraMode, -1.
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        // game.cpp:713 — mov gameStatePl, ST_STOPPED (101).
        Memory.WriteWord(Memory.Addr.gameStatePl, kStStopped);
        // game.cpp:714 — mov gameNotInProgressCounterWriteOnly, 0.
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);
        // game.cpp:715 — mov cameraDirection, -1.
        Memory.WriteWord(Memory.Addr.cameraDirection, -1);
        // game.cpp:716 — mov lastTeamPlayedBeforeBreak, offset topTeamData.
        Memory.WriteDword(Memory.Addr.lastTeamPlayedBeforeBreak, TeamData.TopBase);
        // game.cpp:717-718 — clear stoppage timers.
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);
        // game.cpp:719 — call stopAllPlayers().
        TeamPort.StopAllPlayers();
        // game.cpp:720-721 — zero camera velocities.
        Memory.WriteWord(Memory.Addr.cameraXVelocity, 0);
        Memory.WriteWord(Memory.Addr.cameraYVelocity, 0);
        // game.cpp:722 — mov stateGoal, 0.
        Memory.WriteWord(Memory.Addr.stateGoal, 0);
    }

    // (TickHalftimeCeremony — the port-only three-stage half-time collapse —
    //  was DELETED 2026-07-02: the faithful l_stoppage_event_triggered
    //  dispatcher above now drives 29 → 23 → 25 → 22 → prepareForInitialKick
    //  exactly like gameLoop.cpp:961-1009 / 862-893 / 945-959.)

    // ---- End-of-match transition --------------------------------------------
    // gameOver(), isMatchRunning(), and the four FSM-interval setters live
    // here. None of them call into the render/audio/replay layers — the
    // gameOver() port skips the fadeOut/drawPitch/playEndGameCrowdSampleAndComment
    // calls and keeps only the scalar state writes. Same rationale as the
    // half-end / shower transitions above.

    // gameLoop.cpp:40-41 — `constexpr FixedPoint kGameEndCameraX = 176;`
    // and `kGameEndCameraY = 80`. End-of-match camera anchor.
    private const short kGameEndCameraX = 176;
    private const short kGameEndCameraY = 80;

    // gameLoop.cpp:363 — `constexpr int kBallOffCourtX = 1672;`. The ball
    // gets repositioned off-pitch on game-over. Same X used by the half-end /
    // shower-camera setters above (gameLoop.cpp:2044 / 2094).
    private const short kBallOffCourtX = 1672;

    // pitchConstants.h:4 — `constexpr int kPitchCenterY = 449;`. Y-coord
    // of the pitch centre line; gameOver pairs kBallOffCourtX with this Y.
    private const short kPitchCenterY = 449;

    // gameLoop.cpp:25 — `GameState::kResultAfterTheGame = 26`. swos.h:587.
    private const short kStResultAfterTheGame = 26;

    // Mechanical port of gameLoop.cpp:361-395 — gameOver().
    //
    // Triggered from the break-camera FSM when gameState == ST_PLAYERS_GOING_TO_SHOWER
    // (gameLoop.cpp:1037-1041 → SWOS::GameOver). Sets up the post-match
    // result-panel state: camera fixed at (176, 80), ball off-pitch at
    // (1672, 449), resultTimer = 30000 (dwell), stoppageEventTimer = 1650,
    // gameState = kResultAfterTheGame (26), gameStatePl = kStopped (101),
    // breakCameraMode = -1, cameraDirection = -1, lastTeamPlayedBeforeBreak
    // → topTeamData. Stoppage timers cleared. Camera velocities zeroed. All
    // players stopped. m_doFadeIn flag set so the next outer-loop tick fades
    // back in to the result panel.
    //
    // SKIPPED here (delegated to Godot host or future audio/render port):
    //   - gameLoop.cpp:365  fadeOut() — render layer.
    //   - gameLoop.cpp:374  drawPitchAtCurrentCamera() — render.
    //   - gameLoop.cpp:392  playEndGameCrowdSampleAndComment() — audio.
    //   - gameLoop.cpp:394  m_doFadeIn = true — outer-loop fade tracking
    //     (we have no outer loop; the host owns fade-in).
    //
    // Source: external/swos-port/src/game/gameLoop.cpp:361.
    public static void GameOver()
    {
        // gameLoop.cpp:365 — gameFadeOut(): render-only (fade calls drawFrame
        // in a loop). Skipped — Godot owns the screen.

        // gameLoop.cpp:368-372 — setCameraX/Y(kGameEndCameraX/Y). Whole-pixel
        // value goes into the high word of the Q16.16 camera position; we
        // preserve the low-word fraction the same way Camera.SetCameraX does
        // by shifting the whole-pixel constant into Q16.16 space. The original
        // SWOS_TEST branch retains the previous fraction, NON-TEST overwrites
        // it (gameLoop.cpp:371-372). We match the non-test path.
        Camera.SetCameraX(kGameEndCameraX << 16);
        Camera.SetCameraY(kGameEndCameraY << 16);

        // gameLoop.cpp:374 — drawPitchAtCurrentCamera(): render-only.

        // gameLoop.cpp:375 — setBallPosition(kBallOffCourtX, kPitchCenterY).
        BallUpdate.SetBallPosition(kBallOffCourtX, kPitchCenterY);

        // gameLoop.cpp:377 — swos.resultTimer = 30'000. Signed dword in the
        // original (`swos.resultTimer` is `int32_t`); same width here.
        Memory.WriteDword(Memory.Addr.resultTimer, 30000);
        // gameLoop.cpp:378 — swos.stoppageEventTimer = 1'650.
        Memory.WriteWord(Memory.Addr.stoppageEventTimer, 1650);
        // gameLoop.cpp:379 — swos.gameState = kResultAfterTheGame (26).
        Memory.WriteWord(Memory.Addr.gameState, kStResultAfterTheGame);
        // gameLoop.cpp:380 — swos.breakCameraMode = -1.
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        // gameLoop.cpp:381 — swos.gameStatePl = kStopped (101).
        Memory.WriteWord(Memory.Addr.gameStatePl, kStStopped);
        // gameLoop.cpp:382 — swos.cameraDirection = -1.
        Memory.WriteWord(Memory.Addr.cameraDirection, -1);
        // gameLoop.cpp:383 — swos.lastTeamPlayedBeforeBreak = &swos.topTeamData.
        Memory.WriteDword(Memory.Addr.lastTeamPlayedBeforeBreak, TeamData.TopBase);
        // gameLoop.cpp:384-385 — clear stoppage timers.
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);

        // gameLoop.cpp:387 — stopAllPlayers().
        TeamPort.StopAllPlayers();

        // gameLoop.cpp:389-390 — zero camera velocities.
        Memory.WriteWord(Memory.Addr.cameraXVelocity, 0);
        Memory.WriteWord(Memory.Addr.cameraYVelocity, 0);

        // gameLoop.cpp:392 — playEndGameCrowdSampleAndComment(): HOMEWINL /
        // BOOWHISL / CHEER + rout/sensational result comment (comments.cpp:110).
        OpenSwos.Audio.MatchAudio.PlayEndGameCrowd();

        // gameLoop.cpp:394 — m_doFadeIn = true. Tracked in the outer-loop
        // (gameLoop.cpp:110-116) which we don't have; Godot's screen-effects
        // pipeline owns post-match fade-in.
    }

    // Mechanical port of gameLoop.cpp:165-168 — isMatchRunning().
    //
    // Pure getter on the m_playingMatch file-static. Audio + UI + replay
    // layers gate on this to decide whether a match is currently live (e.g.
    // chants.cpp uses it to mute crowd noise outside of matches; the
    // replay-exit menu uses it as a precondition for the "match in progress"
    // branch). Mirroring the flag into SwosVm memory means downstream ports
    // see the same gate semantics as the original.
    //
    // Source: external/swos-port/src/game/gameLoop.cpp:165-168.
    public static bool IsMatchRunning() =>
        Memory.ReadSignedWord(Memory.Addr.m_playingMatch) != 0;

    // Helper to set the flag (gameLoop.cpp:134 / 204). Not a direct port of
    // a named function — the original assigns `m_playingMatch = ...` inline
    // in initGameLoop / the outer-loop tail. Exposing this as a public setter
    // lets the orchestrator (Main.cs) or a future initGameLoop port flip the
    // flag at the right boundary without poking Memory.Addr directly.
    //
    // Source: external/swos-port/src/game/gameLoop.cpp:134,204.
    public static void SetMatchRunning(bool running) =>
        Memory.WriteWord(Memory.Addr.m_playingMatch, running ? 1 : 0);

    // Mechanical port of gameLoop.cpp:170-173 — setPenaltiesInterval().
    // Original: `static int m_penaltiesInterval = 110;` with a setter at line
    // 170-173. Used at gameLoop.cpp:460 to gate per-tick penalty advance.
    // Source: external/swos-port/src/game/gameLoop.cpp:170-173.
    public static void SetPenaltiesInterval(int interval) =>
        Memory.WriteWord(Memory.Addr.m_penaltiesInterval, interval);

    // Mechanical port of gameLoop.cpp:175-178 — setInitalKickInterval().
    // Setter for the m_initalKickInterval static (default 825; used at
    // gameLoop.cpp:551 to compare stoppageTimerActive against). Note SWOS's
    // typo "Inital" preserved for naming parity.
    // Source: external/swos-port/src/game/gameLoop.cpp:175-178.
    public static void SetInitalKickInterval(int interval) =>
        Memory.WriteWord(Memory.Addr.m_initalKickInterval, interval);

    // Mechanical port of gameLoop.cpp:180-183 — setGoalCameraInterval().
    // Setter for m_goalCameraInterval (default 50 PC; used at gameLoop.cpp:1166
    // as the duration the break-camera holds before mode 0 → 1 transition).
    // Source: external/swos-port/src/game/gameLoop.cpp:180-183.
    public static void SetGoalCameraInterval(int interval) =>
        Memory.WriteWord(Memory.Addr.m_goalCameraInterval, interval);

    // Mechanical port of gameLoop.cpp:185-188 — setAllowPlayerControlCameraInterval().
    // Setter for m_allowPlayerControlCameraInterval (default 75; used at
    // gameLoop.cpp:1715 as the ballOutOfGameTimer threshold in mode-7 to
    // fall through into mode 8).
    // Source: external/swos-port/src/game/gameLoop.cpp:185-188.
    public static void SetAllowPlayerControlCameraInterval(int interval) =>
        Memory.WriteWord(Memory.Addr.m_allowPlayerControlCameraInterval, interval);
}
