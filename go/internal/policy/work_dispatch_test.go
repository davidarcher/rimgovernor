package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// dispatchOrder is a stock target of Steel on the machining table: material
// class, so a deficit refills over three days.
func dispatchOrder(target int32) OrderSpec {
	return OrderSpec{Recipe: "Make_Steel", Mode: domain.StockTarget, Target: target, BenchKind: "TableMachining", Product: "Steel", Class: ResourceMaterial}
}

// dispatchBench is a usable machining table whose recipe takes 1000 work per
// unit and needs one Ore; a worker of 8 hours at speed 1
// makes 20 units a day on a bench of speed 1.
func dispatchBench(id string, speed float64, bills ...GearBill) GearBench {
	recipe := GearRecipe{
		Definition: "Make_Steel", AvailableOn: domain.Known(true), WorkAmount: domain.Known(1000.0),
		Ingredients:  domain.Known([][]Amount{{{Resource: "Ore", Count: 1}}}),
		RequiredWork: domain.Known([]WorkRequirement{{Work: "Smithing"}}),
	}
	return GearBench{ID: id, Def: "TableMachining", Usable: domain.Known(true), WorkSpeed: domain.Known(speed), Bills: domain.Known(bills), Recipes: domain.Known([]GearRecipe{recipe})}
}

func dispatchBill(id string, order OrderSpec) GearBill {
	return GearBill{ID: id, Recipe: order.Recipe, Active: domain.Known(true), Spec: domain.Known(order)}
}

func dispatchPawn(id string, job string) WorkPawn {
	hours := make([]string, 24)
	for i := range hours {
		hours[i] = ScheduleSleep
		if i < 8 {
			hours[i] = ScheduleWork
		}
	}
	p := WorkPawn{
		ID: PawnID(id), Available: domain.Known(true), Schedule: domain.Known(hours),
		Work: domain.Known([]WorkPriority{{Work: "Smithing", Priority: 2}}),
		Job:  domain.Unknown[PawnJob](),
	}
	if job != "" {
		p.Job = domain.Known(PawnJob{Def: "DoBill", Target: domain.Known(JobTarget{Thing: job})})
	}
	return p
}

func dispatchStock(n int64) domain.Fact[map[Resource]int64] {
	return domain.Known(map[Resource]int64{"Steel": n, "Ore": 50})
}

func threeBenches() []GearBench {
	return []GearBench{dispatchBench("b1", 1), dispatchBench("b2", 1), dispatchBench("b3", 1)}
}

func TestDispatchOrderSplitsAcrossBenches(t *testing.T) {
	pawns := []WorkPawn{dispatchPawn("p1", ""), dispatchPawn("p2", ""), dispatchPawn("p3", ""), dispatchPawn("p4", "")}
	tests := []struct {
		name     string
		target   int32
		stock    int64
		widen    int
		benches  []GearBench
		copies   int
		noBench  bool
		capacity float64
	}{
		{name: "one bench suffices", target: 30, copies: 1, capacity: 20},
		{name: "three are wanted", target: 150, copies: 3, capacity: 20},
		{name: "met target needs the one standing bill", target: 30, stock: 30, copies: 1, capacity: 20},
		{name: "widening adds a bench", target: 30, widen: 1, copies: 2, capacity: 20},
		{name: "widening is capped at the usable benches", target: 30, widen: 9, copies: 3, capacity: 20},
		{name: "more wanted than exist reports the shortfall", target: 300, copies: 3, noBench: true, capacity: 20},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			benches := tc.benches
			if benches == nil {
				benches = threeBenches()
			}
			d := DispatchOrder(dispatchOrder(tc.target), dispatchStock(tc.stock), benches, pawns, tc.widen)
			if c, _ := d.Capacity.Value(); math.Abs(c-tc.capacity) > 1e-9 {
				t.Fatalf("capacity = %v", d.Capacity)
			}
			if d.Copies != tc.copies || (d.NoBenchShort > 0) != tc.noBench {
				t.Fatalf("copies = %d short = %v wanted = %v", d.Copies, d.NoBenchShort, d.Wanted)
			}
		})
	}
	// 300 units over three days is 100 a day; three benches make 60.
	d := DispatchOrder(dispatchOrder(300), dispatchStock(0), threeBenches(), pawns, 0)
	if math.Abs(d.NoBenchShort-40) > 1e-9 {
		t.Fatalf("short = %v", d.NoBenchShort)
	}
}

