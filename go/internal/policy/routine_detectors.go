package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// routineRun is one DetectRoutine pass: the facts, the policy, the reviews
// every detector may read (run once, before any detector, because the
// latches are built from them) and the needs the detectors append to. The
// registry runs the detectors in order; an assessment lands where its
// detector sits, so the order of goalDetectors is the order of
// RoutineNeeds.Assessments (the stored bindings are index-aligned).
type routineRun struct {
	f        RoutineFacts
	previous RoutineLatches
	p        RoutinePolicy
	// l are the latches the reviews yield; r.Latches starts as l and the
	// housing, comfort and medical detectors set their own phase on it.
	l RoutineLatches
	r RoutineNeeds

	home           domain.Fact[HomeAreaPlan]
	stone          domain.Fact[[]string]
	animals        AnimalUpkeepReview
	medicine       MedicalReserveReview
	foodStorage    FoodStorageReview
	refrigeration  RefrigerationReview
	upkeep         UpkeepReview
	lighting       LightingReview
	flooring       FlooringReview
	routes         RoutesReview
	sleepingActive bool
	homeActive     bool
	stoneActive    bool
}

// raise appends an autopilot goal and returns it for the detector to adjust.
func (c *routineRun) raise(id GoalID, priority int) *DevelopmentGoal {
	c.r.Goals = append(c.r.Goals, DevelopmentGoal{ID: id, Source: AutopilotGoal, Priority: priority, Deficit: RoutineDevelopmentDeficit(id, c.f, c.p), Labor: GoalLabor(id), Risk: RoutineDevelopmentRisk(id, c.f, c.l)})
	return &c.r.Goals[len(c.r.Goals)-1]
}

// assess appends id's assessment and returns it for the detector to adjust.
func (c *routineRun) assess(id GoalID, priority int, recovered domain.Fact[bool]) *RoutineAssessment {
	need := domain.NeedUnknown
	if value, known := recovered.Value(); known {
		need = domain.NeedDeficit
		if value {
			need = domain.NeedRecovered
		}
	}
	c.r.Assessments = append(c.r.Assessments, RoutineAssessment{ID: id, Priority: priority, Need: need})
	return &c.r.Assessments[len(c.r.Assessments)-1]
}

// owed assesses a goal that is recovered unless its fact says a step is
// owed; unknown raises nothing. A known deficit is a full one.
func (c *routineRun) owed(id GoalID, priority int, owed domain.Fact[bool]) {
	c.assess(id, priority, notFact(owed))
	if v, known := owed.Value(); known && v {
		c.raise(id, priority).Deficit = domain.Known(1.0)
	}
}

func notFact(f domain.Fact[bool]) domain.Fact[bool] {
	return measured(f, func(v bool) bool { return !v })
}

// latchRecovered: a retained latch with missing input preserves history, not
// fresh evidence.
func latchRecovered(active bool, observed domain.Fact[float64]) domain.Fact[bool] {
	return measured(observed, func(float64) bool { return !active })
}

func detectColonyNames(c *routineRun) error {
	if positive(c.f.ColonyNaming) {
		c.raise(ConfirmColonyNames, 0)
	}
	c.assess(ConfirmColonyNames, 0, notFact(c.f.ColonyNaming))
	return nil
}

func detectAnswerDialog(c *routineRun) error {
	if positive(c.f.ChoiceDialog) {
		c.raise(AnswerDialog, 0)
	}
	c.assess(AnswerDialog, 0, notFact(c.f.ChoiceDialog))
	return nil
}

func detectActiveCombat(c *routineRun) error {
	f := c.f
	if !positive(combatCleared(f)) {
		c.raise(ActiveCombat, 0)
	}
	a := c.assess(ActiveCombat, 0, combatCleared(f))
	if cleared := measured(f.Hostiles, func(n int64) bool { return n == 0 }); positive(cleared) {
		a.Hunt = HuntRequest(f.FoodPlan)
	}
	return nil
}

func detectCriticalMedicine(c *routineRun) error {
	medicalMet := measured(c.f.CriticalPatients, func(v int64) bool { return v == 0 })
	priority := criticalMedicinePriority(c.f)
	if !positive(medicalMet) {
		c.raise(CriticalMedicine, priority)
	}
	c.assess(CriticalMedicine, priority, medicalMet)
	return nil
}

func detectRestoreWorkers(c *routineRun) error {
	f := c.f
	if positive(measured(f.Hostiles, func(n int64) bool { return n == 0 })) && positive(f.CleanupPawns) {
		c.raise(RestoreWorkers, 1)
	}
	c.assess(RestoreWorkers, 1, notFact(f.CleanupPawns))
	return nil
}

func detectAllowStartingSupplies(c *routineRun) error {
	if positive(c.f.ForbiddenSupplies) {
		c.raise(AllowStartingSupplies, 2)
	}
	c.assess(AllowStartingSupplies, 2, notFact(c.f.ForbiddenSupplies))
	return nil
}

func detectManageSupplySafety(c *routineRun) error {
	priority := supplySafetyPriority(c.f)
	if positive(c.f.EventLootPending) {
		c.raise(ManageSupplySafety, priority)
	}
	c.assess(ManageSupplySafety, priority, notFact(c.f.EventLootPending))
	return nil
}

func detectWorkAssignments(c *routineRun) error {
	f := c.f
	workMet := allFacts(f.WorkCoverage, measured(f.CleanupPawns, func(v bool) bool { return !v }), measured(f.ColonyNaming, func(v bool) bool { return !v }))
	if positive(f.HostilityOwed) || positive(f.SelfTendOwed) || positive(f.NamesOwed) || positive(f.MedicineCarryOwed) || positive(f.MedicalCareOwed) {
		workMet = domain.Known(false)
	}
	if !positive(workMet) {
		c.raise(EnsureWorkAssignments, 2)
	}
	c.assess(EnsureWorkAssignments, 2, workMet)
	return nil
}

