using Godot;

namespace OpenSwos.Menu;

// SWOS-style menu look, re-implemented for OpenSWOS.
//
// The original SWOS menu buttons are NOT flat rectangles: each has a radial
// colour gradient (darkest at the top-left corner, brightest toward the
// bottom-right) framed by a 1 px outer border and an optional 1 px inner
// border, and the selected entry is wrapped in a gently flashing highlight
// frame. This module reproduces that look with our OWN palette — the colours
// are deliberately "similar but different" from the original so OpenSWOS has
// its own identity while keeping the unmistakable SWOS feel.
//
// The radial-gradient algorithm is paraphrased from swos-port's
// menuItemRenderer.cpp:18-40 ("Menu item backgrounds, radial color gradients"):
//   d      = width^2 + height^2                (diagonal length squared)
//   l(x,y) = x^2 + y^2                         (pixel distance squared)
//   s      = l / d * 32                        (gradient position, 0..32)
//   colour = lerp(stops[floor s], stops[ceil s], frac(s))
// The flash-cursor colour cadence mirrors drawMenu.cpp:201 (kColorTableShine).
// Algorithm paraphrased, colours are ours (see project licence rule: paraphrase
// algorithms, our own constants).
public static class MenuTheme
{
    // Set true at boot for <=720x576 handhelds (R36S). Hides x1 fine-print flag
    // codes; other layout unchanged.
    public static bool SmallScreen;

    // ---- named button styles -------------------------------------------------
    // Each screen entry picks a style; the style decides its gradient family,
    // frame colours and text colour. Kept small and semantic so screens read
    // cleanly ("PlayPrimary", "Tool", "Header", ...).
    public enum Style
    {
        PlayPrimary,   // headline "play" buttons — warm gold
        PlaySecondary, // secondary competition buttons — emerald green
        Tool,          // left-column tools / navigation — deep royal blue
        Header,        // screen title bars — vivid royal blue, white text, gold frame
        Info,          // value fields / info / highlight rows — teal
        Accent,        // special (multiplayer, player actions) — violet
        Danger,        // destructive (retire / reject / remove) — red
        Value,         // option value-changer body / field values — clear blue
        Field,         // option label boxes — muted blue (pairs with Value)
        Plain,         // BACK / EXIT — warm amber-rust exit block
    }

    // A style = two gradient endpoints (top-left dark → bottom-right light),
    // an optional mid control point, an outer frame colour, an optional inner
    // frame colour, and a text colour.
    private readonly struct StyleDef
    {
        public readonly Color GA, GMid, GB, Outer, Text;
        public readonly bool HasMid;
        public readonly bool HasInner;
        public readonly Color Inner;
        public StyleDef(Color ga, Color gb, Color outer, Color text,
                        bool hasMid = false, Color gmid = default,
                        bool hasInner = false, Color inner = default)
        { GA = ga; GB = gb; Outer = outer; Text = text; HasMid = hasMid; GMid = gmid; HasInner = hasInner; Inner = inner; }
    }

    private static Color C(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f);

