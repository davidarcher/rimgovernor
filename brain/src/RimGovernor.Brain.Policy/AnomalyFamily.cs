using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the anomaly family reads (#3302): the review's facts, whose containment planning (holders, doors and held
/// entities), research, monolith, gear and custody census its predicates read. An unknown one leaves its Need unknown.
/// </summary>
public sealed record AnomalyFacts
{
    public RoundsFacts Rounds { get; init; } = new();

    /// <summary>The facts a review already holds.</summary>
    public static AnomalyFacts From(RoundsFacts f) => new() { Rounds = f };
}

/// <summary>
/// The anomaly family (#3302): the entity half of the old population Concern, with the lance use that
/// moved out of custody.
/// <list type="bullet">
/// <item><c>anomaly/doors</c> (Wellbeing): no held entity's cell door is held open
/// (<see cref="PolicyFunctions.ContainmentDoorUpkeep"/>). Each door to close is its own Method,
/// <c>entity-door-&lt;x&gt;-&lt;z&gt;</c>, so doors close side by side.</item>
/// <item><c>anomaly/capture</c> (Wellbeing): no downed entity is owed a capture
/// (<see cref="PolicyFunctions.EntityCaptureTarget"/>). Held as <c>unsupported_forecast_population_capture</c>.</item>
/// <item><c>anomaly/tend</c> (Wellbeing): no downed held entity needs tending (<see cref="PolicyFunctions.EntityTendTarget"/>).
/// Held as <c>dependent_care_incident_owner</c>: critical care belongs to the patient's medical/critical Need.</item>
/// <item><c>anomaly/lance</c> (Wellbeing): no recruitable hostile stands while a colonist wears a lance
/// (<see cref="PolicyFunctions.LanceCandidate"/>). The runtime holds it as <c>unsupported_forecast_population_lance</c>
/// once its combat read finds an able wearer.</item>
/// <item><c>anomaly/bioferrite</c> (Development): every held entity whose extraction pays has its extract flag set
/// (<see cref="PolicyFunctions.BioferriteHarvestOwed"/>). Each entity is its own Method, <c>entity-bioferrite-&lt;pawn&gt;</c>.</item>
/// <item><c>anomaly/monolith</c> (Development): the monolith owes no advance now
/// (<see cref="PolicyFunctions.MonolithAdvanceOwed"/>). Held as <c>unsupported_forecast_monolith</c>.</item>
/// </list>
/// The Needs are independent; none waits on another.
/// </summary>
public sealed class AnomalyFamily : IFamily<AnomalyFacts>
{
    public const string FamilyId = "anomaly";

    public static readonly NeedId Doors = NeedId.Of(FamilyId, "doors");

    public static readonly NeedId Capture = NeedId.Of(FamilyId, "capture");

    public static readonly NeedId Tend = NeedId.Of(FamilyId, "tend");

    public static readonly NeedId Lance = NeedId.Of(FamilyId, "lance");

    public static readonly NeedId Bioferrite = NeedId.Of(FamilyId, "bioferrite");

    public static readonly NeedId Monolith = NeedId.Of(FamilyId, "monolith");

    /// <summary>The stable refusal subject of a downed entity to capture.</summary>
    public const string CaptureHeld = "unsupported_forecast_population_capture";

    /// <summary>The stable refusal subject of a held entity to tend.</summary>
    public const string TendHeld = "dependent_care_incident_owner";

    /// <summary>The stable refusal subject of a lance to use.</summary>
    public const string LanceHeld = "unsupported_forecast_population_lance";

    /// <summary>The stable refusal subject of a monolith to advance.</summary>
    public const string MonolithHeld = "unsupported_forecast_monolith";

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Pawns, FactFamily.Colony, FactFamily.Emergency, FactFamily.Research];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(AnomalyFacts facts)
    {
        var f = facts.Rounds;
        var p = f.Containment;
        var holders = p.Holders.TryGetValue(out _);
        var entities = p.Entities.TryGetValue(out _);
        var doors = PolicyFunctions.ContainmentDoorUpkeep(p).CloseDoors.Count;
        var harvest = PolicyFunctions.BioferriteHarvestOwed(p, f.Research);
        var monolith = MonolithRules.MonolithAdvanceOwed(f.Monolith, MonolithRules.MonolithGate(f));
        return
        [
            Counted(Doors, NeedTier.Wellbeing, holders ? doors : null, "doors_to_close"),
            Counted(Capture, NeedTier.Wellbeing, entities ? (PolicyFunctions.EntityCaptureTarget(p) is null ? 0 : 1) : null, "entities_to_capture"),
            Counted(Tend, NeedTier.Wellbeing, entities ? (PolicyFunctions.EntityTendTarget(p) is null ? 0 : 1) : null, "entities_to_tend"),
            Counted(Lance, NeedTier.Wellbeing, LanceTargets(f), "lance_targets"),
            // An unread fact that kept the rule from deciding leaves the Need unknown, never met.
            Counted(Bioferrite, NeedTier.Development, entities && (harvest.Enable.Count > 0 || harvest.Issues.Count == 0) ? harvest.Enable.Count : null, "entities_to_extract"),
            Counted(Monolith, NeedTier.Development,
                f.Monolith.TryGetValue(out _) && (monolith.Order != MonolithOrder.None || !MonolithRules.MonolithInPlay(f.Monolith) || monolith.Issues.Count == 0)
                    ? (monolith.Order == MonolithOrder.None ? 0 : 1) : null,
                "monolith_advance"),
        ];
    }

    /// <summary>A Method per door to close and per entity to flag; holds for capture, tend and the monolith. The lance is the runtime's.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, AnomalyFacts facts)
    {
        var p = facts.Rounds.Containment;
        var output = new List<NeedMethod>();
        foreach (var need in ready)
        {
            if (need.Id == Doors)
            {
                foreach (var cell in PolicyFunctions.ContainmentDoorUpkeep(p).CloseDoors)
                {
                    var door = new DoorControl(cell, false);
                    output.Add(new NeedMethod(Doors, DoorMethod(cell), "",
                        id => new PlanSpec(id, new PlanRevision(1), [DomainAction.NewDoorControlAction(new ActionId(id.Value + "-0"), door)])));
                }
            }
            else if (need.Id == Bioferrite)
            {
                foreach (var entity in PolicyFunctions.BioferriteHarvestOwed(p, facts.Rounds.Research).Enable)
                {
                    var setting = PawnSettings.NewExtractBioferriteSetting(entity, true);
                    output.Add(new NeedMethod(Bioferrite, BioferriteMethod(entity), "",
                        id => new PlanSpec(id, new PlanRevision(1), [DomainAction.NewPawnSettingsAction(new ActionId(id.Value + "-0"), setting)])));
                }
            }
            else if (need.Id == Capture)
            {
                output.Add(NeedMethod.Held(Capture, CaptureHeld, "Population acquisition requires shared participant and custody outcome alternatives."));
            }
            else if (need.Id == Tend)
            {
                output.Add(NeedMethod.Held(Tend, TendHeld,
                    "Critical held-entity care belongs to the patient's medical/critical Need; noncritical dependent care requires shared forecast support."));
            }
            else if (need.Id == Monolith)
            {
                output.Add(NeedMethod.Held(Monolith, MonolithHeld, "Monolith interaction requires shared forecast worker, duration and outcome admission."));
            }
        }
        return output;
    }

    /// <summary>The MethodId of closing the cell door at <paramref name="cell"/>.</summary>
    public static MethodId DoorMethod(Cell cell) => new(string.Create(CultureInfo.InvariantCulture, $"entity-door-{cell.X}-{cell.Z}"));

    /// <summary>The MethodId of setting <paramref name="entity"/>'s extract-bioferrite flag.</summary>
    public static MethodId BioferriteMethod(Domain.PawnId entity) => new("entity-bioferrite-" + entity.Value);

    /// <summary>The lance targets: none while no colonist wears a lance; unknown while the gear, colony or custody census is unread.</summary>
    private static int? LanceTargets(RoundsFacts f)
    {
        if (!f.Gear.TryGetValue(out _))
        {
            return null;
        }
        if (!PopulationRules.LanceWorn(f.Gear))
        {
            return 0;
        }
        if (!f.PrisonerColony.TryGetValue(out _) || !f.Custody.TryGetValue(out _))
        {
            return null;
        }
        return PopulationRules.LanceCandidate(f) is null ? 0 : 1;
    }

    private static Need Counted(NeedId id, NeedTier tier, int? owed, string fact)
    {
        if (owed is not { } n)
        {
            return new Need { Id = id, Tier = tier, Evidence = [new(fact, default)] };
        }
        return new Need
        {
            Id = id,
            Tier = tier,
            Satisfaction = n > 0 ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = Fact.Known((double)n),
            Evidence = [new(fact, Fact.Known(n.ToString(CultureInfo.InvariantCulture)))],
        };
    }
}
