namespace OpenSwos.SwosVm;

// SWOS memory abstraction. Mirrors swos-port's `g_memByte[]` global byte array —
// the runtime image of the original DOS .DATA segment + sprite memory pool.
// All asm-translated functions in swos-port read/write through these offsets:
//
//   ax = *(word *)&g_memByte[325628];   // mov ax, kBallGroundConstant
//   writeMemory(esi + 44, 2, ax);       // mov [esi+Sprite.speed], ax
//
// Our `Read*` / `Write*` helpers wrap a single `byte[]` backing array so the
// ported functions translate near-mechanically. Endian is **little-endian**
// (x86 DOS — swos-port preserves this even though original Amiga was BE; the
// asm we're translating is the IDA disassembly of the PC binary).
//
// Memory layout (matches swos-port's `g_memByte` indexing):
//   0x00000 .. 0x4F7FF  → DOS .DATA segment (constants, tables, working state)
//   0x4F800 .. 0x4FFFF  → sprite memory pool (~32 sprites × ~80 bytes)
//
// Sized to fit the largest known offset (kGoalkeeperDiveDeltas table at 0x4F1A8
// per amigaMode.cpp:9-14) plus sprite pool. 320 KB total — fits in L2 cache.
public static class Memory
{
    // 384 KB. Bumped from 320 KB to fit per-team sprite tables + sprite pool
    // for Phase B5 (keeper port). Layout breakdown:
    //   0x00000 .. 0x4F7FF  → DOS .DATA segment (constants, tables, working state)
    //   0x4F800 .. 0x4F87F  → BallSprite (128 bytes — 110 used)
    //   0x4F880 .. 0x4F8BF  → BallShadowSprite (placeholder)
    //   0x4F900 .. 0x4FAFF  → TeamData top (512 bytes — 145 used)
    //   0x4FB00 .. 0x4FCFF  → TeamData bottom (512 bytes — 145 used)
    //   0x4FD00 .. 0x4FDFF  → Referee + BookedPlayerNumberSprite (256 bytes)
    //   0x4FE00 .. 0x4FE2B  → team1SpritesTable (11 × 4 bytes = 44 bytes)
    //   0x4FE2C .. 0x4FE57  → team2SpritesTable (11 × 4 bytes = 44 bytes)
    //   (relocated 2026-05-27 — previous 0x4F8C0/0x4F8EC overlapped TopBase
    //    fields and got corrupted once AI walk started reading the tables)
    //   0x50000 .. 0x50DFF  → Player sprite pool (22 × 128 = 2816 bytes; only
    //                          110 bytes/slot used, rest padding for alignment).
    //                          Slot 0..10 = team1 sprites (slot 0 = goalie1)
    //                          Slot 11..21 = team2 sprites (slot 11 = goalie2)
    private const int kMemSize = 0x60000;   // 384 KB
    private static readonly byte[] mem = new byte[kMemSize];
    private static bool initialised = false;

    // ---- Constants section (subset; expand as port pulls more in) ------------
    // Addresses come from swos-port `swos/swos.asm` data-section lines.

    // Address layout — we allocate constants in a packed "data section" starting
    // at 0x10000 (well below sprite pool at 0x4F800). The numbers don't have to
    // match the swos-port absolute addresses byte-for-byte; what matters is that
    // each named constant has a unique, non-overlapping address and stable size.
    //
    // When B4 ports asm-translated updateBall, those `g_memByte[NNN]` reads will
    // resolve to NAMED constants via Memory.Addr.*. The actual offsets stay
    // owned by us — easier to manage than line-numbers-as-addresses (which the
    // initial port draft incorrectly used).
    //
    // Annotations show the swos.asm SOURCE LINE for traceability, NOT the
    // address (those are distinct concepts).
    public static class Addr
    {
        private const int DataBase = 0x10000;

        // Ball kick speeds — swos.asm line 203952 etc. (word, Q8.8).
        public const int kBallKickingSpeed       = DataBase + 0x00;  // 2208
        public const int kHighKickBallSpeed      = DataBase + 0x02;  // 2688
        public const int kNormalKickBallSpeed    = DataBase + 0x04;  // 2560
        public const int kPlayerTacklingSpeed    = DataBase + 0x06;  // 1792
        public const int kJumpHeaderSpeed        = DataBase + 0x08;  // 2048

        // Goalkeeper speeds.
        public const int kGoalkeeperCatchSpeed           = DataBase + 0x0A;  // 768
        public const int kGoalkeeperMoveToBallSpeed      = DataBase + 0x0C;  // 1024
        public const int kGoalkeeperSpeedWhenGameStopped = DataBase + 0x0E;  // 1024
        public const int kSubstitutedPlayerSpeed         = DataBase + 0x10;  // 1536
        public const int kRefereeSpeed                   = DataBase + 0x12;  // 1024

        // Vertical deltas (Q16.16, 4 bytes each).
        public const int kBallKickingDeltaZ      = DataBase + 0x14;  // 0x14000
        public const int kBallJumpHeaderDeltaZ   = DataBase + 0x18;  // 0xA000

        // Set by amigaMode.cpp per game style (PC vs Amiga). Writable.
        public const int kBallGroundConstant     = DataBase + 0x1C;  // word — PC=13 / Amiga=16
        public const int kBallAirConstant        = DataBase + 0x1E;  // word — PC=4  / Amiga=10
        // kGravityConstant is a DWORD in SWOS (swos.asm:203966 `dd 0CDBh`).
        // ball.cpp:467 reads it as dword. Spacing reserves 4 bytes.
        public const int kGravityConstant        = DataBase + 0x20;  // dword — PC=3291 / Amiga=4608
        public const int kKeeperSaveDistance     = DataBase + 0x24;  // word — PC=16 / Amiga=24

        // Bounce + per-pitch (words).
        public const int ballSpeedBounceFactor   = DataBase + 0x28;
        public const int ballBounceFactor        = DataBase + 0x2A;
        public const int pitchBallSpeedFactor    = DataBase + 0x2C;

        // Goalkeeper dive deltas — 8 × dword indexed by keeper skill 0..7.
        // 32 bytes total.
        public const int kGoalkeeperDiveDeltasBase = DataBase + 0x40;

        // kBallPlOffsets — outfielder dribble unit vectors per direction
        // (8 × 2 signed words = 32 bytes).
        public const int kBallPlOffsetsBase = DataBase + 0x80;

        // ---- Spin / kick-after-touch tables (swos.asm:203954-203989) -------
        // High/normal kick deltaZ + speed are activated when the controlling
        // player holds joystick BACKWARD relative to kick direction at spinTimer==4.
        public const int kHighKickDeltaZ        = DataBase + 0xA0;  // dword 0x20000 (Q16.16)
        public const int kNormalKickDeltaZ      = DataBase + 0xA4;  // dword 0x16000 (Q16.16)
        // kHighKickBallSpeed (2688) + kNormalKickBallSpeed (2560) are already
        // defined above — reuse those.

        // kSpinMultiplierFactor[10] — 10 words, indexed by spinTimer 0..9.
        // {5, 4, 3, 2, 2, 2, 2, 1, 1, 1}.
        public const int kSpinMultiplierFactor  = DataBase + 0xB0;  // 20 bytes

        // kKickSpinFactor[8 directions][4 words] — curved trajectory offsets
        // applied to ball.destX / destY each tick. Format per direction:
        //   word 0: leftSpin  X delta
        //   word 1: leftSpin  Y delta
        //   word 2: rightSpin X delta
        //   word 3: rightSpin Y delta
        // Address-formula in ApplyBallAfterTouch: direction*8 + (rightSpin?4:0) = byte offset.
        public const int kKickSpinFactor        = DataBase + 0xD0;  // 64 bytes

        // kPassingSpinFactor[8 directions][4 words] — same layout, smaller values.
        public const int kPassingSpinFactor     = DataBase + 0x120; // 64 bytes

        // ---- Global game state words (swos.asm:523xxx region) --------------
        // gameStatePl @ 523116: ST_GAME_IN_PROGRESS (100), other "playable" sub-states.
        // gameState   @ 523118: ST_KEEPER_HOLDS_BALL (3), pre-kickoff (other vals).
        public const int gameStatePl            = DataBase + 0x180;
        public const int gameState              = DataBase + 0x182;

        // calculateNextBallPosition (ball.cpp:4498-4500) writes predicted
        // whole-pixel ball position here. Used by keeper/player AI to anticipate
        // ball motion (e.g., decide whether to dive).
        public const int ballNextX              = DataBase + 0x184;  // word
        public const int ballNextY              = DataBase + 0x186;  // word

        // hideBall flag (ball.cpp:15 `g_memByte[449800]`). Non-zero = ball
        // sprite hidden (e.g., during cutscenes or penalty cinema). Frame index
        // is set to -1 by updateBall when this is set.
        public const int hideBall               = DataBase + 0x188;  // word

        // ballShadowSprite — separate sprite for ball shadow. The frame index
        // is updated in lockstep with ball state. Field offsets within match
        // standard Sprite struct.
        // imageIndex offset: ballShadowSprite + 22 in swos-port, but we just
        // need a stable "shadow imageIndex" word.
        public const int ballShadowImageIndex   = DataBase + 0x18A;  // word

        // ballStaticFrameIndices and ballMovingFrameIndices — addresses of
        // frame-index tables in swos.asm. updateBall switches between them
        // based on whether ball has motion. We store TABLE BASE ADDRESSES here
        // (4 bytes each — these are "pointers" in 32-bit code).
        public const int ballStaticFrameIndices_Addr  = DataBase + 0x18C;  // dword — pointer
        public const int ballMovingFrameIndices_Addr  = DataBase + 0x190;  // dword — pointer

        // The actual frame-index tables — short[] arrays read by sprite anim
        // code. ballStaticFrameIndices = single frame (no animation).
        // ballMovingFrameIndices = N frames (rolling animation).
        public const int ballStaticFrameIndices_Table = DataBase + 0x200;  // bytes
        public const int ballMovingFrameIndices_Table = DataBase + 0x210;  // bytes

        // Substitution state (ball.cpp:338, 346 / g_memByte 486156 + 486164).
        // Used by updateBall section 5 to bypass keeper-holds-ball physics
        // when the team that held the ball is being substituted.
        public const int g_substituteInProgress     = DataBase + 0x220;  // word — 0 = no substitution
        public const int teamThatSubstitutes        = DataBase + 0x224;  // dword — pointer to TeamData of subbing team

        // ball.cpp:348 / g_memByte 523108. Pointer to TeamData of team that
        // had the ball before a break (kick-off, goal-out etc.). Used to
        // determine which keeper should handle ball during set pieces.
        public const int lastTeamPlayedBeforeBreak  = DataBase + 0x228;  // dword — pointer

        // game.cpp:1424-1425 — `team{1,2}NumSubs = 0`. Per-team substitution
        // counter (number of subs made so far this match). Initialized to 0
        // by initGameVariables at every match boot; incremented on each
        // sub-on (updateBench.cpp). Used by the substitutions menu to gate
        // the "no more subs" disable. Limited to gameMaxSubstitutes
        // (typically 3 or 5).
        // Source: external/swos-port/swos/symbols.txt:646-647 — both word-typed.
        public const int team1NumSubs               = DataBase + 0x22C;  // word
        public const int team2NumSubs               = DataBase + 0x22E;  // word

        // updatePlayers.cpp:12681 writes ballNextGroundX/Y (the predicted ball
        // landing X/Y in whole pixels). Distinct from ballNextX/Y (predicted
        // next-tick position): ballNextGround* is "where will the ball be
        // when it FINALLY lands (z=0)" used by keeper to set destination.
        public const int ballNextGroundX            = DataBase + 0x230;  // word
        public const int ballNextGroundY            = DataBase + 0x232;  // word
        // updatePlayers.cpp:12685 writes 0 here unconditionally (named
        // ballNextZDeadVar in swos-port). Kept distinct from ballNextGroundY
        // because writes are word-sized and adjacent.
        public const int ballNextGroundZDead        = DataBase + 0x234;  // word

        // updatePlayers.cpp:11300-11421 writes — ballDefensive{Y,Z} (X exists
        // below at 0x4B8) is a whole-pixel snapshot of ball state when its
        // predicted trajectory intersects the player's Y line (or pitch
        // boundary). Used by AI to decide chase / shield posture.
        public const int ballDefensiveY             = DataBase + 0x600;  // word
        public const int ballDefensiveZ             = DataBase + 0x602;  // word

        // ballNotHigh{X,Y} (Z exists below at 0x4BA) — second snapshot at the
        // 135-pixel Y band (Q16.16 0x870000) for the top team or 50003968
        // for the bottom team. updatePlayers.cpp:11459-11564.
        public const int ballNotHighX               = DataBase + 0x604;  // word
        public const int ballNotHighY               = DataBase + 0x606;  // word

        // strikeDestX / ballStrike{Y,Z} — final snapshot at the 129-pixel band
        // (Q16.16 0x810000). Reset to 0 when ball never crosses these bands
        // in the current trajectory. updatePlayers.cpp:11601-11704.
        // NOTE: dseg_17E661 / dseg_17E663 in swos-port comments — naming here
        // mirrors the pattern (X/Y/Z) for readability. Reused by stats.cpp:99
        // (predicted shot X intercept) — same word, same semantics.
        public const int strikeDestX                = DataBase + 0x608;  // word
        public const int ballStrikeY                = DataBase + 0x60A;  // word
        public const int ballStrikeZ                = DataBase + 0x60C;  // word

        // ---- Match score / scorer state (updateGoals.cpp) ------------------
        // Per-team goal counters (word). Cap is 99 (kMaxGoals).
        // updateGoals.cpp:7-10 — team{1,2}TotalGoals / team{1,2}PenaltyGoals.
        public const int team1TotalGoals            = DataBase + 0x240;  // word
        public const int team2TotalGoals            = DataBase + 0x242;  // word
        public const int team1PenaltyGoals          = DataBase + 0x244;  // word
        public const int team2PenaltyGoals          = DataBase + 0x246;  // word

        // Scoreline display digits + per-team stats counter.
        // updateGoals.cpp:20-21.
        public const int team1GoalsDigit1           = DataBase + 0x248;  // word
        public const int team1GoalsDigit2           = DataBase + 0x24A;  // word
        public const int statsTeam1Goals            = DataBase + 0x24C;  // word
        public const int team2GoalsDigit1           = DataBase + 0x24E;  // word
        public const int team2GoalsDigit2           = DataBase + 0x250;  // word
        public const int statsTeam2Goals            = DataBase + 0x252;  // word

        // statsCopy variables — snapshot taken at end of second half.
        // gameTime.cpp:148-149.
        public const int statsTeam1GoalsCopy        = DataBase + 0x254;  // word
        public const int statsTeam2GoalsCopy        = DataBase + 0x256;  // word

        // First-leg goals (for two-leg ties), away-goals-double tiebreaker.
        // gameTime.cpp:155-156.
        public const int team1GoalsFirstLeg         = DataBase + 0x258;  // word
        public const int team2GoalsFirstLeg         = DataBase + 0x25A;  // word

        // Per-frame goal event flags + last-scorer tracking (updateGoals.cpp:39-48).
        public const int goalScored                 = DataBase + 0x25C;  // word
        public const int runSlower                  = DataBase + 0x25E;  // word
        public const int lastTeamScoredNumber       = DataBase + 0x260;  // word (1 or 2)
        public const int lastPlayerScored           = DataBase + 0x264;  // dword — pointer to scorer sprite
        public const int currentScorer              = DataBase + 0x268;  // dword — pointer to scorer sprite
        public const int lastTeamScored             = DataBase + 0x26C;  // dword — pointer to TeamData
        public const int goalTypeScored             = DataBase + 0x270;  // dword — GoalType enum mirror
        public const int penalty                    = DataBase + 0x274;  // word — non-zero = penalty kick in progress
        public const int playingPenalties           = DataBase + 0x276;  // word — penalty shootout flag

        // In-game team pointers — point at the per-team in-game state struct
        // (separate from TeamData). updateGoals.cpp:45-46, gameTime.cpp:174-175.
        public const int topTeamInGame              = DataBase + 0x278;  // dword
        public const int bottomTeamInGame           = DataBase + 0x27C;  // dword

        // ---- Referee state (referee.cpp) -----------------------------------
        public const int refState                   = DataBase + 0x280;  // word — RefereeState enum
        public const int whichCard                  = DataBase + 0x282;  // word — CardHanding enum
        public const int foulXCoordinate            = DataBase + 0x284;  // word
        public const int foulYCoordinate            = DataBase + 0x286;  // word
        public const int refTimer                   = DataBase + 0x288;  // word
        public const int bookedPlayer               = DataBase + 0x28C;  // dword — pointer to sprite
        public const int lastTeamBooked             = DataBase + 0x290;  // dword — pointer to TeamData

        // Referee animation tables. Each is a (numCycles word + 8 indicesTable
        // dword pointers per direction) struct. Allocate 64 bytes each for
        // header + per-direction pointers.
        // referee.cpp:72, 200, 205, 209, 233, 254.
        public const int refComingAnimTable         = DataBase + 0x2A0;  // 64 bytes
        public const int refWaitingAnimTable        = DataBase + 0x2E0;  // 64 bytes
        public const int refYellowCardAnimTable     = DataBase + 0x320;  // 64 bytes
        public const int refRedCardAnimTable        = DataBase + 0x360;  // 64 bytes
        public const int refSecondYellowAnimTable   = DataBase + 0x3A0;  // 64 bytes

        // lastFrameTicks — frames elapsed since last update. ball.cpp:hash uses
        // this for spin accumulation; gameTime.cpp uses it for clock tick.
        public const int lastFrameTicks             = DataBase + 0x3E0;  // word

        // lastTeamPlayed — pointer to the TeamData of the team that last touched
        // the ball (NOT lastTeamPlayedBeforeBreak which is set on stoppage).
        // gameTime.cpp:193.
        public const int lastTeamPlayed             = DataBase + 0x3E4;  // dword

        // ---- Game length / mode settings -----------------------------------
        // gameTime.cpp:130 — gameLengthInGame in [0..3] (smaller = longer game).
        public const int gameLengthInGame           = DataBase + 0x3E8;  // word