func TestDispatchOrderUnknownsWantOneBench(t *testing.T) {
	pawns := []WorkPawn{dispatchPawn("p1", "")}
	unread := dispatchPawn("p2", "")
	unread.Schedule = domain.Unknown[[]string]()
	for name, d := range map[string]OrderDispatch{
		"stock":     DispatchOrder(dispatchOrder(300), domain.Unknown[map[Resource]int64](), threeBenches(), pawns, 0),
		"schedule":  DispatchOrder(dispatchOrder(300), dispatchStock(0), threeBenches(), append(pawns, unread), 0),
		"no worker": DispatchOrder(dispatchOrder(300), dispatchStock(0), threeBenches(), nil, 0),
		"no table":  DispatchOrder(dispatchOrder(300), dispatchStock(0), nil, pawns, 3),
		"not stock": DispatchOrder(OrderSpec{Recipe: "Make_Steel", Mode: domain.GearBatch, Target: 5, BenchKind: "TableMachining"}, dispatchStock(0), threeBenches(), pawns, 0),
	} {
		if d.Copies != 1 || d.NoBenchShort != 0 {
			t.Errorf("%s: copies = %d short = %v", name, d.Copies, d.NoBenchShort)
		}
	}
}

func TestDispatchWorkersFilterByRecipeWork(t *testing.T) {
	cook := dispatchPawn("cook", "")
	cook.Work = domain.Known([]WorkPriority{{Work: "Cooking", Priority: 1}})
	off := dispatchPawn("off", "")
	off.Work = domain.Known([]WorkPriority{{Work: "Smithing", Priority: 0}})
	away := dispatchPawn("away", "")
	away.Available = domain.Known(false)
	pawns := []WorkPawn{dispatchPawn("smith", ""), cook, off, away}
	if got := DispatchWorkers(pawns, []WorkRequirement{{Work: "Smithing"}}); len(got) != 1 {
		t.Fatalf("smithing workers = %d", len(got))
	}
	if got := DispatchWorkers(pawns, nil); len(got) != 3 {
		t.Fatalf("workers without a work type = %d", len(got))
	}
}

// observation is one Round's reading of a stock target carried by n benches.
func observation(tick, stock int64, carrying int) ThroughputObservation {
	return ThroughputObservation{
		Tick: tick, Stock: domain.Known(stock), Target: 100, Carrying: carrying, Eligible: 3,
		Capacity: domain.Known(20.0), Active: true, Busy: true, Ingredients: domain.Known(true),
	}
}

func TestCalibrateThroughput(t *testing.T) {
	const day = ThroughputWindowTicks
	// One bench predicts 20 a day; the deficit of 100 does not cap it.
	run := func(mut func(*ThroughputObservation), endStock int64) Calibration {
		w, cal := CalibrateThroughput(ThroughputWindow{}, observation(0, 0, 1))
		if cal.Closed {
			t.Fatal("a window closed on its first Round")
		}
		mid := observation(day/2, endStock/2, 1)
		last := observation(day, endStock, 1)
		if mut != nil {
			mut(&mid)
			mut(&last)
		}
		if w, cal = CalibrateThroughput(w, mid); cal.Closed {
			t.Fatal("a half-day window gave a verdict")
		}
		_, cal = CalibrateThroughput(w, last)
		return cal
	}
	tests := []struct {
		name   string
		mut    func(*ThroughputObservation)
		stock  int64
		closed bool
		short  bool
		widen  bool
		reason UnmetReason
	}{
		{name: "on prediction", stock: 20, closed: true},
		{name: "exactly half is not far below", stock: 10, closed: true},
		{name: "under half widens", stock: 9, closed: true, short: true, widen: true},
		{name: "ingredient-bound", mut: func(o *ThroughputObservation) { o.Ingredients = domain.Known(false) }, stock: 0, closed: true, short: true, reason: UnmetIngredients},
		{name: "haul-bound", mut: func(o *ThroughputObservation) { o.Busy = false }, stock: 0, closed: true, short: true, reason: UnmetHaul},
		{name: "idle but ingredients unread is not haul-bound", mut: func(o *ThroughputObservation) { o.Busy = false; o.Ingredients = domain.Unknown[bool]() }, stock: 0, closed: true, short: true, widen: true},
		{name: "idle with the bill inactive is not haul-bound", mut: func(o *ThroughputObservation) { o.Busy = false; o.Active = false }, stock: 0, closed: true, short: true, widen: true},
		{name: "every bench already carries it", mut: func(o *ThroughputObservation) { o.Eligible = 1 }, stock: 0, closed: true, short: true, reason: UnmetBenchesExhausted},
		{name: "target reached mid-window is not a shortfall", mut: func(o *ThroughputObservation) {
			if o.Tick < day {
				o.Stock = domain.Known(int64(100))
			}
		}, stock: 100, closed: true},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			cal := run(tc.mut, tc.stock)
			if cal.Closed != tc.closed || cal.Short != tc.short || cal.Widen != tc.widen || cal.Reason != tc.reason {
				t.Fatalf("calibration = %+v", cal)
			}
			if cal.Short && cal.ShortPerDay <= 0 {
				t.Fatalf("short by %v", cal.ShortPerDay)
			}
		})
	}
}

