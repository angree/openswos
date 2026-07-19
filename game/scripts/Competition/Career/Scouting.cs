using System;
using System.Collections.Generic;
using OpenSwos.Menu;

namespace OpenSwos.Competition.Career;

/// <summary>
/// Deterministic scouting knowledge. Potential remains hidden; scouting exposes
/// only a deliberately fallible range which improves with repeat observation.
/// </summary>
public static class Scouting
{
    private const int PlayersScoutedPerSeason = 3;
    public const long BaseScoutingCost = 50_000L;
    public const long QualityScoutingCost = 25_000L;
    public const long BaseScoutUpgradeCost = 150_000L;

    /// <summary>Returns the cost of one player observation at this scout level.</summary>
    public static long PlayerScoutingCost(int scoutQuality)
        => BaseScoutingCost + Math.Clamp(scoutQuality, 0, 7) * QualityScoutingCost;

    /// <summary>Returns the cost to improve from this level to the next one.</summary>
    public static long ScoutUpgradeCost(int scoutQuality)
    {
        int quality = Math.Clamp(scoutQuality, 0, 7);
        return quality >= 7 ? 0L : BaseScoutUpgradeCost * (quality + 1L);
    }

    /// <summary>
    /// Pays for a deterministic observation of a market player. Failed attempts
    /// leave the budget, watch list and estimate untouched.
    /// </summary>
    public static bool TryScoutPlayer(CareerWorld? world, ushort clubId, int playerId, out string refusal)
    {
        refusal = Loc.Tr("scout.notice_player_unavailable", "PLAYER NOT AVAILABLE");
        if (world?.Clubs is null || !world.Clubs.TryGetValue(clubId, out CareerClub? club) || club is null)
            return false;
        CareerPlayer? player = FindExternalPlayer(world, clubId, playerId);
        if (player is null) return false;

        ScoutingState? existing = club.Scouting;
        int quality = Math.Clamp(existing?.ScoutQuality ?? 0, 0, 7);
        long cost = PlayerScoutingCost(quality);
        if (club.Budget < cost) { refusal = Loc.Tr("scout.notice_no_money", "NOT ENOUGH MONEY"); return false; }
        long remainingBudget;
        try { remainingBudget = checked(club.Budget - cost); }
        catch (OverflowException) { refusal = Loc.Tr("scout.notice_no_money", "NOT ENOUGH MONEY"); return false; }

        ScoutingState scouting = existing ?? new ScoutingState();
        List<int> watched = scouting.WatchedPlayerIds ??= new List<int>();
        CareerRng rng = new(unchecked((uint)world.Season), clubId);
        RevealEstimate(player, quality, rng.NextU());
        if (!watched.Contains(player.Id)) watched.Add(player.Id);
        club.Scouting = scouting;
        club.Budget = remainingBudget;
        refusal = "";
        return true;
    }

    /// <summary>Raises a club's scouting quality by one, capped at seven.</summary>
    public static bool TryImproveScoutQuality(CareerWorld? world, ushort clubId, out string refusal)
    {
        refusal = Loc.Tr("scout.notice_unavailable", "SCOUTING UNAVAILABLE");
        if (world?.Clubs is null || !world.Clubs.TryGetValue(clubId, out CareerClub? club) || club is null)
            return false;

        ScoutingState? existing = club.Scouting;
        int quality = Math.Clamp(existing?.ScoutQuality ?? 0, 0, 7);
        if (quality >= 7) { refusal = Loc.Tr("scout.notice_maxed", "SCOUTING MAXED"); return false; }
        long cost = ScoutUpgradeCost(quality);
        if (club.Budget < cost) { refusal = Loc.Tr("scout.notice_no_money", "NOT ENOUGH MONEY"); return false; }
        long remainingBudget;
        try { remainingBudget = checked(club.Budget - cost); }
        catch (OverflowException) { refusal = Loc.Tr("scout.notice_no_money", "NOT ENOUGH MONEY"); return false; }

        ScoutingState scouting = existing ?? new ScoutingState();
        scouting.ScoutQuality = quality + 1;
        club.Scouting = scouting;
        club.Budget = remainingBudget;
        refusal = "";
        return true;
    }

    /// <summary>
    /// Reveals or refines a fuzzy estimate of a player's hidden potential.
    /// The interval always retains a positive width, even for the best scouts.
    /// </summary>
    public static void RevealEstimate(CareerPlayer p, int scoutQuality, uint seed)
    {
        if (p is null) throw new ArgumentNullException(nameof(p));

        int quality = Math.Clamp(scoutQuality, 0, 7);
        int priorAccuracy = Math.Max(0, p.ScoutAccuracy);
        CareerRng rng = new(seed, p.Id);

        // A poor scout can be led about a full potential point astray. A strong
        // scout has much less bias, but the estimate is intentionally never exact.
        double luck = (rng.NextDouble() * 2.0 - 1.0) * (8 - quality) / 8.0;
        double truePotential = FiniteClamp(p.Potential, 0.0, 7.0);
        double center = truePotential + luck;
        double baseError = (8 - quality) * 0.35 / (1.0 + priorAccuracy * 0.5);
        double halfWidth = Math.Max(0.25, baseError);

        double low = Math.Clamp(center - halfWidth, 0.0, 7.0);
        double high = Math.Clamp(center + halfWidth, 0.0, 7.0);

        // The normal calculation has positive width for a valid potential, but
        // retain the invariant if a malformed save supplied non-finite values.
        if (high <= low)
        {
            if (low <= 0.0)
                high = 0.25;
            else
            {
                low = 6.75;
                high = 7.0;
            }
        }

        p.EstLow = low;
        p.EstHigh = high;
        p.Scouted = true;
        if (p.ScoutAccuracy < int.MaxValue)
            p.ScoutAccuracy++;
    }

