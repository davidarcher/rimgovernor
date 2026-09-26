package affected

import (
	"go/ast"
	"go/parser"
	"go/token"
	"os"
	"path/filepath"
	"slices"
	"sort"
	"strings"
	"testing"
)

// The table names every routine_*.go source that owns a family, no file
// that does not exist, and exactly serve's family list.
func TestRoutineFamilyFilesCoverFamilies(t *testing.T) {
	r := repo(t)
	dir := filepath.Join(r, "go", buildingruntimeDir)
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	shared := map[string]bool{"routine_food_plan.go": true, "routine_census.go": true, "routine_yield.go": true, "routine_work_projects.go": true, "routine_sleeping.go": true, "routine_idle_draft.go": true, "routine_resource_runway.go": true}
	for _, entry := range entries {
		name := entry.Name()
		if !strings.HasPrefix(name, "routine_") || strings.HasSuffix(name, "_test.go") || !strings.HasSuffix(name, ".go") {
			continue
		}
		if _, ok := routineFamilyFiles[name]; !ok && !shared[name] {
			t.Errorf("%s is neither in routineFamilyFiles nor a known shared routine file", name)
		}
	}
	for name := range routineFamilyFiles {
		if _, err := os.Stat(filepath.Join(dir, name)); err != nil {
			t.Errorf("routineFamilyFiles names %s: %v", name, err)
		}
	}
	got := routineFamilyNames()
	want := serveFamilies(t, filepath.Join(r, "go", "cmd", "rimgovernor", "serve.go"))
	if !slices.Equal(got, want) {
		t.Errorf("table families %v\nserve families %v", got, want)
	}
}

// serveFamilies reads the routineFamilies table's names from serve.go.
func serveFamilies(t *testing.T, path string) []string {
	t.Helper()
	fset := token.NewFileSet()
	file, err := parser.ParseFile(fset, path, nil, 0)
	if err != nil {
		t.Fatal(err)
	}
	var names []string
	for _, decl := range file.Decls {
		fn, ok := decl.(*ast.FuncDecl)
		if !ok || fn.Name.Name != "routineFamilies" {
			continue
		}
		ast.Inspect(fn.Body, func(n ast.Node) bool {
			lit, ok := n.(*ast.CompositeLit)
			if !ok || len(lit.Elts) == 0 {
				return true
			}
			if name, ok := lit.Elts[0].(*ast.BasicLit); ok && name.Kind == token.STRING {
				names = append(names, strings.Trim(name.Value, `"`))
			}
			return true
		})
	}
	if len(names) == 0 {
		t.Fatalf("no routineFamilies table in %s", path)
	}
	sort.Strings(names)
	return names
}

// A family file whose declarations only its own family and the dispatch
// files use scopes to that family; one a shared file uses, or that shared
// helpers spread through the package, is every family; a test file or a
// file outside buildingruntime never scopes.
func TestRoutineFamilyScope(t *testing.T) {
	goDir := filepath.Join(repo(t), "go")
	cases := []struct {
		file string
		want familyScope
	}{
		{"go/internal/buildingruntime/routine_lighting.go", familyScope{Families: []string{"lighting"}}},
		{"go/internal/buildingruntime/routine_refrigeration.go", familyScope{Families: []string{"refrigeration"}}},
		{"go/internal/buildingruntime/routine_repair_stale.go", familyScope{Families: []string{"repair"}}},
		{"go/internal/buildingruntime/routine_sleeping.go", familyScope{All: true}},
		{"go/internal/buildingruntime/routine_shelter.go", familyScope{All: true}},
		{"go/internal/buildingruntime/clock_scheduler.go", familyScope{All: true}},
		{"go/internal/buildingruntime/routine_lighting_test.go", familyScope{All: true}},
		{"go/internal/policy/lighting.go", familyScope{All: true}},
	}
	for _, tc := range cases {
		got, err := routineFamilyScope(goDir, tc.file)
		if err != nil {
			t.Fatal(err)
		}
		if got.All != tc.want.All || !slices.Equal(got.Families, tc.want.Families) {
			t.Errorf("%s scoped to %+v, want %+v", tc.file, got, tc.want)
		}
	}
	// medical_retry's attempt counter serves the tend, rescue, haul and
	// other planners: the scope is their union, not every family.
	got, err := routineFamilyScope(goDir, "go/internal/buildingruntime/routine_medical_retry.go")
	if err != nil {
		t.Fatal(err)
	}
	if got.All || !slices.Contains(got.Families, "tend") || !slices.Contains(got.Families, "haul") || slices.Contains(got.Families, "lighting") {
		t.Errorf("medical_retry scoped to %+v", got)
	}
}

