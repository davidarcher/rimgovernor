using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The next firebreak ring order the review supports (#3256): the runtime reads the ring and builds it; the family
/// only decides whether to propose it. The MethodId names the cut cells and the ruins of the batch, so the same
/// order continues its open Method and a disjoint one runs beside it.
/// </summary>
/// <param name="Method">The order's MethodId, <c>firebreak-&lt;digest&gt;</c>.</param>
/// <param name="Reason">The planner's short why: how many cells it cuts and ruins it takes down.</param>
/// <param name="Actions">The order's actions under a minted plan id.</param>
public sealed record LayoutCandidate(MethodId Method, string Reason, Func<PlanId, IReadOnlyList<DomainAction>> Actions);

/// <summary>
/// What the layout family reads: each section is read whole for a composed Need (the read raises
/// <see cref="RequiredFactException"/> when its census cannot be read) and null when its Need is not composed.
/// </summary>
public sealed record LayoutFacts
{
    /// <summary>The home-area diff toward the planned coverage (PlanHomeArea).</summary>
    public HomeAreaPlan? HomeCoverage { get; init; }

    /// <summary>The firebreak ring work of this tick's review: cut cells with standing plants and undesignated wooden ruins.</summary>
    public FirebreakWork? Firebreak { get; init; }

    /// <summary>The firebreak order the work supports, or null when it builds no action.</summary>
    public LayoutCandidate? FirebreakStep { get; init; }

    /// <summary>The planned rooms owed a vent or a roof (EnvelopeWork).</summary>
    public IReadOnlyList<PlannedRoom>? ClimateEnvelope { get; init; }

    /// <summary>The flammable owned walls a stone upgrade is owed (ReviewStoneShell).</summary>
    public IReadOnlyList<string>? StoneShell { get; init; }

    /// <summary>The stored defensive layout and the map's fights (<c>family/defense_layout</c>, the military/combat Need's journal).</summary>
    public DefenseLayoutFacts? Defense { get; init; }

    /// <summary>The routes census reviewed this read (ReviewRoutes) and whether a traffic door is owed a hold-open change.</summary>
    public RoutesStanding? Routes { get; init; }
}

/// <summary>What the routes Need reads: the reviewed routes census and whether a traffic door (not a heat door) is owed a change.</summary>
public sealed record RoutesStanding(RoutesReview Review, bool DoorOwed);

/// <summary>
/// What the defensive layout Need reads (#3314): the colony's stored layout for this map and load, and the combat
/// facts it is verified against. Nothing here is a combat precondition: an open fight only adds work.
/// </summary>
public sealed record DefenseLayoutFacts
{
    /// <summary>Every tier of the stored layout stood at its latest census (the record's Standing).</summary>
    public bool Standing { get; init; }

    /// <summary>
    /// The stored layout was verified complete in this load, at the map's latest fight and within the re-verify
    /// window; false before the map's first fight, which the step waits on (threat pending).
    /// </summary>
    public bool Verified { get; init; }

    /// <summary>An open fight in this world waits in rooms the layout hardens.</summary>
    public bool WaitingFight { get; init; }
}

/// <summary>Which of the layout family's Needs the runtime composes.</summary>
public sealed record LayoutNeeds
{
    public bool HomeCoverage { get; init; }

    public bool Firebreak { get; init; }

    public bool StoneShell { get; init; }

    public bool ClimateEnvelope { get; init; }

    /// <summary>The operator's defensive layout opt-in.</summary>
    public bool Defense { get; init; }

    public bool Routes { get; init; }

    public bool Any => HomeCoverage || Firebreak || StoneShell || ClimateEnvelope || Defense || Routes;
}

/// <summary>
/// The layout family (#3256): the colony-wide layout upkeep that keeps the planned ground covered, ringed, enclosed
/// and fireproof. It replaced the MaintainHomeCoverage, MaintainFirebreak, MaintainClimateEnvelope and
/// MaintainStoneShell Concerns, their inspections, latches and planners. Its Needs:
/// <list type="bullet">
/// <item><c>layout/home-coverage</c> (Wellbeing): the home area matches the planned coverage (PlanHomeArea finds no
/// diff). Its Method is the diff: auto-expand off when needed, then the set and clear edits, named by the diff's
/// hash.</item>
/// <item><c>layout/climate-envelope</c> (Wellbeing): no planned room is owed a vent or a roof. The runtime step
/// reconciles the owed rooms' rings and roofs, one Method per room wave.</item>
/// <item><c>layout/firebreak</c> (Wellbeing): no ring cut cell holds an undesignated plant and no planned wooden ruin
/// is undesignated. It waits on <c>layout/home-coverage</c> when that is composed: the ring is drawn around the
/// colony extent the home area covers. Its Method is the ring order.</item>
/// <item><c>layout/stone-shell</c> (Development): no flammable owned wall. It waits on <c>layout/climate-envelope</c>
/// when that is composed: a wall upgrade demolishes the original once its interior is enclosed and roofed, so it
/// does not run while a ring vent or roof is still owed. The runtime step admits one wall upgrade bundle per
/// wall.</item>
/// <item><c>layout/defense</c> (Development, #3314; it replaced EnsureDefensiveLayout): the operator's opt-in
/// corridor layout was verified complete in this load, at the map's latest fight and within the re-verify window,
/// and no open fight waits in rooms to harden. The runtime step proposes, builds and re-verifies the stored layout tier by
/// tier, and hardens the waiting rooms during a fight; its attempts are counted per tier in the
/// <c>family/defense_layout</c> record.</item>
/// <item><c>layout/routes</c> (Development, #3265; it replaced the last registry Concern): every facility the native routes census
/// lists is reachable by some mobile colonist, and no traffic door is owed a hold-open change. The runtime step cuts a
/// door into the wall enclosing an unreachable facility, or sets the owed door, under the Need owner.</item>
/// </list>
/// A latch no longer holds a Need after its census recovers: each is a plain predicate over this review, and an
/// unread census raises <see cref="RequiredFactException"/>.
/// </summary>
public sealed class LayoutFamily(LayoutNeeds composed) : IFamily<LayoutFacts>
{
    public const string FamilyId = "layout";

    public static readonly NeedId HomeCoverage = NeedId.Of(FamilyId, "home-coverage");

    public static readonly NeedId ClimateEnvelope = NeedId.Of(FamilyId, "climate-envelope");

    public static readonly NeedId Firebreak = NeedId.Of(FamilyId, "firebreak");

    public static readonly NeedId StoneShell = NeedId.Of(FamilyId, "stone-shell");

    public static readonly NeedId Defense = NeedId.Of(FamilyId, "defense");

    public static readonly NeedId Routes = NeedId.Of(FamilyId, "routes");

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.World, FactFamily.Rooms, FactFamily.Research];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(LayoutFacts facts)
    {
        var needs = new List<Need>(6);
        if (composed.HomeCoverage)
        {
            needs.Add(HomeCoverageNeed(facts.HomeCoverage ?? throw new RequiredFactException("home area plan")));
        }
        if (composed.ClimateEnvelope)
        {
            needs.Add(ClimateEnvelopeNeed(facts.ClimateEnvelope ?? throw new RequiredFactException("climate envelope")));
        }
        if (composed.Firebreak)
        {
            needs.Add(FirebreakNeed(facts.Firebreak ?? throw new RequiredFactException("firebreak ring")) with
            {
                Preconditions = composed.HomeCoverage ? [HomeCoverage] : [],
            });
        }
        if (composed.StoneShell)
        {
            needs.Add(StoneShellNeed(facts.StoneShell ?? throw new RequiredFactException("flammable walls")) with
            {
                Preconditions = composed.ClimateEnvelope ? [ClimateEnvelope] : [],
            });
        }
        if (composed.Defense)
        {
            needs.Add(DefenseNeed(facts.Defense ?? throw new RequiredFactException("defense layout")));
        }
        if (composed.Routes)
        {
            needs.Add(RoutesNeed(facts.Routes ?? throw new RequiredFactException("routes census")));
        }
        return needs;
    }

    public IReadOnlyList<Need> DeriveSeriousHarm(LayoutFacts facts) => [];

    /// <summary>The home-area diff and the firebreak order; the envelope and stone shell are the runtime step's, which previews and admits buildings.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, LayoutFacts facts)
    {
        var output = new List<NeedMethod>();
        foreach (var need in ready)
        {
            if (need.Id == HomeCoverage && facts.HomeCoverage is { } diff && !diff.Empty())
            {
                output.Add(new NeedMethod(HomeCoverage, diff.MethodId(),
                    string.Create(CultureInfo.InvariantCulture, $"home coverage set {diff.Set.Count} cells, clear {diff.Clear.Count}"),
                    id => new PlanSpec(id, new PlanRevision(1), HomeCoverageActions(id, diff))));
            }
            else if (need.Id == Firebreak && facts.FirebreakStep is { } step)
            {
                output.Add(new NeedMethod(Firebreak, step.Method, step.Reason, id => new PlanSpec(id, new PlanRevision(1), step.Actions(id))));
            }
        }
        return output;
    }

    /// <summary>The home-area diff as actions: auto-expand off when the diff says so, then the set and the clear edit.</summary>
    public static IReadOnlyList<DomainAction> HomeCoverageActions(PlanId id, HomeAreaPlan diff)
    {
        var actions = new List<DomainAction>();
        ActionId Next() => new(string.Create(CultureInfo.InvariantCulture, $"{id}-{actions.Count}"));
        if (diff.AutoOff)
        {
            actions.Add(DomainAction.NewAutoHomeAreaAction(Next(), false));
        }
        (AreaOperation Op, EquatableArray<Cell> Cells)[] edits = [(AreaOperation.SetCells, diff.Set), (AreaOperation.ClearCells, diff.Clear)];
        foreach (var (op, cells) in edits)
        {
            if (cells.Count > 0)
            {
                actions.Add(DomainAction.NewAreaAction(Next(), new Area(op, "", cells)));
            }
        }
        return actions;
    }

    /// <summary>Unmet while the home area differs from the planned coverage.</summary>
    public static Need HomeCoverageNeed(HomeAreaPlan diff)
    {
        var cells = diff.Set.Count + diff.Clear.Count;
        return new Need
        {
            Id = HomeCoverage,
            Tier = NeedTier.Wellbeing,
            Satisfaction = diff.Empty() ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = (double)cells + (diff.AutoOff ? 1 : 0),
        };
    }

    /// <summary>Unmet while the ring owes a cut or a ruin.</summary>
    public static Need FirebreakNeed(FirebreakWork work)
    {
        return new Need
        {
            Id = Firebreak,
            Tier = NeedTier.Wellbeing,
            Satisfaction = work.Owed ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = (double)(work.Cut.Count + work.Deconstruct.Count),
        };
    }

    /// <summary>Unmet while a planned room is owed a vent or a roof.</summary>
    public static Need ClimateEnvelopeNeed(IReadOnlyList<PlannedRoom> owed)
    {
        var rooms = owed.Count;
        return new Need
        {
            Id = ClimateEnvelope,
            Tier = NeedTier.Wellbeing,
            Satisfaction = rooms > 0 ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = (double)rooms,
        };
    }

    /// <summary>Unmet while an owned wall is flammable.</summary>
    public static Need StoneShellNeed(IReadOnlyList<string> rows)
    {
        return new Need
        {
            Id = StoneShell,
            Tier = NeedTier.Development,
            Satisfaction = rows.Count > 0 ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = (double)rows.Count,
        };
    }

    /// <summary>
    /// Unmet while the stored layout's verification is not current or an open fight waits in rooms to harden. The
    /// deficit is zero while every tier stands (a re-verification or a fight only) and one while a tier is owed.
    /// </summary>
    public static Need DefenseNeed(DefenseLayoutFacts f)
    {
        return new Need
        {
            Id = Defense,
            Tier = NeedTier.Development,
            Satisfaction = f.Verified && !f.WaitingFight ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = f.Standing ? 0.0 : 1.0,
        };
    }

    /// <summary>
    /// Unmet while the routes census reads a facility no mobile colonist reaches or a traffic door is owed a change;
    /// an unread census raises <see cref="RequiredFactException"/>. A plain threshold: a facility whose ordered door is framed reads reachable.
    /// </summary>
    public static Need RoutesNeed(RoutesStanding s)
    {
        if (!s.Review.Known)
        {
            throw new RequiredFactException("routes census");
        }
        var unreachable = s.Review.Deficits.Count;
        return new Need
        {
            Id = Routes,
            Tier = NeedTier.Development,
            Satisfaction = s.Review.Active || s.DoorOwed ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = (double)unreachable + (s.DoorOwed ? 1 : 0),
        };
    }

    /// <summary>The routes standing of one read: the census reviewed fresh, and the traffic door changes (not heat) owed.</summary>
    public static RoutesStanding RoutesStandingOf(Fact<RoutesObservation> routes, Fact<RoomObservation> rooms, RoundsPolicy p) =>
        new(RoutesRules.ReviewRoutes(routes, p.Routes), DoorControlRules.DoorChanges(rooms, routes, p).Any(d => !d.Heat));

}