    // OpenSWOS palette — SWOS-derived but shifted (cooler, a touch richer).
    //
    // Reference colour language (SWOS 96/97 menus, from MobyGames/community
    // screenshots): menus are built from SATURATED solid colour blocks, never
    // grey-on-grey. Team-control states are colour-coded (RED = CPU, PURPLE =
    // human plays, BLUE = human manages), title/function bars are solid vivid
    // colour, and the selected entry flashes yellow/gold. We keep that "coloured
    // functional block" grammar but with our own shifted hues (paraphrase rule):
    //   play = gold, competitions = green, navigation/tools = blue, info = teal,
    //   special = violet, destructive = red, fields = blue, exit/BACK = amber.
    private static readonly System.Collections.Generic.Dictionary<Style, StyleDef> kStyles = new()
    {
        // gold play button: deep brown → gold, orange frame
        [Style.PlayPrimary]   = new StyleDef(C(72, 30, 0),  C(244, 172, 40), C(252, 140, 0),  C(252, 252, 224), hasInner: true, inner: C(120, 60, 0)),
        // emerald: dark green → lime, green frame
        [Style.PlaySecondary] = new StyleDef(C(6, 40, 8),   C(120, 216, 48), C(48, 200, 40),  C(240, 255, 224), hasInner: true, inner: C(10, 70, 12)),
        // navy tool: near-black blue → royal, soft-blue frame
        [Style.Tool]          = new StyleDef(C(6, 10, 32),  C(48, 72, 200),  C(120, 140, 240), C(224, 232, 255), hasInner: true, inner: C(12, 20, 64)),
        // vivid header: royal blue → bright azure, WHITE text, gold frame (pops)
        [Style.Header]        = new StyleDef(C(8, 24, 88),  C(96, 176, 255), C(255, 210, 60), C(255, 255, 255), hasMid: true, gmid: C(40, 104, 224), hasInner: true, inner: C(4, 14, 56)),
        // teal info: deep teal → aqua, aqua frame
        [Style.Info]          = new StyleDef(C(4, 28, 40),  C(72, 200, 208), C(96, 220, 224), C(232, 252, 252), hasInner: true, inner: C(8, 44, 60)),
        // violet accent: plum → lilac, lilac frame
        [Style.Accent]        = new StyleDef(C(40, 6, 56),  C(168, 120, 240), C(180, 150, 252), C(244, 236, 255), hasInner: true, inner: C(60, 14, 84)),
        // red danger: dark red → bright red, yellow frame
        [Style.Danger]        = new StyleDef(C(64, 0, 0),   C(232, 40, 40),  C(252, 200, 0),  C(255, 240, 224), hasInner: true, inner: C(96, 0, 0)),
        // clear blue value body: deep blue → bright azure, blue frame, white text
        [Style.Value]         = new StyleDef(C(8, 22, 70),  C(110, 168, 245), C(128, 158, 216), C(240, 246, 255), hasMid: true, gmid: C(46, 100, 204), hasInner: true, inner: C(6, 16, 52)),
        // muted-blue field label box: darker blue than Value so label+value read as one field
        [Style.Field]         = new StyleDef(C(6, 16, 46),  C(70, 112, 190),  C(96, 128, 192), C(216, 228, 255), hasMid: true, gmid: C(30, 62, 132), hasInner: true, inner: C(4, 10, 34)),
        // amber-rust exit block (BACK/EXIT): dark rust → warm orange, gold frame
        [Style.Plain]         = new StyleDef(C(56, 18, 4),  C(236, 132, 40),  C(255, 196, 88), C(255, 244, 224), hasMid: true, gmid: C(168, 68, 20), hasInner: true, inner: C(80, 26, 6)),
    };

    public static Color TextColor(Style s) => kStyles[s].Text;

    // Screen backdrop — deep midnight, our own (SWOS uses a starfield; we use a
    // clean gradient wash that the buttons pop against).
    public static readonly Color BackdropTop    = C(4, 6, 18);
    public static readonly Color BackdropBottom = C(10, 14, 40);

    // ---- background texture cache -------------------------------------------
    private static readonly System.Collections.Generic.Dictionary<(int, int, Style), ImageTexture> s_bgCache = new();

