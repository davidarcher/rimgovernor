package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// defenseFixture is a 20x30 census: a four-wide rock corridor (x 8..11) from
// the map edge at z=0 into an open home area (z 15..28, x 1..18).
func defenseFixture() DefenseRequest {
	r := DefenseRequest{
		Bounds:      Bounds{Width: 250, Height: 250},
		Region:      Rectangle{X: 0, Z: 0, Width: 20, Height: 30},
		Home:        domain.Cell{X: 9, Z: 27},
		Entrances:   []domain.Cell{{X: 9, Z: 26}},
		Definitions: DefenseDefinitions{Sandbag: "Sandbags", Wall: "Wall", WallStuff: "BlocksGranite", Fence: "Fence", FenceStuff: "WoodLog", Trap: "TrapSpike", TrapStuff: "WoodLog", Door: "Door", DoorStuff: "WoodLog", Floor: "WoodPlankFloor"},
		MinRange:    domain.Known(25.9),
		Defenders:   3,
		UnitCosts: map[string][]Amount{
			"Sandbags": {{Resource: "Cloth", Count: 5}}, "Wall": {{Resource: "BlocksGranite", Count: 5}},
			"Fence": {{Resource: "WoodLog", Count: 2}}, "TrapSpike": {{Resource: "WoodLog", Count: 45}}, "Door": {{Resource: "WoodLog", Count: 25}},
		},
	}
	for x := int32(0); x < 20; x++ {
		for z := int32(0); z < 30; z++ {
			open := z < 15 && x >= 8 && x <= 11 || z >= 15 && z <= 28 && x >= 1 && x <= 18
			c := DefenseCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(open), Passable: domain.Known(open), BlocksSight: domain.Known(!open),
				PlayerOwned: domain.Known(false), NaturalRock: domain.Known(!open), EdgeReachable: domain.Known(open), HomeArea: domain.Known(open && z >= 15), Door: domain.Known(false), CoverFill: domain.Known(0.0)}
			if !open {
				c.CoverFill, c.Edifice = domain.Known(1.0), "Granite"
			}
			r.Cells = append(r.Cells, c)
		}
	}
	return r
}
func cells(xs ...int32) []domain.Cell {
	out := make([]domain.Cell, 0, len(xs)/2)
	for i := 0; i+1 < len(xs); i += 2 {
		out = append(out, domain.Cell{X: xs[i], Z: xs[i+1]})
	}
	return out
}
func placed(t *testing.T, tier DefenseTier) map[domain.Cell]string {
	t.Helper()
	out := map[domain.Cell]string{}
	for _, b := range tier.Buildings {
		if _, dup := out[b.Cell()]; dup {
			t.Fatal("duplicate placement", b.Cell())
		}
		out[b.Cell()] = b.Definition()
	}
	return out
}