        // ---- Game-time module private state (gameTime.cpp:18-27) -----------
        // Backs the original `static GameTime m_gameTime`, m_gameSeconds etc.
        public const int gt_gameTime                = DataBase + 0x3F0;  // 16 bytes (4 × int)
        public const int gt_gameTimeInMinutes       = DataBase + 0x400;  // dword
        public const int gt_endGameCounter          = DataBase + 0x404;  // dword
        public const int gt_timeDelta               = DataBase + 0x408;  // dword
        public const int gt_gameSeconds             = DataBase + 0x40C;  // dword
        public const int gt_secondsSwitchAccumulator = DataBase + 0x410; // dword
        public const int gt_showTime                = DataBase + 0x414;  // word

        // gameTime.cpp:139, 145, 158, 209 — extra-time / penalty-shootout state machine.
        public const int extraTimeState             = DataBase + 0x418;  // word — 0=disabled, 1=enabled, -1=running
        public const int penaltiesState             = DataBase + 0x41A;  // word — same convention
        public const int secondLeg                  = DataBase + 0x41C;  // word — non-zero = two-leg tie
        public const int playing2ndGame             = DataBase + 0x41E;  // word — non-zero = this is leg 2

        // gameTime.cpp:71, 167 — stateGoal cleared on period end + winningTeamPtr.
        public const int stateGoal                  = DataBase + 0x420;  // word
        public const int winningTeamPtr             = DataBase + 0x424;  // dword

        // ---- ball.cpp:checkIfBallOutOfPlay state (ball.cpp:3007-4020) -------
        // Whistle queued by checkIfBallOutOfPlay; cleared by goal/own-goal path.
        // g_memByte[523668].
        public const int playRefereeWhistle         = DataBase + 0x428;  // word

        // Last-touched / last-keeper bookkeeping (ball.cpp:3104, 3106).
        // g_memByte[523096] / [523087]. dwords (pointers to player sprites).
        public const int lastPlayerPlayed           = DataBase + 0x42C;  // dword — last outfielder to touch ball
        public const int lastKeeperPlayed           = DataBase + 0x430;  // dword — last keeper to touch ball

        // Stats snapshot taken on goal (ball.cpp:3143-3146).
        // g_memByte[336624] / [336626]. Distinct from statsTeam{1,2}GoalsCopy
        // (gameTime end-of-half snapshots) — Copy2 is "score at last goal".
        public const int statsTeam1GoalsCopy2       = DataBase + 0x434;  // word
        public const int statsTeam2GoalsCopy2       = DataBase + 0x436;  // word

        // Goal-camera + goal-event state (ball.cpp:3150-3151, 3223-3226).
        public const int goalCameraMode             = DataBase + 0x438;  // word — g_memByte[455982]
        public const int teamNumThatScored          = DataBase + 0x43A;  // word — g_memByte[456644] (1 or 2)
        public const int teamScoredDataPtr          = DataBase + 0x43C;  // dword — g_memByte[456648]
        public const int teamScoredGamePtr          = DataBase + 0x440;  // dword — g_memByte[456652]

        // goalCounter / pattern counter — countdown until kickoff. ball.cpp:3434-3435.
        public const int goalCounter                = DataBase + 0x444;  // word — g_memByte[455978]
        public const int patternsGoalCounter        = DataBase + 0x446;  // word — g_memByte[521016]

        // breakCameraMode written on every set-piece. ball.cpp:3439, 3624, 3646, 3655...
        // g_memByte[523120].
        public const int breakCameraMode            = DataBase + 0x448;  // word

        // goalOut flag (ball.cpp:3792). g_memByte[523102]. Distinguishes goal-out
        // from throw-in / corner branches at l_break_handled.
        public const int goalOut                    = DataBase + 0x44A;  // word

        // forceLeftTeam — bit forcing one team to always defend left goal.
        // Used by ball-in-pitch / corner / goal-out direction picks. g_memByte[455960].
        public const int forceLeftTeam              = DataBase + 0x44C;  // word

        // Camera velocity reset at l_break_handled (ball.cpp:4007-4008).
        // g_memByte[449796] / [449798].
        public const int cameraXVelocity            = DataBase + 0x44E;  // word
        public const int cameraYVelocity            = DataBase + 0x450;  // word

        // Stoppage timers — reset on every break (ball.cpp:4004-4005).
        // g_memByte[523112] / [523114].
        public const int stoppageTimerTotal         = DataBase + 0x452;  // word
        public const int stoppageTimerActive        = DataBase + 0x454;  // word

        // Camera direction + player turn flags written by set-piece branches.
        // g_memByte[523128] (word) / [523130] (byte).
        public const int cameraDirection            = DataBase + 0x456;  // word
        public const int playerTurnFlags            = DataBase + 0x458;  // byte

        // Write-only counter touched at l_break_handled (g_memByte[523104]).
        // Documented to keep mechanical-port parity — never read in ported scope.
        public const int gameNotInProgressCounterWriteOnly = DataBase + 0x45A;  // word

        // ---- Section 4 of updateBall (ball.cpp:737-2247) ------------------
        // currentGameTick — incremented each frame by SWOS main loop.
        // g_memByte[323904] in swos-port. Section 4 reads (& 0x1F) to seed
        // a small pseudo-random delta for post-collision (penalty / own-goal).
        public const int currentGameTick            = DataBase + 0x45C;  // word

        // lastPlayerTurnFlags — snapshot of playerTurnFlags taken on EVERY
        // stoppage (EndFirstHalf / EndOfGame / StopAllPlayers). Read in
        // updatePlayers.cpp:908 (`mov ax, lastPlayerTurnFlags+1` reads the
        // HIGH byte — "why +1? makes it 0 always" per the asm comment, so
        // the read always returns 0 in practice). Documented to preserve
        // mechanical-port parity with swos.asm:218352.
        // g_memByte[449804] in swos-port (cseg_84xxx vicinity).
        public const int lastPlayerTurnFlags        = DataBase + 0x45E;  // word

        // ballXQuadrantLimits[5] / ballYQuadrantLimits[7] — pitch divided
        // into a grid of quadrants for tactical AI positioning.
        // swos.asm:245746-245748:
        //   ballXQuadrantLimits dw 81, 183, 285, 387, 489
        //   ballYQuadrantLimits dw 129, 220, 312, 403, 495, 586, 678
        // Section 4 walks the +2 offset of each table (skipping the first
        // entry on the inbound compare; first entry is used as the lower
        // bound when computing the in-quadrant fractional offset).
        // g_memByte[523668] / [523678] in swos-port (10 + 14 bytes).
        public const int ballXQuadrantLimits        = DataBase + 0x460;  // 5 × word = 10 bytes
        public const int ballYQuadrantLimits        = DataBase + 0x46C;  // 7 × word = 14 bytes

        // ballQuadrantIndex / ball{X,Y}QuadrantDead / player{X,Y}QuadrantOffset.
        // Outputs of the Section 4 quadrant calculation. Read by
        // SetPlayerWithNoBallDestination (AI without-ball positioning).
        // g_memByte[523756/523758/523760/523762/523764] in swos-port.
        public const int ballQuadrantIndex          = DataBase + 0x47C;  // word
        public const int ballXQuadrantDead          = DataBase + 0x47E;  // word
        public const int ballYQuadrantDead          = DataBase + 0x480;  // word
        public const int playerXQuadrantOffset      = DataBase + 0x482;  // word
        public const int playerYQuadrantOffset      = DataBase + 0x484;  // word

        // ballShadowSprite base — separate sprite for ball shadow. Section 4
        // (l_update_ball_shadow) writes shadow X/Y/Z derived from ball pos.
        // swos-port g_memByte[329100]; we co-locate near BallSprite at
        // Memory.Base 0x4F800. Sprite struct layout matches BallSprite.
        public const int ballShadowSpriteBase       = 0x4F880;  // 64 bytes reserved

        // ---- gameLoop.cpp orchestrator state (GameLoop.cs port) ------------
        // gameLoop.cpp:86 — swos.playGame flag (g_memByte[252376]). 1 = match
        // running, 0 = exit outer loop. Word in original.
        public const int playGame                   = DataBase + 0x488;  // word

        // gameLoop.cpp:269 — swos.frameCount. Incremented every frame in
        // updateTimers(). Dword.
        public const int frameCounter               = DataBase + 0x48C;  // dword

        // gameControls.cpp:18 — m_teamSwitchCounter. Pre-incremented every
        // tick in selectTeamForUpdate(); bit 0 picks top vs bottom team for
        // that frame. Dword (int).
        public const int teamSwitchCounter          = DataBase + 0x490;  // dword

        // gameControls.cpp:48-56 — swos.fireBlocked. While set, gameLoop
        // skips player updates until both players have released fire. Word.
        public const int fireBlocked                = DataBase + 0x494;  // word

        // gameLoop.cpp:271 — swos.spaceReplayTimer. Decremented every frame
        // in updateTimers() while > 0. Word.
        public const int spaceReplayTimer           = DataBase + 0x496;  // word

        // gameLoop.cpp:438-1300 — updateGameTimersAndCameraBreakMode private
        // counters not already declared. (stoppageTimerTotal, stoppageTimerActive,
        // breakCameraMode and currentGameTick are higher up — reuse those.)
        public const int penaltiesTimer             = DataBase + 0x498;  // word — g_memByte[523156]
        public const int inGameCounter              = DataBase + 0x49A;  // word — g_memByte[523132]
        public const int stoppageEventTimer         = DataBase + 0x49C;  // word — g_memByte[523122]
        public const int gameNotInProgressCounter   = DataBase + 0x49E;  // word — readable mirror

        // gameLoop.cpp:115 — swos.lastGameTick (snapshot of currentGameTick on
        // last frame). Word in original.
        public const int lastGameTick               = DataBase + 0x4A0;  // word

        // gameLoop.cpp:1900-1910 — loadCrowdChantSampleFlag. Toggled by audio
        // system to defer chant loading to a frame boundary. Word.
        public const int loadCrowdChantSampleFlag   = DataBase + 0x4A2;  // word

        // ---- Goalkeeper dive / jump / deflect constants --------------------
        // updatePlayers.cpp:10844 (g_memByte[523998]) — speed used when ball is
        // close to keeper (ballDistance <= 128).
        public const int kGoalkeeperNearJumpSpeed   = DataBase + 0x4A4;  // word — 2048
        // updatePlayers.cpp:10850 (g_memByte[324036]) — default far-jump speed.
        public const int kGoalkeeperFarJumpSpeed    = DataBase + 0x4A6;  // word — 2048
        // updatePlayers.cpp:10860 (g_memByte[324038]) — slower far-jump speed.
        public const int kGoalkeeperFarJumpSlowerSpeed = DataBase + 0x4A8; // word — 1280
        // player.cpp:2470 (g_memByte[324040]) — deflect speeds (strong/medium/weak).
        public const int kGoalkeeperStrongDeflectBallSpeed = DataBase + 0x4AA; // word
        public const int kGoalkeeperMediumDeflectBallSpeed = DataBase + 0x4AC; // word
        public const int kGoalkeeperWeakDeflectBallSpeed   = DataBase + 0x4AE; // word
        // player.cpp:2496 (g_memByte[324046]) — dword (Q16.16).
        public const int kGoalkeeperDeflectDeltaZ   = DataBase + 0x4B0;  // dword

        // updatePlayers.cpp:10561 (g_memByte[325562]) — penalty save reach (far variant).
        public const int kKeeperPenaltySaveDistanceFar  = DataBase + 0x4B4; // word
        // updatePlayers.cpp:10586 (g_memByte[325564]) — penalty save reach (near variant).
        public const int kKeeperPenaltySaveDistanceNear = DataBase + 0x4B6; // word

        // updatePlayers.cpp:10658 / 10708 (g_memByte[524734]) — predicted ball
        // x-position used by defensive AI to decide where to dive towards.
        public const int ballDefensiveX             = DataBase + 0x4B8;  // word
        // updatePlayers.cpp:10912 (g_memByte[524744]) — predicted z used to pick
        // diving-low vs diving-high animation.
        public const int ballNotHighZ               = DataBase + 0x4BA;  // word

        // updatePlayers.cpp:10968 (g_memByte[523294]) — kDefaultDestinations:
        // 8 entries × 4 bytes (dx word + dy word), indexed by direction*4.
        public const int kDefaultDestinations       = DataBase + 0x4BC;  // 32 bytes

        // updatePlayers.cpp:10775 (g_memByte[337190]) — counter incremented each
        // time shouldGoalkeeperDive runs its frame-cost branch (statistical).
        public const int goalkeeperDiveDeadVar      = DataBase + 0x4DC;  // word

        // updatePlayers.cpp:10455 (g_memByte[457534]) — frames since last goal,
        // used to throttle "nobody's ball" commentary.
        public const int nobodysBallTimer           = DataBase + 0x4DE;  // word

        // ---- updatePlayers.cpp entry-point bookkeeping (per-tick) --------------
        // updatePlayers.cpp:51-53 — snapshot of last-tick last player + team.
        // Used by the epilogue to detect "goalie change between ticks" and
        // re-arm nobodysBallTimer. g_memByte[524726] / [524730].
        public const int prevLastPlayer             = DataBase + 0x4E0;  // dword
        public const int prevLastTeamPlayed         = DataBase + 0x4E4;  // dword

        // updatePlayers.cpp:205-336 — ball-location flag globals reset + set
        // each tick by the entry block. g_memByte[524758/524760/524762]. All
        // are writes-as-dword in some paths and reads-as-word in others; we
        // treat them as words (the asm only sets the low 16 bits).
        public const int ballInUpperPenaltyArea     = DataBase + 0x4E8;  // word
        public const int ballInLowerPenaltyArea    = DataBase + 0x4EA;  // word
        public const int ballInGoalkeeperArea       = DataBase + 0x4EC;  // word

        // updatePlayers.cpp:10452 (g_memByte[522756]) — captured at goalie-change
        // transition; "the outfielder that touched the ball just before the keeper
        // grabbed it" — used for assist/last-touch stats. Stub-only for now.
        public const int lastPlayerBeforeGoalkeeper = DataBase + 0x4F0;  // dword

        // ---- Camera state (camera.cpp) -------------------------------------
        // camera.cpp:41-42 — m_cameraX / m_cameraY (Q16.16). Written through
        // SetCameraX/Y, read via GetCameraX/Y. Godot renders the actual camera;
        // this VM value is consumed by gameplay code (referee, AI) that clamps
        // to camera position.
        public const int cameraX                    = DataBase + 0x4F4;  // dword Q16.16
        public const int cameraY                    = DataBase + 0x4F8;  // dword Q16.16
        // camera.cpp:44 — m_leavingBenchMode flag.
        public const int leavingBenchMode           = DataBase + 0x4FC;  // word

        // ---- Settings / global flags --------------------------------------
        // swos.g_trainingGame (camera.cpp:126, stats.cpp:195).
        public const int g_trainingGame             = DataBase + 0x500;  // word
        // swos.showFansCounter (camera.cpp:99).
        public const int showFansCounter            = DataBase + 0x502;  // word
        // swos.g_waitForPlayerToGoInTimer (camera.cpp:108).
        public const int g_waitForPlayerToGoInTimer = DataBase + 0x504;  // word

        // ---- Stats / result state (stats.cpp, result.cpp) -----------------
        // swos.statsTimer (stats.cpp:41, 50, 63, 71). Signed — negative=hide-now.
        public const int statsTimer                 = DataBase + 0x508;  // signed word
        // swos.resultTimer (result.cpp:122-135). Signed dword; sentinel values
        // 30 000 / 31 000 / 32 000 enqueue specific durations.
        public const int resultTimer                = DataBase + 0x50C;  // signed dword
        // swos.strikeDestX (stats.cpp:99 — shot's predicted X intercept).
        // Canonical declaration at DataBase + 0x508 in the upper block; reuse it.
        // result.cpp:268 — dontShowScorers flag (penalty shootout etc.).
        public const int dontShowScorers            = DataBase + 0x512;  // word
        // result.cpp:160 — topTeamPtr / bottomTeamPtr (TeamGame pointers; may
        // differ from topTeamInGame when scorer-side mapping flips).
        public const int topTeamPtr                 = DataBase + 0x514;  // dword
        public const int bottomTeamPtr              = DataBase + 0x518;  // dword

        // ---- Per-team stats backing (TeamStatsData — 14 bytes each) -------
        // swos.h:212 — TeamStatsData has 7 words:
        //   +0  ballPossession    +6  bookings        +12 onTarget
        //   +2  cornersWon        +8  sendingsOff
        //   +4  foulsConceded     +10 goalAttempts
        // Allocated 32 bytes each for padding. TeamData.teamStatsPtr (+14)
        // initialised to point here.
        public const int topTeamStatsData           = DataBase + 0x520;  // 32 bytes
        public const int bottomTeamStatsData        = DataBase + 0x540;  // 32 bytes

        // ---- Result-module private state (result.cpp:81-93) ----------------
        // m_showResult flag (result.cpp:92).
        public const int res_showResult             = DataBase + 0x560;  // word
        // m_team1Name / m_team2Name pointers + m_team1NameLength (result.cpp:88-90).
        // Names are `const char *` in the C++; pure UI — pointer carried opaquely.
        public const int res_team1Name              = DataBase + 0x564;  // dword
        public const int res_team2Name              = DataBase + 0x568;  // dword
        public const int res_team1NameLength        = DataBase + 0x56C;  // word

        // ---- Stats-module private state (stats.cpp:18-21) ------------------
        // m_showStats / m_showingUserRequestedStats / m_isGoalAttempt.
        public const int st_showStats               = DataBase + 0x570;  // word
        public const int st_showingUserRequestedStats = DataBase + 0x572; // word
        public const int st_isGoalAttempt           = DataBase + 0x574;  // word

