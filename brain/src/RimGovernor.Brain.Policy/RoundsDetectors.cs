namespace RimGovernor.Brain.Policy;

/// <summary>
/// One InspectRounds pass: the facts, the policy, the reviews every detector may read (run once, before
/// any detector, because the latches are built from them) and the needs the detectors append to. The
/// registry runs the detectors in order; an assessment lands where its detector sits, so the order of
/// inspections is the order of RoundsFindings.Assessments (the stored bindings are index-aligned).
/// </summary>
public sealed class RoundsRun(RoundsFacts f, RoundsLatches previous, RoundsPolicy p)
{
    public RoundsFacts F { get; set; } = f;

    public RoundsLatches Previous { get; set; } = previous;

    public RoundsPolicy P { get; set; } = p;

    /// <summary>The latches the reviews yield; R.Latches starts as L and the comfort and medical detectors set their own phase on it.</summary>
    public RoundsLatches L { get; set; } = new();

    public RoundsFindings R { get; set; } = new();

    /// <summary>The review's one resource-demand value (ResourceDemandOf).</summary>
    public DerivedDemand Demand { get; set; } = new();

    public MedicalReserveReview Medicine { get; set; } = default!;

    public RoutesReview Routes { get; set; } = default!;

    /// <summary>Appends a Concern and returns it for the detector to adjust.</summary>
    public RoundsConcern Raise(ConcernId id)
    {
        var concern = new RoundsConcern { Id = id, Deficit = PolicyFunctions.RoundsDeficit(id, F, P) };
        R.Concerns.Add(concern);
        return concern;
    }

    /// <summary>Appends id's assessment and returns it for the detector to adjust.</summary>
    public RoundsAssessment Assess(ConcernId id, Fact<bool> recovered)
    {
        var need = Finding.Unclear;
        if (recovered.TryGetValue(out var value))
        {
            need = value ? Finding.Met : Finding.Unmet;
        }
        var assessment = new RoundsAssessment { Id = id, Finding = need };
        R.Assessments.Add(assessment);
        return assessment;
    }

    /// <summary>Assesses a goal that is recovered unless its fact says a step is owed; unknown raises nothing. A known deficit is a full one.</summary>
    public void Owed(ConcernId id, Fact<bool> owed)
    {
        Assess(id, PolicyFunctions.NotFact(owed));
        if (owed.TryGetValue(out var v) && v)
        {
            Raise(id).Deficit = Known(1.0);
        }
    }
}

public static partial class PolicyFunctions
{
    internal static Fact<bool> NotFact(Fact<bool> f) => Measured(f, v => !v);

    internal static void InspectActiveCombat(RoundsRun c)
    {
        var f = c.F;
        if (!Positive(CombatCleared(f)))
        {
            c.Raise(ActiveCombat);
        }
        var a = c.Assess(ActiveCombat, CombatCleared(f));
        if (Positive(Measured(f.Hostiles, n => n == 0)))
        {
            a.Hunt = EquatableArray.From(HuntRequest(f.FoodPlan));
        }
    }

    internal static void InspectWorkAssignments(RoundsRun c)
    {
        var f = c.F;
        var workMet = AllFacts(f.WorkCoverage, Measured(f.CleanupPawns, v => !v));
        if (f.CoupledWorkActive || c.R.CoupledSelection is { Selection.Selected.Length: > 0 } or { Deferred.Count: > 0 }) workMet = Known(false);
        if (Positive(f.HostilityOwed) || Positive(f.NamesOwed) || Positive(f.MedicineCarryOwed) || Positive(f.MedicalCareOwed))
        {
            workMet = Known(false);
        }
        if (!Positive(workMet))
        {
            c.Raise(EnsureWorkAssignments);
        }
        c.Assess(EnsureWorkAssignments, workMet);
    }

    /// <summary>
    /// The review's trade facts the economy family's trade and caravan Needs read (#3316): the silver gap a
    /// purchase need leaves open, and whether the caravans present leave nothing worth trading.
    /// </summary>
    internal static TradeReview TradeReviewOf(RoundsRun c) => new()
    {
        SilverGap = ReviewSilverGap(c.F, c.P, c.Medicine, c.Demand),
        CaravanRecovered = CaravanRecoveredOf(c),
    };

