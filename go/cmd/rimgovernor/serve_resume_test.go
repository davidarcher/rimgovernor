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
}

func (p *resumePlayer) State() buildingruntime.ControlState { return p.state }
func (p *resumePlayer) Resume(_ context.Context, q store.ControlRequest) (store.ControlRecord, error) {
	p.requests = append(p.requests, q)
	if p.fail != nil {
		return store.ControlRecord{Request: q, Phase: store.UncertainControl}, p.fail
	}
	p.state = buildingruntime.ControlState{Snapshot: domain.GenerationSnapshot{Colony: q.World.Colony, Load: q.World.Load, Map: q.World.Map}, ObservationKnown: true, Enabled: true}
	return store.ControlRecord{Request: q, Phase: store.RunningControl}, nil
}

func resumeObserved(load domain.LoadID) httpapi.Snapshot {
	return httpapi.Snapshot{Connected: true, Identity: domain.Known(observation.Identity{Colony: "colony", Load: load, Map: 0})}
}

func TestAutoResumeRunsEachFreshWorldOnce(t *testing.T) {
	snapshots := &resumeSnapshots{}
	player := &resumePlayer{}
	var out bytes.Buffer
	r, err := newAutoResumer(snapshots, player, &out)
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
	// Running now; the same world is never offered again, even after a Pause.
	r.step(ctx)
	player.state.Enabled = false
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
	restarted, err := newAutoResumer(&resumeSnapshots{snapshot: resumeObserved("load-2")}, &resumePlayer{}, &out)
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
	player := &resumePlayer{fail: errors.New("observations not ready")}
	var out bytes.Buffer
	r, err := newAutoResumer(snapshots, player, &out)
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
