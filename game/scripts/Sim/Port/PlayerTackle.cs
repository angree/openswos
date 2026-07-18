namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// CS0164: labels carried over from the asm-translated source for control-flow
// parity. Some are entry-only (referenced via fall-through) so the compiler
// can't see incoming gotos. Mirror AiBrain's suppression so the build stays
// warning-free.
#pragma warning disable CS0164

// Player-vs-player tackle / foul / booking logic ported from
// external/swos-port/src/game/updatePlayers/updatePlayers.cpp.
//
// Three top-level statics:
//   - PlayerTacklingTestFoul (updatePlayers.cpp:12708-13394, ~687 LOC)
//   - TryBookingThePlayer    (updatePlayers.cpp:13877-14174, ~298 LOC)
//   - TrySendingOffThePlayer (updatePlayers.cpp:14180-14400, ~221 LOC)
//
// Plus helpers exclusively used by them:
//   - testFoulForPenaltyAndFreeKick (updatePlayers.cpp:13404-13868, ~465 LOC)
//
// **Inputs** are passed explicitly (original used machine registers):
//   - aPlayerSprite (A1): absolute Memory address of the tackler / fouled player.
//   - aBallSprite   (A2): absolute Memory address of the ball sprite. (Note —
//                         most paths overwrite A2 internally with the opponents'
//                         controlledPlayer pointer, mirroring asm.)
//   - aTeamData     (A6): absolute Memory address of the tackler's TeamData.
//
// Mechanical port. Preserves goto labels and magic numbers from the asm
// translation. Stubs out helper callees that are not yet ported (PlayerTackled,
// PlayDangerousPlayComment, PlayFoulWhistleSample, PlayPenaltyComment,
// PlayFoulComment, Rand). External helpers ActivateReferee + StopAllPlayers
// already exist (Referee.ActivateReferee / TeamPort.StopAllPlayers).
public static class PlayerTackle
{
    // swos-port comment: TS_GOOD_TACKLE constant. Reverse-engineered to the
    // value 2 from `mov [esi+Sprite.tackleState], TS_GOOD_TACKLE` in
    // player.cpp:1961 (writeMemory + literal 2).
    private const int TS_GOOD_TACKLE = 2;

    // kTeamGameHeaderSize = offsetof(TeamGame, players) = 42 (swos.h:296-317;
    // 10 kit words + markedPlayer + teamName[17] + 3 pad). In the ORIGINAL,
    // team.inGameTeamPtr points at the TeamGame START, so the injury/card writes
    // here use PlayerGameHeader = TeamGameHeader(42) + PlayerInfo, i.e. field
    // offsets +48 isInjured / +50 fasterTackle / +51 previousCards / +52 cards /
    // +77 injuriesBitfield (all 42 + the PlayerInfo offset). OpenSWOS's port
    // instead points inGameTeamPtr at players[0] (TeamDataLoader.WireTeamFields),
    // so every one of these four sites must subtract the 42-byte header to land
    // on the SAME bytes TeamDataLoader/PlayerActions read as PlayerInfo (+6/+8/
    // +9/+10/+35). Paired with inGameTeamPlayerOffsets = index*61 (swos.asm:
    // 208198). Without this, PlayerTackled scattered injuries into neighbouring
    // players' name/skill bytes (only ordinal 7 landed correctly) and injuries
    // never persisted — the bug this task fixes.
    private const int kTeamGameHeaderSize = 42;

    // Absolute addresses of topTeamData / bottomTeamData in the asm — preserve
    // identity tests so the port branches the same way the original did.
    // Our backing memory is TeamData.TopBase / TeamData.BottomBase, so the
    // ported "cmp A6, offset topTeamData" turns into "if (A6 == TeamData.TopBase)".
    // We name the asm constants here so the source citations stay readable.
    //   offset topTeamData    = 522792  → TeamData.TopBase    (0x4F900 in our VM)
    //   offset bottomTeamData = 522940  → TeamData.BottomBase (0x4FB00 in our VM)

