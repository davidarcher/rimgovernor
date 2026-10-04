package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// shelterFacts is a quiet colony with the Safe area "safe", two colonists
// ("a", "b") and a pet, all restricted to area.
func shelterFacts(area string) RoutineFacts {
	f := stableRoutine()
	f.ShelterArea = domain.Known("safe")
	f.DisasterConditions = domain.Known([]DisasterCondition{})
	f.Hostiles = domain.Known(int64(0))
	f.OutdoorTemperature, f.SleepingMin, f.SleepingMax = domain.Known(15.0), domain.Known(16.0), domain.Known(24.0)
	f.RecoverySafety = domain.Known(RecoverySafety{Restrictions: []RecoveryRestriction{{Pawn: "a", Area: domain.Known(area)}, {Pawn: "b", Area: domain.Known(area)}}})
	f.RecoveryWorkers = domain.Known([]RecoveryWorker{recoveryWorker("a"), recoveryWorker("b")})
	f.AnimalUpkeep.Animals = domain.Known([]UpkeepAnimal{{ID: "pet", Definition: "Husky", RequiresPen: domain.Known(false), SupportsAreas: domain.Known(true), AllowedArea: domain.Known(area), Release: domain.Known(false), Slaughter: domain.Known(false)}})
	return f
}

func everyoneTo(area string) []AllowedAreaChange {
	return []AllowedAreaChange{{Pawn: "a", Area: area}, {Pawn: "b", Area: area}, {Pawn: "pet", Animal: true, Area: area}}
}

func TestShelteringFalloutMovesEveryoneUndrafted(t *testing.T) {
	f := shelterFacts("")
	f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "c", Definition: ConditionToxicFallout}})
	if got := PlanSheltering(f); !reflect.DeepEqual(got, everyoneTo("safe")) {
		t.Fatal(got)
	}
	workers, _ := f.RecoveryWorkers.Value()
	workers[1].Drafted = domain.Known(true)
	if got := PlanSheltering(f); len(got) != 2 || got[0].Pawn != "a" || got[1].Pawn != "pet" {
		t.Fatal("drafted pawn sheltered", got)
	}
	if !assessedDeficit(needs(t, f, RoutineLatches{}), RecoverDisasterServices) {
		t.Fatal("sheltering raised no incident")
	}
}

func TestShelteringTemperatureOnlyOutsideComfort(t *testing.T) {
	f := shelterFacts("")
	f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "c", Definition: ConditionColdSnap}})
	f.OutdoorTemperature = domain.Known(20.0)
	if got := PlanSheltering(f); len(got) != 0 {
		t.Fatal("comfortable cold snap sheltered", got)
	}
	f.OutdoorTemperature = domain.Known(-12.0)
	if got := PlanSheltering(f); !reflect.DeepEqual(got, everyoneTo("safe")) {
		t.Fatal(got)
	}
	f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "c", Definition: ConditionHeatWave}})
	f.OutdoorTemperature = domain.Known(45.0)
	if got := PlanSheltering(f); !reflect.DeepEqual(got, everyoneTo("safe")) {
		t.Fatal(got)
	}
	// Cold outside without a cold snap is no trigger.
	f.DisasterConditions = domain.Known([]DisasterCondition{})
	if got := PlanSheltering(f); len(got) != 0 {
		t.Fatal(got)
	}
}

func TestShelteringThreatSparesCombatants(t *testing.T) {
	f := shelterFacts("")
	f.Hostiles = domain.Known(int64(3))
	if got := PlanSheltering(f); len(got) != 1 || !got[0].Animal {
		t.Fatal("unknown draft set sheltered colonists", got)
	}
	f.ShelterCombatants = domain.Known([]PawnID{"a"})
	want := []AllowedAreaChange{{Pawn: "b", Area: "safe"}, {Pawn: "pet", Animal: true, Area: "safe"}}
	if got := PlanSheltering(f); !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
}

func TestShelteringRestoresOnlySafeWhenClear(t *testing.T) {
	if got := PlanSheltering(shelterFacts("safe")); !reflect.DeepEqual(got, everyoneTo("")) {
		t.Fatal(got)
	}
	if got := PlanSheltering(shelterFacts("other")); len(got) != 0 {
		t.Fatal("cleared another planner's area", got)
	}
	f := shelterFacts("safe")
	f.Hostiles = domain.Unknown[int64]()
	if got := PlanSheltering(f); len(got) != 0 {
		t.Fatal("unknown trigger restored", got)
	}
}

