package main

import (
	"bytes"
	"context"
	"errors"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type resumeSnapshots struct{ snapshot httpapi.Snapshot }

func (s *resumeSnapshots) Snapshot(context.Context) (httpapi.Snapshot, error) { return s.snapshot, nil }

type resumePlayer struct {
	state    buildingruntime.ControlState
	requests []store.ControlRequest
	fail     error
	// current mirrors the journal's current control record the way
	// store.Store.CurrentControl reports it; ErrNotFound before any intent.
	current store.ControlRecord
	journal error
}

func (p *resumePlayer) State() buildingruntime.ControlState { return p.state }
func (p *resumePlayer) Resume(_ context.Context, q store.ControlRequest) (store.ControlRecord, error) {
	p.requests = append(p.requests, q)
	if p.fail != nil {
		p.current, p.journal = store.ControlRecord{Request: q, Phase: store.UncertainControl}, nil
		return p.current, p.fail
	}
	p.state = buildingruntime.ControlState{Snapshot: domain.GenerationSnapshot{Colony: q.World.Colony, Load: q.World.Load, Map: q.World.Map}, ObservationKnown: true, Enabled: true}
	p.current, p.journal = store.ControlRecord{Request: q, Phase: store.RunningControl}, nil
	return p.current, nil
}
func (p *resumePlayer) CurrentControl(context.Context) (store.ControlRecord, error) {
	if p.journal != nil {
		return store.ControlRecord{}, p.journal
	}
	return p.current, nil
}

// pause models the dashboard's Pause: local writes stop and the journal's
// current intent becomes a paused Pause record for the world.
func (p *resumePlayer) pause(world store.World) {
	p.state.Enabled = false
	p.current = store.ControlRecord{Request: store.ControlRequest{RequestID: "player-pause", Kind: store.PauseControl, World: world}, Phase: store.PausedControl}
}

func newResumePlayer() *resumePlayer { return &resumePlayer{journal: store.ErrNotFound} }

func resumeObserved(load domain.LoadID) httpapi.Snapshot {
	return httpapi.Snapshot{Connected: true, Identity: domain.Known(observation.Identity{Colony: "colony", Load: load, Map: 0})}
}

func TestAutoResumeRunsEachFreshWorldOnce(t *testing.T) {
	snapshots := &resumeSnapshots{}
	player := newResumePlayer()
	var out bytes.Buffer
	r, err := newAutoResumer(snapshots, player, player, &out)
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	// Nothing observed yet, then stale: no attempt.
	if r.step(ctx) || len(player.requests) != 0 {
		t.Fatal("resumed without a fresh world")
	}
	snapshots.snapshot = resumeObserved("load-1")
	snapshots.snapshot.Stale = true
	if r.step(ctx) || len(player.requests) != 0 {
		t.Fatal("resumed a stale world")
	}
	snapshots.snapshot.Stale = false
	if !r.step(ctx) || len(player.requests) != 1 || player.requests[0].Kind != store.ResumeControl || player.requests[0].World != (store.World{Colony: "colony", Load: "load-1"}) {
		t.Fatal(player.requests)
	}
	// Running now; the same world is never offered again after a Pause.
	r.step(ctx)
	player.pause(store.World{Colony: "colony", Load: "load-1"})
	r.step(ctx)
	r.step(ctx)
	if len(player.requests) != 1 {
		t.Fatal("resumed the same world again", player.requests)
	}
	// A native load issues a new load token: that world is resumed once too.
	snapshots.snapshot = resumeObserved("load-2")
	r.step(ctx)
	r.step(ctx)
	if len(player.requests) != 2 || player.requests[1].World.Load != "load-2" || player.requests[1].RequestID == player.requests[0].RequestID {
		t.Fatal(player.requests)
	}
	if !strings.Contains(out.String(), "running colony/load-2/0") {
		t.Fatal(out.String())
	}
	// A restarted process reopening the same journal must not present the
	// request ID the killed process recorded, or the store replays that
	// record instead of acquiring authority again.
	restartedPlayer := newResumePlayer()
	restarted, err := newAutoResumer(&resumeSnapshots{snapshot: resumeObserved("load-2")}, restartedPlayer, restartedPlayer, &out)
	if err != nil {
		t.Fatal(err)
	}
	restarted.step(ctx)
	if got := restarted.player.(*resumePlayer).requests; len(got) != 1 || got[0].RequestID == player.requests[1].RequestID {
		t.Fatal("restart reused a request ID", got, player.requests)
	}
}

func TestAutoResumeRetriesBoundedlyWithFreshRequestIDs(t *testing.T) {
	snapshots := &resumeSnapshots{snapshot: resumeObserved("load-1")}
	player := newResumePlayer()
	player.fail = errors.New("observations not ready")
	var out bytes.Buffer
	r, err := newAutoResumer(snapshots, player, player, &out)
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	for range autoResumeAttempts + 5 {
		r.step(ctx)
	}
	if len(player.requests) != autoResumeAttempts {
		t.Fatal(len(player.requests))
	}
	seen := map[string]bool{}
	for _, q := range player.requests {
		if seen[q.RequestID] {
			t.Fatal("request ID reused", q.RequestID)
		}
		seen[q.RequestID] = true
	}
	if !strings.Contains(out.String(), "giving up on colony/load-1/0") {
		t.Fatal(out.String())
	}
	// A world the dashboard already resumed is settled without an attempt.
	snapshots.snapshot = resumeObserved("load-2")
	player.state = buildingruntime.ControlState{Snapshot: domain.GenerationSnapshot{Colony: "colony", Load: "load-2"}, ObservationKnown: true, Enabled: true}
	if r.step(ctx) || len(player.requests) != autoResumeAttempts {
		t.Fatal("resumed an already running world")
	}
}

// TestAutoResumeReacquiresAfterNonPlayerLoss is the #87 controller side: a
// world running under a Resume record that loses authority for any reason
// other than the player's Pause (native DISCONNECT after a GABS drop, a
// failed observation) is offered one fresh resume cycle per loss, with
// request IDs the journal has not seen; a Pause, or an unreadable journal,
// leaves it alone.
func TestAutoResumeReacquiresAfterNonPlayerLoss(t *testing.T) {
	world := store.World{Colony: "colony", Load: "load-1"}
	snapshots := &resumeSnapshots{snapshot: resumeObserved("load-1")}
	player := newResumePlayer()
	var out bytes.Buffer
	r, err := newAutoResumer(snapshots, player, player, &out)
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	if !r.step(ctx) || len(player.requests) != 1 {
		t.Fatal(player.requests)
	}
	r.step(ctx) // settled, observed running
	// Authority lost while the journal still says resume: re-acquired once.
	player.state.Enabled = false
	if !r.step(ctx) || len(player.requests) != 2 || player.requests[1].World != world || player.requests[1].RequestID == player.requests[0].RequestID {
		t.Fatal(player.requests)
	}
	if !strings.Contains(out.String(), "lost while its control intent is resume") {
		t.Fatal(out.String())
	}
	r.step(ctx)
	if len(player.requests) != 2 || !player.state.Enabled {
		t.Fatal("re-acquired world not settled", player.requests)
	}
	// A second loss is a second cycle: the re-arm is per loss, not once ever.
	player.state.Enabled = false
	r.step(ctx)
	if len(player.requests) != 3 {
		t.Fatal(player.requests)
	}
	r.step(ctx)
	// A loss native keeps refusing exhausts one bounded cycle and then waits
	// for the dashboard instead of looping: the world was never observed
	// running again, so nothing re-arms it.
	player.state.Enabled = false
	player.fail = errors.New("native refused")
	for range autoResumeAttempts * 3 {
		r.step(ctx)
	}
	if len(player.requests) != 3+autoResumeAttempts {
		t.Fatal(len(player.requests))
	}
	// The player's Pause stands: no resume while the journal's intent is pause.
	player.fail = nil
	player.state = buildingruntime.ControlState{Snapshot: domain.GenerationSnapshot{Colony: "colony", Load: "load-1"}, ObservationKnown: true, Enabled: true}
	r.step(ctx) // observed running again
	player.pause(world)
	for range 3 {
		r.step(ctx)
	}
	if len(player.requests) != 3+autoResumeAttempts {
		t.Fatal("resumed over the player's pause", player.requests)
	}
	// An unreadable journal is never treated as intent to resume.
	player.state.Enabled = true
	r.step(ctx)
	player.state.Enabled = false
	player.journal = errors.New("journal closed")
	r.step(ctx)
	if len(player.requests) != 3+autoResumeAttempts {
		t.Fatal("resumed without readable intent", player.requests)
	}
}
