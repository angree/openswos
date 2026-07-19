using OpenSwos.Menu;

namespace OpenSwos.Assets;

// Country names + continent grouping for TEAM.* nation indices, extracted from
// the original game's data via the swos-port IDA disassembly (constants copied,
// layout paraphrased — see CLAUDE.md porting rules):
//   - CountryNumbers enum (CN_ALBANIA=0 .. CN_OCEANIA=85):
//       external/swos-port/swos/swos.asm:1259-1329
//   - name records (continent byte + 'NAME',0 + adjective):
//       swos.asm:186298-186527 (albania .. oceania/world)
//   - countriesTable (nation index -> name record, incl. the gap slots):
//       swos.asm:186528-186614
// Gap indices with a real record in countriesTable: 9 (a second ENGLAND slot,
// swos.asm:186538/186326) and 72 (CUSTOM, swos.asm:186601/186497). All other
// gaps (47, 52-54, 56-59, 61, 63, 68, 70, 74) are dd 0 in the original table
// and fall back to "NATION n" here.
//
// Continent source: the leading db byte of each name record (e.g. albania db 0,
// algeria db 5, argentina db 2, elSalvador db 1, japan db 3, australia db 4 —
// swos.asm:186298-186516). The original encoding is 0=EUROPE, 1=NORTH AMERICA,
// 2=SOUTH AMERICA, 3=ASIA, 4=OCEANIA, 5=AFRICA; we remap it to the
// CN_EUROPE..CN_OCEANIA order (80..85, swos.asm:1324-1329) so that
// Continent(80 + c) == c: 0=EUROPE, 1=AFRICA, 2=SOUTH AMERICA,
// 3=NORTH AMERICA, 4=ASIA, 5=OCEANIA.
public static class NationNames
{
    public const int Count = 86;   // CN_ALBANIA .. CN_OCEANIA

    private static readonly string[] kContinents =
    {
        "EUROPE", "AFRICA", "SOUTH AMERICA", "NORTH AMERICA", "ASIA", "OCEANIA",
    };

    // Index = TEAM.* nation byte. Name null = empty slot in countriesTable.
    // Continent codes use the CN_EUROPE..CN_OCEANIA order documented above.
    private static readonly (string? Name, byte Continent)[] kNations =
    {
        ("ALBANIA", 0),            //  0
        ("AUSTRIA", 0),            //  1
        ("BELGIUM", 0),            //  2
        ("BULGARIA", 0),           //  3
        ("CROATIA", 0),            //  4
        ("CYPRUS", 0),             //  5
        ("CZECH REPUBLIC", 0),     //  6
        ("DENMARK", 0),            //  7
        ("ENGLAND", 0),            //  8
        ("ENGLAND", 0),            //  9 — duplicate slot in the original table
        ("ESTONIA", 0),            // 10
        ("FAROE ISLANDS", 0),      // 11
        ("FINLAND", 0),            // 12
        ("FRANCE", 0),             // 13
        ("GERMANY", 0),            // 14
        ("GREECE", 0),             // 15
        ("HUNGARY", 0),            // 16
        ("ICELAND", 0),            // 17
        ("REPUBLIC IRELAND", 0),   // 18
        ("ISRAEL", 0),             // 19 — UEFA member, Europe in SWOS
        ("ITALY", 0),              // 20
        ("LATVIA", 0),             // 21
        ("LITHUANIA", 0),          // 22
        ("LUXEMBOURG", 0),         // 23
        ("MALTA", 0),              // 24
        ("HOLLAND", 0),            // 25
        ("NORTHERN IRELAND", 0),   // 26
        ("NORWAY", 0),             // 27
        ("POLAND", 0),             // 28
        ("PORTUGAL", 0),           // 29
        ("ROMANIA", 0),            // 30
        ("RUSSIA", 0),             // 31
        ("SAN MARINO", 0),         // 32
        ("SCOTLAND", 0),           // 33
        ("SLOVENIA", 0),           // 34
        ("SPAIN", 0),              // 35
        ("SWEDEN", 0),             // 36
        ("SWITZERLAND", 0),        // 37
        ("TURKEY", 0),             // 38
        ("UKRAINE", 0),            // 39
        ("WALES", 0),              // 40
        ("YUGOSLAVIA", 0),         // 41
        ("ALGERIA", 1),            // 42
        ("ARGENTINA", 2),          // 43
        ("AUSTRALIA", 5),          // 44
        ("BOLIVIA", 2),            // 45
        ("BRAZIL", 2),             // 46
        (null, 0),                 // 47
        ("CHILE", 2),              // 48
        ("COLOMBIA", 2),           // 49
        ("ECUADOR", 2),            // 50
        ("EL SALVADOR", 3),        // 51
        (null, 0),                 // 52
        (null, 0),                 // 53
        (null, 0),                 // 54
        ("JAPAN", 4),              // 55
        (null, 0),                 // 56
        (null, 0),                 // 57
        (null, 0),                 // 58
        (null, 0),                 // 59
        ("MEXICO", 3),             // 60
        (null, 0),                 // 61
        ("NEW ZEALAND", 5),        // 62
        (null, 0),                 // 63
        ("PARAGUAY", 2),           // 64
        ("PERU", 2),               // 65
        ("SURINAM", 2),            // 66
        ("TAIWAN", 4),             // 67
        (null, 0),                 // 68
        ("SOUTH AFRICA", 1),       // 69
        (null, 0),                 // 70
        ("URUGUAY", 2),            // 71
        ("CUSTOM", 0),             // 72 — custom-team slot in the original table
        ("U.S.A.", 3),             // 73
        (null, 0),                 // 74
        ("INDIA", 4),              // 75
        ("BELARUS", 0),            // 76
        ("VENEZUELA", 2),          // 77
        ("SLOVAKIA", 0),           // 78
        ("GHANA", 1),              // 79
        ("EUROPE", 0),             // 80 — continental national sides (CN_EUROPE..)
        ("AFRICA", 1),             // 81
        ("SOUTH AMERICA", 2),      // 82
        ("NORTH AMERICA", 3),      // 83
        ("ASIA", 4),               // 84
        ("OCEANIA", 5),            // 85
    };

    // Country name for a TEAM.* nation index ("NATION n" for empty/unknown slots).
    public static string Name(int nation)
        => nation >= 0 && nation < kNations.Length && kNations[nation].Name is string s
            ? s : $"NATION {nation}";

    // Continent code (0=EUROPE..5=OCEANIA, order above) for a nation index.
    // Empty/unknown slots report EUROPE — they never surface in pickers because
    // callers filter to nations that actually contain teams.
    public static int Continent(int nation)
        => nation >= 0 && nation < kNations.Length ? kNations[nation].Continent : 0;

    public static string ContinentName(int c)
    {
        string en = c >= 0 && c < kContinents.Length ? kContinents[c] : $"CONTINENT {c}";
        // Display-time only: the stored continent CODE (int c) is unchanged; we
        // translate solely the rendered name. English is the identity fallback.
        return c switch
        {
            0 => Loc.Tr("nation.continent_europe", en),
            1 => Loc.Tr("nation.continent_africa", en),
            2 => Loc.Tr("nation.continent_south_america", en),
            3 => Loc.Tr("nation.continent_north_america", en),
            4 => Loc.Tr("nation.continent_asia", en),
            5 => Loc.Tr("nation.continent_oceania", en),
            _ => en,
        };
    }
}
