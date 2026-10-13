namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The read-only native surface <see cref="ColonyStatus"/> needs: the Identity call every clock-scheduled planner
/// uses to learn the current authoritative snapshot, the colony-facts census the rounder judges food from, and
/// the home colonist roster (downed, needs). Nothing here dispatches a native write.
/// </summary>
public interface IColonyStatusNative
{
    Task<PbLifecycle.IdentityReply> IdentityAsync(CancellationToken ct);

    Task<PbObservations.ColonyFactsReply> ReadColonyFactsAsync(PbCommon.Identity identity, bool full, CancellationToken ct);

    Task<PbObservations.ListPawnsReply> ReadHomeColonistsAsync(PbCommon.Identity identity, CancellationToken ct);
}

/// <summary>One living home colonist's state. Share, Spent and Remaining are the colonist's personal wealth share from the held colony projection; unknown when it is missing or stale or holds no share for the colonist.</summary>
public sealed record ColonyStatusPawn
{
    public PawnId Id { get; init; }

    public string Label { get; init; } = "";

    public Fact<bool> Downed { get; init; }

    public Fact<double> Mood { get; init; }

    public Fact<double> Food { get; init; }

    public Fact<double> Share { get; init; }

    public Fact<double> Spent { get; init; }

    public Fact<double> Remaining { get; init; }
}

/// <summary>One <see cref="ColonyStatus"/> read. Unknown facts are unknown, never zero: a native census that omits the field says so.</summary>
public sealed record ColonyStatusReport
{
    public Fact<FoodPlan> FoodPlan { get; init; }

    public Fact<Tick> FoodPlanTick { get; init; }

    /// <summary>Names the held census's animals for the food plan's pet shortfalls; an animal without a label is absent.</summary>
    public IReadOnlyDictionary<PawnId, string> PetLabels { get; init; } = new Dictionary<PawnId, string>();

    /// <summary>The colony census's tick.</summary>
    public Tick Tick { get; init; }

    /// <summary>The roster read's tick: equal under a stopped clock and a few ticks later under a running window.</summary>
    public Tick RosterTick { get; init; }

    public Fact<long> Colonists { get; init; }

    public Fact<long> Workers { get; init; }

    public Fact<double> FoodNutrition { get; init; }

    public Fact<double> NutritionPerDay { get; init; }

    public Fact<double> FoodRunwayDays { get; init; }

    public Fact<double> PendingFoodNutrition { get; init; }

    /// <summary>The census's wealth split and raid points; unknown under a native build that does not report the section.</summary>
    public ColonyThreat Threat { get; init; } = new();

    /// <summary>The ancient shrine census; unknown under a native build that does not serve it.</summary>
    public Fact<EquatableArray<AncientShrine>> Shrines { get; init; }

    /// <summary>Each shrine's breach judgement, one per Shrines row; empty while the census or its inputs are unknown.</summary>
    public IReadOnlyList<ShrineReadinessReport> ShrineReadiness { get; init; } = [];

