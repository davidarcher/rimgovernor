package affected

import (
	"slices"
	"testing"
)

func TestTestInputsSelectOnlyTheirOwner(t *testing.T) {
	r := repo(t)
	for _, tc := range []struct{ file, pkg string }{
		{"go/internal/domain/goal_test.go", "internal/domain"},
		{"go/cmd/rimgovernor/serve_building_test.go", "cmd/rimgovernor"},
		{"go/internal/bridge/testdata/reply.json", "internal/bridge"},
		{"go/internal/nativeaccept/cases/light/light_test.go", "internal/nativeaccept/cases/light"},
	} {
		t.Run(tc.file, func(t *testing.T) {
			sel, err := Select(r, []string{tc.file})
			if err != nil {
				t.Fatal(err)
			}
			want := []string{"github.com/davidarcher/RimGovernor/go/" + tc.pkg}
			if !slices.Equal(sel.Packages, want) || len(sel.Cases) != 0 {
				t.Fatalf("test input selected packages %v, cases %v; want only %v", sel.Packages, sel.Cases, want)
			}
		})
	}
}

func TestControllerEntryPointSelectsServeHostingAreas(t *testing.T) {
	const file = "go/cmd/rimgovernor/serve_building.go"
	sel, err := Select(repo(t), []string{file})
	if err != nil {
		t.Fatal(err)
	}
	wantPackages := []string{"github.com/davidarcher/RimGovernor/go/cmd/rimgovernor"}
	if !slices.Equal(sel.Packages, wantPackages) {
		t.Errorf("packages = %v, want %v", sel.Packages, wantPackages)
	}
	for _, area := range []string{"light", "upkeep", "lifecycle"} {
		if !slices.Contains(sel.Cases, area) {
			t.Errorf("entry-point change omitted serve-hosting area %s: %v", area, sel.Cases)
		}
		wantWhy := []string{"the rimgovernor binary changed (" + file + ")"}
		if !slices.Equal(sel.Why[area], wantWhy) {
			t.Errorf("%s reasons = %v, want %v", area, sel.Why[area], wantWhy)
		}
	}
	for _, area := range []string{"authority", "bed", "zone"} {
		if slices.Contains(sel.Cases, area) {
			t.Errorf("entry-point change selected bridge-only area %s", area)
		}
	}
	if sel.AllHarnesses || len(sel.Sampled) != 0 {
		t.Errorf("entry-point change must select full serve-hosting areas only: %+v", sel)
	}
}

func TestTestDependencyClosureDoesNotFollowDependenciesTests(t *testing.T) {
	// A's tests import B. B's production imports C, and B's tests import D.
	// Changing D should test B but cannot affect A or B's production callers.
	g := &graph{
		deps:     map[string][]string{"a": {}, "b": {"c"}, "c": {}, "d": {}, "caller": {"b", "c"}},
		testDeps: map[string][]string{},
	}
	imports := map[string][]string{"a": {"b"}, "b": {"d"}, "c": {}, "d": {}, "caller": {}}
	for range 100 {
		g.addTestDependencies(imports)
		for pkg, want := range map[string][]string{"a": {"b", "c"}, "b": {"c", "d"}, "caller": {"b", "c"}} {
			if !slices.Equal(g.testDeps[pkg], want) {
				t.Fatalf("%s test dependencies = %v, want %v", pkg, g.testDeps[pkg], want)
			}
		}
		if !slices.Equal(g.deps["b"], []string{"c"}) {
			t.Fatalf("production dependencies changed: %v", g.deps)
		}
	}
}
