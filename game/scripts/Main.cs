using Godot;
using OpenSwos.Assets;
using OpenSwos.Sim;
using OpenSwos.Sim.Port;

namespace OpenSwos;

public partial class Main : Node2D
{
    // Kick-off positions. World coords; player starts a bit below mid-pitch (defending
    // top goal), opponent starts a bit above mid-pitch (defending bottom goal).
    private const int KickoffPlayerX = 176, KickoffPlayerY = 240;
    private const int KickoffOpponentX = 176, KickoffOpponentY = 80;
    private const int KickoffBallX = 176, KickoffBallY = 160;

    // Goal mouths. Pitch is 672 wide centred at world X 176; each goal mouth ~96 px wide.
    private const int GoalMouthHalfWidth = 48;
    private const int GoalLineNorth = -160;  // top goal: ball Y <= this scores for player
    private const int GoalLineSouth = 472;   // bottom goal: ball Y >= this scores for opponent

    private PlayerState _player = PlayerState.At(KickoffPlayerX, KickoffPlayerY);
    private PlayerState _opponent = PlayerState.At(KickoffOpponentX, KickoffOpponentY);
    // Goalkeepers — one each end of the pitch. Track ball X (clamped to goal mouth),
    // always trigger Kick=true so any ball touching them gets punched away.
    private PlayerState _homeKeeper = PlayerState.At(KickoffBallX, GoalLineNorth + 20);
    private PlayerState _awayKeeper = PlayerState.At(KickoffBallX, GoalLineSouth - 20);
    private BallState _ball = BallState.At(KickoffBallX, KickoffBallY);
    private MatchState _match = MatchState.NewMatch();
    // Keeper movement is constrained to the goal mouth and capped at a slower speed than
    // the outfielders so they don't moonwalk.
    private const int KeeperLineMinX = 128;
    private const int KeeperLineMaxX = 224;
    // Keeper kit is a fixed bright yellow for both teams, contrasting any outfield kit.
    // type=0 plain, shirt1=6 (yellow), shirt2=6, shorts=0 (black), socks=6.
    // Fallback hard-coded keeper kit used if the loaded team doesn't expose a
    // GoalkeeperKit (e.g. when teams aren't loaded yet or for placeholder atlases).
    // Per-team kits come from TeamRecord.GoalkeeperKit (built in AssetMapping).
    private static readonly byte[] FallbackKeeperKit = { 0, 6, 6, 0, 6 };

    // Touchline / back-line bounds for ball physics. Field is ~600 wide and ~640 tall
    // inside the 672×848 pitch image; the rest is stand / ad-banner padding.
    private const int FieldLeft = -130;
    private const int FieldRight = 482;

    private Sprite2D? _playerSprite;
    private Sprite2D? _opponentSprite;
    private Sprite2D? _homeKeeperSprite;
    private Sprite2D? _awayKeeperSprite;
    // === SWOS-port full-roster sprite pool (BUG B FIX, 2026-06-01) ==============
    // The legacy path renders 4 sprites (player + opponent + 2 keepers). The SWOS
    // port owns all 22 player slots (0..10 = team1, 11..21 = team2). When the port
    // path is active we hide the legacy 4 and paint the full pool below. Each
    // slot reuses the existing per-team kit tile cache:
    //   slots  0..10 → _homeKeeperTiles  (slot 0 = goalie) / _playerTiles  (1..10)
    //   slots 11..21 → _awayKeeperTiles  (slot 11 = goalie) / _opponentTiles (12..21)
    // Created in _Ready() right after the legacy sprites; updated each tick in
    // TickSwosPort() via UpdateSprite() with the matching SwosVm slot index.
    private readonly Sprite2D?[] _portPlayerSprites =
        new Sprite2D?[OpenSwos.SwosVm.PlayerSprite.TotalSlots];
    private readonly PlayerState[] _portPlayerStates =
        new PlayerState[OpenSwos.SwosVm.PlayerSprite.TotalSlots];
    private Sprite2D? _ballSprite;
    private Sprite2D? _ballShadow;
    // Goal overlay layers (#156). The goals are baked into the pitch bitmap
    // (ZIndex -10, under every sprite), so a keeper standing in the goal used
    // to draw ON TOP of the net. Original SWOS renders the goals as sprites
    // Y-sorted with the players (goal1TopSprite/goal2BottomSprite at
    // (kGoalX=300, kTopGoalY=129 / kBottomGoalY=778) — swos-port
    // gameSprites.cpp:80-94 + sortDisplaySprites). We reproduce that with two
    // transparent net pieces cut from CJCBITS.RAW, drawn pixel-exactly over
    // their baked twins (correlation-verified 2026-07-03: 100% of the white
    // net pixels match at pitch-bitmap (300,90) and (300,735)) and Z-indexed
    // by the same worldY+1000 rule the player sprites use.
    private Sprite2D? _goalOverlayTop;
    private Sprite2D? _goalOverlayBottom;
    private Sprite2D? _pitchSprite;     // detachable so menu can swap pitch variant
    private Camera2D? _camera;          // child of _playerSprite; offset-compensated each tick
    // Dedicated parent for the camera in port mode. Decouples camera focus
    // from sprite positions so the ball shadow (which used to be camera's
    // parent) can resume tracking the ball's ground projection. Without
    // this split, after a goal `_ballShadow.Position` was the camera focus
    // (centred on the pitch) while the ball stayed at the goal — the shadow
    // visually detached from the ball, and the legacy code overwrote
    // `_ballShadow.Position` every tick so the shadow couldn't follow the ball.
    private Node2D? _cameraAnchor;
    private Label? _scoreLabel;
    private Label? _timerLabel;
    private Label? _overlayLabel;
    private Label? _menuLabel;
    private ColorRect? _menuBackground;
    // Menu backdrop image (res://data/openswos_bg.png, 16:9) shown behind the
    // SWOS front-end, centre-cropped to the content rect. Own CanvasLayer below
    // the menu (Layer -1). Visible only in AppState.Menu.
    private Sprite2D? _menuBgSprite;
    // SWOS-styled front-end (game/scripts/Menu/). Replaces the old plain-text
    // menu label; driven while AppState==Menu. Reads/writes match setup through
    // the IMenuHost bridge implemented in Main.MenuHost.cs.
    private OpenSwos.Menu.MenuClient? _menuClient;
    // --menu-shot harness: drives + screenshots the menu, then quits.
    private bool _menuShotActive;
    private int _menuShotFrame;
    private int _menuShotStep;
    private string _menuShotDir = "";
    // SWOS result panel (port mode) — see _Ready for composition notes.
    // 1:1 copy of drawResult (result.cpp:145-156): a full-width DARKENED STRIP
    // AT THE BOTTOM of the screen (drawGrid) with team names / big score /
    // scorer columns / HALF-FULL TIME tag composited on it at the original
    // VGA coordinates. Per-frame geometry lives in LayoutResultPanel.
    private ColorRect? _resultPanelBg;          // drawGrid       (result.cpp:229-236)
    // Result-panel text is drawn with the real SWOS charset (SwosFont) via
    // NEAREST Sprite2D nodes — team names in the big font, scorers/score in
    // small/big font — replacing the earlier blurry TTF Labels (#167).
    private Sprite2D? _resultPanelTeam1;        // drawTeamNames  (result.cpp:238-246) — right-aligned, ends at VGA x=128
    private Sprite2D? _resultPanelTeam2;        // drawTeamNames  — left-aligned at VGA x=192
    private Sprite2D? _resultPanelScore;        // drawCurrentResult (result.cpp:248-264) — big digits + dash, centred on VGA x≈160
    private Sprite2D? _resultPanelTag;          // drawHalfAndFullTimeSprites (result.cpp:326-332)
    private readonly Sprite2D?[] _resultScorer1 = new Sprite2D?[OpenSwos.Sim.Port.Result.kMaxScorersForDisplay]; // drawScorerList (result.cpp:301-324)
    private readonly Sprite2D?[] _resultScorer2 = new Sprite2D?[OpenSwos.Sim.Port.Result.kMaxScorersForDisplay];
    // SWOS charset bitmap font (CHARSET.RAW). Null only if the asset is missing.
    private OpenSwos.Assets.SwosFont? _font;
    private CanvasLayer? _uiLayer;              // parent for all HUD text nodes
    // On-ball / controlled-player name banner (#169) — drawPlayerName,
    // playerNameDisplay.cpp:57-70. Number + surname, small charset, top-left.
    private Sprite2D? _playerNameSprite;
    // Controlled-player shirt-number sprites, one per team (world-space).
    // BUG #160: was a Godot TTF Label, which anti-aliases to a blurry smear at
    // the ~6 px size SWOS uses. Replaced with a crisp pixel-glyph Sprite2D
    // (NEAREST filter, integer scale) — see BuildShirtNumberTexture.
    private readonly Sprite2D?[] _ctrlNumSprites = new Sprite2D?[2];
    // Cache of rendered shirt-number textures keyed by the integer number.
    private readonly System.Collections.Generic.Dictionary<int, ImageTexture> _shirtNumTextures = new();
    // Referee + card sprites (port mode, task #181). The referee is a SEPARATE
    // sprite (RefereeSprite, ordinals 1273-1283, sprites.h:72-73); its position,
    // pose and card come from the ported referee FSM (Sim/Port/Referee.cs). The
    // authentic Amiga referee art (in CJCGRAFS.RAW) is not decoded yet, so these
    // use procedural stand-in textures — same precedent as the controlled-player
    // shirt-number sprites (BuildShirtNumberImage). Swap in the real 1273-1283
    // art once CJCGRAFS's sprite layout is recovered. World-space children so
    // they scroll + Y-sort with the players.
    private Sprite2D? _refereeSprite;
    private Sprite2D? _refereeCardSprite;
    // Procedural referee pose textures keyed by pose id (0=standing, 1=running,
    // 2=arm-raised). Card textures built once.
    private readonly System.Collections.Generic.Dictionary<int, ImageTexture> _refereePoseTextures = new();
    private ImageTexture? _yellowCardTexture;
    private ImageTexture? _redCardTexture;
    // Injury cross markers (task #184). One per player slot: a small red medical
    // cross drawn above a player who is down injured, giving the user the same
    // "this player is hurt" cue the original shows. In SWOS the red-cross sprite
    // (kRedCrossInjurySprite=1290, sprites.h:77) is drawn only in the bench/subs
    // menu (drawBench.cpp:318-326), NOT on the pitch — on the pitch the injury
    // reads purely from the writhe animation. We add an on-pitch overlay as a
    // deliberate PORT-VISUAL aid so the feedback is unmistakable. Procedural art
    // until CJCGRAFS sprite 1290 is decoded.
    private readonly Sprite2D?[] _injuryCrossSprites =
        new Sprite2D?[OpenSwos.SwosVm.PlayerSprite.TotalSlots];
    private ImageTexture? _injuryCrossTexture;

    // OpenSWOS energy/fatigue overlay (renderer only). One tiny 8×2 bar per
    // sprite slot, drawn just above the head number, reading PlayerEnergy from
    // the sim. Cloned from the injury-cross overlay pattern above. _energyBar
    // gates VISIBILITY; _fatigueSim (a Main.cs option field) gates the SPEED
    // EFFECT in non-career matches — see LoadSettings / InitSwosVmFromMatchSetup.
    private readonly Sprite2D?[] _energyBarSprites =
        new Sprite2D?[OpenSwos.SwosVm.PlayerSprite.TotalSlots];
    private readonly System.Collections.Generic.Dictionary<int, ImageTexture> _energyBarTextures = new();
    private bool _energyBar = false;   // energy bar visibility (renderer only); default OFF (LoadSettings leaves it OFF when the key is absent)
    private bool _fatigueSim = false;  // master toggle: fatigue speed effect in non-career matches
    // SWOS bench / substitutions panel (port mode). PORT-VISUAL: the original
    // draws the bench as sprite rows by the dugout (drawBench.cpp); we
    // approximate with a screen-space text panel. ALL state comes from the
    // ported FSM in Sim/Port/Bench.cs (updateBench.cpp) — these nodes only
    // visualise it. Opened with the B key (P1) during a stoppage.
    private ColorRect? _benchPanelBg;
    // Bench render pool (#168): per-row charset text sprites + per-row highlight
    // bars, laid out at the original drawBench.cpp menu geometry. Sized to the
    // deepest bench list (header + coach + 11 field players + legend lines).
    private const int BenchMaxRows = 20;
    private readonly Sprite2D?[] _benchLineSprites = new Sprite2D?[BenchMaxRows];
    private readonly ColorRect?[] _benchHighlights = new ColorRect?[BenchMaxRows];
    private AmigaSpriteAtlas? _atlas;
    private AmigaSpriteAtlas? _baseAtlas;  // un-recoloured master; cloned per kit choice
    // CJCTEAMG.RAW — goalkeeper-only sheet: same rows 0/1 run/stand layout as
    // CJCTEAM1 plus the dive (y=77/103/125/151) and catch (y=32/56) bands the
    // outfield sheet doesn't have. Source of the keeper dive frames (#155).
    private AmigaSpriteAtlas? _goalieBaseAtlas;
    // Cached per-cell textures keyed by (col, row), one dictionary per kit AND
    // per player FACE type (0=WHITE, 1=GINGER, 2=BLACK — KitPalette.FaceCount).
    // The face pass remaps the skin/hair palette slots per the original's
    // convert tables (swos.asm:218199-218215); without it everyone rendered
    // ginger (raw hair slots sit on the flesh/orange ramp). Index [0] (WHITE)
    // doubles as the legacy-path / preview default via the alias properties.
    private readonly System.Collections.Generic.Dictionary<(int, int), ImageTexture>[] _playerTilesFace =
        { new(), new(), new() };
    private readonly System.Collections.Generic.Dictionary<(int, int), ImageTexture>[] _opponentTilesFace =
        { new(), new(), new() };
    private readonly System.Collections.Generic.Dictionary<(int, int), ImageTexture>[] _homeKeeperTilesFace =
        { new(), new(), new() };
    private readonly System.Collections.Generic.Dictionary<(int, int), ImageTexture>[] _awayKeeperTilesFace =
        { new(), new(), new() };
    private System.Collections.Generic.Dictionary<(int, int), ImageTexture> _playerTiles     => _playerTilesFace[0];
    private System.Collections.Generic.Dictionary<(int, int), ImageTexture> _opponentTiles   => _opponentTilesFace[0];
    private System.Collections.Generic.Dictionary<(int, int), ImageTexture> _homeKeeperTiles => _homeKeeperTilesFace[0];
    private System.Collections.Generic.Dictionary<(int, int), ImageTexture> _awayKeeperTiles => _awayKeeperTilesFace[0];

    // Top-level app state. Menu shows pitch + kit selection; Match runs gameplay;
    // Paused freezes mid-match (ESC).
    private enum AppState { Menu, Match, Paused }
    private AppState _appState = AppState.Menu;

    // Front-end MUSIC preference (persisted). Default AMIGA. Resolved against
    // availability by ResolveMenuMusic(); the MenuMusic node (host-side only)
    // plays it while _appState == Menu and hard-stops otherwise.
    // Default menu-music source is CUSTOM (the bundled Suno tracks) — always
    // present in every build, so no availability edge case. On fresh settings (no
    // "menuMusic" key) LoadSettings leaves this default untouched, so the game
    // boots playing CUSTOM; the user can switch to AMIGA / PC / OFF in OPTIONS.
    private OpenSwos.Audio.MenuMusic.MusicSource _menuMusic = OpenSwos.Audio.MenuMusic.MusicSource.Custom;

    // Match setup state, driven by the menu.
    private readonly System.Collections.Generic.List<TeamRecord> _allTeams = new();
    // Available pitch variant IDs (1..6 in canonical SWOS, but SWCPICH2.MAP is missing
    // from this extracted set so we cycle through whatever actually exists on disk).
    private readonly System.Collections.Generic.List<int> _availablePitches = new();
    private int _pitchSlot = 0;        // index into _availablePitches
    private int _homeTeamIndex = 0;
    private int _awayTeamIndex = 1;
    private int _menuFocus = 0;        // 0=pitch, 1=home, 2=away, 3=opponent, 4=length, 5=speed
    private const int MenuSlotCount = 6;

    // Gameplay path selector. Formerly a menu toggle between the legacy hand-tuned
    // sim and the SWOS mechanical port; the legacy sim is abandoned, so the port
    // is now the only path — hardcoded true (task #183). The legacy AiSim/BallSim/
    // KeeperSim/PlayerSim code still compiles but is never reached. No longer
    // exposed in the menu and no longer loaded from settings.json.
    private bool _useSwosPort = true;

    // Global game-speed multiplier. Affects every sim tick (player, ball, AI, animation,
    // match clock, set-piece countdowns) by gating how often the sim advances. 1.00 =
    // SWOS-authentic 70 Hz (PC mode). <1.00 slows everything down, >1.00 speeds up.
    // Persisted to user://settings.json, restored on launch.
    private double _gameSpeedScale = 1.00;
    private double _speedAccumulator = 0.0;
    private const double SpeedScaleMin = 0.10;
    private const double SpeedScaleMax = 3.00;
    private const double SpeedScaleStep = 0.05;
    private const string SettingsPath = "user://settings.json";

    // === Retro fullscreen (display) system =====================================
    // The game renders a fixed 384x272 retro viewport (project.godot: stretch
    // mode="viewport", aspect="keep", Nearest filtering — must stay crisp). Three
    // display modes cycle via F11; Alt+Enter toggles windowed<->last-fullscreen.
    //   Windowed          — the authored 1152x816 window.
    //   FullscreenFill    — borderless native-res fullscreen, fractional scale to
    //                       fill keeping aspect (thin side bars, crisp/nearest).
    //   FullscreenInteger — borderless native-res fullscreen, pixel-perfect
    //                       integer scale (bigger black bars, perfectly even).
    // We use Window.ModeEnum.Fullscreen (BORDERLESS, native desktop res) — NEVER
    // ExclusiveFullscreen — so the monitor resolution/desktop-icon layout is left
    // untouched. Scale mode stays Viewport (matches the project stretch mode);
    // only ContentScaleStretch flips Fractional<->Integer.
    private enum DisplayMode { Windowed, FullscreenFill, FullscreenInteger }
    private DisplayMode _displayMode = DisplayMode.Windowed;
    // Small-screen / handheld (R36S 640x480) mode — detected once in _Ready.
    private bool _smallScreen;
    // Android / touch on-screen UI — detected once in _Ready (real touchscreen or
    // the --touch dev flag). Never true headless. When true, the menu client gets
    // TouchEnabled and the match on-screen d-pad/fire/pause overlay is built.
    private bool _touchUi;
    // Per-second perf logging (allocations + frame time), gated by --perf-log.
    private bool _perfLog;
    private double _perfAccum;
    private int _perfFrames;
    private long _perfLastMem;
    // Match on-screen touch overlay (built ONCE when _touchUi, screen-space).
    private CanvasLayer? _touchOverlay;
    private Sprite2D? _tStickBase, _tStickKnob, _tFire, _tPause, _tToggle;
    private sbyte _touchDx, _touchDy;
    private bool _touchFire;
    private int _stickFinger = -1, _fireFinger = -1;
    private Vector2 _stickAnchor;
    private bool _touchPauseRequested;
    // User toggle: hide the on-screen d-pad/fire/pause (e.g. when using a Bluetooth
    // pad). The small toggle icon itself stays visible. Persisted as "touchOverlay"
    // (default true on touchscreens). Instant, no restart.
    private bool _touchOverlayOn = true;
    // Screen-space centres/radii for the overlay hit-tests (match the drawn sprites).
    private Vector2 _tStickCenter, _tFireCenter, _tPauseCenter, _tToggleCenter;
    private float _tFireRadius, _tPauseHalf, _tToggleHalf;
    // Touch capture zones. Defaults reproduce the pre-#231 behaviour exactly
    // (left half = stick, fire = the drawn circle only); the Android expand path
    // widens them to "whole bar + grace strip" in LayoutTouchOverlay.
    private float _tStickZoneMaxX = ViewportWidth * RenderScale / 2f;   // 576
    private float _tFireZoneMinX = float.MaxValue;
    // Cached glyph textures for the top-right slot: PAUSE in match, BACK in menu.
    private ImageTexture? _pauseTex, _backTex;
    // === Android on-screen d-pad → MENU navigation (touch) =====================
    // Kept in DEDICATED fields, never the match _touchDx/_touchDy: the sim reads
    // _touchDx/_touchDy and only while AppState==Match, so driving menus this way
    // is provably sim-neutral. Main computes edge pulses with its own auto-repeat
    // and feeds them to MenuClient.InjectNav() (which OR-s them into the menu's
    // keyboard reads) — Godot's global Input state is never touched.
    private sbyte _menuStickDx, _menuStickDy;   // current 8-way of the menu d-pad
    private int _menuStickFinger = -1;
    private bool _menuConfirmPulse, _menuBackPulse;   // one-shot from CONFIRM/BACK taps
    private double _navRepeatTimer;                    // seconds until the next repeat pulse
    private bool _navHeld;                             // a direction is currently held
    private sbyte _navHeldDx, _navHeldDy;              // the held cardinal (for edge detect)
    private const double NavInitialDelay = 0.35, NavRepeatInterval = 0.12;
    // === Android expand-viewport presentation (#231) ===========================
    // ANDROID ONLY. Desktop/Linux/macOS/R36S keep aspect=Keep and are untouched.
    //
    // Problem: with aspect=Keep the side space on a 20:9 phone is OS window
    // background — NOTHING can be drawn there, so the touch overlay gets clipped.
    // With aspect=Expand that space becomes part of the root viewport and IS
    // renderable, but the viewport gets WIDER (height stays 816, width grows to
    // 816 * deviceAspect), which would show a wider slice of pitch — a fidelity
    // break.
    //
    // Fix: keep Expand, then (a) widen the match Camera2D limits by exactly the
    // extra half-width so the camera CENTRE lands on the same world point it would
    // in Keep mode, (b) shift every screen-space CanvasLayer right by _barW so the
    // 1152-wide content sits centred, and (c) mask the extra side space with two
    // opaque black ColorRects. Net visible result: pixel-identical to the Keep-mode
    // 4:3 image, with the extra space available for the touch overlay.
    private bool _expandMode;               // Android (or --android-sim): root aspect=Expand
    private bool _androidSim;               // desktop dev flag: --android-sim WxH
    private Vector2I _androidSimSize = new Vector2I(2400, 1080);
    private float _barW;                    // width of ONE side bar, in content units
    private float _vpW = ViewportWidth * RenderScale;   // current root viewport width (content units)
    private CanvasLayer? _barsLayer;        // Layer 3: black side bars (above HUD, below overlay)
    private ColorRect? _barLeft, _barRight;
    private CanvasLayer? _hudLayer;         // kept so the expand offset can be re-applied on resize
    // Below this bar width the bars are too thin to hold a thumb stick (near-4:3
    // tablet) — the overlay then falls back to its original on-content positions.
    private const float BarMinWidth = 70f;
    private bool _barsHostControls;
    // Which fullscreen mode Alt+Enter restores; seeded from a persisted fullscreen
    // mode if any, else Fill.
    private DisplayMode _lastFullscreenMode = DisplayMode.FullscreenFill;
    // Edge-detect for the display hotkeys is done in _UnhandledInput via k.Echo,
    // so no separate prev-flags are needed here.

    private int PitchId => _availablePitches.Count > 0 ? _availablePitches[_pitchSlot] : 1;
    // Match length presets (seconds per half). Default 30 s for fast iteration.
    // Real seconds per half. 1/2/3/5/8/10 minutes — the original's gameplay
    // options offered 3/5/(7)/10 MINS (gameplayOptionsMenu); we add the shorter
    // 1-2 min for quick games and 8 as a mid-step (user pick, 2026-07-10).
    private static readonly int[] MatchLengthPresets = { 60, 120, 180, 300, 480, 600 };
    private int _matchLengthSlot = 2;  // default 3 MIN — the original's default
    private int SecondsPerHalf => MatchLengthPresets[_matchLengthSlot];

    private enum Difficulty { Easy, Normal, Hard }
    private Difficulty _difficulty = Difficulty.Normal;
    // Speed scale (1/256 units) applied to the AI opponent's player tick. Player-1 is
    // always full speed; the AI variant slows itself.
    private int CurrentAiSpeedScale => _difficulty switch
    {
        Difficulty.Easy => 130,
        Difficulty.Normal => 180,
        Difficulty.Hard => 230,
        _ => 180,
    };
    // Held-key acceleration for the menu: with 1730 teams a single-press-per-team scroll
    // would be cruel. We count consecutive ticks of held vertical input and ramp the step.
    private int _menuHeldTicks = 0;

    private enum OpponentMode { Ai, Player2, Demo }
    private OpponentMode _opponentMode = OpponentMode.Ai;
    // Edge-detect state for raw keys that aren't bound to Godot actions.
    private bool _p2KickPrev = false;
    private bool _p1SlidePrev = false;
    private bool _p2SlidePrev = false;
    // SWOS kick wind-up state per player (game.txt:84-86): on press, start counting.
    // While counting check for back-press → high-shot flag. Release before 4 ticks →
    // pass; auto-fire after 4 ticks → shot or high-shot.
    private int _p1KickHoldTicks = 0;
    private bool _p1PulledBack = false;
    private bool _p1KickFired = false;     // true after kick triggered, until button released
    private int _p2KickHoldTicks = 0;
    private bool _p2PulledBack = false;
    private bool _p2KickFired = false;
    private const int KickWindUpTicks = 4;

    public override void _Ready()
    {
        GD.Print($"OpenSWOS booted at {Time.GetDatetimeStringFromSystem()}");

        // "P" = pause toggle, like the original (ESC keeps working too). A
        // runtime InputMap action so no editor/project file changes are needed.
        if (!InputMap.HasAction("swos_pause"))
        {
            InputMap.AddAction("swos_pause");
            InputMap.ActionAddEvent("swos_pause", new InputEventKey { PhysicalKeycode = Key.P });
        }

        // Belt-and-suspenders joypad bindings on the built-in ui_* actions so
        // Bluetooth pads whose confirm/cancel/dpad don't reach the defaults can
        // still drive the menu. Additive (never replaces the keyboard/stick defaults).
        EnsureJoypadUiBindings();

        // Lock physics tick rate to 70 Hz (matches SWOS PC). project.godot already
        // sets common/physics_ticks_per_second=70, but we re-assert here so that
        // a stripped-down project (or future variant) still gets the right pace —
        // the SWOS port path is hard-wired to ONE GameLoop.Tick() per physics tick.
        Engine.PhysicsTicksPerSecond = 70;

        LoadSettings();

        // --- Small-screen / handheld (R36S 640x480) detection ---
        bool smallByArg = false;
        foreach (var a in OS.GetCmdlineArgs()) if (a == "--small-screen") smallByArg = true;
        bool smallByScreen = false;
        // The Linux ARM64 build is tagged "handheld" (export_presets.cfg) — it ships
        // ONLY to R36S-class devices, so force small-screen there UNCONDITIONALLY
        // (ArkOS/Rocknix can report a desktop-sized virtual screen, so the size probe
        // below is unreliable on the actual hardware).
        bool handheldBuild = OS.HasFeature("handheld");
        if (handheldBuild) smallByScreen = true;
        else if (DisplayServer.GetName() != "headless")
        {
            var scr = DisplayServer.ScreenGetSize();
            if (scr.X > 0 && scr.X <= 720 && scr.Y > 0 && scr.Y <= 576) smallByScreen = true;
        }
        _smallScreen = smallByArg || smallByScreen;
        if (_smallScreen)
        {
            OpenSwos.Menu.MenuTheme.SmallScreen = true;
            // Native handhelds -> fullscreen fill; the --small-screen flag on a desktop -> 640x480 window for testing.
            _displayMode = smallByScreen ? DisplayMode.FullscreenFill : DisplayMode.Windowed;
        }

        // --- Touch UI (Android) + perf-log detection ---
        // A real touchscreen (DisplayServer.IsTouchscreenAvailable) OR the --touch
        // dev flag turns on the on-screen d-pad/fire/pause overlay + menu touch.
        // NEVER enabled headless (IsTouchscreenAvailable is false there, and no
        // --touch on CI runs).
        bool forceTouch = false;
        // Accept the flags whether they arrive before `--` (GetCmdlineArgs) or
        // after it (GetCmdlineUserArgs) — Godot splits the two. Also matches how
        // the harness arg loop below reads args.
        var _startupArgs = new System.Collections.Generic.List<string>(OS.GetCmdlineArgs());
        _startupArgs.AddRange(OS.GetCmdlineUserArgs());
        for (int i = 0; i < _startupArgs.Count; i++)
        {
            string a = _startupArgs[i];
            if (a == "--touch") forceTouch = true;
            else if (a == "--perf-log") _perfLog = true;
            // --android-sim WxH : desktop dev flag. Forces the Android code path
            // (aspect=Expand + bars + touch overlay) and sizes the window to WxH so
            // the phone layout can be screenshotted without a device. Purely a test
            // hook — a real Android build takes the same path via isMobile below.
            else if (a == "--android-sim")
            {
                _androidSim = true;
                if (i + 1 < _startupArgs.Count)
                {
                    var wh = _startupArgs[i + 1].Split('x');
                    if (wh.Length == 2 && int.TryParse(wh[0], out int sw) && int.TryParse(wh[1], out int sh)
                        && sw > 0 && sh > 0)
                    {
                        _androidSimSize = new Vector2I(sw, sh);
                        i++;
                    }
                }
            }
        }
        // Bulletproof gating: enable the touch/overlay UI on any mobile platform
        // (OS name "Android"/"iOS" or the "mobile" feature tag), on a real
        // touchscreen, or when forced with --touch. Realme-7-Pro report: some
        // Android devices return IsTouchscreenAvailable()==false at boot, so the
        // platform check is the primary signal there. NEVER enabled headless.
        bool isHeadless = DisplayServer.GetName() == "headless";
        bool isMobile = OS.GetName() == "Android" || OS.GetName() == "iOS" || OS.HasFeature("mobile");
        bool touchscreen = !isHeadless && DisplayServer.IsTouchscreenAvailable();
        _touchUi = !isHeadless && (forceTouch || isMobile || touchscreen || _androidSim);
        // Expand-viewport presentation: ANDROID ONLY (plus the --android-sim dev
        // flag). A touchscreen alone is NOT enough — a Windows touch laptop keeps
        // the desktop Keep path. Must be decided BEFORE ApplyDisplayMode(), which
        // reads _expandMode when it sets ContentScaleAspect.
        _expandMode = !isHeadless && (_androidSim || OS.GetName() == "Android" || OS.HasFeature("mobile"));
        // Always log (even when disabled) so future device reports are diagnosable.
        GD.Print($"[touch] platform={OS.GetName()} touchscreen={touchscreen} overlay={_touchUi} expand={_expandMode}");

        // Self-contained verification flag (only reads res://data/music, no SWOS
        // team data). Handle it HERE, before the asset/first-run branch, so it also
        // runs in a data-less exported build (proving res:// mp3 loading in a PCK).
        foreach (var a in _startupArgs)
        {
            if (a == "--music-custom-test")
            {
                OpenSwos.Audio.MenuMusic.TestCustomLoad();
                GetTree().Quit();
                return;
            }
        }
        if (_perfLog) _perfLastMem = System.GC.GetTotalMemory(false);

        // Apply the persisted display mode now that the Window exists (no-op &
        // safe under --headless; see ApplyDisplayMode's headless guard).
        ApplyDisplayMode();

        // Desktop --small-screen test path: give it an actual 640x480 window with fill scaling.
        if (_smallScreen && !smallByScreen && DisplayServer.GetName() != "headless")
            DisplayServer.WindowSetSize(new Vector2I(640, 480));

        // Desktop --android-sim test path: a real window at the simulated device
        // resolution so the phone layout renders/screenshots exactly as on device.
        if (_androidSim && DisplayServer.GetName() != "headless")
            DisplayServer.WindowSetSize(_androidSimSize);

        // Initialise SwosVm memory layer (Phase B port target) — populates ball
        // physics constants from swos.asm data section so ported updateBall() can
        // read them at the right offsets. Defaults to PC mode (matches swos-port
        // swos.ini gameStyle=0). Sanity-tested round-trip Memory↔BallSprite.
        OpenSwos.SwosVm.Memory.Init(pcMode: true);

        // Resolve the original-asset directories (export-safe): search the user's
        // 'original_swos_files' folder (extracted OR loose WHDLoad files), then the
        // in-repo dev tree. If nothing is present yet, EnsureImported extracts any *.adf
        // dropped in 'original_swos_adf' (and scaffolds both folders on a fresh install).
        // Android: request all-files access + create the public drop folders under
        // /storage/emulated/0/Download/OpenSWOS (the only place a file manager can reach
        // on Android 13+) plus the user:// fallbacks. No-op on every other platform.
        DataPaths.AndroidStartupInit();

        AmigaImporter.EnsureImported();
        string grafsDir = DataPaths.AmigaGrafsDir();

        // Scan disk for available pitch variants (canonical: 1..6, but the extracted set
        // is missing SWCPICH2.MAP).
        for (int i = 1; i <= 6; i++)
        {
            if (grafsDir.Length == 0) break;
            string p = System.IO.Path.Combine(grafsDir, $"SWCPICH{i}.MAP");
            if (System.IO.File.Exists(p)) _availablePitches.Add(i);
        }
        if (_availablePitches.Count == 0)
        {
            GD.PrintErr("No pitch variants found — falling back to placeholder");
            BuildPlaceholderPitch();
        }
        else
        {
            LoadPitchVariant(PitchId);
        }

        // Smoke-test the variant abstraction: load teams from PC + Amiga and print
        // identical sample data to prove the format truly is variant-neutral.
        string pcDataDir = DataPaths.PcDataDir();
        string amigaDataDir = DataPaths.AmigaDataDir();
        if (pcDataDir.Length > 0) TryLoadTeams(new PcAssetSource(pcDataDir));
        if (amigaDataDir.Length > 0) TryLoadTeams(new AmigaAssetSource(amigaDataDir));

        // Load player atlas (un-recoloured) and full PC team list. Menu picks which two
        // teams the recolour pipeline runs against; ApplyMatchSetup wires the textures.
        string atlasPath = grafsDir.Length > 0
            ? System.IO.Path.Combine(grafsDir, "CJCTEAM1.RAW")
            : "";
        if (atlasPath.Length == 0 || !System.IO.File.Exists(atlasPath))
        {
            GD.Print("Amiga graphics not found — skipping visual render");
            ShowFirstRunMessage();
            return;
        }

        try
        {
            _baseAtlas = AmigaSpriteAtlas.Load(atlasPath);
            GD.Print($"Loaded Amiga atlas: {AmigaSpriteAtlas.AtlasWidth}x{AmigaSpriteAtlas.AtlasHeight}");

            // Goalkeeper sheet — holds the dive/catch bands (bug #155). Optional:
            // when missing the keeper falls back to outfield-frame poses.
            string goalieAtlasPath = System.IO.Path.Combine(grafsDir, "CJCTEAMG.RAW");
            if (System.IO.File.Exists(goalieAtlasPath))
            {
                _goalieBaseAtlas = AmigaSpriteAtlas.Load(goalieAtlasPath);
                GD.Print("Loaded Amiga goalkeeper atlas (CJCTEAMG)");
            }
            else
            {
                GD.PrintErr($"Goalkeeper atlas not found at {goalieAtlasPath} — keeper dive frames unavailable");
            }

            // Team list for the menu / competitions. PC (≈1730 teams) is preferred
            // when present, but an Amiga-only install (the documented minimum) MUST be
            // playable too — fall back to the Amiga TEAM.* (≈1616 teams). Both parse
            // through the same variant-neutral TeamRecord, so everything downstream
            // (menu, competitions, kit recolour, skills) is unchanged.
            if (pcDataDir.Length > 0)
                foreach (var t in new PcAssetSource(pcDataDir).LoadAllTeams()) _allTeams.Add(t);
            if (_allTeams.Count == 0 && amigaDataDir.Length > 0)
                foreach (var t in new AmigaAssetSource(amigaDataDir).LoadAllTeams()) _allTeams.Add(t);
            if (_allTeams.Count == 0)
            {
                GD.PrintErr("No teams loaded (need PC or Amiga TEAM data) — menu cannot proceed");
                ShowFirstRunMessage();
                return;
            }
            // Pre-select Arsenal as home if present, otherwise team #0.
            for (int i = 0; i < _allTeams.Count; i++)
            {
                if (_allTeams[i].Name == "ARSENAL") { _homeTeamIndex = i; break; }
            }
            // Away = the next team that's not the same as home (and not the same name).
            _awayTeamIndex = (_homeTeamIndex + 1) % _allTeams.Count;
            for (int i = 0; i < _allTeams.Count; i++)
            {
                if (_allTeams[i].Name == "CHELSEA") { _awayTeamIndex = i; break; }
            }

            // Build initial kit tile caches.
            ApplyMatchSetup();

            // Controllable player sprite — starts standing facing south.
            var (sc, sr) = PlayerFrames.Standing(_player.Facing);
            _playerSprite = new Sprite2D
            {
                Texture = _playerTiles[(sc, sr)],
                Centered = true,
                Position = new Vector2(_player.X.ToInt(), _player.Y.ToInt()),
                TextureFilter = TextureFilterEnum.Nearest,
            };
            AddChild(_playerSprite);

            // Opponent sprite — same atlas, opposite kit, faces north (toward player).
            _opponent.Facing = Direction.North;
            var (osc, osr) = PlayerFrames.Standing(_opponent.Facing);
            _opponentSprite = new Sprite2D
            {
                Texture = _opponentTiles[(osc, osr)],
                Centered = true,
                Position = new Vector2(_opponent.X.ToInt(), _opponent.Y.ToInt()),
                TextureFilter = TextureFilterEnum.Nearest,
            };
            AddChild(_opponentSprite);

            // Goalkeepers — yellow kit, facing into the pitch. Home keeper at N back-line
            // (faces south); away keeper at S back-line (faces north).
            _homeKeeper.Facing = Direction.South;
            var (hkc, hkr) = PlayerFrames.Standing(_homeKeeper.Facing);
            _homeKeeperSprite = new Sprite2D
            {
                Texture = _homeKeeperTiles[(hkc, hkr)],
                Centered = true,
                Position = new Vector2(_homeKeeper.X.ToInt(), _homeKeeper.Y.ToInt()),
                TextureFilter = TextureFilterEnum.Nearest,
            };
            AddChild(_homeKeeperSprite);

            _awayKeeper.Facing = Direction.North;
            var (akc, akr) = PlayerFrames.Standing(_awayKeeper.Facing);
            _awayKeeperSprite = new Sprite2D
            {
                Texture = _awayKeeperTiles[(akc, akr)],
                Centered = true,
                Position = new Vector2(_awayKeeper.X.ToInt(), _awayKeeper.Y.ToInt()),
                TextureFilter = TextureFilterEnum.Nearest,
            };
            AddChild(_awayKeeperSprite);

            // === SWOS-port full-roster sprite pool (BUG B FIX, 2026-06-01) =====
            // 22 Sprite2D nodes (one per SwosVm slot). Hidden by default; flipped
            // visible only while _useSwosPort is true. The four legacy sprites
            // (_playerSprite, _opponentSprite, _homeKeeperSprite,
            // _awayKeeperSprite) get hidden in the same toggle so we don't
            // double-paint slots 0/1/11/12. Each slot uses the appropriate
            // pre-built kit cache so kit colours match _homeTeamIndex /
            // _awayTeamIndex selections from the menu.
            int totalSlots = OpenSwos.SwosVm.PlayerSprite.TotalSlots;
            for (int slot = 0; slot < totalSlots; slot++)
            {
                bool topTeam = slot < OpenSwos.SwosVm.PlayerSprite.TeamSize;
                bool isKeeper = (slot == OpenSwos.SwosVm.PlayerSprite.SlotGoalie1)
                             || (slot == OpenSwos.SwosVm.PlayerSprite.SlotGoalie2);
                var startFacing = topTeam ? Direction.South : Direction.North;
                _portPlayerStates[slot] = PlayerState.At(0, 0);
                _portPlayerStates[slot].Facing = startFacing;
                var (pc, pr) = PlayerFrames.Standing(startFacing);
                var tileCache = isKeeper
                    ? (topTeam ? _homeKeeperTiles : _awayKeeperTiles)
                    : (topTeam ? _playerTiles   : _opponentTiles);
                _portPlayerSprites[slot] = new Sprite2D
                {
                    Texture = tileCache[(pc, pr)],
                    Centered = true,
                    Position = new Vector2(0, 0),
                    TextureFilter = TextureFilterEnum.Nearest,
                    Visible = false,
                };
                AddChild(_portPlayerSprites[slot]);
            }

            // Referee + card sprites (task #181). Hidden until the referee FSM
            // activates. Procedural stand-in art (see field comments).
            _refereeSprite = new Sprite2D
            {
                Centered = true,
                TextureFilter = TextureFilterEnum.Nearest,
                Visible = false,
            };
            AddChild(_refereeSprite);
            _refereeCardSprite = new Sprite2D
            {
                Centered = true,
                TextureFilter = TextureFilterEnum.Nearest,
                Visible = false,
            };
            AddChild(_refereeCardSprite);
            _yellowCardTexture = ImageTexture.CreateFromImage(BuildCardImage(false));
            _redCardTexture    = ImageTexture.CreateFromImage(BuildCardImage(true));

            // Injury cross markers (task #184) — one per player slot, hidden
            // until UpdateInjuryMarkers shows it over a downed injured player.
            _injuryCrossTexture = ImageTexture.CreateFromImage(BuildInjuryCrossImage());
            for (int slot = 0; slot < totalSlots; slot++)
            {
                _injuryCrossSprites[slot] = new Sprite2D
                {
                    Texture = _injuryCrossTexture,
                    Centered = true,
                    TextureFilter = TextureFilterEnum.Nearest,
                    Visible = false,
                };
                AddChild(_injuryCrossSprites[slot]);

                // Energy bar overlay (OpenSWOS fatigue) — hidden until
                // UpdateEnergyBars shows it over an on-pitch player.
                _energyBarSprites[slot] = new Sprite2D
                {
                    Centered = true,
                    TextureFilter = TextureFilterEnum.Nearest,
                    Visible = false,
                };
                AddChild(_energyBarSprites[slot]);
            }

            // Camera follows the player but is clamped to the pitch bounds so it never
            // pans into the void beyond the field. Its `Position` (local to the player
            // sprite) is updated each tick to cancel out the per-frame anchor offset —
            // otherwise the screen wobbles ±1 px as the run-cycle anchors shift.
            _camera = new Camera2D
            {
                AnchorMode = Camera2D.AnchorModeEnum.DragCenter,
                // Native viewport is design ×RenderScale; the match world is
                // authored in design pixels, so zoom the camera to fill it 1:1.
                Zoom = new Vector2(RenderScale, RenderScale),
                // Bounds match the original's camera clip window: world x in [0, 672],
                // world y in [16, 848] — external/swos-port/src/game/pitch/pitch.cpp:376-398
                // (clipPitch clamps the view top to kSwosPatternSize=16 and the bottom to
                // kPitchHeight=848; the bitmap's last 16 px, world 848..864, never show).
                LimitLeft = PitchOffsetX,
                LimitTop = PitchOffsetY + PitchBitmapWorldY,
                LimitRight = PitchOffsetX + PitchWidth,
                LimitBottom = PitchOffsetY + PitchHeight,
                LimitSmoothed = false,
            };
            // Dedicated camera-anchor node. In port mode, the camera was previously
            // parented to `_ballShadow` and the shadow's Position was hijacked to
            // drive the camera focus — which broke the shadow visually (it followed
            // the camera, not the ball) and looked frozen on goals (ball at goal,
            // camera at centre → shadow at centre). Splitting the anchor out lets
            // the shadow track the ball ground projection again and the camera
            // track its smoothed focus independently.
            _cameraAnchor = new Node2D { Position = Vector2.Zero };
            AddChild(_cameraAnchor);
            _playerSprite.AddChild(_camera);
            _camera.MakeCurrent();

            // Real ball sprite from CJCBITS.RAW cell (0, 3). The atlas tile is 16×16 but
            // the actual ball pixels are a 4×4 image at the cell's top-left — trim to the
            // bounding box of non-transparent pixels so the SWOS per-frame anchor (1, 3)
            // applies to the real ball image, same dimensions as PC ball sprites
            // 1179-1182 (4×4, center (1,3) — swos-port spriteDatabase.h).
            string ballAtlasPath = System.IO.Path.Combine(grafsDir, "CJCBITS.RAW");
            Image ballImg;
            if (System.IO.File.Exists(ballAtlasPath))
            {
                var bitsAtlas = AmigaSpriteAtlas.Load(ballAtlasPath);
                CreateGoalOverlays(bitsAtlas);
                var rawBall = bitsAtlas.GetTile(0, 3);
                var used = rawBall.GetUsedRect();
                if (used.Size.X > 0 && used.Size.Y > 0)
                {
                    var trimmed = Image.CreateEmpty(used.Size.X, used.Size.Y, false, rawBall.GetFormat());
                    trimmed.BlitRect(rawBall, used, new Vector2I(0, 0));
                    ballImg = trimmed;
                    GD.Print($"Ball trimmed from 16×16 to {used.Size.X}×{used.Size.Y} at atlas-cell offset {used.Position.X},{used.Position.Y}");
                }
                else
                {
                    ballImg = rawBall;
                }
            }
            else
            {
                ballImg = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
                ballImg.Fill(new Color(1f, 1f, 1f, 1f));
            }

            // Ball shadow — 4×3 dark blob standing in for SWOS sprite 1183 (4×4,
            // anchor (1,3)). Drawn top-left-anchored (Centered=false) so the same
            // (x - centerX, y - centerY) placement math as the original applies —
            // renderSprites.cpp:44-52. Position is driven per-tick from the original
            // shadow formula (ball.cpp:1689-1754): x = ball.x + z/2 + 1,
            // y = ball.y + z/4 + 1 — at Z=0 the shadow peeks 1 px right/below the
            // ball, exactly like SWOS; as Z grows it slides down-right.
            var shadowImg = Image.CreateEmpty(4, 3, false, Image.Format.Rgba8);
            for (int sy = 0; sy < 3; sy++)
                for (int sx = 0; sx < 4; sx++)
                    shadowImg.SetPixel(sx, sy, new Color(0f, 0f, 0f, 0.45f));
            _ballShadow = new Sprite2D
            {
                Texture = ImageTexture.CreateFromImage(shadowImg),
                Centered = false,
                Position = new Vector2(_ball.X.ToInt() + 1 - BallSpriteCenterX,
                                       _ball.Y.ToInt() + 1 - BallSpriteCenterY),
                TextureFilter = TextureFilterEnum.Nearest,
                ZIndex = -1,
            };
            AddChild(_ballShadow);

            // Ball — top-left-anchored with the original's per-frame anchor applied:
            // image top-left at (x - centerX, y - z - centerY), centers (1, 3) —
            // renderSprites.cpp:44-52 + spriteDatabase.h ordinals 1179-1182. The
            // ball's ground-contact pixel therefore sits exactly at (X, Y - Z).
            _ballSprite = new Sprite2D
            {
                Texture = ImageTexture.CreateFromImage(ballImg),
                Centered = false,
                Position = new Vector2(_ball.X.ToInt() - BallSpriteCenterX,
                                       _ball.Y.ToInt() - BallSpriteCenterY),
                TextureFilter = TextureFilterEnum.Nearest,
            };
            AddChild(_ballSprite);

            // Score / timer / overlay UI on a screen-space CanvasLayer so it doesn't
            // scroll with the camera. NOTE: font sizes here are in VIEWPORT pixels
            // (384×272 — see project.godot). The window stretches 3× to 1152×816, so
            // a 7pt viewport font reads as ~21pt on screen. Pick sizes accordingly —
            // anything > 16 starts to feel huge.
            var ui = new CanvasLayer();
            // MENU layer: the SWOS-styled front-end (MenuClient) + career tables
            // paint here in 576×408 design space, scaled ×2 into the native
            // 1152×816 viewport. ×2 (not ×3) is a deliberate user preference — the
            // bitmap charset reads crisper and more compact at half again the size.
            ui.Transform = Transform2D.Identity.Scaled(new Vector2(MenuScale, MenuScale));
            AddChild(ui);
            _uiLayer = ui;

            // MATCH HUD layer: the score/timer/overlay labels, SWOS-charset result
            // panel, bench panel and on-ball player-name banner are all authored in
            // 384×272 (ViewportWidth/Height) design space and MUST keep their ×3
            // on-screen size — so they live on their own CanvasLayer at RenderScale,
            // NOT on the ×2 menu layer. Layer=1 keeps the HUD above the menu (the
            // two are mutually exclusive by game state anyway).
            var hud = new CanvasLayer { Layer = 1 };
            hud.Transform = Transform2D.Identity.Scaled(new Vector2(RenderScale, RenderScale));
            AddChild(hud);
            _hudLayer = hud;

            // SWOS charset bitmap font (CHARSET.RAW). Drives the result bar,
            // bench panel and on-ball player-name banner with the real 1:1
            // SWOS glyphs (NEAREST) instead of anti-aliased TTF. Missing asset
            // just leaves those HUD elements blank rather than crashing.
            {
                string charsetPath = System.IO.Path.Combine(grafsDir, "CHARSET.RAW");
                if (System.IO.File.Exists(charsetPath))
                {
                    try { _font = OpenSwos.Assets.SwosFont.Load(charsetPath); }
                    catch (System.Exception ex) { GD.PrintErr($"SwosFont load failed: {ex.Message}"); }
                }
                else GD.PrintErr($"CHARSET.RAW not found at {charsetPath} — HUD text disabled");
            }

            // OpenSWOS SWOS-styled front-end. Built on the ×2 menu CanvasLayer;
            // starts hidden, shown by UpdateUi when AppState==Menu. `this`
            // implements IMenuHost (Main.MenuHost.cs). Career data tables now share
            // this same 576×408 ×2 space (they used to sit on a separate ×2 layer);
            // one space, one scale — MenuClient routes table sprites to its own root.
            _menuClient = new OpenSwos.Menu.MenuClient(ui, _font, this, MenuViewportWidth, MenuViewportHeight);
            _menuClient.Active = false;
            _menuClient.TouchEnabled = _touchUi;

            // Match on-screen touch overlay (Android). Built ONCE, screen-space
            // (identity transform on its own CanvasLayer above the HUD). Only when
            // a touchscreen/--touch is present — desktop is untouched.
            if (_touchUi)
            {
                BuildTouchOverlay();
                GD.Print("[touch] menu-overlay active — d-pad drives menu nav, FIRE=CONFIRM, top-right=BACK");
            }

            // Android expand presentation (#231): build the black side bars and
            // apply the centring offset. No-op on every non-expand platform.
            if (_expandMode)
            {
                BuildSideBars();
                GetTree().Root.SizeChanged += ApplyExpandLayout;
                ApplyExpandLayout();
            }

            _scoreLabel = new Label
            {
                Text = "0 - 0",
                Position = new Vector2(4, 2),
                Modulate = Colors.White,
            };
            _scoreLabel.AddThemeFontSizeOverride("font_size", 8);
            hud.AddChild(_scoreLabel);

            // Match clock — top-centre. Viewport is 384 wide so X≈170 centres a small label.
            _timerLabel = new Label
            {
                Text = "00:00",
                Position = new Vector2(170, 2),
                Modulate = Colors.White,
            };
            _timerLabel.AddThemeFontSizeOverride("font_size", 8);
            hud.AddChild(_timerLabel);

            // Big-text overlay for GOAL! / HALF TIME / FULL TIME. Hidden when empty.
            _overlayLabel = new Label
            {
                Text = "",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // Size to the DESIGN viewport, not screen anchors: the ×RenderScale
                // layer transform would otherwise resolve anchors against 1152×816
                // and then scale again, overshooting.
                Position = new Vector2(0, 0),
                Size = new Vector2(ViewportWidth, ViewportHeight),
                Modulate = new Color(1f, 1f, 0.4f, 1f),  // SWOS-yellow
            };
            _overlayLabel.AddThemeFontSizeOverride("font_size", 16);
            hud.AddChild(_overlayLabel);

            // SWOS result panel (port mode). Visibility + duration are driven by
            // the ported result.cpp timers (Result.ShouldDrawResult — resultTimer
            // armed to 31000 at breakCameraMode 7, gameLoop.cpp:1808; 30000 at
            // half/full time) — timing untouched here. Layout is a 1:1 copy of
            // drawResult (result.cpp:145-156): NOT a centred box, but a
            // full-width DARKENED STRIP AT THE BOTTOM of the screen (drawGrid,
            // result.cpp:229-236 — x=0, width = whole screen incl. margins,
            // top = kGridTopY(167) + scorer-list offset, extending down to the
            // screen bottom; black @ alpha 127/255 per darkRectangle.cpp:53).
            // Team names / big score / scorer columns / HALF-FULL TIME tag sit
            // on it at the original VGA coordinates, mapped to our viewport
            // via ResultVgaToViewportX/Y (see those consts for the mapping
            // rationale). Per-frame geometry (the whole block slides up 7 px
            // per scorer line) is applied in LayoutResultPanel.
            // Team names / score / scorer columns are drawn with the SWOS
            // charset (SwosFont) — big font for the team names + score, small
            // font for the scorer lines — each a NEAREST Sprite2D positioned
            // per frame by LayoutResultPanel (kWhiteText, result.cpp:244-245,318).
            _resultPanelBg = new ColorRect
            {
                Color = new Color(0f, 0f, 0f, 127f / 255f),  // darkRectangle.cpp:53 — SDL_SetRenderDrawColor(0,0,0,127)
                Visible = false,
                MouseFilter = Control.MouseFilterEnum.Ignore,  // display-only; never eat touches
            };
            hud.AddChild(_resultPanelBg);
            _resultPanelTeam1 = MakeTextSprite(hud);   // drawTeamNames (result.cpp:238-246)
            _resultPanelTeam2 = MakeTextSprite(hud);
            _resultPanelScore = MakeTextSprite(hud);   // drawCurrentResult (result.cpp:248-264)
            _resultPanelTag   = MakeTextSprite(hud);   // drawHalfAndFullTimeSprites (result.cpp:326-332)
            for (int si = 0; si < OpenSwos.Sim.Port.Result.kMaxScorersForDisplay; si++)
            {
                _resultScorer1[si] = MakeTextSprite(hud);   // drawScorerList (result.cpp:301-324)
                _resultScorer2[si] = MakeTextSprite(hud);
            }

            // Controlled-player shirt numbers (port mode) — world-space labels
            // above each human team's controlled player. Data comes from the
            // ported updateControlledPlayerNumbers (GameSprites.cs, swos.asm:
            // 97583-97718): imageIndex kSmallDigit1+shirt-1, z = player z + 20,
            // hidden (-1) for CPU teams and during the marked-player blink.
            // BUG #160: crisp pixel-glyph sprites (NEAREST, integer scale) instead
            // of a TTF Label. The authentic pre-composed number sprites (SPRITE.DAT
            // ordinals kSmallDigit1..16 = 1188..1203) are not yet decoded from
            // either the PC or Amiga assets, so this draws the shirt number with a
            // compact SWOS-style pixel digit font (BuildShirtNumberTexture) — pixel-
            // exact and readable, matching the original's crisp look rather than a
            // blurred anti-aliased glyph. Follow-up: swap in the real 1188..1203
            // sprites once SPRITE.DAT/CHARSET are decoded.
            for (int ti = 0; ti < 2; ti++)
            {
                var spr = new Sprite2D
                {
                    Visible = false,
                    ZIndex = 200,
                    Centered = true,
                    TextureFilter = TextureFilterEnum.Nearest,
                    Modulate = Colors.White,
                };
                AddChild(spr);          // world node — scrolls with the camera
                _ctrlNumSprites[ti] = spr;
            }

            // SWOS bench / substitutions panel (port mode). Visibility + all
            // content are driven per-frame by UpdateBenchPanel() from the
            // ported bench FSM (Sim/Port/Bench.cs ← updateBench.cpp). Rendered
            // as a team-coloured panel (drawBench.cpp determineMenuTeamColors)
            // with the selected entry on a highlight bar, all text in the SWOS
            // charset small font. The row highlights are added BEFORE the text
            // sprites so glyphs paint on top of the bar.
            _benchPanelBg = new ColorRect
            {
                Color = new Color(0f, 0f, 0f, 0.85f),
                Visible = false,
                Position = new Vector2(0, 0),
                Size = new Vector2(0, 0),
                MouseFilter = Control.MouseFilterEnum.Ignore,  // display-only; never eat touches
            };
            hud.AddChild(_benchPanelBg);
            for (int i = 0; i < BenchMaxRows; i++)
            {
                var hl = new ColorRect { Visible = false, Color = Colors.Transparent, MouseFilter = Control.MouseFilterEnum.Ignore };
                hud.AddChild(hl);
                _benchHighlights[i] = hl;
            }
            for (int i = 0; i < BenchMaxRows; i++)
            {
                _benchLineSprites[i] = MakeTextSprite(hud);
            }

            // On-ball / controlled-player name banner (#169). drawPlayerName
            // (playerNameDisplay.cpp:57-70) draws "<number> <SURNAME>" at
            // (kPlayerNameX=12, kPlayerNameY=0) in game-screen space — the top
            // left corner. Mapped into our viewport with the same +gameOffsetX
            // the result bar uses.
            _playerNameSprite = MakeTextSprite(hud);

            // Legacy plain-text menu backdrop — superseded by MenuClient and now
            // always hidden (UpdateUi keeps it invisible), but retained. Authored
            // in 384×272 space, so it lives on the ×3 HUD layer alongside the other
            // ViewportWidth/Height nodes rather than the ×2 menu layer.
            _menuBackground = new ColorRect
            {
                Color = new Color(0f, 0f, 0f, 0.7f),
                Position = new Vector2(0, 0),
                Size = new Vector2(ViewportWidth, ViewportHeight),
                // Touch-transparent: a full-screen ColorRect defaults to
                // MouseFilter=Stop and would eat touches meant for the menu /
                // overlay. It is purely a dim backdrop, never interactive.
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            hud.AddChild(_menuBackground);

            // Menu backdrop image behind the SWOS front-end. A Sprite2D on the
            // MenuClient root (so it inherits the ×2 menu scale + Android expand
            // centring) with a very negative ZIndex so it draws behind every menu
            // widget. Scaled to COVER the 576×408 menu design space, centre-cropped
            // (the 16:9 art overflows top/bottom, which is the intended 4:3 crop).
            if (_menuClient is not null)
            {
                var bgTex = GD.Load<Texture2D>("res://data/openswos_bg.png");
                if (bgTex is not null)
                {
                    float cover = System.Math.Max(576f / bgTex.GetWidth(), 408f / bgTex.GetHeight());
                    var spr = new Sprite2D
                    {
                        Texture = bgTex,
                        Centered = false,
                        TextureFilter = TextureFilterEnum.Linear,
                        Scale = new Vector2(cover, cover),
                        Position = new Vector2((576f - bgTex.GetWidth() * cover) / 2f,
                                               (408f - bgTex.GetHeight() * cover) / 2f),
                        // ZIndex 1: above MenuClient's own navy backdrop (ZIndex 0),
                        // below every widget (ZIndex >= 8). Dimmed a touch so the
                        // vivid buttons stay readable over the busy art.
                        ZIndex = 1,
                        Modulate = new Color(0.62f, 0.62f, 0.62f),
                        Visible = false,
                    };
                    _menuClient.Root.AddChild(spr);
                    _menuBgSprite = spr;
                }
            }

            // Menu screen uses its own multi-line label at a smaller size so the team
            // list fits without scrolling. Renders on the same CanvasLayer so it sits on
            // top of the (preview) match scene underneath.
            _menuLabel = new Label
            {
                Text = "",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // Design-space size (see _overlayLabel note).
                Position = new Vector2(0, 0),
                Size = new Vector2(ViewportWidth, ViewportHeight),
                Modulate = Colors.White,
            };
            _menuLabel.AddThemeFontSizeOverride("font_size", 7);
            hud.AddChild(_menuLabel);

            // Start in the menu — kick-off positions are valid so the preview behind the
            // menu shows the chosen kits already coloured.
            EnterPreKickoff();
            _appState = AppState.Menu;

            // Skill-scaling league context (task #196): ScaleSkill's teamAppPercent
            // compares a team's average player value against the average across
            // ALL teams (swos.asm:38001-38006). Compute that league-wide average
            // once, over the full master list.
            {
                long sum = 0; int cnt = 0;
                foreach (var t in _allTeams)
                {
                    sum += OpenSwos.Sim.Port.SkillScaling.ComputeTeamValue(t);
                    cnt++;
                }
                OpenSwos.Sim.Port.SkillScaling.LeagueAvgTeamValue = cnt > 0 ? (int)(sum / cnt) : 0;
                GD.Print($"SkillScaling: league avg team value = {OpenSwos.Sim.Port.SkillScaling.LeagueAvgTeamValue} (enabled={OpenSwos.Sim.Port.SkillScaling.Enabled})");
            }

            GD.Print($"Boot complete. {_allTeams.Count} teams. Home={_allTeams[_homeTeamIndex].Name}, Away={_allTeams[_awayTeamIndex].Name}. Menu: arrows + space.");

            // Front-end music: create the node (host-side only) and start the
            // resolved source immediately on HOME (CUSTOM plays by default).
            EnsureMenuMusic();
            UpdateMenuMusic();

            // Deterministic render-alignment self-check (regression guard for the
            // "players drawn 10-15 px below the pitch" bug). A player standing at the
            // SWOS centre spot (336, 449 — swos-port pitchConstants.h:3-4) must have
            // his foot-anchor pixel land on the centre line of the drawn pitch bitmap,
            // which is bitmap row 449 - PitchBitmapWorldY = 433 (measured in
            // SWCPICH1.MAP). Both sides below are pure constant math:
            //  - feet: UpdateSprite puts the tile top-left at (X - cx, Y - cy) with the
            //    Amiga cell content flush at the tile's top-left, so the anchor pixel
            //    renders exactly at Godot (swosX + PitchOffsetX, swosY + PitchOffsetY).
            //  - spot: pitch node origin + bitmap row of the centre line.
            {
                int feetX = 336 + PitchOffsetX;
                int feetY = 449 + PitchOffsetY;
                int spotX = PitchOffsetX + 336;
                int spotY = (PitchOffsetY + PitchBitmapWorldY) + (449 - PitchBitmapWorldY);
                GD.Print($"Render-anchor check: feet=({feetX},{feetY}) centreSpot=({spotX},{spotY}) -> "
                    + (feetX == spotX && feetY == spotY ? "ALIGNED" : "MISALIGNED"));
            }

            // Smoke-test hook: when launched with `--swos-smoke` (or `--swos-test`)
            // on the command line, after boot completes, programmatically drive the
            // SWOS-port path through N ticks of Play and report pass/fail. Used by
            // CI / agent harnesses; not invoked in interactive launches.
            // Godot splits args at `--`: everything after goes to OS.GetCmdlineUserArgs(),
            // everything before stays in OS.GetCmdlineArgs(). We accept either.
            var allArgs = new System.Collections.Generic.List<string>(OS.GetCmdlineArgs());
            allArgs.AddRange(OS.GetCmdlineUserArgs());
            GD.Print($"[cmdline] all args: [{string.Join(", ", allArgs)}]");
            for (int i = 0; i < allArgs.Count; i++)
            {
                if (allArgs[i] == "--swos-smoke" || allArgs[i] == "--swos-test")
                {
                    int ticks = 100;
                    if (i + 1 < allArgs.Count && int.TryParse(allArgs[i + 1], out int parsed))
                        ticks = parsed;
                    RunSwosPortSmokeTest(ticks);
                    break;
                }
                if (allArgs[i] == "--keeper-release-test")
                {
                    RunKeeperReleaseTest();
                    break;
                }
                if (allArgs[i] == "--corner-goalkick-test")
                {
                    RunCornerGoalKickTest();
                    break;
                }
                if (allArgs[i] == "--goal-celebration-test")
                {
                    RunGoalCelebrationTest();
                    break;
                }
                if (allArgs[i] == "--halftime-ceremony-test")
                {
                    RunHalftimeCeremonyTest();
                    break;
                }
                if (allArgs[i] == "--referee-fsm-test")
                {
                    int ticks = 600;
                    if (i + 1 < allArgs.Count && int.TryParse(allArgs[i + 1], out int parsed))
                        ticks = parsed;
                    RunRefereeFsmTest(ticks);
                    break;
                }
                if (allArgs[i] == "--bench-test")
                {
                    RunBenchTest();
                    break;
                }
                if (allArgs[i] == "--competition-test")
                {
                    RunCompetitionEngineTest();
                    break;
                }
                if (allArgs[i] == "--career-report")
                {
                    RunCareerReport();
                    break;
                }
                if (allArgs[i] == "--headnum-test")
                {
                    RunHeadNumberTest();
                    break;
                }
                if (allArgs[i] == "--music-render")
                {
                    string outPath = (i + 1 < allArgs.Count) ? allArgs[i + 1] : "menu_music_test.wav";
                    double secs = 10.0;
                    if (i + 2 < allArgs.Count && double.TryParse(allArgs[i + 2],
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ps)) secs = ps;
                    var mod = OpenSwos.Audio.Rjp1Module.Load("MENU");
                    if (mod == null) GD.PrintErr("[music-render] MENU.SNG/INS not found");
                    else {
                        var st = OpenSwos.Audio.Rjp1Player.RenderSongToWav(mod, 0, secs, outPath);
                        GD.Print($"[music-render] {secs}s -> {outPath}: frames={st.frames} rmsL={st.rmsL:F4} rmsR={st.rmsR:F4} peak={st.peak:F4} noteOns={st.noteOns}");
                    }
                    GetTree().Quit();
                    return;
                }
                // --match-shot [dir] : screenshot verification hook for the Android
                // expand layout (#231). Starts a match immediately (same entry the
                // menu uses), then captures the touch overlay ON and OFF. Windowed
                // only — needs a real framebuffer to read back.
                if (allArgs[i] == "--match-shot")
                {
                    _matchShotActive = true;
                    _menuShotDir = (i + 1 < allArgs.Count && !allArgs[i + 1].StartsWith("--"))
                        ? allArgs[i + 1] : "menu_shots";
                    try { System.IO.Directory.CreateDirectory(_menuShotDir); } catch { }
                    _useSwosPort = true;
                    _swosVmDirty = true;
                    _match = MatchState.NewMatch();
                    EnterPreKickoff();
                    _match.EnterPhase(MatchPhase.Play);
                    _appState = AppState.Match;
                    if (_menuClient is not null) _menuClient.Active = false;
                    GD.Print($"[match-shot] active, output dir = {_menuShotDir}");
                    break;
                }
                if (allArgs[i] == "--menu-shot")
                {
                    _menuShotActive = true;
                    _menuShotDir = (i + 1 < allArgs.Count && !allArgs[i + 1].StartsWith("--"))
                        ? allArgs[i + 1] : "menu_shots";
                    try { System.IO.Directory.CreateDirectory(_menuShotDir); } catch { }
                    _appState = AppState.Menu;
                    if (_menuClient is not null) _menuClient.Active = true;
                    GD.Print($"[menu-shot] active, output dir = {_menuShotDir}");
                    break;
                }
            }
        }
        // (debug atlas overlay and tile preview removed — were leftover testing aids)
        catch (System.Exception ex)
        {
            GD.PrintErr($"Atlas load failed: {ex.Message}");
        }
    }

    // === Competition engine test =============================================
    // Headless: exercises CompetitionEngine end-to-end (league, cup, tournament,
    // career + season rollover) with deterministic seeds and prints PASS/FAIL.
    // No sim ticks — every result comes from the engine's own SimulateResult, so
    // this is a pure logic test of scheduling / standings / knockout / rollover.
    private void RunCompetitionEngineTest()
    {
        GD.Print("=== Competition engine test ===");
        int failures = 0;
        void Check(bool ok, string what)
        {
            if (ok) GD.Print($"  OK   {what}");
            else { GD.PrintErr($"  FAIL {what}"); failures++; }
        }

        System.Collections.Generic.List<OpenSwos.Competition.TeamRef> MakeTeams(int count)
        {
            var list = new System.Collections.Generic.List<OpenSwos.Competition.TeamRef>();
            for (int i = 0; i < count && i < _allTeams.Count; i++)
            {
                var t = _allTeams[i];
                int n = t.Players.Count, sum = 0;
                foreach (var p in t.Players)
                    sum += p.Passing + p.Shooting + p.Heading + p.Tackling + p.Control + p.Speed + p.Finishing;
                list.Add(new OpenSwos.Competition.TeamRef
                {
                    MasterIndex = i,
                    GlobalId = t.GlobalId,
                    Name = (t.Name ?? $"TEAM{i}").Trim().ToUpperInvariant(),
                    Strength = (n > 0) ? System.Math.Clamp(sum / (n * 7), 1, 7) : 3,
                });
            }
            return list;
        }

        // Drives a competition to completion: the "player" fixture is resolved
        // with the engine's own simulator (public SimulateResult), AI fixtures
        // via FastForwardAiOnly — exercising both recording paths.
        void PlayOut(OpenSwos.Competition.CompetitionState st, int guardMax)
        {
            int guard = 0;
            while (!st.Finished && guard++ < guardMax)
            {
                var mine = OpenSwos.Competition.CompetitionEngine.NextPlayerFixture(st);
                if (mine is not null)
                {
                    var (h, a) = OpenSwos.Competition.CompetitionEngine.SimulateResult(st, mine);
                    OpenSwos.Competition.CompetitionEngine.RecordResult(st, mine, h, a);
                }
                OpenSwos.Competition.CompetitionEngine.FastForwardAiOnly(st);
                if (st.Career is not null && OpenSwos.Competition.CompetitionEngine.PendingSeasonRollover(st))
                    break;   // career: season complete counts as "done" here
            }
        }

        try
        {
            // --- 1. League: 8 teams, double round robin -> 14 rounds, 56 games
            var lgTeams = MakeTeams(8);
            var lg = OpenSwos.Competition.CompetitionEngine.CreateLeague("TEST LEAGUE", lgTeams, 0, true, 1234);
            Check(lg.Fixtures.Count == 56, $"league fixture count 56 (got {lg.Fixtures.Count})");
            PlayOut(lg, 500);
            Check(lg.Finished, "league finishes");
            var table = OpenSwos.Competition.CompetitionEngine.Table(lg, "LEAGUE");
            Check(table.Count == 8, $"league table has 8 rows (got {table.Count})");
            bool all14 = true;
            foreach (var row in table) if (row.Played != 14) all14 = false;
            Check(all14, "every league team played 14");
            Check(lg.Champion >= 0 && table[0].Team == lg.Champion, "champion = table leader");

            // --- 2. Cup: 8 teams -> QF(4) + SF(2) + F(1) = 7 fixtures
            var cup = OpenSwos.Competition.CompetitionEngine.CreateCup("TEST CUP", MakeTeams(8), 0, 777);
            PlayOut(cup, 200);
            Check(cup.Finished, "cup finishes");
            Check(cup.Fixtures.Count == 7, $"cup total fixtures 7 (got {cup.Fixtures.Count})");
            Check(cup.Champion >= 0, "cup has a champion");
            bool cupScoresValid = true;
            foreach (var f in cup.Fixtures)
                if (!f.Played || f.HomeGoals < 0 || f.AwayGoals < 0) cupScoresValid = false;
            Check(cupScoresValid, "all cup fixtures played with scores");

            // --- 3. Tournament: 2 groups of 4 -> 12 group games + SF(2) + F(1)
            var tour = OpenSwos.Competition.CompetitionEngine.CreateTournament("TEST TOURNAMENT", MakeTeams(8), 0, 2, 4242);
            PlayOut(tour, 300);
            Check(tour.Finished, "tournament finishes");
            Check(tour.Fixtures.Count == 15, $"tournament total fixtures 15 (got {tour.Fixtures.Count})");

            // --- 4. Career: league 8 + cup 8, one season, then rollover
            var car = OpenSwos.Competition.CompetitionEngine.CreateCareer(
                "TEST CAREER", MakeTeams(8), MakeTeams(8), 0, 0, 1, 999);
            Check(car.Career is not null, "career state attached");

            // --- STEP 02: materialize the FULL career world (all clubs/players).
            var world = OpenSwos.Competition.Career.CareerWorldBuilder.BuildWorld(car, _allTeams);
            Check(car.Career!.World is not null, "career world materialized");
            var distinctIds = new System.Collections.Generic.HashSet<ushort>();
            foreach (var t in _allTeams) distinctIds.Add(t.GlobalId);
            Check(world.Clubs.Count == distinctIds.Count,
                $"world has one club per distinct team ({world.Clubs.Count}/{distinctIds.Count})");
            int playerRoster = 0;
            foreach (var t in _allTeams) if (t.GlobalId == car.Career!.ClubGlobalId) { playerRoster = t.Players.Count; break; }
            bool pClubOk = car.Career!.World!.Clubs.TryGetValue(car.Career!.ClubGlobalId, out var pClub)
                           && pClub.Squad.Count == playerRoster;
            Check(pClubOk, $"player club squad = its roster ({(pClub?.Squad.Count ?? -1)}/{playerRoster})");
            GD.Print($"  [world] {world.Clubs.Count} clubs, " +
                $"{OpenSwos.Competition.Career.CareerWorldBuilder.CountPlayers(world)} players materialized");

            // --- STEP A: verify age / stamina / potential distributions.
            int ageMin = 99, ageMax = -1, staMin = 99, staMax = -1;
            int youthCnt = 0, youthPotOk = 0, youthGems = 0, potBad = 0;
            static double Overall(OpenSwos.Competition.Career.CareerPlayer q)
                => (q.Passing + q.Shooting + q.Heading + q.Tackling + q.Control + q.Speed + q.Finishing) / 7.0;
            foreach (var club in world.Clubs.Values)
                foreach (var q in club.Squad)
                {
                    if (q.Age < ageMin) ageMin = q.Age;
                    if (q.Age > ageMax) ageMax = q.Age;
                    if (q.Stamina < staMin) staMin = q.Stamina;
                    if (q.Stamina > staMax) staMax = q.Stamina;
                    double ov = Overall(q);
                    if (q.Potential < ov - 0.5 || q.Potential > 7.0001) potBad++;
                    if (q.Age <= 21)
                    {
                        youthCnt++;
                        if (q.Potential >= ov - 0.0001) youthPotOk++;
                        if (q.Potential - ov >= 2.0) youthGems++;
                    }
                }
            // Real 1996/97 ages from the offline table can legitimately exceed the
            // old skill-derived [16,38] band (veteran keepers ~40, young debutants
            // ~15), so the world-wide range is widened to KnownAges' clamp [15,45].
            Check(ageMin >= 15 && ageMax <= 45, $"ages within [15,45] (got {ageMin}..{ageMax})");
            Check(ageMin < 22 && ageMax > 30, $"age spread present (min {ageMin}, max {ageMax})");
            Check(staMin >= 0 && staMax <= 7, $"stamina within [0,7] (got {staMin}..{staMax})");
            Check(potBad == 0, $"potential within [overall,7] for all ({potBad} out of range)");
            Check(youthCnt > 0 && youthPotOk == youthCnt, $"youth potential >= current ({youthPotOk}/{youthCnt})");
            Check(youthGems * 100 < youthCnt * 30, $"gems rare (<30% of youth): {youthGems}/{youthCnt}");
            GD.Print($"  [attrs] age {ageMin}..{ageMax}, stamina {staMin}..{staMax}, youth {youthCnt}, gems {youthGems}");

            // --- STEP 18: ORIGINAL SWOS transfer offers (Career/TransferOffers.cs).
            // List a player, run the deterministic offer Tick, then accept a bid.
            {
                var offClub = car.Career!.World!.Clubs[car.Career!.ClubGlobalId];
                Check(car.Career!.TimeToNegotiate == 6, $"time-to-negotiate seeded 6 (got {car.Career!.TimeToNegotiate})");
                int listedId = offClub.Squad.Count > 0 ? offClub.Squad[0].Id : -1;
                Check(OpenSwos.Competition.Career.TransferOffers.ListPlayer(car, listedId), "player transfer-listed");
                // Offers expire after 1-6 rounds, so Tick until one lands (deterministic).
                var offers = car.Career!.PendingOffers;
                int ticks = 0;
                for (; ticks < 400 && offers.Count == 0; ticks++)
                    OpenSwos.Competition.Career.TransferOffers.Tick(car);
                Check(offers.Count >= 1, $"at least one offer arrived within {ticks} ticks (got {offers.Count})");
                if (offers.Count >= 1)
                {
                    var off = offers[0];
                    OpenSwos.Competition.Career.CareerPlayer? offPlayer = null;
                    foreach (var q in offClub.Squad) if (q.Id == off.PlayerId) { offPlayer = q; break; }
                    Check(offPlayer is not null, "offer targets a squad player");
                    long ov = offPlayer is null ? 0 : OpenSwos.Competition.Career.Finance.PlayerValue(offPlayer);
                    long pct = ov > 0 ? off.Amount * 100 / ov : 0;
                    Check(offPlayer is not null && off.Amount >= ov * 85 / 100 && off.Amount <= ov * 119 / 100,
                        $"offer amount 85-119% of value (amount {off.Amount}, value {ov}, {pct}%)");
                    Check(off.ExpiryRounds >= 1 && off.ExpiryRounds <= 6, $"offer expiry in [1,6] (got {off.ExpiryRounds})");
                    ushort bidder = off.BidderClubId;
                    long sellerBudget0 = offClub.Budget;
                    long amount = off.Amount;
                    int soldId = off.PlayerId;
                    bool accepted = OpenSwos.Competition.Career.TransferOffers.Accept(car, car.Career!.World!, off);
                    Check(accepted, "offer accepted");
                    var bidderClub = car.Career!.World!.Clubs[bidder];
                    Check(bidderClub.Squad.Exists(q => q.Id == soldId) && !offClub.Squad.Exists(q => q.Id == soldId),
                        "sold player moved to bidder club");
                    Check(offClub.Budget == sellerBudget0 + amount,
                        $"seller credited amount ({sellerBudget0}->{offClub.Budget}, +{amount})");
                    Check(car.Career!.TimeToNegotiate == 5, $"time-to-negotiate spent to 5 (got {car.Career!.TimeToNegotiate})");
                    GD.Print($"  [offers] listed {listedId}, offers {offers.Count + 1}, amount {amount} " +
                        $"(value {ov}, {pct}%, exp {off.ExpiryRounds}), sold {soldId} -> club {bidder}");
                }
                // Reset the market so it does not perturb the season play-out below.
                car.Career!.PendingOffers.Clear();
                car.Career!.TransferListedPlayerIds.Clear();
                car.Career!.TimeToNegotiate = 6;
                car.Career!.SellsThisSeason = 0;
            }

            PlayOut(car, 800);
            Check(OpenSwos.Competition.CompetitionEngine.PendingSeasonRollover(car), "season rollover pending after season");
            Check(car.Career!.History.Count >= 1, "career history line written");
            OpenSwos.Competition.CompetitionEngine.AdvanceCareerSeason(car, MakeTeams(8), MakeTeams(8), 0);
            Check(car.Career!.Season == 2, $"career season advanced to 2 (got {car.Career!.Season})");
            Check(!car.Finished && OpenSwos.Competition.CompetitionEngine.NextPlayerFixture(car) is not null,
                "season 2 has fixtures to play");

            // --- STEP 07: multi-season ageing + retirement (no regen yet, so the
            // world only shrinks; refill lands in a later step).
            int startSeason = car.Career!.Season;   // 2
            var ageClub0 = car.Career!.World!.Clubs[car.Career!.ClubGlobalId];
            int sampleId = ageClub0.Squad.Count > 0 ? ageClub0.Squad[0].Id : -1;
            int sampleAge0 = ageClub0.Squad.Count > 0 ? ageClub0.Squad[0].Age : -1;
            var origIds = new System.Collections.Generic.HashSet<int>();
            foreach (var q in ageClub0.Squad) origIds.Add(q.Id);
            // growth tracking: pick a young high-potential prospect from the world.
            OpenSwos.Competition.Career.CareerPlayer? prospect = null;
            foreach (var club in car.Career!.World!.Clubs.Values)
            {
                foreach (var q in club.Squad)
                    if (q.Age <= 19 && q.Potential - Overall(q) >= 1.5) { prospect = q; break; }
                if (prospect is not null) break;
            }
            int prospectId = prospect?.Id ?? -1;
            double prospectOv0 = prospect is not null ? Overall(prospect) : 0;
            double prospectPot = prospect?.Potential ?? 0;
            for (int s2 = 0; s2 < 8; s2++)
                OpenSwos.Competition.CompetitionEngine.AdvanceCareerSeason(car, MakeTeams(8), MakeTeams(8), 0);
            int seasonsElapsed = car.Career!.Season - startSeason;
            Check(car.Career!.World!.Season == car.Career!.Season,
                $"world season synced to career ({car.Career!.World!.Season}/{car.Career!.Season})");
            // ageing: sample player aged by seasons elapsed (or retired/left).
            var pClubNow = car.Career!.World!.Clubs[car.Career!.ClubGlobalId];
            OpenSwos.Competition.Career.CareerPlayer? sp = null;
            foreach (var q in pClubNow.Squad) if (q.Id == sampleId) { sp = q; break; }
            Check(sp is null || sp.Age == sampleAge0 + seasonsElapsed,
                sp is null ? "sample player retired/left (ok)" : $"sample aged +{seasonsElapsed} ({sampleAge0}->{sp.Age})");
            // retirement + regen churn: some originals gone, some fresh youths in.
            int origSurvivors = 0, freshFaces = 0;
            foreach (var q in pClubNow.Squad) { if (origIds.Contains(q.Id)) origSurvivors++; else freshFaces++; }
            Check(origSurvivors < origIds.Count, $"some original players retired/left ({origSurvivors}/{origIds.Count} remain)");
            Check(freshFaces > 0, $"regen brought in fresh players ({freshFaces})");
            // STEP 10: world stays populated (no collapse), pool bounded, squads fieldable.
            int minSquad = int.MaxValue, shortClubs = 0;
            foreach (var club in car.Career!.World!.Clubs.Values)
            { if (club.Squad.Count < minSquad) minSquad = club.Squad.Count; if (club.Squad.Count < 11) shortClubs++; }
            int faPool = car.Career!.World!.FreeAgents.Count;
            int worldNow = OpenSwos.Competition.Career.CareerWorldBuilder.CountPlayers(car.Career!.World!);
            Check(shortClubs == 0, $"every club fields >=11 after regen ({shortClubs} short, min {minSquad})");
            Check(worldNow > 27680 * 8 / 10, $"world stays populated after regen (got {worldNow})");
            Check(faPool <= 300, $"free-agent pool bounded (<=300, got {faPool})");
            GD.Print($"  [ageing/regen] {seasonsElapsed} seasons, sample {sampleAge0}->{(sp?.Age ?? -1)}, " +
                $"club fresh {freshFaces}, min squad {minSquad}, free agents {faPool}, world {worldNow}");

            // --- REGEN NAMES: after ~10 seasons of intake, every living GENERATED
            // player must carry a two-token "FIRST SURNAME" (no bare nicknames) and
            // no two generated players may share a full name (surname uniqueness).
            {
                var genNames = new System.Collections.Generic.Dictionary<string, int>();
                int genCount = 0, singleToken = 0, dupNames = 0;
                string sampleDup = "", sampleSingle = "";
                void Inspect(OpenSwos.Competition.Career.CareerPlayer q)
                {
                    if (q is null || !q.Generated || q.Retired) return;
                    genCount++;
                    string nm = (q.Name ?? "").Trim();
                    if (nm.IndexOf(' ') < 0) { singleToken++; if (sampleSingle.Length == 0) sampleSingle = nm; }
                    genNames.TryGetValue(nm, out int seen);
                    genNames[nm] = seen + 1;
                    if (seen == 1) { dupNames++; if (sampleDup.Length == 0) sampleDup = nm; }
                }
                foreach (var club in car.Career!.World!.Clubs.Values)
                    foreach (var q in club.Squad) Inspect(q);
                foreach (var q in car.Career!.World!.FreeAgents) Inspect(q);
                Check(genCount > 0, $"generated players exist to check ({genCount})");
                Check(singleToken == 0, $"no single-token generated name ({singleToken} found, e.g. '{sampleSingle}')");
                Check(dupNames == 0, $"no duplicate full name among generated players ({dupNames} dup names, e.g. '{sampleDup}')");
                GD.Print($"  [regen-names] {genCount} living generated players, " +
                    $"{genNames.Count} distinct names, {singleToken} single-token, {dupNames} duplicated");
            }

            // --- STEP 09: growth — a young prospect trends UP toward potential.
            OpenSwos.Competition.Career.CareerPlayer? grown = null;
            if (prospectId >= 0)
                foreach (var club in car.Career!.World!.Clubs.Values)
                {
                    foreach (var q in club.Squad) if (q.Id == prospectId) { grown = q; break; }
                    if (grown is not null) break;
                }
            Check(grown is null || Overall(grown) > prospectOv0 + 0.001,
                grown is null ? "prospect left the world (ok)"
                              : $"young prospect grew ({prospectOv0:0.00}->{Overall(grown):0.00}, pot {prospectPot:0.00})");
            // controlled decline: an isolated 34-year-old loses skill under growth.
            var tw = new OpenSwos.Competition.Career.CareerWorld();
            var vet = new OpenSwos.Competition.Career.CareerPlayer
            {
                Id = 1, Age = 34, Passing = 5, Shooting = 5, Heading = 5, Tackling = 5,
                Control = 5, Speed = 5, Finishing = 5, Potential = 5,
            };
            var twClub = new OpenSwos.Competition.Career.CareerClub { GlobalId = 1 };
            twClub.Squad.Add(vet);
            tw.Clubs[1] = twClub;
            double vetBefore = Overall(vet);
            for (int k = 0; k < 8; k++) { tw.Season = k + 1; OpenSwos.Competition.Career.GrowthModel.ApplySeasonGrowth(tw); }
            Check(Overall(vet) < vetBefore, $"veteran (34) declines under growth ({vetBefore:0.00}->{Overall(vet):0.00})");
            // keeper development: ability lives in ValueCode, not the 7 skills —
            // a young high-potential keeper must gain EFF over seasons.
            var gkw = new OpenSwos.Competition.Career.CareerWorld();
            var youngGk = new OpenSwos.Competition.Career.CareerPlayer
            { Id = 1, Age = 18, Position = "G", ValueCode = 10, Potential = 6.5 };
            var gkClub = new OpenSwos.Competition.Career.CareerClub { GlobalId = 1 };
            gkClub.Squad.Add(youngGk);
            gkw.Clubs[1] = gkClub;
            int gkEff0 = youngGk.EffectiveOverall();
            for (int k = 0; k < 6; k++) { gkw.Season = k + 1; OpenSwos.Competition.Career.GrowthModel.ApplySeasonGrowth(gkw); }
            Check(youngGk.EffectiveOverall() > gkEff0,
                $"young keeper develops via ValueCode (EFF {gkEff0}->{youngGk.EffectiveOverall()})");
            GD.Print($"  [growth] prospect {prospectOv0:0.00}->{(grown is null ? -1 : Overall(grown)):0.00} " +
                $"(pot {prospectPot:0.00}), vet {vetBefore:0.00}->{Overall(vet):0.00}");

            // --- POST-MATCH INJURIES (persistence + recovery + availability).
            {
                // (a) A severity-4 injury recovers stepwise over fixtures and
                // eventually heals fully. Recovery is deterministic per fixture
                // (CareerRng keyed by the round injury seed + player id).
                var injured = new OpenSwos.Competition.Career.CareerPlayer { Id = 4242, InjurySeverity = 4 };
                int prev = injured.InjurySeverity, roseCount = 0, healedAt = -1;
                for (int round = 0; round < 40; round++)
                {
                    OpenSwos.Competition.Career.MatchEffects.RecoverInjury(
                        injured, OpenSwos.Competition.Career.MatchEffects.InjurySeedForRound(round));
                    if (injured.InjurySeverity > prev) roseCount++;
                    if (injured.InjurySeverity == 0 && healedAt < 0) healedAt = round;
                    prev = injured.InjurySeverity;
                }
                Check(roseCount == 0, $"severity-4 injury never worsens during recovery ({roseCount} rises)");
                Check(healedAt >= 0, $"severity-4 injury heals fully within 40 fixtures (healed at {healedAt})");

                // (c) A severity-7 injury NEVER heals (career-ending).
                var perma = new OpenSwos.Competition.Career.CareerPlayer { Id = 4343, InjurySeverity = 7 };
                for (int t = 0; t < 50; t++)
                    OpenSwos.Competition.Career.MatchEffects.RecoverInjury(
                        perma, OpenSwos.Competition.Career.MatchEffects.InjurySeedForRound(t));
                Check(perma.InjurySeverity == 7, $"severity-7 injury never recovers over 50 ticks (got {perma.InjurySeverity})");

                // (b) A severity>=2 player is excluded from the BuildOrder XI and
                // demoted out of a PreferredLineup (which degrades gracefully).
                var injClub = new OpenSwos.Competition.Career.CareerClub { GlobalId = 9001 };
                for (int pi = 1; pi <= 16; pi++)
                    injClub.Squad.Add(new OpenSwos.Competition.Career.CareerPlayer
                    {
                        Id = pi, Position = pi == 1 ? "G" : "M", ShirtNumber = (byte)pi,
                        Passing = 4, Shooting = 4, Heading = 4, Tackling = 4, Control = 4, Speed = 4, Finishing = 4,
                    });
                // Prefer ids 1..11 as the XI; injure the id-5 starter (severity 3).
                injClub.PreferredLineup = new System.Collections.Generic.List<int> { 1,2,3,4,5,6,7,8,9,10,11 };
                injClub.Squad.Find(q => q.Id == 5)!.InjurySeverity = 3;
                var order = OpenSwos.Competition.Career.CareerMatchTeam.BuildOrder(injClub, null);
                bool keeperFirst = order.Count > 0 && OpenSwos.Competition.Career.CareerMatchTeam.IsKeeper(order[0]);
                bool injuredOutOfXI = true;
                for (int sl = 0; sl < 11 && sl < order.Count; sl++)
                    if (order[sl].Id == 5) injuredOutOfXI = false;
                Check(keeperFirst, "injured-squad BuildOrder keeps a keeper in slot 0");
                Check(order.Count >= 11, $"injured-squad BuildOrder still fields >=11 ({order.Count})");
                Check(injuredOutOfXI, "severity-3 preferred starter demoted out of the XI (compacts, no hard fail)");

                // Guard: if too few are fit, the least-injured are re-admitted so we
                // never field fewer than 11 when bodies exist.
                var thinClub = new OpenSwos.Competition.Career.CareerClub { GlobalId = 9002 };
                for (int pi = 1; pi <= 12; pi++)
                    thinClub.Squad.Add(new OpenSwos.Competition.Career.CareerPlayer
                    { Id = pi, Position = pi == 1 ? "G" : "M", ShirtNumber = (byte)pi,
                      InjurySeverity = pi >= 6 ? 4 : 0 });   // 5 fit + 7 injured
                var thinOrder = OpenSwos.Competition.Career.CareerMatchTeam.BuildOrder(thinClub, null);
                Check(thinOrder.Count >= 11, $"fit-pool guard re-admits least-injured to reach 11 ({thinOrder.Count})");

                GD.Print($"  [injuries] sev4 healed@{healedAt}, sev7 stayed {perma.InjurySeverity}, " +
                    $"XI={order.Count} (injured id5 out={injuredOutOfXI}), thin XI={thinOrder.Count}");
            }

            // --- STEP 13: finances — budgets bounded, non-degenerate, value-linked.
            long budMin = long.MaxValue, budMax = long.MinValue;
            long richBudget = long.MinValue, richVal = long.MinValue;
            long poorBudget = 0, poorVal = long.MaxValue;
            bool budgetsSane = true;
            foreach (var club in car.Career!.World!.Clubs.Values)
            {
                long b = club.Budget;
                if (b < budMin) budMin = b;
                if (b > budMax) budMax = b;
                if (System.Math.Abs(b) > 1_000_000_000_000_000L) budgetsSane = false;   // no overflow/runaway
                long v = OpenSwos.Competition.Career.Finance.ClubValue(club);
                if (v > richVal) { richVal = v; richBudget = b; }
                if (v < poorVal) { poorVal = v; poorBudget = b; }
            }
            Check(budgetsSane, $"budgets bounded / no overflow (min {budMin}, max {budMax})");
            Check(budMax != budMin, "budgets vary across clubs");
            GD.Print($"  [finance] budget {budMin}..{budMax}; top-value club budget {richBudget} (val {richVal}), " +
                $"low-value club budget {poorBudget} (val {poorVal})");

            // --- STEP 14: coaches — a focused youth under a good YOUTH coach develops
            // faster than the same youth with no staff. Compare a fine metric
            // (skill + fractional GrowthCarry) so sub-integer progress counts.
            static double Fine(OpenSwos.Competition.Career.CareerPlayer q)
            {
                double[] s = { q.Passing, q.Shooting, q.Heading, q.Tackling, q.Control, q.Speed, q.Finishing };
                double sum = 0;
                for (int i = 0; i < 7; i++) sum += s[i] + (q.GrowthCarry.Length > i ? q.GrowthCarry[i] : 0);
                return sum / 7.0;
            }
            static OpenSwos.Competition.Career.CareerWorld MakeYouthWorld(bool withCoach)
            {
                var w = new OpenSwos.Competition.Career.CareerWorld();
                var youth = new OpenSwos.Competition.Career.CareerPlayer
                {
                    Id = 1, Age = 17, Position = "M",
                    Passing = 2, Shooting = 2, Heading = 2, Tackling = 2, Control = 2, Speed = 2, Finishing = 2,
                    Potential = 7,
                };
                var cl = new OpenSwos.Competition.Career.CareerClub { GlobalId = 1 };
                cl.Squad.Add(youth);
                if (withCoach)
                {
                    cl.Coaches.Add(new OpenSwos.Competition.Career.Coach { Id = 100, Quality = 7, Specialty = "YOUTH", Wage = 0 });
                    cl.TrainingFocusIds.Add(1);
                }
                w.Clubs[1] = cl;
                return w;
            }
            var wNo = MakeYouthWorld(false);
            var wCoach = MakeYouthWorld(true);
            for (int k = 0; k < 3; k++)
            {
                wNo.Season = k + 1; OpenSwos.Competition.Career.GrowthModel.ApplySeasonGrowth(wNo);
                wCoach.Season = k + 1; OpenSwos.Competition.Career.GrowthModel.ApplySeasonGrowth(wCoach);
            }
            double fineNo = Fine(wNo.Clubs[1].Squad[0]);
            double fineCoach = Fine(wCoach.Clubs[1].Squad[0]);
            Check(fineCoach > fineNo, $"coached+focused youth develops faster ({fineNo:0.000} vs {fineCoach:0.000})");
            GD.Print($"  [coaches] youth dev after 3 seasons: no-coach {fineNo:0.000}, coached {fineCoach:0.000}");

            // --- STEP 11-12: scouting — fuzzy estimate; better scout & repeats tighten
            // the range; width is always > 0 (never 100% certain).
            static OpenSwos.Competition.Career.CareerPlayer MakeScoutee(int id)
                => new()
                {
                    Id = id, Age = 18, Potential = 6.0,
                    Passing = 3, Shooting = 3, Heading = 3, Tackling = 3, Control = 3, Speed = 3, Finishing = 3,
                };
            var lowScout = MakeScoutee(1);
            var hiScout = MakeScoutee(2);
            OpenSwos.Competition.Career.Scouting.RevealEstimate(lowScout, 1, 0xC0FFEEu);
            OpenSwos.Competition.Career.Scouting.RevealEstimate(hiScout, 7, 0xC0FFEEu);
            double wLow = lowScout.EstHigh - lowScout.EstLow;
            double wHi = hiScout.EstHigh - hiScout.EstLow;
            Check(lowScout.Scouted && hiScout.Scouted, "scouting marks players scouted");
            Check(wLow > 0 && wHi > 0, $"estimate ranges have positive width (q1 {wLow:0.00}, q7 {wHi:0.00})");
            Check(wHi < wLow, $"better scout gives a tighter range (q7 {wHi:0.00} < q1 {wLow:0.00})");
            var rep = MakeScoutee(3);
            OpenSwos.Competition.Career.Scouting.RevealEstimate(rep, 4, 0xBEEFu);
            double wFirst = rep.EstHigh - rep.EstLow;
            for (int k = 0; k < 4; k++) OpenSwos.Competition.Career.Scouting.RevealEstimate(rep, 4, 0xBEEFu);
            double wMany = rep.EstHigh - rep.EstLow;
            Check(wMany < wFirst && wMany > 0, $"repeat scouting tightens the range ({wFirst:0.00}->{wMany:0.00})");
            GD.Print($"  [scouting] range width q1 {wLow:0.00}, q7 {wHi:0.00}; repeat {wFirst:0.00}->{wMany:0.00}");

            // --- STEP 17: form — win streak lifts form (+1 skill), loss streak drops
            // it (-1), sitting out decays back to neutral.
            var winner = new OpenSwos.Competition.Career.CareerPlayer { Id = 1, Form = 0 };
            for (int k = 0; k < 6; k++) OpenSwos.Competition.Career.FormModel.UpdateFormAfterMatch(winner, +1, 90, 0x11u);
            Check(winner.Form == 3 && OpenSwos.Competition.Career.FormModel.FormSkillDelta(winner) == 1,
                $"win streak -> form +3, +1 skill (form {winner.Form})");
            var loser = new OpenSwos.Competition.Career.CareerPlayer { Id = 2, Form = 0 };
            for (int k = 0; k < 6; k++) OpenSwos.Competition.Career.FormModel.UpdateFormAfterMatch(loser, -1, 90, 0x22u);
            Check(loser.Form == -3 && OpenSwos.Competition.Career.FormModel.FormSkillDelta(loser) == -1,
                $"loss streak -> form -3, -1 skill (form {loser.Form})");
            int hotForm = winner.Form;
            for (int k = 0; k < 6; k++) OpenSwos.Competition.Career.FormModel.UpdateFormAfterMatch(winner, 0, 0, 0x11u);
            Check(winner.Form == 0, $"idle form decays to neutral (was {hotForm}, now {winner.Form})");
            GD.Print($"  [form] win-streak {3}, loss-streak {-3}, decayed-to {winner.Form}");

            // --- STEP 15-16: fatigue — low stamina tires more; in-match penalty
            // monotone & bounded; young + high-stamina recover faster between matches.
            int gainLowSta = OpenSwos.Competition.Career.FatigueModel.MatchFatigueGain(1000, 1);
            int gainHiSta = OpenSwos.Competition.Career.FatigueModel.MatchFatigueGain(1000, 7);
            Check(gainLowSta > gainHiSta, $"low-stamina tires more ({gainLowSta} vs {gainHiSta} at equal distance)");
            int pen0 = OpenSwos.Competition.Career.FatigueModel.SkillPenalty(0);
            int pen60 = OpenSwos.Competition.Career.FatigueModel.SkillPenalty(60);
            int pen90 = OpenSwos.Competition.Career.FatigueModel.SkillPenalty(90);
            Check(pen0 == 0 && pen60 <= pen0 && pen90 <= pen60 && pen90 >= -2,
                $"in-match skill penalty monotone & bounded ({pen0},{pen60},{pen90})");
            var youngFit = new OpenSwos.Competition.Career.CareerPlayer { Id = 1, Age = 19, Stamina = 7, FatigueCarry = 80 };
            var oldTired = new OpenSwos.Competition.Career.CareerPlayer { Id = 2, Age = 34, Stamina = 1, FatigueCarry = 80 };
            OpenSwos.Competition.Career.FatigueModel.RecoverBetweenMatches(youngFit, 7);
            OpenSwos.Competition.Career.FatigueModel.RecoverBetweenMatches(oldTired, 7);
            Check(youngFit.FatigueCarry < oldTired.FatigueCarry,
                $"young+fit recovers more (young {youngFit.FatigueCarry} < old {oldTired.FatigueCarry})");
            Check(youngFit.FatigueCarry >= 0 && oldTired.FatigueCarry <= 100, "fatigue stays within [0,100]");
            GD.Print($"  [fatigue] gain sta1 {gainLowSta} vs sta7 {gainHiSta}; penalty 0/60/90 = {pen0}/{pen60}/{pen90}; " +
                $"recover young->{youngFit.FatigueCarry} old->{oldTired.FatigueCarry}");

            // --- 4a. Default lineup: an untouched club (no PreferredLineup) must
            // follow its ORIGINAL TeamRecord lineup, not a position/ability resort.
            // Uses a FRESH world so no earlier transfer/season test has mutated the
            // squad — the projection is then a straight slot->roster mapping.
            {
                var freshCar = OpenSwos.Competition.CompetitionEngine.CreateCareer(
                    "LINEUP CHECK", MakeTeams(8), MakeTeams(8), 0, 0, 1, 998);
                OpenSwos.Competition.Career.CareerWorldBuilder.BuildWorld(freshCar, _allTeams);
                TeamRecord? fBase = null;
                foreach (var t in _allTeams) if (t.GlobalId == freshCar.Career!.ClubGlobalId) { fBase = t; break; }
                freshCar.Career!.World!.Clubs.TryGetValue(freshCar.Career!.ClubGlobalId, out var fClub);
                if (fClub is not null && fBase is not null && fBase.LineupOrder is { Length: 16 })
                {
                    // Independently project base LineupOrder -> expected CareerPlayer ids
                    // (same ShirtNumber+Name match + skip-missing rule as BuildFromBaseLineup).
                    var expected = new System.Collections.Generic.List<int>();
                    var usedE = new System.Collections.Generic.HashSet<int>();
                    foreach (byte ri in fBase.LineupOrder)
                    {
                        if (ri >= fBase.Players.Count) continue;
                        var pr = fBase.Players[ri];
                        foreach (var cp in fClub.Squad)
                        {
                            if (usedE.Contains(cp.Id)) continue;
                            if (cp.ShirtNumber == pr.ShirtNumber
                                && string.Equals((cp.Name ?? "").Trim(), (pr.Name ?? "").Trim(), System.StringComparison.OrdinalIgnoreCase))
                            { expected.Add(cp.Id); usedE.Add(cp.Id); break; }
                        }
                        if (expected.Count >= 11) break;
                    }
                    var def = OpenSwos.Competition.Career.CareerMatchTeam.BuildOrder(fClub, fBase);
                    bool baseDefaultOk = def.Count >= 11 && expected.Count >= 11;
                    for (int slot = 0; slot < 11 && baseDefaultOk; slot++)
                        if (def[slot].Id != expected[slot]) baseDefaultOk = false;
                    Check(baseDefaultOk, "untouched club default lineup follows base TeamRecord order");
                }
            }

            // Base team record for the player's club (used by the preferred-lineup test).
            TeamRecord? pBase = null;
            foreach (var t in _allTeams) if (t.GlobalId == car.Career!.ClubGlobalId) { pBase = t; break; }

            // --- 4b. Preferred lineup: set a custom order, verify it survives.
            var preferredIds = new System.Collections.Generic.List<int>();
            if (pClub is not null)
            {
                var order = OpenSwos.Competition.Career.CareerMatchTeam.BuildOrder(pClub, pBase);
                foreach (var p in order) preferredIds.Add(p.Id);
                // Swap two outfield slots (3 and 4) so it differs from auto.
                if (preferredIds.Count > 4) (preferredIds[3], preferredIds[4]) = (preferredIds[4], preferredIds[3]);
                pClub.PreferredLineup = new System.Collections.Generic.List<int>(preferredIds);
                // BuildOrder must honor it: slot 0 keeper preserved, slot 3/4 swapped.
                var rebuilt = OpenSwos.Competition.Career.CareerMatchTeam.BuildOrder(pClub);
                bool honored = rebuilt.Count >= 5 && rebuilt[0].Id == preferredIds[0]
                    && rebuilt[3].Id == preferredIds[3] && rebuilt[4].Id == preferredIds[4];
                Check(honored, "preferred lineup honored by BuildOrder");
            }

            // --- 5. Store round-trip
            OpenSwos.Competition.CompetitionStore.Save(car);
            var loaded = OpenSwos.Competition.CompetitionStore.Load();
            Check(loaded is not null && loaded.Career?.Season == car.Career?.Season && loaded.Fixtures.Count == car.Fixtures.Count,
                "store save/load round-trip");
            // STEP 02: the CareerWorld must survive JSON save/load intact.
            bool worldRoundTrip = loaded?.Career?.World is not null
                && loaded.Career.World.Clubs.Count == world.Clubs.Count
                && loaded.Career.World.Clubs.TryGetValue(car.Career!.ClubGlobalId, out var lpc)
                && pClub is not null && lpc.Squad.Count == pClub.Squad.Count
                && (pClub.Squad.Count == 0 || lpc.Squad[0].Name == pClub.Squad[0].Name);
            Check(worldRoundTrip, "career world survives save/load");
            // Preferred lineup must survive JSON save/load intact.
            bool lineupRoundTrip = loaded?.Career?.World is not null
                && loaded.Career.World.Clubs.TryGetValue(car.Career!.ClubGlobalId, out var llc)
                && llc.PreferredLineup.Count == preferredIds.Count;
            if (lineupRoundTrip && loaded is not null
                && loaded.Career!.World!.Clubs.TryGetValue(car.Career!.ClubGlobalId, out var llc2))
                for (int i = 0; i < preferredIds.Count; i++)
                    if (llc2.PreferredLineup[i] != preferredIds[i]) { lineupRoundTrip = false; break; }
            Check(lineupRoundTrip, "preferred lineup survives save/load");
            OpenSwos.Competition.CompetitionStore.Delete();
            Check(!OpenSwos.Competition.CompetitionStore.Exists(), "store delete");
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"  FAIL exception: {ex}");
            failures++;
        }

        GD.Print(failures == 0
            ? "COMPETITION ENGINE TEST PASSED"
            : $"COMPETITION ENGINE TEST FAILED ({failures} failures)");
        GetTree().Quit(failures == 0 ? 0 : 1);
    }

    // Headless human-readable career snapshot so a human can eyeball the result of
    // the career systems (ages / scouted potential / form / fatigue / regen).
    // Run: ... --headless --path game --career-report
    private void RunCareerReport()
    {
        GD.Print("=== Career report ===");
        System.Collections.Generic.List<OpenSwos.Competition.TeamRef> Teams(int start, int count)
        {
            var list = new System.Collections.Generic.List<OpenSwos.Competition.TeamRef>();
            for (int i = start; i < start + count && i < _allTeams.Count; i++)
            {
                var t = _allTeams[i];
                int n = t.Players.Count, sum = 0;
                foreach (var p in t.Players)
                    sum += p.Passing + p.Shooting + p.Heading + p.Tackling + p.Control + p.Speed + p.Finishing;
                list.Add(new OpenSwos.Competition.TeamRef
                {
                    MasterIndex = i,
                    GlobalId = t.GlobalId,
                    Name = (t.Name ?? $"TEAM{i}").Trim().ToUpperInvariant(),
                    Strength = (n > 0) ? System.Math.Clamp(sum / (n * 7), 1, 7) : 3,
                });
            }
            return list;
        }
        static double Ov(OpenSwos.Competition.Career.CareerPlayer q)
            => (q.Passing + q.Shooting + q.Heading + q.Tackling + q.Control + q.Speed + q.Finishing) / 7.0;

        // Pick the 16 STRONGEST clubs so the snapshot is representative (team 0 is
        // the weakest side in the game and makes everything look broken).
        var allRefs = Teams(0, _allTeams.Count);
        allRefs.Sort((a, b) => b.Strength.CompareTo(a.Strength));
        if (allRefs.Count < 16) { GD.Print("Not enough teams loaded for a report."); GetTree().Quit(1); return; }
        var league = allRefs.GetRange(0, 16);
        var cup = new System.Collections.Generic.List<OpenSwos.Competition.TeamRef>(league);

        var car = OpenSwos.Competition.CompetitionEngine.CreateCareer("REPORT CAREER", league, cup, 0, 0, 1, 12345);
        var world = OpenSwos.Competition.Career.CareerWorldBuilder.BuildWorld(car, _allTeams);
        ushort clubId = car.Career!.ClubGlobalId;

        void PrintSquad(string when)
        {
            var club = world.Clubs[clubId];
            GD.Print($"\n--- {car.Career!.ClubName} squad ({when}) — budget {club.Budget}, coaches {club.Coaches.Count} ---");
            GD.Print("   # NAME             POS  AGE  OVR  EFF  POT(scout) STA FORM FATG        VALUE");
            foreach (var p in club.Squad)
            {
                string pot = p.Scouted ? $"{p.EstLow:0.0}-{p.EstHigh:0.0}" : "  ?  ";
                GD.Print($"  {p.ShirtNumber,2} {p.Name,-16} {p.Position,-4} {p.Age,3}  {Ov(p),4:0.0} " +
                    $"{p.EffectiveOverall(),4}  {pot,-9} {p.Stamina,2}  {p.Form,3}  {p.FatigueCarry,4} " +
                    $"{OpenSwos.Competition.Career.Finance.PlayerValue(p),12}");
            }
        }

        PrintSquad("season 1, before any development");
        // Must re-use the SAME strong league each season — AdvanceCareerSeason
        // locates the player's club inside the list it is given. 20 seasons =
        // the balance soak (step 18): watch for runaway inflation / collapse.
        for (int s = 0; s < 20; s++)
            OpenSwos.Competition.CompetitionEngine.AdvanceCareerSeason(
                car,
                new System.Collections.Generic.List<OpenSwos.Competition.TeamRef>(league),
                new System.Collections.Generic.List<OpenSwos.Competition.TeamRef>(cup), 0);
        PrintSquad($"season {car.Career!.Season}, after 20 seasons");

        var youths = new System.Collections.Generic.List<OpenSwos.Competition.Career.CareerPlayer>();
        foreach (var club in world.Clubs.Values)
            foreach (var p in club.Squad)
                if (p.Age <= 20 && p.Scouted) youths.Add(p);
        youths.Sort((a, b) => b.EstHigh.CompareTo(a.EstHigh));
        GD.Print("\n--- brightest scouted youths across the whole world ---");
        for (int i = 0; i < youths.Count && i < 12; i++)
        {
            var p = youths[i];
            GD.Print($"  {p.Name,-16} age {p.Age}  ovr {Ov(p),4:0.0}  scouted potential {p.EstLow:0.0}-{p.EstHigh:0.0}");
        }

        GD.Print($"\nWorld: {world.Clubs.Count} clubs, " +
            $"{OpenSwos.Competition.Career.CareerWorldBuilder.CountPlayers(world)} players, now season {car.Career!.Season}.");
        GD.Print("=== end career report ===");
        GetTree().Quit(0);
    }

    // === SWOS-port smoke test ================================================
    // Headless / CI hook: drives the SwosPort path through N sim ticks starting
    // from a fresh kick-off, without any user input. Logs PASS / FAIL plus the
    // first exception's stack trace, then quits. Used by `--swos-smoke` to
    // verify the port path doesn't crash before behavior tuning. Pure runtime
    // sanity check — NOT a correctness test (sprites won't move much because
    // most updatePlayers/AI stubs are no-ops).
    private void RunSwosPortSmokeTest(int ticks)
    {
        GD.Print($"=== SWOS-port smoke test: {ticks} ticks ===");
        _useSwosPort = true;
        _swosVmDirty = true;
        _forceBothTeamsCpu = true;  // no input in headless — human team would faithfully stall
        // Force entry into Play immediately — bypass the menu, PreKickoff hold,
        // and HalfTime/FullTime logic that would otherwise gate ticks.
        _match = MatchState.NewMatch();
        EnterPreKickoff();
        _match.EnterPhase(MatchPhase.Play);
        _appState = AppState.Match;
        // Reset fallback counters so cumulative state from prior calls in the
        // same process doesn't bias the smoke summary.
        OpenSwos.Sim.Port.UpdatePlayers.ResetFallbackCounters();
        OpenSwos.Sim.Port.PlayerControlled.ResetFireFallbackCounters();
        OpenSwos.Sim.Port.AiBrain.ResetFireSiteCounters();
        OpenSwos.Sim.Port.Referee.ResetDebugCounters();
        OpenSwos.Sim.Port.PlayerUpdate.ResetDiveCounters();
        OpenSwos.Sim.Port.InputControls.ResetCtrlSwapCounters();

        // ---- Telemetry tracking state -----------------------------------------
        // To detect "STUCK" (nobody moves for 200+ consecutive ticks) we snapshot
        // ball + all 22 player slot positions each tick and compare against the
        // previous snapshot.
        //
        // 2026-06-01: compare WHOLE-PIXEL (>>16) coords, not raw Q16.16. Sub-pixel
        // fractional drift was masking real stalls — e.g. a settled ball whose
        // Z fluctuated by ±0.001 px from the `dz |= 1` parity bit kept resetting
        // stuckTicks even though the ball was effectively pinned. Whole-pixel
        // matches what the renderer (and a human watcher) actually perceives.
        int stuckTicks = 0;
        const int kStuckThreshold = 200;
        int totalStuckEvents = 0;
        bool stuckWarned = false;
        int prevBallX = OpenSwos.SwosVm.BallSprite.X >> 16;
        int prevBallY = OpenSwos.SwosVm.BallSprite.Y >> 16;
        int prevBallZ = OpenSwos.SwosVm.BallSprite.Z >> 16;
        var prevPlayerX = new int[OpenSwos.SwosVm.PlayerSprite.TotalSlots];
        var prevPlayerY = new int[OpenSwos.SwosVm.PlayerSprite.TotalSlots];
        // Out-of-bounds: pitch is roughly 0..672 X and 0..848 Y in SWOS world
        // coords; we use slightly looser limits and only flag once per sprite to
        // keep the log readable.
        bool[] oobFlagged = new bool[OpenSwos.SwosVm.PlayerSprite.TotalSlots + 1]; // +1 = ball

        // ---- Clock-stall detection -------------------------------------------
        // The port's GameTime.UpdateGameTime() stalls the clock at period
        // boundaries (45/90/105/120) via SetupLastMinuteSwitchNextFrame: it
        // pins gameSeconds=-1 then ticks endGameCounter down until either (a)
        // ProlongLastMinute returns false (handler fires, half/full-time
        // begins) or (b) prolong-conditions persist forever (ball pinned in
        // penalty area / lastTeamPlayed never changes hands). Without an
        // active AI driving real possession churn, (b) traps the smoke at
        // 45:00 indefinitely with match.phase=Play. Detect by tracking how
        // long we've been at one of the boundary minutes.
        //
        // 2026-06-01: GameTime.UpdateGameTime now has a port-only safety cap
        // (kProlongSafetyCapTicks = 600) that force-fires the period handler
        // after 600 consecutive prolong ticks. This stall detector still
        // triggers at 500 ticks as a heads-up that the safety net engaged
        // (i.e. the natural prolong-exit condition didn't fire). A printed
        // CLOCK-STALL warning followed by phase → HalfTime/FullTime means
        // the safety net worked; only a sustained stall to end-of-smoke would
        // indicate the safety cap itself is broken.
        int clockStallTicks = 0;
        const int kClockStallThreshold = 500;  // ~3 game-minutes at the smoke pace
        int totalClockStallEvents = 0;
        bool clockStallWarned = false;
        uint prevBoundaryMin = 0;

        int completed = 0;
        bool kickoffDumped = false;
        try
        {
            for (int t = 0; t < ticks; t++)
            {
                TickSwosPort();
                completed = t + 1;

                // OpenSWOS fatigue verification: the first tick runs
                // InitSwosVmFromMatchSetup (seeds energy + sets EffectEnabled from
                // the OPTIONS gate, which is off in headless). Force the effect ON
                // so the smoke exercises the fatigue path deterministically.
                if (t == 0) OpenSwos.Sim.Port.PlayerEnergy.EffectEnabled = true;

                // Deterministic energy dump (INTEGER content only). t==300 may not
                // be reached when ticks<=300, so the final tick is always dumped too.
                if (t == 0 || t == 150 || t == 300 || t == ticks - 1)
                    GD.Print($"[energy] t={t} slots 1/5/12/16 = "
                        + OpenSwos.Sim.Port.PlayerEnergy.ReadEnergy(1) + "/"
                        + OpenSwos.Sim.Port.PlayerEnergy.ReadEnergy(5) + "/"
                        + OpenSwos.Sim.Port.PlayerEnergy.ReadEnergy(12) + "/"
                        + OpenSwos.Sim.Port.PlayerEnergy.ReadEnergy(16));

                // ---- TEMP DIAG (2026-06-02) — kickoff position dump at t=0 ---
                // Confirms Kickoff.PrepareForInitialKick placed all 22 slots
                // in the SWOS-native formation (top/bottomStartingPositions
                // tables) rather than the old top-left 4-3-3 placeholder grid.
                // Dumps after the FIRST tick (which is when
                // InitSwosVmFromMatchSetup runs Kickoff.PrepareForInitialKick
                // — TickSwosPort wires it on demand on the first call).
                if (!kickoffDumped)
                {
                    kickoffDumped = true;
                    GD.Print("=== KICKOFF POSITION DUMP (post-tick 0) ===");
                    int bxPx0 = OpenSwos.SwosVm.BallSprite.X >> 16;
                    int byPx0 = OpenSwos.SwosVm.BallSprite.Y >> 16;
                    GD.Print($"  ball ({bxPx0,4},{byPx0,4})");
                    for (int s = 0; s < OpenSwos.SwosVm.PlayerSprite.TotalSlots; s++)
                    {
                        int xPx = OpenSwos.SwosVm.PlayerSprite.X(s) >> 16;
                        int yPx = OpenSwos.SwosVm.PlayerSprite.Y(s) >> 16;
                        int dir = OpenSwos.SwosVm.PlayerSprite.Direction(s);
                        string role = s == 0 ? "T-GK" : s == 11 ? "B-GK"
                            : (s < 11 ? $"T-{s}" : $"B-{s-11}");
                        GD.Print($"  slot {s,2} {role,-5} ({xPx,4},{yPx,4}) dir={dir}");
                    }
                    GD.Print("=== END KICKOFF DUMP ===");
                }

                // ---- Every 50 ticks: dump live telemetry --------------------
                if (t % 50 == 0 || t == ticks - 1)
                {
                    int bxPx = OpenSwos.SwosVm.BallSprite.X >> 16;
                    int byPx = OpenSwos.SwosVm.BallSprite.Y >> 16;
                    int bzPx = OpenSwos.SwosVm.BallSprite.Z >> 16;
                    int bdxPx = OpenSwos.SwosVm.BallSprite.DeltaX >> 16;
                    int bdyPx = OpenSwos.SwosVm.BallSprite.DeltaY >> 16;
                    int bdzPx = OpenSwos.SwosVm.BallSprite.DeltaZ >> 16;
                    int bdir = OpenSwos.SwosVm.BallSprite.Direction;
                    int bimg = OpenSwos.SwosVm.BallSprite.ImageIndex;
                    int bspd = OpenSwos.SwosVm.BallSprite.Speed;
                    int gState = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameState);
                    int gStatePl = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameStatePl);
                    int curTick = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.currentGameTick);
                    // Clock + score readout — confirms SyncMatchFromSwosPort()
                    // is reading the port's authoritative match state each tick.
                    uint gtMin = OpenSwos.SwosVm.Memory.ReadDword(OpenSwos.SwosVm.Memory.Addr.gt_gameTimeInMinutes);
                    int gtSec = OpenSwos.SwosVm.Memory.ReadSignedDword(OpenSwos.SwosVm.Memory.Addr.gt_gameSeconds);
                    int t1g = OpenSwos.SwosVm.Memory.ReadWord(OpenSwos.SwosVm.Memory.Addr.team1TotalGoals);
                    int t2g = OpenSwos.SwosVm.Memory.ReadWord(OpenSwos.SwosVm.Memory.Addr.team2TotalGoals);
                    int playGameFlag = OpenSwos.SwosVm.Memory.ReadWord(OpenSwos.SwosVm.Memory.Addr.playGame);

                    GD.Print($"[t={t,4}] gs={gState}/gsPl={gStatePl} curTick={curTick} " +
                             $"ball({bxPx},{byPx},{bzPx}) d({bdxPx},{bdyPx},{bdzPx}) dir={bdir} img={bimg} spd={bspd} " +
                             $"clock={gtMin:00}:{System.Math.Max(gtSec,0):00} score={t1g}-{t2g} playGame={playGameFlag} " +
                             $"match.phase={_match.Phase} half={_match.Half}");

                    LogSlot(t, 0,  "p1.keeper");
                    LogSlot(t, 1,  "p1.outf1 ");
                    LogSlot(t, 11, "p2.keeper");
                    LogSlot(t, 12, "p2.outf1 ");
                }

                // ---- Frozen-state detection ----------------------------------
                // Whole-pixel compare (>>16): the renderer / human watcher only
                // perceives motion at the pixel level. A settled ball whose
                // raw Q16.16 wiggles by ±1 from the `dz |= 1` parity bit is
                // visually frozen and should count as STUCK.
                int curBallX = OpenSwos.SwosVm.BallSprite.X >> 16;
                int curBallY = OpenSwos.SwosVm.BallSprite.Y >> 16;
                int curBallZ = OpenSwos.SwosVm.BallSprite.Z >> 16;
                bool anyMoved = curBallX != prevBallX || curBallY != prevBallY || curBallZ != prevBallZ;
                for (int s = 0; s < OpenSwos.SwosVm.PlayerSprite.TotalSlots; s++)
                {
                    int px = OpenSwos.SwosVm.PlayerSprite.X(s) >> 16;
                    int py = OpenSwos.SwosVm.PlayerSprite.Y(s) >> 16;
                    if (t > 0 && (px != prevPlayerX[s] || py != prevPlayerY[s])) anyMoved = true;
                    prevPlayerX[s] = px;
                    prevPlayerY[s] = py;
                }
                prevBallX = curBallX; prevBallY = curBallY; prevBallZ = curBallZ;

                // Skip stuck-detection during legitimate frozen windows:
                // SWOS holds the ball + all players still during the post-goal
                // celebration (gameState=0 with goalCounter>0). The celebration
                // duration is `(rand()>>1)+100 + rand()` — up to ~482 ticks —
                // which exceeds the 200-tick STUCK threshold. See
                // BallOutOfPlay.cs:194 (D1_goal = (rand()>>1)+100) and
                // BallOutOfPlay.cs:246 (D1_goal += rand()) + UpdateGoals.cs:202
                // (UpdatePostGoalRestart decrements goalCounter per tick).
                int gsForStuck = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameState);
                int gcForStuck = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.goalCounter);
                bool inPostGoalCel = (gsForStuck == 0) && (gcForStuck > 0);
                // The half-time / full-time / game-break RESULT PANEL is another
                // legitimate all-frozen window: SWOS parks the ball off-pitch
                // (1672,449) and holds every player while the scoreline + scorers
                // dwell (gs 23/25 etc.). res_showResult is the exact signal — a
                // ≥45:00 run would otherwise flag one benign stuck+clock-stall
                // pair during the HT dwell.
                bool inResultPanel = OpenSwos.Sim.Port.Result.ShouldDrawResult();
                // The whole PERIOD-END CEREMONY (half-time / full-time) is a
                // legitimate non-gameplay window: gameState 21..30 are the
                // transmission states (starting-game intro, first-half-ended,
                // result panel, stats panel, walk-to-shower, game-ended). SWOS
                // pins the clock at 45/90 and parks/freezes sprites for the full
                // ~1500-tick ceremony — the period handler HAS already fired, so
                // this is not a stall. res_showResult only covers the 275-tick
                // result sub-panel; exclude the entire ceremony range.
                bool inPeriodEndCeremony = gsForStuck >= 21 && gsForStuck <= 30;

                if (anyMoved || inPostGoalCel || inResultPanel || inPeriodEndCeremony)
                    { stuckTicks = 0; stuckWarned = false; }
                else
                {
                    stuckTicks++;
                    if (stuckTicks == kStuckThreshold && !stuckWarned)
                    {
                        GD.PrintErr($"STUCK at tick {t}: nobody moves (ball + 22 players frozen for {kStuckThreshold} ticks)");
                        stuckWarned = true;
                        totalStuckEvents++;
                    }
                }

                // ---- Clock-stall detection -----------------------------------
                // Period-end handler should fire within ~kEndGameCounterTicks
                // (55 PC / 50 Amiga) once the clock pins at 45/90/105/120.
                // If we sit at the same boundary minute for kClockStallThreshold
                // ticks the prolong loop is trapped — usually because the ball
                // never leaves the penalty area or lastTeamPlayed sticks on the
                // current attacker. Print once per stall event; tally for summary.
                // See Sim/Port/GameTime.cs:300-318 ProlongLastMinute.
                uint clockMin = OpenSwos.SwosVm.Memory.ReadDword(OpenSwos.SwosVm.Memory.Addr.gt_gameTimeInMinutes);
                bool atPeriodBoundary = clockMin == 45 || clockMin == 90 || clockMin == 105 || clockMin == 120;
                // The clock legitimately pins at 45/90/… for the whole half/full-
                // time ceremony (result + stats + walk-out) — the handler has
                // fired; that is not a stall. Exclude the ceremony range, not
                // just the 275-tick result sub-panel.
                if (atPeriodBoundary && clockMin == prevBoundaryMin && !inResultPanel && !inPeriodEndCeremony)
                {
                    clockStallTicks++;
                    if (clockStallTicks == kClockStallThreshold && !clockStallWarned)
                    {
                        GD.PrintErr($"CLOCK-STALL at tick {t}: gtMinutes pinned at {clockMin} for {kClockStallThreshold} ticks — period handler never fired");
                        clockStallWarned = true;
                        totalClockStallEvents++;
                    }
                }
                else
                {
                    clockStallTicks = 0;
                    clockStallWarned = false;
                    prevBoundaryMin = atPeriodBoundary ? clockMin : 0;
                }

                // ---- OOB detection -------------------------------------------
                {
                    int bxPx = OpenSwos.SwosVm.BallSprite.X >> 16;
                    int byPx = OpenSwos.SwosVm.BallSprite.Y >> 16;
                    if (!oobFlagged[0] && (bxPx < -100 || bxPx > 800 || byPx < -100 || byPx > 900))
                    {
                        GD.PrintErr($"OOB at tick {t}: ball at ({bxPx},{byPx})");
                        oobFlagged[0] = true;
                    }
                    for (int s = 0; s < OpenSwos.SwosVm.PlayerSprite.TotalSlots; s++)
                    {
                        int xPx = OpenSwos.SwosVm.PlayerSprite.X(s) >> 16;
                        int yPx = OpenSwos.SwosVm.PlayerSprite.Y(s) >> 16;
                        if (!oobFlagged[s + 1] && (xPx < -100 || xPx > 800 || yPx < -100 || yPx > 900))
                        {
                            GD.PrintErr($"OOB at tick {t}: slot {s} at ({xPx},{yPx})");
                            oobFlagged[s + 1] = true;
                        }
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"RUNTIME TEST FAILED after {completed}/{ticks} ticks: {ex.GetType().Name}: {ex.Message}");
            GD.PrintErr(ex.StackTrace ?? "(no stack)");
            GetTree().Quit(1);
            return;
        }

        // Fallback heuristic telemetry. When AiBrain's port covers the real
        // SWOS branches (cseg_84A85 → cseg_850F9, cseg_84F4B → cseg_84FCA,
        // l_activate_normal_fire, l_our_player_closest chase), these counters
        // should trend toward zero. Source: PlayerControlled.cs s_fireFallback*
        // and UpdatePlayers.cs s_fallbackChases*.
        int chaseT = OpenSwos.Sim.Port.UpdatePlayers.FallbackChasesTop;
        int chaseB = OpenSwos.Sim.Port.UpdatePlayers.FallbackChasesBot;
        int kickT  = OpenSwos.Sim.Port.UpdatePlayers.KickFallbackTop;
        int kickB  = OpenSwos.Sim.Port.UpdatePlayers.KickFallbackBot;
        int reaimT = OpenSwos.Sim.Port.UpdatePlayers.ReaimAppliedTop;
        int reaimB = OpenSwos.Sim.Port.UpdatePlayers.ReaimAppliedBot;
        int fireT  = OpenSwos.Sim.Port.PlayerControlled.FireFallbackTop;
        int fireB  = OpenSwos.Sim.Port.PlayerControlled.FireFallbackBot;
        int realCseg850F9 = OpenSwos.Sim.Port.AiBrain.FireSiteCseg850F9;
        int realCseg84FCA = OpenSwos.Sim.Port.AiBrain.FireSiteCseg84FCA;
        int realActivate  = OpenSwos.Sim.Port.AiBrain.FireSiteActivate;
        GD.Print($"[fallback] chase top={chaseT} bot={chaseB} | kick top={kickT} bot={kickB} | reaim top={reaimT} bot={reaimB} | fire top={fireT} bot={fireB}");
        int kT_def = OpenSwos.Sim.Port.UpdatePlayers.KickFallbackTopDef;
        int kT_mid = OpenSwos.Sim.Port.UpdatePlayers.KickFallbackTopMid;
        int kT_att = OpenSwos.Sim.Port.UpdatePlayers.KickFallbackTopAtt;
        int kT_box = OpenSwos.Sim.Port.UpdatePlayers.KickFallbackTopBox;
        int kB_def = OpenSwos.Sim.Port.UpdatePlayers.KickFallbackBotDef;
        int kB_mid = OpenSwos.Sim.Port.UpdatePlayers.KickFallbackBotMid;
        int kB_att = OpenSwos.Sim.Port.UpdatePlayers.KickFallbackBotAtt;
        int kB_box = OpenSwos.Sim.Port.UpdatePlayers.KickFallbackBotBox;
        GD.Print($"[kick-zones] top def={kT_def} mid={kT_mid} att={kT_att} box={kT_box} | bot def={kB_def} mid={kB_mid} att={kB_att} box={kB_box}");
        GD.Print($"[ai-fire] cseg_850F9={realCseg850F9} cseg_84FCA={realCseg84FCA} l_activate_normal_fire={realActivate}");
        int enterCseg80FFD = OpenSwos.Sim.Port.PlayerControlled.EnterCseg80FFD;
        int reachedCwb = OpenSwos.Sim.Port.PlayerControlled.ReachedCwbCall;
        int duelOwn = OpenSwos.Sim.Port.PlayerControlled.SkillDuelOwnWin;
        int duelOpp = OpenSwos.Sim.Port.PlayerControlled.SkillDuelOppWin;
        int pinTick = OpenSwos.Sim.Port.PlayerControlled.PinControlledPerTick;
        GD.Print($"[ball-arb] cseg_80FFD={enterCseg80FFD} CalculateIfPlayerWinsBall={reachedCwb} skillDuel(own/opp)={duelOwn}/{duelOpp} pinPerTick={pinTick}");
        int trigNear = OpenSwos.Sim.Port.AiBrain.TrigPlayerNear;
        int trigA85  = OpenSwos.Sim.Port.AiBrain.TrigCseg84A85;
        int trigGate = OpenSwos.Sim.Port.AiBrain.TrigCseg850F9Gate;
        int trigB5B  = OpenSwos.Sim.Port.AiBrain.TrigCseg84B5B;
        int trigF4B  = OpenSwos.Sim.Port.AiBrain.TrigCseg84F4B;
        GD.Print($"[ai-trig] player_near={trigNear} cseg_84A85={trigA85} cseg_850F9_gate={trigGate} cseg_84B5B={trigB5B} cseg_84F4B={trigF4B}");

        // Auto-switch (controlledPlayer reassignment) telemetry.
        // UpdateControlledPlayer (InputControls.cs) bumps these whenever it
        // promotes a new sprite. Human-team open-play swaps were introduced
        // 2026-06-06 to fix the "human stranded after losing ball" bug.
        // Source: external/swos-port/src/game/updatePlayers/updatePlayers.cpp:
        //   8221-8229 (passed_to_player_becomes_main) + 18958-18966
        //   (AiBrain l_noone_near swap).
        int swapHT = OpenSwos.Sim.Port.InputControls.CtrlSwapHumanTop;
        int swapHB = OpenSwos.Sim.Port.InputControls.CtrlSwapHumanBot;
        int swapAT = OpenSwos.Sim.Port.InputControls.CtrlSwapAiTop;
        int swapAB = OpenSwos.Sim.Port.InputControls.CtrlSwapAiBot;
        GD.Print($"[ctrl-swap] human top={swapHT} bot={swapHB} | ai top={swapAT} bot={swapAB}");

        // Surface latent stall events. The smoke historically only fired
        // `STUCK` from inside the loop (once per event), so a clean run that
        // happened to skirt the threshold looked identical to one that
        // tripped it. Print a roll-up so callers see the real story.
        GD.Print($"[stall-summary] stuck-events={totalStuckEvents} clock-stall-events={totalClockStallEvents}");

        // Referee FSM telemetry — counts state transitions so callers can
        // confirm the booking pipeline actually advances when fouls are
        // registered. activations counts ActivateReferee calls;
        // entered-{Incoming,Waiting,AboutToGive,Booking,Leaving,OffScreen}
        // count refState transitions per the FSM in referee.cpp:192-245 +
        // updatePlayers.cpp:8997. cards counts whichCard writes per type;
        // sent-away counts SendPlayerAway invocations (red + 2nd-yellow
        // outcomes). A clean fault-free smoke with at least one foul should
        // see activations > 0 AND entered-Booking > 0; otherwise the FSM
        // wire is broken.
        GD.Print($"[referee] activations={OpenSwos.Sim.Port.Referee.DbgActivations} " +
                 $"entered-Incoming={OpenSwos.Sim.Port.Referee.DbgEnteredIncoming} " +
                 $"Waiting={OpenSwos.Sim.Port.Referee.DbgEnteredWaiting} " +
                 $"AboutToGive={OpenSwos.Sim.Port.Referee.DbgEnteredAboutToGive} " +
                 $"Booking={OpenSwos.Sim.Port.Referee.DbgEnteredBooking} " +
                 $"Leaving={OpenSwos.Sim.Port.Referee.DbgEnteredLeaving} " +
                 $"OffScreen={OpenSwos.Sim.Port.Referee.DbgEnteredOffScreen} " +
                 $"| cards yellow={OpenSwos.Sim.Port.Referee.DbgYellowCards} " +
                 $"red={OpenSwos.Sim.Port.Referee.DbgRedCards} " +
                 $"2ndYellow={OpenSwos.Sim.Port.Referee.DbgSecondYellowCards} " +
                 $"| sent-away={OpenSwos.Sim.Port.Referee.DbgPlayersSentAway}");

        int diveH = OpenSwos.Sim.Port.PlayerUpdate.DiveCallsHigh;
        int diveL = OpenSwos.Sim.Port.PlayerUpdate.DiveCallsLow;
        int diveShould = OpenSwos.Sim.Port.PlayerUpdate.ShouldDiveTrueCount;
        GD.Print($"[keeper-dive] diveHigh={diveH} diveLow={diveL} shouldDiveTrue={diveShould}");

        GD.Print($"RUNTIME TEST PASSED ({completed} ticks completed without exception)");
        // The smoke drives every tick synchronously and quits before any idle
        // _Process frame — block on the background audio preload so its
        // "[audio] N samples loaded" summary always lands (verification harness).
        OpenSwos.Audio.MatchAudio.EnsurePreloadComplete();
        GetTree().Quit(0);
    }

    // === Keeper-holds-ball auto-release test =================================
    // Drives the SWOS-port path through a kick-off, then PROGRAMMATICALLY
    // forces the keeper-holds-ball state (sets gameState = ST_KEEPER_HOLDS_BALL,
    // pins ball to the top-team keeper, primes lastTeamPlayedBeforeBreak), and
    // runs ticks until the auto-release fires. Validates PlayerUpdate
    // .TickGoalkeeperHoldAutoRelease (port of updatePlayers.cpp:16759 +
    // gameLoop.cpp:530-537 + player.cpp:750).
    //
    // Pass: keeper releases (gameState transitions away from 3) within
    // 250 ticks (allowing some slack over the 150 SWOS threshold). Fail:
    // gameState stuck at 3 after 1000 ticks, or process throws.
    private void RunKeeperReleaseTest()
    {
        GD.Print("=== Keeper-holds-ball auto-release test ===");
        _useSwosPort = true;
        _swosVmDirty = true;
        _forceBothTeamsCpu = true;  // release path: AI keeper kick (human waits forever, gameLoop.cpp:545-546)
        _match = MatchState.NewMatch();
        EnterPreKickoff();
        _match.EnterPhase(MatchPhase.Play);
        _appState = AppState.Match;

        // Session 24: the faithful match-start ceremony (gameState 21 → 0 →
        // breakCameraMode ladder → 102 → AI fire) takes ~700 ticks. Forcing
        // the hold state mid-ceremony (old: 60 fixed ticks) left the ball
        // off-pitch at (1672,449) and produced a throw-in on release. Wait
        // for genuine open play first.
        int warmup = 0;
        while (warmup < 2000)
        {
            TickSwosPort();
            warmup++;
            if (OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameState) == 100 &&
                OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameStatePl) == 100)
                break;
        }
        GD.Print($"[setup] reached open play after {warmup} warmup ticks");

        // Force keeper-holds-ball state: mirror what
        // PlayerUpdate.GoalkeeperClaimedTheBall (player.cpp:2238) does.
        // We target the top-team keeper (slot 0).
        int keeperAddr = OpenSwos.SwosVm.PlayerSprite.Base(OpenSwos.SwosVm.PlayerSprite.SlotGoalie1);
        int topTeamBase = OpenSwos.SwosVm.TeamData.TopBase;

        // Pin the ball at the keeper's feet/hands so the hold is physically
        // coherent (release kick starts from the goal mouth, not off-pitch).
        short kx = OpenSwos.SwosVm.Memory.ReadSignedWord(
            keeperAddr + OpenSwos.SwosVm.PlayerSprite.OffX + 2);
        short ky = OpenSwos.SwosVm.Memory.ReadSignedWord(
            keeperAddr + OpenSwos.SwosVm.PlayerSprite.OffY + 2);
        OpenSwos.SwosVm.BallSprite.XPixels = kx;
        OpenSwos.SwosVm.BallSprite.YPixels = ky;
        OpenSwos.SwosVm.BallSprite.DestX = kx;
        OpenSwos.SwosVm.BallSprite.DestY = ky;

        // ball.speed = 0; gameState = ST_KEEPER_HOLDS_BALL (3); gameStatePl = ST_STOPPED (101).
        OpenSwos.SwosVm.BallSprite.Speed = 0;
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.gameState, 3);
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.gameStatePl, 101);

        // lastTeamPlayedBeforeBreak = top team (the team holding the ball).
        OpenSwos.SwosVm.Memory.WriteDword(OpenSwos.SwosVm.Memory.Addr.lastTeamPlayedBeforeBreak, topTeamBase);

        // team.controlledPlayer = keeper sprite address.
        OpenSwos.SwosVm.Memory.WriteDword(topTeamBase + OpenSwos.SwosVm.TeamData.OffControlledPlayer, keeperAddr);
        OpenSwos.SwosVm.Memory.WriteWord(topTeamBase + OpenSwos.SwosVm.TeamData.OffBallOutOfPlayOrKeeper, 1);

        // Reset stoppage timers to 0 (mirrors player.cpp:2307-2308).
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.stoppageTimerActive, 0);
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.stoppageTimerTotal, 0);

        int initialGameState = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameState);
        GD.Print($"[setup] gameState={initialGameState} (expected 3); stoppageTimerActive=0; lastTeamPlayedBeforeBreak=topTeamBase");

        // Drive ticks until gameState transitions out of ST_KEEPER_HOLDS_BALL.
        const int maxTicks = 1000;
        int releaseAtTick = -1;
        int peakTimer = 0;
        for (int t = 0; t < maxTicks; t++)
        {
            TickSwosPort();

            short timer = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.stoppageTimerActive);
            if (timer > peakTimer) peakTimer = timer;

            short gs = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameState);
            if (gs != 3)
            {
                releaseAtTick = t + 1;
                short gsPl = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameStatePl);
                GD.Print($"[t={t,4}] RELEASE: gameState={gs} gameStatePl={gsPl} stoppageTimerActive={timer} peakTimer={peakTimer}");
                break;
            }

            if (t % 25 == 0 || t < 5)
            {
                GD.Print($"[t={t,4}] gs={gs} stoppageTimerActive={timer}");
            }
        }

        if (releaseAtTick == -1)
        {
            GD.PrintErr($"FAIL: keeper still holds ball after {maxTicks} ticks (peakTimer={peakTimer}); expected release around tick 150");
            GetTree().Quit(1);
            return;
        }

        // Sanity bounds (Session 24, faithful flow): an AI keeper releases via
        // the l_keepers_ball AI fire (updatePlayers.cpp:16656, gated on
        // stoppageTimerActive > (gameTick&63)+100 → ~[100,170]); the PORT-SAFETY
        // fallback guarantees ≤ ~310. A [PORT-SAFETY] line in the log means the
        // AI path failed and the fallback carried it — investigate if seen.
        if (releaseAtTick < 100 || releaseAtTick > 310)
        {
            GD.PrintErr($"WARN: release tick {releaseAtTick} outside expected [100, 310] window");
        }

        GD.Print($"KEEPER RELEASE TEST PASSED (released at tick {releaseAtTick}, peakTimer={peakTimer})");
        GetTree().Quit(0);
    }

    // Corner + goal-kick restart test (Session 24 — faithful ladder). Forces
    // gameState into ST_CORNER_* / ST_GOAL_OUT_* break states and verifies the
    // ported breakCameraMode ladder (gameLoop.cpp:1110-1846) + walk-up AI +
    // AI fire resolve each back to ST_GAME_IN_PROGRESS with the ball kicked.
    // The old assertion ("within a single tick") tested the deleted port-only
    // auto-resolver; a faithful set piece takes a full ceremony (walk-up,
    // whistle, AI fire) — budget 3000 ticks per scenario.
    private void RunCornerGoalKickTest()
    {
        GD.Print("=== Corner + goal-kick faithful restart test ===");
        _useSwosPort = true;
        _swosVmDirty = true;
        _forceBothTeamsCpu = true;  // set pieces resolve via AI fire under the faithful ladder
        _match = MatchState.NewMatch();
        EnterPreKickoff();
        _match.EnterPhase(MatchPhase.Play);
        _appState = AppState.Match;

        // Warm up through the real match-start ceremony to open play.
        int warmup = 0;
        while (warmup < 2500)
        {
            TickSwosPort();
            warmup++;
            if (OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameState) == 100 &&
                OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameStatePl) == 100)
                break;
        }
        GD.Print($"[setup] open play after {warmup} warmup ticks");

        bool allPassed = true;

        // Forces one break state (mirrors the ball.cpp out-of-play writers) and
        // ticks the faithful machine until play resumes. Coordinates/flags per
        // the cited ball.cpp corner/goal-out setup blocks.
        bool RunScenario(string label, short breakGameState, short foulX, short foulY,
                         short camDir, byte turnFlags, int takingTeamBase)
        {
            // Return to clean live play between scenarios.
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.gameState, 100);
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.gameStatePl, 100);
            for (int t = 0; t < 5; t++) TickSwosPort();

            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.gameState, breakGameState);
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.gameStatePl, 101);
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.breakCameraMode, unchecked((short)-1));
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.foulXCoordinate, foulX);
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.foulYCoordinate, foulY);
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.cameraDirection, camDir);
            OpenSwos.SwosVm.Memory.WriteByte(OpenSwos.SwosVm.Memory.Addr.playerTurnFlags, turnFlags);
            OpenSwos.SwosVm.Memory.WriteDword(OpenSwos.SwosVm.Memory.Addr.lastTeamPlayedBeforeBreak, takingTeamBase);
            OpenSwos.SwosVm.BallSprite.Speed = 0;
            OpenSwos.Sim.Port.TeamPort.StopAllPlayers();

            const int budget = 3000;
            int resolvedAt = -1;
            bool ballKicked = false;
            for (int t = 0; t < budget; t++)
            {
                TickSwosPort();
                if (OpenSwos.SwosVm.BallSprite.Speed > 0) ballKicked = true;
                short gs = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameState);
                short gsPl = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameStatePl);
                if (gs == 100 && gsPl == 100)
                {
                    resolvedAt = t + 1;
                    break;
                }
            }
            bool pass = resolvedAt > 0 && ballKicked;
            GD.Print($"  [{label}] resolvedAt={resolvedAt} ballKicked={ballKicked} -> {(pass ? "PASS" : "FAIL")}");
            return pass;
        }

        // ball.cpp:3659-3664 — left_upper_corner (ST_CORNER_LEFT=4).
        allPassed &= RunScenario("Corner LEFT @ (86,134)", 4, 86, 134, 2, 28,
            OpenSwos.SwosVm.TeamData.TopBase);
        // ball.cpp:3699-3706 — upper goal-out (ST_GOAL_OUT_LEFT=1).
        allPassed &= RunScenario("GoalKick UPPER @ (396,154)", 1, 396, 154, 4, 124,
            OpenSwos.SwosVm.TeamData.TopBase);
        // ball.cpp:3641-3647 — lower_left_corner (ST_CORNER_RIGHT=5).
        allPassed &= RunScenario("Corner RIGHT @ (86,764)", 5, 86, 764, 2, 7,
            OpenSwos.SwosVm.TeamData.BottomBase);
        // ball.cpp:3743-3749 — lower goal-out (ST_GOAL_OUT_RIGHT=2).
        allPassed &= RunScenario("GoalKick LOWER @ (396,744)", 2, 396, 744, 0, 199,
            OpenSwos.SwosVm.TeamData.BottomBase);

        if (allPassed)
        {
            GD.Print("CORNER + GOAL-KICK TEST PASSED (all 4 scenarios resolved via faithful ladder)");
            GetTree().Quit(0);
        }
        else
        {
            GD.PrintErr("CORNER + GOAL-KICK TEST FAILED");
            GetTree().Quit(1);
        }
    }

    // === Goal celebration test (Session 24 — faithful flow) ==================
    // Drives the real match-start ceremony to open play, injects a goal (the
    // ported UpdateGoals.GoalScored + a test-harness mirror of the ball.cpp
    // goal tail: 3432-3439 + l_break_handled 3975-4008), then asserts the
    // FAITHFUL post-goal sequence:
    //   1. goalCounter drains, breakCameraMode ladder arms (gameState=0),
    //   2. all 22 sprites walk back and reach destReachedState==3,
    //   3. the result panel gets armed (Result.ShouldDrawResult true at some
    //      point — resultTimer=31000→165, gameLoop.cpp:1808),
    //   4. play resumes (gameState==gameStatePl==100) with the score KEPT.
    // The old per-goal PL_HAPPY/PL_SAD assertion is gone — the original only
    // sets those at match end (updatePlayers.cpp:8706-8804).
    private void RunGoalCelebrationTest()
    {
        GD.Print("=== Goal celebration test (faithful post-goal restart) ===");
        _useSwosPort = true;
        _swosVmDirty = true;
        _forceBothTeamsCpu = true;  // faithful restart needs an AI kicker (no input headless)
        _match = MatchState.NewMatch();
        EnterPreKickoff();
        _match.EnterPhase(MatchPhase.Play);
        _appState = AppState.Match;

        // Warm up through the real 21→0→ladder→102→kick ceremony.
        int warmup = 0;
        while (warmup < 2500)
        {
            TickSwosPort();
            warmup++;
            if (OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameState) == 100 &&
                OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameStatePl) == 100)
                break;
        }
        if (warmup >= 2500)
        {
            GD.PrintErr("FAIL: never reached open play (100/100) within 2500 warmup ticks");
            GetTree().Quit(1);
            return;
        }
        GD.Print($"[setup] open play after {warmup} warmup ticks");

        // Inject a goal for team 1 and mirror the ball.cpp goal tail the real
        // detection would perform (ball.cpp:3148-3299 + 3432-3439 + 3975-4008).
        const int scoringTeam = 1;
        int scorerSlot = OpenSwos.SwosVm.PlayerSprite.SlotTeam1First;
        OpenSwos.Sim.Port.UpdateGoals.GoalScored(scoringTeam, scorerSlot);
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.goalCameraMode, 1);      // ball.cpp:3150
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.stateGoal, unchecked((short)-1)); // ball.cpp:3290
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.goalCounter, 60);        // short for test (real: Rand+100..300)
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.gameState, 0);           // ball.cpp:3437
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.breakCameraMode, unchecked((short)-1)); // ball.cpp:3438
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.gameStatePl, 101);       // ball.cpp:3992
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.foulXCoordinate, 336);
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.foulYCoordinate, 449);
        OpenSwos.SwosVm.Memory.WriteDword(OpenSwos.SwosVm.Memory.Addr.lastTeamPlayedBeforeBreak,
            OpenSwos.SwosVm.TeamData.BottomBase);  // conceding team kicks off (ball.cpp:3227,4002)
        OpenSwos.Sim.Port.TeamPort.StopAllPlayers();

        bool sawAllArrived = false;
        bool sawResultPanel = false;
        int resumeAtTick = -1;
        const int maxTicks = 3000;
        for (int t = 0; t < maxTicks; t++)
        {
            TickSwosPort();

            // Walk-back completion probe: all 22 destReachedState == 3.
            int arrived = 0;
            for (int s = 0; s < OpenSwos.SwosVm.PlayerSprite.TotalSlots; s++)
            {
                int spr = OpenSwos.SwosVm.PlayerSprite.Base(s);
                if (OpenSwos.SwosVm.Memory.ReadSignedWord(spr + OpenSwos.SwosVm.PlayerSprite.OffDestReachedState) == 3)
                    arrived++;
            }
            if (arrived == OpenSwos.SwosVm.PlayerSprite.TotalSlots) sawAllArrived = true;

            if (OpenSwos.Sim.Port.Result.ShouldDrawResult()) sawResultPanel = true;

            short gs = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameState);
            short gsPl = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameStatePl);
            if (gs == 100 && gsPl == 100)
            {
                resumeAtTick = t + 1;
                break;
            }
        }

        int t1Goals = OpenSwos.SwosVm.Memory.ReadWord(OpenSwos.SwosVm.Memory.Addr.team1TotalGoals);
        GD.Print($"[result] resumeAtTick={resumeAtTick} sawAllArrived={sawAllArrived} sawResultPanel={sawResultPanel} team1Goals={t1Goals}");

        bool pass = resumeAtTick > 0 && sawAllArrived && sawResultPanel && t1Goals == 1;
        if (!pass)
        {
            GD.PrintErr($"FAIL: resume={resumeAtTick} allArrived={sawAllArrived} resultPanel={sawResultPanel} goals={t1Goals} (expected resume>0, both true, goals=1)");
            GetTree().Quit(1);
            return;
        }

        GD.Print($"GOAL CELEBRATION TEST PASSED (faithful restart in {resumeAtTick} ticks, walk-back + result panel observed, score kept)");
        GetTree().Quit(0);
    }

    // === Half-time ceremony test ============================================
    // Drives a kick-off, programmatically invokes the half-time period handler
    // (which calls EndFirstHalf), then ticks forward and verifies the four-stage
    // ceremony progresses 22 → 24 → 25 → 100 with ~300 ticks dwell per stage
    // (~12 s total ceremony @ PC 70 ticks/s).
    //
    // Validates GameTime.EndFirstHalf + GameLoop.TickHalftimeCeremony — the
    // ported half-time camera-to-shower-to-result-to-kickoff sequence.
    private void RunHalftimeCeremonyTest()
    {
        GD.Print("=== Half-time ceremony test ===");
        _useSwosPort = true;
        _swosVmDirty = true;
        _forceBothTeamsCpu = true;  // faithful 2nd-half kickoff needs an AI kicker
        _match = MatchState.NewMatch();
        EnterPreKickoff();
        _match.EnterPhase(MatchPhase.Play);
        _appState = AppState.Match;

        // Settle initial kickoff.
        for (int t = 0; t < 60; t++) TickSwosPort();

        // Snapshot pre-ceremony state.
        short preTpu = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.teamPlayingUp);
        short preTs  = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.teamStarting);
        short preHalfNumber = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.halfNumber);
        GD.Print($"[pre-ceremony] halfNumber={preHalfNumber} teamPlayingUp={preTpu} teamStarting={preTs}");

        // Manually trigger EndFirstHalf the same way GameTime.UpdateGameTime
        // would at the 45:00 boundary: pin gt_gameSeconds=0 and call the
        // public test hook. Since EndFirstHalf is private, we use the same
        // entrypoint by pinning the clock to 45 minutes and forcing a
        // last-minute prolong + safety-cap trip — but that's too indirect.
        // Easier: pin gameTimeInMinutes=45, gameSeconds=0, then call
        // GameLoop.SetCameraMovingToShowerState directly to seed Stage 1,
        // and manually set the ceremony stage tracker.
        //
        // 2026-06-06: To match the real EndFirstHalf path, we use reflection
        // to invoke the private static EndFirstHalf via the GetPeriodEndHandler
        // dispatch. The cleanest test entry is: pin gt_gameTimeInMinutes to 45
        // and call UpdateGameTime with the safety cap tripped — but that's also
        // indirect.
        //
        // Session 24 (faithful dispatcher): the 3-stage collapse is gone. We
        // enter the ceremony at its Stage-22 doorway (SetCameraMovingToShower
        // State — the same call the real dispatcher makes from gameState 25,
        // gameLoop.cpp:882-884/2012-2072: flips halfNumber/teamPlayingUp/
        // teamStarting and sets gameState=22) and then assert the FAITHFUL
        // walk: 22 → prepareForInitialKick (gameState=0) → breakCameraMode
        // ladder → 2nd-half kickoff (100/100).
        OpenSwos.SwosVm.Memory.WriteDword(OpenSwos.SwosVm.Memory.Addr.gt_gameSeconds, 0);
        OpenSwos.Sim.Port.GameLoop.SetCameraMovingToShowerState();

        short postStage1Gs = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameState);
        short postHalfNumber = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.halfNumber);
        short postTpu = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.teamPlayingUp);
        short postTs  = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.teamStarting);
        GD.Print($"[Stage-22 entry] gameState={postStage1Gs} (expect 22) halfNumber={postHalfNumber} (expect 2) " +
                 $"teamPlayingUp={postTpu} (expect {3 - preTpu}) teamStarting={postTs} (expect {3 - preTs})");

        bool entryOk = (postStage1Gs == 22) && (postHalfNumber == 2)
            && (postTpu == 3 - preTpu) && (postTs == 3 - preTs);
        if (!entryOk)
        {
            GD.PrintErr($"FAIL: Stage-22 entry state wrong");
            GetTree().Quit(1);
            return;
        }

        // Walk the faithful chain: expect gameState 22 → 0 → 100 within budget.
        bool saw0 = false;
        int resumeAt = -1;
        const int kMaxTicks = 3000;
        short prevGs = postStage1Gs;
        for (int t = 0; t < kMaxTicks; t++)
        {
            TickSwosPort();
            short gs = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameState);
            short gsPl = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameStatePl);
            if (gs != prevGs)
            {
                GD.Print($"[t={t,4}] gameState {prevGs} → {gs} (gsPl={gsPl})");
                prevGs = gs;
            }
            if (gs == 0) saw0 = true;
            if (gs == 100 && gsPl == 100)
            {
                resumeAt = t + 1;
                break;
            }
        }

        short finalHalf = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.halfNumber);
        GD.Print($"[final] resumeAt={resumeAt} saw gameState 0 (2nd-half prep)={saw0} halfNumber={finalHalf} " +
                 $"≈ {(resumeAt > 0 ? resumeAt / 70.0 : -1):F1} s ceremony @ 70 ticks/s");

        if (resumeAt > 0 && saw0 && finalHalf == 2)
        {
            GD.Print("HALFTIME CEREMONY TEST PASSED (faithful 22 → 0 → 2nd-half kickoff, sides swapped)");
            GetTree().Quit(0);
        }
        else
        {
            GD.PrintErr($"FAIL: resumeAt={resumeAt} saw0={saw0} halfNumber={finalHalf}");
            GetTree().Quit(1);
        }
    }

    // === Controlled-player head-number test (task #202) ======================
    // Headless repro for the "controlled outfielder shows shirt '1'" bug that
    // survives into the 2nd half. GameSprites.UpdateControlledPlayerNumbers only
    // computes a number when the gate
    //   (isPlCoach || playerNumber || g_trainingGame) && controlledPlayer
    // is open; a CPU-vs-CPU smoke has playerNumber==0 so the number never
    // computes. This harness forces the gate open (g_trainingGame=1) for BOTH
    // teams every tick, forces each team's controlledPlayer to a rotating
    // NON-KEEPER sprite drawn from that team's OWN spritesTable, plays through a
    // real half-time end-swap, and each sampled tick DUMPS the full resolution
    // chain + WARNs whenever the raw shirt byte is <1/>16 or the ordinal is
    // <1/>11 (the exact conditions the shipping clamp silently hides as "1").
    private void RunHeadNumberTest()
    {
        GD.Print("=== Head-number (controlled-player shirt) test ===");
        _useSwosPort = true;
        _swosVmDirty = true;
        _forceBothTeamsCpu = true;
        _match = MatchState.NewMatch();
        EnterPreKickoff();
        _match.EnterPhase(MatchPhase.Play);
        _appState = AppState.Match;

        int warnCount = 0;
        int rotate = 0;
        int slotOf(int ctrlPtr)
        {
            if (ctrlPtr == 0) return -1;
            return (ctrlPtr - OpenSwos.SwosVm.PlayerSprite.SpritePoolBase)
                   / OpenSwos.SwosVm.PlayerSprite.SlotStride;
        }

        // ---- NATURAL probe: read the controlledPlayer the sim itself assigned
        // (no forcing) and check its ordinal resolves to a real shirt via the
        // team's CURRENT inGameTeamPtr. This is the real-play bug detector.
        void ProbeNatural(int half, int t)
        {
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.g_trainingGame, 1);
            OpenSwos.Sim.Port.GameSprites.UpdateControlledPlayerNumbers();
            for (int i = 0; i < 2; i++)
            {
                bool top = (i == 0);
                int teamBase = OpenSwos.SwosVm.TeamData.Base(top);
                int inGameTeamPtr = OpenSwos.SwosVm.Memory.ReadSignedDword(
                    teamBase + OpenSwos.SwosVm.TeamData.OffInGameTeamPtr);
                int spritesTable = OpenSwos.SwosVm.Memory.ReadSignedDword(
                    teamBase + OpenSwos.SwosVm.TeamData.OffPlayers);
                int ctrlPtr = OpenSwos.SwosVm.Memory.ReadSignedDword(
                    teamBase + OpenSwos.SwosVm.TeamData.OffControlledPlayer);
                if (ctrlPtr == 0) continue;
                int ctrlSlot = slotOf(ctrlPtr);
                short ord = OpenSwos.SwosVm.Memory.ReadSignedWord(
                    ctrlPtr + OpenSwos.SwosVm.PlayerSprite.OffPlayerOrdinal);
                short spriteTeamNo = OpenSwos.SwosVm.Memory.ReadSignedWord(
                    ctrlPtr + OpenSwos.SwosVm.PlayerSprite.OffTeamNumber);
                int rawShirt = (inGameTeamPtr != 0 && ord >= 1 && ord <= 16)
                    ? OpenSwos.SwosVm.Memory.ReadByte(inGameTeamPtr + (ord - 1) * 61 + 3) : -1;
                var (_, _, _, img) = OpenSwos.Sim.Port.GameSprites.GetCurPlayerNumSprite(i);
                int rendered = img < 0 ? -1 : img - OpenSwos.Sim.Port.GameSprites.kSmallDigit1 + 1;
                // Pool the controlled sprite lives in vs the pool this team owns.
                bool tblIsTeam1 = spritesTable == OpenSwos.SwosVm.PlayerSprite.Team1TableBase;
                bool ctrlInTeam1Pool = ctrlSlot >= 0 && ctrlSlot <= 10;
                bool poolMismatch = ctrlSlot >= 0 && (tblIsTeam1 != ctrlInTeam1Pool);
                // GROUND TRUTH: the shirt this sprite SHOULD show = the roster of
                // the physical pool the sprite lives in (team1 store for slots
                // 0-10, team2 store for 11-21), at (ord-1). A midfielder that
                // renders "1" == expected!=resolved with expected being a real
                // number and resolved==1.
                int physRosterBase = ctrlInTeam1Pool
                    ? OpenSwos.SwosVm.Memory.Addr.team1InGameTeamPlayers
                    : OpenSwos.SwosVm.Memory.Addr.team2InGameTeamPlayers;
                int expectedShirt = (ctrlSlot >= 0 && ord >= 1 && ord <= 16)
                    ? OpenSwos.SwosVm.Memory.ReadByte(physRosterBase + (ord - 1) * 61 + 3) : -1;
                bool valueWrong = expectedShirt != rawShirt;
                bool bad = ord < 1 || ord > 11 || rawShirt < 1 || rawShirt > 16;
                if (bad || poolMismatch || valueWrong)
                {
                    warnCount++;
                    GD.PrintErr($"NAT-WARN H{half} t={t} team{i}({(top ? "top" : "bot")}) " +
                        $"sprites={(tblIsTeam1 ? "team1pool" : "team2pool")} " +
                        $"ctrlSlot={ctrlSlot} spriteTeamNo={spriteTeamNo} ord={ord} " +
                        $"rawShirt={rawShirt} expected={expectedShirt} rendered={rendered} " +
                        $"poolMismatch={poolMismatch} valueWrong={valueWrong}");
                }
            }
        }

        // Probes both teams for the current tick. `half` and `t` label the dump.
        // `force` = overwrite controlledPlayer with a rotating own-team outfielder.
        void Probe(int half, int t, bool verbose)
        {
            // Open the gate for both teams (isPlCoach/playerNumber may be 0).
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.g_trainingGame, 1);

            for (int i = 0; i < 2; i++)
            {
                bool top = (i == 0);
                int teamBase = OpenSwos.SwosVm.TeamData.Base(top);
                // Force controlledPlayer to a rotating non-keeper (slots 1..10 in
                // this team's own spritesTable — indices into the pointer table).
                int tableAddr = OpenSwos.SwosVm.TeamData.PlayersTable(top);
                int tblSlot = 1 + ((rotate + i) % 10);           // 1..10 (skip keeper)
                int forcedSprite = OpenSwos.SwosVm.Memory.ReadSignedDword(tableAddr + tblSlot * 4);
                OpenSwos.SwosVm.Memory.WriteDword(
                    teamBase + OpenSwos.SwosVm.TeamData.OffControlledPlayer, forcedSprite);
            }

            // Run the actual code under test.
            OpenSwos.Sim.Port.GameSprites.UpdateControlledPlayerNumbers();

            for (int i = 0; i < 2; i++)
            {
                bool top = (i == 0);
                int teamBase = OpenSwos.SwosVm.TeamData.Base(top);
                int teamNumber = OpenSwos.SwosVm.Memory.ReadSignedWord(
                    teamBase + OpenSwos.SwosVm.TeamData.OffTeamNumber);
                int inGameTeamPtr = OpenSwos.SwosVm.Memory.ReadSignedDword(
                    teamBase + OpenSwos.SwosVm.TeamData.OffInGameTeamPtr);
                int spritesTable = OpenSwos.SwosVm.Memory.ReadSignedDword(
                    teamBase + OpenSwos.SwosVm.TeamData.OffPlayers);
                int ctrlPtr = OpenSwos.SwosVm.Memory.ReadSignedDword(
                    teamBase + OpenSwos.SwosVm.TeamData.OffControlledPlayer);
                int ctrlSlot = slotOf(ctrlPtr);
                short ord = ctrlPtr == 0 ? (short)0
                    : OpenSwos.SwosVm.Memory.ReadSignedWord(
                        ctrlPtr + OpenSwos.SwosVm.PlayerSprite.OffPlayerOrdinal);
                short spriteTeamNo = ctrlPtr == 0 ? (short)0
                    : OpenSwos.SwosVm.Memory.ReadSignedWord(
                        ctrlPtr + OpenSwos.SwosVm.PlayerSprite.OffTeamNumber);
                int playerInfoAddr = inGameTeamPtr + (ord - 1) * 61;
                int rawShirt = (inGameTeamPtr != 0 && ord >= 1 && ord <= 16)
                    ? OpenSwos.SwosVm.Memory.ReadByte(playerInfoAddr + 3) : -1;
                var (_, _, _, img) = OpenSwos.Sim.Port.GameSprites.GetCurPlayerNumSprite(i);
                int rendered = img < 0 ? -1
                    : img - OpenSwos.Sim.Port.GameSprites.kSmallDigit1 + 1;

                // Which roster does inGameTeamPtr point at (team1 vs team2 store)?
                string rosterId =
                    inGameTeamPtr == OpenSwos.SwosVm.Memory.Addr.team1InGameTeamPlayers ? "team1"
                    : inGameTeamPtr == OpenSwos.SwosVm.Memory.Addr.team2InGameTeamPlayers ? "team2"
                    : $"0x{inGameTeamPtr:X}";
                // Which physical pool is the controlled sprite in?
                string pool = ctrlSlot < 0 ? "none" : (ctrlSlot <= 10 ? "slots0-10(team1)" : "slots11-21(team2)");

                bool ctrlInTeam1Pool = ctrlSlot >= 0 && ctrlSlot <= 10;
                int physRosterBase = ctrlInTeam1Pool
                    ? OpenSwos.SwosVm.Memory.Addr.team1InGameTeamPlayers
                    : OpenSwos.SwosVm.Memory.Addr.team2InGameTeamPlayers;
                int expectedShirt = (ctrlSlot >= 0 && ord >= 1 && ord <= 16)
                    ? OpenSwos.SwosVm.Memory.ReadByte(physRosterBase + (ord - 1) * 61 + 3) : -1;
                bool valueWrong = expectedShirt != rawShirt;
                bool bad = ord < 1 || ord > 11 || rawShirt < 1 || rawShirt > 16 || valueWrong;
                if (bad)
                {
                    warnCount++;
                    GD.PrintErr($"WARN H{half} t={t} team{i}({(top ? "top" : "bot")}) " +
                        $"teamNo={teamNumber} inGame={rosterId} sprites=0x{spritesTable:X} " +
                        $"ctrlSlot={ctrlSlot}({pool}) spriteTeamNo={spriteTeamNo} ord={ord} " +
                        $"-> rawShirt={rawShirt} expected={expectedShirt} rendered={rendered} valueWrong={valueWrong}");
                }
                else if (verbose)
                {
                    GD.Print($"     H{half} t={t} team{i}({(top ? "top" : "bot")}) " +
                        $"teamNo={teamNumber} inGame={rosterId} " +
                        $"ctrlSlot={ctrlSlot}({pool}) ord={ord} rawShirt={rawShirt} rendered={rendered}");
                }
            }
            rotate++;
        }

        void DumpRosterShirts(string tag)
        {
            for (int who = 0; who < 2; who++)
            {
                int rosterBase = who == 0
                    ? OpenSwos.SwosVm.Memory.Addr.team1InGameTeamPlayers
                    : OpenSwos.SwosVm.Memory.Addr.team2InGameTeamPlayers;
                var sb = new System.Text.StringBuilder();
                for (int s = 0; s <= 10; s++)
                    sb.Append(OpenSwos.SwosVm.Memory.ReadByte(rosterBase + s * 61 + 3)).Append(' ');
                GD.Print($"[{tag}] team{who + 1} roster shirts (in-game 0..10): {sb}");
            }
            for (int i = 0; i < 2; i++)
            {
                bool top = (i == 0);
                int teamBase = OpenSwos.SwosVm.TeamData.Base(top);
                GD.Print($"[{tag}] {(top ? "top" : "bot")} teamNo=" +
                    $"{OpenSwos.SwosVm.Memory.ReadSignedWord(teamBase + OpenSwos.SwosVm.TeamData.OffTeamNumber)} " +
                    $"inGame=0x{OpenSwos.SwosVm.Memory.ReadSignedDword(teamBase + OpenSwos.SwosVm.TeamData.OffInGameTeamPtr):X} " +
                    $"sprites=0x{OpenSwos.SwosVm.Memory.ReadSignedDword(teamBase + OpenSwos.SwosVm.TeamData.OffPlayers):X}");
            }
        }

        // ---- First half ----------------------------------------------------
        for (int t = 0; t < 60; t++) TickSwosPort();   // settle kickoff
        DumpRosterShirts("H1-pre");
        int h1Warn0 = warnCount;
        for (int t = 0; t < 120; t++)
        {
            TickSwosPort();
            ProbeNatural(1, t);                 // real-play detector (before forcing)
            Probe(1, t, verbose: t % 40 == 0);  // forced-coverage detector
        }
        GD.Print($"[H1 summary] warns this half = {warnCount - h1Warn0}");

        // ---- Trigger the half-time end-swap (same call the real dispatcher
        // makes from gameState 25 — flips teamPlayingUp/teamStarting, sets
        // halfNumber=2, runs Kickoff.ReseatTeamsForNewHalf).
        OpenSwos.SwosVm.Memory.WriteDword(OpenSwos.SwosVm.Memory.Addr.gt_gameSeconds, 0);
        OpenSwos.Sim.Port.GameLoop.SetCameraMovingToShowerState();
        DumpRosterShirts("H2-postswap");

        // Walk the ceremony to the 2nd-half kickoff (gameState 100/100).
        for (int t = 0; t < 3000; t++)
        {
            TickSwosPort();
            short gs = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameState);
            short gsPl = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameStatePl);
            if (gs == 100 && gsPl == 100) { GD.Print($"[H2] 2nd-half kickoff reached at ceremony tick {t}"); break; }
        }
        short finalHalf = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.halfNumber);
        DumpRosterShirts("H2-play");

        // ---- Second half ---------------------------------------------------
        int h2Warn0 = warnCount;
        for (int t = 0; t < 120; t++)
        {
            TickSwosPort();
            ProbeNatural(2, t);                 // real-play detector (before forcing)
            Probe(2, t, verbose: t % 40 == 0);  // forced-coverage detector
        }
        GD.Print($"[H2 summary] halfNumber={finalHalf} warns this half = {warnCount - h2Warn0}");

        // ---- Long realistic phase -----------------------------------------
        // Re-seed a fresh match and let it self-play (CPU-vs-CPU) for a long
        // run that crosses the REAL timer-driven half-time (and any subs / set
        // pieces). Force the number gate open each tick and check the natural
        // controlled player with the absolute oracle. This is the closest
        // headless proxy for the user's real match without manual input.
        GD.Print("=== Long realistic self-play phase (through real half-time) ===");
        _swosVmDirty = true;
        _match = MatchState.NewMatch();
        EnterPreKickoff();
        _match.EnterPhase(MatchPhase.Play);
        _appState = AppState.Match;
        int longWarn0 = warnCount;
        short prevHalf = 1;
        int gs3Ticks = 0, keeperCtrlTicks = 0, ctrlNonNullTicks = 0;
        const int kLongTicks = 25000;
        for (int t = 0; t < kLongTicks; t++)
        {
            TickSwosPort();
            // Coverage bookkeeping.
            {
                short gsC = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameState);
                if (gsC == 3) gs3Ticks++;
                for (int i = 0; i < 2; i++)
                {
                    int cp = OpenSwos.SwosVm.Memory.ReadSignedDword(
                        OpenSwos.SwosVm.TeamData.Base(i == 0) + OpenSwos.SwosVm.TeamData.OffControlledPlayer);
                    if (cp == 0) continue;
                    ctrlNonNullTicks++;
                    if (OpenSwos.SwosVm.Memory.ReadSignedWord(cp + OpenSwos.SwosVm.PlayerSprite.OffPlayerOrdinal) == 1)
                        keeperCtrlTicks++;
                }
            }
            short h = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.halfNumber);
            if (h != prevHalf)
            {
                GD.Print($"[long t={t}] halfNumber {prevHalf} -> {h}");
                DumpRosterShirts($"long-H{h}");
                prevHalf = h;
            }
            // Probe EVERY tick (all game states — play, keeper-claim gs=3, set
            // pieces, breaks) so hardcoded-slot keeper/set-piece assignments that
            // could point controlledPlayer at the wrong physical pool after the
            // swap are caught. ProbeNatural skips teams whose controlledPlayer==0.
            ProbeNatural(9, t);
        }
        GD.Print($"[long summary] warns = {warnCount - longWarn0} | coverage: " +
            $"ctrlNonNull-team-ticks={ctrlNonNullTicks} keeper-controlled={keeperCtrlTicks} gs3(keeperHolds)={gs3Ticks}");

        // ---- Oracle self-check (fault injection) --------------------------
        // Prove the detector is NOT a no-op: deliberately break the H2 pairing
        // the way the reported bug would (point the top team's inGameTeamPtr at
        // the OTHER team's roster while its sprites stay in their pool) and
        // confirm ProbeNatural FIRES. Then restore. A detector that stays
        // silent here would be worthless.
        {
            int selfWarn0 = warnCount;
            int topBase = OpenSwos.SwosVm.TeamData.TopBase;
            int savedInGame = OpenSwos.SwosVm.Memory.ReadSignedDword(
                topBase + OpenSwos.SwosVm.TeamData.OffInGameTeamPtr);
            // Force a controlled outfielder on the top team from its own pool.
            int tbl = OpenSwos.SwosVm.TeamData.PlayersTable(true);
            int spr = OpenSwos.SwosVm.Memory.ReadSignedDword(tbl + 6 * 4);   // table slot 6
            OpenSwos.SwosVm.Memory.WriteDword(
                topBase + OpenSwos.SwosVm.TeamData.OffControlledPlayer, spr);
            // Corrupt: point inGameTeamPtr at the WRONG roster store.
            int wrongRoster = savedInGame == OpenSwos.SwosVm.Memory.Addr.team1InGameTeamPlayers
                ? OpenSwos.SwosVm.Memory.Addr.team2InGameTeamPlayers
                : OpenSwos.SwosVm.Memory.Addr.team1InGameTeamPlayers;
            OpenSwos.SwosVm.Memory.WriteDword(
                topBase + OpenSwos.SwosVm.TeamData.OffInGameTeamPtr, wrongRoster);
            ProbeNatural(0, -1);
            bool detectorFired = warnCount > selfWarn0;
            // Restore.
            OpenSwos.SwosVm.Memory.WriteDword(
                topBase + OpenSwos.SwosVm.TeamData.OffInGameTeamPtr, savedInGame);
            warnCount = selfWarn0;   // don't count the injected fault as a real warn
            GD.Print(detectorFired
                ? "[oracle self-check] PASS — detector fires on injected pairing fault"
                : "[oracle self-check] FAIL — detector is blind (test is worthless!)");
            if (!detectorFired)
            {
                GD.PrintErr("HEAD-NUMBER TEST INCONCLUSIVE: oracle self-check failed");
                GetTree().Quit(2);
                return;
            }
        }

        GD.Print(warnCount == 0
            ? "HEAD-NUMBER TEST PASSED (no bad/wrong shirt in either half)"
            : $"HEAD-NUMBER TEST FAILED ({warnCount} warns)");
        GetTree().Quit(warnCount == 0 ? 0 : 1);
    }

    // === Referee FSM test ====================================================
    // Headless harness for the referee card-handing FSM (Sim/Port/Referee.cs).
    // Boots a fresh match, settles initial kickoff, then PROGRAMMATICALLY injects
    // a foul: writes whichCard / foulXY / bookedPlayer / lastTeamBooked, calls
    // Referee.ActivateReferee, and ticks forward observing the FSM advances:
    //
    //   ActivateReferee → refState = kRefIncoming                     (1)
    //   UpdateRefereeState (incoming) → kRefWaitingPlayer on arrival  (2)
    //   CheckIfThisPlayerGettingBooked → kRefAboutToGiveCard          (3)
    //   UpdateRefereeState (next tick) → kRefBooking                  (4)
    //   UpdateBookedPlayerNumberSprite (blink-table sentinel at idx 29):
    //                                  → kRefLeaving                  (5)
    //   UpdateRefereeState (leaving + offscreen) → kRefOffScreen      (0)
    //
    // Verifies all six transitions tagged via Referee.Dbg* counters. Pass iff
    // every counter > 0; for red cards, sent-away > 0 too.
    //
    // Source: external/swos-port/src/game/referee.cpp:50-258 + 8997 set-site.
    private void RunRefereeFsmTest(int ticks)
    {
        GD.Print($"=== Referee FSM test: {ticks} ticks ===");
        _useSwosPort = true;
        _swosVmDirty = true;
        _forceBothTeamsCpu = true;  // no input in headless — AI-vs-AI
        _match = MatchState.NewMatch();
        EnterPreKickoff();
        _match.EnterPhase(MatchPhase.Play);
        _appState = AppState.Match;
        OpenSwos.Sim.Port.Referee.ResetDebugCounters();

        // Settle initial kickoff so the player pool is populated and the game
        // state is kInProgress (so the referee's onScreen/moveSprite branch fires).
        for (int t = 0; t < 60; t++) TickSwosPort();

        // Pick top team's slot 1 (a non-keeper outfielder) as the bookedPlayer.
        // Pin the foul spot near pitch centre. ActivateReferee will read
        // foulXY off Memory.Addr.foulX/YCoordinate.
        int bookedSpriteAddr = OpenSwos.SwosVm.PlayerSprite.Base(1);
        int topTeamBase      = OpenSwos.SwosVm.TeamData.Base(top: true);
        short foulX = 336, foulY = 400;
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.foulXCoordinate, foulX);
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.foulYCoordinate, foulY);
        OpenSwos.SwosVm.Memory.WriteDword(OpenSwos.SwosVm.Memory.Addr.bookedPlayer, bookedSpriteAddr);
        OpenSwos.SwosVm.Memory.WriteDword(OpenSwos.SwosVm.Memory.Addr.lastTeamBooked, topTeamBase);
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.refTimer, 0);
        // Red card so SendPlayerAway fires when the blink sentinel hits.
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.whichCard, 2);

        OpenSwos.Sim.Port.Referee.ActivateReferee();
        GD.Print($"[fsm-test] foul injected at ({foulX},{foulY}), bookedSlot=1, card=red. " +
                 $"activations={OpenSwos.Sim.Port.Referee.DbgActivations} (expect 1) " +
                 $"Incoming={OpenSwos.Sim.Port.Referee.DbgEnteredIncoming} (expect 1)");

        // Walk forward and log state every 25 ticks. The FSM is paced by the
        // referee sprite walking + the 30-byte blink table (one entry per 8
        // ticks = 240 ticks). ticks=600 gives plenty of headroom.
        int lastRefState = -1, lastWhichCard = -1;
        for (int t = 0; t < ticks; t++)
        {
            TickSwosPort();
            short rs = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.refState);
            short wc = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.whichCard);
            if (rs != lastRefState || wc != lastWhichCard)
            {
                GD.Print($"[t={t,4}] refState={rs} whichCard={wc} " +
                         $"bookedPlayer.state={OpenSwos.SwosVm.PlayerSprite.PlayerState(1)} " +
                         $"refTimer={OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.refTimer)}");
                lastRefState = rs;
                lastWhichCard = wc;
            }
        }

        bool incomingOk = OpenSwos.Sim.Port.Referee.DbgEnteredIncoming >= 1;
        bool waitingOk  = OpenSwos.Sim.Port.Referee.DbgEnteredWaiting  >= 1;
        bool aboutOk    = OpenSwos.Sim.Port.Referee.DbgEnteredAboutToGive >= 1;
        bool bookingOk  = OpenSwos.Sim.Port.Referee.DbgEnteredBooking  >= 1;
        bool leavingOk  = OpenSwos.Sim.Port.Referee.DbgEnteredLeaving  >= 1;
        bool sentAwayOk = OpenSwos.Sim.Port.Referee.DbgPlayersSentAway >= 1;
        // kRefOffScreen requires sprite.onScreen flipping to 0, which is
        // owned by the host renderer's culling pass (gameSprites.cpp:101 +
        // m_refereeSprite.onScreen writes). In the headless smoke there is
        // no culling pass, so onScreen stays pinned at 1 and the
        // kRefLeaving → kRefOffScreen edge never fires. We don't gate the
        // test on it — the booking pipeline up through Leaving + SendPlayerAway
        // is the loadbearing wire here.

        GD.Print($"[fsm-test summary] activations={OpenSwos.Sim.Port.Referee.DbgActivations} " +
                 $"Incoming={OpenSwos.Sim.Port.Referee.DbgEnteredIncoming} " +
                 $"Waiting={OpenSwos.Sim.Port.Referee.DbgEnteredWaiting} " +
                 $"AboutToGive={OpenSwos.Sim.Port.Referee.DbgEnteredAboutToGive} " +
                 $"Booking={OpenSwos.Sim.Port.Referee.DbgEnteredBooking} " +
                 $"Leaving={OpenSwos.Sim.Port.Referee.DbgEnteredLeaving} " +
                 $"OffScreen={OpenSwos.Sim.Port.Referee.DbgEnteredOffScreen} (renderer-owned, headless ok if 0) " +
                 $"sent-away={OpenSwos.Sim.Port.Referee.DbgPlayersSentAway}");

        bool allOk = incomingOk && waitingOk && aboutOk && bookingOk && leavingOk && sentAwayOk;
        if (allOk)
        {
            GD.Print("REFEREE FSM TEST PASSED (full Incoming → Waiting → AboutToGive → Booking → Leaving cycle, player sent off)");
            GetTree().Quit(0);
        }
        else
        {
            GD.PrintErr($"FAIL: " +
                $"incomingOk={incomingOk} waitingOk={waitingOk} aboutOk={aboutOk} " +
                $"bookingOk={bookingOk} leavingOk={leavingOk} sentAwayOk={sentAwayOk}");
            GetTree().Quit(1);
        }
    }

    // === Bench / substitution test ==========================================
    // Headless harness for the ported bench FSM (Sim/Port/Bench.cs ←
    // bench.cpp + updateBench.cpp) and the substituted-player walk
    // (updatePlayers.cpp:9051-9197, hosted in Bench.UpdateSubstitutedPlayerWalk).
    //
    // Drives the REAL input path end to end:
    //   1. force a clean stoppage (gameStatePl=101, the state the original's
    //      benchUnavailable gate accepts — updateBench.cpp:652-664);
    //   2. hold the bench event (the bit the B key feeds) until the bench
    //      opens — ic_pl1Events → team.secondaryFire → benchInvoked;
    //   3. DOWN to substitute row 1, FIRE → kAboutToSubstitute (player 11
    //      queued to come on), FIRE → initiateSubstitution;
    //   4. observe the walk-off (g_substituteInProgress 1 → 2 → -1), the data
    //      swap (substitutePlayer: PlayerInfo records exchanged, outgoing
    //      record flagged kSubstituted, team1NumSubs == 1), the bench close
    //      and the walk-in settling (g_substituteInProgress → 0, both bench
    //      timers drained by benchBlocked);
    //   5. resume proof: the bench can be re-opened (RequestBench1) and left
    //      again via LEFT (leavingBenchMotion) — i.e. no interlock deadlock.
    private void RunBenchTest()
    {
        GD.Print("=== Bench/substitution test ===");
        _useSwosPort = true;
        _swosVmDirty = true;
        _forceBothTeamsCpu = true;   // AI-vs-AI baseline; we inject P1 events by hand
        _match = MatchState.NewMatch();
        EnterPreKickoff();
        _match.EnterPhase(MatchPhase.Play);
        _appState = AppState.Match;

        TickSwosPort();   // seeds the VM (InitSwosVmFromMatchSetup) + first tick

        for (int t = 0; t < 30; t++) TickSwosPort();

        // Injected-input tick: write P1 events (direction 0..7 or -1, fire,
        // bench bit) exactly like TickSwosPort's keyboard bridge, then run one
        // GameLoop tick. p2Dir drives Player 2's joystick slot the same way
        // (used by the AI-team tap probe below).
        void Step(int dir, bool fire, bool benchKey, int ticks = 1, int p2Dir = -1)
        {
            for (int i = 0; i < ticks; i++)
            {
                InputControls.SetJoystickState(InputControls.kPlayer1, dir, fire, false);
                InputControls.SetJoystickState(InputControls.kPlayer2, p2Dir, false, false);
                if (benchKey)
                {
                    int ev = OpenSwos.SwosVm.Memory.ReadSignedDword(OpenSwos.SwosVm.Memory.Addr.ic_pl1Events);
                    ev |= (int)InputControls.GameControlEvents.kGameEventBench;
                    OpenSwos.SwosVm.Memory.WriteDword(OpenSwos.SwosVm.Memory.Addr.ic_pl1Events, ev);
                }
                GameLoop.Tick();
            }
        }

        // Top team gets a P1 human controller so the bench FSM reads P1 events
        // (updateBench.cpp:676-701 updateNonBenchControls / :712 updateBenchControls).
        OpenSwos.SwosVm.Memory.WriteWord(
            OpenSwos.SwosVm.TeamData.TopBase + OpenSwos.SwosVm.TeamData.OffPlayerNumber, 1);

        // --- 0-pre. OPEN-PLAY gate probe (unit level) ------------------------
        // The availability gate (swos.asm @@bench_not_invoked /
        // benchUnavailable, updateBench.cpp:652-664) must swallow AND reset
        // the tap chain on every poll while gameStatePl == kInProgress(100),
        // so neither direction taps NOR the bench event (B / secondary fire)
        // may open the bench during open play — and taps made while PLAYING
        // must never accumulate into a bench call that fires at the next
        // stoppage (user bug: "bench pulls out by itself at free kicks").
        //
        // Driven by calling Bench.UpdateBench() directly with hand-written
        // team.direction / team.secondaryFire values: a full GameLoop.Tick
        // cannot hold the half-real state (legitimate stoppage setters like
        // goalkeeper-claim rewrite gameStatePl mid-tick), while the direct
        // call exercises exactly the original's per-frame poll
        // (gameLoop.cpp:315 → benchCheckControls).
        int topBase = OpenSwos.SwosVm.TeamData.TopBase;
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.gameStatePl, 100);
        bool openPlayTapOpened = false;
        bool openPlayPollValid = true;
        for (int press = 0; press < 3; press++)
        {
            for (int t = 0; t < 6; t++)
            {
                // press for 2 polls, release for 4 (same-direction tap pattern
                // that MUST open the bench once the game is stopped — probe b)
                OpenSwos.SwosVm.Memory.WriteWord(
                    topBase + OpenSwos.SwosVm.TeamData.OffDirection, t < 2 ? 6 /*kFacingLeft*/ : -1);
                // B key / joystick secondary fire pressed the whole time —
                // must ALSO be swallowed by the gate (states gate runs first).
                OpenSwos.SwosVm.Memory.WriteByte(
                    topBase + OpenSwos.SwosVm.TeamData.OffSecondaryFire, 1);
                Bench.UpdateBench();
                openPlayTapOpened |= Bench.InBench();
                openPlayPollValid &= Bench.DebugLastPollGameStatePl == 100;
            }
        }
        OpenSwos.SwosVm.Memory.WriteByte(topBase + OpenSwos.SwosVm.TeamData.OffSecondaryFire, 0);
        OpenSwos.SwosVm.Memory.WriteWord(topBase + OpenSwos.SwosVm.TeamData.OffDirection, -1);
        GD.Print($"[bench-test] gate-probe OPEN PLAY (taps + bench key): opened={openPlayTapOpened} " +
                 $"(expect False) pollSaw100={openPlayPollValid} (expect True) tapState={Bench.DebugTapStateString()}");
        bool reachedOpenPlay = openPlayPollValid;

        // Force a clean stoppage the bench accepts (benchUnavailable,
        // updateBench.cpp:652: gameStatePl != kInProgress, gameState outside
        // 21..30, no penalties / cards; benchBlocked, updateBench.cpp:639:
        // timers + substitute walk idle, referee off, stats panel down).
        void ForceStoppage()
        {
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.gameStatePl, 101);
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.gameState, 0);
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.breakCameraMode, unchecked((short)-1));
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.stoppageEventTimer, 0);
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.stoppageTimerTotal, 0);
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.stoppageTimerActive, 0);
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.statsTimer, 0);
            OpenSwos.SwosVm.Memory.WriteDword(OpenSwos.SwosVm.Memory.Addr.resultTimer, 0);
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.playingPenalties, 0);
            OpenSwos.SwosVm.Memory.WriteDword(OpenSwos.SwosVm.Memory.Addr.lastTeamPlayedBeforeBreak,
                OpenSwos.SwosVm.TeamData.TopBase);
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.g_waitForPlayerToGoInTimer, 0);
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.g_cameraLeavingSubsTimer, 0);
            OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.g_substituteInProgress, 0);
        }
        ForceStoppage();
        CloseBenchIfOpen();   // in case the open-play probe (a bug) left it up
        Step(-1, false, false, Bench.kTapTimeoutTicks * 2 + 4);   // reset tap chains

        // --- 0. Tap-detector fidelity probes (benchInvoked ← swos.asm
        // BenchCheckControls @@sub_controls_updated_branch_on_team, mirrored
        // by updateBench.cpp:762-794) ----------------------------------------
        //
        // (a) A CONTINUOUSLY HELD direction must NOT open the bench:
        //     blockWhileHoldingDirection latches on the first press-edge
        //     (swos.asm @@block_pl1_controls) and only a full release
        //     (direction < 0 → @@pl1_no_control_movement) re-arms the
        //     detector — a hold counts as exactly ONE tap. This is the
        //     user-facing regression guard: waiting at a free kick with an
        //     arrow held must never pull the bench out by itself.
        bool heldOpened = false;
        for (int t = 0; t < 200 && !heldOpened; t++)
        {
            Step(6 /*kFacingLeft*/, false, false);
            heldOpened = Bench.InBench();
        }
        GD.Print($"[bench-test] tap-probe HELD LEFT x200: opened={heldOpened} (expect False)");
        // Full release long enough for the 15-tick tap timeout
        // (kTapTimeoutTicks, counted only on this team's alternating polls —
        // updateBench.cpp:666-674) to reset the chain before the next probe.
        Step(-1, false, false, Bench.kTapTimeoutTicks * 2 + 4);

        // (b) DISCRETE taps of one direction DO open it: press-edge #1 arms
        //     previousDirection, edges #2 and #3 bump tapCount; tapCount
        //     reaching kNumTapsForBench(2) on the third physical press
        //     invokes the bench (swos.asm `cmp subsPl1TapCount, 2 / jz
        //     @@enter_substitutes_menu`; "any one direction tapped three
        //     times quickly", updateBench.cpp:761). Presses/releases span
        //     multiple ticks because the out-of-bench poll alternates teams
        //     every tick.
        // NOTE tap registration lag: the bench poll reads team.direction,
        // which the alternating updateTeamControls wrote up to one tick
        // earlier — a press becomes visible to the tap counter one tick after
        // the joystick edge (both alternations exist in the original:
        // selectTeamForUpdate gameControls.cpp:58-62 vs getNonBenchControlsTeam
        // updateBench.cpp:666-674). So check InBench during the release
        // window too.
        int tapOpenPress = -1;
        for (int press = 1; press <= 3 && tapOpenPress < 0; press++)
        {
            for (int t = 0; t < 2 && tapOpenPress < 0; t++)
            {
                Step(6 /*kFacingLeft*/, false, false);
                if (Bench.InBench()) tapOpenPress = press;
            }
            for (int t = 0; t < 4 && tapOpenPress < 0; t++)
            {
                Step(-1, false, false);      // release gap < tap timeout
                if (Bench.InBench()) tapOpenPress = press;
            }
        }
        bool tapOpened = tapOpenPress == 3;
        GD.Print($"[bench-test] tap-probe TRIPLE-TAP LEFT: openedOnPress={tapOpenPress} (expect 3)");
        CloseBenchIfOpen();
        Step(-1, false, false, Bench.kTapTimeoutTicks * 2 + 4);   // reset tap chains

        // (b2) HOLD-then-double-tap: a direction HELD when the game stops
        //     arms previousDirection on its first poll (one "tap"), so after
        //     releasing it just TWO further discrete taps of the same
        //     direction open the bench. This is the classic user-facing form
        //     ("press 2x left / 2x right during a stoppage") of the same asm
        //     chain — the hold contributes the arming edge.
        Step(6 /*kFacingLeft*/, false, false, 10);   // hold — arms, then blocks
        Step(-1, false, false, 4);                   // release
        int holdTapOpenPress = -1;
        for (int press = 1; press <= 2 && holdTapOpenPress < 0; press++)
        {
            for (int t = 0; t < 2 && holdTapOpenPress < 0; t++)
            {
                Step(6, false, false);
                if (Bench.InBench()) holdTapOpenPress = press;
            }
            for (int t = 0; t < 4 && holdTapOpenPress < 0; t++)
            {
                Step(-1, false, false);
                if (Bench.InBench()) holdTapOpenPress = press;
            }
        }
        bool holdDoubleTapOpened = holdTapOpenPress == 2;
        GD.Print($"[bench-test] tap-probe HOLD+DOUBLE-TAP LEFT: openedOnTap={holdTapOpenPress} (expect 2)");

        // Close the tap-opened bench again (LEFT = leavingBenchMotion after
        // the enter delay) and drain g_cameraLeavingSubsTimer (55) so the
        // next probe starts from a clean out-of-bench state.
        void CloseBenchIfOpen()
        {
            if (!Bench.InBench()) return;
            Step(-1, false, false, Bench.kEnterBenchDelay + 2);
            for (int t = 0; t < 60 && Bench.InBench(); t++)
                Step(6 /*kFacingLeft*/, false, false);
            Step(-1, false, false, Bench.kLeavingSubsDelay + 10);
        }
        CloseBenchIfOpen();

        // (d) An AI team can never invoke the bench: the bottom team has
        //     playerNumber == 0 and playerCoachNumber == 0, so
        //     updateNonBenchControls bails before the tap detector runs
        //     (swos.asm @@possible_cpu_player `jz nullsub_38` /
        //     updateBench.cpp:692-697). P2 joystick taps must do nothing.
        bool aiTapOpened = false;
        for (int press = 0; press < 3; press++)
        {
            for (int t = 0; t < 2; t++)
            {
                Step(-1, false, false, 1, p2Dir: 6 /*kFacingLeft*/);
                aiTapOpened |= Bench.InBench();
            }
            Step(-1, false, false, 4);
        }
        GD.Print($"[bench-test] tap-probe P2 taps vs AI bottom team: opened={aiTapOpened} (expect False)");
        Step(-1, false, false, Bench.kTapTimeoutTicks * 2 + 4);

        // --- 1. Open the bench via the bench event (B key path) -------------
        int openTick = -1;
        for (int t = 0; t < 200; t++)
        {
            Step(-1, false, benchKey: true);
            if (Bench.InBench()) { openTick = t; break; }
        }
        bool opened = openTick >= 0;
        GD.Print($"[bench-test] open: {(opened ? $"OK at tick {openTick}" : "FAILED")} " +
                 $"inSubs={OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.g_inSubstitutesMenu)} " +
                 $"gameStatePl={OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameStatePl)} " +
                 $"gameState={OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameState)}");
        if (!opened) { GD.PrintErr("BENCH TEST FAILED (bench did not open)"); GetTree().Quit(1); return; }

        // Interlock probe: while the bench is open the restart ladder's
        // mode-4 gate (gameLoop.cpp:1484-1489) must hold — the flag it reads
        // is up exactly while InBench().
        bool interlockUp = OpenSwos.SwosVm.Memory.ReadSignedWord(
            OpenSwos.SwosVm.Memory.Addr.g_inSubstitutesMenu) != 0;

        // Release the bench key; drain the enter delay (m_goToBenchTimer=15).
        Step(-1, false, false, Bench.kEnterBenchDelay + 5);

        // --- 2. DOWN → substitute row, FIRE → pick player off ---------------
        Step(4 /*kFacingBottom*/, false, false, 1);
        Step(-1, false, false, 3);
        int arrow = Bench.GetBenchPlayerIndex();
        bool arrowOk = arrow > 0;
        GD.Print($"[bench-test] arrow after DOWN: {arrow} (expected > 0)");

        Step(-1, true, false, 1);   // fire press → firePressedInSubsMenu
        Step(-1, false, false, 2);  // release
        int state = Bench.GetBenchState();
        bool aboutOk = state == Bench.kBenchStateAboutToSubstitute;
        int enterIdx = Bench.PlayerToEnterGameIndex();
        int offPos = Bench.PlayerToBeSubstitutedPos();
        GD.Print($"[bench-test] after FIRE: state={state} (expect {Bench.kBenchStateAboutToSubstitute}) " +
                 $"enterIdx={enterIdx} offPos={offPos} offOrd={Bench.PlayerToBeSubstitutedIndex()}");

        // Identity snapshot (PlayerInfo.index byte = index in the team FILE,
        // written by TeamDataLoader.WritePlayerInfos per swos.asm:36013-36015).
        int teamGame = Bench.GetBenchTeamGameBase();
        byte outIdx = OpenSwos.SwosVm.Memory.ReadByte(
            teamGame + offPos * TeamDataLoader.PlayerInfoSize + TeamDataLoader.OffIndex);
        byte inIdx = OpenSwos.SwosVm.Memory.ReadByte(
            teamGame + enterIdx * TeamDataLoader.PlayerInfoSize + TeamDataLoader.OffIndex);

        // --- 3. FIRE → initiateSubstitution → walk-off -----------------------
        Step(-1, true, false, 1);
        Step(-1, false, false, 2);
        bool subStarted = Bench.SubstituteInProgress();
        short numSubs = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.team1NumSubs);
        GD.Print($"[bench-test] initiateSubstitution: inProgress={subStarted} team1NumSubs={numSubs} (expect 1)");

        // --- 4. Observe walk-off → swap → close → walk-in settle -------------
        bool sawPhase2 = false, sawMinus1 = false;
        int closedTick = -1, settledTick = -1;
        for (int t = 0; t < 4000; t++)
        {
            Step(-1, false, false);
            short sip = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.g_substituteInProgress);
            if (sip == 2) sawPhase2 = true;
            if (sip < 0) sawMinus1 = true;
            if (closedTick < 0 && !Bench.InBench()) closedTick = t;
            if (closedTick >= 0 && sip == 0
                && OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.g_waitForPlayerToGoInTimer) == 0
                && OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.g_cameraLeavingSubsTimer) == 0)
            {
                settledTick = t;
                break;
            }
        }
        GD.Print($"[bench-test] walk: phase2={sawPhase2} newPlayerPhase={sawMinus1} " +
                 $"benchClosedAt={closedTick} settledAt={settledTick}");

        byte newAtPos = OpenSwos.SwosVm.Memory.ReadByte(
            teamGame + offPos * TeamDataLoader.PlayerInfoSize + TeamDataLoader.OffIndex);
        byte newAtBench = OpenSwos.SwosVm.Memory.ReadByte(
            teamGame + enterIdx * TeamDataLoader.PlayerInfoSize + TeamDataLoader.OffIndex);
        sbyte benchPosFlag = (sbyte)OpenSwos.SwosVm.Memory.ReadByte(
            teamGame + enterIdx * TeamDataLoader.PlayerInfoSize + TeamDataLoader.OffPosition);
        bool swapOk = newAtPos == inIdx && newAtBench == outIdx;
        bool subbedFlagOk = benchPosFlag == -1;   // PlayerPosition::kSubstituted
        GD.Print($"[bench-test] data swap: players[{offPos}].index {outIdx}→{newAtPos} (expect {inIdx}); " +
                 $"players[{enterIdx}].index {inIdx}→{newAtBench} (expect {outIdx}); " +
                 $"bench slot position={benchPosFlag} (expect -1 kSubstituted)");

        // --- 5. Resume proof: reopen + leave --------------------------------
        Step(-1, false, false, 100);
        Bench.RequestBench1();
        int reopenTick = -1;
        for (int t = 0; t < 300; t++)
        {
            Step(-1, false, false);
            if (Bench.InBench()) { reopenTick = t; break; }
        }
        bool reopened = reopenTick >= 0;
        bool closedAgain = false;
        if (reopened)
        {
            Step(-1, false, false, Bench.kEnterBenchDelay + 2);
            for (int t = 0; t < 60; t++)
            {
                Step(6 /*kFacingLeft — leavingBenchMotion*/, false, false);
                if (!Bench.InBench()) { closedAgain = true; break; }
            }
        }
        GD.Print($"[bench-test] resume: reopened={reopened} (tick {reopenTick}) leftViaLEFT={closedAgain}");

        bool tapProbesPass = reachedOpenPlay && !openPlayTapOpened
            && !heldOpened && tapOpened && holdDoubleTapOpened && !aiTapOpened;
        bool pass = tapProbesPass && opened && interlockUp && arrowOk && aboutOk && subStarted
            && numSubs == 1 && sawPhase2 && sawMinus1
            && closedTick >= 0 && settledTick >= 0
            && swapOk && subbedFlagOk && reopened && closedAgain;
        if (pass)
        {
            GD.Print("BENCH TEST PASSED (tap-detector probes → open → select sub → walk-off → data swap → close → walk-in → reopen/leave)");
            GetTree().Quit(0);
        }
        else
        {
            GD.PrintErr($"BENCH TEST FAILED: heldOpened={heldOpened} tapOpenPress={tapOpenPress} " +
                $"holdDoubleTap={holdTapOpenPress} openPlayTap={openPlayTapOpened} " +
                $"openPlayPollValid={reachedOpenPlay} aiTap={aiTapOpened} " +
                $"opened={opened} interlock={interlockUp} arrowOk={arrowOk} " +
                $"aboutOk={aboutOk} subStarted={subStarted} numSubs={numSubs} phase2={sawPhase2} " +
                $"minus1={sawMinus1} closed={closedTick} settled={settledTick} swapOk={swapOk} " +
                $"subbedFlag={subbedFlagOk} reopened={reopened} closedAgain={closedAgain}");
            GetTree().Quit(1);
        }
    }

    // Telemetry helper — pretty-prints one player slot's snapshot. Q16.16 → px
    // by `>> 16`. PlayerState byte is the small enum (Normal=0, Down=10, …).
    private static void LogSlot(int t, int slot, string label)
    {
        int xPx = OpenSwos.SwosVm.PlayerSprite.X(slot) >> 16;
        int yPx = OpenSwos.SwosVm.PlayerSprite.Y(slot) >> 16;
        int dir = OpenSwos.SwosVm.PlayerSprite.Direction(slot);
        int st  = OpenSwos.SwosVm.PlayerSprite.PlayerState(slot);
        int img = OpenSwos.SwosVm.PlayerSprite.ImageIndex(slot);
        int spd = OpenSwos.SwosVm.PlayerSprite.Speed(slot);
        int destX = OpenSwos.SwosVm.PlayerSprite.DestX(slot);
        int destY = OpenSwos.SwosVm.PlayerSprite.DestY(slot);
        GD.Print($"           slot[{slot,2}] {label}: ({xPx,4},{yPx,4}) dest=({destX,4},{destY,4}) dir={dir} state={st} img={img} spd={spd}");
    }

    // Idle-frame hook: ONLY drives the --menu-shot screenshot harness. The real
    // game logic runs in _PhysicsProcess; this stays empty in normal launches.
    public override void _Process(double delta)
    {
        if (_menuShotActive) { _menuShotStepSeconds += delta; MenuShotTick(); }
        if (_matchShotActive) { _matchShotSeconds += delta; MatchShotTick(); }

        if (_touchUi) UpdateTouchOverlayVisibility();

        if (_perfLog)
        {
            _perfAccum += delta; _perfFrames++;
            if (_perfAccum >= 1.0)
            {
                long mem = System.GC.GetTotalMemory(false);
                long dMem = mem - _perfLastMem; _perfLastMem = mem;
                double avgMs = _perfAccum / _perfFrames * 1000.0;
                GD.Print($"[perf] state={_appState} fps~={_perfFrames / _perfAccum:0} avgFrame={avgMs:0.00}ms gcTotal={mem / 1024}KB dGC={dMem / 1024}KB");
                _perfAccum = 0; _perfFrames = 0;
            }
        }
    }

    // Wall-clock seconds spent in the CURRENT menu-shot step (frame counts are
    // useless as timeouts — an uncapped _Process can run at hundreds of fps).
    private double _menuShotStepSeconds;

    // --match-shot harness state (#231 verification).
    private bool _matchShotActive;
    private double _matchShotSeconds;
    private int _matchShotStep;

    // Lets the match settle (kickoff -> play, camera centred on the pitch, so the
    // side space behind the bars is GREEN — the strongest possible bleed test),
    // captures the overlay ON, toggles it OFF, captures again, then quits.
    private void MatchShotTick()
    {
        void Shot(string name)
        {
            var img = GetViewport()?.GetTexture()?.GetImage();
            if (img is null) { GD.PrintErr($"[match-shot] no image for {name} (headless?)"); return; }
            string p = System.IO.Path.Combine(_menuShotDir, name + ".png");
            img.SavePng(p);
            GD.Print($"[match-shot] saved {p} ({img.GetWidth()}x{img.GetHeight()}) overlayOn={_touchOverlayOn}");
        }
        switch (_matchShotStep)
        {
            case 0:
                if (_matchShotSeconds < 2.5) return;
                Shot("android_match_overlay_on");
                _touchOverlayOn = false;
                UpdateTouchOverlayVisibility();
                _matchShotStep++; _matchShotSeconds = 0;
                return;
            case 1:
                if (_matchShotSeconds < 0.5) return;
                Shot("android_match_overlay_off");
                _matchShotStep++;
                return;
            default:
                GetTree().Quit();
                return;
        }
    }

    // Deterministically navigates the SWOS-styled menu and captures a PNG of
    // each screen to _menuShotDir, then quits. Lets an agent visually verify the
    // menu look/nav without a human at the keyboard.
    private void MenuShotTick()
    {
        _menuShotFrame++;
        int f = _menuShotFrame;
        var c = _menuClient;
        if (c is null) { GetTree().Quit(); return; }
        void Adv() { _menuShotStep++; _menuShotFrame = 0; _menuShotStepSeconds = 0; }
        void Shot(string name)
        {
            var img = GetViewport()?.GetTexture()?.GetImage();
            if (img is null) { GD.PrintErr($"[menu-shot] no image for {name} (headless?)"); return; }
            string p = System.IO.Path.Combine(_menuShotDir, name + ".png");
            img.SavePng(p);
            GD.Print($"[menu-shot] saved {p} ({img.GetWidth()}x{img.GetHeight()}) screen='{c.DebugTitle}'");
        }
        // Label-driven navigation (DebugFireLabel) so conditional entries (the
        // HOME "CONTINUE:" button) can't shift hard-coded indices. The walk
        // covers every top-level screen INCLUDING the competition flow: create
        // a league, view its dashboard/table/fixtures, then start a CAREER and
        // launch its first fixture as a real match.
        switch (_menuShotStep)
        {
            case 0:  if (f >= 6)
                     {
                         // fresh start: no leftover competition save
                         OpenSwos.Competition.CompetitionStore.Delete();
                         Shot("01_home"); Adv();
                     } break;
            case 1:  if (f >= 3) { c.DebugFireLabel("PLAY MATCH"); Adv(); } break;
            case 2:  if (f >= 6) { Shot("02_friendly"); Adv(); } break;
            case 3:  if (f >= 3) { c.DebugBack(); Adv(); } break;
            case 4:  if (f >= 3) { c.DebugFireLabel("TEAM SETUP"); Adv(); } break;
            case 5:  if (f >= 8) { Shot("03_teamconfig"); Adv(); } break;
            case 6:  if (f >= 3) { c.DebugBack(); Adv(); } break;
            case 7:  if (f >= 3) { c.DebugFireLabel("MULTIPLAYER"); Adv(); } break;
            case 8:  if (f >= 6) { Shot("04_multiplayer"); Adv(); } break;
            case 9:  if (f >= 3) { c.DebugBack(); Adv(); } break;
            case 10: if (f >= 3) { c.DebugFireLabel("COMPETITIONS"); Adv(); } break;
            case 11: if (f >= 6) { Shot("05_competitions_hub"); Adv(); } break;
            case 12: if (f >= 3) { c.DebugFireLabel("NEW LEAGUE"); Adv(); } break;
            case 13:
                // Prove the nation picker, now a SWOS-style pushed list picker:
                // FIRE on COUNTRY opens the scrollable list, screenshot it, then
                // scroll to a foreign country and FIRE to select. Shots are taken
                // a few frames after each action so the render reflects it.
                if (f == 3) c.DebugFireLabel("COUNTRY");
                if (f == 6 && c.DebugListPickerActive) c.DebugListPickerSelect(c.DebugListPickerCount - 1);
                if (f == 9 && c.DebugListPickerActive) Shot("06a_country_picker");
                if (f == 12 && c.DebugListPickerActive) c.DebugListPickerConfirm();
                if (f >= 16) { Shot("06_league_setup"); Adv(); }
                break;
            case 14: if (f >= 3) { c.DebugFireLabel("CREATE LEAGUE"); Adv(); } break;
            case 15: if (f >= 8) { Shot("07_league_dashboard"); GD.Print($"[menu-shot] comp: {c.DebugCompSummary()}"); Adv(); } break;
            case 16: if (f >= 3) { c.DebugFireLabel("TABLE"); Adv(); } break;
            case 17: if (f >= 6) { Shot("08_league_table"); Adv(); } break;
            case 18: if (f >= 3) { c.DebugBack(); Adv(); } break;
            case 19: if (f >= 3) { c.DebugFireLabel("FIXTURES"); Adv(); } break;
            case 20: if (f >= 6) { Shot("09_league_fixtures"); Adv(); } break;
            case 21: if (f >= 3) { c.DebugBack(); Adv(); } break;
            case 22: if (f >= 3) { c.DebugBack(); Adv(); } break;                       // dashboard -> hub
            case 23: if (f >= 3) { c.DebugFireLabel("NEW CAREER"); Adv(); } break;
            case 24: if (f >= 6) { Shot("10_career_setup"); Adv(); } break;
            case 25: if (f >= 3) { c.DebugFireLabel("START CAREER"); Adv(); } break;
            case 26:
                     // START CAREER now opens the MANAGER NAME text-entry screen
                     // (wave M2) — shoot it, then confirm the default name.
                     if (f == 6) Shot("10b_manager_name");
                     if (f == 9 && c.DebugTextInputActive) c.DebugTextConfirm();
                     if (f == 16) { Shot("11_career_dashboard"); GD.Print($"[menu-shot] comp: {c.DebugCompSummary()}"); }
                     // Career SQUAD screen: ages / scouted potential / form / fitness.
                     // Inject injuries first so the red INJ / yellow-knock rows show.
                     if (f == 18) c.DebugInjureSquadPlayers();
                     if (f == 20) c.DebugFireLabel("SQUAD");
                     if (f == 26) Shot("11b_career_squad");
                     if (f == 30) c.DebugBack();
                     if (f == 34) c.DebugFireLabel("TRANSFERS");
                     if (f == 40) Shot("11c_career_transfers");
                     // Inline table-select proof: FIRE on PLAYER drops into the
                     // visible transfer table (no pushed picker); move a few rows
                     // and the bound field turns accent-gold while the row
                     // highlight follows — screenshot then cancel back out.
                     if (f == 42) c.DebugTableSelectEnter();
                     if (f == 45) c.DebugTableSelectMove(+3);
                     if (f == 48 && c.DebugTableSelectActive) Shot("11c2_transfers_tableselect");
                     if (f == 51) c.DebugTableSelectCancel();
                     if (f == 54) c.DebugBack();
                     if (f == 58) c.DebugFireLabel("STAFF");
                     if (f == 64) Shot("11d_career_staff");
                     if (f == 68) c.DebugBack();
                     if (f == 72) c.DebugFireLabel("SCOUTING");
                     if (f == 78) Shot("11e_career_scouting");
                     if (f == 82) c.DebugBack();
                     // Pre-match lineup editor: SWOS inline-table swap. It enters
                     // directly in table mode over the 16 slot rows. Mark row 3,
                     // move to row 5 (both highlights visible), FIRE to swap, then
                     // restore via AUTO.
                     if (f == 86) c.DebugFireLabel("LINEUP");        // push editor -> auto table mode
                     if (f == 92) Shot("11f_career_lineup");        // table mode, row 0 lit
                     if (f == 95) c.DebugTableSelectMove(+3);       // -> row 3
                     if (f == 98) c.DebugTableSelectConfirm();      // mark row 3 (stays in table)
                     if (f == 101) c.DebugTableSelectMove(+2);      // -> row 5
                     if (f == 104) Shot("11g_career_lineup_marked");// mark@3 (gold) + nav@5 (blue)
                     if (f == 107) c.DebugTableSelectConfirm();     // swap 3<->5, saves
                     if (f == 110) Shot("11h_career_lineup_swapped");// swapped names
                     if (f == 113) c.DebugTableSelectCancel();      // -> entry column (AUTO/BACK)
                     if (f == 116) c.DebugFireLabel("AUTO");        // restore original lineup
                     if (f == 119) Shot("11i_career_lineup_auto");
                     if (f == 122) c.DebugBack();
                     if (f >= 126) Adv();
                     break;
            case 27:
                     // E2E proof: instead of PLAYING the career's first fixture,
                     // use SWOS-style VIEW RESULT to simulate it instantly and
                     // verify the result lands back in the standings. The full
                     // visual-match path is covered by --swos-smoke; the menu
                     // harness stays fast (no kickoff-to-fulltime wait).
                     // With PRE MATCH MENUS on, the flow is PLAY NEXT MATCH ->
                     // VERSUS -> VIEW RESULT -> FULL TIME notice -> CONTINUE.
                     if (f == 3)
                     {
                         if (!c.DebugFireLabel("PLAY NEXT MATCH"))
                             GD.PrintErr("[menu-shot] PLAY NEXT MATCH not available!");
                     }
                     if (f == 10) Shot("11b_comp_versus");
                     if (f == 13)
                     {
                         if (!c.DebugFireLabel("VIEW RESULT"))
                             GD.PrintErr("[menu-shot] versus VIEW RESULT not available!");
                         Adv();
                     }
                     // Timeout guard: VIEW RESULT is instant, but never hang.
                     if (_menuShotStepSeconds > 30)
                     {
                         GD.PrintErr("[menu-shot] TIMEOUT waiting for VIEW RESULT");
                         GetTree().Quit(1);
                     }
                     break;
            case 28: if (f >= 6) { Shot("12_career_viewresult"); Adv(); } break;
            case 29: if (f >= 3) { if (!c.DebugFireLabel("CONTINUE")) c.DebugBack(); Adv(); } break;
            case 30: if (f >= 3)
                     {
                         Shot("13_career_after_match");
                         GD.Print($"[menu-shot] comp after match: {c.DebugCompSummary()}");
                         Adv();
                     } break;
            case 31: if (f >= 3) { c.DebugBack(); Adv(); } break;                      // dashboard -> home
            case 32: if (f >= 3) { c.DebugFireLabel("OPTIONS"); Adv(); } break;
            case 33: if (f >= 6) { Shot("14_options_p1"); Adv(); } break;
            case 34: if (f >= 3) { c.DebugFireLabel("PAGE"); Adv(); } break;           // pager -> page 2
            case 35: if (f >= 6) { Shot("15_options_p2"); Adv(); } break;
            default:
                OpenSwos.Competition.CompetitionStore.Delete();   // leave no test save behind
                GD.Print("[menu-shot] done");
                GetTree().Quit();
                break;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        // Menu music follows the app state (start on menu, hard-stop on match,
        // resume on return). No-op headless (node never created).
        UpdateMenuMusic();

        bool acceptPressed = Input.IsActionJustPressed(A_UiAccept);
        bool cancelPressed = Input.IsActionJustPressed(A_UiCancel);
        bool pausePressed  = Input.IsActionJustPressed(A_SwosPause) || _touchPauseRequested; // "P" / touch pause icon
        _touchPauseRequested = false;

        if (_appState == AppState.Paused)
        {
            // Two-tier dismiss: space (or P again) resumes, ESC drops to the menu.
            if (acceptPressed || pausePressed) _appState = AppState.Match;
            else if (cancelPressed)
            {
                // Abandoning mid-match: a pending competition fixture stays
                // UNPLAYED (no result is delivered; the dashboard offers it again).
                _competitionMatchPending = false;
                _match = MatchState.NewMatch();
                EnterPreKickoff();
                _appState = AppState.Menu;
                DestroyMatchAudio();
            }
        }
        else if (_appState == AppState.Menu)
        {
            // SWOS-styled front-end owns menu input + rendering now. Fall back to
            // the legacy plain-text menu only if the client failed to build.
            if (_menuClient is not null)
            {
                _menuClient.Active = true;
                // Android/touch: translate the on-screen d-pad into menu nav
                // pulses (with auto-repeat) BEFORE Tick reads them. No-op on
                // desktop (_touchUi false).
                if (_touchUi) UpdateMenuTouchNav(delta);
                _menuClient.Tick();
            }
            else TickMenu(acceptPressed);
        }
        else
        {
            // ESC or P during a match enters pause. Skip the rest of the tick so
            // the sim doesn't advance on the same frame the pause is triggered.
            if (cancelPressed || pausePressed)
            {
                _appState = AppState.Paused;
            }
            else
            {
                // Apply the global game-speed multiplier. Accumulator adds _gameSpeedScale
                // per Godot physics tick (Godot runs at 70 Hz, see project.godot). Each
                // full unit in the accumulator triggers ONE complete sim tick. So:
                //   scale=1.00 → 1 sim tick / physics tick (SWOS-authentic 70 Hz PC)
                //   scale=0.50 → 1 sim tick every 2 physics ticks (35 Hz sim, half speed)
                //   scale=2.00 → 2 sim ticks per physics tick (140 Hz sim, double speed)
                _speedAccumulator += _gameSpeedScale;
                int simSteps = 0;
                while (_speedAccumulator >= 1.0 && simSteps < 8)  // cap so a huge scale can't hang
                {
                    _speedAccumulator -= 1.0;
                    simSteps++;
                    _match.PhaseTick++;
                    // Per-phase dispatch. Sim only ticks during Play; everything else freezes the
                    // world and counts down its overlay.
                    switch (_match.Phase)
                    {
                    case MatchPhase.PreKickoff:
                        // Port mode: the SWOS machine owns the ENTIRE pre-kickoff
                        // ceremony (gameState 21 hold → prepareForInitialKick →
                        // breakCameraMode ladder → whistle → fire). It must tick
                        // here or the ceremony never advances — the old 140-tick
                        // dwell-without-ticking is legacy-only now.
                        if (_useSwosPort)
                            TickSwosPort();
                        else if (_match.PhaseTick >= MatchTimings.PreKickoffTicks)
                            EnterPlay();
                        break;

                    case MatchPhase.Play:
                        TickPlay();
                        break;

                    case MatchPhase.PostGoal:
                        // Port path: SyncMatchFromSwosPort flips phase back to Play
                        // when UpdatePostGoalRestart returns gameState to 100 — but
                        // SyncMatchFromSwosPort only runs as the tail of
                        // TickSwosPort(). We MUST keep ticking the port here, else
                        // goalCounter never decrements, gameState never returns to
                        // 100, and MatchPhase.PostGoal is permanent → user-visible
                        // freeze on every goal (repro'd 2026-06-06, GUI mode; the
                        // headless --goal-celebration-test bypasses _PhysicsProcess
                        // and called TickSwosPort directly so never tripped this).
                        // Triggering EnterPreKickoff here in port mode would set
                        // _swosVmDirty=true and the next TickSwosPort() would call
                        // InitSwosVmFromMatchSetup() which calls Memory.Init() +
                        // InitGameVariables(), both of which ZERO team{1,2}TotalGoals
                        // — score-reset bug repro'd 2026-06-02 (0-0 -> 0-1 -> 0-0 -> 0-1 ... loop).
                        if (_useSwosPort)
                            TickSwosPort();
                        else if (_match.PhaseTick >= MatchTimings.PostGoalTicks)
                            EnterPreKickoff();
                        break;

                    case MatchPhase.HalfTime:
                        // Same reasoning as PostGoal: in port mode GameTime.UpdateGameTime
                        // owns the half boundary via SetupLastMinuteSwitchNextFrame +
                        // period-end handler. We must keep TickSwosPort()'ing so the
                        // halftime ceremony stages advance and phase eventually flips
                        // back to Play (via SyncMatchFromSwosPort's gameState==100 check).
                        // Nuking SwosVm via EnterPreKickoff here would reset the score.
                        if (_useSwosPort)
                            TickSwosPort();
                        else if (_match.PhaseTick >= MatchTimings.HalfTimeTicks)
                        {
                            _match.Half = 2;
                            _match.MatchTick = 0;  // ← bez tego h2 trigger-uje FullTime w 1. ticku
                            EnterPreKickoff();
                        }
                        break;

                    case MatchPhase.FullTime:
                        // Port mode: keep ticking until the port's end-of-game
                        // chain (gameState 30 → 24 → GameOver → 26 → playGame=0,
                        // gameLoop.cpp:361-395 + dispatcher) fully completes —
                        // the full-time result panel timers live in that chain.
                        if (_useSwosPort &&
                            OpenSwos.SwosVm.Memory.ReadWord(OpenSwos.SwosVm.Memory.Addr.playGame) != 0)
                            TickSwosPort();
                        // Accept after a short delay dismisses the overlay and returns to menu.
                        if (_match.PhaseTick >= MatchTimings.FullTimeAcceptDelay && acceptPressed)
                        {
                            // Competition fixture? Hand the final score to the menu
                            // client (player = home slot) BEFORE NewMatch zeroes it.
                            if (_competitionMatchPending)
                            {
                                _lastCompetitionResult = (_match.ScorePlayer, _match.ScoreOpponent);
                                // The human always sims on the top/home slots
                                // (0..10). Average their consumed energy and map
                                // to the 0..10 distance scale MatchEffects uses
                                // (surrogate is a flat 5). Read while sim memory
                                // is still live — before NewMatch zeroes it.
                                int homeConsumed = 0;
                                for (int slot = 0; slot <= 10; slot++)
                                    homeConsumed += OpenSwos.Sim.Port.PlayerEnergy.Max
                                        - OpenSwos.Sim.Port.PlayerEnergy.ReadEnergy(slot);
                                homeConsumed /= 11;
                                _lastMatchHomeDistance =
                                    homeConsumed * 10 / OpenSwos.Sim.Port.PlayerEnergy.Max;

                                // Post-match injuries (UpdatePlayerInjuries,
                                // swos.asm:35651-35701). The human always plays the
                                // home/top physical store (team1InGameTeamPlayers),
                                // which never moves (half-time swaps only pointer
                                // fields). Scan all 16 slots (a starter subbed off
                                // keeps his record in the 11..15 range) and record
                                // the severity of every slot that finished injured
                                // and was NOT substituted off — the original skips
                                // subbed-off and CPU players (only the human club
                                // persists). Read while sim memory is still live.
                                var injuries = new System.Collections.Generic.List<(int slot, int severity)>();
                                int injBase = OpenSwos.SwosVm.Memory.Addr.team1InGameTeamPlayers;
                                for (int islot = 0; islot < 16; islot++)
                                {
                                    int rec = injBase + islot * OpenSwos.Sim.Port.TeamDataLoader.PlayerInfoSize;
                                    if (OpenSwos.SwosVm.Memory.ReadByte(rec + OpenSwos.Sim.Port.TeamDataLoader.OffSubstituted) != 0)
                                        continue;
                                    if (OpenSwos.SwosVm.Memory.ReadByte(rec + OpenSwos.Sim.Port.TeamDataLoader.OffIsInjured) == 0)
                                        continue;
                                    int severity = (OpenSwos.SwosVm.Memory.ReadByte(
                                        rec + OpenSwos.Sim.Port.TeamDataLoader.OffInjuriesBits) >> 5) & 7;
                                    if (severity <= 0) continue;
                                    int index = OpenSwos.SwosVm.Memory.ReadByte(
                                        rec + OpenSwos.Sim.Port.TeamDataLoader.OffIndex);
                                    injuries.Add((index, severity));
                                }
                                _lastMatchInjuries = injuries.Count > 0 ? injuries : null;
                                _competitionMatchPending = false;
                            }
                            else
                            {
                                // Plain friendly / local-MP match — offer PLAY AGAIN
                                // (replayExit.mnu) when the menu reappears.
                                _friendlyJustEnded = true;
                            }
                            _match = MatchState.NewMatch();
                            EnterPreKickoff();
                            _appState = AppState.Menu;
                            DestroyMatchAudio();
                        }
                        break;

                    case MatchPhase.ThrowIn:
                        TickSetPieceHold();
                        if (_match.PhaseTick >= MatchTimings.ThrowInHoldTicks)
                            ReleaseSetPiece();
                        break;

                    case MatchPhase.CornerKick:
                        TickSetPieceHold();
                        if (_match.PhaseTick >= MatchTimings.CornerHoldTicks)
                            ReleaseSetPiece();
                        break;

                    case MatchPhase.GoalKick:
                        TickSetPieceHold();
                        if (_match.PhaseTick >= MatchTimings.GoalKickHoldTicks)
                            ReleaseSetPiece();
                        break;
                    }
                }
            }
        }

        UpdateUi();
        // === SWOS-port roster vs legacy 4-sprite split (BUG B FIX, 2026-06-01) ===
        // Legacy path: 4 hand-driven sprites (_player/_opponent + 2 keepers).
        // Port path: all 22 SwosVm slots, painted through _portPlayerSprites.
        // We hide the inactive set so we never double-render and the offscreen
        // legacy positions don't confuse the camera bounds.
        int playerOx = 0, playerOy = 0;
        if (_useSwosPort)
        {
            // Hide legacy 4. The port pool below covers slots 0/1/11/12 too.
            if (_playerSprite     is not null) _playerSprite.Visible     = false;
            if (_opponentSprite   is not null) _opponentSprite.Visible   = false;
            if (_homeKeeperSprite is not null) _homeKeeperSprite.Visible = false;
            if (_awayKeeperSprite is not null) _awayKeeperSprite.Visible = false;

            // Paint all 22 SwosVm slots. Each slot picks its kit cache by team
            // (top/bottom) and role (keeper vs outfielder) — same partitioning
            // used in _Ready when the sprites were created. UpdateSprite is the
            // existing legacy renderer (handles atlas-cell lookup, anchor
            // offsets, ZIndex). Passing the SwosVm slot lets it consult the
            // port's frameIndicesTable when wired; falls back to the legacy
            // animation-phase lookup otherwise (see UpdateSprite portSlot doc).
            for (int slot = 0; slot < _portPlayerSprites.Length; slot++)
            {
                var sprite = _portPlayerSprites[slot];
                if (sprite is null) continue;
                sprite.Visible = true;
                bool topTeam = slot < OpenSwos.SwosVm.PlayerSprite.TeamSize;
                bool isKeeper = (slot == OpenSwos.SwosVm.PlayerSprite.SlotGoalie1)
                             || (slot == OpenSwos.SwosVm.PlayerSprite.SlotGoalie2);
                // Per-player skin/hair: pick the face-variant tile cache for the
                // player CURRENTLY occupying this slot (survives substitutions —
                // resolved through PlayerInfo.index each frame).
                int face = FaceForSlot(slot, topTeam);
                var tileCache = isKeeper
                    ? (topTeam ? _homeKeeperTilesFace[face] : _awayKeeperTilesFace[face])
                    : (topTeam ? _playerTilesFace[face]   : _opponentTilesFace[face]);
                var (sox, soy) = UpdateSprite(sprite, _portPlayerStates[slot], tileCache, slot);
                // Camera follows the team1 CF (slot 1) — same anchor the
                // legacy path uses for _player.
                if (slot == OpenSwos.SwosVm.PlayerSprite.SlotTeam1First)
                {
                    playerOx = sox;
                    playerOy = soy;
                }
            }
        }
        else
        {
            // Legacy path — show the 4 hand-driven sprites and hide the
            // port pool so they don't bleed into the legacy renderer.
            if (_playerSprite     is not null) _playerSprite.Visible     = true;
            if (_opponentSprite   is not null) _opponentSprite.Visible   = true;
            if (_homeKeeperSprite is not null) _homeKeeperSprite.Visible = true;
            if (_awayKeeperSprite is not null) _awayKeeperSprite.Visible = true;
            for (int slot = 0; slot < _portPlayerSprites.Length; slot++)
            {
                if (_portPlayerSprites[slot] is not null)
                    _portPlayerSprites[slot]!.Visible = false;
            }
            var (lox, loy) = UpdateSprite(_playerSprite, _player, _playerTiles, -1);
            UpdateSprite(_opponentSprite,   _opponent,   _opponentTiles,   -1);
            UpdateSprite(_homeKeeperSprite, _homeKeeper, _homeKeeperTiles, -1);
            UpdateSprite(_awayKeeperSprite, _awayKeeper, _awayKeeperTiles, -1);
            playerOx = lox;
            playerOy = loy;
        }
        // Referee + card (task #181). Independent of the player pool — reads the
        // ported referee FSM state and draws the ref sprite + card when active.
        UpdateRefereeRender();
        // Injury cross markers (task #184) — small red cross over any player
        // who is down injured, mirroring the pitch's player pool.
        UpdateInjuryMarkers();
        // Energy bars (OpenSWOS fatigue overlay) — small fill bar above the head.
        UpdateEnergyBars();
        // Camera is a child of _playerSprite. The sprite is positioned at
        //   sprite.world = player + (8-sCx - ox, 8-sCy - oy)
        // so to put the camera back at player.world (foot), camera.local must equal
        //   -( (8-sCx) - ox, (8-sCy) - oy ) = (sCx - 8 + ox, sCy - 8 + oy).
        if (_camera is not null)
        {
            // Legacy mode: camera child of _playerSprite + anchor-offset compensation.
            // Port mode: camera child of a dedicated _cameraAnchor node so the focus
            // point is driven by the smoothed Camera.cs tracker without piggy-backing
            // on a sprite's Position (which used to be _ballShadow — that hijack
            // broke the shadow visually and looked frozen on goals).
            Node? desiredParent = _useSwosPort
                ? (Node?)_cameraAnchor
                : _playerSprite;
            if (desiredParent is not null && _camera.GetParent() != desiredParent)
            {
                _camera.GetParent()?.RemoveChild(_camera);
                desiredParent.AddChild(_camera);
            }
            if (_useSwosPort)
            {
                _camera.Position = Vector2.Zero;
            }
            else
            {
                var (sCx, sCy) = PlayerFrames.StandingAnchor(_player.Facing);
                _camera.Position = new Vector2(playerOx + sCx - 8, playerOy + sCy - 8);
            }
        }
        // hideBall (#157b): during a throw-in the sim hides the ball — the
        // taker's animation frames depict it held overhead instead
        // (updatePlayers.cpp:8055-8067 sets hideBall=1; ball.cpp:15-28 then
        // writes ball + shadow imageIndex = -1 every tick). Honor that here:
        // imageIndex < 0 → don't draw the ball or its shadow.
        bool ballHidden = _useSwosPort && _appState == AppState.Match
            && OpenSwos.SwosVm.BallSprite.ImageIndex < 0;
        if (_ballSprite is not null)
        {
            _ballSprite.Visible = !ballHidden;
            // Original draw math: image top-left = (x - centerX, y - z - centerY) —
            // external/swos-port/src/sprites/gameSprites.cpp:210-211 (x/y/z combine)
            // + renderSprites.cpp:44-52 (anchor subtract). Ball center = (1, 3).
            // The sprite is Centered=false so Position IS the image top-left.
            _ballSprite.Position = new Vector2(
                _ball.X.ToInt() - BallSpriteCenterX,
                _ball.Y.ToInt() - _ball.Z.ToInt() - BallSpriteCenterY);
            // Same +1000 offset as players so the ball stays above the pitch at all Y.
            _ballSprite.ZIndex = _ball.Y.ToInt() + 1000;
        }
        if (_useSwosPort && _cameraAnchor is not null)
        {
            // Port mode: drive the camera anchor from the smoothed tracker.
            // Real SWOS applies a slew-rate filter to the camera-tracking target — see
            // external/swos-port/src/game/camera.cpp:219-249
            // (updateCameraCoordinates): each tick computes
            //   delta = (dest - cur) / 16
            // then clamps |delta| <= 5 px before applying. Camera.cs already
            // ports this and runs every tick inside GameLoop.Tick() (via
            // Camera.MoveCamera at line 132). Read the smoothed result
            // here. cameraX/Y is Q16.16 and stores the TOP-LEFT of the VGA
            // viewport (camera.cpp:224-225 subtracts kVgaWidth/2, kVgaHeight/2
            // from the focus); add half-VGA back to recover the focus point,
            // then apply the PitchOffset mapping the rest of the renderer
            // uses (SyncPlayerStateFromSlot, BallSprite sync above).
            //
            // PRIOR BUG: this write used to clobber `_ballShadow.Position`
            // (the camera's parent in port mode). That meant after a goal
            // the shadow detached from the ball (it tracked the camera focus
            // recentring, while the ball stayed at the goal Y) — which the
            // user perceived as a freeze. Splitting `_cameraAnchor` out of
            // the shadow restores shadow-follows-ball semantics and keeps
            // the camera focus on its own node.
            int camXWhole = OpenSwos.Sim.Port.Camera.GetCameraXWhole();
            int camYWhole = OpenSwos.Sim.Port.Camera.GetCameraYWhole();
            int focusX = camXWhole + OpenSwos.Sim.Port.Camera.kVgaWidth / 2;
            int focusY = camYWhole + OpenSwos.Sim.Port.Camera.kVgaHeight / 2;
            _cameraAnchor.Position = new Vector2(focusX + PitchOffsetX, focusY + PitchOffsetY);
        }
        if (_ballShadow is not null)
        {
            _ballShadow.Visible = !ballHidden;
            // Original shadow placement — ball.cpp:1689-1754 (UpdateBallShadow tail):
            //   shadow.x = ball.x + ball.z/2 + 1
            //   shadow.y = ball.y + ball.z/4 + 1 - 10,  shadow.z = -10
            // The -10/-(-10) pair cancels in the draw (y - z), leaving an on-screen
            // ground point of (ball.x + z/2 + 1, ball.y + z/4 + 1); it only exists to
            // bias the y-sort 10 px earlier. At Z=0 the shadow sits 1 px right/below
            // the ball; as the ball rises the shadow slides down-right — the visual
            // height cue. Same (1, 3) anchor as the ball (sprite 1183, Centered=false).
            int shadowX = _ball.X.ToInt() + _ball.Z.ToInt() / 2 + 1;
            int shadowY = _ball.Y.ToInt() + _ball.Z.ToInt() / 4 + 1;
            _ballShadow.Position = new Vector2(shadowX - BallSpriteCenterX,
                                               shadowY - BallSpriteCenterY);
        }
    }

    // === Menu ===

    private void TickMenu(bool accept)
    {
        // Standard UI nav: up/down moves focus across vertical slot list,
        // left/right adjusts the value of the focused slot. Swapped 2026-05-24
        // from the prior left/right-focus convention (user feedback).
        if (Input.IsActionJustPressed("ui_up"))
            _menuFocus = (_menuFocus + MenuSlotCount - 1) % MenuSlotCount;
        else if (Input.IsActionJustPressed("ui_down"))
            _menuFocus = (_menuFocus + 1) % MenuSlotCount;

        // Value step with hold-to-fast-scroll: 1 step on first press, then after a
        // short delay step repeats, with a big stride once held for ~1 s.
        bool upHeld = Input.IsActionPressed("ui_left");
        bool downHeld = Input.IsActionPressed("ui_right");
        int step = 0;
        if (Input.IsActionJustPressed("ui_left")) { step = -1; _menuHeldTicks = 0; }
        else if (Input.IsActionJustPressed("ui_right")) { step = 1; _menuHeldTicks = 0; }
        else if (upHeld || downHeld)
        {
            _menuHeldTicks++;
            // 0-14 ticks: no extra step (just the initial press).
            // 15-44 ticks: 1 step every 3 ticks (~8/sec).
            // 45+ ticks: 5 steps every tick (jumps through ~125 teams/sec).
            int dir = upHeld ? -1 : 1;
            // Thresholds @ 50 Hz: ~0.3 s before any repeat, ~1.8 s before fast-scroll.
            if (_menuHeldTicks >= 90) step = dir * 5;
            else if (_menuHeldTicks >= 30 && (_menuHeldTicks - 30) % 6 == 0) step = dir;
        }
        else
        {
            _menuHeldTicks = 0;
        }

        if (step != 0)
        {
            switch (_menuFocus)
            {
                case 0: // pitch — cycle through whatever variants are actually on disk
                    if (_availablePitches.Count > 0)
                    {
                        int delta = System.Math.Sign(step);  // pitch only steps 1 at a time
                        _pitchSlot = (_pitchSlot + delta + _availablePitches.Count) % _availablePitches.Count;
                        LoadPitchVariant(PitchId);
                    }
                    break;
                case 1:
                    _homeTeamIndex = (_homeTeamIndex + step + _allTeams.Count) % _allTeams.Count;
                    if (_homeTeamIndex == _awayTeamIndex)
                        _homeTeamIndex = (_homeTeamIndex + System.Math.Sign(step) + _allTeams.Count) % _allTeams.Count;
                    ApplyMatchSetup();
                    break;
                case 2:
                    _awayTeamIndex = (_awayTeamIndex + step + _allTeams.Count) % _allTeams.Count;
                    if (_awayTeamIndex == _homeTeamIndex)
                        _awayTeamIndex = (_awayTeamIndex + System.Math.Sign(step) + _allTeams.Count) % _allTeams.Count;
                    ApplyMatchSetup();
                    break;
                case 3:
                    // Cycle Ai → Player 2 → Demo
                    _opponentMode = (OpponentMode)(((int)_opponentMode + System.Math.Sign(step) + 3) % 3);
                    // Don't let held arrows blast through the toggle every tick.
                    _menuHeldTicks = 0;
                    break;
                case 4:
                    {
                        int delta = System.Math.Sign(step);
                        _matchLengthSlot = (_matchLengthSlot + delta + MatchLengthPresets.Length) % MatchLengthPresets.Length;
                    }
                    break;
                case 5:
                    // Game speed multiplier — 0.05 per up/down press.
                    // (Was slot 6 before the difficulty + physics slots were
                    // removed in task #183.)
                    _gameSpeedScale = System.Math.Round(
                        System.Math.Clamp(
                            _gameSpeedScale + System.Math.Sign(step) * SpeedScaleStep,
                            SpeedScaleMin, SpeedScaleMax),
                        2);
                    SaveSettings();
                    break;
            }
        }

        if (accept)
        {
            // Lock in selection and start a fresh match.
            _match = MatchState.NewMatch();
            EnterPreKickoff();
            _appState = AppState.Match;
        }
    }

    private string MenuOverlayText()
    {
        string m0 = _menuFocus == 0 ? "> " : "  ";
        string m1 = _menuFocus == 1 ? "> " : "  ";
        string m2 = _menuFocus == 2 ? "> " : "  ";
        string m3 = _menuFocus == 3 ? "> " : "  ";
        string m4 = _menuFocus == 4 ? "> " : "  ";
        string m5 = _menuFocus == 5 ? "> " : "  ";
        string pitchTag = _availablePitches.Count > 0
            ? $"SWCPICH{PitchId}  ({_pitchSlot + 1}/{_availablePitches.Count})"
            : "(none)";
        string oppTag = _opponentMode switch
        {
            OpponentMode.Ai => "AI",
            OpponentMode.Player2 => "Player 2  (WASD + L-Shift)",
            OpponentMode.Demo => "Demo  (AI vs AI — autoplay)",
            _ => "?",
        };
        // Each preset gives 45 in-game minutes per half, just compressed into less
        // real time (matches SWOS gameLength setting, see gameTime.cpp:128-132).
        string lengthTag = SecondsPerHalf < 60
            ? $"{SecondsPerHalf} s real (45 game-min/half)"
            : $"{SecondsPerHalf / 60} min real (45 game-min/half)";
        string speedTag = $"x{_gameSpeedScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}";
        // Difficulty + physics slots removed (task #183): difficulty only fed the
        // abandoned legacy AiSim, and the port is now the sole gameplay path.
        return $"OpenSWOS\n\n" +
               $"{m0}pitch      : {pitchTag}\n" +
               $"{m1}home       : {TeamTag(_homeTeamIndex)}\n" +
               $"{m2}away       : {TeamTag(_awayTeamIndex)}\n" +
               $"{m3}opponent   : {oppTag}\n" +
               $"{m4}length     : {lengthTag}\n" +
               $"{m5}speed      : {speedTag}\n\n" +
               $"up down  focus    <- ->  value    space  start";
    }

    private string TeamTag(int idx)
    {
        if (idx < 0 || idx >= _allTeams.Count) return "?";
        var t = _allTeams[idx];
        // Drop trailing whitespace/control chars from coach name (TEAM.* records pad
        // fixed-width strings with spaces/zeros).
        string coach = (t.Coach ?? "").Trim().TrimEnd('\0');
        int n = t.Players.Count;
        int sum = 0;
        foreach (var p in t.Players)
            sum += p.Passing + p.Shooting + p.Heading + p.Tackling + p.Control + p.Speed + p.Finishing;
        // Skills are 1..7 each, 7 stats → 7..49 per player. Avg / 7 puts the per-stat
        // average back in 1..7 range for quick "how good is this team" comparison.
        int perStatAvg = (n > 0) ? sum / (n * 7) : 0;
        return string.IsNullOrEmpty(coach)
            ? $"{t.Name}  (avg {perStatAvg})"
            : $"{t.Name}  ·  {coach}  (avg {perStatAvg})";
    }

    // Average speed skill (0..7) across all 16 players on a team. Used to look up the
    // per-skill speed value in PlayerSpeedTable.InProgress.
    private int GetTeamSpeedSkill(int teamIdx)
    {
        if (teamIdx < 0 || teamIdx >= _allTeams.Count) return 4;
        var team = _allTeams[teamIdx];
        if (team.Players.Count == 0) return 4;
        int sum = 0;
        foreach (var p in team.Players) sum += p.Speed;
        int avg = sum / team.Players.Count;
        return System.Math.Clamp(avg, 0, 7);
    }

    private void StepKeeper(ref PlayerState keeper, bool isNorthEnd)
    {
        // 1) Advance state-machine timers (dive recovery → catching → claimed → kick).
        KeeperSim.AdvanceTimers(ref keeper);

        // 2) State-specific movement.
        switch (keeper.GoalieState)
        {
            case KeeperState.DivingHigh:
            case KeeperState.DivingLow:
                // Velocity was set by StartDive; apply with PC 41/64 sprite-delta damping
                // so the dive arc matches outfielder motion damping.
                keeper.X += Fixed.FromRaw(keeper.VelocityX.Raw * BallSim.PcSpriteDeltaNum / BallSim.PcSpriteDeltaDen);
                keeper.Y += Fixed.FromRaw(keeper.VelocityY.Raw * BallSim.PcSpriteDeltaNum / BallSim.PcSpriteDeltaDen);
                // Clamp keeper to goal mouth so a dive doesn't carry him into the pitch.
                int clampedX = System.Math.Clamp(keeper.X.ToInt(), KeeperSim.GoalMouthXMin + PitchOffsetX, KeeperSim.GoalMouthXMax + PitchOffsetX);
                if (clampedX != keeper.X.ToInt()) keeper.X = Fixed.FromInt(clampedX);
                return;
            case KeeperState.CatchingBall:
            case KeeperState.Claimed:
                // Frozen during catch animation + while holding. (Phase C will let claimed
                // keeper walk with the ball — for now he stands.)
                keeper.VelocityX = Fixed.FromInt(0);
                keeper.VelocityY = Fixed.FromInt(0);
                return;
        }

        // 3) Normal: decide whether to commit to a dive, otherwise track the ball X
        //    along the goal mouth. Predict where the ball will cross the keeper Y line —
        //    that's the spot to defend, NOT the ball's current X.
        int predictedX = PredictBallCrossingX(_ball, keeper.Y.ToInt());
        if (KeeperSim.ShouldDive(in keeper, in _ball, predictedX, keeper.Y.ToInt()))
        {
            KeeperSim.StartDive(ref keeper, predictedX, _ball.Z.ToInt());
            return;
        }

        // Track predicted X with a small dead-zone so the keeper doesn't jitter ±1.
        int targetX = System.Math.Clamp(predictedX, KeeperLineMinX, KeeperLineMaxX);
        int dx = targetX - keeper.X.ToInt();
        sbyte sx = (System.Math.Abs(dx) > 2) ? (sbyte)System.Math.Sign(dx) : (sbyte)0;
        var input = new InputState(sx, 0, action: false);
        PlayerSim.Tick(ref keeper, input, KeeperEffectiveSpeed);
        // Facing is fixed by which end the keeper guards — points into the pitch.
        keeper.Facing = isNorthEnd ? Direction.South : Direction.North;
    }

    // Linear interpolation: at what X will the ball be when its Y crosses keeperY?
    // If ball has no Y velocity (rolling sideways or stationary), just return ball.X.
    private static int PredictBallCrossingX(in BallState ball, int keeperY)
    {
        int vyRaw = ball.VelocityY.Raw;
        if (vyRaw == 0) return ball.X.ToInt();
        // dy/vy = ticks until crossing; then predicted X = ball.X + vx * dy/vy.
        long dyRaw = (long)(keeperY - ball.Y.ToInt()) * 256;
        long ticksQ24_8 = dyRaw * 256 / vyRaw;
        long dxRaw = (long)ball.VelocityX.Raw * ticksQ24_8 / 256;
        return ball.X.ToInt() + (int)(dxRaw / 256);
    }

    // SWOS keeper full state machine — replaces the old 2-second auto-punt hack.
    //   1. Detect catch: ball within 72 px + Z<17 + keeper not already holding.
    //   2. Mid-dive contact may be catch (timer ≥ 60) or parry (timer < 60).
    //   3. Once Claimed: count up to 150 then auto-release toward closest team-mate.
    //
    // Called AFTER BallSim.Tick (which has updated ball position for this tick).
    private void HandleKeeperHold(ref PlayerState keeper, int keeperInfluenceIdx, bool isNorthEnd)
    {
        // Already holding → tick auto-release timer.
        if (keeper.GoalieState == KeeperState.Claimed)
        {
            // Pin ball to keeper HANDS (+1 px in facing direction × kBallPlOffsets,
            // Z = 5 px = keeper hand height). swos-port:
            //   updateBallWithControllingGoalkeeper (player.cpp:200) pins ball XY
            //     to keeper + kBallPlOffsets[dir], halves+abs deltaZ
            //   ball.cpp:433-456 (check_keeper_z, already ported in BallUpdate.cs)
            //     forces Z toward 5 each tick
            //   updatePlayers.cpp:3092 (post-goalkeeperClaimedTheBall) sets
            //     ballSprite.z+2 = 5 explicitly
            // The "5" is the magic keeper-hand-height constant.
            // Fixed 2026-05-23: previously set Z = 0 → ball pinned at FEET visually,
            // which was user pain point #2.
            var (fx, fy) = PlayerSim.FacingUnit(keeper.Facing);
            _ball.X = Fixed.FromInt(keeper.X.ToInt() + fx);
            _ball.Y = Fixed.FromInt(keeper.Y.ToInt() + fy);
            _ball.Z = Fixed.FromInt(5);   // keeper hand height — was 0 (bug)
            _ball.VelocityX = Fixed.FromInt(0);
            _ball.VelocityY = Fixed.FromInt(0);
            _ball.VelocityZ = Fixed.FromInt(0);

            if (keeper.GoalieClaimedTimer >= KeeperSim.ClaimAutoReleaseTicks)
            {
                ReleaseKeeperBall(ref keeper, keeperInfluenceIdx, isNorthEnd);
            }
            return;
        }

        // CatchingBall = 15-tick animation; AdvanceTimers moves to Claimed automatically.
        if (keeper.GoalieState == KeeperState.CatchingBall) return;

        // Detect new catch — only when in Normal or mid-dive states.
        int dxk = keeper.X.ToInt() - _ball.X.ToInt();
        int dyk = keeper.Y.ToInt() - _ball.Y.ToInt();
        int distSq = dxk * dxk + dyk * dyk;
        bool ballInCatchZone = distSq < KeeperSim.CatchRadius * KeeperSim.CatchRadius
                            && _ball.Z.ToInt() < KeeperSim.CatchZMax;

        if (!ballInCatchZone) return;

        // Mid-dive contact: catch-vs-parry decision.
        if (KeeperSim.ParryOnlyContact(in keeper))
        {
            // Parry: deflect the ball outward — keep horizontal speed, redirect to side.
            // (Simplified — SWOS picks a deflection direction based on dive direction.)
            int outDirY = isNorthEnd ? +1 : -1;
            _ball.VelocityX = Fixed.FromRaw(_ball.VelocityX.Raw / 2);  // bleed momentum
            _ball.VelocityY = Fixed.FromRaw(outDirY * System.Math.Abs(_ball.VelocityY.Raw) / 2);
            _ball.KickerExclusionIdx = (sbyte)keeperInfluenceIdx;
            _ball.KickerExclusionTicks = 30;
            return;
        }

        // Clean catch — stop ball, transition keeper to CatchingBall (then Claimed).
        _ball.VelocityX = Fixed.FromInt(0);
        _ball.VelocityY = Fixed.FromInt(0);
        _ball.VelocityZ = Fixed.FromInt(0);
        _ball.Z = Fixed.FromInt(0);
        KeeperSim.OnCatch(ref keeper);
    }

    // Auto-release: keeper kicks the ball toward the closest team-mate facing his
    // current direction (or upfield as fallback). swos-port updatePlayers.cpp:16985
    // findClosestPlayerToBallFacing → DoPass.
    private void ReleaseKeeperBall(ref PlayerState keeper, int keeperInfluenceIdx, bool isNorthEnd)
    {
        // Target: outfielder team-mate of the keeper. Home keeper (idx 2) passes to
        // _player (idx 0); away keeper (idx 3) passes to _opponent (idx 1). Simplified
        // until we have a real "closest team-mate" search.
        var teammate = isNorthEnd ? _player : _opponent;
        int dxPx = teammate.X.ToInt() - keeper.X.ToInt();
        int dyPx = teammate.Y.ToInt() - keeper.Y.ToInt();
        // Fallback: just punt upfield if team-mate is too close to dribble to.
        if (System.Math.Abs(dxPx) + System.Math.Abs(dyPx) < 30)
        {
            dxPx = 0;
            dyPx = isNorthEnd ? +1 : -1;
        }
        // DoPass equivalent — modest power, with PostKickCardinalPct reduction.
        int basePower = BallSim.NormalKickPower * BallSim.PostKickCardinalPct / 256;
        ComputeKickVector(dxPx, dyPx, basePower, out int vxRaw, out int vyRaw);
        _ball.VelocityX = Fixed.FromRaw(vxRaw);
        _ball.VelocityY = Fixed.FromRaw(vyRaw);
        _ball.VelocityZ = Fixed.FromInt(0);  // ground roll, like DoPass
        // Advance one damped tick to clear keeper catch radius.
        _ball.X += Fixed.FromRaw(_ball.VelocityX.Raw * BallSim.PcSpriteDeltaNum / BallSim.PcSpriteDeltaDen);
        _ball.Y += Fixed.FromRaw(_ball.VelocityY.Raw * BallSim.PcSpriteDeltaNum / BallSim.PcSpriteDeltaDen);
        // Lock keeper out for a while so he doesn't re-grab.
        _ball.KickerExclusionIdx = (sbyte)keeperInfluenceIdx;
        _ball.KickerExclusionTicks = 30;
        KeeperSim.OnRelease(ref keeper);
    }

    // Keeper movement speed in Q8.8 (raw, SWOS-style). PlayerSim applies the global
    // MovementScaleNum internally, so this is the "as-if-SWOS" speed.
    private const int KeeperEffectiveSpeed = 1024;

    // P2 kick state machine — same SWOS logic as P1 (game.txt:84-86).
    private (InputState input, KickType kickType) ReadPlayer2Input()
    {
        sbyte dx = 0, dy = 0;
        if (Input.IsKeyPressed(Key.A)) dx--;
        if (Input.IsKeyPressed(Key.D)) dx++;
        if (Input.IsKeyPressed(Key.W)) dy--;
        if (Input.IsKeyPressed(Key.S)) dy++;

        bool kickHeld = Input.IsKeyPressed(Key.Shift);
        bool justPressed = kickHeld && !_p2KickPrev;
        bool justReleased = !kickHeld && _p2KickPrev;
        _p2KickPrev = kickHeld;

        bool fire = false;
        var kickType = KickType.Shot;
        if (justPressed) { _p2KickHoldTicks = 1; _p2PulledBack = false; _p2KickFired = false; }
        else if (kickHeld && !_p2KickFired)
        {
            _p2KickHoldTicks++;
            if (IsBackPress(dx, dy, _opponent.Facing)) _p2PulledBack = true;
            if (_p2KickHoldTicks >= KickWindUpTicks)
            {
                fire = true;
                kickType = _p2PulledBack ? KickType.HighShot : KickType.Shot;
                _p2KickFired = true;
            }
        }
        if (justReleased && !_p2KickFired && _p2KickHoldTicks > 0)
        {
            fire = true;
            kickType = KickType.Pass;
            _p2KickFired = true;
        }
        if (!kickHeld) { _p2KickFired = false; _p2KickHoldTicks = 0; _p2PulledBack = false; }

        bool slideHeld = Input.IsKeyPressed(Key.C);
        bool slide = slideHeld && !_p2SlidePrev;
        _p2SlidePrev = slideHeld;

        return (new InputState(dx, dy, fire, slide), kickType);
    }

    // Player input is "pulled back" when the held direction is opposite the facing
    // direction — used by the SWOS kick wind-up to flag high-shot intent.
    private static bool IsBackPress(sbyte dx, sbyte dy, Direction facing)
    {
        if (dx == 0 && dy == 0) return false;
        var (fx, fy) = PlayerSim.FacingUnit(facing);
        int dot = dx * fx + dy * fy;
        return dot < 0;
    }

    private void TickPlay()
    {
        // SWOS port path: bypass legacy BallSim/KeeperSim/PlayerSim entirely and
        // drive gameplay via ported SWOS code (Memory + GameLoop.Tick). The
        // first wire-up — many port functions still stub, so behavior will be
        // chaotic. The legacy path below stays intact as fallback.
        if (_useSwosPort)
        {
            TickSwosPort();
            return;
        }

        InputState humanInput;
        KickType p1KickType = KickType.Shot;
        if (_opponentMode == OpponentMode.Demo)
        {
            // Demo: P1 is also driven by AiSim — Shot only, no wind-up logic.
            var (p1Own, p1Enemy) = PlayerGoalsForHalf(_match.Half);
            humanInput = AiSim.Decide(in _player, in _opponent, in _ball, ownGoalY: p1Own, enemyGoalY: p1Enemy);
        }
        else
        {
            // 1. Player-1 (arrow keys + space). SWOS kick wind-up (game.txt:84-86):
            //    on press start counting; back-press during hold flags high-shot;
            //    release < 4 ticks → pass; auto-fire at 4 ticks → shot/high-shot.
            sbyte dx = 0, dy = 0;
            if (Input.IsActionPressed("ui_left")) dx--;
            if (Input.IsActionPressed("ui_right")) dx++;
            if (Input.IsActionPressed("ui_up")) dy--;
            if (Input.IsActionPressed("ui_down")) dy++;
            bool kickHeld = Input.IsActionPressed("ui_accept");
            bool justPressed = Input.IsActionJustPressed("ui_accept");
            bool justReleased = Input.IsActionJustReleased("ui_accept");
            bool p1Kick = false;
            if (justPressed) { _p1KickHoldTicks = 1; _p1PulledBack = false; _p1KickFired = false; }
            else if (kickHeld && !_p1KickFired)
            {
                _p1KickHoldTicks++;
                if (IsBackPress(dx, dy, _player.Facing)) _p1PulledBack = true;
                if (_p1KickHoldTicks >= KickWindUpTicks)
                {
                    p1Kick = true;
                    p1KickType = _p1PulledBack ? KickType.HighShot : KickType.Shot;
                    _p1KickFired = true;
                }
            }
            if (justReleased && !_p1KickFired && _p1KickHoldTicks > 0)
            {
                p1Kick = true;
                p1KickType = KickType.Pass;
                _p1KickFired = true;
            }
            if (!kickHeld) { _p1KickFired = false; _p1KickHoldTicks = 0; _p1PulledBack = false; }

            bool p1SlideHeld = Input.IsKeyPressed(Key.X);
            bool p1Slide = p1SlideHeld && !_p1SlidePrev;
            _p1SlidePrev = p1SlideHeld;
            humanInput = new InputState(dx, dy, p1Kick, p1Slide);
        }

        // 2. Opponent input — AI or local player-2 (WASD + L-Shift) depending on menu.
        //    Sides swap at half-time so AI goal mapping is half-aware.
        InputState opponentInput;
        KickType opponentKickType = KickType.Shot;
        bool opponentIsCpu;
        if (_opponentMode == OpponentMode.Player2)
        {
            (opponentInput, opponentKickType) = ReadPlayer2Input();
            opponentIsCpu = false;
        }
        else
        {
            var (aiOwn, aiEnemy) = AiGoalsForHalf(_match.Half);
            opponentInput = AiSim.Decide(in _opponent, in _player, in _ball, ownGoalY: aiOwn, enemyGoalY: aiEnemy);
            opponentIsCpu = true;
        }

        // Effective speeds. SWOS picks per-skill speed from PlayerSpeedTable.InProgress
        // (game.txt), penalises the ball-carrier 12.5%, and the AI difficulty knob layers
        // on top (Easy 50% / Normal 70% / Hard 90% — our non-canonical addition).
        int playerSkill = GetTeamSpeedSkill(_homeTeamIndex);
        int aiSkill = GetTeamSpeedSkill(_awayTeamIndex);
        int playerSpeed = PlayerSpeedTable.InProgress[playerSkill];
        int opponentSpeed = PlayerSpeedTable.InProgress[aiSkill];

        bool playerCarriesBall = false, opponentCarriesBall = false;
        if (_ball.Z.Raw < 8 * Fixed.One)
        {
            int pbx = _player.X.ToInt() - _ball.X.ToInt();
            int pby = _player.Y.ToInt() - _ball.Y.ToInt();
            int pbSq = pbx * pbx + pby * pby;
            int obx = _opponent.X.ToInt() - _ball.X.ToInt();
            int oby = _opponent.Y.ToInt() - _ball.Y.ToInt();
            int obSq = obx * obx + oby * oby;
            const int CarrierRangeSq = 20 * 20;
            playerCarriesBall = pbSq < CarrierRangeSq && pbSq < obSq;
            opponentCarriesBall = obSq < CarrierRangeSq && obSq < pbSq;
        }
        if (playerCarriesBall) playerSpeed = playerSpeed * PlayerSpeedTable.BallCarrierMul / 256;
        if (opponentCarriesBall) opponentSpeed = opponentSpeed * PlayerSpeedTable.BallCarrierMul / 256;
        if (opponentIsCpu) opponentSpeed = opponentSpeed * CurrentAiSpeedScale / 256;
        // In Demo, P1 is also CPU-driven → apply the difficulty modifier symmetrically
        // so neither side has the advantage.
        bool playerIsCpu = _opponentMode == OpponentMode.Demo;
        if (playerIsCpu) playerSpeed = playerSpeed * CurrentAiSpeedScale / 256;
        // (No extra Main-side scale — PlayerSim.MovementScaleNum handles the global
        // slow-mo factor internally so animation pace stays SWOS-authentic.)

        int playerSlideMul = playerIsCpu ? PlayerSpeedTable.CpuSlideMul : PlayerSpeedTable.HumanSlideMul;
        int opponentSlideMul = opponentIsCpu ? PlayerSpeedTable.CpuSlideMul : PlayerSpeedTable.HumanSlideMul;

        // 3. Sim tick.
        PlayerSim.Tick(ref _player, humanInput, playerSpeed, playerSlideMul);
        PlayerSim.Tick(ref _opponent, opponentInput, opponentSpeed, opponentSlideMul);

        // Goalkeeper movement — track ball X along the goal line, never leave the mouth.
        // Always-Kick so any ball within InteractionRadius gets punched in the keeper's
        // facing direction (always into the pitch). `_homeKeeper` guards the N goal,
        // `_awayKeeper` the S.
        StepKeeper(ref _homeKeeper, isNorthEnd: true);
        StepKeeper(ref _awayKeeper, isNorthEnd: false);

        System.Span<PlayerInfluence> influences = stackalloc PlayerInfluence[]
        {
            new PlayerInfluence(_player, humanInput.Action, kickType: p1KickType),
            new PlayerInfluence(_opponent, opponentInput.Action, kickType: opponentKickType),
            // Keepers: kick=false + larger catchRadius (24 px vs outfielders' 14).
            // Without the extended radius, a fast shot at 8 px/tick can pass right by
            // the keeper because the per-tick proximity window is too narrow.
            new PlayerInfluence(_homeKeeper, kick: false, catchRadius: 16),
            new PlayerInfluence(_awayKeeper, kick: false, catchRadius: 16),
        };

        BallSim.Tick(ref _ball, influences);

        // B4.7 wire-up DISABLED 2026-05-24 — the naive "undo BallSim position,
        // replay via port" approach has fundamental issues:
        //   1) BallSim's motion includes 41/64 PC damping internally; port path
        //      doesn't apply it correctly when only Section3 runs.
        //   2) BallSim's dribble pin (ball.X = player.X + offset) is overwritten
        //      by undo, so player loses ball control.
        //   3) BallSim's friction + bounce velocity changes can't be cleanly
        //      separated from kick-applied velocity changes.
        //
        // Proper fix needs BallSim split into:
        //   - BallSim.TickKicksAndPossession (kicks, catch, dribble pin)
        //   - BallSim.TickPhysics (motion + friction + bounce + Z)
        // …then port mode replaces TickPhysics with BallUpdate.TickPhysicsOnly.
        //
        // Until then the SwosVm port path stays code-complete but inert at the
        // toggle. The bridge (BallSyncBridge), Memory, PlayerSprite, ported
        // keeper functions all stay — ready for the split refactor.
        //
        // if (_useSwosPort) {  /* needs BallSim split — TODO */  }

        _match.MatchTick++;

        // Stats: track per-team kicks and ball possession ticks.
        if (humanInput.Action) _match.KicksPlayer++;
        if (opponentInput.Action) _match.KicksOpponent++;
        // Track last team to touch the ball for set-piece awards. Mirrors SWOS
        // `lastTeamPlayed` (ball.cpp:3796). Only count outfielder kicks — keepers
        // explicitly skip this so a routine save doesn't gift the opponent a corner.
        if (humanInput.Action) _match.LastTouchedByPlayer = true;
        else if (opponentInput.Action) _match.LastTouchedByPlayer = false;

        // Keeper state machine: detect catch (Ball in 72 px radius + Z<17), advance
        // catching/diving/claimed timers, auto-release at 150 ticks toward team-mate.
        // Influence indices match the array built above: 2 = home keeper, 3 = away.
        HandleKeeperHold(ref _homeKeeper, keeperInfluenceIdx: 2, isNorthEnd: true);
        HandleKeeperHold(ref _awayKeeper, keeperInfluenceIdx: 3, isNorthEnd: false);
        int pdx = _player.X.ToInt() - _ball.X.ToInt();
        int pdy = _player.Y.ToInt() - _ball.Y.ToInt();
        int p1Sq = pdx * pdx + pdy * pdy;
        int odx = _opponent.X.ToInt() - _ball.X.ToInt();
        int ody = _opponent.Y.ToInt() - _ball.Y.ToInt();
        int p2Sq = odx * odx + ody * ody;
        const int PossessionThresholdSq = 24 * 24;
        if (p1Sq < PossessionThresholdSq && p1Sq < p2Sq) _match.PossessionTicksPlayer++;
        else if (p2Sq < PossessionThresholdSq && p2Sq < p1Sq) _match.PossessionTicksOpponent++;

        // 4. Goal detection. Whoever attacks a given goal scores when ball crosses its
        //    line. In half 1 player attacks N goal; sides swap in half 2.
        int bx = _ball.X.ToInt(), by = _ball.Y.ToInt();
        bool inGoalX = bx >= KickoffBallX - GoalMouthHalfWidth
                    && bx <= KickoffBallX + GoalMouthHalfWidth;

        if (inGoalX && by <= GoalLineNorth)
        {
            if (_match.Half == 1) _match.ScorePlayer++; else _match.ScoreOpponent++;
            EnterPostGoal();
            return;
        }
        if (inGoalX && by >= GoalLineSouth)
        {
            if (_match.Half == 1) _match.ScoreOpponent++; else _match.ScorePlayer++;
            EnterPostGoal();
            return;
        }

        // 5. Ball boundary — line crossings trigger set pieces. SWOS detection lives in
        //    external/swos-port/src/game/ball/ball.cpp around l_check_for_corner_goal_out
        //    (line ~3522) and the throw-in cluster cseg_7DAC7..cseg_7DAFE (lines
        //    3948..3969). Original uses six throw-in variants (kThrowInForwardRight..
        //    kThrowInBackLeft = 15..20 in swos.h:576-581); we collapse to one ThrowIn
        //    phase because the variants only differ in which animation plays.
        if (bx < FieldLeft || bx > FieldRight)
        {
            // Touchline crossing → throw-in. The team that did NOT touch the ball last
            // takes it (mirrors `lastTeamPlayed` flip in ball.cpp:3796..3989).
            EnterThrowIn(takingTeamIsPlayer: !_match.LastTouchedByPlayer, ballX: bx, ballY: by);
            return;
        }
        if (by < GoalLineNorth && !inGoalX)
        {
            // North back-line crossing OUTSIDE goal mouth. In half 1 the player attacks
            // the N goal — so the player crossing it is a goal kick for the AI (defender
            // of N), and the AI crossing it is a corner for the player. Sides swap in
            // half 2.
            bool playerAttacksThisLine = (_match.Half == 1);
            if (_match.LastTouchedByPlayer == playerAttacksThisLine)
            {
                // Attacker last touched the ball before it crossed the back-line they're
                // attacking → GOAL KICK for the defenders. (SWOS:
                // ball.cpp:3699 `l_upper_goal_out` → kKeeperHoldsTheBall.)
                EnterGoalKick(takingTeamIsPlayer: !playerAttacksThisLine, atNorthGoal: true);
            }
            else
            {
                // Defender last touched → attacker gets a corner. (SWOS:
                // ball.cpp:3658 `l_left_upper_corner` / 3649 `l_right_upper_corner`.)
                EnterCornerKick(takingTeamIsPlayer: playerAttacksThisLine, atNorthGoal: true,
                    cornerIsLeft: bx < KickoffBallX);
            }
            return;
        }
        if (by > GoalLineSouth && !inGoalX)
        {
            // South back-line, mirror of north. In half 1 the AI attacks the S goal, so
            // the player attacks the OTHER line; in half 2 the player attacks S.
            bool playerAttacksThisLine = (_match.Half != 1);
            if (_match.LastTouchedByPlayer == playerAttacksThisLine)
            {
                // Attacker last touched → goal kick for the defenders.
                EnterGoalKick(takingTeamIsPlayer: !playerAttacksThisLine, atNorthGoal: false);
            }
            else
            {
                // Defender last touched → attacker's corner.
                EnterCornerKick(takingTeamIsPlayer: playerAttacksThisLine, atNorthGoal: false,
                    cornerIsLeft: bx < KickoffBallX);
            }
            return;
        }

        // 6. Half-time / full-time gate.
        if (_match.MatchTick >= SecondsPerHalf * MatchTimings.TicksPerSecond)
        {
            if (_match.Half == 1) EnterHalfTime();
            else EnterFullTime();
        }
    }

    // === SWOS port path ======================================================
    // Active when _useSwosPort == true. Bypasses BallSim/KeeperSim/PlayerSim and
    // drives a full SWOS gameLoop tick through ported code instead. State sync
    // with the C# render layer happens at the boundaries:
    //   - InitSwosVmFromMatchSetup() seeds Memory at kick-off (or on toggle).
    //   - TickSwosPort() reads Godot input → InputControls bridge, calls
    //     GameLoop.Tick(), then copies ball + controlled players back into
    //     _ball / _player / _opponent so the existing sprite renderer picks
    //     them up unchanged.
    //
    // Expect chaotic visible behavior — most callees in GameLoop.Tick() are
    // currently no-op stubs (updatePlayers, movePlayers, AI, …). The first goal
    // here is BOOT + NO CRASH + ONE TICK RUNS. Visible fidelity comes later as
    // the stubs are filled in.

    // True the first time we enter the SwosPort branch after a kick-off / menu
    // selection. Forces InitSwosVmFromMatchSetup() to run on the next tick so we
    // don't lose the state init when toggle is flipped mid-match.
    private bool _swosVmDirty = true;

    // SWOS-port -> MatchState sync state. Set by SyncMatchFromSwosPort() each
    // tick so we can detect score-deltas (new goal -> EnterPostGoal) and
    // playGame falling edge (-> EnterFullTime). Mirrors the port's view of
    // Memory at the boundary so the UI (UpdateUi / OverlayText) reads from a
    // single source of truth.
    private int _swosPortPrevGoalsTeam1 = 0;
    private int _swosPortPrevGoalsTeam2 = 0;
    private bool _swosPortPenaltiesActive = false;

    // Headless test hook: force BOTH teams to CPU control. The faithful
    // keeper-hold / restart flow waits indefinitely for a HUMAN team's fire
    // press (gameLoop.cpp:545-546), so input-less smoke runs must be AI-vs-AI
    // or they stall exactly like the original would with an idle human.
    private bool _forceBothTeamsCpu = false;

    // Seeds Memory at match start. Called once when entering the SwosPort path
    // (gated by _swosVmDirty so it doesn't repeat every tick). Stubs aggressively
    // — many TeamData / formation pointers aren't wired here; that's deferred to
    // future port phases. The minimum we need today is:
    //   1. Memory.Init() — clears and seeds constants.
    //   2. Ball at center pitch (kick-off spot) in Q16.16.
    //   3. 22 player slots at simple formation positions (5 outfielders + 1 keeper
    //      per team) so any updatePlayers code that iterates the pool sees valid
    //      coords instead of all-zero.
    //   4. gameState / gameStatePl set to "in progress" so SWOS knows to run.
    private void InitSwosVmFromMatchSetup()
    {
        // 1. Fresh memory image — clears all state, repopulates constants/tables.
        OpenSwos.SwosVm.Memory.Init(pcMode: true);

        // 2. initMatch() helpers ported from game.cpp:1361-1401 + 1237-1247.
        //    These four calls land in the same order game.cpp:60-93 invokes
        //    them inside initMatch() / startingMatch(). Until this wiring landed
        //    OpenSWOS was silently running with:
        //      - playerCardChance = 0  (cards never triggered at tackles)
        //      - teamPlayingUp = teamStarting = 1  (top always kicked off)
        //      - pitch ball factors hard-locked to "Normal" with no per-pitch
        //        condition response
        //      - no save/restore snapshot of in-game teams for cancel-paths
        //    See game/scripts/Sim/Port/GameTime.cs for the per-helper port.
        OpenSwos.Sim.Port.GameTime.SaveTeams();
        OpenSwos.Sim.Port.GameTime.InitPlayerCardChance();
        OpenSwos.Sim.Port.GameTime.DetermineStartingTeamAndTeamPlayingUp();
        // game.cpp:65 calls setPitchTypeAndNumber() *before* initPitchBallFactors
        // — the per-pitch row that InitPitchBallFactors selects comes from the
        // pitchType chosen here. See game/scripts/Sim/Port/Pitch.cs (ports
        // pitch.cpp:222-284). With gamePitchType defaulting to -1 (Memory.Init),
        // SetPitchType rolls the fixed-probability table; SetPitchNumber hashes
        // top-team name+colours to a deterministic variant.
        OpenSwos.Sim.Port.Pitch.SetPitchTypeAndNumber();
        OpenSwos.Sim.Port.GameTime.InitPitchBallFactors();

        // game.cpp:86 — initGameVariables(). Per-match zeroing of match-scoped
        // runtime state (score digits, stats counters, camera velocity, pl1Fire,
        // currentGameTick / currentTick, AI_turnDirection, etc.). Also writes
        // gameRandValue (game.cpp:75) using one rand() byte — that's the seed
        // ApplyTeamTactics consumes. See Sim/Port/GameTime.cs (ports
        // game.cpp:1403-1448 + line 75).
        //
        // Order matters: this must run AFTER DetermineStartingTeamAndTeamPlayingUp
        // (it uses 2 RNG bytes; tucking InitGameVariables before would pull
        // gameRandValue from the bytes that DetermineStartingTeamAndTeamPlayingUp
        // is meant to consume), AND AFTER InitPitchBallFactors (which already
        // wrote ball{Speed,}BounceFactor). game.cpp orders it line 86, after
        // initPitchBallFactors at line 85 — we mirror that exactly.
        //
        // Memory.Init() above already zeroed most of these slots at process
        // boot, but that ran ONCE; this runs PER MATCH. A future re-entry
        // path (cancel-to-menu, second-leg start, replay restart) will land
        // here without a fresh Memory.Init and need the per-match scrub.
        // Wire the menu HALF LENGTH into the clock BEFORE InitGameVariables (which
        // calls InitTimeDelta). timeDelta = 2700 / SecondsPerHalf makes a full
        // 45-game-minute half take exactly SecondsPerHalf real seconds at 70 Hz —
        // the menu length setting used to be ignored (clock ran at a fixed pace).
        OpenSwos.Sim.Port.GameTime.TimeDeltaOverride =
            System.Math.Clamp(2700 / System.Math.Max(1, SecondsPerHalf), 1, 70);

        OpenSwos.Sim.Port.GameTime.InitGameVariables();

        // 3a. Load the per-team data (PlayerInfo records, names, shotChanceTable,
        // tactics index). Populates the SwosVm memory regions reserved in
        // Memory.Addr (team1InGameTeamPlayers, team1ShotChanceTable, team1NameStorage
        // and the team2 equivalents). Each team's TeamData (+10 inGameTeamPtr,
        // +24 shotChanceTable, +28 tactics, +18 teamNumber) get wired here.
        // See game/scripts/Sim/Port/TeamDataLoader.cs for the layout.
        SeedTeamData();

        // OpenSWOS fatigue: enable the speed penalty for career/competition
        // matches, or when the master OPTIONS toggle is on. Set ONCE per match
        // (deterministic — never flipped mid-match). Energy itself is always
        // tracked (bar shows drain regardless).
        OpenSwos.Sim.Port.PlayerEnergy.EffectEnabled = _fatigueSim || _competitionMatchPending;

        // 3a'. resetResult (result.cpp:104-118) — the original calls it at match
        // init; our Result scorer arrays are C# STATICS that survive Memory.Init,
        // so without this the previous match's scorer list bleeds into the next
        // one (user repro: career match 2 still showed match 1's 6-1 scorers).
        {
            string home = EffectiveTeam(true)?.Name ?? "";
            string away = EffectiveTeam(false)?.Name ?? "";
            OpenSwos.Sim.Port.Result.ResetResult(home, away);
        }

        // 3b. Tactics pool — copy 19 × 370-byte tact_4_3_3 into teamTacticsPool.
        // Until tactics.cpp ports a real loader, every formation index resolves
        // to the same 4-3-3 layout. See Sim/Port/TacticsLoader.cs.
        OpenSwos.Sim.Port.TacticsLoader.LoadAllTactics();

        // PlayerNumber + team1Computer/team2Computer: which team has a human
        // controller. SeedTeamData() above already writes playerNumber based on
        // _opponentMode; we just sync the Memory.Addr.team1Computer/team2Computer
        // flags here for AI brain logic (AiBrain.cs reads these to decide whether
        // to auto-press fire on result screen).
        bool topHuman    = !_forceBothTeamsCpu;  // P1 drives top team (tests force AI-vs-AI)
        bool bottomHuman = !_forceBothTeamsCpu && _opponentMode == OpponentMode.Player2;
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.team1Computer,
                                          topHuman    ? 0 : -1);
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.team2Computer,
                                          bottomHuman ? 0 : -1);

        // 4. Faithful match start — Kickoff.StartingMatch (game.cpp:1450-1475):
        //    gameState=21 (kStartingGame), gameStatePl=101 (kStopped),
        //    stoppageEventTimer=100, ball off-pitch at (1672,449), all 22
        //    sprites spawned AT their formation coordinates via
        //    InitPlayersBeforeEnteringPitch (game.cpp:551-705), showFansCounter
        //    =100. From here the ported GameLoop stoppage dispatcher owns the
        //    ceremony: timer expiry → prepareForInitialKick (gameState=0) →
        //    breakCameraMode ladder 0→8 (result panel at bcm7, whistle at
        //    bcm8, gameStatePl=102) → kicking player fires → 100/100.
        //    Session-24 replacement for the old teleport + forced
        //    gameState=100 short-circuit (which skipped the whole ceremony
        //    and left the user starting mid-pitch with no control handoff).
        OpenSwos.Sim.Port.Kickoff.StartingMatch();
        OpenSwos.SwosVm.Memory.WriteWord(OpenSwos.SwosVm.Memory.Addr.playGame, 1);

        // initGameLoop (gameLoop.cpp:215) calls initBenchBeforeMatch() before
        // the first frame: zeroes g_inSubstitutesMenu/g_cameraLeavingSubsTimer,
        // resets the whole bench FSM (initBenchControls) and seeds bench Y +
        // plComingX/Y (initBench). Runs before the camera seed below, matching
        // the original order (bench init :215 → setCameraToInitialPosition :226).
        // Source: external/swos-port/src/game/bench/bench.cpp:26-33.
        OpenSwos.Sim.Port.Bench.InitBenchBeforeMatch();

        // Camera seed — original initGameLoop() (gameLoop.cpp:226) calls
        // setCameraToInitialPosition() before the first frame: camera behind
        // the kicking-off end's goal (camera.cpp:121-132, crowd view) so the
        // showFansCounter intro shows the stands, then the camera pans up the
        // centre line to the spot. Without this the camera opened at SWOS
        // (0,0) = top-left pitch corner (user-reported wrong start view).
        OpenSwos.Sim.Port.Camera.SetCameraToInitialPosition();

        // 5. Execute the initial kickoff kick — Option C (direct kick at start).
        //
        // In real SWOS, kickoff is a held-state: stoppageEventTimer counts down
        // (game.cpp:1461 — kInitialDelayBeforeKickOff = 100), then the kicking
        // team's controlled player presses fire and updatePlayers' kick branch
        // (updatePlayers.cpp:5332-5407) calls playerKickingBall. We don't yet
        // have either the stoppage-timer dispatcher OR a live PlayerKickingBall
        // wire-up inside PlayerControlled.RunControlledBranch (it calls
        // Stubs.PlayerKickingBall, see PlayerControlled.cs:606). Without an
        // explicit nudge the ball sits on (336, 449) with Speed=0 forever and
        // the smoke test reports STUCK after 200 ticks.
        //
        // We bridge that gap here by invoking the real, ported
        // PlayerActions.PlayerKickingBall (player.cpp:750) directly on the
        // kicking team's CF. This uses ALL the SWOS-port constants:
        //   - kBallKickingSpeed (2208)        — Memory.cs:1008
        //   - kBallKickingDeltaZ (0x14000)    — Memory.cs:1022
        //   - kDefaultDestinations[dir*4]     — Memory.cs:1259-1272 (swos.asm:245575)
        //   - kBallSpeedFinishing / Kicking   — looked up via GetBallDestCoordinatesTable
        // Kicker is slot 1 (top-team CF, the one Kickoff.cs placed on the
        // ball with direction=kFacingBottom (4)). The default-destinations
        // table entry for dir=4 is (0, +1000), so destX/destY becomes
        // (336, 1449) i.e. straight south toward the bottom goal. From there
        // BallUpdate.Tick → CalculateNextBallPosition picks it up each tick.
        //
        // No float math (integer-only via Memory + Q16.16 BallSprite fields).
        // When PlayerControlled.cs:606 is upgraded from Stubs.PlayerKickingBall
        // to the real entry point, this manual call becomes redundant and can
        // be removed in favour of pre-loading TeamData.OffNormalFire = -1 on
        // tick 0 to drive the regular dispatch path.
        //
        // === DISABLED 2026-06-01 (BUG A FIX) =====================================
        // The auto-kick at tick 0 fired the ball south with kBallKickingSpeed=2208
        // before anything was rendered, which presented as a teleport to the
        // bottom edge of the screen on the first visible frame. The user can now
        // manually trigger the kickoff via space (P1 fire), which flows through
        // InputControls.SetJoystickState → kicker fire bitmask in the regular
        // updatePlayers dispatch path. Ball stays still at (336, 449) until then.
        // {
        //     int kickerSpriteAddr = OpenSwos.SwosVm.PlayerSprite.Base(
        //         OpenSwos.SwosVm.PlayerSprite.SlotTeam1First);
        //     int kickerTeamBase   = OpenSwos.SwosVm.TeamData.TopBase;
        //     OpenSwos.Sim.Port.PlayerActions.PlayerKickingBall(kickerTeamBase, kickerSpriteAddr);
        // }

        // Reset port -> MatchState sync trackers. Memory.Init() above already
        // zeroed team{1,2}TotalGoals + goalScored + playingPenalties (see
        // Memory.cs:1084-1097), so prev-goals at 0 matches the live state.
        _swosPortPrevGoalsTeam1 = 0;
        _swosPortPrevGoalsTeam2 = 0;
        _swosPortPenaltiesActive = false;

        _swosVmDirty = false;
        GD.Print("InitSwosVmFromMatchSetup: seeded SwosVm with kick-off layout.");

        // In-match audio — create the MatchAudio node (skipped headless) and load
        // samples now that team data (home kit colour for the intro chant) is
        // seeded. OnMatchInit is idempotent, so the half-time re-seed keeps the
        // crowd bed + chants running continuously. See game/scripts/Audio/.
        EnsureMatchAudio();
        // Belt-and-suspenders: hard-stop any front-end music the instant a match
        // begins (the per-frame driver also does this, but do it immediately).
        OpenSwos.Audio.MenuMusic.Instance?.Stop();
        // Resolve the SOUND preference (PC/AMIGA) against availability and hand it
        // to MatchAudio BEFORE OnMatchInit locks it in for the match.
        OpenSwos.Audio.MatchAudio.Source = ResolveSoundSource();
        OpenSwos.Audio.MatchAudio.Instance?.OnMatchInit();
    }

    // Create the MatchAudio scene node once per match (host-side only). Never in
    // headless mode — the sim's per-frame MatchAudio.Tick() no-ops when Instance
    // is null, so --competition-test / --menu-shot never touch the audio bus.
    private void EnsureMatchAudio()
    {
        if (DisplayServer.GetName() == "headless") return;
        if (OpenSwos.Audio.MatchAudio.Instance != null) return;
        var node = new OpenSwos.Audio.MatchAudio { Name = "MatchAudio" };
        AddChild(node);
    }

    // Tear down the MatchAudio node on the return to menu so the next match
    // starts with a fresh crowd/chant/commentary state (and the home team's
    // intro chant is re-picked from the new kit colour).
    private void DestroyMatchAudio()
    {
        var node = OpenSwos.Audio.MatchAudio.Instance;
        if (node != null) { node.QueueFree(); }
    }

    // Front-end music node — host-side only, never headless (mirrors EnsureMatchAudio).
    private void EnsureMenuMusic()
    {
        if (DisplayServer.GetName() == "headless") return;
        if (OpenSwos.Audio.MenuMusic.Instance != null) return;
        AddChild(new OpenSwos.Audio.MenuMusic { Name = "MenuMusic" });
    }

    // Resolve the persisted preference against availability. OFF stays OFF; an
    // unavailable choice degrades to the first available of AMIGA, PC, CUSTOM
    // (CUSTOM is always available → music always plays unless the user picks OFF).
    private OpenSwos.Audio.MenuMusic.MusicSource ResolveMenuMusic()
    {
        var pref = _menuMusic;
        if (pref == OpenSwos.Audio.MenuMusic.MusicSource.Off) return pref;
        if (MenuMusicSourceAvailable(pref)) return pref;
        foreach (var s in new[] { OpenSwos.Audio.MenuMusic.MusicSource.Amiga,
                                  OpenSwos.Audio.MenuMusic.MusicSource.Pc,
                                  OpenSwos.Audio.MenuMusic.MusicSource.Custom })
            if (MenuMusicSourceAvailable(s)) return s;
        return OpenSwos.Audio.MenuMusic.MusicSource.Off;
    }

    // Per-frame: menu music follows the app state. Plays in the menu (idempotent),
    // hard-stops the moment a match/pause begins. Resumes on return to menu.
    private void UpdateMenuMusic()
    {
        var mm = OpenSwos.Audio.MenuMusic.Instance;
        if (mm == null) return;
        if (_appState == AppState.Menu) mm.Play(ResolveMenuMusic());
        else mm.Stop();
    }

    // Populates per-team PlayerInfo records + name + shotChanceTable + tactics
    // index for both top (home) and bottom (away) sides. Pulls from
    // `_allTeams[_homeTeamIndex]` and `_allTeams[_awayTeamIndex]` — these are
    // the same TeamRecord objects the menu cycles through (loaded from PC
    // TEAM.* files via PcAssetSource). See Sim/Port/TeamDataLoader.cs for the
    // memory layout.
    // Resolves the FACE type (0=WHITE, 1=GINGER, 2=BLACK) of the player
    // currently occupying a SwosVm sprite slot: slot → in-game ordinal →
    // PlayerInfo.index (the ROSTER index, kept current across substitutions
    // by Bench's data swap) → TeamRecord.Players[roster].Face.
    private int FaceForSlot(int slot, bool topTeam)
    {
        var team = EffectiveTeam(topTeam);
        if (team is null) return 0;
        int ord = slot - (topTeam ? 0 : OpenSwos.SwosVm.PlayerSprite.TeamSize);   // 0..10 = PlayerInfo slot
        if (ord < 0 || ord > 15) return 0;
        int playersBase = topTeam
            ? OpenSwos.SwosVm.Memory.Addr.team1InGameTeamPlayers
            : OpenSwos.SwosVm.Memory.Addr.team2InGameTeamPlayers;
        int roster = OpenSwos.SwosVm.Memory.ReadByte(
            playersBase + ord * OpenSwos.Sim.Port.TeamDataLoader.PlayerInfoSize
                        + OpenSwos.Sim.Port.TeamDataLoader.OffIndex);
        if (roster < 0 || roster >= team.Players.Count) return 0;
        int f = team.Players[roster].Face;
        return (f >= 0 && f < OpenSwos.Assets.KitPalette.FaceCount) ? f : 0;
    }

    // Resolves the TeamRecord the sim should actually use for a side: the
    // per-match override when one is set (career fixtures seed the LIVE squad —
    // see Main.MenuHost.cs _homeTeamOverride/_awayTeamOverride), otherwise the
    // read-only master record from _allTeams. home == true is the top/human side.
    private OpenSwos.Assets.TeamRecord? EffectiveTeam(bool home)
    {
        var over = home ? _homeTeamOverride : _awayTeamOverride;
        if (over is not null) return over;
        int idx = home ? _homeTeamIndex : _awayTeamIndex;
        return (idx >= 0 && idx < _allTeams.Count) ? _allTeams[idx] : null;
    }

    private void SeedTeamData()
    {
        OpenSwos.Assets.TeamRecord? homeTeam = EffectiveTeam(true);
        OpenSwos.Assets.TeamRecord? awayTeam = EffectiveTeam(false);

        // P1 is always the top-team controller (tests force AI-vs-AI). Needed
        // both for TeamData wiring below and for the ScaleSkill price feedback
        // (CPU teams pin the price percent to 100, swos.asm:37691-37695).
        bool topIsHuman    = !_forceBothTeamsCpu;
        bool bottomIsHuman = !_forceBothTeamsCpu && _opponentMode == OpponentMode.Player2;

        // PlayerInfo records — 16 × 61 bytes per team starting at
        // team1InGameTeamPlayers / team2InGameTeamPlayers.
        if (homeTeam is not null)
            OpenSwos.Sim.Port.TeamDataLoader.WritePlayerInfos(
                OpenSwos.SwosVm.Memory.Addr.team1InGameTeamPlayers, homeTeam,
                isHumanControlled: topIsHuman);
        if (awayTeam is not null)
            OpenSwos.Sim.Port.TeamDataLoader.WritePlayerInfos(
                OpenSwos.SwosVm.Memory.Addr.team2InGameTeamPlayers, awayTeam,
                isHumanControlled: bottomIsHuman);

        // Wire TeamData fields + topTeamInGame / bottomTeamInGame globals +
        // res_team1Name / res_team2Name name pointers. The player-number flag
        // depends on _opponentMode (P1 is always top-team controller).

        if (homeTeam is not null)
            OpenSwos.Sim.Port.TeamDataLoader.WireTeamFields(
                top: true, team: homeTeam,
                playersBaseAddr:    OpenSwos.SwosVm.Memory.Addr.team1InGameTeamPlayers,
                shotChanceTableAddr: OpenSwos.SwosVm.Memory.Addr.team1ShotChanceTable,
                nameStorageAddr:    OpenSwos.SwosVm.Memory.Addr.team1NameStorage,
                isHumanControlled:  topIsHuman);
        if (awayTeam is not null)
            OpenSwos.Sim.Port.TeamDataLoader.WireTeamFields(
                top: false, team: awayTeam,
                playersBaseAddr:    OpenSwos.SwosVm.Memory.Addr.team2InGameTeamPlayers,
                shotChanceTableAddr: OpenSwos.SwosVm.Memory.Addr.team2ShotChanceTable,
                nameStorageAddr:    OpenSwos.SwosVm.Memory.Addr.team2NameStorage,
                isHumanControlled:  bottomIsHuman);
    }

    // (SeedTeamFormation removed — replaced by Kickoff.PrepareForInitialKick
    //  in Sim/Port/Kickoff.cs which handles all 22 slots + ball + per-team
    //  controlledPlayer / playerHasBall wiring in one shot.)

    // One sim tick under the SWOS port. Reads input → memory bridge, calls
    // GameLoop.Tick() exactly once, then copies positions back into the C#
    // BallState / PlayerState so the sprite renderer sees them.
    private void TickSwosPort()
    {
        if (_swosVmDirty)
            InitSwosVmFromMatchSetup();

        // 1. Sample Godot input → bridge into InputControls memory slots.
        //    We map P1 arrows + space → kPlayer1; WASD + Shift → kPlayer2.
        //    The bridge in InputControls.SetJoystickState converts a (direction,
        //    fireDown) pair into the GameControlEvents bitmask the rest of the
        //    port consumes.
        sbyte p1dx = 0, p1dy = 0;
        if (Input.IsActionPressed(A_UiLeft)) p1dx--;
        if (Input.IsActionPressed(A_UiRight)) p1dx++;
        if (Input.IsActionPressed(A_UiUp)) p1dy--;
        if (Input.IsActionPressed(A_UiDown)) p1dy++;
        // Gamepad (device 0) — additive; harmless when no pad present. Handhelds (R36S) map here.
        const float kJoyDeadzone = 0.4f;
        if (Input.GetJoyAxis(0, JoyAxis.LeftX) < -kJoyDeadzone || Input.IsJoyButtonPressed(0, JoyButton.DpadLeft))  p1dx = -1;
        if (Input.GetJoyAxis(0, JoyAxis.LeftX) >  kJoyDeadzone || Input.IsJoyButtonPressed(0, JoyButton.DpadRight)) p1dx =  1;
        if (Input.GetJoyAxis(0, JoyAxis.LeftY) < -kJoyDeadzone || Input.IsJoyButtonPressed(0, JoyButton.DpadUp))    p1dy = -1;
        if (Input.GetJoyAxis(0, JoyAxis.LeftY) >  kJoyDeadzone || Input.IsJoyButtonPressed(0, JoyButton.DpadDown))  p1dy =  1;
        // Touch d-pad (Android on-screen stick) — additive override, same as the gamepad.
        if (_touchUi && (_touchDx != 0 || _touchDy != 0)) { p1dx = _touchDx; p1dy = _touchDy; }
        bool p1PadFire = Input.IsJoyButtonPressed(0, JoyButton.A);
        bool p1Fire = Input.IsActionPressed(A_UiAccept) || p1PadFire || (_touchUi && _touchFire);
        bool p1FireTriggered = Input.IsActionJustPressed(A_UiAccept);
        int p1Dir = AxisToSwosDirection(p1dx, p1dy);
        OpenSwos.Sim.Port.InputControls.SetJoystickState(
            OpenSwos.Sim.Port.InputControls.kPlayer1, p1Dir, p1Fire, p1FireTriggered);

        // Bench call — key B (P1). The original summons the substitutes bench
        // with the joystick's secondary fire (kGameEventBench) or a quick
        // direction triple-tap while the ball is dead (updateBench.cpp:762-794
        // benchInvoked, gated by benchUnavailable at updateBench.cpp:652-664).
        // Our keyboard bridge maps B → kGameEventBench for P1; the bit must be
        // OR-ed in AFTER SetJoystickState (which rewrites the event dword).
        // The event flows: ic_pl1Events → InputControls.UpdateTeamControls →
        // team.secondaryFire → Bench.UpdateNonBenchControls → bench opens.
        if (Input.IsKeyPressed(Key.B))
        {
            int ev1 = OpenSwos.SwosVm.Memory.ReadSignedDword(OpenSwos.SwosVm.Memory.Addr.ic_pl1Events);
            ev1 |= (int)OpenSwos.Sim.Port.InputControls.GameControlEvents.kGameEventBench;
            OpenSwos.SwosVm.Memory.WriteDword(OpenSwos.SwosVm.Memory.Addr.ic_pl1Events, ev1);
        }

        sbyte p2dx = 0, p2dy = 0;
        if (Input.IsKeyPressed(Key.A)) p2dx--;
        if (Input.IsKeyPressed(Key.D)) p2dx++;
        if (Input.IsKeyPressed(Key.W)) p2dy--;
        if (Input.IsKeyPressed(Key.S)) p2dy++;
        // Gamepad (device 1) — additive; harmless when no pad present.
        if (Input.GetJoyAxis(1, JoyAxis.LeftX) < -kJoyDeadzone || Input.IsJoyButtonPressed(1, JoyButton.DpadLeft))  p2dx = -1;
        if (Input.GetJoyAxis(1, JoyAxis.LeftX) >  kJoyDeadzone || Input.IsJoyButtonPressed(1, JoyButton.DpadRight)) p2dx =  1;
        if (Input.GetJoyAxis(1, JoyAxis.LeftY) < -kJoyDeadzone || Input.IsJoyButtonPressed(1, JoyButton.DpadUp))    p2dy = -1;
        if (Input.GetJoyAxis(1, JoyAxis.LeftY) >  kJoyDeadzone || Input.IsJoyButtonPressed(1, JoyButton.DpadDown))  p2dy =  1;
        bool p2Fire = Input.IsKeyPressed(Key.Shift) || Input.IsJoyButtonPressed(1, JoyButton.A);
        int p2Dir = AxisToSwosDirection(p2dx, p2dy);
        OpenSwos.Sim.Port.InputControls.SetJoystickState(
            OpenSwos.Sim.Port.InputControls.kPlayer2, p2Dir, p2Fire, false);

        // 2. Run one full SWOS game tick. GameLoop owns the order — see
        //    gameLoop.cpp:289-317. Most callees are stubs today; the live ones
        //    are BallUpdate.Tick(), GameTime.UpdateGameTime(), Referee.UpdateReferee().
        OpenSwos.Sim.Port.GameLoop.Tick();

        // 3. Copy state back out so the sprite renderer (UpdateSprite below)
        //    paints the new positions. BallSprite.X/Y/Z are Q16.16 — shift down
        //    8 to get our Q24.8 raw value (or just integer-divide by 65536 to
        //    get whole pixels). We keep velocity in sync too, even though the
        //    renderer doesn't read it, so a future toggle-back to legacy starts
        //    from a coherent state.
        //
        // === COORD TRANSFORM (BUG C FIX, 2026-06-01) =============================
        // SWOS port world is in pitch-pixel coords; OpenSwos Godot world is offset
        // by (PitchOffsetX, PitchOffsetY). Add PitchOffset*<<8 to the raw Q24.8
        // value so _ball.X.ToInt() lands in the same world the legacy renderer
        // already uses. Z is height — no horizontal transform needed.
        int bxRaw = (OpenSwos.SwosVm.BallSprite.X >> 8) + (PitchOffsetX << 8);
        int byRaw = (OpenSwos.SwosVm.BallSprite.Y >> 8) + (PitchOffsetY << 8);
        int bzRaw = OpenSwos.SwosVm.BallSprite.Z >> 8;
        _ball.X = Fixed.FromRaw(bxRaw);
        _ball.Y = Fixed.FromRaw(byRaw);
        _ball.Z = Fixed.FromRaw(bzRaw);
        _ball.VelocityX = Fixed.FromRaw(OpenSwos.SwosVm.BallSprite.DeltaX >> 8);
        _ball.VelocityY = Fixed.FromRaw(OpenSwos.SwosVm.BallSprite.DeltaY >> 8);
        _ball.VelocityZ = Fixed.FromRaw(OpenSwos.SwosVm.BallSprite.DeltaZ >> 8);

        // Pull controlled players from the per-team slots. Team1 (top) maps to
        // _player; team2 (bottom) maps to _opponent. Slots SlotTeam1First (1)
        // and SlotTeam2First (12) — see InitSwosVmFromMatchSetup.
        SyncPlayerStateFromSlot(ref _player,  OpenSwos.SwosVm.PlayerSprite.SlotTeam1First);
        SyncPlayerStateFromSlot(ref _opponent, OpenSwos.SwosVm.PlayerSprite.SlotTeam2First);

        // Keepers — slots 0 and 11. The legacy sprite renderer still draws them,
        // so keep their positions current too.
        SyncPlayerStateFromSlot(ref _homeKeeper, OpenSwos.SwosVm.PlayerSprite.SlotGoalie1);
        SyncPlayerStateFromSlot(ref _awayKeeper, OpenSwos.SwosVm.PlayerSprite.SlotGoalie2);

        // === SWOS-port full-roster sync (BUG B FIX, 2026-06-01) ============
        // Pull all 22 sprite slots into _portPlayerStates so _PhysicsProcess()
        // can paint each one through UpdateSprite(). Same coord transform as
        // the legacy 4 (PitchOffsetX/Y add inside SyncPlayerStateFromSlot).
        for (int s = 0; s < _portPlayerStates.Length; s++)
            SyncPlayerStateFromSlot(ref _portPlayerStates[s], s);

        // 4. Copy port-owned match state (score, clock, goal/playGame flags)
        //    back into _match so the UI overlay (score line + timer + GOAL! /
        //    FULL TIME phases) reflects what the port is computing. We do NOT
        //    increment _match.MatchTick here — the port owns the clock via
        //    gt_gameTimeInMinutes + gt_gameSeconds (gameTime.cpp:60-98). The
        //    legacy SecondsPerHalf-driven MatchTick path remains for the
        //    _useSwosPort=false branch only.
        SyncMatchFromSwosPort();
    }

    // Pulls match-level state (score, clock, goal/playGame/penalty flags) from
    // the SWOS-port Memory image into _match so the existing HUD / overlay
    // path (UpdateUi, OverlayText) renders what the port is computing. Called
    // every tick at the tail of TickSwosPort(), AFTER GameLoop.Tick().
    //
    // Source addresses (all defined in scripts/SwosVm/Memory.cs):
    //   - team1TotalGoals / team2TotalGoals : updateGoals.cpp:7-10 (caps at 99).
    //   - goalScored                        : updateGoals.cpp:39  (per-frame flag).
    //   - playingPenalties                  : updateGoals.cpp:48  (shootout flag).
    //   - playGame                          : gameLoop.cpp:86     (0 = exit loop).
    //   - gt_gameTimeInMinutes              : gameTime.cpp:80     (0..120 game-minutes).
    //   - gt_gameSeconds                    : gameTime.cpp:80     (0..59 seconds-into-minute).
    //
    // Goal detection uses a score-delta against the previous tick rather than
    // the raw goalScored flag because the port doesn't auto-clear goalScored
    // (UpdateGoals.cs sets it to 1; only Memory.Init() resets it). A delta
    // catches the rising edge cleanly even if a future port change leaves
    // goalScored sticky.
    private void SyncMatchFromSwosPort()
    {
        // Score sync. ushort -> byte cap-clamp at 99 matches updateGoals.cpp:14
        // (kMaxGoals). MatchState stores scores as byte, so we cast safely.
        int t1Goals = OpenSwos.SwosVm.Memory.ReadWord(OpenSwos.SwosVm.Memory.Addr.team1TotalGoals);
        int t2Goals = OpenSwos.SwosVm.Memory.ReadWord(OpenSwos.SwosVm.Memory.Addr.team2TotalGoals);
        if (t1Goals > 99) t1Goals = 99;
        if (t2Goals > 99) t2Goals = 99;
        _match.ScorePlayer   = (byte)t1Goals;
        _match.ScoreOpponent = (byte)t2Goals;

        // New goal detected this tick — log the rising edge (phase itself is
        // derived below from the port's own state machine).
        bool newGoal = (t1Goals != _swosPortPrevGoalsTeam1) || (t2Goals != _swosPortPrevGoalsTeam2);
        _swosPortPrevGoalsTeam1 = t1Goals;
        _swosPortPrevGoalsTeam2 = t2Goals;
        if (newGoal)
            GD.Print($"[swos-port] goal -> score now {t1Goals}-{t2Goals}");

        // playingPenalties — shootout flag (updateGoals.cpp:48). We don't yet
        // own a penalty-shootout UI phase, so just track it for OverlayText to
        // append a marker. Falls back to false on the rising edge of a new
        // match because InitSwosVmFromMatchSetup() resets it.
        _swosPortPenaltiesActive =
            OpenSwos.SwosVm.Memory.ReadWord(OpenSwos.SwosVm.Memory.Addr.playingPenalties) != 0;

        // playGame == 0 means the port's outer loop has been told to exit
        // (gameLoop.cpp:86) — typically by cancelGame(). End-of-game is
        // ALSO signalled via gameState == kGameEnded (30), which is what
        // EndOfGame() writes (swos.asm:112122 / swos.h:591). The port's
        // EndOfGame is dispatched from GameTime.UpdateGameTime() when the
        // 90 / 105 / 120-minute period boundary fires (see GetPeriodEndHandler
        // in Sim/Port/GameTime.cs). Catching either signal lets the OpenSWOS
        // overlay flip to MatchPhase.FullTime regardless of which upstream
        // path was taken.
        // === Phase derivation (Session 24) ==================================
        // In port mode MatchPhase is a pure UI MIRROR of the port's state
        // machine — the sim ticks in every phase (see _PhysicsProcess), so
        // this only selects overlay text. Mapping (swos.h:568-595):
        //   playGame==0 / gameState 30,24,26  → FullTime  (kGameEnded →
        //       kPlayersGoingToShower → kResultAfterTheGame chain)
        //   gameState 29,23,25,22             → HalfTime  (kFirstHalfEnded →
        //       kGoingToHalftime → kResultOnHalftime → kCameraGoingToShowers)
        //   goalScored != 0                   → PostGoal  (set by
        //       updateGoals.cpp:39, cleared at bcm2 gameLoop.cpp:1372)
        //   gameStatePl != 100                → PreKickoff (any other
        //       stoppage: 21-hold, walk-to-positions, set pieces, keeper-hold)
        //   else                              → Play
        // NEVER call EnterPreKickoff/EnterPostGoal here — those legacy helpers
        // reset legacy sprite state and EnterPreKickoff arms _swosVmDirty,
        // which would Memory.Init() and zero the score (bug repro'd 2026-06-02).
        int playGame = OpenSwos.SwosVm.Memory.ReadWord(OpenSwos.SwosVm.Memory.Addr.playGame);
        int portGameState = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameState);
        int portGameStatePl = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameStatePl);
        int portGoalScored = OpenSwos.SwosVm.Memory.ReadWord(OpenSwos.SwosVm.Memory.Addr.goalScored);
        bool portGameOver = playGame == 0 || portGameState == 30 || portGameState == 26 || portGameState == 24;
        MatchPhase derived;
        if (portGameOver)
            derived = MatchPhase.FullTime;
        else if (portGameState == 29 || portGameState == 23 || portGameState == 25 || portGameState == 22)
            derived = MatchPhase.HalfTime;
        else if (portGoalScored != 0)
            derived = MatchPhase.PostGoal;
        else if (portGameStatePl != 100)
            derived = MatchPhase.PreKickoff;
        else
            derived = MatchPhase.Play;
        if (_match.Phase != derived)
        {
            GD.Print($"[swos-port] phase {_match.Phase} -> {derived} (gs={portGameState} gsPl={portGameStatePl} goalScored={portGoalScored} playGame={playGame})");
            _match.EnterPhase(derived);
        }

        // Clock sync. The port advances gt_gameTimeInMinutes and gt_gameSeconds
        // inside GameTime.UpdateGameTime() (gameTime.cpp:78-97). MatchTick is a
        // legacy real-tick counter (used by UpdateUi's wall-clock-compression
        // model); under the port we mirror the port's authoritative clock by
        // mapping (minute, second) back into 70 Hz ticks. UpdateUi prefers the
        // port-native readout (see TimerTextForCurrentMatch); MatchTick is kept
        // in sync as a fallback for any consumer not yet ported over.
        uint gtMinutes = OpenSwos.SwosVm.Memory.ReadDword(OpenSwos.SwosVm.Memory.Addr.gt_gameTimeInMinutes);
        int gtSeconds = OpenSwos.SwosVm.Memory.ReadSignedDword(OpenSwos.SwosVm.Memory.Addr.gt_gameSeconds);
        // gameSeconds goes -1 during the last-minute-prolong window
        // (gameTime.cpp:62-77); treat that as 0 for the MatchTick mirror so we
        // never go backwards.
        if (gtSeconds < 0) gtSeconds = 0;
        // Half follows the 45-minute boundary just like the original.
        _match.Half = (gtMinutes < 45) ? (byte)1 : (byte)2;
        // MatchTick mirror: ticks-into-current-half at the PC 70 Hz rate. Used
        // by the legacy UpdateUi timer fallback. Each game-minute is one real
        // minute at the canonical SWOS PC pace, but our `gameLengthInGame=0`
        // setting compresses 45 game-minutes into 30 real-seconds — UpdateUi
        // owns that mapping for the non-port path, so all we need here is a
        // monotonic counter the overlay can use if it falls back.
        int minutesIntoHalf = (int)(gtMinutes - (_match.Half == 1 ? 0u : 45u));
        if (minutesIntoHalf < 0) minutesIntoHalf = 0;
        _match.MatchTick = (minutesIntoHalf * 60 + gtSeconds) * MatchTimings.TicksPerSecond;
    }

    // Converts our (-1/0/+1, -1/0/+1) joystick axis into SWOS's 8-way direction
    // index (kFacingTop=0 .. kFacingTopLeft=7, kNoDirection=-1 for centred).
    private static int AxisToSwosDirection(sbyte dx, sbyte dy)
    {
        return (dx, dy) switch
        {
            ( 0, -1) => OpenSwos.Sim.Port.InputControls.kFacingTop,
            ( 1, -1) => OpenSwos.Sim.Port.InputControls.kFacingTopRight,
            ( 1,  0) => OpenSwos.Sim.Port.InputControls.kFacingRight,
            ( 1,  1) => OpenSwos.Sim.Port.InputControls.kFacingBottomRight,
            ( 0,  1) => OpenSwos.Sim.Port.InputControls.kFacingBottom,
            (-1,  1) => OpenSwos.Sim.Port.InputControls.kFacingBottomLeft,
            (-1,  0) => OpenSwos.Sim.Port.InputControls.kFacingLeft,
            (-1, -1) => OpenSwos.Sim.Port.InputControls.kFacingTopLeft,
            _ => OpenSwos.Sim.Port.InputControls.kNoDirection,
        };
    }

    // Additively bind common joypad buttons onto the built-in ui_* actions so
    // pads whose confirm/cancel/dpad don't reach Godot's defaults still navigate
    // the menu. Idempotent: skips a button already present (safe on re-entry).
    private static void EnsureJoypadUiBindings()
    {
        void AddBtn(string action, JoyButton b)
        {
            if (!InputMap.HasAction(action)) return;
            foreach (var e in InputMap.ActionGetEvents(action))
                if (e is InputEventJoypadButton jb && jb.ButtonIndex == b) return;
            InputMap.ActionAddEvent(action, new InputEventJoypadButton { ButtonIndex = b });
        }
        AddBtn("ui_accept", JoyButton.A);
        AddBtn("ui_accept", JoyButton.Start);
        AddBtn("ui_cancel", JoyButton.B);
        AddBtn("ui_up", JoyButton.DpadUp);
        AddBtn("ui_down", JoyButton.DpadDown);
        AddBtn("ui_left", JoyButton.DpadLeft);
        AddBtn("ui_right", JoyButton.DpadRight);
    }

    // === Android expand presentation: black side bars + centring (#231) =======
    // See the field block near the top for the why. This is the ONLY place the
    // expand geometry is computed; everything else reads _barW / _vpW.

    // Two opaque black ColorRects on their own CanvasLayer at Layer 3 — above the
    // world (0) / HUD (1) / menu fine-print (2), below the touch overlay (4). They
    // mask the extra side space that Expand exposes, so the pitch/void bleeding
    // past the 1152-wide content region is never visible.
    private void BuildSideBars()
    {
        if (DisplayServer.GetName() == "headless") return;
        var bars = new CanvasLayer { Layer = 3 };   // identity transform: screen space
        AddChild(bars);
        _barsLayer = bars;
        // MouseFilter=Ignore is CRITICAL: a ColorRect defaults to Stop, and with
        // emulate_mouse_from_touch the bars would EAT every touch that lands on
        // them — and the d-pad, FIRE/CONFIRM and the toggle icon all sit ON the
        // bars. With Stop the overlay was dead on device (the "+" toggle did
        // nothing). Ignore lets the touch reach Main._UnhandledInput.
        _barLeft = new ColorRect { Color = Colors.Black, Position = Vector2.Zero, Size = Vector2.Zero, MouseFilter = Control.MouseFilterEnum.Ignore };
        _barRight = new ColorRect { Color = Colors.Black, Position = Vector2.Zero, Size = Vector2.Zero, MouseFilter = Control.MouseFilterEnum.Ignore };
        bars.AddChild(_barLeft);
        bars.AddChild(_barRight);
    }

    // Recomputes the bar width and re-applies the centring offset. Called once at
    // boot and on every root-viewport resize (orientation change / window resize).
    private void ApplyExpandLayout()
    {
        if (!_expandMode) return;
        var root = GetTree()?.Root;
        if (root is null) return;
        var vis = root.GetVisibleRect().Size;
        if (vis.X <= 0f || vis.Y <= 0f) return;

        const float contentW = ViewportWidth * RenderScale;   // 1152
        _vpW = vis.X;
        float extra = _vpW - contentW;
        // Quantise the bar to a whole multiple of RenderScale. This is what makes
        // the centre region PIXEL-IDENTICAL to Keep mode: the camera-limit widening
        // below is then _barW / RenderScale EXACTLY (no rounding), so the camera
        // clamps at precisely the same world coordinate it does in Keep mode. Any
        // 1-2 px remainder is absorbed by the right bar's width, which is computed
        // from the remainder rather than mirrored from the left.
        _barW = extra > 0f ? Mathf.Floor(extra / 2f / RenderScale) * RenderScale : 0f;
        _barsHostControls = _barW >= BarMinWidth;

        // (a) MATCH WORLD — camera limits only. The camera is DragCenter, so its
        // view is already centred on the (now wider) viewport; the 1152-wide centre
        // slice therefore shows the same world point Keep mode would, EXCEPT where
        // the limits clamp near a pitch edge. Widening each limit by exactly the
        // extra half-width restores Keep-mode clamping, so the centre slice matches
        // everywhere. Nothing else is touched: the sim never reads the camera, and
        // the ported camera-focus math (_cameraAnchor.Position, swos-port
        // pitch.cpp clipPitch) writes POSITION, not limits — the two are
        // independent, so this cannot perturb the port. The extra pitch that
        // becomes visible past the old limits is exactly what the bars mask.
        if (_camera is not null)
        {
            int worldExtra = (int)(_barW / RenderScale);   // exact: _barW is a multiple of RenderScale
            _camera.LimitLeft  = PitchOffsetX - worldExtra;
            _camera.LimitRight = PitchOffsetX + PitchWidth + worldExtra;
        }

        // (b) SCREEN-SPACE LAYERS — shift right by _barW so the 1152-wide content
        // sits centred instead of anchored top-left. Scale is re-stated (not
        // multiplied in) so repeated resizes can't accumulate.
        if (_uiLayer is not null)
            _uiLayer.Transform = new Transform2D(0f, new Vector2(MenuScale, MenuScale), 0f, new Vector2(_barW, 0f));
        if (_hudLayer is not null)
            _hudLayer.Transform = new Transform2D(0f, new Vector2(RenderScale, RenderScale), 0f, new Vector2(_barW, 0f));
        _menuClient?.SetPresentationOffsetX(_barW);
        // _menuBgImage lives on the ui layer, so it inherits the _barW centring
        // via _uiLayer.Transform above — no separate offset needed.

        // (c) BARS — left [0.._barW], right [_barW+contentW.._vpW]. Full viewport
        // height. Right width comes from the remainder so odd extra is still
        // covered (no 1 px seam of pitch at the screen edge).
        if (_barLeft is not null)
        {
            _barLeft.Position = Vector2.Zero;
            _barLeft.Size = new Vector2(_barW, vis.Y);
            _barLeft.Visible = _barW > 0f;
        }
        if (_barRight is not null)
        {
            float rightX = _barW + contentW;
            _barRight.Position = new Vector2(rightX, 0f);
            _barRight.Size = new Vector2(Mathf.Max(0f, _vpW - rightX), vis.Y);
            _barRight.Visible = _vpW - rightX > 0f;
        }

        if (_touchUi) LayoutTouchOverlay();
        GD.Print($"[expand] vp={_vpW}x{vis.Y} barW={_barW} barsHostControls={_barsHostControls}");
    }

    // Places the overlay for the current bar geometry. Two cases:
    //   bars wide enough  -> stick centred in the LEFT bar, FIRE centred in the
    //                        RIGHT bar at ~2/3 height, pause top-right bar, toggle
    //                        top-left bar. Controls sit entirely off the game image.
    //   bars too thin     -> original on-content positions, shifted by _barW so they
    //                        stay on the (now centred) content.
    // Touch capture zones are the whole respective bar PLUS a ~1/6-of-content grace
    // strip of the adjacent game area, so a thumb landing just inside the image
    // still registers.
    private void LayoutTouchOverlay()
    {
        if (_touchOverlay is null) return;
        const float contentW = ViewportWidth * RenderScale;    // 1152
        const float contentH = ViewportHeight * RenderScale;   // 816
        float grace = contentW / 6f;                           // 192
        float rightBarX = _barW + contentW;
        float rightBarW = Mathf.Max(0f, _vpW - rightBarX);

        if (_barsHostControls)
        {
            _tStickCenter  = new Vector2(_barW / 2f, contentH / 2f);
            _tFireCenter   = new Vector2(rightBarX + rightBarW / 2f, contentH * 2f / 3f);
            _tPauseCenter  = new Vector2(rightBarX + rightBarW / 2f, 40f);
            _tToggleCenter = new Vector2(_barW / 2f, 40f);
            // Generous zones: whole bar + grace strip into the game area.
            _tStickZoneMaxX = _barW + grace;
            _tFireZoneMinX  = rightBarX - grace;
            _tPauseHalf = 34f;
            _tToggleHalf = 34f;
        }
        else
        {
            // Fallback: the original 1152×816 layout, shifted onto the centred content.
            _tStickCenter  = new Vector2(190f, 626f) + new Vector2(_barW, 0f);
            _tFireCenter   = new Vector2(1042f, 706f) + new Vector2(_barW, 0f);
            _tPauseCenter  = new Vector2(1102f, 40f) + new Vector2(_barW, 0f);
            _tToggleCenter = new Vector2(50f, 40f) + new Vector2(_barW, 0f);
            _tStickZoneMaxX = _barW + contentW / 2f;
            _tFireZoneMinX  = float.MaxValue;    // circle-only hit test, as on desktop
            _tPauseHalf = 24f;
            _tToggleHalf = 24f;
        }

        _stickAnchor = _tStickCenter;
        if (_tStickBase is not null) _tStickBase.Position = _tStickCenter;
        if (_tStickKnob is not null) _tStickKnob.Position = _tStickCenter;
        if (_tFire is not null) _tFire.Position = _tFireCenter;
        if (_tPause is not null) _tPause.Position = _tPauseCenter;
        if (_tToggle is not null) _tToggle.Position = _tToggleCenter;
    }

    // === Android on-screen touch overlay ======================================
    // A single screen-space CanvasLayer (identity transform → hit-tests are in the
    // 1152×816 root viewport space) holding a translucent left-thumb d-pad, a
    // bottom-right fire circle and a top-right pause icon. Built ONCE from cached
    // circle textures; visibility is toggled per-frame (only during Match).

    // Renders a filled translucent circle with a ring into an ImageTexture ONCE.
    private static ImageTexture MakeCircleTex(int radius, Color fill, Color ring)
    {
        int d = radius * 2;
        var img = Image.CreateEmpty(d, d, false, Image.Format.Rgba8);
        img.Fill(new Color(0, 0, 0, 0));
        float cx = radius - 0.5f, cy = radius - 0.5f;
        float rOuter = radius;
        float rInner = radius - Mathf.Max(3f, radius * 0.12f);   // ring thickness
        for (int y = 0; y < d; y++)
        for (int x = 0; x < d; x++)
        {
            float dx = x - cx, dy = y - cy;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            if (dist > rOuter) continue;
            img.SetPixel(x, y, dist >= rInner ? ring : fill);
        }
        return ImageTexture.CreateFromImage(img);
    }

    private Sprite2D AddOverlaySprite(Texture2D tex, Vector2 center, float alpha)
    {
        var s = new Sprite2D
        {
            Texture = tex,
            Position = center,
            Modulate = new Color(1, 1, 1, alpha),
            TextureFilter = TextureFilterEnum.Nearest,
            Visible = false,
        };
        _touchOverlay!.AddChild(s);
        return s;
    }

    private void BuildTouchOverlay()
    {
        if (DisplayServer.GetName() == "headless") return;
        // Layer 4: above the world (0), HUD (1), menu fine-print (2) and the
        // Android black side bars (3) — the controls must draw ON the bars.
        var ov = new CanvasLayer { Layer = 4 };   // identity transform: screen space
        AddChild(ov);
        _touchOverlay = ov;

        // Left thumb d-pad: 150px-diameter ring bottom-left, 64px knob on top.
        _tStickCenter = new Vector2(190, 626);
        _stickAnchor = _tStickCenter;
        var baseTex = MakeCircleTex(75, new Color(1, 1, 1, 0.12f), new Color(1, 1, 1, 0.9f));
        var knobTex = MakeCircleTex(32, new Color(1, 1, 1, 0.9f), new Color(1, 1, 1, 0.9f));
        _tStickBase = AddOverlaySprite(baseTex, _tStickCenter, 0.4f);
        _tStickKnob = AddOverlaySprite(knobTex, _tStickCenter, 0.5f);

        // Fire circle bottom-right (radius 64).
        _tFireCenter = new Vector2(1042, 706);
        _tFireRadius = 64f;
        var fireTex = MakeCircleTex(64, new Color(1f, 0.55f, 0.25f, 0.35f), new Color(1f, 0.75f, 0.4f, 0.9f));
        _tFire = AddOverlaySprite(fireTex, _tFireCenter, 0.5f);

        // Top-right (~40px square): PAUSE glyph in a match, BACK '<' glyph in a
        // menu. Texture is swapped per-state in UpdateTouchOverlayVisibility.
        _tPauseCenter = new Vector2(1102, 40);
        _tPauseHalf = 24f;
        _pauseTex = MakePauseTex();
        _backTex = MakeBackTex();
        _tPause = AddOverlaySprite(_pauseTex, _tPauseCenter, 0.5f);

        // Overlay ON/OFF toggle — small icon top-LEFT (clear of the pause icon at
        // top-right and the d-pad at bottom-left). ALWAYS visible in match; tapping
        // it hides/shows the d-pad + fire + pause so a Bluetooth-pad user can play
        // with a clean screen. Persisted in settings.json ("touchOverlay").
        _tToggleCenter = new Vector2(50, 40);
        _tToggleHalf = 24f;
        _tToggle = AddOverlaySprite(MakeToggleTex(), _tToggleCenter, 0.55f);
    }

    // Small d-pad glyph in a rounded translucent square — the overlay ON/OFF
    // toggle icon, rendered ONCE.
    private static ImageTexture MakeToggleTex()
    {
        int d = 48;
        var img = Image.CreateEmpty(d, d, false, Image.Format.Rgba8);
        img.Fill(new Color(1, 1, 1, 0.15f));
        var arm = new Color(1, 1, 1, 0.95f);
        // A plus/d-pad cross: vertical bar + horizontal bar.
        for (int y = 12; y < d - 12; y++)
            for (int x = 21; x < 27; x++) img.SetPixel(x, y, arm);
        for (int x = 12; x < d - 12; x++)
            for (int y = 21; y < 27; y++) img.SetPixel(x, y, arm);
        return ImageTexture.CreateFromImage(img);
    }

    // '<' chevron glyph in a rounded translucent square — the BACK button shown
    // in the top-right overlay slot while in a menu. Rendered ONCE.
    private static ImageTexture MakeBackTex()
    {
        int d = 48;
        var img = Image.CreateEmpty(d, d, false, Image.Format.Rgba8);
        img.Fill(new Color(1, 1, 1, 0.15f));
        var arm = new Color(1, 1, 1, 0.95f);
        // Two diagonal arms meeting at the left → a '<' pointing back.
        for (int i = 0; i <= 12; i++)
        {
            int x = 18 + i;              // 18..30
            for (int t = 0; t < 4; t++)  // 4px thick
            {
                int yUp = 24 - i + t;
                int yDn = 24 + i - t;
                if (yUp >= 0 && yUp < d) img.SetPixel(x, yUp, arm);
                if (yDn >= 0 && yDn < d) img.SetPixel(x, yDn, arm);
            }
        }
        return ImageTexture.CreateFromImage(img);
    }

    // Two-bar pause glyph in a rounded translucent square, rendered ONCE.
    private static ImageTexture MakePauseTex()
    {
        int d = 48;
        var img = Image.CreateEmpty(d, d, false, Image.Format.Rgba8);
        img.Fill(new Color(1, 1, 1, 0.15f));
        var bar = new Color(1, 1, 1, 0.95f);
        for (int y = 12; y < d - 12; y++)
        {
            for (int x = 14; x < 20; x++) img.SetPixel(x, y, bar);
            for (int x = 28; x < 34; x++) img.SetPixel(x, y, bar);
        }
        return ImageTexture.CreateFromImage(img);
    }

    // Per-frame overlay visibility. The SAME d-pad + right-hand action button +
    // top-right slot + toggle serve BOTH a match and a menu:
    //   match — stick = move, FIRE circle = pass/shot, top-right = PAUSE.
    //   menu  — stick = navigate, FIRE circle = CONFIRM (ui_accept), top-right =
    //           BACK '<' (ui_cancel).
    private void UpdateTouchOverlayVisibility()
    {
        if (_touchOverlay is null) return;
        bool inMatch = _touchUi && _appState == AppState.Match;
        bool inMenu  = _touchUi && _appState == AppState.Menu;
        bool overlayCtx = inMatch || inMenu;
        bool controls = overlayCtx && _touchOverlayOn;
        if (_tStickBase is not null) _tStickBase.Visible = controls;
        if (_tStickKnob is not null) _tStickKnob.Visible = controls;
        if (_tFire is not null) _tFire.Visible = controls;
        if (_tPause is not null)
        {
            _tPause.Visible = controls;
            // BACK '<' in menus, PAUSE bars in a match.
            var want = inMenu ? _backTex : _pauseTex;
            if (want is not null && _tPause.Texture != want) _tPause.Texture = want;
        }
        // Toggle icon is always available whenever the overlay context is live;
        // dim it when the controls are hidden so its state reads at a glance.
        if (_tToggle is not null)
        {
            _tToggle.Visible = overlayCtx;
            _tToggle.Modulate = new Color(1, 1, 1, _touchOverlayOn ? 0.55f : 0.30f);
        }
    }

    // Multi-finger match touch handling. `pos` is raw screen space (1152×816);
    // hit-tests compare against the overlay sprite centres directly.
    private void HandleMatchTouch(Vector2 pos, bool pressed, int index)
    {
        if (pressed)
        {
            // Overlay ON/OFF toggle (top-left) — always live, even when the rest of
            // the controls are hidden. Flips instantly + persists the choice.
            if (Mathf.Abs(pos.X - _tToggleCenter.X) <= _tToggleHalf &&
                Mathf.Abs(pos.Y - _tToggleCenter.Y) <= _tToggleHalf)
            {
                _touchOverlayOn = !_touchOverlayOn;
                if (!_touchOverlayOn)   // clear any in-flight touch input
                {
                    _touchDx = 0; _touchDy = 0; _touchFire = false;
                    _stickFinger = -1; _fireFinger = -1;
                }
                UpdateTouchOverlayVisibility();
                SaveSettings();
                return;
            }
            // The remaining zones only exist while the controls are shown.
            if (!_touchOverlayOn) return;
            // Pause icon (top-right square).
            if (Mathf.Abs(pos.X - _tPauseCenter.X) <= _tPauseHalf &&
                Mathf.Abs(pos.Y - _tPauseCenter.Y) <= _tPauseHalf)
            {
                _touchPauseRequested = true;
                return;
            }
            // Fire: the drawn circle, or (Android bars path) anywhere in the right
            // bar + its grace strip. _tFireZoneMinX is float.MaxValue elsewhere, so
            // desktop/--touch keeps the circle-only test.
            if (pos.DistanceTo(_tFireCenter) <= _tFireRadius || pos.X >= _tFireZoneMinX)
            {
                _fireFinger = index;
                _touchFire = true;
                return;
            }
            // Stick zone (left half, or the left bar + grace strip) → thumb d-pad;
            // anchor where the finger lands.
            if (pos.X < _tStickZoneMaxX)
            {
                _stickFinger = index;
                _stickAnchor = pos;
                _touchDx = 0;
                _touchDy = 0;
                if (_tStickKnob is not null) _tStickKnob.Position = pos;
            }
        }
        else
        {
            if (index == _fireFinger) { _fireFinger = -1; _touchFire = false; }
            if (index == _stickFinger)
            {
                _stickFinger = -1;
                _touchDx = 0;
                _touchDy = 0;
                if (_tStickKnob is not null) _tStickKnob.Position = _tStickCenter;
            }
        }
    }

    // Root-viewport touch position -> menu 576×408 design space. _barW is 0 on every
    // non-expand platform, so this stays exactly `pos / MenuScale` there; on Android
    // it undoes the centring shift applied to the menu CanvasLayer.
    private Vector2 MenuTouchPos(Vector2 pos) => (pos - new Vector2(_barW, 0f)) / MenuScale;

    private void HandleMatchDrag(Vector2 pos, int index)
    {
        if (index != _stickFinger) return;
        Vector2 v = pos - _stickAnchor;
        if (v.Length() < 28f)   // deadzone
        {
            _touchDx = 0;
            _touchDy = 0;
            if (_tStickKnob is not null) _tStickKnob.Position = _stickAnchor;
            return;
        }
        (sbyte dx, sbyte dy) = EightWay(v);
        _touchDx = dx;
        _touchDy = dy;
        // Visual: clamp the knob to the ring radius along the drag vector.
        if (_tStickKnob is not null)
        {
            float len = v.Length();
            Vector2 clamped = len > 75f ? _stickAnchor + v * (75f / len) : pos;
            _tStickKnob.Position = clamped;
        }
    }

    // Quantises a stick vector to an 8-way (dx,dy) in {-1,0,1}. SCREEN-space
    // angle, y is DOWN. Shared by the match d-pad and the menu d-pad.
    private static (sbyte dx, sbyte dy) EightWay(Vector2 v)
    {
        double a = Mathf.Atan2(v.Y, v.X);
        int oct = ((int)Mathf.Round(a / (Mathf.Pi / 4)) + 8) & 7; // 0=E,1=SE,2=S,3=SW,4=W,5=NW,6=N,7=NE
        return oct switch
        {
            0 => ((sbyte)1, (sbyte)0),
            1 => ((sbyte)1, (sbyte)1),
            2 => ((sbyte)0, (sbyte)1),
            3 => ((sbyte)-1, (sbyte)1),
            4 => ((sbyte)-1, (sbyte)0),
            5 => ((sbyte)-1, (sbyte)-1),
            6 => ((sbyte)0, (sbyte)-1),
            _ => ((sbyte)1, (sbyte)-1),
        };
    }

    // === MENU on-screen overlay touch =========================================
    // The overlay lives on top of the SWOS front-end while AppState==Menu. This
    // handles taps/drags on the overlay CONTROLS (toggle / BACK / CONFIRM /
    // d-pad) in raw 1152×816 screen space. Returns true when a control consumed
    // the touch; false lets the tap fall through to MenuClient's tap-to-select,
    // so the two input styles coexist exactly as required.
    private bool HandleMenuTouch(Vector2 pos, bool pressed, int index)
    {
        if (!pressed)
        {
            if (index == _menuStickFinger)
            {
                _menuStickFinger = -1;
                _menuStickDx = 0; _menuStickDy = 0;
                if (_tStickKnob is not null) _tStickKnob.Position = _tStickCenter;
                return true;
            }
            return false;
        }
        // Overlay ON/OFF toggle (top-left) — always live, even when the rest of
        // the controls are hidden. Flips instantly + persists the choice.
        if (Mathf.Abs(pos.X - _tToggleCenter.X) <= _tToggleHalf &&
            Mathf.Abs(pos.Y - _tToggleCenter.Y) <= _tToggleHalf)
        {
            _touchOverlayOn = !_touchOverlayOn;
            if (!_touchOverlayOn)
            {
                _menuStickDx = 0; _menuStickDy = 0; _menuStickFinger = -1;
                _navHeld = false; _navRepeatTimer = 0;
            }
            UpdateTouchOverlayVisibility();
            SaveSettings();
            return true;
        }
        // Remaining controls exist only while the overlay is shown; when hidden
        // let taps through to the menu content (and the small '<' box returns).
        if (!_touchOverlayOn) return false;
        // BACK (top-right slot) → ui_cancel.
        if (Mathf.Abs(pos.X - _tPauseCenter.X) <= _tPauseHalf &&
            Mathf.Abs(pos.Y - _tPauseCenter.Y) <= _tPauseHalf)
        {
            _menuBackPulse = true;
            return true;
        }
        // CONFIRM (the drawn circle, or the right bar + grace strip) → ui_accept.
        if (pos.DistanceTo(_tFireCenter) <= _tFireRadius || pos.X >= _tFireZoneMinX)
        {
            _menuConfirmPulse = true;
            return true;
        }
        // d-pad zone (left bar / left half) → anchor where the finger lands.
        if (pos.X < _tStickZoneMaxX)
        {
            _menuStickFinger = index;
            _stickAnchor = pos;
            _menuStickDx = 0; _menuStickDy = 0;
            if (_tStickKnob is not null) _tStickKnob.Position = pos;
            return true;
        }
        return false;   // outside every control → a menu-content tap
    }

    // Drag on the menu d-pad → updates the 8-way _menuStick* state (NEVER the
    // match _touchDx/_touchDy). Mirrors HandleMatchDrag's visuals.
    private void HandleMenuDrag(Vector2 pos, int index)
    {
        if (index != _menuStickFinger) return;
        Vector2 v = pos - _stickAnchor;
        if (v.Length() < 28f)   // deadzone
        {
            _menuStickDx = 0; _menuStickDy = 0;
            if (_tStickKnob is not null) _tStickKnob.Position = _stickAnchor;
            return;
        }
        (sbyte dx, sbyte dy) = EightWay(v);
        _menuStickDx = dx;
        _menuStickDy = dy;
        if (_tStickKnob is not null)
        {
            float len = v.Length();
            Vector2 clamped = len > 75f ? _stickAnchor + v * (75f / len) : pos;
            _tStickKnob.Position = clamped;
        }
    }

    // Translates the menu d-pad's 8-way state into discrete ui_up/down/left/right
    // "just pressed" pulses with auto-repeat (initial delay then steady repeat),
    // plus one-shot CONFIRM/BACK from the buttons, and feeds them to MenuClient.
    // Runs once per menu physics frame, immediately before MenuClient.Tick().
    //
    // DETERMINISM / SIM SAFETY: nothing here writes _touchDx/_touchDy or Godot's
    // global Input state. The pulses are injected ONLY into MenuClient's own
    // action reads via InjectNav(); the sim never observes them, and it runs
    // only while AppState==Match (this runs only while AppState==Menu).
    private void UpdateMenuTouchNav(double delta)
    {
        if (_menuClient is null) return;
        // Small '<' box is the fallback back-control only when the big overlay
        // BACK is hidden (overlay toggled off) — never both at once.
        _menuClient.ShowBackBox = !_touchOverlayOn;

        bool up = false, down = false, left = false, right = false;
        sbyte dx = _touchOverlayOn ? _menuStickDx : (sbyte)0;
        sbyte dy = _touchOverlayOn ? _menuStickDy : (sbyte)0;
        bool active = dx != 0 || dy != 0;

        void Emit()
        {
            if (dy < 0) up = true; else if (dy > 0) down = true;
            if (dx < 0) left = true; else if (dx > 0) right = true;
        }

        if (!active) { _navHeld = false; _navRepeatTimer = 0; }
        else if (!_navHeld || dx != _navHeldDx || dy != _navHeldDy)
        {
            _navHeld = true; _navHeldDx = dx; _navHeldDy = dy;
            _navRepeatTimer = NavInitialDelay;
            Emit();   // immediate move on the crossing into a new direction
        }
        else
        {
            _navRepeatTimer -= delta;
            if (_navRepeatTimer <= 0) { _navRepeatTimer = NavRepeatInterval; Emit(); }
        }

        bool accept = _menuConfirmPulse; _menuConfirmPulse = false;
        bool cancel = _menuBackPulse;    _menuBackPulse = false;
        _menuClient.InjectNav(up, down, left, right, accept, cancel);
    }

    // Pulls X/Y/Z/Direction from a sprite slot into the C# PlayerState so the
    // existing renderer paints it. SwosVm coords are Q16.16 pixels — divide raw
    // by 65536 to get whole pixels.
    //
    // === COORD TRANSFORM (BUG C FIX, 2026-06-01) =================================
    // SWOS port stores world positions in **pitch-pixel coords**: (0, 0) is the
    // top-left of the pitch bitmap (672×848); centre spot is (336, 449); each
    // goal-line is at Y≈98 / Y≈798. The OpenSwos Godot world places the same
    // pitch image at (PitchOffsetX=-160, PitchOffsetY=-288), so the legacy sim
    // uses (0, 0) ≈ centre-of-pitch. To paint a SWOS-port sprite at the right
    // place in the Godot world we add the pitch offset.
    //
    //   worldX = swosX + PitchOffsetX  → SWOS centre (336) → world 176
    //   worldY = swosY + PitchOffsetY  → SWOS centre (449) → world 161
    //
    // This integer add carries no fractional bits so we still satisfy the
    // "no float math" rule.
    private static void SyncPlayerStateFromSlot(ref PlayerState p, int slot)
    {
        int xPix = (OpenSwos.SwosVm.PlayerSprite.X(slot) >> 16) + PitchOffsetX;
        int yPix = (OpenSwos.SwosVm.PlayerSprite.Y(slot) >> 16) + PitchOffsetY;
        p.X = Fixed.FromInt(xPix);
        p.Y = Fixed.FromInt(yPix);
        // Direction: SWOS 0..7 (kFacingTop=0 → North in our enum). Map both
        // 0..7 sequences are the same ordering (N, NE, E, SE, S, SW, W, NW) so
        // the cast is direct.
        int dir = OpenSwos.SwosVm.PlayerSprite.Direction(slot);
        if (dir >= 0 && dir <= 7) p.Facing = (Direction)dir;
    }

    // Returns the per-frame anchor offset applied — caller uses it to offset-compensate
    // any child node (e.g. the camera, which would otherwise inherit the wobble).
    //
    // PlayerState.X/Y represents the FOOT world position (matching SWOS sprite.x which
    // is "top-left then offset by cx/cy = foot/grass-contact pixel"). To draw the
    // 16×16 sprite tile so that the foot pixel (cx, cy) ends up at (player.X, player.Y),
    // sprite tile centre is offset by (8-cx, 8-cy). On top of that we apply the per-
    // frame anchor delta (current cell.cx - standing.cx etc.) which keeps the foot
    // stable across run-cycle frames.
    //
    // Equivalence with the original (verified 2026-07-02): the resulting tile
    // TOP-LEFT is (X - (sCx+ox), Y - (sCy+oy)) = (X - frame.cx, Y - frame.cy),
    // which is exactly drawSprite's placement of the trimmed frame —
    // external/swos-port/src/sprites/gameSprites.cpp:210-214 +
    // renderSprites.cpp:44-52 (top-left = x - centerX, y - z - centerY). Our Amiga
    // atlas cells store their content flush at the cell's top-left (measured: all
    // 24 stand/run cells of CJCTEAM1.RAW have content starting at cell (0,0)), so
    // applying the PC trimmed-frame anchors to the 16×16 cell is pixel-identical.
    // NOTE the pitch bitmap itself sits at world (0, PitchBitmapWorldY) — see the
    // constant — which is what anchors these world positions to the pitch art.
    //
    // `portSlot` (-1 to opt out, default) selects the SWOS-port data path.
    // When non-negative it indexes a PlayerSprite slot in Memory; we read
    // animTablePtr + frameIndicesTable + frameIndex to compute the sprite
    // ordinal exactly the way SWOS does (player.cpp:3517-3547), then map the
    // ordinal back to our 16x16 atlas (col, row). When portSlot < 0 OR the
    // slot has a null animTablePtr (i.e. SetPlayerAnimationTable hasn't run
    // yet) we silently fall back to the legacy PlayerSim.AnimationPhase
    // lookup so the toggle stays non-destructive.
    private static (int ox, int oy) UpdateSprite(Sprite2D? sprite, in PlayerState p,
        System.Collections.Generic.Dictionary<(int, int), ImageTexture> tiles, int portSlot = -1)
    {
        if (sprite is null) return (0, 0);
        (int col, int row) cell = default;
        bool resolved = false;
        if (portSlot >= 0)
        {
            // SWOS-port path. Mirrors player.cpp:3517-3547:
            //   D0 = *(word *)(frameIndicesTable + frameIndex * 2);
            //   if (D0 < 0)  keep current imageIndex;
            //   imageIndex = D0 + sprite.frameOffset;
            // frameIndex starts at -1 (SetPlayerAnimationTable sentinel);
            // SWOS's updateSpriteAnimation cycles 0..N-1 as frameDelay ticks
            // elapse. Our port hasn't wired that walker yet (Referee.cs:484
            // stub) so frameIndex stays -1 unless something else writes it -
            // when negative we read frame 0 as the "static" pose, which is
            // what SetPlayerAnimationTable would flip to anyway.
            int slotBase = OpenSwos.SwosVm.PlayerSprite.Base(portSlot);
            int animTablePtr = OpenSwos.SwosVm.Memory.ReadSignedDword(
                slotBase + OpenSwos.SwosVm.PlayerSprite.OffAnimTablePtr);
            int frameIndicesPtr = OpenSwos.SwosVm.Memory.ReadSignedDword(
                slotBase + OpenSwos.SwosVm.PlayerSprite.OffFrameIndicesTable);
            if (animTablePtr != 0 && frameIndicesPtr > 0 && frameIndicesPtr < 0x60000)
            {
                short frameIdx = OpenSwos.SwosVm.Memory.ReadSignedWord(
                    slotBase + OpenSwos.SwosVm.PlayerSprite.OffFrameIndex);
                if (frameIdx < 0) frameIdx = 0;
                short rawImg = OpenSwos.SwosVm.Memory.ReadSignedWord(frameIndicesPtr + (frameIdx * 2));
                int imageIndex;
                if (rawImg >= 0)
                {
                    short frameOff = OpenSwos.SwosVm.Memory.ReadSignedWord(
                        slotBase + OpenSwos.SwosVm.PlayerSprite.OffFrameOffset);
                    imageIndex = rawImg + frameOff;
                }
                else
                {
                    // Negative entries are animation opcodes (-3..-50 pause,
                    // -90 goto, -101/-104/-999 terminators). The per-tick
                    // walker (SpriteUpdate.SetNextPlayerFrame, swos.asm:
                    // 102834-102966) already resolved the current picture —
                    // frameOffset included — into sprite.imageIndex, so read
                    // that instead of falling straight to legacy. Matters on
                    // the first tick of a freshly-installed stream whose
                    // element 0 is a pause opcode (all dive/throw-in streams).
                    imageIndex = OpenSwos.SwosVm.Memory.ReadSignedWord(
                        slotBase + OpenSwos.SwosVm.PlayerSprite.OffImageIndex);
                }
                if (imageIndex >= 0)
                {
                    // The VM has already resolved the exact sprite ordinal for
                    // this tick (writhe frames included — SpriteUpdate.
                    // SetNextPlayerFrame flip-flops the pair with the axis
                    // frozen), so a plain ordinal→tile lookup is all we need.
                    var portCell = SwosOrdinalToAtlasCell(imageIndex);
                    if (portCell.HasValue)
                    {
                        cell = portCell.Value;
                        resolved = true;
                    }
                }
            }
        }
        if (!resolved)
        {
            // Legacy hand-built path. Unchanged when portSlot < 0 or the port
            // hasn't yet wired enough state to resolve a sprite ordinal.
            int phase = PlayerSim.AnimationPhase(in p);
            // Keepers override the normal animation cycle based on goalie state.
            // Until we wire CJCTEAMG (Phase C proper), we re-use the outfielder
            // atlas pose frames as placeholders so the visual at least
            // communicates the state.
            cell = p.GoalieState switch
            {
                KeeperState.DivingHigh or KeeperState.DivingLow => PlayerFrames.Slide(p.Facing),
                KeeperState.CatchingBall => PlayerFrames.Fallen(p.Facing),
                // Claimed = keeper holds ball upright (swos-port keeps him
                // standing - PlayerNormalStandingAnimTable at updatePlayers.cpp:3056).
                KeeperState.Claimed => PlayerFrames.Standing(p.Facing),
                _ => phase switch
                {
                    -2 => PlayerFrames.Slide(p.Facing),
                    -1 => PlayerFrames.Standing(p.Facing),
                    _ => PlayerFrames.RunFrame(p.Facing, phase),
                },
            };
        }
        if (tiles.TryGetValue(cell, out var tex))
            sprite.Texture = tex;
        var (sCx, sCy) = PlayerFrames.StandingAnchor(p.Facing);
        var (ox, oy) = PlayerFrames.AnchorOffset(cell.col, cell.row);
        // Extended frames (goalie dive/catch 16×20, throw-in/cheer 16×24) carry
        // authentic per-frame anchors (bug #162). Rebase (ox,oy) so the raw
        // formula below (which subtracts sCx/sCy + ox/oy from the world position)
        // lands the frame's authentic anchor (ExtAnchor) on (player.X, player.Y):
        //   tile_topleft = (X - sCx - ox, Y - sCy - oy)  [ext-centre block cancels H]
        //   want tile_topleft = (X - ax, Y - ay)  ⇒  ox = ax - sCx, oy = ay - sCy.
        // This also keeps the camera shim (which reuses ox/oy) correct.
        if (PlayerFrames.IsExtCell(cell))
        {
            var (ax, ay) = PlayerFrames.ExtAnchor(cell);
            ox = ax - sCx;
            oy = ay - sCy;
        }
        // Sprite tile centre offset so that the FRAME's foot anchor (standing + delta)
        // lands at (player.X, player.Y).
        int spriteX = p.X.ToInt() + (8 - sCx) - ox;
        int spriteY = p.Y.ToInt() + (8 - sCy) - oy;
        // Extended frames (throw-in 16×24, goalie dive/catch 16×20) are taller
        // than the 16×16 cell the formula above assumes. The sprite node is
        // Centered, so shift by half the size difference to keep the frame's
        // TOP-LEFT where a 16×16 tile's would be (content grows downward from
        // the same origin; AnchorOffset already lifted it by the foot delta).
        if (PlayerFrames.IsExtCell(cell) && sprite.Texture is not null)
        {
            spriteX += (sprite.Texture.GetWidth() - 16) / 2;
            spriteY += (sprite.Texture.GetHeight() - 16) / 2;
        }
        sprite.Position = new Vector2(spriteX, spriteY);
        // Z-order by Y with a +1000 base so even sprites at the north end (Y=-200)
        // stay above the pitch (ZIndex=-10). Without the offset, north-bound players
        // disappear behind the pitch graphic.
        sprite.ZIndex = p.Y.ToInt() + 1000;
        // Camera compensation uses the COMBINED sprite-offset so the screen doesn't
        // wobble per-frame (run-cycle deltas) nor per-direction (foot anchor varies).
        // The caller (camera) only counters the delta — direction-base offset is the
        // same for adjacent ticks so it's a non-issue for "wobble". But we still return
        // (ox, oy) for the camera shim.
        return (ox, oy);
    }

    // Maps a SWOS sprite ordinal (the `imageIndex` field on a player sprite)
    // to the (col, row) of our 16x16 CJCTEAM atlas. Returns null for ordinals
    // outside the ranges PlayerFrames currently caches - caller falls back to
    // the legacy lookup so we don't render a missing-texture marker.
    //
    // Bases (from external/swos-port/swos/swos.asm:218368+ via AnimationTablesData):
    //   team1 outfield : 341..364 + tackles 395..402 + tackled/throw-in blocks
    //   team2 outfield : 644..667 + tackles 698..705
    //   team1 goalie   : 947..970
    //   team2 goalie   : 1063..1086
    // Per-block layout matches PlayerFrames:
    //   ord_in_team   0..11 -> row 0 cols 0..11 (cardinals: N/S/E/W stand+run triplets)
    //   ord_in_team  12..23 -> row 1 cols 0..11 (diagonals: SW/SE/NW/NE triplets)
    //   ord_in_team  54..61 -> row 0 cols 12..19 (slide-tackle, 8 directions)
    //
    // ord_in_team = imageIndex - team_base. team1Player=341, team2Player=644,
    // team1Goalie=947, team2Goalie=1063. The outfield atlas is reused for
    // goalies (caller's _homeKeeperTiles / _awayKeeperTiles dict re-colours
    // into the keeper kit) so we collapse all four bases here.
    private static (int col, int row)? SwosOrdinalToAtlasCell(int imageIndex)
    {
        int rel;
        bool goalie = false;
        if      (imageIndex >= 341  && imageIndex <= 643)  rel = imageIndex - 341;   // team1 player
        else if (imageIndex >= 644  && imageIndex <= 946)  rel = imageIndex - 644;   // team2 player
        else if (imageIndex >= 947  && imageIndex <= 1062) { rel = imageIndex - 947;  goalie = true; }  // team1 goalie
        else if (imageIndex >= 1063 && imageIndex <= 1178) { rel = imageIndex - 1063; goalie = true; }  // team2 goalie
        else return null;

        if (goalie)
        {
            // Each team's goalie block holds TWO 58-ordinal sets (main +
            // reserve keeper — frameOffset selects the set, swos-port
            // gameSprites.cpp:231-240 getGoalkeeperSpriteOffset). The Amiga
            // CJCTEAMG sheet has a single art set, so fold them together.
            rel %= 58;
            // Run/stand rel 0..23 — same rows 0/1 layout as the outfield
            // atlas (keeper tiles are cached from the keeper-kit recolour).
            if (rel <= 11) return (rel, 0);
            if (rel <= 23) return (rel - 12, 1);
            // Dive (rel 24..51 → ordinals 971-998/1087-1114) and catch
            // (rel 52..57 → 999-1004/1115-1120) — synthetic CJCTEAMG cells.
            return PlayerFrames.GoalieExtCell(rel);
        }

        // Cardinals: cols 0..11 of row 0 (run + stand frames for N/S/E/W).
        if (rel >= 0  && rel <= 11) return (rel, 0);
        // Diagonals: cols 0..11 of row 1 (SW/SE/NW/NE triplets).
        if (rel >= 12 && rel <= 23) return (rel - 12, 1);
        // Slide-tackle poses: row 0 cols 12..19 (8 directions, one cell each).
        if (rel >= 54 && rel <= 61) return (rel - 54 + 12, 0);
        // Tackled / fallen knockdown poses (task #177). swos.asm
        // playerTackledAnimTable (218408-218417 / 218486-218495) →
        // AnimationTablesData.s_Tackled*: ordinals 403-410 (team1) and 706-713
        // (team2) are rel 62..69, laid out in direction order N,S,W,E,SW,SE,NW,NE
        // — exactly PlayerFrames.Fallen's row-1 cols 12..19. PlayerTackle.
        // PlayerTackled sets Sprite.playerState = PL_TACKLED and installs this
        // stream (updatePlayers.cpp:14827-14830); without mapping the ordinal the
        // render fell through to the legacy standing/run pick, so a tripped player
        // stayed upright instead of dropping to the ground.
        if (rel >= 62 && rel <= 69) return (rel - 62 + 12, 1);
        // Injured poses (ordinals 411-414 team1 / 714-717 team2 = rel 70-73).
        // plInjuredAnimTable (swos.asm:219566-219582; frame streams at
        // 218450-218453 / 218536-218538) drives two 2-frame writhe streams the
        // VM picks by the player's facing at injury time and then flip-flops
        // every 20 ticks, with the orientation FROZEN (state-13 re-bind
        // suppression) so it never rotates:
        //   dirs 4-7 → TopRight (411,-20,412 / 714,-20,715 = rel 70,71)
        //   dirs 0-3 → TopLeft  (413,-20,414 / 716,-20,717 = rel 72,73)
        // The CJCTEAM* sheet DOES carry the real writhe art: four distinct
        // lying-body tiles on the y=32 row (grid row 2), cols 7-10 (verified
        // visually 2026-07-11 — col 7/8 lie head-up-RIGHT = TopRight; col 9/10
        // lie head-up-LEFT = TopLeft). Map each ordinal straight to its own
        // tile; the VM already alternates the pair and locks the axis, so this
        // static per-ordinal map reproduces the original writhe exactly.
        //   rel 70 (411/714) → col 7   rel 71 (412/715) → col 8   [TopRight]
        //   rel 72 (413/716) → col 9   rel 73 (414/717) → col 10  [TopLeft]
        if (rel >= 70 && rel <= 73)
            return (7 + (rel - 70), PlayerFrames.ExtWritheRowBase);
        // SW tumble/roll continuation (ordinals 417-424 — the s_Tackled*BottomLeft
        // sequence, shared by both teams so rel 76..83 via the team1 range). The
        // Amiga CJCTEAM sheet carries no distinct tumble art, so hold the SW
        // fallen pose (row 1 col 16) for the whole roll rather than dropping to
        // the legacy standing pick.
        if (rel >= 76 && rel <= 83) return (16, 1);
        // Cheer (rel 24..29) + throw-in (rel 30..53) — synthetic band cells
        // below row 1 of the CJCTEAM* sheet (bug #157c: the throw-in taker
        // must show the ball-held-overhead pose while hideBall=1).
        var ext = PlayerFrames.OutfieldExtCell(rel);
        if (ext.HasValue) return ext;
        // Remaining tackled/injured ordinals outside the cached set - returning
        // null routes to the legacy renderer's PlayerFrames.Fallen / Slide /
        // Standing pick which IS cached.
        return null;
    }

    private void UpdateUi()
    {
        if (_appState == AppState.Menu)
        {
            if (_scoreLabel is not null) _scoreLabel.Text = "";
            if (_timerLabel is not null) _timerLabel.Text = "";
            if (_overlayLabel is not null) _overlayLabel.Text = "";
            // New SWOS-styled client owns the whole menu; keep the legacy label
            // and dim-backdrop hidden.
            if (_menuLabel is not null) _menuLabel.Visible = false;
            if (_menuBackground is not null) _menuBackground.Visible = false;
            if (_menuBgSprite is not null) _menuBgSprite.Visible = true;
            if (_menuClient is not null) _menuClient.Active = true;
            return;
        }

        if (_menuLabel is not null) _menuLabel.Visible = false;
        if (_menuBackground is not null) _menuBackground.Visible = false;
        if (_menuBgSprite is not null) _menuBgSprite.Visible = false;
        if (_menuClient is not null) _menuClient.Active = false;
        if (_scoreLabel is not null)
        {
            string home = (_allTeams.Count > _homeTeamIndex) ? _allTeams[_homeTeamIndex].Name : "HOME";
            string away = (_allTeams.Count > _awayTeamIndex) ? _allTeams[_awayTeamIndex].Name : "AWAY";
            _scoreLabel.Text = $"{home}  {_match.ScorePlayer} - {_match.ScoreOpponent}  {away}";
        }
        if (_timerLabel is not null)
        {
            int totalMin, totalSec, gameSecInHalf;
            if (_useSwosPort)
            {
                // Port path: read the authoritative clock straight out of the
                // SWOS memory image. gt_gameTimeInMinutes (gameTime.cpp:80) is
                // a true cumulative minute count (0..120); gt_gameSeconds is
                // seconds-into-current-minute (0..59, briefly -1 during the
                // last-minute prolong window — clamp to 0 so we never go
                // backwards).
                uint mins = OpenSwos.SwosVm.Memory.ReadDword(OpenSwos.SwosVm.Memory.Addr.gt_gameTimeInMinutes);
                int secs = OpenSwos.SwosVm.Memory.ReadSignedDword(OpenSwos.SwosVm.Memory.Addr.gt_gameSeconds);
                if (secs < 0) secs = 0;
                if (secs > 59) secs = 59;
                totalMin = (int)mins;
                totalSec = secs;
                gameSecInHalf = ((int)mins % 45) * 60 + secs;
            }
            else
            {
                // Legacy hand-tuned path. SWOS shows in-game minutes (0:00 to
                // 45:00 per half) regardless of how much real time the half
                // actually takes — same model as SWOS's
                // m_secondsSwitchAccumulator (gameTime.cpp:80-95). 45
                // game-minutes squeezed into SecondsPerHalf of real time.
                long realTicks = _match.MatchTick;
                long realTicksPerHalf = (long)SecondsPerHalf * MatchTimings.TicksPerSecond;
                int gameSecTotal = (int)(realTicks * 45 * 60 / System.Math.Max(realTicksPerHalf, 1));
                gameSecInHalf = System.Math.Min(gameSecTotal, 45 * 60);
                int baseMinute = (_match.Half == 1) ? 0 : 45;
                totalMin = baseMinute + gameSecInHalf / 60;
                totalSec = gameSecInHalf % 60;
            }
            // Stoppage-time suffix. SWOS pinned the clock at 45:00 / 90:00
            // during the last-minute prolong window (gameTime.cpp:64-77 — the
            // m_gameSeconds=-1 sentinel disables the normal MM:SS advance);
            // modern broadcasts annotate that period as "45:00 +M:SS". Render
            // the same hint by reading Sim/Port/GameTime.cs's stoppage
            // accumulator. Port-only — the original never displayed it.
            string stoppageSuffix = "";
            if (_useSwosPort && OpenSwos.Sim.Port.GameTime.InProlong)
            {
                int stoppageSec = OpenSwos.Sim.Port.GameTime.StoppageGameSeconds;
                int stMin = stoppageSec / 60;
                int stSec = stoppageSec % 60;
                stoppageSuffix = $" +{stMin}:{stSec:D2}";
            }
            _timerLabel.Text = $"{totalMin,2}:{totalSec:D2}{stoppageSuffix}";
            // Pulse red in last 60 game-seconds of a half (≈ last 1.3 s real time @ 30s/half).
            int gameSecLeft = 45 * 60 - gameSecInHalf;
            _timerLabel.Modulate = (_match.Phase == MatchPhase.Play && gameSecLeft <= 60 && gameSecLeft >= 0)
                ? new Color(1f, 0.3f, 0.3f, 1f)
                : Colors.White;
        }
        if (_overlayLabel is not null)
            _overlayLabel.Text = OverlayText();

        if (_useSwosPort)
            UpdateSwosPortHud();
        else
        {
            SetResultPanelVisible(false);
            if (_ctrlNumSprites[0] is not null) _ctrlNumSprites[0]!.Visible = false;
            if (_ctrlNumSprites[1] is not null) _ctrlNumSprites[1]!.Visible = false;
        }
    }

    // === SWOS-port HUD extras (Session 24) ===================================
    // 1. Result panel: shown exactly when the ported result.cpp timers say so
    //    (Result.ShouldDrawResult; armed to 165 ticks before every kickoff at
    //    breakCameraMode 7, gameLoop.cpp:1808; 275 ticks at half/full time).
    //    Layout copies drawResult (result.cpp:145-156, 229-344) 1:1 — see
    //    LayoutResultPanel.
    // 2. Controlled-player shirt number above the head — reads the sprite
    //    slots computed by GameSprites.UpdateControlledPlayerNumbers
    //    (swos.asm:97583-97718 via gameSprites.cpp:281-312): imageIndex
    //    kSmallDigit1 + shirt - 1, world pos (x, y - z) with z already +20,
    //    imageIndex == -1 → hidden (CPU team / no controlled / blink phase).
    private void UpdateSwosPortHud()
    {
        bool showResult = _appState == AppState.Match
            && OpenSwos.Sim.Port.Result.ShouldDrawResult();
        SetResultPanelVisible(showResult);
        if (showResult)
            LayoutResultPanel();

        UpdateBenchPanel();
        UpdatePlayerNameBanner();

        for (int ti = 0; ti < 2; ti++)
        {
            var spr = _ctrlNumSprites[ti];
            if (spr is null) continue;
            if (_appState != AppState.Match)
            {
                spr.Visible = false;
                continue;
            }
            var (nx, ny, nz, img) = OpenSwos.Sim.Port.GameSprites.GetCurPlayerNumSprite(ti);
            if (img < 0)
            {
                spr.Visible = false;
                continue;
            }
            // Suppress the controlled-number sprite for the GOALKEEPER (players[0]
            // → playerOrdinal 1): the keeper's per-tick dive/catch animation jitters
            // its z, so the above-head number smeared (user report). Renderer-only
            // gate; the sim is untouched.
            int ctrlPtr = OpenSwos.SwosVm.TeamData.ControlledPlayer(ti == 0);
            if (ctrlPtr != 0 &&
                OpenSwos.SwosVm.Memory.ReadSignedWord(ctrlPtr + OpenSwos.SwosVm.PlayerSprite.OffPlayerOrdinal) == 1)
            {
                spr.Visible = false;
                continue;
            }
            int shirt = img - OpenSwos.Sim.Port.GameSprites.kSmallDigit1 + 1;
            spr.Texture = GetShirtNumberTexture(shirt);
            // SWOS draws sprites at (x, y - z); z already includes the +20
            // above-head offset (swos.asm:97686-97688). Same PitchOffset
            // world transform as the player sprites. Centered sprite → position
            // at the head-centre (no -3 half-width fudge the Label needed).
            spr.Position = new Vector2(nx + PitchOffsetX, ny - nz + PitchOffsetY);
            spr.Visible = true;
        }
    }

    // BUG #160: renders a shirt number to a crisp pixel texture (cached).
    // The glyphs are a compact 3×5 SWOS-style pixel digit font, white on
    // transparent — drawn at native resolution so the NEAREST-filtered
    // Sprite2D stays pixel-sharp at any zoom. This stands in for the authentic
    // pre-composed number sprites (SPRITE.DAT ordinals kSmallDigit1..16) which
    // are not yet decoded from the PC/Amiga art.
    private ImageTexture GetShirtNumberTexture(int number)
    {
        if (_shirtNumTextures.TryGetValue(number, out var cached))
            return cached;
        var tex = ImageTexture.CreateFromImage(BuildShirtNumberImage(number));
        _shirtNumTextures[number] = tex;
        return tex;
    }

    // 3×5 pixel bitmaps for digits 0-9 (row-major, MSB = leftmost column).
    // Compact numeric-only font sized to match SWOS's tiny above-head numbers.
    private static readonly byte[][] Digit3x5 =
    {
        new byte[]{0b111,0b101,0b101,0b101,0b111}, // 0
        new byte[]{0b010,0b110,0b010,0b010,0b111}, // 1
        new byte[]{0b111,0b001,0b111,0b100,0b111}, // 2
        new byte[]{0b111,0b001,0b111,0b001,0b111}, // 3
        new byte[]{0b101,0b101,0b111,0b001,0b001}, // 4
        new byte[]{0b111,0b100,0b111,0b001,0b111}, // 5
        new byte[]{0b111,0b100,0b111,0b101,0b111}, // 6
        new byte[]{0b111,0b001,0b001,0b001,0b001}, // 7
        new byte[]{0b111,0b101,0b111,0b101,0b111}, // 8
        new byte[]{0b111,0b101,0b111,0b001,0b111}, // 9
    };

    private static Image BuildShirtNumberImage(int number)
    {
        string digits = System.Math.Abs(number).ToString();
        const int gw = 3, gh = 5, gap = 1, pad = 1;
        int inner = digits.Length * gw + (digits.Length - 1) * gap;
        int w = inner + pad * 2;
        int h = gh + pad * 2;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        img.Fill(new Color(0, 0, 0, 0));
        var white = new Color(1f, 1f, 1f, 1f);
        var shadow = new Color(0f, 0f, 0f, 0.6f);   // 1-px drop shadow for pitch contrast
        // Two passes so a later shadow never overwrites an earlier white pixel.
        for (int pass = 0; pass < 2; pass++)
        {
            int cx = pad;
            foreach (char c in digits)
            {
                var g = Digit3x5[c - '0'];
                for (int ry = 0; ry < gh; ry++)
                    for (int rx = 0; rx < gw; rx++)
                        if ((g[ry] & (1 << (gw - 1 - rx))) != 0)
                        {
                            if (pass == 0) img.SetPixel(cx + rx + 1, ry + pad + 1, shadow);
                            else           img.SetPixel(cx + rx,     ry + pad,     white);
                        }
                cx += gw + gap;
            }
        }
        return img;
    }

    // === Injury cross markers (port mode, task #184) =========================
    // Draws a small red medical cross above any player who is down injured.
    // A player is "down injured" while playerState is PL_TACKLED(3) WITH a
    // non-zero injuryLevel (the initial knock-down, updatePlayers.cpp:14827) or
    // PL_ROLLING_INJURED(13) (the writhe phase, updatePlayers.cpp:7488). A plain
    // tackle (injuryLevel==0) is deliberately excluded so only genuine injuries
    // show the marker. Position/z mirror the player's own sprite (set in the
    // port loop earlier this tick), so the cross scrolls + y-sorts with him.
    private void UpdateInjuryMarkers()
    {
        bool active = _useSwosPort && _appState == AppState.Match;
        for (int slot = 0; slot < _injuryCrossSprites.Length; slot++)
        {
            var cross = _injuryCrossSprites[slot];
            if (cross is null) continue;
            var pl = _portPlayerSprites[slot];
            bool show = false;
            if (active && pl is not null && pl.Visible)
            {
                int baseAddr = OpenSwos.SwosVm.PlayerSprite.Base(slot);
                short injuryLevel = OpenSwos.SwosVm.Memory.ReadSignedWord(
                    baseAddr + OpenSwos.SwosVm.PlayerSprite.OffInjuryLevel);
                byte state = OpenSwos.SwosVm.PlayerSprite.PlayerState(slot);
                show = injuryLevel != 0 && (state == 3 || state == 13);
            }
            cross.Visible = show;
            if (show)
            {
                // A touch above the head (sprite is Centered, 16 px tall).
                cross.Position = new Vector2(pl!.Position.X, pl.Position.Y - 11);
                cross.ZIndex = pl.ZIndex + 2;
            }
        }
    }

    // === Energy bars (OpenSWOS fatigue overlay) ==============================
    // 8×2 px bar above each on-pitch player's head, reading PlayerEnergy from
    // the sim (0..4096 → 0..8 filled columns). Colour by fill: green >= 5,
    // yellow >= 2, else red. Cloned from UpdateInjuryMarkers so it scrolls +
    // y-sorts with the player sprite. Visibility gated by the ENERGY BAR option.
    private ImageTexture GetEnergyBarTexture(int fillPx)
    {
        fillPx = System.Math.Clamp(fillPx, 0, 8);
        if (_energyBarTextures.TryGetValue(fillPx, out var cached))
            return cached;
        var tex = ImageTexture.CreateFromImage(BuildEnergyBarImage(fillPx));
        _energyBarTextures[fillPx] = tex;
        return tex;
    }

    private static Image BuildEnergyBarImage(int fillPx)
    {
        const int w = 8, h = 2;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        var bg = new Color(0f, 0f, 0f, 0.65f);
        Color fill = fillPx >= 5 ? new Color(0.2f, 0.85f, 0.2f)   // green
                   : fillPx >= 2 ? new Color(0.9f, 0.85f, 0.15f)  // yellow
                                 : new Color(0.9f, 0.2f, 0.15f);   // red
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                img.SetPixel(x, y, x < fillPx ? fill : bg);
        return img;
    }

    private void UpdateEnergyBars()
    {
        bool active = _useSwosPort && _appState == AppState.Match;
        for (int slot = 0; slot < _energyBarSprites.Length; slot++)
        {
            var bar = _energyBarSprites[slot];
            if (bar is null) continue;
            var pl = _portPlayerSprites[slot];
            if (!_energyBar || !active || pl is null || !pl.Visible)
            {
                bar.Visible = false;
                continue;
            }
            int energy = OpenSwos.Sim.Port.PlayerEnergy.ReadEnergy(slot);
            int fillPx = System.Math.Clamp(
                (energy * 8 + OpenSwos.Sim.Port.PlayerEnergy.Max / 2)
                    / OpenSwos.Sim.Port.PlayerEnergy.Max, 0, 8);
            bar.Texture = GetEnergyBarTexture(fillPx);
            // Centre the bar on the player's BODY, not the sprite node's centre.
            // The player Sprite2D is Centered but foot-anchored (StandingAnchor Cx
            // is 1..4, not 8), so its node centre sits a few px RIGHT of the body.
            // Use the port world-X (same source the head-number sprite centres on
            // via nx + PitchOffsetX) so the 8px bar sits over the player.
            float bodyX = OpenSwos.SwosVm.PlayerSprite.XPixels(slot) + PitchOffsetX;
            // -11: midpoint between the original -13 (too high, overlapped the
            // number) and -9 (too low) — user asked to split the difference.
            bar.Position = new Vector2(bodyX, pl.Position.Y - 11);
            bar.ZIndex = pl.ZIndex + 3;
            bar.Visible = true;
        }
    }

    // === Referee + card rendering (port mode, task #181) =====================
    // Reads the ported referee FSM (Sim/Port/Referee.cs) and draws the referee
    // sprite + card. The FSM already runs the run-in / wait / card / leave state
    // machine + moves the RefereeSprite; the only gap was that nothing was ever
    // drawn. Referee.RefWorldX/Y/Z surface the SWOS-world sprite position; we map
    // it with the SAME (PitchOffsetX/Y) transform + worldY+1000 z-order the
    // player sprites use. Pose picks the procedural texture from the referee's
    // current imageIndex (1273-1283); the card colour comes from whichCard.
    private void UpdateRefereeRender()
    {
        if (_refereeSprite is null) return;
        // kRefOffScreen == 0 (referee.cpp:25). Only draw while the FSM is active
        // AND the sprite is shown (the FSM hides it off-screen / at rest).
        bool show = _useSwosPort && _appState == AppState.Match
            && OpenSwos.Sim.Port.Referee.RefState != 0
            && OpenSwos.Sim.Port.Referee.RefVisible;
        if (!show)
        {
            _refereeSprite.Visible = false;
            if (_refereeCardSprite is not null) _refereeCardSprite.Visible = false;
            return;
        }

        int worldX = OpenSwos.Sim.Port.Referee.RefWorldX + PitchOffsetX;
        int worldY = OpenSwos.Sim.Port.Referee.RefWorldY + PitchOffsetY;
        int z      = OpenSwos.Sim.Port.Referee.RefWorldZ;

        int pose = RefereePoseFromImage(
            OpenSwos.Sim.Port.Referee.RefImageIndex,
            OpenSwos.Sim.Port.Referee.RefState);
        _refereeSprite.Texture = GetRefereePoseTexture(pose);
        // Figure feet sit at texture row 14; texture is 16×16 Centered (centre
        // row 8), so drop by (14-8)=6 to land the feet on the sim's foot position.
        _refereeSprite.Position = new Vector2(worldX, worldY - z - 6);
        _refereeSprite.ZIndex = worldY + 1000;
        _refereeSprite.Visible = true;

        // Card: shown for the whole booking phase (kRefBooking == 4) so the
        // colour is unmistakable. referee.cpp encodes the colour as the ref's
        // 1280 (yellow) / 1281 (red) hand poses; we draw a dedicated colored card
        // above his raised hand. whichCard: 1=yellow, 2=red, 3=second-yellow
        // (a yellow that also sends off) → show yellow.
        if (_refereeCardSprite is not null)
        {
            int card = OpenSwos.Sim.Port.Referee.RefWhichCard;
            bool showCard = OpenSwos.Sim.Port.Referee.RefState == 4 /*kRefBooking*/ && card != 0;
            _refereeCardSprite.Visible = showCard;
            if (showCard)
            {
                _refereeCardSprite.Texture = (card == 2) ? _redCardTexture : _yellowCardTexture;
                // Above + slightly right of the referee (the raised-arm side).
                _refereeCardSprite.Position = new Vector2(worldX + 4, worldY - z - 18);
                _refereeCardSprite.ZIndex = worldY + 1001;
            }
        }
    }

    // Map the referee sprite ordinal (1273-1283) to a procedural pose id.
    // Falls back to the FSM state when the animation walker hasn't produced an
    // imageIndex yet. Pose ids: 0=standing, 1=running, 2=arm-raised(card).
    private static int RefereePoseFromImage(int img, int refState)
    {
        if (img >= 1273 && img <= 1278) return 1;  // running (both walk cycles)
        if (img == 1279)                return 0;  // standing
        if (img >= 1280 && img <= 1283) return 2;  // card / arm raised
        return refState switch
        {
            1 or 5 => 1,   // kRefIncoming / kRefLeaving → walking
            4      => 2,   // kRefBooking → arm raised
            _      => 0,   // waiting / about-to-give → standing
        };
    }

    private ImageTexture GetRefereePoseTexture(int pose)
    {
        if (_refereePoseTextures.TryGetValue(pose, out var cached)) return cached;
        var tex = ImageTexture.CreateFromImage(BuildRefereeImage(pose));
        _refereePoseTextures[pose] = tex;
        return tex;
    }

    // Procedural 16×16 referee figure (black kit — SWOS referees wear black).
    // pose: 0=standing, 1=running (legs spread), 2=arm raised (holding a card).
    // Stand-in for the undecoded CJCGRAFS referee sprites 1273-1283.
    private static Image BuildRefereeImage(int pose)
    {
        const int w = 16, h = 16;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        img.Fill(new Color(0, 0, 0, 0));
        var skin  = new Color(0.90f, 0.72f, 0.55f);
        var black = new Color(0.08f, 0.08f, 0.08f);   // referee's black shirt/shorts
        var hair  = new Color(0.20f, 0.13f, 0.08f);
        void Px(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) img.SetPixel(x, y, c); }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Px(x, y, c); }

        Rect(6, 0, 9, 0, hair);    // hair
        Rect(6, 1, 9, 3, skin);    // head
        Rect(5, 4, 10, 9, black);  // torso (shirt)
        // Legs (row 10-14).
        if (pose == 1) { Rect(4, 10, 6, 14, black); Rect(9, 10, 11, 14, black); } // running: spread
        else           { Rect(6, 10, 7, 14, black); Rect(8, 10, 9, 14, black); }  // standing: together
        // Arms.
        if (pose == 2)
        {
            Rect(11, 2, 12, 5, black);  // right arm raised (sleeve)
            Rect(11, 0, 12, 1, skin);   // raised hand at top
            Rect(3, 5, 4, 8, black);    // left arm at side
        }
        else
        {
            Rect(3, 5, 4, 8, black);    // left arm
            Rect(11, 5, 12, 8, black);  // right arm
        }
        return img;
    }

    // Small colored card sprite (yellow / red) shown during a booking.
    private static Image BuildCardImage(bool red)
    {
        const int w = 7, h = 10;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        var face   = red ? new Color(0.85f, 0.12f, 0.10f) : new Color(0.97f, 0.86f, 0.15f);
        var border = new Color(0.05f, 0.05f, 0.05f);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                img.SetPixel(x, y, edge ? border : face);
            }
        return img;
    }

    // Small procedural red medical cross (task #184), a white-bordered red plus
    // sign on a transparent field. Stand-in for the undecoded CJCGRAFS injury
    // sprite (kRedCrossInjurySprite=1290). Drawn above a downed injured player.
    private static Image BuildInjuryCrossImage()
    {
        const int w = 9, h = 9;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        img.Fill(new Color(0, 0, 0, 0));
        var white = new Color(1f, 1f, 1f);
        var red   = new Color(0.86f, 0.10f, 0.10f);
        void Px(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) img.SetPixel(x, y, c); }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Px(x, y, c); }
        // White plus (outer), then a red plus one pixel smaller inside it.
        Rect(3, 0, 5, 8, white);   // vertical bar
        Rect(0, 3, 8, 5, white);   // horizontal bar
        Rect(4, 1, 4, 7, red);     // red vertical
        Rect(1, 4, 7, 4, red);     // red horizontal
        return img;
    }

    // === SWOS bench / substitutions panel (port mode) ========================
    // PORT-VISUAL: the original renders the bench as sprite rows at the dugout
    // (drawBench.cpp: drawSubstitutesMenuEntry / drawShirtNumber / drawName /
    // drawPosition). We approximate with a text panel; ALL state (cursor,
    // menu mode, eligibility, shirt numbers) comes from the ported FSM in
    // Sim/Port/Bench.cs (updateBench.cpp), so the panel is a pure view.
    //
    // Controls (P1, in port mode): B during a stoppage opens the bench.
    // In the menu: UP/DOWN move the cursor, SPACE (fire) confirms,
    // LEFT or RIGHT walks away (leavingBenchMotion, updateBench.cpp:608-612).
    // Accumulator for one bench-menu frame: (text, isSelectedEntry).
    private readonly System.Collections.Generic.List<(string text, bool sel)> _benchRows = new();

    // Shirt numbers of the benching team's currently-injured players — so the
    // bench rows can flag them (task #190). The original draws a red cross next
    // to injured players in the subs menu (drawBench.cpp:318-325 drawInjuryIcon,
    // via PlayerInfo.injuriesBitfield). Injury lives on the SPRITE (injuryLevel);
    // we resolve each injured sprite → its shirt number and match by shirt in
    // FormatBenchPlayerRow (shirt numbers are unique within a team).
    private readonly System.Collections.Generic.HashSet<int> _benchInjuredShirts = new();

    private void UpdateBenchPanel()
    {
        bool show = _useSwosPort
            && _appState == AppState.Match
            && OpenSwos.Sim.Port.Bench.InBenchOrGoingTo();
        if (_benchPanelBg is null)
            return;
        if (!show || _font is null)
        {
            HideBenchPanel();
            return;
        }

        _benchRows.Clear();
        int state = OpenSwos.Sim.Port.Bench.GetBenchState();
        int teamGame = OpenSwos.Sim.Port.Bench.GetBenchTeamGameBase();

        // Collect the benching team's injured shirt numbers (scoped to that
        // team's 11 sprites so a same-number player on the other side can't
        // false-positive). injuryLevel > 0 = injured, -2 = stretchered off.
        _benchInjuredShirts.Clear();
        bool benchIsTeam1 = teamGame == OpenSwos.SwosVm.Memory.Addr.team1InGameTeamPlayers;
        int injSlotStart = benchIsTeam1 ? 0 : OpenSwos.SwosVm.PlayerSprite.TeamSize;
        int injPhysBase = benchIsTeam1
            ? OpenSwos.SwosVm.Memory.Addr.team1InGameTeamPlayers
            : OpenSwos.SwosVm.Memory.Addr.team2InGameTeamPlayers;
        for (int s = injSlotStart; s < injSlotStart + OpenSwos.SwosVm.PlayerSprite.TeamSize; s++)
        {
            int spriteBase = OpenSwos.SwosVm.PlayerSprite.Base(s);
            short injLvl = OpenSwos.SwosVm.Memory.ReadSignedWord(
                spriteBase + OpenSwos.SwosVm.PlayerSprite.OffInjuryLevel);
            if (injLvl <= 0 && injLvl != -2) continue;
            short ordinal = OpenSwos.SwosVm.Memory.ReadSignedWord(
                spriteBase + OpenSwos.SwosVm.PlayerSprite.OffPlayerOrdinal);
            if (ordinal < 1) continue;
            int piAddr = injPhysBase + (ordinal - 1) * OpenSwos.Sim.Port.TeamDataLoader.PlayerInfoSize;
            _benchInjuredShirts.Add(OpenSwos.SwosVm.Memory.ReadByte(
                piAddr + OpenSwos.Sim.Port.TeamDataLoader.OffShirtNumber));
        }

        // Header — TeamGame.teamName sits 20 bytes before players[0]
        // (swos.h:296 header layout: name at +22, players at +42).
        string teamName = teamGame != 0 ? ReadVmAscii(teamGame - 20, 17) : "";
        _benchRows.Add(($"BENCH - {teamName}", false));

        if (OpenSwos.Sim.Port.Bench.SubstituteInProgress())
        {
            _benchRows.Add(("", false));
            _benchRows.Add(("SUBSTITUTION IN PROGRESS...", false));
        }
        else switch (state)
        {
            case OpenSwos.Sim.Port.Bench.kBenchStateInitial:
            {
                // drawBench: coach row + 5 substitutes (players[11..15]).
                int arrow = OpenSwos.Sim.Port.Bench.GetBenchPlayerIndex();
                _benchRows.Add(("COACH", arrow == 0));
                for (int i = 1; i <= OpenSwos.Sim.Port.Bench.kMaxSubstitutes; i++)
                {
                    int infoAddr = teamGame + (i + 10) * OpenSwos.Sim.Port.TeamDataLoader.PlayerInfoSize;
                    _benchRows.Add((FormatBenchPlayerRow(infoAddr), arrow == i));
                }
                _benchRows.Add(("", false));
                _benchRows.Add(("UP/DN SELECT  FIRE OK", false));
                _benchRows.Add(("LEFT/RIGHT LEAVE BENCH", false));
                break;
            }
            case OpenSwos.Sim.Port.Bench.kBenchStateAboutToSubstitute:
            {
                int enterIdx = OpenSwos.Sim.Port.Bench.PlayerToEnterGameIndex();
                int enterAddr = teamGame + enterIdx * OpenSwos.Sim.Port.TeamDataLoader.PlayerInfoSize;
                _benchRows.Add(($"COMING ON: {FormatBenchPlayerRow(enterAddr)}", false));
                _benchRows.Add(("PLAYER OFF:", false));
                int ord = OpenSwos.Sim.Port.Bench.PlayerToBeSubstitutedIndex();
                for (int i = 0; i < 11; i++)
                {
                    int infoAddr = OpenSwos.Sim.Port.Bench.GetBenchPlayerInfoAddr(i);
                    _benchRows.Add((FormatBenchPlayerRow(infoAddr), ord == i));
                }
                break;
            }
            case OpenSwos.Sim.Port.Bench.kBenchStateFormationMenu:
            {
                _benchRows.Add(("FORMATION:", false));
                int sel = OpenSwos.Sim.Port.Bench.GetSelectedFormationEntry();
                for (int i = 0; i < OpenSwos.Sim.Port.Bench.kNumFormationEntries; i++)
                {
                    // TeamTactics.name — first 9 bytes of each 370-byte struct
                    // in teamTacticsPool (swos.h:415, TacticsLoader.cs).
                    string name = ReadVmAscii(
                        OpenSwos.SwosVm.Memory.Addr.teamTacticsPool
                        + i * OpenSwos.Sim.Port.TacticsLoader.TacticsStructSize, 9);
                    if (string.IsNullOrWhiteSpace(name)) name = $"USER {(char)('A' + (i - 12))}";
                    _benchRows.Add((name, sel == i));
                }
                break;
            }
            case OpenSwos.Sim.Port.Bench.kBenchStateMarkingPlayers:
            {
                _benchRows.Add(("SWAP / FORMATION:", false));
                int ord = OpenSwos.Sim.Port.Bench.PlayerToBeSubstitutedIndex();
                int selPos = OpenSwos.Sim.Port.Bench.GetBenchMenuSelectedPlayer();
                _benchRows.Add(("FORMATION", ord < 0));
                for (int i = 1; i < 11; i++)
                {
                    int pos = OpenSwos.Sim.Port.Bench.GetBenchPlayerPosition(i);
                    int infoAddr = teamGame + pos * OpenSwos.Sim.Port.TeamDataLoader.PlayerInfoSize;
                    string marker = (selPos >= 0 && selPos == pos) ? "*" : " ";
                    _benchRows.Add((marker + FormatBenchPlayerRow(infoAddr), ord == i));
                }
                _benchRows.Add(("", false));
                _benchRows.Add(("FIRE PICK+PICK = SWAP", false));
                _benchRows.Add(("HOLD FIRE = MARK PLAYER", false));
                break;
            }
        }

        RenderBenchRows(teamGame);
    }

    // Draws the accumulated bench rows into the sprite/highlight pool. Mirrors
    // drawBench.cpp's substitutes/formation menu (drawSubstitutesMenuPlayers +
    // drawEntryHighlight): a team-coloured panel with the selected entry sitting
    // on a highlight bar, the row text in the SWOS charset small font.
    // PORT-VISUAL: charset text stands in for the face/shirt/card sprites the
    // original composites; geometry is a compact stack rather than the exact
    // kMenuX/kPlayerListY grid (our rows carry name + position inline).
    private const int BenchRowHeight = 9;      // >= kSmallFontHeight(6) + gap
    private const int BenchPanelX = 40;        // viewport left (kMenuX-ish, mapped for readability)
    private const int BenchPanelY = 14;        // near the top of the play area
    private const int BenchPadX = 5;
    private const int BenchPadY = 4;
    private void RenderBenchRows(int teamGame)
    {
        int n = System.Math.Min(_benchRows.Count, BenchMaxRows);

        // Panel width = widest rendered line (+ padding). determineMenuTeamColors
        // (drawBench.cpp:499-533) — background/highlight from the bench team kit.
        int maxW = 0;
        for (int i = 0; i < n; i++)
            maxW = System.Math.Max(maxW, _font!.Measure(_benchRows[i].text, false));
        int panelW = maxW + BenchPadX * 2;
        int panelH = n * BenchRowHeight + BenchPadY * 2;

        var (bgColor, hlColor) = GetBenchColors(teamGame);
        if (_benchPanelBg is not null)
        {
            _benchPanelBg.Color = bgColor;
            _benchPanelBg.Position = new Vector2(BenchPanelX, BenchPanelY);
            _benchPanelBg.Size = new Vector2(panelW, panelH);
            _benchPanelBg.Visible = true;
        }

        for (int i = 0; i < BenchMaxRows; i++)
        {
            var hl = _benchHighlights[i];
            var spr = _benchLineSprites[i];
            if (i >= n)
            {
                if (hl is not null) hl.Visible = false;
                if (spr is not null) spr.Visible = false;
                continue;
            }

            int ry = BenchPanelY + BenchPadY + i * BenchRowHeight;
            var (text, sel) = _benchRows[i];

            if (hl is not null)
            {
                if (sel)
                {
                    hl.Color = hlColor;
                    hl.Position = new Vector2(BenchPanelX, ry - 1);
                    hl.Size = new Vector2(panelW, BenchRowHeight);
                    hl.Visible = true;
                }
                else hl.Visible = false;
            }

            SetText(spr, text, false, TextAlign.Left, BenchPanelX + BenchPadX, ry, kWhiteText);
        }
    }

    private void HideBenchPanel()
    {
        if (_benchPanelBg is not null) _benchPanelBg.Visible = false;
        foreach (var s in _benchLineSprites) if (s is not null) s.Visible = false;
        foreach (var h in _benchHighlights) if (h is not null) h.Visible = false;
    }

    // Bench panel background + highlight-bar colours, approximating
    // determineMenuTeamColors (drawBench.cpp:499-533): both are picked from the
    // bench team's kit colours, avoiding bright (white/yellow) so white text
    // stays legible. Background = the darkest kit colour, highlight = the
    // brightest non-bright kit colour distinct from it. TeamGame header kit
    // bytes sit before players[0]: prShirtCol at -40, prStripesCol -38,
    // prShortsCol -36, prSocksCol -34 (swos.h:298-301, header at players-42).
    private static (Color bg, Color hl) GetBenchColors(int teamGame)
    {
        Color fallbackBg = new(0.10f, 0.12f, 0.28f, 0.92f);
        Color fallbackHl = new(0.20f, 0.45f, 0.85f, 1f);
        if (teamGame == 0) return (fallbackBg, fallbackHl);

        System.Span<int> offs = stackalloc int[] { -40, -38, -36, -34 };
        var cols = new System.Collections.Generic.List<Color>(4);
        foreach (int o in offs)
        {
            byte name = (byte)(OpenSwos.SwosVm.Memory.ReadByte(teamGame + o) & 0x0F);
            var (r, g, b) = OpenSwos.Assets.KitPalette.Get((byte)(name % 10));
            cols.Add(new Color(r / 255f, g / 255f, b / 255f));
        }
        static float Lum(Color c) => 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
        static bool Bright(Color c) => Lum(c) > 0.75f;

        // Background: darkest kit colour (kept opaque-ish for readability).
        Color bg = fallbackBg;
        float bgLum = 2f;
        foreach (var c in cols) if (Lum(c) < bgLum) { bgLum = Lum(c); bg = new Color(c.R, c.G, c.B, 0.92f); }

        // Highlight: brightest non-bright colour distinct from bg.
        Color hl = fallbackHl;
        float hlLum = -1f;
        foreach (var c in cols)
        {
            if (Bright(c)) continue;
            if (System.Math.Abs(Lum(c) - bgLum) < 0.001f) continue;
            if (Lum(c) > hlLum) { hlLum = Lum(c); hl = new Color(c.R, c.G, c.B, 1f); }
        }
        return (bg, hl);
    }

    // On-ball / controlled-player name banner (#169). drawPlayerName
    // (playerNameDisplay.cpp:57-70): when m_playerOrdinal >= 0, draw
    // "<shirtNumber> <SURNAME>" at (kPlayerNameX=12, kPlayerNameY=0) in the SWOS
    // small charset. The FSM (Sim/Port/PlayerNameDisplay.cs ← playerNameDisplay.cpp:
    // 20-55) decides visibility (control changes, goal/booking blink, 50-tick
    // nobody's-ball grace); we only render the state it exposes.
    private void UpdatePlayerNameBanner()
    {
        var spr = _playerNameSprite;
        if (spr is null) return;
        if (!_useSwosPort || _appState != AppState.Match || _font is null)
        {
            spr.Visible = false;
            return;
        }

        var (topTeam, ordinal) = OpenSwos.Sim.Port.PlayerNameDisplay.GetDisplayedPlayerNumberAndTeam();
        if (ordinal < 0 || ordinal > 15)
        {
            spr.Visible = false;
            return;
        }

        // team = topTeam ? topTeamPtr : bottomTeamPtr; players[ordinal] — same
        // TeamGame players base the bench reads (ReadDword(topTeamInGame) →
        // players[0]). PlayerInfo.shirtNumber @ +3, shortName @ +12.
        int teamGame = OpenSwos.SwosVm.Memory.ReadSignedDword(
            topTeam ? OpenSwos.SwosVm.Memory.Addr.topTeamInGame
                    : OpenSwos.SwosVm.Memory.Addr.bottomTeamInGame);
        if (teamGame == 0) { spr.Visible = false; return; }

        int pi = teamGame + ordinal * OpenSwos.Sim.Port.TeamDataLoader.PlayerInfoSize;
        byte shirt = OpenSwos.SwosVm.Memory.ReadByte(pi + OpenSwos.Sim.Port.TeamDataLoader.OffShirtNumber);
        string raw = ReadVmAscii(pi + OpenSwos.Sim.Port.TeamDataLoader.OffShortName, 14);
        // getPlayerName (result.cpp:346-351) / ExtractSurname: strip a leading
        // "X. " initial so only the surname shows.
        string surname = (raw.Length >= 3 && raw[1] == '.' && raw[2] == ' ') ? raw.Substring(3) : raw;
        string text = $"{shirt} {surname}".Trim();
        if (text.Length == 0) { spr.Visible = false; return; }

        // PORT-VISUAL: the original draws at kPlayerNameX=12 top-left, but our
        // viewport already parks the score there — top-RIGHT instead (user
        // request), right edge mirroring the original's 12-px margin. The
        // glyphs carry their authentic baked-in black shadow.
        SetText(spr, text, false, TextAlign.Right,
            ViewportWidth - 12, 2, kWhiteText);
    }

    // One bench-menu row: shirt number, short name, position code — mirrors
    // drawShirtNumber/drawName/drawPosition (drawBench.cpp:280-316).
    private string FormatBenchPlayerRow(int playerInfoAddr)
    {
        byte shirt = OpenSwos.SwosVm.Memory.ReadByte(
            playerInfoAddr + OpenSwos.Sim.Port.TeamDataLoader.OffShirtNumber);
        string name = ReadVmAscii(
            playerInfoAddr + OpenSwos.Sim.Port.TeamDataLoader.OffShortName, 14);
        sbyte pos = (sbyte)OpenSwos.SwosVm.Memory.ReadByte(
            playerInfoAddr + OpenSwos.Sim.Port.TeamDataLoader.OffPosition);
        byte cards = OpenSwos.SwosVm.Memory.ReadByte(
            playerInfoAddr + OpenSwos.Sim.Port.TeamDataLoader.OffCards);
        byte subFlag = OpenSwos.SwosVm.Memory.ReadByte(
            playerInfoAddr + OpenSwos.Sim.Port.TeamDataLoader.OffSubstituted);
        string tag = cards >= 2 ? " [OFF]" : (subFlag != 0 || pos == -1) ? " [SUB]" : "";
        // task #190 — flag injured players so the user knows who to sub.
        if (_benchInjuredShirts.Contains(shirt)) tag += " INJ";
        return $"{shirt,2} {name,-14} {PositionCode(pos)}{tag}";
    }

    // swos.playerPositionsStringTable equivalent (PlayerPosition, swos.h:149-160).
    private static string PositionCode(int position) => position switch
    {
        0 => "GK", 1 => "RB", 2 => "LB", 3 => "D ",
        4 => "RW", 5 => "LW", 6 => "M ", 7 => "A ",
        -1 => "--", _ => "? ",
    };

    // Reads a null-terminated ASCII string of at most maxLen chars from VM memory.
    private static string ReadVmAscii(int addr, int maxLen)
    {
        var sb = new System.Text.StringBuilder(maxLen);
        for (int i = 0; i < maxLen; i++)
        {
            byte b = OpenSwos.SwosVm.Memory.ReadByte(addr + i);
            if (b == 0) break;
            sb.Append(b >= 32 && b < 127 ? (char)b : '?');
        }
        return sb.ToString();
    }

    // === SWOS result panel (drawResult, result.cpp:145-156) =================
    //
    // Original geometry, in SWOS VGA space (320x200, render.h:3-4):
    //   kGridTopY            = 167  (kTeamNameY - 15, result.cpp:34)
    //   kPeriodEndSpriteY    = 157  (kGridTopY - 10,  result.cpp:35)
    //   kBigResultDigitY     = 177  (result.cpp:26)
    //   kTeamNameY           = 182  (result.cpp:33)
    //   kDashX/kDashY        = 157/185 (result.cpp:19-20)
    //   kLeftMargin          = 128  (team1 text right-aligned to it, result.cpp:240)
    //   kRightMargin         = 192  (team2 text left-aligned at it,  result.cpp:241)
    //   kFirstLineBelowResultY = 199, kScorerLineHeight = 7 (result.cpp:14,32)
    // The whole block slides UP by getScorerListOffsetY() (result.cpp:334-344):
    // -7 px per leading scorer line, clamped to <= -14. The dark strip spans
    // the FULL screen width (result.cpp:231 widens it by 2*gameScreenOffsetX
    // and drawGrid passes offsetX=false) and runs from its top edge down to
    // the bottom of the screen (result.cpp:233: height = kVgaHeight - y).
    //
    // Mapping VGA(320x200) -> our viewport (384x272, project.godot):
    //   X: +32 — centre the 320-wide SWOS UI space in the 384-wide viewport,
    //      exactly what swos-port does (gameFieldMapping.cpp:50 gameOffsetX).
    //   Y: +72 — BOTTOM-align the 200-high VGA screen so the darkened strip
    //      hugs the bottom edge, as in the original 320x200 fullscreen.
    //      (swos-port centres vertically — gameFieldMapping.cpp:51 — which
    //      would float the bar 36 px above the bottom of our taller viewport;
    //      the original look is bottom-flush, so we bottom-align instead.)
    // DESIGN space stays 384×272 — all match/menu layout math is authored at
    // this resolution. The root viewport is now the NATIVE 1152×816 (=×3), so
    // the match world (Camera2D.Zoom) and the UI CanvasLayer transform each
    // scale design coords up by RenderScale to fill it 1:1.
    // Cached StringName action ids for the HOT per-tick input paths (_PhysicsProcess
    // + TickSwosPort run at 70 Hz). Passing string literals to Input.IsAction*
    // implicitly allocates a StringName each call — cache them once instead.
    private static readonly StringName A_UiUp = "ui_up", A_UiDown = "ui_down",
        A_UiLeft = "ui_left", A_UiRight = "ui_right", A_UiAccept = "ui_accept",
        A_UiCancel = "ui_cancel", A_SwosPause = "swos_pause";

    private const int ViewportWidth  = 384;  // design width  (native = ×RenderScale) — MATCH HUD space
    private const int ViewportHeight = 272;  // design height (native = ×RenderScale) — MATCH HUD space
    private const int RenderScale    = 3;    // native viewport = 1152×816 = design ×3 (match world + HUD)
    // The menu/tables front-end lives at ×2 instead of ×3 (user preference: the
    // ×2 charset reads crisper + more compact than ×3). Same native 1152×816
    // viewport, so the menu's design space is native/2 = 576×408. The match HUD
    // stays at ×3 on its own `hud` CanvasLayer (see _Ready) so it never shrinks.
    private const int MenuScale          = 2;
    private const int MenuViewportWidth  = ViewportWidth  * RenderScale / MenuScale;  // 576
    private const int MenuViewportHeight = ViewportHeight * RenderScale / MenuScale;  // 408
    private const int ResultVgaToViewportX = (ViewportWidth - 320) / 2;  // 32
    private const int ResultVgaToViewportY = ViewportHeight - 200;       // 72

    // Alignment for charset text sprites (mirrors drawText / drawTextRightAligned
    // / drawTextCentered in text.cpp).
    private enum TextAlign { Left, Right, Center }

    // Factory for a hidden, NEAREST, top-left-anchored charset text sprite.
    private static Sprite2D MakeTextSprite(CanvasLayer ui)
    {
        var s = new Sprite2D
        {
            Visible = false,
            Centered = false,
            TextureFilter = TextureFilterEnum.Nearest,
        };
        ui.AddChild(s);
        return s;
    }

    // Sets a text sprite's glyph texture + position, anchoring at viewport
    // (vx, vy): Left → vx is the left edge, Right → vx is the right edge,
    // Center → vx is the centre. Hides the sprite for empty text / missing font.
    private static readonly Color kWhiteText = Colors.White;   // result.cpp kWhiteText
    private void SetText(Sprite2D? s, string text, bool big, TextAlign align, int vx, int vy, Color color)
    {
        if (s is null) return;
        if (_font is null || string.IsNullOrEmpty(text))
        {
            s.Visible = false;
            return;
        }
        var tex = _font.Render(text, big, color);
        if (tex is null) { s.Visible = false; return; }
        int w = tex.GetWidth();
        int x = align switch
        {
            TextAlign.Right => vx - w,
            TextAlign.Center => vx - w / 2,
            _ => vx,
        };
        s.Texture = tex;
        s.Position = new Vector2(x, vy);
        s.Visible = true;
    }

    private void SetResultPanelVisible(bool visible)
    {
        if (_resultPanelBg is not null) _resultPanelBg.Visible = visible;
        // The text sprites are shown/positioned per frame by LayoutResultPanel;
        // on hide, blank them all.
        if (!visible)
        {
            if (_resultPanelTeam1 is not null) _resultPanelTeam1.Visible = false;
            if (_resultPanelTeam2 is not null) _resultPanelTeam2.Visible = false;
            if (_resultPanelScore is not null) _resultPanelScore.Visible = false;
            if (_resultPanelTag is not null) _resultPanelTag.Visible = false;
            foreach (var s in _resultScorer1) if (s is not null) s.Visible = false;
            foreach (var s in _resultScorer2) if (s is not null) s.Visible = false;
        }
    }

    // drawResult (result.cpp:145-156) — geometry recomputed every frame
    // because the block slides up with the scorer-line count.
    private void LayoutResultPanel()
    {
        // DEFECT #4 (scorers missing): the sim never composes the scorer LINE
        // strings — result.cpp builds them in updateScorersText at
        // registerScorer time, but our Result.cs (Sim/Port) intentionally
        // delegates that pure text composition to the host renderer
        // (Result.cs:369-374 — "Godot iterates m_team{1,2}Scorers at draw time
        // to build the same strings"). So GetTeam*ScorerLines() are ALWAYS
        // empty; build the lines here from the structured ScorerInfo[] the sim
        // DOES populate. team1 == topTeamInGame roster, team2 == bottomTeamInGame
        // (RegisterScorer's team1=top / team2=bottom convention, result.cpp:
        // 165-172); the "other" roster resolves own-goal names (shirt+1000).
        int topRoster    = OpenSwos.SwosVm.Memory.ReadSignedDword(OpenSwos.SwosVm.Memory.Addr.topTeamInGame);
        int bottomRoster = OpenSwos.SwosVm.Memory.ReadSignedDword(OpenSwos.SwosVm.Memory.Addr.bottomTeamInGame);
        string[] s1 = BuildScorerLines(OpenSwos.Sim.Port.Result.GetTeam1Scorers(), topRoster, bottomRoster);
        string[] s2 = BuildScorerLines(OpenSwos.Sim.Port.Result.GetTeam2Scorers(), bottomRoster, topRoster);

        // getScorerListOffsetY (result.cpp:334-344): -7 (kScorerLineHeight)
        // per leading line index where EITHER team has a scorer line, clamped
        // to at most -14 ("force at least 14 pixels between team name and
        // scorer list").
        int numLines = 0;
        while (numLines < s1.Length
               && (!string.IsNullOrEmpty(s1[numLines]) || !string.IsNullOrEmpty(s2[numLines])))
            numLines++;
        int off = System.Math.Min(-OpenSwos.Sim.Port.Result.kScorerLineHeight * numLines,
                                  -2 * OpenSwos.Sim.Port.Result.kScorerLineHeight);

        // drawGrid (result.cpp:229-236): full-width dark strip from
        // kGridTopY(167)+off down to the screen bottom.
        int gridTop = OpenSwos.Sim.Port.Result.kGridTopY + off + ResultVgaToViewportY;
        if (_resultPanelBg is not null)
        {
            _resultPanelBg.Position = new Vector2(0, gridTop);
            _resultPanelBg.Size = new Vector2(ViewportWidth, ViewportHeight - gridTop);
        }

        // drawTeamNames (result.cpp:238-246): y = kTeamNameY(182)+off, BIG font;
        // team1 right-aligned ENDING at kLeftMargin(128), team2 left-aligned at
        // kRightMargin(192).
        string home = (_allTeams.Count > _homeTeamIndex) ? _allTeams[_homeTeamIndex].Name : "HOME";
        string away = (_allTeams.Count > _awayTeamIndex) ? _allTeams[_awayTeamIndex].Name : "AWAY";
        int nameY = OpenSwos.Sim.Port.Result.kTeamNameY + off + ResultVgaToViewportY;
        SetText(_resultPanelTeam1, home, true, TextAlign.Right,
            OpenSwos.Sim.Port.Result.kLeftMargin + ResultVgaToViewportX, nameY, kWhiteText);
        SetText(_resultPanelTeam2, away, true, TextAlign.Left,
            OpenSwos.Sim.Port.Result.kRightMargin + ResultVgaToViewportX, nameY, kWhiteText);

        // drawCurrentResult (result.cpp:248-264): the big result-digit sprites
        // (kBigZeroSprite/kBigDashSprite) around the dash at kDashX(157), block
        // centred on VGA x≈160. PORT-VISUAL: rendered with the SWOS BIG charset
        // digits (the dedicated big-digit sprites are not decoded yet). Score
        // comes from the same match state the rest of the HUD reads (original
        // reads team1/2GoalsDigit1/2).
        // DEFECT #3 (Y alignment): the original draws the score at
        // kBigResultDigitY(177) — 5 px ABOVE kTeamNameY(182) — because the tall
        // kBigZeroSprite digit sprites need lifting to share the team-name
        // baseline. We render the score with the SAME big charset font as the
        // team names, so that 5 px sprite-vs-font compensation does NOT apply:
        // the score and the names must sit on ONE row. Use kTeamNameY so
        // score.Y == names.Y (result.cpp:242,250).
        SetText(_resultPanelScore, $"{_match.ScorePlayer} - {_match.ScoreOpponent}", true, TextAlign.Center,
            160 + ResultVgaToViewportX,
            OpenSwos.Sim.Port.Result.kTeamNameY + off + ResultVgaToViewportY, kWhiteText);

        // drawHalfAndFullTimeSprites (result.cpp:326-332): kHalftimeSprite at
        // gameState kResultOnHalftime(25), kFullTimeSprite at
        // kResultAfterTheGame(26), centred at kResultX(160), floating 10 px
        // above the strip (kPeriodEndSpriteY = 157).
        int gs = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.gameState);
        string tag = gs == OpenSwos.Sim.Port.Result.kGameStateResultOnHalftime ? "HALF TIME"
                   : gs == OpenSwos.Sim.Port.Result.kGameStateResultAfterTheGame ? "FULL TIME" : "";
        SetText(_resultPanelTag, tag, true, TextAlign.Center,
            OpenSwos.Sim.Port.Result.kResultX + ResultVgaToViewportX,
            OpenSwos.Sim.Port.Result.kPeriodEndSpriteY + off + ResultVgaToViewportY, kWhiteText);

        // drawScorerList (result.cpp:301-324): skipped while dontShowScorers
        // is set (penalty shootout — GameTime.cs:1053). Each team's column
        // starts at kFirstLineBelowResultY(199)+off and compacts non-empty
        // lines 7 px apart; team1 right-aligned to kLeftMargin, team2
        // left-aligned at kRightMargin. The '\t' in the line text is the
        // original's 5-px name/minutes separator (result.cpp:375) — the SWOS
        // charset renders '\t' as a real 5-px tab, matching the original.
        // draw1stLegResult (result.cpp:266-299) is intentionally not rendered:
        // aggregate/second-leg cup flow isn't wired in the port yet.
        bool showScorers = OpenSwos.SwosVm.Memory.ReadSignedWord(OpenSwos.SwosVm.Memory.Addr.dontShowScorers) == 0;
        int baseY = OpenSwos.Sim.Port.Result.kFirstLineBelowResultY + off + ResultVgaToViewportY;
        LayoutScorerColumn(_resultScorer1, s1, showScorers, TextAlign.Right,
            OpenSwos.Sim.Port.Result.kLeftMargin + ResultVgaToViewportX, baseY);
        LayoutScorerColumn(_resultScorer2, s2, showScorers, TextAlign.Left,
            OpenSwos.Sim.Port.Result.kRightMargin + ResultVgaToViewportX, baseY);
    }

    private void LayoutScorerColumn(Sprite2D?[] sprites, string[] lines, bool show, TextAlign align, int x, int y)
    {
        for (int i = 0; i < sprites.Length; i++)
        {
            var s = sprites[i];
            if (s is null) continue;
            bool has = show && i < lines.Length && !string.IsNullOrEmpty(lines[i]);
            if (has)
            {
                SetText(s, lines[i], false, align, x, y, kWhiteText);
                y += OpenSwos.Sim.Port.Result.kScorerLineHeight;
            }
            else
            {
                s.Visible = false;
            }
        }
    }

    // Renderer-side port of updateScorersText + getPlayerName + GoalInfo::
    // storeTime (result.cpp:60-70, 346-351, 363-429). result.cpp composes the
    // scorer lines when a goal is registered; our Result.cs (Sim/Port) records
    // only the structured ScorerInfo[] (shirt number + per-goal minute/type)
    // and delegates the text composition to us (Result.cs:369-374). Produces
    // one display line per filled scorer slot: "SURNAME\tMM,NN(PEN)". The '\t'
    // is the original's 5-px name/minutes separator (result.cpp:375); SwosFont
    // renders it as a real 5-px tab.
    //
    // NOTE: the original's per-line width wrapping (result.cpp:405-420, spilling
    // a very prolific scorer's minute list onto extra lines) is not replicated —
    // one slot maps to one line, which matches the original for the ordinary
    // 1-4-goals-per-player case. ownRoster resolves regular scorers; otherRoster
    // resolves own goals (RegisterScorer tags those with shirt+1000 on the
    // beneficiary team's list, result.cpp:181).
    private string[] BuildScorerLines(OpenSwos.Sim.Port.Result.ScorerInfo[] scorers,
                                      int ownRoster, int otherRoster)
    {
        var lines = new string[OpenSwos.Sim.Port.Result.kMaxScorersForDisplay];
        for (int i = 0; i < lines.Length; i++) lines[i] = string.Empty;

        int lineIdx = 0;
        foreach (var info in scorers)
        {
            if (info.ShirtNum == 0) break;               // slots fill contiguously
            if (lineIdx >= lines.Length) break;

            // result.cpp:181 — own-goal entries carry shirt+1000 and belong to
            // the opposing roster.
            bool ownGoalEntry = info.ShirtNum >= 1000;
            int shirt = ownGoalEntry ? info.ShirtNum - 1000 : info.ShirtNum;
            int roster = ownGoalEntry ? otherRoster : ownRoster;

            var sb = new System.Text.StringBuilder();
            sb.Append(ScorerSurname(shirt, roster));
            sb.Append('\t');                              // result.cpp:375
            for (int g = 0; g < info.NumGoals; g++)
            {
                sb.Append(FormatGoalMinute(info.Goals[g]));
                if (g != info.NumGoals - 1) sb.Append(','); // result.cpp:399-400
            }
            lines[lineIdx++] = sb.ToString();
        }
        return lines;
    }

    // GoalInfo::storeTime (result.cpp:60-70): render the goal minute from its
    // BCD digits — hundreds/tens dropped when zero, ones always kept (the
    // original's exact leading-zero rule) — then append the goal-type tag
    // (result.cpp:388-397).
    private static string FormatGoalMinute(OpenSwos.Sim.Port.Result.GoalInfo goal)
    {
        var sb = new System.Text.StringBuilder(8);
        if (goal.TimeDigit1 != 0) sb.Append((char)('0' + goal.TimeDigit1));
        if (goal.TimeDigit2 != 0) sb.Append((char)('0' + goal.TimeDigit2));
        sb.Append((char)('0' + goal.TimeDigit3));
        if (goal.Type == 1) sb.Append("(PEN)");           // GoalType::kPenalty
        else if (goal.Type == 2) sb.Append("(OG)");       // GoalType::kOwnGoal
        return sb.ToString();
    }

    // getPlayerName (result.cpp:346-351): the surname of the shirt-holder with a
    // leading "X. " initial stripped. ScorerInfo keeps only the shirt number, so
    // scan the in-game roster (the same TeamGame records the bench/name-banner
    // read) for the matching shirtNumber and read its shortName.
    private static string ScorerSurname(int shirtNum, int rosterBase)
    {
        if (rosterBase == 0) return string.Empty;
        for (int i = 0; i < 16; i++)                       // TeamGame: 16 players
        {
            int pi = rosterBase + i * OpenSwos.Sim.Port.TeamDataLoader.PlayerInfoSize;
            byte shirt = OpenSwos.SwosVm.Memory.ReadByte(pi + OpenSwos.Sim.Port.TeamDataLoader.OffShirtNumber);
            if (shirt == shirtNum)
            {
                string raw = ReadVmAscii(pi + OpenSwos.Sim.Port.TeamDataLoader.OffShortName, 14);
                return (raw.Length >= 3 && raw[1] == '.' && raw[2] == ' ') ? raw.Substring(3) : raw;
            }
        }
        return string.Empty;
    }

    private string OverlayText()
    {
        if (_appState == AppState.Paused)
            return "PAUSED\n\nspace  resume\nESC    quit to menu";
        // SWOS-port penalty shootout banner — port has no dedicated MatchPhase
        // yet, so when playingPenalties=1 (updateGoals.cpp:48) we overlay a
        // banner on whatever phase is active. Hides during the PAUSED branch
        // above so the resume hint stays readable.
        if (_useSwosPort && _swosPortPenaltiesActive)
            return "PENALTIES";
        return _match.Phase switch
        {
            // BUG #158: original SWOS shows NO pre-kickoff overlay — the
            // "READY?" text here was a leftover placeholder. Removed so
            // PreKickoff renders a clean pitch like the original.
            MatchPhase.PreKickoff => "",
            MatchPhase.PostGoal => "GOAL!",
            MatchPhase.HalfTime => "HALF TIME",
            MatchPhase.FullTime => FullTimeOverlay(),
            MatchPhase.ThrowIn => "THROW-IN",
            MatchPhase.CornerKick => "CORNER",
            MatchPhase.GoalKick => "GOAL KICK",
            _ => "",
        };
    }

    private string FullTimeOverlay()
    {
        // SWOS full-time shows scoreline + scorers (already rendered by the
        // result panel); the old kicks/possession stat lines were a dead legacy
        // overlay and were removed (task #183).
        string home = (_allTeams.Count > _homeTeamIndex) ? _allTeams[_homeTeamIndex].Name : "HOME";
        string away = (_allTeams.Count > _awayTeamIndex) ? _allTeams[_awayTeamIndex].Name : "AWAY";
        return $"FULL TIME\n\n{home}  {_match.ScorePlayer} - {_match.ScoreOpponent}  {away}\n\n" +
               $"press space";
    }

    // === Phase transitions ===

    private void EnterPreKickoff()
    {
        _match.EnterPhase(MatchPhase.PreKickoff);
        var (px, py, ox, oy, bx, by) = KickoffPositionsForHalf(_match.Half);
        _player = PlayerState.At(px, py);
        _opponent = PlayerState.At(ox, oy);
        // Each player faces the centre of the pitch (the other team's side) at kickoff.
        _player.Facing = (_match.Half == 1) ? Direction.North : Direction.South;
        _opponent.Facing = (_match.Half == 1) ? Direction.South : Direction.North;
        _ball = BallState.At(bx, by);
        ResetKeepers();
        // SwosVm seed needs to refresh on every kick-off so memory state
        // matches the new layout (sides swap at HT, ball back at center).
        _swosVmDirty = true;
    }

    private void EnterPlay() => _match.EnterPhase(MatchPhase.Play);

    private void EnterPostGoal()
    {
        _match.EnterPhase(MatchPhase.PostGoal);
        // Reset positions immediately so the GOAL! freeze shows kickoff layout.
        var (px, py, ox, oy, bx, by) = KickoffPositionsForHalf(_match.Half);
        _player = PlayerState.At(px, py);
        _opponent = PlayerState.At(ox, oy);
        _player.Facing = (_match.Half == 1) ? Direction.North : Direction.South;
        _opponent.Facing = (_match.Half == 1) ? Direction.South : Direction.North;
        _ball = BallState.At(bx, by);
        ResetKeepers();
    }

    private void ResetKeepers()
    {
        _homeKeeper = PlayerState.At(KickoffBallX, GoalLineNorth + 20);
        _awayKeeper = PlayerState.At(KickoffBallX, GoalLineSouth - 20);
        _homeKeeper.Facing = Direction.South;
        _awayKeeper.Facing = Direction.North;
        // Goalie state machine init — keeper Y goal direction (+1 south end / -1 north
        // end) drives ShouldDive's "ball moving toward me" check. Skill 0..7 from
        // TeamRecord (NOT yet plumbed → default 4 which sits mid-table on the dive
        // delta array).
        _homeKeeper.GoalieFaceTeamY = -1;  // home keeper guards north goal
        _awayKeeper.GoalieFaceTeamY = +1;  // away keeper guards south goal
        _homeKeeper.GoalieSkill = (byte)GetTeamKeeperSkill(_homeTeamIndex);
        _awayKeeper.GoalieSkill = (byte)GetTeamKeeperSkill(_awayTeamIndex);
        _homeKeeper.GoalieState = KeeperState.Normal;
        _awayKeeper.GoalieState = KeeperState.Normal;
        _homeKeeper.GoaliePlayerDownTimer = 0;
        _awayKeeper.GoaliePlayerDownTimer = 0;
        _homeKeeper.GoalieClaimedTimer = 0;
        _awayKeeper.GoalieClaimedTimer = 0;
    }

    // Pull the per-team goalkeeper skill (0..7) from TeamRecord. SWOS conventionally
    // stores the goalkeeper as player index 0; we use Goalkeeper-of-team avg if the
    // dedicated stat isn't accessible yet.
    private int GetTeamKeeperSkill(int teamIdx)
    {
        if (teamIdx < 0 || teamIdx >= _allTeams.Count) return 4;
        var team = _allTeams[teamIdx];
        if (team.Players.Count == 0) return 4;
        // Player 0 = goalkeeper in SWOS convention; use his Control or Heading stat as
        // an approximation until we wire a dedicated GoalkeeperSkill field. Skills are
        // 1..7 in TeamRecord; clamp into 0..7 dive-table range.
        var gk = team.Players[0];
        int s = (gk.Control + gk.Heading) / 2;
        if (s < 0) s = 0;
        if (s > 7) s = 7;
        return s;
    }

    private void EnterHalfTime() => _match.EnterPhase(MatchPhase.HalfTime);
    private void EnterFullTime() => _match.EnterPhase(MatchPhase.FullTime);

    // === Set pieces =========================================================
    //
    // Real SWOS positions one of the team's players at the set-piece spot and waits
    // on input. We don't have a walks-to-ball state machine yet — see
    // docs/porting/set-pieces.md TODO — so we freeze the ball at the correct spot
    // and auto-release after a fixed hold timer.

    // SWOS set-piece kick magnitudes (raw Q8.8). Reuse BallSim's existing
    // NormalKickPower / HighKickPower constants — see docs/porting/ball-physics.md.
    // Throw-in is the weakest (a hand-throw); corner the strongest (a long lob to
    // the penalty spot); goal-kick a long upfield punt. Hand-tuned within the
    // SWOS power band — not from a specific source citation.

    private void EnterThrowIn(bool takingTeamIsPlayer, int ballX, int ballY)
    {
        _match.EnterPhase(MatchPhase.ThrowIn);
        _match.TakingTeamIsPlayer = takingTeamIsPlayer;

        // Clamp ball to the touchline at the Y where it crossed. SWOS uses three
        // bands (Forward / Center / Back) per side — we keep the exact crossing Y
        // because that's where the throw will be visible on screen.
        int clampedY = System.Math.Clamp(ballY, GoalLineNorth + 8, GoalLineSouth - 8);
        int touchlineX = (ballX < FieldLeft) ? FieldLeft : FieldRight;
        _ball = BallState.At(touchlineX, clampedY);

        // Auto-release: throw the ball horizontally into the pitch. Positive X if
        // taking from the left touchline, negative X if from the right.
        int sign = (touchlineX == FieldLeft) ? +1 : -1;
        const int ThrowPowerRaw = 1500;  // weaker than a kick — it's a throw-in
        _match.SetPieceKickVxRaw = sign * ThrowPowerRaw;
        _match.SetPieceKickVyRaw = 0;
        _match.SetPieceKickVzRaw = BallSim.KickLoft / 2;  // tiny arc
    }

    private void EnterCornerKick(bool takingTeamIsPlayer, bool atNorthGoal, bool cornerIsLeft)
    {
        _match.EnterPhase(MatchPhase.CornerKick);
        _match.TakingTeamIsPlayer = takingTeamIsPlayer;

        // Corner flag positions ported from SWOS ball.cpp:3619-3663:
        //   Left  corners X = 86  → our X = 86 - PitchOffsetX(160) = -74
        //   Right corners X = 585 → our X = 585 - 160 = 425
        //   Upper corners Y = 134 → our Y = 134 - PitchOffsetY(288) = -154
        //   Lower corners Y = 764 → our Y = 764 - 288 = 476
        int cornerX = cornerIsLeft ? -74 : 425;
        int cornerY = atNorthGoal ? -154 : 476;
        _ball = BallState.At(cornerX, cornerY);

        // Aim the auto-release at roughly the penalty spot (~30 px in front of the
        // goal mouth, on the goal-line X centre). Reuse BallSim.HighKickPower (raw
        // 2688 = 10.5 px/tick) — corners are the lobbed kick.
        int targetX = KickoffBallX;
        int targetY = atNorthGoal ? (GoalLineNorth + 100) : (GoalLineSouth - 100);
        int dxPx = targetX - cornerX;
        int dyPx = targetY - cornerY;
        ComputeKickVector(dxPx, dyPx, BallSim.HighKickPower,
            out _match.SetPieceKickVxRaw, out _match.SetPieceKickVyRaw);
        _match.SetPieceKickVzRaw = BallSim.KickLoft;  // full lob for the cross
    }

    private void EnterGoalKick(bool takingTeamIsPlayer, bool atNorthGoal)
    {
        _match.EnterPhase(MatchPhase.GoalKick);
        _match.TakingTeamIsPlayer = takingTeamIsPlayer;

        // Ball positioned ~60 px in front of the goal mouth — outside the keeper's
        // 24 px catch radius (keeper sits 20 px in from the goal line). If we spawn
        // closer, BallSim immediately re-glues the ball on release and the kick is
        // cancelled. Bug fix from earlier session.
        int kickerY = atNorthGoal ? (GoalLineNorth + 60) : (GoalLineSouth - 60);
        _ball = BallState.At(KickoffBallX, kickerY);

        // Aim upfield — toward the opposite goal mouth. Use NormalKickPower
        // (raw 2560 = 10.0 px/tick) — a flat-ish long punt.
        int targetX = KickoffBallX;
        int targetY = atNorthGoal ? GoalLineSouth - 80 : GoalLineNorth + 80;
        int dxPx = targetX - KickoffBallX;
        int dyPx = targetY - kickerY;
        ComputeKickVector(dxPx, dyPx, BallSim.NormalKickPower + 200,  // small extra heft
            out _match.SetPieceKickVxRaw, out _match.SetPieceKickVyRaw);
        _match.SetPieceKickVzRaw = BallSim.KickLoft;  // lob to clear the keeper
    }

    // Splits a unit direction (dxPx, dyPx) into Q8.8 raw velocities scaled by
    // powerRaw, matching BallSim.Tick's diagonal-normalisation rule (kick speed
    // is direction-independent: cardinal × 256/256, diagonal × 181/256 each axis).
    private static void ComputeKickVector(int dxPx, int dyPx, int powerRaw, out int vxRaw, out int vyRaw)
    {
        // Build a length-256 unit vector in fixed-point so the velocity magnitude
        // matches powerRaw regardless of the (dxPx, dyPx) angle. SqrtInt is a
        // local deterministic integer sqrt.
        long magSq = (long)dxPx * dxPx + (long)dyPx * dyPx;
        if (magSq == 0)
        {
            vxRaw = 0;
            vyRaw = 0;
            return;
        }
        int mag = BallSim.IntSqrt(magSq);  // deterministic — netcode-safe
        if (mag <= 0)
        {
            vxRaw = 0;
            vyRaw = 0;
            return;
        }
        vxRaw = dxPx * powerRaw / mag;
        vyRaw = dyPx * powerRaw / mag;
    }

    // Settles all 4 players + the ball on the spot for the duration of the set-
    // piece hold. Keepers continue their idle StepKeeper; outfielders simply hold
    // position (no input). The ball is frozen at the position set by EnterX.
    private void TickSetPieceHold()
    {
        // Outfielders idle. Note: SWOS would walk the nearest player to the ball
        // — see TODO in docs/porting/set-pieces.md.
        PlayerSim.Tick(ref _player, InputState.Idle, _player.EffectiveSpeed);
        PlayerSim.Tick(ref _opponent, InputState.Idle, _opponent.EffectiveSpeed);
        StepKeeper(ref _homeKeeper, isNorthEnd: true);
        StepKeeper(ref _awayKeeper, isNorthEnd: false);

        // Ball frozen — nail velocity to zero, leave position alone.
        _ball.VelocityX = Fixed.FromInt(0);
        _ball.VelocityY = Fixed.FromInt(0);
        _ball.VelocityZ = Fixed.FromInt(0);
        _ball.Z = Fixed.FromInt(0);
    }

    // Releases the held set-piece ball: empty BallSim influence span (no possession),
    // then write the queued kick velocities directly into the ball. After this the
    // phase returns to Play and the ball flies under normal physics.
    private void ReleaseSetPiece()
    {
        // Empty influence span — no one is in possession, so BallSim runs straight
        // physics. Per the brief: "call BallSim.Tick with empty influences ... then
        // add the kick velocity to _ball.VelocityX/Y directly post-tick".
        BallSim.Tick(ref _ball, System.ReadOnlySpan<PlayerInfluence>.Empty);
        _ball.VelocityX = Fixed.FromRaw(_match.SetPieceKickVxRaw);
        _ball.VelocityY = Fixed.FromRaw(_match.SetPieceKickVyRaw);
        _ball.VelocityZ = Fixed.FromRaw(_match.SetPieceKickVzRaw);
        // Update last-touched so the next out-of-bounds award flips correctly.
        _match.LastTouchedByPlayer = _match.TakingTeamIsPlayer;
        _match.EnterPhase(MatchPhase.Play);
    }

    // Half 1: player south (Y=240, attacks N), opponent north (Y=80, attacks S).
    // Half 2: swapped — player north, opponent south.
    private static (int px, int py, int ox, int oy, int bx, int by) KickoffPositionsForHalf(byte half)
    {
        if (half == 1)
            return (KickoffPlayerX, KickoffPlayerY, KickoffOpponentX, KickoffOpponentY, KickoffBallX, KickoffBallY);
        return (KickoffPlayerX, KickoffOpponentY, KickoffOpponentX, KickoffPlayerY, KickoffBallX, KickoffBallY);
    }

    // AI's own goal / enemy goal flipped at half-time. In h1 AI is on the north side,
    // defends N goal, shoots toward S goal.
    private static (int ownGoalY, int enemyGoalY) AiGoalsForHalf(byte half) =>
        half == 1
            ? (GoalLineNorth, GoalLineSouth)
            : (GoalLineSouth, GoalLineNorth);

    // P1's own/enemy goals — mirror of AI's. Used by Demo mode where P1 is also AI-driven.
    private static (int ownGoalY, int enemyGoalY) PlayerGoalsForHalf(byte half) =>
        half == 1
            ? (GoalLineSouth, GoalLineNorth)
            : (GoalLineNorth, GoalLineSouth);

    // === Match setup (menu → match wiring) ===

    // Rebuilds player + opponent tile caches and (if sprites already exist) refreshes
    // their textures so the new kit colours show up immediately in the menu preview.
    private void ApplyMatchSetup()
    {
        if (_baseAtlas is null || _allTeams.Count == 0) return;

        // Change-kit selection (task #185). SWOS compares the two teams' kits and,
        // if they look too similar, switches the away side to its real alternate
        // (change) kit — NOT a synthetic recolour. KitClash.Select ports the
        // original 4-combination test (home/away × primary/secondary, threshold
        // 11) from swos.asm:36165-37237. Home = top team, away = bottom team.
        var homeTeam = _allTeams[_homeTeamIndex];
        var awayTeam = _allTeams[_awayTeamIndex];
        var (homeKit, awayKit, kitCombo) = OpenSwos.Assets.KitClash.Select(
            homeTeam.HomeKit, homeTeam.AwayKit, awayTeam.HomeKit, awayTeam.AwayKit);
        if (kitCombo != 0)
            GD.Print($"[kit-clash] home='{homeTeam.Name.Trim()}' away='{awayTeam.Name.Trim()}' " +
                     $"combo={kitCombo} homeKit=[{string.Join(",", homeKit)}] awayKit=[{string.Join(",", awayKit)}]");

        var homeAtlas = _baseAtlas.WithKitRecolour(homeKit);
        var awayAtlas = _baseAtlas.WithKitRecolour(awayKit);
        // Per-team keeper kit: shorts/socks come from team's primary colours
        // (TeamRecord.GoalkeeperKit, synthesised in AssetMapping). Replaces the old
        // single shared yellow keeper.
        byte[] homeKeeperKit = (_homeTeamIndex >= 0 && _homeTeamIndex < _allTeams.Count
                                && _allTeams[_homeTeamIndex].GoalkeeperKit.Length >= 5)
            ? _allTeams[_homeTeamIndex].GoalkeeperKit : FallbackKeeperKit;
        byte[] awayKeeperKit = (_awayTeamIndex >= 0 && _awayTeamIndex < _allTeams.Count
                                && _allTeams[_awayTeamIndex].GoalkeeperKit.Length >= 5)
            ? _allTeams[_awayTeamIndex].GoalkeeperKit : FallbackKeeperKit;
        var homeKeeperAtlas = _baseAtlas.WithKitRecolour(homeKeeperKit);
        var awayKeeperAtlas = _baseAtlas.WithKitRecolour(awayKeeperKit);
        _atlas = homeAtlas;

        // Bake every kit in every FACE variant (0=WHITE, 1=GINGER, 2=BLACK).
        // WithFaceRecolour remaps the skin/hair palette slots per the
        // original's per-face convert tables (swos.asm:218199-218215) — the
        // render loop then picks the dictionary matching each player's Face
        // (FaceForSlot). Composes with the kit recolour (disjoint slots).
        for (int face = 0; face < OpenSwos.Assets.KitPalette.FaceCount; face++)
        {
            var homeF       = homeAtlas.WithFaceRecolour(face);
            var awayF       = awayAtlas.WithFaceRecolour(face);
            var homeKeeperF = homeKeeperAtlas.WithFaceRecolour(face);
            var awayKeeperF = awayKeeperAtlas.WithFaceRecolour(face);

            _playerTilesFace[face].Clear();
            _opponentTilesFace[face].Clear();
            _homeKeeperTilesFace[face].Clear();
            _awayKeeperTilesFace[face].Clear();

            foreach (var d in System.Enum.GetValues<Direction>())
            {
                foreach (var cell in new[] {
                    PlayerFrames.RunFrame(d, 0), PlayerFrames.RunFrame(d, 1), PlayerFrames.RunFrame(d, 2),
                    PlayerFrames.Standing(d), PlayerFrames.Slide(d), PlayerFrames.Fallen(d),
                })
                {
                    CacheTileInto(_playerTilesFace[face], homeF, cell);
                    CacheTileInto(_opponentTilesFace[face], awayF, cell);
                    CacheTileInto(_homeKeeperTilesFace[face], homeKeeperF, cell);
                    CacheTileInto(_awayKeeperTilesFace[face], awayKeeperF, cell);
                }
            }

            // Extended (non-16×16) frames — throw-in hold/release + goal cheer
            // (CJCTEAM* bands, bug #157c). Cached for every kit so any player,
            // keeper included, can take a throw-in.
            foreach (var cell in PlayerFrames.AllOutfieldExtCells())
            {
                CacheExtInto(_playerTilesFace[face], homeF, cell);
                CacheExtInto(_opponentTilesFace[face], awayF, cell);
                CacheExtInto(_homeKeeperTilesFace[face], homeKeeperF, cell);
                CacheExtInto(_awayKeeperTilesFace[face], awayKeeperF, cell);
            }

            // Injured-writhe tiles (CJCTEAM* row 2, cols 7-10). Baked for every
            // kit so any downed player lies in his own colours.
            foreach (var cell in PlayerFrames.AllWritheCells())
            {
                CacheExtInto(_playerTilesFace[face], homeF, cell);
                CacheExtInto(_opponentTilesFace[face], awayF, cell);
                CacheExtInto(_homeKeeperTilesFace[face], homeKeeperF, cell);
                CacheExtInto(_awayKeeperTilesFace[face], awayKeeperF, cell);
            }

            // Goalkeeper dive + catch frames from the dedicated CJCTEAMG sheet
            // (bug #155), recoloured with each side's keeper kit + face.
            if (_goalieBaseAtlas is not null)
            {
                var homeGoalieF = _goalieBaseAtlas.WithKitRecolour(homeKeeperKit).WithFaceRecolour(face);
                var awayGoalieF = _goalieBaseAtlas.WithKitRecolour(awayKeeperKit).WithFaceRecolour(face);
                foreach (var cell in PlayerFrames.AllGoalieExtCells())
                {
                    CacheExtInto(_homeKeeperTilesFace[face], homeGoalieF, cell);
                    CacheExtInto(_awayKeeperTilesFace[face], awayGoalieF, cell);
                }
            }
        }

        // Refresh existing sprite textures so menu preview reflects the choice instantly.
        if (_playerSprite is not null)
        {
            var cellP = PlayerFrames.Standing(_player.Facing);
            if (_playerTiles.TryGetValue(cellP, out var texP)) _playerSprite.Texture = texP;
        }
        if (_opponentSprite is not null)
        {
            var cellO = PlayerFrames.Standing(_opponent.Facing);
            if (_opponentTiles.TryGetValue(cellO, out var texO)) _opponentSprite.Texture = texO;
        }
    }

    // First-run help: shown only when NO SWOS graphics could be resolved or imported.
    // Draws a plain full-screen overlay telling the user where to drop their ADFs.
    // Uses a screen-space CanvasLayer + Label (no SWOS assets needed — those are what's
    // missing). Safe to call once; the visual load then returns gracefully.
    private void ShowFirstRunMessage()
    {
        var layer = new CanvasLayer { Layer = 100 };
        // Full-screen help overlay in the ×2 menu design space (576×408 scaled to
        // the native 1152×816 viewport) — matches the menu layer so the wrapped
        // paragraph has the same width the menu client uses.
        layer.Transform = Transform2D.Identity.Scaled(new Vector2(MenuScale, MenuScale));
        AddChild(layer);

        var bg = new ColorRect
        {
            Color = new Color(0.04f, 0.10f, 0.04f, 1f),
            Size = new Vector2(MenuViewportWidth, MenuViewportHeight),
        };
        layer.AddChild(bg);

        string inDir = AmigaImporter.InputFolder;
        string outDir = AmigaImporter.OutputFolder;

        var label = new Label
        {
            Text =
                "No SWOS game data found.\n\n" +
                "OpenSWOS needs Sensible World of Soccer 96/97 (Amiga edition).\n" +
                "Two options — then restart the game:\n\n" +
                "  (a) Drop your Amiga floppy images (*.adf) into:\n" +
                $"        {inDir}\n" +
                "      OpenSWOS extracts what it needs on the next start.\n\n" +
                "  (b) Already have the loose game files (e.g. a WHDLoad install)?\n" +
                "      Copy them into:\n" +
                $"        {outDir}\n\n" +
                "Optional: drop the PC (DOS) DATA folder into 'original_swos_pc/' for a\n" +
                "slightly larger team list — not required, and it doesn't change graphics.\n\n" +
                "Earlier SWOS editions (94/95 etc.) are NOT compatible.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Position = new Vector2(0, 0),
            Size = new Vector2(MenuViewportWidth, MenuViewportHeight),
            Modulate = Colors.White,
        };
        layer.AddChild(label);

        GD.Print("[Main] First-run message shown — no SWOS data available.");
    }

    // Replaces (or creates) the pitch background sprite from SWCPICH{idx}.MAP. Called by
    // _Ready (initial load) and TickMenu (when the user cycles pitch variants).
    private void LoadPitchVariant(int idx)
    {
        string grafsDir = DataPaths.AmigaGrafsDir();
        string pitchPath = grafsDir.Length > 0
            ? System.IO.Path.Combine(grafsDir, $"SWCPICH{idx}.MAP")
            : "";

        if (pitchPath.Length == 0 || !System.IO.File.Exists(pitchPath))
        {
            GD.PrintErr($"Pitch variant {idx} not found at {pitchPath}");
            if (_pitchSprite is null) BuildPlaceholderPitch();
            return;
        }

        try
        {
            var pitch = AmigaPitch.Load(pitchPath);
            var pitchTex = ImageTexture.CreateFromImage(pitch.ToImage());
            if (_pitchSprite is not null)
            {
                _pitchSprite.Texture = pitchTex;
            }
            else
            {
                _pitchSprite = new Sprite2D
                {
                    Texture = pitchTex,
                    Centered = false,
                    // +PitchBitmapWorldY: bitmap top-left lives at SWOS world (0, 16),
                    // not (0, 0) — see the constant's comment for the swos-port cites.
                    Position = new Vector2(PitchOffsetX, PitchOffsetY + PitchBitmapWorldY),
                    TextureFilter = TextureFilterEnum.Nearest,
                    ZIndex = -10,
                };
                AddChild(_pitchSprite);
            }
            GD.Print($"Loaded pitch variant {idx}: {pitch.Width}×{pitch.Height}");
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"Pitch load failed: {ex.Message} — falling back to placeholder");
            if (_pitchSprite is null) BuildPlaceholderPitch();
        }
    }

    private void CacheTileInto(
        System.Collections.Generic.Dictionary<(int, int), ImageTexture> cache,
        AmigaSpriteAtlas srcAtlas, (int col, int row) cell)
    {
        if (cache.ContainsKey(cell)) return;
        cache[cell] = ImageTexture.CreateFromImage(srcAtlas.GetTile(cell.col, cell.row));
    }

    // Cache an extended (synthetic-cell) frame — throw-in/cheer bands of
    // CJCTEAM* or dive/catch bands of CJCTEAMG. See PlayerFrames.ExtRect.
    private void CacheExtInto(
        System.Collections.Generic.Dictionary<(int, int), ImageTexture> cache,
        AmigaSpriteAtlas srcAtlas, (int col, int row) cell)
    {
        if (cache.ContainsKey(cell)) return;
        var (x, y, w, h) = PlayerFrames.ExtRect(cell);
        cache[cell] = ImageTexture.CreateFromImage(srcAtlas.GetRegion(x, y, w, h));
    }

    // Builds the two transparent goal-net overlay sprites from CJCBITS.RAW
    // (bug #156 — bottom-goal net must draw ABOVE the keeper). Art pieces
    // (index-0 transparent, measured on the decoded 320×256 sheet):
    //   piece A (0,64) 74×7  — top-goal net roof strip (behind the crossbar)
    //   piece B (0,80) 74×28 — bottom-goal net (crossbar + hanging net)
    // Placement: each piece sits pixel-exactly over its baked twin in the
    // pitch bitmap — piece A at bitmap (300,90), piece B at (300,735); both
    // verified by binary cross-correlation against SWCPICH1 (100% white-pixel
    // match). Z-order: the original Y-sorts the goal sprites with the players
    // at kTopGoalY=129 / kBottomGoalY=778 (swos-port gameSprites.cpp:85-94),
    // and our players use ZIndex = worldY(Godot) + 1000, so the overlays get
    // (129|778) + PitchOffsetY + 1000. Works identically for the legacy sim,
    // which shares the same world mapping.
    private void CreateGoalOverlays(AmigaSpriteAtlas bitsAtlas)
    {
        _goalOverlayTop = new Sprite2D
        {
            Texture = ImageTexture.CreateFromImage(bitsAtlas.GetRegion(0, 64, 74, 7)),
            Centered = false,
            Position = new Vector2(300 + PitchOffsetX, 90 + PitchOffsetY + PitchBitmapWorldY),
            TextureFilter = TextureFilterEnum.Nearest,
            ZIndex = 129 + PitchOffsetY + 1000,
        };
        AddChild(_goalOverlayTop);
        _goalOverlayBottom = new Sprite2D
        {
            Texture = ImageTexture.CreateFromImage(bitsAtlas.GetRegion(0, 80, 74, 28)),
            Centered = false,
            Position = new Vector2(300 + PitchOffsetX, 735 + PitchOffsetY + PitchBitmapWorldY),
            TextureFilter = TextureFilterEnum.Nearest,
            ZIndex = 778 + PitchOffsetY + 1000,
        };
        AddChild(_goalOverlayBottom);
        GD.Print("Goal net overlays created (top strip 74x7, bottom net 74x28)");
    }

    private const int PitchOffsetX = -160;
    private const int PitchOffsetY = -288;
    // Visible pitch dimensions: 42×53 cells × 16 px = 672×848. Rows 0 and 54 of the on-
    // disk map are off-screen padding in original SWOS and are dropped by PitchFile.Render.
    private const int PitchWidth = 672;
    private const int PitchHeight = 848;
    // The rendered pitch bitmap does NOT start at SWOS world (0, 0). The on-disk tile
    // map has 55 rows; renderers keep rows 1..53 (PitchFile.Render here, and
    // external/swos-port/assets/compileAssets.py:331 `for i in range(1, 54)`).
    // The original draws map row (cameraY/16 - 1) at the top of the screen with the
    // camera clamped to worldY >= 16 — external/swos-port/src/game/pitch/pitch.cpp:131-136
    // (`row = yDiv.quot - 1`) + clipPitch pitch.cpp:389-391 (`if (y < kSwosPatternSize)`).
    // Net effect: the 672×848 bitmap's top-left sits at SWOS world (0, 16), i.e.
    // bitmapY = worldY - 16. Empirically verified: the halfway line is bitmap row 433
    // in SWCPICH1.MAP = kPitchCenterY(449) - 16 (swos-port pitchConstants.h:4).
    // Without this shift every sprite renders 16 px too LOW relative to the pitch art.
    private const int PitchBitmapWorldY = 16;
    // Ball + ball-shadow sprite anchor. All four ball frames (ordinals 1179-1182) and
    // the shadow (1183) are 4×4 images with center (1, 3) — swos-port generated
    // spriteDatabase.h (widthF=4, heightF=4, centerXF=1.0, centerYF=3.0). The draw
    // path places the image top-left at (x - centerX, y - z - centerY) —
    // external/swos-port/src/sprites/renderSprites.cpp:44-52 — so the ball's ground
    // point is 1 px in from the left and on the bottom pixel row of the 4×4 image.
    // Our Amiga ball (CJCBITS.RAW cell (0,3)) is also exactly 4×4 at the cell's
    // top-left, so the PC anchor applies to the trimmed tile 1:1.
    private const int BallSpriteCenterX = 1;
    private const int BallSpriteCenterY = 3;

    private void BuildPlaceholderPitch()
    {
        // Green field — same world origin as the real bitmap (top-left at world (0, 16)).
        AddChild(new ColorRect
        {
            Color = new Color(0.12f, 0.45f, 0.18f),
            Size = new Vector2(PitchWidth, PitchHeight),
            Position = new Vector2(PitchOffsetX, PitchOffsetY + PitchBitmapWorldY),
            ZIndex = -10,
        });

        int px = PitchOffsetX, py = PitchOffsetY + PitchBitmapWorldY;
        int pw = PitchWidth, ph = PitchHeight;
        const int LineW = 2;

        // Touchlines (pitch boundary). Inset 30 px from the grass edge to leave a margin
        // — the actual playable area on SWOS Amiga is interior to the green.
        const int Margin = 30;
        Line(px + Margin, py + Margin, pw - 2 * Margin, LineW);                 // top
        Line(px + Margin, py + ph - Margin - LineW, pw - 2 * Margin, LineW);    // bottom
        Line(px + Margin, py + Margin, LineW, ph - 2 * Margin);                 // left
        Line(px + pw - Margin - LineW, py + Margin, LineW, ph - 2 * Margin);    // right

        // Halfway line
        Line(px + Margin, py + ph / 2 - LineW / 2, pw - 2 * Margin, LineW);

        // Top penalty box
        int boxW = 240, boxH = 90;
        int boxX = px + (pw - boxW) / 2;
        Line(boxX,                py + Margin,           LineW, boxH);
        Line(boxX + boxW - LineW, py + Margin,           LineW, boxH);
        Line(boxX,                py + Margin + boxH,    boxW,  LineW);

        // Bottom penalty box
        int botBoxY = py + ph - Margin - boxH;
        Line(boxX,                botBoxY,         LineW, boxH);
        Line(boxX + boxW - LineW, botBoxY,         LineW, boxH);
        Line(boxX,                botBoxY,         boxW,  LineW);

        // Top goal
        int goalW = 80, goalH = 8;
        Line(px + (pw - goalW) / 2, py + Margin - goalH, goalW, goalH);

        // Bottom goal
        Line(px + (pw - goalW) / 2, py + ph - Margin, goalW, goalH);
    }

    private void Line(int x, int y, int w, int h)
    {
        AddChild(new ColorRect
        {
            Color = Colors.White,
            Size = new Vector2(w, h),
            Position = new Vector2(x, y),
            ZIndex = -5,
        });
    }

    private static void TryLoadTeams(IAssetSource source)
    {
        int teamCount = 0;
        int playerCount = 0;
        TeamRecord? first = null;
        foreach (var team in source.LoadAllTeams())
        {
            teamCount++;
            playerCount += team.Players.Count;
            first ??= team;
        }
        if (teamCount == 0)
        {
            GD.Print($"[{source.Variant}] no teams loaded (data dir missing or empty)");
            return;
        }
        GD.Print($"[{source.Variant}] {teamCount} teams, {playerCount} players. First: {first?.Name} / {first?.Coach}");
    }

    // === Retro fullscreen (display) input + apply ==============================
    // Display hotkeys live here in _UnhandledInput (Main had none before) so they
    // are edge-triggered once per keypress (k.Pressed && !k.Echo) and stay fully
    // independent of the per-tick physical-key gameplay polling elsewhere.
    //   F11        — cycle Windowed -> Fill -> Integer -> Windowed ...
    //   Alt+Enter  — toggle Windowed <-> last-used fullscreen mode.
    public override void _UnhandledInput(InputEvent e)
    {
        // --- Touch routing (only when the touch UI is active) ---
        // Handled BEFORE the key guard below. With stretch=viewport, the touch
        // Position is already in the 1152×816 root viewport space: the menu wants
        // 576×408 design space (divide by MenuScale), the match overlay is
        // identity-scale screen space (raw pos, no divide).
        if (_touchUi)
        {
            if (e is InputEventScreenTouch t)
            {
                if (_appState == AppState.Menu && _menuClient is not null)
                {
                    // Overlay controls (d-pad / CONFIRM / BACK / toggle) get first
                    // crack in screen space; anything they don't consume falls
                    // through to MenuClient's tap-to-select (both coexist).
                    if (!HandleMenuTouch(t.Position, t.Pressed, t.Index))
                        _menuClient.HandleTouch(MenuTouchPos(t.Position), t.Pressed);
                }
                else
                    HandleMatchTouch(t.Position, t.Pressed, t.Index);
                GetViewport().SetInputAsHandled();
                return;
            }
            if (e is InputEventScreenDrag d)
            {
                if (_appState == AppState.Menu && _menuClient is not null)
                {
                    // A drag on the menu d-pad steers navigation; otherwise pass
                    // it to the menu as a move (which it mostly ignores).
                    if (d.Index == _menuStickFinger)
                        HandleMenuDrag(d.Position, d.Index);
                    else
                        _menuClient.HandleTouch(MenuTouchPos(d.Position), true);
                }
                else
                    HandleMatchDrag(d.Position, d.Index);
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (e is not InputEventKey k || !k.Pressed || k.Echo) return;

        // Mobile-only: forward physical key events into the active menu's text field
        // (soft keyboard). Strict no-op on desktop (OS.HasFeature("mobile") == false).
        if (_menuClient is not null && OS.HasFeature("mobile"))
        {
            if (_menuClient.FeedTextInputEvent(k)) { GetViewport().SetInputAsHandled(); return; }
        }

        if (k.Keycode == Key.F11)
        {
            CycleDisplayModeInternal();
            GetViewport().SetInputAsHandled();
        }
        else if ((k.Keycode == Key.Enter || k.Keycode == Key.KpEnter) && k.AltPressed)
        {
            ToggleFullscreenInternal();
            GetViewport().SetInputAsHandled();
        }
    }

    // F11: Windowed -> FullscreenFill -> FullscreenInteger -> Windowed ...
    private void CycleDisplayModeInternal()
    {
        _displayMode = _displayMode switch
        {
            DisplayMode.Windowed => DisplayMode.FullscreenFill,
            DisplayMode.FullscreenFill => DisplayMode.FullscreenInteger,
            _ => DisplayMode.Windowed,
        };
        if (_displayMode != DisplayMode.Windowed) _lastFullscreenMode = _displayMode;
        ApplyDisplayMode();
        SaveSettings();
    }

    // Alt+Enter: windowed -> last-used fullscreen; any fullscreen -> windowed.
    private void ToggleFullscreenInternal()
    {
        _displayMode = _displayMode == DisplayMode.Windowed ? _lastFullscreenMode : DisplayMode.Windowed;
        if (_displayMode != DisplayMode.Windowed) _lastFullscreenMode = _displayMode;
        ApplyDisplayMode();
        SaveSettings();
    }

    // Push _displayMode onto the OS Window. Borderless native-res fullscreen only
    // (never ExclusiveFullscreen) so the desktop resolution/icon layout is never
    // disturbed. Scale stays Viewport+Keep (project stretch mode); only the
    // stretch flips Fractional (fill) vs Integer (pixel-perfect). No-op headless.
    private void ApplyDisplayMode()
    {
        try
        {
            if (DisplayServer.GetName() == "headless") return;
            var win = GetWindow();
            if (win is null) return;

            win.ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
            // Android (#231): Expand makes the side space part of the root viewport
            // so the touch overlay can be drawn there; the extra width is then
            // masked with black bars and the content is centred, so the visible
            // image stays the 4:3 Keep-mode one. Every other platform keeps Keep.
            win.ContentScaleAspect = _expandMode
                ? Window.ContentScaleAspectEnum.Expand
                : Window.ContentScaleAspectEnum.Keep;
            win.ContentScaleSize = new Vector2I(ViewportWidth * RenderScale, ViewportHeight * RenderScale);

            switch (_displayMode)
            {
                case DisplayMode.Windowed:
                    win.Mode = Window.ModeEnum.Windowed;
                    win.ContentScaleStretch = Window.ContentScaleStretchEnum.Fractional;
                    break;
                case DisplayMode.FullscreenFill:
                    win.Mode = Window.ModeEnum.Fullscreen;   // borderless, native res
                    win.ContentScaleStretch = Window.ContentScaleStretchEnum.Fractional;
                    break;
                case DisplayMode.FullscreenInteger:
                    win.Mode = Window.ModeEnum.Fullscreen;   // borderless, native res
                    win.ContentScaleStretch = Window.ContentScaleStretchEnum.Integer;
                    break;
            }
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"ApplyDisplayMode failed: {ex.Message}");
        }
    }

    // OPTIONS-row + F11 share this label / cycle (exposed via IMenuHost).
    private static string DisplayModeToLabel(DisplayMode m) => m switch
    {
        DisplayMode.Windowed => "WINDOWED",
        DisplayMode.FullscreenFill => "FULL FILL",
        DisplayMode.FullscreenInteger => "FULL INTEGER",
        _ => "?",
    };

    // === Settings persistence ==================================================
    // Plain-text JSON-ish blob in Godot user data dir (per-platform):
    //   Windows: %APPDATA%/Godot/app_userdata/<project_name>/settings.json
    //   Linux:   ~/.local/share/godot/app_userdata/<project_name>/settings.json
    //   macOS:   ~/Library/Application Support/Godot/app_userdata/<project_name>/
    // Only stores values we want to persist between launches.

    private void LoadSettings()
    {
        try
        {
            using var f = Godot.FileAccess.Open(SettingsPath, Godot.FileAccess.ModeFlags.Read);
            if (f is null) return;
            string text = f.GetAsText();
            var m = System.Text.RegularExpressions.Regex.Match(text,
                "\"gameSpeedScale\"\\s*:\\s*([0-9]+\\.?[0-9]*)");
            if (m.Success && double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v))
            {
                _gameSpeedScale = System.Math.Round(
                    System.Math.Clamp(v, SpeedScaleMin, SpeedScaleMax), 2);
                GD.Print($"Settings loaded: gameSpeedScale = x{_gameSpeedScale:0.00}");
            }
            // useSwosPort is no longer persisted or loaded (task #183) — the port
            // is the only gameplay path, hardcoded true at the field declaration.

            // Skill scaling (task #196): faithful ScaleSkill price/team-value
            // feedback. Default ON; only an explicit "false" turns it off.
            var ms = System.Text.RegularExpressions.Regex.Match(text,
                "\"skillScaling\"\\s*:\\s*(true|false)");
            if (ms.Success)
            {
                OpenSwos.Sim.Port.SkillScaling.Enabled = ms.Groups[1].Value == "true";
                GD.Print($"Settings loaded: skillScaling = {OpenSwos.Sim.Port.SkillScaling.Enabled}");
            }
            // "All player teams equal" equalizer (original gameplay option; off
            // by default like the original).
            var me = System.Text.RegularExpressions.Regex.Match(text,
                "\"allTeamsEqual\"\\s*:\\s*(true|false)");
            if (me.Success)
                OpenSwos.Sim.Port.SkillScaling.AllPlayerTeamsEqual = me.Groups[1].Value == "true";
            // "Show pre-match menus" (versus + lineups) — default ON.
            var mp = System.Text.RegularExpressions.Regex.Match(text,
                "\"preMatchMenus\"\\s*:\\s*(true|false)");
            if (mp.Success)
                _showPreMatchMenus = mp.Groups[1].Value == "true";
            // Energy bar overlay (default ON) + master fatigue effect (default OFF).
            var mEnergyBar = System.Text.RegularExpressions.Regex.Match(text,
                "\"energyBar\"\\s*:\\s*(true|false)");
            if (mEnergyBar.Success) _energyBar = mEnergyBar.Groups[1].Value == "true";
            var mFatigueSim = System.Text.RegularExpressions.Regex.Match(text,
                "\"fatigueSim\"\\s*:\\s*(true|false)");
            if (mFatigueSim.Success) _fatigueSim = mFatigueSim.Groups[1].Value == "true";
            // On-screen touch controls visible (default ON). Only meaningful on
            // touchscreens; harmless to load on desktop.
            var mTouchOv = System.Text.RegularExpressions.Regex.Match(text,
                "\"touchOverlay\"\\s*:\\s*(true|false)");
            if (mTouchOv.Success) _touchOverlayOn = mTouchOv.Groups[1].Value == "true";
            // Display mode: "windowed" / "fill" / "integer" (default windowed).
            // A persisted fullscreen mode also seeds _lastFullscreenMode so
            // Alt+Enter restores what the player last used.
            var md = System.Text.RegularExpressions.Regex.Match(text,
                "\"displayMode\"\\s*:\\s*\"(windowed|fill|integer)\"");
            if (md.Success)
            {
                _displayMode = md.Groups[1].Value switch
                {
                    "fill" => DisplayMode.FullscreenFill,
                    "integer" => DisplayMode.FullscreenInteger,
                    _ => DisplayMode.Windowed,
                };
                if (_displayMode != DisplayMode.Windowed) _lastFullscreenMode = _displayMode;
                GD.Print($"Settings loaded: displayMode = {DisplayModeToLabel(_displayMode)}");
            }
            // Sound source: "pc" / "amiga" (default pc). Resolved against
            // availability at match start, so a stale value degrades gracefully.
            var mss = System.Text.RegularExpressions.Regex.Match(text,
                "\"soundSource\"\\s*:\\s*\"(pc|amiga)\"");
            if (mss.Success)
            {
                _soundSource = mss.Groups[1].Value == "amiga"
                    ? OpenSwos.Audio.MatchAudio.SoundSource.Amiga
                    : OpenSwos.Audio.MatchAudio.SoundSource.Pc;
                GD.Print($"Settings loaded: soundSource = {_soundSource}");
            }
            // Front-end menu music: "amiga" / "pc" / "custom" / "off". When the
            // key is ABSENT the field keeps its AMIGA default (fresh settings →
            // AMIGA). Resolved against availability at play time.
            var mmu = System.Text.RegularExpressions.Regex.Match(text,
                "\"menuMusic\"\\s*:\\s*\"(amiga|pc|custom|off)\"");
            if (mmu.Success)
            {
                _menuMusic = mmu.Groups[1].Value switch {
                    "pc" => OpenSwos.Audio.MenuMusic.MusicSource.Pc,
                    "custom" => OpenSwos.Audio.MenuMusic.MusicSource.Custom,
                    "off" => OpenSwos.Audio.MenuMusic.MusicSource.Off,
                    _ => OpenSwos.Audio.MenuMusic.MusicSource.Amiga,
                };
                GD.Print($"Settings loaded: menuMusic = {_menuMusic}");
            }
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"LoadSettings failed: {ex.Message}");
        }
    }

    private void SaveSettings()
    {
        // Harness runs (--menu-shot etc.) tweak speed/toggles for their own pacing;
        // those must never leak into the player's persisted settings (a leaked
        // gameSpeedScale=3.00 once made every real match run 3x too fast).
        if (_menuShotActive) return;
        try
        {
            using var f = Godot.FileAccess.Open(SettingsPath, Godot.FileAccess.ModeFlags.Write);
            if (f is null) return;
            string json = "{\"gameSpeedScale\":" +
                _gameSpeedScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"skillScaling\":" +
                (OpenSwos.Sim.Port.SkillScaling.Enabled ? "true" : "false") +
                ",\"allTeamsEqual\":" +
                (OpenSwos.Sim.Port.SkillScaling.AllPlayerTeamsEqual ? "true" : "false") +
                ",\"preMatchMenus\":" +
                (_showPreMatchMenus ? "true" : "false") +
                ",\"energyBar\":" +
                (_energyBar ? "true" : "false") +
                ",\"fatigueSim\":" +
                (_fatigueSim ? "true" : "false") +
                ",\"touchOverlay\":" +
                (_touchOverlayOn ? "true" : "false") +
                ",\"displayMode\":\"" +
                (_displayMode switch
                {
                    DisplayMode.FullscreenFill => "fill",
                    DisplayMode.FullscreenInteger => "integer",
                    _ => "windowed",
                }) + "\"" +
                ",\"soundSource\":\"" +
                (_soundSource == OpenSwos.Audio.MatchAudio.SoundSource.Amiga ? "amiga" : "pc") + "\"" +
                ",\"menuMusic\":\"" +
                (_menuMusic switch {
                    OpenSwos.Audio.MenuMusic.MusicSource.Pc => "pc",
                    OpenSwos.Audio.MenuMusic.MusicSource.Custom => "custom",
                    OpenSwos.Audio.MenuMusic.MusicSource.Off => "off",
                    _ => "amiga",
                }) + "\"" +
                "}";
            f.StoreString(json);
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"SaveSettings failed: {ex.Message}");
        }
    }
}
