using System;
using System.Collections.Generic;
using OpenSwos.Menu;

namespace OpenSwos.Competition;

// ============================================================================
// CompetitionEngine — pure, static, deterministic competition logic for
// league / cup / tournament / career play. No Godot types, no System.Random:
// every random decision (draws, AI scores, penalty shootouts) flows through
// the xorshift32 state persisted in CompetitionState.RngState so a saved
// competition replays identically after load.
//
// Contract: see the comment block at the bottom of CompetitionModels.cs.
// ============================================================================

public static class CompetitionEngine
{
    // ------------------------------------------------------------------
    // Creation
    // ------------------------------------------------------------------

    /// Round-robin league via the Berger/circle method. Odd team counts get a
    /// bye (the padded slot simply produces no fixture). doubleRoundRobin
    /// appends the mirrored second half with continuing round numbers.
    public static CompetitionState CreateLeague(
        string name, List<TeamRef> teams, int playerTeam, bool doubleRoundRobin, int seed)
    {
        var s = NewState(CompetitionKind.League, name, teams, playerTeam, seed);
        s.DoubleRoundRobin = doubleRoundRobin;

        List<List<(int Home, int Away)>> rounds = RoundRobin(teams.Count);
        int half = rounds.Count;
        for (int r = 0; r < half; r++)
            foreach (var (h, a) in rounds[r])
                s.Fixtures.Add(new Fixture { Round = r, Stage = "LEAGUE", HomeTeam = h, AwayTeam = a });
        if (doubleRoundRobin)
            for (int r = 0; r < half; r++)
                foreach (var (h, a) in rounds[r])
                    s.Fixtures.Add(new Fixture { Round = half + r, Stage = "LEAGUE", HomeTeam = a, AwayTeam = h });

        s.TotalRounds = doubleRoundRobin ? half * 2 : half;
        return s;
    }

    /// Single-elimination cup. Round 1 is drawn at random (deterministic RNG);
    /// later rounds are created by RecordResult as each round completes.
    public static CompetitionState CreateCup(string name, List<TeamRef> teams, int playerTeam, int seed)
    {
        int c = teams?.Count ?? 0;
        if (c != 4 && c != 8 && c != 16 && c != 32)
            throw new ArgumentException("CUP NEEDS 4, 8, 16 OR 32 TEAMS", nameof(teams));

        var s = NewState(CompetitionKind.Cup, name, teams!, playerTeam, seed);
        var order = new List<int>(c);
        for (int i = 0; i < c; i++) order.Add(i);
        Shuffle(s, order);

        string stage = KnockoutStageLabel(c);
        for (int i = 0; i + 1 < c; i += 2)
            s.Fixtures.Add(new Fixture { Round = 0, Stage = stage, HomeTeam = order[i], AwayTeam = order[i + 1] });

        s.TotalRounds = Log2(c);
        return s;
    }

    /// Groups-of-4 tournament: random group draw, single round-robin inside
    /// each group (rounds 0..2), then a knockout built from the group tables
    /// once every group game is played (top 2 advance, cross-paired).
    public static CompetitionState CreateTournament(
        string name, List<TeamRef> teams, int playerTeam, int groupCount, int seed)
    {
        if (groupCount < 2 || groupCount > 8 || (groupCount & (groupCount - 1)) != 0)
            throw new ArgumentException("GROUP COUNT MUST BE 2, 4 OR 8", nameof(groupCount));
        if (teams is null || teams.Count != groupCount * 4)
            throw new ArgumentException("TOURNAMENT NEEDS EXACTLY 4 TEAMS PER GROUP", nameof(teams));

        var s = NewState(CompetitionKind.Tournament, name, teams, playerTeam, seed);
        s.GroupCount = groupCount;

        // Random draw: shuffled team indices, 4 consecutive per group.
        var order = new List<int>(teams.Count);
        for (int i = 0; i < teams.Count; i++) order.Add(i);
        Shuffle(s, order);
        for (int g = 0; g < groupCount; g++)
            for (int m = 0; m < 4; m++)
                s.GroupOf[order[g * 4 + m]] = g;

        // Single round-robin inside each group; all groups share rounds 0..2.
        List<List<(int Home, int Away)>> rr = RoundRobin(4);
        for (int r = 0; r < rr.Count; r++)
            for (int g = 0; g < groupCount; g++)
            {
                string stage = "GROUP " + (char)('A' + g);
                foreach (var (h, a) in rr[r])
                    s.Fixtures.Add(new Fixture
                    {
                        Round = r,
                        Stage = stage,
                        HomeTeam = order[g * 4 + h],
                        AwayTeam = order[g * 4 + a],
                    });
            }

        s.TotalRounds = GroupStageRounds + Log2(groupCount * 2);
        return s;
    }

