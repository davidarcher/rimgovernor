package policy

import (
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func arrivalFixture(t *testing.T) defenseSite {
	t.Helper()
	r := defenseFixture()
	r.Bounds, r.Region = Bounds{Width: 9, Height: 9}, Rectangle{Width: 9, Height: 9}
	r.Home, r.Entrances, r.Cells = domain.Cell{X: 4, Z: 6}, nil, nil
	r.Tick, r.CoverThreshold, r.MinRange = 200000, domain.Known(0.55), domain.Known(20.0)
	for z := int32(0); z < 9; z++ {
		for x := int32(0); x < 9; x++ {
			edge := z == 0 && (x == 1 || x == 2 || x == 7) || z == 8 && x == 4
			r.Cells = append(r.Cells, DefenseCell{Cell: domain.Cell{X: x, Z: z}, Passable: domain.Known(true), Walkable: domain.Known(true), EdgeReachable: domain.Known(edge), CoverFill: domain.Known(0.0)})
		}
	}
	s, err := newDefenseSite(r)
	if err != nil {
		t.Fatal(err)
	}
	return s
}

func TestDefenseSectorsClusterRankAndDeduplicate(t *testing.T) {
	s := arrivalFixture(t)
	sectors, unmatched := s.sectors(s.r.Home)
	if len(sectors) != 3 || len(unmatched) != 0 || sectors[0].PathLength != 2 || !reflect.DeepEqual(sectors[1].Edge, cells(1, 0, 2, 0)) {
		t.Fatalf("sectors: %+v; unmatched: %v", sectors, unmatched)
	}
	a := DefenseArrival{ID: "raid-a", Edge: domain.Cell{X: 1}, Tick: s.r.Tick - 1}
	s.r.Arrivals = []DefenseArrival{a, a, {ID: "raid-b", Edge: domain.Cell{X: 2}, Tick: s.r.Tick - 2}, {ID: "old", Edge: domain.Cell{X: 4, Z: 8}, Tick: 0}, {ID: "unknown-crossing", Edge: domain.Cell{X: 0}, Tick: s.r.Tick}, {ID: "snapped", Edge: domain.Cell{X: 6, Z: 2}, Tick: s.r.Tick}}
	sectors, unmatched = s.sectors(s.r.Home)
	if sectors[0].RecentRaids != 3 || sectors[0].LastArrivalTick != s.r.Tick || !reflect.DeepEqual(sectors[0].Edge, cells(1, 0, 2, 0)) || len(unmatched) != 0 {
		t.Fatalf("ranked sectors: %+v; unmatched: %v", sectors, unmatched)
	}
	// A crossing sampled inside the border snaps to the nearest sector
	// edge within reach; (0,0) is a sector cell's neighbour and so counts,
	// while a crossing farther than the snap radius stays unmatched.
	if sectors[1].RecentRaids != 1 || !reflect.DeepEqual(sectors[1].Edge, cells(7, 0)) {
		t.Fatalf("snapped arrival: %+v", sectors)
	}
	s.r.Bounds, s.r.Region = Bounds{Width: 40, Height: 40}, Rectangle{Width: 40, Height: 40}
	s.r.Arrivals = []DefenseArrival{{ID: "far", Edge: domain.Cell{X: 30, Z: 30}, Tick: s.r.Tick}}
	if _, unmatched = s.sectors(s.r.Home); !reflect.DeepEqual(unmatched, []string{"far"}) {
		t.Fatalf("far arrival matched: %v", unmatched)
	}
	s.r.Bounds, s.r.Region = Bounds{Width: 9, Height: 9}, Rectangle{Width: 9, Height: 9}
	// Both reachability predicates are required. A native edge-reachable
	// cell disconnected from Home within this census proves no local route.
	for _, c := range cells(4, 7, 3, 8, 5, 8) {
		row := s.cells[c]
		row.Passable = domain.Unknown[bool]()
		s.cells[c] = row
	}
	sectors, _ = s.sectors(s.r.Home)
	if len(sectors) != 2 {
		t.Fatalf("disconnected sector retained: %+v", sectors)
	}
}

func TestDefenseSectorOpenPerimeterHasFourSides(t *testing.T) {
	s := arrivalFixture(t)
	for c, row := range s.cells {
		row.EdgeReachable = domain.Known(true)
		s.cells[c] = row
	}
	sectors, _ := s.sectors(s.r.Home)
	if len(sectors) != 4 {
		t.Fatalf("open perimeter collapsed into %d sectors", len(sectors))
	}
	seen := map[domain.Cell]bool{}
	for _, sector := range sectors {
		for _, c := range sector.Edge {
			if seen[c] {
				t.Fatal("duplicate corner", c)
			}
			seen[c] = true
		}
	}
	if len(seen) != 32 {
		t.Fatalf("missing perimeter cells: %d", len(seen))
	}
}

func TestDefenseCoverSetAndDemandOrder(t *testing.T) {
	s := arrivalFixture(t)
	l := DefenseLayout{Entry: domain.Cell{X: 4, Z: 4}, Toward: domain.North, Firing: []FiringPosition{{Cell: domain.Cell{X: 4, Z: 6}, Cover: domain.Cell{X: 4, Z: 5}, Retreat: domain.Cell{X: 4, Z: 7}}}}
	set := func(c domain.Cell, fill float64) {
		row := s.cells[c]
		row.CoverFill = domain.Known(fill)
		s.cells[c] = row
	}
	left, right := domain.Cell{X: 1}, domain.Cell{X: 7}
	set(left, 0.7)
	set(right, 0.7)
	set(domain.Cell{X: 3, Z: 2}, 0.55) // threshold is exclusive
	set(domain.Cell{X: 3, Z: 3}, 0.55001)
	set(domain.Cell{X: 0, Z: 8}, 1) // behind firing positions and off the route tails
	set(l.Firing[0].Cover, 1)
	protected := domain.Cell{X: 2, Z: 2}
	set(protected, 1)
	s.protect[protected] = true
	s.r.Arrivals = []DefenseArrival{{ID: "right", Edge: right, Tick: s.r.Tick}}
	got := s.defenseApproaches(l)
	if len(got.Cover) != 3 || got.Cover[0].Cell != right || got.Cover[0].Sector != 0 {
		t.Fatalf("cover set/order: %+v", got.Cover)
	}
	for _, sector := range got.Sectors {
		if len(sector.Route) == 0 || sector.Route[len(sector.Route)-1] != l.Entry {
			t.Fatalf("route: %+v", sector)
		}
		for i := 1; i < len(sector.Route); i++ {
			if squaredDistance(sector.Route[i-1], sector.Route[i]) != 1 {
				t.Fatal("non-passable route step", sector.Route)
			}
		}
		if sector.Finding != "route_bypasses_entry" {
			t.Fatal("open flank not reported", sector)
		}
	}
	row := s.cells[left]
	row.NaturalRock = domain.Known(true)
	s.cells[left] = row
	got = s.defenseApproaches(l)
	for _, c := range got.Cover {
		if c.Cell == left && c.Hold != "map_edge_rock" {
			t.Fatal("edge rock not held", c)
		}
	}
	s.r.CoverThreshold = domain.Unknown[float64]()
	if got = s.defenseApproaches(l); got.Hold != "cover_threshold_unknown" || len(got.Cover) != 0 {
		t.Fatal("unknown threshold admitted cover", got)
	}
}

func TestDefenseApproachesPreserveAcceptedGeometry(t *testing.T) {
	r := defenseFixture()
	l, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	r.CoverThreshold = domain.Known(0.55)
	with, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(l.Geometry(), with.Geometry()) || l.LinesVerified != with.LinesVerified {
		t.Fatal("cover assessment changed geometry or verification")
	}
	reserved := map[domain.Cell]bool{}
	for _, tier := range with.Tiers {
		for _, c := range tier.Reserved {
			reserved[c] = true
		}
	}
	for _, c := range with.Approaches.Cover {
		if reserved[c.Cell] {
			t.Fatal("reserved cover selected", c.Cell)
		}
	}
	for _, sector := range with.Approaches.Sectors {
		if sector.Finding != "" {
			t.Fatal("safe lane reported as a bypass", sector)
		}
	}
}

func TestDefenseArrivalValidation(t *testing.T) {
	for name, edit := range map[string]func(*DefenseRequest){
		"future":  func(r *DefenseRequest) { r.Arrivals = []DefenseArrival{{ID: "a", Tick: 1}} },
		"outside": func(r *DefenseRequest) { r.Arrivals = []DefenseArrival{{ID: "a", Edge: domain.Cell{X: 300}}} },
		"conflict": func(r *DefenseRequest) {
			r.Tick = 2
			r.Arrivals = []DefenseArrival{{ID: "a", Tick: 1}, {ID: "a", Tick: 2}}
		},
		"negative tick":  func(r *DefenseRequest) { r.Tick = -1 },
		"nan threshold":  func(r *DefenseRequest) { r.CoverThreshold = domain.Known(math.NaN()) },
		"full threshold": func(r *DefenseRequest) { r.CoverThreshold = domain.Known(1.0) },
		"oversized":      func(r *DefenseRequest) { r.Arrivals = make([]DefenseArrival, 129) },
	} {
		t.Run(name, func(t *testing.T) {
			r := defenseFixture()
			edit(&r)
			if _, err := DefenseLayouts(r); err == nil {
				t.Fatal("invalid arrival request accepted")
			}
		})
	}
}

func TestDefenseCoverRangeUnknownsAndRockHolds(t *testing.T) {
	s := arrivalFixture(t)
	l := DefenseLayout{Entry: domain.Cell{X: 4, Z: 4}, Toward: domain.North, Firing: []FiringPosition{{Cell: domain.Cell{X: 4, Z: 6}}}}
	rock := domain.Cell{X: 1, Z: 2}
	for _, c := range append(cells(1, 1, 0, 2, 2, 2, 1, 3), rock) {
		row := s.cells[c]
		row.Passable = domain.Known(false)
		s.cells[c] = row
	}
	row := s.cells[rock]
	row.CoverFill, row.NaturalRock = domain.Known(1.0), domain.Known(true)
	s.cells[rock] = row
	got := s.defenseApproaches(l)
	if len(got.Cover) != 1 || got.Cover[0].Hold != "mountain_interior" {
		t.Fatal("unexposed rock not held", got.Cover)
	}
	// Exposed on one side but part of a mass: a rock face, not a boulder.
	face := s.cells[domain.Cell{X: 1, Z: 3}]
	face.Passable, face.NaturalRock = domain.Known(true), domain.Known(false)
	s.cells[domain.Cell{X: 1, Z: 3}] = face
	neighbour := s.cells[domain.Cell{X: 1, Z: 1}]
	neighbour.NaturalRock = domain.Known(true)
	s.cells[domain.Cell{X: 1, Z: 1}] = neighbour
	if got = s.defenseApproaches(l); len(got.Cover) != 1 || got.Cover[0].Hold != "rock_face" {
		t.Fatal("rock face not held", got.Cover)
	}
	// A lone rock with passable, rock-free neighbours is orderable.
	for _, c := range cells(1, 1, 0, 2, 2, 2) {
		row := s.cells[c]
		row.Passable, row.NaturalRock = domain.Known(true), domain.Known(false)
		s.cells[c] = row
	}
	if got = s.defenseApproaches(l); len(got.Cover) != 1 || got.Cover[0].Hold != "" {
		t.Fatal("lone rock held", got.Cover)
	}
	s.r.MinRange = domain.Known(2.0)
	if got = s.defenseApproaches(l); len(got.Cover) != 0 {
		t.Fatal("out-of-range cover selected", got.Cover)
	}
	s.r.MinRange = domain.Unknown[float64]()
	if got = s.defenseApproaches(l); got.Hold != "engagement_range_unknown" {
		t.Fatal("unknown range not held", got)
	}
	s.r.MinRange = domain.Known(20.0)
	row.CoverFill = domain.Unknown[float64]()
	s.cells[rock] = row
	if got = s.defenseApproaches(l); len(got.Cover) != 0 {
		t.Fatal("unknown cover selected", got.Cover)
	}
}

func TestDefenseSectorRoutesRespectProposedWalls(t *testing.T) {
	s := arrivalFixture(t)
	l := DefenseLayout{Entry: domain.Cell{X: 4, Z: 4}, Toward: domain.North}
	var walls []domain.Building
	for x := int32(0); x < 9; x++ {
		b, err := domain.NewBuilding(s.r.Definitions.Wall, domain.Cell{X: x, Z: 3}, domain.North, "")
		if err != nil {
			t.Fatal(err)
		}
		walls = append(walls, b)
	}
	l.Tiers = []DefenseTier{{Name: TierFunnel, Buildings: walls}}
	got := s.defenseApproaches(l)
	blocked := 0
	for _, sector := range got.Sectors {
		if sector.Edge[0].Z == 0 {
			blocked++
			if len(sector.Route) != 0 || sector.Finding != "entry_unreachable" {
				t.Fatal("route crossed proposed wall", sector)
			}
		}
	}
	if blocked != 2 {
		t.Fatal("lost blocked sectors", got.Sectors)
	}
}

func TestDefenseApproachesDeterministicCensusOrder(t *testing.T) {
	r := defenseFixture()
	r.CoverThreshold = domain.Known(0.55)
	a, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	for i, j := 0, len(r.Cells)-1; i < j; i, j = i+1, j-1 {
		r.Cells[i], r.Cells[j] = r.Cells[j], r.Cells[i]
	}
	b, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(a.Approaches, b.Approaches) {
		t.Fatal("census order changed arrival model")
	}
}

// A colony centre that has drifted onto a building floods the sectors
// from the layout's Entry instead of finding none.
func TestDefenseApproachesFloodFromEntryWhenHomeIsBlocked(t *testing.T) {
	r := arrivalFixture(t).r
	layout := DefenseLayout{Entry: domain.Cell{X: 4, Z: 4}, Toward: domain.North}
	blocked := r
	blocked.Cells = append([]DefenseCell(nil), r.Cells...)
	for i := range blocked.Cells {
		if blocked.Cells[i].Cell == r.Home {
			blocked.Cells[i].Passable, blocked.Cells[i].Walkable = domain.Known(false), domain.Known(false)
		}
	}
	open, err := DefenseApproachesFor(r, layout)
	if err != nil {
		t.Fatal(err)
	}
	got, err := DefenseApproachesFor(blocked, layout)
	if err != nil {
		t.Fatal(err)
	}
	if len(open.Sectors) == 0 || len(got.Sectors) != len(open.Sectors) {
		t.Fatalf("sectors %d with Home blocked, %d open", len(got.Sectors), len(open.Sectors))
	}
}
