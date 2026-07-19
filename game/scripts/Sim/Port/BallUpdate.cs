namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Ported ball functions from external/swos-port/src/game/ball/ball.cpp.
// Each method documents its source file:line. We translate idiomatic C++
// (`swos.ballSprite.x = x;`) directly into SwosVm.BallSprite property
// assignments — same semantics, same backing memory layout.
//
// Phase B3: warmup ports (simple utilities) — DONE.
// Phase B4: WIP — full updateBall() / applyBallAfterTouch() port.
//   ✅ ApplyBallAfterTouch (ball spin / curl + post-kick speed adjust)
//   ✅ ResetBothTeamSpinTimers
//   ✅ SetBallPosition
//   ⏳ updateBall (per-tick motion, bounce, possession) — not started
//   ⏳ calculateNextBallPosition (destX/destY + speed → deltaX/deltaY) — not started
//
// Until BallUpdate.Tick() is implemented, Main.cs gates calls behind the
// `_useSwosPort` toggle which defaults to false.
public static class BallUpdate
{
    // === Phase B4 scaffolding =================================================
    // Sections of ball.cpp:updateBall() to port (~25 logical blocks identified):
    //
    //  [TODO] 1. Hide/show ball + frame index pick                 ball.cpp:13-90
    //  [TODO] 2. Goal post bounce                                  ball.cpp:91-180
    //  [TODO] 3. Direction quantisation (Sprite.direction)         ball.cpp:181-239
    //  [DONE] 4. Linear friction selector (ground/air/pitch)       (in BallSim)
    //  [TODO] 5. Apply deltaX/Y/Z to position                      ball.cpp:300-350
    //  [TODO] 6. Z clamp + bounce-on-ground                        ball.cpp:351-420
    //  [TODO] 7. Possession check (player/keeper)                  ball.cpp:421-530
    //  [DONE] 8. Spin (applyBallAfterTouch)                        ball.cpp:2248-3005
    //  [DONE] 9. checkIfBallOutOfPlay                              (BallOutOfPlay.cs)

    // Per-tick ball physics + state machine. Mirrors ball.cpp:13-2247 updateBall
    // in section-by-section ports. Each section is gated by feature flags
    // (NotImplementedException) so partial progress builds + boots cleanly —
    // toggle the menu slot only after all sections land.
    //
    // Two entry points:
    //   - `Tick()` runs the FULL pipeline (Section1+2+3) — used when the SWOS
    //     destX/destY-based motion model owns the state (port AI → port motion).
    //   - `TickPhysicsOnly()` skips Section2 (direction recalc) — used by
    //     `BallSyncBridge` to bridge our velocity-based BallState into the
    //     port's physics. Caller pre-populates DeltaX/Y/Z and the port runs
    //     friction + position + bounce without overwriting velocity.
    public static void Tick()
    {
        Section1_HideAndFrameIndex();

        // hideBall == 1 path: Section1 already wrote imageIndex=-1 and returned
        // before frame extraction. Original code also skips animation but DOES
        // continue with sections 2+ (line 27 `goto l_calculate_deltas` means
        // "skip frame stuff, do delta calc"). Section2's logic is fine to run
        // when hidden — ball still updates physics, just isn't drawn.

        Section2_DirectionAndFriction();
        Section3_ApplyDeltasAndBounce();
        Section4_GoalDetectionAndShadow();
    }

    // Bridge entry point — runs Section1 (animation cosmetic) + Section3
    // (apply deltas + gravity + bounce + barriers) but **SKIPS Section2**
    // (direction recalc from destX/destY). Used by `BallSyncBridge` when our
    // velocity-based BallState owns motion: we pre-populate DeltaX/Y/Z from
    // VelocityX/Y/Z, then the port applies friction + position + bounce
    // WITHOUT overwriting our velocity vector.
    //
    // This is the pragmatic compromise for B4.7 wire-up. Once the rest of
    // updatePlayers AI lands, the full `Tick()` entry point becomes correct
    // (destX/destY are set by AI, Section2 recomputes velocity each tick).
    //
    // Section1 IS run because frame index switching + image picking is purely
    // cosmetic; Section3 IS run because that's where the physics live.
    // Friction inside Section2 IS lost — Section3's friction is in the bounce
    // path, not on every tick. Caller (BallSyncBridge) replicates the per-tick
    // friction in Push() so the result still matches what BallSim would do.
    public static void TickPhysicsOnly()
    {
        Section1_HideAndFrameIndex();
        Section3_ApplyDeltasAndBounce();
    }

    // ball.cpp:299-735 — combined: apply deltas, handle keeper-holds-ball Z
    // path, gravity + ground bounce, pitch barrier bounce (during non-game-in-progress
    // states like set pieces).
    //
    // Original asm uses D5/D6/D7 (saved at l_calculate_deltas line 182-186) as
    // pre-motion position snapshot. If the ball hits an invisible barrier (during
    // set pieces), it rolls position back to D5/D6/D7. We pass these as locals.
    private static void Section3_ApplyDeltasAndBounce()
    {
        // ball.cpp:182-186 — save pre-motion position (referenced again by
        // barrier-bounce rollback).
        int preX = BallSprite.X;  // Q16.16
        int preY = BallSprite.Y;
        int preZ = BallSprite.Z;

        // ball.cpp:300-324 — apply deltaX/deltaY to position (Q16.16 add).
        int dx = BallSprite.DeltaX;
        int dy = BallSprite.DeltaY;
        int dz = BallSprite.DeltaZ;

        BallSprite.X = (int)(BallSprite.X + dx);
        BallSprite.Y = (int)(BallSprite.Y + dy);

        // ball.cpp:325-336 — gameState check: ST_KEEPER_HOLDS_BALL = 3.
        // Branch into keeper-specific Z handling vs. normal gravity path.
        short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);
        bool keeperHoldsBall = gameState == 3;

        bool gotoSetDeltaZTo0 = false;  // flag to jump to l_set_delta_z_to_0
        bool gotoApplyDeltaZ = false;   // flag to jump to l_apply_delta_z directly with custom dz

        if (keeperHoldsBall)
        {
            // ball.cpp:338-372 — substitution carve-out. The "set z=0, dz=0" path
            // fires ONLY when ALL THREE conditions hold:
            //   1. substituteInProgress != 0
            //   2. teamThatSubstitutes == lastTeamPlayedBeforeBreak
            //   3. teamThatSubstitutes->controlledPlayer == null
            // Otherwise → goto keeper_is_controlled (check_keeper_z runs, dz forced
            // to push ball toward keeper hand height).
            //
            // Fix 2026-05-23: previous implementation had this inverted — set
            // dz=0 whenever lastTeamPlayed was null. Caught by golden test at
            // frame 5135 (kickoff.csv): expected dz=131076 (force-up), got 0.
            bool substituteInProgress = Memory.ReadWord(Memory.Addr.g_substituteInProgress) != 0;
            bool skipKeeperZ = false;

            if (substituteInProgress)
            {
                int teamThatSubs = Memory.ReadSignedDword(Memory.Addr.teamThatSubstitutes);
                int lastTeamPlayed = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
                if (teamThatSubs == lastTeamPlayed && teamThatSubs != 0)
                {
                    int controlledPlayer = TeamData.ControlledPlayerFromBase(teamThatSubs);
                    if (controlledPlayer == 0)
                    {
                        // ball.cpp:370-372 — all three conditions met.
                        BallSprite.ZPixels = 0;
                        gotoSetDeltaZTo0 = true;
                        skipKeeperZ = true;
                    }
                }
            }

            if (!skipKeeperZ)
            {
                // ball.cpp:374-431 — keeper_is_controlled: speed check + optional
                // call to updateBallWithControllingGoalkeeper.
                // Then fall through to check_keeper_z REGARDLESS of speed.
                if (BallSprite.Speed != 0)
                {
                    // ball.cpp:396-431 — read lastTeamPlayedBeforeBreak's
                    // controlledPlayer, pass to updateBallWithControllingGoalkeeper.
                    int lastTeamPlayed = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
                    if (lastTeamPlayed != 0)
                    {
                        int controlledPlayer = TeamData.ControlledPlayerFromBase(lastTeamPlayed);
                        if (controlledPlayer != 0)
                        {
                            PlayerUpdate.UpdateBallWithControllingGoalkeeper(controlledPlayer);
                        }
                    }
                    // writeOnlyVar04 increment (g_memByte[455948] += 1) intentionally
                    // omitted — irrelevant for fidelity (write-only debug counter).
                }

                // ball.cpp:433-456 — check_keeper_z. Based on ball height:
                //   z.whole == 5: settle (deltaZ = 0)
                //   z.whole >  5: deltaZ = -65538  (Q16.16 = -1.000030, fall toward keeper hand height)
                //   z.whole <  5: deltaZ = +131076 (Q16.16 = +2.000061, rise to keeper hand height)
                // Keeper-hand height is fixed at z=5 px.
                // B4.9 audit Bug #3 verified 2026-06-01: ball.cpp:451 `D3 = 20004h`
                // (= +131076) is taken on `jb @@apply_delta_z` from `cmp z, 5`, i.e.
                // z < 5 (ball below hand). ball.cpp:455 `D3 = -10002h` (= -65538)
                // is taken on `ja @@height_higher_than_5`, i.e. z > 5. Signs match.
                short zWhole = BallSprite.ZPixels;
                if (zWhole == 5)
                {
                    gotoSetDeltaZTo0 = true;
                }
                else if (zWhole > 5)
                {
                    dz = -65538;  // -10002h in asm
                    gotoApplyDeltaZ = true;
                }
                else
                {
                    dz = 131076;  // 20004h in asm
                    gotoApplyDeltaZ = true;
                }
            }
        }
        else
        {
            // ball.cpp:458-474 — keeper_doesnt_hold_the_ball. Apply gravity to deltaZ.
            // ball.cpp:463-465 `jz @@assign_delta_z` — if existing deltaZ is 0,
            // SKIP apply_delta_z entirely (no Z+=dz, no bounce, no ZPixels touch).
            // B4.9 audit Bug #7 fix 2026-06-01: previous code unconditionally
            // ran apply_delta_z, which is harmless when Z>=0 (Z+=0 is no-op and
            // sign-check passes), but diverges from asm semantics if Z were
            // ever negative at entry. Reachability of that case is gated by
            // post-bounce ZPixels=0 clamp, so the bug doesn't manifest in
            // practice — but mechanical parity demands the early exit.
            if (dz != 0)
            {
                int gravity = Memory.ReadSignedDword(Memory.Addr.kGravityConstant);
                dz -= gravity;
                // ball.cpp:474 `or D3, 1` — force odd bit. Prevents deltaZ from
                // ever becoming exactly 0 (which would skip gravity next tick).
                dz |= 1;
                gotoApplyDeltaZ = true;
            }
            // else: dz stays 0, fall through directly to assign_delta_z below.
        }

