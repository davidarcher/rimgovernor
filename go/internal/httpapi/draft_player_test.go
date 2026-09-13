package httpapi

import (
	"context"
	"encoding/json"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// Any accidental native call panics: submission must use only fresh world and journal.
type draftUnusedNative struct{ boundary.Native }
type draftUnusedAuthority struct {
	buildingruntime.NativeAuthority
}
type draftUnusedWriter struct{ boundary.BuildingWriter }
type draftHTTPClock struct{}

func (draftHTTPClock) Now() time.Time { return time.Now() }

type draftHTTPWorld struct{ world store.World }

func (w *draftHTTPWorld) ReadWorld(ctx context.Context) (store.World, error) {
	return w.world, ctx.Err()
}
func TestDraftHTTPActualPlayerSubmissionAndRecovery(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()
	db, err := store.Open(ctx, filepath.Join(dir, "state.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	session, err := buildingruntime.NewSession(ctx, buildingruntime.SessionConfig{Control: buildingruntime.ControlConfig{ProfileDirectory: dir, LeaseDuration: time.Second, CallTimeout: time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second}}, db, draftUnusedNative{}, draftUnusedAuthority{}, draftUnusedWriter{}, draftHTTPClock{})
	if err != nil {
		t.Fatal(err)
	}
	world := &draftHTTPWorld{store.World{Colony: "colony", Load: "load", Map: 0}}
	player, err := buildingruntime.NewPlayer(ctx, buildingruntime.PlayerConfig{CallTimeout: time.Second, JournalTimeout: time.Second}, db, session, world)
	if err != nil {
		t.Fatal(err)
	}
	defer func() {
		if err := player.Close(context.Background()); err != nil {
			t.Error(err)
		}
	}()
	s, err := NewWithPlayer(Config{ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20}, snapshotFunc(func(context.Context) (Snapshot, error) { return Snapshot{}, nil }), db, player, db)
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	first := playerCall(s, "POST", "/api/drafts/plans", draftJSON, s.playerToken)
	if first.Code != 201 {
		t.Fatal(first.Code, first.Body.String())
	}
	var dto draftSubmissionDTO
	if err := json.Unmarshal(first.Body.Bytes(), &dto); err != nil {
		t.Fatal(err)
	}
	view := playerCall(s, "GET", "/api/plan?id="+string(dto.PlanID), "", "")
	if view.Code != 200 {
		t.Fatal(view.Code, view.Body.String())
	}
	world.world.Load = "replacement"
	replay := playerCall(s, "POST", "/api/drafts/plans", draftJSON, s.playerToken)
	if replay.Code != 200 || replay.Body.String() != first.Body.String() {
		t.Fatal(replay.Code, replay.Body.String())
	}
	lookup := playerCall(s, "GET", "/api/drafts/submission?requestId=draft-request", "", "")
	if lookup.Code != 200 || lookup.Body.String() != first.Body.String() {
		t.Fatal(lookup.Code, lookup.Body.String())
	}
	stale := playerCall(s, "POST", "/api/drafts/plans", strings.Replace(draftJSON, "draft-request", "new-request", 1), s.playerToken)
	if stale.Code != 409 {
		t.Fatal(stale.Code, stale.Body.String())
	}
	if player.State().Enabled {
		t.Fatal("submission acquired permission")
	}
}
