package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// woodShortage: initial shelter (priority 2, served by an admitted shell)
// waits on wood, one worker can cut, cook or haul, and the feed and supply
// upkeep outrank MaintainResource on deficit.
func woodShortage() DevelopmentRequest {
	return DevelopmentRequest{
		Snapshot: domain.GenerationSnapshot{Colony: "colony", Map: 1, Load: "load", Plan: "plan"}, Tick: 100,
		Workers: domain.Known(1),
		Concerns: []DevelopmentConcern{
			{ID: MaintainHousing, Priority: 2, Served: true},
			{ID: MaintainAnimalFeed, Priority: 3, Deficit: domain.Known(1.0), Labor: LaborProfile{WorkCooking}},
			{ID: MaintainIncineration, Priority: 3, Deficit: domain.Known(.9), Labor: LaborProfile{WorkHauling}},
			{ID: MaintainResource, Priority: 3, Deficit: domain.Known(.3), Labor: LaborProfile{WorkPlantCutting}},
		},
	}
}

func shelterWood(available domain.Fact[int64], costs ...DependencyCost) DevelopmentDependency {
	return DevelopmentDependency{Dependent: MaintainHousing, Concern: "routine-shelter", Episode: 1, Method: "shell", Prerequisite: MaintainResource, Resource: "WoodLog", Costs: costs, Available: available, Observed: 90}
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
	// Without the edge wood ranks behind feed.
	base := rankDep(t, woodShortage())
	if rowOf(base, MaintainResource).Donation != nil || base.Rows[0].Concern != MaintainAnimalFeed {
		t.Fatalf("baseline should rank wood behind feed: %+v", base.Rows)
	}
	r := woodShortage()
	r.Dependencies = []DevelopmentDependency{shelterWood(domain.Known[int64](40), DependencyCost{"wall-1", 60}, DependencyCost{"wall-2", 60})}
	s := rankDep(t, r)
	wood := rowOf(s, MaintainResource)
	if !wood.Selected || wood.Donation == nil || s.Rows[0].Concern != MaintainResource {
		t.Fatalf("wood should take the worker: %+v", s.Rows)
	}
	want := DevelopmentDonation{Priority: 2, Chain: []ConcernID{MaintainHousing, MaintainResource}, Resource: "WoodLog", Shortfall: 80}
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
	if r.Concerns[3].Priority != 3 {
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
		if w := rowOf(s, MaintainResource); w.Donation != nil {
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
	b.Dependent, b.Concern, b.Method = EnsureComfort, "routine-comfort", "room"
	b.Costs = []DependencyCost{{"wall-2", 60}, {"wall-3", 30}}
	goals := append(woodShortage().Concerns, DevelopmentConcern{ID: EnsureComfort, Priority: 3, Deficit: domain.Known(1.0)})
	d, _ := ResolveDonations(goals, []DevelopmentDependency{a, b})
	if got := d[MaintainResource]; got.Shortfall != 60+60+30-50 || got.Priority != 2 {
		t.Fatalf("shared demand %+v", got)
	}
}

func TestDependencyCyclesDepthAndInactiveBlock(t *testing.T) {
	goals := []DevelopmentConcern{
		{ID: "A", Priority: 2},
		{ID: "B", Priority: 3, Deficit: domain.Known(1.0)},
		{ID: "C", Priority: 3, Deficit: domain.Known(1.0)},
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
	long := []DevelopmentConcern{{ID: "G0", Priority: 1}}
	for i := 1; i <= MaxDependencyChain+2; i++ {
		id := ConcernID("G" + string(rune('0'+i)))
		long = append(long, DevelopmentConcern{ID: id, Priority: 3})
		chain = append(chain, DevelopmentDependency{Dependent: long[i-1].ID, Prerequisite: id})
	}
	// Equal-priority structural links donate from mid-chain origins too;
	// G0's own donation stops at the depth bound.
	d, blockers = ResolveDonations(long, chain)
	fromOrigin := 0
	for _, don := range d {
		if don.Chain[0] == "G0" {
			fromOrigin++
		}
	}
	if fromOrigin != MaxDependencyChain-1 || !hasBlocker(blockers, DependencyDepth) {
		t.Fatalf("depth: %d donations from G0, %+v", fromOrigin, blockers)
	}
	// A prerequisite with no executable method donates nothing, explicitly.
	r := woodShortage()
	r.Concerns[3].MethodUnavailable = true
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
	f := stableRounds()
	f.Wood = domain.Known(int64(150))
	if r := needs(t, f, RoundsLatches{}); r.Latches.Wood || hasNeed(r, MaintainResource) {
		t.Fatal("150 wood is above WoodMin", r)
	}
	f.Admitted = []AdmittedCost{{"WoodLog", "a", 120}, {"WoodLog", "b", 80}}
	r := needs(t, f, RoundsLatches{})
	if r.Latches.Wood || !hasNeed(r, MaintainResource) {
		t.Fatal("shortfall did not activate MaintainResource", r)
	}
	for _, a := range r.Assessments {
		if a.ID == MaintainResource && a.Finding != domain.FindingUnmet {
			t.Fatal(a)
		}
	}
	if got := ConstructionDemandOf(f, RoundsPolicy{}, r.Latches)["WoodLog"]; got != 200 {
		t.Fatal(got)
	}
	f.Admitted = nil
	r = needs(t, f, r.Latches)
	if hasNeed(r, MaintainResource) {
		t.Fatal("settled edge kept MaintainResource", r)
	}
	for _, a := range r.Assessments {
		if a.ID == MaintainResource && a.Finding != domain.FindingMet {
			t.Fatal(a)
		}
	}
}

// A shortfall in any resource but wood routes to MaintainResource and
// raises that resource's floor to the open costs.
func TestNonWoodShortfallRaisesResourceFloor(t *testing.T) {
	if g, ok := ResourcePrerequisite("Steel"); !ok || g != MaintainResource {
		t.Fatal(g, ok)
	}
	f := stableRounds()
	f.Resources = domain.Known([]Amount{{"Steel", 10}})
	if hasNeed(needs(t, f, RoundsLatches{}), MaintainResource) {
		t.Fatal("no floor configured")
	}
	f.Admitted = []AdmittedCost{{"Steel", "a", 25}, {"Steel", "b", 25}}
	if got := ConstructionDemandOf(f, RoundsPolicy{}, RoundsLatches{}); got["Steel"] != 50 || len(got) != 1 {
		t.Fatal(got)
	}
	if !hasNeed(needs(t, f, RoundsLatches{}), MaintainResource) {
		t.Fatal("steel shortfall did not activate MaintainResource")
	}
	f.Resources = domain.Known([]Amount{{"Steel", 50}})
	if got := ConstructionDemandOf(f, RoundsPolicy{}, RoundsLatches{}); len(got) != 0 {
		t.Fatal("covered edge raised a floor", got)
	}
}