    // Bakes a button background (radial gradient + outer frame + optional inner
    // frame) for the given size and style. Cached by (w,h,style).
    public static ImageTexture Button(int w, int h, Style style)
    {
        if (w < 1) w = 1; if (h < 1) h = 1;
        var key = (w, h, style);
        if (s_bgCache.TryGetValue(key, out var cached)) return cached;

        var def = kStyles[style];
        var stops = BuildStops(def);

        double d = (double)(w - 1) * (w - 1) + (double)(h - 1) * (h - 1);
        if (d < 1) d = 1;

        var bytes = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double l = (double)x * x + (double)y * y;
                double s = l / d * 32.0;
                if (s < 0) s = 0; if (s > 32) s = 32;
                int i = (int)s;
                double frac = s - i;
                int j = i < 32 ? i + 1 : 32;
                Color c = stops[i].Lerp(stops[j], (float)frac);
                int o = (y * w + x) * 4;
                bytes[o + 0] = (byte)(c.R * 255f + 0.5f);
                bytes[o + 1] = (byte)(c.G * 255f + 0.5f);
                bytes[o + 2] = (byte)(c.B * 255f + 0.5f);
                bytes[o + 3] = 255;
            }
        }

        // inner frame first (inset by 1), then outer frame on the very border
        if (def.HasInner && w > 3 && h > 3)
            StrokeRect(bytes, w, h, 1, 1, w - 2, h - 2, def.Inner);
        StrokeRect(bytes, w, h, 0, 0, w, h, def.Outer);

        var img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes);
        var tex = ImageTexture.CreateFromImage(img);
        if (s_bgCache.Count > 400) s_bgCache.Clear();
        s_bgCache[key] = tex;
        return tex;
    }

    // 33 gradient stops from the style's endpoints (linear, with an optional mid
    // control point so multi-hue families like green→lime read correctly).
    private static Color[] BuildStops(StyleDef def)
    {
        var stops = new Color[33];
        if (def.HasMid)
        {
            for (int i = 0; i <= 16; i++) stops[i] = def.GA.Lerp(def.GMid, i / 16f);
            for (int i = 17; i <= 32; i++) stops[i] = def.GMid.Lerp(def.GB, (i - 16) / 16f);
        }
        else
        {
            for (int i = 0; i <= 32; i++) stops[i] = def.GA.Lerp(def.GB, i / 32f);
        }
        return stops;
    }

    private static void StrokeRect(byte[] bytes, int imgW, int imgH, int x, int y, int w, int h, Color col)
    {
        byte r = (byte)(col.R * 255f + 0.5f), g = (byte)(col.G * 255f + 0.5f), b = (byte)(col.B * 255f + 0.5f);
        void Px(int px, int py)
        {
            if (px < 0 || py < 0 || px >= imgW || py >= imgH) return;
            int o = (py * imgW + px) * 4;
            bytes[o] = r; bytes[o + 1] = g; bytes[o + 2] = b; bytes[o + 3] = 255;
        }
        for (int px = x; px < x + w; px++) { Px(px, y); Px(px, y + h - 1); }
        for (int py = y; py < y + h; py++) { Px(x, py); Px(x + w - 1, py); }
    }

    // ---- selection cursor ----------------------------------------------------
    // A 1 px hollow white outline texture (modulated per-frame with the flash
    // colour). Paraphrase of drawMenu.cpp kColorTableShine cadence.
    private static readonly System.Collections.Generic.Dictionary<(int, int), ImageTexture> s_cursorCache = new();

    public static ImageTexture CursorOutline(int w, int h)
    {
        if (w < 1) w = 1; if (h < 1) h = 1;
        var key = (w, h);
        if (s_cursorCache.TryGetValue(key, out var cached)) return cached;
        var bytes = new byte[w * h * 4];
        // 2 px bright frame (outer + inset) so the selection reads clearly.
        StrokeRect(bytes, w, h, 0, 0, w, h, Colors.White);
        if (w > 3 && h > 3) StrokeRect(bytes, w, h, 1, 1, w - 2, h - 2, Colors.White);
        var img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes);
        var tex = ImageTexture.CreateFromImage(img);
        s_cursorCache[key] = tex;
        return tex;
    }

    // Flash cadence: a bright pulse between white and gold (our take on the
    // original's shine table, drawMenu.cpp:201) — high-contrast so the cursor is
    // unmistakable on any button colour.
    private static readonly Color[] kShine =
    {
        C(255, 255, 255), C(255, 255, 200), C(255, 236, 96), C(255, 210, 0),
        C(255, 236, 96), C(255, 255, 200), C(255, 255, 255), C(255, 255, 255),
    };
    public static Color CursorColor(int frame) => kShine[frame & 7];
}
