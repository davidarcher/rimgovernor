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
/// What the layout family reads; a section is null when its Need is not composed, and unknown while the census
/// behind it cannot be read.
/// </summary>
public sealed record LayoutFacts
{
    /// <summary>The home-area diff toward the planned coverage (PlanHomeArea).</summary>
    public Fact<HomeAreaPlan>? HomeCoverage { get; init; }

    /// <summary>The firebreak ring work of this tick's review: cut cells with standing plants and undesignated wooden ruins.</summary>
    public Fact<FirebreakWork>? Firebreak { get; init; }

    /// <summary>The firebreak order the work supports, or null when it builds no action.</summary>
    public LayoutCandidate? FirebreakStep { get; init; }

    /// <summary>How many planned rooms are owed a vent or a roof (EnvelopeWork); unknown without a layout plan or colony ground.</summary>
    public Fact<int>? ClimateEnvelope { get; init; }

    /// <summary>The flammable owned walls a stone upgrade is owed (ReviewStoneShell).</summary>
    public Fact<EquatableArray<string>>? StoneShell { get; init; }

    /// <summary>The stored defensive layout and the map's fights (<c>family/defense_layout</c>, the military/combat Need's journal).</summary>
    public Fact<DefenseLayoutFacts>? Defense { get; init; }

