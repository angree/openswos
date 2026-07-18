using Godot;
using OpenSwos.Assets;
using OpenSwos.Competition;

namespace OpenSwos.Menu;

// One rendered menu screen: a title plus a vertical list of entries, with a
// remembered cursor position (so BACK returns you where you were).
public sealed class MenuScreen
{
    public string Title = "";
    public System.Collections.Generic.List<MenuEntry> Entries = new();
    public int Selected;
    // Optional richer body painter (team-config squad table etc.), drawn under
    // the entries. Receives the client so it can spawn its own sprites.
    public System.Action<MenuClient>? Body;
    public System.Collections.Generic.List<Node> BodyNodes = new();
    // Vertical pixels the body painter needs below the entry column. Layout
    // switches to compact entry metrics when the column would eat into this.
    public int BodyReserve;
    // First free Y below the last laid-out entry (set by LayoutAndBuild before
    // Body runs). Body painters MUST anchor here, never at hardcoded Y values.
    public int BodyTop;
    // Inline table-select binding: screens that already paint a data table in
    // their body (transfer market, squad, offers, ...) expose it here so the
    // PLAYER/OFFER/CANDIDATE field can drive the visible table's own row
    // selection in place, rather than pushing a redundant full-screen picker.
    public MenuTableSelect? TableSelect;
    // When true the screen drops straight into its TableSelect on Push, so the
    // 16 slot rows are the default focus (lineup editor) instead of the entry
    // column. The entries stay reachable by scrolling UP out of the table.
    public bool AutoTableSelect;
}

// Binds a table-showing screen's body highlight to inline row navigation. The
// bound Field renders in an accent colour while the mode is active; Count /
// GetIndex / SetIndex read+write the screen's existing per-screen selection var
// (SetIndex also flips the page it lives on, mirroring the old picker onPick).
public sealed class MenuTableSelect
{
    public MenuEntry? Field;                        // the table-bound option (accent while active)
    public System.Func<int> Count = () => 0;        // number of selectable rows
    public System.Func<int> GetIndex = () => 0;     // current row index
    public System.Action<int> SetIndex = _ => { };  // move selection to an absolute row (also flips page)
    public System.Action? OnConfirm;                // optional action on FIRE (else just exit to entries)
    // When true FIRE runs OnConfirm but STAYS in table mode (multi-step flows
    // like the lineup swap: pick a row, then pick another to swap). Default
    // false keeps the classic "confirm then return to entries" behaviour.
    public bool StayOnConfirm;
    // Optional ESC override. Default ESC exits the table back to the entries;
    // when set (lineup editor: ESC leaves the whole screen) ESC runs this after
    // exiting table mode. Scrolling UP off row 0 always returns to the entries.
    public System.Action? OnCancel;
    // Optional footer control hint shown while this table is active (else the
    // generic "UP/DOWN ROW  FIRE OK  ESC BACK").
    public string? Hint;
    // Touch support: per-row hit rects (menu design space), indexed by row.
    // The career body painters (MenuClient.Career.cs) do NOT fill this yet, so
    // HandleTouch falls back to a row-height FORMULA when it is empty. Present
    // for future-proofing — a painter can populate it for pixel-exact touch
    // rows. See MenuClient.HandleTouch (TABLE SELECT mode).
    public System.Collections.Generic.List<Godot.Rect2> RowRects = new();
}

// The OpenSWOS front-end: a SWOS-styled, stack-navigated menu client that lives
// on the game's UI CanvasLayer and drives match setup, team config and launch.
// Replaces the old single plain-text label. All visuals come from MenuTheme.
public sealed partial class MenuClient
{
    private readonly Node2D _root;
    // Career data tables paint to _tableRoot, which is now simply _root: the whole
    // front-end (menu + tables) shares one 576×408 ×2 CanvasLayer. (Tables used to
    // live on a separate ×2 layer while the menu ran at ×3; now that the menu is
    // ×2 as well there is nothing to separate — one space, one scale.) The field
    // and InTableSpace routing are kept so the shared BodyBox/BodyText helpers and
    // the Active visibility toggle continue to work unchanged.
    private readonly Node2D? _tableRoot;
    private bool _tableSpace;
    // "Fine print" layer (#223): a SEPARATE CanvasLayer at scale 1 (native
    // 1152×816 px) sitting ABOVE the ×2 menu layer, so the tiny 3-letter nation
    // codes can render at true x1 with a 1 px black outline — the bitmap charset
    // cannot be fractionally scaled, so a x1 glyph on the ×2 menu layer is
    // impossible. Sprites live under _finePrintRoot (toggled with Active); each
    // one is still registered in the owning screen's BodyNodes for cleanup.
    private CanvasLayer? _finePrintLayer;
    private Node2D? _finePrintRoot;
    private readonly SwosFont? _font;
    private readonly IMenuHost _host;
    private readonly int _vw, _vh;

    private Sprite2D? _backdrop;
    private Sprite2D? _cursor;
    // Last framed cursor size — lets UpdateCursor skip the redundant Texture
    // property write when the selection's box size hasn't changed.
    private int _cursorW = -1, _cursorH = -1;
    private readonly System.Collections.Generic.Stack<MenuScreen> _stack = new();

    // ---- touch support (Android) ---------------------------------------------
    // Main sets this true on touchscreens; when true we draw an always-visible
    // BACK affordance and HandleTouch acts on taps. Purely additive — the
    // frame-polled Tick() keyboard/gamepad path is untouched.
    public bool TouchEnabled { get; set; }
    // A small '<' BACK box drawn at the top-left corner while TouchEnabled.
    private Sprite2D? _backBox, _backGlyph;
    private static readonly Rect2 kBackRect = new Rect2(4, 4, 24, 24);

    private int _cursorFrame;
    private int _heldTicks;
    private bool _dirty;

    // ---- competition session state -------------------------------------------
    // The single active competition (lazily loaded from CompetitionStore) and
    // the fixture the player is currently out playing (null between matches).
    private OpenSwos.Competition.CompetitionState? _comp;
    private OpenSwos.Competition.Fixture? _pendingFixture;

    // Setup-screen selections (persist across screen rebuilds within a session).
    private static readonly int[] kLeagueSizes = { 4, 6, 8, 10, 12, 16, 20 };
    private static readonly int[] kCupSizes = { 4, 8, 16, 32 };
    private static readonly int[] kTournamentGroups = { 2, 4 };
    private static readonly string[] kEntrantModes = { "SAME DIVISION", "SAME NATION", "RANDOM" };
    private int _setupTeamIdx;          // master-list index of "YOUR TEAM"
    private int _leagueSizeIdx = 2;     // default 8 teams
    private int _cupSizeIdx = 1;        // default 8 teams
    private int _tournamentGroupsIdx = 1; // default 4 groups of 4
    private int _entrantsMode;          // 0 same division, 1 same nation, 2 random
    private int _leagueRoundsIdx = 1;   // 0 single round-robin, 1 double

    // Wave-2 session state: manager identity, presets, save slots, table view.
    private static readonly string[] kManagerTitles = { "MR", "MS" };
    private int _mgrTitleIdx;                     // 0 = MR, 1 = MS (career creation)
    private PresetCompetition? _preset;           // preset picked on the list screen
    private System.Collections.Generic.List<int> _presetEntrants = new();
    private int _presetYou;                       // YOUR TEAM: index INTO _presetEntrants
    private bool _slotDeleteMode;                 // LOAD GAME: fire deletes instead of loads
    private string? _saveNotice;                  // one-shot dashboard status confirmation
    private int _tableGroup;                      // tournament TABLE screen group (0 = A)

    // Nation picker (continent -> country -> team, like original SWOS).
    private int _setupContinent;        // 0..5, NationNames continent code
    private int _setupNation;           // TEAM.* nation index of the picked country
    private int _friendlyNation = -1;   // friendly/local-MP country filter, -1 = ALL
    // Nations that actually contain teams, with team counts — probed once via
    // the host and cached (the master team list never changes at runtime).
    private System.Collections.Generic.List<int>? _nationsWithTeams;
    private System.Collections.Generic.Dictionary<int, int>? _nationTeamCount;
    // Built on demand because the career views are the only ones that need to
    // resolve arbitrary TEAM.* GlobalIds back to their full master-list names.
    private System.Collections.Generic.Dictionary<ushort, string>? _teamNamesByGlobalId;

    public MenuClient(Node uiLayer, SwosFont? font, IMenuHost host, int viewportW, int viewportH)
    {
        _font = font; _host = host; _vw = viewportW; _vh = viewportH;
        _optionsPages = new System.Action<MenuScreen>[] { AddOptionsPageGameplay, AddOptionsPageAudioDisplay };
        _root = new Node2D { Name = "MenuClient" };
        uiLayer.AddChild(_root);
        // Tables share the menu's own root now (see _tableRoot note above).
        _tableRoot = _root;

        // Fine-print layer: a scale-1 sibling CanvasLayer added to the menu
        // layer's parent (Main). uiLayer is already in the tree at this point
        // (Main adds it before constructing us), so GetParent() resolves. Layer
        // 2 keeps it above the ×2 menu (0) and the HUD (1) but below the fade /
        // pause overlays (100).
        var finePrintHost = uiLayer.GetParent() ?? uiLayer;
        _finePrintLayer = new CanvasLayer { Layer = 2 };
        finePrintHost.AddChild(_finePrintLayer);
        _finePrintRoot = new Node2D { Name = "MenuFinePrint" };
        _finePrintLayer.AddChild(_finePrintRoot);

        BuildBackdrop();
        _cursor = new Sprite2D { Visible = false, Centered = false, TextureFilter = CanvasItem.TextureFilterEnum.Nearest, ZIndex = 60 };
        _root.AddChild(_cursor);

        Push(BuildHome());
    }

    // Android expand presentation (#231): Main centres the 1152-wide content inside
    // the wider root viewport by shifting each screen-space CanvasLayer right. The
    // menu's own ×2 layer is shifted by Main; the fine-print layer is ours, so Main
    // calls this to keep it aligned. `px` is 0 on every non-expand platform, which
    // reproduces the original identity transform exactly.
    public void SetPresentationOffsetX(float px)
    {
        if (_finePrintLayer is not null)
            _finePrintLayer.Transform = Transform2D.Identity.Translated(new Vector2(px, 0f));
    }

    public bool Active
    {
        get => _root.Visible;
        set
        {
            bool was = _root.Visible;
            _root.Visible = value;
            if (_tableRoot is not null) _tableRoot.Visible = value;
            // The fine-print codes live on their own layer; hide them with the
            // rest of the menu so they never bleed over a match or overlay.
            if (_finePrintRoot is not null) _finePrintRoot.Visible = value;
            if (value)
            {
                // First time we (re)appear — e.g. on boot or after a match ends —
                // snap back to the home screen so the player never lands deep in a
                // stale sub-menu.
                if (!was) ResetToHome();
                _dirty = true;
            }
        }
    }

    private void ResetToHome()
    {
        while (_stack.Count > 0) DestroyScreenNodes(_stack.Pop());
        Push(BuildHome());
    }

    private System.Collections.Generic.Dictionary<ushort, string> TeamNamesByGlobalId()
    {
        if (_teamNamesByGlobalId is not null) return _teamNamesByGlobalId;

        var names = new System.Collections.Generic.Dictionary<ushort, string>();
        for (int i = 0; i < _host.TeamCount; i++)
        {
            TeamRecord team;
            try { team = _host.Team(i); }
            catch { continue; }

            string name = (team.Name ?? "").Trim().TrimEnd('\0');
            if (name.Length > 0) names.TryAdd(team.GlobalId, name);
        }
        return _teamNamesByGlobalId = names;
    }

