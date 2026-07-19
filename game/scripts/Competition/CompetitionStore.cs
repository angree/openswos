using System;
using System.IO;
using System.Text.Json;
using Godot;
using OpenSwos.Menu;

namespace OpenSwos.Competition;

// ============================================================================
// CompetitionStore — competition state persisted as pretty-printed JSON.
//
// Two tiers:
//   * AUTOSAVE slot — the legacy single-slot API (Save/Load/Delete/Exists) at
//     user://competition.json. Behaviour unchanged.
//   * Named slots — SaveAs/LoadSlot/DeleteSlot at user://saves/<SLOT>.json.
//     Slot names are sanitized to A-Z 0-9 '-' '_' (max 16 chars). The slot
//     name "AUTOSAVE" is reserved and aliases the legacy slot.
//   * ListSlots() enumerates everything (AUTOSAVE first when present) with a
//     human-readable label built from the loaded state.
//
// All methods are best-effort: writes/deletes log and swallow I/O errors,
// loads return null on a missing or corrupt file so the menu can fall back to
// "no competition in progress".
// ============================================================================

public static class CompetitionStore
{
    private const string SavePath = "user://competition.json";
    private const string SlotsDir = "user://saves";
    private const string AutosaveSlot = "AUTOSAVE";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string FilePath() => ProjectSettings.GlobalizePath(SavePath);

    public static void Save(CompetitionState state)
    {
        try
        {
            string path = FilePath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOpts));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore.Save failed: {ex.Message}");
        }
    }

    public static CompetitionState? Load()
    {
        try
        {
            string path = FilePath();
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CompetitionState>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore.Load failed: {ex.Message}");
            return null;
        }
    }

    public static void Delete()
    {
        try
        {
            string path = FilePath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore.Delete failed: {ex.Message}");
        }
    }

    public static bool Exists()
    {
        try { return File.Exists(FilePath()); }
        catch { return false; }
    }

    // ------------------------------------------------------------------ slots

    // Reduce an arbitrary user string to a safe slot name: uppercase ASCII,
    // keep only A-Z 0-9 '-' '_', cap at 16 chars; empty result -> "SAVE".
    public static string SanitizeSlotName(string slotName)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char raw in (slotName ?? "").ToUpperInvariant())
        {
            char c = raw;
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_')
            {
                sb.Append(c);
                if (sb.Length >= 16) break;
            }
        }
        return sb.Length == 0 ? "SAVE" : sb.ToString();
    }

    private static string SlotFilePath(string slotName) =>
        Path.Combine(ProjectSettings.GlobalizePath(SlotsDir), slotName + ".json");

    public static void SaveAs(CompetitionState state, string slotName)
    {
        string slot = SanitizeSlotName(slotName);
        if (slot == AutosaveSlot) { Save(state); return; }
        try
        {
            string path = SlotFilePath(slot);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOpts));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore.SaveAs('{slot}') failed: {ex.Message}");
        }
    }

    public static CompetitionState? LoadSlot(string slotName)
    {
        string slot = SanitizeSlotName(slotName);
        if (slot == AutosaveSlot) return Load();
        try
        {
            string path = SlotFilePath(slot);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CompetitionState>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore.LoadSlot('{slot}') failed: {ex.Message}");
            return null;
        }
    }

    public static void DeleteSlot(string slotName)
    {
        string slot = SanitizeSlotName(slotName);
        if (slot == AutosaveSlot) { Delete(); return; }
        try
        {
            string path = SlotFilePath(slot);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore.DeleteSlot('{slot}') failed: {ex.Message}");
        }
    }

    // (slotName, label) pairs for every existing save, AUTOSAVE first if
    // present. Cheap by design: each file is fully loaded to build its label
    // (there will only ever be a handful of slots). Corrupt files are skipped.
    public static System.Collections.Generic.List<(string slot, string label)> ListSlots()
    {
        var result = new System.Collections.Generic.List<(string slot, string label)>();

        CompetitionState? auto = Load();   // logs + returns null if corrupt
        if (auto != null) result.Add((AutosaveSlot, SlotLabel(auto)));

        try
        {
            string dir = ProjectSettings.GlobalizePath(SlotsDir);
            if (Directory.Exists(dir))
            {
                var files = Directory.GetFiles(dir, "*.json");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    string slot = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
                    if (slot == AutosaveSlot) continue;   // reserved alias, never a named slot
                    try
                    {
                        var state = JsonSerializer.Deserialize<CompetitionState>(File.ReadAllText(file), JsonOpts);
                        if (state == null) { GD.PrintErr($"CompetitionStore.ListSlots: '{slot}' is empty, skipped"); continue; }
                        result.Add((slot, SlotLabel(state)));
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"CompetitionStore.ListSlots: '{slot}' corrupt, skipped: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore.ListSlots failed: {ex.Message}");
        }
        return result;
    }

    // "<NAME> - <round/champion summary> - <team you manage>". Derived from
    // the state alone — deliberately no CompetitionEngine dependency.
    private static string SlotLabel(CompetitionState state)
    {
        string progress;
        if (state.Finished)
        {
            progress = state.Champion >= 0 && state.Champion < state.Teams.Count
                ? string.Format(Loc.Tr("comp.slot_winner", "WINNER {0}"), state.Teams[state.Champion].Name)
                : Loc.Tr("comp.slot_finished", "FINISHED");
        }
        else
        {
            progress = string.Format(
                Loc.Tr("comp.slot_round", "ROUND {0}/{1}"), state.CurrentRound + 1, state.TotalRounds);
        }
        string team = state.PlayerTeam >= 0 && state.PlayerTeam < state.Teams.Count
            ? state.Teams[state.PlayerTeam].Name
            : Loc.Tr("comp.slot_no_team", "NO TEAM");
        return $"{state.Name} - {progress} - {team}";
    }
}
