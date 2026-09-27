package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A research bench blueprint short of 25 steel names the steel; a site the
// stock already covers (waiting on a hauler) adds nothing.
func TestConstructionResourceNeedsNamesBlockedSiteCost(t *testing.T) {
	deficit := domain.Known(map[Resource]int64{"Steel": 25, "WoodLog": 10})
	stock := domain.Known([]Amount{{Resource: "Steel", Count: 4}, {Resource: "WoodLog", Count: 40}})
	got := ConstructionResourceNeeds(nil, deficit, stock)
	if !reflect.DeepEqual(got, map[Resource]int64{"Steel": 25}) {
		t.Fatalf("needs = %v", got)
	}
	got = ConstructionResourceNeeds(map[Resource]int64{"Steel": 60}, deficit, stock)
	if got["Steel"] != 60 {
		t.Fatalf("a larger dependency need must stand: %v", got)
	}
	if got := ConstructionResourceNeeds(nil, domain.Unknown[map[Resource]int64](), stock); got != nil {
		t.Fatalf("unknown deficit = %v", got)
	}
}

// woodShortage: initial shelter (priority 2, served by an admitted shell)
// waits on wood, one worker can cut, cook or haul, and the feed and supply
// upkeep outrank MaintainResource on deficit.
func woodShortage() DevelopmentRequest {
	return DevelopmentRequest{
		Snapshot: domain.GenerationSnapshot{Colony: "colony", Map: 1, Load: "load", Plan: "plan"}, Tick: 100,
		Workers: domain.Known(1),
		Census:  census(worker("a", WorkPlantCutting, WorkCooking, WorkHauling)),
		Goals: []DevelopmentGoal{
			{ID: EnsureInitialShelter, Source: AutopilotGoal, Priority: 2, Served: true},
			{ID: MaintainAnimalFeed, Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(1.0), Labor: LaborProfile{WorkCooking}},
			{ID: SecureSupplies, Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(.9), Labor: LaborProfile{WorkHauling}},
			{ID: MaintainResource, Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(.3), Labor: LaborProfile{WorkPlantCutting}},
		},
	}
}

func shelterWood(available domain.Fact[int64], costs ...DependencyCost) DevelopmentDependency {
	return DevelopmentDependency{Dependent: EnsureInitialShelter, Goal: "routine-shelter", Epoch: 1, Method: "shell", Prerequisite: MaintainResource, Resource: "WoodLog", Costs: costs, Available: available, Observed: 90}
}

func rankDep(t *testing.T, r DevelopmentRequest) DevelopmentState {
	t.Helper()
	s, err := RankDevelopment(r)
	if err != nil {
		t.Fatal(err)
	}
	return s
}

func TestShelterWoodShortfallOrdersWoodFirst(t *testing.T) {
	// Without the edge the lower-deficit wood acquisition is deferred.
	base := rankDep(t, woodShortage())
	if !rowOf(base, MaintainAnimalFeed).Selected || rowOf(base, MaintainResource).Selected {
		t.Fatalf("baseline should defer wood behind feed: %+v", base.Rows)
	}
	r := woodShortage()
	r.Dependencies = []DevelopmentDependency{shelterWood(domain.Known[int64](40), DependencyCost{"wall-1", 60}, DependencyCost{"wall-2", 60})}
	s := rankDep(t, r)
	wood := rowOf(s, MaintainResource)
	if !wood.Selected || wood.Donation == nil || rowOf(s, MaintainAnimalFeed).Selected {
		t.Fatalf("wood should take the worker: %+v", s.Rows)
	}
	want := DevelopmentDonation{Priority: 2, Chain: []GoalID{EnsureInitialShelter, MaintainResource}, Resource: "WoodLog", Shortfall: 80}
	if !reflect.DeepEqual(*wood.Donation, want) {
		t.Fatalf("donation %+v, want %+v", *wood.Donation, want)
	}
	// Effective ordering is not a class: nothing reads emergency or
	// startup, and the stage/clock see the declared priority only.
	for _, row := range s.Rows {
		if row.Reason == DevelopmentEmergency || row.Reason == DevelopmentStartup {
			t.Fatalf("donation became a class: %+v", row)
		}
	}
	if r.Goals[3].Priority != 3 {
		t.Fatal("declared priority changed")
	}
}

