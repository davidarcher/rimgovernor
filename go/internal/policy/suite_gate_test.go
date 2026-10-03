package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// suiteGate prices a new suite: a Steel bed (delta 200 over the owners'
// Wood bed) plus end table, dresser and lamp (350): 550 in all.
func suiteGate(stage ColonyStage, remaining map[PawnID]float64) RoomGate {
	g := pieceGate(stage, remaining)
	g.BedPrice = func(def, stuff Resource) (float64, bool) {
		switch stuff {
		case "Wood":
			return 100, true
		case "Steel":
			return 300, true
		}
		return 0, false
	}
	g.SuiteBed, g.SuiteBedStuff = "Bed", "Steel"
	g.SuitePieces = []string{"EndTable", "Dresser", "StandingLamp"}
	return g
}

// ownedBeds gives the fixture's beds a priced stuff and quality.
func ownedBeds(sleeping SleepingObservation) SleepingObservation {
	beds := append([]SleepingBed(nil), sleeping.Beds...)
	for i := range beds {
		beds[i].Stuff, beds[i].Quality = domain.Known("Wood"), domain.Known("Normal")
	}
	sleeping.Beds = beds
	return sleeping
}

func TestSuiteClaimsStopAtTheOwnersShare(t *testing.T) {
	plan, rooms, sleeping := suiteFixture()
	sleeping = ownedBeds(sleeping)
	targets := suiteTargetsFor(sleeping, suiteTraits)
	claim := func(g RoomGate) []SuiteClaim {
		return SuiteClaims(plan, rooms, sleeping, targets, suiteTraits, nil, g)
	}
	if got := claim(suiteGate(StageReserves, map[PawnID]float64{"a": 1000})); len(got) != 1 || got[0].Pawn != "a" {
		t.Fatalf("rich claims = %+v", got)
	}
	// A poor owner makes no claim, so no suite is shelled and the goal can
	// recover.
	poor := suiteGate(StageReserves, map[PawnID]float64{"a": 500})
	if got := claim(poor); len(got) != 0 {
		t.Fatalf("poor claims = %+v", got)
	}
	if step := NextBedroomStep(plan, rooms, sleeping, targets, suiteTraits, nil, poor); step.Room.Role == ModuleSuite {
		t.Fatalf("poor step = %+v", step)
	}
	if owed, _ := BedroomsOwed(domain.Known(plan), domain.Known(rooms), domain.Known(sleeping), targets, suiteTraits, nil, poor).Value(); owed {
		t.Fatal("poor owner holds MaintainHousing open")
	}
	if step := NextBedroomStep(plan, rooms, sleeping, targets, suiteTraits, nil, suiteGate(StageReserves, map[PawnID]float64{"a": 1000})); step.Kind != BedroomShell || step.Room.Role != ModuleSuite {
		t.Fatalf("rich step = %+v", step)
	}
	// Charged steps begin at Reserves; an unknown share is necessities only.
	if got := claim(suiteGate(StageFoothold, map[PawnID]float64{"a": 1000})); len(got) != 0 {
		t.Fatalf("foothold claims = %+v", got)
	}
	if got := claim(suiteGate(StageReserves, nil)); len(got) != 0 {
		t.Fatalf("unknown share claims = %+v", got)
	}
	// An unpriced suite is refused while gated, and the zero gate is ungated.
	unpriced := suiteGate(StageReserves, map[PawnID]float64{"a": 1000})
	unpriced.SuiteBed = ""
	if got := claim(unpriced); len(got) != 0 {
		t.Fatalf("unpriced claims = %+v", got)
	}
	if got := claim(RoomGate{}); len(got) != 1 {
		t.Fatalf("ungated claims = %+v", got)
	}
}

