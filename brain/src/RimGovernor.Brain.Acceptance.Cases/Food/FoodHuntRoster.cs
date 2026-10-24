using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using MilitaryFamily = RimGovernor.Brain.Policy.MilitaryFamily;
using RimGovernor.Brain.Telemetry;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>food/hunt-roster</c> (#3515): the formation hunt end to end on a prepared colony. Two riflemen short of food, a
/// butcher table with its bill and a corpse stockpile; a wild rhinoceros (revenge big game) and a boar twelve cells
/// north of them, inside the hunt read's window. The served brain estimates every roster analytically inside its review
/// (#3533: no worker, no clock hold) and drafts exactly the chosen roster's gunners; an admitted prey is killed
/// (the delivery ledger's kill record); the formation is released after the kill (standing hunters empty, the
/// military/combat Need cleared, nobody drafted); and the corpse is butchered into meat in stock. No roster size is
/// prescribed: a lone-gunner roster passes on the same evidence. One served window carries every phase, because the
/// admitted formation lives in the brain's memory and a restart would forget it.
/// <para>
/// The lab's quiet storyteller zeroes revenge, so neither animal can fight back: the comparison ranks the rosters by
/// yield and labor alone. The big game is a rhinoceros: revenge big game that is not a predator, so the lone
/// designation channel may also take it. The crew is exactly the two staged colonists: the runner's default crew of
/// three would add an unarmed third hunter to the read, and its staffing sets every colonist's skills to the crew level.
/// </para>
/// </summary>
internal static class FoodHuntRoster
{
    private const int Colonists = 2;
    private const string Rifle = "Gun_BoltActionRifle";
    private const string BigGame = "Rhinoceros";
    private const string SmallGame = "WildBoar";
    // The meat defs the game generates for the two races (the boar uses the pig's meat).
    private const string BigGameMeat = "Meat_Rhinoceros";
    private const string SmallGameMeat = "Meat_Pig";
    private const string Table = "butcher";
    private const string Larder = "larder";
    private const string MeatPrefix = "Meat_";

    /// <summary>The charter's lab ceiling: the comparison, the hunt, the release and the butchering all come out of it.</summary>
    private const int Ticks = 30_000;

    // Absolute lab cells (LabWorld.LabMapSize = 100): hunters on a row south of the centre, prey twelve cells north of them
    // (the qualification fixture's placement), the butcher table and the corpse stockpile behind the hunters.
    private static readonly Fx.StageCell[] HunterCells = [new(47, 40), new(49, 40)];
    private static readonly Fx.StageCell RhinoCell = new(48, 52);
    private static readonly Fx.StageCell BoarCell = new(56, 54);
    private static readonly Fx.StageCell TableCell = new(48, 34);
    private static readonly Fx.StageCell MealsCell = new(50, 36);
    private static readonly Fx.StageCell[] LarderCells = [.. Enumerable.Range(42, 4).SelectMany(x => Enumerable.Range(33, 3).Select(z => new Fx.StageCell(x, z)))];

    // The families the hunt needs: acquisition compares and opens it, defense drafts and stands the formation down, work
    // keeps the hunters' priorities, bill and stockpiles butcher and haul, food-storage-upkeep allows the kill's corpse (a
    // drafted kill outside the home area drops it forbidden), tend and rescue answer a mauling.
    private static readonly IReadOnlyList<string> Families = ["acquisition", "defense", "work", "bill", "stockpiles", "food-storage-upkeep", "tend", "rescue"];

    private static readonly Fx.StageSpec Camp = new()
    {
        Paused = true,
        Buildings = [new Fx.StageBuilding { Name = Table, Def = "TableButcher", Stuff = "WoodLog", Cells = [TableCell] }],
        Bills = [new Fx.StageBill { Name = "butcher", Building = Table, Recipe = "ButcherCorpseFlesh", RepeatCount = 4 }],
        // Little food: one meal per colonist, under a day, so the plan has a gap to hunt for.
        Things = [new Fx.StageThing { Name = "meals", Def = "MealSimple", Count = Colonists, Cell = MealsCell }],
        Zones =
        [
            new Fx.StageZone
            {
                Name = Larder,
                Cells = LarderCells,
                Priority = Fx.StageStoragePriority.Important,
                Allow = ["Corpse_" + BigGame, "Corpse_" + SmallGame, BigGameMeat, SmallGameMeat, "MealSimple"],
            },
        ],
        Pawns =
        [
            .. Enumerable.Range(0, Colonists).Select(i => new Fx.StagePawn
            {
                Name = Hunter(i),
                Colonist = i,
                Cell = HunterCells[i],
                Gear = new Fx.StageGear { Weapon = Rifle },
                AllWork = 3,
                Work = new Dictionary<string, int> { ["Hunting"] = 1, ["Hauling"] = 1, ["Cooking"] = 1 },
                Timetable = Fx.StageTimeAssignment.Work,
            }),
            new Fx.StagePawn { Name = "rhino", Kind = BigGame, Faction = Fx.StageFaction.None, Age = 3, Cell = RhinoCell },
            new Fx.StagePawn { Name = "boar", Kind = SmallGame, Faction = Fx.StageFaction.None, Age = 3, Cell = BoarCell },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "food/hunt-roster",
        Scope = "The formation hunt end to end (#3515) on a prepared lab colony: two bolt-action riflemen short of food, a butcher "
            + "table with a butcher bill and a corpse stockpile, a wild rhinoceros and a boar twelve cells north. The served brain "
            + "estimates the rosters analytically in its review and chooses one (hunt_choice names a chosen roster) and the drafted "
            + "colonists are exactly that roster's gunners; the delivery ledger records a "
            + "kill of an admitted prey; the formation is then released (no standing hunters, military/combat cleared, nobody drafted); "
            + "the corpse is butchered and meat is in stock. Roster size is the comparison's; the lab's quiet storyteller zeroes revenge.",
        Ticks = Ticks,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Colonists },
        Crew = new Crew { Size = Colonists },
        Stage = Camp,
        Keep = ["Food"],
        // No ClockSpeed: the test-acceleration launch pins Hyperfast, so a request here would be a no-op.
        Serve = new ServeSpec { Families = Families, Prefix = "food-hunt-roster" },
        Run = RunAsync,
    };

    private static string Hunter(int i) => "hunter" + i.ToString(CultureInfo.InvariantCulture);

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged ?? throw new AcceptanceException("food/hunt-roster stages its colony through Stage");
        var hunters = new HashSet<string>(Enumerable.Range(0, Colonists).Select(i => staged.Pawns[Hunter(i)].Id), StringComparer.Ordinal);
        var prey = new Dictionary<string, string>(StringComparer.Ordinal) { [staged.Pawns["rhino"].Id] = BigGame, [staged.Pawns["boar"].Id] = SmallGame };
        s.Record("hunters", Ids(hunters));
        s.Record("prey", new JsonObject(prey.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))));

        // The service log outlives a served window on a reused root: the reviews of this window are the rows after the ones it starts with.
        var run = new HuntRun(s, hunters, prey, s.Telemetry(TelemetryKinds.RoundsReview).Select(r => r.Sequence).DefaultIfEmpty(0UL).Max());
        _ = await s.ServeUntilAsync("hunt", s.TicksLeft, run.ProbeAsync, cancellationToken).ConfigureAwait(false);
    }

    private static JsonArray Ids(IEnumerable<string> ids) => new([.. ids.Order(StringComparer.Ordinal).Select(id => (JsonNode)id)]);

    private static string Join(IEnumerable<string> ids) => string.Join(",", ids.Order(StringComparer.Ordinal));

    private static string Text(LogRow row, string key) => row.Payload.GetValueOrDefault(key) as string ?? "";

    /// <summary>The value of <c>name=</c> in a space-separated note, empty when absent.</summary>
    private static string Field(string note, string name)
    {
        foreach (var part in note.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith(name, StringComparison.Ordinal))
            {
                return part[name.Length..];
            }
        }

        return "";
    }

    private enum Phase
    {
        Comparison,
        Draft,
        Kill,
        Release,
        Butcher,
        Done,
    }

    /// <summary>The case's phases over one served window, in the order the claim states them; each records its evidence as it passes.</summary>
    private sealed class HuntRun(TypedSession s, IReadOnlySet<string> hunters, IReadOnlyDictionary<string, string> prey, ulong before)
    {
        private readonly JsonArray _corpsePath = [];
        private Phase _phase = Phase.Comparison;
        private string _chosen = "";
        private string _note = "";
        private HashSet<string> _roster = new(StringComparer.Ordinal);
        private HashSet<string> _admitted = new(StringComparer.Ordinal);
        private Observations.Kill? _kill;
        private string _corpseCell = "";
        private long _meat;

        public async Task<(string Signature, bool Done)> ProbeAsync(CancellationToken ct)
        {
            var tick = await CombatLabKit.TickAsync(s, "hunt-tick", ct).ConfigureAwait(false);
            switch (_phase)
            {
                case Phase.Comparison:
                    Compare(tick);
                    break;
                case Phase.Draft:
                    await DraftAsync(tick, ct).ConfigureAwait(false);
                    break;
                case Phase.Kill:
                    await KillAsync(tick, ct).ConfigureAwait(false);
                    break;
                case Phase.Release:
                    await ReleaseAsync(tick, ct).ConfigureAwait(false);
                    break;
                case Phase.Butcher:
                    await ButcherAsync(tick, ct).ConfigureAwait(false);
                    break;
                case Phase.Done:
                    break;
            }

            return (Wait.Signature(_phase, tick, _chosen, _roster.Count, _kill?.CorpseId, _corpseCell, _meat, _note), _phase == Phase.Done);
        }

        /// <summary>This window's rounds_review rows with a hunt note, oldest first; empty before the first review that saw the hunt read.</summary>
        private IEnumerable<LogRow> Choices() => s.Telemetry(TelemetryKinds.RoundsReview).Where(r => r.Sequence > before && r.Payload.ContainsKey(RoundsReviewTelemetry.HuntChoiceKey));

        /// <summary>Reads the row's chosen roster key; empty until the comparison has chosen a roster.</summary>
        private static string Chosen(LogRow row)
        {
            var chosen = Field(Text(row, RoundsReviewTelemetry.HuntChoiceKey), "chosen=");
            return chosen is "" or "none" ? "" : chosen;
        }

        /// <summary>The roster of the food plan's hunt/roster channel in the row: the admitted formation's once one stands (the comparison then goes on among the free hunters), the chosen roster's before.</summary>
        private static string Channel(LogRow row) =>
            Text(row, RoundsReviewTelemetry.FoodPlanKey).Split('\n').Select(l => Field(l.Trim(), "hunt/roster:")).FirstOrDefault(k => k.Length > 0) ?? "";

        // Comparison made: the first review whose note names a chosen roster. The first one, not the probe's latest: once
        // the formation stands, the review compares the free hunters again.
        private void Compare(long tick)
        {
            if (Choices().FirstOrDefault(r => Chosen(r).Length > 0) is not { } row)
            {
                return;
            }

            _chosen = Chosen(row);
            _note = Text(row, RoundsReviewTelemetry.HuntChoiceKey);
            s.Record("comparison", CombatLabKit.Obj(
                ("tick", tick),
                ("review_tick", row.Tick),
                (RoundsReviewTelemetry.HuntChoiceKey, _note),
                (RoundsReviewTelemetry.HuntReadKey, Text(row, RoundsReviewTelemetry.HuntReadKey))));
            // The chosen roster's estimate terms (hunt_* chances): its hunt/roster line of the food plan.
            var terms = Text(row, RoundsReviewTelemetry.FoodPlanKey).Split('\n').FirstOrDefault(l => l.Contains("hunt/roster:" + _chosen, StringComparison.Ordinal)) ?? "";
            s.Record("estimate_terms", terms.Trim());
            _phase = Phase.Draft;
        }

        // Drafted: the standing hunters the review filed are exactly the drafted colonists, and they are the gunners of the
        // roster the plan executes (its hunt/roster channel), a roster some review chose.
        private async Task DraftAsync(long tick, CancellationToken ct)
        {
            var review = await s.ReviewAsync(ct).ConfigureAwait(false);
            if (review?.Combat is not { Hunters.Count: > 0 } combat)
            {
                return;
            }

            var standing = combat.Hunters.Select(h => h.Value).ToHashSet(StringComparer.Ordinal);
            var drafted = await DraftedAsync(ct).ConfigureAwait(false);
            if (!drafted.SetEquals(standing))
            {
                // The draft follows the filing by a combat round; a drafted colonist outside the roster is a failure the next probe sees.
                if (!drafted.IsSubsetOf(standing))
                {
                    throw new AcceptanceException($"drafted colonists {Join(drafted)} are not the standing hunters {Join(standing)} ({_note})");
                }

                return;
            }

            var rows = Choices().ToList();
            var chosen = rows.Select(Chosen).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
            var executing = rows.Count == 0 ? "" : Channel(rows[^1]);
            if (executing.Length == 0 || !chosen.Contains(executing))
            {
                throw new AcceptanceException($"the plan's hunt/roster channel {executing} is not a roster the comparison chose ({Join(chosen)})");
            }

            _chosen = executing;
            var gunners = executing.Split('>')[0].Split('+').ToHashSet(StringComparer.Ordinal);
            if (!gunners.SetEquals(standing))
            {
                throw new AcceptanceException($"the standing hunters {Join(standing)} are not the executing roster's gunners {Join(gunners)} ({executing})");
            }

            if (!standing.IsSubsetOf(hunters))
            {
                throw new AcceptanceException($"the standing hunters {Join(standing)} are not the staged riflemen {Join(hunters)}");
            }

            _roster = standing;
            _admitted = combat.Prey.Select(p => p.Value).ToHashSet(StringComparer.Ordinal);
            if (_admitted.Count == 0 || !_admitted.All(prey.ContainsKey))
            {
                throw new AcceptanceException($"the admitted prey {Join(_admitted)} are not the staged animals {Join(prey.Keys)}");
            }

            s.Record("roster", CombatLabKit.Obj(
                ("tick", tick),
                ("key", _chosen),
                ("gunners", Ids(standing)),
                ("prey", Ids(_admitted)),
                ("prey_kinds", Ids(_admitted.Select(id => prey[id]))),
                ("situation", combat.Situation.ToString())));
            _phase = Phase.Kill;
        }

        // Kill: the delivery ledger holds a kill record of an admitted prey, with nobody outside the roster drafted meanwhile.
        private async Task KillAsync(long tick, CancellationToken ct)
        {
            var drafted = await DraftedAsync(ct).ConfigureAwait(false);
            if (!drafted.IsSubsetOf(_roster))
            {
                throw new AcceptanceException($"drafted colonists {Join(drafted)} outside the roster {Join(_roster)} during the hunt");
            }

            var ledger = await FoodLedger.ReadAsync(s, "ledger-kill", ct).ConfigureAwait(false);
            var kill = ledger.Kills.FirstOrDefault(k => _admitted.Contains(k.Pawn.Value));
            if (kill is null)
            {
                return;
            }

            _kill = kill;
            s.RecordWire("kill", kill);
            s.Record("kill_tick", kill.Tick.Value);
            _phase = Phase.Release;
        }

        // Released: no standing hunters, nobody drafted, and the military/combat Need's latest transition cleared its cause.
        private async Task ReleaseAsync(long tick, CancellationToken ct)
        {
            var review = await s.ReviewAsync(ct).ConfigureAwait(false);
            if (review?.Combat is { Hunters.Count: > 0 })
            {
                return;
            }

            var drafted = await DraftedAsync(ct).ConfigureAwait(false);
            if (drafted.Count > 0)
            {
                return;
            }

            var combatNeed = MilitaryFamily.Combat.Value;
            var transitions = await s.NeedTransitionsAsync(ct).ConfigureAwait(false);
            var latest = transitions.LastOrDefault(t => t.Need == combatNeed || t.Need.StartsWith(combatNeed + "/", StringComparison.Ordinal));
            if (latest is null || latest.Cause.Length != 0)
            {
                return;
            }

            var note = Choices().LastOrDefault() is { } row ? Text(row, RoundsReviewTelemetry.HuntChoiceKey) : "";
            s.Record("release", CombatLabKit.Obj(
                ("tick", tick),
                ("need", latest.Need),
                ("verdict", latest.Verdict),
                ("previous", latest.Previous),
                ("held", latest.Held),
                ("transition_tick", latest.Tick),
                (RoundsReviewTelemetry.HuntChoiceKey, note)));
            _phase = Phase.Butcher;
        }

        // Butchered: the corpse's cells on its way (evidence), then its butcher record and meat in stock.
        private async Task ButcherAsync(long tick, CancellationToken ct)
        {
            var corpseId = _kill!.CorpseId;
            var corpses = await SuppliesAsync("corpses", new Observations.StockFilter
            {
                DefNames = ["Corpse_" + BigGame, "Corpse_" + SmallGame],
                Ownership = Observations.StockOwnership.All,
                Corpses = true,
            }, ct).ConfigureAwait(false);
            var corpse = corpses.Stocks.SelectMany(st => st.Items).FirstOrDefault(i => i.Item.Id == corpseId);
            var cell = corpse is null ? "gone" : string.Create(CultureInfo.InvariantCulture, $"{corpse.Cell.X},{corpse.Cell.Z}");
            if (cell != _corpseCell)
            {
                _corpseCell = cell;
                var inLarder = corpse is not null && LarderCells.Any(c => c.X == corpse.Cell.X && c.Z == corpse.Cell.Z);
                _corpsePath.Add(CombatLabKit.Obj(("tick", tick), ("cell", cell), ("in_larder", inLarder)));
            }

            var ledger = await FoodLedger.ReadAsync(s, "ledger-butcher", ct).ConfigureAwait(false);
            var butcher = ledger.Butchers.FirstOrDefault(b => b.CorpseId == corpseId);
            if (butcher is null)
            {
                return;
            }

            var food = await SuppliesAsync("meat", new Observations.StockFilter { Category = Observations.StockCategory.Food, Ownership = Observations.StockOwnership.All }, ct).ConfigureAwait(false);
            var meat = food.Stocks.Where(st => st.Definition.DefName.StartsWith(MeatPrefix, StringComparison.Ordinal)).ToList();
            _meat = meat.Sum(st => st.Units);
            if (_meat <= 0)
            {
                return;
            }

            s.RecordWire("butcher", butcher);
            s.Record("meat", CombatLabKit.Obj(
                ("tick", tick),
                ("units", _meat),
                ("stocks", new JsonObject(meat.Select(st => KeyValuePair.Create(st.Definition.DefName, (JsonNode?)st.Units))))));
            s.Record("corpse_path", _corpsePath);
            _phase = Phase.Done;
        }

        private async Task<HashSet<string>> DraftedAsync(CancellationToken ct)
        {
            var reply = await s.CallAsync("drafted", NativeOps.ListPawns, new Observations.ListPawnsRequest
            {
                Scope = s.Scope,
                Filter = new Observations.PawnFilter { Colonist = true, Drafted = true },
            }, ct).ConfigureAwait(false);
            var snapshot = reply;
            return snapshot.Pawns.Select(p => p.Pawn.Id).ToHashSet(StringComparer.Ordinal);
        }

        private async Task<Observations.SuppliesSnapshot> SuppliesAsync(string label, Observations.StockFilter filter, CancellationToken ct)
        {
            var reply = await s.CallAsync(label, NativeOps.ListSupplies, new Observations.ListSuppliesRequest { Scope = s.Scope, Filter = filter }, ct).ConfigureAwait(false);
            return TypedReads.Observed(label, NativeOps.ListSupplies, reply, reply.Outcome?.Observed);
        }
    }
}