        // ---- Fouls / cards bookkeeping (updatePlayers.cpp playerTacklingTestFoul,
        // ---- tryBookingThePlayer, trySendingOffThePlayer) --------------------
        // g_memByte[523636] — `cardsDisallowed`. When non-zero, all card paths
        // in playerTacklingTestFoul skip and the foul becomes pure (no booking).
        public const int cardsDisallowed            = DataBase + 0x580;  // word
        // g_memByte[523650] — `playerCardChance`. Set by game.cpp:1378 based on
        // game-length lookup table. Compared each tackle against (currentGameTick
        // & 30) >> 1 to gate booking attempts in the penalty area.
        public const int playerCardChance           = DataBase + 0x582;  // word
        // g_memByte[449320] — `plg_D3_param`. Player game flags (set by game.cpp:1489).
        // Non-zero selects the "user-controlled team" branch in booking logic.
        public const int plg_D3_param               = DataBase + 0x584;  // word
        // g_memByte[449322] / [449324] — per-team remaining injury substitutions
        // counters (game.cpp:170 initialised to 4). Decremented on red card or
        // injury; when 0 the team can't receive cards via the booking path.
        public const int team1NumAllowedInjuries    = DataBase + 0x586;  // word
        public const int team2NumAllowedInjuries    = DataBase + 0x588;  // word
        // g_memByte[337208] — `dseg_114EC2`, written each tackle with the address
        // of the nearest non-controlled, non-sent-off teammate (closest substitute
        // candidate). Reused later by referee/substitution logic.
        public const int lastTackleNearestTeammate  = DataBase + 0x58C;  // dword
        // g_memByte[332510] — `inGameTeamPlayerOffsets` (swos.asm:208198,
        // `dw 0,61,122,...` = index × 61). 11 words used, indexed by
        // (playerOrdinal - 1) × 2, giving the byte offset within InGameTeam to
        // that player's record. In the original the caller does
        // `inGameTeamPtr(=TeamGame start) + offset`; OpenSWOS's inGameTeamPtr is
        // players[0], so PlayerTackle's four header-relative sites subtract the
        // 42-byte TeamGame header (PlayerTackle.kTeamGameHeaderSize).
        public const int inGameTeamPlayerOffsets    = DataBase + 0x590;  // 11 × word = 22 bytes
        // g_memByte[524232] / [524237] — `dseg_17E3EE` / `dseg_17E3F3`. Two 5-byte
        // lookup tables for new-card values, indexed by (currentCards XOR 1) + 3
        // (red) or by current_cards+1 (yellow). Selection between them is gated
        // on PlayerGameHeader.previousCards (high bit set → use dseg_17E3F3).
        public const int dseg_17E3EE                = DataBase + 0x5A8;  // 5 bytes
        public const int dseg_17E3F3                = DataBase + 0x5AD;  // 5 bytes

        // ---- Pre-match menu selections (pitch.cpp:244-254, 269-277) --------
        // swos.gamePitchTypeOrSeason — when non-zero, pitch.cpp:245 picks the
        // condition from the seasonal table indexed by gameSeason; when zero,
        // falls back to the fixed-probability path (or honours gamePitchType).
        // OpenSWOS defaults to 0 (no seasonal selector wired yet).
        public const int gamePitchTypeOrSeason      = DataBase + 0x5B4;  // word
        // swos.gameSeason — 0..11 month index. Used only when gamePitchTypeOrSeason != 0.
        public const int gameSeason                 = DataBase + 0x5B6;  // word
        // swos.gamePitchType — manual pitch-condition override. -1 = random
        // (forces the fixed-probability fall-back); 0..6 = honour verbatim.
        // OpenSWOS defaults to -1 so SetPitchType always rolls the table.
        public const int gamePitchType              = DataBase + 0x5B8;  // signed word
        // swos.plg_D0_param — friendly-vs-career flag (pitch.cpp:269). Non-zero
        // → friendly path (RNG variant); zero → career path (team-hash variant).
        public const int plg_D0_param               = DataBase + 0x5BA;  // word

        // ---- AI helper globals (updatePlayers.cpp AI_Kick /
        // ---- AI_SetDirectionTowardOpponentsGoal / AI_DecideWhetherToTriggerFire) ----
        // g_memByte[526994] — `AI_counter` (word). Set to 15 by
        // AI_DecideWhetherToTriggerFire on a fire-trigger; decremented elsewhere.
        // Read as the "AI active" gate by AI_SetDirectionTowardOpponentsGoal.
        public const int AI_counter                 = DataBase + 0x5C0;  // word
        // g_memByte[526996] — `AI_attackHalf` (word). 1 = top half attacking,
        // 2 = bottom half attacking. Selects which goal mouth AI aims at.
        public const int AI_attackHalf              = DataBase + 0x5C2;  // word
        // g_memByte[526998] — `AI_counterWriteOnly` (word). Stamped with the
        // chosen direction on a successful trigger; never read in the ported scope.
        public const int AI_counterWriteOnly        = DataBase + 0x5C4;  // word
        // g_memByte[449467] — `deadVarAlways0` (word). Initialised to 0 and never
        // assigned a non-zero value anywhere in swos-port — the AI_Kick and
        // related early-out tests therefore always short-circuit through the
        // "carry on" path. Preserved as a real backing word so the mechanical
        // port stays byte-for-byte faithful.
        public const int deadVarAlways0             = DataBase + 0x5C6;  // word
        // g_memByte[449493] — `dseg_1309C1` (dword). Companion to deadVarAlways0:
        // a pointer-typed dead variable that stays 0 and is only consulted in the
        // dead branch protected by deadVarAlways0. Compared to team / opponent
        // base addresses; the equality is never satisfied in practice.
        public const int dseg_1309C1                = DataBase + 0x5C8;  // dword

        // ---- updatePlayers.cpp l_its_controlled_player branch private vars ----
        // g_memByte[455928] — `dseg_132804` (word). Counter incremented inside
        // the l_player_expecting_pass branch when cameraDirection >= 8 forces
        // a refresh from sprite.direction (the joystick / camera disagreement
        // path). Dead-statistical (never read in the ported scope, retained
        // for byte-for-byte parity with swos-port).
        // updatePlayers.cpp:7992 (l_pass_success cameraDirection override).
        public const int dseg_132804                = DataBase + 0x5CE;  // word
        // g_memByte[455930] — `dseg_132806` (word). Counter incremented every
        // time the controlling-player branch overrides the joystick direction
        // (because playerTurnFlags rejected it). Dead-statistical (never read in
        // the ported scope, retained for byte-for-byte parity).
        // updatePlayers.cpp:4519 (l_its_controlled_player turn-flag override).
        public const int dseg_132806                = DataBase + 0x5D0;  // word
        // g_memByte[455934] — `deadThrowInDirectionVar` (word). Same idea:
        // incremented when the "find acceptable turn flags" loop has to
        // synthesise a fresh cameraDirection because none of the joystick
        // directions matched playerTurnFlags. Dead-statistical.
        // updatePlayers.cpp:4589.
        public const int deadThrowInDirectionVar    = DataBase + 0x5D2;  // word
        // g_memByte[455940] — `disallowedTurnFlagsCounter` (word). Counter
        // incremented inside the THROW-IN turn-flag override block (not the
        // controlled-player block above). Kept here for proximity / parity.
        // updatePlayers.cpp:5830.
        public const int disallowedTurnFlagsCounter = DataBase + 0x5D4;  // word

        // g_memByte[449744] — `timeVar` (signed word). Companion to resultTimer:
        // when fire is pressed while statsTimer is active, both timeVar and
        // resultTimer get their signs negated (snapping the result panel away).
        // updatePlayers.cpp:4668.
        public const int timeVar                    = DataBase + 0x5D6;  // signed word

        // g_memByte[523085] — `playerHadBall` (word). Set to 1 by the
        // controlled-player branch when the OPPONENT just-released the ball
        // (so calculateIfPlayerWinsBall can tell whether the touch is a
        // contested 50/50 or a free ball).
        // updatePlayers.cpp:5044 / cleared at 5032.
        public const int playerHadBall              = DataBase + 0x5D8;  // word

        // ---- playerTackled injury-roll tables (swos.asm:245826-245837) ----
        // 5 small static tables consulted by the injury logic in
        // updatePlayers.cpp:14411-14823. Values are byte-literal copies of the
        // m68k DATA declarations in swos.asm at offset PlayerTackled+B8/E3/10E/139.
        //
        //   kInjuryLevels (db, 7 bytes) — RNG threshold table for non-injured
        //     player; the rolling D1 bucket index (0..7) picks an injury severity
        //     and the byte at that index becomes the injuriesBitfield write.
        //     swos.asm:245826.
        //   kInjuryLevelAlreadyInjured (db, 7 bytes) — same shape but for an
        //     already-injured player. swos.asm:245827.
        //   dseg_17E2EC (dw, 8 words = 16 bytes) — Sprite.injuryLevel destination
        //     value, indexed by the high 3 bits of injuriesBitfield. swos.asm:245829.
        //   kTackleInjuryProbability (db, 4 bytes) — non-injured probability
        //     gate indexed by gameLengthInGame (0..3). swos.asm:245831.
        //   kTackleInjuryProbabilityAlreadyInjured (db, 4 bytes) — already-injured
        //     probability gate indexed by gameLengthInGame. swos.asm:245837.
        //
        // Initialised by `InitInjuryTables` in Memory.Init (see ~L1620). Without
        // these, the playerTackled injury branch can't run mechanically.
        public const int kInjuryLevels                          = DataBase + 0x5DA;  // 7 bytes
        public const int kInjuryLevelAlreadyInjured             = DataBase + 0x5E1;  // 7 bytes
        public const int dseg_17E2EC                            = DataBase + 0x5E8;  // 8 × word = 16 bytes
        public const int kTackleInjuryProbability               = DataBase + 0x5F8;  // 4 bytes
        public const int kTackleInjuryProbabilityAlreadyInjured = DataBase + 0x5FC;  // 4 bytes

        // ---- Input / team-controls private state (gameControls.cpp:15-26) ----
        // The original C++ keeps these as module-static ints/enums; we mirror
        // them in VM memory so the InputControls port is self-contained and a
        // future netplay desync diff can inspect them. Layout:
        //   ic_pl{1,2}FireCounter (dword each)
        //     gameControls.cpp:15-16. Signed int — starts at -1 while held,
        //     negated to positive on release. Reset to 0 by post-update path.
        //   ic_pl{1,2}LastFired (byte each)
        //     gameControls.cpp:19-20. Tracks whether the last frame saw fire
        //     pressed (so we can detect "fire started this frame").
        //   ic_oldPl{1,2}Events (dword each)
        //     gameControls.cpp:21-22. Previous-frame events bitmask used by
        //     the overlap filter (which of up/down arrived first).
        //   ic_pl{1,2}LastVertical / LastHorizontal (dword each)
        //     gameControls.cpp:23-26. Sticky "force this axis" override while
        //     both opposing keys remain pressed.
        //   ic_pl{1,2}Events (dword each)
        //     CURRENT-FRAME raw events bitmask. Written by the Godot input
        //     layer via InputControls.SetJoystickState; read by getPlayerEvents.
        //     Replaces the original `keyboard1Events() / pl1JoypadEvents()`
        //     dispatch — Main.cs decides which physical device is bound and
        //     pushes the resulting GameControlEvents bitmask in here.
        //   ic_pl{1,2}Fire (word each)
        //     Mirror of swos.pl{1,2}Fire (gameControls.cpp:330-332). -1 when
        //     fire pressed, 0 otherwise. Set by updatePlayerFire each frame.
        public const int ic_pl1FireCounter          = DataBase + 0x700;  // dword (signed)
        public const int ic_pl2FireCounter          = DataBase + 0x704;  // dword (signed)
        public const int ic_pl1LastFired            = DataBase + 0x708;  // byte
        public const int ic_pl2LastFired            = DataBase + 0x709;  // byte
        public const int ic_oldPl1Events            = DataBase + 0x70C;  // dword (GameControlEvents)
        public const int ic_oldPl2Events            = DataBase + 0x710;  // dword
        public const int ic_pl1LastVertical         = DataBase + 0x714;  // dword
        public const int ic_pl1LastHorizontal       = DataBase + 0x718;  // dword
        public const int ic_pl2LastVertical         = DataBase + 0x71C;  // dword
        public const int ic_pl2LastHorizontal       = DataBase + 0x720;  // dword
        public const int ic_pl1Events               = DataBase + 0x724;  // dword — raw input from Godot
        public const int ic_pl2Events               = DataBase + 0x728;  // dword
        public const int ic_pl1Fire                 = DataBase + 0x72C;  // word (-1 / 0)
        public const int ic_pl2Fire                 = DataBase + 0x72E;  // word

        // ---- Set-piece state (updatePlayers.cpp throw-in handler) ------------
        // g_memByte[523140] — `throwInPassOrKick`. 0 = throw will be a kick on
        // release; 1 = throw will be a pass. Selected by quickFire vs normalFire
        // on the thrower's input (updatePlayers.cpp:6201 / 6238).
        public const int throwInPassOrKick          = DataBase + 0x740;  // word

        // Legacy aliases — pre-AnimationTablesData these were 4-byte pointer
        // slots; now they alias the full 130-byte PlayerAnimationTable structs
        // at k*AnimTableAddr. Kept so existing SetPieces.cs / Bench code reads
        // cleanly without churn.
        public const int aboutToThrowInAnimTable    = kAboutToThrowInAnimTableAddr;
        public const int throwInPassAnimTable       = kThrowInPassAnimTableAddr;
        public const int throwInKickAnimTable       = kThrowInKickAnimTableAddr;

        // updatePlayers.cpp:16616 — `AI_throwInDirections`. Byte table indexed
        // by (gameState - ST_THROW_IN_FORWARD_RIGHT) giving the throw direction
        // for an AI-controlled throw-in. Original at g_memByte[527000].
        // Stored here as a 6-byte table (one entry per throw-in sub-state).
        public const int AI_throwInDirections       = DataBase + 0x750;  // 6 bytes

        // updatePlayers.cpp:5912 — `substitutedPlSprite`. Absolute address of
        // the player sprite that is currently being substituted off the pitch.
        // Used by the throw-in handler to abort when the thrower is being
        // substituted (compare against A1 = thrower sprite). Original at
        // g_memByte[486160] as a dword pointer.
        public const int substitutedPlSprite        = DataBase + 0x756;  // dword — pointer

        // Legacy alias — same as kPlayerStandingAnimTableAddr.
        public const int playerNormalStandingAnimTable = kPlayerStandingAnimTableAddr;

        // ---- PlayerHeader port (updatePlayers.cpp:15253/15380/15803) ---------
        // g_memByte[524724] — kStaticHeaderPlayerSpeed (swos.asm:245903 `dw 256`).
        // Read by attemptStaticHeader to fill Sprite.speed when launching a
        // standing header. Same value in both PC and Amiga modes.
        public const int kStaticHeaderPlayerSpeed   = DataBase + 0x760;  // word — 256

        // g_memByte[523694] — playerXQuadrantsCoordinates (swos.asm:245750).
        // 15 × word = 30 bytes. X-axis grid centroids used by
        // setPlayerWithNoBallDestination to map a quadrant nibble to a pitch
        // X coordinate. Values:
        //   {98, 132, 166, 200, 234, 268, 302, 336, 370, 404, 438, 472, 506, 540, 574}.
        public const int playerXQuadrantsCoordinates = DataBase + 0x764; // 30 bytes

        // g_memByte[523724] — playerYQuadrantCoordinates (swos.asm:245753).
        // 16 × word = 32 bytes. Y-axis grid centroids. Values:
        //   {149, 189, 229, 269, 309, 349, 389, 429,
        //    469, 509, 549, 589, 629, 669, 709, 749}.
        public const int playerYQuadrantCoordinates = DataBase + 0x784; // 32 bytes

        // m_playerDownHeadingInterval — updatePlayers.cpp:12 module-static int
        // initialised to 55, written by setPlayerDownHeadingInterval
        // (line 10475). Read by playerAttemptingJumpHeader as the
        // playerDownTimer seed when launching a jump header. The asm writes
        // it byte-wide via writeMemory(esi+13, 1, ...); we allocate a word.
        public const int m_playerDownHeadingInterval = DataBase + 0x7A4; // word

        // m_playerDownTacklingInterval — updatePlayers.cpp:11 module-static
        // int initialised to 55. Read by playerBeginTackling
        // (updatePlayers.cpp:14852) as the playerDownTimer seed when the
        // player launches a sliding tackle. Written byte-wide into
        // Sprite.playerDownTimer; we allocate a word and read low byte.
        public const int m_playerDownTacklingInterval = DataBase + 0x7A6; // word

        // g_memByte[372768] — g_tacticsTable (swos.asm:209342). 19 × dword =
        // 76 bytes. Each entry is the absolute address of a TeamTactics
        // struct (370 bytes each). Indexed by TeamGeneralInfo.tactics
        // (× 4-byte offset). setPlayerWithNoBallDestination derefs this to
        // reach Tactics.ballOutOfPlayTactics (+369) and Tactics.playerPos
        // (+9, PlayerPositions[10] × 35 = 350 bytes).
        public const int g_tacticsTable             = DataBase + 0x7A8; // 76 bytes

        // 19 × TeamTactics(370 bytes), zero-filled. Real tactics data isn't
        // loaded yet — the port references this region symbolically. Reads
        // resolve to zero (interpreted as ballOutOfPlayTactics index 0 and
        // playerPos all-zero), so set-piece quadrant lookups currently
        // return the top-left quadrant centroid (98, 149). When tactics.cpp
        // gets a loader, this block becomes live.
        public const int teamTacticsPool            = DataBase + 0x800; // 19 × 370 = 7030 bytes
        // ^ Reserves DataBase+0x800 .. DataBase+0x2376.

        // updatePlayers.cpp:2124 (g_memByte[324070]) — kShotAtGoalMinumumSpeed (sic).
        // Word constant — 512 in PC mode. Read by the `cseg_7F7BC` chain inside
        // `l_goalie_not_catching_the_ball`: when ball.speed >= this, the keeper
        // skips the early shot-at-goal trip-wire and falls through to the
        // normal ball-trajectory dive logic. swos.asm:202413 `dw 512`.
        public const int kShotAtGoalMinumumSpeed    = DataBase + 0x2380;  // word — 512

        // updatePlayers.cpp:2334 (g_memByte[526888]) — goalScoredChances table.
        // 15 bytes of byte-typed thresholds indexed by `(finishing - goalieSkill + 7)`
        // (clamped 0..14). RNG sample `(currentGameTick >> 1) & 15` < threshold →
        // goal scored, else keeper save. swos.asm:246161 — `db 1..15`.
        public const int goalScoredChances          = DataBase + 0x2382;  // 15 bytes

        // updatePlayers.cpp:2547 (g_memByte[324028]) — dseg_1105EF. Keeper
        // walk speed applied by cseg_7FBEF when the keeper's OWN team played
        // the ball last deliberately (playerHadBall == 0) — the "don't rush a
        // back-pass" speed. swos.asm:202375 `dseg_1105EF dw 512`.
        public const int dseg_1105EF                = DataBase + 0x2392;  // word — 512