    /// <summary>
    /// Whether a caravan visit leaves nothing to trade: known true when no tradeable caravan is present or nothing
    /// is worth trading, unknown while the trader census or the need is unread. Restore parts a bench could make
    /// do not count.
    /// </summary>
    internal static Fact<bool> CaravanRecoveredOf(RoundsRun c)
    {
        var f = c.F;
        var p = c.P;
        var tradeNeed = AnimalSaleNeed(
            f.Items,
            ShedArtNeed(
                SurgeryTradeNeed(
                    ReserveSurgeryStock(
                        OrganSaleSurplus(
                            f.Items,
                            ExportSaleSurplus(
                                f.Items,
                                ReviewTradeNeed(f.Items, c.Medicine, f.Resources, c.Demand.Needs, RoundsTradeFloors(p, null), f.Wealth, c.Demand.Retained, RoundsTradeFood(f, p)),
                                f.Resources,
                                f.Colonists,
                                f.ExportProducts),
                            f.Resources,
                            f.Colonists,
                            f.Recipes),
                        f.MedicalPawns),
                    SurgeryPurchaseParts(f.MedicalPawns, f.SurgeryContextOf(), SurgeryParts(SelectSurgery(f.MedicalPawns, null, new SurgeryContext()).Wants), f.FabricableParts ?? new HashSet<Resource>())),
                f.WealthBudget(),
                f.SaleArt),
            f.SaleAnimals(),
            f.Silver(),
            f.Colonists);
        tradeNeed = FavorGoldNeed(tradeNeed, f.Traders, f.Resources, c.Demand.Needs, RoundsTradeFloors(p, null), c.Demand.Retained);
        var shortage = RoundsSilverShort(f, p, c.Medicine.Active).TryGetValue(out var silverShort) && silverShort;
        tradeNeed = FavorPrisonerNeed(tradeNeed, f.Traders, f.SurplusPrisoners(shortage));
        return TradeRecovered(f.Traders, PopulationTradeNeed(tradeNeed, JoinerCapacity(f.JoinerCapacity())));
    }

    /// <summary>EnsureDefensiveLayout is config-only: opt-in activates the goal at priority 3 (after the storage gate) and the planner reports no work once every tier stands.</summary>
    internal static void InspectDefensiveLayout(RoundsRun c)
    {
        var recovered = Known(!c.P.DefensiveLayout);
        if (!Positive(recovered))
        {
            c.Raise(EnsureDefensiveLayout);
        }
        c.Assess(EnsureDefensiveLayout, recovered);
    }

    /// <summary>A ranked development project: the deficit is the measured fraction of facilities no colonist reaches.</summary>
    internal static void InspectRoutes(RoundsRun c)
    {
        var recovered = Unknown<bool>();
        if (c.Routes.Known)
        {
            recovered = Known(!c.Routes.Active);
        }
        if (DoorChanges(c.F.Upkeep.Rooms, c.F.Upkeep.Routes, c.P).Count > 0)
        {
            recovered = Known(false);
        }
        c.Assess(MaintainRoutes, recovered);
        if (!Positive(recovered))
        {
            var g = c.Raise(MaintainRoutes);
            if (c.Routes.Known)
            {
                g.Deficit = Known(1.0);
            }
        }
    }

    /// <summary>
    /// A pawn's mood is an EnsureMood incident keyed by the pawn, never a development goal. Its relief is
    /// optional and a mental break ends only as ticks pass, so it never suspends other work.
    /// </summary>
    internal static void InspectMood(RoundsRun c)
    {
        c.F.Mood.Validate();
        foreach (var state in c.F.Mood.States)
        {
            c.R.Assessments.Add(new RoundsAssessment { Id = EnsureMood, Subject = state.Pawn.Id, Finding = state.Need(), MethodUnavailable = true });
        }
    }

    /// <summary>Runs last: the disaster review files its own assessment.</summary>
    internal static void InspectDisaster(RoundsRun c)
    {
        var f = c.F;
        var r = c.R;
        r.Disaster = ReviewDisaster(f.DisasterConditions, f.RecoveryBuildings, DisasterServiceFacts(f, c.P), f.Disaster, f.DisasterTick, f.ShortCircuitTick);
        var disaster = r.Disaster;
        var areaChanges = PlanSheltering(f);
        if (disaster is not null || f.RecoverySafety.TryGetValue(out _))
        {
            var need = RecoveryNeed(disaster);
            if (areaChanges.Count > 0)
            {
                need = Finding.Unmet;
            }
            r.Assessments.Add(new RoundsAssessment { Id = RecoverDisasterServices, Finding = need });
        }
    }
}