func detectFoodSupply(c *routineRun) error {
	f, l := c.f, c.l
	foodMet, productionMet := footholdFood(f, c.p), footholdProduction(f)
	fieldsCovered := measured(f.FieldCoverage, func(v float64) bool { return v >= 1-1e-9 })
	if HumanFoodPending(f.FoodPlan) || l.Food || !positive(foodMet) || !positive(productionMet) || !positive(fieldsCovered) {
		c.raise(EnsureFoodSupply, 2)
	}
	c.assess(EnsureFoodSupply, 2, allFacts(domain.Known(!HumanFoodPending(f.FoodPlan)), foodMet, productionMet, fieldsCovered, latchRecovered(l.Food, f.FoodDays)))
	return nil
}

func detectHousing(c *routineRun) error {
	housing := reviewHousing(c.f, c.previous, c.p, c.sleepingActive)
	c.r.Latches.Housing = housing.Phase
	if housing.Phase != "" {
		g := c.raise(MaintainHousing, housing.Priority)
		g.Deficit = housing.Deficit
		g.Blocked = housing.Blocked
		if housing.Phase == HousingShelter {
			// The starter shelter is a foothold goal: no ranked labor, as
			// before the housing goals merged.
			g.Labor, g.Risk = nil, domain.Known(0.0)
		}
	}
	c.assess(MaintainHousing, housing.Priority, housing.Recovered)
	return nil
}

func detectTemperatureSafety(c *routineRun) error {
	f, p, l := c.f, c.p, c.l
	temperatureMet := footholdTemperature(f, p)
	if positive(f.TemperatureOwed) {
		// A refuel switch or a room below its sleepers' band holds the
		// goal open (#1180, #1199).
		temperatureMet = domain.Known(false)
	}
	if l.Cold || l.Hot || !positive(temperatureMet) {
		c.raise(EnsureTemperatureSafety, 2)
	}
	c.assess(EnsureTemperatureSafety, 2, allFacts(temperatureMet, latchRecovered(l.Cold, fallback(f.SleepingMin, f.OutdoorTemperature)), latchRecovered(l.Hot, fallback(f.SleepingMax, f.OutdoorTemperature))))
	return nil
}

func detectCooking(c *routineRun) error {
	if !positive(cookingMet(c.f)) {
		c.raise(EnsureCooking, 2)
	}
	c.assess(EnsureCooking, 2, cookingMet(c.f))
	return nil
}

func detectButcherSpot(c *routineRun) error {
	if !positive(butcherSpotMet(c.f)) {
		c.raise(MaintainButcherSpot, 2)
	}
	c.assess(MaintainButcherSpot, 2, butcherSpotMet(c.f))
	return nil
}

func detectBasicPower(c *routineRun) error {
	powerMet := footholdPower(c.f)
	if !positive(powerMet) {
		// A solar flare with a known remaining duration switches every
		// powered building off for hours: the power deficit it measures is
		// real but answering it with a generator is not, so the goal stays
		// open with no method (it neither extends the startup hold nor is
		// cancelled) until the flare ends and the planner can tell an
		// outage from a shortfall.
		c.raise(EnsureBasicPower, 2).MethodUnavailable = PowerOutageHold(c.f.DisasterConditions)
	}
	c.assess(EnsureBasicPower, 2, powerMet)
	return nil
}

func detectBasicDefense(c *routineRun) error {
	f := c.f
	defenseMet := allFacts(footholdArmed(f), measured(f.Hostiles, func(v int64) bool { return v == 0 }))
	defense := basicDefenseRecovered(defenseMet, f.Unarmed)
	if !positive(defense) {
		g := c.raise(EnsureBasicDefense, 3)
		n, k := footholdCount(f).Value()
		stock, sk := f.Armed.Value()
		if k && sk && n > 0 {
			deficit := max(0, float64(min(2, n)-stock)/float64(min(2, n)))
			if unarmed, uk := f.Unarmed.Value(); uk && unarmed > 0 && deficit == 0 {
				deficit = float64(unarmed) / float64(unarmed+stock)
			}
			g.Deficit = domain.Known(deficit)
		}
	}
	c.assess(EnsureBasicDefense, 3, defense)
	return nil
}

func detectComfort(c *routineRun) error {
	f := c.f
	basicComfort, err := ReviewBasicComfort(f.BasicComfort)
	if err != nil {
		return err
	}
	// A table with a seat and a recreation source are provided with the
	// starter hut, at foothold priority: the two cheapest mood debuffs to
	// remove should not wait for the ranked comfort project (#232). While the
	// initial shelter is still owed there is no room to furnish, so the goal
	// holds no method and neither extends the startup hold nor competes.
	comfortRanked := c.p.ColonyStage >= StageDevelopment
	if !positive(basicComfort.Recovered()) {
		c.r.Latches.Comfort = ComfortBasic
		g := c.raise(EnsureComfort, basicComfort.Priority())
		g.Deficit = basicComfort.Deficit()
		g.MethodUnavailable = !positive(footholdShelter(f)) || !positive(footholdSleeping(f)) || basicComfort.Priority() == 3 && !basicComfort.VarietyKnown
	} else if comfortRanked && !positive(f.ComfortRecovered) {
		// The ranked phase: hosting rooms and proof of use, once the basic
		// facilities stand and the colony reached StageDevelopment.
		c.r.Latches.Comfort = ComfortRanked
		g := c.raise(EnsureComfort, 4)
		g.Comfort = true
		g.Deficit = f.ComfortDeficit
	}
	priority, recovered := 4, basicComfort.Recovered()
	if c.r.Latches.Comfort == ComfortBasic {
		priority = basicComfort.Priority()
	} else {
		// The ranked phase is assessed at every stage, as before the merge;
		// only its goal waits for StageDevelopment.
		recovered = allFacts(recovered, f.ComfortRecovered)
	}
	c.assess(EnsureComfort, priority, recovered)
	return nil
}

