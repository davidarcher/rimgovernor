package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func stableRoutine() RoutineFacts {
	gear := GearObservation{}
	for _, id := range []PawnID{"a", "b", "c"} {
		gear.Pawns = append(gear.Pawns, GearPawn{Pawn: id, Loadout: "loadout", Deficit: domain.Known(false), Candidates: domain.Known([]GearCandidate{})})
	}
	return RoutineFacts{
		CurrentConstruction: domain.Known(CurrentConstruction{Colony: true}),
		ConstructionClaims:  domain.Known([]ConstructionClaim{}), HomeCoverage: domain.Known(HomeCoverageObservation{}),
		SleepingRecovered:    domain.Known(true),
		AnimalUpkeep:         AnimalUpkeepObservation{Animals: domain.Known([]UpkeepAnimal{}), WildAnimals: domain.Known([]UpkeepAnimal{})},
		Prisoners:            domain.Known([]PrisonerFacts{}),
		Waste:                domain.Known([]WasteItem{}),
		Blight:               domain.Known([]BlightedPlant{}),
		LayoutTidy:           domain.Known(TidyReview{Known: true, Reason: "nothing off grid"}),
		Stockpiles:           domain.Known(StockpileReview{Known: true, Reason: "stockpiles fit their contents"}),
		MedicalReserve:       MedicalReserveObservation{Items: domain.Known([]MedicineStack{{ID: "medicine", Definition: "MedicineHerbal", Count: 9, Perishable: domain.Known(false)}}), Resources: domain.Known([]Amount{{"MedicineHerbal", 9}})},
		FoodStorageUpkeep:    FoodStorageObservation{Stocks: domain.Known([]FoodStorageStock{})},
		Upkeep:               emptyUpkeep(),
		Gear:                 domain.Known(gear),
		MedicalCareRecovered: domain.Known(true), ComfortRecovered: domain.Known(true),
		BasicComfort: domain.Known(providedComfort("a", "b", "c")),
		Colonists:    domain.Known(int64(3)), HousingTarget: domain.Known(int64(0)), BedCapacity: domain.Known(int64(3)), IndoorCapacity: domain.Known(int64(4)), GrowingCells: domain.Known(int64(30)), Armed: domain.Known(int64(2)),
		FoodDays: domain.Known(8.0), FieldCoverage: domain.Known(1.0), SleepingMin: domain.Known(20.0), SleepingMax: domain.Known(20.0), Wood: domain.Known(int64(400)),
		Hostiles: domain.Known(int64(0)), CriticalPatients: domain.Known(int64(0)), ColonyNaming: domain.Known(false), ChoiceDialog: domain.Known(false), CleanupPawns: domain.Known(false), ForbiddenSupplies: domain.Known(false), EventLootPending: domain.Known(false),
		FoodStorage: domain.Known(true), Cooking: domain.Known(true), WorkCoverage: domain.Known(true), PowerRequired: domain.Known(false), DisabledConsumers: domain.Known(false),
	}
}
func needs(t *testing.T, f RoutineFacts, l RoutineLatches) RoutineNeeds {
	t.Helper()
	r, e := DetectRoutine(f, l, DefaultRoutinePolicy())
	if e != nil {
		t.Fatal(e)
	}
	return r
}
func hasNeed(r RoutineNeeds, id GoalID) bool {
	for _, g := range r.Goals {
		if g.ID == id {
			return true
		}
	}
	return false
}

