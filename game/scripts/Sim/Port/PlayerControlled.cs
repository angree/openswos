namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Human-controlled outfielder branch ported from
//   external/swos-port/src/game/updatePlayers/updatePlayers.cpp
// lines 4398..5779 — the `l_its_controlled_player:` block.
//
// **NOTE: `updatePlayerWithBall()` and `updateControllingPlayer()` live in
// `player.cpp`, NOT in this file.** Per the task brief, those are being
// ported by another agent. This file only contains the calling-site logic
// (the giant inline branch inside `updatePlayers()` that runs each tick for
// the human-controlled player on a team that has a controlledPlayer set).
//
// **Source mapping** (line numbers refer to updatePlayers.cpp):
//   - l_its_controlled_player entry        ........  4398
//     (playingPenalties + gameState gate)  ........  4399-4418
//   - l_check_for_cpu_team                 ........  4420
//     (run AI_SetControlsDirection if AI team)
//   - allowedDirections / shooting bookkeeping...  4438-4459
//   - break-handling (gameStatePl != 100)  ........  4461-4717
//     · turn-flag re-pick (cameraDirection override)
//     · find_acceptable_turn_flags loop
//     · result panel hide
//     · test_direction / updatePlayerWithBall call
//   - l_skip_break_handling                ........  4720-...
//     · playerHasBall short-circuit
//     · header attempt (attemptStaticHeader)
//     · cseg_80FFD = ball-becomes-free branch
//     · l_calculate_if_player_wins_ball    ........  5046
//     · l_test_quick_fire / doPass         ........  5067-5237
//     · l_no_passing / playerKickingBall   ........  5239-5407
//     · l_check_if_goalkeeper / tackle/header ....  5409-5614
//     · l_not_a_header / pitch-clamp dest update.  5616-5778
//
// **Mechanical port discipline**:
//   - Goto-labels preserved as goto labels (C# supports goto).
//   - SWOS asm magic numbers (TeamGeneralInfo offsets +32/+40/+42/...) reused
//     via the constants on TeamData / PlayerSprite (when available), else
//     written as raw offsets to match the asm exactly.
//   - All arithmetic is integer; no float anywhere.
//   - Calls into not-yet-ported functions (AI_SetControlsDirection,
//     attemptStaticHeader, playerKickingBall, doPass, playerBeginTackling,
//     playerAttemptingJumpHeader, calculateIfPlayerWinsBall,
//     updatePlayerWithBall) are wired through Stubs.cs entries below so
//     the file builds clean while waiting for the other agents.
//
// In:
//   - spriteAddr (= A1)  — controlled player sprite address.
//   - topTeam            — selects A6 = TopBase / BottomBase.
//   - A2 / A3 / A5       — SWOS scratch registers; we pass nothing.
//
// **Exit branches** (jumps OUT of this block in the asm):
//   - l_update_player_speed_and_deltas → caller falls through to
//     updatePlayerSpeedAndFrameDelay. We represent this with `return Exit.UpdateSpeed`.
//   - l_stop_player → set sprite.dest{X,Y} = sprite.{x,y} then fall through to
//     updatePlayerSpeedAndFrameDelay. `return Exit.StopPlayer`.
//   - Caller of UpdatePlayers should handle both: stop-player overwrites
//     dest before the speed update.
public static class PlayerControlled
{
    public enum Exit
    {
        UpdateSpeed,   // l_update_player_speed_and_deltas
        StopPlayer,    // l_stop_player → sets dest=pos then updates speed
    }

    // Per-team offsets the asm reads via `[esi + N]`. Some of these are not
    // yet present on TeamData; declared locally for explicit-asm-mapping.
    private const int OffOpponentsTeam      =   0; // dword
    private const int OffPlayerNumber       =   4; // word (0 = AI team)
    private const int OffControlledPlayer   =  32; // dword
    private const int OffPassToPlayerPtr    =  36; // dword (passToPlayerPtr / A5 in asm at 5468)
    private const int OffPlayerHasBall      =  40; // word
    private const int OffAllowedDirections  =  42; // word
    private const int OffCurrentAllowedDir  =  44; // word
    private const int OffQuickFire          =  48; // byte
    private const int OffNormalFire         =  49; // byte
    private const int OffFirePressed        =  50; // byte
    private const int OffFireThisFrame      =  51; // byte
    private const int OffHeaderOrTackle     =  52; // word
    private const int OffShooting           =  58; // word
    private const int OffPlVeryCloseToBall  =  61; // byte
    private const int OffPlCloseToBall      =  62; // byte
    private const int OffPlNotFarFromBall   =  63; // byte
    private const int OffBallLessEqual4     =  64; // byte
    private const int OffBall4To8           =  65; // byte
    private const int OffBall8To12          =  66; // byte
    private const int OffBall12To17         =  67; // byte
    private const int OffBallAbove17        =  68; // byte
    private const int OffPrevPlVeryClose    =  69; // byte
    private const int OffLastHeadingPlayer  =  72; // dword (lastHeadingTacklingPlayer)
    private const int OffGoalieSavedTimer   =  76; // word (goalkeeperSavedCommentTimer)
    private const int OffBallInPlay         =  94; // word
    private const int OffBallOutOfPlay      =  96; // word
    private const int OffPassKickTimer      = 102; // word
    private const int OffPassingToPlayer    =  90; // word
    private const int OffPassingKickingPlayer = 104; // dword
    private const int OffOfs108             = 108; // word (unkBallTimer)
    private const int OffBallCanBeControlled = 110; // word
    private const int OffBallCtrlPlDirection = 112; // word
    private const int OffSpinTimer          = 118; // word
    private const int OffWonTheBallTimer    = 138; // word

    // Telemetry: how often the AI-fire / off-ball kick fallback heuristics fire.
    // Tracks the writes at the two `=== AI-FIRE FALLBACK ===` blocks below (one
    // in RunControlledBranch, one in the off-ball outfielder iteration). When
    // AiBrain's deep branches (cseg_84A85 → cseg_850F9, cseg_84F4B → cseg_84FCA,
    // l_activate_normal_fire) cover the same conditions, these counters should
    // trend toward zero.
    private static int s_fireFallbackTop = 0;
    private static int s_fireFallbackBot = 0;
    public static int FireFallbackTop => s_fireFallbackTop;
    public static int FireFallbackBot => s_fireFallbackBot;
    public static void ResetFireFallbackCounters()
    {
        s_fireFallbackTop = 0;
        s_fireFallbackBot = 0;
    }

    // Telemetry: ball-arbitration chain (cseg_80FFD → cseg_8108A → CalculateIfPlayerWinsBall).
    // Tracks how often the cseg_80FFD dribble-pin chain actually fires, and how
    // many of those reach the CalculateIfPlayerWinsBall call site (i.e. pass
    // every short-circuit gate at 4962-4994 in updatePlayers.cpp).
    //   - EnterCseg80FFD       = entered the cseg_80FFD label (cad >= 0 path)
    //   - ReachedCwbCall       = reached the CalculateIfPlayerWinsBall call (5046+)
    private static int s_enterCseg80FFD = 0;
    private static int s_reachedCwbCall = 0;
    // Skill-arbitration outcome (player.cpp:325-425): counts winners after
    // tackling+ballControl average diff vs Rand() roll against
    // kPlAvgTacklingBallControlDiffChance. Lets the smoke test verify the
    // ball-arbitration table is actually skill-driven (not a constant 50/50).
    //   - SkillDuelOwnWin = own team won the post-Rand check
    //   - SkillDuelOppWin = opp team won the post-Rand check
    private static int s_skillDuelOwnWin = 0;
    private static int s_skillDuelOppWin = 0;
    // Per-tick controlled-player ball-pin fire counter (added 2026-06-01).
    // Counts how many ticks RunControlledBranch invokes
    // PlayerActions.UpdateControllingPlayer at cseg_80E8F (the
    // "controller currently has the ball" gate). When this fires every tick
    // the carrier owns the ball, plVeryCloseToBall at the original 32-sq-px
    // threshold should latch consistently because the ball is glued ±1 px.
    // Source: external/swos-port/src/game/player.cpp:135-191 +
    //         external/swos-port/src/game/updatePlayers/updatePlayers.cpp:4792-4810.
    private static int s_pinControlledPerTick = 0;
    public static int EnterCseg80FFD => s_enterCseg80FFD;
    public static int ReachedCwbCall => s_reachedCwbCall;
    public static int SkillDuelOwnWin => s_skillDuelOwnWin;
    public static int SkillDuelOppWin => s_skillDuelOppWin;
    public static int PinControlledPerTick => s_pinControlledPerTick;
    public static void IncSkillDuelOwnWin() { s_skillDuelOwnWin++; }
    public static void IncSkillDuelOppWin() { s_skillDuelOppWin++; }
    public static void ResetBallArbCounters()
    {
        s_enterCseg80FFD = 0;
        s_reachedCwbCall = 0;
        s_skillDuelOwnWin = 0;
        s_skillDuelOppWin = 0;
        s_pinControlledPerTick = 0;
    }


    // Per-sprite offsets (A1). Most defined on PlayerSprite; mirrored here.
    private const int SOffPlayerOrdinal     =   2; // word — 1 = goalkeeper
    private const int SOffX                 =  32; // word ptr [A1+32]  → sprite.x.whole
    private const int SOffY                 =  36; // word ptr [A1+36]  → sprite.y.whole
    private const int SOffDirection         =  42; // word
    private const int SOffShooting          =  58; // word (NOTE: collides with team.shooting — asm at 4768 writes to sprite[58]; ambiguous, treat as TeamData offset since esi=A6 there).
    private const int SOffDestX             =  58; // word
    private const int SOffDestY             =  60; // word
    private const int SOffBallDistance      =  74; // dword
    private const int SOffZWhole            =  40; // word ptr [A2+40] — sprite.z.whole (ball, A2 = ballSprite)

    // (2026-07-02: the temporary dseg_17E276 lazy seed that lived here has
    // been deleted — Memory.Init now seeds the table properly, swos.asm:245800.)

