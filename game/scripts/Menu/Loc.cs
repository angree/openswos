namespace OpenSwos.Menu;

using System.Collections.Generic;

// Localization (i18n) for the whole front-end.
//
// Every user-facing string goes through Loc.Tr(key, english): the English text
// is written INLINE at the call site (so the UI always works and the source is
// self-documenting), and `key` selects the translated form for the current
// language. Translations live in per-language tables built from the master
// string database (see docs/i18n/strings.tsv + StringCatalog) and loaded here.
// A missing key OR a missing language falls back to the English text — so the
// game is never left with a blank or a raw key.
//
// The SWOS charset is ASCII/Latin-basic with NO accented glyphs, so translations
// are transliterated to plain ASCII (é->E, ł->L, ö->O ...). The language order
// below is the user's priority: EN, DE, FR, IT, PL first, then 15 more that
// survive the accent-stripping without becoming unreadable.
public static class Loc
{
    // Display name (shown in the LANGUAGE selector) + short code (table key).
    public readonly record struct Lang(string Name, string Code);

    public static readonly Lang[] Languages =
    {
        new("ENGLISH", "en"),     new("DEUTSCH", "de"),     new("FRANCAIS", "fr"),
        new("ITALIANO", "it"),    new("POLSKI", "pl"),      new("ESPANOL", "es"),
        new("PORTUGUES", "pt"),   new("NEDERLANDS", "nl"),  new("SVENSKA", "sv"),
        new("NORSK", "no"),       new("DANSK", "da"),       new("SUOMI", "fi"),
        new("INDONESIA", "id"),   new("MELAYU", "ms"),      new("KISWAHILI", "sw"),
        new("TURKCE", "tr"),      new("ROMANA", "ro"),      new("HRVATSKI", "hr"),
        new("SLOVENSKY", "sk"),   new("CESTINA", "cs"),
    };

    private static int s_current;   // index into Languages; 0 = English (identity)

    public static int Current
    {
        get => s_current;
        set => s_current = ((value % Languages.Length) + Languages.Length) % Languages.Length;
    }

    public static string CurrentName => Languages[s_current].Name;
    public static string CurrentCode => Languages[s_current].Code;

    // code -> (english-key -> translated text). English is never stored (identity).
    private static readonly Dictionary<string, Dictionary<string, string>> s_tables = new();

    /// <summary>Register a translated table for one language code (called at load time).</summary>
    public static void Register(string code, Dictionary<string, string> table)
    {
        if (!string.IsNullOrEmpty(code) && table is not null) s_tables[code] = table;
    }

    /// <summary>
    /// Load every per-language table from res://data/i18n/&lt;code&gt;.tsv (2-col:
    /// key TAB translated). English is skipped (identity). Missing files are fine
    /// — that language just falls back to English. Call once at startup.
    /// </summary>
    public static void LoadAll()
    {
        foreach (var lang in Languages)
        {
            if (lang.Code == "en") continue;
            string path = $"res://data/i18n/{lang.Code}.tsv";
            if (!Godot.FileAccess.FileExists(path)) continue;
            using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            if (f is null) continue;
            var table = new Dictionary<string, string>();
            while (!f.EofReached())
            {
                string line = f.GetLine();
                if (string.IsNullOrEmpty(line) || line[0] == '#') continue;
                int t = line.IndexOf('\t');
                if (t <= 0) continue;
                string key = line.Substring(0, t);
                if (key == "key") continue;              // header row
                string val = line.Substring(t + 1).Trim();
                if (!string.IsNullOrEmpty(val)) table[key] = val;
            }
            if (table.Count > 0) Register(lang.Code, table);
        }
    }

    public static bool HasTable(string code) => s_tables.ContainsKey(code);

    /// <summary>
    /// Translate: returns the current language's text for `key`, or the inline
    /// English fallback when the language is English / the table or key is absent.
    /// </summary>
    public static string Tr(string key, string english)
    {
        if (s_current == 0) return english;                       // English is identity
        if (s_tables.TryGetValue(CurrentCode, out var t)
            && t.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
            return v;
        return english;                                           // fallback
    }
}