        // updatePlayers.cpp:4190 (g_memByte[325544]) — dseg_110BDB. Eight
        // words of SAR shift counts used by the dive-parry destY damping
        // (cseg_8095F), indexed by (currentGameTick & 0x0E) as a byte offset.
        // swos.asm:203728 `db 1,0, 4,0, 3,0, 3,0, 3,0, 2,0, 2,0, 2,0`.
        public const int dseg_110BDB                = DataBase + 0x2394;  // 8 × word

        // updatePlayers.cpp:3877 (g_memByte[526903]) — dseg_17EECC. The
        // PENALTY variant of the shot-chance row consumed by the diving-keeper
        // outcome verdict at :3953-4024 (word reads at byte offsets +42/+44).
        // swos.asm:246164-246167 — 60 bytes verbatim.
        public const int dseg_17EECC                = DataBase + 0x23A4;  // 60 bytes

        // ---- AI_SetControlsDirection globals (updatePlayers.cpp:15980+) ------
        // Source line numbers in this block refer to updatePlayers.cpp.
        // AI_counter / AI_attackHalf / AI_counterWriteOnly are declared above
        // (DataBase+0x5C0..0x5C4). deadVarAlways0 / dseg_1309C1 likewise above.
        // AI_throwInDirections already declared at DataBase+0x750.
        //
        // L16009 — `AI_resumePlayTimer` (g_memByte[526966]). Cooldown ticker
        // bumped to 15 after the AI commits to a kick / pass; decremented every
        // entry. Other paths early-return while non-zero.
        public const int AI_resumePlayTimer         = DataBase + 0x2400;  // word
        // L16031-16033 — `AI_rand` (g_memByte[526968]). Refreshed every entry
        // with Rand(), then sampled by goto-branches for stochastic decisions.
        public const int AI_rand                    = DataBase + 0x2402;  // word
        // L17097 — `AI_turnDirection` (g_memByte[526964]). Signed sentinel: -1 /
        // 0 / +1. Carries the previous-frame's chosen turn (left/none/right)
        // across frames so the AI doesn't oscillate.
        public const int AI_turnDirection           = DataBase + 0x2404;  // signed word
        // L18142 — `AI_maxStoppageTime` (g_memByte[455980]). Tracks the largest
        // stoppage timer seen so far during a play.
        public const int AI_maxStoppageTime         = DataBase + 0x2406;  // word

        // L19054 — `AI_randomRotateTable @ g_memByte[526972]`. 2 × word.
        public const int AI_randomRotateTable       = DataBase + 0x2410;  // 4 bytes
        // L19261 — `AI_leftSpinTable @ g_memByte[526976]`. 3 × word indexed by
        // AI_afterTouchStrength * 2 (weak/medium/strong left-spin deltas).
        public const int AI_leftSpinTable           = DataBase + 0x2414;  // 6 bytes
        // L19277 — `AI_rotateRightTable @ g_memByte[526982]`. 3 × word.
        public const int AI_rotateRightTable        = DataBase + 0x241C;  // 6 bytes
        // L19241 — `AI_longKickTable @ g_memByte[526988]`. 3 × word.
        public const int AI_longKickTable           = DataBase + 0x2424;  // 6 bytes

        // L15909/16261/etc. — `m_clearResultInterval` / `m_clearResultHalftimeInterval`.
        // Game-state intervals (in stoppage-tick units) used by the AI to time
        // the post-goal / post-halftime "press space" tap.
        public const int m_clearResultInterval          = DataBase + 0x242C;  // word
        public const int m_clearResultHalftimeInterval  = DataBase + 0x242E;  // word

        // L17889 — `frameCount` (g_memByte[323898]). Distinct from currentGameTick;
        // increments only when a frame is drawn (so it skips frames during pause).
        public const int frameCount                 = DataBase + 0x2430;  // word

        // L16217/16225 — team1Computer / team2Computer (g_memByte[449302/304]).
        // Non-zero = team is AI-controlled. Read at end-of-match to decide
        // whether to auto-press fire to dismiss the result screen.
        public const int team1Computer              = DataBase + 0x2434;  // word
        public const int team2Computer              = DataBase + 0x2436;  // word

        // L15994 — `g_inSubstitutesMenu` (g_memByte[486142]). Non-zero = the
        // substitutes menu is up; AI must NOT issue controls this tick.
        public const int g_inSubstitutesMenu        = DataBase + 0x2438;  // word

        // L16312 — `AI_resultTimer` (g_memByte[449742]). Distinct from the
        // result.cpp `resultTimer` (declared earlier at 0x50C). Used by the AI
        // `l_check_if_result_shown` path to gate fire-trigger before a result.
        public const int AI_resultTimer             = DataBase + 0x243A;  // word

        // gameLoop.cpp:52 — `static int m_initalKickInterval = 825;`. Used at
        // gameLoop.cpp:551-558 to compare `stoppageTimerActive` against — if
        // crossed while gameStatePl == kWaitingOnPlayer (102) AND
        // lastTeamPlayedBeforeBreak.playerNumber == 0 (no human waiting), the
        // safety net `prepareForInitialKick()` fires. 825 ticks @ 70 Hz ≈ 11.8 s.
        // Static C int in the original (not in g_memByte); mirrored here for
        // parity so a future amigaMode/timer-init can `WriteWord` it.
        public const int m_initalKickInterval       = DataBase + 0x243C;  // word — 825

        // gameLoop.cpp:562-571 — `initialKickWriteOnlyTicks` (g_memByte[455938]).
        // Bumped by 1 each time the stoppage-timer safety net auto-triggers
        // `prepareForInitialKick()`. Documented as write-only in the same
        // tradition as gameNotInProgressCounterWriteOnly — mechanical-port
        // parity only; no reads in the ported scope.
        public const int initialKickWriteOnlyTicks  = DataBase + 0x243E;  // word

        // ---- Match-half / kick-off direction state (swos.asm:112090+) ------
        // halfNumber, teamPlayingUp, teamStarting — read by EndFirstHalf,
        // StartFirstExtraTime, EndFirstExtraTime, StartPenalties (swos.asm:
        // 103926, 103969, 103970, 103972, 100517, 100522). All three are
        // single-word fields in the SWOS data section.
        // halfNumber: 1 = first half, 2 = second half (incl. ET first half),
        // 3 = ET second half (`mov halfNumber, 2` at swos.asm:103969 etc.).
        // teamPlayingUp / teamStarting: 1 or 2 (which team plays toward top
        // goal / which team kicks off this half). Negated + +3 each half
        // (swos.asm:103970-103973).
        public const int halfNumber                 = DataBase + 0x2440;  // word
        public const int teamPlayingUp              = DataBase + 0x2442;  // word
        public const int teamStarting               = DataBase + 0x2444;  // word

        // ---- Penalty-shootout bookkeeping (swos.asm:100501-100527) ---------
        // savedTeam1Goals / savedTeam2Goals: stats counters snapshotted at
        // the start of penalties so they can be restored after the shootout
        // for display purposes (StartPenalties resets stats to 0 for the
        // shootout itself). swos.asm:100502-100505.
        public const int savedTeam1Goals            = DataBase + 0x2446;  // word
        public const int savedTeam2Goals            = DataBase + 0x2448;  // word

        // Per-team penalty shooter cursor — index into team players[] of the
        // next shooter (initialised to 11, decremented per shot).
        // swos.asm:100524-100525.
        public const int team1PenaltyShooterIndex   = DataBase + 0x244A;  // word
        public const int team2PenaltyShooterIndex   = DataBase + 0x244C;  // word

        // Per-team penalty attempt counter (number of pens taken so far).
        // swos.asm:100526-100527.
        public const int team1PenaltyAttempts       = DataBase + 0x244E;  // word
        public const int team2PenaltyAttempts       = DataBase + 0x2450;  // word

        // swos.asm:245614 `injuriesForever dw 0`. Tester/debug toggle. When
        // non-zero, l_player_injured early-exits without decrementing the
        // playerDownTimer — the rolling-injured player stays on the ground
        // indefinitely. Defaults to 0 (normal play). Read at
        // updatePlayers.cpp:7433 (TickInjuredRollingPlayer) and others
        // (swos.asm:91928, 101784, 112665, 116173).
        // Source: external/swos-port/swos/swos.asm:245614.
        public const int injuriesForever            = DataBase + 0x2452;  // word

        // ---- spinningLogo.cpp private state --------------------------------
        // spinningLogo.cpp:12-15 — file-scope statics.
        // m_enabled: enabled flag (set by enableSpinningLogo / GetSpinningLogoEnabled).
        // m_frameIndex: 6-bit counter advanced every 2 game ticks while spinning.
        // m_pictureIndex: derived sprite index (kBigSSpriteStart + m_frameIndex / 2).
        // Source: external/swos-port/src/game/spinningLogo.cpp:12-15.
        public const int sl_enabled                 = DataBase + 0x2454;  // word
        public const int sl_frameIndex              = DataBase + 0x2456;  // word
        public const int sl_pictureIndex            = DataBase + 0x2458;  // word

        // ---- Bench / substitution gameplay-side state (bench.cpp:19-29) -----
        // Source: external/swos-port/src/game/bench/bench.cpp:19-29
        //         external/swos-port/src/game/bench/updateBench.cpp:619, 644-645
        //         external/swos-port/src/game/updatePlayers/updatePlayers.cpp:9134-9136
        //
        // bench.cpp:19-20 — m_benchY / m_opponentBenchY (file-static int). Y of
        // each team's bench on the pitch (top or bottom band). Swapped at half
        // time via swapBenchWithOpponent(). Kept in VM memory so a future
        // renderer / camera port can read where to centre on.
        public const int m_benchY                   = DataBase + 0x245A;  // word
        public const int m_opponentBenchY           = DataBase + 0x245C;  // word

        // bench.cpp:29 — `swos.g_cameraLeavingSubsTimer`. Original at
        // g_memByte[486144] (gameLoop.cpp:1097, updateBench.cpp:619). Counts
        // down while the camera is panning away from the bench after a sub.
        public const int g_cameraLeavingSubsTimer   = DataBase + 0x245E;  // word

        // ---- gameSprites.cpp — corner flag sprite state --------------------
        // gameSprites.cpp:14-19, 252-279. The four corner flags are private Sprite
        // objects (not part of the 22-player pool). Per-tick updateCornerFlags
        // sets their (x, y, imageIndex, visible). We back the gameplay-relevant
        // fields (image index per flag) here so a renderer can consult them.
        // Layout: 4 flags × { imageIndex (word), x (word), y (word) } = 24 bytes.
        // gameSprites.cpp:259-262 — positions are file-scope constants; we still
        // store them for renderer convenience.
        public const int cornerFlags                = DataBase + 0x2460;  // 24 bytes (4 × 6)

        // ---- Bench / substitution: new-player-coming-in destination --------
        // bench.cpp:116-117 — `swos.plComingX` / `swos.plComingY`. Original at
        // g_memByte[486148 / 486150] (read by updatePlayers.cpp:9134-9136 to
        // set the substitute walker's destination). World-pixel coordinates
        // where a new sub enters the pitch. Seeded by initBench() at
        // kPlayerGoingInX=26 / kPlayerGoingInY=kPitchCenterY=449.
        public const int plComingX                  = DataBase + 0x2478;  // word
        public const int plComingY                  = DataBase + 0x247A;  // word

        // game.cpp:1040 — `penaltyShooterSprite` (dword at g_memByte[523162]).
        // Populated by NextPenalty at the start of each penalty kick with the
        // pointer to the currently shooting player sprite (read from the
        // team's spritesTable at index team{1,2}PenaltyShooterIndex).
        public const int penaltyShooterSprite       = DataBase + 0x247C;  // dword

        // ---- gameSprites.cpp — controlled-player number sprite state -------
        // gameSprites.cpp:281-312 — m_team1/2CurPlayerNumSprite. Two sprites
        // (one per team) that blink the controlled player's shirt number above
        // their head. We back imageIndex + (x, y, z) for the renderer.
        // Layout: 2 sprites × { imageIndex (word), x (word), y (word), z (word) } = 16 bytes.
        public const int curPlayerNumSprites        = DataBase + 0x2480;  // 16 bytes (2 × 8)

        // ---- playerNameDisplay.cpp private state ---------------------------
        // playerNameDisplay.cpp:11-12 (m_topTeam, m_playerOrdinal) and the
        // static s_nobodysBallLastFrame inside updateCurrentPlayerName (line 22).
        // pnd_visible is the swos.currentPlayerNameSprite "visible" flag — pure
        // render state but tracked here for parity. pnd_playerOrdinal == -1
        // means hidden; positive is the index into the team's players[16].
        // Source: external/swos-port/src/game/playerNameDisplay.cpp:11-22.
        public const int pnd_topTeam                = DataBase + 0x2490;  // word (0 = bottom)
        public const int pnd_playerOrdinal          = DataBase + 0x2492;  // signed word (-1 = hidden)
        public const int pnd_visible                = DataBase + 0x2494;  // word
        public const int pnd_nobodysBallLastFrame   = DataBase + 0x2496;  // word

        // ---- player.cpp constants & tables (PlayerActions.cs port) --------
        // Mechanical port of remaining player.cpp functions added these. Bases
        // from swos.asm; values stay zero until populated by a future
        // amigaMode.cpp / swos.asm data-init port. Tables that read as zero
        // make the gameplay paths that consume them behave like "minimum skill"
        // until populated — the asm-translated control flow stays exercisable.

        // player.cpp:100 (g_memByte[524200]) — kPlayerWithBallOffsets.
        // 8 directions × (dx word + dy word) = 32 bytes. Dribble offset from
        // player to ball (different from kBallPlOffsets used by keeper).
        public const int kPlayerWithBallOffsets     = DataBase + 0x2500;  // 32 bytes

        // player.cpp:402 (g_memByte[523920]) — kPlAvgTacklingBallControlDiffChance.
        // 32-byte table indexed by (tackling+ballControl)/2 - opponent's same.
        public const int kPlAvgTacklingBallControlDiffChance = DataBase + 0x2520; // 32 bytes

        // player.cpp:599 (g_memByte[523904]) — kBallSpeedDeltaWhenControlled.
        // 8 entries × word = 16 bytes (indexed by ballControl skill).
        public const int kBallSpeedDeltaWhenControlled = DataBase + 0x2540;  // 16 bytes

        // player.cpp:713 (g_memByte[523856]) — dseg_17E276 (8 × word ball-control-by-tick).
        public const int dseg_17E276                = DataBase + 0x2550;  // 16 bytes

        // player.cpp:1021 (g_memByte[523888]) — kBallSpeedFinishing.
        // 8 entries × word = 16 bytes (indexed by finishing skill).
        public const int kBallSpeedFinishing        = DataBase + 0x2560;  // 16 bytes

        // player.cpp:1052 (g_memByte[523840]) — kBallSpeedKicking.
        // 8 entries × word = 16 bytes (indexed by shooting skill).
        public const int kBallSpeedKicking          = DataBase + 0x2570;  // 16 bytes

        // player.cpp:1347 (g_memByte[524722]) — kStaticHeaderBallSpeed (word).
        public const int kStaticHeaderBallSpeed     = DataBase + 0x2580;  // word

        // player.cpp:1351 (g_memByte[523766]) — kPlayerHeaderSpeedIncrease.
        // 8 entries × word = 16 bytes (indexed by heading skill).
        public const int kPlayerHeaderSpeedIncrease = DataBase + 0x2582;  // 16 bytes

        // player.cpp:2532 (g_memByte[523798]) — kHeaderLowJumpHeight (dword Q16.16).
        public const int kHeaderLowJumpHeight       = DataBase + 0x2594;  // dword

        // player.cpp:3259 (g_memByte[523802]) — kHeaderHighJumpHeight (dword Q16.16).
        public const int kHeaderHighJumpHeight      = DataBase + 0x2598;  // dword

        // player.cpp:2562 (g_memByte[523658]) — goodPassSampleCommand (signed word).
        // -1 = enqueue, -2 = stop, 0 = idle.
        public const int goodPassSampleCommand      = DataBase + 0x259C;  // word

        // player.cpp:3771 (g_memByte[485380]) — goodPassTimer (word).
        public const int goodPassTimer              = DataBase + 0x259E;  // word
        // player.cpp:3766 (g_memByte[485382]) — playingGoodPassTimer (signed word).
        public const int playingGoodPassTimer       = DataBase + 0x25A0;  // word

        // player.cpp:2626 (g_memByte[523872]) — kAIFailedPassChance.
        // 16 entries × word = 32 bytes (indexed by passing skill).
        public const int kAIFailedPassChance        = DataBase + 0x25A4;  // 32 bytes

        // player.cpp:2884-2992 (g_memByte[523822-523836]) — kPassingSpeed_*.
        // 8 thresholds × word = 16 bytes.
        public const int kPassingSpeedTable                = DataBase + 0x25C4;  // 16 bytes
        public const int kPassingSpeedCloserThan2500       = DataBase + 0x25C4;
        public const int kPassingSpeed_2500_10000          = DataBase + 0x25C6;
        public const int kPassingSpeed_10000_22500         = DataBase + 0x25C8;
        public const int kPassingSpeed_22500_40000         = DataBase + 0x25CA;
        public const int kPassingSpeed_40000_62500         = DataBase + 0x25CC;
        public const int kPassingSpeed_62500_90000         = DataBase + 0x25CE;
        public const int kPassingSpeed_90000_122500        = DataBase + 0x25D0;
        public const int kPassingSpeedFurtherThan122500    = DataBase + 0x25D2;

        // player.cpp:3003 (g_memByte[523806]) — kBallSpeedPassingIncrease.
        // 8 entries × word = 16 bytes (indexed by passing skill).
        public const int kBallSpeedPassingIncrease  = DataBase + 0x25D4;  // 16 bytes

        // player.cpp:3062 (g_memByte[523838]) — kFreePassReleasingBallSpeed (word).
        public const int kFreePassReleasingBallSpeed = DataBase + 0x25E4;  // word

        // player.cpp:3136 (g_memByte[524000]) — kPlayerTacklingDownTime.
        // 8 entries × word = 16 bytes (indexed by tackling skill).
        public const int kPlayerTacklingDownTime    = DataBase + 0x25E6;  // 16 bytes
        // player.cpp:3150 (g_memByte[524016]) — kComputerTacklingDownTime.
        public const int kComputerTacklingDownTime  = DataBase + 0x25F6;  // 16 bytes