    // ====================================================================
    // playerTacklingTestFoul — updatePlayers.cpp:12708-13394
    // ====================================================================
    //
    // Check if a tackle ends up a foul. Conditions:
    // - opponent has a controlled player (else immediate return).
    // - distance between them is more than 32 units → no foul, return.
    // - else: handle player-very-close / opponents-player-goalkeeper /
    //   foul-conceded / cards / penalty-vs-free-kick.
    //
    // from updatePlayers.cpp:12708
    public static void PlayerTacklingTestFoul(int aPlayerSprite, int aTeamData)
    {
        int A1 = aPlayerSprite;
        int A6 = aTeamData;
        int A2;
        int A0;
        int A4;
        int A5 = 0;
        int esi;

        // 12710-12715 — A2 = opponentsTeam.controlledPlayer.
        esi = A6;
        int eax = Memory.ReadSignedDword(esi + 0);              // [A6+OffOpponentsTeam]
        A2 = eax;
        esi = A2;
        eax = Memory.ReadSignedDword(esi + 32);                 // [opponent + OffControlledPlayer]
        A2 = eax;

        // 12716-12726 — cmp A2, 0 ; jz @@out.
        if (A2 == 0) return;

        // 12728-12747 — D0 = (player.x.whole - opponent.x.whole)^2.
        esi = A1;
        short axS = Memory.ReadSignedWord(esi + 32);            // player.x +2
        int D0_w = axS;
        esi = A2;
        axS = Memory.ReadSignedWord(esi + 32);                  // opponent.x +2
        D0_w = (short)(D0_w - axS);
        int D0 = (short)D0_w * (short)D0_w;                     // imul bx, signed

        // 12748-12767 — D1 = (player.y.whole - opponent.y.whole)^2.
        esi = A1;
        axS = Memory.ReadSignedWord(esi + 36);                  // player.y +2
        int D1_w = axS;
        esi = A2;
        axS = Memory.ReadSignedWord(esi + 36);                  // opponent.y +2
        D1_w = (short)(D1_w - axS);
        int D1 = (short)D1_w * (short)D1_w;

        // 12768-12774 — D0 += D1 (squared distance).
        D0 = D0 + D1;

        // 12775-12785 — cmp D0, 32 ; jbe @@players_very_close
        // jbe = unsigned; the asm uses signed flags but treats this as unsigned.
        // We use uint cast to match.
        if ((uint)D0 <= 32u) goto l_players_very_close;

    l_out:
        return;

    l_play_foul_comment_and_return:
        StubPlayDangerousPlayComment();
        return;

    l_opponents_player_goalkeeper:
        // 12794-12817 — shr speed, 1 twice (speed >>= 2); or speed, 1.
        esi = A1;
        {
            ushort src = Memory.ReadWord(esi + 44);             // player.speed
            src = (ushort)(src >> 1);
            Memory.WriteWord(esi + 44, src);
        }
        {
            ushort src = Memory.ReadWord(esi + 44);
            src = (ushort)(src >> 1);
            Memory.WriteWord(esi + 44, src);
        }
        {
            ushort src = Memory.ReadWord(esi + 44);
            src = (ushort)(src | 1);
            Memory.WriteWord(esi + 44, src);
        }
        return;

    l_players_very_close:
        // 12819-12832 — cmp opponent.playerOrdinal, 1 ; jz l_opponents_player_goalkeeper.
        // (asm has TWO consecutive identical compares; preserve mechanically.)
        esi = A2;
        if ((short)Memory.ReadWord(esi + 2) == 1) goto l_opponents_player_goalkeeper;

        // 12834-12845 — second identical compare; same branch, opposite sense
        // — the asm reads it twice (an artifact of the disassembly). We keep
        // both so the byte-for-byte trace remains parseable.
        if ((short)Memory.ReadWord(esi + 2) == 1) return;

        // 12847-12858 — cmp opponent.x +2, 81 ; jl out.
        if ((short)Memory.ReadWord(esi + 32) < 81) return;

        // 12860-12871 — cmp opponent.y +2, 129 ; jl out.
        if ((short)Memory.ReadWord(esi + 36) < 129) return;

        // 12873-12884 — cmp opponent.x +2, 590 ; jg out.
        if ((short)Memory.ReadWord(esi + 32) > 590) return;

        // 12886-12897 — cmp opponent.y +2, 769 ; jg out.
        if ((short)Memory.ReadWord(esi + 36) > 769) return;

        // 12899-12920 — tackler.speed >>= 2; tackler.speed |= 1.
        esi = A1;
        {
            ushort src = Memory.ReadWord(esi + 44);
            src = (ushort)(src >> 1);
            Memory.WriteWord(esi + 44, src);
        }
        {
            ushort src = Memory.ReadWord(esi + 44);
            src = (ushort)(src >> 1);
            Memory.WriteWord(esi + 44, src);
        }
        {
            ushort src = Memory.ReadWord(esi + 44);
            src = (ushort)(src | 1);
            Memory.WriteWord(esi + 44, src);
        }

        // 12921-12930 — push A1; push A6; call playerTackled; pop A6; pop A1.
        // A1 becomes opponent, A6 becomes opponent's team.
        {
            int savedA1 = A1;
            int savedA6 = A6;
            A1 = A2;                                            // mov A1, A2
            esi = A6;
            eax = Memory.ReadSignedDword(esi + 0);              // [A6+OffOpponentsTeam]
            A6 = eax;
            PlayerTackled(A1, A6);                              // call PlayerTackled
            A6 = savedA6;
            A1 = savedA1;
        }

        // 12931-12943 — cmp opponent.ballDistance, 800 ; ja out.
        esi = A2;
        {
            uint src = Memory.ReadDword(esi + 74);
            if (src > 800u) return;
        }

        // 12945-12952 — load tackler.tackleState; or ax,ax ; jz @@foul_conceded.
        esi = A1;
        short tackleState = (short)Memory.ReadWord(esi + 96);
        if (tackleState == 0) goto l_foul_conceeded;

        // 12954-12965 — cmp tackler.tackleState, TS_GOOD_TACKLE (2) ;
        // jz @@play_foul_comment_and_return.
        if (tackleState == TS_GOOD_TACKLE) goto l_play_foul_comment_and_return;

        // 12967-12987 — D0 = tackler.direction; D0 -= opponent.direction;
        // cmp D0,-1 ; jl out.
        short tacklerDir = (short)Memory.ReadWord(esi + 42);
        short D0_dir = tacklerDir;
        esi = A2;
        short oppDir = (short)Memory.ReadWord(esi + 42);
        D0_dir = (short)(D0_dir - oppDir);
        if (D0_dir < -1) return;

        // 12989-12999 — cmp D0, 1 ; jg out.
        if (D0_dir > 1) return;

    l_foul_conceeded:
        // 13002-13013 — A0 = team.teamStatsPtr; add [A0+4], 1 (foulsConceded).
        esi = A6;
        eax = Memory.ReadSignedDword(esi + 14);                 // [A6+OffTeamStatsPtr]
        A0 = eax;
        esi = A0;
        {
            ushort src = Memory.ReadWord(esi + 4);
            src = (ushort)(src + 1);
            Memory.WriteWord(esi + 4, src);
        }

        // 13014-13020 — or ax, cardsDisallowed ; jnz @@no_cards_given.
        short ax = (short)Memory.ReadWord(Memory.Addr.cardsDisallowed);
        if (ax != 0) goto l_no_cards_given;

        // 13022-13028 — or ax, g_trainingGame ; jnz @@no_cards_given.
        ax = (short)Memory.ReadWord(Memory.Addr.g_trainingGame);
        if (ax != 0) goto l_no_cards_given;

        // 13030-13045 — D1 = opponent.x +2; D2 = opponent.y +2.
        // cmp D1, 193 ; jl @@not_in_penalty_area
        esi = A2;
        short D1s = (short)Memory.ReadWord(esi + 32);
        short D2s = (short)Memory.ReadWord(esi + 36);
        if (D1s < 193) goto l_not_in_penalty_area;
        // 13047-13057 — cmp D1, 478 ; jg @@not_in_penalty_area
        if (D1s > 478) goto l_not_in_penalty_area;
        // 13059-13069 — cmp D2, 129 ; jl @@not_in_penalty_area
        if (D2s < 129) goto l_not_in_penalty_area;
        // 13071-13081 — cmp D2, 769 ; jg @@not_in_penalty_area
        if (D2s > 769) goto l_not_in_penalty_area;

        // 13083-13093 — cmp A6, offset topTeamData ; jnz @@right_team.
        if (A6 != TeamData.TopBase) goto l_right_team;

        // 13095-13105 — cmp D2, 216 ; jle @@in_penalty_area.
        if (D2s <= 216) goto l_in_penalty_area;
        goto l_not_in_penalty_area;

    l_right_team:
        // 13110-13120 — cmp D2, 682 ; jge @@in_penalty_area.
        if (D2s >= 682) goto l_in_penalty_area;
        // falls through to l_not_in_penalty_area below.

    l_not_in_penalty_area:
        // 13123-13137 — D1 = 336, D2 = 129 (if top) or 769 (if bottom).
        D1s = 336;
        D2s = 129;
        if (A6 != TeamData.TopBase)
            D2s = 769;

    l_left_team:
        // 13140-13189 — A5 = A2; D3 = (opp.x - D1)^2 + (opp.y - D2)^2.
        // Then iterate the team's spritesTable to find closest non-sent-off,
        // non-keeper teammate to (D1, D2). A5 ends up as the closest such player.
        A5 = A2;
        esi = A2;
        short D6_w = (short)Memory.ReadWord(esi + 32);
        D6_w = (short)(D6_w - D1s);
        int D6 = D6_w * D6_w;
        short D3_w = (short)Memory.ReadWord(esi + 36);
        D3_w = (short)(D3_w - D2s);
        int D3 = D3_w * D3_w;
        D3 = D3 + D6;

        esi = A6;
        eax = Memory.ReadSignedDword(esi + 20);                 // [A6+OffPlayers]
        A0 = eax;
        int D0_loop = 10;

    l_players_loop:
        esi = A0;
        eax = Memory.ReadSignedDword(esi + 0);
        A0 = A0 + 4;
        A4 = eax;
        esi = A4;
        short sentAway = (short)Memory.ReadWord(esi + 108);
        if (sentAway != 0) goto l_next_player;

        if ((short)Memory.ReadWord(esi + 2) == 1) goto l_next_player;     // ordinal == 1 (goalkeeper)
        if (A4 == A1) goto l_next_player;                                  // skip self

        D6_w = (short)Memory.ReadWord(esi + 32);
        D6_w = (short)(D6_w - D1s);
        D6 = D6_w * D6_w;
        short D7_w = (short)Memory.ReadWord(esi + 36);
        D7_w = (short)(D7_w - D2s);
        int D7 = D7_w * D7_w;
        D6 = D6 + D7;

        // cmp D6, D3 ; ja next_player (D6 is unsigned via `ja`).
        if ((uint)D6 > (uint)D3) goto l_next_player;
        D3 = D6;
        A5 = A4;

    l_next_player:
        D0_loop--;
        // jns players_loop — repeat while non-negative.
        if (D0_loop >= 0) goto l_players_loop;

        // 13305-13317 — dseg_114EC2 = A5; cmp A2, A5 ; jz cseg_79444.
        Memory.WriteDword(Memory.Addr.lastTackleNearestTeammate, A5);
        if (A2 == A5) goto cseg_79444;

    l_in_penalty_area:
        // 13320-13341 — D0 = (currentGameTick & 0x1E) >> 1.
        // cmp D0, playerCardChance ; jnb @@no_cards_given.
        short tick = (short)Memory.ReadWord(Memory.Addr.currentGameTick);
        int D0_tick = tick & 30;
        D0_tick = D0_tick >> 1;
        short cardChance = (short)Memory.ReadWord(Memory.Addr.playerCardChance);
        // jnb = unsigned >= ; treat signed values as such with cast.
        if ((ushort)D0_tick >= (ushort)cardChance) goto l_no_cards_given;

        // 13343-13354 — Rand(); cmp D0, 32 ; jb @@direct_red_card.
        int D0_rand = SwosRand();
        if ((ushort)D0_rand < 32u) goto l_direct_red_card;

    l_yellow_card:
        // 13356-13365 — call TryBookingThePlayer; if !zero (no card given),
        // goto no_cards. Else proceed to TestFoulForPenaltyAndFreeKick + ActivateReferee.
        {
            int bookOut = TryBookingThePlayer(A1, A6);
            if (bookOut != 0) goto l_no_cards_given;
            TestFoulForPenaltyAndFreeKick(A2, A6);
            {
                int savedA1 = A1;
                Referee.ActivateReferee();
                A1 = savedA1;
            }
            return;
        }

    cseg_79444:
        // 13367-13379 — Rand() ; cmp D0, 32 ; jb @@yellow_card.
        D0_rand = SwosRand();
        if ((ushort)D0_rand < 32u) goto l_yellow_card;

    l_direct_red_card:
        // 13381-13390 — call TrySendingOffThePlayer; if !zero, no_cards. Else
        // TestFoulForPenaltyAndFreeKick + ActivateReferee.
        {
            int sendOut = TrySendingOffThePlayer(A1, A6);
            if (sendOut != 0) goto l_no_cards_given;
            TestFoulForPenaltyAndFreeKick(A2, A6);
            {
                int savedA1 = A1;
                Referee.ActivateReferee();
                A1 = savedA1;
            }
            return;
        }

    l_no_cards_given:
        // 13392-13393 — jmp TestFoulForPenaltyAndFreeKick (tail call).
        TestFoulForPenaltyAndFreeKick(A2, A6);
    }

