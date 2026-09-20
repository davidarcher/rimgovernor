package observation

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
)

// RoutineStore is the routine census's section refresher (#360): the facts
// store a review's earlier readings filed their sections in, attached to
// the reading's context by the reviewer. Each continuous section the
// bracket would read (research, population, rooms, the colonists' pawn
// detail) is served from the store instead when the held value is still
// fresh at the reading's tick under the section's cadence
// (facts.Section.TickTolerance) and any MaxAge a policy set for it; a
// stale or absent section is read natively as before. The colony facts
// and the emergency census are always read: the colony read is the
// bracket's identity anchor and every step's bundle carries the emergency
// census. The served sections keep their own as-of tick, so the review's
// as_of_spread shows what it planned against.
type RoutineStore struct {
	Store *facts.Store
	// MaxAge bounds a section's cadence for this reading, in ticks
	// (facts.Store.FreshWithin); a section absent from it keeps its cadence.
	MaxAge map[facts.Section]int64
	// Held names the sections no planner this reading feeds consumes
	// (#625): each is served from the store past its cadence while the
	// store holds it usable (facts.Store.Held), and read again only once
	// an invalidation has dropped or marked it. MaxAge still bounds a
	// section named in both.
	Held map[facts.Section]bool
}

type routineStoreKey struct{}

// WithRoutineStore attaches store to ctx so the routine census read under
// it serves its fresh sections from the store.
func WithRoutineStore(ctx context.Context, store RoutineStore) context.Context {
	return context.WithValue(ctx, routineStoreKey{}, store)
}

// RoutineStoreFrom returns the store ctx carries; a zero value serves
// nothing.
func RoutineStoreFrom(ctx context.Context) RoutineStore {
	store, _ := ctx.Value(routineStoreKey{}).(RoutineStore)
	return store
}

// held returns the section the store holds when it still serves tick.
func heldSection[T any](r RoutineStore, section facts.Section, tick int64) (facts.Held[T], bool) {
	if r.Store == nil {
		return facts.Held[T]{}, false
	}
	maxAge := bridge.FactTickUnbounded
	if age, ok := r.MaxAge[section]; ok {
		maxAge = age
	}
	if r.Held[section] && maxAge == bridge.FactTickUnbounded {
		if !r.Store.Held(section, tick) {
			return facts.Held[T]{}, false
		}
		return facts.Get[T](r.Store, section)
	}
	if !r.Store.FreshWithin(section, tick, maxAge) {
		return facts.Held[T]{}, false
	}
	return facts.Get[T](r.Store, section)
}
