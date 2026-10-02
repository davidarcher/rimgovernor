package main

import (
	"os"
	"path/filepath"
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func TestProfileBundleLatestCI(t *testing.T) {
	root := t.TempDir()
	downloads := 0
	old := ghRunner
	defer func() { ghRunner = old }()
	ghRunner = func(args ...string) ([]byte, error) {
		switch args[1] {
		case "list":
			return []byte(`[{"databaseId":42,"headSha":"abc"}]`), nil
		case "download":
			downloads++
			dir := args[len(args)-1]
			ringDir := filepath.Join(dir, factoryArtifact+"abc", "checkpoints", "sustained", "colony")
			ring := &na.Ring{Case: "sustained/colony", Entries: []na.Checkpoint{{Label: "t+7m"}, {Label: "t+14m"}}}
			return nil, ring.Write(ringDir)
		}
		t.Fatalf("unexpected gh %v", args)
		return nil, nil
	}
	for range 2 {
		bundle, err := profileBundle(root, "sustained/colony", latestCI)
		want := filepath.Join(root, "ci-fixtures", "42", factoryArtifact+"abc", "checkpoints", "sustained", "colony", "t+14m")
		if err != nil || bundle != want {
			t.Fatalf("bundle %s %v, want %s", bundle, err, want)
		}
	}
	if downloads != 1 {
		t.Fatalf("downloaded %d times, want once per run", downloads)
	}
	if _, err := os.Stat(filepath.Join(root, "ci-fixtures", "42.partial")); !os.IsNotExist(err) {
		t.Fatalf("partial left behind: %v", err)
	}
}

func TestLatestCINoRun(t *testing.T) {
	old := ghRunner
	defer func() { ghRunner = old }()
	ghRunner = func(args ...string) ([]byte, error) { return []byte(`[]`), nil }
	if _, err := profileBundle(t.TempDir(), "sustained/colony", latestCI); err == nil {
		t.Fatal("resolved without a factory run")
	}
}

func TestCIStreamsBesideRing(t *testing.T) {
	artifact := filepath.Join(t.TempDir(), "colony-checkpoints-abc")
	ring := filepath.Join(artifact, "checkpoints", "sustained", "colony")
	stream := filepath.Join(artifact, "snapshots", "sustained", "colony", "routine-stream-100-7.jsonl")
	if err := os.MkdirAll(filepath.Dir(stream), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(stream, []byte("{}\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	got := ciStreams(ring, "sustained/colony")
	if len(got) != 1 || got[0] != stream {
		t.Fatalf("ciStreams = %v, want [%s]", got, stream)
	}
}
