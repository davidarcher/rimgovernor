package policy

import (
	"fmt"
	"math"
	"math/rand"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func current() domain.GenerationSnapshot {
	return domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "plan", Revision: 1}
}
func candidate(t *testing.T, id domain.ActionID, x int32, count int64) Candidate {
	t.Helper()
	b, err := domain.NewBuilding("ModdedWall", domain.Cell{X: x, Z: 1}, domain.North, "Steel")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction(id, b)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewProgress(plan, id)
	if err != nil {
		t.Fatal(err)
	}
	return Candidate{Action: a, Progress: p, Purpose: Routine, Preview: Preview{Action: a, Snapshot: current(), Tick: 20, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(true), Footprint: domain.Known([]domain.Cell{b.Cell()}), Costs: domain.Known([]Amount{{"Steel", count}})}}
}
func request(c ...Candidate) Request {
	return Request{Current: current(), CurrentTick: 20, Bounds: domain.Known(Bounds{100, 100}), Candidates: c, Stock: StockObservation{current(), 20, []Stock{{"Steel", domain.Known(int64(100))}}}}
}
func decide(t *testing.T, r Request) Decision {
	t.Helper()
	input, err := NewInput(r)
	if err != nil {
		t.Fatal(err)
	}
	return Admit(input)
}
func reason(t *testing.T, r Request, want Reason) {
	t.Helper()
	d := decide(t, r)
	if len(d.Admitted) != 0 || len(d.Refused) != 1 || d.Refused[0].Reason != want {
		t.Fatalf("want %s got %+v", want, d)
	}
}
func hold(c Candidate) Reservation {
	costs, _ := c.Preview.Costs.Value()
	cells, _ := c.Preview.Footprint.Value()
	return Reservation{c.Action, c.Progress, current(), costs, cells}
}
func issue(t *testing.T, c Candidate) Candidate {
	t.Helper()
	p, err := c.Progress.Prepare(current(), 10)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.MarkDispatched(current(), 10)
	if err != nil {
		t.Fatal(err)
	}
	c.Progress = p
	return c
}