func detectPests(c *routineRun) error {
	// A recognised pest on the map (an alphabeaver pack eating the trees,
	// #247) is a foothold deficit answered by hunting, priority 2: it is
	// not an emergency (the census never holds the clock for a docile
	// animal) but it outranks every ranked project while it lasts. Only a
	// known census with a pest opens the goal: an unknown wild census
	// (beyond the native bound) has nothing to hunt and must not hold the
	// startup ladder for good.
	pests := PestCensus(c.f.AnimalUpkeep.WildAnimals)
	if n, known := pests.Value(); known && n > 0 {
		c.raise(ClearPests, 2).Deficit = domain.Known(1.0)
	}
	c.assess(ClearPests, 2, measured(pests, func(n int) bool { return n == 0 }))
	return nil
}

func detectEquipment(c *routineRun) error {
	f := c.f
	gear, err := ReviewGear(f.Gear)
	if err != nil {
		return err
	}
	if !positive(gear.Recovered) {
		c.raise(MaintainEquipment, 3).Deficit = gear.Deficit
	}
	equipped := gear.Recovered
	if short, _ := f.ShellsShort.Value(); short {
		equipped = domain.Known(false)
	}
	c.assess(MaintainEquipment, 3, equipped)
	return nil
}

// detectResearch: EnsureResearch and MaintainResource are operator-configured
// targets whose deficit is measured against native facts read in this review:
// no target configured is certain recovery, a configured target with missing
// facts is unknown, and RoutineResearchPlanner/RoutineResourcePlanner still
// re-read native state immediately before proposing a method.
func detectResearch(c *routineRun) error {
	f, p := c.f, c.p
	researchNeeds := DeepDrillingResearch(f.ResearchNeeds, f.ResourceRunways)
	researchTarget, researchDerived := ResearchGoal(ArmorResearchPolicy(p, c.l.Soldiers), researchNeeds, f.Research)
	researchRecovered, researchDeficit := ResearchTargetNeed(researchTarget, researchDerived, f.Research)
	// An empty Anomaly knowledge slot with a project to fund is a spending
	// need of its own (#1745); a disabled goal (no target, empty ladder)
	// funds none.
	if facts, known := f.Research.Value(); known && facts.KnowledgePick != "" && (len(researchNeeds) != 0 || len(ArmorResearchPolicy(p, c.l.Soldiers).ResearchLadder) != 0) {
		deficit, _ := researchDeficit.Value()
		researchRecovered, researchDeficit = domain.Known(false), domain.Known(max(deficit, 1.0))
	}
	if !positive(researchRecovered) {
		c.raise(EnsureResearch, 4).Deficit = researchDeficit
	}
	c.assess(EnsureResearch, 4, researchRecovered)
	return nil
}

func detectResource(c *routineRun) error {
	f, p, l := c.f, c.p, c.l
	// The wood latch is a WoodLog floor on MaintainResource (#728): below
	// WoodMin it asks for WoodTarget until the latch recovers.
	c.r.WoodFloor = WoodFloor(l.Wood, p)
	resourceTargets, err := p.EffectiveResourceTargets(f.Resources, ResourceGoalTargets(ResourceGoalTargets(MedicineResourceNeeds(f.Items, ResourceGoalTargets(f.ResourceNeeds, SocialDrugTargets(f.Research)), p.MedicineReserveTarget(f.Colonists, c.medicine.Active)), DependencyResourceNeeds(f.Dependencies)), WoodFloorNeeds(c.r.WoodFloor)))
	if err != nil {
		return err
	}
	c.r.ResourceTargets = resourceTargets
	resourceRecovered, resourceDeficit := ResourceTargetNeed(resourceTargets, WoodStock(f.Resources, f.Wood, resourceTargets))
	for _, runway := range f.ResourceRunways {
		if _, known := runway.Deficit.Value(); !known && runway.WindowDays >= 1 && positive(resourceRecovered) {
			resourceRecovered = domain.Unknown[bool]()
			resourceDeficit = domain.Unknown[float64]()
		}
		if deficit, known := runway.Deficit.Value(); known && deficit {
			resourceRecovered = domain.Known(false)
			if days, known := runway.DaysLeft.Value(); known {
				old, _ := resourceDeficit.Value()
				resourceDeficit = domain.Known(max(old, 1-days/ResourceRunwayDays))
			}
		}
	}
	priority := 4
	if l.Wood {
		// Low wood keeps the old wood goal's standing (#728).
		priority = 3
	}
	if !positive(resourceRecovered) {
		g := c.raise(MaintainResource, priority)
		g.Deficit = resourceDeficit
		// The ladder's research rung: while a project the workshop recorded
		// as gating the bench is unfinished, the goal has no method of its
		// own and holds no slot, so EnsureResearch can take one (#4 M4).
		// Wood and dependency floors are chopped or mined meanwhile.
		g.MethodUnavailable = ResearchGoalTarget("", f.ResearchNeeds, f.Research) != "" && c.r.WoodFloor == 0 && len(DependencyResourceNeeds(f.Dependencies)) == 0
	}
	c.assess(MaintainResource, priority, resourceRecovered)
	return nil
}

// detectTrade: TradeWithCaravan is a Response (#1078): an incident per
// caravan visit, never a development goal. It needs a negotiator's
// conversation, not a development slot, and recovers by itself when the
// caravan leaves or nothing is left worth trading. Restore parts a bench
// could make do not stand the goal (#1255).
func detectTrade(c *routineRun) error {
	f, p := c.f, c.p
	tradeNeed := AnimalSaleNeed(f.Items, ShedArtNeed(SurgeryTradeNeed(ReserveSurgeryStock(OrganSaleSurplus(f.Items, ReviewTradeNeed(f.Items.Currency, c.medicine, f.Resources, p.ResourceTargets, RoutineTradeFloors(p, nil), f.Wealth, p.Trade, RoutineTradeFood(f, p)), f.Resources, f.Colonists), f.MedicalPawns), SurgeryPurchaseParts(f.MedicalPawns, f.SurgeryContext(), SurgeryParts(SelectSurgery(f.MedicalPawns, nil, SurgeryContext{}).Wants), f.FabricableParts)), f.WealthBudget(), f.SaleArt), f.SaleAnimals(), f.Silver(), f.Colonists)
	tradeNeed = FavorGoldNeed(tradeNeed, f.Traders, f.Resources, p.ResourceTargets, RoutineTradeFloors(p, nil), p.Trade)
	c.assess(TradeWithCaravan, 3, TradeRecovered(f.Traders, PopulationTradeNeed(tradeNeed, JoinerCapacity(f.JoinerCapacity()))))
	return nil
}

