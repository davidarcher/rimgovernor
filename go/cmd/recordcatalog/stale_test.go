package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const (
	committedSidecar = "../../internal/observation/testdata/full_catalog.version"
	committedDefs    = "../../../contracts/proto/defs.proto"
)

func TestCommittedRecordingIsFresh(t *testing.T) {
	if err := checkFresh(filepath.FromSlash(committedSidecar), filepath.FromSlash(committedDefs)); err != nil {
		t.Fatal(err)
	}
}

func TestMismatchedSidecarFailsWithTheRefreshCommand(t *testing.T) {
	original, err := os.ReadFile(filepath.FromSlash(committedSidecar))
	if err != nil {
		t.Fatal(err)
	}
	const source = "defs_proto_source=Assembly-CSharp "
	i := strings.Index(string(original), source)
	if i < 0 {
		t.Fatalf("committed sidecar lacks %q", source)
	}
	edited := string(original[:i+len(source)]) + "0.0.1.1\n"
	copyPath := filepath.Join(t.TempDir(), "full_catalog.version")
	if err := os.WriteFile(copyPath, []byte(edited), 0o644); err != nil {
		t.Fatal(err)
	}
	err = checkFresh(copyPath, filepath.FromSlash(committedDefs))
	if err == nil || !strings.Contains(err.Error(), refreshCommand) {
		t.Fatalf("mismatched sidecar error = %v; want one naming %q", err, refreshCommand)
	}
}
