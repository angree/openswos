# RESUME HERE — Phase B port progress

**Last updated: 2026-07-11 (Session 31 — play-test fixes: writhe animation, keeper-name swap, head-number H2, HEADER ball-contact.)**

## SESSION 31h (2026-07-11) — Linux release + FIRST PUSH TO GITHUB

- **Linux release:** added a `Linux` preset to `game/export_presets.cfg`, exported
  `build/linux/{OpenSWOS.x86_64, OpenSWOS.pck, data_OpenSwos_linuxbsd_x86_64/}`,
  packaged `build/OpenSWOS-linux-x86_64-2026-07-11.zip` (59 MB) with a Linux README
  (chmod +x note). Structurally identical to the verified Windows build; not runtime-
  tested (no Linux env on this machine).
- **First real push (user-authorised):** committed the whole project — 199 files,
  ~162k insertions, commit 6f7cfc8 — and pushed `main` to `github.com/angree/openswos`
  (private). Pre-push audit: `git add` dry-run + staged-set scan confirmed ZERO
  proprietary/build files (gitignore holds). Author = `angree <angree@wp.pl>` (matches
  the repo's existing 2 commits). Stray `player_model_*.glb` gitignored.
  NOTE: contributions count retroactively once the repo is made public, PROVIDED
  the author email used (angree@wp.pl — matching the repo's git config + existing
  commits) is verified on the GitHub account.
- Release .zips live in build/ (gitignored) — NOT attached to a GitHub Release yet
  (offered; awaiting user decision on public/release).

## SESSION 31g (2026-07-11) — fullscreen + first runnable Windows release (#209, #210)

- **Fullscreen (#209):** 3 display modes — Windowed / FullscreenFill (nearest, fills, thin
  bars) / FullscreenInteger (pixel-perfect, black bars). Borderless native-res
  (`Window.ModeEnum.Fullscreen`, NOT exclusive → desktop icons untouched); only
  `ContentScaleStretch` flips Fractional↔Integer. F11 cycles all 3; Alt+Enter toggles
  Windowed↔last-fullscreen (default Fill). OPTIONS "DISPLAY" row + persisted `displayMode`
  in settings.json. Headless-guarded (`DisplayServer.GetName()=="headless"`). Build 0/0,
  smoke PASS.
- **Windows release (#210):** downloaded+installed the 4.6.2 mono export templates
  (`%APPDATA%/Godot/export_templates/4.6.2.stable.mono/`), created `game/export_presets.cfg`
  (Windows Desktop x86_64) + `game/OpenSwos.sln` (REQUIRED — Godot .NET export needs a
  solution or it silently ships without the C# assemblies). Exported →
  `build/win/{OpenSWOS.exe, OpenSWOS.pck, data_OpenSwos_windows_x86_64/}` + README.txt +
  empty original_swos_adf/. Packaged `build/OpenSWOS-win64-2026-07-11.zip` (67 MB).
  VERIFIED: unzipped to a clean folder + dropped ADFs → the exe self-extracts + boots +
  1616 teams + RUNTIME TEST PASSED. build/ is gitignored (not committed); export_presets.cfg
  + OpenSwos.sln are new tracked project files. Templates .tpz cached in scratchpad (gitignored).

## SESSION 31f (2026-07-11) — dedicated optional PC folder (#208)

Added `original_swos_pc/` — an OPTIONAL drop folder for the PC (DOS) `DATA/`. `PcDataDir`
now resolves: `original_swos_pc/` (recursive, non-RNC TEAM.000) → `original_swos_files/`
(back-compat) → dev fallback. Auto-created on first run + noted as optional (only a
slightly larger team list, ~1730 vs ~1616; not required; doesn't change graphics) in
HOW_TO / README / on-screen. Verified end-to-end: Amiga ADFs in `original_swos_adf/` +
PC DATA in `original_swos_pc/` (both dev fallbacks hidden) → importer decodes graphics,
`[Pc] 1730 teams`, "Boot complete. 1730 teams". Build 0/0; dev smoke PASS. Restored+clean.

## SESSION 31e (2026-07-11) — ADF import VERIFIED end-to-end + Amiga-only playability fix (#207)

Real end-to-end test of the import mechanism (dropped the actual SWOS 96/97 ADFs into
`user://original_swos_adf/`, hid the dev fallbacks, ran headless). Result: **the game
decodes the ADFs and boots** — both floppies extracted (SWOS 26 files + SWOS2 99),
pitch + Amiga atlas + goalkeeper atlas + 1616 Amiga teams loaded. The test CAUGHT two
real bugs that would have hit every Amiga-only user (our documented minimum):
1. `DataPaths.PcDataDir()` used `POOLPLYR.DAT` as the "this is PC" marker, but the
   **Amiga data folder also contains POOLPLYR.DAT** → it false-matched the Amiga data
   as PC, PcAssetSource failed to parse the RNC bytes → 0 teams. Fixed: the real marker
   is compression — Amiga TEAM.* are RNC-packed, PC's are raw; PcDataDir now requires a
   NON-RNC TEAM.000 (added `IsRnc`), AmigaDataDir prefers an RNC TEAM.000.
2. `Main.cs` built the menu team list (`_allTeams`) from PC ONLY → Amiga-only →
   "menu cannot proceed". Fixed: fall back to `AmigaAssetSource` teams when PC absent.
Verified: PC present → 1730 teams, smoke 25k PASS (0 stuck); Amiga-ONLY → 1616 teams,
"Boot complete", menu proceeds. Build 0/0. All test scaffolding restored + cleaned.
(Cosmetic leftover: both ADFs get tagged `disk2` during import — harmless, disk2's
grafs win; refine later.)

## SESSION 31d (2026-07-11) — publish prep 2: copyright audit + user-facing ADF import (#206)

- **Copyright audit before GitHub.** Nothing proprietary is committed (34 tracked
  files = code+docs). Extended `.gitignore` to hard-block every proprietary-derived
  artifact from an accidental `git add .`: `game/_research/` (decoded Amiga sprites,
  original palette, a copy of original-amiga-swos.asm, third-party PDFs/Pascal), root
  sprite dumps (`player*.png`, `swos_graphics_sheet*`), `*.RAW/*.256/*.adf`, logs.
  Verified: no risky file is stageable. LICENSE → MIT `Copyright (c) 2026 Grzegorz
  Korycki` + note that MIT covers only our code, not SWOS assets/trademarks. Decision
  (user): keep the RE-based engine approach (OpenTTD model — free code, user supplies
  their own assets); the copied-constants/mechanical-port from swos-port (no licence)
  is the known residual gray area, accepted knowingly.
- **User-facing ADF import.** Folders renamed to `original_swos_adf/` (drop `.adf`) +
  `original_swos_files/` (extracted OR loose files). DataPaths now resolves by
  depth-capped recursive search under those roots (tolerates WHDLoad/loose layouts),
  then exact dev-fallback direct paths (unchanged). Importer scans all `*.adf` by
  content, extracts via AdfDisk to first-writable of exeDir/`user://`. First run with
  no data auto-creates both folders + `HOW_TO_ADD_SWOS_FILES.txt`; on-screen message
  names both options. Required version documented: **SWOS 96/97 Amiga** (94/95 not
  compatible); PC optional. Build 0/0, smoke PASS via dev fallback.

## SESSION 31c (2026-07-11) — publish prep: user-friendly asset loading + README (#205)

Reworked original-asset loading for GitHub/export (was: hardcoded `res://..` repo-root
paths, manual ADF pre-extraction, split across two dirs, broken in exported builds).
Now: `game/scripts/Assets/DataPaths.cs` resolver (priority: `user://swos_data/` cache →
`<exeDir>/swos_data/` next-to-binary → dev fallback = today's exact `assets/extracted` +
`Swos9697_PC` paths) + `game/scripts/Assets/AmigaImporter.cs` (first-run: scans
`<exeDir>/swos_import/`, `user://import/`, dev `Swos9697_Amiga/` for `*.adf`, extracts via
`AdfDisk` — added `AdfReader.cs` to OpenSwos.csproj — into `user://swos_data/`; RNC still
decoded at load so extraction is raw ADF→files). Main.cs routes every asset path through
DataPaths + calls EnsureImported + shows a `ShowFirstRunMessage()` overlay when no data.
Dev fallback preserved 1:1 → build 0/0, smoke loads atlas/pitch/goalie + PASSED.
README.md rewritten (laconic, honest: pitch ~99%, menu/subs are placeholders, bring your
own Amiga files, nothing proprietary bundled). **Which version required: AMIGA** (only
Amiga graphics are decoded; PC `SPRITE.DAT` = #27 would enable PC-or-Amiga choice later).
NOT pushed — awaiting user consent to publish.

## SESSION 31b (2026-07-11) — headers never connected (#204) — FIXED

Fable investigator root-caused the "header whiffs even when perfectly positioned":
the jump-header ball-CONTACT dispatch (`updatePlayers.cpp:7095-7430`) was DEFERRED
during the original port and NEVER landed — our `kJumpHeader` branch only decremented
the timer + decayed speed, never tested contact, never called `PlayerHittingJumpHeader`,
so a jump header could NEVER connect. Inverse bug: `kStaticHeader` called
`PlayerHittingStaticHeader` UNCONDITIONALLY every wind-up tick (false "gates internally"
comment) → static header remote-kicked the ball from anywhere. swos-port matches the
Amiga original (only delta: Amiga timer seed 50 vs PC 55). Fix (one file,
`UpdatePlayers.cs`): new `TickJumpHeader` (1:1 port of cpp:6972-7430 — speed decay,
full pitch-bounds ladder, winded-up gates timer>=42 / gameStatePl==100 / heading==0 /
ballDistance<=64 / ballZ in [8..15], strike commit) + `TickStaticHeader` (gates
cpp:6874-6969) + shared `HeaderExitToNormal`. All helpers were already ported. Build
0/0, smoke 25k PASS (0 stuck, 0 clock-stall). Needs human play-test to confirm contact.



## SESSION 31 (2026-07-11) — three play-test fixes (build 0/0, smoke 25k PASS)

1. **Injured writhe animation (#203) — FIXED at last.** Fable investigator mapped
   it end-to-end and confirmed `swos-port` is FAITHFUL to the Amiga original (our
   reference is NOT corrupted). Authentic model: **2 fixed orientations** (dir<=3 →
   ordinals 413/414 "TopLeft"; dir>=4 → 411/412 "TopRight"), each a 2-frame leg-kick
   flip-flopping every 20 ticks, orientation LOCKED at fall time, NEVER rotating.
   Our VM already emitted this correctly (walker + state-13 rebind suppression). The
   ONLY bug was the renderer: `Main.cs SwosOrdinalToAtlasCell` mapped rel 70-73 onto
   an 8-way `kFallenColByDir` with a `phaseB` adjacent-DIRECTION rock = the
   "flips/wrong-axis" the user reported ~6×. The **real writhe art exists** in the
   CJCTEAM atlas at y=32 cols 7-10 (verified visually). Fix: `PlayerFrames.ExtWritheRowBase=108`,
   4 tiles baked with kit+face recolour, per-ordinal remap (rel70→col7 … rel73→col10),
   stable per-pair anchors (torso doesn't jitter), deleted `kFallenColByDir`/`injuredDir`.
2. **Keeper name attributed to wrong team (#201) — FIXED.** `PlayerNameDisplay.ShowCurrentPlayerName`
   computed `topTeam` by comparing `lastTeam.inGameTeamPtr` against the SWAPPING
   `TopBase` field instead of the STABLE `topTeamInGame` global — so in H2 (after the
   end-swap) the banner showed the opponent's roster (user: opponent keeper displayed
   OUR keeper Dreszer). Now compares against the stable global, matching
   `playerNameDisplay.cpp:96`.
3. **Head-number "1" in H2 (#202) — proven correct + hardened.** A headless oracle
   harness (`--headnum-test`, fixed physical-pool ground truth) proved the current
   resolution is correct across 25k ticks through real HT incl. keeper-claim paths (0
   mismatches). The wrong "1" was masked by a silent clamp in
   `GameSprites.UpdateControlledPlayerNumbers` → replaced with a loud debounced
   diagnostic + safe HIDE (never paints a wrong "1"; logs ordinal/pointer if any
   future fault triggers). Watch next play-test for the diagnostic.

Open follow-up noted by the #202 agent: boot always wires top=team1 regardless of
rolled `teamPlayingUp`, and `ReseatTeamsForNewHalf` swaps unconditionally —
self-consistent for internals but can seat teams on opposite ends vs the original
when `teamPlayingUp==2` at kickoff (Kickoff.cs:318-320). Separate concern.

---

**Prior: 2026-07-11 (Sessions 29-30 COMPLETE — full front-end + competitions/career + play-test fixes; pre-compaction handoff.)**

## 🎯 STATE ON RESUME (read first)

The SwosVm mechanical port plays full matches AND the game now has a complete
OWN front-end. What exists and works (each verified by build 0/0 + the
`--menu-shot` self-driving screenshot walk + the headless battery:
`--swos-smoke`, keeper-release/corner-goalkick/goal-celebration/bench/
competition tests — ALL PASS as of the last run):

- **Menu client** (`game/scripts/Menu/` + `Main.MenuHost.cs` bridge): SWOS-style
  radial-gradient buttons/frames/cursor in the SWOS charset at 384×272. Screens:
  HOME, FRIENDLY (continent/country filter), VERSUS (+comp name/round), STADIUM
  lineups, QUIT confirm, PLAY AGAIN, paged OPTIONS (pager on top; HALF LENGTH
  1/2/3/5/8/10 MIN, GAME SPEED, OPPONENT, PITCH, SKILL SCALING, ALL TEAMS EQUAL,
  PRE MATCH MENUS), TEAM SETUP (squad viewer, faithful 0..7 skills), LOCAL
  MULTIPLAYER. Navigation: DebugFireLabel & friends drive the test harness.
- **Competitions** (`game/scripts/Competition/`): engine (Berger league, cup
  with lazy rounds + simulated pens, tournament groups→KO, deterministic
  xorshift32) + CAREER (league+cup interleaved per season, promotion/relegation,
  trophies/history, manager name/title, RETIRE, MANAGEMENT RECORD) + PRESET
  COMPETITIONS (6 real comps, names verified vs TEAM.* data) + save slots
  (user://saves/, AUTOSAVE alias) + cup DRAW ceremony + CONTINENT→COUNTRY picker
  (NationNames.cs — real tables from swos.asm). Result loop: PLAY NEXT MATCH →
  real sim match → FullTime accept → RecordResult + SimulateAiRound + autosave.
- **Faithful skill pipeline** (task #196, toggleable): mod-8 nibble decode,
  GetPlayerPrice + ScaleSkill price/team feedback (SkillScaling.cs), keeper
  skill from PRICE. Per-player FACE skin/hair recolor (positionAndFace bits 3-4;
  convert tables swos.asm:218199-218215) via KitPalette.ApplyFace + per-face
  tile caches (FaceForSlot).
- **Recent play-test fixes (S30b/c)**: head-number +28 misalign (InGameTeamPtr
  IS players[0] — recurring port-layout pattern, markedPlayer at base-22),
  scorer-list ResetResult per match, P pause key, injured writhe (state-13
  re-bind suppression + direction-aware fallen pose with 2-frame rocking),
  header per-tick speed decay (jump −72 then shr 1; static −16).
- **Open follow-ups**: #198 class-C subsystems (replays, audio, input rebind,
  PC/AMIGA style, top scorers = per-player goal attribution, EDIT TACTICS/TEAMS,
  pitch-type physics, transfers/finances #199), #164 real small-digit + writhe
  sprites from SPRITE.DAT, #171/#176/#182, older #27/#72/#73/#89/#101/#102.
  User still feels the game slightly TOO EASY (won 6-1 with Zaglebie) — no
  root-caused mechanism yet; skill scaling ON may help, watch next play-tests.
- Harness gotchas learned: frame counts are useless as timeouts (uncapped fps —
  use wall seconds); the result-poll lives in the 70 Hz physics tick (wait ≥1 s
  before post-match asserts); a screenshot taken in the mutation frame shows the
  PREVIOUS render (mutate at f==3, shoot at f>=10).

Session log (newest first) follows.

## SESSION 30c — header fly-away + writhe axis/motion (play-test round 2)

1. **Header launched the player across the pitch** — the header START aims at
   kDefaultDestinations (~1000px direction vector, never meant to be reached);
   the original makes the hop SHORT via PER-TICK SPEED DECAY which we had
   skipped when deferring the impact dispatch: jump header −72/tick
   (kPlayerAirConstant, swos.asm:203807) above the anim threshold then
   `shr speed,1` (updatePlayers.cpp:6996-7093); static header −16/tick
   (:6853-6870). Both decays now ported in UpdatePlayers.cs kJumpHeader/
   kStaticHeader flight branches. Impact dispatch (:7095-7359) remains
   deferred (gates on ball arrival, not travel).
2. **Injured writhe: opposite axis + no motion** — mapping v3: the renderer now
   picks the lying pose from the sprite's startingDirection (frozen at the
   injury instant thanks to the 30b state-13 re-bind suppression) and the
   stream's two frames alternate between that direction's fallen pose and the
   neighbouring one — a 20-tick rocking that reads as writhing on the correct
   fall axis. Still PORT-VISUAL (Amiga sheet has no true writhe art; real
   411-414 frames await SPRITE.DAT decode, #164).
Verified: build 0/0, smoke 25000 (0-1 — both header paths exercised), full
battery PASS. Backup: `session30c_header_decay_writhe_axis`.

## SESSION 30b — career play-test bug batch (#200, all fixed)

User played a career (Zaglebie Lubin) and reported 5 issues; all fixed + verified
(battery green, hair verified on a zoomed screenshot):

1. **Head numbers showed "1"** — GameSprites read PlayerInfo through a +28
   TeamGame header offset, but OUR InGameTeamPtr points at players[0] DIRECTLY
   (WireTeamFields) → shirt bytes read from a neighbour's name field. Fixed the
   read + the equally-misaligned markedPlayer (now base-22, the Bench.cs
   convention). ANOTHER instance of the port-layout mismatch pattern.
2. **Everyone rendered GINGER** — the raw sprite palette's hair slots (9/12/13)
   sit on the flesh/orange ramp; the original REMAPS palette indices per player
   FACE (white/ginger/black — positionAndFace bits 3-4, swos.asm:84815-84818;
   convert tables swos.asm:218199-218215). Now: TeamFile parses Face,
   KitPalette.ApplyFace ports the remap, AmigaSpriteAtlas.WithFaceRecolour +
   per-face tile caches in Main (FaceForSlot resolves via PlayerInfo.index so
   substitutions keep the right face).
3. **Career transfers absent** — task #199 (needs the economy subsystem, #198).
4. **Scorer list bled into the next match** — Result.cs scorer arrays are C#
   statics and NOBODY called ResetResult (result.cpp:104-118); now called from
   InitSwosVmFromMatchSetup every match.
5. **No P pause** — runtime InputMap action: P pauses, P/space resumes, ESC to
   menu.
6. **Injured writhe faced the wrong way + flipped** — the port-only direction
   re-bind in SetNextPlayerFrame (absent in swos.asm:102834) re-selected the
   writhe stream every time the downed player's facing drifted toward the ball;
   state 13 (PL_ROLLING_INJURED) joined the goalie-save suppression set
   (#178/#179 pattern).

## SESSION 30 — MENU GAP-FILL vs original (deep analysis + waves M1/M2)

A deep gap analysis vs the original menu tree (all .mnu files + swos.asm screen
strings) produced a class-A/B/C fill plan; everything class A+B is now BUILT
(3 agents: store/presets, menu wave 1, menu wave 2; orchestrator wired Main):

- **Pre-match flow**: QUIT confirm (quit.mnu), post-friendly PLAY AGAIN
  (replayExit.mnu), versus screen with competition name + round, STADIUM
  lineups screen (stadium.mnu — both XIs + bench + kit swatches), cup
  "NEXT ROUND DRAW" ceremony (pairings, player line gold), all gated by the
  new PRE MATCH MENUS option (original "show pre-match menus").
- **Career identity**: MANAGER NAME text-entry screen (typed input widget —
  physical-key polling with edge detection; LEFT/RIGHT flips MR/MS),
  MANAGEMENT RECORD screen (season P/W/D/L/GF/GA + trophies + history),
  RETIRE flow (confirm → history line → CAREER OVER record; retired dashboard
  reduced to RECORD/ABANDON/BACK), manager banner on the dashboard.
- **PRESET COMPETITION** (PresetCompetitionMenu swos.asm:23781): 6 presets
  with entrant names grep-verified against real TEAM.* data (EUROPEAN CUP,
  UEFA CUP 32, CUP WINNERS CUP, WORLD CUP 4x4, EURO 4x4, COPA AMERICA 2x4) —
  data in Competition/PresetCompetitions.cs, menu resolves names→teams with
  spare-name + random fill.
- **Save slots**: CompetitionStore.SaveAs/LoadSlot/DeleteSlot/ListSlots at
  user://saves/ (AUTOSAVE aliases the legacy slot); dashboard SAVE AS (name
  entry), hub LOAD GAME browser with DELETE MODE toggle.
- **Options**: ALL TEAMS EQUAL (wires the already-ported SkillScaling
  equalizer, swos.asm:35761-35825) + PRE MATCH MENUS; both persisted in
  settings.json. VIEW WORLD: tournament TABLE screen got a GROUP A-D stepper.
- Class C backlog (NO menu stubs; needs subsystems first) tracked as task
  #198: replays/highlights, audio options, input rebinding + SELECT MATCH
  CONTROLS + PC/AMIGA style, top scorers (needs per-player goal attribution),
  EDIT TACTICS, EDIT CUSTOM TEAMS, pitch-type physics, transfers/finances.
- Verified: build 0/0; --menu-shot walk incl. MANAGER NAME (confirmed via new
  DebugText* hooks), comp versus, stadium lineups, career dashboard with
  RECORD/SAVE AS/RETIRE + "MR PLAYER" banner; career E2E match still records
  (ROUND 1/30→2/30); full battery + competition-test PASS. Backup:
  `session30_menu_gapfill_complete`.

## SESSION 29d — CONTINENT/COUNTRY picker + half lengths + OPTIONS layout

User feedback fixes (all verified via --menu-shot):
- **NATION PICKER (original-style)**: new `Assets/NationNames.cs` — the REAL
  country table from swos.asm (CN_ enum 1259-1329, name records 186298-186527,
  countriesTable 186528-186614; continent membership from each record's leading
  byte — no geography guessing; index 9 = duplicate ENGLAND, 72 = CUSTOM).
  Every competition setup (league/cup/tournament/CAREER) now has CONTINENT →
  COUNTRY ("ITALY (86)"-style with team count) → YOUR TEAM (steps only that
  country's clubs). FRIENDLY + LOCAL MP gained a COUNTRY filter row ("ALL" =
  master list) backed by new IMenuHost.SetHomeTeam/SetAwayTeam. Verified: league
  created in LATVIA (AUSEKLIS, SKONTO RIGA... on the dashboard).
- **HALF LENGTH**: presets now 1/2/3/5/8/10 MIN REAL (was 30s..10min), default
  3 MIN like the original.
- **OPTIONS**: single page again — PAGE pager row moved to the TOP, Header-style
  (distinct colour), shows 1/1; page-builder array kept so page 2 is one line.
- Harness gotcha #3: a screenshot taken in the same _Process frame as a menu
  mutation captures the PREVIOUS render — mutate at f==3, shoot at f>=10.

## SESSION 29c — SKILL SCALING (task #196) + menu overlap fixes + paged OPTIONS

Follow-up to the skills investigation (why all-F plays worse in SWOS):
- **Faithful skill pipeline ported, as a TOGGLE (default ON)** — new
  `Sim/Port/SkillScaling.cs`: GetPlayerPrice (position-quadrant ponders,
  swos.asm:129720-129838), price-percent (CPU pinned to 100, ±12 Rand2 spread
  per player, swos.asm:37670-37727/37959-37981), ScaleSkill (ratio clamp
  75..125 × teamAppPercent, stochastic rounding, swos.asm:37986-38054),
  teamAppPercent equalizer (only under "all player teams equal",
  swos.asm:35761-35825; exposed as SkillScaling.AllPlayerTeamsEqual, off).
  TeamDataLoader routes starting-XI skills through it; bench + OFF path = raw.
- **Decoder now faithful**: TeamFile.cs decodes skill nibbles as MOD 8 (0..7);
  the old (raw&7)+1 inflated every skill by +1 vs the original engine. Skill
  displays now show 0..7 like the original (keepers show 0s — their nibbles
  are filler; keeper ability comes from the PRICE byte).
- **Menu**: two-tier layout (normal → compact) kills the overlapping panels
  (TEAM SETUP, dashboard); all body painters use dynamic MenuScreen.BodyTop.
  OPTIONS is now PAGED (PAGE 1/2 pager, trivially extensible): page 1 = match
  settings, page 2 = SKILL SCALING ON/OFF (persisted in settings.json,
  "skillScaling", default true).
- Verified: build 0/0; skill-sum probe ON 340/323 vs OFF 346/322 (no
  systematic nerf — ~1-2% stochastic spread as designed); smoke 25k/30k PASS
  both modes (single-run scorelines differ — RNG stream shifts at load, not a
  regression); full battery + competition-test PASS; menu-shot walk incl.
  options pages + fixed TEAM SETUP visually verified. Backup:
  `session29c_skillscaling_pagedoptions`.

## SESSION 29b — COMPETITIONS + CAREER + LOCAL MP (no more stubs)

User directive (now in CLAUDE.md): NEVER ship "COMING SOON" stubs — build every
requested feature to completion. Delivered via 3-agent orchestration
(Explore = source analysis, 2 × general-purpose implementers on disjoint files;
orchestrator owned Main.cs/Main.MenuHost.cs + integration + build/test loop):

- `game/scripts/Competition/` — **CompetitionModels.cs** (contract/POCOs),
  **CompetitionEngine.cs** (Berger round-robin league incl. odd-count byes,
  single-elim cup 4/8/16/32 with lazy next rounds + simulated penalty shootouts,
  tournament groups→knockout with cross-pairing, CAREER = league + domestic cup
  interleaved per season with promotion/relegation rollover + trophies/history,
  deterministic xorshift32, strength-weighted Poisson AI results),
  **CompetitionStore.cs** (JSON save at user://competition.json, one active slot).
- Menu: COMPETITIONS hub (NEW LEAGUE/CUP/TOURNAMENT/CAREER + CONTINUE), setup
  screens, DASHBOARD (round label, player summary, mini-table with gold player
  row / group table / knockout fixture list, PLAY NEXT MATCH / CONTINUE /
  NEXT SEASON / TABLE / FIXTURES / ABANDON), full TABLE + FIXTURES screens,
  LOCAL MULTIPLAYER screen (P2 = WASD). HOME shows a CONTINUE banner when a
  save exists. `BuildComingSoon` deleted.
- Main wiring: `StartCompetitionMatch` (human always home slot),
  `TakeLastCompetitionResult` (captured at FullTime→Menu accept; ESC-abandon
  keeps the fixture unplayed), team info + entrant pool queries.
- Original-source analysis (Explore agent, citations in transcript): DIY had
  league/cup/tournament with 2/3-pts, legs, ET/pens/replays, away-goals; AI
  results = strength-diff + 2 home bonus into RNG probability tables
  (CalculateViewResult, swos.asm:32548); career = 20 seasons × 106-byte ledger
  (docs/SWOS/career.txt). Faithful follow-ups: probability-table port for AI
  results, 2-legged ties, replays, presets (Euro/World Cup), job offers.
- Verification: build 0/0; `--competition-test` headless 18/18 OK;
  `--menu-shot` E2E walk — creates a league (56 fixtures) AND a career
  (248 fixtures), plays a real career match to FULL TIME (0-2), result recorded,
  AI round simulated, ROUND 1/30→2/30, table shows points, "YOU ARE 14TH";
  full regression battery PASS (smoke 5000 / keeper-release / corner-goalkick /
  goal-celebration / bench). Backup: `session29_competitions_career_complete`.
- Harness gotcha fixed twice: `_Process` frame counts are useless as timeouts
  (uncapped fps) — use wall-clock seconds; and the result-poll lives in the
  70 Hz physics tick, so post-match dashboard checks must wait ≥1 s wall.

## SESSION 29a — OpenSWOS menu client (our menu instead of SWOS's)

Built a from-scratch, SWOS-styled front-end that replaces the old plain-text
menu and launches real matches. **Full write-up: `docs/design/02-menu-client-implementation.md`**
(design/vision: `docs/design/01-menu-and-game-modes.md`).

- New, self-contained: `game/scripts/Menu/{MenuTheme,MenuModels,MenuClient}.cs`
  + `game/scripts/Main.MenuHost.cs` (Main's `IMenuHost` bridge, a partial).
  `Main.cs` edits are minimal (field, `_Ready` create, route menu tick + render,
  add `_Process`/`--menu-shot` harness).
- Screens: HOME → PLAY MATCH → FRIENDLY setup → VERSUS → live match; plus
  TEAM SETUP (squad table + kit swatches), OPTIONS, and COMING SOON stubs for
  Competitions/Multiplayer. SWOS-look radial-gradient buttons + beveled frames +
  flashing gold cursor + control-hint footer, all in the SWOS charset, colours
  "similar but different" from the original.
- Verify visually with `--menu-shot <dir>` (windowed; writes `01_home…07_match`
  PNGs then quits). 2026-07-10 run: all screens render, cursor visible, full
  HOME→PLAY→VERSUS→match flow launches a real match; `--swos-smoke` still passes
  (sim untouched). Build 0/0.
- TEAM SETUP is read-only for now; OPTIONS only exposes sim-supported settings.
  The legacy `MenuOverlayText/TickMenu/_menuLabel` path is dead but kept as a
  compile-time fallback (safe to delete later).

---

**Session 28 (2026-07-10) — 10 play-test-driven bug waves; pre-compaction handoff.**

## STATE ON RESUME AS OF SESSION 28 (historical — superseded by the block at the top)

The SwosVm mechanical port is the ACTIVE path and the game plays full matches
end-to-end. Session 28 was a long, user-play-test-driven bug-fixing marathon:
the user played the GUI build via `I:\GITHUB\W_OPEN_SWOS\tools\test-swos-port.bat`
and reported issues in Polish; each was fixed by comparing 1:1 to
`external/swos-port`. Ten waves landed (#153-#190) — full details in the wave
logs below. Verification battery (all green after every wave): build 0/0,
`--swos-smoke 15000/25000/30000` (0 stuck/stall), `--keeper-release-test`,
`--corner-goalkick-test`, `--goal-celebration-test`, `--bench-test`.

Latest backup: `I:\GITHUB\W_OPEN_SWOS\.backups\20260705_133920_session28j_cpuaim_penalty_injuryframes_benchinjury.7z`
(a fresh pre-compaction backup is taken at the end of this update).

**Recurring bug pattern this session (watch for more): inverted signs /
mirrored directions / hardcoded-side assumptions** — many fixes were a single
flipped condition. Confirmed instances: foul-team assignment, spin-pass lead
negation, top/bottom tactics mirroring, running-SE + slide-SE dest clamps,
keeper-catch keeper-by-side (HT-swap regression), and the big one — a port-only
"reaim to goal centre" crutch that made the CPU shoot into the keeper (see
wave 10). When a new "X goes the wrong way" report comes in, suspect a sign/side
inversion first and instrument before guessing.

**Biggest open QUALITY item (not a crash, a balance issue):** a MILD residual
positional bias — the bottom goal is ~35% vs 21% easier to attack; the
top-attacking team's off-ball advancement stalls near y≈244. Root: off-ball
advancement in `PlayerHeader.SetPlayerWithNoBallDestination` (per the wave-10
AI-aim agent). Not an inversion; a future balance pass.

**Open follow-ups (deferred, not blocking play):**
- #182 — opponent player camped by the keeper: red-card send-off path verified
  faithful; likely just a frozen attacker during the goal result panel. PENDING
  the user confirming whether it also happens in LIVE play (= box-camp).
- #176 — verify scorer-list team attribution survives the HT end-swap
  (physical vs positional team; scorers work, this is an edge check).
- #171 — add a card-foul harness flag (smoke is structurally card-free: cards
  only fire with a human team, so AI-vs-AI smoke never exercises bookings).
- ART stand-ins to replace with real decoded sprites: referee + yellow/red card
  (procedural), injury red-cross (procedural), player-number small digits
  (#164, SPRITE.DAT 1188-1203 not decoded), big score digits (charset font).
- Dead telemetry counters `s_reaimApplied*` / `s_kickFallback*` in
  UpdatePlayers.cs are now unused but still referenced by Main.cs smoke
  reporting — strip both together in a cleanup pass.
- Legacy hand-tuned AiSim still compiles but is unreferenced (port is forced on,
  physics/difficulty menu slots removed in wave 9) — candidate for deletion.
- Older: #27 (sprite descriptor table), #89 (B6 dribble/ball-control),
  #72/#73 (kick-types / sprite-anchor audits), #101/#102 (C# idiom / BallSim
  split), #149-era art.

**Editable graphics sheet (user is reworking art):**
`I:\GITHUB\W_OPEN_SWOS\swos_graphics_sheet.bmp` (+ `.offsets.txt`) — copied to
repo root on request; source at `assets\extracted\graphics\`; regenerate via
`dotnet run --project tools\atlas-export`. NO reverse-importer yet (edited sheet
can't be loaded back into the game) — build one when the user starts wanting
edits in-game.

---

## ✅ Session 28 wave 10 (2026-07-05) — THE "10-0 too easy" ROOT CAUSE + penalties + injury frames + bench injury

1. **CPU never scored / "10-0 too easy" — ROOT CAUSE FOUND (#188).** A port-only
   "goal-ward reaim" crutch in PlayerControlled.cs (cseg_8141C, ran right before
   PlayerKickingBall) overrode EVERY controlled kick's direction to aim at the
   goal CENTRE (336, 129|769) — exactly where the keeper stands. So every CPU
   shot went dead-centre and was saved → CPU scored 0. The aim was NEVER
   inverted (that hypothesis disproven by logging 259 kicks both halves — all
   pointed at the correct enemy goal, HT swap correct). The reaim was the whole
   problem. REMOVED it; the faithful AI (AiBrain.SetControlsDirection +
   AiHelpers.AI_SetDirectionTowardOpponentsGoal) already aims correctly. After:
   233/234 kicks aim correctly, shots spread across the mouth. **AI-vs-AI 30k
   smoke now 2-2 (both teams score); was one-way 1-0.**
2. **Penalties (#189):** the SAME reaim forced the human penalty to the goal
   centre → "shot right → ball centre." The rest of the penalty flow is a
   faithful port with NO inversion (spot 336,711/336,187, keeper dive masks
   56/131 mirrored per goal, TickPenalty dive pick, GoalkeeperDeflectedBall Y
   always into pitch + random X jitter = the "deflect left"). Removing the
   reaim restores the taker's real aim. (Verified at code level; no live
   headless penalty trace possible.)
3. **Injury writhe frames (#187):** streams were correct but the Amiga sheet has
   NO distinct writhe art (only 8 fallen poses); the renderer mapped a stream's
   two consecutive frames to two OPPOSITE diagonals (SW/SE) → blink. Collapsed
   each stream to a single stable on-ground pose (Main.cs rel 70-73).
4. **Bench injury marker (#190):** the subs menu didn't flag injured players.
   Added an " INJ" tag to injured rows — scan the benching team's 11 sprites for
   injuryLevel>0/-2, resolve → shirt number via the fixed physical arrays, match
   by shirt in FormatBenchPlayerRow (drawBench.cpp:318-325 draws a red cross;
   text tag is our PORT-VISUAL stand-in).

Verification: build 0/0; --swos-smoke 30000 (both halves) PASS 0/0 score 2-2;
--keeper-release-test PASS; --corner-goalkick 4/4; --bench-test PASS.
Backup: `I:\GITHUB\W_OPEN_SWOS\.backups\20260705_133920_session28j_cpuaim_penalty_injuryframes_benchinjury.7z`
Follow-ups noted by the AI-aim agent: (a) dead telemetry counters
s_reaimApplied*/s_kickFallback* in UpdatePlayers.cs are referenced by Main.cs
smoke reporting — strip both together later; (b) a MILD residual positional
bias remains (bottom goal ~35% vs 21% ball-time; top attacker stalls ~y244) in
PlayerHeader.SetPlayerWithNoBallDestination off-ball advancement — not an
inversion, a future balance pass. Also open: #182, #176, authentic
referee/card + injury-cross art, #171 card-foul harness.

## ✅ Session 28 wave 9 (2026-07-05) — injuries + kit clash + menu cleanup + keeper-catch dart

1. **Keeper-catch ball darted to the halfway line (#186) — a HALF-TIME-SWAP
   REGRESSION.** DoGoalkeeperSprites (GameLoop.cs) hard-coded top→goalie1 /
   bottom→goalie2. After the wave-6 end-swap, in H2 the bottom team IS team1
   (keeper=goalie1Sprite), so a catch pinned the ball to the OPPOSITE keeper
   ~600px up-pitch for the ~15-tick catch anim, then snapped back. Fixed to
   select the keeper sprite by teamNumber (swos.asm:110907-110978), like the
   original. 40000-tick smoke: 0 gs=3 teleports (was 8 dart/snap pairs).
2. **Injuries (#184):** the state machine (UpdatePlayers.cs PL_ROLLING_INJURED
   at :3404-3418) + roll (PlayerTackle.cs) were already correct, but
   kPlInjuredAnimTable was stubbed to STANDING frames → injured player stood
   up. Added the real writhe streams (ordinals 411-414 / 714-717, swos.asm:
   219566-219582) to AnimationTablesData.cs, mapped rel 70-73 → on-ground
   poses in Main.cs, and added an on-pitch red-cross overlay (PORT-VISUAL — the
   original shows the cross only in the bench menu). Injured player now writhes
   then recovers.
3. **Kit clash (#185) — and a KIT-PARSE BUG.** tools/team-decode/TeamFile.cs
   read the primary kit at the wrong offset (+0x1C straddled) and away as only
   3 bytes — team colours were scrambled (right before only by KitPalette
   calibration). Fixed to the true TeamFileHeader offsets (primary @0x1A/5B,
   secondary @0x1F/5B, tactics @0x18), rewrote KitPalette.Apply for the
   canonical {type,stripes,basic,shorts,socks} layout (Arsenal still red-body/
   white-sleeves, now correct RED socks), fixed the GK-kit synth offsets, and
   added KitClash.cs — faithful port of SetInGameTeamsPrimaryColors +
   GetTeamKitsSimilarityFactor (swos.asm:36165-37237, conflict tables
   217863/217878/217891). ApplyMatchSetup now uses the real AWAY kit on a
   clash (deleted the synthetic-recolor hack + KitsLookAlike). User's
   white/red vs white/blue → factor 77 → away switches to change kit.
   NOTE: KitPalette rewrite changes ALL teams' rendered colours toward
   correctness — visually confirm Arsenal red/white in-game.
4. **Menu cleanup (#183):** removed the AI-difficulty slot (fed only the dead
   legacy AiSim) and the physics toggle (legacy path abandoned) — MenuSlotCount
   8→6 (pitch/home/away/opponent/length/speed), _useSwosPort hardcoded true,
   toggle persistence removed. Removed the dead FT "kicks 0:0 / possession 0%"
   overlay (scoreline+scorers retained). Legacy AiSim kept compiling but
   unreferenced.

Verification: build 0/0; --swos-smoke 25000 (crosses HT) PASS 0/0;
--keeper-release-test PASS; --corner-goalkick 4/4; --goal-celebration PASS.
Backup: `I:\GITHUB\W_OPEN_SWOS\.backups\20260705_012325_session28i_injuries_kitclash_menuclean_keepercatch.7z`
Open follow-ups: #182 (opponent by keeper — pending live-vs-panel confirm),
#176 (scorer attribution across HT swap), authentic referee/card + injury-cross
art (currently procedural stand-ins), #171 (card-foul harness flag).

## ✅ Session 28 wave 8 (2026-07-04) — keeper dive anim (real fix) + diagonal slide + player fall + referee/cards

1. **Keeper dive/parry/deflect animation was frozen on the STANDING pose (#178,
   #179 — user's most-repeated bug). ROOT CAUSE: a port-only "direction-change
   re-bind" hack in SpriteUpdate.cs (SetNextPlayerFrame).** The goalie jumping
   tables (AnimationTablesData.cs:547-562, real dive ordinals 971-998/1087-1114
   from swos.asm:218576-218650) populate ONLY dirs 2/6. As the keeper drifts
   into his dive, RecomputeSpriteDeltas faithfully rewrites his facing (0,1,3…);
   the re-bind hack then re-installed the anim table for the new dir → hit a
   NULL slot → SetPlayerAnimationTable zeroed frameIndicesTable → walker bailed
   → renderer fell to the LEGACY standing pick. The original SetNextPlayerFrame
   (swos.asm:102834) never re-binds mid-stream. FIX: suppress the re-bind for
   goalie save states (4/6/7/11) in SpriteUpdate.cs so the installed dive/catch
   stream walks to completion. All save types now animate (dive high/low,
   in-place parry=small dive, catch, both keepers) — proven tick-by-tick. The
   "punch-to-corner in place stays standing" case was the same small-dive bug
   (goalkeeperDeflectedBall has a single call site inside the catch handler,
   already rendered catch frames correctly).
2. **Diagonal slide tackles (#180): all 4 diagonals broke** (SE→straight down
   etc.). PlayerBeginTackling (PlayerTackle.cs) clamped destX/destY to
   [81,590]×[129,769] INDEPENDENTLY → rotated the slide vector toward the
   nearer cardinal (cardinals unaffected: one component is 0). Replaced with a
   DIRECTION-PRESERVING clamp (scale the whole vector uniformly to the pitch
   edge). Probe: SE +247/+247, SW −150/+150, NE +335/−335 (exact 45°). Same
   class as the running-SE clamp removed in wave 6.
3. **Tripped player stayed standing (#177):** sim was already correct
   (PlayerTackle.PlayerTackled sets PL_TACKLED + tackled anim; TickTackledPlayer
   counts down + recovers). Pure RENDER gap: Main.SwosOrdinalToAtlasCell
   returned null for the fall ordinals 403-410/706-713 (+SW tumble 417-424) →
   legacy standing pick. Mapped them to PlayerFrames.Fallen row-1 cells. Player
   now falls then gets up.
4. **Referee + cards not drawn (#181):** sim FSM was fine but the referee anim
   tables were zero-filled in the VM. Added the 6 referee frame streams
   (ordinals 1273-1283: running/standing/yellow-card 1280/red-card 1281/wave)
   from swos.asm:218660-218676/219599-219650 to AnimationTablesData.cs + a
   WriteRefTable helper; added read-only render accessors to Referee.cs; Main.cs
   now draws _refereeSprite + _refereeCardSprite (yellow/red by whichCard),
   z-ordered like a player. Referee/card art is a procedural stand-in (TODO:
   real CJCGRAFS art) — same precedent as the shirt-number sprites.
5. **#182 (opponent camped by keeper in the goal-panel screenshot):** red-card
   send-off path verified faithful (SendPlayerAway sets sentAway=1 + off-pitch
   dest; all positioning/control/AI skip sentAway). Likely a legitimate frozen
   attacker during the result panel, NOT a red-card glitch; left OPEN pending
   whether it also happens in live play (= the box-camp/keeper-not-clearing).

Verification: build 0/0; --swos-smoke 15000 PASS 0/0; --keeper-release-test
PASS; --corner-goalkick-test 4/4. Backup:
`I:\GITHUB\W_OPEN_SWOS\.backups\20260704_202954_session28h_keeperdive_slide_fall_referee.7z`
Note: --referee-fsm-test default 600 ticks is pre-existingly too short (passes
at 1500); test-duration only, unrelated to this work.

## ✅ Session 28 wave 7 (2026-07-03) — half-time prolong bounded + ceremony detector

- **Half-time hang at 45:00 (#149) bounded.** ProlongLastMinute/UpdateGameTime
  are faithful (gameTime.cpp:60-98/178-196; exit = gsPl==100 && ball outside
  box && lastTeamPlayed!=attacker, held for one 55-tick endGameCounter). Real
  cause: the ball CAMPS in the penalty box (ballY>682) so ballInsidePenaltyArea
  stays true and the faithful exit never fires — that's the keeper-scramble
  open task #153. The port-only safety cap (deleted 2026-07-02) was re-added,
  tighter: the ProlongLastMinute reset is gated `&& s_stoppageRealTicks <
  kProlongBackstopTicks(60)` → total prolong ≈115 ticks, seed-independent (was
  415-5200). Remove the guard once #153 lands (keeper clears the box → faithful
  exit fires first). GameTime.cs.
- **Smoke detector: exclude the whole period-end ceremony.** stuck + clock-stall
  detectors now skip while gameState ∈ 21..30 (HT/FT result+stats+walkout) —
  SWOS legitimately pins the clock at 45/90 and freezes sprites there for
  ~1500 ticks; res_showResult only covered the 275-tick result sub-panel.
  Main.cs.

Verification: build 0/0; --swos-smoke 25000 (crosses HT into H2) PASS **0/0**;
--swos-smoke 15000 PASS 0/0; --keeper-release-test PASS; --corner-goalkick 4/4.
Backup: `I:\GITHUB\W_OPEN_SWOS\.backups\20260703_192445_session28g_halftime_prolong_bounded.7z`
Still open: #153 (ball camps in box → keeper doesn't clear) is the true fix for
both the prolong length AND the earlier keeper-hold camping.

## ✅ Session 28 wave 6 (2026-07-03) — SE-run + half-time end-swap + result panel + scorers

1. **Cannot run bottom-right / SE (#173):** PlayerControlled.cs cseg_81793 had
   a port-only defensive dest clamp to [81,590]×[129,769]; the original
   (updatePlayers.cpp:5733-5778) writes dest=pos+kDefaultDestinations[dir] with
   NO clamp. The clamp skewed the SE angle → CalculateDeltaXAndY resolved it to
   pure S/E, and for x∈{591,592} pulled dest.x below pos → negative deltaX (ran
   LEFT). Removed the clamp (safe: cseg_81793 runs only for the controlled
   player, re-entered every tick as the original relies on). All 8 directions
   now yield correct diagonal delta signs.
2. **Teams don't switch ends at half-time (#173→#174, was #148):** the
   game.cpp:422-548 per-team pointer reseat (teamPlayingUp==2 end-swap) was
   never ported — only teamPlayingUp flipped. Added
   Kickoff.ReseatTeamsForNewHalf() (swaps OffInGameTeamPtr/TeamStatsPtr/
   PlayerNumber/PlayerCoachNumber/IsPlCoach/Players/TeamNumber/Tactics/
   ShotChanceTable between TopBase/BottomBase + game.cpp:479-508 reset), called
   from GameLoop.SetCameraMovingToShowerState at the InitTeamsData point. H2
   now mirrors positions (team1 keeper top→bottom) and flips attacking goals,
   control end, goal crediting — all via the swapped fields. Closes #148.
3. **Result panel (#175):** score was 5px ABOVE the club names (kBigResultDigitY
   compensation that only applies to the undecoded big-digit SPRITES, not our
   charset font) → moved score Y to kTeamNameY (one baseline). Scorers were
   missing because Result.RegisterScorer read topTeamPtr/bottomTeamPtr (never
   written → 0 → early return); switched to topTeamInGame/bottomTeamInGame
   (players[0] base, same as UpdateGoals:113-115) + neutralised the numOwnGoals
   write (no reader; +40 from players[0] would corrupt the keeper's name).
   Renderer-side scorer-line composition added in Main.cs (BuildScorerLines).
   Verified live: team1 shirt=8 (Ian Wright) registers with minute.
4. **Font baked shadow (earlier this wave):** charset glyphs carry face idx 2 +
   baked shadow idx 8; renderer now recolours only the face (was painting both
   → white ghosting). Player-name banner moved top-right.
5. **Smoke detector:** stuck + clock-stall detectors now skip while the result
   panel is up (Result.ShouldDrawResult()).

Verification: build 0/0; --swos-smoke 15000 PASS (0 stuck/stall);
--goal-celebration-test PASS; --keeper-release-test PASS; --corner-goalkick 4/4.
NOTE: a ≥45:00 run (25000-tick smoke) still flags stuck/clock-stall during the
HALF-TIME injury-time prolong — that is the OPEN #149 (ProlongLastMinute exit
takes ~1000-5200 ticks at 45:00; the clock visibly hangs at half-time). Now the
top remaining half-time defect since teams reach/cross HT correctly.
Backup: `I:\GITHUB\W_OPEN_SWOS\.backups\20260703_185724_session28f_SErun_htswap_scorers_panel.7z`

## ✅ Session 28 wave 5 (2026-07-03) — KEEPER SAVE CHAIN + spin-pass lead + font baked shadow

1. **Keeper concedes straight shots — ROOT CAUSE FOUND & FIXED (#71 deep audit).**
   The entire keeper in-area positioning chain (updatePlayers.cpp:1143-2070)
   was missing: predicted-landing chase, strikeDest l_goal_attempt routing,
   close-shot chase, goal-mouth mirroring ((ball.x-336)/2+336), defensive-line
   intercept, fall-backs. Keeper never stepped sideways to the shot line;
   dive fired only in the -10..0 desperation window with deltaX=0 →
   framesNeeded=0 veto (:10681-10693). Also: cseg_7FCD0 catch chain (:2605-2778)
   had NO caller (state-4 unreachable); invented fallback dive (dir=facing);
   shot-chain exits flattened to bool (lost cseg_7FC48/7FC01/7FBEF dest+speed
   side effects); dive collision "simplified" box ignored the skill-weighted
   3-way verdict (:3934-4024, row elems 21/22) and even converted RNG goals
   into claims; penalty dive speed-flag swap (cseg_7FB1B). All ported 1:1:
   new RunGoalkeeperInAreaChain (:1143-2940), full TickGoalieDiving collision
   (:3537-4396), RunShotTripWire/RunShotAtGoal split, 3 new data seeds.
   **Measured: 40 straight shots at skill-7 keeper: BEFORE 27 goals / AFTER 0
   (29 saves); skill-0: 4 goals — skill sensitivity confirmed.** Known cited
   stub: dive X-span nominal pixWidth=24 (SpriteGraphics tables TODO).
2. **Spin/aftertouch pass receiver ran away (#172):** cseg_829AC negation
   inverted (asm `jl` SKIPS neg → negate when D0>=0; we negated when D0<0) —
   kBallFriction lead waypoint pointed to the wrong side of the ball's travel
   line. Probe: 276/277 lead ticks correct after (1/277 before).
3. **Charset font baked shadow (#167-#169 follow-up):** glyphs carry face
   (palette idx 2) + baked black shadow (idx 8); we painted BOTH in text
   colour → white ghosting on every HUD string. Renderer now recolours only
   the face; shadow stays black. Player-name banner moved top-right
   (ViewportWidth-12, PORT-VISUAL).

Verification: build 0/0; --swos-smoke 15000 PASS (0 stuck/stall, 1-0, clock
38:13); --keeper-release-test PASS; --corner-goalkick-test 4/4. Backup:
`I:\GITHUB\W_OPEN_SWOS\.backups\20260703_135651_session28e_keeper_chain_spinpass_fixed.7z`

## ✅ Session 28 wave 4 (2026-07-03) — back-pass keeper play + SWOS HUD + free-kick booking deadlocks

1. **Back-pass keeper plays with feet (#166):** ported the missing
   l_goalie_cant_catch_ball pickup body (updatePlayers.cpp:2780-2905 — keeper
   at a low own-team ball becomes controlledPlayer with ball at feet,
   goalkeeperPlaying=1) + fixed the head-block flag clobber (goaliePlayingOrOut
   zeroed every tick broke control-switch TO the keeper, :917-941 jz
   l_its_controlled_player). Full goalkeeperPlaying consumer audit table in
   the session log. Keeper now dribbles/passes a received back-pass.
2. **SWOS HUD 1:1 (#167-#169):** new Assets/SwosFont.cs decodes CHARSET.RAW
   (small 6px + big 8px proportional fonts, 16-col ASCII grid; metrics per
   text.cpp charToSprite/drawText). Result bar = big-font team names + score,
   small-font scorer lines "NAME\t23,67(PEN)" per result.cpp:145-429; bench =
   team-coloured panel + highlight bar per drawBench.cpp; on-ball player name
   banner "N SURNAME" top-left per playerNameDisplay.cpp:57-90 (FSM was
   already ported). All NEAREST, no TTF. Known approximation: big score
   digits use big charset font, not the undecoded kBigZeroSprite art.
3. **Free-kick infinite stall (#170)** — NOT the foul-team fix (verified
   faithful): two stacked BOOKING deadlocks, visible only with a human team
   (cards never fire CPU-vs-CPU, updatePlayers.cpp:13886-13917 — smoke was
   structurally blind, → task #171 test flag): (a) referee onScreen flag was
   never maintained (no DrawSprites pass) so kRefLeaving never completed —
   added Referee.UpdateRefereeOnScreenFlag (clip test per swos.asm:
   100200-100317); (b) CheckIfThisPlayerGettingBooked ran at the wrong
   dispatch position — moved inside the stoppage tail at the asm slot
   (:8933-9049), exits skip the wall/pushback dest writers so the booked
   player actually reaches the ref at (foulX+21,foulY). Card fouls resolve in
   ~585-640 ticks, plain fouls ~380, human fire within ~6 ticks.
   "Own player walks up first" = faithful booking ceremony (fouler walks to
   the ref for the card).

Verification: build 0/0; --swos-smoke 15000 PASS (0 stuck/stall/PORT-SAFETY);
--corner-goalkick-test 4/4; --keeper-release-test PASS; foul repro resolves
for both teams as fouler. Backup:
`I:\GITHUB\W_OPEN_SWOS\.backups\20260703_122936_session28d_backpass_hud_freekick_fixed.7z`

## ✅ Session 28 wave 3 (2026-07-03) — lineup permutation + foul-team inversion

1. **LINEUP WAS SCRAMBLED (root cause of "keeper saves like a scrub"):** the
   TEAM.* header carries a 16-byte lineup table `playerNumbers` @ +0x3C
   (swos.asm:343 / swos.h:257) mapping in-game slot → roster index; the
   original resolves every in-game player through it (InitInGameTeamStructure
   swos.asm:35824-35923 → GetPlayerAtIndex swos.asm:25572-25607). Our loader
   copied raw roster order, so e.g. Arsenal's reserve keeper LUKIC played
   right back and the field lineup was shifted. Ported: PlayerRecord/
   TeamRecord.LineupOrder plumbed from the parser (TeamFile.PlayerDisplayOrder)
   through AssetMapping into TeamDataLoader.WritePlayerInfos; PlayerInfo.index
   = file roster index; forced-GK-at-slot-0 hack removed (position comes from
   the file); MapPositionString fixed to the real enum (RB=1/LB=2 were being
   read as wings). Verified: SEAMAN ord 1 GK, DIXON RB, ... bench LUKIC(GK);
   Chelsea HITCHCOCK ord 1. Now goalieSkill-from-price actually applies to
   the player standing in goal.
2. **Foul awarded to the WRONG team:** PlayerTackle.cs
   TestFoulForPenaltyAndFreeKick had the forceLeftTeam branch inverted vs
   updatePlayers.cpp:13842-13855 (jnz SKIPS `A6 = bottomTeamData`) — restarts
   nearly always went to the top-team's opponent regardless of who fouled.
   Fixed: fouled team (opponent of A6 fouler) restarts.

Verification: build 0/0, --swos-smoke 15000 PASS (0 stuck/0 stall),
--keeper-release-test PASS. Backup:
`I:\GITHUB\W_OPEN_SWOS\.backups\20260703_113044_session28c_lineup_foul_fixed.7z`

## ✅ Session 28 wave 2 (2026-07-03) — bugs #158-#163 all fixed

1. **#158 "READY?" overlay removed** (Main.cs placeholder; original shows nothing).
2. **#159 back-pass rule ported** — keeper does NOT catch an own-team pass:
   gate at updatePlayers.cpp:8259-8301 (lastTeamPlayed==A6 && playerHadBall==0
   → goalkeeperPlaying=1, keeper plays with feet; claim only from opponent).
   Second ungated claim path (fallback dive collision) gated per cseg_7FCD0
   (updatePlayers.cpp:2605-2630). Dive-saves of shots still claim (original is
   unconditional there too).
3. **#160 player-number indicator** — blurry TTF Label replaced with
   NEAREST-filtered pixel-crisp 3x5 digit sprites (stand-in; authentic
   kSmallDigit 1188-1203 glyphs not decoded yet → task #164).
4. **#161 TEAMS-SWAPPED break positions FIXED (major)** — PlayerHeader.cs
   SetPlayerWithNoBallDestination had the top/bottom tactics-quadrant
   mirroring INVERTED vs updatePlayers.cpp:15484-15538 (TOP=straight read,
   BOTTOM=mirror 34-idx / 0xEF-byte). Every break destination went to the
   other side's quadrant — "opponents crowd my keeper".
5. **#162 ball float during keeper dive-claim** — replaced hand-tuned (0,4)
   ext-frame anchor with AUTHENTIC per-frame anchors: PC sprite centers from
   GOAL1.DAT/TEAM1.DAT via tools/sprite-anchors-extract, rebased into Amiga
   tile bboxes (PlayerFrames.ExtAnchor table; PORT-VISUAL cited).
6. **#163 goalie skill from PRICE** — was Clamp7(Finishing) (keepers' nibbles
   are filler=1 → every keeper skill 1 → "puszcza szmaty"). Now the original
   AdjustPlayerSkills formula (swos.asm:37783-37867): (valueCode+3)/7 +
   (rand&1|rand>>1)+1, clamped 0..7 per FixTwoCPUsGameCrash
   (game.cpp:1508-1516). PlayerRecord.ValueCode plumbed through AssetMapping
   from TeamFile byte +0x20. Verified live: Seaman(32)→7, Kharin(27)→6,
   Hitchcock(21)→5, Lukic(15)→4.

Verification: build 0/0; --swos-smoke 15000 PASS (0 stuck, clock 32:29, 3-3);
--keeper-release-test PASS (tick 110); --corner-goalkick-test 4/4 PASS.
Backup: `I:\GITHUB\W_OPEN_SWOS\.backups\20260703_111207_session28b_bugs158-163_fixed.7z`
New follow-up: #164 (authentic small-digit glyphs from SPRITE.DAT).

## ✅ Session 28 (2026-07-03) — bugs #153-#157 all fixed (3 parallel agents)

1. **#154 pass-receiver veer-off — FIXED (root cause of most gameplay damage).**
   Missing plain-pass gate (updatePlayers.cpp:7674-7696) meant the
   "not-turned-enough-toward-ball" check ran on EVERY human pass →
   passingToPlayer/passingBall cleared ~every tick → UpdatePlayerBeingPassedTo
   re-elected passToPlayerPtr to whoever was near the passer; keeper targets
   were dropped permanently (re-election skips ordinal==1). Also fixed:
   kBallFriction table had a dropped (1000,1000) pair at slot 12 (all slots
   ≥12 shifted!); team.ballX/Y written on paths where the original writes
   nothing; intercept prediction based on ball pos instead of receiver pos;
   ported the missing in-progress tail `RunPassChaseTail`
   (updatePlayers.cpp:8312-8634: hold-position, l_its_a_pass, keeper leash
   cancel zones, goalOut crowd stop). Files: PlayerControlled.cs (+ verify-only
   routers in UpdatePlayers.cs).
2. **#153 keeper-hold camping — did NOT reproduce after the above** (old cause
   was unconditional l_pass_success + ungated re-election, both already fixed).
   Audit verified faithful: GoalkeeperClaimedTheBall writes, StopAllPlayers,
   round-robin stoppage tail, ballOutOfPlayTactics indirection, cseg_83A41
   65px/±70px pushback (opponent dests settle 85-93 px from spot). Hardening:
   try/catch in SetPlayerWithNoBallDestinationForBreak no longer skips the
   pushback. Plus 1-line fidelity fix: shooting=1 after stoppage kick lands on
   the KICKER's team, not opponent (updatePlayers.cpp:5396-5402;
   PlayerControlled.cs ~934).
3. **#155 keeper dive frames — FIXED.** Dive anim tables were stand-ins →
   ported 16 real streams from swos.asm:218568-218650; CJCTEAMG dive bands
   decoded (4×7 cells 16×20) and wired into renderer with kit recolour
   (AnimationTablesData.cs, PlayerFrames.cs, AmigaSpriteAtlas.cs, Main.cs).
4. **#156 bottom-goal net — FIXED.** Goals were baked into the pitch bitmap;
   added transparent net overlays from CJCBITS at (300,90)/(300,735)
   (pixel-verified 100% match), Y-sorted like players → keeper draws behind
   the net at both ends.
5. **#157 throw-in overhead ball — FIXED.** Renderer now honors hideBall
   (ball+shadow hidden while ImageIndex<0); overhead-hold frames 371-394
   (aboutToThrowInAnimTable) mapped to CJCTEAM cells; cheer frames 365-370
   mapped as bonus. Sim side already faithful.

Verification: build 0/0, --swos-smoke 15000 PASS (0 stuck, 0 clock-stall,
score 1-1 both ways, clock 31:10), --keeper-release-test PASS,
--corner-goalkick-test 4/4, --goal-celebration-test PASS.
Backup: `I:\GITHUB\W_OPEN_SWOS\.backups\20260703_102147_session28_bugs153-157_fixed.7z`
Known follow-up: ext-frame anchors visually tuned (0,4) not from sprite
headers (candidate: tools/sprite-anchors-extract).

## 🎯 Session 28 original bug list (tasks #153-#156 — ALL DONE, kept for reference)

User verdict after a full GUI match (lost 2-4, all 4 conceded goals were engine
bugs, "coraz bardziej przypomina swosa"):

1. **#153 Keeper-hold camping** — when the keeper catches (gs=3), the two
   NEAREST players are opponent strikers (own defenders are away); the release
   pass lands at their feet → instant goal (conceded 2x). Compare original:
   where do break tables / AI position the ATTACKING team during opponent's
   keeper-hold, and how does l_keepers_ball + UpdatePlayerBeingPassedTo pick
   the release target (own-team eligibility!).
2. **#154 Pass receiver veers off mid-flight** (worst gameplay bug, both teams,
   every pass) — receiver starts toward the incoming ball correctly, then
   suddenly turns back to formation as if the pass vanished, then sometimes
   re-reacts. 2 conceded goals on keeper pass-backs. Suspects: passToPlayerPtr
   re-election mid-flight (UpdatePlayerBeingPassedTo, swos.asm:101045-101321,
   playerSwitchTimer=25 gate), the in-progress positioning tail
   (SetPlayerWithNoBallDestination) overwriting the receiver's chase dest, or
   passingBall/ballInPlay flags dropping. Port target: l_player_expecting_pass
   IN-PROGRESS branch (updatePlayers.cpp:7604+) 1:1.
3. **#155 Keeper dive animation not rendered** — dives work in sim; renderer
   doesn't map the CJCTEAMG dive frames (imageIndex windows 971-1057 /
   1087-1173) to atlas cells; shows standing/fallback frames.
4. **#156 Bottom goal net under keeper** — need a second TRANSPARENT net layer
   drawn ABOVE sprites for the near (bottom) goal (check swos-port goal sprite
   z-order / drawSprites sort; InitGoalSprites).

Pre-compaction backup: `I:\GITHUB\W_OPEN_SWOS\.backups\20260703_025752_session28_precompaction_4bugs_reported.7z`

## 🏁 Sessions 24-27 (2026-07-02) — match flow is a faithful 1:1 port now

Heuristic-collapse era is OVER. What landed (each with asm/cpp citations in code):

- **Full stoppage/restart machine** (GameLoop.cs): l_stoppage_event_triggered
  dispatcher + breakCameraMode ladder 0→1→2→4→3→5→6→7→8 (gameLoop.cpp:426-1853),
  faithful StartingMatch/InitPlayersBeforeEnteringPitch/PrepareForInitialKick
  (Kickoff.cs), walk-to-formation with destReachedState 1→2→3 + break-position
  tables (UpdatePlayers.cs, swos.asm:245961-246110), result panel timers
  (Result.cs) RENDERED as the original bottom dark bar, camera seeded via
  SetCameraToInitialPosition, controlled-player shirt number rendered.
- **Control**: UpdateControlledPlayer faithful (switch only when ballOutOfPlay
  != 0 = open play, strictly-closest, no debounce); AI writes gated to CPU
  teams; per-sprite AI_Kick and off-ball kick fallback REMOVED (no original
  counterpart); off-ball AI_SetControlsDirection gated on controlledPlayer==0
  (updatePlayers.cpp:8902-8915) — this gate fixed 2700-5600-tick restart stalls.
- **Duels**: wonTheBallTimer at +138 (was misread via +130=AI_timer), duel
  winner/loser mapping UN-inverted (weaker side loses with (16+diff)/32 —
  player.cpp:376-429), dseg_17E276 turn-tolerance seeded, tackle epilogue
  cseg_821F5. Goal-ward kick reaim suppressed for dispossession pokes.
- **Directions**: fullDirection = BALL→PLAYER angle (writer was inverted →
  everyone faced away + all kicks mirrored). Convention: 0..255, 0=N, CW;
  direction 0..7 = ((full+16)&255)>>5; kDefaultDestinations dir4=(0,+1000)=S.
- **Keeper**: dive routed to faithful l_goalie_diving (decay 112/192/128, no
  more corner-run), claim ports playerTurnFlags 0x7C/0xC7/&0xBB (no own-net
  release), keeper eligible as goal-kick taker (ballOutOfPlayOrKeeper=-1
  writes), DoGoalkeeperSprites dead-guard fix (index ALWAYS doubled,
  overflow→kGoalKeeperClaimingBallHeight; was misaligned reads → Z=2304px).
- **Rendering**: pitch bitmap world origin = (0,16) (rows 1..53 of 55) — fixed
  the 16px "players below pitch" offset; ball anchor (1,3); shadow formula
  ball.cpp:1689-1754; slide-left cell (14,0) neighbour-pixel trim.
- **Bench**: full updateBench.cpp port (open via double-tap of a direction
  during stoppage — original semantics, or key B; 5 subs, limit 2, walk-off/in,
  tactics menu; ladder interlock g_inSubstitutesMenu). `--bench-test` PASS.
- **Tests now faithful**: --swos-smoke (15k: clock 36:50, 0 stuck, 2-1),
  --goal-celebration-test, --corner-goalkick-test, --halftime-ceremony-test,
  --keeper-release-test, --referee-fsm-test, --bench-test — all PASS.

**Known deferred**: #148 teamPlayingUp end-swap reseat; #149 ProlongLastMinute
exit conditions (45' prolong ~5200 ticks); weak-tackle variant unported; pin
retirement + ball-distance bucket gates (probe notes in task #151 desc).

**Graphics sheet for user art rework** (tools/atlas-export):
`I:\GITHUB\W_OPEN_SWOS\assets\extracted\graphics\swos_graphics_sheet.bmp`
(8 atlases stacked, offsets in .offsets.txt; reverse importer NOT yet built).

**Last updated (previous): 2026-06-01 (B-FULL Session 19 — FULL MATCH MILESTONE)**

## 🏁 LATEST: B-FULL Session 19 — FULL MATCH WORKS, 5 GOALS IN 30K TICKS (2026-06-01)

**State**: **~25,500 LOC across 43 files**. Full match flow runs end-to-end:
kick-off → ball physics → AI play → keeper saves → goals → post-goal restart
→ half-time → full-time. **30,000-tick headless smoke produced score 0-5**.
Clean build (0 warnings, 0 errors).

**All major systems working**:
- ✅ Kick-off (PrepareForInitialKick + PlayerKickingBall fires opening kick)
- ✅ Ball physics (Section1/2/3 + ApplyBallAfterTouch + bounce + friction)
- ✅ AI play (chase-ball, possession-kick, defensive positioning by formation)
- ✅ Keeper saves (UpdateBallWithControllingGoalkeeper + Caught/Claimed/Diving)
- ✅ Goals (Section4 goal detection + score increment + scoreline update)
- ✅ Post-goal restart (re-kickoff after celebration)
- ✅ Half-time (sides swap, restart from centre)
- ✅ Full-time (FT screen + stats overlay)

**Remaining known limits**:
- **Asymmetric AI** — bot scores more than top team. Needs tuning pass on
  AI weighting / formation-side bias.
- **Penalty-box edge cases** — some shots through traffic may slip past
  unrealistic keeper positions; corner/throw-in edge logic not fully ported.

**Milestone backup**: `I:\GITHUB\W_OPEN_SWOS\.backups\20260601_204539_snapshot.7z`
(3.52 MB, tagged `B_FULL_S19_full_match_5goals_2026_06_01`).

**To test**: `I:\GITHUB\W_OPEN_SWOS\tools\test-swos-port.bat`

**Headless smoke (30k ticks)**:
```
I:\GITHUB\W_OPEN_SWOS\.tools\godot\Godot_v4.6.2-stable_mono_win64_console.exe --headless --path I:\GITHUB\W_OPEN_SWOS\game --swos-smoke 30000
```

## 🏁 Earlier: B-FULL Session 3 — KICK-OFF WORKS (2026-05-27)

**State**: ~14,000 LOC port. Ball gets kicked at kick-off (spd=2195, rolls
233px south, stops via friction). Players take 4-3-3 formation. Keeper goal-mouth
position correct (dest=(336,158)/(336,760)). Clock advances. NO crashes for 500
ticks. Renderer wired to read frameIndicesTable from PlayerSprite.

**Telemetry confirmed**:
- t=0: ball(336, **451**, 1) spd=**2195** — kickoff fires real PlayerKickingBall
- t=50: ball(336, 581, 2) spd=1479 — rolling
- t=150: ball(336, 682, 0) spd=86 — nearly stopped
- t=200+: ball at rest (no follow-up AI kick yet — chase-ball AI is stubbed)

**Architecture fixes this session**:
- `GameLoop.StubUpdatePlayers` → `UpdatePlayers.Update(0)` + `Update(1)` (was no-op!)
- `GameLoop.StubMovePlayers` → `SpriteUpdate.MoveAllPlayers()` (ported from updateSprite.cpp:145-229)
- `PlayerHeader.SetPlayerWithNoBallDestination`: now reads BALL X/Y (was reading own X/Y, hence keeper dest=15877 overflow)
- `Kickoff.PrepareForInitialKick` (NEW) — places 22 players in 4-3-3, ball at (336,449), wires controlled player
- `Main.cs InitSwosVmFromMatchSetup` → calls `Kickoff.PrepareForInitialKick(0)` + `PlayerActions.PlayerKickingBall(kickerSprite, topTeam)` to fire opening kick
- `UpdatePlayers.TickAiControlled` → calls `PlayerHeader.SetPlayerWithNoBallDestination(...)` per outfielder so dest gets set

**Read first**: `docs/porting/B-FULL-READY-FOR-TESTING.md` — full inventory +
verification + how-to-test + known limits.

**Headless smoke test (legacy)**:
```
.tools\godot\Godot_v4.6.2-stable_mono_win64_console.exe --headless --path game --swos-smoke 5000
```

Expect: ball physics work, players may freeze visually (animation table stubs
pending), no crashes. ESC → menu → "physics: Old sim" reverts to stable
fallback if needed.

## 🎯 Earlier: B-FULL Session 1 DONE

Pipeline wired end-to-end. See `docs/porting/B-FULL-SESSION1-DONE.md`.

## 🚨 ORIGINAL PIVOT NOTE (2026-05-26 morning)

**Phase B-FULL committed**. NO MORE PIECEMEAL FIXES.
Read `docs/porting/B-FULL-PLAN.md` BEFORE doing anything else.

Summary:
- User explicitly rejected partial porting (4th time in this session series)
- Track record: UAE port — 2 days partial failure → full port worked in <10 tries
- Research (OpenTTD/OpenRCT2/Devilution/ScummVM) confirms: mechanical port
  first, refactor only after compile + fidelity tests pass
- Plan: 3-7 intense sessions to port ~10-15k LOC of new C# via SwosVm
- Old Sim stays UNTOUCHED as fallback
- Agents used aggressively (user confirmed: highest individual plan, no limit)

**HARD RULE (2026-05-26)**: When user reports a behavior gap, the ONLY
acceptable response is to port the relevant swos-port function mechanically.
DO NOT add `if (X) {...}` heuristic patches to BallSim/Main.cs/KeeperSim.

Memory entry: `feedback-full-port-not-piecemeal`.

## Other hard rules

**(2026-05-27) — ABSOLUTE PATHS ALWAYS.** User has many projects on this
machine. Every path in user-facing text MUST be absolute (start with drive
letter), e.g. `I:\GITHUB\W_OPEN_SWOS\tools\test-swos-port.bat` — NOT
`tools\test-swos-port.bat`. Reminded multiple times.

**(2026-05-24)** When asking user to test ANYTHING, ALWAYS give them the path
to a `.bat` in `tools\`. NEVER paste multi-line PowerShell.

**(2026-05-23)** Treat me as senior — every few steps, stop and request deep
dive (research + brainstorm + plan modification).

If session crashed / new conversation / lost context — start by reading this
file. Other key docs in priority order:

1. **`C:\Users\Admin\.claude\plans\lazy-crunching-finch.md`** — approved plan
   (look for section "2026-05-23 UPDATE")
2. **`docs/porting/swosvm-port.md`** — port strategy + audit findings + bug list
3. **Memory entries** `C:\Users\Admin\.claude\projects\I--GITHUB-W-OPEN-SWOS\memory\`:
   - `feedback_treat_as_senior_request_research.md` — work pattern preference
   - `feedback_use_agents_heavily.md` — 4 broad + 4 custom agent pattern
   - `project_phase_B_pivot_parallel_keeper_port.md` — choice of Option B
   - `project_swosvm_port_strategy.md` — overall strategy
4. **`CLAUDE.md`** — project rules (read first if NEW session)

## 2026-05-23 LATE NIGHT — autonomous keeper port wave

Worked through plan modifications + first wave of keeper port autonomously
(user went to sleep). Backup at `.backups/20260523_225537_snapshot.7z`.

**Quick wins delivered**:
- ✅ `Main.cs:786` — `_ball.Z = 0` → `Fixed.FromInt(5)` (user pain #2 "ball at feet"
  now visible at keeper hand height in old sim).
- ✅ Audit bug #1 fixed — substitution truth table inverted in Section3 of
  BallUpdate. Validated by harness: 10169 enter→exit pairs match.
- ✅ Audit bug #2 fixed — speed==0 guard added before
  UpdateBallWithControllingGoalkeeper call.

**B5.0 PlayerSprite layout (Opcja Alpha) — COMPLETE**:
- `SwosVm/PlayerSprite.cs` — 22-slot pool at 0x50000, 128-byte stride, 110-byte
  actual Sprite struct. Per-team SpritesTable at 0x4F8C0 / 0x4F8EC.
- Memory bumped 320 KB → 384 KB. Init populates pointer tables + slot
  ordinals (keeper=1, outfielders=2..11) + teamNumbers.
- TeamData expanded with full TeamGeneralInfo fields (Players ptr,
  ShotChanceTable, GoalkeeperDivingLeft/Right, BallOutOfPlayOrKeeper,
  GoaliePlayingOrOut, etc.) — every offset cited swos.h:331+.

**B5 keeper port (first wave)**:
- `Sim/Port/PlayerUpdate.cs` — new file. Four functions ported:
  - `UpdateBallWithControllingGoalkeeper` (player.cpp:200-265) — pins ball to
    keeper + kBallPlOffsets, halves deltaZ. **Wired into BallUpdate.Section3
    keeper branch**, replacing stub.
  - `GoalkeeperClaimedTheBall` (player.cpp:2238-2336) — minimal subset.
    Sets gameState=3, gameStatePl=101, lastTeamPlayedBeforeBreak, team.controlledPlayer,
    ballOutOfPlayOrKeeper=1, goaliePlayingOrOut=1, keeper.direction.
    Stubs: lastPlayerBeforeGoalkeeper, playerTurnFlags, stopAllPlayers,
    updatePlayerWithBall, camera velocity (all listed in code comments).
  - `GoalkeeperCaughtTheBall` (updatePlayers.cpp:11022-11089) — sets keeper
    state to kGoalieCatchingBall(4), timer=15, speed=kGoalkeeperCatchSpeed(768),
    destX/Y to ballNextGroundX/Y clamped to [137, 761].
  - `TickGoalieCatchingBall` + `TickGoalieClaimed` — per-tick state handlers
    from updatePlayers.cpp:3059-3186. Drive timer-based transitions
    Catching → Claimed → Normal.

**Memory.Addr additions**: `ballNextGroundX`, `ballNextGroundY`.

**Test results**: 10169 enter→exit pairs match swos-port. Same 5 mismatches
(all Section 8 GameState/GameStatePl which is intentionally not yet ported).

### What's NEXT in B5

Remaining (in priority order):
- **`shouldGoalkeeperDive` (updatePlayers.cpp:10485-10816)** — 331 LOC asm
  soup. Estimated 3h. Replaces our heuristic `KeeperSim.ShouldDive()`.
- **`goalkeeperJumping` (updatePlayers.cpp:10829-11017)** — 189 LOC asm.
  Sets DivingHigh/Low state + speed + animation pointer.
- **`goalkeeperDeflectedBall` (player.cpp:2342)** — 187 LOC, parry path.
  Different speed/deltaZ per shotChanceTable lottery.
- **Per-tick updatePlayers loop** — orchestrates calling all the above.
  Currently nothing in OpenSWOS calls these ported functions — they're
  available for use but inert until the loop port lands.
- **Auto-release via real kick path** — replace our `Main.cs:ReleaseKeeperBall`
  with `findClosestPlayerToBallFacing` + `playerKickingBall`. Big — ~4h.

## What's done

### SwosVm foundation (Phase B1-B3)
- `game/scripts/SwosVm/Memory.cs` — 320 KB byte[] + Addr enum + Read*/Write* helpers
- `game/scripts/SwosVm/Flags.cs` — 68k flag register emulation
- `game/scripts/SwosVm/BallSprite.cs` — ball sprite view at 0x4F800
- `game/scripts/SwosVm/TeamData.cs` — top/bottom team data at 0x4F900 / 0x4FB00
- `game/scripts/SwosVm/Tables.cs` — kSineCosineTable + kAngleTangent (verbatim from swos-port)

### Phase B4 — updateBall + applyBallAfterTouch port (PARTIAL)
- ✅ `BallUpdate.ApplyBallAfterTouch` — full ball.cpp:2248-3005 port
- ✅ `BallUpdate.ResetBothTeamSpinTimers` — ball.cpp:4022-4026
- ✅ `BallUpdate.SetBallPosition` — ball.cpp:4029-4042
- ✅ `BallUpdate.ReverseDestXDirection` / `ReverseDestYDirection` — ball.cpp:4509-4583
- ✅ `BallUpdate.CalculateNextBallPosition` — ball.cpp:4206-4501
- ✅ `SpriteUpdate.CalculateDeltaXAndY` — updateSprite.cpp:231-336 (incl. PC 41/64 damping)
- ✅ `BallUpdate.Section1_HideAndFrameIndex` — ball.cpp:13-178
- ✅ `BallUpdate.Section2_DirectionAndFriction` — ball.cpp:180-298
- ✅ `BallUpdate.Section3_ApplyDeltasAndBounce` — ball.cpp:299-735 ⚠️ HAS 5 KNOWN BUGS
- ❌ `BallUpdate.Section4_GoalDetectionAndShadow` — ball.cpp:737-2247 (NOT STARTED, SKIPPED)

### Bug list — must fix in B4.9 BEFORE wire-up

See `docs/porting/swosvm-port.md` section "Bugs found in BallUpdate.cs Section3".

### Plan (modified 2026-05-23 after research pivot)

| Step | Description | Status |
|------|-------------|--------|
| **B4.8** | Fidelity harness (instrument swos-port-modified, CSV traces, C# replay test) | **NEXT** |
| B4.9 | Fix 5 audit bugs in BallUpdate.cs | after B4.8 |
| B5.0 | PlayerSprite memory layout (Opcja Alpha — full views, 22 × ~100 bytes pool) | after B4.9 |
| B4.10 | C# idiom upgrade (MemoryMarshal.Cast + ref struct views) | after B5.0 |
| B5 | Keeper port (updatePlayers.cpp keeper sections) | after B4.10 |
| B4.6 | Section 8 (goal detection) — deferred | before wire-up |
| B4.7 | Wire-up + USER TEST | last |

## B4.8 progress as of 2026-05-23

✅ **Step 1: Backup** — `.backups/20260523_173713_snapshot.7z`
✅ **Step 2: Copy** — `external/swos-port-modified/` created via `git clone --local
--no-hardlinks` from `external/swos-port/`. 376 MB (includes 3rd-party/, src/, tools/,
swos/, assets/, etc. — excludes tmp/ and bin/ which are gitignored).
**Original swos-port/ UNTOUCHED.**
✅ **Step 3: Instrument** —
- `external/swos-port-modified/src/game/ball/ball.cpp` modified (+88 lines):
  - Added anonymous-namespace `TraceScope` RAII struct.
  - Added `dumpRow(phase)` writing CSV row to `ball_trace.csv` in CWD.
  - Added `TraceScope _trace("...");` at top of `updateBall()` AND `applyBallAfterTouch()`.
  - Reads through `g_memByte[]` at known SWOS addresses (no swos:: accessor dependency):
    - ballSprite @ 328988, topTeamData @ 522792, bottomTeamData @ 522940, gameState @ 523118, gameStatePl @ 523116.
  - 25 CSV columns: frame, phase, gameState, gameStatePl, ball x/y/z/dx/dy/dz/destX/destY/speed/dir/img, top+bot {playerHasBall, spinTimer, allowedDir, leftSpin, rightSpin}.
- `external/swos-port-modified/src/stdinc.h:65` — sprintf_s poison guard added
  (`!defined(_MSC_VER)`), needed for VS 2019 stdlib compatibility (same fix as
  was applied to original outside-git).

✅ **Step 4: Build modified swos-port** — DONE 2026-05-23.
- Robocopy'd `external/swos-port/tmp/` (~302 MB, ~2 min) and `bin/x64/` runtime
  layout (DLLs, assets, data, audio) into `swos-port-modified/bin/x64/`.
- Build via VS 2019 BuildTools toolset v142 succeeded. Output:
  `external/swos-port-modified/bin/x64/swos-port-x64-Release.exe` (2.7 MB).
- All instrumentation linked correctly — function calls go through TraceScope
  RAII guard, writing `ball_trace.csv` to CWD.

⏳ **Step 5: Capture sample trace** — USER-BOUND, NOT done.
**How to capture**:
```pwsh
Start-Process -FilePath "I:\GITHUB\W_OPEN_SWOS\external\swos-port-modified\bin\x64\swos-port-x64-Release.exe" `
              -WorkingDirectory "I:\GITHUB\W_OPEN_SWOS\external\swos-port-modified\bin\x64"
```
- Set up a friendly match (Arsenal vs Chelsea is fine).
- Play through a kick-off scenario (e.g., kick the ball + watch it roll).
- Exit cleanly so CSV is flushed.
- Copy `external/swos-port-modified/bin/x64/ball_trace.csv` → `tests/OpenSwos.Tests/golden/kickoff.csv`.
- Repeat for `free_kick.csv`, `corner.csv`, `goal_kick.csv`.

✅ **Step 6: C# test project** — DONE 2026-05-23.
- `tests/OpenSwos.Tests/OpenSwos.Tests.csproj` — net9.0 xUnit project. NOT
  Godot.NET.Sdk (stand-alone). Imports `SwosVm/*.cs` + `Sim/Port/*.cs` via
  `<Compile Include>`. Packages: xUnit 2.9.2, Verify.Xunit 26.6.0, FsCheck.Xunit 3.0.0.
- `tests/OpenSwos.Tests/CsvTraceReader.cs` — parses 25-column ball_trace.csv into `TraceRow` records.
- `tests/OpenSwos.Tests/TraceComparer.cs` — `CaptureCurrent()`, `RestoreFrom(row)`,
  `DiffAgainst(expected)` with field-level diff output.
- `tests/OpenSwos.Tests/BallUpdateGoldenTests.cs`:
  - 3 smoke tests (no CSV needed): `Tick_RunsWithoutThrowingOnFreshMemory`,
    `CalculateDeltaXAndY_*` × 2.
  - 1 [Theory] with 4 cases (`kickoff.csv`, `free_kick.csv`, `corner.csv`,
    `goal_kick.csv`) — SKIPS gracefully if golden CSV not found, FAILS on first
    diff once CSV is present.
- **All 7 tests pass today** — the [Theory] cases skip because no CSVs yet.
- Run via: `cd tests/OpenSwos.Tests && dotnet test`

### How to actually USE the harness (next session)

1. Run instrumented exe (Step 5 above).
2. Drop `ball_trace.csv` files into `tests/OpenSwos.Tests/golden/`.
3. Re-run `dotnet test`. The [Theory] cases will turn red with field-level
   diffs pointing at the first port bug.
4. Fix in B4.9. Re-run. Repeat until green.

## Build commands

```pwsh
# C# build
dotnet build I:\GITHUB\W_OPEN_SWOS\game\OpenSwos.csproj

# C# headless smoke
I:\GITHUB\W_OPEN_SWOS\.tools\godot\Godot_v4.6.2-stable_mono_win64_console.exe --headless --path I:\GITHUB\W_OPEN_SWOS\game --quit-after 3

# Backup before risky work
powershell -ExecutionPolicy Bypass -File I:\GITHUB\W_OPEN_SWOS\tools\backup.ps1 -Tag "your_tag_here"

# Original swos-port (REFERENCE, do not modify)
I:\GITHUB\W_OPEN_SWOS\external\swos-port\bin\x64\swos-port-x64-Release.exe
```

## Latest backups (.backups/)

- `20260523_112432_snapshot.7z` — sections 1-7 ported, before audit-driven pivot
- `20260523_095109_snapshot.7z` — applyBallAfterTouch port only
- `20260523_093832_snapshot.7z` — Phase B4 start (before any updateBall section)

## Mental state reminders

- Treat me as SENIOR not JUNIOR — request research + own opinions every few
  steps. See `feedback-treat-as-senior-request-research` memory.
- Use AGENTS heavily — 4 broad + 4 custom Explore pattern proven to find root
  causes faster than single thread.
- Don't refactor mechanical port BEFORE fidelity tests pass.
- Keep `goto`. Preserve original bugs. Magic numbers are content.
- Don't touch `external/swos-port/` or `Swos9697_*/` — those are read-only refs.