func TestDonationOnlyWhileShortfallOpen(t *testing.T) {
	for name, dep := range map[string]DevelopmentDependency{
		"covered":       shelterWood(domain.Known[int64](200), DependencyCost{"wall-1", 60}, DependencyCost{"wall-2", 60}),
		"unknown stock": shelterWood(domain.Unknown[int64](), DependencyCost{"wall-1", 60}),
	} {
		r := woodShortage()
		r.Dependencies = []DevelopmentDependency{dep}
		s := rankDep(t, r)
		if w := rowOf(s, MaintainResource); w.Selected || w.Donation != nil {
			t.Fatalf("%s: wood kept a donation: %+v", name, w)
		}
	}
	// Resource upkeep serving no dependency keeps its normal order.
	s := rankDep(t, woodShortage())
	if rowOf(s, MaintainResource).Donation != nil {
		t.Fatal("donation without an edge")
	}
}

func TestSharedDemandCountsEachActionOnce(t *testing.T) {
	a := shelterWood(domain.Known[int64](50), DependencyCost{"wall-1", 60}, DependencyCost{"wall-2", 60})
	b := a
	b.Dependent, b.Goal, b.Method = EnsureExpansion, "routine-expansion", "room"
	b.Costs = []DependencyCost{{"wall-2", 60}, {"wall-3", 30}}
	goals := append(woodShortage().Goals, DevelopmentGoal{ID: EnsureExpansion, Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(1.0)})
	d, _ := ResolveDonations(goals, []DevelopmentDependency{a, b})
	if got := d[MaintainResource]; got.Shortfall != 60+60+30-50 || got.Priority != 2 {
		t.Fatalf("shared demand %+v", got)
	}
}

func TestDependencyCyclesDepthAndInactiveBlock(t *testing.T) {
	goals := []DevelopmentGoal{
		{ID: "A", Source: AutopilotGoal, Priority: 2},
		{ID: "B", Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(1.0)},
		{ID: "C", Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(1.0)},
	}
	d, blockers := ResolveDonations(goals, []DevelopmentDependency{{Dependent: "A", Prerequisite: "B"}, {Dependent: "B", Prerequisite: "A"}})
	if len(d) != 0 || len(blockers) == 0 || blockers[0].Reason != DependencyCycle {
		t.Fatalf("cycle donated: %+v %+v", d, blockers)
	}
	// Production needs a building that needs that production.
	d, blockers = ResolveDonations(goals, []DevelopmentDependency{{Dependent: "A", Prerequisite: "B"}, {Dependent: "B", Prerequisite: "C"}, {Dependent: "C", Prerequisite: "B"}})
	if _, ok := d["C"]; ok {
		t.Fatalf("cycle member donated: %+v %+v", d, blockers)
	}
	// Chain depth is bounded.
	var chain []DevelopmentDependency
	long := []DevelopmentGoal{{ID: "G0", Source: AutopilotGoal, Priority: 1}}
	for i := 1; i <= MaxDependencyChain+2; i++ {
		id := GoalID("G" + string(rune('0'+i)))
		long = append(long, DevelopmentGoal{ID: id, Source: AutopilotGoal, Priority: 3})
		chain = append(chain, DevelopmentDependency{Dependent: long[i-1].ID, Prerequisite: id})
	}
	d, blockers = ResolveDonations(long, chain)
	if len(d) != MaxDependencyChain-1 || !hasBlocker(blockers, DependencyDepth) {
		t.Fatalf("depth: %d donations, %+v", len(d), blockers)
	}
	// A prerequisite with no executable method donates nothing, explicitly.
	r := woodShortage()
	r.Goals[3].MethodUnavailable = true
	r.Dependencies = []DevelopmentDependency{shelterWood(domain.Known[int64](0), DependencyCost{"wall-1", 60})}
	s := rankDep(t, r)
	if rowOf(s, MaintainResource).Donation != nil || !hasBlocker(s.Blockers, DependencyInactive) || !rowOf(s, MaintainAnimalFeed).Selected {
		t.Fatalf("inactive prerequisite: %+v %+v", s.Rows, s.Blockers)
	}
}

