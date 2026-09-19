package affected

import (
	"os"
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
		if !slices.Contains(sel.Packages, "github.com/davidarcher/RimGovernor/go/"+tc.owner) {
			t.Fatalf("missing owner: %+v", sel)
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

func TestEmbeddedSelectionWithRemovedInputs(t *testing.T) {
	for _, removed := range []bool{false, true} {
		t.Run(map[bool]string{false: "edit", true: "remove"}[removed], func(t *testing.T) {
			r := t.TempDir()
			write := func(name, body string) {
				t.Helper()
				file := filepath.Join(r, "go", filepath.FromSlash(name))
				if err := os.MkdirAll(filepath.Dir(file), 0755); err != nil {
					t.Fatal(err)
				}
				if err := os.WriteFile(file, []byte(body), 0600); err != nil {
					t.Fatal(err)
				}
			}
			write("go.mod", "module example.com/embedded\n\ngo 1.25\n")
			write("assets/assets.go", "package assets\nimport _ \"embed\"\n//go:embed data/*.json\nvar Data string\n")
			write("assets/assets_test.go", "package assets\nimport _ \"embed\"\n//go:embed tests/*.json\nvar testData string\n")
			write("caller/caller.go", "package caller\nimport _ \"example.com/embedded/assets\"\n")
			write("assets/data/input.json", "{}")
			write("assets/tests/input.json", "{}")
			if removed {
				for _, file := range []string{"assets/data/input.json", "assets/tests/input.json"} {
					if err := os.Remove(filepath.Join(r, "go", filepath.FromSlash(file))); err != nil {
						t.Fatal(err)
					}
				}
			}
			for _, tc := range []struct {
				file     string
				packages []string
			}{
				{"assets/data/input.json", []string{"example.com/embedded/assets", "example.com/embedded/caller"}},
				{"assets/tests/input.json", []string{"example.com/embedded/assets"}},
				{"assets/unrelated.json", nil},
			} {
				sel, err := Select(r, []string{"go/" + tc.file})
				if err != nil {
					t.Fatal(err)
				}
				if !slices.Equal(sel.Packages, tc.packages) || len(sel.Cases) != 0 {
					t.Fatalf("%s: got %+v, want %v", tc.file, sel, tc.packages)
				}
			}
		})
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
