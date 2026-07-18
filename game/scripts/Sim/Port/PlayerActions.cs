namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Mechanical port of the remaining player.cpp functions
// (external/swos-port/src/game/player.cpp). The four already-ported routines
// (updateBallWithControllingGoalkeeper, goalkeeperClaimedTheBall,
// goalkeeperCaughtTheBall, goalkeeperDeflectedBall) live in PlayerUpdate.cs.
//
// This file contains the rest of the file's public + static helpers ported in
// the same asm-translated idiom — `goto` labels keep the original source-line
// correspondence, magic numbers stay as their original literals, and "useless"
// flag updates / register copies are preserved for byte-for-byte parity with
// the IDA disassembly. Tables that live in swos.asm are accessed through
// `Memory.Addr.*` entries (declared at the bottom of Memory.cs).
//
// Callees that are not yet ported are stubbed as `private static` empty
// methods at the end of the class — wire them up in their own port passes.
//
// Asm-translation conventions (same as PlayerUpdate.cs):
//   - "A6 -> team general info"  ↔ `int a6TeamBase` (an absolute address into
//     Memory.* of either TopBase or BottomBase).
//   - "A1 -> player sprite"      ↔ `int a1PlayerAddr` (absolute address of a
//     sprite slot inside the player pool).
//   - "A2 -> ball sprite"        ↔ `int a2BallAddr` (= BallSprite.Base).
//   - "A0 -> table base"          ↔ `int a0TableAddr` (table base in Memory).
//   - "D0/D1/D2 word"             ↔ `short` locals.
//   - "D0/D1/D2 dword"            ↔ `int` locals.
//   - `xor ax, ax` style flag updates are dropped when the result is dead;
//     comparison-side-effects feed into the next conditional jump only.
//   - Out-of-scope helpers are stubbed at file end.
public static class PlayerActions
{
    // FAITHFUL BALL CONTROL (2026-07-18). The original expresses the CONTROL
    // skill ONLY while dribbling: CalculateIfPlayerWinsBall nudges the ball ahead
    // each odd tick by kBallSpeedDeltaWhenControlled[control] (a weak player pushes
    // it too far and loses it) + a turn-tolerance break. A per-tick "pin" that
    // glued the ball to every outfield carrier's feet (PlayerControlled.cs) was a
    // NON-original compensating hack — it made control-0 and control-7 dribble
    // identically. With this flag TRUE the outfield pin is OFF (faithful): the ball
    // "runs away" from bad dribblers. The KEEPER pin stays on always (it IS
    // canonical — updateBallWithControllingGoalkeeper). Flip to false to restore
    // the old glued-ball behaviour if the un-pinned chase regresses.
    public static bool FaithfulBallControl = true;

    // TeamGeneralInfo.wonTheBallTimer — swos.asm struct offset +138
    // (swos.h:399 names it `unkTimer`). "Team may not control the ball while
    // != 0" — the anti-re-steal lockout (duel loser 12 ticks, turn-dribble
    // breakaway 8 ticks). NOT the same field as AI_timer (+130), which is
    // write-only in the original.
    private const int OffWonTheBallTimer = 138;

    // ====================================================================
    // updatePlayerSpeedAndFrameDelay — player.cpp:17-77
    // ====================================================================
    // Idiomatic C++ in the source (not asm-translated). Sets player.speed
    // from the per-skill speed table, modulated by injury, ball-control,
    // pass-overlap, and stoppage timers. Then computes frameDelay from speed.
    //
    // The function reads several PlayerGameHeader fields that the C# port
    // doesn't expose yet (injuryLevel, longPass, leftSpin, rightSpin,
    // passToPlayerPtr, passingToPlayer, fullDirection, etc.). The ones we
    // CAN reach are wired; the rest fall through to "skip" branches —
    // matching what would happen if those fields were zero on the original.
    //
    // from player.cpp:17
    public static void UpdatePlayerSpeedAndFrameDelay(int a6TeamBase, int a1PlayerAddr)
    {
        // player.cpp:19 — early-out unless player.state == PlayerState::kNormal
        // AND (game is in progress AND player.playerOrdinal == 1 AND not the
        // controlled player). The composite predicate gates a goalkeeper-only
        // override. In the simplified port we keep the gate identical.
        //
        // IMPORTANT: the early-out skips speed/frameDelay writes BUT the asm
        // caller (l_update_player_speed_and_deltas, updatePlayers.cpp:10086)
        // still UNCONDITIONALLY runs the delta-recompute step using whatever
        // speed is currently on the sprite. We mirror that here by calling
        // RecomputeSpriteDeltas before every early-out return.
        byte playerState = Memory.ReadByte(a1PlayerAddr + PlayerSprite.OffPlayerState);
        if (playerState != 0)
        {
            // PL_NORMAL = 0. Anything else → bail (but still recompute deltas).
            RecomputeSpriteDeltas(a6TeamBase, a1PlayerAddr);
            return;
        }
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        short playerOrdinal = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffPlayerOrdinal);
        int controlledPlayer = Memory.ReadSignedDword(a6TeamBase + TeamData.OffControlledPlayer);
        if (gameStatePl == 100 /*kInProgress*/ && playerOrdinal == 1 && a1PlayerAddr != controlledPlayer)
        {
            // Keeper early-out — same delta-recompute responsibility.
            RecomputeSpriteDeltas(a6TeamBase, a1PlayerAddr);
            return;
        }

        // player.cpp:23-24 — per-state speed tables.
        ReadOnlySpan<int> kPlayerSpeedsGameInProgress = stackalloc int[] {
            928, 974, 1020, 1066, 1112, 1158, 1204, 1250
        };
        ReadOnlySpan<int> kPlayerSpeedsGameStopped = stackalloc int[] {
            1136, 1152, 1168, 1184, 1200, 1216, 1232, 1248
        };
        ReadOnlySpan<int> speedTable = gameStatePl == 100
            ? kPlayerSpeedsGameInProgress
            : kPlayerSpeedsGameStopped;

        // player.cpp:29 — auto& playerInfo = getPlayerPointerFromShirtNumber(team, player).
        // Ported via GetPlayerInfoForSprite (player.cpp:3245-3251). Reads
        // PlayerInfo.speed (byte at +32, see TeamDataLoader.cs:55 / swos.h:182).
        // Fallback to skill=4 if PlayerInfo hasn't been wired (early-tick
        // safety, e.g. headless smoke before WritePlayerInfos has run).
        int playerInfoAddr = GetPlayerInfoForSprite(a6TeamBase, a1PlayerAddr);
        int playerInfoSpeedSkill = 4;
        if (playerInfoAddr != 0)
        {
            int s = Memory.ReadByte(playerInfoAddr + TeamDataLoader.OffSpeed);
            if (s >= 0 && s <= 7) playerInfoSpeedSkill = s;
        }

        // player.cpp:32 — player.speed = speedTable[playerInfo.speed].
        int newSpeed = speedTable[playerInfoSpeedSkill];

        // player.cpp:34-37 — `runSlower` flag (post-goal slowdown).
        short runSlower = Memory.ReadSignedWord(Memory.Addr.runSlower);
        if (runSlower != 0)
        {
            newSpeed = 5 * newSpeed / 8;
        }