        if (gotoSetDeltaZTo0)
        {
            // ball.cpp:549-551 — set_delta_z_to_0. fall through to assign_delta_z.
            dz = 0;
        }
        else if (gotoApplyDeltaZ)
        {
            // ball.cpp:476-547 — apply_delta_z: ball.z += deltaZ, check if went
            // negative (i.e., bounced through ground), apply XY damping (via
            // ballSpeedBounceFactor) + Z reflection (via ballBounceFactor).
            int newZ = BallSprite.Z + dz;
            BallSprite.Z = newZ;

            if (newZ < 0)
            {
                // ball.cpp:494-547 — bounce mechanics.
                // ball.cpp:494-513 — XY speed reduction (B4.9 audit Bug #4 fix
                // 2026-06-01): use UNSIGNED 16×16→32 multiply matching `mul bx`
                // semantics (NOT `imul`). Previous signed-int math gave the same
                // result in practice (speed and bounceXY both small positive) but
                // diverged from ball.cpp on edge cases. Sequence ball.cpp:494-513:
                //   ax = word(speed); bx = word(ballSpeedBounceFactor);
                //   mul bx;                  // dx:ax = uint32(ax * bx)
                //   D0 = (dx<<16) | ax;
                //   shr D0, 8;               // logical shift right by 8 (unsigned)
                //   sub [speed], ax;         // subtract low word (16-bit signed)
                uint uSpeed = (ushort)BallSprite.Speed;
                uint uBounceXY = (ushort)Memory.ReadWord(Memory.Addr.ballSpeedBounceFactor);
                uint uProd = uSpeed * uBounceXY;
                uint uShrink = uProd >> 8;
                short shrinkLow = (short)(ushort)uShrink;  // low 16 bits, signed for subtract
                BallSprite.Speed = (short)(BallSprite.Speed - shrinkLow);

                // ball.cpp:514 — z.whole = 0 (clamp to ground).
                BallSprite.ZPixels = 0;

                // ball.cpp:515-536 — reflect deltaZ (B4.9 audit Bug #5 fix
                // 2026-06-01): same signed/unsigned discipline as Bug #4.
                // Sequence ball.cpp:515-536:
                //   neg D3;                  // signed 32-bit negate
                //   sar D0(=D3), 8;          // ARITHMETIC shift right (sign-preserving)
                //   ax = word(D0);           // low 16 bits as uint16 for `mul`
                //   mul bx (= ballBounceFactor); // UNSIGNED 16×16 → uint32
                //   D0 = (dx<<16) | ax;      // unsigned 32-bit product
                //   sub D3, eax;             // signed 32-bit subtract
                //   or D3, 1;
                //
                // The `ax = word(D0)` step after `sar` is the load-bearing detail:
                // when shiftedDz is negative, the low 16 bits — as uint16 —
                // become a large positive number, so `mul` yields a much larger
                // result than the signed `imul` we previously used.
                int negDz = -dz;
                int shiftedDz = negDz >> 8;  // arithmetic shift preserves sign
                uint uShiftedLow = (ushort)shiftedDz;       // low 16 bits as unsigned
                uint uBounceZ = (ushort)Memory.ReadWord(Memory.Addr.ballBounceFactor);
                uint uProdZ = uShiftedLow * uBounceZ;       // unsigned 16×16 → 32
                dz = negDz - (int)uProdZ;                   // signed 32-bit subtract
                dz |= 1;

                // ball.cpp:537-547 — if D3 > 40960 (0xA000), play bounce sample.
                // Else, jump to set_delta_z_to_0 (squelch the bounce, ball at rest).
                if (dz <= 40960)
                {
                    // Small bounce — settle.
                    dz = 0;
                }
                else
                {
                    // ball.cpp:537-547 — bounce loud enough → play BOUNCEX.
                    OpenSwos.Audio.MatchAudio.PlayBounce();
                }

            }
        }

        // ball.cpp:568-571 — assign_delta_z: write deltaZ back to ball sprite.
        BallSprite.DeltaZ = dz;

        // ball.cpp:572-583 — check gameStatePl. If ST_GAME_IN_PROGRESS (100),
        // skip barrier checks (real pitch has goal lines that allow ball-out)
        // and jump straight to `l_in_allowed_range_y` (top of Section 4).
        // Else (set piece / cinema / etc.), bounce off invisible barriers
        // first, then fall through to Section 4 anyway.
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        if (gameStatePl != 100)
        {
            // ball.cpp:585-609 — check X bounds [53, 618]. If outside, bounce.
            short xWhole = Memory.ReadSignedWord(BallSprite.Base + 32);
            if (xWhole < 53 || xWhole > 618)
            {
                // ball.cpp:611-644 — bounce off X barrier:
                //   destX = 2*x - destX (reverseDestXDirection)
                //   speed /= 2  (UNSIGNED — `shr [speed], 1`, ball.cpp:633-638)
                //   restore x, y, z to pre-motion values (D5, D6, D7)
                // B4.9 audit Bug #6 fix 2026-06-01: ball.cpp:635 is `shr` on a
                // word (unsigned). Previous code used C#'s signed `>>` which
                // diverges if speed somehow becomes negative. Speed values are
                // normally positive (bounceFactors are 24-80 << 256) so this
                // typically doesn't manifest, but mechanical fidelity demands
                // unsigned semantics.
                ReverseDestXDirection();
                BallSprite.Speed = (short)((ushort)BallSprite.Speed >> 1);
                BallSprite.X = preX;
                BallSprite.Y = preY;
                BallSprite.Z = preZ;
            }

            // ball.cpp:646-672 — check Y bounds [100, 799]. If outside, bounce.
            short yWhole = Memory.ReadSignedWord(BallSprite.Base + 36);
            if (yWhole < 100 || yWhole > 799)
            {
                // ball.cpp:674-707 — bounce off Y barrier (symmetric to X).
                // Same unsigned `shr` semantics as X bounce (ball.cpp:697-701).
                ReverseDestYDirection();
                BallSprite.Speed = (short)((ushort)BallSprite.Speed >> 1);
                BallSprite.X = preX;
                BallSprite.Y = preY;
                BallSprite.Z = preZ;
            }
        }