        // player.cpp:3605-3715 — ball dest delta tables.
        // Each is 8 directions × (dx word + dy word) = 32 bytes.
        public const int kLeftThrowInBallDestDelta       = DataBase + 0x2606;  // 32 bytes
        public const int kRightThrowInBallDestDelta      = DataBase + 0x2626;  // 32 bytes
        public const int kPenaltyBallDestDelta           = DataBase + 0x2646;  // 32 bytes
        public const int kUpperLeftCornerBallDestDelta   = DataBase + 0x2666;  // 32 bytes
        public const int kUpperRightCornerBallDestDelta  = DataBase + 0x2686;  // 32 bytes
        public const int kLowerLeftCornerBallDestDelta   = DataBase + 0x26A6;  // 32 bytes
        public const int kLowerRightCornerBallDestDelta  = DataBase + 0x26C6;  // 32 bytes
        // End at 0x26E6 — still well below 0x4F800 sprite pool.

        // ---- Per-team TeamGame block (swos.h:296 TeamGame; pointed at by
        // ---- topTeamInGame / bottomTeamInGame and team.inGameTeamPtr) -------
        // The C++ TeamGame struct is 1704 bytes (42-byte header + 16 × 61-byte
        // PlayerInfo + 686-byte unknown tail). Existing port code (GameTime.cs
        // ForEachPlayer at line 482, TeamPort.UpdatePlayerShotChanceTable, etc.)
        // treats `topTeamInGame` as POINTING DIRECTLY AT players[0] — i.e., the
        // 42-byte header is skipped. To stay compatible, we expose two anchors
        // per team: a header base (used internally for layout + team name) and
        // the players[0] address, which is `header + 42`. The pointers stored
        // at `topTeamInGame` / `bottomTeamInGame` point at the players[0]
        // address; `team.inGameTeamPtr` (TeamData +10) points at the same.
        //
        // 16 PlayerInfo × 61 = 976 bytes per team. Plus 42-byte header. We
        // allocate 1024 bytes per team for padding + slack.
        public const int team1InGameTeamHeader      = DataBase + 0x2700;  // 1024 bytes
        public const int team1InGameTeamPlayers     = team1InGameTeamHeader + 42; // players[0]
        public const int team2InGameTeamHeader      = DataBase + 0x2B00;  // 1024 bytes
        public const int team2InGameTeamPlayers     = team2InGameTeamHeader + 42; // players[0]

        // Per-team shotChanceTable (swos.h:341, TeamGeneralInfo+24). Source
        // tables are in TeamPort.cs:38-52 — 30 × int16_t = 60 bytes per row.
        // We allocate a 60-byte slot per team and TeamPort.UpdatePlayerShot...
        // memcpy's the chosen row into it; team.shotChanceTable (+24) points
        // here. Consumers (PlayerUpdate.cs:466, :700) read +10/+52/+54 offsets.
        public const int team1ShotChanceTable       = DataBase + 0x2F00;  // 60 bytes
        public const int team2ShotChanceTable       = DataBase + 0x2F40;  // 60 bytes

        // Per-team display name (result.cpp:88-90 m_team1Name/m_team2Name).
        // 17 chars + null. Pointer stored in `res_team1Name` / `res_team2Name`.
        public const int team1NameStorage           = DataBase + 0x2F80;  // 18 bytes
        public const int team2NameStorage           = DataBase + 0x2F94;  // 18 bytes

        // ---- Replay request flags (gameLoop.cpp:43-44 m_fadeAndSaveReplay /
        // m_fadeAndInstantReplay). Original C++ keeps these as file-static
        // bools; we mirror them in VM memory so InputControls.cs and a future
        // replay-system port can share state through Memory.
        //
        // Set by:
        //   - InputControls.RequestFadeAndInstantReplay (controls.cpp:267 →
        //     gameLoop.cpp:155 mechanical port).
        //   - InputControls.RequestFadeAndSaveReplay  (controls.cpp:270 →
        //     gameLoop.cpp:150).
        // Read by:
        //   - handleHighlightsAndReplays (gameLoop.cpp:319-358) — fades the
        //     screen and dispatches to playInstantReplay / saveHighlightScene.
        //     That consumer isn't ported yet (replay system is Godot-render
        //     bound); we set the flag for parity so when the replay port lands
        //     the trigger pipeline already works.
        // Source: external/swos-port/src/game/gameLoop.cpp:43-44.
        public const int m_fadeAndSaveReplay        = DataBase + 0x2FB0;  // word (bool 0/1)
        public const int m_fadeAndInstantReplay     = DataBase + 0x2FB2;  // word

        // ---- Pause / stats flags (gameLoop.cpp:1855 pausedLoop +
        // gameLoop.cpp:1887 showStatsLoop). The original C++ uses
        // isGamePaused() and statsEnqueued() — backed by file-static state.
        // We mirror the bool input here so GameLoop.HandlePauseAndStats can
        // gate the no-op vs the (future) blocking-loop behaviour. Godot's
        // _PhysicsProcess never blocks the engine, so when pause is requested
        // we skip the CoreGameUpdate tick instead of looping.
        //
        // Set by:
        //   - InputControls.TogglePause (kGameEventPause handler — gameControls
        //     .cpp:259 → gameLoop.cpp:togglePause). Not ported; flag stays 0.
        // Read by:
        //   - GameLoop.HandlePauseAndStats (gameLoop.cpp:275 — see comment).
        // Source: external/swos-port/src/game/gameLoop.cpp:1855-1885.
        public const int gl_isPaused                = DataBase + 0x2FB4;  // word
        public const int gl_statsEnqueued           = DataBase + 0x2FB6;  // word

        // result.cpp scorer-text dirty flag — set whenever
        // Result.RegisterScorer commits a new goal into the scorer list. The
        // host renderer can poll this to know when to re-build the displayed
        // scorer lines (we don't render the strings inside the sim — see
        // Result.cs comment on m_team{1,2}ScorerLines). Cleared by the
        // renderer after consumption.
        // Source: external/swos-port/src/game/result.cpp:202 (updateScorersText
        //         call) — that's the call we stubbed in Result.cs:291.
        public const int res_scorersDirtyFlag       = DataBase + 0x2FB8;  // word

        // ---- gameLoop.cpp break-camera FSM private state -------------------
        // Source: external/swos-port/src/game/gameLoop.cpp.
        // These fields are read/written by the break-camera dispatcher
        // (gameLoop.cpp:1110-1846) which walks `breakCameraMode` 0..8 to drive
        // the post-stoppage camera + replay + transition logic. Two of its
        // states (mode 2 and mode 8) write `gameStatePl = 102` — without that
        // value being set, `stoppageTimerActive` is never accumulated (the
        // accumulator gate at gameLoop.cpp:519-572 requires gameStatePl==102),
        // which in turn means the AI fire-decision branches that gate on
        // `stoppageTimerActive >= 150` (AiBrain.cs:464-468 / l_goal_scored) never
        // trip. Net result: AI never shoots at goal during stoppages.
        //
        // Address allocations: 0x2FBA..0x2FCF (22 bytes free, sl_enabled at 0x3000-ish).

        // gameLoop.cpp:1388 — `mov breakState, ax (=gameState)`. Snapshot of
        // gameState taken when entering gameStatePl=102. Cleared by next break.
        // g_memByte[523134].
        public const int breakState                 = DataBase + 0x2FBA;  // word

        // gameLoop.cpp:52 — `static int m_goalCameraInterval = 55;`.
        // Used as the duration the break-camera holds before transitioning
        // mode 0 → 1 (75 when goalCameraMode != 0, gameLoop.cpp:1166-1174).
        // Static C int in the original (not in g_memByte); mirrored here for
        // parity. Default initialised in Memory.Init().
        public const int m_goalCameraInterval       = DataBase + 0x2FBC;  // word — 55

        // gameLoop.cpp:53 — `static int m_allowPlayerControlCameraInterval = 550;`.
        // Threshold: while `ballOutOfGameTimer < this`, mode-7 stays and waits
        // for the kicking team's controlledPlayer; once crossed, falls through
        // to mode 8 path (gameLoop.cpp:1704-1722).
        public const int m_allowPlayerControlCameraInterval = DataBase + 0x2FBE; // word — 550

        // gameLoop.cpp:1365 — `cameraCoordinatesValid` (g_memByte[455926]).
        // Set to 1 by pitch.cpp:145/155 at pitch-load. Mode-2 dispatch reads
        // this as a gate: must be non-zero to proceed with the gameStatePl=102
        // transition. We pre-init this to 1 in Memory.Init since our pitch
        // load happens unconditionally at boot.
        public const int cameraCoordinatesValid     = DataBase + 0x2FC0;  // word

        // gameLoop.cpp:1671,1704-1722 — `ballOutOfGameTimer` (g_memByte[523652]).
        // Mode-7 frame counter; reset to 0 entering mode 6, bumped each tick
        // in mode 7, compared to m_allowPlayerControlCameraInterval to fall
        // to mode 8.
        public const int ballOutOfGameTimer         = DataBase + 0x2FC2;  // word

        // gameLoop.cpp:1823 — `writeOnlyVar03` (g_memByte[523654]). Reset to 0
        // on entering mode 8; never read in the ported scope. Mechanical parity.
        public const int writeOnlyVar03             = DataBase + 0x2FC4;  // word

        // gameLoop.cpp:1278,1289 — replay / highlight auto-trigger globals
        // (g_memByte[131704] / [131700]). Both default 0 (off); SWOS UI sets
        // them via the options menu. We leave at 0 — the replay system isn't
        // ported anyway, so the auto-trigger branch is a no-op.
        public const int g_autoSaveHighlights       = DataBase + 0x2FC6;  // word
        public const int g_autoReplays              = DataBase + 0x2FC8;  // word

        // gameLoop.cpp:1285,1296-1297 — write-only replay-request flags.
        // saveHighlightScene (g_memByte[486134]) and instantReplayFlag
        // (g_memByte[486132]) are consumed by handleHighlightsAndReplays
        // (not ported). userRequestedReplay (g_memByte[455924]) is cleared
        // here as a side-effect.
        public const int saveHighlightScene         = DataBase + 0x2FCA;  // word
        public const int instantReplayFlag          = DataBase + 0x2FCC;  // word
        public const int userRequestedReplay        = DataBase + 0x2FCE;  // word

        // updateBench.cpp:54 — `static BenchState m_state`. Tracks bench menu
        // FSM state during a substitution. Enumerated as kInitial=0,
        // kAboutToSubstitute=1, kFormationMenu=2, kMarkingPlayers=3,
        // kOpponentsBench=4 (updateBench.h:3-10). Default at boot / between
        // matches is kInitial (initBenchVars at updateBench.cpp:140). Read by
        // bench.cpp:47-50 `inBenchMenus()` to qualify the bench-menu visibility.
        // Source: external/swos-port/src/game/bench/updateBench.cpp:54,140,194
        public const int m_benchState               = DataBase + 0x2FD0;  // word (BenchState enum)

        // gameLoop.cpp:51 — `static int m_penaltiesInterval = 110;`. Used at
        // gameLoop.cpp:460-465 to gate the per-tick penalty advance (compared
        // against `penaltiesTimer` which is bumped while a penalty shootout is
        // active). Static C int in the original (not in g_memByte); mirrored
        // here for parity so setPenaltiesInterval() can flip it. Default 110.
        // Source: external/swos-port/src/game/gameLoop.cpp:51,170-173.
        public const int m_penaltiesInterval        = DataBase + 0x2FD2;  // word — 110

        // gameLoop.cpp:49 — `static bool m_playingMatch;`. Set true by
        // initGameLoop() (gameLoop.cpp:204) and false after the match loop
        // exits (gameLoop.cpp:134). Read by isMatchRunning() (gameLoop.cpp:
        // 165-168). Behavioural gate consumed by audio + UI layers (e.g.
        // chants.cpp / sfx.cpp / replay-exit menu) to decide whether an
        // in-progress match is live. Mirrored here as a word for parity with
        // the rest of the gameLoop scalars; 0 = false, non-zero = true.
        // Source: external/swos-port/src/game/gameLoop.cpp:49,134,165-168,204.
        public const int m_playingMatch             = DataBase + 0x2FD4;  // word (bool)

        // ---- Animation tables (swos.asm:218678+) ----------------------------
        // Each `PlayerAnimationTable` is a 130-byte struct:
        //   +0    word    frameDelay
        //   +2    dword[8]  team1 player frame-indices pointers (one per direction)
        //   +34   dword[8]  team2 player frame-indices pointers
        //   +66   dword[8]  team1 goalie frame-indices pointers
        //   +98   dword[8]  team2 goalie frame-indices pointers
        //
        // SetPlayerAnimationTable (swos.asm:104309) indexes with
        // `(teamNumber-1 + (ordinal==1 ? 2 : 0)) * 8 + direction`. The
        // frame-indices array referenced by each entry is a word-stream of
        // image indices terminated by -999 (or other negative opcodes).
        //
        // The frame-indices arrays themselves live at the addresses just below
        // (kPlayerRunningUpTeam1Frames etc.). AnimationTablesData.Init()
        // populates both regions at boot.
        //
        // Source: swos.asm:218920 (playerRunningAnimTable),
        //         swos.asm:218954 (playerNormalStandingAnimTable),
        //         swos.asm:218989 (plTacklingAnimTable),
        //         swos.asm:219043 (playerTackledAnimTable),
        //         swos.asm:219064 (goalieCatchingBallAnimTable),
        //         swos.asm:219098 (aboutToThrowInAnimTable),
        //         swos.asm:219133 (throwInPassAnimTable),
        //         swos.asm:219166 (throwInKickAnimTable),
        //         swos.asm:219232 (leftGoalieJumpingHighAnimTable),
        //         swos.asm:219298 (rightGoalieJumpingHighAnimTable),
        //         swos.asm:219332 (leftGoalieJumpingLowAnimTable),
        //         swos.asm:219365 (rightGoalieJumpingLowAnimTable),
        //         swos.asm:218678 (staticHeaderAttemptAnimTable),
        //         swos.asm:218746 (staticHeaderHitAnimTable),
        //         swos.asm:218781 (jumpHeaderAttemptAnimTable),
        //         swos.asm:218817 (jumpHeaderHitAnimTable),
        //         swos.asm:219566 (plInjuredAnimTable).
        //
        // 17 tables × 130 bytes = 2210 bytes; reserve 4 KB (16 × 0x100) for
        // padding. Each anchored at a clean 0x100 boundary so SetPlayerAnimationTable
        // bound-check is trivial.
        public const int kPlayerRunningAnimTableAddr        = DataBase + 0x3000;
        public const int kPlayerStandingAnimTableAddr       = DataBase + 0x3100;
        public const int kPlTacklingAnimTableAddr           = DataBase + 0x3200;
        public const int kPlayerTackledAnimTableAddr        = DataBase + 0x3300;
        public const int kGoalieCatchingBallAnimTableAddr   = DataBase + 0x3400;
        public const int kAboutToThrowInAnimTableAddr       = DataBase + 0x3500;
        public const int kThrowInPassAnimTableAddr          = DataBase + 0x3600;
        public const int kThrowInKickAnimTableAddr          = DataBase + 0x3700;
        public const int kLeftGoalieJumpingHighAnimTableAddr  = DataBase + 0x3800;
        public const int kRightGoalieJumpingHighAnimTableAddr = DataBase + 0x3900;
        public const int kLeftGoalieJumpingLowAnimTableAddr   = DataBase + 0x3A00;
        public const int kRightGoalieJumpingLowAnimTableAddr  = DataBase + 0x3B00;
        public const int kStaticHeaderAttemptAnimTableAddr  = DataBase + 0x3C00;
        public const int kStaticHeaderHitAnimTableAddr      = DataBase + 0x3D00;
        public const int kJumpHeaderAttemptAnimTableAddr    = DataBase + 0x3E00;
        public const int kJumpHeaderHitAnimTableAddr        = DataBase + 0x3F00;
        public const int kPlInjuredAnimTableAddr            = DataBase + 0x4000;

        // ---- Frame-indices arrays (swos.asm:218368+) ------------------------
        // Each is a stream of word image indices terminated by -999 (or other
        // negative opcode). Pointed at from the per-table per-direction entries
        // above. Populated by AnimationTablesData.Init().
        //
        // Block layout: starts at DataBase + 0x4200, 4 bytes per word-entry
        // (with terminator), reserved ~4 KB region.
        public const int kFrameIndicesArraysBase            = DataBase + 0x4200;
        // End of frame-indices block (allocation cursor maintained by
        // AnimationTablesData). Sized to fit the per-table pointers we expose.
        public const int kFrameIndicesArraysEnd             = DataBase + 0x4FF0;

        // ---- DoGoalkeeperSprites ball-height tables (swos.asm:245601-245604) -
        // Two static tables read by DoGoalkeeperSprites to drive ballSprite.z
        // during a goalkeeper dive animation.
        //
        // dseg_17DEF4 — 28 × word indexed by (sprite.imageIndex - 971) or
        //               (sprite.imageIndex - 1029) for goalie1, or
        //               (sprite.imageIndex - 1087) or (- 1145) for goalie2.
        //               The asm shifts the result left by 1 before indexing
        //               (clamping out-of-range to <28). Used by the
        //               "diving-right" branch (gameLoop.cpp:308 →
        //               DoGoalkeeperSprites mid-routine).
        //
        // kGoalKeeperClaimingBallHeight — 7 × word indexed by
        //                                  sprite.frameSwitchCounter (already
        //                                  left-shifted by 1 in asm). Used by
        //                                  the "diving-left" branch.
        //
        // Source: external/swos-port/swos/swos.asm:245601-245604
        //         (dseg_17DEF4 / kGoalKeeperClaimingBallHeight verbatim).
        public const int dseg_17DEF4                        = DataBase + 0x5000; // 28 × word = 56 bytes
        public const int kGoalKeeperClaimingBallHeight      = DataBase + 0x5040; // 7 × word = 14 bytes

        // ---- initGoalSprites memory slots (gameLoop.cpp:1911-1915) -----------
        // goal1TopSprite + goal2BottomSprite are static Sprite instances in
        // swos-port. initGoalSprites simply calls setImage() to bind the
        // engine-side sprite-image index. We mirror the imageIndex slot only
        // (the X/Y/Z positions are pitch-relative constants Godot draws from
        // pitch-tile coords). The renderer reads these to decide whether to
        // draw the goal sprite (image >= 0).
        //
        // Source: external/swos-port/src/game/gameLoop.cpp:1911-1915
        //         external/swos-port/src/sprites/sprites.h:67-68
        //           (kTopGoalSprite=1205, kBottomGoalSprite=1206)
        public const int goal1TopSprite_ImageIndex          = DataBase + 0x5060; // word
        public const int goal2BottomSprite_ImageIndex       = DataBase + 0x5062; // word