// assessedDeficit reports an unrecovered assessment of id: a Response
// (#1078) is assessed, never filed as a development goal.
func assessedDeficit(r RoutineNeeds, id GoalID) bool {
	for _, a := range r.Assessments {
		if a.ID == id && a.Need != domain.NeedRecovered {
			return true
		}
	}
	return false
}
func TestRoutineStableAndRenewedDeficits(t *testing.T) {
	f := stableRoutine()
	r := needs(t, f, RoutineLatches{})
	if len(r.Goals) != 0 {
		t.Fatal(r)
	}
	f.FoodDays = domain.Known(2.0)
	r = needs(t, f, r.Latches)
	if !hasNeed(r, EnsureFoodSupply) || !r.Latches.Food {
		t.Fatal(r)
	}
	f.FoodDays = domain.Known(5.0)
	r = needs(t, f, r.Latches)
	if !positive(footholdFood(f, DefaultRoutinePolicy())) || !hasNeed(r, EnsureFoodSupply) {
		t.Fatal("foothold gate must not erase recovery target", r)
	}
	f.FoodDays = domain.Known(7.0)
	r = needs(t, f, r.Latches)
	if !r.Latches.Food {
		t.Fatal("exact exit retains latch")
	}
	f.FoodDays = domain.Known(7.1)
	r = needs(t, f, r.Latches)
	if r.Latches.Food {
		t.Fatal(r)
	}
	f.FoodDays = domain.Known(2.0)
	r = needs(t, f, r.Latches)
	if !r.Latches.Food {
		t.Fatal(r)
	}
}
func TestRoutineUnknownNeverRecovers(t *testing.T) {
	r := needs(t, RoutineFacts{}, RoutineLatches{Food: true, Wood: true, Cold: true, Hot: true})
	if positive(footholdFood(RoutineFacts{}, DefaultRoutinePolicy())) || !r.Latches.Food || !r.Latches.Wood || !r.Latches.Cold || !r.Latches.Hot {
		t.Fatal(r)
	}
	if !hasNeed(r, ActiveCombat) || !hasNeed(r, CriticalMedicine) {
		t.Fatal("unknown emergency facts must hold", r)
	}
	for _, g := range r.Goals {
		if g.ID == MaintainResource {
			if _, known := g.Deficit.Value(); known {
				t.Fatal("unknown stock became zero")
			}
		}
	}
}
func TestRoutineTemperatureAndWoodThresholds(t *testing.T) {
	f := stableRoutine()
	f.SleepingMin = domain.Known(11.0)
	f.SleepingMax = domain.Known(33.0)
	f.Wood = domain.Known(int64(100))
	r := needs(t, f, RoutineLatches{})
	if !r.Latches.Cold || !r.Latches.Hot || !r.Latches.Wood {
		t.Fatal(r)
	}
	f.SleepingMin = domain.Known(16.0)
	f.SleepingMax = domain.Known(28.0)
	f.Wood = domain.Known(int64(350))
	r = needs(t, f, r.Latches)
	if !r.Latches.Cold || !r.Latches.Hot || !r.Latches.Wood {
		t.Fatal("exact recovery boundaries must remain active", r)
	}
	f.SleepingMin = domain.Known(16.1)
	f.SleepingMax = domain.Known(27.9)
	f.Wood = domain.Known(int64(351))
	r = needs(t, f, r.Latches)
	if r.Latches.Cold || r.Latches.Hot || r.Latches.Wood {
		t.Fatal(r)
	}
}
func TestRoutineForecastAndPopulationAreSeparateFromStock(t *testing.T) {
	f := stableRoutine()
	f.FoodDays = domain.Known(0.0)
	f.FieldCoverage = domain.Known(10.0)
	r := needs(t, f, RoutineLatches{})
	if positive(footholdFood(f, DefaultRoutinePolicy())) || !hasNeed(r, EnsureFoodSupply) {
		t.Fatal(r)
	}
	f = stableRoutine()
	f.HousingTarget = domain.Known(int64(4))
	f.IndoorCapacity = domain.Known(int64(3))
	f.PopulationFoodDays = domain.Known(2.0)
	r = needs(t, f, RoutineLatches{})
	if positive(footholdShelter(f)) || positive(footholdProduction(f)) || positive(footholdFood(f, DefaultRoutinePolicy())) {
		t.Fatal(r)
	}
}
func TestRoutineRestingMedicalStillRequiresKnownPatients(t *testing.T) {
	f := stableRoutine()
	f.AllPatientsResting = domain.Known(true)
	f.CriticalPatients = domain.Known(int64(1))
	r := needs(t, f, RoutineLatches{})
	if r.Goals[0].ID != CriticalMedicine || r.Goals[0].Priority != 2 {
		t.Fatal(r)
	}
	f.CriticalPatients = domain.Unknown[int64]()
	r = needs(t, f, RoutineLatches{})
	if r.Goals[0].Priority != 1 {
		t.Fatal(r)
	}
}

