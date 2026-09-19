package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/remoteaccept"
)

func TestAggregateCommandOverwritesStalePassOnMalformedEvidence(t *testing.T) {
	root := t.TempDir()
	source := filepath.Join("..", "..", "..", "docs", "developers", "contracts", "remote-acceptance")
	if err := os.CopyFS(root, os.DirFS(source)); err != nil {
		t.Fatal(err)
	}
	b, err := os.ReadFile(filepath.Join(root, "aggregate.json"))
	if err != nil {
		t.Fatal(err)
	}
	var a remoteaccept.Aggregate
	if err = remoteaccept.Decode(b, &a); err != nil {
		t.Fatal(err)
	}
	if _, err = remoteaccept.WriteJSON(root, "shards.json", a.Shards); err != nil {
		t.Fatal(err)
	}
	if err = run([]string{"aggregate", "-root", root}); err != nil {
		t.Fatal(err)
	}
	// A previously green output must not survive a later malformed shard.
	if err = os.WriteFile(filepath.Join(root, "s1", "attempts.json"), []byte(`{"schema_version":1,"schema_version":1}`), 0o600); err != nil {
		t.Fatal(err)
	}
	if err = run([]string{"aggregate", "-root", root}); err == nil {
		t.Fatal("malformed shard exited successfully")
	}
	b, err = os.ReadFile(filepath.Join(root, "result.json"))
	if err != nil {
		t.Fatal(err)
	}
	var result struct {
		Passed bool   `json:"passed"`
		Error  string `json:"error"`
	}
	if err = json.Unmarshal(b, &result); err != nil {
		t.Fatal(err)
	}
	if result.Passed || result.Error == "" {
		t.Fatalf("stale success survived: %s", b)
	}
}
