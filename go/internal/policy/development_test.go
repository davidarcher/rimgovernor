package policy

import (
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func developmentFixture() DevelopmentRequest {
	return DevelopmentRequest{
		Snapshot: domain.GenerationSnapshot{Colony: "colony", Map: 1, Load: "load", Plan: "plan"}, Tick: 100,
		Workers: domain.Known(3),
		Concerns: []DevelopmentConcern{
			{ID: "storage", Priority: 3, Deficit: domain.Known(1.0)},
			{ID: "defense", Priority: 3, Deficit: domain.Known(.5)},
			{ID: "wood", Priority: 3, Deficit: domain.Known(50.0 / 350)},
		},
	}
}
func rank(t *testing.T, r DevelopmentRequest) DevelopmentState {
	t.Helper()
	s, e := RankDevelopment(r)
	if e != nil {
		t.Fatal(e)
	}
	return s
}
func selected(s DevelopmentState) []ConcernID {
	var ids []ConcernID
	for _, r := range s.Rows {
		if r.Selected {
			ids = append(ids, r.Concern)
		}
	}
	return ids
}
func requireSelected(t *testing.T, s DevelopmentState, ids ...ConcernID) {
	t.Helper()
	if !reflect.DeepEqual(selected(s), ids) {
		t.Fatalf("selected %v, want %v: %+v", selected(s), ids, s.Rows)
	}
}
func TestDevelopmentCapacity(t *testing.T) {
	r := developmentFixture()
	s := rank(t, r)
	requireSelected(t, s, "storage", "defense", "wood")
	r.Workers = domain.Known(1)
	requireSelected(t, rank(t, r), "storage", "defense", "wood")
	r.Workers = domain.Known(0)
	requireSelected(t, rank(t, r))
	r.Workers = domain.Unknown[int]()
	s = rank(t, r)
	requireSelected(t, s)
	if s.Rows[0].Reason != DevelopmentWorkersUnknown {
		t.Fatal(s)
	}
}

func TestUnavailableMethodDoesNotStarveExecutableDevelopment(t *testing.T) {
	r := developmentFixture()
	r.Concerns[0].MethodUnavailable = true
	s := rank(t, r)
	requireSelected(t, s, "defense", "wood")
	if s.Rows[0].Reason != DevelopmentMethodUnavailable || s.Rows[0].Deficit != domain.Known(1.0) {
		t.Fatal(s)
	}
	r.Concerns[0].MethodUnavailable = false
	requireSelected(t, rank(t, r), "storage", "defense", "wood")
}

func TestDevelopmentHolds(t *testing.T) {
	for _, kind := range []string{"emergency", "unknown", "blocked"} {
		t.Run(kind, func(t *testing.T) {
			r := developmentFixture()
			r.Concerns = r.Concerns[:1]
			switch kind {
			case "emergency":
				r.Assessments = []RoundsAssessment{{ID: ActiveCombat, Priority: 0, Finding: domain.FindingUnmet}}
			case "unknown":
				r.Concerns[0].Deficit = domain.Unknown[float64]()
			case "blocked":
				r.Concerns[0].Blocked = true
			}
			s := rank(t, r)
			requireSelected(t, s)
			if s.Rows[0].Reason == "" {
				t.Fatal(s)
			}
		})
	}
}

// A startup goal no longer holds comfort back: a comfort goal with work acts.
func TestDevelopmentComfortActsBesideUnservedStartupGoals(t *testing.T) {
	r := developmentFixture()
	r.Concerns = r.Concerns[:1]
	r.Concerns[0].Comfort = true
	r.Concerns = append(r.Concerns, DevelopmentConcern{ID: "cooking", Priority: 2})
	s := rank(t, r)
	for _, row := range s.Rows {
		if row.Concern == "storage" && !row.Selected {
			t.Fatal("comfort held behind an unserved startup goal", s.Rows)
		}
	}
}

func TestDevelopmentAgeAndReset(t *testing.T) {
	r := developmentFixture()
	s := rank(t, r)
	requireSelected(t, s, "storage", "defense", "wood")
	for range 20 {
		r.Previous = s
		s = rank(t, r)
	}
	for _, row := range s.Rows {
		if row.WaitingSince != 100 {
			t.Fatal(row)
		}
	}
	r.Tick = 600000
	r.Previous = s
	// Committed work resets only its own waiting age.
	r.Commitments = []Commitment{{Concern: "wood", Priority: 3, Progress: developmentProgress(t)}}
	s = rank(t, r)
	requireSelected(t, s, "storage", "defense")
	r.Previous = s
	r.Commitments = nil
	requireSelected(t, rank(t, r), "wood", "storage", "defense")
	for _, change := range []string{"load", "map", "rewind"} {
		t.Run(change, func(t *testing.T) {
			q := r
			switch change {
			case "load":
				q.Snapshot.Load = "other"
			case "map":
				q.Snapshot.Map++
			case "rewind":
				q.Tick = 50
			}
			for _, row := range rank(t, q).Rows {
				if row.WaitingSince != q.Tick {
					t.Fatal(row)
				}
			}
		})
	}
}
func developmentProgress(t *testing.T) domain.Progress {
	t.Helper()
	// An observed (non-intent) kind: commitments follow per-attempt effects.
	cut, e := domain.NewAcquisition("plant", "WoodLog", domain.Cell{X: 1, Z: 2})
	if e != nil {
		t.Fatal(e)
	}
	a, e := domain.NewAcquisitionAction("action", cut)
	if e != nil {
		t.Fatal(e)
	}
	p, e := domain.NewPlan("plan", 0, []domain.Action{a})
	if e != nil {
		t.Fatal(e)
	}
	v, e := domain.NewProgress(p, a.ID())
	if e != nil {
		t.Fatal(e)
	}
	return v
}
func TestDevelopmentSharedProgressCommitments(t *testing.T) {
	r := developmentFixture()
	pending := developmentProgress(t)
	prepared, e := pending.Prepare(r.Snapshot, r.Tick)
	if e != nil {
		t.Fatal(e)
	}
	dispatched, e := prepared.MarkDispatched(r.Snapshot, r.Tick)
	if e != nil {
		t.Fatal(e)
	}
	cancelled, e := dispatched.Cancel()
	if e != nil {
		t.Fatal(e)
	}
	for _, p := range []domain.Progress{pending, prepared, dispatched, cancelled} {
		r.Commitments = []Commitment{{Concern: "player-room", Priority: 3, Progress: p}}
		s := rank(t, r)
		requireSelected(t, s, "storage", "defense", "wood")
		if !reflect.DeepEqual(s.Committed, []ConcernID{"player-room"}) {
			t.Fatal(s)
		}
	}
}
func TestDevelopmentRejectsInvalidInputs(t *testing.T) {
	for _, mutate := range []func(*DevelopmentRequest){
		func(r *DevelopmentRequest) { r.Tick = -1 }, func(r *DevelopmentRequest) { r.Workers = domain.Known(-1) },
		func(r *DevelopmentRequest) { r.Concerns[0].Deficit = domain.Known(math.NaN()) },
		func(r *DevelopmentRequest) { r.Concerns[0].Deficit = domain.Known(math.Inf(1)) },
		func(r *DevelopmentRequest) { r.Concerns[0].Deficit = domain.Known(1.1) },
		func(r *DevelopmentRequest) { r.Concerns = append(r.Concerns, r.Concerns[0]) },
	} {
		r := developmentFixture()
		mutate(&r)
		if _, err := RankDevelopment(r); err == nil {
			t.Fatal("accepted invalid request")
		}
	}
}

// The idle tier must be a total order: rows carrying a reason compare by
// score against both idle and non-idle eligible rows, so a score-only tier
// check is cyclic and sort.Slice may leave an idle goal ahead (colony-4
// selected idle EnsureDefensiveLayout over MaintainFlooring for a game day).
func TestIdleTierOrderIsTotal(t *testing.T) {
	r := developmentFixture()
	r.Tick = 69054
	goal := func(id ConcernID, risk float64, blocked bool) DevelopmentConcern {
		g := DevelopmentConcern{ID: id, Priority: 3, Deficit: domain.Known(1.0), Blocked: blocked}
		if risk > 0 {
			g.Risk = domain.Known(risk)
		}
		return g
	}
	r.Concerns = []DevelopmentConcern{
		goal("EnsureDefensiveLayout", 0, false), goal("MaintainEquipment", 0, true), goal("EnsureComfort", 0.5, true),
		goal("MaintainFlooring", 0.5, false), goal("MaintainAnimalFeed", 0, false), goal("MaintainLighting", 0, false),
		goal("MaintainRoutes", 0, false), goal("MaintainHousing", 0.5, false),
		goal("MaintainStoneShell", 0.5, false), goal("MaintainIncineration", 0, true),
	}
	since := map[ConcernID]domain.Tick{"MaintainFlooring": 6430, "MaintainLighting": 36769, "MaintainRoutes": 36769, "MaintainStoneShell": 33189}
	previous := DevelopmentState{Snapshot: r.Snapshot, Tick: r.Tick - 500, Capacity: 1}
	for _, g := range r.Concerns {
		row := DevelopmentRow{Concern: g.ID, WaitingSince: 15, Idle: g.ID != "MaintainFlooring" && !g.Blocked}
		if v, ok := since[g.ID]; ok {
			row.WaitingSince = v
		}
		previous.Rows = append(previous.Rows, row)
	}
	r.Previous = previous
	// Every eligible goal acts; the non-idle one still ranks first.
	s := rank(t, r)
	for _, row := range s.Rows {
		if row.Selected {
			if row.Concern != "MaintainFlooring" {
				t.Fatalf("first selected row %s, want MaintainFlooring: %+v", row.Concern, s.Rows)
			}
			break
		}
	}
}

// A wake step runs only the planners it names, so a goal selected by that
// review may never have had its planner run: the next review must not count
// it idle (colony-5 idled EnsureBasicDefense before the equip planner ran).
func TestPartialPlannerPassDoesNotIdleSelection(t *testing.T) {
	r := developmentFixture()
	r.Partial = true
	s := rank(t, r)
	requireSelected(t, s, "storage", "defense", "wood")
	if !s.Partial {
		t.Fatal("partial pass not recorded", s)
	}
	r.Partial = false
	r.Previous, r.Tick = s, r.Tick+2500
	s = rank(t, r)
	requireSelected(t, s, "storage", "defense", "wood")
	if s.Rows[0].Idle || s.Rows[0].Score != 100+2.5+20 {
		t.Fatal("selection after a partial pass keeps its hysteresis", s.Rows)
	}
}

// Capacity is the worker count, so a stored review of a colony of more than
// eight colonists must validate: the planner stalled at the first review of a
// ten-colonist perimeter case.
func TestDevelopmentStateOfALargeColonyValidates(t *testing.T) {
	r := developmentFixture()
	r.Workers = domain.Known(36)
	s := rank(t, r)
	if s.Capacity != 36 {
		t.Fatalf("capacity %d, want the 36 workers", s.Capacity)
	}
	if err := ValidateDevelopmentState(s); err != nil {
		t.Fatalf("a 36-worker development state is invalid: %v", err)
	}
}
