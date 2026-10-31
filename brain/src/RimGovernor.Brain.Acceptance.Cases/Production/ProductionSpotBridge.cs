using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/spot-bridge</c> (RG-HABITAT-005 "A spot bridges workstation construction", redefine-habitat-requirements
/// task 3.4): fresh animal corpses wait, no butcher bench stands, food is short and wood is in stock. Under the served
/// bill and work families' normal review the shared layout comparison adopts a spot-now/table-later chain (the plan's
/// butchering production holds a <see cref="Rules.ButcherSpotDefinition"/> Bridge and a
/// <see cref="KitchenSeparationRules.ButcherTableDefinition"/> Host), and the observed sequence follows the chain's
/// dependency and fate rules (PlannedRules.HostBindingOf, HostRemovalHold):
/// <list type="number">
/// <item>the bridge spot is built by the brain and serves: a bill stands on it;</item>
/// <item>no butcher table is placed before the spot stands built;</item>
/// <item>the spot is not removed before the table stands built with a butcher bill (its replacement serving), and once
/// it does the spot's removal is admitted: the spot is gone. A spot whose bill a pawn works when the replacement serves
/// is held (a <c>host_retire_held</c> need transition) rather than cut.</item>
/// </list>
/// Nothing selects the winner but the comparison: a comparison that adopts no chain fails the case naming the plan it
/// adopted. The unit tests (HostRetirementTests, PlannedRoomGateTests) prove the rules; only the game proves the native
/// build, the bills, the BillState.Workers read and the deconstruction.
/// </summary>
internal static class ProductionSpotBridge
{
    private const int Colonists = 2;
    private const string Spot = Rules.ButcherSpotDefinition;
    private const string Table = KitchenSeparationRules.ButcherTableDefinition;
    private const string Prey = "Muffalo";

    /// <summary>The <c>BuildingPlanner.HostRetireHeld</c> AwaitingPlan subject (internal to the runtime), as the need transitions record it.</summary>
    private const string RetireHeld = "host_retire_held";

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
            new Fx.StageThing { Name = "logs-2", Def = "WoodLog", Count = 75, Cell = new(9, 3) }, // a log stack holds 75
            new Fx.StageThing { Name = "logs-3", Def = "WoodLog", Count = 75, Cell = new(9, 4) },
        ],
        Pawns =
        [
            new Fx.StagePawn { Name = "a", Colonist = 0, Cell = new(2, 3), Timetable = Fx.StageTimeAssignment.Work },
            new Fx.StagePawn { Name = "b", Colonist = 1, Cell = new(3, 3), Timetable = Fx.StageTimeAssignment.Work },
            // Fresh corpses: the butchering load the comparison hosts (FoodOrdersRules.UnhostedFoodOrder counts each).
            .. Enumerable.Range(0, 4).Select(i => new Fx.StagePawn
            {
                Name = "prey" + i.ToString(CultureInfo.InvariantCulture), Kind = Prey, Faction = Fx.StageFaction.None, Cell = new(5 + i, 6), Dead = true,
            }),
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "production/spot-bridge",
        Scope = "RG-HABITAT-005: four fresh muffalo corpses, no butcher bench, no meals and wood in stock; under the served bill and "
            + "work families' normal review the layout comparison adopts a ButcherSpot-now/TableButcher-later chain; the brain builds "
            + "the spot and a bill stands on it, places no table before the spot stands built, and removes the spot only once the "
            + "table stands built with a butcher bill (a spot bill worked then is held, host_retire_held), read back natively.",
        Ticks = TypedCase.StagedTickCap, // a hang guard: a spot is instant, the table a short build, and the replan hourly
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Colonists },
        Crew = new Crew { Size = Colonists },
        Stage = Spec,
        Ops = [FixtureStage.Tool],
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["bill", "work"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "spot-bridge" },
        Run = RunAsync,
    };

    private sealed class Track
    {
        public int Round { get; set; }
        public int ChainAdopted { get; set; } = -1;
        public int SpotBuilt { get; set; } = -1;
        public int SpotBilled { get; set; } = -1;
        public int TableSeen { get; set; } = -1;
        public int TableServing { get; set; } = -1;
        public int SpotGone { get; set; } = -1;
        public int BridgeDropped { get; set; } = -1;
        public int SpotWorkedWhileReplaced { get; set; } = -1;
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
        var t = new Track();
        _ = await s.ServeUntilAsync("spot-bridge", s.TicksLeft, async ct =>
        {
            t.Round++;
            var plan = await s.LayoutPlanAsync(ct).ConfigureAwait(false);
            var made = plan?.Rooms.SelectMany(r => r.Production).ToArray() ?? [];
            t.Plan = string.Join(",", made.Where(p => p.Bench is Spot or Table).Select(p => p.Bench + ":" + p.Step));
            if (t.ChainAdopted < 0 && made.Any(p => p.Bench == Spot && p.Step == HostStep.Bridge)
                && made.Any(p => p.Bench == Table && p.Step == HostStep.Host))
            {
                t.ChainAdopted = t.Round;
            }
            // The replacement adopted as the load's one host (the chain's Then retained): the spot's removal is now owed.
            if (t.ChainAdopted >= 0 && t.BridgeDropped < 0 && made.Any(p => p.Bench == Table && p.Step == HostStep.Host) && !made.Any(p => p.Bench == Spot))
            {
                t.BridgeDropped = t.Round;
            }

            var standing = await s.BuildingsAsync("butcher", new RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest { DefNames = [Spot, Table] }, ct).ConfigureAwait(false);
            var spot = standing.FirstOrDefault(b => b.Building.DefName == Spot && b.Status == RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built);
            var table = standing.Where(b => b.Building.DefName == Table).ToArray();
            var bills = await s.CallAsync("bills", NativeOps.ReadBills, new RimGovernor.Brain.GameOrders.Observations.BillsRequest { Scope = s.Scope }, ct).ConfigureAwait(false);
            var spotRow = spot is null ? null : bills.Benches.FirstOrDefault(b => b.Bench == spot.Building.Id);
            var tableBuilt = table.FirstOrDefault(b => b.Status == RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built);
            var tableRow = tableBuilt is null ? null : bills.Benches.FirstOrDefault(b => b.Bench == tableBuilt.Building.Id);

            if (spot is not null)
            {
                t.Spot = spot.Building.Id;
                if (t.SpotBuilt < 0) t.SpotBuilt = t.Round;
                if (t.SpotBilled < 0 && spotRow is { Bills.Count: > 0 }) t.SpotBilled = t.Round;
            }
            if (t.TableSeen < 0 && table.Length > 0)
            {
                t.TableSeen = t.Round;
                // (b) the replacement is bound only once the bridge stands.
                if (t.SpotBuilt < 0)
                {
                    throw new AcceptanceException($"a {Table} ({table[0].Building.Id}, {table[0].Status}) was placed before the {Spot} bridge stood built (plan {t.Plan})");
                }
            }
            if (t.TableServing < 0 && tableRow is { Usable: true, Reachable: true, Bills.Count: > 0 }) t.TableServing = t.Round;
            if (t.BridgeDropped >= 0 && t.TableServing >= 0 && spotRow is not null && spotRow.Bills.Any(b => b.Workers.Count > 0) && t.SpotWorkedWhileReplaced < 0)
            {
                t.SpotWorkedWhileReplaced = t.Round;
            }
            if (t.SpotBuilt >= 0 && spot is null && t.SpotGone < 0)
            {
                t.SpotGone = t.Round;
                // (c) the bridge's removal waits for its replacement's service.
                if (t.TableServing < 0)
                {
                    throw new AcceptanceException($"the {Spot} bridge {t.Spot} was removed before a built {Table} served with a butcher bill (plan {t.Plan})");
                }
            }
            return (Wait.Signature(t.Plan, t.ChainAdopted, t.SpotBuilt, t.SpotBilled, t.TableSeen, t.TableServing, t.BridgeDropped, t.SpotWorkedWhileReplaced, t.SpotGone), t.SpotGone >= 0);
        }, cancellationToken).ConfigureAwait(false);

        var held = (await s.NeedTransitionsAsync(cancellationToken).ConfigureAwait(false)).Where(r => r.Subject == RetireHeld).ToArray();
        s.Record("plan", t.Plan);
        s.Record("rounds", string.Create(CultureInfo.InvariantCulture,
            $"chain={t.ChainAdopted} spot_built={t.SpotBuilt} spot_billed={t.SpotBilled} table_seen={t.TableSeen} table_serving={t.TableServing} bridge_dropped={t.BridgeDropped} spot_worked={t.SpotWorkedWhileReplaced} spot_gone={t.SpotGone}"));
        s.Record("retire_held", held.Length);
        if (t.ChainAdopted < 0)
        {
            throw new AcceptanceException($"the comparison adopted no {Spot}>{Table} chain (butcher production {t.Plan}): the staged draw did not make the bridge worth its labor");
        }
        // (a) the bridge was selected, placed and served.
        if (t.SpotBuilt < 0 || t.SpotBilled < 0)
        {
            throw new AcceptanceException($"the {Spot} bridge never stood built with a bill (built round {t.SpotBuilt}, billed round {t.SpotBilled})");
        }
        if (t.SpotGone < 0)
        {
            throw new AcceptanceException($"the {Spot} bridge {t.Spot} was never removed after its {Table} replacement served (round {t.TableServing}, plan {t.Plan})");
        }
        // (c) a bill worked at the spot once its replacement served held the removal, naming host_retire_held.
        if (t.SpotWorkedWhileReplaced >= 0 && held.Length == 0)
        {
            throw new AcceptanceException($"a pawn worked the {Spot}'s bill after its replacement served (round {t.SpotWorkedWhileReplaced}) once the plan owed its removal, but no {RetireHeld} transition held it");
        }
    }
}
