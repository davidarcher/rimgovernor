package domain

import (
	"context"
	"fmt"
	"time"
)

// AgeClass is how long a fact of its kind serves a decision (#624). A
// decision context carries one ReadValidity; every fact it checks names
// its class, so a definitions read and a dispatch precondition observed
// the same ticks apart are judged differently.
type AgeClass uint8

const (
	// AgeStable: definitions and other facts the map's ticks do not
	// change. Cached until the view is invalidated; never tick-bound.
	AgeStable AgeClass = iota + 1
	// AgeInventory: inventory, construction, bills and the rest of the
	// colony state. Fresh by section version where the native fact-change
	// digests give one, else within the step's tick bound: the planning
	// tolerance plus the ticks the running window's pace covers in the
	// wall time the step's reads have (#345).
	AgeInventory
	// AgeDispatch: a dangerous action's preconditions (the pawn, the
	// target, the route, the authority). Bound to the ticks the pace
	// covers in one dispatch's reads (DispatchReadWall), never the step's
	// whole wall; the native operation revalidates them at application
	// time, and a fresh emergency census never certifies an earlier read.
	AgeDispatch
)

func (c AgeClass) String() string {
	switch c {
	case AgeStable:
		return "stable"
	case AgeInventory:
		return "inventory"
	case AgeDispatch:
		return "dispatch"
	}
	return fmt.Sprintf("class(%d)", uint8(c))
}

// DispatchReadWall is the wall time a dispatch's own reads span: the
// inspection, its preparation and the live re-read, a few native round
// trips. The dispatch class converts it to ticks at the window's pace.
const DispatchReadWall = 250 * time.Millisecond

// ReadScope is the loaded world a read describes: the map, its load token
// and the native order generation. A read from another scope is stale at
// any age and any version.
type ReadScope struct {
	Colony ColonyID
	Map    MapID
	Load   LoadID
	Native NativeGeneration
}

// ScopeOf is the read scope of a generation snapshot.
func ScopeOf(s GenerationSnapshot) ReadScope {
	return ReadScope{Colony: s.Colony, Map: s.Map, Load: s.Load, Native: s.Native}
}

// ValidityOf is the validity bound to s at tick, before a pace or the
// section versions are known.
func ValidityOf(s GenerationSnapshot, tick Tick) ReadValidity {
	return ReadValidity{Scope: ScopeOf(s), Plan: s.Plan, Revision: s.Revision, Tick: tick}
}

func (s ReadScope) String() string {
	return fmt.Sprintf("%s/%d/%s@%d", s.Colony, s.Map, s.Load, s.Native)
}

// ReadValidity is what a decision context's facts are valid against
// (#624): the scope and tick the step's reads are bound to, the pace the
// running window was measured at and the wall time the step's reads have
// (together the tick bound of each age class), the per-section versions
// the fact store held when the scope was fixed, and whether the view was
// invalidated whole. It replaces the process-global live drift: the same
// ticks widen an inventory bound and a dispatch bound differently, and a
// zero Pace (a stopped clock) keeps every bound tick-exact.
type ReadValidity struct {
	Scope ReadScope
	// Plan and Revision are the plan revision the step commits under;
	// zero when the validity binds reads alone.
	Plan     PlanID
	Revision PlanRevision
	// Tick is the observation tick the step's facts describe.
	Tick Tick
	// Pace is the running window's measured ticks per wall second; zero
	// under a stopped clock or before a pace is measured.
	Pace float64
	// Wall is the wall time the step's reads already have (the
	// scheduler's MaxAge): the inventory class's bound at Pace.
	Wall time.Duration
	// Versions is the fact store's per-section version when the scope
	// was fixed, keyed by section name: a version the native invalidation
	// stream has moved since is stale at any age.
	Versions map[string]uint64
	// Invalid names why the whole view no longer holds (an event gap, a
	// reload, an unsupported mutation); set, every observation is stale.
	Invalid string
}

// ReadObservation is one fact's provenance as a validity checks it.
type ReadObservation struct {
	Scope ReadScope
	Tick  Tick
	// Section and Version, when set, name the fact-store section the
	// observation was read under and its version then.
	Section string
	Version uint64
}

// Known reports a validity fixed by a step; the zero value validates
// nothing and every check falls back to the tick bound alone.
func (v ReadValidity) Known() bool { return v.Scope != ReadScope{} }

