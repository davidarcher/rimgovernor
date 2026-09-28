package main

import (
	"context"
	"fmt"
	"io"
	"maps"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
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
func shadowGovernorState(ctx context.Context, native governorStateNative, world func(context.Context) (governorWorld, bool), database *store.Store, orphans orphanNative, refresh time.Duration, out io.Writer) {
	shadow := governorShadow{orphans: orphans}
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
// orphans, when set, is the native the #1000 orphan pass sweeps.
type governorShadow struct {
	world   governorWorld
	written map[string]string
	orphans orphanNative
}

func (s *governorShadow) round(ctx context.Context, world governorWorld, native governorStateNative, database *store.Store, out io.Writer) error {
	if world != s.world {
		s.world, s.written = world, nil
	}
	var pass store.GoalOrphanPass
	if s.orphans != nil {
		pass = orphanSweep(s.orphans, world, out)
	}
	return shadowGovernorStateOnce(ctx, native, database, &s.written, pass, out)
}

// orphanNative lists and cancels the Autopilot's native side effects
// (#1000). Only the trade session has a "list mine" read today; every other
// intent kind is a filed follow-up.
type orphanNative interface {
	buildingruntime.TradeNative
	boundary.ActionsWriter
}

// orphanSweep is the #1000 orphan pass for one world. A rebuild deletes
// every goal method (#998), so no rebuilt goal owns a native side effect and
// each one listed is cancelled (D3: the Autopilot has full control). A read
// or transport error aborts the rebuild so the next round retries; a native
// refusal is logged.
func orphanSweep(native orphanNative, world governorWorld, out io.Writer) store.GoalOrphanPass {
	return func(ctx context.Context, plans []store.PlanState) error {
		identity := boundary.Identity(domain.GenerationSnapshot{Colony: world.Colony, Map: world.Map, Load: world.Load})
		session, _, err := native.ReadTradeSession(ctx, identity)
		if err != nil {
			return fmt.Errorf("orphans: trade session: %w", err)
		}
		if session.Trader == "" {
			return nil
		}
		end, err := domain.NewTradeEnd(session.Trader, domain.PawnID(session.Negotiator), domain.TradeEndCancel, false)
		if err != nil {
			return err
		}
		action, err := domain.NewTradeAction("orphan-trade-end", end)
		if err != nil {
			return err
		}
		wire, err := bridge.IntentAction("orphan-trade-end", action)
		if err != nil {
			return err
		}
		reply, _, err := native.Apply(ctx, identity, []*o.Action{wire})
		if err != nil {
			return fmt.Errorf("orphans: cancel trade: %w", err)
		}
		if result := reply.GetResults()[0]; result.GetApplied() == nil {
			fmt.Fprintf(out, "governor state: orphan trade with %s not cancelled: %v\n", session.Trader, result)
			return nil
		}
		fmt.Fprintf(out, "governor state: cancelled orphan trade with %s (%d old plans)\n", session.Trader, len(plans))
		return nil
	}
}

func shadowGovernorStateOnce(ctx context.Context, native governorStateNative, database *store.Store, written *map[string]string, reconcileGoalOrphans store.GoalOrphanPass, out io.Writer) error {
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
		if err = database.RebuildFamilies(ctx, saved); err != nil {
			return fmt.Errorf("rebuild families: %w", err)
		}
		// The shadow writer owns every saved key, so it seeds from all.
		*written = maps.Clone(saved)
		if *written == nil {
			*written = map[string]string{}
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