// detectDefensiveLayout is config-only like EnsureResearch: opt-in activates
// the goal at priority 3 (after the storage gate) and the planner reports no
// work once every tier stands.
func detectDefensiveLayout(c *routineRun) error {
	recovered := domain.Known(!c.p.DefensiveLayout)
	if !positive(recovered) {
		c.raise(EnsureDefensiveLayout, 3)
	}
	c.assess(EnsureDefensiveLayout, 3, recovered)
	return nil
}

// upkeepDetector serves one of the direct upkeep goals: ReviewUpkeepWith
// files a need for each, and `hold` is the goal's extra method hold.
func upkeepDetector(id GoalID, hold func(c *routineRun) bool) func(c *routineRun) error {
	return func(c *routineRun) error {
		for _, n := range c.upkeep.Needs {
			if n.Goal != id {
				continue
			}
			recovered := domain.Unknown[bool]()
			targetsKnown := false
			if _, known := n.Targets.Value(); known {
				recovered = domain.Known(!n.Active)
				targetsKnown = true
			}
			c.assess(id, n.Priority, recovered)
			if !positive(recovered) {
				g := c.raise(id, n.Priority)
				// RoutineDevelopmentDeficit only covers a few measured
				// goals and otherwise reports Unknown, leaving a goal
				// permanently DevelopmentUnknown in RankDevelopment so it
				// could never win a capacity slot. These needs are binary
				// (recovered/deficit, not a partial fraction -- see
				// UpkeepNeed.Active/Targets), so a confirmed active deficit
				// reports the full Known(1.0).
				if targetsKnown {
					g.Deficit = domain.Known(1.0)
				}
				// MaintainEssentialRepairs, MaintainCleanFacilities,
				// ClearHomeObstructions and ClearAncientShrine have composed
				// dispatch methods; the rest of the direct upkeep orders
				// remain visible-only until their own dispatch verticals land.
				g.MethodUnavailable = hold != nil && hold(c)
			}
		}
		return nil
	}
}

// Fire safety has no composed method of its own: it stays visible-only.
func holdFireSafety(*routineRun) bool { return true }

// Clearance ranks below repairs and above direct cleaning. Safety goals
// already suspend all development work through the shared emergency gate.
// While the shrine breach (#458) is issued, obstruction clearance waits so
// the construction hand is the breacher, not a wanderer past the trap line.
func holdClearance(c *routineRun) bool {
	return c.upkeep.History.Repairs || c.f.UpkeepIssued[ClearAncientShrine]
}

// holdShrine: repairs hold the shrine only when their method is served: a
// repair deficit nothing can serve must not hold the breach forever.
func holdShrine(c *routineRun) bool {
	repairsHold := c.upkeep.History.Repairs
	if methods, known := c.f.AvailableMethods.Value(); known && repairsHold {
		repairsHold = slices.Contains(methods, MaintainEssentialRepairs)
	}
	return repairsHold
}

func holdCleaning(c *routineRun) bool { return c.upkeep.History.Clearance }

// facilityDetector serves a home-area facility goal: a binary need, like the
// upkeep goals. A confirmed deficit ranks at Known(1.0); an unknown census
// stays DevelopmentUnknown. Method availability follows the composed
// capability list, since both verticals dispatch.
func facilityDetector(id GoalID, priority int, recovered func(c *routineRun) domain.Fact[bool], active func(c *routineRun) bool) func(c *routineRun) error {
	return func(c *routineRun) error {
		recovered, priority := recovered(c), priority
		if _, known := recovered.Value(); !known && !active(c) && !c.f.UpkeepIssued[id] {
			priority = 4
		}
		if c.f.UpkeepIssued[id] {
			recovered = domain.Known(false)
		}
		c.assess(id, priority, recovered)
		if !positive(recovered) {
			g := c.raise(id, priority)
			if _, known := recovered.Value(); known {
				g.Deficit = domain.Known(1.0)
			}
		}
		return nil
	}
}

var detectHomeCoverage = facilityDetector(MaintainHomeCoverage, 3, func(c *routineRun) domain.Fact[bool] {
	if diff, known := c.home.Value(); known {
		return domain.Known(diff.Empty())
	}
	return domain.Unknown[bool]()
}, func(c *routineRun) bool { return c.homeActive })

var detectStoneShell = facilityDetector(MaintainStoneShell, 4, func(c *routineRun) domain.Fact[bool] {
	if rows, known := c.stone.Value(); known {
		return domain.Known(len(rows) == 0)
	}
	return domain.Unknown[bool]()
}, func(c *routineRun) bool { return c.stoneActive })

