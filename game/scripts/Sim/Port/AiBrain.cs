namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// CS0164: labels carried over from the asm-translated source for control-flow
// parity. Some are entry-only (referenced via fall-through) so the compiler
// can't see incoming gotos. We keep them so future agents can wire the
// stubbed sections without re-deriving the layout from the .cpp.
#pragma warning disable CS0164

// AI_SetControlsDirection — mechanically ported from
// external/swos-port/src/game/updatePlayers/updatePlayers.cpp:15980 (~3333 LOC).
//
// THIS IS THE LARGEST AI FUNCTION IN SWOS. It runs once per team per tick and
// decides:
//
//   - whether to early-out (substitutes menu, resetControls flag, off-tick)
//   - whether to "press fire" (kick / pass / dismiss-result)
//   - what direction to ask the controlled player to face
//   - what spin (left / right / none) to apply to ball-after-touch
//   - what after-touch strength to use (weak / medium / strong)
//
// The function shape is a giant if/else cascade driven by gameState.
// Phases (with l_label names mirroring swos-port comments):
//
//   ENTRY        L15980-16060  resetControls / counters / AI_rand reset / state snapshot
//   DISTANCE     L16078-16164  D6 = ball-distance-from-own-goal-line², D5 = angle
//   GAME_OVER    L16178-16307  end-of-match / halftime auto-press-space branch
//   SET_DIRECT   L16309-16448  l_check_if_result_shown / set-piece (free kick, foul, throw)
//   GAME_NOT_OVER L16450-16654 game in progress; gameState-dispatched throw / penalty / corner
//   KEEPERS_BALL L16656-16860  keeper-holds-ball + goal-scored hold-still branch
//   APPLY_AFTER  L16948-17081  apply after-touch (currentAllowedDirection)
//   TURN_UPDATE  L17083-17227  AI_turnDirection accumulator update (half-step turning)
//   GAME_IN_PROG L17229-17307  l_no_penalty / l_direction_updated entry into chase logic
//   THEMS_NEAR   L17333-17370  AI_DecideWhetherToTriggerFire branch (player near ball)
//   CHASE_LOGIC  L17372-17555  facing / ball-y / D6 bucket dispatch (chase / fall back)
//   COMPLEX_BALL L17600-18450  ball-acquisition + spin / after-touch decisions
//   FLIPPING     L18017-18105  l_decide_if_flipping_direction (180° turn-around)
//   OUR_CLOSEST  L18106-18207  l_our_player_closest path → tackle / fire
//   AFTER_TOUCH  L18510-18920  l_update_max_stoppage_time → AFTERTOUCH STRENGTH PICKER
//   NO_NEAR      L18919-19046  no-one near → switch controlled / random rotate
//   BALL_TOUCH   L19174-19306  l_ball_after_touch_allowed → spin tables → direction
//
// **Port scope (this commit)**: ENTRY + DISTANCE + GAME_OVER + SET_DIRECT + the
// first half of GAME_NOT_OVER + KEEPERS_BALL + APPLY_AFTER + TURN_UPDATE +
// GAME_IN_PROG skeleton + FLIPPING + OUR_CLOSEST + AFTER_TOUCH skeleton.
//
// **Stubs**: The deep "ball chase + after-touch decision" branches at L17600-18450
// and L18919-19046 are partial — they wire control flow to the right labels but
// some inner sub-branches are stubbed with `// TODO: ASM lines NNN-NNN`. Other
// agents are porting AI_Kick / AI_SetDirectionTowardOpponentsGoal / etc. (already
// in AiHelpers.cs).
//
// Hard rules:
//   - mechanical 1:1 with the asm — preserve gotos, magic numbers, dead writes
//   - cite source line every block
//   - no floats
//   - no inputs lost: every label has a target (use `// FALLTHROUGH stub` markers
//     if we drop into a not-yet-ported section).
//
// asm register/memory convention used by swos-port translations (same as AiHelpers):
//   A1..A6 = absolute addresses; D0..D7 = working registers.
public static class AiBrain
{
    // Telemetry: per-branch entry counters for the four asm sites that
    // commit `normalFire = 1`. Exposed via public properties so the smoke
    // test can verify the AI's real fire paths are reached.
    //   - SetCseg850F9: reaches cseg_85178 fall-through (updatePlayers.cpp:18487)
    //   - SetCseg84FCA: reaches cseg_84FCA           (updatePlayers.cpp:18229)
    //   - SetActivateFire: reaches l_activate_normal_fire (updatePlayers.cpp:18691)
    //   - SetActivateFireRewrite: rewrites at 18746 (dead store)
    private static int s_fireSiteCseg850F9 = 0;
    private static int s_fireSiteCseg84FCA = 0;
    private static int s_fireSiteActivate  = 0;
    // Debug: gate entries. trigPlayerNear = enters l_theres_a_player_near.
    // trigCseg84A85 = passes the d6/Y guards to reach cseg_84A85.
    // trigCseg850F9Gate = passes the angle window in cseg_84A85.
    // trigCseg84B5B = reaches cseg_84B5B (post-cseg_84AEB).
    // trigCseg84F4B = reaches cseg_84F4B.
    private static int s_trigPlayerNear = 0;
    private static int s_trigCseg84A85  = 0;
    private static int s_trigCseg850F9Gate = 0;
    private static int s_trigCseg84B5B  = 0;
    private static int s_trigCseg84F4B  = 0;
    public static int FireSiteCseg850F9 => s_fireSiteCseg850F9;
    public static int FireSiteCseg84FCA => s_fireSiteCseg84FCA;
    public static int FireSiteActivate  => s_fireSiteActivate;
    public static int TrigPlayerNear    => s_trigPlayerNear;
    public static int TrigCseg84A85     => s_trigCseg84A85;
    public static int TrigCseg850F9Gate => s_trigCseg850F9Gate;
    public static int TrigCseg84B5B     => s_trigCseg84B5B;
    public static int TrigCseg84F4B     => s_trigCseg84F4B;
    public static void ResetFireSiteCounters()
    {
        s_fireSiteCseg850F9 = 0;
        s_fireSiteCseg84FCA = 0;
        s_fireSiteActivate  = 0;
        s_trigPlayerNear = 0;
        s_trigCseg84A85  = 0;
        s_trigCseg850F9Gate = 0;
        s_trigCseg84B5B  = 0;
        s_trigCseg84F4B  = 0;
    }

    // ====================================================================
    // AI_SetControlsDirection — updatePlayers.cpp:15980
    // ====================================================================
    //
    // In:
    //   A6 -> team data   (a6TeamBase, either TeamData.TopBase or BottomBase)
    //
    // Side effects: writes team.currentAllowedDirection, team.firePressed,
    //   team.fireThisFrame, team.quickFire, team.normalFire, team.AI_timer,
    //   team.AI_afterTouchStrength, team.AI_ballSpinDirection, team.field_84.
    //   Also touches AI_counter / AI_resumePlayTimer / AI_rand /
    //   AI_turnDirection / AI_maxStoppageTime / AI_resultTimer.
    //
    // CALL-SITE CONTRACT (updatePlayers.cpp has exactly THREE call sites):
    //   :4433 — l_check_for_cpu_team, the CPU team's CONTROLLED player;
    //   :5793 — l_player_taking_throw_in, the CPU set-piece taker (state 5);
    //   :8928 — off-ball loop tail, gated on BOTH playerNumber == 0 AND
    //           team.controlledPlayer == 0 (:8902-8923).
    // Every entry clears the team fire flags + currentAllowedDirection
    // (:16043-16047) and decrements the GLOBAL AI_resumePlayTimer
    // (:16008-16028). Calling this from off-ball sprites while a controlled
    // player EXISTS therefore wipes the controlled invocation's committed
    // quickFire/normalFire before the consume path (:5067-5407) can act and
    // starves the l_our_player_closest rpt==0 gate — the exact restart-stall
    // regression fixed on 2026-07-02 (see docs in scratchpad/airestart-diffs.md
    // for the UpdatePlayers.cs call-site gate).
    //
    // From updatePlayers.cpp:15980.
    public static void SetControlsDirection(int a6TeamBase)
    {
        // --------------------------------------------------------------
        // ENTRY block (L15982-16060)
        // --------------------------------------------------------------

        // 15982-15989 — `mov ax, [team+resetControls]; or ax,ax; jnz return`.
        // If the per-team resetControls latch is set, skip this tick entirely.
        short resetControls = Memory.ReadSignedWord(a6TeamBase + TeamData.OffResetControls);
        if (resetControls != 0)
            return;

        // 15991-16006 — decrement AI_counter if non-zero.
        short aiCounter = Memory.ReadSignedWord(Memory.Addr.AI_counter);
        if (aiCounter != 0)
            Memory.WriteWord(Memory.Addr.AI_counter, aiCounter - 1);
        // l_bump_resume_play_ai_timer:

        // 16008-16028 — same dance for AI_resumePlayTimer.
        short aiResumeTimer = Memory.ReadSignedWord(Memory.Addr.AI_resumePlayTimer);
        if (aiResumeTimer != 0)
            Memory.WriteWord(Memory.Addr.AI_resumePlayTimer, aiResumeTimer - 1);
        // l_generate_rand:

        // 16030-16033 — AI_rand = Rand(). Pulls a fresh byte from the shared
        // SWOS rand stream every per-team entry. Downstream consumers mask
        // small bit-fields (& 0xF, & 7, & 1, & 0x18) so a byte is sufficient.
        // Live impl: `SwosVm.Rng`. Source: external/swos-port/src/util/random.cpp.
        int aiRand = Rng.NextByte();
        Memory.WriteWord(Memory.Addr.AI_rand, aiRand);

        // Snapshot of currentGameTick for downstream `tick & N` masks (used
        // for stoppage timers, joystick wobble timing, etc.). Kept separate
        // from AI_rand because those branches need cycle-locked timing
        // (predictable per-tick behaviour), not random noise.
        ushort gameTick = Memory.ReadWord(Memory.Addr.currentGameTick);

        // 16034-16041 — team.AI_timer += 1.
        short aiTimer = Memory.ReadSignedWord(a6TeamBase + TeamData.OffAITimer);
        Memory.WriteWord(a6TeamBase + TeamData.OffAITimer, aiTimer + 1);

        // 16043-16047 — clear all fire flags + currentAllowedDirection (default).
        Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, -1);
        Memory.WriteByte(a6TeamBase + TeamData.OffFirePressed, 0);
        Memory.WriteByte(a6TeamBase + TeamData.OffFireThisFrame, 0);
        Memory.WriteByte(a6TeamBase + TeamData.OffQuickFire, 0);
        Memory.WriteByte(a6TeamBase + TeamData.OffNormalFire, 0);

