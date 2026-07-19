using System;
using System.Collections.Generic;
using OpenSwos.Menu;

namespace OpenSwos.Competition.Career;

/// <summary>A deterministic, non-persistent coach offer shown by the career UI.</summary>
public sealed class CoachHireCandidate
{
    public int Slot { get; init; }
    public string Name { get; init; } = "";
    public string Specialty { get; init; } = "";
    public int Quality { get; init; }
    public long Wage { get; init; }
    public long SigningFee { get; init; }
}

/// <summary>
/// Deterministic club staff hiring and the development benefits supplied by
/// coaches. Staff randomness is isolated from the competition RNG stream.
/// </summary>
public static class StaffModel
{
    public const int MaximumCoaches = 3;
    public const int MaximumTrainingFocus = 3;
    private const int FocusCount = MaximumTrainingFocus;

    /// <summary>
    /// Returns three stable offers for this club and season without changing the
    /// world. The club's resources determine the quality band.
    /// </summary>
    public static List<CoachHireCandidate> HireCandidates(CareerWorld? world, ushort clubId)
    {
        List<CoachHireCandidate> candidates = new();
        if (world?.Clubs is null || !world.Clubs.TryGetValue(clubId, out CareerClub? club) || club is null)
            return candidates;

        CareerRng rng = new(unchecked((uint)world.Season), clubId);
        string[] specialties = ["YOUTH", "ATTACK", "DEFENCE"];
        int rotation = rng.NextInt(specialties.Length);
        for (int slot = 0; slot < specialties.Length; slot++)
        {
            int quality = QualityFor(club, ref rng);
            long wage = 100_000L + quality * 100_000L;
            candidates.Add(new CoachHireCandidate
            {
                Slot = slot,
                Name = "COACH " + clubId + "-" + (char)('A' + slot),
                Specialty = specialties[(rotation + slot) % specialties.Length],
                Quality = quality,
                Wage = wage,
                SigningFee = wage * 2L,
            });
        }
        return candidates;
    }

    /// <summary>Attempts an atomic hire from the current season's deterministic offers.</summary>
    public static bool TryHire(CareerWorld? world, ushort clubId, int candidateSlot, out string refusal)
    {
        refusal = Loc.Tr("staff.notice_unavailable", "STAFF UNAVAILABLE");
        if (world?.Clubs is null || !world.Clubs.TryGetValue(clubId, out CareerClub? club) || club is null)
            return false;
        if (club.Coaches is null) { refusal = Loc.Tr("staff.notice_unavailable", "STAFF UNAVAILABLE"); return false; }
        if (club.Coaches.Count >= MaximumCoaches) { refusal = Loc.Tr("staff.notice_full", "STAFF FULL"); return false; }

        List<CoachHireCandidate> candidates = HireCandidates(world, clubId);
        CoachHireCandidate? candidate = candidates.Find(item => item.Slot == candidateSlot);
        if (candidate is null) { refusal = Loc.Tr("staff.notice_coach_unavailable", "COACH NOT AVAILABLE"); return false; }
        if (club.Budget < candidate.SigningFee) { refusal = Loc.Tr("staff.notice_no_money", "NOT ENOUGH MONEY"); return false; }
        if (world.NextPlayerId == int.MaxValue) { refusal = Loc.Tr("staff.notice_unavailable", "STAFF UNAVAILABLE"); return false; }

        long remainingBudget;
        try { remainingBudget = checked(club.Budget - candidate.SigningFee); }
        catch (OverflowException) { refusal = Loc.Tr("staff.notice_no_money", "NOT ENOUGH MONEY"); return false; }

        club.Coaches.Add(new Coach
        {
            Id = world.NextPlayerId++,
            Name = candidate.Name,
            Specialty = candidate.Specialty,
            Quality = candidate.Quality,
            Wage = candidate.Wage,
        });
        club.Budget = remainingBudget;
        refusal = "";
        return true;
    }

    /// <summary>Fires one current coach and charges a half-wage severance.</summary>
    public static bool TryFire(CareerWorld? world, ushort clubId, int coachId, out string refusal)
    {
        refusal = Loc.Tr("staff.notice_coach_unavailable", "COACH NOT AVAILABLE");
        if (world?.Clubs is null || !world.Clubs.TryGetValue(clubId, out CareerClub? club) || club?.Coaches is null)
            return false;

        Coach? coach = club.Coaches.Find(item => item is not null && item.Id == coachId);
        if (coach is null) return false;
        long severance = Math.Max(0L, coach.Wage) / 2L;
        if (club.Budget < severance) { refusal = Loc.Tr("staff.notice_no_money", "NOT ENOUGH MONEY"); return false; }
        long remainingBudget;
        try { remainingBudget = checked(club.Budget - severance); }
        catch (OverflowException) { refusal = Loc.Tr("staff.notice_no_money", "NOT ENOUGH MONEY"); return false; }

        club.Coaches.Remove(coach);
        club.Budget = remainingBudget;
        refusal = "";
        return true;
    }

    /// <summary>Adds or removes a squad player from the manually chosen training focus.</summary>
    public static bool TryToggleTrainingFocus(CareerWorld? world, ushort clubId, int playerId, out string refusal)
    {
        refusal = Loc.Tr("staff.notice_player_unavailable", "PLAYER NOT AVAILABLE");
        if (world?.Clubs is null || !world.Clubs.TryGetValue(clubId, out CareerClub? club) || club?.Squad is null)
            return false;
        if (!club.Squad.Exists(player => player is not null && player.Id == playerId && player.ClubId == clubId))
            return false;

        List<int> focus = club.TrainingFocusIds ??= new List<int>();
        if (focus.Contains(playerId))
        {
            focus.RemoveAll(id => id == playerId);
            refusal = "";
            return true;
        }
        if (focus.Count >= MaximumTrainingFocus) { refusal = Loc.Tr("staff.notice_focus_full", "FOCUS FULL"); return false; }

        focus.Add(playerId);
        refusal = "";
        return true;
    }

