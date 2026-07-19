using OpenSwos.Tools.RncDecode;
using OpenSwos.Tools.TeamDecode;

namespace OpenSwos.Assets;

public sealed class AmigaAssetSource : IAssetSource
{
    private readonly string _dataDir;

    public GameVariant Variant => GameVariant.Amiga;

    // dataDir = path to extracted disk2/data/ where the Amiga TEAM.* files live.
    // Files are RNC ProPack v1 compressed — decompress transparently before parsing.
    public AmigaAssetSource(string dataDir) => _dataDir = dataDir;

    public IEnumerable<TeamRecord> LoadAllTeams()
    {
        if (!Directory.Exists(_dataDir)) yield break;
        // Case-insensitive: on Linux/Android/R36S a "TEAM.*" pattern would miss a
        // lowercased set (team.000 …), which is exactly what a DOS install copied
        // off a FAT partition looks like — folder found, yet ZERO teams loaded.
        foreach (string file in Directory.EnumerateFiles(
                     _dataDir, "TEAM.*", DataPaths.CaseInsensitiveScan))
        {
            foreach (var rec in ParseFile(file)) yield return rec;
        }
    }

    public IEnumerable<TeamRecord> LoadTeamsForNation(int nation)
    {
        string file = DataPaths.ResolveFile(_dataDir, $"TEAM.{nation:D3}");
        if (file.Length == 0) yield break;
        foreach (var rec in ParseFile(file)) yield return rec;
    }

    private static IEnumerable<TeamRecord> ParseFile(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
            if (data.Length >= 4 && data[0] == (byte)'R' && data[1] == (byte)'N'
                && data[2] == (byte)'C' && data[3] == 0x01)
            {
                data = RncV1.Decode(data);
            }
        }
        catch (RncV1Exception) { yield break; }
        catch { yield break; }

        List<Team> teams;
        try { teams = TeamFile.Read(data); }
        catch (TeamFileException) { yield break; }
        foreach (var t in teams) yield return AssetMapping.From(t);
    }
}