        // player.cpp:39-43 — injury speed handicap.
        //   if ((team.playerNumber || team.playerCoachNumber) && player.injuryLevel) {
        //       static const int kInjuriesSpeedHandicap[] =
        //           { 0, -96, -128, -160, -192, -224, -256, -288 };
        //       player.speed += kInjuriesSpeedHandicap[player.injuryLevel / 32];
        //   }
        //
        // Gate: only applies to human/coach-controlled teams (playerNumber !=0
        // OR playerCoachNumber !=0). For AI-only teams the asm leaves the
        // handicap unapplied (matches the SWOS-design intent: keep CPU
        // opponents at full speed). injuryLevel is a byte 0..255 (encoded
        // 32-step buckets). Sprite.injuryLevel at offset +104 (word).
        // Source: external/swos-port/src/game/player.cpp:39-43.
        short playerNumberHandicap     = Memory.ReadSignedWord(a6TeamBase + TeamData.OffPlayerNumber);
        short playerCoachNumberHandicap = Memory.ReadSignedWord(a6TeamBase + TeamData.OffPlayerCoachNumber);
        if ((playerNumberHandicap != 0 || playerCoachNumberHandicap != 0))
        {
            short injuryLevel = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffInjuryLevel);
            if (injuryLevel != 0)
            {
                ReadOnlySpan<int> kInjuriesSpeedHandicap = stackalloc int[] {
                    0, -96, -128, -160, -192, -224, -256, -288
                };
                int bucket = injuryLevel / 32;
                // assert(bucket < 8) — clamp defensively.
                if (bucket < 0) bucket = 0;
                else if (bucket >= kInjuriesSpeedHandicap.Length) bucket = kInjuriesSpeedHandicap.Length - 1;
                newSpeed += kInjuriesSpeedHandicap[bucket];
            }
        }

        // OpenSWOS OPTIONAL fatigue penalty (NOT in the original — dead
        // 'FITNESS*****' string swos.asm:186013 has no XREF). Reuse the same
        // 46-per-skill-point step the speed table uses so the reduction stays
        // in the engine's integer quantisation. Gated + set-once at match setup.
        if (PlayerEnergy.EffectEnabled)
            newSpeed -= 46 * PlayerEnergy.SpeedStep(a1PlayerAddr);

        // player.cpp:45-48 — controlled player carrying the ball runs at 87.5%.
        short playerHasBall = TeamData.PlayerHasBall(a6TeamBase == TeamData.TopBase);
        if (a1PlayerAddr == controlledPlayer && playerHasBall != 0)
        {
            newSpeed -= newSpeed / 8;
        }

        // player.cpp:50-60 — pass-overlap speed boost.
        // For a human-controlled team, the designated pass-receiver gets their
        // running speed clamped while a long-pass / spin pass is in flight,
        // provided ball direction overlaps player direction.
        //   if (team.playerNumber && &player == team.passToPlayerPtr &&
        //       team.passingToPlayer && (team.longPass || team.leftSpin ||
        //       team.rightSpin) && ballSprite.speed) {
        //       int8_t dirDiff = ball.fullDirection - player.fullDirection;
        //       if (-7 <= dirDiff && dirDiff <= 7)
        //           player.speed = (dirDiff >= -5 && dirDiff <= 5) ? 256 : 512;
        //   }
        // All TeamData fields live in SwosVm.TeamData. ball.fullDirection at
        // BallSprite +82; player.fullDirection at +82. fullDirection is a
        // 0..255 byte the asm reads via `mov al, [esi+Sprite.fullDirection]`
        // and subtracts in 8-bit space (`sub al, [esi+Sprite.fullDirection]`),
        // so the diff wraps in int8.
        // Source: external/swos-port/src/game/player.cpp:50-60.
        short teamPlayerNumber = Memory.ReadSignedWord(a6TeamBase + TeamData.OffPlayerNumber);
        if (teamPlayerNumber != 0)
        {
            int passToPtr = Memory.ReadSignedDword(a6TeamBase + TeamData.OffPassToPlayerPtr);
            if (passToPtr == a1PlayerAddr)
            {
                short passingToPlayer = Memory.ReadSignedWord(a6TeamBase + TeamData.OffPassingToPlayer);
                if (passingToPlayer != 0)
                {
                    short longPassFlag = Memory.ReadSignedWord(a6TeamBase + TeamData.OffLongPass);
                    short leftSpinFlag = Memory.ReadSignedWord(a6TeamBase + TeamData.OffLeftSpin);
                    short rightSpinFlag = Memory.ReadSignedWord(a6TeamBase + TeamData.OffRightSpin);
                    if (longPassFlag != 0 || leftSpinFlag != 0 || rightSpinFlag != 0)
                    {
                        short ballSpeed = BallSprite.Speed;
                        if (ballSpeed != 0)
                        {
                            // 8-bit signed wrap of fullDirection diff.
                            byte bFD = (byte)(BallSprite.FullDirection & 0xFF);
                            byte pFD = (byte)(Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffFullDirection) & 0xFF);
                            sbyte directionDiff = (sbyte)(bFD - pFD);
                            if (directionDiff >= -7 && directionDiff <= 7)
                            {
                                newSpeed = (directionDiff >= -5 && directionDiff <= 5) ? 256 : 512;
                            }
                        }
                    }
                }
            }
        }

        // player.cpp:62-72 — stoppage-time slowdown.
        //
        // BUG FIX (2026-07-02): the GameState constants were wrong (16/24/25/26).
        // Per swos.h:584-591: kGoingToHalftime = 23, kPlayersGoingToShower = 24,
        // kFirstHalfEnded = 29, kGameEnded = 30. The old values made the
        // "slow down and stop gradually" branch fire during
        // kPlayersGoingToShower (24) and never during the states it belongs
        // to. Cross-checked against the asm compares in updatePlayers.cpp:
        // 9253 (ST_FIRST_HALF_ENDED == 29) and 8709 (ST_GAME_ENDED == 30).
        short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);
        if (gameStatePl != 100)
        {
            short stoppageTimerTotal = Memory.ReadSignedWord(Memory.Addr.stoppageTimerTotal);
            if (gameState == 29 /*kFirstHalfEnded, swos.h:590*/ || gameState == 30 /*kGameEnded, swos.h:591*/)
            {
                // player.cpp:63-65 — slow down and stop players gradually.
                newSpeed = System.Math.Max(newSpeed - stoppageTimerTotal * 32, 0);
            }
            else if (gameState == 23 /*kGoingToHalftime, swos.h:584*/ || gameState == 24 /*kPlayersGoingToShower, swos.h:585*/)
            {
                // player.cpp:66-71 — speed weighted with time since game/half end.
                int factor = System.Math.Min(stoppageTimerTotal * 4, 100);
                newSpeed = newSpeed * factor / 100;
            }
        }

        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffSpeed, newSpeed);

        // player.cpp:74-76 — frameDelay derived from speed.
        const int kMaxSpeed = 1280;
        int frameDelay = System.Math.Max(kMaxSpeed - newSpeed, 0) / 128 + 6;
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffFrameDelay, frameDelay);

        // updatePlayers.cpp:10088-10356 — l_update_player_speed_and_deltas
        // post-UpdatePlayerSpeed: unconditional CalculateDeltaXAndY → write
        // sprite.deltaX/deltaY, direction/fullDirection update, and the
        // standing ↔ running anim-table switch (full tail in the helper).
        RecomputeSpriteDeltas(a6TeamBase, a1PlayerAddr);
    }

    // ====================================================================
    // RecomputeSpriteDeltas — updatePlayers.cpp:10088-10106
    // ====================================================================
    //
    // Reads (destX, destY, x, y, speed) off the sprite and writes back
    // (deltaX, deltaY), then runs the FULL loop-exit tail: direction update
    // (movement quantisation / stationary face-the-ball), fullDirection =
    // ball→player (or foul-spot→player during restarts), and the standing ↔
    // running animation-table switch. This is the back-half of
    // `l_update_player_speed_and_deltas` (updatePlayers.cpp:10086-10356) —
    // extracted as a separate helper because the keeper early-out in
    // UpdatePlayerSpeedAndFrameDelay would otherwise skip it.
    //
    // 2026-07-02 (USER-VISIBLE BUG FIX — "players slide sideways without
    // animating during break walks"): the tail after the delta write
    // (updatePlayers.cpp:10107-10356) was previously unported. It is the ONLY
    // place the original ever installs `playerRunningAnimTable` (:10334-10336)
    // — our port never installed it anywhere, so every walking sprite kept the
    // standing table (a single per-direction frame) and glided without a run
    // cycle. The tail also owns the classic SWOS idle behaviour: a stationary,
    // non-controlled sprite turns to FACE the ball (or the foul spot during a
    // restart), via fullDirection + 128 (:10186-10218).
    //
    // from updatePlayers.cpp:10088-10356 (the post-UpdatePlayerSpeed asm)
    //      external/swos-port/swos/swos.asm:117409-117536 (same tail).
    public static void RecomputeSpriteDeltas(int a6TeamBase, int a1PlayerAddr)
    {
        short curSpeed = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffSpeed);
        short destX = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDestX);
        short destY = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDestY);
        short curX  = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffX + 2);
        short curY  = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffY + 2);
        // updatePlayers.cpp:10088-10101 — CalculateDeltaXAndY(D0=speed, ...).
        var deltas = SpriteUpdate.CalculateDeltaXAndY(curSpeed, curX, curY, destX, destY);
        // updatePlayers.cpp:10102-10106 — write deltaX/deltaY.
        Memory.WriteDword(a1PlayerAddr + PlayerSprite.OffDeltaX, deltas.DeltaX);
        Memory.WriteDword(a1PlayerAddr + PlayerSprite.OffDeltaY, deltas.DeltaY);

        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        short ordinal = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffPlayerOrdinal);

        // ----- updatePlayers.cpp:10107-10148 — keeper facing override -------
        // A keeper (ordinal 1), game in progress, NOT playing outfield, with
        // the ball in EITHER penalty area, always faces the ball regardless of
        // movement. The asm's `mov eax, dword ptr ballInUpperPenaltyArea`
        // (:10142) is a 32-bit read spanning ballInUpperPenaltyArea AND the
        // adjacent ballInLowerPenaltyArea word — we read both words explicitly
        // since our Memory layout does not guarantee adjacency.
        bool calcDirection = false;
        if (ordinal == 1 && gameStatePl == 100)
        {
            short gkPlaying = Memory.ReadSignedWord(a6TeamBase + TeamData.OffGoalkeeperPlaying);
            if (gkPlaying == 0)
            {
                ushort upPa = Memory.ReadWord(Memory.Addr.ballInUpperPenaltyArea);
                ushort loPa = Memory.ReadWord(Memory.Addr.ballInLowerPenaltyArea);
                if ((upPa | loPa) != 0)
                    calcDirection = true;                      // :10147-10148 jnz l_calc_direction
            }
        }

        // ----- updatePlayers.cpp:10150-10218 — direction (0..7) update ------
        // Moving (result direction >= 0) → quantise movement angle.
        // Stationary: controlled / passingKicking sprites keep their facing
        // (:10159-10184 → l_skip_setting_direction); everyone else turns via
        // (fullDirection + 128) & 255 — fullDirection holds LAST tick's
        // BALL→PLAYER angle at this point (see the refresh below), so the
        // +128 flips it into player→ball = the face-the-ball turn.
        int d0Dir = deltas.Direction;
        bool skipSettingDirection = false;
        if (!calcDirection)
        {
            if (d0Dir < 0)                                     // :10151-10157 jns l_got_movement
            {
                int ctrlDir = Memory.ReadSignedDword(a6TeamBase + TeamData.OffControlledPlayer);
                int pkpDir = Memory.ReadSignedDword(a6TeamBase + TeamData.OffPassingKickingPlayer);
                if (a1PlayerAddr == ctrlDir || a1PlayerAddr == pkpDir)
                    skipSettingDirection = true;               // :10159-10184
                else
                    calcDirection = true;                      // fall into l_calc_direction
            }
        }
        if (!skipSettingDirection)
        {
            if (calcDirection)
            {
                // l_calc_direction — :10186-10199: D0 = (fullDirection + 128) & 255.
                short oldFull = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffFullDirection);
                d0Dir = (oldFull + 128) & 0xFF;
            }
            // l_got_movement — :10201-10218: direction = ((D0 + 16) & 255) >> 5.
            int dir8 = ((d0Dir + 16) & 0xFF) >> 5;
            Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffDirection, dir8);
        }

        // ----- updatePlayers.cpp:10220-10278 — fullDirection refresh --------
        // fullDirection := angle from the BALL to the PLAYER (ball→player) —
        // or from the FOUL SPOT to the player when the game is stopped in a
        // restart state (gameState <= 20, i.e. kickoff walk / goal outs /
        // corners / free kicks / throw-ins). Speed argument is the constant
        // 256 (:10266).
        //
        // ARGUMENT ORDER IS LOAD-BEARING (2026-07-02 180° facing/kick fix):
        // CalculateDeltaXAndY computes the vector dest − current
        // (swos.asm:69476-69479 `sub D1, D3` / `sub D2, D4`; D1/D2 = dest,
        // D3/D4 = current). The tail loads D3/D4 = ball-or-foul-spot
        // (:10247-10250, :10253-10258) and D1/D2 = PLAYER x/y (:10261-10265)
        // — so the stored angle is BALL→PLAYER, and the +128 flip in
        // l_calc_direction above is what turns it into "face the ball".
        // This was previously called with (player, ball) — player→ball — which
        // made every stationary player face 180° AWAY from the ball and every
        // pass-receiver lookup (GetClosestNonControlledPlayerInDirection,
        // player.cpp:3360) pick players opposite to the kick direction.
        {
            short d3Target, d4Target;
            short gsTail = Memory.ReadSignedWord(Memory.Addr.gameState);
            if (gameStatePl != 100 && (ushort)gsTail <= 20)    // :10221-10245 (ja → ball)
            {
                d3Target = Memory.ReadSignedWord(Memory.Addr.foulXCoordinate);  // :10247-10250
                d4Target = Memory.ReadSignedWord(Memory.Addr.foulYCoordinate);
            }
            else
            {
                // l_ball_going_to_player — :10253-10258.
                d3Target = BallSprite.XPixels;
                d4Target = BallSprite.YPixels;
            }
            // l_calculate_player_ball_direction — :10260-10278.
            // current = ball/foul spot (D3/D4), destination = player (D1/D2).
            var face = SpriteUpdate.CalculateDeltaXAndY(256, d3Target, d4Target, curX, curY);
            short newFull = face.Direction < 0 ? (short)0 : (short)face.Direction; // :10270-10273
            Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffFullDirection, newFull); // :10276-10278
        }

        // ----- updatePlayers.cpp:10279-10354 — anim-table switch ------------
        // PL_NORMAL only. Compare the fresh isMoving state against the stamp
        // taken at loop start (updatePlayers.cpp:415-427); on movement
        // start/stop — or, when unchanged, on a facing change (playerDirection
        // stamp vs current direction) — install the running or standing table.
        // SetPlayerAnimationTable rebinds the per-direction frame stream and
        // restarts the cycle, which is what animates the walk.
        {
            byte plState = Memory.ReadByte(a1PlayerAddr + PlayerSprite.OffPlayerState);
            if (plState != 0) return;                          // :10279-10290 jnz l_next_player

            bool moving = deltas.DeltaX != 0 || deltas.DeltaY != 0; // :10292-10304
            byte newMoving = moving ? (byte)0xFF : (byte)0x00;
            byte stampedMoving = Memory.ReadByte(a1PlayerAddr + PlayerSprite.OffIsMoving);
            if (newMoving == stampedMoving)                    // :10306-10319 jz l_no_change_in_movement
            {
                // l_no_change_in_movement — :10339-10354: reinstall only when
                // the 8-way facing changed since the loop-start stamp.
                short stampedDir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffPlayerDirection);
                short dirNow = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
                if (stampedDir == dirNow) return;              // jnz cseg_83D7F / fall to l_next_player
            }
            // cseg_83D7F — :10321-10337.
            if (moving)
            {
                // l_player_is_running — :10334-10336 (playerRunningAnimTable @ 453104).
                SetPlayerAnimationTable(a1PlayerAddr, Memory.Addr.kPlayerRunningAnimTableAddr);
            }
            else
            {
                // :10330-10332 (playerNormalStandingAnimTable @ 453234).
                SetPlayerAnimationTable(a1PlayerAddr, Memory.Addr.kPlayerStandingAnimTableAddr);
            }
        }
    }


    // ====================================================================
    // getPlayerPointerFromShirtNumber — player.cpp:3245-3251
    // ====================================================================
    //
    // C++ original:
    //     const PlayerInfo& getPlayerPointerFromShirtNumber(
    //         const TeamGeneralInfo& team, const Sprite& player) {
    //         return team.inGameTeamPtr->players[player.playerOrdinal - 1];
    //     }
    //
    // PlayerInfo records are written into VM memory by
    // TeamDataLoader.WritePlayerInfos at match start. Each is 61 bytes
    // (TeamDataLoader.PlayerInfoSize). The TeamData.OffInGameTeamPtr field
    // (TeamGeneralInfo.inGameTeamPtr) is wired in TeamDataLoader.WireTeamFields
    // to point directly at players[0] (see TeamDataLoader.cs:147-149), so
    // `players[ordinal-1]` becomes `inGameTeamPtr + (ordinal-1) * 61`.
    //
    // playerOrdinal is 1-based: 1 = goalkeeper, 2..11 = outfielders. We expect
    // ordinal in [1, 16] (PlayerInfo array spans kNumPlayersInTeam = 16 slots).
    //
    // Returns 0 if the team's inGameTeamPtr isn't wired yet (early-call safety),
    // matching the assertion guard the C++ port keeps elsewhere.
    public static int GetPlayerInfoForSprite(int a6TeamBase, int a1PlayerAddr)
    {
        int inGameTeamPtr = Memory.ReadSignedDword(a6TeamBase + TeamData.OffInGameTeamPtr);
        if (inGameTeamPtr <= 0) return 0;

        short ordinal = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffPlayerOrdinal);
        if (ordinal < 1 || ordinal > 16) return 0;

        // PlayerInfoSize = 61. The inGameTeamPtr already points at players[0],
        // so no kTeamGameHeaderSize back-adjustment is needed (unlike the asm
        // which receives an A4 = PlayerInfo* and then steps back by header
        // size to read PlayerGameHeader fields at +72..+75).
        return inGameTeamPtr + (ordinal - 1) * TeamDataLoader.PlayerInfoSize;
    }


    // ====================================================================
    // updatePlayerWithBall — player.cpp:85-130
    // ====================================================================
    // Pin ball to outfielder's "ball offset" (kPlayerWithBallOffsets) — the
    // dribble offset which differs from the keeper-hold offset.
    //
    // from player.cpp:85
    public static void UpdatePlayerWithBall(int a1PlayerAddr)
    {
        // player.cpp:87-95 — read ball + player coords, then direction.
        short ballX = BallSprite.XPixels;
        short ballY = BallSprite.YPixels;
        short dir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);

        // player.cpp:97-99 — direction <<= 2 (4 bytes per entry).
        int dirOffset = (dir & 0xFFFF) << 2;

        // player.cpp:100-119 — newX = ballX + table[dir*4]; newY += table+2.
        short tableDx = Memory.ReadSignedWord(Memory.Addr.kPlayerWithBallOffsets + dirOffset);
        short tableDy = Memory.ReadSignedWord(Memory.Addr.kPlayerWithBallOffsets + dirOffset + 2);

        short newX = (short)(ballX + tableDx);
        short newY = (short)(ballY + tableDy);

        // player.cpp:120-128 — write player.x/y + destX/destY.
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffX + 2, newX);
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffY + 2, newY);
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffDestX, newX);
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffDestY, newY);

        // player.cpp:129 — resetBothTeamSpinTimers.
        BallUpdate.ResetBothTeamSpinTimers();
    }


    // ====================================================================
    // updateControllingPlayer — player.cpp:135-191
    // ====================================================================
    // Pin the ball to the outfielder (controlling player), zero its speed,
    // halve its deltaZ. Counterpart of updateBallWithControllingGoalkeeper
    // but uses the OUTFIELDER offset table (kBallPlOffsets, not the keeper
    // table). The Z handling is symmetric — sar by 1, no extra negation.
    //
    // from player.cpp:135
    public static void UpdateControllingPlayer(int a1PlayerAddr)
    {
        // player.cpp:137-143 — direction <<= 2.
        short dir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        int dirOffset = (dir & 0xFFFF) << 2;

        // player.cpp:144-166 — newX = player.x + kBallPlOffsets[dir*4].
        short playerX = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffX + 2);
        short playerY = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffY + 2);

        short offsX = Memory.ReadSignedWord(Memory.Addr.kBallPlOffsetsBase + dirOffset);
        short offsY = Memory.ReadSignedWord(Memory.Addr.kBallPlOffsetsBase + dirOffset + 2);

        short newX = (short)(playerX + offsX);
        short newY = (short)(playerY + offsY);

        // player.cpp:167-178 — ball.speed = 0; ball.x/y/destX/destY = new.
        BallSprite.Speed = 0;
        BallSprite.XPixels = newX;
        BallSprite.YPixels = newY;
        BallSprite.DestX = newX;
        BallSprite.DestY = newY;

        // player.cpp:178 — ball.z.whole = 0 (clamp to ground).
        BallSprite.ZPixels = 0;

        // player.cpp:179-189 — deltaZ sar 1 (no negation, unlike the keeper variant).
        int dz = BallSprite.DeltaZ;
        BallSprite.DeltaZ = dz >> 1;  // sar — preserves sign.

        // player.cpp:190 — resetBothTeamSpinTimers.
        BallUpdate.ResetBothTeamSpinTimers();
    }


    // ====================================================================
    // calculateIfPlayerWinsBall — player.cpp:276-744
    // ====================================================================
    // When a player closes on the ball, decide if the player wins it,
    // including 50/50 contests with the opponent's controlling player.
    // Tie-breaker is a Rand() roll against kPlAvgTacklingBallControlDiffChance
    // indexed by (tackling+ballControl)/2 diff.
    //
    // After winning, kicks the ball forward a touch (1 px x-nudge) and
    // sets up team.controlledPlDirection and the spinTimer.
    //
    // from player.cpp:276
    public static void CalculateIfPlayerWinsBall(int d0Direction, int a6TeamBase, int a1PlayerAddr)
    {
        bool isTopTeam = (a6TeamBase == TeamData.TopBase);

        // player.cpp:279 — team.passInProgress = 0.
        Memory.WriteWord(a6TeamBase + TeamData.OffPassInProgress, 0);

        // player.cpp:280-289 — read opponent's team. if opponent.wonTheBallTimer != 0 → set_team_direction.
        // player.cpp:283 reads [esi+138] = TeamGeneralInfo.wonTheBallTimer —
        // NOT AI_timer (+130). Same field UpdatePlayers.cs decrements per tick.
        // (2026-07-02 duel ping-pong fix: the +130 misread made this gate
        // almost always non-zero — 0 duels resolved in 619 calls.)
        int oppTeamBase = Memory.ReadSignedDword(a6TeamBase + TeamData.OffOpponentsTeam);
        short wonBallTimer = Memory.ReadSignedWord(oppTeamBase + OffWonTheBallTimer);
        if (wonBallTimer != 0) goto l_set_team_direction;

        // player.cpp:291-297 — if opponent.playerHasBall == 0 → set_team_direction.
        short oppPlayerHasBall = Memory.ReadSignedWord(oppTeamBase + TeamData.OffPlayerHasBall);
        if (oppPlayerHasBall == 0) goto l_set_team_direction;

        // player.cpp:299-305 — if opponent.currentAllowedDirection < 0 → set_team_direction.
        short oppAllowedDir = Memory.ReadSignedWord(oppTeamBase + TeamData.OffCurrentAllowedDirection);
        if (oppAllowedDir < 0) goto l_set_team_direction;

        // player.cpp:307-323 — A1 = opponent.controlledPlayer. If null → no_opponent_controlled_player.
        int oppControlledPlayer = Memory.ReadSignedDword(oppTeamBase + TeamData.OffControlledPlayer);
        if (oppControlledPlayer == 0) goto l_no_opponent_controlled_player;

        // player.cpp:325-340 — D1 = (own's player_at_opp_ordinal tackling + ballControl) / 2.
        //
        // Mechanical port of the asm sequence (player.cpp:307-340). A1 has been
        // overwritten with opp.controlledPlayer at line 311-312 (eax = [esi+32]
        // where esi is the opp team), but A6 remains the OWN team here (the
        // swap doesn't happen until line 343). So this first call resolves to
        // `getPlayerPointerFromShirtNumber(own_team, opp_controlled_sprite)` —
        // meaning OWN team's roster slot at the opp's controlled-player ordinal.
        //
        // Strange but exact: this is what the asm does. The fields read are
        // PlayerInfo.tackling (+30) and PlayerInfo.ballControl (+31) — the asm
        // accesses them via [esi+72]/[esi+73] after A4 -= kTeamGameHeaderSize
        // (42 bytes), which lands on PlayerInfo+30/+31. Our
        // GetPlayerInfoForSprite returns PlayerInfo* directly, so we add
        // OffTackling / OffBallControl unmodified.
        int a4D1 = GetPlayerInfoForSprite(a6TeamBase, oppControlledPlayer);
        int d1Tack = a4D1 != 0 ? Memory.ReadByte(a4D1 + TeamDataLoader.OffTackling)    : 0;
        int d1Bc   = a4D1 != 0 ? Memory.ReadByte(a4D1 + TeamDataLoader.OffBallControl) : 0;
        // 8-bit signed add then arithmetic shr 1 — matches asm `add bl,al`/`shr bl,1`.
        sbyte d1Sum  = (sbyte)((sbyte)d1Tack + (sbyte)d1Bc);
        sbyte d1OwnAvg = (sbyte)(((byte)d1Sum) >> 1);

        // player.cpp:342-357 — A6 swap to opponent for the comparison; A1 = opp.controlledPlayer.
        if (oppControlledPlayer == 0) goto l_no_opponent_controlled_player;

        // player.cpp:359-374 — D0 = (opp's controlled player tackling + ballControl) / 2.
        // After the A6 swap (lines 342-344), A6=opp team. A1 is still
        // opp.controlledPlayer (set at line 311). So this resolves OPP's actual
        // controlled-player skill record. This is the meaningful read of the
        // two — the dribbler's own tackling+ballControl.
        int a4D0 = GetPlayerInfoForSprite(oppTeamBase, oppControlledPlayer);
        int d0Tack = a4D0 != 0 ? Memory.ReadByte(a4D0 + TeamDataLoader.OffTackling)    : 0;
        int d0Bc   = a4D0 != 0 ? Memory.ReadByte(a4D0 + TeamDataLoader.OffBallControl) : 0;
        sbyte d0Sum  = (sbyte)((sbyte)d0Tack + (sbyte)d0Bc);
        sbyte d0OppAvg = (sbyte)(((byte)d0Sum) >> 1);

        // player.cpp:376-396 / swos.asm:108109-108151 — the A6 bookkeeping
        // tracks the DUEL LOSER (the team that receives wonTheBallTimer=12):
        //   asm:108109-108111 — before the compare A6 = opponentsTeam;
        //   asm:108126-108131 — `sub D1,D0; jns` KEEPS A6 (= opponent) when
        //     d1Diff >= 0; when negative, `neg D1` and A6 swaps back to own.
        //   Net pre-roll rule: A6 = the LOWER-average team (tie → opponent).
        //   The stronger player is protected: P(weaker side loses) =
        //   (16+|diff|)/32 = 50..72%.
        // (2026-07-02 fix, confirmed independently by two audit probes: this
        // was previously INVERTED — the higher-avg side was picked, so the
        // STRONGER player lost 50-72% of duels — the "CPU takes everything
        // from a good human player" bug.)
        int d1Diff = d1OwnAvg - d0OppAvg;
        int a6LoserTeam;
        if (d1Diff < 0)
        {
            // own side has the LOWER avg — abs the diff, own is the pre-roll loser.
            d1Diff = -d1Diff;
            a6LoserTeam = a6TeamBase;
        }
        else
        {
            // opponent side has the lower (or equal) avg — pre-roll loser.
            a6LoserTeam = oppTeamBase;
        }

        // cseg_7A26D (player.cpp:398-410):
        // player.cpp:399-410 — D1 = cbw(D1); A0 = kPlAvgTacklingBallControlDiffChance.
        // Rand() & 31 → D0. al = table[D1]. compare D0 < al → init_ball_winner_team.
        int rand = SwosRand() & 31;
        int tableIdx = d1Diff & 0xFFFF;
        byte chance = Memory.ReadByte(Memory.Addr.kPlAvgTacklingBallControlDiffChance + tableIdx);
        if ((byte)rand < chance)
        {
            // l_init_ball_winner_team. The pre-roll loser stays the loser.
        }
        else
        {
            // player.cpp:423-425 — upset: swap A6 to the opponent side.
            a6LoserTeam = Memory.ReadSignedDword(a6LoserTeam + TeamData.OffOpponentsTeam);
        }

        // Telemetry: "own win" = the OTHER side got locked out.
        if (a6LoserTeam != a6TeamBase)
            PlayerControlled.IncSkillDuelOwnWin();
        else
            PlayerControlled.IncSkillDuelOppWin();

        // l_init_ball_winner_team (player.cpp:427-440):
        // player.cpp:429 writes [esi+138] = wonTheBallTimer = 12 — the 12-tick
        // "may not control the ball" lockout for the duel LOSER. NOT AI_timer.
        Memory.WriteWord(a6LoserTeam + OffWonTheBallTimer, 12);
        BallSprite.Speed = 0;
        BallSprite.DestX = BallSprite.XPixels;
        BallSprite.DestY = BallSprite.YPixels;
        return;

    l_no_opponent_controlled_player:;
        // player.cpp:442-447 — pop & fall to set_team_direction.

    l_set_team_direction:;
        // player.cpp:448-451 — team.controlledPlDirection = D0.
        Memory.WriteWord(a6TeamBase + TeamData.OffControlledPlDirection, d0Direction);

        // player.cpp:452-462 — if ball.deltaZ > 0 → clamp to -1.
        int ballDz = BallSprite.DeltaZ;
        if (ballDz > 0)
        {
            BallSprite.DeltaZ = -1;
        }

        // player.cpp:464-475 — if D0 != 0 (direction has horizontal component) → set_player_destination.
        if (d0Direction != 0) goto l_set_player_destination;

        // player.cpp:477-498 — nudge ball X by 1 px when player straight-up.
        short bxNudge = BallSprite.XPixels;
        short pxNudge = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffX + 2);
        short d2NudgeDiff = (short)(bxNudge - pxNudge);
        if (d2NudgeDiff >= 4) goto l_set_player_destination;
        if (d2NudgeDiff <= -4) goto l_set_player_destination;
        if (d2NudgeDiff < 0)
        {
            // l_nudge_ball_left (player.cpp:520-533).
            BallSprite.XPixels = (short)(bxNudge - 1);
            goto l_set_player_destination;
        }
        // l_nudge_ball_right (player.cpp:535-544).
        BallSprite.XPixels = (short)(bxNudge + 1);

    l_set_player_destination:;
        // player.cpp:547-584 — destX/Y = player.x/y + kDefaultDestinations[dir*4].
        int destIdx = (d0Direction & 0xFFFF) << 2;
        short plDestX = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffX + 2);
        short plDestY = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffY + 2);
        short defDx = Memory.ReadSignedWord(Memory.Addr.kDefaultDestinations + destIdx);
        short defDy = Memory.ReadSignedWord(Memory.Addr.kDefaultDestinations + destIdx + 2);
        BallSprite.DestX = (short)(plDestX + defDx);
        BallSprite.DestY = (short)(plDestY + defDy);

        // player.cpp:585-587 — A4 = PlayerGameHeader pointer. Wired through
        // GetPlayerInfoForSprite (player.cpp:3245-3251). The asm reads
        // [esi+72] / [esi+73] which is PlayerInfo.tackling / ballControl
        // (offsets +30/+31 in PlayerInfo, +72/+73 in PlayerGameHeader after
        // the +42-byte TeamGameHeader prefix).
        int a4PlayerInfo = GetPlayerInfoForSprite(a6TeamBase, a1PlayerAddr);

        // player.cpp:588-597 — test currentTick bit 1; if zero → update_ball_speed (d1=0).
        short d1Inc = 0;
        byte currentTickByte = Memory.ReadByte(Memory.Addr.currentGameTick);
        if ((currentTickByte & 2) != 0)
        {
            // player.cpp:599-611 — read kBallSpeedDeltaWhenControlled[ballControl*2].
            int ballControlSkill = 4;
            if (a4PlayerInfo != 0)
            {
                int bc = Memory.ReadByte(a4PlayerInfo + TeamDataLoader.OffBallControl);
                if (bc >= 0 && bc <= 7) ballControlSkill = bc;
            }
            d1Inc = Memory.ReadSignedWord(Memory.Addr.kBallSpeedDeltaWhenControlled + ballControlSkill * 2);
        }

        // l_update_ball_speed (player.cpp:613-624):
        // ball.speed += d1 + player.speed.
        short playerSpeed = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffSpeed);
        BallSprite.Speed = (short)(d1Inc + playerSpeed);

        // player.cpp:625-685 — direction-diff between player's full-angle pose
        // (fullDirection + 128, low byte) and the move direction (direction*32, low byte).
        // The asm dispatch is:
        //   cmp D0, 64        ; jge cseg_7A517 (add +256)
        //   cmp D0, 192 (=-64); jg  cseg_7A523 (skip; D0 in (-64, 64))
        //   fall through      ; cseg_7A517 (add +256)
        // i.e. add +256 when |signedDiff| >= 64, skip when -64 < signedDiff < 64.
        // Real SWOS: player moving straight along their facing → diff ends up
        // near ±128 → add +256 ball-speed boost so the ball runs slightly
        // ahead of the dribbler (this is the "ball at the feet" pin).
        // (Previously this gate was INVERTED — diff in (-64,64) → +256 —
        // which meant the boost almost never fired for a southbound run,
        // making the ball trail the dribbler. Bug found 2026-06-01.)
        short playerFullDir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffFullDirection);
        byte d0Byte = (byte)((playerFullDir & 0xFF) + 128);  // +128 = recenter
        short playerDir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        byte d1Byte = (byte)(((playerDir & 0xFFFF) << 5) & 0xFF);
        sbyte signedDiff = (sbyte)(d0Byte - d1Byte);
        if (signedDiff >= 64 || signedDiff <= -64)
        {
            // cseg_7A517 — speed += 256.
            BallSprite.Speed = (short)(BallSprite.Speed + 256);
        }

        // cseg_7A523 (player.cpp:686-739):
        // player.cpp:687-702 — controlledPlDirection vs player.direction.
        short controlledDir = Memory.ReadSignedWord(a6TeamBase + TeamData.OffControlledPlDirection);
        short pDir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        if (controlledDir == pDir) goto l_reset_ball_spin;

        // player.cpp:704-741 — unkBallTimer++. Compare dseg_17E276[ballControl*2] with timer.
        // If timer >= entry → set wonTheBallTimer = 8.
        short unkBallTimer = Memory.ReadSignedWord(a6TeamBase + TeamData.OffOfs108);
        unkBallTimer++;
        Memory.WriteWord(a6TeamBase + TeamData.OffOfs108, unkBallTimer);

        // Same ballControl skill as above (player.cpp:715 — re-reads
        // PlayerGameHeader.ballControl at [esi+73]).
        int dsegBallControlSkill = 4;
        if (a4PlayerInfo != 0)
        {
            int bc2 = Memory.ReadByte(a4PlayerInfo + TeamDataLoader.OffBallControl);
            if (bc2 >= 0 && bc2 <= 7) dsegBallControlSkill = bc2;
        }
        short entry = Memory.ReadSignedWord(Memory.Addr.dseg_17E276 + dsegBallControlSkill * 2);
        if (entry > unkBallTimer)
        {
            // Counter hasn't crossed — wonTheBallTimer not updated.
        }
        else
        {
            // Counter crossed → wonTheBallTimer = 8.
            // player.cpp:740 writes [esi+138], not AI_timer (+130).
            Memory.WriteWord(a6TeamBase + OffWonTheBallTimer, 8);
        }

    l_reset_ball_spin:;
        // player.cpp:743 — resetBothTeamSpinTimers.
        BallUpdate.ResetBothTeamSpinTimers();
    }


    // ====================================================================
    // playerKickingBall — player.cpp:750-1136
    // ====================================================================
    // Issue a kick from the controlled player. Computes ball.destX/Y from
    // direction-table, sets speed = kBallKickingSpeed, deltaZ = kBallKickingDeltaZ,
    // adds finishing or long-shot speed bonus if appropriate, and clears spin.
    //
    // from player.cpp:750
    public static void PlayerKickingBall(int a6TeamBase, int a1PlayerAddr)
    {
        // player.cpp:752 — stateGoal = 0.
        Memory.WriteWord(Memory.Addr.stateGoal, 0);

        // player.cpp:753-755 — A0 = team.controlledPlayer (use as table base for kPlayerWithBallOffsets).
        int a0CtrlPlayer = Memory.ReadSignedDword(a6TeamBase + TeamData.OffControlledPlayer);

        // player.cpp:756-761 — d0 = d2 = player.direction; team.controlledPlDirection = direction.
        short dir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        Memory.WriteWord(a6TeamBase + TeamData.OffControlledPlDirection, dir);

        // player.cpp:762 — A0 = getBallDestCoordinatesTable() (per-state table).
        int a0DestTable = GetBallDestCoordinatesTable();

        // player.cpp:763-799 — ball.destX/Y = ball.x/y + table[dir*4].
        int dirOff = (dir & 0xFFFF) << 2;
        short tableDx = Memory.ReadSignedWord(a0DestTable + dirOff);
        short tableDy = Memory.ReadSignedWord(a0DestTable + dirOff + 2);
        BallSprite.DestX = (short)(BallSprite.XPixels + tableDx);
        BallSprite.DestY = (short)(BallSprite.YPixels + tableDy);

        // player.cpp:800-805 — ball.speed = kBallKickingSpeed; ball.deltaZ = kBallKickingDeltaZ; reset spin.
        short kickSpeed = Memory.ReadSignedWord(Memory.Addr.kBallKickingSpeed);
        BallSprite.Speed = kickSpeed;
        int kickDz = Memory.ReadSignedDword(Memory.Addr.kBallKickingDeltaZ);
        BallSprite.DeltaZ = kickDz;
        BallUpdate.ResetBothTeamSpinTimers();

        // player.cpp:806-817 — if gameStatePl == ST_GAME_IN_PROGRESS (100) → game_in_progress.
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        if (gameStatePl == 100) goto l_game_in_progress;

        // player.cpp:819-830 — if gameState < ST_THROW_IN_FORWARD_RIGHT (15) → game_in_progress.
        short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);
        if (gameState < 15) goto l_game_in_progress;

        // player.cpp:832-843 — if gameState <= ST_THROW_IN_BACK_LEFT (20) → return.
        if (gameState <= 20) return;

    l_game_in_progress:;
        // player.cpp:846-856 — if A6 == topTeam → left_team.
        bool isTopTeam = (a6TeamBase == TeamData.TopBase);
        if (isTopTeam) goto l_left_team;

        // player.cpp:858-870 — BOTTOM team. ball.y > 342 → not a shot.
        if (BallSprite.YPixels > 342) goto l_not_a_shot_on_goal;

        // player.cpp:872-907 — controlledPlDirection ∈ {0, 1, 7} → possible shot.
        short ctrlDir = Memory.ReadSignedWord(a6TeamBase + TeamData.OffControlledPlDirection);
        if (ctrlDir == 0) goto l_possible_shot_on_goal;
        if (ctrlDir == 1) goto l_possible_shot_on_goal;
        if (ctrlDir == 7) goto l_possible_shot_on_goal;
        goto l_not_a_shot_on_goal;

    l_left_team:;
        // player.cpp:910-922 — TOP team. ball.y < 556 → not a shot.
        if (BallSprite.YPixels < 556) goto l_not_a_shot_on_goal;

        // player.cpp:924-962 — controlledPlDirection ∈ {3, 4, 5} → possible shot.
        short ctrlDirTop = Memory.ReadSignedWord(a6TeamBase + TeamData.OffControlledPlDirection);
        if (ctrlDirTop == 4) goto l_possible_shot_on_goal;
        if (ctrlDirTop == 3) goto l_possible_shot_on_goal;
        if (ctrlDirTop != 5) goto l_not_a_shot_on_goal;

    l_possible_shot_on_goal:;
        // player.cpp:964-977 — if ball.x < 241 → long shot.
        if (BallSprite.XPixels < 241) goto l_its_a_long_shot;
        // player.cpp:979-990 — if ball.x > 431 → long shot.
        if (BallSprite.XPixels > 431) goto l_its_a_long_shot;
        // player.cpp:992-1003 — if ball.y < 204 → finishing shot.
        if (BallSprite.YPixels < 204) goto l_its_a_finishing_shot;
        // player.cpp:1005-1016 — if ball.y < 694 → long shot.
        if (BallSprite.YPixels < 694) goto l_its_a_long_shot;
        // Else → finishing.

    l_its_a_finishing_shot:;
        // player.cpp:1019-1047 — speed += kBallSpeedFinishing[finishing*2].
        // Asm at player.cpp:1023 reads [esi+75] = PlayerGameHeader.finishing
        // (= PlayerInfo +33, see TeamDataLoader.OffFinishing).
        int finishingSkill = 4;
        int playerInfoFin = GetPlayerInfoForSprite(a6TeamBase, a1PlayerAddr);
        if (playerInfoFin != 0)
        {
            int f = Memory.ReadByte(playerInfoFin + TeamDataLoader.OffFinishing);
            if (f >= 0 && f <= 7) finishingSkill = f;
        }
        // OpenSWOS fatigue: exhausted (<10%) shooter loses 1 finishing point too.
        finishingSkill = System.Math.Max(0, finishingSkill - PlayerEnergy.ShotPenalty(a1PlayerAddr));
        short finishBoost = Memory.ReadSignedWord(Memory.Addr.kBallSpeedFinishing + finishingSkill * 2);
        BallSprite.Speed = (short)(BallSprite.Speed + finishBoost);
        goto l_not_a_shot_on_goal;

    l_its_a_long_shot:;
        // player.cpp:1049-1073 — speed += kBallSpeedKicking[shooting*2].
        // Asm at player.cpp:1054 reads [esi+70] = PlayerGameHeader.shooting
        // (= PlayerInfo +28, see TeamDataLoader.OffShooting).
        int shootingSkill = 4;
        int playerInfoShoot = GetPlayerInfoForSprite(a6TeamBase, a1PlayerAddr);
        if (playerInfoShoot != 0)
        {
            int sh = Memory.ReadByte(playerInfoShoot + TeamDataLoader.OffShooting);
            if (sh >= 0 && sh <= 7) shootingSkill = sh;
        }
        // OpenSWOS fatigue: an exhausted (<10% energy) shooter loses 1 shot-power
        // skill point (gated on EffectEnabled inside ShotPenalty). User spec.
        shootingSkill = System.Math.Max(0, shootingSkill - PlayerEnergy.ShotPenalty(a1PlayerAddr));
        short shootBoost = Memory.ReadSignedWord(Memory.Addr.kBallSpeedKicking + shootingSkill * 2);
        BallSprite.Speed = (short)(BallSprite.Speed + shootBoost);

    l_not_a_shot_on_goal:;
        // player.cpp:1076-1114 — if player.playerOrdinal == 1 (keeper) AND
        // direction ∈ {2, 6} (left/right) → skip spin clear.
        short orderTest = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffPlayerOrdinal);
        if (orderTest != 1) goto cseg_7AE0E;

        short dirTest = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        if (dirTest == 2) goto l_play_kick_sample_and_leave;
        if (dirTest == 6) goto l_play_kick_sample_and_leave;

    cseg_7AE0E:;
        // player.cpp:1116-1130 — if opp.goalkeeperSavedCommentTimer < 0 → leave.
        int oppTeam = Memory.ReadSignedDword(a6TeamBase + TeamData.OffOpponentsTeam);
        short oppKeepTimer = Memory.ReadSignedWord(oppTeam + TeamData.OffGoalkeeperSavedCommentTimer);
        if (oppKeepTimer < 0) goto l_play_kick_sample_and_leave;

        // player.cpp:1129-1130 — team.spinTimer = 0.
        Memory.WriteWord(a6TeamBase + TeamData.OffSpinTimer, 0);

    l_play_kick_sample_and_leave:;
        // player.cpp:1133-1135 — team.passInProgress = 0; PlayKickSample.
        Memory.WriteWord(a6TeamBase + TeamData.OffPassInProgress, 0);
        PlayKickSample();
    }


    // ====================================================================
    // playerHittingStaticHeader — player.cpp:1146-1388
    // ====================================================================
    // Standing-header impact. Sets ball deltaZ, kicks ball forward in
    // appropriate direction, applies player's heading skill bonus.
    //
    // from player.cpp:1146
    public static void PlayerHittingStaticHeader(int a6TeamBase, int a1PlayerAddr)
    {
        // player.cpp:1148 — passInProgress = 0.
        Memory.WriteWord(a6TeamBase + TeamData.OffPassInProgress, 0);

        // player.cpp:1150-1161 — if player.animationTable already == staticHeaderHitAnimTable
        // → skip direction-shift block.
        int animTablePtr = Memory.ReadSignedDword(a1PlayerAddr + PlayerSprite.OffAnimTablePtr);
        if (animTablePtr == Memory.Addr.kStaticHeaderHitAnimTableAddr) goto l_set_static_header_anim_table;

        // player.cpp:1163-1178 — D0 = team.currentAllowedDirection - player.direction.
        short curAllowedDir = Memory.ReadSignedWord(a6TeamBase + TeamData.OffCurrentAllowedDirection);
        short pDir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        short d0DirDiff = (short)(curAllowedDir - pDir);
        if (d0DirDiff == 0) goto l_set_static_header_anim_table;

        // player.cpp:1180-1193 — D0 &= 7; if == 4 → set anim table.
        d0DirDiff = (short)(d0DirDiff & 7);
        if (d0DirDiff == 4) goto l_set_static_header_anim_table;

        // player.cpp:1195-1196 — if diff < 4 → turn right; else → turn left (no carry).
        if (d0DirDiff < 4) goto l_turn_player_right;

        // Turn left: pDir = (pDir - 1) & 7; if still wrong, pDir -= 1.
        pDir = (short)((pDir - 1) & 7);
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffDirection, pDir);
        if (pDir == d0DirDiff) goto l_fix_sprite_direction;
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffDirection, (short)(pDir - 1));
        goto l_fix_sprite_direction;

    l_turn_player_right:;
        // player.cpp:1241-1280 — pDir = (pDir + 1) & 7; if still wrong, pDir += 1.
        pDir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        pDir = (short)((pDir + 1) & 7);
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffDirection, pDir);
        if (pDir == d0DirDiff) goto l_fix_sprite_direction;
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffDirection, (short)(pDir + 1));

    l_fix_sprite_direction:;
        // player.cpp:1282-1291 — direction &= 7 (sanity wrap).
        short fixedDir = (short)(Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection) & 7);
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffDirection, fixedDir);

    l_set_static_header_anim_table:;
        // player.cpp:1294 — A0 = staticHeaderHitAnimTable.
        SetPlayerAnimationTableAndPictureIndex(Memory.Addr.kStaticHeaderHitAnimTableAddr, a1PlayerAddr);

        // player.cpp:1296-1304 — D1 = team.currentAllowedDirection or player.direction.
        short ad = Memory.ReadSignedWord(a6TeamBase + TeamData.OffCurrentAllowedDirection);
        short d1Dir;
        if (ad < 0)
        {
            d1Dir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        }
        else
        {
            d1Dir = ad;
        }

        // player.cpp:1310-1311 — team.controlledPlDirection = D1.
        Memory.WriteWord(a6TeamBase + TeamData.OffControlledPlDirection, d1Dir);

        // player.cpp:1312-1346 — destX/Y = ball.x/y + kDefaultDestinations[D1*4].
        int destIdx = (d1Dir & 0xFFFF) << 2;
        short defDx = Memory.ReadSignedWord(Memory.Addr.kDefaultDestinations + destIdx);
        short defDy = Memory.ReadSignedWord(Memory.Addr.kDefaultDestinations + destIdx + 2);
        BallSprite.DestX = (short)(BallSprite.XPixels + defDx);
        BallSprite.DestY = (short)(BallSprite.YPixels + defDy);

        // player.cpp:1347-1348 — ball.speed = kStaticHeaderBallSpeed.
        short headSpeed = Memory.ReadSignedWord(Memory.Addr.kStaticHeaderBallSpeed);
        BallSprite.Speed = headSpeed;

        // player.cpp:1349-1371 — speed += kPlayerHeaderSpeedIncrease[heading*2].
        // Real lookup: A4 = getPlayerPointerFromShirtNumber(team, player) → PlayerInfo;
        // asm reads [esi+71] = PlayerGameHeader.heading. PlayerGameHeader is the
        // PlayerInfo block addressed AFTER A4 -= kTeamGameHeaderSize (42 bytes), so
        // [PlayerGameHeader+71] === [PlayerInfo+29] === TeamDataLoader.OffHeading.
        // Source: external/swos-port/src/game/player.cpp:1349-1362.
        int headingSkill = 4;
        {
            int piAddr = GetPlayerInfoForSprite(a6TeamBase, a1PlayerAddr);
            if (piAddr != 0)
            {
                int hs = Memory.ReadByte(piAddr + TeamDataLoader.OffHeading);
                if (hs >= 0 && hs <= 7) headingSkill = hs;
            }
        }
        short headBoost = Memory.ReadSignedWord(Memory.Addr.kPlayerHeaderSpeedIncrease + headingSkill * 2);
        BallSprite.Speed = (short)(BallSprite.Speed + headBoost);

        // player.cpp:1372-1383 — deltaZ = -(deltaZ) >> 1 (i.e. -|dz|/2 — invert AND halve).
        int curDz = BallSprite.DeltaZ;
        curDz = -curDz;
        curDz = curDz >> 1;  // sar (preserves sign).
        BallSprite.DeltaZ = curDz;

        // player.cpp:1385-1387 — player.heading = 1; PlayKickSample; resetBothTeamSpinTimers.
        Memory.WriteWord(a1PlayerAddr + 98 /*heading*/, 1);
        PlayKickSample();
        BallUpdate.ResetBothTeamSpinTimers();
    }


    // ====================================================================
    // playerHittingJumpHeader — player.cpp:1396-1671
    // ====================================================================
    // Flying or lob header impact. Picks lob vs flying based on controls
    // direction relative to player direction.
    //
    // from player.cpp:1396
    public static void PlayerHittingJumpHeader(int a6TeamBase, int a1PlayerAddr)
    {
        // player.cpp:1399 — passInProgress = 0.
        Memory.WriteWord(a6TeamBase + TeamData.OffPassInProgress, 0);

        // player.cpp:1400-1410 — D1 = team.currentAllowedDirection or player.direction.
        short ad = Memory.ReadSignedWord(a6TeamBase + TeamData.OffCurrentAllowedDirection);
        short d1Dir;
        if (ad >= 0)
        {
            d1Dir = ad;
        }
        else
        {
            d1Dir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        }

    // l_set_player_z_and_ball_speed:
        // player.cpp:1413-1421 — ball.deltaZ = kBallJumpHeaderDeltaZ; speed = player.speed.
        int jumpDz = Memory.ReadSignedDword(Memory.Addr.kBallJumpHeaderDeltaZ);
        BallSprite.DeltaZ = jumpDz;
        short pSpeed = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffSpeed);
        BallSprite.Speed = pSpeed;

        // player.cpp:1422-1437 — ball.speed += player.speed >> 2.
        short bumpSpeed = (short)(pSpeed >> 2);
        BallSprite.Speed = (short)(BallSprite.Speed + bumpSpeed);

        // player.cpp:1438-1447 — D0 = player.direction - D1.
        short pDir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        short d0DirDiff = (short)(pDir - d1Dir);

        // player.cpp:1448-1454 — if team.currentAllowedDirection < 0 → static header path.
        ad = Memory.ReadSignedWord(a6TeamBase + TeamData.OffCurrentAllowedDirection);
        if (ad < 0) goto l_do_static_header;

        // player.cpp:1456-1465 — D0 &= 7; if == 0 → use_allowed_direction.
        d0DirDiff = (short)(d0DirDiff & 7);
        if (d0DirDiff == 0) goto l_use_allowed_direction;

        // player.cpp:1466-1475 — if D0 == 4 → lob_header.
        if (d0DirDiff == 4) goto l_lob_header;

        // player.cpp:1477-1486 — if D0 == 1 → aim_left.
        if (d0DirDiff == 1) goto l_aim_left;

        // player.cpp:1488-1497 — if D0 == 7 → aim_right.
        if (d0DirDiff == 7) goto l_aim_right;

        // player.cpp:1499-1508 — if D0 == 2 → left_held.
        if (d0DirDiff == 2) goto l_left_held;

        // player.cpp:1510-1519 — if D0 == 6 → right_held.
        if (d0DirDiff == 6) goto l_right_held;

        // player.cpp:1521-1530 — if D0 == 3 → down_left_held.
        if (d0DirDiff == 3) goto l_down_left_held;

        // Else (D0 == 5) — doLobHeader + aim_right.
        // a1PlayerAddr threaded through so the inner
        // setPlayerJumpHeaderHitAnimationTable can read player.frameSwitchCounter
        // and flip kJumpHeaderHitAnimTable. Asm: A1 stays live across the calls.
        // Source: external/swos-port/src/game/player.cpp:1532-1553.
        DoLobHeader(a1PlayerAddr);
        goto l_aim_right;

    l_down_left_held:;
        DoLobHeader(a1PlayerAddr);
        goto l_aim_left;

    l_right_held:;
        DoFlyingHeader(a1PlayerAddr);
        goto l_aim_right;

    l_left_held:;
        DoFlyingHeader(a1PlayerAddr);
        goto l_aim_left;

    l_do_static_header:;
        DoFlyingHeader(a1PlayerAddr);
        goto l_use_allowed_direction;

    l_lob_header:;
        DoLobHeader(a1PlayerAddr);
        goto l_use_allowed_direction;

    l_aim_right:;
        // player.cpp:1555-1568 — D0 = player.direction + 1.
        d0DirDiff = (short)(Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection) + 1);
        goto l_update_player_direction;

    l_aim_left:;
        // player.cpp:1570-1583 — D0 = player.direction - 1.
        d0DirDiff = (short)(Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection) - 1);
        goto l_update_player_direction;

    l_use_allowed_direction:;
        // player.cpp:1585-1588 — D0 = player.direction (uses player.direction not team's).
        d0DirDiff = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);

    l_update_player_direction:;
        // player.cpp:1590-1597 — direction &= 7; team.controlledPlDirection = direction.
        d0DirDiff = (short)(d0DirDiff & 7);
        Memory.WriteWord(a6TeamBase + TeamData.OffControlledPlDirection, d0DirDiff);

        // player.cpp:1598-1632 — ball.destX/Y = ball.x/y + kDefaultDestinations[D0*4].
        int destIdx = (d0DirDiff & 0xFFFF) << 2;
        short defDx = Memory.ReadSignedWord(Memory.Addr.kDefaultDestinations + destIdx);
        short defDy = Memory.ReadSignedWord(Memory.Addr.kDefaultDestinations + destIdx + 2);
        BallSprite.DestX = (short)(BallSprite.XPixels + defDx);
        BallSprite.DestY = (short)(BallSprite.YPixels + defDy);

        // player.cpp:1633-1656 — speed += kPlayerHeaderSpeedIncrease[heading*2].
        // Real lookup: PlayerGameHeader.heading at +71 === PlayerInfo+29 (see
        // playerHittingStaticHeader counterpart above for derivation).
        // Source: external/swos-port/src/game/player.cpp:1633-1646.
        int headingSkill = 4;
        {
            int piAddr = GetPlayerInfoForSprite(a6TeamBase, a1PlayerAddr);
            if (piAddr != 0)
            {
                int hs = Memory.ReadByte(piAddr + TeamDataLoader.OffHeading);
                if (hs >= 0 && hs <= 7) headingSkill = hs;
            }
        }
        short headBoost = Memory.ReadSignedWord(Memory.Addr.kPlayerHeaderSpeedIncrease + headingSkill * 2);
        BallSprite.Speed = (short)(BallSprite.Speed + headBoost);

        // player.cpp:1657-1666 — player.speed >>= 1; player.heading = 1.
        short ps = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffSpeed);
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffSpeed, (short)((ushort)ps >> 1));
        Memory.WriteWord(a1PlayerAddr + 98 /*heading*/, 1);

        // player.cpp:1668-1670 — PlayKickSample; playHeaderComment; ResetSpinTimers.
        PlayKickSample();
        PlayHeaderComment(a6TeamBase);
        BallUpdate.ResetBothTeamSpinTimers();
    }


    // ====================================================================
    // playerTackledTheBallStrong — player.cpp:1684-1966
    // ====================================================================
    // Strong tackle: ball direction shifts by player.direction±1 if controls
    // diverge, ball.destX/Y set far in chosen direction, speed = 125% for human
    // / 100% CPU, player.speed = 50%, player.tackleState = TS_TACKLING_THE_BALL.
    // If opponent's controlled player is >9u ball-distance AND >32u player-distance,
    // it's a TS_GOOD_TACKLE (with comment played).
    //
    // from player.cpp:1684
    public static void PlayerTackledTheBallStrong(int a6TeamBase, int a1PlayerAddr)
    {
        // player.cpp:1687-1697 — D1 = team.currentAllowedDirection or player.direction.
        short ad = Memory.ReadSignedWord(a6TeamBase + TeamData.OffCurrentAllowedDirection);
        short d1Dir;
        if (ad < 0)
        {
            d1Dir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        }
        else
        {
            d1Dir = ad;
        }

    // l_current_direction_allowed:
        // player.cpp:1699-1715 — D0 = player.direction - D1.
        short pDir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        short d0DirDiff = (short)(pDir - d1Dir);
        if (d0DirDiff == 0) goto l_set_controlled_player_direction;

        // player.cpp:1717-1733 — D0 &= 7; if == 4 → same/opposite; else branch.
        d0DirDiff = (short)(d0DirDiff & 7);
        if (d0DirDiff == 4) goto l_set_controlled_player_direction;
        if (d0DirDiff < 4) goto l_controls_leaning_leftward;

        // Right lean: D0 = player.direction + 1.
        d0DirDiff = (short)(Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection) + 1);
        goto l_set_new_direction;

    l_controls_leaning_leftward:;
        // player.cpp:1748-1761 — D0 = player.direction - 1.
        d0DirDiff = (short)(Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection) - 1);
        goto l_set_new_direction;

    l_set_controlled_player_direction:;
        // player.cpp:1763-1767 — D0 = player.direction.
        d0DirDiff = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);

    l_set_new_direction:;
        // player.cpp:1768-1775 — D0 &= 7; team.controlledPlDirection = D0.
        d0DirDiff = (short)(d0DirDiff & 7);
        Memory.WriteWord(a6TeamBase + TeamData.OffControlledPlDirection, d0DirDiff);

        // player.cpp:1776-1807 — destX/Y = ball.x/y + kDefaultDestinations[D0*4].
        int destIdx = (d0DirDiff & 0xFFFF) << 2;
        short defDx = Memory.ReadSignedWord(Memory.Addr.kDefaultDestinations + destIdx);
        short defDy = Memory.ReadSignedWord(Memory.Addr.kDefaultDestinations + destIdx + 2);
        BallSprite.DestX = (short)(BallSprite.XPixels + defDx);
        BallSprite.DestY = (short)(BallSprite.YPixels + defDy);

        // player.cpp:1808-1814 — if team.playerNumber == 0 (CPU) → player_not_cpu (yes, inverted).
        short pNum = Memory.ReadSignedWord(a6TeamBase + TeamData.OffPlayerNumber);
        if (pNum != 0) goto l_player_not_cpu;

        // CPU path (player.cpp:1816-1824) — ball.speed = player.speed; halve player.speed below.
        short cpuSpeed = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffSpeed);
        BallSprite.Speed = cpuSpeed;
        goto l_halve_player_speed;

    l_player_not_cpu:;
        // player.cpp:1826-1860 — Human player. ball.speed = player.speed + (player.speed >> 2)
        // applied TWICE (i.e. 125% × 125% / 100% ≈ 156% — but as asm 1.25 × source).
        short pSp = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffSpeed);
        short d0Shr2 = (short)((ushort)pSp >> 2);
        pSp = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffSpeed);
        d0Shr2 = (short)(d0Shr2 + pSp);
        BallSprite.Speed = d0Shr2;
        // Second pass.
        pSp = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffSpeed);
        d0Shr2 = (short)((ushort)pSp >> 2);
        pSp = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffSpeed);
        d0Shr2 = (short)(d0Shr2 + pSp);
        BallSprite.Speed = d0Shr2;

    l_halve_player_speed:;
        // player.cpp:1862-1870 — player.speed >>= 1; tackleState = 1 (TS_TACKLING_THE_BALL).
        short pSpFinal = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffSpeed);
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffSpeed, (short)((ushort)pSpFinal >> 1));
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffTackleState, 1);

        // player.cpp:1871-1899 — if opp.controlledPlayer is null OR ballDistance < 9 → out.
        int oppTeam = Memory.ReadSignedDword(a6TeamBase + TeamData.OffOpponentsTeam);
        int oppCtrl = Memory.ReadSignedDword(oppTeam + TeamData.OffControlledPlayer);
        if (oppCtrl == 0) goto l_out_strong;
        int oppBallDist = Memory.ReadSignedDword(oppCtrl + PlayerSprite.OffBallDistance);
        if (oppBallDist < 9) goto l_out_strong;

        // player.cpp:1901-1957 — distance² between players. If > 32 → TS_GOOD_TACKLE.
        short px = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffX + 2);
        short ox = Memory.ReadSignedWord(oppCtrl + PlayerSprite.OffX + 2);
        short dx = (short)(px - ox);
        int dxSq = (int)dx * (int)dx;
        short py = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffY + 2);
        short oy = Memory.ReadSignedWord(oppCtrl + PlayerSprite.OffY + 2);
        short dy = (short)(py - oy);
        int dySq = (int)dy * (int)dy;
        int distSq = dxSq + dySq;
        if (distSq <= 32) goto l_out_strong;

        PlayGoodTackleComment();
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffTackleState, 2);  // TS_GOOD_TACKLE

    l_out_strong:;
        PlayKickSample();
        BallUpdate.ResetBothTeamSpinTimers();
    }


    // ====================================================================
    // playerTackledTheBallWeak — player.cpp:1974-2231
    // ====================================================================
    // Weak tackle: similar to Strong but speeds are different (50%/75%) and
    // player.tackleState = TS_TACKLING_THE_BALL even on good tackle (no
    // PlayGoodTackleComment).
    //
    // from player.cpp:1974
    public static void PlayerTackledTheBallWeak(int a6TeamBase, int a1PlayerAddr)
    {
        // player.cpp:1976 — passInProgress = 0.
        Memory.WriteWord(a6TeamBase + TeamData.OffPassInProgress, 0);

        // player.cpp:1977-1988 — D1 = team.currentAllowedDirection or player.direction.
        short ad = Memory.ReadSignedWord(a6TeamBase + TeamData.OffCurrentAllowedDirection);
        short d1Dir;
        if (ad < 0)
        {
            d1Dir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        }
        else
        {
            d1Dir = ad;
        }

    // l_controls_something:
        // player.cpp:1990-2006 — D0 = player.direction - D1.
        short pDir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        short d0DirDiff = (short)(pDir - d1Dir);
        if (d0DirDiff == 0) goto l_tackling_in_same_or_oposite_direction;

        // player.cpp:2008-2024 — D0 &= 7; if == 4 → same/opposite; else lean.
        d0DirDiff = (short)(d0DirDiff & 7);
        if (d0DirDiff == 4) goto l_tackling_in_same_or_oposite_direction;
        if (d0DirDiff < 4) goto l_strive_left;

        // Right strive: D0 = player.direction + 1.
        d0DirDiff = (short)(Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection) + 1);
        goto l_set_new_ball_direction_and_speed;

    l_strive_left:;
        // player.cpp:2039-2052 — D0 = player.direction - 1.
        d0DirDiff = (short)(Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection) - 1);
        goto l_set_new_ball_direction_and_speed;

    l_tackling_in_same_or_oposite_direction:;
        // player.cpp:2054-2058 — D0 = player.direction.
        d0DirDiff = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);

    l_set_new_ball_direction_and_speed:;
        // player.cpp:2059-2066 — D0 &= 7; team.controlledPlDirection = D0.
        d0DirDiff = (short)(d0DirDiff & 7);
        Memory.WriteWord(a6TeamBase + TeamData.OffControlledPlDirection, d0DirDiff);

        // player.cpp:2067-2098 — destX/Y = ball.x/y + kDefaultDestinations[D0*4].
        int destIdx = (d0DirDiff & 0xFFFF) << 2;
        short defDx = Memory.ReadSignedWord(Memory.Addr.kDefaultDestinations + destIdx);
        short defDy = Memory.ReadSignedWord(Memory.Addr.kDefaultDestinations + destIdx + 2);
        BallSprite.DestX = (short)(BallSprite.XPixels + defDx);
        BallSprite.DestY = (short)(BallSprite.YPixels + defDy);

        // player.cpp:2099-2135 — player.speed -= (speed>>1)|1; ball.speed = 1.5 * player.speed.
        short pSp = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffSpeed);
        short halfDecrement = (short)((ushort)pSp >> 1);
        pSp = (short)(pSp - halfDecrement);
        pSp = (short)(pSp | 1);
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffSpeed, pSp);

        // Now player.speed = pSp. ball.speed = pSp + (pSp >> 1).
        short halfP = (short)((ushort)pSp >> 1);
        short ballNewSpeed = (short)(halfP + pSp);
        BallSprite.Speed = ballNewSpeed;

        // player.cpp:2137 — tackleState = TS_TACKLING_THE_BALL (1).
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffTackleState, 1);

        // player.cpp:2138-2153 — if opp.controlledPlayer is null OR ballDist<9 → out.
        int oppTeam = Memory.ReadSignedDword(a6TeamBase + TeamData.OffOpponentsTeam);
        int oppCtrl = Memory.ReadSignedDword(oppTeam + TeamData.OffControlledPlayer);
        if (oppCtrl == 0) goto l_out_weak;
        int oppBallDist = Memory.ReadSignedDword(oppCtrl + PlayerSprite.OffBallDistance);
        if (oppBallDist < 9) goto l_out_weak;

        // player.cpp:2168-2223 — distance² check. >32 → TS_GOOD_TACKLE.
        short px = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffX + 2);
        short ox = Memory.ReadSignedWord(oppCtrl + PlayerSprite.OffX + 2);
        short dx = (short)(px - ox);
        int dxSq = (int)dx * (int)dx;
        short py = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffY + 2);
        short oy = Memory.ReadSignedWord(oppCtrl + PlayerSprite.OffY + 2);
        short dy = (short)(py - oy);
        int dySq = (int)dy * (int)dy;
        int distSq = dxSq + dySq;
        if (distSq <= 32) goto l_out_weak;

        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffTackleState, 2);  // TS_GOOD_TACKLE

    l_out_weak:;
        PlayKickSample();
        BallUpdate.ResetBothTeamSpinTimers();
    }


    // ====================================================================
    // doFlyingHeader — player.cpp:2530-2554
    // ====================================================================
    // Sets ball.deltaZ = kHeaderLowJumpHeight, reduces ball speed by 25%, then
    // SetPlayerJumpHeaderHitAnimationTable.
    //
    // from player.cpp:2530
    // Param `a1PlayerAddr` added per audit (2026-06-01): the asm body reads
    // global A1 = player sprite both in the caller (`playerHittingJumpHeader`,
    // player.cpp:1396-1670) AND inside `setPlayerJumpHeaderHitAnimationTable`
    // (player.cpp:3427-3444). Dropping the address (old C# stub passed 0) made
    // the entire jump-header animation update silently no-op.
    // Source: external/swos-port/src/game/player.cpp:2530-2554.
    public static void DoFlyingHeader(int a1PlayerAddr)
    {
        // player.cpp:2532-2534 — ball.deltaZ = kHeaderLowJumpHeight.
        int dz = Memory.ReadSignedDword(Memory.Addr.kHeaderLowJumpHeight);
        BallSprite.DeltaZ = dz;

        // player.cpp:2535-2552 — speed -= speed >> 2 (i.e. 75% of original).
        short sp = BallSprite.Speed;
        short shr2 = (short)((ushort)sp >> 2);
        BallSprite.Speed = (short)(sp - shr2);

        // player.cpp:2553 — SetPlayerJumpHeaderHitAnimationTable.
        SetPlayerJumpHeaderHitAnimationTable(a1PlayerAddr);
    }


    // ====================================================================
    // doPass — player.cpp:2560-3123
    // ====================================================================
    // The pass routine. Finds the closest non-controlled player in the chosen
    // direction; if found, sets that player as pass target and computes ball
    // destination + speed by distance bracket. If no candidate, picks a free
    // destination (kDefaultDestinations + state-specific offset).
    //
    // from player.cpp:2560
    public static void DoPass(int a6TeamBase, int a1PlayerAddr)
    {
        // player.cpp:2562-2563 — goodPassSampleCommand = 0; stateGoal = 0.
        Memory.WriteWord(Memory.Addr.goodPassSampleCommand, 0);
        Memory.WriteWord(Memory.Addr.stateGoal, 0);

        // player.cpp:2564-2566 — A0 = team.controlledPlayer.
        int a0CtrlPlayer = Memory.ReadSignedDword(a6TeamBase + TeamData.OffControlledPlayer);

        // player.cpp:2567-2572 — D0 = D7 = player.direction; team.controlledPlDirection = direction.
        short pDir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        short d7Dir = pDir;
        Memory.WriteWord(a6TeamBase + TeamData.OffControlledPlDirection, pDir);

        // player.cpp:2574-2580 — A4 = PlayerGameHeader of A1; getClosestNonControlledPlayerInDirection.
        // STUB: closest = 0 (none) — falls through to no_closest path.
        int a0Closest = GetClosestNonControlledPlayerInDirection(pDir, a6TeamBase, a1PlayerAddr);
        if (a0Closest == -1) goto l_no_closest_player;

        // player.cpp:2592-2596 — team.passToPlayerPtr = closest; passingBall=1; passingToPlayer=1.
        Memory.WriteDword(a6TeamBase + TeamData.OffPassToPlayerPtr, a0Closest);
        Memory.WriteWord(a6TeamBase + 88 /*passingBall*/, 1);
        Memory.WriteWord(a6TeamBase + 90 /*passingToPlayer*/, 1);

        // player.cpp:2597-2608 — if gameStatePl != ST_GAME_IN_PROGRESS (100) → calculate_pass_to_player_delta_x_y.
        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        if (gameStatePl != 100) goto l_calculate_pass_to_player_delta_x_y;

        // player.cpp:2610-2616 — if team.playerNumber != 0 → calculate_pass_to_player_delta_x_y.
        short pNum = Memory.ReadSignedWord(a6TeamBase + TeamData.OffPlayerNumber);
        if (pNum != 0) goto l_calculate_pass_to_player_delta_x_y;

        // player.cpp:2618-2650 — AI failed-pass check.
        // D7 = (currentGameTick & 0x1E) >> 1; chance = kAIFailedPassChance[passing*2].
        // If D7 >= chance → calculate_pass_to_player_delta_x_y (good pass).
        // Real lookup: A4 = getPlayerPointerFromShirtNumber(team, player);
        // asm reads [esi+70] = PlayerGameHeader.passing. PlayerGameHeader+70
        // === PlayerInfo+27 === TeamDataLoader.OffPassing (player skills block).
        // Source: external/swos-port/src/game/player.cpp:2574-2625.
        int passingSkill = 4;
        {
            int piAddrPass = GetPlayerInfoForSprite(a6TeamBase, a1PlayerAddr);
            if (piAddrPass != 0)
            {
                int ps = Memory.ReadByte(piAddrPass + TeamDataLoader.OffPassing);
                if (ps >= 0 && ps <= 7) passingSkill = ps;
            }
        }
        ushort tick = Memory.ReadWord(Memory.Addr.currentGameTick);
        int d7Rnd = (tick & 0x1E) >> 1;
        short chance = Memory.ReadSignedWord(Memory.Addr.kAIFailedPassChance + passingSkill * 2);
        if (d7Rnd >= chance) goto l_calculate_pass_to_player_delta_x_y;

        // player.cpp:2652-2742 — Failed pass: destX/Y = target + dx/dy where
        // dx/dy = (target - ball) * either (>>1) or 1 then << 5, depending on
        // currentGameTick & 0x20.
        Memory.WriteWord(Memory.Addr.goodPassSampleCommand, -2);
        // A1 = ballSprite (set in asm; not used by C# port as we access via BallSprite.*).

        // dx = target.x - ball.x
        short txp = Memory.ReadSignedWord(a0Closest + PlayerSprite.OffX + 2);
        short bxp = BallSprite.XPixels;
        short dx = (short)(txp - bxp);
        bool jitter1 = (Memory.ReadByte(Memory.Addr.currentGameTick) & 0x20) != 0;
        if (!jitter1)
        {
            dx = (short)(dx >> 1);  // sar
        }
        dx = (short)(dx << 5);
        BallSprite.DestX = (short)(BallSprite.XPixels + dx);

        // dy = target.y - ball.y
        short typ = Memory.ReadSignedWord(a0Closest + PlayerSprite.OffY + 2);
        short byp = BallSprite.YPixels;
        short dy = (short)(typ - byp);
        bool jitter2 = (Memory.ReadByte(Memory.Addr.currentGameTick) & 0x20) != 0;
        if (jitter2)
        {
            dy = (short)(dy >> 1);  // sar
        }
        dy = (short)(dy << 5);
        BallSprite.DestY = (short)(BallSprite.YPixels + dy);
        goto l_determine_ball_speed;

    l_calculate_pass_to_player_delta_x_y:;
        // player.cpp:2744-2856 — D1 = target.x - ball.x; D2 = target.y - ball.y.
        // Then doubling loop until destX/Y land outside playable area.
        short ctxp = Memory.ReadSignedWord(a0Closest + PlayerSprite.OffX + 2);
        short cbxp = BallSprite.XPixels;
        short d1Dx = (short)(ctxp - cbxp);
        short ctyp = Memory.ReadSignedWord(a0Closest + PlayerSprite.OffY + 2);
        short cbyp = BallSprite.YPixels;
        short d2Dy = (short)(ctyp - cbyp);
        if (d1Dx == 0 && d2Dy == 0)
        {
            d1Dx = 1;
        }

        // l_increase_distances_loop (player.cpp:2786-2856):
    l_increase_distances_loop:;
        short testDestX = (short)(BallSprite.XPixels + d1Dx);
        if (testDestX < 0) goto l_set_dest_x_y;
        if (testDestX >= 672) goto l_set_dest_x_y;

        short testDestY = (short)(BallSprite.YPixels + d2Dy);
        if (testDestY < 0) goto l_set_dest_x_y;
        if (testDestY >= 880) goto l_set_dest_x_y;

        d1Dx = (short)(d1Dx << 1);
        d2Dy = (short)(d2Dy << 1);
        goto l_increase_distances_loop;

    l_set_dest_x_y:;
        BallSprite.DestX = (short)(BallSprite.XPixels + d1Dx);
        BallSprite.DestY = (short)(BallSprite.YPixels + d2Dy);

    l_determine_ball_speed:;
        // player.cpp:2883-2992 — pick speed by ballDistance (squared).
        short d1Sp;
        int dist = Memory.ReadSignedDword(a0Closest + PlayerSprite.OffBallDistance);
        if (dist < 2500) { d1Sp = Memory.ReadSignedWord(Memory.Addr.kPassingSpeedCloserThan2500); goto l_set_ball_speed; }
        if (dist < 10000) { d1Sp = Memory.ReadSignedWord(Memory.Addr.kPassingSpeed_2500_10000); goto l_set_ball_speed; }
        if (dist < 22500) { d1Sp = Memory.ReadSignedWord(Memory.Addr.kPassingSpeed_10000_22500); goto l_set_ball_speed; }
        if (dist < 40000) { d1Sp = Memory.ReadSignedWord(Memory.Addr.kPassingSpeed_22500_40000); goto l_set_ball_speed; }
        if (dist < 62500) { d1Sp = Memory.ReadSignedWord(Memory.Addr.kPassingSpeed_40000_62500); goto l_set_ball_speed; }
        if (dist < 90000) { d1Sp = Memory.ReadSignedWord(Memory.Addr.kPassingSpeed_62500_90000); goto l_set_ball_speed; }
        if (dist < 122500) { d1Sp = Memory.ReadSignedWord(Memory.Addr.kPassingSpeed_90000_122500); goto l_set_ball_speed; }
        d1Sp = Memory.ReadSignedWord(Memory.Addr.kPassingSpeedFurtherThan122500);

    l_set_ball_speed:;
        // player.cpp:2994-3020 — ball.speed = D1 + kBallSpeedPassingIncrease[passing*2].
        int passingSkill2 = 4;
        short incr = Memory.ReadSignedWord(Memory.Addr.kBallSpeedPassingIncrease + passingSkill2 * 2);
        BallSprite.Speed = (short)(d1Sp + incr);
        Memory.WriteWord(Memory.Addr.goodPassSampleCommand, -1);
        goto l_reset_spin_timers;

    l_no_closest_player:;
        // player.cpp:3023-3063 — no candidate — kick toward kDefaultDestinations[D7*4]
        // (or per-state table).
        int a0DestTable = GetBallDestCoordinatesTable();
        int destIdx2 = (d7Dir & 0xFFFF) << 2;
        short defDx = Memory.ReadSignedWord(a0DestTable + destIdx2);
        short defDy = Memory.ReadSignedWord(a0DestTable + destIdx2 + 2);
        BallSprite.DestX = (short)(BallSprite.XPixels + defDx);
        BallSprite.DestY = (short)(BallSprite.YPixels + defDy);
        short freeSp = Memory.ReadSignedWord(Memory.Addr.kFreePassReleasingBallSpeed);
        BallSprite.Speed = freeSp;

    l_reset_spin_timers:;
        // player.cpp:3066 — resetBothTeamSpinTimers.
        BallUpdate.ResetBothTeamSpinTimers();

        // player.cpp:3067-3076 — if team.playerNumber == 0 (CPU) → player_passing
        // skipping spinTimer = 0.
        short pNum2 = Memory.ReadSignedWord(a6TeamBase + TeamData.OffPlayerNumber);
        if (pNum2 == 0) goto l_player_passing;

        // Human player — clear spinTimer.
        Memory.WriteWord(a6TeamBase + TeamData.OffSpinTimer, 0);

    l_player_passing:;
        // player.cpp:3079-3080 — team.passInProgress = 1.
        Memory.WriteWord(a6TeamBase + TeamData.OffPassInProgress, 1);

        // player.cpp:3081-3118 — if gameStatePl == ST_GAME_IN_PROGRESS (100)
        // OR gameState < 15 OR gameState > 20 → play kick & pass samples.
        short gameStatePlEnd = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        if (gameStatePlEnd == 100) goto l_play_kick_and_pass_samples;
        short gameStateEnd = Memory.ReadSignedWord(Memory.Addr.gameState);
        if (gameStateEnd < 15) goto l_play_kick_and_pass_samples;
        if (gameStateEnd <= 20) return;

    l_play_kick_and_pass_samples:;
        // player.cpp:3121-3122 — PlayKickSample; PlayStopGoodPassSampleIfNeeded.
        PlayKickSample();
        PlayStopGoodPassSampleIfNeeded();
    }


    // ====================================================================
    // setPlayerDowntimeAfterTackle — player.cpp:3132-3170
    // ====================================================================
    // Indexes kPlayerTacklingDownTime (human) or kComputerTacklingDownTime (CPU)
    // by tackling skill and writes to player.playerDownTimer.
    //
    // from player.cpp:3132
    public static void SetPlayerDowntimeAfterTackle(int a6TeamBase, int a1PlayerAddr)
    {
        // player.cpp:3138-3148 — if player.tacklingTimer == -1 → CPU path (kComputerTacklingDownTime).
        short tackleTimer = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffTacklingTimer);
        int a0Table;
        if (tackleTimer == -1)
        {
            a0Table = Memory.Addr.kComputerTacklingDownTime;
        }
        else
        {
            a0Table = Memory.Addr.kPlayerTacklingDownTime;
        }

        // player.cpp:3152-3169 — playerDownTimer = table[tackling*2].
        // Real lookup: A4 = getPlayerPointerFromShirtNumber(team, player);
        // asm reads [esi+72] = PlayerGameHeader.tackling. PlayerGameHeader+72
        // === PlayerInfo+30 === TeamDataLoader.OffTackling.
        // Source: external/swos-port/src/game/player.cpp:3134-3154.
        int tacklingSkill = 4;
        {
            int piAddr = GetPlayerInfoForSprite(a6TeamBase, a1PlayerAddr);
            if (piAddr != 0)
            {
                int ts = Memory.ReadByte(piAddr + TeamDataLoader.OffTackling);
                if (ts >= 0 && ts <= 7) tacklingSkill = ts;
            }
        }
        short downTime = Memory.ReadSignedWord(a0Table + tacklingSkill * 2);
        Memory.WriteByte(a1PlayerAddr + PlayerSprite.OffPlayerDownTimer, downTime);
    }


    // ====================================================================
    // setJumpHeaderHitAnimTable — player.cpp:3175-3243
    // ====================================================================
    // Conditionally sets the jump-header-hit animation table. Multiple guards:
    // existing anim must not already be jumpHeaderHitAnimTable, player.heading
    // must be 0, downTimer must equal 40, frameSwitchCounter must be > 2, and
    // (currentTick+1) bit 1 must be set.
    //
    // from player.cpp:3175
    public static void SetJumpHeaderHitAnimTable(int a1PlayerAddr)
    {
        // player.cpp:3177-3188 — if animTable already == jumpHeaderHitAnimTable → out.
        int animTable = Memory.ReadSignedDword(a1PlayerAddr + PlayerSprite.OffAnimTablePtr);
        if (animTable == Memory.Addr.kJumpHeaderHitAnimTableAddr) return;

        // player.cpp:3190-3195 — if player.heading != 0 → out.
        short heading = Memory.ReadSignedWord(a1PlayerAddr + 98 /*heading*/);
        if (heading != 0) return;

        // player.cpp:3197-3207 — if playerDownTimer != 40 → out.
        byte dt = Memory.ReadByte(a1PlayerAddr + PlayerSprite.OffPlayerDownTimer);
        if (dt != 40) return;

        // player.cpp:3209-3219 — if frameSwitchCounter > 2 → out.
        short fsCounter = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffFrameSwitchCounter);
        if (fsCounter > 2) return;

        // player.cpp:3221-3229 — if (currentTick+1) bit 1 not set → out.
        byte tickHi = Memory.ReadByte(Memory.Addr.currentGameTick + 1);
        if ((tickHi & 2) == 0) return;

        // player.cpp:3231-3242 — A0 = jumpHeaderHitAnimTable; player.speed >>= 1.
        SetPlayerAnimationTableAndPictureIndex(Memory.Addr.kJumpHeaderHitAnimTableAddr, a1PlayerAddr);
        short sp = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffSpeed);
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffSpeed, (short)((ushort)sp >> 1));
    }


    // ====================================================================
    // doLobHeader — player.cpp:3257-3281 (static)
    // ====================================================================
    // Sets ball.deltaZ = kHeaderHighJumpHeight, reduces ball speed by 1/16
    // (vs flying header's 1/4). Then SetPlayerJumpHeaderHitAnimationTable.
    //
    // Param `a1PlayerAddr` added per audit (2026-06-01): same reason as
    // `DoFlyingHeader` above — `setPlayerJumpHeaderHitAnimationTable` reads
    // global A1. Dropping the address made `kJumpHeaderHitAnimTable` never
    // get applied after a lob-header impact.
    // Source: external/swos-port/src/game/player.cpp:3257-3281.
    public static void DoLobHeader(int a1PlayerAddr)
    {
        // player.cpp:3259-3261 — ball.deltaZ = kHeaderHighJumpHeight.
        int dz = Memory.ReadSignedDword(Memory.Addr.kHeaderHighJumpHeight);
        BallSprite.DeltaZ = dz;

        // player.cpp:3262-3279 — speed -= speed >> 4 (i.e. 93.75% of original).
        short sp = BallSprite.Speed;
        short shr4 = (short)((ushort)sp >> 4);
        BallSprite.Speed = (short)(sp - shr4);

        // player.cpp:3280 — SetPlayerJumpHeaderHitAnimationTable.
        SetPlayerJumpHeaderHitAnimationTable(a1PlayerAddr);
    }


    // ====================================================================
    // getClosestNonControlledPlayerInDirection — player.cpp:3293-3420 (static)
    // ====================================================================
    // Iterate team's sprites (slot 0..10), find the one with smallest
    // ballDistance whose direction is within ±16 of the input direction*32.
    // Skip the controlled player, sent-off players, and non-Normal-state
    // players.
    //
    // Returns: player sprite address, or -1 if none.
    //
    // from player.cpp:3293
    public static int GetClosestNonControlledPlayerInDirection(int d0Dir, int a6TeamBase, int a1PlayerAddr)
    {
        // player.cpp:3295-3309 — D3 = ball.x, D4 = ball.y, D0 = d0Dir << 5.
        short ballX = BallSprite.XPixels;
        short ballY = BallSprite.YPixels;
        int d0Shift = (d0Dir & 0xFFFF) << 5;

        // player.cpp:3310-3312 — A0 = -1, D2 = -1, D7 = 10 (loop counter).
        int a0Best = -1;
        int d2BestDist = -1;
        int loopCount = 10;

        // A2 = team.spritesTable (TeamData +20).
        int spritesTableAddr = Memory.ReadSignedDword(a6TeamBase + TeamData.OffPlayers);

    l_players_loop:;
        // player.cpp:3315-3322 — A1 = spritesTable[i]; spritesTable += 4.
        int a1Slot = Memory.ReadSignedDword(spritesTableAddr);
        spritesTableAddr += 4;

        // player.cpp:3324-3336 — skip if A1 == team.controlledPlayer.
        int ctrlPlayer = Memory.ReadSignedDword(a6TeamBase + TeamData.OffControlledPlayer);
        if (a1Slot == ctrlPlayer) goto l_next_iter;

        // player.cpp:3338-3345 — skip if player.sentAway != 0.
        short sentAway = Memory.ReadSignedWord(a1Slot + PlayerSprite.OffSentAway);
        if (sentAway != 0) goto l_next_iter;

        // player.cpp:3347-3358 — skip if playerState != PL_NORMAL (0).
        byte plState = Memory.ReadByte(a1Slot + PlayerSprite.OffPlayerState);
        if (plState != 0) goto l_next_iter;

        // player.cpp:3360-3391 — D1 = player.fullDirection - d0Shift (byte).
        // Skip if |D1| > 16.
        short fullDir = Memory.ReadSignedWord(a1Slot + PlayerSprite.OffFullDirection);
        sbyte d1Byte = (sbyte)((fullDir & 0xFF) - (d0Shift & 0xFF));
        if (d1Byte < -16) goto l_next_iter;
        if (d1Byte > 16) goto l_next_iter;

        // player.cpp:3393-3406 — D1 = ballDistance. If D1 >= best → skip.
        int ballDist = Memory.ReadSignedDword(a1Slot + PlayerSprite.OffBallDistance);
        if ((uint)ballDist >= (uint)d2BestDist) goto l_next_iter;

        // player.cpp:3408-3411 — best updated.
        d2BestDist = ballDist;
        a0Best = a1Slot;

    l_next_iter:;
        // player.cpp:3414-3419 — dec D7; loop while >= 0.
        loopCount--;
        if (loopCount >= 0) goto l_players_loop;

        return a0Best;
    }


    // ====================================================================
    // setPlayerJumpHeaderHitAnimationTable — player.cpp:3427-3444 (static)
    // ====================================================================
    // Tiny wrapper that only sets the jumpHeaderHitAnimTable when
    // frameSwitchCounter <= 2.
    //
    // from player.cpp:3427
    public static void SetPlayerJumpHeaderHitAnimationTable(int a1PlayerAddr)
    {
        // Defensive: callers may still pass 0 if A1 wasn't wired yet. The asm
        // would dereference esi=0 and crash; in C# we no-op which matches the
        // old behaviour for any unwired call site.
        if (a1PlayerAddr == 0) return;

        // player.cpp:3429-3440 — if frameSwitchCounter > 2 → out.
        // Asm: `cmp [esi+Sprite.frameSwitchCounter], 2; ja short @@out`
        // `ja` is unsigned > — frameSwitchCounter is a small positive word so
        // signed > is equivalent here.
        short fsCounter = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffFrameSwitchCounter);
        if (fsCounter > 2) return;

        // player.cpp:3442-3443 — set jumpHeaderHitAnimTable.
        SetPlayerAnimationTableAndPictureIndex(Memory.Addr.kJumpHeaderHitAnimTableAddr, a1PlayerAddr);
    }


    // ====================================================================
    // SetPlayerAnimationTable — swos.asm:104309-104364
    // ====================================================================
    // Slim sibling of `SetPlayerAnimationTableAndPictureIndex` — same struct
    // lookup but DOES NOT touch imageIndex (caller may still be repositioning
    // the sprite). Used by all the gameLoop / updateSprite / updatePlayers
    // state-transition sites that just need to flip which animation is
    // running.
    //
    // Asm-side semantics (verbatim):
    //   sprite.animationTable = A0;
    //   D0 = sprite.teamNumber - 1;
    //   if (sprite.playerOrdinal == 1) D0 += 2;   // 0/1 player, 2/3 goalie
    //   D0 = (D0 << 3) + sprite.direction;        // 0..31 slot
    //   sprite.frameDelay = *(word *)A0;
    //   sprite.frameIndicesTable = *(dword *)(A0 + 2 + D0 * 4);
    //   if (frameIndicesTable == 0) int 3;        // fatal
    //   sprite.frameSwitchCounter = -1;
    //   sprite.frameIndex          = -1;
    //   sprite.cycleFramesTimer    =  1;
    //   sprite.startingDirection   = sprite.direction;
    //
    // Used by ~38 call-sites; the renderer reads `frameIndicesTable` +
    // `frameIndex` + `frameSwitchCounter` to step through frames.
    //
    // from swos.asm:104309
    public static void SetPlayerAnimationTable(int a1PlayerAddr, int a0AnimTable)
    {
        // 104312-104314 — sprite.animationTable = A0.
        Memory.WriteDword(a1PlayerAddr + PlayerSprite.OffAnimTablePtr, a0AnimTable);

        // 104316-104324 — D0 = (teamNumber - 1); if ordinal == 1 → D0 += 2.
        short teamNum = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffTeamNumber);
        short d0Index = (short)(teamNum - 1);
        short ord = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffPlayerOrdinal);
        if (ord == 1)
        {
            d0Index = (short)(d0Index + 2);
        }

        // 104326-104333 — D0 = (D0 << 3) + direction, then D0 <<= 2 (byte
        // offset into the 32-entry pointer table).
        d0Index = (short)(d0Index << 3);
        short pDir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        d0Index = (short)(d0Index + pDir);
        int d0Off = (d0Index & 0xFFFF) << 2;

        // 104334-104337 — sprite.frameDelay = *(word*)A0.
        short frameDelay = Memory.ReadSignedWord(a0AnimTable);
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffFrameDelay, frameDelay);

        // 104338-104342 — sprite.frameIndicesTable = *(dword*)(A0 + 2 + D0).
        int fitPtr = Memory.ReadSignedDword(a0AnimTable + 2 + d0Off);
        Memory.WriteDword(a1PlayerAddr + PlayerSprite.OffFrameIndicesTable, fitPtr);

        // 104343-104344 — fatal_error path: animation table has a null pointer
        // at the requested (team, ordinal, direction) slot. The asm `int 3`s
        // here; our port treats it as a soft no-op — leaves the sprite in
        // whatever animation it had before. Used by tables that only carry
        // entries for a subset of groups (e.g. plTacklingAnimTable only has
        // outfielder entries, goalie entries are 0). swos-port hits this when
        // a goalie tries to tackle — original game does not, but our defensive
        // skip protects against bad transitions.
        if (fitPtr == 0)
        {
            // Restore the previous animation table pointer (since we already
            // wrote A0 above) so callers can detect the failure if needed.
            // Actually — the asm leaves animationTable updated; we match.
            return;
        }

        // 104345-104354 — reset cycle bookkeeping + cache startingDirection.
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffFrameSwitchCounter, -1);
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffFrameIndex, -1);
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffCycleFramesTimer, 1);
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffStartingDirection, pDir);
    }


    // ====================================================================
    // setPlayerAnimationTableAndPictureIndex — player.cpp:3450-3558 (static)
    // ====================================================================
    // Sets player.animationTable, plus reads the frameDelay + frameIndicesTable
    // from the animation table indexed by (teamNumber-1 + (ordinal==1?2:0)) * 8
    // + direction. Then computes imageIndex from frameIndex.
    //
    // from player.cpp:3450
    public static void SetPlayerAnimationTableAndPictureIndex(int a0AnimTable, int a1PlayerAddr)
    {
        // player.cpp:3452-3454 — player.animationTable = A0.
        Memory.WriteDword(a1PlayerAddr + PlayerSprite.OffAnimTablePtr, a0AnimTable);

        // player.cpp:3456-3463 — D0 = teamNumber - 1.
        short teamNum = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffTeamNumber);
        short d0Index = (short)(teamNum - 1);

        // player.cpp:3465-3474 — if ordinal == 1 → D0 += 2.
        short ord = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffPlayerOrdinal);
        if (ord == 1)
        {
            d0Index = (short)(d0Index + 2);
        }

        // l_calc_table_index (player.cpp:3483-3499):
        // D0 <<= 3; D0 += player.direction; D0 <<= 2.
        d0Index = (short)(d0Index << 3);
        short pDir = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffDirection);
        d0Index = (short)(d0Index + pDir);
        int d0Off = (d0Index & 0xFFFF) << 2;

        // player.cpp:3500-3508 — frameDelay = [A0]; frameIndicesTable = [A0 + d0Off + 2].
        // STUB: animation table layout not directly addressable in the C# port
        // (the original is a packed struct at a specific address). For now we
        // do the reads but the table base may be outside our Memory.* allocation.
        if (a0AnimTable < 0 || a0AnimTable > 0x60000) return;  // Bail if outside Memory.

        short fdelay = Memory.ReadSignedWord(a0AnimTable);
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffFrameDelay, fdelay);
        int fitPtr = Memory.ReadSignedDword(a0AnimTable + d0Off + 2);
        Memory.WriteDword(a1PlayerAddr + PlayerSprite.OffFrameIndicesTable, fitPtr);

        // player.cpp:3509-3513 — if frameIndicesTable == 0 → fatal_error.
        if (fitPtr == 0) return;  // STUB: replace with debug break when feasible.

        // player.cpp:3515-3516 — startingDirection = direction.
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffStartingDirection, pDir);

        // player.cpp:3517-3528 — D0 = [frameIndicesTable + frameIndex*2].
        short frameIndex = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffFrameIndex);
        if (fitPtr < 0 || fitPtr > 0x60000) return;  // STUB bail.
        short rawImg = Memory.ReadSignedWord(fitPtr + (frameIndex << 1));
        if (rawImg < 0) return;  // Stay at the existing imageIndex.

        // player.cpp:3535-3547 — imageIndex = D0 + player.frameOffset.
        short frameOff = Memory.ReadSignedWord(a1PlayerAddr + PlayerSprite.OffFrameOffset);
        Memory.WriteWord(a1PlayerAddr + PlayerSprite.OffImageIndex, (short)(rawImg + frameOff));
    }


    // ====================================================================
    // getBallDestCoordinatesTable — player.cpp:3567-3720 (static)
    // ====================================================================
    // Returns the address of the appropriate kBallDestDelta table for the
    // current game state. Tables (left/right throw, penalty, corners, default)
    // are picked by gameState + foulX/foulY.
    //
    // from player.cpp:3567
    public static int GetBallDestCoordinatesTable()
    {
        short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);

        // player.cpp:3568-3591 — gameState 15..20 → throw-in.
        if (gameState >= 15 && gameState <= 20)
        {
            // player.cpp:3593-3603 — if foulX > 336 → right throw; else → left throw.
            short foulX = Memory.ReadSignedWord(Memory.Addr.foulXCoordinate);
            if (foulX > 336) return Memory.Addr.kRightThrowInBallDestDelta;
            return Memory.Addr.kLeftThrowInBallDestDelta;
        }

        // player.cpp:3612-3623 — gameState 14 (PENALTY) → penalty.
        if (gameState == 14) return Memory.Addr.kPenaltyBallDestDelta;
        // player.cpp:3625-3635 — gameState 31 (PENALTIES) → penalty.
        if (gameState == 31) return Memory.Addr.kPenaltyBallDestDelta;

        // player.cpp:3641-3664 — gameState 4 or 5 → corner.
        if (gameState == 4 || gameState == 5)
        {
            // player.cpp:3666-3712 — foulY > 449 → lower; foulX > 336 → right-side.
            short foulY = Memory.ReadSignedWord(Memory.Addr.foulYCoordinate);
            short foulX2 = Memory.ReadSignedWord(Memory.Addr.foulXCoordinate);
            if (foulY > 449)
            {
                // Lower corner.
                if (foulX2 > 336) return Memory.Addr.kLowerRightCornerBallDestDelta;
                return Memory.Addr.kLowerLeftCornerBallDestDelta;
            }
            // Upper corner.
            if (foulX2 > 336) return Memory.Addr.kUpperRightCornerBallDestDelta;
            return Memory.Addr.kUpperLeftCornerBallDestDelta;
        }

        // l_not_corner — default destinations table.
        return Memory.Addr.kDefaultDestinations;
    }


    // ====================================================================
    // playStopGoodPassSampleIfNeeded — player.cpp:3722-3762 (static)
    // ====================================================================
    // Reads goodPassSampleCommand: -1 → enqueue, -2 → stop, 0 → idle.
    //
    // from player.cpp:3722
    public static void PlayStopGoodPassSampleIfNeeded()
    {
        // player.cpp:3724-3734 — if cmd == -1 → enqueue_good_pass_sample.
        short cmd = Memory.ReadSignedWord(Memory.Addr.goodPassSampleCommand);
        if (cmd == -1)
        {
            Memory.WriteWord(Memory.Addr.goodPassSampleCommand, 0);
            EnqueuePlayingGoodPassSample();
            return;
        }

        // player.cpp:3736-3746 — if cmd == -2 → stop_sample.
        if (cmd == -2)
        {
            Memory.WriteWord(Memory.Addr.goodPassSampleCommand, 0);
            StopGoodPassSample();
            return;
        }

        // player.cpp:3748 — nothing to do.
    }


    // ====================================================================
    // stopGoodPassSample — player.cpp:3764-3767 (static)
    // ====================================================================
    // Sets playingGoodPassTimer = -1.
    //
    // from player.cpp:3764
    public static void StopGoodPassSample()
    {
        Memory.WriteWord(Memory.Addr.playingGoodPassTimer, -1);
        // player.cpp:3764 stopGoodPassSample — cancel the pending good-pass comment.
        OpenSwos.Audio.MatchAudio.CancelGoodPass();
    }


    // ====================================================================
    // enqueuePlayingGoodPassSample — player.cpp:3769-3793 (static)
    // ====================================================================
    // Increments goodPassTimer. When it hits 5, sets playingGoodPassTimer=10
    // and resets goodPassTimer=0. Else sets playingGoodPassTimer=-1.
    //
    // from player.cpp:3769
    public static void EnqueuePlayingGoodPassSample()
    {
        // player.cpp:3771 — playingGoodPassTimer = -1.
        Memory.WriteWord(Memory.Addr.playingGoodPassTimer, -1);

        // player.cpp:3772-3779 — goodPassTimer += 1.
        short timer = Memory.ReadSignedWord(Memory.Addr.goodPassTimer);
        timer = (short)(timer + 1);
        Memory.WriteWord(Memory.Addr.goodPassTimer, timer);

        // player.cpp:3780-3793 — if timer == 5 → set playing=10, timer=0.
        if (timer != 5) return;
        Memory.WriteWord(Memory.Addr.goodPassTimer, 0);
        Memory.WriteWord(Memory.Addr.playingGoodPassTimer, 10);
        // player.cpp:3788-3793 — every 5th good pass arms the 10-tick good-pass
        // comment. The audio layer owns the countdown (CommentaryEngine).
        OpenSwos.Audio.MatchAudio.EnqueueGoodPass();
    }


    // ====================================================================
    // Stubs for callees not yet ported in OpenSwos
    // ====================================================================

    // SWOS::Rand — table-driven xor-stream PRNG. Returns a byte (0..255).
    // player.cpp:399 uses `Rand() & 31` for ball-control tie-break against
    // the kPlAvgTacklingBallControlDiffChance table (one byte per skill
    // diff). Live impl in `SwosVm.Rng`. Source:
    // external/swos-port/src/util/random.cpp.
    private static int SwosRand() => Rng.NextByte();

    // SWOS::PlayKickSample — kick / header / deflect SFX (sfx.cpp:129).
    private static void PlayKickSample() => OpenSwos.Audio.MatchAudio.PlayKick();

    // SWOS::PlayGoodTackleComment — small (yielding) comment (comments.cpp:408).
    private static void PlayGoodTackleComment() => OpenSwos.Audio.MatchAudio.GoodTackleComment();

    // playHeaderComment — small (yielding) comment (comments.cpp:228).
    private static void PlayHeaderComment(int a6TeamBase) => OpenSwos.Audio.MatchAudio.HeaderComment();
}