func detectMedicalReserves(c *routineRun) error {
	f, medicine := c.f, c.medicine
	// A plan issued for the care phase is not a medicine bill.
	active := medicine.Active || f.UpkeepIssued[MaintainMedicalReserves] && c.previous.Medical != MedicalCare
	reserveRecovered := domain.Unknown[bool]()
	reservePriority := 3
	if _, known := medicine.Stock.Value(); known {
		reserveRecovered = domain.Known(!active)
	} else if !active {
		reservePriority = 4
	}
	// MaintainMedicalReserves is the one medical upkeep goal: resting and
	// hospital care for the sick first (from StageStable), then the medicine
	// stock. The care phase keeps its pre-merge shape: priority 2, no ranked
	// method and no labor, since work assignments own disease rest and
	// monitoring recovery must not reserve execution capacity while the pawn
	// rests.
	careRaised := c.p.ColonyStage >= StageStable
	priority, recovered := reservePriority, reserveRecovered
	if careRaised {
		recovered = allFacts(f.MedicalCareRecovered, reserveRecovered)
	}
	if careRaised && !positive(f.MedicalCareRecovered) {
		c.r.Latches.Medical, priority = MedicalCare, 2
		g := c.raise(MaintainMedicalReserves, 2)
		g.MethodUnavailable = true
		g.Labor = nil
	} else if !positive(reserveRecovered) {
		c.r.Latches.Medical = MedicalReserves
		c.raise(MaintainMedicalReserves, reservePriority)
	}
	c.assess(MaintainMedicalReserves, priority, recovered)
	return nil
}

// detectSurgery (#1164): an operation the planner serves stands on a living
// colonist until the health change removes it.
func detectSurgery(c *routineRun) error {
	f, p := c.f, c.p
	// An actionable elective upgrade (#1167) keeps it open too.
	recovered := allFacts(SurgeryRecovered(f.MedicalPawns), measured(ElectiveSurgeryOwed(f.MedicalPawns, f.SurgeryContext(), f.FabricableParts), func(owed bool) bool { return !owed }))
	// A sale organ harvest (#1169) holds it open while the silver runway
	// is short and a prisoner's organ clears its cost; a prisoner's
	// recoverable artificial part (#1232) too, and a peg-leg step: doctor
	// training below the Medicine floor, prisoner control or a reinstall
	// before release (#1236). A prisoner whose care allows better than herbal
	// (#1239) too.
	if SaleHarvestWanted(f, reviewSilverShort(f, p, c.medicine)) || PartRecoveryWanted(f) || PegCycleWanted(f, p.Prisoners()) {
		recovered = domain.Known(false)
	}
	c.assess(MaintainSurgery, surgeryPriority, recovered)
	if !positive(recovered) {
		c.raise(MaintainSurgery, surgeryPriority)
	}
	return nil
}

// detectBabyFeeding (#1681): owed while babies have no breastfeeder and too
// little baby-edible food; unknown raises nothing.
func detectBabyFeeding(c *routineRun) error {
	recovered := measured(c.f.BabyFeeding, func(b BabyFeeding) bool { return !b.Short })
	c.assess(MaintainBabyFeeding, babyFeedingPriority, recovered)
	if v, known := recovered.Value(); known && !v {
		c.raise(MaintainBabyFeeding, babyFeedingPriority)
	}
	return nil
}

// detectFoodStorage: MaintainFoodStorage is the one food storage goal: the
// foothold food stockpile (the storage gate) first, then the larder, the
// reserve and the stored-food upkeep.
func detectFoodStorage(c *routineRun) error {
	f := c.f
	reserve, reserveKnown := f.FoodReserve.Value()
	reserveAccess := reserveKnown && (len(reserve.Hold) > 0 || len(reserve.Release) > 0)
	reserveRefill := reserveKnown && !reserve.Emergency && reserve.DeficitNutrition > 0
	stockpileOwed := !positive(f.FoodStorage)
	active := c.foodStorage.Active || f.UpkeepIssued[MaintainFoodStorage] || reserveAccess || reserveRefill
	recovered := domain.Unknown[bool]()
	priority := foodStorageUpkeepPriority
	larder, _ := SelectCorpseLarder(f.FoodStorageUpkeep)
	// The stockpile, releasing cooking inputs and preserving fresh corpses
	// must not wait behind development projects, just as refrigeration
	// must not.
	if larder.Kind != "" || reserveAccess || stockpileOwed {
		priority = 2
	}
	if _, known := c.foodStorage.StoredNutrition.Value(); known {
		recovered = domain.Known(!active)
	} else if !active && !stockpileOwed {
		priority = 4
	}
	if reserveAccess || reserveRefill {
		recovered = domain.Known(false)
	}
	recovered = allFacts(f.FoodStorage, recovered)
	c.assess(MaintainFoodStorage, priority, recovered)
	if !positive(recovered) {
		c.raise(MaintainFoodStorage, priority).MethodUnavailable = !stockpileOwed && larder.Kind == "" && !reserveAccess && !reserveRefill
	}
	return nil
}

// detectRefrigeration answers the same at-risk perishable nutrition as
// MaintainFoodStorage by cooling the room the food already sits in. It runs
// at foothold priority like EnsureTemperatureSafety rather than as a ranked
// development project: the review only latches on food inside SafeRotDays of
// spoiling, and a cooler queued behind the project limit arrives after the
// food is gone.
func detectRefrigeration(c *routineRun) error {
	f, refrigeration := c.f, c.refrigeration
	recovered := domain.Unknown[bool]()
	if _, known := refrigeration.WarmNutrition.Value(); known {
		recovered = domain.Known(!refrigeration.Active)
	}
	closetOwed, _ := f.MealClosetOwed.Value()
	if closetOwed {
		recovered = domain.Known(false)
	}
	c.assess(MaintainRefrigeration, refrigerationPriority, recovered)
	if !positive(recovered) {
		g := c.raise(MaintainRefrigeration, refrigerationPriority)
		// A cooler cannot run under a solar flare either (the cooler
		// planner reports solar_flare), but the goal keeps a method: the
		// warm stock is cooked ahead on a bench that still works (#408).
		if nutrition, known := refrigeration.WarmNutrition.Value(); known && c.p.FoodStorage.AtRiskNutritionThreshold > 0 {
			g.Deficit = domain.Known(min(1, nutrition/c.p.FoodStorage.AtRiskNutritionThreshold))
		}
		if len(refrigeration.Tombs) > 0 {
			g.Deficit = domain.Known(1.0)
		}
		if d, _ := g.Deficit.Value(); closetOwed && d < 0.5 {
			g.Deficit = domain.Known(0.5)
		}
	}
	return nil
}