        // 16048-16054 — return early if substitutes menu is up.
        short inSubsMenu = Memory.ReadSignedWord(Memory.Addr.g_inSubstitutesMenu);
        if (inSubsMenu != 0)
            return;

        // 16056 — D7 = -1 (controlled player direction, may stay -1 if no
        // controlledPlayer pointer yet).
        int d7 = -1;

        // D2_byte — shared across cseg_84A85 → cseg_850F9 path AND
        // l_apply_after_touch → l_update_max_stoppage_time → l_decide_after_touch_strength
        // path. The asm uses it as a sign bit input to the spin-direction decision
        // (negative D2_byte → left spin, positive → right spin).
        int d2Byte = 0;

        // 16057-16058 — A5 = team.controlledPlayer.
        int a5 = Memory.ReadSignedDword(a6TeamBase + TeamData.OffControlledPlayer);
        // 16059-16060 — A4 = team.passToPlayerPtr.
        int a4 = Memory.ReadSignedDword(a6TeamBase + TeamData.OffPassToPlayerPtr);

        // 16061-16071 — if A5 != 0, D7 = A5.direction.
        if (a5 != 0)
        {
            d7 = Memory.ReadSignedWord(a5 + PlayerSprite.OffDirection);
        }
        // l_player_direction_set:

        // 16078 — A0 = ballSprite_Base (0x4F800). We use BallSprite directly.

        // 16079-16095 — D2 = team-base–dependent goal-line Y constant.
        //   if (A6 == bottomTeamData) D2 = 129;     // top goal-line
        //   else                      D2 = 769;     // bottom goal-line
        // i.e. we measure distance to OUR OWN goal here? No — the original
        // comment chooses the OPPONENT goal Y. The constants come from
        // pitch.cpp: 129 = top goal-line, 769 = bottom goal-line.
        // (The cmp at L16080 is against bottomTeamData @ 522940 → maps to
        // TeamData.BottomBase in our model.)
        int d2Word;
        if (a6TeamBase == TeamData.BottomBase)
        {
            d2Word = 129;
            // goto l_bottom_team -> falls through to l_calc_distance after writing 129
        }
        else
        {
            d2Word = 769;
        }

        // l_calc_distance: (L16097)
        // D1 = 336 (centre goal X — pitch is 671 px wide; goal X centres at 336)
        int d1Word = 336;

        // 16099-16110 — D3 = (ballSprite.x+2) - 336.
        short ballXPixels = BallSprite.XPixels;
        int d3Word = (short)(ballXPixels - d1Word);

        // 16112-16117 — D4 = (ballSprite.y+2) - D2.
        short ballYPixels = BallSprite.YPixels;
        int d4Word = (short)(ballYPixels - d2Word);

        // 16118-16135 — D3 *= D3 (signed 16x16 → 32). Same for D4.
        // `imul bx` writes ax (low) and dx (high). swos-port stores both halves
        // back into D3, so D3 ends up as a signed 32-bit square.
        int d3 = (int)(short)d3Word * (int)(short)d3Word;
        int d4 = (int)(short)d4Word * (int)(short)d4Word;

        // 16136-16147 — D3 = D3 + D4 (32-bit signed). D6 = D3.
        int d3Sum = d3 + d4;
        int d6 = d3Sum;

        // 16149-16156 — D3 = ballSprite.x+2 (current x); D4 = ballSprite.y+2
        //   (current y); D0 = 256 (speed). D1 = 336 (destX = goal centre),
        //   D2 = d2Word (destY = own-goal y). Call CalculateDeltaXAndY which
        //   returns the SWOS-format full-direction (0..255) angle in D0.
        // Ported via SpriteUpdate.CalculateDeltaXAndY (updateSprite.cpp:231-336).
        // The asm pushes/pops D5 around the call because the helper clobbers
        // D5; we only read result.Direction so no D5 dance needed here.
        var calcResult = SpriteUpdate.CalculateDeltaXAndY(
            256,                  // D0 = speed
            (short)ballXPixels,   // D3 = current x  (the helper expects x,y as the *current* point)
            (short)ballYPixels,   // D4 = current y
            (short)d1Word,        // D1 = destX (336 — goal centre)
            (short)d2Word);       // D2 = destY (own-goal y, 23 for top team, 769 for bottom)
        int calcD0 = calcResult.Direction;

        // 16157-16158 — if !sign (D0 >= 0) goto l_save_angle.
        //   `flags.sign = result.direction < 0` (updateSprite.cpp:366).
        // 16160 — else D0 = 0.
        int d0 = (calcD0 < 0) ? 0 : calcD0;
        // l_save_angle: (L16162-16164) — D5 = D0 (16-bit ax).
        int d5 = d0 & 0xFFFF;

        // --------------------------------------------------------------
        // GAME_STATE_PL = 100 fast path (L16165-16176)
        // --------------------------------------------------------------
        // 16165-16176 — `cmp gameStatePl, ST_GAME_IN_PROGRESS (100); jz l_game_in_progress`.
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        if (gameStatePl == 100)
            goto l_game_in_progress;

        // --------------------------------------------------------------
        // Not in progress: gate on last-team-played-before-break
        // L16178-16307 (end-of-match / halftime auto-fire branch)
        // --------------------------------------------------------------
        // 16178-16189 — eax = lastTeamPlayedBeforeBreak; if A6 != eax return.
        int lastTeamBb = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
        if (a6TeamBase != lastTeamBb)
            return;

