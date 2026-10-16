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

    public Fact<HomeAreaPlan> Home { get; set; }

    public Fact<EquatableArray<string>> Stone { get; set; }

    public MedicalReserveReview Medicine { get; set; } = default!;

    public LightingReview Lighting { get; set; } = default!;

    public FlooringReview Flooring { get; set; } = default!;

    public RoutesReview Routes { get; set; } = default!;

    public bool SleepingActive { get; set; }

    public bool HomeActive { get; set; }

    public bool StoneActive { get; set; }

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

    internal static void InspectCriticalMedicine(RoundsRun c)
    {
        var medicalMet = Measured(c.F.CriticalPatients, v => v == 0);
        if (!Positive(medicalMet))
        {
            c.Raise(CriticalMedicine);
        }
        c.Assess(CriticalMedicine, medicalMet);
    }

    internal static void InspectRestoreWorkers(RoundsRun c)
    {
        var f = c.F;
        if (Positive(Measured(f.Hostiles, n => n == 0)) && Positive(f.CleanupPawns))
        {
            c.Raise(RestoreWorkers);
        }
        c.Assess(RestoreWorkers, NotFact(f.CleanupPawns));
    }

    internal static void InspectManageSupplySafety(RoundsRun c)
    {
        if (Positive(c.F.EventLootPending))
        {
            c.Raise(ManageSupplySafety);
        }
        c.Assess(ManageSupplySafety, NotFact(c.F.EventLootPending));
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

    internal static void InspectComfort(RoundsRun c)
    {
        var f = c.F;
        var basicComfort = ReviewBasicComfort(f.BasicComfort);
        // A table with a seat and a recreation source are provided with the starter hut, at foothold
        // priority: the two cheapest mood debuffs to remove should not wait for the ranked comfort project.
        if (!Positive(basicComfort.Recovered()))
        {
            c.R.Latches.Comfort = Phase.ComfortBasic;
            var g = c.Raise(EnsureComfort);
            g.Deficit = basicComfort.Deficit();
            g.MethodUnavailable = basicComfort.CoreRecovered() && !basicComfort.VarietyKnown;
        }
        else if (!Positive(f.ComfortRecovered))
        {
            // The ranked phase: hosting rooms and proof of use, once the basic facilities stand and the
            // colony reached StageDevelopment.
            c.R.Latches.Comfort = Phase.ComfortRanked;
            var g = c.Raise(EnsureComfort);
            g.Deficit = f.ComfortDeficit;
        }
        else if (Positive(f.PartySpotOwed))
        {
            // Neither facility phase is owed, but the colony's PartySpot is missing, duplicated or outranked
            // by a better shared room. The step is one placement or one deconstruction (the review drops the
            // deficit once the one-shot is spent or no cell takes it), so comfort is Unmet for a pass or
            // two, not indefinitely.
            c.R.Latches.Comfort = Phase.ComfortSpot;
            c.Raise(EnsureComfort).Deficit = Known(1.0);
        }
        var recovered = basicComfort.Recovered();
        if (c.R.Latches.Comfort != Phase.ComfortBasic)
        {
            // The ranked phase is assessed at every stage, as before the merge; only its goal waits for StageDevelopment.
            recovered = AllFacts(recovered, f.ComfortRecovered);
            if (Positive(f.PartySpotOwed))
            {
                recovered = Known(false);
            }
        }
        c.Assess(EnsureComfort, recovered);
    }

    internal static void InspectEquipment(RoundsRun c)
    {
        var f = c.F;
        var gear = ReviewGear(f.Gear);
        foreach (var l in gear.Loadouts)
        {
            if (l.SearchBudgetExhausted)
            {
                c.R.GearBudgetPawns.Add(l.Pawn);
            }
        }
        // A crafting spot beside the weapon bench it stood in for keeps the goal open for its deconstruction.
        var retire = Positive(f.CraftingSpotRetireOwed);
        if (!Positive(gear.Recovered) || retire)
        {
            var g = c.Raise(MaintainEquipment);
            g.Deficit = gear.Deficit;
            if (retire && Positive(gear.Recovered))
            {
                g.Deficit = Known(1.0);
            }
        }
        var equipped = gear.Recovered;
        if (retire)
        {
            equipped = Known(false);
        }
        if (f.ShellsShort.TryGetValue(out var shellsShort) && shellsShort)
        {
            equipped = Known(false);
        }
        c.Assess(MaintainEquipment, equipped);
    }

    /// <summary>
    /// MaintainResource is an operator-configured target whose deficit is measured against native facts read in
    /// this review: no target configured is certain recovery, a configured target with missing facts is unknown,
    /// and RoundsResourcePlanner still re-reads native state immediately before proposing a method.
    /// </summary>
    internal static void InspectResource(RoundsRun c)
    {
        var f = c.F;
        var l = c.L;
        // The review's resource demand (construction, bills, clothing, fuel, animal feed, runway targets,
        // evidence needs) is a stock floor on MaintainResource.
        var stock = new StockReader { Resources = f.Resources, Wood = f.Wood };
        var resourceTargets = c.Demand.Needs;
        var (resourceRecovered, resourceDeficit) = ResourceTargetNeed(resourceTargets, stock.Census(resourceTargets));
        foreach (var runway in f.ResourceRunways)
        {
            var deficitKnown = runway.Deficit.TryGetValue(out var runwayDeficit);
            if (!deficitKnown && runway.WindowDays >= 1 && Positive(resourceRecovered))
            {
                resourceRecovered = Unknown<bool>();
                resourceDeficit = Unknown<double>();
            }
            if (deficitKnown && runwayDeficit)
            {
                resourceRecovered = Known(false);
                if (runway.DaysLeft.TryGetValue(out var days))
                {
                    resourceDeficit.TryGetValue(out var old);
                    resourceDeficit = Known(Math.Max(old, 1 - days / MaintenanceHorizonDays));
                }
            }
        }
        if (!Positive(resourceRecovered))
        {
            var g = c.Raise(MaintainResource);
            g.Deficit = resourceDeficit;
            // The ladder's research rung: while a project the workshop recorded as gating the bench is
            // unfinished, the goal has no method of its own and holds no slot; the economy/research Need works
            // the project. Construction demand is chopped or mined meanwhile.
            g.MethodUnavailable = ResearchConcernTarget("", f.ResearchNeeds, f.Research) != "" && c.Demand.Works.Count == 0;
        }
        c.Assess(MaintainResource, resourceRecovered);
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

    /// <summary>
    /// Serves a home-area facility goal: a binary need, like the upkeep goals. A confirmed deficit ranks at
    /// Known(1.0); an unknown census stays DevelopmentUnknown. Method availability follows the composed
    /// capability list, since both verticals dispatch.
    /// </summary>
    internal static void FacilityInspect(RoundsRun c, ConcernId id, Func<RoundsRun, Fact<bool>> recoveredOf, Func<RoundsRun, bool> active)
    {
        var recovered = recoveredOf(c);
        if (c.F.IsIssued(id))
        {
            recovered = Known(false);
        }
        c.Assess(id, recovered);
        if (!Positive(recovered))
        {
            var g = c.Raise(id);
            if (recovered.TryGetValue(out _))
            {
                g.Deficit = Known(1.0);
            }
        }
    }

    internal static void InspectHomeCoverage(RoundsRun c) => FacilityInspect(c, MaintainHomeCoverage, r =>
    {
        if (r.Home.TryGetValue(out var diff))
        {
            return Known(diff.Empty());
        }
        return Unknown<bool>();
    }, r => r.HomeActive);

    internal static void InspectStoneShell(RoundsRun c) => FacilityInspect(c, MaintainStoneShell, r =>
    {
        if (r.Stone.TryGetValue(out var rows))
        {
            return Known(rows.Count == 0);
        }
        return Unknown<bool>();
    }, r => r.StoneActive);

    internal static void InspectMedicalReserves(RoundsRun c)
    {
        var f = c.F;
        var medicine = c.Medicine;
        // A plan issued for the care phase is not a medicine bill.
        var active = medicine.Active || (f.IsIssued(MaintainMedicalReserves) && c.Previous.Medical != Phase.MedicalCare);
        var reserveRecovered = Unknown<bool>();
        if (medicine.Stock.TryGetValue(out _))
        {
            reserveRecovered = Known(!active);
        }
        // MaintainMedicalReserves is the one medical upkeep goal: resting and hospital care for the sick
        // first then the medicine stock. The care phase keeps its pre-merge shape:
        // priority 2, no ranked method and no labor, since work assignments own disease rest and monitoring
        // recovery must not reserve execution capacity while the pawn rests.
        var recovered = AllFacts(f.MedicalCareRecovered, reserveRecovered);
        if (!Positive(f.MedicalCareRecovered))
        {
            c.R.Latches.Medical = Phase.MedicalCare;
            var g = c.Raise(MaintainMedicalReserves);
            g.MethodUnavailable = true;
        }
        else if (!Positive(reserveRecovered))
        {
            c.R.Latches.Medical = Phase.MedicalReserves;
            c.Raise(MaintainMedicalReserves);
        }
        c.Assess(MaintainMedicalReserves, recovered);
    }

    /// <summary>An operation the planner serves stands on a living colonist until the health change removes it.</summary>
    internal static void InspectSurgery(RoundsRun c)
    {
        var f = c.F;
        var p = c.P;
        // An actionable elective upgrade keeps it open too.
        var recovered = AllFacts(SurgeryRecovered(f.MedicalPawns), Measured(ElectiveSurgeryOwed(f.MedicalPawns, f.SurgeryContextOf(), f.FabricableParts), owed => !owed));
        // A sale organ harvest holds it open while the silver runway is short and a prisoner's organ clears
        // its cost; a prisoner's recoverable artificial part too, and a peg-leg step: doctor training below
        // the Medicine floor, prisoner control or a reinstall before release. A prisoner whose care allows
        // better than herbal too.
        if (SaleHarvestWanted(f, ReviewSilverShort(f, p, c.Medicine, c.Demand)) || PartRecoveryWanted(f) || PegCycleWanted(f, p.Prisoners()))
        {
            recovered = Known(false);
        }
        c.Assess(MaintainSurgery, recovered);
        if (!Positive(recovered))
        {
            c.Raise(MaintainSurgery);
        }
    }

    /// <summary>Owed while babies have no breastfeeder and too little baby-edible food; unknown raises nothing.</summary>
    internal static void InspectBabyFeeding(RoundsRun c)
    {
        var recovered = Measured(c.F.BabyFeeding, b => !b.IsShort);
        c.Assess(MaintainBabyFeeding, recovered);
        if (recovered.TryGetValue(out var v) && !v)
        {
            c.Raise(MaintainBabyFeeding);
        }
    }

    /// <summary>
    /// A ranked development project: a dark bench costs work speed and mood, not lives, so it competes for
    /// a project slot like the other upkeep needs. The deficit is the measured dark fraction.
    /// </summary>
    internal static void InspectLighting(RoundsRun c)
    {
        var recovered = Unknown<bool>();
        if (c.Lighting.Known)
        {
            recovered = Known(!c.Lighting.Active);
        }
        c.Assess(MaintainLighting, recovered);
        if (!Positive(recovered))
        {
            var g = c.Raise(MaintainLighting);
            if (c.Lighting.Known)
            {
                g.Deficit = Known(1.0);
            }
        }
    }

    /// <summary>Likewise a ranked project, at the lowest rank in every tier.</summary>
    internal static void InspectFlooring(RoundsRun c)
    {
        var flooring = c.Flooring;
        var recovered = Unknown<bool>();
        if (flooring.Known)
        {
            recovered = Known(!flooring.Active);
        }
        c.Assess(MaintainFlooring, recovered);
        if (!Positive(recovered))
        {
            var g = c.Raise(MaintainFlooring);
            if (flooring.Known)
            {
                g.Deficit = Known(1.0);
            }
        }
    }

    /// <summary>Likewise a ranked project: the deficit is the measured fraction of facilities no colonist reaches.</summary>
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

    /// <summary>A ranked upkeep project too: owed only while a room needs a sculpture and a qualifying artist exists; unknown raises nothing.</summary>
    internal static void InspectArt(RoundsRun c)
    {
        var f = c.F;
        var recovered = Unknown<bool>();
        // An inspired artist holds it open without a room.
        var pk = f.WorkProfiles.TryGetValue(out var profiles);
        if (pk && InspiredArtists(profiles).Count > 0)
        {
            recovered = Known(false);
        }
        else
        {
            var known = f.SculptureRoomsOwed.TryGetValue(out var owed);
            if (known && !owed)
            {
                recovered = Known(true);
            }
            else if (known && pk)
            {
                recovered = Known(Artists(profiles).Count == 0);
            }
        }
        c.Assess(MaintainArt, recovered);
        if (recovered.TryGetValue(out var v) && !v)
        {
            c.Raise(MaintainArt).Deficit = Known(1.0);
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

    /// <summary>MaintainFirebreak: ring work is owed.</summary>
    internal static void InspectFirebreak(RoundsRun c) => c.Owed(MaintainFirebreak, c.F.FirebreakOwed);

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

    /// <summary>
    /// The joiner and anomaly halves of the population; prisoners are their own family (#3262). An unknown
    /// optional sub-step fact is not held against recovery: only a known deficit blocks it. joinerDeficit needs
    /// the quest census (RoundsPopulationJoinerPlanner).
    /// </summary>
    internal static void InspectPopulation(RoundsRun c)
    {
        var f = c.F;
        var recovered = Unknown<bool>();
        var joinerDeficitKnown = JoinerDeficit(f.QuestOffers, JoinerCapacity(f.JoinerCapacity()), f).TryGetValue(out var joinerDeficit);
        var questKnown = QuestDeficit(f).TryGetValue(out var questDeficit);
        var letterKnown = JoinerLetterDeficit(f.JoinerLetters, JoinerCapacity(f.JoinerCapacity())).TryGetValue(out var letterDeficit);
        var ceremonyStarts = CeremonyStartOf(f.Royalty) is not null;
        if (LanceTarget(f) is not null || EntityCaptureOwed(f.Containment) || EntityDoorOwed(f.Containment) || EntityTendOwed(f.Containment)
            || EntityBioferriteOwed(f.Containment, f.Research) || MonolithAdvanceOwedNow(f)
            || (joinerDeficitKnown && joinerDeficit) || (questKnown && questDeficit) || (letterKnown && letterDeficit)
            || ceremonyStarts || DecreeDeficit(f) || MonumentDeficit(f) || DepartureDeficit(f) || RefugeeDeficit(f) || GravInspectionDeficit(f))
        {
            recovered = Known(false);
        }
        else if (joinerDeficitKnown)
        {
            recovered = Known(!joinerDeficit);
        }
        c.Assess(MaintainPopulation, recovered);
        if (!Positive(recovered))
        {
            c.Raise(MaintainPopulation).MethodUnavailable = true;
        }
    }

    /// <summary>Census-driven: any standing blighted plant is a full deficit; availability is gated through AvailableMethods.</summary>
    internal static void InspectBlight(RoundsRun c)
    {
        var recovered = Unknown<bool>();
        if (BlightDeficit(c.F.Blight).TryGetValue(out var deficit))
        {
            recovered = Known(!deficit);
        }
        c.Assess(RemoveBlight, recovered);
        if (!Positive(recovered))
        {
            c.Raise(RemoveBlight);
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
