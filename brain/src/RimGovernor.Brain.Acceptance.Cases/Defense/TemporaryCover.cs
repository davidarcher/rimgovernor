using System.Globalization;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;
using Obs = RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>defense/temporary-cover</c> (task 7.4 of redefine-habitat-requirements): a walled hut with one door, one colonist holding
/// an assault rifle, no standing defense, and one hostile gunner that walks in from the map edge so the native raid read
/// records one ground raid track (its edge and its weapon range). Under the served defense families' normal review, admission
/// and Hands:
/// <list type="number">
/// <item>the comparison binds the sandbag TemporaryCover (the layout/defense Need works a
/// <c>defense-choice-temporary_cover-&lt;digest&gt;</c> Method) and no killbox piece is placed: every blueprint and frame on the
/// map is a Sandbags;</item>
/// <item>the Need stays open while its sandbags are only ordered: its progress row stands as long as no Sandbags stands;</item>
/// <item>it closes only once the native building read observes the sandbags built.</item>
/// </list>
/// The comparisons are DefenseChoiceTests and DefenseBindingTests; only the game proves the native raid track, the probe of
/// the standing cover and the closing facts. Registered on demand: the repository gate registers it and never executes it.
/// </summary>
internal static class DefenseTemporaryCover
{
    private const string Rifle = "Gun_AssaultRifle";
    private const string Shooter = "shooter";
    private const string Raider = "raider";
    private const string Sandbags = DefenseCoverStock.DefenseSandbags;

    // A 16x16 Home site: a 5x5 wooden hut with a south door in the middle, open ground around it for the cover lines.
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        World = new Fx.StageWorld { Hour = 10 },
        Site = new Fx.StageSite
        {
            Width = 16,
            Height = 16,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 4,
            Radius = 40,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
        },
        Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 16, 16), Home = true }],
        Rooms = [new Fx.StageRoom { Name = "hut", Rect = new Fx.StageRect(5, 6, 5, 5), Stuff = "WoodLog", Doors = [new(7, 6)], Roof = true }],
        Things = [new Fx.StageThing { Name = "steel", Def = "Steel", Count = 300, Cell = new(14, 14), Near = true }],
        Pawns =
        [
            new Fx.StagePawn
            {
                Name = Shooter,
                Colonist = 0,
                Cell = new(7, 8),
                AllWork = 3,
                Timetable = Fx.StageTimeAssignment.Work,
                Gear = new Fx.StageGear { Weapon = Rifle },
            },
            new Fx.StagePawn
            {
                Name = Raider,
                Kind = "Mercenary_Gunner",
                Faction = Fx.StageFaction.Hostile,
                Age = 30,
                ClearTraits = true,
                Cell = new(15, 0),
                Gear = new Fx.StageGear { Weapon = Rifle, Strip = true },
            },
        ],
        Raid = new Fx.StageRaid { Arrival = "EdgeWalkIn" },
    };

    internal static TypedCase Case() => new()
    {
        Name = "defense/temporary-cover",
        Scope = "Task 7.4: a wooden hut with one door, one colonist with an assault rifle, no standing defense and one hostile gunner "
            + "walking in from the map edge (one ground raid track). Under the served defense families the layout/defense Need binds "
            + "the sandbag TemporaryCover (a defense-choice-temporary_cover- Method), every blueprint or frame placed is a Sandbags (no "
            + "killbox piece), the Need's progress row stands while no Sandbags stands, and it closes only once "
            + "observations_list_buildings reads the sandbags built.",
        Ticks = 30_000, // a hang guard (the start's cap): the fight, then a few sandbags built by one colonist
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Crew = new Crew { Size = 1 },
        Stage = Spec,
        Ops = [FixtureStage.Tool],
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["defense", "defensive-layout"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "temporary-cover" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        _ = s.Staged ?? throw new AcceptanceException("defense/temporary-cover stages its colony through Stage");
        var prefix = DefenseBindingRules.ChoicePrefix + RimGovernor.Brain.Domain.Wire.Name(DefenseCandidateKind.TemporaryCover) + "-";
        var methods = new SortedSet<string>(StringComparer.Ordinal);
        int seen = -1, selected = -1, closed = -1, round = 0, standing = 0;
        _ = await s.ServeUntilAsync("temporary-cover", s.TicksLeft, async ct =>
        {
            round++;
            // The review first, then the facts: facts only improve, so a row missing before the sandbags read built is a premature close.
            var review = await s.ReviewAsync(ct).ConfigureAwait(false);
            var row = review?.Progress.FirstOrDefault(p => p.Concern == LayoutFamily.Defense.FilingKey);
            var built = await s.BuildingsAsync("sandbags", new RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest { Statuses = [Obs.BuildingStatus.Built] }, ct).ConfigureAwait(false);
            standing = built.Count(b => b.Building.DefName == Sandbags);
            var construction = await s.BuildingsAsync("construction", new RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest { Statuses = [Obs.BuildingStatus.Blueprint, Obs.BuildingStatus.Frame] }, ct).ConfigureAwait(false);
            if (construction.FirstOrDefault(c => c.Building.DefName != Sandbags) is { } other)
            {
                throw new AcceptanceException($"round {round}: a piece other than sandbags was placed ({other.Building.DefName}:{other.Status}): a killbox or another alternative was admitted");
            }
            if (row is not null)
            {
                if (seen < 0) seen = round;
                if (row.Method.Length > 0) methods.Add(row.Method);
                if (row.Method.StartsWith(prefix, StringComparison.Ordinal) && selected < 0) selected = round;
                closed = -1;
            }
            else if (seen >= 0 && closed < 0)
            {
                if (standing == 0)
                {
                    throw new AcceptanceException($"round {round}: layout/defense closed before any sandbags stood (methods {string.Join(",", methods)})");
                }
                closed = round;
            }
            return (Wait.Signature(standing, construction.Count, row?.Method ?? "", seen, selected, closed), closed >= 0 && standing > 0);
        }, cancellationToken).ConfigureAwait(false);

        foreach (var t in await s.NeedTransitionsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (t.Need == LayoutFamily.Defense.Value && t.Method.Length > 0) methods.Add(t.Method);
        }
        s.Record("defense", string.Create(CultureInfo.InvariantCulture, $"seen={seen} selected={selected} closed={closed} sandbags={standing} methods={string.Join(",", methods)}"));
        if (seen < 0)
        {
            throw new AcceptanceException("layout/defense never stood unmet in a persisted review: the deficit was never derived");
        }
        if (!methods.Any(m => m.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new AcceptanceException($"layout/defense never worked the temporary cover ({string.Join(",", methods)})");
        }
        if (closed < 0 || standing == 0)
        {
            throw new AcceptanceException($"layout/defense never closed on standing sandbags (closed={closed}, sandbags={standing})");
        }
    }
}
