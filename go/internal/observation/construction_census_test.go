package observation

import (
	"fmt"
	"math/rand"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	pp "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	"google.golang.org/protobuf/proto"
)

func siteTestRow(id string, status o.BuildingStatus, hp int32, need int64) *o.BuildingState {
	row := &o.BuildingState{
		Building:  &o.EntityRef{Id: proto.String(id), DefName: proto.String("Wall"), Position: &c.Cell{X: proto.Int32(int32(len(id))), Z: proto.Int32(1)}},
		Rotation:  pp.Rotation_ROTATION_NORTH.Enum(),
		Status:    status.Enum(),
		Stuff:     proto.String("WoodLog"),
		HitPoints: proto.Int32(hp),
	}
	if status != o.BuildingStatus_BUILDING_STATUS_BUILT {
		row.BuildDefName = proto.String("Wall")
		row.Construction = &o.ConstructionState{Resources: []*o.MaterialDeficit{{DefName: proto.String("WoodLog"), StillNeeded: proto.Int64(need)}}}
	}
	return row
}

// TestConstructionStateMatchesWholeTable: the census derived frame
// to frame from the changed rows is the census projected from the whole
// table, whatever the edits (status changes, removals, need changes).
func TestConstructionStateMatchesWholeTable(t *testing.T) {
	rng := rand.New(rand.NewSource(3))
	statuses := []o.BuildingStatus{o.BuildingStatus_BUILDING_STATUS_BUILT, o.BuildingStatus_BUILDING_STATUS_BLUEPRINT, o.BuildingStatus_BUILDING_STATUS_FRAME}
	var rows bridge.Buildings
	for i := 0; i < 300; i++ {
		id := fmt.Sprintf("b%d", i)
		rows = rows.With(id, siteTestRow(id, statuses[i%3], 10, int64(i%5)))
	}
	var state *constructionState
	for round := 0; round < 60; round++ {
		for e := 0; e < 1+rng.Intn(6); e++ {
			id := fmt.Sprintf("b%d", rng.Intn(320))
			if rng.Intn(4) == 0 {
				rows = rows.Without(id)
			} else {
				rows = rows.With(id, siteTestRow(id, statuses[rng.Intn(3)], int32(rng.Intn(100)), int64(rng.Intn(6))))
			}
		}
		state = state.next(rows)
		// No memo: the census projected from the whole table.
		whole, wholeDeficit, err := ConstructionFromCensus(&bridge.BuildingCensus{Rows: rows})
		if err != nil {
			t.Fatal(err)
		}
		// Row order is by id here and by table order there.
		if !sameConstruction(state.census, whole) {
			t.Fatalf("round %d: incremental census differs from the whole-table one", round)
		}
		if got, want := state.deficitFact, wholeDeficit; !reflect.DeepEqual(got, want) {
			t.Fatalf("round %d: deficit %v, want %v", round, got, want)
		}
	}
}

func sameConstruction(a, b domain.Fact[policy.CurrentConstruction]) bool {
	x, ak := a.Value()
	y, bk := b.Value()
	if ak != bk {
		return false
	}
	if !ak {
		return true
	}
	key := func(r policy.CurrentConstruction) (out []string) {
		for _, b := range r.Buildings {
			out = append(out, fmt.Sprintf("b %v", b))
		}
		for _, s := range r.Sites {
			out = append(out, fmt.Sprintf("s %v", s))
		}
		slicesSort(out)
		return out
	}
	return reflect.DeepEqual(key(x), key(y))
}

func slicesSort(s []string) {
	for i := 1; i < len(s); i++ {
		for j := i; j > 0 && s[j] < s[j-1]; j-- {
			s[j], s[j-1] = s[j-1], s[j]
		}
	}
}

// TestConstructionStateCostIsIndependentOfSize: a frame that
// changes one building's hit points, which no projection reads, costs the
// same few allocations at 100 buildings and at 10000, and rebuilds no
// list.
func TestConstructionStateCostIsIndependentOfSize(t *testing.T) {
	cost := func(n int) float64 {
		var rows bridge.Buildings
		for i := 0; i < n; i++ {
			id := fmt.Sprintf("b%d", i)
			rows = rows.With(id, siteTestRow(id, o.BuildingStatus_BUILDING_STATUS_BUILT, 10, 0))
		}
		state := (*constructionState)(nil).next(rows)
		before := state.census
		hp := int32(10)
		allocs := testing.AllocsPerRun(30, func() {
			hp++
			rows = rows.With("b5", siteTestRow("b5", o.BuildingStatus_BUILDING_STATUS_BUILT, hp, 0))
			state = state.next(rows)
		})
		if after, _ := state.census.Value(); len(after.Buildings) != n {
			t.Fatalf("%d buildings in the census, want %d", len(after.Buildings), n)
		}
		a, _ := before.Value()
		b, _ := state.census.Value()
		if &a.Buildings[0] != &b.Buildings[0] {
			t.Fatal("an unchanged projection rebuilt the census list")
		}
		return allocs
	}
	small, large := cost(100), cost(10000)
	t.Logf("allocations per hit-point frame: %.0f at 100 buildings, %.0f at 10000", small, large)
	if large > small+10 {
		t.Fatalf("frame cost grew with the table: %.0f vs %.0f", small, large)
	}
}