    private void BuildBackdrop()
    {
        // Vertical wash, midnight blue — our own backdrop (SWOS uses a starfield).
        int w = _vw, h = _vh;
        var bytes = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            Color c = MenuTheme.BackdropTop.Lerp(MenuTheme.BackdropBottom, y / (float)(h - 1));
            byte r = (byte)(c.R * 255f), g = (byte)(c.G * 255f), b = (byte)(c.B * 255f);
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                bytes[o] = r; bytes[o + 1] = g; bytes[o + 2] = b; bytes[o + 3] = 255;
            }
        }
        var img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes);
        _backdrop = new Sprite2D
        {
            Texture = ImageTexture.CreateFromImage(img),
            Centered = false,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            ZIndex = 0,
        };
        _root.AddChild(_backdrop);
    }

    // ---- screen stack --------------------------------------------------------
    private MenuScreen Current => _stack.Peek();

    private void Push(MenuScreen s)
    {
        // Hide the screen we're covering so it doesn't bleed through underneath
        // (its selection index survives on the MenuScreen object for BACK).
        if (_stack.Count > 0) DestroyScreenNodes(Current);
        _stack.Push(s);
        LayoutAndBuild(s);
        // Screens that want the inline table as their default focus (lineup
        // editor) drop straight into it, so the 16 slot rows are live on entry.
        if (s.AutoTableSelect && s.TableSelect is { } ts && ts.Count() > 0) EnterTableSelect(s);
        _dirty = true;
    }

    private void Pop()
    {
        if (_stack.Count <= 1) return;
        var top = _stack.Pop();
        DestroyScreenNodes(top);
        LayoutAndBuild(Current);   // rebuild the one we return to (fresh nodes)
        _dirty = true;
    }

    private void GoTo(MenuScreen s)
    {
        // replace whole stack root-ward for a fresh navigation (used by "start over")
        Push(s);
    }

    private void DestroyScreenNodes(MenuScreen s)
    {
        foreach (var e in s.Entries)
        {
            e.Bg?.QueueFree(); e.LabelSpr?.QueueFree(); e.ValueBg?.QueueFree();
            e.ValueSpr?.QueueFree(); e.ArrowL?.QueueFree(); e.ArrowR?.QueueFree();
            e.Bg = e.LabelSpr = e.ValueBg = e.ValueSpr = e.ArrowL = e.ArrowR = null;
        }
        foreach (var n in s.BodyNodes) n.QueueFree();
        s.BodyNodes.Clear();
    }

    // ---- layout + node creation ---------------------------------------------
    private void LayoutAndBuild(MenuScreen s)
    {
        DestroyScreenNodes(s);
        LayoutEntries(s);
        foreach (var e in s.Entries) BuildEntryNodes(e);
        s.Body?.Invoke(this);
        // clamp selection to a selectable entry
        if (!IsSelectable(s, s.Selected)) s.Selected = FirstSelectable(s);
    }

    private void LayoutEntries(MenuScreen s)
    {
        // Entries flow in a centred column between the title bar (bottom ~36)
        // and the footer hint (top ~ _vh-14), minus the screen's body
        // reservation. Try normal metrics first; if the column would overflow,
        // retry with the compact tier; if even that overflows, log and clip.
        int limit = _vh - 14 - s.BodyReserve;
        if (!TryLayoutEntries(s, 46, compact: false, limit)
            && !TryLayoutEntries(s, 40, compact: true, limit))
        {
            GD.PrintErr($"MenuClient: '{s.Title}' entries overflow even compact layout, clipping");
            TryLayoutEntries(s, 40, compact: true, int.MaxValue);
        }
        // First free row below the last entry — where a body painter may start.
        int bottom = 40;
        foreach (var e in s.Entries) if (e.Y + e.H > bottom) bottom = e.Y + e.H;
        // Pure button/option screens (no body painter) centre their column
        // vertically between the title bar and the footer instead of hugging
        // the top (user ask: "wysrodkuj te menu").
        if (s.Body is null)
        {
            int shift = (limit - bottom) / 2;
            if (shift > 0)
            {
                foreach (var e in s.Entries) e.Y += shift;
                bottom += shift;
            }
        }
        s.BodyTop = bottom + 6;
    }

    // One layout pass at the given metrics tier; true if the column's bottom
    // edge stays above `limit` (footer top minus any body reservation).
    private bool TryLayoutEntries(MenuScreen s, int top, bool compact, int limit)
    {
        int gap = compact ? 2 : 5;
        int y = top;
        foreach (var e in s.Entries)
        {
            switch (e.Kind)
            {
                // Widths scaled to the 576-wide design space (×1.5 vs the old
                // 384 space) so buttons keep the same RELATIVE width. Heights and
                // gaps are left as-is: the taller 408 viewport gives the column
                // extra room (this is what lets the CAREER dashboard fit).
                case EntryKind.Label:
                    e.W = 450; e.H = compact ? 10 : 12;
                    break;
                case EntryKind.Button:
                    e.W = e.WidthOverride > 0 ? e.WidthOverride : 312;
                    e.H = e.Big ? (compact ? 15 : 20) : (compact ? 13 : 16);
                    break;
                case EntryKind.Option:
                    e.W = 450; e.H = compact ? 13 : 16;
                    break;
            }
            e.X = (_vw - e.W) / 2; e.Y = y; y += e.H + gap;
        }
        return y - gap <= limit;
    }

    private const int OptLabelW = 225;   // option row: label box | value box (×1.5 for 576 space)

    private void BuildEntryNodes(MenuEntry e)
    {
        if (e.Kind == EntryKind.Label)
        {
            e.LabelSpr = MakeText();
            return;
        }
        if (e.Kind == EntryKind.Button)
        {
            e.Bg = MakeSprite(MenuTheme.Button(e.W, e.H, e.Style), e.X, e.Y, 10);
            e.LabelSpr = MakeText();
            return;
        }
        // Option: label box (left) + value box (right) with arrows.
        int labelW = OptLabelW, valueW = e.W - labelW - 4;
        e.Bg = MakeSprite(MenuTheme.Button(labelW, e.H, MenuTheme.Style.Field), e.X, e.Y, 10);
        e.ValueBg = MakeSprite(MenuTheme.Button(valueW, e.H, e.Style), e.X + labelW + 4, e.Y, 10);
        e.LabelSpr = MakeText();
        e.ValueSpr = MakeText();
        e.ArrowL = MakeText();
        e.ArrowR = MakeText();
    }

    private Sprite2D MakeSprite(Texture2D tex, int x, int y, int z, Node2D? parent = null)
    {
        var s = new Sprite2D { Texture = tex, Centered = false, TextureFilter = CanvasItem.TextureFilterEnum.Nearest, Position = new Vector2(x, y), ZIndex = z };
        (parent ?? _root).AddChild(s);
        return s;
    }

    private Sprite2D MakeText(Node2D? parent = null)
    {
        var s = new Sprite2D { Visible = false, Centered = false, TextureFilter = CanvasItem.TextureFilterEnum.Nearest, ZIndex = 20 };
        (parent ?? _root).AddChild(s);
        return s;
    }

    // The parent for body-painter sprites: the ×2 table layer while a career
    // table painter runs inside InTableSpace, else the ×3 menu root. Keeping the
    // routing in this one place is what lets the SAME BodyBox/BodyText helpers
    // serve both the menu-space lineups and the table-space career screens.
    private Node2D BodyParent => _tableSpace && _tableRoot is not null ? _tableRoot : _root;

    // Runs a career table body painter with its sprites routed to _tableRoot.
    // try/finally so an early-return painter can never leak table-space routing
    // onto the next (menu-space) screen.
    private void InTableSpace(System.Action paint)
    {
        _tableSpace = true;
        try { paint(); }
        finally { _tableSpace = false; }
    }

    private enum Align { Left, Center, Right }

    private void SetText(Sprite2D? s, string text, bool big, Align align, int boxX, int boxY, int boxW, int boxH, Color color)
    {
        if (s is null) return;
        if (_font is null || string.IsNullOrEmpty(text)) { s.Visible = false; return; }
        var tex = _font.Render(text, big, color);
        if (tex is null) { s.Visible = false; return; }
        int w = tex.GetWidth(), h = tex.GetHeight();
        int x = align switch { Align.Right => boxX + boxW - w - 3, Align.Center => boxX + (boxW - w) / 2, _ => boxX + 3 };
        int yv = boxY + (boxH - h) / 2;
        s.Texture = tex; s.Position = new Vector2(x, yv); s.Visible = true;
    }

    // ---- public helpers for custom bodies (team-config) ---------------------
    public Node2D Root => _root;
    public SwosFont? Font => _font;
    public int ViewportW => _vw;
    public int ViewportH => _vh;

    public Sprite2D BodyBox(MenuScreen s, int x, int y, int w, int h, MenuTheme.Style style, int z = 6)
    {
        var spr = MakeSprite(MenuTheme.Button(w, h, style), x, y, z, BodyParent);
        s.BodyNodes.Add(spr);
        return spr;
    }

    public Sprite2D BodyText(MenuScreen s, string text, bool big, int x, int y, Color color, bool rightAlign = false, int centerW = 0)
    {
        var spr = MakeText(BodyParent);
        spr.ZIndex = 22;
        // reuse SetText with a zero box so alignment is anchor-based
        if (_font is not null && !string.IsNullOrEmpty(text))
        {
            var tex = _font.Render(text, big, color);
            if (tex is not null)
            {
                int w = tex.GetWidth();
                // centerW > 0 → centre the glyph run inside [x, x+centerW] (tile labels);
                // else rightAlign anchors the right edge at x; default is left at x.
                int px = centerW > 0 ? x + System.Math.Max(0, (centerW - w) / 2)
                       : rightAlign ? x - w : x;
                spr.Texture = tex;
                spr.Position = new Vector2(px, y);
                spr.Visible = true;
            }
        }
        s.BodyNodes.Add(spr);
        return spr;
    }

    // Draws a short string at true x1 (native px) on the fine-print layer with a
    // 1 px black outline, so the tiny nation codes stay legible over a flag. The
    // anchor (xMenu,yMenu) is in the ×2 menu design space; it is doubled into the
    // scale-1 layer. The glyph face is baked WHITE with four black cardinal
    // offsets underneath for the outline. Every sprite is registered in the
    // screen's BodyNodes so it is freed on the next rebuild. (#223)
    public void FinePrintText(MenuScreen s, string text, int xMenu, int yMenu, bool rightAlign = false)
    {
        if (_font is null || _finePrintRoot is null || string.IsNullOrEmpty(text)) return;
        var white = _font.Render(text, false, new Color(1f, 1f, 1f));
        if (white is null) return;
        var black = _font.Render(text, false, new Color(0f, 0f, 0f));
        int w = white.GetWidth();
        int sx = rightAlign ? xMenu * 2 - w : xMenu * 2;
        int sy = yMenu * 2;
        if (black is not null)
        {
            (int dx, int dy)[] outline = { (1, 0), (-1, 0), (0, 1), (0, -1) };
            foreach (var (dx, dy) in outline)
                s.BodyNodes.Add(MakeFinePrintSprite(black, sx + dx, sy + dy, 30));
        }
        s.BodyNodes.Add(MakeFinePrintSprite(white, sx, sy, 31));
    }

    // Like FinePrintText but centered horizontally on centerXMenu (menu-space).
    public void FinePrintTextCentered(MenuScreen s, string text, int centerXMenu, int yMenu)
    {
        if (_font is null || _finePrintRoot is null || string.IsNullOrEmpty(text)) return;
        var white = _font.Render(text, false, new Color(1f, 1f, 1f));
        if (white is null) return;
        var black = _font.Render(text, false, new Color(0f, 0f, 0f));
        int w = white.GetWidth();
        // +1: w/2 truncation left the code hanging 2px over the flag's left edge
        // (user report, e.g. "RUS"); +3 on y: code sits on the flag's lower part
        // (was +4 — bled one pixel into the row below, user report).
        int sx = centerXMenu * 2 - w / 2 + 1;
        int sy = yMenu * 2 + 3;
        if (black is not null)
        {
            (int dx, int dy)[] outline = { (1, 0), (-1, 0), (0, 1), (0, -1) };
            foreach (var (dx, dy) in outline)
                s.BodyNodes.Add(MakeFinePrintSprite(black, sx + dx, sy + dy, 30));
        }
        s.BodyNodes.Add(MakeFinePrintSprite(white, sx, sy, 31));
    }

    private Sprite2D MakeFinePrintSprite(Texture2D tex, int x, int y, int z)
    {
        var spr = new Sprite2D
        {
            Texture = tex,
            Centered = false,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            Position = new Vector2(x, y),
            ZIndex = z,
        };
        _finePrintRoot!.AddChild(spr);
        return spr;
    }

    public Sprite2D BodySwatch(MenuScreen s, int x, int y, int w, int h, Color fill)
    {
        var bytes = new byte[w * h * 4];
        byte r = (byte)(fill.R * 255f), g = (byte)(fill.G * 255f), b = (byte)(fill.B * 255f);
        for (int i = 0; i < w * h; i++) { bytes[i * 4] = r; bytes[i * 4 + 1] = g; bytes[i * 4 + 2] = b; bytes[i * 4 + 3] = 255; }
        // 1px black border for definition
        for (int px = 0; px < w; px++) { Border(bytes, w, px, 0); Border(bytes, w, px, h - 1); }
        for (int py = 0; py < h; py++) { Border(bytes, w, 0, py); Border(bytes, w, w - 1, py); }
        var img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes);
        var spr = new Sprite2D { Texture = ImageTexture.CreateFromImage(img), Centered = false, TextureFilter = CanvasItem.TextureFilterEnum.Nearest, Position = new Vector2(x, y), ZIndex = 8 };
        _root.AddChild(spr);
        s.BodyNodes.Add(spr);
        return spr;
    }
    private static void Border(byte[] b, int w, int x, int y) { int o = (y * w + x) * 4; b[o] = b[o + 1] = b[o + 2] = 0; b[o + 3] = 255; }

    // ---- per-frame refresh (dynamic labels/values) --------------------------
    private void Refresh()
    {
        EnsureTitleNodes();
        var s = Current;
        // y=11 (was 14): the title text sat visibly too low in its bar (user
        // reported it twice) — raised 6 screen px to sit centred in the band.
        SetText(_titleSpr, s.Title, true, Align.Center, 0, 11, _vw, 22, MenuTheme.TextColor(MenuTheme.Style.Header));
        _titleBar!.Visible = true;

        foreach (var e in s.Entries)
        {
            Color tc = MenuTheme.TextColor(e.Kind == EntryKind.Option ? MenuTheme.Style.Plain : e.Style);
            switch (e.Kind)
            {
                case EntryKind.Label:
                    SetText(e.LabelSpr, e.Label(), e.Big, Align.Center, e.X, e.Y, e.W, e.H, new Color(0.75f, 0.8f, 0.95f));
                    break;
                case EntryKind.Button:
                    SetText(e.LabelSpr, e.Label(), e.Big, Align.Center, e.X, e.Y, e.W, e.H, MenuTheme.TextColor(e.Style));
                    break;
                case EntryKind.Option:
                    int labelW = OptLabelW, valueW = e.W - labelW - 4, valueX = e.X + labelW + 4;
                    // Picker options open a list on FIRE, so they show no value-
                    // stepping ‹ › arrows — just the current selection in the box.
                    bool picker = e.OnActivate is not null;
                    // Accent-gold the bound field while its inline table is active.
                    bool tableField = _tableSelect is not null && ReferenceEquals(e, _tableSelect.Field);
                    Color labelCol = tableField ? kActiveFieldColor : new Color(0.85f, 0.88f, 0.96f);
                    Color valueCol = tableField ? kActiveFieldColor : MenuTheme.TextColor(e.Style);
                    SetText(e.LabelSpr, e.Label(), false, Align.Left, e.X, e.Y, labelW, e.H, labelCol);
                    SetText(e.ValueSpr, e.Value?.Invoke() ?? "", false, Align.Center, valueX, e.Y, valueW, e.H, valueCol);
                    SetText(e.ArrowL, picker ? "" : "<", false, Align.Left, valueX, e.Y, valueW, e.H, MenuTheme.TextColor(e.Style));
                    SetText(e.ArrowR, picker ? "" : ">", false, Align.Right, valueX, e.Y, valueW, e.H, MenuTheme.TextColor(e.Style));
                    break;
            }
        }

        // context footer: the HOME (root) screen shows the author credit in the
        // bottom-right corner instead of the control legend (user ask); every
        // other screen keeps the context-sensitive control hints.
        if (_stack.Count == 1 && _listPicker is null && _tableSelect is null)
        {
            SetText(_footerSpr, Loc.Tr("home.credit", "BY GRZEGORZ KORYCKI"), false, Align.Right,
                0, _vh - 11, _vw - 3, 10, new Color(0.55f, 0.6f, 0.72f));
        }
        else
        {
            string hint;
            if (_tableSelect is not null)
                hint = _tableSelect.Hint ?? "UP/DOWN ROW   FIRE OK   ESC BACK";
            else if (_listPicker is not null)
                hint = _listPicker.Columns > 1
                ? "UP/DOWN SCROLL   LEFT/RIGHT COLUMN   FIRE SELECT   ESC CANCEL"
                : "UP/DOWN SCROLL   FIRE SELECT   ESC CANCEL";
            else
            {
                bool hasStepper = s.Entries.Exists(e => e.Kind == EntryKind.Option && e.OnActivate is null);
                hint = hasStepper
                    ? "UP/DOWN SELECT   LEFT/RIGHT CHANGE   FIRE OK   ESC BACK"
                    : "UP/DOWN SELECT   FIRE OK   ESC BACK";
            }
            SetText(_footerSpr, hint, false, Align.Center, 0, _vh - 11, _vw, 10, new Color(0.55f, 0.6f, 0.72f));
        }

        // Touch BACK affordance: only visible on touchscreens. A tap inside it
        // (checked first in HandleTouch) maps to ui_cancel/ESC for the current
        // mode (BackPressed). On HOME it draws but Pop() is a no-op.
        if (_backBox is not null)
        {
            bool showBack = TouchEnabled && _showBackBox;
            _backBox.Visible = showBack;
            SetText(_backGlyph, showBack ? "<" : "", true, Align.Center,
                (int)kBackRect.Position.X, (int)kBackRect.Position.Y,
                (int)kBackRect.Size.X, (int)kBackRect.Size.Y, MenuTheme.TextColor(MenuTheme.Style.Plain));
        }
    }

    private Sprite2D? _titleBar, _titleSpr, _footerSpr;

    private void EnsureTitleNodes()
    {
        if (_titleBar is null)
        {
            _titleBar = MakeSprite(MenuTheme.Button(450, 22, MenuTheme.Style.Header), (_vw - 450) / 2, 12, 10);
            _titleSpr = MakeText();
            _titleSpr!.ZIndex = 21;
            _footerSpr = MakeText();
            _footerSpr!.ZIndex = 21;
            // Persistent BACK affordance (shown/hidden by TouchEnabled in
            // Refresh). Sits above the buttons/text but below the flash cursor.
            _backBox = MakeSprite(MenuTheme.Button((int)kBackRect.Size.X, (int)kBackRect.Size.Y, MenuTheme.Style.Plain),
                (int)kBackRect.Position.X, (int)kBackRect.Position.Y, 55);
            _backBox.Visible = false;
            _backGlyph = MakeText();
            _backGlyph!.ZIndex = 56;
        }
    }

    // ---- injected on-screen d-pad nav (Android) ------------------------------
    // Main computes edge pulses (with its OWN auto-repeat) from the touch d-pad
    // and pushes them here once per frame, immediately before Tick(). They are
    // OR-ed into the keyboard/gamepad "just pressed" reads via JP(), so the
    // ENTIRE mode ladder — main screen, list picker, table select, text input,
    // value stepping — is reused unchanged. This NEVER touches Godot's Input
    // singleton, so it can't perturb the match sim (which reads its own _touch*
    // fields, and only while AppState==Match).
    private bool _injUp, _injDown, _injLeft, _injRight, _injAccept, _injCancel;
    public void InjectNav(bool up, bool down, bool left, bool right, bool accept, bool cancel)
    {
        _injUp = up; _injDown = down; _injLeft = left; _injRight = right;
        _injAccept = accept; _injCancel = cancel;
    }
    // "Just pressed" OR an injected touch pulse for this action name.
    private bool JP(string a)
    {
        bool inj = a switch
        {
            "ui_up" => _injUp,
            "ui_down" => _injDown,
            "ui_left" => _injLeft,
            "ui_right" => _injRight,
            "ui_accept" => _injAccept,
            "ui_cancel" => _injCancel,
            _ => false,
        };
        return inj || Input.IsActionJustPressed(a);
    }

    // Main hides the small '<' BACK box (top-left) when the big on-screen touch
    // BACK button is shown in the overlay bar, so there are never two backs. On
    // change it re-renders immediately (Refresh re-runs the back-box block).
    private bool _showBackBox = true;
    public bool ShowBackBox
    {
        get => _showBackBox;
        set { if (_showBackBox == value) return; _showBackBox = value; if (Active) Refresh(); }
    }

    // ---- input ---------------------------------------------------------------
    // Called once per physics tick by Main while AppState==Menu.
    public void Tick()
    {
        // Competition result hand-back — polled before the Active check and
        // before any input handling so a finished competition match is recorded
        // the moment the menu layer runs again (the Active setter's ResetToHome
        // has already fired by then; we re-navigate to the dashboard below).
        PollCompetitionResult();
        PollFriendlyEnd();

        if (!Active) return;
        EnsureTitleNodes();
        var s = Current;

        // Text-input mode: the keyboard belongs to the typed entry, so ALL
        // normal navigation (up/down/left/right/fire/back) is suspended —
        // otherwise typing SPACE would fire buttons and WASD would steer the
        // cursor. ENTER confirms, ESC cancels (both handled in TickTextInput).
        if (_textInput is not null)
        {
            if (!ReferenceEquals(_textInputScreen, s)) EndTextInput();   // screen changed under us
            else
            {
                var ti = _textInput;
                if (ti.Typing)
                {
                    TickTextInput();
                    // DOWN (action = keyboard arrow OR gamepad dpad) leaves the
                    // field for the OK button — retro handhelds have no ENTER.
                    if (_textInput is not null && JP("ui_down"))
                    { ti.Typing = false; _dirty = true; }
                }
                else
                {
                    if (JP("ui_up")) { ti.Typing = true; _dirty = true; }
                    else if (JP("ui_accept")) HandleTextKey(ti, Key.Enter);
                    else if (JP("ui_cancel")) HandleTextKey(ti, Key.Escape);
                }
                _cursorFrame++;
                UpdateCursor();
                if (_dirty) { Refresh(); _dirty = false; }
                return;
            }
        }

        // List-picker mode: while a SWOS-style full-list picker is open the
        // keyboard belongs to the list (UP/DOWN scroll, FIRE selects, ESC
        // cancels) — normal menu navigation is suspended, exactly like text
        // input above.
        if (_listPicker is not null)
        {
            if (!ReferenceEquals(_listPickerScreen, s)) EndListPicker();   // screen changed under us
            else
            {
                TickListPicker();
                _cursorFrame++;
                UpdateCursor();
                // Refresh() alone never re-runs s.Body, and the picker's rows +
                // highlight + ITEM n/N header all live in the body — without a
                // LayoutAndBuild the selection moves invisibly (user-reported:
                // "up/down does nothing, only enter exits").
                if (_dirty && _listPicker is not null) { LayoutAndBuild(s); Refresh(); _dirty = false; }
                return;
            }
        }

        // Table-select mode: inline row selection inside a screen that already
        // paints the data table. Mirrors the list-picker branch — the keyboard
        // drives the table's own selection var while the bound field shows an
        // accent colour. Like the picker, the highlight lives in s.Body, so a
        // bare Refresh never re-runs it: redraw via LayoutAndBuild on every
        // change (and on the exit frame, to clear the accent + footer hint).
        if (_tableSelect is not null)
        {
            if (!ReferenceEquals(_tableSelectScreen, s)) ExitTableSelect();   // screen changed under us
            else
            {
                TickTableSelect();
                _cursorFrame++;
                UpdateCursor();
                if (_dirty) { LayoutAndBuild(s); Refresh(); _dirty = false; }
                return;
            }
        }

        bool up = JP("ui_up");
        bool down = JP("ui_down");
        // Drop into the inline table by scrolling DOWN off the last menu entry
        // ("albo móc na nią sam zjechać") — only on screens that expose one.
        if (down && s.TableSelect is { } tsCfg && tsCfg.Count() > 0 && s.Selected == LastSelectable(s))
        {
            EnterTableSelect(s);
            _cursorFrame++;
            UpdateCursor();
            if (_dirty) { LayoutAndBuild(s); Refresh(); _dirty = false; }
            return;
        }
        if (up) { MoveSelection(s, -1); _dirty = true; }
        else if (down) { MoveSelection(s, +1); _dirty = true; }

        var sel = (s.Selected >= 0 && s.Selected < s.Entries.Count) ? s.Entries[s.Selected] : null;

        // A "picker option" is an Option whose FIRE opens a pushed list picker
        // (OnActivate set) rather than cycling a value in place. It never
        // responds to LEFT/RIGHT — value-stepping is reserved for true option
        // toggles (OPTIONS screen, match length, sizes, ...).
        bool selIsPicker = sel is { Kind: EntryKind.Option } && sel.OnActivate is not null;

        // LEFT/RIGHT — step a value-cycling Option (with optional accelerated
        // hold). Picker options are deliberately excluded.
        bool leftHeld = Input.IsActionPressed("ui_left");
        bool rightHeld = Input.IsActionPressed("ui_right");
        int step = 0;
        if (JP("ui_left")) { step = -1; _heldTicks = 0; }
        else if (JP("ui_right")) { step = 1; _heldTicks = 0; }
        else if ((leftHeld || rightHeld) && sel is { Kind: EntryKind.Option, FastScroll: true } && !selIsPicker)
        {
            _heldTicks++;
            int dir = leftHeld ? -1 : 1;
            if (_heldTicks >= 90) step = dir * 5;
            else if (_heldTicks >= 30 && (_heldTicks - 30) % 6 == 0) step = dir;
        }
        else _heldTicks = 0;

        if (step != 0 && sel is { Kind: EntryKind.Option } && !selIsPicker)
        {
            sel.OnStep?.Invoke(step);
            _dirty = true;
        }

        // FIRE — activate a Button, OPEN a picker option, or nudge a value Option.
        if (JP("ui_accept"))
        {
            if (sel is { Kind: EntryKind.Button }) sel.OnActivate?.Invoke();
            else if (selIsPicker) sel!.OnActivate?.Invoke();
            else if (sel is { Kind: EntryKind.Option }) { sel.OnStep?.Invoke(+1); _dirty = true; }
        }

        // BACK — pop a screen.
        if (JP("ui_cancel")) Pop();

        // cursor flash cadence
        _cursorFrame++;
        UpdateCursor();
        UpdateShimmer(s);

        if (_dirty) { Refresh(); _dirty = false; }
    }

    // ---- touch input (event-driven; Main calls this from _UnhandledInput) -----
    // Tap-to-select for Android. `menuPos` is ALREADY in the 576×408 design
    // space. Only the PRESS edge acts (simple press-to-activate). Dispatches on
    // the SAME mode ladder as Tick() — text input, list picker, table select,
    // then normal entries — so touch and keyboard/gamepad stay behaviourally
    // identical. Purely additive: Tick()'s polled path is never touched here.
    public void HandleTouch(Godot.Vector2 menuPos, bool pressed)
    {
        if (!Active) return;
        if (!pressed) return;   // tap-to-select acts on press only
        EnsureTitleNodes();
        var s = Current;

        // BACK affordance FIRST — maps to ui_cancel/ESC in the current mode.
        if (TouchEnabled && kBackRect.HasPoint(menuPos)) { BackPressed(); return; }

        // A. TEXT INPUT (mirror Tick's _textInput branch) --------------------
        if (_textInput is not null)
        {
            if (!ReferenceEquals(_textInputScreen, s)) EndTextInput();   // screen changed under us
            else
            {
                var ti = _textInput;
                // The PushNameEntry OK button is a normal Button entry — a tap
                // on it confirms (same as ui_accept on the OK button).
                foreach (var e in s.Entries)
                    if (e.Kind == EntryKind.Button && HitEntry(e, menuPos))
                    { e.OnActivate?.Invoke(); return; }
                // A tap on the live-text field (re)focuses typing; the OS soft
                // keyboard was already shown by StartTextInput.
                foreach (var e in s.Entries)
                    if (e.Kind == EntryKind.Label && HitEntry(e, menuPos))
                    { ti.Typing = true; _dirty = true; Refresh(); UpdateCursor(); return; }
                return;   // taps elsewhere ignored while a field owns the keyboard
            }
        }

        // B. LIST PICKER (mirror Tick's _listPicker branch) ------------------
        if (_listPicker is not null)
        {
            if (!ReferenceEquals(_listPickerScreen, s)) EndListPicker();   // screen changed under us
            else { HandleTouchListPicker(menuPos); return; }
        }

        // C. TABLE SELECT (mirror Tick's _tableSelect branch) ----------------
        if (_tableSelect is not null)
        {
            if (!ReferenceEquals(_tableSelectScreen, s)) ExitTableSelect();   // screen changed under us
            else { HandleTouchTableSelect(menuPos); return; }
        }

        // D. NORMAL ENTRIES (mirror DebugSelect/DebugFireLabel semantics) ----
        for (int i = 0; i < s.Entries.Count; i++)
        {
            var e = s.Entries[i];
            if (!e.Selectable) continue;              // skip Labels
            if (!HitEntry(e, menuPos)) continue;      // live rect, never cached
            s.Selected = i;                           // move the cursor there
            if (e.Kind == EntryKind.Button) e.OnActivate?.Invoke();
            else if (e.Kind == EntryKind.Option && e.OnActivate is not null) e.OnActivate.Invoke();  // picker → open list
            else if (e.Kind == EntryKind.Option)      // stepper → step by tapped half
            {
                int valueX = e.X + OptLabelW + 4, valueW = e.W - OptLabelW - 4;
                // Label half (left of the value box) steps forward; inside the
                // value box the left half is ‹ (-1) and the right half is › (+1).
                int dir = menuPos.X < valueX ? +1 : (menuPos.X < valueX + valueW / 2f ? -1 : +1);
                e.OnStep?.Invoke(dir);
            }
            _dirty = true; Refresh(); UpdateCursor();
            return;
        }
    }

    // List-picker tap dispatch — geometry mirrors DrawListPickerBody, but hit-
    // tests the per-cell rects it publishes into lp.CellRects (kept in sync).
    private void HandleTouchListPicker(Godot.Vector2 menuPos)
    {
        var lp = _listPicker!;
        var s = Current;
        int panelX = 8, panelY = s.BodyTop, panelW = _vw - 16, panelH = _vh - panelY - 21;
        var panel = new Rect2(panelX, panelY, panelW, panelH);

        // Tap a specific cell: the selected cell confirms (like ui_accept in
        // TickListPicker); any other visible cell just moves the selection.
        foreach (var (rect, idx) in lp.CellRects)
        {
            if (!rect.HasPoint(menuPos)) continue;
            if (idx == lp.Selected)
            {
                ConfirmPicker();
                _dirty = true; Refresh(); UpdateCursor();
            }
            else { lp.Selected = idx; LayoutAndBuild(Current); Refresh(); UpdateCursor(); }
            return;
        }

        // Inside the panel but not on a cell → paging on the multi-column grid:
        // bottom band = next page, top (header) band = prev page. Clamp to pages.
        if (panel.HasPoint(menuPos) && lp.Columns > 1 && lp.ColRows > 0)
        {
            int n = lp.Rows.Count, capacity = lp.Columns * lp.ColRows;
            if (n > capacity)
            {
                int page = lp.Selected / capacity;
                if (menuPos.Y >= panelY + panelH - 12 && (page + 1) * capacity < n)
                { lp.Selected = (page + 1) * capacity; LayoutAndBuild(Current); Refresh(); UpdateCursor(); return; }
                if (menuPos.Y <= panelY + 14 && page > 0)
                { lp.Selected = (page - 1) * capacity; LayoutAndBuild(Current); Refresh(); UpdateCursor(); return; }
            }
            return;   // tap on empty panel space — ignore
        }

        // Tap fully outside the panel → cancel (like ui_cancel in TickListPicker).
        if (!panel.HasPoint(menuPos)) { CancelPicker(); _dirty = true; Refresh(); UpdateCursor(); }
    }

    // Table-select tap dispatch — hit-tests painter-published RowRects when
    // present, else the CareerSquadRowsPerPage row FORMULA. Mirrors the FIRE /
    // move semantics of TickTableSelect.
    private void HandleTouchTableSelect(Godot.Vector2 menuPos)
    {
        var ts = _tableSelect!;
        int n = ts.Count();
        if (n == 0) { ExitTableSelect(); LayoutAndBuild(Current); Refresh(); UpdateCursor(); return; }
        int cur = System.Math.Clamp(ts.GetIndex(), 0, n - 1);

        int row = -1;
        // Prefer painter-published exact rects (future-proof; empty for now).
        if (ts.RowRects.Count > 0)
        {
            for (int i = 0; i < ts.RowRects.Count && i < n; i++)
                if (ts.RowRects[i].HasPoint(menuPos)) { row = i; break; }
        }
        if (row < 0)
        {
            // Formula fallback: same maths as CareerSquadRowsPerPage. Header sits
            // at panelY+15, first data-row highlight box at panelY+24, 8px pitch.
            int panelX = 8, panelY = Current.BodyTop, panelW = _vw - 16, panelH = _vh - panelY - 21;
            int rows = System.Math.Max(1, (panelH - 29) / 8);
            int first = (cur / rows) * rows;
            int firstTop = panelY + 24;
            for (int k = 0; k < rows && first + k < n; k++)
                if (new Rect2(panelX, firstTop + k * 8, panelW, 8).HasPoint(menuPos)) { row = first + k; break; }
        }
        if (row < 0) return;   // tap missed the rows

        if (row == cur)
        {
            // Confirm the already-current row (respect StayOnConfirm), like FIRE.
            if (ts.StayOnConfirm) { ts.OnConfirm?.Invoke(); _dirty = true; }
            else { var confirm = ts.OnConfirm; ExitTableSelect(); confirm?.Invoke(); }
        }
        else ts.SetIndex(row);
        LayoutAndBuild(Current); Refresh(); UpdateCursor();
    }

    // A tap-inside-BACK maps to whatever ui_cancel/ESC does in the CURRENT mode
    // (mirrors each mode's cancel branch). Reuses the existing cancel paths.
    private void BackPressed()
    {
        var s = Current;
        if (_textInput is not null && ReferenceEquals(_textInputScreen, s))
        { HandleTextKey(_textInput, Key.Escape); _dirty = true; Refresh(); UpdateCursor(); return; }
        if (_listPicker is not null && ReferenceEquals(_listPickerScreen, s))
        { EndListPicker(); Pop(); _dirty = true; Refresh(); UpdateCursor(); return; }
        if (_tableSelect is not null && ReferenceEquals(_tableSelectScreen, s))
        { var onCancel = _tableSelect.OnCancel; ExitTableSelect(); onCancel?.Invoke(); LayoutAndBuild(Current); Refresh(); UpdateCursor(); return; }
        Pop(); Refresh(); UpdateCursor();
    }

    // Live-rect hit test for a menu entry (never uses cached geometry).
    private static bool HitEntry(MenuEntry e, Godot.Vector2 p)
        => p.X >= e.X && p.Y >= e.Y && p.X < e.X + e.W && p.Y < e.Y + e.H;

    // Gentle two-colour sheen on Shimmer-flagged buttons (HOME "CAREER"): the
    // background texture is modulated between a neutral and a warm-bright tint
    // on a slow sine, so the gradient appears to breathe. Pure per-frame
    // Modulate — no texture re-render, no Refresh needed.
    private int _shimmerFrame;
    private static readonly Color kShimmerA = new(1.00f, 1.00f, 1.00f);
    private static readonly Color kShimmerB = new(1.30f, 1.22f, 0.88f);
    private void UpdateShimmer(MenuScreen s)
    {
        _shimmerFrame++;
        float t = (Mathf.Sin(_shimmerFrame * 0.045f) + 1f) * 0.5f;
        foreach (var e in s.Entries)
            if (e.Shimmer && e.Bg is not null)
                e.Bg.Modulate = kShimmerA.Lerp(kShimmerB, t);
    }

    private bool IsSelectable(MenuScreen s, int i) => i >= 0 && i < s.Entries.Count && s.Entries[i].Selectable;
    private int FirstSelectable(MenuScreen s)
    {
        for (int i = 0; i < s.Entries.Count; i++) if (s.Entries[i].Selectable) return i;
        return 0;
    }
    private int LastSelectable(MenuScreen s)
    {
        for (int i = s.Entries.Count - 1; i >= 0; i--) if (s.Entries[i].Selectable) return i;
        return -1;
    }
    private void MoveSelection(MenuScreen s, int dir)
    {
        int n = s.Entries.Count;
        for (int k = 0; k < n; k++)
        {
            s.Selected = (s.Selected + dir + n) % n;
            if (s.Entries[s.Selected].Selectable) return;
        }
    }

    private void UpdateCursor()
    {
        var s = Current;
        if (_cursor is null) return;
        // While TYPING the caret is the focus indicator; once DOWN moves focus
        // to the OK button the normal flashing cursor frames it.
        if (_textInput is not null && _textInput.Typing) { _cursor.Visible = false; return; }
        if (_listPicker is not null) { _cursor.Visible = false; return; }  // list owns the screen (its own highlight)
        if (_tableSelect is not null) { _cursor.Visible = false; return; } // inline table owns the screen (row highlight + accent field)
        if (s.Selected < 0 || s.Selected >= s.Entries.Count) { _cursor.Visible = false; return; }
        var e = s.Entries[s.Selected];
        if (!e.Selectable) { _cursor.Visible = false; return; }
        // frame the whole row (label+value for options)
        int x = e.X - 2, y = e.Y - 2, w = e.W + 4, h = e.H + 4;
        // CursorOutline is already (w,h)-cached in MenuTheme, but skip even the
        // redundant Texture property write while the framed size is unchanged
        // (this runs every tick). Only the Modulate flash below must repaint.
        if (w != _cursorW || h != _cursorH)
        {
            _cursor.Texture = MenuTheme.CursorOutline(w, h);
            _cursorW = w; _cursorH = h;
        }
        _cursor.Position = new Vector2(x, y);
        _cursor.Modulate = MenuTheme.CursorColor(_cursorFrame >> 2);   // ~17 Hz flash at 70 Hz tick
        _cursor.Visible = true;
    }

    // ---- debug / screenshot-driver hooks ------------------------------------
    public string DebugTitle => Current.Title;
    public int DebugEntryCount => Current.Entries.Count;
    public int DebugSelected => Current.Selected;
    public void DebugSelect(int i)
    {
        var s = Current;
        if (i >= 0 && i < s.Entries.Count) s.Selected = s.Entries[i].Selectable ? i : FirstSelectable(s);
        _dirty = true; Refresh(); UpdateCursor();
    }
    public void DebugFire()
    {
        var s = Current; if (s.Selected < 0 || s.Selected >= s.Entries.Count) return;
        var sel = s.Entries[s.Selected];
        if (sel.Kind == EntryKind.Button) sel.OnActivate?.Invoke();
        else if (sel.Kind == EntryKind.Option && sel.OnActivate is not null) sel.OnActivate.Invoke();
        else if (sel.Kind == EntryKind.Option) sel.OnStep?.Invoke(+1);
        _dirty = true; Refresh(); UpdateCursor();
    }
    public void DebugBack() { Pop(); Refresh(); UpdateCursor(); }
    // Finds the first selectable entry whose label CONTAINS `label` and fires
    // it (Button) or steps it forward (Option). Robust against conditional
    // entries shifting indices (e.g. the HOME CONTINUE button). Returns false
    // when no such entry exists on the current screen.
    public bool DebugFireLabel(string label)
    {
        var s = Current;
        for (int i = 0; i < s.Entries.Count; i++)
        {
            var e = s.Entries[i];
            if (!e.Selectable) continue;
            if (!e.Label().Contains(label)) continue;
            s.Selected = i;
            if (e.Kind == EntryKind.Button) e.OnActivate?.Invoke();
            else if (e.OnActivate is not null) e.OnActivate.Invoke();   // picker option opens its list
            else e.OnStep?.Invoke(+1);
            _dirty = true; Refresh(); UpdateCursor();
            return true;
        }
        return false;
    }
    public void DebugStep(int d)
    {
        var s = Current; if (s.Selected < 0 || s.Selected >= s.Entries.Count) return;
        var sel = s.Entries[s.Selected];
        if (sel.Kind == EntryKind.Option && sel.OnActivate is null) sel.OnStep?.Invoke(d);
        _dirty = true; Refresh();
    }
    // One-line competition state for the orchestrator's screenshot harness.
    public string DebugCompSummary()
        => $"{_comp?.Name}|{(_comp is null ? "none" : OpenSwos.Competition.CompetitionEngine.RoundLabel(_comp))}|fixtures={_comp?.Fixtures.Count ?? 0}";

    // ======================================================================
    //  TEXT INPUT — screen-level typed-entry mode (manager name, save slots)
    // ======================================================================
    // A minimal typed-input mechanism: while a screen owns an active text
    // input, Tick() routes the raw keyboard here instead of the menu. Keys
    // are polled with just-pressed edge detection (Input.IsPhysicalKeyPressed
    // against a HashSet of previously-down keys, over a fixed Key[] list):
    // A-Z / 0-9 / SPACE / MINUS append (up to MaxLen), BACKSPACE deletes,
    // ENTER confirms into OnConfirm, ESC cancels (default: pop the screen).
    // LEFT/RIGHT go to an optional side hook so a companion value (the
    // manager's MR/MS title) stays steppable while navigation is suspended.
    // The live text renders as a Label-like entry with a blinking underscore.
    private sealed class TextInputState
    {
        public readonly System.Text.StringBuilder Text = new();
        public int MaxLen = 16;
        public System.Action<string>? OnConfirm;
        public System.Action? OnCancel;
        public System.Action<int>? OnSide;
        // True while the keyboard types into the field. DOWN drops focus onto
        // the OK button below (gamepad-only handhelds have no ENTER key);
        // UP returns to typing. While false, FIRE confirms and ESC cancels.
        public bool Typing = true;
    }

    private TextInputState? _textInput;
    private MenuScreen? _textInputScreen;
    private readonly System.Collections.Generic.HashSet<Key> _textKeysDown = new();
    private int _textBlinkPhase;

    private static readonly Key[] kTextKeys = BuildTextKeyTable();
    private static Key[] BuildTextKeyTable()
    {
        var keys = new System.Collections.Generic.List<Key>();
        for (int i = 0; i < 26; i++) keys.Add((Key)((long)Key.A + i));
        for (int i = 0; i < 10; i++) keys.Add((Key)((long)Key.Key0 + i));
        keys.Add(Key.Space); keys.Add(Key.Minus); keys.Add(Key.Backspace);
        keys.Add(Key.Enter); keys.Add(Key.KpEnter); keys.Add(Key.Escape);
        keys.Add(Key.Left); keys.Add(Key.Right);
        return keys.ToArray();
    }

    private void StartTextInput(MenuScreen owner, string initial, int maxLen,
        System.Action<string> onConfirm, System.Action? onCancel = null, System.Action<int>? onSide = null)
    {
        _textInput = new TextInputState { MaxLen = maxLen, OnConfirm = onConfirm, OnCancel = onCancel, OnSide = onSide };
        string init = (initial ?? "").Trim().ToUpperInvariant();
        if (init.Length > maxLen) init = init.Substring(0, maxLen);
        _textInput.Text.Append(init);
        _textInputScreen = owner;
        // Seed the down-set with every currently-held key so the ENTER/SPACE
        // press that opened this screen cannot instantly type or confirm.
        _textKeysDown.Clear();
        foreach (Key k in kTextKeys) if (Input.IsPhysicalKeyPressed(k)) _textKeysDown.Add(k);
        _dirty = true;
        // Android/mobile: pop the on-screen keyboard so text can actually be
        // typed. No-op on desktop (feature absent).
        if (DisplayServer.HasFeature(DisplayServer.Feature.VirtualKeyboard))
            DisplayServer.VirtualKeyboardShow(_textInput.Text.ToString(), new Rect2I(),
                DisplayServer.VirtualKeyboardType.Default, _textInput.MaxLen);
    }

    private void EndTextInput()
    {
        _textInput = null;
        _textInputScreen = null;
        _textKeysDown.Clear();
        _dirty = true;
        if (DisplayServer.HasFeature(DisplayServer.Feature.VirtualKeyboard))
            DisplayServer.VirtualKeyboardHide();
    }

    /// <summary>Feed a key event (Android soft keyboard / IME) into the active text field.
    /// Returns true if consumed. Called only on mobile platforms by Main._UnhandledInput;
    /// desktop keeps using the frame-polled TickTextInput path.</summary>
    public bool FeedTextInputEvent(Godot.InputEventKey ev)
    {
        if (_textInput is null) return false;
        var ti = _textInput;
        Key kc = ev.Keycode != Key.None ? ev.Keycode : ev.PhysicalKeycode;
        // Editing / control keys route through the existing HandleTextKey path.
        if (kc == Key.Enter || kc == Key.KpEnter || kc == Key.Escape ||
            kc == Key.Backspace || kc == Key.Left || kc == Key.Right)
        { HandleTextKey(ti, kc); return true; }
        // Printable soft-keyboard chars usually arrive as Unicode with Keycode==None.
        long uni = ev.Unicode;
        if (uni > 0)
        {
            char c = char.ToUpperInvariant((char)uni);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == ' ' || c == '-')
            {
                if (ti.Text.Length < ti.MaxLen) { ti.Text.Append(c); _dirty = true; }
                return true;
            }
        }
        // Hardware-keyboard fallback (keycode present, no unicode).
        if ((kc >= Key.A && kc <= Key.Z) || (kc >= Key.Key0 && kc <= Key.Key9) || kc == Key.Space || kc == Key.Minus)
        { HandleTextKey(ti, kc); return true; }
        return false;
    }

    private void TickTextInput()
    {
        var ti = _textInput!;
        foreach (Key k in kTextKeys)
        {
            bool down = Input.IsPhysicalKeyPressed(k);
            if (down) { if (_textKeysDown.Add(k)) HandleTextKey(ti, k); }   // just-pressed edge
            else _textKeysDown.Remove(k);
            if (_textInput is null) return;   // confirm/cancel ended the mode
        }
        // Blinking underscore: repaint only when the blink phase flips.
        int phase = (_cursorFrame >> 4) & 1;
        if (phase != _textBlinkPhase) { _textBlinkPhase = phase; _dirty = true; }
    }

    private void HandleTextKey(TextInputState ti, Key k)
    {
        switch (k)
        {
            case Key.Enter:
            case Key.KpEnter:
            {
                string result = ti.Text.ToString().Trim();
                EndTextInput();
                ti.OnConfirm?.Invoke(result);
                return;
            }
            case Key.Escape:
            {
                var cancel = ti.OnCancel;
                EndTextInput();
                if (cancel is not null) cancel();
                else Pop();
                return;
            }
            case Key.Backspace:
                if (ti.Text.Length > 0) { ti.Text.Length -= 1; _dirty = true; }
                return;
            case Key.Left:  ti.OnSide?.Invoke(-1); _dirty = true; return;
            case Key.Right: ti.OnSide?.Invoke(+1); _dirty = true; return;
        }
        char c;
        if (k >= Key.A && k <= Key.Z) c = (char)('A' + (int)(k - Key.A));
        else if (k >= Key.Key0 && k <= Key.Key9) c = (char)('0' + (int)(k - Key.Key0));
        else if (k == Key.Space) c = ' ';
        else if (k == Key.Minus) c = '-';
        else return;
        if (ti.Text.Length < ti.MaxLen) { ti.Text.Append(c); _dirty = true; }
    }

    // The live entry row: current text plus a blinking underscore caret
    // (space keeps the rendered width steady while the caret is off).
    private string TextInputDisplay()
        => _textInput is null ? ""
         : !_textInput.Typing ? _textInput.Text.ToString()   // caret off while OK has focus
         : _textInput.Text.ToString() + (_textBlinkPhase == 0 ? "_" : " ");

    // Generic typed-entry screen: big live-text row, optional side-value row
    // (stepped with LEFT/RIGHT via onSide), and a key-hint caption. Input
    // mode starts as the screen is pushed; ESC pops back to the caller.
    private void PushNameEntry(string title, string initial, int maxLen, string hint,
        System.Action<string> onConfirm, System.Func<string>? sideRow = null, System.Action<int>? onSide = null)
    {
        var s = new MenuScreen { Title = title };
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = true, Label = TextInputDisplay });
        if (sideRow is not null)
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = sideRow });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => hint });
        // OK confirm button: reachable with DOWN from the field (gamepad-only
        // consoles have no ENTER). ui_accept on it is handled by the !Typing
        // branch of Tick; OnActivate is the keyboard/mouse-parity fallback.
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = false,
            Label = () => Loc.Tr("common.ok", "OK"), OnActivate = () => { if (_textInput is not null) HandleTextKey(_textInput, Key.Enter); } });
        Push(s);
        StartTextInput(s, initial, maxLen, onConfirm, onCancel: () => Pop(), onSide: onSide);
    }

    // ---- text-input debug hooks (screenshot/orchestrator harness) ------------
    public bool DebugTextInputActive => _textInput is not null;
    public string DebugTextInputValue => _textInput?.Text.ToString() ?? "";
    public void DebugTextType(string text)
    {
        var ti = _textInput;
        if (ti is null) return;
        foreach (char raw in (text ?? "").ToUpperInvariant())
        {
            if (raw == '\b') { if (ti.Text.Length > 0) ti.Text.Length -= 1; continue; }
            bool ok = (raw >= 'A' && raw <= 'Z') || (raw >= '0' && raw <= '9') || raw == ' ' || raw == '-';
            if (ok && ti.Text.Length < ti.MaxLen) ti.Text.Append(raw);
        }
        _dirty = true; Refresh();
    }
    public void DebugTextConfirm()
    {
        if (_textInput is not null) HandleTextKey(_textInput, Key.Enter);
        _dirty = true; Refresh(); UpdateCursor();
    }
    public void DebugTextCancel()
    {
        if (_textInput is not null) HandleTextKey(_textInput, Key.Escape);
        _dirty = true; Refresh(); UpdateCursor();
    }
    public void DebugTextSide(int d)
    {
        if (_textInput is not null) HandleTextKey(_textInput, d < 0 ? Key.Left : Key.Right);
        _dirty = true; Refresh();
    }

    // ======================================================================
    //  LIST PICKER — SWOS-style pushed scrollable list (replaces the
    //  left/right value-steppers everywhere an ITEM is chosen from a set).
    // ======================================================================
    // The user's model: FIRE on a picker field pushes a full-screen list (like
    // the original league/team selectors), UP/DOWN scroll through it (held-key
    // fast scroll), FIRE selects the highlighted row and returns, ESC cancels.
    // Rows are plain strings; the caller keeps the index->domain mapping in its
    // onPick closure. Handles thousands of rows via a windowed body painter.
    private sealed class ListPickerState
    {
        public System.Collections.Generic.IReadOnlyList<string> Rows = System.Array.Empty<string>();
        public int Selected;
        public System.Action<int>? OnPick;
        // Optional back-navigation: fired when the user cancels (ESC / tap-outside)
        // instead of picking. Used by the continent->country->team drill-down to
        // step back up one level instead of dropping straight out to the screen.
        public System.Action? OnCancel;
        // When true this picker is a level of an in-place drill-down: FIRE/ESC run
        // OnPick/OnCancel WITHOUT popping the screen (the handler swaps the rows in
        // place via SwapListPicker). Keeps the whole chain on ONE screen node so
        // an earlier level can never bleed through under a later one.
        public bool KeepOpen;
        // Grid geometry published by DrawListPickerBody for the input tick:
        // >5 items lay out in 3 columns like the original SWOS selectors, and
        // LEFT/RIGHT then jump a whole column (ColRows items).
        public int Columns = 1;
        public int ColRows = 1;
        // Touch support: per-visible-cell hit rects (menu design space) paired
        // with their row index, filled by DrawListPickerBody as it lays each
        // cell out so HandleTouch hit-tests the REAL rects instead of
        // re-deriving the grid formula (keeps the two in sync). (#touch)
        public readonly System.Collections.Generic.List<(Godot.Rect2 rect, int index)> CellRects = new();
    }

    private ListPickerState? _listPicker;
    private MenuScreen? _listPickerScreen;

    // Push a list picker. `current` pre-highlights a row; `onPick` receives the
    // chosen row index AFTER the picker screen has popped, so the opener can
    // update its own selection state and rebuild in place.
    private void PushListPicker(string title, System.Collections.Generic.IReadOnlyList<string> rows,
        int current, System.Action<int> onPick, System.Action? onCancel = null)
    {
        int n = rows.Count;
        _listPicker = new ListPickerState
        {
            Rows = rows,
            Selected = n == 0 ? 0 : System.Math.Clamp(current, 0, n - 1),
            OnPick = onPick,
            OnCancel = onCancel,
        };
        var s = new MenuScreen { Title = title, BodyReserve = _vh };   // list owns the whole body
        s.Body = client => client.DrawListPickerBody(s);
        _listPickerScreen = s;
        _heldTicks = 0;
        Push(s);
    }

    private void EndListPicker()
    {
        _listPicker = null;
        _listPickerScreen = null;
        _heldTicks = 0;
        _dirty = true;
    }

    // Update the OPEN picker to a new drill-down level IN PLACE (title + rows +
    // handlers) instead of pushing a second picker screen. Pushing a new screen
    // per level left the previous level's body nodes bleeding through under the
    // next (QueueFree is deferred) — the "miksuje kraje i kluby" kasza bug. One
    // screen node, contents swapped, old body freed synchronously by
    // LayoutAndBuild. Falls back to Push for the FIRST level (no picker open yet).
    private void SwapListPicker(string title, System.Collections.Generic.IReadOnlyList<string> rows,
        int current, System.Action<int> onPick, System.Action? onCancel = null)
    {
        if (_listPicker is null || _listPickerScreen is null)
        {
            PushListPicker(title, rows, current, onPick, onCancel);
            _listPicker!.KeepOpen = true;
            return;
        }
        int n = rows.Count;
        _listPicker.Rows = rows;
        _listPicker.Selected = n == 0 ? 0 : System.Math.Clamp(current, 0, n - 1);
        _listPicker.OnPick = onPick;
        _listPicker.OnCancel = onCancel;
        _listPicker.KeepOpen = true;
        _listPickerScreen.Title = title;
        _heldTicks = 0;
        LayoutAndBuild(_listPickerScreen);   // frees old body nodes, rebuilds with new rows
        _dirty = true;
    }

    // FIRE on the highlighted row. A drill-down level (KeepOpen) runs OnPick which
    // swaps the picker in place; a leaf picker closes first (End + Pop) then picks.
    private void ConfirmPicker()
    {
        var lp = _listPicker; if (lp is null) return;
        int idx = lp.Selected; var pick = lp.OnPick;
        if (lp.KeepOpen) pick?.Invoke(idx);
        else { EndListPicker(); Pop(); pick?.Invoke(idx); }
        _dirty = true;
    }

    // ESC / tap-outside. A drill-down level with an OnCancel steps back one level
    // in place; otherwise it closes the picker (End + Pop) and runs OnCancel (if any).
    private void CancelPicker()
    {
        var lp = _listPicker; if (lp is null) return;
        var oc = lp.OnCancel;
        if (lp.KeepOpen && oc is not null) oc.Invoke();
        else { EndListPicker(); Pop(); oc?.Invoke(); }
        _dirty = true;
    }

    private void TickListPicker()
    {
        var lp = _listPicker!;
        if (JP("ui_cancel")) { CancelPicker(); return; }
        int n = lp.Rows.Count;
        if (n == 0) return;

        bool upHeld = Input.IsActionPressed("ui_up");
        bool downHeld = Input.IsActionPressed("ui_down");
        int move = 0;
        if (JP("ui_up")) { move = -1; _heldTicks = 0; }
        else if (JP("ui_down")) { move = 1; _heldTicks = 0; }
        else if (upHeld || downHeld)
        {
            _heldTicks++;
            int dir = upHeld ? -1 : 1;
            if (_heldTicks >= 90) move = dir * 5;          // long hold: page-fast
            else if (_heldTicks >= 24 && (_heldTicks - 24) % 3 == 0) move = dir;
        }
        else _heldTicks = 0;

        if (move != 0) { lp.Selected = ((lp.Selected + move) % n + n) % n; _dirty = true; }

        // Multi-column grid (original-SWOS style): LEFT/RIGHT jump one column.
        if (lp.Columns > 1)
        {
            if (JP("ui_left") && lp.Selected > 0)
            { lp.Selected = System.Math.Max(0, lp.Selected - lp.ColRows); _dirty = true; }
            else if (JP("ui_right") && lp.Selected < n - 1)
            { lp.Selected = System.Math.Min(n - 1, lp.Selected + lp.ColRows); _dirty = true; }
        }

        if (JP("ui_accept")) ConfirmPicker();
    }

    // Original-SWOS selector body: a grid of TILES (each item is its own filled,
    // bordered rectangle with the label centred inside), not a plain text list.
    // ≤5 items = one centred column of wide tiles; 6+ = 3 columns of ~1/3-panel
    // tiles filled top-to-bottom then left-to-right, paged. The selected tile is
    // drawn in the bright Info style; the rest in the flat Tool style. A header
    // shows the 1-based position; LEFT/RIGHT jump a column (see TickListPicker).
    private void DrawListPickerBody(MenuScreen s)
    {
        var lp = _listPicker;
        if (lp is null) return;
        lp.CellRects.Clear();   // re-published every paint for HandleTouch
        int panelX = 8, panelY = s.BodyTop, panelW = _vw - 16, panelH = _vh - panelY - 21;
        if (panelH < 24) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var selCol = new Color(1f, 1f, 1f);
        var normal = new Color(0.9f, 0.93f, 1f);
        int n = lp.Rows.Count;
        if (n == 0) { BodyText(s, Loc.Tr("pick.nothing_to_choose", "NOTHING TO CHOOSE"), false, panelX + 8, panelY + 8, head); return; }

        BodyText(s, Loc.Tr("pick.item", "ITEM") + " " + (lp.Selected + 1) + " / " + n, false, panelX + 8, panelY + 3, head);

        const int rowH = 12;      // tile pitch (tile = 10 tall + 2 gap)
        const int tileH = 10;
        int topY = panelY + 15;
        int availH = panelH - 17;
        int rowsFit = System.Math.Max(1, availH / rowH);
        int cols = n <= 5 ? 1 : 3;
        lp.Columns = cols; lp.ColRows = rowsFit;
        int capacity = cols * rowsFit;
        int page = lp.Selected / capacity;
        int start = page * capacity;
        if (n > capacity)
        {
            string more = (page > 0 ? "^ " : "") + (start + capacity < n ? Loc.Tr("pick.more_down", "MORE v") : "");
            if (more.Trim().Length > 0)
                BodyText(s, more.Trim(), false, panelX + panelW - 6, panelY + 3, head, rightAlign: true);
        }

        int shown = System.Math.Min(capacity, n - start);
        int cw = (panelW - 12) / cols;
        int tileW = cols == 1 ? System.Math.Min(340, panelW - 12) : cw - 4;
        int x0 = cols == 1 ? panelX + (panelW - tileW) / 2 : panelX + 6;
        // A short single column is vertically centred in the panel.
        int yPad = cols == 1 ? System.Math.Max(0, (availH - shown * rowH) / 2) : 0;

        for (int k = 0; k < shown; k++)
        {
            int i = start + k;
            int col = cols == 1 ? 0 : k / rowsFit;
            int row = cols == 1 ? k : k % rowsFit;
            int tx = x0 + col * cw;
            int ty = topY + yPad + row * rowH;
            bool sel = i == lp.Selected;
            lp.CellRects.Add((new Rect2(tx, ty, tileW, tileH), i));
            // Filled tile background — bright for the selected one, flat otherwise.
            BodyBox(s, tx, ty, tileW, tileH, sel ? MenuTheme.Style.Info : MenuTheme.Style.Tool, sel ? 21 : 20);
            BodyText(s, FitText(lp.Rows[i] ?? "", false, tileW - 8), false, tx, ty + 2, sel ? selCol : normal, centerW: tileW);
        }
    }

    // ---- list-picker debug hooks (screenshot/orchestrator harness) -----------
    public bool DebugListPickerActive => _listPicker is not null;
    public int DebugListPickerCount => _listPicker?.Rows.Count ?? 0;
    public int DebugListPickerSelected => _listPicker?.Selected ?? -1;
    public void DebugListPickerSelect(int i)
    {
        var lp = _listPicker; if (lp is null || lp.Rows.Count == 0) return;
        lp.Selected = System.Math.Clamp(i, 0, lp.Rows.Count - 1);
        _dirty = true; Refresh();
    }
    public void DebugListPickerConfirm()
    {
        if (_listPicker is null) return;
        ConfirmPicker();
        _dirty = true; Refresh(); UpdateCursor();
    }
    public void DebugListPickerCancel()
    {
        if (_listPicker is null) return;
        CancelPicker();
        _dirty = true; Refresh(); UpdateCursor();
    }
    // Selects+confirms the first row whose text CONTAINS `label`. Returns false
    // when no picker is open or no row matches.
    public bool DebugListPickerPick(string label)
    {
        var lp = _listPicker; if (lp is null) return false;
        for (int i = 0; i < lp.Rows.Count; i++)
            if ((lp.Rows[i] ?? "").Contains(label)) { lp.Selected = i; DebugListPickerConfirm(); return true; }
        return false;
    }

    // ======================================================================
    //  TABLE SELECT — inline row selection inside a screen that already shows
    //  the data table (transfer market, squad, offers, ...). Mirrors the list
    //  picker, but instead of pushing a new screen it drives the visible
    //  table's own selection var in place. Entered by FIRE on the bound field
    //  or by scrolling DOWN off the last menu entry; exited by FIRE (confirm),
    //  ESC (cancel), or scrolling UP off the first row — selection is kept in
    //  every case. The bound field shows an accent colour while active.
    // ======================================================================
    private MenuTableSelect? _tableSelect;
    private MenuScreen? _tableSelectScreen;
    private static readonly Color kActiveFieldColor = new Color(1f, 0.84f, 0.2f);   // accent gold

    private void EnterTableSelectCurrent() => EnterTableSelect(Current);

    private void EnterTableSelect(MenuScreen s)
    {
        var cfg = s.TableSelect;
        if (cfg is null || cfg.Count() == 0) return;
        _tableSelect = cfg;
        _tableSelectScreen = s;
        // Park the menu cursor on the bound field so exiting returns there.
        int fi = cfg.Field is null ? -1 : s.Entries.IndexOf(cfg.Field);
        if (fi >= 0) s.Selected = fi;
        _heldTicks = 0;
        _dirty = true;
    }

    private void ExitTableSelect()
    {
        _tableSelect = null;
        _tableSelectScreen = null;
        _heldTicks = 0;
        _dirty = true;
    }

    private void TickTableSelect()
    {
        var ts = _tableSelect!;
        if (JP("ui_cancel"))
        {
            // ESC: default returns to the entry column (keeping selection); an
            // OnCancel override (lineup editor) leaves the whole screen instead.
            var onCancel = ts.OnCancel;
            ExitTableSelect();
            onCancel?.Invoke();
            return;
        }
        int n = ts.Count();
        if (n == 0) { ExitTableSelect(); return; }
        int cur = System.Math.Clamp(ts.GetIndex(), 0, n - 1);

        bool upHeld = Input.IsActionPressed("ui_up");
        bool downHeld = Input.IsActionPressed("ui_down");
        int move = 0;
        if (JP("ui_up")) { move = -1; _heldTicks = 0; }
        else if (JP("ui_down")) { move = 1; _heldTicks = 0; }
        else if (upHeld || downHeld)
        {
            _heldTicks++;
            int dir = upHeld ? -1 : 1;
            if (_heldTicks >= 90) move = dir * 5;          // long hold: page-fast
            else if (_heldTicks >= 24 && (_heldTicks - 24) % 3 == 0) move = dir;
        }
        else _heldTicks = 0;

        // Scroll out the top: UP at the first row returns to the entry column
        // (the inverse of dropping into the table with DOWN on the last entry).
        if (move < 0 && cur == 0) { ExitTableSelect(); return; }

        if (move != 0)
        {
            int next = System.Math.Clamp(cur + move, 0, n - 1);
            if (next != cur) { ts.SetIndex(next); _dirty = true; }
        }

        if (JP("ui_accept"))
        {
            if (ts.StayOnConfirm) { ts.OnConfirm?.Invoke(); _dirty = true; }
            else { var confirm = ts.OnConfirm; ExitTableSelect(); confirm?.Invoke(); }
        }
    }

    // ---- table-select debug hooks (screenshot/orchestrator harness) ----------
    public bool DebugTableSelectActive => _tableSelect is not null;
    public int DebugTableSelectCount => _tableSelect?.Count() ?? 0;
    public int DebugTableSelectIndex => _tableSelect?.GetIndex() ?? -1;
    public void DebugTableSelectEnter()
    {
        var s = Current;
        EnterTableSelect(s);
        LayoutAndBuild(s); Refresh(); UpdateCursor();
    }
    public void DebugTableSelectMove(int delta)
    {
        var ts = _tableSelect; if (ts is null) return;
        int n = ts.Count(); if (n == 0) return;
        int cur = System.Math.Clamp(ts.GetIndex(), 0, n - 1);
        ts.SetIndex(System.Math.Clamp(cur + delta, 0, n - 1));
        var s = Current; LayoutAndBuild(s); Refresh(); UpdateCursor();
    }
    public void DebugTableSelectConfirm()
    {
        var ts = _tableSelect; if (ts is null) return;
        // Mirror TickTableSelect's FIRE: StayOnConfirm flows (lineup swap) run
        // the action and remain in table mode; others confirm then exit.
        if (ts.StayOnConfirm) ts.OnConfirm?.Invoke();
        else { var confirm = ts.OnConfirm; ExitTableSelect(); confirm?.Invoke(); }
        var s = Current; LayoutAndBuild(s); Refresh(); UpdateCursor();
    }
    public void DebugTableSelectCancel()
    {
        ExitTableSelect();
        var s = Current; LayoutAndBuild(s); Refresh(); UpdateCursor();
    }

    // ======================================================================
    //  SCREEN BUILDERS
    // ======================================================================
    private MenuScreen BuildHome()
    {
        var s = new MenuScreen { Title = Loc.Tr("home.title", "O P E N   S W O S") };
        if (SaveExists())
        {
            // Green quick-resume for the one active competition slot.
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlaySecondary, Big = false,
                Label = ContinueLabel, OnActivate = OpenSavedCompetition });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = true,
            Label = () => Loc.Tr("home.play_match", "PLAY MATCH"), OnActivate = () => Push(BuildFriendly()) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlaySecondary, Big = true,
            Label = () => Loc.Tr("home.competitions", "COMPETITIONS"), OnActivate = () => Push(BuildCompetitionsHub()) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Accent, Big = true,
            Label = () => Loc.Tr("home.multiplayer", "MULTIPLAYER"), OnActivate = () => Push(BuildLocalMultiplayer()) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = true,
            Label = () => Loc.Tr("home.team_setup", "TEAM SETUP"), OnActivate = () => Push(BuildTeamConfig()) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = true,
            Label = () => Loc.Tr("home.options", "OPTIONS"), OnActivate = () => Push(BuildOptions(0)) });
        // Career shortcut: straight into the saved career if one exists, else
        // the new-career setup. Shimmer = gentle two-colour sheen (user ask).
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Accent, Big = true, Shimmer = true,
            Label = () => Loc.Tr("home.career", "CAREER"), OnActivate = OpenCareerShortcut });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Danger, Big = true,
            Label = () => Loc.Tr("home.exit", "EXIT"), OnActivate = () => Push(BuildQuitConfirm()) });
        return s;
    }

    // Quit confirmation prompt (mirror of swos-port src/menus/mnu/quit.mnu:
    // "QUIT TO OS" / "RETURN TO GAME", initial entry on RETURN).
    private MenuScreen BuildQuitConfirm()
    {
        var s = new MenuScreen { Title = Loc.Tr("quit.title", "QUIT") };
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = true, Label = () => Loc.Tr("quit.are_you_sure", "ARE YOU SURE?") });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Danger, Big = true,
            Label = () => Loc.Tr("quit.quit_to_windows", "QUIT TO WINDOWS"), OnActivate = () => _host.QuitGame() });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlaySecondary, Big = true,
            Label = () => Loc.Tr("quit.return_to_game", "RETURN TO GAME"), OnActivate = () => Pop() });
        s.Selected = 2;   // quit.mnu: initialEntry is returnToGame — safe default
        return s;
    }

    private MenuScreen BuildFriendly()
    {
        var s = new MenuScreen { Title = Loc.Tr("friendly.title", "FRIENDLY MATCH") };
        // HOME/AWAY open a CONTINENT -> COUNTRY -> CLUB drill-down (OpenFriendlySidePicker),
        // so the old flat COUNTRY filter row is gone — the drill-down covers it.
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Info,
            Label = () => Loc.Tr("friendly.home", "HOME"), Value = () => _host.TeamTag(_host.HomeIndex), OnActivate = () => OpenFriendlySidePicker(false) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Info,
            Label = () => Loc.Tr("friendly.away", "AWAY"), Value = () => _host.TeamTag(_host.AwayIndex), OnActivate = () => OpenFriendlySidePicker(true) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
            Label = () => Loc.Tr("friendly.swap_teams", "SWAP TEAMS"), OnActivate = () => { _host.SwapTeams(); _dirty = true; } });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("friendly.pitch", "PITCH"), Value = () => _host.PitchLabel, OnStep = d => _host.StepPitch(d) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("friendly.opponent", "OPPONENT"), Value = () => _host.OpponentLabel, OnStep = d => _host.StepOpponent(d) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("friendly.half_length", "HALF LENGTH"), Value = () => _host.LengthLabel, OnStep = d => _host.StepLength(d) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("friendly.game_speed", "GAME SPEED"), Value = () => _host.SpeedLabel, OnStep = d => _host.StepSpeed(d) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = true,
            Label = () => Loc.Tr("friendly.kick_off", "KICK OFF"), OnActivate = LaunchFriendly });
        s.Selected = 0;
        return s;
    }

    // Friendly launch point. PRE MATCH MENUS off = start straight away (as the
    // menu always did); on = route through versus -> stadium line-ups first.
    private void LaunchFriendly()
    {
        if (!_host.ShowPreMatchMenus) { _host.StartMatch(); return; }
        Push(BuildVersus(Loc.Tr("friendly.title", "FRIENDLY MATCH"),
            () => Loc.Tr("friendly.context", "FRIENDLY"),
            () => _host.TeamName(_host.HomeIndex),
            () => _host.TeamName(_host.AwayIndex),
            () => $"{_host.OpponentLabel}   -   {_host.LengthLabel}",
            () => Push(BuildStadium(_host.HomeIndex, _host.AwayIndex, () => _host.StartMatch()))));
    }

    // The SWOS "versus" screen (mirror of swos-port src/menus/mnu/versus.mnu:
    // game name + round on top, then big HOME  V  AWAY). PLAY continues to the
    // stadium line-ups screen (or straight into the match for callers that
    // pass a launch action directly).
    private MenuScreen BuildVersus(string title, System.Func<string> context,
        System.Func<string> homeName, System.Func<string> awayName,
        System.Func<string>? detail, System.Action onPlay, System.Action? onViewResult = null)
    {
        var s = new MenuScreen { Title = title };
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = context });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = true, Label = homeName });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = true, Label = () => Loc.Tr("common.versus", "V") });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = true, Label = awayName });
        if (detail is not null)
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = detail });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = true,
            Label = () => Loc.Tr("versus.play", "PLAY"), OnActivate = onPlay });
        // Competition fixtures offer VIEW RESULT: simulate the tie instantly
        // instead of playing it (original SWOS had exactly this option).
        if (onViewResult is not null)
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlaySecondary, Big = false,
                Label = () => Loc.Tr("versus.view_result", "VIEW RESULT"), OnActivate = onViewResult });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        return s;
    }

    // Stadium line-ups screen (mirror of swos-port src/menus/mnu/stadium.mnu):
    // both starting XIs side by side, bench dimmed underneath, shown between
    // the versus screen and the actual kick-off for friendlies AND
    // competition fixtures. `kickOff` performs the real match launch.
    // Overrides are supplied as funcs (not fixed records) so the body — which
    // re-runs on every re-layout, e.g. when we Pop back from the lineup editor —
    // re-synthesizes the live career squad and reflects any lineup edit at once.
    private MenuScreen BuildStadium(int homeMaster, int awayMaster, System.Action kickOff,
        System.Func<TeamRecord?>? homeOverride = null, System.Func<TeamRecord?>? awayOverride = null,
        System.Action? editLineup = null)
    {
        var s = new MenuScreen { Title = Loc.Tr("stadium.title", "TEAMS"), BodyReserve = 156 };
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = true,
            Label = () => Loc.Tr("stadium.kick_off", "KICK OFF"), OnActivate = kickOff });
        if (editLineup is not null)
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("stadium.edit_lineup", "EDIT LINEUP"), OnActivate = editLineup });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.DrawLineupsBody(s, homeMaster, awayMaster, homeOverride, awayOverride);
        return s;
    }

    // Two-column line-ups body: home XI left, away XI right, bench (in-game
    // slots 11..15) dimmed below each, kit swatch + team name as headers.
    // Overrides (when supplied) render the live career squad in place of the
    // master roster.
    private void DrawLineupsBody(MenuScreen s, int homeMaster, int awayMaster,
        System.Func<TeamRecord?>? homeOverride = null, System.Func<TeamRecord?>? awayOverride = null)
    {
        int y = s.BodyTop;
        int colW = (_vw - 44) / 2;
        DrawLineupColumn(s, homeMaster, 22, y, homeOverride?.Invoke());
        DrawLineupColumn(s, awayMaster, 22 + colW + 8, y, awayOverride?.Invoke());
    }

    private void DrawLineupColumn(MenuScreen s, int teamIdx, int x, int y, TeamRecord? overrideTeam = null)
    {
        TeamRecord t;
        if (overrideTeam is not null) t = overrideTeam;
        else try { t = _host.Team(teamIdx); } catch { return; }
        int bottom = _vh - 14;
        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        var dim = new Color(0.62f, 0.66f, 0.78f);

        // column header: kit swatch + team name
        DrawKitStrip(s, x, y, 9, t.HomeKit);
        string nm = (t.Name ?? "").Trim();
        if (nm.Length > 14) nm = nm.Substring(0, 14);
        BodyText(s, nm, false, x + 13, y + 4, head);
        y += 18;

        // In-game slot order via the TEAM.* playerNumbers table (LineupOrder):
        // the same slot->roster resolution TeamDataLoader.SeedTeamData uses —
        // slot 0 the keeper, 1..10 the XI in tactics order, 11..15 the bench.
        byte[] order = t.LineupOrder;
        bool orderValid = order is { Length: 16 };
        if (orderValid)
            for (int i = 0; i < 16; i++)
                if (order[i] >= t.Players.Count) { orderValid = false; break; }

        int slots = System.Math.Min(16, t.Players.Count);
        for (int i = 0; i < slots; i++)
        {
            if (i == 11) y += 4;   // stadium.mnu separates the bench block
            if (y + 8 > bottom) break;
            int roster = orderValid ? order[i] : i;
            if (roster >= t.Players.Count) continue;
            var p = t.Players[roster];
            Color rc = i < 11 ? normal : dim;
            BodyText(s, p.ShirtNumber.ToString(), false, x + 12, y, rc, rightAlign: true);
            BodyHeadIcon(s, p.Face, x + 17, y - 1);
            string pn = (p.Name ?? "").Trim();
            if (pn.Length > 13) pn = pn.Substring(0, 13);
            BodyText(s, pn, false, x + 17 + HeadIconAdvance, y, rc);
            y += 8;
        }
    }

    // OPTIONS is paged: one builder per page in _optionsPages — adding a
    // page-2 is one more element there. Every page opens with the PAGE pager
    // row (indicator "N/M", LEFT/RIGHT flips pages, Header style so it reads
    // as navigation rather than a setting) and closes with a BACK button.
    private readonly System.Action<MenuScreen>[] _optionsPages;
    private int _optionsPage;

    private MenuScreen BuildOptions(int page)
    {
        var pages = _optionsPages;
        _optionsPage = ((page % pages.Length) + pages.Length) % pages.Length;
        var s = new MenuScreen { Title = Loc.Tr("opt.title", "OPTIONS") };
        // PAGE pager sits at the very top in its own accent colour, set apart from
        // the settings below by a spacer row, so it reads as navigation not a
        // setting (user ask).
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Accent,
            Label = () => Loc.Tr("opt.page", "PAGE"), Value = () => $"{_optionsPage + 1}/{_optionsPages.Length}",
            OnStep = SwitchOptionsPage });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => "" });  // gap
        pages[_optionsPage](s);
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        return s;
    }

    private void SwitchOptionsPage(int d)
    {
        ReplaceTop(BuildOptions(_optionsPage + d));
        // keep the cursor on the pager row so repeated LEFT/RIGHT keeps paging
        Current.Selected = 0;
    }

    private void AddOptionsPageGameplay(MenuScreen s)
    {
        // LANGUAGE selector (page 1). Cycles Loc.Current; every menu label is a
        // lambda routed through Loc.Tr, so rebuilding re-renders in the new tongue.
        // Falls back to English until a language's translation table is loaded.
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("opt.language", "LANGUAGE"), Value = () => Loc.CurrentName,
            // Every label is a lambda routed through Loc.Tr, so re-rendering (Refresh)
            // is enough to switch tongues — no ReplaceTop, so the cursor stays put.
            OnStep = d => { Loc.Current += System.Math.Sign(d == 0 ? 1 : d); _host.OnLanguageChanged(); _dirty = true; Refresh(); } });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("opt.half_length", "HALF LENGTH"), Value = () => _host.LengthLabel, OnStep = d => _host.StepLength(d) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("opt.game_speed", "GAME SPEED"), Value = () => _host.SpeedLabel, OnStep = d => _host.StepSpeed(d) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("opt.opponent", "OPPONENT"), Value = () => _host.OpponentLabel, OnStep = d => _host.StepOpponent(d) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("opt.pitch", "PITCH"), Value = () => _host.PitchLabel, OnStep = d => _host.StepPitch(d) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("opt.skill_scaling", "SKILL SCALING"), Value = () => _host.SkillScalingEnabled ? Loc.Tr("common.on", "ON") : Loc.Tr("common.off", "OFF"),
            OnStep = _ => _host.ToggleSkillScaling() });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
            Label = () => Loc.Tr("opt.skill_scaling_hint", "FAITHFUL SWOS PLAYER PRICE SKILL SCALING") });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("opt.all_teams_equal", "ALL TEAMS EQUAL"), Value = () => _host.AllPlayerTeamsEqual ? Loc.Tr("common.on", "ON") : Loc.Tr("common.off", "OFF"),
            OnStep = _ => _host.ToggleAllPlayerTeamsEqual() });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
            Label = () => Loc.Tr("opt.all_teams_equal_hint", "EQUALIZES TEAM STRENGTH FOR PLAYER TEAMS") });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("opt.pre_match_menus", "PRE MATCH MENUS"), Value = () => _host.ShowPreMatchMenus ? Loc.Tr("common.on", "ON") : Loc.Tr("common.off", "OFF"),
            OnStep = _ => _host.ToggleShowPreMatchMenus() });
        // ENERGY BAR is locked to N/A while PLAYER FATIGUE is off (the bar is
        // meaningless without the fatigue sim; user spec). Ordered AFTER fatigue
        // so the dependency reads top-to-bottom.
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("opt.player_fatigue", "PLAYER FATIGUE"), Value = () => _host.FatigueSim ? Loc.Tr("common.on", "ON") : Loc.Tr("common.off", "OFF"),
            OnStep = _ => _host.ToggleFatigueSim() });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("opt.energy_bar", "ENERGY BAR"), Value = () => !_host.FatigueSim ? Loc.Tr("common.na", "N/A") : (_host.EnergyBar ? Loc.Tr("common.on", "ON") : Loc.Tr("common.off", "OFF")),
            OnStep = _ => _host.ToggleEnergyBar() });
    }

    private void AddOptionsPageAudioDisplay(MenuScreen s)
    {
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("opt.music", "MUSIC"), Value = () => _host.MenuMusicLabel, OnStep = d => _host.StepMenuMusic(d) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
            Label = () => Loc.Tr("opt.music_hint", "AMIGA / PC / CUSTOM MENU MUSIC") });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("opt.sound", "SOUND"), Value = () => _host.SoundSourceLabel, OnStep = d => _host.StepSoundSource(d) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
            Label = () => Loc.Tr("opt.sound_hint", "PC OR AMIGA IN-MATCH SOUNDS") });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("opt.display", "DISPLAY"), Value = () => _host.DisplayModeLabel,
            OnStep = _ => _host.CycleDisplayMode() });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
            Label = () => Loc.Tr("opt.display_hint", "F11 CYCLES  ALT+ENTER TOGGLES FULLSCREEN") });
    }

    // ======================================================================
    //  LOCAL MULTIPLAYER
    // ======================================================================
    private MenuScreen BuildLocalMultiplayer()
    {
        var s = new MenuScreen { Title = Loc.Tr("mp.title", "LOCAL MULTIPLAYER") };
        // HOME/AWAY open the CONTINENT -> COUNTRY -> CLUB drill-down (shared with FRIENDLY).
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Info,
            Label = () => Loc.Tr("friendly.home", "HOME"), Value = () => _host.TeamTag(_host.HomeIndex), OnActivate = () => OpenFriendlySidePicker(false) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Info,
            Label = () => Loc.Tr("friendly.away", "AWAY"), Value = () => _host.TeamTag(_host.AwayIndex), OnActivate = () => OpenFriendlySidePicker(true) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
            Label = () => Loc.Tr("mp.controls_hint", "PLAYER 1: ARROWS+SPACE   PLAYER 2: WASD+SHIFT") });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = true,
            Label = () => Loc.Tr("mp.start", "START 2 PLAYER MATCH"), OnActivate = () => _host.StartLocalMultiplayerMatch() });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        return s;
    }

    // ======================================================================
    //  COMPETITIONS — hub, setup screens, dashboard, table/fixtures views
    // ======================================================================

    // ---- session plumbing ----------------------------------------------------
    private bool SaveExists()
    {
        if (_comp is not null) return true;
        try { return CompetitionStore.Exists(); } catch { return false; }
    }

    // Lazily load the saved competition (single slot at user://competition.json).
    private CompetitionState? LoadedComp()
    {
        if (_comp is null)
        {
            try { if (CompetitionStore.Exists()) _comp = CompetitionStore.Load(); }
            catch { _comp = null; }
            EnsureCareerWorld(_comp);
        }
        return _comp;
    }

    // Every career system (ageing, retirement, regen, growth, finances, scouting)
    // runs off Career.World and is guarded by `World is not null` in
    // CompetitionEngine.AdvanceCareerSeason — so without a materialized world they
    // ALL silently do nothing. Build it when a career is created, and rebuild it
    // lazily for saves written before the world existed.
    private void EnsureCareerWorld(CompetitionState? c)
    {
        if (c?.Career is null || c.Career.World is not null) return;
        int n = _host.TeamCount;
        if (n <= 0) return;
        var master = new System.Collections.Generic.List<OpenSwos.Assets.TeamRecord>(n);
        for (int i = 0; i < n; i++) master.Add(_host.Team(i));
        var world = OpenSwos.Competition.Career.CareerWorldBuilder.BuildWorld(c, master);
        GD.Print($"[career] world materialized: {world.Clubs.Count} clubs, " +
            $"{OpenSwos.Competition.Career.CareerWorldBuilder.CountPlayers(world)} players");
    }

    private string ContinueLabel()
    {
        var c = LoadedComp();
        if (c is null) return Loc.Tr("common.continue", "CONTINUE");
        return $"{Loc.Tr("home.continue_prefix", "CONTINUE:")} {CompTitle(c)} {CompetitionEngine.RoundLabel(c)}";
    }

    private static string CompTitle(CompetitionState c)
        => c.Career is not null ? $"{c.Name} S{c.Career.Season}" : c.Name;

    private void OpenSavedCompetition()
    {
        if (LoadedComp() is null) { RebuildCurrent(); return; }
        Push(BuildCompetitionDashboard());
    }

    // HOME "CAREER" shortcut: resume the saved game when it IS a career,
    // otherwise jump straight to the new-career setup.
    private void OpenCareerShortcut()
    {
        if (LoadedComp()?.Career is not null) { Push(BuildCompetitionDashboard()); return; }
        Push(BuildCareerSetup());
    }

    // Pop the top screen unconditionally and push a replacement (used by the
    // dashboard to re-generate itself after every state change).
    private void ReplaceTop(MenuScreen s)
    {
        if (_stack.Count > 0) DestroyScreenNodes(_stack.Pop());
        Push(s);
    }

    // Result hand-back from a competition match (see Tick). Maps the sim's
    // (player, opponent) goals back onto the fixture's home/away orientation.
    private void PollCompetitionResult()
    {
        var r = _host.TakeLastCompetitionResult();
        // The human's real in-match energy consumption (0..10), captured at the
        // same FullTime the score was; hand it to MatchEffects for the human's club.
        int? realDist = _host.TakeLastMatchHomeDistance();
        if (r is null) return;
        if (_comp is null || _pendingFixture is null) return;
        var fx = _pendingFixture;
        string playedStage = fx.Stage;
        // Map the sim's (player, opponent) goals onto the fixture's home/away
        // orientation before handing off to the shared result pipeline.
        int homeGoals, awayGoals;
        if (fx.HomeTeam == _comp.PlayerTeam) { homeGoals = r.Value.player; awayGoals = r.Value.opponent; }
        else                                 { homeGoals = r.Value.opponent; awayGoals = r.Value.player; }
        // The human always sims on the top/home slots (0..10) regardless of the
        // fixture's home/away orientation, so the drain always feeds the player's club.
        ushort humanClubId = (_comp.PlayerTeam >= 0 && _comp.PlayerTeam < _comp.Teams.Count)
            ? _comp.Teams[_comp.PlayerTeam].GlobalId : (ushort)0;
        // Resolve the human's post-match injuries (captured in-game slot ->
        // CareerPlayer id) against the SAME lineup the match launched with, i.e.
        // BEFORE ApplyFixtureResult mutates the world (recovery would shuffle
        // availability). Persisted, OR-max, AFTER recovery inside the pipeline.
        var resolvedInjuries = ResolveMatchInjuries(_host.TakeLastMatchInjuries(), humanClubId);
        ApplyFixtureResult(fx, homeGoals, awayGoals, playedStage, humanClubId, realDist ?? -1,
            resolvedInjuries);
    }

    // Maps captured (in-game slot, severity) pairs onto stable CareerPlayer ids
    // using the SAME CareerMatchTeam.BuildOrder the launch used (LineupOrder is
    // identity, so the captured PlayerInfo.index equals the BuildOrder slot).
    // Returns null when there is nothing to persist. Human club only.
    private System.Collections.Generic.List<(int playerId, int severity)>? ResolveMatchInjuries(
        System.Collections.Generic.List<(int slot, int severity)>? injuries, ushort humanClubId)
    {
        if (injuries is null || injuries.Count == 0 || humanClubId == 0) return null;
        var world = _comp?.Career?.World;
        if (world?.Clubs is null || !world.Clubs.TryGetValue(humanClubId, out var club) || club is null)
            return null;
        int playerMaster = _comp!.Teams[_comp.PlayerTeam].MasterIndex;
        var order = OpenSwos.Competition.Career.CareerMatchTeam.BuildOrder(club, _host.Team(playerMaster));
        var resolved = new System.Collections.Generic.List<(int, int)>(injuries.Count);
        foreach (var (slot, severity) in injuries)
            if (slot >= 0 && slot < order.Count)
                resolved.Add((order[slot].Id, System.Math.Clamp(severity, 1, 7)));
        return resolved.Count > 0 ? resolved : null;
    }

    // Shared post-result pipeline for BOTH the played-match hand-back
    // (PollCompetitionResult) and instant VIEW RESULT: record the score in the
    // fixture's home/away orientation, run the career match effects + transfer
    // market, simulate the rest of the AI round, save, and rebuild navigation
    // (HOME -> DASHBOARD, plus any freshly drawn knockout ceremony on top).
    private void ApplyFixtureResult(Fixture fx, int homeGoals, int awayGoals, string playedStage,
        ushort realDrainClubId = 0, int realDrainDistance = -1,
        System.Collections.Generic.List<(int playerId, int severity)>? matchInjuries = null)
    {
        if (_comp is null) return;
        CompetitionEngine.RecordResult(_comp, fx, homeGoals, awayGoals);
        _pendingFixture = null;
        // Career: the match moves form / growth / fatigue for both clubs.
        if (_comp.Career?.World is not null && fx.Played)
        {
            OpenSwos.Competition.Career.MatchEffects.ApplyFixture(
                _comp.Career.World,
                _comp.Teams[fx.HomeTeam].GlobalId,
                _comp.Teams[fx.AwayTeam].GlobalId,
                fx.HomeGoals, fx.AwayGoals, fx.Round,
                realDrainClubId, realDrainDistance);
            // Persist the human's NEW match injuries (OR-max) AFTER MatchEffects
            // ran the per-fixture recovery, matching the original order
            // (UpdatePlayerInjuries writes after the round's recover pass,
            // swos.asm:35207 recover -> 35651 write). Human club only.
            if (matchInjuries is not null && realDrainClubId != 0
                && _comp.Career.World.Clubs.TryGetValue(realDrainClubId, out var injClub) && injClub is not null)
            {
                foreach (var (playerId, severity) in matchInjuries)
                {
                    var cp = injClub.Squad.Find(q => q.Id == playerId);
                    if (cp is not null)
                        cp.InjurySeverity = System.Math.Max(cp.InjurySeverity, System.Math.Clamp(severity, 0, 7));
                }
            }
            // Advance the transfer market one round: age offers, maybe generate new bids.
            OpenSwos.Competition.Career.TransferOffers.Tick(_comp);
        }
        CompetitionEngine.SimulateAiRound(_comp);
        CompetitionStore.Save(_comp);
        // Fresh navigation: HOME -> DASHBOARD, so BACK never lands on a stale screen.
        ResetToHome();
        Push(BuildCompetitionDashboard());
        // Cup/tournament: a freshly drawn knockout round gets its ceremony on
        // top of the dashboard (original: ChooseTeamsMenu "NEXT ROUND DRAW").
        MaybePushDrawCeremony(playedStage);
        _dirty = true;
    }

    // Friendly (non-competition) full-time hand-back: offer an immediate
    // rematch, mirroring swos-port src/menus/mnu/replayExit.mnu. The Active
    // setter's ResetToHome has already run, so this lands on top of HOME.
    private void PollFriendlyEnd()
    {
        if (!_host.TakeFriendlyJustEnded()) return;
        Push(BuildPlayAgain());
        _dirty = true;
    }

    private MenuScreen BuildPlayAgain()
    {
        var s = new MenuScreen { Title = Loc.Tr("playagain.title", "FULL TIME") };
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = true,
            Label = () => Loc.Tr("playagain.play_again", "PLAY AGAIN"), OnActivate = () => _host.StartMatch() });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = true,
            Label = () => Loc.Tr("playagain.exit", "EXIT"), OnActivate = () => ResetToHome() });
        return s;
    }

    // ---- hub -------------------------------------------------------------------
    private MenuScreen BuildCompetitionsHub()
    {
        var s = new MenuScreen { Title = Loc.Tr("comp.title", "COMPETITIONS") };
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlaySecondary, Big = true,
            Label = () => Loc.Tr("comp.new_league", "NEW LEAGUE"), OnActivate = () => Push(BuildLeagueSetup()) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlaySecondary, Big = true,
            Label = () => Loc.Tr("comp.new_cup", "NEW CUP"), OnActivate = () => Push(BuildCupSetup()) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlaySecondary, Big = true,
            Label = () => Loc.Tr("comp.new_tournament", "NEW TOURNAMENT"), OnActivate = () => Push(BuildTournamentSetup()) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlaySecondary, Big = true,
            Label = () => Loc.Tr("comp.preset_competition", "PRESET COMPETITION"), OnActivate = () => Push(BuildPresetList()) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Accent, Big = true,
            Label = () => Loc.Tr("comp.new_career", "NEW CAREER"), OnActivate = () => Push(BuildCareerSetup()) });
        if (SaveExists())
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = false,
                Label = ContinueLabel, OnActivate = OpenSavedCompetition });
        }
        if (CompetitionStore.ListSlots().Count > 0)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("comp.load_game", "LOAD GAME"), OnActivate = () => { _slotDeleteMode = false; Push(BuildLoadGame()); } });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        return s;
    }

    // ---- setup helpers ---------------------------------------------------------
    private static int StepIdx(int idx, int d, int n) => ((idx + d) % n + n) % n;

    // ---- nation picker (continent -> country -> team, like original SWOS) ----
    private System.Collections.Generic.List<int> NationsWithTeams()
    {
        if (_nationsWithTeams is null)
        {
            _nationsWithTeams = new System.Collections.Generic.List<int>();
            _nationTeamCount = new System.Collections.Generic.Dictionary<int, int>();
            for (int n = 0; n < NationNames.Count; n++)
            {
                int count = _host.TeamsByNationDivision(n, -1, 999).Count;
                if (count <= 0) continue;
                _nationsWithTeams.Add(n);
                _nationTeamCount[n] = count;
            }
        }
        return _nationsWithTeams;
    }

    private int NationTeamCount(int nation)
    {
        NationsWithTeams();   // ensure the cache is built
        if (_nationTeamCount!.TryGetValue(nation, out int c)) return c;
        // nation byte outside the CN table (odd data) — probe directly
        return _host.TeamsByNationDivision(nation, -1, 999).Count;
    }

    private System.Collections.Generic.List<int> NationsInContinent(int continent)
    {
        var list = new System.Collections.Generic.List<int>();
        foreach (int n in NationsWithTeams())
            if (NationNames.Continent(n) == continent) list.Add(n);
        return list;
    }

    // Seed the picker from the current home team so setup screens open on the
    // player's own continent/country (and their team pre-selected).
    private void InitSetupNationPicker()
    {
        _setupTeamIdx = _host.HomeIndex;
        _setupNation = _host.TeamNation(_setupTeamIdx);
        _setupContinent = NationNames.Continent(_setupNation);
    }

    // Country changed: snap YOUR TEAM to the country's first team.
    private void SetSetupNation(int nation)
    {
        _setupNation = nation;
        var teams = _host.TeamsByNationDivision(nation, -1, 1);
        if (teams.Count > 0) _setupTeamIdx = teams[0];
    }

    // The shared CONTINENT / COUNTRY / YOUR TEAM rows every competition setup
    // screen opens with. Callers must InitSetupNationPicker() first. Each row is
    // a list picker (FIRE opens a scrollable list), never a value stepper.
    private void AddNationPickerRows(MenuScreen s)
    {
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("pick.continent", "CONTINENT"), Value = () => NationNames.ContinentName(_setupContinent),
            OnActivate = OpenSetupContinentPicker });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("pick.country", "COUNTRY"),
            Value = () => $"{NationNames.Name(_setupNation)} ({NationTeamCount(_setupNation)})",
            OnActivate = OpenSetupCountryPicker });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Info,
            Label = () => Loc.Tr("pick.your_team", "YOUR TEAM"), Value = () => _host.TeamTag(_setupTeamIdx), OnActivate = OpenSetupTeamPicker });
    }

    // ---- list-picker openers for the nation/team choosers --------------------
    private System.Collections.Generic.List<int> AllMasterIndices()
    {
        int n = _host.TeamCount;
        var list = new System.Collections.Generic.List<int>(n);
        for (int i = 0; i < n; i++) list.Add(i);
        return list;
    }

    // FRIENDLY / LOCAL-MP country filter picker: "ALL" plus every nation that
    // has teams. Selecting a country snaps both sides into it (SetFriendlyNation).
    private void OpenFriendlyCountryPicker()
    {
        var nations = NationsWithTeams();
        var rows = new System.Collections.Generic.List<string> { Loc.Tr("pick.all_countries", "ALL COUNTRIES") };
        foreach (int n in nations) rows.Add($"{NationNames.Name(n)} ({NationTeamCount(n)})");
        int cur = _friendlyNation < 0 ? 0 : nations.IndexOf(_friendlyNation) + 1;
        PushListPicker(Loc.Tr("pick.select_country", "SELECT COUNTRY"), rows, cur, idx =>
        {
            SetFriendlyNation(idx <= 0 ? -1 : nations[idx - 1]);
            _dirty = true;
        });
    }

    // Original-SWOS team chooser: a CONTINENT -> COUNTRY -> CLUB drill-down of
    // 3-column tile grids, instead of one flat ~1730-team list. Every level runs
    // through SwapListPicker, so the WHOLE chain lives on ONE picker screen whose
    // contents are swapped in place (no per-level Push/Pop — that left an earlier
    // level bleeding through under a later one). FIRE descends; ESC climbs back a
    // level (top level exits). Seeds from `currentIdx` so it opens on that team's
    // own continent/country. Shared by FRIENDLY, LOCAL-MP and TEAM SETUP.
    private void OpenTeamDrillDown(string what, int currentIdx, System.Action<int> onPicked)
    {
        int startNation = _host.TeamNation(currentIdx);
        int startCont = NationNames.Continent(startNation);

        // Level 3 — clubs of one country. Leaf: FIRE closes the picker then picks.
        void ShowTeam(int nation)
        {
            var teams = _host.TeamsByNationDivision(nation, -1, 999);
            if (teams.Count == 0) { ShowCountry(NationNames.Continent(nation)); return; }
            var rows = new System.Collections.Generic.List<string>(teams.Count);
            foreach (int m in teams) rows.Add(_host.TeamTag(m));
            int cur = teams.IndexOf(currentIdx);
            SwapListPicker($"{what}: {NationNames.Name(nation)}", rows, cur < 0 ? 0 : cur,
                idx => { int t = teams[idx]; EndListPicker(); Pop(); onPicked(t); },
                onCancel: () => ShowCountry(NationNames.Continent(nation)));
        }

        // Level 2 — countries of one continent (skip the step if only one).
        void ShowCountry(int cont)
        {
            var nations = NationsInContinent(cont);
            if (nations.Count == 0) return;
            if (nations.Count == 1) { ShowTeam(nations[0]); return; }
            var rows = new System.Collections.Generic.List<string>();
            foreach (int n in nations) rows.Add($"{NationNames.Name(n)} ({NationTeamCount(n)})");
            int cur = nations.IndexOf(startNation);
            SwapListPicker($"{what}: {Loc.Tr("pick.select_country", "SELECT COUNTRY")}", rows, cur < 0 ? 0 : cur,
                idx => ShowTeam(nations[idx]),
                onCancel: ShowContinent);
        }

        // Level 1 — continents that have any teams (skip if only one).
        void ShowContinent()
        {
            var conts = new System.Collections.Generic.List<int>();
            for (int c = 0; c < 6; c++) if (NationsInContinent(c).Count > 0) conts.Add(c);
            if (conts.Count == 0) return;
            if (conts.Count == 1) { ShowCountry(conts[0]); return; }
            var rows = new System.Collections.Generic.List<string>();
            foreach (int c in conts) rows.Add(NationNames.ContinentName(c));
            int cur = conts.IndexOf(startCont);
            // No onCancel on the top level: ESC drops back to the setup screen.
            SwapListPicker($"{what}: {Loc.Tr("pick.select_continent", "SELECT CONTINENT")}", rows, cur < 0 ? 0 : cur,
                idx => ShowCountry(conts[idx]));
        }

        ShowContinent();
    }

    private void OpenFriendlySidePicker(bool away)
    {
        int cur = away ? _host.AwayIndex : _host.HomeIndex;
        OpenTeamDrillDown(away ? Loc.Tr("pick.away_team", "AWAY TEAM") : Loc.Tr("pick.home_team", "HOME TEAM"), cur, pick =>
        {
            int other = away ? _host.HomeIndex : _host.AwayIndex;
            // Keep HOME != AWAY: if they'd collide, pick another club from the
            // same country (falls through unchanged if none exists).
            if (pick == other)
                foreach (int alt in _host.TeamsByNationDivision(_host.TeamNation(pick), -1, 999))
                    if (alt != other) { pick = alt; break; }
            if (away) _host.SetAwayTeam(pick); else _host.SetHomeTeam(pick);
            _dirty = true;
        });
    }

    private void OpenSetupContinentPicker()
    {
        var conts = new System.Collections.Generic.List<int>();
        for (int c = 0; c < 6; c++) if (NationsInContinent(c).Count > 0) conts.Add(c);
        if (conts.Count == 0) return;
        var rows = new System.Collections.Generic.List<string>();
        foreach (int c in conts) rows.Add(NationNames.ContinentName(c));
        int cur = conts.IndexOf(_setupContinent);
        PushListPicker(Loc.Tr("pick.select_continent", "SELECT CONTINENT"), rows, cur < 0 ? 0 : cur, idx =>
        {
            _setupContinent = conts[idx];
            var nations = NationsInContinent(_setupContinent);
            if (nations.Count > 0) SetSetupNation(nations[0]);
            _dirty = true;
        });
    }

    private void OpenSetupCountryPicker()
    {
        var nations = NationsInContinent(_setupContinent);
        if (nations.Count == 0) return;
        var rows = new System.Collections.Generic.List<string>();
        foreach (int n in nations) rows.Add($"{NationNames.Name(n)} ({NationTeamCount(n)})");
        int cur = nations.IndexOf(_setupNation);
        PushListPicker(Loc.Tr("pick.select_country", "SELECT COUNTRY"), rows, cur < 0 ? 0 : cur, idx => { SetSetupNation(nations[idx]); _dirty = true; });
    }

    private void OpenSetupTeamPicker()
    {
        var teams = _host.TeamsByNationDivision(_setupNation, -1, 999);
        if (teams.Count == 0) return;
        var rows = new System.Collections.Generic.List<string>();
        foreach (int m in teams) rows.Add(_host.TeamTag(m));
        int cur = teams.IndexOf(_setupTeamIdx);
        PushListPicker(Loc.Tr("pick.select_your_team", "SELECT YOUR TEAM"), rows, cur < 0 ? 0 : cur, idx => { _setupTeamIdx = teams[idx]; _dirty = true; });
    }

    // ---- friendly / local-MP country filter ("ALL" = whole master list) ------
    private string FriendlyNationLabel()
        => _friendlyNation < 0 ? Loc.Tr("pick.all", "ALL")
           : $"{NationNames.Name(_friendlyNation)} ({NationTeamCount(_friendlyNation)})";

    // Apply a friendly/local-MP country filter (nation < 0 = ALL). Snaps both
    // sides into the chosen country so the HOME/AWAY rows show its teams at once.
    private void SetFriendlyNation(int nation)
    {
        _friendlyNation = nation;
        if (nation < 0) return;
        var teams = _host.TeamsByNationDivision(nation, -1, 999);
        if (teams.Count == 0) return;
        _host.SetHomeTeam(teams[0]);
        _host.SetAwayTeam(teams[teams.Count > 1 ? 1 : 0]);
    }

    // Adds one random master index not yet in pool (guaranteed to succeed:
    // RandomTeams returns pool.Count+1 distinct indices, so one must be new).
    private void AddRandomDistinct(System.Collections.Generic.List<int> pool)
    {
        foreach (int idx in _host.RandomTeams(pool.Count + 1, -1))
            if (!pool.Contains(idx)) { pool.Add(idx); return; }
    }

    // Master-list indices for a competition: your team first, then (count-1)
    // others per the entrants mode, random-filled if the pool runs short.
    private System.Collections.Generic.List<int> BuildEntrants(int you, int count, int mode)
    {
        var result = new System.Collections.Generic.List<int> { you };
        if (mode == 2)
        {
            foreach (int idx in _host.RandomTeams(count, you))
            {
                if (result.Count >= count) break;
                if (idx == you || result.Contains(idx)) continue;
                result.Add(idx);
            }
        }
        else
        {
            int nation = _host.TeamNation(you);
            int division = mode == 0 ? _host.TeamDivision(you) : -1;
            foreach (int idx in _host.TeamsByNationDivision(nation, division, count + 4))
            {
                if (result.Count >= count) break;
                if (idx == you || result.Contains(idx)) continue;
                result.Add(idx);
            }
        }
        while (result.Count < count) AddRandomDistinct(result);
        return result;
    }

    private System.Collections.Generic.List<TeamRef> MakeTeamRefs(System.Collections.Generic.List<int> masters)
    {
        var list = new System.Collections.Generic.List<TeamRef>();
        foreach (int m in masters)
        {
            list.Add(new TeamRef
            {
                MasterIndex = m,
                GlobalId = _host.TeamGlobalId(m),
                Name = (_host.TeamName(m) ?? "").Trim().ToUpperInvariant(),
                Strength = _host.TeamStrength(m),
            });
        }
        return list;
    }

    // Deterministic seed from the participant set + format selections (FNV-1a).
    // Never wall-clock, so recreating with identical picks reproduces the draw.
    private static int SeedFrom(System.Collections.Generic.List<int> masters, int salt)
    {
        uint h = 2166136261u;
        foreach (int m in masters) { h ^= (uint)m; h *= 16777619u; }
        h ^= (uint)salt; h *= 16777619u;
        if (h == 0) h = 0x9E3779B9u;
        return unchecked((int)h);
    }

    private void OpenNewCompetition(CompetitionState comp)
    {
        _comp = comp;
        _pendingFixture = null;
        CompetitionStore.Save(comp);
        // Clean stack: HOME -> HUB -> DASHBOARD (BACK from dashboard = hub).
        ResetToHome();
        Push(BuildCompetitionsHub());
        Push(BuildCompetitionDashboard());
        // Cup: the round-1 draw ceremony sits on top of the fresh dashboard.
        MaybePushDrawCeremony(null);
    }

    // ---- next-round-draw ceremony ---------------------------------------------
    // Original: ChooseTeamsMenu / "NEXT ROUND DRAW" (swos.asm strings
    // 185626-185642). Knockout = any stage that is neither the league nor a
    // group stage ("LEAGUE" / "GROUP*" are excluded).
    private static bool IsKnockoutStage(string stage)
        => stage != "LEAGUE" && !stage.StartsWith("GROUP", System.StringComparison.Ordinal);

    // Push the "<STAGE> DRAW" screen when the player's NEXT fixture opens a
    // knockout stage different from the one just recorded (playedStage null =
    // competition creation, so any knockout round-1 draw shows).
    private void MaybePushDrawCeremony(string? playedStage)
    {
        var c = _comp;
        if (c is null || c.Finished) return;
        var next = CompetitionEngine.NextPlayerFixture(c);
        if (next is null) return;
        if (!IsKnockoutStage(next.Stage)) return;
        if (playedStage is not null && next.Stage == playedStage) return;
        Push(BuildDrawCeremony(next.Stage, next.Round));
    }

    private MenuScreen BuildDrawCeremony(string stage, int round)
    {
        var s = new MenuScreen { Title = stage + " " + Loc.Tr("draw.title_suffix", "DRAW"), BodyReserve = 72 };
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = true,
            Label = () => Loc.Tr("common.continue", "CONTINUE"), OnActivate = () => Pop() });
        s.Body = client => client.DrawDrawCeremonyBody(s, stage, round);
        return s;
    }

    // The round's pairings, one "HOME  V  AWAY" line each, player's line gold.
    private void DrawDrawCeremonyBody(MenuScreen s, string stage, int round)
    {
        var c = _comp;
        if (c is null) return;
        int y = s.BodyTop, bottom = _vh - 14;
        var normal = new Color(0.92f, 0.94f, 1f);
        var gold = new Color(1f, 0.85f, 0.25f);
        foreach (var f in c.Fixtures)
        {
            if (f.Round != round || f.Stage != stage) continue;
            if (y + 8 > bottom) break;
            bool mine = f.HomeTeam == c.PlayerTeam || f.AwayTeam == c.PlayerTeam;
            BodyText(s, $"{TeamShort(c, f.HomeTeam, 15)}  V  {TeamShort(c, f.AwayTeam, 15)}",
                false, 60, y, mine ? gold : normal);
            y += 8;
        }
    }

    // ---- setup screens ---------------------------------------------------------
    private MenuScreen BuildLeagueSetup()
    {
        InitSetupNationPicker();
        var s = new MenuScreen { Title = Loc.Tr("setup.league_title", "NEW LEAGUE") };
        AddNationPickerRows(s);
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("setup.size", "SIZE"), Value = () => kLeagueSizes[_leagueSizeIdx] + " " + Loc.Tr("setup.teams_suffix", "TEAMS"),
            OnStep = d => _leagueSizeIdx = StepIdx(_leagueSizeIdx, d, kLeagueSizes.Length) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("setup.entrants", "ENTRANTS"), Value = () => kEntrantModes[_entrantsMode],
            OnStep = d => _entrantsMode = StepIdx(_entrantsMode, d, kEntrantModes.Length) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("setup.rounds", "ROUNDS"), Value = () => _leagueRoundsIdx == 0 ? Loc.Tr("setup.rounds_single", "SINGLE") : Loc.Tr("setup.rounds_double", "DOUBLE"),
            OnStep = d => _leagueRoundsIdx = StepIdx(_leagueRoundsIdx, d, 2) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = true,
            Label = () => Loc.Tr("setup.create_league", "CREATE LEAGUE"), OnActivate = CreateLeagueNow });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        return s;
    }

    private void CreateLeagueNow()
    {
        int size = kLeagueSizes[_leagueSizeIdx];
        bool dbl = _leagueRoundsIdx == 1;
        var masters = BuildEntrants(_setupTeamIdx, size, _entrantsMode);
        int seed = SeedFrom(masters, 100 + size * 8 + _entrantsMode * 2 + (dbl ? 1 : 0));
        OpenNewCompetition(CompetitionEngine.CreateLeague(
            "LEAGUE", MakeTeamRefs(masters), masters.IndexOf(_setupTeamIdx), dbl, seed));
    }

    private MenuScreen BuildCupSetup()
    {
        InitSetupNationPicker();
        var s = new MenuScreen { Title = Loc.Tr("setup.cup_title", "NEW CUP") };
        AddNationPickerRows(s);
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("setup.size", "SIZE"), Value = () => kCupSizes[_cupSizeIdx] + " " + Loc.Tr("setup.teams_suffix", "TEAMS"),
            OnStep = d => _cupSizeIdx = StepIdx(_cupSizeIdx, d, kCupSizes.Length) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("setup.entrants", "ENTRANTS"), Value = () => kEntrantModes[_entrantsMode],
            OnStep = d => _entrantsMode = StepIdx(_entrantsMode, d, kEntrantModes.Length) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = true,
            Label = () => Loc.Tr("setup.create_cup", "CREATE CUP"), OnActivate = CreateCupNow });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        return s;
    }

    private void CreateCupNow()
    {
        int size = kCupSizes[_cupSizeIdx];   // power of two by construction
        var masters = BuildEntrants(_setupTeamIdx, size, _entrantsMode);
        int seed = SeedFrom(masters, 300 + size * 4 + _entrantsMode);
        OpenNewCompetition(CompetitionEngine.CreateCup(
            "CUP", MakeTeamRefs(masters), masters.IndexOf(_setupTeamIdx), seed));
    }

    private MenuScreen BuildTournamentSetup()
    {
        InitSetupNationPicker();
        var s = new MenuScreen { Title = Loc.Tr("setup.tournament_title", "NEW TOURNAMENT") };
        AddNationPickerRows(s);
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("setup.groups", "GROUPS"), Value = () => kTournamentGroups[_tournamentGroupsIdx] + " " + Loc.Tr("setup.groups_of_4_suffix", "GROUPS OF 4"),
            OnStep = d => _tournamentGroupsIdx = StepIdx(_tournamentGroupsIdx, d, kTournamentGroups.Length) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("setup.entrants", "ENTRANTS"), Value = () => kEntrantModes[_entrantsMode],
            OnStep = d => _entrantsMode = StepIdx(_entrantsMode, d, kEntrantModes.Length) });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = true,
            Label = () => Loc.Tr("setup.create_tournament", "CREATE TOURNAMENT"), OnActivate = CreateTournamentNow });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        return s;
    }

    private void CreateTournamentNow()
    {
        int groups = kTournamentGroups[_tournamentGroupsIdx];
        int size = groups * 4;
        var masters = BuildEntrants(_setupTeamIdx, size, _entrantsMode);
        int seed = SeedFrom(masters, 500 + groups * 4 + _entrantsMode);
        OpenNewCompetition(CompetitionEngine.CreateTournament(
            "TOURNAMENT", MakeTeamRefs(masters), masters.IndexOf(_setupTeamIdx), groups, seed));
    }

    private MenuScreen BuildCareerSetup()
    {
        InitSetupNationPicker();
        var s = new MenuScreen { Title = Loc.Tr("career.title", "NEW CAREER") };
        AddNationPickerRows(s);
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
            Label = () => Loc.Tr("career.blurb_seasons", "LEAGUE AND DOMESTIC CUP EVERY SEASON") });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
            Label = () => Loc.Tr("career.blurb_promotion", "WIN THE LEAGUE FOR PROMOTION, FINISH LAST AND DROP") });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = true,
            Label = () => Loc.Tr("career.start", "START CAREER"), OnActivate = OpenManagerNameEntry });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        return s;
    }

    // League pool for a career season: same nation+division as the club, up to
    // 16 incl. the club; fewer than 8 available -> same nation, any division.
    // Random-fills to at least 8 and an even count for clean round-robins.
    private System.Collections.Generic.List<int> BuildCareerLeaguePool(int you, int nation, int division)
    {
        var pool = new System.Collections.Generic.List<int> { you };
        foreach (int idx in _host.TeamsByNationDivision(nation, division, 24))
        {
            if (pool.Count >= 16) break;
            if (idx == you || pool.Contains(idx)) continue;
            pool.Add(idx);
        }
        if (pool.Count < 8)
        {
            pool = new System.Collections.Generic.List<int> { you };
            foreach (int idx in _host.TeamsByNationDivision(nation, -1, 24))
            {
                if (pool.Count >= 16) break;
                if (idx == you || pool.Contains(idx)) continue;
                pool.Add(idx);
            }
        }
        while (pool.Count < 8 || (pool.Count & 1) == 1) AddRandomDistinct(pool);
        return pool;
    }

    // Cup pool for a career season: 16 same-nation teams incl. the club,
    // random-nation fill if the nation is short.
    private System.Collections.Generic.List<int> BuildCareerCupPool(int you, int nation)
    {
        var pool = new System.Collections.Generic.List<int> { you };
        foreach (int idx in _host.TeamsByNationDivision(nation, -1, 32))
        {
            if (pool.Count >= 16) break;
            if (idx == you || pool.Contains(idx)) continue;
            pool.Add(idx);
        }
        while (pool.Count < 16) AddRandomDistinct(pool);
        return pool;
    }

    // START CAREER first asks who the manager is (original career flow greets
    // the manager by name — ManagementRecordMenu "%a %b", swos.asm:44076).
    // ENTER creates the career with the typed name; ESC returns to the setup.
    private void OpenManagerNameEntry()
    {
        PushNameEntry(Loc.Tr("mgr.title", "MANAGER NAME"), Loc.Tr("mgr.default_name", "PLAYER"), 14,
            Loc.Tr("mgr.hint", "TYPE NAME   LEFT/RIGHT TITLE   ENTER OK   ESC CANCEL"),
            name => CreateCareerNow(name.Length == 0 ? Loc.Tr("mgr.default_name", "PLAYER") : name, kManagerTitles[_mgrTitleIdx]),
            sideRow: () => Loc.Tr("mgr.title_row", "TITLE") + "  " + kManagerTitles[_mgrTitleIdx],
            onSide: _ => { _mgrTitleIdx ^= 1; _dirty = true; });
    }

    private void CreateCareerNow(string managerName, string managerTitle)
    {
        int you = _setupTeamIdx;
        int nation = _host.TeamNation(you);
        int division = _host.TeamDivision(you);
        var leaguePool = BuildCareerLeaguePool(you, nation, division);
        var cupPool = BuildCareerCupPool(you, nation);
        int seed = SeedFrom(leaguePool, 900 + you);
        // Club sits first in leaguePool, so playerTeam = 0.
        var comp = CompetitionEngine.CreateCareer(
            "CAREER", MakeTeamRefs(leaguePool), MakeTeamRefs(cupPool), 0, nation, division, seed);
        // Create first, then stamp the manager identity — OpenNewCompetition
        // saves the state, so the name/title land in the very first autosave.
        if (comp.Career is not null)
        {
            comp.Career.ManagerName = managerName;
            comp.Career.ManagerTitle = managerTitle;
        }
        // Materialize the persistent career world BEFORE the first autosave, so
        // ages / potential / stamina exist and the season systems actually run.
        EnsureCareerWorld(comp);
        OpenNewCompetition(comp);
    }

    // ---- dashboard ---------------------------------------------------------------
    private MenuScreen BuildCompetitionDashboard()
    {
        var c = LoadedComp();
        if (c is null) return BuildCompetitionsHub();
        // Whatever fixture was pending is still unplayed in the engine (the
        // match was abandoned) — clear it so PLAY NEXT MATCH works again.
        _pendingFixture = null;
        // The SAVE AS confirmation is one-shot: any full rebuild clears it.
        // (It is set just before Pop()-ing back onto the live dashboard, and
        // Pop re-lays-out the existing screen without calling this builder.)
        _saveNotice = null;

        bool retired = c.Career?.Retired == true;
        // Reserve the mini-table / round-fixtures block below the buttons. An
        // active career carries RECORD / SQUAD / SAVE AS / RETIRE, so leave
        // enough room for the taller compact entry column before the body.
        var s = new MenuScreen { Title = CompTitle(c), BodyReserve = c.Career is not null && !retired ? 60 : 84 };
        // Single status line: round + summary, with CHAMPIONS / ELIMINATED
        // merged in so the entry column never stacks two status labels.
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = DashboardStatusLine });

        if (retired)
        {
            // Retired career: management record + abandon only — no fixtures
            // are playable (original retire flow: swos.asm:381,44509).
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("dash.management_record", "MANAGEMENT RECORD"), OnActivate = () => Push(BuildManagementRecord(careerOver: true)) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Danger, Big = false,
                Label = () => Loc.Tr("dash.abandon", "ABANDON"), OnActivate = AbandonCompetition });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
                Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
            s.Body = client => client.DrawCompetitionBody(s);
            return s;
        }

        var playerNext = c.Finished ? null : CompetitionEngine.NextPlayerFixture(c);
        var anyNext = c.Finished ? null : CompetitionEngine.NextFixture(c);
        bool aiOnlyNext = anyNext is not null && anyNext.HomeTeam != c.PlayerTeam && anyNext.AwayTeam != c.PlayerTeam;
        bool rollover = c.Career is not null && (CompetitionEngine.PendingSeasonRollover(c) || c.Finished);

        if (playerNext is not null)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = false,
                Label = () => Loc.Tr("dash.play_next_match", "PLAY NEXT MATCH"), OnActivate = PlayNextCompetitionMatch });
        }
        if (aiOnlyNext)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlaySecondary, Big = false,
                Label = () => Loc.Tr("dash.continue", "CONTINUE"), OnActivate = FastForwardAi });
        }
        if (rollover)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlaySecondary, Big = false,
                Label = () => Loc.Tr("dash.next_season", "NEXT SEASON"), OnActivate = AdvanceSeason });
        }
        if (c.Kind != CompetitionKind.Cup)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("dash.table", "TABLE"), OnActivate = () => Push(BuildTableScreen()) });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
            Label = () => Loc.Tr("dash.fixtures", "FIXTURES"), OnActivate = () => Push(BuildFixturesScreen()) });
        if (c.Career is not null)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("dash.squad", "SQUAD"), OnActivate = () => Push(BuildSquadScreen()) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("dash.lineup", "LINEUP"), OnActivate = () => Push(BuildLineupEditor()) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("dash.transfers", "TRANSFERS"), OnActivate = () => Push(BuildTransferMarket()) });
            // OFFERS: incoming bids for your players. Blinks with a "!" prefix
            // while any bid is unseen (original SWOS: the entry flashes).
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Accent, Big = false,
                Label = OffersEntryLabel, OnActivate = () => Push(BuildOffersScreen()) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("dash.staff", "STAFF"), OnActivate = () => Push(BuildStaffScreen()) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("dash.scouting", "SCOUTING"), OnActivate = () => Push(BuildScoutingScreen()) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("dash.record", "RECORD"), OnActivate = () => Push(BuildManagementRecord(careerOver: false)) });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
            Label = () => Loc.Tr("dash.save_as", "SAVE AS"), OnActivate = OpenSaveAsEntry });
        if (c.Career is not null)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Danger, Big = false,
                Label = () => Loc.Tr("dash.retire", "RETIRE"), OnActivate = () => Push(BuildRetireConfirm()) });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Danger, Big = false,
            Label = () => Loc.Tr("dash.abandon", "ABANDON"), OnActivate = AbandonCompetition });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });

        s.Body = client => client.DrawCompetitionBody(s);
        return s;
    }

    // Round + player summary, with the finished/eliminated state folded into
    // the same line (the dashboard keeps exactly one status label).
    private string DashboardStatusLine()
    {
        var c = _comp;
        if (c is null) return "";
        if (_saveNotice is not null) return _saveNotice;   // one-shot SAVE AS confirmation
        if (c.Career?.Retired == true)
            return Loc.Tr("dash.status_retired", "RETIRED") + "  " + CompetitionEngine.PlayerSummary(c);
        if (c.Finished && c.Champion >= 0)
            return CompetitionEngine.RoundLabel(c) + "  " + Loc.Tr("dash.status_champions", "CHAMPIONS:") + " " + TeamShort(c, c.Champion, 18);
        string line = CompetitionEngine.RoundLabel(c) + "  " + CompetitionEngine.PlayerSummary(c);
        if (!c.Finished && CompetitionEngine.NextPlayerFixture(c) is null && !CompetitionEngine.IsPlayerAlive(c))
            line += "  " + Loc.Tr("dash.status_eliminated", "ELIMINATED");
        return line;
    }

    private void PlayNextCompetitionMatch()
    {
        var c = _comp;
        if (c is null) return;
        var fx = CompetitionEngine.NextPlayerFixture(c);
        if (fx is null) { ReplaceTop(BuildCompetitionDashboard()); return; }
        // PRE MATCH MENUS off = launch straight away (as before); on = route
        // through the versus screen (with competition + round context) and the
        // stadium line-ups first, in the fixture's home/away orientation.
        if (!_host.ShowPreMatchMenus) { LaunchCompetitionFixture(fx); return; }
        int homeMaster = c.Teams[fx.HomeTeam].MasterIndex;
        int awayMaster = c.Teams[fx.AwayTeam].MasterIndex;
        // Offer EDIT LINEUP only when the manager's own club is a live career
        // club (so there is a squad + PreferredLineup to edit). The editor
        // mutates that club; on Pop the stadium body re-synthesizes and the
        // kick-off re-synthesizes too, so the edit shows up immediately.
        System.Action? editLineup = CurrentCareerClub() is not null
            ? () => Push(BuildLineupEditor())
            : null;
        Push(BuildVersus(Loc.Tr("next.title", "NEXT MATCH"),
            () => $"{c.Name}  {CompetitionEngine.RoundLabel(c)}",
            () => TeamShort(c, fx.HomeTeam, 20),
            () => TeamShort(c, fx.AwayTeam, 20),
            null,
            () => Push(BuildStadium(homeMaster, awayMaster, () => LaunchCompetitionFixture(fx),
                () => SynthesizeCareerTeam(homeMaster), () => SynthesizeCareerTeam(awayMaster),
                editLineup)),
            () => ViewResultCompetitionMatch(fx)));
    }

    // SWOS-style VIEW RESULT: simulate the pending player fixture instantly
    // (deterministic strength-weighted score) and run it through the same
    // post-result pipeline as a played match — no sim launch. Ends on a small
    // FULL TIME notice showing the score, on top of the rebuilt dashboard.
    private void ViewResultCompetitionMatch(Fixture fx)
    {
        var c = _comp;
        if (c is null) return;
        string playedStage = fx.Stage;
        string homeName = TeamShort(c, fx.HomeTeam, 20);
        string awayName = TeamShort(c, fx.AwayTeam, 20);
        var (h, a) = CompetitionEngine.SimulateResult(c, fx);
        _pendingFixture = fx;   // ApplyFixtureResult clears it
        ApplyFixtureResult(fx, h, a, playedStage);
        // ApplyFixtureResult rebuilt HOME -> DASHBOARD (+ any draw ceremony);
        // show the simulated score on top, CONTINUE returns to that stack.
        Push(BuildViewResultFullTime(homeName, awayName, h, a));
        _dirty = true;
    }

    private MenuScreen BuildViewResultFullTime(string homeName, string awayName, int home, int away)
    {
        var s = new MenuScreen { Title = Loc.Tr("result.title", "FULL TIME") };
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => Loc.Tr("result.label", "RESULT") });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = true, Label = () => $"{homeName}  {home}" });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = true, Label = () => $"{awayName}  {away}" });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = true,
            Label = () => Loc.Tr("common.continue", "CONTINUE"), OnActivate = () => Pop() });
        return s;
    }

    // The one true competition-match launch: marks the fixture pending and
    // starts the sim with the human on the first slot (existing host path).
    // For a career competition, both sides are synthesized from the LIVE
    // CareerWorld squad (bought/sold/developed players) instead of the frozen
    // TEAM.* master roster; the player's club is passed as homeOverride because
    // the host always seats the human on the home slot (Main.MenuHost.cs).
    private void LaunchCompetitionFixture(Fixture fx)
    {
        var c = _comp;
        if (c is null) return;
        _pendingFixture = fx;
        int opp = fx.HomeTeam == c.PlayerTeam ? fx.AwayTeam : fx.HomeTeam;
        int playerMaster = c.Teams[c.PlayerTeam].MasterIndex;
        int oppMaster = c.Teams[opp].MasterIndex;
        _host.StartCompetitionMatch(playerMaster, oppMaster,
            SynthesizeCareerTeam(playerMaster), SynthesizeCareerTeam(oppMaster));
    }

    // Builds a match-ready TeamRecord from the current career squad of the club
    // occupying `masterIndex`, or null when there is no career world / no such
    // club (friendly, cup of master-roster teams, etc.) so callers fall back to
    // the read-only master record.
    private OpenSwos.Assets.TeamRecord? SynthesizeCareerTeam(int masterIndex)
    {
        var world = _comp?.Career?.World;
        if (world?.Clubs is null) return null;
        ushort gid = _host.TeamGlobalId(masterIndex);
        if (gid == 0 || !world.Clubs.TryGetValue(gid, out var club) || club is null) return null;
        return OpenSwos.Competition.Career.CareerMatchTeam.Build(_host.Team(masterIndex), club);
    }

    private void FastForwardAi()
    {
        var c = _comp;
        if (c is null) return;
        CompetitionEngine.FastForwardAiOnly(c);
        CompetitionStore.Save(c);
        ReplaceTop(BuildCompetitionDashboard());
    }

    private void AbandonCompetition()
    {
        try { CompetitionStore.Delete(); } catch { }
        _comp = null;
        _pendingFixture = null;
        ResetToHome();
        Push(BuildCompetitionsHub());
    }

    // Career season rollover: champion of the top table gets promoted (until
    // division 0), bottom club drops if a lower division exists in the nation.
    private void AdvanceSeason()
    {
        var c = _comp;
        if (c is null || c.Career is null) return;
        int you = c.Teams[c.PlayerTeam].MasterIndex;
        int nation = c.Career.Nation;
        var table = CompetitionEngine.Table(c, "LEAGUE");
        int pos = 0;
        for (int i = 0; i < table.Count; i++)
            if (table[i].Team == c.PlayerTeam) { pos = i + 1; break; }
        int newDivision = c.Career.Division;
        if (pos == 1 && c.Career.Division > 0) newDivision = c.Career.Division - 1;
        else if (pos > 0 && pos == table.Count
                 && _host.TeamsByNationDivision(nation, c.Career.Division + 1, 1).Count > 0)
            newDivision = c.Career.Division + 1;

        // The club moves divisions logically even if its data byte disagrees.
        var pool = new System.Collections.Generic.List<int> { you };
        foreach (int idx in _host.TeamsByNationDivision(nation, newDivision, 24))
        {
            if (pool.Count >= 16) break;
            if (idx == you || pool.Contains(idx)) continue;
            pool.Add(idx);
        }
        if (pool.Count < 8)
        {
            pool = new System.Collections.Generic.List<int> { you };
            foreach (int idx in _host.TeamsByNationDivision(nation, -1, 24))
            {
                if (pool.Count >= 16) break;
                if (idx == you || pool.Contains(idx)) continue;
                pool.Add(idx);
            }
        }
        while (pool.Count < 8 || (pool.Count & 1) == 1) AddRandomDistinct(pool);
        var cupPool = BuildCareerCupPool(you, nation);

        CompetitionEngine.AdvanceCareerSeason(c, MakeTeamRefs(pool), MakeTeamRefs(cupPool), newDivision);
        CompetitionStore.Save(c);
        _pendingFixture = null;
        ReplaceTop(BuildCompetitionDashboard());
    }

    // ---- management record (original: ManagementRecordMenu swos.asm:44076,
    //      "MANAGEMENT RECORD OF %a %b") --------------------------------------
    private MenuScreen BuildManagementRecord(bool careerOver)
    {
        var career = _comp?.Career;
        string mgr = (career?.ManagerName ?? "").Trim();
        string title = mgr.Length > 0
            ? (Loc.Tr("mrec.title_of_prefix", "MANAGEMENT RECORD OF") + " " + ((career!.ManagerTitle ?? "").Trim() + " " + mgr).Trim())
            : Loc.Tr("mrec.title", "MANAGEMENT RECORD");
        var s = new MenuScreen { Title = FitText(title, true, 294), BodyReserve = 150 };
        if (careerOver)
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = true, Label = () => Loc.Tr("mrec.career_over", "CAREER OVER") });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.DrawManagementRecordBody(s);
        return s;
    }

    // Body: (1) current-season aggregate over the player's played fixtures,
    // (2) trophy cabinet (most recent four), (3) season history, newest first.
    private void DrawManagementRecordBody(MenuScreen s)
    {
        var c = _comp;
        var career = c?.Career;
        if (c is null || career is null) return;
        int x = 22, y = s.BodyTop, bottom = _vh - 14;
        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        var gold = new Color(1f, 0.85f, 0.25f);

        // (1) this season, orientation-corrected onto the player's team
        int p = 0, w = 0, d = 0, l = 0, gf = 0, ga = 0;
        foreach (var f in c.Fixtures)
        {
            if (!f.Played) continue;
            bool home = f.HomeTeam == c.PlayerTeam;
            if (!home && f.AwayTeam != c.PlayerTeam) continue;
            int mine = home ? f.HomeGoals : f.AwayGoals;
            int theirs = home ? f.AwayGoals : f.HomeGoals;
            p++; gf += mine; ga += theirs;
            if (mine > theirs) w++; else if (mine == theirs) d++; else l++;
        }
        BodyText(s, $"SEASON {career.Season}  {TeamShort(c, c.PlayerTeam, 16)}  DIV {career.Division + 1}", false, x, y, head);
        y += 9;
        BodyText(s, $"P {p}   W {w}   D {d}   L {l}   GF {gf}   GA {ga}", false, x, y, normal);
        y += 12;

        // (2) trophies — the last four, oldest of those first
        BodyText(s, Loc.Tr("mrec.trophies", "TROPHIES"), false, x, y, head); y += 9;
        if (career.Trophies.Count == 0) { BodyText(s, Loc.Tr("mrec.none_yet", "NONE YET"), false, x, y, normal); y += 8; }
        else
        {
            for (int i = System.Math.Max(0, career.Trophies.Count - 4); i < career.Trophies.Count; i++)
            {
                if (y + 8 > bottom) return;
                BodyText(s, career.Trophies[i], false, x, y, gold); y += 8;
            }
        }
        y += 4;

        // (3) history — one line per season, most recent first, clipped
        if (y + 9 > bottom) return;
        BodyText(s, Loc.Tr("mrec.history", "HISTORY"), false, x, y, head); y += 9;
        if (career.History.Count == 0)
        {
            if (y + 8 <= bottom) BodyText(s, Loc.Tr("mrec.first_season", "FIRST SEASON IN PROGRESS"), false, x, y, normal);
            return;
        }
        for (int i = career.History.Count - 1; i >= 0; i--)
        {
            if (y + 8 > bottom) break;
            BodyText(s, career.History[i], false, x, y, normal); y += 8;
        }
    }

    // ---- retire (original: retire / manager status swos.asm:381,44509) -------
    private MenuScreen BuildRetireConfirm()
    {
        var s = new MenuScreen { Title = Loc.Tr("retire.title", "RETIRE") };
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = true, Label = () => Loc.Tr("retire.prompt", "RETIRE FROM MANAGEMENT?") });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Danger, Big = true,
            Label = () => Loc.Tr("retire.confirm", "RETIRE"), OnActivate = RetireNow });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlaySecondary, Big = true,
            Label = () => Loc.Tr("common.cancel", "CANCEL"), OnActivate = () => Pop() });
        s.Selected = 2;   // CANCEL is the safe default, like the quit prompt
        return s;
    }

    private void RetireNow()
    {
        var c = _comp;
        if (c?.Career is null || c.Career.Retired) { Pop(); return; }
        c.Career.Retired = true;
        c.Career.History.Add($"S{c.Career.Season}: RETIRED");
        CompetitionStore.Save(c);
        Pop();                                      // confirm -> (stale) dashboard
        ReplaceTop(BuildCompetitionDashboard());    // rebuilt with the retired layout
        Push(BuildManagementRecord(careerOver: true));
    }

    // ---- preset competitions (original: PresetCompetitionMenu swos.asm:23781) --
    private MenuScreen BuildPresetList()
    {
        var s = new MenuScreen { Title = Loc.Tr("preset.title", "PRESET COMPETITION") };
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
            Label = () => Loc.Tr("preset.blurb", "REAL COMPETITIONS WITH THEIR REAL FIELDS") });
        foreach (var p in PresetCompetitions.All)
        {
            var preset = p;   // capture per-iteration
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlaySecondary, Big = false,
                Label = () => preset.Name, OnActivate = () => OpenPresetSetup(preset) });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        return s;
    }

    // Resolve a preset's named field against the master list: ONE linear scan
    // builds a name->index map (first occurrence wins), then the preset names
    // are collected in order up to Size; any shortfall is random-filled.
    private System.Collections.Generic.List<int> ResolvePresetEntrants(PresetCompetition p)
    {
        var byName = new System.Collections.Generic.Dictionary<string, int>();
        int n = _host.TeamCount;
        for (int i = 0; i < n; i++)
        {
            string nm = (_host.TeamName(i) ?? "").Trim().ToUpperInvariant();
            if (nm.Length > 0 && !byName.ContainsKey(nm)) byName[nm] = i;
        }
        var resolved = new System.Collections.Generic.List<int>();
        foreach (string name in p.TeamNames)
        {
            if (resolved.Count >= p.Size) break;
            if (byName.TryGetValue(name.Trim().ToUpperInvariant(), out int idx) && !resolved.Contains(idx))
                resolved.Add(idx);
        }
        if (resolved.Count < p.Size)
        {
            // RandomTeams returns Size distinct indices (incl. the anchor), so
            // at least Size - resolved.Count of them are new — always enough.
            int first = resolved.Count > 0 ? resolved[0] : -1;
            foreach (int idx in _host.RandomTeams(p.Size, first))
            {
                if (resolved.Count >= p.Size) break;
                if (!resolved.Contains(idx)) resolved.Add(idx);
            }
        }
        return resolved;
    }

    private void OpenPresetSetup(PresetCompetition p)
    {
        _preset = p;
        _presetEntrants = ResolvePresetEntrants(p);
        _presetYou = 0;
        Push(BuildPresetSetup());
    }

    private void OpenPresetTeamPicker()
    {
        if (_presetEntrants.Count == 0) return;
        var rows = new System.Collections.Generic.List<string>(_presetEntrants.Count);
        foreach (int m in _presetEntrants) rows.Add(_host.TeamTag(m));
        PushListPicker(Loc.Tr("pick.select_your_team", "SELECT YOUR TEAM"), rows, _presetYou, idx => { _presetYou = idx; _dirty = true; });
    }

    private MenuScreen BuildPresetSetup()
    {
        var p = _preset;
        if (p is null) return BuildPresetList();
        var s = new MenuScreen { Title = p.Name };
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
            Label = () => p.Kind == CompetitionKind.Cup
                ? $"{p.Size} {Loc.Tr("preset.team_knockout_suffix", "TEAM KNOCKOUT")}"
                : $"{p.GroupCount} {Loc.Tr("preset.groups_then_knockout_suffix", "GROUPS OF 4 THEN KNOCKOUT")}" });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Info,
            Label = () => Loc.Tr("pick.your_team", "YOUR TEAM"),
            Value = () => _presetEntrants.Count == 0 ? "" : _host.TeamTag(_presetEntrants[_presetYou]),
            OnActivate = OpenPresetTeamPicker });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = true,
            Label = () => Loc.Tr("preset.create", "CREATE"), OnActivate = CreatePresetNow });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        return s;
    }

    private void CreatePresetNow()
    {
        var p = _preset;
        if (p is null || _presetEntrants.Count < p.Size) return;
        int presetIdx = System.Array.IndexOf(PresetCompetitions.All, p);
        int seed = SeedFrom(_presetEntrants, 700 + presetIdx * 8 + _presetYou);
        var refs = MakeTeamRefs(_presetEntrants);
        var comp = p.Kind == CompetitionKind.Cup
            ? CompetitionEngine.CreateCup(p.Name, refs, _presetYou, seed)
            : CompetitionEngine.CreateTournament(p.Name, refs, _presetYou, p.GroupCount, seed);
        OpenNewCompetition(comp);
    }

    // ---- save slots -------------------------------------------------------------
    private void OpenSaveAsEntry()
    {
        var c = _comp;
        if (c is null) return;
        PushNameEntry(Loc.Tr("save.title", "SAVE AS"), c.Name, 16,
            Loc.Tr("save.hint", "TYPE SLOT NAME   ENTER OK   ESC CANCEL"),
            name =>
            {
                var comp = _comp;
                if (comp is null) { Pop(); return; }
                string slot = CompetitionStore.SanitizeSlotName(name);
                CompetitionStore.SaveAs(comp, slot);
                _saveNotice = Loc.Tr("save.saved_as_prefix", "SAVED AS") + " " + slot;
                Pop();   // back onto the live dashboard — status line shows the notice
            });
    }

    private MenuScreen BuildLoadGame()
    {
        var s = new MenuScreen { Title = Loc.Tr("load.title", "LOAD GAME") };
        var slots = CompetitionStore.ListSlots();
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Danger,
            Label = () => Loc.Tr("load.delete_mode", "DELETE MODE"), Value = () => _slotDeleteMode ? Loc.Tr("common.on", "ON") : Loc.Tr("common.off", "OFF"),
            OnStep = _ => { _slotDeleteMode = !_slotDeleteMode; _dirty = true; } });
        if (slots.Count == 0)
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => Loc.Tr("load.no_saved_games", "NO SAVED GAMES") });
        foreach (var (slot, label) in slots)
        {
            string slotName = slot;
            string text = slotName + " - " + label;
            if (text.Length > 44) text = text.Substring(0, 44);   // keep inside the button
            string rowText = text;
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => rowText, OnActivate = () => FireSaveSlot(slotName) });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        return s;
    }

    private void FireSaveSlot(string slot)
    {
        if (_slotDeleteMode)
        {
            CompetitionStore.DeleteSlot(slot);
            // Deleting the AUTOSAVE kills the active competition slot too.
            if (slot == "AUTOSAVE") { _comp = null; _pendingFixture = null; }
            ReplaceTop(BuildLoadGame());   // rebuild the list; delete mode stays ON
            return;
        }
        var loaded = CompetitionStore.LoadSlot(slot);
        if (loaded is null) { ReplaceTop(BuildLoadGame()); return; }
        EnsureCareerWorld(loaded);       // legacy saves carry no world — rebuild it
        _comp = loaded;
        _pendingFixture = null;
        CompetitionStore.Save(loaded);   // the loaded game becomes the active AUTOSAVE
        ReplaceTop(BuildCompetitionDashboard());
    }

    // ---- table / fixtures screens ---------------------------------------------
    private MenuScreen BuildTableScreen()
    {
        var s = new MenuScreen { Title = Loc.Tr("table.title", "TABLE") };
        var c = _comp;
        // VIEW WORLD (original: contestMenu VIEW COMPETITIONS/VIEW WORLD,
        // swos.asm:227077/227097): tournaments get a group stepper so every
        // group's table is viewable, opening on the player's own group.
        if (c is not null && c.Kind == CompetitionKind.Tournament && c.GroupCount > 1)
        {
            int g = (c.PlayerTeam >= 0 && c.PlayerTeam < c.GroupOf.Count) ? c.GroupOf[c.PlayerTeam] : 0;
            _tableGroup = g < 0 ? 0 : g;
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("table.group", "GROUP"), Value = () => ((char)('A' + _tableGroup)).ToString(),
                OnStep = d => { _tableGroup = StepIdx(_tableGroup, d, c.GroupCount); RebuildCurrent(); } });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.DrawFullTableBody(s);
        return s;
    }

    private MenuScreen BuildFixturesScreen()
    {
        var s = new MenuScreen { Title = Loc.Tr("fixtures.title", "FIXTURES") };
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.DrawFixturesBody(s);
        return s;
    }

    // ---- body painters ----------------------------------------------------------
    // All painters anchor at s.BodyTop (set by layout) and stay above the
    // footer hint at _vh-14 — never hardcode panel Y positions.

    private static string TeamShort(CompetitionState c, int team, int max)
    {
        string n = (team >= 0 && team < c.Teams.Count) ? c.Teams[team].Name : "";
        n = n.Trim();
        return n.Length > max ? n.Substring(0, max) : n;
    }

    private static string FixtureLine(CompetitionState c, Fixture f)
    {
        string h = TeamShort(c, f.HomeTeam, 13), a = TeamShort(c, f.AwayTeam, 13);
        if (!f.Played) return $"{h} - {a}";
        string line = $"{h} {f.HomeGoals}-{f.AwayGoals} {a}";
        if (f.OnPenalties) line += " P";   // decided on penalties
        return line;
    }

    private void DrawCompetitionBody(MenuScreen s)
    {
        var c = _comp;
        if (c is null) return;
        int x = 22, y = s.BodyTop;
        int bottom = _vh - 14;
        var head = new Color(0.7f, 0.85f, 1f);

        if (c.Career is not null)
        {
            string extra = $"DIV {c.Career.Division + 1}  SEASON {c.Career.Season}";
            // Manager identity (title + name) when a name has been entered
            // (name entry itself arrives in a later wave — omit while empty).
            string mgrName = (c.Career.ManagerName ?? "").Trim();
            if (mgrName.Length > 0)
                extra += "  " + ((c.Career.ManagerTitle ?? "").Trim() + " " + mgrName).Trim();
            if (c.Career.Trophies.Count > 0)
                extra += "  " + c.Career.Trophies[c.Career.Trophies.Count - 1];
            BodyText(s, FitText(extra, false, _vw - x - 4), false, x, y, head);
            y += 9;
        }

        switch (c.Kind)
        {
            case CompetitionKind.League:
            case CompetitionKind.Career:
                DrawTableRows(s, c, CompetitionEngine.Table(c, "LEAGUE"), x, y, (bottom - y - 9) / 8);
                break;
            case CompetitionKind.Cup:
                DrawRoundFixtures(s, c, x, y, (bottom - y - 9) / 8);
                break;
            case CompetitionKind.Tournament:
                if (InGroupStage(c))
                    DrawTableRows(s, c, CompetitionEngine.Table(c, PlayerGroupStage(c)), x, y, (bottom - y - 9) / 8);
                else
                    DrawRoundFixtures(s, c, x, y, (bottom - y - 9) / 8);
                break;
        }
    }

    private static bool InGroupStage(CompetitionState c)
    {
        foreach (var f in c.Fixtures)
            if (!f.Played) return f.Stage.StartsWith("GROUP");
        return false;   // everything played -> show the (knockout) final round
    }

    private static string PlayerGroupStage(CompetitionState c)
    {
        int g = (c.PlayerTeam >= 0 && c.PlayerTeam < c.GroupOf.Count) ? c.GroupOf[c.PlayerTeam] : 0;
        if (g < 0) g = 0;
        return "GROUP " + (char)('A' + g);
    }

    // Standings block at fixed column anchors (the SWOS font is proportional,
    // so string padding would not line columns up).
    private void DrawTableRows(MenuScreen s, CompetitionState c,
        System.Collections.Generic.List<TableRow> rows, int x, int y, int maxRows)
    {
        if (maxRows <= 0) return;
        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        var gold = new Color(1f, 0.85f, 0.25f);
        int cPos = x + 16, cName = x + 22;
        int cP = x + 158, cW = x + 180, cD = x + 202, cL = x + 224, cGF = x + 252, cGA = x + 280, cPts = x + 312;

        BodyText(s, Loc.Tr("col.pos", "POS"), false, x, y, head);
        BodyText(s, Loc.Tr("col.team", "TEAM"), false, cName, y, head);
        BodyText(s, Loc.Tr("col.p", "P"), false, cP, y, head, rightAlign: true);
        BodyText(s, Loc.Tr("col.w", "W"), false, cW, y, head, rightAlign: true);
        BodyText(s, Loc.Tr("col.d", "D"), false, cD, y, head, rightAlign: true);
        BodyText(s, Loc.Tr("col.l", "L"), false, cL, y, head, rightAlign: true);
        BodyText(s, Loc.Tr("col.gf", "GF"), false, cGF, y, head, rightAlign: true);
        BodyText(s, Loc.Tr("col.ga", "GA"), false, cGA, y, head, rightAlign: true);
        BodyText(s, Loc.Tr("col.pts", "PTS"), false, cPts, y, head, rightAlign: true);
        y += 9;

        int n = System.Math.Min(rows.Count, maxRows);
        for (int i = 0; i < n; i++)
        {
            var r = rows[i];
            Color rc = r.Team == c.PlayerTeam ? gold : normal;
            BodyText(s, (i + 1).ToString(), false, cPos, y, rc, rightAlign: true);
            BodyText(s, TeamShort(c, r.Team, 16), false, cName, y, rc);
            BodyText(s, r.Played.ToString(), false, cP, y, rc, rightAlign: true);
            BodyText(s, r.Won.ToString(), false, cW, y, rc, rightAlign: true);
            BodyText(s, r.Drawn.ToString(), false, cD, y, rc, rightAlign: true);
            BodyText(s, r.Lost.ToString(), false, cL, y, rc, rightAlign: true);
            BodyText(s, r.GoalsFor.ToString(), false, cGF, y, rc, rightAlign: true);
            BodyText(s, r.GoalsAgainst.ToString(), false, cGA, y, rc, rightAlign: true);
            BodyText(s, r.Points.ToString(), false, cPts, y, rc, rightAlign: true);
            y += 8;
        }
    }

    // Current round's fixture list (or the last round once everything played).
    private void DrawRoundFixtures(MenuScreen s, CompetitionState c, int x, int y, int maxRows)
    {
        if (c.Fixtures.Count == 0 || maxRows <= 0) return;
        int round = -1;
        foreach (var f in c.Fixtures)
            if (!f.Played) { round = f.Round; break; }
        if (round < 0) round = c.Fixtures[c.Fixtures.Count - 1].Round;

        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        var gold = new Color(1f, 0.85f, 0.25f);
        int shown = 0;
        foreach (var f in c.Fixtures)
        {
            if (f.Round != round) continue;
            if (shown == 0 && f.Stage.Length > 0) { BodyText(s, f.Stage, false, x, y, head); y += 9; }
            bool mine = f.HomeTeam == c.PlayerTeam || f.AwayTeam == c.PlayerTeam;
            BodyText(s, FixtureLine(c, f), false, x, y, mine ? gold : normal);
            y += 8;
            shown++;
            if (shown >= maxRows) break;
        }
    }

    private void DrawFullTableBody(MenuScreen s)
    {
        var c = _comp;
        if (c is null) return;
        int y = s.BodyTop;
        // Tournament: the TABLE screen's GROUP stepper picks which group's
        // standings render; league/career keep the single LEAGUE table.
        string stage = "LEAGUE";
        if (c.Kind == CompetitionKind.Tournament)
        {
            int g = System.Math.Clamp(_tableGroup, 0, System.Math.Max(0, c.GroupCount - 1));
            stage = "GROUP " + (char)('A' + g);
        }
        if (c.Kind == CompetitionKind.Tournament)
        {
            BodyText(s, stage, false, 22, y, new Color(0.7f, 0.85f, 1f));
            y += 9;
        }
        DrawTableRows(s, c, CompetitionEngine.Table(c, stage), 22, y, (_vh - 14 - y - 9) / 8);
    }

    // The player's team fixtures: recent results plus what is coming up, with
    // the window anchored a few rows before the first unplayed fixture.
    private void DrawFixturesBody(MenuScreen s)
    {
        var c = _comp;
        if (c is null) return;
        int y = s.BodyTop;
        int maxRows = (_vh - 14 - y) / 8;
        if (maxRows <= 0) return;

        var mine = new System.Collections.Generic.List<Fixture>();
        foreach (var f in c.Fixtures)
            if (f.HomeTeam == c.PlayerTeam || f.AwayTeam == c.PlayerTeam) mine.Add(f);
        mine.Sort((a, b) => a.Round.CompareTo(b.Round));

        int first = mine.FindIndex(f => !f.Played);
        int start = 0;
        if (mine.Count > maxRows)
        {
            start = first < 0 ? mine.Count - maxRows
                              : System.Math.Clamp(first - 4, 0, mine.Count - maxRows);
        }

        var played = new Color(0.62f, 0.66f, 0.78f);
        var upcoming = new Color(0.92f, 0.94f, 1f);
        for (int i = start; i < mine.Count && i - start < maxRows; i++)
        {
            var f = mine[i];
            BodyText(s, f.Stage + "  " + FixtureLine(c, f), false, 22, y, f.Played ? played : upcoming);
            y += 8;
        }
    }

    // Team & squad configuration screen — a hand-crafted squad table with kit
    // swatches and tactics, in the SWOS spirit (like the original team screen).
    private int _cfgTeamIsAway;   // 0 = home, 1 = away
    private MenuScreen BuildTeamConfig()
    {
        // Reserve the full squad table (header + 16 rows) — pushes the control
        // strip onto compact metrics so all 16 players fit above the footer.
        var s = new MenuScreen { Title = Loc.Tr("teamcfg.title", "TEAM SETUP"), BodyReserve = 160 };
        // A tiny control strip at the top: toggle which team, then BACK.
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Info,
            Label = () => Loc.Tr("teamcfg.team", "TEAM"), Value = () => (_cfgTeamIsAway == 0 ? Loc.Tr("teamcfg.team_home_prefix", "HOME") + "  " : Loc.Tr("teamcfg.team_away_prefix", "AWAY") + "  ") + _host.TeamName(_cfgTeamIsAway == 0 ? _host.HomeIndex : _host.AwayIndex),
            OnActivate = OpenTeamConfigPicker });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
            Label = () => Loc.Tr("teamcfg.switch_home_away", "SWITCH HOME / AWAY"), OnActivate = () => { _cfgTeamIsAway ^= 1; RebuildCurrent(); } });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.DrawSquadBody(s, _cfgTeamIsAway == 0 ? _host.HomeIndex : _host.AwayIndex);
        return s;
    }

    // TEAM SETUP team chooser: CONTINENT -> COUNTRY -> CLUB drill-down (same as
    // FRIENDLY), replacing the old flat ~1730-team list.
    private void OpenTeamConfigPicker()
    {
        int cur = _cfgTeamIsAway == 0 ? _host.HomeIndex : _host.AwayIndex;
        OpenTeamDrillDown(_cfgTeamIsAway == 0 ? Loc.Tr("pick.home_team", "HOME TEAM") : Loc.Tr("pick.away_team", "AWAY TEAM"), cur, pick =>
        {
            if (_cfgTeamIsAway == 0) _host.SetHomeTeam(pick); else _host.SetAwayTeam(pick);
            RebuildCurrent();
        });
    }

    private void RebuildCurrent() { LayoutAndBuild(Current); _dirty = true; }

    // Draws the squad table + kit + tactics for a team below the control strip.
    private void DrawSquadBody(MenuScreen s, int teamIdx)
    {
        TeamRecord t;
        try { t = _host.Team(teamIdx); } catch { return; }

        int panelX = 20, panelY = s.BodyTop, panelW = _vw - 40, panelH = _vh - panelY - 14;
        if (panelH < 40) return;   // nothing sensible fits — bail rather than overlap
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);

        int col1 = panelX + 8;         // number
        int col2 = panelX + 30;        // name
        int col3 = panelX + 150;       // position
        int col4 = panelX + 196;       // skills line

        // panel header: coach + division on the left, kit swatches on the right.
        var head = new Color(0.7f, 0.85f, 1f);
        string coach = (t.Coach ?? "").Trim().TrimEnd('\0');
        string info = string.IsNullOrEmpty(coach) ? $"DIV {t.Division + 1}" : $"COACH {coach}   DIV {t.Division + 1}";
        BodyText(s, info, false, col1, panelY + 4, head);
        int sw = 11, gap = 3, kx = panelX + panelW - (sw + gap) * 3 - 4, ky = panelY + 3;
        DrawKitStrip(s, kx, ky, sw, t.HomeKit);
        DrawKitStrip(s, kx + sw + gap, ky, sw, t.AwayKit);
        DrawKitStrip(s, kx + (sw + gap) * 2, ky, sw, t.GoalkeeperKit);

        // column header
        int y = panelY + 15;
        BodyText(s, Loc.Tr("col.number_hash", "#"), false, col1, y, head);
        BodyText(s, Loc.Tr("col.name", "NAME"), false, col2, y, head);
        BodyText(s, Loc.Tr("col.pos", "POS"), false, col3, y, head);
        BodyText(s, Loc.Tr("col.skill_row", "PA TA SH HE CO SP FI"), false, col4, y, head);
        y += 10;

        var rowCol = new Color(0.92f, 0.94f, 1f);
        // rows start 25px into the panel (info + column header); 8px per row,
        // clipped to what actually fits above the panel's bottom edge.
        int maxRows = (panelH - 27) / 8;
        int rows = System.Math.Min(System.Math.Min(t.Players.Count, 16), maxRows);
        for (int i = 0; i < rows; i++)
        {
            var p = t.Players[i];
            bool starter = i < 11;   // 0..10 line up, 11..15 the bench (dimmer)
            Color rc = starter ? rowCol : new Color(0.62f, 0.66f, 0.78f);
            BodyText(s, p.ShirtNumber.ToString(), false, col1 + 8, y, rc, rightAlign: true);
            string nm = (p.Name ?? "").Trim();
            if (nm.Length > 15) nm = nm.Substring(0, 15);
            BodyText(s, nm, false, col2, y, rc);
            BodyText(s, p.Position ?? "", false, col3, y, rc);
            string skills = $"{p.Passing,2} {p.Tackling,2} {p.Shooting,2} {p.Heading,2} {p.Control,2} {p.Speed,2} {p.Finishing,2}";
            BodyText(s, skills, false, col4, y, rc);
            y += 8;
        }
    }

    private void DrawKitStrip(MenuScreen s, int x, int y, int sw, byte[] kit)
    {
        // kit layout {type, shirt1, shirt2, shorts, socks} → shirt / shorts / socks bands
        byte shirt = kit.Length > 2 ? kit[2] : (byte)0;
        byte shorts = kit.Length > 3 ? kit[3] : (byte)0;
        byte socks = kit.Length > 4 ? kit[4] : (byte)0;
        BodySwatch(s, x, y, sw, 8, _host.KitSwatch(shirt));
        BodySwatch(s, x, y + 8, sw, 4, _host.KitSwatch(shorts));
        BodySwatch(s, x, y + 12, sw, 4, _host.KitSwatch(socks));
    }
}
