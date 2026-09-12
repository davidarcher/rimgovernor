package policy

import (
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func developmentFixture() DevelopmentRequest {
	return DevelopmentRequest{
		Snapshot: domain.GenerationSnapshot{Colony: "colony", Map: 1, Load: "load", Plan: "plan", Direction: 1}, Tick: 100,
		Workers: domain.Known(3), Limit: 1,
		Goals: []DevelopmentGoal{
			{ID: "storage", Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(1.0)},
			{ID: "defense", Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(.5)},
			{ID: "wood", Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(50.0 / 350)},
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
func selected(s DevelopmentState) []GoalID {
	var ids []GoalID
	for _, r := range s.Rows {
		if r.Selected {
			ids = append(ids, r.Goal)
		}
	}
	return ids
}
func requireSelected(t *testing.T, s DevelopmentState, ids ...GoalID) {
	t.Helper()
	if !reflect.DeepEqual(selected(s), ids) {
		t.Fatalf("selected %v, want %v: %+v", selected(s), ids, s.Rows)
	}
}
func TestDevelopmentCapacityAndYield(t *testing.T) {
	r := developmentFixture()
	s := rank(t, r)
	requireSelected(t, s, "storage")
	next := YieldDevelopment(s, "storage")
	requireSelected(t, next, "defense")
	requireSelected(t, s, "storage")
	next = YieldDevelopment(next, "defense")
	requireSelected(t, next, "wood")
	r.Workers = domain.Known(1)
	r.Limit = 8
	requireSelected(t, rank(t, r), "storage")
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
	r.Goals[0].MethodUnavailable = true
	s := rank(t, r)
	requireSelected(t, s, "defense")
	if s.Rows[0].Reason != DevelopmentMethodUnavailable || s.Rows[0].Deficit != domain.Known(1.0) {
		t.Fatal(s)
	}
	r.Previous = s
	r.Tick += 25000
	s = rank(t, r)
	requireSelected(t, s, "defense")
	r.Goals[0].MethodUnavailable = false
	requireSelected(t, rank(t, r), "storage")
}
func TestDevelopmentHolds(t *testing.T) {
	for _, kind := range []string{"emergency", "unknown", "cancelled", "adviser", "blocked", "comfort"} {
		t.Run(kind, func(t *testing.T) {
			r := developmentFixture()
			r.Goals = r.Goals[:1]
			switch kind {
			case "emergency":
				r.Goals = append(r.Goals, DevelopmentGoal{ID: "combat", Source: AutopilotGoal, Priority: 0})
			case "unknown":
				r.Goals[0].Deficit = domain.Unknown[float64]()
			case "cancelled":
				r.Goals[0].Cancelled = true
			case "adviser":
				r.Goals[0].Source = AdviserGoal
			case "blocked":
				r.Goals[0].Blocked = true
			case "comfort":
				r.Goals[0].Comfort = true
				r.Goals = append(r.Goals, DevelopmentGoal{ID: "food", Source: AutopilotGoal, Priority: 2})
			}
			s := rank(t, r)
			requireSelected(t, s)
			if s.Rows[0].Reason == "" {
				t.Fatal(s)
			}
		})
	}
}
func TestDevelopmentPlayerPreferenceAgeAndReset(t *testing.T) {
	r := developmentFixture()
	r.Goals[2].Source = PlayerGoal
	s := rank(t, r)
	requireSelected(t, s, "wood")
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
	r.Commitments = []Commitment{{Goal: "wood", Source: PlayerGoal, Priority: 3, Progress: developmentProgress(t)}}
	s = rank(t, r)
	requireSelected(t, s)
	r.Previous = s
	r.Commitments = nil
	requireSelected(t, rank(t, r), "storage")
	for _, change := range []string{"direction", "load", "map", "rewind"} {
		t.Run(change, func(t *testing.T) {
			q := r
			switch change {
			case "direction":
				q.Snapshot.Direction++
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
	b, e := domain.NewBuilding("Wall", domain.Cell{X: 1, Z: 1}, domain.North, "WoodLog")
	if e != nil {
		t.Fatal(e)
	}
	a, e := domain.NewBuildingAction("action", b)
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
		r.Commitments = []Commitment{{Goal: "player-room", Source: PlayerGoal, Priority: 2, Progress: p}}
		s := rank(t, r)
		requireSelected(t, s)
		if !reflect.DeepEqual(s.Committed, []GoalID{"player-room"}) {
			t.Fatal(s)
		}
	}
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectAbsent} {
		observed, err := cancelled.Observe(domain.Observation{Action: "action", Attempt: 1, Snapshot: r.Snapshot, Tick: r.Tick + 1, Effect: effect}, r.Snapshot)
		if err != nil {
			t.Fatal(err)
		}
		r.Commitments = []Commitment{{Goal: "player-room", Source: PlayerGoal, Priority: 2, Progress: observed}}
		requireSelected(t, rank(t, r), "storage")
	}
}
func TestDevelopmentRejectsInvalidInputs(t *testing.T) {
	for _, mutate := range []func(*DevelopmentRequest){
		func(r *DevelopmentRequest) { r.Limit = 0 }, func(r *DevelopmentRequest) { r.Limit = 9 },
		func(r *DevelopmentRequest) { r.Tick = -1 }, func(r *DevelopmentRequest) { r.Workers = domain.Known(-1) },
		func(r *DevelopmentRequest) { r.Goals[0].Deficit = domain.Known(math.NaN()) },
		func(r *DevelopmentRequest) { r.Goals[0].Deficit = domain.Known(math.Inf(1)) },
		func(r *DevelopmentRequest) { r.Goals[0].Deficit = domain.Known(1.1) },
		func(r *DevelopmentRequest) { r.Goals = append(r.Goals, r.Goals[0]) },
		func(r *DevelopmentRequest) { r.Goals[0].Source = "invalid" },
	} {
		r := developmentFixture()
		mutate(&r)
		if _, err := RankDevelopment(r); err == nil {
			t.Fatal("accepted invalid request")
		}
	}
}