func TestShelteringNeedsSafeAreaAndSkipsPenAnimals(t *testing.T) {
	f := shelterFacts("")
	f.DisasterConditions = domain.Known([]DisasterCondition{{ID: "c", Definition: ConditionToxicFallout}})
	f.ShelterArea = domain.Known("")
	if got := PlanSheltering(f); len(got) != 0 {
		t.Fatal("sheltered without a Safe area", got)
	}
	f.ShelterArea = domain.Known("safe")
	animals, _ := f.AnimalUpkeep.Animals.Value()
	animals[0].RequiresPen = domain.Known(true)
	if got := PlanSheltering(f); len(got) != 2 || got[1].Animal {
		t.Fatal("moved pen animal", got)
	}
}

// killboxFacts is shelterFacts after the fight: "a" hauls, "b" does not,
// and the NoKillbox area "nokill" exists.
func killboxFacts(area string, window bool) RoutineFacts {
	f := shelterFacts(area)
	f.NoDangerArea = domain.Known("nokill")
	f.DangerWindow = domain.Known(window)
	f.DangerHaulers = domain.Known([]PawnID{"a"})
	return f
}

func TestKillboxHaulerRestrictedAfterFight(t *testing.T) {
	want := []AllowedAreaChange{{Pawn: "a", Area: "nokill"}}
	if got := PlanSheltering(killboxFacts("", true)); !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
	// A hauler leaving the Safe area after the fight goes to NoKillbox,
	// the others back to unrestricted.
	want = []AllowedAreaChange{{Pawn: "a", Area: "nokill"}, {Pawn: "b", Area: ""}, {Pawn: "pet", Animal: true, Area: ""}}
	if got := PlanSheltering(killboxFacts("safe", true)); !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
	// During a threat a combatant hauler is kept out too.
	f := killboxFacts("", true)
	f.Hostiles = domain.Known(int64(2))
	f.ShelterCombatants = domain.Known([]PawnID{"a"})
	want = []AllowedAreaChange{{Pawn: "a", Area: "nokill"}, {Pawn: "b", Area: "safe"}, {Pawn: "pet", Animal: true, Area: "safe"}}
	if got := PlanSheltering(f); !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
}

func TestKillboxReleasedAfterCooldown(t *testing.T) {
	if got := PlanSheltering(killboxFacts("nokill", true)); len(got) != 2 || got[0].Pawn != "b" || got[1].Pawn != "pet" {
		t.Fatal("non-hauler kept in NoKillbox", got)
	}
	want := []AllowedAreaChange{{Pawn: "a", Area: ""}, {Pawn: "b", Area: ""}, {Pawn: "pet", Animal: true, Area: ""}}
	if got := PlanSheltering(killboxFacts("nokill", false)); !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
	f := killboxFacts("nokill", true)
	f.DangerWindow = domain.Unknown[bool]()
	if got := PlanSheltering(f); len(got) != 0 {
		t.Fatal("unknown window released", got)
	}
	none := domain.Known(int64(0))
	if w, _ := DangerWindowOf(domain.Known(int64(1)), 0, false, 100).Value(); !w {
		t.Fatal("live hostile closed window")
	}
	if w, _ := DangerWindowOf(none, 100, true, 100+DangerCooldown-1).Value(); !w {
		t.Fatal("window closed inside cooldown")
	}
	if w, _ := DangerWindowOf(none, 100, true, 100+DangerCooldown).Value(); w {
		t.Fatal("window held past cooldown")
	}
	if _, k := DangerWindowOf(domain.Unknown[int64](), 0, false, 0).Value(); k {
		t.Fatal("unknown hostiles known window")
	}
}