func TestDefenseLayoutCorridorAtNarrowestChokepoint(t *testing.T) {
	layout, err := DefenseLayouts(defenseFixture())
	if err != nil {
		t.Fatal(err)
	}
	if layout.Chokepoint != (domain.Cell{X: 9, Z: 14}) || layout.Toward != domain.North || layout.Width != 4 || layout.Entry != layout.Chokepoint {
		t.Fatalf("%+v", layout)
	}
	if !reflect.DeepEqual(layout.TrapLane, cells(9, 14, 9, 15, 9, 16, 9, 17, 9, 18, 9, 19)) || !reflect.DeepEqual(layout.SafeLane, cells(8, 14, 8, 15, 8, 16, 8, 17, 8, 18, 8, 19)) {
		t.Fatal(layout.TrapLane, layout.SafeLane)
	}
	corridor, _ := layout.Tier(TierTrapCorridor)
	got := placed(t, corridor)
	want := map[domain.Cell]string{}
	// Traps on rows 1 and 3 (RimWorld refuses adjacent traps) with fences
	// between them price the trap lane above the two wooden doors of the
	// safe lane for colonists, and below them for raiders (#619).
	for _, c := range cells(9, 15, 9, 17) {
		want[c] = "TrapSpike"
	}
	for _, c := range cells(9, 16, 9, 18) {
		want[c] = "Fence"
	}
	for _, c := range cells(8, 15, 8, 17) {
		want[c] = "Door"
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("corridor %v", got)
	}
	funnel, _ := layout.Tier(TierFunnel)
	got = placed(t, funnel)
	want = map[domain.Cell]string{}
	for _, c := range cells(10, 14, 11, 14, 7, 15, 10, 15, 7, 16, 10, 16, 7, 17, 10, 17, 7, 18, 10, 18, 7, 19, 10, 19) {
		want[c] = "Wall"
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("funnel %v", got)
	}
	if v, k := funnel.Costs.Value(); !k || !reflect.DeepEqual(v, []Amount{{Resource: "BlocksGranite", Count: 60}}) {
		t.Fatal(funnel.Costs)
	}
	if v, k := corridor.Costs.Value(); !k || !reflect.DeepEqual(v, []Amount{{Resource: "WoodLog", Count: 144}}) {
		t.Fatal(corridor.Costs)
	}
	choke, _ := layout.Tier(TierChokepoint)
	if len(choke.Buildings) != 0 || len(choke.Reserved) != 12 {
		t.Fatal(choke)
	}
	firing, _ := layout.Tier(TierFiringLine)
	if len(layout.Firing) != 3 || len(firing.Buildings) != 6 || layout.LinesVerified {
		t.Fatalf("%+v", layout.Firing)
	}
	// Each shooter cell is floored so nothing grows onto the position (#224).
	got = placed(t, firing)
	want = map[domain.Cell]string{}
	for _, c := range cells(9, 22, 8, 22, 10, 22) {
		want[c] = "Sandbags"
	}
	for _, c := range cells(9, 23, 8, 23, 10, 23) {
		want[c] = "WoodPlankFloor"
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("firing line %v", got)
	}
	wantFiring := []FiringPosition{
		{Cell: domain.Cell{X: 9, Z: 23}, Cover: domain.Cell{X: 9, Z: 22}, Retreat: domain.Cell{X: 9, Z: 24}},
		{Cell: domain.Cell{X: 8, Z: 23}, Cover: domain.Cell{X: 8, Z: 22}, Retreat: domain.Cell{X: 8, Z: 24}},
		{Cell: domain.Cell{X: 10, Z: 23}, Cover: domain.Cell{X: 10, Z: 22}, Retreat: domain.Cell{X: 10, Z: 24}},
	}
	if !reflect.DeepEqual(layout.Firing, wantFiring) {
		t.Fatalf("%+v", layout.Firing)
	}
	f, a := layout.Probe()
	if !reflect.DeepEqual(f, cells(9, 23, 8, 23, 10, 23)) || !reflect.DeepEqual(a, cells(9, 14)) {
		t.Fatal(f, a)
	}
	again, _ := DefenseLayouts(defenseFixture())
	if !reflect.DeepEqual(layout, again) {
		t.Fatal("layout is not deterministic")
	}
}
func TestDefenseLayoutLinesOfFire(t *testing.T) {
	r := defenseFixture()
	entry := domain.Cell{X: 9, Z: 14}
	r.Lines = []DefenseLine{
		{From: domain.Cell{X: 9, Z: 23}, To: entry, LineOfSight: domain.Known(false)},
		{From: domain.Cell{X: 8, Z: 23}, To: entry, LineOfSight: domain.Known(true)},
		{From: domain.Cell{X: 10, Z: 23}, To: entry, LineOfSight: domain.Known(true)},
		{From: domain.Cell{X: 7, Z: 23}, To: entry, LineOfSight: domain.Known(true)},
	}
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	if !layout.LinesVerified || !reflect.DeepEqual([]domain.Cell{layout.Firing[0].Cell, layout.Firing[1].Cell, layout.Firing[2].Cell}, cells(8, 23, 10, 23, 7, 23)) {
		t.Fatalf("%+v", layout.Firing)
	}
	r.Lines[3].LineOfSight = domain.Unknown[bool]()
	if layout, err = DefenseLayouts(r); err != nil || layout.LinesVerified || layout.Firing[2].Verified {
		t.Fatal(layout.Firing, err)
	}
}
func TestDefenseLayoutRangeAndDefenders(t *testing.T) {
	r := defenseFixture()
	r.MinRange = domain.Unknown[float64]()
	layout, err := DefenseLayouts(r)
	if tier, _ := layout.Tier(TierFiringLine); err != nil || len(layout.Firing) != 0 || len(tier.Buildings) != 0 {
		t.Fatal(layout.Firing, err)
	}
	r.MinRange = domain.Known(5.0)
	if layout, err = DefenseLayouts(r); err != nil || len(layout.Firing) != 0 {
		t.Fatal("firing cells beyond range", layout.Firing, err)
	}
	r.MinRange, r.Defenders = domain.Known(25.9), 0
	if layout, err = DefenseLayouts(r); err != nil || len(layout.Firing) != 0 {
		t.Fatal(layout.Firing, err)
	}
	r.Defenders = 8
	if layout, err = DefenseLayouts(r); err != nil || len(layout.Firing) != 8 {
		t.Fatal(layout.Firing, err)
	}
	r.Defenders = 9
	if _, err = DefenseLayouts(r); err == nil {
		t.Fatal("defender bound")
	}
}
func TestDefenseLayoutRefusesProtectedAndCutOff(t *testing.T) {
	r := defenseFixture()
	r.Protected = cells(7, 17)
	if _, err := DefenseLayouts(r); err == nil {
		t.Fatal("wall on a protected walkway")
	}
	r = defenseFixture()
	r.Protected = cells(9, 17)
	if _, err := DefenseLayouts(r); err == nil {
		t.Fatal("trap on a protected walkway")
	}
	r = defenseFixture()
	for i := range r.Cells {
		if r.Cells[i].Cell == (domain.Cell{X: 15, Z: 5}) {
			r.Cells[i].Passable, r.Cells[i].Walkable, r.Cells[i].EdgeReachable = domain.Known(true), domain.Known(true), domain.Known(false)
		}
	}
	r.Entrances = cells(15, 5)
	if _, err := DefenseLayouts(r); err == nil {
		t.Fatal("entrance without a route accepted")
	}
	r = defenseFixture()
	r.UnitCosts = nil
	layout, err := DefenseLayouts(r)
	if funnel, _ := layout.Tier(TierFunnel); err != nil {
		t.Fatal(err)
	} else if _, known := funnel.Costs.Value(); known {
		t.Fatal("cost invented without unit costs")
	}
}
func TestDefenseLayoutUnknownFactsBlock(t *testing.T) {
	r := defenseFixture()
	for i := range r.Cells {
		if r.Cells[i].Cell == (domain.Cell{X: 10, Z: 16}) {
			r.Cells[i].Walkable = domain.Unknown[bool]()
		}
	}
	if _, err := DefenseLayouts(r); err == nil {
		t.Fatal("unknown walkability became a wall site")
	}
	r = defenseFixture()
	r.Cells = r.Cells[:len(r.Cells)/2]
	if _, err := DefenseLayouts(r); err == nil {
		t.Fatal("missing census became a route")
	}
}
func TestDefenseLayoutNoChokepoint(t *testing.T) {
	r := defenseFixture()
	for i := range r.Cells {
		c := &r.Cells[i]
		if c.Cell.Z < 15 && c.Cell.X >= 1 && c.Cell.X <= 18 {
			c.Passable, c.Walkable, c.EdgeReachable, c.NaturalRock, c.BlocksSight, c.CoverFill, c.Edifice = domain.Known(true), domain.Known(true), domain.Known(true), domain.Known(false), domain.Known(false), domain.Known(0.0), ""
		}
	}
	if _, err := DefenseLayouts(r); err == nil {
		t.Fatal("18-wide approach accepted as a chokepoint")
	}
}
func TestDefenseLayoutRejectsInvalidRequests(t *testing.T) {
	for name, edit := range map[string]func(*DefenseRequest){
		"region outside bounds": func(r *DefenseRequest) { r.Region.Width = 300 },
		"home outside region":   func(r *DefenseRequest) { r.Home = domain.Cell{X: 40, Z: 40} },
		"duplicate cell":        func(r *DefenseRequest) { r.Cells = append(r.Cells, r.Cells[0]) },
		"missing definition":    func(r *DefenseRequest) { r.Definitions.Trap = "" },
		"negative range":        func(r *DefenseRequest) { r.MinRange = domain.Known(-1.0) },
		"cover over one":        func(r *DefenseRequest) { r.Cells[0].CoverFill = domain.Known(2.0) },
		"entrance outside":      func(r *DefenseRequest) { r.Entrances = cells(99, 99) },
		"line outside":          func(r *DefenseRequest) { r.Lines = []DefenseLine{{From: domain.Cell{X: 99, Z: 0}}} },
	} {
		r := defenseFixture()
		edit(&r)
		if _, err := DefenseLayouts(r); err == nil {
			t.Fatal(name, "accepted")
		}
	}
}
