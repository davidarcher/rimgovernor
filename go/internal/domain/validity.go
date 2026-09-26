package domain

import (
	"context"
	"fmt"
)

// ReadScope is the loaded world a read describes: the map, its load token
// and the native order generation. A read from another scope describes
// another world; the tick a read was taken at never makes it stale — the
// native side revalidates every action when it applies.
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

// ValidityOf is the validity bound to s at tick.
func ValidityOf(s GenerationSnapshot, tick Tick) ReadValidity {
	return ReadValidity{Scope: ScopeOf(s), Plan: s.Plan, Revision: s.Revision, Tick: tick}
}

func (s ReadScope) String() string {
	return fmt.Sprintf("%s/%d/%s@%d", s.Colony, s.Map, s.Load, s.Native)
}

// ReadValidity is the world a decision context's facts belong to: the
// scope, the plan revision the step commits under, the tick the step
// observed, and whether the view was invalidated whole.
type ReadValidity struct {
	Scope ReadScope
	// Plan and Revision are the plan revision the step commits under;
	// zero when the validity binds reads alone.
	Plan     PlanID
	Revision PlanRevision
	// Tick is the observation tick the step's facts describe.
	Tick Tick
	// Invalid names why the whole view no longer holds (an event gap, a
	// reload, an unsupported mutation); set, every observation is stale.
	Invalid string
}

// Known reports a validity fixed by a step; the zero value validates
// nothing.
func (v ReadValidity) Known() bool { return v.Scope != ReadScope{} }

// Stale names why a read of scope no longer serves v, empty when it
// does: an invalidated view, or another world or native generation.
func (v ReadValidity) Stale(have ReadScope) string {
	if !v.Known() {
		return ""
	}
	if v.Invalid != "" {
		return "view invalidated: " + v.Invalid
	}
	want := v.Scope
	switch {
	case have.Colony != want.Colony || have.Map != want.Map || have.Load != want.Load:
		return fmt.Sprintf("scope %s/%d/%s, step is %s/%d/%s", have.Colony, have.Map, have.Load, want.Colony, want.Map, want.Load)
	case have.Native != want.Native:
		return fmt.Sprintf("native generation %d, step is %d", have.Native, want.Native)
	}
	return ""
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