    // ===================================================================
    // RunControlledBranch — l_its_controlled_player (updatePlayers.cpp:4398-5779)
    // ===================================================================
    public static Exit RunControlledBranch(int spriteAddr, bool topTeam)
    {
        // A1 = spriteAddr; A6 = teamBase; A0 = opponentTeamBase; A2 = ballSprite.
        int teamBase     = TeamData.Base(topTeam);
        int opponentBase = TeamData.Base(!topTeam);
        int ballSprite   = BallSprite.Base;

        // updatePlayers.cpp:4398 — l_its_controlled_player:
        // updatePlayers.cpp:4399-4418 — playingPenalties != 0 AND gameState != ST_PENALTIES → bail out.
        short ax;
        ax = Memory.ReadSignedWord(Memory.Addr.playingPenalties);
        if (ax != 0)
        {
            short gameStateVal = Memory.ReadSignedWord(Memory.Addr.gameState);
            // cmp gameState, ST_PENALTIES (31)
            if (gameStateVal != 31)
            {
                return Exit.UpdateSpeed;
            }
        }

        // l_check_for_cpu_team: updatePlayers.cpp:4420-4436.
        // If playerNumber == 0 (CPU/AI team), run AI_SetControlsDirection.
        short playerNumber = Memory.ReadSignedWord(teamBase + OffPlayerNumber);
        if (playerNumber == 0)
        {
            // Real port — updatePlayers.cpp:15980 → AiBrain.SetControlsDirection.
            // Asm sig takes A6 (teamBase) only; the spriteAddr is implicit via
            // team.controlledPlayer. Pass teamBase.
            AiBrain.SetControlsDirection(teamBase);

            // === DRIBBLE-PIN FALLBACK (RETAINED 2026-06-01) ===================
            // This branch is a STAND-IN for the asm's natural cad write path,
            // not a deviation from it.
            //
            // In retail SWOS, when the AI team owns the ball, AiBrain.SetControls-
            // Direction always commits a non-negative cad before returning:
            //   - `l_theres_a_player_near` calls AI_DecideWhetherToTriggerFire
            //     which writes cad (via the "candidate #1" rotate) when the
            //     facing+Z windows match, OR falls through to the chase tree at
            //     cseg_84A0D → cseg_84AEB / cseg_84DD3 → cseg_84DE0 / cseg_84E16
            //     which writes cad either from D5 (ball-angle bucket) or from
            //     sprite.direction directly.
            //   - `l_noone_near → l_pass_to_player_too_far_or_null →
            //     l_decide_if_flipping_direction / l_use_current_player_direction`
            //     also writes cad on every branch.
            //
            // OpenSwos AiBrain reproduces those paths, but several gates depend
            // on TEAM-LEVEL flags that the per-sprite dispatcher only computes
            // for the LAST sprite in the bucket loop (plVeryCloseToBall,
            // plCloseToBall, frameCount &0x7F at cseg_84D10, etc.). Because we
            // call AiBrain per AI sprite (not once per team like the asm), some
            // mid-loop calls hit unpopulated team flags and AiBrain returns
            // early at the `l_theres_a_player_near` AI_DecideWhetherToTriggerFire
            // no_fire exit (no cad written). Telemetry confirms this is the
            // edge: `[ai-fire] cseg_850F9=8 cseg_84FCA=29` over 3000 ticks —
            // the fire-commit paths run, but the chase-only paths (which only
            // *write* cad without firing) don't get reached for every sprite.
            //
            // The compensating write below mirrors the asm's
            // `cseg_84E16: writeMemory(esi + 44, 2, [A5+Sprite.direction])`
            // (updatePlayers.cpp:18011-18014) — same source register, same
            // destination field. Gating on `playerHasBall != 0` restricts it to
            // the carrier-tick condition the upstream chase tree would also be
            // running under. Without this, cseg_80FFD →
            // CalculateIfPlayerWinsBall is skipped because cseg_80ECF gates on
            // `cad >= 0` (PlayerControlled.cs:534) and the ball stays at rest
            // while the carrier walks past it.
            //
            // TODO drop once AiBrain is restructured to run once per team
            // (asm-faithful dispatch) instead of once per AI sprite.
            //
            // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:18011-18014
            //         (cseg_84E16 — writes cad = A5.direction unconditionally).
            short adFix = Memory.ReadSignedWord(teamBase + OffCurrentAllowedDir);
            if (adFix < 0)
            {
                short hasBallFix = Memory.ReadSignedWord(teamBase + OffPlayerHasBall);
                if (hasBallFix != 0)
                {
                    int ctrlFix = Memory.ReadSignedDword(teamBase + OffControlledPlayer);
                    if (ctrlFix != 0)
                    {
                        short spriteDirFix = Memory.ReadSignedWord(ctrlFix + SOffDirection);
                        if (spriteDirFix >= 0 && spriteDirFix < 8)
                        {
                            Memory.WriteWord(teamBase + OffCurrentAllowedDir, spriteDirFix);
                        }
                    }
                }
            }

            // === AI-FIRE FALLBACK (REMOVED 2026-06-01) =======================
            // Was: write team.currentAllowedDirection = sprite.direction and
            // normalFire = 1 when AiBrain.SetControlsDirection left the team in
            // an unprimed-but-ball-owning state. Empirically dead: 3000-tick
            // smoke shows the AiBrain cseg_84FCA path (updatePlayers.cpp:18229)
            // and cseg_850F9 path (18487) already commit normalFire on the
            // canonical path — the FIRE FALLBACK counter measured 0/0 events
            // every run before this removal, so the block was pure scaffolding.
            // Telemetry counters s_fireFallbackTop/Bot retained (always 0) for
            // Main.cs:713 API compatibility.
            //
            // Source: external/swos-port/src/game/updatePlayers/
            //   updatePlayers.cpp:18229 (cseg_84FCA) + :18487 (cseg_850F9).
        }

        // cseg_80C0C: updatePlayers.cpp:4438-4449.
        // allowedDirections = playerHasBall; playerHasBall = 0.
        ax = Memory.ReadSignedWord(teamBase + OffPlayerHasBall);
        Memory.WriteWord(teamBase + OffAllowedDirections, ax);
        Memory.WriteWord(teamBase + OffPlayerHasBall, 0);
        // shooting reset unless firePressed != 0.
        short shooting = Memory.ReadSignedWord(teamBase + OffShooting);
        if (shooting != 0)
        {
            byte firePressed = Memory.ReadByte(teamBase + OffFirePressed);
            if (firePressed == 0)
            {
                Memory.WriteWord(teamBase + OffShooting, 0);
            }
        }

        // cseg_80C56: updatePlayers.cpp:4461-4473. gameStatePl != 100 → break-handling path.
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        if (gameStatePl == 100)
        {
            goto l_skip_break_handling;
        }

        // updatePlayers.cpp:4475-4486. lastTeamPlayedBeforeBreak != A6 → bail out.
        {
            int lastTeamPlayedBeforeBreak = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
            if (teamBase != lastTeamPlayedBeforeBreak)
            {
                return Exit.UpdateSpeed;
            }
        }

        // updatePlayers.cpp:4488-4509. If (1 << sprite.direction) NOT in
        // playerTurnFlags → cameraDirection override + dseg_132806++.
        // updatePlayers.cpp:4488: ax = sprite.direction.
        short spriteDirection = Memory.ReadSignedWord(spriteAddr + SOffDirection);
        short d0 = spriteDirection;
        {
            // shl ax, cl where ax=1, cl=spriteDirection. Mask cl to 5 bits.
            int shift = d0 & 0x1F;
            int mask = (1 << shift) & 0xFFFF;
            byte playerTurnFlags = Memory.ReadByte(Memory.Addr.playerTurnFlags);
            int test = playerTurnFlags & mask;
            if (test == 0)
            {
                // cseg_80CB3 fall-through path (NOT taken means we entered this if).
                // Actually asm: jnz cseg_80CB3 → if zero we enter this block (override).
                short cameraDir = Memory.ReadSignedWord(Memory.Addr.cameraDirection);
                Memory.WriteWord(spriteAddr + SOffDirection, cameraDir);
                short cnt = Memory.ReadSignedWord(Memory.Addr.dseg_132806);
                Memory.WriteWord(Memory.Addr.dseg_132806, (short)(cnt + 1));
            }
        }

        // cseg_80CB3: updatePlayers.cpp:4522-4544.
        // Same test again with possibly-updated sprite.direction; if STILL fails,
        // we go into the find-loop to pick any acceptable direction.
        spriteDirection = Memory.ReadSignedWord(spriteAddr + SOffDirection);
        d0 = spriteDirection;
        {
            int shift = d0 & 0x1F;
            int mask = (1 << shift) & 0xFFFF;
            byte playerTurnFlags = Memory.ReadByte(Memory.Addr.playerTurnFlags);
            int test = playerTurnFlags & mask;
            if (test != 0) goto l_turn_flags_acceptable;
        }

        // updatePlayers.cpp:4546-4574 — find_acceptable_turn_flags_loop.
        // D0 = 7; while (--D0 >= 0) if (playerTurnFlags & (1<<D0)) break.
        d0 = 7;
    l_find_acceptable_turn_flags_loop:
        {
            int shift = d0 & 0x1F;
            int mask = (1 << shift) & 0xFFFF;
            byte playerTurnFlags = Memory.ReadByte(Memory.Addr.playerTurnFlags);
            int test = playerTurnFlags & mask;
            if (test != 0) goto l_created_acceptable_turn_flags;
            d0 = (short)(d0 - 1);
            if (d0 >= 0) goto l_find_acceptable_turn_flags_loop;
            d0 = 0;
        }
    l_created_acceptable_turn_flags:
        // updatePlayers.cpp:4578-4590.
        Memory.WriteWord(Memory.Addr.cameraDirection, d0);
        Memory.WriteWord(spriteAddr + SOffDirection, d0);
        {
            short cnt = Memory.ReadSignedWord(Memory.Addr.deadThrowInDirectionVar);
            Memory.WriteWord(Memory.Addr.deadThrowInDirectionVar, (short)(cnt + 1));
        }

    l_turn_flags_acceptable:
        // updatePlayers.cpp:4592-4615.
        // If playerNumber == 0 (CPU) AND stoppageTimerActive > 55 → l_hide_result.
        playerNumber = Memory.ReadSignedWord(teamBase + OffPlayerNumber);
        if (playerNumber == 0)
        {
            short stoppageActive = Memory.ReadSignedWord(Memory.Addr.stoppageTimerActive);
            if (stoppageActive > 55)
            {
                goto l_hide_result;
            }
            goto l_test_fire;
        }

        // cseg_80D49: updatePlayers.cpp:4617-4625. If currentAllowedDirection < 0 (sign set)
        // → l_joy_any_fire_pressed.
        {
            short cad = Memory.ReadSignedWord(teamBase + OffCurrentAllowedDir);
            if (cad < 0) goto l_joy_any_fire_pressed;
        }

    l_test_fire:
        // updatePlayers.cpp:4627-4651. Fire/quickFire/normalFire byte tests.
        {
            byte v = Memory.ReadByte(teamBase + OffFirePressed);
            if (v != 0) goto l_joy_any_fire_pressed;
            v = Memory.ReadByte(teamBase + OffQuickFire);
            if (v != 0) goto l_joy_any_fire_pressed;
            v = Memory.ReadByte(teamBase + OffNormalFire);
            if (v == 0) goto l_test_direction;
        }

    l_joy_any_fire_pressed:
        // updatePlayers.cpp:4653-4662. If statsTimer != 0 → fireBlocked = 1, else hide result.
        {
            short st = Memory.ReadSignedWord(Memory.Addr.statsTimer);
            if (st == 0) goto l_hide_result;
            Memory.WriteWord(Memory.Addr.fireBlocked, 1);
        }

    l_hide_result:
        // updatePlayers.cpp:4664-4674. timeVar = -timeVar; resultTimer = -resultTimer.
        {
            short t = Memory.ReadSignedWord(Memory.Addr.timeVar);
            Memory.WriteWord(Memory.Addr.timeVar, (short)(-t));
            int r = Memory.ReadSignedDword(Memory.Addr.resultTimer);
            Memory.WriteDword(Memory.Addr.resultTimer, -r);
        }

    l_test_direction:
        // updatePlayers.cpp:4676-4691. currentAllowedDirection < 0 → set from sprite.direction
        // then jump to l_skip_break_handling. Else fall through to test playerTurnFlags.
        {
            short cad = Memory.ReadSignedWord(teamBase + OffCurrentAllowedDir);
            d0 = cad;
            if (cad < 0)
            {
                // cseg_80DB6 — sprite.direction → currentAllowedDirection.
                short dir = Memory.ReadSignedWord(spriteAddr + SOffDirection);
                Memory.WriteWord(teamBase + OffCurrentAllowedDir, dir);
                goto l_skip_break_handling;
            }
        }

        // cseg_80DCC: updatePlayers.cpp:4694-4717. Mask test against playerTurnFlags.
        {
            int shift = d0 & 0x1F;
            int mask = (1 << shift) & 0xFFFF;
            byte playerTurnFlags = Memory.ReadByte(Memory.Addr.playerTurnFlags);
            int test = playerTurnFlags & mask;
            if (test == 0)
            {
                // Failed → cseg_80DB6 (re-snap from sprite.direction).
                short dir = Memory.ReadSignedWord(spriteAddr + SOffDirection);
                Memory.WriteWord(teamBase + OffCurrentAllowedDir, dir);
                goto l_skip_break_handling;
            }
            // Pass → write d0 to sprite.direction, then call UpdatePlayerWithBall.
            Memory.WriteWord(spriteAddr + SOffDirection, d0);
            // Real port — player.cpp:85 → PlayerActions.UpdatePlayerWithBall.
            PlayerActions.UpdatePlayerWithBall(spriteAddr);
        }

    l_skip_break_handling:
        // updatePlayers.cpp:4720-4728. If sprite[+40].playerHasBall != 0 → exit.
        // NOTE: The asm reads esi+40 with esi=A1 — that is sprite[40]. Sprite
        // does NOT have a "playerHasBall" field at +40; this is treated as
        // word access on the sprite struct (PlayerSprite layout). We mirror.
        ax = Memory.ReadSignedWord(spriteAddr + 40);
        if (ax != 0) return Exit.UpdateSpeed;

        // updatePlayers.cpp:4730-4743. currentAllowedDirection >= 0 (sign clear)
        // → skip; else copy sprite.x/y to sprite.destX/destY.
        {
            short cad = Memory.ReadSignedWord(teamBase + OffCurrentAllowedDir);
            if (cad < 0)
            {
                short sx = Memory.ReadSignedWord(spriteAddr + SOffX);
                Memory.WriteWord(spriteAddr + SOffDestX, sx);
                short sy = Memory.ReadSignedWord(spriteAddr + SOffY);
                Memory.WriteWord(spriteAddr + SOffDestY, sy);
            }
        }

        // cseg_80E41: updatePlayers.cpp:4745-4790.
        // If gameStatePl == 100 AND quickFire == 0 AND shooting == 0
        //    AND (plVeryCloseToBall != 0 OR plCloseToBall != 0)
        //    AND opponentsTeam → l_ball_becomes_free chain at cseg_80E8F.
        gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        if (gameStatePl != 100) goto l_test_quick_fire;
        {
            short qf = Memory.ReadSignedWord(teamBase + OffQuickFire);
            if (qf != 0) goto l_test_quick_fire;
            short sh = Memory.ReadSignedWord(teamBase + OffShooting);
            if (sh != 0) goto l_test_quick_fire;
            byte pvc = Memory.ReadByte(teamBase + OffPlVeryCloseToBall);
            if (pvc != 0) goto cseg_80E8F;
            byte pcb = Memory.ReadByte(teamBase + OffPlCloseToBall);
            if (pcb == 0) goto cseg_80ECF;
        }

    cseg_80E8F:
        // updatePlayers.cpp:4792-4810. If allowedDirections == 0 → clear unkBallTimer.
        // Set playerHasBall = 1; set opponent.spinTimer = -1.
        {
            short adp = Memory.ReadSignedWord(teamBase + OffAllowedDirections);
            if (adp == 0)
            {
                Memory.WriteWord(teamBase + OffOfs108, 0);
            }
            Memory.WriteWord(teamBase + OffPlayerHasBall, 1);
            Memory.WriteWord(opponentBase + OffSpinTimer, -1);

            // === PER-TICK BALL PIN (2026-06-01) ============================
            // Wire updateControllingPlayer here so the ball gets glued to the
            // carrier each tick they have it — not just on pass receipt.
            //
            // In the upstream C++ port `updateControllingPlayer` is only
            // called once from the pass-receipt commit at
            // external/swos-port/src/game/updatePlayers/updatePlayers.cpp:8252
            // (the cseg_82D59 swap of passToPlayerPtr → controlledPlayer).
            // From then on the real game keeps the ball ahead of the runner
            // via calculateIfPlayerWinsBall's "nudge ball forward" branch
            // (external/swos-port/src/game/player.cpp:442-685) plus the
            // continuous physics update — the chasing ball never escapes
            // because player speed >= ball speed when controlled.
            //
            // Our port's physics + AI chase are still partially stubbed, so
            // a loose ball at kick-off never gets pinned without first being
            // passed, and the carrier's plVeryCloseToBall gate (32 sq-px)
            // is too tight for a ball travelling at ~2200 vs a player at
            // ~970. Calling UpdateControllingPlayer here each tick the
            // carrier is plVeryCloseToBall OR plCloseToBall (the very gate
            // that brought us into cseg_80E8F) restores the canonical
            // ±1 px glue from external/swos-port/src/game/player.cpp:135-191
            // and lets us roll back the temporary widened gate in
            // UpdatePlayerBallDistanceAndHeight (UpdatePlayers.cs:1112-1115)
            // to the original 32 sq-px.
            //
            // Keeper ordinal (==1) handled separately because the keeper
            // uses kBallPlOffsets's keeper variant in
            // updateBallWithControllingGoalkeeper (player.cpp:200-265).
            // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:8235-8252
            //         (pass-receipt path branches on ordinal exactly like this).
            //
            // === PIN POSSESSION GATE (2026-07-02, duel ping-pong fix) ======
            // The pin is a compensating hack NOT present in the original —
            // asm cseg_80E8F/cseg_80EAA (updatePlayers.cpp:4792-4810) only
            // sets playerHasBall=1 + opponent.spinTimer=-1 and never moves
            // the ball. Because this block runs BEFORE the wonTheBallTimer
            // gate at cseg_80FFD (updatePlayers.cpp:4936-4960, ported below
            // at label cseg_80FFD) and before the CalculateIfPlayerWinsBall
            // duel (player.cpp:276-440), an ungated pin lets:
            //   (a) a team locked out by a lost duel / tackle
            //       (wonTheBallTimer != 0, swos.asm:108151 "= 12" /
            //       swos.asm:115891 "= 12" / swos.asm:108310 "= 8") still
            //       physically yank the ball to its player's feet, and
            //   (b) a challenger grab the ball off a legitimate holder with
            //       a bare touch, when the original requires winning the
            //       CalculateIfPlayerWinsBall skill duel first
            //       (player.cpp:283-305 gates: opponent.wonTheBallTimer == 0
            //       AND opponent.playerHasBall != 0 AND opponent moving).
            // Both produced the user-visible "possession flips back and
            // forth every tick" bug. Gate the pin (and ONLY the pin — the
            // playerHasBall/spinTimer/unkBallTimer writes above stay
            // unconditional, faithful to cseg_80EAA) on the same conditions
            // under which the original lets this team's player control the
            // ball this tick:
            //   - own wonTheBallTimer == 0   (cseg_81028 skip,
            //     updatePlayers.cpp:4954-4960)
            //   - NOT (opponent holds the ball AND opponent is not locked
            //     out) — that contested case is decided by the duel below,
            //     not by a touch-steal (player.cpp:283-305).
            short pinOwnWbt   = Memory.ReadSignedWord(teamBase + OffWonTheBallTimer);
            short pinOppHas   = Memory.ReadSignedWord(opponentBase + OffPlayerHasBall);
            short pinOppWbt   = Memory.ReadSignedWord(opponentBase + OffWonTheBallTimer);
            bool  pinAllowed  = pinOwnWbt == 0 && !(pinOppHas != 0 && pinOppWbt == 0);
            if (pinAllowed)
            {
                short pinOrdinal = Memory.ReadSignedWord(spriteAddr + SOffPlayerOrdinal);
                if (pinOrdinal == 1)
                {
                    // Keeper glue is canonical (updateBallWithControllingGoalkeeper) — always on.
                    PlayerUpdate.UpdateBallWithControllingGoalkeeper(spriteAddr);
                    s_pinControlledPerTick++;
                }
                else if (!PlayerActions.FaithfulBallControl)
                {
                    // Outfield per-tick pin = the non-original hack that masks the
                    // CONTROL skill. OFF by default (FaithfulBallControl) so the ball
                    // runs away from weak dribblers per CalculateIfPlayerWinsBall.
                    PlayerActions.UpdateControllingPlayer(spriteAddr);
                    s_pinControlledPerTick++;
                }
            }
        }

    cseg_80ECF:
        // updatePlayers.cpp:4812-5045. The MAIN "ball control" arbitration block.
        // currentAllowedDirection < 0 (sign set) → check various paths;
        //   else (>= 0) → cseg_80FFD (we have a directional input).
        {
            short cad = Memory.ReadSignedWord(teamBase + OffCurrentAllowedDir);
            d0 = cad;
            if (cad >= 0) goto cseg_80FFD;

            // playerNumber == 0 (CPU) → l_jmp_update_player_speed.
            short pn = Memory.ReadSignedWord(teamBase + OffPlayerNumber);
            if (pn == 0) return Exit.UpdateSpeed;

            // sprite.playerOrdinal == 1 (goalkeeper) → l_jmp_update_player_speed.
            short ord = Memory.ReadSignedWord(spriteAddr + SOffPlayerOrdinal);
            if (ord == 1) return Exit.UpdateSpeed;

            // fireThisFrame == 0 → exit.
            byte ftf = Memory.ReadByte(teamBase + OffFireThisFrame);
            if (ftf == 0) return Exit.UpdateSpeed;

            // currentAllowedDirection (re-read) >= 0 → exit (signed comparison;
            // we already know <0, but asm re-checks here; keep parity).
            short cad2 = Memory.ReadSignedWord(teamBase + OffCurrentAllowedDir);
            if (cad2 >= 0) return Exit.UpdateSpeed;

            // ball-distance bucket check. plVeryCloseToBall | plCloseToBall | plNotFarFromBall must be set.
            byte pvc = Memory.ReadByte(teamBase + OffPlVeryCloseToBall);
            byte pcb = Memory.ReadByte(teamBase + OffPlCloseToBall);
            byte pnf = Memory.ReadByte(teamBase + OffPlNotFarFromBall);
            if (pvc == 0 && pcb == 0 && pnf == 0) return Exit.UpdateSpeed;

            // cseg_80F61: ball-height bucket check. ball8To12 | ball12To17 | ballAbove17.
            byte b8 = Memory.ReadByte(teamBase + OffBall8To12);
            byte b12 = Memory.ReadByte(teamBase + OffBall12To17);
            byte ba = Memory.ReadByte(teamBase + OffBallAbove17);
            if (b8 == 0 && b12 == 0 && ba == 0) return Exit.UpdateSpeed;

            // cseg_80F88: trigger static header.
            Memory.WriteWord(teamBase + OffHeaderOrTackle, 1);
            // D0 = sprite.direction (passed as the direction arg to the real port).
            short headerDir = Memory.ReadSignedWord(spriteAddr + SOffDirection);
            // Real port — updatePlayers.cpp:15803 → PlayerHeader.AttemptStaticHeader.
            PlayerHeader.AttemptStaticHeader(spriteAddr, headerDir);
            Memory.WriteDword(teamBase + OffLastHeadingPlayer, spriteAddr);
            Memory.WriteWord(teamBase + OffBallCanBeControlled, 0);
            return Exit.UpdateSpeed;
        }

    cseg_80FFD:
        s_enterCseg80FFD++;
        // updatePlayers.cpp:4936-4960. opponent.playerHasBall == 0 → clear our wonTheBallTimer.
        // Then if wonTheBallTimer != 0 → goto cseg_81793 (pitch-clamp branch).
        {
            short oppHas = Memory.ReadSignedWord(opponentBase + OffPlayerHasBall);
            if (oppHas == 0)
            {
                Memory.WriteWord(teamBase + OffWonTheBallTimer, 0);
            }
            short wbt = Memory.ReadSignedWord(teamBase + OffWonTheBallTimer);
            if (wbt != 0) goto cseg_81793;
        }

        // updatePlayers.cpp:4962-4994. ball-distance / ball-height bucket gates.
        {
            byte pvc = Memory.ReadByte(teamBase + OffPlVeryCloseToBall);
            if (pvc == 0) goto l_test_quick_fire;
            byte ba = Memory.ReadByte(teamBase + OffBallAbove17);
            if (ba != 0) goto l_test_quick_fire;
            byte b12 = Memory.ReadByte(teamBase + OffBall12To17);
            if (b12 != 0) goto l_test_quick_fire;

            // cseg_8108A: updatePlayers.cpp:4986-4994.
            byte pvp = Memory.ReadByte(teamBase + OffPrevPlVeryClose);
            if (pvp == 0)
            {
                Memory.WriteWord(teamBase + OffBallCanBeControlled, 0);
            }
        }

        // cseg_8108A: updatePlayers.cpp:4996-5042 — ball becomes free, log this team played.
        {
            short bcpd = Memory.ReadSignedWord(teamBase + OffBallCtrlPlDirection);
            // The asm compares D0 (currentAllowedDirection) against ball-controlling-player-direction
            // and clears ballCanBeControlled if they differ.
            if (d0 != bcpd)
            {
                Memory.WriteWord(teamBase + OffBallCanBeControlled, 0);
            }
        }

        // l_ball_becomes_free: updatePlayers.cpp:5013-5044. ballCanBeControlled++ then
        // record this team as the ball-handler.
        {
            short bcc = Memory.ReadSignedWord(teamBase + OffBallCanBeControlled);
            short bccNew = (short)(bcc + 1);
            Memory.WriteWord(teamBase + OffBallCanBeControlled, bccNew);
            // D1 = ballCanBeControlled (new value); written below.
            short newDirection = d0;
            Memory.WriteWord(teamBase + OffBallCtrlPlDirection, newDirection);
            Memory.WriteDword(Memory.Addr.lastTeamPlayed, teamBase);
            Memory.WriteDword(Memory.Addr.lastPlayerPlayed, spriteAddr);
            Memory.WriteWord(Memory.Addr.penalty, 0);
            Memory.WriteWord(Memory.Addr.playerHadBall, 0);
            short oppHas = Memory.ReadSignedWord(opponentBase + OffPlayerHasBall);
            if (oppHas != 0)
            {
                Memory.WriteWord(Memory.Addr.playerHadBall, 1);
            }
        }

        // l_calculate_if_player_wins_ball: updatePlayers.cpp:5046-5064.
        // Real port — player.cpp:276 → PlayerActions.CalculateIfPlayerWinsBall.
        // D0 = currentAllowedDirection (held in local `d0` since cseg_80ECF line 377).
        s_reachedCwbCall++;
        // (2026-07-02: PlayerActions.CalculateIfPlayerWinsBall now addresses
        // wonTheBallTimer at the correct +138 offset directly — the temporary
        // +130 mirror shim that lived here has been deleted.)
        PlayerActions.CalculateIfPlayerWinsBall(d0, teamBase, spriteAddr);
        Memory.WriteWord(opponentBase + OffSpinTimer, -1);
        Memory.WriteWord(opponentBase + OffPassingToPlayer, 0);
        Memory.WriteDword(opponentBase + OffPassingKickingPlayer, 0);
        goto cseg_81793;

    l_test_quick_fire:
        // updatePlayers.cpp:5067-5075. quickFire = 0 → l_no_passing.
        {
            byte qf = Memory.ReadByte(teamBase + OffQuickFire);
            if (qf == 0) goto l_no_passing;
        }
        // updatePlayers.cpp:5077-5100.
        {
            short cad = Memory.ReadSignedWord(teamBase + OffCurrentAllowedDir);
            d0 = cad;
            if (cad < 0) goto l_no_passing;
            byte pvc = Memory.ReadByte(teamBase + OffPlVeryCloseToBall);
            if (pvc == 0)
            {
                byte pcb = Memory.ReadByte(teamBase + OffPlCloseToBall);
                if (pcb == 0) goto l_no_passing;
            }
        }
        // cseg_811DF: updatePlayers.cpp:5102-5134. gameStatePl != 100 → test turn flags.
        {
            short gsp = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            if (gsp != 100)
            {
                int shift = d0 & 0x1F;
                int mask = (1 << shift) & 0xFFFF;
                byte ptf = Memory.ReadByte(Memory.Addr.playerTurnFlags);
                if ((ptf & mask) == 0) goto l_no_passing;
            }
        }
        // cseg_81203: updatePlayers.cpp:5136-5158. sprite.playerOrdinal != 1 (not goalie)
        // requires ballLessEqual4 set; if 0 → no passing.
        {
            short ord = Memory.ReadSignedWord(spriteAddr + SOffPlayerOrdinal);
            if (ord != 1)
            {
                byte ble4 = Memory.ReadByte(teamBase + OffBallLessEqual4);
                if (ble4 == 0) goto l_no_passing;
            }
        }
        // cseg_81221: updatePlayers.cpp:5160-5237. Pass execution.
        {
            Memory.WriteDword(Memory.Addr.lastTeamPlayed, teamBase);
            Memory.WriteDword(Memory.Addr.lastPlayerPlayed, spriteAddr);
            Memory.WriteWord(Memory.Addr.penalty, 0);
            Memory.WriteWord(Memory.Addr.playerHadBall, 0);
            // A2 = ballSprite; writeMemory(esi + 40, 2, 0) — clear ball.z.whole.
            Memory.WriteWord(ballSprite + SOffZWhole, 0);
            // Real port — player.cpp:2560 → PlayerActions.DoPass(teamBase, sprite).
            PlayerActions.DoPass(teamBase, spriteAddr);
            Memory.WriteDword(teamBase + OffLastHeadingPlayer, spriteAddr);
            short gsp = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            if (gsp != 100)
            {
                Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);
            }
            // cseg_812C6: penalty check.
            short gs = Memory.ReadSignedWord(Memory.Addr.gameState);
            if (gs == 14)
            {
                Memory.WriteWord(Memory.Addr.penalty, 1);
            }
            // l_not_penalty: set gameStatePl=100 / gameState=100, mark in-play.
            Memory.WriteWord(Memory.Addr.gameStatePl, 100);
            Memory.WriteWord(Memory.Addr.gameState, 100);
            Memory.WriteWord(teamBase + OffBallInPlay, 1);
            Memory.WriteWord(teamBase + OffBallOutOfPlay, 1);
            Memory.WriteWord(opponentBase + OffBallInPlay, 1);
            Memory.WriteWord(opponentBase + OffBallOutOfPlay, 1);
            Memory.WriteWord(opponentBase + OffSpinTimer, -1);
            Memory.WriteDword(teamBase + OffControlledPlayer, 0);
            Memory.WriteDword(teamBase + OffPassingKickingPlayer, spriteAddr);
            Memory.WriteWord(teamBase + OffPassKickTimer, 25);
            Memory.WriteWord(teamBase + OffBallCanBeControlled, 0);
            // A0 = readMemory(esi, 4) — opponentsTeam. Already opponentBase.
            Memory.WriteWord(opponentBase + OffPassingToPlayer, 0);
            Memory.WriteDword(opponentBase + OffPassingKickingPlayer, 0);
            return Exit.StopPlayer;
        }