        // ---- markPlayer sprite slot (gameLoop.cpp:1919-2010) -----------------
        // playerMarkSprite — a single floating sprite that hovers above the
        // controlled-player target during set-pieces. We mirror imageIndex,
        // X/Y/Z (whole-pixel words), which is all the consumer (renderer) reads.
        //
        // Source: external/swos-port/src/game/gameLoop.cpp:1919 (markPlayer)
        //         + external/swos-port/swos/swos.asm:1938 (playerMarkSprite =
        //           offset 325968, Sprite struct 110 bytes; we own 8 bytes here).
        public const int playerMarkSprite_ImageIndex        = DataBase + 0x5064; // word
        public const int playerMarkSprite_XWhole            = DataBase + 0x5066; // word
        public const int playerMarkSprite_YWhole            = DataBase + 0x5068; // word
        public const int playerMarkSprite_ZWhole            = DataBase + 0x506A; // word

        // ---- Display-sprite dirty flag (gameSprites.cpp:101-111) -------------
        // initDisplaySprites rebuilds the per-frame sorted sprite list. Our
        // renderer iterates sprites directly each frame, so the sorted list
        // is implicit — we just need a "sprite set changed" notification so
        // the Godot side knows to invalidate any cached draw state. Mirrors
        // the dirty-flag pattern used by StubUpdateScorersText (Result.cs:390).
        //
        // Source: external/swos-port/src/sprites/gameSprites.cpp:101-111.
        public const int displaySpritesDirtyFlag            = DataBase + 0x506C; // word

        // ---- initGameVariables() per-match init fields ---------------------
        // Mechanical port of external/swos-port/src/game/game.cpp:1403-1448.
        // These four words are zeroed by initGameVariables() at the start of
        // every match (not just at process boot). The other 30+ fields touched
        // by initGameVariables() are already declared above (statsTimer,
        // statsTeam{1,2}Goals, team1{1,2}TotalGoals, etc.); these are the
        // ones that had no prior home.
        //
        // gameRandValue (game.cpp:75, swos.asm:219870 — `dw 0`): one-shot
        // seed-XOR slot, written immediately after initPlayerCardChance with
        // `swos.gameRandValue = SWOS::rand()`. Consumed by ApplyTeamTactics
        // (swos.asm:37500-37503) to XOR into D1 before calling Randomize2.
        // Currently no C# caller of ApplyTeamTactics exists, but persisting
        // the value here keeps the deterministic chain intact for the moment
        // the asm-side ApplyTeamTactics gets ported (or runs from the VM).
        public const int gameRandValue              = DataBase + 0x5070;  // word

        // currentTick (game.cpp:1445, swos.asm:202139 — `dw 0`): file-static
        // word incremented every video timer interrupt by timer.cpp:116
        // (`swos.currentTick++`). Distinct from currentGameTick — currentTick
        // ticks at the host-platform timer rate (used as a "true" randomness
        // source by ApplyTeamTactics' second XOR; player.cpp:595 also reads
        // `bit 1` to alternate between two animation frames per tick).
        // initGameVariables zeroes this; the VM/host then increments it.
        public const int currentTick                = DataBase + 0x5072;  // word

        // longFireFlag / longFireTime (game.cpp:1442, swos.asm:199186-199188
        // — both `dw 0`): menu-controls long-press accumulator. menuControls.cpp:
        //   113: `swos.longFireTime = swos.longFireFlag = 0;`  (fire released)
        //   115: `swos.longFireTime += swos.lastFrameTicks;`
        //   116-118: when longFireTime >= 24 → reset to 16 and bump
        //            longFireFlag (auto-repeat tick).
        // Belongs to menu-mode controls, but initGameVariables() also clears
        // them at match start so the post-match menu starts from a clean slate.
        public const int longFireFlag               = DataBase + 0x5074;  // word
        public const int longFireTime               = DataBase + 0x5076;  // word
    }

    // ---- Lifecycle ----------------------------------------------------------