    /// Career season 1: double round-robin league plus a domestic cup drawn
    /// from cupTeams (which must include the player's club). Cup rounds are
    /// interleaved into the league calendar (one cup round after every
    /// ~quarter of the league schedule, the cup final after the last league
    /// round). playerTeam indexes leagueTeams.
    public static CompetitionState CreateCareer(
        string name, List<TeamRef> leagueTeams, List<TeamRef> cupTeams,
        int playerTeam, int nation, int division, int seed)
    {
        if (leagueTeams is null || leagueTeams.Count < 2)
            throw new ArgumentException("CAREER LEAGUE NEEDS AT LEAST 2 TEAMS", nameof(leagueTeams));
        if (playerTeam < 0 || playerTeam >= leagueTeams.Count)
            throw new ArgumentOutOfRangeException(nameof(playerTeam));

        var s = new CompetitionState
        {
            Kind = CompetitionKind.Career,
            Name = name ?? "",
            RngState = SeedToRng(seed),
            Career = new CareerState
            {
                Season = 1,
                Nation = nation,
                Division = division,
                ClubName = leagueTeams[playerTeam].Name,
                ClubGlobalId = leagueTeams[playerTeam].GlobalId,
            },
        };
        BuildCareerSeason(s, leagueTeams, cupTeams, playerTeam);
        return s;
    }

    // ------------------------------------------------------------------
    // Fixture navigation
    // ------------------------------------------------------------------

    /// First unplayed fixture involving PlayerTeam (by Round, then list order).
    public static Fixture? NextPlayerFixture(CompetitionState state)
    {
        if (state.PlayerTeam < 0) return null;
        Fixture? best = null;
        foreach (var f in state.Fixtures)
            if (!f.Played && Involves(f, state.PlayerTeam) && (best is null || f.Round < best.Round))
                best = f;
        return best;
    }

    /// First unplayed fixture of any team (by Round, then list order).
    public static Fixture? NextFixture(CompetitionState state)
    {
        Fixture? best = null;
        foreach (var f in state.Fixtures)
            if (!f.Played && (best is null || f.Round < best.Round))
                best = f;
        return best;
    }

    // ------------------------------------------------------------------
    // Result recording and progression
    // ------------------------------------------------------------------

    /// Writes a result and drives all competition progression: penalty
    /// shootout on level knockout ties, next knockout round when a round
    /// completes, Finished/Champion, CurrentRound advance, and career
    /// season close-out. A second call on an already-played fixture is a
    /// no-op; a fixture that is not part of state throws.
    public static void RecordResult(CompetitionState state, Fixture fixture, int homeGoals, int awayGoals)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (fixture is null) throw new ArgumentNullException(nameof(fixture));
        if (!ContainsFixture(state, fixture))
            throw new ArgumentException("FIXTURE IS NOT PART OF THIS COMPETITION", nameof(fixture));
        if (fixture.Played) return;   // ignore double-record

        fixture.HomeGoals = Math.Max(0, homeGoals);
        fixture.AwayGoals = Math.Max(0, awayGoals);
        fixture.Played = true;

        // Knockout ties never end level — resolve on simulated penalties.
        if (IsKnockoutStage(fixture.Stage) && fixture.HomeGoals == fixture.AwayGoals)
        {
            fixture.OnPenalties = true;
            fixture.PenaltyWinner = SimulateShootout(state, fixture);
        }

        if (RoundIsComplete(state, fixture.Round))
            OnRoundComplete(state, fixture.Round);

        AdvanceCurrentRound(state);