    // ====================================================================
    // testFoulForPenaltyAndFreeKick — updatePlayers.cpp:13404-13868
    // ====================================================================
    //
    // in:
    //   A2 -> fouled player (sprite)
    //   A6 -> team that fouled (general info)
    //
    // Foul made; set gameState to penalty / free-kick / ordinary foul depending
    // on location and team. Also writes foul{X,Y}Coordinate, breakCameraMode,
    // playerTurnFlags, then transitions to stoppage mode.
    private static void TestFoulForPenaltyAndFreeKick(int aBallOrFouledSprite, int aTeamData)
    {
        int A2 = aBallOrFouledSprite;
        int A6 = aTeamData;
        int esi;

        // 13406-13417 — cmp gameStatePl, 101 ; jz @@out.
        if ((short)Memory.ReadWord(Memory.Addr.gameStatePl) == 101) return;

        StubPlayFoulWhistleSample();

        // 13420-13424 — D1 = foul.x +2, D2 = foul.y +2.
        esi = A2;
        short D1 = (short)Memory.ReadWord(esi + 32);
        short D2 = (short)Memory.ReadWord(esi + 36);

        // 13425-13435 — cmp A6, offset topTeamData ; jz @@left_team.
        if (A6 == TeamData.TopBase) goto l_left_team;

        // 13437 — cameraDirection = 4.
        Memory.WriteWord(Memory.Addr.cameraDirection, 4);
        // 13438-13472 — penalty area test for bottom team (Y>=682, 193<=X<=478).
        if (D2 < 682) goto l_not_in_lower_penalty_area;
        if (D1 < 193) goto l_not_in_lower_penalty_area;
        if (D1 > 478) goto l_not_in_lower_penalty_area;

        // 13474-13509 — penalty.
        Memory.WriteWord(Memory.Addr.gameState, 14);
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        Memory.WriteByte(Memory.Addr.playerTurnFlags, 56);
        Memory.WriteWord(Memory.Addr.foulXCoordinate, 336);
        Memory.WriteWord(Memory.Addr.foulYCoordinate, 711);
        StubPlayPenaltyComment();
        goto l_continue_after_penalty;

    l_left_team:
        // 13513 — cameraDirection = 0.
        Memory.WriteWord(Memory.Addr.cameraDirection, 0);
        // 13514-13548 — penalty area test for top team (Y<=216, 193<=X<=478).
        if (D2 > 216) goto l_not_in_upper_penalty_area;
        if (D1 < 193) goto l_not_in_upper_penalty_area;
        if (D1 > 478) goto l_not_in_upper_penalty_area;

        // 13550-13585 — penalty (upper).
        Memory.WriteWord(Memory.Addr.gameState, 14);
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        Memory.WriteByte(Memory.Addr.playerTurnFlags, 131);
        Memory.WriteWord(Memory.Addr.foulXCoordinate, 336);
        Memory.WriteWord(Memory.Addr.foulYCoordinate, 187);
        StubPlayPenaltyComment();
        goto l_continue_after_penalty;

    l_not_in_upper_penalty_area:
        StubPlayFoulComment();
        // 13591-13600 — cmp D2, 216 ; jl @@ordinary_foul.
        if (D2 < 216) goto l_ordinary_foul;
        // 13602-13612 — cmp D2, 331 ; jl @@its_a_free_kick.
        if (D2 < 331) goto l_its_a_free_kick;
        goto l_ordinary_foul;

    l_not_in_lower_penalty_area:
        StubPlayFoulComment();
        // 13619-13628 — cmp D2, 567 ; jl @@ordinary_foul.
        if (D2 < 567) goto l_ordinary_foul;
        // 13630-13640 — cmp D2, 682 ; jg @@ordinary_foul.
        if (D2 > 682) goto l_ordinary_foul;

    l_its_a_free_kick:
        // 13643-13653 — cmp A6, offset bottomTeamData ; jz @@right_team_made_foul.
        if (A6 == TeamData.BottomBase) goto l_right_team_made_foul;

        // 13655-13727 — for top-team-made-foul (foul is taken by bottom team):
        // map X coordinate to one of 7 free-kick zones (left_1..center..right_3).
        if (D1 < 153) goto l_free_kick_left_1;
        if (D1 < 261) goto l_free_kick_left_2;
        if (D1 < 309) goto l_free_kick_left_3;
        if (D1 < 362) goto l_free_kick_center;
        if (D1 < 410) goto l_free_kick_right_1;
        if (D1 < 518) goto l_free_kick_right_2;
        goto l_free_kick_right_3;

    l_right_team_made_foul:
        // 13730-13800 — for bottom-team-made-foul: mirror mapping.
        if (D1 < 153) goto l_free_kick_right_3;
        if (D1 < 261) goto l_free_kick_right_2;
        if (D1 < 309) goto l_free_kick_right_1;
        if (D1 < 362) goto l_free_kick_center;
        if (D1 < 410) goto l_free_kick_left_3;
        if (D1 < 518) goto l_free_kick_left_2;
        // fall through to l_free_kick_left_1.

    l_free_kick_left_1:
        Memory.WriteWord(Memory.Addr.gameState, 6);   // ST_FREE_KICK_LEFT1
        goto l_save_foul_coordinates;
    l_free_kick_left_2:
        Memory.WriteWord(Memory.Addr.gameState, 7);
        goto l_save_foul_coordinates;
    l_free_kick_left_3:
        Memory.WriteWord(Memory.Addr.gameState, 8);
        goto l_save_foul_coordinates;
    l_free_kick_center:
        Memory.WriteWord(Memory.Addr.gameState, 9);
        goto l_save_foul_coordinates;
    l_free_kick_right_1:
        Memory.WriteWord(Memory.Addr.gameState, 10);
        goto l_save_foul_coordinates;
    l_free_kick_right_2:
        Memory.WriteWord(Memory.Addr.gameState, 11);
        goto l_save_foul_coordinates;
    l_free_kick_right_3:
        Memory.WriteWord(Memory.Addr.gameState, 12);
        goto l_save_foul_coordinates;

    l_ordinary_foul:
        Memory.WriteWord(Memory.Addr.gameState, 13);   // ST_FOUL

    l_save_foul_coordinates:
        Memory.WriteWord(Memory.Addr.breakCameraMode, -1);
        Memory.WriteByte(Memory.Addr.playerTurnFlags, -1);
        Memory.WriteWord(Memory.Addr.foulXCoordinate, D1);
        Memory.WriteWord(Memory.Addr.foulYCoordinate, D2);

    l_continue_after_penalty:
        // 13842-13855 — `cmp forceLeftTeam, 1 / jnz @@jump_here /
        // mov A6, offset bottomTeamData`: the jnz SKIPS the assignment, so
        // A6 becomes bottomTeamData only when forceLeftTeam == 1; otherwise
        // A6 stays the FOULING team. (Was inverted here — every foul awarded
        // the restart to the top team's opponent regardless of who fouled.)
        if ((short)Memory.ReadWord(Memory.Addr.forceLeftTeam) == 1)
            A6 = TeamData.BottomBase;

        // 13858-13867 — stoppage transition.
        Memory.WriteWord(Memory.Addr.gameStatePl, 101);
        Memory.WriteWord(Memory.Addr.gameNotInProgressCounterWriteOnly, 0);
        esi = A6;
        int teamOpp = Memory.ReadSignedDword(esi + 0);  // [A6+OffOpponentsTeam]
        Memory.WriteDword(Memory.Addr.lastTeamPlayedBeforeBreak, teamOpp);
        Memory.WriteWord(Memory.Addr.stoppageTimerTotal, 0);
        Memory.WriteWord(Memory.Addr.stoppageTimerActive, 0);
        TeamPort.StopAllPlayers();
        Memory.WriteWord(Memory.Addr.cameraXVelocity, 0);
        Memory.WriteWord(Memory.Addr.cameraYVelocity, 0);
    }