    // Reset to initial state — clears memory, then loads constants we know about.
    // Called at game start AND when the user toggles between Amiga / PC mode.
    public static void Init(bool pcMode = true)
    {
        System.Array.Clear(mem, 0, mem.Length);

        // Ball kick speeds — same in both modes (swos.asm data section).
        WriteWord(Addr.kBallKickingSpeed, 2208);
        WriteWord(Addr.kHighKickBallSpeed, 2688);
        WriteWord(Addr.kNormalKickBallSpeed, 2560);
        WriteWord(Addr.kPlayerTacklingSpeed, 1792);
        WriteWord(Addr.kJumpHeaderSpeed, 2048);

        // Tackling downtime tables (16 bytes each = 8 entries of WORD,
        // indexed by player tackling skill 0..7).
        //   kPlayerTacklingDownTime   = human  cancellation downtime.
        //   kComputerTacklingDownTime = CPU    cancellation downtime.
        // The asm constant lookup is `[A0 + skill*2]` (16-bit word).
        //
        // Without these the lookup returns 0 → setPlayerDowntimeAfterTackle
        // writes 0 to Sprite.playerDownTimer, the next per-tick decrement
        // wraps to 0xFF, and the player stays in PL_TACKLING for ~10 s
        // before the wrap completes — visibly stuck at the goal-line
        // dest-Y clamp (PlayerBeginTackling, 129/769).
        //
        // Source: external/swos-port/swos/swos.asm:245843-245845.
        for (int i = 0; i < 8; i++)
        {
            // 30, 27, 24, 21, 18, 15, 12, 9 → 30 - 3*i.
            WriteWord(Addr.kPlayerTacklingDownTime + i * 2, 30 - 3 * i);
            // 3 across the board.
            WriteWord(Addr.kComputerTacklingDownTime + i * 2, 3);
        }

        // Goalkeeper speeds — same in both modes.
        WriteWord(Addr.kGoalkeeperCatchSpeed, 768);
        WriteWord(Addr.kGoalkeeperMoveToBallSpeed, 1024);
        WriteWord(Addr.kGoalkeeperSpeedWhenGameStopped, 1024);
        WriteWord(Addr.kSubstitutedPlayerSpeed, 1536);
        WriteWord(Addr.kRefereeSpeed, 1024);

        // Vertical deltas Q16.16 — same in both modes.
        WriteDword(Addr.kBallKickingDeltaZ, 0x14000);
        WriteDword(Addr.kBallJumpHeaderDeltaZ, 0xA000);

        // amigaMode.cpp:35-37 (Amiga) / 53-55 (PC). Set at boot to match swos-port
        // default (PC mode = swos.ini:15 gameStyle=0).
        // NOTE: kGravityConstant is DWORD in SWOS (swos.asm:203966 `dd 0CDBh`).
        // ball.cpp:467 reads as dword — must use WriteDword here.
        if (pcMode)
        {
            WriteWord(Addr.kBallGroundConstant, 13);
            WriteWord(Addr.kBallAirConstant, 4);
            WriteDword(Addr.kGravityConstant, 3291);
            WriteWord(Addr.kKeeperSaveDistance, 16);
        }
        else
        {
            WriteWord(Addr.kBallGroundConstant, 16);
            WriteWord(Addr.kBallAirConstant, 10);
            WriteDword(Addr.kGravityConstant, 4608);
            WriteWord(Addr.kKeeperSaveDistance, 24);
        }

        // Pitch-dependent values — initialised to Normal pitch (type 4) defaults.
        // swos-port writes these in pitch.cpp based on selected pitch type, BUT
        // the values are NOT in our ball_trace.csv (because they're stable per
        // match, not per tick). Our golden tests need these to match what
        // swos-port had set, so we hardcode Normal pitch (the most common).
        //
        // For non-Normal pitches, the test will diverge on bounce mechanics —
        // either capture pitch type in CSV or hardcode the relevant defaults.
        // Values from swos.asm pitch-data tables:
        //   BallSpeedBounceFactors[0..6] = {24, 80, 80, 72, 64, 40, 32}  // pitch type 0..6
        //   BallBounceFactors[0..6]      = {88,112,104,104, 96, 88, 80}
        //   PitchBallSpeedInfluence[0..6]= {-3,  4,  1,  0,  0, -1, -1}  // PC mode
        // Normal pitch = index 4.
        WriteWord(Addr.ballSpeedBounceFactor, 64);   // Normal
        WriteWord(Addr.ballBounceFactor, 96);        // Normal
        WriteWord(Addr.pitchBallSpeedFactor, 0);     // Normal

        // Pre-match menu selections — defaults that drive Pitch.SetPitchType /
        // SetPitchNumber. gamePitchType = -1 forces the fixed-probability roll
        // (the standard friendly-match path); the other three stay at 0.
        // pitch.cpp:244, 269. OpenSWOS doesn't surface these in the menu yet,
        // so the defaults stand until a season selector lands.
        WriteWord(Addr.gamePitchType, -1);
        WriteWord(Addr.gamePitchTypeOrSeason, 0);
        WriteWord(Addr.gameSeason, 0);
        WriteWord(Addr.plg_D0_param, 0);

        // Dive deltas (PC mode by default, amigaMode.cpp:12-13).
        int[] divePc    = { 0x28000, 0x30000, 0x38000, 0x40000, 0x48000, 0x50000, 0x58000, 0x60000 };
        int[] diveAmiga = { 0x30000, 0x38000, 0x40000, 0x48000, 0x50000, 0x58000, 0x60000, 0x68000 };
        int[] dive = pcMode ? divePc : diveAmiga;
        for (int i = 0; i < 8; i++)
            WriteDword(Addr.kGoalkeeperDiveDeltasBase + i * 4, dive[i]);

        // kBallPlOffsets (swos.asm:245818):
        //   dw 0,-1, 1,-1, 1,0, 1,1, 0,1, -1,1, -1,0, -1,-1
        short[] plOffs = { 0, -1, 1, -1, 1, 0, 1, 1, 0, 1, -1, 1, -1, 0, -1, -1 };
        for (int i = 0; i < plOffs.Length; i++)
            WriteWord(Addr.kBallPlOffsetsBase + i * 2, plOffs[i]);

        // kPlayerWithBallOffsets (swos.asm:245869):
        //   dw 0, 1, -1, 1, -1, 0, -1, -1, 0, -1, 1, -1, 1, 0, 1, 1
        // Consumed by player.cpp:85 UpdatePlayerWithBall — the per-direction
        // delta added to ball.x/y to derive where the keeper (or other
        // ball-pinned outfielder, via UpdatePlayerWithBall callers in
        // PlayerControlled.RunControlledBranch) is snapped to. Without this
        // init, UpdatePlayerWithBall read all-zero offsets and pinned the
        // sprite exactly onto the ball — visible as a half-pixel desync after
        // every keeper claim. Note: this table is the negation of
        // kBallPlOffsets above (player-from-ball vs ball-from-player).
        // Source: external/swos-port/swos/swos.asm:245869.
        short[] plWithBallOffs = { 0, 1, -1, 1, -1, 0, -1, -1, 0, -1, 1, -1, 1, 0, 1, 1 };
        for (int i = 0; i < plWithBallOffs.Length; i++)
            WriteWord(Addr.kPlayerWithBallOffsets + i * 2, plWithBallOffs[i]);

        // Spin / kick-after-touch constants (swos.asm:203954-203989).
        WriteDword(Addr.kHighKickDeltaZ, 0x20000);    // 2.0 in Q16.16
        WriteDword(Addr.kNormalKickDeltaZ, 0x16000);  // 1.375 in Q16.16

        // kSpinMultiplierFactor[10] = {5, 4, 3, 2, 2, 2, 2, 1, 1, 1}.
        // swos.asm:203963 — indexed by spinTimer 0..9 (× 2 bytes per entry).
        short[] spinMult = { 5, 4, 3, 2, 2, 2, 2, 1, 1, 1 };
        for (int i = 0; i < spinMult.Length; i++)
            WriteWord(Addr.kSpinMultiplierFactor + i * 2, spinMult[i]);

        // kKickSpinFactor — swos.asm:203967-203969. 8 directions × 4 words each
        // (leftX, leftY, rightX, rightY). Curved trajectory deltas for a KICK.
        short[] kickSpin = {
            -32,   0,  32,   0,  // dir 0 (N)
              0, -23,  23,   0,  // dir 1 (NE)
              0, -32,   0,  32,  // dir 2 (E)
             23,   0,   0,  23,  // dir 3 (SE)
             32,   0, -32,   0,  // dir 4 (S)
              0,  23, -23,   0,  // dir 5 (SW)
              0,  32,   0, -32,  // dir 6 (W)
            -23,   0,   0, -23,  // dir 7 (NW)
        };
        for (int i = 0; i < kickSpin.Length; i++)
            WriteWord(Addr.kKickSpinFactor + i * 2, kickSpin[i]);

        // kPassingSpinFactor — swos.asm:203975-203977. Same layout, smaller values
        // (passes curve less than shots).
        short[] passSpin = {
            -16,   0,  16,   0,  // dir 0
              0, -11,  11,   0,  // dir 1
              0, -16,   0,  16,  // dir 2
             11,   0,   0,  11,  // dir 3
             16,   0, -16,   0,  // dir 4
              0,  11, -11,   0,  // dir 5
              0,  16,   0, -16,  // dir 6
            -11,   0,   0, -11,  // dir 7
        };
        for (int i = 0; i < passSpin.Length; i++)
            WriteWord(Addr.kPassingSpinFactor + i * 2, passSpin[i]);

        // AI after-touch delta tables — swos.asm:246182-246188. Consecutive
        // SIGNED words at each declared table base (see Addr.AI_* @ 0x2410..).
        // Read back via Memory.ReadSignedWord in AiBrain.cs's steer path, so
        // the -999 long-kick sentinel round-trips as a signed 16-bit value.
        // Without these the tables read all-zero → AI shots have no
        // strength-graded curve / no loft.
        short[] aiRandomRotate = { -32, 32 };        // AI_randomRotateTable
        short[] aiLeftSpin     = { -1, -2, -3 };     // AI_leftSpinTable  (strength 0/1/2)
        short[] aiRotateRight  = { 1, 2, 3 };        // AI_rotateRightTable
        short[] aiLongKick     = { 0, -999, 4 };     // AI_longKickTable
        for (int i = 0; i < aiRandomRotate.Length; i++)
            WriteWord(Addr.AI_randomRotateTable + i * 2, aiRandomRotate[i]);
        for (int i = 0; i < aiLeftSpin.Length; i++)
            WriteWord(Addr.AI_leftSpinTable + i * 2, aiLeftSpin[i]);
        for (int i = 0; i < aiRotateRight.Length; i++)
            WriteWord(Addr.AI_rotateRightTable + i * 2, aiRotateRight[i]);
        for (int i = 0; i < aiLongKick.Length; i++)
            WriteWord(Addr.AI_longKickTable + i * 2, aiLongKick[i]);

        // Global game state — sane defaults. Real values pushed by Main.cs each
        // tick via SwosVm.Sync (when the port path is enabled).
        WriteWord(Addr.gameStatePl, 100);  // ST_GAME_IN_PROGRESS
        WriteWord(Addr.gameState, 0);
        WriteWord(Addr.hideBall, 0);
        WriteWord(Addr.ballShadowImageIndex, 1183);
        WriteWord(Addr.ballNextX, 0);
        WriteWord(Addr.ballNextY, 0);

        // Ball frame-index tables (swos.asm:219721, 219724).
        // ballMovingFrameIndices = { 1182, 1181, 1180, 1179, -1 } — rolling animation
        // ballStaticFrameIndices = { 1182, 1182, -1 } — single frame
        short[] movingFrames = { 1182, 1181, 1180, 1179, -1 };
        short[] staticFrames = { 1182, 1182, -1 };
        for (int i = 0; i < movingFrames.Length; i++)
            WriteWord(Addr.ballMovingFrameIndices_Table + i * 2, movingFrames[i]);
        for (int i = 0; i < staticFrames.Length; i++)
            WriteWord(Addr.ballStaticFrameIndices_Table + i * 2, staticFrames[i]);
        // Pointers to those tables.
        WriteDword(Addr.ballMovingFrameIndices_Addr, Addr.ballMovingFrameIndices_Table);
        WriteDword(Addr.ballStaticFrameIndices_Addr, Addr.ballStaticFrameIndices_Table);

        // Substitution + last-team defaults.
        WriteWord(Addr.g_substituteInProgress, 0);
        WriteDword(Addr.teamThatSubstitutes, 0);
        WriteDword(Addr.lastTeamPlayedBeforeBreak, 0);

        // ballNextGroundX/Y default to -1 (matches updatePlayers.cpp:916).
        WriteWord(Addr.ballNextGroundX, -1);
        WriteWord(Addr.ballNextGroundY, -1);

        // Match score + scorer state — start zeroed. Memory.Clear() above
        // already wiped these; explicit writes here document the contract.
        WriteWord(Addr.team1TotalGoals, 0);
        WriteWord(Addr.team2TotalGoals, 0);
        WriteWord(Addr.team1PenaltyGoals, 0);
        WriteWord(Addr.team2PenaltyGoals, 0);
        WriteWord(Addr.team1GoalsDigit1, 0);
        WriteWord(Addr.team1GoalsDigit2, 0);
        WriteWord(Addr.statsTeam1Goals, 0);
        WriteWord(Addr.team2GoalsDigit1, 0);
        WriteWord(Addr.team2GoalsDigit2, 0);
        WriteWord(Addr.statsTeam2Goals, 0);
        WriteWord(Addr.goalScored, 0);
        WriteWord(Addr.runSlower, 0);
        WriteWord(Addr.penalty, 0);
        WriteWord(Addr.playingPenalties, 0);

        // Referee — start off-screen with no card pending.
        WriteWord(Addr.refState, 0);     // kRefOffScreen
        WriteWord(Addr.whichCard, 0);    // kNoCard
        WriteWord(Addr.refTimer, 0);
        WriteWord(Addr.lastFrameTicks, 1);

        // Game-length — default to medium (1 = 18 sec/min-of-game).
        WriteWord(Addr.gameLengthInGame, 1);

        // Game-time module state — pristine.
        WriteDword(Addr.gt_gameTimeInMinutes, 0);
        WriteDword(Addr.gt_endGameCounter, 0);
        WriteDword(Addr.gt_timeDelta, 18);  // matches kGameLenSecondsTable[1]
        WriteDword(Addr.gt_gameSeconds, 0);
        WriteDword(Addr.gt_secondsSwitchAccumulator, 0);
        WriteWord(Addr.gt_showTime, 0);

        // Extra-time / penalty defaults — disabled.
        WriteWord(Addr.extraTimeState, 0);
        WriteWord(Addr.penaltiesState, 0);
        WriteWord(Addr.secondLeg, 0);
        WriteWord(Addr.playing2ndGame, 0);
        WriteWord(Addr.stateGoal, 0);

        // Match-half / kickoff direction (swos.asm:112090+ initialisers).
        // halfNumber=1 at boot, flipped to 2 by EndFirstHalf / EndFirstExtraTime;
        // teamPlayingUp / teamStarting are written by InitMatch (game.cpp) at
        // kick-off; default to 1 here so the post-init code-paths don't see 0.
        WriteWord(Addr.halfNumber, 1);
        WriteWord(Addr.teamPlayingUp, 1);
        WriteWord(Addr.teamStarting, 1);

        // Penalty-shootout bookkeeping defaults — zeroed; populated only when
        // StartPenalties runs (swos.asm:100501-100527).
        WriteWord(Addr.savedTeam1Goals, 0);
        WriteWord(Addr.savedTeam2Goals, 0);
        WriteWord(Addr.team1PenaltyShooterIndex, 0);
        WriteWord(Addr.team2PenaltyShooterIndex, 0);
        WriteWord(Addr.team1PenaltyAttempts, 0);
        WriteWord(Addr.team2PenaltyAttempts, 0);

        // ball.cpp:checkIfBallOutOfPlay defaults — pristine values.
        WriteWord(Addr.playRefereeWhistle, 0);
        WriteDword(Addr.lastPlayerPlayed, 0);
        WriteDword(Addr.lastKeeperPlayed, 0);
        WriteWord(Addr.statsTeam1GoalsCopy2, 0);
        WriteWord(Addr.statsTeam2GoalsCopy2, 0);
        WriteWord(Addr.goalCameraMode, 0);
        WriteWord(Addr.teamNumThatScored, 0);
        WriteDword(Addr.teamScoredDataPtr, 0);
        WriteDword(Addr.teamScoredGamePtr, 0);
        WriteWord(Addr.goalCounter, 0);
        WriteWord(Addr.patternsGoalCounter, 0);
        WriteWord(Addr.breakCameraMode, 0);
        WriteWord(Addr.goalOut, 0);
        WriteWord(Addr.forceLeftTeam, 0);
        WriteWord(Addr.cameraXVelocity, 0);
        WriteWord(Addr.cameraYVelocity, 0);
        WriteWord(Addr.stoppageTimerTotal, 0);
        WriteWord(Addr.stoppageTimerActive, 0);
        WriteWord(Addr.cameraDirection, 0);
        WriteByte(Addr.playerTurnFlags, 0);
        WriteWord(Addr.gameNotInProgressCounterWriteOnly, 0);
        WriteWord(Addr.lastPlayerTurnFlags, 0);

        // spinningLogo.cpp:12-15 — defaults. spinningLogo starts disabled
        // (m_enabled=false, m_frameIndex=0, m_pictureIndex=0). enableSpinningLogo()
        // flips m_enabled on at match-start (gameLoop.cpp:115 in original).
        WriteWord(Addr.sl_enabled, 0);
        WriteWord(Addr.sl_frameIndex, 0);
        WriteWord(Addr.sl_pictureIndex, 0);

        // Corner flag + curPlayerNum sprite arrays — zeroed (imageIndex 0 ≡
        // hidden / unset; per-tick UpdateCornerFlags fills with real values).
        for (int i = 0; i < 24; i++) WriteByte(Addr.cornerFlags + i, 0);
        for (int i = 0; i < 16; i++) WriteByte(Addr.curPlayerNumSprites + i, 0);

        // playerNameDisplay.cpp:11-12 — pnd_* defaults (hidden state).
        WriteWord(Addr.pnd_topTeam, 0);
        WriteWord(Addr.pnd_playerOrdinal, -1);
        WriteWord(Addr.pnd_visible, 0);
        WriteWord(Addr.pnd_nobodysBallLastFrame, 0);

        // Section 4 of updateBall — quadrant tables + indexed outputs.
        // swos.asm:245746-245748.
        short[] xQuadLimits = { 81, 183, 285, 387, 489 };
        short[] yQuadLimits = { 129, 220, 312, 403, 495, 586, 678 };
        for (int i = 0; i < xQuadLimits.Length; i++)
            WriteWord(Addr.ballXQuadrantLimits + i * 2, xQuadLimits[i]);
        for (int i = 0; i < yQuadLimits.Length; i++)
            WriteWord(Addr.ballYQuadrantLimits + i * 2, yQuadLimits[i]);
        WriteWord(Addr.ballQuadrantIndex, 0);
        WriteWord(Addr.ballXQuadrantDead, 0);
        WriteWord(Addr.ballYQuadrantDead, 0);
        WriteWord(Addr.playerXQuadrantOffset, 0);
        WriteWord(Addr.playerYQuadrantOffset, 0);
        WriteWord(Addr.currentGameTick, 0);

        // updatePlayers.cpp — entry-point bookkeeping defaults.
        WriteDword(Addr.prevLastPlayer, 0);
        WriteDword(Addr.prevLastTeamPlayed, 0);
        WriteWord(Addr.ballInUpperPenaltyArea, 0);
        WriteWord(Addr.ballInLowerPenaltyArea, 0);
        WriteWord(Addr.ballInGoalkeeperArea, 0);
        WriteDword(Addr.lastPlayerBeforeGoalkeeper, 0);
        WriteWord(Addr.nobodysBallTimer, 0);

        // gameLoop.cpp orchestrator state — pristine.
        WriteWord(Addr.playGame, 1);  // matches gameLoop.cpp:86 (top of gameLoop()).
        WriteDword(Addr.frameCounter, 0);
        WriteDword(Addr.teamSwitchCounter, 0);
        WriteWord(Addr.fireBlocked, 0);
        WriteWord(Addr.spaceReplayTimer, 0);
        WriteWord(Addr.penaltiesTimer, 0);
        WriteWord(Addr.inGameCounter, 0);
        WriteWord(Addr.stoppageEventTimer, 0);
        WriteWord(Addr.gameNotInProgressCounter, 0);
        WriteWord(Addr.lastGameTick, 0);
        WriteWord(Addr.loadCrowdChantSampleFlag, 0);
        // gameLoop.cpp:52 — static int m_initalKickInterval = 825 (~11.8 s @ 70 Hz).
        WriteWord(Addr.m_initalKickInterval, 825);
        WriteWord(Addr.initialKickWriteOnlyTicks, 0);
        // gameLoop.cpp:51 — static int m_penaltiesInterval = 110.
        WriteWord(Addr.m_penaltiesInterval, 110);
        // gameLoop.cpp:49 — static bool m_playingMatch (default false).
        // initGameLoop() flips this to 1 at match start; the outer-loop tail
        // (gameLoop.cpp:134) drops it back to 0 after gameOver completes.
        WriteWord(Addr.m_playingMatch, 0);

        // gameLoop.cpp break-camera FSM private state — see Memory.Addr block.
        // m_goalCameraInterval = 55, m_allowPlayerControlCameraInterval = 550
        // (gameLoop.cpp:52-53; earlier sessions mis-seeded 50/75).
        // cameraCoordinatesValid = 1 (set by pitch.cpp at pitch-load; we set
        // unconditionally because our pitch loader runs at boot).
        // Source: external/swos-port/src/game/gameLoop.cpp + pitch.cpp:145.
        WriteWord(Addr.breakState, 0);
        WriteWord(Addr.m_goalCameraInterval, 55);
        WriteWord(Addr.m_allowPlayerControlCameraInterval, 550);
        WriteWord(Addr.cameraCoordinatesValid, 1);
        WriteWord(Addr.ballOutOfGameTimer, 0);
        WriteWord(Addr.writeOnlyVar03, 0);
        WriteWord(Addr.g_autoSaveHighlights, 0);
        WriteWord(Addr.g_autoReplays, 0);
        WriteWord(Addr.saveHighlightScene, 0);
        WriteWord(Addr.instantReplayFlag, 0);
        WriteWord(Addr.userRequestedReplay, 0);

        // Goalkeeper jump / deflect speeds — swos.asm:202381-202392, 245841.
        WriteWord(Addr.kGoalkeeperNearJumpSpeed, 1024);
        WriteWord(Addr.kGoalkeeperFarJumpSpeed, 2048);
        WriteWord(Addr.kGoalkeeperFarJumpSlowerSpeed, 1280);
        WriteWord(Addr.kGoalkeeperStrongDeflectBallSpeed, 1536);
        WriteWord(Addr.kGoalkeeperMediumDeflectBallSpeed, 1024);
        WriteWord(Addr.kGoalkeeperWeakDeflectBallSpeed, 512);
        WriteDword(Addr.kGoalkeeperDeflectDeltaZ, 49152);

        // Penalty save distances — swos.asm:203733/203736.
        WriteWord(Addr.kKeeperPenaltySaveDistanceFar, 20);
        WriteWord(Addr.kKeeperPenaltySaveDistanceNear, 12);

        // updatePlayers.cpp:2124 — kShotAtGoalMinumumSpeed. swos.asm:202413 `dw 512`.
        WriteWord(Addr.kShotAtGoalMinumumSpeed, 512);

        // updatePlayers.cpp:2334 — goalScoredChances table. swos.asm:246161
        // `db 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15` (15 bytes).
        for (int i = 0; i < 15; i++)
            WriteByte(Addr.goalScoredChances + i, (byte)(i + 1));

        // updatePlayers.cpp:2547 — dseg_1105EF. swos.asm:202375 `dw 512`.
        WriteWord(Addr.dseg_1105EF, 512);

        // updatePlayers.cpp:4190 — dseg_110BDB. swos.asm:203728.
        {
            short[] diveParryShifts = { 1, 4, 3, 3, 3, 2, 2, 2 };
            for (int i = 0; i < diveParryShifts.Length; i++)
                WriteWord(Addr.dseg_110BDB + i * 2, diveParryShifts[i]);
        }

        // updatePlayers.cpp:3877 — dseg_17EECC. swos.asm:246164-246167 (60 bytes).
        {
            byte[] penaltyDiveRow =
            {
                // 60 bytes / 30 words. The run of 7s must be SIXTEEN pairs — it was
                // fifteen, which was 58 bytes and shifted every element from index 20
                // on down one word, so element 21 (+42) read 0 instead of 2 and the
                // keeper could NEVER cleanly claim a penalty save (always deflected
                // loose → attacker taps into an open goal). Verified vs
                // original-amiga-swos.asm:35574-35604 (sField_18) + swos.asm:246164.
                1, 0, 0, 4, 176, 0, 0, 4, 0, 1,
                7, 0, 7, 0, 7, 0, 7, 0, 7, 0, 7, 0, 7, 0, 7, 0,
                7, 0, 7, 0, 7, 0, 7, 0, 7, 0, 7, 0, 7, 0, 7, 0,   // 16× 7
                2, 0, 0, 0, 14, 0, 13, 0, 3, 0, 10, 0, 5, 0, 1, 0, 8, 0,
            };
            for (int i = 0; i < penaltyDiveRow.Length; i++)
                WriteByte(Addr.dseg_17EECC + i, penaltyDiveRow[i]);
        }

        // ---- player.cpp skill-indexed tables (swos.asm:245768-245813) ---------
        // All are mechanical copies of the asm DATA declarations consumed by
        // PlayerActions/PlayerControlled paths. Without these, the lookups
        // return zero — silently making every player play at "skill 0" stats
        // (zero passing speed, zero finishing offset, etc.). Earlier audit
        // (kPlayerWithBallOffsets fix at L1566) revealed the same class of bug.

        // kPlAvgTacklingBallControlDiffChance — swos.asm:245813.
        // `db 16, 17, 18, 19, 20, 21, 22, 23` (8 bytes). Indexed by skill diff
        // (0..7) in CalculateIfPlayerWinsBall (swos.asm:108137-108143). RNG
        // 0..31 compared to byte at index: if RNG < threshold, ball won.
        byte[] kPlAvgTacklingBallControlDiffChance_values = { 16, 17, 18, 19, 20, 21, 22, 23 };
        for (int i = 0; i < kPlAvgTacklingBallControlDiffChance_values.Length; i++)
            WriteByte(Addr.kPlAvgTacklingBallControlDiffChance + i,
                      kPlAvgTacklingBallControlDiffChance_values[i]);

        // kBallSpeedDeltaWhenControlled — swos.asm:245808.
        // `dw 130, 116, 102, 88, 74, 60, 46, 32` (8 words). Indexed by
        // ballControl skill; ball-speed offset added on odd frames so ball
        // "runs away" less from better players. CalculateIfPlayerWinsBall+314.
        short[] kBallSpeedDeltaWhenControlled_values =
            { 130, 116, 102, 88, 74, 60, 46, 32 };
        for (int i = 0; i < kBallSpeedDeltaWhenControlled_values.Length; i++)
            WriteWord(Addr.kBallSpeedDeltaWhenControlled + i * 2,
                      kBallSpeedDeltaWhenControlled_values[i]);

        // dseg_17E276 — swos.asm:245800.
        // `dw 4, 5, 6, 8, 11, 14, 17, 21` (8 words). Indexed by ballControl.
        // Turn-dribble tolerance: number of consecutive ticks the controlling
        // player may turn (controlledPlDirection != sprite.direction) before
        // the ball breaks loose (CalculateIfPlayerWinsBall writes
        // wonTheBallTimer = 8; player.cpp:704-741, swos.asm:108290-108310).
        // Was declared but never seeded — all-zero table made dribblers lose
        // the ball on the FIRST turning tick (2026-07-02 duel ping-pong fix).
        short[] dseg_17E276_values = { 4, 5, 6, 8, 11, 14, 17, 21 };
        for (int i = 0; i < dseg_17E276_values.Length; i++)
            WriteWord(Addr.dseg_17E276 + i * 2, dseg_17E276_values[i]);

        // kBallSpeedFinishing — swos.asm:245805.
        // `dw 65248, 65376, 65504, 96, 224, 352, 480, 608` (8 words). First
        // four are signed-negative when interpreted as int16 (-288, -160, -32,
        // 0...). Indexed by finishing skill; offsets player kick speed in
        // PlayerKickingBall+1EA.
        short[] kBallSpeedFinishing_values =
            { -288, -160, -32, 96, 224, 352, 480, 608 };
        for (int i = 0; i < kBallSpeedFinishing_values.Length; i++)
            WriteWord(Addr.kBallSpeedFinishing + i * 2,
                      kBallSpeedFinishing_values[i]);

        // kBallSpeedKicking — swos.asm:245798.
        // `dw 65152, 65266, 65374, 65482, 54, 162, 270, 384` (8 words).
        // Indexed by shooting skill. PlayerKickingBall+244.
        short[] kBallSpeedKicking_values =
            { -384, -270, -162, -54, 54, 162, 270, 384 };
        for (int i = 0; i < kBallSpeedKicking_values.Length; i++)
            WriteWord(Addr.kBallSpeedKicking + i * 2,
                      kBallSpeedKicking_values[i]);

        // kStaticHeaderBallSpeed — swos.asm:245900 `dw 1792` (=7.0 Q8.8).
        // Read by PlayerHittingStaticHeader+1D0 to fill ballSprite.speed
        // on a standing-header kick.
        WriteWord(Addr.kStaticHeaderBallSpeed, 1792);

        // kPlayerHeaderSpeedIncrease — swos.asm:245768.
        // `dw -336, -288, -240, -192, -144, -96, -48, 0` (first 8 words —
        // remaining values in the asm declaration are read by other consumers
        // beyond the 0..7 heading skill index used here). Indexed by heading
        // skill (PlayerHittingJumpHeader/StaticHeader).
        short[] kPlayerHeaderSpeedIncrease_values =
            { -336, -288, -240, -192, -144, -96, -48, 0 };
        for (int i = 0; i < kPlayerHeaderSpeedIncrease_values.Length; i++)
            WriteWord(Addr.kPlayerHeaderSpeedIncrease + i * 2,
                      kPlayerHeaderSpeedIncrease_values[i]);

        // kHeaderLowJumpHeight / kHeaderHighJumpHeight — swos.asm:245772-245774.
        // `dd 20000h` (=2.0 Q16.16) for flying headers (DoFlyingHeader);
        // `dd 24000h` (=2.25 Q16.16) for lob headers (DoLobHeader).
        WriteDword(Addr.kHeaderLowJumpHeight, 0x20000);
        WriteDword(Addr.kHeaderHighJumpHeight, 0x24000);

        // kAIFailedPassChance — swos.asm:245802.
        // `dw 6, 4, 3, 2, 1, 0, 0, 0` (8 words). Indexed by passing skill;
        // compared to random 0..15 timer sample. If sample >= threshold,
        // pass is botched. DoPass+E4.
        short[] kAIFailedPassChance_values = { 6, 4, 3, 2, 1, 0, 0, 0 };
        for (int i = 0; i < kAIFailedPassChance_values.Length; i++)
            WriteWord(Addr.kAIFailedPassChance + i * 2,
                      kAIFailedPassChance_values[i]);

        // kPassingSpeed* — swos.asm:245779-245794. Eight individual word
        // constants for the distance-bucketed pass speed in DoPass.
        WriteWord(Addr.kPassingSpeedCloserThan2500,    1536);  // =6.0
        WriteWord(Addr.kPassingSpeed_2500_10000,       1664);  // =6.5
        WriteWord(Addr.kPassingSpeed_10000_22500,      1792);  // =7.0
        WriteWord(Addr.kPassingSpeed_22500_40000,      1877);  // =7.33
        WriteWord(Addr.kPassingSpeed_40000_62500,      1962);  // =7.66
        WriteWord(Addr.kPassingSpeed_62500_90000,      2048);  // =8.0
        WriteWord(Addr.kPassingSpeed_90000_122500,     2133);  // =8.33
        WriteWord(Addr.kPassingSpeedFurtherThan122500, 2218);  // =8.66

        // kBallSpeedPassingIncrease — swos.asm:245776.
        // `dw 0, 48, 96, 144, 192, 256, 320, 384` (8 words). Indexed by
        // passing skill; ball-speed bump added after the pass. DoPass+464.
        short[] kBallSpeedPassingIncrease_values =
            { 0, 48, 96, 144, 192, 256, 320, 384 };
        for (int i = 0; i < kBallSpeedPassingIncrease_values.Length; i++)
            WriteWord(Addr.kBallSpeedPassingIncrease + i * 2,
                      kBallSpeedPassingIncrease_values[i]);

        // kFreePassReleasingBallSpeed — swos.asm:245796 `dw 1792` (=7.0).
        // Speed assigned to passer's sprite after a free-pass. DoPass+52C.
        WriteWord(Addr.kFreePassReleasingBallSpeed, 1792);

        // ---- Ball-destination delta tables (swos.asm:245580-245598) -----------
        // Seven 16-word (8 directions × dx, dy) tables consulted by
        // getBallDestCoordinatesTable (ball.cpp:4051+) to pick the per-direction
        // delta added to ball.x/y when computing a destination for set pieces.
        // All values are signed words in pitch units (×1000 for full-pitch).
        // Without these, the AI/player set-piece kicks all aim at (0, 0) of
        // pitch — visible as ball flying off-pitch every throw-in/corner.

        // kLeftThrowInBallDestDelta — swos.asm:245580.
        short[] kLeftThrowInBallDestDelta_values = {
              250, -1000,  1000, -1000,  1000,    0,  1000, 1000,
              250,  1000, -1000,  1000, -1000,    0, -1000, -1000,
        };
        for (int i = 0; i < kLeftThrowInBallDestDelta_values.Length; i++)
            WriteWord(Addr.kLeftThrowInBallDestDelta + i * 2,
                      kLeftThrowInBallDestDelta_values[i]);

        // kRightThrowInBallDestDelta — swos.asm:245583.
        short[] kRightThrowInBallDestDelta_values = {
             -250, -1000,  1000, -1000,  1000,    0,  1000, 1000,
             -250,  1000, -1000,  1000, -1000,    0, -1000, -1000,
        };
        for (int i = 0; i < kRightThrowInBallDestDelta_values.Length; i++)
            WriteWord(Addr.kRightThrowInBallDestDelta + i * 2,
                      kRightThrowInBallDestDelta_values[i]);

        // kPenaltyBallDestDelta — swos.asm:245586.
        short[] kPenaltyBallDestDelta_values = {
                0, -1000,   500, -1000,  1000,    0,   500, 1000,
                0,  1000,  -500,  1000, -1000,    0,  -500, -1000,
        };
        for (int i = 0; i < kPenaltyBallDestDelta_values.Length; i++)
            WriteWord(Addr.kPenaltyBallDestDelta + i * 2,
                      kPenaltyBallDestDelta_values[i]);

        // kUpperLeftCornerBallDestDelta — swos.asm:245589.
        short[] kUpperLeftCornerBallDestDelta_values = {
                0, -1000,  1000, -1000,  1000,  150,  1000,  300,
              250,  1000, -1000,  1000, -1000,    0, -1000, -1000,
        };
        for (int i = 0; i < kUpperLeftCornerBallDestDelta_values.Length; i++)
            WriteWord(Addr.kUpperLeftCornerBallDestDelta + i * 2,
                      kUpperLeftCornerBallDestDelta_values[i]);

        // kUpperRightCornerBallDestDelta — swos.asm:245592.
        short[] kUpperRightCornerBallDestDelta_values = {
                0, -1000,  1000, -1000,  1000,    0,  1000, 1000,
             -250,  1000, -1000,   350, -1000,  150, -1000, -1000,
        };
        for (int i = 0; i < kUpperRightCornerBallDestDelta_values.Length; i++)
            WriteWord(Addr.kUpperRightCornerBallDestDelta + i * 2,
                      kUpperRightCornerBallDestDelta_values[i]);

        // kLowerLeftCornerBallDestDelta — swos.asm:245595.
        short[] kLowerLeftCornerBallDestDelta_values = {
              250, -1000,  1000,  -350,  1000, -150,  1000, 1000,
                0,  1000, -1000,  1000, -1000,    0, -1000, -1000,
        };
        for (int i = 0; i < kLowerLeftCornerBallDestDelta_values.Length; i++)
            WriteWord(Addr.kLowerLeftCornerBallDestDelta + i * 2,
                      kLowerLeftCornerBallDestDelta_values[i]);

        // kLowerRightCornerBallDestDelta — swos.asm:245598. Declared in the
        // asm as `db` byte-pairs; decoded as signed LE-words here.
        short[] kLowerRightCornerBallDestDelta_values = {
             -250, -1000,  1000, -1000,  1000,    0,  1000, 1000,
                0,  1000, -1000,  1000, -1000, -150, -1000, -350,
        };
        for (int i = 0; i < kLowerRightCornerBallDestDelta_values.Length; i++)
            WriteWord(Addr.kLowerRightCornerBallDestDelta + i * 2,
                      kLowerRightCornerBallDestDelta_values[i]);

        // kDefaultDestinations — swos.asm:245575. 8 directions × 2 words (dx, dy).
        // dir 0=N, 1=NE, 2=E, 3=SE, 4=S, 5=SW, 6=W, 7=NW.
        short[] defaultDests = {
                0, -1000,    // dir 0 (N)
             1000, -1000,    // dir 1 (NE)
             1000,     0,    // dir 2 (E)
             1000,  1000,    // dir 3 (SE)
                0,  1000,    // dir 4 (S)
            -1000,  1000,    // dir 5 (SW)
            -1000,     0,    // dir 6 (W)
            -1000, -1000,    // dir 7 (NW)
        };
        for (int i = 0; i < defaultDests.Length; i++)
            WriteWord(Addr.kDefaultDestinations + i * 2, defaultDests[i]);

        // updatePlayers.cpp:10658 / 10912 — pristine defaults.
        WriteWord(Addr.ballDefensiveX, 0);
        WriteWord(Addr.ballNotHighZ, 0);
        WriteWord(Addr.goalkeeperDiveDeadVar, 0);

        // Fouls / cards — defaults match game.cpp:170 / gameLoop.cpp:416.
        WriteWord(Addr.cardsDisallowed, 0);
        WriteWord(Addr.playerCardChance, 0);
        WriteWord(Addr.plg_D3_param, 0);
        WriteWord(Addr.team1NumAllowedInjuries, 4);
        WriteWord(Addr.team2NumAllowedInjuries, 4);
        WriteDword(Addr.lastTackleNearestTeammate, 0);

        // inGameTeamPlayerOffsets — byte offset from the InGameTeam base to each
        // player record. The original table (swos.asm:208198) is
        // `dw 0,61,122,...` — index × 61 (sizeof(PlayerGame) = 0x3D = 61), NOT
        // 54. The earlier 54 was wrong and scattered PlayerTackled's injury/card
        // writes across neighbouring 61-byte records. Only on-pitch ordinals
        // 1..11 (index 0..10) reach the consumers, so 11 entries suffice.
        for (int i = 0; i < 11; i++)
            WriteWord(Addr.inGameTeamPlayerOffsets + i * 2, (short)(i * 61));

        // dseg_17E3EE / dseg_17E3F3 — 5-byte yellow-card / red-card progression
        // tables (swos-port pulls these out of the data segment). The exact
        // values aren't published in any header reachable from here; mechanical
        // port preserves the cmp/lookup pattern but uses 0-init bytes for now.
        // Booking still functions because the lookup is overwritten on top of
        // the existing PlayerGameHeader.cards; the table is only consulted when
        // plg_D3_param == 0 (AI control path).
        for (int i = 0; i < 5; i++) WriteByte(Addr.dseg_17E3EE + i, 0);
        for (int i = 0; i < 5; i++) WriteByte(Addr.dseg_17E3F3 + i, 0);

        // playerTackled injury tables — swos.asm:245826-245837. Byte-literal
        // copies of the m68k DATA declarations consulted by the injury-roll
        // branch in updatePlayers.cpp:14411-14823.
        byte[] kInjuryLevels = { 42, 7, 5, 4, 3, 2, 1 };
        byte[] kInjuryLevelAlreadyInjured = { 14, 15, 12, 9, 7, 5, 2 };
        short[] dseg_17E2EC = { 0, 60, 70, 80, 90, 100, 110, 130 };
        byte[] kTackleInjuryProbability = { 48, 28, 20, 14 };
        byte[] kTackleInjuryProbabilityAlreadyInjured = { 96, 57, 41, 28 };
        for (int i = 0; i < kInjuryLevels.Length; i++)
            WriteByte(Addr.kInjuryLevels + i, kInjuryLevels[i]);
        for (int i = 0; i < kInjuryLevelAlreadyInjured.Length; i++)
            WriteByte(Addr.kInjuryLevelAlreadyInjured + i, kInjuryLevelAlreadyInjured[i]);
        for (int i = 0; i < dseg_17E2EC.Length; i++)
            WriteWord(Addr.dseg_17E2EC + i * 2, dseg_17E2EC[i]);
        for (int i = 0; i < kTackleInjuryProbability.Length; i++)
            WriteByte(Addr.kTackleInjuryProbability + i, kTackleInjuryProbability[i]);
        for (int i = 0; i < kTackleInjuryProbabilityAlreadyInjured.Length; i++)
            WriteByte(Addr.kTackleInjuryProbabilityAlreadyInjured + i, kTackleInjuryProbabilityAlreadyInjured[i]);

        // AI helper globals — all pristine 0. AI_attackHalf is set to a non-zero
        // half (1/2) by AI_DecideWhetherToTriggerFire on the first successful
        // trigger; defaulting to 0 means the first
        // AI_SetDirectionTowardOpponentsGoal call falls into the "no match"
        // branch (jnz @@out) until the AI is armed.
        WriteWord(Addr.AI_counter, 0);
        WriteWord(Addr.AI_attackHalf, 0);
        WriteWord(Addr.AI_counterWriteOnly, 0);
        WriteWord(Addr.deadVarAlways0, 0);
        WriteDword(Addr.dseg_1309C1, 0);

        // Player sprite pool + per-team SpritesTable initialization. Must run
        // BEFORE TeamData.Init (which reads PlayerSprite.Team1/2TableBase
        // addresses to set `players` field).
        PlayerSprite.Init();

        // Animation tables (swos.asm:218368-219641) + frame-indices arrays.
        // Must run before SetPlayerAnimationTable can be called (i.e. before
        // any port-side code that handles a state transition). Wires up
        // 17 PlayerAnimationTable structs at Memory.Addr.k*AnimTableAddr.
        AnimationTablesData.Init();

        // TeamData cross-pointers (opponentsTeam) + per-team players pointers + safe defaults.
        TeamData.Init();

        // gameControls.cpp:33-46 — resetGameControls() defaults. Array.Clear above
        // already zeroed everything; explicit writes here document the contract.
        WriteDword(Addr.ic_pl1FireCounter, 0);
        WriteDword(Addr.ic_pl2FireCounter, 0);
        WriteByte(Addr.ic_pl1LastFired, 0);
        WriteByte(Addr.ic_pl2LastFired, 0);
        WriteDword(Addr.ic_oldPl1Events, 0);   // kNoGameEvents
        WriteDword(Addr.ic_oldPl2Events, 0);
        WriteDword(Addr.ic_pl1LastVertical, 0);
        WriteDword(Addr.ic_pl1LastHorizontal, 0);
        WriteDword(Addr.ic_pl2LastVertical, 0);
        WriteDword(Addr.ic_pl2LastHorizontal, 0);
        WriteDword(Addr.ic_pl1Events, 0);
        WriteDword(Addr.ic_pl2Events, 0);
        WriteWord(Addr.ic_pl1Fire, 0);
        WriteWord(Addr.ic_pl2Fire, 0);

        // PlayerHeader port — initialise constants used by
        // playerAttemptingJumpHeader / attemptStaticHeader /
        // setPlayerWithNoBallDestination (updatePlayers.cpp:15253/15803/15380).
        WriteWord(Addr.kStaticHeaderPlayerSpeed, 256);          // swos.asm:245903
        WriteWord(Addr.m_playerDownHeadingInterval, 55);        // updatePlayers.cpp:12
        WriteWord(Addr.m_playerDownTacklingInterval, 55);       // updatePlayers.cpp:11
        // updatePlayers.cpp:9-10 — PC defaults for the post-result intervals.
        // Without these, AiBrain's stoppage-tick comparison reads 0 → fires
        // every tick. amigaMode.cpp:32-33,50-51 overrides via the PortTuning
        // setters (Amiga 600/350, PC 660/385); OpenSWOS hard-locks PC.
        WriteWord(Addr.m_clearResultInterval, 660);             // updatePlayers.cpp:9
        WriteWord(Addr.m_clearResultHalftimeInterval, 385);     // updatePlayers.cpp:10

        // playerXQuadrantsCoordinates (swos.asm:245750) — 15 words.
        short[] xQuadCoords = {
            98, 132, 166, 200, 234, 268, 302, 336,
            370, 404, 438, 472, 506, 540, 574,
        };
        for (int i = 0; i < xQuadCoords.Length; i++)
            WriteWord(Addr.playerXQuadrantsCoordinates + i * 2, xQuadCoords[i]);

        // playerYQuadrantCoordinates (swos.asm:245753) — 16 words.
        short[] yQuadCoords = {
            149, 189, 229, 269, 309, 349, 389, 429,
            469, 509, 549, 589, 629, 669, 709, 749,
        };
        for (int i = 0; i < yQuadCoords.Length; i++)
            WriteWord(Addr.playerYQuadrantCoordinates + i * 2, yQuadCoords[i]);

        // g_tacticsTable (swos.asm:209342) — 19 dword pointers, each pointing
        // at a 370-byte TeamTactics struct inside teamTacticsPool. Bytes
        // inside the pool stay zeroed until the tactics loader lands.
        for (int i = 0; i < 19; i++)
            WriteDword(Addr.g_tacticsTable + i * 4, Addr.teamTacticsPool + i * 370);

        // dseg_17DEF4 (swos.asm:245601) — 28 × word ball-Z height table read
        // by DoGoalkeeperSprites during the "diving-right" branch. Verbatim
        // from the asm; do NOT massage.
        short[] dseg_17DEF4_values = {
            7, 13, 11, 8, 5, 2, 20, 2, 5, 8, 11, 13, 7, 20,
            5, 11, 9, 6, 3, 0, 20, 0, 3, 6, 9, 11, 5, 20,
        };
        for (int i = 0; i < dseg_17DEF4_values.Length; i++)
            WriteWord(Addr.dseg_17DEF4 + i * 2, dseg_17DEF4_values[i]);

        // kGoalKeeperClaimingBallHeight (swos.asm:245604) — 7 × word.
        // Indexed by sprite.frameSwitchCounter * 2 in the asm
        // ("diving-left" branch of DoGoalkeeperSprites). Verbatim values.
        short[] kGoalKeeperClaimingBallHeight_values = {
            17, 17, 17, 10, 8, 5, 255,
        };
        for (int i = 0; i < kGoalKeeperClaimingBallHeight_values.Length; i++)
            WriteWord(Addr.kGoalKeeperClaimingBallHeight + i * 2,
                      kGoalKeeperClaimingBallHeight_values[i]);

        // Goal-sprite image-index slots start at -1 (no image). initGoalSprites
        // (gameLoop.cpp:1911-1915) called per-frame sets them to the constant
        // sprite-IDs (1205/1206); kept as -1 here so a fresh boot before the
        // first CoreGameUpdate doesn't paint goal posts.
        WriteWord(Addr.goal1TopSprite_ImageIndex, -1);
        WriteWord(Addr.goal2BottomSprite_ImageIndex, -1);

        // playerMarkSprite — initial imageIndex -1 (hidden). markPlayer
        // (gameLoop.cpp:1919-2010) overwrites every tick when the marked-player
        // condition holds; otherwise stays at -1.
        WriteWord(Addr.playerMarkSprite_ImageIndex, -1);
        WriteWord(Addr.playerMarkSprite_XWhole, 0);
        WriteWord(Addr.playerMarkSprite_YWhole, 0);
        WriteWord(Addr.playerMarkSprite_ZWhole, 0);

        // Display-sprite dirty flag starts clean; producers (initDisplaySprites
        // wire-through) set it, host renderer clears it after consumption.
        WriteWord(Addr.displaySpritesDirtyFlag, 0);

        // Deterministic RNG seed. Tied to currentGameTick so each match
        // restart picks a reproducible byte stream (zero at boot). Lockstep
        // netplay relies on this being identical on both peers.
        Rng.Reseed(ReadWord(Addr.currentGameTick));

        initialised = true;
    }

