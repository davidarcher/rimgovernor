using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>redevelopment/replace-before-remove</c> (redefine-habitat-requirements task 8.4, the narrow autonomous
/// replacement-before-removal case). Registered and compiled only: no automated run executes it (it is OnDemand, run by
/// hand against a live game), so it is evidence only once a run of it is recorded.
/// <para>
/// Fresh animal corpses wait, no butcher bench stands, food is short and wood is in stock. Under the served bill and work
/// families' normal review the layout comparison adopts a spot-now/table-later chain, and the observed sequence is the
/// standing-building transition's normal path:
/// </para>
/// <list type="number">
/// <item>comparison: the plan holds the <see cref="Rules.ButcherSpotDefinition"/> Bridge and the
/// <see cref="KitchenSeparationRules.ButcherTableDefinition"/> Host;</item>
/// <item>transition bound: once the table is the load's host, <c>LayoutPlan.Transitions</c> carries the spot's
/// <see cref="TransitionCause.HostRetirement"/> transition with its replacement dependency;</item>
/// <item>replacement built and observed serving: the table stands built with a butcher bill, usable and reachable;</item>
/// <item>the old piece's removal admitted and deconstructed: the spot is gone;</item>
/// <item>target gone observed, transition settled: the plan no longer carries the spot's transition.</item>
/// </list>
/// Negative, in the same run: no Deconstruct designation stands on the spot while the table is not serving. Nothing selects
/// the winner but the comparison: a run whose comparison adopts no chain fails naming the plan it adopted.
/// </summary>
internal static class ReplaceBeforeRemove
{
    private const int Colonists = 2;
    private const string Spot = Rules.ButcherSpotDefinition;
    private const string Table = KitchenSeparationRules.ButcherTableDefinition;
    private const string Prey = "Muffalo";

    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        World = new Fx.StageWorld { Hour = 7 },
        Site = new Fx.StageSite
        {
            Width = 12,
            Height = 8,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 12, 8), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones] }],
        Things =
        [
            new Fx.StageThing { Name = "logs", Def = "WoodLog", Count = 75, Cell = new(9, 2) },
            new Fx.StageThing { Name = "logs-2", Def = "WoodLog", Count = 75, Cell = new(9, 3) },
            new Fx.StageThing { Name = "logs-3", Def = "WoodLog", Count = 75, Cell = new(9, 4) },
        ],
        Pawns =
        [
            new Fx.StagePawn { Name = "a", Colonist = 0, Cell = new(2, 3), Timetable = Fx.StageTimeAssignment.Work },
            new Fx.StagePawn { Name = "b", Colonist = 1, Cell = new(3, 3), Timetable = Fx.StageTimeAssignment.Work },
            .. Enumerable.Range(0, 4).Select(i => new Fx.StagePawn
            {
                Name = "prey" + i.ToString(CultureInfo.InvariantCulture), Kind = Prey, Faction = Fx.StageFaction.None, Cell = new(5 + i, 6), Dead = true,
            }),
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "redevelopment/replace-before-remove",
        Scope = "Task 8.4, registered and compiled only (no automated run executes it): four fresh muffalo corpses, no butcher bench, "
            + "no meals and wood in stock; under the served bill and work families' normal review the comparison adopts a "
            + "ButcherSpot-now/TableButcher-later chain, the spot's host_retirement transition is bound on the layout plan with its "
            + "replacement dependency, the table is built and observed serving, only then is the spot deconstructed (no Deconstruct "
            + "designation stands on it while the table is not serving), and once the spot is gone the plan no longer carries its "
            + "transition, read back natively.",
        Ticks = TypedCase.StagedTickCap, // a hang guard: a spot is instant, the table a short build, and the replan hourly
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Colonists },
        Crew = new Crew { Size = Colonists },
        Stage = Spec,
        Ops = [FixtureStage.Tool],
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["bill", "work"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "replace-before-remove" },
        Run = RunAsync,
    };

    private sealed class Track
    {
        public int Round { get; set; }
        public int ChainAdopted { get; set; } = -1;
        public int SpotBuilt { get; set; } = -1;
        public int Bound { get; set; } = -1;
        public int TableServing { get; set; } = -1;
        public int SpotGone { get; set; } = -1;
        public int Settled { get; set; } = -1;
        public string Spot { get; set; } = "";
        public string Plan { get; set; } = "";
    }

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var before = await s.BuildingsAsync("butcher-before", new RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest { DefNames = [Spot, Table] }, cancellationToken).ConfigureAwait(false);
        if (before.Count > 0)
        {
            throw new AcceptanceException($"a butcher bench already stands ({string.Join(",", before.Select(b => b.Building.Id))}): the precondition hides the claim");
        }
        var site = s.Staged!.Site ?? throw new AcceptanceException("stage: no site");
        var t = new Track();
        _ = await s.ServeUntilAsync("replace-before-remove", s.TicksLeft, async ct =>
        {
            t.Round++;
            var plan = await s.LayoutPlanAsync(ct).ConfigureAwait(false);
            var made = plan?.Rooms.SelectMany(r => r.Production).ToArray() ?? [];
            t.Plan = string.Join(",", made.Where(p => p.Bench is Spot or Table).Select(p => p.Bench + ":" + p.Step));
            if (t.ChainAdopted < 0 && made.Any(p => p.Bench == Spot && p.Step == HostStep.Bridge) && made.Any(p => p.Bench == Table && p.Step == HostStep.Host))
            {
                t.ChainAdopted = t.Round;
            }

            var standing = await s.BuildingsAsync("butcher", new RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest { DefNames = [Spot, Table] }, ct).ConfigureAwait(false);
            var spot = standing.FirstOrDefault(b => b.Building.DefName == Spot && b.Status == RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built);
            var tableBuilt = standing.FirstOrDefault(b => b.Building.DefName == Table && b.Status == RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built);
            var bills = await s.CallAsync("bills", NativeOps.ReadBills, new RimGovernor.Brain.GameOrders.Observations.BillsRequest { Scope = s.Scope }, ct).ConfigureAwait(false);
            var tableRow = tableBuilt is null ? null : bills.Benches.FirstOrDefault(b => b.Bench == tableBuilt.Building.Id);
            if (t.TableServing < 0 && tableRow is { Usable: true, Reachable: true, Bills.Count: > 0 })
            {
                t.TableServing = t.Round;
            }

            if (spot is not null)
            {
                t.Spot = spot.Building.Id;
                if (t.SpotBuilt < 0) t.SpotBuilt = t.Round;
                // Negative: no Deconstruct designation on the old piece while its replacement is not serving.
                if (t.TableServing < 0 && spot.Occupied.Minimum is { X: { } x, Z: { } z })
                {
                    var grid = await TypedApply.CellsAsync(s, "spot-cell", site, ct).ConfigureAwait(false);
                    if (grid.ThingsAt(new Cell(x, z)).Any(th => th.Def == Spot && th.Has(ThingMarks.Designated)))
                    {
                        throw new AcceptanceException($"the {Spot} {t.Spot} was designated for removal before a built {Table} served (plan {t.Plan})");
                    }
                }
            }
            var transition = plan?.Transitions.FirstOrDefault(x => x.Target == t.Spot && t.Spot.Length > 0);
            if (t.Bound < 0 && transition is { Cause: TransitionCause.HostRetirement, Dependency: not null })
            {
                t.Bound = t.Round;
            }
            if (t.SpotBuilt >= 0 && spot is null && t.SpotGone < 0)
            {
                t.SpotGone = t.Round;
                if (t.TableServing < 0)
                {
                    throw new AcceptanceException($"the {Spot} {t.Spot} was removed before a built {Table} served with a butcher bill (plan {t.Plan})");
                }
            }
            if (t.SpotGone >= 0 && transition is null && t.Settled < 0)
            {
                t.Settled = t.Round;
            }
            return (Wait.Signature(t.Plan, t.ChainAdopted, t.SpotBuilt, t.Bound, t.TableServing, t.SpotGone, t.Settled), t.Settled >= 0);
        }, cancellationToken).ConfigureAwait(false);

        s.Record("plan", t.Plan);
        s.Record("rounds", string.Create(CultureInfo.InvariantCulture,
            $"chain={t.ChainAdopted} spot_built={t.SpotBuilt} bound={t.Bound} table_serving={t.TableServing} spot_gone={t.SpotGone} settled={t.Settled}"));
        if (t.ChainAdopted < 0)
        {
            throw new AcceptanceException($"the comparison adopted no {Spot}>{Table} chain (butcher production {t.Plan}): the staged draw did not make the bridge worth its labor");
        }
        if (t.SpotBuilt < 0)
        {
            throw new AcceptanceException($"the {Spot} bridge never stood built (plan {t.Plan})");
        }
        if (t.Bound < 0)
        {
            throw new AcceptanceException($"the plan never bound the {Spot} {t.Spot}'s host_retirement transition with its replacement dependency (plan {t.Plan})");
        }
        if (t.SpotGone < 0)
        {
            throw new AcceptanceException($"the {Spot} {t.Spot} was never removed after its {Table} replacement served (round {t.TableServing}, plan {t.Plan})");
        }
        if (t.Bound > t.SpotGone)
        {
            throw new AcceptanceException($"the {Spot} {t.Spot} was removed (round {t.SpotGone}) before its transition was bound (round {t.Bound})");
        }
        if (t.Settled < 0)
        {
            throw new AcceptanceException($"the {Spot} {t.Spot} is gone (round {t.SpotGone}) but the plan still carries its transition");
        }
    }
}
