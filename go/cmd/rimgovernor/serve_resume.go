package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// autoResumeAttempts bounds how often one observed world is offered a
// startup resume before the controller leaves it to the dashboard.
const autoResumeAttempts = 20

type autoResumePlayer interface {
	State() buildingruntime.ControlState
	Resume(context.Context, store.ControlRequest) (store.ControlRecord, error)
}

// autoResumer runs the bot for every fresh observed world once, without a
// dashboard click: the world present at startup, and each new load token a
// native load afterwards issues. It goes through exactly the control intent
// POST /api/player/control/resume submits, so authority, the root plan and
// the fresh review are established the same way. A world is offered a resume
// at most once per run (bounded retries while observations settle), so a
// player's later Pause for that world stands.
type autoResumer struct {
	snapshots httpapi.SnapshotProvider
	player    autoResumePlayer
	out       io.Writer
	// process makes this process's request IDs distinct from those a previous
	// process left in a reopened journal, which would otherwise replay the
	// old record instead of acquiring authority again.
	process  string
	attempts map[store.World]int
	settled  map[store.World]bool
}

func newAutoResumer(snapshots httpapi.SnapshotProvider, player autoResumePlayer, out io.Writer) (*autoResumer, error) {
	if snapshots == nil || player == nil || out == nil {
		return nil, errors.New("auto resume requires snapshots, a player and a diagnostics writer")
	}
	var entropy [8]byte
	if _, err := rand.Read(entropy[:]); err != nil {
		return nil, err
	}
	return &autoResumer{snapshots: snapshots, player: player, out: out, process: hex.EncodeToString(entropy[:]), attempts: map[store.World]int{}, settled: map[store.World]bool{}}, nil
}

func (a *autoResumer) run(ctx context.Context, interval time.Duration) {
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for {
		a.step(ctx)
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}

// step offers the currently observed fresh world a resume if it has not been
// settled yet. It returns whether a resume was attempted this step.
func (a *autoResumer) step(ctx context.Context) bool {
	snapshot, err := a.snapshots.Snapshot(ctx)
	if err != nil {
		return false
	}
	identity, known := snapshot.Identity.Value()
	if !snapshot.Connected || snapshot.Stale || !known {
		return false
	}
	world := store.World{Colony: identity.Colony, Load: identity.Load, Map: identity.Map}
	if a.settled[world] {
		return false
	}
	state := a.player.State()
	if state.Enabled && state.ObservationKnown && state.Snapshot.Colony == world.Colony && state.Snapshot.Load == world.Load && state.Snapshot.Map == world.Map {
		a.settled[world] = true
		return false
	}
	a.attempts[world]++
	if a.attempts[world] > autoResumeAttempts {
		a.settled[world] = true
		fmt.Fprintf(a.out, "auto resume: giving up on %s/%s/%d after %d attempts; use the dashboard's Resume\n", world.Colony, world.Load, world.Map, autoResumeAttempts)
		return false
	}
	request := store.ControlRequest{RequestID: fmt.Sprintf("auto-resume/%s/%s/%s/%d/%d", a.process, world.Colony, world.Load, world.Map, a.attempts[world]), Kind: store.ResumeControl, World: world}
	record, err := a.player.Resume(ctx, request)
	if err == nil && record.Phase == store.RunningControl {
		a.settled[world] = true
		fmt.Fprintf(a.out, "auto resume: running %s/%s/%d\n", world.Colony, world.Load, world.Map)
		return true
	}
	if err == nil {
		err = fmt.Errorf("control phase %s", record.Phase)
	}
	fmt.Fprintf(a.out, "auto resume: attempt %d for %s/%s/%d failed: %v\n", a.attempts[world], world.Colony, world.Load, world.Map, err)
	return true
}