    public static bool IsInitialised => initialised;

    // ---- Read helpers ------------------------------------------------------
    // Little-endian (x86 PC native). swos-port's `readMemory(esi+44, 2)` and
    // `*(word *)&g_memByte[xxx]` both do LE reads.

    public static byte ReadByte(int addr) => mem[addr];

    // Unsigned word (16-bit). swos-port `word` type.
    public static ushort ReadWord(int addr) =>
        (ushort)(mem[addr] | (mem[addr + 1] << 8));

    // Signed word — for SWOS `int16_t` reads (motion deltas etc.).
    public static short ReadSignedWord(int addr) => (short)ReadWord(addr);

    // Dword (32-bit). swos-port `dword`.
    public static uint ReadDword(int addr) =>
        (uint)(mem[addr]
              | (mem[addr + 1] << 8)
              | (mem[addr + 2] << 16)
              | (mem[addr + 3] << 24));

    public static int ReadSignedDword(int addr) => (int)ReadDword(addr);

    // ---- Write helpers -----------------------------------------------------

    public static void WriteByte(int addr, int value) => mem[addr] = (byte)value;

    public static void WriteWord(int addr, int value)
    {
        mem[addr]     = (byte)(value & 0xFF);
        mem[addr + 1] = (byte)((value >> 8) & 0xFF);
    }

    public static void WriteDword(int addr, int value)
    {
        mem[addr]     = (byte)(value & 0xFF);
        mem[addr + 1] = (byte)((value >> 8) & 0xFF);
        mem[addr + 2] = (byte)((value >> 16) & 0xFF);
        mem[addr + 3] = (byte)((value >> 24) & 0xFF);
    }

    // ---- Direct backing access (for debugging / asserts) -------------------

    internal static System.ReadOnlySpan<byte> View(int addr, int length)
        => mem.AsSpan(addr, length);
}
