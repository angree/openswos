namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Player + keeper update functions ported from external/swos-port/src/game/player.cpp
// and src/game/updatePlayers/updatePlayers.cpp.
//
// Phase B5 in progress. First port: `updateBallWithControllingGoalkeeper`
// (player.cpp:200-265) — pins ball to keeper hand position, damps deltaZ.
public static class PlayerUpdate
{
    // Telemetry counters for keeper-dive trip rate. Bumped from
    // GoalkeeperJumping (the only entry point that actually puts the keeper
    // into PL_GOALIE_DIVING_HIGH / LOW) and from ShouldGoalkeeperDive's
    // true-return path. Useful for tracking regression on the dive bug fix
    // (2026-06-06): a zero count here means the keeper never dives even when
    // the ball flies at goal — a sign that CheckShotAtGoalAndKeeperSave or
    // ShouldGoalkeeperDive is gating incorrectly.
    private static int s_diveCallsHigh = 0;
    private static int s_diveCallsLow = 0;
    private static int s_shouldDiveTrueCount = 0;
    public static int DiveCallsHigh => s_diveCallsHigh;
    public static int DiveCallsLow => s_diveCallsLow;
    public static int ShouldDiveTrueCount => s_shouldDiveTrueCount;
    public static void ResetDiveCounters()
    {
        s_diveCallsHigh = 0;
        s_diveCallsLow = 0;
        s_shouldDiveTrueCount = 0;
    }
    internal static void BumpDiveCounter(byte state)
    {
        if (state == (byte)PortPlayerState.kGoalieDivingHigh) s_diveCallsHigh++;
        else if (state == (byte)PortPlayerState.kGoalieDivingLow) s_diveCallsLow++;
    }
    internal static void BumpShouldDiveTrue() => s_shouldDiveTrueCount++;

    // ====================================================================
    // updateBallWithControllingGoalkeeper — player.cpp:200-265
    // ====================================================================
    //
    // Called from updateBall (ball.cpp ~line 416) when gameState==ST_KEEPER_HOLDS_BALL.
    // Input: `controllingPlayerAddr` = absolute Memory address of the keeper's
    // sprite slot (sourced from `lastTeamPlayedBeforeBreak.controlledPlayer`).
    //
    // **Effect**:
    //   - Ball XY pinned to keeper.x + kBallPlOffsets[direction*4]
    //   - Ball destX/Y set to the same target (so motion code doesn't pull it away)
    //   - Ball speed = 0
    //   - Ball deltaZ = -|deltaZ/2|  (always falling, then ball.cpp check_keeper_z
    //     overrides this to rise/fall toward z=5 per frame).
    //   - Spin timers reset (so no curve carry-over from previous touch).
    //
    // Mechanically equivalent to swos-port's asm: shift direction left 2 (each
    // direction = 4 bytes in offset table), look up dx/dy as 16-bit words, add
    // to player.x.whole / y.whole, write to ball XY+destXY, sar deltaZ by 1
    // then negate if positive (final dz <= 0).
    public static void UpdateBallWithControllingGoalkeeper(int controllingPlayerAddr)
    {
        // player.cpp:202-208 — dir = sprite.direction; byteOffset = dir << 2.
        int dir = Memory.ReadSignedWord(controllingPlayerAddr + PlayerSprite.OffDirection);
        int byteOffset = dir << 2;  // each entry is 4 bytes (dx word + dy word)

        // player.cpp:210-220 — newX = player.x.whole + kBallPlOffsets[byteOffset]
        int playerX = Memory.ReadSignedWord(controllingPlayerAddr + PlayerSprite.OffX + 2);
        int playerY = Memory.ReadSignedWord(controllingPlayerAddr + PlayerSprite.OffY + 2);

        int offsX = Memory.ReadSignedWord(Memory.Addr.kBallPlOffsetsBase + byteOffset);
        int offsY = Memory.ReadSignedWord(Memory.Addr.kBallPlOffsetsBase + byteOffset + 2);

        short newX = (short)(playerX + offsX);
        short newY = (short)(playerY + offsY);

        // player.cpp:232-242 — write ball position + destination + clear speed.
        BallSprite.Speed = 0;
        BallSprite.XPixels = newX;
        BallSprite.YPixels = newY;
        BallSprite.DestX = newX;
        BallSprite.DestY = newY;

        // player.cpp:243-263 — sar deltaZ by 1 (preserves sign), then negate
        // ONLY if the result was positive. Net effect: dz <= 0 always (falling).
        // The ball.cpp:433-456 check_keeper_z then races against this to force
        // dz toward keeper hand height (z=5 px).
        int dz = BallSprite.DeltaZ;
        int dzHalf = dz >> 1;            // arithmetic shift, sign preserved
        if (dzHalf > 0) dzHalf = -dzHalf; // negate only if positive
        BallSprite.DeltaZ = dzHalf;

        // player.cpp:264 — call resetBothTeamSpinTimers.
        BallUpdate.ResetBothTeamSpinTimers();
    }