    l_no_passing:
        // updatePlayers.cpp:5239-5272. normalFire path (kicking ball).
        {
            byte nf = Memory.ReadByte(teamBase + OffNormalFire);
            if (nf == 0) goto l_check_if_goalkeeper;
            short cad = Memory.ReadSignedWord(teamBase + OffCurrentAllowedDir);
            d0 = cad;
            if (cad < 0) goto l_check_if_goalkeeper;
            byte pvc = Memory.ReadByte(teamBase + OffPlVeryCloseToBall);
            if (pvc == 0)
            {
                byte pcb = Memory.ReadByte(teamBase + OffPlCloseToBall);
                if (pcb == 0) goto l_check_if_goalkeeper;
            }
        }
        // cseg_813DA: updatePlayers.cpp:5274-5306. gameStatePl != 100 → check turn flags.
        {
            short gsp = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            if (gsp != 100)
            {
                int shift = d0 & 0x1F;
                int mask = (1 << shift) & 0xFFFF;
                byte ptf = Memory.ReadByte(Memory.Addr.playerTurnFlags);
                if ((ptf & mask) == 0) goto l_check_if_goalkeeper;
            }
        }
        // cseg_813FE: updatePlayers.cpp:5308-5330. playerOrdinal check for non-goalie.
        {
            short ord = Memory.ReadSignedWord(spriteAddr + SOffPlayerOrdinal);
            if (ord != 1)
            {
                byte ble4 = Memory.ReadByte(teamBase + OffBallLessEqual4);
                if (ble4 == 0) goto l_check_if_goalkeeper;
            }
        }
        // cseg_8141C: updatePlayers.cpp:5332-5407. Kick execution.
        {
            Memory.WriteDword(Memory.Addr.lastTeamPlayed, teamBase);
            Memory.WriteDword(Memory.Addr.lastPlayerPlayed, spriteAddr);
            Memory.WriteWord(Memory.Addr.penalty, 0);
            Memory.WriteWord(Memory.Addr.playerHadBall, 0);
            // === REMOVED (2026-07-05, tasks #188/#189): port-only goal-ward
            // reaim crutch ================================================
            // A 2026-06-01 stand-in used to OVERRIDE the kicker's direction
            // here, pointing it at the goal CENTRE (kGoalCentreX=336, goalY
            // 129/769) before every controlled kick — a non-faithful crutch
            // added before AiBrain's aim was wired, to stop bot strikers
            // shooting their own net. It is now both redundant and harmful:
            //   1. The faithful AI (AiBrain.SetControlsDirection +
            //      AiHelpers.AI_SetDirectionTowardOpponentsGoal / AI_Decide-
            //      WhetherToTriggerFire, updatePlayers.cpp:19461/19577) already
            //      steers the carrier to face the opponent goal before firing.
            //      Measured: 233/234 controlled kicks now aim at the correct
            //      enemy goal in a 30k-tick AI-vs-AI smoke, both halves.
            //   2. It forced every shot to the goal CENTRE — exactly where the
            //      keeper stands — so shots were saved and NEITHER team scored
            //      (1-0 one-way; task #188 "CPU never scores"). Removing it:
            //      2-2, both teams score, shots spread across the mouth.
            //   3. It ran for HUMAN kicks and PENALTIES too, overriding the
            //      player's own aim: a penalty aimed right was re-pointed at
            //      centre (task #189 "shot right → ball centre → deflect").
            // The faithful cseg_8141C path (updatePlayers.cpp:5332-5407) commits
            // the kick in the sprite's CURRENT direction — no reaim. We now do
            // exactly that: fall straight through to PlayerKickingBall.
            // ===============================================================
            // Real port — player.cpp:750 → PlayerActions.PlayerKickingBall(teamBase, sprite).
            PlayerActions.PlayerKickingBall(teamBase, spriteAddr);
            Memory.WriteDword(teamBase + OffLastHeadingPlayer, spriteAddr);
            short gsp = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            if (gsp != 100)
            {
                Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);
            }
            // cseg_814B5: penalty check.
            short gs = Memory.ReadSignedWord(Memory.Addr.gameState);
            if (gs == 14)
            {
                Memory.WriteWord(Memory.Addr.penalty, 1);
            }
            // l_not_penalty2: same set-state as l_not_penalty, plus shooting=1.
            Memory.WriteWord(Memory.Addr.gameStatePl, 100);
            Memory.WriteWord(Memory.Addr.gameState, 100);
            Memory.WriteWord(teamBase + OffBallInPlay, 1);
            Memory.WriteWord(teamBase + OffBallOutOfPlay, 1);
            Memory.WriteWord(opponentBase + OffBallInPlay, 1);
            Memory.WriteWord(opponentBase + OffBallOutOfPlay, 1);
            Memory.WriteWord(opponentBase + OffSpinTimer, -1);
            Memory.WriteDword(teamBase + OffControlledPlayer, 0);
            Memory.WriteDword(teamBase + OffPassingKickingPlayer, spriteAddr);
            Memory.WriteWord(teamBase + OffPassKickTimer, 25);
            Memory.WriteWord(teamBase + OffBallCanBeControlled, 0);
            // updatePlayers.cpp:5396-5402 — `mov esi, A6` precedes the
            // controlledPlayer/passingKicking/passKickTimer/ballCanBeControlled
            // writes AND `mov [esi+58], 1` — shooting=1 lands on the KICKER's
            // team. esi switches to A0 (opponent) only afterwards (5403-5404)
            // for the passingToPlayer/passingKickingPlayer clears below.
            Memory.WriteWord(teamBase + OffShooting, 1);
            Memory.WriteWord(opponentBase + OffPassingToPlayer, 0);
            Memory.WriteDword(opponentBase + OffPassingKickingPlayer, 0);
            return Exit.StopPlayer;
        }

    l_check_if_goalkeeper:
        // updatePlayers.cpp:5409-5497. Tackle attempt branch.
        // If sprite.playerOrdinal == 1 → cseg_8167F (no tackle, attempt header).
        {
            short ord = Memory.ReadSignedWord(spriteAddr + SOffPlayerOrdinal);
            if (ord == 1) goto cseg_8167F;
            byte ftf = Memory.ReadByte(teamBase + OffFireThisFrame);
            if (ftf == 0) goto cseg_8167F;
            short cad = Memory.ReadSignedWord(teamBase + OffCurrentAllowedDir);
            d0 = cad;
            if (cad < 0) goto cseg_8167F;
            byte pnf = Memory.ReadByte(teamBase + OffPlNotFarFromBall);
            if (pnf == 0) goto cseg_8167F;
            byte ble4 = Memory.ReadByte(teamBase + OffBallLessEqual4);
            if (ble4 == 0)
            {
                byte b4 = Memory.ReadByte(teamBase + OffBall4To8);
                if (b4 == 0) goto cseg_8167F;
            }
        }
        // cseg_815F7: updatePlayers.cpp:5466-5497. Tackle target choice check.
        {
            int passToPlayerPtr = Memory.ReadSignedDword(teamBase + OffPassToPlayerPtr);
            // The A5 = passToPlayerPtr; if 0 → l_begin_tackling.
            if (passToPlayerPtr != 0)
            {
                int targetDist = Memory.ReadSignedDword(passToPlayerPtr + SOffBallDistance);
                int ourDist = Memory.ReadSignedDword(spriteAddr + SOffBallDistance);
                // cmp D1, eax; jb cseg_8167F (unsigned <).
                if ((uint)targetDist < (uint)ourDist) goto cseg_8167F;
            }
        }
        // l_begin_tackling: updatePlayers.cpp:5499-5513.
        Memory.WriteWord(teamBase + OffHeaderOrTackle, 1);
        // Real port — updatePlayers.cpp:14839 → PlayerTackle.PlayerBeginTackling.
        // D0 = currentAllowedDirection (held in local `d0` since cseg_8167F
        // line 658 above, mirroring the asm `mov D0, currentAllowedDirection`).
        PlayerTackle.PlayerBeginTackling(spriteAddr, teamBase, d0);
        Memory.WriteWord(teamBase + OffBallCanBeControlled, 0);
        return Exit.UpdateSpeed;

    cseg_8167F:
        // updatePlayers.cpp:5515-5570. Jump-header path (non-goalie).
        {
            short ord = Memory.ReadSignedWord(spriteAddr + SOffPlayerOrdinal);
            if (ord == 1) goto l_not_a_header;
            byte ftf = Memory.ReadByte(teamBase + OffFireThisFrame);
            if (ftf == 0) goto l_not_a_header;
            short cad = Memory.ReadSignedWord(teamBase + OffCurrentAllowedDir);
            d0 = cad;
            if (cad < 0) goto l_not_a_header;
            byte pvc = Memory.ReadByte(teamBase + OffPlVeryCloseToBall);
            byte pcb = Memory.ReadByte(teamBase + OffPlCloseToBall);
            byte pnf = Memory.ReadByte(teamBase + OffPlNotFarFromBall);
            if (pvc == 0 && pcb == 0 && pnf == 0) goto l_not_a_header;
        }
        // cseg_816E5: updatePlayers.cpp:5572-5596.
        {
            byte b8 = Memory.ReadByte(teamBase + OffBall8To12);
            byte b12 = Memory.ReadByte(teamBase + OffBall12To17);
            byte ba = Memory.ReadByte(teamBase + OffBallAbove17);
            if (b8 == 0 && b12 == 0 && ba == 0) goto l_not_a_header;
        }
        // l_its_a_header: updatePlayers.cpp:5598-5614.
        Memory.WriteWord(teamBase + OffHeaderOrTackle, 1);
        // Real port — updatePlayers.cpp:15253 → PlayerHeader.PlayerAttemptingJumpHeader.
        // D0 = currentAllowedDirection (held in local `d0` since cseg_8167F line 686).
        PlayerHeader.PlayerAttemptingJumpHeader(spriteAddr, d0);
        Memory.WriteDword(teamBase + OffLastHeadingPlayer, spriteAddr);
        Memory.WriteWord(teamBase + OffBallCanBeControlled, 0);
        return Exit.UpdateSpeed;

    l_not_a_header:
        // updatePlayers.cpp:5616-5638. gameStatePl != 100 OR currentAllowedDirection < 0 → exit.
        {
            short gsp = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            if (gsp != 100) return Exit.UpdateSpeed;
            short cad = Memory.ReadSignedWord(teamBase + OffCurrentAllowedDir);
            d0 = cad;
            if (cad < 0) return Exit.UpdateSpeed;
        }

    cseg_81793:
        // updatePlayers.cpp:5640-5778. Pitch-clamp + default-dest update.
        // D1 = sprite.x.whole; D2 = sprite.y.whole; D3 = 0 (out-of-pitch mask).
        short d1 = Memory.ReadSignedWord(spriteAddr + SOffX);
        short d2 = Memory.ReadSignedWord(spriteAddr + SOffY);
        int d3 = 0;
        // Each pitch-side test ORs a direction bitmask into D3.
        if (d1 < 79)    d3 |= 0xE0;  // left side → block dirs 5/6/7 (NW/W/SW)
    // l_inside_pitch_left_x
        if (d1 > 592)   d3 |= 0x0E;  // right side → block dirs 1/2/3 (NE/E/SE)
    // l_inside_pitch_right_x
        if (d2 < 127)   d3 |= 0x83;  // top → block dirs 0/7/1 ... actually 7,0,1 (N row)
    // l_inside_pitch_top_y
        if (d2 > 771)   d3 |= 0x38;  // bottom → block dirs 3/4/5

    // l_inside_pitch_bottom_y: updatePlayers.cpp:5706-5724. test D3 & (1<<D0).
        {
            int shift = d0 & 0x1F;
            int mask = 1 << shift;
            if ((d3 & mask) != 0)
            {
                // Out of pitch in this direction — clamp dest to current pos.
                short sx = Memory.ReadSignedWord(spriteAddr + SOffX);
                Memory.WriteWord(spriteAddr + SOffDestX, sx);
                short sy = Memory.ReadSignedWord(spriteAddr + SOffY);
                Memory.WriteWord(spriteAddr + SOffDestY, sy);
                return Exit.UpdateSpeed;
            }
        }

        // l_update_player_dest_x_y: updatePlayers.cpp:5733-5778.
        // Look up kDefaultDestinations[d0] (4 bytes per entry: dx word + dy word),
        // add to sprite.x/y, write to dest.
        {
            int byteOffset = (d0 & 0xFFFF) << 2;
            short dx = Memory.ReadSignedWord(Memory.Addr.kDefaultDestinations + byteOffset);
            short dy = Memory.ReadSignedWord(Memory.Addr.kDefaultDestinations + byteOffset + 2);
            short sx = Memory.ReadSignedWord(spriteAddr + SOffX);
            short sy = Memory.ReadSignedWord(spriteAddr + SOffY);
            short newDx = (short)(dx + sx);
            short newDy = (short)(dy + sy);
            // BUG FIX (2026-07-03, task #173 "cannot run/shoot bottom-right"):
            // the previous port applied a DEFENSIVE CLAMP here
            // (newDx→[81,590], newDy→[129,769]) that is NOT in the original asm
            // (updatePlayers.cpp:5733-5778 writes dest = pos + kDefaultDestinations
            // with no clamp). The clamp corrupted the movement ANGLE: a controlled
            // player in the right/lower half pressing a diagonal had its dest
            // pinned to the cushion corner (e.g. dest.x→590 while dest.y→769),
            // so CalculateDeltaXAndY resolved the vector to pure S or pure E
            // instead of the pressed diagonal — and for x∈{591,592} the clamp
            // pulled dest.x BELOW the current x, inverting deltaX (the player
            // ran LEFT while Down+Right was held). Verified: SE @(500,449) gave
            // dir'=4 (S), @(580,449) pure S, @(400,769) pure E.
            //
            // The clamp's stated rationale (off-ball sprites not re-entering
            // cseg_81793 every tick) does not apply: this path (RunControlledBranch)
            // only ever runs for `spriteAddr == team.controlledPlayer`
            // (UpdatePlayers.cs DispatchByPlayerState :3840 / keeper :793/912),
            // which IS re-entered every tick — exactly the condition the original
            // relies on. The cushion gate above (X>592 / Y>771 → dest=pos, stop)
            // already keeps the controlled player in bounds; the off-pitch dest
            // (pos ± 1000) is intentional and harmless because the NEXT tick's
            // gate stops the sprite at the boundary. Restored 1:1.
            Memory.WriteWord(spriteAddr + SOffDestX, newDx);
            Memory.WriteWord(spriteAddr + SOffDestY, newDy);
        }
        return Exit.UpdateSpeed;
    }

    // ===================================================================
    // RunPassReceiptTrigger — updatePlayers.cpp:8180-8252
    // ===================================================================
    //
    // The "pass arrived at a teammate" gate. Sits inside the
    // `l_player_expecting_pass` branch (updatePlayers.cpp:7604) — NOT the
    // `l_its_controlled_player` branch. The per-team-sprite dispatcher at
    // updatePlayers.cpp:851-876 routes:
    //
    //     if (A1 == team.controlledPlayer)  → l_its_controlled_player
    //     if (A1 == team.passToPlayerPtr)   → l_player_expecting_pass → ... → cseg_82D59
    //
    // These are MUTUALLY EXCLUSIVE branches. cseg_82D59 (the pass-receipt
    // commit) is reached only by the second case, after several upstream
    // guards including `team.passingToPlayer != 0` (line 7718) and a
    // `passToPlayerPtr != 0` implicit guard (you cannot equal a null pointer
    // and pass the line 875 compare).
    //
    // **Correction over the original port comment**: the previous comment
    // claimed this block ran from `l_its_controlled_player`. That is wrong —
    // the asm flow makes A1 == passToPlayerPtr the only way in. The pass
    // receiver is a different sprite than the previously-controlled player.
    //
    // When all conditions fire, the asm commits:
    //   team.controlledPlayer = team.passToPlayerPtr   (swap in the receiver)
    //   team.passToPlayerPtr  = 0                      (consume the pending pass)
    //   team.playerSwitchTimer = 25                    (debounce switch input)
    //   team.passingBall      = 0                      (pass complete)
    //   team.passingToPlayer  = 0
    //   team.shooting         = 0                      (cancel shoot-armed state)
    //   sprite.destX = sprite.x; sprite.destY = sprite.y  (snap receiver dest)
    //   if ordinal==1: updateBallWithControllingGoalkeeper(sprite)
    //   else:          updateControllingPlayer(sprite)
    //
    // Conditions inside cseg_82D59 itself (8182-8219):
    //   1. team.ballLessEqual4 != 0            (ball is low — height <= 4 px)
    //   2. ballSprite.speed >= 1536            (ball is moving fast — pass/shot)
    //   3. team.plCloseToBall != 0 OR
    //      team.plVeryCloseToBall != 0         (a teammate is near the ball)
    //
    // **Hardened call-site invariant (REGRESSION FIX 2026-06-01)**: the previous
    // port called this from TickHumanControlled (the controlledPlayer branch)
    // with sprite == team.controlledPlayer. That violated the asm's A1 identity
    // (this should fire from the receiver branch). Combined with our half-
    // ported ball-distance pipeline (Sprite.ballDistance never written → reads
    // 0 → plVeryCloseToBall=1 for everyone), the trigger fired on tick 1 every
    // match, executing `controlledPlayer = passToPlayerPtr (==0)` and zeroing
    // both teams' controlledPlayer pointers, freezing all play. The guards
    // below enforce the asm semantics directly:
    //   - passToPlayerPtr != 0  (else swap zeroes controlledPlayer)
    //   - sprite == passToPlayerPtr  (matches asm line 875 dispatch test)
    //
    // **Where the ball-sprite address comes from**: 8190 reads
    // `g_memByte[329032]` which is `ballSprite.speed` (BallSprite.Base 0x4F800
    // + OffSpeed 44 = 0x4F82C = 325676 decimal — not 329032 since swos-port
    // and our port have different base addresses; we use the abstraction
    // BallSprite.Speed which forwards to Memory).
    //
    // from updatePlayers.cpp:8180-8252
    public static void RunPassReceiptTrigger(int spriteAddr, bool topTeam)
    {
        int teamBase = TeamData.Base(topTeam);

        // REGRESSION FIX (2026-06-01): enforce the asm dispatch guard at
        // updatePlayers.cpp:865-876 — this branch only fires when the
        // iteration sprite IS the receiver. Without these checks, calling
        // this with the controlledPlayer sprite + null passToPlayerPtr
        // zeroes out the team's controlledPlayer and pins the ball.
        int passToPlayerPtrEarly = Memory.ReadSignedDword(teamBase + TeamData.OffPassToPlayerPtr);
        if (passToPlayerPtrEarly == 0) return;
        if (spriteAddr != passToPlayerPtrEarly) return;

        // updatePlayers.cpp:8182-8188 — ballLessEqual4 == 0 → bail.
        byte ballLE4 = Memory.ReadByte(teamBase + 64);  // OffBallLessEqual4
        if (ballLE4 == 0) return;

        // updatePlayers.cpp:8190-8201 — ballSprite.speed < 1536 → bail (cseg_82D82
        // path which then checks plVeryCloseToBall only). To match the asm
        // exactly: speed >= 1536 enables plCloseToBall as a valid trigger;
        // below 1536 only plVeryCloseToBall counts.
        short ballSpeed = BallSprite.Speed;

        bool plClose = Memory.ReadByte(teamBase + TeamData.OffPlCloseToBall) != 0;
        bool plVeryClose = Memory.ReadByte(teamBase + TeamData.OffPlVeryCloseToBall) != 0;

        // updatePlayers.cpp:8200-8219 — gate dispatch:
        //   if ballSpeed >= 1536 AND plCloseToBall → l_passed_to_player_becomes_main
        //   else if plVeryCloseToBall              → l_passed_to_player_becomes_main
        //   else                                    → cseg_82EC2 (no trigger)
        bool trigger;
        if (ballSpeed >= 1536 && plClose)
        {
            trigger = true;
        }
        else if (plVeryClose)
        {
            trigger = true;
        }
        else
        {
            trigger = false;
        }

        if (!trigger) return;

        // l_passed_to_player_becomes_main — updatePlayers.cpp:8221-8229.
        // team.controlledPlayer = team.passToPlayerPtr; team.passToPlayerPtr = 0.
        int passToPlayerPtr = Memory.ReadSignedDword(teamBase + TeamData.OffPassToPlayerPtr);
        Memory.WriteDword(teamBase + TeamData.OffControlledPlayer, passToPlayerPtr);
        Memory.WriteDword(teamBase + TeamData.OffPassToPlayerPtr, 0);
        // playerSwitchTimer = 25 — debounce so the human can't switch again immediately.
        Memory.WriteWord(teamBase + TeamData.OffPlayerSwitchTimer, 25);
        // passingBall = 0; passingToPlayer = 0; shooting = 0.
        Memory.WriteWord(teamBase + TeamData.OffPassingBall, 0);
        Memory.WriteWord(teamBase + TeamData.OffPassingToPlayer, 0);
        Memory.WriteWord(teamBase + TeamData.OffShooting, 0);

        // updatePlayers.cpp:8230-8234 — esi=A1; sprite.destX = sprite.x.whole;
        // sprite.destY = sprite.y.whole. (The asm comments mislabel these as
        // TeamGeneralInfo fields but with esi=A1 they're Sprite.x/y high-words.)
        // Snaps the iteration sprite's destination to its current position so
        // the speed update doesn't drift it while the controlledPlayer swap
        // settles.
        short spriteXw = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
        short spriteYw = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, spriteXw);
        Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, spriteYw);

        // updatePlayers.cpp:8235-8252 — sprite.playerOrdinal == 1 (keeper)
        // → updateBallWithControllingGoalkeeper(A1); else → updateControllingPlayer(A1).
        short ordinal = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffPlayerOrdinal);
        if (ordinal == 1)
        {
            // player.cpp:200 — pins ball at keeper's hand position.
            PlayerUpdate.UpdateBallWithControllingGoalkeeper(spriteAddr);
        }
        else
        {
            // player.cpp:135 — pins ball one pixel in front of outfielder.
            PlayerActions.UpdateControllingPlayer(spriteAddr);
        }

        // cseg_82E23 — updatePlayers.cpp:8254-8257 — clear pass-kick bookkeeping.
        Memory.WriteWord(teamBase + TeamData.OffPassKickTimer, 0);          // :8255
        Memory.WriteDword(teamBase + OffPassingKickingPlayer, 0);           // :8256
        Memory.WriteWord(teamBase + 116, 0);                                // :8257 field_74

        // updatePlayers.cpp:8259-8301 — BACK-PASS RULE. If the receiver is the
        // goalkeeper (playerOrdinal == 1):
        //   - ball last played by THIS team (A6 == lastTeamPlayed) AND
        //     playerHadBall == 0 (a deliberate pass, not a loose/contested
        //     ball) → keeper may NOT pick it up: goalkeeperPlaying = 1,
        //     goaliePlayingOrOut = 1, ballOutOfPlayOrKeeper = 1 — he controls
        //     the ball with his feet (updatePlayers.cpp:8285-8298).
        //   - otherwise (opponent played it last, or playerHadBall != 0) →
        //     cseg_82E92: GoalkeeperClaimedTheBall (updatePlayers.cpp:8300-8301).
        // NOTE: the gate reads lastTeamPlayed BEFORE the cseg_82E97 update below.
        if (ordinal == 1)
        {
            int lastTeamPlayed = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayed);  // :8274
            short playerHadBall = Memory.ReadSignedWord(Memory.Addr.playerHadBall);   // :8288
            if (teamBase == lastTeamPlayed && playerHadBall == 0)
            {
                // updatePlayers.cpp:8294-8297 — keeper plays with his feet.
                Memory.WriteWord(teamBase + TeamData.OffGoalkeeperPlaying, 1);        // :8295 (word +140)
                Memory.WriteByte(teamBase + TeamData.OffGoaliePlayingOrOut, 1);       // :8296 (byte +86)
                Memory.WriteByte(teamBase + TeamData.OffBallOutOfPlayOrKeeper, 1);    // :8297 (byte +84)
            }
            else
            {
                // cseg_82E92 — updatePlayers.cpp:8300-8301.
                PlayerUpdate.GoalkeeperClaimedTheBall(spriteAddr, teamBase == TeamData.TopBase);
            }
        }

        // cseg_82E97 — updatePlayers.cpp:8303-8310 — record possession.
        Memory.WriteDword(Memory.Addr.lastTeamPlayed, teamBase);            // :8304-8305
        Memory.WriteDword(Memory.Addr.lastPlayerPlayed, spriteAddr);        // :8306-8307
        Memory.WriteWord(Memory.Addr.penalty, 0);                           // :8308
        Memory.WriteWord(Memory.Addr.playerHadBall, 0);                     // :8309
    }

    // ===================================================================
    // RunPassExpectingBranch — updatePlayers.cpp:7604-8311 (l_player_expecting_pass)
    // ===================================================================
    //
    // The dispatcher branch the asm reaches when
    //   spriteAddr == team.passToPlayerPtr  (updatePlayers.cpp:865-876).
    //
    // Source mapping (lines reference updatePlayers.cpp):
    //   - l_player_expecting_pass entry        ......... 7604
    //   - pitch-bounds test (player x in [81,590], y in [129,769]) 7605-7656
    //     · out-of-pitch → clear passingToPlayer/passingBall, stop player (7658-7662)
    //   - in-pitch CPU/human pass-direction update     7664-7891
    //     · cpu team           → l_cpu_passing_to (7670-7672)
    //     · long-pass / spin   → l_test_for_long_pass spin-offset table at
    //                            kBallFriction (7674-7891). PORTED — see
    //                            ApplyKBallFrictionOrFallback below + the
    //                            s_kBallFriction table (verbatim copy of
    //                            swos.asm:245568) earlier in this file.
    //   - AI_Kick call for CPU team                    7892-7908
    //   - gameStatePl == ST_GAME_IN_PROGRESS gate      7910-7922
    //     · in-progress  → cseg_82C41 (8069-8311: keeper/tackle/receipt)
    //     · stopped      → cseg_82AE5 (7924-8067: throw-in / pass-success direct commit)
    //   - cseg_82D59 receiver-commit (8180-8252)        ── already in RunPassReceiptTrigger.
    //   - cseg_82EC2 ball-chase fallback (8312-8430)    ── port here.
    //
    // Mechanical-port scope (this commit): pitch-bounds gate, the team.ballX/ballY
    // refresh (CPU-team simple path + human long-pass spin via
    // ApplyKBallFrictionOrFallback), the AI_Kick call, the in-progress dispatch
    // (RunPassReceiptTrigger + cseg_82EC2 chase), and the game-stopped →
    // pass-success commit (cseg_82AE5/AF6 → l_pass_success).
    //
    // What's STILL stubbed (cited inline):
    //   - cseg_82CE1 tackle path inside cseg_82C41 (8101-8178) — playerBeginTackling
    //     commit ported; gate logic + headerOrTackle/ballCanBeControlled writes
    //     all live (see "cseg_82CE1 commit" inline). Opponent-team passing flag
    //     wipe also ported.
    //   - throw-in handoff at l_pass_success (8038-8067) — requires
    //     SetThrowInPlayerDestinationCoordinates which is already ported.
    //
    // Hard rules: no floats, integer math only, cite source comments.
    public static void RunPassExpectingBranch(int spriteAddr, bool topTeam)
    {
        int teamBase = TeamData.Base(topTeam);
        // BallSprite.Base is implicitly the A2 register in swos-port; we don't
        // need to alias it here because the BallSprite static accessors index
        // it directly. Comment kept so future ports keep the asm-mapping clear.

        // updatePlayers.cpp:7605-7656 — pitch-bounds test on player's whole-pixel
        // (x, y). If x < 81 || x > 590 || y < 129 || y > 769 → out-of-pitch.
        short pxWord = Memory.ReadSignedWord(spriteAddr + SOffX);
        short pyWord = Memory.ReadSignedWord(spriteAddr + SOffY);
        bool outOfPitch = (pxWord < 81 || pxWord > 590 ||
                           pyWord < 129 || pyWord > 769);
        if (outOfPitch)
        {
            // updatePlayers.cpp:7658-7662 — clear pass flags, then l_stop_player.
            Memory.WriteWord(teamBase + TeamData.OffPassingToPlayer, 0);
            Memory.WriteWord(teamBase + TeamData.OffPassingBall, 0);
            // l_stop_player: snap destX/Y to current pos.
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, pxWord);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, pyWord);
            return;
        }

        // updatePlayers.cpp:7664-7891 — in-pitch ball-prediction update.
        // For CPU team (playerNumber == 0) → l_cpu_passing_to directly. For
        // human team the spin-offset table at kBallFriction is sampled to
        // lead the pass.
        // Mechanical port of updatePlayers.cpp:7670-7891 +
        // external/swos-port/swos/swos.asm:245568 (kBallFriction table data).
        ApplyKBallFrictionOrFallback(spriteAddr, teamBase);

        // updatePlayers.cpp:7892-7908 — l_cpu_passing_to: if team.playerNumber == 0
        // (CPU team) → AI_Kick(A1).
        short playerNumber = Memory.ReadSignedWord(teamBase + TeamData.OffPlayerNumber);
        if (playerNumber == 0)
        {
            // from updatePlayers.cpp:19313 — AI_Kick. Helper takes the per-player
            // sprite + the team base. We call with this iteration's A1.
            AiHelpers.AI_Kick(spriteAddr, teamBase);
        }

        // updatePlayers.cpp:7910-7922 — cseg_82AAF: gameStatePl != ST_GAME_IN_PROGRESS
        // routes to cseg_82C41 (the in-progress receipt chain). The asm test:
        //   cmp gameStatePl, 100 ; jz cseg_82C41
        // i.e. the in-progress path is the jz fall-through to cseg_82C41 starting
        // at 8069. The "stopped" path (cseg_82AE5+) is the jnz fall-through.
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);

        if (gameStatePl != 100)
        {
            // ----- Game-STOPPED path (cseg_82AE5..l_pass_success @ 7924-8067) ----
            // updatePlayers.cpp:7924-7935 — if lastTeamPlayedBeforeBreak != A6
            //   → l_update_player_speed_and_deltas (no commit). Stoppage came
            //     from the OTHER team's action; this team isn't the one resuming.
            int lastBeforeBreak = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
            if (lastBeforeBreak != teamBase) return;

            // updatePlayers.cpp:7937-7972 — if gameState == ST_KEEPER_HOLDS_BALL (3)
            //   AND playerOrdinal != 1 (not a goalie) AND ballDistance != 0
            //   → cseg_82EC2 (chase path). Else fall through to l_pass_success.
            short gs = Memory.ReadSignedWord(Memory.Addr.gameState);
            short ordinalA1 = Memory.ReadSignedWord(spriteAddr + SOffPlayerOrdinal);
            bool fallThroughToPassSuccess = false;
            if (gs == 3 && ordinalA1 != 1)
            {
                int bd = Memory.ReadSignedDword(spriteAddr + SOffBallDistance);
                if (bd == 0)
                    fallThroughToPassSuccess = true;
            }
            else
            {
                fallThroughToPassSuccess = true;
            }

            if (!fallThroughToPassSuccess)
            {
                // updatePlayers.cpp:cseg_82EC2 → fall through to chase. Implement
                // simple chase commit (8393-8417): dest = team.ballX/ballY.
                RunChaseBall(spriteAddr, teamBase);
                return;
            }

            // ----- cseg_82AF6..l_pass_success @ 7974-8067 ---------------------
            // updatePlayers.cpp:7974-8003 — cameraDirection < 8 → l_pass_success
            // direct; else cameraDirection = sprite.direction.
            short camDir = Memory.ReadSignedWord(Memory.Addr.cameraDirection);
            if (camDir >= 8)
            {
                short spriteDir = Memory.ReadSignedWord(spriteAddr + SOffDirection);
                Memory.WriteWord(Memory.Addr.cameraDirection, spriteDir);
                // updatePlayers.cpp:7991-8002 — `add dseg_132804, 1`. Increments
                // a dead-statistical counter every time the pass-success branch
                // has to refresh cameraDirection from the receiver's sprite
                // direction (i.e. cameraDirection was out-of-range >= 8 at the
                // call site). Mirrors the dseg_132806 increment a few hundred
                // lines above (line 357 in this file) for the controlled-player
                // turn-flag override path.
                // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:7991-8002.
                short cnt132804 = Memory.ReadSignedWord(Memory.Addr.dseg_132804);
                Memory.WriteWord(Memory.Addr.dseg_132804, (short)(cnt132804 + 1));
            }

            // l_pass_success @ 8004-8024 — commit pass receipt.
            short camDirNow = Memory.ReadSignedWord(Memory.Addr.cameraDirection);
            Memory.WriteWord(spriteAddr + SOffDirection, camDirNow);
            int passToPtrNow = Memory.ReadSignedDword(teamBase + TeamData.OffPassToPlayerPtr);
            Memory.WriteDword(teamBase + TeamData.OffControlledPlayer, passToPtrNow);
            Memory.WriteDword(teamBase + TeamData.OffPassToPlayerPtr, 0);
            Memory.WriteWord(teamBase + TeamData.OffPlayerSwitchTimer, 25);
            Memory.WriteWord(teamBase + TeamData.OffPassingBall, 0);
            Memory.WriteWord(teamBase + TeamData.OffPassingToPlayer, 0);
            Memory.WriteWord(teamBase + TeamData.OffShooting, 0);
            // updatePlayers.cpp:8017 — A2 was ballSprite; `[esi+44]` is
            // ball.currentAllowedDirection-shaped — actually this writes to
            // ball.speed (offset +44 on BallSprite). The asm comment mislabels.
            // Setting ball.speed=0 here is consistent with "the ball is now
            // controlled by the receiver, momentum cancelled".
            Memory.WriteWord(BallSprite.Base + 44, 0);
            Memory.WriteDword(Memory.Addr.lastTeamPlayed, teamBase);
            Memory.WriteDword(Memory.Addr.lastPlayerPlayed, spriteAddr);
            Memory.WriteWord(Memory.Addr.penalty, 0);
            Memory.WriteWord(Memory.Addr.playerHadBall, 0);
            // updatePlayers.cpp:8024 — updatePlayerWithBall(A1).
            PlayerActions.UpdatePlayerWithBall(spriteAddr);
            // updatePlayers.cpp:8026-8028 — reset pass timers.
            Memory.WriteWord(teamBase + TeamData.OffPassKickTimer, 0);
            Memory.WriteDword(teamBase + 104, 0);   // passingKickingPlayer
            Memory.WriteWord(teamBase + 116, 0);    // field_74
            // updatePlayers.cpp:8029-8067 — throw-in special-case for
            // gameState in [15..20]. SetPieces.SetThrowInPlayerDestinationCoordinates
            // would fire here. TODO from updatePlayers.cpp:8059 — throw-in
            // override during pass receipt is rare; not yet exercised by smoke.
            return;
        }

        // ----- Game-IN-PROGRESS path (cseg_82C41..@ 8069-8311) --------------
        //
        // updatePlayers.cpp:8069-8082 — playerOrdinal == 1 (goalie) → skip
        // the tackle branch and go straight to receipt at cseg_82D59.
        short ordinalIp = Memory.ReadSignedWord(spriteAddr + SOffPlayerOrdinal);
        bool goalieReceiving = (ordinalIp == 1);

        if (!goalieReceiving)
        {
            // updatePlayers.cpp:8084-8124 — non-goalie tackle pre-check.
            //   if fireThisFrame != 0 AND currentAllowedDirection >= 0
            //     AND plNotFarFromBall != 0
            //     AND (ballLessEqual4 != 0 OR ball4To8 != 0)
            //   then cseg_82CAB: compare ballDistance vs team.controlledPlayer's
            //     ballDistance; if A1 not closer → bail to cseg_82D59.
            //   Else cseg_82CE1 fires playerBeginTackling.
            //
            // cseg_82CE1 commit is ported below (8159-8178).
            byte ftf = Memory.ReadByte(teamBase + TeamData.OffFireThisFrame);
            short cad = Memory.ReadSignedWord(teamBase + TeamData.OffCurrentAllowedDirection);
            byte pnf = Memory.ReadByte(teamBase + 63);   // plNotFarFromBall
            byte ble4 = Memory.ReadByte(teamBase + 64);  // ballLessEqual4
            byte b4to8 = Memory.ReadByte(teamBase + 65); // ball4To8
            bool tackleGate = ftf != 0 && cad >= 0 && pnf != 0 &&
                              (ble4 != 0 || b4to8 != 0);
            if (tackleGate)
            {
                int controlled = Memory.ReadSignedDword(teamBase + TeamData.OffControlledPlayer);
                bool a1Closer = true;  // default: trigger if no controlled exists
                if (controlled != 0)
                {
                    int ctrlDist = Memory.ReadSignedDword(controlled + SOffBallDistance);
                    int a1Dist = Memory.ReadSignedDword(spriteAddr + SOffBallDistance);
                    // cmp D1, eax; jb cseg_82D59 — A1 is closer iff a1Dist < ctrlDist (unsigned)
                    a1Closer = (uint)a1Dist < (uint)ctrlDist;
                }
                if (a1Closer)
                {
                    // updatePlayers.cpp:8159-8178 — cseg_82CE1 commit.
                    // 8161 — team.headerOrTackle = 1 (locks input next frame).
                    Memory.WriteWord(teamBase + TeamData.OffHeaderOrTackle, 1);
                    // 8166 — playerBeginTackling(D0=team.currentAllowedDirection).
                    PlayerTackle.PlayerBeginTackling(spriteAddr, teamBase, cad);
                    // 8172 — team.ballCanBeControlled = 0.
                    Memory.WriteWord(teamBase + TeamData.OffBallCanBeControlled, 0);
                    // 8173-8177 — opponent.passingToPlayer = 0; passingKickingPlayer = 0.
                    int opponentTeamBase = Memory.ReadSignedDword(teamBase + TeamData.OffOpponentsTeam);
                    if (opponentTeamBase != 0)
                    {
                        Memory.WriteWord(opponentTeamBase + TeamData.OffPassingToPlayer, 0);
                        Memory.WriteDword(opponentTeamBase + TeamData.OffPassingKickingPlayer, 0);
                    }
                    // 8178 — jmp l_update_player_speed_and_deltas. Refresh speed/anim
                    // and bail; we MUST NOT fall through to receipt path.
                    PlayerActions.UpdatePlayerSpeedAndFrameDelay(teamBase, spriteAddr);
                    return;
                }
            }
        }

        // updatePlayers.cpp:8180-8252 (cseg_82D59) — receipt commit if the
        // ball is on the ground AND close enough. Already ported.
        // RunPassReceiptTrigger self-gates on spriteAddr == passToPlayerPtr.
        RunPassReceiptTrigger(spriteAddr, topTeam);

        // updatePlayers.cpp:8312-8634 (cseg_82EC2 → l_player_chase_ball →
        // l_player_still_moving tail) — if the receipt didn't fire, run the
        // full hold-position / chase / keeper-leash / crowd-stop chain.
        // Note: RunPassReceiptTrigger consumes passToPlayerPtr on success. If
        // it cleared it, the tail must not run. Detect by re-reading.
        int passPtrAfter = Memory.ReadSignedDword(teamBase + TeamData.OffPassToPlayerPtr);
        if (passPtrAfter == spriteAddr)
        {
            RunPassChaseTail(spriteAddr, teamBase);
        }
    }

    // ===================================================================
    // RunPassChaseTail — updatePlayers.cpp:8312-8634
    // (cseg_82EC2 → l_its_a_pass → l_player_chase_ball →
    //  l_player_still_moving → l_cancel_pass / cseg_8308D)
    // ===================================================================
    //
    // Mechanically ported 2026-07-03 (task #154). The previous port jumped
    // straight to the l_player_chase_ball dest write and skipped:
    //   - the passingBall == 0 "hold position" branch (8322-8387): when the
    //     team's controlledPlayer is closer to the ball than the receiver
    //     (both within 3200), the RECEIVER's dest snaps to his OWN position
    //     — the controlled player will take the ball instead;
    //   - the keeper leash-zone cancel (8432-8561): a keeper chasing a pass
    //     outside his box cancels the pass and stops;
    //   - the goal-out crowd-zone stop for outfielders (8563-8634).
    //
    // The caller (router) runs UpdatePlayerSpeedAndFrameDelay after we
    // return, which is the asm's l_update_player_speed_and_deltas join.
    // l_stop_player is dest = own position, then the same join.
    //
    // from updatePlayers.cpp:8312.
    private static void RunPassChaseTail(int spriteAddr, int teamBase)
    {
        // cseg_82EC2 @ 8312-8320 — if passingBall != 0 → l_player_chase_ball.
        short passingBall = Memory.ReadSignedWord(teamBase + TeamData.OffPassingBall);
        if (passingBall != 0)
            goto l_player_chase_ball;

        {
            // 8322-8334 — A5 = team.controlledPlayer; if 0 → l_its_a_pass.
            int a5Controlled = Memory.ReadSignedDword(teamBase + TeamData.OffControlledPlayer);
            if (a5Controlled == 0)
                goto l_its_a_pass;

            // 8336-8348 — cmp [A5+Sprite.ballDistance], 3200; ja l_its_a_pass.
            int a5Dist = Memory.ReadSignedDword(a5Controlled + SOffBallDistance);
            if ((uint)a5Dist > 3200u)
                goto l_its_a_pass;

            // 8350-8364 — cmp A5.ballDistance, A1.ballDistance; ja l_its_a_pass
            // (controlled player farther than the receiver → it's a pass).
            int a1Dist = Memory.ReadSignedDword(spriteAddr + SOffBallDistance);
            if ((uint)a5Dist > (uint)a1Dist)
                goto l_its_a_pass;

            // 8366-8377 — cmp [A1+Sprite.ballDistance], 3200; ja l_player_chase_ball.
            if ((uint)a1Dist > 3200u)
                goto l_player_chase_ball;

            // 8379-8387 — hold: A1.destX/destY = A1's own x/y (the controlled
            // player is closer — HE will take the ball), then the speed join.
            short holdX = Memory.ReadSignedWord(spriteAddr + SOffX);
            short holdY = Memory.ReadSignedWord(spriteAddr + SOffY);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, holdX);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, holdY);
            return;
        }

    l_its_a_pass:
        // 8389-8391 — team.passingBall = 1.
        Memory.WriteWord(teamBase + TeamData.OffPassingBall, 1);

    l_player_chase_ball:
        {
            // 8393-8401 — A1.destX = team.ballX; A1.destY = team.ballY.
            short ballX = Memory.ReadSignedWord(teamBase + TeamData.OffBallX);
            short ballY = Memory.ReadSignedWord(teamBase + TeamData.OffBallY);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, ballX);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, ballY);

            // 8402-8430 — D1 = A1.x.whole; D2 = A1.y.whole; if already at
            // (destX, destY) → speed join (done).
            short d1 = Memory.ReadSignedWord(spriteAddr + SOffX);
            short d2 = Memory.ReadSignedWord(spriteAddr + SOffY);
            short destX = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffDestX);
            if (d1 == destX)
            {
                short destY = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffDestY);
                if (d2 == destY)
                    return;
            }

            // l_player_still_moving @ 8432-8444 — cmp gameStatePl, 100;
            // jnz cseg_8308D.
            short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            if (gameStatePl != 100)
                goto cseg_8308D_entry;

            // 8446-8458 — cmp [A1+Sprite.playerOrdinal], 1; jnz cseg_8308D.
            short ordinal = Memory.ReadSignedWord(spriteAddr + SOffPlayerOrdinal);
            if (ordinal != 1)
                goto cseg_8308D_entry;

            // 8460-8467 — cmp team.goalkeeperPlaying, 0; jnz cseg_8308D.
            short goalkeeperPlaying = Memory.ReadSignedWord(teamBase + TeamData.OffGoalkeeperPlaying);
            if (goalkeeperPlaying != 0)
                goto cseg_8308D_entry;

            // 8469-8479 — cmp A6, offset bottomTeamData; jz cseg_8303E.
            if (teamBase == TeamData.BottomBase)
                goto cseg_8303E;

            // ---- TOP-team keeper leash (goal at the top) -------------------
            // 8481-8491 — cmp D2, 206; jg l_cancel_pass.
            if (d2 > 206)
                goto l_cancel_pass;
            // 8493-8503 — cmp D1, 203; jl l_cancel_pass.
            if (d1 < 203)
                goto l_cancel_pass;
            // 8505-8515 — cmp D1, 468; jg l_cancel_pass.
            if (d1 > 468)
                goto l_cancel_pass;
            // 8517 — jmp l_update_player_speed_and_deltas.
            return;

        cseg_8303E:
            // ---- BOTTOM-team keeper leash (goal at the bottom) -------------
            // 8519-8530 — cmp D2, 692; jl l_cancel_pass.
            if (d2 < 692)
                goto l_cancel_pass;
            // 8532-8542 — cmp D1, 203; jl l_cancel_pass.
            if (d1 < 203)
                goto l_cancel_pass;
            // 8544-8554 — cmp D1, 468; jle l_update_player_speed_and_deltas.
            if (d1 <= 468)
                return;
            // fall through

        l_cancel_pass:
            // 8556-8561 — clear passToPlayerPtr + passingBall + passingToPlayer,
            // then l_stop_player.
            Memory.WriteDword(teamBase + TeamData.OffPassToPlayerPtr, 0);
            Memory.WriteWord(teamBase + TeamData.OffPassingBall, 0);
            Memory.WriteWord(teamBase + TeamData.OffPassingToPlayer, 0);
            goto l_stop_player;

        cseg_8308D_entry:
            // cseg_8308D @ 8563-8570 — if goalOut == 0 → speed join.
            short goalOut = Memory.ReadSignedWord(Memory.Addr.goalOut);
            if (goalOut == 0)
                return;

            // 8572-8584 — keepers (ordinal == 1) skip the crowd-zone stop.
            short ord2 = Memory.ReadSignedWord(spriteAddr + SOffPlayerOrdinal);
            if (ord2 == 1)
                return;

            // 8586-8608 — cmp D1, 183; jl done. cmp D1, 488; jg done.
            if (d1 < 183)
                return;
            if (d1 > 488)
                return;

            // 8610-8632 — cmp D2, 226; jle l_stop_player.
            //             cmp D2, 672; jge l_stop_player. else done.
            if (d2 <= 226)
                goto l_stop_player;
            if (d2 >= 672)
                goto l_stop_player;
            return;

        l_stop_player:
            // l_stop_player — dest = own position (same commit as the
            // out-of-pitch branch above), then the speed join in the caller.
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestX, d1);
            Memory.WriteWord(spriteAddr + PlayerSprite.OffDestY, d2);
        }
    }

    // updatePlayers.cpp:8393-8431 — l_player_chase_ball.
    // sprite.destX = team.ballX; sprite.destY = team.ballY; if sprite is
    // already at (destX,destY) the speed update is a no-op. We mirror the
    // simple assignment; the chase loop in 8432+ handles cancel-pass /
    // goalkeeper override which we don't need for the mechanical port.
    //
    // from updatePlayers.cpp:8393.
    private static void RunChaseBall(int spriteAddr, int teamBase)
    {
        short ballX = Memory.ReadSignedWord(teamBase + TeamData.OffBallX);
        short ballY = Memory.ReadSignedWord(teamBase + TeamData.OffBallY);
        // DEFENSIVE CLAMP — team.ballX/Y can briefly be off-pitch when a kick
        // sends the ball over the touchline before the OOB handler reins it
        // in. Stomping that onto a chaser's dest would let the chaser drift
        // beyond the cushion. Same band as setPlayerWithNoBallDestination
        // (updatePlayers.cpp:15608-15666).
        if (ballX <  81) ballX =  81;
        if (ballX > 590) ballX = 590;
        if (ballY < 129) ballY = 129;
        if (ballY > 769) ballY = 769;
        Memory.WriteWord(spriteAddr + SOffDestX, ballX);
        Memory.WriteWord(spriteAddr + SOffDestY, ballY);
    }

    // ===================================================================
    // All callees now route to real ports:
    //   AI_SetControlsDirection      → AiBrain.SetControlsDirection
    //   UpdatePlayerWithBall         → PlayerActions.UpdatePlayerWithBall
    //   AttemptStaticHeader          → PlayerHeader.AttemptStaticHeader
    //   CalculateIfPlayerWinsBall    → PlayerActions.CalculateIfPlayerWinsBall
    //   DoPass                       → PlayerActions.DoPass
    //   PlayerKickingBall            → PlayerActions.PlayerKickingBall
    //   PlayerAttemptingJumpHeader   → PlayerHeader.PlayerAttemptingJumpHeader
    //   PlayerBeginTackling          → PlayerTackle.PlayerBeginTackling
    //   UpdateControllingPlayer      → PlayerActions.UpdateControllingPlayer
    //     (fired from RunPassReceiptTrigger above)
    //
    // Note: PlayerTackle.PlayerTacklingTestFoul is the foul-check that runs
    // AFTER a tackle hits (called from UpdatePlayers kTackling case at
    // UpdatePlayers.cs:777). PlayerTackle.PlayerBeginTackling is the entry
    // that transitions a player INTO the kTackling state.
    // ===================================================================

    // kBallFriction spin-offset table. Mechanical port of
    //   external/swos-port/swos/swos.asm:245568
    // 32 pairs (dx, dy) indexed by ((ballFullDir + spinSign + 4) & 0xFF) >> 3.
    // Values are pixel offsets (scaled by 1000-unit step). Used to lead the
    // pass target when the ball is moving and the carrier wants to "spin" it.
    // BUG FIX (2026-07-03, task #154): the previous transcription of this table
    // dropped the (1000, 1000) pair at slot 12 (the asm source wraps mid-pair
    // across `dw` lines at swos.asm:245570-245571), leaving 63 shorts instead
    // of 64 and shifting every pair from slot 12 onward — slot 31 read past the
    // end of the array. Re-transcribed pair-by-pair from swos.asm:245568-245573.
    private static readonly short[] s_kBallFriction = new short[]
    {
        // slots 0..7
            0, -1000,   199, -1000,   414, -1000,   668, -1000,
         1000, -1000,  1000,  -668,  1000,  -414,  1000,  -199,
        // slots 8..15
         1000,     0,  1000,   199,  1000,   414,  1000,   668,
         1000,  1000,   668,  1000,   414,  1000,   199,  1000,
        // slots 16..23
            0,  1000,  -199,  1000,  -414,  1000,  -668,  1000,
        -1000,  1000, -1000,   668, -1000,   414, -1000,   199,
        // slots 24..31
        -1000,     0, -1000,  -199, -1000,  -414, -1000,  -668,
        -1000, -1000,  -668, -1000,  -414, -1000,  -199, -1000,
    };

    // Mechanical port of updatePlayers.cpp:7664-7891 — long-pass spin update.
    // Drives the team.ballX/ballY lead targeting used by the pass-receipt
    // chain.
    //
    // BUG FIX (2026-07-03, task #154 "receiver veers off mid-pass"): the
    // previous version of this function diverged from the asm in four ways
    // that combined into the reported symptom:
    //   1. It was MISSING the longPass/leftSpin/rightSpin gate at
    //      updatePlayers.cpp:7674-7696, so the fullDirection "not turned
    //      enough" check ran for EVERY human pass. A receiver running toward
    //      an incoming ball faces roughly opposite the ball's direction
    //      (|delta| ≈ 128 > 64), so passingToPlayer/passingBall were cleared
    //      on effectively every plain pass. With passingToPlayer == 0,
    //      InputControls.UpdatePlayerBeingPassedTo (swos.asm:101066-101073
    //      gate) re-elects passToPlayerPtr every tick to the sprite CLOSEST
    //      to the ball — early in the flight that is someone near the passer,
    //      so the intended receiver was un-designated (dest snapped to own
    //      pos, then routed to the AI positioning tail → walked back to
    //      formation), and only re-elected once the ball got close to him
    //      ("remembers and comes back"). In the original, a plain pass
    //      (no longPass/spin) never reaches the fullDirection check at all.
    //   2. Every fallback path wrote team.ballX/ballY = live ball position.
    //      The asm writes NOTHING on those paths (l_cpu_passing_to has no
    //      ballX/Y store — the per-tick mirror in UpdatePlayerBeingPassedTo
    //      at swos.asm:101049-101057 is the only refresher).
    //   3. l_ball_got_no_direction (7794-7803) writes ballX/Y from the
    //      RECEIVER's own x/y (esi = A1), not the ball's — i.e. "stand where
    //      you are, the pass is coming straight at you".
    //   4. The spin/lead path (7862-7890) bases the target on the RECEIVER's
    //      position (esi = A1) + kBallFriction entry, not the ball's.
    //
    // Branch map (lines reference updatePlayers.cpp):
    //   7664-7672: CPU team (playerNumber == 0) → l_cpu_passing_to (no write).
    //   7674-7696: longPass == 0 && leftSpin == 0 && rightSpin == 0
    //              → l_cpu_passing_to (no write).
    //   7698-7710: ballSprite.speed < 512 (unsigned) → l_cpu_passing_to.
    //   7712-7719: passingToPlayer == 0 → l_cpu_passing_to.
    //   7721-7727: ballSprite.direction < 0 → l_ball_got_no_direction.
    //   7729-7761: D0 = ball.fullDirection - receiver.fullDirection (int8);
    //              |D0| > 64 → l_player_not_turned_enough_toward_ball:
    //              clear passingToPlayer + passingBall, → l_cpu_passing_to
    //              (NO ballX/Y write).
    //   7769-7792: -4 <= D0 <= 4 → l_ball_got_no_direction:
    //              team.ballX/Y = receiver.x/y.
    //   7805-7890: spin lead: D1 = longPass ? 224 : 192 (negated if D0 < 0);
    //              slot = ((ball.fullDir + D1 + 4) & 0xFF) >> 3;
    //              team.ballX = receiver.x + kBallFriction[slot].dx;
    //              team.ballY = receiver.y + kBallFriction[slot].dy.
    // Source:
    //   external/swos-port/src/game/updatePlayers/updatePlayers.cpp:7664-7891
    //   external/swos-port/swos/swos.asm:245568 (kBallFriction)
    private static void ApplyKBallFrictionOrFallback(int spriteAddr, int teamBase)
    {
        // updatePlayers.cpp:7664-7672 — CPU team: → l_cpu_passing_to, no write.
        short playerNumber = Memory.ReadSignedWord(teamBase + TeamData.OffPlayerNumber);
        if (playerNumber == 0)
            return;

        // updatePlayers.cpp:7674-7696 — plain pass gate: only longPass OR
        // leftSpin OR rightSpin enters the prediction block; otherwise
        // → l_cpu_passing_to (no write, no flag clearing).
        int longPass = Memory.ReadSignedDword(teamBase + TeamData.OffLongPass);
        if (longPass == 0)
        {
            short leftSpin = Memory.ReadSignedWord(teamBase + TeamData.OffLeftSpin);
            if (leftSpin == 0)
            {
                short rightSpin = Memory.ReadSignedWord(teamBase + TeamData.OffRightSpin);
                if (rightSpin == 0)
                    return;
            }
        }

        // updatePlayers.cpp:7698-7710 — `cmp ballSprite.speed, 512 / jb @@cpu_passing_to`.
        // Unsigned compare: under 512 → no write.
        int ballSpeed = BallSprite.Speed;
        if ((ushort)ballSpeed < 512)
            return;

        // updatePlayers.cpp:7712-7719 — `cmp team.passingToPlayer, 0 / jz @@cpu_passing_to`.
        short passingToPlayer = Memory.ReadSignedWord(teamBase + TeamData.OffPassingToPlayer);
        if (passingToPlayer == 0)
            return;

        // The receiver's whole-pixel position — the base for every target
        // write below (asm: esi = A1 at 7795-7802 and 7862-7890).
        short recvX = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffX + 2);
        short recvY = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffY + 2);

        // updatePlayers.cpp:7721-7727 — `mov ax, ballSprite.direction / js l_ball_got_no_direction`.
        // direction is the QUANTISED 0..7 byte; the `js` checks bit 15 of the
        // word — i.e. direction == -1 (no direction).
        short ballDir = BallSprite.Direction;
        short ballFullDir = BallSprite.FullDirection;
        if (ballDir < 0)
            goto l_ball_got_no_direction;

        {
            // updatePlayers.cpp:7729-7738 — D0 = ball.fullDirection - player.fullDirection
            // (signed 8-bit subtract). Both are 0..255 angles.
            short playerFullDir = Memory.ReadSignedWord(spriteAddr + PlayerSprite.OffFullDirection);
            sbyte d0 = (sbyte)((byte)(ballFullDir & 0xFF) - (byte)(playerFullDir & 0xFF));

            // updatePlayers.cpp:7740-7761 — `cmp D0, 64 / jg` and `cmp D0, -64 / jl`
            // → l_player_not_turned_enough_toward_ball.
            if (d0 > 64 || d0 < -64)
            {
                // updatePlayers.cpp:7763-7767 — clear passingToPlayer +
                // passingBall, then `jmp @@cpu_passing_to` — NO ballX/Y write.
                Memory.WriteWord(teamBase + TeamData.OffPassingToPlayer, 0);
                Memory.WriteWord(teamBase + TeamData.OffPassingBall, 0);
                return;
            }

            // updatePlayers.cpp:7769-7792 — cseg_82952: `cmp D0, 4 / jg spin`
            // and `cmp D0, -4 / jl spin`; fall-through (|D0| <= 4)
            // → l_ball_got_no_direction.
            if (d0 <= 4 && d0 >= -4)
                goto l_ball_got_no_direction;

            // updatePlayers.cpp:7805-7816 — D1 selector: 192 default, 224 when
            // longPass (dword field, zero/non-zero gate).
            sbyte d1 = (sbyte)(longPass != 0 ? 224 : 192);

            // updatePlayers.cpp:7818-7827 — cseg_829AC:
            //   mov al, byte ptr D0 / or al, al / jl cseg_829BB / neg byte ptr D1
            // The `jl` SKIPS the negation when D0 is NEGATIVE — i.e. D1 is
            // negated when D0 >= 0.
            // BUG FIX (2026-07-03, task #172 "receiver runs away from spun
            // pass"): this condition was previously `d0 < 0` — inverted vs the
            // asm — so the perpendicular lead offset pointed to the WRONG side
            // of the ball's travel line (e.g. ball east, receiver north of the
            // line, D0 > 0: original leads at ballDir+64 = south = intercept;
            // inverted led at ballDir-64 = north = away from the ball).
            if (d0 >= 0)
                d1 = (sbyte)(-d1);

            // updatePlayers.cpp:7829-7861 — slot = ((ball.fullDir + D1 + 4) & 0xFF) >> 3
            // (the asm's extra `shl 2` is the byte stride for dw pairs; our
            // managed array steps in shorts, 2 per slot).
            int angle = (byte)((ballFullDir & 0xFF) + d1 + 4);
            int idx = (angle >> 3) & 0x1F;  // 0..31

            // updatePlayers.cpp:7862-7890 — team.ballX = receiver.x + table[slot].dx;
            //                               team.ballY = receiver.y + table[slot].dy.
            short dx = s_kBallFriction[idx * 2];
            short dy = s_kBallFriction[idx * 2 + 1];
            Memory.WriteWord(teamBase + TeamData.OffBallX, (short)(recvX + dx));
            Memory.WriteWord(teamBase + TeamData.OffBallY, (short)(recvY + dy));
            return;
        }

    l_ball_got_no_direction:
        // updatePlayers.cpp:7794-7803 — team.ballX/Y = RECEIVER's own x/y
        // (esi = A1): the pass is on target, hold position and wait for it.
        Memory.WriteWord(teamBase + TeamData.OffBallX, recvX);
        Memory.WriteWord(teamBase + TeamData.OffBallY, recvY);
    }
}
