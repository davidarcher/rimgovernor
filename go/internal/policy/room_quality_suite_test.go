package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// suiteFixture: a and b each own a 3x4 standard bedroom (r1, r2), the plan
// holds one suite not built yet, and the barracks has a spare bed.
func suiteFixture() (LayoutPlan, RoomObservation, SleepingObservation) {
	standard := func(x int32) LayoutRoom {
		return LayoutRoom{Role: ModuleBedroom, Interior: Rectangle{X: x, Z: 0, Width: 3, Height: 4}, Door: domain.Cell{X: x + 1, Z: 4}, DoorRot: domain.North}
	}
	suite := LayoutRoom{Role: ModuleSuite, Interior: Rectangle{X: 30, Z: 0, Width: 7, Height: 8}, Door: domain.Cell{X: 33, Z: 8}, DoorRot: domain.North}
	plan := LayoutPlan{
		Rooms: []LayoutRoom{{Role: ModuleBarracks, Interior: Rectangle{X: 0, Z: 0, Width: 7, Height: 7}, DoorRot: domain.North}},
		Wings: []Wing{
			{Purpose: WingBedrooms, Rooms: []LayoutRoom{standard(10), standard(14)}},
			{Purpose: WingSuites, Rooms: []LayoutRoom{suite}},
		},
	}
	room := func(id string, x int32, beds ...string) Room {
		return Room{ID: id, Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: beds, Cells: []domain.Cell{{X: x + 1, Z: 2}}}
	}
	rooms := RoomObservation{Rooms: []Room{
		{ID: "barracks", Role: domain.Known(RoomRoleBarracks), Enclosed: domain.Known(true), Beds: []string{"spare"}, Cells: []domain.Cell{{X: 3, Z: 3}}},
		room("r1", 10, "ra"), room("r2", 14, "rb"),
	}}
	bed := func(id string, owners ...PawnID) SleepingBed {
		return SleepingBed{ID: id, Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Roofed: domain.Known(true), Owners: owners, AccessibleTo: []PawnID{"a", "b", "c"}}
	}
	inRoom := func(b SleepingBed, room string) SleepingBed { b.Room = domain.Known(room); return b }
	q := func(v float64) domain.Fact[RoomQuality] {
		return domain.Known(RoomQuality{Wealth: 1500, Beauty: 3, Space: 30, Cleanliness: 0, Impressiveness: v})
	}
	sleeping := SleepingObservation{Colonists: 2,
		People: []SleepingPerson{{ID: "a", OwnedBed: domain.Known("ra")}, {ID: "b", OwnedBed: domain.Known("rb")}},
		Beds:   []SleepingBed{inRoom(bed("ra", "a"), "r1"), inRoom(bed("rb", "b"), "r2"), inRoom(bed("spare"), "barracks")},
		Rooms:  domain.Known([]UpkeepRoom{{ID: "r1", Role: "Bedroom", Quality: q(25)}, {ID: "r2", Role: "Bedroom", Quality: q(25)}}),
	}
	return plan, rooms, sleeping
}

var suiteTraits = map[PawnID]TraitEffects{"a": {Greedy: true}}

func suiteTargetsFor(sleeping SleepingObservation, traits map[PawnID]TraitEffects) map[string]RoomTarget {
	return RoomQualityTargets(sleeping, traits, BuildTierMasonry)
}

func TestSuiteClaimsQualifyOnlyOutgrownRooms(t *testing.T) {
	plan, rooms, sleeping := suiteFixture()
	got := SuiteClaims(plan, rooms, sleeping, suiteTargetsFor(sleeping, suiteTraits), suiteTraits, nil)
	if len(got) != 1 || got[0] != (SuiteClaim{Pawn: "a", Bed: "ra", Target: ImpressivenessSlightlyImpressive}) {
		t.Fatalf("greedy claims = %+v, want a alone at 50", got)
	}
	// An ascetic never gets one, even a greedy one.
	ascetic := map[PawnID]TraitEffects{"a": {Greedy: true, Ascetic: true}}
	if got := SuiteClaims(plan, rooms, sleeping, suiteTargetsFor(sleeping, ascetic), ascetic, nil); len(got) != 0 {
		t.Fatalf("ascetic claims = %+v", got)
	}
	// Space the weakest stat below a tier target: b qualifies too.
	cramped := domain.Known(RoomQuality{Wealth: 1500, Beauty: 3, Space: 5, Impressiveness: 15})
	sleeping.Rooms = domain.Known([]UpkeepRoom{{ID: "r1", Role: "Bedroom", Quality: cramped}, {ID: "r2", Role: "Bedroom", Quality: cramped}})
	got = SuiteClaims(plan, rooms, sleeping, suiteTargetsFor(sleeping, nil), nil, nil)
	if len(got) != 2 || got[0].Pawn != "a" || got[1].Pawn != "b" {
		t.Fatalf("cramped claims = %+v, want a and b", got)
	}
	// Space weakest but the target met: nobody.
	sleeping.Rooms = domain.Known([]UpkeepRoom{{ID: "r1", Role: "Bedroom", Quality: domain.Known(RoomQuality{Wealth: 1500, Beauty: 3, Space: 5, Impressiveness: 21})}})
	if got := SuiteClaims(plan, rooms, sleeping, suiteTargetsFor(sleeping, nil), nil, nil); len(got) != 0 {
		t.Fatalf("met target claims = %+v", got)
	}
}

