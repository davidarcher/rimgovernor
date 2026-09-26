package buildingruntime

import (
	"context"
	"errors"
	"reflect"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func (p *Player) WorkPreferences(ctx context.Context, plan domain.PlanID) (store.WorkPreferences, error) {
	return p.journal.LoadWorkPreferences(ctx, plan)
}

// Preference changes and routine reviews share the player gate. The store
// atomically invalidates any old review and its methods when preferences change.
func (p *Player) SetWorkPreferences(ctx context.Context, q store.WorkPreferenceRequest) (store.WorkPreferenceRecord, error) {
	if err := q.Validate(); err != nil {
		return store.WorkPreferenceRecord{}, err
	}
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.WorkPreferenceRecord{}, err
	}
	defer done()
	old, err := p.journal.LookupWorkPreference(call, q.RequestID)
	if err == nil {
		if !reflect.DeepEqual(old.Request, q) {
			return store.WorkPreferenceRecord{}, store.ErrConflict
		}
		return old, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.WorkPreferenceRecord{}, err
	}
	if err = p.world(call, q.World); err != nil {
		return store.WorkPreferenceRecord{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.WorkPreferenceRecord{}, err
	}
	record, err := p.journal.SetWorkPreferences(call, q)
	if err == nil {
		p.replanned()
	}
	return record, err
}
