package domain

import (
	"context"
	"strings"
	"testing"
	"time"
)

// The same tick difference is fresh for a definitions read and stale for a
// dispatch precondition; a version mismatch is stale at any age; a scope
// change is stale at any version; an invalidated view is stale whole; a
// stopped clock keeps the tick-exact bound (#624).
func TestReadValidityAgeClasses(t *testing.T) {
	t.Parallel()
	scope := ReadScope{Colony: "c", Map: 1, Load: "l", Native: 4}
	// 6000 ticks/s over the step's 1 s: inventory bound 6250, dispatch
	// 250 + 6000 * 0.25 s = 1750.
	step := ReadValidity{Scope: scope, Tick: 10000, Pace: 6000, Wall: time.Second, Versions: map[string]uint64{"buildings": 3, "bills": 7}}
	other := scope
	other.Native++
	reloaded := scope
	reloaded.Load = "l2"
	cases := []struct {
		name  string
		v     ReadValidity
		class AgeClass
		obs   ReadObservation
		stale string
	}{
		{"definitions 3000 ticks back", step, AgeStable, ReadObservation{Scope: scope, Tick: 7000}, ""},
		{"inventory 3000 ticks back", step, AgeInventory, ReadObservation{Scope: scope, Tick: 7000}, ""},
		{"dispatch 3000 ticks back", step, AgeDispatch, ReadObservation{Scope: scope, Tick: 7000}, "dispatch bound 1750"},
		{"dispatch at its bound", step, AgeDispatch, ReadObservation{Scope: scope, Tick: 10000 - 1750}, ""},
		{"inventory past its bound", step, AgeInventory, ReadObservation{Scope: scope, Tick: 10000 - 6251}, "inventory bound 6250"},
		{"definitions a day back", step, AgeStable, ReadObservation{Scope: scope, Tick: 0}, ""},
		{"version mismatch at the same tick", step, AgeInventory, ReadObservation{Scope: scope, Tick: 10000, Section: "buildings", Version: 2}, "section buildings version 2, step holds 3"},
		{"version mismatch on a stable read", step, AgeStable, ReadObservation{Scope: scope, Tick: 10000, Section: "buildings", Version: 4}, "section buildings version 4"},
		{"version match certifies any age", step, AgeInventory, ReadObservation{Scope: scope, Tick: 0, Section: "bills", Version: 7}, ""},
		{"version match never certifies a dispatch", step, AgeDispatch, ReadObservation{Scope: scope, Tick: 0, Section: "bills", Version: 7}, "dispatch bound"},
		{"untracked section falls back to the tick", step, AgeInventory, ReadObservation{Scope: scope, Tick: 0, Section: "zones", Version: 1}, "inventory bound"},
		{"generation change at a matching version", step, AgeStable, ReadObservation{Scope: other, Tick: 10000, Section: "bills", Version: 7}, "native generation 5, step is 4"},
		{"reload at a matching version", step, AgeInventory, ReadObservation{Scope: reloaded, Tick: 10000, Section: "bills", Version: 7}, "scope c/1/l2, step is c/1/l"},
		{"observation ahead of the step", step, AgeStable, ReadObservation{Scope: scope, Tick: 10001}, "ahead of the step"},
		{"invalidated view", ReadValidity{Scope: scope, Tick: 10000, Invalid: "event gap"}, AgeStable, ReadObservation{Scope: scope, Tick: 10000}, "view invalidated: event gap"},
		{"stopped clock is tick-exact", ReadValidity{Scope: scope, Tick: 10000, Wall: time.Second}, AgeInventory, ReadObservation{Scope: scope, Tick: 10000 - PlanningTickTolerance - 1}, "inventory bound 250"},
		{"stopped clock within the tolerance", ReadValidity{Scope: scope, Tick: 10000, Wall: time.Second}, AgeDispatch, ReadObservation{Scope: scope, Tick: 10000 - PlanningTickTolerance}, ""},
		{"unknown validity checks nothing", ReadValidity{}, AgeDispatch, ReadObservation{Scope: other, Tick: 0}, ""},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			got := c.v.Stale(c.class, c.obs)
			if c.stale == "" && got != "" || c.stale != "" && !strings.Contains(got, c.stale) {
				t.Fatalf("stale %q, want %q", got, c.stale)
			}
			if c.v.Fresh(c.class, c.obs) != (got == "") {
				t.Fatal("Fresh disagrees with Stale")
			}
		})
	}
}

// FreshFor and Covers apply the class bound in the shim's direction, and
// the context helpers fall back to the shim without a validity.
func TestReadValidityContextHelpers(t *testing.T) {
	scope := ReadScope{Colony: "c", Map: 1, Load: "l", Native: 1}
	v := ReadValidity{Scope: scope, Tick: 100, Pace: 6000, Wall: time.Second}
	ctx := WithReadValidity(context.Background(), v)
	if got, ok := ReadValidityFrom(ctx); !ok || got.Tick != 100 {
		t.Fatal(got, ok)
	}
	if _, ok := ReadValidityFrom(context.Background()); ok {
		t.Fatal("bare context carries no validity")
	}
	if !FreshIn(ctx, AgeInventory, 3100, 100) || FreshIn(ctx, AgeDispatch, 3100, 100) || !FreshIn(ctx, AgeStable, 1e6, 100) || FreshIn(ctx, AgeStable, 99, 100) {
		t.Fatal("class bounds")
	}
	if !CoversIn(ctx, AgeInventory, 100, 3100) || CoversIn(ctx, AgeDispatch, 100, 3100) || !CoversIn(ctx, AgeDispatch, 3100, 100) {
		t.Fatal("covers")
	}
	SetLiveDrift(0)
	t.Cleanup(func() { SetLiveDrift(0) })
	if FreshIn(context.Background(), AgeInventory, 3100, 100) || !FreshIn(context.Background(), AgeInventory, 350, 100) {
		t.Fatal("shim fallback")
	}
	SetLiveDrift(5000)
	if !FreshIn(context.Background(), AgeDispatch, 3100, 100) {
		t.Fatal("shim widens every class the same way")
	}
}

// A dispatch's reads that took a second under a 2000 ticks/s window may
// straddle the ticks that second ran; a fast round trip keeps the
// DispatchReadWall floor, and a stopped clock stays tick-exact (#666).
func TestFreshAcrossWidensByTheReadsOwnWall(t *testing.T) {
	scope := ReadScope{Colony: "c", Map: 1, Load: "l", Native: 1}
	live := WithReadValidity(context.Background(), ReadValidity{Scope: scope, Tick: 100, Pace: 2000, Wall: time.Second})
	if FreshIn(live, AgeDispatch, 2100, 100) || !FreshAcross(live, time.Second, 2100, 100) || FreshAcross(live, time.Second, 2400, 100) {
		t.Fatal("the reads' wall widens the dispatch bound")
	}
	if !FreshAcross(live, 0, 850, 100) || FreshAcross(live, 0, 851, 100) || FreshAcross(live, time.Second, 99, 100) {
		t.Fatal("floor and direction")
	}
	stopped := WithReadValidity(context.Background(), ReadValidity{Scope: scope, Tick: 100, Wall: time.Second})
	if !FreshAcross(stopped, time.Minute, 350, 100) || FreshAcross(stopped, time.Minute, 351, 100) {
		t.Fatal("a stopped clock keeps the tick-exact bound")
	}
}
