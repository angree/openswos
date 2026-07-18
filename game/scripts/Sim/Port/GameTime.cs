namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Match clock + half-time / full-time / extra-time / penalties transitions
// ported from external/swos-port/src/game/gameTime.cpp.
//
// The clock ticks in REAL FRAMES via `lastFrameTicks` (PC=70 ticks per game
// second, Amiga=49). Game-length setting picks `timeDelta`:
//   gameLengthInGame = 0 → 30 seconds (= 2 game-minutes per real-second × kGameLenSecondsTable[0])
//   1 → 18, 2 → 12, 3 → 9
//
// State machine per period (45/90/105/120 game-minutes):
//   - Once gameTimeInMinutes crosses the period boundary, set m_gameSeconds = -1
//     and m_endGameCounter = 50 (Amiga) or 55 (PC) ticks. This is the "last
//     minute prolong" — additional time before whistle if attack is in
//     progress or ball is in penalty area.
//   - During prolong, decrement endGameCounter each frame. When it hits 0, fire
//     the period end handler (endFirstHalf / endSecondHalf / endFirstExtraTime
//     / endSecondExtraTime).
//   - If prolongLastMinute() returns true (attack ongoing OR ball in penalty
//     area), reset the prolong timer (call setupLastMinuteSwitchNextFrame).
//
// Player tracking — `bumpPlayersLastPlayedHalfAtHalfStart` /
// `bumpPlayersLastPlayedHalfAtHalfEnd` walk the 11×2 PlayerInfo arrays of
// both teams updating each player's `halfPlayed` field.
public static class GameTime
{
    // gameTime.cpp:10-11 — time display position (menu sprite coords).
    public const int kTimeX = 20 - 6;
    public const int kTimeY = 9;

    // pitchConstants.h:3-4.
    public const int kPitchCenterY = 449;

    // Ticks per game-second (accumulator refill). The GENUINE PC SWS.EXE refills
    // secondsSwitchAccumulator with 54, NOT 70 — see swos.asm:97838
    // (`add secondsSwitchAccumulator, 54`). 54 is the Amiga 50 Hz constant left
    // unchanged in the PC port, which is why the real PC clock ran fast. zlatkok's
    // C++ rewrite (swos-port/src/game/gameTime.cpp:81 kPcTicksPerGameSeconds=70)
    // deviated to make the "N MIN" label literal; we restore the authentic 54 so
    // the in-play clock feels exactly like the original (user pick 2026-07-18).
    public const int kPcTicksPerGameSecond    = 54;
    public const int kAmigaTicksPerGameSecond = 49;

    // gameTime.cpp:183-184 — penalty-area Y bounds for last-minute prolong check.
    public const int kUpperPenaltyAreaLowerLine = 216;
    public const int kLowerPenaltyAreaUpperLine = 682;

    // PORT-ONLY. Ceiling on the number of ticks the last-minute prolong may keep
    // resetting the whistle countdown before it is allowed to drain (see the
    // backstop note in UpdateGameTime). The faithful endGameCounter budget is 55
    // (PC); this allows roughly one extra whistle-delay of injury time on top of
    // that, so the WHOLE prolong window lasts ~60 + ~55-drain ≈ 115 ticks (~4.6 s
    // at 25 Hz) even when the ball is pathologically camped in a penalty box
    // (task #153). Chosen so the entire prolong fits inside the smoke harness's
    // first 500-tick CLOCK-STALL window (prolong + the faithful gs29→gs23 half-
    // time dwell of ~375 ticks stays < 500), i.e. the prolong itself contributes
    // no clock-stall event. Only engages when the faithful exit can't fire; with
    // a keeper that clears the box (task #153) the natural condition wins first.
    public const int kProlongBackstopTicks = 60;

    // gameTime.cpp:129 — kGameLenSecondsTable[gameLengthInGame].
    // Smaller value = longer game (each game-second consumes more real-frames).
    private static readonly int[] kGameLenSecondsTable = { 30, 18, 12, 9 };

    // GameState::kInProgress (swos.h:592).
    private const short kGameStateInProgress = 100;

    // sprites.h:38, 41.
    public const int kTimeSprite8Mins      = 328;
    public const int kBigTimeDigitSprite0  = 331;

    // gameTime.cpp:13-14 — function pointer typedef.
    private delegate void LastPeriodMinuteHandler();

    // gameTime.cpp:17 — GameTime is a 4-int array: [_, digit1, digit2, digit3].
    // We back it onto Memory so it survives across calls in the same way as the
    // original `static GameTime m_gameTime` does.
    //
    // Layout in Memory:
    //   m_gameTime[0]            = Memory[Addr.gt_gameTime + 0]   (unused)
    //   m_gameTime[1] (hundreds) = Memory[Addr.gt_gameTime + 4]
    //   m_gameTime[2] (tens)     = Memory[Addr.gt_gameTime + 8]
    //   m_gameTime[3] (ones)     = Memory[Addr.gt_gameTime + 12]
    private static int ReadGameTime(int slot) =>
        Memory.ReadSignedDword(Memory.Addr.gt_gameTime + slot * 4);
    private static void WriteGameTime(int slot, int v) =>
        Memory.WriteDword(Memory.Addr.gt_gameTime + slot * 4, v);

    // gameTime.cpp:42-53 — resetGameTime.
    public static void ResetGameTime()
    {
        Memory.WriteWord(Memory.Addr.gt_showTime, 0);
        Memory.WriteDword(Memory.Addr.gt_gameSeconds, 0);
        WriteGameTime(0, 0);
        WriteGameTime(1, 0);
        WriteGameTime(2, 0);
        WriteGameTime(3, 0);
        Memory.WriteDword(Memory.Addr.gt_gameTimeInMinutes, 0);
        Memory.WriteDword(Memory.Addr.gt_secondsSwitchAccumulator, 0);
        Memory.WriteDword(Memory.Addr.gt_endGameCounter, 0);
        s_stoppageRealTicks = 0;
        s_halftimeCeremonyStage = 0;

        InitTimeDelta();
    }

    // gameTime.cpp:55-58.
    public static bool GameTimeShowing()
    {
        return Memory.ReadWord(Memory.Addr.gt_showTime) != 0;
    }

    // (kProlongSafetyCapTicks / s_prolongStallTicks — the port-only 600-tick
    //  prolong force-fire — were DELETED 2026-07-02. The faithful FSM in
    //  GameLoop.UpdateGameTimersAndCameraBreakMode now handles every
    //  stoppage/restart end-to-end, and ProlongLastMinute's exit conditions
    //  behave as the original intended once stoppages actually stop play.)

    // Stoppage-time tracker (port-only, display enhancement).
    //
    // Real SWOS never showed an explicit "+X:YY" injury-time indicator: the
    // clock simply pinned at 45:00 / 90:00 while ProlongLastMinute fired,
    // then jumped to the next half once the prolong window closed. Modern
    // football UIs render the additional time so the viewer knows the
    // referee is in stoppage. We mirror that here by counting real
    // lastFrameTicks elapsed while the prolong window is active and
    // converting them to game-seconds using the same timeDelta /
    // ticksPerGameSecond ratio that drives the regular clock advance
    // (gameTime.cpp:79-84). The counter resets on ResetGameTime AND each
    // time UpdateGameTime exits the prolong branch (period handler fired)
    // so half 2's stoppage starts at +0:00 again.
    //
    // NOTE: no Memory slot. Stoppage is presentation-only — nothing in the
    // ported gameplay references it. Keeping it as a static field avoids
    // burning Memory.Addr space and matches the s_prolongStallTicks
    // pattern above.
    private static int s_stoppageRealTicks = 0;

    /// <summary>Game-seconds elapsed in the current prolong window. 0 when not in prolong.</summary>
    public static int StoppageGameSeconds
    {
        get
        {
            // Mirror the per-second mapping used in UpdateGameTime:
            //   m_secondsSwitchAccumulator -= timeDelta each tick
            //   wraps when accumulator < 0, adding ticksPerSecond back
            //   gameSeconds advances by lastFrameTicks (=1) on each wrap
            // → one game-second per (ticksPerSecond / timeDelta) real ticks.
            int timeDelta = Memory.ReadSignedDword(Memory.Addr.gt_timeDelta);
            if (timeDelta <= 0) return 0;
            int ticksPerSecond = AmigaModeActive()
                ? kAmigaTicksPerGameSecond
                : kPcTicksPerGameSecond;
            // stoppageGameSec = realTicks * timeDelta / ticksPerSecond.
            return s_stoppageRealTicks * timeDelta / ticksPerSecond;
        }
    }

    /// <summary>True while ProlongLastMinute is keeping the clock at a period boundary (gameSeconds == -1).</summary>
    public static bool InProlong =>
        Memory.ReadSignedDword(Memory.Addr.gt_gameSeconds) < 0
        && GetPeriodEndHandler() != null;

    /// <summary>Resets the stoppage tick accumulator. Called by ResetGameTime + period transitions.</summary>
    public static void ResetStoppage() => s_stoppageRealTicks = 0;