func TestPriorityStableIdentityAndNoOvercommit(t *testing.T) {
	a, b, c := candidate(t, "a", 1, 60), candidate(t, "b", 2, 50), candidate(t, "c", 3, 40)
	b.Priority = 2
	r := request(c, a, b)
	d := decide(t, r)
	if len(d.Admitted) != 2 || d.Admitted[0].Action.ID() != "b" || d.Admitted[1].Action.ID() != "c" || len(d.Refused) != 1 || d.Refused[0].Action != "a" {
		t.Fatal(d)
	}
	a.Preview.Costs = domain.Known([]Amount{{"Steel", 50}})
	r = request(b, a)
	b.Priority = 0
	r.Candidates = []Candidate{b, a}
	d = decide(t, r)
	if d.Admitted[0].Action.ID() != "a" {
		t.Fatal("equal priority not stable ID ordered")
	}
}
func TestUnknownUnsafeAndStaleFactsRefuse(t *testing.T) {
	for name, change := range map[string]func(*Request){
		"stock":         func(r *Request) { r.Stock.Values[0].Available = domain.Unknown[int64]() },
		"missing stock": func(r *Request) { r.Stock.Values = nil },
		"cost":          func(r *Request) { r.Candidates[0].Preview.Costs = domain.Unknown[[]Amount]() },
		"footprint":     func(r *Request) { r.Candidates[0].Preview.Footprint = domain.Unknown[[]domain.Cell]() },
		"bounds":        func(r *Request) { r.Bounds = domain.Unknown[Bounds]() },
		"safe":          func(r *Request) { r.Candidates[0].Preview.SafeToPlace = domain.Unknown[bool]() },
		"material":      func(r *Request) { r.Candidates[0].Preview.MadeFromStuff = domain.Unknown[bool]() },
	} {
		t.Run(name, func(t *testing.T) { r := request(candidate(t, "a", 1, 10)); change(&r); reason(t, r, UnknownFacts) })
	}
	for _, change := range []func(*Request){func(r *Request) { r.Stock.Tick-- }, func(r *Request) { r.Candidates[0].Preview.Tick-- }, func(r *Request) { r.Candidates[0].Preview.Snapshot.Direction++ }, func(r *Request) { r.Stock.Snapshot.Load = "other" }, func(r *Request) { r.Candidates[0].Preview.Action = candidate(t, "other", 1, 10).Action }} {
		r := request(candidate(t, "a", 1, 10))
		change(&r)
		reason(t, r, StaleFacts)
	}
	r := request(candidate(t, "a", 1, 10))
	r.Candidates[0].Preview.SafeToPlace = domain.Known(false)
	reason(t, r, UnsafePlacement)
	r.Candidates[0].Preview.SafeToPlace = domain.Known(true)
	r.Candidates[0].Preview.CanPlace = domain.Known(false)
	reason(t, r, UnsafePlacement)
	c := candidate(t, "a", 1, 10)
	b, err := domain.NewBuilding("Wall", domain.Cell{X: 1, Z: 1}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction("a", b)
	if err != nil {
		t.Fatal(err)
	}
	c.Action = a
	c.Preview.Action = a
	reason(t, request(c), MaterialRequired)
}
func TestFootprintUsesAllNativeCells(t *testing.T) {
	a, b := candidate(t, "a", 1, 10), candidate(t, "b", 2, 10)
	a.Preview.Footprint = domain.Known([]domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 1}})
	d := decide(t, request(b, a))
	if len(d.Admitted) != 1 || d.Admitted[0].Action.ID() != "a" || d.Refused[0].Reason != GeometryBlocked {
		t.Fatal(d)
	}
	for _, cells := range [][]domain.Cell{nil, {{X: 2, Z: 1}}, {{X: 1, Z: 1}, {X: 1, Z: 1}}, {{X: 1, Z: 1}, {X: 100, Z: 1}}, {{X: 1, Z: 1}, {X: -1, Z: 1}}} {
		c := candidate(t, "c", 1, 10)
		c.Preview.Footprint = domain.Known(cells)
		reason(t, request(c), GeometryBlocked)
	}
}
func TestSpendingReservesAndDependencies(t *testing.T) {
	c := candidate(t, "a", 1, 10)
	r := request(c)
	r.Rules = []ResourceRule{{"Steel", 95, Allow}}
	reason(t, r, InsufficientStock)
	r.Rules = []ResourceRule{{"Steel", 0, Stop}}
	reason(t, r, SpendingBlocked)
	r.Rules = []ResourceRule{{"Steel", 0, DefenseOnly}}
	reason(t, r, SpendingBlocked)
	r.Candidates[0].Purpose = Defense
	if len(decide(t, r).Admitted) != 1 {
		t.Fatal("defense spending blocked")
	}
	r = request(c)
	r.Candidates[0].Dependencies = []Dependency{{Action: "previous", Snapshot: current(), Tick: 20}}
	reason(t, r, DependencyBlocked)
	r.Candidates[0].Dependencies[0].Completed = domain.Known(false)
	reason(t, r, DependencyBlocked)
	r.Candidates[0].Dependencies[0].Completed = domain.Known(true)
	r.Candidates[0].Dependencies[0].Tick--
	reason(t, r, DependencyBlocked)
	r.Candidates[0].Dependencies[0].Tick = 20
	if len(decide(t, r).Admitted) != 1 {
		t.Fatal("observed dependency blocked")
	}
	issued := issue(t, c)
	reason(t, request(issued), NotReady)
}
func TestCancellationRetainsUncertainReservation(t *testing.T) {
	ctx := current()
	old := issue(t, candidate(t, "old", 1, 100))
	p, err := old.Progress.Cancel()
	if err != nil {
		t.Fatal(err)
	}
	old.Progress = p
	r := request(candidate(t, "new", 2, 1))
	r.Held = []Reservation{hold(old)}
	reason(t, r, InsufficientStock)
	old.Progress, err = old.Progress.Observe(domain.Observation{Action: "old", Attempt: 1, Snapshot: ctx, Tick: 11, Effect: domain.EffectUnknown}, ctx)
	if err != nil {
		t.Fatal(err)
	}
	r.Held = []Reservation{hold(old)}
	reason(t, r, InsufficientStock)
	old.Progress, err = old.Progress.Observe(domain.Observation{Action: "old", Attempt: 1, Snapshot: ctx, Tick: 12, Effect: domain.EffectAbsent}, ctx)
	if err != nil {
		t.Fatal(err)
	}
	r.Held = []Reservation{hold(old)}
	d := decide(t, r)
	if len(d.Admitted) != 1 || len(d.Held) != 0 {
		t.Fatal("resolved absent cancellation retained", d)
	}
	neverIssued := candidate(t, "never", 2, 100)
	neverIssued.Progress, err = neverIssued.Progress.Cancel()
	if err != nil {
		t.Fatal(err)
	}
	r.Held = []Reservation{hold(neverIssued)}
	if len(decide(t, r).Admitted) != 1 {
		t.Fatal("unissued cancellation not released")
	}
}
func TestCompletedBudgetReleaseKeepsGeometry(t *testing.T) {
	old := issue(t, candidate(t, "old", 1, 100))
	p, err := old.Progress.Observe(domain.Observation{Action: "old", Attempt: 1, Snapshot: current(), Tick: 15, Effect: domain.EffectCompleted}, current())
	if err != nil {
		t.Fatal(err)
	}
	old.Progress = p
	r := request(candidate(t, "new", 2, 100))
	r.Held = []Reservation{hold(old)}
	d := decide(t, r)
	if len(d.Admitted) != 1 || len(d.Held) != 1 || d.Held[0].Costs[0].Count != 100 {
		t.Fatal("completed budget not released or evidence lost", d)
	}
	r.Candidates = []Candidate{candidate(t, "new", 1, 1)}
	reason(t, r, GeometryBlocked)
	r.Candidates = []Candidate{candidate(t, "new", 2, 1)}
	r.Stock.Tick = 14
	reason(t, r, StaleFacts)
	r.Stock.Tick = 20
	r.Current.Load = "other"
	r.Stock.Snapshot = r.Current
	r.Candidates[0].Preview.Snapshot = r.Current
	reason(t, r, InvalidHeld)
}

