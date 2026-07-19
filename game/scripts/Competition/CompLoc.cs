using System;
using OpenSwos.Menu;

namespace OpenSwos.Competition;

// ============================================================================
// CompLoc — DISPLAY-TIME translators for competition/career strings whose
// English form is PERSISTED into save files and/or matched by logic
// (Fixture.Stage, Coach.Specialty, ordinals inside history lines, ...).
//
// The golden rule (see the localisation gotchas): the value STORED in a save
// and the value COMPARED in logic must stay ENGLISH. Translation happens only
// where the menu renders the value. These helpers are that render-time bridge:
// they take the stored English token and return the current language's text,
// falling back to English (identity) when no translation exists.
//
// Nothing here is ever assigned back into a persisted field.
// ============================================================================
public static class CompLoc
{
    // Fixture.Stage is stored English: "LEAGUE", "GROUP A".."GROUP H",
    // "FINAL"/"SEMI FINAL"/"QUARTER FINAL"/"ROUND OF 16"/"ROUND OF 32"/
    // "ROUND OF n", and the same knockout labels prefixed with "CUP ".
    // Translate only for display; the raw string is what logic compares.
    public static string TrStage(string? stage)
    {
        if (string.IsNullOrEmpty(stage)) return stage ?? "";
        if (stage == "LEAGUE") return Loc.Tr("comp.stage_league", "LEAGUE");
        if (stage.Length == 7 && stage.StartsWith("GROUP ", StringComparison.Ordinal))
            return Loc.Tr("comp.stage_group", "GROUP") + " " + stage.Substring(6);
        if (stage.StartsWith("CUP ", StringComparison.Ordinal))
            return Loc.Tr("comp.cup", "CUP") + " " + TrKnockout(stage.Substring(4));
        return TrKnockout(stage);
    }

    private static string TrKnockout(string k) => k switch
    {
        "FINAL" => Loc.Tr("comp.stage_final", "FINAL"),
        "SEMI FINAL" => Loc.Tr("comp.stage_semi", "SEMI FINAL"),
        "QUARTER FINAL" => Loc.Tr("comp.stage_quarter", "QUARTER FINAL"),
        "ROUND OF 16" => Loc.Tr("comp.stage_r16", "ROUND OF 16"),
        "ROUND OF 32" => Loc.Tr("comp.stage_r32", "ROUND OF 32"),
        _ => k.StartsWith("ROUND OF ", StringComparison.Ordinal)
            ? Loc.Tr("comp.stage_round_of", "ROUND OF") + " " + k.Substring(9)
            : k,
    };

    // Coach.Specialty is stored English ("YOUTH"/"ATTACK"/"DEFENCE"/"GENERAL")
    // and switched on in StaffModel — translate only for the staff tables.
    public static string TrSpecialty(string? specialty)
    {
        string key = specialty?.Trim().ToUpperInvariant() ?? "";
        return key switch
        {
            "YOUTH" => Loc.Tr("staff.spec_youth", "YOUTH"),
            "ATTACK" => Loc.Tr("staff.spec_attack", "ATTACK"),
            "DEFENCE" => Loc.Tr("staff.spec_defence", "DEFENCE"),
            "GENERAL" => Loc.Tr("staff.spec_general", "GENERAL"),
            _ => specialty ?? "",
        };
    }

    // Display ordinal ("1ST", "2ND", ...). The English suffix table is grammar
    // and per-language overridable; the numeric part is kept verbatim. This is
    // the DISPLAY ordinal only — CompetitionEngine keeps its own English
    // Ordinal() for the persisted History lines.
    public static string Ordinal(int n)
    {
        int rem100 = n % 100;
        string suffix = (rem100 >= 11 && rem100 <= 13)
            ? Loc.Tr("comp.ord_th", "TH")
            : (n % 10) switch
            {
                1 => Loc.Tr("comp.ord_st", "ST"),
                2 => Loc.Tr("comp.ord_nd", "ND"),
                3 => Loc.Tr("comp.ord_rd", "RD"),
                _ => Loc.Tr("comp.ord_th", "TH"),
            };
        return n + suffix;
    }
}
