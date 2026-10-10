package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const (
	committedSidecar = "../../internal/observation/testdata/full_catalog.version"
	committedCsproj  = "../../../tools/defmirror/DefMirror.csproj"
	committedDefs    = "../../../contracts/proto/defs.proto"
)

func TestCommittedRecordingIsFresh(t *testing.T) {
	if err := checkFresh(filepath.FromSlash(committedSidecar), filepath.FromSlash(committedCsproj), filepath.FromSlash(committedDefs)); err != nil {
		t.Fatal(err)
	}
}

func TestMismatchedSidecarFailsWithTheRefreshCommand(t *testing.T) {
	original, err := os.ReadFile(filepath.FromSlash(committedSidecar))
	if err != nil {
		t.Fatal(err)
	}
	for name, edit := range map[string][2]string{
		"game version": {"game_version=1.6.4871 ", "game_version=1.6.4000 "},
		"defs source":  {"Assembly-CSharp 1.6.9676.17735", "Assembly-CSharp 1.6.1.1"},
	} {
		t.Run(name, func(t *testing.T) {
			if !strings.Contains(string(original), edit[0]) {
				t.Fatalf("committed sidecar lacks %q", edit[0])
			}
			copyPath := filepath.Join(t.TempDir(), "full_catalog.version")
			if err := os.WriteFile(copyPath, []byte(strings.Replace(string(original), edit[0], edit[1], 1)), 0o644); err != nil {
				t.Fatal(err)
			}
			err := checkFresh(copyPath, filepath.FromSlash(committedCsproj), filepath.FromSlash(committedDefs))
			if err == nil || !strings.Contains(err.Error(), refreshCommand) {
				t.Fatalf("mismatched sidecar error = %v; want one naming %q", err, refreshCommand)
			}
		})
	}
}