    /// <summary>The routes census reviewed this read (ReviewRoutes) and whether a traffic door is owed a hold-open change.</summary>
    public Fact<RoutesStanding>? Routes { get; init; }
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
/// unread census leaves it unknown.
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
            needs.Add(HomeCoverageNeed(facts.HomeCoverage ?? Fact.Unknown<HomeAreaPlan>()));
        }
        if (composed.ClimateEnvelope)
        {
            needs.Add(ClimateEnvelopeNeed(facts.ClimateEnvelope ?? Fact.Unknown<int>()));
        }
        if (composed.Firebreak)
        {
            needs.Add(FirebreakNeed(facts.Firebreak ?? Fact.Unknown<FirebreakWork>()) with
            {
                Preconditions = composed.HomeCoverage ? [HomeCoverage] : [],
            });
        }
        if (composed.StoneShell)
        {
            needs.Add(StoneShellNeed(facts.StoneShell ?? Fact.Unknown<EquatableArray<string>>()) with
            {
                Preconditions = composed.ClimateEnvelope ? [ClimateEnvelope] : [],
            });
        }
        if (composed.Defense)
        {
            needs.Add(DefenseNeed(facts.Defense ?? Fact.Unknown<DefenseLayoutFacts>()));
        }
        if (composed.Routes)
        {
            needs.Add(RoutesNeed(facts.Routes ?? Fact.Unknown<RoutesStanding>()));
        }
        return needs;
    }

    /// <summary>The home-area diff and the firebreak order; the envelope and stone shell are the runtime step's, which previews and admits buildings.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, LayoutFacts facts)
    {
        var output = new List<NeedMethod>();
        foreach (var need in ready)
        {
            if (need.Id == HomeCoverage && facts.HomeCoverage is { } home && home.TryGetValue(out var diff) && !diff.Empty())
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

    /// <summary>Unmet while the home area differs from the planned coverage, unknown while the geometry, Home cells or auto-expand state cannot be read.</summary>
    public static Need HomeCoverageNeed(Fact<HomeAreaPlan> planned)
    {
        if (!planned.TryGetValue(out var diff))
        {
            return new Need { Id = HomeCoverage, Tier = NeedTier.Wellbeing, Evidence = [new("home_coverage", default)] };
        }
        var cells = diff.Set.Count + diff.Clear.Count;
        return new Need
        {
            Id = HomeCoverage,
            Tier = NeedTier.Wellbeing,
            Satisfaction = diff.Empty() ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = Fact.Known((double)cells + (diff.AutoOff ? 1 : 0)),
            Evidence = [new("set_cells", Count(diff.Set.Count)), new("clear_cells", Count(diff.Clear.Count)), new("auto_off", Fact.Known(diff.AutoOff ? "true" : "false"))],
        };
    }

    /// <summary>Unmet while the ring owes a cut or a ruin, unknown while this tick's ring review is missing.</summary>
    public static Need FirebreakNeed(Fact<FirebreakWork> ring)
    {
        if (!ring.TryGetValue(out var work))
        {
            return new Need { Id = Firebreak, Tier = NeedTier.Wellbeing, Evidence = [new("firebreak_work", default)] };
        }
        return new Need
        {
            Id = Firebreak,
            Tier = NeedTier.Wellbeing,
            Satisfaction = work.Owed ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = Fact.Known((double)(work.Cut.Count + work.Deconstruct.Count)),
            Evidence = [new("cut_cells", Count(work.Cut.Count)), new("ruins", Count(work.Deconstruct.Count))],
        };
    }

    /// <summary>Unmet while a planned room is owed a vent or a roof, unknown without the layout plan or colony ground.</summary>
    public static Need ClimateEnvelopeNeed(Fact<int> owed)
    {
        if (!owed.TryGetValue(out var rooms))
        {
            return new Need { Id = ClimateEnvelope, Tier = NeedTier.Wellbeing, Evidence = [new("envelope_rooms", default)] };
        }
        DomainException.Require(rooms >= 0, "invalid envelope room count");
        return new Need
        {
            Id = ClimateEnvelope,
            Tier = NeedTier.Wellbeing,
            Satisfaction = rooms > 0 ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = Fact.Known((double)rooms),
            Evidence = [new("envelope_rooms", Count(rooms))],
        };
    }

    /// <summary>Unmet while an owned wall is flammable, unknown while the owned construction or the structure census cannot be read.</summary>
    public static Need StoneShellNeed(Fact<EquatableArray<string>> walls)
    {
        if (!walls.TryGetValue(out var rows))
        {
            return new Need { Id = StoneShell, Tier = NeedTier.Development, Evidence = [new("flammable_walls", default)] };
        }
        return new Need
        {
            Id = StoneShell,
            Tier = NeedTier.Development,
            Satisfaction = rows.Count > 0 ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = Fact.Known((double)rows.Count),
            Evidence = [new("flammable_walls", Count(rows.Count))],
        };
    }

    /// <summary>
    /// Unmet while the stored layout's verification is not current or an open fight waits in rooms to harden; unknown
    /// while the layout facts are unread. The deficit is zero while every tier stands (a re-verification or a fight
    /// only) and one while a tier is owed.
    /// </summary>
    public static Need DefenseNeed(Fact<DefenseLayoutFacts> layout)
    {
        if (!layout.TryGetValue(out var f))
        {
            return new Need { Id = Defense, Tier = NeedTier.Development, Evidence = [new("defense_layout", default)] };
        }
        return new Need
        {
            Id = Defense,
            Tier = NeedTier.Development,
            Satisfaction = f.Verified && !f.WaitingFight ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = Fact.Known(f.Standing ? 0.0 : 1.0),
            Evidence = [new("standing", Flag(f.Standing)), new("verified", Flag(f.Verified)), new("waiting_fight", Flag(f.WaitingFight))],
        };
    }

    /// <summary>
    /// Unmet while the routes census reads a facility no mobile colonist reaches or a traffic door is owed a change;
    /// unknown while the census is unread. A plain threshold: a facility whose ordered door is framed reads reachable.
    /// </summary>
    public static Need RoutesNeed(Fact<RoutesStanding> standing)
    {
        if (!standing.TryGetValue(out var s) || !s.Review.Known)
        {
            return new Need { Id = Routes, Tier = NeedTier.Development, Evidence = [new("routes_census", default)] };
        }
        var unreachable = s.Review.Deficits.Count;
        return new Need
        {
            Id = Routes,
            Tier = NeedTier.Development,
            Satisfaction = s.Review.Active || s.DoorOwed ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = Fact.Known((double)unreachable + (s.DoorOwed ? 1 : 0)),
            Evidence = [new("unreachable_facilities", Count(unreachable)), new("door_owed", Flag(s.DoorOwed))],
        };
    }

    /// <summary>The routes standing of one read: the census reviewed fresh, and the traffic door changes (not heat) owed.</summary>
    public static RoutesStanding RoutesStandingOf(Fact<RoutesObservation> routes, Fact<RoomObservation> rooms, RoundsPolicy p) =>
        new(RoutesRules.ReviewRoutes(routes, p.Routes), DoorControlRules.DoorChanges(rooms, routes, p).Any(d => !d.Heat));

    private static Fact<string> Flag(bool b) => Fact.Known(b ? "true" : "false");

    private static Fact<string> Count(int n) => Fact.Known(n.ToString(CultureInfo.InvariantCulture));
}
