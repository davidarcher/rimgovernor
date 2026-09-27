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
