package policy

import (
	"math"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// platformDefs are recorded-style inputs: a platform (factor 1, base 0), a
// wall of 4000 hit points (wall term 100 + 3000/9000*50) and a door of 300
// (60), plain floor.
func platformDefs() ContainmentDefs {
	return ContainmentDefs{Holder: "HoldingPlatform", HolderFactor: 1, WallHP: 4000, DoorHP: 300}
}

func TestPredictContainmentFollowsTheGameFormula(t *testing.T) {
	near := func(got, want float64) {
		t.Helper()
		if math.Abs(got-want) > 1e-6 {
			t.Fatalf("strength %v, want %v", got, want)
		}
	}
	d := platformDefs()
	got, err := d.Predict(ContainmentRoom{})
	if err != nil {
		t.Fatal(err)
	}
	near(got, 100+50.0/3+60)
	// Light adds ten per unit of glow, an open roof takes thirty, a floor
	// stat adds, and each other holder scales the four terms by 0.9.
	got, _ = d.Predict(ContainmentRoom{MeanGlow: 0.5})
	near(got, 100+50.0/3+60+5)
	got, _ = d.Predict(ContainmentRoom{OpenRoof: true})
	near(got, 100+50.0/3+60-30)
	f := d
	f.FloorStrength = 15
	got, _ = f.Predict(ContainmentRoom{})
	near(got, 100+50.0/3+60+15)
	got, _ = d.Predict(ContainmentRoom{OtherHolders: 2, OpenRoof: true})
	near(got, (100+50.0/3+60)*0.81-30)
	// A holding spot's factor 0.7 scales the terms but not its stat base.
	s := d
	s.HolderFactor, s.HolderBase = 0.7, 5
	got, _ = s.Predict(ContainmentRoom{})
	near(got, 5+(100+50.0/3+60)*0.7)
	// Facility offsets are added after the factor, capped by maxSimultaneous.
	fac := d
	fac.Facilities = []ContainmentFacility{{Def: "Inhibitor", Offset: 10, MaxDistance: 5, MaxSimultaneous: 2}, {Def: "Harvester", Offset: -25, MaxDistance: 5, MaxSimultaneous: 1}}
	got, _ = fac.Predict(ContainmentRoom{Facilities: map[string]int{"Inhibitor": 3, "Harvester": 1}})
	near(got, 100+50.0/3+60+20-25)
}

func TestContainmentWallCurveInterpolatesAndClamps(t *testing.T) {
	for hp, want := range map[float64]float64{-5: 0, 0: 0, 500: 50, 1000: 100, 10000: 150, 25000: 150} {
		if got := containmentWallTerm(hp); math.Abs(got-want) > 1e-9 {
			t.Fatalf("wall term at %v hp = %v, want %v", hp, got, want)
		}
	}
}

func TestPredictContainmentRefusesWhatItCannotEvaluate(t *testing.T) {
	for name, mutate := range map[string]func(*ContainmentDefs){
		"holder": func(d *ContainmentDefs) { d.Holder = "" },
		"wall":   func(d *ContainmentDefs) { d.WallHP = 0 },
		"door":   func(d *ContainmentDefs) { d.DoorHP = 0 },
	} {
		d := platformDefs()
		mutate(&d)
		if _, err := d.Predict(ContainmentRoom{}); err == nil {
			t.Fatalf("%s: predicted from nothing", name)
		}
	}
}

func platformFurniture() []FurnitureDefinition {
	return []FurnitureDefinition{{Name: "HoldingPlatform", Available: domain.Known(true), Size: domain.Known(Bounds{Width: 1, Height: 1})}}
}

func planning(entities int, required float64) ContainmentPlanning {
	return ContainmentPlanning{
		Demand:  domain.Known(ContainmentDemand{Entities: entities, Required: required}),
		Holders: domain.Known([]BuiltHolder{}),
		Defs:    domain.Known(platformDefs()),
	}
}

func TestContainmentCellIsOwedWhenTheDesignReachesTheEntity(t *testing.T) {
	need, verdict := ContainmentCellNeed(planning(1, 100), platformFurniture(), FlooringFacts{})
	if !verdict.Owed || verdict.Reason != "" || need.Role != RoomRoleContainmentCell || need.Module != PlannedContainmentCell || len(need.Furniture) != 2 || need.Furniture[0].Defs[0] != "HoldingPlatform" || need.Furniture[1].Defs[0] != ContainmentLampDefinition || !need.Furniture[1].Optional {
		t.Fatalf("%+v %+v", need, verdict)
	}
	// The predicted 176.67 reaches 116.6 plus the margin of 60.
	if _, v := ContainmentCellNeed(planning(1, 116.6), platformFurniture(), FlooringFacts{}); !v.Owed {
		t.Fatalf("equal strength: %+v", v)
	}
}

// TestContainmentCellIsFurnishedWithALamp (#1743): the standing lamp joins the
// cell once the catalog offers it; until then it is left out and the cell is
// owed all the same, and the platform's room holds the lamp either way.
func TestContainmentCellIsFurnishedWithALamp(t *testing.T) {
	lamp := FurnitureDefinition{Name: ContainmentLampDefinition, Available: domain.Known(true), Size: domain.Known(Bounds{Width: 1, Height: 1})}
	withLamp := append(platformFurniture(), lamp)
	need, v := ContainmentCellNeed(planning(1, 100), withLamp, FlooringFacts{})
	if !v.Owed {
		t.Fatalf("%+v", v)
	}
	pieces, ok := need.resolve(withLamp)
	if !ok || len(pieces) != 2 || pieces[1].Piece.Def != ContainmentLampDefinition {
		t.Fatalf("%+v %v", pieces, ok)
	}
	lamp.Available = domain.Known(false)
	pieces, ok = need.resolve(append(platformFurniture(), lamp))
	if !ok || len(pieces) != 1 || pieces[0].Piece.Def != "HoldingPlatform" {
		t.Fatalf("an unresearched lamp is left out: %+v %v", pieces, ok)
	}
	// The predicted strength counts no glow, so the lamp never makes a cell owed.
	if _, v := ContainmentCellNeed(planning(1, 400), withLamp, FlooringFacts{}); v.Owed {
		t.Fatalf("%+v", v)
	}
	shape, _ := need.shape(withLamp)
	plain, _ := need.shape(platformFurniture())
	base := growPlan(LayoutPlan{Zones: coreTestZones()}, 6, 1, BuildTierCamp)
	grown, added, _ := growChildRoom(base, plain, nil)
	if !added {
		t.Fatal("no room grown")
	}
	if _, ok := grown.ChildRoomFor(shape); !ok {
		t.Fatal("a room grown for the platform alone does not hold the lamp's shape")
	}
}

func TestContainmentCellOwesNothingAndSaysWhy(t *testing.T) {
	noDefs := planning(1, 1)
	noDefs.Defs, noDefs.DefsReason = domain.Unknown[ContainmentDefs](), "no stat"
	cases := map[string]struct {
		p      ContainmentPlanning
		f      []FurnitureDefinition
		reason string
	}{
		"no entity":      {planning(0, 0), platformFurniture(), ""},
		"too strong":     {planning(1, 400), platformFurniture(), "needs 400.0"},
		"unread demand":  {ContainmentPlanning{Demand: domain.Unknown[ContainmentDemand]()}, platformFurniture(), "unread"},
		"unread holders": {ContainmentPlanning{Demand: domain.Known(ContainmentDemand{Entities: 1}), Holders: domain.Unknown[[]BuiltHolder]()}, platformFurniture(), "platforms are unread"},
		"no defs":        {noDefs, platformFurniture(), "no stat"},
		"not buildable":  {planning(1, 1), nil, "not buildable"},
	}
	for name, c := range cases {
		need, v := ContainmentCellNeed(c.p, c.f, FlooringFacts{})
		if v.Owed || len(need.Furniture) != 0 || !strings.Contains(v.Reason, c.reason) || (c.reason == "" && v.Reason != "") {
			t.Fatalf("%s: %+v %+v", name, need, v)
		}
	}
}

func TestStandingPlatformThatReachesTheEntityOwesNoCell(t *testing.T) {
	p := planning(1, 100)
	p.Holders = domain.Known([]BuiltHolder{{Strength: 120, Available: true}, {Strength: 300, Available: false}})
	if _, v := ContainmentCellNeed(p, platformFurniture(), FlooringFacts{}); !v.Owed {
		t.Fatalf("a weak or taken platform leaves the cell owed: %+v", v)
	}
	p.Holders = domain.Known([]BuiltHolder{{Strength: 160, Available: true}})
	if _, v := ContainmentCellNeed(p, platformFurniture(), FlooringFacts{}); v.Owed || v.Reason != "" {
		t.Fatalf("a native strength that reaches it owes nothing: %+v", v)
	}
}

func TestContainmentCellRoleTables(t *testing.T) {
	if role, ok := PlannedRoleFor(RoomRoleContainmentCell); !ok || role != PlannedContainmentCell {
		t.Fatal("the ContainmentCell role has no layout module")
	}
	if moduleRoomRoles[PlannedContainmentCell] != RoomRoleContainmentCell {
		t.Fatal("containment cell tables")
	}
	if _, ok := InteriorTemplateFor(RoomRoleContainmentCell); !ok {
		t.Fatal("no interior template")
	}
	if f, err := Facility(RoomRoleContainmentCell); err != nil || f.Status != FacilityImplemented || !f.FurnitureFromGame || f.Content != "Anomaly" {
		t.Fatalf("catalog row: %+v %v", f, err)
	}
}

func TestContainmentCellIsStagedLikeAnyChildRoom(t *testing.T) {
	need, _ := ContainmentCellNeed(planning(1, 100), platformFurniture(), FlooringFacts{})
	defs := platformFurniture()
	shape, ok := need.shape(defs)
	if !ok || shape.Module != PlannedContainmentCell {
		t.Fatalf("shape %+v %v", shape, ok)
	}
	base := growPlan(LayoutPlan{Zones: coreTestZones()}, 6, 1, BuildTierCamp)
	grown, added, _ := growChildRoom(base, shape, nil)
	if !added {
		t.Fatal("no room grown for the platform")
	}
	var room PlannedRoom
	for _, r := range grown.AllRooms() {
		if r.Role == PlannedContainmentCell {
			room = r
		}
	}
	if room.Role == "" {
		t.Fatal("grown plan has no containment cell")
	}
	plan := LayoutPlan{Rooms: []PlannedRoom{room}}
	if step := NextChildRoomStep(plan, RoomObservation{Shapes: testShapes}, GroundCensus{}, nil, []ChildRoomNeed{need}, defs); step.Kind != ChildRoomReconcile {
		t.Fatalf("unbuilt room: %+v", step)
	}
	if step := NextChildRoomStep(plan, tombStanding(room), ringWalls(plan, room), nil, []ChildRoomNeed{need}, defs); step.Kind != ChildRoomReconcile || len(step.Template) == 0 || step.Template[0].DefName != "HoldingPlatform" {
		t.Fatalf("standing room: %+v", step)
	}
}
