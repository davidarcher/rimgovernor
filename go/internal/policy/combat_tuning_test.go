package policy

import (
	"fmt"
	"reflect"
	"sync"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// tuningOutcome is a scenario's last stop: orders, the geometry ask and the
// memory with its carried tuning cleared, so two tunings compare on what
// they decided, not on what they carry.
type tuningOutcome struct {
	Orders []CombatOrder
	Ask    *GeometryRequest
	Memory CombatMemory
}

// tuningScenario plays default-tuned stops up to a final stop, applying
// tune to the memory the final stop starts from.
type tuningScenario func(t *testing.T, tune func(CombatMemory) CombatMemory) tuningOutcome

func finalStop(view CombatView, geometry GeometryReply, m CombatMemory, resolve bool) tuningOutcome {
	orders, ask, next := DecideCombat(view, geometry, StopEvent{}, m)
	if resolve && ask != nil {
		orders, ask, next = DecideCombat(view, GeometryReply{Answered: true, Standable: ask.Cells}, StopEvent{}, m)
	}
	next.Tuning = nil
	return tuningOutcome{orders, ask, next}
}

func flankFinal(answered bool) tuningScenario {
	return func(t *testing.T, tune func(CombatMemory) CombatMemory) tuningOutcome {
		view := flankView(5)
		_, m := decideStop(t, view, StopEvent{}, CombatMemory{})
		view.Tick += 60
		geometry := GeometryReply{}
		if answered {
			p := flankProposals()
			geometry = GeometryReply{Answered: true, Role: RoleFiringCells, Proposals: p, Standable: p}
		}
		return finalStop(view, geometry, tune(m), false)
	}
}

func doorAfter(wolfZ ...int32) tuningScenario {
	return func(t *testing.T, tune func(CombatMemory) CombatMemory) tuningOutcome {
		var m CombatMemory
		for i, z := range wolfZ {
			view := doorView(z)
			view.Tick = domain.Tick(100 + 60*i)
			if i < len(wolfZ)-1 {
				_, m = decideStop(t, view, StopEvent{}, m)
				continue
			}
			return finalStop(view, GeometryReply{}, tune(m), true)
		}
		panic("no stops")
	}
}

func TestCombatTuningChangesOrders(t *testing.T) {
	cases := []struct {
		name     string
		scenario tuningScenario
		tune     func(*CombatTuning)
	}{
		{"DoorCloseRange", doorAfter(5, 17), func(c *CombatTuning) { c.DoorCloseRange = 1 }},
		{"DoorOpenRange", doorAfter(5, 17, 12), func(c *CombatTuning) { c.DoorOpenRange = 10 }},
		{"DoorGunners", doorAfter(5), func(c *CombatTuning) { c.DoorGunners = 1 }},
		{"FlankMinDefenders", flankFinal(true), func(c *CombatTuning) { c.FlankMinDefenders = 6 }},
		{"FlankSize", flankFinal(true), func(c *CombatTuning) { c.FlankSize = 1 }},
		{"FlankLateral", flankFinal(false), func(c *CombatTuning) { c.FlankLateral = 2 }},
		{"GroupGap", func(t *testing.T, tune func(CombatMemory) CombatMemory) tuningOutcome {
			view := twoGroupView()
			_, m := decideStop(t, view, StopEvent{}, CombatMemory{})
			view.Tick += 60
			return finalStop(view, GeometryReply{}, tune(m), false)
		}, func(c *CombatTuning) { c.GroupGap = 30 }},
		{"KiteLeadRange", func(t *testing.T, tune func(CombatMemory) CombatMemory) tuningOutcome {
			_, m := decideStop(t, kiteView(3.5), StopEvent{}, CombatMemory{})
			view := kiteView(3.5)
			view.Tick = 160
			return finalStop(view, GeometryReply{}, tune(m), true)
		}, func(c *CombatTuning) { c.KiteLeadRange = 30 }},
		{"KiteSpeedRatio", func(t *testing.T, tune func(CombatMemory) CombatMemory) tuningOutcome {
			return finalStop(kiteView(3.5), GeometryReply{}, tune(CombatMemory{}), true)
		}, func(c *CombatTuning) { c.KiteSpeedRatio = 1.8 }},
		{"RushRange", func(t *testing.T, tune func(CombatMemory) CombatMemory) tuningOutcome {
			_, m := decideStop(t, sapperView(), StopEvent{}, CombatMemory{})
			view := sapperView()
			view.Tick = 160
			return finalStop(view, GeometryReply{}, tune(m), true)
		}, func(c *CombatTuning) { c.RushRange = 30 }},
		{"ScatterCells", func(t *testing.T, tune func(CombatMemory) CombatMemory) tuningOutcome {
			view, memory := fallbackView(t)
			view.DamagedDoors = []domain.Cell{{X: 9, Z: 30}}
			for _, th := range view.Positional {
				c, _ := th.Position.Value()
				view.Pawns = append(view.Pawns, CombatPawnState{ID: domain.PawnID(th.ID), Kind: "Pirate", Cell: domain.Known(c)})
			}
			_, memory = decideStop(t, view, StopEvent{}, memory)
			for i := range 3 {
				id, cell := PawnID(fmt.Sprintf("x%d", i)), domain.Cell{X: int32(8 + i), Z: 6}
				s, d := combatRaider(id, cell)
				view.Threats, view.Positional = append(view.Threats, s), append(view.Positional, d)
				view.Pawns = append(view.Pawns, CombatPawnState{ID: domain.PawnID(id), Kind: "Pirate", Cell: domain.Known(cell)})
			}
			view.Tick = 300
			view.Pawns[1].Cell = domain.Known(domain.Cell{X: 8, Z: 27})
			view.Pawns[2].Cell = domain.Known(domain.Cell{X: 11, Z: 28})
			out := finalStop(view, GeometryReply{}, tune(memory), true)
			if !out.Memory.Scattered {
				t.Fatalf("fixture did not scatter: %+v", out.Memory)
			}
			return out
		}, func(c *CombatTuning) { c.ScatterCells = 5 }},
		{"DoseMargin", func(t *testing.T, tune func(CombatMemory) CombatMemory) tuningOutcome {
			far := CombatPawnState{Cell: domain.Known(domain.Cell{X: 60, Z: 0})}
			view, orderable, state := doseView(2, far)
			m := tune(CombatMemory{})
			orders := doseOrders(view, &m, nil, orderable, state)
			m.Tuning = nil
			return tuningOutcome{Orders: orders, Memory: m}
		}, func(c *CombatTuning) { c.DoseMargin = 40 }},
	}
	covered := map[string]bool{}
	for _, c := range cases {
		covered[c.name] = true
		t.Run(c.name, func(t *testing.T) {
			tuned := DefaultCombatTuning()
			c.tune(&tuned)
			if err := tuned.Validate(); err != nil {
				t.Fatal(err)
			}
			def := c.scenario(t, func(m CombatMemory) CombatMemory { return m })
			explicit := c.scenario(t, func(m CombatMemory) CombatMemory { return m.WithTuning(DefaultCombatTuning()) })
			if !reflect.DeepEqual(def, explicit) {
				t.Fatalf("explicit default tuning differs from none:\n%+v\n%+v", def, explicit)
			}
			got := c.scenario(t, func(m CombatMemory) CombatMemory { return m.WithTuning(tuned) })
			if reflect.DeepEqual(def, got) {
				t.Fatalf("tuning %+v changed nothing: %+v", tuned, got)
			}
		})
	}
	// Every field of CombatTuning has a case.
	typ := reflect.TypeOf(CombatTuning{})
	for i := range typ.NumField() {
		if !covered[typ.Field(i).Name] {
			t.Errorf("no case for %s", typ.Field(i).Name)
		}
	}
}

func TestCombatTuningValidate(t *testing.T) {
	if err := DefaultCombatTuning().Validate(); err != nil {
		t.Fatal(err)
	}
	bad := []func(*CombatTuning){
		func(c *CombatTuning) { c.DoorCloseRange = 0 },
		func(c *CombatTuning) { c.DoorCloseRange = c.DoorOpenRange + 1 },
		func(c *CombatTuning) { c.DoorGunners = 9 },
		func(c *CombatTuning) { c.FlankSize = 0 },
		func(c *CombatTuning) { c.KiteSpeedRatio = 0.5 },
		func(c *CombatTuning) { c.DoseMargin = -1 },
		func(c *CombatTuning) { c.ScatterCells = 61 },
	}
	for i, mutate := range bad {
		c := DefaultCombatTuning()
		mutate(&c)
		if c.Validate() == nil {
			t.Errorf("case %d: %+v validated", i, c)
		}
	}
}

func TestCombatTuningCloned(t *testing.T) {
	m := CombatMemory{}.WithTuning(DefaultCombatTuning())
	c := m.clone()
	c.Tuning.DoorGunners = 7
	if m.Tuning.DoorGunners == 7 {
		t.Fatal("clone shares the tuning")
	}
}

// Concurrent fights with different tunings each decide as they do alone.
func TestCombatTuningConcurrentFights(t *testing.T) {
	loose, tight := DefaultCombatTuning(), DefaultCombatTuning()
	loose.DoorGunners, tight.DoorGunners = 1, 3
	run := func(tuning CombatTuning) tuningOutcome {
		return finalStop(doorView(5), GeometryReply{}, CombatMemory{}.WithTuning(tuning), true)
	}
	wantLoose, wantTight := run(loose), run(tight)
	if reflect.DeepEqual(wantLoose, wantTight) {
		t.Fatal("tunings do not differ")
	}
	var wg sync.WaitGroup
	for i := range 8 {
		tuning, want := loose, wantLoose
		if i%2 == 1 {
			tuning, want = tight, wantTight
		}
		wg.Add(1)
		go func() {
			defer wg.Done()
			for range 50 {
				if got := run(tuning); !reflect.DeepEqual(got, want) {
					t.Errorf("fight with %+v decided differently under concurrency", tuning)
					return
				}
			}
		}()
	}
	wg.Wait()
}