    // ====================================================================
    // tryBookingThePlayer — updatePlayers.cpp:13877-14174
    // ====================================================================
    //
    // in:
    //   A1 -> player sprite (fouler)
    //   A6 -> team (general)
    // out:
    //   0 = card given (the asm sets zero flag)
    //   1 = card NOT given (the asm sets ax=1)
    //
    // The original returns through the zero flag plus D0; we return D0 (0 or 1).
    private static int TryBookingThePlayer(int aPlayerSprite, int aTeamData)
    {
        int A1 = aPlayerSprite;
        int A6 = aTeamData;
        int A0;
        int A5;
        int esi;

        // 13879-13884 — or ax, plg_D3_param ; jnz cseg_794A5.
        short ax = (short)Memory.ReadWord(Memory.Addr.plg_D3_param);
        if (ax != 0) goto cseg_794A5;

        // 13886-13899 — for AI-only path: check team.playerNumber + playerCoachNumber.
        esi = A6;
        ax = (short)Memory.ReadWord(esi + 4);                 // playerNumber
        if (ax != 0) goto l_player_controls_team;

        ax = (short)Memory.ReadWord(esi + 6);                 // playerCoachNumber
        if (ax != 0) goto l_player_controls_team;

        goto l_no_card_out;

    cseg_794A5:
        // 13904-13917 — same playerNumber + playerCoachNumber checks.
        esi = A6;
        ax = (short)Memory.ReadWord(esi + 4);
        if (ax != 0) goto l_player_controls_team;
        ax = (short)Memory.ReadWord(esi + 6);
        if (ax != 0) goto l_player_controls_team;

        // 13919-13925 — cmp player.cards, 0 ; jnz l_no_card_out.
        esi = A1;
        ax = (short)Memory.ReadWord(esi + 102);
        if (ax != 0) goto l_no_card_out;

    l_player_controls_team:
        // 13927-13934 — cmp player.cards, 0 ; jz l_player_has_no_cards.
        esi = A1;
        ax = (short)Memory.ReadWord(esi + 102);
        if (ax == 0) goto l_player_has_no_cards;

        // 13936-13947 — cmp team.teamNumber, 1 ; jnz l_second_team.
        esi = A6;
        if ((short)Memory.ReadWord(esi + 18) != 1) goto l_second_team;

        // 13949-13954 — or ax, team1NumAllowedInjuries ; jz l_no_card_out.
        ax = (short)Memory.ReadWord(Memory.Addr.team1NumAllowedInjuries);
        if (ax == 0) goto l_no_card_out;
        goto l_player_has_no_cards;

    l_second_team:
        // 13959-13964 — or ax, team2NumAllowedInjuries ; jz l_no_card_out.
        ax = (short)Memory.ReadWord(Memory.Addr.team2NumAllowedInjuries);
        if (ax == 0) goto l_no_card_out;

    l_player_has_no_cards:
        // 13966-13996 — A5 = team.inGameTeamPtr + inGameTeamPlayerOffsets[(ordinal-1)*2].
        // Port fix: inGameTeamPtr is players[0]; the +51/+52 field literals below
        // are TeamGame-header-relative, so rebase to the TeamGame start (−42).
        esi = A6;
        int eax = Memory.ReadSignedDword(esi + 10) - kTeamGameHeaderSize;   // OffInGameTeamPtr − TeamGame header
        A5 = eax;
        esi = A1;
        short D0 = (short)Memory.ReadWord(esi + 2);            // ordinal
        D0 = (short)(D0 - 1);
        D0 = (short)(D0 << 1);                                 // *2
        A0 = Memory.Addr.inGameTeamPlayerOffsets;
        esi = A0;
        int ebx = (ushort)D0;
        short axS = (short)Memory.ReadWord(esi + ebx);
        D0 = axS;
        eax = A5;
        ebx = (ushort)D0;
        eax = eax + ebx;
        A5 = eax;

        // 13997-14005 — D0 = player.cards + 1.
        esi = A1;
        ax = (short)Memory.ReadWord(esi + 102);
        D0 = ax;
        D0 = (short)(D0 + 1);

        // 14006-14011 — or ax, plg_D3_param ; jnz cseg_795E0.
        ax = (short)Memory.ReadWord(Memory.Addr.plg_D3_param);
        if (ax != 0) goto cseg_795E0;

        // 14013-14022 — A0 = dseg_17E3EE; if PlayerGameHeader.previousCards != 0
        // then A0 = dseg_17E3F3.
        A0 = Memory.Addr.dseg_17E3EE;
        esi = A5;
        byte al = Memory.ReadByte(esi + 51);                   // previousCards
        if (al != 0)
            A0 = Memory.Addr.dseg_17E3F3;

    // cseg_795B7:
        // 14025-14033 — D1 = PlayerGameHeader.cards;
        // newCards = dseg_17E3xx[D0 (ordinal byte)]; write to PlayerGameHeader.cards.
        esi = A5;
        al = Memory.ReadByte(esi + 52);                        // cards
        byte D1b = al;
        esi = A0;
        ebx = (ushort)D0;
        al = Memory.ReadByte(esi + ebx);
        esi = A5;
        Memory.WriteByte(esi + 52, al);
        goto l_give_yellow_card_to_player;

    cseg_795E0:
        // 14036-14050 — D1 = PlayerGameHeader.cards;
        // if cards == 0: cards = 1 (yellow). Else: cards = 3 (second yellow).
        esi = A5;
        al = Memory.ReadByte(esi + 52);
        D1b = al;
        if (al != 0) goto cseg_79603;
        Memory.WriteByte(esi + 52, 1);
        goto l_give_yellow_card_to_player;

    cseg_79603:
        esi = A5;
        Memory.WriteByte(esi + 52, 3);
        // l_jmp_give_yellow_card — fall through.

    l_no_card_out:
        return 1;

    l_give_yellow_card_to_player:
        // 14063-14077 — player.cards++; lastTeamBooked = A6; bookedPlayer = A1; refTimer = 0.
        esi = A1;
        {
            ushort src = Memory.ReadWord(esi + 102);
            src = (ushort)(src + 1);
            Memory.WriteWord(esi + 102, src);
        }
        Memory.WriteDword(Memory.Addr.lastTeamBooked, A6);
        Memory.WriteDword(Memory.Addr.bookedPlayer, A1);
        Memory.WriteWord(Memory.Addr.refTimer, 0);

        // 14078-14088 — cmp player.cards, 2 ; jz @@second_yellow_card.
        if ((short)Memory.ReadWord(esi + 102) == 2) goto l_second_yellow_card;

        // 14090-14107 — whichCard = CARD_YELLOW (1); team.teamStatsPtr.bookings++.
        Memory.WriteWord(Memory.Addr.whichCard, 1);
        esi = A6;
        eax = Memory.ReadSignedDword(esi + 14);                // teamStatsPtr
        A0 = eax;
        esi = A0;
        {
            ushort src = Memory.ReadWord(esi + 6);
            src = (ushort)(src + 1);
            Memory.WriteWord(esi + 6, src);
        }
        return 0;

    l_second_yellow_card:
        // 14111-14123 — whichCard = CARD_SECOND_YELLOW (3); bookings--.
        Memory.WriteWord(Memory.Addr.whichCard, 3);
        esi = A6;
        eax = Memory.ReadSignedDword(esi + 14);
        A0 = eax;
        esi = A0;
        {
            ushort src = Memory.ReadWord(esi + 6);
            src = (ushort)(src - 1);
            Memory.WriteWord(esi + 6, src);
        }
        // 14124-14131 — sendingsOff++.
        {
            ushort src = Memory.ReadWord(esi + 8);
            src = (ushort)(src + 1);
            Memory.WriteWord(esi + 8, src);
        }

        // 14132-14143 — cmp team.teamNumber, 1 ; jnz l_team2_red_card.
        esi = A6;
        if ((short)Memory.ReadWord(esi + 18) != 1) goto l_team2_red_card;

        // 14145-14156 — team1NumAllowedInjuries--.
        {
            ushort src = Memory.ReadWord(Memory.Addr.team1NumAllowedInjuries);
            src = (ushort)(src - 1);
            Memory.WriteWord(Memory.Addr.team1NumAllowedInjuries, src);
        }
        goto l_given_card_ok;

    l_team2_red_card:
        // 14159-14166 — team2NumAllowedInjuries--.
        {
            ushort src = Memory.ReadWord(Memory.Addr.team2NumAllowedInjuries);
            src = (ushort)(src - 1);
            Memory.WriteWord(Memory.Addr.team2NumAllowedInjuries, src);
        }

    l_given_card_ok:
        return 0;
    }

