using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the anomaly family reads (#3302): the review's facts, whose containment planning (holders, doors and held
/// entities), research, monolith, gear and custody census its predicates read. Without Anomaly
/// (<see cref="RoundsFacts.AnomalyInstalled"/>) the family derives no Needs; with it, an unread fact raises
/// <see cref="RequiredFactException"/>.
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
/// (<see cref="EntityUpkeepRules.ContainmentDoorUpkeep"/>). Each door to close is its own Method,
/// <c>entity-door-&lt;x&gt;-&lt;z&gt;</c>, so doors close side by side.</item>
/// <item><c>anomaly/capture</c> (Wellbeing): no downed entity is owed a capture
/// (<see cref="EntityCaptureRules.EntityCaptureTarget"/>). Held as <c>unsupported_forecast_population_capture</c>.</item>
/// <item><c>anomaly/tend</c> (Wellbeing): no downed held entity needs tending (<see cref="EntityUpkeepRules.EntityTendTarget"/>).
/// Held as <c>dependent_care_incident_owner</c>: critical care belongs to the patient's medical/critical Need.</item>
/// <item><c>anomaly/lance</c> (Wellbeing): no recruitable hostile stands while a colonist wears a lance
/// (<see cref="PopulationRules.LanceCandidate"/>). The runtime holds it as <c>unsupported_forecast_population_lance</c>
/// once its combat read finds an able wearer.</item>
/// <item><c>anomaly/bioferrite</c> (Development): every held entity whose extraction pays has its extract flag set
/// (<see cref="EntityBioferriteRules.BioferriteHarvestOwed"/>). Each entity is its own Method, <c>entity-bioferrite-&lt;pawn&gt;</c>.</item>
/// <item><c>anomaly/monolith</c> (Development): the monolith owes no advance now
/// (<see cref="MonolithRules.MonolithAdvanceOwed"/>). Held as <c>unsupported_forecast_monolith</c>.</item>
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
        if (!f.AnomalyInstalled)
        {
            return [];
        }
        var p = f.Containment;
        if (!p.Holders.TryGetValue(out _))
        {
            throw new RequiredFactException("containment holders");
        }
        if (!p.Entities.TryGetValue(out _))
        {
            throw new RequiredFactException("containment entities");
        }
        if (!f.Monolith.TryGetValue(out _))
        {
            throw new RequiredFactException("monolith");
        }
        var harvest = EntityBioferriteRules.BioferriteHarvestOwed(p, f.Research);
        var monolith = MonolithRules.MonolithAdvanceOwed(f.Monolith, MonolithRules.MonolithGate(f));
        // A rule an unread fact kept from deciding names its issues; it raises, never reads as met.
        if (harvest.Enable.Count == 0 && harvest.Issues.Count > 0)
        {
            throw new RequiredFactException(string.Join("; ", harvest.Issues));
        }
        if (monolith.Order == MonolithOrder.None && MonolithRules.MonolithInPlay(f.Monolith) && monolith.Issues.Count > 0)
        {
            throw new RequiredFactException(string.Join("; ", monolith.Issues));
        }
        return
        [
            Counted(Doors, NeedTier.Wellbeing, EntityUpkeepRules.ContainmentDoorUpkeep(p).CloseDoors.Count),
            Counted(Capture, NeedTier.Wellbeing, EntityCaptureRules.EntityCaptureTarget(p) is null ? 0 : 1),
            Counted(Tend, NeedTier.Wellbeing, EntityUpkeepRules.EntityTendTarget(p) is null ? 0 : 1),
            Counted(Lance, NeedTier.Wellbeing, LanceTargets(f)),
            Counted(Bioferrite, NeedTier.Development, harvest.Enable.Count),
            Counted(Monolith, NeedTier.Development, monolith.Order == MonolithOrder.None ? 0 : 1),
        ];
    }

    public IReadOnlyList<Need> DeriveSeriousHarm(AnomalyFacts facts) => [];

    /// <summary>A Method per door to close and per entity to flag; holds for capture, tend and the monolith. The lance is the runtime's.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, AnomalyFacts facts)
    {
        var p = facts.Rounds.Containment;
        var output = new List<NeedMethod>();
        foreach (var need in ready)
        {
            if (need.Id == Doors)
            {
                foreach (var cell in EntityUpkeepRules.ContainmentDoorUpkeep(p).CloseDoors)
                {
                    var door = new DoorControl(cell, false);
                    output.Add(new NeedMethod(Doors, DoorMethod(cell), "",
                        id => new PlanSpec(id, new PlanRevision(1), [DomainAction.NewDoorControlAction(new ActionId(id.Value + "-0"), door)])));
                }
            }
            else if (need.Id == Bioferrite)
            {
                foreach (var entity in EntityBioferriteRules.BioferriteHarvestOwed(p, facts.Rounds.Research).Enable)
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

    /// <summary>The lance targets: none while no colonist wears a lance; an unread gear, colony or custody census raises <see cref="RequiredFactException"/>.</summary>
    private static int LanceTargets(RoundsFacts f)
    {
        if (!f.Gear.TryGetValue(out _))
        {
            throw new RequiredFactException("gear");
        }
        if (!PopulationRules.LanceWorn(f.Gear))
        {
            return 0;
        }
        if (!f.Population.TryGetValue(out _))
        {
            throw new RequiredFactException("population census");
        }
        return PopulationRules.LanceCandidate(f) is null ? 0 : 1;
    }

    private static Need Counted(NeedId id, NeedTier tier, int owed) => new()
    {
        Id = id,
        Tier = tier,
        Satisfaction = owed > 0 ? Satisfaction.Unmet : Satisfaction.Met,
        Deficit = Fact.Known((double)owed),
    };
}
