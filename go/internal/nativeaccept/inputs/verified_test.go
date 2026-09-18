package inputs

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestParseVerifiedTrailers(t *testing.T) {
	message := "fix(sleeping): thing\n\nBody.\n\nVerified: upkeepaccept inputs=0123456789abcdef\nVerified: bad line\nCo-Authored-By: x\nVerified: bedassignaccept inputs=fedcba9876543210\n"
	got := ParseVerifiedTrailers(message)
	want := []VerifiedTrailer{{"upkeepaccept", "0123456789abcdef"}, {"bedassignaccept", "fedcba9876543210"}}
	if len(got) != len(want) {
		t.Fatalf("got %v, want %v", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Errorf("trailer %d: got %v, want %v", i, got[i], want[i])
		}
		if ParseVerifiedTrailers(got[i].String())[0] != want[i] {
			t.Errorf("trailer %d does not round-trip: %q", i, got[i].String())
		}
	}
}

func TestHarnessInputsRejectsPaths(t *testing.T) {
	for _, harness := range []string{"", "../x", "cmd/upkeepaccept", "no-such-harness"} {
		if _, err := HarnessInputs(t.TempDir(), harness); err == nil {
			t.Errorf("HarnessInputs(%q) accepted", harness)
		}
	}
}

// The hash tracks the harness's own package and the native sources, and
// ignores files outside the inputs and test files.
func TestHarnessInputHashTracksInputs(t *testing.T) {
	if testing.Short() {
		t.Skip("runs go list")
	}
	wd, _ := os.Getwd()
	repo, ok := FindRepo(wd)
	if !ok {
		t.Skip("not in a checkout")
	}
	files, err := HarnessInputs(repo, "verified")
	if err != nil {
		t.Fatal(err)
	}
	has := func(path string) bool {
		for _, file := range files {
			if file == path {
				return true
			}
		}
		return false
	}
	for _, want := range []string{
		"go/internal/nativeaccept/cmd/verified/main.go",
		"go/internal/nativeaccept/inputs/verified.go",
		"go/cmd/rimgovernor/main.go",
		"go/go.mod",
		"integrations/rimgovernor-native/README.md",
	} {
		if !has(want) {
			t.Errorf("inputs lack %s", want)
		}
	}
	if has("go/internal/nativeaccept/verified_test.go") || has("AGENTS.md") {
		t.Errorf("inputs include non-inputs")
	}
	for _, file := range files {
		if strings.Contains(file, "\\") {
			t.Errorf("input %q is not forward-slashed", file)
		}
	}
	// A scratch file in the harness package changes the hash; removing it restores it.
	before, err := HarnessInputHash(repo, "verified")
	if err != nil {
		t.Fatal(err)
	}
	if len(before) != VerifiedHashLength {
		t.Fatalf("hash %q has length %d", before, len(before))
	}
	scratch := filepath.Join(repo, "go", "internal", "nativeaccept", "cmd", "verified", "zz_scratch.txt")
	if err := os.WriteFile(scratch, []byte("x"), 0o644); err != nil {
		t.Fatal(err)
	}
	defer os.Remove(scratch)
	during, err := HarnessInputHash(repo, "verified")
	if err != nil {
		t.Fatal(err)
	}
	if during == before {
		t.Errorf("hash ignored a new file in the harness package")
	}
	os.Remove(scratch)
	after, err := HarnessInputHash(repo, "verified")
	if err != nil {
		t.Fatal(err)
	}
	if after != before {
		t.Errorf("hash is not reproducible: %s then %s", before, after)
	}
}