    // ====================================================================
    // trySendingOffThePlayer — updatePlayers.cpp:14180-14400
    // ====================================================================
    //
    // in:
    //   A1 -> player (sprite)
    //   A6 -> team (general)
    // out:
    //   0 = red card given
    //   1 = no red card given
    private static int TrySendingOffThePlayer(int aPlayerSprite, int aTeamData)
    {
        int A1 = aPlayerSprite;
        int A6 = aTeamData;
        int A0;
        int A5;
        int esi;

        // 14182-14195 — playerNumber check; else playerCoachNumber.
        esi = A6;
        short ax = (short)Memory.ReadWord(esi + 4);
        if (ax != 0) goto l_player;

        ax = (short)Memory.ReadWord(esi + 6);
        if (ax == 0) goto l_computer_team_no_red_card;

    l_player:
        // 14197-14209 — cmp team.teamNumber, 1.
        esi = A6;
        if ((short)Memory.ReadWord(esi + 18) != 1) goto l_team2;

        // 14211-14218 — or ax, team1NumAllowedInjuries ; jz @@computer_team_no_red_card.
        ax = (short)Memory.ReadWord(Memory.Addr.team1NumAllowedInjuries);
        if (ax == 0) goto l_computer_team_no_red_card;
        goto cseg_7972E;

    l_team2:
        // 14221-14226 — or ax, team2NumAllowedInjuries ; jz @@computer_team_no_red_card.
        ax = (short)Memory.ReadWord(Memory.Addr.team2NumAllowedInjuries);
        if (ax == 0) goto l_computer_team_no_red_card;

    cseg_7972E:
        // 14229-14258 — A5 = team.inGameTeamPtr + offsets[(ordinal-1)*2].
        // Port fix: rebase inGameTeamPtr(players[0]) to the TeamGame start (−42)
        // so the +51/+52 header-relative field literals land correctly.
        esi = A6;
        int eax = Memory.ReadSignedDword(esi + 10) - kTeamGameHeaderSize;
        A5 = eax;
        esi = A1;
        short D0 = (short)Memory.ReadWord(esi + 2);
        D0 = (short)(D0 - 1);
        D0 = (short)(D0 << 1);
        A0 = Memory.Addr.inGameTeamPlayerOffsets;
        esi = A0;
        int ebx = (ushort)D0;
        short axS = (short)Memory.ReadWord(esi + ebx);
        D0 = axS;
        eax = A5;
        ebx = (ushort)D0;
        eax = eax + ebx;
        A5 = eax;

        // 14259-14268 — D0 = (player.cards XOR 1) + 3.
        esi = A1;
        ax = (short)Memory.ReadWord(esi + 102);
        D0 = ax;
        D0 = (short)(D0 ^ 1);
        D0 = (short)(D0 + 3);

        // 14269-14274 — or ax, plg_D3_param ; jnz cseg_79804.
        ax = (short)Memory.ReadWord(Memory.Addr.plg_D3_param);
        if (ax != 0) goto cseg_79804;

        // 14276-14285 — A0 = dseg_17E3EE; if previousCards != 0, A0 = dseg_17E3F3.
        A0 = Memory.Addr.dseg_17E3EE;
        esi = A5;
        byte al = Memory.ReadByte(esi + 51);
        if (al != 0)
            A0 = Memory.Addr.dseg_17E3F3;

    // cseg_797DB:
        // 14288-14296 — D1 = PlayerGameHeader.cards; new = table[D0]; write back.
        esi = A5;
        al = Memory.ReadByte(esi + 52);
        byte D1b = al;
        esi = A0;
        ebx = (ushort)D0;
        al = Memory.ReadByte(esi + ebx);
        esi = A5;
        Memory.WriteByte(esi + 52, al);
        goto l_update_statistics_with_red_card;

    cseg_79804:
        // 14299-14302 — D1 = PlayerGameHeader.cards; cards = 3.
        esi = A5;
        al = Memory.ReadByte(esi + 52);
        D1b = al;
        Memory.WriteByte(esi + 52, 3);
        // l_jmp_to_update — fall through.

        goto l_update_statistics_with_red_card;

    l_computer_team_no_red_card:
        return 1;

    l_update_statistics_with_red_card:
        // 14316-14321 — lastTeamBooked = A6; bookedPlayer = A1; refTimer = 0;
        // whichCard = CARD_RED (2).
        Memory.WriteDword(Memory.Addr.lastTeamBooked, A6);
        Memory.WriteDword(Memory.Addr.bookedPlayer, A1);
        Memory.WriteWord(Memory.Addr.refTimer, 0);
        Memory.WriteWord(Memory.Addr.whichCard, 2);

        // 14322-14336 — A0 = team.teamStatsPtr; if player.cards == 1, A0.bookings--.
        esi = A6;
        eax = Memory.ReadSignedDword(esi + 14);
        A0 = eax;
        esi = A1;
        if ((short)Memory.ReadWord(esi + 102) != 1) goto l_no_yellow_card;

        esi = A0;
        {
            ushort src = Memory.ReadWord(esi + 6);
            src = (ushort)(src - 1);
            Memory.WriteWord(esi + 6, src);
        }

    l_no_yellow_card:
        // 14349-14357 — A0.sendingsOff++.
        esi = A0;
        {
            ushort src = Memory.ReadWord(esi + 8);
            src = (ushort)(src + 1);
            Memory.WriteWord(esi + 8, src);
        }

        // 14358-14369 — cmp team.teamNumber, 1 ; jnz l_second_team_player.
        esi = A6;
        if ((short)Memory.ReadWord(esi + 18) != 1) goto l_second_team_player;

        // 14371-14381 — team1NumAllowedInjuries--.
        {
            ushort src = Memory.ReadWord(Memory.Addr.team1NumAllowedInjuries);
            src = (ushort)(src - 1);
            Memory.WriteWord(Memory.Addr.team1NumAllowedInjuries, src);
        }
        goto l_red_card_given_out;

    l_second_team_player:
        // 14385-14392 — team2NumAllowedInjuries--.
        {
            ushort src = Memory.ReadWord(Memory.Addr.team2NumAllowedInjuries);
            src = (ushort)(src - 1);
            Memory.WriteWord(Memory.Addr.team2NumAllowedInjuries, src);
        }

    l_red_card_given_out:
        return 0;
    }

    // ====================================================================
    // Helper stubs — not yet ported. Match swos-port function signatures.
    // ====================================================================
    //
    // External callees of playerTacklingTestFoul that are still TODOs. They are
    // safe-default (do nothing / return 0) so the booking + foul state writes
    // still happen — only the audio cue / animation hook is missing.