        // ball.cpp:709-735 — `l_in_allowed_range_y`: y < 129 or y > 769
        // → enter Section 4's goal-detection path. Stash the snapshot in
        // module-level statics so Section4 can read them without an extra
        // parameter pass.
        _section4PreX = preX;
        _section4PreY = preY;
        _section4PreZ = preZ;
    }

    // D5/D6/D7 snapshot passed between Section3 and Section4 (in asm, those
    // were saved registers; in C# we use a static so the call signature
    // stays clean).
    private static int _section4PreX;
    private static int _section4PreY;
    private static int _section4PreZ;

    // ball.cpp:737-2247 — `l_goal_or_gol_out` through end of updateBall.
    // Covers:
    //   - Upper-goal Y range detection (y ∈ (112, 128]) → goal/post collision
    //   - Top-of-upper-goal bar deflect (z > 15) vs net catch
    //   - Lower-goal Y range (y ∈ [770, 785)) → mirror of upper
    //   - Left/right post collisions inside goal mouth (reverseDestXDirection)
    //   - Goal-scored detection (l_penalty_goal / l_own_goal) with deltaY gate
    //   - Post/bar audio cue (PlayPostHitComment / PlayBarHitComment)
    //   - Out-of-play check (when x < 81 or x > 590 or y < 129 or y > 769)
    //   - Ball-shadow update (X/Y derived from ball X/Y + z/2 offset)
    //   - Ball quadrant calculation (l_calc_x_ball_quadrant → l_set_y_quadrant)
    //     for AI player without-ball positioning.
    //
    // Snapshot D5/D6/D7 = `_section4PreX/Y/Z` (pre-motion ball position).
    private static void Section4_GoalDetectionAndShadow()
    {
        // ball.cpp:738-744 — load whole-pixel ball position from sprite.
        int D1 = (short)BallSprite.XPixels;  // word
        int D2 = (short)BallSprite.YPixels;
        int D3 = (short)BallSprite.ZPixels;
        int D5 = _section4PreX;
        int D6 = _section4PreY;
        int D7 = _section4PreZ;
        int D4 = 0;  // used by penalty/own-goal RNG

        // ball.cpp:746-750 — `sub word ptr D1, 1` (pre-bias for goal coord asymmetry).
        D1 = (short)(D1 - 1);  // from ball.cpp:747

        // ball.cpp:751-761 — `cmp D2, 128 / jg @@not_in_upper_goal`.
        Flags.SetFromSub16(D2, 128);  // from ball.cpp:753
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto l_not_in_upper_goal;

        // ball.cpp:763-773 — `cmp D2, 112 / jg @@in_upper_goal_y`.
        Flags.SetFromSub16(D2, 112);  // from ball.cpp:765
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto l_in_upper_goal_y;

    l_not_in_upper_goal:
        // ball.cpp:775-786 — `cmp D2, 770 / jl @@not_in_lower_goal`.
        Flags.SetFromSub16(D2, 770);  // from ball.cpp:778
        if (Flags.Sign != Flags.Overflow)
            goto l_not_in_lower_goal;

        // ball.cpp:788-798 — `cmp D2, 785 / jge @@not_in_lower_goal`.
        Flags.SetFromSub16(D2, 785);  // from ball.cpp:790
        if (Flags.Sign == Flags.Overflow)
            goto l_not_in_lower_goal;

        // ball.cpp:800-810 — `cmp D3, 19 / jg @@not_in_lower_goal`.
        Flags.SetFromSub16(D3, 19);  // from ball.cpp:802
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto l_not_in_lower_goal;

        // ball.cpp:812-822 — `cmp D1, 295 / jle @@not_in_lower_goal`.
        Flags.SetFromSub16(D1, 295);  // from ball.cpp:814
        if (Flags.Zero || Flags.Sign != Flags.Overflow)
            goto l_not_in_lower_goal;

        // ball.cpp:824-834 — `cmp D1, 372 / jg @@not_in_lower_goal`.
        Flags.SetFromSub16(D1, 372);  // from ball.cpp:826
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto l_not_in_lower_goal;

        // ball.cpp:836-846 — `cmp D3, 15 / jg @@ball_in_top_of_lower_goal`.
        Flags.SetFromSub16(D3, 15);  // from ball.cpp:838
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto l_ball_in_top_of_lower_goal;

        // ball.cpp:848-858 — `cmp D1, 302 / jl @@left_edge_of_lower_goal`.
        Flags.SetFromSub16(D1, 302);  // from ball.cpp:850
        if (Flags.Sign != Flags.Overflow)
            goto l_left_edge_of_lower_goal;

        // ball.cpp:860-870 — `cmp D1, 366 / jg @@left_edge_of_lower_goal`.
        Flags.SetFromSub16(D1, 366);  // from ball.cpp:862
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto l_left_edge_of_lower_goal;

        // ball.cpp:872-882 — `cmp D2, 778 / jg @@ball_in_net`.
        Flags.SetFromSub16(D2, 778);  // from ball.cpp:874
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto l_ball_in_net;

        goto l_not_in_lower_goal;  // ball.cpp:884

    l_in_upper_goal_y:
        // ball.cpp:886-897 — `cmp D3, 19 / jg @@not_in_lower_goal`.
        Flags.SetFromSub16(D3, 19);  // from ball.cpp:889
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto l_not_in_lower_goal;

        // ball.cpp:899-909 — `cmp D1, 295 / jle @@not_in_lower_goal`.
        Flags.SetFromSub16(D1, 295);  // from ball.cpp:901
        if (Flags.Zero || Flags.Sign != Flags.Overflow)
            goto l_not_in_lower_goal;

        // ball.cpp:911-921 — `cmp D1, 372 / jg @@not_in_lower_goal`.
        Flags.SetFromSub16(D1, 372);  // from ball.cpp:913
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto l_not_in_lower_goal;

        // ball.cpp:923-933 — `cmp D2, 123 / jg @@ball_just_in_upper_goal`.
        Flags.SetFromSub16(D2, 123);  // from ball.cpp:925
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto l_ball_just_in_upper_goal;

        // ball.cpp:935-945 — `cmp D3, 10 / jg @@top_of_upper_goal`.
        Flags.SetFromSub16(D3, 10);  // from ball.cpp:937
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto l_top_of_upper_goal;

        goto l_ball_in_upper_net;  // ball.cpp:947

    l_ball_just_in_upper_goal:
        // ball.cpp:949-960 — `cmp D3, 15 / jg @@top_of_upper_goal`.
        Flags.SetFromSub16(D3, 15);  // from ball.cpp:952
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto l_top_of_upper_goal;
        // fall through to l_ball_in_upper_net

    l_ball_in_upper_net:
        // ball.cpp:962-972 — `cmp D1, 302 / jl @@left_edge_of_lower_goal`.
        Flags.SetFromSub16(D1, 302);  // from ball.cpp:965
        if (Flags.Sign != Flags.Overflow)
            goto l_left_edge_of_lower_goal;

        // ball.cpp:975-985 — `cmp D1, 366 / jg @@left_edge_of_lower_goal`.
        Flags.SetFromSub16(D1, 366);  // from ball.cpp:977
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto l_left_edge_of_lower_goal;

        // ball.cpp:987-997 — `cmp D2, 119 / jl @@ball_in_net`.
        Flags.SetFromSub16(D2, 119);  // from ball.cpp:989
        if (Flags.Sign != Flags.Overflow)
            goto l_ball_in_net;

        goto l_not_in_lower_goal;  // ball.cpp:999

    l_top_of_upper_goal:
        // ball.cpp:1001-1031 — top of upper goal: BAR collision.
        // D0 = (D7 high word swapped) → essentially preZ in pixels — confusing
        // but mechanically equivalent to D7 >> 16 (whole-pixel preZ).
        // If preZ > 15 → goto l_top_of_the_goal (ball was already above the bar).
        // Else: deflect deltaZ = 1, speed = 0, rollback to pre-motion XYZ.
        {
            int preZpx = (short)(D7 >> 16);  // ball.cpp:1002-1009 — xchg trick
            Flags.SetFromSub16(preZpx, 15);  // from ball.cpp:1012
            if (!Flags.Zero && Flags.Sign == Flags.Overflow)
                goto l_top_of_the_goal;
        }
        // ball.cpp:1022-1031.
        BallSprite.DeltaZ = 1;          // from ball.cpp:1023
        BallSprite.Speed = 0;           // from ball.cpp:1024
        BallSprite.X = D5;              // from ball.cpp:1025-1026
        BallSprite.Y = D6;              // from ball.cpp:1027-1028
        BallSprite.Z = D7;              // from ball.cpp:1029-1030
        goto l_not_in_lower_goal;

    l_top_of_the_goal:
        // ball.cpp:1033-1053 — ball was over bar: bend it BACK into the field
        // (destY -= 1000), speed = 512, deltaZ = 1.
        BallSprite.DeltaZ = 1;          // from ball.cpp:1035
        {
            int yPx = BallSprite.YPixels;  // ball.cpp:1036-1037
            int destY = (short)(yPx - 1000);  // from ball.cpp:1040
            BallSprite.DestY = (short)destY;  // from ball.cpp:1049
        }
        BallSprite.Speed = 512;         // from ball.cpp:1050
        BallSprite.Z = D7;              // from ball.cpp:1051-1052
        goto l_not_in_lower_goal;

    l_ball_in_top_of_lower_goal:
        // ball.cpp:1055-1085 — symmetric to l_top_of_upper_goal but for lower
        // goal. If preZ > 15 (ball above bar already) → bend back (destY+=1000),
        // else rollback + speed=0.
        {
            int preZpx = (short)(D7 >> 16);  // ball.cpp:1056-1063
            Flags.SetFromSub16(preZpx, 15);  // from ball.cpp:1066
            if (!Flags.Zero && Flags.Sign == Flags.Overflow)
                goto cseg_7C69B;
        }
        // ball.cpp:1076-1085 — rollback path.
        BallSprite.DeltaZ = 1;          // from ball.cpp:1077
        BallSprite.Speed = 0;           // from ball.cpp:1078
        BallSprite.X = D5;              // from ball.cpp:1079-1080
        BallSprite.Y = D6;              // from ball.cpp:1081-1082
        BallSprite.Z = D7;              // from ball.cpp:1083-1084
        goto l_not_in_lower_goal;

    cseg_7C69B:
        // ball.cpp:1087-1107 — ball over lower bar: destY += 1000.
        BallSprite.DeltaZ = 1;          // from ball.cpp:1089
        {
            int yPx = BallSprite.YPixels;  // ball.cpp:1090
            int destY = (short)(yPx + 1000);  // from ball.cpp:1094
            BallSprite.DestY = (short)destY;  // from ball.cpp:1103
        }
        BallSprite.Speed = 512;         // from ball.cpp:1104
        BallSprite.Z = D7;              // from ball.cpp:1105-1106
        goto l_not_in_lower_goal;

    l_ball_in_net:
        // ball.cpp:1109-1124 — back of net: reverse Y direction + shrink speed
        // by /8 (3-bit shift). Then jump to common cseg_7C756 to restore X/Y.
        ReverseDestYDirection();        // ball.cpp:1110
        ResetBothTeamSpinTimers();      // ball.cpp:1111
        {
            int speed = BallSprite.Speed;  // ball.cpp:1113
            BallSprite.Speed = (short)((ushort)speed >> 3);  // from ball.cpp:1121
        }
        goto cseg_7C756;

    l_left_edge_of_lower_goal:
        // ball.cpp:1126-1137 — side post: reverse X direction + shrink speed /4.
        ReverseDestXDirection();        // ball.cpp:1127
        ResetBothTeamSpinTimers();      // ball.cpp:1128
        {
            int speed = BallSprite.Speed;  // ball.cpp:1130
            BallSprite.Speed = (short)((ushort)speed >> 2);  // from ball.cpp:1135
        }
        // fall through to cseg_7C756

    cseg_7C756:
        // ball.cpp:1139-1144 — restore X/Y (NOT Z) to pre-motion.
        BallSprite.X = D5;              // from ball.cpp:1140-1142
        BallSprite.Y = D6;              // from ball.cpp:1143-1144

    l_not_in_lower_goal:
        // ball.cpp:1146-1158 — `cmp gameStatePl, 100 / jnz cseg_7CA2C`.
        // Only check for goal events when game is in progress.
        {
            short gsPl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            Flags.SetFromSub16(gsPl, 100);  // from ball.cpp:1150
            if (!Flags.Zero)
                goto cseg_7CA2C;
        }

        // ball.cpp:1160-1166 — refresh D1/D2/D3 from current ball position
        // (ball X/Y may have been rolled back to D5/D6/D7 in the post-collision
        // paths above).
        D1 = (short)BallSprite.XPixels;  // ball.cpp:1161
        D2 = (short)BallSprite.YPixels;  // ball.cpp:1163
        D3 = (short)BallSprite.ZPixels;  // ball.cpp:1165

        // ball.cpp:1167-1172 — `sub D1, 1` (same coord-bias as line 747).
        D1 = (short)(D1 - 1);  // from ball.cpp:1169

        // ball.cpp:1173-1183 — `cmp D2, 128 / jle cseg_7CA2C`.
        Flags.SetFromSub16(D2, 128);  // from ball.cpp:1175
        if (Flags.Zero || Flags.Sign != Flags.Overflow)
            goto cseg_7CA2C;

        // ball.cpp:1185-1195 — `cmp D2, 132 / jle cseg_7C7F0` (upper goal cap area).
        Flags.SetFromSub16(D2, 132);  // from ball.cpp:1187
        if (Flags.Zero || Flags.Sign != Flags.Overflow)
            goto cseg_7C7F0;

        // ball.cpp:1197-1207 — `cmp D2, 770 / jge cseg_7CA2C`.
        Flags.SetFromSub16(D2, 770);  // from ball.cpp:1199
        if (Flags.Sign == Flags.Overflow)
            goto cseg_7CA2C;

        // ball.cpp:1209-1219 — `cmp D2, 766 / jl cseg_7CA2C` (lower goal cap area).
        Flags.SetFromSub16(D2, 766);  // from ball.cpp:1211
        if (Flags.Sign != Flags.Overflow)
            goto cseg_7CA2C;
        // fall through to cseg_7C7F0

    cseg_7C7F0:
        // ball.cpp:1221-1232 — `cmp D3, 19 / jg cseg_7CA2C`.
        Flags.SetFromSub16(D3, 19);  // from ball.cpp:1224
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto cseg_7CA2C;

        // ball.cpp:1234-1244 — `cmp D1, 295 / jle cseg_7CA2C`.
        Flags.SetFromSub16(D1, 295);  // from ball.cpp:1236
        if (Flags.Zero || Flags.Sign != Flags.Overflow)
            goto cseg_7CA2C;

        // ball.cpp:1246-1256 — `cmp D1, 372 / jg cseg_7CA2C`.
        Flags.SetFromSub16(D1, 372);  // from ball.cpp:1248
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto cseg_7CA2C;

        // ball.cpp:1258-1268 — `cmp D3, 15 / jg @@penalty_goal`.
        // Above the bar → penalty (ball hit upright/post above bar height).
        Flags.SetFromSub16(D3, 15);  // from ball.cpp:1260
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto l_penalty_goal;

        // ball.cpp:1270-1280 — `cmp D1, 302 / jl @@own_goal`.
        // Left edge of goal → own_goal handling (post hit on left upright).
        Flags.SetFromSub16(D1, 302);  // from ball.cpp:1272
        if (Flags.Sign != Flags.Overflow)
            goto l_own_goal;

        // ball.cpp:1282-1292 — `cmp D1, 366 / jg @@own_goal`.
        // Right edge of goal → own_goal.
        Flags.SetFromSub16(D1, 366);  // from ball.cpp:1284
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto l_own_goal;

        goto cseg_7CA2C;  // ball.cpp:1294

    l_penalty_goal:
        // ball.cpp:1296-1341 — set goalTypeScored = 1 (GT_PENALTY), pick D4 jitter
        // from currentGameTick, then check |deltaY| < 0x5000 → l_reverse_delta_z
        // (no goal — bar deflect), else cseg_7C911 (regular goal).
        Memory.WriteWord(Memory.Addr.goalTypeScored, 1);  // from ball.cpp:1297
        D4 = Memory.ReadSignedWord(Memory.Addr.currentGameTick);  // from ball.cpp:1298
        D4 = (short)(D4 & 31);     // from ball.cpp:1301
        D4 = (short)(D4 << 4);     // from ball.cpp:1305
        D4 = (short)(D4 - 256);    // from ball.cpp:1310
        {
            int dy = BallSprite.DeltaY;  // ball.cpp:1316
            // ball.cpp:1318 — `cmp deltaY, -5000h` → jl cseg_7C911.
            // Dword compare: signed.
            if (dy < -20480)  // from ball.cpp:1318
                goto cseg_7C911;
            // ball.cpp:1331 — `cmp deltaY, 5000h` → jg cseg_7C911.
            if (dy > 20480)  // from ball.cpp:1331
                goto cseg_7C911;
        }
        goto l_reverse_delta_z;  // ball.cpp:1341

    l_own_goal:
        // ball.cpp:1343-1406 — set goalTypeScored = 2 (GT_OWN_GOAL), same D4
        // jitter, then check |deltaY| < 0x5000 → cseg_7C911, else apply goal
        // X-offset to ball.destY (reverseDestY and accumulate D4 into destY).
        Memory.WriteWord(Memory.Addr.goalTypeScored, 2);  // from ball.cpp:1344
        D4 = Memory.ReadSignedWord(Memory.Addr.currentGameTick);  // from ball.cpp:1345
        D4 = (short)(D4 & 31);     // from ball.cpp:1348
        D4 = (short)(D4 << 4);     // from ball.cpp:1352
        D4 = (short)(D4 - 256);    // from ball.cpp:1357
        {
            int dy = BallSprite.DeltaY;
            if (dy < -20480)  // from ball.cpp:1365
                goto cseg_7C911;
            if (dy > 20480)   // from ball.cpp:1378
                goto cseg_7C911;
        }
        // ball.cpp:1388-1406 — push D4, reverseY, pop D4, reset spins,
        // ball.destY += D4.
        ReverseDestYDirection();        // ball.cpp:1389
        ResetBothTeamSpinTimers();      // ball.cpp:1391
        BallSprite.DestY = (short)(BallSprite.DestY + D4);  // from ball.cpp:1394-1405
        goto cseg_7C989;  // ball.cpp:1406

    cseg_7C911:
        // ball.cpp:1408-1430 — split path on D2 vs 449 (midfield Y).
        // If D2 > 449 (lower half), check deltaY non-negative → cseg_7CA2C (bar deflect, no event).
        // Else (upper half), check deltaY negative → cseg_7CA2C.
        Flags.SetFromSub16(D2, 449);  // from ball.cpp:1411
        if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            goto cseg_7C92F;

        // upper-half branch
        {
            int dy = BallSprite.DeltaY;  // ball.cpp:1422
            // ball.cpp:1423-1428 — `or eax, eax / jns cseg_7CA2C` (non-negative skip).
            Flags.Carry = false;
            Flags.Overflow = false;
            Flags.Sign = dy < 0;
            Flags.Zero = dy == 0;
            if (!Flags.Sign)
                goto cseg_7CA2C;
        }
        goto cseg_7C940;  // ball.cpp:1430

    cseg_7C92F:
        // lower-half branch — symmetric: skip when deltaY negative.
        {
            int dy = BallSprite.DeltaY;  // ball.cpp:1434
            Flags.Carry = false;
            Flags.Overflow = false;
            Flags.Sign = dy < 0;
            Flags.Zero = dy == 0;
            if (Flags.Sign)
                goto cseg_7CA2C;
        }
        // fall through to cseg_7C940

    cseg_7C940:
        // ball.cpp:1442-1461 — apply X-offset to ball.destX (post bounce).
        // Same shape as l_own_goal's destY accumulation but on destX.
        ReverseDestYDirection();        // ball.cpp:1444
        ResetBothTeamSpinTimers();      // ball.cpp:1446
        BallSprite.DestX = (short)(BallSprite.DestX + D4);  // from ball.cpp:1450-1460
        goto cseg_7C989;  // ball.cpp:1461

    l_reverse_delta_z:
        // ball.cpp:1463-1481 — bar deflect on penalty: deltaZ = -deltaZ, Z = preZ,
        // D6 += 0x10000 (rollback Y but 1 pixel further; "useless" preserved).
        BallSprite.DeltaZ = -BallSprite.DeltaZ;  // from ball.cpp:1466-1468
        BallSprite.Z = D7;  // from ball.cpp:1470-1471
        D6 = D6 + 65536;    // from ball.cpp:1474 (preserved even though only used in this scope)
        // fall through to cseg_7C989

    cseg_7C989:
        // ball.cpp:1483-1526 — pick an audio sample based on random bit of D0.
        // Stubbed audio calls; the goalTypeScored reset is real.
        {
            int rand = SwosRand();  // from ball.cpp:1484
            // ball.cpp:1485-1493 — `test D0, 1 / jz @@play_frame_hit_comment`.
            if ((rand & 1) == 0)
                goto l_play_frame_hit_comment;

            // ball.cpp:1495-1506 — `cmp goalTypeScored, GT_OWN_GOAL / jnz @@play_bar_hit_comment`.
            int gts = Memory.ReadSignedWord(Memory.Addr.goalTypeScored);
            Flags.SetFromSub16(gts, 2);  // from ball.cpp:1498
            if (!Flags.Zero)
                goto l_play_bar_hit_comment;

            // ball.cpp:1508-1511 — own-goal random branch: play post-hit, then miss-goal.
            StubPlayPostHitComment();  // from ball.cpp:1509
            goto l_play_miss_goal_sample;
        }

    l_play_bar_hit_comment:
        // ball.cpp:1513-1517.
        StubPlayBarHitComment();  // from ball.cpp:1515
        goto l_play_miss_goal_sample;

    l_play_frame_hit_comment:
        // ball.cpp:1519-1522 — bar hit OR post hit (depending on goalTypeScored
        // path). Under the asm: this is the "carry-bit-0 even" branch which also
        // calls PlayPostHitComment (different from l_play_bar_hit_comment).
        StubPlayPostHitComment();  // from ball.cpp:1521
        // fall through to l_play_miss_goal_sample

    l_play_miss_goal_sample:
        // ball.cpp:1524-1546 — common audio + speed-shrink + position-rollback.
        StubPlayMissGoalSample();  // ball.cpp:1525
        Memory.WriteWord(Memory.Addr.goalTypeScored, 0);  // from ball.cpp:1526
        {
            int speed = BallSprite.Speed;          // ball.cpp:1528
            int sub = (ushort)speed >> 2;          // from ball.cpp:1531
            BallSprite.Speed = (short)(speed - sub);  // from ball.cpp:1539
        }
        BallSprite.X = D5;  // from ball.cpp:1543-1544
        BallSprite.Y = D6;  // from ball.cpp:1545-1546
        // fall through

    cseg_7CA2C:
        // ball.cpp:1548-1591 — out-of-play check. ONLY run when game in progress
        // AND ball outside the legal pitch X/Y band. Calls checkIfBallOutOfPlay
        // (stubbed) which produces throw-in / corner / goal-out events.
        {
            short gsPl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            Flags.SetFromSub16(gsPl, 100);  // from ball.cpp:1552
            if (!Flags.Zero)
                goto l_update_ball_shadow;
        }

        // ball.cpp:1562-1574 — `cmp x, 81 / jge cseg_7CAA5`.
        {
            short x = BallSprite.XPixels;
            Flags.SetFromSub16(x, 81);  // from ball.cpp:1566
            if (Flags.Sign != Flags.Overflow)
            {
                // Out left side.
                CheckIfBallOutOfPlay();  // ball.cpp:1583
                goto l_update_ball_shadow;
            }
        }

    // (cseg_7CAA5)
        // ball.cpp:1593-1606 — `cmp x, 590 / jle cseg_7CB11`.
        {
            short x = BallSprite.XPixels;
            Flags.SetFromSub16(x, 590);  // from ball.cpp:1598
            if (!Flags.Zero && Flags.Sign == Flags.Overflow)
            {
                // Out right side.
                CheckIfBallOutOfPlay();  // ball.cpp:1615
                goto l_update_ball_shadow;
            }
        }

    // (cseg_7CB11)
        // ball.cpp:1625-1638 — `cmp y, 129 / jge cseg_7CB7A`.
        {
            short y = BallSprite.YPixels;
            Flags.SetFromSub16(y, 129);  // from ball.cpp:1630
            if (Flags.Sign != Flags.Overflow)
            {
                // Out top.
                CheckIfBallOutOfPlay();  // ball.cpp:1647
                goto l_update_ball_shadow;
            }
        }

    // (cseg_7CB7A)
        // ball.cpp:1657-1670 — `cmp y, 769 / jle @@update_ball_shadow`.
        {
            short y = BallSprite.YPixels;
            Flags.SetFromSub16(y, 769);  // from ball.cpp:1662
            if (Flags.Zero || Flags.Sign != Flags.Overflow)
                goto l_update_ball_shadow;
        }

        // ball.cpp:1672-1686 — out bottom.
        CheckIfBallOutOfPlay();  // ball.cpp:1679

    l_update_ball_shadow:
        // ball.cpp:1688-1754 — ball-shadow sprite is positioned at:
        //   shadow.x = ball.x + z/2 + 1
        //   shadow.y = ball.y + z/4 + 1 - 10
        //   shadow.z = -10
        // The shadow image moves to the side of the ball as it rises.
        {
            int shadowBase = Memory.Addr.ballShadowSpriteBase;  // from ball.cpp:1689 (offset 329100)
            int z = (short)BallSprite.ZPixels;       // from ball.cpp:1691
            int zHalf = (ushort)z >> 1;              // from ball.cpp:1694 (logical shift)
            int xBall = BallSprite.XPixels;          // from ball.cpp:1697

            // shadow.x = xBall + zHalf + 1
            Memory.WriteWord(shadowBase + 32, xBall);            // from ball.cpp:1699
            int shadowX = xBall + zHalf;                          // from ball.cpp:1704
            shadowX = shadowX + 1;                                // from ball.cpp:1712
            Memory.WriteWord(shadowBase + 32, (short)shadowX);

            // shadow.y = yBall + (zHalf >> 1) + 1 - 10
            int zQuarter = (ushort)zHalf >> 1;        // from ball.cpp:1718
            int yBall = BallSprite.YPixels;           // from ball.cpp:1722
            Memory.WriteWord(shadowBase + 36, yBall); // from ball.cpp:1724
            int shadowY = yBall + zQuarter;           // from ball.cpp:1729
            shadowY = shadowY + 1;                    // from ball.cpp:1737
            shadowY = shadowY - 10;                   // from ball.cpp:1745
            Memory.WriteWord(shadowBase + 36, (short)shadowY);

            // shadow.z = -10
            Memory.WriteWord(shadowBase + 40, -10);   // from ball.cpp:1754
        }

        // ball.cpp:1755 — calculateNextBallPosition writes ballNextX/Y.
        CalculateNextBallPosition();

        // ball.cpp:1756-1767 — `cmp gameStatePl, 100 / jz @@game_in_progress`.
        {
            short gsPl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
            Flags.SetFromSub16(gsPl, 100);  // from ball.cpp:1759
            if (Flags.Zero)
                goto l_game_in_progress;
        }

        // ball.cpp:1769-1772 — gameStatePl != 100 (break state): use foul XY.
        D1 = Memory.ReadSignedWord(Memory.Addr.foulXCoordinate);  // from ball.cpp:1769
        D2 = Memory.ReadSignedWord(Memory.Addr.foulYCoordinate);  // from ball.cpp:1771

        // ball.cpp:1773-1784 — `cmp gameState, 3 / jz @@keepers_ball_or_goal_out`.
        {
            short gs = Memory.ReadSignedWord(Memory.Addr.gameState);
            Flags.SetFromSub16(gs, 3);  // from ball.cpp:1776
            if (Flags.Zero)
                goto l_keepers_ball_or_goal_out;
        }

        // ball.cpp:1786-1797 — `cmp gameState, 1 / jz @@keepers_ball_or_goal_out`.
        {
            short gs = Memory.ReadSignedWord(Memory.Addr.gameState);
            Flags.SetFromSub16(gs, 1);  // from ball.cpp:1789
            if (Flags.Zero)
                goto l_keepers_ball_or_goal_out;
        }

        // ball.cpp:1799-1810 — `cmp gameState, 2 / jnz @@calc_x_ball_quadrant`.
        {
            short gs = Memory.ReadSignedWord(Memory.Addr.gameState);
            Flags.SetFromSub16(gs, 2);  // from ball.cpp:1802
            if (!Flags.Zero)
                goto l_calc_x_ball_quadrant;
        }
        // fall through to l_keepers_ball_or_goal_out

    l_keepers_ball_or_goal_out:
        // ball.cpp:1812-1815 — pin to center kick-off position.
        D1 = 336;  // from ball.cpp:1813
        D2 = 449;  // from ball.cpp:1814
        goto l_calc_x_ball_quadrant;

    l_game_in_progress:
        // ball.cpp:1817-1821 — use ballNextX/Y (calculated above).
        D1 = Memory.ReadSignedWord(Memory.Addr.ballNextX);  // from ball.cpp:1818
        D2 = Memory.ReadSignedWord(Memory.Addr.ballNextY);  // from ball.cpp:1820
        // fall through

    l_calc_x_ball_quadrant:
        // ball.cpp:1823-1962 — walk ballXQuadrantLimits[+2..end] comparing D1
        // against each. Stop at first entry > D1; D0 holds count = quadrant idx
        // (0..4), D3 += 1 per step (X portion of quadrantIndex).
        // A1 walks the table; first compare uses entry[0] of the +2 slice.
        D3 = 0;  // ball.cpp:1824
        int D0 = 0;  // ball.cpp:1825
        int A1 = Memory.Addr.ballXQuadrantLimits + 2;  // ball.cpp:1826
        // ball.cpp:1828-1845 — first compare. table[A1], A1 += 2, cmp D1 to ax.
        // If carry (D1 < ax) → goto set_y_quadrant.
        for (int i = 0; i < 4; i++)  // 4 compares max (5 entries, +2 skip = 4)
        {
            int ax = (short)Memory.ReadSignedWord(A1);  // ball.cpp:1828, 1860, 1892, 1924
            A1 += 2;                                     // ball.cpp:1831, 1865, 1897, 1929
            Flags.SetFromSub16(D1, ax);  // from ball.cpp:1837
            if (Flags.Carry)
                goto l_calc_y_ball_quadrant;
            D0 = (short)(D0 + 1);  // from ball.cpp:1849
            D3 = (short)(D3 + 1);  // from ball.cpp:1854
        }
        // ball.cpp:1955-1962 — fall-through bump for "all compares passed".
        A1 = A1 + 2;

    l_calc_y_ball_quadrant:
        // ball.cpp:1964-1998 — finalise X quadrant outputs.
        Memory.WriteWord(Memory.Addr.ballXQuadrantDead, D0);  // from ball.cpp:1966
        {
            // ball.cpp:1968 — `mov ax, [esi-4]` (previous quadrant lower edge).
            int ax = (short)Memory.ReadSignedWord(A1 - 4);
            // ball.cpp:1970-1974 — D1 -= ax.
            D1 = (short)(D1 - ax);
            // ball.cpp:1976-1980 — D1 -= 51 (half-quadrant offset).
            D1 = (short)(D1 - 51);
            // ball.cpp:1983-1995 — D1 = D1 * 5 / 15 (signed).
            int prod = (short)D1 * 5;
            int quot = prod / 15;
            D1 = (short)quot;
            Memory.WriteWord(Memory.Addr.playerXQuadrantOffset, D1);  // from ball.cpp:1998
        }

        // ball.cpp:1999-2001 — start Y walk.
        D0 = 0;  // ball.cpp:1999
        A1 = Memory.Addr.ballYQuadrantLimits + 2;  // ball.cpp:2000
        // ball.cpp:2002-2200 — same shape as X walk, 6 compares (7 entries, +2 skip = 6).
        // Each successful step bumps D0 += 1, D3 += 5 (note: D3 step is 5 here vs
        // 1 in the X walk — this is the "row × 5 columns" packing of ballQuadrantIndex).
        for (int i = 0; i < 6; i++)
        {
            int ax = (short)Memory.ReadSignedWord(A1);
            A1 += 2;
            Flags.SetFromSub16(D2, ax);  // from ball.cpp:2012
            if (Flags.Carry)
                goto l_set_y_quadrant;
            D0 = (short)(D0 + 1);  // from ball.cpp:2024
            D3 = (short)(D3 + 5);  // from ball.cpp:2029 (5 per row)
        }
        A1 = A1 + 2;  // ball.cpp:2193-2200

    l_set_y_quadrant:
        // ball.cpp:2202-2238 — finalise Y quadrant + write ballQuadrantIndex.
        Memory.WriteWord(Memory.Addr.ballYQuadrantDead, D0);  // from ball.cpp:2204
        {
            int ax = (short)Memory.ReadSignedWord(A1 - 4);  // ball.cpp:2206
            D2 = (short)(D2 - ax);
            D2 = (short)(D2 - 45);  // from ball.cpp:2215 (Y half-quadrant offset)
            int prod = (short)D2 * 5;
            int quot = prod / 15;
            D2 = (short)quot;
            Memory.WriteWord(Memory.Addr.playerYQuadrantOffset, D2);  // from ball.cpp:2236
        }
        Memory.WriteWord(Memory.Addr.ballQuadrantIndex, D3);  // from ball.cpp:2238

        // (end of updateBall)
    }

    // ---- Stubbed external callees -------------------------------------------
    // These are wired into Section 4 above. Empty bodies — pipeline runs but
    // the gameplay effect (audio cue, out-of-play event) is silently skipped
    // until a real port lands.

    // SWOS::Rand — table-driven xor-stream PRNG. ball.cpp:1484 uses the low
    // bit for a 50/50 audio-comment branch. Returns a byte (0..255) — the
    // asm caller only inspects `test D0, 1`. Wired through SwosVm.Rng — the
    // canonical deterministic-lockstep stream-1 source (matches
    // PlayerActions.SwosRand and BallOutOfPlay.SwosRand).
    // Source: external/swos-port/src/util/random.cpp
    private static int SwosRand() => Rng.NextByte();

    // ball.cpp:1521 — fallback comment when ball hit goal frame.
    // TODO from external/swos-port/src/sound/commentary.cpp
    private static void StubPlayPostHitComment() => OpenSwos.Audio.MatchAudio.PostHitComment();

    // ball.cpp:1515 — comment when ball hit crossbar.
    private static void StubPlayBarHitComment() => OpenSwos.Audio.MatchAudio.BarHitComment();

    // ball.cpp:1525 — generic "miss goal" sound effect.
    private static void StubPlayMissGoalSample() => OpenSwos.Audio.MatchAudio.PlayMissGoal();

    // ball.cpp:1583/1615/1647/1679 — produces throw-in / corner / goal-out events.
    // Fully ported in BallOutOfPlay.cs (ball.cpp:3007-4020 port). This local
    // alias keeps the four call sites within Section 4 readable; remove if a
    // future refactor inlines BallOutOfPlay.CheckIfBallOutOfPlay() at the
    // call sites directly.
    private static void CheckIfBallOutOfPlay() => BallOutOfPlay.CheckIfBallOutOfPlay();

    // ball.cpp:180-298 — combined: direction recompute (from destX/destY +
    // speed) AND friction reduction.
    //
    // Step 1 (180-239) — `l_calculate_deltas`:
    //   - Read ball position + destX/destY + speed
    //   - Call CalculateDeltaXAndY → produces direction (0..255 or -1),
    //     deltaX, deltaY (both Q16.16)
    //   - Write back: ball.deltaX = result.deltaX, ball.deltaY = result.deltaY
    //   - If direction valid (>= 0): write ball.fullDirection = direction,
    //     quantize to 8-direction (0..7) via `((dir+16) & 0xFF) >> 5`,
    //     write ball.direction
    //   - If direction == -1 (no motion): skip fullDirection write, write
    //     -1 (or whatever) to ball.direction
    //
    // Step 2 (240-297) — friction:
    //   - Base friction = kBallGroundConstant (13 PC / 16 Amiga)
    //   - If NEITHER team has the ball (both playerHasBall == 0):
    //       friction += pitchBallSpeedFactor (varies by pitch type)
    //     This is the "muddy pitch slows free ball more" effect.
    //   - If ball.z != 0 (in air): friction = kBallAirConstant (4 PC / 10 Amiga)
    //     (overrides ground friction — note: pitch factor only matters on ground)
    //   - speed -= friction; clamp to 0 if negative
    private static void Section2_DirectionAndFriction()
    {
        // ---- Step 1: direction + delta recompute (ball.cpp:180-239) ----------
        // Inputs: ball.x.whole (offset +32), ball.y.whole (offset +36),
        //         ball.destX, destY, ball.speed
        int x = Memory.ReadSignedWord(BallSprite.Base + 32);  // whole pixels
        int y = Memory.ReadSignedWord(BallSprite.Base + 36);
        int destX = BallSprite.DestX;
        int destY = BallSprite.DestY;
        int speed = BallSprite.Speed;

        var result = SpriteUpdate.CalculateDeltaXAndY(speed, x, y, destX, destY);

        // ball.cpp:207-211 — always write deltaX, deltaY (even if direction -1,
        // the result would be 0 anyway).
        BallSprite.DeltaX = result.DeltaX;
        BallSprite.DeltaY = result.DeltaY;

        if (result.Direction >= 0)
        {
            // ball.cpp:220-234 — fullDirection + 8-dir quantization.
            BallSprite.FullDirection = (short)result.Direction;
            int dir8 = ((result.Direction + 16) & 0xff) >> 5;
            BallSprite.Direction = (short)dir8;
        }
        else
        {
            // ball.cpp:217-218, 236-239 — angle == -1: skip fullDirection,
            // write -1 to direction (matches asm where D0 still holds -1).
            BallSprite.Direction = -1;
        }

        // ---- Step 2: friction (ball.cpp:240-297) -----------------------------
        // ball.cpp:240 — ax = kBallGroundConstant (initial value).
        int friction = Memory.ReadSignedWord(Memory.Addr.kBallGroundConstant);

        // ball.cpp:242-256 — if either team has the ball, skip pitch factor.
        // Otherwise (free ball): add pitchBallSpeedFactor.
        bool topHasBall = TeamData.PlayerHasBall(true) != 0;
        bool botHasBall = TeamData.PlayerHasBall(false) != 0;
        if (!topHasBall && !botHasBall)
        {
            // ball.cpp:258-264 — free ball, modulate by pitch type.
            short pitchFactor = Memory.ReadSignedWord(Memory.Addr.pitchBallSpeedFactor);
            friction += pitchFactor;
        }

        // ball.cpp:266-277 — if ball in air (z.high != 0), override with air constant.
        // NOTE: ball.cpp reads `word ptr [esi+(Sprite.z+2)]` which is the WHOLE
        // PIXEL part of z. Z = 0 means on ground.
        short zWhole = BallSprite.ZPixels;
        if (zWhole != 0)
        {
            friction = Memory.ReadSignedWord(Memory.Addr.kBallAirConstant);
        }

        // ball.cpp:279-297 — apply friction to speed, clamp to 0.
        int newSpeed = speed - friction;
        if (newSpeed < 0) newSpeed = 0;
        BallSprite.Speed = (short)newSpeed;
    }

    // ball.cpp:13-178 — handle hideBall flag, pick moving vs static frame
    // table, advance frame animation based on speed.
    //
    // Step 1: if hideBall != 0, set ball + shadow imageIndex to -1 and jump
    // straight to the delta-calculation section (skip animation entirely).
    // Step 2: pick frameIndicesTable — moving if deltaX/deltaY non-zero,
    // static otherwise. Reset frameIndex to 0 when switching.
    // Step 3: pace animation timer based on speed (speed/512 + 1 per tick).
    // When timer underflows, advance frameIndex and look up the next sprite.
    private static void Section1_HideAndFrameIndex()
    {
        // ball.cpp:15-28 — hideBall path.
        ushort hideBall = Memory.ReadWord(Memory.Addr.hideBall);
        if (hideBall != 0)
        {
            BallSprite.ImageIndex = -1;
            Memory.WriteWord(Memory.Addr.ballShadowImageIndex, -1);
            return;  // jmp @@calculate_deltas — skip rest of section 1
        }

        // ball.cpp:30 — show shadow.
        Memory.WriteWord(Memory.Addr.ballShadowImageIndex, 1183);

        // ball.cpp:32-46, 65-81 — pick moving vs static frame table.
        // The "ball moving" check is deltaX != 0 OR deltaY != 0.
        int deltaX = BallSprite.DeltaX;
        int deltaY = BallSprite.DeltaY;
        bool isMoving = deltaX != 0 || deltaY != 0;

        int desiredTable = isMoving
            ? Memory.Addr.ballMovingFrameIndices_Table
            : Memory.Addr.ballStaticFrameIndices_Table;
        int currentTable = BallSprite.FrameIndicesTable;

        if (currentTable != desiredTable)
        {
            BallSprite.FrameIndicesTable = desiredTable;
            BallSprite.FrameIndex = 0;
        }

        // ball.cpp:83-138 — animation pacing.
        short speed = BallSprite.Speed;
        bool advanceFrame = false;

        if (speed != 0)
        {
            // ball.cpp:94-107 — tickContrib = (speed >> 8) >> 1 + 1
            //                           = (speed / 256) / 2 + 1
            //                           = speed / 512 + 1
            int tickContrib = ((speed >> 8) >> 1) + 1;
            int timer = BallSprite.CycleFramesTimer - tickContrib;
            BallSprite.CycleFramesTimer = (short)timer;

            // ball.cpp:121-138 — if timer went negative, advance frame.
            // `jns short @@check_if_picture_set` — sign clear (>= 0) skips.
            if (timer < 0)
            {
                BallSprite.FrameIndex = (short)(BallSprite.FrameIndex + 1);
                BallSprite.CycleFramesTimer = BallSprite.FrameDelay;
                advanceFrame = true;  // fall through to extract_frame
            }
        }

        // ball.cpp:140-148 — `check_if_picture_set`: if imageIndex is already
        // valid (>= 0), skip frame lookup.
        if (!advanceFrame)
        {
            short imageIndex = BallSprite.ImageIndex;
            if (imageIndex >= 0) return;  // jmp @@calculate_deltas
            // else fall through to extract_frame
        }

        // ball.cpp:150-178 — `extract_frame` loop. Read frame value from
        // table[frameIndex * 2]. If negative (sentinel -1), reset frameIndex
        // to 0 and retry. Otherwise, store as imageIndex.
        ExtractFrameLoop();
    }

    // ball.cpp:150-178 — frame table walker. Reads table[frameIndex].
    // -1 sentinel = wrap to start of table (loop animation).
    // Positive value = the sprite image index to display.
    private static void ExtractFrameLoop()
    {
        int tableBase = BallSprite.FrameIndicesTable;
        while (true)
        {
            short idx = BallSprite.FrameIndex;
            short frame = Memory.ReadSignedWord(tableBase + idx * 2);

            if (frame >= 0)
            {
                // ball.cpp:175-178 — set imageIndex and exit.
                BallSprite.ImageIndex = frame;
                return;
            }

            // ball.cpp:171-173 — wrap (-1 = loop marker).
            BallSprite.FrameIndex = 0;
            // continue loop
        }
    }


    // ball.cpp:4022-4026.
    // Resets per-team spin timers when ball changes possession or goes
    // out of play. -1 = "no spin active".
    public static void ResetBothTeamSpinTimers()
    {
        TeamData.SetSpinTimer(top: true, -1);
        TeamData.SetSpinTimer(top: false, -1);
    }

    // ball.cpp:4029-4042.
    // Sets ball position, clears velocity, drops to ground. Used by
    // kick-off / goal-out / restart code paths.
    public static void SetBallPosition(int x, int y)
    {
        BallSprite.Speed = 0;
        BallSprite.XPixels = (short)x;
        BallSprite.YPixels = (short)y;
        BallSprite.Z = 0;
        BallSprite.DestX = (short)x;
        BallSprite.DestY = (short)y;
        BallSprite.DeltaZ = 0;
    }

    // ball.cpp:4509-4541 (X) and 4551-4583 (Y).
    // Reflect destination around current ball position — used by goal-post
    // collision (post bounce). Semantically:
    //   newDest = 2 * ballWholePos - oldDest
    // Equivalent to: post = (ball + dest) / 2; ball "leaves" the post in the
    // mirror direction (dest moves to other side of ball at same distance).
    public static void ReverseDestXDirection()
    {
        int ballX = BallSprite.XPixels;  // whole pixel
        int destX = BallSprite.DestX;
        BallSprite.DestX = (short)(2 * ballX - destX);
    }

    public static void ReverseDestYDirection()
    {
        int ballY = BallSprite.YPixels;  // whole pixel
        int destY = BallSprite.DestY;
        BallSprite.DestY = (short)(2 * ballY - destY);
    }

    // ball.cpp:4206-4501.
    // Predicts where the ball will land (when z reaches 0) given current motion.
    // Result is written to Memory.Addr.ballNextX / ballNextY as WHOLE PIXELS.
    //
    // Algorithm: starting from current state, run a substep-based simulation
    // (x += deltaX, y += deltaY, deltaZ -= gravity, z += deltaZ) until z goes
    // negative. Number of substeps and step size depends on ball height:
    //
    //   z > 35     (or deltaZ > 0): step × 8 (z, deltas, gravity all scaled)
    //   z in (30, 35]:              step × 4
    //   z in (20, 30]:              step × 2
    //   z <= 20:                    step × 1
    //
    // The "scaling" multiplies deltaX/Y/gravity by N AND divides z by N so the
    // loop iterates with bigger jumps when ball is high — fast prediction
    // ahead. Used by keeper / AI to decide where to position themselves.
    //
    // If ball.speed == 0, the loop is skipped and current x/y are written.
    public static void CalculateNextBallPosition()
    {
        int x = BallSprite.X;            // Q16.16
        int y = BallSprite.Y;            // Q16.16
        int z = BallSprite.Z;            // Q16.16
        int speed = BallSprite.Speed;

        if (speed != 0)
        {
            int dx = BallSprite.DeltaX;  // Q16.16
            int dy = BallSprite.DeltaY;  // Q16.16
            int dz = BallSprite.DeltaZ;  // Q16.16
            int gravity = Memory.ReadSignedWord(Memory.Addr.kGravityConstant);

            // ball.cpp:4232-4276. Determine substep scaling.
            // jg @@ball_very_high — if deltaZ > 0 (ball rising), force 8× substeps.
            int substepShift = 0;
            if (dz > 0)
            {
                substepShift = 3;  // ball_very_high path
            }
            else
            {
                int zWhole = BallSprite.ZPixels;
                if (zWhole <= 20)        substepShift = 0;  // ball_low
                else if (zWhole <= 30)   substepShift = 1;  // ball_a_little_bit_high
                else if (zWhole <= 35)   substepShift = 2;  // ball_high
                else                     substepShift = 3;  // ball_very_high
            }

            if (substepShift > 0)
            {
                dx <<= substepShift;
                dy <<= substepShift;
                gravity <<= substepShift;
                z >>= substepShift;
            }

            // ball.cpp:4296-4330 (very high) / 4352-4386 (high) /
            // 4408-4442 (a_little_bit_high) / 4446-4480 (low) — same loop body.
            // Loop until z goes negative (ball has landed below ground).
            while (true)
            {
                x += dx;
                y += dy;
                dz -= gravity;
                z += dz;
                if (z < 0) break;
            }
        }

        // ball.cpp:4482-4500 — extract whole-pixel parts of x/y via xchg
        // (which effectively shifts high word into low word position).
        // Equivalent: take the WHOLE PIXEL part of Q16.16.
        short nextX = (short)(x >> 16);
        short nextY = (short)(y >> 16);
        Memory.WriteWord(Memory.Addr.ballNextX, nextX);
        Memory.WriteWord(Memory.Addr.ballNextY, nextY);
    }


    // ====================================================================
    // applyBallAfterTouch — ball.cpp:2248-3005
    // ====================================================================
    //
    // **Effect**: Implements ball spin (curl) and post-kick speed/height
    // adjustments. Called every tick by updatePlayers.cpp:198 for each team
    // while a kick is "still in flight" (spinTimer >= 0). For 10 ticks after a
    // touch, the controlling player's joystick direction relative to the kick
    // direction determines:
    //   - left/right spin → ball.destX/destY get perpendicular nudges per tick
    //   - at tick 4, pulling back triggers high-kick boost
    //   - at tick 4, perpendicular triggers normal-kick boost (deltaZ + speed)
    //   - holding into/away from kick direction also adjusts ball speed
    //
    // **Caller setup** (when porting kick handlers later):
    //   - When player kicks: set TeamData.SpinTimer = 0, controlledPlDirection
    //     = kick direction recorded AT kick time (0..7 quantised, -1 if no spin
    //     allowed e.g. headers/tackles), currentAllowedDirection = current LIVE
    //     held/joystick direction.
    //   - Each subsequent tick: update currentAllowedDirection from the joystick,
    //     call ApplyBallAfterTouch(topTeam_kicker).
    //   - Function self-terminates by setting spinTimer = -1 after 10 ticks.
    //
    // **NOTE on integration**: This port writes to `BallSprite.DestX/DestY`.
    // SWOS' motion model uses dest as the target position; deltaX/Y are
    // computed each tick from (dest - pos) normalised × speed in
    // calculateNextBallPosition (ball.cpp:4206). Wiring this into our DeltaX/Y
    // motion model requires either:
    //   (a) also porting calculateNextBallPosition, OR
    //   (b) reading post-call DestX/DestY shift and converting to deltaX/Y nudge.
    // Done as Phase B4 follow-up before user A/B test.
    public static void ApplyBallAfterTouch(bool topTeam)
    {
        // ball.cpp:2251 — if pass is active, take the passing branch instead.
        if (TeamData.PassInProgress(topTeam) != 0)
        {
            ApplyAfterPass(topTeam);
            return;
        }

        // ball.cpp:2258-2266 — opponent keeper recently saved → kill spin tracking
        // (avoids spin transfer after a clean catch + release pattern).
        bool opponent = !topTeam;
        if (TeamData.GoalkeeperSavedCommentTimer(opponent) < 0)
        {
            ResetSpinTimer(topTeam);
            return;
        }

        // ball.cpp:2268-2290 — if game isn't strictly in progress (gameStatePl != 100)
        // AND keeper holds ball (gameState == 3), the spin handler should reset and exit.
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);
        const short ST_GAME_IN_PROGRESS = 100;
        const short ST_KEEPER_HOLDS_BALL = 3;
        if (gameStatePl != ST_GAME_IN_PROGRESS && gameState == ST_KEEPER_HOLDS_BALL)
        {
            ResetSpinTimer(topTeam);
            return;
        }

        // ball.cpp:2294-2306 — spinTimer < 0 = no spin active; spinTimer == 0 =
        // first tick after touch, reset spin flags.
        short spinTimer = TeamData.GetSpinTimer(topTeam);
        if (spinTimer < 0) return;
        if (spinTimer == 0)
        {
            TeamData.SetLeftSpin(topTeam, 0);
            TeamData.SetRightSpin(topTeam, 0);
        }

        // ball.cpp:2308-2378 — determine spin "side" (0 = left, 4 = right table offset)
        int spinSide = DetermineKickSpinSide(topTeam);

        // ball.cpp:2379-2440 — apply spin offsets to ball destX/destY each tick.
        if (spinSide >= 0)
        {
            ApplySpinOffsetToDest(topTeam, spinSide, Memory.Addr.kKickSpinFactor);
        }

        // ball.cpp:2442-2614 — at spinTimer == 4, joystick direction can trigger
        // high-kick or normal-kick boost (deltaZ + speed), plus speed adjustment
        // based on joystick alignment with kick direction.
        if (spinTimer == 4)
        {
            ApplyTick4HighOrNormalKickBoost(topTeam);
        }

        // ball.cpp:2635-2659 — increment spin timer; reset to -1 after 10 ticks.
        IncrementSpinTimerOrReset(topTeam);
    }

    // ball.cpp:2664-3004 — passing-side branch of applyBallAfterTouch.
    // Same skeleton as the kick branch but uses kPassingSpinFactor (smaller
    // values), no high-kick boost, plus longPass / longSpinPass tracking.
    private static void ApplyAfterPass(bool topTeam)
    {
        // ball.cpp:2666-2698 — opponent keeper save / game-state gating (same as kick branch).
        bool opponent = !topTeam;
        if (TeamData.GoalkeeperSavedCommentTimer(opponent) < 0)
        {
            ResetSpinTimer(topTeam);
            return;
        }

        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);
        if (gameStatePl != 100 && gameState == 3)
        {
            ResetSpinTimer(topTeam);
            return;
        }

        // ball.cpp:2700-2716 — spinTimer gate. On tick 0, also reset longPass / longSpinPass.
        short spinTimer = TeamData.GetSpinTimer(topTeam);
        if (spinTimer < 0) return;
        if (spinTimer == 0)
        {
            TeamData.SetLeftSpin(topTeam, 0);
            TeamData.SetRightSpin(topTeam, 0);
            TeamData.SetLongPass(topTeam, 0);
            TeamData.SetLongSpinPass(topTeam, 0);
        }

        // ball.cpp:2718-2850 — pass spin side determination + offset application.
        // Identical structure to kick branch but indexed off ballSprite.direction
        // (not controlledPlDirection) AND uses kPassingSpinFactor.
        int spinSide = DeterminePassSpinSide(topTeam);
        if (spinSide >= 0)
        {
            ApplySpinOffsetToDest(topTeam, spinSide, Memory.Addr.kPassingSpinFactor,
                useBallDirection: true);
        }

        // ball.cpp:2852-2975 — longPass / longSpinPass speed boost.
        // If longPass was already active (>0), skip to timer update.
        // Else, if joystick direction relative to kick direction matches
        // a "long pass" pattern, set longPass / longSpinPass flag and boost
        // speed by 1/8.
        if (TeamData.LongPass(topTeam) == 0)
        {
            ApplyLongPassBoostIfNeeded(topTeam);
        }

        // ball.cpp:2980-3004 — bump spin timer, reset at 10.
        IncrementSpinTimerOrReset(topTeam);
    }

    // ----- helpers ------------------------------------------------------------

    // Resets spinTimer to -1 (no spin active). Used when opponent keeper saved
    // or keeper holds the ball.
    private static void ResetSpinTimer(bool topTeam)
    {
        TeamData.SetSpinTimer(topTeam, -1);
    }

    // ball.cpp:2308-2378 (kick) and 2718-2787 (pass) share this logic with a
    // single key difference: for KICK the "reference direction" is
    // currentAllowedDirection (the kick direction at impact); for PASS it's
    // ballSprite.direction (current ball direction).
    //
    // Returns:
    //   0  → left spin (table offset 0 within direction bucket)
    //   4  → right spin (table offset 4 within direction bucket)
    //   -1 → no spin this tick (skip offset application)
    private static int DetermineKickSpinSide(bool topTeam)
    {
        // Already-spinning shortcut.
        if (TeamData.LeftSpin(topTeam) != 0)  return 0;
        if (TeamData.RightSpin(topTeam) != 0) return 4;

        int allowedDir = TeamData.CurrentAllowedDirection(topTeam);
        if (allowedDir < 0) return -1;  // kick that disallows spin (header etc.)

        int ctrlDir = TeamData.ControlledPlDirection(topTeam);
        int delta = ctrlDir - allowedDir;
        if (delta == 0) return -1;  // joystick aligned with kick — no spin

        // ball.cpp:2349 `and word ptr D0, 7` (8-direction modulo).
        int dWrapped = delta & 7;
        // ball.cpp:2360 `cmp D0, 4` → if equal, opposite direction → no spin.
        if (dWrapped == 4) return -1;

        // ball.cpp:2363 `if (flags.carry) goto spin_left;` — carry is set when
        // dWrapped < 4 (unsigned compare). So {1,2,3} → spin LEFT (table offset 0),
        // {5,6,7} → spin RIGHT (table offset 4).
        //
        // NOTE: this is the OPPOSITE of what one might guess from the label names
        // (`spin_left` writes leftSpin=1 → spinSide returns 0). The mapping is:
        //   leftSpin = 1 means table-offset 0; rightSpin = 1 means table-offset 4.
        if (dWrapped < 4)
        {
            TeamData.SetLeftSpin(topTeam, 1);
            return 0;
        }
        else
        {
            TeamData.SetRightSpin(topTeam, 1);
            return 4;
        }
    }

    // ball.cpp:2718-2787 — pass variant uses ballSprite.direction as reference
    // instead of currentAllowedDirection. Same delta-quantisation logic
    // otherwise. Reads /writes leftSpin / rightSpin same way.
    private static int DeterminePassSpinSide(bool topTeam)
    {
        if (TeamData.LeftSpin(topTeam) != 0)  return 0;
        if (TeamData.RightSpin(topTeam) != 0) return 4;

        // For passes, swos-port uses currentAllowedDirection same as kicks.
        // ball.cpp:2735 `readMemory(esi + 44, 2)` — also currentAllowedDirection.
        int allowedDir = TeamData.CurrentAllowedDirection(topTeam);
        if (allowedDir < 0) return -1;

        int ctrlDir = TeamData.ControlledPlDirection(topTeam);
        int delta = ctrlDir - allowedDir;
        if (delta == 0) return -1;

        int dWrapped = delta & 7;
        if (dWrapped == 4) return -1;

        if (dWrapped < 4)
        {
            TeamData.SetLeftSpin(topTeam, 1);
            return 0;
        }
        else
        {
            TeamData.SetRightSpin(topTeam, 1);
            return 4;
        }
    }

    // ball.cpp:2379-2440 (kick), 2789-2850 (pass).
    // Looks up spin delta from per-direction table, multiplies by spinMultiplier,
    // adds to ballSprite.destX / destY. Spin offsets are TINY pixel-scale values
    // (e.g., 32 for kick, 16 for pass). spinMultiplier {5,4,3,2,2,2,2,1,1,1}
    // makes curve strongest right after touch and decays.
    //
    // spinSide: 0 = left (table bytes 0..3 within bucket), 4 = right (bytes 4..7).
    // useBallDirection: false → use controlledPlDirection (kick branch).
    //                    true  → use ballSprite.direction (pass branch).
    private static void ApplySpinOffsetToDest(bool topTeam, int spinSide,
        int spinFactorTableBase, bool useBallDirection = false)
    {
        int refDir = useBallDirection
            ? BallSprite.Direction
            : TeamData.ControlledPlDirection(topTeam);

        // ball.cpp:2385 `shl word ptr D0, 3` — direction * 8 (8 bytes / direction).
        int byteOffset = refDir * 8 + spinSide;

        // ball.cpp:2396-2404 — kSpinMultiplierFactor[spinTimer * 2].
        short spinTimer = TeamData.GetSpinTimer(topTeam);
        int spinMultiplier = Memory.ReadSignedWord(
            Memory.Addr.kSpinMultiplierFactor + spinTimer * 2);

        // ball.cpp:2406-2413 — load deltaX, multiply by spinMultiplier (low 16 bits).
        int spinDx = Memory.ReadSignedWord(spinFactorTableBase + byteOffset);
        int spinDy = Memory.ReadSignedWord(spinFactorTableBase + byteOffset + 2);

        // ball.cpp:2414-2422, 2432-2440 — add to destX / destY (16-bit wraparound).
        short xMul = (short)(spinDx * spinMultiplier);
        short yMul = (short)(spinDy * spinMultiplier);

        BallSprite.DestX = (short)(BallSprite.DestX + xMul);
        BallSprite.DestY = (short)(BallSprite.DestY + yMul);
    }

    // ball.cpp:2442-2614 — at spinTimer == 4, the joystick direction relative
    // to the kick direction can trigger a HIGH-KICK or NORMAL-KICK boost:
    //   - Aligned (delta == 0): no boost, just bump timer.
    //   - Perpendicular (delta == 2 or 6): NORMAL kick (modest deltaZ).
    //   - Mostly opposite (delta == 3, 4, 5): HIGH kick (big deltaZ).
    //   - Slight off (delta == 1 or 7): no boost.
    // After the boost is applied, ApplySpeedAdjustment further modulates speed
    // based on the joystick direction (cardinal vs diagonal).
    private static void ApplyTick4HighOrNormalKickBoost(bool topTeam)
    {
        int allowedDir = TeamData.CurrentAllowedDirection(topTeam);
        if (allowedDir < 0)
        {
            // ball.cpp:2461 `js short @@normal_not_high_kick` — sign bit means
            // forced normal kick (allowedDir was set to -1 by a non-spin kick type).
            ApplyNormalKick();
            ApplySpeedAdjustment(topTeam);
            return;
        }

        int ctrlDir = TeamData.ControlledPlDirection(topTeam);
        int delta = ctrlDir - allowedDir;
        if (delta == 0) return;  // ball.cpp:2476 — aligned, no change

        int dWrapped = delta & 7;
        if (dWrapped == 2 || dWrapped == 6)
        {
            // ball.cpp:2491, 2502 — perpendicular triggers normal kick.
            ApplyNormalKick();
            ApplySpeedAdjustment(topTeam);
            return;
        }

        // ball.cpp:2513 `cmp D0, 3 / jb @@jmp_increase_spin_timer` — D0 < 3 (i.e., 1)
        // → no change, just bump timer.
        if (dWrapped < 3) return;

        // ball.cpp:2523 `cmp D0, 5 / ja @@jmp_increase_spin_timer` — D0 > 5 (i.e., 7)
        // → no change.
        if (dWrapped > 5) return;

        // ball.cpp:2527-2532 — D0 ∈ {3, 4, 5} → HIGH kick boost.
        ApplyHighKick();
        ApplySpeedAdjustment(topTeam);
    }

    private static void ApplyHighKick()
    {
        // ball.cpp:2527-2531
        BallSprite.DeltaZ = Memory.ReadSignedDword(Memory.Addr.kHighKickDeltaZ);
        BallSprite.Speed = (short)Memory.ReadWord(Memory.Addr.kHighKickBallSpeed);
    }

    private static void ApplyNormalKick()
    {
        // ball.cpp:2535-2539
        BallSprite.DeltaZ = Memory.ReadSignedDword(Memory.Addr.kNormalKickDeltaZ);
        BallSprite.Speed = (short)Memory.ReadWord(Memory.Addr.kNormalKickBallSpeed);
    }

    // ball.cpp:2545-2613 — based on joystick direction:
    //   - 0 or 4 (aligned/opposite cardinal): speed *= 3/4
    //   - 2 or 6 (perpendicular cardinal):    no change
    //   - 1/3/5/7 (any diagonal):             speed *= 7/8
    // The 3/4 reduction reflects "pulling back from kick" damping; 7/8 reflects
    // diagonal joystick partially redirecting energy.
    private static void ApplySpeedAdjustment(bool topTeam)
    {
        int ctrlDir = TeamData.ControlledPlDirection(topTeam);
        int speed = BallSprite.Speed;

        if (ctrlDir == 0 || ctrlDir == 4)
        {
            // ball.cpp:2616-2633 `decrease_ball_speed_by_quarter`
            // D1 = -(speed >> 2) + speed = speed - speed/4 = 3/4 * speed
            int reduced = speed - (speed >> 2);
            BallSprite.Speed = (short)reduced;
        }
        else if ((ctrlDir & 1) != 0)
        {
            // ball.cpp:2580-2613 — diagonal path.
            // D1 = speed - speed/4 (3/4)
            // D2 = speed/8
            // speed = D1 + D2 = 3/4 + 1/8 = 7/8 * speed
            int q3_4 = speed - (speed >> 2);
            int q1_8 = speed >> 3;
            BallSprite.Speed = (short)(q3_4 + q1_8);
        }
        // else (ctrlDir == 2 or 6): cardinal perpendicular — no change.
    }

    // ball.cpp:2854-2975 — long pass speed boost path.
    // After spin offset application, check joystick direction relative to kick:
    //   - Same as kick direction (delta == 0): goto update_spin_timer2 (no boost)
    //   - delta == 2 or 6 (perpendicular): set longPass = 1, boost speed by 1/8
    //   - delta ∈ {3,4,5} (back): set longSpinPass = 1, boost speed by 1/8
    //   - delta ∈ {1,7}: no boost
    //   - allowedDir < 0: goto holding_left_or_right (also boost speed)
    private static void ApplyLongPassBoostIfNeeded(bool topTeam)
    {
        int allowedDir = TeamData.CurrentAllowedDirection(topTeam);

        if (allowedDir < 0)
        {
            // ball.cpp:2867 — `js @@holding_left_or_right` — allowedDir negative path.
            ApplyLongPassSpeedBoost(topTeam, longPassFlag: true);
            return;
        }

        int ctrlDir = TeamData.ControlledPlDirection(topTeam);
        int delta = ctrlDir - allowedDir;
        if (delta == 0)
        {
            // ball.cpp:2881 `jz @@update_spin_timer2` — same dir, no boost.
            return;
        }

        int dWrapped = delta & 7;
        if (dWrapped == 2 || dWrapped == 6)
        {
            // ball.cpp:2896-2908 — perpendicular → longPass path
            ApplyLongPassSpeedBoost(topTeam, longPassFlag: true);
            return;
        }

        if (dWrapped < 3)
        {
            // ball.cpp:2918 `jb @@update_spin_timer2` — delta == 1 → no boost
            return;
        }
        if (dWrapped > 5)
        {
            // ball.cpp:2929 — delta == 7 → no boost
            return;
        }

        // ball.cpp:2932-2952 — delta ∈ {3,4,5} → longSpinPass + speed boost
        ApplyLongPassSpeedBoost(topTeam, longPassFlag: false);
    }

    // ball.cpp:2932-2952 / 2954-2975 — speed += speed/8 (12.5% boost).
    // longPassFlag: true → set TeamData.longPass = 1
    //               false → set TeamData.longSpinPass = 1
    private static void ApplyLongPassSpeedBoost(bool topTeam, bool longPassFlag)
    {
        if (longPassFlag)
            TeamData.SetLongPass(topTeam, 1);
        else
            TeamData.SetLongSpinPass(topTeam, 1);

        int speed = BallSprite.Speed;
        int boost = speed >> 3;  // 1/8
        BallSprite.Speed = (short)(speed + boost);
    }

    // ball.cpp:2635-2660 (kick) and 2980-3004 (pass) — common timer logic.
    // Spin timer increments; at 10, reset to -1 (no spin active).
    private static void IncrementSpinTimerOrReset(bool topTeam)
    {
        short t = (short)(TeamData.GetSpinTimer(topTeam) + 1);
        TeamData.SetSpinTimer(topTeam, t);
        if (t == 10)
        {
            TeamData.SetSpinTimer(topTeam, -1);
        }
    }
}