    // ====================================================================
    // goalkeeperClaimedTheBall — player.cpp:2238-2336
    // ====================================================================
    //
    // Called when keeper successfully catches the ball (after dive landing or
    // direct stop). Transitions match state to "keeper holds ball" stoppage:
    //
    //   - lastPlayerBeforeGoalkeeper / lastTeamScored bookkeeping
    //   - ball.speed = 0
    //   - gameState = ST_KEEPER_HOLDS_BALL (3), breakCameraMode = -1
    //   - foulX/YCoordinate = ball position
    //   - gameStatePl = ST_STOPPED (101), gameNotInProgressCounter = 0
    //   - lastTeamPlayedBeforeBreak = team
    //   - stoppageTimerTotal = stoppageTimerActive = 0
    //   - stopAllPlayers()  ← stops all 22 sprites so nobody jitters ±1px
    //     around a stale dest during the keeper hold
    //   - THEN, only if the keeper is NOT mid-dive:
    //     team.controlledPlayer = keeper, team.ballOutOfPlayOrKeeper = 1,
    //     keeper.direction = cameraDirection (0 bottom / 4 top),
    //     team.goaliePlayingOrOut = 1, updatePlayerWithBall(),
    //     camera velocities zeroed.
    //
    // Mid-dive early-out (player.cpp:2310-2323): if goalkeeperDivingRight or
    // goalkeeperDivingLeft != 0 the claim defers the controlledPlayer block —
    // the dive/catch state handler re-invokes this function with the flag
    // cleared when the animation completes:
    //   - divingRight → l_goalkeeper_still_diving, playerDownTimer <= 42
    //     (updatePlayers.cpp:3212-3242) → TickGoalieDivingClaimCompletion below.
    //   - divingLeft  → catch timer hits 0 (updatePlayers.cpp:3076-3093)
    //     → TickGoalieCatchingBall below.
    //
    // STUBBED in this port:
    //   - forceLeftTeam == 1 → A6 = topTeamData — player.cpp:2282-2294
    //     (forceLeftTeam is never set to 1 in our port; the branch is a no-op)
    // (The playerTurnFlags writes — player.cpp:2257-2258/2262-2263/2274-2279 —
    // were ported 2026-07-02; see the block below.)
    public static void GoalkeeperClaimedTheBall(int keeperSpriteAddr, bool isTopTeam)
    {
        // player.cpp:2240-2241 — lastPlayerBeforeGoalkeeper = lastPlayerPlayed.
        Memory.WriteDword(Memory.Addr.lastPlayerBeforeGoalkeeper,
                          Memory.ReadSignedDword(Memory.Addr.lastPlayerPlayed));
        // player.cpp:2242-2243 — lastTeamScored = lastTeamPlayed.
        Memory.WriteDword(Memory.Addr.lastTeamScored,
                          Memory.ReadSignedDword(Memory.Addr.lastTeamPlayed));

        // player.cpp:2245 — ball.speed = 0
        BallSprite.Speed = 0;

        // player.cpp:2246-2263 — camera direction + playerTurnFlags by team.
        // BOTTOM team keeper (south end) faces NORTH (dir 0).
        // TOP team keeper (north end) faces SOUTH (dir 4).
        //   player.cpp:2257-2258 — top team:    cameraDirection = 4;
        //                          playerTurnFlags = 0x7C (dirs 2..6 — every
        //                          direction pointing AWAY from the top goal).
        //   player.cpp:2262-2263 — bottom team: cameraDirection = 0;
        //                          playerTurnFlags = 0xC7 (dirs 0,1,2,6,7 —
        //                          away from the bottom goal).
        // These masks are what stop the holding keeper from ever FACING (and
        // therefore releasing) into his own net: both the AI turn logic
        // (l_update_turn_direction, updatePlayers.cpp:17182-17227) and the
        // stoppage fire-consume gate (cseg_813DA `test playerTurnFlags, 1<<cad`,
        // updatePlayers.cpp:5288-5306) honour the mask. Previously stubbed —
        // the stale mask from the prior stoppage (often 0xFF) let the keeper
        // release toward his own goal line and instantly re-claim (the
        // "gs=3 re-claim loop"), or concede an own goal outright.
        int cameraDirection = isTopTeam ? 4 : 0;
        Memory.WriteWord(Memory.Addr.cameraDirection, cameraDirection);
        Memory.WriteByte(Memory.Addr.playerTurnFlags, (byte)(isTopTeam ? 0x7C : 0xC7));

        // player.cpp:2266-2279 — CPU team (playerNumber == 0):
        //   `and byte ptr playerTurnFlags, 10111011b` (0xBB) — additionally
        // clears the pure east/west directions, so the CPU keeper's release
        // always has an infield (vertical) component:
        //   top:    0x7C & 0xBB = 0x38 → dirs 3,4,5 (SE,S,SW)
        //   bottom: 0xC7 & 0xBB = 0x83 → dirs 7,0,1 (NW,N,NE)
        {
            int teamBaseTf = isTopTeam ? TeamData.TopBase : TeamData.BottomBase;
            short pnTf = Memory.ReadSignedWord(teamBaseTf + TeamData.OffPlayerNumber);
            if (pnTf == 0)
            {
                byte tf = Memory.ReadByte(Memory.Addr.playerTurnFlags);
                Memory.WriteByte(Memory.Addr.playerTurnFlags, (byte)(tf & 0xBB));
            }
        }

        // OpenSWOS fatigue: a keeper tires from EACH ball caught/held — random
        // 1..3% of his current energy (user spec). This funnel re-fires within a
        // single catch (mid-dive completion, bench re-check), but only the FIRST
        // call runs with gameState != 3 (open play); every repeat already sees 3
        // (set just below). So gating on "gameState != 3" makes it fire exactly
        // once per catch. Gated on EffectEnabled inside DrainOnKeeperCatch.
        if (Memory.ReadSignedWord(Memory.Addr.gameState) != 3)
            PlayerEnergy.DrainOnKeeperCatch(keeperSpriteAddr);

        // player.cpp:2297 — gameState = ST_KEEPER_HOLDS_BALL (3)
        Memory.WriteWord(Memory.Addr.gameState, 3);
        // player.cpp:2298 — breakCameraMode = -1 (offset 523120).
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        // player.cpp:2299-2302 — foulXCoordinate/foulYCoordinate = ball.x/y.whole.
        Memory.WriteWord(Memory.Addr.foulXCoordinate, BallSprite.XPixels);
        Memory.WriteWord(Memory.Addr.foulYCoordinate, BallSprite.YPixels);
        // player.cpp:2303 — gameStatePl = ST_STOPPED (101)
        Memory.WriteWord(Memory.Addr.gameStatePl, 101);
        // player.cpp:2304 — gameNotInProgressCounterWriteOnly = 0.
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);
        // player.cpp:2305-2306 — lastTeamPlayedBeforeBreak = this team's TeamData address.
        int teamBase = isTopTeam ? TeamData.TopBase : TeamData.BottomBase;
        Memory.WriteDword(Memory.Addr.lastTeamPlayedBeforeBreak, teamBase);
        // player.cpp:2307-2308 — stoppageTimerTotal = 0; stoppageTimerActive = 0.
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);
        // player.cpp:2309 — stopAllPlayers(). Runs even when the keeper is
        // still mid-dive — this is what freezes the other 21 players during
        // the hold. Its absence made every sprite keep its speed and
        // oscillate ±1px around dest for the whole stoppage.
        TeamPort.StopAllPlayers();

        // player.cpp:2310-2323 — if keeper still diving, return WITHOUT setting
        // controlledPlayer / ball coords. The dive animation must finish first;
        // the dive/catch handlers re-invoke this function (see header).
        if (TeamData.GoalkeeperDivingRight(isTopTeam) != 0) return;
        if (TeamData.GoalkeeperDivingLeft(isTopTeam) != 0) return;

        // player.cpp:2325-2326 — team.controlledPlayer = keeper sprite address.
        TeamData.SetControlledPlayer(isTopTeam, keeperSpriteAddr);
        // player.cpp:2327 — team.ballOutOfPlayOrKeeper = 1.
        TeamData.SetBallOutOfPlayOrKeeper(isTopTeam, 1);

        // player.cpp:2328-2330 — keeper.direction = cameraDirection.
        Memory.WriteWord(keeperSpriteAddr + PlayerSprite.OffDirection, cameraDirection);

        // player.cpp:2331-2332 — team.goaliePlayingOrOut = 1.
        Memory.WriteWord(TeamData.Base(isTopTeam) + TeamData.OffGoaliePlayingOrOut, 1);

        // player.cpp:2333 — updatePlayerWithBall() positions keeper at ball.
        // Mechanical wire to PlayerActions.UpdatePlayerWithBall (player.cpp:85)
        // which snaps keeper.x/y/destX/destY to ball.x/y + kPlayerWithBallOffsets
        // [direction*4].
        // Source: external/swos-port/src/game/player.cpp:2333.
        PlayerActions.UpdatePlayerWithBall(keeperSpriteAddr);

        // player.cpp:2334-2335 — cameraXVelocity = 0; cameraYVelocity = 0.
        Memory.WriteWord(Memory.Addr.cameraXVelocity, 0);
        Memory.WriteWord(Memory.Addr.cameraYVelocity, 0);
    }


    // ====================================================================
    // Mid-dive claim completion — updatePlayers.cpp:3212-3242
    // (l_goalkeeper_still_diving, goalkeeperDivingRight branch)
    // ====================================================================
    //
    // When the ball hits a DIVING keeper, the collision handler (original:
    // cseg_807CB, updatePlayers.cpp:4062-4090; port: UpdatePlayers.cs dive
    // branch) snaps the ball to the keeper, sets goalkeeperDivingRight = 1
    // and calls goalkeeperClaimedTheBall — which early-returns before the
    // controlledPlayer / goaliePlayingOrOut / updatePlayerWithBall writes
    // (player.cpp:2310-2316) because the dive animation must finish first.
    //
    // The original completes the deferred claim inside the per-tick dive
    // handler l_goalie_diving: after the playerDownTimer decrement
    // (updatePlayers.cpp:3190-3203), l_goalkeeper_still_diving checks
    // goalkeeperDivingRight != 0 AND playerDownTimer <= 42
    // (updatePlayers.cpp:3214-3235); when both hold it clears the flag,
    // calls updateBallWithControllingGoalkeeper + goalkeeperClaimedTheBall
    // (now running the FULL path — controlledPlayer gets set), writes
    // ball.z.whole = 5, and jumps to l_goalkeeper_rise (state = PL_NORMAL +
    // standing anim, updatePlayers.cpp:3205-3210) → l_stop_player
    // (dest = pos, updatePlayers.cpp:10079-10084).
    //
    // Port wiring note: the dive handler lives in UpdatePlayers.cs (another
    // module); this method is invoked from TickGoalkeeperHoldAutoRelease,
    // which UpdatePlayers.cs already calls at the top of every keeper tick —
    // BEFORE its own playerDownTimer decrement. To keep the tick arithmetic
    // identical to the original we therefore test the PREDICTED post-
    // decrement value (timer - 1) and, on completion, consume the decrement
    // ourselves; the caller skips its dive branch when we return true
    // (mirroring the `jmp @@goalkeeper_rise` control flow).
    //
    // Returns true if the deferred claim completed this tick (keeper now
    // stands in PL_NORMAL holding the ball, team.controlledPlayer set).
    //
    // from updatePlayers.cpp:3212-3242
    public static bool TickGoalieDivingClaimCompletion(int keeperSpriteAddr, bool isTopTeam)
    {
        // Only relevant while the keeper is mid-dive (states 6/7 — the
        // l_goalie_diving handler in the original).
        byte state = Memory.ReadByte(keeperSpriteAddr + PlayerSprite.OffPlayerState);
        if (state != (byte)PortPlayerState.kGoalieDivingHigh &&
            state != (byte)PortPlayerState.kGoalieDivingLow)
            return false;

        // updatePlayers.cpp:3214-3219 — if goalkeeperDivingRight == 0 →
        // @@goalie_diving_left (no deferred claim pending).
        if (TeamData.GoalkeeperDivingRight(isTopTeam) == 0)
            return false;

        // updatePlayers.cpp:3190-3203 + 3221-3235 — the original decrements
        // playerDownTimer first (`sub [esi+Sprite.playerDownTimer], 1`), then:
        //   - result == 0 → l_goalkeeper_rise WITHOUT completing (the
        //     divingRight flag stays set — original leak, preserved; the
        //     caller's dive branch performs that rise).
        //   - result > 42 → keep diving (`cmp playerDownTimer, 42; ja`).
        //   - else → complete the claim.
        // We run before the caller's decrement, so test (timer - 1).
        sbyte timer = (sbyte)Memory.ReadByte(keeperSpriteAddr + PlayerSprite.OffPlayerDownTimer);
        int timerAfter = timer - 1;
        if (timerAfter <= 0) return false;   // rise path — caller handles.
        if (timerAfter > 42) return false;   // still too early in the dive.

        // Consume this tick's decrement (caller skips its dive branch when
        // we return true).
        Memory.WriteByte(keeperSpriteAddr + PlayerSprite.OffPlayerDownTimer, (byte)timerAfter);

        // updatePlayers.cpp:3237 — goalkeeperDivingRight = 0. MUST precede the
        // claim call so it runs its full path this time.
        TeamData.SetGoalkeeperDivingRight(isTopTeam, 0);

        // updatePlayers.cpp:3238 — updateBallWithControllingGoalkeeper().
        UpdateBallWithControllingGoalkeeper(keeperSpriteAddr);

        // updatePlayers.cpp:3239 — goalkeeperClaimedTheBall() — full path:
        // sets team.controlledPlayer = keeper (so BallUpdate's per-frame pin
        // engages), ballOutOfPlayOrKeeper, goaliePlayingOrOut, direction,
        // updatePlayerWithBall.
        GoalkeeperClaimedTheBall(keeperSpriteAddr, isTopTeam);

        // updatePlayers.cpp:3240-3241 — ball.z.whole = 5 (keeper hand height).
        BallSprite.ZPixels = 5;

        // updatePlayers.cpp:3242 → l_goalkeeper_rise (3205-3210): state =
        // PL_NORMAL + playerNormalStandingAnimTable, then l_stop_player
        // (10079-10084): dest = pos. (updatePlayerWithBall inside the claim
        // already snapped keeper x/y/destX/destY to the ball; the explicit
        // stop is kept for mechanical parity.)
        Memory.WriteByte(keeperSpriteAddr + PlayerSprite.OffPlayerState, 0);
        SetPlayerAnimationTable(keeperSpriteAddr, Memory.Addr.kPlayerStandingAnimTableAddr);
        short kx = Memory.ReadSignedWord(keeperSpriteAddr + PlayerSprite.OffX + 2);
        short ky = Memory.ReadSignedWord(keeperSpriteAddr + PlayerSprite.OffY + 2);
        Memory.WriteWord(keeperSpriteAddr + PlayerSprite.OffDestX, kx);
        Memory.WriteWord(keeperSpriteAddr + PlayerSprite.OffDestY, ky);
        return true;
    }


    // ====================================================================
    // goalkeeperCaughtTheBall — updatePlayers.cpp:11022-11089
    // ====================================================================
    //
    // Initiates the catch animation when keeper reaches the ball. Sets keeper
    // sprite state, animation timer, speed, and destination. Caller (the
    // updatePlayers loop) decides this fires (not us — yet).
    //
    // Effect:
    //   - keeper.direction = 4 (TOP team faces south) or 0 (BOTTOM faces north)
    //   - team.goalkeeperDivingLeft = 0
    //   - keeper.playerState = kGoalieCatchingBall (4)
    //   - keeper.playerDownTimer = 15 (animation ticks)
    //   - keeper.speed = kGoalkeeperCatchSpeed (768)
    //   - keeper.destX = ballNextGroundX (predicted landing X)
    //   - keeper.destY = clamp(ballNextGroundY, 137, 761)
    //
    // Calls SetPlayerAnimationTable(goalieCatchingBallAnimTable) so the
    // sprite's frameIndicesTable + frameDelay are populated; C# renderer
    // (KeeperSim Phase A) still owns the visual catch animation for now.
    public static void GoalkeeperCaughtTheBall(int keeperSpriteAddr, bool isTopTeam)
    {
        // updatePlayers.cpp:11036-11042 — keeper.direction by team
        // TOP team (A6 == topTeamData): direction = 4 (face south)
        // BOTTOM team (A6 != topTeamData): direction = 0 (face north)
        int dir = isTopTeam ? 4 : 0;
        Memory.WriteWord(keeperSpriteAddr + PlayerSprite.OffDirection, dir);

        // updatePlayers.cpp:11046 — clear goalkeeperDivingLeft.
        TeamData.SetGoalkeeperDivingLeft(isTopTeam, 0);

        // updatePlayers.cpp:11048 — SetPlayerAnimationTable(goalieCatchingBallAnimTable).
        SetPlayerAnimationTable(keeperSpriteAddr, Memory.Addr.kGoalieCatchingBallAnimTableAddr);

        // updatePlayers.cpp:11050-11051 — set state + timer.
        Memory.WriteByte(keeperSpriteAddr + PlayerSprite.OffPlayerState, 4);   // kGoalieCatchingBall
        Memory.WriteByte(keeperSpriteAddr + PlayerSprite.OffPlayerDownTimer, 15);

        // updatePlayers.cpp:11052-11053 — keeper.speed = kGoalkeeperCatchSpeed (768).
        int catchSpeed = Memory.ReadWord(Memory.Addr.kGoalkeeperCatchSpeed);
        Memory.WriteWord(keeperSpriteAddr + PlayerSprite.OffSpeed, catchSpeed);

        // updatePlayers.cpp:11054-11057 — destX/Y = predicted ball landing.
        short groundX = Memory.ReadSignedWord(Memory.Addr.ballNextGroundX);
        short groundY = Memory.ReadSignedWord(Memory.Addr.ballNextGroundY);
        Memory.WriteWord(keeperSpriteAddr + PlayerSprite.OffDestX, groundX);
        Memory.WriteWord(keeperSpriteAddr + PlayerSprite.OffDestY, groundY);

        // updatePlayers.cpp:11058-11088 — clamp destY to [137, 761] (goal-area Y range).
        if (groundY < 137)
        {
            Memory.WriteWord(keeperSpriteAddr + PlayerSprite.OffDestY, 137);
        }
        else if (groundY > 761)
        {
            Memory.WriteWord(keeperSpriteAddr + PlayerSprite.OffDestY, 761);
        }
    }


    // ====================================================================
    // Per-tick state handlers — kGoalieCatchingBall (state=4)
    //                          kGoalieClaimed       (state=11)
    // From updatePlayers.cpp:3059-3186
    // ====================================================================
    //
    // These are invoked by the per-player update loop (not yet ported) when
    // the keeper sprite's playerState matches. Available as standalone ports
    // so the loop can call them straight off when it lands.

    // updatePlayers.cpp:3059-3093 — handles kGoalieCatchingBall (4) per tick.
    // Decrements playerDownTimer. At 0, transitions to Normal and, if the
    // goalkeeperDivingLeft flag was set, fully claims the ball.
    //
    // Returns true if the keeper just finished the catch and transitioned to
    // Normal/Claimed (caller may want to skip further movement logic).
    public static bool TickGoalieCatchingBall(int keeperSpriteAddr, bool isTopTeam)
    {
        // 3061-3072 — decrement playerDownTimer.
        sbyte timer = (sbyte)Memory.ReadByte(keeperSpriteAddr + PlayerSprite.OffPlayerDownTimer);
        timer--;
        Memory.WriteByte(keeperSpriteAddr + PlayerSprite.OffPlayerDownTimer, timer);

        if (timer != 0)
        {
            // 3095-3148 — `l_check_diving_side` path. If goalkeeperDivingLeft is
            // set, keeper is still in dive animation, no claim attempt this tick.
            // Otherwise, if ball is very close, RNG-based catch-or-deflect.
            if (TeamData.GoalkeeperDivingLeft(isTopTeam) != 0) return false;

            // updatePlayers.cpp:3107-3148 — check team.plVeryCloseToBall (offset +61).
            int teamBase = TeamData.Base(isTopTeam);
            byte close = Memory.ReadByte(teamBase + 61);  // plVeryCloseToBall
            if (close == 0) return false;

            // updatePlayers.cpp:3117-3142 — RNG: D1 = (currentGameTick & 0xF0) >> 4
            // (game-tick % 16 high bits). Compare with shotChanceTable[+48].
            // If D1 - shotChance is signed-negative → catch path
            // (l_goalie_catches_the_ball at 3150). Else → goalkeeperDeflectedBall
            // (parry, at 3144).
            // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:3117-3148.
            int shotChanceTablePtr = Memory.ReadSignedDword(teamBase + TeamData.OffShotChanceTable);
            short shotChance48 = shotChanceTablePtr != 0
                ? Memory.ReadSignedWord(shotChanceTablePtr + 48)
                : (short)0;
            ushort gameTick = Memory.ReadWord(Memory.Addr.currentGameTick);
            short d1Rnd = (short)((gameTick & 0xF0) >> 4);
            short diff = (short)(d1Rnd - shotChance48);
            if (diff < 0)
            {
                // 3150-3161 — l_goalie_catches_the_ball.
                //   keeper.speed = 0; ball.speed = 0; team.goalkeeperDivingLeft = 1;
                //   goalkeeperClaimedTheBall(); PlayKeeperClaimedComment();
                Memory.WriteWord(keeperSpriteAddr + PlayerSprite.OffSpeed, 0);
                BallSprite.Speed = 0;
                TeamData.SetGoalkeeperDivingLeft(isTopTeam, 1);
                GoalkeeperClaimedTheBall(keeperSpriteAddr, isTopTeam);
                // updatePlayers.cpp:3143 — PlayKeeperClaimedComment().
                OpenSwos.Audio.MatchAudio.KeeperClaimedComment();
                return true;
            }
            else
            {
                // 3144 — goalkeeperDeflectedBall(); PlayKeeperClaimedComment().
                GoalkeeperDeflectedBall(BallSprite.Base, teamBase);
                // updatePlayers.cpp:3144 — PlayKeeperClaimedComment().
                OpenSwos.Audio.MatchAudio.KeeperClaimedComment();
                return false;
            }
        }

        // 3076-3093 — timer hit 0. Transition to Normal.
        Memory.WriteByte(keeperSpriteAddr + PlayerSprite.OffPlayerState, 0);
        // updatePlayers.cpp:3078 — SetPlayerAnimationTable(playerNormalStandingAnimTable).
        SetPlayerAnimationTable(keeperSpriteAddr, Memory.Addr.kPlayerStandingAnimTableAddr);

        // 3080-3086 — if goalkeeperDivingLeft was set (catch-pending flag),
        // complete the claim: clear flag, pin ball, transition gameState.
        short diving = TeamData.GoalkeeperDivingLeft(isTopTeam);
        if (diving != 0)
        {
            TeamData.SetGoalkeeperDivingLeft(isTopTeam, 0);
            UpdateBallWithControllingGoalkeeper(keeperSpriteAddr);
            GoalkeeperClaimedTheBall(keeperSpriteAddr, isTopTeam);
            // updatePlayers.cpp:3092 — ball.z.whole = 5 (keeper hand height).
            BallSprite.ZPixels = 5;
        }
        return true;
    }

    // updatePlayers.cpp:3163-3186 — handles kGoalieClaimed (11) per tick.
    // Decrements playerDownTimer. At 0, transitions to Normal.
    //
    // Returns true if the keeper just transitioned back to Normal.
    public static bool TickGoalieClaimed(int keeperSpriteAddr)
    {
        sbyte timer = (sbyte)Memory.ReadByte(keeperSpriteAddr + PlayerSprite.OffPlayerDownTimer);
        timer--;
        Memory.WriteByte(keeperSpriteAddr + PlayerSprite.OffPlayerDownTimer, timer);

        if (timer != 0) return false;

        // 3180 — keeper.playerState = Normal.
        Memory.WriteByte(keeperSpriteAddr + PlayerSprite.OffPlayerState, 0);
        // updatePlayers.cpp:3182 — SetPlayerAnimationTable(playerNormalStandingAnimTable).
        SetPlayerAnimationTable(keeperSpriteAddr, Memory.Addr.kPlayerStandingAnimTableAddr);
        return true;
    }


    // ====================================================================
    // Keeper-holds-ball release — PORT-ONLY safety net (CPU teams only)
    // ====================================================================
    //
    // ORIGINAL behavior while gameState == ST_KEEPER_HOLDS_BALL (3):
    //   - HUMAN keeper's team → the game waits INDEFINITELY for the player
    //     to press fire: gameLoop.cpp:545-546 bails out of the stoppage
    //     timeout whenever lastTeamPlayedBeforeBreak.playerNumber != 0.
    //     There is NO auto-release for human teams in SWOS.
    //   - CPU team → the AI decides to kick via the l_keepers_ball block of
    //     AI_SetControlsDirection (updatePlayers.cpp:16656; ported at
    //     AiBrain.cs:428-461), reachable once stoppageTimerActive exceeds
    //     (gameTick & 63) + 100 (updatePlayers.cpp:16451-16480, AiBrain.cs:
    //     344-348). Last resort: gameLoop.cpp:548-560 calls
    //     prepareForInitialKick once stoppageTimerActive >= initalKickInterval
    //     (825, gameLoop.cpp:52) — being ported into GameLoop.cs.
    //
    // THIS method is NOT part of the original: it is a port-only fallback
    // that force-kicks the CPU keeper at kPortSafetyReleaseTicks in case the
    // AI chain above fails to fire. It logs "[PORT-SAFETY]" so smoke logs
    // reveal any reliance on it. It never fires for human-controlled teams
    // (mirrors gameLoop.cpp:545-546).
    //
    // It ALSO hosts the faithful mid-dive claim completion
    // (TickGoalieDivingClaimCompletion, updatePlayers.cpp:3212-3242) because
    // this is the hook UpdatePlayers.cs already calls at the top of every
    // keeper tick, before its dive-state branch.
    //
    // Caller: TickGoalkeeper (UpdatePlayers.cs) once per tick per keeper.
    // Returns true if the keeper's state was resolved this tick (deferred
    // claim completed, or fallback kick fired) — caller skips movement logic.
    //
    // from external/swos-port/src/game/gameLoop.cpp:545-560
    //     external/swos-port/src/game/updatePlayers/updatePlayers.cpp:16656
    //     external/swos-port/src/game/updatePlayers/updatePlayers.cpp:3212-3242
    public const short kPortSafetyReleaseTicks = 300;   // PORT-ONLY, no asm counterpart

    public static bool TickGoalkeeperHoldAutoRelease(int keeperSpriteAddr, bool isTopTeam)
    {
        // Faithful deferred mid-dive claim completion — see
        // TickGoalieDivingClaimCompletion (updatePlayers.cpp:3212-3242).
        // Runs first: it must fire while the keeper is still in the dive
        // states, independent of the fallback gating below.
        if (TickGoalieDivingClaimCompletion(keeperSpriteAddr, isTopTeam))
            return true;

        // Only meaningful during the keeper-holds-ball stoppage.
        short gameState = Memory.ReadSignedWord(Memory.Addr.gameState);
        if (gameState != 3) return false;

        // The team holding the ball is recorded at goalkeeperClaimedTheBall
        // (player.cpp:2306 `lastTeamPlayedBeforeBreak = team`). Only that
        // team's keeper releases; the other keeper sits out the stoppage.
        int holdingTeam = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayedBeforeBreak);
        int teamBase = TeamData.Base(isTopTeam);
        if (holdingTeam != 0 && holdingTeam != teamBase) return false;

        // gameLoop.cpp:545-546 — human-controlled team (playerNumber != 0):
        // the original waits indefinitely for the player to press fire.
        // Never auto-release.
        short playerNumber = Memory.ReadSignedWord(teamBase + TeamData.OffPlayerNumber);
        if (playerNumber != 0) return false;

        // PORT-ONLY fallback threshold. The faithful CPU release is the AI
        // l_keepers_ball decision (AiBrain.cs:428, from updatePlayers.cpp:
        // 16656) which typically fires between ~100 and ~163 ticks; the
        // original's own last resort is prepareForInitialKick at 825 ticks
        // (gameLoop.cpp:548-560). 300 sits between the two so genuine AI
        // releases stay untouched while a stuck hold still resolves.
        // The per-tick increment of stoppageTimerActive lives in
        // GameLoop.UpdateGameTimersAndCameraBreakMode (mirror of
        // gameLoop.cpp:530-537); the reset to 0 lives at
        // goalkeeperClaimedTheBall (player.cpp:2307-2308).
        short active = Memory.ReadSignedWord(Memory.Addr.stoppageTimerActive);
        if ((ushort)active < (ushort)kPortSafetyReleaseTicks) return false;

        Godot.GD.Print("[PORT-SAFETY] keeper hold auto-release");

        // updatePlayers.cpp:5344 / 6317 — the AI branch calls
        // `playerKickingBall()` with A1 = player, A6 = team. The team's
        // controlledPlayer has already been set to the keeper at
        // goalkeeperClaimedTheBall (player.cpp:2326).
        //
        // playerKickingBall reads team.controlledPlayer for the kBallPlOffsets
        // table base AND uses A1 (keeper) for direction. We pass keeperSprite
        // as A1 directly.
        PlayerActions.PlayerKickingBall(teamBase, keeperSpriteAddr);

        // player.cpp:2326-2327 — clear controlledPlayer + ballOutOfPlayOrKeeper
        // so the kicked ball is no longer pinned to the keeper. Mirror the
        // l_pass_success path (updatePlayers.cpp:8011-8017) which clears
        // these on the AI's successful release.
        Memory.WriteDword(teamBase + TeamData.OffControlledPlayer, 0);
        Memory.WriteWord(teamBase + TeamData.OffBallOutOfPlayOrKeeper, 0);

        // updatePlayers.cpp:8013-8015 — passingBall/passingToPlayer/shooting=0;
        // playerSwitchTimer=25 (so AI doesn't immediately re-tackle the
        // outgoing ball).
        Memory.WriteWord(teamBase + 88, 0);                 // passingBall
        Memory.WriteWord(teamBase + 90, 0);                 // passingToPlayer
        Memory.WriteWord(teamBase + 58, 0);                 // shooting
        Memory.WriteWord(teamBase + 92, 25);                // playerSwitchTimer

        // Resume live play. The kick puts the ball back into motion; mark
        // gameStatePl = ST_GAME_IN_PROGRESS (100) and gameState = 100 so
        // BallUpdate / GameTime / AI dispatchers see live state.
        // Mirrors PlayerControlled.cs:556-557 / 629-630 (the same writes the
        // human-kick path makes after kicking out of a stoppage).
        Memory.WriteWord(Memory.Addr.gameStatePl, 100);
        Memory.WriteWord(Memory.Addr.gameState,   100);

        // Reset the stoppage counter so the next claim starts fresh.
        // ball.cpp:4004-4005 does the same at l_break_handled, and
        // gameLoop.cpp:385 does it at the start of every kickoff cycle.
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal,  0);

        // Move keeper sprite state out of kGoalieClaimed (state=11) since
        // the ball has left his hands. Mirrors TickGoalieClaimed transition
        // at line 272 above.
        Memory.WriteByte(keeperSpriteAddr + PlayerSprite.OffPlayerState, 0);
        SetPlayerAnimationTable(keeperSpriteAddr, Memory.Addr.kPlayerStandingAnimTableAddr);
        return true;
    }


    // ====================================================================
    // getFramesNeededToCoverDistance — updatePlayers.cpp:11105-11191
    // ====================================================================
    //
    // Inputs:
    //   d0 — movement delta per frame (Q16.16 absolute taken)
    //   d4 — distance to cover (integer pixels)
    // Output:
    //   d7 — frames needed (integer)
    //
    // The asm divides d4 / d0 by repeated subtraction (no DIV instruction).
    // When |d0| < 1.0 (i.e. < 0x10000) it pre-scales d0 by powers of 2 to keep
    // the loop count bounded; d5 tracks the inverse scale so the result stays
    // accurate. Exact divisions return one MORE than expected (e.g. 9/3 -> 4)
    // because the post-decrement test is `sub d4, d0` then `jns divide_loop` —
    // the loop fires once more when d4 reaches exactly zero.
    //
    // from updatePlayers.cpp:11105
    public static int GetFramesNeededToCoverDistance(int d0, int d4)
    {
        // 11107-11112 — `or eax, eax; jz l_not_moving` — zero delta = no frames.
        if (d0 == 0)
        {
            return 0;
        }

        // 11114-11117 — `jns l_positive` / `neg d0` — absolute value.
        if (d0 < 0) d0 = -d0;

        // 11119-11150 — d5 = 1. If d0 < 0x10000 (i.e. < 1.0 fixed-point),
        // scale d0 up by 2 each iteration; d5 doubles in lockstep.
        int d5 = 1;
        while ((uint)d0 < 0x10000u)
        {
            d0 <<= 1;
            d5 <<= 1;
        }

        // l_goalkeeper_delta_eq_1_or_more (11152):
        // 11152-11160 — `xchg ax, word ptr d4+2` swaps low and high words of d4
        // (because d4 contains an integer pixel count in the low half, hi word
        // expected zero) then writes word ptr d4 = 0. Net effect: convert d4
        // from "whole-pixel int" to Q16.16 by shifting into the high half.
        d4 = (int)((uint)(d4 & 0xFFFF) << 16);

        // 11160 — d7 = 0 initial accumulator.
        int d7 = 0;

    l_divide_loop:;
        // 11163-11181 — `add word ptr d7, ax` then `sub d4, d0`. Loop until d4
        // goes negative (sign bit set).
        d7 = (short)(d7 + d5);
        d4 = (int)((uint)d4 - (uint)d0);
        if (d4 >= 0) goto l_divide_loop;

        // 11183 — return d7.
        return d7 & 0xFFFF;
    }


    // ====================================================================
    // shouldGoalkeeperDive — updatePlayers.cpp:10485-10816
    // ====================================================================
    //
    // Inputs (mirrors asm regs):
    //   A1 — goalkeeper sprite address
    //   A2 — ball sprite address (always BallSprite.Base in C# port)
    //   A6 — team data base (522792 = topTeamData, else bottomTeamData)
    //
    // Output:
    //   true  — keeper CAN attempt save (caller should call goalkeeperJumping)
    //   false — keeper won't dive this frame
    //
    // Algorithm:
    //   1. Compute Δy = ball.y - keeper.y. For TOP team flip sign (the keeper
    //      faces south, so "in front" means lower Y).
    //   2. If Δy < 0 (ball BEHIND keeper): allow if -10 <= Δy < 0 → try saving;
    //      else won't dive.
    //   3. If ball in front:
    //      a. During penalty (playingPenalties || penalty) → pick
    //         kKeeperPenaltySaveDistanceFar/Near at random (Rand() & 0x18) — if
    //         Δy <= chosen distance → try saving, else won't dive.
    //      b. Normal play → if Δy > kKeeperSaveDistance → won't dive.
    //         Otherwise compute frames the keeper needs to reach ball X using
    //         deltaX + the chosen kGoalkeeperDiveDeltas[shotChanceTable hi-byte]
    //         entry. If keeper's dive can outpace the ball, try saving.
    //
    // from updatePlayers.cpp:10485
    public static bool ShouldGoalkeeperDive(int a1KeeperAddr, int a2BallAddr, int a6TeamBase)
    {
        // 10487-10497 — d0_w = ball.y.whole - keeper.y.whole
        short ballYw = Memory.ReadSignedWord(a2BallAddr + 36);    // Sprite.y+2
        short keeperYw = Memory.ReadSignedWord(a1KeeperAddr + 36);
        short d0w = (short)(ballYw - keeperYw);

        // 10498-10510 — cmp A6, offset topTeamData; jz l_check_…; else neg D0.
        // I.e. for BOTTOM team (A6 != topTeamData) the sign is inverted.
        bool isTopTeam = (a6TeamBase == TeamData.TopBase);
        if (!isTopTeam)
        {
            d0w = (short)(-d0w);
        }

        // l_check_goalkeeper_ball_distance (10512):
        // 10513-10519 — `or ax, ax; jns l_ball_in_front_of_goalkeeper`.
        // d0w >= 0 → in front; d0w < 0 → behind.
        if (d0w >= 0) goto l_ball_in_front_of_goalkeeper;

        // 10521-10531 — `cmp d0, -10; jl l_goalkeeper_wont_dive`.
        // Behind keeper, but within 10 px → still try.
        if (d0w < -10) goto l_goalkeeper_wont_dive;
        goto l_try_saving;

    l_ball_in_front_of_goalkeeper:;
        // 10535-10542 — `or ax, ax; js l_goalkeeper_wont_dive` (redundant — we
        // already know >= 0 by branch). Preserved for parity with asm.
        if (d0w < 0) goto l_goalkeeper_wont_dive;

        // 10544-10558 — penalty handling: jump to penalty branch if
        // playingPenalties OR penalty != 0. Else normal_shot.
        short playingPenalties = Memory.ReadSignedWord(Memory.Addr.playingPenalties);
        if (playingPenalties != 0) goto l_penalty_shot;
        short penalty = Memory.ReadSignedWord(Memory.Addr.penalty);
        if (penalty == 0) goto l_normal_shot;

    l_penalty_shot:;
        // 10561-10602 — D1 = kKeeperPenaltySaveDistanceFar. Then Rand() & 0x18:
        // if non-zero, replace with kKeeperPenaltySaveDistanceNear.
        // Then `cmp d0, d1; jg l_goalkeeper_wont_dive`.
        short d1 = Memory.ReadSignedWord(Memory.Addr.kKeeperPenaltySaveDistanceFar);

        // 10567-10584 — Rand() & 0x18: if zero, keep far variant; else swap
        // to near. Wired through SwosVm.Rng (source:
        // external/swos-port/src/util/random.cpp).
        int rnd = Rng.NextByte() & 0x18;
        if (rnd != 0)
            d1 = Memory.ReadSignedWord(Memory.Addr.kKeeperPenaltySaveDistanceNear);

        // l_penalty_compare_distance (10589):
        if (d0w > d1) goto l_goalkeeper_wont_dive;
        goto l_try_saving;

    l_normal_shot:;
        // 10606-10618 — D1 = kKeeperSaveDistance; if d0 > d1 → wont_dive.
        short kSaveDist = Memory.ReadSignedWord(Memory.Addr.kKeeperSaveDistance);
        d1 = kSaveDist;
        if (d0w > d1) goto l_goalkeeper_wont_dive;

        // 10620-10643 — D4 = |ball.y.w - keeper.y.w|. Sign-magnitude.
        short ballYw2 = Memory.ReadSignedWord(a2BallAddr + 36);
        short keeperYw2 = Memory.ReadSignedWord(a1KeeperAddr + 36);
        short d4w = (short)(ballYw2 - keeperYw2);
        if (d4w < 0) d4w = (short)(-d4w);

        // cseg_78AE6 (10644):
        // 10645-10656 — D0 = ball.deltaY; D1 = framesNeededToCoverDistance(D0, D4)
        // (the Y component). If frames == 0 → wont_dive.
        int ballDeltaY = Memory.ReadSignedDword(a2BallAddr + 50);
        int d1Frames = GetFramesNeededToCoverDistance(ballDeltaY, d4w);
        if (d1Frames == 0) goto l_goalkeeper_wont_dive;

        // 10658-10679 — D4 = |ballDefensiveX - keeper.x.w|.
        short ballDefX = Memory.ReadSignedWord(Memory.Addr.ballDefensiveX);
        short keeperXw = Memory.ReadSignedWord(a1KeeperAddr + 32);
        short d4w2 = (short)(ballDefX - keeperXw);
        if (d4w2 < 0) d4w2 = (short)(-d4w2);

        // cseg_78B34 (10681):
        // 10681-10693 — D0 = keeper.deltaX; D2 = framesNeededToCoverDistance.
        // (Yes, asm reads keeper.deltaX here — A1 not A2 — to figure out how
        // many frames the keeper needs to traverse the X gap.)
        int keeperDeltaX = Memory.ReadSignedDword(a1KeeperAddr + 46);
        int d2Frames = GetFramesNeededToCoverDistance(keeperDeltaX, d4w2);
        if (d2Frames == 0) goto l_goalkeeper_wont_dive;

        // 10695-10706 — `cmp D2, D1; jbe l_goalkeeper_wont_dive`.
        // Unsigned compare: if keeper's X-traverse takes <= ball's Y-traverse,
        // ball will arrive first → wont_dive.
        if ((uint)d2Frames <= (uint)d1Frames) goto l_goalkeeper_wont_dive;

        // 10708-10726 — D4 = |ballDefensiveX - keeper.x.w| (recomputed).
        short ballDefX2 = Memory.ReadSignedWord(Memory.Addr.ballDefensiveX);
        short keeperXw2 = Memory.ReadSignedWord(a1KeeperAddr + 32);
        short d4w3 = (short)(ballDefX2 - keeperXw2);
        if (d4w3 < 0) d4w3 = (short)(-d4w3);

        // cseg_78B95 (10727):
        // 10728-10755 — A0 = team.shotChanceTable. Read entry at [A0 + (gameTick & 0x0F)*2 + 10].
        // Decrement; clamp to >= 0.
        int shotChanceTablePtr = Memory.ReadSignedDword(a6TeamBase + TeamData.OffShotChanceTable);
        ushort gameTick = Memory.ReadWord(Memory.Addr.currentGameTick);
        int idx16 = ((gameTick & 0x0F) << 1);
        short chanceEntry;
        if (shotChanceTablePtr != 0)
        {
            chanceEntry = Memory.ReadSignedWord(shotChanceTablePtr + idx16 + 10);
        }
        else
        {
            // shotChanceTable not yet wired; treat as zero (forces ax = 0 path).
            chanceEntry = 0;
        }
        chanceEntry--;
        if (chanceEntry < 0) chanceEntry = 0;

        // l_calc_frames (10754):
        // 10755-10764 — D0 = kGoalkeeperDiveDeltas[chanceEntry * 4] (dword).
        int diveDelta = Memory.ReadSignedDword(Memory.Addr.kGoalkeeperDiveDeltasBase + (chanceEntry << 2));

        // 10765-10776 — goalkeeperDiveDeadVar += 1.
        short divDead = Memory.ReadSignedWord(Memory.Addr.goalkeeperDiveDeadVar);
        Memory.WriteWord(Memory.Addr.goalkeeperDiveDeadVar, (short)(divDead + 1));

        // 10777-10785 — D3 = framesNeededToCoverDistance(D0=diveDelta, D4=|defX-keeperX|).
        int d3Frames = GetFramesNeededToCoverDistance(diveDelta, d4w3);
        if (d3Frames == 0) goto l_goalkeeper_wont_dive;

        // 10787-10798 — `cmp D3, D1; jb l_goalkeeper_wont_dive`.
        // If keeper's dive-cover frames < ball's Y-arrival frames → too slow.
        if ((uint)d3Frames < (uint)d1Frames) goto l_goalkeeper_wont_dive;

    l_try_saving:;
        // 10801-10807 — D0 = 1; return TRUE.
        BumpShouldDiveTrue();
        return true;

    l_goalkeeper_wont_dive:;
        // 10810-10815 — D0 = 0; return FALSE.
        return false;
    }


    // ====================================================================
    // goalkeeperJumping — updatePlayers.cpp:10829-11017
    // ====================================================================
    //
    // Inputs (mirrors asm regs):
    //   D0 — direction (written to team.controlledPlDirection)
    //   D1 — far-jump speed flag (0 = high speed, 1 = lower, else randomised)
    //   D3 — direction for destination calc (indexes into kDefaultDestinations)
    //   A1 — keeper sprite
    //   A2 — ball sprite (BallSprite.Base)
    //   A6 — team data base
    //
    // Effect:
    //   - keeper.speed set per ball distance (close: kGoalkeeperNearJumpSpeed,
    //     else kGoalkeeperFarJumpSpeed, with optional slow/random variants)
    //   - team.field_46 = 0; team.goalkeeperDivingRight = 0
    //   - keeper.playerState = PL_GOALIE_DIVING_LOW (7) if ballNotHighZ <= 5,
    //     else PL_GOALIE_DIVING_HIGH (6)
    //   - SetPlayerAnimationTable called with one of 4 jumping anim tables
    //   - keeper.playerDownTimer = 75 (dive duration)
    //   - team.controlledPlDirection = D0
    //   - keeper.destX/Y = keeper.x/y + kDefaultDestinations[D3 * 4]
    //
    // from updatePlayers.cpp:10829
    public static void GoalkeeperJumping(int d0Dir, int d1SpeedFlag, int d3DestDir,
        int a1KeeperAddr, int a2BallAddr, int a6TeamBase)
    {
        // 10831-10842 — cmp keeper.ballDistance, 128 ; ja l_ball_far_away.
        // ballDistance is a dword squared distance.
        uint ballDist = Memory.ReadDword(a1KeeperAddr + 74);
        if (ballDist > 128u) goto l_ball_far_away;

        // 10844-10847 — speed = kGoalkeeperNearJumpSpeed.
        short nearSpeed = Memory.ReadSignedWord(Memory.Addr.kGoalkeeperNearJumpSpeed);
        Memory.WriteWord(a1KeeperAddr + 44, nearSpeed);
        // d1 unused in near branch but matches asm path.
        goto l_speed_setting_done;

    l_ball_far_away:;
        // 10850-10852 — speed = kGoalkeeperFarJumpSpeed.
        short farSpeed = Memory.ReadSignedWord(Memory.Addr.kGoalkeeperFarJumpSpeed);
        Memory.WriteWord(a1KeeperAddr + 44, farSpeed);

        // 10853-10858 — `or ax, ax; jz l_speed_setting_done` — only branches
        // if D1 == 0 (the original speed flag). 0 = use default far speed.
        if (d1SpeedFlag == 0) goto l_speed_setting_done;

        // 10860-10861 — speed = kGoalkeeperFarJumpSlowerSpeed.
        short slowerSpeed = Memory.ReadSignedWord(Memory.Addr.kGoalkeeperFarJumpSlowerSpeed);
        Memory.WriteWord(a1KeeperAddr + 44, slowerSpeed);

        // 10862-10871 — cmp d1, 1; jz l_speed_setting_done.
        // D1 == 1 → keep slower speed; anything else → randomise.
        if (d1SpeedFlag == 1) goto l_speed_setting_done;

        // 10873-10887 — D1 = (currentGameTick & 0xFF) + kGoalkeeperNearJumpSpeed.
        ushort tick = Memory.ReadWord(Memory.Addr.currentGameTick);
        short nearSpd = Memory.ReadSignedWord(Memory.Addr.kGoalkeeperNearJumpSpeed);
        int randomised = (tick & 0xFF) + nearSpd;
        Memory.WriteWord(a1KeeperAddr + 44, (short)randomised);

    l_speed_setting_done:;
        // 10890-10901 — D1 = ball.x.w - keeper.x.w (signed). Result is captured
        // into D1 in the asm but never used outside the side-effect; ignore.
        // (Preserved for parity.)
        // short ballXw = Memory.ReadSignedWord(a2BallAddr + 32);
        // short keepXw = Memory.ReadSignedWord(a1KeeperAddr + 32);
        // _ = (short)(ballXw - keepXw);

        // 10902-10903 — team.field_46 = 0; team.goalkeeperDivingRight = 0.
        Memory.WriteByte(a6TeamBase + 70, 0);
        Memory.WriteWord(a6TeamBase + TeamData.OffGoalkeeperDivingRight, 0);

        // Hoisted above both diving-low / diving-high branches so C# can
        // see assignment under all goto-paths.
        bool isBottomTeam = (a6TeamBase == TeamData.BottomBase);

        // 10904-10914 — cmp ballNotHighZ, 5 ; ja l_goalie_jumping_high.
        ushort ballNotHighZ = Memory.ReadWord(Memory.Addr.ballNotHighZ);
        if (ballNotHighZ > 5) goto l_goalie_jumping_high;

        // 10916-10917 — keeper.playerState = PL_GOALIE_DIVING_LOW (7).
        Memory.WriteByte(a1KeeperAddr + PlayerSprite.OffPlayerState, 7);
        BumpDiveCounter(7);

        // 10918-10927 — cmp A6, bottomTeamData ; jz l_right_goalie_jumping_low.
        if (isBottomTeam) goto l_right_goalie_jumping_low;

        // 10929-10931 — SetPlayerAnimationTable(leftGoalieJumpingLowAnimTable).
        SetPlayerAnimationTable(a1KeeperAddr, Memory.Addr.kLeftGoalieJumpingLowAnimTableAddr);
        goto l_set_down_timer;

    l_right_goalie_jumping_low:;
        // 10933-10936 — SetPlayerAnimationTable(rightGoalieJumpingLowAnimTable).
        SetPlayerAnimationTable(a1KeeperAddr, Memory.Addr.kRightGoalieJumpingLowAnimTableAddr);
        goto l_set_down_timer;

    l_goalie_jumping_high:;
        // 10938-10940 — keeper.playerState = PL_GOALIE_DIVING_HIGH (6).
        Memory.WriteByte(a1KeeperAddr + PlayerSprite.OffPlayerState, 6);
        BumpDiveCounter(6);

        // 10941-10950 — cmp A6, bottomTeamData ; jz l_right_goalie_jumping_high.
        if (isBottomTeam) goto l_right_goalie_jumping_high;

        // 10952-10954 — SetPlayerAnimationTable(leftGoalieJumpingHighAnimTable).
        SetPlayerAnimationTable(a1KeeperAddr, Memory.Addr.kLeftGoalieJumpingHighAnimTableAddr);
        goto l_set_down_timer;

    l_right_goalie_jumping_high:;
        // 10956-10958 — SetPlayerAnimationTable(rightGoalieJumpingHighAnimTable).
        SetPlayerAnimationTable(a1KeeperAddr, Memory.Addr.kRightGoalieJumpingHighAnimTableAddr);

    l_set_down_timer:;
        // 10961-10965 — keeper.playerDownTimer = 75;
        //               team.controlledPlDirection = D0.
        Memory.WriteByte(a1KeeperAddr + PlayerSprite.OffPlayerDownTimer, 75);
        Memory.WriteWord(a6TeamBase + TeamData.OffControlledPlDirection, (short)d0Dir);

        // 10967-10989 — A5 = kDefaultDestinations + D3*4.
        //               D0 = [A5] (dx word). A5 += 2. (Then [A5] = dy word.)
        int defDestEntry = Memory.Addr.kDefaultDestinations + (d3DestDir << 2);
        short dx = Memory.ReadSignedWord(defDestEntry);

        // 10991-11000 — keeper.destX = keeper.x.w + dx.
        short keeperXw3 = Memory.ReadSignedWord(a1KeeperAddr + 32);
        // 11001-11016 — A5 incremented → reads dy. keeper.destY = keeper.y.w + dy.
        short dy = Memory.ReadSignedWord(defDestEntry + 2);
        short keeperYw3 = Memory.ReadSignedWord(a1KeeperAddr + 36);
        short keeperDestX = (short)(dx + keeperXw3);
        short keeperDestY = (short)(dy + keeperYw3);
        // UNCLAMPED, like the original (updatePlayers.cpp:10991-11016 writes
        // dest = keeper.{x,y} + kDefaultDestinations[dir] with no bounds).
        // A defensive clamp to (81/590, 137/761) used to live here — added
        // when the dive had no speed decay and the keeper would glide to
        // ~Y=901. It bent every DIAGONAL dive's direction toward a pitch
        // corner (dest (81,137)/(590,761) etc. — the visible "keeper sprints
        // to the corner flag" bug) because clamping the far-away dest changes
        // the CalculateDeltaXAndY angle. The faithful fix is the per-tick
        // dive speed decay in UpdatePlayers.TickGoalieDiving
        // (l_set_new_goalkeeper_speed, updatePlayers.cpp:3403-3418), which
        // stops the slide after a few px exactly like the original; the raw
        // ±1000 dest is then harmless and keeps the dive angle exact.
        Memory.WriteWord(a1KeeperAddr + PlayerSprite.OffDestX, keeperDestX);
        Memory.WriteWord(a1KeeperAddr + PlayerSprite.OffDestY, keeperDestY);
    }


    // ====================================================================
    // goalkeeperDeflectedBall — player.cpp:2342-2528
    // ====================================================================
    //
    // Inputs:
    //   A2 — ball sprite address
    //   A6 — team data base
    //
    // Effect (parry path — keeper touches ball but doesn't claim it):
    //   - ball.destX/Y = ball.x/y + kDefaultDestinations[dirOffset]
    //     dirOffset = 0 (for BOTTOM team → deflects toward (0, -1000))
    //     dirOffset = 4 (for TOP team   → deflects toward (0,  1000))
    //   - ball.destX += ((currentGameTick & 31) << 5) - 512    // X jitter
    //   - Read team.shotChanceTable[+52] and [+54] (deflect-strength thresholds).
    //     Pick strong/medium/weak deflect speed accordingly:
    //       D1 (gameTick & 0x3C >> 2) - thresh1 < 0 → strong
    //       Else - thresh2 < 0 → medium, else → weak
    //   - ball.speed = chosenSpeed + ((gameTick & 0x1FF) - 256)  // speed jitter
    //   - ball.deltaZ = ((gameTick & 0x7F) << 8) - 16384 + kGoalkeeperDeflectDeltaZ
    //   - ResetBothTeamSpinTimers + PlayKickSample
    //
    // from player.cpp:2342
    public static void GoalkeeperDeflectedBall(int a2BallAddr, int a6TeamBase)
    {
        // 2344-2356 — D0 = 0; if A6 == bottomTeamData → keep 0; else D0 = 4.
        bool isBottomTeam = (a6TeamBase == TeamData.BottomBase);
        short d0 = isBottomTeam ? (short)0 : (short)4;

        // cseg_78DB8 (2358):
        // 2358-2376 — D0 <<= 2 (byte offset into kDefaultDestinations).
        //             D1 = ball.x.w + kDefaultDestinations[D0].
        int destByteOff = (d0 & 0xFFFF) << 2;
        short ballXw = Memory.ReadSignedWord(a2BallAddr + 32);
        short defDx = Memory.ReadSignedWord(Memory.Addr.kDefaultDestinations + destByteOff);
        short newDestX = (short)(ballXw + defDx);
        Memory.WriteWord(a2BallAddr + 58, newDestX);   // ball.destX

        // 2379-2391 — D1 = ball.y.w + kDefaultDestinations[D0+2].
        short ballYw = Memory.ReadSignedWord(a2BallAddr + 36);
        short defDy = Memory.ReadSignedWord(Memory.Addr.kDefaultDestinations + destByteOff + 2);
        short newDestY = (short)(ballYw + defDy);
        Memory.WriteWord(a2BallAddr + 60, newDestY);   // ball.destY

        // 2392-2416 — destX += ((gameTick & 31) << 5) - 512. X jitter.
        ushort gameTick = Memory.ReadWord(Memory.Addr.currentGameTick);
        short jitterX = (short)(((gameTick & 31) << 5) - 512);
        short curDestX = Memory.ReadSignedWord(a2BallAddr + 58);
        Memory.WriteWord(a2BallAddr + 58, (short)(curDestX + jitterX));

        // 2417-2455 — A0 = team.shotChanceTable.
        //   D1 = (gameTick & 0x3C) >> 2 (0..15 random sample from gameTick mid bits)
        //   thresh1 = shotChanceTable[+52]; D1 -= thresh1; if signed-negative → strong
        //   thresh2 = shotChanceTable[+54]; D1 -= thresh2; if signed-negative → medium
        //   else weak.
        int shotChanceTablePtr = Memory.ReadSignedDword(a6TeamBase + TeamData.OffShotChanceTable);
        short d1 = (short)((gameTick & 0x3C) >> 2);
        short thresh1, thresh2;
        if (shotChanceTablePtr != 0)
        {
            thresh1 = Memory.ReadSignedWord(shotChanceTablePtr + 52);
            thresh2 = Memory.ReadSignedWord(shotChanceTablePtr + 54);
        }
        else
        {
            // STUB: shotChanceTable not yet populated by team init in port path.
            // Treat as max thresholds (always-weak deflection).
            thresh1 = 32767;
            thresh2 = 32767;
        }

        short deflectSpeed;
        d1 = (short)(d1 - thresh1);
        if (d1 < 0)
        {
            // 2469-2471 — l_strong_deflect.
            deflectSpeed = Memory.ReadSignedWord(Memory.Addr.kGoalkeeperStrongDeflectBallSpeed);
            goto l_set_ball_speed;
        }

        d1 = (short)(d1 - thresh2);
        if (d1 < 0)
        {
            // 2461-2467 — l_medium_deflect.
            deflectSpeed = Memory.ReadSignedWord(Memory.Addr.kGoalkeeperMediumDeflectBallSpeed);
            goto l_set_ball_speed;
        }

        // 2457-2459 — fall-through path = weak deflect.
        deflectSpeed = Memory.ReadSignedWord(Memory.Addr.kGoalkeeperWeakDeflectBallSpeed);

    l_set_ball_speed:;
        // 2473-2495 — D0 = (gameTick & 0x1FF) - 256;
        //             ball.speed = D0 + D1 (with D1 = deflectSpeed).
        short speedJitter = (short)((gameTick & 0x1FF) - 256);
        short newSpeed = (short)(speedJitter + deflectSpeed);
        Memory.WriteWord(a2BallAddr + 44, newSpeed);   // ball.speed

        // 2496-2525 — D1 = kGoalkeeperDeflectDeltaZ (dword).
        //             D0 = ((gameTick & 0x7F) << 8) - 16384 + D1.
        //             ball.deltaZ = D0.
        int deflectDz = Memory.ReadSignedDword(Memory.Addr.kGoalkeeperDeflectDeltaZ);
        int dzJitter = (int)(((uint)gameTick & 0x7Fu) << 8) - 16384;
        int newDeltaZ = dzJitter + deflectDz;
        Memory.WriteDword(a2BallAddr + 54, newDeltaZ);   // ball.deltaZ

        // 2526 — resetBothTeamSpinTimers.
        BallUpdate.ResetBothTeamSpinTimers();

        // 2527 — SWOS::PlayKickSample. STUB — audio not yet wired.
        PlayKickSample();
    }


    // ====================================================================
    // RunShotTripWire — updatePlayers.cpp:2072-2194 (cseg_7F7BC)
    // RunShotAtGoal  — updatePlayers.cpp:2196-2544 (l_shot_at_goal)
    // ====================================================================
    //
    // TASK #71 refactor (2026-07-03): the old CheckShotAtGoalAndKeeperSave
    // fused cseg_7F7BC with l_shot_at_goal and flattened every asm exit label
    // to `return false`, losing the exits' side-effects — cseg_7FC48 chases
    // the ball (dest = ball.xy, speed = [scTab+6], :2569-2594), cseg_7FC01
    // falls back to ballNotHigh tracking at [scTab+8] speed (:2552-2567) and
    // cseg_7FBEF sets speed = dseg_1105EF (:2546-2550). Those exits are now
    // surfaced via ShotChainExit so the caller
    // (UpdatePlayers.RunGoalkeeperInAreaChain) can route them exactly like
    // the original UpdatePlayers body.
    //
    // Inputs (mirror asm regs): A1 keeper sprite, A2 ball sprite, A6 team.
    public enum ShotChainExit
    {
        ShotAtGoal,   // jmp @@shot_at_goal
        C7FC48,       // jmp cseg_7FC48 (track ball at row[+6] speed)
        C7FBEF,       // jmp cseg_7FBEF (speed = dseg_1105EF)
        C7FC01,       // jmp cseg_7FC01 (ballNotHigh dest at row[+8] speed)
        Clamp,        // jmp @@clamp_ball_y_inside_pitch
    }

    // from updatePlayers.cpp:2072 (`cseg_7F7BC` entry).
    public static ShotChainExit RunShotTripWire(int a1KeeperAddr, int a2BallAddr,
        int a6TeamBase, bool isTopTeam)
    {
        // ----- cseg_7F7BC ---------------------------------------------------
        // updatePlayers.cpp:2073-2083 — `eax = opponentsTeam; ax = playerHasBall;
        // jnz cseg_7FC48` (opponent controls the ball → keeper tracks it).
        int opponentBase = Memory.ReadSignedDword(a6TeamBase + TeamData.OffOpponentsTeam);
        short opponentHasBall = Memory.ReadSignedWord(opponentBase + TeamData.OffPlayerHasBall);
        if (opponentHasBall != 0) return ShotChainExit.C7FC48;

        // 2085-2092 — `ax = this.playerHasBall; jnz cseg_7FC48`.
        short thisHasBall = Memory.ReadSignedWord(a6TeamBase + TeamData.OffPlayerHasBall);
        if (thisHasBall != 0) return ShotChainExit.C7FC48;

        // 2094-2100 — `al = plVeryCloseToBall; jnz cseg_7FC48`. The keeper's
        // own outfielders close to the ball are about to take possession.
        byte plVeryClose = Memory.ReadByte(a6TeamBase + TeamData.OffPlVeryCloseToBall);
        if (plVeryClose != 0) return ShotChainExit.C7FC48;

        // 2102-2121 — `eax = lastTeamPlayed; cmp A6, eax; jnz l_check_shot_at_goal_speed`.
        // Else `ax = playerHadBall; jz cseg_7FBEF`.
        int lastTeamPlayed = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayed);
        if (a6TeamBase == lastTeamPlayed)
        {
            short playerHadBall = Memory.ReadSignedWord(Memory.Addr.playerHadBall);
            if (playerHadBall == 0)
                return ShotChainExit.C7FBEF;
            // Fall-through to l_check_shot_at_goal_speed.
        }

        // ----- l_check_shot_at_goal_speed (updatePlayers.cpp:2123) ----------
        {
            short kMin = Memory.ReadSignedWord(Memory.Addr.kShotAtGoalMinumumSpeed);
            short ballSpeed = Memory.ReadSignedWord(a2BallAddr + 44);   // ball.speed (word, +44)
            // 2128-2138 — `cmp d0, ax; jb l_shot_at_goal` (unsigned-below).
            // If kMin < ballSpeed → ball is fast → l_shot_at_goal.
            if ((ushort)kMin < (ushort)ballSpeed) return ShotChainExit.ShotAtGoal;
        }

        // 2140-2152 — `cmp [esi+74], 5000; ja cseg_7FC01` (ballDistance > 5000).
        int ballDistance = Memory.ReadSignedDword(a1KeeperAddr + 74);
        if ((uint)ballDistance > 5000u) return ShotChainExit.C7FC01;

        // 2154-2166 — `cmp word ptr [esi+(Sprite.z+2)], 12; ja cseg_7FC01`.
        short ballZw = Memory.ReadSignedWord(a2BallAddr + 40);
        if ((ushort)ballZw > 12) return ShotChainExit.C7FC01;

        // 2168-2192 — `cmp [esi+Sprite.deltaZ], 8000h ; jg cseg_7FC01 ;
        // cmp [esi+Sprite.deltaZ], 8000h ; jl cseg_7FC01`. Only deltaZ ==
        // 0x8000 exactly falls through (to cseg_7FC48 at :2194).
        int ballDeltaZ = Memory.ReadSignedDword(a2BallAddr + 54);
        if (ballDeltaZ > 0x8000) return ShotChainExit.C7FC01;
        if (ballDeltaZ < 0x8000) return ShotChainExit.C7FC01;

        // 2194 — jmp cseg_7FC48.
        return ShotChainExit.C7FC48;
    }

    // from updatePlayers.cpp:2196 (`l_shot_at_goal`). Returns C7FC01 when the
    // asm jumps to cseg_7FC01 (no dive), Clamp when it exits via
    // l_clamp_ball_y_inside_pitch (goal-scored branch or a committed dive).
    public static ShotChainExit RunShotAtGoal(int a1KeeperAddr, int a2BallAddr,
        int a6TeamBase, bool isTopTeam)
    {
        int opponentBase = Memory.ReadSignedDword(a6TeamBase + TeamData.OffOpponentsTeam);

        // 2197-2209 — `cmp word ptr [esi+(Sprite.z+2)], 16; ja cseg_7FC01`.
        // Ball too high → no dive.
        short ballZwTop = Memory.ReadSignedWord(a2BallAddr + 40);
        if ((ushort)ballZwTop > 16) return ShotChainExit.C7FC01;

        // 2211-2218 — `ax = team.goalkeeperSavedCommentTimer; jg l_goalkeeper_saved`.
        // Positive timer → keeper just made a save, force the dive path.
        short gksTimer = TeamData.GoalkeeperSavedCommentTimer(isTopTeam);
        bool forceSavedPath = (gksTimer > 0);

        if (!forceSavedPath)
        {
            // 2220-2232 — `cmp [keeper.ballDistance], 128; ja l_goalkeeper_saved`.
            int keeperBallDistance = Memory.ReadSignedDword(a1KeeperAddr + 74);
            if (keeperBallDistance > 128)
            {
                forceSavedPath = true;
            }
        }

        if (!forceSavedPath)
        {
            // 2234-2241 — `al = team.ballAbove17; jnz l_goalkeeper_saved`.
            byte ballAbove17 = Memory.ReadByte(a6TeamBase + 68);   // ballAbove17 @ +68
            if (ballAbove17 != 0) forceSavedPath = true;
        }

        if (!forceSavedPath)
        {
            // 2243-2252 — `eax = opponentsTeam; al = opponent.firePressed; jz l_goalkeeper_saved`.
            // Opponent hasn't pressed fire (i.e. ball is on the move but not a
            // committed kick) → keeper still attempts the save.
            byte opFire = Memory.ReadByte(opponentBase + TeamData.OffFirePressed);
            if (opFire == 0) forceSavedPath = true;
        }

        if (!forceSavedPath)
        {
            // 2254-2260 — `ax = opponent.playerHasBall; jnz cseg_7F910` (skip to
            // RNG). 2262-2273 — `cmp opponent.passKickTimer, 22; jb l_goalkeeper_saved`.
            short opHasBall = Memory.ReadSignedWord(opponentBase + TeamData.OffPlayerHasBall);
            if (opHasBall == 0)
            {
                short opPassKick = Memory.ReadSignedWord(opponentBase + TeamData.OffPassKickTimer);
                if ((ushort)opPassKick < 22) forceSavedPath = true;
            }
        }

        // ----- cseg_7F910 — RNG goal-roll (updatePlayers.cpp:2275-2362) -----
        if (!forceSavedPath)
        {
            // 2276-2293 — `*D1 = 0; eax = opponent.lastHeadingTacklingPlayer; jz l_get_goalie_skill`.
            // If non-zero → load finishing skill from that player's
            // PlayerGameHeader (+75 = finishing of PlayerInfo → +33 in our
            // PlayerInfo-only layout).
            int d1Finishing = 0;
            int lastHeadTackle = Memory.ReadSignedDword(opponentBase + TeamData.OffLastHeadingPlayer);
            if (lastHeadTackle != 0)
            {
                // 2295-2312 — getPlayerPointerFromShirtNumber + finishing byte
                // read. The opponent sprite's player-info address resolves via
                // the team's in-game team players base + (ordinal - 1) * 61.
                short ordinal = Memory.ReadSignedWord(lastHeadTackle + PlayerSprite.OffPlayerOrdinal);
                int opPlayersBase = OpponentPlayersBase(isTopTeam);
                if (ordinal >= 1 && ordinal <= 11 && opPlayersBase != 0)
                {
                    int piAddr = opPlayersBase + (ordinal - 1) * 61;
                    d1Finishing = (sbyte)Memory.ReadByte(piAddr + 33);   // PlayerInfo.finishing
                }
            }

            // 2314-2333 — `l_get_goalie_skill`: A4 = getPlayerPointerFromShirtNumber(A6, A1).
            // Reads goalieSkill (PlayerInfo+34). D1 = finishing - goalieSkill;
            // sign-extend; D1 += 7.
            int d1GoalieSkill = 0;
            {
                short kOrdinal = Memory.ReadSignedWord(a1KeeperAddr + PlayerSprite.OffPlayerOrdinal);
                int myPlayersBase = OwnPlayersBase(isTopTeam);
                if (kOrdinal >= 1 && kOrdinal <= 11 && myPlayersBase != 0)
                {
                    int piAddr = myPlayersBase + (kOrdinal - 1) * 61;
                    d1GoalieSkill = (sbyte)Memory.ReadByte(piAddr + 34);   // PlayerInfo.goalieSkill
                }
            }
            // OpenSWOS fatigue: a tired keeper saves worse — subtract the fatigue
            // penalty (0/1/2 at >50% / <=50% / <=20% energy) from his save skill,
            // so d1 = finishing - skill grows → higher goal chance. User spec.
            d1GoalieSkill -= PlayerEnergy.KeeperSkillPenalty(a1KeeperAddr);
            if (d1GoalieSkill < 0) d1GoalieSkill = 0;
            int d1 = d1Finishing - d1GoalieSkill;
            // 2326 — `cbw`: sign-extend low byte to word. Mirror by masking.
            d1 = (sbyte)(d1 & 0xFF);
            d1 += 7;
            if (d1 < 0) d1 = 0;
            if (d1 > 14) d1 = 14;

            // 2334-2347 — `A0 = goalScoredChances; D0 = (currentGameTick >> 1) & 15;
            //              al = goalScoredChances[ebx=D1]`.
            ushort gameTick = Memory.ReadWord(Memory.Addr.currentGameTick);
            int d0Sample = (gameTick >> 1) & 15;
            byte threshold = Memory.ReadByte(Memory.Addr.goalScoredChances + d1);

            // 2348-2358 — `cmp byte ptr D0, al; jb l_goal_scored`.
            // Signed cmp; if D0 < threshold (unsigned) → goal scored.
            if ((byte)d0Sample < threshold)
            {
                // ----- l_goal_scored (updatePlayers.cpp:2364-2428) -----------
                // The RNG decided the shot is going in. Mark the keeper as
                // "about to be beaten", swap the keeper's facing direction to
                // match the ball's X side, cap ball speed at 1536, clear
                // spin/pass timers and call goalkeeperJumping as a futile dive.
                // :2428 — jmp @@clamp_ball_y_inside_pitch.
                ApplyGoalScoredBranch(a1KeeperAddr, a2BallAddr, a6TeamBase, opponentBase, isTopTeam);
                return ShotChainExit.Clamp;
            }

            // 2360-2362 — `writeMemory(A6+76, 2, 5)` then `jmp l_goalkeeper_saved`.
            // Keeper save imminent — commentary timer fires.
            TeamData.SetGoalkeeperSavedCommentTimer(isTopTeam, 5);
        }

        // ----- l_goalkeeper_saved (updatePlayers.cpp:2430-2544) -------------
        // Calls ShouldGoalkeeperDive; if true, picks dive direction based on
        // ball X side vs. ballDefensiveX, calls GoalkeeperJumping (exit
        // :2544 jmp @@clamp…). If ShouldGoalkeeperDive said no —
        // :2437-2438 `jz cseg_7FC01`.
        return ApplyGoalkeeperSavedBranch(a1KeeperAddr, a2BallAddr, a6TeamBase, isTopTeam)
            ? ShotChainExit.Clamp
            : ShotChainExit.C7FC01;
    }

    // updatePlayers.cpp:2364-2428 — l_goal_scored branch.
    // RNG-decided "shot beats keeper" path: still calls goalkeeperJumping for
    // the futile dive animation; the actual goal credit is awarded by ball.cpp
    // when the ball crosses the line.
    private static void ApplyGoalScoredBranch(int a1KeeperAddr, int a2BallAddr,
        int a6TeamBase, int opponentBase, bool isTopTeam)
    {
        // 2365-2366 — `writeMemory(A6+76, 2, -5)`. Negative = "scored against".
        TeamData.SetGoalkeeperSavedCommentTimer(isTopTeam, -5);

        // 2367-2391 — D1 = 2; D0 = ball.x.w; cmp with keeper.x.w; if ball X <
        // keeper X → D0 = D3 = 6 (face W), else D0 = D3 = 2 (face E).
        short ballXw = Memory.ReadSignedWord(a2BallAddr + 32);
        short keeperXw = Memory.ReadSignedWord(a1KeeperAddr + 32);
        short d0Direction;
        short d3DestDir;
        if ((ushort)ballXw < (ushort)keeperXw)
        {
            d0Direction = 6;
            d3DestDir = 6;
        }
        else
        {
            d0Direction = 2;
            d3DestDir = 2;
        }

        // 2393-2396 — keeper.direction = D0.
        Memory.WriteWord(a1KeeperAddr + PlayerSprite.OffDirection, d0Direction);

        // 2397-2398 — mirror ballSprite.speed into dseg_114EA8.
        //   ax = ballSprite.speed; mov ballSpeed, ax. STUB: ballSpeed mirror
        //   (g_memByte[337178]) isn't wired. The mirror is read by audio /
        //   stats only.

        // 2399-2403 — opponent.spinTimer = -1. Cancels any in-flight spin.
        TeamData.SetSpinTimer(!isTopTeam, -1);

        // 2404-2417 — cap ball.speed at 1536.
        short ballSpeed = Memory.ReadSignedWord(a2BallAddr + 44);
        if ((ushort)ballSpeed > 1536u)
        {
            Memory.WriteWord(a2BallAddr + 44, 1536);
        }

        // 2421 — goalkeeperJumping(). Standard far-jump speed flag (D1 = 0
        // because D1 was reset to 2 at the start of l_goal_scored but
        // goalkeeperJumping reads team.controlledPlDirection from the new D0,
        // not the inherited D1; we pass d1Flag=0 for "default speed").
        GoalkeeperJumping(d0Direction, 0, d3DestDir, a1KeeperAddr, a2BallAddr, a6TeamBase);

        // 2423-2424 — mirror ballSprite.speed again (audio cue). STUB.

        // 2425-2427 — passKickTimer = 0; passingKickingPlayer = 0 on OUR team.
        Memory.WriteWord(a6TeamBase + TeamData.OffPassKickTimer, 0);
        Memory.WriteDword(a6TeamBase + TeamData.OffPassingKickingPlayer, 0);

        // 2428 — `jmp l_clamp_ball_y_inside_pitch`. The clamp is run by ball.cpp
        // each tick; we don't need to do anything here.
    }

    // updatePlayers.cpp:2430-2544 — l_goalkeeper_saved branch.
    // Calls ShouldGoalkeeperDive; if it agrees, GoalkeeperJumping. Direction
    // selection mirrors the asm: pick D0 = 2 (E) or 6 (W) based on which side
    // of ballDefensiveX the keeper is, with extra randomisation during
    // penalties. Returns true when the dive was committed (asm exits via
    // l_clamp_ball_y_inside_pitch), false when ShouldGoalkeeperDive vetoed
    // (asm :2437-2438 `jz cseg_7FC01`).
    private static bool ApplyGoalkeeperSavedBranch(int a1KeeperAddr, int a2BallAddr,
        int a6TeamBase, bool isTopTeam)
    {
        // 2431-2438 — call shouldGoalkeeperDive; if 0 → cseg_7FC01 (skip).
        bool shouldDive = ShouldGoalkeeperDive(a1KeeperAddr, a2BallAddr, a6TeamBase);
        if (!shouldDive) return false;

        // 2440-2454 — D0 = ballDefensiveX; cmp with keeper.x.w; if ballDefX <
        // keeper.x → cseg_7FB57 (W-side dive). Else fall-through to cseg_7FB1B
        // (E-side dive) area.
        short ballDefX = Memory.ReadSignedWord(Memory.Addr.ballDefensiveX);
        short keeperXw = Memory.ReadSignedWord(a1KeeperAddr + 32);

        short d0Dir;
        short d1SpeedFlag;
        short d3DestDir;

        if ((ushort)ballDefX < (ushort)keeperXw)
        {
            // 2495-2533 — W-side dive selection.
            d1SpeedFlag = 0;
            short playingPenalties = Memory.ReadSignedWord(Memory.Addr.playingPenalties);
            short penalty = Memory.ReadSignedWord(Memory.Addr.penalty);
            bool isPenalty = (playingPenalties != 0) || (penalty != 0);
            if (isPenalty)
            {
                // cseg_7FB76 — D1 = 1; if (gameTick & 12) == 0 → cseg_7FB43 → use 2/2.
                d1SpeedFlag = 1;
                ushort gameTick = Memory.ReadWord(Memory.Addr.currentGameTick);
                if ((gameTick & 12) == 0)
                {
                    // cseg_7FB43 — D0 = 2; D3 = 2 (E-side dive variant).
                    d0Dir = 2;
                    d3DestDir = 2;
                }
                else
                {
                    // cseg_7FB9E — D0 = 6; D3 = 6.
                    d0Dir = 6;
                    d3DestDir = 6;
                    d1SpeedFlag = 0;
                }
            }
            else
            {
                // cseg_7FB9E — D0 = 6; D3 = 6 (face W).
                d0Dir = 6;
                d3DestDir = 6;
            }
        }
        else
        {
            // 2456-2493 — E-side dive selection.
            // TASK #71 FIX (2026-07-03): the D1 speed-flag assignments were
            // SWAPPED versus the asm. cseg_7FB1B (:2473-2488) sets D1 = 1,
            // then `and D0, 6`: if ZERO it jumps to cseg_7FB9E (D0=D3=6)
            // KEEPING D1 = 1; if non-zero it clears D1 = 0 and falls into
            // cseg_7FB43 (D0=D3=2). Our port had the flag values inverted
            // across the two arms (penalties only).
            d1SpeedFlag = 0;
            short playingPenalties = Memory.ReadSignedWord(Memory.Addr.playingPenalties);
            short penalty = Memory.ReadSignedWord(Memory.Addr.penalty);
            bool isPenalty = (playingPenalties != 0) || (penalty != 0);
            if (isPenalty)
            {
                // cseg_7FB1B (:2473-2488) — D1 = 1; if (gameTick & 6) == 0 →
                // cseg_7FB9E (6/6, D1 stays 1); else D1 = 0 → cseg_7FB43 (2/2).
                d1SpeedFlag = 1;
                ushort gameTick = Memory.ReadWord(Memory.Addr.currentGameTick);
                if ((gameTick & 6) == 0)
                {
                    // cseg_7FB9E (:2530-2532).
                    d0Dir = 6;
                    d3DestDir = 6;
                }
                else
                {
                    // :2488 — D1 = 0; cseg_7FB43 (:2490-2492).
                    d1SpeedFlag = 0;
                    d0Dir = 2;
                    d3DestDir = 2;
                }
            }
            else
            {
                // cseg_7FB43.
                d0Dir = 2;
                d3DestDir = 2;
            }
        }

        // 2535-2540 — keeper.direction = D0; goalkeeperJumping.
        Memory.WriteWord(a1KeeperAddr + PlayerSprite.OffDirection, d0Dir);
        GoalkeeperJumping(d0Dir, d1SpeedFlag, d3DestDir, a1KeeperAddr, a2BallAddr, a6TeamBase);

        // 2541-2543 — passKickTimer = 0; passingKickingPlayer = 0.
        Memory.WriteWord(a6TeamBase + TeamData.OffPassKickTimer, 0);
        Memory.WriteDword(a6TeamBase + TeamData.OffPassingKickingPlayer, 0);
        return true;
    }

    // Resolve the PlayerInfo base for THIS team. Mirrors swos-port's
    // team.inGameTeamPtr field, which points at the in-game players[0] of
    // the team. Returns 0 if the in-game team pointer isn't wired.
    private static int OwnPlayersBase(bool isTopTeam)
    {
        int teamBase = TeamData.Base(isTopTeam);
        return Memory.ReadSignedDword(teamBase + TeamData.OffInGameTeamPtr);
    }

    private static int OpponentPlayersBase(bool isTopTeam)
    {
        return OwnPlayersBase(!isTopTeam);
    }


    // ====================================================================
    // Stubs for callees not yet ported
    // ====================================================================

    // updatePlayers.cpp:10930 / 11048 etc. — sets sprite's animation table
    // pointer. Forwards to the real port in PlayerActions (swos.asm:104309).
    // The asm function sets sprite.animationTable, sprite.frameIndicesTable,
    // sprite.frameDelay, sprite.frameSwitchCounter, sprite.frameIndex,
    // sprite.cycleFramesTimer, sprite.startingDirection so the renderer can
    // step through frames per direction.
    private static void SetPlayerAnimationTable(int spriteAddr, int animTablePtr)
    {
        PlayerActions.SetPlayerAnimationTable(spriteAddr, animTablePtr);
    }

    // player.cpp:2527 — SWOS::PlayKickSample. Plays the kick / parry SFX.
    private static void PlayKickSample() => OpenSwos.Audio.MatchAudio.PlayKick();
}