    // updatePlayers.cpp:14410-14831 — playerTackled.
    //
    // Mechanical port. Marks the fouled player down (common tail at
    // l_set_tackled_anim_table @ 14825-14830) and conditionally writes
    // PlayerGameHeader.isInjured / .injuriesBitfield + Sprite.injuryLevel
    // when the injury-probability roll lands. The three branches before the
    // common tail decide whether the tackle becomes an injury:
    //
    //   (a) Training game: 75% chance to skip injury entirely (Rand & 3 != 0).
    //   (b) Non-training, non-zero teamN allowed-injuries:
    //       roll Rand against kTackleInjuryProbability[gameLengthInGame] (or
    //       the AlreadyInjured table if PlayerGameHeader.injuriesBitfield's
    //       top-3 bits already equal 32 — i.e. injuriesBitfield & 0xE0 == 0x20).
    //       Carry-set (Rand < threshold) → injury fires; carry-clear → skip.
    //   (c) Injury fires: roll Rand again against kInjuryLevels[] / kInjuryLevelAlreadyInjured[]
    //       walking the table until found, accumulating D1 += 32 each step.
    //       The final D1 (in [0x20..0xE0] across the top-3 bits) gets ORed
    //       into PlayerGameHeader.injuriesBitfield (only if higher than current),
    //       and `dseg_17E2EC[D1 >> 5]` (the corresponding Sprite.injuryLevel
    //       words: 0/60/70/80/90/100/110/130) becomes Sprite.injuryLevel.
    //       Both teamN allowed-injuries counters decrement on this path.
    //
    // From swos.asm:107802-107976 (`PlayerTackled` proc) + table defs at
    // swos.asm:245826-245837. PlayerGameHeader offsets +48 / +77 match
    // updatePlayers.cpp:14751 / 14752 (Hex-Rays of m68k struct).
    //
    // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:14410.
    private static void PlayerTackled(int playerSpriteAddr, int teamDataAddr)
    {
        int A1 = playerSpriteAddr;
        int A6 = teamDataAddr;
        int A0 = 0;       // PlayerGameHeader ptr (set in @@injury_allowed)
        int A5 = 0;       // table walking pointer
        short D0 = 0;     // Rand result + temp
        short D1 = 0;     // injury bitfield accumulator
        bool doInjury = false;
        bool injuryAlreadyInjured = false;

        // 14412-14417 — training game check.
        // `if (g_trainingGame) and (Rand & 3) jmp @@set_tackled_anim_table`
        // — i.e. 3/4 of the time the tackle is harmless in training.
        short trainingGame = Memory.ReadSignedWord(Memory.Addr.g_trainingGame);
        if (trainingGame != 0)
        {
            int trRnd = Rng.NextByte() & 3;
            if (trRnd != 0) goto l_set_tackled_anim_table;
        }

    // l_not_a_training_game:
        // 14431-14459 — check teamN allowed-injuries; bail (no injury) if 0.
        short teamNumber = Memory.ReadSignedWord(A6 + 18);
        if (teamNumber == 1)
        {
            short ali = Memory.ReadSignedWord(Memory.Addr.team1NumAllowedInjuries);
            if (ali == 0) goto l_set_tackled_anim_table;
        }
        else
        {
            short ali = Memory.ReadSignedWord(Memory.Addr.team2NumAllowedInjuries);
            if (ali == 0) goto l_set_tackled_anim_table;
        }

    // l_injury_allowed:
        // 14461-14495 — resolve PlayerGameHeader.
        //   D0 = sprite.playerOrdinal - 1
        //   D0 *= 2
        //   D0 = inGameTeamPlayerOffsets[D0]
        //   A0 = team.inGameTeamPtr + D0  → PlayerGameHeader*
        short ordinal = Memory.ReadSignedWord(A1 + 2);
        int idx2 = (ordinal - 1) << 1;
        int byteOff = Memory.ReadSignedWord(Memory.Addr.inGameTeamPlayerOffsets + (ushort)idx2);
        // Port fix: inGameTeamPtr is players[0]; the +48/+77 field literals below
        // are TeamGame-header-relative, so rebase to the TeamGame start (−42) —
        // A0+48 then lands on PlayerInfo+6 (isInjured), A0+77 on +35 (injuriesBits).
        int inGameTeamPtr = Memory.ReadSignedDword(A6 + 10) - kTeamGameHeaderSize;
        A0 = inGameTeamPtr + (ushort)byteOff;

        // 14490-14491 — D1 = gameLengthInGame (probability table index).
        D1 = Memory.ReadSignedWord(Memory.Addr.gameLengthInGame);

        // 14492-14507 — choose probability table:
        //   A5 = kTackleInjuryProbability
        //   if (PlayerGameHeader.injuriesBitfield & 0xE0) == 0x20:
        //     A5 = kTackleInjuryProbabilityAlreadyInjured
        A5 = Memory.Addr.kTackleInjuryProbability;
        byte alIb = Memory.ReadByte(A0 + 77);
        short tmp = (short)(alIb & 0xE0);
        if (tmp == 32)
        {
            A5 = Memory.Addr.kTackleInjuryProbabilityAlreadyInjured;
            injuryAlreadyInjured = true;
        }

        // OpenSWOS fatigue: being tackled costs the fouled player (A1) a random
        // 1..5% of their current energy — fires once here, at tackle resolution
        // (gated on EffectEnabled inside). User spec.
        PlayerEnergy.DrainOnTackle(A1);

    // l_not_injured:
        // 14513-14527 — roll Rand against A5[D1]. Carry-clear (Rand >= thr) → skip.
        D0 = (short)Rng.NextByte();
        int threshold = Memory.ReadByte(A5 + (ushort)D1);
        // OpenSWOS fatigue: an exhausted (<20% energy) tackled player has DOUBLE
        // the injury chance — double the probability threshold (gated on
        // EffectEnabled inside InjuryRiskDoubled). No extra RNG draw, so the
        // stream stays deterministic. User spec.
        if (PlayerEnergy.InjuryRiskDoubled(A1)) threshold = System.Math.Min(255, threshold * 2);
        // cmp byte ptr D0, threshold ; jnb l_set_tackled_anim_table.
        // jnb = jump if not below, i.e. when Rand >= threshold. So injury only
        // fires when D0 < threshold.
        if ((byte)D0 >= threshold) goto l_set_tackled_anim_table;

        doInjury = true;

        // 14529 — playInjuryComment (audio cue, stubbed).
        StubPlayInjuryComment();

        // 14530-14549 — choose injury-level table:
        //   A5 = kInjuryLevels
        //   if (PlayerGameHeader.injuriesBitfield & 0xE0) == 0x20:
        //     A5 = kInjuryLevelAlreadyInjured
        A5 = Memory.Addr.kInjuryLevels;
        alIb = Memory.ReadByte(A0 + 77);
        tmp = (short)(alIb & 0xE0);
        if (tmp == 32)
            A5 = Memory.Addr.kInjuryLevelAlreadyInjured;

    // cseg_79E4D:
        // 14552-14747 — injury-severity rolling walk.
        //   D0 = Rand & 0x3F
        //   D1 = 0x20
        //   walk A5[0..6]: if D0 < A5[i] → break (final D1).
        //     else D0 -= A5[i]; D1 += 0x20.
        //   After max 7 steps, fall through with D1 fully accumulated.
        D0 = (short)(Rng.NextByte() & 0x3F);
        D1 = 0x20;
        // A5 already pointing at the chosen table (kInjuryLevels or
        // kInjuryLevelAlreadyInjured) from the branch above.
        for (int step = 0; step < 7; step++)
        {
            byte limit = Memory.ReadByte(A5);
            A5++;
            // cmp byte ptr D0, al ; jb @@set_injury_level   (i.e. break)
            if ((byte)D0 < limit)
                goto l_set_injury_level;
            // sub byte ptr D0, al
            D0 = (short)((byte)((byte)D0 - limit));
            // add word ptr D1, 32
            D1 = (short)(D1 + 32);
        }
        // No explicit break — fall through with D1 = 0x20 * 8 = 0x100 (truncated
        // to 0x00 byte-wise? No — D1 is word-sized so it actually reaches 0x100).
        // The asm DOES fall through; the bitfield write (& 0xE0) below truncates
        // to 0xE0 max (matches the "worst" injury severity).

    l_set_injury_level:;
        // 14751 — PlayerGameHeader.isInjured = 1.
        Memory.WriteByte(A0 + 48, 1);

        // 14752-14765 — if (D1 > injuriesBitfield) injuriesBitfield = D1.
        // (Compare unsigned byte; jbe = jump if D1 <= existing.)
        byte existingIb = Memory.ReadByte(A0 + 77);
        if ((byte)D1 > existingIb)
        {
            Memory.WriteByte(A0 + 77, (byte)D1);
        }

    // cseg_79FD7:
        // 14768-14785 — Sprite.injuryLevel = dseg_17E2EC[(D1 & 0xE0) >> 5].
        short d1Idx = (short)((D1 & 0xE0) >> 5);
        short injLvl = Memory.ReadSignedWord(Memory.Addr.dseg_17E2EC + (ushort)(d1Idx << 1));
        Memory.WriteWord(A1 + PlayerSprite.OffInjuryLevel, injLvl);

        // 14786-14797 — branch on teamNumber.
        // 14798-14823 — decrement the appropriate teamN allowed-injuries counter.
        if (teamNumber == 1)
        {
            ushort src = Memory.ReadWord(Memory.Addr.team1NumAllowedInjuries);
            src = (ushort)(src - 1);
            Memory.WriteWord(Memory.Addr.team1NumAllowedInjuries, src);
        }
        else
        {
            ushort src = Memory.ReadWord(Memory.Addr.team2NumAllowedInjuries);
            src = (ushort)(src - 1);
            Memory.WriteWord(Memory.Addr.team2NumAllowedInjuries, src);
        }
        // Suppress CS0219 "assigned but not used" on injuryAlreadyInjured —
        // it documents the table choice; the side-effects (A5) carry the value.
        _ = injuryAlreadyInjured;
        _ = doInjury;

    l_set_tackled_anim_table:;
        // 14825-14830 — common tail. Mark down + animation.
        // updatePlayers.cpp:14827 — mov [esi+Sprite.playerState], PL_TACKLED.
        Memory.WriteByte(A1 + PlayerSprite.OffPlayerState, 3);
        // updatePlayers.cpp:14828 — mov [esi+Sprite.playerDownTimer], 50.
        Memory.WriteByte(A1 + PlayerSprite.OffPlayerDownTimer, 50);
        // updatePlayers.cpp:14829-14830 — SetPlayerAnimationTable(playerTackledAnimTable).
        PlayerActions.SetPlayerAnimationTable(
            A1, Memory.Addr.kPlayerTackledAnimTableAddr);
    }

    // SWOS::PlayInjuryComment — audio cue at updatePlayers.cpp:14529. Plays an
    // injury-related commentary line. Defer until the audio bus lands.
    // TODO from external/swos-port/src/audio/comments.cpp PlayInjuryComment.
    private static void StubPlayInjuryComment() => OpenSwos.Audio.MatchAudio.InjuryComment();

    // SWOS::PlayDangerousPlayComment / PlayFoulWhistleSample / PlayPenaltyComment
    // / PlayFoulComment — audio cues. No audio backend wired yet.
    // TODO from external/swos-port/src/swos/swos.h SWOS::PlayXxx hooks
    private static void StubPlayDangerousPlayComment() => OpenSwos.Audio.MatchAudio.DangerousPlayComment();
    private static void StubPlayFoulWhistleSample() => OpenSwos.Audio.MatchAudio.PlayFoulWhistle();
    private static void StubPlayPenaltyComment() => OpenSwos.Audio.MatchAudio.PenaltyComment();
    private static void StubPlayFoulComment() => OpenSwos.Audio.MatchAudio.FoulComment();

    // SWOS::Rand — table-driven xor-stream PRNG. updatePlayers.cpp:13343 and
    // :13367 use this for foul-severity → card-colour selection (Rand() < 32
    // routes to red card, otherwise yellow). Returns a byte (0..255). Wired
    // through SwosVm.Rng — canonical deterministic-lockstep stream-1 source.
    // Source: external/swos-port/src/util/random.cpp
    private static int SwosRand() => Rng.NextByte();