func TestKillboxNonHaulersUnaffected(t *testing.T) {
	f := killboxFacts("other", true)
	if got := PlanSheltering(f); len(got) != 1 || got[0].Pawn != "a" {
		t.Fatal(got)
	}
	f.DangerHaulers = domain.Known([]PawnID{})
	if got := PlanSheltering(f); len(got) != 0 {
		t.Fatal(got)
	}
	workers := domain.Known([]WorkPawn{
		{ID: "a", Work: domain.Known([]WorkPriority{{Work: WorkHauling, Priority: 3}})},
		{ID: "b", Work: domain.Known([]WorkPriority{{Work: WorkHauling, Priority: 0}})},
		{ID: "c", Work: domain.Known([]WorkPriority{{Work: WorkHauling, Priority: 2, Disabled: true}})},
	})
	if got, _ := DangerHaulers(workers).Value(); !reflect.DeepEqual(got, []PawnID{"a"}) {
		t.Fatal(got)
	}
}

func TestNoDangerCells(t *testing.T) {
	home := []domain.Cell{{X: 2, Z: 0}, {X: 1, Z: 0}, {X: 3, Z: 0}}
	if got := NoDangerCells(home, []domain.Cell{{X: 2, Z: 0}}, nil); !reflect.DeepEqual(got, []domain.Cell{{X: 1, Z: 0}, {X: 3, Z: 0}}) {
		t.Fatal(got)
	}
	// A seed takes every home cell within the reach out, the killbox too.
	far := domain.Cell{X: 3 + ThreatReachCells + 1, Z: 0}
	home = append(home, far)
	if got := NoDangerCells(home, nil, []domain.Cell{{X: 1, Z: ThreatReachCells}}); !reflect.DeepEqual(got, []domain.Cell{far}) {
		t.Fatal(got)
	}
}

func TestDangerSeedsAreLiveHostilesAndEveryHive(t *testing.T) {
	threats := []EmergencyThreat{
		{ID: "raider", Kind: Hostile, Position: domain.Known(domain.Cell{X: 5, Z: 6})},
		{ID: "dead", Kind: Hostile, Dead: domain.Known(true), Position: domain.Known(domain.Cell{X: 9, Z: 9})},
		{ID: "downed", Kind: Hostile, Downed: domain.Known(true), Position: domain.Known(domain.Cell{X: 8, Z: 8})},
		{ID: "hive", Kind: HostileBuilding, Definition: "Hive", Passive: domain.Known(true), Cells: []domain.Cell{{X: 40, Z: 41}, {X: 41, Z: 41}}},
		{ID: "pet", Kind: NearbyPredator, Position: domain.Known(domain.Cell{X: 1, Z: 1})},
	}
	want := []domain.Cell{{X: 5, Z: 6}, {X: 40, Z: 41}, {X: 41, Z: 41}}
	got := DangerSeeds(threats)
	if len(got) != len(want) {
		t.Fatal(got)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatal(got)
		}
	}
}

// A threat is itself the ActiveCombat emergency, and EmergencySafeguard vetoes
// priority 2 and above: the Safe area and the moves into it must sit below
// that floor, or a manhunter pack shelters no one (#1560).
func TestShelteringUnderThreatEscapesEmergencyVeto(t *testing.T) {
	f := shelterFacts("")
	f.Hostiles = domain.Known(int64(3))
	f.ShelterCombatants = domain.Known([]PawnID{})
	f.SafeAreaOwed = domain.Known(true)
	r := needs(t, f, RoutineLatches{})
	rule := SafeguardContext{Enabled: true, Emergency: []ConcernID{ActiveCombat}}
	seen := map[ConcernID]bool{}
	for _, g := range r.Goals {
		if g.ID == MaintainShelter {
			seen[g.ID] = true
			if reason := VetoProposal(rule, SafeguardProposal{Need: g.ID, Priority: g.Priority}); reason != "" {
				t.Fatal("MaintainShelter vetoed under a threat:", reason)
			}
		}
	}
	for _, a := range r.All() {
		if a.ID == RecoverDisasterServices && a.Finding == domain.FindingUnmet {
			seen[a.ID] = true
			if reason := VetoProposal(rule, SafeguardProposal{Need: a.ID, Priority: a.Priority}); reason != "" {
				t.Fatal("sheltering vetoed under a threat:", reason)
			}
		}
	}
	if !seen[MaintainShelter] || !seen[RecoverDisasterServices] {
		t.Fatal("threat raised no sheltering work", seen)
	}
}
