using Godot;

namespace OpenSwos.Menu;

// One row on a menu screen. Three kinds:
//   Button — a label the player activates with FIRE (start match, open a screen).
//   Option — a label plus a value box with ‹ › arrows; LEFT/RIGHT change the value.
//   Label  — a static, non-selectable caption / spacer.
public enum EntryKind { Button, Option, Label }

public sealed class MenuEntry
{
    public string Id = "";
    public EntryKind Kind = EntryKind.Button;
    public MenuTheme.Style Style = MenuTheme.Style.Tool;
    public bool Big = true;
    // Gentle two-colour sheen animated on the button background each frame
    // (used by the HOME screen CAREER shortcut).
    public bool Shimmer;

    public System.Func<string> Label = () => "";
    public System.Func<string>? Value;        // Option only
    public System.Action? OnActivate;         // Button only
    public System.Action<int>? OnStep;        // Option only — delta already scaled by hold-repeat
    public bool FastScroll;                   // Option only — held ‹ › accelerates (team lists)

    // Explicit geometry (screen fills these during layout).
    public int X, Y, W, H;
    public int WidthOverride;   // 0 = default per-kind width

    public bool Selectable => Kind != EntryKind.Label;

    // Live Godot nodes (created lazily by the screen; disposed on rebuild).
    public Sprite2D? Bg, LabelSpr, ValueBg, ValueSpr, ArrowL, ArrowR;
}

// Everything a menu screen needs from the game. Implemented by Main (see
// Main.MenuHost.cs) so the menu module stays decoupled from gameplay internals.
public interface IMenuHost
{
    // Match-setup state (each Step* wraps, de-dupes home!=away, and re-applies
    // the live preview underneath the menu).
    int  TeamCount { get; }
    string TeamTag(int idx);                  // compact "NAME  AVGn" for value boxes
    string TeamName(int idx);                 // bare team name (uppercased)
    int  HomeIndex { get; }
    int  AwayIndex { get; }
    void StepHome(int delta);
    void StepAway(int delta);
    // Direct selection by master-list index (country-filtered pickers).
    void SetHomeTeam(int masterIndex);
    void SetAwayTeam(int masterIndex);
    void SwapTeams();

    string PitchLabel { get; }
    void StepPitch(int delta);

    string OpponentLabel { get; }
    void StepOpponent(int delta);

    string LengthLabel { get; }
    void StepLength(int delta);

    string SpeedLabel { get; }
    void StepSpeed(int delta);

    // Actions.
    void StartMatch();
    void QuitGame();

    // Team-config data (squad, kit, tactics).
    OpenSwos.Assets.TeamRecord Team(int idx);
    // Approximate RGB for a SWOS kit colour byte, for kit swatches.
    Color KitSwatch(byte colorByte);

    // --- competition support ---
    // Launches a sim match with the HUMAN always on the first (home) slot.
    // Optional overrides supply match-ready TeamRecords (e.g. the live career
    // squad) in place of the read-only master roster for the two sides.
    void StartCompetitionMatch(int playerMasterIndex, int opponentMasterIndex,
        OpenSwos.Assets.TeamRecord? homeOverride = null,
        OpenSwos.Assets.TeamRecord? awayOverride = null);
    // Non-null exactly once after a competition match reaches full time:
    // (playerGoals, opponentGoals). Cleared by the call.
    (int player, int opponent)? TakeLastCompetitionResult();
    // Companion to TakeLastCompetitionResult: the human (HOME) team's real
    // in-match energy consumption as a 0..10 distance metric, or null when the
    // last match produced none. Cleared by the call. See Main.cs FullTime.
    int? TakeLastMatchHomeDistance();
    // Companion to TakeLastCompetitionResult: the human (HOME) team's post-match
    // injuries as (in-game slot, severity 1..7) for slots that finished injured
    // and were NOT substituted off (UpdatePlayerInjuries, swos.asm:35651-35701).
    // Null/empty when none. Cleared by the call. See Main.cs FullTime.
    System.Collections.Generic.List<(int slot, int severity)>? TakeLastMatchInjuries();
    int TeamStrength(int idx);          // avg per-stat skill 1..7
    int TeamDivision(int idx);
    int TeamNation(int idx);
    ushort TeamGlobalId(int idx);
    // Master-list indices of teams in a nation+division (division -1 = any),
    // capped at max, EXCLUDING none (caller filters). Ordered as in the master list.
    System.Collections.Generic.List<int> TeamsByNationDivision(int nation, int division, int max);
    // count random distinct master indices, always including mustInclude (if >=0).
    System.Collections.Generic.List<int> RandomTeams(int count, int mustInclude);
    // Local 2-player: set opponent mode to PLAYER 2 (WASD) and start a match
    // with the current home/away selection.
    void StartLocalMultiplayerMatch();

    // --- gameplay options ---
    bool SkillScalingEnabled { get; }
    void ToggleSkillScaling();

    // --- gameplay options wave 2 ---
    bool AllPlayerTeamsEqual { get; }
    void ToggleAllPlayerTeamsEqual();
    bool ShowPreMatchMenus { get; }
    void ToggleShowPreMatchMenus();
    // In-match energy bar overlay (renderer only) + master fatigue-effect toggle
    // for non-career matches. See Main.cs _energyBar / _fatigueSim.
    bool EnergyBar { get; }
    void ToggleEnergyBar();
    bool FatigueSim { get; }
    void ToggleFatigueSim();

    // --- audio ---
    // SOUND source (PC / AMIGA); unavailable sources render as "X (N/A)" and are
    // skipped when cycling.
    string SoundSourceLabel { get; }
    void StepSoundSource(int delta);
    string CommentatorLabel { get; }
    void StepCommentator(int delta);

    // Front-end MUSIC source (AMIGA / PC / CUSTOM / OFF); unavailable sources
    // render as "X (N/A)" and are skipped when cycling.
    string MenuMusicLabel { get; }
    void StepMenuMusic(int delta);

    // --- display / fullscreen ---
    // Mirrors the F11 cycle: Windowed / Fullscreen Fill / Fullscreen Integer.
    string DisplayModeLabel { get; }
    void CycleDisplayMode();
    // Picture smoothing (OFF / SHARP). Default OFF; auto-defaults to SHARP on a
    // small handheld screen. Cycling it persists the choice.
    string SmoothingLabel { get; }
    void CycleSmoothing();
    // Persist the current UI language (Loc.Current) after the user changes it.
    void OnLanguageChanged();
    // True exactly once after a NON-competition match returns to the menu.
    bool TakeFriendlyJustEnded();
}