    // ====================================================================
    // playerBeginTackling — updatePlayers.cpp:14839-14946
    // ====================================================================
    //
    // in:
    //   A1 -> player sprite that's tackling
    //   A6 -> team (general)
    //   D0  = currently pressed direction
    //
    // Set player state to PL_TACKLING, set his dest x and y from
    // kDefaultDestinations[direction] table, and set speed to tackling speed.
    //
    // Mechanical port. Preserves the asm bug at 14893 where playerDownTimer
    // is unconditionally written to -1 immediately after the conditional
    // write at 14889 (so the fasterTackle branch result gets clobbered).
    //
    // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:14839
    public static void PlayerBeginTackling(int aPlayerSprite, int aTeamData, int direction)
    {
        int A1 = aPlayerSprite;
        int A6 = aTeamData;
        int D0 = direction;
        int A0;
        int A5;
        int esi;

        // 14841-14842 — sprite.tackleState = 0.
        esi = A1;
        Memory.WriteWord(esi + 96, 0);                          // Sprite.tackleState

        // 14843-14845 — team.controlledPlDirection = D0.
        short ax = (short)D0;
        esi = A6;
        Memory.WriteWord(esi + 56, ax);                         // TeamGeneralInfo.controlledPlDirection

        // 14846-14847 — sprite.direction = D0.
        esi = A1;
        Memory.WriteWord(esi + 42, ax);                         // Sprite.direction

        // 14848-14849 — SetPlayerAnimationTable(plTacklingAnimTable).
        // Asm uses absolute address 453364; our Memory remap puts the table
        // at Memory.Addr.kPlTacklingAnimTableAddr. Forwarded through
        // PlayerActions.SetPlayerAnimationTable (swos.asm:104309).
        A0 = Memory.Addr.kPlTacklingAnimTableAddr;
        PlayerActions.SetPlayerAnimationTable(A1, A0);

        // 14850-14852 — sprite.playerState = PL_TACKLING (1);
        //               sprite.playerDownTimer = m_playerDownTacklingInterval.
        esi = A1;
        Memory.WriteByte(esi + 12, 1);                          // PL_TACKLING
        int downInterval = Memory.ReadSignedWord(Memory.Addr.m_playerDownTacklingInterval);
        Memory.WriteByte(esi + 13, downInterval);               // Sprite.playerDownTimer

        // 14853-14868 — D1 = inGameTeamPlayerOffsets[(sprite.playerOrdinal - 1) * 2].
        short ordinal = (short)Memory.ReadWord(esi + 2);        // Sprite.playerOrdinal
        int D1 = ordinal;
        D1 = (short)(D1 - 1);
        A0 = Memory.Addr.inGameTeamPlayerOffsets;
        D1 = (short)(D1 << 1);
        esi = A0;
        int ebx = (ushort)(short)D1;
        short axOff = (short)Memory.ReadWord(esi + ebx);
        D1 = axOff;

        // 14870-14879 — A0 = team.inGameTeamPtr + D1 = PlayerGameHeader.
        // Port fix: rebase inGameTeamPtr(players[0]) to the TeamGame start (−42)
        // so the +50 fasterTackle header-relative literal lands correctly.
        esi = A6;
        int eax = Memory.ReadSignedDword(esi + 10) - kTeamGameHeaderSize;   // TeamGeneralInfo.inGameTeamPtr − header
        ebx = (ushort)(short)D1;
        eax = eax + ebx;
        A0 = eax;

        // 14880-14886 — al = PlayerGameHeader.fasterTackle; or al, al ; jz no_faster_tackle.
        esi = A0;
        byte al = Memory.ReadByte(esi + 50);                    // PlayerGameHeader.fasterTackle
        if (al == 0) goto l_no_faster_tackle;

        // 14888-14889 — sprite.playerDownTimer = 25.
        esi = A1;
        Memory.WriteByte(esi + 13, 25);

    l_no_faster_tackle:
        // 14892-14893 — sprite.playerDownTimer = -1.
        // NOTE: This unconditional write clobbers both the m_playerDownTacklingInterval
        // seed AND the fasterTackle = 25 branch. Mechanical port; preserve the
        // asm exactly even though it looks like a bug — Sprite.playerDownTimer
        // is a byte so -1 = 0xFF, which the downstream consumer treats as a
        // sentinel "tackle in progress" value rather than a countdown.
        esi = A1;
        Memory.WriteByte(esi + 13, -1);

        // 14894-14907 — A5 = kDefaultDestinations + (D0 << 2).
        A5 = Memory.Addr.kDefaultDestinations;
        D0 = (short)((short)D0 << 2);
        eax = A5;
        ebx = (ushort)(short)D0;
        eax = eax + ebx;
        A5 = eax;

        // 14908-14926 — destX = (word ptr [A5]) + sprite.x.whole; A5 += 2.
        esi = A5;
        short dx = (short)Memory.ReadWord(esi);
        A5 = A5 + 2;
        D0 = dx;
        esi = A1;
        short spriteXWhole = (short)Memory.ReadWord(esi + 32);  // Sprite.x +2 (whole)
        D0 = (short)((short)D0 + spriteXWhole);
        short tackleDestX = (short)D0;

        // 14927-14942 — destY = (word ptr [A5]) + sprite.y.whole.
        esi = A5;
        short dy = (short)Memory.ReadWord(esi);
        D0 = dy;
        esi = A1;
        short spriteYWhole = (short)Memory.ReadWord(esi + 36);  // Sprite.y +2 (whole)
        D0 = (short)((short)D0 + spriteYWhole);
        short tackleDestY = (short)D0;

        // DIRECTION-PRESERVING pitch clamp — PORT-SAFETY (not in original asm;
        // updatePlayers.cpp:14908-14942 writes dest = pos + kDefaultDestinations
        // [dir] unclamped and relies on the per-tick sprite-position cushion to
        // bound the slide, which our half-ported PL_TACKLING integration lacks).
        // The PREVIOUS clamp capped destX and destY to [81,590]×[129,769]
        // INDEPENDENTLY, which rotated every DIAGONAL slide toward the nearer
        // cardinal (kDefaultDestinations[SE]=(+1000,+1000): from x=550 the X cap
        // at 590 shrank dx to 40 while dy stayed 320 → a "SE" slide ran almost
        // straight down). USER BUG: "can't slide to the bottom-right / other
        // diagonal slides also wrong." Fix: scale the WHOLE slide vector down by
        // one common factor so the target lands on the pitch edge while the
        // slide ANGLE is preserved exactly. kDefaultDestinations components are
        // 0 or ±1000, so travel-per-axis == the common `travel` (45° for
        // diagonals; the zero axis is unconstrained).
        int vx = dx, vy = dy;                                   // slide vector (±1000 / 0)
        int posX = spriteXWhole, posY = spriteYWhole;
        int travel = 1000;                                     // full vector magnitude
        if (vx != 0)
        {
            int allowedX = vx > 0 ? (590 - posX) : (posX - 81);
            if (allowedX < 0) allowedX = 0;
            if (allowedX < travel) travel = allowedX;
        }
        if (vy != 0)
        {
            int allowedY = vy > 0 ? (769 - posY) : (posY - 129);
            if (allowedY < 0) allowedY = 0;
            if (allowedY < travel) travel = allowedY;
        }
        // vx,vy ∈ {0,±1000} → (v * travel) / 1000 == sign(v) * travel.
        tackleDestX = (short)(posX + (vx > 0 ? travel : vx < 0 ? -travel : 0));
        tackleDestY = (short)(posY + (vy > 0 ? travel : vy < 0 ? -travel : 0));
        esi = A1;
        Memory.WriteWord(esi + 58, tackleDestX);                // Sprite.destX
        Memory.WriteWord(esi + 60, tackleDestY);                // Sprite.destY

        // 14943-14944 — sprite.speed = kPlayerTacklingSpeed.
        short tacklingSpeed = (short)Memory.ReadWord(Memory.Addr.kPlayerTacklingSpeed);
        Memory.WriteWord(esi + 44, tacklingSpeed);              // Sprite.speed

        // 14945 — sprite.tacklingTimer = 0.
        Memory.WriteWord(esi + 106, 0);                         // Sprite.tacklingTimer
    }

