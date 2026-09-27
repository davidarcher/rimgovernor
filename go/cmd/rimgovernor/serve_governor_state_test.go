package main

import (
	"bytes"
	"context"
	"path/filepath"
	"strings"
	"testing"

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
