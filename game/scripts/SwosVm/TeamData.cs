namespace OpenSwos.SwosVm;

// Per-team runtime data — mirrors swos-port `swos.topTeamData` / `swos.bottomTeamData`.
// Full TeamGeneralInfo struct (swos.h:331) has 100+ fields; we add them as each
// port pulls them in. Field OFFSETS below are verified against asm-translated
// readMemory(esi + N, …) calls in ball.cpp and updatePlayers.cpp.
public static class TeamData
{
    // Allocate 512 bytes per team. swos-port TeamGeneralInfo is ~400 bytes
    // (varies by config). Top and bottom team data live consecutively in memory.
    public const int TopBase    = 0x4F900;
    public const int BottomBase = 0x4FB00;

    // ---- Field offsets within TeamGeneralInfo (verified from ball.cpp +
    // ---- struct definition in swos-port src/swos/swos.h:331-405) -----------
    // ball.cpp:2258 `eax = readMemory(esi, 4)` — opponentsTeam pointer at +0.
    public const int OffOpponentsTeam              = 0;     // dword — absolute address of opponent's TeamData
    public const int OffPlayerNumber               = 4;     // word — set to 1/2 for human-controlled team
    public const int OffPlayerCoachNumber          = 6;     // word
    public const int OffIsPlCoach                  = 8;     // word
    public const int OffInGameTeamPtr              = 10;    // dword
    public const int OffTeamStatsPtr               = 14;    // dword
    public const int OffTeamNumber                 = 18;    // word
    public const int OffPlayers                    = 20;    // dword — pointer to per-team SpritesTable (11 × 4 bytes)
    public const int OffShotChanceTable            = 24;    // dword — pointer to per-team chance table (used by shouldGoalkeeperDive)
    public const int OffTactics                    = 28;    // word
    public const int OffUpdatePlayerIndex          = 30;    // word
    // swos.h:344 — controlledPlayer SwosDataPointer<Sprite>. dword at +32.
    public const int OffControlledPlayer           = 32;    // dword — pointer to controlled player Sprite
    public const int OffPassToPlayerPtr            = 36;    // dword
    // ball.cpp:242 `g_memByte[522832]` = topTeamData.playerHasBall (swos.h:346, +40)
    public const int OffPlayerHasBall              = 40;    // word — non-zero = this team's player controls ball
    public const int OffAllowedDirections          = 42;    // word
    // ball.cpp:2456 `readMemory(esi+44, 2)` — currentAllowedDirection.
    public const int OffCurrentAllowedDirection    = 44;    // word — LIVE held/joystick direction this frame (or -1 = no spin allowed)
    // ball.cpp:2333 `readMemory(esi+56, 2)` — controlledPlDirection.
    public const int OffControlledPlDirection      = 56;    // word — direction recorded AT kick time (0..7)
    public const int OffDirection                  = 46;    // word
    public const int OffQuickFire                  = 48;    // byte
    public const int OffNormalFire                 = 49;    // byte
    public const int OffFirePressed                = 50;    // byte
    public const int OffFireThisFrame              = 51;    // byte
    public const int OffHeaderOrTackle             = 52;    // word
    public const int OffFireCounter                = 54;    // word
    public const int OffShooting                   = 58;    // word
    // ball.cpp:2261 `readMemory(esi+76, 2)` — goalkeeperSavedCommentTimer.
    public const int OffGoalkeeperSavedCommentTimer = 76;   // word — < 0 disables spin
    public const int OffLastHeadingPlayer          = 72;    // dword
    public const int OffOfs78                      = 78;    // word
    // updatePlayers.cpp:10829+ `goalkeeperJumping` — goalkeeperDiving{Right,Left} at +80/+82
    public const int OffGoalkeeperDivingRight      = 80;    // word — set by goalkeeperJumping (NEW per 2026-05-23 research)
    public const int OffGoalkeeperDivingLeft       = 82;    // word — set by goalkeeperJumping
    public const int OffBallOutOfPlayOrKeeper      = 84;    // word — set by goalkeeperClaimedTheBall
    public const int OffGoaliePlayingOrOut         = 86;    // word
    public const int OffPassingBall                = 88;    // word
    public const int OffPassingToPlayer            = 90;    // word
    public const int OffPlayerSwitchTimer          = 92;    // word
    public const int OffBallInPlay                 = 94;    // word
    public const int OffBallOutOfPlay              = 96;    // word
    public const int OffBallX                      = 98;    // word
    public const int OffBallY                      = 100;   // word
    public const int OffPassKickTimer              = 102;   // word
    public const int OffPassingKickingPlayer       = 104;   // dword
    public const int OffOfs108                     = 108;   // word
    public const int OffBallCanBeControlled        = 110;   // word
    public const int OffBallControllingPlayerDirection = 112; // word
    // ball.cpp:2294 `readMemory(esi+118, 2)` — spinTimer.
    public const int OffSpinTimer                  = 118;   // word — -1 = no spin, else 0..10 ticks since touch
    public const int OffLeftSpin                   = 120;   // word — 1 if left-spinning
    public const int OffRightSpin                  = 122;   // word — 1 if right-spinning
    public const int OffLongPass                   = 124;   // word — 1 if long pass active
    public const int OffLongSpinPass               = 126;   // word — 1 if long+spin pass
    public const int OffPassInProgress             = 128;   // word — non-zero → take passing path in applyBallAfterTouch
    public const int OffAITimer                    = 130;   // word
    // updatePlayers.cpp:15980+ (AI_SetControlsDirection). field_84 — chase/regroup counter.
    public const int OffAiField84                  = 132;   // word
    // updatePlayers.cpp:18210/18223/18675 — AI after-touch strength (0/1/2).
    public const int OffAiAfterTouchStrength       = 134;   // word
    // updatePlayers.cpp:18431/18506/18823 — AI ball-spin direction (-1/0/+1).
    public const int OffAiBallSpinDirection        = 136;   // word
    public const int OffGoalkeeperPlaying          = 140;   // word
    public const int OffResetControls              = 142;   // word
    public const int OffSecondaryFire              = 144;   // byte (last field; total struct size = 145)