    // gameTime.cpp:60-98 — updateGameTime.
    // The per-tick clock advance. Three paths:
    //   1. Last-minute prolong active (gameSeconds < 0): tick down endGameCounter;
    //      fire period handler at zero or refresh prolong if conditions still hold.
    //   2. In-progress play + not penalties: accumulate ticks until kSecondsAccum
    //      threshold, then bump gameSeconds. When seconds reach 60, bump
    //      gameTime + check half-start / next-minute-last-in-period.
    //   3. Otherwise: idle.
    public static void UpdateGameTime()
    {
        Memory.WriteWord(Memory.Addr.gt_showTime, 1);

        int gameSeconds = Memory.ReadSignedDword(Memory.Addr.gt_gameSeconds);
        bool lastMinuteSwitchAboutToHappen = gameSeconds < 0;

        if (lastMinuteSwitchAboutToHappen)
        {
            // gameTime.cpp:66-77.
            int endGameCounter = Memory.ReadSignedDword(Memory.Addr.gt_endGameCounter);
            short lastFrameTicks = Memory.ReadSignedWord(Memory.Addr.lastFrameTicks);
            endGameCounter -= lastFrameTicks;
            Memory.WriteDword(Memory.Addr.gt_endGameCounter, endGameCounter);

            // Stoppage-time accumulator. Bump by the same lastFrameTicks the
            // endGameCounter decrements with so the "+M:SS" display tracks the
            // exact real-tick budget consumed in the prolong window. Read by
            // Main.UpdateUi via StoppageGameSeconds. (Port-only display aid.)
            // It also doubles as the prolong-window elapsed-tick counter that
            // drives the PORT-ONLY backstop in the `else if` below — it is reset
            // to 0 exactly at prolong exit (period handler fired) and in
            // ResetGameTime, so within one boundary it equals the number of
            // ticks the clock has been pinned at 45/90/105/120.
            s_stoppageRealTicks += lastFrameTicks;

            if (endGameCounter < 0)
            {
                LastPeriodMinuteHandler? handler = GetPeriodEndHandler();
                if (handler != null)
                {
                    // swos.asm:112106-112139 — EndFirstHalf / EndOfGame gate on
                    // `goalCounter == 0`: the period-end transition defers while
                    // the post-goal celebration countdown is still draining
                    // (a goal scored in the dying seconds plays out its full
                    // celebration + restart before the whistle sequence runs).
                    // goalCounter is decremented per-tick by
                    // GameLoop.PlayEnqueuedSamples (comments.cpp:206-207).
                    short goalCounter = Memory.ReadSignedWord(Memory.Addr.goalCounter);
                    if (goalCounter != 0)
                        return;   // retry next tick once celebration drains

                    Memory.WriteDword(Memory.Addr.gt_gameSeconds, 0);
                    Memory.WriteWord(Memory.Addr.stateGoal, 0);
                    StubPlayEndGameWhistleSample();
                    s_stoppageRealTicks = 0;  // Next period starts at +0:00.
                    handler();
                }
            }
            // gameTime.cpp:75-77 — `else if (prolongLastMinute()) setupLast...`.
            // The faithful primary path: while an attack is live / the ball is
            // in a penalty area / play is stopped, keep the clock pinned by
            // resetting endGameCounter. The half whistles on the first calm
            // in-play midfield moment (prolongLastMinute() == false for one full
            // endGameCounter budget).
            //
            // PORT-ONLY BACKSTOP (`&& s_stoppageRealTicks < kProlongBackstopTicks`):
            // the original relies on the keeper/AI clearing a camped ball OUT of
            // the penalty box within a second or two, which ends the "ball inside
            // penalty area" arm of prolongLastMinute() (gameTime.cpp:191). Our
            // keeper/updatePlayers port still camps the ball in the six-yard box
            // (open task #153), so in AI-vs-AI play the ball can stay inside a
            // penalty area for hundreds of ticks — ballInsidePenaltyArea never
            // clears and the faithful exit never triggers, pinning the clock at
            // 45:00 for ~1000-5200 ticks (user bug #149). Once the window has run
            // past a plausible maximum injury-time budget we STOP refreshing the
            // counter and let it drain, so the whistle still blows. This is a
            // last-resort bound, NOT the normal path: with a correctly-clearing
            // keeper the faithful condition fires first (well inside the budget).
            // Remove the `&& ...` guard once task #153 lands. (Restores the cap
            // deleted 2026-07-02, but tick-budget-based and much tighter.)
            else if (ProlongLastMinute() && s_stoppageRealTicks < kProlongBackstopTicks)
            {
                SetupLastMinuteSwitchNextFrame();
            }
        }
        else
        {
            // gameTime.cpp:78-97.
            short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            bool playingPenalties = Memory.ReadWord(Memory.Addr.playingPenalties) != 0;
            if (gameStatePl != kGameStateInProgress || playingPenalties) return;

            int timeDelta = Memory.ReadSignedDword(Memory.Addr.gt_timeDelta);
            int accumulator = Memory.ReadSignedDword(Memory.Addr.gt_secondsSwitchAccumulator);
            accumulator -= timeDelta;

            if (accumulator < 0)
            {
                int ticksPerSecond = AmigaModeActive()
                    ? kAmigaTicksPerGameSecond
                    : kPcTicksPerGameSecond;
                accumulator += ticksPerSecond;

                short lastFrameTicks = Memory.ReadSignedWord(Memory.Addr.lastFrameTicks);
                gameSeconds += lastFrameTicks;

                if (gameSeconds >= 60)
                {
                    gameSeconds = 0;
                    BumpGameTime();

                    int gtMinutes = Memory.ReadSignedDword(Memory.Addr.gt_gameTimeInMinutes);
                    if (IsGameAtMinute(1) || IsGameAtMinute(46))
                        BumpPlayersLastPlayedHalfAtHalfStart();

                    if (IsNextMinuteLastInPeriod())
                    {
                        // setupLastMinuteSwitchNextFrame writes m_gameSeconds = -1
                        // directly in upstream (gameTime.cpp:312). In our port the
                        // helper writes to Memory but our local `gameSeconds` is
                        // stale - resync so the line-below write doesn't clobber
                        // the -1 sentinel back to 0 (which would leak past the
                        // prolong window and let the clock sail past 45 / 90 / 105
                        // / 120 without ever calling the period-end handler).
                        SetupLastMinuteSwitchNextFrame();
                        gameSeconds = Memory.ReadSignedDword(Memory.Addr.gt_gameSeconds);
                    }
                }

                Memory.WriteDword(Memory.Addr.gt_gameSeconds, gameSeconds);
            }

            Memory.WriteDword(Memory.Addr.gt_secondsSwitchAccumulator, accumulator);
        }
    }

    // gameTime.cpp:100-104.
    public static void DrawGameTime()
    {
        if (Memory.ReadWord(Memory.Addr.gt_showTime) == 0) return;
        DrawGameTimeImpl(new int[] { ReadGameTime(0), ReadGameTime(1), ReadGameTime(2), ReadGameTime(3) });
    }

    // gameTime.cpp:106-110 — overload taking explicit digits.
    public static void DrawGameTime(int digit1, int digit2, int digit3)
    {
        // assert(digit1 >= 0 && digit2 >= 0 && digit3 >= 0 && digit1 <= 1 && digit2 <= 9 && digit3 <= 9)
        DrawGameTimeImpl(new int[] { 0, digit1, digit2, digit3 });
    }

    // gameTime.cpp:112-115.
    public static uint GameTimeInMinutes()
    {
        return (uint)Memory.ReadDword(Memory.Addr.gt_gameTimeInMinutes);
    }

    // gameTime.cpp:117-120 — gameTimeAsBcd. Returns tuple (digit1, digit2, digit3).
    public static (int, int, int) GameTimeAsBcd()
    {
        return (ReadGameTime(1), ReadGameTime(2), ReadGameTime(3));
    }

    // gameTime.cpp:122-125.
    public static bool GameAtZeroMinute()
    {
        return Memory.ReadDword(Memory.Addr.gt_gameTimeInMinutes) == 0;
    }

    // gameTime.cpp:127-132.
    // Menu HALF LENGTH override (set once per match by Main from SecondsPerHalf).
    // The original had only 4 fixed lengths (gameLengthInGame 0-3 → the table);
    // our menu offers 6 real-time presets. When >0 this value is used as timeDelta
    // instead of the table entry, so a 45-game-minute half takes exactly the
    // chosen real seconds. 0 = fall back to the faithful table. Deterministic:
    // set before InitGameVariables and never changed mid-match.
    public static int TimeDeltaOverride = 0;

    private static void InitTimeDelta()
    {
        int gameLengthInGame = Memory.ReadSignedWord(Memory.Addr.gameLengthInGame);
        // assert(gameLengthInGame <= 3)
        if (gameLengthInGame < 0) gameLengthInGame = 0;
        if (gameLengthInGame > 3) gameLengthInGame = 3;
        int td = TimeDeltaOverride > 0 ? TimeDeltaOverride : kGameLenSecondsTable[gameLengthInGame];
        Memory.WriteDword(Memory.Addr.gt_timeDelta, td);
    }

    // gameTime.cpp:134-137.
    private static bool IsGameAtMinute(uint minute)
    {
        return minute == Memory.ReadDword(Memory.Addr.gt_gameTimeInMinutes);
    }

    // gameTime.cpp:139-143 — endFirstHalf.
    //
    //   static void endFirstHalf() {
    //       EndFirstHalf();                          // swos.asm:112089
    //       bumpPlayersLastPlayedHalfAtHalfEnd();
    //   }
    //
    // EndFirstHalfImpl (the asm EndFirstHalf) sets gameState=29
    // (ST_FIRST_HALF_ENDED) / gameStatePl=101 / stoppageEventTimer=100. The
    // faithful dispatcher in GameLoop.UpdateGameTimersAndCameraBreakMode then
    // walks 29 → firstHalfJustEnded (23) → goToHalftime (25, result panel) →
    // setCameraMovingToShowerState (22, sides swapped) → prepareForInitialKick
    // (0) → breakCameraMode ladder → second-half kickoff, exactly as
    // gameLoop.cpp:961-1009 / 862-893 / 945-959 do.
    private static void EndFirstHalf()
    {
        // gameTime.cpp:141 — EndFirstHalf() (asm footprint).
        EndFirstHalfImpl();
        // gameTime.cpp:142.
        BumpPlayersLastPlayedHalfAtHalfEnd();
    }

    // VESTIGIAL PORT-ONLY API (kept for Main.cs compile compatibility —
    // Main.cs:1284-1348 smoke-test helpers reference these). The half-time
    // ceremony is now driven entirely by the faithful dispatcher in
    // GameLoop.UpdateGameTimersAndCameraBreakMode; nothing in the sim reads
    // or advances this stage tracker any more.
    public static int HalftimeCeremonyStage => s_halftimeCeremonyStage;
    public static void SetHalftimeCeremonyStage(int stage) => s_halftimeCeremonyStage = stage;
    private static int s_halftimeCeremonyStage = 0;
    public const short kHalftimeStageDwellTicks = 300;

    // gameTime.cpp:145-176 — endSecondHalf.
    //
    // On "game actually over" this calls EndOfGame() (swos.asm:112119):
    // gameState=30 (ST_GAME_ENDED) / gameStatePl=101 / stoppageEventTimer=150.
    // The faithful dispatcher in GameLoop.UpdateGameTimersAndCameraBreakMode
    // then walks 30 → playersLeavingPitch (24) → SWOS::GameOver (26, result
    // panel, stoppageEventTimer=1650) → playGame=0, exactly as
    // gameLoop.cpp:1011-1041 / 895-927 do. ET / penalties branches start a
    // NEW period via StartFirstExtraTime / StartPenalties instead.
    //
    // Source: external/swos-port/src/game/gameTime.cpp:145-176.
    private static void EndSecondHalf()
    {
        BumpPlayersLastPlayedHalfAtHalfEnd();

        // gameTime.cpp:148-149 — snapshot stats counts.
        int statsT1 = Memory.ReadWord(Memory.Addr.statsTeam1Goals);
        int statsT2 = Memory.ReadWord(Memory.Addr.statsTeam2Goals);
        Memory.WriteWord(Memory.Addr.statsTeam1GoalsCopy, statsT1);
        Memory.WriteWord(Memory.Addr.statsTeam2GoalsCopy, statsT2);

        int totalT1 = Memory.ReadWord(Memory.Addr.team1TotalGoals);
        int totalT2 = Memory.ReadWord(Memory.Addr.team2TotalGoals);

        if (totalT1 == totalT2)
        {
            // gameTime.cpp:155-156 — tie-breaker uses combined statsGoals +
            // 2 * firstLegGoals (away-goals-count-double rule).
            totalT1 = statsT1 + 2 * Memory.ReadSignedWord(Memory.Addr.team1GoalsFirstLeg);
            totalT2 = statsT2 + 2 * Memory.ReadSignedWord(Memory.Addr.team2GoalsFirstLeg);

            bool secondLeg = Memory.ReadWord(Memory.Addr.secondLeg) != 0;
            short playing2ndGame = Memory.ReadSignedWord(Memory.Addr.playing2ndGame);
            bool gameTied = !secondLeg || playing2ndGame != 1 || totalT1 == totalT2;

            if (gameTied)
            {
                short extraTimeState = Memory.ReadSignedWord(Memory.Addr.extraTimeState);
                short penaltiesState = Memory.ReadSignedWord(Memory.Addr.penaltiesState);
                if (extraTimeState != 0)
                {
                    Memory.WriteWord(Memory.Addr.extraTimeState, -1);
                    StartFirstExtraTime();
                }
                else if (penaltiesState != 0)
                {
                    Memory.WriteWord(Memory.Addr.penaltiesState, -1);
                    StartPenalties();
                }
                else
                {
                    // gameTime.cpp:167-168.
                    Memory.WriteDword(Memory.Addr.winningTeamPtr, 0);
                    EndOfGame();
                    MarkPlayersHappyOrSad();
                }
                return;
            }
        }

        // gameTime.cpp:174-175.
        int winningTeamGame = totalT1 > totalT2
            ? Memory.ReadSignedDword(Memory.Addr.topTeamInGame)
            : Memory.ReadSignedDword(Memory.Addr.bottomTeamInGame);
        Memory.WriteDword(Memory.Addr.winningTeamPtr, winningTeamGame);
        EndOfGame();
        MarkPlayersHappyOrSad();
    }

