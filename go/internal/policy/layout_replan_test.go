package policy

import (
	"math/rand"
	"reflect"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// replanBudget is the paused-map time one hourly replan should stay well
// under on the baseline fixture (#1958).
const replanBudget = time.Second

func replanFixture(t testing.TB) (MapSurvey, LayoutPlan) {
	t.Helper()
	s := zoningSurvey(120, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	plan, ok := DeriveLayoutPlan(s, 3, BuildTierCamp, nil, 0, 0).Value()
	if !ok {
		t.Fatal("no plan")
	}
	return s, plan
}

// fixedRoom is a room as it was when something of ours first stood on it.
type fixedRoom struct {
	door  domain.Cell
	doors []Door
	link  *domain.Cell
}

// #1958: for any sequence of pawn growth, research (the tier) and terrain
// changes, no fixed room ever changes its Interior, Door or Doors, and no
// room sited again lands on something of ours.
func TestReplanNeverChangesFixedRooms(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	was := planWeights.ReplanGain
	t.Cleanup(func() { planWeights.ReplanGain = was })
	for _, gain := range []int{was, -1 << 30} {
		planWeights.ReplanGain = gain
		replanNeverChangesFixedRooms(t)
	}
}

func replanNeverChangesFixedRooms(t *testing.T) {
	for seed := int64(1); seed <= 3; seed++ {
		rng := rand.New(rand.NewSource(seed))
		s, plan := replanFixture(t)
		pawns, tier := 3, BuildTierCamp
		fixed := map[Rectangle]fixedRoom{}
		occupied := map[domain.Cell]bool{}
		for step := 0; step < 5; step++ {
			switch rng.Intn(3) {
			case 0:
				pawns += 1 + rng.Intn(3)
			case 1:
				tier = min(tier+1, BuildTierSpacer)
			default:
				// A rock patch, off every fixed room and the plan's hallways.
				x, z := int32(rng.Intn(110)), int32(rng.Intn(110))
				patch := Rectangle{X: x, Z: z, Width: 4, Height: 4}
				clear := true
				for _, r := range plan.AllRooms() {
					clear = clear && !rectsOverlap(pad(roomWalls(r), 1), patch)
				}
				for _, h := range spineRects(plan.Hallways()) {
					clear = clear && !rectsOverlap(pad(h, 1), patch)
				}
				for i := range s.Cells {
					if clear && contains(patch, s.Cells[i].Cell) {
						s.Cells[i].Rock, s.Cells[i].Walkable = true, false
					}
				}
			}
			// Something is built on a few more rooms: a wall cell each.
			for _, r := range plan.AllRooms() {
				if rng.Intn(4) == 0 {
					occupied[domain.Cell{X: r.Interior.X - 1, Z: r.Interior.Z - 1}] = true
				}
			}
			growth := RoomGrowth{Fixed: FixedRooms(plan, GroundCensus{}, occupied), Occupied: occupied}
			for _, r := range plan.AllRooms() {
				if growth.Fixed[r.Interior] {
					if _, ok := fixed[r.Interior]; !ok {
						fixed[r.Interior] = fixedRoom{door: r.Door, doors: r.Doors, link: r.Link}
					}
				}
			}
			next, _, _ := ReplanLayoutWithRooms(plan, s, growth, 0, pawns, 0, tier, nil, nil)
			have := map[Rectangle]PlannedRoom{}
			for _, r := range next.AllRooms() {
				have[r.Interior] = r
			}
			for in, was := range fixed {
				got, ok := have[in]
				if !ok {
					t.Fatalf("seed %d step %d: fixed room %v left the plan", seed, step, in)
				}
				if got.Door != was.door || !reflect.DeepEqual(got.Doors, was.doors) || !reflect.DeepEqual(got.Link, was.link) {
					t.Fatalf("seed %d step %d: fixed room %v changed doors: %v %v -> %v %v", seed, step, in, was.door, was.doors, got.Door, got.Doors)
				}
			}
			// A room not fixed lands on nothing of ours but the wall of a
			// fixed neighbour it shares: nothing built is in its planned ground.
			foreign := map[domain.Cell]bool{}
			for c := range occupied {
				foreign[c] = true
			}
			for _, r := range next.AllRooms() {
				if _, isFixed := fixed[r.Interior]; isFixed {
					for _, c := range rectCells(roomWalls(r)) {
						delete(foreign, c)
					}
				}
			}
			for _, r := range next.AllRooms() {
				if _, isFixed := fixed[r.Interior]; !isFixed && rectHits(roomWalls(r), foreign) {
					t.Fatalf("seed %d step %d: planned %s %v holds a built cell", seed, step, r.Role, r.Interior)
				}
			}
			plan = next
		}
	}
}

// #1958: a plan replanned again with the same inputs is byte-identical, and
// a gain under the threshold leaves it so however many times it is checked.
func TestReplanHysteresisKeepsThePlan(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s, plan := replanFixture(t)
	was := planWeights.ReplanGain
	t.Cleanup(func() { planWeights.ReplanGain = was })
	planWeights.ReplanGain = 1 << 30
	for range 2 {
		next, changed, _ := ReplanLayoutWithRooms(plan, s, RoomGrowth{Fixed: map[Rectangle]bool{}}, 0, 3, 0, BuildTierCamp, nil, nil)
		if changed || !reflect.DeepEqual(next, plan) {
			t.Fatalf("a gain under the threshold changed the plan: %s -> %s", plan.Summary(), next.Summary())
		}
	}
}

// #1958: a gain above the threshold re-sites the unbuilt rooms only.
func TestReplanAboveThresholdMovesOnlyUnbuiltRooms(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s, plan := replanFixture(t)
	was := planWeights.ReplanGain
	t.Cleanup(func() { planWeights.ReplanGain = was })
	planWeights.ReplanGain = -1 << 30
	built := map[Rectangle]bool{}
	for i, r := range plan.Rooms {
		if i%2 == 0 {
			built[r.Interior] = true
		}
	}
	next, changed, _ := ReplanLayoutWithRooms(plan, s, RoomGrowth{Fixed: built}, 0, 3, 0, BuildTierCamp, nil, nil)
	if !changed {
		t.Fatal("nothing re-sited")
	}
	have := map[Rectangle]PlannedRoom{}
	for _, r := range next.AllRooms() {
		have[r.Interior] = r
	}
	for _, r := range plan.Rooms {
		if got, ok := have[r.Interior]; built[r.Interior] && (!ok || got.Door != r.Door) {
			t.Fatalf("built room %v moved", r.Interior)
		}
	}
	if !next.Valid() {
		t.Fatal("replanned plan is invalid")
	}
}

// #1958: with the census unknown (Fixed nil) no room is known to be
// unbuilt, so nothing is re-sited however large the gain.
func TestReplanWithUnknownCensusKeepsEveryRoom(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s, plan := replanFixture(t)
	was := planWeights.ReplanGain
	t.Cleanup(func() { planWeights.ReplanGain = was })
	planWeights.ReplanGain = -1 << 30
	next, changed, _ := ReplanLayoutWithRooms(plan, s, RoomGrowth{}, 0, 3, 0, BuildTierCamp, nil, nil)
	if changed || !reflect.DeepEqual(next, plan) {
		t.Fatalf("a plan with an unknown census changed: %s -> %s", plan.Summary(), next.Summary())
	}
}

// BenchmarkReplanHourly is the hourly replan on the baseline fixture with
// the growth stage and the re-siting stage both running; it fails when one
// replan runs over replanBudget.
func BenchmarkReplanHourly(b *testing.B) {
	s := loadSurvey(b, baselineSurveyPath)
	plan, ok := DeriveLayoutPlan(s, 3, BuildTierCamp, nil, 0, 0).Value()
	if !ok {
		b.Fatal("no plan")
	}
	built := map[Rectangle]bool{}
	for i, r := range plan.Rooms {
		if i%2 == 0 {
			built[r.Interior] = true
		}
	}
	b.ResetTimer()
	start, n := time.Now(), 0
	for b.Loop() {
		ReplanLayoutWithRooms(plan, s, RoomGrowth{Fixed: built}, 0, 5, 0, BuildTierCamp, nil, nil)
		n++
	}
	if per := time.Since(start) / time.Duration(n); per > replanBudget {
		b.Fatalf("replan took %v, budget %v", per, replanBudget)
	}
}