func TestSuiteClaimsKeepPriorityAmongAffordable(t *testing.T) {
	plan, rooms, sleeping := suiteFixture()
	sleeping = ownedBeds(sleeping)
	cramped := domain.Known(RoomQuality{Wealth: 1500, Beauty: 3, Space: 5, Impressiveness: 15})
	sleeping.Rooms = domain.Known([]UpkeepRoom{{ID: "r1", Role: "Bedroom", Quality: cramped}, {ID: "r2", Role: "Bedroom", Quality: cramped}})
	targets := suiteTargetsFor(sleeping, nil)
	pressure := map[PawnID]float64{"a": -1, "b": -9}
	claim := func(g RoomGate) []SuiteClaim { return SuiteClaims(plan, rooms, sleeping, targets, nil, pressure, g) }
	if got := claim(suiteGate(StageReserves, map[PawnID]float64{"a": 1000, "b": 1000})); len(got) != 2 || got[0].Pawn != "b" || got[1].Pawn != "a" {
		t.Fatalf("both affordable = %+v", got)
	}
	// The more pressed pawn is too poor: the affordable one is claimed.
	if got := claim(suiteGate(StageReserves, map[PawnID]float64{"a": 1000, "b": 10})); len(got) != 1 || got[0].Pawn != "a" {
		t.Fatalf("b poor = %+v", got)
	}
}

// A vacant suite with its bed standing has only the move left, which is free.
func TestSuiteClaimsChargeOnlyWhatIsLeftToBuild(t *testing.T) {
	plan, rooms, sleeping := suiteFixture()
	sleeping = ownedBeds(sleeping)
	targets := suiteTargetsFor(sleeping, suiteTraits)
	poor := suiteGate(StageReserves, map[PawnID]float64{"a": 5})
	if got := SuiteClaims(plan, rooms, sleeping, targets, suiteTraits, nil, poor); len(got) != 0 {
		t.Fatalf("unbuilt suite, poor = %+v", got)
	}
	rooms.Rooms = append(rooms.Rooms, Room{ID: "s", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: []string{"sbed"}, Cells: []domain.Cell{{X: 33, Z: 4}}})
	sleeping.Beds = append(sleeping.Beds, SleepingBed{ID: "sbed", Definition: "Bed", Room: domain.Known("s"), AccessibleTo: []PawnID{"a", "b"}})
	if got := SuiteClaims(plan, rooms, sleeping, targets, suiteTraits, nil, poor); len(got) != 1 {
		t.Fatalf("furnished suite, poor = %+v", got)
	}
}

func TestSculptureInstallStopsAtTheOwnersShare(t *testing.T) {
	obs, rooms, _ := upgradeFixture(t, RoomQuality{Wealth: 3000, Beauty: -1, Space: 25, Impressiveness: 35})
	targets := map[string]RoomTarget{"Room_1": {Room: "Room_1", Owners: []PawnID{"a"}, Min: ImpressivenessSlightlyImpressive}}
	dear := PackedSculpture{ID: "Thing_dear", Def: SculptureDefinition, MarketValue: 800}
	cheap := PackedSculpture{ID: "Thing_cheap", Def: SculptureDefinition, MarketValue: 90}
	packed := []PackedSculpture{dear, cheap}
	next := func(g RoomGate) (SculptureStep, bool) {
		return NextSculpture(obs, targets, rooms, packed, CoreItemFacts(), g)
	}
	if s, ok := next(pieceGate(StageReserves, map[PawnID]float64{"a": 1000})); !ok || s.Packed != dear.ID {
		t.Fatalf("rich = %+v %v", s, ok)
	}
	if s, ok := next(pieceGate(StageReserves, map[PawnID]float64{"a": 100})); !ok || s.Packed != cheap.ID {
		t.Fatalf("middling = %+v %v", s, ok)
	}
	if s, ok := next(pieceGate(StageReserves, map[PawnID]float64{"a": 10})); ok {
		t.Fatalf("poor = %+v", s)
	}
	if s, ok := next(pieceGate(StageFoothold, map[PawnID]float64{"a": 1000})); ok {
		t.Fatalf("foothold = %+v", s)
	}
	// The rooms owed (and so the art sale) are not gated.
	if owed, _ := SculptureRoomsOwed(domain.Known(obs), targets, rooms).Value(); !owed {
		t.Fatal("owed changed by the gate")
	}
}
