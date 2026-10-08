package main

import (
	"context"
	"fmt"
	"io"
	"log/slog"
	"maps"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// governorStateNative is the save's governor state component (#882).
type governorStateNative interface {
	GovernorState(context.Context) (map[string]string, error)
	PutGovernorState(context.Context, string, string) error
}

// shadowGovernorState mirrors goals and family records into the save each
// refresh (#974): only changed keys are put, a vanished key is deleted.
// A commit that may create a goal wakes it early (#1362), so a restart
// right after creation does not rebuild the goal away.
// The first successful read in each world (#994: a new world or native
// generation re-reads the save) rebuilds the store's goals from the save
// (#998) and logs family drift; the store stays authoritative for
// families. A round with no current world skips.
func shadowGovernorState(ctx context.Context, native governorStateNative, world func(context.Context) (governorWorld, bool), database *store.Store, rebuild *worldRebuild, refresh time.Duration, out io.Writer) {
	shadow := governorShadow{rebuild: rebuild}
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
			// The service is stopping: put what the last round missed, so the
			// next process rebuilds from a save that holds the latest records.
			final, stop := context.WithTimeout(context.WithoutCancel(ctx), 5*time.Second)
			defer stop()
			if current, ok := world(final); ok {
				if err := shadow.round(final, current, native, database, out); err != nil {
					fmt.Fprintf(out, "governor state: final flush: %v\n", err)
				}
			}
			return
		case <-ticker.C:
		case <-database.StandardsWritten():
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

// worldRebuild is the per-world rebuild (#998/#1005/#1011): the save's
// goals and families replace the store's and rounds_review empties. The
// clock worker and the shadow writer both call ensure before they act in a
// world, so it runs once per world ahead of the first review (#1123).
// orphans, when set, is the native the #1000 orphan pass sweeps.
type worldRebuild struct {
	mu       sync.Mutex
	world    governorWorld
	count    uint64
	database *store.Store
	orphans  orphanNative
	out      io.Writer
}

// ensure rebuilds the store for world unless it was the last one rebuilt.
// The native generation is not a new world (#1141): authority toggles
// (resume, Manual, a reason=None bump) raise it with the save unchanged,
// and rebuilding on each one wiped every method and the review.
func (r *worldRebuild) ensure(ctx context.Context, world governorWorld, native governorStateNative) error {
	r.mu.Lock()
	defer r.mu.Unlock()
	sameWorld := r.world.Colony == world.Colony && r.world.Map == world.Map && r.world.Load == world.Load
	if r.count != 0 && sameWorld {
		return nil
	}
	// Start and end are logged with each phase's wall time (#1251): the
	// rebuild runs before the first review, so a slow one reads as a hang.
	began := time.Now()
	log := slog.With(telemetry.ComponentKey, "world-rebuild")
	log.InfoContext(ctx, "world rebuild started", "colony", world.Colony, "map", world.Map, "load", world.Load)
	saved, err := native.GovernorState(ctx)
	if err != nil {
		return err
	}
	read := time.Since(began)
	var pass store.StandardOrphanPass
	if r.orphans != nil {
		pass = orphanSweep(r.orphans, world, r.out)
	}
	if err = r.database.RebuildStandards(ctx, saved, pass); err != nil {
		return fmt.Errorf("rebuild standards: %w", err)
	}
	standards := time.Since(began) - read
	if err = r.database.RebuildFamilies(ctx, saved); err != nil {
		return fmt.Errorf("rebuild families: %w", err)
	}
	families := time.Since(began) - read - standards
	if err = r.database.ResetRounds(ctx); err != nil {
		return fmt.Errorf("reset rounds: %w", err)
	}
	r.world = world
	r.count++
	log.InfoContext(ctx, "world rebuild done", "keys", len(saved), "elapsed", time.Since(began).Round(time.Millisecond), "read", read.Round(time.Millisecond), "standards", standards.Round(time.Millisecond), "families", families.Round(time.Millisecond))
	return nil
}

// workerGate is the clock worker's WorldReady hook: it ensures the step's
// world is rebuilt and reports whether any rebuild ran since the worker's
// last step, so the step reviews again from the reset cache.
func (r *worldRebuild) workerGate(native governorStateNative) func(context.Context, *c.ObservationContext) (bool, error) {
	var seen uint64
	return func(ctx context.Context, observed *c.ObservationContext) (bool, error) {
		id := observed.GetIdentity()
		world := governorWorld{domain.ColonyID(id.GetColonyId()), domain.MapID(id.GetMapId()), domain.LoadID(id.GetLoadToken()), domain.NativeGeneration(observed.GetNativeGeneration())}
		if err := r.ensure(ctx, world, native); err != nil {
			return false, err
		}
		r.mu.Lock()
		defer r.mu.Unlock()
		reset := r.count != seen
		seen = r.count
		return reset, nil
	}
}

// governorShadow is the per-world written cache; a world change drops it
// so the next round re-reads the save and re-checks drift.
type governorShadow struct {
	world   governorWorld
	written map[string]string
	rebuild *worldRebuild
}

func (s *governorShadow) round(ctx context.Context, world governorWorld, native governorStateNative, database *store.Store, out io.Writer) error {
	if world != s.world {
		s.world, s.written = world, nil
	}
	if s.rebuild == nil {
		s.rebuild = &worldRebuild{database: database, out: out}
	}
	if s.written == nil {
		if err := s.rebuild.ensure(ctx, world, native); err != nil {
			return err
		}
	}
	return shadowGovernorStateOnce(ctx, native, database, &s.written, out)
}

// orphanNative lists and cancels the Autopilot's native side effects
// (#1000). Only the trade session has a "list mine" read today; every other
// intent kind is a filed follow-up.
type orphanNative interface {
	buildingruntime.TradeNative
	boundary.ActionsWriter
}

// orphanSweep is the #1000 orphan pass for one world. A rebuild deletes
// every method (#998), so no rebuilt goal owns a native side effect and
// each one listed is cancelled (D3: the Autopilot has full control). A read
// or transport error aborts the rebuild so the next round retries; a native
// refusal is logged.
func orphanSweep(native orphanNative, world governorWorld, out io.Writer) store.StandardOrphanPass {
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

func shadowGovernorStateOnce(ctx context.Context, native governorStateNative, database *store.Store, written *map[string]string, out io.Writer) error {
	if *written == nil {
		// First round in a world: the caller has rebuilt the store from
		// the save, so the writer seeds from the save's keys (it owns
		// every one) and puts only what changed since.
		saved, err := native.GovernorState(ctx)
		if err != nil {
			return err
		}
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
		if err := native.PutGovernorState(ctx, key, blob); err != nil {
			return fmt.Errorf("put %s: %w", key, err)
		}
		(*written)[key] = blob
	}
	for key := range *written {
		if _, ok := blobs[key]; ok {
			continue
		}
		if err := native.PutGovernorState(ctx, key, ""); err != nil {
			return fmt.Errorf("delete %s: %w", key, err)
		}
		delete(*written, key)
	}
	return nil
}