func hasBlocker(blockers []DependencyBlocker, reason string) bool {
	for _, b := range blockers {
		if b.Reason == reason {
			return true
		}
	}
	return false
}

// A shell admitted short of wood above WoodMin activates MaintainResource for
// the shortfall alone, leaves the latch off, and drops the goal once the
// edge settles (#711).
func TestShelterShortfallActivatesMaintainResource(t *testing.T) {
	f := stableRoutine()
	f.Wood = domain.Known(int64(150))
	if r := needs(t, f, RoutineLatches{}); r.Latches.Wood || hasNeed(r, MaintainResource) {
		t.Fatal("150 wood is above WoodMin", r)
	}
	f.Dependencies = []DevelopmentDependency{{Dependent: EnsureInitialShelter, Goal: "g", Epoch: 1, Method: "m", Prerequisite: MaintainResource, Resource: "WoodLog", Costs: []DependencyCost{{Action: "a", Count: 120}, {Action: "b", Count: 80}}, Available: domain.Known(int64(150))}}
	r := needs(t, f, RoutineLatches{})
	if r.Latches.Wood || !hasNeed(r, MaintainResource) {
		t.Fatal("shortfall did not activate MaintainResource", r)
	}
	for _, a := range r.Assessments {
		if a.ID == MaintainResource && a.Need != domain.NeedDeficit {
			t.Fatal(a)
		}
	}
	if got := DependencyResourceNeeds(f.Dependencies)["WoodLog"]; got != 200 {
		t.Fatal(got)
	}
	f.Dependencies = nil
	r = needs(t, f, r.Latches)
	if hasNeed(r, MaintainResource) {
		t.Fatal("settled edge kept MaintainResource", r)
	}
	for _, a := range r.Assessments {
		if a.ID == MaintainResource && a.Need != domain.NeedRecovered {
			t.Fatal(a)
		}
	}
}

// A shortfall in any resource but wood routes to MaintainResource and
// raises that resource's floor to the open costs (#728).
func TestNonWoodShortfallRaisesResourceFloor(t *testing.T) {
	if g, ok := ResourcePrerequisite("Steel"); !ok || g != MaintainResource {
		t.Fatal(g, ok)
	}
	f := stableRoutine()
	f.Resources = domain.Known([]Amount{{"Steel", 10}})
	if hasNeed(needs(t, f, RoutineLatches{}), MaintainResource) {
		t.Fatal("no floor configured")
	}
	f.Dependencies = []DevelopmentDependency{{Dependent: EnsureInitialShelter, Goal: "g", Epoch: 1, Method: "m", Prerequisite: MaintainResource, Resource: "Steel", Costs: []DependencyCost{{Action: "a", Count: 25}, {Action: "b", Count: 25}}, Available: domain.Known(int64(10))}}
	if got := DependencyResourceNeeds(f.Dependencies); got["Steel"] != 50 || len(got) != 1 {
		t.Fatal(got)
	}
	if !hasNeed(needs(t, f, RoutineLatches{}), MaintainResource) {
		t.Fatal("steel shortfall did not activate MaintainResource")
	}
	f.Dependencies[0].Available = domain.Known(int64(50))
	if got := DependencyResourceNeeds(f.Dependencies); len(got) != 0 {
		t.Fatal("covered edge raised a floor", got)
	}
}
