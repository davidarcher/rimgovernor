package buildingruntime

import (
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// clockFacts is the scheduler's cross-step observation cache, the decoded
// state store beside it (facts.Store, #354) and the bounded memory of
// which action kind each natively watched attempt belongs to, so an
// OperationOutcome event can drop only the fact families that kind of
// operation changes. Every discard the cache takes, the store takes too.
type clockFacts struct {
	cache   *bridge.FactCache
	store   *facts.Store
	mu      sync.Mutex
	watched map[domain.ActionID]domain.ActionKind
	// windowRefreshes counts the planning window's delta refreshes across
	// steps for the resync cadence (planningWindow, #357).
	windowRefreshes int
	zoneRefreshes   int
	// entityRefreshes counts each entity section's delta refreshes across
	// steps for the same cadence (refreshEntitySections, #358).
	entityRefreshes map[facts.Section]int
	// asks are the step families the last review step's planners asked
	// for, folded into the next review bundle (#593).
	asks bridge.BundleStepAsks
}

const clockFactsWatchedMax = 256

func newClockFacts(cache *bridge.FactCache, store *facts.Store) *clockFacts {
	if cache == nil {
		cache = bridge.NewFactCache()
	}
	if store == nil {
		store = facts.NewStore()
	}
	return &clockFacts{cache: cache, store: store, watched: map[domain.ActionID]domain.ActionKind{}}
}

// remember keeps the kind of every attempt a window arms; the map is
// bounded and cleared once it fills, which only costs a broader invalidation.
func (f *clockFacts) remember(items []clockWorkItem) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, item := range items {
		if item.Attempt == 0 {
			continue
		}
		if len(f.watched) >= clockFactsWatchedMax {
			f.watched = map[domain.ActionID]domain.ActionKind{}
		}
		f.watched[item.Action] = item.Kind
	}
}

func (f *clockFacts) kindOf(id domain.ActionID) (domain.ActionKind, bool) {
	f.mu.Lock()
	defer f.mu.Unlock()
	kind, ok := f.watched[id]
	return kind, ok
}

// apply drops the cached facts a committed events page makes stale and
// reports whether anything was dropped or marked. The byte cache drops
// every family named; the store takes each invalidation as narrowed
// (facts.Store.Apply), so a zone edit marks its rows and leaves a
// planning window it does not touch fresh.
func (f *clockFacts) apply(page *k.EventsPage) bool {
	all, families, narrowed := clockPageInvalidation(page, f.kindOf)
	if all {
		f.cache.Invalidate()
		f.store.InvalidateAll()
		return true
	}
	f.cache.InvalidateFamilies(families...)
	var whole []bridge.FactFamily
	for _, family := range families {
		if !clockNarrowedOnly(family, narrowed) {
			whole = append(whole, family)
		}
	}
	f.store.InvalidateFamily(whole...)
	for _, inv := range narrowed {
		f.store.Apply(inv)
	}
	return len(families) > 0
}

// clockNarrowedOnly reports whether every mention of family on the page
// was a narrowed invalidation; one whole-family mention drops it.
func clockNarrowedOnly(family bridge.FactFamily, narrowed []facts.Invalidation) bool {
	found := false
	for _, inv := range narrowed {
		for _, named := range inv.Families {
			if named == family {
				found = true
			}
		}
	}
	return found
}

// operationFamilies names the fact families an operation of kind changes
// once its outcome is terminal; an unknown kind changes everything.
func operationFamilies(kind domain.ActionKind, known bool) (bool, []bridge.FactFamily) {
	if !known {
		return true, nil
	}
	switch kind {
	case domain.BuildingAction:
		return false, []bridge.FactFamily{bridge.FactColony, bridge.FactRooms, bridge.FactPawns}
	}
	return true, nil
}

// clockPageInvalidation folds the typed events of one committed page into
// either a whole-cache discard (an authority change, epoch start or stop
// can change any fact) or the union of families the operation outcomes and
// ObservationInvalidated events name. families is that union, for the
// byte cache; narrowed lists the ObservationInvalidated events that named
// entity ids or a rectangle (#359), for the store, while a family an
// outcome or an unnarrowed event names is whole in families and absent
// from narrowed's exclusive coverage.
func clockPageInvalidation(page *k.EventsPage, kindOf func(domain.ActionID) (domain.ActionKind, bool)) (all bool, families []bridge.FactFamily, narrowed []facts.Invalidation) {
	seen := map[bridge.FactFamily]bool{}
	whole := map[bridge.FactFamily]bool{}
	add := func(list []bridge.FactFamily) {
		for _, family := range list {
			if !seen[family] {
				seen[family] = true
				families = append(families, family)
			}
		}
	}
	for _, event := range page.GetEvents() {
		switch v := event.Event.(type) {
		case *k.Event_AuthorityChanged, *k.Event_Started, *k.Event_Stopped:
			return true, nil, nil
		case *k.Event_OperationOutcome:
			kind, known := kindOf(domain.ActionID(v.OperationOutcome.GetAttempt().GetActionId()))
			everything, list := operationFamilies(kind, known)
			if everything {
				return true, nil, nil
			}
			add(list)
			for _, family := range list {
				whole[family] = true
			}
		case *k.Event_ObservationInvalidated:
			inv, ok := facts.InvalidationFromWire(v.ObservationInvalidated)
			if !ok {
				return true, nil, nil
			}
			add(inv.Families)
			if inv.Narrowed() {
				narrowed = append(narrowed, inv)
				continue
			}
			for _, family := range inv.Families {
				whole[family] = true
			}
		}
	}
	// A family also named whole on the page is dropped whole; its narrowed
	// mentions add nothing.
	kept := narrowed[:0]
	for _, inv := range narrowed {
		var partial []bridge.FactFamily
		for _, family := range inv.Families {
			if !whole[family] {
				partial = append(partial, family)
			}
		}
		if len(partial) > 0 {
			inv.Families = partial
			kept = append(kept, inv)
		}
	}
	return false, families, kept
}
