namespace RimGovernor.Brain.Runtime;

/// <summary>
/// The read-only native surface <see cref="ColonyStatus"/> needs: the Identity call every clock-scheduled planner
/// uses to learn the current authoritative snapshot, the colony-facts census the reviewer judges food from, and
/// the home colonist roster (downed, needs). Nothing here dispatches a native write.
/// </summary>
public interface IColonyStatusNative : IResearchSource
{
    Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity> IdentityAsync(CancellationToken ct);

    Task<RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot> ReadColonyFactsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct);

    Task<RimGovernor.Brain.GameFacts.Observations.PawnSnapshot> ReadHomeColonistsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct);
}

/// <summary>One living home colonist's state.</summary>
public sealed record ColonyStatusPawn
{
    public PawnId Id { get; init; }

    public string Label { get; init; } = "";

    public bool Downed { get; init; }

    public required RimGovernor.Brain.GameFacts.Observations.NeedLevel Mood { get; init; }

    public required RimGovernor.Brain.GameFacts.Observations.FoodNeed Food { get; init; }
}

/// <summary>What the home roster read found. An empty roster is a valid state (no living colonist), not a read failure.</summary>
public enum ColonyRoster
{
    /// <summary>At least one living home colonist.</summary>
    Colonists,

    /// <summary>No living home colonist on the map.</summary>
    NoColonists,
}

/// <summary>One total native census with an optional matching routine review.</summary>
public sealed record ColonyStatusReport
{
    /// <summary>Absent until a matching routine review has filed.</summary>
    public ColonyStatusReview? Review { get; init; }

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

    public required RimGovernor.Brain.GameFacts.Observations.FoodRunway FoodRunway { get; init; }

    public double PendingFoodNutrition { get; init; }

    /// <summary>The census's wealth split and raid points.</summary>
    public required RimGovernor.Brain.GameFacts.Observations.ThreatFacts Threat { get; init; }

    /// <summary>The player faction's native TechLevel.</summary>
    public RimGovernor.Brain.GameFacts.Defs.TechLevel PlayerTechLevel { get; init; }

    /// <summary>The construction tier derived from the current native research census and faction tech level.</summary>
    public TechTier TechTier { get; init; }

    /// <summary>The living home colonist roster, sorted as native listed it.</summary>
    public IReadOnlyList<ColonyStatusPawn> Pawns { get; init; } = [];
}

/// <summary>The most recent matching routine review; absence from the report means pending review.</summary>
public sealed record ColonyStatusReview
{
    public Tick Tick { get; init; }
    public FoodPlan? FoodPlan { get; init; }
    public IReadOnlyDictionary<PawnId, string> PetLabels { get; init; } = new Dictionary<PawnId, string>();
    public EquatableArray<StockpileRoleCount> Stockpiles { get; init; }
    public bool ForbiddenSupplies { get; init; }
    public MoodLedger? MoodLedger { get; init; }
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
        var decoded = new ObservationIdentity { Context = identityReply.Context, Paused = identityReply.Paused };
        var identity = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = decoded.Colony.Value, LoadToken = decoded.Load.Value, MapId = decoded.Map.Value };
        var observed = await Native.ReadColonyFactsAsync(identity, call).ConfigureAwait(false);
        var roster = await Native.ReadHomeColonistsAsync(identity, call).ConfigureAwait(false);
        var research = await Native.ReadResearchAsync(identity, call).ConfigureAwait(false);
        foreach (var context in new[] { observed.Context, roster.Context, research.Context })
        {
            var read = new ObservationIdentity { Context = context };
            if (!read.SameContext(decoded) || read.NativeGeneration != decoded.NativeGeneration || read.Tick < decoded.Tick)
                throw new ObservationChangedException();
        }
        var pawns = roster;
        var report = new ColonyStatusReport
        {
            Tick = new Tick((long)observed.Context.Tick),
            RosterTick = new ObservationIdentity { Context = pawns.Context }.Tick,
            Colonists = observed.ColonistCount,
            Workers = observed.WorkerCount,
            FoodNutrition = observed.FoodNutrition,
            NutritionPerDay = observed.NutritionPerDay,
            FoodRunway = observed.FoodRunway,
            PendingFoodNutrition = observed.PendingFoodNutrition,
            Threat = observed.Threat.Observed,
            PlayerTechLevel = observed.PlayerTechLevel,
            TechTier = TechTierRules.SelectTechTier(research.Finished.Select(project => new ResearchProjectId(project)).ToArray(), observed.PlayerTechLevel),
        };
        if (_food is not null && FactStore.TryGet<ColonyReading>(_food, FactSection.Colony, out var held))
        {
            var id = held.Value.Identity;
            if (id.SameContext(decoded) && id.Tick <= decoded.Tick
                && id.NativeGeneration is RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase a && decoded.NativeGeneration is RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase b && a == b)
            {
                var labels = new Dictionary<PawnId, string>();
                foreach (var animal in held.Value.Facts.AnimalUpkeep.Animals.Where(animal => animal.Label != ""))
                {
                    labels[animal.Id] = animal.Label;
                }
                report = report with
                {
                    Review = new ColonyStatusReview
                    {
                        Tick = id.Tick,
                        FoodPlan = held.Value.Facts.FoodPlan,
                        ForbiddenSupplies = ForbiddenSupplies(held.Value.Facts.EventLoot),
                        Stockpiles = held.Value.Facts.StockpileZones,
                        MoodLedger = held.Value.Facts.MoodLedger,
                        PetLabels = labels,
                    },
                };
            }
        }
        var rows = new List<ColonyStatusPawn>();
        foreach (var row in pawns.Pawns)
        {
            if (row is null || (row.Pawn.Id ?? "") == "" || row.Dead)
            {
                continue;
            }
            var pawn = new ColonyStatusPawn { Id = new PawnId((row.Pawn.Id ?? "")), Label = row.Pawn.Label ?? "", Downed = row.Downed, Mood = row.Needs.Mood, Food = row.Needs.Food };
            rows.Add(pawn);
        }
        report = report with { Pawns = rows, Roster = rows.Count == 0 ? ColonyRoster.NoColonists : ColonyRoster.Colonists };
        return report;
    }

    /// <summary>Whether the loot census lists a safe stack still forbidden, the stack the admin/supply-safety Need releases.</summary>
    internal static bool ForbiddenSupplies(EquatableArray<LootItem> census) => census.Any(row => row.IsSafe && row.Forbidden);
}