    // updatePlayers.cpp:17289 — plVeryCloseToBall byte flag (offset +61). Set by
    // updatePlayers' per-player loop when at least one outfielder is < ~200 px from ball.
    public const int OffPlVeryCloseToBall          = 61;    // byte
    // updatePlayers.cpp:17297 — plCloseToBall byte (offset +62). Looser threshold.
    public const int OffPlCloseToBall              = 62;    // byte
    // updatePlayers.cpp:2832 — ball12To17 byte (offset +67): ball height in (12..17] px.
    public const int OffBall12To17                 = 67;    // byte
    // updatePlayers.cpp:2809 — ballAbove17 byte (offset +68): ball height > 17 px.
    public const int OffBallAbove17                = 68;    // byte

    // ---- Per-team accessors -------------------------------------------------

    public static int Base(bool top) => top ? TopBase : BottomBase;

    public static int  OpponentsTeam(bool top)
        => Memory.ReadSignedDword(Base(top) + OffOpponentsTeam);

    public static int   ControlledPlayer(bool top)
        => Memory.ReadSignedDword(Base(top) + OffControlledPlayer);
    public static void  SetControlledPlayer(bool top, int spritePtr)
        => Memory.WriteDword(Base(top) + OffControlledPlayer, spritePtr);

    // Read controlledPlayer pointer from a TeamData address (used when the
    // port branches via lastTeamPlayedBeforeBreak — pointer to either top or
    // bottom TeamData).
    public static int  ControlledPlayerFromBase(int teamDataBase)
        => Memory.ReadSignedDword(teamDataBase + OffControlledPlayer);

    public static short PlayerHasBall(bool top)
        => Memory.ReadSignedWord(Base(top) + OffPlayerHasBall);
    public static void  SetPlayerHasBall(bool top, int v)
        => Memory.WriteWord(Base(top) + OffPlayerHasBall, v);

    public static short CurrentAllowedDirection(bool top)
        => Memory.ReadSignedWord(Base(top) + OffCurrentAllowedDirection);
    public static void  SetCurrentAllowedDirection(bool top, int v)
        => Memory.WriteWord(Base(top) + OffCurrentAllowedDirection, v);

    public static short ControlledPlDirection(bool top)
        => Memory.ReadSignedWord(Base(top) + OffControlledPlDirection);
    public static void  SetControlledPlDirection(bool top, int v)
        => Memory.WriteWord(Base(top) + OffControlledPlDirection, v);

    public static short GoalkeeperSavedCommentTimer(bool top)
        => Memory.ReadSignedWord(Base(top) + OffGoalkeeperSavedCommentTimer);
    public static void  SetGoalkeeperSavedCommentTimer(bool top, int v)
        => Memory.WriteWord(Base(top) + OffGoalkeeperSavedCommentTimer, v);

    public static short GetSpinTimer(bool top)
        => Memory.ReadSignedWord(Base(top) + OffSpinTimer);
    public static void  SetSpinTimer(bool top, int v)
        => Memory.WriteWord(Base(top) + OffSpinTimer, v);

    public static short LeftSpin(bool top)
        => Memory.ReadSignedWord(Base(top) + OffLeftSpin);
    public static void  SetLeftSpin(bool top, int v)
        => Memory.WriteWord(Base(top) + OffLeftSpin, v);