func TestHeldOrderingAndOtherPlanReservations(t *testing.T) {
	r := request(candidate(t, "new", 1, 10))
	held := hold(candidate(t, "held", 2, 95))
	other, err := domain.NewPlan("other", 1, []domain.Action{held.Action})
	if err != nil {
		t.Fatal(err)
	}
	held.Progress, err = domain.NewProgress(other, held.Action.ID())
	if err != nil {
		t.Fatal(err)
	}
	held.Snapshot.Plan = "other"
	r.Held = []Reservation{held}
	reason(t, r, InsufficientStock)
	large := hold(candidate(t, "large", 3, math.MaxInt64))
	invalid := hold(candidate(t, "invalid", 4, 1))
	invalid.Footprint = nil
	r.Held = []Reservation{held, large, invalid}
	first := decide(t, r)
	r.Held = []Reservation{invalid, large, held}
	if !reflect.DeepEqual(first, decide(t, r)) {
		t.Fatal("held ordering changed refusal")
	}
}

func TestOnlyPendingIntentIsAdmissible(t *testing.T) {
	c := candidate(t, "a", 1, 10)
	p, err := c.Progress.Prepare(current(), 10)
	if err != nil {
		t.Fatal(err)
	}
	c.Progress = p
	reason(t, request(c), NotReady)
	c.Progress, err = c.Progress.Cancel()
	if err != nil {
		t.Fatal(err)
	}
	reason(t, request(c), NotReady)
	c = issue(t, candidate(t, "a", 1, 10))
	c.Progress, err = c.Progress.Observe(domain.Observation{Action: "a", Attempt: 1, Snapshot: current(), Tick: 11, Effect: domain.EffectCompleted}, current())
	if err != nil {
		t.Fatal(err)
	}
	reason(t, request(c), NotReady)
}
func TestValidationCopiesAndOverflow(t *testing.T) {
	r := request(candidate(t, "a", 1, 10))
	input, err := NewInput(r)
	if err != nil {
		t.Fatal(err)
	}
	costs, _ := r.Candidates[0].Preview.Costs.Value()
	costs[0].Count = 999
	r.Stock.Values[0].Available = domain.Known(int64(0))
	r.Candidates[0].Dependencies = append(r.Candidates[0].Dependencies, Dependency{Action: "a"})
	d := Admit(input)
	if len(d.Admitted) != 1 {
		t.Fatal("caller mutation altered input")
	}
	d.Admitted[0].Costs[0].Count = 999
	if !reflect.DeepEqual(Admit(input).Admitted[0].Costs, []Amount{{"Steel", 10}}) {
		t.Fatal("result mutation altered input")
	}
	r = request(candidate(t, "a", 1, math.MaxInt64))
	r.Stock.Values[0].Available = domain.Known(int64(math.MaxInt64))
	r.Rules = []ResourceRule{{"Steel", 1, Allow}}
	reason(t, r, ArithmeticOverflow)
	r.Rules = nil
	r.Held = []Reservation{hold(candidate(t, "held", 2, 1))}
	reason(t, r, ArithmeticOverflow)
	for _, change := range []func(*Request){func(r *Request) { r.Candidates = append(r.Candidates, r.Candidates[0]) }, func(r *Request) { r.Candidates[0].Dependencies = []Dependency{{Action: "a"}} }, func(r *Request) { r.Candidates[0].Dependencies = []Dependency{{Action: "b"}, {Action: "b"}} }, func(r *Request) { r.Stock.Values = append(r.Stock.Values, r.Stock.Values[0]) }, func(r *Request) { r.Candidates[0].Preview.Costs = domain.Known([]Amount{{"Steel", -1}}) }, func(r *Request) { r.Rules = []ResourceRule{{"Steel", -1, Allow}} }} {
		r := request(candidate(t, "a", 1, 10))
		change(&r)
		if _, err = NewInput(r); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
}
func TestAdmissionConservationAndPermutation(t *testing.T) {
	random := rand.New(rand.NewSource(41))
	for trial := 0; trial < 100; trial++ {
		var candidates []Candidate
		for i := 0; i < 20; i++ {
			c := candidate(t, domain.ActionID(fmt.Sprintf("a%02d", i)), int32(i), int64(random.Intn(30)))
			c.Priority = int32(random.Intn(4))
			candidates = append(candidates, c)
		}
		r := request(candidates...)
		reserve := int64(random.Intn(30))
		r.Rules = []ResourceRule{{"Steel", reserve, Allow}}
		baseline := decide(t, r)
		sum := reserve
		for _, a := range baseline.Admitted {
			sum += a.Costs[0].Count
		}
		if sum > 100 {
			t.Fatal("overcommit", sum)
		}
		random.Shuffle(len(r.Candidates), func(i, j int) { r.Candidates[i], r.Candidates[j] = r.Candidates[j], r.Candidates[i] })
		if !reflect.DeepEqual(baseline, decide(t, r)) {
			t.Fatal("input order changed admission")
		}
	}
}
