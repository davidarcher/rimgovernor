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

    /// <summary>The latches the reviews yield; R.Latches starts as L and the housing, comfort and medical detectors set their own phase on it.</summary>
    public RoundsLatches L { get; set; } = new();

    public RoundsFindings R { get; set; } = new();

    /// <summary>The review's one resource-demand value (ResourceDemandOf).</summary>
    public DerivedDemand Demand { get; set; } = new();

    public MedicalReserveReview Medicine { get; set; } = default!;

    public RoutesReview Routes { get; set; } = default!;

    public bool SleepingActive { get; set; }

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

    /// <summary>A retained latch with missing input preserves history, not fresh evidence.</summary>
    internal static Fact<bool> LatchRecovered(bool active, Fact<double> observed) => Measured(observed, _ => !active);

    internal static void InspectAnswerDialog(RoundsRun c)
    {
        if (Positive(c.F.ChoiceDialog))
        {
            c.Raise(AnswerDialog);
        }
        c.Assess(AnswerDialog, NotFact(c.F.ChoiceDialog));
    }

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

    internal static void InspectHousing(RoundsRun c)
    {
        var housing = ReviewHousing(c.F, c.Previous, c.P, c.SleepingActive);
        c.R.Latches.Housing = housing.Phase;
        if (housing.Phase != Phase.None)
        {
            var g = c.Raise(MaintainHousing);
            g.Deficit = housing.Deficit;
        }
        c.Assess(MaintainHousing, housing.Recovered);
    }

    internal static void InspectTemperatureSafety(RoundsRun c)
    {
        var f = c.F;
        var p = c.P;
        var l = c.L;
        var temperatureMet = FootholdTemperature(f, p);
        if (Positive(f.TemperatureOwed))
        {
            // A refuel switch or a room below its sleepers' band holds the goal open.
            temperatureMet = Known(false);
        }
        if (DoorChanges(f.Upkeep.Rooms, f.Upkeep.Routes, p).Any(d => d.Heat))
        {
            temperatureMet = Known(false);
        }
        if (l.Cold || l.Hot || !Positive(temperatureMet))
        {
            c.Raise(EnsureTemperatureSafety);
        }
        c.Assess(EnsureTemperatureSafety, AllFacts(temperatureMet, LatchRecovered(l.Cold, Fallback(f.SleepingMin, f.OutdoorTemperature)), LatchRecovered(l.Hot, Fallback(f.SleepingMax, f.OutdoorTemperature))));
    }

    internal static void InspectBasicPower(RoundsRun c)
    {
        var powerMet = FootholdPower(c.F);
        if (!Positive(powerMet))
        {
            // A solar flare with a known remaining duration switches every powered building off for hours:
            // the power deficit it measures is real but answering it with a generator is not, so the goal
            // stays open with no method (it neither extends the startup hold nor is cancelled) until the
            // flare ends and the planner can tell an outage from a shortfall.
            c.Raise(EnsureBasicPower).MethodUnavailable = PowerOutageHold(c.F.DisasterConditions);
        }
        c.Assess(EnsureBasicPower, powerMet);
    }

    /// <summary>
    /// TradeWithCaravan is a Response: an incident per caravan visit, never a development goal. It needs a
    /// negotiator's conversation, not a development slot, and recovers by itself when the caravan leaves or
    /// nothing is left worth trading. Restore parts a bench could make do not stand the goal.
    /// </summary>
    internal static void InspectTrade(RoundsRun c)
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
        c.Assess(TradeWithCaravan, TradeRecovered(f.Traders, PopulationTradeNeed(tradeNeed, JoinerCapacity(f.JoinerCapacity()))));
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

    /// <summary>MaintainTrade is owed while a purchase need's silver gap is open. A closed gap recovers it; an unread gap raises nothing.</summary>
    internal static void InspectExport(RoundsRun c)
    {
        var recovered = Unknown<bool>();
        var gap = ReviewSilverGap(c.F, c.P, c.Medicine, c.Demand);
        if (gap.TryGetValue(out var g))
        {
            recovered = Known(!(g > 0));
        }
        c.Assess(MaintainTrade, recovered);
        if (recovered.TryGetValue(out var v) && !v)
        {
            // Binary like the other upkeep needs: the silver amount is not a fraction.
            c.Raise(MaintainTrade).Deficit = Known(1.0);
        }
    }

    /// <summary>
    /// A Safe area edit is owed. A settings write, unknown raises nothing.
    /// </summary>
    internal static void InspectShelter(RoundsRun c)
    {
        c.Owed(MaintainShelter, c.F.SafeAreaOwed);
    }

    /// <summary>MaintainMechs: a mech is owed inside the bandwidth.</summary>
    internal static void InspectMechs(RoundsRun c) => c.Owed(MaintainMechs, c.F.MechGestationOwed);

    /// <summary>MaintainPsylink: a held neuroformer waits for a willing colonist.</summary>
    internal static void InspectPsylink(RoundsRun c) => c.Owed(MaintainPsylink, c.F.PsylinkOwed);


    /// <summary>A colonist holds permit points for a permit worth taking. Unknown without the royalty read raises nothing.</summary>
    internal static void InspectPermits(RoundsRun c)
    {
        c.Assess(MaintainPermits, PermitsSpent(c.F.Royalty));
        var owed = NextPermitOf(c.F.Royalty) is not null;
        if (owed)
        {
            c.Raise(MaintainPermits).Deficit = Known(1.0);
        }
    }

    /// <summary>Without the Biotech read the need is unknown and raises no goal. It is always assessed so the stored bindings match the set the review loader derives from empty facts.</summary>
    internal static void InspectPollution(RoundsRun c)
    {
        var cleared = Unknown<bool>();
        if (PollutionDeficit(c.F.Pollution).TryGetValue(out var deficit))
        {
            cleared = Known(!deficit);
        }
        c.Assess(ManagePollution, cleared);
        if (c.F.Pollution.TryGetValue(out _) && !Positive(cleared))
        {
            c.Raise(ManagePollution);
        }
    }

    /// <summary>Unknown without the charger read, and then it raises no goal. Always assessed, like ManagePollution.</summary>
    internal static void InspectMechCharger(RoundsRun c)
    {
        c.Assess(EnsureMechCharger, NotFact(c.F.MechChargerOwed));
        if (c.F.MechChargerOwed.TryGetValue(out var owed) && owed)
        {
            c.Raise(EnsureMechCharger);
        }
    }

    /// <summary>Unknown without the gene-building read, and then it raises no goal. Always assessed, like EnsureMechCharger.</summary>
    internal static void InspectGeneBank(RoundsRun c)
    {
        c.Assess(MaintainGeneBank, NotFact(c.F.GeneBankOwed));
        if (c.F.GeneBankOwed.TryGetValue(out var owed) && owed)
        {
            c.Raise(MaintainGeneBank);
        }
    }

    /// <summary>A standing stockpile edit is the deficit; availability is gated through AvailableMethods.</summary>
    internal static void InspectStockpiles(RoundsRun c)
    {
        var recovered = Unknown<bool>();
        if (c.F.Stockpiles.TryGetValue(out var review) && review.Known)
        {
            recovered = Known(!review.Active);
        }
        c.Assess(MaintainStockpiles, recovered);
        if (!Positive(recovered))
        {
            c.Raise(MaintainStockpiles);
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
