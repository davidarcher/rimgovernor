package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// woodShortShelter: a shelter spread over five open actions and its
// MaintainResource prerequisite (one open action) share one construction
// shortfall; the feed upkeep outranks both on deficit and age but has no
// projected shortfall, and the unmapped incineration goal is never scored.
func woodShortShelter() DevelopmentRequest {
	p := ForwardProjection{HorizonDays: ProjectionHorizonDays, Construction: domain.Known(ConstructionProjection{ShortfallDays: 2.5})}
	return DevelopmentRequest{
		Snapshot: domain.GenerationSnapshot{Colony: "colony", Map: 1, Load: "load", Plan: "plan"}, Tick: 100,
		Workers: domain.Known(2),
		Concerns: []DevelopmentConcern{
			{ID: MaintainShelter, Priority: 3, Deficit: domain.Known(.2), Labor: LaborProfile{WorkConstruction}},
			{ID: MaintainHerd, Priority: 3, Deficit: domain.Known(1.0), Labor: LaborProfile{WorkCooking}},
			{ID: MaintainIncineration, Priority: 3, Deficit: domain.Known(.9), Labor: LaborProfile{WorkHauling}},
			{ID: MaintainResource, Priority: 3, Deficit: domain.Known(.3), Labor: LaborProfile{WorkPlantCutting}},
		},
		Projection:  p,
		OpenActions: map[ConcernID]int{MaintainShelter: 1, MaintainResource: 4, MaintainHerd: 1},
	}
}

func rankOrder(t *testing.T, r DevelopmentRequest) (DevelopmentState, []ConcernID) {
	t.Helper()
	s, err := RankDevelopment(r)
	if err != nil {
		t.Fatal(err)
	}
	var order []ConcernID
	for _, row := range s.Rows {
		order = append(order, row.Concern)
	}
	return s, order
}

// A wood-short shelter never ranks ahead of the MaintainResource that
// acquires its wood, even when the shelter's shortfall per open action
// scores higher; both are admitted, and the ranker lifts them over the
// feed upkeep that has no projected shortfall.
func TestWoodShortShelterAdmitsPrerequisiteFirst(t *testing.T) {
	base := woodShortShelter()
	base.Projection = ForwardProjection{}
	if _, order := rankOrder(t, base); order[0] != MaintainHerd {
		t.Fatalf("without a projection the deficit order holds: %v", order)
	}
	s, order := rankOrder(t, woodShortShelter())
	if order[0] != MaintainResource || order[1] != MaintainShelter || order[2] != MaintainHerd || order[3] != MaintainIncineration {
		t.Fatalf("order %v", order)
	}
	for _, id := range []ConcernID{MaintainResource, MaintainShelter} {
		if err := AdmitDevelopment(s, id); err != nil {
			t.Fatal(id, err)
		}
	}
}

// An unmapped or unknown goal is never given a ranker score: it follows the
// scored goals in deficit-and-age order.
func TestUnscoredGoalsAreNotDefaulted(t *testing.T) {
	r := woodShortShelter()
	r.Projection.Construction = domain.Unknown[ConstructionProjection]()
	r.Projection.Food = domain.Unknown[FoodProjection]()
	if _, order := rankOrder(t, r); order[0] != MaintainHerd || order[1] != MaintainIncineration {
		t.Fatalf("unknown projection must leave the deficit order: %v", order)
	}
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

// A shortfall in any resource but wood raises that resource's floor to the
// open costs.
func TestNonWoodShortfallRaisesResourceFloor(t *testing.T) {
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