func TestSuiteTargetsAddOnlyUnansweredClaims(t *testing.T) {
	plan, rooms, sleeping := suiteFixture()
	claims := []SuiteClaim{{Pawn: "a", Bed: "ra", Target: 50}, {Pawn: "b", Bed: "rb", Target: 60}}
	if got := SuiteTargets(plan, rooms, sleeping, nil, claims); !slices.Equal(got, []float64{0, 60}) {
		t.Fatalf("targets = %v, want the kept suite and b's", got)
	}
	plan.Wings = plan.Wings[:1]
	if got := SuiteTargets(plan, rooms, sleeping, nil, claims); !slices.Equal(got, []float64{50, 60}) {
		t.Fatalf("no suite wing targets = %v", got)
	}
	grown := Grow(PlanCore(coreTestZones(), 2, BuildTierCamp), 2, 1, BuildTierCamp, 50)
	if grown.SuiteRooms() != 1 {
		t.Fatalf("grown suites = %d, want 1", grown.SuiteRooms())
	}
}

func TestSuiteMoveShellsFurnishesAssignsThenReusesTheRoom(t *testing.T) {
	plan, rooms, sleeping := suiteFixture()
	targets := suiteTargetsFor(sleeping, suiteTraits)
	step := NextBedroomStep(plan, rooms, sleeping, targets, suiteTraits, nil)
	if step.Kind != BedroomShell || step.Room.Role != ModuleSuite {
		t.Fatalf("first step = %+v, want the suite's shell", step)
	}
	var cells []domain.Cell
	for x := int32(30); x < 37; x++ {
		for z := int32(0); z < 8; z++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	rooms.Rooms = append(rooms.Rooms, Room{ID: "s1", Role: domain.Known(RoomRole("None")), Enclosed: domain.Known(true), Cells: cells})
	if step = NextBedroomStep(plan, rooms, sleeping, targets, suiteTraits, nil); step.Kind != BedroomFurnish || len(step.Cells) != 56 {
		t.Fatalf("standing suite = %+v, want furnish", step)
	}
	rooms.Rooms[3].Role, rooms.Rooms[3].Beds = domain.Known(RoomRoleBedroom), []string{"sbed"}
	sleeping.Beds = append(sleeping.Beds, SleepingBed{ID: "sbed", Definition: "Bed", Room: domain.Known("s1"), Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), AccessibleTo: []PawnID{"a", "b", "c"}})
	step = NextBedroomStep(plan, rooms, sleeping, targets, suiteTraits, nil)
	if step.Kind != BedroomMove || step.Pawn != "a" || step.Bed != "sbed" || step.PreviousBed != "ra" {
		t.Fatalf("furnished suite = %+v, want a moved from ra", step)
	}
	// Moved: a's suite answers nobody else, and r1 goes to joiner c.
	sleeping.Beds[0].Owners, sleeping.Beds[3].Owners = nil, []PawnID{"a"}
	sleeping.People[0].OwnedBed = domain.Known("sbed")
	sleeping.Colonists = 3
	sleeping.People = append(sleeping.People, SleepingPerson{ID: "c", OwnedBed: domain.Known("spare")})
	sleeping.Beds[2].Owners = []PawnID{"c"}
	targets = suiteTargetsFor(sleeping, suiteTraits)
	if got := SuiteClaims(plan, rooms, sleeping, targets, suiteTraits, nil); len(got) != 0 {
		t.Fatalf("claims after the move = %+v", got)
	}
	step = NextBedroomStep(plan, rooms, sleeping, targets, suiteTraits, nil)
	if step.Kind != BedroomMove || step.Pawn != "c" || step.Bed != "ra" {
		t.Fatalf("vacated room = %+v, want c into ra", step)
	}
}

func TestSuiteBedIsNotAnUnhousedPawns(t *testing.T) {
	plan, rooms, sleeping := suiteFixture()
	rooms.Rooms = append(rooms.Rooms, Room{ID: "s1", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: []string{"sbed"}, Cells: []domain.Cell{{X: 33, Z: 4}}})
	sleeping.Beds = append(sleeping.Beds, SleepingBed{ID: "sbed", Definition: "Bed", Room: domain.Known("s1"), Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), AccessibleTo: []PawnID{"a", "b", "c"}})
	sleeping.Colonists = 3
	sleeping.People = append(sleeping.People, SleepingPerson{ID: "c", OwnedBed: domain.Known("spare")})
	sleeping.Beds[2].Owners = []PawnID{"c"}
	if step := NextBedroomStep(plan, rooms, sleeping, nil, nil, nil); step.Kind == BedroomMove && step.Bed == "sbed" {
		t.Fatalf("unhoused c took the suite bed: %+v", step)
	}
}

func TestBedroomSwapLeavesSuitesAlone(t *testing.T) {
	obs := SleepingObservation{
		Rooms: domain.Known([]UpkeepRoom{{ID: "r1", Quality: domain.Known(RoomQuality{Impressiveness: 20})}, {ID: "s1", Quality: domain.Known(RoomQuality{Impressiveness: 60})}}),
		Beds: []SleepingBed{
			{ID: "ra", Room: domain.Known("r1"), Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Owners: []PawnID{"a"}, AccessibleTo: []PawnID{"a", "b"}},
			{ID: "sb", Room: domain.Known("s1"), Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Owners: []PawnID{"b"}, AccessibleTo: []PawnID{"a", "b"}},
		},
	}
	jealous := map[PawnID]TraitEffects{"a": {Jealous: true}}
	if _, ok := NextBedroomSwap(obs, jealous, nil); !ok {
		t.Fatal("control: jealous a should take the better room")
	}
	if s, ok := NextBedroomSwap(obs, jealous, map[string]bool{"s1": true}); ok {
		t.Fatalf("suite swapped: %+v", s)
	}
}
