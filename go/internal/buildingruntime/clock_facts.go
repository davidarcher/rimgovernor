package buildingruntime

import (
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// clockFacts is the scheduler's cross-step observation cache and the
// bounded memory of which action kind each natively watched attempt
// belongs to, so an OperationOutcome event can drop only the fact families
// that kind of operation changes.
type clockFacts struct {
	cache   *bridge.FactCache
	mu      sync.Mutex
	watched map[domain.ActionID]domain.ActionKind
}

const clockFactsWatchedMax = 256

func newClockFacts() *clockFacts {
	return &clockFacts{cache: bridge.NewFactCache(), watched: map[domain.ActionID]domain.ActionKind{}}
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

// apply drops the cached facts a committed events page makes stale.
func (f *clockFacts) apply(page *k.EventsPage) {
	all, families := clockPageInvalidation(page, f.kindOf)
	if all {
		f.cache.Invalidate()
		return
	}
	f.cache.InvalidateFamilies(families...)
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
// ObservationInvalidated events name.
func clockPageInvalidation(page *k.EventsPage, kindOf func(domain.ActionID) (domain.ActionKind, bool)) (all bool, families []bridge.FactFamily) {
	seen := map[bridge.FactFamily]bool{}
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
			return true, nil
		case *k.Event_OperationOutcome:
			kind, known := kindOf(domain.ActionID(v.OperationOutcome.GetAttempt().GetActionId()))
			whole, list := operationFamilies(kind, known)
			if whole {
				return true, nil
			}
			add(list)
		case *k.Event_ObservationInvalidated:
			for _, wire := range v.ObservationInvalidated.GetFamilies() {
				family, ok := bridge.FactFamilyFromWire(wire)
				if !ok {
					return true, nil
				}
				add([]bridge.FactFamily{family})
			}
		}
	}
	return false, families
}