func TestCalibrateThroughputRestartsOnCarryingChangeAndUnknowns(t *testing.T) {
	w, _ := CalibrateThroughput(ThroughputWindow{}, observation(0, 0, 1))
	w, cal := CalibrateThroughput(w, observation(ThroughputWindowTicks-1, 0, 2))
	if cal.Closed || w.Start != ThroughputWindowTicks-1 || w.Carrying != 2 {
		t.Fatalf("window = %+v cal = %+v", w, cal)
	}
	unknown := observation(ThroughputWindowTicks, 0, 2)
	unknown.Stock = domain.Unknown[int64]()
	if w, cal = CalibrateThroughput(w, unknown); w.Open || cal.Closed {
		t.Fatalf("an unread stock kept the window: %+v", w)
	}
	if w, _ = CalibrateThroughput(w, observation(0, 0, 0)); w.Open {
		t.Fatal("a window opened with no bill carrying")
	}
}

// The memory widens after a short day, never past the usable benches, and
// forgets an order nobody declares.
func TestDispatchMemoryWidensThenDrops(t *testing.T) {
	// The model wants one bench (10 a day against 20); a pawn works b1 all day
	// and the stock does not move, so the day is a shortfall that is not hauling.
	small := dispatchOrder(30)
	pawns := []WorkPawn{dispatchPawn("p1", "b1"), dispatchPawn("p2", ""), dispatchPawn("p3", "")}
	benches := []GearBench{dispatchBench("b1", 1, dispatchBill("x", small)), dispatchBench("b2", 1), dispatchBench("b3", 1)}
	var m DispatchMemory
	in := DispatchInputs{Declared: []Declared{{Orders: []OrderSpec{small}}}, Benches: benches, Pawns: pawns, Stock: dispatchStock(0)}
	copies, _ := m.Dispatch(in)
	if copies[small.Key()] != 1 {
		t.Fatalf("copies = %v", copies)
	}
	in.Tick = ThroughputWindowTicks
	if copies, _ = m.Dispatch(in); copies[small.Key()] != 1 {
		t.Fatalf("copies at the window end = %v", copies)
	}
	copies, _ = m.Dispatch(in)
	if copies[small.Key()] != 2 || m.Widen[small.Key()] != 1 {
		t.Fatalf("a short day did not widen: copies = %v widen = %v", copies, m.Widen)
	}
	in.Declared = []Declared{{Orders: nil}}
	m.Dispatch(in)
	if len(m.Windows) != 0 || len(m.Widen) != 0 {
		t.Fatalf("memory kept an undeclared order: %+v", m)
	}
}

func TestDispatchMemoryUnmetHaulBound(t *testing.T) {
	order := dispatchOrder(30)
	pawns := []WorkPawn{dispatchPawn("p1", "")}
	benches := []GearBench{dispatchBench("b1", 1, dispatchBill("x", order))}
	var m DispatchMemory
	in := DispatchInputs{Declared: []Declared{{Orders: []OrderSpec{order}}}, Benches: benches, Pawns: pawns, Stock: dispatchStock(0)}
	m.Dispatch(in)
	in.Tick = ThroughputWindowTicks
	m.Dispatch(in)
	unmet := m.Unmet(nil, nil, nil, benches)
	if len(unmet) != 1 || unmet[0].BenchKind != "TableMachining" || unmet[0].Reason != UnmetHaul || unmet[0].ShortPerDay <= 0 {
		t.Fatalf("unmet = %+v", unmet)
	}
	// The next full day on target clears the shortfall.
	in.Tick = 2 * ThroughputWindowTicks
	in.Stock = dispatchStock(30)
	m.Dispatch(in)
	if unmet = m.Unmet(nil, nil, nil, benches); len(unmet) != 0 {
		t.Fatalf("unmet = %+v", unmet)
	}
}