    public static short RightSpin(bool top)
        => Memory.ReadSignedWord(Base(top) + OffRightSpin);
    public static void  SetRightSpin(bool top, int v)
        => Memory.WriteWord(Base(top) + OffRightSpin, v);

    public static short LongPass(bool top)
        => Memory.ReadSignedWord(Base(top) + OffLongPass);
    public static void  SetLongPass(bool top, int v)
        => Memory.WriteWord(Base(top) + OffLongPass, v);

    public static short LongSpinPass(bool top)
        => Memory.ReadSignedWord(Base(top) + OffLongSpinPass);
    public static void  SetLongSpinPass(bool top, int v)
        => Memory.WriteWord(Base(top) + OffLongSpinPass, v);

    public static short PassInProgress(bool top)
        => Memory.ReadSignedWord(Base(top) + OffPassInProgress);
    public static void  SetPassInProgress(bool top, int v)
        => Memory.WriteWord(Base(top) + OffPassInProgress, v);

    // Goalkeeper diving state — set by goalkeeperJumping (updatePlayers.cpp:10829+).
    public static short GoalkeeperDivingRight(bool top)
        => Memory.ReadSignedWord(Base(top) + OffGoalkeeperDivingRight);
    public static void  SetGoalkeeperDivingRight(bool top, int v)
        => Memory.WriteWord(Base(top) + OffGoalkeeperDivingRight, v);

    public static short GoalkeeperDivingLeft(bool top)
        => Memory.ReadSignedWord(Base(top) + OffGoalkeeperDivingLeft);
    public static void  SetGoalkeeperDivingLeft(bool top, int v)
        => Memory.WriteWord(Base(top) + OffGoalkeeperDivingLeft, v);

    public static short BallOutOfPlayOrKeeper(bool top)
        => Memory.ReadSignedWord(Base(top) + OffBallOutOfPlayOrKeeper);
    public static void  SetBallOutOfPlayOrKeeper(bool top, int v)
        => Memory.WriteWord(Base(top) + OffBallOutOfPlayOrKeeper, v);

    // Pointer to per-team SpritesTable (11 × 4-byte sprite slot addresses).
    // Initialized by TeamData.Init() to point at PlayerSprite.Team1TableBase
    // or Team2TableBase.
    public static int   PlayersTable(bool top)
        => Memory.ReadSignedDword(Base(top) + OffPlayers);
    public static void  SetPlayersTable(bool top, int v)
        => Memory.WriteDword(Base(top) + OffPlayers, v);

    // Get the sprite slot address for slot index 0..10 of this team.
    // Reads via the players table — matches asm idiom `mov esi, [A1+i*4]`.
    public static int   GetTeamSpriteAddr(bool top, int slotInTeam)
    {
        int tableAddr = PlayersTable(top);
        return Memory.ReadSignedDword(tableAddr + slotInTeam * 4);
    }

    // ---- Initialization (called from Memory.Init) --------------------------
    // Sets up opponentsTeam cross-pointers + per-team sprite tables.
    public static void Init()
    {
        Memory.WriteDword(TopBase + OffOpponentsTeam, BottomBase);
        Memory.WriteDword(BottomBase + OffOpponentsTeam, TopBase);

        // Players field — points at per-team SpritesTable (populated by PlayerSprite.Init).
        Memory.WriteDword(TopBase + OffPlayers, PlayerSprite.Team1TableBase);
        Memory.WriteDword(BottomBase + OffPlayers, PlayerSprite.Team2TableBase);

        // teamStatsPtr (+14) — wires each team to its TeamStatsData backing.
        // stats.cpp:79-103 dereferences this pointer for the 7-word
        // TeamStatsData record (ballPossession, cornersWon, ...).
        Memory.WriteDword(TopBase + OffTeamStatsPtr, Memory.Addr.topTeamStatsData);
        Memory.WriteDword(BottomBase + OffTeamStatsPtr, Memory.Addr.bottomTeamStatsData);

        // Reasonable defaults — match values are typically refreshed before use.
        SetSpinTimer(true, -1);
        SetSpinTimer(false, -1);
        SetCurrentAllowedDirection(true, -1);
        SetCurrentAllowedDirection(false, -1);
        SetGoalkeeperSavedCommentTimer(true, 0);
        SetGoalkeeperSavedCommentTimer(false, 0);
        SetGoalkeeperDivingRight(true, 0);
        SetGoalkeeperDivingRight(false, 0);
        SetGoalkeeperDivingLeft(true, 0);
        SetGoalkeeperDivingLeft(false, 0);
    }
}