// detectLighting: a ranked development project: a dark bench costs work
// speed and mood, not lives, so it competes for a project slot like the
// other upkeep needs. The deficit is the measured dark fraction.
func detectLighting(c *routineRun) error {
	recovered := domain.Unknown[bool]()
	priority := lightingPriority
	if c.lighting.Known {
		recovered = domain.Known(!c.lighting.Active)
	} else if !c.lighting.Active {
		priority = 4
	}
	c.assess(MaintainLighting, priority, recovered)
	if !positive(recovered) {
		g := c.raise(MaintainLighting, priority)
		if c.lighting.Known {
			g.Deficit = domain.Known(1.0)
		}
	}
	return nil
}

// detectFlooring is likewise a ranked project. A clean workspace on bare
// ground ranks with lighting; living rooms alone rank one step lower.
func detectFlooring(c *routineRun) error {
	flooring := c.flooring
	recovered := domain.Unknown[bool]()
	priority := flooringPriority
	if flooring.Known {
		recovered = domain.Known(!flooring.Active)
		if flooring.Active && flooring.Deficits[0].Tier != FloorTierClean {
			priority = min(4, priority+floorTierOrder[flooring.Deficits[0].Tier])
		}
	} else if !flooring.Active {
		priority = 4
	}
	c.assess(MaintainFlooring, priority, recovered)
	if !positive(recovered) {
		g := c.raise(MaintainFlooring, priority)
		if flooring.Known {
			g.Deficit = domain.Known(1.0)
		}
	}
	return nil
}

// detectRoutes is likewise a ranked project: the deficit is the measured
// fraction of facilities no colonist reaches.
func detectRoutes(c *routineRun) error {
	recovered := domain.Unknown[bool]()
	priority := routesPriority
	if c.routes.Known {
		recovered = domain.Known(!c.routes.Active)
	} else if !c.routes.Active {
		priority = 4
	}
	c.assess(MaintainRoutes, priority, recovered)
	if !positive(recovered) {
		g := c.raise(MaintainRoutes, priority)
		if c.routes.Known {
			g.Deficit = domain.Known(1.0)
		}
	}
	return nil
}

// detectArt is a ranked upkeep project too (#1190): owed only while a room
// needs a sculpture and a qualifying artist exists; unknown raises nothing.
func detectArt(c *routineRun) error {
	f := c.f
	recovered := domain.Unknown[bool]()
	// An inspired artist holds it open without a room (#1192). Sale demand
	// (#1193) holds it open the same way while an artist exists.
	if profiles, pk := f.WorkProfiles.Value(); pk && (len(InspiredArtists(profiles)) > 0 || len(Artists(profiles)) > 0 && artForSale(f, c.p, c.medicine)) {
		recovered = domain.Known(false)
	} else if owed, known := f.SculptureRoomsOwed.Value(); known && !owed {
		recovered = domain.Known(true)
	} else if profiles, pk := f.WorkProfiles.Value(); known && pk {
		recovered = domain.Known(len(Artists(profiles)) == 0)
	}
	c.assess(MaintainArt, artPriority, recovered)
	if v, known := recovered.Value(); known && !v {
		c.raise(MaintainArt, artPriority).Deficit = domain.Known(1.0)
	}
	return nil
}

// detectShelter (#1325): a Safe area edit is owed. A settings write, ranked
// with the upkeep projects; unknown raises nothing. While a sheltering
// trigger holds it is ShelterPriority, so a threat's emergency cannot veto
// the Safe area that PlanSheltering moves pawns into.
func detectShelter(c *routineRun) error {
	priority := 3
	if trigger, _ := ShelterTriggerOf(c.f); trigger != ShelterNone {
		priority = ShelterPriority(trigger)
	}
	c.owed(MaintainShelter, priority, c.f.SafeAreaOwed)
	return nil
}

// MaintainFirebreak (#1548): ring work is owed.
func detectFirebreak(c *routineRun) error {
	c.owed(MaintainFirebreak, 3, c.f.FirebreakOwed)
	return nil
}

// MaintainMechs (#1686): a mech is owed inside the bandwidth.
func detectMechs(c *routineRun) error {
	c.owed(MaintainMechs, mechPriority, c.f.MechGestationOwed)
	return nil
}

// MaintainPsylink (#1609): a held neuroformer waits for a willing colonist.
func detectPsylink(c *routineRun) error { c.owed(MaintainPsylink, 3, c.f.PsylinkOwed); return nil }

// MaintainIdeoRoles (#1661): a role place and a fitting believer.
func detectIdeoRoles(c *routineRun) error { c.owed(MaintainIdeoRoles, 3, c.f.RolesOwed); return nil }

// MaintainRituals (#1660): a ritual is due, calm and ready to begin.
func detectRituals(c *routineRun) error { c.owed(MaintainRituals, 3, c.f.RitualsOwed); return nil }

// ManageCreepJoiners (#1740): a creepjoiner holds a weapon before its
// downside has shown.
func detectCreepJoiners(c *routineRun) error {
	c.owed(ManageCreepJoiners, 3, c.f.CreepJoinerOwed)
	return nil
}

// detectPermits (#1606): a colonist holds permit points for a permit worth
// taking. Unknown without the royalty read raises nothing.
func detectPermits(c *routineRun) error {
	c.assess(MaintainPermits, 3, PermitsSpent(c.f.Royalty))
	if _, owed := NextPermitOf(c.f.Royalty); owed {
		c.raise(MaintainPermits, 3).Deficit = domain.Known(1.0)
	}
	return nil
}