// Drift is the ticks the pace covers in the wall time class allows: the
// step's wall for inventory, one dispatch's reads for dispatch, none for
// a stable fact.
func (v ReadValidity) Drift(class AgeClass) Tick {
	wall := v.Wall
	switch class {
	case AgeStable:
		return 0
	case AgeDispatch:
		wall = min(wall, DispatchReadWall)
	}
	if v.Pace <= 0 || wall <= 0 {
		return 0
	}
	return Tick(v.Pace * wall.Seconds())
}

// Bound is the most ticks an observation of class may lag its anchor;
// false for a stable fact, which no tick advance ages.
func (v ReadValidity) Bound(class AgeClass) (Tick, bool) {
	if class == AgeStable {
		return 0, false
	}
	return PlanningTickTolerance + v.Drift(class), true
}

// FreshFor is Tick.FreshFor under class: t describes anchor when it is at
// or after it and past it by no more than Bound.
func (v ReadValidity) FreshFor(class AgeClass, t, anchor Tick) bool {
	if t < anchor {
		return false
	}
	bound, bounded := v.Bound(class)
	return !bounded || t-anchor <= bound
}

// Covers is Tick.Covers under class: t describes anchor when at or after
// it, or before it by no more than Bound.
func (v ReadValidity) Covers(class AgeClass, t, anchor Tick) bool {
	return t >= anchor || v.FreshFor(class, anchor, t)
}

// Stale names the first reason obs no longer serves v under class, empty
// when it still does. In order: an invalidated view; another scope (the
// world, then the native generation); a section version the invalidation
// stream has moved, which is stale at any age, while a matching version
// certifies a stable or inventory fact at any age; then the tick: obs
// may predate v.Tick by at most the class's bound, and never follow it.
func (v ReadValidity) Stale(class AgeClass, obs ReadObservation) string {
	if !v.Known() {
		return ""
	}
	if v.Invalid != "" {
		return "view invalidated: " + v.Invalid
	}
	have, want := obs.Scope, v.Scope
	switch {
	case have.Colony != want.Colony || have.Map != want.Map || have.Load != want.Load:
		return fmt.Sprintf("scope %s/%d/%s, step is %s/%d/%s", have.Colony, have.Map, have.Load, want.Colony, want.Map, want.Load)
	case have.Native != want.Native:
		return fmt.Sprintf("native generation %d, step is %d", have.Native, want.Native)
	}
	if obs.Section != "" {
		if want, tracked := v.Versions[obs.Section]; tracked {
			if obs.Version != want {
				return fmt.Sprintf("section %s version %d, step holds %d", obs.Section, obs.Version, want)
			}
			if class != AgeDispatch {
				return ""
			}
		}
	}
	if obs.Tick > v.Tick {
		return fmt.Sprintf("tick %d ahead of the step's %d", obs.Tick, v.Tick)
	}
	if bound, bounded := v.Bound(class); bounded && v.Tick-obs.Tick > bound {
		return fmt.Sprintf("tick %d, step is at %d (%s bound %d)", obs.Tick, v.Tick, class, bound)
	}
	return ""
}

// Fresh reports Stale empty.
func (v ReadValidity) Fresh(class AgeClass, obs ReadObservation) bool {
	return v.Stale(class, obs) == ""
}

type readValidityKey struct{}

// WithReadValidity carries v on the decision context.
func WithReadValidity(ctx context.Context, v ReadValidity) context.Context {
	return context.WithValue(ctx, readValidityKey{}, v)
}

// ReadValidityFrom is the validity the context carries, false when none.
func ReadValidityFrom(ctx context.Context) (ReadValidity, bool) {
	if ctx == nil {
		return ReadValidity{}, false
	}
	v, ok := ctx.Value(readValidityKey{}).(ReadValidity)
	return v, ok && v.Known()
}

// FreshIn is ReadValidity.FreshFor under the context's validity, or the
// compatibility shim Tick.FreshFor (the global drift) for a caller not yet
// migrated to carry one.
func FreshIn(ctx context.Context, class AgeClass, t, anchor Tick) bool {
	if v, ok := ReadValidityFrom(ctx); ok {
		return v.FreshFor(class, t, anchor)
	}
	return t.FreshFor(anchor)
}

// CoversIn is ReadValidity.Covers under the context's validity, or the
// shim Tick.Covers.
func CoversIn(ctx context.Context, class AgeClass, t, anchor Tick) bool {
	if v, ok := ReadValidityFrom(ctx); ok {
		return v.Covers(class, t, anchor)
	}
	return t.Covers(anchor)
}
