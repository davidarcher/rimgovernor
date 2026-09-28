package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type fakeGovernorState struct {
	blobs map[string]string
	puts  int
}

func (f *fakeGovernorState) GovernorState(context.Context) (map[string]string, error) {
	return f.blobs, nil
}

func (f *fakeGovernorState) PutGovernorState(_ context.Context, key, blob string) (map[string]string, error) {
	f.puts++
	if blob == "" {
		delete(f.blobs, key)
	} else {
		f.blobs[key] = blob
	}
	return f.blobs, nil
}

func TestShadowGovernorStateLogsDriftAndPutsChanges(t *testing.T) {
	ctx := context.Background()
	database, err := store.Open(ctx, filepath.Join(t.TempDir(), "s.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer database.Close()
	native := &fakeGovernorState{blobs: map[string]string{"family/tidies": "{}", "unrelated": "x"}}
	var out bytes.Buffer
	var written map[string]string
	if err = shadowGovernorStateOnce(ctx, native, database, &written, &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "family/tidies: extra") || native.blobs["family/tidies"] != "" || native.blobs["unrelated"] != "x" {
		t.Fatal(out.String(), native.blobs)
	}
	puts := native.puts
	if err = shadowGovernorStateOnce(ctx, native, database, &written, &out); err != nil || native.puts != puts {
		t.Fatal("unchanged store put again", err)
	}
}

// A second world in the same process re-reads its save and re-checks
// drift (#994); the same world does not.
func TestShadowGovernorStateRechecksDriftOnWorldChange(t *testing.T) {
	ctx := context.Background()
	database, err := store.Open(ctx, filepath.Join(t.TempDir(), "s.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer database.Close()
	var shadow governorShadow
	var out bytes.Buffer
	first := governorWorld{Colony: "a", Map: 1, Load: "load-a", Generation: 1}
	if err = shadow.round(ctx, first, &fakeGovernorState{blobs: map[string]string{"family/tidies": "{}"}}, database, &out); err != nil || strings.Count(out.String(), "drift") != 1 {
		t.Fatal(err, out.String())
	}
	if err = shadow.round(ctx, first, &fakeGovernorState{blobs: map[string]string{"family/tidies": "{}"}}, database, &out); err != nil || strings.Count(out.String(), "drift") != 1 {
		t.Fatal("same world re-checked", err, out.String())
	}
	second := &fakeGovernorState{blobs: map[string]string{"family/tidies": "{}"}}
	if err = shadow.round(ctx, governorWorld{Colony: "b", Map: 1, Load: "load-b", Generation: 1}, second, database, &out); err != nil || strings.Count(out.String(), "drift") != 2 || second.blobs["family/tidies"] != "" {
		t.Fatal("second world not re-checked", err, out.String(), second.blobs)
	}
}

func governorGoalBlob(t *testing.T, id domain.GoalID, colony domain.ColonyID, revision uint64) string {
	t.Helper()
	g, err := domain.NewGoal(id, domain.AutopilotGoal, 1, domain.GenerationSnapshot{Colony: colony, Load: "l", Plan: "p"}, 0)
	if err != nil {
		t.Fatal(err)
	}
	data, err := json.Marshal(store.GovernorGoalBlob{SchemaVersion: store.GovernorStateSchemaVersion, Goal: g, Revision: revision})
	if err != nil {
		t.Fatal(err)
	}
	return string(data)
}

// Loading world 2 replaces world 1's goals with world 2's saved goals
// (#998): the save wins over the store.
func TestShadowGovernorStateRebuildsGoalsOnWorldChange(t *testing.T) {
	ctx := context.Background()
	database, err := store.Open(ctx, filepath.Join(t.TempDir(), "s.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer database.Close()
	var shadow governorShadow
	var out bytes.Buffer
	first := &fakeGovernorState{blobs: map[string]string{"goal/a": governorGoalBlob(t, "a", "one", 3)}}
	if err = shadow.round(ctx, governorWorld{Colony: "one", Map: 1, Load: "l", Generation: 1}, first, database, &out); err != nil {
		t.Fatal(err)
	}
	if state, err := database.LoadGoal(ctx, "a"); err != nil || state.Revision != 3 {
		t.Fatal("world 1 goals not rebuilt", state, err)
	}
	second := &fakeGovernorState{blobs: map[string]string{"goal/b": governorGoalBlob(t, "b", "two", 7)}}
	if err = shadow.round(ctx, governorWorld{Colony: "two", Map: 1, Load: "l", Generation: 1}, second, database, &out); err != nil {
		t.Fatal(err)
	}
	if _, err := database.LoadGoal(ctx, "a"); !errors.Is(err, store.ErrNotFound) {
		t.Fatal("world 1 goal survived the load", err)
	}
	if state, err := database.LoadGoal(ctx, "b"); err != nil || state.Revision != 7 || state.Goal.Snapshot.Colony != "two" || len(state.Methods) != 0 {
		t.Fatal("world 2 goals not rebuilt", state, err)
	}
	if strings.Contains(out.String(), "goal/") {
		t.Fatal("rebuilt goals drifted", out.String())
	}
}

// A save without governor state starts with no goals (D5).
func TestShadowGovernorStateEmptySaveStartsWithoutGoals(t *testing.T) {
	ctx := context.Background()
	database, err := store.Open(ctx, filepath.Join(t.TempDir(), "s.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer database.Close()
	g, err := domain.NewGoal("stale", domain.AutopilotGoal, 1, domain.GenerationSnapshot{Colony: "c", Load: "l", Plan: "p"}, 0)
	if err != nil {
		t.Fatal(err)
	}
	if err = database.CreateGoal(ctx, g); err != nil {
		t.Fatal(err)
	}
	var shadow governorShadow
	var out bytes.Buffer
	native := &fakeGovernorState{blobs: map[string]string{}}
	if err = shadow.round(ctx, governorWorld{Colony: "c", Map: 1, Load: "l", Generation: 1}, native, database, &out); err != nil {
		t.Fatal(err)
	}
	if _, err := database.LoadGoal(ctx, "stale"); !errors.Is(err, store.ErrNotFound) || len(native.blobs) != 0 {
		t.Fatal("empty save kept a goal", err, native.blobs)
	}
}
