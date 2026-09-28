package main

import (
	"context"
	"fmt"
	"io"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// governorStateNative is the save's governor state component (#882).
type governorStateNative interface {
	GovernorState(context.Context) (map[string]string, error)
	PutGovernorState(context.Context, string, string) (map[string]string, error)
}

// shadowGovernorState mirrors goals and family records into the save each
// refresh (#974): only changed keys are put, a vanished key is deleted.
// The first successful read in each world (#994: a new world or native
// generation re-reads the save) rebuilds the store's goals from the save
// (#998) and logs family drift; the store stays authoritative for
// families. A round with no current world skips.
func shadowGovernorState(ctx context.Context, native governorStateNative, world func(context.Context) (governorWorld, bool), database *store.Store, refresh time.Duration, out io.Writer) {
	var shadow governorShadow
	ticker := time.NewTicker(refresh)
	defer ticker.Stop()
	for {
		call, cancel := context.WithTimeout(ctx, 10*time.Second)
		if current, ok := world(call); ok {
			if err := shadow.round(call, current, native, database, out); err != nil && ctx.Err() == nil {
				fmt.Fprintf(out, "governor state: %v\n", err)
			}
		}
		cancel()
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}

// governorWorld names one loaded world and native generation: the
// SameWorld fields plus the generation, never the plan.
type governorWorld struct {
	Colony     domain.ColonyID
	Map        domain.MapID
	Load       domain.LoadID
	Generation domain.NativeGeneration
}

// currentGovernorWorld reads the world from a fresh, connected snapshot.
func currentGovernorWorld(reads httpapi.SnapshotProvider) func(context.Context) (governorWorld, bool) {
	return func(ctx context.Context) (governorWorld, bool) {
		snapshot, err := reads.Snapshot(ctx)
		if err != nil || !snapshot.Connected || snapshot.Stale {
			return governorWorld{}, false
		}
		identity, ok := snapshot.Identity.Value()
		if !ok {
			return governorWorld{}, false
		}
		generation, ok := identity.NativeGeneration.Value()
		if !ok {
			return governorWorld{}, false
		}
		return governorWorld{identity.Colony, identity.Map, identity.Load, generation}, true
	}
}

// governorShadow is the per-world written cache; a world change drops it
// so the next round re-reads the save and re-checks drift.
type governorShadow struct {
	world   governorWorld
	written map[string]string
}

func (s *governorShadow) round(ctx context.Context, world governorWorld, native governorStateNative, database *store.Store, out io.Writer) error {
	if world != s.world {
		s.world, s.written = world, nil
	}
	return shadowGovernorStateOnce(ctx, native, database, &s.written, out)
}

// reconcileGoalOrphans is where the #1000 orphan pass runs: a goal rebuild
// hands it the old goal method plans before retiring them. Nil until then.
var reconcileGoalOrphans store.GoalOrphanPass

func shadowGovernorStateOnce(ctx context.Context, native governorStateNative, database *store.Store, written *map[string]string, out io.Writer) error {
	if *written == nil {
		// First round in a world (#998): the save's goals replace the
		// store's before the first write, so the writer seeds from them.
		saved, err := native.GovernorState(ctx)
		if err != nil {
			return err
		}
		if err = database.RebuildGoals(ctx, saved, reconcileGoalOrphans); err != nil {
			return fmt.Errorf("rebuild goals: %w", err)
		}
		blobs, err := database.GovernorStateBlobs(ctx)
		if err != nil {
			return err
		}
		if drift := store.GovernorStateDrift(blobs, saved); len(drift) > 0 {
			fmt.Fprintf(out, "governor state: drift from store on load (%d keys): %v\n", len(drift), drift)
		}
		*written = map[string]string{}
		for key, blob := range saved {
			if store.GovernorShadowKey(key) {
				(*written)[key] = blob
			}
		}
	}
	blobs, err := database.GovernorStateBlobs(ctx)
	if err != nil {
		return err
	}
	for key, blob := range blobs {
		if (*written)[key] == blob {
			continue
		}
		if _, err := native.PutGovernorState(ctx, key, blob); err != nil {
			return fmt.Errorf("put %s: %w", key, err)
		}
		(*written)[key] = blob
	}
	for key := range *written {
		if _, ok := blobs[key]; ok {
			continue
		}
		if _, err := native.PutGovernorState(ctx, key, ""); err != nil {
			return fmt.Errorf("delete %s: %w", key, err)
		}
		delete(*written, key)
	}
	return nil
}
