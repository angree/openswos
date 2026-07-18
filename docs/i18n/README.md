# OpenSWOS UI string database (i18n)

`strings.tsv` is the **master database of every user-facing English string** in
the OpenSWOS front-end (menus, competition/career screens, and the in-match
HUD). It is the source of truth translators work from and the input to the
per-language translation tables loaded by `game/scripts/Menu/Loc.cs`.

## Format

Tab-separated values, UTF-8, one row per string, with a header line:

```
key<TAB>english<TAB>context
```

- **key** — a stable, lowercase, dot-separated identifier in `screen.item`
  style (e.g. `opt.half_length`, `friendly.kick_off`, `common.back`). The key
  never changes once assigned, even if the English text is reworded, so
  existing translations keep matching. It is the lookup key for
  `Loc.Tr(key, english)`.
- **english** — the exact English text as it appears in the UI, **case
  preserved** (the SWOS charset is uppercase, so most strings are uppercase).
  Placeholders such as `%d` / `%s` mark runtime-substituted values (numbers,
  names, money) and MUST be kept in the translation in the same order.
- **context** — a one-sentence description of **where the string appears and
  what it means/does**, written for a translator with no access to the game.
  This is essential because the same English word is translated differently by
  context in many languages (e.g. a menu "PLAY" verb vs. a noun elsewhere).

## Conventions

- **Blank lines and lines beginning with `#`** are comments/section separators.
  The loader ignores them; they exist to group the file by screen for humans.
- **Grouping.** Rows are grouped by screen (`# HOME`, `# OPTIONS`, …). Strings
  shared across many screens live under `common.*` (BACK, ON, OFF, NONE, …) and
  short table column headers under `col.*`.
- **Deliberate duplicate English under different keys.** Because the context
  field drives translation, the same English text can legitimately appear under
  more than one key when its meaning differs. For example `col.p` = "P"
  (matches *Played*) vs. `fixture.penalties_mark` = "P" (decided on
  *Penalties*); `common.off` (a boolean toggle) vs. `val.music_off` (a music
  *source*). Translate each in its own context — do not assume identical English
  means identical translation.
- **Placeholders / prefixes.** Some rows are prefixes or suffixes that the code
  concatenates with runtime data (e.g. `save.saved_as_prefix` = "SAVED AS"
  followed by a slot name, `season.trophy_league` = "SEASON %d LEAGUE
  CHAMPIONS"). The context notes what is appended.

## Scope — what is and isn't here

Included: all display labels, option values, buttons, hints, headers, screen
titles and in-match banners.

**Excluded on purpose** (do not translate here):

- Team names, player names, coach names — these come from the game data files
  (`TEAM.*`) at runtime, not from this table.
- Continent and country names — these come from the real SWOS nation tables in
  `game/scripts/Assets/NationNames.cs` / `PlayerNationNames.cs`, not the UI.
- File paths, debug/`GD.Print` logs, and format-only fragments.

## How it is consumed

Every UI string is emitted through `Loc.Tr(key, english)`: the English text is
written inline at the call site (so the UI always works and the source is
self-documenting), and `key` selects the translated form for the current
language. A missing key or missing language falls back to the inline English, so
the game is never left with a blank or a raw key. The SWOS bitmap charset is
ASCII/Latin-basic with no accented glyphs, so translations are transliterated to
plain ASCII (é→E, ł→L, ö→O, …).

## Maintenance

When you add or change a user-facing string in the front-end:

1. Add/keep a row here with a stable key and a clear context sentence.
2. Wrap the call site in `Loc.Tr("your.key", "ENGLISH TEXT")`.
3. Regenerate/extend the per-language tables from this file.