    /// <summary>
    /// Runs each club's annual scouting work. Clubs with funds watch their most
    /// promising young players and pay a small, deterministic operating cost.
    /// </summary>
    public static void RunScoutingAI(CareerWorld world)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        List<ushort> clubIds = new(world.Clubs.Keys);
        clubIds.Sort();
        foreach (ushort clubId in clubIds)
        {
            CareerClub club = world.Clubs[clubId];
            if (club.Budget <= 0L) continue;

            CareerRng rng = new(unchecked((uint)world.Season), club.GlobalId);
            ScoutingState scouting = club.Scouting ??= new ScoutingState();
            scouting.Regions ??= new List<int>();
            scouting.WatchedPlayerIds ??= new List<int>();
            EnsureNetworkRegion(scouting, club.GlobalId);

            if (scouting.ScoutQuality <= 0)
                scouting.ScoutQuality = SeedScoutQuality(club, ref rng);
            else
                scouting.ScoutQuality = Math.Clamp(scouting.ScoutQuality, 0, 7);

            long cost = PlayerScoutingCost(scouting.ScoutQuality);
            club.Budget -= cost;

            List<CareerPlayer> candidates = PromisingYoungPlayers(club);
            int count = Math.Min(PlayersScoutedPerSeason, candidates.Count);
            for (int i = 0; i < count; i++)
            {
                CareerPlayer player = candidates[i];
                if (!scouting.WatchedPlayerIds.Contains(player.Id))
                    scouting.WatchedPlayerIds.Add(player.Id);

                // The club/season key supplies an independent repeatable seed;
                // RevealEstimate then keys the result to this stable player id.
                RevealEstimate(player, scouting.ScoutQuality, rng.NextU());
            }
        }
    }

    private static int SeedScoutQuality(CareerClub club, ref CareerRng rng)
    {
        long resources = Math.Max(0L, club.Budget) + Finance.ClubValue(club) / 10L;
        int wealthTier = resources switch
        {
            >= 500_000_000L => 5,
            >= 200_000_000L => 4,
            >= 75_000_000L => 3,
            >= 20_000_000L => 2,
            >= 5_000_000L => 1,
            _ => 0,
        };
        return Math.Clamp(1 + wealthTier + rng.NextInt(2), 1, 7);
    }

    private static void EnsureNetworkRegion(ScoutingState scouting, ushort clubId)
    {
        if (scouting.Regions.Count == 0)
            scouting.Regions.Add(clubId % 8);
    }

    private static List<CareerPlayer> PromisingYoungPlayers(CareerClub club)
    {
        List<CareerPlayer> candidates = new();
        if (club.Squad is null) return candidates;
        foreach (CareerPlayer? player in club.Squad)
            if (player is not null && !player.Retired)
                candidates.Add(player);

        candidates.Sort((left, right) =>
        {
            int byAge = left.Age.CompareTo(right.Age);
            if (byAge != 0) return byAge;

            double leftHeadroom = FiniteClamp(left.Potential - PotentialModel.OverallOf(left), 0.0, 7.0);
            double rightHeadroom = FiniteClamp(right.Potential - PotentialModel.OverallOf(right), 0.0, 7.0);
            int byHeadroom = rightHeadroom.CompareTo(leftHeadroom);
            return byHeadroom != 0 ? byHeadroom : left.Id.CompareTo(right.Id);
        });
        return candidates;
    }

    private static CareerPlayer? FindExternalPlayer(CareerWorld world, ushort clubId, int playerId)
    {
        if (world.FreeAgents is not null)
            foreach (CareerPlayer? player in world.FreeAgents)
                if (player is not null && player.Id == playerId && player.ClubId == 0 && !player.Retired)
                    return player;

        if (world.Clubs is null) return null;
        List<ushort> clubIds = new(world.Clubs.Keys);
        clubIds.Sort();
        foreach (ushort otherClubId in clubIds)
        {
            if (otherClubId == clubId) continue;
            CareerClub? otherClub = world.Clubs[otherClubId];
            if (otherClub?.Squad is null) continue;
            foreach (CareerPlayer? player in otherClub.Squad)
                if (player is not null && player.Id == playerId && player.ClubId == otherClubId && !player.Retired)
                    return player;
        }
        return null;
    }

    private static double FiniteClamp(double value, double minimum, double maximum)
        => double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : minimum;
}
