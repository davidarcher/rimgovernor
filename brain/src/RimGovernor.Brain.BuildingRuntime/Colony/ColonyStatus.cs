namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The read-only native surface <see cref="ColonyStatus"/> needs: the Identity call every clock-scheduled planner
/// uses to learn the current authoritative snapshot, the colony-facts census the rounder judges food from, and
/// the home colonist roster (downed, needs). Nothing here dispatches a native write.
/// </summary>
public interface IColonyStatusNative
{
    Task<PbLifecycle.LoadedIdentity> IdentityAsync(CancellationToken ct);

    Task<PbObservations.ColonyFactsSnapshot> ReadColonyFactsAsync(PbCommon.Identity identity, bool full, CancellationToken ct);

    Task<PbObservations.PawnSnapshot> ReadHomeColonistsAsync(PbCommon.Identity identity, CancellationToken ct);
}

/// <summary>One living home colonist's state.</summary>
public sealed record ColonyStatusPawn
{
    public PawnId Id { get; init; }

    public string Label { get; init; } = "";

    public Fact<bool> Downed { get; init; }

    public Fact<double> Mood { get; init; }

    public Fact<double> Food { get; init; }
}

/// <summary>What the home roster read found. An empty roster is a valid state (no living colonist), not a read failure.</summary>
public enum ColonyRoster
{
    /// <summary>At least one living home colonist.</summary>
    Colonists,

    /// <summary>No living home colonist on the map.</summary>
    NoColonists,
}

/// <summary>One <see cref="ColonyStatus"/> read. Unknown facts are unknown, never zero: a native census that omits the field says so.</summary>
public sealed record ColonyStatusReport
{
    public FoodPlan? FoodPlan { get; init; }

    public Fact<Tick> FoodPlanTick { get; init; }

    /// <summary>Names the held census's animals for the food plan's pet shortfalls; an animal without a label is absent.</summary>
    public IReadOnlyDictionary<PawnId, string> PetLabels { get; init; } = new Dictionary<PawnId, string>();

    /// <summary>The colony census's tick.</summary>
    public Tick Tick { get; init; }

    /// <summary>The roster read's tick: equal under a stopped clock and a few ticks later under a running window.</summary>
    public Tick RosterTick { get; init; }

    /// <summary>Whether the roster read found any living home colonist.</summary>
    public ColonyRoster Roster { get; init; }

    public long Colonists { get; init; }

    public long Workers { get; init; }

    public double FoodNutrition { get; init; }

    public double NutritionPerDay { get; init; }

    public Fact<double> FoodRunwayDays { get; init; }

    public double PendingFoodNutrition { get; init; }

    /// <summary>The census's wealth split and raid points.</summary>
    public required PbObservations.ThreatFacts Threat { get; init; }

    /// <summary>The player faction's native TechLevel.</summary>
    public RimGovernor.Contracts.Wire.Defs.TechLevel PlayerTechLevel { get; init; }

    /// <summary>The construction tier the last rounds derived from the tech level and finished research; unknown until a review with the research census has filed.</summary>
    public Fact<TechTier> TechTier { get; init; }

    /// <summary>Counts the owned stockpile zones by role kind as the last rounds read them.</summary>
    public Fact<EquatableArray<StockpileRoleCount>> Stockpiles { get; init; }

    /// <summary>Whether the loot census held a safe stack forbidden at the last rounds.</summary>
    public Fact<bool> ForbiddenSupplies { get; init; }

    /// <summary>Where the colony loses mood, as the last rounds built it; unknown until a review has filed.</summary>
    public Fact<MoodLedger> MoodLedger { get; init; }

    /// <summary>The living home colonist roster, sorted as native listed it.</summary>
    public IReadOnlyList<ColonyStatusPawn> Pawns { get; init; } = [];
}

/// <summary>
/// A live, read-only census of the facts a sustained run is judged by: the food stock and runway the reviewer
/// sees and every home colonist's downed state and needs, read under one identity. It claims no plan slot and no
/// player gate: it issues only native reads, so a planner step holding the gate never delays a sample past its
/// timeout.
/// </summary>
public sealed class ColonyStatus
{
    private readonly Player Player;
    private readonly IColonyStatusNative Native;
    private readonly FactStore? _food;

    /// <summary>Builds a ColonyStatus boundary.</summary>
    public ColonyStatus(Player player, IColonyStatusNative native, FactStore? food = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(native);
        Player = player;
        Native = native;
        _food = food;
    }