        if (state.Kind == CompetitionKind.Career)
            MaybeCloseCareerSeason(state);
    }

    /// Simulates and records every unplayed current-round fixture that does
    /// not involve the player.
    public static void SimulateAiRound(CompetitionState state)
    {
        int round = state.CurrentRound;
        // RecordResult may append next-round fixtures; those have a higher
        // Round so the index loop with a round filter stays correct.
        for (int i = 0; i < state.Fixtures.Count; i++)
        {
            var f = state.Fixtures[i];
            if (f.Round != round || f.Played) continue;
            if (Involves(f, state.PlayerTeam)) continue;
            var (h, a) = SimulateResult(state, f);
            RecordResult(state, f, h, a);
        }
    }

    /// Simulates fixtures in order until the next one involves the player or
    /// the competition is finished.
    public static void FastForwardAiOnly(CompetitionState state)
    {
        while (!state.Finished)
        {
            var f = NextFixture(state);
            if (f is null) break;
            if (Involves(f, state.PlayerTeam)) break;
            var (h, a) = SimulateResult(state, f);
            RecordResult(state, f, h, a);
        }
    }

    /// Strength-weighted random score: small Poisson (Knuth) around a base
    /// expectation with home advantage. Deterministic via RngState.
    public static (int Home, int Away) SimulateResult(CompetitionState state, Fixture fixture)
    {
        double strH = state.Teams[fixture.HomeTeam].Strength;
        double strA = state.Teams[fixture.AwayTeam].Strength;
        double baseH = Math.Clamp(1.30 + 0.22 * (strH - strA), 0.15, 4.5);
        double baseA = Math.Clamp(1.05 + 0.22 * (strA - strH), 0.15, 4.5);
        return (Poisson(state, baseH), Poisson(state, baseA));
    }

    // ------------------------------------------------------------------
    // Career season rollover
    // ------------------------------------------------------------------

    /// True when a career season has fully concluded (league + cup) and the
    /// next season has not been built yet.
    public static bool PendingSeasonRollover(CompetitionState s)
        => s.Kind == CompetitionKind.Career && s.Career is not null && s.Finished;

    /// Builds the next career season. The caller decides promotion/relegation
    /// and supplies the new league and cup entrant lists; the player's club is
    /// located in newLeagueTeams by GlobalId (name as fallback).
    public static void AdvanceCareerSeason(
        CompetitionState s,
        System.Collections.Generic.List<TeamRef> newLeagueTeams,
        System.Collections.Generic.List<TeamRef> newCupTeams,
        int newDivision)
    {
        if (s.Kind != CompetitionKind.Career || s.Career is null)
            throw new InvalidOperationException("NOT A CAREER COMPETITION");
        if (newLeagueTeams is null || newLeagueTeams.Count < 2)
            throw new ArgumentException("NEW LEAGUE NEEDS AT LEAST 2 TEAMS", nameof(newLeagueTeams));

        var c = s.Career;
        int idx = -1;
        if (c.ClubGlobalId != 0)
            for (int i = 0; i < newLeagueTeams.Count; i++)
                if (newLeagueTeams[i].GlobalId == c.ClubGlobalId) { idx = i; break; }
        if (idx < 0)
            for (int i = 0; i < newLeagueTeams.Count; i++)
                if (string.Equals(newLeagueTeams[i].Name, c.ClubName, StringComparison.OrdinalIgnoreCase))
                { idx = i; break; }
        if (idx < 0)
            throw new ArgumentException("PLAYER CLUB NOT FOUND IN NEW LEAGUE TEAMS", nameof(newLeagueTeams));

        if (c.World is not null)
        {
            OpenSwos.Competition.Career.SeasonProgression.AgeAndRetire(c.World);
            OpenSwos.Competition.Career.RegenModel.RunRegen(c.World);
            OpenSwos.Competition.Career.StaffModel.RunClubStaffAI(c.World);
            OpenSwos.Competition.Career.Scouting.RunScoutingAI(c.World);
            OpenSwos.Competition.Career.GrowthModel.ApplySeasonGrowth(c.World);
            OpenSwos.Competition.Career.Finance.ApplySeasonFinances(c.World);
            // Transfer market resets each season: offers/list cleared, negotiation
            // budget refilled to 6, sell/buy quotas zeroed (swos.asm:127226).
            OpenSwos.Competition.Career.TransferOffers.ResetForNewSeason(c);
        }

        c.Season++;
        if (c.World is not null)
            c.World.Season = c.Season;
        c.Division = newDivision;
        c.ClubName = newLeagueTeams[idx].Name;
        c.ClubGlobalId = newLeagueTeams[idx].GlobalId;
        BuildCareerSeason(s, newLeagueTeams, newCupTeams, idx);
    }

    // ------------------------------------------------------------------
    // Standings / status queries
    // ------------------------------------------------------------------

    /// League/group standings over played fixtures whose Stage starts with
    /// stagePrefix. Every participating team of the stage gets a row, even
    /// with zero games played. Sort: Pts desc, GD desc, GF desc, Name asc.
    public static List<TableRow> Table(CompetitionState state, string stagePrefix)
    {
        var rows = new Dictionary<int, TableRow>();
        TableRow Row(int team)
        {
            if (!rows.TryGetValue(team, out var row))
            {
                row = new TableRow { Team = team };
                rows[team] = row;
            }
            return row;
        }

        // Group prefixes ("GROUP B") also seed rows from the draw so teams
        // with no fixture yet still appear.
        if (stagePrefix.Length == 7 && stagePrefix.StartsWith("GROUP ", StringComparison.Ordinal))
        {
            int g = stagePrefix[6] - 'A';
            for (int i = 0; i < state.GroupOf.Count && i < state.Teams.Count; i++)
                if (state.GroupOf[i] == g) Row(i);
        }

        foreach (var f in state.Fixtures)
        {
            if (!f.Stage.StartsWith(stagePrefix, StringComparison.Ordinal)) continue;
            var home = Row(f.HomeTeam);
            var away = Row(f.AwayTeam);
            if (!f.Played) continue;

            home.Played++; away.Played++;
            home.GoalsFor += f.HomeGoals; home.GoalsAgainst += f.AwayGoals;
            away.GoalsFor += f.AwayGoals; away.GoalsAgainst += f.HomeGoals;
            if (f.HomeGoals > f.AwayGoals) { home.Won++; away.Lost++; home.Points += 3; }
            else if (f.HomeGoals < f.AwayGoals) { away.Won++; home.Lost++; away.Points += 3; }
            else { home.Drawn++; away.Drawn++; home.Points++; away.Points++; }
        }

        var list = new List<TableRow>(rows.Values);
        list.Sort((x, y) =>
        {
            int cmp = y.Points.CompareTo(x.Points);
            if (cmp != 0) return cmp;
            cmp = y.GoalDiff.CompareTo(x.GoalDiff);
            if (cmp != 0) return cmp;
            cmp = y.GoalsFor.CompareTo(x.GoalsFor);
            if (cmp != 0) return cmp;
            cmp = string.CompareOrdinal(state.Teams[x.Team].Name, state.Teams[y.Team].Name);
            if (cmp != 0) return cmp;
            return x.Team.CompareTo(y.Team);   // total order -> deterministic sort
        });
        return list;
    }

    /// Label of the next unplayed fixture's stage with league progress, e.g.
    /// "LEAGUE - ROUND 7/22", "CUP QUARTER FINAL", "GROUP B", "FINAL".
    public static string RoundLabel(CompetitionState state)
    {
        var f = NextFixture(state);
        if (f is null) return state.Finished
            ? Loc.Tr("comp.competition_complete", "COMPETITION COMPLETE")
            : Loc.Tr("comp.no_fixtures", "NO FIXTURES");
        if (f.Stage == "LEAGUE")
        {
            List<int> leagueRounds = DistinctStageRounds(state, "LEAGUE");
            int idx = leagueRounds.IndexOf(f.Round) + 1;
            return string.Format(
                Loc.Tr("comp.league_round", "LEAGUE - ROUND {0}/{1}"), idx, leagueRounds.Count);
        }
        return CompLoc.TrStage(f.Stage);
    }

    /// True while the player still has a current-or-future fixture: an
    /// unplayed fixture now, a live group-stage campaign, or a won knockout
    /// tie whose next round has not been drawn yet.
    public static bool IsPlayerAlive(CompetitionState state)
    {
        int p = state.PlayerTeam;
        if (p < 0 || p >= state.Teams.Count) return false;
        if (state.Finished) return false;

        foreach (var f in state.Fixtures)
            if (!f.Played && Involves(f, p)) return true;

        // Tournament group stage still running: qualification is open until
        // every group game is played (at which point the knockout exists).
        if (state.Kind == CompetitionKind.Tournament && !AllGroupFixturesPlayed(state))
        {
            bool groupDone = true;
            int g = (p < state.GroupOf.Count) ? state.GroupOf[p] : -1;
            foreach (var f in state.Fixtures)
                if (!f.Played && f.Stage.StartsWith("GROUP", StringComparison.Ordinal)
                    && (Involves(f, p) || (g >= 0 && f.Stage.Length == 7 && f.Stage[6] - 'A' == g)))
                    groupDone = false;
            if (!groupDone) return true;
            // Player's group finished early: alive iff currently in the top 2.
            if (g >= 0)
            {
                var table = Table(state, "GROUP " + (char)('A' + g));
                for (int i = 0; i < table.Count && i < 2; i++)
                    if (table[i].Team == p) return true;
                return false;
            }
            return true;
        }

        // Knockout progression pending: alive if the player won the latest
        // knockout tie they played (the next round is drawn on completion).
        Fixture? last = null;
        foreach (var f in state.Fixtures)
            if (f.Played && IsKnockoutStage(f.Stage) && Involves(f, p)
                && (last is null || f.Round > last.Round))
                last = f;
        return last is not null && WinnerOf(last) == p;
    }

    /// One short status line for the player, uppercase ASCII.
    public static string PlayerSummary(CompetitionState state)
    {
        int p = state.PlayerTeam;
        if (p < 0 || p >= state.Teams.Count) return Loc.Tr("comp.no_player_team", "NO PLAYER TEAM");
        if (state.Finished && state.Champion == p) return Loc.Tr("comp.you_champion", "YOU ARE THE CHAMPION");

        if (state.Kind == CompetitionKind.League || state.Kind == CompetitionKind.Career)
        {
            var table = Table(state, "LEAGUE");
            for (int i = 0; i < table.Count; i++)
                if (table[i].Team == p)
                    return string.Format(Loc.Tr("comp.you_are_pos", "YOU ARE {0}"), CompLoc.Ordinal(i + 1));
            return Loc.Tr("comp.you_unplaced", "YOU ARE UNPLACED");
        }

        // Cup / tournament.
        if (!IsPlayerAlive(state)) return Loc.Tr("comp.you_eliminated", "YOU WERE ELIMINATED");
        var next = NextPlayerFixture(state);
        if (next is null) return Loc.Tr("comp.you_through", "YOU ARE THROUGH TO THE NEXT ROUND");
        if (next.Stage.StartsWith("GROUP", StringComparison.Ordinal))
            return string.Format(Loc.Tr("comp.you_in", "YOU ARE IN {0}"), CompLoc.TrStage(next.Stage));
        return string.Format(Loc.Tr("comp.you_in_the", "YOU ARE IN THE {0}"), CompLoc.TrStage(next.Stage));
    }

    // ==================================================================
    // Internals
    // ==================================================================

    private const int GroupStageRounds = 3;   // single round-robin of a 4-team group

    // --- deterministic RNG (xorshift32 over CompetitionState.RngState) ---

    private static uint SeedToRng(int seed)
    {
        uint v = unchecked((uint)seed);
        return v == 0 ? 0x9E3779B9u : v;
    }

    private static uint NextRng(CompetitionState s)
    {
        uint x = s.RngState;
        if (x == 0) x = 0x9E3779B9u;   // xorshift32 must never sit on 0
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        s.RngState = x;
        return x;
    }

    private static int NextInt(CompetitionState s, int maxExclusive)
        => maxExclusive <= 1 ? 0 : (int)(NextRng(s) % (uint)maxExclusive);

    private static double NextDouble(CompetitionState s)
        => (NextRng(s) >> 8) * (1.0 / 16777216.0);   // 24-bit mantissa in [0,1)

    private static void Shuffle(CompetitionState s, List<int> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = NextInt(s, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static int Poisson(CompetitionState s, double lambda)
    {
        // Knuth: multiply uniforms until below e^-lambda.
        double limit = Math.Exp(-lambda);
        int k = 0;
        double p = 1.0;
        do { k++; p *= NextDouble(s); } while (p > limit && k < 16);
        return k - 1;
    }

    // --- construction helpers ---

    private static CompetitionState NewState(
        CompetitionKind kind, string name, List<TeamRef> teams, int playerTeam, int seed)
    {
        if (teams is null || teams.Count < 2)
            throw new ArgumentException("AT LEAST 2 TEAMS REQUIRED", nameof(teams));
        if (playerTeam < -1 || playerTeam >= teams.Count)
            throw new ArgumentOutOfRangeException(nameof(playerTeam));
        var s = new CompetitionState
        {
            Kind = kind,
            Name = name ?? "",
            Teams = new List<TeamRef>(teams),
            PlayerTeam = playerTeam,
            RngState = SeedToRng(seed),
        };
        for (int i = 0; i < s.Teams.Count; i++) s.GroupOf.Add(-1);
        return s;
    }

    /// Berger/circle round-robin over local indices 0..teamCount-1. Odd counts
    /// get a padded bye slot whose pairings are skipped, so every real team
    /// still meets every other exactly once across teamCount rounds.
    private static List<List<(int Home, int Away)>> RoundRobin(int teamCount)
    {
        int n = (teamCount % 2 == 0) ? teamCount : teamCount + 1;
        var slots = new int[n];
        for (int i = 0; i < n; i++) slots[i] = i;

        var rounds = new List<List<(int, int)>>(n - 1);
        for (int r = 0; r < n - 1; r++)
        {
            var pairs = new List<(int, int)>(n / 2);
            for (int i = 0; i < n / 2; i++)
            {
                int a = slots[i], b = slots[n - 1 - i];
                if (a >= teamCount || b >= teamCount) continue;   // bye
                if ((r + i) % 2 == 1) (a, b) = (b, a);            // rough home/away balance
                pairs.Add((a, b));
            }
            rounds.Add(pairs);
            // Rotate every slot except the fixed slots[0] one step clockwise.
            int lastSlot = slots[n - 1];
            for (int i = n - 1; i >= 2; i--) slots[i] = slots[i - 1];
            slots[1] = lastSlot;
        }
        return rounds;
    }

    /// (Re)builds one career season into s: union team list, double
    /// round-robin league, cup round 1 draw, interleaved global rounds.
    private static void BuildCareerSeason(
        CompetitionState s, List<TeamRef> leagueTeams, List<TeamRef> cupTeams, int playerLeagueIndex)
    {
        int cupCount = cupTeams?.Count ?? 0;
        if (cupCount != 4 && cupCount != 8 && cupCount != 16 && cupCount != 32)
            throw new ArgumentException("CAREER CUP NEEDS 4, 8, 16 OR 32 TEAMS", nameof(cupTeams));

        int playerMaster = leagueTeams[playerLeagueIndex].MasterIndex;
        bool playerInCup = false;
        foreach (var t in cupTeams!)
            if (t.MasterIndex == playerMaster) { playerInCup = true; break; }
        if (!playerInCup)
            throw new ArgumentException("CUP TEAMS MUST INCLUDE THE PLAYER CLUB", nameof(cupTeams));

        // Union of league + cup entrants, deduped by MasterIndex. League teams
        // come first so league fixtures use indices 0..leagueTeams.Count-1.
        s.Teams = new List<TeamRef>();
        var byMaster = new Dictionary<int, int>();
        foreach (var t in leagueTeams)
            if (!byMaster.ContainsKey(t.MasterIndex))
            {
                byMaster[t.MasterIndex] = s.Teams.Count;
                s.Teams.Add(t);
            }
        foreach (var t in cupTeams)
            if (!byMaster.ContainsKey(t.MasterIndex))
            {
                byMaster[t.MasterIndex] = s.Teams.Count;
                s.Teams.Add(t);
            }

        s.PlayerTeam = byMaster[playerMaster];
        s.GroupOf = new List<int>();
        for (int i = 0; i < s.Teams.Count; i++) s.GroupOf.Add(-1);
        s.GroupCount = 0;
        s.DoubleRoundRobin = true;
        s.Fixtures = new List<Fixture>();
        s.CurrentRound = 0;
        s.Finished = false;
        s.Champion = -1;

        // League: double round-robin over league-local indices (== union
        // indices because league teams were added first).
        List<List<(int Home, int Away)>> rounds = RoundRobin(leagueTeams.Count);
        int half = rounds.Count;
        int leagueRounds = half * 2;
        int cupRounds = Log2(cupCount);

        int LeagueGlobal(int j)
        {
            int offset = 0;
            for (int k = 0; k < cupRounds; k++)
                if (CareerCupCutoff(leagueRounds, cupRounds, k) <= j) offset++;
            return j + offset;
        }

        int Union(int leagueLocal) => byMaster[leagueTeams[leagueLocal].MasterIndex];

        for (int r = 0; r < half; r++)
            foreach (var (h, a) in rounds[r])
                s.Fixtures.Add(new Fixture
                { Round = LeagueGlobal(r), Stage = "LEAGUE", HomeTeam = Union(h), AwayTeam = Union(a) });
        for (int r = 0; r < half; r++)
            foreach (var (h, a) in rounds[r])
                s.Fixtures.Add(new Fixture
                { Round = LeagueGlobal(half + r), Stage = "LEAGUE", HomeTeam = Union(a), AwayTeam = Union(h) });

        // Cup round 1: random draw. Later rounds are created by RecordResult.
        var entrants = new List<int>(cupCount);
        foreach (var t in cupTeams) entrants.Add(byMaster[t.MasterIndex]);
        Shuffle(s, entrants);
        string stage = "CUP " + KnockoutStageLabel(cupCount);
        int cupRound0 = CareerCupCutoff(leagueRounds, cupRounds, 0);
        for (int i = 0; i + 1 < entrants.Count; i += 2)
            s.Fixtures.Add(new Fixture
            { Round = cupRound0, Stage = stage, HomeTeam = entrants[i], AwayTeam = entrants[i + 1] });

        s.TotalRounds = leagueRounds + cupRounds;
    }

    /// How many league rounds precede career cup round k. Cup rounds are
    /// spread evenly across the season (last one after the final league
    /// round), so with 2 cup rounds they land at the halfway point and the
    /// end; with 4 they land after every quarter. Global round of cup round k
    /// is cutoff + k (earlier cup rounds shift the calendar).
    private static int CareerCupCutoff(int leagueRounds, int cupRounds, int k)
    {
        int cutoff = (int)Math.Round((k + 1) * (double)leagueRounds / cupRounds, MidpointRounding.AwayFromZero);
        return Math.Clamp(cutoff, 1, leagueRounds);
    }

    // --- stage / fixture predicates ---

    private static bool Involves(Fixture f, int team) => f.HomeTeam == team || f.AwayTeam == team;

    private static bool IsKnockoutStage(string stage)
        => stage != "LEAGUE" && !stage.StartsWith("GROUP", StringComparison.Ordinal);

    private static string KnockoutStageLabel(int teamsRemaining) => teamsRemaining switch
    {
        2 => "FINAL",
        4 => "SEMI FINAL",
        8 => "QUARTER FINAL",
        16 => "ROUND OF 16",
        32 => "ROUND OF 32",
        _ => "ROUND OF " + teamsRemaining,
    };

    private static int WinnerOf(Fixture f)
    {
        if (f.HomeGoals > f.AwayGoals) return f.HomeTeam;
        if (f.AwayGoals > f.HomeGoals) return f.AwayTeam;
        return f.PenaltyWinner >= 0 ? f.PenaltyWinner : f.HomeTeam;
    }

    private static bool ContainsFixture(CompetitionState s, Fixture f)
    {
        foreach (var x in s.Fixtures) if (ReferenceEquals(x, f)) return true;
        return false;
    }

    private static bool RoundIsComplete(CompetitionState s, int round)
    {
        bool any = false;
        foreach (var f in s.Fixtures)
            if (f.Round == round)
            {
                any = true;
                if (!f.Played) return false;
            }
        return any;
    }

    private static bool AllFixturesPlayed(CompetitionState s)
    {
        foreach (var f in s.Fixtures) if (!f.Played) return false;
        return true;
    }

    private static bool AllGroupFixturesPlayed(CompetitionState s)
    {
        foreach (var f in s.Fixtures)
            if (f.Stage.StartsWith("GROUP", StringComparison.Ordinal) && !f.Played) return false;
        return true;
    }

    private static bool HasKnockoutFixtures(CompetitionState s)
    {
        foreach (var f in s.Fixtures) if (IsKnockoutStage(f.Stage)) return true;
        return false;
    }

    private static List<int> DistinctStageRounds(CompetitionState s, string stagePrefix)
    {
        var set = new SortedSet<int>();
        foreach (var f in s.Fixtures)
            if (f.Stage.StartsWith(stagePrefix, StringComparison.Ordinal)) set.Add(f.Round);
        return new List<int>(set);
    }

    // --- progression ---

    private static void AdvanceCurrentRound(CompetitionState s)
    {
        while (s.CurrentRound < s.TotalRounds)
        {
            bool any = false;
            foreach (var f in s.Fixtures)
                if (f.Round == s.CurrentRound)
                {
                    any = true;
                    if (!f.Played) return;
                }
            if (!any) return;   // round not created yet (pending knockout draw)
            s.CurrentRound++;
        }
    }

    private static void OnRoundComplete(CompetitionState s, int round)
    {
        var roundFixtures = new List<Fixture>();
        foreach (var f in s.Fixtures) if (f.Round == round) roundFixtures.Add(f);
        if (roundFixtures.Count == 0) return;
        string stage = roundFixtures[0].Stage;

        switch (s.Kind)
        {
            case CompetitionKind.League:
                if (AllFixturesPlayed(s))
                {
                    s.Finished = true;
                    var table = Table(s, "LEAGUE");
                    s.Champion = table.Count > 0 ? table[0].Team : -1;
                }
                break;

            case CompetitionKind.Cup:
                AdvanceKnockout(s, roundFixtures, "", round + 1, crownChampion: true);
                break;

            case CompetitionKind.Tournament:
                if (stage.StartsWith("GROUP", StringComparison.Ordinal))
                {
                    if (AllGroupFixturesPlayed(s) && !HasKnockoutFixtures(s))
                        CreateTournamentKnockout(s);
                }
                else
                {
                    AdvanceKnockout(s, roundFixtures, "", round + 1, crownChampion: true);
                }
                break;

            case CompetitionKind.Career:
                // League completion is handled by MaybeCloseCareerSeason.
                if (stage.StartsWith("CUP ", StringComparison.Ordinal))
                    AdvanceCareerCup(s, roundFixtures);
                break;
        }
    }

    /// Pairs the winners of a completed knockout round into the next round
    /// (bracket order: winner of fixture 0 hosts winner of fixture 1, ...).
    /// A single winner means the final was just played.
    private static void AdvanceKnockout(
        CompetitionState s, List<Fixture> completed, string stagePrefix, int nextRound, bool crownChampion)
    {
        var winners = new List<int>(completed.Count);
        foreach (var f in completed) winners.Add(WinnerOf(f));

        if (winners.Count == 1)
        {
            if (crownChampion)
            {
                s.Finished = true;
                s.Champion = winners[0];
            }
            return;
        }

        string stage = stagePrefix + KnockoutStageLabel(winners.Count);
        for (int i = 0; i + 1 < winners.Count; i += 2)
            s.Fixtures.Add(new Fixture
            { Round = nextRound, Stage = stage, HomeTeam = winners[i], AwayTeam = winners[i + 1] });
    }

    /// Group stage done: top 2 of each group advance, cross-paired so group
    /// mates can only re-meet in the final (A1-B2, C1-D2, ..., B1-A2, D1-C2).
    private static void CreateTournamentKnockout(CompetitionState s)
    {
        int g = s.GroupCount;
        var winners = new int[g];
        var runners = new int[g];
        for (int i = 0; i < g; i++)
        {
            var table = Table(s, "GROUP " + (char)('A' + i));
            winners[i] = table[0].Team;
            runners[i] = table[1].Team;
        }

        string stage = KnockoutStageLabel(g * 2);
        for (int i = 0; i + 1 < g; i += 2)
            s.Fixtures.Add(new Fixture
            { Round = GroupStageRounds, Stage = stage, HomeTeam = winners[i], AwayTeam = runners[i + 1] });
        for (int i = 0; i + 1 < g; i += 2)
            s.Fixtures.Add(new Fixture
            { Round = GroupStageRounds, Stage = stage, HomeTeam = winners[i + 1], AwayTeam = runners[i] });
    }

    /// Career cup round completed: draw the next round at its scheduled slot
    /// in the interleaved calendar. The cup final's completion is picked up
    /// by MaybeCloseCareerSeason instead of crowning Champion here.
    private static void AdvanceCareerCup(CompetitionState s, List<Fixture> completed)
    {
        var winners = new List<int>(completed.Count);
        foreach (var f in completed) winners.Add(WinnerOf(f));
        if (winners.Count == 1) return;   // cup final done

        // Reconstruct the season's calendar parameters from the fixtures.
        int leagueRounds = DistinctStageRounds(s, "LEAGUE").Count;
        int firstCupRound = int.MaxValue;
        foreach (var f in s.Fixtures)
            if (f.Stage.StartsWith("CUP ", StringComparison.Ordinal) && f.Round < firstCupRound)
                firstCupRound = f.Round;
        int firstCupFixtures = 0;
        foreach (var f in s.Fixtures)
            if (f.Round == firstCupRound && f.Stage.StartsWith("CUP ", StringComparison.Ordinal))
                firstCupFixtures++;
        int cupRounds = Log2(firstCupFixtures * 2);
        int created = DistinctStageRounds(s, "CUP ").Count;   // includes the round just completed

        int nextGlobal = CareerCupCutoff(leagueRounds, cupRounds, created) + created;
        string stage = "CUP " + KnockoutStageLabel(winners.Count);
        for (int i = 0; i + 1 < winners.Count; i += 2)
            s.Fixtures.Add(new Fixture
            { Round = nextGlobal, Stage = stage, HomeTeam = winners[i], AwayTeam = winners[i + 1] });
    }

    /// When both the league and the cup of a career season have concluded:
    /// Finished + Champion (league leader), one history line, trophies.
    private static void MaybeCloseCareerSeason(CompetitionState s)
    {
        if (s.Finished || s.Career is null) return;

        foreach (var f in s.Fixtures)
            if (f.Stage == "LEAGUE" && !f.Played) return;   // league still running
        Fixture? final = null;
        foreach (var f in s.Fixtures)
            if (f.Stage == "CUP FINAL" && f.Played) final = f;
        if (final is null) return;                          // cup still running

        s.Finished = true;
        var table = Table(s, "LEAGUE");
        s.Champion = table.Count > 0 ? table[0].Team : -1;

        int pos = 0;
        for (int i = 0; i < table.Count; i++)
            if (table[i].Team == s.PlayerTeam) { pos = i + 1; break; }

        int cupWinner = WinnerOf(final);
        string cupPart;
        if (cupWinner == s.PlayerTeam) cupPart = "WINNER";
        else if (Involves(final, s.PlayerTeam)) cupPart = "RUNNER UP";
        else
        {
            Fixture? exit = null;
            foreach (var f in s.Fixtures)
                if (f.Played && f.Stage.StartsWith("CUP ", StringComparison.Ordinal)
                    && Involves(f, s.PlayerTeam) && WinnerOf(f) != s.PlayerTeam
                    && (exit is null || f.Round > exit.Round))
                    exit = f;
            cupPart = exit is not null ? "OUT IN " + exit.Stage.Substring(4) : "OUT";
        }

        var c = s.Career;
        if (s.Champion == s.PlayerTeam) c.Trophies.Add($"SEASON {c.Season} LEAGUE CHAMPIONS");
        if (cupWinner == s.PlayerTeam) c.Trophies.Add($"SEASON {c.Season} CUP WINNERS");
        string posText = pos > 0 ? Ordinal(pos) : "N/A";
        c.History.Add($"S{c.Season}: LEAGUE {posText}, CUP {cupPart}");
    }

    /// Deterministic penalty shootout: best of 5, then sudden death. Slightly
    /// strength-weighted per-kick conversion. Returns the winning Teams index.
    private static int SimulateShootout(CompetitionState s, Fixture f)
    {
        double strH = s.Teams[f.HomeTeam].Strength;
        double strA = s.Teams[f.AwayTeam].Strength;
        double pH = Math.Clamp(0.76 + 0.02 * (strH - strA), 0.55, 0.92);
        double pA = Math.Clamp(0.76 + 0.02 * (strA - strH), 0.55, 0.92);

        int h = 0, a = 0;
        for (int kick = 0; kick < 5; kick++)
        {
            if (NextDouble(s) < pH) h++;
            if (NextDouble(s) < pA) a++;
        }
        while (h == a)   // sudden death, one kick each
        {
            if (NextDouble(s) < pH) h++;
            if (NextDouble(s) < pA) a++;
        }
        return h > a ? f.HomeTeam : f.AwayTeam;
    }

    // --- misc ---

    private static int Log2(int n)
    {
        int r = 0;
        while (n > 1) { n >>= 1; r++; }
        return r;
    }

    private static string Ordinal(int n)
    {
        int rem100 = n % 100;
        string suffix = (rem100 >= 11 && rem100 <= 13)
            ? "TH"
            : (n % 10) switch { 1 => "ST", 2 => "ND", 3 => "RD", _ => "TH" };
        return n + suffix;
    }
}