func detectHerd(c *routineRun) error {
	f := c.f
	herd := f.HerdPolicy()
	recovered := domain.Unknown[bool]()
	if deficit, known := AnimalHerdDeficit(f.AnimalUpkeep.Animals, f.AnimalUpkeep.WildAnimals, HerdFeedShort(c.animals), herd).Value(); known {
		recovered = domain.Known(!deficit)
	}
	if choice := FoodSlaughterChoice(f.FoodPlan, f.AnimalUpkeep.Animals, herd); choice.Method == domain.HusbandrySlaughter {
		recovered = domain.Known(false)
	}
	if choice := ReconcileHerdRemoval(f.AnimalUpkeep.Animals, herd, f.FoodPlan); choice.Method != "" {
		recovered = domain.Known(false)
	} else if choice.Reason == HusbandryUnknown {
		recovered = domain.Unknown[bool]()
	}
	// A standing designation with a capable handler is ordered to completion.
	if PrioritizeSlaughterChoice(f.AnimalUpkeep.Animals, f.WorkProfiles).Method != "" {
		recovered = domain.Known(false)
	}
	if HerdMasterChoice(f.AnimalUpkeep.Animals, herd, f.WorkProfiles).Method != "" {
		recovered = domain.Known(false)
	}
	if SterilizeChoice(f.AnimalUpkeep.Animals, herd, f.VetRoom).Method != "" {
		recovered = domain.Known(false)
	}
	shelter, err := f.AnimalShelterChoice()
	if err != nil {
		return err
	}
	if shelter.Method != "" {
		recovered = domain.Known(false)
	}
	c.assess(MaintainHerd, 3, recovered)
	if !positive(recovered) {
		c.raise(MaintainHerd, 3).MethodUnavailable = true
	}
	return nil
}

// detectPopulation: custodyDeficit is only known once a deployment reads the
// population census broadened for custody (RoutinePopulationCustodyPlanner);
// an unknown custody status is not held against recovery, matching every
// other optional sub-step fact -- only a known deficit, in either prisoner
// recruitment or custody, blocks recovery. joinerDeficit likewise needs the
// quest census (RoutinePopulationJoinerPlanner).
func detectPopulation(c *routineRun) error {
	f, p := c.f, c.p
	recovered := domain.Unknown[bool]()
	prisonerDeficit, prisonerDeficitKnown := PrisonerRecruitDeficit(f.Prisoners, f.PrisonerColony, f.FoodDays, p.Prisoners()).Value()
	custodyDeficit, custodyDeficitKnown := CustodyDeficit(f.Custody).Value()
	joinerDeficit, joinerDeficitKnown := JoinerDeficit(f.QuestOffers, JoinerCapacity(f.JoinerCapacity())).Value()
	empireDeficit, empireKnown := EmpireDeficit(f.QuestOffers, f.TitleClaimQuests...).Value()
	odysseyDeficit, odysseyKnown := OdysseyDeficit(f.QuestOffers).Value()
	letterDeficit, letterKnown := JoinerLetterDeficit(f.JoinerLetters, JoinerCapacity(f.JoinerCapacity())).Value()
	_, ceremonyStarts := CeremonyStartOf(f.Royalty)
	switch {
	case ShrineArrestTarget(f) != "", LanceTarget(f) != "", entityCaptureOwed(f.Containment), entityDoorOwed(f.Containment), entityTendOwed(f.Containment), prisonerDeficitKnown && prisonerDeficit, custodyDeficitKnown && custodyDeficit, joinerDeficitKnown && joinerDeficit, empireKnown && empireDeficit, odysseyKnown && odysseyDeficit, letterKnown && letterDeficit, ceremonyStarts:
		recovered = domain.Known(false)
	case prisonerDeficitKnown:
		recovered = domain.Known(!prisonerDeficit)
	case custodyDeficitKnown:
		recovered = domain.Known(!custodyDeficit)
	case joinerDeficitKnown:
		recovered = domain.Known(!joinerDeficit)
	}
	c.assess(MaintainPopulation, 3, recovered)
	if !positive(recovered) {
		c.raise(MaintainPopulation, 3).MethodUnavailable = true
	}
	return nil
}

// animalNeedDetector serves an animal upkeep goal. Both have composed
// planners (RoutineAnimalContainmentPlanner, RoutineAnimalFeedPlanner);
// availability is gated through AvailableMethods like MaintainWaste. Like
// waste, the deficit is census-driven: any uncontained or unfed target is a
// full deficit, so a known need ranks for a development slot.
func animalNeedDetector(id GoalID, recovered func(c *routineRun) domain.Fact[bool], active func(c *routineRun) bool) func(c *routineRun) error {
	return func(c *routineRun) error {
		recovered, priority := recovered(c), 3
		if _, known := recovered.Value(); !known && !active(c) && !c.f.UpkeepIssued[id] {
			priority = 4
		}
		if c.f.UpkeepIssued[id] {
			recovered = domain.Known(false)
		}
		c.assess(id, priority, recovered)
		if !positive(recovered) {
			g := c.raise(id, priority)
			if _, known := recovered.Value(); known {
				g.Deficit = domain.Known(1.0)
			}
		}
		return nil
	}
}

var detectAnimalContainment = animalNeedDetector(MaintainAnimalContainment, func(c *routineRun) domain.Fact[bool] {
	recovered := domain.Unknown[bool]()
	if targets, known := c.animals.Containment.Value(); known {
		recovered = domain.Known(len(targets) == 0)
	}
	if owed, known := c.f.HerdRoomsOwed.Value(); known && owed {
		recovered = domain.Known(false)
	}
	return recovered
}, func(c *routineRun) bool { return c.animals.History.Containment })

var detectAnimalFeed = animalNeedDetector(MaintainAnimalFeed, func(c *routineRun) domain.Fact[bool] {
	recovered := domain.Unknown[bool]()
	if targets, known := c.animals.Feed.Value(); known {
		recovered = domain.Known(len(targets) == 0)
	}
	if need, known := HayNutritionNeed(c.f.PenGrazing, HarvestGapDays(c.f.Calendar, c.f.DisasterConditions)).Value(); known && need > 0 {
		recovered = domain.Known(false)
	}
	return recovered
}, func(*routineRun) bool { return false })