func TestUnmetReasons(t *testing.T) {
	order := dispatchOrder(300)
	pawns := []WorkPawn{dispatchPawn("p1", ""), dispatchPawn("p2", ""), dispatchPawn("p3", "")}
	var m DispatchMemory
	// Three benches cannot make 100 a day: a further bench would help.
	in := DispatchInputs{Declared: []Declared{{Orders: []OrderSpec{order}}}, Benches: threeBenches(), Pawns: pawns, Stock: dispatchStock(0)}
	_, dispatch := m.Dispatch(in)
	wanted, _ := WantedOrders(in.Declared)
	unmet := m.Unmet(dispatch, wanted, nil, in.Benches)
	if len(unmet) != 1 || unmet[0].Reason != UnmetNoBench || math.Abs(unmet[0].ShortPerDay-40) > 1e-9 {
		t.Fatalf("unmet = %+v", unmet)
	}
	// Every capable bench at the cap: slots full, one bench's capacity lost.
	full := dispatchBench("b1", 1)
	bills := make([]GearBill, LedgerBenchSlots)
	for i := range bills {
		bills[i] = dispatchBill("f", ledgerSpec("other"))
	}
	full.Bills = domain.Known(bills)
	if got := UnplacedUnmet([]OrderSpec{order}, []GearBench{full}, dispatch); len(got) != 1 || got[0].Reason != UnmetSlotsFull || math.Abs(got[0].ShortPerDay-20) > 1e-9 {
		t.Fatalf("unplaced unmet = %+v", got)
	}
	// No capable bench at all: the whole demand is unmet.
	if got := UnplacedUnmet([]OrderSpec{order}, nil, dispatch); len(got) != 1 || got[0].Reason != UnmetNoBench || math.Abs(got[0].ShortPerDay-100) > 1e-9 {
		t.Fatalf("unplaced unmet = %+v", got)
	}
}

func TestMergeUnmetSumsPerKind(t *testing.T) {
	got := MergeUnmet([]UnmetThroughput{
		{BenchKind: "B", ShortPerDay: 2, Reason: UnmetHaul},
		{BenchKind: "A", ShortPerDay: 1, Reason: UnmetNoBench},
		{BenchKind: "B", ShortPerDay: 5, Reason: UnmetIngredients},
	})
	if len(got) != 2 || got[0].BenchKind != "A" || got[1].ShortPerDay != 7 || got[1].Reason != UnmetIngredients {
		t.Fatalf("merged = %+v", got)
	}
}

func TestPlaceLedgerOrdersPrefersFastBenchAndSkipsCarriers(t *testing.T) {
	order := dispatchOrder(30)
	fast, slow := dispatchBench("b2", 1.5), dispatchBench("b1", 1)
	placed, _ := PlaceLedgerOrders([]OrderSpec{order}, []GearBench{slow, fast})
	if len(placed) != 1 || placed[0].Bench != "b2" {
		t.Fatalf("placed = %+v", placed)
	}
	// The fast bench already carries the order: the copy goes to the other.
	fast = dispatchBench("b2", 1.5, dispatchBill("x", order))
	if placed, _ = PlaceLedgerOrders([]OrderSpec{order}, []GearBench{slow, fast}); len(placed) != 1 || placed[0].Bench != "b1" {
		t.Fatalf("placed = %+v", placed)
	}
	// Two copies in one plan take both benches, and a third has none.
	placed, unplaced := PlaceLedgerOrders([]OrderSpec{order, order, order}, []GearBench{slow, dispatchBench("b2", 1.5)})
	if len(placed) != 2 || len(unplaced) != 1 || placed[0].Bench != "b2" || placed[1].Bench != "b1" {
		t.Fatalf("placed = %+v unplaced = %+v", placed, unplaced)
	}
}

func TestReconcileLedgerCopies(t *testing.T) {
	order := dispatchOrder(30)
	bill := func(id string) ActualBill { return ActualBill{ID: id, Bench: id, Spec: order, Migrated: true} }
	want := []Declared{{Orders: []OrderSpec{order}}}
	copies := map[string]int{order.Key(): 3}
	plan := ReconcileLedger(want, []ActualBill{bill("1")}, nil, copies)
	if len(plan.Place) != 2 || len(plan.Keep) != 1 {
		t.Fatalf("plan = %+v", plan)
	}
	plan = ReconcileLedger(want, []ActualBill{bill("1"), bill("2"), bill("3")}, nil, copies)
	if len(plan.Place) != 0 || len(plan.Keep) != 3 {
		t.Fatalf("plan = %+v", plan)
	}
	// Shrinking the copy count orphans the extra bills like any duplicate.
	plan = ReconcileLedger(want, []ActualBill{bill("1"), bill("2"), bill("3")}, map[string]int{"2": OrphanGraceRounds - 1, "3": OrphanGraceRounds - 1}, map[string]int{order.Key(): 1})
	if len(plan.Remove) != 2 || len(plan.Keep) != 1 {
		t.Fatalf("plan = %+v", plan)
	}
	// Product and class are not identity.
	other := order
	other.Product, other.Class = "", ""
	if other.Key() != order.Key() {
		t.Fatal("the declarer's product context changed the spec identity")
	}
}