    // ====================================================================
    // playersTackledTheBallStrong — updatePlayers.cpp:14960-15242
    // ====================================================================
    //
    // in:
    //      A1 -> sprite (player)
    //      A6 -> team (general)
    //
    // Player is tackling and hitting the ball. Adjust ball direction according
    // to the player direction and controls direction (do deflected tackles).
    // Set ball destination coordinates far away in that direction. Adjust ball
    // speed after tackle to 125% of player's speed (100% if CPU player). Set
    // player's speed afterward to 50%. If opponent's controlled player is more
    // than 9u away from the ball and distance between the 2 players is greater
    // than 32u it is considered a good tackle.
    //   u = sqr((x1 - x2)^2) + sqr((y1 - y2)^2)
    //
    // NOTE: there is a sibling/duplicate routine `playerTackledTheBallStrong`
    // (singular) in `player.cpp:1684-1966` already ported to
    // `PlayerActions.PlayerTackledTheBallStrong`. The two have nearly identical
    // logic but slightly different ball-speed scaling and use of A2: the
    // updatePlayers.cpp version sets A2 = ballSprite (328988) and writes
    // ball.destX/destY/speed via [A2+58/60/44], while the player.cpp version
    // routes everything through the BallSprite.* setters. This port preserves
    // the updatePlayers.cpp asm form mechanically.
    //
    // from updatePlayers.cpp:14960
    public static void PlayersTackledTheBallStrong(int aPlayerSprite, int aTeamData)
    {
        int A1 = aPlayerSprite;
        int A6 = aTeamData;
        int A0;
        int A2;
        int esi;
        int ebx;

        // 14962-14969 — D1 = team.currentAllowedDirection; if sign-bit set
        // (== -1, "no direction") then D1 = sprite.direction.
        esi = A6;
        short axS = (short)Memory.ReadWord(esi + 44);           // TeamGeneralInfo.currentAllowedDirection
        short D1 = axS;
        if (axS >= 0) goto l_current_direction_allowed;

        esi = A1;
        axS = (short)Memory.ReadWord(esi + 42);                 // Sprite.direction
        D1 = axS;

    l_current_direction_allowed:
        // 14975-14979 — A2 = offset ballSprite (328988).
        A2 = BallSprite.Base;

        // 14977-14989 — D0 = sprite.direction; D0 -= D1.
        esi = A1;
        short pDir = (short)Memory.ReadWord(esi + 42);          // Sprite.direction
        short D0 = pDir;
        // sub word ptr D0, D1 (signed-flag and unsigned-carry both read off
        // the same subtraction; jz uses zero flag, jb uses carry).
        ushort subSrc = (ushort)D1;
        ushort subDst = (ushort)D0;
        bool subCarry = subDst < subSrc;
        D0 = (short)(D0 - D1);

        // 14990-14991 — jz @@set_controlled_player_direction.
        if (D0 == 0) goto l_set_controlled_player_direction;

        // 14993-14996 — D0 &= 7.
        D0 = (short)(D0 & 7);

        // 14997-15006 — cmp D0, 4 ; jz @@set_controlled_player_direction.
        // The original re-uses the sub-flags from earlier for the `jb` below
        // — preserve mechanically by recomputing carry from D0 vs 4.
        bool cmp4Carry = (ushort)D0 < 4u;
        if (D0 == 4) goto l_set_controlled_player_direction;

        // 15008-15009 — jb @@controls_leaning_leftward.
        if (cmp4Carry) goto l_controls_leaning_leftward;

        // 15011-15022 — D0 = sprite.direction + 1.
        esi = A1;
        axS = (short)Memory.ReadWord(esi + 42);
        D0 = axS;
        D0 = (short)(D0 + 1);
        goto l_set_new_direction;

    l_controls_leaning_leftward:
        // 15024-15037 — D0 = sprite.direction - 1.
        esi = A1;
        axS = (short)Memory.ReadWord(esi + 42);
        D0 = axS;
        D0 = (short)(D0 - 1);
        goto l_set_new_direction;

    l_set_controlled_player_direction:
        // 15039-15042 — D0 = sprite.direction.
        esi = A1;
        axS = (short)Memory.ReadWord(esi + 42);
        D0 = axS;

    l_set_new_direction:
        // 15044-15051 — D0 &= 7; team.controlledPlDirection = D0.
        D0 = (short)(D0 & 7);
        esi = A6;
        Memory.WriteWord(esi + 56, D0);                         // TeamGeneralInfo.controlledPlDirection

        // 15052-15060 — A0 = offset kDefaultDestinations; D1 = [A0 + (D0 << 2)].
        A0 = Memory.Addr.kDefaultDestinations;
        short D0_shl2 = (short)(D0 << 2);
        esi = A0;
        ebx = (ushort)D0_shl2;
        axS = (short)Memory.ReadWord(esi + ebx);
        D1 = axS;

        // 15061-15070 — esi = A2 (ballSprite); D1 += ball.x +2; ball.destX = D1.
        esi = A2;
        axS = (short)Memory.ReadWord(esi + 32);                 // ball.x +2 (whole)
        D1 = (short)(D1 + axS);
        Memory.WriteWord(esi + 58, D1);                         // ball.destX

        // 15071-15083 — D1 = ball.y +2; D1 += [A0 + ebx + 2]; ball.destY = D1.
        axS = (short)Memory.ReadWord(esi + 36);                 // ball.y +2 (whole)
        D1 = axS;
        esi = A0;
        axS = (short)Memory.ReadWord(esi + ebx + 2);
        D1 = (short)(D1 + axS);
        esi = A2;
        Memory.WriteWord(esi + 60, D1);                         // ball.destY

        // 15084-15090 — D0 = team.playerNumber; if != 0 → @@player_not_cpu.
        esi = A6;
        short pNum = (short)Memory.ReadWord(esi + 4);           // TeamGeneralInfo.playerNumber
        if (pNum != 0) goto l_player_not_cpu;

        // 15092-15100 — CPU path: ball.speed = sprite.speed.
        esi = A1;
        short spSpeed = (short)Memory.ReadWord(esi + 44);       // Sprite.speed
        D0 = spSpeed;
        esi = A2;
        Memory.WriteWord(esi + 44, spSpeed);                    // ball.speed
        goto l_halve_player_speed;

    l_player_not_cpu:
        // 15102-15119 — first pass: D0 = sprite.speed; D0 >>= 2; D0 += sprite.speed;
        // ball.speed = D0.
        esi = A1;
        spSpeed = (short)Memory.ReadWord(esi + 44);
        D0 = spSpeed;
        D0 = (short)((ushort)D0 >> 2);
        spSpeed = (short)Memory.ReadWord(esi + 44);
        D0 = (short)(D0 + spSpeed);
        esi = A2;
        Memory.WriteWord(esi + 44, D0);

        // 15120-15136 — second pass: D0 = sprite.speed; D0 >>= 2;
        // D0 += sprite.speed; ball.speed = D0.
        esi = A1;
        spSpeed = (short)Memory.ReadWord(esi + 44);
        D0 = spSpeed;
        D0 = (short)((ushort)D0 >> 2);
        spSpeed = (short)Memory.ReadWord(esi + 44);
        D0 = (short)(D0 + spSpeed);
        esi = A2;
        Memory.WriteWord(esi + 44, D0);

    l_halve_player_speed:
        // 15138-15146 — sprite.speed >>= 1; sprite.tackleState = 1.
        esi = A1;
        {
            ushort src = Memory.ReadWord(esi + 44);
            src = (ushort)(src >> 1);
            Memory.WriteWord(esi + 44, src);
        }
        Memory.WriteWord(esi + 96, 1);                          // Sprite.tackleState

        // 15147-15162 — A0 = team.opponentsTeam; A0 = A0.controlledPlayer;
        // if A0 == 0 → @@out.
        esi = A6;
        int eax = Memory.ReadSignedDword(esi + 0);              // TeamGeneralInfo.opponentsTeam
        A0 = eax;
        esi = A0;
        eax = Memory.ReadSignedDword(esi + 32);                 // TeamGeneralInfo.controlledPlayer
        A0 = eax;
        if (A0 == 0) goto l_out;

        // 15164-15175 — cmp [A0+Sprite.ballDistance], 9 ; jb @@out (unsigned).
        esi = A0;
        {
            uint src = Memory.ReadDword(esi + 74);
            if (src < 9u) goto l_out;
        }

        // 15177-15196 — D0 = (sprite.x +2 - opp.x +2); D0 = (int16)D0 * (int16)D0.
        esi = A1;
        short pxS = (short)Memory.ReadWord(esi + 32);
        D0 = pxS;
        esi = A0;
        short oxS = (short)Memory.ReadWord(esi + 32);
        D0 = (short)(D0 - oxS);
        int dxSq = (short)D0 * (short)D0;                       // imul bx (signed 16x16 → 32).
        int D0_32 = dxSq;

        // 15197-15216 — D1 = (sprite.y +2 - opp.y +2); D1 = (int16)D1 * (int16)D1.
        esi = A1;
        short pyS = (short)Memory.ReadWord(esi + 36);
        D1 = pyS;
        esi = A0;
        short oyS = (short)Memory.ReadWord(esi + 36);
        D1 = (short)(D1 - oyS);
        int dySq = (short)D1 * (short)D1;
        int D1_32 = dySq;

        // 15217-15223 — D0 += D1 (32-bit).
        D0_32 = D0_32 + D1_32;

        // 15224-15233 — cmp D0, 32 ; jbe @@out (unsigned compare).
        if ((uint)D0_32 <= 32u) goto l_out;

        // 15235-15237 — PlayGoodTackleComment; sprite.tackleState = TS_GOOD_TACKLE (2).
        StubPlayGoodTackleComment();
        esi = A1;
        Memory.WriteWord(esi + 96, TS_GOOD_TACKLE);             // Sprite.tackleState

    l_out:
        // 15240-15241 — PlayKickSample; resetBothTeamSpinTimers.
        StubPlayKickSample();
        BallUpdate.ResetBothTeamSpinTimers();
    }

    // SWOS::PlayGoodTackleComment / PlayKickSample — audio cues. Audio backend
    // not wired yet. The existing PlayerActions stubs are private; mirror them
    // locally so PlayerTackle stays self-contained.
    private static void StubPlayGoodTackleComment() => OpenSwos.Audio.MatchAudio.GoodTackleComment();
    private static void StubPlayKickSample() => OpenSwos.Audio.MatchAudio.PlayKick();
}
