package affected

import (
	"path/filepath"
	"slices"
	"testing"
)

func TestEmbeddedManifestsSelectProductionOwners(t *testing.T) {
	r := repo(t)
	for _, tc := range []struct {
		file, owner string
		runner      bool
	}{
		{"go/internal/nativeaccept/cmd/acceptance/suites/smoke.json", "internal/nativeaccept/cmd/acceptance", true},
		{"go/internal/nativeaccept/cases/sustained/manifests/issue-1-matrix.json", "internal/nativeaccept/cases/sustained", false},
	} {
		sel, err := Select(r, []string{tc.file})
		if err != nil {
			t.Fatal(err)
		}
		want := []string{"sustained"}
		if tc.runner {
			want, err = caseAreas(filepath.Join(r, "go"))
			if err != nil {
				t.Fatal(err)
			}
		}
		if !slices.Equal(sel.Cases, want) {
			t.Fatalf("%s: cases %v, want %v", tc.file, sel.Cases, want)
		}
	}
}

func TestRemovedEmbedPatternMatching(t *testing.T) {
	for _, tc := range []struct {
		file, pattern string
		want          bool
	}{
		{"suites/smoke.json", "suites/smoke.json", true},
		{"manifests/issue-1-matrix.json", "manifests/*.json", true},
		{"data/nested/file.json", "data", true},
		{"data/.hidden/file.json", "data", false},
		{"data/_hidden.json", "all:data", true},
		{"data/.hidden.json", "data/*", true},
		{"elsewhere/file.json", "data", false},
	} {
		if got := matchesEmbedPatterns(tc.file, []string{tc.pattern}); got != tc.want {
			t.Errorf("%s / %s = %v", tc.file, tc.pattern, got)
		}
	}
}