    // (FinishMatchAtFullTime / TickFullTimeCeremony / kFullTimeCeremonyDwell-
    //  Ticks — the port-only full-time collapse that jumped straight to
    //  GameLoop.GameOver + a synthetic ceremony countdown — were DELETED
    //  2026-07-02. The faithful dispatcher in GameLoop now walks
    //  gameState 30 → 24 → 26 → playGame=0 per gameLoop.cpp:1011-1041 /
    //  895-927, with the original stoppageEventTimer dwells 150 / 275 / 1650.)

    // Winner/loser end-of-match reaction — mechanical port of the
    // `gameState == ST_GAME_ENDED (30)` arm in updatePlayers.cpp:8706-8804.
    // Per player, the original checks (in order):
    //   - gameStatePl != ST_GAME_IN_PROGRESS   (:8695-8704)
    //   - gameState == ST_GAME_ENDED (30)      (:8706-8717)
    //   - winningTeamPtr != 0                  (:8719-8725, tie → no poses)
    //   - Sprite.playerState == PL_NORMAL (0)  (:8727-8740)
    //   - Sprite.playerOrdinal != 1            (:8742-8754, keepers excluded)
    //   - Sprite.deltaX | deltaY == 0          (:8756-8765, standing still)
    //   - Rand() <= 64                         (:8767-8778, ~25 % chance per
    //     visit — the original re-rolls every tick so the reaction ripples
    //     across the squad instead of snapping)
    //   - team.inGameTeamPtr == winningTeamPtr → PL_HAPPY (15) + winning anim
    //     table (452974), else PL_SAD (14) + losing anim table (452844)
    //     (:8780-8804). The reaction anim tables aren't allocated in our
    //     Memory map yet; the renderer keys off PlayerState, so only the
    //     state byte is written here.
    //
    // CALL-SITE NOTE (PORT-ONLY): the canonical home of this check is the
    // per-player loop in updatePlayers.cpp (a concurrent port owns that
    // file). Until it lands there, EndSecondHalf / EndSecondExtraTime invoke
    // this sweep once right after EndOfGame — a single Rand-gated pass, so
    // roughly a quarter of the standing outfielders strike a pose.
    //
    // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:8706-8804.
    public static void MarkPlayersHappyOrSad()
    {
        // updatePlayers.cpp:8706-8717 — gameState must be ST_GAME_ENDED (30).
        if (Memory.ReadSignedWord(Memory.Addr.gameState) != kGameStateGameEnded)
            return;

        // updatePlayers.cpp:8719-8725 — winningTeamPtr == 0 (tie) → no poses.
        int winningTeamPtr = Memory.ReadSignedDword(Memory.Addr.winningTeamPtr);
        if (winningTeamPtr == 0) return;

        int topInGame = Memory.ReadSignedDword(Memory.Addr.topTeamInGame);
        // Team 1 (slots 0..10) wins iff winningTeamPtr == topTeamInGame —
        // same identity the original resolves via team.inGameTeamPtr
        // (updatePlayers.cpp:8780-8790).
        bool topIsWinner = winningTeamPtr == topInGame;
        short winningTeamNumber = topIsWinner ? (short)1 : (short)2;

        const byte PL_NORMAL = 0;
        const byte PL_SAD    = 14;
        const byte PL_HAPPY  = 15;
        for (int slot = 0; slot < PlayerSprite.TotalSlots; slot++)
        {
            int spriteAddr = PlayerSprite.Base(slot);

            // updatePlayers.cpp:8727-8740 — `cmp [esi+Sprite.playerState], PL_NORMAL`.
            byte curState = Memory.ReadByte(spriteAddr + PlayerSprite.OffPlayerState);
            if (curState != PL_NORMAL) continue;

            // updatePlayers.cpp:8742-8754 — `cmp [esi+Sprite.playerOrdinal], 1`
            // (ordinal 1 = goalkeeper → skip; outfielders only).
            if (Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffPlayerOrdinal) == 1)
                continue;

            // updatePlayers.cpp:8756-8765 — `mov D0, deltaX; or D0, deltaY;
            // jnz` — only players standing still react.
            int dx = Memory.ReadSignedDword(spriteAddr + PlayerSprite.OffDeltaX);
            int dy = Memory.ReadSignedDword(spriteAddr + PlayerSprite.OffDeltaY);
            if ((dx | dy) != 0) continue;

            // updatePlayers.cpp:8767-8778 — `call Rand; cmp D0, 64; ja skip`.
            if (Rng.NextByte() > 64) continue;

            // updatePlayers.cpp:8780-8804 — winner → PL_HAPPY, loser → PL_SAD.
            short spriteTeamNum = PlayerSprite.TeamNumber(slot);
            byte newState = (spriteTeamNum == winningTeamNumber) ? PL_HAPPY : PL_SAD;
            Memory.WriteByte(spriteAddr + PlayerSprite.OffPlayerState, newState);
        }
    }

    // gameTime.cpp:178-196 — prolongLastMinute.
    // Determines if the last minute should be prolonged: if game not in
    // progress (set-piece / break) OR if ball is in either penalty area OR
    // if the attacking team is still attacking, keep the clock alive.
    private static bool ProlongLastMinute()
    {
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        if (gameStatePl != kGameStateInProgress) return true;

        // Production path uses ball.y direct (whole pixels).
        short ballY = Memory.ReadSignedWord(BallSprite.Base + BallSprite.OffY + 2);

        bool ballInsidePenaltyArea =
            ballY <= kUpperPenaltyAreaLowerLine || ballY > kLowerPenaltyAreaUpperLine;

        // gameTime.cpp:192-193 — attacking team = whichever side is opposite to
        // where the ball is (ball in top half → bottom team is attacking up).
        int attackingTeam = ballY > kPitchCenterY ? TeamData.TopBase : TeamData.BottomBase;
        int lastTeamPlayed = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayed);
        bool attackInProgress = lastTeamPlayed == attackingTeam;

        return ballInsidePenaltyArea || attackInProgress;
    }

    // gameTime.cpp:198-222 — endSecondExtraTime.
    //
    // Mirrors EndSecondHalf: when the game is actually over (no shootout
    // left to fall back to, no second-leg differential to resolve) we call
    // EndOfGame (gameState=30) and let the faithful dispatcher walk
    // 30 → 24 → 26 → playGame=0. The shootout branch starts a new sub-period
    // via StartPenalties.
    //
    // Source: external/swos-port/src/game/gameTime.cpp:198-222.
    private static void EndSecondExtraTime()
    {
        int totalT1 = Memory.ReadWord(Memory.Addr.team1TotalGoals);
        int totalT2 = Memory.ReadWord(Memory.Addr.team2TotalGoals);

        if (totalT1 == totalT2)
        {
            // gameTime.cpp:203-205.
            totalT1 = Memory.ReadWord(Memory.Addr.statsTeam1Goals)
                    + 2 * Memory.ReadSignedWord(Memory.Addr.team1GoalsFirstLeg);
            totalT2 = Memory.ReadWord(Memory.Addr.statsTeam2Goals)
                    + 2 * Memory.ReadSignedWord(Memory.Addr.team2GoalsFirstLeg);

            bool secondLeg = Memory.ReadWord(Memory.Addr.secondLeg) != 0;
            bool playing2ndGame = Memory.ReadWord(Memory.Addr.playing2ndGame) != 0;
            bool gameTied = !secondLeg || !playing2ndGame || totalT1 == totalT2;

            if (gameTied)
            {
                short penaltiesState = Memory.ReadSignedWord(Memory.Addr.penaltiesState);
                if (penaltiesState != 0)
                {
                    Memory.WriteWord(Memory.Addr.penaltiesState, -1);
                    StartPenalties();
                }
                else
                {
                    // gameTime.cpp:214-215.
                    Memory.WriteDword(Memory.Addr.winningTeamPtr, 0);
                    EndOfGame();
                    MarkPlayersHappyOrSad();
                }
                return;
            }
        }

        // gameTime.cpp:220-221.
        int winningTeamGame = totalT1 > totalT2
            ? Memory.ReadSignedDword(Memory.Addr.topTeamInGame)
            : Memory.ReadSignedDword(Memory.Addr.bottomTeamInGame);
        Memory.WriteDword(Memory.Addr.winningTeamPtr, winningTeamGame);
        EndOfGame();
        MarkPlayersHappyOrSad();
    }

    // gameTime.cpp:224-240 — getGameTimeSprites.
    // Picks which big-time-digit sprite indices to render based on which
    // digits of the time are nonzero (suppress leading zeros). Result is a
    // 4-element int array where positions are filled left-to-right with sprite
    // indices and -1 marks the end.
    private static int[] GetGameTimeSprites(int[] gameTime)
    {
        int[] sprites = { -1, -1, -1, -1 };

        if (gameTime[1] != 0)
        {
            sprites[0] = gameTime[1] + kBigTimeDigitSprite0;
            sprites[1] = gameTime[2] + kBigTimeDigitSprite0;
            sprites[2] = gameTime[3] + kBigTimeDigitSprite0;
        }
        else if (gameTime[2] != 0)
        {
            sprites[0] = gameTime[2] + kBigTimeDigitSprite0;
            sprites[1] = gameTime[3] + kBigTimeDigitSprite0;
        }
        else
        {
            sprites[0] = gameTime[3] + kBigTimeDigitSprite0;
        }

        return sprites;
    }

    // gameTime.cpp:242-251 — getPeriodEndHandler.
    private static LastPeriodMinuteHandler? GetPeriodEndHandler()
    {
        uint mins = (uint)Memory.ReadDword(Memory.Addr.gt_gameTimeInMinutes);
        switch (mins)
        {
            case 45:  return EndFirstHalf;
            case 90:  return EndSecondHalf;
            case 105: return EndFirstExtraTime;
            case 120: return EndSecondExtraTime;
            default:  return null;
        }
    }

    // gameTime.cpp:253-256.
    private static bool IsNextMinuteLastInPeriod()
    {
        return GetPeriodEndHandler() != null;
    }

    // gameTime.cpp:258-271 — drawGameTime(const GameTime&).
    private static void DrawGameTimeImpl(int[] gameTime)
    {
        // assert width <= 25 etc — skip in port.
        int kDigitWidth = GetSpriteWidth(kBigTimeDigitSprite0 + 8);
        int[] timeDigitSprites = GetGameTimeSprites(gameTime);

        int xOffset = 0;
        for (int i = 0; i < timeDigitSprites.Length && timeDigitSprites[i] >= 0; i++)
        {
            StubDrawMenuSprite(timeDigitSprites[i], kTimeX + xOffset, kTimeY);
            xOffset += kDigitWidth;
        }

        StubDrawMenuSprite(kTimeSprite8Mins, kTimeX + xOffset, kTimeY);
    }

    // gameTime.cpp:273-284 — bumpGameTime.
    // Increments minute counter with carry from ones → tens → hundreds.
    // Hundreds digit caps at 9 (so display caps at 999 minutes).
    private static void BumpGameTime()
    {
        int d3 = ReadGameTime(3) + 1;
        if (d3 >= 10)
        {
            d3 = 0;
            int d2 = ReadGameTime(2) + 1;
            if (d2 >= 10)
            {
                d2 = 0;
                int d1 = ReadGameTime(1);
                if (d1 < 9)
                    WriteGameTime(1, d1 + 1);
            }
            WriteGameTime(2, d2);
        }
        WriteGameTime(3, d3);

        int mins = Memory.ReadSignedDword(Memory.Addr.gt_gameTimeInMinutes) + 1;
        Memory.WriteDword(Memory.Addr.gt_gameTimeInMinutes, mins);
    }

    // gameTime.cpp:286-292 — bumpPlayersLastPlayedHalfAtHalfStart.
    // For each player on each team: if not sent off (cards < 2) AND not already
    // tagged as second-half-played, mark them as first-half-played.
    private static void BumpPlayersLastPlayedHalfAtHalfStart()
    {
        ForEachPlayer((playerInfoAddr) =>
        {
            int cards = ReadPlayerInfoCards(playerInfoAddr);
            int halfPlayed = ReadPlayerInfoHalfPlayed(playerInfoAddr);
            if (cards < 2 && halfPlayed != 2)
                WritePlayerInfoHalfPlayed(playerInfoAddr, 1);
        });
    }

    // gameTime.cpp:294-300 — bumpPlayersLastPlayedHalfAtHalfEnd.
    private static void BumpPlayersLastPlayedHalfAtHalfEnd()
    {
        ForEachPlayer((playerInfoAddr) =>
        {
            int cards = ReadPlayerInfoCards(playerInfoAddr);
            int halfPlayed = ReadPlayerInfoHalfPlayed(playerInfoAddr);
            if (cards < 2 && halfPlayed == 1)
                WritePlayerInfoHalfPlayed(playerInfoAddr, 2);
        });
    }

    // gameTime.cpp:302-310 — forEachPlayer.
    // Iterates both teams' 11 PlayerInfo records and invokes the callback for
    // each. PlayerInfo struct lives inside topTeamInGame.players /
    // bottomTeamInGame.players (61 bytes each, 11 per team).
    private static void ForEachPlayer(System.Action<int> action)
    {
        int top = Memory.ReadSignedDword(Memory.Addr.topTeamInGame);
        int bot = Memory.ReadSignedDword(Memory.Addr.bottomTeamInGame);
        for (int team = 0; team < 2; team++)
        {
            int teamGame = team == 0 ? top : bot;
            if (teamGame == 0) continue;
            // players array assumed to be the first field of TeamGame.
            for (int i = 0; i < 11; i++)
                action(teamGame + i * 61);
        }
    }

    // gameTime.cpp:312-316 — setupLastMinuteSwitchNextFrame.
    private static void SetupLastMinuteSwitchNextFrame()
    {
        Memory.WriteDword(Memory.Addr.gt_gameSeconds, -1);
        int endGame = AmigaModeActive() ? 50 : 55;
        Memory.WriteDword(Memory.Addr.gt_endGameCounter, endGame);
    }

    // ---- Stubs ---------------------------------------------------------------

    // Mechanical port of amigaMode.cpp:16-19 — amigaModeActive() returns the
    // module-static m_enabled flag (default false, toggled via
    // setAmigaModeEnabled). OpenSWOS hard-locks to PC mode (CLAUDE.md), so
    // m_enabled never flips and we return false. The flag still has a single
    // call site so it's worth keeping as a function (parity with C++ port).
    public static bool AmigaModeActive() => false;

    // sfx.cpp playEndGameWhistleSample() — whistle at each period end (half/full).
    private static void StubPlayEndGameWhistleSample() => OpenSwos.Audio.MatchAudio.PlayEndGameWhistle();

    // GameState constants from swos.h:577-595 — also referenced by orchestrator
    // (Main.cs). NOTE: kFirstHalfEnded is 29 (swos.h:590) — an earlier port
    // revision wrote 25 here (confusing it with the asm's write-only
    // `writeOnlyAtEndOfHalf, 25` and with ST_RESULT_ON_HALFTIME); the
    // faithful dispatcher (gameLoop.cpp:961-970) matches on 29.
    private const short kGameStateFirstHalfEnded = 29;  // ST_FIRST_HALF_ENDED (swos.h:590)
    private const short kGameStateGameEnded      = 30;  // ST_GAME_ENDED
    private const short kGameStatePlStopped      = 101; // ST_STOPPED

    // Mechanical port of EndFirstHalf (swos.asm:112089-112113). Identical
    // structure to EndOfGame below — only difference is gameState value and
    // stoppageEventTimer dwell (100 vs 150 ticks).
    //
    // Without this, the clock sails past 45/90 game-minutes — the
    // gameTime.cpp:139 endFirstHalf path expected the original to set the
    // half-ended state so the camera / UI / orchestrator can react.
    //
    // Ported from external/swos-port/swos/swos.asm:112089 (the C++ port
    // forwards to this asm function via game.h).
    private static void EndFirstHalfImpl()
    {
        // swos.asm:112090 — mov hideBall, 0.
        Memory.WriteWord(Memory.Addr.hideBall, 0);
        // swos.asm:112091 — mov stoppageEventTimer, 100.
        Memory.WriteWord(Memory.Addr.stoppageEventTimer, 100);
        // swos.asm:112092 — mov gameState, ST_FIRST_HALF_ENDED (29, swos.h:590).
        Memory.WriteWord(Memory.Addr.gameState, kGameStateFirstHalfEnded);
        // swos.asm:112093 — mov breakCameraMode, -1.
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        // swos.asm:112094 — mov gameStatePl, 101.
        Memory.WriteWord(Memory.Addr.gameStatePl, kGameStatePlStopped);
        // swos.asm:112095 — mov gameNotInProgressCounterWriteOnly, 0.
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);
        // swos.asm:112096 — mov cameraDirection, -1.
        Memory.WriteWord(Memory.Addr.cameraDirection, -1);
        // swos.asm:112097 — mov lastTeamPlayedBeforeBreak, offset topTeamData.
        Memory.WriteDword(Memory.Addr.lastTeamPlayedBeforeBreak, TeamData.TopBase);
        // swos.asm:112098-112099 — clear stoppage timers.
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);
        // swos.asm:112100 — call ResetBothTeamsPlayerPassingKicking.
        ResetBothTeamsPlayerPassingKicking();
        // swos.asm:112101-112102 — zero camera velocities.
        Memory.WriteWord(Memory.Addr.cameraXVelocity, 0);
        Memory.WriteWord(Memory.Addr.cameraYVelocity, 0);
        // swos.asm:112103-112109 — only set writeOnlyAtEndOfHalf when playGame
        // && !goalCounter. We don't track writeOnlyAtEndOfHalf (write-only, no
        // readers), but preserve the gate so future readers see the right
        // call-site footprint.
        short playGame = Memory.ReadSignedWord(Memory.Addr.playGame);
        short goalCounter = Memory.ReadSignedWord(Memory.Addr.goalCounter);
        _ = (playGame != 0 && goalCounter == 0);  // writeOnlyAtEndOfHalf = 25.
    }

    // Mechanical port of EndOfGame (swos.asm:112119-112143). Sets the
    // post-match state so the orchestrator (Main.cs SyncMatchFromSwosPort)
    // can flip MatchPhase to FullTime.
    //
    // Without this the clock sails past 90 game-minutes forever — the
    // smoke test caught that at 25 000 ticks the clock was at 103:43 with
    // match.phase=Play. See GameState enum in swos.h:577-595.
    //
    // Ported from external/swos-port/swos/swos.asm:112119.
    private static void EndOfGame()
    {
        // swos.asm:112120 — mov hideBall, 0.
        Memory.WriteWord(Memory.Addr.hideBall, 0);
        // swos.asm:112121 — mov stoppageEventTimer, 150.
        Memory.WriteWord(Memory.Addr.stoppageEventTimer, 150);
        // swos.asm:112122 — mov gameState, ST_GAME_ENDED (30).
        Memory.WriteWord(Memory.Addr.gameState, kGameStateGameEnded);
        // swos.asm:112123 — mov breakCameraMode, -1.
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        // swos.asm:112124 — mov gameStatePl, 101.
        Memory.WriteWord(Memory.Addr.gameStatePl, kGameStatePlStopped);
        // swos.asm:112125 — mov gameNotInProgressCounterWriteOnly, 0.
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);
        // swos.asm:112126 — mov cameraDirection, -1.
        Memory.WriteWord(Memory.Addr.cameraDirection, -1);
        // swos.asm:112127 — mov lastTeamPlayedBeforeBreak, offset topTeamData.
        Memory.WriteDword(Memory.Addr.lastTeamPlayedBeforeBreak, TeamData.TopBase);
        // swos.asm:112128-112129 — clear stoppage timers.
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);
        // swos.asm:112130 — call ResetBothTeamsPlayerPassingKicking.
        ResetBothTeamsPlayerPassingKicking();
        // swos.asm:112131-112132 — zero camera velocities.
        Memory.WriteWord(Memory.Addr.cameraXVelocity, 0);
        Memory.WriteWord(Memory.Addr.cameraYVelocity, 0);
        // swos.asm:112133-112139 — writeOnlyAtEndOfHalf gate; see EndFirstHalf.
        short playGame = Memory.ReadSignedWord(Memory.Addr.playGame);
        short goalCounter = Memory.ReadSignedWord(Memory.Addr.goalCounter);
        _ = (playGame != 0 && goalCounter == 0);
    }

    // Mechanical port of ResetBothTeamsPlayerPassingKicking
    // (swos.asm:112149-112166). Called by EndFirstHalf / EndOfGame to clear
    // each team's per-player passing/kicking state at a stoppage.
    //
    // Original snapshots playerTurnFlags into lastPlayerTurnFlags (which the
    // goalie code in updatePlayers.cpp:908 reads the HIGH byte of — always 0
    // in practice per the asm comment, but we keep parity), then walks both
    // teams calling ResetPlayerPassingKicking; finally clears the BOTTOM
    // team's goalkeeperPlaying (the asm has a subtle bug — esi is left
    // pointing at bottomTeamData after the second call, so only the bottom
    // keeper's flag is cleared; we replicate that exactly).
    //
    // Ported from external/swos-port/swos/swos.asm:112149.
    private static void ResetBothTeamsPlayerPassingKicking()
    {
        // swos.asm:112153-112154 — dseg_130FF9 = cameraDirection.
        // dseg_130FF9 is write-only with no readers; skip.

        // swos.asm:112155-112156 — lastPlayerTurnFlags = playerTurnFlags.
        // playerTurnFlags is a byte; the original treats it as a word for the
        // mov ax (low byte = playerTurnFlags, high byte = whatever's next).
        // We replicate as a word read/write.
        ushort ptf = Memory.ReadByte(Memory.Addr.playerTurnFlags);
        Memory.WriteWord(Memory.Addr.lastPlayerTurnFlags, ptf);

        // swos.asm:112157-112160 — reset each team's per-player passing/kicking.
        ResetPlayerPassingKicking(TeamData.TopBase);
        ResetPlayerPassingKicking(TeamData.BottomBase);

        // swos.asm:112161-112162 — clear bottom team's goalkeeperPlaying
        // (esi still points to bottomTeamData from the last call). Bug or
        // by design — we mirror.
        Memory.WriteWord(TeamData.BottomBase + TeamData.OffGoalkeeperPlaying, 0);
    }

    // Mechanical port of ResetPlayerPassingKicking (swos.asm:112174-112186).
    // Clears the team's per-frame ball-control / passing / kicking state.
    private static void ResetPlayerPassingKicking(int teamBase)
    {
        // swos.asm:112178 — TeamGeneralInfo.ballInPlay = 0.
        Memory.WriteWord(teamBase + TeamData.OffBallInPlay, 0);
        // swos.asm:112179 — TeamGeneralInfo.ballOutOfPlay = 0.
        Memory.WriteWord(teamBase + TeamData.OffBallOutOfPlay, 0);
        // swos.asm:112180 — TeamGeneralInfo.controlledPlayerSprite = 0.
        Memory.WriteDword(teamBase + TeamData.OffControlledPlayer, 0);
        // swos.asm:112181 — TeamGeneralInfo.passToPlayerPtr = 0.
        Memory.WriteDword(teamBase + TeamData.OffPassToPlayerPtr, 0);
        // swos.asm:112182 — TeamGeneralInfo.passingBall = 0.
        Memory.WriteWord(teamBase + TeamData.OffPassingBall, 0);
        // swos.asm:112183 — TeamGeneralInfo.passingToPlayer = 0.
        Memory.WriteWord(teamBase + TeamData.OffPassingToPlayer, 0);
        // swos.asm:112184 — TeamGeneralInfo.playerSwitchTimer = 0.
        Memory.WriteWord(teamBase + TeamData.OffPlayerSwitchTimer, 0);
        // swos.asm:112185 — TeamGeneralInfo.passingKickingPlayer = 0.
        Memory.WriteDword(teamBase + TeamData.OffPassingKickingPlayer, 0);
    }

    // GameState constants from swos.asm:1428-1429.
    private const short kGameStateFirstExtraStarting = 27;  // ST_FIRST_EXTRA_STARTING
    private const short kGameStateFirstExtraEnded    = 28;  // ST_FIRST_EXTRA_ENDED

    // Mechanical port of StartFirstExtraTime (swos.asm:103925-103962).
    //
    // Triggered when game-time crosses 90:00 with the score tied AND extra-time
    // enabled. Resets the per-period state machine for the first 15-minute
    // ET half: halfNumber=1, picks teamPlayingUp / teamStarting randomly,
    // gameState=27, gameStatePl=101 (stopped), all stoppage / camera timers
    // cleared, all players stopped. When stoppageEventTimer (110) expires the
    // faithful dispatcher fires prepareForInitialKick for gameState 27
    // (gameLoop.cpp:1043-1057).
    //
    // Source: external/swos-port/swos/swos.asm:103925.
    private static void StartFirstExtraTime()
    {
        // swos.asm:103926 — mov halfNumber, 1.
        Memory.WriteWord(Memory.Addr.halfNumber, 1);

        // swos.asm:103927-103931 — Rand & 1 + 1 → teamPlayingUp (1 or 2).
        Memory.WriteWord(Memory.Addr.teamPlayingUp, (short)((Rng.NextByte() & 1) + 1));

        // swos.asm:103932-103936 — same recipe → teamStarting.
        Memory.WriteWord(Memory.Addr.teamStarting, (short)((Rng.NextByte() & 1) + 1));

        // swos.asm:103937 — mov hideBall, 0.
        Memory.WriteWord(Memory.Addr.hideBall, 0);

        // swos.asm:103938 — call InitTeamsData. Scalar reset block ported;
        // per-team field reseat still pending (see InitTeamsDataForExtraTime
        // for the full coverage map).
        InitTeamsDataForExtraTime();

        // swos.asm:103939 — mov stoppageEventTimer, 110.
        Memory.WriteWord(Memory.Addr.stoppageEventTimer, 110);
        // swos.asm:103940 — mov gameState, ST_FIRST_EXTRA_STARTING (27).
        Memory.WriteWord(Memory.Addr.gameState, kGameStateFirstExtraStarting);
        // swos.asm:103941 — mov breakCameraMode, -1.
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        // swos.asm:103942 — mov gameStatePl, ST_STOPPED (101).
        Memory.WriteWord(Memory.Addr.gameStatePl, kGameStatePlStopped);
        // swos.asm:103943 — mov gameNotInProgressCounterWriteOnly, 0.
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);
        // swos.asm:103944 — mov cameraDirection, -1.
        Memory.WriteWord(Memory.Addr.cameraDirection, -1);

        // swos.asm:103945-103951 — A0 = topTeamData if teamStarting==teamPlayingUp
        // else A0 = bottomTeamData. Then lastTeamPlayedBeforeBreak = A0.
        // (Picks the team that starts attacking upward this half.)
        short teamStarting   = Memory.ReadSignedWord(Memory.Addr.teamStarting);
        short teamPlayingUp  = Memory.ReadSignedWord(Memory.Addr.teamPlayingUp);
        int lastTeam = (teamStarting == teamPlayingUp) ? TeamData.TopBase : TeamData.BottomBase;
        // swos.asm:103954-103955 — mov lastTeamPlayedBeforeBreak, A0.
        Memory.WriteDword(Memory.Addr.lastTeamPlayedBeforeBreak, lastTeam);

        // swos.asm:103956-103957 — clear stoppage timers.
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);

        // swos.asm:103958 — call StopAllPlayers.
        TeamPort.StopAllPlayers();

        // swos.asm:103959-103960 — zero camera velocities.
        Memory.WriteWord(Memory.Addr.cameraXVelocity, 0);
        Memory.WriteWord(Memory.Addr.cameraYVelocity, 0);
    }

    // (CompleteExtraTimeIntro — the port-only gameState=27 → 100 collapse —
    //  was DELETED 2026-07-02. GameLoop.DispatchStoppageEventTriggered now
    //  fires Kickoff.PrepareForInitialKick for gameState 27 / 28 faithfully,
    //  per gameLoop.cpp:1043-1073.)

    // Mechanical port of EndFirstExtraTime (swos.asm:103968-103998).
    //
    // Triggered when game-time crosses 105:00 (end of first 15-min ET half).
    // Mirror of StartFirstExtraTime except: halfNumber=2, both teamPlayingUp /
    // teamStarting are flipped (neg + +3 = 3 - old, so 1→2 / 2→1), gameState=28.
    //
    // Source: external/swos-port/swos/swos.asm:103968.
    private static void EndFirstExtraTime()
    {
        // swos.asm:103969 — mov halfNumber, 2.
        Memory.WriteWord(Memory.Addr.halfNumber, 2);

        // swos.asm:103970-103971 — neg teamPlayingUp ; add teamPlayingUp, 3
        // (so 1 → -1 + 3 = 2, and 2 → -2 + 3 = 1).
        short tpu = Memory.ReadSignedWord(Memory.Addr.teamPlayingUp);
        Memory.WriteWord(Memory.Addr.teamPlayingUp, (short)(3 - tpu));
        // swos.asm:103972-103973 — neg teamStarting ; add teamStarting, 3.
        short ts = Memory.ReadSignedWord(Memory.Addr.teamStarting);
        Memory.WriteWord(Memory.Addr.teamStarting, (short)(3 - ts));

        // swos.asm:103974 — mov hideBall, 0.
        Memory.WriteWord(Memory.Addr.hideBall, 0);

        // swos.asm:103975 — call InitTeamsData. See StartFirstExtraTime note.
        InitTeamsDataForExtraTime();

        // swos.asm:103976 — mov stoppageEventTimer, 110.
        Memory.WriteWord(Memory.Addr.stoppageEventTimer, 110);
        // swos.asm:103977 — mov gameState, ST_FIRST_EXTRA_ENDED (28).
        Memory.WriteWord(Memory.Addr.gameState, kGameStateFirstExtraEnded);
        // swos.asm:103978 — mov breakCameraMode, -1.
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        // swos.asm:103979 — mov gameStatePl, ST_STOPPED (101).
        Memory.WriteWord(Memory.Addr.gameStatePl, kGameStatePlStopped);
        // swos.asm:103980 — mov gameNotInProgressCounterWriteOnly, 0.
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);
        // swos.asm:103981 — mov cameraDirection, -1.
        Memory.WriteWord(Memory.Addr.cameraDirection, -1);

        // swos.asm:103982-103988 — same teamStarting==teamPlayingUp branch.
        short teamStarting   = Memory.ReadSignedWord(Memory.Addr.teamStarting);
        short teamPlayingUp  = Memory.ReadSignedWord(Memory.Addr.teamPlayingUp);
        int lastTeam = (teamStarting == teamPlayingUp) ? TeamData.TopBase : TeamData.BottomBase;
        Memory.WriteDword(Memory.Addr.lastTeamPlayedBeforeBreak, lastTeam);

        // swos.asm:103993-103994.
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);

        // swos.asm:103995.
        TeamPort.StopAllPlayers();

        // swos.asm:103996-103997.
        Memory.WriteWord(Memory.Addr.cameraXVelocity, 0);
        Memory.WriteWord(Memory.Addr.cameraYVelocity, 0);
    }

    // Mechanical partial port of StartPenalties (swos.asm:100500-100560).
    //
    // Triggered when extra time ended tied. Snapshots stats counters, resets
    // shootout-specific counters, picks teamPlayingUp / teamStarting randomly,
    // and (in the original) clears each sprite's cards + sentAway flags via two
    // team-sprite loops. We port the scalar state setup verbatim; the per-sprite
    // loops (swos.asm:100533-100557) require team{1,2}SpritesTable addresses
    // that aren't modeled — stub-noted but skipped so a future sprite-table
    // port slots in cleanly. NextPenalty() at swos.asm:100558 likewise stays as
    // a TODO since it kicks off the per-pen state machine that's far larger.
    //
    // Source: external/swos-port/swos/swos.asm:100500.
    private static void StartPenalties()
    {
        // swos.asm:100501 — mov penaltiesState, -1.
        Memory.WriteWord(Memory.Addr.penaltiesState, -1);

        // swos.asm:100502-100505 — snapshot pre-shootout stats so they can be
        // restored after the shootout for the result screen.
        short s1 = Memory.ReadSignedWord(Memory.Addr.statsTeam1Goals);
        short s2 = Memory.ReadSignedWord(Memory.Addr.statsTeam2Goals);
        Memory.WriteWord(Memory.Addr.savedTeam1Goals, s1);
        Memory.WriteWord(Memory.Addr.savedTeam2Goals, s2);

        // swos.asm:100506-100511 — zero the live scoreline + stats counters
        // so the shootout draws its own score from zero.
        Memory.WriteWord(Memory.Addr.statsTeam1Goals,   0);
        Memory.WriteWord(Memory.Addr.team1GoalsDigit1,  0);
        Memory.WriteWord(Memory.Addr.team1GoalsDigit2,  0);
        Memory.WriteWord(Memory.Addr.statsTeam2Goals,   0);
        Memory.WriteWord(Memory.Addr.team2GoalsDigit1,  0);
        Memory.WriteWord(Memory.Addr.team2GoalsDigit2,  0);

        // swos.asm:100512-100513 — clear penalty goal counters.
        Memory.WriteWord(Memory.Addr.team1PenaltyGoals, 0);
        Memory.WriteWord(Memory.Addr.team2PenaltyGoals, 0);

        // swos.asm:100514-100518 — Rand & 1 + 1 → teamPlayingUp.
        Memory.WriteWord(Memory.Addr.teamPlayingUp, (short)((Rng.NextByte() & 1) + 1));
        // swos.asm:100519-100523 — Rand & 1 + 1 → teamStarting.
        Memory.WriteWord(Memory.Addr.teamStarting,  (short)((Rng.NextByte() & 1) + 1));

        // swos.asm:100524-100527 — init per-team penalty cursors + counters.
        Memory.WriteWord(Memory.Addr.team1PenaltyShooterIndex, 11);
        Memory.WriteWord(Memory.Addr.team2PenaltyShooterIndex, 11);
        Memory.WriteWord(Memory.Addr.team1PenaltyAttempts, 0);
        Memory.WriteWord(Memory.Addr.team2PenaltyAttempts, 0);

        // swos.asm:100528-100529 — engage shootout mode.
        Memory.WriteWord(Memory.Addr.playingPenalties, 1);
        Memory.WriteWord(Memory.Addr.dontShowScorers, 1);

        // swos.asm:100530-100557 — clear Sprite.cards + sentAway on every
        // player on both teams (makes red-carded players eligible to shoot
        // pens). PlayerSprite slot range covers both teams.
        ResetPenaltyShooters();

        // swos.asm:100558 — call NextPenalty (decides if shootout finishes
        // and, if not, sets up the next pen). Decision block ported; per-pen
        // FSM init is the remaining TODO inside NextPenalty.
        NextPenalty();
    }

    // Partial mechanical port of game.cpp:396-421 — initTeamsData() scalar
    // reset block. The original then walks both teams' TeamGeneralInfo +
    // TeamSpritesTable to rewire pointers / pre-game state (game.cpp:422-
    // ~660); that part requires the in-game-team layout which is set up
    // once by TeamDataLoader at match boot and isn't safely re-runnable
    // here yet. The scalar resets ARE safely re-runnable and matter at
    // ET start because EndFirstHalf-style state (currentScorer, penalty
    // flag, stoppage timers, etc.) leaks through into the ET half if we
    // don't clear it.
    //
    // Source: external/swos-port/src/game/game.cpp:396-421
    private static void InitTeamsDataForExtraTime()
    {
        // game.cpp:398-421 — all of the scalar resets:
        Memory.WriteDword(Memory.Addr.currentScorer, 0);                   // 398
        Memory.WriteDword(Memory.Addr.lastPlayerBeforeGoalkeeper, 0);      // 399
        Memory.WriteWord(Memory.Addr.goalScored, 0);                       // 400
        Memory.WriteWord(Memory.Addr.runSlower, 0);                        // 401
        Memory.WriteWord(Memory.Addr.whichCard, 0);                        // 402
        Memory.WriteDword(Memory.Addr.bookedPlayer, 0);                    // 403
        Memory.WriteWord(Memory.Addr.playerHadBall, 0);                    // 404
        Memory.WriteDword(Memory.Addr.lastKeeperPlayed, 0);                // 405
        Memory.WriteDword(Memory.Addr.lastTeamPlayed, 0);                  // 406
        Memory.WriteDword(Memory.Addr.lastPlayerPlayed, 0);                // 407
        Memory.WriteWord(Memory.Addr.penalty, 0);                          // 408
        Memory.WriteWord(Memory.Addr.goalCameraMode, 0);                   // 409
        Memory.WriteWord(Memory.Addr.goalOut, 0);                          // 410
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);// 411
        Memory.WriteWord(Memory.Addr.fireBlocked, 0);                      // 412
        Memory.WriteDword(Memory.Addr.lastTeamPlayedBeforeBreak, 0);       // 413
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);               // 414
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);              // 415
        Memory.WriteWord(Memory.Addr.stoppageEventTimer, 0);               // 416
        Memory.WriteWord(Memory.Addr.inGameCounter, 0);                    // 417
        Memory.WriteWord(Memory.Addr.gameStatePl, 100);                    // 418
        Memory.WriteWord(Memory.Addr.gameState, 100);                      // 419 ST_GAME_IN_PROGRESS
        // 420 breakState — global not yet declared in Memory.Addr; skipped
        // until break-state machine port lands.
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);                 // 421

        // game.cpp:422-660 — per-team field reseat (TeamGeneralInfo, sprite
        // tables, controlled players, etc.) requires structures not modeled
        // here. Boot-time TeamDataLoader already wired those.
    }

    // Mechanical port of the two per-team sprite-clear loops at
    // swos.asm:100533-100557 (StartPenalties tail). The asm walks
    // team1SpritesTable / team2SpritesTable (11 pointers each) and zeros
    // Sprite.cards + Sprite.sentAway on every player so red-carded players
    // are eligible to shoot pens.
    //
    // PlayerSprite slots are laid out as { TopGoalie..TopForward,
    // BottomGoalie..BottomForward } = slots 0..10 then 11..21, matching the
    // SpritesTable layout. The loop count is 10 (`mov D0, 10` then `dec /
    // jns`) which iterates 11 times (10..0 inclusive).
    //
    // Source: external/swos-port/swos/swos.asm:100533-100557
    private static void ResetPenaltyShooters()
    {
        for (int slot = 0; slot < PlayerSprite.TotalSlots; slot++)
        {
            int spriteAddr = PlayerSprite.Base(slot);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffCards, 0);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffSentAway, 0);
        }
    }

    // Partial mechanical port of game.cpp:828-885 — nextPenalty().
    //
    // The original picks the next shooter, positions ball + sprite, and (when
    // the shootout is decided) restores the saved stats counters + calls
    // PlayersLeavingPitch. We port the SHOOTOUT-OVER decision (game.cpp:828-
    // 885) which determines whether to finish. The per-pen FSM init (sprite
    // setup, kicker pick, ball reset) at game.cpp:887+ requires
    // PlayersLeavingPitch + sprite positioning that isn't ported yet —
    // leaves a TODO.
    //
    // Decision logic (from game.cpp:828-885):
    //   total = team1Attempts + team2Attempts
    //   if (total >= 10)
    //       if (team1Attempts == team2Attempts) check team1Goals - team2Goals
    //           if equal: continue (sudden death)
    //           else: finish
    //       else: continue
    //   else (total < 10):
    //       gap1 = (5 - team1Attempts) + team1Goals
    //       if (gap1 < team2Goals)   finish (team2 already wins)
    //       gap2 = (5 - team2Attempts) + team2Goals
    //       if (gap2 < team1Goals)   finish (team1 already wins)
    //
    // Source: external/swos-port/src/game/game.cpp:828-885
    //
    // Exposed `internal` so SetPieces.AdvancePenaltiesTimer (the per-tick
    // `penaltiesTimer == m_penaltiesInterval` check ported from
    // gameLoop.cpp:449-469) can invoke it to advance to the next pen kicker.
    internal static void NextPenalty()
    {
        short t1Attempts = Memory.ReadSignedWord(Memory.Addr.team1PenaltyAttempts);
        short t2Attempts = Memory.ReadSignedWord(Memory.Addr.team2PenaltyAttempts);
        short t1Goals    = Memory.ReadSignedWord(Memory.Addr.team1PenaltyGoals);
        short t2Goals    = Memory.ReadSignedWord(Memory.Addr.team2PenaltyGoals);

        int total = t1Attempts + t2Attempts;
        bool finish = false;

        if (total >= 10)
        {
            // game.cpp:850-876 — both teams have shot >= 5; only finish when
            // they've taken the same number of pens AND scores differ.
            if (t1Attempts == t2Attempts && t1Goals != t2Goals)
                finish = true;
        }
        else
        {
            // game.cpp:887-940 — early decisive check: best-case for the
            // trailing team can't catch up.
            // gap1 = (5 - team1Attempts) + team1Goals  (max final t1)
            // if (gap1 < t2Goals) — team1 can't catch team2, finish.
            int gap1 = (5 - t1Attempts) + t1Goals;
            if (gap1 < t2Goals)
                finish = true;
            else
            {
                int gap2 = (5 - t2Attempts) + t2Goals;
                if (gap2 < t1Goals)
                    finish = true;
            }
        }

        if (finish)
        {
            // game.cpp:878-885 — l_finish_penalties.
            Memory.WriteWord(Memory.Addr.playingPenalties, 0);
            short savedT1 = Memory.ReadSignedWord(Memory.Addr.savedTeam1Goals);
            Memory.WriteWord(Memory.Addr.statsTeam1Goals, savedT1);
            short savedT2 = Memory.ReadSignedWord(Memory.Addr.savedTeam2Goals);
            Memory.WriteWord(Memory.Addr.statsTeam2Goals, savedT2);
            // game.cpp:884 — playersLeavingPitch(). Mechanical port now lives
            // in GameLoop.PlayersLeavingPitch (gameState → 24,
            // stoppageEventTimer = 275, all stoppage / camera vars cleared,
            // stateGoal = 0). The full leaving-pitch animation FSM still
            // waits on a future port, but the state transition above is the
            // gameplay-visible part that lets the post-shootout result panel
            // sequence the next frame.
            GameLoop.PlayersLeavingPitch();
            return;
        }

        // game.cpp:942-1078 — l_next_penalty: per-pen FSM init.
        //
        // 1. Reset ball Z, flip teamPlayingUp / teamStarting (neg + +3 = swap).
        // 2. InitTeamsData (scalar resets — full per-team reseat skipped, see
        //    InitTeamsDataForExtraTime for the coverage map).
        // 3. Slam scalar state: gameState=ST_PENALTIES (31), gameStatePl=101,
        //    breakCameraMode=-1, cameraDirection=0, playerTurnFlags=0x83 (131),
        //    foul coords = (336, 187), lastTeamPlayedBeforeBreak = bottomTeam.
        // 4. Stop all players, zero camera velocities, zero penaltiesTimer.
        // 5. Pick which team's PenaltyShooterIndex to advance: bottomTeamData
        //    has teamNumber field; if teamNumber==1 use team1PenaltyShooterIndex,
        //    else team2's. Decrement; on hit-zero wrap to 10 (skip goalkeeper @
        //    index 0). Read selected player pointer from team's spritesTable
        //    (bottomTeamData.spritesTable + selectedIndex*4) → penaltyShooterSprite.
        // 6. Bump the corresponding team's PenaltyAttempts counter.

        // game.cpp:943 — mov word ptr ballSprite.z+2, 0 (zeroes high word of Z).
        Memory.WriteWord(BallSprite.Base + BallSprite.OffZ + 2, 0);

        // game.cpp:944-956 — neg teamPlayingUp ; add teamPlayingUp, 3 (swap 1↔2).
        short tpu = Memory.ReadSignedWord(Memory.Addr.teamPlayingUp);
        Memory.WriteWord(Memory.Addr.teamPlayingUp, (short)(3 - tpu));
        // game.cpp:957-972 — neg teamStarting ; add teamStarting, 3.
        short ts = Memory.ReadSignedWord(Memory.Addr.teamStarting);
        Memory.WriteWord(Memory.Addr.teamStarting, (short)(3 - ts));

        // game.cpp:973 — mov hideBall, 0.
        Memory.WriteWord(Memory.Addr.hideBall, 0);

        // game.cpp:974 — call InitTeamsData. Scalar reset block ported; per-team
        // field reseat still pending. See InitTeamsDataForExtraTime.
        InitTeamsDataForExtraTime();

        // game.cpp:975 — mov gameState, ST_PENALTIES (31).
        Memory.WriteWord(Memory.Addr.gameState, 31);
        // game.cpp:976 — mov breakCameraMode, -1.
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        // game.cpp:977 — mov cameraDirection, 0.
        Memory.WriteWord(Memory.Addr.cameraDirection, 0);
        // game.cpp:978 — mov byte ptr playerTurnFlags, 0x83 (131).
        Memory.WriteByte(Memory.Addr.playerTurnFlags, 131);
        // game.cpp:979 — mov foulXCoordinate, 336.
        Memory.WriteWord(Memory.Addr.foulXCoordinate, 336);
        // game.cpp:980 — mov foulYCoordinate, 187.
        Memory.WriteWord(Memory.Addr.foulYCoordinate, 187);
        // game.cpp:981 — mov gameStatePl, 101 (ST_STOPPED).
        Memory.WriteWord(Memory.Addr.gameStatePl, kGameStatePlStopped);
        // game.cpp:982 — mov gameNotInProgressCounterWriteOnly, 0.
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);

        // game.cpp:983-985 — A6 = offset bottomTeamData ; lastTeamPlayedBeforeBreak = A6.
        // (Both the original and our port use bottomTeamData unconditionally as the
        // A6 anchor for the rest of the FSM init — the asm-level cursor.)
        int a6 = TeamData.BottomBase;
        Memory.WriteDword(Memory.Addr.lastTeamPlayedBeforeBreak, a6);

        // game.cpp:986-987 — clear stoppage timers.
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);

        // game.cpp:988 — call StopAllPlayers.
        TeamPort.StopAllPlayers();

        // game.cpp:989-990 — zero camera velocities.
        Memory.WriteWord(Memory.Addr.cameraXVelocity, 0);
        Memory.WriteWord(Memory.Addr.cameraYVelocity, 0);

        // game.cpp:991 — mov penaltiesTimer, 0.
        Memory.WriteWord(Memory.Addr.penaltiesTimer, 0);

        // game.cpp:992-1006 — A0 = offset team1PenaltyShooterIndex if
        // bottomTeamData.teamNumber == 1 else A0 = team2PenaltyShooterIndex.
        // ESI = A6 (= bottomTeamData).
        short bottomTeamNumber = Memory.ReadSignedWord(a6 + TeamData.OffTeamNumber);
        int shooterIndexAddr = (bottomTeamNumber == 1)
            ? Memory.Addr.team1PenaltyShooterIndex
            : Memory.Addr.team2PenaltyShooterIndex;

        // game.cpp:1008-1024 — decrement [A0] (the shooter index); if reaches 0
        // (i.e. would point at the goalkeeper at slot 0), wrap to 10.
        short shooterIdx = Memory.ReadSignedWord(shooterIndexAddr);
        shooterIdx = (short)(shooterIdx - 1);
        if (shooterIdx == 0)
            shooterIdx = 10;
        Memory.WriteWord(shooterIndexAddr, shooterIdx);

        // game.cpp:1026-1040 — penaltyShooterSprite = bottomTeamData.spritesTable[shooterIdx].
        // shl D0, 2 → byte offset = shooterIdx * 4 (4-byte pointers in the table).
        int spritesTable = Memory.ReadSignedDword(a6 + TeamData.OffPlayers);
        int shooterSprite = Memory.ReadSignedDword(spritesTable + shooterIdx * 4);
        Memory.WriteDword(Memory.Addr.penaltyShooterSprite, shooterSprite);

        // game.cpp:1041-1078 — if bottomTeamData.teamNumber == 1 then bump
        // team1PenaltyAttempts ; else bump team2PenaltyAttempts.
        if (bottomTeamNumber == 1)
        {
            short t1Att = Memory.ReadSignedWord(Memory.Addr.team1PenaltyAttempts);
            Memory.WriteWord(Memory.Addr.team1PenaltyAttempts, (short)(t1Att + 1));
        }
        else
        {
            short t2Att = Memory.ReadSignedWord(Memory.Addr.team2PenaltyAttempts);
            Memory.WriteWord(Memory.Addr.team2PenaltyAttempts, (short)(t2Att + 1));
        }
    }

    // Partial port of external/swos-port/src/sprites/sprites.cpp:80-86 —
    // getSprite(spriteIndex).widthF. The full path loads the menu sprite
    // descriptor stream from MENUSPR.DAT (or the Amiga MENUFNTS atlas) and
    // returns the per-image width. The descriptor loader is not yet wired
    // (see "Sprite descriptor stream" memory-port note + tools/sprite-
    // descriptor-extract README).
    //
    // Until then, supply the widths for the only sprite-indices DrawGameTime
    // actually queries: the big-time-digit family (kBigTimeDigitSprite0..9
    // = 331..340) plus the "8 mins" suffix (kTimeSprite8Mins = 328). These
    // were measured off `tools/sprite-descriptor-extract/` output for the PC
    // SPRITE.DAT atlas — digits 0-9 are uniform 13 px wide except '1' (8 px),
    // matching SWOS's mono-digit clock layout.
    //
    // The asm-level call site (DrawGameTimeImpl above) only ever passes
    // kBigTimeDigitSprite0 + 8 (the constant width quoted for digit '8'),
    // so the per-digit branch is presently unused but documented for the
    // future drawHalfTimeScore + drawFullTimeScore call sites.
    //
    // Source: external/swos-port/src/sprites/sprites.cpp:80-86 (getSprite)
    //         external/swos-port/src/sprites/sprites.h:38-42 (sprite indices)
    private static int GetSpriteWidth(int spriteIndex)
    {
        // kBigTimeDigitSprite0..9 — digits in the big clock font.
        if (spriteIndex >= kBigTimeDigitSprite0 && spriteIndex <= kBigTimeDigitSprite0 + 9)
        {
            int digit = spriteIndex - kBigTimeDigitSprite0;
            // Digit '1' is narrower than the rest in SWOS's variable-width font.
            return digit == 1 ? 8 : 13;
        }
        // kTimeSprite8Mins — the "8MINS" half-length banner painted next to
        // the clock. 22 px from SPRITE.DAT descriptor.
        if (spriteIndex == kTimeSprite8Mins) return 22;
        // Fallback for unsupported indices — matches the pre-port stub value
        // so any newly-added call site sees deterministic output.
        return 13;
    }

    // External: renderSprites.cpp drawMenuSprite — pushes the sprite into the
    // menu-overlay render list.
    // TODO from external/swos-port/src/sprites/renderSprites.cpp:drawMenuSprite
    private static void StubDrawMenuSprite(int sprite, int x, int y) { /* TODO */ }

    // PlayerInfo struct field accessors. Source:
    // external/swos-port/src/swos/swos.h:162-188 (PlayerInfo, 61 bytes).
    // Field offsets verified against the struct layout: substituted, index,
    // goalsScored, shirtNumber, position, face, isInjured, field_{7..9},
    // cards@10, field_B@11, shortName[15]@12, passing..goalieSkill (8 bytes
    // 27..34), injuriesBitfield@35, halfPlayed@36, face2, fullName[23].
    private static int  ReadPlayerInfoCards(int playerInfoAddr) =>
        playerInfoAddr == 0 ? 0 : Memory.ReadByte(playerInfoAddr + 10);
    private static int  ReadPlayerInfoHalfPlayed(int playerInfoAddr) =>
        playerInfoAddr == 0 ? 0 : Memory.ReadByte(playerInfoAddr + 36);
    private static void WritePlayerInfoHalfPlayed(int playerInfoAddr, int v)
    {
        if (playerInfoAddr != 0) Memory.WriteByte(playerInfoAddr + 36, v);
    }

    // ========================================================================
    // initMatch() helpers — game.cpp:1361-1401, 1237-1247.
    //
    // These five static functions live in the swos-port `initMatch()` flow
    // (game.cpp:59-106) but had no OpenSWOS port until now. They're tiny,
    // table-driven, and each is called exactly once per match boot — yet
    // without them the cards/coin-toss/pitch-influence/save-restore semantics
    // are silently wrong (cards never fire because playerCardChance stays at
    // 0; teamPlayingUp/teamStarting are hard-locked to 1, so the kick-off
    // coin-toss is deterministic; ball physics never adapts to pitch
    // condition; saveTeams/restoreTeams are no-ops, so a cancel-to-menu
    // restart sees mutated in-game team state).
    //
    // Wired from Main.InitSwosVmFromMatchSetup() in the same order
    // game.cpp:60-93 runs them.
    // ========================================================================

    // Mechanical port of game.cpp:1361-1379 — initPlayerCardChance().
    //
    // Picks the per-match card-chance threshold from a 4 × 16 table indexed
    // by gameLengthInGame (0=3min, 1=5min, 2=7min, 3=10min) plus a randomised
    // column 0..15. Compared against `(currentGameTick >> 1) & 15` at each
    // tackle (PlayerTackle.cs:328) — greater = card handed out, lesser-or-equal
    // = clean foul. Longer matches use a smaller threshold (more cards expected)
    // so total cards-per-match stays roughly constant regardless of length.
    //
    // Source: external/swos-port/src/game/game.cpp:1361-1379.
    public static void InitPlayerCardChance()
    {
        // game.cpp:1367-1372 — kPlayerCardChancesPerGameLength[4][16]
        // (laid out row-major in the original; transposed here as 4 rows of
        // 16 ints for direct lookup).
        // Row 0 = 3-min half, row 1 = 5-min, row 2 = 7-min, row 3 = 10-min.
        // Within a row the column index is (Rand() & 0x1E) >> 1 — i.e. an
        // even-only sample of bits 1..4, producing a 0..15 random column.
        // Verbatim values; do NOT massage.
        int[,] kPlayerCardChancesPerGameLength = new int[4, 16]
        {
            { 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 7, 7, 8, 9, 10 }, // 3 min
            { 2, 2, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 5, 5,  6 }, // 5 min
            { 1, 1, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 4, 4,  4 }, // 7 min
            { 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3,  3 }, // 10 min
        };

        // game.cpp:1374 — assert(swos.gameLengthInGame <= 3). Clamp defensively
        // instead of crashing: malformed memory shouldn't take the sim down.
        short row = Memory.ReadSignedWord(Memory.Addr.gameLengthInGame);
        if (row < 0) row = 0;
        if (row > 3) row = 3;

        // game.cpp:1376 — (SWOS::rand() & 0x1E) >> 1 → 0..15 column index.
        int col = (Rng.NextByte() & 0x1E) >> 1;

        // game.cpp:1378 — swos.playerCardChance = chanceTable[chanceIndex].
        Memory.WriteWord(Memory.Addr.playerCardChance,
                         kPlayerCardChancesPerGameLength[row, col]);
    }

    // Mechanical port of game.cpp:1381-1385 — determineStartingTeamAndTeamPlayingUp().
    //
    // Coin-toss for which team kicks off (teamStarting) and which team plays
    // up-pitch (teamPlayingUp). Both are 1 or 2; downstream uses 1==top team
    // and 2==bottom team. EndFirstHalf flips them via `3 - x`. Until now both
    // were hard-locked to 1 at boot (Memory.cs:1512-1513), so the top team
    // always kicked off and always played up — a tell that betrayed the sim
    // against any rule that depends on randomised initial sides.
    //
    // Source: external/swos-port/src/game/game.cpp:1381-1385.
    public static void DetermineStartingTeamAndTeamPlayingUp()
    {
        // game.cpp:1383 — teamPlayingUp = (Rand() & 1) + 1. Rng.NextByte()'s
        // low bit is the masked sample; +1 maps 0/1 → 1/2.
        Memory.WriteWord(Memory.Addr.teamPlayingUp, (short)((Rng.NextByte() & 1) + 1));
        // game.cpp:1384 — teamStarting = (Rand() & 1) + 1. Two separate Rand
        // calls, NOT one with both bits read; preserves the lockstep-friendly
        // 1-byte-per-decision RNG cadence.
        Memory.WriteWord(Memory.Addr.teamStarting,  (short)((Rng.NextByte() & 1) + 1));
    }

    // Mechanical port of game.cpp:1387-1401 — initPitchBallFactors().
    //
    // Three per-pitch-type tables that drive ball physics:
    //   - kPitchBallSpeedInfluence  — added each tick to ball speed by
    //     BallUpdate (read via Memory.Addr.pitchBallSpeedFactor at
    //     BallUpdate.cs:1092). Negative on Frozen/Hard pitches (slower),
    //     positive on Muddy/Wet.
    //   - kBallSpeedBounceFactorTable — XY bounce damping after the ball
    //     hits the pitch surface (BallUpdate.cs:239). Lower = ball loses
    //     more horizontal energy per bounce.
    //   - kBallBounceFactorTable     — Z bounce reflection factor
    //     (BallUpdate.cs:266). Lower = ball bounces less high.
    //
    // PC mode uses the kPitchBallSpeedInfluence row; Amiga the
    // kPitchBallSpeedInfluenceAmiga row. OpenSWOS hard-locks PC mode at boot
    // (CLAUDE.md "Engine and rendering" + PortTuning.cs:23), so the Amiga
    // table is preserved as a comment for future amigaMode toggle.
    //
    // Pitch-type index 0..6: Frozen, Muddy, Wet, Soft, Normal, Dry, Hard.
    // Until a pre-match menu surfaces pitch condition, BallSim.CurrentPitchType
    // (default 4 = Normal) is the single source of truth.
    //
    // Source: external/swos-port/src/game/game.cpp:1387-1401.
    public static void InitPitchBallFactors()
    {
        // game.cpp:1389-1392 — verbatim tables.
        int[] kPitchBallSpeedInfluence       = { -3, 4, 1, 0, 0, -1, -1 };
        // PC mode — amiga branch retained for parity but unused while
        // PortTuning hard-locks PC.
        // int[] kPitchBallSpeedInfluenceAmiga = { -2, 2, 3, 0, 0, -1, -1 };
        int[] kBallSpeedBounceFactorTable    = { 24, 80, 80, 72, 64, 40, 32 };
        int[] kBallBounceFactorTable         = { 88, 112, 104, 104, 96, 88, 80 };

        // game.cpp:1394-1395 — pitchType = getPitchType(); assert(0..6).
        // BallSim.CurrentPitchType is the live source (declared on the BallSim
        // static class, NOT the BallState struct — line 127 of BallState.cs
        // sits inside the `public static class BallSim` block that starts at
        // line 33). Clamp on read so a stale value can't crash the sim.
        int pitchType = OpenSwos.Sim.BallSim.CurrentPitchType;
        if (pitchType < 0) pitchType = 0;
        if (pitchType > 6) pitchType = 6;

        // game.cpp:1398-1400 — three Memory writes. swos-port assigns into
        // `swos.*` C++ globals; we mirror via Memory.Addr.* into the same
        // 16-bit slots that BallUpdate reads each tick.
        Memory.WriteWord(Memory.Addr.pitchBallSpeedFactor,  kPitchBallSpeedInfluence[pitchType]);
        Memory.WriteWord(Memory.Addr.ballSpeedBounceFactor, kBallSpeedBounceFactorTable[pitchType]);
        Memory.WriteWord(Memory.Addr.ballBounceFactor,      kBallBounceFactorTable[pitchType]);
    }

    // Mechanical port of game.cpp:1237-1247 — saveTeams() / restoreTeams().
    //
    // Snapshots both InGameTeam structs before a match starts so a mid-game
    // cancel-to-menu can restore the pristine pre-match state (player skills,
    // injuries, tactics index, etc.). C++ uses struct-assignment on the
    // 1704-byte TeamGame; we copy the 1024-byte memory slot Memory.cs reserves
    // for each side (team1InGameTeamHeader / team2InGameTeamHeader). 1024 is
    // larger than the live struct content per Memory.cs:1094 ("16 PlayerInfo
    // × 61 = 976 bytes per team. Plus 42-byte header") so the snapshot covers
    // all fields touched by the port.
    //
    // initMatch() calls saveTeams() or restoreTeams() based on its
    // `saveOrRestoreTeams` bool (game.cpp:61). Match boot always calls save;
    // a replay-restart or post-cancel path passes false to restore.
    //
    // Source: external/swos-port/src/game/game.cpp:1237-1247.
    private const int kInGameTeamSlotBytes = 1024;
    private static readonly byte[] m_topTeamSaved    = new byte[kInGameTeamSlotBytes];
    private static readonly byte[] m_bottomTeamSaved = new byte[kInGameTeamSlotBytes];

    // game.cpp:1237-1241 — m_topTeamSaved = swos.topTeamInGame; m_bottomTeamSaved = swos.bottomTeamInGame.
    public static void SaveTeams()
    {
        var src = Memory.View(Memory.Addr.team1InGameTeamHeader, kInGameTeamSlotBytes);
        src.CopyTo(m_topTeamSaved);
        src = Memory.View(Memory.Addr.team2InGameTeamHeader, kInGameTeamSlotBytes);
        src.CopyTo(m_bottomTeamSaved);
    }

    // game.cpp:1243-1247 — swos.topTeamInGame = m_topTeamSaved; swos.bottomTeamInGame = m_bottomTeamSaved.
    public static void RestoreTeams()
    {
        for (int i = 0; i < kInGameTeamSlotBytes; i++)
            Memory.WriteByte(Memory.Addr.team1InGameTeamHeader + i, m_topTeamSaved[i]);
        for (int i = 0; i < kInGameTeamSlotBytes; i++)
            Memory.WriteByte(Memory.Addr.team2InGameTeamHeader + i, m_bottomTeamSaved[i]);
    }

    // Mechanical port of game.cpp:1403-1448 — initGameVariables().
    //
    // Per-match init function: zeroes match-scoped runtime state at the very
    // start of every match (so a second-leg restart, replay, or post-cancel
    // reboot doesn't inherit the previous match's scoreline / camera velocity
    // / pl1Fire / etc.). Until now Memory.Init() did most of the same writes
    // at process boot — but Memory.Init runs ONCE; this runs PER MATCH.
    // (The current OpenSWOS smoke test redoes Memory.Init each match via
    //  Main.InitSwosVmFromMatchSetup, so the gap was hidden there. The
    //  swos-port flow is per-match without the full reset.)
    //
    // Order matches game.cpp:1405-1447 line-for-line. Two-leg-tie branch at
    // line 1419-1422 honours team{1,2}GoalsFirstLeg if `secondLeg` is set.
    //
    // The two team-data scrub blocks (1430-1431) zero TeamData from +24 to
    // end, preserving the first 24 bytes (opponentsTeam, playerNumber,
    // playerCoachNumber, isPlCoach, inGameTeamPtr, teamStatsPtr, teamNumber,
    // players-table) which were set by initializeIngameTeams BEFORE
    // initGameVariables fires.
    //
    // The team-stats-data memset (1427-1428) zeroes the 7-word TeamStatsData
    // record per team. swos.h:212-221.
    //
    // gameRandValue (1442 — written in the C++ outside initGameVariables,
    // at game.cpp:75, immediately after initPlayerCardChance). We bundle
    // the write here because it shares the same per-match-reset contract
    // and we want a single porting hook for callers.
    //
    // Source: external/swos-port/src/game/game.cpp:1403-1448.
    public static void InitGameVariables()
    {
        // game.cpp:1405-1409 — match-state flags.
        Memory.WriteWord(Memory.Addr.playingPenalties,           0);
        Memory.WriteWord(Memory.Addr.dontShowScorers,            0);
        Memory.WriteWord(Memory.Addr.statsTimer,                 0);
        Memory.WriteWord(Memory.Addr.g_waitForPlayerToGoInTimer, 0);
        Memory.WriteWord(Memory.Addr.g_substituteInProgress,     0);

        // game.cpp:1410-1415 — display digits + stats counters.
        Memory.WriteWord(Memory.Addr.statsTeam1Goals,   0);
        Memory.WriteWord(Memory.Addr.team1GoalsDigit1,  0);
        Memory.WriteWord(Memory.Addr.team1GoalsDigit2,  0);
        Memory.WriteWord(Memory.Addr.statsTeam2Goals,   0);
        Memory.WriteWord(Memory.Addr.team2GoalsDigit1,  0);
        Memory.WriteWord(Memory.Addr.team2GoalsDigit2,  0);

        // game.cpp:1417-1422 — total-goals; honour first-leg aggregate.
        Memory.WriteWord(Memory.Addr.team1TotalGoals, 0);
        Memory.WriteWord(Memory.Addr.team2TotalGoals, 0);
        if (Memory.ReadSignedWord(Memory.Addr.secondLeg) != 0)
        {
            Memory.WriteWord(Memory.Addr.team1TotalGoals,
                             Memory.ReadSignedWord(Memory.Addr.team1GoalsFirstLeg));
            Memory.WriteWord(Memory.Addr.team2TotalGoals,
                             Memory.ReadSignedWord(Memory.Addr.team2GoalsFirstLeg));
        }

        // game.cpp:1424-1425 — substitution counters.
        Memory.WriteWord(Memory.Addr.team1NumSubs, 0);
        Memory.WriteWord(Memory.Addr.team2NumSubs, 0);

        // game.cpp:1427-1428 — `memset(&team{1,2}StatsData, 0, sizeof(TeamStatsData))`.
        // TeamStatsData is 14 bytes (7 × word: ballPossession, cornersWon,
        // foulsConceded, bookings, sendingsOff, goalAttempts, onTarget).
        // We allocated 32 bytes per side in Memory.cs:539-540 to leave room
        // for padding; only the first 14 bytes hold semantic data, but we
        // zero the full 32 to match the C++ memset granularity (which only
        // touched 14 — using 32 is a safe over-zero, never a regression).
        for (int i = 0; i < 14; i++)
        {
            Memory.WriteByte(Memory.Addr.topTeamStatsData    + i, 0);
            Memory.WriteByte(Memory.Addr.bottomTeamStatsData + i, 0);
        }

        // game.cpp:1430-1431 — `memset((char *)&team{1,2}Data + 24, 0,
        // sizeof(team{1,2}Data) - 24)`. Preserve the first 24 bytes
        // (opponentsTeam, playerNumber, playerCoachNumber, isPlCoach,
        // inGameTeamPtr, teamStatsPtr, teamNumber, players — all set by
        // initializeIngameTeams BEFORE this call), zero everything else.
        // TeamData slot is 512 bytes (TopBase 0x4F900 → 0x4FB00). The live
        // struct content extends to OffSecondaryFire at +144 (TeamData.cs:83
        // notes "total struct size = 145"); zero from +24 to +144 inclusive
        // (121 bytes) to mirror the C++ memset's `sizeof(team) - 24`. We
        // intentionally cap at +144 not +511: the trailing slot bytes are
        // padding only and over-zeroing risks clobbering future fields we
        // might co-locate in that range. swos.h:331-405 defines the struct;
        // 145 is the verified upper bound.
        const int kTeamDataScrubFrom = 24;
        const int kTeamDataScrubTo   = 145;
        for (int i = kTeamDataScrubFrom; i < kTeamDataScrubTo; i++)
        {
            Memory.WriteByte(TeamData.TopBase    + i, 0);
            Memory.WriteByte(TeamData.BottomBase + i, 0);
        }

        // game.cpp:1433-1434 — goal-event latches.
        Memory.WriteWord(Memory.Addr.goalCounter, 0);
        Memory.WriteWord(Memory.Addr.stateGoal,   0);

        // game.cpp:1437 — `cameraXVelocity = cameraYVelocity = 0`.
        Memory.WriteWord(Memory.Addr.cameraXVelocity, 0);
        Memory.WriteWord(Memory.Addr.cameraYVelocity, 0);

        // game.cpp:1439-1440 — input fire flags. SWOS uses byte-typed slots
        // here (controls.cpp:359-360 also writes `swos.pl1Fire = 0` as
        // bytes). Our InputControls layer uses ic_pl1Fire / ic_pl2Fire word
        // slots; we clear those for the same effect.
        Memory.WriteWord(Memory.Addr.ic_pl1Fire, 0);
        Memory.WriteWord(Memory.Addr.ic_pl2Fire, 0);

        // game.cpp:1442 — `longFireFlag = longFireTime = 0`. menuControls.cpp
        // long-press accumulator.
        Memory.WriteWord(Memory.Addr.longFireFlag, 0);
        Memory.WriteWord(Memory.Addr.longFireTime, 0);

        // game.cpp:1444-1445 — clock counters.
        Memory.WriteWord(Memory.Addr.currentGameTick, 0);
        Memory.WriteWord(Memory.Addr.currentTick,     0);

        // game.cpp:1447 — `AI_turnDirection = 1`. Default to +1 (right turn)
        // so the first AI direction-decision lands consistently.
        Memory.WriteWord(Memory.Addr.AI_turnDirection, 1);

        // game.cpp:75 (logically part of the per-match init sequence, even
        // though it's outside initGameVariables in the C++). After all the
        // zeros above, sample one RNG byte and stash it into gameRandValue.
        // ApplyTeamTactics (swos.asm:37500) reads this slot, XORs into D1,
        // and pushes onto Randomize2. With this missing, any future port
        // (or the in-VM asm-mapped ApplyTeamTactics call) would consume a
        // freshly-zeroed slot and pin team tactics randomisation to a
        // deterministic-but-trivial seed.
        //
        // Cadence note: this is a single Rng.NextByte() — the SAME stream-1
        // call that pitch.cpp / initPlayerCardChance / etc. tap. The C++
        // calls `SWOS::rand()` (stream 1) here, not rand2, so we mirror that.
        Memory.WriteWord(Memory.Addr.gameRandValue, (short)Rng.NextByte());
    }
}