// A patient who only needs tending keeps CriticalMedicine active at priority
// 2 (tended, but not suspending the colony); a downed or bleeding patient, or
// an unknown urgent count, is the priority-1 emergency (#66).
func TestRoutineStablePatientsAreNotAnEmergency(t *testing.T) {
	f := stableRoutine()
	f.CriticalPatients = domain.Known(int64(1))
	f.UrgentPatients = domain.Known(int64(0))
	r := needs(t, f, RoutineLatches{})
	if r.Goals[0].ID != CriticalMedicine || r.Goals[0].Priority != 2 {
		t.Fatal(r)
	}
	for _, a := range r.Assessments {
		if a.ID == CriticalMedicine && (a.Priority != 2 || a.Need != domain.NeedDeficit) {
			t.Fatal(a)
		}
	}
	for _, urgent := range []domain.Fact[int64]{domain.Known(int64(1)), domain.Unknown[int64]()} {
		f.UrgentPatients = urgent
		if r := needs(t, f, RoutineLatches{}); r.Goals[0].ID != CriticalMedicine || r.Goals[0].Priority != 1 {
			t.Fatal(r)
		}
	}
	f.UrgentPatients = domain.Known(int64(-1))
	if _, err := DetectRoutine(f, RoutineLatches{}, DefaultRoutinePolicy()); err == nil {
		t.Fatal("negative urgent count accepted")
	}
}
func TestRoutineRejectsInvalidFactsAndPolicy(t *testing.T) {
	f := stableRoutine()
	f.FoodDays = domain.Known(math.NaN())
	if _, e := DetectRoutine(f, RoutineLatches{}, DefaultRoutinePolicy()); e == nil {
		t.Fatal("NaN accepted")
	}
	p := DefaultRoutinePolicy()
	p.HotExit = p.HotEnter
	if _, e := DetectRoutine(stableRoutine(), RoutineLatches{}, p); e == nil {
		t.Fatal("invalid thresholds accepted")
	}
}

func TestRoutineRepairAndCleanDeficitsStayMethodAvailable(t *testing.T) {
	f := stableRoutine()
	f.Upkeep.Structures = domain.Known([]UpkeepStructure{{ID: "wall", Home: true, HitPoints: 1, MaxHitPoints: 2}})
	f.Upkeep.Filth = domain.Known([]UpkeepFilth{{ID: "dirt", Home: true}})
	r := needs(t, f, RoutineLatches{})
	for _, id := range []GoalID{MaintainEssentialRepairs, MaintainCleanFacilities} {
		found := false
		for _, g := range r.Goals {
			if g.ID == id {
				found = true
				if g.MethodUnavailable {
					t.Fatal("expected method available for", id)
				}
			}
		}
		if !found {
			t.Fatal("expected goal present for", id)
		}
	}
}

// An owed meal closet (#936) opens MaintainRefrigeration with no warm food,
// at half deficit or more.
func TestRoutineMealClosetOwedOpensRefrigeration(t *testing.T) {
	f := stableRoutine()
	f.FoodStorageUpkeep = FoodStorageObservation{Stocks: domain.Known([]FoodStorageStock{})}
	open := func(r RoutineNeeds) (bool, float64) {
		for _, g := range r.Goals {
			if g.ID == MaintainRefrigeration {
				d, _ := g.Deficit.Value()
				return true, d
			}
		}
		return false, 0
	}
	if ok, _ := open(needs(t, f, RoutineLatches{})); ok {
		t.Fatal("refrigeration open with nothing owed")
	}
	f.MealClosetOwed = domain.Known(true)
	if ok, d := open(needs(t, f, RoutineLatches{})); !ok || d < 0.5 {
		t.Fatal("owed closet left refrigeration shut", ok, d)
	}
}