// detectWaste: MaintainWaste dispatches a GiveJobIntent HaulWaste
// (RoutineWastePlanner); availability is config-only, gated through
// AvailableMethods like MaintainResource/EnsureResearch.
func detectWaste(c *routineRun) error {
	recovered := domain.Unknown[bool]()
	if items, known := c.f.Waste.Value(); known {
		recovered = domain.Known(len(pendingWaste(items)) == 0)
	}
	if owed, known := c.f.CorpsesOwed.Value(); known && owed {
		recovered = domain.Known(false)
	}
	c.assess(MaintainWaste, 3, recovered)
	if !positive(recovered) {
		c.raise(MaintainWaste, 3)
	}
	return nil
}

// detectBlight is census-driven like waste: any standing blighted plant is a
// full deficit; availability is gated through AvailableMethods.
func detectBlight(c *routineRun) error {
	recovered := domain.Unknown[bool]()
	if deficit, known := BlightDeficit(c.f.Blight).Value(); known {
		recovered = domain.Known(!deficit)
	}
	c.assess(RemoveBlight, 3, recovered)
	if !positive(recovered) {
		c.raise(RemoveBlight, 3)
	}
	return nil
}

// detectPollution (#1683): without the Biotech read the need is unknown and
// raises no goal. It is always assessed so the stored bindings match the set
// the review loader derives from empty facts.
func detectPollution(c *routineRun) error {
	cleared := domain.Unknown[bool]()
	if deficit, known := PollutionDeficit(c.f.Pollution).Value(); known {
		cleared = domain.Known(!deficit)
	}
	c.assess(ManagePollution, 3, cleared)
	if _, biotech := c.f.Pollution.Value(); biotech && !positive(cleared) {
		c.raise(ManagePollution, 3)
	}
	return nil
}

// detectMechCharger (#1688): unknown without the charger read, and then it
// raises no goal. Always assessed, like ManagePollution.
func detectMechCharger(c *routineRun) error {
	c.assess(EnsureMechCharger, 3, notFact(c.f.MechChargerOwed))
	if owed, known := c.f.MechChargerOwed.Value(); known && owed {
		c.raise(EnsureMechCharger, 3)
	}
	return nil
}

// detectGeneBank (#1933): unknown without the gene-building read, and then
// it raises no goal. Always assessed, like EnsureMechCharger.
func detectGeneBank(c *routineRun) error {
	c.assess(MaintainGeneBank, 3, notFact(c.f.GeneBankOwed))
	if owed, known := c.f.GeneBankOwed.Value(); known && owed {
		c.raise(MaintainGeneBank, 3)
	}
	return nil
}

// detectTidyLayout (#611) is census-driven too: the layout review measures
// off-plan furniture against each room's interior plan and stands a
// proposal only while the colony is idle; a standing proposal is the
// deficit. It ranks last (tidyPriority, tidyDeficit), and its availability
// is gated through AvailableMethods.
func detectTidyLayout(c *routineRun) error {
	recovered := domain.Unknown[bool]()
	if tidy, known := c.f.LayoutTidy.Value(); known && tidy.Known {
		recovered = domain.Known(!tidy.Active)
	}
	c.assess(TidyLayout, tidyPriority, recovered)
	if !positive(recovered) {
		c.raise(TidyLayout, tidyPriority)
	}
	return nil
}

// detectStockpiles (#725): a standing stockpile edit is the deficit;
// availability is gated through AvailableMethods.
func detectStockpiles(c *routineRun) error {
	recovered := domain.Unknown[bool]()
	if review, known := c.f.Stockpiles.Value(); known && review.Known {
		recovered = domain.Known(!review.Active)
	}
	c.assess(MaintainStockpiles, stockpilePriority, recovered)
	if !positive(recovered) {
		c.raise(MaintainStockpiles, stockpilePriority)
	}
	return nil
}

// detectMood: a pawn's mood is an EnsureMood incident keyed by the pawn
// (#1078), never a development goal. Its relief is optional and a mental
// break ends only as ticks pass, so it never suspends other work.
func detectMood(c *routineRun) error {
	if err := c.f.Mood.Validate(); err != nil {
		return err
	}
	for _, state := range c.f.Mood.States {
		c.r.Assessments = append(c.r.Assessments, RoutineAssessment{ID: EnsureMood, Subject: domain.PawnID(state.Pawn.ID), Priority: state.Priority(), Need: state.Need(), MethodUnavailable: true})
	}
	return nil
}

// detectDisaster runs last: a disaster promotes every goal and assessment
// filed before it.
func detectDisaster(c *routineRun) error {
	f, r := c.f, &c.r
	var err error
	r.Disaster, err = ReviewDisaster(f.DisasterConditions, f.RecoveryBuildings, DisasterServiceFacts(f, c.p), f.Disaster, f.DisasterTick, f.ShortCircuitTick)
	if err != nil {
		return err
	}
	areaChanges := PlanSheltering(f)
	if _, safetyKnown := f.RecoverySafety.Value(); r.Disaster != nil || safetyKnown {
		need := RecoveryNeed(r.Disaster)
		if len(areaChanges) > 0 {
			need = domain.NeedDeficit
		}
		priority := r.Disaster.Promote(RecoverDisasterServices, 3)
		if len(areaChanges) > 0 {
			trigger, _ := ShelterTriggerOf(f)
			priority = ShelterPriority(trigger)
		}
		r.Assessments = append(r.Assessments, RoutineAssessment{ID: RecoverDisasterServices, Priority: priority, Need: need})
		for i := range r.Goals {
			r.Goals[i].Priority = r.Disaster.Promote(r.Goals[i].ID, r.Goals[i].Priority)
		}
		for i := range r.Assessments {
			r.Assessments[i].Priority = r.Disaster.Promote(r.Assessments[i].ID, r.Assessments[i].Priority)
		}
	}
	return nil
}
