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

    public AnimalUpkeepReview Animals { get; set; } = default!;

    public MedicalReserveReview Medicine { get; set; } = default!;

    public FoodStorageReview FoodStorage { get; set; } = default!;

    public RefrigerationReview Refrigeration { get; set; } = default!;

    public UpkeepReview Upkeep { get; set; } = default!;

    public LightingReview Lighting { get; set; } = default!;

    public FlooringReview Flooring { get; set; } = default!;

    public RoutesReview Routes { get; set; } = default!;

    public bool SleepingActive { get; set; }

    public bool HomeActive { get; set; }

    public bool StoneActive { get; set; }

    /// <summary>Appends a Concern and returns it for the detector to adjust.</summary>
    public RoundsConcern Raise(ConcernId id, int priority)
    {
        var concern = new RoundsConcern { Id = id, Priority = priority, Deficit = PolicyFunctions.RoundsDeficit(id, F, P) };
        R.Concerns.Add(concern);
        return concern;
    }

    /// <summary>Appends id's assessment and returns it for the detector to adjust.</summary>
    public RoundsAssessment Assess(ConcernId id, int priority, Fact<bool> recovered)
    {
        var need = Finding.Unclear;
        if (recovered.TryGetValue(out var value))
        {
            need = value ? Finding.Met : Finding.Unmet;
        }
        var assessment = new RoundsAssessment { Id = id, Priority = priority, Finding = need };
        R.Assessments.Add(assessment);
        return assessment;
    }

    /// <summary>Assesses a goal that is recovered unless its fact says a step is owed; unknown raises nothing. A known deficit is a full one.</summary>
    public void Owed(ConcernId id, int priority, Fact<bool> owed)
    {
        Assess(id, priority, PolicyFunctions.NotFact(owed));
        if (owed.TryGetValue(out var v) && v)
        {
            Raise(id, priority).Deficit = Known(1.0);
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
            c.Raise(AnswerDialog, 0);
        }
        c.Assess(AnswerDialog, 0, NotFact(c.F.ChoiceDialog));
    }

    internal static void InspectActiveCombat(RoundsRun c)
    {
        var f = c.F;
        if (!Positive(CombatCleared(f)))
        {
            c.Raise(ActiveCombat, 0);
        }
        var a = c.Assess(ActiveCombat, 0, CombatCleared(f));
        if (Positive(Measured(f.Hostiles, n => n == 0)))
        {
            a.Hunt = EquatableArray.From(HuntRequest(f.FoodPlan));
        }
    }

    internal static void InspectCriticalMedicine(RoundsRun c)
    {
        var medicalMet = Measured(c.F.CriticalPatients, v => v == 0);
        var priority = CriticalMedicinePriority(c.F);
        if (!Positive(medicalMet))
        {
            c.Raise(CriticalMedicine, priority);
        }
        c.Assess(CriticalMedicine, priority, medicalMet);
    }

    internal static void InspectRestoreWorkers(RoundsRun c)
    {
        var f = c.F;
        if (Positive(Measured(f.Hostiles, n => n == 0)) && Positive(f.CleanupPawns))
        {
            c.Raise(RestoreWorkers, 1);
        }
        c.Assess(RestoreWorkers, 1, NotFact(f.CleanupPawns));
    }

    internal static void InspectManageSupplySafety(RoundsRun c)
    {
        var priority = SupplySafetyPriority(c.F);
        if (Positive(c.F.EventLootPending))
        {
            c.Raise(ManageSupplySafety, priority);
        }
        c.Assess(ManageSupplySafety, priority, NotFact(c.F.EventLootPending));
    }

    internal static void InspectWorkAssignments(RoundsRun c)
    {
        var f = c.F;
        var workMet = AllFacts(f.WorkCoverage, Measured(f.CleanupPawns, v => !v));
        if (Positive(f.HostilityOwed) || Positive(f.SelfTendOwed) || Positive(f.NamesOwed) || Positive(f.MedicineCarryOwed) || Positive(f.EatTriggerOwed) || Positive(f.MedicalCareOwed))
        {
            workMet = Known(false);
        }
        if (!Positive(workMet))
        {
            c.Raise(EnsureWorkAssignments, 2);
        }
        c.Assess(EnsureWorkAssignments, 2, workMet);
    }

    internal static void InspectFoodSupply(RoundsRun c)
    {
        var f = c.F;
        var l = c.L;
        var foodMet = FootholdFood(f, c.P);
        var productionMet = FootholdProduction(f);
        var fieldsCovered = Measured(f.FieldCoverage, v => v >= 1 - 1e-9);
        // The butcher facility (spot, then table, in the planned butchery) is owed whatever the food
        // runway; an unread bench census raises nothing and recovers nothing.
        var butcherMet = ButcherFacilityMet(f);
        var butcherOwed = butcherMet.TryGetValue(out var met) && !met;
        if (HumanFoodPending(f.FoodPlan) || l.Food || !Positive(foodMet) || !Positive(productionMet) || !Positive(fieldsCovered) || butcherOwed)
        {
            c.Raise(EnsureFoodSupply, 2);
        }
        c.Assess(EnsureFoodSupply, 2, AllFacts(Known(!HumanFoodPending(f.FoodPlan)), foodMet, productionMet, fieldsCovered, butcherMet, LatchRecovered(l.Food, f.FoodDays)));
    }

    internal static void InspectHousing(RoundsRun c)
    {
        var housing = ReviewHousing(c.F, c.Previous, c.P, c.SleepingActive);
        c.R.Latches.Housing = housing.Phase;
        if (housing.Phase != Phase.None)
        {
            var g = c.Raise(MaintainHousing, housing.Priority);
            g.Deficit = housing.Deficit;
        }
        c.Assess(MaintainHousing, housing.Priority, housing.Recovered);
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
            c.Raise(EnsureTemperatureSafety, 2);
        }
        c.Assess(EnsureTemperatureSafety, 2, AllFacts(temperatureMet, LatchRecovered(l.Cold, Fallback(f.SleepingMin, f.OutdoorTemperature)), LatchRecovered(l.Hot, Fallback(f.SleepingMax, f.OutdoorTemperature))));
    }

    internal static void InspectCooking(RoundsRun c)
    {
        if (!Positive(CookingMet(c.F)))
        {
            c.Raise(EnsureCooking, 2);
        }
        c.Assess(EnsureCooking, 2, CookingMet(c.F));
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
            c.Raise(EnsureBasicPower, 2).MethodUnavailable = PowerOutageHold(c.F.DisasterConditions);
        }
        c.Assess(EnsureBasicPower, 2, powerMet);
    }

    internal static void InspectBasicDefense(RoundsRun c)
    {
        var f = c.F;
        var defenseMet = AllFacts(FootholdArmed(f), Measured(f.Hostiles, v => v == 0));
        var defense = BasicDefenseRecovered(defenseMet, f.Unarmed);
        if (!Positive(defense))
        {
            var g = c.Raise(EnsureBasicDefense, 3);
            var k = FootholdCount(f).TryGetValue(out var n);
            var sk = f.Armed.TryGetValue(out var stock);
            if (k && sk && n > 0)
            {
                var deficit = Math.Max(0, (double)(ArmedTarget(n) - stock) / ArmedTarget(n));
                if (f.Unarmed.TryGetValue(out var unarmed) && unarmed > 0 && deficit == 0)
                {
                    deficit = (double)unarmed / (unarmed + stock);
                }
                g.Deficit = Known(deficit);
            }
        }
        c.Assess(EnsureBasicDefense, 3, defense);
    }

    internal static void InspectComfort(RoundsRun c)
    {
        var f = c.F;
        var basicComfort = ReviewBasicComfort(f.BasicComfort);
        // A table with a seat and a recreation source are provided with the starter hut, at foothold
        // priority: the two cheapest mood debuffs to remove should not wait for the ranked comfort project.
        // While the initial shelter is still owed there is no room to furnish, so the goal holds no method
        // and neither extends the startup hold nor competes.
        var comfortRanked = c.P.ColonyStage >= ColonyStage.Development;
        if (!Positive(basicComfort.Recovered()))
        {
            c.R.Latches.Comfort = Phase.ComfortBasic;
            var g = c.Raise(EnsureComfort, basicComfort.Priority());
            g.Deficit = basicComfort.Deficit();
            g.MethodUnavailable = !Positive(FootholdShelter(f)) || !Positive(FootholdSleeping(f)) || (basicComfort.Priority() == 3 && !basicComfort.VarietyKnown);
        }
        else if (comfortRanked && !Positive(f.ComfortRecovered))
        {
            // The ranked phase: hosting rooms and proof of use, once the basic facilities stand and the
            // colony reached StageDevelopment.
            c.R.Latches.Comfort = Phase.ComfortRanked;
            var g = c.Raise(EnsureComfort, 4);
            g.Deficit = f.ComfortDeficit;
        }
        else if (Positive(f.PartySpotOwed))
        {
            // Neither facility phase is owed, but the colony's PartySpot is missing, duplicated or outranked
            // by a better shared room. The step is one placement or one deconstruction (the review drops the
            // deficit once the one-shot is spent or no cell takes it), so comfort is Unmet for a pass or
            // two, not indefinitely.
            c.R.Latches.Comfort = Phase.ComfortSpot;
            c.Raise(EnsureComfort, 4).Deficit = Known(1.0);
        }
        var priority = 4;
        var recovered = basicComfort.Recovered();
        if (c.R.Latches.Comfort == Phase.ComfortBasic)
        {
            priority = basicComfort.Priority();
        }
        else
        {
            // The ranked phase is assessed at every stage, as before the merge; only its goal waits for StageDevelopment.
            recovered = AllFacts(recovered, f.ComfortRecovered);
            if (Positive(f.PartySpotOwed))
            {
                recovered = Known(false);
            }
        }
        c.Assess(EnsureComfort, priority, recovered);
    }

    internal static void InspectPests(RoundsRun c)
    {
        // A recognised pest on the map (an alphabeaver pack eating the trees) is a foothold deficit answered
        // by hunting, priority 2: it is not an emergency (the census never holds the clock for a docile
        // animal) but it outranks every ranked project while it lasts. Only a known census with a pest
        // opens the goal: an unknown wild census (beyond the native bound) has nothing to hunt and must not
        // hold the startup ladder for good.
        var pests = PestCensus(c.F.AnimalUpkeep.WildAnimals);
        if (pests.TryGetValue(out var n) && n > 0)
        {
            c.Raise(ClearPests, 2).Deficit = Known(1.0);
        }
        c.Assess(ClearPests, 2, Measured(pests, v => v == 0));
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
            var g = c.Raise(MaintainEquipment, 3);
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
        c.Assess(MaintainEquipment, 3, equipped);
    }

    /// <summary>
    /// EnsureResearch and MaintainResource are operator-configured targets whose deficit is measured against
    /// native facts read in this review: no target configured is certain recovery, a configured target with
    /// missing facts is unknown, and RoundsResearchPlanner/RoundsResourcePlanner still re-read native state
    /// immediately before proposing a method.
    /// </summary>
    internal static void InspectResearch(RoundsRun c)
    {
        var f = c.F;
        var p = c.P;
        var researchNeeds = DeepDrillingResearch(f.ResearchNeeds, f.ResourceRunways, f.Items);
        var researchPolicy = ResearchPolicy(p, c.L.Soldiers, c.L.FoodFirst);
        var (researchTarget, researchDerived) = ResearchConcern(researchPolicy, researchNeeds, f.Research);
        var (researchRecovered, researchDeficit) = ResearchTargetNeed(researchTarget, researchDerived, f.Research);
        // An empty Anomaly knowledge slot with a project to fund is a spending need of its own; a disabled
        // goal (no target, empty ladder) funds none.
        if (f.Research.TryGetValue(out var facts) && facts.KnowledgePick.Value is { Length: > 0 } && (researchNeeds.Count != 0 || researchPolicy.ResearchLadder.Count != 0))
        {
            researchDeficit.TryGetValue(out var deficit);
            researchRecovered = Known(false);
            researchDeficit = Known(Math.Max(deficit, 1.0));
        }
        if (!Positive(researchRecovered))
        {
            c.Raise(EnsureResearch, 4).Deficit = researchDeficit;
        }
        c.Assess(EnsureResearch, 4, researchRecovered);
    }

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
                    resourceDeficit = Known(Math.Max(old, 1 - days / ProjectionHorizonDays));
                }
            }
        }
        var priority = 4;
        if (l.Wood)
        {
            // Low wood keeps the old wood goal's standing.
            priority = 3;
        }
        if (!Positive(resourceRecovered))
        {
            var g = c.Raise(MaintainResource, priority);
            g.Deficit = resourceDeficit;
            // The ladder's research rung: while a project the workshop recorded as gating the bench is
            // unfinished, the goal has no method of its own and holds no slot, so EnsureResearch can take
            // one. Construction demand is chopped or mined meanwhile.
            g.MethodUnavailable = ResearchConcernTarget("", f.ResearchNeeds, f.Research) != "" && c.Demand.Works.Count == 0;
        }
        c.Assess(MaintainResource, priority, resourceRecovered);
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
        c.Assess(TradeWithCaravan, 3, TradeRecovered(f.Traders, PopulationTradeNeed(tradeNeed, JoinerCapacity(f.JoinerCapacity()))));
    }

    /// <summary>EnsureDefensiveLayout is config-only like EnsureResearch: opt-in activates the goal at priority 3 (after the storage gate) and the planner reports no work once every tier stands.</summary>
    internal static void InspectDefensiveLayout(RoundsRun c)
    {
        var recovered = Known(!c.P.DefensiveLayout);
        if (!Positive(recovered))
        {
            c.Raise(EnsureDefensiveLayout, 3);
        }
        c.Assess(EnsureDefensiveLayout, 3, recovered);
    }

    /// <summary>Serves one of the direct upkeep goals: ReviewUpkeep files a need for each, and hold is the goal's extra method hold.</summary>
    internal static Action<RoundsRun> UpkeepInspection(ConcernId id, Func<RoundsRun, bool>? hold) => c =>
    {
        foreach (var n in c.Upkeep.Needs)
        {
            if (n.Concern != id)
            {
                continue;
            }
            var recovered = Unknown<bool>();
            var targetsKnown = false;
            if (n.Targets.TryGetValue(out _))
            {
                recovered = Known(!n.Active);
                targetsKnown = true;
            }
            c.Assess(id, n.Priority, recovered);
            if (!Positive(recovered))
            {
                var g = c.Raise(id, n.Priority);
                // These needs are binary, so a confirmed active deficit reports Known(1.0) rather than a partial fraction.
                if (targetsKnown)
                {
                    g.Deficit = Known(1.0);
                }
                // MaintainEssentialRepairs, ClearHomeObstructions and ClearAncientShrine have composed
                // dispatch methods; the rest of the direct upkeep orders remain visible-only until their
                // own dispatch verticals land.
                g.MethodUnavailable = hold is not null && hold(c);
            }
        }
    };

    /// <summary>Fire safety has no composed method of its own: it stays visible-only.</summary>
    internal static bool HoldFireSafety(RoundsRun c) => true;

    /// <summary>
    /// Clearance ranks below repairs and above direct cleaning. Safety goals already suspend all development
    /// work through the shared emergency gate. While the shrine breach is issued, obstruction clearance
    /// waits so the construction hand is the breacher, not a wanderer past the trap line. While the initial
    /// shelter is owed (the housing latch still at HousingShelter, the Foothold shelter fact) clearance
    /// waits as well: its Deconstruct jobs use the Construction work the shelter needs (#2814). The planner
    /// holds the same way; this marks the review.
    /// </summary>
    internal static bool HoldClearance(RoundsRun c) =>
        c.Upkeep.History.Repairs || c.F.IsIssued(ClearAncientShrine) || c.R.Latches.Housing == Phase.HousingShelter;

    /// <summary>Repairs hold the shrine only when their method is served: a repair deficit nothing can serve must not hold the breach forever.</summary>
    internal static bool HoldShrine(RoundsRun c)
    {
        var repairsHold = c.Upkeep.History.Repairs;
        if (c.F.AvailableMethods.TryGetValue(out var methods) && repairsHold)
        {
            repairsHold = methods.Contains(MaintainEssentialRepairs);
        }
        return repairsHold;
    }

    /// <summary>
    /// Serves a home-area facility goal: a binary need, like the upkeep goals. A confirmed deficit ranks at
    /// Known(1.0); an unknown census stays DevelopmentUnknown. Method availability follows the composed
    /// capability list, since both verticals dispatch.
    /// </summary>
    internal static void FacilityInspect(RoundsRun c, ConcernId id, int priority, Func<RoundsRun, Fact<bool>> recoveredOf, Func<RoundsRun, bool> active)
    {
        var recovered = recoveredOf(c);
        if (!recovered.TryGetValue(out _) && !active(c) && !c.F.IsIssued(id))
        {
            priority = 4;
        }
        if (c.F.IsIssued(id))
        {
            recovered = Known(false);
        }
        c.Assess(id, priority, recovered);
        if (!Positive(recovered))
        {
            var g = c.Raise(id, priority);
            if (recovered.TryGetValue(out _))
            {
                g.Deficit = Known(1.0);
            }
        }
    }

    internal static Action<RoundsRun> FacilityInspection(ConcernId id, int priority, Func<RoundsRun, Fact<bool>> recovered, Func<RoundsRun, bool> active) =>
        c => FacilityInspect(c, id, priority, recovered, active);

    internal static void InspectHomeCoverage(RoundsRun c) => FacilityInspect(c, MaintainHomeCoverage, 3, r =>
    {
        if (r.Home.TryGetValue(out var diff))
        {
            return Known(diff.Empty());
        }
        return Unknown<bool>();
    }, r => r.HomeActive);

    internal static void InspectStoneShell(RoundsRun c) => FacilityInspect(c, MaintainStoneShell, 4, r =>
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
        var reservePriority = 3;
        if (medicine.Stock.TryGetValue(out _))
        {
            reserveRecovered = Known(!active);
        }
        else if (!active)
        {
            reservePriority = 4;
        }
        // MaintainMedicalReserves is the one medical upkeep goal: resting and hospital care for the sick
        // first (from StageStable), then the medicine stock. The care phase keeps its pre-merge shape:
        // priority 2, no ranked method and no labor, since work assignments own disease rest and monitoring
        // recovery must not reserve execution capacity while the pawn rests.
        var careRaised = c.P.ColonyStage >= ColonyStage.Stable;
        var priority = reservePriority;
        var recovered = reserveRecovered;
        if (careRaised)
        {
            recovered = AllFacts(f.MedicalCareRecovered, reserveRecovered);
        }
        if (careRaised && !Positive(f.MedicalCareRecovered))
        {
            c.R.Latches.Medical = Phase.MedicalCare;
            priority = 2;
            var g = c.Raise(MaintainMedicalReserves, 2);
            g.MethodUnavailable = true;
        }
        else if (!Positive(reserveRecovered))
        {
            c.R.Latches.Medical = Phase.MedicalReserves;
            c.Raise(MaintainMedicalReserves, reservePriority);
        }
        c.Assess(MaintainMedicalReserves, priority, recovered);
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
        c.Assess(MaintainSurgery, SurgeryPriority, recovered);
        if (!Positive(recovered))
        {
            c.Raise(MaintainSurgery, SurgeryPriority);
        }
    }

    /// <summary>Owed while babies have no breastfeeder and too little baby-edible food; unknown raises nothing.</summary>
    internal static void InspectBabyFeeding(RoundsRun c)
    {
        var recovered = Measured(c.F.BabyFeeding, b => !b.IsShort);
        c.Assess(MaintainBabyFeeding, BabyFeedingPriority, recovered);
        if (recovered.TryGetValue(out var v) && !v)
        {
            c.Raise(MaintainBabyFeeding, BabyFeedingPriority);
        }
    }

    /// <summary>
    /// MaintainFoodStorage is the one food storage goal: the foothold food stockpile (the storage gate)
    /// first, then the larder, the reserve and the stored-food upkeep.
    /// </summary>
    internal static void InspectFoodStorage(RoundsRun c)
    {
        var f = c.F;
        var reserveKnown = f.FoodReserve.TryGetValue(out var reserve);
        var reserveAccess = reserveKnown && (reserve!.Hold.Count > 0 || reserve.Release.Count > 0);
        var reserveRefill = reserveKnown && !reserve!.Emergency && reserve.DeficitNutrition > 0;
        // Unread claims are not yet known, never "missing".
        var stockpileOwed = Negative(FoodStorageStanding(f));
        var active = c.FoodStorage.Active || f.IsIssued(MaintainFoodStorage) || reserveAccess || reserveRefill;
        var recovered = Unknown<bool>();
        var priority = FoodStorageUpkeepPriority;
        var larder = SelectCorpseLarder(f.FoodStorageUpkeep);
        // The stockpile, releasing cooking inputs and preserving fresh corpses must not wait behind
        // development projects, just as refrigeration must not.
        if (larder.Kind != "" || reserveAccess || stockpileOwed)
        {
            priority = 2;
        }
        if (c.FoodStorage.StoredNutrition.TryGetValue(out _))
        {
            recovered = Known(!active);
        }
        else if (!active && !stockpileOwed)
        {
            priority = 4;
        }
        if (reserveAccess || reserveRefill)
        {
            recovered = Known(false);
        }
        recovered = AllFacts(Known(!stockpileOwed), recovered);
        c.Assess(MaintainFoodStorage, priority, recovered);
        if (!Positive(recovered))
        {
            c.Raise(MaintainFoodStorage, priority).MethodUnavailable = !stockpileOwed && larder.Kind == "" && !reserveAccess && !reserveRefill;
        }
    }

    /// <summary>
    /// Answers the same at-risk perishable nutrition as MaintainFoodStorage by cooling the room the food
    /// already sits in. It runs at foothold priority like EnsureTemperatureSafety rather than as a ranked
    /// development project: the review only latches on food inside SafeRotDays of spoiling, and a cooler
    /// queued behind the project limit arrives after the food is gone.
    /// </summary>
    internal static void InspectRefrigeration(RoundsRun c)
    {
        var f = c.F;
        var refrigeration = c.Refrigeration;
        var recovered = Unknown<bool>();
        if (refrigeration.WarmNutrition.TryGetValue(out _))
        {
            recovered = Known(!refrigeration.Active);
        }
        f.MealClosetOwed.TryGetValue(out var closetOwed);
        if (closetOwed)
        {
            recovered = Known(false);
        }
        c.Assess(MaintainRefrigeration, RefrigerationPriority, recovered);
        if (!Positive(recovered))
        {
            var g = c.Raise(MaintainRefrigeration, RefrigerationPriority);
            // A cooler cannot run under a solar flare either (the cooler planner reports solar_flare), but
            // the goal keeps a method: the warm stock is cooked ahead on a bench that still works.
            if (refrigeration.WarmNutrition.TryGetValue(out var nutrition) && c.P.FoodStorage.AtRiskNutritionThreshold > 0)
            {
                g.Deficit = Known(Math.Min(1, nutrition / c.P.FoodStorage.AtRiskNutritionThreshold));
            }
            if (refrigeration.Warm.Count > 0)
            {
                g.Deficit = Known(1.0);
            }
            g.Deficit.TryGetValue(out var d);
            if (closetOwed && d < 0.5)
            {
                g.Deficit = Known(0.5);
            }
        }
    }

    /// <summary>
    /// A ranked development project: a dark bench costs work speed and mood, not lives, so it competes for
    /// a project slot like the other upkeep needs. The deficit is the measured dark fraction.
    /// </summary>
    internal static void InspectLighting(RoundsRun c)
    {
        var recovered = Unknown<bool>();
        var priority = LightingPriority;
        if (c.Lighting.Known)
        {
            recovered = Known(!c.Lighting.Active);
        }
        else if (!c.Lighting.Active)
        {
            priority = 4;
        }
        c.Assess(MaintainLighting, priority, recovered);
        if (!Positive(recovered))
        {
            var g = c.Raise(MaintainLighting, priority);
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
        c.Assess(MaintainFlooring, FlooringPriority, recovered);
        if (!Positive(recovered))
        {
            var g = c.Raise(MaintainFlooring, FlooringPriority);
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
        var priority = RoutesPriority;
        if (c.Routes.Known)
        {
            recovered = Known(!c.Routes.Active);
        }
        else if (!c.Routes.Active)
        {
            priority = 4;
        }
        if (DoorChanges(c.F.Upkeep.Rooms, c.F.Upkeep.Routes, c.P).Count > 0)
        {
            recovered = Known(false);
        }
        c.Assess(MaintainRoutes, priority, recovered);
        if (!Positive(recovered))
        {
            var g = c.Raise(MaintainRoutes, priority);
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
        c.Assess(MaintainArt, ArtPriority, recovered);
        if (recovered.TryGetValue(out var v) && !v)
        {
            c.Raise(MaintainArt, ArtPriority).Deficit = Known(1.0);
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
        c.Assess(MaintainTrade, TradePriority, recovered);
        if (recovered.TryGetValue(out var v) && !v)
        {
            // Binary like the other upkeep needs: the silver amount is not a fraction.
            c.Raise(MaintainTrade, TradePriority).Deficit = Known(1.0);
        }
    }

    /// <summary>
    /// A Safe area edit is owed. A settings write, ranked with the upkeep projects; unknown raises nothing.
    /// While a sheltering trigger holds it is ShelterPriority, so a threat's emergency cannot veto the Safe
    /// area that PlanSheltering moves pawns into.
    /// </summary>
    internal static void InspectShelter(RoundsRun c)
    {
        var priority = 3;
        var (trigger, _) = ShelterTriggerOf(c.F);
        if (trigger != ShelterTrigger.None)
        {
            priority = ShelterPriority(trigger);
        }
        c.Owed(MaintainShelter, priority, c.F.SafeAreaOwed);
    }

    /// <summary>MaintainFirebreak: ring work is owed.</summary>
    internal static void InspectFirebreak(RoundsRun c) => c.Owed(MaintainFirebreak, 3, c.F.FirebreakOwed);

    /// <summary>MaintainMechs: a mech is owed inside the bandwidth.</summary>
    internal static void InspectMechs(RoundsRun c) => c.Owed(MaintainMechs, MechPriority, c.F.MechGestationOwed);

    /// <summary>MaintainPsylink: a held neuroformer waits for a willing colonist.</summary>
    internal static void InspectPsylink(RoundsRun c) => c.Owed(MaintainPsylink, 3, c.F.PsylinkOwed);

    /// <summary>MaintainIdeoRoles: a role place and a fitting believer.</summary>
    internal static void InspectIdeoRoles(RoundsRun c) => c.Owed(MaintainIdeoRoles, 3, c.F.RolesOwed);

    /// <summary>MaintainRituals: a ritual is due, calm and ready to begin.</summary>
    internal static void InspectRituals(RoundsRun c) => c.Owed(MaintainRituals, 3, c.F.RitualsOwed);

    /// <summary>ManageCreepJoiners: a creepjoiner holds a weapon before its downside has shown.</summary>
    internal static void InspectCreepJoiners(RoundsRun c) => c.Owed(ManageCreepJoiners, 3, c.F.CreepJoinerOwed);

    /// <summary>A colonist holds permit points for a permit worth taking. Unknown without the royalty read raises nothing.</summary>
    internal static void InspectPermits(RoundsRun c)
    {
        c.Assess(MaintainPermits, 3, PermitsSpent(c.F.Royalty));
        var owed = NextPermitOf(c.F.Royalty) is not null;
        if (owed)
        {
            c.Raise(MaintainPermits, 3).Deficit = Known(1.0);
        }
    }

    internal static void InspectHerd(RoundsRun c)
    {
        var f = c.F;
        var herd = f.HerdPolicy();
        var feedShort = HerdFeedShort(f.AnimalFeedRunway().Projection);
        var recovered = Unknown<bool>();
        if (AnimalHerdDeficit(f.AnimalUpkeep.Animals, f.AnimalUpkeep.WildAnimals, feedShort, herd).TryGetValue(out var deficit))
        {
            recovered = Known(!deficit);
        }
        if (FoodSlaughterChoice(f.FoodPlan, f.AnimalUpkeep.Animals, herd).Method == HusbandryMethod.Slaughter)
        {
            recovered = Known(false);
        }
        if (FoodTameChoice(f.FoodPlan, f.AnimalUpkeep.WildAnimals, feedShort, f.WorkProfiles).Method == HusbandryMethod.Tame)
        {
            recovered = Known(false);
        }
        var removal = ReconcileHerdRemoval(f.AnimalUpkeep.Animals, herd, f.FoodPlan);
        if (removal.Method is not null)
        {
            recovered = Known(false);
        }
        else if (removal.Reason == HusbandryPlanReason.Unknown)
        {
            recovered = Unknown<bool>();
        }
        if (HerdMasterChoice(f.AnimalUpkeep.Animals, herd, f.WorkProfiles).Method is not null)
        {
            recovered = Known(false);
        }
        if (SterilizeChoice(f.AnimalUpkeep.Animals, herd, f.AnimalWard).Method is not null)
        {
            recovered = Known(false);
        }
        c.Assess(MaintainHerd, 3, recovered);
        if (!Positive(recovered))
        {
            c.Raise(MaintainHerd, 3).MethodUnavailable = true;
        }
    }

    /// <summary>
    /// custodyDeficit is only known once a deployment reads the population census broadened for custody
    /// (RoundsPopulationCustodyPlanner); an unknown custody status is not held against recovery, matching
    /// every other optional sub-step fact -- only a known deficit, in either prisoner recruitment or
    /// custody, blocks recovery. joinerDeficit likewise needs the quest census (RoundsPopulationJoinerPlanner).
    /// </summary>
    internal static void InspectPopulation(RoundsRun c)
    {
        var f = c.F;
        var p = c.P;
        var recovered = Unknown<bool>();
        var prisonerDeficitKnown = PrisonerRecruitDeficit(f.Prisoners, f.PrisonerColony, f.FoodDays, p.Prisoners()).TryGetValue(out var prisonerDeficit);
        var custodyDeficitKnown = CustodyDeficit(f.Custody).TryGetValue(out var custodyDeficit);
        var joinerDeficitKnown = JoinerDeficit(f.QuestOffers, JoinerCapacity(f.JoinerCapacity()), f).TryGetValue(out var joinerDeficit);
        var questKnown = QuestDeficit(f).TryGetValue(out var questDeficit);
        var letterKnown = JoinerLetterDeficit(f.JoinerLetters, JoinerCapacity(f.JoinerCapacity())).TryGetValue(out var letterDeficit);
        var ceremonyStarts = CeremonyStartOf(f.Royalty) is not null;
        if (!ShrineArrestTarget(f).IsEmpty || LanceTarget(f) is not null || EntityCaptureOwed(f.Containment) || EntityDoorOwed(f.Containment) || EntityTendOwed(f.Containment)
            || EntityBioferriteOwed(f.Containment, f.Research) || MonolithAdvanceOwedNow(f, p.ColonyStage) || (prisonerDeficitKnown && prisonerDeficit)
            || (custodyDeficitKnown && custodyDeficit) || (joinerDeficitKnown && joinerDeficit) || (questKnown && questDeficit) || (letterKnown && letterDeficit)
            || ceremonyStarts || DecreeDeficit(f) || MonumentDeficit(f) || DepartureDeficit(f) || RefugeeDeficit(f) || GravInspectionDeficit(f))
        {
            recovered = Known(false);
        }
        else if (prisonerDeficitKnown)
        {
            recovered = Known(!prisonerDeficit);
        }
        else if (custodyDeficitKnown)
        {
            recovered = Known(!custodyDeficit);
        }
        else if (joinerDeficitKnown)
        {
            recovered = Known(!joinerDeficit);
        }
        c.Assess(MaintainPopulation, 3, recovered);
        if (!Positive(recovered))
        {
            c.Raise(MaintainPopulation, 3).MethodUnavailable = true;
        }
    }

    /// <summary>
    /// Serves an animal upkeep goal. It has a composed planner (RoundsAnimalContainmentPlanner); availability
    /// is gated through AvailableMethods. The deficit is census-driven: any uncontained or unfed target is a
    /// full deficit, so a known need ranks for a development slot.
    /// </summary>
    internal static void AnimalInspect(RoundsRun c, ConcernId id, Func<RoundsRun, Fact<bool>> recoveredOf, Func<RoundsRun, bool> active)
    {
        var recovered = recoveredOf(c);
        var priority = 3;
        if (!recovered.TryGetValue(out _) && !active(c) && !c.F.IsIssued(id))
        {
            priority = 4;
        }
        if (c.F.IsIssued(id))
        {
            recovered = Known(false);
        }
        c.Assess(id, priority, recovered);
        if (!Positive(recovered))
        {
            var g = c.Raise(id, priority);
            if (recovered.TryGetValue(out _))
            {
                g.Deficit = Known(1.0);
            }
        }
    }

    internal static Action<RoundsRun> AnimalInspection(ConcernId id, Func<RoundsRun, Fact<bool>> recovered, Func<RoundsRun, bool> active) =>
        c => AnimalInspect(c, id, recovered, active);

    internal static void InspectAnimalContainment(RoundsRun c) => AnimalInspect(c, MaintainAnimalContainment, r =>
    {
        var recovered = Unknown<bool>();
        if (r.Animals.Containment.TryGetValue(out var targets))
        {
            recovered = Known(targets.Count == 0);
        }
        if (r.F.HerdRoomsOwed.TryGetValue(out var owed) && owed)
        {
            recovered = Known(false);
        }
        return recovered;
    }, r => r.Animals.History.Containment);

    /// <summary>Census-driven: any standing blighted plant is a full deficit; availability is gated through AvailableMethods.</summary>
    internal static void InspectBlight(RoundsRun c)
    {
        var recovered = Unknown<bool>();
        if (BlightDeficit(c.F.Blight).TryGetValue(out var deficit))
        {
            recovered = Known(!deficit);
        }
        c.Assess(RemoveBlight, 3, recovered);
        if (!Positive(recovered))
        {
            c.Raise(RemoveBlight, 3);
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
        c.Assess(ManagePollution, 3, cleared);
        if (c.F.Pollution.TryGetValue(out _) && !Positive(cleared))
        {
            c.Raise(ManagePollution, 3);
        }
    }

    /// <summary>Unknown without the charger read, and then it raises no goal. Always assessed, like ManagePollution.</summary>
    internal static void InspectMechCharger(RoundsRun c)
    {
        c.Assess(EnsureMechCharger, 3, NotFact(c.F.MechChargerOwed));
        if (c.F.MechChargerOwed.TryGetValue(out var owed) && owed)
        {
            c.Raise(EnsureMechCharger, 3);
        }
    }

    /// <summary>Unknown without the gene-building read, and then it raises no goal. Always assessed, like EnsureMechCharger.</summary>
    internal static void InspectGeneBank(RoundsRun c)
    {
        c.Assess(MaintainGeneBank, 3, NotFact(c.F.GeneBankOwed));
        if (c.F.GeneBankOwed.TryGetValue(out var owed) && owed)
        {
            c.Raise(MaintainGeneBank, 3);
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
        c.Assess(MaintainStockpiles, StockpilePriorityRank, recovered);
        if (!Positive(recovered))
        {
            c.Raise(MaintainStockpiles, StockpilePriorityRank);
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
            c.R.Assessments.Add(new RoundsAssessment { Id = EnsureMood, Subject = state.Pawn.Id, Priority = state.Priority(), Finding = state.Need(), MethodUnavailable = true });
        }
    }

    /// <summary>Runs last: a disaster promotes every goal and assessment filed before it.</summary>
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
            var priority = disaster is null ? 3 : disaster.Promote(RecoverDisasterServices, 3);
            if (areaChanges.Count > 0)
            {
                var (trigger, _) = ShelterTriggerOf(f);
                priority = ShelterPriority(trigger);
            }
            r.Assessments.Add(new RoundsAssessment { Id = RecoverDisasterServices, Priority = priority, Finding = need });
            foreach (var concern in r.Concerns)
            {
                concern.Priority = disaster is null ? concern.Priority : disaster.Promote(concern.Id, concern.Priority);
            }
            foreach (var assessment in r.Assessments)
            {
                assessment.Priority = disaster is null ? assessment.Priority : disaster.Promote(assessment.Id, assessment.Priority);
            }
        }
    }
}