func TestRoutineSolarFlareSuspendsPowerAndRefrigerationMethods(t *testing.T) {
	f := stableRoutine()
	f.PowerRequired, f.PowerHeadroom = domain.Known(true), domain.Known(-100.0)
	f.FoodStorageUpkeep = FoodStorageObservation{Stocks: domain.Known([]FoodStorageStock{warmStock("meat", "b", 20, 20)})}
	method := func(r RoutineNeeds, id GoalID) (open, unavailable bool) {
		for _, g := range r.Goals {
			if g.ID == id {
				return true, g.MethodUnavailable
			}
		}
		return false, false
	}
	r := needs(t, f, RoutineLatches{})
	for _, id := range []GoalID{EnsureBasicPower, MaintainRefrigeration} {
		if open, unavailable := method(r, id); !open || unavailable {
			t.Fatal(id, open, unavailable)
		}
	}
	// A flare with a remaining-duration read suspends the power method;
	// refrigeration keeps one (the cook-ahead bill, #408). The goals stay
	// open (not cancelled) and the latch keeps its state.
	flare := int64(12000)
	f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "f", Definition: ConditionSolarFlare, TicksLeft: &flare}})
	f.RecoveryBuildings = domain.Known([]RecoveryBuilding{})
	r = needs(t, f, r.Latches)
	if open, unavailable := method(r, EnsureBasicPower); !open || !unavailable {
		t.Fatal(EnsureBasicPower, open, unavailable)
	}
	if open, unavailable := method(r, MaintainRefrigeration); !open || unavailable {
		t.Fatal(MaintainRefrigeration, open, unavailable)
	}
	if !r.Latches.Refrigeration {
		t.Fatal("the flare released the refrigeration latch")
	}
	// A flare without a remaining-duration read is not planned against.
	f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "f", Definition: ConditionSolarFlare}})
	r = needs(t, f, r.Latches)
	for _, id := range []GoalID{EnsureBasicPower, MaintainRefrigeration} {
		if open, unavailable := method(r, id); !open || unavailable {
			t.Fatal(id, open, unavailable)
		}
	}
}

// A sick colonist puts MaintainMedicalReserves in its care phase, but the
// medicine stock latch stays live and the phase still restocks: the bill and
// herb harvest never stop because someone is ill.
func TestMedicalCarePhaseKeepsRestocking(t *testing.T) {
	f := stableRoutine()
	f.MedicalCareRecovered = domain.Known(false)
	f.MedicalReserve = MedicalReserveObservation{Items: domain.Known([]MedicineStack{}), Resources: domain.Known([]Amount{})}
	p := DefaultRoutinePolicy()
	p.ColonyStage = StageStable
	r, err := DetectRoutine(f, RoutineLatches{}, p)
	if err != nil {
		t.Fatal(err)
	}
	if r.Latches.Medical != MedicalCare || !r.Latches.MedicalReserve || !r.Latches.Medical.Restocks() || !hasNeed(r, MaintainMedicalReserves) {
		t.Fatal(r.Latches.Medical, r.Latches.MedicalReserve, r.Goals)
	}
	f.MedicalCareRecovered = domain.Known(true)
	if r, err = DetectRoutine(f, r.Latches, p); err != nil || r.Latches.Medical != MedicalReserves || !r.Latches.Medical.Restocks() {
		t.Fatal(err, r.Latches.Medical)
	}
	if Phase("").Restocks() {
		t.Fatal("a recovered goal restocks nothing")
	}
}
