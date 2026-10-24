using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

using BandedThought = (string Thought, IReadOnlyList<(double Start, double End, double OffsetDelta)> Spans);

public static partial class ForecastCoupled
{
    private static ForecastCapacityInput CapacityInput(string pawn, WorkPawnAvailability? observed, double horizon, Fact<EquatableArray<ForecastOccupancy>> occupancy) => new()
    {
        Pawn = new()
        {
            Id = new(pawn),
            Available = observed?.Available ?? default,
            Applies = observed?.Applies ?? default
        },
        Location = "start",
        HorizonTicks = horizon,
        ProtectTimetable = false,
        LocalHour = observed?.LocalHour ?? default,
        OpportunityCostPerTick = 1.0,
        CurrentWork = new(new(pawn), observed?.Occupied ?? default, default,
            observed is { Occupied.IsKnown: true, Occupied.Value: false } ? Known(0.0) : default),
        Occupancy = occupancy
    };

    /// <summary>A search cache's result with the id it was first cached under, so a later key holds the id instead of the value.</summary>
    internal readonly record struct Cached<T>(T Value, int Id);

    /// <summary>A task's material shape, fixed for a search: inputs by resource in first-appearance order with units summed
    /// in row order, the lots its inputs name, its source lots with summed units, and the meal it serves with that meal's lots.</summary>
    internal sealed record TaskShape((Resource Resource, double Units)[] Inputs, string[] InputLots,
        ((string Lot, string Location, Resource Resource) Key, double Units)[] LotInputs, ForecastNeedTask? Ingestion, string[] FoodLots);

    internal readonly record struct ScheduleEvent(double Time, int Order, bool Start, ForecastActivityAllocation Allocation);