    /// <summary>The player faction's native TechLevel name.</summary>
    public Fact<string> PlayerTechLevel { get; init; }

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
        var decoded = ObservationApi.DecodeIdentity(identityReply);
        var identity = new PbCommon.Identity { ColonyId = decoded.Colony.Value, LoadToken = decoded.Load.Value, MapId = decoded.Map.Value };
        var colony = await Native.ReadColonyFactsAsync(identity, false, call).ConfigureAwait(false);
        var observed = colony.Observed ?? throw new ControlException("Read: observed == nil");
        var roster = await Native.ReadHomeColonistsAsync(identity, call).ConfigureAwait(false);
        var pawns = roster.Observed ?? throw new ControlException("Read: pawns == nil");
        var report = new ColonyStatusReport
        {
            Tick = new Tick((long)(observed.Context?.Tick ?? 0)),
            RosterTick = new Tick((long)(pawns.Context?.Tick ?? 0)),
            Colonists = CountFact(observed.HasColonistCount, observed.ColonistCount),
            Workers = CountFact(observed.HasWorkerCount, observed.WorkerCount),
            FoodNutrition = OptionalFact(observed.HasFoodNutrition, observed.FoodNutrition),
            NutritionPerDay = OptionalFact(observed.HasNutritionPerDay, observed.NutritionPerDay),
            FoodRunwayDays = OptionalFact(observed.HasFoodRunwayDays, observed.FoodRunwayDays),
            PendingFoodNutrition = OptionalFact(observed.HasPendingFoodNutrition, observed.PendingFoodNutrition),
            Threat = BridgeApi.ProjectColonyThreat(observed),
            Shrines = Unknown<EquatableArray<AncientShrine>>(),
            PlayerTechLevel = observed.HasPlayerTechLevel ? Known(observed.PlayerTechLevel) : Unknown<string>(),
            TechTier = Unknown<TechTier>(),
            Stockpiles = Unknown<EquatableArray<StockpileRoleCount>>(),
            ForbiddenSupplies = Unknown<bool>(),
            MoodLedger = Unknown<MoodLedger>(),
        };
        if (Native is IShrineSource source)
        {
            report = report with { Shrines = await ObservationApi.ObserveShrines(source, decoded, call).ConfigureAwait(false) };
        }
        IReadOnlyDictionary<PawnId, PersonalShare>? shares = null;
        if (_food is not null && FactStore.TryGet<ColonyProjection>(_food, FactSection.Colony, out var held))
        {
            var id = held.Value.Identity;
            if (id.SameContext(decoded) && id.Tick <= decoded.Tick
                && id.NativeGeneration.TryGetValue(out var a) && decoded.NativeGeneration.TryGetValue(out var b) && a == b)
            {
                shares = held.Value.Facts.PersonalShares;
                var labels = new Dictionary<PawnId, string>();
                if (held.Value.Facts.AnimalUpkeep.Animals.TryGetValue(out var animals))
                {
                    foreach (var animal in animals.Where(animal => animal.Label != ""))
                    {
                        labels[animal.Id] = animal.Label;
                    }
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
                if (report.FoodPlan.TryGetValue(out _))
                {
                    report = report with { FoodPlanTick = Known(id.Tick) };
                }
            }
        }
        var rows = new List<ColonyStatusPawn>();
        foreach (var row in pawns.Pawns)
        {
            if (row?.Pawn is null || row.Pawn.Id == "" || row.Dead)
            {
                continue;
            }
            var pawn = new ColonyStatusPawn { Id = new PawnId(row.Pawn.Id), Label = row.Pawn.Label, Downed = OptionalFact(row.HasDowned, row.Downed) };
            if (row.Needs is { } needs)
            {
                pawn = pawn with { Mood = OptionalFact(needs.HasMood, needs.Mood), Food = OptionalFact(needs.HasFood, needs.Food) };
            }
            if (shares is not null && shares.TryGetValue(pawn.Id, out var share))
            {
                pawn = pawn with { Share = share.Share, Spent = share.Spent, Remaining = share.Remaining };
            }
            rows.Add(pawn);
        }
        report = report with { Pawns = rows };
        if (report.Shrines.TryGetValue(out var shrines) && shrines.Count > 0 && Native is IShrineReadinessNative readiness)
        {
            var colonists = rows.Select(pawn => pawn.Id.Value).ToList();
            var bounds = new Bounds((int)(observed.MapSize?.Width ?? 0), (int)(observed.MapSize?.Height ?? 0));
            var center = new Cell(observed.Center?.X ?? 0, observed.Center?.Z ?? 0);
            report = report with { ShrineReadiness = await ShrineReadinessAsync(readiness, identity, shrines, colonists, report.Threat.RaidPoints, center, bounds, call).ConfigureAwait(false) };
        }
        return report;
    }

    private static Fact<long> CountFact(bool has, uint value) => has ? Known((long)value) : Unknown<long>();

    private static Fact<double> OptionalFact(bool has, double value) => has ? Known(value) : Unknown<double>();

    private static Fact<bool> OptionalFact(bool has, bool value) => has ? Known(value) : Unknown<bool>();

    /// <summary>Whether the loot census lists a safe stack still forbidden, the stack ManageSupplySafety releases.</summary>
    internal static Fact<bool> ForbiddenSupplies(Fact<EquatableArray<LootItem>> census)
    {
        if (!census.TryGetValue(out var rows))
        {
            return Unknown<bool>();
        }
        return Known(rows.Any(row => row.SafetyKnown && row.SafeToHaul && row.Forbidden));
    }
}