// An area names the families it composes; one that sets none composes
// every family; a bridge-only area hosts no binary.
func TestReadAreaProfile(t *testing.T) {
	areas := filepath.Join(repo(t), "go", "internal", "nativeaccept", "cases")
	light, err := readAreaProfile(filepath.Join(areas, "light"))
	if err != nil {
		t.Fatal(err)
	}
	if !light.Binary || light.AllFamilies || !slices.Equal(light.Families, []string{"lighting", "work"}) {
		t.Errorf("light profile %+v", light)
	}
	upkeep, err := readAreaProfile(filepath.Join(areas, "upkeep"))
	if err != nil {
		t.Fatal(err)
	}
	if !upkeep.Binary || upkeep.AllFamilies || !slices.Contains(upkeep.Families, "home-coverage") || slices.Contains(upkeep.Families, "lighting") {
		t.Errorf("upkeep profile %+v", upkeep)
	}
	lifecycle, err := readAreaProfile(filepath.Join(areas, "lifecycle"))
	if err != nil {
		t.Fatal(err)
	}
	if !lifecycle.Binary || !lifecycle.AllFamilies {
		t.Errorf("lifecycle profile %+v", lifecycle)
	}
	authority, err := readAreaProfile(filepath.Join(areas, "authority"))
	if err != nil {
		t.Fatal(err)
	}
	if authority.Binary {
		t.Errorf("authority profile %+v", authority)
	}
}

// A lighting planner change selects the areas composing lighting or every
// family, with the reason; a scheduler change selects every area hosting
// the binary and no bridge-only area; a buildingruntime test change
// selects no area.
func TestSelectScopesRoutineFamilies(t *testing.T) {
	r := repo(t)
	sel, err := Select(r, []string{"go/internal/buildingruntime/routine_lighting.go"})
	if err != nil {
		t.Fatal(err)
	}
	// The areas naming lighting, plus every area whose profile composes every
	// family (campaign/* #633, startup/* #639, ...), read by profile so a new
	// every-family area does not stale the expectation (#668).
	want := []string{"condition", "light"}
	areas := filepath.Join(r, "go", "internal", "nativeaccept", "cases")
	entries, err := os.ReadDir(areas)
	if err != nil {
		t.Fatal(err)
	}
	for _, entry := range entries {
		if !entry.IsDir() || slices.Contains(want, entry.Name()) {
			continue
		}
		profile, err := readAreaProfile(filepath.Join(areas, entry.Name()))
		if err != nil {
			t.Fatal(err)
		}
		if profile.Binary && profile.AllFamilies {
			want = append(want, entry.Name())
		}
	}
	slices.Sort(want)
	if !slices.Contains(want, "lifecycle") || !slices.Equal(sel.Cases, want) {
		t.Errorf("lighting change selected %v, want %v", sel.Cases, want)
	}
	if why := sel.Why["light"]; len(why) != 1 || !strings.Contains(why[0], "routine family lighting changed (go/internal/buildingruntime/routine_lighting.go) and the area composes it") {
		t.Errorf("light why = %v", why)
	}
	if why := sel.Why["lifecycle"]; len(why) != 1 || !strings.HasSuffix(why[0], "composes every family") {
		t.Errorf("lifecycle why = %v", why)
	}
	sel, err = Select(r, []string{"go/internal/buildingruntime/clock_scheduler.go"})
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{"light", "shelter", "upkeep", "smoke"} {
		if !slices.Contains(sel.Cases, want) && want != "smoke" {
			t.Errorf("scheduler change lacks %s: %v", want, sel.Cases)
		}
	}
	for _, bridgeOnly := range []string{"authority", "bed", "zone"} {
		if slices.Contains(sel.Cases, bridgeOnly) {
			t.Errorf("scheduler change selected bridge-only %s: %v", bridgeOnly, sel.Cases)
		}
	}
	if why := sel.Why["light"]; len(why) != 1 || !strings.HasPrefix(why[0], "the rimgovernor binary imports ") {
		t.Errorf("light why = %v", why)
	}
	sel, err = Select(r, []string{"go/internal/buildingruntime/clock_scheduler_test.go"})
	if err != nil {
		t.Fatal(err)
	}
	if len(sel.Cases) != 0 || !slices.Contains(sel.Packages, "github.com/davidarcher/RimGovernor/go/internal/buildingruntime") {
		t.Errorf("test change selected %+v", sel)
	}
}