    /// <summary>
    /// Reports the current colony census. It does not require player control to be enabled: nothing here writes,
    /// so no control epoch protects it. The game may be running under a live window, so the two reads land on
    /// nearby ticks rather than one; the report carries both.
    /// </summary>
    public async Task<ColonyStatusReport> ReadAsync(CancellationToken ct)
    {
        using var done = Player.Observe(ct);
        var call = done.Token;
        var identityReply = await Native.IdentityAsync(call).ConfigureAwait(false);
        var decoded = DecodeReads.DecodeIdentity(identityReply);
        var identity = new PbCommon.Identity { ColonyId = decoded.Colony.Value, LoadToken = decoded.Load.Value, MapId = decoded.Map.Value };
        var observed = await Native.ReadColonyFactsAsync(identity, false, call).ConfigureAwait(false);
        var roster = await Native.ReadHomeColonistsAsync(identity, call).ConfigureAwait(false);
        var pawns = roster;
        var report = new ColonyStatusReport
        {
            Tick = new Tick((long)observed.Context.Tick),
            RosterTick = new Tick((long)(pawns.Context?.Tick ?? 0)),
            Colonists = observed.ColonistCount,
            Workers = observed.WorkerCount,
            FoodNutrition = observed.FoodNutrition,
            NutritionPerDay = observed.NutritionPerDay,
            FoodRunwayDays = OptionalFact(observed.FoodRunwayDays is not null, observed.FoodRunwayDays.GetValueOrDefault()),
            PendingFoodNutrition = observed.PendingFoodNutrition,
            Threat = observed.Threat.Observed,
            PlayerTechLevel = observed.PlayerTechLevel,
            TechTier = Unknown<TechTier>(),
            Stockpiles = Unknown<EquatableArray<StockpileRoleCount>>(),
            ForbiddenSupplies = Unknown<bool>(),
            MoodLedger = Unknown<MoodLedger>(),
        };
        if (_food is not null && FactStore.TryGet<ColonyProjection>(_food, FactSection.Colony, out var held))
        {
            var id = held.Value.Identity;
            if (id.SameContext(decoded) && id.Tick <= decoded.Tick
                && id.NativeGeneration.TryGetValue(out var a) && decoded.NativeGeneration.TryGetValue(out var b) && a == b)
            {
                var labels = new Dictionary<PawnId, string>();
                foreach (var animal in held.Value.Facts.AnimalUpkeep.Animals.Where(animal => animal.Label != ""))
                {
                    labels[animal.Id] = animal.Label;
                }
                report = report with
                {
                    TechTier = held.Value.TechTier,
                    FoodPlan = held.Value.Facts.FoodPlan,
                    ForbiddenSupplies = ForbiddenSupplies(held.Value.Facts.EventLoot),
                    Stockpiles = held.Value.Facts.StockpileZones,
                    MoodLedger = held.Value.Facts.MoodLedger,
                    PetLabels = labels,
                };
                if (report.FoodPlan is not null)
                {
                    report = report with { FoodPlanTick = Known(id.Tick) };
                }
            }
        }
        var rows = new List<ColonyStatusPawn>();
        foreach (var row in pawns.Pawns)
        {
            if (row is null || (row.Pawn.Id ?? "") == "" || row.Dead)
            {
                continue;
            }
            var pawn = new ColonyStatusPawn { Id = new PawnId((row.Pawn.Id ?? "")), Label = row.Pawn.Label ?? "", Downed = Known(row.Downed) };
            var needs = row.Needs;
            pawn = pawn with
            {
                Mood = OptionalFact(needs.Mood is PbObservations.NeedLevel.Has, needs.Mood is PbObservations.NeedLevel.Has mood ? mood.Level : 0),
                Food = OptionalFact(needs.Food is PbObservations.FoodNeed.Has, needs.Food is PbObservations.FoodNeed.Has food ? food.Level : 0),
            };
            rows.Add(pawn);
        }
        report = report with { Pawns = rows, Roster = rows.Count == 0 ? ColonyRoster.NoColonists : ColonyRoster.Colonists };
        return report;
    }

    private static Fact<double> OptionalFact(bool has, double value) => has ? Known(value) : Unknown<double>();

    private static Fact<bool> OptionalFact(bool has, bool value) => has ? Known(value) : Unknown<bool>();

    /// <summary>Whether the loot census lists a safe stack still forbidden, the stack the admin/supply-safety Need releases.</summary>
    internal static Fact<bool> ForbiddenSupplies(Fact<EquatableArray<LootItem>> census)
    {
        if (!census.TryGetValue(out var rows))
        {
            return Unknown<bool>();
        }
        return Known(rows.Any(row => row.SafetyKnown && row.SafeToHaul && row.Forbidden));
    }
}