    /// <summary>Returns the development multiplier supplied to a club player by its staff.</summary>
    public static double GrowthMultiplier(CareerClub club, CareerPlayer player)
    {
        if (club is null) throw new ArgumentNullException(nameof(club));
        if (player is null) throw new ArgumentNullException(nameof(player));

        double multiplier = 1.0;
        int bestYouthQuality = 0;

        if (club.Coaches is not null)
        foreach (Coach? coach in club.Coaches)
        {
            if (coach is null) continue;
            int quality = Math.Clamp(coach.Quality, 0, 7);
            double scaledQuality = quality / 7.0;
            string specialty = coach.Specialty?.Trim().ToUpperInvariant() ?? string.Empty;

            switch (specialty)
            {
                case "YOUTH":
                    if (player.Age <= 21)
                        multiplier += 0.3 * scaledQuality;
                    bestYouthQuality = Math.Max(bestYouthQuality, quality);
                    break;
                case "ATTACK":
                    if (IsAttacker(player.Position))
                        multiplier += 0.3 * scaledQuality;
                    break;
                case "DEFENCE":
                    if (IsDefender(player.Position))
                        multiplier += 0.3 * scaledQuality;
                    break;
                case "GENERAL":
                    multiplier += 0.15 * scaledQuality;
                    break;
            }
        }

        if (bestYouthQuality > 0 && club.TrainingFocusIds?.Contains(player.Id) == true)
            multiplier += 0.4 * bestYouthQuality / 7.0;

        return Math.Clamp(multiplier, 1.0, 1.8);
    }

    /// <summary>
    /// Runs each club's once-per-season, deterministic staff decisions and
    /// refreshes its promising-player training focus after youth regeneration.
    /// </summary>
    public static void RunClubStaffAI(CareerWorld world)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        List<ushort> clubIds = new(world.Clubs.Keys);
        clubIds.Sort();
        foreach (ushort clubId in clubIds)
        {
            CareerClub club = world.Clubs[clubId];
            HireCoachIfNeeded(world, club);
            RecomputeTrainingFocus(club);
        }
    }

    private static void HireCoachIfNeeded(CareerWorld world, CareerClub club)
    {
        List<Coach> coaches = club.Coaches ??= new List<Coach>();
        bool hasYouthCoach = false;
        foreach (Coach? coach in coaches)
        {
            if (coach is null) continue;
            if (string.Equals(coach.Specialty, "YOUTH", StringComparison.OrdinalIgnoreCase))
            {
                hasYouthCoach = true;
                break;
            }
        }

        if (hasYouthCoach || coaches.Count >= MaximumCoaches) return;

        CareerRng rng = new(unchecked((uint)world.Season), club.GlobalId);
        int quality = QualityFor(club, ref rng);
        long wage = 100_000L + quality * 100_000L;
        long signingCost = wage * 2L;
        if (club.Budget < signingCost) return;

        int id = world.NextPlayerId++;
        coaches.Add(new Coach
        {
            Id = id,
            Name = "COACH " + id,
            Quality = quality,
            Specialty = SpecialtyFor(ref rng),
            Wage = wage,
        });
        club.Budget -= signingCost;
    }

    private static int QualityFor(CareerClub club, ref CareerRng rng)
    {
        long clubValue = Finance.ClubValue(club);
        long resources = Math.Max(0L, club.Budget) + clubValue / 10L;
        int wealthTier = resources switch
        {
            >= 500_000_000L => 4,
            >= 200_000_000L => 3,
            >= 75_000_000L => 2,
            >= 20_000_000L => 1,
            _ => 0,
        };
        return Math.Clamp(2 + wealthTier + rng.NextInt(2), 1, 7);
    }

    private static string SpecialtyFor(ref CareerRng rng) => rng.NextInt(10) switch
    {
        0 => "ATTACK",
        1 => "DEFENCE",
        _ => "YOUTH",
    };

    private static void RecomputeTrainingFocus(CareerClub club)
    {
        List<CareerPlayer> candidates = club.Squad is null ? new() : new(club.Squad);
        candidates.Sort((left, right) =>
        {
            double leftHeadroom = PotentialModel.OverallOf(left);
            double rightHeadroom = PotentialModel.OverallOf(right);
            int byHeadroom = (right.Potential - rightHeadroom).CompareTo(left.Potential - leftHeadroom);
            if (byHeadroom != 0) return byHeadroom;
            int byAge = left.Age.CompareTo(right.Age);
            return byAge != 0 ? byAge : left.Id.CompareTo(right.Id);
        });

        List<int> focus = club.TrainingFocusIds ??= new List<int>();
        focus.Clear();
        for (int i = 0; i < candidates.Count && i < FocusCount; i++)
            focus.Add(candidates[i].Id);
    }

    private static bool IsAttacker(string position)
    {
        string key = position?.Trim().ToUpperInvariant() ?? string.Empty;
        return key is "A" or "ST" or "CF" or "FW"
            || key.Contains("ATTACK", StringComparison.Ordinal)
            || key.Contains("FORWARD", StringComparison.Ordinal)
            || key.Contains("STRIKER", StringComparison.Ordinal);
    }

    private static bool IsDefender(string position)
    {
        string key = position?.Trim().ToUpperInvariant() ?? string.Empty;
        return key == "D" || key.Contains("DEFEND", StringComparison.Ordinal);
    }
}