    /// <summary>Two references as one key: the records a search shares are keyed by identity, never compared by value.</summary>
    private readonly struct RefPair(object first, object second) : IEquatable<RefPair>
    {
        private readonly object first = first, second = second;
        public bool Equals(RefPair other) => ReferenceEquals(first, other.first) && ReferenceEquals(second, other.second);
        public override bool Equals(object? obj) => obj is RefPair other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(first), RuntimeHelpers.GetHashCode(second));
    }

    private readonly record struct Claim(string Target, string Pawn, int Count, Fact<int> MaxPawns, Fact<double> AvailableCount, string Stack, string Task, TargetUse Use);

    /// <summary>What a search's context fixes per pawn: the observed availability row, the pawn's need tasks and the journal
    /// occupancy of a pawn with claims.</summary>
    internal sealed record PawnFacts(NeedsForecastPawn Pawn, ForecastCapacityInput Input, ForecastNeedTask[] NeedTasks,
        ForecastOccupancy? Journal);

    private sealed record PlannedOutcome(ForecastPlannedRoomOutcome[] Rooms, string[] Development, string[] Sites, string[] Unassessed);

    private sealed record ReliefOutcome(Fact<double> Relief, string? Unassessed);

    /// <summary>A context's indexes, built once per search: the pawns with what the context fixes for each, the tasks that
    /// raise a shelter or develop a planned room, the support consequences when no schedule changes them, and the per-pawn
    /// scratch of one evaluation.</summary>
    private sealed record ContextIndex(PawnFacts[] Pawns, Dictionary<string, int> Index, HashSet<string> Nonproductive,
        HashSet<string> ShelterTasks, HashSet<string> DevelopmentTasks, ForecastSupportConsequences? Support,
        List<(NeedMemory Thought, double Start)>[] Granted, List<(string Thought, double OffsetDelta, double Start)>[] Situational,
        List<(double MinDelta, double MaxDelta, double Start)>[] Comfort,
        List<ForecastActivityAllocation>[] Allocations, List<ForecastActivityAllocation>[] Use, Cached<ForecastThermalExposure>[] Exposure,
        HashSet<string> Unplaced);

    /// <summary>
    /// One search's evaluator and memory (#3467, #3468). Every portfolio's schedules are evaluated against one context, so
    /// the context's indexes are built once; a schedule's own scratch (its ledger, events, findings and per-pawn rows) is
    /// pooled here and reused by the next evaluation; and the sequences evaluations repeat (a pawn's choices and occupancy,
    /// the completed allocations a result reads, the need services) are interned to ids, so each timeline, shelter
    /// projection, support, maintenance and relief result is computed once per distinct input and found again by an
    /// integer key. Each memo is keyed by exactly the completed rows its result reads (the shelter prerequisites, the
    /// development tasks by pawn, the support services), so schedules that differ elsewhere share it.
    /// </summary>
    internal sealed partial class CoupledSearchCache
    {
        internal ForecastActivities.RouteIndex Routes { get; } = new();

        /// <summary>One schedule's events; evaluation recurses only after it is done with them.</summary>
        internal List<ScheduleEvent> Events { get; } = [];
        private readonly Dictionary<ForecastActivityProblem, Dictionary<string, ForecastActivity>> tasks = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ForecastActivity, TaskShape> shapes = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<string, ForecastActivity> Tasks(ForecastActivityProblem problem)
        {
            if (!tasks.TryGetValue(problem, out var byId)) tasks.Add(problem, byId = problem.Activities.ToDictionary(t => t.Id, StringComparer.Ordinal));
            return byId;
        }
        internal TaskShape Shape(ForecastActivity task, ILookup<string, ForecastNeedTask> needTasksByTask)
        {
            if (shapes.TryGetValue(task, out var shape)) return shape;
            static double Sum(IEnumerable<ForecastActivityYield> rows)
            {
                double total = 0;
                foreach (var row in rows) total += row.Units;
                return total;
            }
            var inputs = task.Inputs.GroupBy(i => i.Resource).Select(g => (g.Key, Sum(g))).ToArray();
            var lotInputs = task.Inputs.Where(i => i.Lot.Length != 0)
                .GroupBy(i => (i.Lot, Location: i.LotLocation.Length == 0 ? task.Location : i.LotLocation, i.Resource))
                .Select(g => (g.Key, Sum(g))).ToArray();
            var ingestion = needTasksByTask[task.Id].SingleOrDefault(n => n.Kind == "food");
            shape = new(inputs, task.Inputs.Where(i => i.Lot.Length != 0).Select(i => i.Lot).ToArray(), lotInputs, ingestion,
                ingestion is null ? [] : task.Inputs.Where(i => i.Resource == ingestion.Resource).Select(i => i.Lot).ToArray());
            shapes.Add(task, shape);
            return shape;
        }
        private ForecastCoupledContext? targetsOf;
        private (Dictionary<string, Fact<int>> MaxPawns, Dictionary<string, NeedTarget> Decaying, Dictionary<string, Fact<double>> Stocks) targets;
        /// <summary>Each observed target's MaxPawns, the observed food stacks that deteriorate, by target id, and each target's
        /// known stock count (a haul from such a stack shares it, #3626).</summary>
        internal (Dictionary<string, Fact<int>> MaxPawns, Dictionary<string, NeedTarget> Decaying, Dictionary<string, Fact<double>> Stocks) Targets(ForecastCoupledContext context)
        {
            if (!ReferenceEquals(context, targetsOf))
            {
                var observed = context.Needs.Pawns.SelectMany(p => p.Targets).ToArray();
                (targetsOf, targets) = (context, (observed.GroupBy(t => t.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().MaxPawns, StringComparer.Ordinal),
                    observed.Where(t => t.Kind == "food" && t.Deterioration is not null).GroupBy(t => t.Id, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal),
                    observed.Where(t => t.StockCount.IsKnown).GroupBy(t => t.Id, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.First().StockCount, StringComparer.Ordinal)));
            }
            return targets;
        }
        private (IReadOnlyDictionary<string, ForecastCommitment> Commitments, double Horizon)? externalOf;
        private (string Owner, ForecastStockChange Change)[] external = [];
        /// <summary>The stock effects of commitments without activities inside the horizon, by day and owner.</summary>
        internal (string Owner, ForecastStockChange Change)[] External(IReadOnlyDictionary<string, ForecastCommitment> commitments, double horizonDays)
        {
            if (externalOf is not { } of || !ReferenceEquals(of.Commitments, commitments) || of.Horizon != horizonDays)
                (externalOf, external) = ((commitments, horizonDays), commitments.Values.Where(c => c.ActivityCompletions.Count == 0)
                    .SelectMany(c => c.StockChanges.Select(change => (Owner: c.Id, Change: change))).Where(e => e.Change.Day <= horizonDays)
                    .OrderBy(e => e.Change.Day).ThenBy(e => e.Owner, StringComparer.Ordinal).ToArray());
            return external;
        }
        private IReadOnlyDictionary<string, ForecastCommitment>? grantingOf;
        private ForecastCommitment[] granting = [];
        /// <summary>The commitments with mood memories, situational changes, comfort shifts or outfit changes to grant.</summary>
        private ForecastCommitment[] Granting(IReadOnlyDictionary<string, ForecastCommitment> commitments)
        {
            if (!ReferenceEquals(commitments, grantingOf))
                (grantingOf, granting) = (commitments, commitments.Values.Where(c => c.MoodMemories.Count + c.SituationalChanges.Count + c.ComfortShifts.Count + c.OutfitChanges.Count > 0).ToArray());
            return granting;
        }
        private (ColonyForecastRequest Request, IReadOnlyDictionary<string, ForecastCommitment> Commitments)? materialsOf;
        private ForecastCoupledMaterials.MaterialHolds? materials;
        private readonly Dictionary<int, (Fact<IReadOnlyDictionary<Resource, Fact<double>>> Budget, KeyValuePair<Resource, Fact<double>>[] Rows)> budgets = [];
        internal ForecastCoupledMaterials.MaterialHolds? Materials(ColonyForecastRequest request, IReadOnlyDictionary<string, ForecastCommitment> commitments)
        {
            if (materialsOf is not { } of || !ReferenceEquals(of.Request, request) || !ReferenceEquals(of.Commitments, commitments))
            {
                (materialsOf, materials) = ((request, commitments), ForecastCoupledMaterials.MaterialHoldsOf(request, commitments));
                budgets.Clear();
            }
            return materials;
        }
        private (ColonyForecastRequest Request, IReadOnlyDictionary<string, ForecastCommitment> Commitments)? inventoryOf;
        private ColonyForecastRules.InventoryBase inventory = null!;
        private readonly ColonyForecastRules.InventoryWalk inventoryWalk = new();
        /// <summary>The stock projection's fixed part: the observed stock and the commitments' own effects.</summary>
        private ColonyForecastRules.InventoryBase Inventory(ColonyForecastRequest request, IReadOnlyDictionary<string, ForecastCommitment> commitments)
        {
            if (inventoryOf is not { } of || !ReferenceEquals(of.Request, request) || !ReferenceEquals(of.Commitments, commitments))
                (inventoryOf, inventory) = ((request, commitments), new(request.Facts.Stock, commitments, request.HorizonDays));
            return inventory;
        }
        private ForecastCoupledContext? indexed;
        private (ILookup<string, ForecastNeedTask> ByTask, ILookup<string, ForecastNeedTask> ByPawn) needTasks;
        internal (ILookup<string, ForecastNeedTask> ByTask, ILookup<string, ForecastNeedTask> ByPawn) NeedTasks(ForecastCoupledContext context)
        {
            if (!ReferenceEquals(context, indexed))
                (indexed, needTasks) = (context, (context.NeedTasks.ToLookup(n => n.Task, StringComparer.Ordinal), context.NeedTasks.ToLookup(n => n.Pawn, StringComparer.Ordinal)));
            return needTasks;
        }
        private (ForecastCoupledContext Context, double Horizon)? pawnsOf;
        private ContextIndex index = null!;
        private ContextIndex Pawns(ForecastCoupledContext context, double horizon)
        {
            if (pawnsOf is { } of && ReferenceEquals(of.Context, context) && of.Horizon == horizon) return index;
            var byPawn = NeedTasks(context).ByPawn;
            var pawns = new PawnFacts[context.Needs.Pawns.Count];
            var byName = new Dictionary<string, int>(pawns.Length, StringComparer.Ordinal);
            for (var p = 0; p < pawns.Length; p++)
            {
                var pawn = context.Needs.Pawns[p];
                WorkPawnAvailability? observed = null;
                if (context.Resources.Evidence.Pawns.TryGetValue(out var rows))
                    foreach (var row in rows)
                        if (row.Pawn == pawn.Pawn) { if (observed is not null) throw new InvalidOperationException("Sequence contains more than one matching element"); observed = row; }
                var journal = false;
                foreach (var claimant in context.Resources.Claims.Pawns) journal |= claimant == pawn.Pawn;
                pawns[p] = new(pawn, CapacityInput(pawn.Pawn, observed, horizon, default), byPawn[pawn.Pawn].ToArray(),
                    journal ? new("journal", pawn.Pawn, Known(0.0), default, ForecastOccupancyState.Active) : null);
                byName[pawn.Pawn] = p;
            }
            var shelterTasks = new HashSet<string>(StringComparer.Ordinal);
            var developmentTasks = new HashSet<string>(StringComparer.Ordinal);
            foreach (var room in context.Shelters)
            {
                shelterTasks.UnionWith(room.Prerequisites);
                foreach (var group in room.AlternativePrerequisites) shelterTasks.UnionWith(group);
                foreach (var cell in room.RoofCells) developmentTasks.UnionWith(cell);
            }
            foreach (var site in context.PlannedSites) developmentTasks.UnionWith(site.Finishes);
            foreach (var ((activity, _), binding) in context.Bindings)
                if (binding is { Kind: PawnActivityKind.Work } && binding.Job is "FinishFrame" or "BuildRoof") developmentTasks.Add(activity);
            // Without demands or deliveries, the support consequences are the context's own, whatever completes.
            var support = context.Support.Demands.Count == 0 && context.Support.Deliveries.Count == 0
                ? ForecastSupport.ForecastSupportConsequencesOf(context, new([], 0, 0), new Dictionary<string, Fact<bool>>(), horizon) : null;
            static T[] Lists<T>(int count) where T : new() { var lists = new T[count]; for (var i = 0; i < count; i++) lists[i] = new(); return lists; }
            index = new(pawns, byName, new HashSet<string>(context.NonproductiveOccupancy, StringComparer.Ordinal), shelterTasks, developmentTasks, support,
                Lists<List<(NeedMemory, double)>>(pawns.Length), Lists<List<(string, double, double)>>(pawns.Length), Lists<List<(double, double, double)>>(pawns.Length),
                Lists<List<ForecastActivityAllocation>>(pawns.Length), Lists<List<ForecastActivityAllocation>>(pawns.Length),
                new Cached<ForecastThermalExposure>[pawns.Length], context.PlannedSites.SelectMany(s => s.Activities).ToHashSet(StringComparer.Ordinal));
            pawnsOf = (context, horizon);
            return index;
        }
        internal static readonly Fact<EquatableArray<ForecastOccupancy>> NoOccupancy = Known<EquatableArray<ForecastOccupancy>>([]);
        private int ids;
        private Cached<T> Next<T>(T value) => new(value, ids++);
        private static EquatableArray<T> Key<T>(IReadOnlyList<T>? rows) => rows is null || rows.Count == 0 ? default : EquatableArray.From(rows);

        // The sequences evaluations repeat, interned to ids (#3468). Allocations, choices, occupancy rows, exposures and bill
        // bindings are the records the search shares, keyed by reference once mapped to the one object of their value; the
        // rest are small value rows.
        private readonly SequenceKeys<ForecastActivityAllocation> allocs = new(ReferenceEqualityComparer.Instance);
        private readonly SequenceKeys<ForecastNeedChoice> choiceKeys = new(ReferenceEqualityComparer.Instance);
        private readonly SequenceKeys<ForecastOccupancy> occupancies = new(ReferenceEqualityComparer.Instance);
        private readonly SequenceKeys<ForecastThermalExposure> exposureKeys = new(ReferenceEqualityComparer.Instance);
        private readonly SequenceKeys<Methods.ActivityTaskCandidate> bills = new(ReferenceEqualityComparer.Instance);
        private readonly SequenceKeys<(string Activity, string Pawn)> pairKeys = new();
        private readonly SequenceKeys<(NeedMemory Thought, double Start)> grantedKeys = new();
        private readonly SequenceKeys<(string Thought, double OffsetDelta, double Start)> situationalKeys = new();
        private readonly SequenceKeys<(double MinDelta, double MaxDelta, double Start)> comfortKeys = new();
        private readonly Dictionary<RefPair, ForecastNeedChoice> choiceRows = [];
        private readonly Dictionary<(int Pawn, double End), ForecastNeedChoice> observedSleeps = [];
        private readonly Dictionary<(ForecastNeedChoice Choice, double Depart), ForecastNeedChoice> departuresAfterSleep = [];
        // The search hands equal allocations to many plans as distinct objects (each branch places its own), so each is
        // first mapped to the one object of its value; every reference-keyed sequence above then compares by value.
        private readonly Dictionary<ForecastActivityAllocation, ForecastActivityAllocation> allocationsByValue = [];
        private readonly Dictionary<ForecastThermalExposure, Cached<ForecastThermalExposure>> exposuresByValue = [];
        private readonly List<ForecastActivityAllocation> canonical = [];
        private readonly Dictionary<ForecastActivityAllocation, ForecastOccupancy> occupancyRows = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Methods.ActivityTaskCandidate, SupportDelivery?> deliveryRows = new(ReferenceEqualityComparer.Instance);

        // What a schedule's completed rows (by the id of the rows each reads) or its need services (by id) determine, computed once each.
        private readonly Dictionary<int, EquatableArray<ForecastShelterOutcome>> sheltersByPrerequisites = [];
        private readonly Dictionary<int, ForecastSupportConsequences> supportByServices = [];
        private readonly Dictionary<int, EquatableArray<ForecastMaintenanceConsequence>> maintenanceByCompleted = [];
        private readonly Dictionary<int, PlannedOutcome> plannedByDevelopment = [];
        private readonly Dictionary<int, string> occupancyByUse = [];
        private readonly Dictionary<(int Use, int Shelters), ReliefOutcome> reliefByUse = [];
        private readonly Dictionary<(int Pawn, int Allocations, int Shelters, int Comfort), Cached<ForecastThermalExposure>> exposures = [];
        private readonly Dictionary<(int Pawn, int Allocations), Cached<ForecastThermalExposure>> unchangedExposures = [];
        private readonly Dictionary<(int Pawn, int Choices, int Granted, int Renewals), ForecastNeeds.NeedsCore> cores = [];
        private readonly Dictionary<(int Pawn, int Occupancy), ForecastCapacityResult> capacities = [];
        private readonly Dictionary<(int Pawn, int Occupancy, int Choices, int Granted, int Renewals), Cached<ForecastNeedsResult>> needs = [];
        private readonly Dictionary<(int Pawn, int Choices, int Granted, int Situational, int Banded, int Renewals), Cached<ForecastMoodResult>> moods = [];
        private readonly Dictionary<(int Pawn, int Choices, EquatableArray<ForecastClosedRoom> Rooms), Cached<ForecastRoofMood>> roofs = [];
        private readonly Dictionary<(int Pawn, int Exposure, int Roof, int Choices), Cached<BandedThought[]>> banded = [];
        private readonly Dictionary<(int Needs, int Mood, int Roof), ForecastNeedsResult> projected = [];
        private Cached<ForecastThermalExposure> Exposure(ForecastThermalExposure value)
        {
            if (!exposuresByValue.TryGetValue(value, out var cached)) exposuresByValue.Add(value, cached = Next(value));
            return cached;
        }

        // One evaluation's scratch, reused by the next (the evaluator recurses only as its last step).
        private readonly List<ForecastFinding> findings = [];
        private readonly Dictionary<string, ForecastActivityAllocation> allocations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Fact<bool>> completed = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Lot, string Location, Resource Resource), Fact<double>> lots = [];
        private readonly Dictionary<string, Fact<double>> expiry = new(StringComparer.Ordinal);
        private readonly Dictionary<Resource, Fact<double>> balance = [];
        private readonly List<(Resource Resource, double Day, Fact<double> Units)> effects = [];
        private readonly Dictionary<string, double> finishedAt = new(StringComparer.Ordinal);
        private readonly HashSet<string> decayed = new(StringComparer.Ordinal);
        private readonly List<(string Lot, string Location, Resource Resource)> expiredKeys = [], decayKeys = [];
        private readonly List<Claim> claims = [];
        private readonly List<TargetUse> uses = [];
        private readonly List<SupportDelivery> deliveries = [];
        private readonly List<Methods.ActivityTaskCandidate> billRows = [];
        private readonly List<ForecastActivityAllocation> completedRows = [], shelterRows = [], serviceRows = [], useRows = [], productive = [];
        private readonly List<(string Activity, string Pawn)> developmentRows = [];
        private readonly List<ForecastNeedChoice> choices = [];
        private readonly List<ForecastOccupancy> protectedTime = [];
        private readonly List<ForecastThermalExposure> exposureRows = [];
        private readonly List<ForecastThermalSpan> spanScratch = [];
        private readonly ForecastShelterPolicy.ShelterScratch shelterScratch = new();
        private ForecastCoupledContext context = null!;
        private Fact<bool> feasible;
        private Dictionary<string, NeedTarget> decaying = null!;
        private (string Owner, ForecastStockChange Change)[] timedExternal = [];
        private int nextExternal;

        private void Finding(string reason, string id) => findings.Add(new(reason, id));
        private void Uncertain(string reason, string id)
        { if (feasible != Known(false)) feasible = default; Finding(reason, id); }
        private void Refuse(string reason, string id)
        { feasible = Known(false); Finding(reason, id); }

        /// <summary>
        /// The tick a commitment's effects start (#3881): when its last activity completes inside the horizon, the forecast start
        /// when it has none, infinity when an activity fails or ends later. Every fact that settles a completion is read (bill
        /// reservations, lots, expiry, material budget), so an unknown one is an unread fact.
        /// </summary>
        private double CompletedAt(ForecastCommitment commitment, double horizon)
        {
            var at = 0d;
            foreach (var id in commitment.ActivityCompletions)
            {
                var done = completed.GetValueOrDefault(id);
                if (!allocations.TryGetValue(id, out var allocation) || allocation.End > horizon || done == Known(false)) return double.PositiveInfinity;
                if (!done.IsKnown) throw new RequiredFactException("commitment_completion:" + commitment.Id + ":" + id);
                at = Math.Max(at, allocation.End);
            }
            return at;
        }

        private void ApplyExternal(double time)
        {
            while (nextExternal < timedExternal.Length && timedExternal[nextExternal].Change.Day * GameTime.TicksPerDay <= time)
            {
                var item = timedExternal[nextExternal++]; var change = item.Change; var at = change.Day * GameTime.TicksPerDay;
                Expire(at);
                if (string.IsNullOrEmpty(change.Resource.Value)) { ApplyNutrition(item.Owner + "/" + nextExternal, change.Units, at); continue; }
                if (change.Units > 0)
                {
                    var id = item.Owner + "/output/" + change.Resource + "/" + nextExternal;
                    var uses = context.FoodUses.Where(u => u.Resource == change.Resource.Value).ToArray();
                    var edible = items.Nutrition.GetValueOrDefault(change.Resource) > 0;
                    // A use whose resource never rots keeps the lot forever; with no observed use its shelf life is unread.
                    var fresh = uses.Length > 0 ? Known(uses.Min(u => u.FreshTicks is FoodFreshness.Rots(var keeps) ? keeps : double.PositiveInfinity)) : default;
                    RegisterOutput(id, change.Resource, edible, fresh, at);
                    lots[(id, "external", change.Resource)] = Known(change.Units);
                }
                else DebitLots(change.Resource, -change.Units);
                var before = balance.GetValueOrDefault(change.Resource, Known(0.0));
                balance[change.Resource] = before.IsKnown ? Known(before.Value + change.Units) : default;
                if (before.IsKnown && before.Value + change.Units < 0)
                    Refuse("external_stock_shortfall", item.Owner);
            }
        }

        private void Lose((string Lot, string Location, Resource Resource) lot, double at, string reason)
        {
            if (!lots[lot].TryGetValue(out var count) || count <= 0) return;
            lots[lot] = Known(0.0);
            if (!outsidePhysical.Contains(lot.Lot))
            {
                var before = balance.GetValueOrDefault(lot.Resource, Known(0.0));
                balance[lot.Resource] = before.IsKnown ? Known(before.Value - count) : default;
                effects.Add((lot.Resource, at / GameTime.TicksPerDay, Known(-count)));
            }
            Finding(reason, lot.Lot);
        }

        private void Expire(double time)
        {
            ConsumeOthers(time);
            expiredKeys.Clear();
            foreach (var k in lots.Keys)
                if (expiry.TryGetValue(k.Lot, out var until) && until.IsKnown && until.Value <= time) expiredKeys.Add(k);
            foreach (var lot in expiredKeys) Lose(lot, expiry[lot.Lot].Value, "observed_lot_expired");
            if (decaying.Count == 0) return;
            // Expected deterioration, from the planned rooms closed and roofed by completions so far (a later completion
            // changes the rate only after its own tick).
            var rooms = ForecastRoofConsequences.PlannedClosedRooms(context, finishedAt);
            foreach (var (id, stack) in decaying)
            {
                if (decayed.Contains(id)) continue;
                var at = ForecastRoofConsequences.DeterioratedAt(stack, context.Needs, rooms, time);
                if (at is null) { decayed.Add(id); Finding("food_deterioration_unassessed", id); continue; }
                if (!double.IsFinite(at.Value)) continue;
                decayed.Add(id);
                decayKeys.Clear();
                foreach (var k in lots.Keys) if (k.Lot == id) decayKeys.Add(k);
                foreach (var lot in decayKeys) Lose(lot, at.Value, "observed_lot_deteriorated");
            }
        }

        private ForecastNeedChoice Choice(ForecastNeedTask task, ForecastActivityAllocation a, double gain)
        {
            var key = new RefPair(task, a);
            if (!choiceRows.TryGetValue(key, out var choice))
                choiceRows.Add(key, choice = new(task.Task, task.Pawn, task.Kind, task.Target, task.Resource.Value, task.Count,
                    a.Depart, Math.Min(a.End, a.Start + task.ServiceDelay), a.End, gain, task.Thoughts));
            return choice;
        }

        /// <summary>
        /// The observed sleep a pawn asleep now keeps when the outcome leaves it to native (#3882), or null when the outcome wakes
        /// it at tick 0: an order interrupts the lie-down, and the binding orders the pawns
        /// CoordinatedActivityBinding.OrderedAtStart names for the outcome as the review dispatches it (#3884, #3889). Left alone,
        /// Toils_LayDown keeps it asleep until RestUtility.ShouldWakeUp (its rest at the wake threshold, at once while a hediff
        /// blocks sleeping) or until the schedule has it leave for its first task, gaining its lie-down's rest. No order holds it
        /// waiting at a service the schedule departs it for at once, so it leaves for a service at its planned start less travel
        /// (the caller departs the service then), and for other work at its departure. A deathrest, which ShouldWakeUp never ends
        /// and whose lie-down never looks for other jobs (JobDriver_Deathrest's LayDown, lookForOtherJobs false), goes on until it
        /// ends (<see cref="DeathrestEnd"/>, #3883) whatever the schedule.
        /// </summary>
        private ForecastNeedChoice? ObservedSleep(int p, NeedsForecastPawn pawn, List<ForecastActivityAllocation> own,
            ILookup<string, ForecastNeedTask> needTasksByTask, IReadOnlySet<string> ordered, IReadOnlyList<ForecastClosedRoom> rooms, double horizon)
        {
            if (ordered.Contains(pawn.Pawn)) return null;
            var lying = pawn.LyingIn ?? throw new RequiredFactException("lie-down:" + pawn.Pawn);
            var rate = lying.Place.GainPerTick;
            if (!double.IsFinite(rate) || rate <= 0) throw new RequiredFactException("lie-down rest gain:" + pawn.Pawn);
            double end;
            if (lying.Deathrest is not LyingInDeathrest.None)
            {
                end = DeathrestEnd(lying.Deathrest, rooms, horizon, reason => Finding(reason, pawn.Pawn))
                    ?? throw new RequiredFactException("deathrest sunlight past the scanned year:" + pawn.Pawn);
                if (end < horizon) Finding("deathrest_chamber_thought_at_wake_not_modelled", pawn.Pawn);
            }
            else
            {
                if (lying.HediffBlocksSleeping) return null;
                var until = horizon;
                foreach (var a in own)
                    until = Math.Min(until, needTasksByTask.Contains(a.Activity) ? Math.Max(a.Depart, a.Start - a.TravelTicks) : a.Depart);
                var rest = pawn.Needs.SingleOrDefault(n => n.Kind == "rest") ?? throw new RequiredFactException("rest need:" + pawn.Pawn);
                end = Math.Min(until, Math.Max(0, pawn.WakeThreshold - rest.Level) / rate);
            }
            if (end <= 0) return null;
            if (!observedSleeps.TryGetValue((p, end), out var choice))
                observedSleeps.Add((p, end), choice = new("need/" + pawn.Pawn + "/observed_sleep", pawn.Pawn, "rest", lying.Place.Bed, "", 0,
                    0, 0, end, rate * end, []));
            return choice;
        }

        /// <summary>
        /// The tick a deathrest observed now ends, capped at <paramref name="horizon"/> (#3883, <see cref="LyingInDeathrest"/>). A paused
        /// one holds to the horizon. An advancing one ends at its auto-wake (<see cref="LyingInDeathrest.Wakes"/>), or at the
        /// horizon when held, unless its bed touches sunlight first: at the first sunlit check, when one of the bed's unroofed cells
        /// is still open then (the schedule's planned rooms roof a cell from their RoofedAt, <see cref="ForecastRoofConsequences.CellStateAt"/>).
        /// A roof once done stays, so a bed the planned roof covers by then never wakes so. The sunlight forecast leaves the weather out,
        /// which <paramref name="finding"/> names. Null when the bed stays unlit through native's scan but the horizon reaches past it.
        /// </summary>
        internal static double? DeathrestEnd(LyingInDeathrest deathrest, IReadOnlyList<ForecastClosedRoom> rooms, double horizon, Action<string> finding)
        {
            // Paused: no progress and no sunlight check while it lasts.
            if (deathrest is not (LyingInDeathrest.Wakes or LyingInDeathrest.Held)) return horizon;
            var (wake, sunlight) = deathrest is LyingInDeathrest.Wakes w ? (Math.Min(horizon, w.InTicks), w.Sunlight)
                : (horizon, ((LyingInDeathrest.Held)deathrest).Sunlight);
            if (sunlight is DeathrestSunlight.Shaded) return wake;
            finding("deathrest_sunlight_weather_not_modelled");
            if (sunlight is DeathrestSunlight.Unlit { ThroughTicks: var through }) return wake <= through ? wake : null;
            var (at, cells) = sunlight is DeathrestSunlight.Sunlit s ? (s.InTicks, s.Unroofed)
                : throw new ArgumentOutOfRangeException(nameof(deathrest), sunlight, "deathrest sunlight");
            if (at >= wake) return wake;
            var open = new NeedCell(false, false, false, true, true);
            return cells.Any(c => !ForecastRoofConsequences.CellStateAt(open, c, at, rooms).Roofed) ? at : wake;
        }

        private ForecastOccupancy Occupancy(ForecastActivityAllocation a)
        {
            if (!occupancyRows.TryGetValue(a, out var row))
                occupancyRows.Add(a, row = new(a.Activity, a.Pawn, Known(a.Depart), Known(a.End), ForecastOccupancyState.Active));
            return row;
        }

        private SupportDelivery? Delivery(Methods.ActivityTaskCandidate binding)
        {
            if (deliveryRows.TryGetValue(binding, out var delivery)) return delivery;
            foreach (var d in context.Support.Deliveries)
                if (d.Demand == binding.SupportDemand && d.Target == binding.Target) { delivery = d; break; }
            deliveryRows.Add(binding, delivery);
            return delivery;
        }

        /// <summary>A binding the support consequences read: a demand's service or delivery, or bench work.</summary>
        private static bool Serves(Methods.ActivityTaskCandidate binding)
            => binding.SupportDemand.Length != 0 || binding.Kind is PawnActivityKind.Recipe or PawnActivityKind.Deliver;

        /// <summary>One pawn's exposure over its given allocations against the shelters the given prerequisites raise, with its comfort shifts.</summary>
        private Cached<ForecastThermalExposure> ExposureAt(int p, int allocationsId, int sheltersId, int comfortId, Dictionary<string, ForecastActivity> tasks,
            ForecastActivityAllocation[] physical, EquatableArray<ForecastShelterOutcome> shelters, double horizon)
        {
            var key = (p, allocationsId, sheltersId, comfortId);
            if (exposures.TryGetValue(key, out var exposure)) return exposure;
            exposures.Add(key, exposure = Exposure(ForecastThermalExposure.PawnExposureOf(context, tasks, index.Pawns[p].Pawn, allocs.Array(allocationsId), physical,
                shelters, horizon, comfortKeys.Array(comfortId), spanScratch)));
            return exposure;
        }

        /// <summary>One pawn's exposure over its given allocations against the rooms that already stand, which no schedule changes.</summary>
        private Cached<ForecastThermalExposure> UnchangedAt(int p, int allocationsId, Dictionary<string, ForecastActivity> tasks, double horizon)
        {
            var key = (p, allocationsId);
            if (unchangedExposures.TryGetValue(key, out var exposure)) return exposure;
            var (_, existingContext, existingShelters) = Existing(context, horizon);
            unchangedExposures.Add(key, exposure = Exposure(ForecastThermalExposure.PawnExposureOf(existingContext, tasks, index.Pawns[p].Pawn, allocs.Array(allocationsId),
                allocs.Array(allocationsId), existingShelters, horizon, comfortKeys.Array(0), spanScratch)));
            return exposure;
        }

        /// <summary>One plan's outcome: the unit of the comparison's evaluation budget (#3722). The caller draws the unit before calling
        /// (<see cref="ForecastSearchLedger.TryStartEvaluation"/>); the recursion that retries the plan without rejected work
        /// (<paramref name="unavailableWork"/>) is part of the same unit.</summary>
        internal ForecastCoupledOutcome Evaluate(ColonyForecastRequest request, IReadOnlyDictionary<string, ForecastCommitment> commitments,
            ForecastActivityProblem problem, ForecastActivityPlan plan, HashSet<string>? unavailableWork = null)
        {
            context = request.Coupled!;
            var horizon = request.HorizonDays * GameTime.TicksPerDay;
            var tasks = Tasks(problem);
            var pawns = Pawns(context, horizon);
            findings.Clear(); allocations.Clear(); completed.Clear(); lots.Clear(); expiry.Clear(); effects.Clear(); finishedAt.Clear(); decayed.Clear(); canonical.Clear();
            feasible = Known(true);
            foreach (var a in plan.Allocations)
            {
                if (!allocationsByValue.TryGetValue(a, out var row)) allocationsByValue.Add(a, row = a);
                canonical.Add(row);
                if (!allocations.TryAdd(a.Activity, row))
                    throw new DomainException("schedule allocates one activity more than once: " + a.Activity + " to "
                        + string.Join(",", plan.Allocations.Where(x => x.Activity == a.Activity).Select(x => x.Pawn + "@" + x.Start)));
            }
            // The outcome's commitments are its selection, dispatched as the review dispatches it (#3889). An outcome the review would
            // not start is one the autopilot never runs, so it is refused: the binding starts none of a dispatch with an unbound placed
            // allocation, and a review that releases an incumbent it cannot release dispatches nothing.
            var (dispatchedIds, dispatched) = context.IncumbentHold.Dispatched(commitments.Keys, plan.Allocations, tasks);
            if (Methods.Binding.CoordinatedActivityBinding.Unbound(dispatchedIds, dispatched, context.Bindings, pawns.Unplaced) is { } unbound)
                Refuse("dispatch_allocation_unbound", unbound.Activity);
            foreach (var incumbent in context.IncumbentHold.Unreleased(commitments.Keys, plan.Allocations, context.Bindings))
                Refuse("incumbent_release_unavailable", incumbent.Commitment);
            foreach (var l in context.InitialLots)
            {
                lots.Add((l.Lot, l.Location, l.Resource), l.Count);
                if (l.Perishable) expiry.TryAdd(l.Lot, l.FreshTicks);
            }
            InitializeCensus(request);
            var holds = Materials(request, commitments);
            billRows.Clear();
            foreach (var a in plan.Allocations)
                if (context.Bindings.TryGetValue((a.Activity, a.Pawn), out var b) && (b.Kind == PawnActivityKind.Recipe || b.Kind == PawnActivityKind.Work && b.Job == "DoBill")
                    && b.Recipe is { Bill.Length: > 0 }) billRows.Add(b);
            var billsId = bills.Id(billRows);
            if (!budgets.TryGetValue(billsId, out var material))
            {
                var budget = ForecastCoupledMaterials.CoupledMaterialBudgetOf(request, holds, tasks, plan);
                budgets.Add(billsId, material = (budget, budget.TryGetValue(out var rows) ? [.. rows] : []));
            }
            balance.Clear();
            foreach (var row in material.Rows) balance.Add(row.Key, row.Value);
            var physicalKnown = material.Budget.IsKnown;
            // Hands owns exact targets for the whole Method, including future services. Claims on one target conflict by
            // admission's own rule (TargetUseRules, #3626): a target admits at most MaxPawns distinct owners at once (an
            // unreadable MaxPawns is read as 1), a haul from a stack holds it alone over its schedule, an eat holds its
            // stack over its own, any other claim holds its target throughout, and the stack quantity bounds the summed claims.
            var (needTasksByTask, _) = NeedTasks(context);
            var (observedTargets, decayingStacks, stocks) = Targets(context);
            decaying = decayingStacks;
            claims.Clear();
            foreach (var n in context.NeedTasks)
                if (n.Target.Length != 0 && allocations.TryGetValue(n.Task, out var eat))
                    claims.Add(new(n.Target, n.Pawn, n.Count, n.MaxPawns, n.AvailableCount, n.Batch, n.Task, n.Kind == "food"
                        ? TargetUseRules.Scheduled(n.Pawn, n.Count, 0, eat.Depart, eat.End, false) : new(n.Pawn, n.Count, long.MinValue, long.MaxValue, false)));
            var needClaims = claims.Count;
            foreach (var a in plan.Allocations)
            {
                if (!context.Bindings.TryGetValue((a.Activity, a.Pawn), out var b) || !observedTargets.TryGetValue(b.Target, out var maxPawns)) continue;
                var claimed = false;
                for (var i = 0; i < needClaims && !claimed; i++) claimed = claims[i].Task == b.Id && claims[i].Pawn == b.Pawn;
                if (claimed) continue;
                if (b.Kind is PawnActivityKind.Deliver or PawnActivityKind.Supply && stocks.TryGetValue(b.Target, out var stock))
                    claims.Add(new(b.Target, b.Pawn, b.Count, maxPawns, stock, "", b.Id, TargetUseRules.Scheduled(b.Pawn, b.Count, 0, a.Depart, a.End, true)));
                else claims.Add(new(b.Target, b.Pawn, 0, maxPawns, default, "", b.Id, new(b.Pawn, 0, long.MinValue, long.MaxValue, false)));
            }
            for (var i = 0; i < claims.Count; i++)
            {
                var target = claims[i].Target;
                var first = true;
                for (var j = 0; j < i && first; j++) first = claims[j].Target != target;
                if (!first) continue;
                var capacity = int.MaxValue;
                uses.Clear();
                for (var j = i; j < claims.Count; j++)
                {
                    var c = claims[j];
                    if (c.Target != target) continue;
                    capacity = Math.Min(capacity, c.MaxPawns.TryGetValue(out var max) ? max : 1);
                    uses.Add(c.Use);
                }
                if (TargetUseRules.OwnersConflict(CollectionsMarshal.AsSpan(uses), capacity)) Refuse("conflicting_selected_target_owners", target);
                for (var j = i; j < claims.Count; j++)
                {
                    var c = claims[j];
                    if (c.Target != target || c.Count <= 0 || !c.AvailableCount.IsKnown) continue;
                    var firstStack = true;
                    for (var k = i; k < j && firstStack; k++)
                        firstStack = !(claims[k].Target == target && claims[k].Count > 0 && claims[k].AvailableCount.IsKnown && claims[k].Stack == c.Stack);
                    if (!firstStack) continue;
                    var available = double.PositiveInfinity;
                    uses.Clear();
                    for (var k = j; k < claims.Count; k++)
                    {
                        var d = claims[k];
                        if (d.Target != target || d.Count <= 0 || !d.AvailableCount.IsKnown || d.Stack != c.Stack) continue;
                        uses.Add(d.Use); available = Math.Min(available, d.AvailableCount.Value);
                    }
                    if (TargetUseRules.UnitsConflict(CollectionsMarshal.AsSpan(uses), Known(available))) Refuse("target_stack_quantity_exceeded", target);
                }
            }
            deliveries.Clear();
            foreach (var a in plan.Allocations)
            {
                if (!context.Bindings.TryGetValue((a.Activity, a.Pawn), out var b) || b.Kind != PawnActivityKind.Deliver || b.SupportDemand.Length == 0) continue;
                if (Delivery(b) is { Bill.Length: > 0 } delivery) deliveries.Add(delivery);
            }
            for (var i = 0; i < deliveries.Count; i++)
            {
                var bill = deliveries[i].Bill;
                var first = true;
                for (var j = 0; j < i && first; j++) first = deliveries[j].Bill != bill;
                if (!first) continue;
                var resources = 0;
                for (var j = i; j < deliveries.Count; j++)
                {
                    if (deliveries[j].Bill != bill) continue;
                    var seen = false;
                    for (var k = i; k < j && !seen; k++) seen = deliveries[k].Bill == bill && deliveries[k].Resource == deliveries[j].Resource;
                    if (!seen) resources++;
                }
                if (resources > 1) Refuse("conflicting_bill_material_choices", bill);
            }
            var timed = External(commitments, request.HorizonDays);
            var scheduled = context.ModelBasis?.Policy.Scheduled.Where(e => e.Day <= request.HorizonDays && (e.Stock == ModelStock.Nutrition || !string.IsNullOrEmpty(e.Resource.Value)))
                .Select(e => (Owner: "scheduled/" + e.Id, Change: new ForecastStockChange(e.Resource, e.Day, ScheduledUnits(e)))).ToArray() ?? [];
            if (scheduled.Length > 0)
            {
                timed = timed.Concat(scheduled).OrderBy(e => e.Change.Day).ThenBy(e => e.Owner, StringComparer.Ordinal).ToArray();
                effects.AddRange(scheduled.Where(e => !string.IsNullOrEmpty(e.Change.Resource.Value)).Select(e => (e.Change.Resource, e.Change.Day, Known(e.Change.Units))));
            }
            timedExternal = timed; nextExternal = 0;
            // Observed food stacks that deteriorate (#3339), and the completion tick of every completed activity, which places the
            // planned rooms this schedule closes and roofs.
            if (decaying.Count > 0) Finding("deterioration_rain_and_other_stat_parts_held", "deterioration");
            // Completion precedes dependent start for positive-duration work; zero-duration tasks cannot finance themselves.
            // (Time, order, activity) is a total order: an activity is allocated once, and its start and end orders differ.
            var events = Events;
            events.Clear();
            foreach (var a in canonical)
            {
                events.Add(new(a.Start, 1, true, a));
                events.Add(new(a.End, a.End > a.Start ? 0 : 2, false, a));
            }
            events.Sort(static (x, y) => x.Time != y.Time ? x.Time.CompareTo(y.Time) : x.Order != y.Order ? x.Order.CompareTo(y.Order)
                : string.CompareOrdinal(x.Allocation.Activity, y.Allocation.Activity));
            foreach (var e in events)
            {
                if (e.Time > horizon) { Finding("beyond_horizon", e.Allocation.Activity); continue; }
                ApplyExternal(e.Time);
                Expire(e.Time);
                var task = tasks[e.Allocation.Activity];
                if (e.Start)
                {
                    if (unavailableWork is not null && unavailableWork.Contains(task.Id))
                    { completed[task.Id] = Known(false); Refuse("work_outside_recovered_capacity", task.Id); continue; }
                    bool failed = false, unknownPrerequisite = false;
                    foreach (var prerequisite in task.Prerequisites)
                    {
                        var done = completed.GetValueOrDefault(prerequisite);
                        failed |= done == Known(false); unknownPrerequisite |= !done.IsKnown;
                    }
                    if (failed)
                    { completed[task.Id] = Known(false); Refuse("failed_prerequisite", task.Id); continue; }
                    if (unknownPrerequisite)
                    { completed[task.Id] = default; Uncertain("unknown_prerequisite", task.Id); continue; }
                    var shape = Shape(task, needTasksByTask);
                    if (shape.Ingestion is not null)
                    {
                        // Physical lot accounting below owns quantity and dependency completion owns access.
                        // Ingestion additionally needs the selected actual lot fresh through chewing.
                        bool unknownExpiry = shape.FoodLots.Length == 0, stale = false;
                        foreach (var lot in shape.FoodLots)
                        {
                            var fresh = expiry.GetValueOrDefault(lot);
                            unknownExpiry |= !fresh.IsKnown; stale |= fresh.IsKnown && e.Allocation.End >= fresh.Value;
                        }
                        if (unknownExpiry)
                        { completed[task.Id] = default; Uncertain("unknown_food_access_or_expiry", task.Id); continue; }
                        if (stale)
                        { completed[task.Id] = Known(false); Refuse("food_unavailable_at_ingestion", task.Id); continue; }
                        // Nor may it deteriorate away before the meal ends, at the rate the completions so far leave it (#3339).
                        if (decaying.Count != 0)
                        {
                            var rooms = ForecastRoofConsequences.PlannedClosedRooms(context, finishedAt);
                            var gone = false;
                            foreach (var lot in shape.FoodLots)
                                gone |= decaying.TryGetValue(lot, out var stack)
                                    && ForecastRoofConsequences.DeterioratedAt(stack, context.Needs, rooms, e.Allocation.End) is { } at && at <= e.Allocation.End;
                            if (gone)
                            { completed[task.Id] = Known(false); Refuse("food_deteriorated_before_ingestion", task.Id); continue; }
                        }
                    }
                    bool unknownMaterial = !physicalKnown && shape.Inputs.Length != 0, shortfall = false;
                    foreach (var input in shape.Inputs) unknownMaterial |= !balance.GetValueOrDefault(input.Resource, Known(0.0)).IsKnown;
                    if (unknownMaterial)
                    { completed[task.Id] = default; Uncertain("unknown_timed_material", task.Id); continue; }
                    foreach (var input in shape.Inputs) shortfall |= balance.GetValueOrDefault(input.Resource, Known(0.0)).Value < input.Units;
                    if (shortfall)
                    { completed[task.Id] = Known(false); Refuse("timed_material_shortfall", task.Id); continue; }
                    bool unknownIngredient = false, expired = false;
                    foreach (var lot in shape.InputLots)
                        if (expiry.TryGetValue(lot, out var fresh)) { unknownIngredient |= !fresh.IsKnown; expired |= fresh.IsKnown && fresh.Value <= e.Time; }
                    if (unknownIngredient)
                    { completed[task.Id] = default; Uncertain("unknown_ingredient_expiry", task.Id); continue; }
                    if (expired)
                    { completed[task.Id] = Known(false); Refuse("ingredient_expired_before_service", task.Id); continue; }
                    bool unknownLot = false, lotShortfall = false;
                    foreach (var input in shape.LotInputs)
                    {
                        var held = lots.GetValueOrDefault(input.Key, Known(0.0));
                        unknownLot |= !held.IsKnown; lotShortfall |= held.IsKnown && held.Value < input.Units;
                    }
                    if (unknownLot)
                    { completed[task.Id] = default; Uncertain("unknown_source_lot", task.Id); continue; }
                    if (lotShortfall)
                    { completed[task.Id] = Known(false); Refuse("source_lot_shortfall", task.Id); continue; }
                    foreach (var input in shape.LotInputs) lots[input.Key] = Known(lots.GetValueOrDefault(input.Key, Known(0.0)).Value - input.Units);
                    foreach (var input in shape.Inputs)
                    {
                        var unnamed = input.Units - shape.LotInputs.Where(l => l.Key.Resource == input.Resource).Sum(l => l.Units);
                        if (unnamed > 0) DebitLots(input.Resource, unnamed);
                    }
                    completed[task.Id] = Known(true);
                    foreach (var input in shape.Inputs)
                    {
                        balance[input.Resource] = Known(balance.GetValueOrDefault(input.Resource, Known(0.0)).Value - input.Units);
                        effects.Add((input.Resource, e.Time / GameTime.TicksPerDay, Known(-input.Units)));
                    }
                }
                else if (completed.GetValueOrDefault(task.Id) == Known(true))
                {
                    finishedAt[task.Id] = e.Time;
                    foreach (var output in task.Outputs)
                    {
                        var outputId = output.Lot.Length == 0 ? task.Id + "/output/" + output.Resource : output.Lot;
                        RegisterOutput(outputId, output.Resource, output.Perishable, Known(output.FreshTicks), e.Time);
                        if (output.Lot.Length == 0)
                        {
                            var key = (outputId, task.EndLocation, output.Resource);
                            var beforeLot = lots.GetValueOrDefault(key, Known(0d));
                            lots[key] = beforeLot.IsKnown ? Known(beforeLot.Value + output.Units) : default;
                        }
                        if (output.Lot.Length != 0)
                        {
                            if (output.Perishable && !expiry.ContainsKey(output.Lot)) expiry[output.Lot] = Known(e.Time + output.FreshTicks);
                            var key = (output.Lot, output.LotLocation.Length == 0 ? task.EndLocation : output.LotLocation, output.Resource);
                            var beforeLot = lots.GetValueOrDefault(key, Known(0.0));
                            lots[key] = beforeLot.IsKnown ? Known(beforeLot.Value + output.Units) : default;
                        }
                        var before = balance.GetValueOrDefault(output.Resource, Known(0.0));
                        balance[output.Resource] = before.IsKnown ? Known(before.Value + output.Units) : default;
                        effects.Add((output.Resource, e.Time / GameTime.TicksPerDay, Known(output.Units)));
                    }
                }
            }
            ApplyExternal(horizon);
            Expire(horizon);
            // A commitment's mood memories, situational changes, comfort shifts and outfit changes take effect when all its activities
            // have completed inside the horizon, or at the forecast start when it has none. Failed or later work changes nothing here.
            var byName = pawns.Index;
            for (var p = 0; p < pawns.Pawns.Length; p++)
            {
                pawns.Granted[p].Clear(); pawns.Situational[p].Clear(); pawns.Comfort[p].Clear(); pawns.Allocations[p].Clear(); pawns.Use[p].Clear();
            }
            List<ForecastEffectiveTick>? outfitTicks = null;
            foreach (var commitment in Granting(commitments))
            {
                var at = CompletedAt(commitment, horizon);
                foreach (var row in commitment.MoodMemories)
                {
                    if (!byName.TryGetValue(row.Pawn, out var p)) continue;
                    if (at <= horizon) pawns.Granted[p].Add((row.Memory, at));
                }
                foreach (var row in commitment.SituationalChanges)
                {
                    if (!byName.TryGetValue(row.Pawn, out var p)) continue;
                    if (at <= horizon) pawns.Situational[p].Add((row.Thought, row.OffsetDelta, at));
                }
                foreach (var row in commitment.ComfortShifts)
                {
                    if (!byName.TryGetValue(row.Pawn, out var p)) continue;
                    if (at <= horizon) pawns.Comfort[p].Add((row.MinDelta, row.MaxDelta, at));
                }
                // An outfit change with activities (a crafted garment, #3621) is priced by the harm term from this tick.
                if (commitment.OutfitChanges.Count > 0 && commitment.ActivityCompletions.Count > 0)
                    (outfitTicks ??= []).Add(new(commitment.Id, at));
            }
            // The completed rows each later result reads, interned: shelters read their prerequisites, planned rooms and
            // development the development tasks by pawn, support its services; the need services are the use rows.
            completedRows.Clear(); shelterRows.Clear(); serviceRows.Clear(); useRows.Clear(); developmentRows.Clear();
            foreach (var a in canonical)
            {
                if (completed.GetValueOrDefault(a.Activity) == Known(true))
                {
                    completedRows.Add(a);
                    if (pawns.ShelterTasks.Contains(a.Activity)) shelterRows.Add(a);
                    if (pawns.DevelopmentTasks.Contains(a.Activity)) developmentRows.Add((a.Activity, a.Pawn));
                    if (context.Bindings.TryGetValue((a.Activity, a.Pawn), out var b) && Serves(b)) serviceRows.Add(a);
                }
                var use = needTasksByTask.Contains(a.Activity);
                if (use) useRows.Add(a);
                if (byName.TryGetValue(a.Pawn, out var p)) { pawns.Allocations[p].Add(a); if (use) pawns.Use[p].Add(a); }
            }
            // A completed range drill's company memory (TrainingCompany.Finished and NearTrainer, #3871): another colonist is on
            // its own completed session at a target within chat range of the stand when the drill finishes.
            foreach (var drill in context.PracticeTasks)
            {
                if (drill.Worker.Skill != "Shooting" || drill.Worker.Thoughts.Count == 0 || drill.Worker.Company.Count == 0
                    || !byName.TryGetValue(drill.Worker.Pawn, out var p)
                    || completedRows.FirstOrDefault(r => r.Activity == drill.Task) is not { } a || a.End > horizon) continue;
                if (context.PracticeTasks.Any(other => other.Worker.Pawn != drill.Worker.Pawn && drill.Worker.Company.Contains(other.Worker.Target)
                        && completedRows.Any(b => b.Activity == other.Task && b.Start <= a.End && a.End <= b.End)))
                    foreach (var thought in drill.Worker.Thoughts) pawns.Granted[p].Add((thought, a.End));
            }
            var completedId = allocs.Id(completedRows);
            var sheltersId = allocs.Id(shelterRows);
            if (!sheltersByPrerequisites.TryGetValue(sheltersId, out var shelters))
                sheltersByPrerequisites.Add(sheltersId, shelters = ForecastShelterPolicy.ForecastShelters(context.Shelters, allocations, completed, horizon, shelterScratch));
            // Comfort shifts change the pawn's own exposure, not what a shelter relieves (relief below is outfit-neutral).
            exposureRows.Clear();
            for (var p = 0; p < pawns.Pawns.Length; p++)
            {
                pawns.Exposure[p] = ExposureAt(p, allocs.Id(pawns.Allocations[p]), sheltersId, pawns.Comfort[p].Count == 0 ? 0 : comfortKeys.Id(pawns.Comfort[p]), tasks, plan.Allocations, shelters, horizon);
                exposureRows.Add(pawns.Exposure[p].Value);
            }
            var exposure = exposureKeys.Canonical(exposureKeys.Id(exposureRows));
            // Temperature mood (#3327): each Live temperature card along the pawn's assessed exposure spans, comfort shifts included.
            var temperatureCards = TemperatureCards(context);
            var needsByPawn = new Dictionary<string, ForecastNeedsResult>(pawns.Pawns.Length, StringComparer.Ordinal);
            var closedRooms = ForecastRoofConsequences.PlannedClosedRooms(context, finishedAt);
            var service = PlannedRoomCredits.ServiceDays(context, finishedAt, horizon);
            // A bedroom occupant's mood also integrates breaks at its move's shift (#3655), a wearer's at its garments' (#3816),
            // a colonist's at the corpse mood a morgue or tomb removes (#3806); constant per pawn, so the mood key holds.
            var breakShifts = PlannedRoomCredits.BreakShifts(context);
            HashSet<string>? rejectedWork = null;
            IReadOnlySet<string>? ordered = null;
            // Whom the binding orders at tick 0, on the outcome as the review dispatches it.
            IReadOnlySet<string> Ordered() => Methods.Binding.CoordinatedActivityBinding.OrderedAtStart(dispatchedIds, dispatched, context.Bindings, pawns.Unplaced);
            // Per pawn, the cached timelines are looked up by their inputs; a timeline's capacity input is built only on a miss (#3468).
            for (var p = 0; p < pawns.Pawns.Length; p++)
            {
                var facts = pawns.Pawns[p]; var pawn = facts.Pawn;
                choices.Clear(); productive.Clear(); protectedTime.Clear();
                foreach (var task in facts.NeedTasks)
                {
                    // A task that starts past the horizon (named beyond_horizon) or fails (its refusal names why) recovers nothing
                    // here (#3881); every fact that settles the rest is read, so an unknown completion is an unread fact.
                    if (!allocations.TryGetValue(task.Task, out var a) || !completed.TryGetValue(task.Task, out var done) || done == Known(false)) continue;
                    if (!done.IsKnown) throw new RequiredFactException("need_completion:" + task.Task);
                    choices.Add(Choice(task, a, task.Gain));
                }
                if (!pawn.Awake && !choices.Any(c => c.Kind == "rest" && c.Start == 0)
                    && ObservedSleep(p, pawn, pawns.Allocations[p], needTasksByTask,
                        ordered ??= Ordered(), closedRooms, horizon) is { } observedSleep)
                {
                    // Left asleep, the pawn departs for a service the schedule departed it for at once when the sleep ends (#3884).
                    for (var c = 0; c < choices.Count; c++)
                        if (choices[c].Depart < observedSleep.End)
                        {
                            if (!departuresAfterSleep.TryGetValue((choices[c], observedSleep.End), out var departed))
                                departuresAfterSleep.Add((choices[c], observedSleep.End), departed = choices[c] with { Depart = observedSleep.End });
                            choices[c] = departed;
                        }
                    choices.Add(observedSleep);
                }
                foreach (var a in pawns.Allocations[p])
                {
                    if (needTasksByTask.Contains(a.Activity)) continue;
                    if (!pawns.Nonproductive.Contains(a.Activity)) productive.Add(a);
                    protectedTime.Add(Occupancy(a));
                }
                if (facts.Journal is { } journal) protectedTime.Add(journal);
                var occupancyId = context.Resources.Claims.Truncated ? -1 : occupancies.Id(protectedTime);
                var choicesId = choiceKeys.Id(choices);
                // Needs recovery is assessed without masking physiological unavailability beneath planned work.
                var physiology = NeedsAt(p, 0, choicesId, 0, 0, null, facts, horizon);
                if (physiology.Value.Capacity.Capacity.Windows is not ForecastCapacityWindows.Read(var able))
                { if (productive.Count != 0 || choices.Count != 0) Uncertain("unknown_future_capacity", pawn.Pawn); }
                else foreach (var work in productive)
                {
                    var inside = false;
                    foreach (var w in able) inside |= w.Start <= work.Depart && w.End >= work.End;
                    if (inside) continue;
                    (rejectedWork ??= unavailableWork is null ? new(StringComparer.Ordinal) : new(unavailableWork, StringComparer.Ordinal)).Add(work.Activity);
                    completed[work.Activity] = Known(false); Refuse("work_outside_recovered_capacity", work.Activity);
                }
                // Room and roof thoughts (#3339): bed thoughts at the wake, weather, the outdoors need and undergrounder thoughts.
                // Roof mood reads the pawn's choices and the closed rooms; the rest is the search's context (#3468).
                var roofKey = (p, choicesId, Key(closedRooms));
                if (!roofs.TryGetValue(roofKey, out var roof))
                    roofs.Add(roofKey, roof = Next(ForecastRoofConsequences.RoofMoodOf(pawn, context.Needs, choiceKeys.Array(choicesId), closedRooms,
                        context.ColonistThoughts, horizon)));
                var roofMood = roof.Value;
                var grantedId = pawns.Granted[p].Count == 0 ? 0 : grantedKeys.Id(pawns.Granted[p]);
                var situationalId = pawns.Situational[p].Count == 0 ? 0 : situationalKeys.Id(pawns.Situational[p]);
                var bandedThoughts = Banded(p, pawns.Exposure[p], roof, temperatureCards, pawn, choicesId, physiology.Value.Needs);
                var renewalsKey = roofMood.Renewals.Count == 0 ? 0 : roof.Id + 1;
                var projectedNeeds = NeedsAt(p, occupancyId, choicesId, grantedId, renewalsKey, roofMood.Renewals, facts, horizon);
                // Mood reads neither occupancy nor capacity, so it is cached on its own inputs (#3468).
                var moodKey = (p, choicesId, grantedId, situationalId, bandedThoughts.Id, renewalsKey);
                if (!moods.TryGetValue(moodKey, out var mood))
                    moods.Add(moodKey, mood = Next(ForecastMood.ForecastMoodTimeline(pawn, horizon, choiceKeys.Array(choicesId), null,
                        grantedId == 0 ? null : grantedKeys.Array(grantedId), situationalId == 0 ? null : situationalKeys.Array(situationalId),
                        bandedThoughts.Value, roofMood.Renewals, context.MoodTrajectory, breakShifts.GetValueOrDefault(pawn.Pawn))));
                needsByPawn[pawn.Pawn] = Projected(projectedNeeds, mood, roof);
            }
            // Removing impossible work may invalidate its meals and therefore later work. Each pass removes at least one task.
            if (rejectedWork is not null && rejectedWork.Count > (unavailableWork?.Count ?? 0))
                return Evaluate(request, commitments, problem, plan, rejectedWork);
            // Ordinary production needs no training mandate. Only a claimed learning-dependent later
            // speedup requires evidence that its prerequisite practice produced useful food in this slice.
            // A terminal native batch can retain remainder; XP itself never improves the consequence vector.
            foreach (var allocation in plan.Allocations)
            {
                if (!tasks[allocation.Activity].RequiresFoodUse || allocation.Learning is not { } prior
                    || prior.After.AppliedGain <= prior.Before.AppliedGain) continue;
                var speedup = false;
                foreach (var later in plan.Allocations)
                    speedup |= later.Pawn == allocation.Pawn && later.Start >= allocation.End && later.Learning is { } learned && learned.WorkTicks < learned.ObservedWorkTicks;
                if (!speedup) continue;
                var useful = false;
                foreach (var n in context.NeedTasks)
                    if (n.Kind == "food" && n.FreshFrom == allocation.Activity && needsByPawn.TryGetValue(n.Pawn, out var result) && result.UsefulFoodGain.GetValueOrDefault(n.Task) > 0)
                    { useful = true; break; }
                if (!useful) Refuse("productive_output_without_useful_demand", allocation.Activity);
            }
            // The stock shortfalls, walked against the commitments' own effects.
            inventoryWalk.Run(Inventory(request, commitments), CollectionsMarshal.AsSpan(effects), findings);
            // Known stationary exposure of the need services against the completed shelters, and against the rooms that already stand.
            var useId = allocs.Id(useRows);
            var reliefKey = (useId, sheltersId);
            if (!reliefByUse.TryGetValue(reliefKey, out var relief))
            {
                var reliefKnown = false; double unchangedTicks = 0, changedTicks = 0;
                for (var p = 0; p < pawns.Pawns.Length; p++)
                {
                    var useAllocs = allocs.Id(pawns.Use[p]);
                    var changed = ExposureAt(p, useAllocs, sheltersId, 0, tasks, plan.Allocations, shelters, horizon).Value;
                    var unchanged = UnchangedAt(p, useAllocs, tasks, horizon).Value;
                    reliefKnown |= changed.AssessedTicks > 0;
                    if (changed.AssessedTicks != unchanged.AssessedTicks) { reliefKnown = false; break; }
                    unchangedTicks += unchanged.AssessedColdDegreeTicks + unchanged.AssessedHotDegreeTicks;
                    changedTicks += changed.AssessedColdDegreeTicks + changed.AssessedHotDegreeTicks;
                }
                string? unassessed = null;
                if (!reliefKnown && shelters.Count != 0)
                {
                    var changedUse = new string[pawns.Pawns.Length]; var unchangedUse = new string[pawns.Pawns.Length];
                    for (var p = 0; p < pawns.Pawns.Length; p++)
                    {
                        var useAllocs = allocs.Id(pawns.Use[p]);
                        var changed = ExposureAt(p, useAllocs, sheltersId, 0, tasks, plan.Allocations, shelters, horizon).Value;
                        var unchanged = UnchangedAt(p, useAllocs, tasks, horizon).Value;
                        changedUse[p] = changed.Pawn + "/" + Math.Round(changed.AssessedTicks);
                        unchangedUse[p] = unchanged.Pawn + "/" + Math.Round(unchanged.AssessedTicks);
                    }
                    unassessed = "thermal_relief_unassessed:changed=" + string.Join(",", changedUse) + ";unchanged=" + string.Join(",", unchangedUse) + ";shelters="
                        + string.Join(",", shelters.Select(s => (s.CompleteAt.IsKnown ? "c" : "-") + (s.TemperatureAtHorizon.IsKnown ? "t" : "-") + ":" + string.Join("|", s.Findings)));
                }
                reliefByUse.Add(reliefKey, relief = new(reliefKnown ? Known(unchangedTicks - changedTicks) : default, unassessed));
            }
            if (relief.Unassessed is { } unassessedRelief) Finding(unassessedRelief, "thermal");
            if (!maintenanceByCompleted.TryGetValue(completedId, out var maintenance))
                maintenanceByCompleted.Add(completedId, maintenance = ForecastMaintenancePolicy.ForecastMaintenance(context.MaintenanceRequirements, context.PracticeTasks, plan, completed, horizon));
            var developmentId = pairKeys.Id(developmentRows);
            if (!plannedByDevelopment.TryGetValue(developmentId, out var planned))
                plannedByDevelopment.Add(developmentId, planned = PlannedOf(plan));
            // A room with a service day (#3796) is assessed by it.
            foreach (var room in planned.Unassessed)
                if (!service.Any(r => "planned/room" + r.Room == room)) Finding("planned_room_effect_unassessed", room);
            var servicesId = allocs.Id(serviceRows);
            if (pawns.Support is not { } support && !supportByServices.TryGetValue(servicesId, out support))
                supportByServices.Add(servicesId, support = ForecastSupport.ForecastSupportConsequencesOf(context, plan, completed, horizon));
            if (!occupancyByUse.TryGetValue(useId, out var thermalOccupancy))
                occupancyByUse.Add(useId, thermalOccupancy = string.Join(";", context.NeedTasks.Where(n => allocations.ContainsKey(n.Task))
                    .Select(n => (Need: n, Allocation: allocations[n.Task]))
                    .OrderBy(p => p.Need.Pawn, StringComparer.Ordinal).ThenBy(p => p.Allocation.Arrive).ThenBy(p => p.Need.Kind, StringComparer.Ordinal)
                    .Select(p => FormattableString.Invariant($"{p.Need.Pawn}|{p.Need.Kind}|{tasks[p.Need.Task].Location}|{p.Allocation.Arrive:R}|{p.Allocation.End:R}"))));
            var outcome = new ForecastCoupledOutcome(plan, feasible, needsByPawn, EquatableArray.Create<ForecastFinding>(CollectionsMarshal.AsSpan(findings)))
            {
                Support = support,
                OutfitTicks = outfitTicks is null ? [] : EquatableArray.From(outfitTicks),
                Maintenance = maintenance,
                Shelters = shelters, ThermalExposure = exposure,
                ThermalOccupancy = thermalOccupancy,
                StationaryThermalRelief = relief.Relief,
                PlannedRooms = EquatableArray.Wrap(planned.Rooms),
                RequestedDevelopment = EquatableArray.Wrap(planned.Development),
                CompletedDevelopmentSites = EquatableArray.Wrap(planned.Sites),
                ServedRooms = service,
            };
            return outcome with { Terminal = new(request, commitments, new Dictionary<Resource, Fact<double>>(inventoryWalk.Terminal), new Dictionary<string, double>(finishedAt, StringComparer.Ordinal), holds)
                { Lots = TerminalLots(), FoodUnread = request.Facts.FoodUnread, MedicineComplete = request.Facts.Medicine.Items.IsKnown && physicalCensusComplete,
                    PhysicalCensusComplete = physicalCensusComplete } };
        }

        /// <summary>A planned site counts once it finishes inside the horizon; a room only when a whole alternative does (#3276).
        /// Requested development records each completed planned site once, whichever root built it, and a planned room's
        /// per-cell native roof as that room's one roof unit once every cell completes (#3334); the ranking counts no
        /// planned room's unit (#3828).</summary>
        private PlannedOutcome PlannedOf(ForecastActivityPlan plan)
        {
            var builtSites = context.PlannedSites.Where(s => s.Finishes.Any(f => completed.GetValueOrDefault(f) == Known(true)))
                .Select(s => s.Site).ToHashSet(StringComparer.Ordinal);
            var plannedRooms = context.PlannedSites.Where(s => builtSites.Contains(s.Site)).GroupBy(s => s.Room).OrderBy(g => g.Key)
                .Select(g => new ForecastPlannedRoomOutcome(g.Key, EquatableArray.From(g.Select(s => s.Site).Order(StringComparer.Ordinal)),
                    EquatableArray.From(context.PlannedRooms.Where(a => a.Room == g.Key && a.Sites.All(builtSites.Contains)).Select(a => a.Id))))
                .ToArray();
            var development = new SortedSet<string>(builtSites, StringComparer.Ordinal);
            foreach (var shelter in context.Shelters.Where(s => s.PlannedRoom != 0 && s.RoofCells.Count != 0))
                if (shelter.RoofCells.All(cell => cell.Any(f => completed.GetValueOrDefault(f) == Known(true))))
                    development.Add(ForecastPlannedSite.RoofUnit(shelter.PlannedRoom));
            var sites = plan.Allocations.Where(a => completed.GetValueOrDefault(a.Activity) == Known(true))
                .Select(a => context.Bindings.GetValueOrDefault((a.Activity, a.Pawn)))
                .Where(b => b is { Kind: PawnActivityKind.Work } && b.Job is "FinishFrame" or "BuildRoof")
                .Select(b => b!.Job == "BuildRoof" ? "roof/" + b.Location.X + "/" + b.Location.Z : "site/" + b.Target)
                .Distinct().Order(StringComparer.Ordinal).ToArray();
            return new(plannedRooms, [.. development], sites,
                plannedRooms.Where(r => r.Complete.Count == 0).Select(r => "planned/room" + r.Room).ToArray());
        }

        private Cached<ForecastNeedsResult> NeedsAt(int p, int occupancyId, int choicesId, int grantedId, int renewalsKey,
            IReadOnlyList<ForecastMemoryRenewal>? renewals, PawnFacts facts, double horizon)
        {
            var key = (p, occupancyId, choicesId, grantedId, renewalsKey);
            if (needs.TryGetValue(key, out var result)) return result;
            // The core reads no occupancy, so it is shared by every occupancy the schedules give the pawn (#3468).
            var coreKey = (p, choicesId, grantedId, renewalsKey);
            if (!cores.TryGetValue(coreKey, out var core))
                cores.Add(coreKey, core = ForecastNeeds.NeedsCoreOf(facts.Pawn, horizon, choiceKeys.Array(choicesId), null, grantedId == 0 ? null : grantedKeys.Array(grantedId), renewals));
            var input = facts.Input with { Occupancy = occupancyId < 0 ? default : Known(occupancies.Canonical(occupancyId)) };
            var capacityKey = (p, occupancyId);
            if (!capacities.TryGetValue(capacityKey, out var capacity)) capacities.Add(capacityKey, capacity = ForecastCapacity.ForecastPawnCapacityOf(input));
            needs.Add(key, result = Next(ForecastNeeds.NeedsWithCapacity(facts.Pawn, core, input, null, capacity)));
            return result;
        }

        /// <summary>A pawn's banded thoughts: each Live temperature card along its exposure spans, its roof mood's, then the
        /// food and rest thoughts along the needs timeline of its choices (<paramref name="needs"/>, #3598).</summary>
        private Cached<BandedThought[]> Banded(int p, Cached<ForecastThermalExposure> exposure, Cached<ForecastRoofMood> roof,
            MechanicCard[] temperatureCards, NeedsForecastPawn pawn, int choicesId, EquatableArray<ForecastNeedMetric> needs)
        {
            var key = (p, exposure.Id, roof.Id, choicesId);
            if (banded.TryGetValue(key, out var result)) return result;
            var cardPawn = context.CardPawns.GetValueOrDefault(pawn.Pawn);
            var thoughts = new List<BandedThought>(temperatureCards.Length + roof.Value.Banded.Count + 2);
            // A colonist whose thoughts were not read keeps each card's observed constant.
            var read = context.ColonistThoughts.TryGetValue(pawn.Pawn, out var observed);
            foreach (var card in temperatureCards)
                thoughts.Add(read ? ForecastMood.TemperatureThought(card, cardPawn, observed, exposure.Value.Spans) : (card.Def, []));
            thoughts.AddRange(roof.Value.Banded);
            thoughts.AddRange(ForecastMood.NeedThoughts(needs));
            banded.Add(key, result = Next(thoughts.ToArray()));
            return result;
        }

        /// <summary>A pawn's needs result with its mood and roof findings applied.</summary>
        private ForecastNeedsResult Projected(Cached<ForecastNeedsResult> needsResult, Cached<ForecastMoodResult> moodResult, Cached<ForecastRoofMood> roofMood)
        {
            var key = (needsResult.Id, moodResult.Id, roofMood.Id);
            if (projected.TryGetValue(key, out var result)) return result;
            var (mood, roof) = (moodResult.Value, roofMood.Value);
            result = needsResult.Value with { Mood = mood, Findings = mood.BreakRisk.IsKnown ? needsResult.Value.Findings
                : EquatableArray.From(needsResult.Value.Findings.Append("mental_break_probability_unassessed").Distinct().Order(StringComparer.Ordinal)) };
            if (roof.Findings.Count > 0) result = result with { Findings = EquatableArray.From(result.Findings.Concat(roof.Findings).Distinct().Order(StringComparer.Ordinal)) };
            projected.Add(key, result);
            return result;
        }
        private ForecastCoupledContext? cardsOf;
        private MechanicCard[] cards = [];
        /// <summary>The Live temperature cards, by def.</summary>
        internal MechanicCard[] TemperatureCards(ForecastCoupledContext context)
        {
            if (!ReferenceEquals(context, cardsOf))
                (cardsOf, cards) = (context, (context.Cards ?? MechanicCards.ByDef).Values.Where(ForecastMood.TemperatureCard).OrderBy(c => c.Def, StringComparer.Ordinal).ToArray());
            return cards;
        }
        private (ForecastCoupledContext Context, double Horizon)? existingOf;
        private (EquatableArray<ForecastShelterPlan> Rooms, ForecastCoupledContext Context, EquatableArray<ForecastShelterOutcome> Shelters) existing;
        /// <summary>The shelters that already stand (no prerequisites), the context with only those, and their projection,
        /// which no schedule changes.</summary>
        internal (EquatableArray<ForecastShelterPlan> Rooms, ForecastCoupledContext Context, EquatableArray<ForecastShelterOutcome> Shelters) Existing(
            ForecastCoupledContext context, double horizon)
        {
            if (existingOf is not { } of || !ReferenceEquals(of.Context, context) || of.Horizon != horizon)
            {
                var rooms = EquatableArray.From(context.Shelters.Where(r => r.Prerequisites.Count == 0 && r.AlternativePrerequisites.Count == 0));
                (existingOf, existing) = ((context, horizon), (rooms, context with { Shelters = rooms }, ForecastShelterPolicy.ForecastShelters(rooms, new ForecastActivityPlan([], 0, 0), horizon)));
            }
            return existing;
        }
    }
}