        // 16191-16202 — cmp gameState, ST_STARTING_GAME (21); jb l_game_not_over.
        short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);
        if ((ushort)gameState < (ushort)21)
            goto l_game_not_over;

        // 16204-16215 — cmp gameState, ST_GAME_ENDED (30); ja l_game_not_over.
        if ((ushort)gameState > (ushort)30)
            goto l_game_not_over;

        // 16217-16224 — if team1Computer == 0 goto l_game_not_over.
        short t1Comp = Memory.ReadSignedWord(Memory.Addr.team1Computer);
        if (t1Comp == 0)
            goto l_game_not_over;
        // 16226-16231 — if team2Computer == 0 goto l_game_not_over.
        short t2Comp = Memory.ReadSignedWord(Memory.Addr.team2Computer);
        if (t2Comp == 0)
            goto l_game_not_over;

        // 16233-16244 — cmp gameState, ST_RESULT_ON_HALFTIME (25); jz l_showing_result_on_halftime.
        if (gameState == 25)
            goto l_showing_result_on_halftime;

        // 16246-16257 — cmp gameState, ST_RESULT_AFTER_THE_GAME (26); jnz l_not_showing_final_result.
        if (gameState != 26)
            goto l_not_showing_final_result;

        // 16259-16272 — cmp stoppageTimerTotal, clearResultInterval; jb return.
        {
            short stoppageTotal = Memory.ReadSignedWord(Memory.Addr.stoppageTimerTotal);
            short clearInterval = Memory.ReadSignedWord(Memory.Addr.m_clearResultInterval);
            if ((ushort)stoppageTotal < (ushort)clearInterval)
                return;
            goto l_interval_expired_fire;
        }

    l_showing_result_on_halftime:
        // 16274-16288 — cmp stoppageTimerTotal, clearResultInterval; jb return.
        {
            short stoppageTotal = Memory.ReadSignedWord(Memory.Addr.stoppageTimerTotal);
            short clearInterval = Memory.ReadSignedWord(Memory.Addr.m_clearResultInterval);
            if ((ushort)stoppageTotal < (ushort)clearInterval)
                return;
            goto l_interval_expired_fire;
        }

    l_not_showing_final_result:
        // 16290-16302 — cmp stoppageTimerTotal, clearResultHalftimeInterval; jb return.
        {
            short stoppageTotal = Memory.ReadSignedWord(Memory.Addr.stoppageTimerTotal);
            short clearInterval = Memory.ReadSignedWord(Memory.Addr.m_clearResultHalftimeInterval);
            if ((ushort)stoppageTotal < (ushort)clearInterval)
                return;
        }

    l_interval_expired_fire:
        // 16304-16307 — team.firePressed = 1; return.
        Memory.WriteByte(a6TeamBase + TeamData.OffFirePressed, 1);
        return;

    l_game_not_over:
        // --------------------------------------------------------------
        // GAME_NOT_OVER (L16450-...)
        // currentGameTick & 0x3F + 50 + 50  vs stoppageTimerActive
        // --------------------------------------------------------------
        // 16451-16480 — D0 = (currentGameTick & 0x3F) + 50 + 50 = (gameTick & 63) + 100.
        //               cmp D0, stoppageTimerActive; ja l_set_direction.
        // I.e. while the stoppage hasn't ticked past (gameTick&63)+100, jump
        // to l_set_direction (handle set-piece directions etc.).
        {
            int d0Tick = (gameTick & 0x3F) + 100;
            short stoppageActive = Memory.ReadSignedWord(Memory.Addr.stoppageTimerActive);
            if (d0Tick > stoppageActive)
                goto l_set_direction;
        }

        // 16482-16488 — if playingPenalties != 0 goto l_doing_penalties.
        {
            short pp = Memory.ReadSignedWord(Memory.Addr.playingPenalties);
            if (pp != 0)
                goto l_doing_penalties;
        }

        // 16490-16501 — cmp gameState, ST_PENALTY (14); jz l_doing_penalties.
        if (gameState == 14)
            goto l_doing_penalties;

        // 16503-16514 — cmp gameState, ST_KEEPER_HOLDS_BALL (3); jz l_keepers_ball.
        if (gameState == 3)
            goto l_keepers_ball;

        // 16516-16527 — cmp gameState, ST_GOAL_OUT_LEFT (1); jz l_keepers_ball.
        if (gameState == 1)
            goto l_keepers_ball;

        // 16529-16540 — cmp gameState, ST_GOAL_OUT_RIGHT (2); jz l_keepers_ball.
        if (gameState == 2)
            goto l_keepers_ball;

        // 16542-16553 — cmp gameState, ST_PLAYERS_TO_INITIAL_POSITIONS (0); jz l_goal_scored.
        if (gameState == 0)
            goto l_goal_scored;

        // 16555-16566 — cmp gameState, ST_FREE_KICK_LEFT1 (6); jb l_test_throw_in.
        if ((ushort)gameState < (ushort)6)
            goto l_test_throw_in;

        // 16568-16579 — cmp gameState, ST_FREE_KICK_RIGHT3 (12); jbe l_free_kick.
        if ((ushort)gameState <= (ushort)12)
            goto l_free_kick;

    l_test_throw_in:
        // 16582-16593 — cmp gameState, ST_THROW_IN_FORWARD_RIGHT (15); jb l_test_foul.
        if ((ushort)gameState < (ushort)15)
            goto l_test_foul;

        // 16595-16606 — cmp gameState, ST_THROW_IN_BACK_LEFT (20); ja l_test_foul.
        if ((ushort)gameState > (ushort)20)
            goto l_test_foul;

        // 16608-16638 — throw-in: look up direction byte in AI_throwInDirections[gameState-15].
        // top-team rotates the nibbles (ror 4).
        {
            int idx = gameState - 15;
            byte tableByte = Memory.ReadByte(Memory.Addr.AI_throwInDirections + idx);
            byte d1Byte = tableByte;
            if (a6TeamBase == TeamData.BottomBase)
                goto cseg_84671_byD1;
            // top team: ror byte D1, 4 (swap nibbles).
            d1Byte = (byte)(((d1Byte >> 4) & 0x0F) | ((d1Byte & 0x0F) << 4));

        cseg_84671_byD1:
            // cseg_84671 (L16824-16857): tests AI_rand & 0xF == 0; if so use D1
            // as a direction mask vs (1 << D7) and goto l_apply_after_touch.
            int d0Mask = aiRand & 0xF;
            if (d0Mask != 0)
                goto cseg_84775;
            // shl eax, cl where eax=1, cl=D7
            int mask = (d7 >= 0 && d7 < 32) ? (1 << (d7 & 0x1F)) : 0;
            int test = d1Byte & mask;
            if (test != 0)
                goto l_apply_after_touch;
            goto cseg_84775;
        }

    l_test_foul:
        // 16640-16652 — cmp gameState, ST_FOUL (13); jz l_free_kick.
        if (gameState == 13)
            goto l_free_kick;

        // 16654 — goto l_apply_after_touch.
        goto l_apply_after_touch;

    l_keepers_ball:
        // 16656-16668 — D0 = AI_rand & 1; if zero goto cseg_84775.
        if ((aiRand & 1) == 0)
            goto cseg_84775;

        // 16670-16681 — if D7 == cameraDirection goto l_apply_after_touch.
        {
            short camDir = Memory.ReadSignedWord(Memory.Addr.cameraDirection);
            if (d7 == camDir)
                goto l_apply_after_touch;
        }

        // 16683-16694 — cmp ballSprite.x+2, 336; ja cseg_845CC.
        short ballXNow = BallSprite.XPixels;
        if ((ushort)ballXNow > (ushort)336)
            goto cseg_845CC;

        // 16696-16706 — cmp D7, 1; jz l_apply_after_touch.
        if (d7 == 1)
            goto l_apply_after_touch;
        // 16708-16718 — cmp D7, 3; jz l_apply_after_touch.
        if (d7 == 3)
            goto l_apply_after_touch;

        goto cseg_84775;

    cseg_845CC:
        // 16723-16733 — cmp D7, 5; jz l_apply_after_touch.
        if (d7 == 5)
            goto l_apply_after_touch;
        // 16735-16745 — cmp D7, 7; jz l_apply_after_touch.
        if (d7 == 7)
            goto l_apply_after_touch;
        goto cseg_84775;

    l_goal_scored:
        // 16750-16761 — cmp stoppageTimerActive, 150; jb cseg_84775.
        {
            short stoppageActive = Memory.ReadSignedWord(Memory.Addr.stoppageTimerActive);
            if ((ushort)stoppageActive < (ushort)150)
                goto cseg_84775;
        }
        // 16763-16774 — if D7 == cameraDirection goto l_apply_after_touch.
        {
            short camDir = Memory.ReadSignedWord(Memory.Addr.cameraDirection);
            if (d7 == camDir)
                goto l_apply_after_touch;
        }
        goto cseg_84775;

    l_free_kick:
        // 16809-16822 — D0 = AI_rand & 0xF; if zero goto cseg_84775 (else cseg_8470F).
        if ((aiRand & 0xF) == 0)
            goto cseg_84775;
        goto cseg_8470F;

    l_doing_penalties:
        // 16860-16889 — D0 = AI_rand & 7; ax = 1 << D0; if (playerTurnFlags & ax)
        //                team.currentAllowedDirection = D0; return.
        {
            int d0p = aiRand & 7;
            int mask = 1 << d0p;
            byte turnFlags = Memory.ReadByte(Memory.Addr.playerTurnFlags);
            if ((turnFlags & mask) != 0)
            {
                Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d0p);
                return;
            }
        }
        // l_penalty_random_direction_disallowed: (L16891-16914)
        // if (A5.direction == 0 || A5.direction == 4) return;
        // else goto l_apply_after_touch.
        if (a5 == 0) return;  // defensive — shouldn't reach here with A5==0
        {
            short a5Dir = Memory.ReadSignedWord(a5 + PlayerSprite.OffDirection);
            if (a5Dir == 0) return;
            if (a5Dir == 4) return;
            goto l_apply_after_touch;
        }

    cseg_8470F:
        // 16917-16944 — quirky angle-comparison branch. D0 = ((D5+16)&0xFF)>>5.
        //               if (D0 == D7) goto l_apply_after_touch; else goto cseg_84815.
        {
            int d0w = (d5 + 16) & 0xFF;
            d0w = d0w >> 5;
            if (d0w == (d7 & 0xFFFF))
                goto l_apply_after_touch;
            goto cseg_84815;
        }

    l_apply_after_touch:
        // 16948-16973 — apply after-touch with D7.
        //   if (D7 < 0) return;
        //   D2 = (D7 << 5) (with overflow into hi byte)
        //   D2 -= D5 (byte-wise)
        //   goto l_update_max_stoppage_time.
        if (d7 < 0) return;
        {
            int d2Apply = (d7 << 5) & 0xFFFF;
            int d2LoSubByte = ((d2Apply & 0xFF) - (d5 & 0xFF)) & 0xFF;
            // Save the low byte for l_decide_after_touch_strength's sign test.
            d2Byte = d2LoSubByte;
            goto l_update_max_stoppage_time;
        }

    cseg_84775:
        // 16976-17015 — D0 = (D7 << 5).
        //   FindClosestPlayerToBallFacing.
        //   if A0 == -1 goto l_update_turn_direction.
        //   if A5.teamNumber != A0.teamNumber goto l_update_turn_direction.
        //   else goto l_our_player_closest.
        {
            int d0FullDir = (d7 << 5) & 0xFFFF;
            int closestPlayer = AiHelpers.FindClosestPlayerToBallFacing(d0FullDir, a6TeamBase, out _);
            if (closestPlayer == -1)
                goto l_update_turn_direction;
            if (a5 == 0)
                goto l_update_turn_direction;
            short a5Team = Memory.ReadSignedWord(a5 + PlayerSprite.OffTeamNumber);
            short a0Team = Memory.ReadSignedWord(closestPlayer + PlayerSprite.OffTeamNumber);
            if (a5Team != a0Team)
                goto l_update_turn_direction;
            goto l_our_player_closest;
        }

    cseg_84815:
        // 17059-17081 — D0 = ((D5+16)&0xFF)>>5; team.currentAllowedDirection = D0.
        {
            int d0w = ((d5 + 16) & 0xFF) >> 5;
            Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d0w);
            return;
        }

    l_update_turn_direction:
        // 17084-17227 — half-step direction turn (AI_turnDirection accumulator).
        // Only run on every 16th game tick (gameTick & 0x0E != 0 → return).
        {
            int d0Tick = gameTick & 0x0E;
            if (d0Tick != 0)
                return;

            short turnDir = Memory.ReadSignedWord(Memory.Addr.AI_turnDirection);
            int d1Direction;

            if (turnDir < 0)
            {
                // 17105-17137 — sign set: deal with -1 case.
                if (turnDir == -1)
                {
                    goto cseg_84890;
                }
                // sub AI_turnDirection, -1  (i.e. ++)
                Memory.WriteWord(Memory.Addr.AI_turnDirection, turnDir + 1);
                short tdAfter = Memory.ReadSignedWord(Memory.Addr.AI_turnDirection);
                if (tdAfter != -1)
                    return;
            cseg_84890:
                d1Direction = 1;
                goto l_apply_turn_direction;
            }
            // cseg_8489B (L17143-17178):
            if (turnDir == 1)
                goto cseg_848BB;
            Memory.WriteWord(Memory.Addr.AI_turnDirection, turnDir - 1);
            {
                short tdAfter = Memory.ReadSignedWord(Memory.Addr.AI_turnDirection);
                if (tdAfter != 1)
                    return;
            }
        cseg_848BB:
            d1Direction = -1;

        l_apply_turn_direction:
            // 17182-17227 — combine D7 with AI_turnDirection, mask to 0..7,
            // test playerTurnFlags; if allowed write to currentAllowedDirection,
            // else save d1Direction back to AI_turnDirection for next frame.
            short curTurnDir = Memory.ReadSignedWord(Memory.Addr.AI_turnDirection);
            int d0New = ((d7 + curTurnDir) & 7);
            byte tf = Memory.ReadByte(Memory.Addr.playerTurnFlags);
            int mask = 1 << d0New;
            if ((tf & mask) == 0)
            {
                // l_save_for_next_frame:
                Memory.WriteWord(Memory.Addr.AI_turnDirection, d1Direction);
                return;
            }
            Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d0New);
            return;
        }

    l_set_direction:
        // 17311-17448 — set-piece direction logic: throw-in, foul, free-kick.
    l_check_if_result_shown:
        // 17312-17318 — if AI_resultTimer != 0 return.
        {
            short aiRes = Memory.ReadSignedWord(Memory.Addr.AI_resultTimer);
            if (aiRes != 0)
                return;
        }

        // 17320-17344 — gameState branches → l_update_turn_direction for
        // most stoppage states.
        {
            short gs = Memory.ReadSignedWord(Memory.Addr.gameState);
            if (gs == 1) goto l_update_turn_direction;        // ST_GOAL_OUT_LEFT
            if (gs == 2) goto l_update_turn_direction;        // ST_GOAL_OUT_RIGHT
            if (gs == 3) goto l_update_turn_direction;        // ST_KEEPER_HOLDS_BALL
            // 17359-17383 — cmp gs, 15..20: ja l_no_throw_in else goto l_update_turn_direction.
            if ((ushort)gs >= (ushort)15)
            {
                if ((ushort)gs <= (ushort)20)
                    goto l_update_turn_direction;
                // fall to l_no_throw_in
            }
        l_no_throw_in:
            // 17386-17397 — cmp gs, ST_FOUL (13); jz l_foul_or_free_kick.
            if (gs == 13) goto l_foul_or_free_kick;
            // 17399-17423 — cmp gs, ST_FREE_KICK_LEFT1 (6); jb return / cmp 12 ja return.
            if ((ushort)gs < (ushort)6) return;
            if ((ushort)gs > (ushort)12) return;
        }

    l_foul_or_free_kick:
        // 17426-17448 — D0 = ((D5 + 16) & 0xFF) >> 5; team.currentAllowedDirection = D0.
        {
            int d0w = ((d5 + 16) & 0xFF) >> 5;
            Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d0w);
            return;
        }

    l_game_in_progress:
        // 17229-17307 — main game-in-progress branch entry.
        // 17230-17244 — if playingPenalties != 0 OR penalty != 0 goto l_penalty.
        {
            short pp = Memory.ReadSignedWord(Memory.Addr.playingPenalties);
            short pen = Memory.ReadSignedWord(Memory.Addr.penalty);
            if (pp == 0 && pen == 0)
                goto l_no_penalty;
        }
    // l_penalty:
        // 17247-17254 — if spinTimer >= 0 goto l_ball_after_touch_allowed.
        {
            short spinTimer = Memory.ReadSignedWord(a6TeamBase + TeamData.OffSpinTimer);
            if (spinTimer >= 0)
                goto l_ball_after_touch_allowed;
        }

    l_no_penalty:
        // 17257-17266 — if AI_counter != 0 call AI_SetDirectionTowardOpponentsGoal.
        {
            short aic = Memory.ReadSignedWord(Memory.Addr.AI_counter);
            if (aic != 0)
                AiHelpers.AI_SetDirectionTowardOpponentsGoal(a6TeamBase);
        }
    // l_direction_updated:
        // 17269-17287 — if A5 == 0 return; if spinTimer >= 0 goto l_ball_after_touch_allowed.
        if (a5 == 0) return;
        {
            short spinTimer = Memory.ReadSignedWord(a6TeamBase + TeamData.OffSpinTimer);
            if (spinTimer >= 0)
                goto l_ball_after_touch_allowed;
        }

        // 17289-17305 — check team.plVeryCloseToBall / plCloseToBall.
        // If either non-zero goto l_theres_a_player_near; else goto l_noone_near.
        {
            byte plVeryClose = Memory.ReadByte(a6TeamBase + TeamData.OffPlVeryCloseToBall);
            if (plVeryClose != 0)
                goto l_theres_a_player_near;
            byte plClose = Memory.ReadByte(a6TeamBase + TeamData.OffPlCloseToBall);
            if (plClose != 0)
                goto l_theres_a_player_near;
            goto l_noone_near;
        }

    l_theres_a_player_near:
        // 17333-17370 — AI_DecideWhetherToTriggerFire. If returns true (zero flag set
        // in asm), return immediately.
        s_trigPlayerNear++;
        {
            bool fired = AiHelpers.AI_DecideWhetherToTriggerFire(d7, a5, a6TeamBase);
            if (fired) return;

            // 17338-17369 — dead branch protected by deadVarAlways0 (always 0).
            // Skipped semantically (always falls through to cseg_84A0D).
        }
    // cseg_84A0D: (L17371-17541)
        // Body: large chase-vs-fall-back decision tree based on A5.direction,
        // ball Y, D6 bucket. Mechanically ported piecewise: facing-LR check,
        // ball-Y top/bottom guard, D6 threshold ladder.
        {
            short a5Dir = Memory.ReadSignedWord(a5 + PlayerSprite.OffDirection);
            bool facingLeftOrRight = (a5Dir == 2 || a5Dir == 6);

            // L17398-17440 — ball-Y bounds depend on team. TOP team: ball.y > 740 → cseg_84AEB.
            // BOTTOM team: ball.y <= 158 → cseg_84AEB. Otherwise cseg_84A53.
            bool jumpTo84AEB = false;
            if (facingLeftOrRight)
            {
                if (a6TeamBase == TeamData.TopBase)
                {
                    short by = BallSprite.YPixels;
                    if (by >= 740) jumpTo84AEB = true;
                }
                else
                {
                    short by = BallSprite.YPixels;
                    if (by <= 158) jumpTo84AEB = true;
                }
            }

            if (!jumpTo84AEB)
            {
                // cseg_84A53 (L17441-17542) — d6 (squared distance to opp goal)
                // bucketed against thresholds. Above 28800 → goto 84AEB.
                if (d6 > 28800)
                {
                    jumpTo84AEB = true;
                }
                else
                {
                    // L17454-17477 — between 12800 and 28800 with AI_rand&3 != 0 → 84AEB.
                    if (d6 >= 12800)
                    {
                        int randMask = aiRand & 3;
                        if (randMask != 0) jumpTo84AEB = true;
                    }
                }
            }

            if (jumpTo84AEB)
                goto cseg_84AEB;

            // cseg_84A85: (L17479-17542) — fine-grain byte-wise angle window.
            // from updatePlayers.cpp:17479
            s_trigCseg84A85++;
            {
                // 17480 — D1_byte = 15 (default).
                int d1Byte = 15;
                // 17481-17491 — cmp D6, 3200; ja cseg_84A9F (skip overwrite).
                if (!(d6 > 3200))
                {
                    // 17493 — D1_byte = 50 if D6 <= 3200.
                    d1Byte = 50;
                }
            // cseg_84A9F:
                // 17496-17504 — D2 = A5.direction (word). If sign set (direction
                // negative, i.e. invalid) → goto cseg_84AEB.
                short a5DirCs = Memory.ReadSignedWord(a5 + PlayerSprite.OffDirection);
                if (a5DirCs < 0)
                    goto cseg_84AEB;
                int d2WordCs = a5DirCs & 0xFFFF;
                // 17506-17509 — D2_word <<= 5.
                d2WordCs = (d2WordCs << 5) & 0xFFFF;
                // 17510-17516 — D2_byte -= D5_byte (byte-wise).
                int d2Lo = ((d2WordCs & 0xFF) - (d5 & 0xFF)) & 0xFF;
                d2WordCs = (d2WordCs & ~0xFF) | d2Lo;
                // Save D2 byte for cseg_850F9 sign test.
                d2Byte = d2Lo;
                // 17517-17528 — cmp D2_byte, D1_byte (signed); jg cseg_84AEB.
                sbyte d2Signed = (sbyte)d2Lo;
                sbyte d1Signed = (sbyte)d1Byte;
                if (d2Signed > d1Signed)
                    goto cseg_84AEB;
                // 17530 — D1_byte = -D1_byte.
                d1Signed = (sbyte)(-d1Signed);
                // 17531-17541 — cmp D2_byte, D1_byte; jg cseg_850F9.
                if (d2Signed > d1Signed)
                {
                    s_trigCseg850F9Gate++;
                    goto cseg_850F9;
                }
                // else fall through to cseg_84AEB.
                goto cseg_84AEB;
            }
        }

    cseg_84AEB:
        // 17544-17601 — team.field_84 (chase counter) gate.
        {
            short field84 = Memory.ReadSignedWord(a6TeamBase + TeamData.OffAiField84);
            if (field84 != 0)
            {
                Memory.WriteWord(a6TeamBase + TeamData.OffAiField84, field84 - 1);
                int d0FullDir = (d7 << 5) & 0xFFFF;
                int closest = AiHelpers.FindClosestPlayerToBallFacing(d0FullDir, a6TeamBase, out _);
                if (closest == -1)
                    goto cseg_84D57;
                if (a5 == 0)
                    goto cseg_84D57;
                short a5Team = Memory.ReadSignedWord(a5 + PlayerSprite.OffTeamNumber);
                short cTeam = Memory.ReadSignedWord(closest + PlayerSprite.OffTeamNumber);
                if (a5Team == cTeam)
                    goto l_our_player_closest;
                goto cseg_84D57;
            }
        }

    cseg_84B5B:
        // 17603-17716 — D6 ladder & opp-controlled-player + passToPlayer ball
        // distance check. From updatePlayers.cpp:17603
        s_trigCseg84B5B++;
        {
            // 17604-17614 — cmp D6, 9800; jb cseg_84DD3.
            if ((uint)d6 < (uint)9800)
                goto cseg_84DD3;

            // 17616-17626 — cmp A5, 0; jz cseg_84DD3.
            if (a5 == 0)
                goto cseg_84DD3;

            // 17628-17633 — A0 = team.opponentsTeam; A2 = A0.controlledPlayer.
            int a0Cs = Memory.ReadSignedDword(a6TeamBase + TeamData.OffOpponentsTeam);
            int a2Cs = Memory.ReadSignedDword(a0Cs + TeamData.OffControlledPlayer);

            // 17634-17644 — cmp A2, 0; jz cseg_84BBE (skip to passTo branch).
            if (a2Cs == 0)
                goto cseg_84BBE;

            // 17646-17658 — cmp [A2.ballDistance], 800; jb cseg_84C00.
            int a2BallDist = Memory.ReadSignedDword(a2Cs + PlayerSprite.OffBallDistance);
            if ((uint)a2BallDist < (uint)800)
                goto cseg_84C00;

            // 17660-17671 — cmp [A2.ballDistance], 5000; jb cseg_84C93.
            if ((uint)a2BallDist < (uint)5000)
                goto cseg_84C93;

        cseg_84BBE:
            // 17673-17676 — A2 = A0.passToPlayerPtr.
            a2Cs = Memory.ReadSignedDword(a0Cs + TeamData.OffPassToPlayerPtr);

            // 17677-17687 — cmp A2, 0; jz cseg_84DD3.
            if (a2Cs == 0)
                goto cseg_84DD3;

            // 17689-17701 — cmp [A2.ballDistance], 800; jb cseg_84C00.
            int a2BallDistB = Memory.ReadSignedDword(a2Cs + PlayerSprite.OffBallDistance);
            if ((uint)a2BallDistB < (uint)800)
                goto cseg_84C00;

            // 17703-17714 — cmp [A2.ballDistance], 5000; jb cseg_84C93.
            if ((uint)a2BallDistB < (uint)5000)
                goto cseg_84C93;

            // 17716 — jmp cseg_84DD3.
            goto cseg_84DD3;
        }

    cseg_84C00:
        // 17718-17798 — close enemy (ballDist<800). from updatePlayers.cpp:17718
        {
            // 17719-17729 — cmp D6, 180000; ja cseg_84F4B.
            if (d6 > 180000)
                goto cseg_84F4B;

            // 17731-17740 — D0 = D7 << 5 (word); FindClosestPlayerToBallFacing.
            int d0Full = (d7 << 5) & 0xFFFF;
            int a0Cf = AiHelpers.FindClosestPlayerToBallFacing(d0Full, a6TeamBase, out _);

            // 17741-17751 — cmp A0, -1; jz cseg_84D10.
            if (a0Cf == -1)
                goto cseg_84D10;

            // 17753-17768 — cmp A5.teamNumber, A0.teamNumber; jz l_our_player_closest.
            short a5Tn = Memory.ReadSignedWord(a5 + PlayerSprite.OffTeamNumber);
            short a0Tn = Memory.ReadSignedWord(a0Cf + PlayerSprite.OffTeamNumber);
            if (a5Tn == a0Tn)
                goto l_our_player_closest;

            // 17770 — jmp cseg_84D10.
            goto cseg_84D10;

            // 17772-17798 — dead branch (cmp A0, -1; jnz ...) preserved for parity.
            // The assembler emits an unreachable path here that loads team1Computer
            // / team2Computer-equivalents. Not exercised in retail flow.
        }

    cseg_84C93:
        // 17800-17878 — mid-range enemy (800 <= ballDist < 5000).
        // from updatePlayers.cpp:17800
        {
            // 17801-17812 — cmp AI_rand, 8; ja cseg_84CAD.
            if ((ushort)aiRand > (ushort)8)
                goto cseg_84CAD;

            // 17814-17824 — cmp D6, 48400; ja cseg_84F4B.
            if (d6 > 48400)
                goto cseg_84F4B;

        cseg_84CAD:
            // 17827-17838 — currentGameTick & 12; jnz cseg_84DD3.
            if ((gameTick & 12) != 0)
                goto cseg_84DD3;

            // 17840-17849 — D0 = D7 << 5; FindClosestPlayerToBallFacing.
            int d0Full = (d7 << 5) & 0xFFFF;
            int a0Cf = AiHelpers.FindClosestPlayerToBallFacing(d0Full, a6TeamBase, out _);

            // 17850-17860 — cmp A0, -1; jz cseg_84D10.
            if (a0Cf == -1)
                goto cseg_84D10;

            // 17862-17877 — cmp A5.teamNumber, A0.teamNumber; jz l_our_player_closest.
            short a5Tn = Memory.ReadSignedWord(a5 + PlayerSprite.OffTeamNumber);
            short a0Tn = Memory.ReadSignedWord(a0Cf + PlayerSprite.OffTeamNumber);
            if (a5Tn == a0Tn)
                goto l_our_player_closest;
            // fall through into cseg_84D10.
        }

    cseg_84D10:
        // 17879-17907 — gate via team.field_84 and frameCount.
        // from updatePlayers.cpp:17879
        {
            // 17880-17887 — if team.field_84 != 0 goto cseg_84DD3.
            short f84 = Memory.ReadSignedWord(a6TeamBase + TeamData.OffAiField84);
            if (f84 != 0)
                goto cseg_84DD3;

            // 17889-17905 — D0 = frameCount & 0x7F; cmp D0, 32; jnb cseg_84DD3.
            ushort frameCnt = Memory.ReadWord(Memory.Addr.frameCount);
            int d0Fc = frameCnt & 0x7F;
            if ((ushort)d0Fc >= (ushort)32)
                goto cseg_84DD3;

            // 17907 — team.field_84 = 4.
            Memory.WriteWord(a6TeamBase + TeamData.OffAiField84, 4);
            goto cseg_84D57;
        }

    cseg_84DD3:
        // 17975-18015 — if team.plVeryCloseToBall != 0 fall through into cseg_84DE0;
        // else goto cseg_84E16 (use A5.direction as currentAllowedDirection).
        {
            byte plClose = Memory.ReadByte(a6TeamBase + TeamData.OffPlVeryCloseToBall);
            if (plClose == 0)
                goto cseg_84E16;
            goto cseg_84DE0;
        }

    cseg_84D57:
        // 17910-17973 — plVeryCloseToBall && currentGameTick & 0x80 → turn-right;
        // else turn-left. Both write currentAllowedDirection and return.
        {
            byte plClose = Memory.ReadByte(a6TeamBase + TeamData.OffPlVeryCloseToBall);
            if (plClose == 0)
                goto cseg_84E16;

            bool turnRight = (gameTick & 0x80) != 0;
            short a5Dir = Memory.ReadSignedWord(a5 + PlayerSprite.OffDirection);
            int d0New = ((a5Dir + (turnRight ? 1 : -1)) & 7);
            Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d0New);
            return;
        }

    cseg_84DE0:
        // 17985-18008 — D0 = ((D5+16)&0xFF)>>5; write to currentAllowedDirection.
        {
            int d0w = ((d5 + 16) & 0xFF) >> 5;
            Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d0w);
            return;
        }

    cseg_84E16:
        // 18011-18015 — write A5.direction to currentAllowedDirection.
        {
            short a5Dir = Memory.ReadSignedWord(a5 + PlayerSprite.OffDirection);
            Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, a5Dir);
            return;
        }

    l_decide_if_flipping_direction:
        // 18017-18093 — half-step turn-around: D0 = ((A5.fullDirection + 128 + 16) & 0xFF) >> 5.
        //               if D0 == A5.direction → set opposite direction.
        //               else if ballDistance < 800 || gameTick & 0x0E == 0 → set opposite.
        //               else: write A5.direction to currentAllowedDirection.
        {
            short fullDir = Memory.ReadSignedWord(a5 + PlayerSprite.OffFullDirection);
            int d0Calc = (fullDir & 0xFFFF);
            // add byte ptr D0, 128 (byte-wise)
            int d0LoByte = ((d0Calc & 0xFF) + 0x80) & 0xFF;
            d0Calc = (d0Calc & ~0xFF) | d0LoByte;
            d0Calc = (d0Calc + 16) & 0xFFFF;
            d0Calc &= 0xFF;
            d0Calc >>= 5;

            short a5Dir = Memory.ReadSignedWord(a5 + PlayerSprite.OffDirection);
            if (d0Calc == a5Dir)
                goto l_set_opposite_direction;

            int ballDist = Memory.ReadSignedDword(a5 + PlayerSprite.OffBallDistance);
            if ((uint)ballDist < (uint)800)
                goto l_set_opposite_direction;

            int d1Tick = gameTick & 0x0E;
            if (d1Tick == 0)
                goto l_set_opposite_direction;

            Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, a5Dir);
            return;

        l_set_opposite_direction:
            Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d0Calc);
            return;
        }

    l_our_player_closest:
        // 18107-18145 — successful chase: stop chase counter, gate on
        //               AI_resumePlayTimer / AI_ResumeGameDelay; if fire, set
        //               quickFire + clamp AI_maxStoppageTime.
        Memory.WriteWord(a6TeamBase + TeamData.OffAiField84, 0);
        {
            short rpt = Memory.ReadSignedWord(Memory.Addr.AI_resumePlayTimer);
            if (rpt != 0) return;
            bool carry = AiHelpers.AI_ResumeGameDelay(a6TeamBase, out _);
            if (!carry) return;
            Memory.WriteWord(Memory.Addr.AI_resumePlayTimer, 15);
            Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d7);
            Memory.WriteByte(a6TeamBase + TeamData.OffQuickFire, 1);
            short aiMax = Memory.ReadSignedWord(Memory.Addr.AI_maxStoppageTime);
            short stoppageActive = Memory.ReadSignedWord(Memory.Addr.stoppageTimerActive);
            if (aiMax <= stoppageActive)
                Memory.WriteWord(Memory.Addr.AI_maxStoppageTime, stoppageActive);
            return;
        }

    l_update_max_stoppage_time:
        // 18510-18527 — clamp AI_maxStoppageTime to stoppageTimerActive.
        {
            short aiMax = Memory.ReadSignedWord(Memory.Addr.AI_maxStoppageTime);
            short stoppageActive = Memory.ReadSignedWord(Memory.Addr.stoppageTimerActive);
            if (aiMax <= stoppageActive)
                Memory.WriteWord(Memory.Addr.AI_maxStoppageTime, stoppageActive);
        }
    l_decide_after_touch_strength:
        // 18528-18686 — gameState/D6-driven after-touch strength ladder.
        // from updatePlayers.cpp:18528
        {
            // 18529 — AI_resumePlayTimer = 15.
            Memory.WriteWord(Memory.Addr.AI_resumePlayTimer, 15);

            // 18530-18541 — cmp gameState, ST_PENALTY (14); jz l_weak_after_touch.
            short gsAt = Memory.ReadSignedWord(Memory.Addr.gameState);
            if (gsAt == 14)
                goto l_weak_after_touch;

            // 18543-18554 — cmp gameState, ST_PENALTIES (31); jz l_weak_after_touch.
            if (gsAt == 31)
                goto l_weak_after_touch;

            // 18556-18567 — cmp gameState, ST_FREE_KICK_LEFT1 (6); jb l_test_corner.
            if ((ushort)gsAt < (ushort)6)
                goto l_test_corner;

            // 18569-18580 — cmp gameState, ST_FREE_KICK_RIGHT3 (12); jbe l_check_distance_from_the_goal.
            if ((ushort)gsAt <= (ushort)12)
                goto l_check_distance_from_the_goal;

        l_test_corner:
            // 18582-18594 — cmp gameState, ST_CORNER_LEFT (4); jz l_medium_after_touch.
            if (gsAt == 4)
                goto l_medium_after_touch;
            // 18596-18607 — cmp gameState, ST_CORNER_RIGHT (5); jz l_medium_after_touch.
            if (gsAt == 5)
                goto l_medium_after_touch;

            // 18609-18620 — D0 = AI_rand & 0x18; if !zero goto l_check_distance_from_the_goal.
            int d0Rand = aiRand & 0x18;
            if (d0Rand != 0)
                goto l_check_distance_from_the_goal;
            // 18622-18632 — cmp D0, 16 (0x10); jz l_weak_after_touch.
            if (d0Rand == 16)
                goto l_weak_after_touch;
            // 18634-18644 — cmp D0, 8; jz l_medium_after_touch.
            if (d0Rand == 8)
                goto l_medium_after_touch;
            // 18646 — jmp l_strong_after_touch.
            goto l_strong_after_touch;

        l_check_distance_from_the_goal:
            // 18648-18659 — cmp D6, 28800; jb l_weak_after_touch.
            if ((uint)d6 < (uint)28800)
                goto l_weak_after_touch;
            // 18661-18671 — cmp D6, 57800; jb l_medium_after_touch.
            if ((uint)d6 < (uint)57800)
                goto l_medium_after_touch;

        l_strong_after_touch:
            // 18673-18676 — AI_afterTouchStrength = 2; jmp l_activate_normal_fire.
            Memory.WriteWord(a6TeamBase + TeamData.OffAiAfterTouchStrength, 2);
            goto l_activate_normal_fire;

        l_medium_after_touch:
            // 18678-18681 — AI_afterTouchStrength = 1; jmp l_activate_normal_fire.
            Memory.WriteWord(a6TeamBase + TeamData.OffAiAfterTouchStrength, 1);
            goto l_activate_normal_fire;

        l_weak_after_touch:
            // 18683-18685 — AI_afterTouchStrength = 0; fall to l_activate_normal_fire.
            Memory.WriteWord(a6TeamBase + TeamData.OffAiAfterTouchStrength, 0);
            // fall through to l_activate_normal_fire.
        }

    l_activate_normal_fire:
        // 18687-18917 — write direction + normalFire + AI_ballSpinDirection
        // (gameState-aware spin selection: penalty / corner / foul branches).
        // from updatePlayers.cpp:18687
        {
            // 18688-18691 — currentAllowedDirection = D7; normalFire = 1.
            Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d7);
            Memory.WriteByte(a6TeamBase + TeamData.OffNormalFire, 1);
            s_fireSiteActivate++;

            // 18692-18703 — cmp gameState, ST_PENALTY (14); jz l_no_after_touch.
            short gsAct = Memory.ReadSignedWord(Memory.Addr.gameState);
            if (gsAct == 14)
                goto l_no_after_touch;
            // 18705-18716 — cmp gameState, ST_PENALTIES (31); jz l_no_after_touch.
            if (gsAct == 31)
                goto l_no_after_touch;

            // 18718-18729 — cmp gameState, ST_CORNER_LEFT (4); jz l_corner.
            if (gsAct == 4)
                goto l_corner;
            // 18731-18742 — cmp gameState, ST_CORNER_RIGHT (5); jz l_corner.
            if (gsAct == 5)
                goto l_corner;

            // 18744-18746 — AI_resumePlayTimer = 15; rewrites direction/normalFire
            //               (asm dead-stores ax which is D7 still from above —
            //               we preserve the rewrite literally even though it's
            //               redundant with 18688-18691).
            Memory.WriteWord(Memory.Addr.AI_resumePlayTimer, 15);
            Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d7);
            Memory.WriteByte(a6TeamBase + TeamData.OffNormalFire, 1);

            // 18747 — D0 = 0 (default spin = none for non-foul case).
            int d0Spin = 0;

            // 18748-18759 — cmp gameState, ST_FOUL (13); jnz cseg_8536B.
            if (gsAct != 13)
                goto cseg_8536B;

            // 18761-18771 — cmp A6, topTeamData; jnz cseg_8535D.
            if (a6TeamBase != TeamData.TopBase)
                goto cseg_8535D;

            // 18773-18787 — A2 = passToPlayerPtr (asm uses A2 from earlier);
            //               cmp [A2.y+2], 682; jge cseg_85384 else jmp cseg_8536B.
            // The asm here uses A2 which retains its prior value. In retail,
            // the typical path through here has A2 = passToPlayerPtr. The
            // common case to match the foul-on-top-team semantics: A2 = passToPtr.
            {
                int a2Foul = Memory.ReadSignedDword(a6TeamBase + TeamData.OffPassToPlayerPtr);
                if (a2Foul != 0)
                {
                    short a2Y = Memory.ReadSignedWord(a2Foul + PlayerSprite.OffY + 2);
                    if (a2Y >= 682)
                        goto cseg_85384;
                }
                goto cseg_8536B;
            }

        cseg_8535D:
            // 18789-18802 — bottom team: A2 = passToPlayerPtr; cmp [A2.y+2], 216;
            //               jle cseg_85384.
            {
                int a2Foul = Memory.ReadSignedDword(a6TeamBase + TeamData.OffPassToPlayerPtr);
                if (a2Foul != 0)
                {
                    short a2Y = Memory.ReadSignedWord(a2Foul + PlayerSprite.OffY + 2);
                    if (a2Y <= 216)
                        goto cseg_85384;
                }
                // fall through to cseg_8536B.
            }

        cseg_8536B:
            // 18804-18818 — D0 = -1; if D2_byte sign clear, jns cseg_85384.
            //               else neg D0 (D0 = 1).
            d0Spin = -1;
            {
                sbyte d2Sign = (sbyte)d2Byte;
                if (d2Sign >= 0)
                    goto cseg_85384;
            }
            d0Spin = -(-1); // = 1
            // fall through

        cseg_85384:
            // 18820-18824 — AI_ballSpinDirection = D0; return.
            Memory.WriteWord(a6TeamBase + TeamData.OffAiBallSpinDirection, d0Spin);
            return;

        l_corner:
            // 18826-18856 — D0 = AI_rand & 7; branch on value:
            //   D0 < 3 → cseg_853FA (right spin: D1=+1, D0 = D7+1)
            //   D0 < 6 → cseg_853DB (left  spin: D1=-1, D0 = D7-1)
            //   else  → l_no_after_touch (no spin)
            {
                int d0Corner = aiRand & 7;
                if (d0Corner < 3)
                    goto cseg_853FA;
                if (d0Corner < 6)
                    goto cseg_853DB;
                // else fall through to l_no_after_touch
            }

        l_no_after_touch:
            // 18857-18860 — AI_ballSpinDirection = 0; return.
            Memory.WriteWord(a6TeamBase + TeamData.OffAiBallSpinDirection, 0);
            return;

        cseg_853DB:
            // 18862-18876 — D1 = -1; D0 = D7 - 1; jmp cseg_85417.
            {
                int d1SpinL = -1;
                int d0SideL = ((d7 & 0xFFFF) - 1) & 7;
                int maskTfL = 1 << d0SideL;
                byte tfL = Memory.ReadByte(Memory.Addr.playerTurnFlags);
                if ((tfL & maskTfL) == 0)
                    goto l_no_after_touch;
                Memory.WriteWord(a6TeamBase + TeamData.OffAiBallSpinDirection, d1SpinL);
                return;
            }

        cseg_853FA:
            // 18878-18887 — D1 = +1; D0 = D7 + 1; fall to cseg_85417.
            {
                int d1SpinR = 1;
                int d0SideR = ((d7 & 0xFFFF) + 1) & 7;
                // cseg_85417: 18889-18912 — mask = 1 << D0; test playerTurnFlags.
                int maskTfR = 1 << d0SideR;
                byte tfR = Memory.ReadByte(Memory.Addr.playerTurnFlags);
                if ((tfR & maskTfR) == 0)
                    goto l_no_after_touch;
                // 18914-18917 — AI_ballSpinDirection = D1; return.
                Memory.WriteWord(a6TeamBase + TeamData.OffAiBallSpinDirection, d1SpinR);
                return;
            }
        }

    l_noone_near:
        // 18920-19046 — no-one near: re-test fire trigger, then either reassign
        //              controlled player vs pass target, or decide flip-direction.
        {
            bool fired = AiHelpers.AI_DecideWhetherToTriggerFire(d7, a5, a6TeamBase);
            if (fired) return;

            if (a4 == 0)
                goto l_pass_to_player_too_far_or_null;

            int a4Dist = Memory.ReadSignedDword(a4 + PlayerSprite.OffBallDistance);
            int a5Dist = Memory.ReadSignedDword(a5 + PlayerSprite.OffBallDistance);
            if ((a4Dist - a5Dist) < 50)
                goto l_pass_to_player_too_far_or_null;

            // 18958-18966 — swap controlledPlayer and passToPlayerPtr (i.e. pick
            // the player closer to the ball as the new "controlled" one).
            int passPtr = Memory.ReadSignedDword(a6TeamBase + TeamData.OffPassToPlayerPtr);
            int ctlPtr = Memory.ReadSignedDword(a6TeamBase + TeamData.OffControlledPlayer);
            Memory.WriteDword(a6TeamBase + TeamData.OffPassToPlayerPtr, ctlPtr);
            Memory.WriteDword(a6TeamBase + TeamData.OffControlledPlayer, passPtr);
            return;

        l_pass_to_player_too_far_or_null:
            if (a4 == 0)
                goto l_decide_if_flipping_direction;

            // 18981-18987 — deadVarAlways0 gate. Always 0 → skip to next check.
            short dv = Memory.ReadSignedWord(Memory.Addr.deadVarAlways0);
            // Dead path: if (dv != 0) goto cseg_84DE0. Always taken false in retail.
            _ = dv;

            // 18989-19003 — if topTeam.playerNumber or bottomTeam.playerNumber
            // != 0 (i.e. a human controls one of the teams) → goto l_jmp_decide_if_flipping_direction.
            short topPl = Memory.ReadSignedWord(TeamData.TopBase + TeamData.OffPlayerNumber);
            if (topPl != 0)
                goto l_decide_if_flipping_direction;
            short botPl = Memory.ReadSignedWord(TeamData.BottomBase + TeamData.OffPlayerNumber);
            if (botPl != 0)
                goto l_decide_if_flipping_direction;
            // else l_randomly_flip_or_continue_direction
            goto l_randomly_flip_or_continue_direction;
        }

    l_randomly_flip_or_continue_direction:
        // 19033-19093 — every ~8 ticks, sometimes flip direction by 90° via
        //              AI_randomRotateTable[aiRand & 2]; write to currentAllowedDirection.
        {
            int d0Tick = gameTick & 0x18;
            if (d0Tick != 0)
                goto l_use_current_player_direction;

            // 19047 — checkForAmigaModeDirectionFlipBan(A5).
            // Ported from external/swos-port/src/game/amigaMode.cpp:64-72.
            // In PC mode this is a no-op (always clears m_preventDirectionFlip).
            // In Amiga mode it latches m_preventDirectionFlip when the player
            // sprite sits in the central goal corridor near either goal line
            // (x in [273..398] AND (y <= 158 OR y >= 740)). The latched flag is
            // read at l_use_current_player_direction to overwrite the
            // freshly-written direction with -1 (no movement) — Amiga retail
            // suppresses random 180° flips when standing in front of the goal.
            CheckForAmigaModeDirectionFlipBan(a5);

            int idx = aiRand & 2;
            short rotateAmount = Memory.ReadSignedWord(Memory.Addr.AI_randomRotateTable + idx);
            short fullDir = Memory.ReadSignedWord(a5 + PlayerSprite.OffFullDirection);
            int d0Calc = (rotateAmount + fullDir) & 0xFFFF;
            // add byte D0, -128 (byte-wise)
            int d0LoByte = ((d0Calc & 0xFF) + 0x80) & 0xFF;
            d0Calc = (d0Calc & ~0xFF) | d0LoByte;
            d0Calc = (d0Calc + 16) & 0xFFFF;
            d0Calc &= 0xFF;
            d0Calc >>= 5;
            Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d0Calc);
            return;
        }

    l_use_current_player_direction:
        // 19096-19104 — write D7 (controlled player direction) directly, then
        // run the Amiga-mode direction-flip post-hook.
        Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d7);
        // 18103 — writeAmigaModeDirectionFlip(A6).
        // Ported from external/swos-port/src/game/amigaMode.cpp:74-81. When
        // CheckForAmigaModeDirectionFlipBan has latched m_preventDirectionFlip
        // (Amiga-only goal-corridor freeze) we overwrite currentAllowedDirection
        // with -1 so the player stays put this tick.
        WriteAmigaModeDirectionFlip(a6TeamBase);
        return;

    cseg_850F9:
        // 18434-18507 — AI_ResumeGameDelay + after-touch-strength=0 + spin pick.
        // The asm contains THREE near-identical blocks (one each for strength
        // 0, 1, 2) but only the FIRST block is wired by goto. The other two are
        // dead code in the compiled binary. We port only the live block here.
        // from updatePlayers.cpp:18434
        {
            // 18435-18441 — if AI_resumePlayTimer != 0 return.
            short rptCs = Memory.ReadSignedWord(Memory.Addr.AI_resumePlayTimer);
            if (rptCs != 0) return;

            // 18443-18445 — AI_ResumeGameDelay; if !carry return.
            bool carryRgd = AiHelpers.AI_ResumeGameDelay(a6TeamBase, out _);
            if (!carryRgd) return;

            // 18447-18448 — AI_afterTouchStrength = 0.
            Memory.WriteWord(a6TeamBase + TeamData.OffAiAfterTouchStrength, 0);
            // 18449 — jmp cseg_85178.
            // fall through:
        // cseg_85178:
            // 18483 — AI_resumePlayTimer = 15.
            Memory.WriteWord(Memory.Addr.AI_resumePlayTimer, 15);
            // 18484-18486 — currentAllowedDirection = D7.
            Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d7);
            // 18487 — normalFire = 1.
            Memory.WriteByte(a6TeamBase + TeamData.OffNormalFire, 1);
            s_fireSiteCseg850F9++;

            // 18488-18495 — D0 = -1; if D2_byte sign clear → cseg_851B4 (skip neg).
            int d0Cs = -1;
            sbyte d2SignCs = (sbyte)d2Byte;
            if (d2SignCs < 0)
            {
                // 18497-18501 — neg word D0 (D0 was -1 → becomes 1).
                d0Cs = -d0Cs;
            }
        // cseg_851B4:
            // 18503-18507 — AI_ballSpinDirection = D0; return.
            Memory.WriteWord(a6TeamBase + TeamData.OffAiBallSpinDirection, d0Cs);
            return;
        }

    cseg_84F4B:
        s_trigCseg84F4B++;
        // 18147-18432 — long-range / mid-range "facing-the-ball-direction"
        // spin selector. Entered when an enemy is close-ish AND we are FAR
        // from our own goal (D6 > 180000) OR from cseg_84C93 with D6 > 48400.
        // Decides whether the AI commits to a kick toward the ball-bearing
        // direction (D7 ± 0 / 1 / -1 sector) — if so we set normalFire/spin,
        // else fall back to the regular chase code at cseg_84D10.
        // from updatePlayers.cpp:18147
        {
            // 18148-18155 — D0 = (D5 + 16) (word add, retains sign).
            int d0F4B = ((short)(d5 + 16)) & 0xFFFF;
            // 18156-18159 — D0 &= 0xFF.
            d0F4B &= 0xFF;
            // 18160-18163 — D0 >>= 5 (logical, word). Yields 0..7 sector index.
            d0F4B = (d0F4B & 0xFFFF) >> 5;
            // 18164-18170 — sub D0, D7 (word sub).
            d0F4B = (short)(d0F4B - d7);
            // 18171-18178 — D0 &= 7. Result is the (signed) sector delta in [0..7].
            d0F4B = d0F4B & 7;
            // 18179-18180 — jz cseg_84FA0.
            if (d0F4B == 0)
                goto cseg_84FA0;
            // 18182-18192 — cmp D0, 1; jz cseg_84FA0.
            if (d0F4B == 1)
                goto cseg_84FA0;
            // 18194-18204 — cmp D0, -1; jz cseg_84FA0.
            // After `& 7`, "-1" lands as 7 (two's complement low-bits) — the
            // asm cmp is word-wide so D0 here is 0..7 and -1 never matches.
            // We preserve the mechanical compare for parity (it is dead code
            // in the compiled binary).
            if (d0F4B == -1)
                goto cseg_84FA0;
            // 18206 — jmp cseg_84D10.
            goto cseg_84D10;
        }

    cseg_84FA0:
        // 18208-18223 — AI_afterTouchStrength = 2; if D6 <= 115600 → 1.
        // from updatePlayers.cpp:18208
        {
            Memory.WriteWord(a6TeamBase + TeamData.OffAiAfterTouchStrength, 2);
            // 18211-18221 — cmp D6, 115600; ja cseg_84FCA.
            if (!(d6 > 115600))
                Memory.WriteWord(a6TeamBase + TeamData.OffAiAfterTouchStrength, 1);
            // fall through into cseg_84FCA.
        }

    cseg_84FCA:
        // 18225-18432 — commit direction + pick ball-spin from ball XY+tick.
        // from updatePlayers.cpp:18225
        {
            // 18226-18228 — currentAllowedDirection = D7.
            Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, d7);
            // 18229 — normalFire = 1.
            Memory.WriteByte(a6TeamBase + TeamData.OffNormalFire, 1);
            s_fireSiteCseg84FCA++;
            // 18230-18234 — D2 = D7 << 5 (word). High bits clipped.
            int d2 = (d7 << 5) & 0xFFFF;
            // 18235-18241 — sub byte ptr D2, al (al = D5 low byte). 8-bit sub.
            int d2Lo = ((d2 & 0xFF) - (d5 & 0xFF)) & 0xFF;
            d2 = (d2 & 0xFF00) | d2Lo;
            // 18242 — AI_resumePlayTimer = 15.
            Memory.WriteWord(Memory.Addr.AI_resumePlayTimer, 15);
            // 18243 — D0 = -1 (word).
            int d0Spin = -1;
            // 18244-18250 — or al, al (al = D2 low byte); jns short cseg_85025.
            sbyte d2Sign = (sbyte)d2Lo;
            if (d2Sign >= 0)
                goto cseg_85025;
            // 18252 — neg word D0 → D0 = 1.
            d0Spin = -d0Spin;
            // fall into cseg_85025:

        cseg_85025:
            // 18254-18276 — branch on ball deltaY sign, then ball-Y bucket.
            // from updatePlayers.cpp:18254
            int ballDeltaY = BallSprite.DeltaY;
            short ballY2 = BallSprite.YPixels;
            // 18255-18261 — or eax, eax (eax = ball deltaY); js short cseg_8503F.
            if (ballDeltaY < 0)
                goto cseg_8503F;
            // 18263-18274 — cmp ballY+2, 555; ja cseg_850E1.
            if (ballY2 > 555)
                goto cseg_850E1;
            // 18276 — jmp cseg_8504E.
            goto cseg_8504E;

        cseg_8503F:
            // 18278-18290 — cmp ballY+2, 342; jb cseg_850E1.
            // from updatePlayers.cpp:18278
            if ((ushort)ballY2 < (ushort)342)
                goto cseg_850E1;
            // fall through into cseg_8504E.

        cseg_8504E:
            // 18292-18327 — D1 = (currentGameTick & 0x1C) >> 2, then X-bucket.
            // from updatePlayers.cpp:18292
            int d1 = (gameTick & 0x1C) >> 2;
            short ballX2 = BallSprite.XPixels;
            // 18303-18314 — cmp ballX+2, 193; jb cseg_85080.
            if ((ushort)ballX2 < (ushort)193)
                goto cseg_85080;
            // 18316-18327 — cmp ballX+2, 478; jb cseg_85097.
            if ((ushort)ballX2 < (ushort)478)
                goto cseg_85097;
            // fall through into cseg_85080.

        cseg_85080:
            // 18329-18356 — outer X edges: pick cseg_850C5 if very near sideline.
            // from updatePlayers.cpp:18329
            // 18330-18341 — cmp ballX+2, 118; jb cseg_850C5.
            if ((ushort)ballX2 < (ushort)118)
                goto cseg_850C5;
            // 18343-18354 — cmp ballX+2, 553; ja cseg_850C5.
            if (ballX2 > 553)
                goto cseg_850C5;
            // 18356 — jmp cseg_850AE.
            goto cseg_850AE;

        cseg_85097:
            // 18358-18379 — mid-X bucket; D1 dispatches to one of three outcomes.
            // from updatePlayers.cpp:18358
            // 18359-18365 — or ax, ax (ax = D1); jz cseg_850CF.
            if (d1 == 0)
                goto cseg_850CF;
            // 18367-18377 — cmp D1, 5; jb cseg_850E1.
            if ((ushort)d1 < (ushort)5)
                goto cseg_850E1;
            // 18379 — jmp cseg_850DA.
            goto cseg_850DA;

        cseg_850AE:
            // 18381-18402 — inner X bucket near goal lines (118..193 or 478..553).
            // from updatePlayers.cpp:18381
            // 18382-18388 — or ax, ax (ax = D1); jz cseg_850DA.
            if (d1 == 0)
                goto cseg_850DA;
            // 18390-18400 — cmp D1, 4; jb cseg_850CF.
            if ((ushort)d1 < (ushort)4)
                goto cseg_850CF;
            // 18402 — jmp cseg_850E1.
            goto cseg_850E1;

        cseg_850C5:
            // 18404-18415 — sideline corridor: cmp D1, 4; jb cseg_850E1.
            // from updatePlayers.cpp:18404
            if ((ushort)d1 < (ushort)4)
                goto cseg_850E1;
            // fall through into cseg_850CF.

        cseg_850CF:
            // 18417-18419 — D0 = 0; jmp cseg_850E1.
            // from updatePlayers.cpp:18417
            d0Spin = 0;
            goto cseg_850E1;

        cseg_850DA:
            // 18421-18426 — neg word D0 (flip spin direction sign).
            // from updatePlayers.cpp:18421
            d0Spin = (short)(-d0Spin);
            // fall through into cseg_850E1.

        cseg_850E1:
            // 18428-18432 — AI_ballSpinDirection = D0; return.
            // from updatePlayers.cpp:18428
            Memory.WriteWord(a6TeamBase + TeamData.OffAiBallSpinDirection, d0Spin);
            return;
        }

    l_ball_after_touch_allowed:
        // 19174-19306 — ball-after-touch (spin) decision block.
        // D1 = team.controlledPlDirection.
        {
            short d1Raw = Memory.ReadSignedWord(a6TeamBase + TeamData.OffControlledPlDirection);
            int d1 = d1Raw & 0xFFFF;

            // 19178-19192 — gate on playingPenalties / penalty.
            short pp = Memory.ReadSignedWord(Memory.Addr.playingPenalties);
            short pen = Memory.ReadSignedWord(Memory.Addr.penalty);
            if (pp != 0 || pen != 0)
                goto l_no_ball_after_touch_local;

            // 19194-19205 — AI_rand & 1 == 0 → no after-touch.
            if ((aiRand & 1) == 0)
                goto l_no_ball_after_touch_local;

            // 19207-19216 — read team.AI_ballSpinDirection (signed).
            //               js → l_apply_left_spin (negative = left).
            //               jnz (and !sign) → l_apply_right_spin (positive = right).
            //               else fall to l_no_ball_after_touch.
            short spinDir = Memory.ReadSignedWord(a6TeamBase + TeamData.OffAiBallSpinDirection);
            if (spinDir < 0)
                goto l_apply_left_spin_local;
            if (spinDir > 0)
                goto l_apply_right_spin_local;

        l_no_ball_after_touch_local:
            // 19218-19238 — if AI_afterTouchStrength != 1 → l_do_long_kick; else
            // write currentAllowedDirection = -1 (no kick this frame).
            short ats = Memory.ReadSignedWord(a6TeamBase + TeamData.OffAiAfterTouchStrength);
            if (ats != 1)
                goto l_do_long_kick;
            Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, -1);
            return;

        l_do_long_kick:
            // 19240-19242 — A0 = AI_longKickTable. fall to l_set_new_direction.
            {
                int a0Table = Memory.Addr.AI_longKickTable;
                int d1Adj = d1;
                goto l_set_new_direction_for_kick_pass_local;
            l_set_new_direction_for_kick_pass_local:
                short ats2 = Memory.ReadSignedWord(a6TeamBase + TeamData.OffAiAfterTouchStrength);
                int byteOff = ats2 << 1;
                short delta = Memory.ReadSignedWord(a0Table + byteOff);
                int newD1 = (d1Adj + delta) & 7;
                Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, newD1);
                return;
            }

        l_apply_left_spin_local:
            // 19244-19262 — D0 = (D1 - 1) & 7; A0 = AI_leftSpinTable.
            {
                // Retail (updatePlayers.cpp:19289-19306) computes (D1-1)&7 but
                // DISCARDS it, then adds the table delta to the UNMODIFIED D1.
                // Kept here as a dead-but-documented parity note (dead in the asm too):
                //   int d1Adj = (d1 - 1) & 7;
                int a0Table = Memory.Addr.AI_leftSpinTable;
                short ats2 = Memory.ReadSignedWord(a6TeamBase + TeamData.OffAiAfterTouchStrength);
                int byteOff = ats2 << 1;
                short delta = Memory.ReadSignedWord(a0Table + byteOff);
                int newD1 = (d1 + delta) & 7;
                Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, newD1);
                return;
            }

        l_apply_right_spin_local:
            // 19264-19306 — D0 = (D1 + 1) & 7; A0 = AI_rotateRightTable.
            {
                // Retail (updatePlayers.cpp:19289-19306) computes (D1+1)&7 but
                // DISCARDS it, then adds the table delta to the UNMODIFIED D1.
                // Kept here as a dead-but-documented parity note (dead in the asm too):
                //   int d1Adj = (d1 + 1) & 7;
                int a0Table = Memory.Addr.AI_rotateRightTable;
                short ats2 = Memory.ReadSignedWord(a6TeamBase + TeamData.OffAiAfterTouchStrength);
                int byteOff = ats2 << 1;
                short delta = Memory.ReadSignedWord(a0Table + byteOff);
                int newD1 = (d1 + delta) & 7;
                Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, newD1);
                return;
            }
        }
    }


    // ====================================================================
    // CalculateDeltaXAndY is referenced by AI_SetControlsDirection and lives
    // in SpriteUpdate.cs (port of updateSprite.cpp:231-336). The asm wrapper
    // CalculateDeltaXAndY (updateSprite.cpp:355-367) returns the angle in D0
    // and sets flags.sign from `result.direction < 0`. We invoke the helper
    // directly above where the original asm calls it.
    // ====================================================================

    // ====================================================================
    // Amiga-mode direction-flip hooks — ported from
    // external/swos-port/src/game/amigaMode.cpp:64-81.
    // ====================================================================
    //
    // Upstream tracks two module-static flags inside amigaMode.cpp:
    //
    //   static bool m_enabled;               // toggled by setAmigaModeEnabled
    //   static bool m_preventDirectionFlip;  // latched by checkForAmigaModeDirectionFlipBan,
    //                                        // consumed by writeAmigaModeDirectionFlip
    //
    // We mirror m_preventDirectionFlip as a private static on AiBrain because
    // these two helpers are its only producer/consumer pair in retail. The
    // m_enabled equivalent is the module-shared `GameTime.AmigaModeActive`
    // (PC-locked to false until Amiga support lands — CLAUDE.md "Engine and
    // rendering (locked-in decisions)").
    //
    // Behaviour summary:
    //   - PC mode (AmigaModeActive == false): both helpers are no-ops by
    //     construction. The latch never sets, so the post-hook never writes.
    //     We keep the calls live so swapping to Amiga mode is one flag flip.
    //   - Amiga mode: the latch fires when the controlled player sprite is
    //     standing in the central goal corridor (x in [273..398] AND y in
    //     either goal-mouth band). When latched, the next
    //     l_use_current_player_direction execution writes
    //     currentAllowedDirection = -1, suppressing AI's random 180° flip
    //     while it's lined up for a shot.
    //
    // Both helpers also drive the asm `flags.zero` value the original VM uses
    // to chain comparisons. None of our current callers branch on that flag,
    // so we discard the boolean return — but we keep the helpers returning it
    // for future Amiga-mode-only callers.

    // amigaMode.cpp:7 — module-static, default false. Set true by
    // CheckForAmigaModeDirectionFlipBan when the player sits in the goal
    // corridor, cleared otherwise. WriteAmigaModeDirectionFlip reads it.
    private static bool s_amigaPreventDirectionFlip;

    // amigaMode.cpp:64-72 — checkForAmigaModeDirectionFlipBan.
    // Returns the asm zero-flag value (true = zero flag set = ban CLEARED).
    // PC mode clears the latch unconditionally; Amiga mode sets it inside the
    // goal-corridor window. Returns true when ban is NOT active (the asm
    // semantics — zero flag is the NEGATION of m_preventDirectionFlip).
    private static bool CheckForAmigaModeDirectionFlipBan(int a5SpriteAddr)
    {
        // amigaMode.cpp:66 — m_preventDirectionFlip = false.
        s_amigaPreventDirectionFlip = false;
        // amigaMode.cpp:67 — if (amigaModeActive()) { ... }.
        if (GameTime.AmigaModeActive() && a5SpriteAddr != 0)
        {
            // amigaMode.cpp:68-69 — whole-pixel x/y read off the FixedPoint
            // members. The high half of the Q16.16 sprite.x lives at OffX+2.
            short xWhole = Memory.ReadSignedWord(a5SpriteAddr + PlayerSprite.OffX + 2);
            short yWhole = Memory.ReadSignedWord(a5SpriteAddr + PlayerSprite.OffY + 2);
            s_amigaPreventDirectionFlip =
                xWhole >= 273 && xWhole <= 398 &&
                (yWhole <= 158 || yWhole >= 740);
            // amigaMode.cpp:70 — SwosVM::flags.zero = !m_preventDirectionFlip.
            // We don't maintain a global SwosVM::flags; expose via return.
        }
        return !s_amigaPreventDirectionFlip;
    }

    // amigaMode.cpp:74-81 — writeAmigaModeDirectionFlip.
    // When m_preventDirectionFlip is latched, writes -1 to
    // team.currentAllowedDirection (replacing whatever D7 / fall-through code
    // wrote before this call). Returns true when a write happened.
    private static bool WriteAmigaModeDirectionFlip(int a6TeamBase)
    {
        // amigaMode.cpp:76 — if (m_preventDirectionFlip) { ... }.
        if (s_amigaPreventDirectionFlip)
        {
            // amigaMode.cpp:77 — team->currentAllowedDirection = -1.
            Memory.WriteWord(a6TeamBase + TeamData.OffCurrentAllowedDirection, -1);
            // amigaMode.cpp:78-79 — D0.lo16 = -1; SwosVM::ax = -1.
            // We don't maintain VM-wide D0/ax registers across function calls,
            // and no downstream reader in this function path consumes them.
            return true;
        }
        return false;
    }
}
